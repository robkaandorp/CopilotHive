using CopilotHive.Orchestration;

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
/// Composer CONFIGURATION (persisted model, max steps, event notifications) is NOT part of this
/// facade — it lives on <see cref="IConfigFacade"/> and is deliberately not aliased here.
/// </para>
/// <para>
/// Exception semantics mirror the pre-facade endpoint handlers EXACTLY:
/// <see cref="SwitchModelAsync"/> catches <see cref="ArgumentException"/> and maps it to
/// <see cref="FacadeErrorKind.BadRequest"/>; <see cref="CompactAsync"/> and
/// <see cref="CompactPartialAsync"/> have the handlers' catch-all and map any exception to
/// <see cref="FacadeErrorKind.BadRequest"/>; the two reads
/// (<see cref="GetCurrentModelAsync"/> and <see cref="GetModels"/>) catch NOTHING, so a failure
/// there propagates to the caller exactly as it did before. Anything not listed is RETHROWN,
/// never mapped.
/// </para>
/// <para>
/// No operation takes a <see cref="CancellationToken"/>: none of the pre-facade endpoints
/// accepted one, so the adapters pass <see cref="CancellationToken.None"/> to the Composer.
/// </para>
/// </remarks>
public interface IComposerFacade
{
    /// <summary>
    /// Reads the Composer's current model.
    /// </summary>
    /// <returns>
    /// A SUCCESS result carrying <c>CurrentModelDto(null)</c> when the Composer has no active
    /// model — an unconfigured/disconnected Composer is a NORMAL state, not an error. Only a
    /// null Composer produces a failure (<see cref="FacadeErrorKind.NotConfigured"/>).
    /// </returns>
    Task<FacadeResult<CurrentModelDto>> GetCurrentModelAsync();

    /// <summary>
    /// Lists the Composer's normalised model catalog together with its current reasoning effort.
    /// </summary>
    /// <returns>
    /// A success result carrying the catalog (<see cref="Composer.AvailableModels"/> — the sole
    /// listing authority, identical to what <see cref="SwitchModelAsync"/> validates against), or
    /// <see cref="FacadeErrorKind.NotConfigured"/> when the Composer is null.
    /// </returns>
    FacadeResult<ComposerModelsDto> GetModels();

    /// <summary>
    /// Switches the running Composer to another model without losing the session.
    /// </summary>
    /// <param name="model">Model identifier to switch to; required.</param>
    /// <param name="reasoning">
    /// Reasoning effort in its canonical wire form (e.g. <c>extra_high</c>); required — a switch
    /// always carries an explicit effort so the Composer can never inherit a stale one.
    /// </param>
    /// <returns>
    /// A success result carrying the applied model and reasoning effort, or
    /// <see cref="FacadeErrorKind.BadRequest"/> for a missing/unparsable/unavailable value.
    /// </returns>
    Task<FacadeResult<SwitchResultDto>> SwitchModelAsync(string? model, string? reasoning);

    /// <summary>
    /// Compacts the whole Composer session.
    /// </summary>
    /// <returns>
    /// A success result carrying whether compaction ran and the resulting message count, or
    /// <see cref="FacadeErrorKind.BadRequest"/> carrying the failure message.
    /// </returns>
    Task<FacadeResult<CompactResultDto>> CompactAsync();

    /// <summary>
    /// Compacts the oldest <paramref name="percent"/> percent of the Composer session.
    /// </summary>
    /// <param name="percent">Percentage of the oldest messages to compact.</param>
    /// <returns>
    /// A success result carrying whether compaction ran and the resulting message count, or
    /// <see cref="FacadeErrorKind.BadRequest"/> carrying the failure message.
    /// </returns>
    Task<FacadeResult<CompactResultDto>> CompactPartialAsync(int percent);
}

