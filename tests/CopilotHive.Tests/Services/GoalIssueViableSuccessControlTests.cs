using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE VIABLE AUTHORIZED SUCCESS CONTROLS for the goal operations whose success paths the
/// iteration-1 matrix only reached as a DOWNSTREAM NotFound/BadRequest answer.
/// <para>
/// A downstream NotFound proves only that the gate was PASSED — it does not prove that the
/// authorized role can actually PERFORM the operation. This file therefore drives the real
/// production paths to a SUCCESSFUL outcome with an OBSERVABLE EFFECT for every one of them:
/// </para>
/// <list type="bullet">
///   <item><b>REVIEW</b> — a real <see cref="GoalReviewService"/> over a stub chat client records a
///   real verdict, persists the goal's <see cref="ReviewStatus.Approved"/> and returns the
///   verdict DTO.</item>
///   <item><b>CANCEL</b> — a real <see cref="GoalDispatcher"/> cancels a real Pending goal and the
///   facade answers the real confirmation message.</item>
///   <item><b>EXTEND-ITERATIONS</b> — a real <see cref="GoalDispatcher"/> resumes a real Failed goal
///   and the facade answers the real extended-budget confirmation, with the goal's iteration
///   budget demonstrably topped up.</item>
/// </list>
/// <para>
/// EVERY row runs for BOTH the Operator AND the Admin, so the "not admin-gated" claim is backed
/// by a successful outcome rather than by a downstream error.
/// </para>
/// </summary>
public sealed class GoalIssueViableSuccessControlTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string OperatorId = "viable-operator";
    private const string AdminId = "viable-admin";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    /// <summary>The review verdict the stub reviewer reports.</summary>
    private const string ApprovedReviewJson =
        """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"Looks good"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    private async Task SeedRolesAsync()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A chat client that answers the reviewer with a fixed approved verdict.</summary>
    private sealed class StubReviewerChatClient(string reply) : IChatClient
    {
        /// <summary>The number of review requests this client served.</summary>
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
            {
                FinishReason = ChatFinishReason.Stop,
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Streaming is not used by the review agent in these tests.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// A Brain that plans deterministically, so the real resume path completes without a model.
    /// </summary>
    private sealed class PlanningBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description}"));

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult("summary");

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task UpdateModelAsync(string model, int? maxContextTokens, ReasoningEffort? reasoningEffort, CancellationToken ct = default) =>
            Task.CompletedTask;

        public BrainStats? GetStats() => null;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Builds the production <see cref="GoalFacade"/> with a REAL review service (driven by the
    /// supplied stub chat client) and a REAL dispatcher over the supplied pipeline manager.
    /// </summary>
    private GoalFacade GoalFacadeFor(
        RecordingGoalStore store,
        GoalPipelineManager? pipelineManager = null,
        StubReviewerChatClient? reviewer = null,
        GoalDispatcher? dispatcher = null)
    {
        var reviewService = new GoalReviewService(
            knowledgeGraph: null,
            configRepo: null,
            config: new HiveConfigFile
            {
                Workers = { ["reviewer"] = new WorkerConfig { Model = "reviewer-model" } },
            },
            goalStore: store,
            brainRepoManager: null,
            stateDir: Path.GetTempPath(),
            logger: NullLogger<GoalReviewService>.Instance,
            chatClientFactory: _ => reviewer ?? new StubReviewerChatClient(ApprovedReviewJson));

        return new GoalFacade(
            store,
            new RecordingIssueStore(),
            reviewService,
            new DashboardNotifier(),
            _auth.Resolver(HiveAuthMode.AuthenticationEnabled),
            _auth.Policy,
            NullLogger<GoalFacade>.Instance,
            repoManager: null,
            docCleanup: null,
            goalReadyNotifier: null,
            dispatcher: dispatcher ?? CreateDispatcher(store, pipelineManager),
            dashboardState: null,
            progressReports: null);
    }

    /// <summary>Creates a REAL dispatcher over the recording store.</summary>
    private static GoalDispatcher CreateDispatcher(
        IGoalStore goalStore,
        GoalPipelineManager? pipelineManager = null)
    {
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager ?? new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new CopilotHive.Git.BrainRepoManager(Path.GetTempPath(), NullLogger<CopilotHive.Git.BrainRepoManager>.Instance),
            new PlanningBrain(),
            config: new HiveConfigFile
            {
                Workers = { ["coder"] = new WorkerConfig { Model = "coder-model" } },
            },
            goalStore: goalStore);

        // The resume path's Brain planning must not block on a real model.
        dispatcher.ResumeTimeout = TimeSpan.FromSeconds(30);
        return dispatcher;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  REVIEW — a real verdict, recorded and persisted
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE VIABLE REVIEW CONTROL: for BOTH the Operator and the Admin, the review of a REAL Draft
    /// goal SUCCEEDS with the REAL verdict from the reviewer, and the observable effect is
    /// recorded — the goal's <see cref="ReviewStatus"/> becomes
    /// <see cref="ReviewStatus.Approved"/> in the STORE.
    /// <para>
    /// This replaces the iteration-1 row that only reached a downstream NotFound.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Review_OperatorAndAdmin_SucceedWithARealRecordedVerdict()
    {
        await SeedRolesAsync();

        foreach (var (role, id) in new[] { ("Operator", OperatorId), ("Admin", AdminId) })
        {
            using var store = new RecordingGoalStore();
            store.AddGoal(new Goal
            {
                Id = "review-goal",
                Description = "A goal to review",
                Status = GoalStatus.Draft,
                ReviewStatus = ReviewStatus.None,
            });

            var reviewer = new StubReviewerChatClient(ApprovedReviewJson);
            var facade = GoalFacadeFor(store, reviewer: reviewer);
            var user = Principal(id);

            var result = await facade.RequestReviewAsync("review-goal", user, Ct);

            // THE SUCCESSFUL OUTCOME: the real verdict, not a downstream error.
            Assert.True(result.Success, $"a {role} must be able to review: {result.Error}");
            Assert.NotNull(result.Value);
            Assert.Equal("Approved", result.Value!.Verdict);
            Assert.Equal("Looks good", result.Value.Summary);

            // THE OBSERVABLE EFFECT: the reviewer really ran, and the goal's review status was
            // really recorded as Approved.
            Assert.Equal(1, reviewer.Calls);
            Assert.Equal(ReviewStatus.Approved, store.GoalUpdates[^1].ReviewStatus);
            Assert.Contains("UpdateGoalAsync", store.AllCalls);
        }
    }

    /// <summary>
    /// THE VIABLE REVIEW CONTROL, NEGATIVE SIDE: the SAME review by a MEMBER is refused with
    /// Forbidden and records NOTHING — the review really is an Operator/Admin operation and the
    /// success row above is a discriminating pair, not a "refuse everything" implementation.
    /// </summary>
    [Fact]
    public async Task Review_AMemberIsRefused_WhileOperatorAndAdminSucceed()
    {
        await SeedRolesAsync();
        await _auth.SeedAsync("viable-member", UserRole.Member);

        using var store = new RecordingGoalStore();
        store.AddGoal(new Goal
        {
            Id = "review-goal",
            Description = "A goal to review",
            Status = GoalStatus.Draft,
            ReviewStatus = ReviewStatus.None,
        });

        var reviewer = new StubReviewerChatClient(ApprovedReviewJson);
        var facade = GoalFacadeFor(store, reviewer: reviewer);

        var refused = await facade.RequestReviewAsync("review-goal", Principal("viable-member"), Ct);

        Assert.Equal(FacadeErrorKind.Forbidden, refused.Kind);
        Assert.Equal(ForbiddenMessage, refused.Error);
        Assert.Equal(0, reviewer.Calls);
        Assert.Empty(store.AllCalls);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  CANCEL — a real cancellation with the real confirmation
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE VIABLE CANCEL CONTROL: for BOTH the Operator and the Admin, cancelling a REAL Pending
    /// goal SUCCEEDS and answers the REAL confirmation message, and the observable effect is
    /// recorded — the goal's status became <see cref="GoalStatus.Failed"/> with the cancellation
    /// reason.
    /// <para>
    /// This replaces the iteration-1 row that only reached a downstream NotFound.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Cancel_OperatorAndAdmin_SucceedWithTheRealConfirmationMessage()
    {
        await SeedRolesAsync();

        foreach (var (role, id) in new[] { ("Operator", OperatorId), ("Admin", AdminId) })
        {
            using var store = new RecordingGoalStore();
            store.AddGoal(new Goal
            {
                Id = "cancel-goal",
                Description = "A goal to cancel",
                Status = GoalStatus.Pending,
            });

            var facade = GoalFacadeFor(store);
            var user = Principal(id);

            var result = await facade.CancelGoalAsync("cancel-goal", user);

            // THE SUCCESSFUL OUTCOME: the real confirmation, not a downstream error.
            Assert.True(result.Success, $"a {role} must be able to cancel: {result.Error}");
            Assert.NotNull(result.Value);
            Assert.Equal("Goal 'cancel-goal' has been cancelled.", result.Value!.Message);

            // THE OBSERVABLE EFFECT: the goal really was cancelled in the store.
            Assert.Contains("UpdateGoalStatusAsync", store.AllCalls);
            var (goalId, status) = store.StatusUpdates[^1];
            Assert.Equal("cancel-goal", goalId);
            Assert.Equal(GoalStatus.Failed, status);
        }
    }

    /// <summary>
    /// THE VIABLE CANCEL CONTROL, NEGATIVE SIDE: the SAME cancellation by a MEMBER is refused with
    /// Forbidden and records NOTHING.
    /// </summary>
    [Fact]
    public async Task Cancel_AMemberIsRefused_WhileOperatorAndAdminSucceed()
    {
        await SeedRolesAsync();
        await _auth.SeedAsync("viable-member", UserRole.Member);

        using var store = new RecordingGoalStore();
        store.AddGoal(new Goal
        {
            Id = "cancel-goal",
            Description = "A goal to cancel",
            Status = GoalStatus.Pending,
        });

        var facade = GoalFacadeFor(store);

        var refused = await facade.CancelGoalAsync("cancel-goal", Principal("viable-member"));

        Assert.Equal(FacadeErrorKind.Forbidden, refused.Kind);
        Assert.Equal(ForbiddenMessage, refused.Error);
        Assert.Empty(store.AllCalls);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  EXTEND-ITERATIONS — a real extended iteration budget
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE VIABLE EXTEND-ITERATIONS CONTROL: for BOTH the Operator and the Admin, extending a
    /// REAL Failed goal's iteration budget SUCCEEDS with the REAL confirmation message, and the
    /// observable effect is recorded — the pipeline's iteration budget was genuinely TOPPED UP.
    /// <para>
    /// This replaces the iteration-1 row that only reached a downstream BadRequest (an invalid
    /// count) and the Admin row that did not cover the operation at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExtendIterations_OperatorAndAdmin_SucceedWithARealExtendedBudget()
    {
        await SeedRolesAsync();

        foreach (var (role, id) in new[] { ("Operator", OperatorId), ("Admin", AdminId) })
        {
            using var store = new RecordingGoalStore();
            var goal = new Goal
            {
                Id = "extend-goal",
                Description = "A goal to extend",
                Status = GoalStatus.Failed,
                // The branchless resume is defined for an ITERATION-EXHAUSTION failure.
                FailureReason = "Exceeded max iterations",
            };
            store.AddGoal(goal);

            // A REAL failed pipeline for that goal, so the dispatcher can really resume it.
            var pipelineManager = new GoalPipelineManager();
            var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
            pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
            pipeline.AdvanceTo(GoalPhase.Failed);

            var budgetBefore = pipeline.IterationBudget.Allowed;

            var facade = GoalFacadeFor(store, pipelineManager);
            var user = Principal(id);

            var result = await facade.ExtendIterationsAsync("extend-goal", 5, user, Ct);

            // THE SUCCESSFUL OUTCOME: the real extended-budget confirmation, not a downstream error.
            Assert.True(result.Success, $"a {role} must be able to extend the budget: {result.Error}");
            Assert.NotNull(result.Value);
            Assert.Equal("Extended iteration budget by 5.", result.Value!.Message);

            // THE OBSERVABLE EFFECT: the budget really was topped up and the goal really resumed.
            Assert.True(
                pipeline.IterationBudget.Allowed > budgetBefore,
                $"the iteration budget must have been topped up (was {budgetBefore}, now {pipeline.IterationBudget.Allowed}).");
            Assert.Equal(GoalStatus.InProgress, goal.Status);
            Assert.Null(goal.FailureReason);
        }
    }

    /// <summary>
    /// THE VIABLE EXTEND-ITERATIONS CONTROL, NEGATIVE SIDE: the SAME extension by a MEMBER is
    /// refused with Forbidden and records NOTHING — not even the goal read the resume needs.
    /// </summary>
    [Fact]
    public async Task ExtendIterations_AMemberIsRefused_WhileOperatorAndAdminSucceed()
    {
        await SeedRolesAsync();
        await _auth.SeedAsync("viable-member", UserRole.Member);

        using var store = new RecordingGoalStore();
        store.AddGoal(new Goal
        {
            Id = "extend-goal",
            Description = "A goal to extend",
            Status = GoalStatus.Failed,
            FailureReason = "Exceeded max iterations",
        });

        var facade = GoalFacadeFor(store);

        var refused = await facade.ExtendIterationsAsync("extend-goal", 5, Principal("viable-member"), Ct);

        Assert.Equal(FacadeErrorKind.Forbidden, refused.Kind);
        Assert.Equal(ForbiddenMessage, refused.Error);
        Assert.Empty(store.AllCalls);
    }

    public void Dispose() => _auth.Dispose();
}
