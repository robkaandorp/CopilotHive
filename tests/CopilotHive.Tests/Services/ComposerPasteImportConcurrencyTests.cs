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
using Microsoft.JSInterop;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE PASTE-IMPORT LIFECYCLE UNDER CONCURRENT ATTEMPTS: two import continuations can be in flight
/// at once (the textarea is released while one import is outstanding, and a new textarea starts a
/// second). Each continuation must decide, when it resumes, whether it still owns the lifecycle —
/// and a STALE continuation must never clear the flag a NEWER attempt owns.
/// <para>
/// This is the ADDITIVE coverage for the F5 repair's second half: the repair resets
/// <c>_pasteInstalled</c> only when the resuming attempt still owns the generation. The row below
/// pins that: the STALE attempt resumes LAST, and the NEWER attempt's install must survive it.
/// </para>
/// <para>
/// The harness arms MULTIPLE independent import holds, so both continuations are genuinely in
/// flight — no sleep, no polling, no timer.
/// </para>
/// </summary>
public sealed class ComposerPasteImportConcurrencyTests : IAsyncLifetime, IDisposable
{
    private const string Model = "paste-concurrency-model";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-paste-conc-{Guid.NewGuid():N}");

    private int _memberId;

    public ComposerPasteImportConcurrencyTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _memberId = (await _auth.SeedAsync("paste-conc-member", UserRole.Member)).Id;

    /// <summary>
    /// A STALE IMPORT CONTINUATION MUST NOT CLEAR THE FLAG A NEWER ATTEMPT OWNS: the first import is
    /// held, the page releases the listener and starts a SECOND import (also held), and then the
    /// FIRST (stale) continuation resumes. It must be dropped — and it must NOT clear
    /// <c>_pasteInstalled</c>, which now belongs to the newer attempt.
    /// <para>
    /// THE DISCRIMINATOR: after the stale continuation resumes, the newer attempt must still be able
    /// to install its module (exactly ONE install, and the flag still set). A stale continuation that
    /// cleared the flag would leave the page with NO listener for its textarea — the "the textarea
    /// never gets a listener" defect.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AStaleImportContinuation_DoesNotClearTheFlagANewerAttemptOwns()
    {
        await using var host = NewHost();

        // TWO independent import holds: the first and the second attempt.
        var firstHold = host.Js.ArmImport();
        var secondHold = host.Js.ArmImport();

        await using var rendered = await RenderAsync(host);

        // The FIRST attempt starts and parks inside the import.
        var firstInstallation = rendered.AfterRenderAsync();
        await firstHold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(firstInstallation.IsCompleted);

        // The page RELEASES the listener (the textarea is gone) and renders again: the release bumps
        // the generation, so the first continuation will be stale when it resumes.
        await rendered.InvokeAsync("ReleasePasteListenerAsync");
        Assert.False((bool)typeof(ComposerChat)
            .GetField("_pasteInstalled", PrivateInstance)!.GetValue(rendered.Page)!);

        // A NEW textarea appears: the SECOND attempt starts and parks inside its own import.
        var secondInstallation = rendered.AfterRenderAsync();
        await secondHold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(secondInstallation.IsCompleted);

        // NOW the STALE (first) continuation resumes — after the newer attempt took the flag.
        firstHold.Release();
        await firstInstallation.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The stale continuation installed nothing and retained nothing.
        Assert.Equal(0, host.Js.Count("install"));
        Assert.True(host.Js.Modules[0].Disposed,
            "the stale attempt's late module must be disposed, not installed or retained");

        // ...and it did NOT clear the flag the newer attempt owns.
        Assert.True((bool)typeof(ComposerChat)
            .GetField("_pasteInstalled", PrivateInstance)!.GetValue(rendered.Page)!,
            "a stale continuation must not clear the flag a newer attempt owns");

        // The newer attempt resumes and DOES install — exactly once.
        secondHold.Release();
        await secondInstallation.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(1, host.Js.Count("install"));
        Assert.False(host.Js.Modules[1].Disposed);
        Assert.True(host.Js.AllowedValues.Last());
    }

