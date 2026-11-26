using System.Security.Claims;

using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Security;

using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// THE AUTHORIZED OPERATIONAL BOUNDARY over the live execution surface the Orchestrator page reads
/// and mutates: the master Brain session RESET and the SHARED operational read projections (Brain
/// stats, orchestrator info, the sanitized LLM-session list, and the knowledge-document
/// list/detail/count projections the Knowledge pages and the navigation badge render).
/// <para>
/// The Orchestrator, Knowledge, KnowledgeDetail and NavMenu components depend on this interface
/// instead of reaching into <see cref="DashboardStateService"/> or <see cref="LlmSessionRegistry"/>
/// directly, so every operational read and the one operational mutation carry an explicit acting
/// identity that is authorized BEFORE any service is touched. <see cref="DashboardStateService"/>
/// and <see cref="LlmSessionRegistry"/> themselves stay TRUSTED INTERNAL services with their public
/// APIs unchanged: this facade never reimplements any of their work, it delegates verbatim.
/// </para>
/// <para>
/// KNOWLEDGE PROJECTIONS ARE DETACHED: the knowledge operations return value copies
/// (<see cref="KnowledgeDocumentListProjection"/>, <see cref="KnowledgeDocumentProjection"/>) with
/// only the fields the pages render, never the live mutable
/// <see cref="Knowledge.KnowledgeGraph"/>/<see cref="Knowledge.KnowledgeDocument"/> or its mutable
/// tag/link collections. Linked document TITLES are resolved inside these operations, while the
/// caller is authorized, because resolving a title is itself a graph read.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// AUTHORIZATION IS PART OF EVERY OPERATION, AND IT RUNS FIRST. Each operation takes the caller's
/// per-request / per-circuit <see cref="ClaimsPrincipal"/> and re-resolves the acting principal FRESH
/// through <see cref="HivePrincipalResolver"/> — never trusting cookie claims and never retaining a
/// resolved principal between calls — so a demoted, disabled or deleted account is refused on its
/// very NEXT operation. There is deliberately NO identity-free overload.
/// </para>
/// <para>
/// THE CAPABILITY SPLIT: resetting the master Brain session rewrites the orchestrator's live
/// conversation for everybody, so it requires <see cref="HiveCapability.ExecutionOperator"/>
/// (<see cref="UserRole.Operator"/> and above; a Viewer or a Member is refused). The read projections
/// — including all three knowledge reads and the three DASHBOARD/WORKERS/WORKER-PROGRESS display
/// projections — describe shared data, so they require
/// <see cref="HiveCapability.SharedRead"/> (every role). A refusal produces
/// <see cref="FacadeErrorKind.Forbidden"/> carrying the single sanitized
/// <see cref="FacadeAuthorization.ForbiddenMessage"/> BEFORE any read or mutation runs.
/// </para>
/// <para>
/// OPEN MODE IS UNCHANGED: with authentication disabled the resolver answers the full
/// <see cref="ActingUserPrincipal.LocalOperator"/> for the forwarded identity (a Blazor circuit in
/// open mode has none, so it forwards <c>null</c>), that principal holds every capability, and the
/// legacy single-operator installation therefore keeps working exactly as before.
/// </para>
/// </remarks>
public interface IOperationalFacade
{
    /// <summary>
    /// Resets the MASTER Brain conversation session, so the next goal starts from a freshly loaded
    /// orchestrator instruction set. Per-goal sessions of goals already in progress are untouched.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token forwarded to the reset; cancellation propagates.</param>
    /// <returns>
    /// A success result (<see cref="FacadeErrorKind.None"/>) once the unchanged
    /// <see cref="DashboardStateService.ResetBrainSessionAsync"/> has completed, or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.ExecutionOperator"/>. Any failure of the reset itself propagates.
    /// </returns>
    Task<FacadeResult> ResetBrainSessionAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the Brain's current usage statistics — shared execution data.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result carrying the Brain's live statistics, or a success result whose value is
    /// <c>null</c> when no Brain is configured (exactly what
    /// <see cref="DashboardStateService.GetBrainStats"/> reports), or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.SharedRead"/>.
    /// </returns>
    Task<FacadeResult<BrainStats>> GetBrainStatsAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the orchestrator's shared system information (uptime, versions, model configuration).
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result carrying the unchanged
    /// <see cref="DashboardStateService.GetOrchestratorInfo"/> projection, or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.SharedRead"/>.
    /// </returns>
    Task<FacadeResult<OrchestratorInfo>> GetOrchestratorInfoAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the SHARED execution LLM sessions — <see cref="LlmSessionRegistry.GetAll"/> with every
    /// <see cref="LlmSessionType.Composer"/> entry removed.
    /// </summary>
    /// <remarks>
    /// The filter is <see cref="SharedSessionProjection.SharedOnly"/>, the SAME method the
    /// <c>GET /api/sessions</c> route uses, so the two shared-read surfaces cannot drift apart. The
    /// registry's own <see cref="LlmSessionRegistry.GetAll"/> semantics are unchanged: internal
    /// callers still see every entry, this projection is the shared-read boundary only.
    /// </remarks>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result carrying the non-Composer sessions (an empty list when none are registered),
    /// or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.SharedRead"/>.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<LlmSessionInfo>>> GetSharedLlmSessionsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the SHARED knowledge-document list projection — optionally applying the graph's own
    /// search semantics — together with whether a knowledge graph is configured at all.
    /// </summary>
    /// <remarks>
    /// The projection is DETACHED: each row is a value copy (see
    /// <see cref="KnowledgeDocumentSummary"/>), never the live mutable
    /// <see cref="Knowledge.KnowledgeDocument"/> or its mutable tag/link collections. When
    /// <paramref name="searchQuery"/> is non-blank the rows come from the unchanged
    /// <see cref="Knowledge.KnowledgeGraph.Search(string)"/> semantics; otherwise they come from
    /// <see cref="Knowledge.KnowledgeGraph.GetAllDocuments"/>. The page keeps its own type/status/tag
    /// filtering and ordering, exactly as before.
    /// </remarks>
    /// <param name="searchQuery">The optional search text, or <c>null</c>/blank for every document.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result carrying the detached rows and the configured flag (an UNCONFIGURED graph is
    /// a success with <see cref="KnowledgeDocumentListProjection.IsConfigured"/> <c>false</c>, never a
    /// denial), or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.SharedRead"/>.
    /// </returns>
    Task<FacadeResult<KnowledgeDocumentListProjection>> GetKnowledgeDocumentsAsync(
        string? searchQuery, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads ONE knowledge document plus its outgoing and incoming links, with every linked
    /// document's title RESOLVED HERE while the caller is authorized.
    /// </summary>
    /// <remarks>
    /// Resolving a link title is itself a graph read, so it happens inside this authorized operation:
    /// the component never looks a document up to fill in a link row. Every returned part is
    /// DETACHED (see <see cref="KnowledgeDocumentProjection"/>).
    /// </remarks>
    /// <param name="documentId">The document identifier to read.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result carrying the detached document and its links,
    /// <see cref="FacadeErrorKind.NotFound"/> when the document does not exist (or the graph is not
    /// configured — the page renders its existing not-found surface for both), or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.SharedRead"/>.
    /// </returns>
    Task<FacadeResult<KnowledgeDocumentProjection>> GetKnowledgeDocumentAsync(
        string documentId, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the SHARED knowledge-document count (the navigation badge's number).
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result carrying the unchanged
    /// <see cref="DashboardStateService.GetKnowledgeDocumentCount"/> answer (0 when the graph is not
    /// configured), or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.SharedRead"/>.
    /// </returns>
    Task<FacadeResult<int>> GetKnowledgeDocumentCountAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the DASHBOARD DISPLAY PROJECTION — the <c>Home</c> page's cards and its two tables — as
    /// one detached, bounded value.
    /// </summary>
    /// <remarks>
    /// THE MERGED SNAPSHOT'S OWN WORK IS REUSED, NOT REIMPLEMENTED:
    /// <see cref="DashboardStateService.GetSnapshot"/> still performs the side-effect-free merge of the
    /// goal store, the pipelines and the one worker capture, so the phase-derived statuses, the store
    /// precedence and the full-set counts are exactly what the page rendered before. This operation
    /// only PROJECTS that answer: the five goal counts are carried through unchanged (they always
    /// describe the WHOLE set), the worker half is reduced to the two counts the dashboard shows (no
    /// worker row is returned) and the rows are bounded — see
    /// <see cref="DashboardDisplaySnapshot.RecentGoals"/>, which is the newest
    /// <see cref="DashboardDisplaySnapshot.RecentGoalLimit"/> Draft/Completed/Failed goals ordered by
    /// creation time descending, and <see cref="DashboardDisplaySnapshot.Pipelines"/>, which keeps the
    /// snapshot's existing order.
    /// </remarks>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result carrying the detached display projection, or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.SharedRead"/>. A failure of the underlying snapshot read propagates.
    /// </returns>
    Task<FacadeResult<DashboardDisplaySnapshot>> GetDashboardDisplayAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the WORKERS DISPLAY PROJECTION — the <c>Workers</c> table and the
    /// <c>WorkerDetail</c> header cards — as one detached value: every worker row plus all five counts,
    /// all derived from ONE <c>WorkerPool.CaptureWorkerStatus</c> call.
    /// </summary>
    /// <remarks>
    /// NO GOAL, PIPELINE OR COMPOSER DATA IS READ — not even indirectly: unlike
    /// <see cref="GetOrchestratorInfoAsync"/> this operation does not touch the Brain's or the
    /// Composer's live statistics, and it reads no goal store and no pipeline. The model and reasoning
    /// values a worker row displays are computed inside the trusted service from COPIED role
    /// configuration, so no live configuration object crosses the boundary.
    /// </remarks>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result carrying the detached worker rows and counts (an empty row list when no worker
    /// is connected), or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently
    /// hold <see cref="HiveCapability.SharedRead"/>.
    /// </returns>
    Task<FacadeResult<WorkerDisplayProjection>> GetWorkerDisplayAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads ONE worker's progress-report rows for the <c>WorkerDetail</c> table, through the existing
    /// capped <see cref="ProgressReportService.GetProgressForWorker(string, int)"/> aggregation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE AGGREGATION IS REUSED VERBATIM: pipeline-collected reports plus the global log, with the
    /// service's own deduplication, ordering and cap — the facade never re-collects, re-orders,
    /// re-caps or back-fills. No worker status is captured and no Composer statistics are read.
    /// </para>
    /// <para>
    /// THE DISCLOSURE OF <see cref="WorkerProgressDisplayEntry.Status"/> AND
    /// <see cref="WorkerProgressDisplayEntry.Details"/> FOLLOWS THE INSTALLATION MODE. While
    /// authentication is enabled, the already-capped rows are FILTERED (never extended) to the seven
    /// <see cref="WorkerProgressDisplayEntry.ApprovedStatuses"/>, matched as a full value ordinally and
    /// case-insensitively without trimming; each kept row carries the canonical spelling and a
    /// <c>null</c> Details, and a row with any other status is OMITTED ENTIRELY rather than rewritten.
    /// Only an EXPLICITLY open installation returns the raw status and details. BOTH modes authorize
    /// first, and a <c>null</c> identity in authenticated mode is refused — it never selects the raw
    /// branch.
    /// </para>
    /// </remarks>
    /// <param name="workerId">The worker whose reports to read.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result carrying the projected rows (empty when the worker has reported nothing, or
    /// when no row has an approved status), or <see cref="FacadeErrorKind.Forbidden"/> when the caller
    /// does not currently hold <see cref="HiveCapability.SharedRead"/>.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<WorkerProgressDisplayEntry>>> GetWorkerProgressAsync(
        string workerId, ClaimsPrincipal? actingUser, CancellationToken ct);
}

/// <summary>
/// THE ONE non-Composer session filter shared by every SHARED-READ surface: the
/// <c>GET /api/sessions</c> route and <see cref="IOperationalFacade.GetSharedLlmSessionsAsync"/>.
/// <para>
/// A <see cref="LlmSessionType.Composer"/> session is per-USER PRIVATE metadata, so it is never part
/// of a shared projection; the exclusion lives HERE, in exactly one place, so the two surfaces cannot
/// drift. <see cref="LlmSessionRegistry"/> and its <see cref="LlmSessionRegistry.GetAll"/> are
/// deliberately untouched, so internal callers keep observing every session.
/// </para>
/// </summary>
public static class SharedSessionProjection
{
    /// <summary>
    /// Returns the entries of <paramref name="sessions"/> that may be exposed on a SHARED-READ
    /// surface: everything except <see cref="LlmSessionType.Composer"/>. Registration order is
    /// preserved and the input sequence is not modified.
    /// </summary>
    /// <param name="sessions">The registry snapshot to project.</param>
    /// <returns>The non-Composer sessions, as a detached list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    public static IReadOnlyList<LlmSessionInfo> SharedOnly(IEnumerable<LlmSessionInfo> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        return sessions
            .Where(session => session.SessionType != LlmSessionType.Composer)
            .ToList();
    }
}

/// <summary>
/// Default implementation of <see cref="IOperationalFacade"/> delegating to the unchanged
/// <see cref="DashboardStateService"/> (including its <see cref="DashboardStateService.KnowledgeGraph"/>
/// and <see cref="DashboardStateService.GetKnowledgeDocumentCount"/>, so the constructor takes no new
/// dependency) and <see cref="LlmSessionRegistry"/>, plus the already-registered
/// <see cref="ProgressReportService"/> behind the one worker-progress read. The dashboard state
/// service, the registry, the stateless principal resolver and the pure policy evaluator are all
/// required dependencies, so there is no unauthenticated — and no dashboard-less — construction.
/// </summary>
public sealed class OperationalFacade : IOperationalFacade
{
    private readonly DashboardStateService _dashboard;
    private readonly LlmSessionRegistry _sessions;
    private readonly ProgressReportService _progressReports;
    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy;
    private readonly ILogger<OperationalFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="OperationalFacade"/>.
    /// </summary>
    /// <param name="dashboard">The trusted live dashboard state service (required, unchanged).</param>
    /// <param name="sessions">The trusted LLM session registry (required, unchanged).</param>
    /// <param name="resolver">
    /// The stateless principal resolver every operation re-resolves through (required). It is
    /// required so there is no unauthenticated construction: the resolver and the policy are
    /// stateless singletons, and this facade never captures the scoped circuit accessor.
    /// </param>
    /// <param name="policy">The pure capability evaluator (required together with <paramref name="resolver"/>).</param>
    /// <param name="log">Logger used for secret-free denial and operation diagnostics.</param>
    /// <param name="progressReports">
    /// The trusted progress-report aggregation behind <see cref="GetWorkerProgressAsync"/> — REQUIRED,
    /// exactly as the host registers it (the host already resolves it with
    /// <c>GetRequiredService</c>), so there is no partially-wired construction and no silent
    /// "no reports" fallback for an operation that was never given its data source.
    /// </param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public OperationalFacade(
        DashboardStateService dashboard,
        LlmSessionRegistry sessions,
        HivePrincipalResolver resolver,
        HivePolicy policy,
        ILogger<OperationalFacade> log,
        ProgressReportService progressReports)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _progressReports = progressReports ?? throw new ArgumentNullException(nameof(progressReports));
    }

    // ── Authorized mutation (ExecutionOperator) ─────────────────────────────

    /// <inheritdoc />
    public async Task<FacadeResult> ResetBrainSessionAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // AUTHORIZATION FIRST: a denied caller never reaches the Brain — not even the
        // "no Brain configured" no-op inside DashboardStateService.
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(ResetBrainSessionAsync), ct))
            return Forbidden();

        // Delegation verbatim: the reset itself (master session only; per-goal sessions preserved)
        // is the trusted service's work, and a failure of it propagates exactly as it did when the
        // component called the service directly.
        await _dashboard.ResetBrainSessionAsync(ct);
        _log.LogInformation("Reset the master Brain session for an authorized operator.");
        return new FacadeResult(true, null, FacadeErrorKind.None);
    }

    // ── Authorized shared reads (SharedRead) ────────────────────────────────

    /// <inheritdoc />
    public async Task<FacadeResult<BrainStats>> GetBrainStatsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetBrainStatsAsync), ct))
            return Forbidden<BrainStats>();

        // The service's own "no Brain configured" answer (null) is PRESERVED as a success: the page
        // renders its existing "Brain not connected." surface for it, and a null value is not a
        // denial.
        return new(true, _dashboard.GetBrainStats(), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<OrchestratorInfo>> GetOrchestratorInfoAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetOrchestratorInfoAsync), ct))
            return Forbidden<OrchestratorInfo>();

        return new(true, _dashboard.GetOrchestratorInfo(), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<LlmSessionInfo>>> GetSharedLlmSessionsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetSharedLlmSessionsAsync), ct))
            return Forbidden<IReadOnlyList<LlmSessionInfo>>();

        // THE SHARED-READ SESSION PROJECTION, and the reason there is no "composer stats" method
        // here: a Composer SESSION is per-user private metadata and Composer USAGE
        // (DashboardStateService.GetComposerStats) is per-user private usage. Both are deliberately
        // ABSENT from every shared projection — the sessions are removed by the one shared filter
        // below and the composer statistics are simply never projected. A user's own Composer usage
        // is rendered by the Composer's own per-circuit runtime binding (ComposerChat), never by a
        // shared page.
        return new(
            true,
            SharedSessionProjection.SharedOnly(_sessions.GetAll()),
            null,
            FacadeErrorKind.None);
    }

    // ── Authorized knowledge reads (SharedRead) ─────────────────────────────

    /// <inheritdoc />
    public async Task<FacadeResult<KnowledgeDocumentListProjection>> GetKnowledgeDocumentsAsync(
        string? searchQuery, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // AUTHORIZATION FIRST: the denied caller never reaches DashboardStateService or its graph.
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetKnowledgeDocumentsAsync), ct))
            return Forbidden<KnowledgeDocumentListProjection>();

        var graph = _dashboard.KnowledgeGraph;

        // An UNCONFIGURED graph is the service's own answer and stays a SUCCESS: the page renders its
        // existing "knowledge graph is not configured" surface for it. A denial is never disguised as
        // "not configured", and "not configured" is never disguised as a denial.
        if (graph is null)
            return new(true, new KnowledgeDocumentListProjection(false, []), null, FacadeErrorKind.None);

        // The graph's OWN search semantics are preserved verbatim: a non-blank query goes through
        // Search(...), everything else through GetAllDocuments(). Only the PROJECTION is new; the
        // returned rows are detached copies, so no live document or mutable tag list escapes.
        var documents = string.IsNullOrWhiteSpace(searchQuery)
            ? graph.GetAllDocuments()
            : graph.Search(searchQuery.Trim());

        return new(
            true,
            new KnowledgeDocumentListProjection(
                true,
                DetachedKnowledgeCopy.Of(documents.Select(KnowledgeDocumentSummary.From))),
            null,
            FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<KnowledgeDocumentProjection>> GetKnowledgeDocumentAsync(
        string documentId, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // AUTHORIZATION FIRST: no graph access before the fresh capability answer.
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetKnowledgeDocumentAsync), ct))
            return Forbidden<KnowledgeDocumentProjection>();

        var graph = _dashboard.KnowledgeGraph;

        // An unconfigured graph has no documents: the page's existing not-found surface is the right
        // answer, exactly as when the page itself saw a null graph.
        if (graph is null)
            return NotFoundKnowledgeDocument(documentId);

        var document = graph.GetDocument(documentId);
        if (document is null)
            return NotFoundKnowledgeDocument(documentId);

        // LINK TITLES ARE RESOLVED HERE, while the caller is authorized: resolving a title is a graph
        // read, so the component must never perform it. A missing target/source simply yields a null
        // title (the page then renders only the id, exactly as before).
        var outgoing = graph.GetOutgoingLinks(documentId)
            .Select(link => new KnowledgeOutgoingLink(
                link.TargetId,
                graph.GetDocument(link.TargetId)?.Title,
                link.Type,
                link.Description))
            .ToList();

        var incoming = graph.GetIncomingLinks(documentId)
            .Select(link => new KnowledgeIncomingLink(
                link.SourceId,
                graph.GetDocument(link.SourceId)?.Title,
                link.Type,
                link.Description))
            .ToList();

        return new(
            true,
            new KnowledgeDocumentProjection(
                KnowledgeDocumentDetail.From(document),
                DetachedKnowledgeCopy.Of(outgoing),
                DetachedKnowledgeCopy.Of(incoming)),
            null,
            FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<int>> GetKnowledgeDocumentCountAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetKnowledgeDocumentCountAsync), ct))
            return Forbidden<int>();

        // The service's own answer (0 when the graph is not configured) is preserved unchanged.
        return new(true, _dashboard.GetKnowledgeDocumentCount(), null, FacadeErrorKind.None);
    }

    // ── Authorized operational display projections (SharedRead) ──────────────

    /// <inheritdoc />
    public async Task<FacadeResult<DashboardDisplaySnapshot>> GetDashboardDisplayAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // AUTHORIZATION FIRST: the denied caller never reaches the snapshot's store, pipeline or pool read.
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetDashboardDisplayAsync), ct))
            return Forbidden<DashboardDisplaySnapshot>();

        // THE SIDE-EFFECT-FREE MERGED SNAPSHOT, REUSED: its own merge (store precedence, phase-derived
        // display statuses, ONE worker capture) is the trusted service's work and is not reimplemented
        // here. Only the PROJECTION below is new; an unexpected failure of the read propagates exactly
        // as it did when the page called the service directly.
        var snapshot = await _dashboard.GetSnapshot();

        // RECENT GOALS: the newest twenty Draft/Completed/Failed rows by creation time descending.
        // CreatedAt is the SORT KEY only — it is deliberately absent from the projected row — and the
        // cap is applied HERE so the projection is bounded, not merely the rendering.
        var recentGoals = snapshot.Goals
            .Where(goal => goal.Status is GoalStatus.Draft or GoalStatus.Completed or GoalStatus.Failed)
            .OrderByDescending(goal => goal.CreatedAt)
            .Take(DashboardDisplaySnapshot.RecentGoalLimit)
            .Select(goal => new DashboardGoalDisplayEntry(goal.Id, goal.Status, goal.Iterations))
            .ToList();

        // PIPELINES: the snapshot's existing order, with the description reduced to its bounded
        // preview — the full text and the omitted suffix never leave this operation.
        var pipelines = snapshot.Pipelines
            .Select(pipeline => new DashboardPipelineDisplayEntry(
                pipeline.GoalId,
                pipeline.Phase,
                pipeline.Iteration,
                pipeline.TotalTests,
                pipeline.PassedTests,
                pipeline.FailedTests,
                pipeline.CoveragePercent,
                pipeline.CreatedAt,
                pipeline.GoalStartedAt,
                pipeline.CompletedAt,
                OperationalTextPreview.Of(pipeline.Description)))
            .ToList();

        return new(
            true,
            new DashboardDisplaySnapshot(
                // THE FIVE FULL-SET COUNTS, carried through unchanged: they always describe the whole
                // goal set, not the bounded recent set.
                snapshot.DraftGoals,
                snapshot.PendingGoals,
                snapshot.ActiveGoals,
                snapshot.CompletedGoals,
                snapshot.FailedGoals,
                // The dashboard shows ONLY these two worker numbers, and no worker row at all.
                snapshot.BusyWorkers,
                snapshot.TotalWorkers,
                recentGoals,
                pipelines),
            null,
            FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<WorkerDisplayProjection>> GetWorkerDisplayAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // AUTHORIZATION FIRST: no pool capture, no goal read and no configuration read before the answer.
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetWorkerDisplayAsync), ct))
            return Forbidden<WorkerDisplayProjection>();

        // ONE worker-only capture, producing the rows AND every count. The service reads no goal, no
        // pipeline and no Composer statistics for this projection.
        return new(true, _dashboard.GetWorkerDisplay(), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<WorkerProgressDisplayEntry>>> GetWorkerProgressAsync(
        string workerId, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // AUTHORIZATION FIRST, IN BOTH MODES: even the open-mode raw branch below is only reached by a
        // caller who currently holds SharedRead, and a null identity here has no bearing on that
        // answer — the resolver decides.
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetWorkerProgressAsync), ct))
            return Forbidden<IReadOnlyList<WorkerProgressDisplayEntry>>();

        // THE EXISTING AGGREGATION, REUSED VERBATIM: pipeline-collected reports plus the global log,
        // with its own deduplication, ordering and cap. No worker status is captured here and no
        // Composer statistics are read.
        var progress = _progressReports.GetProgressForWorker(workerId);

        // THE RAW BRANCH IS SELECTED BY THE EXPLICIT MODE, NEVER BY THE CALLER'S IDENTITY: an
        // authentication-enabled installation always takes the approved-status projection below, so a
        // null identity (refused above anyway) cannot reach raw data.
        if (!_resolver.Mode.AuthEnabled)
        {
            return new(
                true,
                progress
                    .Select(entry => new WorkerProgressDisplayEntry(
                        entry.Timestamp, entry.GoalId, entry.Status, entry.Details))
                    .ToList(),
                null,
                FacadeErrorKind.None);
        }

        // AUTHENTICATED PROJECTION: FILTER the already-capped rows (never extend them, never back-fill
        // after a filtered row), keeping only the seven approved statuses in their CANONICAL spelling
        // and dropping the free-text details. A row with any other status is omitted entirely.
        var projected = new List<WorkerProgressDisplayEntry>(progress.Count);
        foreach (var entry in progress)
        {
            var canonicalStatus = WorkerProgressDisplayEntry.CanonicalStatus(entry.Status);
            if (canonicalStatus is null)
                continue;

            projected.Add(new(entry.Timestamp, entry.GoalId, canonicalStatus, Details: null));
        }

        return new(true, projected, null, FacadeErrorKind.None);
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

    /// <summary>Builds the shared denial result for the value-less operation.</summary>
    /// <returns>A <see cref="FacadeErrorKind.Forbidden"/> result carrying the sanitized denial text.</returns>
    private static FacadeResult Forbidden() =>
        new(false, FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden);

    /// <summary>
    /// Builds the "this knowledge document does not exist" answer — the same shape for a document id
    /// the graph does not hold and for an unconfigured graph, because the page's existing
    /// not-found surface renders either way. It names only the id the caller already supplied.
    /// </summary>
    /// <param name="documentId">The requested document id (echoed, never a graph detail).</param>
    /// <returns>A <see cref="FacadeErrorKind.NotFound"/> result carrying no value.</returns>
    private static FacadeResult<KnowledgeDocumentProjection> NotFoundKnowledgeDocument(string documentId) =>
        new(false, default, $"Knowledge document '{documentId}' was not found.", FacadeErrorKind.NotFound);
}
