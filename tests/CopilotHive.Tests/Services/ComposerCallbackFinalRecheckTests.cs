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
/// THE FRESHNESS GUARD'S FINAL RE-CHECK: a queued subscription callback must be dropped when the
/// circuit's binding is REPLACED WHILE the guard's own authorization re-query is in flight.
/// <para>
/// This is the narrowest and hardest race in the guard. The guard makes TWO binding-identity
/// decisions: the cheap one BEFORE the authorization re-query, and a FINAL one AFTER it. The second
/// one matters because the authorization re-query re-runs the whole selection — and a selection that
/// lands on a DIFFERENT runtime replaces the binding record WHILE the callback sits between the two
/// checks.
/// </para>
/// <para>
/// The race is made DETERMINISTIC by an accessor that answers the FIRST acquisition with one real
/// runtime and the SECOND (the guard's re-query) with another — so the binding is provably replaced
/// exactly inside the guard's window. No sleep, no polling, no timer.
/// </para>
/// </summary>
public sealed class ComposerCallbackFinalRecheckTests : IAsyncLifetime, IDisposable
{
    private const string Model = "final-recheck-model";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-final-recheck-{Guid.NewGuid():N}");

    private int _memberId;

    public ComposerCallbackFinalRecheckTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _memberId = (await _auth.SeedAsync("final-recheck-member", UserRole.Member)).Id;

    /// <summary>
    /// A CALLBACK WHOSE BINDING IS REPLACED DURING THE GUARD'S RE-QUERY IS DROPPED: the guard's
    /// authorization re-query selects a DIFFERENT runtime, the binding record is replaced, and the
    /// final re-check must notice and drop the callback — it must NOT apply work captured for the
    /// runtime that is no longer displayed.
    /// <para>
    /// THE DISCRIMINATOR: removing the final re-check makes the callback APPLY (its counter goes to
    /// 1), so this row pins the SECOND decision of the guard, not just the first.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACallbackWhoseBindingIsReplacedDuringTheRequery_IsDropped()
    {
        // TWO real runtimes over the SAME account: the first is what the page binds, the second is
        // what the guard's re-query selects (so the binding is replaced inside the guard's window).
        var firstComposer = NewComposer();
        var secondComposer = NewComposer();
        var accessor = new ReplacingAccessor(
            await NewRuntime(firstComposer),
            await NewRuntime(secondComposer));

        await using var host = NewHost(accessor);
        await using var rendered = await RenderAsync(host, accessor);

        // The page bound the FIRST runtime.
        Assert.Same(firstComposer, BoundComposerOf(rendered.Page));
        var capturedBefore = rendered.Page.CurrentCallbackBinding;
        Assert.NotNull(capturedBefore);

        // HOLD the callback so its body is genuinely pending, then raise the production handler.
        await using var hold = rendered.HoldCallbacks();
        rendered.Raise("HandleStreamingUpdate");
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);

        // RELEASE: the body now runs. Its guard re-queries the accessor, which answers with the
        // SECOND runtime — so the binding record is replaced inside the guard's window.
        hold.Release();
        await rendered.QueuedCallbacksAsync();

        // The replacement really happened (the guard's re-query selected the second runtime).
        Assert.True(accessor.AcquireCalls >= 2, "the guard must have re-queried the accessor");
        Assert.NotSame(firstComposer, BoundComposerOf(rendered.Page));
        Assert.Same(secondComposer, BoundComposerOf(rendered.Page));

