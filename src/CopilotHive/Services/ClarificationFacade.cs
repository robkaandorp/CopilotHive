using System.Security.Claims;

using CopilotHive.Orchestration;
using CopilotHive.Security;

namespace CopilotHive.Services;

/// <summary>
/// THE AUTHORIZED API ADAPTER over <see cref="ClarificationQueueService"/>: the four
/// <c>/api/clarifications</c> routes delegate here instead of reaching into the queue, so each
/// request's identity is authorized BEFORE the queue is read or mutated.
/// </summary>
/// <remarks>
/// <para>
/// AUTHORIZATION IS PART OF EVERY OPERATION, AND IT RUNS FIRST. Each operation takes the caller's
/// per-request <see cref="ClaimsPrincipal"/> and re-resolves the acting principal FRESH through
/// <see cref="HivePrincipalResolver"/> — never trusting cookie claims and never retaining a resolved
/// principal between calls — so a demoted, disabled or deleted account is refused on its very NEXT
/// operation. The three reads (all requests, pending-human requests, pending count) require
/// <see cref="HiveCapability.SharedRead"/>; submitting an answer requires
/// <see cref="HiveCapability.ExecutionOperator"/>. A refusal produces
/// <see cref="FacadeErrorKind.Forbidden"/> with the single sanitized denial text BEFORE any queue
/// read, validation or mutation.
/// </para>
/// <para>
/// <see cref="ClarificationQueueService"/> ITSELF IS UNCHANGED. It stays the trusted internal queue
/// whose other callers — <c>ClarificationHandler</c> and the Composer's own
/// <c>"composer"</c>-attributed answers — keep working without modification. This adapter is the
/// USER-FACING boundary only; it does not claim to protect those internal callers.
/// </para>
/// </remarks>
public interface IClarificationFacade
{
    /// <summary>
    /// Lists every clarification request regardless of status, newest first.
    /// </summary>
    /// <param name="actingUser">The per-request identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The queue's own request list (the unchanged wire shape of <c>GET /api/clarifications</c>), or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold shared read.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<ClarificationRequest>>> GetAllRequestsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Lists the requests currently awaiting a human answer, oldest first.
    /// </summary>
    /// <param name="actingUser">The per-request identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The queue's own pending list (the unchanged wire shape of <c>/pending</c>), or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold shared read.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<ClarificationRequest>>> GetPendingHumanRequestsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the number of requests currently awaiting a human answer.
    /// </summary>
    /// <param name="actingUser">The per-request identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The queue's own count (the unchanged wire shape of <c>/count</c>), or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold shared read.
    /// </returns>
    Task<FacadeResult<int>> GetPendingHumanCountAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Submits a HUMAN answer to a clarification request.
    /// </summary>
    /// <param name="id">The clarification request ID.</param>
    /// <param name="answer">The answer text.</param>
    /// <param name="actingUser">The per-request identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success with the unchanged confirmation message, <see cref="FacadeErrorKind.BadRequest"/>
    /// carrying <c>"Answer is required."</c> for a blank answer,
    /// <see cref="FacadeErrorKind.NotFound"/> carrying <c>"Clarification '{id}' not found."</c> when
    /// the request does not exist, or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not
    /// currently hold <see cref="HiveCapability.ExecutionOperator"/>.
    /// </returns>
    Task<FacadeResult<ClarificationAnswerResult>> SubmitAnswerAsync(
        string id, string answer, ClaimsPrincipal? actingUser, CancellationToken ct);
}

/// <summary>
/// The confirmation <c>POST /api/clarifications/{id}/answer</c> returns for an accepted answer.
/// </summary>
/// <param name="Message">The unchanged confirmation text, naming the answered request.</param>
public sealed record ClarificationAnswerResult(string Message);

/// <summary>
/// Default implementation of <see cref="IClarificationFacade"/> delegating to the unchanged
/// <see cref="ClarificationQueueService"/>. The queue, the stateless principal resolver and the pure
/// policy evaluator are all required dependencies, so there is no unauthenticated construction.
/// </summary>
public sealed class ClarificationFacade : IClarificationFacade
{
    /// <summary>The attribution recorded for an answer submitted through this human-facing adapter.</summary>
    private const string HumanResolver = "human";

    private readonly ClarificationQueueService _queue;
    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy;
    private readonly ILogger<ClarificationFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="ClarificationFacade"/>.
    /// </summary>
    /// <param name="queue">The trusted clarification queue (required, unchanged).</param>
    /// <param name="resolver">
    /// The stateless principal resolver every operation re-resolves through (required). It is
    /// required so there is no unauthenticated construction: the resolver and the policy are
    /// stateless singletons, and this facade never captures the scoped circuit accessor.
    /// </param>
    /// <param name="policy">The pure capability evaluator (required together with <paramref name="resolver"/>).</param>
    /// <param name="log">Logger instance.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ClarificationFacade(
        ClarificationQueueService queue,
        HivePrincipalResolver resolver,
        HivePolicy policy,
        ILogger<ClarificationFacade> log)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<ClarificationRequest>>> GetAllRequestsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetAllRequestsAsync), ct))
            return Forbidden<IReadOnlyList<ClarificationRequest>>();

        return new(true, _queue.GetAllRequests(), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<ClarificationRequest>>> GetPendingHumanRequestsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetPendingHumanRequestsAsync), ct))
            return Forbidden<IReadOnlyList<ClarificationRequest>>();

        return new(true, _queue.GetPendingHumanRequests(), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<int>> GetPendingHumanCountAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetPendingHumanCountAsync), ct))
            return Forbidden<int>();

        return new(true, _queue.PendingHumanCount, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ClarificationAnswerResult>> SubmitAnswerAsync(
        string id, string answer, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // AUTHORIZATION FIRST: a denied caller is never told whether the request exists and never
        // learns the body contract.
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(SubmitAnswerAsync), ct))
            return Forbidden<ClarificationAnswerResult>();

        if (string.IsNullOrWhiteSpace(answer))
            return new(false, null, "Answer is required.", FacadeErrorKind.BadRequest);

        if (!_queue.SubmitAnswer(id, answer, HumanResolver))
            return new(false, null, $"Clarification '{id}' not found.", FacadeErrorKind.NotFound);

        return new(true, new ClarificationAnswerResult($"Answer submitted for clarification '{id}'."),
            null, FacadeErrorKind.None);
    }

    // ── Authorization + shared results ──────────────────────────────────────

    /// <summary>
    /// THE ONE AUTHORIZATION CALL every operation makes, BEFORE any of its own work: one fresh
    /// resolution of the caller's CURRENT stored state and one capability evaluation.
    /// </summary>
    /// <param name="actingUser">The per-request identity, or <c>null</c> when signed out.</param>
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
}
