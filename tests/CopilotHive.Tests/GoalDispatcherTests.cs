using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
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
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
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
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
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

    /// <summary>Optional commit message override; null means return null (use fallback).</summary>
    public string? CommitMessageOverride { get; set; }

    /// <summary>When true, <see cref="GenerateCommitMessageAsync"/> throws an exception.</summary>
    public bool ThrowOnGenerateCommitMessage { get; set; }

    /// <summary>Number of times <see cref="GenerateCommitMessageAsync"/> was called.</summary>
    public int GenerateCommitMessageCalls { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult($"Work on {pipeline.Description} as {phase}");

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        GenerateCommitMessageCalls++;
        if (ThrowOnGenerateCommitMessage)
            throw new InvalidOperationException("Simulated Brain failure in GenerateCommitMessageAsync");
        return Task.FromResult(CommitMessageOverride);
    }

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public BrainStats? GetStats() => null;
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

    /// <summary>
    /// BuildIterationSummary populates PhaseResult.WorkerOutput from pipeline.PhaseOutputs
    /// using the {role}-{iteration} key format.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_PopulatesPhaseResultWorkerOutput()
    {
        var goal = new Goal { Id = "output-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.Metrics.PhaseDurations["Coding"] = TimeSpan.FromSeconds(60);
        pipeline.Metrics.PhaseDurations["Testing"] = TimeSpan.FromSeconds(30);

        // RecordOutput uses "{role}-{iteration}" format (iteration = 1 by default)
        pipeline.RecordOutput(WorkerRole.Coder, 1, "Coder finished the task.");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "All 5 tests pass.");

        var summary = GoalDispatcher.BuildIterationSummary(pipeline, failedPhase: null);

        var codingPhase = summary.Phases.FirstOrDefault(p => p.Name == "Coding");
        var testingPhase = summary.Phases.FirstOrDefault(p => p.Name == "Testing");

        Assert.NotNull(codingPhase);
        Assert.Equal("Coder finished the task.", codingPhase.WorkerOutput);

        Assert.NotNull(testingPhase);
        Assert.Equal("All 5 tests pass.", testingPhase.WorkerOutput);
    }

    /// <summary>
    /// BuildIterationSummary populates IterationSummary.PhaseOutputs with entries
    /// keyed by {role}-{iteration} matching the current iteration.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_PopulatesPhaseOutputsDictionary()
    {
        var goal = new Goal { Id = "dict-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.Metrics.PhaseDurations["Coding"] = TimeSpan.FromSeconds(45);
        pipeline.RecordOutput(WorkerRole.Coder, 1, "Coder output.");

        var summary = GoalDispatcher.BuildIterationSummary(pipeline, failedPhase: null);

        Assert.True(summary.PhaseOutputs.ContainsKey("coder-1"));
        Assert.Equal("Coder output.", summary.PhaseOutputs["coder-1"]);
    }

    /// <summary>
    /// BuildIterationSummary does not include outputs from other iterations
    /// (e.g., outputs from iteration 2 are excluded when summarizing iteration 1).
    /// </summary>
    [Fact]
    public void BuildIterationSummary_ExcludesOutputsFromOtherIterations()
    {
        var goal = new Goal { Id = "multi-iter-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.Metrics.PhaseDurations["Coding"] = TimeSpan.FromSeconds(30);
        // Simulate outputs from multiple iterations coexisting in PhaseOutputs
        pipeline.RecordOutput(WorkerRole.Coder, 1, "Iteration 1 output.");
        pipeline.RecordOutput(WorkerRole.Coder, 2, "Iteration 2 output.");

        // Summary is built for iteration 1 (pipeline.Iteration = 1)
        var summary = GoalDispatcher.BuildIterationSummary(pipeline, failedPhase: null);

        Assert.True(summary.PhaseOutputs.ContainsKey("coder-1"));
        Assert.False(summary.PhaseOutputs.ContainsKey("coder-2"),
            "Outputs from a different iteration must not appear in the summary.");
    }
}

/// <summary>
/// Tests for <see cref="GoalDispatcher.BuildWorkerOutputSummary"/> logic.
/// </summary>
public sealed class GoalDispatcherBuildWorkerOutputSummaryTests
{
    [Fact]
    public void IncludesVerdictAndPhase()
    {
        var result = new TaskResult { TaskId = "t1", Status = TaskOutcome.Completed };
        var summary = GoalDispatcher.BuildWorkerOutputSummary(GoalPhase.Review, "REQUEST_CHANGES", result);

        Assert.Contains("Phase Review completed", summary);
        Assert.Contains("verdict: REQUEST_CHANGES", summary);
    }

    [Fact]
    public void IncludesReviewIssues()
    {
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            Metrics = new TaskMetrics
            {
                Verdict = "REQUEST_CHANGES",
                Issues = ["GetActiveTask called after MarkComplete", "Missing null check on branch name"],
            },
        };

        var summary = GoalDispatcher.BuildWorkerOutputSummary(GoalPhase.Review, "REQUEST_CHANGES", result);

        Assert.Contains("GetActiveTask called after MarkComplete", summary);
        Assert.Contains("Missing null check on branch name", summary);
        Assert.Contains("Issues found:", summary);
    }

    [Fact]
    public void IncludesTestMetrics()
    {
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            Metrics = new TaskMetrics
            {
                Verdict = "FAIL",
                TotalTests = 50,
                PassedTests = 47,
                FailedTests = 3,
            },
        };

        var summary = GoalDispatcher.BuildWorkerOutputSummary(GoalPhase.Testing, "FAIL", result);

        Assert.Contains("Tests: 47/50 passed, 3 failed", summary);
    }

    [Fact]
    public void IncludesGitStats()
    {
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            GitStatus = new GitChangeSummary { FilesChanged = 3, Insertions = 42, Deletions = 10 },
        };

        var summary = GoalDispatcher.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.Contains("Files changed: 3 (+42 -10)", summary);
    }

    [Fact]
    public void TruncatesLongOutput()
    {
        var longOutput = new string('x', 3000);
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            Output = longOutput,
        };

        var summary = GoalDispatcher.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.Contains("Worker output:", summary);
        Assert.Contains("...", summary);
        // Should be significantly shorter than 3000 chars of raw output
        Assert.True(summary.Length < 2000);
    }

    [Fact]
    public void SkipsEmptyOutput()
    {
        var result = new TaskResult { TaskId = "t1", Status = TaskOutcome.Completed, Output = "" };

        var summary = GoalDispatcher.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.DoesNotContain("Worker output:", summary);
    }
}