/// <summary>
/// Default implementation of <see cref="IComposerFacade"/> delegating to <see cref="Composer"/>.
/// </summary>
/// <remarks>
/// The Composer may be absent (no Composer registered at all). In that case EVERY operation
/// returns the SAME explicit failure — <c>Success = false</c>, <c>Error = "Composer is not
/// available."</c>, <see cref="FacadeErrorKind.NotConfigured"/> — instead of throwing a
/// <see cref="NullReferenceException"/> or fabricating an empty payload.
/// </remarks>
public sealed class ComposerFacade : IComposerFacade
{
    /// <summary>Error message every operation returns when no Composer is available.</summary>
    internal const string ComposerUnavailableError = "Composer is not available.";

    private readonly Composer? _composer;
    private readonly ILogger<ComposerFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="ComposerFacade"/>.
    /// </summary>
    /// <param name="composer">The Composer to expose, or <c>null</c> when none is registered.</param>
    /// <param name="log">Logger instance.</param>
    public ComposerFacade(Composer? composer, ILogger<ComposerFacade> log)
    {
        _composer = composer;
        _log = log;
    }

    /// <summary>Builds the shared "no Composer" failure result for a value-producing operation.</summary>
    private FacadeResult<T> NotAvailable<T>(string operation)
    {
        _log.LogWarning("Composer is not available; {Operation} cannot run.", operation);
        return new(false, default, ComposerUnavailableError, FacadeErrorKind.NotConfigured);
    }

    /// <inheritdoc />
    public Task<FacadeResult<CurrentModelDto>> GetCurrentModelAsync()
    {
        if (_composer is null)
            return Task.FromResult(NotAvailable<CurrentModelDto>(nameof(GetCurrentModelAsync)));

        // Frozen contract: a Composer that is not connected / has no active model reports a NULL
        // model on a SUCCESSFUL result — a value is never fabricated from the catalog.
        // Nothing is caught here: the pre-facade read handler caught nothing either.
        var model = _composer.GetStats()?.Model;
        return Task.FromResult<FacadeResult<CurrentModelDto>>(
            new(true, new CurrentModelDto(model), null, FacadeErrorKind.None));
    }

    /// <inheritdoc />
    public FacadeResult<ComposerModelsDto> GetModels()
    {
        if (_composer is null)
            return NotAvailable<ComposerModelsDto>(nameof(GetModels));

        // The Composer's normalised catalog is the sole authority for listing, so listed models
        // are IDENTICAL to what SwitchModelAsync validates. Nothing is caught (read handler).
        return new(
            true,
            new ComposerModelsDto(_composer.AvailableModels, _composer.ReasoningEffort),
            null,
            FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SwitchResultDto>> SwitchModelAsync(string? model, string? reasoning)
    {
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

            await _composer.SwitchModelAsync(model, parsedReasoning, CancellationToken.None);
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
            // pre-facade handler behaved.
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<CompactResultDto>> CompactAsync()
    {
        if (_composer is null)
            return NotAvailable<CompactResultDto>(nameof(CompactAsync));

        try
        {
            var compacted = await _composer.CompactSessionAsync(CancellationToken.None);
            return new(
                true,
                new CompactResultDto(compacted, _composer.GetStats()?.MessageCount ?? 0),
                null,
                FacadeErrorKind.None);
        }
        catch (Exception ex)
        {
            // The pre-facade handler had a catch-all mapping to 400 — preserved verbatim.
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<CompactResultDto>> CompactPartialAsync(int percent)
    {
        if (_composer is null)
            return NotAvailable<CompactResultDto>(nameof(CompactPartialAsync));

        try
        {
            var compacted = await _composer.CompactOldestPercentAsync(percent, CancellationToken.None);
            return new(
                true,
                new CompactResultDto(compacted, _composer.GetStats()?.MessageCount ?? 0),
                null,
                FacadeErrorKind.None);
        }
        catch (Exception ex)
        {
            // The pre-facade handler had a catch-all mapping to 400 — preserved verbatim.
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
    }
}
