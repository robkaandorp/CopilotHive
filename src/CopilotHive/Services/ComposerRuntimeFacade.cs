using System.Security.Claims;

using CopilotHive.Orchestration;
using CopilotHive.Security;

namespace CopilotHive.Services;

/// <summary>
/// THE PER-USER RECOVERY FACADE: the <see cref="IComposerFacade"/> implementation that drives the
/// caller's OWN <see cref="ComposerUserRuntime"/> (through the scoped
/// <see cref="ComposerRuntimeAccessor"/>) instead of the legacy singleton <see cref="Composer"/>.
/// </summary>
/// <remarks>
/// <para>
/// NOT THE LIVE DEFAULT: the deployment gate
/// (<see cref="ComposerRuntimeFacadeDeployment.PerUserRuntimeEnabled"/>, read by
/// <see cref="ComposerFacadeRegistration"/>) defaults to OFF, so this facade is only ever selected when
/// an operator explicitly enables the per-user live path. With the gate OFF nothing in the host
/// resolves this type, so no split-runtime behaviour is observable.
/// </para>
/// <para>
/// ONE STATE MACHINE ONLY: the manager owns runtime lifecycle and status reconciliation. Every
/// operation asks the SCOPED accessor, which asks the manager, which reports
/// <see cref="ComposerRuntimeStatus.Connected"/> whenever a runtime settled as
/// <see cref="ComposerRuntimeStatus.Deferred"/> or <see cref="ComposerRuntimeStatus.DisconnectedShell"/>
/// but is in fact live (its own <c>ReconcileStatus</c>). This facade therefore performs NO second
/// reconciliation: it acts on the status it is handed and never inspects <c>IsConnected</c> to derive a
/// status of its own, and it never starts a connect for a runtime that is already live (the manager
/// skips that connect and reports the live status).
/// </para>
/// <para>
/// EXACT RECOVERY MAPPINGS — one per <see cref="ComposerRuntimeStatus"/>, decided per operation:
/// <list type="bullet">
/// <item><b>Connected</b> — the operation runs against the connected runtime.</item>
/// <item><b>DisconnectedShell</b> — READS answer with shell metadata (the frozen null-model contract plus
/// the configured catalog/effort); <see cref="SwitchModelAsync"/> PROCEEDS against the shell so the
/// actor's first-selection path may connect it; COMPACTION is attempted on the shell, which the runtime
/// itself refuses while it has no agent, so it returns the defined OPERATIONAL answer
/// (<see cref="FacadeErrorKind.BadRequest"/> carrying the runtime's own "not connected" message) exactly
/// as the legacy path does — a model-less shell is not a service outage, so no 503 is invented.</item>
/// <item><b>Deferred</b> — READS answer with shell metadata (a read never connects); every MUTATION
/// (switch or compaction) first attempts the operation-driven connect through the accessor. If the
/// manager reports <see cref="ComposerRuntimeStatus.Connected"/> the mutation proceeds; if it is still
/// <see cref="ComposerRuntimeStatus.Deferred"/> the operation answers
/// <see cref="FacadeErrorKind.ServiceUnavailable"/> with an actionable, secret-free message.</item>
/// <item><b>Failed</b> — <see cref="FacadeErrorKind.ServiceUnavailable"/> with a fixed sanitized message
/// (the fault itself is logged, never returned; it may name internal provider state).</item>
/// <item><b>Removed</b> — the handle is stale: the manager is asked ONCE more (the accessor is never
/// disposed — the manager owns every runtime, and it waits for the old teardown before creating a fresh
/// one), so a removed/recreated runtime is picked up transparently. A second
/// <see cref="ComposerRuntimeStatus.Removed"/> answers <see cref="FacadeErrorKind.Conflict"/> with a safe
/// retry/requery message.</item>
/// <item><b>Refused</b> — <see cref="FacadeErrorKind.Forbidden"/> with the sanitized denial text: no
/// runtime, no content. The ONLY exception is the explicit OPEN-MODE fallback below.</item>
/// </list>
/// </para>
/// <para>
/// OPEN MODE IS THE ONE EXPLICIT LEGACY FALLBACK: with authentication DISABLED the resolver yields the
/// synthetic local operator, which has no persisted account, so the manager refuses it
/// (<see cref="ComposerRuntimeRefusal.LocalOperator"/>) — and open mode is exactly the legacy
/// single-operator installation. That single refusal therefore delegates to the legacy
/// <see cref="ComposerFacade"/> (the legacy singleton <see cref="Composer"/>). The fallback is gated on
/// BOTH the open mode and that typed refusal: an AUTHENTICATED refusal NEVER falls back to the legacy
/// Composer, whatever its reason, so a refused account can never inherit the original admin's runtime.
/// </para>
/// <para>
/// AUTHORIZATION AND CANCELLATION: the accessor re-resolves the acting principal from CURRENT stored
/// state and requires <see cref="HiveCapability.InteractiveMember"/> before the manager is
/// asked for anything, so a Viewer (or a disabled/unknown account) gets <see cref="FacadeErrorKind.Forbidden"/>
/// and no runtime. The operation shaping itself is the LEGACY facade's own
/// (<see cref="ComposerFacade"/> instance helpers): identical validation, identical messages, the
/// existing operational <see cref="FacadeErrorKind.BadRequest"/> mapping unchanged. Caller cancellation
/// always propagates — it is never converted into a result, on any path.
/// </para>
/// </remarks>
public sealed class ComposerRuntimeFacade : IComposerFacade
{
    /// <summary>
    /// The fixed, sanitized message for a runtime that could not be created or connected. It names no
    /// exception text, model, provider, account or credential — only what the caller can act on.
    /// </summary>
    internal const string RuntimeUnavailableError =
        "The Composer runtime for this account is not available right now. Retry the request; if it "
        + "persists, an administrator should check the account's configured model and provider access.";

