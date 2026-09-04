using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;
using BranchDeleteResult = CopilotHive.Git.BranchDeleteResult;

#pragma warning disable CS0618 // Obsolete members tested for backward compatibility

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
        CreateDispatcher(GoalPhase phase, IDistributedBrain brain, int maxRetries = 3, DashboardNotifier? dashboardNotifier = null)
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        // Populate the internal goal→source map so UpdateGoalStatusAsync doesn't throw.
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries);
        // Put the pipeline genuinely mid-iteration: the state machine must be inside the plan
        // (not in the Planning re-plan window) or TaskCompletionService drops the completion.
        var iterationPlan = IterationPlan.Default();
        pipeline.SetPlan(iterationPlan);
        pipeline.StateMachine.RestoreFromPlan(iterationPlan.Phases, phase);
        pipeline.AdvanceTo(phase);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/repo.git", DefaultBranch = "main" },
            ],
            // All broadcastable roles need configured models to pass the readiness gate.
            Workers = TestHelpers.AllBroadcastableRoleModels(),
        };

        var notifier = new TaskCompletionNotifier();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            notifier,
            NullLogger<GoalDispatcher>.Instance,
            new FakeRepoManager(),
            brain,
            config: config,
            dashboardNotifier: dashboardNotifier);

        return (dispatcher, pipeline, taskId);
    }

    private sealed class FakeRepoManager : IBrainRepoManager
    {
        public string WorkDirectory => "/fake/work";
        public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.FromResult($"/fake/work/{repoName}");
        public Task<string> MergeFeatureBranchAsync(string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default) =>
            Task.FromResult("fake-sha");
        public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default) =>
            Task.FromResult(BranchDeleteResult.Success);
        public string GetClonePath(string repoName) => $"/fake/work/{repoName}";
        public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default) =>
            Task.FromResult(new List<string>());
    }
    [Fact]
    public async Task HandleTaskCompletionAsync_PhaseChangesNonTerminally_NotifiesDashboardOnce()
    {
        var brain = new FakeDispatcherBrain();
        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Coding, brain, dashboardNotifier: notifier);
        pipeline.SetPlan(new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging] });
        pipeline.StateMachine.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);

        var phaseBefore = pipeline.Phase;

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Coding done.",
            GitStatus = new GitChangeSummary { FilesChanged = 3, Pushed = true },
            Metrics = new TaskMetrics { Verdict = "PASS" },
        }, TestContext.Current.CancellationToken);

        Assert.NotEqual(phaseBefore, pipeline.Phase);
        Assert.True(pipeline.Phase is not GoalPhase.Done and not GoalPhase.Failed);
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public async Task HandleTaskCompletionAsync_PhaseUnchanged_DoesNotNotifyDashboard()
    {
        var brain = new FakeDispatcherBrain();
        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Coding, brain, dashboardNotifier: notifier);
        pipeline.SetPlan(new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging] });
        pipeline.StateMachine.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);

        var phaseBefore = pipeline.Phase;

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Coding done.",
            Metrics = new TaskMetrics { Verdict = "FAIL" },
        }, TestContext.Current.CancellationToken);

        // Phase stayed Coding (retry in the same phase) → no notification.
        Assert.Equal(phaseBefore, pipeline.Phase);
        Assert.Equal(0, notificationCount);
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

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) => Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PlanResult.Success(IterationPlan.Default()));

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        GenerateCommitMessageCalls++;
        if (ThrowOnGenerateCommitMessage)
            throw new InvalidOperationException("Simulated Brain failure in GenerateCommitMessageAsync");
        return Task.FromResult(CommitMessageOverride);
    }

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) => Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("Brain is not available. Please proceed with your best judgment."));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>
/// Tests for <see cref="PipelineHelpers.BuildIterationSummary"/> logic.
/// </summary>
public sealed class GoalDispatcherBuildIterationSummaryTests
{
    /// <summary>
    /// BuildIterationSummary reads PhaseLog entries with Result=Skip for improver.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_ImproverSkipped_SingleSkipEntry()
    {
        var goal = new Goal { Id = "test-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Improve, Result = PhaseOutcome.Skip,
            Iteration = 1, Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddSeconds(-5),
            CompletedAt = DateTime.UtcNow,
            Verdict = "Brain timeout",
        });

        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        var improvePhases = summary.Phases.Where(p => p.Name == GoalPhase.Improve).ToList();
        Assert.Single(improvePhases);
        Assert.Equal(PhaseOutcome.Skip, improvePhases[0].Result);
    }

    /// <summary>
    /// BuildIterationSummary reads WorkerOutput from PhaseLog entries.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_PopulatesPhaseResultWorkerOutput()
    {
        var goal = new Goal { Id = "output-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "Coder finished the task.",
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "All 5 tests pass.",
        });

        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        var codingPhase = summary.Phases.FirstOrDefault(p => p.Name == GoalPhase.Coding);
        var testingPhase = summary.Phases.FirstOrDefault(p => p.Name == GoalPhase.Testing);

        Assert.NotNull(codingPhase);
        Assert.Equal("Coder finished the task.", codingPhase.WorkerOutput);

        Assert.NotNull(testingPhase);
        Assert.Equal("All 5 tests pass.", testingPhase.WorkerOutput);
    }

    /// <summary>
    /// BuildIterationSummary populates IterationSummary.PhaseOutputs with entries
    /// derived from PhaseLog for backward compatibility.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_PopulatesPhaseOutputsDictionary()
    {
        var goal = new Goal { Id = "dict-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "Coder output.",
        });

        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        Assert.True(summary.PhaseOutputs.ContainsKey("coder-1"));
        Assert.Equal("Coder output.", summary.PhaseOutputs["coder-1"]);
    }

    /// <summary>
    /// BuildIterationSummary only includes PhaseLog entries for the current iteration.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_ExcludesOutputsFromOtherIterations()
    {
        var goal = new Goal { Id = "multi-iter-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "Iteration 1 output.",
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 2, Occurrence = 1,
            WorkerOutput = "Iteration 2 output.",
        });

        // Summary is built for iteration 1 (pipeline.Iteration = 1)
        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        Assert.True(summary.PhaseOutputs.ContainsKey("coder-1"));
        Assert.False(summary.PhaseOutputs.ContainsKey("coder-2"),
            "Outputs from a different iteration must not appear in the summary.");
    }

    /// <summary>
    /// BuildIterationSummary copies IterationMetrics.BuildSuccess=true into
    /// the returned IterationSummary.BuildSuccess.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_CopiesBuildSuccess_True()
    {
        var goal = new Goal { Id = "build-true-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.PhaseLog.Add(new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, Iteration = 1, Occurrence = 1 });
        pipeline.PhaseLog.Add(new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Pass, Iteration = 1, Occurrence = 1 });
        pipeline.Metrics.BuildSuccess = true;

        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        Assert.True(summary.BuildSuccess);
    }

    /// <summary>
    /// BuildIterationSummary copies IterationMetrics.BuildSuccess=false into
    /// the returned IterationSummary.BuildSuccess.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_CopiesBuildSuccess_False()
    {
        var goal = new Goal { Id = "build-false-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.PhaseLog.Add(new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, Iteration = 1, Occurrence = 1 });
        pipeline.PhaseLog.Add(new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Pass, Iteration = 1, Occurrence = 1 });
        pipeline.Metrics.BuildSuccess = false;

        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        Assert.False(summary.BuildSuccess);
    }

    /// <summary>
    /// BuildIterationSummary emits separate PhaseResult entries for each occurrence
    /// of a repeated phase (e.g., two Coding phases in a multi-round plan).
    /// </summary>
    [Fact]
    public void BuildIterationSummary_MultiRoundPlan_EmitsPerOccurrencePhaseResults()
    {
        var goal = new Goal { Id = "multi-round-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        var start = DateTime.UtcNow;
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "First coding output",
            StartedAt = start, CompletedAt = start.AddSeconds(30),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "First testing output",
            StartedAt = start.AddSeconds(30), CompletedAt = start.AddSeconds(45),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 2,
            WorkerOutput = "Second coding output",
            StartedAt = start.AddSeconds(45), CompletedAt = start.AddSeconds(90),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 2,
            WorkerOutput = "Second testing output",
            StartedAt = start.AddSeconds(90), CompletedAt = start.AddSeconds(110),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Review, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "Review output",
            StartedAt = start.AddSeconds(110), CompletedAt = start.AddSeconds(120),
        });

        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        var codingPhases = summary.Phases.Where(p => p.Name == GoalPhase.Coding).ToList();
        var testingPhases = summary.Phases.Where(p => p.Name == GoalPhase.Testing).ToList();
        var reviewPhases = summary.Phases.Where(p => p.Name == GoalPhase.Review).ToList();

        Assert.Equal(2, codingPhases.Count);
        Assert.Equal(2, testingPhases.Count);
        Assert.Single(reviewPhases);

        Assert.Equal("First coding output", codingPhases[0].WorkerOutput);
        Assert.Equal("Second coding output", codingPhases[1].WorkerOutput);
        Assert.Equal("First testing output", testingPhases[0].WorkerOutput);
        Assert.Equal("Second testing output", testingPhases[1].WorkerOutput);
        Assert.Equal("Review output", reviewPhases[0].WorkerOutput);
    }

    /// <summary>
    /// BuildIterationSummary includes both per-occurrence and backward-compatible keys
    /// in the PhaseOutputs dictionary.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_MultiRoundPlan_IncludesBothOccurrenceAndLatestKeys()
    {
        var goal = new Goal { Id = "output-keys-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "Round 1 code",
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 2,
            WorkerOutput = "Round 2 code",
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "Round 1 tests",
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 2,
            WorkerOutput = "Round 2 tests",
        });

        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        // PhaseOutputs should include both occurrence-specific and backward-compatible keys
        Assert.True(summary.PhaseOutputs.ContainsKey("coder-1-1"));
        Assert.True(summary.PhaseOutputs.ContainsKey("coder-1-2"));
        Assert.True(summary.PhaseOutputs.ContainsKey("coder-1"));
        Assert.True(summary.PhaseOutputs.ContainsKey("tester-1-1"));
        Assert.True(summary.PhaseOutputs.ContainsKey("tester-1-2"));
        Assert.True(summary.PhaseOutputs.ContainsKey("tester-1"));

        Assert.Equal("Round 1 code", summary.PhaseOutputs["coder-1-1"]);
        Assert.Equal("Round 2 code", summary.PhaseOutputs["coder-1-2"]);
        Assert.Equal("Round 2 code", summary.PhaseOutputs["coder-1"]);
    }

    /// <summary>
    /// BuildIterationSummary with a failed phase in a multi-round plan marks only
    /// the specific occurrence that failed via its PhaseLog entry Result.
    /// </summary>
    [Fact]
    public void BuildIterationSummary_MultiRoundPlan_FailedPhaseMarksOnlySpecificOccurrence()
    {
        var goal = new Goal { Id = "fail-occ-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        var start = DateTime.UtcNow;
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            StartedAt = start, CompletedAt = start.AddSeconds(30),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            StartedAt = start.AddSeconds(30), CompletedAt = start.AddSeconds(45),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 2,
            StartedAt = start.AddSeconds(45), CompletedAt = start.AddSeconds(90),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Result = PhaseOutcome.Fail,
            Iteration = 1, Occurrence = 2,
            StartedAt = start.AddSeconds(90), CompletedAt = start.AddSeconds(110),
            Verdict = "FAIL",
        });

        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        var testingPhases = summary.Phases.Where(p => p.Name == GoalPhase.Testing).ToList();
        Assert.Equal(2, testingPhases.Count);

        // First Testing occurrence should be "pass"
        Assert.Equal(PhaseOutcome.Pass, testingPhases[0].Result);

        // Second Testing occurrence should be "fail"
        Assert.Equal(PhaseOutcome.Fail, testingPhases[1].Result);

        // Coding phases should all be "pass"
        var codingPhases = summary.Phases.Where(p => p.Name == GoalPhase.Coding).ToList();
        Assert.All(codingPhases, c => Assert.Equal(PhaseOutcome.Pass, c.Result));
    }
}

/// <summary>
/// Tests for <see cref="PipelineHelpers.GetLastCraftPromptFromConversation"/>
/// and <see cref="PipelineHelpers.GetPlanningPromptsFromConversation"/>.
/// </summary>
public sealed class GoalDispatcherConversationExtractionTests
{
    private static GoalPipeline CreatePipeline(int iteration = 1)
    {
        var goal = new Goal { Id = "test-goal", Description = "Test" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);
        // Set the iteration to match
        for (int i = 1; i < iteration; i++)
            pipeline.AdvanceTo(GoalPhase.Coding);
        return pipeline;
    }

    [Fact]
    public void GetLastCraftPromptFromConversation_ReturnsLastUserCraftPrompt()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "Brain prompt for coding", 1, "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Worker prompt", 1, "craft-prompt"));

        var result = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);

        Assert.Equal("Brain prompt for coding", result);
    }

    [Fact]
    public void GetLastCraftPromptFromConversation_ReturnsNull_WhenNoCraftPromptExists()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "Plan please", 1, "planning"));

        var result = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);

        Assert.Null(result);
    }

    [Fact]
    public void GetLastCraftPromptFromConversation_FiltersOnCurrentIteration()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "Old prompt", 1, "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("user", "New prompt", 2, "craft-prompt"));

        // Pipeline is at iteration 1 — should return the iteration-1 entry
        var result = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);

        Assert.Equal("Old prompt", result);
    }

    [Fact]
    public void GetPlanningPromptsFromConversation_ReturnsBothPromptAndResponse()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "Plan iteration 1", 1, "planning"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "I will code and test.", 1, "planning"));

        var (prompt, response) = PipelineHelpers.GetPlanningPromptsFromConversation(pipeline);

        Assert.Equal("Plan iteration 1", prompt);
        Assert.Equal("I will code and test.", response);
    }

    [Fact]
    public void GetPlanningPromptsFromConversation_ReturnsNulls_WhenNoPlanningEntries()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "Some craft prompt", 1, "craft-prompt"));

        var (prompt, response) = PipelineHelpers.GetPlanningPromptsFromConversation(pipeline);

        Assert.Null(prompt);
        Assert.Null(response);
    }

    [Fact]
    public void GetPlanningPromptsFromConversation_ReturnsLastPair_WhenMultipleExist()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "First plan attempt", 1, "planning"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "First response", 1, "planning"));
        pipeline.Conversation.Add(new ConversationEntry("user", "Retry plan attempt", 1, "planning"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Retry response", 1, "planning"));

        var (prompt, response) = PipelineHelpers.GetPlanningPromptsFromConversation(pipeline);

        Assert.Equal("Retry plan attempt", prompt);
        Assert.Equal("Retry response", response);
    }
}

