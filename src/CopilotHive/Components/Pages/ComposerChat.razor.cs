using System.Security.Claims;

using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Components.Authorization;

namespace CopilotHive.Components.Pages;

/// <summary>
/// THE CIRCUIT HALF of the Composer page: it binds THIS circuit to the per-user
/// <see cref="ComposerUserRuntime"/> the singleton <see cref="ComposerRuntimeManager"/> owns, through the
/// SCOPED <see cref="IComposerRuntimeAccessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// ONE SELECTION PER OPERATION, ALWAYS FRESH: every operation calls
/// <c>AcquireForCircuitAsync</c> again, which re-resolves the current account and re-evaluates
/// <see cref="HiveCapability.InteractiveMember"/> BEFORE the manager is asked for anything. Nothing about
/// the previous answer is treated as live truth: a demotion, a disablement or a deleted account lands on
/// the very next operation. A cached <see cref="Orchestration.Composer"/> handle is only a RENDER
/// convenience, never an authorization decision.
/// </para>
/// <para>
/// THE MAPPING IS PURE AND TOTAL: <see cref="ClassifyRuntimeAccess"/> maps every manager outcome to one
/// <see cref="ComposerCircuitStatus"/>, and <see cref="ResolveBinding"/> turns that status into the exact
/// binding one render operates on. Both are side-effect free, so the recovery contract is asserted
/// directly instead of through a live circuit.
/// </para>
/// <para>
/// STALE AND DEFERRED HANDLES ARE HANDLED BY THE MANAGER, NOT HERE: at most ONE stale-handle re-query and
/// at most ONE operation-driven connect are performed, and neither ever disposes anything — the singleton
/// manager is the SOLE owner and disposer of every runtime. This page's disposal path releases only its
/// own event subscriptions and JS interop handles.
/// </para>
/// <para>
/// SUBSCRIPTIONS FOLLOW THE BINDING: adopting a (re)created runtime unsubscribes the previous Composer
/// first, so a stale runtime can never keep delivering events into this component's render.
/// </para>
/// </remarks>
public partial class ComposerChat
{
    /// <summary>
    /// WHAT THIS RENDER MAY SHOW for the circuit's last runtime selection. Every value except
    /// <see cref="Ready"/> is fail-closed: no connected controls, and for the denied and failed states no
    /// Composer, no history and no content at all.
    /// </summary>
    internal enum ComposerCircuitStatus
    {
        /// <summary>No runtime and no content: a Viewer, a signed-out caller or an authenticated refusal.</summary>
        Forbidden = 0,

        /// <summary>The caller's runtime exists but is not connected yet: it stays actionable, with no content.</summary>
        Deferred = 1,

        /// <summary>The runtime could not be created or connected: a fixed, secret-free error.</summary>
        Failed = 2,

        /// <summary>The handle was stale even after one re-query: a fixed retry message, no content.</summary>
        Removed = 3,

        /// <summary>A Composer is bound: the caller's own runtime, or the explicit open-mode legacy singleton.</summary>
        Ready = 4,
    }

    /// <summary>
    /// THE BINDING ONE RENDER OPERATES ON: the status the circuit must render, the Composer and the
    /// attachment service of the selected runtime (or of the legacy singleton in open mode), and whether
    /// that binding is the explicit open-mode legacy one.
    /// </summary>
    /// <param name="Status">The status this render must honour.</param>
    /// <param name="Composer">The Composer of the selection, or <c>null</c> when nothing may be shown.</param>
    /// <param name="Attachments">The attachment service of the same selection, or <c>null</c>.</param>
    /// <param name="IsOpenModeLegacy">Whether the binding is the open-mode legacy singleton (never a per-user runtime).</param>
    internal sealed record ComposerCircuitBinding(
        ComposerCircuitStatus Status,
        Composer? Composer,
        ComposerAttachmentService? Attachments,
        bool IsOpenModeLegacy)
    {
        /// <summary>
        /// Whether a Composer is bound at all (a connected runtime, a model-less shell, a deferred runtime
        /// or the open-mode legacy singleton). <c>false</c> means this render may show NO content.
        /// </summary>
        internal bool HasComposer => Composer is not null;

        /// <summary>Whether the bound Composer may run an operation that MUTATES the session.</summary>
        internal bool CanMutate => Status == ComposerCircuitStatus.Ready && Composer is not null;
    }

