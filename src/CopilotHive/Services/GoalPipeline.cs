using System.Collections.Concurrent;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;

namespace CopilotHive.Services;

/// <summary>
/// Tracks a single goal's progress through the multi-phase pipeline.
/// Thread-safe: state mutations are guarded by a lock.
/// </summary>
public sealed class GoalPipeline
{
    private readonly object _lock = new();

    /// <summary>Unique identifier of the goal this pipeline is tracking.</summary>
    public string GoalId { get; }
    /// <summary>Human-readable description of the goal.</summary>
    public string Description { get; }
    /// <summary>The goal this pipeline is working toward.</summary>
    public Goal Goal { get; }

    /// <summary>State machine that enforces valid phase transitions.</summary>
    public PipelineStateMachine StateMachine { get; } = new();

    /// <summary>Current phase the pipeline is executing.</summary>
    public GoalPhase Phase { get; private set; } = GoalPhase.Planning;

    /// <summary>Budget tracking review retry attempts.</summary>
    public RetryBudget ReviewRetryBudget { get; }

    /// <summary>Budget tracking test retry attempts.</summary>
    public RetryBudget TestRetryBudget { get; }

    /// <summary>Budget tracking iteration attempts (depleted each time a new iteration starts).</summary>
    public RetryBudget IterationBudget { get; }

    /// <summary>One-based iteration counter; increments each time the pipeline loops.</summary>
    public int Iteration => IterationBudget.Used + 1;

    /// <summary>Number of times the review phase has been retried in the current iteration.</summary>
    public int ReviewRetries => ReviewRetryBudget.Used;

    /// <summary>Number of times the test phase has been retried in the current iteration.</summary>
    public int TestRetries => TestRetryBudget.Used;

    /// <summary>Maximum number of task-level retries allowed.</summary>
    public int MaxRetries => ReviewRetryBudget.Allowed;

    /// <summary>Maximum number of iterations allowed before the goal is failed.</summary>
    public int MaxIterations => IterationBudget.Allowed + 1;

    /// <summary>Brain-determined plan for the current iteration, or null if no plan set.</summary>
    public IterationPlan? Plan { get; private set; }

    /// <summary>The active task ID currently assigned to a worker (null when idle).</summary>
    public string? ActiveTaskId { get; private set; }

    /// <summary>Grouped branch-related state (feature branch, iteration SHA, merge hash).</summary>
    public BranchContext Branch { get; } = new();

    /// <summary>The feature branch created by the coder for this goal.</summary>
    public string? CoderBranch
    {
        get => Branch.CoderBranch;
        set => Branch.CoderBranch = value;
    }

    /// <summary>
    /// Persisted agent session JSON blobs, keyed by role name (case-insensitive).
    /// Allows workers to resume mid-session after an orchestrator restart.
    /// </summary>
    public RoleSessionStore RoleSessions { get; } = new();

    /// <summary>In-memory iteration summaries for completed iterations (available to dashboard before goal finishes).</summary>
    public List<IterationSummary> CompletedIterationSummaries { get; } = [];

    /// <summary>Append-only log of phase entries, one per phase dispatch, in chronological order.</summary>
    public List<PhaseResult> PhaseLog { get; } = [];

    /// <summary>The most recently appended log entry, or <c>null</c> if no phase has started yet.</summary>
    public PhaseResult? CurrentPhaseEntry => PhaseLog.Count > 0 ? PhaseLog[^1] : null;

    /// <summary>All clarifications Q&amp;As that occurred during this goal's execution.</summary>
    public ConcurrentBag<ClarificationEntry> Clarifications { get; } = [];

    /// <summary>
    /// Progress reports (<c>report_progress</c> tool calls) generated during this goal's execution.
    /// Stored per-pipeline for Phase/Iteration-aware dashboard filtering.
    /// </summary>
    public ConcurrentBag<ProgressEntry> ProgressReports { get; } = [];

    /// <summary>
    /// When <c>true</c>, the active phase is paused waiting for a clarification answer.
    /// The dashboard displays this phase with status "waiting".
    /// </summary>
    public bool IsWaitingForClarification { get; set; }

