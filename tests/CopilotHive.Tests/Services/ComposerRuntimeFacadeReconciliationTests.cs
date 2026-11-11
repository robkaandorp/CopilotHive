using System.Security.Claims;
using System.Runtime.ExceptionServices;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE LIVE-STATE RECONCILIATION of the recovery facade: the facade has NO state machine of its
/// own — it acts on the status the MANAGER reports, and the manager reconciles a cached
/// <see cref="ComposerRuntimeStatus.Deferred"/> or <see cref="ComposerRuntimeStatus.DisconnectedShell"/>
/// against the runtime's actual <c>IsConnected</c> state.
/// <para>
/// The rows here run over the REAL manager, the REAL accessor and a REAL per-user runtime, so
/// "the facade does not issue a duplicate connect" and "a removed runtime is replaced
/// deterministically" are observed on the objects production hands out — not on a substitute.
/// </para>
/// </summary>
public sealed class ComposerRuntimeFacadeReconciliationTests : IAsyncLifetime, IDisposable
{
    private const string Model = "reconcile-model";
    private const string OtherModel = "reconcile-other-model";
    private const string OriginalId = "reconcile-original";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _goalDb = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-reconcile-{Guid.NewGuid():N}");
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<ComposerRuntimeManager> _managers = [];
    private readonly List<Task> _requests = [];

