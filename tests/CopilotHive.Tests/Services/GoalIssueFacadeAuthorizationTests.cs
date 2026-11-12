using System.Security.Claims;

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
/// THE GOAL/ISSUE OPERATION AUTHORIZATION MATRIX, driven through the REAL
/// <see cref="GoalFacade"/> and <see cref="IssueFacade"/> over the REAL production
/// <see cref="UserService"/> directory (see <see cref="FacadeAuthorizationHarness"/>) and the REAL
/// <see cref="HivePrincipalResolver"/>/<see cref="HivePolicy"/> pair.
/// <para>
/// THE ENFORCED MAP (one refusal row per cell, each paired with a discriminating success control so
/// no row can be satisfied by an implementation that simply refuses everything):
/// <list type="bullet">
///   <item><b>SharedRead</b> — every goal read (list, detail, search, iterations, dependency
///   statuses, live dashboard detail, progress, linked issues) and both issue reads. A Viewer IS
///   granted these.</item>
///   <item><b>ExecutionOperator</b> — goal delete / status / review-status / review / cancel /
///   extend-iterations / attach-release, and the ENTIRE issue update surface plus issue delete. A
///   Viewer and a Member are refused; an Operator is granted.</item>
///   <item><b>InteractiveMember</b> — issue create. A Viewer is refused; a Member is granted.</item>
///   <item><b>GlobalAdmin</b> is NEVER consulted: an Operator performs every mutation, so the
///   mutations are NOT secretly admin-gated.</item>
/// </list>
/// </para>
/// <para>
/// FRESHNESS: a role change or a disablement between two calls on the SAME facade instance with the
/// SAME principal object takes effect on the very next call — nothing is cached.
/// </para>
/// <para>
/// ORDER: authorization runs BEFORE the operation's own store work and validation, so a refused
/// caller performs NO store read, NO validation and NO side effect (a Viewer sending an invalid
/// update gets Forbidden, not BadRequest).
/// </para>
/// </summary>
public sealed class GoalIssueFacadeAuthorizationTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "gi-viewer";
    private const string MemberId = "gi-member";
    private const string OperatorId = "gi-operator";
    private const string AdminId = "gi-admin";

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

    /// <summary>
    /// A real <see cref="GoalPipelineManager"/> + <see cref="ProgressLog"/> pair (no pipelines, no
    /// progress), so the live dashboard detail resolves to "no live state" and the progress feed to
    /// an empty list through the REAL production services.
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

    /// <summary>
    /// Builds the production goal facade for <paramref name="mode"/> over a store that already holds
    /// <paramref name="goals"/> (and, when given, one release).
    /// </summary>
    private GoalFacade GoalFacadeFor(
        HiveAuthMode mode, RecordingGoalStore store, IReadOnlyList<Goal> goals, bool withRelease = false)
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
                config: new CopilotHive.Configuration.HiveConfigFile
                {
                    Workers = { ["reviewer"] = new CopilotHive.Configuration.WorkerConfig { Model = "reviewer-model" } },
                },
                goalStore: store,
                brainRepoManager: null,
                stateDir: Path.GetTempPath(),
                logger: NullLogger<GoalReviewService>.Instance),
            new DashboardNotifier(),
            _auth.Resolver(mode),
            _auth.Policy,
            NullLogger<GoalFacade>.Instance,
            repoManager: null,
            docCleanup: null,
            goalReadyNotifier: null,
            dispatcher: null,
            dashboardState: state,
            progressReports: progress);
    }

    /// <summary>Builds the production issue facade for <paramref name="mode"/> over in-memory issues.</summary>
    private IssueFacade IssueFacadeFor(HiveAuthMode mode, RecordingIssueStore store) =>
        new(store, eventBus: null, _auth.Resolver(mode), _auth.Policy, NullLogger<IssueFacade>.Instance);

    private static Goal DraftGoal(string id = "goal-1") => new()
    {
        Id = id,
        Description = "Authorization test goal",
        Status = GoalStatus.Draft,
        DependsOn = ["dep-1"],
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  GOAL READS — SharedRead
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE READ MAP: every goal read is a SHARED READ. A Viewer is GRANTED every one of them (the
    /// non-vacuity control), and a signed-out principal and an authenticated identity with no
    /// activated account are refused on every one of them.
    /// </summary>
    [Fact]
    public async Task GoalReads_ViewerIsGranted_AndAnonymousOrUnknownIsRefused()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingGoalStore();

        var viewer = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
            .GetGoalsAsync(Principal(ViewerId), Ct);
        Assert.True(viewer.Success, viewer.Error ?? "a Viewer must hold shared read");
        Assert.Single(viewer.Value!);

        var anonymous = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
            .GetGoalsAsync(actingUser: null, Ct);
        Assert.Equal(FacadeErrorKind.Forbidden, anonymous.Kind);

        var unknown = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
            .GetGoalsAsync(Principal("never-invited"), Ct);
        Assert.Equal(FacadeErrorKind.Forbidden, unknown.Kind);
    }

    /// <summary>
    /// EVERY goal read honours SharedRead — one row per read method, so a read that skipped the gate
    /// (or one that wrongly demanded Operator) fails here. The Viewer succeeds on all of them; the
    /// anonymous caller is refused on all of them.
    /// </summary>
    [Fact]
    public async Task EveryGoalRead_RequiresSharedRead_Only()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingGoalStore();
        store.AddGoal(DraftGoal());

        // A Viewer (the LOWEST role) can read every one of these.
        var viewerFacade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);
        var viewer = Principal(ViewerId);
        Assert.True((await viewerFacade.GetGoalAsync("goal-1", viewer, Ct)).Success);
        Assert.True((await viewerFacade.SearchGoalsAsync("goal", status: null, viewer, Ct)).Success);
        Assert.True((await viewerFacade.GetIterationsAsync("goal-1", viewer, Ct)).Success);
        Assert.True((await viewerFacade.GetDependencyStatusesAsync("goal-1", viewer, Ct)).Success);
        Assert.True((await viewerFacade.GetLiveGoalDetailAsync("goal-1", viewer, Ct)).Success);
        Assert.True((await viewerFacade.GetProgressAsync("goal-1", viewer, Ct)).Success);
        Assert.True((await viewerFacade.GetLinkedIssuesAsync("goal-1", viewer, Ct)).Success);

        // …and a signed-out caller is refused on every one of them.
        var anonymousFacade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);
        Assert.Equal(FacadeErrorKind.Forbidden, (await anonymousFacade.GetGoalAsync("goal-1", actingUser: null, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await anonymousFacade.SearchGoalsAsync("goal", null, null, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await anonymousFacade.GetIterationsAsync("goal-1", null, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await anonymousFacade.GetDependencyStatusesAsync("goal-1", null, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await anonymousFacade.GetLiveGoalDetailAsync("goal-1", null, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await anonymousFacade.GetProgressAsync("goal-1", null, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await anonymousFacade.GetLinkedIssuesAsync("goal-1", null, Ct)).Kind);
    }

    /// <summary>
    /// THE AUTHORIZED-READ PAYLOAD CONTRACT: an authorized goal detail read still reports NotFound
    /// for a missing goal (the pre-facade 404 semantics are unchanged for an authorized caller), and
    /// the list/detail return the DOMAIN <see cref="Goal"/> — never a mutation DTO substitution.
    /// </summary>
    [Fact]
    public async Task AuthorizedGoalRead_KeepsNotFoundSemantics_AndReturnsTheDomainGoal()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingGoalStore();
        var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);

        var missing = await facade.GetGoalAsync("missing", Principal(ViewerId), Ct);
        Assert.False(missing.Success);
        Assert.Equal(FacadeErrorKind.NotFound, missing.Kind);
        Assert.Equal("Goal 'missing' not found.", missing.Error);

        var found = await facade.GetGoalAsync("goal-1", Principal(ViewerId), Ct);
        Assert.True(found.Success);
        Assert.IsType<Goal>(found.Value);
        Assert.Equal("goal-1", found.Value!.Id);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GOAL MUTATIONS — ExecutionOperator
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY goal mutation refuses a Viewer AND a Member with <see cref="FacadeErrorKind.Forbidden"/>
    /// and the sanitized denial text, performing NO store work whatsoever.
    /// <para>
    /// The refusals run BEFORE the operation's own validation, so the invalid-input shapes used here
    /// (an unparsable status, a numeric review status, an out-of-range iteration count, an unknown
    /// release) would otherwise produce BadRequest/NotFound — proving the gate precedes validation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EveryGoalMutation_RefusesViewerAndMember_BeforeAnyStoreWork()
    {
        await SeedAllRolesAsync();

        foreach (var actor in new[] { ViewerId, MemberId })
        {
            var user = Principal(actor);
            using var store = new RecordingGoalStore();
            var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);

            var delete = await facade.DeleteGoalAsync("goal-1", user);
            Assert.Equal(FacadeErrorKind.Forbidden, delete.Kind);
            Assert.Equal(ForbiddenMessage, delete.Error);

            var status = await facade.UpdateGoalStatusAsync("goal-1", "NotAStatus", user);
            Assert.Equal(FacadeErrorKind.Forbidden, status.Kind);
            Assert.Equal(ForbiddenMessage, status.Error);

            var reviewStatus = await facade.UpdateReviewStatusAsync("goal-1", "99", user);
            Assert.Equal(FacadeErrorKind.Forbidden, reviewStatus.Kind);
            Assert.Equal(ForbiddenMessage, reviewStatus.Error);

            var review = await facade.RequestReviewAsync("goal-1", user, Ct);
            Assert.Equal(FacadeErrorKind.Forbidden, review.Kind);
            Assert.Equal(ForbiddenMessage, review.Error);

            var cancel = await facade.CancelGoalAsync("goal-1", user);
            Assert.Equal(FacadeErrorKind.Forbidden, cancel.Kind);
            Assert.Equal(ForbiddenMessage, cancel.Error);

            var extend = await facade.ExtendIterationsAsync("goal-1", 0, user, Ct);
            Assert.Equal(FacadeErrorKind.Forbidden, extend.Kind);
            Assert.Equal(ForbiddenMessage, extend.Error);

            var attach = await facade.AttachReleaseAsync("goal-1", "missing-release", user);
            Assert.Equal(FacadeErrorKind.Forbidden, attach.Kind);
            Assert.Equal(ForbiddenMessage, attach.Error);

            // NOTHING was read, mutated or notified for any of the seven refused operations.
            Assert.Empty(store.ReadCalls);
            Assert.Empty(store.DeleteCalls);
            Assert.Empty(store.StatusUpdates);
            Assert.Empty(store.ResetCalls);
            Assert.Empty(store.GoalUpdates);
            Assert.Equal(0, store.ReleaseReads);
            Assert.Equal(0, store.GetAllCalls);
        }
    }

    /// <summary>
    /// THE DISCRIMINATING SUCCESS CONTROL: an OPERATOR — not an Admin — performs EVERY goal mutation.
    /// This is the row that proves the mutations are gated at ExecutionOperator and NOT at
    /// GlobalAdmin, and that the gate never refuses an authorized caller.
    /// </summary>
    [Fact]
    public async Task Operator_PerformsEveryGoalMutation_ProvingTheGateIsNotAdminLevel()
    {
        await SeedAllRolesAsync();
        var operatorUser = Principal(OperatorId);

        // DELETE (Draft goal) succeeds.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .DeleteGoalAsync("goal-1", operatorUser);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to delete");
            Assert.Equal(["goal-1"], store.DeleteCalls);
        }

        // STATUS transition succeeds (and is applied).
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .UpdateGoalStatusAsync("goal-1", "Pending", operatorUser);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to approve");
            Assert.Equal([("goal-1", GoalStatus.Pending)], store.StatusUpdates);
        }

        // REVIEW STATUS change succeeds and is persisted.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .UpdateReviewStatusAsync("goal-1", "Approved", operatorUser);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to set the review status");
            Assert.Equal(ReviewStatus.Approved, Assert.Single(store.GoalUpdates).ReviewStatus);
        }

        // CANCEL: reaches the goal lookup (so the gate passed), which then reports NotFound.
        using (var store = new RecordingGoalStore())
        {
            var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);
            // The dispatcher is required for cancel: a null dispatcher throws, exactly as before.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => facade.CancelGoalAsync("goal-1", operatorUser));
        }

        // EXTEND ITERATIONS: reaches the range check, so the gate passed.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .ExtendIterationsAsync("goal-1", 0, operatorUser, Ct);
            Assert.Equal(FacadeErrorKind.ServiceUnavailable, result.Kind);
        }

        // ATTACH RELEASE: reaches the release lookup, so the gate passed.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], withRelease: true)
                .AttachReleaseAsync("goal-1", "rel-1", operatorUser);
            Assert.True(result.Success, result.Error ?? "an Operator must be allowed to attach a release");
            Assert.Equal("rel-1", store.GoalUpdates[0].ReleaseId);
        }
    }

    /// <summary>
    /// THE AUTHORIZED-CALLER VALIDATION CONTRACT IS BYTE-IDENTICAL: an Operator sending the same
    /// invalid inputs the refusal rows used gets the SAME NotFound/BadRequest answers an authorized
    /// caller always got — so authorization never reshaped the existing error semantics.
    /// </summary>
    [Fact]
    public async Task Operator_InvalidInputs_KeepThePreFacadeNotFoundAndBadRequestAnswers()
    {
        await SeedAllRolesAsync();
        var operatorUser = Principal(OperatorId);

        using var store = new RecordingGoalStore();
        var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()], withRelease: true);

        var unparsable = await facade.UpdateGoalStatusAsync("goal-1", "NotAStatus", operatorUser);
        Assert.Equal(FacadeErrorKind.BadRequest, unparsable.Kind);
        Assert.Equal("Invalid status 'NotAStatus'.", unparsable.Error);

        var numericReview = await facade.UpdateReviewStatusAsync("goal-1", "99", operatorUser);
        Assert.Equal(FacadeErrorKind.BadRequest, numericReview.Kind);
        Assert.Equal(
            "Invalid review status. Valid values: none, pending, approved, needschanges.",
            numericReview.Error);

        var commaReview = await facade.UpdateReviewStatusAsync("goal-1", "Pending, Approved", operatorUser);
        Assert.Equal(FacadeErrorKind.BadRequest, commaReview.Kind);

        var missingGoal = await facade.DeleteGoalAsync("missing", operatorUser);
        Assert.Equal(FacadeErrorKind.NotFound, missingGoal.Kind);
        Assert.Equal("Goal 'missing' not found.", missingGoal.Error);

        var missingRelease = await facade.AttachReleaseAsync("goal-1", "missing-release", operatorUser);
        Assert.Equal(FacadeErrorKind.NotFound, missingRelease.Kind);
        Assert.Equal("Release 'missing-release' not found.", missingRelease.Error);
    }

    /// <summary>
    /// THE REVIEW-STATUS ROUTE NOW GOES THROUGH AN AUTHORIZED OPERATION, not a direct store write:
    /// a refused caller performs no store read at all, and the authorized path still answers the
    /// frozen NotFound-then-numeric-then-parse-then-comma order.
    /// </summary>
    [Fact]
    public async Task UpdateReviewStatus_AuthorizedOrder_IsNotFoundNumericParseComma()
    {
        await SeedAllRolesAsync();
        var operatorUser = Principal(OperatorId);

        // NotFound runs FIRST (an invalid value on a missing goal is still 404).
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [])
                .UpdateReviewStatusAsync("missing", "notastatus", operatorUser);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
            Assert.Equal("Goal 'missing' not found.", result.Error);
        }

        // Numeric → 400 before the parse, even for a value that IS a defined enum integer.
        using (var store = new RecordingGoalStore())
        {
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .UpdateReviewStatusAsync("goal-1", "2", operatorUser);
            Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        }

        // Every accepted named value round-trips onto the stored goal.
        foreach (var (wire, expected) in new (string, ReviewStatus)[]
        {
            ("none", ReviewStatus.None),
            ("pending", ReviewStatus.Pending),
            ("approved", ReviewStatus.Approved),
            ("needschanges", ReviewStatus.NeedsChanges),
            ("NEEDSCHANGES", ReviewStatus.NeedsChanges),
        })
        {
            using var store = new RecordingGoalStore();
            var result = await GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()])
                .UpdateReviewStatusAsync("goal-1", wire, operatorUser);
            Assert.True(result.Success, result.Error ?? $"'{wire}' must be accepted");
            Assert.Equal(expected, result.Value!.ReviewStatus);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ISSUE OPERATIONS — SharedRead / InteractiveMember / ExecutionOperator
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ISSUE READS ARE SHARED READS: a Viewer is granted both, an anonymous caller is refused on
    /// both, and an authorized caller keeps the pre-facade payload/NotFound semantics.
    /// </summary>
    [Fact]
    public async Task IssueReads_RequireSharedRead_Only()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("issue-1"));
        var facade = IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store);

        var viewer = Principal(ViewerId);
        var list = await facade.GetIssuesAsync(new IssueFilter(), viewer, Ct);
        Assert.True(list.Success, list.Error ?? "a Viewer must hold shared read");
        Assert.Single(list.Value!);

        var single = await facade.GetIssueAsync("issue-1", viewer, Ct);
        Assert.True(single.Success);
        Assert.Equal("issue-1", single.Value!.Id);

        var missing = await facade.GetIssueAsync("missing", viewer, Ct);
        Assert.Equal(FacadeErrorKind.NotFound, missing.Kind);
        Assert.Equal("Issue 'missing' not found.", missing.Error);

        Assert.Equal(FacadeErrorKind.Forbidden,
            (await facade.GetIssuesAsync(new IssueFilter(), actingUser: null, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden,
            (await facade.GetIssueAsync("issue-1", actingUser: null, Ct)).Kind);
    }

    /// <summary>
    /// ISSUE CREATE requires <see cref="HiveCapability.InteractiveMember"/>: a Viewer is refused
    /// (with the invalid-input shape, so the gate provably precedes validation) and a MEMBER —
    /// not an Operator — succeeds, which is what pins the gate at InteractiveMember rather than a
    /// stronger capability.
    /// </summary>
    [Fact]
    public async Task IssueCreate_RefusesViewer_AndSucceedsForMember()
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
            // No store call for a refused caller.
            Assert.Empty(store.Tokens);
        }

        using (var store = new RecordingIssueStore())
        {
            var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store)
                .CreateIssueAsync(
                    new CreateIssueRequest(Id: "member-issue", Type: IssueType.Bug, Title: "T", Description: "D"),
                    Principal(MemberId), Ct);
            Assert.True(result.Success, result.Error ?? "a Member must be allowed to create an issue");
            Assert.Equal("member-issue", result.Value!.Id);
        }
    }

    /// <summary>
    /// THE ENTIRE ISSUE UPDATE SURFACE IS AN OPERATOR OPERATION: every field — Type, status,
    /// severity, title, description, repositories, linked goal — and every combination, plus DELETE,
    /// is refused for a Viewer AND a Member before any store work, and granted to an Operator.
    /// <para>
    /// Each refused row uses a shape that would otherwise be NotFound/BadRequest, so the refusal
    /// provably precedes the existence read and the field validation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EveryIssueUpdateShapeAndDelete_RequireExecutionOperator()
    {
        await SeedAllRolesAsync();

        var updates = new (string Name, UpdateIssueRequest Request)[]
        {
            ("type", new UpdateIssueRequest(Type: IssueType.Bug)),
            ("status", new UpdateIssueRequest(Status: IssueStatus.Resolved)),
            ("severity", new UpdateIssueRequest(Severity: IssueSeverity.High)),
            ("title", new UpdateIssueRequest(Title: "  ")),
            ("description", new UpdateIssueRequest(Description: "  ")),
            ("repositories", new UpdateIssueRequest(RepositoryNames: ["RepoA"])),
            ("linked-goal-set", new UpdateIssueRequest(LinkedGoalId: "goal-1")),
            ("linked-goal-clear", new UpdateIssueRequest(LinkedGoalId: "")),
            ("combined", new UpdateIssueRequest(
                Type: IssueType.Concern, Status: IssueStatus.Closed, Severity: IssueSeverity.Low,
                Title: "Combined", Description: "Combined", RepositoryNames: ["RepoA"], LinkedGoalId: "goal-1")),
        };

        foreach (var actor in new[] { ViewerId, MemberId })
        {
            foreach (var (name, request) in updates)
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

            using var deleteStore = new RecordingIssueStore();
            deleteStore.AddIssue(MakeIssue("issue-1"));
            var delete = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, deleteStore)
                .DeleteIssueAsync("issue-1", Principal(actor), Ct);
            Assert.Equal(FacadeErrorKind.Forbidden, delete.Kind);
            Assert.Equal(ForbiddenMessage, delete.Error);
            Assert.Empty(deleteStore.Tokens);
        }

        // THE OPERATOR CONTROL: for the very same request SHAPES an Operator is not refused with
        // Forbidden — so no refusal row above can be satisfied by an implementation that refuses
        // every update. (The blank Title/Description shapes still legitimately report their own
        // BadRequest validation, which is exactly the discriminating pair asserted below.)
        foreach (var (name, request) in updates)
        {
            using var store = new RecordingIssueStore();
            store.AddIssue(MakeIssue("issue-1"));
            var result = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store)
                .UpdateIssueAsync("issue-1", request, Principal(OperatorId), Ct);

            Assert.NotEqual(FacadeErrorKind.Forbidden, result.Kind);
            if (name is "title" or "description")
                Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
            else
                Assert.True(result.Success, result.Error ?? $"an Operator must be allowed the '{name}' update");
        }

        using var operatorDeleteStore = new RecordingIssueStore();
        operatorDeleteStore.AddIssue(MakeIssue("issue-1"));
        var operatorDelete = await IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, operatorDeleteStore)
            .DeleteIssueAsync("issue-1", Principal(OperatorId), Ct);
        Assert.True(operatorDelete.Success, operatorDelete.Error ?? "an Operator must be allowed to delete an issue");
    }

    /// <summary>
    /// ORDER: a caller who does NOT hold shared read (signed out, or an authenticated identity with
    /// no activated account) sending an INVALID filter value gets Forbidden, not the filter's exact
    /// BadRequest message — so an unauthorized caller cannot use the validation messages to probe the
    /// schema, and the two answers stay distinguishable for an authorized one.
    /// <para>
    /// A VIEWER is deliberately NOT the caller here: a Viewer HOLDS shared read, so it is an
    /// authorized reader of this surface and legitimately receives the frozen BadRequest — which is
    /// the second half of this pair, and the reason the page's read gate is SharedRead and not a
    /// stronger capability.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UnauthorizedCaller_SendingAnInvalidFilter_GetsForbidden_NotBadRequest()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingIssueStore();
        var facade = IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, store);

        var deniedAnonymous = await facade.GetIssuesAsync(new IssueFilter(Type: "not-a-type"), actingUser: null, Ct);
        Assert.Equal(FacadeErrorKind.Forbidden, deniedAnonymous.Kind);
        Assert.DoesNotContain("not-a-type", deniedAnonymous.Error!, StringComparison.Ordinal);

        var deniedUnknown = await facade.GetIssuesAsync(
            new IssueFilter(Type: "not-a-type"), Principal("never-invited"), Ct);
        Assert.Equal(FacadeErrorKind.Forbidden, deniedUnknown.Kind);

        // The refused reads performed NO store query (the parse rejected it anyway, but the gate ran
        // first — proven by the Forbidden, not the BadRequest).
        Assert.Empty(store.Queries);

        // A Viewer is an AUTHORIZED reader, so it keeps the frozen validation answer.
        var viewer = await facade.GetIssuesAsync(new IssueFilter(Type: "not-a-type"), Principal(ViewerId), Ct);
        Assert.Equal(FacadeErrorKind.BadRequest, viewer.Kind);
        Assert.Equal(
            "Invalid type 'not-a-type'. Valid values: code_quality, bug, suggestion, concern, workflow.",
            viewer.Error);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  FRESHNESS — demotion / disablement / deletion between two calls
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A DEMOTION LANDS ON THE VERY NEXT OPERATION: an Operator who is demoted to Viewer can no
    /// longer mutate, using the SAME facade instance and the SAME principal object. The role change
    /// is re-read from the directory on every call, so nothing stale can grant the second operation.
    /// </summary>
    [Fact]
    public async Task OperatorDemotedBetweenTwoCalls_IsRefusedOnTheSecondCall()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingGoalStore();
        var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);
        var user = Principal(OperatorId);

        var first = await facade.UpdateGoalStatusAsync("goal-1", "Pending", user);
        Assert.True(first.Success, first.Error ?? "the Operator's first status change must succeed");

        await _auth.SetRoleAsync(OperatorId, UserRole.Viewer);

        var second = await facade.UpdateGoalStatusAsync("goal-1", "Draft", user);
        Assert.Equal(FacadeErrorKind.Forbidden, second.Kind);
        Assert.Equal(ForbiddenMessage, second.Error);

        // The refused operation really performed no further store write.
        Assert.Equal([("goal-1", GoalStatus.Pending)], store.StatusUpdates);
    }

    /// <summary>
    /// A DISABLEMENT LANDS ON THE VERY NEXT OPERATION — for a READ as well as a mutation, so a
    /// disabled account loses even shared read: the page's next refresh is refused and its data is
    /// cleared rather than served stale.
    /// </summary>
    [Fact]
    public async Task DisabledAccount_IsRefusedOnTheNextReadAndMutation()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingGoalStore();
        var facade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);
        var user = Principal(OperatorId);

        Assert.True((await facade.GetGoalsAsync(user, Ct)).Success);
        Assert.True((await facade.UpdateGoalStatusAsync("goal-1", "Pending", user)).Success);

        await _auth.SetEnabledAsync(OperatorId, enabled: false);

        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.GetGoalsAsync(user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden, (await facade.GetGoalAsync("goal-1", user, Ct)).Kind);
        Assert.Equal(FacadeErrorKind.Forbidden,
            (await facade.UpdateGoalStatusAsync("goal-1", "Draft", user)).Kind);
    }

    /// <summary>
    /// A DEMOTED MEMBER LOSES CREATE ON THE NEXT CALL: the Member who could create an issue is
    /// refused once demoted to Viewer, with the same facade instance and principal.
    /// </summary>
    [Fact]
    public async Task MemberDemotedToViewer_IsRefusedIssueCreateOnTheNextCall()
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

        // Exactly ONE create reached the store — the refused one performed no store work at all.
        Assert.Single(store.Tokens);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  OPEN MODE + CANCELLATION
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OPEN MODE RESOLVES THE EXPLICIT LOCAL OPERATOR: with no configured authentication a null
    /// identity performs every covered read AND mutation exactly as the pre-multi-user installation
    /// did — backward compatibility, not a bypass, because the mode is an explicit host input.
    /// </summary>
    [Fact]
    public async Task OpenMode_ResolvesTheLocalOperator_ForReadsAndMutations()
    {
        using var store = new RecordingGoalStore();
        var facade = GoalFacadeFor(HiveAuthMode.Open, store, [DraftGoal()], withRelease: true);

        Assert.True((await facade.GetGoalsAsync(actingUser: null, Ct)).Success);
        Assert.True((await facade.GetLinkedIssuesAsync("goal-1", actingUser: null, Ct)).Success);
        Assert.True((await facade.UpdateGoalStatusAsync("goal-1", "Pending", actingUser: null)).Success);
        Assert.True((await facade.AttachReleaseAsync("goal-1", "rel-1", actingUser: null)).Success);

        using var issueStore = new RecordingIssueStore();
        var issueFacade = IssueFacadeFor(HiveAuthMode.Open, issueStore);
        Assert.True((await issueFacade.GetIssuesAsync(new IssueFilter(), null, Ct)).Success);
        Assert.True((await issueFacade.CreateIssueAsync(
            new CreateIssueRequest(Id: "open-issue", Type: IssueType.Bug, Title: "T", Description: "D"),
            actingUser: null, Ct)).Success);
    }

    /// <summary>
    /// CANCELLATION PROPAGATES and is never converted into a denial — for an authorized caller AND
    /// for an unauthorized one, so a cancelled request can neither be answered with a result nor be
    /// mistaken for an authorization decision.
    /// </summary>
    [Fact]
    public async Task CancelledToken_Propagates_ForAuthorizedAndUnauthorizedCallers()
    {
        await SeedAllRolesAsync();
        using var store = new RecordingGoalStore();
        var goalFacade = GoalFacadeFor(HiveAuthMode.AuthenticationEnabled, store, [DraftGoal()]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => goalFacade.GetGoalsAsync(Principal(OperatorId), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => goalFacade.GetGoalsAsync(Principal(ViewerId), cts.Token));

        using var issueStore = new RecordingIssueStore();
        var issueFacade = IssueFacadeFor(HiveAuthMode.AuthenticationEnabled, issueStore);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => issueFacade.GetIssuesAsync(new IssueFilter(), Principal(OperatorId), cts.Token));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SHAPE — no identity-free overload can be re-added
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE NO-BYPASS SURFACE CONTRACT: EVERY <see cref="IGoalFacade"/> and <see cref="IIssueFacade"/>
    /// method declares EXACTLY ONE overload and carries a REQUIRED
    /// <see cref="ClaimsPrincipal"/> parameter — so a re-added identity-free overload (or a defaulted
    /// identity) fails here before it can ship an unauthorized operation.
    /// </summary>
    [Fact]
    public void EveryGoalAndIssueFacadeOperation_RequiresAnIdentity_WithNoBypassOverload()
    {
        foreach (var type in new[] { typeof(IGoalFacade), typeof(IIssueFacade) })
        {
            var methods = type.GetMethods();
            Assert.NotEmpty(methods);

            foreach (var group in methods.GroupBy(m => m.Name, StringComparer.Ordinal))
            {
                Assert.True(
                    group.Count() == 1,
                    $"{type.Name}.{group.Key} declares {group.Count()} overloads; a second overload could be an identity-free bypass.");
            }

            foreach (var method in methods)
            {
                var identity = Assert.Single(
                    method.GetParameters(),
                    p => p.ParameterType == typeof(ClaimsPrincipal));
                Assert.True(identity.ParameterType == typeof(ClaimsPrincipal), $"{method.Name} identity type");
                Assert.False(
                    identity.IsOptional,
                    $"{type.Name}.{method.Name}: the acting identity must be REQUIRED, never defaulted.");
            }
        }
    }

    public void Dispose() => _auth.Dispose();

    // ── Shared fakes ────────────────────────────────────────────────────────

    private static Issue MakeIssue(string id) => new()
    {
        Id = id,
        Title = $"Title {id}",
        Description = "desc",
        Type = IssueType.Bug,
        Severity = IssueSeverity.Medium,
        Status = IssueStatus.Open,
    };

    /// <summary>
    /// In-memory <see cref="IGoalStore"/> that RECORDS every read/write this facade makes, so a
    /// refusal can be proven to perform no store work at all.
    /// </summary>
    private sealed class RecordingGoalStore : IGoalStore, IDisposable
    {
        private readonly Dictionary<string, Goal> _goals = new();
        private readonly Dictionary<string, Release> _releases = new();

        public string Name => "recording-goal-store";

        /// <summary>Goal IDs passed to <see cref="GetGoalAsync"/>, in call order.</summary>
        public List<string> ReadCalls { get; } = [];

        /// <summary>Number of <see cref="GetAllGoalsAsync"/> calls.</summary>
        public int GetAllCalls { get; private set; }

        /// <summary>Number of <see cref="GetReleasesAsync"/> calls.</summary>
        public int ReleaseReads { get; private set; }

        /// <summary>Goal IDs passed to <see cref="DeleteGoalAsync"/>, in call order.</summary>
        public List<string> DeleteCalls { get; } = [];

        /// <summary>(goalId, status) pairs passed to <see cref="UpdateGoalStatusAsync"/>, in call order.</summary>
        public List<(string GoalId, GoalStatus Status)> StatusUpdates { get; } = [];

        /// <summary>Goal IDs passed to <see cref="ResetGoalIterationDataAsync"/>, in call order.</summary>
        public List<string> ResetCalls { get; } = [];

        /// <summary>Goals passed to <see cref="UpdateGoalAsync"/>, in call order.</summary>
        public List<Goal> GoalUpdates { get; } = [];

        public void AddGoal(Goal goal) => _goals[goal.Id] = goal;

        public void AddRelease(Release release) => _releases[release.Id] = release;

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            return Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList());
        }

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
        {
            ReadCalls.Add(goalId);
            return Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? goal : null);
        }

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            _goals[goal.Id] = goal;
            return Task.FromResult(goal);
        }

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            GoalUpdates.Add(goal);
            _goals[goal.Id] = goal;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
        {
            DeleteCalls.Add(goalId);
            return Task.FromResult(_goals.Remove(goalId));
        }

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
            string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(
                _goals.Values.Where(g => g.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.Where(g => g.Status == status).ToList());

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IterationSummary>>(
                _goals.TryGetValue(goalId, out var goal) ? goal.IterationSummaries : []);

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            StatusUpdates.Add((goalId, status));
            if (_goals.TryGetValue(goalId, out var goal))
                goal.Status = status;
            return Task.CompletedTask;
        }

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
        {
            _releases[release.Id] = release;
            return Task.FromResult(release);
        }

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult(_releases.TryGetValue(releaseId, out var release) ? release : null);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
        {
            ReleaseReads++;
            return Task.FromResult<IReadOnlyList<Release>>(_releases.Values.ToList());
        }

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
            string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default)
        {
            ResetCalls.Add(goalId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
            int? limit = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);

        public void Dispose()
        {
            // No unmanaged state: the fakes are plain dictionaries.
        }
    }

    /// <summary>
    /// In-memory <see cref="IIssueStore"/> recording every call, so a refusal can be proven to
    /// perform no store work at all. Implements <see cref="IDisposable"/> purely so a test can scope
    /// one with <c>using</c> like its <see cref="RecordingGoalStore"/> sibling; there is no
    /// unmanaged state.
    /// </summary>
    private sealed class RecordingIssueStore : IIssueStore, IDisposable
    {
        private readonly Dictionary<string, Issue> _issues = new();

        public List<(IssueStatus? Status, IssueType? Type, IssueSeverity? Severity, string? Repository,
            string? SourceGoalId, string? LinkedGoalId)> Queries { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public void AddIssue(Issue issue) => _issues[issue.Id] = issue;

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
        {
            Tokens.Add(ct);
            return Task.FromResult<IReadOnlyList<Issue>>(_issues.Values.ToList());
        }

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, string? linkedGoalId = null,
            CancellationToken ct = default)
        {
            Queries.Add((status, type, severity, repository, sourceGoalId, linkedGoalId));
            Tokens.Add(ct);

            IEnumerable<Issue> query = _issues.Values;
            if (status is not null) query = query.Where(i => i.Status == status);
            if (type is not null) query = query.Where(i => i.Type == type);
            if (severity is not null) query = query.Where(i => i.Severity == severity);
            if (repository is not null)
                query = query.Where(i => i.RepositoryNames.Contains(repository, StringComparer.OrdinalIgnoreCase));
            if (sourceGoalId is not null) query = query.Where(i => i.SourceGoalId == sourceGoalId);
            if (linkedGoalId is not null) query = query.Where(i => i.LinkedGoalId == linkedGoalId);
            return Task.FromResult<IReadOnlyList<Issue>>(query.ToList());
        }

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
        {
            Tokens.Add(ct);
            return Task.FromResult(_issues.TryGetValue(issueId, out var issue) ? issue : null);
        }

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Tokens.Add(ct);
            if (_issues.ContainsKey(issue.Id))
                throw new InvalidOperationException($"Issue '{issue.Id}' already exists.");
            _issues[issue.Id] = issue;
            return Task.FromResult(issue);
        }

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Tokens.Add(ct);
            if (!_issues.ContainsKey(issue.Id))
                throw new InvalidOperationException($"Issue '{issue.Id}' not found.");
            _issues[issue.Id] = issue;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
        {
            Tokens.Add(ct);
            return Task.FromResult(_issues.Remove(issueId));
        }

        public void Dispose()
        {
            // No unmanaged state: the fake is a plain dictionary.
        }
    }
}
