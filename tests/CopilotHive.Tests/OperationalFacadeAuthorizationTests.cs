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
/// THE AUTHORIZATION CONTRACT of <see cref="IOperationalFacade"/> — the boundary over the live
/// operational surface the Orchestrator page reads and mutates:
/// <list type="bullet">
///   <item>Resetting the master Brain session requires <see cref="HiveCapability.ExecutionOperator"/>
///   (an Operator or an Admin), and a Viewer, a Member, a disabled account, an unknown/deleted
///   account and a signed-out caller are all refused with the sanitized
///   <see cref="FacadeErrorKind.Forbidden"/> result BEFORE the trusted
///   <see cref="DashboardStateService"/> is touched.</item>
///   <item>The three shared read projections require <see cref="HiveCapability.SharedRead"/>, so a
///   Viewer reads them — and the sanitized session projection omits exactly the
///   <see cref="LlmSessionType.Composer"/> entries while the registry keeps every one of them.</item>
///   <item>OPEN MODE keeps working: the resolver's <c>LocalOperator</c> holds every capability, so
///   the legacy single-operator installation observes no behavioural change.</item>
///   <item>Every operation re-resolves CURRENT stored state, so a demotion or a disablement between
///   two calls takes effect on the very next call — one facade instance, two answers.</item>
/// </list>
/// <para>
/// Every row drives the REAL production facade over the REAL production
/// <see cref="HivePrincipalResolver"/> over the REAL production <see cref="UserService"/> over an
/// in-memory SQLite directory (<see cref="FacadeAuthorizationHarness"/>), and over the REAL
/// <see cref="DashboardStateService"/> / <see cref="LlmSessionRegistry"/>, so a refusal is an actual
/// resolution answer and a success is an actual delegation.
/// </para>
/// </summary>
public sealed class OperationalFacadeAuthorizationTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "op-viewer";
    private const string MemberId = "op-member";
    private const string OperatorId = "op-operator";
    private const string AdminId = "op-admin";
    private const string UnknownId = "op-unknown";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static DashboardStateService NewDashboard() =>
        new(new WorkerPool(),
            new GoalPipelineManager(),
            new DashboardLogSink(),
            new ProgressLog());

    /// <summary>
    /// The production facade for <paramref name="mode"/> over a REAL dashboard service and a REAL
    /// registry, both supplied by the caller so the test owns their state.
    /// </summary>
    private OperationalFacade FacadeFor(
        HiveAuthMode mode, DashboardStateService dashboard, LlmSessionRegistry registry) =>
        new(dashboard, registry, _auth.Resolver(mode), _auth.Policy, NullLogger<OperationalFacade>.Instance,
            new ProgressReportService(new GoalPipelineManager(), new ProgressLog()));

    private async Task SeedAllRolesAsync()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);
    }

    private static LlmSessionInfo Session(string id, LlmSessionType type) => new()
    {
        SessionId = id,
        SessionType = type,
        Model = "m",
        Status = "active",
        MaxTokens = 1,
    };

    private static void AssertForbidden(FacadeResult result, string scenario)
    {
        Assert.False(result.Success, $"'{scenario}' must be refused.");
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);
    }

    private static void AssertForbidden<T>(FacadeResult<T> result, string scenario)
    {
        Assert.False(result.Success, $"'{scenario}' must be refused.");
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);

        // A denial never carries a value: nothing may leak out of a refused read.
        Assert.Null(result.Value);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. RESET BRAIN SESSION — ExecutionOperator only
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE RESET MATRIX: only an account currently holding
    /// <see cref="HiveCapability.ExecutionOperator"/> (Operator or Admin) may reset the master Brain
    /// session. A Viewer and a Member are refused, a signed-out caller is refused, and — the
    /// POSITIVE CONTROL — the Operator and the Admin both succeed, so the gate refuses the
    /// insufficient rather than everybody.
    /// </summary>
    [Fact]
    public async Task ResetBrainSession_RequiresExecutionOperator()
    {
        await SeedAllRolesAsync();

        using var dashboard = NewDashboard();
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, new LlmSessionRegistry());

        // ── Denied: below ExecutionOperator, and signed out. ──
        AssertForbidden(await facade.ResetBrainSessionAsync(User(ViewerId), Ct), "Viewer reset");
        AssertForbidden(
            await facade.ResetBrainSessionAsync(User(ViewerId), Ct), "Viewer reset (repeat)");
        AssertForbidden(await facade.ResetBrainSessionAsync(User(MemberId), Ct), "Member reset");
        AssertForbidden(await facade.ResetBrainSessionAsync(null, Ct), "signed-out reset");
        AssertForbidden(
            await facade.ResetBrainSessionAsync(FacadeAuthorizationHarness.Anonymous(), Ct),
            "anonymous principal reset");

        // ── SUCCESS CONTROL: an Operator and an Admin reset it. ──
        var byOperator = await facade.ResetBrainSessionAsync(User(OperatorId), Ct);
        Assert.True(byOperator.Success, byOperator.Error);
        Assert.Equal(FacadeErrorKind.None, byOperator.Kind);
        Assert.Null(byOperator.Error);

        var byAdmin = await facade.ResetBrainSessionAsync(User(AdminId), Ct);
        Assert.True(byAdmin.Success, byAdmin.Error);
        Assert.Equal(FacadeErrorKind.None, byAdmin.Kind);
    }

    /// <summary>
    /// A DISABLED operator and an UNKNOWN (never-activated / deleted) identity are refused on the
    /// reset — the resolver fails closed for both, and a re-enabled operator succeeds afterwards so
    /// the refusal is provably about CURRENT stored state rather than about the identity being
    /// permanently unusable.
    /// </summary>
    [Fact]
    public async Task ResetBrainSession_RefusesDisabledAndUnknownAccounts()
    {
        await SeedAllRolesAsync();

        using var dashboard = NewDashboard();
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, new LlmSessionRegistry());

        // A disabled Operator: the role is sufficient, the account is not.
        await _auth.SetEnabledAsync(OperatorId, enabled: false);
        AssertForbidden(await facade.ResetBrainSessionAsync(User(OperatorId), Ct), "disabled operator reset");

        // An identity with NO activated account at all.
        AssertForbidden(await facade.ResetBrainSessionAsync(User(UnknownId), Ct), "unknown account reset");

        // Re-enabling restores the capability: the refusal was CURRENT state, not a permanent verdict.
        await _auth.SetEnabledAsync(OperatorId, enabled: true);
        var allowedAgain = await facade.ResetBrainSessionAsync(User(OperatorId), Ct);
        Assert.True(allowedAgain.Success, allowedAgain.Error);
    }

    /// <summary>
    /// FRESH RESOLUTION PER CALL: ONE facade instance, TWO answers. An Operator is demoted to Viewer
    /// and the very next reset is refused; promoted back and the next one succeeds. A resolver that
    /// cached (or a facade that held) a principal would answer the stale role instead.
    /// </summary>
    [Fact]
    public async Task ResetBrainSession_ReResolvesCurrentRoleOnEveryCall()
    {
        await SeedAllRolesAsync();

        using var dashboard = NewDashboard();
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, new LlmSessionRegistry());

        var beforeDemotion = await facade.ResetBrainSessionAsync(User(OperatorId), Ct);
        Assert.True(beforeDemotion.Success, beforeDemotion.Error);

        await _auth.SetRoleAsync(OperatorId, UserRole.Viewer);
        AssertForbidden(
            await facade.ResetBrainSessionAsync(User(OperatorId), Ct), "demoted operator reset");

        await _auth.SetRoleAsync(OperatorId, UserRole.Operator);
        var afterPromotion = await facade.ResetBrainSessionAsync(User(OperatorId), Ct);
        Assert.True(afterPromotion.Success, afterPromotion.Error);
    }

    /// <summary>
    /// OPEN MODE IS UNCHANGED: with authentication disabled the resolver answers the legacy
    /// <c>LocalOperator</c> for the forwarded identity — which a Blazor circuit in open mode has
    /// none of, so it forwards <c>null</c> — and the reset therefore succeeds exactly as the direct
    /// service call did before this facade existed.
    /// </summary>
    [Fact]
    public async Task ResetBrainSession_SucceedsInOpenMode()
    {
        using var dashboard = NewDashboard();
        var facade = FacadeFor(HiveAuthMode.Open, dashboard, new LlmSessionRegistry());

        var result = await facade.ResetBrainSessionAsync(OpenModeActingUser.User, Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. SHARED READS — SharedRead for every role; Composer data never exposed
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY shared read answers a Viewer and up (only
    /// <see cref="HiveCapability.SharedRead"/> is needed) with the trusted service's own data, and a
    /// signed-out caller is refused — the read half of the matrix. The orchestrator info is
    /// deliberately checked for a real value, proving the read is a genuine delegation rather than a
    /// default-constructed stand-in.
    /// </summary>
    [Fact]
    public async Task SharedReads_AnswerEveryRole_AndRefuseASignedOutCaller()
    {
        await SeedAllRolesAsync();

        using var dashboard = NewDashboard();
        var registry = new LlmSessionRegistry();
        registry.RegisterOrUpdate(Session("brain-1", LlmSessionType.Brain));
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, registry);

        foreach (var caller in new[] { ViewerId, MemberId, OperatorId, AdminId })
        {
            var brain = await facade.GetBrainStatsAsync(User(caller), Ct);
            Assert.True(brain.Success, $"{caller}: {brain.Error}");

            // No Brain is configured on this dashboard service, so the service's own answer is
            // null — and null is a SUCCESS, not a denial (the page renders "Brain not connected.").
            Assert.Null(brain.Value);

            var info = await facade.GetOrchestratorInfoAsync(User(caller), Ct);
            Assert.True(info.Success, $"{caller}: {info.Error}");
            Assert.NotNull(info.Value);
            Assert.False(string.IsNullOrWhiteSpace(info.Value!.Version));

            var sessions = await facade.GetSharedLlmSessionsAsync(User(caller), Ct);
            Assert.True(sessions.Success, $"{caller}: {sessions.Error}");
            Assert.Equal(["brain-1"], sessions.Value!.Select(s => s.SessionId));
        }

        // ── Signed out: every shared read is refused, carrying no value. ──
        AssertForbidden(await facade.GetBrainStatsAsync(null, Ct), "signed-out brain stats");
        AssertForbidden(await facade.GetOrchestratorInfoAsync(null, Ct), "signed-out orchestrator info");
        AssertForbidden(await facade.GetSharedLlmSessionsAsync(null, Ct), "signed-out sessions");
    }

    /// <summary>
    /// THE SESSION-PRIVACY CONTRACT of the shared read projection:
    /// <list type="bullet">
    ///   <item>a registered <see cref="LlmSessionType.Composer"/> session is ABSENT from what a
    ///   Viewer reads, while all three SHARED execution session types are PRESENT (the positive
    ///   control proving the filter removes Composer rather than emptying the projection);</item>
    ///   <item><see cref="LlmSessionRegistry.GetAll"/> still returns EVERY entry — the registry's
    ///   semantics are provably unchanged, so the exclusion is the shared-read boundary only.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task SharedLlmSessions_ExcludeComposerEntries_WhileTheRegistryKeepsThem()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        using var dashboard = NewDashboard();
        var registry = new LlmSessionRegistry();

        var sharedTypes = new[]
        {
            LlmSessionType.Brain,
            LlmSessionType.BrainGoal,
            LlmSessionType.GoalReview,
        };
        foreach (var type in sharedTypes)
            registry.RegisterOrUpdate(Session($"shared-{type}", type));
        registry.RegisterOrUpdate(Session("private-composer", LlmSessionType.Composer));

        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, registry);

        var result = await facade.GetSharedLlmSessionsAsync(User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);

        var projected = result.Value!;
        foreach (var type in sharedTypes)
            Assert.Contains(projected, s => s.SessionId == $"shared-{type}");

        Assert.DoesNotContain(projected, s => s.SessionId == "private-composer");
        Assert.DoesNotContain(projected, s => s.SessionType == LlmSessionType.Composer);

        // The registry itself is UNCHANGED: internal callers still see all four entries.
        var all = registry.GetAll();
        Assert.Equal(4, all.Count);
        Assert.Contains(all, s => s.SessionId == "private-composer");
        Assert.Contains(all, s => s.SessionType == LlmSessionType.Composer);
    }

    /// <summary>
    /// THE SHARED PROJECTION IS THE ONE FILTER the <c>GET /api/sessions</c> route uses, and it is the
    /// exact complement of "keep everything except Composer": every non-Composer entry survives
    /// (nothing is dropped by accident) and the input sequence is never mutated. Together with the
    /// route's own tests this is what makes the two shared-read surfaces impossible to drift apart.
    /// </summary>
    [Fact]
    public void SharedSessionProjection_KeepsEveryNonComposerEntry_AndLeavesTheInputIntact()
    {
        var sessions = new List<LlmSessionInfo>
        {
            Session("a", LlmSessionType.Brain),
            Session("b", LlmSessionType.Composer),
            Session("c", LlmSessionType.BrainGoal),
            Session("d", LlmSessionType.GoalReview),
        };

        var projected = SharedSessionProjection.SharedOnly(sessions);

        Assert.Equal(["a", "c", "d"], projected.Select(s => s.SessionId));

        // The input is untouched: the filter PROJECTS, it never removes from the registry snapshot.
        Assert.Equal(4, sessions.Count);

        // An all-Composer input projects to an EMPTY list (never a failure)...
        Assert.Empty(SharedSessionProjection.SharedOnly([Session("x", LlmSessionType.Composer)]));

        // ...and an empty input stays empty.
        Assert.Empty(SharedSessionProjection.SharedOnly([]));
    }

    /// <summary>
    /// NO PRIVATE COMPOSER USAGE IS EXPOSED ON THE SHARED BOUNDARY:
    /// <see cref="IOperationalFacade"/> declares no operation that reports Composer statistics
    /// (<see cref="DashboardStateService.GetComposerStats"/> is per-user private usage), and neither
    /// does any projection it returns. This pins the OMISSION decision so a later change cannot
    /// quietly add the surface back without changing the boundary's contract on purpose.
    /// <para>
    /// The expected operation set is EXACT: the four original operations, the three knowledge
    /// <see cref="HiveCapability.SharedRead"/> reads added by the KNOWLEDGE/NAVIGATION AUTHORIZATION
    /// slice, and the three DASHBOARD/WORKERS/WORKER-PROGRESS display projections added by the
    /// OPERATIONAL READ PROJECTIONS slice — ten in all. Anything beyond that set (a Composer-statistics
    /// projection above all) fails here.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSharedBoundary_ExposesNoComposerUsageProjection()
    {
        var operations = typeof(IOperationalFacade)
            .GetMethods()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The boundary's operation set is EXACTLY the ten authorized operations: no more, no less.
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(IOperationalFacade.ResetBrainSessionAsync),
                nameof(IOperationalFacade.GetBrainStatsAsync),
                nameof(IOperationalFacade.GetOrchestratorInfoAsync),
                nameof(IOperationalFacade.GetSharedLlmSessionsAsync),
                // THE KNOWLEDGE/NAVIGATION SharedRead reads: the document list/search projection,
                // one document with its outgoing/incoming links, and the navigation badge count.
                nameof(IOperationalFacade.GetKnowledgeDocumentsAsync),
                nameof(IOperationalFacade.GetKnowledgeDocumentAsync),
                nameof(IOperationalFacade.GetKnowledgeDocumentCountAsync),
                // THE OPERATIONAL DISPLAY projections the Home / Workers / WorkerDetail surfaces read:
                // the dashboard display snapshot, the worker display rows + counts, and one worker's
                // approved progress rows.
                nameof(IOperationalFacade.GetDashboardDisplayAsync),
                nameof(IOperationalFacade.GetWorkerDisplayAsync),
                nameof(IOperationalFacade.GetWorkerProgressAsync),
            },
            operations);

        // No operation mentions the private per-user Composer usage surface at all. (Name is the only
        // distinguishing marker: a Composer-statistics operation would return the SAME
        // Task<FacadeResult<BrainStats>> shape as GetBrainStatsAsync, so the omission is enforced by
        // there being no such operation at all.)
        Assert.DoesNotContain(operations, name => name.Contains("Composer", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE EXACT SURFACE OF THE TEN OPERATIONS, PINNED ON BOTH TYPES: every operation's full signature
    /// — return type and parameter types in order — is asserted on <see cref="IOperationalFacade"/>
    /// AND on the public <see cref="OperationalFacade"/>, so the contract cannot drift on one side of
    /// the boundary while the other keeps compiling. The three display projections are spelled out in
    /// full because they are the values the pages bind to.
    /// </summary>
    [Fact]
    public void TheExactTenOperationSignatures_ArePinnedOnBothTypes()
    {
        var expected = new[]
        {
            "Task`1[FacadeResult`1[BrainStats]] GetBrainStatsAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[DashboardDisplaySnapshot]] GetDashboardDisplayAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[KnowledgeDocumentProjection]] GetKnowledgeDocumentAsync(String, ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[Int32]] GetKnowledgeDocumentCountAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[KnowledgeDocumentListProjection]] GetKnowledgeDocumentsAsync(String, ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[OrchestratorInfo]] GetOrchestratorInfoAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[IReadOnlyList`1[LlmSessionInfo]]] GetSharedLlmSessionsAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[WorkerDisplayProjection]] GetWorkerDisplayAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[IReadOnlyList`1[WorkerProgressDisplayEntry]]] GetWorkerProgressAsync(String, ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult] ResetBrainSessionAsync(ClaimsPrincipal, CancellationToken)",
        };
        Array.Sort(expected, StringComparer.Ordinal);

        foreach (var type in new[] { typeof(IOperationalFacade), typeof(OperationalFacade) })
        {
            var actual = type.GetMethods()
                // The concrete type also inherits Object's members; the facade's own public surface is
                // everything declared on it.
                .Where(method => method.DeclaringType == type)
                .Select(DescribeOperation)
                .OrderBy(signature => signature, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>Renders one operation as "ReturnType Name(ParameterTypes)".</summary>
    private static string DescribeOperation(System.Reflection.MethodInfo method) =>
        $"{DescribeType(method.ReturnType)} {method.Name}("
        + string.Join(", ", method.GetParameters().Select(parameter => DescribeType(parameter.ParameterType)))
        + ")";

    /// <summary>
    /// Renders a closed constructed type in its generic form, e.g.
    /// <c>Task`1[FacadeResult`1[BrainStats]]</c>, so the pinned signature names the exact value types.
    /// </summary>
    private static string DescribeType(Type type) => type.IsGenericType
        ? $"{type.GetGenericTypeDefinition().Name}[{string.Join(", ", type.GetGenericArguments().Select(DescribeType))}]"
        : type.Name;

    /// <summary>
    /// THERE IS NO IDENTITY-FREE OVERLOAD: for every operation name on the interface — and on the
    /// public implementation — there is EXACTLY ONE declared method, and none of them omits the
    /// acting identity. A same-name convenience overload that dropped the
    /// <see cref="ClaimsPrincipal"/> would silently reintroduce an unauthorized path, so it fails here
    /// even though every other row would still pass.
    /// </summary>
    [Fact]
    public void NoOperation_HasAnIdentityFreeOverload()
    {
        foreach (var type in new[] { typeof(IOperationalFacade), typeof(OperationalFacade) })
        {
            var declared = type.GetMethods()
                .Where(method => method.DeclaringType == type)
                .ToList();

            // One declared method per name: a second overload of the same name is a contract change.
            var duplicates = declared
                .GroupBy(method => method.Name, StringComparer.Ordinal)
                .Where(group => group.Count() != 1)
                .Select(group => group.Key)
                .ToArray();
            Assert.Empty(duplicates);

            // EVERY one of them takes the acting identity plus a cancellation token, and no operation
            // exists in an identity-free form: the only non-identity parameter any operation takes is
            // the argument it is reading (a search query, a document id or a worker id) together with
            // the token, so a convenience overload that dropped the principal has nowhere to hide.
            foreach (var method in declared)
            {
                var parameters = method.GetParameters().Select(parameter => parameter.ParameterType).ToList();

                Assert.True(
                    parameters.Contains(typeof(ClaimsPrincipal)),
                    $"'{type.Name}.{method.Name}' must take the acting identity.");
                Assert.True(
                    parameters.Contains(typeof(CancellationToken)),
                    $"'{type.Name}.{method.Name}' must take a cancellation token.");

                // At most ONE non-identity, non-token argument — the operation's own subject.
                Assert.True(
                    parameters.Count(type0 => type0 != typeof(CancellationToken)) <= 2,
                    $"'{type.Name}.{method.Name}' declares more arguments than the identity, one subject "
                    + "and the token; an identity-free overload would look exactly like this.");
            }
        }
    }

    /// <summary>
    /// THE FACADE IS A DELEGATION, NOT A REIMPLEMENTATION: the trusted services keep their public
    /// APIs — <see cref="DashboardStateService.ResetBrainSessionAsync"/> still takes only a
    /// cancellation token, <see cref="DashboardStateService.GetBrainStats"/>,
    /// <see cref="DashboardStateService.GetComposerStats"/> and
    /// <see cref="DashboardStateService.GetOrchestratorInfo"/> are still parameterless, and
    /// <see cref="LlmSessionRegistry.GetAll"/> still has no filter argument — so internal callers
    /// are provably untouched by this boundary.
    /// </summary>
    [Fact]
    public void TheTrustedServicesKeepTheirUnchangedPublicSurfaces()
    {
        var resetParameters = typeof(DashboardStateService)
            .GetMethod(nameof(DashboardStateService.ResetBrainSessionAsync))!
            .GetParameters();
        Assert.Single(resetParameters);
        Assert.Equal(typeof(CancellationToken), resetParameters[0].ParameterType);

        Assert.Empty(typeof(DashboardStateService).GetMethod(nameof(DashboardStateService.GetBrainStats))!.GetParameters());
        Assert.Empty(typeof(DashboardStateService).GetMethod(nameof(DashboardStateService.GetComposerStats))!.GetParameters());
        Assert.Empty(typeof(DashboardStateService).GetMethod(nameof(DashboardStateService.GetOrchestratorInfo))!.GetParameters());
        Assert.Empty(typeof(LlmSessionRegistry).GetMethod(nameof(LlmSessionRegistry.GetAll))!.GetParameters());
    }

    /// <summary>
    /// OPEN MODE keeps the reads working for the identity-free circuit (open mode forwards no
    /// principal): the resolver answers the local operator and every shared projection succeeds.
    /// </summary>
    [Fact]
    public async Task SharedReads_SucceedInOpenMode()
    {
        using var dashboard = NewDashboard();
        var registry = new LlmSessionRegistry();
        registry.RegisterOrUpdate(Session("brain-1", LlmSessionType.Brain));
        registry.RegisterOrUpdate(Session("composer-1", LlmSessionType.Composer));

        var facade = FacadeFor(HiveAuthMode.Open, dashboard, registry);

        Assert.True((await facade.GetBrainStatsAsync(OpenModeActingUser.User, Ct)).Success);
        Assert.True((await facade.GetOrchestratorInfoAsync(OpenModeActingUser.User, Ct)).Success);

        var sessions = await facade.GetSharedLlmSessionsAsync(OpenModeActingUser.User, Ct);
        Assert.True(sessions.Success, sessions.Error);

        // Open mode is NOT a privacy bypass: the Composer exclusion applies there too.
        Assert.Equal(["brain-1"], sessions.Value!.Select(s => s.SessionId));
    }

    /// <summary>
    /// THE FACADE NEVER CAPTURES THE SCOPED CIRCUIT ACCESSOR: a singleton holding it would leak one
    /// circuit's identity into every other circuit, so its fields are only the trusted services, the
    /// stateless resolver/policy and the logger.
    /// </summary>
    [Fact]
    public void TheSingletonFacade_NeverCapturesTheScopedAccessor()
    {
        var fieldTypes = typeof(OperationalFacade)
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .Select(f => f.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(CircuitUserPrincipalAccessor), fieldTypes);
        Assert.DoesNotContain(typeof(ClaimsPrincipal), fieldTypes);
        Assert.DoesNotContain(typeof(ActingUserPrincipal), fieldTypes);
    }

    public void Dispose() => _auth.Dispose();
}
