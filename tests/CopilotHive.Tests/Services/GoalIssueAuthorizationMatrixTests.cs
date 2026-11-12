using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE EXHAUSTIVE GOAL/ISSUE AUTHORIZATION MATRIX, driven through the REAL
/// <see cref="GoalFacade"/> and <see cref="IssueFacade"/> over the REAL production
/// <see cref="UserService"/> directory (the <see cref="FacadeAuthorizationHarness"/> pattern) and
/// the REAL <see cref="HivePrincipalResolver"/>/<see cref="HivePolicy"/> pair.
/// <para>
/// EVERY CELL of the role × operation-class matrix is a NAMED row in this file. The matrix is:
/// </para>
/// <list type="table">
///   <item><term>Goal reads</term><description>list, detail, search, iterations, dependency statuses,
///   live detail, progress, linked issues — SharedRead (Viewer+).</description></item>
///   <item><term>Goal mutations</term><description>delete, approve/status, review-status, review,
///   cancel, extend-iterations, attach-release — ExecutionOperator (Operator+).</description></item>
///   <item><term>Issue reads</term><description>list, detail — SharedRead (Viewer+).</description></item>
///   <item><term>Issue create</term><description>InteractiveMember (Member+).</description></item>
///   <item><term>Issue updates</term><description>Type, status/triage, severity, title, description,
///   repositories, linked goal, combined — ExecutionOperator (Operator+).</description></item>
///   <item><term>Issue delete</term><description>ExecutionOperator (Operator+).</description></item>
/// </list>
/// <para>
/// EVERY row is a REFUSAL row paired with a DISCRIMINATING SUCCESS CONTROL for the same operation
/// and the same role class, so no refusal row can be satisfied by an implementation that simply
/// refuses everything. <see cref="HiveCapability.GlobalAdmin"/> is never consulted: the Operator
/// control rows prove the mutations are NOT admin-gated.
/// </para>
/// </summary>
public sealed class GoalIssueAuthorizationMatrixTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "matrix-viewer";
    private const string MemberId = "matrix-member";
    private const string OperatorId = "matrix-operator";
    private const string AdminId = "matrix-admin";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    /// <summary>Seeds all four roles so every cell can be exercised over the same directory.</summary>
    private async Task SeedAllRolesAsync()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The REAL live dashboard/progress pair (no pipelines, no progress), so the live detail
    /// resolves to "no live state" and the progress feed to an empty list through the REAL
    /// production services — never a scripted stand-in.
    /// </summary>
    private static (DashboardStateService State, ProgressReportService Progress) CreateLiveSources()
    {
        var pipelineManager = new GoalPipelineManager();
        var progressLog = new ProgressLog();
        var state = new DashboardStateService(
            new WorkerPool(),
            pipelineManager,
            new DashboardLogSink(),
            progressLog);
        return (state, new ProgressReportService(pipelineManager, progressLog));
    }

    /// <summary>Builds the production goal facade over a recording store for the given mode.</summary>
    private GoalFacade GoalFacadeFor(
        HiveAuthMode mode,
        RecordingGoalStore store,
        IReadOnlyList<Goal> goals,
        bool withRelease = false,
        bool withDispatcher = false,
        DashboardNotifier? notifier = null)
    {
        foreach (var goal in goals)
            store.AddGoal(goal);
        if (withRelease)
            store.AddRelease(new Release { Id = "rel-1", Tag = "v1.0.0" });

        var (state, progress) = CreateLiveSources();
        return new GoalFacade(
            store,
            new RecordingIssueStore(),
            new GoalReviewService(
                knowledgeGraph: null,
                configRepo: null,
                config: new HiveConfigFile
                {
                    Workers = { ["reviewer"] = new WorkerConfig { Model = "reviewer-model" } },
                },
                goalStore: store,
                brainRepoManager: null,
                stateDir: Path.GetTempPath(),
                logger: NullLogger<GoalReviewService>.Instance),
            notifier ?? new DashboardNotifier(),
            _auth.Resolver(mode),
            _auth.Policy,
            NullLogger<GoalFacade>.Instance,            repoManager: null,
            docCleanup: null,
            goalReadyNotifier: null,
            dispatcher: withDispatcher ? CreateDispatcher(store) : null,
            dashboardState: state,
            progressReports: progress);
    }

    /// <summary>Builds the production issue facade over a recording store for the given mode.</summary>
    private IssueFacade IssueFacadeFor(HiveAuthMode mode, RecordingIssueStore store, IEventBus? eventBus = null) =>
        new(store, eventBus, _auth.Resolver(mode), _auth.Policy, NullLogger<IssueFacade>.Instance);

    private static Goal DraftGoal(string id = "goal-1") => new()
    {
        Id = id,
        Description = "Authorization matrix goal",
        Status = GoalStatus.Draft,
        DependsOn = ["dep-1"],
    };

    private static Issue MakeIssue(string id) => new()
    {
        Id = id,
        Title = $"Title {id}",
        Description = "desc",
        Type = IssueType.Bug,
        Severity = IssueSeverity.Medium,
        Status = IssueStatus.Open,
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  GOAL READ MATRIX — SharedRead
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GOAL READ MATRIX — THE VIEWER IS GRANTED EVERY GOAL READ (the SharedRead non-vacuity
    /// control for all eight reads at once), and the ANONYMOUS caller is refused on every one.
    /// <para>
    /// Named per operation inside the test body so a failure names the operation that broke.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GoalReads_SharedRead_ViewerGranted_AnonymousRefused_EveryOperation()
    {
        await SeedAllRolesAsync();

        var reads = new (string Operation, Func<GoalFacade, ClaimsPrincipal?, Task<bool>> Call)[]
        {
            ("list", async (f, u) => (await f.GetGoalsAsync(u, Ct)).Success),
            ("detail", async (f, u) => (await f.GetGoalAsync("goal-1", u, Ct)).Success),
            ("search", async (f, u) => (await f.SearchGoalsAsync("goal", null, u, Ct)).Success),
            ("iterations", async (f, u) => (await f.GetIterationsAsync("goal-1", u, Ct)).Success),
            ("dependency-statuses", async (f, u) => (await f.GetDependencyStatusesAsync("goal-1", u, Ct)).Success),
            ("live-detail", async (f, u) => (await f.GetLiveGoalDetailAsync("goal-1", u, Ct)).Success),
            ("progress", async (f, u) => (await f.GetProgressAsync("goal-1", u, Ct)).Success),
            ("linked-issues", async (f, u) => (await f.GetLinkedIssuesAsync("goal-1", u, Ct)).Success),
        };

        using var viewerStore = new RecordingGoalStore();
        var viewerFacade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, viewerStore, [DraftGoal()]);
        var viewer = Principal(ViewerId);

        using var anonymousStore = new RecordingGoalStore();
        var anonymousFacade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, anonymousStore, [DraftGoal()]);

        foreach (var (operation, call) in reads)
        {
            Assert.True(
                await call(viewerFacade, viewer),
                $"a Viewer must hold shared read for the '{operation}' read");
            Assert.False(
                await call(anonymousFacade, null),
                $"an anonymous caller must be refused the '{operation}' read");
        }
    }

    /// <summary>
    /// GOAL READ MATRIX — THE MEMBER, OPERATOR AND ADMIN ARE GRANTED EVERY GOAL READ: the read
    /// gate is shared read, so no higher role is refused and no read is accidentally gated higher.
    /// </summary>
    [Fact]
    public async Task GoalReads_SharedRead_MemberOperatorAdminAllGranted()
    {
        await SeedAllRolesAsync();

        var reads = new (string Operation, Func<GoalFacade, ClaimsPrincipal?, Task<bool>> Call)[]
        {
            ("list", async (f, u) => (await f.GetGoalsAsync(u, Ct)).Success),
            ("detail", async (f, u) => (await f.GetGoalAsync("goal-1", u, Ct)).Success),
            ("search", async (f, u) => (await f.SearchGoalsAsync("goal", null, u, Ct)).Success),
            ("iterations", async (f, u) => (await f.GetIterationsAsync("goal-1", u, Ct)).Success),
            ("dependency-statuses", async (f, u) => (await f.GetDependencyStatusesAsync("goal-1", u, Ct)).Success),
            ("live-detail", async (f, u) => (await f.GetLiveGoalDetailAsync("goal-1", u, Ct)).Success),
            ("progress", async (f, u) => (await f.GetProgressAsync("goal-1", u, Ct)).Success),
            ("linked-issues", async (f, u) => (await f.GetLinkedIssuesAsync("goal-1", u, Ct)).Success),
        };

        foreach (var (role, id) in new[]
        {
            ("Member", MemberId),
            ("Operator", OperatorId),
            ("Admin", AdminId),
        })
        {
            using var store = new RecordingGoalStore();
            var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);
            var user = Principal(id);

            foreach (var (operation, call) in reads)
                Assert.True(await call(facade, user), $"a {role} must hold shared read for '{operation}'");
        }
    }

    /// <summary>
    /// GOAL READ MATRIX — A DISABLED ACCOUNT LOSES EVERY GOAL READ, INCLUDING THE READ A VIEWER
    /// WOULD OTHERWISE HOLD: the disablement is observed by the very next operation.
    /// </summary>
    [Fact]
    public async Task GoalReads_SharedRead_DisabledAccountRefusedOnEveryRead()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingGoalStore();
        var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);
        var user = Principal(ViewerId);

        Assert.True((await facade.GetGoalsAsync(user, Ct)).Success);

        await _auth.SetEnabledAsync(ViewerId, enabled: false);

        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.GetGoalsAsync(user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.GetGoalAsync("goal-1", user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.SearchGoalsAsync("goal", null, user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.GetIterationsAsync("goal-1", user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.GetDependencyStatusesAsync("goal-1", user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.GetLiveGoalDetailAsync("goal-1", user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.GetProgressAsync("goal-1", user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.GetLinkedIssuesAsync("goal-1", user, Ct)).Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GOAL MUTATION MATRIX — ExecutionOperator
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ZERO-STORE-WORK CONTRACT FOR THE GOAL READS: a caller who does NOT hold shared read
    /// (a DISABLED account, an anonymous caller and an unknown identity) is refused by every goal
    /// read, and every one of those refusals performs NO store work at all — including the SEARCH
    /// lookup and the ITERATION read, which no per-operation counter would otherwise see.
    /// <para>
    /// This is the refusal row that a lookup moved AHEAD of the gate would fail, so it is the
    /// read-side counterpart of the mutation refusal row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GoalReads_ARefusedCaller_PerformsNoStoreWorkOnAnyRead()
    {
        await SeedAllRolesAsync();

        var reads = new (string Operation, Func<GoalFacade, ClaimsPrincipal?, Task<FacadeErrorKind>> Call)[]
        {
            ("list", async (f, u) => (await f.GetGoalsAsync(u, Ct)).Kind),
            ("detail", async (f, u) => (await f.GetGoalAsync("goal-1", u, Ct)).Kind),
            ("search", async (f, u) => (await f.SearchGoalsAsync("goal", null, u, Ct)).Kind),
            ("iterations", async (f, u) => (await f.GetIterationsAsync("goal-1", u, Ct)).Kind),
            ("dependency-statuses", async (f, u) => (await f.GetDependencyStatusesAsync("goal-1", u, Ct)).Kind),
            ("live-detail", async (f, u) => (await f.GetLiveGoalDetailAsync("goal-1", u, Ct)).Kind),
            ("progress", async (f, u) => (await f.GetProgressAsync("goal-1", u, Ct)).Kind),
            ("linked-issues", async (f, u) => (await f.GetLinkedIssuesAsync("goal-1", u, Ct)).Kind),
        };

        // THE REFUSED CALLERS: a DISABLED account (which holds nothing), an anonymous caller and
        // an authenticated identity with no activated account.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);

        var callers = new (string Caller, ClaimsPrincipal? Principal)[]
        {
            ("disabled", Principal(ViewerId)),
            ("anonymous", null),
            ("unknown", Principal("never-invited")),
        };

        foreach (var (caller, principal) in callers)
        {
            foreach (var (operation, call) in reads)
            {
                using var store = new RecordingGoalStore();
                var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);

                var kind = await call(facade, principal);

                Assert.Equal(FacadeErrorKind.Forbidden, kind);
                Assert.Empty(store.AllCalls);
            }
        }
    }

    /// <summary>
    /// GOAL MUTATION MATRIX — EVERY GOAL MUTATION REFUSES THE VIEWER AND THE MEMBER WITH
    /// <see cref="FacadeErrorKind.Forbidden"/> AND PERFORMS NO STORE WORK.
    /// <para>
    /// Each row uses an input shape that would otherwise answer BadRequest/NotFound, so the
    /// refusal provably precedes the operation's own validation and store reads.
    /// </para>
    /// <para>
    /// THE ZERO-STORE-WORK ASSERTION IS WRITTEN AGAINST
    /// <see cref="RecordingGoalStore.AllCalls"/> — the COMPLETE observation surface — not against
    /// the per-operation counters. A counter can only observe the method it happens to count, so
    /// an implementation that moved the attach-release RELEASE lookup (<c>GetReleaseAsync</c>)
    /// ahead of the gate would still answer Forbidden and still leave every per-operation counter
    /// at zero. The observation-control row below proves this recorder DOES see that lookup for
    /// an authorized caller.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GoalMutations_ExecutionOperator_ViewerAndMemberRefusedOnEveryOperation()
    {
        await SeedAllRolesAsync();

        var mutations = new (string Operation, Func<GoalFacade, ClaimsPrincipal, Task<(FacadeErrorKind Kind, string? Error)>> Call)[]
        {
            ("delete", async (f, u) => { var r = await f.DeleteGoalAsync("goal-1", u); return (r.Kind, r.Error); }),
            ("approve/status", async (f, u) => { var r = await f.UpdateGoalStatusAsync("goal-1", "NotAStatus", u); return (r.Kind, r.Error); }),
            ("review-status", async (f, u) => { var r = await f.UpdateReviewStatusAsync("goal-1", "99", u); return (r.Kind, r.Error); }),
            ("review", async (f, u) => { var r = await f.RequestReviewAsync("goal-1", u, Ct); return (r.Kind, r.Error); }),
            ("cancel", async (f, u) => { var r = await f.CancelGoalAsync("goal-1", u); return (r.Kind, r.Error); }),
            ("extend-iterations", async (f, u) => { var r = await f.ExtendIterationsAsync("goal-1", 0, u, Ct); return (r.Kind, r.Error); }),
            ("attach-release", async (f, u) => { var r = await f.AttachReleaseAsync("goal-1", "missing-release", u); return (r.Kind, r.Error); }),
        };

        foreach (var actor in new[] { ViewerId, MemberId })
        {
            foreach (var (operation, call) in mutations)
            {
                using var store = new RecordingGoalStore();
                var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], withRelease: true);

                var (kind, error) = await call(facade, Principal(actor));

                Assert.Equal(FacadeErrorKind.Forbidden, kind);
                Assert.Equal(ForbiddenMessage, error);

                // NO store work whatsoever for a refused caller — not ONE IGoalStore method of
                // ANY kind, including the release lookup, the search and the iteration reads
                // that no per-operation counter would otherwise see.
                Assert.Empty(store.AllCalls);

                // The per-operation counters agree with the complete observation surface.
                Assert.Empty(store.ReadCalls);
                Assert.Empty(store.DeleteCalls);
                Assert.Empty(store.StatusUpdates);
                Assert.Empty(store.ResetCalls);
                Assert.Empty(store.GoalUpdates);
                Assert.Empty(store.ReleaseLookupCalls);
                Assert.Empty(store.SearchCalls);
                Assert.Empty(store.IterationReads);
                Assert.Equal(0, store.ReleaseReads);
                Assert.Equal(0, store.GetAllCalls);
            }
        }
    }

    /// <summary>
    /// THE OBSERVATION CONTROL for the zero-store-work assertion above: the SAME recorder DOES
    /// observe the attach-release RELEASE lookup (<c>GetReleaseAsync</c>), the goal SEARCH lookup
    /// (<c>SearchGoalsAsync</c>) and the ITERATION read (<c>GetIterationsAsync</c>) when the
    /// caller is AUTHORIZED.
    /// <para>
    /// So the refusal test's <c>Assert.Empty(store.AllCalls)</c> cannot pass merely because the
    /// recorder is blind to those methods: an implementation that moved a lookup ahead of the
    /// gate (while still answering Forbidden) would leave that method in <c>AllCalls</c> and
    /// fail <see cref="GoalMutations_ExecutionOperator_ViewerAndMemberRefusedOnEveryOperation"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GoalStoreObservationControls_TheRecorderSeesTheReleaseSearchAndIterationLookups()
    {
        await SeedAllRolesAsync();
        var operatorUser = Principal(OperatorId);

        // THE ATTACH-RELEASE RELEASE LOOKUP: an authorized caller performs it, and a MISSING
        // release performs it ALONE (the release is checked before the goal).
        using (var store = new RecordingGoalStore())
        {
            var facade = GoalFacadeFor(
                HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], withRelease: true);
            var result = await facade.AttachReleaseAsync("goal-1", "rel-1", operatorUser);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to attach a release");
            Assert.Equal(["GetReleaseAsync", "GetGoalAsync", "UpdateGoalAsync"], store.AllCalls);
            Assert.Equal(["rel-1"], store.ReleaseLookupCalls);
        }

        using (var store = new RecordingGoalStore())
        {
            var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);
            var result = await facade.AttachReleaseAsync("goal-1", "missing-release", operatorUser);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
            Assert.Equal(["GetReleaseAsync"], store.AllCalls);
        }

        // THE SEARCH LOOKUP: an authorized caller performs it.
        using (var store = new RecordingGoalStore())
        {
            var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);
            var result = await facade.SearchGoalsAsync("goal", null, operatorUser, Ct);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to search");
            Assert.Equal(["SearchGoalsAsync"], store.AllCalls);
            Assert.Equal(["goal"], store.SearchCalls);
        }

        // THE ITERATION READ: an authorized caller performs it.
        using (var store = new RecordingGoalStore())
        {
            var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);
            var result = await facade.GetIterationsAsync("goal-1", operatorUser, Ct);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to read the iterations");
            Assert.Equal(["GetIterationsAsync"], store.AllCalls);
            Assert.Equal(["goal-1"], store.IterationReads);
        }
    }

    /// <summary>
    /// GOAL MUTATION MATRIX — THE OPERATOR IS GRANTED EVERY GOAL MUTATION (and NOT an Admin):
    /// this is the row that proves the mutation gate is ExecutionOperator and NOT GlobalAdmin, and
    /// that the gate never refuses an authorized caller.
    /// </summary>
    [Fact]
    public async Task GoalMutations_ExecutionOperator_OperatorGrantedOnEveryOperation()
    {
        await SeedAllRolesAsync();
        var operatorUser = Principal(OperatorId);

        // DELETE (a Draft goal) succeeds.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .DeleteGoalAsync("goal-1", operatorUser);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to delete");
            Assert.Equal(["goal-1"], store.DeleteCalls);
        }

        // APPROVE / STATUS transition succeeds and is applied.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .UpdateGoalStatusAsync("goal-1", "Pending", operatorUser);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to approve");
            Assert.Equal([("goal-1", GoalStatus.Pending)], store.StatusUpdates);
        }

        // REVIEW STATUS succeeds and is persisted.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .UpdateReviewStatusAsync("goal-1", "Approved", operatorUser);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to set the review status");
            Assert.Equal(ReviewStatus.Approved, Assert.Single(store.GoalUpdates).ReviewStatus);
        }

        // REVIEW, CANCEL and EXTEND-ITERATIONS have their OWN viable success controls in
        // GoalIssueViableSuccessControlTests (a real verdict, a real cancellation and a real
        // extended iteration budget, for BOTH the Operator and the Admin). The rows below are
        // therefore the GATE-PASSAGE rows for this matrix: they prove the operation reached its
        // own validation/store work — which is what this file is about — and NOT that the role
        // can perform the operation, which the viable-control file proves with a real outcome.

        // REVIEW reaches the goal lookup (so the gate passed) and then reports NotFound.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [])
                .RequestReviewAsync("missing", operatorUser, Ct);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        }

        // CANCEL reaches the goal lookup (so the gate passed) and then reports NotFound. The
        // dispatcher is present, so the pre-facade "no dispatcher registered" exception is not
        // reached and the goal lookup is the first thing the operation does.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(
                HiveAuthMode.AuthenticationEnabled, store, [], withDispatcher: true)
                .CancelGoalAsync("missing", operatorUser);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
            Assert.Equal("Goal 'missing' not found.", result.Error);
        }

        // EXTEND-ITERATIONS reaches the range check (so the gate passed) and reports BadRequest.
        // The dispatcher is present, so the frozen "dispatcher availability FIRST" order does
        // not pre-empt the range check with a 503.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(
                HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], withDispatcher: true)
                .ExtendIterationsAsync("goal-1", 0, operatorUser, Ct);
            Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        }

        // ATTACH-RELEASE reaches the release lookup (so the gate passed) and succeeds.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], withRelease: true)
                .AttachReleaseAsync("goal-1", "rel-1", operatorUser);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to attach a release");
            Assert.Equal("rel-1", store.GoalUpdates[0].ReleaseId);
        }
    }

    /// <summary>
    /// GOAL MUTATION MATRIX — THE ADMIN IS GRANTED EVERY GOAL MUTATION TOO: the Admin row proves
    /// the gate is a MINIMUM role, not an exclusion of the top role.
    /// <para>
    /// REVIEW, CANCEL and EXTEND-ITERATIONS are covered for the Admin by the VIABLE success
    /// controls in <see cref="GoalIssueViableSuccessControlTests"/> (a real verdict, a real
    /// cancellation and a real extended iteration budget). The rows here are the GATE-PASSAGE
    /// rows: they prove the operation reached its own validation/store work.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GoalMutations_ExecutionOperator_AdminGrantedOnEveryOperation()
    {
        await SeedAllRolesAsync();
        var admin = Principal(AdminId);

        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .DeleteGoalAsync("goal-1", admin);
            Assert.True(result.Success, result.Error ?? "an Admin must be allowed to delete");
        }

        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .UpdateGoalStatusAsync("goal-1", "Pending", admin);
            Assert.True(result.Success, result.Error ?? "an Admin must be allowed to approve");
        }

        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .UpdateReviewStatusAsync("goal-1", "Approved", admin);
            Assert.True(result.Success, result.Error ?? "an Admin must be allowed to set the review status");
        }

        // REVIEW reaches the goal lookup (so the gate passed) and then reports NotFound. The
        // Admin's viable review success is in GoalIssueViableSuccessControlTests.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [])
                .RequestReviewAsync("missing", admin, Ct);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        }

        // CANCEL reaches the goal lookup (so the gate passed) and then reports NotFound. The
        // Admin's viable cancellation success is in GoalIssueViableSuccessControlTests.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(
                HiveAuthMode.AuthenticationEnabled, store, [], withDispatcher: true)
                .CancelGoalAsync("missing", admin);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
            Assert.Equal("Goal 'missing' not found.", result.Error);
        }

        // EXTEND-ITERATIONS reaches the range check (so the gate passed) and reports BadRequest.
        // The dispatcher is present, so the frozen "dispatcher availability FIRST" order does
        // not pre-empt the range check with a 503.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(
                HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], withDispatcher: true)
                .ExtendIterationsAsync("goal-1", 0, admin, Ct);
            Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        }

        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], withRelease: true)
                .AttachReleaseAsync("goal-1", "rel-1", admin);
            Assert.True(result.Success, result.Error ?? "an Admin must be allowed to attach a release");
        }
    }

    /// <summary>
    /// GOAL MUTATION MATRIX — A DEMOTED OPERATOR LOSES EVERY GOAL MUTATION ON THE VERY NEXT
    /// CALL, and the demotion lands on the FIRST mutation after the change.
    /// </summary>
    [Fact]
    public async Task GoalMutations_ExecutionOperator_DemotedOperatorRefusedOnEveryMutationNextCall()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingGoalStore();
        var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], withRelease: true);
        var user = Principal(OperatorId);

        // Before the demotion the Operator may mutate.
        Assert.True((await facade.UpdateGoalStatusAsync("goal-1", "Pending", user)).Success);

        await _auth.SetRoleAsync(OperatorId, UserRole.Viewer);

        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.DeleteGoalAsync("goal-1", user)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.UpdateGoalStatusAsync("goal-1", "Draft", user)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.UpdateReviewStatusAsync("goal-1", "Approved", user)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.RequestReviewAsync("goal-1", user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.CancelGoalAsync("goal-1", user)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.ExtendIterationsAsync("goal-1", 5, user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.AttachReleaseAsync("goal-1", "rel-1", user)).Kind);

        // Only the pre-demotion status change ever reached the store.
        Assert.Equal([("goal-1", GoalStatus.Pending)], store.StatusUpdates);
        Assert.Empty(store.DeleteCalls);
        Assert.Empty(store.GoalUpdates);
    }

    /// <summary>
    /// GOAL MUTATION MATRIX — A DISABLED OPERATOR LOSES EVERY GOAL MUTATION ON THE VERY NEXT CALL.
    /// </summary>
    [Fact]
    public async Task GoalMutations_ExecutionOperator_DisabledOperatorRefusedOnEveryMutationNextCall()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingGoalStore();
        var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], withRelease: true);
        var user = Principal(OperatorId);

        Assert.True((await facade.UpdateGoalStatusAsync("goal-1", "Pending", user)).Success);

        await _auth.SetEnabledAsync(OperatorId, enabled: false);

        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.DeleteGoalAsync("goal-1", user)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.UpdateGoalStatusAsync("goal-1", "Draft", user)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.UpdateReviewStatusAsync("goal-1", "Approved", user)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.RequestReviewAsync("goal-1", user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.CancelGoalAsync("goal-1", user)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.ExtendIterationsAsync("goal-1", 5, user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.AttachReleaseAsync("goal-1", "rel-1", user)).Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ISSUE READ MATRIX — SharedRead
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ISSUE READ MATRIX — EVERY ROLE IS GRANTED BOTH ISSUE READS (SharedRead), and the anonymous
    /// caller is refused on both.
    /// </summary>
    [Fact]
    public async Task IssueReads_SharedRead_EveryRoleGranted_AnonymousRefused()
    {
        await SeedAllRolesAsync();

        foreach (var (role, id) in new[]
        {
            ("Viewer", ViewerId),
            ("Member", MemberId),
            ("Operator", OperatorId),
            ("Admin", AdminId),
        })
        {
            using var store = new RecordingIssueStore();
            store.AddIssue(MakeIssue("issue-1"));
            var facade = IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store);
            var user = Principal(id);

            var list = await facade.GetIssuesAsync(new IssueFilter(), user, Ct);
            Assert.True(list.Success, $"a {role} must hold shared read for the issue list");
            Assert.Single(list.Value!);

            var single = await facade.GetIssueAsync("issue-1", user, Ct);
            Assert.True(single.Success, $"a {role} must hold shared read for the issue detail");
            Assert.Equal("issue-1", single.Value!.Id);
        }

        using var anonymousStore = new RecordingIssueStore();
        anonymousStore.AddIssue(MakeIssue("issue-1"));
        var anonymousFacade = IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, anonymousStore);

        Assert.Equal(FacadeErrorKind.Forbidden,
            (await anonymousFacade.GetIssuesAsync(new IssueFilter(), null, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden,
            (await anonymousFacade.GetIssueAsync("issue-1", null, Ct)).Kind);
        Assert.Empty(anonymousStore.AllCalls);
        Assert.Empty(anonymousStore.Tokens);
    }

    /// <summary>
    /// THE ISSUE-STORE OBSERVATION CONTROL: the SAME recorder DOES observe the issue list query
    /// and the issue existence read for an AUTHORIZED caller, so the refusal rows' empty
    /// <see cref="RecordingIssueStore.AllCalls"/> cannot pass merely because the recorder is blind
    /// to those methods.
    /// </summary>
    [Fact]
    public async Task IssueStoreObservationControls_TheRecorderSeesTheListQueryAndTheExistenceRead()
    {
        await SeedAllRolesAsync();
        var operatorUser = Principal(OperatorId);

        using (var store = new RecordingIssueStore())
        {
            store.AddIssue(MakeIssue("issue-1"));
            var facade = IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store);
            var result = await facade.GetIssuesAsync(new IssueFilter(), operatorUser, Ct);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to list the issues");
            Assert.Equal(["GetIssuesAsync"], store.AllCalls);
        }

        using (var store = new RecordingIssueStore())
        {
            store.AddIssue(MakeIssue("issue-1"));
            var facade = IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store);
            var result = await facade.GetIssueAsync("issue-1", operatorUser, Ct);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to read an issue");
            Assert.Equal(["GetIssueAsync"], store.AllCalls);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ISSUE CREATE MATRIX — InteractiveMember
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ISSUE CREATE MATRIX — THE VIEWER IS REFUSED (with an input shape that would otherwise be a
    /// BadRequest, so the gate provably precedes validation) and the MEMBER, OPERATOR AND ADMIN
    /// ARE GRANTED — which pins the gate at InteractiveMember, not at ExecutionOperator.
    /// </summary>
    [Fact]
    public async Task IssueCreate_InteractiveMember_ViewerRefused_MemberOperatorAdminGranted()
    {
        await SeedAllRolesAsync();

        using (var store = new RecordingIssueStore())
        {
            var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store)
                .CreateIssueAsync(
                    new CreateIssueRequest(Id: "Bad ID!", Type: null, Title: " ", Description: " "),
                    Principal(ViewerId), Ct);
            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
            Assert.Equal(ForbiddenMessage, result.Error);
            // NO store work whatsoever — the COMPLETE observation surface is empty.
            Assert.Empty(store.AllCalls);
            Assert.Empty(store.Tokens);
        }

        foreach (var (role, id) in new[]
        {
            ("Member", MemberId),
            ("Operator", OperatorId),
            ("Admin", AdminId),
        })
        {
            using var store = new RecordingIssueStore();
            var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store)
                .CreateIssueAsync(
                    new CreateIssueRequest(Id: $"issue-{role.ToLowerInvariant()}", Type: IssueType.Bug, Title: "T", Description: "D"),
                    Principal(id), Ct);
            Assert.True(result.Success, $"a {role} must be allowed to create an issue");
            Assert.Equal($"issue-{role.ToLowerInvariant()}", result.Value!.Id);
        }
    }

    /// <summary>
    /// ISSUE CREATE MATRIX — A MEMBER DEMOTED TO VIEWER LOSES THE CREATE ON THE VERY NEXT CALL.
    /// </summary>
    [Fact]
    public async Task IssueCreate_InteractiveMember_DemotedMemberRefusedOnNextCall()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingIssueStore();
        var facade = IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store);
        var user = Principal(MemberId);

        var first = await facade.CreateIssueAsync(
            new CreateIssueRequest(Id: "first", Type: IssueType.Bug, Title: "T", Description: "D"), user, Ct);
        Assert.True(first.Success, first.Error ?? "the Member's first create must succeed");

        await _auth.SetRoleAsync(MemberId, UserRole.Viewer);

        var second = await facade.CreateIssueAsync(
            new CreateIssueRequest(Id: "second", Type: IssueType.Bug, Title: "T", Description: "D"), user, Ct);
        Assert.Equal(FacadeErrorKind.Forbidden, second.Kind);
        Assert.Equal(ForbiddenMessage, second.Error);

        // Exactly ONE create reached the store.
        Assert.Single(store.Tokens);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ISSUE UPDATE / DELETE MATRIX — ExecutionOperator
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE COMPLETE ISSUE UPDATE SURFACE, ONE NAMED SHAPE EACH: Type, triage/status, severity,
    /// title, description, repositories, linked-goal-set, linked-goal-clear and the COMBINED
    /// multi-field update, plus the issue delete.
    /// </summary>
    private static readonly (string Name, UpdateIssueRequest Request)[] IssueUpdateShapes =
    [
        ("type", new UpdateIssueRequest(Type: IssueType.Bug)),
        ("triage/status", new UpdateIssueRequest(Status: IssueStatus.Resolved)),
        ("severity", new UpdateIssueRequest(Severity: IssueSeverity.High)),
        ("title", new UpdateIssueRequest(Title: "  ")),
        ("description", new UpdateIssueRequest(Description: "  ")),
        ("repositories", new UpdateIssueRequest(RepositoryNames: ["RepoA"])),
        ("linked-goal-set", new UpdateIssueRequest(LinkedGoalId: "goal-1")),
        ("linked-goal-clear", new UpdateIssueRequest(LinkedGoalId: "")),
        ("combined", new UpdateIssueRequest(
            Type: IssueType.Concern, Status: IssueStatus.Closed, Severity: IssueSeverity.Low,
            Title: "Combined", Description: "Combined", RepositoryNames: ["RepoA"], LinkedGoalId: "goal-1")),
    ];

    /// <summary>
    /// ISSUE UPDATE MATRIX — EVERY UPDATE SHAPE IS REFUSED FOR THE VIEWER AND THE MEMBER WITH
    /// <see cref="FacadeErrorKind.Forbidden"/>, and NO store work happens.
    /// </summary>
    [Fact]
    public async Task IssueUpdates_ExecutionOperator_ViewerAndMemberRefusedOnEveryShape()
    {
        await SeedAllRolesAsync();

        foreach (var actor in new[] { ViewerId, MemberId })
        {
            foreach (var (name, request) in IssueUpdateShapes)
            {
                using var store = new RecordingIssueStore();
                store.AddIssue(MakeIssue("issue-1"));

                var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store)
                    .UpdateIssueAsync("issue-1", request, Principal(actor), Ct);

                Assert.True(
                    result.Kind == FacadeErrorKind.Forbidden,
                    $"{actor} must be refused the '{name}' update, got {result.Kind}.");
                Assert.Equal(ForbiddenMessage, result.Error);
                Assert.Empty(store.Tokens);
            }
        }
    }

    /// <summary>
    /// ISSUE UPDATE MATRIX — THE OPERATOR IS GRANTED EVERY UPDATE SHAPE (and the Admin too), so
    /// no refusal row above can be satisfied by an implementation that refuses every update.
    /// <para>
    /// The blank Title/Description shapes still legitimately report their own BadRequest — that
    /// is exactly the discriminating pair: the SAME shape answers BadRequest for an Operator and
    /// Forbidden for a Member.
    /// </para>
    /// </summary>
    [Fact]
    public async Task IssueUpdates_ExecutionOperator_OperatorAndAdminGrantedOnEveryShape()
    {
        await SeedAllRolesAsync();

        foreach (var (role, id) in new[] { ("Operator", OperatorId), ("Admin", AdminId) })
        {
            foreach (var (name, request) in IssueUpdateShapes)
            {
                using var store = new RecordingIssueStore();
                store.AddIssue(MakeIssue("issue-1"));

                var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store)
                    .UpdateIssueAsync("issue-1", request, Principal(id), Ct);

                Assert.NotEqual(FacadeErrorKind.Forbidden, result.Kind);
                if (name is "title" or "description")
                    Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
                else
                    Assert.True(result.Success, $"a {role} must be allowed the '{name}' update: {result.Error}");
            }
        }
    }

    /// <summary>
    /// ISSUE DELETE MATRIX — THE VIEWER AND THE MEMBER ARE REFUSED (no store work), and the
    /// OPERATOR AND THE ADMIN ARE GRANTED.
    /// </summary>
    [Fact]
    public async Task IssueDelete_ExecutionOperator_ViewerMemberRefused_OperatorAdminGranted()
    {
        await SeedAllRolesAsync();

        foreach (var actor in new[] { ViewerId, MemberId })
        {
            using var store = new RecordingIssueStore();
            store.AddIssue(MakeIssue("issue-1"));

            var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store)
                .DeleteIssueAsync("issue-1", Principal(actor), Ct);

            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
            Assert.Equal(ForbiddenMessage, result.Error);
            // NO store work whatsoever — the COMPLETE observation surface is empty.
            Assert.Empty(store.AllCalls);
            Assert.Empty(store.Tokens);
        }

        foreach (var id in new[] { OperatorId, AdminId })
        {
            using var store = new RecordingIssueStore();
            store.AddIssue(MakeIssue("issue-1"));

            var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store)
                .DeleteIssueAsync("issue-1", Principal(id), Ct);

            Assert.True(result.Success, result.Error ?? "an Operator/Admin must be allowed to delete an issue");
        }
    }

    /// <summary>
    /// ISSUE UPDATE / DELETE MATRIX — A DEMOTED OPERATOR LOSES EVERY UPDATE SHAPE AND THE DELETE
    /// ON THE VERY NEXT CALL.
    /// </summary>
    [Fact]
    public async Task IssueUpdatesAndDelete_ExecutionOperator_DemotedOperatorRefusedOnNextCall()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("issue-1"));
        var facade = IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store);
        var user = Principal(OperatorId);

        var first = await facade.UpdateIssueAsync("issue-1", new UpdateIssueRequest(Severity: IssueSeverity.High), user, Ct);
        Assert.True(first.Success, first.Error ?? "the Operator's first update must succeed");

        await _auth.SetRoleAsync(OperatorId, UserRole.Viewer);

        foreach (var (name, request) in IssueUpdateShapes)
        {
            var result = await facade.UpdateIssueAsync("issue-1", request, user, Ct);
            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
            Assert.Equal(ForbiddenMessage, result.Error);
        }

        var delete = await facade.DeleteIssueAsync("issue-1", user, Ct);
        Assert.Equal(FacadeErrorKind.Forbidden, delete.Kind);
    }

    /// <summary>
    /// ISSUE UPDATE / DELETE MATRIX — A DISABLED OPERATOR LOSES EVERY UPDATE SHAPE AND THE DELETE
    /// ON THE VERY NEXT CALL.
    /// </summary>
    [Fact]
    public async Task IssueUpdatesAndDelete_ExecutionOperator_DisabledOperatorRefusedOnNextCall()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("issue-1"));
        var facade = IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store);
        var user = Principal(OperatorId);

        Assert.True((await facade.UpdateIssueAsync(
            "issue-1", new UpdateIssueRequest(Severity: IssueSeverity.High), user, Ct)).Success);

        await _auth.SetEnabledAsync(OperatorId, enabled: false);

        foreach (var (name, request) in IssueUpdateShapes)
        {
            var result = await facade.UpdateIssueAsync("issue-1", request, user, Ct);
            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        }

        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.DeleteIssueAsync("issue-1", user, Ct)).Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  OPEN MODE
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OPEN MODE RESOLVES THE EXPLICIT LOCAL OPERATOR: with no configured authentication a null
    /// identity performs every covered goal read AND mutation and every covered issue operation
    /// exactly as the pre-multi-user installation did — backward compatibility, not a bypass,
    /// because the mode is an explicit host input.
    /// </summary>
    [Fact]
    public async Task OpenMode_LocalOperator_PerformsEveryCoveredOperation()
    {
        using var goalStore = new RecordingGoalStore();
        var goalFacade = GoalFacadeFor(HiveAuthMode.Open, goalStore, [DraftGoal()], withRelease: true);

        // Reads.
        Assert.True((await goalFacade.GetGoalsAsync(null, Ct)).Success);
        Assert.True((await goalFacade.GetGoalAsync("goal-1", null, Ct)).Success);
        Assert.True((await goalFacade.SearchGoalsAsync("goal", null, null, Ct)).Success);
        Assert.True((await goalFacade.GetIterationsAsync("goal-1", null, Ct)).Success);
        Assert.True((await goalFacade.GetDependencyStatusesAsync("goal-1", null, Ct)).Success);
        Assert.True((await goalFacade.GetLiveGoalDetailAsync("goal-1", null, Ct)).Success);
        Assert.True((await goalFacade.GetProgressAsync("goal-1", null, Ct)).Success);
        Assert.True((await goalFacade.GetLinkedIssuesAsync("goal-1", null, Ct)).Success);

        // Mutations.
        Assert.True((await goalFacade.UpdateGoalStatusAsync("goal-1", "Pending", null)).Success);
        Assert.True((await goalFacade.AttachReleaseAsync("goal-1", "rel-1", null)).Success);
        Assert.True((await goalFacade.UpdateReviewStatusAsync("goal-1", "Approved", null)).Success);

        using var issueStore = new RecordingIssueStore();
        var issueFacade = IssueFacadeFor(HiveAuthMode.Open, issueStore);

        Assert.True((await issueFacade.GetIssuesAsync(new IssueFilter(), null, Ct)).Success);
        Assert.True((await issueFacade.CreateIssueAsync(
            new CreateIssueRequest(Id: "open-issue", Type: IssueType.Bug, Title: "T", Description: "D"),
            null, Ct)).Success);
        Assert.True((await issueFacade.UpdateIssueAsync(
            "open-issue", new UpdateIssueRequest(Severity: IssueSeverity.High), null, Ct)).Success);
        Assert.True((await issueFacade.DeleteIssueAsync("open-issue", null, Ct)).Success);
    }

    /// <summary>
    /// THE DENY-BEFORE-SIDE-EFFECT CONTRACT, PROVEN THROUGH THE EVENT BUS: a refused issue
    /// update publishes NO <see cref="EventType.IssueResolved"/> event and writes NO store row,
    /// while the SAME update by an Operator publishes the event EXACTLY once and writes exactly
    /// once.
    /// <para>
    /// This is the discriminating pair for the ordering contract: an implementation that denied
    /// AFTER the side effect would publish the event for the refused caller, and an
    /// implementation that never published it would fail the Operator row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task IssueUpdate_DenyRunsBeforeTheSideEffect_PublishesNoEventAndWritesNoRow()
    {
        await SeedAllRolesAsync();

        var bus = new RecordingEventBus();
        var resolvedRequest = new UpdateIssueRequest(Status: IssueStatus.Resolved);

        // THE REFUSED CALLER: no event, no store write.
        using (var store = new RecordingIssueStore())
        {
            store.AddIssue(MakeIssue("issue-1"));
            var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store, bus)
                .UpdateIssueAsync("issue-1", resolvedRequest, Principal(MemberId), Ct);

            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
            Assert.Empty(bus.Published);
            // The refused update performed NO store call at all — not even the existence read.
            Assert.Empty(store.AllCalls);
            Assert.Empty(store.Tokens);
        }

        // THE AUTHORIZED CALLER: exactly one event, exactly one write.
        using (var store = new RecordingIssueStore())
        {
            store.AddIssue(MakeIssue("issue-1"));
            var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store, bus)
                .UpdateIssueAsync("issue-1", resolvedRequest, Principal(OperatorId), Ct);

            Assert.True(result.Success, result.Error ?? "an Operator must be allowed the update");
            var published = Assert.Single(bus.Published);
            Assert.Equal(EventType.IssueResolved, published.Type);
            Assert.Equal("issue-1", published.IssueId);
        }
    }

    /// <summary>
    /// THE DENY-BEFORE-SIDE-EFFECT CONTRACT FOR ISSUE CREATE: a refused create publishes NO
    /// <see cref="EventType.IssueRaised"/> event and creates NO issue, while the same create by a
    /// Member publishes the event exactly once.
    /// </summary>
    [Fact]
    public async Task IssueCreate_DenyRunsBeforeTheSideEffect_PublishesNoEventAndCreatesNoIssue()
    {
        await SeedAllRolesAsync();

        var bus = new RecordingEventBus();
        var request = new CreateIssueRequest(Id: "created-issue", Type: IssueType.Bug, Title: "T", Description: "D");

        using (var store = new RecordingIssueStore())
        {
            var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store, bus)
                .CreateIssueAsync(request, Principal(ViewerId), Ct);

            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
            Assert.Empty(bus.Published);
            Assert.Empty(store.AllCalls);
            Assert.Empty(store.Tokens);
        }

        using (var store = new RecordingIssueStore())
        {
            var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store, bus)
                .CreateIssueAsync(request, Principal(MemberId), Ct);

            Assert.True(result.Success, result.Error ?? "a Member must be allowed to create an issue");
            var published = Assert.Single(bus.Published);
            Assert.Equal(EventType.IssueRaised, published.Type);
            Assert.Equal("created-issue", published.IssueId);
        }
    }

    /// <summary>
    /// THE DENY-BEFORE-SIDE-EFFECT CONTRACT FOR THE GOAL REVIEW-STATUS UPDATE: a refused update
    /// writes NO goal row and notifies NO dashboard listener, while the same update by an
    /// Operator writes exactly once and notifies exactly once.
    /// </summary>
    [Fact]
    public async Task GoalReviewStatus_DenyRunsBeforeTheSideEffect_NotifiesNothingAndWritesNoRow()
    {
        await SeedAllRolesAsync();

        // THE REFUSED CALLER: no write, no notification.
        using (var store = new RecordingGoalStore())
        {
            var notifier = new CountingDashboardNotifier();
            var facade = GoalFacadeFor(
                HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], notifier: notifier.Notifier);

            var result = await facade.UpdateReviewStatusAsync("goal-1", "Approved", Principal(MemberId));

            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
            Assert.Empty(store.GoalUpdates);
            Assert.Equal(0, notifier.Count);
        }

        // THE AUTHORIZED CALLER: exactly one write, exactly one notification.
        using (var store = new RecordingGoalStore())
        {
            var notifier = new CountingDashboardNotifier();
            var facade = GoalFacadeFor(
                HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], notifier: notifier.Notifier);

            var result = await facade.UpdateReviewStatusAsync("goal-1", "Approved", Principal(OperatorId));

            Assert.True(result.Success, result.Error ?? "an Operator must be allowed the review status");
            Assert.Single(store.GoalUpdates);
            Assert.Equal(1, notifier.Count);
        }
    }

    public void Dispose() => _auth.Dispose();

    // ── Shared fakes ────────────────────────────────────────────────────────

    /// <summary>
    /// An <see cref="IEventBus"/> that RECORDS every published event, so a refused operation can
    /// be proven to have published NOTHING.
    /// </summary>
    private sealed class RecordingEventBus : IEventBus
    {
        /// <summary>Every event published, in order.</summary>
        public List<SystemEvent> Published { get; } = [];

        /// <inheritdoc />
        public event Action<SystemEvent>? OnEvent;

        /// <inheritdoc />
        public void Publish(SystemEvent evt)
        {
            Published.Add(evt);
            OnEvent?.Invoke(evt);
        }
    }

    /// <summary>
    /// A <see cref="DashboardNotifier"/> that COUNTS the notifications through its own
    /// <see cref="DashboardNotifier.OnStateChanged"/> subscription, so a refused operation can be
    /// proven to have notified NOBODY.
    /// </summary>
    private sealed class CountingDashboardNotifier
    {
        private readonly DashboardNotifier _notifier = new();

        /// <summary>The number of notifications raised.</summary>
        public int Count { get; private set; }

        /// <summary>The notifier the facade receives.</summary>
        public DashboardNotifier Notifier => _notifier;

        /// <summary>Starts counting the notifications.</summary>
        public CountingDashboardNotifier()
        {
            _notifier.OnStateChanged += () => Count++;
        }
    }

    /// <summary>
    /// A REAL <see cref="GoalDispatcher"/> over the recording store, so the cancel path reaches
    /// the production dispatcher rather than the "no dispatcher registered" exception.
    /// </summary>
    private static GoalDispatcher CreateDispatcher(IGoalStore goalStore)
    {
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        return new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new CopilotHive.Git.BrainRepoManager(Path.GetTempPath(), NullLogger<CopilotHive.Git.BrainRepoManager>.Instance),
            goalStore: goalStore);
    }

}