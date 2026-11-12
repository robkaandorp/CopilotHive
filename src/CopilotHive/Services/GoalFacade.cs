using System.Security.Claims;

using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Security;

namespace CopilotHive.Services;

/// <summary>
/// Facade over the goal operations the Goals and Goal Detail pages (and the goal REST API) perform:
/// listing, reading, searching, iterations, dependency statuses, the per-goal LIVE dashboard detail
/// and progress feeds, deleting a goal, changing its status (including its review status), requesting
/// a pre-execution review, cancelling it, extending its iteration budget, attaching it to a release,
/// and listing the issues linked to it. Endpoint handlers and the Blazor components depend on this
/// interface instead of reaching into the stores/dispatcher/dashboard directly, so each operation's
/// validation order, authorization and side effects live in exactly one place and run exactly once.
/// </summary>
/// <remarks>
/// <para>
/// AUTHORIZATION IS PART OF EVERY OPERATION, AND IT RUNS FIRST. Each operation takes the caller's
/// per-request (or per-circuit) <see cref="ClaimsPrincipal"/> and re-resolves the acting principal
/// FRESH through <see cref="HivePrincipalResolver"/> — never trusting cookie claims and never
/// retaining a resolved principal between calls — so a demoted, disabled or deleted account is
/// refused on its very NEXT operation. A refusal, or a principal that does not hold the required
/// <see cref="HiveCapability"/>, produces <see cref="FacadeErrorKind.Forbidden"/> BEFORE any store
/// read, any validation and any side effect: a Viewer sending an invalid update therefore gets
/// Forbidden, never BadRequest. There is deliberately NO identity-free overload, so no caller can
/// reach a covered operation without an identity.
/// </para>
/// <para>
/// THE ROLE MAP this facade enforces: every read (list, detail, search, iterations, dependency
/// statuses, linked issues, live dashboard detail, progress) requires
/// <see cref="HiveCapability.SharedRead"/>; every mutation (delete, status, review status, review,
/// cancel, extend-iterations, attach-release) requires
/// <see cref="HiveCapability.ExecutionOperator"/>. <see cref="HiveCapability.GlobalAdmin"/> is never
/// consulted here, so an Operator is never held to the admin bar. Open mode resolves the explicit
/// open-mode local operator through the same resolver, so the single-operator installation keeps its
/// previous behaviour exactly.
/// </para>
/// <para>
/// Exception semantics mirror the pre-facade handlers EXACTLY: an operation catches ONLY what its
/// handler caught (<see cref="UpdateGoalStatusAsync"/> catches <see cref="KeyNotFoundException"/> and
/// <see cref="ArgumentException"/>; <see cref="RequestReviewAsync"/> catches
/// <see cref="InvalidOperationException"/> from <see cref="GoalReviewService"/>;
/// <see cref="DeleteGoalAsync"/> catches everything the knowledge-document cleanup throws and logs
/// it). Anything else is RETHROWN. Caller cancellation is never an answer: it propagates out of every
/// operation and is never converted into a denial.
/// </para>
/// <para>
/// Cancellation is preserved per operation, NOT normalised: only
/// <see cref="RequestReviewAsync"/>, <see cref="ExtendIterationsAsync"/>,
/// <see cref="GetLinkedIssuesAsync"/> and the read methods took a token before the facade existed, so
/// only they accept one. <see cref="DeleteGoalAsync"/>, <see cref="UpdateGoalStatusAsync"/>,
/// <see cref="UpdateReviewStatusAsync"/>, <see cref="CancelGoalAsync"/> and
/// <see cref="AttachReleaseAsync"/> take none — adding one would be a behaviour change.
/// </para>
/// <para>
/// SCOPE: ONLY the goal routes and the three named pages are covered
/// (<c>GET /api/goals</c>, <c>GET /api/goals/{id}</c>, <c>GET /api/goals/search</c>,
/// <c>PATCH /api/goals/{id}/status</c>, <c>PATCH /api/goals/{id}/review-status</c>,
/// <c>POST /api/goals/{goalId}/review</c>, <c>POST /api/goals/{id}/cancel</c>,
/// <c>POST /api/goals/{id}/extend-iterations</c>, <c>PATCH /api/goals/{id}/release</c>,
/// <c>DELETE /api/goals/{id}</c>) plus the component-only reads. Goal CREATION is deliberately NOT
/// here (a separate, dependent goal), and <see cref="IGoalStore.GetReleasesAsync"/> release
/// dropdown/list reads stay a direct store read (an explicit exception).
/// </para>
/// </remarks>
public interface IGoalFacade
{
    /// <summary>
    /// Lists every goal, for the Goals page and <c>GET /api/goals</c>.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The DOMAIN goals (the unchanged wire shape of the list route), or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold shared read.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<Goal>>> GetGoalsAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads a single goal by ID, for the Goal Detail page and <c>GET /api/goals/{id}</c>.
    /// </summary>
    /// <param name="id">The goal ID to read.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The DOMAIN goal (the unchanged wire shape of the detail route),
    /// <see cref="FacadeErrorKind.NotFound"/> when it does not exist, or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold shared read.
    /// </returns>
    Task<FacadeResult<Goal>> GetGoalAsync(string id, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Searches goals by text query with the endpoint's LENIENT status filter, for
    /// <c>GET /api/goals/search</c>.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="status">
    /// The raw status filter. Exactly as before: an empty/absent value filters nothing and an
    /// unparsable value is IGNORED rather than rejected (this route never validated it).
    /// </param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The matching DOMAIN goals (the unchanged wire shape of the search route), or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold shared read.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<Goal>>> SearchGoalsAsync(
        string query, string? status, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Lists a goal's stored iteration summaries, for the Goal Detail page's completed/failed path.
    /// </summary>
    /// <param name="goalId">The goal ID whose iterations to read.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The iteration summaries in store order (empty when the goal has none), or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold shared read.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<IterationSummary>>> GetIterationsAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Resolves the CURRENT status of every goal the given goal depends on, for the Goal Detail
    /// page's dependency card. Each dependency prefers its LIVE dashboard detail and falls back to
    /// the authoritative stored goal, exactly as the page did when it issued those reads itself.
    /// </summary>
    /// <param name="goalId">The goal whose dependencies to resolve.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A dependency ID → current status map (empty when the goal has no dependencies or none can be
    /// resolved), or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// shared read.
    /// </returns>
    Task<FacadeResult<IReadOnlyDictionary<string, GoalStatus>>> GetDependencyStatusesAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the goal's LIVE dashboard detail (the rich per-iteration view the Goal Detail page
    /// renders for an active pipeline), replacing the page's direct
    /// <see cref="DashboardStateService.GetGoalDetail"/> call.
    /// </summary>
    /// <param name="goalId">The goal ID whose live detail to read.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A SUCCESS result whose value is the live detail, or <c>null</c> when the goal has no live
    /// pipeline state (the page then renders its stored path) — or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold shared read.
    /// </returns>
    Task<FacadeResult<GoalDetailInfo>> GetLiveGoalDetailAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the goal's recent worker progress entries, replacing the page's direct
    /// <see cref="ProgressReportService.GetProgressForGoal"/> call.
    /// </summary>
    /// <param name="goalId">The goal ID whose progress to read.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The progress entries (empty when nothing was reported), or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold shared read.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<ProgressEntry>>> GetProgressAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Lists the issues that reference a goal, either as their source goal or as their linked
    /// goal. Performs BOTH issue-store queries (the same two the Goal Detail page issued as two
    /// <c>GET /api/issues</c> calls), concatenates them and deduplicates by issue ID.
    /// </summary>
    /// <param name="goalId">The goal ID to look up.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token propagated to both queries.</param>
    /// <returns>
    /// The deduplicated issues (source-goal matches first, in store order; an empty list when
    /// nothing references the goal), or <see cref="FacadeErrorKind.Forbidden"/> when the caller does
    /// not currently hold shared read.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<LinkedIssueDto>>> GetLinkedIssuesAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Deletes a Draft or Failed goal, then best-effort cleans up its knowledge documents and
    /// (for Failed goals) its remote feature branches, and notifies the dashboard.
    /// </summary>
    /// <param name="id">The goal ID to delete.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <returns>
    /// A valueless success result (the route answers 204 No Content with no body),
    /// <see cref="FacadeErrorKind.NotFound"/> when the goal does not exist (or vanished before
    /// the delete), <see cref="FacadeErrorKind.BadRequest"/> when the goal is neither Draft nor
    /// Failed, or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.ExecutionOperator"/>.
    /// </returns>
    /// <remarks>
    /// Takes no cancellation token: the pre-facade handler accepted none, and the
    /// knowledge-document cleanup keeps its explicit <see cref="CancellationToken.None"/>.
    /// </remarks>
    Task<FacadeResult> DeleteGoalAsync(string id, ClaimsPrincipal? actingUser);

    /// <summary>
    /// Applies a status transition to a goal. Only Draft→Pending, Pending→Draft and
    /// Failed→Draft are permitted; Failed→Draft additionally resets iteration data, clears the
    /// dispatcher's retry state, and best-effort deletes the goal's remote feature branches.
    /// </summary>
    /// <param name="id">The goal ID to update.</param>
    /// <param name="status">The requested status, parsed case-insensitively from its name.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <returns>
    /// The full goal after the update, <see cref="FacadeErrorKind.NotFound"/> when the goal does
    /// not exist, <see cref="FacadeErrorKind.BadRequest"/> for an unparsable status or a disallowed
    /// transition, or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently
    /// hold <see cref="HiveCapability.ExecutionOperator"/> (checked BEFORE the parse).
    /// </returns>
    Task<FacadeResult<GoalDto>> UpdateGoalStatusAsync(string id, string status, ClaimsPrincipal? actingUser);

    /// <summary>
    /// Applies a pre-execution review-status change to a goal (the route the UI uses to record
    /// "pending", "approved" or "needs changes").
    /// </summary>
    /// <param name="id">The goal ID to update.</param>
    /// <param name="reviewStatus">The requested review status: none, pending, approved, needschanges.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <returns>
    /// The full goal after the update (the domain shape the route always returned),
    /// <see cref="FacadeErrorKind.NotFound"/> when the goal does not exist,
    /// <see cref="FacadeErrorKind.BadRequest"/> for numeric, unparsable or comma-combined input, or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.ExecutionOperator"/> (checked BEFORE the goal is read).
    /// </returns>
    /// <remarks>Takes no cancellation token: the pre-facade handler accepted none.</remarks>
    Task<FacadeResult<Goal>> UpdateReviewStatusAsync(string id, string reviewStatus, ClaimsPrincipal? actingUser);

    /// <summary>
    /// Runs a pre-execution review for a Draft goal.
    /// </summary>
    /// <param name="goalId">The goal ID to review.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token propagated to the store and the review service.</param>
    /// <returns>
    /// The review verdict, <see cref="FacadeErrorKind.NotFound"/> when the goal does not exist,
    /// <see cref="FacadeErrorKind.BadRequest"/> when it is not a Draft (checked FIRST), or
    /// <see cref="FacadeErrorKind.Conflict"/> when a review is already in progress — either
    /// because the goal's review status is already Pending or because
    /// <see cref="GoalReviewService"/> reports a concurrent review.
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.ExecutionOperator"/>.
    /// </returns>
    Task<FacadeResult<ReviewResultDto>> RequestReviewAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Cancels an InProgress or Pending goal.
    /// </summary>
    /// <param name="id">The goal ID to cancel.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <returns>
    /// The confirmation message, <see cref="FacadeErrorKind.NotFound"/> when the goal does not
    /// exist, <see cref="FacadeErrorKind.BadRequest"/> when the goal has ANY other status
    /// (Draft and the terminal statuses included) or the dispatcher could not cancel it, or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.ExecutionOperator"/>.
    /// </returns>
    /// <remarks>Takes no cancellation token: the pre-facade handler accepted none.</remarks>
    Task<FacadeResult<CancelledResult>> CancelGoalAsync(string id, ClaimsPrincipal? actingUser);

    /// <summary>
    /// Extends a goal's iteration budget and resumes it.
    /// </summary>
    /// <param name="id">The goal ID to extend.</param>
    /// <param name="additionalIterations">Number of additional iterations to grant (1–100).</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token propagated to the dispatcher.</param>
    /// <returns>
    /// The confirmation message, <see cref="FacadeErrorKind.ServiceUnavailable"/> when no
    /// dispatcher is registered (checked FIRST, before the range check),
    /// <see cref="FacadeErrorKind.BadRequest"/> when <paramref name="additionalIterations"/> is
    /// outside 1–100, <see cref="FacadeErrorKind.NotFound"/> when the goal or its pipeline
    /// is not resumable, or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not
    /// currently hold <see cref="HiveCapability.ExecutionOperator"/> (checked before both).
    /// </returns>
    Task<FacadeResult<ExtendedResult>> ExtendIterationsAsync(
        string id, int additionalIterations, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Attaches a goal to an existing release.
    /// </summary>
    /// <param name="id">The goal ID to attach.</param>
    /// <param name="releaseId">The release to attach the goal to.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <returns>
    /// The full goal after the update, <see cref="FacadeErrorKind.NotFound"/> when the
    /// release (checked FIRST) or the goal does not exist, or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.ExecutionOperator"/>.
    /// </returns>
    /// <remarks>Takes no cancellation token: the pre-facade handler accepted none.</remarks>
    Task<FacadeResult<GoalDto>> AttachReleaseAsync(string id, string releaseId, ClaimsPrincipal? actingUser);
}

/// <summary>
/// Default implementation of <see cref="IGoalFacade"/>, delegating to the goal/issue stores,
/// the <see cref="GoalDispatcher"/>, the <see cref="GoalReviewService"/>, the dashboard state
/// services and the notifiers.
/// </summary>
/// <remarks>
/// Optional dependencies mirror the endpoints exactly: <see cref="IBrainRepoManager"/>,
/// <see cref="KnowledgeDocumentCleanupService"/>, <see cref="GoalReadyNotifier"/>,
/// <see cref="GoalDispatcher"/>, <see cref="DashboardStateService"/> and
/// <see cref="ProgressReportService"/> may be <c>null</c>, while the goal store, the issue store,
/// the review service, the dashboard notifier and the authorization primitives are required.
/// A null dashboard/progress service reports "no live detail" and an empty progress feed — it never
/// substitutes a direct store read for the missing live source.
/// </remarks>
public sealed class GoalFacade : IGoalFacade
{
    private readonly IGoalStore _goalStore;
    private readonly IIssueStore _issueStore;
    private readonly GoalReviewService _reviewService;
    private readonly DashboardNotifier _dashboardNotifier;
    private readonly IBrainRepoManager? _repoManager;
    private readonly KnowledgeDocumentCleanupService? _docCleanup;
    private readonly GoalReadyNotifier? _goalReadyNotifier;
    private readonly GoalDispatcher? _dispatcher;
    private readonly DashboardStateService? _dashboardState;
    private readonly ProgressReportService? _progressReports;
    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy;
    private readonly ILogger<GoalFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="GoalFacade"/>.
    /// </summary>
    /// <param name="goalStore">The goal store (required).</param>
    /// <param name="issueStore">The issue store (required) used by <see cref="GetLinkedIssuesAsync"/>.</param>
    /// <param name="reviewService">The pre-execution review service (required).</param>
    /// <param name="dashboardNotifier">The dashboard change notifier (required).</param>
    /// <param name="resolver">
    /// The stateless principal resolver every operation re-resolves through (required). It is
    /// required so there is no unauthenticated construction: the resolver and the policy are
    /// stateless singletons, and this facade never captures the scoped circuit accessor.
    /// </param>
    /// <param name="policy">The pure capability evaluator (required together with <paramref name="resolver"/>).</param>
    /// <param name="log">Logger instance.</param>
    /// <param name="repoManager">Brain repo manager for feature-branch cleanup, or <c>null</c>.</param>
    /// <param name="docCleanup">Knowledge-document cleanup service, or <c>null</c>.</param>
    /// <param name="goalReadyNotifier">Dispatcher wake-up notifier, or <c>null</c>.</param>
    /// <param name="dispatcher">The goal dispatcher, or <c>null</c> when not registered.</param>
    /// <param name="dashboardState">The live dashboard state service, or <c>null</c> when not registered.</param>
    /// <param name="progressReports">The progress report service, or <c>null</c> when not registered.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public GoalFacade(
        IGoalStore goalStore,
        IIssueStore issueStore,
        GoalReviewService reviewService,
        DashboardNotifier dashboardNotifier,
        HivePrincipalResolver resolver,
        HivePolicy policy,
        ILogger<GoalFacade> log,
        IBrainRepoManager? repoManager = null,
        KnowledgeDocumentCleanupService? docCleanup = null,
        GoalReadyNotifier? goalReadyNotifier = null,
        GoalDispatcher? dispatcher = null,
        DashboardStateService? dashboardState = null,
        ProgressReportService? progressReports = null)
    {
        _goalStore = goalStore;
        _issueStore = issueStore;
        _reviewService = reviewService;
        _dashboardNotifier = dashboardNotifier;
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _repoManager = repoManager;
        _docCleanup = docCleanup;
        _goalReadyNotifier = goalReadyNotifier;
        _dispatcher = dispatcher;
        _dashboardState = dashboardState;
        _progressReports = progressReports;
    }

    // ── Authorized reads (SharedRead) ───────────────────────────────────────

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<Goal>>> GetGoalsAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetGoalsAsync), ct))
            return Forbidden<IReadOnlyList<Goal>>();

        var goals = await _goalStore.GetAllGoalsAsync(ct);
        return new(true, goals, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<Goal>> GetGoalAsync(string id, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetGoalAsync), ct))
            return Forbidden<Goal>();

        var goal = await _goalStore.GetGoalAsync(id, ct);
        return goal is null ? NotFoundGoal<Goal>(id) : new(true, goal, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<Goal>>> SearchGoalsAsync(
        string query, string? status, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(SearchGoalsAsync), ct))
            return Forbidden<IReadOnlyList<Goal>>();

        // Lenient, exactly as the route always was: an absent/blank or unparsable status simply
        // filters nothing — this route never rejected an unknown value.
        GoalStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var parsed))
            statusFilter = parsed;

        var results = await _goalStore.SearchGoalsAsync(query, statusFilter, ct);
        return new(true, results, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<IterationSummary>>> GetIterationsAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetIterationsAsync), ct))
            return Forbidden<IReadOnlyList<IterationSummary>>();

        var iterations = await _goalStore.GetIterationsAsync(goalId, ct);
        return new(true, iterations, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyDictionary<string, GoalStatus>>> GetDependencyStatusesAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetDependencyStatusesAsync), ct))
            return Forbidden<IReadOnlyDictionary<string, GoalStatus>>();

        // The dependency list comes from the SAME two sources the page used: the live dashboard
        // detail first, then the authoritative stored goal.
        var detail = _dashboardState is null ? null : await _dashboardState.GetGoalDetail(goalId);
        var storedGoal = await _goalStore.GetGoalAsync(goalId, ct);
        var depIds = detail?.DependsOn ?? storedGoal?.DependsOn ?? [];

        var statuses = new Dictionary<string, GoalStatus>(StringComparer.Ordinal);
        foreach (var depId in depIds)
        {
            var depDetail = _dashboardState is null ? null : await _dashboardState.GetGoalDetail(depId);
            if (depDetail is not null)
            {
                statuses[depId] = depDetail.Status;
                continue;
            }

            var depStored = await _goalStore.GetGoalAsync(depId, ct);
            if (depStored is not null)
                statuses[depId] = depStored.Status;
        }

        return new(true, statuses, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<GoalDetailInfo>> GetLiveGoalDetailAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetLiveGoalDetailAsync), ct))
            return Forbidden<GoalDetailInfo>();

        // No dashboard service means there is no live state to read: that is a SUCCESS carrying no
        // detail (the caller renders its stored path), never a store read standing in for it.
        if (_dashboardState is null)
            return new(true, default, null, FacadeErrorKind.None);

        var detail = await _dashboardState.GetGoalDetail(goalId);
        return new(true, detail, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<ProgressEntry>>> GetProgressAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetProgressAsync), ct))
            return Forbidden<IReadOnlyList<ProgressEntry>>();

        IReadOnlyList<ProgressEntry> progress = _progressReports is null
            ? []
            : _progressReports.GetProgressForGoal(goalId);
        return new(true, progress, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<LinkedIssueDto>>> GetLinkedIssuesAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetLinkedIssuesAsync), ct))
            return Forbidden<IReadOnlyList<LinkedIssueDto>>();

        // The two queries mirror the two GET /api/issues calls the Goal Detail page issued:
        // one filtered by source_goal_id, one by linked_goal_id.
        var sourceIssues = await _issueStore.GetIssuesAsync(sourceGoalId: goalId, ct: ct);
        var linkedIssues = await _issueStore.GetIssuesAsync(linkedGoalId: goalId, ct: ct);

        var merged = sourceIssues
            .Concat(linkedIssues)
            .DistinctBy(i => i.Id)
            .Select(LinkedIssueDto.From)
            .ToList();

        return new(true, merged, null, FacadeErrorKind.None);
    }

    // ── Authorized mutations (ExecutionOperator) ────────────────────────────

    /// <inheritdoc />
    public async Task<FacadeResult> DeleteGoalAsync(string id, ClaimsPrincipal? actingUser)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(DeleteGoalAsync), CancellationToken.None))
            return Forbidden();

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return new(false, $"Goal '{id}' not found.", FacadeErrorKind.NotFound);

        if (goal.Status is not (GoalStatus.Draft or GoalStatus.Failed))
            return new(false, "Only Draft or Failed goals can be deleted", FacadeErrorKind.BadRequest);

        var deleted = await _goalStore.DeleteGoalAsync(id);
        if (!deleted)
            return new(false, $"Goal '{id}' not found.", FacadeErrorKind.NotFound);

        // Best-effort cleanup of knowledge documents. The token is deliberately
        // CancellationToken.None: the delete is already committed, so the cleanup must not be
        // abandoned because the caller went away.
        if (_docCleanup is not null)
        {
            try
            {
                await _docCleanup.CleanupGoalDocumentsAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to cleanup knowledge documents for deleted goal '{GoalId}'", id);
            }
        }

        // Best-effort cleanup of remote feature branches for Failed goals
        if (goal.Status == GoalStatus.Failed)
        {
            await DeleteFeatureBranchesAsync(id, goal.RepositoryNames);
        }

        _dashboardNotifier.NotifyStateChanged();
        return new(true, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<GoalDto>> UpdateGoalStatusAsync(string id, string status, ClaimsPrincipal? actingUser)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(UpdateGoalStatusAsync), CancellationToken.None))
            return Forbidden<GoalDto>();

        try
        {
            var parsed = Enum.Parse<GoalStatus>(status, ignoreCase: true);

            var existing = await _goalStore.GetGoalAsync(id);
            if (existing is null)
                return NotFoundGoal<GoalDto>(id);

            // Allowed transitions via the public API:
            //   Draft ↔ Pending (approve / revert)
            //   Failed → Draft (retry — resets iteration data and cleans up feature branch)
            var validTransition =
                existing.Status == GoalStatus.Draft && parsed == GoalStatus.Pending ||
                existing.Status == GoalStatus.Pending && parsed == GoalStatus.Draft ||
                existing.Status == GoalStatus.Failed && parsed == GoalStatus.Draft;
            if (!validTransition)
            {
                return new(
                    false,
                    null,
                    $"Invalid transition from {existing.Status} to {parsed}. Only Draft→Pending, Pending→Draft, and Failed→Draft are allowed.",
                    FacadeErrorKind.BadRequest);
            }

            // Failed→Draft: reset iteration data and delete the feature branch (best-effort)
            if (existing.Status == GoalStatus.Failed && parsed == GoalStatus.Draft)
            {
                await _goalStore.ResetGoalIterationDataAsync(id);

                // Clear GoalDispatcher runtime state so the goal can be re-dispatched fresh
                _dispatcher?.ClearGoalRetryState(id);

                await DeleteFeatureBranchesAsync(id, existing.RepositoryNames);
            }

            await _goalStore.UpdateGoalStatusAsync(id, parsed);
            var goal = await _goalStore.GetGoalAsync(id);
            _dashboardNotifier.NotifyStateChanged();
            if (parsed == GoalStatus.Pending) _goalReadyNotifier?.NotifyGoalReady();
            return new(true, goal is null ? null : GoalDto.From(goal), null, FacadeErrorKind.None);
        }
        catch (KeyNotFoundException)
        {
            return NotFoundGoal<GoalDto>(id);
        }
        catch (ArgumentException)
        {
            return new(false, null, $"Invalid status '{status}'.", FacadeErrorKind.BadRequest);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<Goal>> UpdateReviewStatusAsync(string id, string reviewStatus, ClaimsPrincipal? actingUser)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(UpdateReviewStatusAsync), CancellationToken.None))
            return Forbidden<Goal>();

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return NotFoundGoal<Goal>(id);

        // Reject numeric input — only named enum values are accepted.
        if (int.TryParse(reviewStatus, out _))
            return InvalidReviewStatus<Goal>();

        // Parse and validate that the result is a defined single enum value.
        if (!Enum.TryParse<ReviewStatus>(reviewStatus, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            return InvalidReviewStatus<Goal>();

        // Reject comma-combined inputs (e.g. "Pending, Approved") that Enum.TryParse may accept via bitwise OR.
        if (reviewStatus.Contains(','))
            return InvalidReviewStatus<Goal>();

        goal.ReviewStatus = parsed;
        await _goalStore.UpdateGoalAsync(goal);
        // The pre-facade route answered the RE-READ goal, so a goal that vanished between the
        // update and the re-read still produced a success with no body — preserved here.
        var updated = await _goalStore.GetGoalAsync(id);
        _dashboardNotifier.NotifyStateChanged();
        return new(true, updated, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ReviewResultDto>> RequestReviewAsync(
        string goalId, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(RequestReviewAsync), ct))
            return Forbidden<ReviewResultDto>();

        var goal = await _goalStore.GetGoalAsync(goalId, ct);
        if (goal is null)
            return NotFoundGoal<ReviewResultDto>(goalId);

        // Frozen order: the non-Draft check runs BEFORE the review-in-progress check, so a
        // non-Draft goal reports 400 even when its review status is Pending.
        if (goal.Status != GoalStatus.Draft)
            return new(false, null, "Only Draft goals can be reviewed.", FacadeErrorKind.BadRequest);

        if (goal.ReviewStatus == ReviewStatus.Pending)
            return new(false, null, "A review is already in progress for this goal.", FacadeErrorKind.Conflict);

        try
        {
            var result = await _reviewService.ReviewGoalAsync(goal, ct);
            return new(true, new ReviewResultDto(result.Verdict, result.Issues, result.Summary), null, FacadeErrorKind.None);
        }
        catch (InvalidOperationException ex)
        {
            // The review service's own concurrency guard — a review started between the check
            // above and the call. Reported as a conflict, exactly as before.
            return new(false, null, ex.Message, FacadeErrorKind.Conflict);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<CancelledResult>> CancelGoalAsync(string id, ClaimsPrincipal? actingUser)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(CancelGoalAsync), CancellationToken.None))
            return Forbidden<CancelledResult>();

        // The pre-facade route bound GoalDispatcher as a REQUIRED service, so an absent
        // dispatcher failed the request with an exception (HTTP 500). Fail the same way here
        // instead of inventing a degraded result.
        if (_dispatcher is null)
            throw new InvalidOperationException("GoalDispatcher is not registered; goals cannot be cancelled.");

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return NotFoundGoal<CancelledResult>(id);

        // EVERY status other than InProgress/Pending is rejected — Draft and the terminal
        // statuses included.
        if (goal.Status is not (GoalStatus.InProgress or GoalStatus.Pending))
        {
            return new(
                false,
                null,
                $"Goal '{id}' is {goal.Status} and cannot be cancelled. Only InProgress or Pending goals can be cancelled.",
                FacadeErrorKind.BadRequest);
        }

        var cancelled = await _dispatcher.CancelGoalAsync(id);
        return cancelled
            ? new(true, new CancelledResult($"Goal '{id}' has been cancelled."), null, FacadeErrorKind.None)
            : new(
                false,
                null,
                $"Goal '{id}' could not be cancelled (it may have already completed or failed).",
                FacadeErrorKind.BadRequest);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ExtendedResult>> ExtendIterationsAsync(
        string id, int additionalIterations, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(ExtendIterationsAsync), ct))
            return Forbidden<ExtendedResult>();

        // Frozen order: dispatcher availability FIRST, so an invalid iteration count with an
        // absent dispatcher still reports ServiceUnavailable rather than BadRequest.
        if (_dispatcher is null)
            return new(false, null, "GoalDispatcher is not available.", FacadeErrorKind.ServiceUnavailable);

        if (additionalIterations <= 0 || additionalIterations > 100)
            return new(false, null, "additionalIterations must be between 1 and 100.", FacadeErrorKind.BadRequest);

        var success = await _dispatcher.ResumeGoalAsync(id, additionalIterations, ct);
        if (!success)
            return new(false, null, $"Goal '{id}' or its pipeline not found.", FacadeErrorKind.NotFound);

        return new(
            true,
            new ExtendedResult($"Extended iteration budget by {additionalIterations}."),
            null,
            FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<GoalDto>> AttachReleaseAsync(string id, string releaseId, ClaimsPrincipal? actingUser)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(AttachReleaseAsync), CancellationToken.None))
            return Forbidden<GoalDto>();

        var release = await _goalStore.GetReleaseAsync(releaseId);
        if (release is null)
            return new(false, null, $"Release '{releaseId}' not found.", FacadeErrorKind.NotFound);

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return NotFoundGoal<GoalDto>(id);

        goal.ReleaseId = releaseId;
        await _goalStore.UpdateGoalAsync(goal);
        _dashboardNotifier.NotifyStateChanged();
        return new(true, GoalDto.From(goal), null, FacadeErrorKind.None);
    }

    // ── Authorization + shared results ──────────────────────────────────────

    /// <summary>
    /// THE ONE AUTHORIZATION CALL every operation makes, BEFORE any of its own work: one fresh
    /// resolution of the caller's CURRENT stored state and one capability evaluation.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="capability">The capability the operation requires.</param>
    /// <param name="operation">The operation name (log context only).</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates, never a denial.</param>
    /// <returns><c>true</c> only when the caller currently holds <paramref name="capability"/>.</returns>
    private Task<bool> IsAllowedAsync(
        ClaimsPrincipal? actingUser, HiveCapability capability, string operation, CancellationToken ct) =>
        FacadeAuthorization.IsAllowedAsync(_resolver, _policy, actingUser, capability, _log, operation, ct);

    /// <summary>Builds the shared denial result for a value-producing operation.</summary>
    /// <typeparam name="T">The facade value type of the operation.</typeparam>
    /// <returns>A <see cref="FacadeErrorKind.Forbidden"/> result carrying the sanitized denial text.</returns>
    private static FacadeResult<T> Forbidden<T>() =>
        new(false, default, FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden);

    /// <summary>Builds the shared denial result for a valueless operation.</summary>
    /// <returns>A <see cref="FacadeErrorKind.Forbidden"/> result carrying the sanitized denial text.</returns>
    private static FacadeResult Forbidden() =>
        new(false, FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden);

    /// <summary>The frozen "invalid review status" failure the review-status route always produced.</summary>
    /// <typeparam name="T">The facade value type of the operation.</typeparam>
    /// <returns>A <see cref="FacadeErrorKind.BadRequest"/> result carrying the frozen message.</returns>
    private static FacadeResult<T> InvalidReviewStatus<T>() =>
        new(false, default,
            "Invalid review status. Valid values: none, pending, approved, needschanges.",
            FacadeErrorKind.BadRequest);

    /// <summary>Builds the shared "goal not found" failure result.</summary>
    /// <typeparam name="T">The facade value type of the operation.</typeparam>
    /// <param name="id">The goal ID that was not found.</param>
    /// <returns>A <see cref="FacadeErrorKind.NotFound"/> result carrying the standard message.</returns>
    private static FacadeResult<T> NotFoundGoal<T>(string id)
        => new(false, default, $"Goal '{id}' not found.", FacadeErrorKind.NotFound);

    /// <summary>
    /// Best-effort deletion of the goal's remote feature branches in each of its repositories.
    /// A missing repo manager is a no-op, exactly as in the pre-facade handlers.
    /// </summary>
    /// <param name="goalId">The goal whose feature branches to delete.</param>
    /// <param name="repositoryNames">The repositories to delete the branch from.</param>
    private async Task DeleteFeatureBranchesAsync(string goalId, IEnumerable<string> repositoryNames)
    {
        if (_repoManager is null)
            return;

        var branchName = $"copilothive/{goalId}";
        foreach (var repoName in repositoryNames)
        {
            _ = await _repoManager.DeleteRemoteBranchAsync(repoName, branchName);
        }
    }
}
