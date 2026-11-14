using System.Reflection;
using System.Security.Claims;
using Moq;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;

using CopilotHive.Components.Layout;
using CopilotHive.Configuration;
using CopilotHive.Components.Pages;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE RETAINED-COMPONENT BEHAVIOUR of the KNOWLEDGE/NAVIGATION authorization slice: the REAL
/// <see cref="Knowledge"/>, <see cref="KnowledgeDetail"/> and <see cref="NavMenu"/> components are
/// rendered through the framework's own <see cref="HtmlRenderer"/> over the REAL
/// <see cref="OperationalFacade"/>, the REAL <see cref="GoalFacade"/>, the REAL
/// <see cref="ClarificationFacade"/>, the REAL <see cref="HivePrincipalResolver"/>/<see cref="HivePolicy"/>
/// pair and the REAL <see cref="UserService"/> directory, with a REAL <see cref="KnowledgeGraph"/>.
/// <para>
/// THE CONTRACT: everything these components render is produced by an operation that re-resolves the
/// caller's CURRENT stored state, so a disabled/deleted account loses the content on its very next
/// load or retained-data callback. A denial CLEARS all previously rendered documents, links, related
/// goals and counters, shows a safe denial state (never a login redirect, never a false
/// "not found"), and never falls back to a direct graph/store/queue read.
/// </para>
/// <para>
/// Retained-state revocation rows prove BOTH HALVES: the authorized baseline really rendered the
/// marker, AND it is really gone afterwards — a state that was never populated would make its later "was cleared" assertion
/// vacuous. The scenario is deterministic: the account is authorized for the FIRST load, the directory
/// is then changed, and the component's OWN production path is triggered. No sleeps, no polling.
/// </para>
/// </summary>
public sealed class KnowledgeNavigationAuthorizationTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "knowledge-ui-viewer";
    private const string OperatorId = "knowledge-ui-operator";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private const string AlphaId = "architecture-alpha";
    private const string BetaId = "architecture-beta";
    private const string GammaId = "feature-gamma";

    private const string AlphaTitle = "ALPHA-TITLE-MARKER";
    private const string BetaTitle = "BETA-TITLE-MARKER";
    private const string GammaTitle = "GAMMA-TITLE-MARKER";
    private const string AlphaTag = "alpha-tag-marker";
    private const string AlphaBody = "ALPHA-BODY-MARKER";
    private const string LinkDescription = "ALPHA-LINK-DESCRIPTION-MARKER";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── Host ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The host over the PRODUCTION seam: the real resolver/policy over the real directory, the real
    /// operational/goal/clarification facades, a real dashboard state service holding a real knowledge
    /// graph, and the real scoped circuit accessor.
    /// </summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        internal Host(
            ServiceProvider provider,
            DashboardStateService dashboardState,
            KnowledgeGraph? graph,
            ClarificationQueueService queue,
            RecordingGoalStore goalStore,
            GateableOperationalFacade facade, Action releasePending)
        {
            _provider = provider;
            ReleasePending = releasePending;
            DashboardState = dashboardState;
            Graph = graph;
            Queue = queue;
            GoalStore = goalStore;
            OperationalFacade = facade;
        }

        internal Action ReleasePending { get; }

        internal ServiceProvider Provider => _provider;

        internal DashboardStateService DashboardState { get; }

        /// <summary>The real graph, or <c>null</c> when this host models an UNCONFIGURED graph.</summary>
        internal KnowledgeGraph? Graph { get; }

        internal ClarificationQueueService Queue { get; }

        internal RecordingGoalStore GoalStore { get; }

        /// <summary>The registered operational facade (decorated so a read can be parked deterministically).</summary>
        internal GateableOperationalFacade OperationalFacade { get; }

        public async ValueTask DisposeAsync()
        {
            // The dashboard service owns a polling timer: dispose it BEFORE the provider so no
            // callback can outlive the services it touches.
            ReleasePending();
            DashboardState.Dispose();
            await _provider.DisposeAsync();
        }
    }

    /// <summary>
    /// A controllable decorator over the production <see cref="IOperationalFacade"/> that is the
    /// REGISTERED <see cref="IOperationalFacade"/>: when a knowledge-list gate is armed it parks the
    /// NEXT list read at the REAL await (after the production answer is computed but BEFORE the page
    /// publishes it), so a test can hold an older authorized refresh in flight deterministically. Every
    /// other operation — and every ungated read — delegates verbatim.
    /// </summary>
    private sealed class GateableOperationalFacade(IOperationalFacade production) : IOperationalFacade
    {
        private readonly List<ReadGate> _gates = [];
        private ReadGate? _armedList;
        private ReadGate? _armedCount;
        private ReadGate? _armedDetail;
        internal FacadeResult<KnowledgeDocumentProjection>? LastDetailResult { get; private set; }

        /// <summary>
        /// EVERY <c>GetKnowledgeDocumentCountAsync</c> delegation the decorator has performed, in order.
        /// The count is the nav menu's own "did a refresh actually run?" signal: an unsubscribe that
        /// silently stops releasing reads (or a refresh that quietly returns without reading) is
        /// distinguishable from a genuine non-event only with this counter.
        /// </summary>
        internal int CountCalls { get; private set; }
        internal int ListCalls { get; private set; }

        /// <summary>Arms a gate on the NEXT knowledge-LIST read (the Knowledge page's refresh).</summary>
        internal ReadGate ArmList()
        {
            var gate = new ReadGate();
            lock (_gates) _gates.Add(gate);
            Volatile.Write(ref _armedList, gate);
            return gate;
        }

        /// <summary>Arms a gate on the NEXT knowledge-DETAIL read (the KnowledgeDetail page's refresh).</summary>
        internal ReadGate ArmDetail()
        {
            var gate = new ReadGate();
            lock (_gates) _gates.Add(gate);
            Volatile.Write(ref _armedDetail, gate);
            return gate;
        }

        /// <summary>Arms a gate on the NEXT knowledge-COUNT read (the NavMenu's refresh).</summary>
        internal ReadGate ArmCount()
        {
            var gate = new ReadGate();
            lock (_gates) _gates.Add(gate);
            Volatile.Write(ref _armedCount, gate);
            return gate;
        }

        internal void ReleaseAll()
        {
            ReadGate[] gates;
            lock (_gates) gates = [.. _gates];
            foreach (var gate in gates) gate.Release();
        }

        public async Task<FacadeResult<KnowledgeDocumentListProjection>> GetKnowledgeDocumentsAsync(
            string? searchQuery, ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            ListCalls++;
            // The PRODUCTION answer is computed FIRST, so the parked state really is "authorized read
            // finished, publication pending" — exactly the interleaving the generation guard protects.
            var result = await production.GetKnowledgeDocumentsAsync(searchQuery, actingUser, ct);

            var gate = Interlocked.Exchange(ref _armedList, null);
            if (gate is not null) await gate.ParkAsync();

            return result;
        }

        public async Task<FacadeResult<int>> GetKnowledgeDocumentCountAsync(
            ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            CountCalls++;
            var result = await production.GetKnowledgeDocumentCountAsync(actingUser, ct);

            var gate = Interlocked.Exchange(ref _armedCount, null);
            if (gate is not null) await gate.ParkAsync();

            return result;
        }

        public Task<FacadeResult> ResetBrainSessionAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.ResetBrainSessionAsync(actingUser, ct);

        public Task<FacadeResult<BrainStats>> GetBrainStatsAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.GetBrainStatsAsync(actingUser, ct);

        public Task<FacadeResult<OrchestratorInfo>> GetOrchestratorInfoAsync(
            ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.GetOrchestratorInfoAsync(actingUser, ct);

        public Task<FacadeResult<IReadOnlyList<LlmSessionInfo>>> GetSharedLlmSessionsAsync(
            ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.GetSharedLlmSessionsAsync(actingUser, ct);

        public async Task<FacadeResult<KnowledgeDocumentProjection>> GetKnowledgeDocumentAsync(
            string documentId, ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            var result = await production.GetKnowledgeDocumentAsync(documentId, actingUser, ct);
            LastDetailResult = result;

            var gate = Interlocked.Exchange(ref _armedDetail, null);
            if (gate is not null) await gate.ParkAsync();
            return result;
        }
    }

    /// <summary>ONE parked read: signals its arrival and awaits an explicit release.</summary>
    private sealed class ReadGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        internal async Task ParkAsync()
        {
            Entered.TrySetResult();
            await _release.Task;
        }
    }

    /// <summary>
    /// Seeds the REAL production knowledge graph with three documents, a distinctive tag and one
    /// outgoing link from Alpha to Beta (so Beta has a real incoming link and both link titles resolve).
    /// </summary>
    private static async Task<KnowledgeGraph> SeedGraphAsync()
    {
        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync(
            AlphaId, AlphaTitle, DocumentType.Implementation, AlphaBody,
            topic: "architecture", subtopic: null, author: "alice",
            tags: [AlphaTag, "shared-tag"], ct: Ct);
        await graph.CreateDocumentAsync(
            BetaId, BetaTitle, DocumentType.Feature, "beta body",
            topic: "architecture", subtopic: "internals", author: "bob",
            tags: ["shared-tag"], ct: Ct);
        await graph.CreateDocumentAsync(
            GammaId, GammaTitle, DocumentType.Idea, "gamma body",
            topic: "feature", subtopic: null, author: "carol",
            tags: ["future"], ct: Ct);
        graph.AddLink(AlphaId, new DocumentLink(BetaId, LinkType.DependsOn, LinkDescription));
        return graph;
    }

    private Host NewHost(
        HiveAuthMode mode,
        KnowledgeGraph? graph = null,
        bool graphConfigured = true,
        bool unusableGoalFacade = false,
        FacadeErrorKind goalFailureKind = FacadeErrorKind.Forbidden,
        Microsoft.JSInterop.IJSRuntime? js = null, GoalsProbe? goalsProbe = null)
    {
        // graphConfigured: false models the production "started without a config repo" shape, where
        // DashboardStateService.KnowledgeGraph is genuinely null (an UNCONFIGURED graph).
        // unusableGoalFacade: registers a goal facade whose every read FAILS, so the page's related-goals
        // branch can be proven to be failure-tolerant (the document itself must still render).
        var resolvedGraph = graphConfigured ? graph ?? new KnowledgeGraph() : null;
        var queue = new ClarificationQueueService();
        var goalStore = new CopilotHive.Tests.Services.RecordingGoalStore();
        var notifier = new DashboardNotifier();
        var dashboardState = new DashboardStateService(
            new WorkerPool(),
            new GoalPipelineManager(),
            new DashboardLogSink(),
            new ProgressLog(),
            notifier,
            knowledgeGraph: resolvedGraph);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(notifier);
        services.AddSingleton(dashboardState);
        if (resolvedGraph is not null) services.AddSingleton(resolvedGraph);
        services.AddSingleton(queue);
        services.AddSingleton(goalStore);
        services.AddSingleton<IGoalStore>(goalStore);
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(js ?? new NullJsRuntime());
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        services.AddSingleton(mode);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));

        var registry = new LlmSessionRegistry();
        services.AddSingleton(registry);
        // The registered operational facade is the PRODUCTION one, wrapped in the gateable decorator so
        // an overlap row can park a real read deterministically. Ungated, the decorator delegates
        // verbatim to the real facade.
        var gateable = new GateableOperationalFacade(new OperationalFacade(
            dashboardState,
            registry,
            _auth.Resolver(mode),
            _auth.Policy,
            NullLogger<OperationalFacade>.Instance));
        services.AddSingleton<IOperationalFacade>(gateable);

        services.AddSingleton<IClarificationFacade>(sp => new ClarificationFacade(
            sp.GetRequiredService<ClarificationQueueService>(),
            sp.GetRequiredService<IGoalStore>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            NullLogger<ClarificationFacade>.Instance));

        if (unusableGoalFacade)
        {
            // A goal facade whose EVERY read is refused by construction (a signed-out resolver in a
            // mode where nothing can authenticate), so the page's related-goals branch must degrade to
            // "no related goals" WITHOUT affecting the document itself.
            services.AddSingleton<IGoalFacade>(_ => new AlwaysDeniedGoalFacade(goalFailureKind));
        }
        else
        {
            services.AddSingleton<IGoalFacade>(sp =>
            {
                var real = new GoalFacade(
                sp.GetRequiredService<IGoalStore>(),
                new RecordingIssueStore(),
                new GoalReviewService(
                    knowledgeGraph: null,
                    configRepo: null,
                    config: new HiveConfigFile
                    {
                        Workers = { ["reviewer"] = new WorkerConfig { Model = "reviewer-model" } },
                    },
                    goalStore: sp.GetRequiredService<IGoalStore>(),
                    brainRepoManager: null,
                    stateDir: Path.GetTempPath(),
                    logger: NullLogger<GoalReviewService>.Instance),
                sp.GetRequiredService<DashboardNotifier>(),
                sp.GetRequiredService<HivePrincipalResolver>(),
                sp.GetRequiredService<HivePolicy>(),
                NullLogger<GoalFacade>.Instance);
                return goalsProbe?.Wrap(real) ?? real;
            });
        }

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, dashboardState, resolvedGraph, queue, goalStore, gateable, () =>
        {
            gateable.ReleaseAll();
            (js as GatedJsRuntime)?.Release();
            goalsProbe?.ReleaseAll();
        });
    }

    // ── Rendering helpers ───────────────────────────────────────────────────

    /// <summary>One rendered component: its markup accessor, scope, renderer and instance.</summary>
    private sealed class Rendered : IAsyncDisposable
    {
        private readonly HtmlRenderer _renderer;
        private readonly IServiceScope _scope;
        private readonly Func<string> _rootHtml;
        private readonly object _component;

        private readonly List<Task> _tasks = [];
        private readonly Action _releasePending;
        private Task Track(Task task) { _tasks.Add(task); return task; }

        internal Rendered(HtmlRenderer renderer, IServiceScope scope, Func<string> rootHtml, object component, Action releasePending)
        {
            _renderer = renderer;
            _scope = scope;
            _rootHtml = rootHtml;
            _component = component;
            _releasePending = releasePending;
        }

        internal object Component => _component;

        /// <summary>The CURRENT markup of the retained root.</summary>
        internal Task<string> HtmlAsync() => _renderer.Dispatcher.InvokeAsync(_rootHtml);

        /// <summary>Reads a private field of the rendered component.</summary>
        internal T Field<T>(string name) =>
            (T)_component.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_component)!;

        /// <summary>Sets a private field of the rendered component (to drive a bound filter value).</summary>
        internal Task SetFieldAsync(string name, object? value) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            _component.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_component, value);
        });

        /// <summary>Invokes a private method of the rendered component on the renderer dispatcher.</summary>
        internal async Task InvokePrivateAsync(string methodName, params object?[] args)
        {
            var method = _component.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"'{methodName}' not found on {_component.GetType().Name}.");
            await Track(_renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (method.Invoke(_component, args) is Task task) await task;
            }));
        }

        /// <summary>
        /// Invokes a PUBLIC method (e.g. the <see cref="IDisposable.Dispose"/> a component implements)
        /// on the renderer dispatcher.
        /// </summary>
        internal async Task InvokePublicAsync(string methodName, params object?[] args)
        {
            var method = _component.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"'{methodName}' not found on {_component.GetType().Name}.");
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (method.Invoke(_component, args) is Task task) await task;
            });
        }

        // The HtmlRenderer emits no browser event ids, but keeps the real rendered tree. Inspect it
        // only to obtain the production callback, preserving its ComponentBase IHandleEvent receiver.