    /// <summary>
    /// THE ONE MAPPING from a FRESH <see cref="ComposerRuntimeAccess"/> to the circuit's render status.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is PURE: no accessor call, no I/O, no mutation. The single exception it encodes is the explicit
    /// OPEN-MODE fallback — the synthetic local operator has no persisted account, so the manager refuses
    /// it, and open mode IS the legacy single-operator installation. That fallback is gated on BOTH the
    /// open mode and the typed <see cref="ComposerRuntimeRefusal.LocalOperator"/>, so an AUTHENTICATED
    /// refusal can never reach the legacy Composer.
    /// </para>
    /// <para>
    /// A <see cref="ComposerRuntimeStatus.DisconnectedShell"/> is READY: it is a usable, model-less
    /// Composer whose actor's first-selection path may connect it.
    /// </para>
    /// </remarks>
    /// <param name="access">The accessor's fresh answer for the current circuit user.</param>
    /// <param name="authEnabled">Whether the host authentication mode is enabled (read from the accessor).</param>
    /// <returns>The status this render must honour.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="access"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The access carries no outcome, or an unexpected status.</exception>
    internal static ComposerCircuitStatus ClassifyRuntimeAccess(ComposerRuntimeAccess access, bool authEnabled)
    {
        ArgumentNullException.ThrowIfNull(access);

        if (!access.IsAuthorized)
            return ComposerCircuitStatus.Forbidden;

        var outcome = access.Result ?? throw new InvalidOperationException(
            "An authorized Composer runtime access must carry the manager's typed outcome.");

        return outcome.Status switch
        {
            // The runtime is live, or is a model-less shell whose actor's first selection may connect it:
            // both are served (reads answer with shell metadata, the switch proceeds against the shell).
            ComposerRuntimeStatus.Connected => ComposerCircuitStatus.Ready,
            ComposerRuntimeStatus.DisconnectedShell => ComposerCircuitStatus.Ready,

            // Not connected yet: no content, but the caller can act (the page offers the ONE operation-driven
            // connect and every mutation attempts it).
            ComposerRuntimeStatus.Deferred => ComposerCircuitStatus.Deferred,

            // A faulted runtime has already been disposed and dropped by the manager: a fixed, secret-free
            // error is all that may be shown.
            ComposerRuntimeStatus.Failed => ComposerCircuitStatus.Failed,

            // The handle was stale. The caller re-queries ONCE; a second Removed is this status.
            ComposerRuntimeStatus.Removed => ComposerCircuitStatus.Removed,

            // ONLY the open-mode local-operator refusal may reach the legacy Composer (see the type remarks).
            ComposerRuntimeStatus.Refused when IsOpenModeLocalOperatorRefusal(access, authEnabled) =>
                ComposerCircuitStatus.Ready,

            // Any other refusal — including every AUTHENTICATED refusal — hides the content.
            ComposerRuntimeStatus.Refused => ComposerCircuitStatus.Forbidden,

            // No silent fallback for an undefined status: an unhandled value is a bug.
            _ => throw new InvalidOperationException($"Unexpected Composer runtime status: {outcome.Status}."),
        };
    }

    /// <summary>
    /// Whether <paramref name="access"/> is THE open-mode local-operator refusal — the single answer that
    /// may be served by the legacy singleton. Both halves are required: the explicit open/no-auth mode,
    /// and the manager's typed refusal of the synthetic account-less operator.
    /// </summary>
    /// <param name="access">The accessor's answer.</param>
    /// <param name="authEnabled">Whether the host authentication mode is enabled.</param>
    /// <returns><c>true</c> only for that one combination.</returns>
    internal static bool IsOpenModeLocalOperatorRefusal(ComposerRuntimeAccess access, bool authEnabled)
    {
        ArgumentNullException.ThrowIfNull(access);

        return !authEnabled
            && access.IsAuthorized
            && access.Result?.Refusal == ComposerRuntimeRefusal.LocalOperator;
    }

    /// <summary>
    /// THE ONE BINDING RESOLUTION: turns one fresh <see cref="ComposerRuntimeAccess"/> into the exact
    /// Composer/attachments this render may use, or into the fail-closed status that forbids any content.
    /// </summary>
    /// <param name="access">The accessor's fresh answer.</param>
    /// <param name="authEnabled">Whether the host authentication mode is enabled.</param>
    /// <param name="legacyComposer">The legacy singleton Composer (the open-mode binding).</param>
    /// <param name="legacyAttachments">The legacy attachment service (the open-mode binding).</param>
    /// <returns>The binding this render must honour.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">A ready status carried neither a runtime nor the open-mode refusal.</exception>
    internal static ComposerCircuitBinding ResolveBinding(
        ComposerRuntimeAccess access,
        bool authEnabled,
        Composer legacyComposer,
        ComposerAttachmentService legacyAttachments)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(legacyComposer);
        ArgumentNullException.ThrowIfNull(legacyAttachments);

