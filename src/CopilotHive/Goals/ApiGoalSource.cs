using CopilotHive.Configuration;
using System.Collections.Concurrent;

namespace CopilotHive.Goals;

/// <summary>
/// In-memory goal source backed by a concurrent dictionary; used by the HTTP API to inject goals at runtime.
/// </summary>
public sealed class ApiGoalSource : IGoalSource
{
    private readonly ConcurrentDictionary<string, Goal> _goals = new();

    /// <summary>Unique name identifying this goal source.</summary>
    public string Name => "api";

    /// <summary>
    /// Returns all goals whose status is <see cref="GoalStatus.Pending"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Read-only list of pending goals.</returns>
    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Goal> pending = _goals.Values
            .Where(g => g.Status == GoalStatus.Pending)
            .ToList()
            .AsReadOnly();

        return Task.FromResult(pending);
    }

    /// <summary>
    /// Updates the status of a goal already held by this source.
    /// </summary>
    /// <param name="goalId">Identifier of the goal to update.</param>
    /// <param name="status">New status to apply.</param>
    /// <param name="metadata">Optional metadata (timestamps, iterations, failure reason).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        if (!_goals.TryGetValue(goalId, out var goal))
            throw new KeyNotFoundException($"Goal '{goalId}' not found in API source.");

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
                goal.Notes.AddRange(metadata.Notes);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a new goal to this source. Throws if a goal with the same ID already exists.
    /// </summary>
    /// <param name="goal">The goal to add.</param>
    /// <returns>The same goal that was added.</returns>
    public Goal AddGoal(Goal goal)
    {
        GoalId.Validate(goal.Id);
        if (!_goals.TryAdd(goal.Id, goal))
            throw new InvalidOperationException($"Goal '{goal.Id}' already exists.");

        return goal;
    }

    /// <summary>Returns a read-only snapshot of all goals regardless of status.</summary>
    public IReadOnlyList<Goal> GetAllGoals() =>
        _goals.Values.ToList().AsReadOnly();

    /// <summary>
    /// Looks up a single goal by its identifier.
    /// </summary>
    /// <param name="id">Goal identifier.</param>
    /// <returns>The matching goal, or <c>null</c> if not found.</returns>
    public Goal? GetGoal(string id) =>
        _goals.GetValueOrDefault(id);
}
