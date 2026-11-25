using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using ConfigurationPage = CopilotHive.Components.Pages.Configuration;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE RETAINED CONFIGURATION PAGE's config-repository path projection, tested through the REAL
/// <see cref="HtmlRenderer"/>, the REAL <see cref="ConfigFacade"/> (real resolver/policy/directory) and
/// the REAL scoped <see cref="CircuitUserPrincipalAccessor"/>. NO <see cref="ConfigRepoManager"/> is
/// registered for page injection: the manager lives INSIDE the facade, so the banner can only be
/// produced by the gated projection.
/// <para>
/// Every denial theory starts with a DISTINCT path visibly rendered on ONE retained component, drives a
/// REAL page event (or a reachable reload), observes the named operation's own outcome, and asserts
/// both the state field and the markup clear. Every row carries a viable SUCCESS CONTROL. Late-success
/// rows park the actual facade result behind a bounded gate on the real lifecycle/event path, join the
/// refresh afterwards and never sleep. The config/backup decorator records EVERY operation the rows
/// observe, so "no unrelated denial" is an assertion, not an assumption.
/// </para>
/// </summary>
public sealed class ConfigurationRepositoryPathRetainedTests : IDisposable
{
    private const string AdminId = "cfgpath-admin";
    private const string ViewerId = "cfgpath-viewer";
    private const string MemberId = "cfgpath-member";
    private const string OperatorId = "cfgpath-operator";
    private const string DisabledId = "cfgpath-disabled";
    private const string UnknownId = "cfgpath-never-activated";
    private const string ReplacementId = "cfgpath-replacement";

    private const string ConfigRepoUrl = "https://example.com/config.git";
    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private const string BannerLabel = "Config repo:";
    private const string BrowseButtonText = "Browse Provider Models";
    private const string CreateBackupButtonText = "Create Backup";
    private const string DiscoveredMarker = "retained-provider-model";
    private const string ListMarker = "retained-backup-list-marker.tar.gz";
    private const string RefreshedListMarker = "retained-backup-refreshed-marker.tar.gz";
    private const string CreatedMarker = "retained-backup-created-marker.tar.gz";

    /// <summary>The EXACT banner markup for one authorized path (the page's own conditional block).</summary>
    private static string Banner(string path) =>
        "<p style=\"color:var(--text-muted);margin-bottom:1.5rem\">\n" +
        "        Config repo: <code style=\"font-size:0.85rem\">" + path + "</code></p>";

    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(15);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly List<string> _tempDirs = [];

    // ── production fixtures ─────────────────────────────────────────────────

    private ConfigFacade CreateProductionFacade(string? repoRoot, HiveAuthMode mode) =>
        new(
            hiveConfig: new HiveConfigFile
            {
                Orchestrator = new OrchestratorConfig { Model = "m" },
                Models = new ModelsConfig { AvailableModels = [] },
            },
            configModel: null,
            discovery: null, // discovery is scripted by the recording decorator, never by production network calls
            NullLogger<ConfigFacade>.Instance,
            _auth.Resolver(mode),
            _auth.Policy,
            repoManager: null,
            configRepo: repoRoot is null ? null : new ConfigRepoManager(ConfigRepoUrl, repoRoot));

    private static string ExpectedPath(string repoRoot) =>
        new ConfigRepoManager(ConfigRepoUrl, repoRoot).LocalPath;

