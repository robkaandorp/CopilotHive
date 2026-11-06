using CopilotHive.Actors;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
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
