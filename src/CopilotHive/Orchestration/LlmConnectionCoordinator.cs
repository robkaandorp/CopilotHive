using Microsoft.Extensions.Logging;

namespace CopilotHive.Orchestration;

/// <summary>
/// Owns the Composer's startup LLM connection: gating, deferral, wake-up on token commit,
/// dedup, cancellation on application shutdown and disposal.
/// <para>
/// The fresh-OAuth-install bug it fixes: on a brand-new install the operator has not signed in
/// yet, so no GitHub Copilot token exists when the host boots. The old startup code called
/// <c>Composer.ConnectAsync()</c> unconditionally, the connect threw
/// <c>"GH_TOKEN or GITHUB_TOKEN is required for copilot provider"</c>, the exception was logged
/// and swallowed — and nothing ever retried. The Composer stayed dead until a manual restart.
/// </para>
/// <para>
/// This coordinator DEFERS that startup connect (state <see cref="ComposerState.PendingConnect"/>)
/// when a Copilot-backed Composer has no token, and performs it the moment
/// <see cref="OnTokenAvailable"/> reports a committed non-whitespace token. It never implements
/// its own retry loop — it invokes the existing <c>Composer.ConnectAsync()</c> delegate handed to
/// it, at most once at a time.
/// </para>
/// </summary>
public sealed class LlmConnectionCoordinator : IAsyncDisposable
{
    /// <summary>Provider token produced by <c>ChatClientFactory.ParseProviderAndModel</c> for GitHub Copilot models.</summary>
    internal const string CopilotProvider = "copilot";

    /// <summary>Log message emitted immediately before a real startup connect attempt.</summary>
    internal const string ConnectingLog = "Connecting Composer…";

    /// <summary>Log message emitted after a successful connect.</summary>
    internal const string ConnectedLog = "Composer connected.";

    /// <summary>Log message emitted when a connect attempt failed.</summary>
    internal const string ConnectFailedLog = "Composer failed to connect — chat will be unavailable";

    /// <summary>Log message emitted for a model-less Composer shell.</summary>
    internal const string ShellLog = "Composer has no configured model — registered as a disconnected shell";

    /// <summary>Log message emitted when the startup connect is deferred until a token arrives.</summary>
    internal const string DeferredLog =
        "Composer startup connect deferred — no GitHub Copilot token available yet; will connect after sign-in";

    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task>? _connectAsync;
    private readonly Func<bool> _isTokenAvailable;
    private readonly Func<string, string> _providerOf;
    private readonly string? _primaryModel;
    private readonly string? _compactionModel;
    private readonly bool _oauthEnabled;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();

    private ComposerState _state = ComposerState.Absent;
    private bool _started;
    private bool _deferred;
    private bool _pendingSignal;

    /// <summary>
    /// Set the moment shutdown begins, BEFORE <see cref="StopAsync"/> releases the lock to await
    /// the in-flight attempt. <see cref="_state"/> alone cannot gate signals during that window:
    /// a <see cref="ComposerState.PendingConnect"/> coordinator keeps that state until the
    /// terminal <see cref="ComposerState.Stopped"/> publication, so a signal landing in between
    /// would otherwise launch an attempt that shutdown has already promised to drop.
    /// </summary>
    private bool _stopping;

    private Task? _inFlight;
    private Action? _unsubscribe;
    private CancellationTokenRegistration _lifetimeRegistration;
    private bool _disposed;

