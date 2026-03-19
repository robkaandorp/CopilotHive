using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

public sealed class GoalDispatcherReviewVerdictTests
{
    // ── ReviewVerdict mapping ────────────────────────────────────────────

    [Fact]
    public async Task ReviewPhase_VerdictRequestChanges_SetsReviewVerdictRequestChanges()
    {
        var brain = new FakeDispatcherBrain();

        // maxRetries=0 so the FAIL-verdict retry path calls MarkGoalFailed instead of
        // re-dispatching to Coder, keeping the test self-contained.
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review, brain, maxRetries: 0);

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Several critical issues found.",
            Metrics = new TaskMetrics { Verdict = "REQUEST_CHANGES", Issues = { "critical issue" } },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ReviewVerdict.RequestChanges, pipeline.Metrics.ReviewVerdict);
    }

    [Fact]
    public async Task ReviewPhase_VerdictApprove_SetsReviewVerdictApprove()
    {
        var brain = new FakeDispatcherBrain();

        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review, brain);

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "LGTM, no issues found.",
            Metrics = new TaskMetrics { Verdict = "APPROVE" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ReviewVerdict.Approve, pipeline.Metrics.ReviewVerdict);
    }

    [Theory]
    [InlineData(GoalPhase.Coding, "PASS")]
    [InlineData(GoalPhase.Coding, "FAIL")]
    [InlineData(GoalPhase.Testing, "FAIL")]
    public async Task NonReviewPhase_AnyVerdict_ReviewVerdictRemainsEmpty(GoalPhase phase, string verdict)
    {
        var brain = new FakeDispatcherBrain();

        // maxRetries=0 prevents retry dispatching so the test stays self-contained.
        var (dispatcher, pipeline, taskId) = CreateDispatcher(phase, brain, maxRetries: 0);

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Worker output.",
            Metrics = new TaskMetrics { Verdict = verdict },
        }, TestContext.Current.CancellationToken);

        Assert.True(
            pipeline.Metrics.ReviewVerdict is null,
            $"Expected ReviewVerdict to be null for phase {phase} with verdict {verdict}, " +
            $"but was: '{pipeline.Metrics.ReviewVerdict}'");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal self-contained <see cref="GoalDispatcher"/> for testing the
    /// ReviewVerdict population logic in <c>DriveNextPhaseAsync</c>.
    /// </summary>
    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string taskId)
        CreateDispatcher(GoalPhase phase, IDistributedBrain brain, int maxRetries = 3)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        // Populate the internal goal→source map so UpdateGoalStatusAsync doesn't throw.
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries);
        pipeline.AdvanceTo(phase);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var notifier = new TaskCompletionNotifier();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            notifier,
            NullLogger<GoalDispatcher>.Instance,
            brain);

        return (dispatcher, pipeline, taskId);
    }
}

/// <summary>
/// Tests for <see cref="GoalDispatcher.ResolveRepositories"/> fail-fast behavior.
/// </summary>
public sealed class GoalDispatcherResolveRepositoriesTests
{
    [Fact]
    public void ResolveRepositories_AllValidNames_ReturnsAllRepositories()
    {
        var dispatcher = CreateDispatcher(
        [
            new RepositoryConfig { Name = "RepoA", Url = "https://github.com/org/repo-a" },
            new RepositoryConfig { Name = "RepoB", Url = "https://github.com/org/repo-b" },
        ]);
        var goal = new Goal { Id = "goal-1", Description = "Test", RepositoryNames = ["RepoA", "RepoB"] };

        var result = dispatcher.ResolveRepositories(goal);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Name == "RepoA");
        Assert.Contains(result, r => r.Name == "RepoB");
    }

    [Fact]
    public void ResolveRepositories_UnknownName_ThrowsInvalidOperationException()
    {
        var dispatcher = CreateDispatcher(
        [
            new RepositoryConfig { Name = "RepoA", Url = "https://github.com/org/repo-a" },
        ]);
        var goal = new Goal { Id = "goal-2", Description = "Test", RepositoryNames = ["unknown-repo"] };

        Assert.Throws<InvalidOperationException>(() => dispatcher.ResolveRepositories(goal));
    }

    [Fact]
    public void ResolveRepositories_ExceptionMessage_IncludesGoalIdAndRepoName()
    {
        var dispatcher = CreateDispatcher(
        [
            new RepositoryConfig { Name = "RepoA", Url = "https://github.com/org/repo-a" },
        ]);
        var goal = new Goal { Id = "goal-42", Description = "Test", RepositoryNames = ["missing-repo"] };

        var ex = Assert.Throws<InvalidOperationException>(() => dispatcher.ResolveRepositories(goal));

        Assert.Contains("goal-42", ex.Message);
        Assert.Contains("missing-repo", ex.Message);
    }

    [Fact]
    public void ResolveRepositories_MixOfValidAndInvalidRepos_FailsWithoutPartialResults()
    {
        var dispatcher = CreateDispatcher(
        [
            new RepositoryConfig { Name = "RepoA", Url = "https://github.com/org/repo-a" },
        ]);
        var goal = new Goal { Id = "goal-3", Description = "Test", RepositoryNames = ["RepoA", "bad-repo"] };

        Assert.Throws<InvalidOperationException>(() => dispatcher.ResolveRepositories(goal));
    }

    private static GoalDispatcher CreateDispatcher(List<RepositoryConfig> repos)
    {
        var goal = new Goal { Id = "setup-goal", Description = "Setup" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var config = new HiveConfigFile { Repositories = repos };

        return new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            config: config);
    }
}

