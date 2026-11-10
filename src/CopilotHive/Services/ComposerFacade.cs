using System.Security.Claims;

using CopilotHive.Orchestration;
using CopilotHive.Security;

using Microsoft.Extensions.AI;

namespace CopilotHive.Services;

/// <summary>
/// Facade over the Composer's RUNTIME surface: the active model, the available-model catalog,
/// runtime model switching and session compaction. Endpoint handlers and the Composer chat
/// component depend on this interface instead of reaching into <see cref="Composer"/> (or an
/// <c>HttpClient</c>) directly, so the request/response shaping lives in exactly one place.
/// </summary>
/// <remarks>
/// <para>
/// AUTHORIZATION IS PART OF EVERY OPERATION: each operation takes the caller's per-request (or
/// per-circuit) <see cref="ClaimsPrincipal"/> plus a <see cref="CancellationToken"/>, re-resolves
/// the acting principal FRESH through <see cref="HivePrincipalResolver"/> — never trusting cookie
/// claims and never retaining a resolved principal between calls — and requires
/// <see cref="HiveCapability.InteractiveMember"/>. A refusal or a principal that does not hold the
/// capability produces <see cref="FacadeErrorKind.Forbidden"/> BEFORE any Composer work runs and
/// outside the compaction catch-all. There is deliberately NO identity-free overload: an
/// unauthorized caller cannot reach a Composer operation at all.
/// </para>
/// <para>
/// Composer CONFIGURATION (persisted model, max steps, event notifications) is NOT part of this
/// facade — it lives on <see cref="IConfigFacade"/> and is deliberately not aliased here.
/// </para>
/// <para>
/// Exception semantics mirror the pre-facade endpoint handlers EXACTLY:
/// <see cref="SwitchModelAsync"/> catches <see cref="ArgumentException"/> and maps it to
/// <see cref="FacadeErrorKind.BadRequest"/>; <see cref="CompactAsync"/> and
/// <see cref="CompactPartialAsync"/> have the handlers' catch-all and map an OPERATIONAL failure to
/// <see cref="FacadeErrorKind.BadRequest"/>; the two reads
/// (<see cref="GetCurrentModelAsync"/> and <see cref="GetModelsAsync"/>) catch NOTHING, so a failure
/// there propagates to the caller exactly as it did before. Anything not listed is RETHROWN,
/// never mapped. Caller cancellation is never an answer: <see cref="OperationCanceledException"/>
/// propagates out of every operation (the resolver runs before the compaction try block, so it can
/// never be converted into a result).
/// </para>
/// </remarks>
public interface IComposerFacade
{
    /// <summary>
    /// Reads the Composer's current model.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates and is never mapped.</param>
    /// <returns>
    /// A SUCCESS result carrying <c>CurrentModelDto(null)</c> when the Composer has no active
    /// model — an unconfigured/disconnected Composer is a NORMAL state, not an error. Only a
    /// null Composer produces a failure (<see cref="FacadeErrorKind.NotConfigured"/>), and only an
    /// unauthorized caller produces <see cref="FacadeErrorKind.Forbidden"/>.
    /// </returns>
    Task<FacadeResult<CurrentModelDto>> GetCurrentModelAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Lists the Composer's normalised model catalog together with its current reasoning effort.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates and is never mapped.</param>
    /// <returns>
    /// A success result carrying the catalog (<see cref="Composer.AvailableModels"/> — the sole
    /// listing authority, identical to what <see cref="SwitchModelAsync"/> validates against),
    /// <see cref="FacadeErrorKind.NotConfigured"/> when the Composer is null, or
    /// <see cref="FacadeErrorKind.Forbidden"/> for an unauthorized caller.
    /// </returns>
    Task<FacadeResult<ComposerModelsDto>> GetModelsAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Switches the running Composer to another model without losing the session.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="model">Model identifier to switch to; required.</param>
    /// <param name="reasoning">
    /// Reasoning effort in its canonical wire form (e.g. <c>extra_high</c>); required — a switch
    /// always carries an explicit effort so the Composer can never inherit a stale one.
    /// </param>
    /// <param name="ct">Caller cancellation token; cancellation propagates and is never mapped.</param>
    /// <returns>
    /// A success result carrying the applied model and reasoning effort,
    /// <see cref="FacadeErrorKind.BadRequest"/> for a missing/unparsable/unavailable value, or
    /// <see cref="FacadeErrorKind.Forbidden"/> for an unauthorized caller.
    /// </returns>
    Task<FacadeResult<SwitchResultDto>> SwitchModelAsync(
        ClaimsPrincipal? actingUser, string? model, string? reasoning, CancellationToken ct);

