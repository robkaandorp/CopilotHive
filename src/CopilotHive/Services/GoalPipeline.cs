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
    Testing,
    Review,
    Improve,
    Merging,
    Done,
    Failed,
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
            if (branch is not null)
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