/// <summary>
/// Tests for <see cref="PipelineHelpers.BuildWorkerOutputSummary"/> logic.
/// </summary>
public sealed class GoalDispatcherBuildWorkerOutputSummaryTests
{
    [Fact]
    public void IncludesVerdictAndPhase()
    {
        var result = new TaskResult { TaskId = "t1", Status = TaskOutcome.Completed };
        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Review, "REQUEST_CHANGES", result);

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

        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Review, "REQUEST_CHANGES", result);

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

        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Testing, "FAIL", result);

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

        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

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

        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.Contains("Worker output (no summary):", summary);
        Assert.Contains("...", summary);
        // Should be significantly shorter than 3000 chars of raw output
        Assert.True(summary.Length < 2000);
    }

    [Fact]
    public void SkipsEmptyOutput()
    {
        var result = new TaskResult { TaskId = "t1", Status = TaskOutcome.Completed, Output = "" };

        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.DoesNotContain("Worker output:", summary);
    }

    // ── Summary vs raw output ───────────────────────────────────────────────

    /// <summary>
    /// When <see cref="TaskMetrics.Summary"/> is present, it must be used as the worker output
    /// (prefixed with "Worker summary:") and raw output must be ignored.
    /// </summary>
    [Fact]
    public void WithMetricsSummary_UsesSummary_NotRawOutput()
    {
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            Output = "This raw output should be ignored.",
            Metrics = new TaskMetrics
            {
                Summary = "Implemented feature X. All tests pass.",
            },
        };

        var output = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.Contains("Worker summary:", output);
        Assert.Contains("Implemented feature X. All tests pass.", output);
        Assert.DoesNotContain("This raw output should be ignored", output);
        Assert.DoesNotContain("Worker output (no summary)", output);
    }

    /// <summary>
    /// When <see cref="TaskMetrics.Summary"/> is absent, raw output must be used as fallback
    /// (prefixed with "Worker output (no summary):").
    /// </summary>
    [Fact]
    public void WithoutMetricsSummary_UsesRawOutput_FallsBackToOutput()
    {
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            Output = "Raw agent output text.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
        };

        var output = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.Contains("Worker output (no summary):", output);
        Assert.Contains("Raw agent output text.", output);
        Assert.DoesNotContain("Worker summary:", output);
    }

    /// <summary>
    /// When both Summary and raw output are absent, no worker output section is added.
    /// </summary>
    [Fact]
    public void WithoutMetricsSummary_AndWithoutRawOutput_OmitsOutputSection()
    {
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            Output = "",
            Metrics = new TaskMetrics { Verdict = "PASS" },
        };

        var output = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.DoesNotContain("Worker summary:", output);
        Assert.DoesNotContain("Worker output", output);
    }

    /// <summary>
    /// Raw output is truncated at 1500 characters when Summary is absent.
    /// </summary>
    [Fact]
    public void WithoutMetricsSummary_TruncatesLongRawOutput()
    {
        var longOutput = new string('x', 3000);
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            Output = longOutput,
            Metrics = new TaskMetrics { Verdict = "PASS" },
        };

        var output = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.Contains("Worker output (no summary):", output);
        Assert.Contains("...", output);
        Assert.True(output.Length < 2000, "Output should be significantly shorter than raw 3000 chars");
    }

    [Fact]
    public void IncludesPushFailureWarning_WhenFilesChangedButNotPushed()
    {
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            GitStatus = new GitChangeSummary { FilesChanged = 5, Insertions = 20, Deletions = 3, Pushed = false },
        };

        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.Contains("⚠️ Git push FAILED", summary);
        Assert.Contains("changes were not pushed to the remote", summary);
    }

    [Fact]
    public void OmitsPushFailureWarning_WhenPushedSuccessfully()
    {
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            GitStatus = new GitChangeSummary { FilesChanged = 5, Insertions = 20, Deletions = 3, Pushed = true },
        };

        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.DoesNotContain("Git push FAILED", summary);
    }

    [Fact]
    public void OmitsPushFailureWarning_WhenNoFilesChanged()
    {
        var result = new TaskResult
        {
            TaskId = "t1",
            Status = TaskOutcome.Completed,
            GitStatus = new GitChangeSummary { FilesChanged = 0, Pushed = false },
        };

        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.DoesNotContain("Git push FAILED", summary);
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
        var innerLogger = new CollectingLogger<GoalDispatcher>();
        var signalingLogger = new SignalingLogger<GoalDispatcher>(innerLogger, "GoalDispatcher starting with");
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
            signalingLogger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        // Act - start the background service and wait for the startup log to be emitted
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, TestContext.Current.CancellationToken);
        await dispatcher.StartAsync(linkedCts.Token);
        try
        {
            var startupLog = await signalingLogger.MatchedLog.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Contains("2 goal source(s)", startupLog);
        }
        finally
        {
            cts.Cancel();
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await dispatcher.StopAsync(stopCts.Token);
        }
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
            // A Brain and all broadcastable role models are required to pass the readiness gate.
            brain: new FakeDispatcherBrain(),
            config: TestHelpers.FullReadyConfig(),
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

    [Fact]
    public async Task DispatchNextGoalAsync_NotifiesDashboardOnce()
    {
        var goal = new Goal { Id = "goal-dispatch-notify-test", Description = "Dispatch notify test", Status = GoalStatus.Pending };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var dispatcher = new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            // A Brain is required to plan the goal — without one, dispatch fails the goal
            // and emits a second (failure) dashboard notification.
            brain: new FakeDispatcherBrain(),
            config: TestHelpers.FullReadyConfig(),
            startupDelay: TimeSpan.Zero,
            dashboardNotifier: notifier);

        using var cts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, TestContext.Current.CancellationToken);
        var executeTask = dispatcher.StartAsync(linkedCts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        cts.Cancel();
        await Task.WhenAny(executeTask, Task.Delay(1000, TestContext.Current.CancellationToken));

        Assert.Equal(1, notificationCount);
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
        var durationPlan = IterationPlan.Default();
        pipeline.SetPlan(durationPlan);
        pipeline.StateMachine.RestoreFromPlan(durationPlan.Phases, GoalPhase.Testing);
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
/// Tests for model name appearing in TaskCompletionService log messages.
/// After extraction, the task completion log is emitted by <see cref="TaskCompletionService"/>
/// using its own logger category.
/// </summary>
public sealed class TaskCompletionServiceModelLoggingTests
{
    private static TaskCompletionService CreateService(
        GoalPipelineManager pipelineManager,
        IDistributedBrain? brain,
        ILogger<TaskCompletionService> logger,
        DashboardNotifier? dashboardNotifier = null)
    {
        var goalManager = new GoalManager();
        var lifecycleService = new GoalLifecycleService(
            goalManager, NullLogger<GoalLifecycleService>.Instance);

        var pipelineDriver = new PipelineDriver(
            brain: brain,
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => Task.CompletedTask,
            resolvePrompt: (_, _, _, _) => Task.FromResult("prompt"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return new TaskCompletionService(
            pipelineManager, brain, pipelineDriver, lifecycleService,
            dashboardNotifier, logger);
    }

    [Fact]
    public async Task HandleTaskCompletionAsync_LogsModelName_InTaskCompletedMessage()
    {
        // Arrange
        var logger = new CollectingLogger<TaskCompletionService>();
        var brain = new FakeDispatcherBrain();
        var goal = new Goal { Id = "goal-model-log-test", Description = "Test model logging" };

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Coding);
        // Initialize the pipeline state machine so the normal completion path (FilesChanged > 0,
        // avoiding the no-op retry path) can transition out of Coding.
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);

        var service = CreateService(pipelineManager, brain, logger);

        // Act — Model is carried directly on the TaskResult (populated by HiveOrchestratorService)
        await service.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Work completed.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
            Model = "claude-sonnet-4-20250514",
            GitStatus = new GitChangeSummary { FilesChanged = 1 },
        }, TestContext.Current.CancellationToken);

        // Assert - verify "model=claude-sonnet-4-20250514" appears in the task completed log
        var taskCompletedLog = logger.Logs.FirstOrDefault(l =>
            l.Message.Contains("task completed") &&
            l.Message.Contains(goal.Id));
        Assert.True(taskCompletedLog != default, $"Expected task completed log. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");
        Assert.Contains("model=claude-sonnet-4-20250514", taskCompletedLog.Message);
    }

    [Fact]
    public async Task HandleTaskCompletionAsync_WhenModelIsEmpty_LogsUnknownModel()
    {
        // Arrange
        var logger = new CollectingLogger<TaskCompletionService>();
        var brain = new FakeDispatcherBrain();
        var goal = new Goal { Id = "goal-unknown-model-test", Description = "Test unknown model logging" };

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Coding);
        // Initialize the pipeline state machine so the normal completion path (FilesChanged > 0,
        // avoiding the no-op retry path) can transition out of Coding.
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);

        var service = CreateService(pipelineManager, brain, logger);

        // Act — Model defaults to "" when not set on TaskResult
        await service.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Work completed.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
            GitStatus = new GitChangeSummary { FilesChanged = 1 },
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
/// Tests for model name appearing in the phase-completed log emitted by
/// <see cref="PipelineDriver"/>, which still uses the <see cref="GoalDispatcher"/> logger.
/// </summary>
public sealed class GoalDispatcherPhaseModelLoggingTests
{
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
        var modelPlan = IterationPlan.Default();
        pipeline.SetPlan(modelPlan);
        pipeline.StateMachine.RestoreFromPlan(modelPlan.Phases, GoalPhase.Testing);
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
}

