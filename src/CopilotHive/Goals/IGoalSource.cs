namespace CopilotHive.Goals;

public interface IGoalSource
{
    string Name { get; }
    Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default);
    Task UpdateGoalStatusAsync(string goalId, GoalStatus status, CancellationToken ct = default);
}