    /// <summary>
    /// Creates a coordinator for one Composer.
    /// </summary>
    /// <param name="primaryModel">
    /// The Composer's effective startup model (<c>Composer.StartupDefaultModel</c>). Null or
    /// whitespace means a model-less shell: the coordinator stays <see cref="ComposerState.Absent"/>
    /// and never connects.
    /// </param>
    /// <param name="compactionModel">
    /// The configured compaction model (<c>config.Models?.CompactionModel</c>). Null/empty inherits
    /// the primary model's provider and therefore never gates on its own.
    /// </param>
    /// <param name="oauthEnabled">The Program "auth-enabled" predicate result (both OAuth env vars set).</param>
    /// <param name="connectAsync">The existing <c>Composer.ConnectAsync</c>. Null behaves like a model-less shell.</param>
    /// <param name="isTokenAvailable">Token availability probe — <c>ChatClientFactory.IsTokenAvailable</c>.</param>
    /// <param name="providerOf">Provider resolver — <c>ChatClientFactory.ParseProviderAndModel(m).Provider</c>.</param>
    /// <param name="logger">Optional logger.</param>
    public LlmConnectionCoordinator(
        string? primaryModel,
        string? compactionModel,
        bool oauthEnabled,
        Func<CancellationToken, Task>? connectAsync,
        Func<bool> isTokenAvailable,
        Func<string, string> providerOf,
        ILogger? logger = null)
    {
        _primaryModel = primaryModel;
        _compactionModel = compactionModel;
        _oauthEnabled = oauthEnabled;
        _connectAsync = connectAsync;
        _isTokenAvailable = isTokenAvailable ?? throw new ArgumentNullException(nameof(isTokenAvailable));
        _providerOf = providerOf ?? throw new ArgumentNullException(nameof(providerOf));
        _logger = logger;
    }

    /// <summary>The current observable state.</summary>
    public ComposerState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>
    /// Whether the startup connect was deferred. Retry-on-fault eligibility is deferred-only.
    /// </summary>
    internal bool WasDeferred
    {
        get { lock (_gate) { return _deferred; } }
    }

    /// <summary>
    /// Raised on every state CHANGE, in exact transition order, while the caller holds the
    /// coordinator's lock. Internal so tests (via <c>InternalsVisibleTo</c>) can assert the
    /// transition sequence deterministically. No notification is ever published after
    /// <see cref="ComposerState.Stopped"/>.
    /// </summary>
    internal event Action<ComposerState>? StateChanged;

    /// <summary>
    /// Whether the Composer would be gated (deferred) right now: OAuth enabled AND the Composer
    /// requires a GitHub Copilot token (primary or compaction model) AND none is available.
    /// </summary>
    internal bool ShouldDefer()
        => _oauthEnabled && RequiresCopilotToken() && !SafeIsTokenAvailable();

    /// <summary>
    /// Whether the Composer's primary or compaction model resolves to the GitHub Copilot
    /// provider. A null/empty compaction model inherits the primary's provider, so it never
    /// gates on its own.
    /// </summary>
    internal bool RequiresCopilotToken()
    {
        if (string.IsNullOrWhiteSpace(_primaryModel))
            return false;

        if (IsCopilot(_primaryModel))
            return true;

        return !string.IsNullOrWhiteSpace(_compactionModel) && IsCopilot(_compactionModel);
    }