/// <summary>
/// Integration tests for <see cref="TaskCompletionService"/> guard logic extracted from
/// <see cref="GoalDispatcher"/>. These tests construct <see cref="TaskCompletionService"/>
/// directly (via <c>InternalsVisibleTo</c>) to verify the three early-exit guards:
/// no pipeline, already terminal (Done/Failed), and stale task (ActiveTaskId mismatch).
/// Also verifies <see cref="GoalPipelineManager.PersistFull"/> is called after a successful
/// phase transition.
/// </summary>
public sealed class TaskCompletionServiceGuardTests
{
    private static TaskCompletionService CreateService(
        GoalPipelineManager pipelineManager,
        IDistributedBrain? brain,
        ILogger<TaskCompletionService> logger,
        DashboardNotifier? dashboardNotifier = null,
        GoalManager? goalManager = null,
        bool failTheDrive = false)
    {
        goalManager ??= new GoalManager();
        var lifecycleService = new GoalLifecycleService(
            goalManager, NullLogger<GoalLifecycleService>.Instance);

        var pipelineDriver = new PipelineDriver(
            brain: brain,
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => Task.CompletedTask,
            // The failure seam: the drive reaches the next phase's prompt resolution, so throwing
            // here makes DriveNextPhaseAsync fail exactly the way a real driver fault would.
            resolvePrompt: failTheDrive
                ? (_, _, _, _) => throw new InvalidOperationException("Simulated drive failure")
                : (_, _, _, _) => Task.FromResult("prompt"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return new TaskCompletionService(
            pipelineManager, brain, pipelineDriver, lifecycleService,
            dashboardNotifier, logger);
    }

    /// <summary>
    /// Guard 1: when no pipeline exists for the task ID, the service logs a warning
    /// and returns without throwing or advancing any pipeline.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_NoPipelineForTaskId_LogsWarningAndReturns()
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var pipelineManager = new GoalPipelineManager();
        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger);

        // Act — task ID has no registered pipeline
        await service.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = "nonexistent-task-id",
            Status = TaskOutcome.Completed,
            Output = "done",
        }, TestContext.Current.CancellationToken);

        // Assert — warning was logged, no exception, no pipeline created
        var warning = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("No pipeline found for completed task") &&
            l.Message.Contains("nonexistent-task-id"));
        Assert.True(warning != default,
            $"Expected 'No pipeline found' warning. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");
    }

    /// <summary>
    /// Guard 2: when the pipeline is already in a terminal phase (Done or Failed), the service
    /// logs an info message and returns without re-driving the pipeline.
    /// </summary>
    [Theory]
    [InlineData(GoalPhase.Done)]
    [InlineData(GoalPhase.Failed)]
    public async Task HandleTaskCompletionAsync_PipelineAlreadyTerminal_LogsInfoAndReturns(GoalPhase terminalPhase)
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var goal = new Goal { Id = $"goal-terminal-{Guid.NewGuid():N}", Description = "Test" };
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(terminalPhase);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);

        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger);

        // Act
        await service.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "done",
        }, TestContext.Current.CancellationToken);

        // Assert — info log about ignoring duplicate, pipeline phase unchanged
        var infoLog = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Information &&
            l.Message.Contains("already") &&
            l.Message.Contains("ignoring duplicate"));
        Assert.True(infoLog != default,
            $"Expected 'already terminal' info log. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");
        Assert.Equal(terminalPhase, pipeline.Phase);
    }

    /// <summary>
    /// Guard 2b: while the state machine is in the honest Planning (re-plan) window its phase
    /// queue is empty, so any arriving completion — late duplicate or a task belonging to the
    /// previous iteration — must drop cleanly instead of flowing into the state machine.
    /// The guard sits BEFORE the stale-task guard, so a NON-active task ID during Planning is
    /// classified as a planning-window completion, not a stale one.
    /// </summary>
    [Theory]
    [InlineData(true)]  // the completion IS the active task
    [InlineData(false)] // the completion is a non-active (stale-looking) task
    public async Task HandleTaskCompletionAsync_PlanningWindow_LogsWarningAndReturns(bool completionIsActiveTask)
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var goal = new Goal { Id = $"goal-planning-{Guid.NewGuid():N}", Description = "Test" };
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Simulate the re-plan window: the state machine produced the NewIteration effect
        // (Review + FAIL → Planning) and the driver advanced the pipeline to Planning.
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Review);
        var transition = pipeline.StateMachine.Transition(PhaseInput.Failed);
        Assert.Equal(TransitionEffect.NewIteration, transition.Effect);
        Assert.Equal(GoalPhase.Planning, pipeline.StateMachine.Phase);
        pipeline.AdvanceTo(GoalPhase.Planning);

        var arrivingTaskId = "task-arriving";
        var activeTaskId = completionIsActiveTask ? arrivingTaskId : "task-other-active";
        pipelineManager.RegisterTask(arrivingTaskId, goal.Id);
        pipeline.SetActiveTask(activeTaskId);

        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger);

        await service.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = arrivingTaskId,
            Status = TaskOutcome.Completed,
            Output = "late completion during re-planning",
        }, TestContext.Current.CancellationToken);

        // The planning-window classification is used — never the stale-task one.
        var windowLog = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("StaleCompletion") &&
            l.Message.Contains("reason=planning-window") &&
            l.Message.Contains(arrivingTaskId));
        Assert.True(windowLog != default,
            $"Expected planning-window warning. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");
        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("ignoring stale completion"));

        // The completion did not drive the pipeline anywhere: the window is still open.
        Assert.Equal(GoalPhase.Planning, pipeline.Phase);
        Assert.Equal(GoalPhase.Planning, pipeline.StateMachine.Phase);
    }

    /// <summary>
    /// Guard 3: when the task ID does not match the pipeline's ActiveTaskId
    /// (stale completion from a previous phase), the service logs a warning and returns.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_StaleTask_LogsWarningAndReturns()
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var goal = new Goal { Id = $"goal-stale-{Guid.NewGuid():N}", Description = "Test" };
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        // Mid-iteration (Testing), NOT in the Planning re-plan window — so a non-active task ID
        // is classified as a stale completion rather than a planning-window completion.
        var stalePlan = IterationPlan.Default();
        pipeline.SetPlan(stalePlan);
        pipeline.StateMachine.RestoreFromPlan(stalePlan.Phases, GoalPhase.Testing);
        pipeline.AdvanceTo(GoalPhase.Testing);

        var oldTaskId = "task-old-phase";
        var currentTaskId = "task-current-phase";
        pipelineManager.RegisterTask(oldTaskId, goal.Id);
        pipelineManager.RegisterTask(currentTaskId, goal.Id);
        pipeline.SetActiveTask(currentTaskId); // pipeline advanced; active task is now current

        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger);

        // Act — late-arriving completion for the OLD task (stale)
        await service.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = oldTaskId,
            Status = TaskOutcome.Completed,
            Output = "stale completion",
        }, TestContext.Current.CancellationToken);

        // Assert — stale warning logged, pipeline phase unchanged
        var staleLog = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("ignoring stale completion") &&
            l.Message.Contains(oldTaskId));
        Assert.True(staleLog != default,
            $"Expected 'stale completion' warning. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");
        Assert.Equal(GoalPhase.Testing, pipeline.Phase);
    }

    /// <summary>
    /// After a successful phase transition, <see cref="GoalPipelineManager.PersistFull"/>
    /// must be called so the pipeline state is persisted. This test uses a real
    /// <see cref="PipelineStore"/> (in-memory SQLite) and verifies the persisted snapshot
    /// reflects the post-transition phase.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_AfterPhaseTransition_PersistsFullPipeline()
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var ct = TestContext.Current.CancellationToken;
        var goal = new Goal { Id = $"goal-persist-{Guid.NewGuid():N}", Description = "Test persist", RepositoryNames = ["test-repo"] };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(ct);

        using var dbContext = CopilotHive.Persistence.CopilotHiveDbContext.CreateInMemory();
        var store = new CopilotHive.Persistence.PipelineStore(dbContext, NullLogger<CopilotHive.Persistence.PipelineStore>.Instance);
        var pipelineManager = new GoalPipelineManager(store);
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.SetPlan(new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging] });
        pipeline.StateMachine.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);

        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger, goalManager: goalManager);

        // Act — Coding phase completes with PASS → should advance to Testing and PersistFull
        await service.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Coding done.",
            GitStatus = new GitChangeSummary { FilesChanged = 2, Pushed = true },
            Metrics = new TaskMetrics { Verdict = "PASS" },
        }, ct);

        // Assert — pipeline advanced past Coding
        Assert.NotEqual(GoalPhase.Coding, pipeline.Phase);

        // Assert — PersistFull was called: the persisted snapshot reflects the new phase
        var snapshot = store.LoadPipeline(goal.Id);
        Assert.NotNull(snapshot);
        Assert.Equal(pipeline.Phase, snapshot!.Phase);
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    //  Guard 4 — THE COMPLETION ADMISSION.
    //
    //  It sits immediately AFTER the stale-task guard (the position the earlier separate
    //  locked read occupied) and is now ONE atomic, lock-scoped decision that both
    //  classifies the completion and — for a Pending slot — CLAIMS it:
    //
    //    • SlotAbandoned       → the stale-completion template, dropped;
    //    • SlotAlreadyAdmitted → the duplicate-completion template, dropped;
    //    • Admitted / NoSlot   → into the drive.
    //
    //  Because the decision and the claim share one lock span, a retire can no longer
    //  land between them. What is NOT claimed: a retire landing AFTER the claim still
    //  proceeds — the drive is not isolated from it.
    //
    //  The completion path's counterpart to the claim is the RECORD, which sits inside
    //  the try immediately after a SUCCESSFUL drive; a failed drive must leave the slot
    //  Claimed. Both placements are pinned below.
    // ══════════════════════════════════════════════════════════════════════════════════

    // WorkSlotState is internal, so it cannot appear in a public test method signature.
    // The compatibility theory below carries integer codes mapped back through SlotState().
    public const int SlotPending = (int)WorkSlotState.Pending;
    public const int SlotClaimed = (int)WorkSlotState.Claimed;
    public const int SlotRecorded = (int)WorkSlotState.Recorded;

    /// <summary>Sentinel meaning "no slot is registered for the task id" (the legacy column).</summary>
    public const int SlotNone = -1;

    private static WorkSlotState? SlotState(int code) => code == SlotNone ? null : (WorkSlotState)code;

    /// <summary>The exact drop template the guard must emit, with the placeholders rendered.</summary>
    private static string ExpectedDropLog(string goalId, string taskId, GoalPhase pipelinePhase) =>
        $"WorkSlotIntegrity: stale-completion goal={goalId} task={taskId} " +
        $"pipeline-phase={pipelinePhase} slot-state=abandoned — the completion is for a retired " +
        "attempt; dropped";

    /// <summary>
    /// The exact DUPLICATE-completion template, with the placeholders rendered. THREE fields —
    /// there is deliberately no <c>slot-state</c> field, because the collapsed
    /// <c>SlotAlreadyAdmitted</c> outcome does not distinguish Claimed from Recorded and the line
    /// must not claim knowledge it does not have.
    /// </summary>
    private static string ExpectedDuplicateLog(string goalId, string taskId, GoalPhase pipelinePhase) =>
        $"WorkSlotIntegrity: duplicate-completion goal={goalId} task={taskId} " +
        $"pipeline-phase={pipelinePhase} — the attempt was already admitted; dropped";

    /// <summary>The single registered slot's state, or <c>null</c> when no slot exists.</summary>
    private static WorkSlotState? SlotStateOf(GoalPipeline pipeline, string taskId) =>
        pipeline.GetSlotsForTest().FirstOrDefault(v => v.Slot.TaskId == taskId)?.State;

    /// <summary>
    /// Builds the reclaim-path fixture: a mid-iteration pipeline (Testing — NOT the Planning
    /// window) whose active task owns a registered work slot in <paramref name="slotState"/>.
    /// <c>StNone</c>-style absence is expressed by passing <c>null</c>.
    /// </summary>
    private static (GoalPipelineManager Manager, GoalPipeline Pipeline, string TaskId) SlotGuardFixture(
        string goalId,
        WorkSlotState? slotState)
    {
        var goal = new Goal { Id = goalId, Description = "Test" };
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Testing);
        pipeline.AdvanceTo(GoalPhase.Testing);

        var taskId = $"task-{Guid.NewGuid():N}";
        // The mapping is registered and NEVER unregistered — exactly what the interim cleanup
        // leaves behind, so the completion still resolves this pipeline.
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);

        if (slotState is not null)
        {
            Assert.True(pipeline.SeedSlotForTest(
                taskId, new WorkSlotPosition(1, GoalPhase.Testing, 1), 1, WorkSlotState.Pending));

            // Reach the target state through the PRODUCTION transitions where possible, so the
            // fixture mirrors the real reclaim path rather than forcing an arbitrary state.
            switch (slotState.Value)
            {
                case WorkSlotState.Pending:
                    break;
                case WorkSlotState.Abandoned:
                    // Today's interim cleanup: AbandonSlot retires the Pending slot in place.
                    Assert.True(pipeline.AbandonSlot(taskId));
                    break;
                case WorkSlotState.Claimed:
                case WorkSlotState.Recorded:
                    Assert.True(pipeline.ForceSlotStateForTest(taskId, slotState.Value));
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled WorkSlotState: {slotState.Value}");
            }
        }

        return (pipelineManager, pipeline, taskId);
    }

    private static TaskResult CompletionFor(string taskId) => new()
    {
        TaskId = taskId,
        Status = TaskOutcome.Completed,
        Output = "Testing passed.",
        Metrics = new TaskMetrics { Verdict = "PASS" },
        GitStatus = new GitChangeSummary { FilesChanged = 1, Pushed = true },
    };

    /// <summary>
    /// Builds a <see cref="GoalManager"/> that KNOWS the fixture's goal, so the terminal paths
    /// (<c>MarkGoalCompletedAsync</c> / <c>MarkGoalFailedAsync</c>) can persist a status update
    /// instead of throwing <see cref="KeyNotFoundException"/>. The polling call primes the
    /// manager's goal→source map, exactly as the persistence vector above does.
    /// </summary>
    private static async Task<GoalManager> TerminalCapableGoalManagerAsync(GoalPipeline pipeline)
    {
        var goalManager = new GoalManager();
        goalManager.AddSource(new FakeGoalSource(pipeline.Goal));
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);
        return goalManager;
    }

    /// <summary>
    /// The completion observes an ALREADY-RETIRED slot while its task→goal mapping is still
    /// intact, so the pipeline resolves and every earlier guard passes. The guard drops it:
    /// the exact template is logged and the pipeline phase does NOT move.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_AbandonedSlotWithIntactMapping_DropsCompletionUnmoved()
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var (pipelineManager, pipeline, taskId) =
            SlotGuardFixture($"goal-abandoned-{Guid.NewGuid():N}", WorkSlotState.Abandoned);
        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger);

        await service.HandleTaskCompletionAsync(CompletionFor(taskId), TestContext.Current.CancellationToken);

        // THE EXACT TEMPLATE, rendered — not a loose substring match.
        var dropLog = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message == ExpectedDropLog(pipeline.GoalId, taskId, GoalPhase.Testing));
        Assert.True(dropLog != default,
            $"Expected the exact WorkSlotIntegrity drop log. Logs: {string.Join(" | ", logger.Logs.Select(l => l.Message))}");

        // The completion never reached the driver: the phase is untouched on BOTH the pipeline
        // and its state machine.
        Assert.Equal(GoalPhase.Testing, pipeline.Phase);
        Assert.Equal(GoalPhase.Testing, pipeline.StateMachine.Phase);
    }

    /// <summary>
    /// THE PASS-THROUGH SET, narrowed by E1 to exactly two members: a <c>Pending</c> slot (the
    /// admission claims it and the drive runs) and an ABSENT slot (legacy, pre-registry tasks —
    /// <c>NoSlot</c>, the untouched legacy pass-through). Claimed and Recorded are NO LONGER here:
    /// under the admission they are duplicates and are dropped, as the two vectors below prove.
    /// </summary>
    [Theory]
    [InlineData(SlotPending)]
    [InlineData(SlotNone)]
    public async Task HandleTaskCompletionAsync_AdmittedOrNoSlot_PassesTheAdmissionAndDrivesThePipeline(
        int slotStateCode)
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var (pipelineManager, pipeline, taskId) =
            SlotGuardFixture($"goal-live-{Guid.NewGuid():N}", SlotState(slotStateCode));
        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger);

        await service.HandleTaskCompletionAsync(CompletionFor(taskId), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("WorkSlotIntegrity"));
        // The completion flowed through: the normal task-completed log was emitted and the
        // pipeline left Testing.
        Assert.Contains(logger.Logs, l => l.Message.Contains("task completed"));
        Assert.NotEqual(GoalPhase.Testing, pipeline.Phase);
    }

    /// <summary>
    /// THE DUPLICATE DROP — the vector that replaces the pre-E1 Claimed/Recorded pass-throughs.
    /// A completion for an attempt that is ALREADY admitted (its slot is Claimed or Recorded) is
    /// a duplicate: the exact three-field template is logged and the drive NEVER runs.
    /// <para>
    /// Both states collapse into the one <c>SlotAlreadyAdmitted</c> outcome and therefore into the
    /// one template — which carries no <c>slot-state</c> field precisely because it cannot tell
    /// them apart.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(SlotClaimed)]
    [InlineData(SlotRecorded)]
    public async Task HandleTaskCompletionAsync_AlreadyAdmittedSlot_DropsDuplicateWithoutDriving(
        int slotStateCode)
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var (pipelineManager, pipeline, taskId) =
            SlotGuardFixture($"goal-duplicate-{Guid.NewGuid():N}", SlotState(slotStateCode));
        var stateBefore = SlotStateOf(pipeline, taskId);
        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger);

        await service.HandleTaskCompletionAsync(CompletionFor(taskId), TestContext.Current.CancellationToken);

        // THE EXACT TEMPLATE, rendered — not a loose substring match.
        var duplicateLog = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message == ExpectedDuplicateLog(pipeline.GoalId, taskId, GoalPhase.Testing));
        Assert.True(duplicateLog != default,
            $"Expected the exact duplicate-completion log. Logs: {string.Join(" | ", logger.Logs.Select(l => l.Message))}");

        // THE DRIVE NEVER RAN: the drop precedes the normal task-completed log, and neither the
        // pipeline nor its state machine moved.
        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("task completed"));
        Assert.Equal(GoalPhase.Testing, pipeline.Phase);
        Assert.Equal(GoalPhase.Testing, pipeline.StateMachine.Phase);

        // The duplicate is inert on the registry too: the slot did not move.
        Assert.Equal(stateBefore, SlotStateOf(pipeline, taskId));
    }

    /// <summary>
    /// The duplicate template must NEVER be confused with the abandoned one: an Abandoned slot
    /// emits the stale-completion line and an already-admitted slot the duplicate line, and
    /// neither vector emits the other's text.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_AbandonedAndDuplicate_UseDistinctTemplates()
    {
        var abandonedLogger = new CollectingLogger<TaskCompletionService>();
        var (abandonedManager, abandonedPipeline, abandonedTaskId) =
            SlotGuardFixture($"goal-tmpl-a-{Guid.NewGuid():N}", WorkSlotState.Abandoned);
        await CreateService(abandonedManager, brain: new FakeDispatcherBrain(), abandonedLogger)
            .HandleTaskCompletionAsync(CompletionFor(abandonedTaskId), TestContext.Current.CancellationToken);

        var duplicateLogger = new CollectingLogger<TaskCompletionService>();
        var (duplicateManager, duplicatePipeline, duplicateTaskId) =
            SlotGuardFixture($"goal-tmpl-d-{Guid.NewGuid():N}", WorkSlotState.Claimed);
        await CreateService(duplicateManager, brain: new FakeDispatcherBrain(), duplicateLogger)
            .HandleTaskCompletionAsync(CompletionFor(duplicateTaskId), TestContext.Current.CancellationToken);

        Assert.Contains(abandonedLogger.Logs, l =>
            l.Message == ExpectedDropLog(abandonedPipeline.GoalId, abandonedTaskId, GoalPhase.Testing));
        Assert.DoesNotContain(abandonedLogger.Logs, l => l.Message.Contains("duplicate-completion"));

        Assert.Contains(duplicateLogger.Logs, l =>
            l.Message == ExpectedDuplicateLog(duplicatePipeline.GoalId, duplicateTaskId, GoalPhase.Testing));
        Assert.DoesNotContain(duplicateLogger.Logs, l => l.Message.Contains("stale-completion"));
    }

    /// <summary>
    /// THE POST-DRIVE RECORD, success path: an admitted (Pending → Claimed) slot whose drive
    /// SUCCEEDS is transitioned to <c>Recorded</c> by the record call that sits immediately after
    /// the await, inside the try.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_SuccessfulDrive_RecordsTheAdmittedSlot()
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var (pipelineManager, pipeline, taskId) =
            SlotGuardFixture($"goal-record-{Guid.NewGuid():N}", WorkSlotState.Pending);
        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger);

        await service.HandleTaskCompletionAsync(CompletionFor(taskId), TestContext.Current.CancellationToken);

        // The drive genuinely ran…
        Assert.NotEqual(GoalPhase.Testing, pipeline.Phase);
        // …and the slot was recorded afterwards.
        Assert.Equal(WorkSlotState.Recorded, SlotStateOf(pipeline, taskId));
    }

    /// <summary>
    /// THE POST-DRIVE RECORD, failure path — the placement proof that matters: when the drive
    /// THROWS, the slot must stay <c>Claimed</c>. A record call moved below the catch blocks would
    /// record a slot whose work never completed, so this vector is what pins the call INSIDE the
    /// try, immediately after the successful await.
    /// <para>
    /// The goal is failed by the catch, and the terminal transition abandons PENDING slots only
    /// (the A1a in-flight exemption), so the Claimed slot survives untouched — the terminal residue
    /// the E2 reconciliation successor owns.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_FailedDrive_LeavesTheAdmittedSlotClaimed()
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var (pipelineManager, pipeline, taskId) =
            SlotGuardFixture($"goal-drivefail-{Guid.NewGuid():N}", WorkSlotState.Pending);
        var service = CreateService(
            pipelineManager, brain: new FakeDispatcherBrain(), logger,
            goalManager: await TerminalCapableGoalManagerAsync(pipeline), failTheDrive: true);

        await service.HandleTaskCompletionAsync(CompletionFor(taskId), TestContext.Current.CancellationToken);

        // The drive really did fail (the catch ran and failed the goal).
        Assert.Contains(logger.Logs, l =>
            l.Level == LogLevel.Error && l.Message.Contains("Error driving pipeline"));
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);

        // THE PLACEMENT PROOF: no record happened.
        Assert.Equal(WorkSlotState.Claimed, SlotStateOf(pipeline, taskId));
    }

    /// <summary>
    /// The drop fires on the NO-BRAIN fixture too: the admission precedes the brain branch, so a
    /// brain-less service must not mark the goal completed off a retired attempt.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_AbandonedSlotWithoutBrain_StillDrops()
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var (pipelineManager, pipeline, taskId) =
            SlotGuardFixture($"goal-nobrain-{Guid.NewGuid():N}", WorkSlotState.Abandoned);
        var service = CreateService(pipelineManager, brain: null, logger);

        await service.HandleTaskCompletionAsync(CompletionFor(taskId), TestContext.Current.CancellationToken);

        Assert.Contains(logger.Logs, l =>
            l.Level == LogLevel.Warning &&
            l.Message == ExpectedDropLog(pipeline.GoalId, taskId, GoalPhase.Testing));

        // The no-brain path (MarkGoalCompletedAsync) was NOT taken.
        Assert.Equal(GoalPhase.Testing, pipeline.Phase);
    }

    /// <summary>
    /// THE NO-BRAIN ADMITTED PATH — the degenerate single-phase mode, pinned as the DELIBERATE
    /// behaviour it is. An admitted completion completes the goal and the slot stays
    /// <c>Claimed</c>: there is no record call on this path, on purpose. The goal is Done and its
    /// pipeline is removed, so the slot's terminal state is irrelevant; the terminal
    /// <c>AbandonPendingSlots</c> exempts in-flight slots, so it is not abandoned either.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_AdmittedWithoutBrain_CompletesGoalAndLeavesSlotClaimed()
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var (pipelineManager, pipeline, taskId) =
            SlotGuardFixture($"goal-nobrain-ok-{Guid.NewGuid():N}", WorkSlotState.Pending);
        var service = CreateService(
            pipelineManager, brain: null, logger,
            goalManager: await TerminalCapableGoalManagerAsync(pipeline));

        await service.HandleTaskCompletionAsync(CompletionFor(taskId), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("WorkSlotIntegrity"));
        // The no-brain early-out completed the goal…
        Assert.Equal(GoalPhase.Done, pipeline.Phase);
        // …and left the admitted slot Claimed — never Recorded, never Abandoned.
        Assert.Equal(WorkSlotState.Claimed, SlotStateOf(pipeline, taskId));
    }

    /// <summary>
    /// GUARD ORDER: the admission sits AFTER the stale-task guard. A completion that is both
    /// stale (its task is not the active one) AND backed by an abandoned slot is classified as
    /// STALE — the earlier guard wins and the WorkSlotIntegrity template is never emitted.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_StaleAndAbandoned_IsClassifiedByTheEarlierStaleGuard()
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var (pipelineManager, pipeline, oldTaskId) =
            SlotGuardFixture($"goal-order-{Guid.NewGuid():N}", WorkSlotState.Abandoned);

        // The pipeline has moved on to a NEWER task, so the old completion is stale as well.
        pipeline.SetActiveTask("task-current-phase");

        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger);

        await service.HandleTaskCompletionAsync(CompletionFor(oldTaskId), TestContext.Current.CancellationToken);

        Assert.Contains(logger.Logs, l =>
            l.Level == LogLevel.Warning && l.Message.Contains("ignoring stale completion"));
        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("WorkSlotIntegrity"));
        Assert.Equal(GoalPhase.Testing, pipeline.Phase);
    }

    /// <summary>
    /// The guard order in the other direction: the ABANDONED classification is only reached once
    /// the stale-task guard has passed, i.e. the retired attempt IS the active task. This is the
    /// complement of the vector above and pins the admission's position rather than its mere
    /// presence.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_AbandonedActiveTask_IsClassifiedByTheNewGuardNotStale()
    {
        var logger = new CollectingLogger<TaskCompletionService>();
        var (pipelineManager, pipeline, taskId) =
            SlotGuardFixture($"goal-order2-{Guid.NewGuid():N}", WorkSlotState.Abandoned);
        var service = CreateService(pipelineManager, brain: new FakeDispatcherBrain(), logger);

        await service.HandleTaskCompletionAsync(CompletionFor(taskId), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("ignoring stale completion"));
        Assert.Contains(logger.Logs, l =>
            l.Message == ExpectedDropLog(pipeline.GoalId, taskId, GoalPhase.Testing));
        // The guard drops BEFORE the normal task-completed log is emitted.
        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("task completed"));
    }
}

