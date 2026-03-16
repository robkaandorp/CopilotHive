using System.Collections.Concurrent;
using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;

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

    /// <summary>Per-phase instructions/context from the Brain.</summary>
    public Dictionary<GoalPhase, string> PhaseInstructions { get; init; } = [];

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

    /// <summary>
    /// Creates a default plan with the standard phase order.
    /// Used as fallback when the Brain doesn't provide a plan.
    /// </summary>
    public static IterationPlan Default(bool includeImprove = false)
    {
        var phases = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Review, GoalPhase.Testing };
        if (includeImprove) phases.Add(GoalPhase.Improve);
        phases.Add(GoalPhase.Merging);
        return new IterationPlan { Phases = phases, Reason = "Default plan" };
    }
}

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

    /// <summary>Current phase the pipeline is executing.</summary>
    public GoalPhase Phase { get; private set; } = GoalPhase.Planning;
    /// <summary>One-based iteration counter; increments each time the pipeline loops.</summary>
    public int Iteration { get; private set; } = 1;
    /// <summary>Number of times the review phase has been retried in the current iteration.</summary>
    public int ReviewRetries { get; private set; }
    /// <summary>Number of times the test phase has been retried in the current iteration.</summary>
    public int TestRetries { get; private set; }
    /// <summary>Number of times the improver has been retried due to agents.md size violations.</summary>
    public int ImproverRetries { get; private set; }
    /// <summary>Maximum number of task-level retries allowed.</summary>
    public int MaxRetries { get; init; } = Constants.DefaultMaxRetriesPerTask;
    /// <summary>Maximum number of iterations allowed before the goal is failed.</summary>
    public int MaxIterations { get; init; } = Constants.DefaultMaxIterations;

    /// <summary>Brain-determined plan for the current iteration, or null if no plan set.</summary>
    public IterationPlan? Plan { get; private set; }

    /// <summary>The active task ID currently assigned to a worker (null when idle).</summary>
    public string? ActiveTaskId { get; private set; }

    /// <summary>The feature branch created by the coder for this goal.</summary>
    public string? CoderBranch { get; private set; }

    /// <summary>Accumulated output from each completed phase, keyed by "{role}-{iteration}".</summary>
    public ConcurrentDictionary<string, string> PhaseOutputs { get; } = new();

    /// <summary>Metrics extracted by the Brain from worker output.</summary>
    public IterationMetrics Metrics { get; } = new() { Iteration = 1 };

    /// <summary>Per-goal conversation history for the Brain.</summary>
    public List<ConversationEntry> Conversation { get; } = [];

    /// <summary>UTC timestamp when the current phase started (for phase duration tracking).</summary>
    public DateTime? PhaseStartedAt { get; private set; }

    /// <summary>UTC timestamp when this pipeline was created.</summary>
    public DateTime CreatedAt { get; private init; } = DateTime.UtcNow;
    /// <summary>UTC timestamp when this pipeline completed (Done or Failed), or <c>null</c> if still active.</summary>
    public DateTime? CompletedAt { get; private set; }

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
        MaxRetries = maxRetries;
        MaxIterations = maxIterations;
    }

    /// <summary>Restore a pipeline from a persisted snapshot.</summary>
    internal GoalPipeline(PipelineSnapshot snapshot)
    {
        Goal = snapshot.Goal;
        GoalId = snapshot.GoalId;
        Description = snapshot.Description;
        Phase = snapshot.Phase;
        Iteration = snapshot.Iteration;
        ReviewRetries = snapshot.ReviewRetries;
        TestRetries = snapshot.TestRetries;
        MaxRetries = snapshot.MaxRetries;
        MaxIterations = snapshot.MaxIterations;
        ActiveTaskId = snapshot.ActiveTaskId;
        CoderBranch = snapshot.CoderBranch;
        Plan = snapshot.Plan;
        CreatedAt = snapshot.CreatedAt;
        CompletedAt = snapshot.CompletedAt;

        foreach (var (key, value) in snapshot.PhaseOutputs)
            PhaseOutputs[key] = value;

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
    public void RecordOutput(string role, int iteration, string output)
    {
        PhaseOutputs[$"{role}-{iteration}"] = output;
    }

    /// <summary>Increment the review retry counter. Returns true if retries remain.</summary>
    public bool IncrementReviewRetry()
    {
        lock (_lock)
        {
            ReviewRetries++;
            return ReviewRetries < MaxRetries;
        }
    }

    /// <summary>Increment the test retry counter. Returns true if retries remain.</summary>
    public bool IncrementTestRetry()
    {
        lock (_lock)
        {
            TestRetries++;
            return TestRetries < MaxRetries;
        }
    }

    /// <summary>Increment the improver retry counter. Returns true if retries remain.</summary>
    public bool IncrementImproverRetry()
    {
        lock (_lock)
        {
            ImproverRetries++;
            return ImproverRetries < MaxRetries;
        }
    }

    /// <summary>
    /// Increment the iteration counter. Returns <c>false</c> when the maximum has been reached.
    /// </summary>
    public bool IncrementIteration()
    {
        lock (_lock)
        {
            Iteration++;
            return Iteration <= MaxIterations;
        }
    }

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
            parts.Add($"\n--- Output from {key} ---\n{truncated}");
        }

        return string.Join("\n", parts);
    }
}
