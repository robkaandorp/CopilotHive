using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// AC1 — THE PASSIVE EVENT BUFFER IS GENUINELY PER-RUNTIME: draining one user's buffer must NOT
/// consume another user's pending notification, and a buffer that belongs to one runtime must never
/// hand its events to another.
/// <para>
/// The isolation is observed the way it actually matters: the FIRST user's buffer is drained while a
/// second user's buffer is still loaded, and the second user's own event is still there afterwards.
/// A shared buffer (one subscriber for every runtime) would have handed the second user the first
/// user's drained events — or emptied the second user's buffer on the first drain.
/// </para>
/// <para>
/// The rows also pin the two halves that make the isolation real: each runtime owns a DISTINCT
/// subscriber instance, and removing one runtime's subscriber from the bus leaves the other's
/// subscription intact.
/// </para>
/// </summary>
public sealed class ComposerPassiveBufferIsolationTests : IAsyncLifetime, IDisposable
{
    private const string Model = "passive-buffer-model";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-passive-{Guid.NewGuid():N}");

    private int _memberId;
    private int _operatorId;

    public ComposerPassiveBufferIsolationTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _memberId = (await _auth.SeedAsync("passive-member", UserRole.Member)).Id;
        _operatorId = (await _auth.SeedAsync("passive-operator", UserRole.Operator)).Id;
    }

    /// <summary>
    /// DRAINING ONE USER'S BUFFER LEAVES THE OTHER USER'S BUFFER INTACT: the first user's drain
    /// consumes ONLY the first user's buffer, and the second user's buffer still holds its own
    /// pending events afterwards.
    /// <para>
    /// The event bus is a genuine BROADCAST — every subscriber sees every event — so the isolation
    /// under test is the BUFFER: each runtime owns its own pending queue, and draining one queue
    /// never empties another's. With a shared buffer the first drain would have emptied the second
    /// user's queue as well, and the second drain would have returned nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DrainingOneUsersBuffer_LeavesTheOtherUsersBufferIntact()
    {
        await using var host = NewHost();

        using var memberCircuit = host.Provider.CreateScope();
        using var operatorCircuit = host.Provider.CreateScope();

        var member = await AcquireAsync(memberCircuit, "passive-member");
        var other = await AcquireAsync(operatorCircuit, "passive-operator");

        // TWO DISTINCT subscribers — one per runtime. A shared subscriber is caught here.
        Assert.NotNull(member.Runtime!.Subscriber);
        Assert.NotNull(other.Runtime!.Subscriber);
        Assert.NotSame(member.Runtime.Subscriber, other.Runtime.Subscriber);

        // A broadcast reaches BOTH buffers (that is the bus's contract).
        host.EventBus.Publish(Event("broadcast-event"));

        // The FIRST user's drain empties ONLY the first user's buffer.
        var memberDrained = member.Runtime.Subscriber.DrainPendingEvents();
        Assert.Single(memberDrained);
        Assert.Equal("broadcast-event", memberDrained[0].Message);

        // The SECOND user's buffer is UNTOUCHED by that drain: its own pending event is still there.
        var operatorPending = other.Runtime.Subscriber.PeekPendingEvents();
        Assert.Single(operatorPending);
        Assert.Equal("broadcast-event", operatorPending[0].Message);

        // ...and its own drain returns exactly that one event.
        var operatorDrained = other.Runtime.Subscriber.DrainPendingEvents();
        Assert.Single(operatorDrained);
        Assert.Equal("broadcast-event", operatorDrained[0].Message);
    }

    /// <summary>
    /// A REMOVED RUNTIME'S SUBSCRIBER LEAVES THE OTHER RUNTIME'S SUBSCRIPTION INTACT: removing the
    /// first user's runtime unsubscribes only ITS subscriber from the bus, so the second user still
    /// receives the next broadcast while the first user's buffer no longer grows.
    /// </summary>
    [Fact]
    public async Task RemovingOneRuntime_UnsubscribesOnlyItsOwnSubscriber()
    {
        await using var host = NewHost();

        using var memberCircuit = host.Provider.CreateScope();
        using var operatorCircuit = host.Provider.CreateScope();

        var member = await AcquireAsync(memberCircuit, "passive-member");
        var other = await AcquireAsync(operatorCircuit, "passive-operator");

        var memberSubscriber = member.Runtime!.Subscriber!;
        var operatorSubscriber = other.Runtime!.Subscriber!;

        // The bus knows BOTH subscribers.
        Assert.Equal(2, host.EventBus.SubscriberCount);

        // The first user's runtime is removed: only ITS subscriber leaves the bus.
        await host.Manager.RemoveAsync(_memberId);

        Assert.Equal(1, host.EventBus.SubscriberCount);

        // A broadcast now reaches ONLY the surviving runtime.
        host.EventBus.Publish(Event("after-removal-event"));

        var operatorPending = operatorSubscriber.PeekPendingEvents();
        Assert.Single(operatorPending);
        Assert.Equal("after-removal-event", operatorPending[0].Message);

        // The removed runtime's subscriber received nothing (it is off the bus).
        Assert.Empty(memberSubscriber.PeekPendingEvents());
    }

    /// <summary>
    /// A BUFFER DRAIN IS ATOMIC AND REPEATABLE: draining twice returns the events once and then
    /// nothing — the second drain must not hand the SAME event to the user a second time (which is
    /// what "one pending notification, delivered once" means).
    /// </summary>
    [Fact]
    public async Task ADrainDeliversEachEventExactlyOnce()
    {
        await using var host = NewHost();

        using var circuit = host.Provider.CreateScope();
        var access = await AcquireAsync(circuit, "passive-member");
        Assert.NotNull(access.Runtime!.Subscriber);

        host.EventBus.Publish(Event("once-only-event"));

        var first = access.Runtime.Subscriber.DrainPendingEvents();
        Assert.Single(first);
        Assert.Equal("once-only-event", first[0].Message);

        // The second drain delivers NOTHING: the event was already delivered.
        Assert.Empty(access.Runtime.Subscriber.DrainPendingEvents());
    }

    // ── Host fixture ────────────────────────────────────────────────────────────────────────────

    /// <summary>Acquires one circuit's runtime through the REAL accessor.</summary>
    private static async Task<ComposerRuntimeAccess> AcquireAsync(IServiceScope scope, string gitHubId)
    {
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, gitHubId)], authenticationType: "Cookies")));

        var access = await scope.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>()
            .AcquireForCircuitAsync(Task.FromResult(state), Ct);
        Assert.True(access.IsAuthorized, $"'{gitHubId}' must be authorized");
        return access;
    }

    /// <summary>A minimal system event carrying a marker.</summary>
    private static SystemEvent Event(string marker) =>
        new(EventType.GoalCompleted, marker, Timestamp: DateTime.UtcNow);

    /// <summary>A host over the PRODUCTION registration seam with a recording event bus.</summary>
    private Host NewHost()
    {
        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HiveConfigFile>(new HiveConfigFile
        {
            Orchestrator = OrchestratorConfig.CreateEmptyModelFallback(),
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = Model }] },
            Composer = new ComposerConfig { Model = Model },
        });
        services.AddSingleton<IGoalStore>(new GoalStore(_db, NullLogger<GoalStore>.Instance));
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));
        services.AddSingleton(sp => new ComposerFactory(
            sp, stateDir, ollamaApiKey: null,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            isTokenAvailable: () => true,
            providerOf: _ => "ollama-local"));
        services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
        services.AddSingleton(sp => new ComposerRuntimeManager(
            sp.GetRequiredService<ComposerFactory>(),
            sp.GetRequiredService<UserService>(),
            NullLogger<ComposerRuntimeManager>.Instance));

        var eventBus = new RecordingEventBus();
        services.AddSingleton<IEventBus>(eventBus);

        // THE PRODUCTION REGISTRATION of the shared (legacy) event subscriber: the host registers it
        // as a SINGLETON. The per-user runtimes must NOT reuse it — each runtime builds its OWN
        // subscriber over the bus — so registering it exactly as production does makes the
        // per-runtime isolation observable instead of accidental.
        services.AddSingleton<ComposerEventSubscriber>();

        // THE PRODUCTION SEAM.
        services.AddComposerRuntimeFacades(ComposerRuntimeFacadeDeployment.PerUserRuntime);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, provider.GetRequiredService<ComposerRuntimeManager>(), eventBus, stateDir);
    }

    /// <summary>
    /// A minimal event bus that records its subscribers and fans out to them on <see cref="Publish"/>.
    /// </summary>
    private sealed class RecordingEventBus : IEventBus
    {
        private readonly List<Action<SystemEvent>> _handlers = [];
        private readonly object _lock = new();

        /// <summary>How many subscribers are currently attached.</summary>
        internal int SubscriberCount
        {
            get { lock (_lock) return _handlers.Count; }
        }

        /// <inheritdoc />
        public event Action<SystemEvent>? OnEvent
        {
            add { lock (_lock) _handlers.Add(value!); }
            remove { lock (_lock) _handlers.Remove(value!); }
        }

        /// <summary>Fans <paramref name="evt"/> out to every current subscriber.</summary>
        public void Publish(SystemEvent evt)
        {
            Action<SystemEvent>[] snapshot;
            lock (_lock) snapshot = _handlers.ToArray();
            foreach (var handler in snapshot)
                handler(evt);
        }
    }

    /// <summary>The provider, the manager, the bus and the state directory of one host.</summary>
    private sealed class Host(
        ServiceProvider provider,
        ComposerRuntimeManager manager,
        RecordingEventBus eventBus,
        string stateDir) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public ComposerRuntimeManager Manager { get; } = manager;

        public RecordingEventBus EventBus { get; } = eventBus;

        public string StateDir { get; } = stateDir;

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();

            async Task Step(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add(ex); }
            }

            // The manager's actual teardown is AWAITED before the provider goes away.
            await Step(() => Manager.ShutdownAsync());
            await Step(() => Provider.DisposeAsync().AsTask());

            if (failures.Count > 0)
                throw new AggregateException("Passive-buffer host cleanup failed", failures);
        }
    }

    /// <summary>
    /// THE RUNNER-INVOKED CLEANUP: xUnit v3 calls <c>DisposeAsync</c> for an
    /// <see cref="IAsyncLifetime"/>, so every cleanup step lives there.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        _auth.Dispose();
        try
        {
            if (Directory.Exists(_dir))
                TestHelpers.ForceDeleteDirectory(_dir);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the isolated state directory.
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
    }
}