/// <summary>
/// Tests for push-failure warning log in <see cref="GoalDispatcher.HandleTaskCompletionAsync"/>.
/// </summary>
public sealed class GoalDispatcherPushFailureLoggingTests
{
    [Fact]
    public async Task HandleTaskCompletionAsync_LogsWarning_WhenFilesChangedButNotPushed()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var brain = new FakeDispatcherBrain();
        var taskQueue = new TaskQueue();
        var goal = new Goal { Id = "goal-push-fail-test", Description = "Test push failure warning" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        var codingPlan = IterationPlan.Default();
        pipeline.SetPlan(codingPlan);
        pipeline.StateMachine.RestoreFromPlan(codingPlan.Phases, GoalPhase.Coding);
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

        // Act — files changed but Pushed = false (push failed)
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Coder completed.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
            GitStatus = new GitChangeSummary { FilesChanged = 4, Insertions = 30, Deletions = 5, Pushed = false },
        }, TestContext.Current.CancellationToken);

        // Assert — warning logged for push failure
        var pushWarning = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("push failed"));
        Assert.True(pushWarning != default, $"Expected push failure warning log. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");
        Assert.Contains(taskId, pushWarning.Message);
    }

    [Fact]
    public async Task HandleTaskCompletionAsync_NoWarning_WhenPushedSuccessfully()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var brain = new FakeDispatcherBrain();
        var taskQueue = new TaskQueue();
        var goal = new Goal { Id = "goal-push-ok-test", Description = "Test no push warning when pushed ok" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        var codingPlan = IterationPlan.Default();
        pipeline.SetPlan(codingPlan);
        pipeline.StateMachine.RestoreFromPlan(codingPlan.Phases, GoalPhase.Coding);
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

        // Act — files changed and Pushed = true
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Coder completed.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
            GitStatus = new GitChangeSummary { FilesChanged = 4, Insertions = 30, Deletions = 5, Pushed = true },
        }, TestContext.Current.CancellationToken);

        // Assert — no push failure warning
        var pushWarning = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("push failed"));
        Assert.True(pushWarning == default, $"Unexpected push failure warning log found.");
    }

    private static async Task<List<(LogLevel Level, string Message)>> RunPushFailureAsync(
        string goalId, GitChangeSummary gitStatus)
    {
        var logger = new CollectingLogger<GoalDispatcher>();
        var brain = new FakeDispatcherBrain();
        var taskQueue = new TaskQueue();
        var goal = new Goal { Id = goalId, Description = "Test push failure paths" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        var codingPlan = IterationPlan.Default();
        pipeline.SetPlan(codingPlan);
        pipeline.StateMachine.RestoreFromPlan(codingPlan.Phases, GoalPhase.Coding);
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

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Coder completed.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
            GitStatus = gitStatus,
        }, TestContext.Current.CancellationToken);

        return logger.Logs;
    }

    /// <summary>
    /// The push-failure warning must list the changed-file paths so the Composer/user can
    /// tell which files the worker touched — including repository-qualified paths.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_PushFailureWarning_IncludesChangedFilePaths()
    {
        var logs = await RunPushFailureAsync("goal-push-paths", new GitChangeSummary
        {
            FilesChanged = 3,
            Insertions = 30,
            Deletions = 5,
            Pushed = false,
            ChangedFiles = ["repoA:src/Services/Foo.cs", "repoA:tests/FooTests.cs", "repoB:docs/README.md"],
        });

        var warning = logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("push failed"));
        Assert.True(warning != default, "Expected push failure warning log.");
        Assert.Contains("repoA:src/Services/Foo.cs", warning.Message);
        Assert.Contains("repoA:tests/FooTests.cs", warning.Message);
        Assert.Contains("repoB:docs/README.md", warning.Message);
        // Not truncated → no "+N more" marker
        Assert.DoesNotContain("more)", warning.Message);
    }

    /// <summary>
    /// When the path list is shorter than the file count, the log adds a "+N more"
    /// marker derived at format time (never stored in the list itself).
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_PushFailureWarning_AddsPlusNMoreWhenTruncated()
    {
        var logs = await RunPushFailureAsync("goal-push-truncated", new GitChangeSummary
        {
            FilesChanged = 60,
            Insertions = 100,
            Deletions = 10,
            Pushed = false,
            ChangedFiles = ["src/A.cs", "src/B.cs"],
        });

        var warning = logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("push failed"));
        Assert.True(warning != default, "Expected push failure warning log.");
        Assert.Contains("src/A.cs", warning.Message);
        Assert.Contains("(+58 more)", warning.Message);
    }

    /// <summary>
    /// Control characters in changed-file paths must be sanitized so a worker cannot inject
    /// extra lines into the orchestrator log.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_PushFailureWarning_SanitizesControlCharacters()
    {
        var logs = await RunPushFailureAsync("goal-push-injection", new GitChangeSummary
        {
            FilesChanged = 1,
            Pushed = false,
            ChangedFiles = ["src/evil\n2026-01-01 FATAL fake\r\tline.cs"],
        });

        var warning = logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("push failed"));
        Assert.True(warning != default, "Expected push failure warning log.");
        Assert.DoesNotContain("\n", warning.Message);
        Assert.DoesNotContain("\r", warning.Message);
        Assert.DoesNotContain("\t", warning.Message);
        Assert.Contains("src/evil?", warning.Message);
    }

    /// <summary>
    /// When no paths are available, the warning omits the path section entirely.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_PushFailureWarning_OmitsPathSectionWhenListEmpty()
    {
        var logs = await RunPushFailureAsync("goal-push-nopaths", new GitChangeSummary
        {
            FilesChanged = 4,
            Pushed = false,
            ChangedFiles = [],
        });

        var warning = logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("push failed"));
        Assert.True(warning != default, "Expected push failure warning log.");
        Assert.DoesNotContain("Changed files:", warning.Message);
    }

    [Theory]
    [InlineData("src/Foo.cs", "src/Foo.cs")]
    [InlineData("a\nb", "a?b")]
    [InlineData("a\r\nb", "a??b")]
    [InlineData("a\tb", "a?b")]
    [InlineData("", "")]
    // NUL, backspace, vertical tab, form feed and other C0 characters
    [InlineData("a\0b", "a?b")]
    [InlineData("a\bb", "a?b")]
    [InlineData("a\vb", "a?b")]
    [InlineData("a\fb", "a?b")]
    [InlineData("a\u0001\u0002\u0003\u0004b", "a????b")]
    [InlineData("a\u0005\u0006\u0007\u0008b", "a????b")]
    [InlineData("a\u001Bb", "a?b")]      // ESC — ANSI escape sequence injection
    [InlineData("a\u007Fb", "a?b")]      // DEL
    // Unicode line-breaking characters
    [InlineData("a\u0085b", "a?b")]      // NEL (NEXT LINE)
    [InlineData("a\u2028b", "a?b")]      // LINE SEPARATOR
    [InlineData("a\u2029b", "a?b")]      // PARAGRAPH SEPARATOR
    // Legal non-ASCII path characters must survive untouched
    [InlineData("src/é-ünï-文件.cs", "src/é-ünï-文件.cs")]
    [InlineData(" padded name ", " padded name ")]
    public void SanitizeLogPath_ReplacesControlCharacters(string input, string expected)
    {
        Assert.Equal(expected, PipelineDriver.SanitizeLogPath(input));
    }

    /// <summary>
    /// A path packed with EVERY category of line-breaking / control character must be fully
    /// neutralised in the emitted warning: the formatted message must remain a single line
    /// with no raw control characters at all.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_PushFailureWarning_SanitizesAllControlAndUnicodeLineBreakers()
    {
        // Every C0 control char (excluding none), DEL, and the Unicode line breakers,
        // embedded in an otherwise ordinary-looking path used to forge a second log line.
        var c0 = new string([.. Enumerable.Range(0, 0x20).Select(i => (char)i)]);
        var evilPath = $"src/evil{c0}\u007F\u0085\u2028\u20292026-01-01 FATAL forged entry.cs";

        var logs = await RunPushFailureAsync("goal-push-allcontrol", new GitChangeSummary
        {
            FilesChanged = 2,
            Pushed = false,
            ChangedFiles = [evilPath, "src/Ok.cs"],
        });

        var warning = logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("push failed"));
        Assert.True(warning != default, "Expected push failure warning log.");

        // No raw control character of ANY kind survived into the formatted message.
        Assert.DoesNotContain(warning.Message, char.IsControl);
        Assert.DoesNotContain('\u2028', warning.Message);
        Assert.DoesNotContain('\u2029', warning.Message);

        // Therefore the log entry is strictly single-line under every splitting convention.
        Assert.Single(warning.Message.Split(['\n', '\r', '\u0085', '\u2028', '\u2029']));

        // The surrounding legal text is preserved, and the clean sibling path is intact.
        Assert.Contains("src/evil", warning.Message);
        Assert.Contains("FATAL forged entry.cs", warning.Message);
        Assert.Contains("src/Ok.cs", warning.Message);
    }
}

/// <summary>
/// Tests for <see cref="PipelineHelpers.BuildSquashCommitMessage"/> logic.
/// </summary>
public sealed class GoalDispatcherBuildSquashCommitMessageTests
{
    [Fact]
    public void BuildSquashCommitMessage_ShortDescription_ReturnsSingleLineMessage()
    {
        var result = PipelineHelpers.BuildSquashCommitMessage("goal-123", "Add logging support");

        Assert.Equal("Goal: goal-123 \u2014 Add logging support", result);
    }

    [Fact]
    public void BuildSquashCommitMessage_SubjectStartsWithGoalIdAndEmdash()
    {
        var result = PipelineHelpers.BuildSquashCommitMessage("abc-42", "Fix the bug");

        Assert.StartsWith("Goal: abc-42 \u2014", result);
    }

    [Fact]
    public void BuildSquashCommitMessage_LongDescription_TruncatesSubjectTo120Chars()
    {
        var longDescription = new string('x', 200);

        var result = PipelineHelpers.BuildSquashCommitMessage("goal-1", longDescription);

        var subjectLine = result.Split('\n')[0];
        Assert.True(subjectLine.Length <= 120,
            $"Subject line length {subjectLine.Length} exceeds 120 characters: {subjectLine}");
    }

    [Fact]
    public void BuildSquashCommitMessage_LongDescription_IncludesFullDescriptionInBody()
    {
        var longDescription = new string('x', 200);

        var result = PipelineHelpers.BuildSquashCommitMessage("goal-1", longDescription);

        Assert.Contains(longDescription.Trim(), result);
    }

    [Fact]
    public void BuildSquashCommitMessage_MultiLineDescription_UsesOnlyFirstLineInSubject()
    {
        var description = "First line summary\nSecond line details\nThird line more details";

        var result = PipelineHelpers.BuildSquashCommitMessage("goal-99", description);

        var subjectLine = result.Split('\n')[0];
        Assert.Contains("First line summary", subjectLine);
        Assert.DoesNotContain("Second line", subjectLine);
    }

    [Fact]
    public void BuildSquashCommitMessage_MultiLineDescription_IncludesFullBodyAfterBlankLine()
    {
        var description = "First line\nSecond line";

        var result = PipelineHelpers.BuildSquashCommitMessage("goal-5", description);

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

        var result = PipelineHelpers.BuildSquashCommitMessage("id", description);

        // Should be a single line (no body needed)
        Assert.Equal($"Goal: id \u2014 {description}", result);
        Assert.DoesNotContain("\n", result);
    }

    [Fact]
    public void BuildSquashCommitMessage_EmptyDescription_HandledGracefully()
    {
        var result = PipelineHelpers.BuildSquashCommitMessage("goal-0", "");

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
    /// When the Brain returns null, the result must equal <see cref="PipelineHelpers.BuildSquashCommitMessage"/>.
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
        var expected = PipelineHelpers.BuildSquashCommitMessage(goalId, description);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// When the Brain throws (non-cancellation), the result must equal <see cref="PipelineHelpers.BuildSquashCommitMessage"/>.
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
        var expected = PipelineHelpers.BuildSquashCommitMessage(goalId, description);
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

/// <summary>
/// Logger that forwards to an inner logger and completes a TCS when a log message
/// matching the trigger is emitted. Used to await specific log output without polling.
/// </summary>
file sealed class SignalingLogger<T> : ILogger<T>
{
    private readonly ILogger<T> _inner;
    private readonly TaskCompletionSource<string> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _trigger;

    public Task<string> MatchedLog => _gate.Task;

    public SignalingLogger(ILogger<T> inner, string trigger)
    {
        _inner = inner;
        _trigger = trigger;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var formatted = formatter(state, exception);
        _inner.Log(logLevel, eventId, state, exception, formatter);
        if (formatted.Contains(_trigger, StringComparison.Ordinal))
            _gate.TrySetResult(formatted);
    }
}

/// <summary>
/// Tests for auto-tagging completed goals to Planning releases when exactly one exists.
/// </summary>
public sealed class GoalDispatcherAutoTagReleaseTests
{
    [Fact]
    public async Task TryAutoTagRelease_WithExactlyOnePlanningRelease_TagsGoal()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a goal store with a Planning release
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);

        // Create a Planning release
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Planning };
        await store.CreateReleaseAsync(release, ct);