        var status = ClassifyRuntimeAccess(access, authEnabled);

        return status switch
        {
            // The caller's OWN runtime: its Composer AND its per-user attachment store travel together, so no
            // attachment operation can ever be redirected into another account's directory.
            _ when access.Runtime is { } runtime =>
                new ComposerCircuitBinding(status, runtime.Composer, runtime.Attachments, IsOpenModeLegacy: false),

            // The explicit open-mode fallback: the legacy single-operator installation.
            ComposerCircuitStatus.Ready when IsOpenModeLocalOperatorRefusal(access, authEnabled) =>
                new ComposerCircuitBinding(status, legacyComposer, legacyAttachments, IsOpenModeLegacy: true),

            // A ready status with neither a runtime nor the open-mode refusal is a wiring bug, never a reason to
            // substitute *some* Composer (which would be a silent fallback onto the legacy singleton).
            ComposerCircuitStatus.Ready => throw new InvalidOperationException(
                "A ready Composer runtime access must carry a runtime, or be the explicit open-mode local-operator refusal."),

            // Every non-ready status binds NOTHING: no Composer, no attachments, no content.
            _ => new ComposerCircuitBinding(status, Composer: null, Attachments: null, IsOpenModeLegacy: false),
        };
    }

    /// <summary>
    /// The circuit's LAST runtime selection, or <c>null</c> before the first one. It is a RENDER source only:
    /// no operation reads it as authorization truth (every operation re-queries the accessor).
    /// </summary>
    private ComposerCircuitBinding? _circuit;

    /// <summary>The fixed, secret-free error shown for a failed or removed runtime selection.</summary>
    private string? _circuitError;

    /// <summary>The Composer this component is CURRENTLY subscribed to (never owned, never disposed here).</summary>
    private Composer? _subscribedComposer;

    /// <summary>
    /// The Composer of the CIRCUIT's current runtime selection (the caller's own runtime in authenticated
    /// mode, the legacy singleton in open mode), or <c>null</c> when the selection may show no content.
    /// </summary>
    private Composer? BoundComposer => _circuit?.Composer;

    /// <summary>
    /// The Composer this render operates on. It is only read by markup whose view state requires a binding,
    /// so an unbound render reaching it is a wiring bug and fails loudly instead of silently substituting a
    /// Composer (which could only be the legacy singleton).
    /// </summary>
    private Composer Composer => BoundComposer ?? throw new InvalidOperationException(
        "No Composer runtime is bound to this render: the circuit's forbidden/deferred/error view must be rendered instead.");

    /// <summary>The attachment service of the SAME selection (per-user in authenticated mode, legacy in open mode).</summary>
    private ComposerAttachmentService? BoundAttachments => _circuit?.Attachments;

    /// <summary>The current circuit status; <see cref="ComposerCircuitStatus.Forbidden"/> until the first selection.</summary>
    private ComposerCircuitStatus CircuitStatus => _circuit?.Status ?? ComposerCircuitStatus.Forbidden;

    /// <summary>Whether a Composer is bound (so its metadata may be read).</summary>
    private bool HasBoundComposer => _circuit?.HasComposer ?? false;

    /// <summary>
    /// THE BINDING THIS RENDER DISPLAYS, for capture at the moment a callback or asynchronous completion is
    /// QUEUED: the captured value is the freshness token that the completion re-checks when it finally runs.
    /// It is never used as authorization truth (every completion that acts re-queries the accessor).
    /// </summary>
    internal ComposerCircuitBinding? CurrentCallbackBinding => _circuit;

    /// <summary>
    /// THE PER-OPERATION RUNTIME SELECTION: re-queries the accessor (a FRESH identity re-resolution and
    /// <see cref="HiveCapability.InteractiveMember"/> evaluation), performs at most ONE stale-handle
    /// re-query and at most ONE operation-driven deferred connect, then BINDS whatever it selected — but ONLY while
    /// no newer selection superseded the binding this operation started under.
    /// </summary>
    /// <param name="mutation">Whether the operation mutates (and may therefore drive the deferred connect).</param>
    /// <param name="ct">Caller cancellation; cancellation propagates.</param>
    /// <param name="adopted">Optional out-signal: set to whether THIS answer was adopted (false when superseded).</param>
    /// <returns>The status the operation must honour.</returns>
    private async Task<ComposerCircuitStatus> AcquireCircuitRuntimeAsync(
        bool mutation, CancellationToken ct, System.Runtime.CompilerServices.StrongBox<bool>? adopted = null)
    {
        // OWNERSHIP, the same pattern RevalidateCompletionAsync proves for callbacks. CAPTURED BEFORE THE FIRST
        // AWAIT: the selection this operation started under.
        var started = _circuit;

        // THE CIRCUIT IDENTITY, ONCE: the cascade is absent in open mode, where the shared helper fabricates
        // nothing and the accessor answers from the explicit open-mode decision. Initialising it here also keeps
        // the ONE identity acquisition on the production helper (the accessor re-resolves on every operation).
        _ = await ActingUserAsync();

        var (access, status) = await SelectRuntimeAsync(
            ComposerRuntimeAccessor, AuthenticationState, mutation, ct);

        // THE CONDITION, BEFORE ANY SIDE EFFECT: a newer selection replaced the binding while this answer was in
        // flight, so this (older) answer is not adopted at all — it cannot overwrite the newer binding, reinstall
        // subscriptions or let a storage write start on a stale authorization. The caller learns it through
        // the adopted signal and receives the CURRENT binding's status.
        if (!ReferenceEquals(started, _circuit))
        {
            if (adopted is not null) adopted.Value = false;
            return CircuitStatus;
        }

        Bind(ResolveBinding(access, ComposerRuntimeAccessor.AuthEnabled, LegacyComposer, LegacyAttachments));
        if (adopted is not null) adopted.Value = true;
        return status;
    }

    /// <summary>
    /// THE ASYNC-COMPLETION RE-ENTRY: re-runs the whole selection for a queued subscription callback or an
    /// asynchronous upload/paste completion, then reports whether that work may still be applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is the SAME selection an ordinary operation performs — fresh identity resolution, fresh
    /// <see cref="HiveCapability.InteractiveMember"/> evaluation, at most one stale-handle re-query — so a
    /// demotion, a disablement or a removed/recreated runtime is observed the moment the completion runs. Nothing
    /// is polled and no timer is involved: the completion asks once, at the only moment that matters.
    /// </para>
    /// <para>
    /// ADOPTION IS CONDITIONAL, AND THE CONDITION IS EVALUATED FIRST. The re-query is awaited, and while it was in
    /// flight a NEWER operation may already have applied its own (possibly fail-closed) selection. The re-queried
    /// answer is therefore adopted ONLY while <paramref name="captured"/> is still the circuit's current binding —
    /// checked immediately after the await and BEFORE any binding, subscription or content side effect. A callback
    /// that lost that race is dropped with the newer binding and its subscriptions completely untouched: it can
    /// never overwrite a newer refusal or re-install subscriptions.
    /// </para>
    /// </remarks>
    /// <param name="captured">The binding the completion was queued/started under.</param>
    /// <param name="ct">Caller cancellation; cancellation propagates.</param>
    /// <returns><c>true</c> only when the completion may still be applied to the CURRENT binding.</returns>
    private async Task<bool> RevalidateCompletionAsync(ComposerCircuitBinding captured, CancellationToken ct)
    {
        // The same single identity acquisition every selection performs (idempotent; reads no state of the binding).
        _ = await ActingUserAsync();

        // PURE selection: no binding, subscription or content side effect happens inside it.
        var (access, status) = await SelectRuntimeAsync(
            ComposerRuntimeAccessor, AuthenticationState, mutation: false, ct);

        // THE CONDITION, BEFORE ANY SIDE EFFECT: a newer selection replaced the binding while the re-query was in
        // flight, so this (older) answer is stale and is not adopted at all.
        if (!IsCurrentCallbackBinding(captured))
            return false;

        Bind(ResolveBinding(access, ComposerRuntimeAccessor.AuthEnabled, LegacyComposer, LegacyAttachments));

        // Adopting the SAME selection preserves the record; adopting a different one replaced it, which is what
        // makes the callback stale.
        return status is ComposerCircuitStatus.Ready or ComposerCircuitStatus.Deferred
            && HasBoundComposer
            && IsCurrentCallbackBinding(captured);
    }

    /// <summary>
    /// THE O(1) BINDING-IDENTITY CHECK: whether the binding a callback/completion captured is still the binding
    /// this circuit displays. It is one half of the freshness guard — see
    /// <see cref="DropStaleCallbackAsync"/>, which adds the authorization re-check that this cheap comparison
    /// cannot see.
    /// </summary>
    /// <remarks>
    /// The binding record is REPLACED whenever the selection changes (a different runtime, or a fail-closed
    /// status), and it is deliberately PRESERVED when an operation re-selects the very same runtime — so a
    /// reference comparison is exactly "is this callback still about the runtime I am showing". A callback
    /// queued before a runtime replacement therefore fails this check without any I/O, which is what stops it
    /// from reattaching the component to the removed/recreated runtime.
    /// </remarks>
    /// <param name="captured">The binding captured when the callback was queued.</param>
    /// <returns><c>true</c> only when <paramref name="captured"/> is the circuit's current binding.</returns>
    internal bool IsCurrentCallbackBinding(ComposerCircuitBinding? captured) =>
        captured is not null && ReferenceEquals(captured, _circuit);

    /// <summary>
    /// THE FULL FRESHNESS GUARD for a callback that would RESTORE CONTENT or COMMIT an asynchronous write:
    /// the binding-identity check, plus ONE fresh authorization re-check through the accessor.
    /// </summary>
    /// <param name="captured">The binding captured when the completion started.</param>
    /// <param name="ct">Caller cancellation; cancellation propagates.</param>
    /// <returns><c>true</c> only when the completion may still be applied.</returns>
    internal async Task<bool> IsCompletionStillAuthorizedAsync(ComposerCircuitBinding? captured, CancellationToken ct)
    {
        if (!IsCurrentCallbackBinding(captured))
        {
            // The runtime was replaced (or the selection became fail-closed) while the work was in flight: the
            // old work belongs to a binding this circuit no longer shows. It is DROPPED — never re-attached.
            return false;
        }

        return await RevalidateCompletionAsync(captured!, ct);
    }

    /// <summary>
    /// The most recently queued subscription-callback body. Production never awaits it; a test awaits it to
    /// observe when a queued callback has FINISHED — deterministically, without polling and without a timer.
    /// </summary>
    internal Task LastCallbackCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// How many queued subscription-callback bodies were APPLIED (i.e. passed the freshness guard and really ran
    /// their work). A callback dropped as stale is counted in <see cref="DroppedCallbackCount"/> instead. This is
    /// a monotonic observation counter, never a decision input.
    /// </summary>
    internal int AppliedCallbackCount { get; private set; }

    /// <summary>How many queued subscription-callback bodies were DROPPED as stale.</summary>
    internal int DroppedCallbackCount { get; private set; }

    /// <summary>
    /// Test observation seam, inert (<c>null</c>) in production: wraps every queued subscription-callback body so a
    /// test can hold it PENDING and release it deterministically — no polling, no timer and no dispatcher trick.
    /// Production never sets it, so the body runs immediately.
    /// </summary>
    internal Func<Func<Task>, Task>? CallbackGate { get; set; }

    /// <summary>
    /// Queues one subscription-callback body on the renderer's dispatcher and records the returned task in
    /// <see cref="LastCallbackCompletion"/>.
    /// </summary>
    /// <param name="body">The callback body.</param>
    private void QueueCallback(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var gate = CallbackGate;
        LastCallbackCompletion = gate is null
            ? InvokeAsync(body)
            : InvokeAsync(() => gate(body));
    }

    /// <summary>
    /// The ONE early-exit every queued subscription callback uses: reports whether the callback is STALE and must
    /// stop, and records the decision for observation.
    /// </summary>
    /// <remarks>
    /// It performs BOTH freshness checks, because either half alone is insufficient:
    /// <list type="bullet">
    /// <item>the BINDING IDENTITY check is O(1) and catches a runtime that was removed/recreated (the binding
    /// record is replaced) or a selection that became fail-closed;</item>
    /// <item>the AUTHORIZATION re-check catches a demotion or a disablement that landed in the directory while the
    /// callback was pending. The binding record cannot see that: no operation has run yet to replace it.</item>
    /// </list>
    /// The authorization check is one resolution through the scoped accessor — the same indexed directory read
    /// every facade operation performs — so a callback can never act on authority the caller no longer holds.
    /// </remarks>
    /// <param name="captured">The binding captured when the callback was queued.</param>
    /// <param name="ct">Caller cancellation; cancellation propagates.</param>
    /// <returns><c>true</c> when the callback is stale and the body must return immediately.</returns>
    private async Task<bool> DropStaleCallbackAsync(ComposerCircuitBinding? captured, CancellationToken ct)
    {
        if (await IsCompletionStillAuthorizedAsync(captured, ct))
        {
            AppliedCallbackCount++;
            return false;
        }

        DroppedCallbackCount++;

        // THE FAIL-CLOSED RENDER IS INDEPENDENT OF THE CALLBACK BODY: the guard's own re-selection may just have
        // transitioned this page to a forbidden/failed state (or a different runtime), and subscription work does
        // not get the automatic UI-event render — so the page is re-rendered (and its JS paste listener released)
        // here, whether or not the stale body is applied.
        await RefreshAfterSelectionChangeAsync();
        return true;
    }

    /// <summary>
    /// Re-renders the page and re-syncs the paste listener after a selection change that no UI event is going to
    /// render for (a subscription callback or an asynchronous completion). Never throws: a JS fault must not turn
    /// a fail-closed transition into an unhandled callback fault.
    /// </summary>
    private async Task RefreshAfterSelectionChangeAsync()
    {
        StateHasChanged();

        try
        {
            await SyncPasteAllowedAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ComposerChat] Paste state sync after a selection change failed: {ex.Message}");
        }
    }

    /// <summary>
    /// THE OPERATION ALGORITHM, pure in its inputs: one FRESH acquisition, at most ONE stale-handle re-query and
    /// at most ONE operation-driven deferred connect — in that order, each only when the previous answer requires
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NO SECOND STATE MACHINE: it never inspects <c>IsConnected</c>, never reconciles a status of its own and
    /// never starts a second connect — every question is answered by the manager through the accessor, and the
    /// manager's single shared connect is the only connect there is.
    /// </para>
    /// <para>
    /// ONE ATTEMPT EACH, AND ONLY ON DEMAND: a read never connects (a deferred read answers with shell metadata),
    /// and a mutation on an already-deferred runtime drives exactly one connect. A second <c>Removed</c> or a
    /// still-deferred answer is reported, never retried in a loop.
    /// </para>
    /// </remarks>
    /// <param name="accessor">The scoped circuit accessor.</param>
    /// <param name="authenticationState">The optional cascading authentication state (absent in open mode).</param>
    /// <param name="mutation">Whether the operation mutates.</param>
    /// <param name="ct">Caller cancellation; cancellation propagates.</param>
    /// <returns>The access the caller must act on, together with its classification.</returns>
    internal static async Task<(ComposerRuntimeAccess Access, ComposerCircuitStatus Status)> SelectRuntimeAsync(
        IComposerRuntimeAccessor accessor,
        Task<AuthenticationState>? authenticationState,
        bool mutation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        var access = await accessor.AcquireForCircuitAsync(authenticationState, ct).ConfigureAwait(false);
        var status = ClassifyRuntimeAccess(access, accessor.AuthEnabled);

        if (status == ComposerCircuitStatus.Removed)
        {
            // A stale handle is NOT disposed here (the manager is its only owner): the manager is asked ONCE
            // more, which joins the old teardown and creates a fresh runtime for this same account.
            access = await accessor.AcquireForCircuitAsync(authenticationState, ct).ConfigureAwait(false);
            status = ClassifyRuntimeAccess(access, accessor.AuthEnabled);
        }

        if (mutation && status == ComposerCircuitStatus.Deferred)
        {
            // The runtime's OWN awaitable connect — exactly ONE attempt per operation, through the manager's
            // single shared connect. A still-deferred outcome leaves the page actionable instead of silently
            // pretending the mutation ran.
            access = await accessor.ConnectForCircuitAsync(authenticationState, ct).ConfigureAwait(false);
            status = ClassifyRuntimeAccess(access, accessor.AuthEnabled);
        }

        return (access, status);
    }

    /// <summary>
    /// Adopts one binding for this render: drops the previous subscription (a stale runtime must never keep
    /// delivering events here), subscribes the newly selected Composer, and clears every piece of displayed
    /// content when the selection forbids content.
    /// </summary>
    /// <remarks>
    /// THE BINDING RECORD IS PRESERVED WHEN THE SELECTION IS UNCHANGED: an operation that re-selects the very
    /// same runtime keeps the SAME record, so a queued subscription callback captured before that operation still
    /// passes the freshness guard. A different runtime (or a fail-closed status) REPLACES the record, which is
    /// exactly what makes every callback queued against the old one stale.
    /// </remarks>
    /// <param name="binding">The binding the selection produced.</param>
    private void Bind(ComposerCircuitBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var previous = BoundComposer;
        var unchanged = _circuit is { } current && IsUnchangedSelection(current, binding);
        if (!unchanged)
            _circuit = binding;

        _circuitError = binding.Status switch
        {
            ComposerCircuitStatus.Failed => ComposerRuntimeFacade.RuntimeUnavailableError,
            ComposerCircuitStatus.Removed => ComposerRuntimeFacade.RuntimeRemovedError,
            _ => null,
        };

        if (!binding.HasComposer)
        {
            // NO CONTENT: the forbidden and failed states must not leave a previous account's thread,
            // catalog or selection visible.
            UnsubscribeFromBoundComposer();
            ClearDisplayedContent();
            return;
        }

        SubscribeToBoundComposer();

        // A (re)created runtime replaces the stale handle: the displayed thread is re-read from the runtime
        // that is authoritative NOW, never from the one that was removed.
        if (!ReferenceEquals(previous, binding.Composer))
            ClearDisplayedContent();
    }

    /// <summary>
    /// Whether <paramref name="next"/> selects EXACTLY what <paramref name="current"/> already selects — the same
    /// status and the same runtime Composer (reference equality). It is the criterion the binding record is
    /// preserved on, and therefore the criterion a queued callback's freshness guard depends on.
    /// </summary>
    /// <param name="current">The circuit's current binding.</param>
    /// <param name="next">The binding the new selection produced.</param>
    /// <returns><c>true</c> when the selection did not change.</returns>
    internal static bool IsUnchangedSelection(ComposerCircuitBinding current, ComposerCircuitBinding next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);

        return current.Status == next.Status
            && ReferenceEquals(current.Composer, next.Composer)
            && ReferenceEquals(current.Attachments, next.Attachments)
            && current.IsOpenModeLegacy == next.IsOpenModeLegacy;
    }

    /// <summary>
    /// Runs the selection for ONE mutating operation and returns the Composer to mutate, or <c>null</c> when
    /// the mutation must not happen (denied, still deferred, failed or removed).
    /// </summary>
    /// <param name="ct">Caller cancellation; cancellation propagates.</param>
    /// <returns>The Composer to mutate, or <c>null</c>.</returns>
    private async Task<Composer?> AcquireComposerForMutationAsync(CancellationToken ct)
    {
        var status = await AcquireCircuitRuntimeAsync(mutation: true, ct);
        return status == ComposerCircuitStatus.Ready ? BoundComposer : null;
    }

    /// <summary>
    /// ADOPTS THE CURRENT SELECTION AFTER A FACADE OPERATION. The facade re-selects the runtime for its own
    /// operation (fresh identity and capability, ONE deferred connect on a mutation, typed 403/409/503 answers) but
    /// it cannot touch this page's binding. Without this step a refusal would leave the page Ready and
    /// subscribed, and a runtime replacement would leave the page showing — and listening to — a runtime the
    /// facade did not act on.
    /// </summary>
    /// <remarks>
    /// It is a READ selection (<c>mutation: false</c>): the facade already drove the operation's single deferred
    /// connect, so this adds no second connect attempt and preserves the typed recovery contract. A refusal hides
    /// the content and unsubscribes; a different runtime unifies the binding, the catalog/history reads and the
    /// subscriptions with the runtime the facade acted on. It never disposes anything.
    /// </remarks>
    private async Task AdoptCurrentSelectionAsync()
    {
        var previous = BoundComposer;

        try
        {
            _ = await AcquireCircuitRuntimeAsync(mutation: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ComposerChat] Composer runtime selection failed: {ex.Message}");
            return;
        }

        // A DIFFERENT runtime is now bound: re-read what this page displays from IT (never from the one the page
        // used to show). The reads are plain facade reads — they adopt nothing, so there is no recursion.
        if (HasBoundComposer && !ReferenceEquals(previous, BoundComposer))
        {
            await LoadAvailableModelsAsync();
            if (HasBoundComposer && Composer.IsConnected)
            {
                LoadHistory();
                await LoadCurrentModelAsync();
            }
        }

        // RESYNCHRONIZE THE PASTE MIRROR: adoption can change what paste may do without removing the textarea (a
        // replacement clears the staged attachment; a refusal closes the page). The surviving listener's last
        // mirrored value must follow the CURRENT state — never the one it was last told. No install happens here:
        // the existing listener is only re-told, so there is no duplicate listener and no runtime is touched.
        try
        {
            await SyncPasteAllowedAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ComposerChat] Paste state sync after adoption failed: {ex.Message}");
        }
    }

    /// <summary>
    /// AUTHORIZES AN ATTACHMENT WRITE BEFORE IT STARTS: one FRESH selection through the accessor, and the write may
    /// only begin when the circuit is READY. Returns the attachment store of the runtime that selection bound, or
    /// <c>null</c> when the write must not start at all (denied, refused, failed, removed or still deferred).
    /// </summary>
    /// <remarks>
    /// This is the START gate; the completion guard is a separate, additional requirement. A write begun after
    /// access was revoked must never reach the store, while a write legitimately started before a later
    /// revocation keeps its file in the store that accepted it (the accepted boundary).
    /// </remarks>
    /// <returns>The store to write into, or <c>null</c> when no write may start.</returns>
    private async Task<ComposerAttachmentService?> AuthorizeAttachmentWriteAsync()
    {
        // A SUPERSEDED answer never starts a write: the newer selection that replaced it did not authorize THIS
        // write, and the older answer was dropped untouched, so nothing may begin on it.
        var adopted = new System.Runtime.CompilerServices.StrongBox<bool>();
        var status = await AcquireCircuitRuntimeAsync(mutation: false, CancellationToken.None, adopted);
        return adopted.Value && status == ComposerCircuitStatus.Ready ? BoundAttachments : null;
    }

    /// <summary>
    /// THE OPERATION-DRIVEN CONNECT behind the deferred banner: exactly one connect attempt through the
    /// accessor, then the ordinary refresh when it settled connected.
    /// </summary>
    /// <returns>A task that completes when the page has re-rendered.</returns>
    private async Task ConnectRuntimeAsync()
    {
        try
        {
            var status = await AcquireCircuitRuntimeAsync(mutation: true, CancellationToken.None);
            if (status == ComposerCircuitStatus.Ready && HasBoundComposer && Composer.IsConnected)
            {
                LoadHistory();
                await LoadCurrentModelAsync();
            }
        }
        catch (Exception ex)
        {
            _circuitError = ComposerRuntimeFacade.RuntimeUnavailableError;
            Console.Error.WriteLine($"[ComposerChat] Composer runtime connect failed: {ex.Message}");
        }

        StateHasChanged();
    }

    /// <summary>Drops every displayed piece of the previously bound account's content.</summary>
    private void ClearDisplayedContent()
    {
        _messages.Clear();
        _availableModels.Clear();
        _selectedModel = "";

        // The staged attachment belongs to the previous selection's store: keeping it would address a file
        // through a binding it was never saved into. Dropping it only forgets the record — it never deletes the
        // file of the runtime that is still using it.
        _pending = null;
    }

    /// <summary>
    /// Subscribes THIS component to the currently bound Composer's events. Idempotent: re-binding the same
    /// Composer installs nothing twice.
    /// </summary>
    private void SubscribeToBoundComposer()
    {
        if (BoundComposer is not { } composer || ReferenceEquals(_subscribedComposer, composer))
            return;

        UnsubscribeFromBoundComposer();

        composer.OnStreamingUpdate += HandleStreamingUpdate;
        composer.OnQuestionAsked += HandleQuestionAsked;
        composer.OnCompactingStarted += HandleCompactingStarted;
        composer.OnCompacted += HandleCompacted;
        composer.OnSubAgentChanged += HandleSubAgentChanged;
        composer.OnActiveInjection += HandleActiveInjection;
        _subscribedComposer = composer;
    }

    /// <summary>
    /// Unsubscribes this component from the Composer it is CURRENTLY subscribed to. It never disposes that
    /// Composer: the singleton <see cref="ComposerRuntimeManager"/> is the sole owner and disposer of every
    /// runtime, so a circuit teardown can never tear a runtime down while another request still uses it.
    /// </summary>
    private void UnsubscribeFromBoundComposer()
    {
        if (_subscribedComposer is not { } composer)
            return;

        composer.OnStreamingUpdate -= HandleStreamingUpdate;
        composer.OnQuestionAsked -= HandleQuestionAsked;
        composer.OnCompactingStarted -= HandleCompactingStarted;
        composer.OnCompacted -= HandleCompacted;
        composer.OnSubAgentChanged -= HandleSubAgentChanged;
        composer.OnActiveInjection -= HandleActiveInjection;
        _subscribedComposer = null;
    }
}