    /// <summary>
    /// Compacts the whole Composer session.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates and is never mapped.</param>
    /// <returns>
    /// A success result carrying whether compaction ran and the resulting message count,
    /// <see cref="FacadeErrorKind.BadRequest"/> carrying an OPERATIONAL failure message, or
    /// <see cref="FacadeErrorKind.Forbidden"/> for an unauthorized caller.
    /// </returns>
    Task<FacadeResult<CompactResultDto>> CompactAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Compacts the oldest <paramref name="percent"/> percent of the Composer session.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="percent">Percentage of the oldest messages to compact.</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates and is never mapped.</param>
    /// <returns>
    /// A success result carrying whether compaction ran and the resulting message count,
    /// <see cref="FacadeErrorKind.BadRequest"/> carrying an OPERATIONAL failure message, or
    /// <see cref="FacadeErrorKind.Forbidden"/> for an unauthorized caller.
    /// </returns>
    Task<FacadeResult<CompactResultDto>> CompactPartialAsync(
        ClaimsPrincipal? actingUser, int percent, CancellationToken ct);
}

/// <summary>
/// Default implementation of <see cref="IComposerFacade"/> delegating to <see cref="Composer"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Composer may be absent (no Composer registered at all). In that case EVERY authorized
/// operation returns the SAME explicit failure — <c>Success = false</c>, <c>Error = "Composer is not
/// available."</c>, <see cref="FacadeErrorKind.NotConfigured"/> — instead of throwing a
/// <see cref="NullReferenceException"/> or fabricating an empty payload. Authorization still runs
/// FIRST, so an unauthorized caller never learns whether a Composer exists.
/// </para>
/// <para>
/// This type is registered as a SINGLETON, so it holds only stateless collaborators
/// (<see cref="HivePrincipalResolver"/> and <see cref="HivePolicy"/>) and the LEGACY singleton
/// <see cref="Composer"/>. It never captures the scoped <see cref="CircuitUserPrincipalAccessor"/>
/// and never caches an <see cref="ActingUserPrincipal"/>: identity flows in per call.
/// </para>
/// </remarks>
public sealed class ComposerFacade : IComposerFacade
{
    /// <summary>Error message every operation returns when no Composer is available.</summary>
    internal const string ComposerUnavailableError = "Composer is not available.";