    private bool IsCopilot(string model)
    {
        try
        {
            return string.Equals(_providerOf(model), CopilotProvider, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // An unparseable model must never crash startup; treat it as non-Copilot so the
            // eager connect path runs and surfaces the real error as Faulted.
            _logger?.LogWarning(ex, "Could not resolve provider for model '{Model}'", model);
            return false;
        }
    }

    private bool SafeIsTokenAvailable()
    {
        try
        {
            return _isTokenAvailable();
        }
        catch (Exception ex)
        {
            // A failing availability probe must not gate startup: assume no token, which is the
            // safe (deferring) answer only when a Copilot model is configured anyway.
            _logger?.LogWarning(ex, "Token availability probe threw; treating token as unavailable");
            return false;
        }
    }

    /// <summary>
    /// Runs the startup decision exactly once:
    /// model-less → stays <see cref="ComposerState.Absent"/>;
    /// gated → <see cref="ComposerState.PendingConnect"/> (returns immediately);
    /// otherwise → <see cref="ComposerState.Connecting"/> and awaits the attempt to
    /// <see cref="ComposerState.Connected"/> or <see cref="ComposerState.Faulted"/>.
    /// Never throws: a connect failure is observed as <see cref="ComposerState.Faulted"/>.
    /// </summary>
    public async Task StartAsync()
    {
        Task? attempt;
        lock (_gate)
        {
            if (_started || _stopping || _state == ComposerState.Stopped)
                return;

            _started = true;

            if (_connectAsync is null || string.IsNullOrWhiteSpace(_primaryModel))
            {
                // Model-less shell: Absent, and only shutdown moves it.
                _logger?.LogWarning(ShellLog);
                return;
            }

            if (ShouldDefer())
            {
                _deferred = true;
                _logger?.LogWarning(DeferredLog);
                Transition(ComposerState.PendingConnect);
                return;
            }

            attempt = StartAttemptLocked();
        }

        await attempt.ConfigureAwait(false);
    }

    /// <summary>
    /// The token-available signal: a non-whitespace GitHub OAuth access token was committed.
    /// Fire-and-forget — this method never throws, and an async connect failure it triggers is
    /// observed as <see cref="ComposerState.Faulted"/> instead of propagating to the caller
    /// (the sign-in request).
    /// </summary>
    public void OnTokenAvailable()
    {
        try
        {
            lock (_gate)
            {
                // Terminal / winding down / model-less: nothing to do. `_stopping` covers the
                // window where shutdown has begun but the terminal Stopped is not yet published —
                // queued signals are dropped and no retry follows.
                if (_stopping
                    || _state is ComposerState.Stopped or ComposerState.Cancelling or ComposerState.Absent)
                    return;

                // Retry eligibility is deferred-only. A never-deferred coordinator DROPS signals:
                // no pending-signal state, no retry on fault.
                if (!_deferred)
                    return;

                switch (_state)
                {
                    case ComposerState.Connecting:
                        // Dedup: exactly one ConnectAsync at a time. Record the signal; it
                        // coalesces into a single retry if (and only if) the attempt fails.
                        _pendingSignal = true;
                        return;

                    case ComposerState.PendingConnect:
                        StartAttemptLocked();
                        return;

                    case ComposerState.Faulted:
                        Transition(ComposerState.PendingConnect);
                        StartAttemptLocked();
                        return;

                    case ComposerState.Connected:
                        // Already connected — later Composer failures are not our concern.
                        return;

                    default:
                        throw new InvalidOperationException(
                            $"Unhandled Composer state '{_state}' in token-available signal.");
                }
            }
        }
        catch (Exception ex)
        {
            // Fire-and-forget delivery: never let signal handling change the caller's behaviour.
            _logger?.LogWarning(ex, "Token-available signal handling failed; ignored");
        }
    }

    /// <summary>
    /// Subscribes this coordinator's <see cref="OnTokenAvailable"/> handler to a signal source and
    /// retains the matching unsubscribe, which runs on disposal.
    /// </summary>
    /// <param name="subscribe">Adds the handler to the source's event.</param>
    /// <param name="unsubscribe">Removes the handler from the source's event.</param>
    public void SubscribeTokenSignal(Action<Action> subscribe, Action<Action> unsubscribe)
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);