#pragma warning disable BL0006
        internal Task FolderClickAsync(string topic) => Track(_renderer.Dispatcher.InvokeAsync(() =>
        {
            var handle = typeof(ComponentBase).GetField("_renderHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_component)!;
            var renderer = typeof(RenderHandle).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handle)!;
            var id = (int)typeof(RenderHandle).GetField("_componentId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handle)!;
            var frames = (ArrayRange<RenderTreeFrame>)typeof(Renderer).GetMethod("GetCurrentRenderTreeFrames", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(renderer, [id])!;
            for (var i = 0; i < frames.Count; i++)
            {
                var element = frames.Array[i];
                if (element.FrameType != RenderTreeFrameType.Element || element.ElementName != "div") continue;
                var end = i + element.ElementSubtreeLength;
                EventCallback? click = null;
                var matches = false;
                for (var j = i + 1; j < end; j++)
                {
                    var frame = frames.Array[j];
                    if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick")
                        click = Assert.IsType<EventCallback>(frame.AttributeValue);
                    if (frame.FrameType == RenderTreeFrameType.Text && frame.TextContent == topic) matches = true;
                }
                // The folder header has only span children; its parent also contains the document
                // table, so choose the innermost matching div with a direct onclick attribute.
                if (!matches || click is null) continue;
                var directClick = false;
                for (var j = i + 1; j < end && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                    directClick |= frames.Array[j].AttributeName == "onclick";
                if (!directClick) continue;
                var callback = click.Value;
                Assert.Same(_component, typeof(EventCallback).GetField("Receiver", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(callback));
                var handler = Assert.IsType<Func<Task>>(typeof(EventCallback).GetField("Delegate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(callback));
                // Own the handler task even if IHandleEvent's immediate render throws.
                return EventCallback.Factory.Create(_component, (Func<Task>)(() => Track(handler()))).InvokeAsync();
            }
            throw new InvalidOperationException($"No rendered folder onclick for '{topic}'.");
        }));
#pragma warning restore BL0006

        /// <summary>Sets a PUBLIC property of the rendered component (its [Parameter]).</summary>
        internal Task SetParameterAsync(string name, object? value) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            var property = _component.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Parameter '{name}' not found on {_component.GetType().Name}.");
            property.SetValue(_component, value);
            return Task.CompletedTask;
        });

        /// <summary>Re-renders the way the framework does after an event handler.</summary>
        internal Task RequestRenderAsync() => _renderer.Dispatcher.InvokeAsync(() =>
        {
            var stateHasChanged = _component.GetType().GetMethod(
                "StateHasChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(stateHasChanged);
            stateHasChanged!.Invoke(_component, null);
            return Task.CompletedTask;
        });

        public async ValueTask DisposeAsync()
        {
            _releasePending();
            List<Exception> failures = [];
            // Attempt EVERY retained operation even when one event failed during immediate render.
            // A fault in the wrapper does not prove that the separately owned handler has completed.
            for (var i = 0; i < _tasks.Count; i++)
            {
                try { await _tasks[i].WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
                catch (Exception ex) { failures.Add(ex); }
            }
            try { await _renderer.DisposeAsync(); } catch (Exception ex) { failures.Add(ex); }
            try { _scope.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count != 0) throw new AggregateException("Retained component cleanup failed.", failures);
        }
    }

    /// <summary>
    /// Renders one component over the production seam for one circuit identity. In OPEN mode the
    /// cascade is deliberately ABSENT, exactly what an open-mode circuit delivers. An
    /// <paramref name="anonymousCascade"/> request renders a REAL cascade whose state carries an
    /// UNAUTHENTICATED principal, exactly what a signed-out circuit with authentication enabled delivers.
    /// </summary>
    private async Task<Rendered> RenderAsync<TComponent>(
        Host host,
        string? gitHubId,
        IDictionary<string, object?>? parameters = null,
        bool anonymousCascade = false)
        where TComponent : IComponent
    {
        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        Func<string>? rootHtml = null;
        object? component = null;

        RenderFragment child = builder =>
        {
            builder.OpenComponent<TComponent>(0);
            if (parameters is not null)
            {
                foreach (var (key, value) in parameters)
                    builder.AddAttribute(0, key, value);
            }

            builder.AddComponentReferenceCapture(1, captured => component = captured);
            builder.CloseComponent();
        };

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root;
            if (gitHubId is null && !anonymousCascade)
            {
                // OPEN MODE: no cascading authentication state anywhere in the tree.
                root = await renderer.RenderComponentAsync<ContentHost>(
                    ParameterView.FromDictionary(new Dictionary<string, object?> { ["ChildContent"] = child }));
            }
            else
            {
                var principal = anonymousCascade
                    ? CopilotHive.Tests.Security.FacadeAuthorizationHarness.Anonymous()
                    : Principal(gitHubId!);
                var state = Task.FromResult(new AuthenticationState(principal));
                root = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["Value"] = state,
                        ["ChildContent"] = child,
                    }));
            }

            rootHtml = root.ToHtmlString;
        });

        Assert.NotNull(rootHtml);
        Assert.NotNull(component);
        return new Rendered(renderer, scope, rootHtml!, component!, host.ReleasePending);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. Knowledge.razor
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE AUTHORIZED LIST LOAD, THEN A DENIED REFRESH CLEARS EVERYTHING. The page first renders the
    /// real documents from the real graph (titles, an id and a tag — the non-vacuity control), then the
    /// account is disabled and the page's OWN refresh path runs: every document row, every filter value
    /// and the configured flag are cleared, the safe denial state is shown, and NOTHING is rendered as
    /// "not configured" or as a login redirect.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_AuthorizedLoadRendersDocuments_ThenDeniedRefreshClearsEverything()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        // ── AUTHORIZED BASELINE: the real documents really rendered. ──
        var before = await page.HtmlAsync();
        Assert.Contains(AlphaTitle, before, StringComparison.Ordinal);
        Assert.Contains(BetaTitle, before, StringComparison.Ordinal);
        Assert.Contains(GammaTitle, before, StringComparison.Ordinal);
        Assert.Contains(AlphaTag, before, StringComparison.Ordinal);
        Assert.Contains("3 document(s)", before, StringComparison.Ordinal);

        // The page's loaded state really holds the rows (from the FACADE, over the graph).
        Assert.Equal(3, page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs").Count);
        Assert.Equal(3, page.Field<List<KnowledgeDocumentSummary>>("_filtered").Count);
        Assert.True(page.Field<bool>("_configured"));
        Assert.False(page.Field<bool>("_denied"));

        // ── THE ACCOUNT IS DISABLED, THEN THE PAGE'S OWN REFRESH RUNS. ──
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();

        // NO stale document content survives.
        Assert.DoesNotContain(AlphaTitle, after, StringComparison.Ordinal);
        Assert.DoesNotContain(BetaTitle, after, StringComparison.Ordinal);
        Assert.DoesNotContain(GammaTitle, after, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("document(s)", after, StringComparison.Ordinal);

        // The page's own state is EMPTY.
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs"));
        Assert.Empty(page.Field<List<KnowledgeDocumentSummary>>("_filtered"));
        Assert.False(page.Field<bool>("_configured"));
        Assert.True(page.Field<bool>("_denied"));

        // A SAFE DENIAL STATE — the facade's own sanitized text, not a login redirect and not
        // "not configured".
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.DoesNotContain("login", after, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not configured", after, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A RETAINED-DATA CALLBACK (the search box) HONOURS DENIAL: with a search value already set and
    /// documents rendered, the account is disabled and the search callback runs — the page shows the
    /// safe denial state and clears the previously filtered rows AND the filter value.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_DeniedSearchCallback_ClearsFilteredRowsAndTheFilterValue()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        // AUTHORIZED: drive the page's OWN bound search value and run its refresh. The tag search
        // really narrows the rows through the graph's own search semantics.
        await page.SetFieldAsync("_searchText", "shared-tag");
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();
        Assert.Equal(2, page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs").Count);
        Assert.False(page.Field<bool>("_denied"));
        Assert.Contains(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.DoesNotContain(AlphaTitle, after, StringComparison.Ordinal);
        Assert.DoesNotContain(BetaTitle, after, StringComparison.Ordinal);
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs"));
        Assert.Empty(page.Field<List<KnowledgeDocumentSummary>>("_filtered"));

        // The retained FILTER VALUE is cleared too, so a later authorized load cannot silently re-apply
        // a search from the denied state.
        Assert.Equal("", page.Field<string>("_searchText"));
    }

    /// <summary>
    /// AN OLDER AUTHORIZED REFRESH CANNOT OVERWRITE A NEWER DENIED CLEAR. A first refresh is parked at
    /// its REAL count await (a controllable decorator over the production facade); a SECOND, denied
    /// refresh then runs to completion and clears everything; the parked older authorized result is
    /// released afterwards. Because the generation guard rejects the stale publication, the denial
    /// stands — no document reappears.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_OlderAuthorizedRefresh_AfterANewerDenial_CannotRepopulateTheDocuments()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        Assert.False(page.Field<bool>("_denied"));
        Assert.Equal(3, page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs").Count);

        // The FIRST (authorized-identity) refresh is parked at the REAL await inside the facade call.
        var gate = host.OperationalFacade.ArmList();
        var older = page.InvokePrivateAsync("RefreshAsync");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        // A NEWER refresh now runs while the older one is parked. The account is already disabled in the
        // directory, so this one is DENIED and clears everything.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();

        var denied = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, denied, StringComparison.Ordinal);
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs"));

        // Release the parked OLDER refresh: its success must NOT overwrite the newer denial.
        gate.Release();
        await older;
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaTitle, after, StringComparison.Ordinal);
        Assert.DoesNotContain(BetaTitle, after, StringComparison.Ordinal);
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs"));
        Assert.Empty(page.Field<List<KnowledgeDocumentSummary>>("_filtered"));
        Assert.True(page.Field<bool>("_denied"));
    }

    /// <summary>
    /// THE UNCONFIGURED GRAPH SURFACE IS PRESERVED FOR AN AUTHORIZED CALLER: with no graph at all the
    /// page renders its existing "not configured" message (a SUCCESS answer), and NOT the denial state.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_UnconfiguredGraph_RendersTheNotConfiguredSurface_NotADenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, graphConfigured: false);
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        var html = await page.HtmlAsync();

        Assert.Contains("Knowledge graph is not configured", html, StringComparison.Ordinal);
        Assert.DoesNotContain(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.False(page.Field<bool>("_denied"));
        Assert.False(page.Field<bool>("_configured"));
    }

    /// <summary>
    /// EVERY SharedRead ROLE — including the two the other rows do not exercise — renders the loaded
    /// documents on the initial load. The control proves the page is not secretly restricted to one role
    /// and that the facade's SharedRead semantics reach the rendered markup.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_AllSharedReadRoles_RenderDocumentsOnInitialLoad()
    {
        foreach (var role in new[] { UserRole.Viewer, UserRole.Member, UserRole.Operator, UserRole.Admin })
        {
            var accountId = $"knowledge-role-{role}";
            await _auth.SeedAsync(accountId, role);
            await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
            await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, accountId);

            var html = await page.HtmlAsync();
            Assert.Contains(AlphaTitle, html, StringComparison.Ordinal);
            Assert.Contains(BetaTitle, html, StringComparison.Ordinal);
            Assert.Contains("3 document(s)", html, StringComparison.Ordinal);
            Assert.False(page.Field<bool>("_denied"));
        }
    }

    /// <summary>
    /// A DELETED ACCOUNT DENIES ON THE VERY NEXT REFRESH: the authorized first load renders the
    /// documents, the row is deleted from the directory, and the page's own refresh clears everything —
    /// the account does not get one last authorized read.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_AccountDeletedAfterLoad_NextRefreshClearsEverything()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        Assert.Contains(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.Equal(3, page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs").Count);

        await _auth.DeleteUserAsync(ViewerId);
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();

        var html = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaTitle, html, StringComparison.Ordinal);
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs"));
        Assert.True(page.Field<bool>("_denied"));
    }

    /// <summary>
    /// AN ANONYMOUS CIRCUIT (authentication enabled, a cascade that carries NO authenticated principal)
    /// is refused exactly as a disabled account is: nothing is rendered, the safe denial state is shown,
    /// and the ordinary content never appears. The page must not fall back to a direct graph read just
    /// because the identity is missing — absence of an identity is a refusal, not a free pass.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_AnonymousCascade_ClearsEverything_AndShowsTheDenialState()
    {
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(
            host, gitHubId: null, anonymousCascade: true);

        var html = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaTitle, html, StringComparison.Ordinal);
        Assert.DoesNotContain("document(s)", html, StringComparison.Ordinal);
        Assert.True(page.Field<bool>("_denied"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs"));
    }

    /// <summary>
    /// AN UNKNOWN ACCOUNT (an authenticated identity with no activated account in the directory) is
    /// refused exactly like a disabled one: nothing rendered, the safe denial state, and the queue and
    /// the graph are never read as a fallback.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_UnknownAccount_DeniesAndClears()
    {
        const string unknownId = "knowledge-ui-never-activated";
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        host.Queue.Enqueue(HumanRequest("nav-pending-unknown"));
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, unknownId);

        var html = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaTitle, html, StringComparison.Ordinal);
        Assert.True(page.Field<bool>("_denied"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs"));
    }

    /// <summary>
    /// THE AUTHORIZED INTERACTIVE FILTERS: type, status and tag filters narrow the rows in place, the
    /// type/status dropdowns and the tag box all drive the page's own filter path, and the filter state
    /// is observable both in the rendered markup and in the component's state.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_FiltersByTypeStatusAndTag_NarrowTheRenderedRows()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        // Seed a graph whose documents differ in type AND status so the filters can discriminate.
        var graph = await SeedGraphAsync();
        await graph.UpdateDocumentAsync(GammaId, status: DocumentStatus.Archived, ct: Ct);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, graph);
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        // Baseline: the archived Gamma is hidden by the default "all except archived" filter; Alpha and Beta show.
        var baseline = await page.HtmlAsync();
        Assert.Contains(AlphaTitle, baseline, StringComparison.Ordinal);
        Assert.Contains(BetaTitle, baseline, StringComparison.Ordinal);
        Assert.DoesNotContain(GammaTitle, baseline, StringComparison.Ordinal);
        Assert.Equal(
            [AlphaId, BetaId],
            page.Field<List<KnowledgeDocumentSummary>>("_filtered").Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal));

        // TYPE FILTER: Implementation leaves only Alpha.
        await page.SetFieldAsync("_typeFilter", nameof(DocumentType.Implementation));
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();
        var byType = await page.HtmlAsync();
        Assert.Contains(AlphaTitle, byType, StringComparison.Ordinal);
        Assert.DoesNotContain(BetaTitle, byType, StringComparison.Ordinal);
        Assert.Equal("Implementation", page.Field<string>("_typeFilter"));

        // STATUS FILTER: Draft leaves only Beta (Alpha is Implementation/Draft too — use Feature for the type reset).
        await page.SetFieldAsync("_typeFilter", "");
        await page.SetFieldAsync("_statusFilter", nameof(DocumentStatus.Draft));
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();
        var byDraft = page.Field<List<KnowledgeDocumentSummary>>("_filtered").Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal([AlphaId, BetaId], byDraft);

        // Explicitly select Archived: only Gamma survives.
        await page.SetFieldAsync("_statusFilter", nameof(DocumentStatus.Archived));
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();
        var archivedOnly = await page.HtmlAsync();
        Assert.Contains(GammaTitle, archivedOnly, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaTitle, archivedOnly, StringComparison.Ordinal);
        Assert.Equal([GammaId], page.Field<List<KnowledgeDocumentSummary>>("_filtered").Select(d => d.Id).ToList());

        // TAG FILTER (on a clean slate): the shared tag matches Alpha and Beta only.
        await page.SetFieldAsync("_statusFilter", "");
        await page.SetFieldAsync("_tagFilter", "shared-tag");
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();
        var byTag = page.Field<List<KnowledgeDocumentSummary>>("_filtered").Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal([AlphaId, BetaId], byTag);

        // TAG FILTER with no match: an empty result set, still authorized and still rendered.
        await page.SetFieldAsync("_tagFilter", "no-such-tag-marker");
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();
        Assert.Empty(page.Field<List<KnowledgeDocumentSummary>>("_filtered"));
        Assert.Contains("No documents match", await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.False(page.Field<bool>("_denied"));
    }

    /// <summary>
    /// THE RESET BUTTON restores the unfiltered authorized view (all rows, default status handling,
    /// cleared filter values) after a filtered search narrowed it.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_ResetFilters_RestoresTheUnfilteredAuthorizedView()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        // Narrow the view until only Gamma remains, then reset.
        await page.SetFieldAsync("_searchText", "future");
        await page.InvokePrivateAsync("RefreshAsync");
        Assert.Equal([GammaId], page.Field<List<KnowledgeDocumentSummary>>("_filtered").Select(d => d.Id).ToList());

        await page.InvokePrivateAsync("ResetFilters");
        await page.RequestRenderAsync();

        Assert.Equal("", page.Field<string>("_searchText"));
        Assert.Equal("", page.Field<string>("_typeFilter"));
        Assert.Equal("", page.Field<string>("_statusFilter"));
        Assert.Equal("", page.Field<string>("_tagFilter"));
        Assert.Equal(3, page.Field<List<KnowledgeDocumentSummary>>("_filtered").Count);
        var html = await page.HtmlAsync();
        Assert.Contains(AlphaTitle, html, StringComparison.Ordinal);
        Assert.Contains(GammaTitle, html, StringComparison.Ordinal);
    }

    /// <summary>
    /// AFTER A DENIAL, EVERY RETAINED-DATA CALLBACK HONOURS THE REFUSAL — the search box, all three
    /// filter dropdowns/tags, the reset button and the folder toggle each re-verify identity and clear
    /// the previously rendered rows instead of resurrecting them from the retained filter state.
    /// </summary>
    [Theory]
    [InlineData(false)] // disable the account between the authorized load and the callback
    [InlineData(true)] // DELETE the account between the authorized load and the callback
    public async Task KnowledgePage_DeniedRetainedDataCallbacks_AllClearTheRenderedRows(bool deleteAccount)
    {
        foreach (var callback in new[] { "Search", "Type", "Status", "Tag", "Reset", "Folder" })
        {
            // A fresh account per callback: the parametrised rows must not depend on execution order.
            var accountId = $"knowledge-ui-callback-{callback.ToLowerInvariant()}-{(deleteAccount ? "delete" : "disable")}";
            await _auth.SeedAsync(accountId, UserRole.Viewer);
            await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
            await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, accountId);

            Assert.Equal(3, page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs").Count);
            if (deleteAccount) await _auth.DeleteUserAsync(accountId);
            else await _auth.SetEnabledAsync(accountId, enabled: false);

            switch (callback)
            {
                case "Search":
                    await page.SetFieldAsync("_searchText", "shared-tag");
                    await page.InvokePrivateAsync("RefreshAsync");
                    break;
                case "Type":
                    await page.SetFieldAsync("_typeFilter", nameof(DocumentType.Implementation));
                    await page.InvokePrivateAsync("RefreshAsync");
                    break;
                case "Status":
                    await page.SetFieldAsync("_statusFilter", nameof(DocumentStatus.Draft));
                    await page.InvokePrivateAsync("RefreshAsync");
                    break;
                case "Tag":
                    await page.SetFieldAsync("_tagFilter", "shared-tag");
                    await page.InvokePrivateAsync("RefreshAsync");
                    break;
                case "Reset":
                    await page.InvokePrivateAsync("ResetFilters");
                    break;
                case "Folder":
                    await page.InvokePrivateAsync("ToggleFolderAsync", "architecture");
                    break;
                default:
                    throw new InvalidOperationException(callback);
            }

            await page.RequestRenderAsync();
            var html = await page.HtmlAsync();
            Assert.True(page.Field<bool>("_denied"), $"{callback}: denial state must be set");
            Assert.Empty(page.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs"));
            Assert.Empty(page.Field<List<KnowledgeDocumentSummary>>("_filtered"));
            Assert.DoesNotContain(AlphaTitle, html, StringComparison.Ordinal);
            Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// THE FOLDER TOGGLE IS AN AUTHORIZED CALLBACK TOO: after a denial, toggling a folder does not
    /// resurrect the previously rendered rows and does not read the graph — the denial state is
    /// re-asserted by the page's own refresh path.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_FolderToggleAfterDenial_KeepsTheDenialState()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        // Authorized: collapse the folder and confirm the state took effect.
        await page.InvokePrivateAsync("ToggleFolderAsync", "architecture");
        Assert.Contains("architecture", page.Field<HashSet<string>>("_collapsedFolders"), StringComparer.OrdinalIgnoreCase);

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokePrivateAsync("ToggleFolderAsync", "feature");
        await page.RequestRenderAsync();

        Assert.True(page.Field<bool>("_denied"));
        Assert.Empty(page.Field<List<KnowledgeDocumentSummary>>("_filtered"));
        Assert.DoesNotContain(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A DENIED REFRESH DOES NOT LEAK ACROSS ACCOUNTS: after the account is disabled and the page is
    /// cleared, a DIFFERENT still-authorized account re-enabling the page through the same component
    /// instance is not possible from the UI — but the cleared state must not be re-populated merely by
    /// a stale render. This row asserts the clear is sticky until a NEW authorized refresh runs.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_DeniedState_IsStickyUntilANewAuthorizedRefreshRuns()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();
        Assert.True(page.Field<bool>("_denied"));

        // A pure re-render (no refresh) must NOT resurrect content.
        await page.RequestRenderAsync();
        var html = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaTitle, html, StringComparison.Ordinal);

        // Re-enable the account: the next authorized refresh restores the content.
        await _auth.SetEnabledAsync(ViewerId, enabled: true);
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();
        var restored = await page.HtmlAsync();
        Assert.Contains(AlphaTitle, restored, StringComparison.Ordinal);
        Assert.False(page.Field<bool>("_denied"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. KnowledgeDetail.razor
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE AUTHORIZED DETAIL LOAD: the document body, its tags, its OUTGOING link with the TARGET'S
    /// RESOLVED TITLE, and the related goal really render (all produced by authorized reads — the link
    /// title by the facade, the goal by <see cref="IGoalFacade"/>), and the page title follows the
    /// document.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_AuthorizedLoadRendersDocumentLinksResolvedTitlesAndRelatedGoals()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());

        // A goal that references Alpha, so the related-goals section really populates through IGoalFacade.
        host.GoalStore.AddGoal(new Goal
        {
            Id = "related-goal-marker",
            Description = "RELATED-GOAL-DESCRIPTION-MARKER",
            Status = GoalStatus.Completed,
            CreatedAt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            Documents = [AlphaId],
        });

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = AlphaId });

        var html = await page.HtmlAsync();

        // The document really rendered: body, tag and title.
        Assert.Contains("ALPHA-BODY-MARKER", html, StringComparison.Ordinal);
        Assert.Contains(AlphaTag, html, StringComparison.Ordinal);

        // The rendered METADATA (type badge, status badge, topic, author, timestamps) comes from the
        // detached projection, not from a live graph read.
        Assert.Contains("Implementation", html, StringComparison.Ordinal);
        Assert.Contains("Draft", html, StringComparison.Ordinal);
        Assert.Contains("architecture", html, StringComparison.Ordinal);
        Assert.Contains("alice", html, StringComparison.Ordinal);
        Assert.Contains("Created:", html, StringComparison.Ordinal);
        Assert.Contains("Updated:", html, StringComparison.Ordinal);

        // The OUTGOING link's TARGET TITLE was resolved inside the authorized facade read.
        Assert.Contains(LinkDescription, html, StringComparison.Ordinal);
        Assert.Contains("Outgoing Links (1)", html, StringComparison.Ordinal);
        Assert.Contains(BetaTitle, html, StringComparison.Ordinal);

        // The related goal rendered through IGoalFacade.GetGoalsAsync.
        Assert.Contains("Related Goals (1)", html, StringComparison.Ordinal);
        Assert.Contains("related-goal-marker", html, StringComparison.Ordinal);

        // The page title follows the DOCUMENT's title (the facade projection, not an id echo).
        var header = host.Provider.GetRequiredService<PageHeaderState>();
        Assert.Equal($"\uD83D\uDCDA {AlphaTitle}", header.Title);
        Assert.Equal("/knowledge", header.BackUrl);

        // The projection is DETACHED: the page holds no live graph/document reference.
        var doc = page.Field<KnowledgeDocumentDetail?>("_doc");
        Assert.NotNull(doc);
        Assert.Equal(AlphaId, doc!.Id);
        Assert.Equal(AlphaTitle, doc.Title);
        Assert.Equal("architecture", doc.Topic);
        Assert.Null(doc.Subtopic);
        Assert.Equal(DocumentType.Implementation, doc.Type);
        Assert.Equal(DocumentStatus.Draft, doc.Status);
        Assert.Equal("alice", doc.Author);
        Assert.Equal("ALPHA-BODY-MARKER", doc.Content);
        Assert.Equal([AlphaTag, "shared-tag"], doc.Tags);
        Assert.False(page.Field<bool>("_denied"));
        Assert.False(page.Field<bool>("_notFound"));
    }

    /// <summary>
    /// A DENIED DETAIL LOAD CLEARS THE DOCUMENT, LINKS, RELATED GOALS AND PAGE TITLE and shows the safe
    /// denial state — never a false "not found" (which would be a false statement about the document)
    /// and never a login redirect.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_DeniedReload_ClearsDocumentLinksGoalsAndTitle_AndIsNotRenderedAsNotFound()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        host.GoalStore.AddGoal(new Goal
        {
            Id = "related-goal-marker",
            Description = "RELATED-GOAL-DESCRIPTION-MARKER",
            Status = GoalStatus.Completed,
            CreatedAt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            Documents = [AlphaId],
        });

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = AlphaId });

        // ── AUTHORIZED BASELINE first (non-vacuity). ──
        var before = await page.HtmlAsync();
        Assert.Contains("ALPHA-BODY-MARKER", before, StringComparison.Ordinal);
        Assert.Contains("Outgoing Links (1)", before, StringComparison.Ordinal);
        Assert.Contains("Related Goals (1)", before, StringComparison.Ordinal);
        var header = host.Provider.GetRequiredService<PageHeaderState>();
        Assert.Equal($"\uD83D\uDCDA {AlphaTitle}", header.Title);

        // ── THE ACCOUNT IS DISABLED AND THE PAGE'S OWN PARAMETER PATH RUNS AGAIN. ──
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokePrivateAsync("OnParametersSetAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();

        // No stale document, link or goal content.
        Assert.DoesNotContain("ALPHA-BODY-MARKER", after, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("Outgoing Links", after, StringComparison.Ordinal);
        Assert.DoesNotContain("Incoming Links", after, StringComparison.Ordinal);
        Assert.DoesNotContain("Related Goals", after, StringComparison.Ordinal);
        Assert.DoesNotContain("related-goal-marker", after, StringComparison.Ordinal);
        Assert.DoesNotContain(BetaTitle, after, StringComparison.Ordinal);

        // The page's state is EMPTY, including the page header.
        Assert.Null(page.Field<KnowledgeDocumentDetail?>("_doc"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeOutgoingLink>>("_outgoingLinks"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeIncomingLink>>("_incomingLinks"));
        Assert.Empty(page.Field<IReadOnlyList<Goal>>("_relatedGoals"));
        Assert.Equal($"\uD83D\uDCDA {AlphaId}", header.Title);
        Assert.True(page.Field<bool>("_denied"));

        // A SAFE DENIAL STATE: the sanitized text, NOT "was not found" and NOT a login redirect.
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found", after, StringComparison.Ordinal);
        Assert.False(page.Field<bool>("_notFound"));
        Assert.DoesNotContain("login", after, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE NOT-FOUND SURFACE IS PRESERVED FOR AN AUTHORIZED CALLER: an unknown document id renders the
    /// existing "was not found" message — a NotFound answer is not a denial.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_UnknownDocument_RendersTheNotFoundSurface_NotADenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = "no-such-document" });

        var html = await page.HtmlAsync();

        Assert.Contains("was not found", html, StringComparison.Ordinal);
        Assert.DoesNotContain(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.True(page.Field<bool>("_notFound"));
        Assert.False(page.Field<bool>("_denied"));
    }

    /// <summary>
    /// INCOMING LINKS ARE RESOLVED TOO: Beta's page shows the incoming link from Alpha with Alpha's
    /// RESOLVED TITLE and the inverse link type — the second half of the "titles resolved inside the
    /// facade" contract, proven through the real page.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_IncomingLink_RendersTheResolvedSourceTitle()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = BetaId });

        var html = await page.HtmlAsync();

        Assert.Contains("Incoming Links (1)", html, StringComparison.Ordinal);
        Assert.Contains(LinkDescription, html, StringComparison.Ordinal);
        Assert.Contains(AlphaTitle, html, StringComparison.Ordinal);
        Assert.Contains("DependedOnBy", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A PARAMETER CHANGE RE-AUTHORIZES FROM SCRATCH: the same retained component instance, given a
    /// different <c>DocumentId</c> AFTER the account was disabled, renders the denial state rather than
    /// the newly requested document, and clears what the authorized first load had shown.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_ParameterChangeAfterDisable_RendersDenialInsteadOfTheNewDocument()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = AlphaId });

        // Authorized first load: the Alpha document is on screen.
        Assert.Contains("ALPHA-BODY-MARKER", await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.Equal(AlphaId, page.Field<KnowledgeDocumentDetail?>("_doc")?.Id);

        // The account is disabled; the ROUTE PARAMETER changes on the same retained component.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.SetParameterAsync("DocumentId", BetaId);
        await page.InvokePrivateAsync("OnParametersSetAsync");
        await page.RequestRenderAsync();

        var html = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain("beta body", html, StringComparison.Ordinal);
        Assert.Null(page.Field<KnowledgeDocumentDetail?>("_doc"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeOutgoingLink>>("_outgoingLinks"));
        Assert.True(page.Field<bool>("_denied"));
    }

    /// <summary>
    /// THE UNKNOWN-ACCOUNT DETAIL LOAD: a fully populated document is rendered first for a real Viewer,
    /// then the SAME circuit re-parameterizes to a document id while the account is deleted — the page
    /// shows the denial state and clears the previously authorized content instead of the not-found state.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_AccountDeletedBeforeReload_ClearsEverything_AndIsNotRenderedAsNotFound()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = AlphaId });
        Assert.Contains("ALPHA-BODY-MARKER", await page.HtmlAsync(), StringComparison.Ordinal);

        await _auth.DeleteUserAsync(ViewerId);
        await page.InvokePrivateAsync("OnParametersSetAsync");
        await page.RequestRenderAsync();

        var html = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ALPHA-BODY-MARKER", html, StringComparison.Ordinal);
        Assert.Null(page.Field<KnowledgeDocumentDetail?>("_doc"));
        Assert.True(page.Field<bool>("_denied"));
        Assert.False(page.Field<bool>("_notFound"));
    }

    /// <summary>
    /// AN UNCONFIGURED GRAPH renders the existing "not found" surface for ANY document id (an absent
    /// graph cannot tell one id from another) — NOT the denial state and NOT a blank page. It is the
    /// authorized not-found answer, exactly as the production page treats an unconfigured graph.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_UnconfiguredGraph_RendersTheNotFoundSurface_NotADenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, graphConfigured: false);

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = AlphaId });

        var html = await page.HtmlAsync();
        Assert.Contains("was not found", html, StringComparison.Ordinal);
        Assert.DoesNotContain(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.True(page.Field<bool>("_notFound"));
        Assert.False(page.Field<bool>("_denied"));
    }

    /// <summary>
    /// AN ANONYMOUS CIRCUIT (authentication enabled, a cascade carrying no authenticated principal) is
    /// given the denial state — never the "not found" surface (which would be a false statement about
    /// the document) and never the content.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_AnonymousCascade_ClearsEverything_AndIsNotRenderedAsNotFound()
    {
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, gitHubId: null, parameters: new Dictionary<string, object?> { ["DocumentId"] = AlphaId },
            anonymousCascade: true);

        var html = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ALPHA-BODY-MARKER", html, StringComparison.Ordinal);
        Assert.False(page.Field<bool>("_notFound"));
        Assert.True(page.Field<bool>("_denied"));
        Assert.Null(page.Field<KnowledgeDocumentDetail?>("_doc"));
    }

    /// <summary>
    /// A FORBIDDEN RELATED-GOALS READ IS A FULL AUTHORIZATION DENIAL: the document read succeeded while
    /// the caller was still authorized, but <see cref="FacadeErrorKind.Forbidden"/> from the goal facade
    /// establishes SharedRead is no longer held (the account was disabled/deleted between the two
    /// reads), so the WHOLE detail surface — document, links, related goals and the page title — is
    /// cleared and the safe denial state is shown (not "not found").
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_RelatedGoalsForbidden_ClearsTheWholeDetailSurface()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(
            HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(), unusableGoalFacade: true);

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = AlphaId });

        var html = await page.HtmlAsync();
        Assert.DoesNotContain("ALPHA-BODY-MARKER", html, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaTag, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Outgoing Links", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Related Goals", html, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found", html, StringComparison.Ordinal);
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.True(page.Field<bool>("_denied"));
        Assert.False(page.Field<bool>("_notFound"));
        Assert.Null(page.Field<KnowledgeDocumentDetail?>("_doc"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeOutgoingLink>>("_outgoingLinks"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeIncomingLink>>("_incomingLinks"));
        Assert.Empty(page.Field<IReadOnlyList<Goal>>("_relatedGoals"));
        // The page title no longer carries the document's title.
        var header = host.Provider.GetRequiredService<PageHeaderState>();
        Assert.DoesNotContain(AlphaTitle, header.Title, StringComparison.Ordinal);
        Assert.Empty(host.GoalStore.AllCalls);
    }

    /// <summary>
    /// A NON-AUTHORIZATION FAILURE OF THE GOALS READ STILL DEGRADES GRACEFULLY: the document, links and
    /// title render from their own authorized read and only the related-goals section is omitted. This
    /// pins that only <see cref="FacadeErrorKind.Forbidden"/> is treated as a denial.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_RelatedGoalsNonAuthorizationFailure_RendersTheDocumentWithoutGoals()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(
            HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(),
            unusableGoalFacade: true, goalFailureKind: FacadeErrorKind.Internal);

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = AlphaId });

        var html = await page.HtmlAsync();
        Assert.Contains("ALPHA-BODY-MARKER", html, StringComparison.Ordinal);
        Assert.Contains("Outgoing Links (1)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Related Goals", html, StringComparison.Ordinal);
        Assert.DoesNotContain(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.Empty(page.Field<IReadOnlyList<Goal>>("_relatedGoals"));
        Assert.False(page.Field<bool>("_denied"));
        Assert.NotNull(page.Field<KnowledgeDocumentDetail?>("_doc"));
        Assert.NotEmpty(page.Field<IReadOnlyList<KnowledgeOutgoingLink>>("_outgoingLinks"));
        Assert.Contains(AlphaTitle, host.Provider.GetRequiredService<PageHeaderState>().Title, StringComparison.Ordinal);
        Assert.Empty(host.GoalStore.AllCalls);
    }

    /// <summary>
    /// Authorized related goals render through the production read. The separate real-between-reads
    /// and refused-goals rows discriminate the facade from an identity-free IGoalStore fallback.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_RelatedGoalsAreReadThroughTheGoalFacade_NotTheStoreDirectly()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        host.GoalStore.AddGoal(new Goal
        {
            Id = "related-goal-marker",
            Description = "RELATED-GOAL-DESCRIPTION-MARKER",
            Status = GoalStatus.Completed,
            CreatedAt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            Documents = [AlphaId],
        });

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = AlphaId });

        Assert.Contains("related-goal-marker", await page.HtmlAsync(), StringComparison.Ordinal);
        // This records the authorized store-read surface, not by itself proof of which caller read it.
        // The refused-goals rows discriminate a direct fallback by requiring ZERO store work there.
        Assert.NotEmpty(host.GoalStore.AllCalls);
        Assert.All(host.GoalStore.AllCalls, call => Assert.Equal("GetAllGoalsAsync", call));
    }

    /// <summary>
    /// AN OLDER AUTHORIZED DETAIL READ CANNOT OVERWRITE A NEWER DENIED CLEAR. The first (authorized)
    /// refresh is parked inside the facade's detail read; a newer refresh runs while it is disabled and
    /// clears everything; the parked older result is released afterwards and must publish nothing.
    /// </summary>
    [Fact]
    public async Task KnowledgeDetailPage_OlderAuthorizedRead_AfterANewerDenial_CannotRepopulateTheDocument()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());

        await using var page = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["DocumentId"] = AlphaId });

        Assert.Equal(AlphaId, page.Field<KnowledgeDocumentDetail?>("_doc")?.Id);
        Assert.False(page.Field<bool>("_denied"));

        // Park the OLDER read inside the facade's detail await; everything before the park (identity
        // acquisition, clearing, page title) has already happened on this call stack.
        var gate = host.OperationalFacade.ArmDetail();
        var older = page.InvokePrivateAsync("OnParametersSetAsync");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        // Disable and run a NEWER refresh: it is denied and clears the rendered document.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokePrivateAsync("OnParametersSetAsync");
        await page.RequestRenderAsync();

        var denied = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, denied, StringComparison.Ordinal);
        Assert.Null(page.Field<KnowledgeDocumentDetail?>("_doc"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeOutgoingLink>>("_outgoingLinks"));

        // Release the parked OLDER authorized read: it must NOT repopulate the cleared page.
        gate.Release();
        await older;
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.DoesNotContain("ALPHA-BODY-MARKER", after, StringComparison.Ordinal);
        Assert.Null(page.Field<KnowledgeDocumentDetail?>("_doc"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeOutgoingLink>>("_outgoingLinks"));
        Assert.True(page.Field<bool>("_denied"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. NavMenu.razor
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE AUTHORIZED COUNTER LOAD: both badges render the AUTHORIZED counts — the pending-clarification
    /// count from <see cref="IClarificationFacade"/> and the knowledge-document count from
    /// <see cref="IOperationalFacade"/> — even though the nav menu also injects the queue as its event
    /// source.
    /// </summary>
    [Fact]
    public async Task NavMenu_AuthorizedLoad_RendersBothAuthorizedCounters()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        host.Queue.Enqueue(HumanRequest("nav-pending-1"));

        await using var menu = await RenderAsync<NavMenu>(host, ViewerId);

        var html = await menu.HtmlAsync();
        Assert.Equal(1, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(3, menu.Field<int>("_knowledgeDocCount"));

        // Both badges really rendered their authorized numbers.
        Assert.Contains("badge-clarification", html, StringComparison.Ordinal);
        Assert.Contains("1 pending clarification(s)", html, StringComparison.Ordinal);
        Assert.Contains("3 document(s)", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE INITIAL LOAD IS AUTHORIZED TOO: the badges render only AFTER the account is confirmed by the
    /// facades. A signed-out (anonymous) circuit — authentication enabled, a cascade carrying no
    /// authenticated principal — renders NEITHER badge, even though the queue and the graph hold data.
    /// </summary>
    [Fact]
    public async Task NavMenu_AnonymousCascade_RendersNoCounters()
    {
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        host.Queue.Enqueue(HumanRequest("nav-pending-anon"));

        await using var menu = await RenderAsync<NavMenu>(host, gitHubId: null, anonymousCascade: true);

        var html = await menu.HtmlAsync();
        Assert.Equal(0, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(0, menu.Field<int>("_knowledgeDocCount"));
        Assert.DoesNotContain("badge-clarification", html, StringComparison.Ordinal);
        Assert.DoesNotContain("document(s)", html, StringComparison.Ordinal);
        // The queue still holds the pending request: nothing was read from it directly.
        Assert.Equal(1, host.Queue.PendingHumanCount);
    }

    /// <summary>
    /// AN UNKNOWN ACCOUNT in the nav menu: both badges render as absent, the queue is still unread, and
    /// the denial is silent (no badge, no stale count) — the resolver refuses before either read runs.
    /// </summary>
    [Fact]
    public async Task NavMenu_UnknownAccount_RendersNeitherCounter()
    {
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        host.Queue.Enqueue(HumanRequest("nav-pending-unknown"));

        await using var menu = await RenderAsync<NavMenu>(host, "nav-never-activated");

        var html = await menu.HtmlAsync();
        Assert.Equal(0, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(0, menu.Field<int>("_knowledgeDocCount"));
        Assert.Equal(1, host.Queue.PendingHumanCount);
        Assert.DoesNotContain("document(s)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("badge-clarification", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// AFTER THE ACCOUNT IS DELETED MID-SESSION, the next badge refresh clears both counters even though
    /// the queue still holds a pending request and the graph still holds three documents — the denial is
    /// a refusal to read, never a fallback to a direct queue/graph read.
    /// </summary>
    [Fact]
    public async Task NavMenu_AccountDeleted_ThenDeniedOnChanged_ClearsBothCounters()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        host.Queue.Enqueue(HumanRequest("nav-pending-1"));

        await using var menu = await RenderAsync<NavMenu>(host, ViewerId);
        Assert.Equal(1, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(3, menu.Field<int>("_knowledgeDocCount"));

        await _auth.DeleteUserAsync(ViewerId);

        // The real queue event drives the refresh; the dispatcher barrier completes it.
        host.Queue.Enqueue(HumanRequest("nav-pending-2"));
        await menu.RequestRenderAsync();

        Assert.Equal(0, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(0, menu.Field<int>("_knowledgeDocCount"));
        Assert.Equal(2, host.Queue.PendingHumanCount);
    }

    /// <summary>
    /// AN AUTHORIZED ONCHANGED REFRESH REPUBLISHES BOTH COUNTERS: a real queue event after the initial
    /// load re-reads both authorized facades and shows the CURRENT counts, not the ones cached at
    /// construction.
    /// </summary>
    [Fact]
    public async Task NavMenu_AuthorizedOnChanged_RefreshesBothCountersFromTheFacades()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var graph = await SeedGraphAsync();
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, graph);
        host.Queue.Enqueue(HumanRequest("nav-before"));

        await using var menu = await RenderAsync<NavMenu>(host, ViewerId);
        Assert.Equal(1, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(3, menu.Field<int>("_knowledgeDocCount"));

        // The world changes while the page is open: another pending request arrives and a document is
        // added to the graph. The OnChanged event must re-read BOTH counters. The document is created
        // BEFORE the event fires so the queued refresh observes it deterministically.
        await graph.CreateDocumentAsync(
            "delta-to-nav", "Delta Nav", DocumentType.Feature, "delta body",
            topic: "navigation", subtopic: null, author: "dave", tags: ["nav-tag"], ct: Ct);
        host.Queue.Enqueue(HumanRequest("nav-after"));
        // The OnChanged handler dispatches the refresh onto the renderer; two dispatcher barriers drain
        // the enqueued refresh (the first lets the handler's InvokeAsync complete, the second observes
        // the fields it wrote).
        await menu.RequestRenderAsync();

        // The queue event drives a full RefreshCountsAsync, which re-reads BOTH counters.
        Assert.Equal(2, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(4, menu.Field<int>("_knowledgeDocCount"));
        var html = await menu.HtmlAsync();
        Assert.Contains("4 document(s)", html, StringComparison.Ordinal);
        Assert.Contains("2 pending clarification(s)", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A DENIED ONCHANGED REFRESH CLEARS BOTH COUNTERS. Both counters are populated through the
    /// authorized facades first (the non-vacuity control), then the account is disabled and the REAL
    /// queue event fires the component's own <c>OnChanged</c> handler: both counters return to 0 together
    /// and no badge is rendered — with no fallback to reading the queue directly (which still holds the
    /// pending request).
    /// </summary>
    [Fact]
    public async Task NavMenu_DeniedOnChangedRefresh_ClearsBothCounters_AndNeverReadsTheQueueDirectly()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        host.Queue.Enqueue(HumanRequest("nav-pending-1"));

        await using var menu = await RenderAsync<NavMenu>(host, ViewerId);

        Assert.Equal(1, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(3, menu.Field<int>("_knowledgeDocCount"));
        Assert.Contains("3 document(s)", await menu.HtmlAsync(), StringComparison.Ordinal);

        // THE ACCOUNT IS DISABLED, THEN THE REAL EVENT SOURCE FIRES.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        host.Queue.Enqueue(HumanRequest("nav-pending-2"));

        // The handler dispatches its refresh onto the renderer; drain the dispatcher so it completes.
        await menu.RequestRenderAsync();

        Assert.Equal(0, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(0, menu.Field<int>("_knowledgeDocCount"));

        var after = await menu.HtmlAsync();
        Assert.DoesNotContain("badge-clarification", after, StringComparison.Ordinal);
        Assert.DoesNotContain("document(s)", after, StringComparison.Ordinal);

        // The queue STILL HOLDS the pending requests: only the facade's denied answer controls this
        // component, so there was no direct-queue fallback.
        Assert.Equal(2, host.Queue.PendingHumanCount);
    }

    /// <summary>
    /// A STALE AUTHORIZED RESULT CANNOT REPOPULATE CLEARED COUNTERS. An authorized refresh is parked at
    /// the REAL count await (inside the registered facade); the account is then disabled and a NEWER
    /// denied refresh clears both counters; the parked older result is released afterwards and the
    /// generation guard refuses to republish it.
    /// </summary>
    [Fact]
    public async Task NavMenu_HeldOlderAuthorizedRefresh_AfterANewerDenial_CannotRepopulateTheCounters()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());

        await using var menu = await RenderAsync<NavMenu>(host, ViewerId);

        // Both counters are populated by the authorized load first (non-vacuity).
        Assert.Equal(3, menu.Field<int>("_knowledgeDocCount"));

        // The older refresh parks INSIDE the facade call, after the production count was computed.
        var gate = host.OperationalFacade.ArmCount();
        var older = menu.InvokePrivateAsync("RefreshCountsAsync");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        // A NEWER denied refresh completes first and clears both counters together.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await menu.InvokePrivateAsync("RefreshCountsAsync");
        await menu.RequestRenderAsync();
        Assert.Equal(0, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(0, menu.Field<int>("_knowledgeDocCount"));

        // Release the parked older authorized result: it must NOT republish its stale numbers.
        gate.Release();
        await older;
        await menu.RequestRenderAsync();

        Assert.Equal(0, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(0, menu.Field<int>("_knowledgeDocCount"));
        Assert.DoesNotContain("document(s)", await menu.HtmlAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// DISPOSE UNSUBSCRIBES AND LATE CALLBACKS WRITE NOTHING: after disposal the component's own handler
    /// runs no refresh (the counters keep their last authorized values and no new facade read happens),
    /// and a late in-flight refresh that completes after disposal publishes nothing.
    /// </summary>
    [Fact]
    public async Task NavMenu_Dispose_UnsubscribesAndLateCallbacksPublishNothing()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());

        await using var menu = await RenderAsync<NavMenu>(host, ViewerId);

        Assert.Equal(3, menu.Field<int>("_knowledgeDocCount"));

        // A LATE refresh is parked inside the count read BEFORE disposal.
        var gate = host.OperationalFacade.ArmCount();
        var late = menu.InvokePrivateAsync("RefreshCountsAsync");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(late.IsCompleted);

        await menu.InvokePublicAsync("Dispose");
        Assert.True(menu.Field<bool>("_disposed"));

        // The event source no longer holds the handler: a later event schedules no refresh at all,
        // observed as ZERO additional facade reads (not merely "the value did not change").
        var readsBefore = host.OperationalFacade.CountCalls;
        host.Queue.Enqueue(HumanRequest("nav-after-dispose"));
        await menu.RequestRenderAsync();
        await menu.RequestRenderAsync();
        Assert.Equal(readsBefore, host.OperationalFacade.CountCalls);
        Assert.True(menu.Field<bool>("_disposed"));
        Assert.Equal(3, menu.Field<int>("_knowledgeDocCount"));

        // The parked late refresh completes AFTER disposal: it publishes nothing.
        gate.Release();
        await late;
        await menu.RequestRenderAsync();
        Assert.Equal(3, menu.Field<int>("_knowledgeDocCount"));
    }

    /// <summary>
    /// OPEN MODE IS UNCHANGED: with NO cascade at all (exactly what an open-mode circuit delivers) and a
    /// mode-resolution of the legacy local operator, both counters load and no denial appears — the
    /// single-operator installation behaves exactly as before.
    /// </summary>
    [Fact]
    public async Task NavMenu_OpenMode_WithNoCascadeAtAll_LoadsBothCounters()
    {
        await using var host = NewHost(HiveAuthMode.Open, await SeedGraphAsync());
        host.Queue.Enqueue(HumanRequest("nav-open-1"));

        // gitHubId: null => the cascade is genuinely ABSENT from the tree.
        await using var menu = await RenderAsync<NavMenu>(host, gitHubId: null);

        Assert.Equal(1, menu.Field<int>("_pendingClarifications"));
        Assert.Equal(3, menu.Field<int>("_knowledgeDocCount"));

        var html = await menu.HtmlAsync();
        Assert.Contains("3 document(s)", html, StringComparison.Ordinal);
        Assert.Contains("1 pending clarification(s)", html, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4. OPEN MODE — the migrated pages activate with NO cascade at all
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE MIGRATED PAGES ACTIVATE IN OPEN MODE: both knowledge pages render with NO cascading
    /// authentication state and still present their authorized content, because the resolver answers the
    /// local operator for the null identity the circuit forwards.
    /// </summary>
    [Fact]
    public async Task KnowledgePages_OpenMode_WithNoCascadeAtAll_RenderTheirContent()
    {
        await using var host = NewHost(HiveAuthMode.Open, await SeedGraphAsync());

        await using var list = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, gitHubId: null);
        var listHtml = await list.HtmlAsync();
        Assert.Contains(AlphaTitle, listHtml, StringComparison.Ordinal);
        Assert.Contains(BetaTitle, listHtml, StringComparison.Ordinal);
        Assert.False(list.Field<bool>("_denied"));

        await using var detail = await RenderAsync<CopilotHive.Components.Pages.KnowledgeDetail>(
            host, gitHubId: null, new Dictionary<string, object?> { ["DocumentId"] = AlphaId });
        var detailHtml = await detail.HtmlAsync();
        Assert.Contains("ALPHA-BODY-MARKER", detailHtml, StringComparison.Ordinal);
        Assert.Contains("Outgoing Links (1)", detailHtml, StringComparison.Ordinal);
        Assert.False(detail.Field<bool>("_denied"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Folder toggle / localStorage restore guards, and cascade updates
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A JS runtime whose <c>localStorage.setItem</c> / <c>getItem</c> can be HELD, so a test can
    /// observe the page while the interop is in flight.
    /// </summary>
    private sealed class GatedJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldSet { get; set; }
        internal bool HoldGet { get; set; }
        internal string? StoredJson { get; set; }
        internal List<string> SetValues { get; } = [];
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "localStorage.setItem")
            {
                SetValues.Add((string)args![1]!);
                if (HoldSet) { Entered.TrySetResult(); await _release.Task; }
                return default!;
            }

            if (identifier == "localStorage.getItem")
            {
                if (HoldGet) { Entered.TrySetResult(); await _release.Task; }
                return (TValue)(object?)StoredJson!;
            }

            return default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    /// <summary>
    /// THE FOLDER TOGGLE AUTHORIZES BEFORE IT MUTATES OR PERSISTS: the account is disabled, then the
    /// toggle runs. The folder state is NOT mutated and <c>localStorage.setItem</c> is NEVER called; the
    /// page is already in its safe denial state with everything cleared.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_DeniedFolderToggle_NeitherMutatesFoldersNorPersists()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var js = new GatedJsRuntime();
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(), js: js);
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokePrivateAsync("ToggleFolderAsync", "architecture");
        await page.RequestRenderAsync();

        Assert.Empty(js.SetValues);
        Assert.Empty(page.Field<HashSet<string>>("_collapsedFolders"));
        Assert.True(page.Field<bool>("_denied"));
        var html = await page.HtmlAsync();
        Assert.DoesNotContain(AlphaTitle, html, StringComparison.Ordinal);
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
    }

    /// <summary>
    /// AN AUTHORIZED TOGGLE PERSISTS: the control proving the guard refuses only the denied toggle.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_AuthorizedFolderToggle_MutatesAndPersists()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var js = new GatedJsRuntime();
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(), js: js);
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        await page.InvokePrivateAsync("ToggleFolderAsync", "architecture");

        Assert.Contains("architecture", page.Field<HashSet<string>>("_collapsedFolders"), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("architecture", Assert.Single(js.SetValues), StringComparison.Ordinal);
    }

    /// <summary>
    /// A HELD setItem CANNOT EXPOSE STALE CONTENT: an authorized toggle is parked inside
    /// <c>localStorage.setItem</c>; the account is disabled and a newer refresh clears the page; the held
    /// interop is released and its (older) generation publishes/re-renders nothing.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_HeldSetItem_AfterANewerDenial_DoesNotRestoreContent()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var js = new GatedJsRuntime { HoldSet = true };
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(), js: js);
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        var toggle = page.FolderClickAsync("architecture");
        await js.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(toggle.IsCompleted);
        Assert.DoesNotContain(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.Contains("architecture", page.Field<HashSet<string>>("_collapsedFolders"));

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokePrivateAsync("RefreshAsync");
        await page.RequestRenderAsync();
        Assert.Contains(ForbiddenMessage, await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Field<HashSet<string>>("_collapsedFolders"));

        js.Release();
        await toggle;
        await page.RequestRenderAsync();

        Assert.True(page.Field<bool>("_denied"));
        Assert.DoesNotContain(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Field<List<KnowledgeDocumentSummary>>("_filtered"));
    }

    /// <summary>
    /// A DELAYED localStorage RESTORE WRITES NOTHING AFTER A DENIAL: the first-render restore is parked
    /// inside <c>getItem</c>; the account is disabled and a refresh clears the page; when the restore
    /// completes it must not repopulate the cleared folder state.
    /// </summary>
    [Fact]
    public async Task KnowledgePage_DelayedLocalStorageRestore_AfterADenial_WritesNothing()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var js = new GatedJsRuntime { HoldGet = true, StoredJson = """["architecture"]""" };
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(), js: js);
        // The static renderer does not run OnAfterRenderAsync's JS on its own thread timeline, so the
        // restore is driven explicitly through the page's own method.
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);

        var restore = page.InvokePrivateAsync("OnAfterRenderAsync", true);
        await js.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokePrivateAsync("RefreshAsync");
        Assert.True(page.Field<bool>("_denied"));

        js.Release();
        await restore;

        Assert.Empty(page.Field<HashSet<string>>("_collapsedFolders"));
        Assert.True(page.Field<bool>("_denied"));
    }

    /// <summary>
    /// A RETAINED Knowledge / NavMenu INSTANCE OBSERVES A CASCADE UPDATE: the parent re-cascades a
    /// signed-out state on the SAME child instance. <c>OnInitializedAsync</c> would not re-run, so the
    /// refresh must come from <c>OnParametersSetAsync</c>: the circuit is invalidated, the read is
    /// denied, and everything rendered is cleared. The queue handler is subscribed exactly once.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetainedInstance_CascadeSignOut_IsObservedAndClearsRenderedState(bool nav)
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        host.Queue.Enqueue(HumanRequest("retained-1"));

        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        object? component = null;
        RetainedCascadeHost? cascade = null;
        Func<string>? html = null;
        await using var cleanup = new AsyncCleanup(async () => { await renderer.DisposeAsync(); scope.Dispose(); });

        RenderFragment child = b =>
        {
            if (nav) b.OpenComponent<NavMenu>(0); else b.OpenComponent<CopilotHive.Components.Pages.Knowledge>(0);
            b.AddComponentReferenceCapture(1, c => component = c);
            b.CloseComponent();
        };
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<RetainedCascadeHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Initial"] = new AuthenticationState(Principal(ViewerId)),
                    ["Content"] = child,
                    ["Capture"] = (Action<RetainedCascadeHost>)(h => cascade = h),
                }));
            html = root.ToHtmlString;
        });
        var instance = component;
        Assert.NotNull(instance);

        string Marker() => nav ? "document(s)" : AlphaTitle;
        Assert.Contains(Marker(), await renderer.Dispatcher.InvokeAsync(html!), StringComparison.Ordinal);
        if (nav)
        {
            // Subscribed once: one enqueue refreshes once (initial load did not double-subscribe).
            var field = instance!.GetType().GetField("_refreshGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var before = (int)field.GetValue(instance)!;
            await renderer.Dispatcher.InvokeAsync(() => host.Queue.Enqueue(HumanRequest("retained-2")));
            await renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
            Assert.Equal(before + 1, (int)field.GetValue(instance)!);
        }

        // THE CASCADE CHANGES TO SIGNED-OUT ON THE RETAINED INSTANCE.
        await renderer.Dispatcher.InvokeAsync(() =>
            cascade!.Change(new AuthenticationState(FacadeAuthorizationHarness.Anonymous())));
        await renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
        await renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        Assert.Same(instance, component);
        var after = await renderer.Dispatcher.InvokeAsync(html!);
        Assert.DoesNotContain(Marker(), after, StringComparison.Ordinal);
        if (nav)
        {
            Assert.DoesNotContain("badge-clarification", after, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        }
    }

    private sealed class AsyncCleanup(Func<Task> action) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await action();
    }

    /// <summary>A parent that re-cascades a NEW authentication state to the SAME retained child.</summary>
    public sealed class RetainedCascadeHost : ComponentBase
    {
        [Parameter] public AuthenticationState Initial { get; set; } = null!;
        [Parameter] public RenderFragment Content { get; set; } = null!;
        [Parameter] public Action<RetainedCascadeHost> Capture { get; set; } = null!;
        private Task<AuthenticationState> _state = null!;
        protected override void OnInitialized() { _state = Task.FromResult(Initial); Capture(this); }
        internal void Change(AuthenticationState state) { _state = Task.FromResult(state); StateHasChanged(); }
        internal void ReRenderSameValue() => StateHasChanged();
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", _state);
            b.AddAttribute(2, "ChildContent", Content);
            b.CloseComponent();
        }
    }

    [Theory]
    [InlineData(false, "same")]
    [InlineData(false, "signed-out")]
    [InlineData(false, "replacement")]
    [InlineData(true, "same")]
    [InlineData(true, "signed-out")]
    [InlineData(true, "replacement")]
    public async Task RetainedHost_FrameworkCascadeUpdates_ClearOrRefreshSameInstances(bool nav, string transition)
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync("replacement-viewer", UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync());
        host.Queue.Enqueue(HumanRequest("framework-before"));
        using var scope = host.Provider.CreateScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        object? component = null;
        RetainedCascadeHost? parent = null;
        Func<string>? html = null;
        RenderFragment content = b =>
        {
            b.OpenComponent(0, nav ? typeof(NavMenu) : typeof(CopilotHive.Components.Pages.Knowledge));
            b.AddComponentReferenceCapture(1, c => component = c);
            b.CloseComponent();
        };
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<RetainedCascadeHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Initial"] = new AuthenticationState(Principal(ViewerId)), ["Content"] = content,
                    ["Capture"] = (Action<RetainedCascadeHost>)(h => parent = h),
                }));
            html = root.ToHtmlString;
        });
        var original = Assert.IsAssignableFrom<ComponentBase>(component);
        await using var view = new Rendered(renderer, scope, html!, original, host.ReleasePending);
        Assert.Contains("3 document(s)", await view.HtmlAsync(), StringComparison.Ordinal);
        if (nav)
        {
            Assert.Contains("1 pending clarification(s)", await view.HtmlAsync(), StringComparison.Ordinal);
            Assert.Equal(1, view.Field<int>("_pendingClarifications"));
            Assert.Equal(3, view.Field<int>("_knowledgeDocCount"));
        }
        else
        {
            Assert.Contains(AlphaTitle, await view.HtmlAsync(), StringComparison.Ordinal);
            Assert.NotEmpty(view.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs"));
            Assert.NotEmpty(view.Field<List<KnowledgeDocumentSummary>>("_filtered"));
            await view.FolderClickAsync("architecture");
            Assert.Contains("architecture", view.Field<HashSet<string>>("_collapsedFolders"));
            Assert.Contains(GammaTitle, await view.HtmlAsync(), StringComparison.Ordinal);
        }

        // Every update is delivered by the real CascadingValue parent render, not by reflective
        // lifecycle calls. Equivalent identity on a NEW cascade task is the same-identity control.
        // Observe actual facade reads, not merely unchanged fields, to prove these updates refresh.
        for (var i = 0; i < 3; i++)
        {
            var reads = nav ? host.OperationalFacade.CountCalls : host.OperationalFacade.ListCalls;
            await renderer.Dispatcher.InvokeAsync(() => parent!.Change(new AuthenticationState(Principal(ViewerId))));
            await renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
            Assert.Same(original, component);
            Assert.Equal(reads + 1, nav ? host.OperationalFacade.CountCalls : host.OperationalFacade.ListCalls);
        }
        // An identical task/value re-render is a distinct passing control: the parent may decide
        // not to notify the cascade, but must never clear authorized retained state.
        await renderer.Dispatcher.InvokeAsync(() => parent!.ReRenderSameValue());
        Assert.Same(original, component);
        Assert.Contains("3 document(s)", await view.HtmlAsync(), StringComparison.Ordinal);
        if (nav)
        {
            var calls = host.OperationalFacade.CountCalls;
            await renderer.Dispatcher.InvokeAsync(() => host.Queue.Enqueue(HumanRequest("framework-after")));
            await renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
            Assert.Equal(calls + 1, host.OperationalFacade.CountCalls);
            Assert.Equal(2, view.Field<int>("_pendingClarifications"));
            Assert.Contains("2 pending clarification(s)", await view.HtmlAsync(), StringComparison.Ordinal);
            var subscribers = (Action?)typeof(ClarificationQueueService).GetField("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host.Queue);
            Assert.Single(subscribers!.GetInvocationList(), d => ReferenceEquals(d.Target, original));
        }
        var user = transition switch
        {
            "same" => Principal(ViewerId),
            "signed-out" => FacadeAuthorizationHarness.Anonymous(),
            "replacement" => Principal("replacement-viewer"),
            _ => throw new InvalidOperationException(transition),
        };
        await renderer.Dispatcher.InvokeAsync(() => parent!.Change(new AuthenticationState(user)));
        await renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
        Assert.Same(original, component);
        var after = await view.HtmlAsync();
        Assert.Empty(((TestNavigationManager)host.Provider.GetRequiredService<NavigationManager>()).Navigations);
        Assert.DoesNotContain("login", after, StringComparison.OrdinalIgnoreCase);
        if (transition == "same")
        {
            Assert.Contains("3 document(s)", after, StringComparison.Ordinal);
            if (nav) Assert.Equal(2, view.Field<int>("_pendingClarifications"));
            else
            {
                Assert.False(view.Field<bool>("_denied"));
                Assert.NotEmpty(view.Field<List<KnowledgeDocumentSummary>>("_filtered"));
                Assert.Contains("architecture", view.Field<HashSet<string>>("_collapsedFolders"));
                Assert.Contains(GammaTitle, after, StringComparison.Ordinal);
            }
        }
        else if (nav)
        {
            Assert.Equal(0, view.Field<int>("_pendingClarifications"));
            Assert.Equal(0, view.Field<int>("_knowledgeDocCount"));
            Assert.DoesNotContain("document(s)", after, StringComparison.Ordinal);
            Assert.DoesNotContain("badge-clarification", after, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(view.Field<bool>("_denied"));
            Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
            Assert.Empty(view.Field<IReadOnlyList<KnowledgeDocumentSummary>>("_allDocs"));
            Assert.Empty(view.Field<List<KnowledgeDocumentSummary>>("_filtered"));
            Assert.Empty(view.Field<HashSet<string>>("_collapsedFolders"));
            foreach (var marker in new[] { AlphaTitle, BetaTitle, GammaTitle, AlphaTag, "document(s)" })
                Assert.DoesNotContain(marker, after, StringComparison.Ordinal);
        }
    }

    // A strict decorator records REAL GoalFacade outcomes and can hold a successfully computed
    // goals result at the SECOND publication window (not the document-read window).
    private sealed class GoalsProbe
    {
        private ReadGate? _armed;
        private readonly List<ReadGate> _gates = [];
        internal List<FacadeResult<IReadOnlyList<Goal>>> Results { get; } = [];
        internal ReadGate Arm() { var gate = new ReadGate(); _gates.Add(gate); _armed = gate; return gate; }
        internal void ReleaseAll() { foreach (var gate in _gates) gate.Release(); }
        internal IGoalFacade Wrap(IGoalFacade real)
        {
            var mock = new Mock<IGoalFacade>(MockBehavior.Strict);
            mock.Setup(f => f.GetGoalsAsync(It.IsAny<ClaimsPrincipal?>(), It.IsAny<CancellationToken>()))
                .Returns(async (ClaimsPrincipal? user, CancellationToken ct) =>
                {
                    var gate = Interlocked.Exchange(ref _armed, null);
                    var result = await real.GetGoalsAsync(user, ct);
                    Results.Add(result);
                    if (gate is not null) await gate.ParkAsync();
                    return result;
                });
            return mock.Object;
        }
    }

    private static void SeedRelatedGoal(Host host)
    {
        host.Graph!.AddLink(GammaId, new DocumentLink(AlphaId, LinkType.Related, "INCOMING-DESCRIPTION"));
        host.GoalStore.AddGoal(new Goal
        {
            Id = "related-goal-marker", Description = "RELATED-GOAL-DESCRIPTION-MARKER",
            Status = GoalStatus.Completed, Documents = [AlphaId],
        });
    }

    private static async Task AssertDetailPopulatedAsync(Rendered page, Host host)
    {
        var html = await page.HtmlAsync();
        Assert.Contains(AlphaBody, html, StringComparison.Ordinal);
        Assert.Contains(AlphaTag, html, StringComparison.Ordinal);
        Assert.Contains(BetaTitle, html, StringComparison.Ordinal);
        Assert.Contains(GammaTitle, html, StringComparison.Ordinal);
        Assert.Contains("related-goal-marker", html, StringComparison.Ordinal);
        Assert.NotNull(page.Field<KnowledgeDocumentDetail?>("_doc"));
        Assert.NotEmpty(page.Field<IReadOnlyList<KnowledgeOutgoingLink>>("_outgoingLinks"));
        Assert.NotEmpty(page.Field<IReadOnlyList<KnowledgeIncomingLink>>("_incomingLinks"));
        Assert.NotEmpty(page.Field<IReadOnlyList<Goal>>("_relatedGoals"));
        Assert.Contains(AlphaTitle, host.Provider.GetRequiredService<PageHeaderState>().Title, StringComparison.Ordinal);
    }

    private static async Task AssertDetailDeniedAsync(Rendered page, Host host)
    {
        var html = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        foreach (var marker in new[] { AlphaBody, AlphaTag, AlphaTitle, BetaTitle, GammaTitle,
            "related-goal-marker", "RELATED-GOAL-DESCRIPTION-MARKER", "Outgoing Links", "Incoming Links", "Related Goals" })
            Assert.DoesNotContain(marker, html, StringComparison.Ordinal);
        Assert.DoesNotContain("login", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("was not found", html, StringComparison.Ordinal);
        Assert.True(page.Field<bool>("_denied"));
        Assert.False(page.Field<bool>("_notFound"));
        Assert.Null(page.Field<KnowledgeDocumentDetail?>("_doc"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeOutgoingLink>>("_outgoingLinks"));
        Assert.Empty(page.Field<IReadOnlyList<KnowledgeIncomingLink>>("_incomingLinks"));
        Assert.Empty(page.Field<IReadOnlyList<Goal>>("_relatedGoals"));
        Assert.Equal($"📚 {AlphaId}", host.Provider.GetRequiredService<PageHeaderState>().Title);
        Assert.Empty(((TestNavigationManager)host.Provider.GetRequiredService<NavigationManager>()).Navigations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnowledgeDetail_RealGoalsForbiddenBetweenReads_ClearsPopulatedSurface(bool delete)
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var probe = new GoalsProbe();
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(), goalsProbe: probe);
        SeedRelatedGoal(host);
        await using var page = await RenderAsync<KnowledgeDetail>(host, ViewerId,
            new Dictionary<string, object?> { ["DocumentId"] = AlphaId });
        var instance = page.Component;
        await AssertDetailPopulatedAsync(page, host);
        host.GoalStore.AllCalls.Clear();
        probe.Results.Clear();

        // The real OperationalFacade success is computed before this hold. Revocation then happens
        // before the page asks the REAL GoalFacade, whose fresh resolution must forbid the read.
        var gate = host.OperationalFacade.ArmDetail();
        var refresh = page.InvokePrivateAsync("OnParametersSetAsync");
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(refresh.IsCompleted);
            Assert.True(host.OperationalFacade.LastDetailResult!.Success);
            Assert.Equal(AlphaId, host.OperationalFacade.LastDetailResult.Value!.Document.Id);
            Assert.Empty(probe.Results); // GoalFacade has NOT run yet.
            if (delete) await _auth.DeleteUserAsync(ViewerId);
            else await _auth.SetEnabledAsync(ViewerId, false);
            gate.Release();
            await refresh;
            await page.RequestRenderAsync();
            var denied = Assert.Single(probe.Results);
            Assert.False(denied.Success);
            Assert.Equal(FacadeErrorKind.Forbidden, denied.Kind);
            Assert.Null(denied.Value);
            Assert.Equal(ForbiddenMessage, denied.Error);
            Assert.Empty(host.GoalStore.AllCalls); // no direct IGoalStore source/fallback after denial
            await AssertDetailDeniedAsync(page, host);
            Assert.Same(instance, page.Component);
        }
        finally { gate.Release(); await refresh.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)] // authorized held-success release control
    [InlineData(true)] // older goals success released AFTER newer denial
    public async Task KnowledgeDetail_HeldGoalsResult_PublicationOwnership(bool newerDenial)
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var probe = new GoalsProbe();
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(), goalsProbe: probe);
        SeedRelatedGoal(host);
        await using var page = await RenderAsync<KnowledgeDetail>(host, ViewerId,
            new Dictionary<string, object?> { ["DocumentId"] = AlphaId });
        await AssertDetailPopulatedAsync(page, host);
        probe.Results.Clear();
        host.GoalStore.AllCalls.Clear();
        var gate = probe.Arm(); // specifically AFTER GetGoalsAsync's REAL authorized result
        var older = page.InvokePrivateAsync("OnParametersSetAsync");
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(older.IsCompleted);
            var success = Assert.Single(probe.Results);
            Assert.True(success.Success);
            Assert.NotEmpty(success.Value!);
            Assert.Equal(["GetAllGoalsAsync"], host.GoalStore.AllCalls);
            Assert.NotNull(page.Field<KnowledgeDocumentDetail?>("_doc"));
            Assert.Empty(page.Field<IReadOnlyList<Goal>>("_relatedGoals"));
            if (newerDenial)
            {
                await _auth.SetEnabledAsync(ViewerId, false);
                await page.InvokePrivateAsync("OnParametersSetAsync");
                await page.RequestRenderAsync();
                await AssertDetailDeniedAsync(page, host);
            }
            gate.Release();
            await older;
            await page.RequestRenderAsync();
            if (newerDenial) await AssertDetailDeniedAsync(page, host);
            else await AssertDetailPopulatedAsync(page, host);
        }
        finally { gate.Release(); await older.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnowledgePage_RealFolderOnclick_RevokedBeforeClick_NeverMutatesOrPersists(bool delete)
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var js = new GatedJsRuntime();
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(), js: js);
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);
        Assert.Contains(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
        await page.FolderClickAsync("architecture");
        Assert.Contains("architecture", page.Field<HashSet<string>>("_collapsedFolders"));
        Assert.DoesNotContain(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.NotEmpty(page.Field<List<KnowledgeDocumentSummary>>("_filtered"));
        js.SetValues.Clear();
        js.HoldSet = true;
        if (delete) await _auth.DeleteUserAsync(ViewerId);
        else await _auth.SetEnabledAsync(ViewerId, false);
        var gate = host.OperationalFacade.ArmList();
        var generation = page.Field<int>("_refreshGeneration");
        var click = page.FolderClickAsync("architecture");
        try
        {
            // Correct code parks at authorization first. A pre-fix onclick expands the retained
            // folder and parks at JS first, exposing Alpha at ComponentBase's immediate event render.
            var first = await Task.WhenAny(gate.Entered.Task, js.Entered.Task).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Same(gate.Entered.Task, first);
            Assert.False(click.IsCompleted);
            Assert.Equal(generation + 1, page.Field<int>("_refreshGeneration"));
            Assert.Contains("architecture", page.Field<HashSet<string>>("_collapsedFolders"));
            Assert.Empty(js.SetValues);
            Assert.DoesNotContain(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
            gate.Release();
            await click;
            Assert.True(page.Field<bool>("_denied"));
            Assert.Empty(page.Field<HashSet<string>>("_collapsedFolders"));
            Assert.Empty(js.SetValues);
            Assert.False(js.Entered.Task.IsCompleted);
            Assert.Contains(ForbiddenMessage, await page.HtmlAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
        }
        finally { gate.Release(); js.Release(); await click.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
    }

    [Fact]
    public async Task KnowledgePage_RealFolderOnclick_AuthorizedHeldPersistence_Control()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var js = new GatedJsRuntime { HoldSet = true };
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(), js: js);
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);
        Assert.Contains(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
        var click = page.FolderClickAsync("architecture");
        try
        {
            await js.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(click.IsCompleted);
            Assert.Contains("architecture", page.Field<HashSet<string>>("_collapsedFolders"));
            Assert.Contains("architecture", Assert.Single(js.SetValues), StringComparison.Ordinal);
            Assert.False(page.Field<bool>("_denied"));
            Assert.DoesNotContain(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
            Assert.Contains(GammaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
            js.Release();
            await click;
            Assert.DoesNotContain(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
            await page.FolderClickAsync("architecture");
            Assert.Contains(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
            Assert.Empty(page.Field<HashSet<string>>("_collapsedFolders"));
            Assert.Equal("[]", js.SetValues.Last());
        }
        finally { js.Release(); await click.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
    }

    [Theory]
    [InlineData("allowed")]
    [InlineData("denied")]
    [InlineData("disposed")]
    public async Task KnowledgePage_HeldInitialRestore_RespectsCurrentOwnership(string outcome)
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var js = new GatedJsRuntime { HoldGet = true, StoredJson = """["architecture"]""" };
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, await SeedGraphAsync(), js: js);
        await using var page = await RenderAsync<CopilotHive.Components.Pages.Knowledge>(host, ViewerId);
        Assert.Contains(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
        // Static HtmlRenderer never invokes OnAfterRenderAsync automatically. Drive this exact
        // production lifecycle method on the retained component's dispatcher (no alternative restore).
        var restore = page.InvokePrivateAsync("OnAfterRenderAsync", true);
        try
        {
            await js.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(restore.IsCompleted);
            if (outcome == "denied")
            {
                await _auth.SetEnabledAsync(ViewerId, false);
                await page.InvokePrivateAsync("RefreshAsync");
                await page.RequestRenderAsync();
                Assert.True(page.Field<bool>("_denied"));
                Assert.DoesNotContain(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
            }
            else if (outcome == "disposed") await page.InvokePublicAsync("Dispose");
            js.Release();
            await restore;
            if (outcome == "allowed")
            {
                Assert.Contains("architecture", page.Field<HashSet<string>>("_collapsedFolders"));
                Assert.DoesNotContain(AlphaTitle, await page.HtmlAsync(), StringComparison.Ordinal);
                Assert.False(page.Field<bool>("_denied"));
            }
            else
            {
                Assert.Empty(page.Field<HashSet<string>>("_collapsedFolders"));
                Assert.Equal(outcome == "denied", page.Field<bool>("_denied"));
                if (outcome == "denied") Assert.Contains(ForbiddenMessage, await page.HtmlAsync(), StringComparison.Ordinal);
            }
        }
        finally { js.Release(); await restore.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    /// <summary>
    /// Scripted boundary-failure control: Forbidden must clear the whole detail surface, while a
    /// non-authorization failure preserves the authorized document and omits only related goals.
    /// Real directory-driven Forbidden behavior is covered separately by between-read regressions.
    /// </summary>
    private sealed class AlwaysDeniedGoalFacade(FacadeErrorKind kind = FacadeErrorKind.Forbidden) : IGoalFacade
    {
        private FacadeResult<T> Denied<T>() =>
            new(false, default,
                kind == FacadeErrorKind.Forbidden ? FacadeAuthorization.ForbiddenMessage : "goal store unavailable",
                kind);

        public Task<FacadeResult<IReadOnlyList<Goal>>> GetGoalsAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Denied<IReadOnlyList<Goal>>());
        public Task<FacadeResult<Goal>> GetGoalAsync(string id, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Denied<Goal>());
        public Task<FacadeResult<IReadOnlyList<Goal>>> SearchGoalsAsync(
            string query, string? status, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Denied<IReadOnlyList<Goal>>());
        public Task<FacadeResult<IReadOnlyList<IterationSummary>>> GetIterationsAsync(
            string goalId, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Denied<IReadOnlyList<IterationSummary>>());
        public Task<FacadeResult<IReadOnlyDictionary<string, GoalStatus>>> GetDependencyStatusesAsync(
            string goalId, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Denied<IReadOnlyDictionary<string, GoalStatus>>());
        public Task<FacadeResult<GoalDetailInfo>> GetLiveGoalDetailAsync(
            string goalId, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Denied<GoalDetailInfo>());
        public Task<FacadeResult<IReadOnlyList<ProgressEntry>>> GetProgressAsync(
            string goalId, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Denied<IReadOnlyList<ProgressEntry>>());
        public Task<FacadeResult<IReadOnlyList<LinkedIssueDto>>> GetLinkedIssuesAsync(
            string goalId, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Denied<IReadOnlyList<LinkedIssueDto>>());
        public Task<FacadeResult> DeleteGoalAsync(string id, ClaimsPrincipal? actingUser) =>
            Task.FromResult(new FacadeResult(false, FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden));
        public Task<FacadeResult<GoalDto>> UpdateGoalStatusAsync(string id, string status, ClaimsPrincipal? actingUser) =>
            Task.FromResult(Denied<GoalDto>());
        public Task<FacadeResult<Goal>> UpdateReviewStatusAsync(
            string id, string reviewStatus, ClaimsPrincipal? actingUser) =>
            Task.FromResult(Denied<Goal>());
        public Task<FacadeResult<ReviewResultDto>> RequestReviewAsync(
            string goalId, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Denied<ReviewResultDto>());
        public Task<FacadeResult<CancelledResult>> CancelGoalAsync(string id, ClaimsPrincipal? actingUser) =>
            Task.FromResult(Denied<CancelledResult>());
        public Task<FacadeResult<ExtendedResult>> ExtendIterationsAsync(
            string id, int additionalIterations, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Denied<ExtendedResult>());
        public Task<FacadeResult<GoalDto>> AttachReleaseAsync(string id, string releaseId, ClaimsPrincipal? actingUser) =>
            Task.FromResult(Denied<GoalDto>());
    }

    private static ClarificationRequest HumanRequest(string id) => new()
    {
        Id = id,
        GoalId = "nav-goal",
        WorkerRole = "tester",
        Question = "NAV-PENDING-QUESTION",
        Status = ClarificationStatus.AwaitingHuman,
        RequestedAt = new DateTime(2024, 7, 1, 14, 23, 45, DateTimeKind.Utc),
    };

    public void Dispose() => _auth.Dispose();

    /// <summary>A JS runtime that answers nothing (these components only use it for localStorage).</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);
    }

    /// <summary>A navigation manager that records nothing (these components only navigate on click).</summary>
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/knowledge");
        internal List<string> Navigations { get; } = [];

        protected override void NavigateToCore(string uri, NavigationOptions options) => Navigations.Add(uri);
    }

    /// <summary>A pass-through host used for the OPEN-MODE rows (no cascade in the tree at all).</summary>
    public sealed class ContentHost : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(
            Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
            builder.AddContent(0, ChildContent);
    }
}
