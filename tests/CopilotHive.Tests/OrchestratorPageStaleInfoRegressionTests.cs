using System.Reflection;
using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE STALE-PROJECTION REGRESSION for the ORCHESTRATOR PAGE, for the defect the iteration-1
/// review raised as [MAJOR] at <c>Orchestrator.razor:318-321</c>: a FORBIDDEN
/// <c>GetOrchestratorInfoAsync</c> left the previously authorized <c>_info</c> rendered
/// indefinitely, so the version, the server time and the model configuration survived the
/// disablement of the acting account.
/// <para>
/// THE CONTRACT, exercised BEHAVIOURALLY: the REAL page is rendered through the framework's own
/// <see cref="HtmlRenderer"/> over the REAL production facade, the REAL
/// <see cref="HivePrincipalResolver"/>/<see cref="HivePolicy"/> pair and the REAL
/// <see cref="UserService"/> directory — the pattern established by
/// <see cref="CopilotHive.Tests.Services.GoalIssuePagesStaleDataRegressionTests"/> and by this
/// author's own <see cref="OrchestratorPageAuthorizedBehaviorTests"/>.
/// </para>
/// <para>
/// <b>THE SEQUENCE IS THE ACCEPTANCE PROOF, AND IT MUST NOT BE AN INITIAL-RENDER-ONLY TEST:</b>
/// </para>
/// <list type="number">
///   <item>the account is an ENABLED Operator, and the page's <c>_info</c> is genuinely POPULATED
///   through an authorized production read — the version, the model configuration and the server
///   time are asserted to be present, both in the page's own state and in the rendered markup, so
///   the baseline is provably NON-EMPTY;</item>
///   <item>the account is DISABLED in the directory;</item>
///   <item>the SAME component INSTANCE is refreshed through the page's OWN production refresh path
///   (the dashboard state notification the page subscribes to) — never a fresh render;</item>
///   <item>the old <c>_info</c> values are gone from BOTH the rendered markup and the page's own
///   state.</item>
/// </list>
/// <para>
/// A second row pins the ALLOWED-refresh CONTROL: an enabled Operator's refresh still POPULATES
/// <c>_info</c>, so the clear fires only on a FORBIDDEN read and never degrades normal behavior.
/// A third row independently re-verifies the coder's AUDIT claim that the OTHER two
/// facade-populated projections (<c>_stats</c> and <c>_llmSessions</c>) already cleared on the very
/// same populated-then-denied sequence.
/// </para>
/// </summary>
public sealed class OrchestratorPageStaleInfoRegressionTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string OperatorId = "stale-info-operator";

    /// <summary>A distinctive model identifier rendered ONLY by the orchestrator-info projection.</summary>
    private const string StaleRoleModelMarker = "STALE-ROLE-MODEL-MARKER";

    /// <summary>A distinctive model identifier for a SECOND role, so the table really has content.</summary>
    private const string StaleTesterModelMarker = "STALE-TESTER-MODEL-MARKER";

    /// <summary>The shared session rendered by the session table before the denial.</summary>
    private const string SharedSessionModelMarker = "STALE-SHARED-SESSION-MARKER";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── The Brain behind the trusted dashboard service ───────────────────────

    /// <summary>
    /// A recording Brain: the dashboard service is the PRODUCTION (sealed) type, so the observable
    /// seam behind it is the Brain whose statistics and master session it owns.
    /// </summary>
    private sealed class RecordingBrain : IDistributedBrain
    {
        /// <summary>Statistics the dashboard projects, so the page renders its Brain section.</summary>
        internal BrainStats Stats { get; } = new()
        {
            Model = "recording-model",
            MessageCount = 4242,
            ContextTokens = 1_000,
            MaxContextTokens = 10_000,
            ContextUsagePercent = 10,
            IsConnected = true,
        };

        public BrainStats? GetStats() => Stats;

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success("recorded prompt"));

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult("recorded summary");

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("recorded answer"));

        public Task UpdateModelAsync(string model, int? maxContextTokens, ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;
    }

    // ── Host ────────────────────────────────────────────────────────────────

    /// <summary>The host over the PRODUCTION seam for one page render.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        internal Host(
            ServiceProvider provider,
            DashboardStateService dashboardState,
            DashboardNotifier notifier,
            LlmSessionRegistry registry)
        {
            _provider = provider;
            DashboardState = dashboardState;
            Notifier = notifier;
            Registry = registry;
        }

        internal DashboardStateService DashboardState { get; }

        /// <summary>The notifier the page's own refresh subscription is wired to.</summary>
        internal DashboardNotifier Notifier { get; }

        internal LlmSessionRegistry Registry { get; }

        /// <summary>The service provider the page is rendered from.</summary>
        internal ServiceProvider Provider => _provider;

        public async ValueTask DisposeAsync()
        {
            // The dashboard service owns a polling timer: it is disposed BEFORE the provider so no
            // callback can outlive the services it touches.
            DashboardState.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private Host NewHost(HiveAuthMode mode)
    {
        var brain = new RecordingBrain();
        var registry = new LlmSessionRegistry();
        var notifier = new DashboardNotifier();

        // A config with DISTINCTIVE worker models, so the orchestrator-info projection renders
        // content that is unique to it — the stale marker can then be observed in the markup and
        // its disappearance cannot be confused with any other field's clearing.
        var config = new HiveConfigFile
        {
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new WorkerConfig { Model = StaleRoleModelMarker },
                ["tester"] = new WorkerConfig { Model = StaleTesterModelMarker },
            },
        };

        var dashboardState = new DashboardStateService(
            new WorkerPool(),
            new GoalPipelineManager(),
            new DashboardLogSink(),
            new ProgressLog(),
            notifier: notifier,
            brain: brain,
            config: config);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(notifier);
        services.AddSingleton(dashboardState);
        services.AddSingleton(registry);
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<IGoalStore>(new InMemoryGoalStore());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
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

        // THE AUTHORIZED FACADE — the production type over the production resolver/policy, exactly
        // as Program.cs registers it.
        services.AddSingleton<IOperationalFacade>(sp => new OperationalFacade(
            sp.GetRequiredService<DashboardStateService>(),
            sp.GetRequiredService<LlmSessionRegistry>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            NullLogger<OperationalFacade>.Instance));

        // THE AUTHORIZED CLARIFICATION FACADE — the production type over the production queue, the
        // seeded goal store and the production resolver/policy, exactly as Program.cs registers it.
        // The page reads its persisted clarification history through this facade.
        services.AddSingleton<IClarificationFacade>(sp => new ClarificationFacade(
            new ClarificationQueueService(),
            sp.GetRequiredService<IGoalStore>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            NullLogger<ClarificationFacade>.Instance));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, dashboardState, notifier, registry);
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    /// <summary>One rendered page: its markup accessor, its scope and its renderer.</summary>
    private sealed class Rendered : IAsyncDisposable
    {
        private readonly HtmlRenderer _renderer;
        private readonly IServiceScope _scope;
        private readonly Func<string> _rootHtml;
        private readonly object _page;

        internal Rendered(HtmlRenderer renderer, IServiceScope scope, Func<string> rootHtml, object page)
        {
            _renderer = renderer;
            _scope = scope;
            _rootHtml = rootHtml;
            _page = page;
        }

        /// <summary>The CURRENT markup of the retained root.</summary>
        internal Task<string> HtmlAsync() => _renderer.Dispatcher.InvokeAsync(_rootHtml);

        /// <summary>
        /// Awaits the renderer's work queue so every work item enqueued BEFORE this call has
        /// COMPLETED. The page's own refresh work item is enqueued before this barrier, so the
        /// barrier observes the finished refresh — no sleep, no polling, no timing assumption.
        /// </summary>
        internal Task DrainAsync() => _renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        /// <summary>Reads a private field of the rendered page (its own loaded state).</summary>
        internal object? Field(string name) =>
            _page.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_page);

        /// <summary>Reads a private field of the rendered page as a typed value.</summary>
        internal T Field<T>(string name) => Assert.IsAssignableFrom<T>(Field(name));

        /// <summary>The identity of the rendered page INSTANCE (the same one is refreshed below).</summary>
        internal object Page => _page;

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            _scope.Dispose();
        }
    }

    /// <summary>
    /// Renders the REAL Orchestrator page for one circuit identity, cascading the authentication
    /// state exactly as the framework does when authentication is enabled.
    /// </summary>
    private async Task<Rendered> RenderAsync(Host host, string gitHubId)
    {
        var state = Task.FromResult(new AuthenticationState(Principal(gitHubId)));

        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        Func<string>? rootHtml = null;
        object? page = null;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var values = new Dictionary<string, object?>
            {
                ["Value"] = state,
                ["ChildContent"] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<CopilotHive.Components.Pages.Orchestrator>(0);
                    builder.AddComponentReferenceCapture(1, captured => page = captured);
                    builder.CloseComponent();
                }),
            };

            var root = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                ParameterView.FromDictionary(values));
            rootHtml = root.ToHtmlString;
        });

        Assert.NotNull(rootHtml);
        Assert.NotNull(page);
        return new Rendered(renderer, scope, rootHtml!, page!);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. THE REVIEWER'S MANDATED REGRESSION: populated → denied → cleared
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE STALE-INFO REGRESSION: the SAME component instance first shows a GENUINELY POPULATED
    /// <c>_info</c> (authorized production read), and then — after the account is disabled — shows
    /// NO trace of it any more.
    /// <para>
    /// The baseline is proven NON-EMPTY before the denial (the version is a real assembly version,
    /// the model configuration carries the distinctive role models, the server time is a real
    /// timestamp), so the "it is gone afterwards" half is not vacuous. The refresh is the page's OWN
    /// production path (the dashboard state notification the page subscribes to) on the SAME page
    /// instance — a fresh render would prove nothing about the leak.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheDeniedRefresh_ClearsThePopulatedOrchestratorInfo_FromTheMarkupAndThePageState()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled);

        // ── STEP 1: the account is ENABLED and the page renders a GENUINELY POPULATED _info. ──
        await using var rendered = await RenderAsync(host, OperatorId);
        var pageInstance = rendered.Page;
        var before = rendered.Field<OrchestratorInfo>("_info");
        var beforeHtml = await rendered.HtmlAsync();

        // THE BASELINE IS NON-EMPTY: a real version, real model configuration, a real server time.
        Assert.False(string.IsNullOrWhiteSpace(before.Version), "the populated _info carried no version");
        Assert.False(string.IsNullOrWhiteSpace(before.SharpCoderVersion), "the populated _info carried no SharpCoder version");
        Assert.NotEqual(default, before.ServerTime);
        Assert.Equal(StaleRoleModelMarker, before.RoleModels["coder"]);
        Assert.Equal(StaleTesterModelMarker, before.RoleModels["tester"]);

        // ...and the SAME content really reached the rendered markup. (The version is HTML-ENCODED
        // in the markup by the framework's own encoder, so it is compared in its encoded form.)
        Assert.Contains(StaleRoleModelMarker, beforeHtml, StringComparison.Ordinal);
        Assert.Contains(StaleTesterModelMarker, beforeHtml, StringComparison.Ordinal);
        Assert.Contains(Encode(before.Version), beforeHtml, StringComparison.Ordinal);
        Assert.Contains(before.SharpCoderVersion!, beforeHtml, StringComparison.Ordinal);
        Assert.Contains(before.ServerTime.ToString("HH:mm:ss"), beforeHtml, StringComparison.Ordinal);

        // ── STEP 2: the account is DISABLED in the directory. ──
        await _auth.SetEnabledAsync(OperatorId, enabled: false);

        // THE REFRESH WAS ACTUALLY REFUSED (not merely empty): the facade's own answer for the
        // disabled account is Forbidden.
        var facade = host.Provider.GetRequiredService<IOperationalFacade>();
        var refused = await facade.GetOrchestratorInfoAsync(Principal(OperatorId), Ct);
        Assert.False(refused.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, refused.Kind);

        // ── STEP 3: refresh the SAME component instance through the page's OWN refresh path. ──
        host.Notifier.NotifyStateChanged();
        await rendered.DrainAsync();

        // THE SAME INSTANCE was refreshed — the leak was on a live page, not on a re-render.
        Assert.Same(pageInstance, rendered.Page);

        // ── STEP 4: the old _info values are GONE from the page's own state. ──
        var after = rendered.Field<OrchestratorInfo>("_info");

        Assert.NotSame(before, after);
        Assert.Equal(string.Empty, after.Version);
        Assert.Null(after.SharpCoderVersion);
        Assert.Equal(default, after.ServerTime);
        Assert.Equal(default, after.Uptime);
        Assert.Empty(after.RoleModels);
        Assert.Equal(string.Empty, after.BrainModel);
        Assert.Equal(string.Empty, after.ComposerModel);
        Assert.Null(after.CompactionModel);

        // ── STEP 4 (continued): ...and GONE from the rendered markup. ──
        var afterHtml = await rendered.HtmlAsync();

        Assert.DoesNotContain(StaleRoleModelMarker, afterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(StaleTesterModelMarker, afterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(Encode(before.Version), afterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(before.SharpCoderVersion!, afterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(before.ServerTime.ToString("HH:mm:ss"), afterHtml, StringComparison.Ordinal);

        // The model-configuration table itself is still rendered, but with EMPTY model cells.
        Assert.Contains("Model Configuration", afterHtml, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. THE ALLOWED-REFRESH CONTROL: the clear must not degrade normal behavior
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ALLOWED-REFRESH CONTROL: an ENABLED Operator's refresh still POPULATES <c>_info</c>
    /// with the genuine production projection. This is what proves the clear fires ONLY on a
    /// FORBIDDEN read — a fix that cleared unconditionally would empty the page for an authorized
    /// operator, and this row would catch it.
    /// </summary>
    [Fact]
    public async Task TheAllowedRefresh_StillPopulatesTheOrchestratorInfo()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled);

        await using var rendered = await RenderAsync(host, OperatorId);

        var before = rendered.Field<OrchestratorInfo>("_info");
        Assert.Equal(StaleRoleModelMarker, before.RoleModels["coder"]);

        // THE REFRESH WAS ALLOWED: the facade answers Success for the enabled account.
        var facade = host.Provider.GetRequiredService<IOperationalFacade>();
        var allowed = await facade.GetOrchestratorInfoAsync(Principal(OperatorId), Ct);
        Assert.True(allowed.Success, allowed.Error);

        // The page's OWN refresh path runs on the SAME instance.
        host.Notifier.NotifyStateChanged();
        await rendered.DrainAsync();

        var after = rendered.Field<OrchestratorInfo>("_info");

        // THE PROJECTION IS STILL POPULATED — the clear did NOT fire on an allowed read.
        Assert.NotSame(before, after);
        Assert.False(string.IsNullOrWhiteSpace(after.Version));
        Assert.NotEqual(default, after.ServerTime);
        Assert.Equal(StaleRoleModelMarker, after.RoleModels["coder"]);
        Assert.Equal(StaleTesterModelMarker, after.RoleModels["tester"]);

        var afterHtml = await rendered.HtmlAsync();
        Assert.Contains(StaleRoleModelMarker, afterHtml, StringComparison.Ordinal);
        Assert.Contains(StaleTesterModelMarker, afterHtml, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. THE AUDIT ROW: the OTHER facade-populated projections really do clear
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE AUDIT, INDEPENDENTLY RE-VERIFIED: the coder's report claims the page has exactly three
    /// facade-populated fields and that <c>_stats</c> and <c>_llmSessions</c> already cleared on a
    /// denial while <c>_info</c> was the only leak. This row reproduces the SAME
    /// populated-then-denied sequence for those two fields and proves both halves: they were
    /// genuinely populated before the denial (non-vacuous baseline) and genuinely empty after it.
    /// </summary>
    [Fact]
    public async Task TheDeniedRefresh_AlsoClearsTheBrainStatsAndTheSessionList_WhichWereAlreadyCorrect()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled);

        host.Registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "stale-shared-session",
            SessionType = LlmSessionType.Brain,
            Model = SharedSessionModelMarker,
            Status = "active",
            MaxTokens = 1,
        });

        await using var rendered = await RenderAsync(host, OperatorId);
        var beforeHtml = await rendered.HtmlAsync();

        // THE NON-VACUOUS BASELINE: both projections were really populated.
        Assert.NotNull(rendered.Field("_stats"));
        Assert.Equal("recording-model", rendered.Field<BrainStats>("_stats").Model);
        Assert.NotEmpty(rendered.Field<List<LlmSessionInfo>>("_llmSessions"));
        Assert.Contains(SharedSessionModelMarker, beforeHtml, StringComparison.Ordinal);

        // ── The account is DISABLED and the SAME instance is refreshed. ──
        await _auth.SetEnabledAsync(OperatorId, enabled: false);
        host.Notifier.NotifyStateChanged();
        await rendered.DrainAsync();

        // BOTH were cleared on the very same denial.
        Assert.Null(rendered.Field("_stats"));
        Assert.Empty(rendered.Field<List<LlmSessionInfo>>("_llmSessions"));

        var afterHtml = await rendered.HtmlAsync();
        Assert.DoesNotContain(SharedSessionModelMarker, afterHtml, StringComparison.Ordinal);
        Assert.Contains("Brain not connected.", afterHtml, StringComparison.Ordinal);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The framework's OWN HTML encoder, so a value rendered into the markup is compared in
    /// exactly the form the renderer produced it (the assembly version contains a '+', which the
    /// framework encodes as '&amp;#x2B;'). Using the same encoder keeps the assertion about the
    /// VALUE, not about an encoding detail.
    /// </summary>
    private static string Encode(string value) =>
        System.Text.Encodings.Web.HtmlEncoder.Default.Encode(value);

    /// <summary>A JS runtime that answers nothing (this page only uses it for the reset confirm).</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);
    }

    /// <summary>A navigation manager that records nothing (this page never navigates).</summary>
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/orchestrator");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            // Navigation is not exercised by these rows.
        }
    }

    public void Dispose() => _auth.Dispose();
}