        Action handler = OnTokenAvailable;
        subscribe(handler);
        _unsubscribe = () => unsubscribe(handler);
    }

    /// <summary>
    /// Binds the coordinator's shutdown table to the application lifetime's
    /// <c>ApplicationStopping</c> token.
    /// </summary>
    /// <param name="applicationStopping">The host's <c>ApplicationStopping</c> token.</param>
    public void BindToApplicationStopping(CancellationToken applicationStopping)
    {
        _lifetimeRegistration = applicationStopping.Register(static state =>
        {
            var self = (LlmConnectionCoordinator)state!;
            try { self.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { self._logger?.LogWarning(ex, "Composer connection shutdown failed"); }
        }, this);
    }

    /// <summary>
    /// Runs the shutdown table: cancel any in-flight attempt (<see cref="ComposerState.Connecting"/>
    /// → <see cref="ComposerState.Cancelling"/>), await its settle, then
    /// <see cref="ComposerState.Stopped"/>. Queued signals are dropped and no retry follows.
    /// Repeat calls are a no-op with no observer notification.
    /// </summary>
    public async Task StopAsync()
    {
        Task? inFlight;
        lock (_gate)
        {
            if (_state == ComposerState.Stopped)
                return;

            // Latched BEFORE the lock is released so no signal can start an attempt while this
            // shutdown is awaiting the in-flight settle below.
            _stopping = true;

            // Queued wake-up signals are dropped — after Stopped there are no retries.
            _pendingSignal = false;
            inFlight = _inFlight;

            if (_state == ComposerState.Connecting)
                Transition(ComposerState.Cancelling);
        }

        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }

        if (inFlight is not null)
        {
            // The attempt's continuation (the settle block) runs to completion first; a
            // late-completing attempt after Cancelling has its result DISCARDED.
            try { await inFlight.ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogDebug(ex, "In-flight Composer connect settled during shutdown"); }
        }

        lock (_gate)
        {
            if (_state == ComposerState.Stopped)
                return;

            Transition(ComposerState.Stopped);
        }
    }

    /// <summary>
    /// Runs the SAME shutdown table as <c>ApplicationStopping</c>, then disposes the
    /// coordinator's own CTS and subscriptions. The Composer itself is NOT disposed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);

        _unsubscribe?.Invoke();
        _unsubscribe = null;
        _lifetimeRegistration.Dispose();
        _cts.Dispose();
    }

    // ── internals ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes <see cref="ComposerState.Connecting"/> and starts exactly one attempt. Must be
    /// called while holding <see cref="_gate"/>; the attempt itself runs off the lock.
    /// </summary>
    private Task StartAttemptLocked()
    {
        Transition(ComposerState.Connecting);
        _pendingSignal = false;
        _inFlight = Task.Run(RunAttemptAsync);
        return _inFlight;
    }

    /// <summary>
    /// Runs one <c>ConnectAsync</c> and settles the resulting state. Never throws — connect
    /// failures become <see cref="ComposerState.Faulted"/>.
    /// </summary>
    private async Task RunAttemptAsync()
    {
        Exception? failure = null;
        try
        {
            _logger?.LogInformation(ConnectingLog);
            await _connectAsync!(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        lock (_gate)
        {
            _inFlight = null;

            // Shutdown won the race: the result is discarded and StopAsync publishes Stopped.
            if (_stopping || _state is ComposerState.Cancelling or ComposerState.Stopped)
            {
                _pendingSignal = false;
                return;
            }

            if (failure is null)
            {
                // A signal recorded during the attempt is dropped on success.
                _pendingSignal = false;
                _logger?.LogInformation(ConnectedLog);
                Transition(ComposerState.Connected);
                return;
            }

            _logger?.LogWarning(failure, ConnectFailedLog);
            Transition(ComposerState.Faulted);

            // Dedup: a signal recorded during the attempt coalesces into exactly ONE immediate
            // retry, publishing Faulted → PendingConnect → Connecting in that order.
            if (_deferred && _pendingSignal)
            {
                Transition(ComposerState.PendingConnect);
                StartAttemptLocked();
            }
        }
    }

    /// <summary>
    /// Publishes a state change to observers. Must be called while holding <see cref="_gate"/>.
    /// Never publishes after <see cref="ComposerState.Stopped"/> and never re-publishes the
    /// current state.
    /// </summary>
    private void Transition(ComposerState next)
    {
        // Stopped is terminal: no further observer notifications, ever.
        if (_state == ComposerState.Stopped || _state == next)
            return;

        _state = next;

        try
        {
            StateChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            // Observer faults must never affect the coordinator or its caller.
            _logger?.LogWarning(ex, "Composer state observer threw; ignored");
        }
    }
}
