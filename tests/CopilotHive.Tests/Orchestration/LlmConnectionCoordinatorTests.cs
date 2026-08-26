using CopilotHive.Orchestration;

using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder.Providers;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Unit tests for <see cref="LlmConnectionCoordinator"/> — the component that defers the
/// Composer's startup LLM connection until a GitHub Copilot token exists (the fresh-OAuth-install
/// fix) and then performs it exactly once per token signal.
/// <para>
/// The tests drive the coordinator through injected delegates and <see cref="TaskCompletionSource"/>
/// gates (the <c>DistributedBrainShadowTests</c> pattern) and assert against the internal
/// state-change observer, so every transition ORDER is verified deterministically — no sleeps,
/// no polling.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class LlmConnectionCoordinatorTests
{
    private const string CopilotModel = "gpt-5";
    private const string OllamaModel = "ollama-local/llama3";

    /// <summary>Real provider resolution — the production seam, not a stub.</summary>
    private static string RealProvider(string model) => ChatClientFactory.ParseProviderAndModel(model).Item1;

    /// <summary>Records the exact sequence of observed state changes.</summary>
    private sealed class StateRecorder
    {
        private readonly List<ComposerState> _states = [];
        private readonly object _gate = new();

        internal void Attach(LlmConnectionCoordinator coordinator) =>
            coordinator.StateChanged += s => { lock (_gate) { _states.Add(s); } };

        internal IReadOnlyList<ComposerState> States
        {
            get { lock (_gate) { return _states.ToList(); } }
        }
    }

    /// <summary>
    /// A stand-in for the Composer's <c>ConnectAsync</c>: counts invocations, captures the
    /// cancellation token it was handed, and lets the test settle each attempt explicitly.
    /// It also tracks disposal so "the coordinator must not dispose the Composer" is provable.
    /// </summary>
    private sealed class FakeComposer : IDisposable
    {
        private readonly Queue<TaskCompletionSource<bool>> _gates = new();
        private readonly object _gate = new();

        internal int ConnectCalls { get; private set; }
        internal bool WasDisposed { get; private set; }
        internal CancellationToken LastToken { get; private set; }

        /// <summary>Signals that an attempt has entered <c>ConnectAsync</c>.</summary>
        internal TaskCompletionSource<bool> Entered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Queues the outcome of the next attempt; null completes it immediately.</summary>
        internal TaskCompletionSource<bool> EnqueueGate()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) { _gates.Enqueue(tcs); }
            return tcs;
        }

        internal async Task ConnectAsync(CancellationToken ct)
        {
            TaskCompletionSource<bool>? gate;
            lock (_gate)
            {
                ConnectCalls++;
                LastToken = ct;
                gate = _gates.Count > 0 ? _gates.Dequeue() : null;
            }

            Entered.TrySetResult(true);

            if (gate is not null)
                await gate.Task;
        }

        /// <summary>Resets the "attempt entered" signal before triggering the next attempt.</summary>
        internal void ArmEnteredSignal() =>
            Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => WasDisposed = true;
    }

    private static LlmConnectionCoordinator Create(
        FakeComposer? composer,
        string? primaryModel,
        string? compactionModel = null,
        bool oauthEnabled = true,
        Func<bool>? isTokenAvailable = null)
        => new(
            primaryModel,
            compactionModel,
            oauthEnabled,
            composer is null ? null : composer.ConnectAsync,
            isTokenAvailable ?? (() => false),
            RealProvider,
            NullLogger<LlmConnectionCoordinator>.Instance);

    private static Task WithTimeout(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    // ── Gating ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// OAuth on, Copilot primary AND compaction, no token anywhere: the startup connect is
    /// deferred — the coordinator settles in <see cref="ComposerState.PendingConnect"/> and
    /// <c>ConnectAsync</c> was never called.
    /// </summary>
    [Fact]
    public async Task StartAsync_OAuthEnabled_CopilotModelsNoToken_DefersToPendingConnect()
    {
        var composer = new FakeComposer();
        await using var coordinator = Create(composer, CopilotModel, CopilotModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());

        Assert.Equal(ComposerState.PendingConnect, coordinator.State);
        Assert.Equal([ComposerState.PendingConnect], recorder.States);
        Assert.Equal(0, composer.ConnectCalls);
        Assert.True(coordinator.WasDeferred);
    }

    /// <summary>
    /// Open mode (OAuth env vars unset → the Program auth predicate is false): never gated, even
    /// for a Copilot model with no token. The attempt runs and settles as
    /// <see cref="ComposerState.Faulted"/> without ever passing through deferral.
    /// </summary>
    [Fact]
    public async Task StartAsync_OpenMode_CopilotModelNoToken_NotGated_AttemptFaults()
    {
        var composer = new FakeComposer();
        var gate = composer.EnqueueGate();
        gate.SetException(new InvalidOperationException("GH_TOKEN or GITHUB_TOKEN is required for copilot provider"));

        await using var coordinator = Create(composer, CopilotModel, CopilotModel, oauthEnabled: false);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());

        Assert.Equal(ComposerState.Faulted, coordinator.State);
        Assert.Equal([ComposerState.Connecting, ComposerState.Faulted], recorder.States);
        Assert.DoesNotContain(ComposerState.PendingConnect, recorder.States);
        Assert.False(coordinator.WasDeferred);
        Assert.Equal(1, composer.ConnectCalls);
    }

    /// <summary>A fully non-Copilot Composer connects eagerly even with no token at all.</summary>
    [Fact]
    public async Task StartAsync_NonCopilotModels_NotGated_ConnectsEagerly()
    {
        var composer = new FakeComposer();
        await using var coordinator = Create(composer, OllamaModel, OllamaModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());

        Assert.Equal(ComposerState.Connected, coordinator.State);
        Assert.Equal([ComposerState.Connecting, ComposerState.Connected], recorder.States);
        Assert.DoesNotContain(ComposerState.PendingConnect, recorder.States);
        Assert.Equal(1, composer.ConnectCalls);
    }

    /// <summary>
    /// An Ollama primary with an absent (null/empty) compaction model is NOT gated — an unset
    /// compaction model inherits the primary's provider.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StartAsync_OllamaPrimaryAbsentCompaction_NotGated(string? compaction)
    {
        var composer = new FakeComposer();
        await using var coordinator = Create(composer, OllamaModel, compaction);

        Assert.False(coordinator.RequiresCopilotToken());
        Assert.False(coordinator.ShouldDefer());

        await WithTimeout(coordinator.StartAsync());

        Assert.Equal(ComposerState.Connected, coordinator.State);
    }

    /// <summary>An Ollama primary with a Copilot COMPACTION model is gated.</summary>
    [Fact]
    public async Task StartAsync_OllamaPrimaryCopilotCompaction_Gated()
    {
        var composer = new FakeComposer();
        await using var coordinator = Create(composer, OllamaModel, CopilotModel);

        Assert.True(coordinator.RequiresCopilotToken());

        await WithTimeout(coordinator.StartAsync());

        Assert.Equal(ComposerState.PendingConnect, coordinator.State);
        Assert.Equal(0, composer.ConnectCalls);
    }

    /// <summary>A Copilot Composer WITH a token available is not gated: it connects eagerly.</summary>
    [Fact]
    public async Task StartAsync_CopilotModelWithToken_NotGated_ConnectsEagerly()
    {
        var composer = new FakeComposer();
        await using var coordinator = Create(composer, CopilotModel, CopilotModel, isTokenAvailable: () => true);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());

        Assert.Equal([ComposerState.Connecting, ComposerState.Connected], recorder.States);
        Assert.False(coordinator.WasDeferred);
    }

    /// <summary>A model-less shell is Absent — never gated, never connected.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StartAsync_NoConfiguredModel_StaysAbsent(string? model)
    {
        var composer = new FakeComposer();
        await using var coordinator = Create(composer, model, CopilotModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());

        Assert.Equal(ComposerState.Absent, coordinator.State);
        Assert.Empty(recorder.States);
        Assert.Equal(0, composer.ConnectCalls);
        Assert.False(coordinator.ShouldDefer());
    }

    /// <summary>An Absent coordinator ignores token signals and only shutdown moves it.</summary>
    [Fact]
    public async Task Absent_StaysAbsentUntilShutdown()
    {
        var composer = new FakeComposer();
        var coordinator = Create(composer, primaryModel: null);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        coordinator.OnTokenAvailable();

        Assert.Equal(ComposerState.Absent, coordinator.State);
        Assert.Empty(recorder.States);

        await WithTimeout(coordinator.StopAsync());

        Assert.Equal(ComposerState.Stopped, coordinator.State);
        Assert.Equal([ComposerState.Stopped], recorder.States);
        Assert.Equal(0, composer.ConnectCalls);

        await coordinator.DisposeAsync();
    }

    // ── Recovery on token commit ──────────────────────────────────────────────────────

    /// <summary>
    /// The fresh-OAuth-install recovery path: deferred at startup, and a committed
    /// non-whitespace token drives PendingConnect → Connecting → Connected.
    /// </summary>
    [Fact]
    public async Task OnTokenAvailable_AfterDeferral_ConnectsAndReportsConnected()
    {
        var composer = new FakeComposer();
        var tokenAvailable = false;
        await using var coordinator = Create(composer, CopilotModel, CopilotModel,
            isTokenAvailable: () => tokenAvailable);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        Assert.Equal(ComposerState.PendingConnect, coordinator.State);

        // The sign-in commits a real token, then signals.
        tokenAvailable = true;
        coordinator.OnTokenAvailable();

        await WithTimeout(composer.Entered.Task);
        await WaitForStateAsync(coordinator, ComposerState.Connected);

        Assert.Equal(
            [ComposerState.PendingConnect, ComposerState.Connecting, ComposerState.Connected],
            recorder.States);
        Assert.Equal(1, composer.ConnectCalls);
    }

    /// <summary>
    /// A deferred connect that FAILS is observed as Faulted, and a later non-whitespace commit
    /// retries: PendingConnect → Connecting → Connected.
    /// </summary>
    [Fact]
    public async Task DeferredFault_LaterTokenCommit_RetriesAndConnects()
    {
        var composer = new FakeComposer();
        var firstGate = composer.EnqueueGate();
        firstGate.SetException(new InvalidOperationException("copilot 401"));

        await using var coordinator = Create(composer, CopilotModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        coordinator.OnTokenAvailable();
        await WaitForStateAsync(coordinator, ComposerState.Faulted);

        composer.ArmEnteredSignal();
        coordinator.OnTokenAvailable();
        await WaitForStateAsync(coordinator, ComposerState.Connected);

        Assert.Equal(
            [
                ComposerState.PendingConnect, ComposerState.Connecting, ComposerState.Faulted,
                ComposerState.PendingConnect, ComposerState.Connecting, ComposerState.Connected
            ],
            recorder.States);
        Assert.Equal(2, composer.ConnectCalls);
    }

    /// <summary>
    /// A NEVER-deferred coordinator that faults stays Faulted: retry eligibility is deferred-only,
    /// so a later sign-in signal starts no attempt.
    /// </summary>
    [Fact]
    public async Task NeverDeferred_Fault_DoesNotRetryOnLaterSignal()
    {
        var composer = new FakeComposer();
        var gate = composer.EnqueueGate();
        gate.SetException(new InvalidOperationException("offline"));

        await using var coordinator = Create(composer, OllamaModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        Assert.Equal(ComposerState.Faulted, coordinator.State);

        coordinator.OnTokenAvailable();
        coordinator.OnTokenAvailable();

        Assert.Equal(ComposerState.Faulted, coordinator.State);
        Assert.Equal([ComposerState.Connecting, ComposerState.Faulted], recorder.States);
        Assert.Equal(1, composer.ConnectCalls);
    }

    /// <summary>Once Connected, later signals are ignored — no second attempt.</summary>
    [Fact]
    public async Task OnTokenAvailable_AfterConnected_DoesNothing()
    {
        var composer = new FakeComposer();
        var tokenAvailable = false;
        await using var coordinator = Create(composer, CopilotModel, isTokenAvailable: () => tokenAvailable);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        tokenAvailable = true;
        coordinator.OnTokenAvailable();
        await WaitForStateAsync(coordinator, ComposerState.Connected);

        coordinator.OnTokenAvailable();

        Assert.Equal(1, composer.ConnectCalls);
        Assert.Equal(
            [ComposerState.PendingConnect, ComposerState.Connecting, ComposerState.Connected],
            recorder.States);
    }

    // ── Dedup ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A signal arriving while an attempt is in flight starts NO new attempt; when the in-flight
    /// attempt FAILS it coalesces into exactly ONE retry, publishing
    /// Faulted → PendingConnect → Connecting in that order.
    /// </summary>
    [Fact]
    public async Task Dedup_SignalDuringConnecting_Failure_CoalescesIntoExactlyOneRetry()
    {
        var composer = new FakeComposer();
        var firstGate = composer.EnqueueGate();

        await using var coordinator = Create(composer, CopilotModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        coordinator.OnTokenAvailable();
        await WithTimeout(composer.Entered.Task);

        // Three more signals while connecting — none may start an attempt.
        composer.ArmEnteredSignal();
        coordinator.OnTokenAvailable();
        coordinator.OnTokenAvailable();
        coordinator.OnTokenAvailable();
        Assert.Equal(1, composer.ConnectCalls);
        Assert.Equal(ComposerState.Connecting, coordinator.State);

        firstGate.SetException(new InvalidOperationException("copilot 401"));
        await WaitForStateAsync(coordinator, ComposerState.Connected);

        Assert.Equal(2, composer.ConnectCalls);
        Assert.Equal(
            [
                ComposerState.PendingConnect, ComposerState.Connecting, ComposerState.Faulted,
                ComposerState.PendingConnect, ComposerState.Connecting, ComposerState.Connected
            ],
            recorder.States);
    }

    /// <summary>A recorded signal is DROPPED when the in-flight attempt succeeds.</summary>
    [Fact]
    public async Task Dedup_SignalDuringConnecting_Success_DropsSignal()
    {
        var composer = new FakeComposer();
        var gate = composer.EnqueueGate();

        await using var coordinator = Create(composer, CopilotModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        coordinator.OnTokenAvailable();
        await WithTimeout(composer.Entered.Task);

        coordinator.OnTokenAvailable();
        gate.SetResult(true);

        await WaitForStateAsync(coordinator, ComposerState.Connected);

        Assert.Equal(1, composer.ConnectCalls);
        Assert.Equal(
            [ComposerState.PendingConnect, ComposerState.Connecting, ComposerState.Connected],
            recorder.States);
    }

    /// <summary>
    /// A never-deferred coordinator DROPS a signal received during Connecting: no pending-signal
    /// state is recorded, so the subsequent fault produces no retry.
    /// </summary>
    [Fact]
    public async Task Dedup_NeverDeferred_SignalDuringConnecting_IsDropped()
    {
        var composer = new FakeComposer();
        var gate = composer.EnqueueGate();

        await using var coordinator = Create(composer, OllamaModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        var start = coordinator.StartAsync();
        await WithTimeout(composer.Entered.Task);

        coordinator.OnTokenAvailable();
        gate.SetException(new InvalidOperationException("offline"));
        await WithTimeout(start);

        Assert.Equal(ComposerState.Faulted, coordinator.State);
        Assert.Equal(1, composer.ConnectCalls);
        Assert.Equal([ComposerState.Connecting, ComposerState.Faulted], recorder.States);
    }

    // ── Fault containment ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A non-token failure during a deferred wake-up is observed as Faulted and never propagates
    /// out of <see cref="LlmConnectionCoordinator.OnTokenAvailable"/> (the sign-in caller).
    /// </summary>
    [Fact]
    public async Task OnTokenAvailable_ConnectThrows_DoesNotPropagate()
    {
        var composer = new FakeComposer();
        var gate = composer.EnqueueGate();
        gate.SetException(new HttpRequestException("network down"));

        await using var coordinator = Create(composer, CopilotModel);
        await WithTimeout(coordinator.StartAsync());

        // Must not throw — fire-and-forget delivery.
        coordinator.OnTokenAvailable();

        await WaitForStateAsync(coordinator, ComposerState.Faulted);
        Assert.Equal(ComposerState.Faulted, coordinator.State);
    }

    /// <summary>An observer that throws never affects the coordinator's state machine.</summary>
    [Fact]
    public async Task StateChanged_ObserverThrows_IsSwallowed()
    {
        var composer = new FakeComposer();
        await using var coordinator = Create(composer, OllamaModel);
        coordinator.StateChanged += _ => throw new InvalidOperationException("observer boom");

        await WithTimeout(coordinator.StartAsync());

        Assert.Equal(ComposerState.Connected, coordinator.State);
    }

    // ── Shutdown table ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shutdown mid-attempt: Connecting → Cancelling, and Stopped once the task settles. The
    /// late-completing attempt's SUCCESS is discarded — Connected is never published.
    /// </summary>
    [Fact]
    public async Task StopAsync_DuringConnecting_CancellingThenStopped_LateResultDiscarded()
    {
        var composer = new FakeComposer();
        var gate = composer.EnqueueGate();

        var coordinator = Create(composer, OllamaModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        var start = coordinator.StartAsync();
        await WithTimeout(composer.Entered.Task);
        Assert.Equal(ComposerState.Connecting, coordinator.State);

        var stop = coordinator.StopAsync();
        await WaitForStateAsync(coordinator, ComposerState.Cancelling);
        Assert.True(composer.LastToken.IsCancellationRequested,
            "The in-flight attempt must observe cancellation on shutdown.");

        // The attempt completes SUCCESSFULLY after Cancelling — its result must be discarded.
        gate.SetResult(true);
        await WithTimeout(stop);
        await WithTimeout(start);

        Assert.Equal(ComposerState.Stopped, coordinator.State);
        Assert.Equal(
            [ComposerState.Connecting, ComposerState.Cancelling, ComposerState.Stopped],
            recorder.States);
        Assert.DoesNotContain(ComposerState.Connected, recorder.States);

        await coordinator.DisposeAsync();
    }

    /// <summary>PendingConnect with no in-flight attempt stops directly, dropping queued signals.</summary>
    [Fact]
    public async Task StopAsync_FromPendingConnect_StopsWithoutRetry()
    {
        var composer = new FakeComposer();
        var coordinator = Create(composer, CopilotModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        await WithTimeout(coordinator.StopAsync());

        Assert.Equal(ComposerState.Stopped, coordinator.State);
        Assert.Equal([ComposerState.PendingConnect, ComposerState.Stopped], recorder.States);

        // After Stopped there are no retries.
        coordinator.OnTokenAvailable();
        Assert.Equal(ComposerState.Stopped, coordinator.State);
        Assert.Equal(0, composer.ConnectCalls);

        await coordinator.DisposeAsync();
    }

    /// <summary>Connected → Stopped.</summary>
    [Fact]
    public async Task StopAsync_FromConnected_Stops()
    {
        var composer = new FakeComposer();
        var coordinator = Create(composer, OllamaModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        await WithTimeout(coordinator.StopAsync());

        Assert.Equal(
            [ComposerState.Connecting, ComposerState.Connected, ComposerState.Stopped],
            recorder.States);

        await coordinator.DisposeAsync();
    }

    /// <summary>An idle Faulted coordinator stops without passing through any other state.</summary>
    [Fact]
    public async Task StopAsync_FromIdleFaulted_Stops()
    {
        var composer = new FakeComposer();
        var gate = composer.EnqueueGate();
        gate.SetException(new InvalidOperationException("offline"));

        var coordinator = Create(composer, OllamaModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        await WithTimeout(coordinator.StopAsync());

        Assert.Equal(
            [ComposerState.Connecting, ComposerState.Faulted, ComposerState.Stopped],
            recorder.States);

        await coordinator.DisposeAsync();
    }

    /// <summary>Repeated stops are idempotent and publish no further observer notification.</summary>
    [Fact]
    public async Task StopAsync_Repeated_IsNoOpWithoutNotification()
    {
        var composer = new FakeComposer();
        var coordinator = Create(composer, CopilotModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        await WithTimeout(coordinator.StopAsync());
        await WithTimeout(coordinator.StopAsync());
        await WithTimeout(coordinator.StopAsync());

        Assert.Equal([ComposerState.PendingConnect, ComposerState.Stopped], recorder.States);

        await coordinator.DisposeAsync();
    }

    /// <summary>
    /// The application lifetime binding runs the shutdown table when <c>ApplicationStopping</c>
    /// fires.
    /// </summary>
    [Fact]
    public async Task BindToApplicationStopping_RunsShutdownTable()
    {
        var composer = new FakeComposer();
        var coordinator = Create(composer, CopilotModel);
        using var lifetime = new CancellationTokenSource();
        coordinator.BindToApplicationStopping(lifetime.Token);

        await WithTimeout(coordinator.StartAsync());
        Assert.Equal(ComposerState.PendingConnect, coordinator.State);

        await lifetime.CancelAsync();

        Assert.Equal(ComposerState.Stopped, coordinator.State);

        await coordinator.DisposeAsync();
    }

    // ── Disposal ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>DisposeAsync</c> runs the same shutdown table (cancel → await settle → Stopped) and does
    /// NOT dispose the Composer. It also unsubscribes the token signal.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_RunsShutdownTable_DoesNotDisposeComposer()
    {
        var composer = new FakeComposer();
        var gate = composer.EnqueueGate();

        var coordinator = Create(composer, OllamaModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        var signal = new TokenSignalSource();
        coordinator.SubscribeTokenSignal(h => signal.TokenAvailable += h, h => signal.TokenAvailable -= h);
        Assert.Equal(1, signal.SubscriberCount);

        var start = coordinator.StartAsync();
        await WithTimeout(composer.Entered.Task);

        var dispose = coordinator.DisposeAsync();
        await WaitForStateAsync(coordinator, ComposerState.Cancelling);
        gate.SetResult(true);
        await WithTimeout(dispose.AsTask());
        await WithTimeout(start);

        Assert.Equal(ComposerState.Stopped, coordinator.State);
        Assert.Equal(
            [ComposerState.Connecting, ComposerState.Cancelling, ComposerState.Stopped],
            recorder.States);
        Assert.False(composer.WasDisposed, "The coordinator must never dispose the Composer.");
        Assert.Equal(0, signal.SubscriberCount);
    }

    /// <summary>Disposing an already-Stopped coordinator is a no-op.</summary>
    [Fact]
    public async Task DisposeAsync_AfterStop_IsNoOp()
    {
        var composer = new FakeComposer();
        var coordinator = Create(composer, CopilotModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        await WithTimeout(coordinator.StopAsync());

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        Assert.Equal([ComposerState.PendingConnect, ComposerState.Stopped], recorder.States);
        Assert.False(composer.WasDisposed);
    }

    /// <summary>The token signal wired through <c>SubscribeTokenSignal</c> drives the connect.</summary>
    [Fact]
    public async Task SubscribeTokenSignal_SignalTriggersDeferredConnect()
    {
        var composer = new FakeComposer();
        await using var coordinator = Create(composer, CopilotModel);
        var signal = new TokenSignalSource();
        coordinator.SubscribeTokenSignal(h => signal.TokenAvailable += h, h => signal.TokenAvailable -= h);

        await WithTimeout(coordinator.StartAsync());
        Assert.Equal(ComposerState.PendingConnect, coordinator.State);

        signal.Raise();

        await WaitForStateAsync(coordinator, ComposerState.Connected);
        Assert.Equal(1, composer.ConnectCalls);
    }

    // ── Token availability / whitespace ───────────────────────────────────────────────

    /// <summary>
    /// <c>ChatClientFactory.IsTokenAvailable</c> (SharpCoder.Providers 0.18.0) is the single
    /// source of truth and takes the FIRST NON-WHITESPACE of (OAuth store, GH_TOKEN,
    /// GITHUB_TOKEN) — whitespace never wins, and a whitespace-only store falls through to the
    /// environment.
    /// </summary>
    [Fact]
    public void ChatClientFactory_IsTokenAvailable_FirstNonWhitespaceWins()
    {
        var previousGh = Environment.GetEnvironmentVariable("GH_TOKEN");
        var previousGithub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

            ChatClientFactory.SetTokenProvider(() => null!);
            Assert.False(ChatClientFactory.IsTokenAvailable());

            // Whitespace in the OAuth store never counts.
            ChatClientFactory.SetTokenProvider(() => "   ");
            Assert.False(ChatClientFactory.IsTokenAvailable());

            // …and whitespace env vars never count either.
            Environment.SetEnvironmentVariable("GH_TOKEN", "  ");
            Assert.False(ChatClientFactory.IsTokenAvailable());

            // The first NON-whitespace source wins.
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "env-token");
            Assert.True(ChatClientFactory.IsTokenAvailable());

            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            ChatClientFactory.SetTokenProvider(() => "store-token");
            Assert.True(ChatClientFactory.IsTokenAvailable());
        }
        finally
        {
            ChatClientFactory.SetTokenProvider(() => null!);
            Environment.SetEnvironmentVariable("GH_TOKEN", previousGh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousGithub);
        }
    }

    /// <summary>
    /// Gating runs through the real <c>IsTokenAvailable</c>: a whitespace-only OAuth token with
    /// both env vars cleared still gates (deferral), and a real <c>GH_TOKEN</c> does not.
    /// </summary>
    [Fact]
    public async Task ShouldDefer_UsesRealTokenAvailability_WhitespaceNeverWins()
    {
        var previousGh = Environment.GetEnvironmentVariable("GH_TOKEN");
        var previousGithub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            ChatClientFactory.SetTokenProvider(() => "   ");

            var composer = new FakeComposer();
            await using (var gated = new LlmConnectionCoordinator(
                CopilotModel, CopilotModel, oauthEnabled: true,
                composer.ConnectAsync, ChatClientFactory.IsTokenAvailable, RealProvider,
                NullLogger<LlmConnectionCoordinator>.Instance))
            {
                Assert.True(gated.ShouldDefer());
                await WithTimeout(gated.StartAsync());
                Assert.Equal(ComposerState.PendingConnect, gated.State);
                Assert.Equal(0, composer.ConnectCalls);
            }

            // GH_TOKEN present → unchanged (eager) behaviour.
            Environment.SetEnvironmentVariable("GH_TOKEN", "gh-token");
            var eagerComposer = new FakeComposer();
            await using (var eager = new LlmConnectionCoordinator(
                CopilotModel, CopilotModel, oauthEnabled: true,
                eagerComposer.ConnectAsync, ChatClientFactory.IsTokenAvailable, RealProvider,
                NullLogger<LlmConnectionCoordinator>.Instance))
            {
                Assert.False(eager.ShouldDefer());
                await WithTimeout(eager.StartAsync());
                Assert.Equal(ComposerState.Connected, eager.State);
                Assert.Equal(1, eagerComposer.ConnectCalls);
            }
        }
        finally
        {
            ChatClientFactory.SetTokenProvider(() => null!);
            Environment.SetEnvironmentVariable("GH_TOKEN", previousGh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousGithub);
        }
    }

    /// <summary>A throwing availability probe is treated as "no token" instead of crashing startup.</summary>
    [Fact]
    public async Task StartAsync_TokenProbeThrows_TreatedAsUnavailable()
    {
        var composer = new FakeComposer();
        await using var coordinator = Create(composer, CopilotModel,
            isTokenAvailable: () => throw new InvalidOperationException("probe boom"));

        await WithTimeout(coordinator.StartAsync());

        Assert.Equal(ComposerState.PendingConnect, coordinator.State);
    }

    /// <summary>A second <c>StartAsync</c> is a no-op — the startup decision runs exactly once.</summary>
    [Fact]
    public async Task StartAsync_CalledTwice_RunsOnce()
    {
        var composer = new FakeComposer();
        await using var coordinator = Create(composer, OllamaModel);

        await WithTimeout(coordinator.StartAsync());
        await WithTimeout(coordinator.StartAsync());

        Assert.Equal(1, composer.ConnectCalls);
    }

    // ── Real-factory (credential-free, offline) startup proofs ────────────────────────

    /// <summary>
    /// Runs <paramref name="body"/> with BOTH token env vars cleared and an EMPTY OAuth store
    /// (the fresh-OAuth-install shape), restoring the previous environment afterwards.
    /// </summary>
    private static async Task WithNoTokenAnywhereAsync(Func<Task> body)
    {
        var previousGh = Environment.GetEnvironmentVariable("GH_TOKEN");
        var previousGithub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            // Empty OAuth store: exactly what UserService.GetActiveAccessTokenAsync returns
            // before anyone has signed in.
            ChatClientFactory.SetTokenProvider(() => null!);
            Assert.False(ChatClientFactory.IsTokenAvailable());

            await body();
        }
        finally
        {
            ChatClientFactory.SetTokenProvider(() => null!);
            Environment.SetEnvironmentVariable("GH_TOKEN", previousGh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousGithub);
        }
    }

    /// <summary>
    /// The fresh-OAuth-install shape end to end through the REAL SharpCoder factory: OAuth on,
    /// Copilot primary + compaction, both env vars cleared, empty OAuth store. The startup
    /// connect is deferred — post-startup state is <see cref="ComposerState.PendingConnect"/> and
    /// the LLM client was never constructed.
    /// </summary>
    [Fact]
    public async Task RealFactory_OAuthOn_CopilotNoTokenAnywhere_PostStartupPendingConnect()
    {
        await WithNoTokenAnywhereAsync(async () =>
        {
            var clientCreations = 0;
            await using var coordinator = new LlmConnectionCoordinator(
                CopilotModel, CopilotModel, oauthEnabled: true,
                _ => { Interlocked.Increment(ref clientCreations); ChatClientFactory.Create(CopilotModel); return Task.CompletedTask; },
                ChatClientFactory.IsTokenAvailable, RealProvider,
                NullLogger<LlmConnectionCoordinator>.Instance);
            var recorder = new StateRecorder();
            recorder.Attach(coordinator);

            await WithTimeout(coordinator.StartAsync());

            Assert.Equal(ComposerState.PendingConnect, coordinator.State);
            Assert.Equal([ComposerState.PendingConnect], recorder.States);
            Assert.Equal(0, clientCreations);
        });
    }

    /// <summary>
    /// Open mode (OAuth off) with the very same no-token environment is NOT gated: the eager
    /// attempt runs through the real factory, fails offline, and settles as
    /// <see cref="ComposerState.Faulted"/> — never through deferral.
    /// </summary>
    [Fact]
    public async Task RealFactory_OpenMode_CopilotNoTokenAnywhere_FaultsWithoutDeferral()
    {
        await WithNoTokenAnywhereAsync(async () =>
        {
            await using var coordinator = new LlmConnectionCoordinator(
                CopilotModel, CopilotModel, oauthEnabled: false,
                _ => { ChatClientFactory.Create(CopilotModel); return Task.CompletedTask; },
                ChatClientFactory.IsTokenAvailable, RealProvider,
                NullLogger<LlmConnectionCoordinator>.Instance);
            var recorder = new StateRecorder();
            recorder.Attach(coordinator);

            await WithTimeout(coordinator.StartAsync());

            Assert.Equal(ComposerState.Faulted, coordinator.State);
            Assert.Equal([ComposerState.Connecting, ComposerState.Faulted], recorder.States);
            Assert.DoesNotContain(ComposerState.PendingConnect, recorder.States);
        });
    }

    /// <summary>
    /// A fully non-Copilot Composer with the same no-token environment connects EAGERLY: the
    /// credential-free offline Ollama client is genuinely constructed by the real factory, so
    /// post-startup state is <see cref="ComposerState.Connected"/> with no deferral.
    /// </summary>
    [Fact]
    public async Task RealFactory_OllamaOnly_NoTokenAnywhere_PostStartupConnected()
    {
        await WithNoTokenAnywhereAsync(async () =>
        {
            await using var coordinator = new LlmConnectionCoordinator(
                OllamaModel, OllamaModel, oauthEnabled: true,
                _ =>
                {
                    // Proves the client really is constructible without any credential.
                    using var client = ChatClientFactory.Create(OllamaModel);
                    Assert.NotNull(client);
                    return Task.CompletedTask;
                },
                ChatClientFactory.IsTokenAvailable, RealProvider,
                NullLogger<LlmConnectionCoordinator>.Instance);
            var recorder = new StateRecorder();
            recorder.Attach(coordinator);

            await WithTimeout(coordinator.StartAsync());

            Assert.Equal(ComposerState.Connected, coordinator.State);
            Assert.Equal([ComposerState.Connecting, ComposerState.Connected], recorder.States);
            Assert.DoesNotContain(ComposerState.PendingConnect, recorder.States);
        });
    }

    // ── Shutdown/signal race ──────────────────────────────────────────────────────────

    /// <summary>
    /// A token signal delivered while <see cref="LlmConnectionCoordinator.StopAsync"/> is awaiting
    /// the in-flight attempt's settle must NOT start another <c>ConnectAsync</c>. Shutdown has
    /// already captured the task it will await, so an attempt launched in that window would be
    /// unawaited — it would outlive shutdown and race the disposal of the coordinator's own CTS.
    /// <para>
    /// This is forced deterministically (no sleeping, no racing): the connect blocks until the
    /// test releases it, so shutdown is provably parked inside the settle-await when the signals
    /// are delivered.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StopAsync_SignalDuringSettleAwait_StartsNoNewAttempt()
    {
        var composer = new FakeComposer();
        var gate = composer.EnqueueGate();

        var coordinator = Create(composer, CopilotModel, CopilotModel, isTokenAvailable: () => false);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        Assert.Equal(ComposerState.PendingConnect, coordinator.State);

        // Wake up the deferred coordinator so an attempt is genuinely in flight.
        coordinator.OnTokenAvailable();
        await WithTimeout(composer.Entered.Task);
        Assert.Equal(1, composer.ConnectCalls);

        // Shutdown parks inside the settle-await because the connect is still blocked.
        var stop = coordinator.StopAsync();
        await WaitForStateAsync(coordinator, ComposerState.Cancelling);

        // Signals delivered squarely inside the shutdown window must all be dropped.
        for (var i = 0; i < 50; i++)
            coordinator.OnTokenAvailable();

        Assert.Equal(1, composer.ConnectCalls);

        gate.SetResult(true);
        await WithTimeout(stop);

        Assert.Equal(ComposerState.Stopped, coordinator.State);
        Assert.Equal(1, composer.ConnectCalls);
        Assert.Equal(
            [
                ComposerState.PendingConnect, ComposerState.Connecting,
                ComposerState.Cancelling, ComposerState.Stopped
            ],
            recorder.States);

        await coordinator.DisposeAsync();
        Assert.Equal(1, composer.ConnectCalls);
    }

    /// <summary>
    /// A <see cref="ComposerState.PendingConnect"/> coordinator that has begun shutting down must
    /// drop token signals: once <see cref="LlmConnectionCoordinator.StopAsync"/> has run, the
    /// shutdown table promises no retry, so a late signal may never resurrect a connect.
    /// </summary>
    [Fact]
    public async Task StopAsync_SignalsAfterShutdown_NeverStartAnAttempt()
    {
        var composer = new FakeComposer();
        var coordinator = Create(composer, CopilotModel);
        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        await WithTimeout(coordinator.StopAsync());

        for (var i = 0; i < 50; i++)
            coordinator.OnTokenAvailable();

        Assert.Equal(ComposerState.Stopped, coordinator.State);
        Assert.Equal(0, composer.ConnectCalls);
        Assert.Equal([ComposerState.PendingConnect, ComposerState.Stopped], recorder.States);

        await coordinator.DisposeAsync();
        Assert.Equal(0, composer.ConnectCalls);
    }

    /// <summary>
    /// A token signal delivered inside the shutdown window must not start a connect that shutdown
    /// will never await.
    /// <para>
    /// <see cref="LlmConnectionCoordinator.StopAsync"/> captures the in-flight task under the lock,
    /// releases it to cancel and await the settle, then re-takes it to publish
    /// <see cref="ComposerState.Stopped"/>. For a coordinator with NO in-flight attempt (idle
    /// <see cref="ComposerState.Faulted"/>, or <see cref="ComposerState.PendingConnect"/>) the
    /// observable state throughout that window is still a signal-accepting one, so state alone
    /// cannot gate it: a signal arriving there launches an attempt AFTER shutdown already decided
    /// there was nothing to await. That connect outlives shutdown and races the disposal of the
    /// coordinator's own CTS.
    /// </para>
    /// <para>
    /// The window is entered deterministically — no sleeping, no thread racing. The connect
    /// delegate captures the coordinator's cancellation token from a first (faulting) attempt;
    /// registering on that token schedules the signal to fire from inside
    /// <c>StopAsync</c>'s own cancel step, which is exactly the window under test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StopAsync_SignalDeliveredInsideShutdownWindow_StartsNoUnawaitedAttempt()
    {
        var composer = new FakeComposer();
        var firstGate = composer.EnqueueGate();
        firstGate.SetException(new InvalidOperationException("copilot 401"));

        LlmConnectionCoordinator? coordinator = null;
        CancellationToken capturedToken = default;

        coordinator = new LlmConnectionCoordinator(
            CopilotModel, CopilotModel, oauthEnabled: true,
            ct => { capturedToken = ct; return composer.ConnectAsync(ct); },
            () => false, RealProvider, NullLogger<LlmConnectionCoordinator>.Instance);

        var recorder = new StateRecorder();
        recorder.Attach(coordinator);

        await WithTimeout(coordinator.StartAsync());
        Assert.Equal(ComposerState.PendingConnect, coordinator.State);

        // Drive one attempt so the coordinator is deferred AND idle-Faulted, and so the test holds
        // the coordinator's own cancellation token.
        coordinator.OnTokenAvailable();
        await WaitForStateAsync(coordinator, ComposerState.Faulted);
        Assert.Equal(1, composer.ConnectCalls);
        Assert.True(capturedToken.CanBeCanceled);

        // Fires from within StopAsync's cancel step — squarely inside the shutdown window.
        using var _ = capturedToken.Register(() => coordinator!.OnTokenAvailable());

        await WithTimeout(coordinator.StopAsync());

        // The in-window signal must have been dropped: no second attempt, and the shutdown table's
        // exact transitions for an idle Faulted coordinator.
        Assert.Equal(1, composer.ConnectCalls);
        Assert.Equal(ComposerState.Stopped, coordinator.State);
        Assert.Equal(
            [
                ComposerState.PendingConnect, ComposerState.Connecting,
                ComposerState.Faulted, ComposerState.Stopped
            ],
            recorder.States);

        await coordinator.DisposeAsync();
        Assert.Equal(1, composer.ConnectCalls);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>A minimal token-signal publisher matching the UserService event shape.</summary>
    private sealed class TokenSignalSource
    {
        internal event Action? TokenAvailable;

        internal int SubscriberCount => TokenAvailable?.GetInvocationList().Length ?? 0;

        internal void Raise() => TokenAvailable?.Invoke();
    }

    /// <summary>
    /// Awaits a state via the coordinator's own observer event — no sleeping and no polling.
    /// </summary>
    private static async Task WaitForStateAsync(LlmConnectionCoordinator coordinator, ComposerState expected)
    {
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ComposerState state)
        {
            if (state == expected)
                reached.TrySetResult(true);
        }

        coordinator.StateChanged += Handler;
        try
        {
            if (coordinator.State == expected)
                return;

            await WithTimeout(reached.Task);
        }
        finally
        {
            coordinator.StateChanged -= Handler;
        }
    }
}