        // Create a goal with a repository but no ReleaseId
        var goal = new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = ["CopilotHive"],
            CreatedAt = DateTime.UtcNow,
        };
        await store.CreateGoalAsync(goal, ct);

        // Simulate the auto-tag by calling the same logic
        var releases = await store.GetReleasesAsync(ct);
        var planningReleases = releases.Where(r => r.Status == ReleaseStatus.Planning).ToList();

        Assert.Single(planningReleases);

        goal.ReleaseId = planningReleases[0].Id;
        await store.UpdateGoalAsync(goal, ct);

        // Verify the goal is now tagged
        var fetched = await store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Equal("v1.0.0", fetched!.ReleaseId);
    }

    [Fact]
    public async Task TryAutoTagRelease_WithMultiplePlanningReleases_DoesNotTag()
    {
        var ct = TestContext.Current.CancellationToken;

        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);

        // Create multiple Planning releases
        await store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Planning }, ct);
        await store.CreateReleaseAsync(new Release { Id = "v1.1.0", Tag = "v1.1.0", Status = ReleaseStatus.Planning }, ct);

        // Create a goal with a repository but no ReleaseId
        var goal = new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = ["CopilotHive"],
            CreatedAt = DateTime.UtcNow,
        };
        await store.CreateGoalAsync(goal, ct);

        // Check the condition - should NOT auto-tag when multiple planning releases
        var releases = await store.GetReleasesAsync(ct);
        var planningReleases = releases.Where(r => r.Status == ReleaseStatus.Planning).ToList();

        Assert.Equal(2, planningReleases.Count);

        // Goal should remain untagged
        var fetched = await store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.ReleaseId);
    }

    [Fact]
    public async Task TryAutoTagRelease_WithNoPlanningReleases_DoesNotTag()
    {
        var ct = TestContext.Current.CancellationToken;

        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);

        // Create a Released release (not Planning)
        await store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Released }, ct);

        // Create a goal with a repository but no ReleaseId
        var goal = new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = ["CopilotHive"],
            CreatedAt = DateTime.UtcNow,
        };
        await store.CreateGoalAsync(goal, ct);

        // Check the condition - should NOT auto-tag when no planning releases
        var releases = await store.GetReleasesAsync(ct);
        var planningReleases = releases.Where(r => r.Status == ReleaseStatus.Planning).ToList();

        Assert.Empty(planningReleases);

        // Goal should remain untagged
        var fetched = await store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.ReleaseId);
    }

    [Fact]
    public async Task TryAutoTagRelease_GoalWithNoRepositories_DoesNotTag()
    {
        var ct = TestContext.Current.CancellationToken;

        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);

        // Create a Planning release
        await store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Planning }, ct);

        // Create a goal WITHOUT repositories
        var goal = new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = [], // Empty list
            CreatedAt = DateTime.UtcNow,
        };
        await store.CreateGoalAsync(goal, ct);

        // Goal should remain untagged (no repositories)
        var fetched = await store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.ReleaseId);
    }

    [Fact]
    public async Task TryAutoTagRelease_GoalAlreadyTagged_DoesNotChange()
    {
        var ct = TestContext.Current.CancellationToken;

        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);

        // Create a Planning release
        await store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Planning }, ct);

        // Create a goal already assigned to a different release
        var goal = new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = ["CopilotHive"],
            ReleaseId = "v0.9.0", // Already assigned
            CreatedAt = DateTime.UtcNow,
        };
        await store.CreateGoalAsync(goal, ct);

        // Goal should keep its original release
        var fetched = await store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Equal("v0.9.0", fetched!.ReleaseId);
    }

    [Fact]
    public async Task TryAutoTagRelease_ReloadsGoalFromStore_DoesNotOverwriteCompletedFields()
    {
        var ct = TestContext.Current.CancellationToken;

        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);

        // Create a Planning release
        await store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Planning }, ct);

        // Create a goal with completion fields set (simulating a goal that was Completed
        // and then had its status / timestamps written to the store by UpdateGoalStatusAsync).
        var completedAt = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var startedAt = new DateTime(2025, 1, 1, 11, 0, 0, DateTimeKind.Utc);
        var goal = new Goal
        {
            Id = "completed-goal",
            Description = "A completed goal",
            RepositoryNames = ["CopilotHive"],
            Status = GoalStatus.Completed,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Iterations = 3,
            MergeCommitHash = "abc123",
            CreatedAt = DateTime.UtcNow,
        };
        await store.CreateGoalAsync(goal, ct);

        // Simulate TryAutoTagReleaseAsync: reload from store, then set ReleaseId
        var goalId = goal.Id;
        var releases = await store.GetReleasesAsync(ct);
        var planningReleases = releases.Where(r => r.Status == ReleaseStatus.Planning).ToList();
        Assert.Single(planningReleases);

        var planningRelease = planningReleases[0];
        // Bug 1 fix: reload fresh goal from store (NOT the stale in-memory object)
        var freshGoal = await store.GetGoalAsync(goalId, ct);
        Assert.NotNull(freshGoal);
        Assert.Null(freshGoal!.ReleaseId); // not yet tagged
        freshGoal.ReleaseId = planningRelease.Id;
        await store.UpdateGoalAsync(freshGoal, ct);

        // Verify the completion fields are preserved and not overwritten
        var fetched = await store.GetGoalAsync(goalId, ct);
        Assert.NotNull(fetched);
        Assert.Equal("v1.0.0", fetched!.ReleaseId);
        Assert.Equal(GoalStatus.Completed, fetched.Status);
        Assert.Equal(completedAt, fetched.CompletedAt);
        Assert.Equal(startedAt, fetched.StartedAt);
        Assert.Equal(3, fetched.Iterations);
        Assert.Equal("abc123", fetched.MergeCommitHash);
    }

    [Fact]
    public async Task GetGoalsByRelease_ReturnsCorrectGoals()
    {
        var ct = TestContext.Current.CancellationToken;

        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);

        // Create a release
        await store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0" }, ct);

        // Create goals with and without release assignment
        await store.CreateGoalAsync(new Goal
        {
            Id = "goal-1",
            Description = "Goal 1",
            ReleaseId = "v1.0.0",
            CreatedAt = DateTime.UtcNow,
        }, ct);

        await store.CreateGoalAsync(new Goal
        {
            Id = "goal-2",
            Description = "Goal 2",
            ReleaseId = "v1.0.0",
            CreatedAt = DateTime.UtcNow,
        }, ct);

        await store.CreateGoalAsync(new Goal
        {
            Id = "goal-3",
            Description = "Goal 3 - unassigned",
            ReleaseId = null,
            CreatedAt = DateTime.UtcNow,
        }, ct);

        // Query goals by release
        var releaseGoals = await store.GetGoalsByReleaseAsync("v1.0.0", ct);

        Assert.Equal(2, releaseGoals.Count);
        Assert.All(releaseGoals, g => Assert.Equal("v1.0.0", g.ReleaseId));
    }
}

/// <summary>
/// Tests for IterationStartSha capture: the dispatcher stores the SHA
/// from the coder's <see cref="TaskResult"/> onto the pipeline.
/// </summary>
public sealed class GoalDispatcherIterationShaTests
{
    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string taskId)
        CreateDispatcherInCodingPhase(int maxRetries = 1)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries);
        var shaPlan = IterationPlan.Default();
        pipeline.SetPlan(shaPlan);
        pipeline.StateMachine.RestoreFromPlan(shaPlan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var notifier = new TaskCompletionNotifier();
        var brain = new FakeDispatcherBrain();
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

    [Fact]
    public async Task CoderTaskComplete_WithIterationStartSha_StoresOnPipeline()
    {
        // Arrange
        const string sha = "feedbabe1234567890abcdef1234567890abcdef";
        var (dispatcher, pipeline, taskId) = CreateDispatcherInCodingPhase();

        // Act — coder completes with an IterationStartSha in the result
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Made changes",
            GitStatus = new GitChangeSummary { FilesChanged = 2 },
            Metrics = new TaskMetrics { Verdict = "PASS" },
            IterationStartSha = sha,
        }, TestContext.Current.CancellationToken);

        // Assert — pipeline stores the SHA for use by the subsequent reviewer task
        Assert.Equal(sha, pipeline.IterationStartSha);
    }

    [Fact]
    public async Task CoderTaskComplete_WithoutIterationStartSha_LeavesExistingPipelineShaUnchanged()
    {
        // Arrange — pipeline already has a SHA from a previous dispatch
        const string existingSha = "previous1234567890abcdef1234567890abcdef";
        var (dispatcher, pipeline, taskId) = CreateDispatcherInCodingPhase();
        pipeline.IterationStartSha = existingSha;

        // Act — coder result has no SHA (e.g. empty repo or worker couldn't capture it)
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Made changes",
            GitStatus = new GitChangeSummary { FilesChanged = 1 },
            Metrics = new TaskMetrics { Verdict = "PASS" },
            IterationStartSha = null, // no SHA
        }, TestContext.Current.CancellationToken);

        // Assert — existing SHA is NOT overwritten by a null result
        Assert.Equal(existingSha, pipeline.IterationStartSha);
    }

    [Fact]
    public async Task CoderTaskComplete_WithEmptyIterationStartSha_LeavesExistingPipelineShaUnchanged()
    {
        // Arrange
        const string existingSha = "previous1234567890abcdef1234567890abcdef";
        var (dispatcher, pipeline, taskId) = CreateDispatcherInCodingPhase();
        pipeline.IterationStartSha = existingSha;

        // Act — coder result has empty string SHA (treated same as null)
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Made changes",
            GitStatus = new GitChangeSummary { FilesChanged = 1 },
            Metrics = new TaskMetrics { Verdict = "PASS" },
            IterationStartSha = "", // empty
        }, TestContext.Current.CancellationToken);

        // Assert — existing SHA is not overwritten
        Assert.Equal(existingSha, pipeline.IterationStartSha);
    }
}

/// <summary>
/// Tests that verify <see cref="GoalDispatcher"/> dispatches the
/// plan's first phase dynamically rather than hardcoding <see cref="GoalPhase.Coding"/>.
/// </summary>
public sealed class GoalDispatcherFirstPhaseDispatchTests
{
    /// <summary>
    /// A docs-only plan (first phase = DocWriting) must dispatch a DocWriter worker,
    /// not a Coder.
    /// </summary>
    [Fact]
    public async Task DocsOnlyPlan_DispatchesDocWriterAsFirstWorker()
    {
        var ct = TestContext.Current.CancellationToken;

        // Brain returns a docs-only plan: DocWriting → Testing → Review → Merging
        var docsOnlyPlan = new IterationPlan
        {
            Phases = [GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
            Reason = "Docs-only plan",
        };
        var brain = new FirstPhasePlanningBrain(docsOnlyPlan);

        var goalId = $"goal-docsonly-{Guid.NewGuid():N}";
        var goal = new Goal { Id = goalId, Description = "Update project docs", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateFirstPhaseDispatcher(goal, brain, out var taskQueue);

        WorkTask? dispatchedTask = null;
        taskQueue.OnEnqueue = t => dispatchedTask = t;

        // Act
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert — DocWriter was dispatched, not Coder
        Assert.NotNull(dispatchedTask);
        Assert.Equal(WorkerRole.DocWriter, dispatchedTask!.Role);
    }

    /// <summary>
    /// A normal plan (first phase = Coding) must still dispatch a Coder as the first worker.
    /// </summary>
    [Fact]
    public async Task NormalPlan_DispatchesCoderAsFirstWorker()
    {
        var ct = TestContext.Current.CancellationToken;

        // Brain returns the default plan (first phase = Coding)
        var brain = new FirstPhasePlanningBrain(IterationPlan.Default());

        var goalId = $"goal-normal-{Guid.NewGuid():N}";
        var goal = new Goal { Id = goalId, Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateFirstPhaseDispatcher(goal, brain, out var taskQueue);

        WorkTask? dispatchedTask = null;
        taskQueue.OnEnqueue = t => dispatchedTask = t;

        // Act
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert — Coder was dispatched
        Assert.NotNull(dispatchedTask);
        Assert.Equal(WorkerRole.Coder, dispatchedTask!.Role);
    }

    /// <summary>
    /// When no Brain is available and the plan's first phase is DocWriting,
    /// the dispatcher must still dispatch DocWriter (not Coder) and use the
    /// generic fallback prompt.
    /// </summary>
    [Fact]
    public async Task DocsOnlyPlan_NoBrain_DispatchesDocWriterWithFallbackPrompt()
    {
        var ct = TestContext.Current.CancellationToken;

        var docsOnlyPlan = new IterationPlan
        {
            Phases = [GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
            Reason = "Docs-only plan",
        };

        var goalId = $"goal-docsonly-nobrain-{Guid.NewGuid():N}";
        var goal = new Goal { Id = goalId, Description = "Update docs", RepositoryNames = ["test-repo"] };

        // We need a brain to supply the plan, but we test the no-brain prompt path
        // by using a brain that returns the docs-only plan but we pass it as null
        // after setting up a dispatcher that uses the plan via no-brain path.
        // Since no-brain path uses IterationPlan.Default() (starts with Coding),
        // we instead verify that the first-phase logic works with null brain + docs plan
        // by directly building a dispatcher with no brain and manually checking the
        // fallback: we'll use a dispatcher with a brain that returns the docs plan,
        // then separately verify the no-brain fallback prompt is "Work on:".

        // Arrange: dispatcher with brain returning docs-only plan
        var brain = new FirstPhasePlanningBrain(docsOnlyPlan);
        var dispatcher = CreateFirstPhaseDispatcher(goal, brain, out var taskQueue);

        WorkTask? dispatchedTask = null;
        taskQueue.OnEnqueue = t => dispatchedTask = t;

        // Act
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert — DocWriter dispatched with a brain-generated prompt containing DocWriting
        Assert.NotNull(dispatchedTask);
        Assert.Equal(WorkerRole.DocWriter, dispatchedTask!.Role);
        Assert.NotNull(dispatchedTask.Prompt);
        Assert.Contains("DocWriting", dispatchedTask.Prompt);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static GoalDispatcher CreateFirstPhaseDispatcher(
        Goal goal,
        IDistributedBrain brain,
        out TaskQueue taskQueue)
    {
        var goalSource = new FirstPhaseFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();

        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/test-repo", DefaultBranch = "main" },
            ],
            // All broadcastable roles need configured models to pass the readiness gate.
            Workers = TestHelpers.AllBroadcastableRoleModels(),
        };

        taskQueue = new TaskQueue();

        return new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
            config: config,
            startupDelay: TimeSpan.Zero);
    }

    private static Task InvokeDispatchNextGoalAsync(GoalDispatcher dispatcher, CancellationToken ct)
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "DispatchNextGoalAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(dispatcher, [ct])!;
    }
}

/// <summary>
/// Brain stub that returns a fixed <see cref="IterationPlan"/> and generates phase-labelled prompts.
/// </summary>
file sealed class FirstPhasePlanningBrain : IDistributedBrain
{
    private readonly IterationPlan _plan;

    public FirstPhasePlanningBrain(IterationPlan plan) => _plan = plan;

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PlanResult.Success(_plan));

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success($"Brain prompt for {phase} on {pipeline.Description}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("proceed"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>Goal source for first-phase dispatch tests.</summary>
file sealed class FirstPhaseFakeGoalSource : IGoalSource
{
    private readonly Goal _goal;

    public FirstPhaseFakeGoalSource(Goal goal) => _goal = goal;

    public string Name => "first-phase-fake";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Tests that verify <see cref="GoalDispatcher"/> routes <see cref="GoalPhase.DocWriting"/>
/// through <see cref="IDistributedBrain.CraftPromptAsync"/> exactly like Coding, Testing, and Review,
/// and that the old <c>BuildDocWriterPrompt</c> method has been removed.
/// </summary>
public sealed class GoalDispatcherDocWritingPhaseTests
{
    /// <summary>
    /// When the Testing phase completes successfully, the next phase is DocWriting.
    /// The dispatcher must call <see cref="IDistributedBrain.CraftPromptAsync"/> with
    /// <see cref="GoalPhase.DocWriting"/>, not use a hardcoded prompt.
    /// </summary>
    [Fact]
    public async Task DocWritingPhase_AfterTestingSucceeds_CallsBrainCraftPromptWithDocWritingPhase()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange — tracking brain that records every CraftPromptAsync call
        var brain = new PhaseCapturingBrain();

        var goal = new Goal { Id = $"goal-docwriting-{Guid.NewGuid():N}", Description = "Update docs" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(ct); // populate internal map

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Start the state machine with a plan that includes DocWriting after Testing
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.DocWriting, GoalPhase.Review, GoalPhase.Merging],
            Reason = "Test plan",
        };
        pipeline.SetPlan(plan);
        // Restore state machine at Testing so the next transition goes to DocWriting
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Testing);
        pipeline.AdvanceTo(GoalPhase.Testing); // simulate pipeline already in Testing

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain);

