using CopilotHive.Configuration;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotHive.Goals;

/// <summary>
/// EF Core-backed implementation of <see cref="IGoalStore"/>.
/// Primary source of truth for goal state, iteration history, and search.
/// Replaces the legacy raw ADO.NET goal store with EF Core persistence.
/// </summary>
public sealed class GoalStore : IGoalStore
{
    private readonly IDbContextFactory<CopilotHiveDbContext>? _dbContextFactory;
    private readonly CopilotHiveDbContext? _directDbContext;
    private readonly ILogger<GoalStore> _logger;
    private readonly PipelineStore? _pipelineStore;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <inheritdoc />
    public string Name => "sqlite";

    /// <summary>Creates a new <see cref="GoalStore"/> using a DbContext factory (production/DI).</summary>
    /// <param name="dbContextFactory">Factory used to create transient <see cref="CopilotHiveDbContext"/> instances.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="pipelineStore">Optional <see cref="PipelineStore"/> used to retrieve pipeline conversations.</param>
    /// <param name="dbPath">Full path to the SQLite database file (used for backups).</param>
    public GoalStore(
        IDbContextFactory<CopilotHiveDbContext> dbContextFactory,
        ILogger<GoalStore> logger,
        PipelineStore? pipelineStore = null,
        string dbPath = ":memory:")
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _pipelineStore = pipelineStore;
        BackupDatabaseIfExists(dbPath);
        _logger.LogInformation("GoalStore initialised at {DbPath}", dbPath);
    }

    /// <summary>Creates a store using a single owned DbContext (for testing).</summary>
    internal GoalStore(CopilotHiveDbContext dbContext, ILogger<GoalStore> logger, PipelineStore? pipelineStore = null)
    {
        _directDbContext = dbContext;
        _logger = logger;
        _pipelineStore = pipelineStore;
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

    private void BackupDatabaseIfExists(string dbPath)
    {
        if (dbPath == ":memory:")
            return;

        if (!File.Exists(dbPath))
            return;

        var dbDir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrEmpty(dbDir))
            dbDir = Directory.GetCurrentDirectory();

        var backupDir = Path.Combine(dbDir, "backups");
        Directory.CreateDirectory(backupDir);

        var dbFileName = Path.GetFileName(dbPath);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture);
        var backupFileName = $"{dbFileName}.{timestamp}.bak";
        var backupPath = Path.Combine(backupDir, backupFileName);

        using (var sourceConn = new SqliteConnection($"Data Source={dbPath}"))
        using (var backupConn = new SqliteConnection($"Data Source={backupPath}"))
        {
            sourceConn.Open();
            backupConn.Open();
            sourceConn.BackupDatabase(backupConn);
        }

        _logger.LogInformation("Database backed up to {BackupPath}", backupPath);

        var oldBackups = Directory
            .EnumerateFiles(backupDir, $"{dbFileName}.*.bak")
            .OrderByDescending(f => f)
            .Skip(10)
            .ToList();

        foreach (var oldBackup in oldBackups)
        {
            File.Delete(oldBackup);
            _logger.LogDebug("Removed old backup {BackupPath}", oldBackup);
        }
    }

    // ── Entity ↔ domain mapping ──────────────────────────────────────────

    private static IterationSummary ToDomain(IterationSummaryEntity entity)
    {
        var phases = string.IsNullOrEmpty(entity.PhasesJson)
            ? []
            : JsonSerializer.Deserialize<List<PhaseResult>>(entity.PhasesJson, JsonOptions) ?? [];

        var notes = string.IsNullOrEmpty(entity.NotesJson)
            ? []
            : JsonSerializer.Deserialize<List<string>>(entity.NotesJson, JsonOptions) ?? [];

        var phaseOutputs = string.IsNullOrEmpty(entity.PhaseOutputsJson)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(entity.PhaseOutputsJson, JsonOptions) ?? [];

        var clarifications = string.IsNullOrEmpty(entity.ClarificationsJson)
            ? []
            : JsonSerializer.Deserialize<List<PersistedClarification>>(entity.ClarificationsJson, JsonOptions) ?? [];

        TestCounts? testCounts = null;
        if (entity.TestTotal.HasValue)
        {
            testCounts = new TestCounts
            {
                Total = entity.TestTotal.Value,
                Passed = entity.TestPassed ?? 0,
                Failed = entity.TestFailed ?? 0,
            };
        }

        return new IterationSummary
        {
            Iteration = entity.Iteration,
            Phases = phases,
            TestCounts = testCounts,
            BuildSuccess = entity.BuildSuccess,
            ReviewVerdict = entity.ReviewVerdict,
            Notes = notes,
            PhaseOutputs = phaseOutputs,
            Clarifications = clarifications,
        };
    }

    private static IterationSummaryEntity ToEntity(string goalId, IterationSummary summary)
    {
        return new IterationSummaryEntity
        {
            GoalId = goalId,
            Iteration = summary.Iteration,
            PhasesJson = JsonSerializer.Serialize(summary.Phases, JsonOptions),
            TestTotal = summary.TestCounts?.Total,
            TestPassed = summary.TestCounts?.Passed,
            TestFailed = summary.TestCounts?.Failed,
            ReviewVerdict = summary.ReviewVerdict,
            NotesJson = summary.Notes.Count > 0 ? JsonSerializer.Serialize(summary.Notes, JsonOptions) : null,
            PhaseOutputsJson = summary.PhaseOutputs.Count > 0 ? JsonSerializer.Serialize(summary.PhaseOutputs, JsonOptions) : null,
            ClarificationsJson = summary.Clarifications.Count > 0 ? JsonSerializer.Serialize(summary.Clarifications, JsonOptions) : null,
            BuildSuccess = summary.BuildSuccess,
            CreatedAt = DateTime.UtcNow.ToString("O"),
        };
    }

    // ── IGoalSource ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
        => GetGoalsByStatusAsync(GoalStatus.Pending, ct);

    /// <inheritdoc />
    public async Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == goalId, ct);
            if (goal is null)
                throw new KeyNotFoundException($"Goal '{goalId}' not found in SQLite store.");

            goal.Status = status;

            if (metadata is not null)
            {
                if (metadata.StartedAt.HasValue)
                    goal.StartedAt = metadata.StartedAt.Value;
                if (metadata.CompletedAt.HasValue)
                    goal.CompletedAt = metadata.CompletedAt.Value;
                if (metadata.Iterations.HasValue)
                    goal.Iterations = metadata.Iterations.Value;
                if (metadata.FailureReason is not null)
                    goal.FailureReason = metadata.FailureReason;
                if (metadata.Notes is { Count: > 0 })
                    goal.Notes = [.. goal.Notes, .. metadata.Notes];
                if (metadata.PhaseDurations is { Count: > 0 })
                    goal.PhaseDurations = metadata.PhaseDurations;
                if (metadata.TotalDurationSeconds.HasValue)
                    goal.TotalDurationSeconds = metadata.TotalDurationSeconds.Value;
                if (metadata.MergeCommitHash is not null)
                    goal.MergeCommitHash = metadata.MergeCommitHash;

                if (metadata.IterationSummary is { } summary)
                    db.Add(ToEntity(goalId, summary));
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    // ── IGoalStore CRUD ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            return await db.Goals.AsNoTracking()
                .OrderByDescending(g => g.CreatedAt)
                .ToListAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var goal = await db.Goals.AsNoTracking().FirstOrDefaultAsync(g => g.Id == goalId, ct);
            if (goal is null)
                return null;

            var entities = await db.IterationSummaries.AsNoTracking()
                .Where(i => i.GoalId == goalId)
                .OrderBy(i => i.Iteration)
                .ToListAsync(ct);
            goal.IterationSummaries = entities.Select(ToDomain).ToList();
            return goal;
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        GoalId.Validate(goal.Id);

        var (db, ownsContext) = ResolveDbContext();
        try
        {
            db.Goals.Add(goal);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                throw new InvalidOperationException($"Failed to create goal '{goal.Id}'; a goal with the same ID may already exist.", ex);
            }
            finally
            {
                // Detach so the tracked instance does not interfere with later operations
                // on the same (shared/test) DbContext.
                db.Entry(goal).State = EntityState.Detached;
            }
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }

        _logger.LogInformation("Created goal {GoalId}: {Description}", goal.Id, Truncate(goal.Description, 80));
        return goal;
    }

    /// <inheritdoc />
    public async Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var existing = await db.Goals.FirstOrDefaultAsync(g => g.Id == goal.Id, ct);
            if (existing is null)
                throw new KeyNotFoundException($"Goal '{goal.Id}' not found in SQLite store.");

            existing.Description = goal.Description;
            existing.Status = goal.Status;
            existing.Priority = goal.Priority;
            existing.Scope = goal.Scope;
            existing.RepositoryNames = goal.RepositoryNames;
            existing.StartedAt = goal.StartedAt;
            existing.CompletedAt = goal.CompletedAt;
            existing.Iterations = goal.Iterations;
            existing.FailureReason = goal.FailureReason;
            existing.Notes = goal.Notes;
            existing.PhaseDurations = goal.PhaseDurations;
            existing.TotalDurationSeconds = goal.TotalDurationSeconds;
            existing.DependsOn = goal.DependsOn;
            existing.MergeCommitHash = goal.MergeCommitHash;
            existing.ReleaseId = goal.ReleaseId;
            existing.Documents = goal.Documents;
            existing.BranchCleanedUp = goal.BranchCleanedUp;
            existing.ReviewStatus = goal.ReviewStatus;

            // Metadata is init-only; set via the EF Core metadata API.
            db.Entry(existing).Property(e => e.Metadata).CurrentValue = goal.Metadata;

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var existing = await db.Goals.FirstOrDefaultAsync(g => g.Id == goalId, ct);
            if (existing is null)
                return false;

            // Clean up orphaned pipeline/conversation data BEFORE deleting the goal,
            // so a pipeline cleanup failure does not leave an orphaned goal deletion.
            _pipelineStore?.RemovePipeline(goalId);

            db.Goals.Remove(existing);
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Deleted goal {GoalId}", goalId);
            return true;
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    // ── Search ───────────────────────────────────────────────────────────

    /// <summary>
    /// Tokenizes a search query by splitting on whitespace, hyphens, underscores, and punctuation,
    /// lowercasing each token, and filtering out empty tokens.
    /// </summary>
    private static List<string> TokenizeSearchQuery(string query)
    {
        var tokens = Regex.Split(query, @"[\s\-_\p{P}]+")
            .Select(t => t.ToLowerInvariant())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        return tokens;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default)
    {
        var terms = TokenizeSearchQuery(query);

        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var q = db.Goals.AsNoTracking().AsQueryable();

            foreach (var term in terms)
            {
                var pattern = $"%{term}%";
                q = q.Where(g =>
                    EF.Functions.Like(g.Id, pattern) ||
                    EF.Functions.Like(g.Description, pattern) ||
                    EF.Functions.Like(g.FailureReason!, pattern));
            }

            if (statusFilter.HasValue)
                q = q.Where(g => g.Status == statusFilter.Value);

            return await q.OrderByDescending(g => g.CreatedAt).ToListAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            return await db.Goals.AsNoTracking()
                .Where(g => g.Status == status)
                .OrderByDescending(g => g.CreatedAt)
                .ToListAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    // ── Iterations ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            // INSERT OR REPLACE semantics: remove any existing entity with the same (goalId, iteration).
            var existing = await db.IterationSummaries
                .Where(i => i.GoalId == goalId && i.Iteration == summary.Iteration)
                .ToListAsync(ct);
            if (existing.Count > 0)
                db.IterationSummaries.RemoveRange(existing);

            db.Add(ToEntity(goalId, summary));
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var entities = await db.IterationSummaries.AsNoTracking()
                .Where(i => i.GoalId == goalId)
                .OrderBy(i => i.Iteration)
                .ToListAsync(ct);
            return entities.Select(ToDomain).ToList();
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    // ── Import ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default)
    {
        var imported = 0;
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            foreach (var goal in goals)
            {
                var exists = await db.Goals.AsNoTracking().AnyAsync(g => g.Id == goal.Id, ct);
                if (exists) continue;

                db.Goals.Add(goal);
                foreach (var summary in goal.IterationSummaries)
                    db.Add(ToEntity(goal.Id, summary));

                imported++;
            }

            if (imported > 0)
                await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }

        if (imported > 0)
            _logger.LogInformation("Imported {Count} goals into SQLite store", imported);

        return imported;
    }

    // ── Release CRUD ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            db.Releases.Add(release);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                throw new InvalidOperationException($"Failed to create release '{release.Id}'; a release with the same ID may already exist.", ex);
            }
            finally
            {
                db.Entry(release).State = EntityState.Detached;
            }
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }

        _logger.LogInformation("Created release {ReleaseId}: {Tag}", release.Id, release.Tag);
        return release;
    }

    /// <inheritdoc />
    public async Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            return await db.Releases.AsNoTracking().FirstOrDefaultAsync(r => r.Id == releaseId, ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            return await db.Releases.AsNoTracking()
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var existing = await db.Releases.FirstOrDefaultAsync(r => r.Id == release.Id, ct);
            if (existing is null)
                throw new KeyNotFoundException($"Release '{release.Id}' not found in SQLite store.");

            existing.Tag = release.Tag;
            existing.Status = release.Status;
            existing.Notes = release.Notes;
            existing.ReleasedAt = release.ReleasedAt;

            // RepositoryNames is init-only; set via the EF Core metadata API.
            db.Entry(existing).Property(e => e.RepositoryNames).CurrentValue = release.RepositoryNames;

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var release = await db.Releases.FirstOrDefaultAsync(r => r.Id == releaseId, ct);
            if (release is null)
                throw new KeyNotFoundException($"Release '{releaseId}' not found in SQLite store.");

            if (release.Status != ReleaseStatus.Planning)
                throw new InvalidOperationException(
                    $"Release '{releaseId}' cannot be edited because it is in '{release.Status}' status. Only Planning releases can be edited.");

            var updatedFields = new List<string>();

            if (update.Tag is not null)
            {
                release.Tag = update.Tag;
                updatedFields.Add("tag");
            }

            if (update.Notes is not null)
            {
                release.Notes = update.Notes;
                updatedFields.Add("notes");
            }

            if (update.Repositories is not null)
            {
                db.Entry(release).Property(e => e.RepositoryNames).CurrentValue = update.Repositories;
                updatedFields.Add("repositories");
            }

            if (updatedFields.Count == 0)
                return;

            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Updated release {ReleaseId} (fields: {Fields})",
                releaseId, string.Join(", ", updatedFields));
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var release = await db.Releases.FirstOrDefaultAsync(r => r.Id == releaseId, ct);
            if (release is null)
                return false; // not found

            if (release.Status != ReleaseStatus.Planning)
                return false; // only Planning releases may be deleted

            db.Releases.Remove(release);
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Deleted release {ReleaseId}", releaseId);
            return true;
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            return await db.Goals.AsNoTracking()
                .Where(g => g.ReleaseId == releaseId)
                .OrderByDescending(g => g.CreatedAt)
                .ToListAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    // ── Pipeline conversation ────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default)
    {
        if (_pipelineStore is null)
            return Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        var conversation = _pipelineStore.GetConversation(goalId);
        return Task.FromResult<IReadOnlyList<ConversationEntry>>(conversation);
    }

    // ── All Clarifications ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var q = db.IterationSummaries.AsNoTracking()
                .Where(i => i.ClarificationsJson != null && i.ClarificationsJson != "[]")
                .OrderByDescending(i => i.Id)
                .Select(i => new { i.GoalId, i.ClarificationsJson });

            if (limit.HasValue)
                q = q.Take(limit.Value * 10); // fetch extra rows to account for multi-clarification rows

            var rows = await q.ToListAsync(ct);

            var results = new List<(string GoalId, PersistedClarification Clarification)>();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.ClarificationsJson)) continue;
                var clarifications = JsonSerializer.Deserialize<List<PersistedClarification>>(row.ClarificationsJson, JsonOptions);
                if (clarifications is null) continue;

                foreach (var clarification in clarifications)
                    results.Add((row.GoalId, clarification));
            }

            results.Sort((a, b) => b.Clarification.Timestamp.CompareTo(a.Clarification.Timestamp));
            if (limit.HasValue && results.Count > limit.Value)
                results = results[..limit.Value];

            return results;
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }
    }

    // ── Iteration reset ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == goalId, ct);
            if (goal is null)
                throw new KeyNotFoundException($"Goal '{goalId}' not found in SQLite store.");

            goal.FailureReason = null;
            goal.Iterations = 0;
            goal.PhaseDurations = null;
            goal.TotalDurationSeconds = null;
            goal.StartedAt = null;
            goal.CompletedAt = null;

            var iterations = await db.IterationSummaries
                .Where(i => i.GoalId == goalId)
                .ToListAsync(ct);
            if (iterations.Count > 0)
                db.IterationSummaries.RemoveRange(iterations);

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (ownsContext)
                await db.DisposeAsync();
        }

        _logger.LogInformation("Reset iteration data for goal {GoalId}", goalId);
    }

    // ── Internals ────────────────────────────────────────────────────────

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}