    /// <summary>A UNIQUE path per row: the configured clone directory is NEVER created.</summary>
    private string UniqueRepoRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cfgpath-retained-{Guid.NewGuid():N}");
        _tempDirs.Add(root);
        return root;
    }

    // ── gates ───────────────────────────────────────────────────────────────

    /// <summary>ONE parked read: signals its arrival and awaits an explicit release. No sleeps.</summary>
    private sealed class ReadGate
    {
        private readonly TaskCompletionSource<FacadeResult<string>> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<FacadeResult<string>> Entered => _entered.Task;
        internal void Release() => _release.TrySetResult();

        internal async Task ParkAsync(FacadeResult<string> result)
        {
            _entered.TrySetResult(result);
            await _release.Task;
        }
    }

    // ── the recording/gating decorator over the PRODUCTION config facade ───

    /// <summary>
    /// One recorded path-projection delegation: the ACTUAL principal argument the page forwarded (with
    /// its actual nullness, so a null identity is distinguishable from an anonymous one), the ACTUAL
    /// production result, and the protected path the caller was allowed to obtain (<c>null</c> unless the
    /// call really succeeded with a configured repo).
    /// </summary>
    internal sealed record PathCall(ClaimsPrincipal? ActingUser, bool Success, FacadeErrorKind Kind, string? Value)
    {
        /// <summary>Whether the page forwarded NO principal at all (the superseded/denied shape).</summary>
        internal bool ForwardedNullPrincipal => ActingUser is null;

        /// <summary>The GitHub identity the page forwarded, or <c>null</c> when it forwarded none.</summary>
        internal string? ActingIdentity => ActingUser?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    /// <summary>
    /// The registered <see cref="IConfigFacade"/>: the production facade decorated so the test can
    /// park a path projection AFTER the real production answer and script the discovery result. EVERY
    /// operation is recorded; every path projection additionally records the ACTUAL argument and result,
    /// so a row can prove which principal really crossed this boundary and what it obtained.
    /// </summary>
    private sealed class GateableConfigFacade(IConfigFacade production) : IConfigFacade
    {
        private readonly object _gateLock = new();
        private readonly Queue<ReadGate> _armed = new();
        private readonly List<ReadGate> _gates = [];
        private readonly List<PathCall> _pathCalls = [];

        internal int PathCalls { get; private set; }

        /// <summary>EVERY path projection recorded so far, in call order (actual argument + actual result).</summary>
        internal IReadOnlyList<PathCall> RecordedPathCalls
        {
            get { lock (_gateLock) return [.. _pathCalls]; }
        }

        internal int DiscoveryCalls { get; private set; }
        internal bool DenyDiscovery { get; set; }
        internal IReadOnlyList<DiscoveredModelDto> DiscoveryResult { get; set; } =
            [new DiscoveredModelDto(DiscoveredMarker, "Retained Provider Model", "provider", 4096, true)];

        internal ReadGate ArmPath()
        {
            var gate = new ReadGate();
            lock (_gateLock) { _gates.Add(gate); _armed.Enqueue(gate); }
            return gate;
        }

        internal void ReleaseAll()
        {
            ReadGate[] gates;
            lock (_gateLock) gates = [.. _gates];
            foreach (var gate in gates) gate.Release();
        }

        public async Task<FacadeResult<string>> GetConfigRepositoryPathAsync(ClaimsPrincipal? a, CancellationToken ct)
        {
            PathCalls++;
            // The PRODUCTION answer is computed FIRST: parked means "real authorized result in hand,
            // publication pending", never "authorization not yet decided".
            var result = await production.GetConfigRepositoryPathAsync(a, ct);
            lock (_gateLock) _pathCalls.Add(new PathCall(a, result.Success, result.Kind, result.Value));
            ReadGate? gate;
            lock (_gateLock) _armed.TryDequeue(out gate);
            if (gate is not null) await gate.ParkAsync(result);
            return result;
        }

        public Task<FacadeResult<IReadOnlyList<DiscoveredModelDto>>> DiscoverModelsAsync(
            ClaimsPrincipal? a, CancellationToken ct)
        {
            DiscoveryCalls++;
            return Task.FromResult(DenyDiscovery
                ? new FacadeResult<IReadOnlyList<DiscoveredModelDto>>(
                    false, null, ForbiddenMessage, FacadeErrorKind.Forbidden)
                : new FacadeResult<IReadOnlyList<DiscoveredModelDto>>(
                    true, DiscoveryResult, null, FacadeErrorKind.None));
        }

        public Task<FacadeResult<ModelsConfigDto>> GetModelsAsync(ClaimsPrincipal? a, CancellationToken ct) => production.GetModelsAsync(a, ct);
        public Task<FacadeResult<SavedResult>> SaveModelsAsync(ModelConfigUpdate u, ClaimsPrincipal? a, CancellationToken ct) => production.SaveModelsAsync(u, a, ct);
        public Task<FacadeResult<SavedResult>> AddAvailableModelAsync(AvailableModelRequest r, ClaimsPrincipal? a, CancellationToken ct) => production.AddAvailableModelAsync(r, a, ct);
        public Task<FacadeResult<SavedResult>> UpdateAvailableModelAsync(string n, AvailableModelRequest r, ClaimsPrincipal? a, CancellationToken ct) => production.UpdateAvailableModelAsync(n, r, a, ct);
        public Task<FacadeResult<RemovedResult>> RemoveAvailableModelAsync(string n, ClaimsPrincipal? a, CancellationToken ct) => production.RemoveAvailableModelAsync(n, a, ct);
        public Task<FacadeResult<SavedResult>> AddSubAgentModelAsync(SubAgentModelRequest r, ClaimsPrincipal? a, CancellationToken ct) => production.AddSubAgentModelAsync(r, a, ct);
        public Task<FacadeResult<SavedResult>> UpdateSubAgentModelAsync(string n, SubAgentModelRequest r, ClaimsPrincipal? a, CancellationToken ct) => production.UpdateSubAgentModelAsync(n, r, a, ct);
        public Task<FacadeResult<RemovedResult>> RemoveSubAgentModelAsync(string n, ClaimsPrincipal? a, CancellationToken ct) => production.RemoveSubAgentModelAsync(n, a, ct);
        public Task<FacadeResult<IReadOnlyList<RepositoryDto>>> GetRepositoriesAsync(ClaimsPrincipal? a, CancellationToken ct) => production.GetRepositoriesAsync(a, ct);
        public Task<FacadeResult<SavedResult>> AddRepositoryAsync(RepositoryRequest r, ClaimsPrincipal? a, CancellationToken ct) => production.AddRepositoryAsync(r, a, ct);
        public Task<FacadeResult<IReadOnlyList<string>>> GetBranchesAsync(string n, ClaimsPrincipal? a, CancellationToken ct) => production.GetBranchesAsync(n, a, ct);
        public Task<FacadeResult<SavedResult>> UpdateRepositoryAsync(string n, RepositoryRequest r, ClaimsPrincipal? a, CancellationToken ct) => production.UpdateRepositoryAsync(n, r, a, ct);
        public Task<FacadeResult<RemovedResult>> RemoveRepositoryAsync(string n, ClaimsPrincipal? a, CancellationToken ct) => production.RemoveRepositoryAsync(n, a, ct);
        public Task<FacadeResult<OrchestratorConfigDto>> GetOrchestratorAsync(ClaimsPrincipal? a, CancellationToken ct) => production.GetOrchestratorAsync(a, ct);
        public Task<FacadeResult<SavedResult>> SaveOrchestratorAsync(OrchestratorSettingsUpdate u, ClaimsPrincipal? a, CancellationToken ct) => production.SaveOrchestratorAsync(u, a, ct);
        public Task<FacadeResult<WorkersConfigDto>> GetWorkersAsync(ClaimsPrincipal? a, CancellationToken ct) => production.GetWorkersAsync(a, ct);
        public Task<FacadeResult<SavedResult>> SaveWorkersAsync(Dictionary<string, int> w, ClaimsPrincipal? a, CancellationToken ct) => production.SaveWorkersAsync(w, a, ct);
        public Task<FacadeResult<ComposerConfigDto>> GetComposerAsync(ClaimsPrincipal? a, CancellationToken ct) => production.GetComposerAsync(a, ct);
        public Task<FacadeResult<IReadOnlyList<ConfigFileContentDto>>> GetConfigFilesAsync(ClaimsPrincipal? a, CancellationToken ct) => production.GetConfigFilesAsync(a, ct);
        public Task<FacadeResult<SavedResult>> SaveComposerAsync(ComposerSettingsUpdate u, ClaimsPrincipal? a, CancellationToken ct) => production.SaveComposerAsync(u, a, ct);
    }

    /// <summary>
    /// A scripted <see cref="IBackupFacade"/> whose LIST and CREATE independently refuse and whose
    /// successes are OBSERVABLE (recorded calls, list contents, success markers).
    /// </summary>
    private sealed class ScriptedBackupFacade : IBackupFacade
    {
        private FacadeResult<IReadOnlyList<BackupInfoDto>>? _nextList;
        internal FacadeResult<IReadOnlyList<BackupInfoDto>>? LastList { get; private set; }

        internal void ArmNextList(bool deny) => _nextList = deny
            ? Forbidden<IReadOnlyList<BackupInfoDto>>()
            : ListWith(RefreshedListMarker);

        private static FacadeResult<IReadOnlyList<BackupInfoDto>> ListWith(string marker) =>
            new(true, [new BackupInfoDto(marker, 42, DateTime.UnixEpoch)], null, FacadeErrorKind.None);
        internal bool DenyCreate { get; set; }
        internal int ListCalls { get; private set; }
        internal int CreateCalls { get; private set; }
        /// <summary>The LAST successful create's exact DTO — observable even after the 3s success flag expires.</summary>
        internal BackupInfoDto? LastCreated { get; private set; }

        private static FacadeResult<T> Forbidden<T>() =>
            new(false, default, ForbiddenMessage, FacadeErrorKind.Forbidden);

        public Task<FacadeResult<IReadOnlyList<BackupInfoDto>>> GetBackupsAsync(
            ClaimsPrincipal? a, CancellationToken ct)
        {
            ListCalls++;
            var result = _nextList ?? ListWith(ListMarker);
            _nextList = null;
            LastList = result;
            return Task.FromResult(result);
        }

        public Task<FacadeResult<BackupInfoDto>> CreateBackupAsync(
            ClaimsPrincipal? a, CancellationToken ct)
        {
            CreateCalls++;
            if (DenyCreate)
                return Task.FromResult(Forbidden<BackupInfoDto>());
            LastCreated = new BackupInfoDto(CreatedMarker, 7, DateTime.UnixEpoch);
            return Task.FromResult(new FacadeResult<BackupInfoDto>(
                true, LastCreated, null, FacadeErrorKind.None));
        }

        public Task<FacadeResult<BackupDownload>> OpenBackupForDownloadAsync(
            string fileName, ClaimsPrincipal? a, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<BackupDownload>(false, null, "not used", FacadeErrorKind.NotFound));

        public Task<FacadeResult<BackupRestore>> RestoreBackupAsync(
            string fileName, ClaimsPrincipal? a, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<BackupRestore>(false, null, "not used", FacadeErrorKind.NotFound));
    }

    private sealed class Host(ServiceProvider provider, GateableConfigFacade configFacade,
        ScriptedBackupFacade backupFacade) : IAsyncDisposable
    {
        internal GateableConfigFacade ConfigFacade { get; } = configFacade;
        internal ScriptedBackupFacade BackupFacade { get; } = backupFacade;
        internal ServiceProvider Provider => provider;
        public async ValueTask DisposeAsync()
        {
            ConfigFacade.ReleaseAll();
            await provider.DisposeAsync();
        }
    }

    private Host NewHost(string? repoRoot, HiveAuthMode mode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new ConfirmingJsRuntime());
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(), mode, NullLogger<HivePrincipalResolver>.Instance));
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(), sp.GetRequiredService<HivePolicy>()));

        // Deliberately NO ConfigRepoManager registration for page injection.
        var gateable = new GateableConfigFacade(CreateProductionFacade(repoRoot, mode));
        var backup = new ScriptedBackupFacade();
        services.AddSingleton<IConfigFacade>(gateable);
        services.AddSingleton<IBackupFacade>(backup);

        return new Host(services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        }), gateable, backup);
    }

    // ── rendering / event infrastructure ───────────────────────────────────

    private sealed class Rendered : IAsyncDisposable
    {
        private readonly HtmlRenderer _renderer;
        private readonly IServiceScope _scope;
        private readonly Func<string> _rootHtml;
        private readonly Func<ConfigurationPage> _captureLatest;
        private readonly RetainedCascadeHost? _parent;
        private readonly Action _releasePending;
        private readonly List<Task> _tasks = [];
        private readonly List<Action> _releaseBeforeJoin = [];
        private Task? _rendererDisposal;

        private Task Track(Task task)
        {
            lock (_tasks) _tasks.Add(task);
            return task;
        }

        internal void ReleaseBeforeJoin(Action release) => _releaseBeforeJoin.Add(release);

        internal Rendered(HtmlRenderer renderer, IServiceScope scope, Func<string> rootHtml,
            Func<ConfigurationPage> captureLatest, RetainedCascadeHost? parent, Action releasePending)
        {
            _renderer = renderer; _scope = scope; _rootHtml = rootHtml;
            _captureLatest = captureLatest; _parent = parent; _releasePending = releasePending;
        }

        internal ConfigurationPage Component => _captureLatest();
        internal CircuitUserPrincipalAccessor CircuitAccessor =>
            _scope.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>();

        internal Task<string> HtmlAsync() => _renderer.Dispatcher.InvokeAsync(_rootHtml);
        internal T Field<T>(string name) => ReadField<T>(Component, name);
        internal Task ReplaceChildAsync() => RunParentRenderAsync(parent => parent.ReplaceChild());
        internal Task SetFieldAsync(string name, object? value) => _renderer.Dispatcher.InvokeAsync(() =>
            Component.GetType().GetField(name, NonPublicInstance)!.SetValue(Component, value));

        internal Task RequestRenderAsync() => _renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(ComponentBase).GetMethod("StateHasChanged",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(Component, null);
        });

        internal Task ChangeCascadeAsync(AuthenticationState state) =>
            StartCascadeAsync(Task.FromResult(state)).WaitAsync(JoinTimeout, Ct);

        /// <summary>
        /// INVOKES THE PRODUCTION <c>@onclick</c> EventCallback found in the page's CURRENT render tree,
        /// preserving the real <see cref="ComponentBase"/> IHandleEvent receiver. The returned task is
        /// OWNED and joined by this root.
        /// </summary>
        internal Task ClickAsync(string buttonText) => Track(_renderer.Dispatcher.InvokeAsync(() =>
        {
#pragma warning disable BL0006 // Intentional inspection of the production render tree in a regression test.
            var render = typeof(ConfigurationPage).GetMethod(
                "BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<RenderTreeBuilder>>(Component);
            using var builder = new RenderTreeBuilder();
            render(builder);
            var frames = builder.GetFrames();
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                var end = Math.Min(i + frame.ElementSubtreeLength, frames.Count);
                var text = false;
                EventCallback? click = null;
                for (var j = i + 1; j < end; j++)
                {
                    var child = frames.Array[j];
                    if (child.FrameType == RenderTreeFrameType.Attribute && child.AttributeName == "onclick")
                    {
                        click = child.AttributeValue switch
                        {
                            EventCallback callbackValue => callbackValue,
                            Func<Task> rawHandler => EventCallback.Factory.Create(Component, rawHandler),
                            _ => throw new InvalidOperationException(
                                $"Unexpected onclick attribute type {child.AttributeValue?.GetType().FullName ?? "null"}."),
                        };
                    }
                    if (child.FrameType == RenderTreeFrameType.Text &&
                        child.TextContent?.Contains(buttonText, StringComparison.Ordinal) == true)
                        text = true;
                }
                if (!text || click is null) continue;
                var callback = click.Value;
                Assert.Same(Component, typeof(EventCallback)
                    .GetField("Receiver", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                    .GetValue(callback));
                var handler = (Func<Task>)typeof(EventCallback)
                    .GetField("Delegate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                    .GetValue(callback)!;
                return EventCallback.Factory.Create(Component, (Func<Task>)(() => Track(handler()))).InvokeAsync();
            }
#pragma warning restore BL0006
            throw new InvalidOperationException($"No rendered button '@onclick' containing '{buttonText}'.");
        }));

        /// <summary>
        /// THE CREATE BACKUP BUTTON: its text lives in a conditional <c>@if</c> block, so the render-tree
        /// builder exposes no Text frame for it. Locate it structurally as the ONLY button with a raw
        /// <c>Func&lt;Task&gt;</c> onclick delegate whose parent container carries the backup button's
        /// style. Invoke that production delegate through the real IHandleEvent receiver.
        /// </summary>
        internal Task ClickCreateBackupAsync() => Track(_renderer.Dispatcher.InvokeAsync(() =>
        {
#pragma warning disable BL0006
            var render = typeof(ConfigurationPage).GetMethod(
                "BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<RenderTreeBuilder>>(Component);
            using var builder = new RenderTreeBuilder();
            render(builder);
            var frames = builder.GetFrames();
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                var end = Math.Min(i + frame.ElementSubtreeLength, frames.Count);
                Func<Task>? handler = null;
                for (var j = i + 1; j < end && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                {
                    if (frames.Array[j].AttributeName == "onclick" &&
                        frames.Array[j].AttributeValue is Func<Task> raw)
                        handler = raw;
                }
                if (handler is null) continue;
                // The create-backup button is uniquely preceded by the backup button-row container.
                var preceded = false;
                for (var j = Math.Max(0, i - 6); j < i; j++)
                    preceded |= frames.Array[j].FrameType == RenderTreeFrameType.Attribute &&
                                frames.Array[j].AttributeName == "style" &&
                                Equals(frames.Array[j].AttributeValue, "margin-bottom:1.5rem");
                if (!preceded) continue;
                return EventCallback.Factory.Create(Component, (Func<Task>)(() => Track(handler()))).InvokeAsync();
            }
#pragma warning restore BL0006
            throw new InvalidOperationException("No rendered Create Backup button onclick.");
        }));

        internal Task StartCascadeAsync(Task<AuthenticationState> state) =>
            RunParentRenderAsync(parent => parent.ChangeTask(state));

        /// <summary>
        /// Render the real parent and capture the framework's lifecycle tasks for THIS render.
        /// HtmlRenderer only tracks pending work for initial quiescence by default. Use its task
        /// collector during synchronous cascade delivery, then restore it before joining. Separate
        /// collectors let the newer lifecycle finish without joining the older held operation.
        /// </summary>
        private Task RunParentRenderAsync(Action<RetainedCascadeHost> render)
        {
            Assert.NotNull(_parent);
            var inner = typeof(HtmlRenderer).GetField("_passiveHtmlRenderer", NonPublicInstance)!
                .GetValue(_renderer)!;
            var pending = typeof(Renderer).GetField("_pendingTasks", NonPublicInstance)!;
            return Track(_renderer.Dispatcher.InvokeAsync(() =>
            {
                var previous = pending.GetValue(inner);
                var current = new List<Task>();
                pending.SetValue(inner, current);
                try { render(_parent!); }
                finally
                {
                    pending.SetValue(inner, previous);
                    // Own the framework work even if synchronous rendering threw after starting it.
                    foreach (var task in current) Track(task);
                }
                return Task.WhenAll(current);
            }));
        }

        internal Task DisposeRendererAsync() => _rendererDisposal ??= _renderer.DisposeAsync().AsTask();

        internal async Task RunAndDisposeAsync(Func<Task> scenario)
        {
            Exception? primary = null;
            try { await scenario(); }
            catch (Exception ex) { primary = ex; }
            finally { await CleanupAsync(primary); }
        }

        public ValueTask DisposeAsync() => CleanupAsync(primary: null);

        private async ValueTask CleanupAsync(Exception? primary)
        {
            var failures = new List<Exception>();
            void Attempt(Action step)
            {
                try { step(); } catch (Exception ex) { failures.Add(ex); }
            }
            async Task JoinAsync(Task task)
            {
                try { await task.WaitAsync(JoinTimeout, CancellationToken.None); }
                catch (Exception ex) when (ReferenceEquals(ex, primary)) { }
                catch (Exception ex) { if (!failures.Contains(ex)) failures.Add(ex); }
            }

            // Release every hold before any joins. Take one task at a time: an event's enclosing
            // dispatcher task may register the actual handler/lifecycle work after it was scheduled.
            Attempt(_releasePending);
            foreach (var release in _releaseBeforeJoin) Attempt(release);
            for (var i = 0; ; i++)
            {
                Task task;
                lock (_tasks)
                {
                    if (i >= _tasks.Count) break;
                    task = _tasks[i];
                }
                await JoinAsync(task);
            }
            try { await JoinAsync(DisposeRendererAsync()); }
            catch (Exception ex) { failures.Add(ex); }
            Attempt(_scope.Dispose);

            if (failures.Count != 0)
            {
                if (primary is not null) failures.Insert(0, primary);
                throw new AggregateException("Retained component scenario or cleanup failed.", failures);
            }
            if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }

    private static T ReadField<T>(ConfigurationPage component, string name) =>
        (T)typeof(ConfigurationPage).GetField(name, NonPublicInstance)!.GetValue(component)!;

    private static async Task<FacadeResult<string>> AwaitHeldReadAsync(ReadGate gate, Task lifecycle)
    {
        var first = await Task.WhenAny(gate.Entered, lifecycle).WaitAsync(JoinTimeout, Ct);
        Assert.Same(gate.Entered, first);
        Assert.False(lifecycle.IsCompleted, "The enclosing cascade lifecycle must still be held.");
        return await gate.Entered;
    }

    /// <summary>A parent that re-cascades a NEW authentication state to the SAME retained child.</summary>
    public sealed class RetainedCascadeHost : ComponentBase
    {
        [Parameter] public AuthenticationState Initial { get; set; } = null!;
        [Parameter] public RenderFragment Content { get; set; } = null!;
        [Parameter] public Action<RetainedCascadeHost> Capture { get; set; } = null!;
        private Task<AuthenticationState> _state = null!;
        protected override void OnInitialized() { _state = Task.FromResult(Initial); Capture(this); }
        private int _childKey;
        internal void ReplaceChild() { _childKey++; StateHasChanged(); }
        internal void ChangeTask(Task<AuthenticationState> state) { _state = state; StateHasChanged(); }
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.SetKey(_childKey);
            b.AddAttribute(1, "Value", _state);
            b.AddAttribute(2, "ChildContent", Content);
            b.CloseComponent();
        }
    }

    /// <summary>Pass-through host for OPEN MODE (no cascade in the tree at all).</summary>
    public sealed class ContentHost : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder b) => b.AddContent(0, ChildContent);
    }

    private static ClaimsPrincipal Principal(string id) => FacadeAuthorizationHarness.Principal(id);

    private async Task<Rendered> RenderAsync(Host host, string? gitHubId, bool anonymousCascade = false)
    {
        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        ConfigurationPage? page = null;
        RetainedCascadeHost? parent = null;
        Func<string>? html = null;

        RenderFragment content = b =>
        {
            b.OpenComponent<ConfigurationPage>(0);
            b.AddComponentReferenceCapture(1, c => page = (ConfigurationPage)c);
            b.CloseComponent();
        };

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root;
            if (gitHubId is null && !anonymousCascade)
                root = await renderer.RenderComponentAsync<ContentHost>(
                    ParameterView.FromDictionary(new Dictionary<string, object?> { ["ChildContent"] = content }));
            else
            {
                var principal = anonymousCascade ? FacadeAuthorizationHarness.Anonymous() : Principal(gitHubId!);
                root = await renderer.RenderComponentAsync<RetainedCascadeHost>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["Initial"] = new AuthenticationState(principal),
                        ["Content"] = content,
                        ["Capture"] = (Action<RetainedCascadeHost>)(h => parent = h),
                    }));
            }
            html = root.ToHtmlString;
        });

        Assert.NotNull(html);
        Assert.NotNull(page);
        return new Rendered(renderer, scope, html!, () => page!, parent, host.ConfigFacade.ReleaseAll);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RetainedPath_InitialRendering
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE INITIAL-RENDER MATRIX on the REAL page with NO manager registered for page injection: a
    /// configured Admin and an explicit open-mode caller render the EXACT banner markup; authorized
    /// absence and a denial render NO banner at all.
    /// </summary>
    [Theory]
    [InlineData("admin-configured")]
    [InlineData("open-configured")]
    [InlineData("authorized-absent")]
    [InlineData("denied")]
    public async Task RetainedPath_InitialRendering(string caller)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var repoRoot = UniqueRepoRoot();
        var expectsBanner = caller is "admin-configured" or "open-configured";
        var mode = caller == "open-configured" ? HiveAuthMode.Open : HiveAuthMode.AuthenticationEnabled;

        await using var host = NewHost(expectsBanner ? repoRoot : null, mode);
        await using var page = caller switch
        {
            "admin-configured" => await RenderAsync(host, AdminId),
            "open-configured" => await RenderAsync(host, gitHubId: null),
            "authorized-absent" => await RenderAsync(host, AdminId),
            "denied" => await RenderAsync(host, ViewerId),
            _ => throw new InvalidOperationException(caller),
        };

        var html = await page.HtmlAsync();
        // PREMISE: the real page rendered, despite NO ConfigRepoManager being available to inject.
        if (expectsBanner || caller == "authorized-absent")
            Assert.Contains("config-tabs", html, StringComparison.Ordinal);
        if (expectsBanner)
        {
            Assert.Contains(Banner(ExpectedPath(repoRoot)), html, StringComparison.Ordinal);
            Assert.Equal(ExpectedPath(repoRoot), page.Field<string?>("_configRepoPath"));
        }
        else
        {
            Assert.DoesNotContain(BannerLabel, html, StringComparison.Ordinal);
            Assert.DoesNotContain(ExpectedPath(repoRoot), html, StringComparison.Ordinal);
            Assert.Null(page.Field<string?>("_configRepoPath"));
        }
        Assert.Equal(1, host.ConfigFacade.PathCalls);
    }

    /// <summary>
    /// EVERY REFUSED CALLER on a CONFIGURED installation renders NO banner: Viewer, Member, Operator,
    /// disabled Admin, unknown account and anonymous cascade. The configured fixture makes absence
    /// attributable to the gate rather than to an unconfigured installation.
    /// </summary>
    [Theory]
    [InlineData(ViewerId, UserRole.Viewer, false)]
    [InlineData(MemberId, UserRole.Member, false)]
    [InlineData(OperatorId, UserRole.Operator, false)]
    [InlineData(DisabledId, UserRole.Admin, false)]
    [InlineData(UnknownId, UserRole.Viewer, false)]
    [InlineData(null, UserRole.Viewer, true)]
    public async Task RetainedPath_InitialRendering_RefusedCallers(
        string? accountId, UserRole seedRole, bool anonymousCascade)
    {
        if (accountId == DisabledId)
            await _auth.SeedAsync(DisabledId, seedRole, enabled: false);
        else if (accountId is not null && accountId != UnknownId)
            await _auth.SeedAsync(accountId, seedRole);

        var repoRoot = UniqueRepoRoot();
        await using var host = NewHost(repoRoot, HiveAuthMode.AuthenticationEnabled);
        await using var page = await RenderAsync(host, accountId,
            anonymousCascade: anonymousCascade || accountId is null);

        var html = await page.HtmlAsync();
        Assert.DoesNotContain(BannerLabel, html, StringComparison.Ordinal);
        Assert.DoesNotContain(ExpectedPath(repoRoot), html, StringComparison.Ordinal);
        Assert.Null(page.Field<string?>("_configRepoPath"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RetainedPath_ConfigForbidden / BackupCreateForbidden / BackupListAfterCreate
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A REAL <c>Browse Provider Models</c> EventCallback issues the named
    /// <see cref="IConfigFacade.DiscoverModelsAsync"/>. The denial is scripted ONLY on discovery (the
    /// backup facade is left untouched and its call count stays unchanged), so no unrelated facade
    /// denial or cascade update can explain the clear. The success control observes the discovery
    /// result in state and the modal, and keeps the unique path visible in state AND markup.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedPath_ConfigForbidden(bool deny)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var repoRoot = UniqueRepoRoot();
        var path = ExpectedPath(repoRoot);
        await using var host = NewHost(repoRoot, HiveAuthMode.AuthenticationEnabled);
        await using var page = await RenderAsync(host, AdminId);

        Assert.Contains(Banner(path), await page.HtmlAsync(), StringComparison.Ordinal);
        var instance = page.Component;
        var pathCallsBefore = host.ConfigFacade.PathCalls;
        var backupCallsBefore = host.BackupFacade.ListCalls + host.BackupFacade.CreateCalls;
        host.ConfigFacade.DenyDiscovery = deny;
        await page.SetFieldAsync("_activeTab", "Available Models");
        await page.RequestRenderAsync();

        await page.ClickAsync(BrowseButtonText); // THE REAL UI EVENT
        await page.RequestRenderAsync();

        Assert.Same(instance, page.Component);
        Assert.Equal(1, host.ConfigFacade.DiscoveryCalls); // the NAMED config call really ran
        Assert.Equal(backupCallsBefore, host.BackupFacade.ListCalls + host.BackupFacade.CreateCalls);
        var html = await page.HtmlAsync();
        if (deny)
        {
            Assert.Contains("Discovery failed:", html, StringComparison.Ordinal);
            Assert.DoesNotContain(DiscoveredMarker, html, StringComparison.Ordinal);
            Assert.False(page.Field<bool>("_showDiscoveryModal"));
            Assert.DoesNotContain(BannerLabel, html, StringComparison.Ordinal);
            Assert.DoesNotContain(path, html, StringComparison.Ordinal);
            Assert.Null(page.Field<string?>("_configRepoPath"));
        }
        else
        {
            // VIABLE SUCCESS: the discovery result is observable in state and in the opened modal.
            Assert.Contains(page.Field<IReadOnlyList<DiscoveredModelDto>>("_discoveredModels"),
                m => m.Id == DiscoveredMarker);
            Assert.True(page.Field<bool>("_showDiscoveryModal"));
            Assert.Contains(DiscoveredMarker, html, StringComparison.Ordinal);
            Assert.Contains(Banner(path), html, StringComparison.Ordinal);
            Assert.Equal(path, page.Field<string?>("_configRepoPath"));
        }
        // No cascade/path re-read explains the clear: the shared denial handler alone did it.
        Assert.Equal(pathCallsBefore, host.ConfigFacade.PathCalls);
        Assert.Equal(CircuitIdentityState.Initialized, page.CircuitAccessor.IdentityState);
    }

    /// <summary>
    /// A REAL <c>Create Backup</c> EventCallback issues the named
    /// <see cref="IBackupFacade.CreateBackupAsync"/>. Only CREATE refuses; the config facade is not
    /// denied and no cascade changes. The success control observes the create call and the ensuing
    /// refreshed list marker in state/markup and keeps the unique path visible.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedPath_BackupCreateForbidden(bool deny)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var repoRoot = UniqueRepoRoot();
        var path = ExpectedPath(repoRoot);
        await using var host = NewHost(repoRoot, HiveAuthMode.AuthenticationEnabled);
        await using var page = await RenderAsync(host, AdminId);

        Assert.Contains(Banner(path), await page.HtmlAsync(), StringComparison.Ordinal);
        var instance = page.Component;
        await page.SetFieldAsync("_activeTab", "Backup");
        await page.RequestRenderAsync();
        host.BackupFacade.DenyCreate = deny;
        var createBefore = host.BackupFacade.CreateCalls;
        var listBefore = host.BackupFacade.ListCalls;

        await page.ClickCreateBackupAsync(); // THE REAL UI EVENT
        await page.RequestRenderAsync();

        Assert.Same(instance, page.Component);
        Assert.Equal(createBefore + 1, host.BackupFacade.CreateCalls);
        var html = await page.HtmlAsync();
        if (deny)
        {
            Assert.Equal(listBefore, host.BackupFacade.ListCalls); // no list/cascade route ran
            Assert.Contains("Backup failed:", html, StringComparison.Ordinal);
            Assert.DoesNotContain(BannerLabel, html, StringComparison.Ordinal);
            Assert.DoesNotContain(path, html, StringComparison.Ordinal);
            Assert.Null(page.Field<string?>("_configRepoPath"));
        }
        else
        {
            // VIABLE SUCCESS: the create succeeded and its ensuing list refresh actually happened.
            Assert.True(host.BackupFacade.ListCalls > listBefore);
            Assert.Equal(CreatedMarker, host.BackupFacade.LastCreated?.FileName); // create really succeeded
            Assert.Contains(ListMarker, html, StringComparison.Ordinal); // ensuing list refresh
            Assert.Contains(Banner(path), html, StringComparison.Ordinal);
            Assert.Equal(path, page.Field<string?>("_configRepoPath"));
        }
        Assert.Equal(CircuitIdentityState.Initialized, page.CircuitAccessor.IdentityState);
    }

    [Theory]
    [InlineData(false)] // Allowed
    [InlineData(true)] // Forbidden
    public async Task RetainedPath_BackupListAfterCreate(bool deny)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var repoRoot = UniqueRepoRoot();
        var path = ExpectedPath(repoRoot);
        await using var host = NewHost(repoRoot, HiveAuthMode.AuthenticationEnabled);
        var page = await RenderAsync(host, AdminId);

        await page.RunAndDisposeAsync(async () =>
        {
            var instance = page.Component;
            await page.SetFieldAsync("_activeTab", "Backup");
            await page.RequestRenderAsync();
            var before = await page.HtmlAsync();
            Assert.Contains(Banner(path), before, StringComparison.Ordinal);
            Assert.Contains(ListMarker, before, StringComparison.Ordinal);
            Assert.DoesNotContain(RefreshedListMarker, before, StringComparison.Ordinal);
            Assert.Equal(ListMarker, Assert.Single(page.Field<IReadOnlyList<BackupInfoDto>>("_backups")).FileName);

            var backup = host.BackupFacade;
            var listBefore = backup.ListCalls;
            var pathBefore = host.ConfigFacade.PathCalls;
            var discoveryBefore = host.ConfigFacade.DiscoveryCalls;
            var cascadeBefore = page.Field<Task<AuthenticationState>>("_observedAuthenticationState");
            Assert.Equal(1, listBefore); // real initial list, not the action's list
            Assert.Equal(0, backup.CreateCalls);
            backup.ArmNextList(deny);
            await page.ClickCreateBackupAsync().WaitAsync(JoinTimeout, Ct);

            Assert.Equal(1, backup.CreateCalls);
            Assert.Equal(CreatedMarker, backup.LastCreated?.FileName);
            Assert.Equal(listBefore + 1, backup.ListCalls); // only the real click can issue this reload
            Assert.Equal(deny ? FacadeErrorKind.Forbidden : FacadeErrorKind.None, backup.LastList?.Kind);
            Assert.Same(instance, page.Component);
            Assert.Equal(pathBefore, host.ConfigFacade.PathCalls);
            Assert.Equal(discoveryBefore, host.ConfigFacade.DiscoveryCalls);
            Assert.Same(cascadeBefore, page.Field<Task<AuthenticationState>>("_observedAuthenticationState"));
            Assert.Equal(CircuitIdentityState.Initialized, page.CircuitAccessor.IdentityState);

            var html = await page.HtmlAsync();
            Assert.DoesNotContain(ListMarker, html, StringComparison.Ordinal);
            Assert.DoesNotContain("Backup failed:", html, StringComparison.Ordinal);
            if (deny)
            {
                Assert.Contains($"Failed to load backups: {ForbiddenMessage}", html, StringComparison.Ordinal);
                Assert.DoesNotContain(RefreshedListMarker, html, StringComparison.Ordinal);
                Assert.Empty(page.Field<IReadOnlyList<BackupInfoDto>>("_backups"));
                Assert.DoesNotContain(BannerLabel, html, StringComparison.Ordinal);
                Assert.DoesNotContain(path, html, StringComparison.Ordinal);
                Assert.Null(page.Field<string?>("_configRepoPath"));
            }
            else
            {
                Assert.Contains(RefreshedListMarker, html, StringComparison.Ordinal);
                Assert.Equal(RefreshedListMarker, Assert.Single(page.Field<IReadOnlyList<BackupInfoDto>>("_backups")).FileName);
                Assert.Contains(Banner(path), html, StringComparison.Ordinal);
                Assert.Equal(path, page.Field<string?>("_configRepoPath"));
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RetainedPath_CascadeChanged
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ACTUAL PARENT CASCADE MATRIX on ONE retained root: sign-out and replacement with another
    /// ENABLED Admin each clear the unique path and PERMANENTLY INVALIDATE the circuit; restoring the
    /// original claims does not restore access. A NEW same-identity state issues a FRESH projection call
    /// and still renders its successful result.
    /// </summary>
    [Theory]
    [InlineData("sign-out")]
    [InlineData("replacement")]
    [InlineData("same-identity")]
    public async Task RetainedPath_CascadeChanged(string transition)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(ReplacementId, UserRole.Admin); // enabled Admin replacement
        var repoRoot = UniqueRepoRoot();
        var path = ExpectedPath(repoRoot);
        await using var host = NewHost(repoRoot, HiveAuthMode.AuthenticationEnabled);
        await using var page = await RenderAsync(host, AdminId);
        var instance = page.Component;
        var accessor = page.CircuitAccessor;

        Assert.Contains(Banner(path), await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);

        var callsBefore = host.ConfigFacade.PathCalls;
        AuthenticationState updated = transition switch
        {
            "sign-out" => new AuthenticationState(FacadeAuthorizationHarness.Anonymous()),
            "replacement" => new AuthenticationState(Principal(ReplacementId)),
            "same-identity" => new AuthenticationState(Principal(AdminId)),
            _ => throw new InvalidOperationException(transition),
        };
        await page.ChangeCascadeAsync(updated); // REAL parent render → retained child OnParametersSetAsync
        await page.RequestRenderAsync();

        Assert.Same(instance, page.Component);
        Assert.Equal(callsBefore + 1, host.ConfigFacade.PathCalls); // a FRESH projection really ran
        var html = await page.HtmlAsync();
        if (transition == "same-identity")
        {
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.Contains(Banner(path), html, StringComparison.Ordinal);
            Assert.Equal(path, page.Field<string?>("_configRepoPath"));
        }
        else
        {
            Assert.True(accessor.IsInvalidated,
                $"{transition} must permanently invalidate the circuit, not adopt the new account");
            Assert.Equal(
                transition == "sign-out" ? CircuitIdentityState.SignedOut : CircuitIdentityState.IdentityReplaced,
                accessor.IdentityState);
            Assert.DoesNotContain(BannerLabel, html, StringComparison.Ordinal);
            Assert.DoesNotContain(path, html, StringComparison.Ordinal);
            Assert.Null(page.Field<string?>("_configRepoPath"));

            // RESTORING the original claims must NOT restore access in this circuit.
            await page.ChangeCascadeAsync(new AuthenticationState(Principal(AdminId)));
            await page.RequestRenderAsync();
            Assert.Same(instance, page.Component);
            Assert.True(accessor.IsInvalidated);
            Assert.DoesNotContain(BannerLabel, await page.HtmlAsync(), StringComparison.Ordinal);
            Assert.Null(page.Field<string?>("_configRepoPath"));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RetainedPath_LateSuccess
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public Task RetainedPath_LateSuccess_HeldResultWithoutSupersession_Publishes() =>
        HeldResultAcrossRendererLifetimeAsync(dispose: false);

    [Fact]
    public Task RetainedPath_HeldSuccessAfterRendererDisposal_DoesNotWriteToDisposedInstance() =>
        HeldResultAcrossRendererLifetimeAsync(dispose: true);

    private async Task HeldResultAcrossRendererLifetimeAsync(bool dispose)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var repoRoot = UniqueRepoRoot();
        var path = ExpectedPath(repoRoot);
        await using var host = NewHost(repoRoot, HiveAuthMode.AuthenticationEnabled);
        var page = await RenderAsync(host, AdminId);

        await page.RunAndDisposeAsync(async () =>
        {
            var instance = page.Component;
            var accessor = page.CircuitAccessor;
            Assert.Contains(Banner(path), await page.HtmlAsync(), StringComparison.Ordinal);
            // Make publication distinguishable from the pre-release state on the original instance.
            await page.SetFieldAsync("_configRepoPath", null);
            await page.RequestRenderAsync();
            Assert.DoesNotContain(BannerLabel, await page.HtmlAsync(), StringComparison.Ordinal);

            var gate = host.ConfigFacade.ArmPath();
            var lifecycle = page.StartCascadeAsync(Task.FromResult(new AuthenticationState(Principal(AdminId))));
            var result = await AwaitHeldReadAsync(gate, lifecycle);
            Assert.True(result.Success);
            Assert.Equal(path, result.Value);
            Assert.Same(instance, page.Component);
            Assert.False(ReadField<bool>(instance, "_disposed"));
            Assert.Null(ReadField<string?>(instance, "_configRepoPath"));
            Assert.DoesNotContain(BannerLabel, await page.HtmlAsync(), StringComparison.Ordinal);

            if (dispose)
            {
                await page.DisposeRendererAsync().WaitAsync(JoinTimeout, Ct);
                Assert.True(ReadField<bool>(instance, "_disposed"));
                Assert.False(lifecycle.IsCompleted, "Renderer disposal must have finished while the refresh is held.");
                Assert.Null(ReadField<string?>(instance, "_configRepoPath"));
            }
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            gate.Release();
            await lifecycle.WaitAsync(JoinTimeout, Ct);

            if (dispose)
            {
                // No markup assertion on a dead renderer: inspect the disposed object's write target.
                Assert.Null(ReadField<string?>(instance, "_configRepoPath"));
            }
            else
            {
                Assert.Same(instance, page.Component);
                Assert.Equal(path, page.Field<string?>("_configRepoPath"));
                Assert.Contains(Banner(path), await page.HtmlAsync(), StringComparison.Ordinal);
            }
        });
    }

    /// <summary>An older authorized result must not repaint over any of these newer outcomes.</summary>
    [Theory]
    [InlineData("newer-config-forbidden")]
    [InlineData("newer-backup-forbidden")]
    [InlineData("sign-out")]
    [InlineData("replacement")]
    public async Task RetainedPath_LateSuccess(string supersession)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(ReplacementId, UserRole.Admin);
        var repoRoot = UniqueRepoRoot();
        var path = ExpectedPath(repoRoot);
        await using var host = NewHost(repoRoot, HiveAuthMode.AuthenticationEnabled);
        var page = await RenderAsync(host, AdminId);

        await page.RunAndDisposeAsync(async () =>
        {
            Assert.Contains(Banner(path), await page.HtmlAsync(), StringComparison.Ordinal);
            var instance = page.Component;
            await page.SetFieldAsync("_activeTab", "Backup");
            await page.RequestRenderAsync();

            var olderGate = host.ConfigFacade.ArmPath();
            var older = page.StartCascadeAsync(Task.FromResult(new AuthenticationState(Principal(AdminId))));
            var authorized = await AwaitHeldReadAsync(olderGate, older);
            Assert.True(authorized.Success);
            Assert.Equal(path, authorized.Value);
            Assert.Contains(Banner(path), await page.HtmlAsync(), StringComparison.Ordinal);

            if (supersession is "sign-out" or "replacement")
            {
                var state = supersession == "sign-out"
                    ? new AuthenticationState(FacadeAuthorizationHarness.Anonymous())
                    : new AuthenticationState(Principal(ReplacementId));
                var newerGate = host.ConfigFacade.ArmPath();
                var newer = page.StartCascadeAsync(Task.FromResult(state));
                Assert.Equal(FacadeErrorKind.Forbidden, (await AwaitHeldReadAsync(newerGate, newer)).Kind);
                newerGate.Release();
                await newer.WaitAsync(JoinTimeout, Ct);

                Assert.Equal(supersession == "sign-out" ? CircuitIdentityState.SignedOut : CircuitIdentityState.IdentityReplaced,
                    page.CircuitAccessor.IdentityState);
            }
            else if (supersession == "newer-config-forbidden")
            {
                host.ConfigFacade.DenyDiscovery = true;
                await page.SetFieldAsync("_activeTab", "Available Models");
                await page.RequestRenderAsync();
                await page.ClickAsync(BrowseButtonText).WaitAsync(JoinTimeout, Ct);
                Assert.Equal(1, host.ConfigFacade.DiscoveryCalls);
                Assert.Contains("Discovery failed:", await page.HtmlAsync(), StringComparison.Ordinal);
                Assert.Equal(CircuitIdentityState.Initialized, page.CircuitAccessor.IdentityState);
            }
            else
            {
                host.BackupFacade.DenyCreate = true;
                await page.ClickCreateBackupAsync().WaitAsync(JoinTimeout, Ct);
                Assert.Equal(1, host.BackupFacade.CreateCalls);
                Assert.Contains("Backup failed:", await page.HtmlAsync(), StringComparison.Ordinal);
                Assert.Equal(CircuitIdentityState.Initialized, page.CircuitAccessor.IdentityState);
            }

            Assert.Same(instance, page.Component);
            Assert.False(older.IsCompleted);
            Assert.DoesNotContain(BannerLabel, await page.HtmlAsync(), StringComparison.Ordinal);
            Assert.Null(page.Field<string?>("_configRepoPath"));

            olderGate.Release();
            await older.WaitAsync(JoinTimeout, Ct);

            Assert.Same(instance, page.Component);
            Assert.DoesNotContain(BannerLabel, await page.HtmlAsync(), StringComparison.Ordinal);
            Assert.Null(page.Field<string?>("_configRepoPath"));
        });
    }

    /// <summary>
    /// REVERSE COMPLETION (updated expectation): the OLDER cascade completes AFTER a newer same-account
    /// cascade already authorized and published the path. The older acquisition is SUPERSEDED by the
    /// newer registration, so it is DENIED (it returns no identity and never forwards stale claims), the
    /// circuit stays <see cref="CircuitIdentityState.Initialized"/> with the account it established, and
    /// the NEWER banner remains untouched in state and markup.
    /// <para>
    /// The retired expectation was that a parked older sign-out/replacement cascade would invalidate the
    /// circuit and clear the banner. That is no longer the contract: an older task is superseded the
    /// moment a newer one is registered, so it cannot change the circuit at all — whether it completes
    /// while the newer one is still pending or after the newer one has won (see
    /// <see cref="ComponentActingUser.AcquireAsync"/>). A GENUINE (current) sign-out/replacement is still
    /// permanently invalidating; that is pinned by <see cref="RetainedPath_CascadeChanged"/>.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("sign-out")]
    [InlineData("replacement")]
    public Task RetainedPath_ReverseCompletion_SupersededOlderCascadeAfterNewerPublication_IsDeniedAndKeepsTheNewerBanner(
        string olderOutcome) => ReverseCascadeCompletionAsync(olderOutcome);

    /// <summary>
    /// THE SAME-IDENTITY CONTROL for the reversed completion above: an older cascade carrying the SAME
    /// identity is likewise superseded (it yields no identity and forwards no claims), the circuit stays
    /// initialized, and the newer banner remains — the same outcome as the sign-out/replacement rows, so
    /// those rows discriminate on the SUPERSESSION rather than on which identity the older task carried.
    /// </summary>
    [Fact]
    public Task RetainedPath_ReverseCompletion_OlderSameIdentityCascadeAfterNewerPublication_KeepsTheBanner() =>
        ReverseCascadeCompletionAsync("same-identity");

    private async Task ReverseCascadeCompletionAsync(string terminal)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(ReplacementId, UserRole.Admin);
        var repoRoot = UniqueRepoRoot();
        var path = ExpectedPath(repoRoot);
        await using var host = NewHost(repoRoot, HiveAuthMode.AuthenticationEnabled);
        var page = await RenderAsync(host, AdminId);
        var originalState = new AuthenticationState(Principal(AdminId));

        await page.RunAndDisposeAsync(async () =>
        {
            var instance = page.Component;
            var accessor = page.CircuitAccessor;
            Assert.Contains(Banner(path), await page.HtmlAsync(), StringComparison.Ordinal);
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);

            // THE ORIGINAL IDENTITY REFERENCE, captured ONCE. A recorded-identity fallback (returning the
            // established principal for a superseded completion) would still leave a banner and still
            // leave a plausible IdentityState — so the REFERENCE is what this row pins: whatever the
            // circuit established before the overlap must be the very same object afterwards.
            var originalIdentity = accessor.EstablishedIdentity;
            Assert.NotNull(originalIdentity);
            Assert.Equal(AdminId, originalIdentity!.FindFirst(ClaimTypes.NameIdentifier)?.Value);

            // A newer publication must change what is visible, not inherit the initial banner.
            await page.SetFieldAsync("_configRepoPath", null);
            await page.RequestRenderAsync();
            Assert.DoesNotContain(BannerLabel, await page.HtmlAsync(), StringComparison.Ordinal);

            var generation = page.Field<int>("_pathRefreshGeneration");
            var callsBefore = host.ConfigFacade.PathCalls;
            var olderAuth = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
            page.ReleaseBeforeJoin(() => olderAuth.TrySetResult(originalState));
            var olderRefresh = page.StartCascadeAsync(olderAuth.Task);
            // The dispatcher processed delivery, the page claimed ownership, but it has not yet
            // called the facade: the enclosing lifecycle really is blocked on the auth task.
            Assert.DoesNotContain(BannerLabel, await page.HtmlAsync(), StringComparison.Ordinal);
            Assert.Equal(generation + 1, page.Field<int>("_pathRefreshGeneration"));
            Assert.Equal(callsBefore, host.ConfigFacade.PathCalls);
            Assert.False(olderAuth.Task.IsCompleted);
            Assert.False(olderRefresh.IsCompleted);

            await page.ChangeCascadeAsync(originalState);
            Assert.Same(instance, page.Component);
            Assert.Equal(callsBefore + 1, host.ConfigFacade.PathCalls);
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.Same(originalIdentity, accessor.EstablishedIdentity);
            Assert.Equal(path, page.Field<string?>("_configRepoPath"));
            Assert.Contains(Banner(path), await page.HtmlAsync(), StringComparison.Ordinal);
            Assert.False(olderAuth.Task.IsCompleted);
            Assert.False(olderRefresh.IsCompleted, "The older cascade must still be held after the newer render.");

            // THE NEWER CALL: the REAL published path call forwarded the ESTABLISHED admin identity and
            // obtained the configured path — the authorized control this row depends on.
            var newerCall = Assert.Single(host.ConfigFacade.RecordedPathCalls.Skip(callsBefore).Take(1));
            Assert.False(newerCall.ForwardedNullPrincipal, "the newer (current) path call must forward a principal");
            Assert.Equal(AdminId, newerCall.ActingIdentity);
            Assert.Same(originalState.User, newerCall.ActingUser);
            Assert.True(newerCall.Success);
            Assert.Equal(FacadeErrorKind.None, newerCall.Kind);
            Assert.Equal(path, newerCall.Value);

            var olderState = terminal switch
            {
                "sign-out" => new AuthenticationState(FacadeAuthorizationHarness.Anonymous()),
                "replacement" => new AuthenticationState(Principal(ReplacementId)),
                "same-identity" => originalState,
                _ => throw new InvalidOperationException(terminal),
            };
            olderAuth.SetResult(olderState);
            await olderRefresh.WaitAsync(JoinTimeout, Ct);

            Assert.Same(instance, page.Component);

            // Pin the preserved publication independently, before checking the stale facade result.
            var html = await page.HtmlAsync();
            Assert.Equal(path, page.Field<string?>("_configRepoPath"));
            Assert.Contains(Banner(path), html, StringComparison.Ordinal);

            // THE OLDER (SUPERSEDED) PATH CALL, observed at the REAL facade boundary. A counted call plus
            // an unchanged banner is NOT proof of denial: the page's generation guard discards a stale
            // SUCCESS exactly as it discards a stale denial, so both "denied" and "silently served the
            // recorded identity" look identical from the banner alone. The recorded argument and result
            // are what discriminate them.
            Assert.Equal(callsBefore + 2, host.ConfigFacade.PathCalls);
            var supersededCall = Assert.Single(host.ConfigFacade.RecordedPathCalls.Skip(callsBefore + 1).Take(1));
            Assert.True(supersededCall.ForwardedNullPrincipal,
                "a superseded completion must forward NO principal — never the recorded/established identity");
            Assert.Null(supersededCall.ActingIdentity);
            Assert.False(supersededCall.Success);
            Assert.Equal(FacadeErrorKind.Forbidden, supersededCall.Kind);
            Assert.Null(supersededCall.Value);

            // THE CIRCUIT IS UNCHANGED: same state, and the SAME established-identity REFERENCE.
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.False(accessor.IsInvalidated);
            Assert.Same(originalIdentity, accessor.EstablishedIdentity);
            Assert.Equal(AdminId, accessor.EstablishedIdentity!
                .FindFirst(ClaimTypes.NameIdentifier)?.Value);
        });
    }

    [Fact]
    public async Task RetainedPath_LateSuccess_InvalidationThroughSameAccessor_BlocksPublication()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(ReplacementId, UserRole.Admin);
        var repoRoot = UniqueRepoRoot();
        var path = ExpectedPath(repoRoot);
        await using var host = NewHost(repoRoot, HiveAuthMode.AuthenticationEnabled);
        var page = await RenderAsync(host, AdminId);

        await page.RunAndDisposeAsync(async () =>
        {
            var instance = page.Component;
            await page.SetFieldAsync("_configRepoPath", null);
            await page.RequestRenderAsync();
            Assert.DoesNotContain(BannerLabel, await page.HtmlAsync(), StringComparison.Ordinal);

            var gate = host.ConfigFacade.ArmPath();
            var refresh = page.StartCascadeAsync(Task.FromResult(new AuthenticationState(Principal(AdminId))));
            var authorized = await AwaitHeldReadAsync(gate, refresh);
            Assert.True(authorized.Success);
            Assert.Equal(path, authorized.Value);
            Assert.Null(page.Field<string?>("_configRepoPath"));

            var accessor = page.CircuitAccessor;
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            // Simulate observation elsewhere in the same scope; no newer page refresh masks this guard.
            Assert.Null(accessor.Observe(new AuthenticationState(Principal(ReplacementId))));
            Assert.True(accessor.IsInvalidated);

            gate.Release();
            await refresh.WaitAsync(JoinTimeout, Ct);

            Assert.Same(instance, page.Component);
            Assert.DoesNotContain(BannerLabel, await page.HtmlAsync(), StringComparison.Ordinal);
            Assert.Null(page.Field<string?>("_configRepoPath"));
        });
    }

    [Fact]
    public async Task RetainedIdentity_LatestCaptureDetectsDeliberateChildReplacement()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var repoRoot = UniqueRepoRoot();
        await using var host = NewHost(repoRoot, HiveAuthMode.AuthenticationEnabled);
        var page = await RenderAsync(host, AdminId);

        await page.RunAndDisposeAsync(async () =>
        {
            var original = page.Component;
            var fixedReference = page.Component;
            Assert.False(ReadField<bool>(original, "_disposed"));
            Assert.Contains(Banner(ExpectedPath(repoRoot)), await page.HtmlAsync(), StringComparison.Ordinal);
            await page.ReplaceChildAsync().WaitAsync(JoinTimeout, Ct);

            Assert.True(ReadField<bool>(original, "_disposed"));
            Assert.Same(original, fixedReference); // the old comparison would have accepted this
            Assert.NotSame(original, page.Component); // the renderer actually captured a replacement
            Assert.False(page.Field<bool>("_disposed"));
            Assert.Contains(Banner(ExpectedPath(repoRoot)), await page.HtmlAsync(), StringComparison.Ordinal);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Exhaustive routing audit (supplement, NOT a replacement for retained behavior)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AUDIT OF EVERY ONE OF THE 23 FACADE RESPONSE SITES named in the goal. Reads the compiled Razor
    /// page's generated C# and verifies, for EACH site, that the awaited result is passed to the ONE
    /// shared <c>HandleForbidden</c> handler BEFORE that call site's own early return, error field or
    /// stale-generation check. The two model-entry HELPERS are pinned by name plus every caller of
    /// their forwarded results; the branch-fetch site is pinned before its generation comparison.
    /// This supplements the retained behavioral rows above; it does not replace them.
    /// </summary>
    [Fact]
    public async Task ConfigurationPage_AllTwentyThreeFacadeResponses_RouteThroughSharedDenialHandling()
    {
        var source = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "CopilotHive", "Components", "Pages", "Configuration.razor"), Ct);

        // THE COMPLETE INVENTORY — 21 config + 2 backup = 23 named sites, in the goal's order.
        string[] configSites =
        [
            "GetConfigFilesAsync", "GetModelsAsync", "SaveModelsAsync", "GetRepositoriesAsync",
            "GetOrchestratorAsync", "SaveOrchestratorAsync", "GetWorkersAsync", "SaveWorkersAsync",
            "GetComposerAsync", "SaveComposerAsync", "AddRepositoryAsync", "GetBranchesAsync",
            "UpdateRepositoryAsync", "RemoveRepositoryAsync", "DiscoverModelsAsync",
            "AddAvailableModelAsync", "UpdateAvailableModelAsync", "RemoveAvailableModelAsync",
            "AddSubAgentModelAsync", "UpdateSubAgentModelAsync", "RemoveSubAgentModelAsync",
        ];
        string[] backupSites = ["GetBackupsAsync", "CreateBackupAsync"];
        Assert.Equal(23, configSites.Length + backupSites.Length);

        // The 20 DIRECT config result sites + 2 backup result sites each hand the awaited result to the
        // ONE shared handler as the immediately following executable statement. The three remaining
        // config sites (GetBranchesAsync + the two model-entry helpers) have dedicated checks below.
        string[] directConfigSites = configSites.Where(s => s is not ("GetBranchesAsync"
            or "AddAvailableModelAsync" or "AddSubAgentModelAsync")).ToArray();
        foreach (var site in directConfigSites.Concat(backupSites))
        {
            var marker = (backupSites.Contains(site) ? "BackupFacade." : "ConfigFacade.") + site + "(";
            var at = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{site}: no production call site found.");
            var statementEnd = source.IndexOf(';', at);
            Assert.True(statementEnd >= 0, $"{site}: unterminated await statement.");
            var between = source[(statementEnd + 1)..];
            var firstStatement = FirstExecutableLine(between);
            Assert.StartsWith("HandleForbidden(" + (site == "GetConfigFilesAsync" ? "filesResult" : "result") + ");",
                firstStatement, StringComparison.Ordinal);
        }

        // The branch-fetch site's shared handler runs BEFORE its own stale-generation check.
        var branchAt = source.IndexOf("ConfigFacade.GetBranchesAsync(", StringComparison.Ordinal);
        Assert.True(branchAt >= 0);
        var branchEnd = source.IndexOf(';', branchAt);
        var handlerAt = source.IndexOf("HandleForbidden(result);", branchEnd, StringComparison.Ordinal);
        var generationAt = source.IndexOf("if (generation == _branchFetchGeneration)", branchEnd, StringComparison.Ordinal);
        Assert.True(handlerAt >= 0 && handlerAt < generationAt,
            "GetBranchesAsync: shared handling must precede the caller-specific generation check.");

        // FORWARDED RESULTS: the two model-entry helpers invoke the remaining two sites and return the
        // production result unchanged. EVERY caller of a helper result handles that result next.
        Assert.Contains("await ConfigFacade.AddAvailableModelAsync(", source, StringComparison.Ordinal);
        Assert.Contains("await ConfigFacade.AddSubAgentModelAsync(", source, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(source, "await AddAvailableModelEntryAsync("));
        Assert.Equal(1, CountOccurrences(source, "await AddSubAgentModelEntryAsync("));
        foreach (var forwarded in new[] { "await AddAvailableModelEntryAsync(", "await AddSubAgentModelEntryAsync(" })
        {
            var from = 0;
            int at;
            while ((at = source.IndexOf(forwarded, from, StringComparison.Ordinal)) >= 0)
            {
                var end = source.IndexOf(';', at);
                Assert.StartsWith("HandleForbidden(result);", FirstExecutableLine(source[(end + 1)..]), StringComparison.Ordinal);
                from = end + 1;
            }
        }

    }

    /// <summary>Returns the first non-blank, non-comment source line after an awaited statement.</summary>
    private static string FirstExecutableLine(string tail)
    {
        foreach (var raw in tail.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith("*", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal))
                continue;
            return line;
        }
        return string.Empty;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    // ── test doubles / cleanup ──────────────────────────────────────────────

    /// <summary>A JS runtime that confirms prompts (the page only uses it for confirm dialogs).</summary>
    private sealed class ConfirmingJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult<TValue>((TValue)(object)true);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult<TValue>((TValue)(object)true);
    }

    public void Dispose()
    {
        _auth.Dispose();
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