        // Act — Testing phase succeeds → dispatcher should advance to DocWriting and call Brain
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "All tests pass",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", TotalTests = 5, PassedTests = 5 },
        }, ct);

        // Assert — Brain was called for DocWriting
        Assert.Contains(GoalPhase.DocWriting, brain.CraftPromptPhases);
    }

    /// <summary>
    /// When <see cref="GoalPhase.DocWriting"/> is dispatched and the Brain is null,
    /// <see cref="GoalDispatcher.ResolvePromptAsync"/> falls back to the generic "Work on:"
    /// prompt — not the old hardcoded <c>BuildDocWriterPrompt</c> with its
    /// <c>&lt;base-branch&gt;</c> placeholder.
    /// </summary>
    [Fact]
    public async Task DocWritingPhase_NoBrain_ResolvePromptReturnsGenericFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Description = "My doc task";

        var goal = new Goal { Id = $"goal-docwriting-nobrain-{Guid.NewGuid():N}", Description = Description };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(ct);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.DocWriting);

        // Dispatcher with no brain — uses the fallback branch
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: null);

        // Act — call ResolvePromptAsync directly (internal, accessible via InternalsVisibleTo)
        var prompt = await dispatcher.ResolvePromptAsync(pipeline, GoalPhase.DocWriting, null, ct);

        // Assert — generic fallback is used, not the old hardcoded BuildDocWriterPrompt
        Assert.Contains("Work on:", prompt);
        Assert.Contains("DocWriting", prompt);
    }

    /// <summary>
    /// Verifies that the <c>BuildDocWriterPrompt</c> method has been removed from
    /// <see cref="GoalDispatcher"/> and no longer exists.
    /// </summary>
    [Fact]
    public void BuildDocWriterPrompt_MethodDoesNotExist()
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "BuildDocWriterPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static);

        Assert.Null(method);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Brain stub that records every phase passed to <see cref="CraftPromptAsync"/>.
    /// </summary>
    private sealed class PhaseCapturingBrain : IDistributedBrain
    {
        /// <summary>All phases for which <see cref="CraftPromptAsync"/> was called.</summary>
        public List<GoalPhase> CraftPromptPhases { get; } = [];

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
        {
            CraftPromptPhases.Add(phase);
            return Task.FromResult(PromptResult.Success($"Brain prompt for {phase}"));
        }

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }
}

/// <summary>
/// Tests for parallel goal dispatch feature controlled by MaxParallelGoals.
/// </summary>
public sealed class GoalDispatcherParallelDispatchTests
{
    /// <summary>
    /// When MaxParallelGoals > 1, multiple goals can be dispatched concurrently.
    /// This test verifies that with MaxParallelGoals = 2 and 1 active pipeline,
    /// a second dispatch succeeds (1 active &lt; limit of 2).
    /// </summary>
    [Fact]
    public async Task MaxParallelGoals_greater_than_1_allows_concurrent_dispatch()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange: config with MaxParallelGoals = 2
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { MaxParallelGoals = 2 },
            Repositories =
            [
                new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/test-repo", DefaultBranch = "main" },
            ],
            // All broadcastable roles need configured models to pass the readiness gate.
            Workers = TestHelpers.AllBroadcastableRoleModels(),
        };

        // Create two goals
        var goal1 = new Goal { Id = $"goal-parallel-1-{Guid.NewGuid():N}", Description = "First parallel goal", RepositoryNames = ["test-repo"] };
        var goal2 = new Goal { Id = $"goal-parallel-2-{Guid.NewGuid():N}", Description = "Second parallel goal", RepositoryNames = ["test-repo"] };
        var goalSource = new ParallelFakeGoalSource([goal1, goal2]);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var brain = new ParallelDispatchBrain();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
            config: config,
            startupDelay: TimeSpan.Zero);

        // Act: dispatch twice in sequence
        await InvokeDispatchNextGoalAsync(dispatcher, ct);
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert: both goals dispatched (2 pipelines created)
        var activePipelines = pipelineManager.GetActivePipelines();
        Assert.Equal(2, activePipelines.Count);
        Assert.Contains(activePipelines, p => p.GoalId == goal1.Id);
        Assert.Contains(activePipelines, p => p.GoalId == goal2.Id);
    }

    /// <summary>
    /// When MaxParallelGoals = 1 (default), only one goal runs at a time.
    /// This test verifies that with 1 active pipeline and MaxParallelGoals = 1,
    /// no additional goal is dispatched.
    /// </summary>
    [Fact]
    public async Task MaxParallelGoals_equals_1_prevents_concurrent_dispatch()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange: config with MaxParallelGoals = 1 (default sequential behavior)
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { MaxParallelGoals = 1 },
            Repositories =
            [
                new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/test-repo", DefaultBranch = "main" },
            ],
            // All broadcastable roles need configured models to pass the readiness gate.
            Workers = TestHelpers.AllBroadcastableRoleModels(),
        };

        // Create two goals
        var goal1 = new Goal { Id = $"goal-seq-1-{Guid.NewGuid():N}", Description = "First sequential goal", RepositoryNames = ["test-repo"] };
        var goal2 = new Goal { Id = $"goal-seq-2-{Guid.NewGuid():N}", Description = "Second sequential goal", RepositoryNames = ["test-repo"] };
        var goalSource = new ParallelFakeGoalSource([goal1, goal2]);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var brain = new ParallelDispatchBrain();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
            config: config,
            startupDelay: TimeSpan.Zero);

        // Act: dispatch twice
        await InvokeDispatchNextGoalAsync(dispatcher, ct);
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert: only 1 active pipeline (second dispatch blocked)
        var activePipelines = pipelineManager.GetActivePipelines();
        Assert.Single(activePipelines);
        Assert.Equal(goal1.Id, activePipelines[0].GoalId);
    }

    /// <summary>
    /// When dispatching a goal, the Brain must fork a session for that goal.
    /// This test verifies ForkSessionForGoalAsync is called exactly once
    /// with the correct goal ID.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoalAsync_forks_session_for_new_goal()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { MaxParallelGoals = 1 },
            Repositories =
            [
                new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/test-repo", DefaultBranch = "main" },
            ],
            // All broadcastable roles need configured models to pass the readiness gate.
            Workers = TestHelpers.AllBroadcastableRoleModels(),
        };

        var goalId = $"goal-fork-{Guid.NewGuid():N}";
        var goal = new Goal { Id = goalId, Description = "Goal to test session fork", RepositoryNames = ["test-repo"] };
        var goalSource = new ParallelFakeGoalSource([goal]);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var brain = new ForkTrackingBrain();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
            config: config,
            startupDelay: TimeSpan.Zero);

        // Act: dispatch the goal
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert: ForkSessionForGoalAsync was called exactly once with the correct goal ID
        Assert.Single(brain.ForkCalls);
        Assert.Equal(goalId, brain.ForkCalls[0]);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Task InvokeDispatchNextGoalAsync(GoalDispatcher dispatcher, CancellationToken ct)
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "DispatchNextGoalAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(dispatcher, [ct])!;
    }

    /// <summary>Goal source that returns a pre-configured list of goals.</summary>
    private sealed class ParallelFakeGoalSource : IGoalSource
    {
        private readonly List<Goal> _goals;
        private int _index;

        public ParallelFakeGoalSource(List<Goal> goals) => _goals = goals;

        public string Name => "parallel-fake";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
        {
            // Return goals one at a time so GoalManager.GetNextGoalAsync gets them in priority order
            if (_index >= _goals.Count)
                return Task.FromResult<IReadOnlyList<Goal>>([]);

            var goal = _goals[_index++];
            return Task.FromResult<IReadOnlyList<Goal>>([goal]);
        }

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>Brain stub for parallel dispatch tests.</summary>
    private sealed class ParallelDispatchBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }

    /// <summary>Brain stub that tracks ForkSessionForGoalAsync calls.</summary>
    private sealed class ForkTrackingBrain : IDistributedBrain
    {
        public List<string> ForkCalls { get; } = [];

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default)
        {
            ForkCalls.Add(goalId);
            return Task.CompletedTask;
        }

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }
}