    private readonly Composer? _composer;
    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy;
    private readonly ILogger<ComposerFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="ComposerFacade"/>.
    /// </summary>
    /// <param name="composer">The Composer to expose, or <c>null</c> when none is registered.</param>
    /// <param name="resolver">The stateless principal resolver every operation re-resolves through.</param>
    /// <param name="policy">The pure capability evaluator.</param>
    /// <param name="log">Logger instance.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ComposerFacade(
        Composer? composer,
        HivePrincipalResolver resolver,
        HivePolicy policy,
        ILogger<ComposerFacade> log)
    {
        _composer = composer;
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Builds the shared "no Composer" failure result for a value-producing operation.</summary>
    private FacadeResult<T> NotAvailable<T>(string operation)
    {
        _log.LogWarning("Composer is not available; {Operation} cannot run.", operation);
        return new(false, default, ComposerUnavailableError, FacadeErrorKind.NotConfigured);
    }

    /// <summary>Builds the shared sanitized authorization-denial result.</summary>
    private static FacadeResult<T> Forbidden<T>() =>
        new(false, default, FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden);

    /// <summary>
    /// Resolves the caller FRESH and requires <see cref="HiveCapability.InteractiveMember"/>. Runs
    /// before any Composer work and outside the compaction catch-all, so a denial is never a
    /// <see cref="FacadeErrorKind.BadRequest"/> and cancellation is never converted into a result.
    /// </summary>
    private Task<bool> IsAuthorizedAsync(ClaimsPrincipal? actingUser, string operation, CancellationToken ct) =>
        FacadeAuthorization.IsAllowedAsync(
            _resolver, _policy, actingUser, HiveCapability.InteractiveMember, _log, operation, ct);

    /// <inheritdoc />
    public async Task<FacadeResult<CurrentModelDto>> GetCurrentModelAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(actingUser, nameof(GetCurrentModelAsync), ct).ConfigureAwait(false))
            return Forbidden<CurrentModelDto>();

        if (_composer is null)
            return NotAvailable<CurrentModelDto>(nameof(GetCurrentModelAsync));

        // Frozen contract: a Composer that is not connected / has no active model reports a NULL
        // model on a SUCCESSFUL result — a value is never fabricated from the catalog.
        // Nothing is caught here: the pre-facade read handler caught nothing either.
        var model = _composer.GetStats()?.Model;
        return new(true, new CurrentModelDto(model), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ComposerModelsDto>> GetModelsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(actingUser, nameof(GetModelsAsync), ct).ConfigureAwait(false))
            return Forbidden<ComposerModelsDto>();

        if (_composer is null)
            return NotAvailable<ComposerModelsDto>(nameof(GetModelsAsync));

        // The Composer's normalised catalog is the sole authority for listing, so listed models
        // are IDENTICAL to what SwitchModelAsync validates. Nothing is caught (read handler).
        return new(
            true,
            new ComposerModelsDto(_composer.AvailableModels, _composer.ReasoningEffort),
            null,
            FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SwitchResultDto>> SwitchModelAsync(
        ClaimsPrincipal? actingUser, string? model, string? reasoning, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(actingUser, nameof(SwitchModelAsync), ct).ConfigureAwait(false))
            return Forbidden<SwitchResultDto>();

        if (_composer is null)
            return NotAvailable<SwitchResultDto>(nameof(SwitchModelAsync));

        // Both values are required: a model switch always carries an explicit reasoning effort so
        // the running Composer can never inherit a stale one.
        if (string.IsNullOrWhiteSpace(model))
            return new(false, null, "The 'model' query parameter is required.", FacadeErrorKind.BadRequest);
        if (string.IsNullOrWhiteSpace(reasoning))
            return new(false, null, "The 'reasoning' query parameter is required.", FacadeErrorKind.BadRequest);

        ReasoningEffort parsedReasoning;
        try
        {
            // The canonical wire form is parsed explicitly. Parse only returns null for
            // null/empty/whitespace, already rejected above.
            parsedReasoning = ReasoningEffortConverter.Parse(reasoning)!.Value;
        }
        catch (ArgumentException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }

        try
        {
            // The Composer's normalised catalog is the sole authority for switch validation —
            // identical to what the backend SwitchModelAsync validates.
            var validModels = _composer.AvailableModels.ToList();
            if (!validModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Model '{model}' is not available. Available models: {string.Join(", ", validModels)}.",
                    nameof(model));
            }

            await _composer.SwitchModelAsync(model, parsedReasoning, ct).ConfigureAwait(false);
            _log.LogInformation("Composer switched to model {Model}.", model);
            return new(
                true,
                new SwitchResultDto(_composer.GetStats()?.Model ?? model, _composer.ReasoningEffort),
                null,
                FacadeErrorKind.None);
        }
        catch (ArgumentException ex)
        {
            // ONLY ArgumentException is mapped — every other failure propagates, exactly as the
            // pre-facade handler behaved. Cancellation is not an ArgumentException, so it
            // propagates too.
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<CompactResultDto>> CompactAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // AUTHORIZATION FIRST, OUTSIDE the operational catch-all below: a denial can therefore
        // never surface as a BadRequest, and caller cancellation can never be converted into a
        // result by the catch-all.
        if (!await IsAuthorizedAsync(actingUser, nameof(CompactAsync), ct).ConfigureAwait(false))
            return Forbidden<CompactResultDto>();

        if (_composer is null)
            return NotAvailable<CompactResultDto>(nameof(CompactAsync));

        try
        {
            var compacted = await _composer.CompactSessionAsync(ct).ConfigureAwait(false);
            return new(
                true,
                new CompactResultDto(compacted, _composer.GetStats()?.MessageCount ?? 0),
                null,
                FacadeErrorKind.None);
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation is not an operational failure: it propagates unchanged (the
            // pre-facade handler never caught it either).
            throw;
        }
        catch (Exception ex)
        {
            // The pre-facade handler had a catch-all mapping to 400 — preserved verbatim for
            // OPERATIONAL failures.
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<CompactResultDto>> CompactPartialAsync(
        ClaimsPrincipal? actingUser, int percent, CancellationToken ct)
    {
        // See CompactAsync: authorization runs first and outside the catch-all.
        if (!await IsAuthorizedAsync(actingUser, nameof(CompactPartialAsync), ct).ConfigureAwait(false))
            return Forbidden<CompactResultDto>();

        if (_composer is null)
            return NotAvailable<CompactResultDto>(nameof(CompactPartialAsync));

        try
        {
            var compacted = await _composer.CompactOldestPercentAsync(percent, ct).ConfigureAwait(false);
            return new(
                true,
                new CompactResultDto(compacted, _composer.GetStats()?.MessageCount ?? 0),
                null,
                FacadeErrorKind.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The pre-facade handler had a catch-all mapping to 400 — preserved verbatim for
            // OPERATIONAL failures.
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
    }
}