    private volatile bool _tokenAvailable = true;
    private int _originalId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Original { get; } = FacadeAuthorizationHarness.Principal(OriginalId);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _originalId = (await _auth.SeedAsync(OriginalId, UserRole.Member)).Id;
    }

    // ── 1. A cached DEFERRED superseded by the live state ──────────────────────────────────────

    /// <summary>
    /// A CACHED DEFERRED IS SUPERSEDED BY THE LIVE STATE: the runtime connects through the actor's
    /// first-selection path, and the manager then reports <see cref="ComposerRuntimeStatus.Connected"/>
    /// for the SAME runtime — the facade never issues a connect of its own to discover that.
    /// </summary>
    [Fact]
    public async Task CachedDeferred_IsSupersededByTheLiveState_WithoutAFacadeConnect()
    {
        // The credential is missing BEFORE the runtime is built: a Copilot-backed primary then
        // settles DEFERRED (the token probe is evaluated at construction).
        _tokenAvailable = false;
        var (facade, runtime, connects) = await BuildAsync(
            config: Config(Model),
            provider: _ => "copilot",
            tokenAvailable: () => _tokenAvailable);

        // The runtime settles DEFERRED (a Copilot-backed primary without a usable credential).
        var deferred = await ReacquireAsync(runtime.Accessor);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        Assert.NotNull(deferred.Runtime);

        // The actor's first-selection path connects the runtime.
        await deferred.Runtime!.Composer.SwitchModelAsync(OtherModel, ReasoningEffort.Medium, Ct);
        Assert.True(deferred.Runtime.Composer.IsConnected);

        // The MANAGER reconciles the cached outcome: the same runtime is now reported CONNECTED.
        var reconciled = await ReacquireAsync(runtime.Accessor);
        Assert.Equal(ComposerRuntimeStatus.Connected, reconciled.Status);
        Assert.Same(deferred.Runtime, reconciled.Runtime);

        // ...and the facade serves the operation on it with NO connect of its own.
        var read = await facade.GetCurrentModelAsync(Original, Ct);
        Assert.True(read.Success, read.Error ?? "read failed");
        Assert.Equal(0, connects.Calls);
    }

    /// <summary>
    /// A CACHED SHELL IS SUPERSEDED BY THE LIVE STATE: a model-less shell becomes connected through
    /// the actor's first selection, and the manager then reports
    /// <see cref="ComposerRuntimeStatus.Connected"/> — never the stale shell status.
    /// </summary>
    [Fact]
    public async Task CachedShell_IsSupersededByTheLiveState_WithoutAFacadeConnect()
    {
        var (facade, runtime, connects) = await BuildAsync(
            config: Config(primary: null),
            provider: _ => "ollama-local");

        var shell = await ReacquireAsync(runtime.Accessor);
        Assert.Equal(ComposerRuntimeStatus.DisconnectedShell, shell.Status);
        Assert.NotNull(shell.Runtime);
        Assert.False(shell.Runtime!.Composer.IsConnected);

        // The switch PROCEEDS against the shell and the actor connects it.
        var switched = await facade.SwitchModelAsync(Original, OtherModel, "medium", Ct);
        Assert.True(switched.Success, switched.Error ?? "shell switch failed");
        Assert.Equal(OtherModel, switched.Value!.Model);
        Assert.True(shell.Runtime.Composer.IsConnected);

        // The manager now reports CONNECTED for the SAME runtime.
        var reconciled = await ReacquireAsync(runtime.Accessor);
        Assert.Equal(ComposerRuntimeStatus.Connected, reconciled.Status);
        Assert.Same(shell.Runtime, reconciled.Runtime);

        // No manager connect was ever issued: the actor's path was the one that connected it.
        Assert.Equal(0, connects.Calls);
    }

    // ── 2. The operation-driven deferred → connected transition ─────────────────────────────────

    /// <summary>
    /// THE OPERATION-DRIVEN DEFERRED → CONNECTED TRANSITION: a mutation on a deferred runtime
    /// drives the manager's connect exactly ONCE, and when the credential is available the mutation
    /// proceeds on the now-live runtime. The accessor's connect count proves the single attempt.
    /// </summary>
    [Fact]
    public async Task OperationDrivenDeferred_BecomesConnected_AfterExactlyOneConnectAttempt()
    {
        _tokenAvailable = false;
        var (facade, runtime, connects) = await BuildAsync(
            config: Config(Model),
            provider: _ => "copilot",
            tokenAvailable: () => _tokenAvailable);

        var deferred = await ReacquireAsync(runtime.Accessor);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);

        // The credential arrives: the operation's own connect now completes the runtime.
        _tokenAvailable = true;
        var switched = await facade.SwitchModelAsync(Original, OtherModel, "high", Ct);

        Assert.True(switched.Success, switched.Error ?? "deferred mutation failed");
        Assert.Equal(1, runtime.Accessor.ConnectCalls);
        Assert.Equal(1, connects.Calls);
        Assert.True(deferred.Runtime!.Composer.IsConnected);
        Assert.Equal(OtherModel, switched.Value!.Model);

        // The runtime is LIVE now: a SECOND mutation must not issue a duplicate connect.
        var again = await facade.SwitchModelAsync(Original, Model, "medium", Ct);
        Assert.True(again.Success, again.Error ?? "second mutation failed");
        Assert.Equal(1, runtime.Accessor.ConnectCalls);
        Assert.Equal(1, connects.Calls);
    }

    /// <summary>
    /// A STILL-DEFERRED RUNTIME AFTER THE OPERATION'S OWN CONNECT ATTEMPT answers the actionable
    /// <see cref="FacadeErrorKind.ServiceUnavailable"/> — and the attempt happened EXACTLY ONCE.
    /// </summary>
    [Fact]
    public async Task OperationDrivenDeferred_StillDeferred_AfterOneAttempt_AnswersServiceUnavailable()
    {
        _tokenAvailable = false;
        var (facade, runtime, connects) = await BuildAsync(
            config: Config(Model),
            provider: _ => "copilot",
            tokenAvailable: () => _tokenAvailable);

        var deferred = await ReacquireAsync(runtime.Accessor);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);

        var compacted = await facade.CompactAsync(Original, Ct);

        Assert.False(compacted.Success);
        Assert.Equal(FacadeErrorKind.ServiceUnavailable, compacted.Kind);
        Assert.Equal(ComposerRuntimeFacade.RuntimeDeferredError, compacted.Error);
        Assert.Equal(1, runtime.Accessor.ConnectCalls);
        // The credential was still missing, so the manager really started no connect.
        Assert.Equal(0, connects.Calls);
        Assert.False(deferred.Runtime!.Composer.IsConnected);
    }

    // ── 3. The deterministic removed/recreated handle replacement ───────────────────────────────

    /// <summary>
    /// A REMOVED RUNTIME IS REPLACED DETERMINISTICALLY: after the manager removed the runtime, the
    /// facade's next operation re-queries the manager, gets the CREATED runtime and serves the
    /// operation on it — with no disposal performed by the facade.
    /// </summary>
    [Fact]
    public async Task RemovedRuntime_IsReplacedDeterministically_AndTheFacadeDisposesNothing()
    {
        var (facade, runtime, connects) = await BuildAsync(
            config: Config(Model),
            provider: _ => "ollama-local");

        var first = await ReacquireAsync(runtime.Accessor);
        Assert.Equal(ComposerRuntimeStatus.Connected, first.Status);
        var originalComposer = first.Runtime!.Composer;

        // The manager removes the runtime (the sole disposal path).
        Assert.True(await runtime.Manager.RemoveAsync(_originalId));

        // The next operation re-queries the manager: the runtime is recreated and served.
        var read = await facade.GetCurrentModelAsync(Original, Ct);

        Assert.True(read.Success, read.Error ?? "read after removal failed");
        var recreated = await ReacquireAsync(runtime.Accessor);
        Assert.Equal(ComposerRuntimeStatus.Connected, recreated.Status);
        Assert.NotSame(originalComposer, recreated.Runtime!.Composer);

        // The facade never disposed anything: the NEW runtime is owned by the manager (the entry is
        // still cached under the same account) and the facade issued no connect of its own.
        Assert.Contains(_originalId, ManagerEntries(runtime.Manager));
        Assert.Equal(0, runtime.Accessor.ConnectCalls);
    }

    /// <summary>
    /// A REMOVAL THAT IS STILL IN FLIGHT IS REPLACED DETERMINISTICALLY: the facade's operation
    /// re-queries the manager, which WAITS for the old teardown and then creates a fresh runtime —
    /// so the operation is served by a NEW runtime, never by the removed one and never by a
    /// conflict.
    /// <para>
    /// THE TEARDOWN IS REALLY HELD INCOMPLETE: the old Composer's client async disposal is awaiting
    /// an explicit release (without blocking a thread),
    /// so the entry provably stays in the Removing state and the old
    /// <see cref="Composer.TeardownCompletion"/> stays pending. The manager's
    /// <c>OnJoiningTeardown</c> hook only SIGNALS (it runs on the teardown's own thread, so blocking
    /// there would deadlock the manager); the facade call's join of the ENTRY teardown is observed
    /// through it, the operation is asserted PENDING while the hold is closed, and only then is the
    /// hold released. Every exit path — including a failed assertion — releases the hold and joins
    /// every owned task in the <c>finally</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RemovalInFlight_IsReplacedDeterministically_AndTheFacadeDisposesNothing()
    {
        await using var hold = new RuntimeAsyncDisposalHold();
        var clientCreations = 0;
        var (facade, runtime, _) = await BuildAsync(
            config: Config(Model),
            provider: _ => "ollama-local",
            chatClientFactory: _ => Interlocked.Increment(ref clientCreations) == 1
                ? hold : new Mock<IChatClient>().Object);

        var first = await ReacquireAsync(runtime.Accessor);
        Assert.Equal(ComposerRuntimeStatus.Connected, first.Status);
        var originalComposer = first.Runtime!.Composer;


        var joins = new JoinSignal(originalComposer.TeardownCompletion);
        runtime.Manager.OnJoiningTeardown = joins.Record;

        Task<bool>? removal = null;
        Task<FacadeResult<CurrentModelDto>>? pending = null;
        ExceptionDispatchInfo? primary = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            hold.Arm();
            removal = runtime.Manager.RemoveAsync(_originalId);
            _requests.Add(removal);

            // The REAL teardown is now awaiting the old client's async disposal and cannot finish.
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(removal.IsCompleted, "the removal must still be in flight");
            Assert.False(originalComposer.TeardownCompletion.IsCompleted, "the old teardown must still be pending");

            // The facade's operation starts while the removal is in flight: its manager call joins the
            // ENTRY teardown (not the Composer's own completion task) and must stay pending.
            pending = facade.GetCurrentModelAsync(Original, Ct);
            _requests.Add(pending);
            await joins.EntryJoined.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.False(pending.IsCompleted, "the operation must wait for the in-flight teardown");
            Assert.False(removal.IsCompleted);
            Assert.False(originalComposer.TeardownCompletion.IsCompleted);
            Assert.False(joins.EntryTask!.IsCompleted, "the joined entry teardown must still be pending");
            // The facade started no connect and disposed nothing while it waited.
            Assert.Equal(0, runtime.Accessor.ConnectCalls);

            // Release the held teardown: the manager's replacement path may now proceed.
            hold.Release();

            var read = await pending.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.True(read.Success, read.Error ?? "read during removal failed");
            Assert.True(await removal.WaitAsync(TimeSpan.FromSeconds(60), Ct));
            Assert.True(originalComposer.TeardownCompletion.IsCompletedSuccessfully);

            var recreated = await ReacquireAsync(runtime.Accessor);
            Assert.Equal(ComposerRuntimeStatus.Connected, recreated.Status);
            Assert.NotSame(originalComposer, recreated.Runtime!.Composer);

            // The facade disposed nothing and issued no connect of its own.
            Assert.Equal(0, runtime.Accessor.ConnectCalls);
            hold.AssertHealthy();
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            // Release BEFORE observing any owned task; attempt EVERY join, retaining the primary fault.
            hold.Release();
            runtime.Manager.OnJoiningTeardown = null;
            foreach (var task in new Task?[] { removal, pending, originalComposer.TeardownCompletion })
            {
                if (task is null) continue;
                try { await task.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None); }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
        }

        if (primary is not null)
        {
            if (cleanupFailures.Count > 0) primary.SourceException.Data["CleanupFailures"] = cleanupFailures;
            primary.Throw();
        }
        Assert.Empty(cleanupFailures);
        Assert.True(removal!.IsCompletedSuccessfully);
        Assert.True(pending!.IsCompletedSuccessfully);
        Assert.True(originalComposer.TeardownCompletion.IsCompletedSuccessfully);
        hold.AssertHealthy();
    }

    /// <summary>
    /// Signals when the manager joins an ENTRY teardown (a task other than the Composer's own
    /// completion). It only records and signals: the manager invokes it on its teardown/caller path, so
    /// it must never block.
    /// </summary>
    private sealed class JoinSignal(Task composerTeardown)
    {
        private Task? _entryTask;

        public TaskCompletionSource EntryJoined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? EntryTask => Volatile.Read(ref _entryTask);

        public void Record(int userId, Task teardown)
        {
            if (ReferenceEquals(teardown, composerTeardown))
                return;

            Volatile.Write(ref _entryTask, teardown);
            EntryJoined.TrySetResult();
        }
    }

    // ── Fixture ────────────────────────────────────────────────────────────────────────────────

    private static HiveConfigFile Config(string? primary) => new()
    {
        Models = new ModelsConfig
        {
            AvailableModels = new List<ModelEntry>
            {
                new() { Name = Model },
                new() { Name = OtherModel },
            },
        },
        Composer = new ComposerConfig { Model = primary },
    };

    /// <summary>
    /// Composes the production lifetimes for one test: the SINGLETON manager over a real factory,
    /// the SCOPED accessor over a real circuit accessor, and the recovery facade over the REAL
    /// legacy facade.
    /// </summary>
    private async Task<(ComposerRuntimeFacade Facade, LiveRuntime Runtime, ConnectCounter Connects)> BuildAsync(
        HiveConfigFile config, Func<string, string> provider, Func<bool>? tokenAvailable = null,
        Func<string, IChatClient>? chatClientFactory = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IGoalStore>(new GoalStore(_goalDb, NullLogger<GoalStore>.Instance));
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton(new HivePolicy());
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddSingleton<LlmSessionRegistry>();

        var serviceProvider = services.BuildServiceProvider();
        _providers.Add(serviceProvider);

        var counter = new ConnectCounter();
        var factory = new ComposerFactory(
            serviceProvider,
            _dir,
            ollamaApiKey: null,
            chatClientFactory: chatClientFactory ?? (_ => new Mock<IChatClient>().Object),
            isTokenAvailable: tokenAvailable ?? (() => true),
            providerOf: provider);
        var manager = new ComposerRuntimeManager(
            factory, _auth.Directory.Service, NullLogger<ComposerRuntimeManager>.Instance,
            (composer, ct) =>
            {
                counter.Increment();
                return composer.ConnectAsync(ct);
            });
        _managers.Add(manager);

        var resolver = serviceProvider.GetRequiredService<HivePrincipalResolver>();
        var policy = serviceProvider.GetRequiredService<HivePolicy>();
        var accessor = new CountingAccessor(new ComposerRuntimeAccessor(
            manager,
            new CircuitUserPrincipalAccessor(resolver, policy),
            resolver,
            policy,
            NullLogger<ComposerRuntimeAccessor>.Instance));

        var legacy = new Composer(
            "reconcile-legacy-model",
            NullLogger<Composer>.Instance,
            new GoalStore(_goalDb, NullLogger<GoalStore>.Instance),
            stateDir: _dir);
        _legacyComposers.Add(legacy);

        var facade = new ComposerRuntimeFacade(
            accessor,
            new ComposerFacade(legacy, resolver, policy, NullLogger<ComposerFacade>.Instance),
            NullLogger<ComposerRuntimeFacade>.Instance);

        var result = await ReacquireAsync(accessor);
        Assert.True(result.IsAuthorized, "the seeded account must be authorized");
        Assert.NotNull(result.Runtime);

        return (facade, new LiveRuntime(manager, accessor, result.Runtime, result), counter);
    }

    private readonly List<Composer> _legacyComposers = [];

    private Task<ComposerRuntimeAccess> ReacquireAsync(IComposerRuntimeAccessor accessor)
    {
        var task = accessor.AcquireForRequestAsync(Original, Ct);
        _requests.Add(task);
        return task;
    }

    private static IEnumerable<int> ManagerEntries(ComposerRuntimeManager manager)
    {
        var field = typeof(ComposerRuntimeManager)
            .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)field.GetValue(manager)!;
        return entries.Keys.Cast<int>().ToArray();
    }

    /// <summary>One built runtime together with the manager/accessor that own it.</summary>
    private sealed record LiveRuntime(
        ComposerRuntimeManager Manager,
        CountingAccessor Accessor,
        ComposerUserRuntime Runtime,
        ComposerRuntimeAccess Access)
    {
        /// <summary>The status the manager reported for this runtime's creation.</summary>
        public ComposerRuntimeStatus Status => Access.Status;
    }

    /// <summary>Counts the manager's own connect invocations (the ONE connect seam).</summary>
    private sealed class ConnectCounter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public void Increment() => Interlocked.Increment(ref _calls);
    }

    /// <summary>Counts the accessor's connect delegations, so "no duplicate connect" is observable.</summary>
    private sealed class CountingAccessor(IComposerRuntimeAccessor inner) : IComposerRuntimeAccessor
    {
        private int _connectCalls;

        public int ConnectCalls => Volatile.Read(ref _connectCalls);
        public bool AuthEnabled => inner.AuthEnabled;

        public Task<ComposerRuntimeAccess> AcquireForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            inner.AcquireForRequestAsync(actingUser, ct);

        public Task<ComposerRuntimeAccess> ConnectForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            Interlocked.Increment(ref _connectCalls);
            return inner.ConnectForRequestAsync(actingUser, ct);
        }

        public Task<ComposerRuntimeAccess> AcquireForCircuitAsync(
            Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>? authenticationState,
            CancellationToken ct) =>
            inner.AcquireForCircuitAsync(authenticationState, ct);

        public Task<ComposerRuntimeAccess> ConnectForCircuitAsync(
            Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>? authenticationState,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _connectCalls);
            return inner.ConnectForCircuitAsync(authenticationState, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();

        async Task Step(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { failures.Add(ex); }
        }

        foreach (var request in _requests.ToArray())
            await Step(() => request.ContinueWith(
                _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));

        // The manager is the sole disposer: shut every one down (and join its teardown) before the providers.
        foreach (var manager in _managers)
            await Step(manager.ShutdownAsync);

        foreach (var legacy in _legacyComposers)
            await Step(() => legacy.DisposeAsync().AsTask());

        foreach (var provider in _providers)
            await Step(() => provider.DisposeAsync().AsTask());

        await Step(() => { _goalDb.Dispose(); return Task.CompletedTask; });

        if (failures.Count > 0)
            throw new AggregateException("Reconciliation test cleanup failed", failures);
    }

    public void Dispose()
    {
        _auth.Dispose();
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the isolated state directory.
        }
    }
}