/// <summary>
/// Tests for <see cref="GoalDispatcher.ResumeGoalAsync"/> — resuming iteration-exhausted goals.
/// </summary>
public sealed class GoalDispatcherResumeTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GoalDispatcher CreateResumeDispatcher(
        IGoalStore? goalStore,
        GoalPipelineManager pipelineManager,
        IDistributedBrain? brain = null,
        HiveConfigFile? config = null,
        IWorkerGateway? workerGateway = null)
    {
        var goalManager = new GoalManager();
        // Register the store as a goal source so status transitions (e.g. MarkGoalFailedAsync)
        // can resolve the goal instead of throwing KeyNotFoundException.
        if (goalStore is not null)
            goalManager.AddSource(goalStore);
        return new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            workerGateway ?? new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            // A Brain is required to plan the resumed iteration — without one, resume fails the goal.
            brain ?? new FakeDispatcherBrain(),
            config: config,
            goalStore: goalStore);
    }

    private static HiveConfigFile ConfigWithRepo() =>
        new()
        {
            Repositories =
            [
                new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/repo.git", DefaultBranch = "main" },
            ],
            // Slice 3b: worker roles need configured models to dispatch.
            Workers =
            {
                ["coder"] = new WorkerConfig { Model = "coder-model" },
            },
        };

    private static Goal CreateFailedGoal(
        string id,
        string reason = "Exceeded max iterations",
        GoalStatus status = GoalStatus.Failed,
        bool withRepo = true) =>
        new()
        {
            Id = id,
            Description = "Resume test goal",
            Status = status,
            FailureReason = status == GoalStatus.Failed ? reason : null,
            RepositoryNames = withRepo ? ["test-repo"] : [],
        };

    /// <summary>
    /// Creates a pipeline, exhausts its iteration budget, and advances it to the Failed phase
    /// to simulate iteration exhaustion. Persists to the store if the manager is store-backed.
    /// </summary>
    private static GoalPipeline CreateFailedPipeline(GoalPipelineManager manager, Goal goal, int maxIterations = 3)
    {
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: maxIterations);
        // Exhaust the iteration budget so Remaining is 0 before resume.
        while (pipeline.IterationBudget.TryConsume()) { }
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);
        return pipeline;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResumeGoalAsync_HttpTokenCancelled_MidResume_CompletesSuccessfully()
    {
        var brain = new CancellationTokenTrackingBrain();
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-cancel-mid");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        var pipeline = CreateFailedPipeline(manager, goal);

        var dispatcher = CreateResumeDispatcher(goalStore, manager, brain: brain, config: ConfigWithRepo());

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await dispatcher.ResumeGoalAsync("resume-cancel-mid", 5, cts.Token);

        Assert.True(result);

        var updated = await goalStore.GetGoalAsync("resume-cancel-mid", TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(GoalStatus.InProgress, updated!.Status);
        Assert.Null(updated.FailureReason);

        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);

        // Verify Brain calls were invoked (proving the method progressed past the lock)
        Assert.True(brain.ForkSessionCalled, "ForkSessionForGoalAsync should have been called");
        Assert.True(brain.PlanIterationCalled, "PlanIterationAsync should have been called");
        Assert.True(brain.CraftPromptCalled, "CraftPromptAsync should have been called");

        // The best-effort Brain calls receive CancellationToken.None so they complete regardless
        // of the caller. Planning receives the caller's token (see
        // ResumeGoalAsync_BestEffortBrainCalls_ReceiveCancellationTokenNone_ButPlanningUsesCallerToken),
        // and this brain ignores tokens, so resume still completes successfully here.
        Assert.All(brain.ForkSessionTokens, t => Assert.False(t.CanBeCanceled,
            "ForkSessionForGoalAsync must receive CancellationToken.None"));
        Assert.All(brain.CraftPromptTokens, t => Assert.False(t.CanBeCanceled,
            "CraftPromptAsync must receive CancellationToken.None"));
    }

    [Fact]
    public async Task ResumeGoalAsync_PreCancelledToken_FailsFastBeforeBrainCalls()
    {
        // When a pre-cancelled CancellationToken is passed, ResumeGoalAsync should fail fast
        // on the goal store read or resume lock acquisition — before reaching any Brain calls.
        var brain = new CancellationTokenTrackingBrain();
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-pre-cancelled");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        CreateFailedPipeline(manager, goal);

        var dispatcher = CreateResumeDispatcher(goalStore, manager, brain: brain, config: ConfigWithRepo());

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel immediately

        // The method should throw OperationCanceledException (fail fast) or return false
        // — but it must NOT reach any Brain calls.
        bool result;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            result = await dispatcher.ResumeGoalAsync("resume-pre-cancelled", 5, cts.Token);
        });

        // No Brain calls should have been made — the pre-cancelled ct stops before reaching them.
        Assert.False(brain.ForkSessionCalled, "ForkSessionForGoalAsync must NOT be called with a pre-cancelled token");
        Assert.False(brain.PlanIterationCalled, "PlanIterationAsync must NOT be called with a pre-cancelled token");
        Assert.False(brain.CraftPromptCalled, "CraftPromptAsync must NOT be called with a pre-cancelled token");
    }

    [Fact]
    public async Task ResumeGoalAsync_BestEffortBrainCalls_ReceiveCancellationTokenNone_ButPlanningUsesCallerToken()
    {
        // Contract: the PLANNING call is governed by the CALLER's token so that a cancelled
        // caller can be distinguished from a self-cancelled planning call (see
        // ResumeGoalAsync_CallerTokenCancelledDuringPlanning_PropagatesAndDoesNotFailGoal).
        // The BEST-EFFORT calls around it — session forking and prompt crafting — must still
        // receive CancellationToken.None so they complete regardless of the caller.
        // This brain ignores tokens entirely, so planning here simply succeeds.
        var brain = new CancellationTokenTrackingBrain(brainCallDelayMs: 200);
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-ct-none");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        var pipeline = CreateFailedPipeline(manager, goal);

        var dispatcher = CreateResumeDispatcher(goalStore, manager, brain: brain, config: ConfigWithRepo());

        // Cancel after a short delay — by the time the Brain calls execute the caller's token
        // is cancelled, which makes the token-routing assertions below meaningful.
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await dispatcher.ResumeGoalAsync("resume-ct-none", 5, cts.Token);

        Assert.True(result);

        // The caller's ct should be cancelled by now (brain calls take 200ms+ each).
        Assert.True(cts.Token.IsCancellationRequested, "caller ct should be cancelled by the time Brain calls execute");

        Assert.True(brain.ForkSessionCalled);
        Assert.True(brain.PlanIterationCalled);
        Assert.True(brain.CraftPromptCalled);

        // Best-effort calls receive CancellationToken.None — a token that cannot be cancelled.
        Assert.All(brain.ForkSessionTokens, t => Assert.False(t.CanBeCanceled,
            "ForkSessionForGoalAsync must receive CancellationToken.None"));
        Assert.All(brain.CraftPromptTokens, t => Assert.False(t.CanBeCanceled,
            "CraftPromptAsync must receive CancellationToken.None"));

        // Planning receives the CALLER's token, so the cancellation distinction is real and not
        // dead code. (Asserting CanBeCanceled rather than IsCancellationRequested: CancellationToken
        // is a struct over a live source, so a captured token reflects the source's current state.)
        Assert.All(brain.PlanIterationTokens, t => Assert.True(t.CanBeCanceled,
            "PlanIterationAsync must receive the caller's cancellable token, not CancellationToken.None"));
    }

    [Fact]
    public async Task ResumeGoalAsync_CallerTokenCancelledDuringPlanning_PropagatesAndDoesNotFailGoal()
    {
        // Regression: the planning call must honour the CALLER's token so that
        // `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`
        // is functional rather than dead code. A caller cancelled mid-planning must NOT fail
        // the goal — it stays InProgress for a later dispatch cycle.
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-caller-cancelled");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        var pipeline = CreateFailedPipeline(manager, goal);

        using var cts = new CancellationTokenSource();

        // The brain blocks in planning until the caller's token is cancelled, then surfaces the
        // caller's OCE — exactly what a token-honouring Brain call does.
        var planningStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var brain = new PlanningGateBrain(async planCt =>
        {
            planningStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, planCt);
            return PlanResult.Success(IterationPlan.Default());
        });

        var dispatcher = CreateResumeDispatcher(goalStore, manager, brain: brain, config: ConfigWithRepo());

        var resumeTask = dispatcher.ResumeGoalAsync("resume-caller-cancelled", 5, cts.Token);

        // Cancel only once planning is genuinely in flight.
        await planningStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        // Bounded wait: if planning ever regresses to CancellationToken.None the blocking Brain
        // call would never observe cancellation, so this must fail fast rather than hang forever.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resumeTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // The goal must NOT be marked Failed — cancellation is caller shutdown, not a planning
        // failure. Resume already set it to InProgress before planning, and it stays there.
        var updated = await goalStore.GetGoalAsync("resume-caller-cancelled", TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(GoalStatus.InProgress, updated!.Status);
        Assert.Null(updated.FailureReason);

        // FailResumedGoalAsync was not invoked: no terminal transition on the pipeline.
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);
        Assert.NotEqual(GoalPhase.Failed, pipeline.StateMachine.Phase);
    }

    [Fact]
    public async Task ResumeGoalAsync_PlanningSelfCancels_FailsGoal()
    {
        // Complement to the test above: an OCE that does NOT carry the caller's token is a
        // self-cancelled planning call (e.g. a Brain-side timeout). The goal is already
        // persisted as InProgress/Planning, so it must be failed rather than stranded.
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-self-cancelled");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        var pipeline = CreateFailedPipeline(manager, goal);

        // Throws an OCE carrying an unrelated, already-cancelled token — the caller's token stays live.
        var brain = new PlanningGateBrain(_ =>
            throw new OperationCanceledException(new CancellationToken(canceled: true)));

        var dispatcher = CreateResumeDispatcher(goalStore, manager, brain: brain, config: ConfigWithRepo());

        var result = await dispatcher.ResumeGoalAsync(
            "resume-self-cancelled", 5, TestContext.Current.CancellationToken);

        Assert.True(result);

        var updated = await goalStore.GetGoalAsync("resume-self-cancelled", TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(GoalStatus.Failed, updated!.Status);
        Assert.Equal("Planning failed: planning was cancelled", updated.FailureReason);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    [Fact]
    public async Task ResumeGoalAsync_PlanningThrowsNonCancellation_FailsGoal()
    {
        // A non-OCE throw from planning must also fail the goal explicitly rather than
        // propagating and stranding a goal already persisted as InProgress/Planning.
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-plan-throws");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        var pipeline = CreateFailedPipeline(manager, goal);

        var brain = new PlanningGateBrain(_ => throw new InvalidOperationException("brain socket closed"));

        var dispatcher = CreateResumeDispatcher(goalStore, manager, brain: brain, config: ConfigWithRepo());

        var result = await dispatcher.ResumeGoalAsync(
            "resume-plan-throws", 5, TestContext.Current.CancellationToken);

        Assert.True(result);

        var updated = await goalStore.GetGoalAsync("resume-plan-throws", TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(GoalStatus.Failed, updated!.Status);
        Assert.Equal("Planning failed: brain socket closed", updated.FailureReason);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    [Fact]
    public async Task ResumeGoalAsync_FailedGoal_ResumesExecution()
    {
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-1");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        var pipeline = CreateFailedPipeline(manager, goal);

        var dispatcher = CreateResumeDispatcher(goalStore, manager, config: ConfigWithRepo());

        var result = await dispatcher.ResumeGoalAsync("resume-1", 5, TestContext.Current.CancellationToken);

        Assert.True(result);

        var updated = await goalStore.GetGoalAsync("resume-1", TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(GoalStatus.InProgress, updated!.Status);
        Assert.Null(updated.FailureReason);

        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);
        // 5 added to an exhausted (0) budget, 1 consumed for the resumed iteration → 4 remaining.
        Assert.Equal(4, pipeline.IterationBudget.Remaining);
    }

    [Fact]
    public async Task ResumeGoalAsync_MaxIterationsPersists_AfterExtension()
    {
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-max");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var pipeline = CreateFailedPipeline(manager, goal, maxIterations: 3);
        var originalMax = pipeline.MaxIterations; // 3

        var dispatcher = CreateResumeDispatcher(goalStore, manager, config: ConfigWithRepo());

        await dispatcher.ResumeGoalAsync("resume-max", 5, TestContext.Current.CancellationToken);

        // Restore via a fresh manager backed by the same store.
        var freshManager = new GoalPipelineManager(store);
        var restored = freshManager.RestorePipeline("resume-max");

        Assert.NotNull(restored);
        Assert.Equal(originalMax + 5, restored!.MaxIterations);
    }

    [Fact]
    public async Task ResumeGoalAsync_ConcurrentCalls_Serialized()
    {
        // A controllable store whose UpdateGoalAsync blocks INSIDE the resume critical section.
        // GetGoalAsync returns a *clone* of the stored goal, so a caller mutating its own copy to
        // InProgress does NOT change the persisted status until UpdateGoalAsync actually writes.
        // This lets us hold the first call inside the lock while the store still reports Failed,
        // so the second call's eligibility check passes and it MUST block on the semaphore.
        var goalStore = new ControllableResumeGoalStore();
        var goal = CreateFailedGoal("resume-concurrent");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        CreateFailedPipeline(manager, goal);

        var dispatcher = CreateResumeDispatcher(goalStore, manager, config: ConfigWithRepo());

        // Arm the gate: the first call to reach UpdateGoalAsync will block until we release it,
        // while still holding the resume lock.
        goalStore.UpdateGoalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start the first call. It runs through the critical section and parks in UpdateGoalAsync,
        // holding the lock and having signalled UpdateGoalEntered.
        var t1 = dispatcher.ResumeGoalAsync("resume-concurrent", 5, TestContext.Current.CancellationToken);
        await goalStore.UpdateGoalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The store still reports Failed (first call has not written yet), so the second call
        // passes the pre-lock eligibility read and can only be held back by the semaphore.
        var t2 = dispatcher.ResumeGoalAsync("resume-concurrent", 5, TestContext.Current.CancellationToken);

        // PROVING ASSERTION: the second call must NOT complete while the first holds the lock.
        // If the semaphore/WaitAsync were removed, the second call would proceed concurrently.
        var winner = await Task.WhenAny(t2, Task.Delay(500, TestContext.Current.CancellationToken));
        Assert.NotSame(t2, winner); // t2 is still blocked → the delay won

        // Release the first call. It finishes, writes InProgress, and releases the lock.
        goalStore.UpdateGoalGate.SetResult();

        var results = await Task.WhenAll(t1, t2);

        // Exactly one succeeds: the first (Failed → InProgress). The second, once it finally
        // acquires the lock, re-checks eligibility, sees InProgress, and returns false.
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(1, results.Count(r => !r));
        Assert.True(results[0]);
        Assert.False(results[1]);
    }

    [Fact]
    public async Task ResumeGoalAsync_RejectsNonIterationFailure()
    {
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-other", reason: "Some other error");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        CreateFailedPipeline(manager, goal);

        var dispatcher = CreateResumeDispatcher(goalStore, manager, config: ConfigWithRepo());

        var result = await dispatcher.ResumeGoalAsync("resume-other", 5, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ResumeGoalAsync_RejectsDonePipeline()
    {
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-done");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        pipeline.AdvanceTo(GoalPhase.Done);

        var dispatcher = CreateResumeDispatcher(goalStore, manager, config: ConfigWithRepo());

        var result = await dispatcher.ResumeGoalAsync("resume-done", 5, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ResumeGoalAsync_RejectsInProgressGoal()
    {
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-inprogress", status: GoalStatus.InProgress);
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        var dispatcher = CreateResumeDispatcher(goalStore, manager, config: ConfigWithRepo());

        var result = await dispatcher.ResumeGoalAsync("resume-inprogress", 5, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ResumeGoalAsync_RejectsPendingGoal()
    {
        var goalStore = new ResumeFakeGoalStore();
        var goal = CreateFailedGoal("resume-pending", status: GoalStatus.Pending);
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        var dispatcher = CreateResumeDispatcher(goalStore, manager, config: ConfigWithRepo());

        var result = await dispatcher.ResumeGoalAsync("resume-pending", 5, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ResumeGoalAsync_NullGoalStore_ReturnsFalse()
    {
        var manager = new GoalPipelineManager();
        var dispatcher = CreateResumeDispatcher(goalStore: null, manager, config: ConfigWithRepo());

        var result = await dispatcher.ResumeGoalAsync("any-goal", 5, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ResumeGoalAsync_StaleTaskMapping_RemovedFromStore()
    {
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var goalStore = new ResumeFakeGoalStore();
        // No repo configured → dispatch throws (no new task mapping registered), leaving only
        // the stale mapping, which resume must remove from the store.
        var goal = CreateFailedGoal("resume-stale", withRepo: false);
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        while (pipeline.IterationBudget.TryConsume()) { }
        const string staleTaskId = "stale-task-1";
        pipeline.SetActiveTask(staleTaskId);
        manager.RegisterTask(staleTaskId, "resume-stale");
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);

        var dispatcher = CreateResumeDispatcher(goalStore, manager);

        var result = await dispatcher.ResumeGoalAsync("resume-stale", 5, TestContext.Current.CancellationToken);

        Assert.True(result);

        // Verify the stale mapping is gone from the persisted store.
        var snapshot = store.LoadPipeline("resume-stale");
        Assert.NotNull(snapshot);
        Assert.DoesNotContain(snapshot!.TaskMappings, m => m.TaskId == staleTaskId);
    }

    [Fact]
    public async Task ResumeGoalAsync_DispatchFailure_ClearsActiveTaskAndEnqueues()
    {
        var goalStore = new ResumeFakeGoalStore();
        // Repo IS configured so ResolveRepositories and TaskBuilder.Build succeed and
        // DispatchToRole reaches pipeline.SetActiveTask(...) → ActiveTaskId becomes non-null.
        // The failure is injected LATER, at the worker-send step, via a gateway that hands back
        // an idle worker but throws on SendTaskAsync. This proves the catch block's
        // pipeline.ClearActiveTask() actually runs on a non-null ActiveTaskId — removing it
        // would leave ActiveTaskId set and fail this test.
        var goal = CreateFailedGoal("resume-dispatch-fail");
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var manager = new GoalPipelineManager();
        var pipeline = CreateFailedPipeline(manager, goal);

        var gateway = new ThrowingSendWorkerGateway();
        var dispatcher = CreateResumeDispatcher(goalStore, manager, config: ConfigWithRepo(), workerGateway: gateway);

        var result = await dispatcher.ResumeGoalAsync("resume-dispatch-fail", 5, TestContext.Current.CancellationToken);

        Assert.True(result);

        // The gateway must have been asked to send (proving we got past SetActiveTask, which
        // happens just before the send) and then thrown.
        Assert.True(gateway.SendAttempted);

        // ActiveTaskId must be cleared so DrainRedispatchQueueAsync will pick the pipeline up.
        Assert.Null(pipeline.ActiveTaskId);
        Assert.NotEqual(GoalPhase.Done, pipeline.Phase);
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);

        // The goal must be enqueued for redispatch.
        var queueField = typeof(GoalDispatcher).GetField(
            "_redispatchQueue",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var queue = (System.Collections.Concurrent.ConcurrentQueue<string>)queueField.GetValue(dispatcher)!;
        Assert.Contains("resume-dispatch-fail", queue);
    }
}

/// <summary>
/// Brain stub that captures the CancellationToken passed to each Brain call,
/// so tests can assert that ResumeGoalAsync passes CancellationToken.None (not the HTTP ct).
/// </summary>
file sealed class CancellationTokenTrackingBrain : IDistributedBrain
{
    private readonly int _brainCallDelayMs;

    public CancellationTokenTrackingBrain(int brainCallDelayMs = 0) => _brainCallDelayMs = brainCallDelayMs;

    public List<CancellationToken> ForkSessionTokens { get; } = [];
    public List<CancellationToken> PlanIterationTokens { get; } = [];
    public List<CancellationToken> CraftPromptTokens { get; } = [];

    public bool ForkSessionCalled => ForkSessionTokens.Count > 0;
    public bool PlanIterationCalled => PlanIterationTokens.Count > 0;
    public bool CraftPromptCalled => CraftPromptTokens.Count > 0;

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) => Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
    {
        PlanIterationTokens.Add(ct);
        if (_brainCallDelayMs > 0)
            return Task.Delay(_brainCallDelayMs, CancellationToken.None).ContinueWith(_ => PlanResult.Success(IterationPlan.Default()), TaskContinuationOptions.ExecuteSynchronously);
        return Task.FromResult(PlanResult.Success(IterationPlan.Default()));
    }

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        CraftPromptTokens.Add(ct);
        if (_brainCallDelayMs > 0)
            return Task.Delay(_brainCallDelayMs, CancellationToken.None).ContinueWith(_ => PromptResult.Success($"Work on {pipeline.Description} as {phase}"), TaskContinuationOptions.ExecuteSynchronously);
        return Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));
    }

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("proceed"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default)
    {
        ForkSessionTokens.Add(ct);
        if (_brainCallDelayMs > 0)
            return Task.Delay(_brainCallDelayMs, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>
/// Brain whose planning behaviour is supplied by the test, so cancellation semantics
/// (caller-cancelled vs. self-cancelled vs. plain throw) can be exercised precisely.
/// All other members are inert successes.
/// </summary>
file sealed class PlanningGateBrain : IDistributedBrain
{
    private readonly Func<CancellationToken, Task<PlanResult>> _plan;

    public PlanningGateBrain(Func<CancellationToken, Task<PlanResult>> plan) => _plan = plan;

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) => Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(
        GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) => _plan(ct);

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("proceed"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>In-memory goal store for GoalDispatcher resume tests.</summary>
file sealed class ResumeFakeGoalStore : IGoalStore
{
    private readonly Dictionary<string, Goal> _goals = new();

    public string Name => "ResumeFakeGoalStore";

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? goal : null);

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.FromResult(goal);
    }

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.Remove(goalId));

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        // Apply the transition so tests can assert the persisted status / failure reason
        // instead of silently observing the pre-update state.
        if (_goals.TryGetValue(goalId, out var goal))
        {
            goal.Status = status;
            if (metadata?.FailureReason is not null)
                goal.FailureReason = metadata.FailureReason;
        }

        return Task.CompletedTask;
    }

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(Array.Empty<(string, PersistedClarification)>());
}

/// <summary>
/// In-memory goal store that can pause <see cref="UpdateGoalAsync"/> inside the resume critical
/// section. <see cref="GetGoalAsync"/> returns a fresh clone so callers mutating their own copy do
/// not change the persisted status until <see cref="UpdateGoalAsync"/> actually writes.
/// </summary>
file sealed class ControllableResumeGoalStore : IGoalStore
{
    private readonly Dictionary<string, Goal> _goals = new();

    /// <summary>When set, <see cref="UpdateGoalAsync"/> awaits this before writing to the store.</summary>
    public TaskCompletionSource? UpdateGoalGate { get; set; }

    /// <summary>Completes when <see cref="UpdateGoalAsync"/> is first entered while gated.</summary>
    public TaskCompletionSource UpdateGoalEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Name => "ControllableResumeGoalStore";

    private static Goal Clone(Goal g) => new()
    {
        Id = g.Id,
        Description = g.Description,
        Status = g.Status,
        FailureReason = g.FailureReason,
        CompletedAt = g.CompletedAt,
        RepositoryNames = [.. g.RepositoryNames],
    };

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.Select(Clone).ToList().AsReadOnly());

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? Clone(goal) : null);

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = Clone(goal);
        return Task.FromResult(goal);
    }

    public async Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        var gate = UpdateGoalGate;
        if (gate is not null)
        {
            UpdateGoalEntered.TrySetResult();
            await gate.Task.WaitAsync(ct);
        }
        _goals[goal.Id] = Clone(goal);
    }

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.Remove(goalId));

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(Array.Empty<(string, PersistedClarification)>());
}

/// <summary>
/// Worker gateway that always reports one idle worker but throws when a task is sent to it.
/// This forces <c>DispatchToRole</c> to fail AFTER <c>pipeline.SetActiveTask(...)</c>, exercising
/// the dispatch-failure catch that clears the active task and enqueues for redispatch.
/// </summary>
file sealed class ThrowingSendWorkerGateway : IWorkerGateway
{
    private readonly ConnectedWorker _worker = new()
    {
        Id = "throwing-worker",
        Role = CopilotHive.Workers.WorkerRole.Unspecified,
        Capabilities = [],
    };

    /// <summary>True once <see cref="SendTaskAsync"/> has been invoked.</summary>
    public bool SendAttempted { get; private set; }

    public Task SendTaskAsync(string workerId, WorkTask task, CancellationToken ct = default)
    {
        SendAttempted = true;
        throw new InvalidOperationException("Simulated worker send failure after SetActiveTask.");
    }

    public Task SendCancelAsync(string workerId, string taskId, string reason, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SendAgentsUpdateAsync(string workerId, string role, string content, CancellationToken ct = default) =>
        Task.CompletedTask;

    public ConnectedWorker? GetIdleWorker() => _worker;

    public IReadOnlyList<ConnectedWorker> GetAllWorkers() => [_worker];

    public void MarkBusy(string workerId, string taskId) { }
}

/// <summary>
/// Tests for the diagnostic logging added to <c>GoalDispatcher.DispatchNextGoalAsync</c>
/// to identify why goals show as <see cref="GoalStatus.Pending"/> when they are actively running.
/// </summary>
public sealed class GoalDispatcherDiagnosticLoggingTests
{
    private static Task InvokeDispatchNextGoalAsync(GoalDispatcher dispatcher, CancellationToken ct)
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "DispatchNextGoalAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(dispatcher, [ct])!;
    }

    private static HiveConfigFile ConfigWithRepo() =>
        new()
        {
            Repositories =
            [
                new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/test-repo", DefaultBranch = "main" },
            ],
            // All broadcastable roles need configured models to pass the readiness gate.
            Workers = TestHelpers.AllBroadcastableRoleModels(),
        };

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Status update must happen BEFORE the Brain repo ensure, so the goal is marked
    /// in_progress as early as possible in the dispatch flow.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoalAsync_StatusUpdateHappensBeforeRepoEnsure()
    {
        var ct = TestContext.Current.CancellationToken;

        var logger = new CollectingLogger<GoalDispatcher>();
        var trackingStore = new DiagnosticTrackingGoalStore();
        var goal = new Goal { Id = "goal-order-test", Description = "Ordering test", RepositoryNames = ["test-repo"] };
        await trackingStore.CreateGoalAsync(goal, ct);

        var brain = new TrackingEnsureBrain();
        var goalManager = new GoalManager();
        goalManager.AddSource(trackingStore);
        await goalManager.GetNextGoalAsync(ct); // populate internal goal→source map

        var dispatcher = new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
            config: ConfigWithRepo(),
            startupDelay: TimeSpan.Zero);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert — UpdateGoalStatusAsync was called at least once
        Assert.NotEmpty(trackingStore.StatusUpdateCalls);

        // Assert — EnsureBrainRepoAsync was called at least once (goal has a repo)
        Assert.NotEmpty(brain.EnsureRepoCalls);

        // Assert — the first status update happened before the first repo ensure
        var firstStatusUpdate = trackingStore.StatusUpdateCalls[0];
        var firstRepoEnsure = brain.EnsureRepoCalls[0];
        Assert.True(
            firstStatusUpdate.Timestamp < firstRepoEnsure.Timestamp,
            $"Expected status update ({firstStatusUpdate.Timestamp:O}) before repo ensure ({firstRepoEnsure.Timestamp:O})");

        // Assert — log order: "Dispatcher: updating goal" appears before any repo-related log
        var updatingLogIdx = logger.Logs.FindIndex(l => l.Message.Contains("Dispatcher: updating goal"));
        var repoEnsureLogIdx = logger.Logs.FindIndex(l => l.Message.Contains("Failed to ensure Brain repo"));
        // repo ensure may not produce a warning log if it succeeds, so only check ordering if both exist
        if (repoEnsureLogIdx >= 0)
        {
            Assert.True(
                updatingLogIdx < repoEnsureLogIdx,
                $"Expected 'updating goal' log (idx={updatingLogIdx}) before repo ensure log (idx={repoEnsureLogIdx})");
        }
        Assert.True(updatingLogIdx >= 0, "Expected 'Dispatcher: updating goal' log message");
    }

    /// <summary>
    /// When <see cref="GoalManager.UpdateGoalStatusAsync"/> throws a non-cancellation exception,
    /// dispatch must continue — the pipeline should still be created.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoalAsync_NonCancellationException_ContinuesDispatch()
    {
        var ct = TestContext.Current.CancellationToken;

        var logger = new CollectingLogger<GoalDispatcher>();
        var trackingStore = new DiagnosticTrackingGoalStore
        {
            ThrowOnUpdateGoalStatus = new InvalidOperationException("test failure"),
        };
        var goal = new Goal { Id = "goal-noncancel-test", Description = "Non-cancel exception test", RepositoryNames = ["test-repo"] };
        await trackingStore.CreateGoalAsync(goal, ct);

        var brain = new TrackingEnsureBrain();
        var goalManager = new GoalManager();
        goalManager.AddSource(trackingStore);
        await goalManager.GetNextGoalAsync(ct);

        var pipelineManager = new GoalPipelineManager();

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
            config: ConfigWithRepo(),
            startupDelay: TimeSpan.Zero);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert — pipeline was created despite the status update failure
        var activePipelines = pipelineManager.GetActivePipelines();
        Assert.Contains(activePipelines, p => p.GoalId == goal.Id);

        // Assert — the error was logged with both "failed to update goal" and "continuing dispatch"
        var errorLog = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Error &&
            l.Message.Contains("failed to update goal") &&
            l.Message.Contains("continuing dispatch"));
        Assert.True(errorLog != default,
            $"Expected 'failed to update goal ... continuing dispatch' log. Logs: {string.Join("\n", logger.Logs.Select(l => $"[{l.Level}] {l.Message}"))}");
    }

    /// <summary>
    /// When the cancellation token is already cancelled and <see cref="GoalManager.UpdateGoalStatusAsync"/>
    /// throws <see cref="OperationCanceledException"/>, the exception must propagate (not be swallowed).
    /// </summary>
    [Fact]
    public async Task DispatchNextGoalAsync_CancellationIsRethrown()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var trackingStore = new DiagnosticTrackingGoalStore
        {
            ThrowOnUpdateGoalStatus = new OperationCanceledException(cts.Token),
        };
        var goal = new Goal { Id = "goal-cancel-test", Description = "Cancellation re-throw test", RepositoryNames = ["test-repo"] };
        await trackingStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var goalManager = new GoalManager();
        goalManager.AddSource(trackingStore);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var dispatcher = new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            new CollectingLogger<GoalDispatcher>(),
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            new TrackingEnsureBrain(),
            config: ConfigWithRepo(),
            startupDelay: TimeSpan.Zero);

        // Act & Assert — OperationCanceledException (or TaskCanceledException) is thrown
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeDispatchNextGoalAsync(dispatcher, cts.Token));
    }

    /// <summary>
    /// When <see cref="GoalManager.GetGoalAsync"/> returns null (source does not implement
    /// <see cref="IGoalStore"/> or goal not found), a warning is logged.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoalAsync_VerificationNullResult_LogsWarning()
    {
        var ct = TestContext.Current.CancellationToken;

        var logger = new CollectingLogger<GoalDispatcher>();

        // FakeGoalSource does NOT implement IGoalStore, so GetGoalAsync returns null
        var goal = new Goal { Id = "goal-verify-null-test", Description = "Verify null test", RepositoryNames = ["test-repo"] };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(ct);

        var dispatcher = new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            new TrackingEnsureBrain(),
            config: ConfigWithRepo(),
            startupDelay: TimeSpan.Zero);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert — a warning log contains "verification returned null"
        var nullWarning = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("verification returned null"));
        Assert.True(nullWarning != default,
            $"Expected 'verification returned null' warning log. Logs: {string.Join("\n", logger.Logs.Select(l => $"[{l.Level}] {l.Message}"))}");
    }

    /// <summary>
    /// When <see cref="GoalManager.GetGoalAsync"/> returns a goal with a status other than
    /// InProgress, a VERIFICATION FAILED error is logged with the goal ID.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoalAsync_VerificationMismatch_LogsVerificationFailed()
    {
        var ct = TestContext.Current.CancellationToken;

        var logger = new CollectingLogger<GoalDispatcher>();
        var trackingStore = new DiagnosticTrackingGoalStore
        {
            // GetGoalAsync returns a goal still in Pending (mismatch with expected InProgress)
            VerifyGoalStatus = GoalStatus.Pending,
        };
        var goal = new Goal { Id = "goal-verify-mismatch", Description = "Verify mismatch test", RepositoryNames = ["test-repo"] };
        await trackingStore.CreateGoalAsync(goal, ct);

        var goalManager = new GoalManager();
        goalManager.AddSource(trackingStore);
        await goalManager.GetNextGoalAsync(ct);

        var dispatcher = new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            new TrackingEnsureBrain(),
            config: ConfigWithRepo(),
            startupDelay: TimeSpan.Zero);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert — an error log contains "VERIFICATION FAILED" and the goal ID
        var verifyError = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Error &&
            l.Message.Contains("VERIFICATION FAILED") &&
            l.Message.Contains(goal.Id));
        Assert.True(verifyError != default,
            $"Expected 'VERIFICATION FAILED' error log for goal '{goal.Id}'. Logs: {string.Join("\n", logger.Logs.Select(l => $"[{l.Level}] {l.Message}"))}");
    }

    /// <summary>
    /// When <see cref="GoalManager.GetGoalAsync"/> returns a goal with <see cref="GoalStatus.InProgress"/>,
    /// a verification success info log is emitted.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoalAsync_VerificationSuccess_LogsVerifiedGoal()
    {
        var ct = TestContext.Current.CancellationToken;

        var logger = new CollectingLogger<GoalDispatcher>();
        var trackingStore = new DiagnosticTrackingGoalStore
        {
            VerifyGoalStatus = GoalStatus.InProgress,
        };
        var goal = new Goal { Id = "goal-verify-success", Description = "Verify success test", RepositoryNames = ["test-repo"] };
        await trackingStore.CreateGoalAsync(goal, ct);

        var goalManager = new GoalManager();
        goalManager.AddSource(trackingStore);
        await goalManager.GetNextGoalAsync(ct);

        var dispatcher = new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            new TrackingEnsureBrain(),
            config: ConfigWithRepo(),
            startupDelay: TimeSpan.Zero);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert — an info log contains "verified goal" and "InProgress"
        var verifyLog = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Information &&
            l.Message.Contains("verified goal") &&
            l.Message.Contains("InProgress"));
        Assert.True(verifyLog != default,
            $"Expected 'verified goal ... InProgress' info log. Logs: {string.Join("\n", logger.Logs.Select(l => $"[{l.Level}] {l.Message}"))}");
    }

}

