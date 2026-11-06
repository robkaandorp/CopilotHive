using CopilotHive.Actors;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Connect-outcome and cancellation-ownership semantics of <see cref="ComposerRuntimeManager"/>:
/// typed outcomes, the shared manager-owned operation, awaitable deferred connect, removal ownership and
/// the proof that managed paths never reach the legacy <see cref="LlmConnectionCoordinator"/>.
/// Interleavings are gated by production-caused milestones (the manager's own connect call, the bus
/// unsubscribe inside failure cleanup, the removal's token cancellation) with bounded, fail-loudly hang
/// guards; no test sleeps.
/// </summary>
public sealed partial class ComposerRuntimeManagerTests
{
    /// <summary>Parks the manager's connect call until released; can fail it, or delegate to the real connect.</summary>
    private sealed class ConnectGate : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> _terminalRuns = [];
        private int _calls;
        private readonly List<Composer> _composers = [];

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref _calls);
        public CancellationToken ObservedToken { get; private set; }
        public Exception? FailWith { get; set; }
        public Composer Composer0 { get { lock (_composers) return _composers[0]; } }

        public void Release() => _release.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Release(); // assertion-failure fallback: release production BEFORE joining it
            Task[] terminals;
            lock (_terminalRuns) terminals = _terminalRuns.ToArray();
            await GuardAsync(Task.WhenAll(terminals), "all connect-gate invocations exited");
        }

        public async Task RunAsync(Composer composer, CancellationToken ct)
        {
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_terminalRuns) _terminalRuns.Add(terminal.Task); // own BEFORE publishing Entered
            try
            {
                Interlocked.Increment(ref _calls);
                lock (_composers) _composers.Add(composer);
                ObservedToken = ct;
                using var registration = ct.Register(() => CancelObserved.TrySetResult());
                Entered.TrySetResult(); // production-caused: the manager reached its connect call
                await GuardAsync(_release.Task, "release parked manager connect");
                if (FailWith is { } failure) throw failure;
                ct.ThrowIfCancellationRequested();
                await composer.ConnectAsync(ct);
            }
            finally { terminal.TrySetResult(); }
        }
    }

    private ConnectGate Park()
    {
        var gate = new ConnectGate();
        _connectHook = gate.RunAsync;
        return gate;
    }

    /// <summary>
    /// Parks the factory's token probe — which production evaluates SYNCHRONOUSLY inside its deferral gate — so a
    /// test can prove the manager never holds its gate across it. The hook must never assert: production CONTAINS
    /// probe failures, so every outcome is recorded in counters the test asserts on afterwards, and a hang-guard
    /// timeout is recorded rather than thrown into production.
    /// </summary>
    private sealed class TokenProbeGate : IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedParked;
        private int _armed;
        private int _calls;
        private int _timedOut;

        /// <param name="expectedParked">How many parked probes complete <see cref="AllEntered"/> (1 = just the first).</param>
        public TokenProbeGate(int expectedParked = 1) => _expectedParked = expectedParked;

        /// <summary>Production-caused: the FIRST armed probe is genuinely parked.</summary>
        public Task Completion => _entered.Task;

        /// <summary>Production-caused: <see cref="_expectedParked"/> probes are parked simultaneously.</summary>
        public Task AllEntered => _allEntered.Task;

        /// <summary>How many armed probes have been evaluated (each of them parks until <see cref="Release"/>).</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Arms the gate: probes evaluated from now on park. Nothing parks before this call.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <summary>Disarms the gate: later probes pass straight through (used while the test drives other production paths).</summary>
        public void Disarm() => Volatile.Write(ref _armed, 0);

        public void Release() => _release.TrySetResult();

        /// <summary>Invoked from the factory's <c>isTokenAvailable</c> probe — the production call path.</summary>
        public void Observe()
        {
            if (Volatile.Read(ref _armed) == 0) return;
            var call = Interlocked.Increment(ref _calls);
            // Only the FIRST <see cref="_expectedParked"/> armed probes park; later ones pass through with the
            // fixture's current token value, so unrelated production paths keep working while the test asserts.
            if (call > _expectedParked) return;
            if (call >= _expectedParked) _allEntered.TrySetResult();
            _entered.TrySetResult();
            // A genuinely BLOCKING probe, exactly like a synchronous SQLite hit.
            if (!_release.Task.Wait(TimeSpan.FromSeconds(30))) Interlocked.Exchange(ref _timedOut, 1);
        }

        public void AssertHealthy() => Assert.True(Volatile.Read(ref _timedOut) == 0,
            "Timed out: release parked token probe (production must not swallow a test timeout)");

        public void Dispose() => Release();
    }

    private IDisposable ParkTokenProbe(TokenProbeGate gate)
    {
        _tokenProbeHook = gate.Observe;
        return new RestoreTokenProbeHook(this);
    }

    private sealed class RestoreTokenProbeHook(ComposerRuntimeManagerTests owner) : IDisposable
    {
        public void Dispose() => owner._tokenProbeHook = null;
    }

    // ── Lock scope: the synchronous token probe never runs under the manager gate ──

    [Fact]
    public async Task ParkedTokenProbe_DoesNotHoldTheManagerGate_WhileAnotherUserCreatesAndTheOwnerStaysDeferred()
    {
        var (first, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider(authEnabled: true));
        _provider = "copilot";
        _tokenAvailable = false;
        var deferred = (await Request(manager, second)).Runtime!;
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, second)).Status);
        using var probe = new TokenProbeGate();
        using var restore = ParkTokenProbe(probe);
        await using var gate = Park();
        probe.Arm();

        // The probe is synchronous, so the owner's ConnectAsync must run on its own thread: it BLOCKS there.
        var parked = RetryOnBackgroundThread(manager, second);
        await GuardAsync(probe.Completion, "owner's connect evaluated the token probe");
        Assert.False(parked.IsCompleted);

        // Unrelated user: the manager gate is free, so creation completes while the probe is still parked.
        // (Bounded, so a regression that held the gate fails loudly instead of hanging the test.)
        var other = RequestOnBackgroundThread(manager, first);
        var otherResult = await Bounded(other, "unrelated user's GetOrCreateAsync completes while the probe is parked");
        Assert.True(otherResult.IsGranted);
        Assert.Equal(ComposerRuntimeStatus.Deferred, otherResult.Status); // same copilot/no-token gate
        Assert.NotSame(deferred, otherResult.Runtime);
        Assert.False(parked.IsCompleted); // production is genuinely still inside the parked probe

        probe.Release();
        var stillDeferred = await Bounded(parked, "parked owner's deferred connect settles");
        Assert.Equal(ComposerRuntimeStatus.Deferred, stillDeferred.Status);
        Assert.Same(deferred, stillDeferred.Runtime);
        Assert.Null(stillDeferred.Failure);
        Assert.Equal(0, gate.Calls); // the probe said "no usable token": no connect was attempted
        Assert.Equal(0, _clientCreations);
        probe.AssertHealthy();
        probe.Disarm();
        gate.Release(); // the control below really connects: let it through the connect gate

        _tokenAvailable = true;
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Retry(manager, second)).Status);
        Assert.Equal(1, gate.Calls);
    }

    /// <summary>
    /// AC2, SECOND-USER <c>ConnectAsync</c> CELL: while one user's token probe is genuinely parked, a DIFFERENT
    /// user's <see cref="ComposerRuntimeManager.ConnectAsync"/> completes — proving the parked probe holds no
    /// manager lock at all (not merely that creation is unaffected).
    /// </summary>
    /// <remarks>
    /// The parked probe belongs to the SECOND user's connect; the FIRST user's connect is the unrelated caller.
    /// The probe seam parks only the FIRST armed evaluation, so the unrelated user's own probe evaluation passes
    /// straight through and its caller settles on the same Deferred verdict the owner settles on.
    /// </remarks>
    [Fact]
    public async Task ParkedTokenProbe_DoesNotBlockAnotherUsersConnectAsync()
    {
        var (first, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider(authEnabled: true));
        _provider = "copilot";
        _tokenAvailable = false;
        // Both users own a Deferred runtime, so BOTH have a cached entry to connect.
        var owner = (await Request(manager, second)).Runtime!;
        var other = (await Request(manager, first)).Runtime!;
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, second)).Status);
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, first)).Status);

        using var probe = new TokenProbeGate();
        using var restore = ParkTokenProbe(probe);
        await using var gate = Park();
        probe.Arm();

        // The owner's connect evaluates the synchronous probe and BLOCKS there.
        var parked = RetryOnBackgroundThread(manager, second);
        await GuardAsync(probe.Completion, "owner's connect evaluated the token probe");
        Assert.False(parked.IsCompleted);

        // The unrelated user's ConnectAsync must complete while the owner's probe is still parked.
        var otherConnect = RetryOnBackgroundThread(manager, first);
        var otherResult = await Bounded(otherConnect, "unrelated user's ConnectAsync completes while the probe is parked");
        Assert.Equal(ComposerRuntimeStatus.Deferred, otherResult.Status);
        Assert.Same(other, otherResult.Runtime);
        Assert.Null(otherResult.Failure);
        Assert.False(parked.IsCompleted); // production is genuinely still inside the parked probe
        Assert.Equal(0, gate.Calls);    // no connect was attempted by either caller

        // The owner's stale probe result settles Deferred as well: no runtime was handed out from it.
        probe.Release();
        var ownerResult = await Bounded(parked, "parked owner's deferred connect settles");
        Assert.Equal(ComposerRuntimeStatus.Deferred, ownerResult.Status);
        Assert.Same(owner, ownerResult.Runtime);
        Assert.Null(ownerResult.Failure);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, _clientCreations);
        probe.AssertHealthy();

        // Control: with a token both users connect normally.
        _tokenAvailable = true;
        gate.Release();
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Retry(manager, second)).Status);
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Retry(manager, first)).Status);
        Assert.Equal(2, gate.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalStartedDuringTheParkedTokenProbe_KeepsOwnership_RemovedAndNoRuntimeHandedOut(bool shutdown)
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider(authEnabled: true));
        _provider = "copilot";
        _tokenAvailable = false;
        var deferred = (await Request(manager, second)).Runtime!;
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, second)).Status);
        using var probe = new TokenProbeGate();
        using var restore = ParkTokenProbe(probe);
        await using var gate = Park();
        probe.Arm();

        var parked = RetryOnBackgroundThread(manager, second);
        await GuardAsync(probe.Completion, "owner's connect evaluated the token probe");

        // The lifecycle takes ownership WHILE the probe is parked: this can only complete if the probe is not
        // holding the manager gate.
        Task<bool>? removal = null;
        Task lifecycle;
        if (shutdown) lifecycle = ShutdownOnBackgroundThread(manager);
        else { removal = RemoveOnBackgroundThread(manager, second.UserId); lifecycle = removal; }
        await GuardAsync(lifecycle, "removal/shutdown completes while the token probe is parked");
        if (removal is not null) Assert.True(await removal, "the parked owner's runtime was removed");
        Assert.False(parked.IsCompleted);

        // The stale probe result would start a connect: release it with a usable token.
        _tokenAvailable = true;
        probe.Release();
        var result = await Bounded(parked, "stale probe result settles after removal/shutdown");

        Assert.Equal(shutdown ? ComposerRuntimeStatus.Refused : ComposerRuntimeStatus.Removed, result.Status);
        if (shutdown) Assert.Equal(ComposerRuntimeRefusal.ShuttingDown, result.Refusal);
        Assert.Null(result.Runtime); // no runtime is handed out from a stale probe
        Assert.Null(result.Failure);
        Assert.Equal(0, gate.Calls); // and no connect is started on a runtime the lifecycle owns
        Assert.Equal(0, _clientCreations);
        probe.AssertHealthy();
        await GuardAsync(deferred.Composer.TeardownCompletion, "removed runtime's actual teardown");
        Assert.True(ActorCompleted(deferred.Composer));
    }

    [Fact]
    public async Task StaleTokenProbe_AfterRemoveAndRecreate_StartsNoConnectAndHandsOutNoRuntime()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider(authEnabled: true));
        _provider = "copilot";
        _tokenAvailable = false;
        var old = (await Request(manager, second)).Runtime!;
        using var probe = new TokenProbeGate();
        using var restore = ParkTokenProbe(probe);
        await using var gate = Park();
        probe.Arm();

        var parked = RetryOnBackgroundThread(manager, second);
        await GuardAsync(probe.Completion, "owner's connect evaluated the token probe");
        Assert.True(await RemoveOnBackgroundThread(manager, second.UserId));

        // Recreation happens while the stale probe is still parked: the cache entry (and runtime) is NEW.
        probe.Disarm();
        var recreated = (await Request(manager, second)).Runtime!;
        Assert.NotSame(old, recreated);
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, second)).Status);

        _tokenAvailable = true;
        probe.Release();
        var result = await Bounded(parked, "stale probe result settles after removal + recreation");

        Assert.Equal(ComposerRuntimeStatus.Removed, result.Status);
        Assert.Null(result.Runtime); // neither the old nor the new runtime is handed out
        Assert.Equal(0, gate.Calls); // the stale probe started no connect at all
        Assert.False(recreated.Composer.IsConnected); // the recreated runtime is untouched

        // Control: the recreated runtime is the one the manager serves now.
        gate.Release();
        Assert.Same(recreated, (await Request(manager, second)).Runtime);
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Retry(manager, second)).Status);
        Assert.Same(recreated, (await Request(manager, second)).Runtime);
        Assert.Equal(1, gate.Calls);
        Assert.True(recreated.Composer.IsConnected);
        probe.AssertHealthy();
    }

    [Fact]
    public async Task RemovalInProgressDuringTheParkedTokenProbe_RevalidationStartsNoConnectOnTheOwnedRuntime()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider(authEnabled: true));
        _provider = "copilot";
        _tokenAvailable = false;
        var deferred = (await Request(manager, second)).Runtime!;
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, second)).Status);
        using var probe = new TokenProbeGate();
        using var restore = ParkTokenProbe(probe);
        await using var gate = Park();

        // Park the REMOVAL's own teardown at a production-caused milestone (the manager's join of the runtime's
        // teardown), so the entry is genuinely mid-removal — Removing set, its CTS cancelled but NOT yet disposed,
        // and still the cached entry — while the stale probe result is released.
        using var releaseTeardown = new ManualResetEventSlim(false);
        var teardownReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownJoins = 0;
        var teardownTimedOut = 0;
        manager.OnJoiningTeardown = (userId, _) =>
        {
            Assert.Equal(second.UserId, userId);
            if (Interlocked.Increment(ref teardownJoins) != 1) return;
            teardownReached.TrySetResult();
            // Production contains hook failures; record the hang-guard failure in a counter instead.
            if (!releaseTeardown.Wait(TimeSpan.FromSeconds(30))) Interlocked.Exchange(ref teardownTimedOut, 1);
        };

        probe.Arm();
        var parked = RetryOnBackgroundThread(manager, second);
        await GuardAsync(probe.Completion, "owner's connect evaluated the token probe");
        Task<bool>? removal = null;
        try
        {
            removal = RemoveOnBackgroundThread(manager, second.UserId);
            await GuardAsync(teardownReached.Task, "removal reached the runtime's actual teardown join");
            Assert.False(removal.IsCompleted); // the entry is still cached, in the Removing state

            // Release the STALE probe result with a usable token: it must NOT start a connect on a runtime whose
            // lifecycle removal now owns. (A connect would enter the parked connect gate and be counted.)
            _tokenAvailable = true;
            probe.Release();
            var result = await Bounded(parked, "stale probe result settles while removal is in progress");

            Assert.Equal(ComposerRuntimeStatus.Removed, result.Status);
            Assert.Null(result.Runtime);
            Assert.Equal(0, gate.Calls); // revalidated: !Removing failed ⇒ no shared connect was started
        }
        finally
        {
            releaseTeardown.Set();
            if (removal is not null) await GuardAsync(removal, "removal completes after its teardown join is released");
            Assert.True(Volatile.Read(ref teardownTimedOut) == 0, "Timed out: release parked removal teardown");
        }

        Assert.True(await removal!);
        await GuardAsync(deferred.Composer.TeardownCompletion, "removed runtime's actual teardown");
        Assert.True(ActorCompleted(deferred.Composer));
        Assert.Equal(0, _clientCreations);
        probe.AssertHealthy();
    }

    [Fact]
    public async Task ConcurrentSameUserConnects_ProbedSimultaneouslyOutsideTheGate_StartExactlyOneConnect()
    {
        const int callers = 6;
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider(authEnabled: true));
        _provider = "copilot";
        _tokenAvailable = false;
        var deferred = (await Request(manager, second)).Runtime!;
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, second)).Status);
        using var probe = new TokenProbeGate(expectedParked: callers);
        using var restore = ParkTokenProbe(probe);
        await using var gate = Park();
        probe.Arm();

        var connets = Enumerable.Range(0, callers).Select(_ => RetryOnBackgroundThread(manager, second)).ToArray();
        // Every caller evaluates the probe OUTSIDE the gate, so all of them park simultaneously.
        await GuardAsync(probe.AllEntered, "all concurrent connect callers evaluated the token probe");
        Assert.All(connets, task => Assert.False(task.IsCompleted));

        _tokenAvailable = true;
        probe.Release();
        // Exactly ONE of them may start the shared connect: it parks in the connect gate, and none can finish.
        await GuardAsync(gate.Entered.Task, "exactly one shared connect started");
        Assert.All(connets, task => Assert.False(task.IsCompleted));
        Assert.Equal(1, gate.Calls); // no duplicate same-user connect
        Assert.Equal(callers, probe.Calls); // every caller really did probe (outside the lock)

        gate.Release();
        var results = await Bounded(Task.WhenAll(connets), "all concurrent connect callers settle");
        Assert.All(results, result =>
        {
            Assert.Equal(ComposerRuntimeStatus.Connected, result.Status);
            Assert.Same(deferred, result.Runtime);
        });
        Assert.Equal(1, gate.Calls);
        Assert.Equal(1, _clientCreations);
        Assert.True(deferred.Composer.IsConnected);
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Retry(manager, second)).Status);
        Assert.Equal(1, gate.Calls); // settled: no reconnect
        probe.AssertHealthy();
    }

    /// <summary>
    /// Starts <see cref="ComposerRuntimeManager.ConnectAsync"/> on a dedicated thread. The factory's token probe is
    /// SYNCHRONOUS, so a parked probe blocks whichever thread runs the call: only a genuine background caller can
    /// prove the manager does not hold its gate across it (and lets a bounded guard fail loudly instead of hanging).
    /// </summary>
    private Task<ComposerRuntimeResult> RetryOnBackgroundThread(ComposerRuntimeManager manager, ActingUserPrincipal principal)
    {
        _managerUsers[manager].Add(principal.UserId);
        var task = Task.Factory.StartNew(() => manager.ConnectAsync(principal, Ct), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _requests.Add(task); // registered from THIS thread, before any assertion can throw
        return task;
    }

    /// <summary>Starts <see cref="ComposerRuntimeManager.GetOrCreateAsync"/> on a dedicated thread (see above).</summary>
    private Task<ComposerRuntimeResult> RequestOnBackgroundThread(ComposerRuntimeManager manager, ActingUserPrincipal principal)
    {
        _managerUsers[manager].Add(principal.UserId);
        var task = Task.Factory.StartNew(() => manager.GetOrCreateAsync(principal, Ct), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _requests.Add(task);
        return task;
    }

    /// <summary>Starts <see cref="ComposerRuntimeManager.RemoveAsync"/> on a dedicated thread (see above).</summary>
    private Task<bool> RemoveOnBackgroundThread(ComposerRuntimeManager manager, int userId)
    {
        var task = Task.Factory.StartNew(() => manager.RemoveAsync(userId), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _removals.Add(task);
        return task;
    }

    /// <summary>Starts <see cref="ComposerRuntimeManager.ShutdownAsync"/> on a dedicated thread (see above).</summary>
    private Task ShutdownOnBackgroundThread(ComposerRuntimeManager manager) =>
        Task.Factory.StartNew(manager.ShutdownAsync, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private static bool ActorCompleted(Composer composer) => Field<ComposerActor>(composer, "_actor").IsCompleted;

    private static async Task<T> Bounded<T>(Task<T> task, string milestone)
    {
        await GuardAsync(task, milestone);
        return await task;
    }

    // ── Typed outcomes: never "the connect was started" ──

    [Fact]
    public async Task Create_CompletesOnlyAfterConnectSettles_AndReportsConnected()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        await using var gate = Park();

        var creator = Request(manager, second);
        await GuardAsync(gate.Entered.Task, "manager entered connect");
        Assert.False(creator.IsCompleted); // a started connect is NOT a completed request
        gate.Release();
        var result = await Bounded(creator, "connected creator");

        Assert.Equal(ComposerRuntimeStatus.Connected, result.Status);
        Assert.True(result.Runtime!.Composer.IsConnected);
        Assert.Null(result.Failure);
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task ModelLessComposer_CompletesAsExplicitShell_WithoutAnyConnectOrClient()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider(config: new HiveConfigFile()));
        await using var gate = Park();

        var result = await Request(manager, second);

        Assert.Equal(ComposerRuntimeStatus.DisconnectedShell, result.Status);
        Assert.True(result.IsGranted);
        Assert.False(result.Runtime!.Composer.IsConnected);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, _clientCreations);
        Assert.Same(result.Runtime, (await Request(manager, second)).Runtime); // cached and shared like any runtime
        Assert.Equal(0, gate.Calls);
        var retry = await Retry(manager, second);
        Assert.Equal(ComposerRuntimeStatus.DisconnectedShell, retry.Status);
        Assert.Same(result.Runtime, retry.Runtime);
        Assert.Null(retry.Failure);
        Assert.Equal(0, gate.Calls);
    }

    [Fact]
    public async Task CopilotModelWithoutToken_UnderAuth_CompletesAsDeferred_ThenConnectAsyncRetriesAwaitably()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider(authEnabled: true));
        _provider = "copilot";
        _tokenAvailable = false;
        await using var gate = Park();

        var deferred = await Request(manager, second);

        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        Assert.True(deferred.IsGranted);
        Assert.False(deferred.Runtime!.Composer.IsConnected);
        Assert.Equal(0, gate.Calls); // deferral is an explicit state, not a started connect
        Assert.Equal(0, _clientCreations);

        // Still no token: the retry stays Deferred and attempts no connect.
        var stillDeferred = await Retry(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Deferred, stillDeferred.Status);
        Assert.Same(deferred.Runtime, stillDeferred.Runtime);
        Assert.Equal(0, gate.Calls);

        _tokenAvailable = true;
        var retry = Retry(manager, second);
        await GuardAsync(gate.Entered.Task, "deferred retry entered connect");
        Assert.False(retry.IsCompleted); // awaitable: completes only once the connect settled
        var concurrent = Retry(manager, second); // joins the ONE in-flight connect
        Assert.False(concurrent.IsCompleted);
        gate.Release();
        var results = await Bounded(Task.WhenAll(retry, concurrent), "deferred connect callers");

        Assert.All(results, r => Assert.Equal(ComposerRuntimeStatus.Connected, r.Status));
        Assert.All(results, r => Assert.Same(deferred.Runtime, r.Runtime));
        Assert.True(deferred.Runtime.Composer.IsConnected);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Retry(manager, second)).Status);
        Assert.Equal(1, gate.Calls); // settled: no reconnect
    }

    [Fact]
    public async Task CopilotModelWithoutToken_InOpenMode_IsNotDeferred_ConnectIsAttempted()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider(authEnabled: false));
        _provider = "copilot";
        _tokenAvailable = false;
        await using var gate = Park();
        gate.Release();

        var result = await Request(manager, second);

        Assert.Equal(ComposerRuntimeStatus.Connected, result.Status);
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task ConnectAsync_WithoutCachedRuntime_IsRefusedNoRuntime()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());

        var result = await Retry(manager, second);

        Assert.Equal(ComposerRuntimeStatus.Refused, result.Status);
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, result.Refusal);
        Assert.Equal(0, _clientCreations);
    }

    // ── Connect faults: typed failure AFTER cleanup, teardown before recreation ──

    [Fact]
    public async Task InitialConnectFault_SharedByJoiner_ReturnsTypedFailureAfterCleanup_AndRecreationIsFresh()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var sentinel = new InvalidOperationException("deliberate connect fault");
        await using var gate = Park();
        gate.FailWith = sentinel;

        var creator = Request(manager, second);
        await GuardAsync(gate.Entered.Task, "manager entered connect");
        var joiner = Request(manager, second); // shares the SAME operation: no second runtime
        Assert.False(joiner.IsCompleted);
        Assert.Equal(1, _bus.SubscriberCount);
        gate.Release();
        var results = await Bounded(Task.WhenAll(creator, joiner), "failed creator and joiner");

        Assert.All(results, r =>
        {
            Assert.Equal(ComposerRuntimeStatus.Failed, r.Status);
            Assert.Same(sentinel, r.Failure);
            Assert.Null(r.Runtime);
        });
        Assert.Equal(1, gate.Calls);
        Assert.True(ActorCompleted(gate.Composer0)); // actually torn down, not merely forgotten
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll());

        _connectHook = null;
        var fresh = await Request(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Connected, fresh.Status);
        Assert.NotSame(gate.Composer0, fresh.Runtime!.Composer);
    }

    [Fact]
    public async Task FailedDeferredConnect_DisposesAndRemovesRuntimeBeforeAnyRecreationCanStart()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        _provider = "copilot";
        _tokenAvailable = false;
        var deferred = (await Request(manager, second)).Runtime!;
        Assert.Equal(0, _clientCreations);

        var sentinel = new InvalidOperationException("deliberate deferred connect fault");
        _tokenAvailable = true;
        await using var gate = Park();
        gate.FailWith = sentinel;

        // Production-caused milestone INSIDE the failure cleanup: the partial subscriber is being detached,
        // which happens after the Composer disposal and before the cache entry is dropped.
        var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cleanupRelease = new ManualResetEventSlim(false);
        var unsubscribeCalls = 0;
        var cleanupGateTimedOut = 0;
        _bus.OnUnsubscribe = () =>
        {
            if (Interlocked.Increment(ref unsubscribeCalls) != 1) return;
            cleanupReached.TrySetResult();
            // Production catches subscriber-disposal exceptions. Record the hang-guard failure
            // independently so that it cannot be swallowed into the expected connect sentinel.
            if (!cleanupRelease.Wait(TimeSpan.FromSeconds(30)))
                Interlocked.Exchange(ref cleanupGateTimedOut, 1);
        };

        Task<ComposerRuntimeResult>? retry = null;
        try
        {
            retry = Retry(manager, second);
            await GuardAsync(gate.Entered.Task, "deferred retry entered connect");
            gate.Release();
            await GuardAsync(cleanupReached.Task, "failure cleanup reached subscriber disposal");
            // Mid-cleanup: the faulted instance is already disposed ...
            Assert.True(ActorCompleted(deferred.Composer));
            // ... but its cache entry is still present, so a recreation attempt JOINS the failing operation
            // instead of building an overlapping second runtime.
            var overlapping = Request(manager, second);
            Assert.False(overlapping.IsCompleted);

            cleanupRelease.Set();
            var settled = await Bounded(Task.WhenAll(retry, overlapping), "failed deferred retry and overlapping request");
            Assert.All(settled, r =>
            {
                Assert.Equal(ComposerRuntimeStatus.Failed, r.Status);
                Assert.Same(sentinel, r.Failure);
            });
            // Counted AFTER both requests settled (no timing window): had the entry been dropped before the
            // faulted instance finished tearing down, the overlapping request would have built and connected
            // a SECOND runtime (a second connect call, a second client) instead of joining the failing one.
            Assert.Equal(1, gate.Calls);
            Assert.Equal(0, _clientCreations);
        }
        finally
        {
            cleanupRelease.Set();
            gate.Release();
            _bus.OnUnsubscribe = null;
            if (retry is not null) await JoinRequestAsync(retry);
            Assert.True(Volatile.Read(ref cleanupGateTimedOut) == 0, "Timed out: release parked failure cleanup");
        }

        Assert.Equal(0, _bus.SubscriberCount);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll());
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, second)).Refusal); // removed from cache

        _connectHook = null;
        var recreated = await Request(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Connected, recreated.Status);
        Assert.NotSame(deferred, recreated.Runtime);
        Assert.NotSame(deferred.Composer, recreated.Runtime!.Composer);
    }

    // ── Cancellation ownership ──

    [Fact]
    public async Task CancelledWaiters_DoNotCancelTheSharedConnect_AndAConcurrentWaiterStillGetsTheSharedResult()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        await using var gate = Park();
        using var creatorCts = new CancellationTokenSource();
        using var waiterCts = new CancellationTokenSource();

        var creator = Request(manager, second, creatorCts.Token); // creates the shared operation
        await GuardAsync(gate.Entered.Task, "manager entered connect");
        var cancelledWaiter = Request(manager, second, waiterCts.Token);
        var survivor = Request(manager, second);
        Assert.False(cancelledWaiter.IsCompleted);
        Assert.False(survivor.IsCompleted);

        await creatorCts.CancelAsync();
        await waiterCts.CancelAsync();
        var creatorCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(creator, "cancelled creator wait"));
        var waiterCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(cancelledWaiter, "cancelled waiter wait"));
        Assert.Equal(creatorCts.Token, creatorCancellation.CancellationToken);
        Assert.Equal(waiterCts.Token, waiterCancellation.CancellationToken);

        // Cancelled callers ended only their own WAIT: the shared connect is untouched and still parked.
        Assert.False(gate.ObservedToken.IsCancellationRequested);
        Assert.False(gate.CancelObserved.Task.IsCompleted);
        Assert.False(survivor.IsCompleted);
        Assert.Equal(1, _bus.SubscriberCount);

        gate.Release();
        var result = await Bounded(survivor, "surviving waiter");

        Assert.Equal(ComposerRuntimeStatus.Connected, result.Status);
        Assert.True(result.Runtime!.Composer.IsConnected);
        // The cache entry survived the cancelled callers: a later call shares the same runtime, no reconnect.
        var later = await Request(manager, second);
        Assert.Same(result.Runtime, later.Runtime);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(1, _clientCreations);
        Assert.Equal(1, _bus.SubscriberCount);
    }

    [Fact]
    public async Task CancelledDeferredConnectCaller_DoesNotCancelTheSharedDeferredConnect()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        _provider = "copilot";
        _tokenAvailable = false;
        var deferred = (await Request(manager, second)).Runtime!;
        _tokenAvailable = true;
        await using var gate = Park();
        using var cts = new CancellationTokenSource();

        var cancelling = Retry(manager, second, cts.Token);
        await GuardAsync(gate.Entered.Task, "deferred connect entered");
        var survivor = Retry(manager, second);
        Assert.False(survivor.IsCompleted);
        await cts.CancelAsync();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(cancelling, "cancelled deferred caller"));
        Assert.Equal(cts.Token, cancellation.CancellationToken);

        Assert.False(gate.ObservedToken.IsCancellationRequested);
        Assert.False(gate.CancelObserved.Task.IsCompleted);
        Assert.False(survivor.IsCompleted);
        var cachedWhileInFlight = Request(manager, second);
        Assert.False(cachedWhileInFlight.IsCompleted);
        gate.Release();
        var result = await Bounded(survivor, "surviving deferred caller");

        Assert.Equal(ComposerRuntimeStatus.Connected, result.Status);
        Assert.Same(deferred, result.Runtime);
        Assert.Same(deferred, (await Bounded(cachedWhileInFlight, "cached waiter after cancellation")).Runtime);
        Assert.Same(deferred, (await Request(manager, second)).Runtime);
        Assert.True(deferred.Composer.IsConnected);
        await deferred.Composer.SaveSessionAsync(Ct); // usable, not just a retained reference
        Assert.Equal(1, _clientCreations);
        Assert.Equal(1, gate.Calls);
    }

    // ── Removal owns the shared operation ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_CancelsTheSharedOperation_AdmitsNoNewWaiters_AndTearsTheRuntimeDown(bool deferred)
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        if (deferred)
        {
            _provider = "copilot";
            _tokenAvailable = false;
            Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, second)).Status);
            _tokenAvailable = true;
        }
        await using var gate = Park();

        var creator = deferred ? Retry(manager, second) : Request(manager, second);
        await GuardAsync(gate.Entered.Task, "manager entered connect");
        var joinedWaiter = Request(manager, second);
        Assert.False(creator.IsCompleted);
        Assert.False(joinedWaiter.IsCompleted);
        Assert.False(gate.ObservedToken.IsCancellationRequested);

        var removal = Remove(manager, second.UserId);
        await GuardAsync(gate.CancelObserved.Task, "removal cancelled the shared operation");
        Assert.True(gate.ObservedToken.IsCancellationRequested);

        // Removing is set BEFORE the cancellation fired: no new waiter joins, and no connect/create starts.
        var lateCreateCall = Request(manager, second);
        var lateConnectCall = Retry(manager, second);
        Assert.True(lateCreateCall.IsCompletedSuccessfully, "Removing entry must refuse create synchronously, not join the doomed operation");
        Assert.True(lateConnectCall.IsCompletedSuccessfully, "Removing entry must refuse connect synchronously, not join the doomed operation");
        var lateCreate = await Bounded(lateCreateCall, "late create refused during removal");
        var lateConnect = await Bounded(lateConnectCall, "late connect refused during removal");
        Assert.Equal(ComposerRuntimeStatus.Removed, lateCreate.Status);
        Assert.Equal(ComposerRuntimeStatus.Removed, lateConnect.Status);
        Assert.Null(lateCreate.Runtime);
        Assert.Null(lateConnect.Runtime);
        Assert.Null(lateCreate.Failure);
        Assert.Null(lateConnect.Failure);
        Assert.False(await Remove(manager, second.UserId)); // a second removal does not double-own
        Assert.Equal(1, gate.Calls); // the late callers started no second operation
        Assert.False(removal.IsCompleted); // removal waits for the shared operation to settle

        gate.Release();
        Assert.True(await Bounded(removal, "removal"));
        var waiters = await Bounded(Task.WhenAll(creator, joinedWaiter), "removed waiters");

        Assert.All(waiters, r =>
        {
            Assert.Equal(ComposerRuntimeStatus.Removed, r.Status);
            Assert.Null(r.Runtime);
        });
        Assert.True(ActorCompleted(gate.Composer0));
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll());

        _connectHook = null;
        var fresh = await Request(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Connected, fresh.Status);
        Assert.NotSame(gate.Composer0, fresh.Runtime!.Composer);
    }

    [Fact]
    public async Task Removal_OfAConnectedRuntime_DisposesItAndAllowsAFreshRuntime()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var before = (await Request(manager, second)).Runtime!;
        Assert.False(await Remove(manager, 12345)); // nothing to remove

        Assert.True(await Remove(manager, second.UserId));

        Assert.True(ActorCompleted(before.Composer));
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll());
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, second)).Refusal);
        var after = await Request(manager, second);
        Assert.NotSame(before, after.Runtime);
        Assert.Equal(2, _clientCreations);
    }

    // ── No global coordinator involvement ──

    [Fact]
    public async Task ManagedPaths_NeverReachTheLegacyCoordinatorOrTokenAvailable()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider(authEnabled: true);
        var manager = NewManager(sp);
        _provider = "copilot";
        _tokenAvailable = false;

        // A real legacy coordinator in PendingConnect with sentinel hooks, wired to the real
        // UserService.TokenAvailable signal exactly like Program does.
        var legacyConnects = 0;
        var legacyProbes = 0;
        var legacyStates = new List<ComposerState>();
        await using var coordinator = new LlmConnectionCoordinator(
            Model, null, oauthEnabled: true,
            _ => { Interlocked.Increment(ref legacyConnects); return Task.CompletedTask; },
            () => { Interlocked.Increment(ref legacyProbes); return false; },
            _ => "copilot");
        coordinator.SubscribeTokenSignal(h => _users.TokenAvailable += h, h => _users.TokenAvailable -= h);
        await coordinator.StartAsync();
        Assert.Equal(ComposerState.PendingConnect, coordinator.State);
        coordinator.StateChanged += legacyStates.Add;
        var probesAfterStart = Volatile.Read(ref legacyProbes);
        var tokenSignals = 0;
        _users.TokenAvailable += () => Interlocked.Increment(ref tokenSignals);

        // Managed create (deferred) → retry (connect) → failure path → removal.
        var deferred = await Request(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        _tokenAvailable = true;
        await using var gate = Park();
        gate.FailWith = new InvalidOperationException("deliberate");
        gate.Release();
        Assert.Equal(ComposerRuntimeStatus.Failed, (await Retry(manager, second)).Status);
        _connectHook = null;
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Request(manager, second)).Status);
        Assert.True(await Remove(manager, second.UserId));

        Assert.Equal(0, Volatile.Read(ref legacyConnects));
        Assert.Equal(probesAfterStart, Volatile.Read(ref legacyProbes));
        Assert.Equal(ComposerState.PendingConnect, coordinator.State);
        Assert.Empty(legacyStates);
        Assert.Equal(0, Volatile.Read(ref tokenSignals));
    }

    [Fact]
    public void ManagerAndFactorySources_ReferenceTheCoordinatorOnlyThroughItsPureStaticPredicate()
    {
        var root = TestRepoRoot.Find();
        foreach (var file in new[] { "ComposerRuntimeManager.cs", "ComposerFactory.cs" })
        {
            var code = File.ReadAllLines(Path.Combine(root, "src", "CopilotHive", "Orchestration", file))
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("//", StringComparison.Ordinal))
                .ToArray();

            Assert.DoesNotContain(code, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"(?<![A-Za-z_])TokenAvailable\b"));
            Assert.DoesNotContain(code, l => l.Contains("new LlmConnectionCoordinator", StringComparison.Ordinal));
            Assert.DoesNotContain(code, l => l.Contains("<LlmConnectionCoordinator>", StringComparison.Ordinal));
            Assert.DoesNotContain(code, l => l.Contains("SubscribeTokenSignal", StringComparison.Ordinal));
            foreach (var use in code.Where(l => l.Contains("LlmConnectionCoordinator", StringComparison.Ordinal)))
                Assert.Contains("LlmConnectionCoordinator.ShouldDefer(", use);
        }
    }

    [Theory]
    [InlineData(true, "copilot", false, null, true)]
    [InlineData(true, "copilot", true, null, false)]
    [InlineData(false, "copilot", false, null, false)]
    [InlineData(true, "ollama-local", false, null, false)]
    [InlineData(true, "ollama-local", false, "copilot-compaction", true)]
    public async Task StaticDeferralPredicate_MatchesTheInstanceGate(
        bool oauth, string provider, bool token, string? compaction, bool expected)
    {
        Func<string, string> providerOf = m => m == "copilot-compaction" ? "copilot" : provider;
        Assert.Equal(expected, LlmConnectionCoordinator.ShouldDefer(oauth, "primary", compaction, () => token, providerOf, null));
        await using var instance = new LlmConnectionCoordinator("primary", compaction, oauth, _ => Task.CompletedTask, () => token, providerOf);
        Assert.Equal(expected, instance.ShouldDefer());
    }
}
