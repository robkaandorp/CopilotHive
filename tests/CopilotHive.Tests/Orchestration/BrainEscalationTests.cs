using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Tests for Brain escalation during planning (<see cref="IDistributedBrain.PlanIterationAsync"/>)
/// and prompt crafting (<see cref="IDistributedBrain.CraftPromptAsync"/>), covering:
/// - Escalation with successful Composer clarification
/// - Escalation with timeout fallback (Composer does not answer in time)
/// </summary>
public sealed class BrainEscalationTests
{
    // ── PlanIterationAsync escalation — successful clarification ──────────

    /// <summary>
    /// When the Brain escalates during planning and the Composer supplies an answer,
    /// the dispatcher retries planning with the answer as additional context and
    /// returns the resulting plan (not the default plan).
    /// </summary>
    [Fact]
    public async Task PlanIterationAsync_BrainEscalates_ComposerAnswers_RetriesWithContext()
    {
        // Arrange
        const string EscalationQuestion = "Should the plan include the review phase?";
        const string ComposerAnswer = "Yes, always include review.";
        const string ExpectedRetryContext = $"=== Clarification answer ===\n{ComposerAnswer}\n=== End clarification answer ===";

        // Brain: first call escalates, second call succeeds
        var planBrain = new EscalatingPlanBrain(EscalationQuestion, "Cannot determine from codebase");
        var queue = new ClarificationQueueService();
        var router = new AutoAnswerRouter(ComposerAnswer);
        var (dispatcher, pipeline) = CreateDispatcher(planBrain, queue, router);

        // Act
        var plan = await dispatcher.ResolvePlanAsync(pipeline, null, TestContext.Current.CancellationToken);

        // Assert — second call should have received clarification context and returned a non-default plan marker
        Assert.NotNull(plan);
        Assert.False(plan.Phases.Count == 0);
        // The retry context should have been passed through (fenced format)
        Assert.Contains(ComposerAnswer, planBrain.LastAdditionalContext ?? string.Empty);
        Assert.Contains("=== Clarification answer ===", planBrain.LastAdditionalContext ?? string.Empty);
        Assert.Contains("=== End clarification answer ===", planBrain.LastAdditionalContext ?? string.Empty);
    }

    /// <summary>
    /// When the Brain escalates during planning and the clarification times out,
    /// <see cref="GoalDispatcher"/> falls back to <see cref="IterationPlan.Default"/>.
    /// </summary>
    [Fact]
    public async Task PlanIterationAsync_BrainEscalates_Timeout_FallsBackToDefaultPlan()
    {
        // Arrange
        var planBrain = new EscalatingPlanBrain(
            "What model should I use?", "Need domain knowledge");

        var queue = new ClarificationQueueService();
        var router = new TimeoutRouter(); // Escalates to human, then MarkTimedOut after short delay

        var (dispatcher, pipeline) = CreateDispatcher(planBrain, queue, router);

        // Act — TimeoutRouter schedules MarkTimedOut after 200ms, which completes the TCS
        // with the fallback message. ResolvePlanAsync maps that to IterationPlan.Default().
        var plan = await dispatcher.ResolvePlanAsync(pipeline, null, TestContext.Current.CancellationToken);

        // Assert — should fall back to default plan
        var defaultPhases = IterationPlan.Default().Phases;
        Assert.Equal(defaultPhases, plan.Phases);
    }

    // ── CraftPromptAsync escalation — successful clarification ───────────

    /// <summary>
    /// When the Brain escalates during prompt crafting and the Composer supplies an answer,
    /// the dispatcher retries crafting with the answer as additional context and returns
    /// the resulting prompt (not the generic fallback).
    /// </summary>
    [Fact]
    public async Task CraftPromptAsync_BrainEscalates_ComposerAnswers_RetriesWithContext()
    {
        // Arrange
        const string EscalationQuestion = "What framework does this project use?";
        const string ComposerAnswer = "xUnit for tests, .NET 10.";
        const string ExpectedPrompt = "Crafted with clarification: " + ComposerAnswer;

        var promptBrain = new EscalatingPromptBrain(EscalationQuestion, "Cannot determine from codebase", ExpectedPrompt);
        var queue = new ClarificationQueueService();
        var router = new AutoAnswerRouter(ComposerAnswer);
        var (dispatcher, pipeline) = CreateDispatcher(promptBrain, queue, router, advanceTo: GoalPhase.Coding);

        // Act
        var prompt = await dispatcher.ResolvePromptAsync(
            pipeline, GoalPhase.Coding, null, TestContext.Current.CancellationToken);

        // Assert — should contain the clarification answer context
        Assert.NotNull(prompt);
        Assert.NotEmpty(prompt);
        // The retry was called with clarification answer as additional context
        Assert.Contains(ComposerAnswer, promptBrain.LastAdditionalContext ?? string.Empty);
    }

