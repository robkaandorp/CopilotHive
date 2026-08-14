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
/// Tests that <see cref="PipelineDriver.DriveNextPhaseAsync"/>'s no-op coder retry path
/// persists the no-op iteration summary (with Coding marked failed) BEFORE consuming the
/// iteration budget, so the iteration stays visible in the dashboard tab bar — the same
/// class of bug as the merge-fail iteration display issue fixed in v0.27.0.
/// </summary>
public sealed class PipelineDriverNoOpRetryTests
{
    // ── Test 1: no-op retry persists iteration summary with Coding = Fail ──

    [Fact]
    public async Task NoOpRetry_PersistsIterationSummaryWithCodingFail()
    {
        // Arrange: pipeline in Coding with a Coding PhaseResult for iteration 1, plus a
        // craft-prompt conversation entry for the retry iteration (2) so the BrainPrompt
        // forwarding on the retry entry is observable.
        const string retryPrompt = "retry with stronger prompt";
        const string craftPrompt = "Brain craft prompt for retry";
        var (driver, pipeline, goalStore) = CreateNoOpDriver();
        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        // GetLastCraftPromptFromConversation runs AFTER TryConsume() has advanced the pipeline
        // to iteration 2, so the seed entry must carry the retry iteration number.
        pipeline.Conversation.Add(new ConversationEntry("user", craftPrompt, 2, "craft-prompt"));

        // Act: coder returns with 0 files changed → no-op retry path.
        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-1",
            Status = TaskOutcome.Completed,
            Output = "I discussed the changes but made no edits.",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
        }, TestContext.Current.CancellationToken);

        // Assert: the Coding PhaseResult was marked failed with the no-op reason.
        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        Assert.Equal("Coder produced no file changes (no-op)", codingEntry.WorkerOutput);
        Assert.NotNull(codingEntry.CompletedAt);

        // Assert: UpdateGoalStatusAsync was called while pipeline.Iteration was still the
        // PRE-consume value (1). If the persist call were moved after
        // IterationBudget.TryConsume(), pipeline.Iteration would be 2 at call time and this
        // assertion would fail — proving the summary is persisted BEFORE the budget is consumed.
        var inProgressUpdate = Assert.Single(goalStore.StatusUpdates, u => u.Status == GoalStatus.InProgress);
        Assert.Equal(1, inProgressUpdate.IterationAtUpdate);

        // Assert: the persisted summary carries the pre-consume iteration with Coding = Fail.
        var summaryUpdate = inProgressUpdate.Metadata?.IterationSummary;
        Assert.NotNull(summaryUpdate);
        Assert.Equal(1, summaryUpdate!.Iteration);
        var codingInSummary = Assert.Single(summaryUpdate.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(PhaseOutcome.Fail, codingInSummary.Result);
        Assert.Equal("Coder produced no file changes (no-op)", codingInSummary.WorkerOutput);

        // Assert: the summary is also in CompletedIterationSummaries and the retry iteration started.
        var completedSummary = Assert.Single(pipeline.CompletedIterationSummaries, s => s.Iteration == 1);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(completedSummary.Phases, p => p.Name == GoalPhase.Coding).Result);
        Assert.Equal(2, pipeline.Iteration);

        // Assert: a fresh Coding PhaseResult was added for the retry iteration with the exact
        // retry prompt and the forwarded Brain craft prompt. Removing the WorkerPrompt/BrainPrompt
        // assignments from PipelineDriver.cs would fail these assertions.
        Assert.Equal(2, pipeline.PhaseLog.Count);
        var retryEntry = pipeline.PhaseLog[1];
        Assert.Equal(GoalPhase.Coding, retryEntry.Name);
        Assert.Equal(2, retryEntry.Iteration);
        Assert.Equal(1, retryEntry.Occurrence);
        Assert.Equal(retryPrompt, retryEntry.WorkerPrompt);
        Assert.Equal(craftPrompt, retryEntry.BrainPrompt);
    }

    // ── Test 2: budget-exhausted terminal path fails without duplicate summary ──

    [Fact]
    public async Task NoOpRetry_BudgetExhausted_FailsWithoutDuplicateSummary()
    {
        // Arrange: exhaust the iteration budget (maxIterations = 5 → IterationBudget allows 4)
        // so the no-op path takes the terminal IsExhausted branch.
        var (driver, pipeline, goalStore) = CreateNoOpDriver();
        for (var i = 0; i < 4; i++)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);

        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        var summariesBefore = pipeline.CompletedIterationSummaries.Count;
        var iterationBefore = pipeline.Iteration; // 5 after exhausting the iteration budget

        // Act
        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-2",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
        }, TestContext.Current.CancellationToken);

        // Assert: the Coding PhaseResult was marked failed with the no-op reason BEFORE terminal
        // exit, so FinalizeGoalAsync's summary includes the failed Coding phase.
        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        Assert.Equal("Coder produced no file changes (no-op)", codingEntry.WorkerOutput);
        Assert.NotNull(codingEntry.CompletedAt);

        // Assert: goal failed via the terminal path.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);

        // Assert: DriveNextPhaseAsync did NOT add a pre-retry snapshot to
        // CompletedIterationSummaries — the only addition is the terminal summary from
        // FinalizeGoalAsync (exactly one, not two).
        Assert.Equal(summariesBefore, pipeline.CompletedIterationSummaries.Count - 1);
        var summary = Assert.Single(pipeline.CompletedIterationSummaries);
        Assert.Equal(iterationBefore, summary.Iteration);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(summary.Phases, p => p.Name == GoalPhase.Coding).Result);

        // Assert: exactly ONE status update carried an IterationSummary (the Failed one from
        // FinalizeGoalAsync) — the no-op path did NOT create an InProgress one.
        var summaryUpdates = goalStore.StatusUpdates
            .Where(u => u.Metadata?.IterationSummary is not null)
            .ToList();
        var failedUpdate = Assert.Single(summaryUpdates);
        Assert.Equal(GoalStatus.Failed, failedUpdate.Status);
    }

    // ── Test 3: completed summary is not mutated by the retry's result ──

    [Fact]
    public async Task NoOpRetry_CompletedSummaryNotMutatedByRetry()
    {
        // Arrange: fake dispatch that immediately completes the retry coding task by writing
        // the completion data onto the retry's PhaseResult (what DriveNextPhaseAsync would do
        // when the retry worker reports back).
        var (driver, pipeline, _) = CreateNoOpDriver((p, role, prompt, ct) =>
        {
            var retryEntry = p.CurrentPhaseEntry!;
            retryEntry.Result = PhaseOutcome.Pass;
            retryEntry.CompletedAt = DateTime.UtcNow;
            retryEntry.WorkerOutput = "retry produced changes";
            return Task.CompletedTask;
        });

        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        // Act
        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-3",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
        }, TestContext.Current.CancellationToken);

        // Assert: the CompletedIterationSummaries entry for the no-op iteration still has
        // Coding = Fail with the no-op output — the retry's completion wrote to the NEW
        // iteration-2 PhaseResult, not the failed iteration-1 entry captured in the summary.
        var completedSummary = Assert.Single(pipeline.CompletedIterationSummaries, s => s.Iteration == 1);
        var codingInSummary = Assert.Single(completedSummary.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(PhaseOutcome.Fail, codingInSummary.Result);
        Assert.Contains("no-op", codingInSummary.WorkerOutput);

        // Assert: the PhaseLog holds two distinct Coding entries — the failed iteration-1 entry
        // and the retry's iteration-2 entry (which the fake dispatch completed as Pass).
        Assert.Equal(2, pipeline.PhaseLog.Count);
        var failedEntry = pipeline.PhaseLog[0];
        var retryEntry = pipeline.PhaseLog[1];
        Assert.Equal(PhaseOutcome.Fail, failedEntry.Result);
        Assert.Equal(1, failedEntry.Iteration);
        Assert.Equal(PhaseOutcome.Pass, retryEntry.Result);
        Assert.Equal(2, retryEntry.Iteration);
        Assert.Equal("retry produced changes", retryEntry.WorkerOutput);

        // Assert: the summary's Coding entry and the new PhaseLog entry are DISTINCT objects —
        // the retry's completion could not have mutated the captured summary entry. Without the
        // fresh iteration-2 PhaseResult in PipelineDriver.cs, CurrentPhaseEntry would still be
        // the failed iteration-1 entry, the fake dispatch would write Pass onto it, and the
        // summary (which references that same object) would show Pass — failing this assertion.
        Assert.NotSame(codingInSummary, retryEntry);
        // The summary's Coding entry references the failed iteration-1 PhaseResult (BuildIterationSummary
        // copies entry references), so it MUST be the same object as the failed PhaseLog entry —
        // which the retry entry (a distinct object) could not have mutated.
        Assert.Same(codingInSummary, failedEntry);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (PipelineDriver Driver, GoalPipeline Pipeline, IterationCapturingGoalStore Store) CreateNoOpDriver(
        Func<GoalPipeline, WorkerRole, string?, CancellationToken, Task>? dispatchToRole = null)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "No-op retry persistence test" };
        var goalStore = new IterationCapturingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.SetPlan(IterationPlan.Default());
        goalStore.Pipeline = pipeline;

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);

        var driver = new PipelineDriver(
            brain: new NoOpRetryFakeBrain(),
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: dispatchToRole ?? ((_, _, _, _) => Task.CompletedTask),
            resolvePrompt: (_, _, _, _) => Task.FromResult("retry with stronger prompt"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return (driver, pipeline, goalStore);
    }

    /// <summary>
    /// In-memory <see cref="IGoalStore"/> that records every status update together with the
    /// pipeline's iteration counter sampled AT THE MOMENT of the call. This lets tests prove
    /// whether an update happened before or after <see cref="GoalPipeline.IterationBudget"/>
    /// was consumed: if the persist call were moved after <c>TryConsume()</c>, the captured
    /// iteration would be the new (post-consume) number and the assertion would fail.
    /// </summary>
    private sealed class IterationCapturingGoalStore(Goal goal) : IGoalStore
    {
        /// <summary>Pipeline whose <see cref="GoalPipeline.Iteration"/> is sampled on each update.</summary>
        internal GoalPipeline? Pipeline { get; set; }

        /// <summary>All status updates in the order they were applied, with the iteration at call time.</summary>
        internal List<(GoalStatus Status, GoalUpdateMetadata? Metadata, int IterationAtUpdate)> StatusUpdates { get; } = [];

        public string Name => "iteration-capturing-store";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(goal.Status == GoalStatus.Pending ? [goal] : []);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            lock (StatusUpdates)
            {
                StatusUpdates.Add((status, metadata, Pipeline?.Iteration ?? 0));
            }

            goal.Status = status;
            if (metadata?.FailureReason is not null)
                goal.FailureReason = metadata.FailureReason;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult(goalId == goal.Id ? goal : null);

        public Task<Goal> CreateGoalAsync(Goal goalToCreate, CancellationToken ct = default) => Task.FromResult(goalToCreate);

        public Task UpdateGoalAsync(Goal goalToUpdate, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
            string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IterationSummary>>([]);

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
            Task.FromResult(release);

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<Release?>(null);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Release>>([]);

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
            string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
            int? limit = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
    }

    /// <summary>Minimal brain stub that returns the default plan.</summary>
    private sealed class NoOpRetryFakeBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
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
