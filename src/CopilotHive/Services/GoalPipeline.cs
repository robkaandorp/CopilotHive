using System.Collections.Concurrent;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Phases a goal progresses through in the pipeline.
/// </summary>
public enum GoalPhase
{
    /// <summary>Initial phase: the brain is planning the iteration.</summary>
    Planning,
    /// <summary>The coder worker is implementing the goal.</summary>
    Coding,
    /// <summary>The reviewer worker is reviewing the coder's changes.</summary>
    Review,
    /// <summary>The tester worker is running and writing tests.</summary>
    Testing,
    /// <summary>The doc-writer worker is updating documentation.</summary>
    DocWriting,
    /// <summary>The improver worker is improving AGENTS.md files.</summary>
    Improve,
    /// <summary>The feature branch is being merged to main.</summary>
    Merging,
    /// <summary>The goal has been completed successfully.</summary>
    Done,
    /// <summary>The goal has failed and will not be retried.</summary>
    Failed,
}

/// <summary>
/// Brain-determined workflow plan for a single iteration.
/// </summary>
public sealed class IterationPlan
{
    /// <summary>Ordered list of phases the Brain wants to execute this iteration.</summary>
    public List<GoalPhase> Phases { get; init; } = [];

    /// <summary>
    /// Per-phase instructions/context from the Brain.
    /// Keys are lowercase phase names: "coding", "review", "testing", "merging", etc.
    /// For multi-round plans, indexed keys like "coding-2" are used for the 2nd occurrence.
    /// </summary>
    public Dictionary<string, string> PhaseInstructions { get; init; } = [];

    /// <summary>Per-role model tier overrides from the Brain, keyed by phase (e.g. Coding → Premium).</summary>
    public Dictionary<GoalPhase, ModelTier> PhaseTiers { get; init; } = [];

    /// <summary>Brain's reasoning for this plan.</summary>
    public string? Reason { get; init; }

    /// <summary>Index of the current phase being executed.</summary>
    public int CurrentPhaseIndex { get; set; }

    /// <summary>The current phase in the plan, or null if plan is complete.</summary>
    public GoalPhase? CurrentPhase => CurrentPhaseIndex < Phases.Count ? Phases[CurrentPhaseIndex] : null;

    /// <summary>The next phase in the plan after the current one, or null if none.</summary>
    public GoalPhase? NextPhase => CurrentPhaseIndex + 1 < Phases.Count ? Phases[CurrentPhaseIndex + 1] : null;

    /// <summary>Whether all planned phases have been executed.</summary>
    public bool IsComplete => CurrentPhaseIndex >= Phases.Count;

    /// <summary>Advance to the next phase. Returns the new current phase or null if complete.</summary>
    public GoalPhase? Advance()
    {
        if (!IsComplete) CurrentPhaseIndex++;
        return CurrentPhase;
    }

    /// <summary>Returns the phase that comes after the specified phase, or null if none.</summary>
    public GoalPhase? NextPhaseAfter(GoalPhase phase)
    {
        var index = Phases.IndexOf(phase);
        return index >= 0 && index + 1 < Phases.Count ? Phases[index + 1] : null;
    }

    /// <summary>
    /// Returns the instruction string for the given phase and occurrence index (1-based).
    /// Tries the indexed key first (e.g. "coding-2" for occurrence 2), then falls back
    /// to the bare key (e.g. "coding") for backward compatibility.
    /// Returns null if neither key exists.
    /// </summary>
    /// <param name="phase">The phase to look up.</param>
    /// <param name="occurrenceIndex">The 1-based occurrence index within the plan's phase sequence.</param>
    /// <returns>The instruction string, or null if not found.</returns>
    public string? GetPhaseInstruction(GoalPhase phase, int occurrenceIndex)
    {
        var phaseName = phase.ToString().ToLowerInvariant();
        // Try indexed key first (e.g. "coding-2" for occurrence 2, "coding-1" for occurrence 1)
        var indexedKey = $"{phaseName}-{occurrenceIndex}";
        if (PhaseInstructions.TryGetValue(indexedKey, out var indexed))
            return indexed;
        // Fall back to bare key
        return PhaseInstructions.GetValueOrDefault(phaseName);
    }

    /// <summary>
    /// Creates a default plan with the standard phase order.
    /// Used as fallback when the Brain doesn't provide a plan.
    /// </summary>
    public static IterationPlan Default(bool includeImprove = false)
    {
        var phases = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.DocWriting, GoalPhase.Review };
        if (includeImprove) phases.Add(GoalPhase.Improve);
        phases.Add(GoalPhase.Merging);
        return new IterationPlan { Phases = phases, Reason = "Default plan" };
    }
}

