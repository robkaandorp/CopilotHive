using System.Security.Claims;

using CopilotHive.Configuration;
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
/// THE WHOLE RECOVERY PATH END TO END — real <see cref="ComposerRuntimeManager"/>, real
/// <see cref="ComposerRuntimeAccessor"/>, real production resolver/policy over a real directory, a REAL
/// per-user runtime and the real legacy facade — so the recovery mappings are proved against the manager's
/// ACTUAL outcomes and status reconciliation rather than against a substitute.
/// <para>
/// The substitute-based mapping suite (<c>ComposerRuntimeFacadeRecoveryTests</c>) pins every status branch; these
/// rows prove the same branches are the ones production really produces, and that the facade adds NO state
/// machine of its own: no duplicate connect, no second reconciliation, and no disposal.
/// </para>
/// </summary>
public sealed class ComposerRuntimeFacadeEndToEndTests : IAsyncLifetime, IDisposable
{
    private const string Model = "e2e-model";
    private const string OtherModel = "e2e-other-model";
    private const string SecondaryModel = "e2e-secondary-model";
    private const string MemberId = "e2e-member";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _goalDb = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-e2e-{Guid.NewGuid():N}");
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<ComposerRuntimeManager> _managers = [];
    private readonly List<Task> _requests = [];
    private volatile bool _tokenAvailable = true;
    private int _memberId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Member { get; } = FacadeAuthorizationHarness.Principal(MemberId);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _memberId = (await _auth.SeedAsync(MemberId, UserRole.Member)).Id;
    }

    /// <summary>
    /// THE MODEL-LESS SHELL ROW: a runtime with no configured model settles as
    /// <see cref="ComposerRuntimeStatus.DisconnectedShell"/>. A READ answers the frozen null-model contract on a
    /// SUCCESSFUL result, and the SWITCH then proceeds against the shell so the actor's first-selection path
    /// connects it — after which the manager itself reports Connected (no facade connect was ever issued, which
    /// the connect counter proves).
    /// </summary>
    [Fact]
    public async Task ShellRuntime_ReadReportsNullModel_AndTheSwitchConnectsThroughTheActorsFirstSelection()
    {
        var (facade, runtime, connects) = await BuildAsync(
            config: Config(primary: null),
            legacy: new Composer("e2e-legacy-model", NullLogger<Composer>.Instance, Store(), stateDir: _dir),
            provider: _ => "ollama-local");

        Assert.Equal(ComposerRuntimeStatus.DisconnectedShell, runtime.Status);

        var read = await facade.GetCurrentModelAsync(Member, Ct);
        Assert.True(read.Success, read.Error ?? "shell read failed");
        Assert.Null(read.Value!.Model);

        var switched = await facade.SwitchModelAsync(Member, OtherModel, "medium", Ct);
        Assert.True(switched.Success, switched.Error ?? "shell switch failed");
        Assert.Equal(OtherModel, switched.Value!.Model);

        // The actor's first selection connected the runtime; the facade never started a manager connect.
        Assert.True(runtime.Runtime!.Composer.IsConnected);
        Assert.Equal(0, connects.Calls);

        // ...and the manager now RECONCILES the cached shell outcome to Connected by itself.
        Assert.Equal(ComposerRuntimeStatus.Connected, (await ReacquireAsync(runtime.Accessor)).Status);

        // A SECOND switch on the (now connected) runtime still issues no manager connect.
        var second = await facade.SwitchModelAsync(Member, Model, "high", Ct);
        Assert.True(second.Success, second.Error ?? "connected switch failed");
        Assert.Equal(0, connects.Calls);
    }

    /// <summary>
    /// THE DEFERRED ROW, OPERATION-DRIVEN: with no usable token the runtime settles
    /// <see cref="ComposerRuntimeStatus.Deferred"/>; a READ stays a shell answer, and the first MUTATION performs
    /// exactly ONE manager connect, which (once the token is available) reaches Connected and applies the
    /// selection.
    /// </summary>
    [Fact]
    public async Task DeferredRuntime_ReadStaysShell_AndTheMutationDrivesExactlyOneConnect()
    {
        // A Copilot-backed model is what the deferral gate covers: without a usable token the runtime
        // settles Deferred (the ORIGINAL identity's legacy token path — the seeded Member is the lowest-id
        // activated account).
        _tokenAvailable = false;
        var composer = new Composer(Model, NullLogger<Composer>.Instance, Store(), stateDir: _dir,
            availableModels: [Model, OtherModel, SecondaryModel],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        var (facade, runtime, _) = await BuildAsync(
            config: Config(primary: Model),
            legacy: composer,
            provider: _ => "copilot");

        Assert.Equal(ComposerRuntimeStatus.Deferred, runtime.Status);

        // A read never connects: it answers shell metadata.
        var read = await facade.GetModelsAsync(Member, Ct);
        Assert.True(read.Success, read.Error ?? "deferred read failed");
        Assert.Equal([Model, OtherModel, SecondaryModel], read.Value!.Models);
        Assert.Equal(0, runtime.Accessor.ConnectCalls);

        // The token becomes available: the operation's own connect now succeeds.
        _tokenAvailable = true;
        var switched = await facade.SwitchModelAsync(Member, OtherModel, "high", Ct);

        Assert.True(switched.Success, switched.Error ?? "deferred mutation failed");
        Assert.Equal(1, runtime.Accessor.ConnectCalls);
        Assert.True(runtime.Runtime!.Composer.IsConnected);
        Assert.Equal(OtherModel, switched.Value!.Model);

        // The runtime is LIVE now: a second mutation must not issue a duplicate connect.
        var again = await facade.SwitchModelAsync(Member, SecondaryModel, "medium", Ct);
        Assert.True(again.Success, again.Error ?? "second mutation failed");
        Assert.Equal(1, runtime.Accessor.ConnectCalls);
    }

    /// <summary>
    /// THE VIEWER ROW OVER THE REAL MANAGER: a Viewer is refused with
    /// <see cref="FacadeErrorKind.Forbidden"/>, NO content, and NO runtime entry is ever created — while the SAME
    /// facade still serves the Member, so the refusal is about the caller and not a broken facade.
    /// </summary>
    [Fact]
    public async Task Viewer_IsForbiddenOnEveryOperation_AndNoRuntimeIsCreatedForThem()
    {
        var (facade, runtime, _) = await BuildAsync(
            config: Config(primary: Model),
            legacy: new Composer(Model, NullLogger<Composer>.Instance, Store(), stateDir: _dir),
            provider: _ => "ollama-local");

        var viewerId = (await _auth.SeedAsync("e2e-viewer", UserRole.Viewer)).Id;
        var viewer = FacadeAuthorizationHarness.Principal("e2e-viewer");

        var kinds = new[]
        {
            (await facade.GetCurrentModelAsync(viewer, Ct)).Kind,
            (await facade.GetModelsAsync(viewer, Ct)).Kind,
            (await facade.SwitchModelAsync(viewer, OtherModel, "high", Ct)).Kind,
            (await facade.CompactAsync(viewer, Ct)).Kind,
            (await facade.CompactPartialAsync(viewer, 50, Ct)).Kind,
        };

        Assert.All(kinds, kind => Assert.Equal(FacadeErrorKind.Forbidden, kind));
        Assert.DoesNotContain(viewerId, ManagerEntries(runtime.Manager));

        // Control: the very same facade object still serves the Member (with its own runtime).
        var member = await facade.GetCurrentModelAsync(Member, Ct);
        Assert.True(member.Success, member.Error ?? "member read failed");
        Assert.Contains(_memberId, ManagerEntries(runtime.Manager));
    }

    /// <summary>
    /// THE FACADE IS A HANDLE, NOT AN OWNER: driving every operation (including one that fails) never disposes the
    /// runtime — after the facade returns, the manager's runtime is still live and still owned by the manager,
    /// which is the sole disposer.
    /// </summary>
    [Fact]
    public async Task FacadeOperations_NeverDisposeTheRuntime_ThatIsTheManagersJob()
    {
        var (facade, runtime, _) = await BuildAsync(
            config: Config(primary: Model),
            legacy: new Composer(Model, NullLogger<Composer>.Instance, Store(), stateDir: _dir),
            provider: _ => "ollama-local");

        Assert.Equal(ComposerRuntimeStatus.Connected, runtime.Status);
        var composer = runtime.Runtime!.Composer;

        await facade.GetCurrentModelAsync(Member, Ct);
        await facade.GetModelsAsync(Member, Ct);
        await facade.SwitchModelAsync(Member, OtherModel, "high", Ct);
        // An OPERATIONAL compaction failure (too few messages is a success; streaming is a 400) exercises
        // the failure path without touching the runtime.
        var compact = await facade.CompactAsync(Member, Ct);
        Assert.True(compact.Success || compact.Kind == FacadeErrorKind.BadRequest, compact.Error ?? "unexpected kind");

        // The runtime is STILL connected and still cached: the facade disposed nothing.
        Assert.True(composer.IsConnected);
        Assert.Contains(_memberId, ManagerEntries(runtime.Manager));
        Assert.Equal(ComposerRuntimeStatus.Connected, (await ReacquireAsync(runtime.Accessor)).Status);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────

    private static HiveConfigFile Config(string? primary) => new()
    {
        Models = new ModelsConfig
        {
            AvailableModels = new List<ModelEntry>
            {
                new() { Name = Model },
                new() { Name = OtherModel },
                new() { Name = SecondaryModel },
            },
        },
        Composer = new ComposerConfig { Model = primary },
    };

    private GoalStore Store() => new(_goalDb, NullLogger<GoalStore>.Instance);

    /// <summary>
    /// Composes the production lifetimes for one test: the SINGLETON manager over a real factory, the SCOPED
    /// accessor over a real circuit accessor, and the recovery facade over the REAL legacy facade.
    /// </summary>
    private async Task<(ComposerRuntimeFacade Facade, LiveRuntime Runtime, ConnectCounter Connects)> BuildAsync(
        HiveConfigFile config, Composer legacy, Func<string, string> provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IGoalStore>(Store());
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton(new HivePolicy());
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));

        var serviceProvider = services.BuildServiceProvider();
        _providers.Add(serviceProvider);

        var counter = new ConnectCounter();
        var factory = new ComposerFactory(
            serviceProvider,
            _dir,
            ollamaApiKey: null,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            isTokenAvailable: () => _tokenAvailable,
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

        var facade = new ComposerRuntimeFacade(
            accessor,
            new ComposerFacade(legacy, resolver, policy, NullLogger<ComposerFacade>.Instance),
            NullLogger<ComposerRuntimeFacade>.Instance);

        var result = await AcquireAsync(accessor);
        Assert.True(result.IsAuthorized, "the seeded Member must be authorized");
        Assert.NotNull(result.Runtime);

        return (facade, new LiveRuntime(manager, accessor, result.Runtime, result), counter);
    }

    private Task<ComposerRuntimeAccess> AcquireAsync(CountingAccessor accessor)
    {
        var task = accessor.AcquireForRequestAsync(Member, Ct);
        _requests.Add(task);
        return task;
    }

    private async Task<ComposerRuntimeAccess> ReacquireAsync(IComposerRuntimeAccessor accessor)
    {
        var task = accessor.AcquireForRequestAsync(Member, Ct);
        _requests.Add(task);
        return await task;
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

        foreach (var provider in _providers)
            await Step(() => provider.DisposeAsync().AsTask());

        await Step(() => { _goalDb.Dispose(); return Task.CompletedTask; });

        if (failures.Count > 0)
            throw new AggregateException("End-to-end facade test cleanup failed", failures);
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
