using CopilotHive.Goals;
using CopilotHive.Orchestration;

namespace CopilotHive.Tests;

/// <summary>Shared in-memory <see cref="IGoalStore"/> used by Brain tool tests.</summary>
public sealed class InMemoryGoalStore : IGoalStore
{
    private readonly Dictionary<string, Goal> _goals = new();

    public string Name => "FakeGoalStore";

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? goal : null);

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.FromResult(goal);
    }

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.Remove(goalId));

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(Array.Empty<(string, PersistedClarification)>());

    /// <summary>Adds a goal to the in-memory store for testing.</summary>
    public void AddGoal(Goal goal) => _goals[goal.Id] = goal;
}
