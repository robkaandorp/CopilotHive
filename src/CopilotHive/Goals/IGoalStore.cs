using CopilotHive.Orchestration;

namespace CopilotHive.Goals;

/// <summary>
/// Extended goal store with full CRUD, search, and iteration tracking.
/// Replaces <see cref="IGoalSource"/> as the primary goal persistence abstraction.
/// </summary>
public interface IGoalStore : IGoalSource
{
    /// <summary>Returns all goals regardless of status.</summary>
    Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default);

    /// <summary>Returns a single goal by ID, or <c>null</c> if not found.</summary>
    Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default);

    /// <summary>Creates a new goal. Throws if a goal with the same ID already exists.</summary>
    Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default);

    /// <summary>Updates a goal's mutable fields. Throws if goal not found.</summary>
    Task UpdateGoalAsync(Goal goal, CancellationToken ct = default);

    /// <summary>Deletes a goal and its related data. Returns <c>false</c> if not found.</summary>
    Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default);

    /// <summary>
    /// Searches goals by text query (matches against id, description, and failure_reason).
    /// Optionally filters by status.
    /// </summary>
    Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default);

    /// <summary>Returns all goals filtered by status.</summary>
    Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default);

    /// <summary>Appends an iteration summary to a goal's history.</summary>
    Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default);

    /// <summary>Returns all iteration summaries for a goal, ordered by iteration number.</summary>
    Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default);

    /// <summary>
    /// Imports goals from a list (e.g. from goals.yaml). Skips goals whose IDs already exist.
    /// Returns the number of goals imported.
    /// </summary>
    Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default);

    // ── Release CRUD ─────────────────────────────────────────────────────

    /// <summary>Creates a new release. Throws if a release with the same ID already exists.</summary>
    Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default);

    /// <summary>Returns a single release by ID, or <c>null</c> if not found.</summary>
    Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default);

    /// <summary>Returns all releases, ordered by creation date descending.</summary>
    Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default);

    /// <summary>Updates a release's mutable fields. Throws if release not found.</summary>
    Task UpdateReleaseAsync(Release release, CancellationToken ct = default);

    /// <summary>
    /// Applies a partial update to a Planning release.
    /// Only non-null fields in <paramref name="update"/> are written.
    /// Throws <see cref="InvalidOperationException"/> if the release is not in Planning status.
    /// Throws <see cref="KeyNotFoundException"/> if the release does not exist.
    /// </summary>
    /// <param name="releaseId">ID of the release to update.</param>
    /// <param name="update">Fields to update; null values are ignored.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default);

    /// <summary>Deletes a release. Returns <c>false</c> if not found.</summary>
    Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default);

    /// <summary>Returns all goals assigned to the given release.</summary>
    Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default);

    // ── Pipeline conversation ────────────────────────────────────────────

    /// <summary>Returns the pipeline conversation entries for the given goal.</summary>
    /// <param name="goalId">The goal ID whose pipeline conversation to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The conversation entries, or an empty list if no pipeline exists for this goal.</returns>
    Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default);

    /// <summary>
    /// Resets all iteration data for a goal so it can be retried from scratch.
    /// Clears: <c>FailureReason</c>, <c>Iterations</c> (reset to 0), <c>IterationSummaries</c>,
    /// phase outputs, <c>TotalDurationSeconds</c>, and <c>StartedAt</c>.
    /// Preserves: ID, Description, Priority, Scope, DependsOn, ReleaseId, RepositoryNames, CreatedAt.
    /// </summary>
    /// <param name="goalId">The ID of the goal to reset.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default);

    /// <summary>
    /// Returns a flat list of all clarifications recorded across all goals and iterations,
    /// each paired with the goal ID it belongs to.
    /// </summary>
    /// <param name="limit">Optional maximum number of clarifications to return (most recent first).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Clarifications ordered by timestamp descending, up to <paramref name="limit"/> items.</returns>
    Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default);
}
