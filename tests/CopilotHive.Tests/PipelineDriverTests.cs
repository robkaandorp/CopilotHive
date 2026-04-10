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

        public void DeleteGoalSession(string goalId) { }

        public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }
}
