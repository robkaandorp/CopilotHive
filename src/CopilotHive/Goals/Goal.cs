using CopilotHive.Services;

namespace CopilotHive.Goals;

/// <summary>
/// Represents a single unit of work that the hive will attempt to accomplish.
/// </summary>
public sealed class Goal
{
    /// <summary>Unique identifier for this goal.</summary>
    public required string Id { get; init; }
    /// <summary>Human-readable description of what the goal requires.</summary>
    public required string Description { get; set; }
    /// <summary>Scheduling priority; higher-priority goals are dispatched first.</summary>
    public GoalPriority Priority { get; set; } = GoalPriority.Normal;
    /// <summary>Scope of change this goal introduces.</summary>
    public GoalScope Scope { get; set; } = GoalScope.Patch;
    /// <summary>Current lifecycle status of the goal.</summary>
    public GoalStatus Status { get; set; } = GoalStatus.Pending;
    /// <summary>Names of repositories this goal applies to.</summary>
    public List<string> RepositoryNames { get; set; } = [];
    /// <summary>IDs of goals that must complete before this goal can be dispatched.</summary>
    public List<string> DependsOn { get; set; } = [];
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

    /// <summary>Optional release identifier that groups this goal into a release, or <c>null</c> if unassigned.</summary>
    public string? ReleaseId { get; set; }

    /// <summary>
    /// IDs of knowledge documents related to this goal.
    /// Set by the Composer when decomposing user intent into goals.
    /// </summary>
    public List<string> Documents { get; set; } = [];
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
    /// <summary>Whether the build succeeded during the testing phase.</summary>
    public bool BuildSuccess { get; init; }
    /// <summary>"approve", "reject", or <c>null</c> if no review was run.</summary>
    public string? ReviewVerdict { get; init; }
    /// <summary>Notable events such as "improver skipped due to timeout".</summary>
    public List<string> Notes { get; init; } = [];
    /// <summary>
    /// Raw worker outputs keyed by <c>{role}-{iteration}</c> (e.g. "coder-1").
    /// Populated when the iteration summary is built so outputs survive goal completion.
    /// </summary>
    public Dictionary<string, string> PhaseOutputs { get; set; } = [];
    /// <summary>Clarification Q&amp;As that occurred during this iteration.</summary>
    public List<PersistedClarification> Clarifications { get; set; } = [];
    /// <summary>Reason for the iteration plan, or <c>null</c> if not available.</summary>
    public string? PlanReason { get; init; }
}

/// <summary>Serialisable clarification record stored in the database alongside an iteration summary.</summary>
public sealed class PersistedClarification
{
    /// <summary>UTC time the clarification was created.</summary>
    public DateTime Timestamp { get; init; }
    /// <summary>Pipeline phase when the clarification was asked (e.g. "Coding").</summary>
    public string Phase { get; init; } = "";
    /// <summary>Worker role that triggered the question (e.g. "coder").</summary>
    public string WorkerRole { get; init; } = "";
    /// <summary>The question that was asked.</summary>
    public string Question { get; init; } = "";
    /// <summary>The answer that was provided.</summary>
    public string Answer { get; init; } = "";
    /// <summary>Who answered: "brain", "composer", "human", or "timeout".</summary>
    public string AnsweredBy { get; init; } = "";
    /// <summary>
    /// 1-based occurrence index of the phase within the iteration plan.
    /// Defaults to 0 for entries persisted before per-occurrence tracking (backward compat).
    /// </summary>
    public int Occurrence { get; init; }
}

/// <summary>Result of a single pipeline phase within one iteration.</summary>
public sealed class PhaseResult
{
    /// <summary>Phase that was executed.</summary>
    public required GoalPhase Name { get; set; }
    /// <summary>Outcome of the phase execution.</summary>
    public required PhaseOutcome Result { get; set; }
    /// <summary>Wall-clock duration of the phase in seconds. Computed from timestamps when available.</summary>
    private double _durationSeconds;

    /// <summary>
    /// Wall-clock duration of the phase in seconds. When both <see cref="StartedAt"/> and
    /// <see cref="CompletedAt"/> are set, the value is computed from the timestamps. Otherwise
    /// returns the explicitly set value (for JSON backward compatibility).
    /// </summary>
    public double DurationSeconds
    {
        get => (CompletedAt.HasValue && StartedAt.HasValue)
            ? (CompletedAt.Value - StartedAt.Value).TotalSeconds
            : _durationSeconds;
        set => _durationSeconds = value;
    }
    /// <summary>Raw worker output captured for this phase, or <c>null</c> if not recorded.</summary>
    public string? WorkerOutput { get; set; }
    /// <summary>Brain prompt (user message) sent when crafting the worker prompt for this phase.</summary>
    public string? BrainPrompt { get; set; }
    /// <summary>Crafted worker prompt (Brain assistant message) for this phase.</summary>
    public string? WorkerPrompt { get; set; }
    /// <summary>Planning prompt sent to the Brain during the Planning phase.</summary>
    public string? PlanningPrompt { get; set; }
    /// <summary>Brain response to the Planning prompt.</summary>
    public string? PlanningResponse { get; set; }
    /// <summary>
    /// 1-based occurrence index of this phase within the iteration plan (e.g. 1 for first Coding, 2 for second Coding).
    /// Null for legacy persisted summaries that predate per-occurrence tracking.
    /// </summary>
    public int? Occurrence { get; set; }
    /// <summary>1-based iteration number this phase entry belongs to. Null for legacy data.</summary>
    public int? Iteration { get; set; }
    /// <summary>UTC timestamp when the phase was dispatched to the worker.</summary>
    public DateTime? StartedAt { get; set; }
    /// <summary>UTC timestamp when the worker result was received.</summary>
    public DateTime? CompletedAt { get; set; }
    /// <summary>The raw verdict string from the worker (e.g. "PASS", "FAIL", "APPROVE", "REQUEST_CHANGES").</summary>
    public string? Verdict { get; set; }

    /// <summary>
    /// Creates a new <see cref="PhaseResult"/> entry for a phase that is about to start.
    /// Sets sensible defaults: <see cref="Result"/> = <see cref="PhaseOutcome.Pass"/>,
    /// <see cref="StartedAt"/> = <see cref="DateTime.UtcNow"/>, and populates
    /// <see cref="Occurrence"/> and <see cref="Iteration"/> from the pipeline state.
    /// </summary>
    /// <param name="phase">The phase being dispatched.</param>
    /// <param name="iteration">The 1-based iteration number.</param>
    /// <param name="occurrence">The 1-based occurrence index within the iteration plan.</param>
    /// <returns>A new <see cref="PhaseResult"/> ready to be appended to <see cref="GoalPipeline.PhaseLog"/>.</returns>
    public static PhaseResult Create(GoalPhase phase, int iteration, int occurrence) => new()
    {
        Name = phase,
        Result = PhaseOutcome.Pass,
        Iteration = iteration,
        Occurrence = occurrence,
        StartedAt = DateTime.UtcNow,
    };
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

/// <summary>Indicates the breadth of change a goal is expected to introduce.</summary>
public enum GoalScope
{
    /// <summary>Small, isolated fix or tweak with no public API impact.</summary>
    Patch,
    /// <summary>New capability added in a backward-compatible way.</summary>
    Feature,
    /// <summary>Change that breaks backward compatibility or alters existing contracts.</summary>
    Breaking,
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
