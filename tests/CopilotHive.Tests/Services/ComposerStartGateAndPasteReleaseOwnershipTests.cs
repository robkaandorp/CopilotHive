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
/// TWO OWNERSHIP WINDOWS, each pinned by a held-answer rendezvous (explicit task gates, no sleeps, no polling):
/// <list type="number">
/// <item>the ORDINARY / START-GATE acquisition must not adopt an answer that a NEWER selection superseded while the
/// answer was in flight — and the storage write behind that gate must then never start;</item>
/// <item>an OLD asynchronous paste release must clean up ONLY the resources it detached, never a NEWER install's
/// .NET reference or module.</item>
/// </list>
/// Each row has a serial / authorized no-change control so the fix cannot pass by refusing everything or by never
/// releasing anything.
/// </summary>
public sealed class ComposerStartGateAndPasteReleaseOwnershipTests : IAsyncLifetime
{
    private const string Model = "ownership-model";
    private const string MemberGh = "ownership-member";
    private const string Marker = "ownership-thread-marker";

    private const System.Reflection.BindingFlags NonPublicInstance =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-ownership-{Guid.NewGuid():N}");

    private int _memberId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _ = await _auth.SeedAsync("ownership-original", UserRole.Admin);
        _memberId = (await _auth.SeedAsync(MemberGh, UserRole.Member)).Id;
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        _auth.Dispose();
        try { if (Directory.Exists(_dir)) TestHelpers.ForceDeleteDirectory(_dir); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    // ── FINDING 1: the start gate must not adopt a superseded answer ─────────────────────────────

    /// <summary>
    /// REFUSAL VECTOR. The start-authorization answer (Ready) is HELD before any storage read; the account is then
    /// demoted and a NEWER operation applies its own Forbidden selection; only then is the older answer released.
    /// The older answer must NOT overwrite the newer binding, must NOT reinstall subscriptions, and the write behind
    /// the gate must never start (zero files, zero staged records).
    /// </summary>
    [Fact]
    public async Task AHeldStartGateAnswer_ReleasedAfterANewerRefusal_NeitherRebindsNorWrites()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host);
        var root = rendered.StoreRoot;
        Assert.NotNull(rendered.Subscribed);