    /// <summary>
    /// A STALE IMPORT CONTINUATION MUST NOT RETAIN A MODULE THE NEWER ATTEMPT DOES NOT OWN: after the
    /// stale continuation resumes, the component's <c>_pasteModule</c> reference is still the NEWER
    /// attempt's business — the stale one must not have overwritten it with its own late module.
    /// </summary>
    [Fact]
    public async Task AStaleImportContinuation_DoesNotRetainItsOwnModule()
    {
        await using var host = NewHost();

        var firstHold = host.Js.ArmImport();
        var secondHold = host.Js.ArmImport();

        await using var rendered = await RenderAsync(host);

        var firstInstallation = rendered.AfterRenderAsync();
        await firstHold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await rendered.InvokeAsync("ReleasePasteListenerAsync");

        var secondInstallation = rendered.AfterRenderAsync();
        await secondHold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The stale continuation resumes first.
        firstHold.Release();
        await firstInstallation.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The component retains NOTHING yet: the newer attempt has not resumed, so there is no module.
        Assert.Null(typeof(ComposerChat)
            .GetField("_pasteModule", PrivateInstance)!.GetValue(rendered.Page));

        // The newer attempt resumes and installs; ITS module is the one retained.
        secondHold.Release();
        await secondInstallation.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var retained = typeof(ComposerChat)
            .GetField("_pasteModule", PrivateInstance)!.GetValue(rendered.Page);
        Assert.NotNull(retained);
        Assert.Same(host.Js.Modules[1], retained);
        Assert.True(host.Js.Modules[1].Disposed == false);
    }

    /// <summary>
    /// A DROPPED IMPORT CONTINUATION DISPOSES ITS LATE MODULE EXACTLY ONCE: the continuation that is
    /// dropped because the textarea is gone must not leak the module reference it received — it
    /// disposes it, and it disposes it ONCE (a double dispose would be a second disposal of a
    /// resource the release path may also own).
    /// <para>
    /// THE DISCRIMINATOR: a continuation that neither disposes nor installs leaks the JS module
    /// reference; one that disposes twice double-releases it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADroppedImportContinuation_DisposesItsLateModuleExactlyOnce()
    {
        await using var host = NewHost();

        var hold = host.Js.ArmImport();
        await using var rendered = await RenderAsync(host);

        var installation = rendered.AfterRenderAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(installation.IsCompleted);

        // The textarea goes away (the page is released) while the import is outstanding: the late
        // module belongs to a textarea that no longer exists.
        await rendered.InvokeAsync("ReleasePasteListenerAsync");

        hold.Release();
        await installation.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var module = Assert.Single(host.Js.Modules);
        Assert.True(module.Disposed, "the late module must be disposed, not leaked");
        Assert.True(module.DisposeCount == 1, $"the late module must be disposed exactly once (was {module.DisposeCount})");
        Assert.Equal(0, host.Js.Count("install"));

        // ...and the component retained nothing.
        Assert.Null(typeof(ComposerChat)
            .GetField("_pasteModule", PrivateInstance)!.GetValue(rendered.Page));
        Assert.Null(typeof(ComposerChat)
            .GetField("_pasteDotNetRef", PrivateInstance)!.GetValue(rendered.Page));
        Assert.False((bool)typeof(ComposerChat)
            .GetField("_pasteInstalled", PrivateInstance)!.GetValue(rendered.Page)!);
    }

    /// <summary>
    /// THE SERIAL ORDER STILL INSTALLS EXACTLY ONCE: with no overlap at all (one import, released,
    /// then the page renders again), the module is installed, retained, undisposed, and the install
    /// flag is set — the no-overlap control for the concurrency rows above.
    /// </summary>
    [Fact]
    public async Task TheSerialImportOrder_InstallsExactlyOnce_AndRetainsItsModule()
    {
        await using var host = NewHost();

        var hold = host.Js.ArmImport();
        await using var rendered = await RenderAsync(host);

        var installation = rendered.AfterRenderAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        hold.Release();
        await installation.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // A second render must not stack a second listener.
        await rendered.AfterRenderAsync();

        Assert.Equal(1, host.Js.Count("install"));
        Assert.Equal(0, host.Js.Count("uninstall"));
        var module = Assert.Single(host.Js.Modules);
        Assert.False(module.Disposed);
        Assert.Same(module, typeof(ComposerChat)
            .GetField("_pasteModule", PrivateInstance)!.GetValue(rendered.Page));
        Assert.True((bool)typeof(ComposerChat)
            .GetField("_pasteInstalled", PrivateInstance)!.GetValue(rendered.Page)!);
    }

    private const System.Reflection.BindingFlags PrivateInstance =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    // ── Host / rendering ────────────────────────────────────────────────────────────────────────