    /// <summary>Metrics extracted by the Brain from worker output.</summary>
    public IterationMetrics Metrics { get; } = new() { Iteration = 1 };

    /// <summary>Owns the per-goal conversation history and context-summary logic.</summary>
    public ConversationTracker ConversationTracker { get; } = new();

    /// <summary>Per-goal conversation history for the Brain.</summary>
    public List<ConversationEntry> Conversation => ConversationTracker.Entries;

    /// <summary>UTC timestamp when the goal was started (captured at dispatch time, before the pipeline is created).</summary>
    public DateTime? GoalStartedAt { get; internal set; }

    /// <summary>UTC timestamp when this pipeline was created.</summary>
    public DateTime CreatedAt { get; private init; } = DateTime.UtcNow;
    /// <summary>UTC timestamp when this pipeline completed (Done or Failed), or <c>null</c> if still active.</summary>
    public DateTime? CompletedAt { get; private set; }
    /// <summary>SHA-1 hash of the merge commit, or <c>null</c> if not yet merged.</summary>
    public string? MergeCommitHash
    {
        get => Branch.MergeCommitHash;
        set => Branch.MergeCommitHash = value;
    }

    /// <summary>HEAD SHA captured before the coder ran this iteration, or <c>null</c>.</summary>
    public string? IterationStartSha
    {
        get => Branch.IterationStartSha;
        set => Branch.IterationStartSha = value;
    }

    /// <summary>
    /// Creates a new pipeline for the specified goal.
    /// </summary>
    /// <param name="goal">The goal to track.</param>
    /// <param name="maxRetries">Maximum task-level retries allowed.</param>
    /// <param name="maxIterations">Maximum iterations before the goal is failed.</param>
    public GoalPipeline(Goal goal, int maxRetries = Constants.DefaultMaxRetriesPerTask, int maxIterations = Constants.DefaultMaxIterations)
    {
        Goal = goal;
        GoalId = goal.Id;
        Description = goal.Description;
        ReviewRetryBudget = new RetryBudget(maxRetries);
        TestRetryBudget = new RetryBudget(maxRetries);
        IterationBudget = new RetryBudget(maxIterations - 1);
    }

    /// <summary>Restore a pipeline from a persisted snapshot.</summary>
    internal GoalPipeline(PipelineSnapshot snapshot)
    {
        Goal = snapshot.Goal;
        GoalId = snapshot.GoalId;
        Description = snapshot.Description;
        Phase = snapshot.Phase;

        // Restore budgets from persisted scalar values.
        // IterationBudget: allowed = maxIterations - 1, used = iteration - 1
        IterationBudget = new RetryBudget(snapshot.MaxIterations - 1);
        for (var i = 0; i < snapshot.Iteration - 1; i++)
            IterationBudget.TryConsume();

        ReviewRetryBudget = new RetryBudget(snapshot.MaxRetries);
        for (var i = 0; i < snapshot.ReviewRetries; i++)
            ReviewRetryBudget.TryConsume();

        TestRetryBudget = new RetryBudget(snapshot.MaxRetries);
        for (var i = 0; i < snapshot.TestRetries; i++)
            TestRetryBudget.TryConsume();

        ActiveTaskId = snapshot.ActiveTaskId;
        Branch.CoderBranch = snapshot.CoderBranch;
        Plan = snapshot.Plan;
        CreatedAt = snapshot.CreatedAt;
        CompletedAt = snapshot.CompletedAt;
        GoalStartedAt = snapshot.GoalStartedAt;
        Branch.MergeCommitHash = snapshot.MergeCommitHash;
        Branch.IterationStartSha = snapshot.IterationStartSha;

        RoleSessions.Load(snapshot.RoleSessions);

        Metrics.BuildSuccess = snapshot.Metrics.BuildSuccess;
        Metrics.TotalTests = snapshot.Metrics.TotalTests;
        Metrics.PassedTests = snapshot.Metrics.PassedTests;
        Metrics.FailedTests = snapshot.Metrics.FailedTests;
        Metrics.CoveragePercent = snapshot.Metrics.CoveragePercent;
        Metrics.IntegrationTestsTotal = snapshot.Metrics.IntegrationTestsTotal;
        Metrics.IntegrationTestsPassed = snapshot.Metrics.IntegrationTestsPassed;
        Metrics.RuntimeVerified = snapshot.Metrics.RuntimeVerified;

        foreach (var entry in snapshot.Conversation)
            Conversation.Add(entry);

        foreach (var entry in snapshot.PhaseLog)
            PhaseLog.Add(entry);

        // Rebuild the state machine from the persisted plan so the dashboard
        // can correctly show completed / active / pending phases.
        if (Plan is not null)
            StateMachine.RestoreFromPlan(Plan.Phases, Phase);
    }