/// <summary>
/// Tests for GoalDispatcher startup logging.
/// </summary>
public sealed class GoalDispatcherStartupLogTests
{
    [Fact]
    public async Task ExecuteAsync_Startup_LogsGoalSourceCount()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var goal1 = new Goal { Id = "test-goal-1", Description = "Test 1" };
        var goal2 = new Goal { Id = "test-goal-2", Description = "Test 2" };
        var goalSource1 = new FakeGoalSource(goal1);
        var goalSource2 = new FakeGoalSource(goal2);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource1);
        goalManager.AddSource(goalSource2);

        var pipelineManager = new GoalPipelineManager();
        var notifier = new TaskCompletionNotifier();
        using var cts = new CancellationTokenSource();

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            notifier,
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        // Act - start the background service and cancel immediately after startup log
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, TestContext.Current.CancellationToken);
        var executeTask = dispatcher.StartAsync(linkedCts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken); // Allow startup logs to emit
        cts.Cancel();
        await Task.WhenAny(executeTask, Task.Delay(1000, TestContext.Current.CancellationToken));

        // Assert
        var startupLog = logger.Logs.FirstOrDefault(l =>
            l.Message.Contains("GoalDispatcher starting with") && l.Message.Contains("goal source"));

        Assert.True(startupLog != default, $"Expected startup log with goal source count. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");
        Assert.Contains("2 goal source(s)", startupLog.Message);
    }
}

