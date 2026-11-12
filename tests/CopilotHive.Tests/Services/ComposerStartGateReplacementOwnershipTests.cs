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
using Microsoft.JSInterop;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE DETERMINISTIC REPLACEMENT VECTOR for the start-gate ownership contract: the start answer for
/// runtime A is HELD, the account's runtime is removed and a FRESH runtime B is created for the same
/// account, a NEWER operation binds B, and only then is the older answer released — which must not
/// overwrite B's binding, must not re-subscribe A, and must not start a storage write.
/// <para>
/// WHY THE SCENARIO NEEDS EXPLICIT CONTROL OF EVERY IN-FLIGHT PATH: <c>RemoveAsync</c> already awaits the removed
/// entry's complete teardown AND its cache eviction, so once it returns the next acquisition creates a fresh
/// runtime B — the removal itself is not the race. The race is every OTHER path running concurrently with it:
/// disposing runtime A can raise Composer events, each of which queues a subscription callback on the page's
/// dispatcher, and such a callback's own freshness re-query can bind a <see cref="ComposerRuntimeStatus.Removed"/>
/// selection (the manager answers Removed while the teardown is still running). That SUPERSEDES the binding the newer
/// operation captured when it started, so the newer operation is — correctly — not adopted and binds no B. Whether
/// that happens depends on scheduling, which is exactly the flake an assertion on "the newer operation bound B"
/// observed.
/// </para>
/// <para>
/// This file therefore enumerates the concurrent paths and gates EACH of them:
/// <list type="number">
/// <item>the OLDER start-gate acquisition — HELD by the decorated real accessor until after the newer operation;</item>
/// <item>incidental teardown-triggered subscription callbacks — PARKED with the page's <c>CallbackGate</c> from before
/// the removal until after the final assertions, so none can bind, drop or re-render mid-scenario (the row asserts
/// that none ran: zero applied, zero dropped);</item>
/// <item>the NEWER operation's own acquisition — awaited to COMPLETION (its returned task is the milestone: the
/// operation returns only after its adoption/bind), never observed post hoc;</item>
/// <item>the removal's teardown and eviction — awaited by <c>RemoveAsync</c> itself.</item>
/// </list>
/// With those controlled, nothing else can touch the binding between the removal and the release, so the newer
/// operation deterministically binds the freshly created runtime B.
/// </para>
/// <para>
/// The held answer is the REAL accessor's computed answer (decorated, never faked), and the storage
/// outcome is observed on the real filesystem.
/// </para>
/// </summary>
public sealed class ComposerStartGateReplacementOwnershipTests : IAsyncLifetime, IDisposable
{
    private const string Model = "start-gate-replacement-model";
    private const string MemberGh = "start-gate-replacement-member";

    private const System.Reflection.BindingFlags NonPublicInstance =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-start-gate-repl-{Guid.NewGuid():N}");

    private int _memberId;

    public ComposerStartGateReplacementOwnershipTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _ = await _auth.SeedAsync("start-gate-replacement-original", UserRole.Admin);
        _memberId = (await _auth.SeedAsync(MemberGh, UserRole.Member)).Id;
    }

    /// <summary>
    /// THE HELD START ANSWER MUST NOT REBIND THE OLD RUNTIME OR WRITE ANYTHING: the start answer for
    /// runtime A is held, A is removed and its REAL teardown is awaited, a NEWER operation binds the
    /// FRESH runtime B, and only then is the older answer released — which must leave B's binding, B's
    /// subscription and the (empty) store exactly as the newer operation left them.
    /// <para>
    /// THE DISCRIMINATOR: with the unconditional Bind restored, the released answer re-binds runtime A
    /// (the page's composer becomes A again, not B) and the storage write starts.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AHeldStartAnswer_ReleasedAfterTheReplacementCompleted_NeitherRebindsNorWrites()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host);
        var root = rendered.StoreRoot;
        var a = rendered.Page.CurrentCallbackBinding!.Composer;

        // PATH 2: PARK every subscription callback BEFORE the removal. Disposing runtime A may raise events; each
        // queued body waits here (it neither binds, drops nor re-renders) until the row is done asserting.
        // The helper first DRAINS callbacks the initial render already queued (they could take the armed hold), then
        // parks every later one.
        await rendered.ParkCallbacksAsync();
        try
        {
            // PATH 1: HOLD the REAL accessor's start answer for the paste's ordinary acquisition.
            var hold = host.Gate.Arm();
            var paste = rendered.PasteAsync(new MemoryStream([1, 2, 3, 4]));
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(paste.IsCompleted, "the older start answer must be genuinely held across the newer operation");

            // PATH 4: RemoveAsync awaits the full teardown AND the cache eviction, so the entry is gone on return.
            Assert.True(await host.Manager.RemoveAsync(_memberId));

            // PATH 3: the NEWER operation's acquisition runs to COMPLETION — the returned task is the milestone.
            await rendered.UiEventAsync("ConnectRuntimeAsync");
            var newer = rendered.Page.CurrentCallbackBinding;
            var b = newer!.Composer;
            Assert.NotNull(b);
            Assert.NotSame(a, b);
            Assert.Same(b, rendered.Subscribed);
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);

            // No parked callback ran at any point of the scenario.
            Assert.Equal(0, rendered.Page.AppliedCallbackCount);
            Assert.Equal(0, rendered.Page.DroppedCallbackCount);

            // RELEASE the older answer (about the removed runtime A).
            hold.Release();
            await paste.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // The newer binding and its subscription SURVIVE untouched...
            Assert.Same(newer, rendered.Page.CurrentCallbackBinding);
            Assert.Same(b, rendered.Subscribed);
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);

            // ...and NO storage mutation happened: zero files, zero staged records.
            Assert.Empty(Directory.GetFiles(root));
            Assert.Null(rendered.Pending);
        }
        finally
        {
            // Parked callbacks are released by disposal (they are dropped as stale) and joined before teardown.
        }
    }

    /// <summary>
    /// CONTROL: with NO newer selection, the very same held start answer IS adopted and the ordinary
    /// paste path still writes and stages its file — so the row above cannot be satisfied by refusing
    /// every paste.
    /// </summary>
    [Fact]
    public async Task AHeldStartAnswer_WithNoNewerSelection_IsAdopted_AndThePasteStillWrites()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host);
        var root = rendered.StoreRoot;
        var composer = rendered.Page.CurrentCallbackBinding!.Composer;

        await rendered.ParkCallbacksAsync();
        var hold = host.Gate.Arm();
        var paste = rendered.PasteAsync(new MemoryStream([1, 2, 3, 4]));
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        hold.Release();
        await paste.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.NotNull(rendered.Pending);
        Assert.Single(Directory.GetFiles(root));
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
        Assert.Same(composer, rendered.Subscribed);
    }

    // ── Host / rendering ─────────────────────────────────────────────────────────────────────────

    private Host NewHost()
    {
        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        var js = new RecordingJsRuntime();
        var gate = new GatingAccessor();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<IJSRuntime>(js);
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
        services.AddComposerRuntimeFacades(ComposerRuntimeFacadeDeployment.PerUserRuntime);
        services.AddScoped<IComposerRuntimeAccessor>(sp => gate.Wrap(sp.GetRequiredService<ComposerRuntimeAccessor>()));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        return new Host(provider, provider.GetRequiredService<ComposerRuntimeManager>(), stateDir, js, gate);
    }

    private async Task<Rendered> RenderAsync(Host host)
    {
        var state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, MemberGh)], authenticationType: "Cookies"))));

        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        ComposerChat? page = null;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = state,
                ["ChildContent"] = (RenderFragment)(b =>
                {
                    b.OpenComponent<ComposerChat>(0);
                    b.AddComponentReferenceCapture(1, c => page = (ComposerChat)c);
                    b.CloseComponent();
                }),
            });

            _ = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
        });

        Assert.NotNull(page);
        return new Rendered(page!, scope, renderer, host);
    }

    // ── Doubles ──────────────────────────────────────────────────────────────────────────────────

    private sealed class Rendered(ComposerChat page, IServiceScope scope, HtmlRenderer renderer, Host host) : IAsyncDisposable
    {
        private readonly List<Task> _writes = [];

        public ComposerChat Page { get; } = page;

        public string StoreRoot { get; } = page.CurrentCallbackBinding!.Attachments!.AttachmentsRootPath;

        public ComposerChat.ComposerCircuitStatus Status => Page.CurrentCallbackBinding!.Status;

        public Composer? Subscribed => (Composer?)Field("_subscribedComposer");

        public ComposerAttachment? Pending => (ComposerAttachment?)Field("_pending");

        private object? Field(string name) => typeof(ComposerChat).GetField(name, NonPublicInstance)!.GetValue(Page);

        public async Task InvokeAsync(string method, params object?[] args)
        {
            var m = typeof(ComposerChat).GetMethod(method, NonPublicInstance | System.Reflection.BindingFlags.Public);
            Assert.NotNull(m);
            await renderer.Dispatcher.InvokeAsync(async () => await (Task)m!.Invoke(Page, args.Length == 0 ? null : args)!);
        }

        public Task UiEventAsync(string method, params object?[] args) => Track(renderer.Dispatcher.InvokeAsync(() =>
            EventCallback.Factory.Create(Page, (Func<Task>)(() => InvokeAsync(method, args))).InvokeAsync()));

        public Task PasteAsync(Stream content) => Track(renderer.Dispatcher.InvokeAsync(() =>
            Page.HandlePasteAsync(new FakeJSStreamReference(content), "image/png", 4)));

        private TaskCompletionSource? _parked;

        /// <summary>
        /// CONTROLS THE INCIDENTAL PATHS before a hold is armed: first DRAINS any subscription callback already queued
        /// by the initial render/connect (it could otherwise take the armed hold, since the hold catches the NEXT
        /// circuit acquisition from ANY path), then PARKS every later callback behind the page's CallbackGate so none
        /// can acquire, bind, drop or re-render until the row is done (released by disposal).
        /// </summary>
        public async Task ParkCallbacksAsync()
        {
            await renderer.Dispatcher.InvokeAsync(() => Page.LastCallbackCompletion);
            var parked = _parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Page.CallbackGate = async body => { await parked.Task; await body(); };
        }

        private Task Track(Task task) { _writes.Add(task); return task; }

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();

            async Task Step(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add(ex); }
            }

            _parked?.TrySetResult();
            host.Gate.ReleaseAll();
            foreach (var task in _writes)
                await Step(() => task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            await Step(() => Page.LastCallbackCompletion.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            await Step(() => renderer.DisposeAsync().AsTask());
            await Step(() => { scope.Dispose(); return Task.CompletedTask; });
            if (failures.Count > 0) throw new AggregateException("Start-gate replacement cleanup failed", failures);
        }
    }

    private sealed class Host(
        ServiceProvider provider, ComposerRuntimeManager manager, string stateDir,
        RecordingJsRuntime js, GatingAccessor gate) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public ComposerRuntimeManager Manager { get; } = manager;
        public string StateDir { get; } = stateDir;
        public RecordingJsRuntime Js { get; } = js;
        public GatingAccessor Gate { get; } = gate;

        public async ValueTask DisposeAsync()
        {
            Gate.ReleaseAll();
            var failures = new List<Exception>();
            try { await Manager.ShutdownAsync(); } catch (Exception ex) { failures.Add(ex); }
            try { await Provider.DisposeAsync(); } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count > 0) throw new AggregateException("Start-gate replacement host cleanup failed", failures);
        }
    }

    /// <summary>Decorates the REAL scoped accessor: can HOLD one acquisition's answer after it was computed.</summary>
    private sealed class GatingAccessor
    {
        private Hold? _armed;
        private readonly List<Hold> _holds = [];

        public void ReleaseAll() { foreach (var hold in _holds) hold.Release(); }

        public IComposerRuntimeAccessor Wrap(IComposerRuntimeAccessor inner) => new Wrapper(this, inner);

        public Hold Arm()
        {
            var hold = new Hold();
            _holds.Add(hold);
            _armed = hold;
            return hold;
        }

        public sealed class Hold
        {
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Gate => _release.Task;
            public void Release() => _release.TrySetResult();
        }

        private sealed class Wrapper(GatingAccessor owner, IComposerRuntimeAccessor inner) : IComposerRuntimeAccessor
        {
            public bool AuthEnabled => inner.AuthEnabled;

            public async Task<ComposerRuntimeAccess> AcquireForCircuitAsync(Task<AuthenticationState>? s, CancellationToken ct)
            {
                var answer = await inner.AcquireForCircuitAsync(s, ct);
                var hold = Interlocked.Exchange(ref owner._armed, null);
                if (hold is not null)
                {
                    hold.Entered.TrySetResult();
                    await hold.Gate.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }
                return answer;
            }

            public Task<ComposerRuntimeAccess> ConnectForCircuitAsync(Task<AuthenticationState>? s, CancellationToken ct) =>
                inner.ConnectForCircuitAsync(s, ct);

            public Task<ComposerRuntimeAccess> AcquireForRequestAsync(ClaimsPrincipal? u, CancellationToken ct) =>
                inner.AcquireForRequestAsync(u, ct);

            public Task<ComposerRuntimeAccess> ConnectForRequestAsync(ClaimsPrincipal? u, CancellationToken ct) =>
                inner.ConnectForRequestAsync(u, ct);
        }
    }

    /// <summary>A JS runtime whose <c>import</c> yields a recording module.</summary>
    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public List<RecordingModule> Modules { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (identifier != "import") return default!;
            var module = new RecordingModule();
            Modules.Add(module);
            return ValueTask.FromResult((TValue)(object)module);
        }
    }

    /// <summary>A recording paste module.</summary>
    private sealed class RecordingModule : IJSObjectReference
    {
        public List<string> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            Calls.Add(identifier);
            return default!;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
