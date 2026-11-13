using CopilotHive.Goals;
using CopilotHive.Orchestration;

namespace CopilotHive.Tests;

/// <summary>
/// An <see cref="IGoalStore"/> that delegates everything to the shared in-memory store but
/// RECORDS every <see cref="GetAllClarificationsAsync"/> call (count, limit argument and token) and
/// optionally throws from it. It lets the page tests prove that the persisted clarification
/// history is read through the facade and never through a direct, identity-free store read — the
/// exact regression the Orchestrator page migration closes.
/// </summary>
internal sealed class RecordingGoalStore : IGoalStore
{
    private readonly InMemoryGoalStore _inner = new();

    /// <summary>How many times <see cref="GetAllClarificationsAsync"/> was called.</summary>
    internal int GetAllClarificationsCallCount { get; private set; }

    /// <summary>The <c>limit</c> argument of every <see cref="GetAllClarificationsAsync"/> call.</summary>
    internal List<int?> ObservedLimits { get; } = [];

    public string Name => _inner.Name;

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        _inner.GetAllGoalsAsync(ct);

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        _inner.GetGoalAsync(goalId, ct);

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) =>
        _inner.CreateGoalAsync(goal, ct);

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) =>
        _inner.UpdateGoalAsync(goal, ct);

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        _inner.DeleteGoalAsync(goalId, ct);

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        _inner.SearchGoalsAsync(query, statusFilter, ct);

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        _inner.GetGoalsByStatusAsync(status, ct);

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        _inner.AddIterationAsync(goalId, summary, ct);

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        _inner.GetIterationsAsync(goalId, ct);

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        _inner.CreateReleaseAsync(release, ct);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        _inner.GetReleaseAsync(releaseId, ct);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        _inner.GetReleasesAsync(ct);

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        _inner.UpdateReleaseAsync(release, ct);

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        _inner.UpdateReleaseAsync(releaseId, update, ct);

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        _inner.DeleteReleaseAsync(releaseId, ct);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        _inner.GetGoalsByReleaseAsync(releaseId, ct);

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        _inner.GetPipelineConversationAsync(goalId, ct);

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        _inner.UpdateGoalStatusAsync(goalId, status, metadata, ct);

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        _inner.GetPendingGoalsAsync(ct);

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        _inner.ResetGoalIterationDataAsync(goalId, ct);

    /// <summary>Records the call and delegates to the in-memory store (empty history by default).</summary>
    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
        int? limit = null, CancellationToken ct = default)
    {
        GetAllClarificationsCallCount++;
        ObservedLimits.Add(limit);
        return _inner.GetAllClarificationsAsync(limit, ct);
    }

    /// <summary>Seeds the in-memory store.</summary>
    public void AddGoal(Goal goal) => _inner.AddGoal(goal);
}