        await rendered.ParkCallbacksAsync();
        var hold = host.Gate.Arm();
        var paste = rendered.PasteAsync(new MemoryStream([1, 2, 3, 4]));
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct); // start answer (Ready) computed, HELD

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.UiEventAsync("ConnectRuntimeAsync"); // the NEWER operation applies Forbidden
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            var newer = rendered.Page.CurrentCallbackBinding;
            Assert.Null(rendered.Subscribed);
            Assert.Empty(Directory.GetFiles(root));

            hold.Release();
            await paste.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.Same(newer, rendered.Page.CurrentCallbackBinding);
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.Empty(Directory.GetFiles(root));
            Assert.Null(rendered.Pending);
        }
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    // The REPLACEMENT vector (removal + recreation) lives in ComposerStartGateReplacementOwnershipTests, whose
    // carrier gates every concurrent path. The earlier row here asserted "the newer operation bound B" immediately
    // after the removal with those paths uncontrolled, so it was scheduling-dependent; it was removed at the
    // reviewer's request once that stable carrier existed.

    /// <summary>
    /// A SUPERSEDED FACADE-ADOPTION ANSWER IS DROPPED: a REAL facade operation (compaction) completes, and the
    /// circuit-adoption query that follows it (<c>AdoptCurrentSelectionAsync</c>) is HELD. A newer operation applies a
    /// Forbidden selection; the older adoption answer (Ready) is then released and must not overwrite it or
    /// re-subscribe.
    /// </summary>
    [Fact]
    public async Task AHeldFacadeAdoptionAnswer_ReleasedAfterANewerRefusal_DoesNotOverwriteIt()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host);
        Assert.NotNull(rendered.Subscribed);

        // CompactSession's facade call uses the REQUEST path (never gated); the FIRST circuit acquisition after it is
        // the adoption query, so arming the hold now catches exactly that one.
        await rendered.ParkCallbacksAsync();
        var hold = host.Gate.Arm();
        var compact = rendered.UiEventAsync("CompactSession");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(compact.IsCompleted, "the adoption answer must be genuinely held");

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.UiEventAsync("ConnectRuntimeAsync");
            var newer = rendered.Page.CurrentCallbackBinding;
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);

            hold.Release();
            await compact.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.Same(newer, rendered.Page.CurrentCallbackBinding);
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);
        }
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>CONTROL: the same held facade-adoption answer with NO newer selection is adopted (page stays Ready, subscribed).</summary>
    [Fact]
    public async Task AHeldFacadeAdoptionAnswer_WithNoNewerSelection_IsAdopted()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host);
        var composer = rendered.Page.CurrentCallbackBinding!.Composer;

        await rendered.ParkCallbacksAsync();
        var hold = host.Gate.Arm();
        var compact = rendered.UiEventAsync("CompactSession");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        hold.Release();
        await compact.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
        Assert.Same(composer, rendered.Subscribed);
    }

    /// <summary>
    /// A SUPERSEDED ORDINARY-MUTATION ANSWER IS DROPPED: <c>ResetSession</c> is an ordinary mutation acquisition (not
    /// a facade adoption); its held answer must not overwrite a newer Forbidden binding.
    /// </summary>
    [Fact]
    public async Task AHeldOrdinaryMutationAnswer_ReleasedAfterANewerRefusal_DoesNotOverwriteIt()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host);

        await rendered.ParkCallbacksAsync();
        var hold = host.Gate.Arm();
        var reset = rendered.UiEventAsync("ResetSession");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.UiEventAsync("ConnectRuntimeAsync");
            var newer = rendered.Page.CurrentCallbackBinding;
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);

            hold.Release();
            await reset.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.Same(newer, rendered.Page.CurrentCallbackBinding);
            Assert.Null(rendered.Subscribed);
        }
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>CONTROL: with NO newer selection the same held start answer is adopted and the paste writes and stages.</summary>
    [Fact]
    public async Task AHeldStartGateAnswer_WithNoNewerSelection_StillWritesAndStages()
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

    // ── FINDING 2: an old async release must not destroy a newer install ─────────────────────────

    /// <summary>
    /// HELD RELEASE / NEW INSTALL. The first listener's uninstall response is HELD while the page is restored to
    /// Ready and installs a NEW listener. When the old release finally completes it must dispose ONLY its own
    /// resources: the new .NET reference stays usable, the new module is retained and undisposed, the old module is
    /// disposed exactly once, the old reference is disposed (not leaked) and the install flag reflects the new
    /// install.
    /// </summary>
    [Fact]
    public async Task AHeldPasteRelease_CompletingAfterANewInstall_LeavesTheNewInstallUntouched()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host);
        await rendered.AfterRenderAsync();
        var oldModule = Assert.Single(host.Js.Modules);
        var oldRef = rendered.DotNetRef;
        Assert.NotNull(oldRef);
        oldModule.HoldUninstall();

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        Task release;
        try
        {
            await rendered.UiEventAsync("ConnectRuntimeAsync");
            release = rendered.AfterRenderAsync(); // starts the old release; uninstall is HELD
            await oldModule.UninstallEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(release.IsCompleted);
        }
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }

        await rendered.UiEventAsync("ConnectRuntimeAsync"); // restored Ready
        await rendered.AfterRenderAsync();                   // installs the NEW listener
        Assert.Equal(2, host.Js.Modules.Count);
        var newModule = host.Js.Modules[1];
        var newRef = rendered.DotNetRef;
        Assert.NotNull(newRef);
        Assert.NotSame(oldRef, newRef);
        Assert.Equal(1, newModule.Calls.Count(c => c == "install"));
        _ = newRef!.Value; // usable BEFORE the old release completes

        oldModule.ReleaseUninstall();
        await release.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The NEW install is completely untouched.
        _ = newRef.Value; // still usable: throws ObjectDisposedException if the old release disposed it
        Assert.Same(newRef, rendered.DotNetRef);
        Assert.Same(newModule, rendered.PasteModule);
        Assert.Equal(0, newModule.DisposeCount);
        Assert.Equal(0, newModule.Calls.Count(c => c == "uninstall"));
        Assert.True(rendered.PasteInstalled);

        // The OLD release cleaned up exactly its own resources.
        Assert.Equal(1, oldModule.DisposeCount);
        Assert.Equal(1, oldModule.Calls.Count(c => c == "uninstall"));
        Assert.Throws<ObjectDisposedException>(() => oldRef!.Value);
    }

    /// <summary>CONTROL: the SERIAL release-then-install order cleans the old install and keeps the new one.</summary>
    [Fact]
    public async Task ASerialPasteRelease_ThenANewInstall_CleansTheOldAndKeepsTheNew()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host);
        await rendered.AfterRenderAsync();
        var oldModule = Assert.Single(host.Js.Modules);
        var oldRef = rendered.DotNetRef;

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.UiEventAsync("ConnectRuntimeAsync");
            await rendered.AfterRenderAsync(); // release completes before anything else happens
        }
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }

        await rendered.UiEventAsync("ConnectRuntimeAsync");
        await rendered.AfterRenderAsync();

        var newModule = host.Js.Modules[1];
        var newRef = rendered.DotNetRef;
        Assert.Equal(1, oldModule.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => oldRef!.Value);
        Assert.Equal(0, newModule.DisposeCount);
        _ = newRef!.Value;
        Assert.Same(newModule, rendered.PasteModule);
        Assert.True(rendered.PasteInstalled);
    }

    // ── Host / rendering ─────────────────────────────────────────────────────────────────────────

    private async Task SeedThreadAsync(Host host)
    {
        var state = await ComposerUserState.ResolveAsync(_auth.Directory.Service, _memberId, Ct);
        var path = ComposerUserState.SessionFilePath(host.StateDir, state);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var session = SharpCoder.AgentSession.Create("composer");
        session.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, Marker));
        await session.SaveAsync(path, Ct);
    }

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
            sp.GetRequiredService<UserService>(), sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(), sp.GetRequiredService<HivePolicy>()));
        services.AddSingleton(sp => new ComposerFactory(
            sp, stateDir, ollamaApiKey: null,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            isTokenAvailable: () => true,
            providerOf: _ => "ollama-local"));
        services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
        services.AddSingleton(sp => new ComposerRuntimeManager(
            sp.GetRequiredService<ComposerFactory>(), sp.GetRequiredService<UserService>(),
            NullLogger<ComposerRuntimeManager>.Instance));
        services.AddSingleton(new ComposerAttachmentService(stateDir, NullLogger<ComposerAttachmentService>.Instance));
        services.AddSingleton<IConfigFacade>(sp => new ConfigFacade(
            null, null, null, NullLogger<ConfigFacade>.Instance,
            sp.GetRequiredService<HivePrincipalResolver>(), sp.GetRequiredService<HivePolicy>()));
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

        public DotNetObjectReference<ComposerChat>? DotNetRef => (DotNetObjectReference<ComposerChat>?)Field("_pasteDotNetRef");

        public IJSObjectReference? PasteModule => (IJSObjectReference?)Field("_pasteModule");

        public bool PasteInstalled => (bool)Field("_pasteInstalled")!;

        private object? Field(string name) => typeof(ComposerChat).GetField(name, NonPublicInstance)!.GetValue(Page);

        public async Task InvokeAsync(string method, params object?[] args)
        {
            var m = typeof(ComposerChat).GetMethod(method, NonPublicInstance | System.Reflection.BindingFlags.Public);
            Assert.NotNull(m);
            await renderer.Dispatcher.InvokeAsync(async () => await (Task)m!.Invoke(Page, args.Length == 0 ? null : args)!);
        }

        public Task UiEventAsync(string method, params object?[] args) => Track(renderer.Dispatcher.InvokeAsync(() =>
            EventCallback.Factory.Create(Page, (Func<Task>)(() => InvokeAsync(method, args))).InvokeAsync()));

        /// <summary>The component's own OnAfterRenderAsync (the static renderer never calls it; a real circuit does).</summary>
        public Task AfterRenderAsync() => Track(InvokeAsync("OnAfterRenderAsync", false));

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
            host.Js.ReleaseAll();
            foreach (var task in _writes)
                await Step(() => task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            await Step(() => Page.LastCallbackCompletion.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            await Step(() => renderer.DisposeAsync().AsTask());
            await Step(() => { scope.Dispose(); return Task.CompletedTask; });
            if (failures.Count > 0) throw new AggregateException("Ownership circuit cleanup failed", failures);
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
            Js.ReleaseAll();
            var failures = new List<Exception>();
            try { await Manager.ShutdownAsync(); } catch (Exception ex) { failures.Add(ex); }
            try { await Provider.DisposeAsync(); } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count > 0) throw new AggregateException("Ownership host cleanup failed", failures);
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

        public void ReleaseAll() { foreach (var module in Modules) module.ReleaseUninstall(); }

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

    /// <summary>A paste module that records its calls and can HOLD its <c>uninstall</c> response.</summary>
    private sealed class RecordingModule : IJSObjectReference
    {
        private TaskCompletionSource? _uninstallGate;

        public List<string> Calls { get; } = [];
        public int DisposeCount { get; private set; }
        public TaskCompletionSource UninstallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HoldUninstall() => _uninstallGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseUninstall() => _uninstallGate?.TrySetResult();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            Calls.Add(identifier);
            if (identifier == "uninstall" && _uninstallGate is not null)
            {
                UninstallEntered.TrySetResult();
                await _uninstallGate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return default!;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
