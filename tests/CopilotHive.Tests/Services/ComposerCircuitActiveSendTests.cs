using System.Security.Claims;

using CopilotHive.Components.Pages;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// AC1 — ONE ACTIVE SEND PER USER, IN THE PER-USER CIRCUIT PATH: two circuits of the SAME user share
/// ONE runtime, so a send started in one of them occupies that user's single active stream — the
/// second send is refused with the Composer's own "already streaming" answer, not with a second
/// concurrent stream — while ANOTHER user's circuit streams independently.
/// <para>
/// The pre-existing "already streaming" rows exercise the legacy singleton Composer directly. This
/// class exercises the PER-USER path the goal introduced: the runtime the two circuits share is the
/// manager's runtime for that account, and the refusal is produced by that runtime's own actor.
/// </para>
/// <para>
/// The chat client is a REAL, controllable one: the first send parks inside the client's response
/// stream until the test releases it, so the stream is genuinely in flight while the second send is
/// attempted — no sleep and no polling.
/// </para>
/// </summary>
public sealed class ComposerCircuitActiveSendTests : IAsyncLifetime, IDisposable
{
    private const string Model = "active-send-model";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-active-send-{Guid.NewGuid():N}");

    private int _memberId;
    private int _otherId;

    public ComposerCircuitActiveSendTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _memberId = (await _auth.SeedAsync("active-send-member", UserRole.Member)).Id;
        _otherId = (await _auth.SeedAsync("active-send-other", UserRole.Member)).Id;
    }

    /// <summary>
    /// ONE ACTIVE SEND PER USER: the first send on the shared per-user runtime takes the stream; a
    /// SECOND send for the SAME user — from the OTHER circuit — is refused with the Composer's own
    /// "already streaming" answer (no second stream is started), and a DIFFERENT user's circuit
    /// streams concurrently and independently.
    /// <para>
    /// The DISCRIMINATOR is the other user's stream: it completes normally while the first user's
    /// stream is still parked, so the refusal above is about the USER, not about a globally blocked
    /// Composer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OneActiveSendPerUser_TwoCircuitsOfTheSameUserShareTheSingleStream()
    {
        await using var host = NewHost();
        var client = host.Client;

        // TWO circuits of the SAME user (two scopes = two browser tabs).
        using var firstTab = host.Provider.CreateScope();
        using var secondTab = host.Provider.CreateScope();

        var firstAccess = await AcquireAsync(firstTab, "active-send-member");
        var secondAccess = await AcquireAsync(secondTab, "active-send-member");

        // The very same runtime: the "one active send per user" rule is about the USER.
        Assert.Same(firstAccess.Runtime!.Composer, secondAccess.Runtime!.Composer);

        // The FIRST send takes the stream and parks inside the chat client.
        var firstSend = firstAccess.Runtime.Composer.SendMessageWithEventsAsync("first-message");

        // The client has been entered: the stream is genuinely in flight.
        await client.Entered;

        // The SECOND send — from the OTHER circuit, on the SAME user's runtime — is refused.
        var secondFailure = await Record.ExceptionAsync(async () =>
            await secondAccess.Runtime.Composer.SendMessageWithEventsAsync("second-message"));

        Assert.NotNull(secondFailure);
        Assert.IsType<InvalidOperationException>(secondFailure);
        Assert.Contains("already streaming", secondFailure.Message, StringComparison.OrdinalIgnoreCase);

        // ...and NO second stream was started: the client is still serving the FIRST send only.
        Assert.Equal(1, client.EnteredCount);
        Assert.Single(client.Requests);
        Assert.Contains("first-message", client.Requests[0], StringComparison.Ordinal);

        // A DIFFERENT user's circuit streams concurrently and independently.
        using var otherTab = host.Provider.CreateScope();
        var otherAccess = await AcquireAsync(otherTab, "active-send-other");
        Assert.NotNull(otherAccess.Runtime);
        Assert.NotSame(firstAccess.Runtime!.Composer, otherAccess.Runtime.Composer);

        // The other user's stream is awaited through the Composer's OWN streaming event — no polling.
        var otherStreamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOtherStreamingUpdate()
        {
            if (!otherAccess.Runtime.Composer.IsStreaming)
                otherStreamed.TrySetResult();
        }

        otherAccess.Runtime.Composer.OnStreamingUpdate += OnOtherStreamingUpdate;
        try
        {
            var otherSend = otherAccess.Runtime.Composer.SendMessageWithEventsAsync("other-user-message");
            await otherStreamed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await otherSend;
        }
        finally
        {
            otherAccess.Runtime.Composer.OnStreamingUpdate -= OnOtherStreamingUpdate;
        }

        // The other user's stream really completed while the first user's was still parked — its own
        // message is in ITS session, and the first user's session never saw it.
        Assert.Contains(otherAccess.Runtime.Composer.GetChatHistory(),
            m => m.Content.Contains("other-user-message"));
        Assert.DoesNotContain(firstAccess.Runtime.Composer.GetChatHistory(),
            m => m.Content.Contains("other-user-message"));

        // Release the first user's stream and join it.
        client.Release();
        await firstSend;

        // Exactly TWO streams in total: the first user's and the other user's — never a third.
        Assert.Equal(2, client.EnteredCount);
    }

    /// <summary>
    /// TWO CONCURRENT SENDS ON THE SAME USER'S RUNTIME: exactly ONE of them is admitted and the other
    /// is refused with the Composer's own "already streaming" answer — the runtime's single active
    /// stream is never doubled, whatever order the two sends race in.
    /// <para>
    /// The DISCRIMINATOR is the model's own request count: the client is entered exactly ONCE, so a
    /// runtime that admitted both sends would have driven the model twice.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoConcurrentSendsOnTheSameRuntime_AdmitExactlyOne()
    {
        await using var host = NewHost();
        var client = host.Client;

        using var firstTab = host.Provider.CreateScope();
        using var secondTab = host.Provider.CreateScope();

        var firstAccess = await AcquireAsync(firstTab, "active-send-member");
        var secondAccess = await AcquireAsync(secondTab, "active-send-member");
        Assert.NotNull(secondAccess.Runtime);

        // Both sends are issued CONCURRENTLY, before either has been admitted.
        var firstSend = firstAccess.Runtime!.Composer.SendMessageWithEventsAsync("concurrent-first");
        var secondSend = secondAccess.Runtime.Composer.SendMessageWithEventsAsync("concurrent-second");

        // Both tasks settle at ADMISSION (the send is admitted or refused), so this join does not
        // wait for the parked stream to finish.
        var outcomes = await Task.WhenAll(
            CaptureAsync(firstSend),
            CaptureAsync(secondSend));

        Assert.Equal(1, outcomes.Count(o => o.Exception is not null));
        var refused = outcomes.Single(o => o.Exception is not null).Exception!;
        Assert.IsType<InvalidOperationException>(refused);
        Assert.Contains("already streaming", refused.Message, StringComparison.OrdinalIgnoreCase);

        // The model was driven EXACTLY ONCE: the refused send never reached it. The client is
        // entered by the STREAMING task, so the count is read after the client has been entered.
        await client.Entered;
        Assert.Equal(1, client.EnteredCount);
        Assert.Single(client.Requests);

        client.Release();
        await Task.WhenAll(
            firstSend.ContinueWith(_ => { }, TaskScheduler.Default),
            secondSend.ContinueWith(_ => { }, TaskScheduler.Default));
    }

    /// <summary>
    /// A SEND THAT IS REFUSED DOES NOT DISTURB THE ACTIVE STREAM: the first user's stream is still
    /// the one in flight, and the refused second message was never delivered to the model — so the
    /// session history carries only the accepted message.
    /// </summary>
    [Fact]
    public async Task ARefusedSecondSend_NeverReachesTheModel()
    {
        await using var host = NewHost();
        var client = host.Client;

        using var firstTab = host.Provider.CreateScope();
        using var secondTab = host.Provider.CreateScope();

        var firstAccess = await AcquireAsync(firstTab, "active-send-member");
        var secondAccess = await AcquireAsync(secondTab, "active-send-member");
        Assert.NotNull(secondAccess.Runtime);

        var firstSend = firstAccess.Runtime!.Composer.SendMessageWithEventsAsync("accepted-message");
        await client.Entered;

        var failure = await Record.ExceptionAsync(async () =>
            await secondAccess.Runtime.Composer.SendMessageWithEventsAsync("refused-message"));
        Assert.NotNull(failure);

        client.Release();
        await firstSend;

        // The client saw EXACTLY ONE request: the refused message never reached the model.
        Assert.Equal(1, client.EnteredCount);
        Assert.Single(client.Requests);
        Assert.Contains("accepted-message", client.Requests[0], StringComparison.Ordinal);
    }

    // ── Host fixture ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A host over the PRODUCTION registration seam with a REAL, controllable chat client: the first
    /// response parks until the test releases it, so a stream can be observed genuinely in flight.
    /// </summary>
    private Host NewHost()
    {
        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var client = new ParkingChatClient();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
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
            chatClientFactory: _ => client,
            isTokenAvailable: () => true,
            providerOf: _ => "ollama-local"));
        services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
        services.AddSingleton(sp => new ComposerRuntimeManager(
            sp.GetRequiredService<ComposerFactory>(),
            sp.GetRequiredService<UserService>(),
            NullLogger<ComposerRuntimeManager>.Instance));
        services.AddSingleton(new ComposerAttachmentService(stateDir, NullLogger<ComposerAttachmentService>.Instance));
        services.AddSingleton<IConfigFacade>(sp => new ConfigFacade(
            null, null, null, NullLogger<ConfigFacade>.Instance,
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));

        // THE PRODUCTION SEAM.
        services.AddComposerRuntimeFacades(ComposerRuntimeFacadeDeployment.PerUserRuntime);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, provider.GetRequiredService<ComposerRuntimeManager>(), client, stateDir);
    }

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

    /// <summary>Captures the outcome of one send without letting a failure escape.</summary>
    private static async Task<(Exception? Exception, string? Result)> CaptureAsync(Task<string?> send)
    {
        try
        {
            return (null, await send);
        }
        catch (Exception ex)
        {
            return (ex, null);
        }
    }

    /// <summary>
    /// A REAL chat client whose FIRST response parks until it is released. It records every request
    /// it serves, so a refused send is observable as "the model never saw it".
    /// <para>
    /// The parking decision is made ONCE, under the lock, from the request index — so it never
    /// depends on a counter that another request may have incremented in between.
    /// </para>
    /// </summary>
    private sealed class ParkingChatClient : IChatClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _requests = [];
        private readonly object _lock = new();
        private int _enteredCount;

        /// <summary>Completes when the client has ENTERED a response stream.</summary>
        internal Task Entered => _entered.Task;

        /// <summary>How many responses the client has entered.</summary>
        internal int EnteredCount => Volatile.Read(ref _enteredCount);

        /// <summary>The prompts the client actually served.</summary>
        internal IReadOnlyList<string> Requests
        {
            get { lock (_lock) return _requests.ToArray(); }
        }

        /// <summary>Releases the parked response.</summary>
        internal void Release() => _release.TrySetResult();

        /// <inheritdoc />
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var prompt = string.Join(" ", messages.Select(m => m.Text));
            bool parked;
            lock (_lock)
            {
                _requests.Add(prompt);
                // The FIRST request parks; every later one is served immediately.
                parked = _requests.Count == 1;
            }

            Interlocked.Increment(ref _enteredCount);
            _entered.TrySetResult();

            if (parked)
                await _release.Task.WaitAsync(cancellationToken);

            yield return new ChatResponseUpdate(ChatRole.Assistant, $"echo:{prompt}")
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        /// <inheritdoc />
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The test client only streams.");

        /// <inheritdoc />
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        /// <inheritdoc />
        public void Dispose()
        {
            // The parked response is released so a discarded host never leaves a send hanging.
            Release();
        }
    }

    /// <summary>A JS runtime that answers nothing.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default!;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default!;
    }

    /// <summary>The provider, the manager, the chat client and the state directory of one host.</summary>
    private sealed class Host(
        ServiceProvider provider,
        ComposerRuntimeManager manager,
        ParkingChatClient client,
        string stateDir) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public ComposerRuntimeManager Manager { get; } = manager;

        public ParkingChatClient Client { get; } = client;

        public string StateDir { get; } = stateDir;

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();

            async Task Step(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add(ex); }
            }

            // The parked response is released first, so the manager's teardown is never blocked by it.
            Client.Release();
            await Step(() => Manager.ShutdownAsync());
            await Step(() => Provider.DisposeAsync().AsTask());

            if (failures.Count > 0)
                throw new AggregateException("Active-send host cleanup failed", failures);
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
