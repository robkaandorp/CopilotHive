namespace CopilotHive.Goals;

/// <summary>
/// Represents a single unit of work that the hive will attempt to accomplish.
/// </summary>
public sealed class Goal
{
    /// <summary>Unique identifier for this goal.</summary>
    public required string Id { get; init; }
    /// <summary>Human-readable description of what the goal requires.</summary>
    public required string Description { get; init; }
    /// <summary>Scheduling priority; higher-priority goals are dispatched first.</summary>
    public GoalPriority Priority { get; init; } = GoalPriority.Normal;
    /// <summary>Current lifecycle status of the goal.</summary>
    public GoalStatus Status { get; set; } = GoalStatus.Pending;
    /// <summary>Names of repositories this goal applies to.</summary>
    public List<string> RepositoryNames { get; init; } = [];
    /// <summary>IDs of goals that must complete before this goal can be dispatched.</summary>
    public List<string> DependsOn { get; init; } = [];
    /// <summary>Arbitrary key/value metadata associated with the goal.</summary>
    public Dictionary<string, string> Metadata { get; init; } = [];
    /// <summary>UTC timestamp when the goal was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>UTC timestamp when the goal was picked up for processing, or <c>null</c> if not yet started.</summary>
    public DateTime? StartedAt { get; set; }
    /// <summary>UTC timestamp when the goal finished (completed or failed), or <c>null</c> if still active.</summary>
    public DateTime? CompletedAt { get; set; }
    /// <summary>Number of iterations it took to complete or fail the goal, or <c>null</c> if not yet finished.</summary>
    public int? Iterations { get; set; }
    /// <summary>Reason the goal failed, or <c>null</c> for non-failed goals.</summary>
    public string? FailureReason { get; set; }
    /// <summary>Optional informational notes (e.g. "improver skipped: timeout").</summary>
    public List<string> Notes { get; set; } = [];
    /// <summary>Per-phase wall-clock durations in seconds.</summary>
    public Dictionary<string, double>? PhaseDurations { get; set; }
    /// <summary>Structured summaries written after each iteration completes.</summary>
    public List<IterationSummary> IterationSummaries { get; set; } = [];
    /// <summary>Total wall-clock duration of the goal from start to completion, in seconds.</summary>
    public double? TotalDurationSeconds { get; set; }
    /// <summary>SHA-1 hash of the merge commit that landed this goal's changes, or <c>null</c> if not yet merged.</summary>
    public string? MergeCommitHash { get; set; }
}

/// <summary>Structured summary of a single completed (or failed) pipeline iteration.</summary>
public sealed class IterationSummary
{
    /// <summary>One-based iteration number.</summary>
    public int Iteration { get; init; }
    /// <summary>Per-phase results for this iteration.</summary>
    public List<PhaseResult> Phases { get; init; } = [];
    /// <summary>Test counts, or <c>null</c> if no tests were run.</summary>
    public TestCounts? TestCounts { get; init; }
    /// <summary>"approve", "reject", or <c>null</c> if no review was run.</summary>
    public string? ReviewVerdict { get; init; }
    /// <summary>Notable events such as "improver skipped due to timeout".</summary>
    public List<string> Notes { get; init; } = [];
}

/// <summary>Result of a single pipeline phase within one iteration.</summary>
public sealed class PhaseResult
{
    /// <summary>Phase name (e.g. "Coding", "Testing").</summary>
    public required string Name { get; init; }
    /// <summary>"pass", "fail", or "skip".</summary>
    public required string Result { get; init; }
    /// <summary>Wall-clock duration of the phase in seconds.</summary>
    public double DurationSeconds { get; init; }
}

/// <summary>Aggregate test counts from a tester run.</summary>
public sealed class TestCounts
{
    /// <summary>Total number of tests discovered.</summary>
    public int Total { get; init; }
    /// <summary>Number of tests that passed.</summary>
    public int Passed { get; init; }
    /// <summary>Number of tests that failed.</summary>
    public int Failed { get; init; }
}

/// <summary>Scheduling priority levels for goals.</summary>
public enum GoalPriority
{
    /// <summary>Lowest priority; processed after all others.</summary>
    Low,
    /// <summary>Default priority level.</summary>
    Normal,
    /// <summary>Processed before Normal and Low goals.</summary>
    High,
    /// <summary>Highest priority; processed before all others.</summary>
    Critical,
}

/// <summary>Lifecycle status values for a goal.</summary>
public enum GoalStatus
{
    /// <summary>Goal is a draft — proposed but not yet approved for execution.</summary>
    Draft,
    /// <summary>Goal has not yet been started.</summary>
    Pending,
    /// <summary>Goal is currently being worked on.</summary>
    InProgress,
    /// <summary>Goal was successfully completed.</summary>
    Completed,
    /// <summary>Goal failed and will not be retried.</summary>
    Failed,
    /// <summary>Goal was cancelled before completion.</summary>
    Cancelled,
}