/// <summary>
/// Tests that the dispatch log message includes the goal's Priority.
/// </summary>
public sealed class GoalDispatcherDispatchLoggingTests
{
    [Fact]
    public async Task DispatchNextGoalAsync_LogsGoalPriority()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var goal = new Goal { Id = "goal-priority-log-test", Description = "Priority logging test", Priority = GoalPriority.High };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var dispatcher = new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            startupDelay: TimeSpan.Zero);

        // Act - run the background service briefly so DispatchNextGoalAsync executes
        using var cts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, TestContext.Current.CancellationToken);
        var executeTask = dispatcher.StartAsync(linkedCts.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        cts.Cancel();
        await Task.WhenAny(executeTask, Task.Delay(1000, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains(logger.Logs, l => l.Message.Contains("High"));
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

/// <summary>
/// Tests for phase duration logging in GoalDispatcher.
/// </summary>
public sealed class GoalDispatcherPhaseDurationLoggingTests
{
    [Fact]
    public async Task DriveNextPhaseAsync_LogsPhaseDuration_WhenPhaseCompletes()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var brain = new FakeDispatcherBrain();
        var goal = new Goal { Id = "goal-duration-log-test", Description = "Test phase duration logging" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken); // Populate internal map

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Testing); // Use Testing phase to avoid no-op detection in Coding

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain);

        // Act
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Testing completed successfully.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(logger.Logs, l =>
            l.Message.Contains("completed in") &&
            l.Message.Contains(goal.Id) &&
            l.Message.Contains("Testing"));
    }
}

/// <summary>
/// Tests for model name appearing in GoalDispatcher log messages.
/// </summary>
public sealed class GoalDispatcherModelLoggingTests
{
    [Fact]
    public async Task HandleTaskCompletionAsync_LogsModelName_InTaskCompletedMessage()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var brain = new FakeDispatcherBrain();
        var taskQueue = new TaskQueue();
        var goal = new Goal { Id = "goal-model-log-test", Description = "Test model logging" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken); // Populate internal map

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain);

        // Act — Model is carried directly on the TaskResult (populated by HiveOrchestratorService)
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Work completed.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
            Model = "claude-sonnet-4-20250514",
        }, TestContext.Current.CancellationToken);

        // Assert - verify "model=claude-sonnet-4-20250514" appears in the task completed log
        var taskCompletedLog = logger.Logs.FirstOrDefault(l =>
            l.Message.Contains("task completed") &&
            l.Message.Contains(goal.Id));
        Assert.True(taskCompletedLog != default, $"Expected task completed log. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");
        Assert.Contains("model=claude-sonnet-4-20250514", taskCompletedLog.Message);
    }

    [Fact]
    public async Task HandleTaskCompletionAsync_LogsModelName_InPhaseCompletedMessage()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var brain = new FakeDispatcherBrain();
        var taskQueue = new TaskQueue();
        var goal = new Goal { Id = "goal-phase-model-test", Description = "Test phase model logging" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Testing);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain);

        // Act — Model is carried directly on the TaskResult
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Testing passed.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
            Model = "claude-sonnet-4-20250514",
        }, TestContext.Current.CancellationToken);

        // Assert - verify "model=claude-sonnet-4-20250514" appears in the phase completed log
        var phaseCompletedLog = logger.Logs.FirstOrDefault(l =>
            l.Message.Contains("completed in") &&
            l.Message.Contains(goal.Id) &&
            l.Message.Contains("Testing"));
        Assert.True(phaseCompletedLog != default, $"Expected phase completed log. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");
        Assert.Contains("model=claude-sonnet-4-20250514", phaseCompletedLog.Message);
    }

    [Fact]
    public async Task HandleTaskCompletionAsync_WhenModelIsEmpty_LogsUnknownModel()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var brain = new FakeDispatcherBrain();
        var taskQueue = new TaskQueue();
        var goal = new Goal { Id = "goal-unknown-model-test", Description = "Test unknown model logging" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain);

        // Act — Model defaults to "" when not set on TaskResult
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Work completed.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
        }, TestContext.Current.CancellationToken);

        // Assert - verify "model=unknown" appears when model is empty
        var taskCompletedLog = logger.Logs.FirstOrDefault(l =>
            l.Message.Contains("task completed") &&
            l.Message.Contains(goal.Id));
        Assert.True(taskCompletedLog != default, $"Expected task completed log. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");
        Assert.Contains("model=unknown", taskCompletedLog.Message);
    }
}

