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
    Planning,
    Coding,
    Review,
    Testing,
    Improve,
    Merging,
    Done,
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

    public string GoalId { get; }
    public string Description { get; }
    public Goal Goal { get; }

    public GoalPhase Phase { get; private set; } = GoalPhase.Planning;
    public int Iteration { get; private set; } = 1;
    public int ReviewRetries { get; private set; }
    public int TestRetries { get; private set; }
    public int MaxRetries { get; init; } = 3;

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

    public DateTime CreatedAt { get; private init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; private set; }

    public GoalPipeline(Goal goal, int maxRetries = 3)
    {
        Goal = goal;
        GoalId = goal.Id;
        Description = goal.Description;
        MaxRetries = maxRetries;
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

    /// <summary>Increment the iteration counter (used when coder re-runs after feedback).</summary>
    public void IncrementIteration()
    {
        lock (_lock)
        {
            Iteration++;
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
