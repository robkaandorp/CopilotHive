namespace CopilotHive.Goals;

/// <summary>
/// Abstraction over a backing store that provides pending goals and accepts status updates.
/// </summary>
public interface IGoalSource
{
    /// <summary>Unique name identifying this source (e.g. "file", "api").</summary>
    string Name { get; }

    /// <summary>
    /// Returns all goals that are currently in the <see cref="GoalStatus.Pending"/> state.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Read-only list of pending goals.</returns>
    Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists a status change for the specified goal back to the source.
    /// </summary>
    /// <param name="goalId">Identifier of the goal to update.</param>
    /// <param name="status">New status value.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateGoalStatusAsync(string goalId, GoalStatus status, CancellationToken ct = default);
}
