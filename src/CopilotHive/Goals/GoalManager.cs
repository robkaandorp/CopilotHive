namespace CopilotHive.Goals;

/// <summary>
/// Aggregates multiple <see cref="IGoalSource"/> instances and dispatches goals by priority.
/// Thread-safe.
/// </summary>
public sealed class GoalManager
{
    private static readonly GoalPriority[] PriorityOrder =
        [GoalPriority.Critical, GoalPriority.High, GoalPriority.Normal, GoalPriority.Low];

    private readonly List<IGoalSource> _sources = [];
    private readonly Dictionary<string, IGoalSource> _goalSourceMap = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Registers a new goal source with this manager.
    /// </summary>
    /// <param name="source">The source to add.</param>
    public void AddSource(IGoalSource source)
    {
        lock (_lock)
        {
            _sources.Add(source);
        }
    }

    /// <summary>A snapshot of all currently registered goal sources.</summary>
    public IReadOnlyList<IGoalSource> Sources
    {
        get
        {
            lock (_lock) { return _sources.ToList().AsReadOnly(); }
        }
    }

    /// <summary>
    /// Queries all registered sources and returns the highest-priority pending goal,
    /// or <c>null</c> when no pending goals exist.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The next goal to process, or <c>null</c>.</returns>
    public async Task<Goal?> GetNextGoalAsync(CancellationToken ct = default)
    {
        List<IGoalSource> snapshot;
        lock (_lock) { snapshot = [.. _sources]; }

        Goal? best = null;

        foreach (var source in snapshot)
        {
            var pending = await source.GetPendingGoalsAsync(ct);
            foreach (var goal in pending)
            {
                lock (_lock) { _goalSourceMap.TryAdd(goal.Id, source); }

                if (best is null || ComparePriority(goal.Priority, best.Priority) > 0)
                    best = goal;
            }
        }

        return best;
    }

    /// <summary>
    /// Marks the specified goal as <see cref="GoalStatus.Completed"/> in its source.
    /// </summary>
    /// <param name="goalId">Identifier of the goal to complete.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CompleteGoalAsync(string goalId, CancellationToken ct = default)
    {
        var source = GetSourceForGoal(goalId);
        await source.UpdateGoalStatusAsync(goalId, GoalStatus.Completed, ct);
    }

    /// <summary>
    /// Marks the specified goal as <see cref="GoalStatus.Failed"/> in its source.
    /// </summary>
    /// <param name="goalId">Identifier of the goal to fail.</param>
    /// <param name="reason">Human-readable reason for the failure (logged only).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task FailGoalAsync(string goalId, string reason, CancellationToken ct = default)
    {
        var source = GetSourceForGoal(goalId);
        await source.UpdateGoalStatusAsync(goalId, GoalStatus.Failed, ct);
    }

    /// <summary>
    /// Updates the status of a goal in its originating source.
    /// </summary>
    /// <param name="goalId">Identifier of the goal to update.</param>
    /// <param name="status">The new status to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateGoalStatusAsync(string goalId, GoalStatus status, CancellationToken ct = default)
    {
        var source = GetSourceForGoal(goalId);
        await source.UpdateGoalStatusAsync(goalId, status, ct);
    }

    private IGoalSource GetSourceForGoal(string goalId)
    {
        lock (_lock)
        {
            if (_goalSourceMap.TryGetValue(goalId, out var source))
                return source;
        }

        throw new KeyNotFoundException($"Goal '{goalId}' not found in any source.");
    }

    private static int ComparePriority(GoalPriority a, GoalPriority b) =>
        Array.IndexOf(PriorityOrder, b) - Array.IndexOf(PriorityOrder, a);
}