/// <summary>
/// Minimal <see cref="IDistributedBrain"/> stub for GoalDispatcher tests.
/// </summary>
file sealed class FakeDispatcherBrain : IDistributedBrain
{
    /// <summary>Verdict to return when a worker completes (used by the test harness).</summary>
    public string Verdict { get; set; } = "PASS";

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult($"Work on {pipeline.Description} as {phase}");
}

/// <summary>
/// Tests for <see cref="GoalDispatcher.BuildIterationSummary"/> logic.
/// </summary>
public sealed class GoalDispatcherBuildIterationSummaryTests
{
    /// <summary>
    /// When <see cref="CopilotHive.Metrics.IterationMetrics.ImproverSkipped"/> is true AND PhaseDurations already
    /// contains an "Improve" entry, the output must contain exactly one "Improve" phase result
    /// with result "skip" (no duplicate entries).
    /// </summary>
    [Fact]
    public void BuildIterationSummary_ImproverSkipped_WithImproveInPhaseDurations_ExactlyOneSkipEntry()
    {
        var goal = new Goal { Id = "test-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.Metrics.PhaseDurations["Coding"]  = TimeSpan.FromSeconds(60);
        pipeline.Metrics.PhaseDurations["Improve"]  = TimeSpan.FromSeconds(5);
        pipeline.Metrics.PhaseDurations["Testing"]  = TimeSpan.FromSeconds(30);
        pipeline.Metrics.ImproverSkipped            = true;
        pipeline.Metrics.ImproverSkipReason         = "Brain timeout";

        var summary = GoalDispatcher.BuildIterationSummary(pipeline, failedPhase: null);

        var improvePhases = summary.Phases.Where(p => p.Name == "Improve").ToList();
        Assert.Single(improvePhases);
        Assert.Equal("skip", improvePhases[0].Result);
    }

    /// <summary>
    /// When <see cref="CopilotHive.Metrics.IterationMetrics.ImproverSkipped"/> is true and PhaseDurations does NOT
    /// contain an "Improve" entry, a single "skip" entry is still produced.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_ImproverSkipped_WithoutImproveInPhaseDurations_SingleSkipEntry()
    {
        var goal = new Goal { Id = "test-goal-2", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.Metrics.PhaseDurations["Coding"]  = TimeSpan.FromSeconds(60);
        pipeline.Metrics.ImproverSkipped            = true;

        var summary = GoalDispatcher.BuildIterationSummary(pipeline, failedPhase: null);

        var improvePhases = summary.Phases.Where(p => p.Name == "Improve").ToList();
        Assert.Single(improvePhases);
        Assert.Equal("skip", improvePhases[0].Result);
    }
}

/// <summary>
/// Minimal <see cref="IGoalSource"/> that returns a single pre-configured goal.
/// </summary>
file sealed class FakeGoalSource : IGoalSource
{
    private readonly Goal _goal;

    public FakeGoalSource(Goal goal) => _goal = goal;

    public string Name => "fake";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;
}