        // ...and the guard's FINAL re-check dropped the callback: it did not apply work captured for
        // the runtime the circuit no longer displays.
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);
        Assert.Equal(1, rendered.Page.DroppedCallbackCount);

        // The component is bound to the NEW runtime — the callback did not re-attach it to the old one.
        Assert.Same(secondComposer, SubscribedComposerOf(rendered.Page));
        Assert.NotSame(firstComposer, SubscribedComposerOf(rendered.Page));
    }

    /// <summary>
    /// THE POSITIVE CONTROL: the very same path, WITHOUT the mid-window replacement, APPLIES the
    /// callback. Without this row the stale row above could be satisfied by dropping every callback
    /// unconditionally.
    /// </summary>
    [Fact]
    public async Task ACallbackWhoseBindingIsUnchangedDuringTheRequery_IsApplied()
    {
        var firstComposer = NewComposer();
        var accessor = new ReplacingAccessor(
            await NewRuntime(firstComposer),
            await NewRuntime(NewComposer()),
            replaceOnSecondAcquire: false);

        await using var host = NewHost(accessor);
        await using var rendered = await RenderAsync(host, accessor);

        await using var hold = rendered.HoldCallbacks();
        rendered.Raise("HandleStreamingUpdate");
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);

        hold.Release();
        await rendered.QueuedCallbacksAsync();

        // The same path APPLIED the callback, so the stale row above is discriminating.
        Assert.Equal(1, rendered.Page.AppliedCallbackCount);
        Assert.Equal(0, rendered.Page.DroppedCallbackCount);
        Assert.Same(firstComposer, BoundComposerOf(rendered.Page));
    }

    // ── The CONTENT-RESTORING callback ───────────────────────────────────────────────────────────

    /// <summary>
    /// THE CONTENT-RESTORING CALLBACK CANNOT RESTORE AN OLD THREAD AFTER A DEMOTION: the
    /// <c>HandleActiveInjection</c> handler is the one that RELOADS the history, so it is the callback
    /// that could really put a previous account's thread back on screen. It is queued, the account is
    /// demoted, and the queued body must do NOTHING: the thread stays hidden and the forbidden view
    /// is what is rendered.
    /// <para>
    /// THE DISCRIMINATOR is the CONTENT: without the guard, the queued body would call
    /// <c>LoadHistory()</c> and the per-user marker would be back on screen.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheContentRestoringCallback_CannotRestoreAnOldThreadAfterADemotion()
    {
        await using var host = NewHostWithRealAccessor();

        // The Member's own per-user session, so a history reload WOULD show a thread.
        var memberState = await ComposerUserState.ResolveAsync(_auth.Directory.Service, _memberId, Ct);
        var perUserPath = ComposerUserState.SessionFilePath(host.StateDir, memberState);
        Directory.CreateDirectory(Path.GetDirectoryName(perUserPath)!);
        var session = SharpCoder.AgentSession.Create("composer");
        session.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "restorable-thread-marker"));
        await session.SaveAsync(perUserPath, Ct);

        await using var rendered = await RenderAsync(host, null);

        // The first render really shows the Member's thread — so a restore would be observable.
        Assert.Contains("restorable-thread-marker", rendered.Html, StringComparison.Ordinal);

        // HOLD the callback, then raise the CONTENT-RESTORING handler.
        await using var hold = rendered.HoldCallbacks();
        rendered.Raise("HandleActiveInjection");
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);

        // The account is demoted, and the page's own operation observes it (the forbidden transition).
        await _auth.SetRoleAsync("final-recheck-member", UserRole.Viewer);
        try
        {
            await rendered.ConnectRuntimeAsync();
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, BoundStatusOf(rendered.Page));

            // RELEASE: the queued content-restoring body runs now.
            hold.Release();
            await rendered.QueuedCallbacksAsync();

            // It did NOTHING: the thread was NOT restored, and the page is still forbidden.
            Assert.Equal(0, rendered.Page.AppliedCallbackCount);
            Assert.Equal(1, rendered.Page.DroppedCallbackCount);
            Assert.DoesNotContain("restorable-thread-marker", rendered.Render(), StringComparison.Ordinal);
            Assert.Contains("You do not have access to the Composer.", rendered.Render(), StringComparison.Ordinal);
            Assert.Null(BoundComposerOf(rendered.Page));
            Assert.Null(SubscribedComposerOf(rendered.Page));
        }
        finally
        {
            await _auth.SetRoleAsync("final-recheck-member", UserRole.Member);
        }
    }

    /// <summary>
    /// THE POSITIVE CONTROL FOR THE CONTENT-RESTORING CALLBACK: the very same handler, with nothing
    /// changed, DOES reload the history (and the thread's marker is rendered). Without this row the
    /// stale row above could be satisfied by a handler that never restores content at all.
    /// </summary>
    [Fact]
    public async Task TheContentRestoringCallback_ReloadsTheHistory_WhenNothingChanged()
    {
        await using var host = NewHostWithRealAccessor();

        var memberState = await ComposerUserState.ResolveAsync(_auth.Directory.Service, _memberId, Ct);
        var perUserPath = ComposerUserState.SessionFilePath(host.StateDir, memberState);
        Directory.CreateDirectory(Path.GetDirectoryName(perUserPath)!);
        var session = SharpCoder.AgentSession.Create("composer");
        session.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "restorable-thread-marker"));
        await session.SaveAsync(perUserPath, Ct);

        await using var rendered = await RenderAsync(host, null);
        Assert.Contains("restorable-thread-marker", rendered.Html, StringComparison.Ordinal);

        await using var hold = rendered.HoldCallbacks();
        rendered.Raise("HandleActiveInjection");
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);

        hold.Release();
        await rendered.QueuedCallbacksAsync();

        // The callback was APPLIED (so the guard really let it through) and the thread is still there.
        Assert.Equal(1, rendered.Page.AppliedCallbackCount);
        Assert.Equal(0, rendered.Page.DroppedCallbackCount);
        Assert.Contains("restorable-thread-marker", rendered.Render(), StringComparison.Ordinal);
    }

    // ── The scripted accessor ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A scripted accessor that answers the FIRST circuit acquisition with the first runtime and —
    /// when asked — every LATER acquisition with another one, so the page's binding is replaced exactly
    /// inside the guard's window (and STAYS replaced, so the guard's final re-check sees it).
    /// </summary>
    private sealed class ReplacingAccessor(
        ComposerUserRuntime first,
        ComposerUserRuntime second,
        bool replaceOnSecondAcquire = true) : IComposerRuntimeAccessor
    {
        private int _acquires;

        /// <summary>How many circuit acquisitions the accessor has answered.</summary>
        internal int AcquireCalls => Volatile.Read(ref _acquires);

        /// <inheritdoc />
        public bool AuthEnabled => true;

        /// <inheritdoc />
        public Task<ComposerRuntimeAccess> AcquireForCircuitAsync(
            Task<AuthenticationState>? authenticationState, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _acquires);
            var runtime = call == 1 || !replaceOnSecondAcquire ? first : second;

            return Task.FromResult(ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, runtime)));
        }

        /// <inheritdoc />
        public Task<ComposerRuntimeAccess> ConnectForCircuitAsync(
            Task<AuthenticationState>? authenticationState, CancellationToken ct) =>
            AcquireForCircuitAsync(authenticationState, ct);

        /// <inheritdoc />
        public Task<ComposerRuntimeAccess> AcquireForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            AcquireForCircuitAsync(null, ct);

        /// <inheritdoc />
        public Task<ComposerRuntimeAccess> ConnectForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            AcquireForCircuitAsync(null, ct);
    }

    // ── Host / rendering ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A host over the PRODUCTION registration seam whose runtime accessor is the PRODUCTION one
    /// (the real accessor over the real manager), so the rows that need the real selection path can
    /// use it unchanged.
    /// </summary>
    private Host NewHostWithRealAccessor() => NewHost(accessor: null);

    /// <summary>Builds a host over the PRODUCTION registration seam.</summary>
    private Host NewHost(IComposerRuntimeAccessor? accessor)
    {
        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

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
            chatClientFactory: _ => new Mock<IChatClient>().Object,
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

        if (accessor is not null)
        {
            // The row's scripted accessor replaces the production one — it is the seam the row is
            // about. The LAST registration for the interface wins, exactly as a host override behaves.
            services.AddScoped<IComposerRuntimeAccessor>(_ => accessor);
        }

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, provider.GetRequiredService<ComposerRuntimeManager>(), stateDir);
    }

    /// <summary>A real Composer over an isolated state directory.</summary>
    private Composer NewComposer()
    {
        var stateDir = Path.Combine(_dir, $"composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        return new Composer(
            Model,
            NullLogger<Composer>.Instance,
            new GoalStore(_db, NullLogger<GoalStore>.Instance),
            stateDir: stateDir,
            availableModels: [Model],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
    }

    /// <summary>A runtime handle over <paramref name="composer"/> for the seeded account.</summary>
    private async Task<ComposerUserRuntime> NewRuntime(Composer composer)
    {
        var state = await ComposerUserState.ResolveAsync(_auth.Directory.Service, _memberId, Ct);
        return new ComposerUserRuntime(
            state,
            composer,
            new ComposerAttachmentService(_dir, NullLogger<ComposerAttachmentService>.Instance),
            subscriber: null,
            new ComposerConstructionSnapshot(composer.StartupDefaultModel, compactionModel: null));
    }

    /// <summary>Renders the REAL page over the production seam with the supplied accessor.</summary>
    private async Task<Rendered> RenderAsync(Host host, IComposerRuntimeAccessor? accessor)
    {
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "final-recheck-member")], authenticationType: "Cookies")));

        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        ComposerChat? page = null;
        Func<string>? rootHtml = null;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = Task.FromResult(state),
                ["ChildContent"] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<ComposerChat>(0);
                    builder.AddComponentReferenceCapture(1, captured => page = (ComposerChat)captured);
                    builder.CloseComponent();
                }),
            });

            // THE RETAINED ROOT: kept for the page's whole life, so every later markup read is the ORIGINAL page's
            // own output (a freshly created page refusing would prove nothing about the callback's page).
            var root = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
            rootHtml = root.ToHtmlString;
            return 0;
        });

        Assert.NotNull(page);
        return new Rendered(page!, scope, renderer, rootHtml!);
    }

    /// <summary>The Composer the page is currently bound to, or <c>null</c>.</summary>
    private static Composer? BoundComposerOf(ComposerChat page)
    {
        var binding = typeof(ComposerChat)
            .GetField("_circuit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(page);
        return (Composer?)binding?.GetType().GetProperty("Composer")!.GetValue(binding);
    }

    /// <summary>The circuit status the page currently holds.</summary>
    private static ComposerChat.ComposerCircuitStatus BoundStatusOf(ComposerChat page)
    {
        var binding = typeof(ComposerChat)
            .GetField("_circuit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(page);
        Assert.NotNull(binding);
        return (ComposerChat.ComposerCircuitStatus)binding!.GetType().GetProperty("Status")!.GetValue(binding)!;
    }

    /// <summary>The Composer the component is CURRENTLY SUBSCRIBED to, or <c>null</c>.</summary>
    private static Composer? SubscribedComposerOf(ComposerChat page) =>
        (Composer?)typeof(ComposerChat)
            .GetField("_subscribedComposer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(page);

    /// <summary>ONE RENDERED CIRCUIT.</summary>
    private sealed class Rendered(
        ComposerChat page,
        IServiceScope scope,
        HtmlRenderer renderer,
        Func<string> rootHtml) : IAsyncDisposable
    {
        public ComposerChat Page { get; } = page;

        /// <summary>
        /// THE MARKUP AS IT STANDS NOW, read from the RETAINED original root — the component's own latest render,
        /// never a freshly created page.
        /// </summary>
        public string Render() => renderer.Dispatcher.InvokeAsync(rootHtml).GetAwaiter().GetResult();

        /// <summary>The current markup.</summary>
        public string Html => Render();

        /// <summary>Waits for the page's most recently queued callback body to finish.</summary>
        public Task QueuedCallbacksAsync() => renderer.Dispatcher.InvokeAsync(() => Page.LastCallbackCompletion);

        /// <summary>Holds every queued subscription body until the returned gate is released.</summary>
        internal CallbackHold HoldCallbacks()
        {
            var gate = new CallbackHold();
            Page.CallbackGate = gate.InvokeAsync;
            return gate;
        }

        /// <summary>Invokes the page's own deferred-banner connect control through its dispatcher.</summary>
        internal async Task ConnectRuntimeAsync()
        {
            var method = typeof(ComposerChat).GetMethod(
                "ConnectRuntimeAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);

            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var task = (Task?)method!.Invoke(Page, null);
                Assert.NotNull(task);
                await task!;
            });
        }

        /// <summary>Invokes ONE production subscription handler by name, off the dispatcher.</summary>
        internal void Raise(string handlerName)
        {
            var method = typeof(ComposerChat).GetMethod(
                handlerName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);

            var parameters = method!.GetParameters().Length == 0 ? null : new object?[] { "injected" };
            method.Invoke(Page, parameters);
        }

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            scope.Dispose();
        }
    }

    /// <summary>A HELD CALLBACK GATE.</summary>
    private sealed class CallbackHold : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Hands the body to the hold: it stays PENDING until <see cref="Release"/>.</summary>
        internal async Task InvokeAsync(Func<Task> body)
        {
            await _release.Task;
            await body();
        }

        /// <summary>Releases the held bodies.</summary>
        internal void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A JS runtime that answers nothing.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default!;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default!;
    }

    /// <summary>The provider, the manager and the state directory of one host.</summary>
    private sealed class Host(
        ServiceProvider provider,
        ComposerRuntimeManager manager,
        string stateDir) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public ComposerRuntimeManager Manager { get; } = manager;

        public string StateDir { get; } = stateDir;

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();

            async Task Step(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add(ex); }
            }

            await Step(() => Manager.ShutdownAsync());
            await Step(() => Provider.DisposeAsync().AsTask());

            if (failures.Count > 0)
                throw new AggregateException("Final-recheck host cleanup failed", failures);
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