    /// <summary>
    /// The fixed, actionable message for a mutation on a runtime whose connect is still deferred (no
    /// usable provider credential yet). It states what the caller can do instead of exposing the gate's
    /// internals.
    /// </summary>
    internal const string RuntimeDeferredError =
        "The Composer is not connected yet: no usable credential for the selected model is available for "
        + "this account. Make sure the account has access to that model (or select a model the account can "
        + "use) and retry the operation.";

    /// <summary>
    /// The fixed retry/requery message for a runtime that was removed while the request was in flight.
    /// The handle is stale; a retry obtains a fresh runtime from the manager.
    /// </summary>
    internal const string RuntimeRemovedError =
        "The Composer runtime for this account was removed while the request was in flight, so the request "
        + "was not applied. Retry it: a fresh runtime is created on the next request.";

    private readonly IComposerRuntimeAccessor _accessor;
    private readonly ComposerFacade _legacy;
    private readonly ILogger<ComposerRuntimeFacade> _log;

    /// <summary>Initialises the recovery facade.</summary>
    /// <param name="accessor">The SCOPED runtime accessor of the calling scope (never captured by a singleton).</param>
    /// <param name="legacy">
    /// The LEGACY facade. It supplies the ONE operation shaping both implementations share, and it IS the
    /// explicit open-mode fallback (the legacy singleton Composer).
    /// </param>
    /// <param name="log">Logger for the secret-free status diagnostics.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ComposerRuntimeFacade(
        IComposerRuntimeAccessor accessor, ComposerFacade legacy, ILogger<ComposerRuntimeFacade> log)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public Task<FacadeResult<CurrentModelDto>> GetCurrentModelAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct) =>
        ExecuteAsync(
            actingUser,
            nameof(GetCurrentModelAsync),
            isMutation: false,
            composer => Task.FromResult(_legacy.ReadCurrentModel(composer)),
            () => _legacy.GetCurrentModelAsync(actingUser, ct),
            ct);

    /// <inheritdoc />
    public Task<FacadeResult<ComposerModelsDto>> GetModelsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct) =>
        ExecuteAsync(
            actingUser,
            nameof(GetModelsAsync),
            isMutation: false,
            composer => Task.FromResult(_legacy.ReadModels(composer)),
            () => _legacy.GetModelsAsync(actingUser, ct),
            ct);

    /// <inheritdoc />
    public Task<FacadeResult<SwitchResultDto>> SwitchModelAsync(
        ClaimsPrincipal? actingUser, string? model, string? reasoning, CancellationToken ct) =>
        ExecuteAsync(
            actingUser,
            nameof(SwitchModelAsync),
            // A switch is a MUTATION: on a deferred runtime it first drives the operation's connect, and
            // on a model-less shell it proceeds so the actor's first selection may connect.
            isMutation: true,
            composer => _legacy.SwitchModelAsync(composer, model, reasoning, ct),
            () => _legacy.SwitchModelAsync(actingUser, model, reasoning, ct),
            ct);

    /// <inheritdoc />
    public Task<FacadeResult<CompactResultDto>> CompactAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
        ExecuteAsync(
            actingUser,
            nameof(CompactAsync),
            isMutation: true,
            composer => _legacy.CompactAsync(composer, ct),
            () => _legacy.CompactAsync(actingUser, ct),
            ct);

    /// <inheritdoc />
    public Task<FacadeResult<CompactResultDto>> CompactPartialAsync(
        ClaimsPrincipal? actingUser, int percent, CancellationToken ct) =>
        ExecuteAsync(
            actingUser,
            nameof(CompactPartialAsync),
            isMutation: true,
            composer => _legacy.CompactPartialAsync(composer, percent, ct),
            () => _legacy.CompactPartialAsync(actingUser, percent, ct),
            ct);

    /// <summary>
    /// Runs ONE operation against the runtime selected for the caller, or returns the recovery answer
    /// that selection requires.
    /// </summary>
    /// <typeparam name="T">The operation's value type.</typeparam>
    /// <param name="actingUser">The request's authenticated principal, or <c>null</c> when signed out.</param>
    /// <param name="operation">The operation name (log context only).</param>
    /// <param name="isMutation">Whether the operation mutates (and may therefore drive a deferred connect).</param>
    /// <param name="onRuntime">Runs the operation against the selected runtime's Composer.</param>
    /// <param name="onOpenModeLegacy">Runs the operation on the LEGACY facade — the open-mode fallback ONLY.</param>
    /// <param name="ct">Caller cancellation; it propagates and is never mapped to a result.</param>
    /// <returns>The operation's result, or the mapped recovery failure.</returns>
    private async Task<FacadeResult<T>> ExecuteAsync<T>(
        ClaimsPrincipal? actingUser,
        string operation,
        bool isMutation,
        Func<Composer, Task<FacadeResult<T>>> onRuntime,
        Func<Task<FacadeResult<T>>> onOpenModeLegacy,
        CancellationToken ct)
    {
        var resolution = await ResolveAsync(actingUser, operation, isMutation, onOpenModeLegacy, ct)
            .ConfigureAwait(false);

        // A resolution either answers the operation outright (denial, recovery failure, or the explicit
        // open-mode legacy result) or selects the runtime to run it on.
        return resolution.Result
            ?? await onRuntime(resolution.Runtime!.Composer).ConfigureAwait(false);
    }

    /// <summary>
    /// The per-operation runtime selection: re-resolves the caller through the accessor, then applies the
    /// exact status mapping described on this type. It performs at most ONE removal re-requery and ONE
    /// deferred connect attempt, both operation-driven, and never disposes anything (the manager is the
    /// sole owner of every runtime).
    /// </summary>
    private async Task<RuntimeResolution<T>> ResolveAsync<T>(
        ClaimsPrincipal? actingUser,
        string operation,
        bool isMutation,
        Func<Task<FacadeResult<T>>> onOpenModeLegacy,
        CancellationToken ct)
    {
        var access = await _accessor.AcquireForRequestAsync(actingUser, ct).ConfigureAwait(false);
        var requeryUsed = false;
        var connectUsed = false;

        while (true)
        {
            if (!access.IsAuthorized)
            {
                // A Viewer (or an unresolved identity) never reaches the manager: no runtime, no content.
                LogStatus(operation, "the acting account is not authorized to use the Composer", warning: false);
                return RuntimeResolution<T>.Answered(Forbidden<T>());
            }

            var outcome = access.Result!;
            switch (outcome.Status)
            {
                case ComposerRuntimeStatus.Refused:
                    if (IsOpenModeLocalOperatorRefusal(outcome))
                    {
                        // EXPLICIT open-mode fallback: the synthetic local operator has no persisted
                        // account, so the manager refuses it — and open mode IS the legacy installation.
                        return RuntimeResolution<T>.Answered(await onOpenModeLegacy().ConfigureAwait(false));
                    }

                    // An AUTHENTICATED refusal is final: it never falls back to the legacy Composer.
                    LogStatus(operation, $"the runtime manager refused ({outcome.Refusal})", warning: true);
                    return RuntimeResolution<T>.Answered(Forbidden<T>());

                case ComposerRuntimeStatus.Failed:
                    LogFailure(operation, outcome.Failure);
                    return RuntimeResolution<T>.Answered(
                        Failure<T>(RuntimeUnavailableError, FacadeErrorKind.ServiceUnavailable));

                case ComposerRuntimeStatus.Removed:
                    if (requeryUsed)
                    {
                        return RuntimeResolution<T>.Answered(
                            Failure<T>(RuntimeRemovedError, FacadeErrorKind.Conflict));
                    }

                    requeryUsed = true;
                    // The stale handle is NOT disposed here (only the manager disposes a runtime): the
                    // manager is asked again, which joins the old teardown and creates a fresh runtime.
                    access = await _accessor.AcquireForRequestAsync(actingUser, ct).ConfigureAwait(false);
                    continue;

                case ComposerRuntimeStatus.Deferred when isMutation:
                    if (connectUsed)
                    {
                        // Still deferred AFTER the operation's own connect attempt: an actionable,
                        // secret-free service answer rather than a silent no-op.
                        return RuntimeResolution<T>.Answered(
                            Failure<T>(RuntimeDeferredError, FacadeErrorKind.ServiceUnavailable));
                    }

                    connectUsed = true;
                    // Operation-driven Deferred→Connected: the accessor connects through the manager,
                    // which re-evaluates the CURRENT account, reuses a single shared connect, and never
                    // starts a second connect for a runtime that is already live.
                    access = await _accessor.ConnectForRequestAsync(actingUser, ct).ConfigureAwait(false);
                    continue;

                case ComposerRuntimeStatus.Deferred:
                case ComposerRuntimeStatus.DisconnectedShell:
                case ComposerRuntimeStatus.Connected:
                    // Reads never connect: a deferred/shell runtime answers with shell metadata, and a
                    // connected one is served as reported (the manager already reconciled it).
                    return RuntimeResolution<T>.Selected(outcome.Runtime!);

                default:
                    // No silent fallback for an undefined status: an unhandled value is a bug.
                    throw new InvalidOperationException($"Unexpected Composer runtime status: {outcome.Status}.");
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="outcome"/> is THE open-mode local-operator refusal — the single refusal
    /// that may fall back to the legacy singleton. Both halves are required: the explicit open/no-auth
    /// mode, and the manager's typed refusal of the synthetic account-less operator.
    /// </summary>
    private bool IsOpenModeLocalOperatorRefusal(ComposerRuntimeResult outcome) =>
        !_accessor.AuthEnabled && outcome.Refusal == ComposerRuntimeRefusal.LocalOperator;

    /// <summary>Builds the sanitized authorization answer. It carries no runtime and no content.</summary>
    internal static FacadeResult<T> Forbidden<T>() =>
        new(false, default, FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden);

    /// <summary>Builds a recovery failure result for <paramref name="kind"/>.</summary>
    private static FacadeResult<T> Failure<T>(string error, FacadeErrorKind kind) =>
        new(false, default, error, kind);

    /// <summary>Logs one status decision (secret-free: the operation and the typed status only).</summary>
    private void LogStatus(string operation, string detail, bool warning)
    {
        try
        {
            if (warning)
                _log.LogWarning("{Operation} could not select a Composer runtime: {Detail}.", operation, detail);
            else
                _log.LogInformation("{Operation} was refused: {Detail}.", operation, detail);
        }
        catch
        {
            // Diagnostics never change the outcome of an operation.
        }
    }

    /// <summary>Logs the runtime fault server-side (the caller only ever sees the sanitized message).</summary>
    private void LogFailure(string operation, Exception? failure)
    {
        try
        {
            _log.LogWarning(failure, "{Operation} failed: the Composer runtime is unavailable.", operation);
        }
        catch
        {
            // Diagnostics never change the outcome of an operation.
        }
    }

    /// <summary>
    /// Either the runtime an operation must run on, or the result that answers it instead. Exactly one
    /// member is non-null.
    /// </summary>
    private sealed record RuntimeResolution<T>(ComposerUserRuntime? Runtime, FacadeResult<T>? Result)
    {
        /// <summary>The selected live/shell/deferred runtime.</summary>
        public static RuntimeResolution<T> Selected(ComposerUserRuntime runtime) =>
            new(runtime ?? throw new ArgumentNullException(nameof(runtime)), Result: null);

        /// <summary>The result that answers the operation without a runtime.</summary>
        public static RuntimeResolution<T> Answered(FacadeResult<T> result) =>
            new(Runtime: null, result ?? throw new ArgumentNullException(nameof(result)));
    }
}
