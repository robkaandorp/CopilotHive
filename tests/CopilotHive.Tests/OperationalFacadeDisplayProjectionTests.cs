using System.Reflection;
using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE AUTHORIZATION + PROJECTION CONTRACT of the three OPERATIONAL DISPLAY reads on
/// <see cref="IOperationalFacade"/> — the detached projections the Home, Workers and WorkerDetail
/// surfaces bind to:
/// <list type="bullet">
///   <item><see cref="IOperationalFacade.GetDashboardDisplayAsync"/> — the five FULL-SET goal counts,
///   only the two worker counts (no worker row), the newest twenty Draft/Completed/Failed goals in
///   creation-time-descending order, the active pipelines in their existing order, and the bounded
///   description preview.</item>
///   <item><see cref="IOperationalFacade.GetWorkerDisplayAsync"/> — every worker row PLUS all five
///   counts from ONE pool capture, with the existing status/availability semantics and the existing
///   premium-before-standard reasoning resolution, computed from COPIED configuration.</item>
///   <item><see cref="IOperationalFacade.GetWorkerProgressAsync"/> — the existing capped
///   <see cref="ProgressReportService.GetProgressForWorker(string, int)"/> result, filtered (never
///   extended) to the seven approved statuses while authentication is enabled, and returned raw only
///   in an EXPLICITLY open installation.</item>
/// </list>
/// <para>
/// Every behavioral row drives the REAL production facade over the REAL production
/// <see cref="HivePrincipalResolver"/>/<see cref="UserService"/> directory
/// (<see cref="FacadeAuthorizationHarness"/>), so a refusal is an actual resolution answer and a
/// success is an actual delegation. "NO DATA WAS READ" is distinguished from "no data was returned"
/// with a NAMED tripwire: the denied rows run against a facade whose protected dependency has been
/// faulted (a throwing goal store, a nulled dashboard/progress service), which would THROW if the
/// read were reached, while the matching authorized control does reach it and throws on purpose.
/// </para>
/// </summary>
public sealed class OperationalFacadeDisplayProjectionTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "display-viewer";
    private const string MemberId = "display-member";
    private const string OperatorId = "display-operator";
    private const string AdminId = "display-admin";
    private const string UnknownId = "display-unknown";
    private const string WorkerId = "display-worker";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A goal store whose EVERY read throws a NAMED tripwire, so "this operation read the goal store"
    /// is observable rather than inferred. Used only as a negative control: an operation that must not
    /// read goals succeeds while this store is installed.
    /// </summary>
    private sealed class ThrowingGoalStore : IGoalStore
    {
        internal const string Tripwire = "goal-store-read-tripwire";

        public string Name => "throwing-goal-store";

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
            string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
            string goalId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
            int? limit = null, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);
    }

    /// <summary>
    /// The REAL dashboard service over the supplied pool / pipeline manager / store / config, so the
    /// facade's delegation reaches production code and not a stand-in.
    /// </summary>
    private static DashboardStateService NewDashboard(
        WorkerPool? pool = null,
        GoalPipelineManager? pipelines = null,
        IGoalStore? goalStore = null,
        HiveConfigFile? config = null,
        ProgressLog? progressLog = null) =>
        new(pool ?? new WorkerPool(),
            pipelines ?? new GoalPipelineManager(),
            new DashboardLogSink(),
            progressLog ?? new ProgressLog(),
            goalStore: goalStore,
            config: config);

    private OperationalFacade FacadeFor(
        HiveAuthMode mode,
        DashboardStateService dashboard,
        ProgressReportService? progressReports = null) =>
        new(dashboard, new LlmSessionRegistry(), _auth.Resolver(mode), _auth.Policy,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OperationalFacade>.Instance,
            // The aggregation is a REQUIRED dependency; this fixture supplies a fresh, empty one when a
            // row has no live progress sources of its own to observe.
            progressReports ?? FacadeAuthorizationHarness.EmptyProgressReports());

    private async Task SeedAllRolesAsync()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);
    }

    private static Goal GoalWith(string id, GoalStatus status, DateTime createdAt, int? iterations = null) => new()
    {
        Id = id,
        Description = $"description of {id}",
        Status = status,
        CreatedAt = createdAt,
        Iterations = iterations,
    };

    private static void AssertForbidden<T>(FacadeResult<T> result, string scenario)
        where T : class
    {
        Assert.False(result.Success, $"'{scenario}' must be refused.");
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(FacadeAuthorization.ForbiddenMessage, result.Error);

        // A denial never carries a value: nothing may leak out of a refused read.
        Assert.Null(result.Value);
    }

    private static readonly DateTime BaseTime = new(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    // ══════════════════════════════════════════════════════════════════════════
    //  1. AUTHORIZATION — every enabled role reads; null/unknown/disabled refused
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ROLE MATRIX: all four enabled roles (Viewer, Member, Operator, Admin) read all three
    /// display projections — the positive control proving the SharedRead gate refuses the insufficient
    /// rather than everybody — while a signed-out caller, an anonymous principal, an unknown identity,
    /// a DELETED account and a DISABLED account are each refused on all three, carrying no value.
    /// </summary>
    [Fact]
    public async Task DisplayProjections_AnswerEveryRole_AndRefuseNullUnknownDisabledAndDeletedCallers()
    {
        await SeedAllRolesAsync();

        var pool = new WorkerPool();
        pool.RegisterWorker(WorkerId, []);
        using var dashboard = NewDashboard(pool: pool, goalStore: new InMemoryGoalStore());
        var progressLog = new ProgressLog();
        var pipelines = new GoalPipelineManager();
        var progressReports = new ProgressReportService(pipelines, progressLog);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, progressReports);

        foreach (var caller in new[] { ViewerId, MemberId, OperatorId, AdminId })
        {
            var dashboardDisplay = await facade.GetDashboardDisplayAsync(User(caller), Ct);
            Assert.True(dashboardDisplay.Success, $"{caller}: {dashboardDisplay.Error}");

            var workerDisplay = await facade.GetWorkerDisplayAsync(User(caller), Ct);
            Assert.True(workerDisplay.Success, $"{caller}: {workerDisplay.Error}");
            Assert.Equal(1, workerDisplay.Value!.TotalWorkers);

            var progress = await facade.GetWorkerProgressAsync(WorkerId, User(caller), Ct);
            Assert.True(progress.Success, $"{caller}: {progress.Error}");
            Assert.Empty(progress.Value!);
        }

        // ── Signed out / anonymous: refused on all three, carrying no value. ──
        AssertForbidden(await facade.GetDashboardDisplayAsync(null, Ct), "signed-out dashboard");
        AssertForbidden(await facade.GetWorkerDisplayAsync(null, Ct), "signed-out workers");
        AssertForbidden(await facade.GetWorkerProgressAsync(WorkerId, null, Ct), "signed-out progress");

        AssertForbidden(
            await facade.GetDashboardDisplayAsync(FacadeAuthorizationHarness.Anonymous(), Ct),
            "anonymous dashboard");
        AssertForbidden(
            await facade.GetWorkerDisplayAsync(FacadeAuthorizationHarness.Anonymous(), Ct),
            "anonymous workers");
        AssertForbidden(
            await facade.GetWorkerProgressAsync(WorkerId, FacadeAuthorizationHarness.Anonymous(), Ct),
            "anonymous progress");

        // ── Unknown (never activated) identity. ──
        AssertForbidden(await facade.GetDashboardDisplayAsync(User(UnknownId), Ct), "unknown dashboard");
        AssertForbidden(await facade.GetWorkerDisplayAsync(User(UnknownId), Ct), "unknown workers");
        AssertForbidden(await facade.GetWorkerProgressAsync(WorkerId, User(UnknownId), Ct), "unknown progress");

        // ── DISABLED account: the role is sufficient, the account is not. ──
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        AssertForbidden(await facade.GetDashboardDisplayAsync(User(ViewerId), Ct), "disabled dashboard");
        AssertForbidden(await facade.GetWorkerDisplayAsync(User(ViewerId), Ct), "disabled workers");
        AssertForbidden(await facade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct), "disabled progress");

        // Re-enabling restores the reads: the refusal was CURRENT state, not a verdict.
        await _auth.SetEnabledAsync(ViewerId, enabled: true);
        Assert.True((await facade.GetDashboardDisplayAsync(User(ViewerId), Ct)).Success);

        // ── DELETED account. ──
        await _auth.DeleteUserAsync(MemberId);
        AssertForbidden(await facade.GetDashboardDisplayAsync(User(MemberId), Ct), "deleted dashboard");
        AssertForbidden(await facade.GetWorkerDisplayAsync(User(MemberId), Ct), "deleted workers");
        AssertForbidden(await facade.GetWorkerProgressAsync(WorkerId, User(MemberId), Ct), "deleted progress");
    }

    /// <summary>
    /// THE DENIAL RUNS BEFORE ANY DATA SOURCE IS TOUCHED — proven by a NAMEABLE fault rather than by an
    /// identical denial payload: with the facade's protected dependencies faulted (a throwing goal
    /// store for the dashboard read, a nulled dashboard service for the workers read, a nulled
    /// progress-report service for the progress read) every denied caller still receives the sanitized
    /// Forbidden result, while the matching AUTHORIZED control reaches the dependency and the failure
    /// propagates. That distinguishes "no data was read" from "no data was returned".
    /// </summary>
    [Fact]
    public async Task DeniedCallers_NeverReachTheFaultedDataSource_WhileTheAuthorizedControlDoes()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        using var dashboard = NewDashboard(goalStore: new ThrowingGoalStore());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        // ── Denied: the faulted store is NEVER reached, so no tripwire exception escapes. ──
        AssertForbidden(await facade.GetDashboardDisplayAsync(null, Ct), "signed-out dashboard over a throwing store");
        AssertForbidden(
            await facade.GetDashboardDisplayAsync(User(UnknownId), Ct),
            "unknown dashboard over a throwing store");

        // ── Authorized control: the SAME facade, the SAME store, only the caller differs — the read is
        //    genuinely reached and its failure propagates instead of being rewritten as a denial. ──
        var reached = await Assert.ThrowsAsync<InvalidOperationException>(
            () => facade.GetDashboardDisplayAsync(User(ViewerId), Ct));
        Assert.Equal(ThrowingGoalStore.Tripwire, reached.Message);

        // ── The WORKERS read does not touch the goal store at all: the same throwing store cannot make
        //    it fail, so "this projection read no goals" is observed rather than assumed. ──
        var workers = await facade.GetWorkerDisplayAsync(User(ViewerId), Ct);
        Assert.True(workers.Success, workers.Error);

        // ── Denied workers/progress callers never reach the dashboard/progress services either:
        //    faulting those references makes an AUTHORIZED call throw, while a denial stays sanitized. ──
        var facadeType = typeof(OperationalFacade);
        var dashboardField = facadeType.GetField("_dashboard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var progressField = facadeType.GetField("_progressReports", BindingFlags.Instance | BindingFlags.NonPublic)!;

        dashboardField.SetValue(facade, null);
        try
        {
            AssertForbidden(await facade.GetWorkerDisplayAsync(null, Ct), "signed-out workers over a nulled dashboard");
            AssertForbidden(await facade.GetDashboardDisplayAsync(null, Ct), "signed-out dashboard over a nulled dashboard");
            await Assert.ThrowsAsync<NullReferenceException>(() => facade.GetWorkerDisplayAsync(User(ViewerId), Ct));
        }
        finally
        {
            dashboardField.SetValue(facade, dashboard);
        }

        // The progress read is faulted at its AGGREGATION: a real ProgressReportService whose pipeline
        // manager reference is nulled throws the moment it is asked for rows.
        var faulted = new ProgressReportService(new GoalPipelineManager(), new ProgressLog());
        typeof(ProgressReportService)
            .GetField("_pipelineManager", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(faulted, null);
        progressField.SetValue(facade, faulted);
        try
        {
            AssertForbidden(
                await facade.GetWorkerProgressAsync(WorkerId, null, Ct),
                "signed-out progress over a faulted aggregation");
            await Assert.ThrowsAsync<NullReferenceException>(
                () => facade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct));
        }
        finally
        {
            progressField.SetValue(facade, null);
        }
    }

    /// <summary>
    /// FRESH RESOLUTION ON EVERY CALL, ONE FACADE INSTANCE: a Viewer reads all three projections, the
    /// SAME principal's persisted role is then corrupted to an unrecognised value (which the resolver
    /// refuses rather than defaulting) and the very next calls are refused, and restoring the role
    /// makes the next calls succeed again. A facade that cached a principal — or a resolver that cached
    /// a role — would answer the stale allow.
    /// </summary>
    [Fact]
    public async Task DisplayProjections_ReResolveTheSameClaimssCurrentStoredStateOnEveryCall()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var pool = new WorkerPool();
        pool.RegisterWorker(WorkerId, []);
        using var dashboard = NewDashboard(pool: pool, goalStore: new InMemoryGoalStore());
        var progressReports = new ProgressReportService(new GoalPipelineManager(), new ProgressLog());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, progressReports);

        // The SAME principal instance is reused across every call below.
        var caller = User(ViewerId);

        Assert.True((await facade.GetDashboardDisplayAsync(caller, Ct)).Success);
        Assert.True((await facade.GetWorkerDisplayAsync(caller, Ct)).Success);
        Assert.True((await facade.GetWorkerProgressAsync(WorkerId, caller, Ct)).Success);

        // The persisted role becomes unreadable: the resolver refuses instead of falling back.
        _auth.SetRawRoleText(ViewerId, "not-a-hive-role");
        AssertForbidden(await facade.GetDashboardDisplayAsync(caller, Ct), "unreadable-role dashboard");
        AssertForbidden(await facade.GetWorkerDisplayAsync(caller, Ct), "unreadable-role workers");
        AssertForbidden(await facade.GetWorkerProgressAsync(WorkerId, caller, Ct), "unreadable-role progress");

        // Restored (as raw text, because the model cannot read the corrupted row it must be written
        // through SQL): the capability returns on the NEXT call, so the refusals were current state.
        _auth.SetRawRoleText(ViewerId, "admin");
        Assert.True((await facade.GetDashboardDisplayAsync(caller, Ct)).Success);
        Assert.True((await facade.GetWorkerDisplayAsync(caller, Ct)).Success);
        Assert.True((await facade.GetWorkerProgressAsync(WorkerId, caller, Ct)).Success);

        // …and a DISABLEMENT is answered on the very next call too.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        AssertForbidden(await facade.GetDashboardDisplayAsync(caller, Ct), "post-disable dashboard");
        AssertForbidden(await facade.GetWorkerDisplayAsync(caller, Ct), "post-disable workers");
        AssertForbidden(await facade.GetWorkerProgressAsync(WorkerId, caller, Ct), "post-disable progress");
    }

    /// <summary>
    /// OPEN MODE KEEPS WORKING for the identity-free circuit: the resolver answers the local operator
    /// and all three projections succeed. The progress read in open mode keeps the RAW status/details
    /// (its parity is pinned separately), and the dashboard/workers projections are the same detached
    /// values as in authenticated mode.
    /// </summary>
    [Fact]
    public async Task DisplayProjections_SucceedInOpenMode()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker(WorkerId, []);
        using var dashboard = NewDashboard(pool: pool, goalStore: new InMemoryGoalStore());
        var progressLog = new ProgressLog();
        var progressReports = new ProgressReportService(new GoalPipelineManager(), progressLog);
        progressLog.Add(WorkerId, "goal-open", "Building", "open mode details");

        var facade = FacadeFor(HiveAuthMode.Open, dashboard, progressReports);

        Assert.True((await facade.GetDashboardDisplayAsync(OpenModeActingUser.User, Ct)).Success);
        Assert.Equal(1, (await facade.GetWorkerDisplayAsync(OpenModeActingUser.User, Ct)).Value!.TotalWorkers);

        var progress = await facade.GetWorkerProgressAsync(WorkerId, OpenModeActingUser.User, Ct);
        Assert.True(progress.Success, progress.Error);
        Assert.Equal("Building", Assert.Single(progress.Value!).Status);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. DASHBOARD DISPLAY — full counts, bounded recent set, preview only
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FIVE COUNTS ARE FULL-SET COUNTS: more recent-eligible goals than the twenty-row cap are
    /// seeded, every count still reports the WHOLE set, the recent rows are the NEWEST twenty in
    /// creation-time-descending order (the oldest rows are the ones dropped), non-eligible statuses
    /// (Pending / InProgress / Cancelled) never appear, and the projection carries an iteration count
    /// that stays <c>null</c> for a goal that never finished.
    /// </summary>
    /// <remarks>
    /// THE MUTATIONS THIS KILLS: applying the cap to the COUNTS as well as the rows (the counts would
    /// collapse to at most twenty); ordering ascending, or ordering by id instead of creation time;
    /// taking the OLDEST twenty; and admitting a Pending/InProgress/Cancelled goal into the recent set.
    /// </remarks>
    [Fact]
    public async Task DashboardDisplay_KeepsFullSetCounts_AndReturnsTheNewestTwentyEligibleGoalsDescending()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var store = new InMemoryGoalStore();

        // 25 COMPLETED goals, one per minute: the FIRST five are the oldest.
        for (var index = 0; index < 25; index++)
        {
            store.AddGoal(GoalWith(
                $"completed-{index:00}", GoalStatus.Completed, BaseTime.AddMinutes(index),
                iterations: index is 0 or 24 ? 3 : null));
        }

        // 12 DRAFT + 8 FAILED goals, all newer than the completed set.
        for (var index = 0; index < 12; index++)
            store.AddGoal(GoalWith($"draft-{index:00}", GoalStatus.Draft, BaseTime.AddHours(1).AddMinutes(index)));
        for (var index = 0; index < 8; index++)
            store.AddGoal(GoalWith($"failed-{index:00}", GoalStatus.Failed, BaseTime.AddHours(2).AddMinutes(index)));

        // NON-ELIGIBLE rows: they contribute to their own count but never to the recent set.
        store.AddGoal(GoalWith("pending-1", GoalStatus.Pending, BaseTime.AddHours(3)));
        store.AddGoal(GoalWith("pending-2", GoalStatus.Pending, BaseTime.AddHours(3).AddMinutes(1)));
        store.AddGoal(GoalWith("active-1", GoalStatus.InProgress, BaseTime.AddHours(4)));
        store.AddGoal(GoalWith("cancelled-1", GoalStatus.Cancelled, BaseTime.AddHours(5)));

        using var dashboard = NewDashboard(goalStore: store);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var result = await facade.GetDashboardDisplayAsync(User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);
        var snapshot = result.Value!;

        // ── FULL-SET COUNTS: 25 completed, 12 draft, 2 pending, 1 active, 8 failed — none capped. ──
        Assert.Equal(12, snapshot.DraftGoals);
        Assert.Equal(2, snapshot.PendingGoals);
        Assert.Equal(1, snapshot.ActiveGoals);
        Assert.Equal(25, snapshot.CompletedGoals);
        Assert.Equal(8, snapshot.FailedGoals);

        // ── RECENT ROWS: exactly the cap, newest first, eligible statuses only. ──
        Assert.Equal(DashboardDisplaySnapshot.RecentGoalLimit, snapshot.RecentGoals.Count);

        var expectedNewestFirst = new[] { "failed-07", "failed-06", "failed-05", "failed-04", "failed-03",
            "failed-02", "failed-01", "failed-00", "draft-11", "draft-10", "draft-09", "draft-08",
            "draft-07", "draft-06", "draft-05", "draft-04", "draft-03", "draft-02", "draft-01", "draft-00" };
        Assert.Equal(expectedNewestFirst, snapshot.RecentGoals.Select(goal => goal.Id));

        // Every eligible status is represented and no other status got in.
        Assert.All(
            snapshot.RecentGoals,
            goal => Assert.Contains(
                goal.Status, new[] { GoalStatus.Draft, GoalStatus.Completed, GoalStatus.Failed }));
        Assert.DoesNotContain(snapshot.RecentGoals, goal => goal.Id is "pending-1" or "active-1" or "cancelled-1");

        // The oldest eligible rows are the ones the cap dropped.
        Assert.DoesNotContain(snapshot.RecentGoals, goal => goal.Id.StartsWith("completed-0", StringComparison.Ordinal));

        // ── THE ONLY PROJECTED FIELDS: id, display status, nullable iteration count. ──
        Assert.Equal(
            new[] { "Id", "Iterations", "Status" },
            typeof(DashboardGoalDisplayEntry).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// THE RECENT ROW'S ITERATION COUNT IS NULLABLE AND PRESERVED: a finished goal carries its count, a
    /// goal that never finished carries <c>null</c> (which the page renders as an em dash), so the
    /// projection never invents a zero.
    /// </summary>
    [Fact]
    public async Task DashboardDisplay_RecentRows_CarryTheNullableIterationCount()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var store = new InMemoryGoalStore();
        store.AddGoal(GoalWith("finished", GoalStatus.Completed, BaseTime, iterations: 4));
        store.AddGoal(GoalWith("never-ran", GoalStatus.Failed, BaseTime.AddMinutes(1), iterations: null));

        using var dashboard = NewDashboard(goalStore: store);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var snapshot = (await facade.GetDashboardDisplayAsync(User(ViewerId), Ct)).Value!;

        Assert.Equal(4, snapshot.RecentGoals.Single(goal => goal.Id == "finished").Iterations);
        Assert.Null(snapshot.RecentGoals.Single(goal => goal.Id == "never-ran").Iterations);
    }

    /// <summary>
    /// THE DASHBOARD PROJECTION RETURNS NO WORKER ROWS: it reports only <c>BusyWorkers</c> and
    /// <c>TotalWorkers</c> — the two numbers the page's worker card shows — and its declared shape has
    /// no worker collection at all. A busy and an idle worker are registered so the two counts are
    /// genuinely different (a projection that returned neither, or both as zero, would fail).
    /// </summary>
    [Fact]
    public async Task DashboardDisplay_ReturnsOnlyTheTwoWorkerCounts_AndNoWorkerRows()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var pool = new WorkerPool();
        pool.RegisterWorker("busy-1", []);
        pool.MarkBusy("busy-1", "task-1");
        pool.RegisterWorker("idle-1", []);
        pool.RegisterWorker("idle-2", []);

        using var dashboard = NewDashboard(pool: pool, goalStore: new InMemoryGoalStore());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var snapshot = (await facade.GetDashboardDisplayAsync(User(ViewerId), Ct)).Value!;

        Assert.Equal(3, snapshot.TotalWorkers);
        Assert.Equal(1, snapshot.BusyWorkers);

        // NO worker row, and no worker-row type reachable from the dashboard projection at all.
        var memberTypes = typeof(DashboardDisplaySnapshot).GetProperties()
            .SelectMany(property => Flatten(property.PropertyType))
            .ToArray();
        Assert.DoesNotContain(typeof(WorkerInfo), memberTypes);
        Assert.DoesNotContain(typeof(WorkerDisplayRow), memberTypes);
    }

    /// <summary>
    /// THE DESCRIPTION IS A PREVIEW AND ONLY A PREVIEW, at the three boundaries: unchanged when it is
    /// SHORTER than eighty characters, unchanged when it is EXACTLY eighty, and the first eighty plus
    /// an ellipsis when it is longer — with the omitted suffix absent from the whole projection. The
    /// pipeline rows keep their identity, phase and timestamps (including <c>CreatedAt</c>, the elapsed
    /// display's fallback) so nothing else the page renders is lost.
    /// </summary>
    [Fact]
    public async Task DashboardDisplay_PipelineRows_ProjectOnlyTheBoundedDescriptionPreview()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var shortText = new string('a', 79);
        var exactText = new string('b', OperationalTextPreview.DescriptionPreviewLength);
        var longText = new string('c', OperationalTextPreview.DescriptionPreviewLength) + "-THE-OMITTED-SUFFIX";
        Assert.Equal(80, OperationalTextPreview.DescriptionPreviewLength);
        Assert.True(longText.Length > OperationalTextPreview.DescriptionPreviewLength);

        var pipelines = new GoalPipelineManager();
        var store = new InMemoryGoalStore();

        var created = new Dictionary<string, GoalPipeline>();
        foreach (var (goalId, text) in new[] { ("short-goal", shortText), ("exact-goal", exactText), ("long-goal", longText) })
        {
            var goal = new Goal { Id = goalId, Description = text, CreatedAt = BaseTime };
            created[goalId] = pipelines.CreatePipeline(goal);
        }

        // The null-start control's fallback input, captured from the pipeline before projection.
        var shortPipelineCreatedAt = created["short-goal"].CreatedAt;
        Assert.Null(created["short-goal"].GoalStartedAt);

        // The long-goal pipeline also carries metrics, a POPULATED start timestamp and a terminal
        // completion, so the remaining cells are proven to still be projected beside the preview.
        created["long-goal"].Metrics.TotalTests = 12;
        created["long-goal"].Metrics.PassedTests = 11;
        created["long-goal"].Metrics.FailedTests = 1;
        created["long-goal"].Metrics.CoveragePercent = 87.5;

        // Seed a POPULATED start distinct from the pipeline's own runtime CreatedAt.
        created["long-goal"].GoalStartedAt = BaseTime.AddMinutes(30);
        // An iteration GREATER THAN 1: the budget's used count drives Iteration, so two consumed
        // retries make it 3 and a hard-coded 1 cannot pass.
        Assert.True(created["long-goal"].IterationBudget.TryConsume());
        Assert.True(created["long-goal"].IterationBudget.TryConsume());
        created["long-goal"].AdvanceTo(GoalPhase.Done);

        // SOURCE PREMISES, asserted before the projection is read.
        Assert.Equal(3, created["long-goal"].Iteration);
        Assert.NotEqual(created["long-goal"].CreatedAt, created["long-goal"].GoalStartedAt);
        Assert.Equal(BaseTime.AddMinutes(30), created["long-goal"].GoalStartedAt);
        Assert.NotNull(created["long-goal"].CompletedAt);

        using var dashboard = NewDashboard(pipelines: pipelines, goalStore: store);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var snapshot = (await facade.GetDashboardDisplayAsync(User(ViewerId), Ct)).Value!;
        Assert.Equal(3, snapshot.Pipelines.Count);

        var shortRow = snapshot.Pipelines.Single(row => row.GoalId == "short-goal");
        Assert.Equal(shortText, shortRow.DescriptionPreview);

        var exactRow = snapshot.Pipelines.Single(row => row.GoalId == "exact-goal");
        Assert.Equal(exactText, exactRow.DescriptionPreview);

        var longRow = snapshot.Pipelines.Single(row => row.GoalId == "long-goal");
        Assert.Equal(longText[..80] + "…", longRow.DescriptionPreview);
        Assert.DoesNotContain("THE-OMITTED-SUFFIX", longRow.DescriptionPreview, StringComparison.Ordinal);

        // Every projected string on the whole snapshot is free of the omitted suffix: the full
        // description never leaves the operation through any other field either.
        foreach (var row in snapshot.Pipelines)
            Assert.DoesNotContain("THE-OMITTED-SUFFIX", row.DescriptionPreview, StringComparison.Ordinal);

        // The rest of the row survives: metrics, phase, the iteration and the three timestamps.
        Assert.Equal(12, longRow.TotalTests);
        Assert.Equal(11, longRow.PassedTests);
        Assert.Equal(1, longRow.FailedTests);
        Assert.Equal(87.5, longRow.CoveragePercent);
        Assert.Equal(created["long-goal"].Phase.ToString(), longRow.Phase);

        // THE POPULATED VALUES, asserted EXACTLY (not source-vs-projection): a populated start
        // timestamp distinct from CreatedAt, an iteration greater than one, and the terminal
        // completion. A constant-null GoalStartedAt or a constant-1 Iteration fails here.
        Assert.Equal(3, longRow.Iteration);
        Assert.Equal(BaseTime.AddMinutes(30), longRow.GoalStartedAt);
        Assert.NotEqual(longRow.CreatedAt, longRow.GoalStartedAt);
        Assert.NotNull(longRow.CompletedAt);
        Assert.Equal(created["long-goal"].CompletedAt, longRow.CompletedAt);

        // THE NULL-START CONTROL, on the same snapshot: a pipeline that never started projects a null
        // start plus the CreatedAt the elapsed display falls back to, and no completion.
        Assert.Null(shortRow.GoalStartedAt);
        Assert.Null(shortRow.CompletedAt);
        Assert.Equal(shortPipelineCreatedAt, shortRow.CreatedAt);

        // CreatedAt is projected for every row, so the elapsed fallback always has an input.
        Assert.Equal(created["exact-goal"].CreatedAt, exactRow.CreatedAt);
        Assert.Equal(created["long-goal"].CreatedAt, longRow.CreatedAt);

        // The pipeline rows keep the snapshot's existing order.
        Assert.Equal(
            (await dashboard.GetSnapshot()).Pipelines.Select(row => row.GoalId),
            snapshot.Pipelines.Select(row => row.GoalId));
    }

    /// <summary>
    /// THE PREVIEW HELPER ITSELF, at exactly the boundary: 80 characters are unchanged, 81 gain one
    /// ellipsis and lose their last character. A general text sanitizer this is not — the text is not
    /// trimmed, escaped or otherwise rewritten.
    /// </summary>
    [Fact]
    public void DescriptionPreview_IsUnchangedThroughEightyCharacters_AndTruncatedBeyondIt()
    {
        Assert.Equal(string.Empty, OperationalTextPreview.Of(string.Empty));
        Assert.Equal("x", OperationalTextPreview.Of("x"));

        var exact = new string('b', 80);
        Assert.Equal(exact, OperationalTextPreview.Of(exact));

        var one = new string('d', 81);
        Assert.Equal(one[..80] + "…", OperationalTextPreview.Of(one));
        Assert.NotEqual(OperationalTextPreview.Of(one), one);

        // Whitespace and punctuation are NOT sanitized: this is a preview, not a cleaner.
        var spaced = "  padded text  ";
        Assert.Equal(spaced, OperationalTextPreview.Of(spaced));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. WORKER DISPLAY — one capture, five counts, detachment, reasoning
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ONE CAPTURE AND FIVE COUNTS: every count is derived from the SAME captured list as the rows, so
    /// the rows' own flags add up to the counts beside them, and the three populations here (clean idle,
    /// awaiting Ready, busy) are all separated — in particular the awaiting worker is counted as idle
    /// but NOT as available.
    /// </summary>
    /// <remarks>
    /// THE MUTATIONS THIS KILLS: deriving a count from a SECOND pool read (the rows and the counts
    /// would then be able to disagree), collapsing <c>Idle</c> into <c>Available</c>, dropping the
    /// awaiting-Ready subset, or reading the live worker dictionary instead of the detached capture.
    /// </remarks>
    [Fact]
    public async Task WorkerDisplay_derivesFiveCountsAndRowsFromOneCapturedList()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var pool = new WorkerPool();
        pool.RegisterWorker("clean", []);
        pool.RegisterWorker("busy", []);
        pool.MarkBusy("busy", "task-busy");

        // An AWAITING-READY worker: non-busy, yet withheld from selection, so it is counted as idle but
        // NOT as available — the distinction the five counts exist to make.
        var awaiting = pool.RegisterWorker(
            "awaiting", [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: true);
        pool.MarkBusy("awaiting", "task-awaiting");
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(awaiting, "task-awaiting"));
        Assert.True(awaiting.AwaitingWorkerReady);

        using var dashboard = NewDashboard(pool: pool);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var result = await facade.GetWorkerDisplayAsync(User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);
        var projection = result.Value!;

        Assert.Equal(3, projection.TotalWorkers);
        Assert.Equal(1, projection.BusyWorkers);

        // IDLE includes the awaiting worker (it is not busy) — it is NOT assignable capacity.
        Assert.Equal(2, projection.IdleWorkers);
        Assert.Equal(1, projection.AvailableWorkers);
        Assert.Equal(1, projection.AwaitingReadyWorkers);

        // THE ROWS AND THE COUNTS AGREE, because they came from one capture.
        Assert.Equal(projection.Workers.Count, projection.TotalWorkers);
        Assert.Equal(projection.Workers.Count(row => row.IsBusy), projection.BusyWorkers);
        Assert.Equal(projection.Workers.Count(row => !row.IsBusy), projection.IdleWorkers);
        Assert.Equal(projection.Workers.Count(row => row.IsAvailable), projection.AvailableWorkers);
        Assert.Equal(projection.Workers.Count(row => row.AwaitingWorkerReady), projection.AwaitingReadyWorkers);

        // The busy row's captured task and status presentation survive.
        var busyRow = projection.Workers.Single(row => row.Id == "busy");
        Assert.Equal("task-busy", busyRow.CurrentTaskId);
        Assert.Equal("Busy", busyRow.StatusLabel);
        Assert.True(busyRow.LastHeartbeat > DateTime.MinValue);
        Assert.True(busyRow.ConnectedAt > DateTime.MinValue);

        var cleanRow = projection.Workers.Single(row => row.Id == "clean");
        Assert.Equal("Available", cleanRow.StatusLabel);
        Assert.Equal("badge-green", cleanRow.StatusCssClass);

        // …and the withheld worker is distinguishable from the available one, never presented as it.
        var awaitingRow = projection.Workers.Single(row => row.Id == "awaiting");
        Assert.False(awaitingRow.IsBusy);
        Assert.False(awaitingRow.IsAvailable);
        Assert.True(awaitingRow.AwaitingWorkerReady);
        Assert.Equal("Awaiting Ready", awaitingRow.StatusLabel);
        Assert.NotEqual("badge-green", awaitingRow.StatusCssClass);
    }

    /// <summary>
    /// THE ROWS ARE DETACHED: a row handed out is not a view over the pool. Marking the worker busy,
    /// renaming its task and re-capturing afterwards leaves the ALREADY-RETURNED row untouched, while a
    /// FRESH call observes the new state — the second half is the positive control proving the
    /// staleness above is detachment and not a pool that never changed.
    /// </summary>
    [Fact]
    public async Task WorkerDisplay_RowsAreDetachedFromLaterPoolMutation()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var pool = new WorkerPool();
        var worker = pool.RegisterWorker(WorkerId, []);
        worker.CurrentModel = "first-model";
        worker.ContextUsagePercent = 10;

        using var dashboard = NewDashboard(pool: pool);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var first = (await facade.GetWorkerDisplayAsync(User(ViewerId), Ct)).Value!;
        var firstRow = Assert.Single(first.Workers);
        Assert.False(firstRow.IsBusy);
        Assert.Equal("first-model", firstRow.CurrentModel);
        Assert.Equal(10, firstRow.ContextUsagePercent);

        // Mutate the live instance afterwards.
        pool.MarkBusy(WorkerId, "task-after-capture");
        worker.CurrentModel = "second-model";
        worker.ContextUsagePercent = 55;

        // The row already handed out did NOT change…
        Assert.False(firstRow.IsBusy);
        Assert.Null(firstRow.CurrentTaskId);
        Assert.Equal("first-model", firstRow.CurrentModel);
        Assert.Equal(10, firstRow.ContextUsagePercent);

        // …while a FRESH call observes the new state (the positive control).
        var secondRow = Assert.Single((await facade.GetWorkerDisplayAsync(User(ViewerId), Ct)).Value!.Workers);
        Assert.True(secondRow.IsBusy);
        Assert.Equal("task-after-capture", secondRow.CurrentTaskId);
        Assert.Equal("second-model", secondRow.CurrentModel);
        Assert.Equal(55, secondRow.ContextUsagePercent);
    }

    /// <summary>
    /// THE REASONING BEHAVIOUR IS THE WORKERS PAGE'S, PRESERVED — and computed from a COPY of the role
    /// configuration, never from a live configuration object:
    /// <list type="bullet">
    ///   <item>a worker running the role's PREMIUM model shows the premium reasoning effort;</item>
    ///   <item>a worker running the role's STANDARD model (or falling back to it because it reported
    ///   no current model) shows the standard effort;</item>
    ///   <item>a role whose premium and standard models are IDENTICAL resolves to the PREMIUM effort,
    ///   because the premium comparison runs first;</item>
    ///   <item>an unmatched model shows the existing em dash.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// THE MUTATIONS THIS KILLS: matching the standard model before the premium one (the identical-models
    /// row would then render the standard effort); dropping the current-model/role fallback (the idle
    /// row would render no model at all); and replacing the em dash with an empty string or a made-up
    /// label for an unmatched model.
    /// </remarks>
    [Fact]
    public async Task WorkerDisplay_ResolvesPremiumBeforeStandard_AndKeepsTheUnmatchedEmDash()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var config = new HiveConfigFile
        {
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new()
                {
                    Model = "copilot/coder",
                    PremiumModel = "copilot/premium-coder",
                    ReasoningEffort = "low",
                    PremiumReasoningEffort = "extra_high",
                },
                ["reviewer"] = new()
                {
                    Model = "copilot/same-model",
                    PremiumModel = "copilot/same-model",
                    ReasoningEffort = "none",
                    PremiumReasoningEffort = "high",
                },
                ["tester"] = new() { ReasoningEffort = "medium" },
            },
        };

        var pool = new WorkerPool();

        // Premium model in flight.
        var premium = pool.RegisterWorker("premium-worker", []);
        premium.Role = WorkerRole.Coder;
        premium.CurrentModel = "copilot/premium-coder";

        // Standard model in flight.
        var standard = pool.RegisterWorker("standard-worker", []);
        standard.Role = WorkerRole.Coder;
        standard.CurrentModel = "copilot/coder";

        // No current model: the ROLE default is displayed, and drives the standard reasoning.
        var idle = pool.RegisterWorker("idle-worker", []);
        idle.Role = WorkerRole.Coder;

        // Identical standard/premium models: premium wins.
        var identical = pool.RegisterWorker("identical-worker", []);
        identical.Role = WorkerRole.Reviewer;
        identical.CurrentModel = "copilot/same-model";

        // An unknown model: neither comparison matches.
        var unmatched = pool.RegisterWorker("unmatched-worker", []);
        unmatched.Role = WorkerRole.Coder;
        unmatched.CurrentModel = "copilot/some-other-model";

        // An unassigned role: no role model to fall back to at all.
        var unassigned = pool.RegisterWorker("unassigned-worker", []);

        using var dashboard = NewDashboard(pool: pool, config: config);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var workers = (await facade.GetWorkerDisplayAsync(User(ViewerId), Ct)).Value!.Workers;

        var premiumRow = workers.Single(row => row.Id == "premium-worker");
        Assert.Equal("copilot/premium-coder", premiumRow.DisplayModel);
        Assert.Equal("Extra High", premiumRow.ReasoningLabel);

        var standardRow = workers.Single(row => row.Id == "standard-worker");
        Assert.Equal("copilot/coder", standardRow.DisplayModel);
        Assert.Equal("Low", standardRow.ReasoningLabel);

        var idleRow = workers.Single(row => row.Id == "idle-worker");
        Assert.Null(idleRow.CurrentModel);
        Assert.Equal("copilot/coder", idleRow.DisplayModel);
        Assert.Equal("Low", idleRow.ReasoningLabel);

        var identicalRow = workers.Single(row => row.Id == "identical-worker");
        Assert.Equal("High", identicalRow.ReasoningLabel);

        var unmatchedRow = workers.Single(row => row.Id == "unmatched-worker");
        Assert.Equal("copilot/some-other-model", unmatchedRow.DisplayModel);
        Assert.Equal("—", unmatchedRow.ReasoningLabel);

        var unassignedRow = workers.Single(row => row.Id == "unassigned-worker");
        Assert.Equal(nameof(WorkerRole.Unspecified), unassignedRow.Role);
        Assert.Null(unassignedRow.DisplayModel);
        Assert.Equal("—", unassignedRow.ReasoningLabel);

        // A ROLE WITH NO CONFIGURED MODEL renders the existing "(not configured)" sentinel rather than
        // an invented model name.
        var tester = pool.RegisterWorker("tester-worker", []);
        tester.Role = WorkerRole.Tester;
        var testerRow = (await facade.GetWorkerDisplayAsync(User(ViewerId), Ct)).Value!.Workers
            .Single(row => row.Id == "tester-worker");
        Assert.Equal("(not configured)", testerRow.DisplayModel);
        Assert.Equal("Medium", testerRow.ReasoningLabel);
    }

    /// <summary>
    /// THE WORKER PROJECTION IS BUILT FROM ONE POOL CAPTURE AND NOTHING ELSE: the production
    /// <see cref="DashboardStateService.GetWorkerDisplay"/> source contains exactly one
    /// <c>CaptureWorkerStatus()</c> call and no goal-store, pipeline-manager or Composer reference, and
    /// the facade's <see cref="IOperationalFacade.GetWorkerDisplayAsync"/> never routes through the
    /// whole-dashboard snapshot or through <see cref="DashboardStateService.GetOrchestratorInfo"/>.
    /// </summary>
    /// <remarks>
    /// A SECOND INDEPENDENT READ IS NOT OBSERVABLE FROM THE RESPONSE when the pool happens to be quiet,
    /// so this pins the SCOPE of the capture structurally — the same idiom
    /// <c>WorkerUtilizationServiceTests</c> uses for the identical claim.
    /// </remarks>
    [Fact]
    public void WorkerDisplay_TakesExactlyOnePoolCapture_AndReadsNoGoalsPipelinesOrComposerStats()
    {
        var service = ReadProductionSource("src/CopilotHive/Dashboard/DashboardStateService.cs");
        var method = MethodBody(service, "public WorkerDisplayProjection GetWorkerDisplay()");

        Assert.Equal(1, CountOccurrences(method, "CaptureWorkerStatus()"));
        Assert.Contains("var workers = _workerPool.CaptureWorkerStatus();", method, StringComparison.Ordinal);

        // No goal read, no pipeline read and no Composer statistics read in the worker-only projection.
        Assert.DoesNotContain("_goalStore", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_pipelineManager", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_composer", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetComposerStats", method, StringComparison.Ordinal);

        // The facade delegates to the worker-only capture, not through the whole-dashboard merge or
        // the orchestrator-info projection (which reads the Brain's and Composer's live stats).
        var facade = ReadProductionSource("src/CopilotHive/Services/OperationalFacade.cs");
        var operation = MethodBody(facade, "public async Task<FacadeResult<WorkerDisplayProjection>> GetWorkerDisplayAsync(");

        Assert.Contains("_dashboard.GetWorkerDisplay()", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSnapshot()", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("GetComposerStats", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrchestratorInfo", operation, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4. WORKER PROGRESS — seven approved statuses, cap-before-filter, raw open
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SEVEN APPROVED STATUSES, canonically spelled, with the details dropped: a worker's rows whose
    /// statuses are the approved values in MIXED CASE come back in their CANONICAL spellings with a
    /// <c>null</c> Details, while NEAR MISSES — a different wording, a prefix, a suffix, surrounding
    /// whitespace, an empty string and an internal-space variant — are OMITTED ENTIRELY (not rewritten
    /// and not replaced with a placeholder), and the returned rows keep only the four display columns.
    /// </summary>
    /// <remarks>
    /// THE MUTATIONS THIS KILLS: passing the raw status through untranslated (mixed case would survive);
    /// TRIM-matching (the padded row would be admitted); prefix/substring matching (the prefixed and
    /// suffixed rows would be admitted); substituting a placeholder for an unknown row instead of
    /// omitting it; and leaking the free-text details.
    /// </remarks>
    [Fact]
    public async Task WorkerProgress_AuthenticatedMode_KeepsOnlyTheSevenApprovedStatusesCanonically()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var pipelines = new GoalPipelineManager();
        var pipeline = pipelines.CreatePipeline(new Goal { Id = "goal-progress", Description = "progress goal" });

        var approved = new (string Raw, string Canonical)[]
        {
            ("reading files", "Reading Files"),
            ("BUILDING", "Building"),
            ("Testing", "Testing"),
            ("tests passing", "Tests Passing"),
            ("committing", "Committing"),
            ("Completed", "Completed"),
            ("FAILED", "Failed"),
        };

        var nearMisses = new[]
        {
            "Reading",                    // prefix of an approved value
            "Reading Files and more",     // suffix beyond an approved value
            " Building",                  // leading whitespace
            "Building ",                  // trailing whitespace
            "",                           // empty
            "TestsPassing",               // internal space removed
            "Started",                    // unrelated wording
        };

        // Timestamps ascend so the aggregation's ordering is deterministic.
        var stamp = BaseTime;
        foreach (var (raw, _) in approved)
            pipeline.ProgressReports.Add(Entry(WorkerId, stamp = stamp.AddMinutes(1), raw, $"details for {raw}"));
        foreach (var raw in nearMisses)
            pipeline.ProgressReports.Add(Entry(WorkerId, stamp = stamp.AddMinutes(1), raw, $"details for '{raw}'"));

        // Another worker's rows are never returned.
        pipeline.ProgressReports.Add(Entry("someone-else", stamp = stamp.AddMinutes(1), "Building", "other worker"));

        using var dashboard = NewDashboard(pipelines: pipelines);
        var progressReports = new ProgressReportService(pipelines, new ProgressLog());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, progressReports);

        var result = await facade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);
        var rows = result.Value!;

        // ONLY the approved statuses survive, in the canonical spelling, in the aggregation's order.
        Assert.Equal(approved.Select(entry => entry.Canonical), rows.Select(row => row.Status));

        // The worker's free-text details never leave through an authenticated surface.
        Assert.All(rows, row => Assert.Null(row.Details));

        // The retained columns are the timestamp and the goal id.
        Assert.All(rows, row => Assert.Equal("goal-progress", row.GoalId));
        Assert.Equal(approved.Length, rows.Select(row => row.Timestamp).Distinct().Count());
        Assert.All(rows, row => Assert.NotEqual(default, row.Timestamp));

        // Every near miss really was present in the source the operation read: the SAME aggregation
        // returns them, so the omission above is the projection's filter and not missing data.
        var rawAggregation = progressReports.GetProgressForWorker(WorkerId);
        foreach (var raw in nearMisses)
            Assert.Contains(rawAggregation, entry => entry.Status == raw);

        // The row carries exactly the four display columns.
        Assert.Equal(
            new[] { "Details", "GoalId", "Status", "Timestamp" },
            typeof(WorkerProgressDisplayEntry).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));

        // …and the approved set itself is exactly the seven canonical spellings.
        Assert.Equal(
            ["Reading Files", "Building", "Testing", "Tests Passing", "Committing", "Completed", "Failed"],
            WorkerProgressDisplayEntry.ApprovedStatuses);
    }

    /// <summary>
    /// THE FILTER RUNS AFTER THE EXISTING CAP, WITHOUT BACK-FILL: the aggregation caps at one hundred
    /// rows, so a hundred unapproved reports that are OLDER than one approved report consume the whole
    /// cap and the approved row is dropped — the projection is empty. Adding one MORE row so that the
    /// approved report is inside the cap makes it survive, which proves (a) the cap is the existing one
    /// and (b) nothing is back-filled to replace a filtered row.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: filtering BEFORE the cap (the approved row would then survive in the
    /// first scenario because the cap would be applied to the already-filtered set), or back-filling
    /// after a filtered row (the first scenario would return rows from beyond the cap).
    /// </remarks>
    [Fact]
    public async Task WorkerProgress_AuthenticatedMode_FiltersAfterTheExistingCapWithoutBackfill()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        // ── SCENARIO A: 100 UNAPPROVED rows (oldest first) and ONE approved row NEWER than all of
        //    them. The aggregation caps at the FIRST hundred by timestamp, so the approved row is
        //    beyond the cap: after filtering, the projection is EMPTY — nothing is back-filled. ──
        var beyondCapManager = new GoalPipelineManager();
        var beyondCapPipeline = beyondCapManager.CreatePipeline(
            new Goal { Id = "goal-cap-a", Description = "cap goal a" });
        var stamp = BaseTime;
        for (var index = 0; index < 100; index++)
            beyondCapPipeline.ProgressReports.Add(Entry(WorkerId, stamp = stamp.AddMinutes(1), "Started", $"unapproved {index}"));
        beyondCapPipeline.ProgressReports.Add(
            Entry(WorkerId, stamp = stamp.AddMinutes(1), "Building", "approved beyond the cap"));

        using var beyondCapDashboard = NewDashboard(pipelines: beyondCapManager);
        var beyondCapReports = new ProgressReportService(beyondCapManager, new ProgressLog());
        var beyondCapFacade = FacadeFor(HiveAuthMode.AuthenticationEnabled, beyondCapDashboard, beyondCapReports);

        // THE PREMISE, NOT AN ASSUMPTION: the aggregation really holds 101 rows and the approved row
        // really is the LAST one, i.e. outside the hundred-row cap.
        var aggregation = beyondCapReports.GetProgressForWorker(WorkerId);
        Assert.Equal(100, aggregation.Count);
        Assert.DoesNotContain(aggregation, entry => entry.Status == "Building");

        var capped = await beyondCapFacade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(capped.Success, capped.Error);
        Assert.Empty(capped.Value!);

        // ── SCENARIO B (CONTROL): the same shape but with the approved row INSIDE the cap — it
        //    survives, canonical and without details. So scenario A's emptiness is the existing cap
        //    doing its job, not a filter that drops approved rows or a back-filling substitution. ──
        var withinCapManager = new GoalPipelineManager();
        var withinCapPipeline = withinCapManager.CreatePipeline(
            new Goal { Id = "goal-cap-b", Description = "cap goal b" });
        stamp = BaseTime;
        for (var index = 0; index < 99; index++)
            withinCapPipeline.ProgressReports.Add(Entry(WorkerId, stamp = stamp.AddMinutes(1), "Started", $"unapproved {index}"));
        withinCapPipeline.ProgressReports.Add(Entry(WorkerId, stamp = stamp.AddMinutes(1), "Building", "approved inside the cap"));

        using var withinCapDashboard = NewDashboard(pipelines: withinCapManager);
        var withinCapReports = new ProgressReportService(withinCapManager, new ProgressLog());
        var withinCapFacade = FacadeFor(HiveAuthMode.AuthenticationEnabled, withinCapDashboard, withinCapReports);

        Assert.Equal(100, withinCapReports.GetProgressForWorker(WorkerId).Count);

        var withinCap = await withinCapFacade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(withinCap.Success, withinCap.Error);
        var survivor = Assert.Single(withinCap.Value!);
        Assert.Equal("Building", survivor.Status);
        Assert.Equal("goal-progress", survivor.GoalId);
        Assert.Null(survivor.Details);
    }

    /// <summary>
    /// THE EXPLICIT OPEN MODE KEEPS THE RAW ROWS — the ORIGINAL status text and the ORIGINAL details —
    /// and the returned rows are EXACTLY the existing aggregation's rows in the existing order, so the
    /// facade provably did not re-collect, re-order, re-cap or dedupe on its own. The authenticated
    /// projection of the SAME sources is the positive control that the two modes really differ.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: applying the authenticated status filter (or the details scrub) in open
    /// mode as well — the raw statuses and details below would come back canonicalized/nulled.
    /// </remarks>
    [Fact]
    public async Task WorkerProgress_OpenMode_ReturnsTheRawRowsOfTheExistingAggregationVerbatim()
    {
        var pipelines = new GoalPipelineManager();
        var pipeline = pipelines.CreatePipeline(new Goal { Id = "goal-raw", Description = "raw goal" });

        var progressLog = new ProgressLog();
        var stamp = BaseTime;
        pipeline.ProgressReports.Add(Entry(WorkerId, stamp = stamp.AddMinutes(1), "reading files", "raw pipeline details 1"));
        pipeline.ProgressReports.Add(Entry(WorkerId, stamp = stamp.AddMinutes(1), "Custom Phase", "raw pipeline details 2"));
        pipeline.ProgressReports.Add(Entry(WorkerId, stamp = stamp.AddMinutes(1), " Building", "raw pipeline details 3"));

        using var dashboard = NewDashboard(pipelines: pipelines);
        var progressReports = new ProgressReportService(pipelines, progressLog);

        // The aggregation this operation must reuse verbatim.
        var expected = progressReports.GetProgressForWorker(WorkerId);

        var openFacade = FacadeFor(HiveAuthMode.Open, dashboard, progressReports);
        var raw = await openFacade.GetWorkerProgressAsync(WorkerId, OpenModeActingUser.User, Ct);
        Assert.True(raw.Success, raw.Error);

        Assert.Equal(
            expected.Select(entry => (entry.Timestamp, entry.GoalId, entry.Status, (string?)entry.Details)),
            raw.Value!.Select(row => (row.Timestamp, row.GoalId, row.Status, row.Details)));

        // The raw text really is raw — the unapproved status and the leading whitespace both survive.
        Assert.Contains(raw.Value!, row => row.Status == "Custom Phase");
        Assert.Contains(raw.Value!, row => row.Status == " Building");
        Assert.All(raw.Value!, row => Assert.NotNull(row.Details));

        // CONTROL: the SAME sources in AUTHENTICATED mode are filtered and scrubbed.
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var authenticatedFacade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, progressReports);
        var projected = await authenticatedFacade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(projected.Success, projected.Error);
        Assert.Equal(["Reading Files"], projected.Value!.Select(row => row.Status));
        Assert.Null(projected.Value![0].Details);
    }

    /// <summary>
    /// THE RAW BRANCH IS SELECTED BY THE EXPLICIT MODE, NEVER BY THE CALLER'S IDENTITY: the SAME null
    /// identity that yields raw rows in an explicitly open installation is REFUSED in an
    /// authentication-enabled one — it never selects raw behaviour — and the authenticated refusal
    /// carries no rows at all.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: keying the raw branch off a null (or unauthenticated) identity instead
    /// of the installation mode. That mutation would answer the authenticated null identity with raw
    /// statuses and details, which is an unauthenticated disclosure.
    /// </remarks>
    [Fact]
    public async Task WorkerProgress_RawBranchIsSelectedByMode_NotByANullIdentity()
    {
        var pipelines = new GoalPipelineManager();
        var pipeline = pipelines.CreatePipeline(new Goal { Id = "goal-mode", Description = "mode goal" });
        pipeline.ProgressReports.Add(Entry(WorkerId, BaseTime.AddMinutes(1), "Custom Phase", "raw details"));

        using var dashboard = NewDashboard(pipelines: pipelines);
        var progressReports = new ProgressReportService(pipelines, new ProgressLog());

        // Open mode + the same null identity: raw rows.
        var open = await FacadeFor(HiveAuthMode.Open, dashboard, progressReports)
            .GetWorkerProgressAsync(WorkerId, null, Ct);
        Assert.True(open.Success, open.Error);
        Assert.Equal("Custom Phase", Assert.Single(open.Value!).Status);

        // Authentication enabled + the same null identity: REFUSED, carrying nothing.
        AssertForbidden(
            await FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, progressReports)
                .GetWorkerProgressAsync(WorkerId, null, Ct),
            "authenticated null-identity progress");
    }

    /// <summary>
    /// THE PROGRESS READ TOUCHES NEITHER THE WORKER POOL NOR THE COMPOSER: it delegates to the existing
    /// aggregation and nothing else. A throwing goal store leaves it unaffected, and the facade's
    /// operation body contains no worker capture and no Composer reference.
    /// </summary>
    [Fact]
    public async Task WorkerProgress_DelegatesToTheExistingAggregation_WithoutWorkerCaptureOrComposerStats()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var pipelines = new GoalPipelineManager();
        var pipeline = pipelines.CreatePipeline(new Goal { Id = "goal-delegation", Description = "delegation goal" });
        pipeline.ProgressReports.Add(Entry(WorkerId, BaseTime.AddMinutes(1), "Building", "details"));

        using var dashboard = NewDashboard(pipelines: pipelines, goalStore: new ThrowingGoalStore());
        var progressReports = new ProgressReportService(pipelines, new ProgressLog());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, progressReports);

        var result = await facade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);
        Assert.Equal(["Building"], result.Value!.Select(row => row.Status));

        // The operation body reads the injected aggregation and nothing else.
        var source = ReadProductionSource("src/CopilotHive/Services/OperationalFacade.cs");
        var body = MethodBody(
            source,
            "public async Task<FacadeResult<IReadOnlyList<WorkerProgressDisplayEntry>>> GetWorkerProgressAsync(");

        Assert.Contains("_progressReports.GetProgressForWorker(workerId)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureWorkerStatus", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetComposerStats", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSnapshot", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrchestratorInfo", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PIPELINE/GLOBAL DEDUPLICATION AND MERGE ARE THE AGGREGATION'S OWN: the SAME report present in
    /// both the pipeline collection and the global log is returned ONCE (the pipeline copy wins, because
    /// that is the one carrying the phase/iteration context), a global-only report is still collected,
    /// and the projected rows are exactly the aggregation's rows in its order — so the facade provably
    /// did not re-collect, re-dedupe or re-order on its own.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: projecting the raw global log (or the raw pipeline collection) instead of
    /// the aggregation — the duplicated report would appear twice, and a pipeline-only report would be
    /// lost.
    /// </remarks>
    [Fact]
    public async Task WorkerProgress_ReusesTheAggregationsPipelineAndGlobalDeduplication()
    {
        var progressLog = new ProgressLog();
        var pipelines = new GoalPipelineManager();
        var pipeline = pipelines.CreatePipeline(new Goal { Id = "goal-dedup", Description = "dedup goal" });

        // A GLOBAL report first, then the pipeline's copy of the very same report (same worker, same
        // goal, same timestamp) — the shape the aggregation deduplicates.
        progressLog.Add(WorkerId, pipeline.GoalId, "Building", "global copy of the duplicate");
        var duplicated = progressLog.GetRecent(500).Single(entry => entry.WorkerId == WorkerId);
        Assert.Equal(pipeline.GoalId, duplicated.GoalId);

        pipeline.ProgressReports.Add(new ProgressEntry
        {
            Timestamp = duplicated.Timestamp,
            WorkerId = WorkerId,
            GoalId = pipeline.GoalId,
            Status = "Testing",
            Details = "pipeline copy of the duplicate",
        });

        // A GLOBAL-ONLY, HISTORICAL report, which the aggregation must still collect.
        progressLog.Add(WorkerId, "goal-historical", "Completed", "global-only historical report");
        var historical = progressLog.GetRecent(500)
            .Single(entry => entry.GoalId == "goal-historical" && entry.WorkerId == WorkerId);

        using var dashboard = NewDashboard(pipelines: pipelines);
        var progressReports = new ProgressReportService(pipelines, progressLog);

        // THE PREMISE, NOT AN ASSUMPTION: the aggregation holds TWO rows (three sources, one duplicate).
        var aggregation = progressReports.GetProgressForWorker(WorkerId);
        Assert.Equal(2, aggregation.Count);
        Assert.DoesNotContain(aggregation, entry => entry.Details == "global copy of the duplicate");
        Assert.Contains(aggregation, entry => entry.Status == "Testing");
        Assert.Contains(aggregation, entry => entry.GoalId == "goal-historical");

        // …and the OPEN-MODE projection is exactly that aggregation, in its order.
        var open = await FacadeFor(HiveAuthMode.Open, dashboard, progressReports)
            .GetWorkerProgressAsync(WorkerId, OpenModeActingUser.User, Ct);
        Assert.True(open.Success, open.Error);
        Assert.Equal(
            aggregation.Select(entry => (entry.Timestamp, entry.GoalId, entry.Status, (string?)entry.Details)),
            open.Value!.Select(row => (row.Timestamp, row.GoalId, row.Status, row.Details)));

        // AUTHENTICATED projection: the duplicate is still one row, canonically spelled and scrubbed.
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var projected = await FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, progressReports)
            .GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(projected.Success, projected.Error);
        Assert.Equal(["Testing", "Completed"], projected.Value!.Select(row => row.Status));
        Assert.All(projected.Value!, row => Assert.Null(row.Details));

        // The historical global row really was the one collected, not a fabricated substitute.
        Assert.Equal(
            historical.Timestamp,
            projected.Value!.Single(row => row.GoalId == "goal-historical").Timestamp);
    }

    /// <summary>One synthetic progress report with an explicit timestamp.</summary>
    private static ProgressEntry Entry(string workerId, DateTime timestamp, string status, string details) => new()
    {
        Timestamp = timestamp,
        WorkerId = workerId,
        GoalId = "goal-progress",
        Status = status,
        Details = details,
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  5. THE EXISTING SEVEN OPERATIONS ARE UNCHANGED
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PROGRESS AGGREGATION IS A REQUIRED DEPENDENCY, so the facade cannot be constructed in a
    /// half-wired state where a worker-progress read would silently answer "no reports" instead of
    /// delegating: the host resolves it with <c>GetRequiredService</c>, and a null argument fails fast
    /// with the standard <see cref="ArgumentNullException"/> naming the dependency.
    /// </summary>
    [Fact]
    public void TheProgressAggregation_IsARequiredDependency_NotASilentEmptyFallback()
    {
        using var dashboard = NewDashboard();

        var failure = Assert.Throws<ArgumentNullException>(() => new OperationalFacade(
            dashboard,
            new LlmSessionRegistry(),
            _auth.Resolver(HiveAuthMode.Open),
            _auth.Policy,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OperationalFacade>.Instance,
            progressReports: null!));

        Assert.Equal("progressReports", failure.ParamName);
    }

    /// <summary>
    /// <see cref="IOperationalFacade.GetOrchestratorInfoAsync"/> KEEPS ITS EXISTING BEHAVIOUR: it still
    /// delegates to the unchanged <see cref="DashboardStateService.GetOrchestratorInfo"/> (so the role
    /// models, the display-only sentinel and the reasoning tables are exactly what they were), still
    /// requires SharedRead, and is not replaced by — or routed through — the new worker projection.
    /// </summary>
    [Fact]
    public async Task TheExistingSevenOperations_ArePreserved_IncludingGetOrchestratorInfo()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var config = new HiveConfigFile
        {
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new() { Model = "copilot/coder", ReasoningEffort = "high" },
            },
        };

        using var dashboard = NewDashboard(config: config);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var info = await facade.GetOrchestratorInfoAsync(User(ViewerId), Ct);
        Assert.True(info.Success, info.Error);

        var serviceAnswer = dashboard.GetOrchestratorInfo();
        Assert.Equal(serviceAnswer.Version, info.Value!.Version);
        Assert.Equal(serviceAnswer.RoleModels["coder"], info.Value.RoleModels["coder"]);
        Assert.Equal(serviceAnswer.RoleModels["tester"], info.Value.RoleModels["tester"]);
        Assert.Equal(serviceAnswer.RoleReasoningEfforts["coder"], info.Value.RoleReasoningEfforts["coder"]);

        // …and still refused for a signed-out caller, carrying no value.
        AssertForbidden(await facade.GetOrchestratorInfoAsync(null, Ct), "signed-out orchestrator info");
    }

    // ── Source-text helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the source of the block that opens at the first <c>{</c> at or after
    /// <paramref name="signature"/>, delimited by BALANCED brace counting, so a scope claim is made
    /// against the matched body rather than against text order.
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found; the production shape changed.");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"'{signature}' has no block body; the production shape changed.");

        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[(open + 1)..index];
            }
        }

        Assert.Fail($"'{signature}' is never closed; the production shape changed.");
        return null!;
    }

    /// <summary>Counts the non-overlapping occurrences of a literal in the source text.</summary>
    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// Loads a production file by its repository-relative path, walking up from the test assembly to the
    /// repository root. A MISSING FILE IS A LOUD FAILURE, never a silently skipped assertion.
    /// </summary>
    private static string ReadProductionSource(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        Assert.Fail(
            $"'{relative}' was not found walking up from '{AppContext.BaseDirectory}'; the structural "
            + "vector cannot be evaluated.");
        return null!;
    }

    /// <summary>
    /// Every type reachable from <paramref name="type"/> through properties and their generic
    /// arguments, so a live entity hidden behind another DTO is still found. Each type is visited ONCE
    /// (tracked in <paramref name="visited"/>), which both terminates the walk on self-referencing
    /// shapes such as <see cref="DateTime.Date"/> and keeps the result finite.
    /// </summary>
    private static IEnumerable<Type> Flatten(Type type, HashSet<Type>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(type))
            yield break;

        yield return type;

        foreach (var argument in type.GetGenericArguments())
            foreach (var nested in Flatten(argument, visited))
                yield return nested;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            foreach (var nested in Flatten(property.PropertyType, visited))
                yield return nested;
    }

    public void Dispose() => _auth.Dispose();
}
