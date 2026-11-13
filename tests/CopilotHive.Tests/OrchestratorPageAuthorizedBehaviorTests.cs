using System.Reflection;
using System.Security.Claims;

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
/// THE BEHAVIOURAL PROOF that the ORCHESTRATOR PAGE runs through the authorized operational
/// boundary: the REAL page is rendered through the framework's own <see cref="HtmlRenderer"/> over
/// the REAL production facade, the REAL <see cref="HivePrincipalResolver"/>/<see cref="HivePolicy"/>
/// pair and the REAL <see cref="UserService"/> directory — exactly the pattern established by
/// <see cref="CopilotHive.Tests.Services.GoalIssuePagesStaleDataRegressionTests"/> and
/// <see cref="CopilotHive.Tests.Orchestration.OpenModeUiToleranceTests"/>.
/// <para>
/// The source-shape rows in <see cref="OrchestratorPageAuthorizedMigrationTests"/> pin WHAT the page
/// calls; these rows pin WHAT THE PAGE ACTUALLY DOES when those calls are made:
/// </para>
/// <list type="bullet">
///   <item>the page ACTIVATES through the facade — a page that dereferenced an absent cascade, or
///   injected an unavailable service, would fail to activate here;</item>
///   <item>the page's session table is fed by the SANITIZED shared projection: a seeded Composer
///   session exists in the real registry (the non-vacuity control) and is ABSENT from the rendered
///   markup, while the shared execution sessions are present;</item>
///   <item>the page's Brain-reset handler goes through the facade: an Operator's click really
///   resets the Brain behind the trusted dashboard service, and a VIEWER's click does NOT — the
///   master session is left alone.</item>
/// </list>
/// </summary>
public sealed class OrchestratorPageAuthorizedBehaviorTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "op-page-viewer";
    private const string OperatorId = "op-page-operator";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── The Brain behind the trusted dashboard service ───────────────────────

    /// <summary>
    /// A recording Brain: the dashboard service is the PRODUCTION (sealed) type, so the observable
    /// seam behind it is the Brain whose master session it resets.
    /// </summary>
    private sealed class RecordingBrain : IDistributedBrain
    {
        /// <summary>How many times the master session reset actually reached the Brain.</summary>
        internal int ResetCount { get; private set; }

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

        public Task ResetSessionAsync(CancellationToken ct = default)
        {
            ResetCount++;
            return Task.CompletedTask;
        }

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
            LlmSessionRegistry registry,
            RecordingBrain brain)
        {
            _provider = provider;
            DashboardState = dashboardState;
            Registry = registry;
            Brain = brain;
        }

        internal DashboardStateService DashboardState { get; }

        internal LlmSessionRegistry Registry { get; }

        internal RecordingBrain Brain { get; }

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

        var dashboardState = new DashboardStateService(
            new WorkerPool(),
            new GoalPipelineManager(),
            new DashboardLogSink(),
            new ProgressLog(),
            notifier: null,
            brain: brain);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(dashboardState);
        services.AddSingleton(registry);
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<IGoalStore>(new InMemoryGoalStore());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new ConfirmingJsRuntime());
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

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, dashboardState, registry, brain);
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
        /// COMPLETED — no sleep, no polling, no timing assumption.
        /// </summary>
        internal Task DrainAsync() => _renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        /// <summary>Reads a private field of the rendered page (its own loaded state).</summary>
        internal object? Field(string name) =>
            _page.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_page);

        /// <summary>Invokes a private method of the rendered page on the renderer's dispatcher.</summary>
        internal async Task InvokePrivateAsync(string methodName, params object?[] args)
        {
            var method = _page.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (method.Invoke(_page, args) is Task task)
                    await task;
            });
        }

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
    //  1. The page activates and renders through the facade/projection paths
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PAGE ACTIVATES AND RENDERS THROUGH THE FACADE: the real page is rendered over the real
    /// facade, the real resolver/policy and the real directory, and its markup contains the
    /// orchestrator info the facade's shared-read projection supplied (a NON-EMPTY marker, so the
    /// row is not satisfied by an empty or failed render).
    /// </summary>
    [Fact]
    public async Task TheOrchestratorPage_Activates_AndRendersTheAuthorizedSharedReadProjections()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled);

        await using var rendered = await RenderAsync(host, ViewerId);
        var html = await rendered.HtmlAsync();

        // The page really rendered.
        Assert.False(string.IsNullOrWhiteSpace(html));

        // The shared-read projection reached the page: the orchestrator info block is present with
        // its distinctive heading, and the Brain section rendered the facade's projection (the
        // message count comes from the Brain's statistics through the facade).
        Assert.Contains("Model Configuration", html, StringComparison.Ordinal);
        Assert.Contains("4242", html, StringComparison.Ordinal);

        // The page's own loaded state came through the facade: the Brain statistics are present.
        Assert.NotNull(rendered.Field("_stats"));
        var stats = Assert.IsAssignableFrom<BrainStats>(rendered.Field("_stats"));
        Assert.Equal("recording-model", stats.Model);
        Assert.Equal(4242, stats.MessageCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. The session table is fed by the SANITIZED projection
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SANITIZED PROJECTION FEEDS THE PAGE: a seeded Composer session REALLY exists in the
    /// real registry (the non-vacuity control — without it the exclusion assertion would be
    /// vacuously green), and the page's rendered session table shows the shared execution sessions
    /// while the Composer session is ABSENT from the markup and from the page's own loaded state.
    /// </summary>
    [Fact]
    public async Task TheOrchestratorPage_RendersTheSharedSessions_ButNeverTheComposerOne()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled);

        host.Registry.RegisterOrUpdate(Session("composer-private-marker", LlmSessionType.Composer, "composer-model-marker"));
        host.Registry.RegisterOrUpdate(Session("brain-shared-marker", LlmSessionType.Brain, "brain-model-marker"));

        // THE NON-VACUITY CONTROL: the Composer session really is in the registry the facade reads.
        Assert.Contains(host.Registry.GetAll(), s => s.SessionId == "composer-private-marker");
        Assert.Contains(host.Registry.GetAll(), s => s.SessionType == LlmSessionType.Composer);

        await using var rendered = await RenderAsync(host, ViewerId);
        var html = await rendered.HtmlAsync();

        // The shared session IS rendered — so the table is really fed by the projection. (The page
        // renders the session's MODEL, not its id, so the model is the observable marker.)
        Assert.Contains("brain-model-marker", html, StringComparison.Ordinal);

        // The Composer session is NOT rendered — the private metadata never reaches the page.
        Assert.DoesNotContain("composer-model-marker", html, StringComparison.Ordinal);
        Assert.DoesNotContain("composer-private-marker", html, StringComparison.Ordinal);

        // And the SESSION TABLE itself carries no Composer entry: the section between the
        // "LLM Sessions" heading and the "Clarifications" heading is inspected on its own, because
        // the page legitimately renders the word "Composer" elsewhere (the clarifications card).
        var sessionSection = SessionSection(html);
        Assert.Contains("brain-model-marker", sessionSection, StringComparison.Ordinal);
        Assert.DoesNotContain("Composer", sessionSection, StringComparison.Ordinal);

        // The page's own loaded collection holds the shared session only.
        var sessions = Assert.IsAssignableFrom<List<LlmSessionInfo>>(rendered.Field("_llmSessions"));
        Assert.Contains(sessions, s => s.SessionId == "brain-shared-marker");
        Assert.DoesNotContain(sessions, s => s.SessionType == LlmSessionType.Composer);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. The reset button goes through the facade
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE RESET BUTTON REACHES THE BRAIN FOR AN OPERATOR: the page's own handler runs, and the
    /// master session is REALLY reset behind the trusted dashboard service — the migration is not
    /// merely a source shape.
    /// </summary>
    [Fact]
    public async Task TheOrchestratorPage_ResetHandler_AsOperator_ReallyResetsTheBrain()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled);

        await using var rendered = await RenderAsync(host, OperatorId);

        // The page really rendered its Brain section, so the reset handler is reachable.
        Assert.NotNull(rendered.Field("_stats"));

        await rendered.InvokePrivateAsync("ResetBrainSessionAsync");
        await rendered.DrainAsync();

        // THE RESET REALLY HAPPENED.
        Assert.Equal(1, host.Brain.ResetCount);
    }

    /// <summary>
    /// THE RESET BUTTON IS REFUSED FOR A VIEWER: the page's own handler runs, the facade refuses,
    /// and the master session is left COMPLETELY ALONE — the page cannot bypass the authorized
    /// boundary by calling the trusted service itself.
    /// </summary>
    [Fact]
    public async Task TheOrchestratorPage_ResetHandler_AsViewer_DoesNotResetTheBrain()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled);

        await using var rendered = await RenderAsync(host, ViewerId);

        await rendered.InvokePrivateAsync("ResetBrainSessionAsync");
        await rendered.DrainAsync();

        // THE MASTER SESSION WAS NOT RESET.
        Assert.Equal(0, host.Brain.ResetCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The page's SESSION TABLE section: the markup between the "LLM Sessions" heading and the
    /// "Clarifications" heading. Isolating it makes the "no Composer entry" assertion about the
    /// session table specifically — the page renders the word "Composer" elsewhere (the
    /// clarifications card), so a whole-page search would be meaningless.
    /// </summary>
    private static string SessionSection(string html)
    {
        var start = html.IndexOf("LLM Sessions", StringComparison.Ordinal);
        Assert.True(start >= 0, "the page rendered no session table at all");

        var end = html.IndexOf("Clarifications", StringComparison.Ordinal);
        Assert.True(end > start, "the page rendered no clarifications heading after the session table");

        return html[start..end];
    }

    private static LlmSessionInfo Session(string id, LlmSessionType type, string model = "m") => new()
    {
        SessionId = id,
        SessionType = type,
        Model = model,
        Status = "active",
        MaxTokens = 1,
    };

    /// <summary>
    /// A JS runtime that CONFIRMS every confirmation dialog, so the page's reset flow proceeds past
    /// its first step. Without this the handler would return before the facade was ever reached
    /// and the rows below would be vacuously green.
    /// </summary>
    private sealed class ConfirmingJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(Confirm<TValue>(identifier));

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(Confirm<TValue>(identifier));

        /// <summary>The confirmation answer: <c>true</c> for the reset dialog, the default otherwise.</summary>
        private static TValue Confirm<TValue>(string identifier) =>
            identifier == "confirm" && typeof(TValue) == typeof(bool)
                ? (TValue)(object)true
                : default!;
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
