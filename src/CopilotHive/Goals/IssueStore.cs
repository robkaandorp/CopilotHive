using CopilotHive.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CopilotHive.Goals;

/// <summary>
/// EF Core-backed implementation of <see cref="IIssueStore"/>.
/// Persists <see cref="Issue"/> entities to the SQLite database via
/// <see cref="CopilotHiveDbContext"/>.
/// </summary>
public sealed class IssueStore : IIssueStore
{
    private readonly IDbContextFactory<CopilotHiveDbContext>? _dbContextFactory;
    private readonly CopilotHiveDbContext? _directDbContext;
    private readonly ILogger<IssueStore> _logger;

    /// <summary>Creates a new <see cref="IssueStore"/> using a DbContext factory (production/DI).</summary>
    /// <param name="dbContextFactory">Factory used to create transient <see cref="CopilotHiveDbContext"/> instances.</param>
    /// <param name="logger">Logger instance.</param>
    public IssueStore(
        IDbContextFactory<CopilotHiveDbContext> dbContextFactory,
        ILogger<IssueStore> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>Creates a store using a single owned DbContext (for testing).</summary>
    /// <param name="dbContext">The DbContext to use for all operations.</param>
    /// <param name="logger">Logger instance.</param>
    internal IssueStore(CopilotHiveDbContext dbContext, ILogger<IssueStore> logger)
    {
        _directDbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a DbContext to use for an operation. When a direct (test-owned) context is set,
    /// returns it with <c>ownsContext = false</c> so the caller does not dispose it. Otherwise
    /// creates a transient context via the factory with <c>ownsContext = true</c>.
    /// </summary>
    private (CopilotHiveDbContext Db, bool OwnsContext) ResolveDbContext()
    {
        if (_directDbContext is not null)
            return (_directDbContext, false);
        return (_dbContextFactory!.CreateDbContext(), true);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            return await db.Issues.AsNoTracking()
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Issue>> GetIssuesAsync(
        IssueStatus? status = null,
        IssueType? type = null,
        IssueSeverity? severity = null,
        string? repository = null,
        string? sourceGoalId = null,
        string? linkedGoalId = null,
        CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var query = db.Issues.AsNoTracking().AsQueryable();

            if (status.HasValue)
                query = query.Where(e => e.Status == status.Value);
            if (type.HasValue)
                query = query.Where(e => e.Type == type.Value);
            if (severity.HasValue)
                query = query.Where(e => e.Severity == severity.Value);
            if (sourceGoalId is not null)
                query = query.Where(e => e.SourceGoalId == sourceGoalId);
            if (linkedGoalId is not null)
                query = query.Where(e => e.LinkedGoalId == linkedGoalId);

            var issues = await query
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync(ct);

            // Repository filter is applied client-side: case-insensitive match against any
            // entry in RepositoryNames (the JSON column cannot be filtered efficiently in SQL).
            if (repository is not null)
            {
                issues = issues
                    .Where(e => e.RepositoryNames.Any(r =>
                        string.Equals(r, repository, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            return issues;
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            return await db.Issues.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == issueId, ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(issue.Id))
            throw new ArgumentException("Issue Id must not be null or empty.", nameof(issue));

        var (db, ownsContext) = ResolveDbContext();
        try
        {
            db.Issues.Add(issue);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // Only wrap primary-key constraint violations as InvalidOperationException
                // (the documented duplicate-ID signal). Other DbUpdateExceptions (locking,
                // I/O, schema, NOT NULL, CHECK, FOREIGN KEY) must propagate so the API
                // returns 500, not a misleading 409 "already exists".
                if (ex.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 })
                    throw new InvalidOperationException($"Failed to create issue '{issue.Id}'; an issue with the same ID may already exist.", ex);
                throw;
            }
            finally
            {
                // Detach so the tracked instance does not interfere with later operations
                // on the same (shared/test) DbContext.
                db.Entry(issue).State = EntityState.Detached;
            }

            _logger.LogInformation("Created issue {IssueId}: {Title}", issue.Id, issue.Title);
            return issue;
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var existing = await db.Issues.FirstOrDefaultAsync(e => e.Id == issue.Id, ct);
            if (existing is null)
                throw new InvalidOperationException($"Issue '{issue.Id}' not found in SQLite store.");

            // ResolvedAt transition logic — compare incoming status against the existing
            // status BEFORE overwriting it.
            var wasTerminal = existing.Status is IssueStatus.Resolved or IssueStatus.Closed;
            var isTerminal = issue.Status is IssueStatus.Resolved or IssueStatus.Closed;

            if (isTerminal && !wasTerminal)
                existing.ResolvedAt = DateTime.UtcNow;
            else if (!isTerminal)
                existing.ResolvedAt = null;
            // else: terminal → terminal — preserve the existing ResolvedAt.

            // Copy mutable fields from the incoming issue.
            existing.Type = issue.Type;
            existing.Title = issue.Title;
            existing.Description = issue.Description;
            existing.Severity = issue.Severity;
            existing.Status = issue.Status;
            existing.RepositoryNames = issue.RepositoryNames;
            existing.LinkedGoalId = issue.LinkedGoalId;

            // Immutable fields (Id, CreatedAt, SourceGoalId, SourceRole, SourceIteration)
            // are intentionally NOT copied.

            existing.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Updated issue {IssueId}", issue.Id);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var existing = await db.Issues.FirstOrDefaultAsync(e => e.Id == issueId, ct);
            if (existing is null)
                return false;

            db.Issues.Remove(existing);
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Deleted issue {IssueId}", issueId);
            return true;
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }
}