/// <summary>
/// Records a single clarification Q&amp;A that occurred during goal execution.
/// </summary>
/// <param name="Timestamp">UTC time the clarification was created.</param>
/// <param name="GoalId">The goal this clarification belongs to.</param>
/// <param name="Iteration">Iteration number when the clarification was asked.</param>
/// <param name="Phase">Pipeline phase when the clarification was asked.</param>
/// <param name="WorkerRole">Worker role that triggered the question.</param>
/// <param name="Question">The question that was asked.</param>
/// <param name="Answer">The answer that was provided.</param>
/// <param name="AnsweredBy">Who answered: \"brain\", \"composer\", or \"human\".</param>
public sealed record ClarificationEntry(
    DateTime Timestamp,
    string GoalId,
    int Iteration,
    string Phase,
    string WorkerRole,
    string Question,
    string Answer,
    string AnsweredBy);

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

    /// <summary>The feature branch created by the coder for this goal.</summary>
    public string? CoderBranch { get; private set; }

    /// <summary>Accumulated output from each completed phase, keyed by "{role}-{iteration}".</summary>
    public ConcurrentDictionary<string, string> PhaseOutputs { get; } = new();

    /// <summary>
    /// Persisted agent session JSON blobs, keyed by role name (case-insensitive).
    /// Allows workers to resume mid-session after an orchestrator restart.
    /// </summary>
    public ConcurrentDictionary<string, string> RoleSessions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>In-memory iteration summaries for completed iterations (available to dashboard before goal finishes).</summary>
    public List<IterationSummary> CompletedIterationSummaries { get; } = [];

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

    /// <summary>Per-goal conversation history for the Brain.</summary>
    public List<ConversationEntry> Conversation { get; } = [];

    /// <summary>UTC timestamp when the current phase started (for phase duration tracking).</summary>
    public DateTime? PhaseStartedAt { get; private set; }

    /// <summary>UTC timestamp when the goal was started (captured at dispatch time, before the pipeline is created).</summary>
    public DateTime? GoalStartedAt { get; internal set; }

    /// <summary>UTC timestamp when this pipeline was created.</summary>
    public DateTime CreatedAt { get; private init; } = DateTime.UtcNow;
    /// <summary>UTC timestamp when this pipeline completed (Done or Failed), or <c>null</c> if still active.</summary>
    public DateTime? CompletedAt { get; private set; }
    /// <summary>SHA-1 hash of the merge commit produced when the feature branch was merged, or <c>null</c> if not yet merged.</summary>
    public string? MergeCommitHash { get; set; }

    /// <summary>
    /// HEAD SHA of the target repository at the moment the coder was dispatched for this iteration.
    /// Used to compute an iteration-scoped diff (<c>git diff {sha}..HEAD</c>) for reviewers so they
    /// can distinguish this iteration's changes from earlier iterations on the same branch.
    /// <c>null</c> when the SHA could not be captured (e.g. empty repository).
    /// </summary>
    public string? IterationStartSha { get; set; }

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
        CoderBranch = snapshot.CoderBranch;
        Plan = snapshot.Plan;
        CreatedAt = snapshot.CreatedAt;
        CompletedAt = snapshot.CompletedAt;
        GoalStartedAt = snapshot.GoalStartedAt;
        MergeCommitHash = snapshot.MergeCommitHash;
        IterationStartSha = snapshot.IterationStartSha;

        foreach (var (key, value) in snapshot.PhaseOutputs)
            PhaseOutputs[key] = value;

        foreach (var (key, value) in snapshot.RoleSessions)
            RoleSessions[key] = value;

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

        // Rebuild the state machine from the persisted plan so the dashboard
        // can correctly show completed / active / pending phases.
        if (Plan is not null)
            StateMachine.RestoreFromPlan(Plan.Phases, Phase);
    }

    /// <summary>Advance to the next phase, recording timing for the previous phase.</summary>
    public void AdvanceTo(GoalPhase phase)
    {
        lock (_lock)
        {
            // Record duration of the phase that just ended
            if (PhaseStartedAt.HasValue && Phase != GoalPhase.Planning)
            {
                var phaseName = Phase.ToString();
                var elapsed = DateTime.UtcNow - PhaseStartedAt.Value;
                // Accumulate: the same phase can repeat across retries
                if (Metrics.PhaseDurations.TryGetValue(phaseName, out var existing))
                    Metrics.PhaseDurations[phaseName] = existing + elapsed;
                else
                    Metrics.PhaseDurations[phaseName] = elapsed;
            }

            Phase = phase;
            PhaseStartedAt = DateTime.UtcNow;
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

    /// <summary>Set the iteration plan from the Brain, resetting to the first phase.</summary>
    public void SetPlan(IterationPlan plan)
    {
        lock (_lock)
        {
            plan.CurrentPhaseIndex = 0;
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

    /// <summary>Record output from a completed phase.</summary>
    public void RecordOutput(WorkerRole role, int iteration, string output)
    {
        PhaseOutputs[$"{role.ToRoleName()}-{iteration}"] = output;
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
        });
    }

    /// <summary>
    /// Returns the persisted session JSON for the given role, or <c>null</c> if no session has been stored.
    /// The lookup is case-insensitive.
    /// </summary>
    /// <param name="roleName">The name of the role whose session to retrieve.</param>
    /// <returns>The session JSON string, or <c>null</c> if not found.</returns>
    public string? GetRoleSession(string roleName) =>
        RoleSessions.TryGetValue(roleName, out var session) ? session : null;

    /// <summary>
    /// Stores the session JSON for the given role, overwriting any previously stored value.
    /// The key is treated case-insensitively.
    /// </summary>
    /// <param name="roleName">The name of the role whose session to store.</param>
    /// <param name="sessionJson">The serialised session JSON to persist.</param>
    public void SetRoleSession(string roleName, string sessionJson) =>
        RoleSessions[roleName] = sessionJson;

    /// <summary>Build a context summary for the Brain about this pipeline's current state.</summary>
    public string BuildContextSummary()
    {
        var parts = new List<string>
        {
            $"Goal: {Description}",
            $"Phase: {Phase}",
            $"Iteration: {Iteration}",
            $"Review retries: {ReviewRetries}/{MaxRetries}",
            $"Test retries: {TestRetries}/{MaxRetries}",
        };

        if (CoderBranch is not null)
            parts.Add($"Branch: {CoderBranch}");

        foreach (var (key, output) in PhaseOutputs)
        {
            var truncated = output.Length > 2000 ? output[..2000] + "..." : output;
            parts.Add($"\n=== Output from {key} ===\n{truncated}\n=== End output from {key} ===");
        }

        return string.Join("\n", parts);
    }

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