    /// <summary>Builds a host over the PRODUCTION registration seam.</summary>
    private Host NewHost()
    {
        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
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

        // The recording JS runtime replaces the null one, so the import lifecycle is observable.
        var js = new RecordingJsRuntime();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(js);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, provider.GetRequiredService<ComposerRuntimeManager>(), js, stateDir);
    }

    /// <summary>Renders the REAL page over the production seam for the Member identity.</summary>
    private async Task<Rendered> RenderAsync(Host host)
    {
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "paste-conc-member")], authenticationType: "Cookies")));

        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        ComposerChat? page = null;

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

            _ = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
            return 0;
        });

        Assert.NotNull(page);
        return new Rendered(page!, scope, renderer);
    }

    /// <summary>A JS runtime that answers nothing.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default!;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default!;
    }

    /// <summary>ONE RENDERED CIRCUIT.</summary>
    private sealed class Rendered(
        ComposerChat page,
        IServiceScope scope,
        HtmlRenderer renderer) : IAsyncDisposable
    {
        public ComposerChat Page { get; } = page;

        /// <summary>Invokes a production method on the page's dispatcher.</summary>
        public async Task InvokeAsync(string method, params object?[] args)
        {
            var m = typeof(ComposerChat).GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            Assert.NotNull(m);
            await renderer.Dispatcher.InvokeAsync(async () => await (Task)m!.Invoke(Page, args.Length == 0 ? null : args)!);
        }

        /// <summary>
        /// Runs the component's own <c>OnAfterRenderAsync</c> — the lifecycle moment where the paste
        /// listener is installed or released.
        /// </summary>
        public Task AfterRenderAsync() => InvokeAsync("OnAfterRenderAsync", false);

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            scope.Dispose();
        }
    }

    /// <summary>
    /// A JS runtime whose <c>import</c> yields a recording module. MULTIPLE independent import holds
    /// can be armed, so two import continuations can be in flight at once.
    /// </summary>
    private sealed class RecordingJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        private readonly Queue<ImportHold> _holds = new();

        /// <summary>The modules handed out by <c>import</c>, in order.</summary>
        public List<RecordingModule> Modules { get; } = [];

        /// <summary>The mirrored paste-allowed values.</summary>
        public List<bool> AllowedValues { get; } = [];

        /// <summary>Arms ONE more independent import hold.</summary>
        public ImportHold ArmImport()
        {
            var hold = new ImportHold();
            lock (_holds) _holds.Enqueue(hold);
            return hold;
        }

        /// <summary>How many times a JS call was made across every module.</summary>
        public int Count(string call) => Modules.Sum(m => m.Calls.Count(c => c == call));

        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (identifier == "import")
            {
                var module = new RecordingModule(this);
                lock (_holds) Modules.Add(module);

                ImportHold? hold;
                lock (_holds) hold = _holds.Count > 0 ? _holds.Dequeue() : null;

                return hold is null
                    ? ValueTask.FromResult((TValue)(object)module)
                    : new ValueTask<TValue>(CompleteImportAsync<TValue>(module, hold, ct));
            }

            return default!;
        }

        private static async Task<TValue> CompleteImportAsync<TValue>(
            RecordingModule module, ImportHold hold, CancellationToken ct)
        {
            hold.Entered.TrySetResult();
            await hold.Gate.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return (TValue)(object)module;
        }
    }

    /// <summary>One held import.</summary>
    private sealed class ImportHold
    {
        /// <summary>Completes when the import was entered.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The gate the import waits on.</summary>
        public Task Gate => _release.Task;

        /// <summary>Releases the held import.</summary>
        public void Release() => _release.TrySetResult();
    }

    /// <summary>A recording JS module reference.</summary>
    private sealed class RecordingModule(RecordingJsRuntime owner) : IJSObjectReference
    {
        /// <summary>The calls made on this module.</summary>
        public List<string> Calls { get; } = [];

        /// <summary>Whether the module was disposed.</summary>
        public bool Disposed { get; private set; }

        /// <summary>How many times the module was disposed.</summary>
        public int DisposeCount { get; private set; }

        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken ct, object?[]? args)
        {
            Calls.Add(identifier);
            if (identifier == "setAllowed") owner.AllowedValues.Add((bool)args![0]!);
            return default!;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The provider, the manager, the JS runtime and the state directory of one host.</summary>
    private sealed class Host(
        ServiceProvider provider,
        ComposerRuntimeManager manager,
        RecordingJsRuntime js,
        string stateDir) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public ComposerRuntimeManager Manager { get; } = manager;

        public RecordingJsRuntime Js { get; } = js;

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
                throw new AggregateException("Paste-concurrency host cleanup failed", failures);
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
