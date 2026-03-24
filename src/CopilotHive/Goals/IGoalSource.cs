namespace CopilotHive.Goals;

/// <summary>
/// Optional metadata passed alongside a goal status update.
/// </summary>
public sealed record GoalUpdateMetadata
{
    /// <summary>UTC timestamp when the goal was started.</summary>
    public DateTime? StartedAt { get; init; }
    /// <summary>UTC timestamp when the goal completed or failed.</summary>
    public DateTime? CompletedAt { get; init; }
    /// <summary>Total iterations used.</summary>
    public int? Iterations { get; init; }
    /// <summary>Failure reason (only for failed goals).</summary>
    public string? FailureReason { get; init; }
    /// <summary>Optional informational notes (e.g. "improver skipped: timeout").</summary>
    public List<string>? Notes { get; init; }
    /// <summary>Per-phase wall-clock durations in seconds.</summary>
    public Dictionary<string, double>? PhaseDurations { get; init; }
    /// <summary>Iteration summary to append to the goal's <see cref="Goal.IterationSummaries"/> list.</summary>
    public IterationSummary? IterationSummary { get; init; }
    /// <summary>Total wall-clock duration of the goal from start to completion, in seconds.</summary>
    public double? TotalDurationSeconds { get; init; }
}

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
    /// <param name="metadata">Optional metadata (timestamps, iterations, failure reason).</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default);
}