/// <summary>
/// Tests for <see cref="GoalDispatcher.BuildSquashCommitMessage"/> logic.
/// </summary>
public sealed class GoalDispatcherBuildSquashCommitMessageTests
{
    [Fact]
    public void BuildSquashCommitMessage_ShortDescription_ReturnsSingleLineMessage()
    {
        var result = GoalDispatcher.BuildSquashCommitMessage("goal-123", "Add logging support");

        Assert.Equal("Goal: goal-123 \u2014 Add logging support", result);
    }

    [Fact]
    public void BuildSquashCommitMessage_SubjectStartsWithGoalIdAndEmdash()
    {
        var result = GoalDispatcher.BuildSquashCommitMessage("abc-42", "Fix the bug");

        Assert.StartsWith("Goal: abc-42 \u2014", result);
    }

    [Fact]
    public void BuildSquashCommitMessage_LongDescription_TruncatesSubjectTo120Chars()
    {
        var longDescription = new string('x', 200);

        var result = GoalDispatcher.BuildSquashCommitMessage("goal-1", longDescription);

        var subjectLine = result.Split('\n')[0];
        Assert.True(subjectLine.Length <= 120,
            $"Subject line length {subjectLine.Length} exceeds 120 characters: {subjectLine}");
    }

    [Fact]
    public void BuildSquashCommitMessage_LongDescription_IncludesFullDescriptionInBody()
    {
        var longDescription = new string('x', 200);

        var result = GoalDispatcher.BuildSquashCommitMessage("goal-1", longDescription);

        Assert.Contains(longDescription.Trim(), result);
    }

    [Fact]
    public void BuildSquashCommitMessage_MultiLineDescription_UsesOnlyFirstLineInSubject()
    {
        var description = "First line summary\nSecond line details\nThird line more details";

        var result = GoalDispatcher.BuildSquashCommitMessage("goal-99", description);

        var subjectLine = result.Split('\n')[0];
        Assert.Contains("First line summary", subjectLine);
        Assert.DoesNotContain("Second line", subjectLine);
    }

    [Fact]
    public void BuildSquashCommitMessage_MultiLineDescription_IncludesFullBodyAfterBlankLine()
    {
        var description = "First line\nSecond line";

        var result = GoalDispatcher.BuildSquashCommitMessage("goal-5", description);

        // Subject + blank line + body
        Assert.Contains("\n\n", result);
        Assert.Contains("Second line", result);
    }

    [Fact]
    public void BuildSquashCommitMessage_ExactlyAtLimit_ReturnsSingleLineMessage()
    {
        // Build a description such that "Goal: id — {desc}" is exactly 120 chars
        var prefix = "Goal: id \u2014 ";
        var descLength = 120 - prefix.Length;
        var description = new string('a', descLength);

        var result = GoalDispatcher.BuildSquashCommitMessage("id", description);

        // Should be a single line (no body needed)
        Assert.Equal($"Goal: id \u2014 {description}", result);
        Assert.DoesNotContain("\n", result);
    }

