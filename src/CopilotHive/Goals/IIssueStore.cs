namespace CopilotHive.Goals;

/// <summary>
/// Persistence abstraction for <see cref="Issue"/> entities.
/// </summary>
public interface IIssueStore
{
    /// <summary>Returns all issues, ordered by <see cref="Issue.CreatedAt"/> descending.</summary>
    Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns issues filtered by the given criteria, ordered by <see cref="Issue.CreatedAt"/> descending.
    /// The repository filter matches case-insensitively against any entry in <see cref="Issue.RepositoryNames"/>.
    /// </summary>
    Task<IReadOnlyList<Issue>> GetIssuesAsync(
        IssueStatus? status = null,
        IssueType? type = null,
        IssueSeverity? severity = null,
        string? repository = null,
        string? sourceGoalId = null,
        CancellationToken ct = default);

    /// <summary>Returns a single issue by ID, or <c>null</c> if not found.</summary>
    Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new issue. Throws <see cref="ArgumentException"/> if the ID is null or empty,
    /// and <see cref="InvalidOperationException"/> if an issue with the same ID already exists.
    /// </summary>
    Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default);

    /// <summary>
    /// Updates an issue's mutable fields. Immutable fields (Id, CreatedAt, SourceGoalId, SourceRole,
    /// SourceIteration) are preserved. Throws <see cref="InvalidOperationException"/> if not found.
    /// </summary>
    Task UpdateIssueAsync(Issue issue, CancellationToken ct = default);

    /// <summary>Deletes an issue by ID. Returns <c>false</c> if not found.</summary>
    Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default);
}
