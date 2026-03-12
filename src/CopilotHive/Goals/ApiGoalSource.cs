using System.Collections.Concurrent;

namespace CopilotHive.Goals;

public sealed class ApiGoalSource : IGoalSource
{
    private readonly ConcurrentDictionary<string, Goal> _goals = new();

    public string Name => "api";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Goal> pending = _goals.Values
            .Where(g => g.Status == GoalStatus.Pending)
            .ToList()
            .AsReadOnly();

        return Task.FromResult(pending);
    }

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, CancellationToken ct = default)
    {
        if (!_goals.TryGetValue(goalId, out var goal))
            throw new KeyNotFoundException($"Goal '{goalId}' not found in API source.");

        goal.Status = status;
        return Task.CompletedTask;
    }

    public Goal AddGoal(Goal goal)
    {
        if (!_goals.TryAdd(goal.Id, goal))
            throw new InvalidOperationException($"Goal '{goal.Id}' already exists.");

        return goal;
    }

    public IReadOnlyList<Goal> GetAllGoals() =>
        _goals.Values.ToList().AsReadOnly();

    public Goal? GetGoal(string id) =>
        _goals.GetValueOrDefault(id);
}