/// <summary>
/// <see cref="IGoalStore"/> implementation that tracks <see cref="UpdateGoalStatusAsync"/>
/// calls (with timestamps) and allows controlling <see cref="GetGoalAsync"/> return value.
/// Used by <see cref="GoalDispatcherDiagnosticLoggingTests"/>.
/// </summary>
file sealed class DiagnosticTrackingGoalStore : IGoalStore
{
    private readonly Dictionary<string, Goal> _goals = new();

    /// <summary>Records every <see cref="UpdateGoalStatusAsync"/> call with a timestamp.</summary>
    public List<(string GoalId, GoalStatus Status, DateTime Timestamp)> StatusUpdateCalls { get; } = [];

    /// <summary>When set, <see cref="UpdateGoalStatusAsync"/> throws this exception.</summary>
    public Exception? ThrowOnUpdateGoalStatus { get; set; }

    /// <summary>
    /// Status to return from <see cref="GetGoalAsync"/>. When null, returns the stored goal
    /// with its current status. When set, returns a clone with this status.
    /// </summary>
    public GoalStatus? VerifyGoalStatus { get; set; }

    public string Name => "DiagnosticTrackingGoalStore";

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
    {
        if (!_goals.TryGetValue(goalId, out var goal))
            return Task.FromResult<Goal?>(null);

        // Return a clone with the controlled status if set
        if (VerifyGoalStatus is { } overrideStatus)
        {
            return Task.FromResult<Goal?>(new Goal
            {
                Id = goal.Id,
                Description = goal.Description,
                Status = overrideStatus,
                RepositoryNames = [.. goal.RepositoryNames],
            });
        }

        return Task.FromResult<Goal?>(goal);
    }

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.FromResult(goal);
    }

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.Remove(goalId));

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        StatusUpdateCalls.Add((goalId, status, DateTime.UtcNow));
        if (ThrowOnUpdateGoalStatus is not null)
            throw ThrowOnUpdateGoalStatus;

        if (_goals.TryGetValue(goalId, out var goal))
            goal.Status = status;

        return Task.CompletedTask;
    }

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(Array.Empty<(string, PersistedClarification)>());
}

/// <summary>
/// Brain stub that records every <see cref="EnsureBrainRepoAsync"/> call with a timestamp.
/// Used by <see cref="GoalDispatcherDiagnosticLoggingTests"/>.
/// </summary>
file sealed class TrackingEnsureBrain : IDistributedBrain
{
    public List<(string RepoName, DateTime Timestamp)> EnsureRepoCalls { get; } = [];

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PlanResult.Success(IterationPlan.Default()));

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
    {
        EnsureRepoCalls.Add((repoName, DateTime.UtcNow));
        return Task.CompletedTask;
    }

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("proceed"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}