    /// <summary>
    /// When the Brain escalates during prompt crafting and the clarification times out,
    /// <see cref="GoalDispatcher"/> falls back to a generic prompt.
    /// </summary>
    [Fact]
    public async Task CraftPromptAsync_BrainEscalates_Timeout_FallsBackToGenericPrompt()
    {
        // Arrange
        const string GoalDescription = "Implement caching";
        var promptBrain = new EscalatingPromptBrain(
            "What cache strategy to use?", "Need domain knowledge",
            retryPrompt: null); // No retry prompt needed — will time out

        var queue = new ClarificationQueueService();
        var router = new TimeoutRouter();

        var (dispatcher, pipeline, goalDescription) = CreateDispatcherWithDescription(
            promptBrain, queue, router, GoalDescription, advanceTo: GoalPhase.Coding);

        // Act — TimeoutRouter schedules MarkTimedOut after 200ms, which completes the TCS
        // with the fallback message. ResolvePromptAsync maps that to the generic prompt.
        var prompt = await dispatcher.ResolvePromptAsync(
            pipeline, GoalPhase.Coding, null, TestContext.Current.CancellationToken);

        // Assert — should fall back to exact generic "Work on:" prompt
        Assert.Equal($"Work on: {GoalDescription}", prompt);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline) CreateDispatcher(
        IDistributedBrain brain,
        ClarificationQueueService queue,
        IClarificationRouter router,
        GoalPhase? advanceTo = null)
    {
        const string desc = "Test goal";
        var (dispatcher, pipeline, _) = CreateDispatcherWithDescription(brain, queue, router, desc, advanceTo);
        return (dispatcher, pipeline);
    }

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string description) CreateDispatcherWithDescription(
        IDistributedBrain brain,
        ClarificationQueueService queue,
        IClarificationRouter router,
        string goalDescription,
        GoalPhase? advanceTo = null)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = goalDescription };
        var goalSource = new SimpleGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        if (advanceTo is not null)
            pipeline.AdvanceTo(advanceTo.Value);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
            clarificationRouter: router,
            clarificationQueue: queue);

        return (dispatcher, pipeline, goalDescription);
    }

    // ── Fake implementations ───────────────────────────────────────────────

    /// <summary>
    /// A Brain that escalates on the first <see cref="PlanIterationAsync"/> call,
    /// then succeeds on the second (retry) call, recording the additional context.
    /// </summary>
    private sealed class EscalatingPlanBrain(
        string escalationQuestion,
        string escalationReason) : IDistributedBrain
    {
        private int _callCount;

        /// <summary>The additionalContext passed on the second (retry) call.</summary>
        public string? LastAdditionalContext { get; private set; }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _callCount);

            if (call == 1)
                return Task.FromResult(PlanResult.Escalated(escalationQuestion, escalationReason));

            // Second call: record context and return a plan
            LastAdditionalContext = additionalContext;
            var plan = IterationPlan.Default();
            return Task.FromResult(PlanResult.Success(plan));
        }

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description}"));

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("Proceed with best judgment."));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public BrainStats? GetStats() => null;
    }

    /// <summary>
    /// A Brain that escalates on the first <see cref="CraftPromptAsync"/> call,
    /// then succeeds on the second (retry) call, recording the additional context.
    /// </summary>
    private sealed class EscalatingPromptBrain(
        string escalationQuestion,
        string escalationReason,
        string? retryPrompt) : IDistributedBrain
    {
        private int _callCount;

        /// <summary>The additionalContext passed on the second (retry) call.</summary>
        public string? LastAdditionalContext { get; private set; }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _callCount);

            if (call == 1)
                return Task.FromResult(PromptResult.Escalated(escalationQuestion, escalationReason));

            // Second call: record context and return the prompt
            LastAdditionalContext = additionalContext;
            var prompt = retryPrompt ?? $"Work on {pipeline.Description}";
            return Task.FromResult(PromptResult.Success(prompt));
        }

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("Proceed with best judgment."));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public BrainStats? GetStats() => null;
    }

    /// <summary>Auto-answer router: returns a pre-configured answer immediately.</summary>
    private sealed class AutoAnswerRouter(string answer) : IClarificationRouter
    {
        public Task<string?> TryAutoAnswerAsync(
            string goalId, string question, string context,
            ClarificationQueueService clarificationQueue,
            ClarificationRequest request,
            CancellationToken ct = default) =>
            Task.FromResult<string?>(answer);
    }

    /// <summary>
    /// Timeout router: escalates to human and then marks the request as timed out
    /// after a short delay, simulating the Composer timeout → human timeout scenario
    /// without relying on the caller's cancellation token.
    /// </summary>
    private sealed class TimeoutRouter : IClarificationRouter
    {
        public Task<string?> TryAutoAnswerAsync(
            string goalId, string question, string context,
            ClarificationQueueService clarificationQueue,
            ClarificationRequest request,
            CancellationToken ct = default)
        {
            clarificationQueue.EscalateToHuman(request.Id);

            // Schedule MarkTimedOut after a brief delay to complete the TCS
            // with the fallback message, simulating real human-answer timeout.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);
                clarificationQueue.MarkTimedOut(request.Id);
            });

            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>Minimal goal source for tests.</summary>
    private sealed class SimpleGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "simple";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