    [Fact]
    public void BuildSquashCommitMessage_EmptyDescription_HandledGracefully()
    {
        var result = GoalDispatcher.BuildSquashCommitMessage("goal-0", "");

        Assert.StartsWith("Goal: goal-0 \u2014", result);
    }
}

/// <summary>
/// Tests for the Brain-generated commit message feature in <see cref="GoalDispatcher"/>
/// via <see cref="GoalDispatcher.GenerateMergeCommitMessageAsync"/> directly.
/// </summary>
public sealed class GoalDispatcherGenerateMergeCommitMessageTests
{
    /// <summary>
    /// When the Brain returns a non-null message, the result must start with
    /// the "Goal: {id} — " prefix and contain the Brain's message verbatim.
    /// </summary>
    [Fact]
    public async Task GenerateMergeCommitMessageAsync_BrainReturnsMessage_UsesPrefixAndContent()
    {
        // Arrange
        var goalId = "test-goal-123";
        var brainMessage = "feat: add user authentication";
        var expected = $"Goal: {goalId} \u2014 {brainMessage}";

        var brain = new FakeDispatcherBrain { CommitMessageOverride = brainMessage };
        var (dispatcher, pipeline) = CreateDispatcher(brain, goalId, "description");

        // Act
        var result = await dispatcher.GenerateMergeCommitMessageAsync(pipeline, TestContext.Current.CancellationToken);

        // Assert — verify ACTUAL output: prefix, brain content, and full equality
        Assert.StartsWith($"Goal: {goalId} \u2014 ", result);
        Assert.Contains(brainMessage, result);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// When the Brain returns null, the result must equal <see cref="GoalDispatcher.BuildSquashCommitMessage"/>.
    /// </summary>
    [Fact]
    public async Task GenerateMergeCommitMessageAsync_BrainReturnsNull_UsesFallback()
    {
        // Arrange
        var goalId = "test-goal-123";
        var description = "verbose goal description";

        var brain = new FakeDispatcherBrain { CommitMessageOverride = null };
        var (dispatcher, pipeline) = CreateDispatcher(brain, goalId, description);

        // Act
        var result = await dispatcher.GenerateMergeCommitMessageAsync(pipeline, TestContext.Current.CancellationToken);

        // Assert — output equals the static fallback
        var expected = GoalDispatcher.BuildSquashCommitMessage(goalId, description);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// When the Brain throws (non-cancellation), the result must equal <see cref="GoalDispatcher.BuildSquashCommitMessage"/>.
    /// </summary>
    [Fact]
    public async Task GenerateMergeCommitMessageAsync_BrainThrows_UsesFallback()
    {
        // Arrange
        var goalId = "test-goal-123";
        var description = "verbose goal description";

        var brain = new FakeDispatcherBrain { ThrowOnGenerateCommitMessage = true };
        var (dispatcher, pipeline) = CreateDispatcher(brain, goalId, description);

        // Act — must not throw; Brain exception is swallowed and fallback is returned
        var result = await dispatcher.GenerateMergeCommitMessageAsync(pipeline, TestContext.Current.CancellationToken);

        // Assert — output equals the static fallback
        var expected = GoalDispatcher.BuildSquashCommitMessage(goalId, description);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Builds a minimal <see cref="GoalDispatcher"/> and its matching <see cref="GoalPipeline"/>
    /// for direct unit-testing of <see cref="GoalDispatcher.GenerateMergeCommitMessageAsync"/>.
    /// </summary>
    private static (GoalDispatcher dispatcher, GoalPipeline pipeline) CreateDispatcher(
        IDistributedBrain brain, string goalId, string description)
    {
        var goal = new Goal { Id = goalId, Description = description };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain);

        return (dispatcher, pipeline);
    }
}

/// <summary>
/// Collecting logger for verifying log output in tests.
/// </summary>
file sealed class CollectingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Logs { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Logs.Add((logLevel, formatter(state, exception)));
    }
}