    /// <summary>Advance to the next phase.</summary>
    public void AdvanceTo(GoalPhase phase)
    {
        lock (_lock)
        {
            Phase = phase;
            if (phase is GoalPhase.Done or GoalPhase.Failed)
                CompletedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Record that a task was dispatched for this pipeline.</summary>
    public void SetActiveTask(string taskId, string? branch = null)
    {
        lock (_lock)
        {
            ActiveTaskId = taskId;
            // Only set CoderBranch on first assignment (the coder's branch).
            // Subsequent phases reuse the same branch for merging.
            if (branch is not null && CoderBranch is null)
                CoderBranch = branch;
        }
    }

    /// <summary>Clear the active task after completion.</summary>
    public void ClearActiveTask()
    {
        lock (_lock)
        {
            ActiveTaskId = null;
        }
    }

    /// <summary>Set the iteration plan from the Brain.</summary>
    public void SetPlan(IterationPlan plan)
    {
        lock (_lock)
        {
            Plan = plan;
        }
    }

    /// <summary>Clear the iteration plan (e.g., after a failure loop-back to Coding).</summary>
    public void ClearPlan()
    {
        lock (_lock)
        {
            Plan = null;
        }
    }

    /// <summary>Records a worker progress report into the pipeline's per-pipeline log.</summary>
    public void AddProgressReport(string workerId, string status, string details)
    {
        ProgressReports.Add(new ProgressEntry
        {
            Timestamp = DateTime.UtcNow,
            WorkerId = workerId,
            GoalId = GoalId,
            Phase = Phase.ToString(),
            Iteration = Iteration,
            Status = status,
            Details = details,
            Occurrence = CurrentPhaseEntry?.Occurrence ?? 1,
        });
    }

    /// <summary>Returns the persisted session JSON for the given role, or <c>null</c> if not found.</summary>
    public string? GetRoleSession(string roleName) =>
        RoleSessions.Get(roleName);

    /// <summary>Stores the session JSON for the given role (case-insensitive key).</summary>
    public void SetRoleSession(string roleName, string sessionJson) =>
        RoleSessions.Set(roleName, sessionJson);

    /// <summary>Build a context summary for the Brain about this pipeline's current state.</summary>
    public string BuildContextSummary() => ConversationTracker.BuildContextSummary(this);

    /// <summary>Returns a human-friendly display name for the given <see cref="GoalPhase"/>.</summary>
    /// <param name="phase">The pipeline phase to get a display name for.</param>
    /// <returns>A human-readable string representation of the phase.</returns>
    public static string GetDisplayName(GoalPhase phase) => phase switch
    {
        GoalPhase.Planning   => "Planning",
        GoalPhase.Coding     => "Coding",
        GoalPhase.Testing    => "Testing",
        GoalPhase.DocWriting => "Doc Writing",
        GoalPhase.Review     => "Review",
        GoalPhase.Improve    => "Improvement",
        GoalPhase.Merging    => "Merging",
        GoalPhase.Done       => "Done",
        GoalPhase.Failed     => "Failed",
        _                    => throw new InvalidOperationException($"Unhandled GoalPhase: {phase}")
    };
}
