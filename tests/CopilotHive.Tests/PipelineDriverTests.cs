using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Tests that <see cref="PipelineDriver.DriveNextPhaseAsync"/> sets
/// <see cref="PhaseResult.WorkerOutput"/> to <see cref="TaskMetrics.Summary"/>
/// when present, falling back to <see cref="TaskResult.Output"/> otherwise,
/// with 4000-char truncation applied in both cases.
/// </summary>
public sealed class PipelineDriverWorkerOutputTests
{
    // ── Test 1: Summary is preferred over Output ─────────────────────────

    [Fact]
    public async Task DriveNextPhaseAsync_WhenMetricsSummaryPresent_UsesMetricsSummaryAsWorkerOutput()
    {
        // Arrange
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review);
        AddPhaseEntry(pipeline, GoalPhase.Review);

        const string summaryText = "Detailed review findings: 3 issues found.";
        const string rawOutput = "changes"; // single word the LLM emits

        // Act
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            Metrics = new TaskMetrics
            {
                Verdict = "REQUEST_CHANGES",
                Summary = summaryText,
            },
        }, TestContext.Current.CancellationToken);

        // Assert: WorkerOutput should be the summary, not the raw LLM output
        Assert.Equal(summaryText, pipeline.PhaseLog[0].WorkerOutput);
        Assert.NotEqual(rawOutput, pipeline.PhaseLog[0].WorkerOutput);
    }

    // ── Test 2: Falls back to Output when Summary is absent or whitespace ─

    [Theory]
    [InlineData(null)]       // Metrics is null
    [InlineData("")]         // Summary is empty string
    [InlineData("   ")]      // Summary is whitespace only
    public async Task DriveNextPhaseAsync_WhenMetricsSummaryAbsentOrWhitespace_UsesRawOutput(string? summary)
    {
        // Arrange
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review);
        AddPhaseEntry(pipeline, GoalPhase.Review);

        const string rawOutput = "All looks good.";

        // Act
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            Metrics = summary is null
                ? null
                : new TaskMetrics { Verdict = "APPROVE", Summary = summary },
        }, TestContext.Current.CancellationToken);

        // Assert: WorkerOutput should be the raw output when Summary is absent/whitespace
        Assert.Equal(rawOutput, pipeline.PhaseLog[0].WorkerOutput);
    }

    // ── Test 3: Truncation at 4000 chars for Summary path ────────────────

    [Fact]
    public async Task DriveNextPhaseAsync_WhenSummaryExceeds4000Chars_TruncatesWithSuffix()
    {
        // Arrange
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review);
        AddPhaseEntry(pipeline, GoalPhase.Review);

        var longSummary = new string('S', 5000); // 5000-char summary

        // Act
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "short",
            Metrics = new TaskMetrics
            {
                Verdict = "REQUEST_CHANGES",
                Summary = longSummary,
            },
        }, TestContext.Current.CancellationToken);

        // Assert: truncated to 4000 chars with trailing length annotation
        var workerOutput = pipeline.PhaseLog[0].WorkerOutput;
        Assert.NotNull(workerOutput);
        Assert.StartsWith(new string('S', 4000), workerOutput);
        Assert.Contains("5000 chars total", workerOutput);
        Assert.True(workerOutput!.Length < 5000);
    }

    // ── Test 4: Truncation at 4000 chars for Output fallback path ─────────

    [Fact]
    public async Task DriveNextPhaseAsync_WhenOutputExceeds4000Chars_TruncatesWithSuffix()
    {
        // Arrange
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review);
        AddPhaseEntry(pipeline, GoalPhase.Review);

        var longOutput = new string('O', 5000); // 5000-char raw output

        // Act
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = longOutput,
            Metrics = new TaskMetrics { Verdict = "REQUEST_CHANGES" }, // no Summary
        }, TestContext.Current.CancellationToken);

        // Assert: truncated to 4000 chars with trailing length annotation
        var workerOutput = pipeline.PhaseLog[0].WorkerOutput;
        Assert.NotNull(workerOutput);
        Assert.StartsWith(new string('O', 4000), workerOutput);
        Assert.Contains("5000 chars total", workerOutput);
        Assert.True(workerOutput!.Length < 5000);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal self-contained <see cref="GoalDispatcher"/> for testing
    /// <c>DriveNextPhaseAsync</c> WorkerOutput assignment.
    /// </summary>
    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string taskId)
        CreateDispatcher(GoalPhase phase)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };

        var goalSource = new LocalFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set up the state machine so transitions work (Review → Merging)
        pipeline.StateMachine.RestoreFromPlan(
            [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
            phase);

        pipeline.AdvanceTo(phase);

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
            brain: new LocalFakeBrain());

        return (dispatcher, pipeline, taskId);
    }

    /// <summary>
    /// Adds a <see cref="PhaseResult"/> for <paramref name="phase"/> to the pipeline's
    /// PhaseLog so that <c>CurrentPhaseEntry</c> is non-null when DriveNextPhaseAsync runs.
    /// </summary>
    private static void AddPhaseEntry(GoalPipeline pipeline, GoalPhase phase)
    {
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = phase,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
        });
    }

    /// <summary>Minimal goal source that returns a single pre-configured goal.</summary>
    private sealed class LocalFakeGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "local-fake";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>Minimal brain stub for pipeline driver tests.</summary>
    private sealed class LocalFakeBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            UpdateModelAsync(model, maxContextTokens, ct);

        public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(
            string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
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
/// Tests that <see cref="PipelineDriver.HandleMergeFailureAsync"/> marks the Merging phase as
/// failed and persists the failed-merge iteration summary so it is visible in the dashboard
/// iteration tab bar (mirrors the <see cref="PipelineDriver.HandleNewIterationAsync"/> pattern).
/// </summary>
public sealed class PipelineDriverMergeFailurePersistenceTests
{
    // ── Test 1: merge failure after successful review persists iteration summary ──

    [Fact]
    public async Task HandleMergeFailureAsync_AfterSuccessfulReview_PersistsIterationSummaryWithMergingFail()
    {
        // Arrange: pipeline with Coding pass, Testing pass, Review pass, and a Merging PhaseResult.
        var (driver, pipeline, goalStore) = CreateDriver();

        AddPhaseEntry(pipeline, GoalPhase.Coding, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Testing, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Review, PhaseOutcome.Pass);
        var mergingEntry = PhaseResult.Create(GoalPhase.Merging, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(mergingEntry);

        const string mergeError = "conflict in Program.cs";

        // Act
        await driver.HandleMergeFailureAsync(pipeline, mergeError, TestContext.Current.CancellationToken);

        // Assert: the Merging PhaseResult was marked failed with the error.
        Assert.Equal(PhaseOutcome.Fail, mergingEntry.Result);
        Assert.Equal(mergeError, mergingEntry.WorkerOutput);
        Assert.NotNull(mergingEntry.CompletedAt);

        // Assert: UpdateGoalStatusAsync was called with an IterationSummary containing
        // Coding/Testing/Review pass and Merging fail with the error.
        var summaryUpdate = goalStore.StatusUpdates
            .Select(u => u.Metadata?.IterationSummary)
            .FirstOrDefault(s => s is not null);
        Assert.NotNull(summaryUpdate);
        Assert.Equal(1, summaryUpdate!.Iteration);

        Assert.Contains(summaryUpdate.Phases,
            p => p.Name == GoalPhase.Coding && p.Result == PhaseOutcome.Pass);
        Assert.Contains(summaryUpdate.Phases,
            p => p.Name == GoalPhase.Testing && p.Result == PhaseOutcome.Pass);
        Assert.Contains(summaryUpdate.Phases,
            p => p.Name == GoalPhase.Review && p.Result == PhaseOutcome.Pass);
        var mergingInSummary = Assert.Single(summaryUpdate.Phases, p => p.Name == GoalPhase.Merging);
        Assert.Equal(PhaseOutcome.Fail, mergingInSummary.Result);
        Assert.Contains(mergeError, mergingInSummary.WorkerOutput);

        // Assert: the summary is also in CompletedIterationSummaries and a retry iteration started.
        var completedSummary = Assert.Single(pipeline.CompletedIterationSummaries, s => s.Iteration == 1);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(completedSummary.Phases, p => p.Name == GoalPhase.Merging).Result);
        Assert.Equal(2, pipeline.Iteration);
    }

    // ── Test 2: timeline shows both the failed-merge iteration and the retry iteration ──

    [Fact]
    public async Task HandleMergeFailureAsync_RetryDispatch_TimelineShowsBothIterations()
    {
        // Arrange
        var (driver, pipeline, _) = CreateDriver();

        AddPhaseEntry(pipeline, GoalPhase.Coding, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Testing, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Review, PhaseOutcome.Pass);
        var mergingEntry = PhaseResult.Create(GoalPhase.Merging, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(mergingEntry);

        const string mergeError = "conflict in Program.cs";

        // Act
        await driver.HandleMergeFailureAsync(pipeline, mergeError, TestContext.Current.CancellationToken);

        // Assert: CompletedIterationSummaries contains the failed-merge iteration (Merging = fail).
        var failedMergeSummary = Assert.Single(pipeline.CompletedIterationSummaries, s => s.Iteration == 1);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(failedMergeSummary.Phases, p => p.Name == GoalPhase.Merging).Result);

        // Assert: a new iteration has started.
        Assert.Equal(2, pipeline.Iteration);
        Assert.Equal(IterationPlan.Default().Phases[0], pipeline.Phase);

        // Assert: the timeline shows both the failed-merge iteration and the current retry iteration.
        var iterations = GoalDetailViewBuilder.BuildIterationTimeline(pipeline.Goal, pipeline.GoalId, pipeline);
        Assert.Equal(2, iterations.Count);

        var failedIteration = Assert.Single(iterations, i => i.Number == 1);
        var mergingPhaseView = Assert.Single(failedIteration.Phases, p => p.Name == "Merging");
        Assert.Equal("failed", mergingPhaseView.Status);
        Assert.Contains(mergeError, mergingPhaseView.WorkerOutput);

        var currentIteration = Assert.Single(iterations, i => i.Number == 2);
        Assert.True(currentIteration.IsCurrent);
    }

    // ── Test 3: budget-exhausted path marks Merging failed but creates no duplicate ──

    [Fact]
    public async Task HandleMergeFailureAsync_ReviewBudgetExhausted_MarksMergingFailedWithoutDuplicateSummary()
    {
        // Arrange: exhaust the review retry budget (maxRetries = 3) so HandleMergeFailureAsync
        // takes the terminal path and lets FinalizeGoalAsync build/persist the summary.
        var (driver, pipeline, goalStore) = CreateDriver();
        for (var i = 0; i < 3; i++)
            pipeline.ReviewRetryBudget.TryConsume();

        AddPhaseEntry(pipeline, GoalPhase.Coding, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Testing, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Review, PhaseOutcome.Pass);
        var mergingEntry = PhaseResult.Create(GoalPhase.Merging, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(mergingEntry);

        const string mergeError = "conflict in Program.cs";

        // Act
        await driver.HandleMergeFailureAsync(pipeline, mergeError, TestContext.Current.CancellationToken);

        // Assert: the Merging PhaseResult was marked failed with the error BEFORE terminal exit,
        // so FinalizeGoalAsync's summary includes the failed Merging phase.
        Assert.Equal(PhaseOutcome.Fail, mergingEntry.Result);
        Assert.Equal(mergeError, mergingEntry.WorkerOutput);
        Assert.NotNull(mergingEntry.CompletedAt);

        // Assert: goal failed via the terminal path.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);

        // Assert: exactly ONE summary (the terminal one from FinalizeGoalAsync) — no duplicate
        // pre-retry snapshot was added by HandleMergeFailureAsync.
        var summary = Assert.Single(pipeline.CompletedIterationSummaries);
        Assert.Equal(1, summary.Iteration);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(summary.Phases, p => p.Name == GoalPhase.Merging).Result);

        // Assert: exactly ONE status update carried an IterationSummary (the Failed one from
        // FinalizeGoalAsync) — HandleMergeFailureAsync did NOT create an InProgress one.
        var summaryUpdates = goalStore.StatusUpdates
            .Where(u => u.Metadata?.IterationSummary is not null)
            .ToList();
        var failedUpdate = Assert.Single(summaryUpdates);
        Assert.Equal(GoalStatus.Failed, failedUpdate.Status);
    }

    // ── Test 4: iteration-budget-exhausted path marks Merging failed but creates no duplicate ──

    [Fact]
    public async Task HandleMergeFailureAsync_IterationBudgetExhausted_MarksMergingFailedWithoutDuplicateSummary()
    {
        // Arrange: exhaust the iteration budget (maxIterations = 5 → IterationBudget allows 4)
        // while the review retry budget still has room, so HandleMergeFailureAsync passes the
        // review-budget check but takes the iteration-budget terminal path.
        var (driver, pipeline, goalStore) = CreateDriver();
        for (var i = 0; i < 4; i++)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);
        Assert.False(pipeline.ReviewRetryBudget.IsExhausted);

        AddPhaseEntry(pipeline, GoalPhase.Coding, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Testing, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Review, PhaseOutcome.Pass);
        var mergingEntry = PhaseResult.Create(GoalPhase.Merging, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(mergingEntry);

        var summariesBefore = pipeline.CompletedIterationSummaries.Count;
        var iterationBefore = pipeline.Iteration; // 5 after exhausting the iteration budget

        const string mergeError = "conflict in Program.cs";

        // Act
        await driver.HandleMergeFailureAsync(pipeline, mergeError, TestContext.Current.CancellationToken);

        // Assert: the Merging PhaseResult was marked failed with the error BEFORE terminal exit,
        // so FinalizeGoalAsync's summary includes the failed Merging phase.
        Assert.Equal(PhaseOutcome.Fail, mergingEntry.Result);
        Assert.Equal(mergeError, mergingEntry.WorkerOutput);
        Assert.NotNull(mergingEntry.CompletedAt);

        // Assert: goal failed via the terminal path.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);

        // Assert: HandleMergeFailureAsync did NOT add a pre-retry snapshot to
        // CompletedIterationSummaries — the only addition is the terminal summary from
        // FinalizeGoalAsync (exactly one, not two).
        Assert.Equal(summariesBefore, pipeline.CompletedIterationSummaries.Count - 1);
        var summary = Assert.Single(pipeline.CompletedIterationSummaries);
        Assert.Equal(iterationBefore, summary.Iteration);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(summary.Phases, p => p.Name == GoalPhase.Merging).Result);

        // Assert: exactly ONE status update carried an IterationSummary (the Failed one from
        // FinalizeGoalAsync) — HandleMergeFailureAsync did NOT create an InProgress one.
        var summaryUpdates = goalStore.StatusUpdates
            .Where(u => u.Metadata?.IterationSummary is not null)
            .ToList();
        var failedUpdate = Assert.Single(summaryUpdates);
        Assert.Equal(GoalStatus.Failed, failedUpdate.Status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (PipelineDriver Driver, GoalPipeline Pipeline, PlanRejectRecordingGoalStore Store) CreateDriver()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Merge failure persistence test" };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        pipeline.AdvanceTo(GoalPhase.Merging);
        pipeline.SetPlan(IterationPlan.Default());

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);

        var driver = new PipelineDriver(
            brain: new MergeFailureFakeBrain(),
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => Task.CompletedTask,
            resolvePrompt: (_, _, _, _) => Task.FromResult("rebase and fix the conflict"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return (driver, pipeline, goalStore);
    }

    /// <summary>Adds a completed <see cref="PhaseResult"/> with the given outcome to the PhaseLog.</summary>
    private static void AddPhaseEntry(GoalPipeline pipeline, GoalPhase phase, PhaseOutcome outcome)
    {
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = phase,
            Result = outcome,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            WorkerOutput = $"{phase} output",
        });
    }

    /// <summary>Minimal brain stub that returns the default plan.</summary>
    private sealed class MergeFailureFakeBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            UpdateModelAsync(model, maxContextTokens, ct);

        public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(
            string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
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
