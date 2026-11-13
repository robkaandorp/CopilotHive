using System.Security.Claims;

using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using CopilotHive.Security;

using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// THE AUTHORIZED OPERATIONAL BOUNDARY over the live execution surface the Orchestrator page reads
/// and mutates: the master Brain session RESET and the SHARED operational read projections (Brain
/// stats, orchestrator info and the sanitized LLM-session list).
/// <para>
/// The Orchestrator component depends on this interface instead of reaching into
/// <see cref="DashboardStateService"/> or <see cref="LlmSessionRegistry"/> directly, so every
/// operational read and the one operational mutation carry an explicit acting identity that is
/// authorized BEFORE any service is touched. <see cref="DashboardStateService"/> and
/// <see cref="LlmSessionRegistry"/> themselves stay TRUSTED INTERNAL services with their public APIs
/// unchanged: this facade never reimplements any of their work, it delegates verbatim.
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
/// describe shared execution data, so they require <see cref="HiveCapability.SharedRead"/> (every
/// role). A refusal produces <see cref="FacadeErrorKind.Forbidden"/> carrying the single sanitized
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
/// <see cref="DashboardStateService"/> and <see cref="LlmSessionRegistry"/>. The dashboard state
/// service, the registry, the stateless principal resolver and the pure policy evaluator are all
/// required dependencies, so there is no unauthenticated — and no dashboard-less — construction.
/// </summary>
public sealed class OperationalFacade : IOperationalFacade
{
    private readonly DashboardStateService _dashboard;
    private readonly LlmSessionRegistry _sessions;
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
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public OperationalFacade(
        DashboardStateService dashboard,
        LlmSessionRegistry sessions,
        HivePrincipalResolver resolver,
        HivePolicy policy,
        ILogger<OperationalFacade> log)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
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
}
