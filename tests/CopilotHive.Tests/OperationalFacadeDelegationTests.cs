using System.Security.Claims;

using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE DELEGATION PROOF for the authorized operational mutation boundary: the facade's
/// <see cref="IOperationalFacade.ResetBrainSessionAsync"/> must actually REACH the Brain behind the
/// trusted <see cref="DashboardStateService"/> when — and only when — the caller currently holds
/// <see cref="HiveCapability.ExecutionOperator"/>.
/// <para>
/// The companion suite (<see cref="OperationalFacadeAuthorizationTests"/>) pins the ANSWER (allowed
/// or <see cref="FacadeErrorKind.Forbidden"/>). It cannot, on its own, tell an ACTUAL delegation
/// from a facade that merely returns a success flag: a facade that never called the trusted service
/// would pass every answer-level row. These rows close that gap by putting an OBSERVABLE seam
/// behind the trusted service — a recording <see cref="IDistributedBrain"/> whose
/// <see cref="IDistributedBrain.ResetSessionAsync"/> call count is the observation — so every row
/// asserts the REAL effect:
/// </para>
/// <list type="bullet">
///   <item>an Operator and an Admin: the reset reaches the Brain EXACTLY ONCE (the mutation
///   happened, not just the flag);</item>
///   <item>a Viewer, a Member, a DISABLED Operator, an unknown identity, a signed-out caller and an
///   anonymous principal: the reset NEVER reaches the Brain (the refusal is not a no-op success) and
///   the answer carries the standard sanitized forbidden message;</item>
///   <item>OPEN MODE: the resolver's <c>LocalOperator</c> outcome passes the gate and the reset
///   really reaches the Brain;</item>
///   <item>FRESH RESOLUTION: one facade instance, two calls — a demoted or a disabled account is
///   refused on its NEXT call and the Brain is NOT reset a second time.</item>
/// </list>
/// <para>
/// Every row drives the REAL production facade over the REAL production
/// <see cref="HivePrincipalResolver"/>/<see cref="HivePolicy"/> pair and the REAL production
/// <see cref="UserService"/> over an in-memory SQLite directory
/// (<see cref="FacadeAuthorizationHarness"/>), so a refusal is an actual resolution answer and an
/// allow is an actual delegation.
/// </para>
/// </summary>
public sealed class OperationalFacadeDelegationTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "deleg-viewer";
    private const string MemberId = "deleg-member";
    private const string OperatorId = "deleg-operator";
    private const string AdminId = "deleg-admin";
    private const string UnknownId = "deleg-unknown";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A recording Brain behind the trusted dashboard service. The dashboard service is the
    /// production type (it is sealed), so the observable seam is the Brain it delegates the reset
    /// to: <see cref="IDistributedBrain.ResetSessionAsync"/> is exactly the call
    /// <see cref="DashboardStateService.ResetBrainSessionAsync"/> makes.
    /// </summary>
    private sealed class RecordingBrain : IDistributedBrain
    {
        /// <summary>How many times the master session reset actually reached the Brain.</summary>
        internal int ResetCount { get; private set; }

        /// <summary>The cancellation token the last reset was invoked with.</summary>
        internal CancellationToken LastResetToken { get; private set; }

        /// <summary>The statistics the dashboard projects for the Brain (so the page can render it).</summary>
        internal BrainStats Stats { get; } = new()
        {
            Model = "recording-model",
            MessageCount = 7,
            ContextTokens = 1_000,
            MaxContextTokens = 10_000,
            ContextUsagePercent = 10,
            IsConnected = true,
        };

        public BrainStats? GetStats() => Stats;

        public Task ResetSessionAsync(CancellationToken ct = default)
        {
            ResetCount++;
            LastResetToken = ct;
            return Task.CompletedTask;
        }

        // ── Everything else is an inert no-op: these rows never exercise the Brain's work. ──

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success("recorded prompt"));

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult("recorded summary");

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
            Task.FromResult(BrainResponse.Answer("recorded answer"));

        public Task UpdateModelAsync(string model, int? maxContextTokens, ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;
    }

    /// <summary>
    /// The trusted dashboard service over the recording Brain. The dashboard service is the
    /// PRODUCTION type — the facade's dependency is the concrete class, so the only seam available
    /// is the Brain behind it.
    /// </summary>
    private static (DashboardStateService Dashboard, RecordingBrain Brain) NewDashboard()
    {
        var brain = new RecordingBrain();
        var dashboard = new DashboardStateService(
            new WorkerPool(),
            new GoalPipelineManager(),
            new DashboardLogSink(),
            new ProgressLog(),
            notifier: null,
            brain: brain);
        return (dashboard, brain);
    }

    private OperationalFacade FacadeFor(
        HiveAuthMode mode, DashboardStateService dashboard, LlmSessionRegistry registry) =>
        _auth.OperationalFacade(mode, dashboard, registry);

    private async Task SeedAllRolesAsync()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);
    }

    private static void AssertForbidden(FacadeResult result, string scenario)
    {
        Assert.False(result.Success, $"'{scenario}' must be refused.");
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(FacadeAuthorization.ForbiddenMessage, result.Error);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. ALLOWED — the reset REALLY reaches the Brain
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ALLOWED PATH DELEGATES: an Operator's reset reaches the Brain behind the trusted
    /// dashboard service EXACTLY ONCE, and the caller's cancellation token is forwarded verbatim.
    /// This is the positive control that the refusal rows below really observe the absence of a
    /// delegation (a facade that never called the Brain would fail this row).
    /// </summary>
    [Fact]
    public async Task ResetBrainSession_AsOperator_ActuallyDelegatesToTheBrain()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var (dashboard, brain) = NewDashboard();
        using var _ = dashboard;
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, new LlmSessionRegistry());

        var result = await facade.ResetBrainSessionAsync(User(OperatorId), Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Null(result.Error);

        // THE DELEGATION REALLY HAPPENED.
        Assert.Equal(1, brain.ResetCount);

        // The caller's token is forwarded, not swallowed or replaced.
        Assert.Equal(Ct, brain.LastResetToken);
    }

    /// <summary>
    /// AN ADMIN ALSO DELEGATES: the gate is "ExecutionOperator and above", so the bootstrap/Admin
    /// account resets the master session exactly like an Operator does.
    /// </summary>
    [Fact]
    public async Task ResetBrainSession_AsAdmin_ActuallyDelegatesToTheBrain()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var (dashboard, brain) = NewDashboard();
        using var _ = dashboard;
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, new LlmSessionRegistry());

        var result = await facade.ResetBrainSessionAsync(User(AdminId), Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, brain.ResetCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. DENIED — the reset NEVER reaches the Brain
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REFUSED PATH NEVER TOUCHES THE BRAIN: a Viewer, a Member, a DISABLED Operator, an
    /// UNKNOWN (never activated) identity, a signed-out caller and an anonymous principal are all
    /// refused with the standard sanitized <see cref="FacadeErrorKind.Forbidden"/> answer — and the
    /// master session is left completely alone (the Brain was never reset).
    /// </summary>
    [Fact]
    public async Task ResetBrainSession_AsAnUnauthorizedCaller_NeverDelegatesToTheBrain()
    {
        await SeedAllRolesAsync();

        var (dashboard, brain) = NewDashboard();
        using var _ = dashboard;
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, new LlmSessionRegistry());

        // ── Below ExecutionOperator. ──
        AssertForbidden(await facade.ResetBrainSessionAsync(User(ViewerId), Ct), "Viewer reset");
        AssertForbidden(await facade.ResetBrainSessionAsync(User(MemberId), Ct), "Member reset");

        // ── The role is sufficient, the ACCOUNT is not: a disabled Operator. ──
        await _auth.SetEnabledAsync(OperatorId, enabled: false);
        AssertForbidden(
            await facade.ResetBrainSessionAsync(User(OperatorId), Ct), "disabled operator reset");

        // ── No activated account at all. ──
        AssertForbidden(await facade.ResetBrainSessionAsync(User(UnknownId), Ct), "unknown account reset");

        // ── Signed out and anonymous. ──
        AssertForbidden(await facade.ResetBrainSessionAsync(null, Ct), "signed-out reset");
        AssertForbidden(
            await facade.ResetBrainSessionAsync(FacadeAuthorizationHarness.Anonymous(), Ct),
            "anonymous principal reset");

        // NOT ONE OF THOSE REACHED THE BRAIN.
        Assert.Equal(0, brain.ResetCount);

        // ── The success control, so the row above is not vacuously green: the same facade, the
        //    same Brain and the same directory — only the caller's role differs. ──
        await _auth.SetEnabledAsync(OperatorId, enabled: true);
        var allowed = await facade.ResetBrainSessionAsync(User(OperatorId), Ct);
        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal(1, brain.ResetCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. OPEN MODE — the local operator is allowed AND the reset is delegated
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OPEN MODE IS UNCHANGED, AND IT REALLY RESETS: with authentication disabled the resolver
    /// answers the legacy <c>LocalOperator</c> for the forwarded identity (a Blazor circuit in open
    /// mode has none, so it forwards <c>null</c>), that principal holds every capability, and the
    /// reset reaches the Brain exactly as the pre-facade direct service call did.
    /// </summary>
    [Fact]
    public async Task ResetBrainSession_InOpenMode_AllowsTheLocalOperator_AndDelegates()
    {
        var (dashboard, brain) = NewDashboard();
        using var _ = dashboard;
        var facade = FacadeFor(HiveAuthMode.Open, dashboard, new LlmSessionRegistry());

        var result = await facade.ResetBrainSessionAsync(OpenModeActingUser.User, Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Equal(1, brain.ResetCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4. FRESH RESOLUTION — a demotion/disablement takes effect on the NEXT call
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ONE FACADE INSTANCE, TWO ANSWERS: an Operator's first reset is allowed and really reaches
    /// the Brain; the account is then DEMOTED to Viewer, and the very next reset is refused — and
    /// the Brain is NOT reset a second time. A facade that cached a principal, or a resolver that
    /// cached a role, would let the second reset through.
    /// </summary>
    [Fact]
    public async Task ResetBrainSession_AfterADemotion_IsRefusedAndTheBrainIsNotResetAgain()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var (dashboard, brain) = NewDashboard();
        using var _ = dashboard;
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, new LlmSessionRegistry());

        var beforeDemotion = await facade.ResetBrainSessionAsync(User(OperatorId), Ct);
        Assert.True(beforeDemotion.Success, beforeDemotion.Error);
        Assert.Equal(1, brain.ResetCount);

        await _auth.SetRoleAsync(OperatorId, UserRole.Viewer);

        AssertForbidden(
            await facade.ResetBrainSessionAsync(User(OperatorId), Ct), "demoted operator reset");

        // The master session was NOT reset a second time.
        Assert.Equal(1, brain.ResetCount);

        // ── Promoted back: the capability returns on the next call, and the reset really happens. ──
        await _auth.SetRoleAsync(OperatorId, UserRole.Operator);
        var afterPromotion = await facade.ResetBrainSessionAsync(User(OperatorId), Ct);
        Assert.True(afterPromotion.Success, afterPromotion.Error);
        Assert.Equal(2, brain.ResetCount);
    }

    /// <summary>
    /// THE SAME FRESHNESS CONTRACT FOR A DISABLEMENT: an Operator's reset is allowed and delegated,
    /// the account is then DISABLED, and the very next reset is refused — with the master session
    /// left alone.
    /// </summary>
    [Fact]
    public async Task ResetBrainSession_AfterADisablement_IsRefusedAndTheBrainIsNotResetAgain()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var (dashboard, brain) = NewDashboard();
        using var _ = dashboard;
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, new LlmSessionRegistry());

        var beforeDisable = await facade.ResetBrainSessionAsync(User(OperatorId), Ct);
        Assert.True(beforeDisable.Success, beforeDisable.Error);
        Assert.Equal(1, brain.ResetCount);

        await _auth.SetEnabledAsync(OperatorId, enabled: false);

        AssertForbidden(
            await facade.ResetBrainSessionAsync(User(OperatorId), Ct), "disabled operator reset");
        Assert.Equal(1, brain.ResetCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  5. THE SESSION PROJECTION REALLY EXCLUDES COMPOSER — with a positive control
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SESSION-PRIVACY CONTRACT, PROVEN AGAINST THE REAL REGISTRY: a seeded
    /// <see cref="LlmSessionType.Composer"/> session really exists in
    /// <see cref="LlmSessionRegistry.GetAll"/> (the positive control that makes the exclusion
    /// assertion non-vacuous), the shared projection contains ZERO Composer entries, and every
    /// non-Composer entry survives — so the filter removes Composer rather than emptying the
    /// projection.
    /// </summary>
    [Fact]
    public async Task SharedLlmSessions_ExcludeTheSeededComposerSession_WhileTheRegistryKeepsIt()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var (dashboard, _) = NewDashboard();
        using var _ = dashboard;

        var registry = new LlmSessionRegistry();
        registry.RegisterOrUpdate(Session("composer-private", LlmSessionType.Composer));
        registry.RegisterOrUpdate(Session("brain-shared", LlmSessionType.Brain));
        registry.RegisterOrUpdate(Session("goal-shared", LlmSessionType.BrainGoal));
        registry.RegisterOrUpdate(Session("review-shared", LlmSessionType.GoalReview));

        // THE POSITIVE CONTROL: the Composer session really is in the registry before the read.
        var before = registry.GetAll();
        Assert.Contains(before, s => s.SessionId == "composer-private");
        Assert.Contains(before, s => s.SessionType == LlmSessionType.Composer);

        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, registry);

        var result = await facade.GetSharedLlmSessionsAsync(User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);

        var projected = result.Value!;

        // ZERO Composer entries — the private metadata never leaves through the shared projection.
        Assert.DoesNotContain(projected, s => s.SessionType == LlmSessionType.Composer);
        Assert.DoesNotContain(projected, s => s.SessionId == "composer-private");

        // Every shared execution session survives: the filter removes Composer, not everything.
        Assert.Equal(
            ["brain-shared", "goal-shared", "review-shared"],
            projected.Select(s => s.SessionId).OrderBy(id => id, StringComparer.Ordinal));

        // The registry's own semantics are UNCHANGED: internal callers still see the Composer entry.
        var after = registry.GetAll();
        Assert.Equal(4, after.Count);
        Assert.Contains(after, s => s.SessionId == "composer-private");
        Assert.Contains(after, s => s.SessionType == LlmSessionType.Composer);
    }

    /// <summary>
    /// THE SHARED-READ GATE COVERS THE PROJECTION: a Viewer (the minimum SharedRead role) reads
    /// the sanitized session list, while a signed-out caller is refused — and the refusal carries
    /// NO value, so nothing leaks out of a refused read.
    /// </summary>
    [Fact]
    public async Task SharedLlmSessions_AreReadableByAViewer_AndRefusedForASignedOutCaller()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var (dashboard, _) = NewDashboard();
        using var _ = dashboard;

        var registry = new LlmSessionRegistry();
        registry.RegisterOrUpdate(Session("brain-shared", LlmSessionType.Brain));
        registry.RegisterOrUpdate(Session("composer-private", LlmSessionType.Composer));

        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, registry);

        var allowed = await facade.GetSharedLlmSessionsAsync(User(ViewerId), Ct);
        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal(["brain-shared"], allowed.Value!.Select(s => s.SessionId));

        var refused = await facade.GetSharedLlmSessionsAsync(null, Ct);
        Assert.False(refused.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, refused.Kind);
        Assert.Equal(FacadeAuthorization.ForbiddenMessage, refused.Error);
        Assert.Null(refused.Value);
    }

    private static LlmSessionInfo Session(string id, LlmSessionType type) => new()
    {
        SessionId = id,
        SessionType = type,
        Model = "m",
        Status = "active",
        MaxTokens = 1,
    };

    public void Dispose() => _auth.Dispose();
}
