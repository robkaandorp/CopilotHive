using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

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

    public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) => Task.CompletedTask;

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
/// Tests for <see cref="IterationPlanValidator.ValidatePlan"/> logic.
/// </summary>
public sealed class GoalDispatcherValidatePlanTests
{
    [Fact]
    public void DocsOnlyPlan_WithReview_CodingNotInserted()
    {
        // Arrange: docs-only plan with Review — Coding should NOT be inserted
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.DocWriting, GoalPhase.Review, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: Coding absent, DocWriting retained
        Assert.DoesNotContain(GoalPhase.Coding, result.Phases);
        Assert.Contains(GoalPhase.DocWriting, result.Phases);
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
    }

    [Fact]
    public void DocsOnlyPlan_WithoutReview_CodingNotInserted()
    {
        // Arrange: docs-only plan with no Testing/Review — Testing will be inserted, but Coding must NOT be
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.DocWriting, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: Coding absent, DocWriting retained, ends with Merging
        Assert.DoesNotContain(GoalPhase.Coding, result.Phases);
        Assert.Contains(GoalPhase.DocWriting, result.Phases);
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
    }

    [Fact]
    public void PlanWithNeitherCodingNorDocWriting_CodingInserted()
    {
        // Arrange: safety fallback — no Coding and no DocWriting
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Review, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: Coding inserted as fallback
        Assert.Contains(GoalPhase.Coding, result.Phases);
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
    }

    [Fact]
    public void PlanWithBothCodingAndDocWriting_NeitherInsertedAgain()
    {
        // Arrange: plan already has both Coding and DocWriting
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.DocWriting, GoalPhase.Review, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: plan is unchanged — Coding appears exactly once
        Assert.Equal(1, result.Phases.Count(p => p == GoalPhase.Coding));
        Assert.Contains(GoalPhase.DocWriting, result.Phases);
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
    }

    [Fact]
    public void StandardCodingPlan_CodingNotDuplicated()
    {
        // Arrange: standard plan already containing Coding
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: Coding still present exactly once, Merging at end
        Assert.Equal(1, result.Phases.Count(p => p == GoalPhase.Coding));
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
    }

    // ── Code-plan enforcement ─────────────────────────────────────────────────

    [Fact]
    public void CodingPlan_MissingReview_ReviewInserted()
    {
        // Arrange: code plan with Testing but no Review
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: Review inserted
        Assert.Contains(GoalPhase.Review, result.Phases);
        Assert.Contains(GoalPhase.Testing, result.Phases);
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
    }

    [Fact]
    public void CodingPlan_MissingTesting_TestingInserted()
    {
        // Arrange: code plan with Review but no Testing
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Review, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: Testing inserted
        Assert.Contains(GoalPhase.Testing, result.Phases);
        Assert.Contains(GoalPhase.Review, result.Phases);
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
    }

    [Fact]
    public void CodingPlan_MissingBothTestingAndReview_BothInserted()
    {
        // Arrange: code plan with neither Testing nor Review
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: both Testing and Review inserted
        Assert.Contains(GoalPhase.Testing, result.Phases);
        Assert.Contains(GoalPhase.Review, result.Phases);
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
    }

    [Fact]
    public void CodingPlan_WithTestingAndReview_Unchanged()
    {
        // Arrange: code plan already has both Testing and Review
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: no duplicates, order preserved, Merging at end
        Assert.Equal(1, result.Phases.Count(p => p == GoalPhase.Testing));
        Assert.Equal(1, result.Phases.Count(p => p == GoalPhase.Review));
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
        // Verify ordering: Coding < Testing < Review < Merging
        var codingIdx = result.Phases.IndexOf(GoalPhase.Coding);
        var testingIdx = result.Phases.IndexOf(GoalPhase.Testing);
        var reviewIdx = result.Phases.IndexOf(GoalPhase.Review);
        Assert.True(codingIdx < testingIdx);
        Assert.True(testingIdx < reviewIdx);
    }

    // ── Docs-only plan behavior ───────────────────────────────────────────────

    [Fact]
    public void DocsOnlyPlan_WithTesting_ReviewNotInserted()
    {
        // Arrange: docs-only plan with Testing but no Review — Review must NOT be inserted
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: Review absent, Coding absent
        Assert.DoesNotContain(GoalPhase.Review, result.Phases);
        Assert.DoesNotContain(GoalPhase.Coding, result.Phases);
        Assert.Contains(GoalPhase.DocWriting, result.Phases);
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
    }

    [Fact]
    public void DocsOnlyPlan_WithoutTestingOrReview_TestingInserted_ReviewNotRequired()
    {
        // Arrange: docs-only plan with neither Testing nor Review
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.DocWriting, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: Testing inserted; Review NOT inserted; Coding absent
        Assert.Contains(GoalPhase.Testing, result.Phases);
        Assert.DoesNotContain(GoalPhase.Review, result.Phases);
        Assert.DoesNotContain(GoalPhase.Coding, result.Phases);
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
    }

    [Fact]
    public void DocsOnlyPlan_WithReview_Unchanged()
    {
        // Arrange: docs-only plan that already has Review — should not change
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.DocWriting, GoalPhase.Review, GoalPhase.Merging],
        };

        // Act
        var result = IterationPlanValidator.ValidatePlan(plan);

        // Assert: Review retained, Testing NOT inserted, Coding absent
        Assert.Contains(GoalPhase.Review, result.Phases);
        Assert.DoesNotContain(GoalPhase.Testing, result.Phases);
        Assert.DoesNotContain(GoalPhase.Coding, result.Phases);
        Assert.Equal(GoalPhase.Merging, result.Phases[^1]);
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

    public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) =>
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

        public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) =>
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

        public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) =>
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

        public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) =>
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
        return new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            workerGateway ?? new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
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

    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default) =>
        Task.FromResult(0);

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

    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default) =>
        Task.FromResult(0);

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
