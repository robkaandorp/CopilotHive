namespace CopilotHive.Goals;

public sealed class GoalManager
{
    private static readonly GoalPriority[] PriorityOrder =
        [GoalPriority.Critical, GoalPriority.High, GoalPriority.Normal, GoalPriority.Low];

    private readonly List<IGoalSource> _sources = [];
    private readonly Dictionary<string, IGoalSource> _goalSourceMap = [];
    private readonly Lock _lock = new();

    public void AddSource(IGoalSource source)
    {
        lock (_lock)
        {
            _sources.Add(source);
        }
    }

    public IReadOnlyList<IGoalSource> Sources
    {
        get
        {
            lock (_lock) { return _sources.ToList().AsReadOnly(); }
        }
    }

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

    public async Task CompleteGoalAsync(string goalId, CancellationToken ct = default)
    {
        var source = GetSourceForGoal(goalId);
        await source.UpdateGoalStatusAsync(goalId, GoalStatus.Completed, ct);
    }

    public async Task FailGoalAsync(string goalId, string reason, CancellationToken ct = default)
    {
        var source = GetSourceForGoal(goalId);
        await source.UpdateGoalStatusAsync(goalId, GoalStatus.Failed, ct);
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
