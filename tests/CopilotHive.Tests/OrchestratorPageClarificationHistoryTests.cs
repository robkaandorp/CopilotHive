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
/// THE PERSISTED-HISTORY REGRESSION for the ORCHESTRATOR PAGE, exercised BEHAVIOURALLY: the REAL
/// page is rendered through the framework's own <see cref="HtmlRenderer"/> with a scripted
/// <see cref="RecordingClarificationFacade"/> in front of it, so the test observes exactly which
/// identity the page hands the facade and what the page does with the answer.
/// <para>
/// THE CONTRACT:
/// </para>
/// <list type="bullet">
///   <item>The page reads the persisted history through the facade ONLY — it never touches
///   <see cref="IGoalStore.GetAllClarificationsAsync"/> itself (the recording store proves that).</item>
///   <item>The identity it hands the facade is the circuit's identity, acquired through the one
///   <c>ComponentActingUser</c> seam — an observable claim, not a source-token assertion.</item>
///   <item>The refresh callback re-reads the history, so a mid-session denial clears it and a later
///   authorized refresh repopulates it.</item>
///   <item>A denial leaves the field empty in BOTH the page state and the markup.</item>
/// </list>
/// </summary>
public sealed class OrchestratorPageClarificationHistoryTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string OperatorId = "clhist-page-operator";
    private const string HistoryMarker = "CLARIFYPAGE-HISTORY-MARKER-4F2A";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── The Brain behind the trusted dashboard service ───────────────────────

    /// <summary>
    /// A recording Brain: the dashboard service is the PRODUCTION (sealed) type, so the observable
    /// seam behind it is the Brain whose statistics it serves.
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
            RecordingGoalStore goalStore,
            RecordingClarificationFacade clarificationFacade)
        {
            _provider = provider;
            DashboardState = dashboardState;
            Notifier = notifier;
            GoalStore = goalStore;
            Clarifications = clarificationFacade;
        }

        internal DashboardStateService DashboardState { get; }

        /// <summary>The notifier the page's own refresh subscription is wired to.</summary>
        internal DashboardNotifier Notifier { get; }

        /// <summary>The recording goal store — the page must NEVER read it for clarifications.</summary>
        internal RecordingGoalStore GoalStore { get; }

        /// <summary>The recording facade the page talks to instead.</summary>
        internal RecordingClarificationFacade Clarifications { get; }

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

    private Host NewHost(HiveAuthMode mode, RecordingClarificationFacade clarifications)
    {
        var brain = new RecordingBrain();
        var registry = new LlmSessionRegistry();
        var notifier = new DashboardNotifier();
        var goalStore = new RecordingGoalStore();

        var dashboardState = new DashboardStateService(
            new WorkerPool(),
            new GoalPipelineManager(),
            new DashboardLogSink(),
            new ProgressLog(),
            notifier: notifier,
            brain: brain);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(notifier);
        services.AddSingleton(dashboardState);
        services.AddSingleton(registry);
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<IGoalStore>(goalStore);
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

        // THE AUTHORIZED OPERATIONAL FACADE — the production type over the production resolver/policy.
        services.AddSingleton<IOperationalFacade>(sp => new OperationalFacade(
            sp.GetRequiredService<DashboardStateService>(),
            sp.GetRequiredService<LlmSessionRegistry>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            NullLogger<OperationalFacade>.Instance));

        // THE RECORDING CLARIFICATION FACADE — what the page is supposed to talk to, exclusively.
        services.AddSingleton<IClarificationFacade>(clarifications);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, dashboardState, notifier, goalStore, clarifications);
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

        /// <summary>Awaits the renderer's work queue: every work item enqueued BEFORE this barrier has completed.</summary>
        internal Task DrainAsync() => _renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        /// <summary>Reads a private field of the rendered page (its own loaded state).</summary>
        internal T Field<T>(string name) =>
            Assert.IsAssignableFrom<T>(_page.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_page));

        /// <summary>The identity of the rendered page INSTANCE (the same one is refreshed below).</summary>
        internal object Page => _page;

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            _scope.Dispose();
        }
    }

    /// <summary>Renders the REAL Orchestrator page for one circuit identity.</summary>
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

    // ══════════════════════════════════════════════════════════════════
    //  1. The identity reaches the facade — and the store is not the page's path
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PAGE PASSES THE CIRCUIT'S IDENTITY TO THE FACADE: the recorded principal is the SAME
    /// principal the cascade carried (same GitHub id claim), obtained by the page through the one
    /// <c>ComponentActingUser</c> seam — and the page never touched the store directly.
    /// </summary>
    [Fact]
    public async Task TheHistoryRead_PassesTheCircuitIdentity_AndNeverReadsTheStoreDirectly()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var recorder = new RecordingClarificationFacade(RecordingClarificationFacade.History(HistoryMarker));
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, recorder);

        await using var rendered = await RenderAsync(host, OperatorId);

        // The page asked the facade, with the circuit identity, at initialisation time.
        Assert.Equal(1, recorder.HistoryCallCount);
        var acting = Assert.Single(recorder.ActingUsers);
        Assert.NotNull(acting);
        Assert.Equal(OperatorId, acting!.FindFirst(ClaimTypes.NameIdentifier)?.Value);

        // The page NEVER read the store itself — no identity-free clarifications read exists here.
        Assert.Equal(0, host.GoalStore.GetAllClarificationsCallCount);

        // ...and the history really rendered.
        var html = await rendered.HtmlAsync();
        Assert.Contains(HistoryMarker, html, StringComparison.Ordinal);
        var rows = rendered.Field<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>("_clarifications");
        Assert.Single(rows);
        Assert.Equal(HistoryMarker, rows[0].Clarification.Question);
    }

    // ══════════════════════════════════════════════════════════════════
    //  2. Refresh re-invokes the history read
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REFRESH CALLBACK RE-READS THE HISTORY: the notifier the page subscribes to (the very
    /// path a reset or an event notification takes) triggers a second facade call with the same
    /// circuit identity — the history is not a one-shot initialisation read.
    /// </summary>
    [Fact]
    public async Task TheRefreshCallback_ReReadsTheHistory_WithTheSameIdentity()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var recorder = new RecordingClarificationFacade(RecordingClarificationFacade.History(HistoryMarker));
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, recorder);

        await using var rendered = await RenderAsync(host, OperatorId);
        Assert.Equal(1, recorder.HistoryCallCount);

        host.Notifier.NotifyStateChanged();
        await rendered.DrainAsync();

        Assert.Equal(2, recorder.HistoryCallCount);
        Assert.All(recorder.ActingUsers, user =>
            Assert.Equal(OperatorId, user!.FindFirst(ClaimTypes.NameIdentifier)?.Value));
        Assert.Equal(0, host.GoalStore.GetAllClarificationsCallCount);
    }

    // ══════════════════════════════════════════════════════════════════
    //  3. A denial clears the loaded history — in state AND in markup
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// POPULATE → DENY → CLEARED: the page first loads a real history, the account is then disabled
    /// in the directory and the page's own refresh path runs on the SAME component instance, and the
    /// history is cleared from BOTH the page's state and the rendered markup.
    /// </summary>
    [Fact]
    public async Task ADeniedRefresh_ClearsTheLoadedHistory_FromTheStateAndTheMarkup()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        // The facade's ANSWER is scripted per call: authorized first, forbidden after the disable.
        // The refusal SHAPE is the production one (the shared sanitized denial), matching what the
        // real facade returns once the account is no longer entitled to the read.
        var allowed = true;
        var recorder = new RecordingClarificationFacade((_, _) =>
            allowed
                ? new FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(
                    true, RecordingClarificationFacade.History(HistoryMarker), null, FacadeErrorKind.None)
                : RecordingClarificationFacade.Denied());

        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, recorder);
        await using var rendered = await RenderAsync(host, OperatorId);

        // THE BASELINE IS NON-EMPTY: the history really rendered.
        var beforeHtml = await rendered.HtmlAsync();
        Assert.Contains(HistoryMarker, beforeHtml, StringComparison.Ordinal);
        Assert.Single(rendered.Field<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>("_clarifications"));

        // The account is disabled; the page's own refresh path runs on the SAME instance.
        await _auth.SetEnabledAsync(OperatorId, enabled: false);
        allowed = false;
        host.Notifier.NotifyStateChanged();
        await rendered.DrainAsync();

        // The refusal really happened (the recording facade saw the second call).
        Assert.Equal(2, recorder.HistoryCallCount);

        // The page holds NOTHING: neither the field nor the markup carries the history anymore.
        Assert.Empty(rendered.Field<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>("_clarifications"));
        var afterHtml = await rendered.HtmlAsync();
        Assert.DoesNotContain(HistoryMarker, afterHtml, StringComparison.Ordinal);
        Assert.Contains("No clarifications recorded yet.", afterHtml, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════
    //  4. A denied refresh is not a permanent suppression
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// DENY → RE-AUTHORIZE → REFRESH REPOPULATES: a denial clears the field, but the clear is not a
    /// cached "do not read again" flag — once the account is enabled again, the page's refresh loads
    /// the history back.
    /// </summary>
    [Fact]
    public async Task ADeniedRefresh_DoesNotSuppressTheNextAuthorizedRefresh()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var allowed = true;
        var recorder = new RecordingClarificationFacade((_, _) =>
            allowed
                ? new FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(
                    true, RecordingClarificationFacade.History(HistoryMarker), null, FacadeErrorKind.None)
                : RecordingClarificationFacade.Denied());

        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, recorder);
        await using var rendered = await RenderAsync(host, OperatorId);

        Assert.Single(rendered.Field<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>("_clarifications"));

        // Deny once: the field clears.
        await _auth.SetEnabledAsync(OperatorId, enabled: false);
        allowed = false;
        host.Notifier.NotifyStateChanged();
        await rendered.DrainAsync();
        Assert.Empty(rendered.Field<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>("_clarifications"));

        // Re-authorize: the very next refresh loads the history again.
        await _auth.SetEnabledAsync(OperatorId, enabled: true);
        allowed = true;
        host.Notifier.NotifyStateChanged();
        await rendered.DrainAsync();

        var rows = rendered.Field<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>("_clarifications");
        Assert.Single(rows);
        Assert.Equal(HistoryMarker, rows[0].Clarification.Question);

        var html = await rendered.HtmlAsync();
        Assert.Contains(HistoryMarker, html, StringComparison.Ordinal);
        Assert.DoesNotContain("No clarifications recorded yet.", html, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════
    //  5. The page surfaces the denial exactly like a first render for an unauthorized caller
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A DENIAL IS NOT A CRASH: an unauthorized caller sees the page render with an EMPTY history
    /// (and the "no clarifications" copy), and the facade recorded the refusal — never an exception
    /// escaping the renderer.
    /// </summary>
    [Fact]
    public async Task AForbiddenHistory_RendersTheEmptyState_NotAnErrorPage()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var recorder = new RecordingClarificationFacade((_, _) => RecordingClarificationFacade.Denied());
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, recorder);

        await using var rendered = await RenderAsync(host, OperatorId);

        var html = await rendered.HtmlAsync();
        Assert.Contains("No clarifications recorded yet.", html, StringComparison.Ordinal);
        Assert.DoesNotContain(HistoryMarker, html, StringComparison.Ordinal);
        Assert.Empty(rendered.Field<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>("_clarifications"));
        Assert.Equal(1, recorder.HistoryCallCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A JS runtime that answers nothing.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);
    }

    /// <summary>A navigation manager that records nothing.</summary>
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
