using System.Reflection;
using System.Security.Claims;

using CopilotHive.Components;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// One renderer, one scope, one retained host/component for every sequence. Cascades are updated by
/// StateHasChanged on the retained parent; no lifecycle method of Routes is invoked by the test.
/// History refreshes rendezvous on the actual facade RESULT, then run through the page's real
/// ComponentBase event receiver so its retained output re-renders on completion.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class CircuitPublicationRegressionTests : IAsyncLifetime
{
    private const string Original = "retained-route-original";
    private const string Replacement = "retained-route-replacement";
    private const string Marker = "RETAINED-HISTORY-ORDER-MARKER-82E";
    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly List<Retained> _roots = [];
    private readonly string? _clientId = Environment.GetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable);
    private readonly string? _clientSecret = Environment.GetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static AuthenticationState State(string id) => new(FacadeAuthorizationHarness.Principal(id));

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, null);
        await _auth.SeedAsync(Original, UserRole.Operator);
        await _auth.SeedAsync(Replacement, UserRole.Admin);
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var root in _roots)
        {
            await root.DisposeAsync();
            failures.AddRange(root.TakeUnreportedCleanupFailures());
        }
        try { _auth.Dispose(); } catch (Exception ex) { failures.Add(ex); }
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, _clientId);
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, _clientSecret);
        if (failures.Count != 0) throw new AggregateException("Retained root cleanup failed", failures);
    }

    private async Task<Retained> RenderAsync(Type component, RecordingClarificationFacade? facade = null)
    {
        facade ??= new RecordingClarificationFacade();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton(new CopilotHive.Orchestration.ClarificationQueueService());
        services.AddSingleton<IClarificationFacade>(facade);
        services.AddSingleton(new DashboardStateService(new WorkerPool(), new GoalPipelineManager(),
            new DashboardLogSink(), new ProgressLog()));
        services.AddSingleton(new LlmSessionRegistry());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJs());
        services.AddSingleton<NavigationManager>(new Navigation());
        services.AddSingleton<Microsoft.AspNetCore.Components.Routing.INavigationInterception>(new Interception());
        services.AddSingleton<Microsoft.AspNetCore.Components.Routing.IScrollToLocationHash>(new Scrolling());
        // Support either static Routes branch, without letting NavMenu render OAuth UI. The unmatched
        // route renders MainLayout only, so no child page can observe later cascades on our behalf.
        services.AddAuthorizationCore();
        var resolver = _auth.Resolver(HiveAuthMode.AuthenticationEnabled);
        services.AddScoped(_ => new CircuitUserPrincipalAccessor(resolver, _auth.Policy));
        services.AddSingleton<IOperationalFacade>(sp => new OperationalFacade(
            sp.GetRequiredService<DashboardStateService>(), sp.GetRequiredService<LlmSessionRegistry>(),
            resolver, _auth.Policy, NullLogger<OperationalFacade>.Instance));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var scope = provider.CreateAsyncScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        var retained = new Retained(provider, scope, renderer, facade);
        _roots.Add(retained); // ownership precedes initialization or any throwing assertion
        await retained.Track(renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = b =>
            {
                b.OpenComponent(0, component);
                b.AddComponentReferenceCapture(1, c => retained.Page = c);
                b.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<ParameterStateHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(ParameterStateHost.InitialState)] = State(Original),
                    [nameof(ParameterStateHost.Content)] = content,
                    [nameof(ParameterStateHost.Capture)] = (Action<ParameterStateHost>)(host => retained.Host = host),
                }));
            retained.Html = root.ToHtmlString;
        }));
        Assert.NotNull(retained.Page);
        Assert.NotNull(retained.Host);
        Assert.False(string.IsNullOrWhiteSpace(await retained.HtmlAsync()));
        return retained;
    }

    [Fact]
    public async Task Routes_RetainedSignOut_PermanentlyInvalidatesPreviouslyAuthorizedCircuit()
    {
        await using var root = await RenderAsync(typeof(Routes));
        var host = root.Host;
        var routes = root.Page;
        var accessor = root.Accessor;
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));
        await root.UpdateAsync(new AuthenticationState(FacadeAuthorizationHarness.Anonymous()));
        Assert.Same(host, root.Host);
        Assert.Same(routes, root.Page);
        Assert.Same(accessor, root.Accessor);
        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
        Assert.True(accessor.IsInvalidated);
        Assert.Null(accessor.EstablishedIdentity);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
        Assert.False((await accessor.GetPrincipalAsync(Ct)).IsResolved);
        // The account is still valid in the directory: the CIRCUIT was invalidated, not disabled.
        Assert.True((await _auth.Resolver(HiveAuthMode.AuthenticationEnabled)
            .ResolveAsync(FacadeAuthorizationHarness.Principal(Original), Ct)).IsResolved);
        await root.UpdateAsync(State(Original));
        Assert.Same(routes, root.Page);
        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));
    }

    [Fact]
    public async Task Routes_RetainedIdentityReplacement_IsPermanentAndNeverAdoptsReplacement()
    {
        await using var root = await RenderAsync(typeof(Routes));
        var routes = root.Page;
        var accessor = root.Accessor;
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));
        Assert.Equal(Original, accessor.EstablishedIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        await root.UpdateAsync(State(Replacement));
        Assert.Same(routes, root.Page);
        Assert.Same(accessor, root.Accessor);
        Assert.Equal(CircuitIdentityState.IdentityReplaced, accessor.IdentityState);
        Assert.Null(accessor.EstablishedIdentity);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        await root.UpdateAsync(State(Original));
        await root.UpdateAsync(State(Replacement));
        Assert.Same(routes, root.Page);
        Assert.Equal(CircuitIdentityState.IdentityReplaced, accessor.IdentityState);
        Assert.False((await accessor.GetPrincipalAsync(Ct)).IsResolved);
    }

    [Fact]
    public async Task Routes_RetainedSameIdentityUpdateControl_RemainsAuthorized()
    {
        await using var root = await RenderAsync(typeof(Routes));
        var routes = root.Page;
        var accessor = root.Accessor;
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));
        // New AuthenticationState and ClaimsPrincipal objects; SAME identity comparison key.
        await root.UpdateAsync(State(Original));
        await root.UpdateAsync(State(Original));
        Assert.Same(routes, root.Page);
        Assert.Same(accessor, root.Accessor);
        Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
        Assert.False(accessor.IsInvalidated);
        Assert.Equal(Original, accessor.EstablishedIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));
    }

    [Fact]
    public async Task Orchestrator_HeldOlderSuccess_AfterNewerForbidden_StaysClearedInStateAndMarkup()
    {
        var allowed = true;
        var recorder = new RecordingClarificationFacade((_, _) => allowed
            ? new FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(
                true, RecordingClarificationFacade.History(Marker), null, FacadeErrorKind.None)
            : RecordingClarificationFacade.Denied());
        await using var root = await RenderAsync(typeof(CopilotHive.Components.Pages.Orchestrator), recorder);
        var page = root.Page;
        Assert.Single(root.History);
        Assert.Contains(Marker, await root.HtmlAsync(), StringComparison.Ordinal);
        var hold = recorder.ArmHistory();
        var older = root.RefreshEventAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);
        Assert.True(hold.Answer!.Success);
        Assert.Equal(Marker, Assert.Single(hold.Answer.Value!).Clarification.Question);
        allowed = false;
        await root.RefreshEventAsync();
        Assert.Same(page, root.Page);
        Assert.Equal(3, recorder.HistoryCallCount);
        Assert.DoesNotContain(Marker, await root.HtmlAsync(), StringComparison.Ordinal);
        Assert.Contains("No clarifications recorded yet.", await root.HtmlAsync(), StringComparison.Ordinal);
        Assert.Empty(root.History);
        await root.ReleaseAsync(hold);
        await older;
        // Markup assertion precedes state assertion: independently discriminates stale publication.
        Assert.DoesNotContain(Marker, await root.HtmlAsync(), StringComparison.Ordinal);
        Assert.Contains("No clarifications recorded yet.", await root.HtmlAsync(), StringComparison.Ordinal);
        Assert.Empty(root.History);
        Assert.Same(page, root.Page);
    }

    [Fact]
    public async Task Orchestrator_HeldSuccessWithoutNewerDenialControl_PublishesInStateAndMarkup()
    {
        var marker = "INITIAL-HISTORY-CONTROL";
        var recorder = new RecordingClarificationFacade((_, _) =>
            new FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(
                true, RecordingClarificationFacade.History(marker), null, FacadeErrorKind.None));
        await using var root = await RenderAsync(typeof(CopilotHive.Components.Pages.Orchestrator), recorder);
        Assert.Contains(marker, await root.HtmlAsync(), StringComparison.Ordinal);
        marker = Marker;
        var hold = recorder.ArmHistory();
        var refresh = root.RefreshEventAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(refresh.IsCompleted);
        Assert.DoesNotContain(Marker, await root.HtmlAsync(), StringComparison.Ordinal);
        await root.ReleaseAsync(hold);
        await refresh;
        Assert.Contains(Marker, await root.HtmlAsync(), StringComparison.Ordinal);
        Assert.Equal(Marker, Assert.Single(root.History).Clarification.Question);
        Assert.Equal(2, recorder.HistoryCallCount);
    }

    public sealed class ParameterStateHost : ComponentBase
    {
        [Parameter] public AuthenticationState InitialState { get; set; } = null!;
        [Parameter] public RenderFragment Content { get; set; } = null!;
        [Parameter] public Action<ParameterStateHost> Capture { get; set; } = null!;
        private Task<AuthenticationState> _state = null!;
        protected override void OnInitialized() { _state = Task.FromResult(InitialState); Capture(this); }
        internal void Change(AuthenticationState state) { _state = Task.FromResult(state); StateHasChanged(); }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", _state);
            b.AddAttribute(2, "ChildContent", Content);
            b.CloseComponent();
        }
    }

    private sealed class Retained(ServiceProvider provider, AsyncServiceScope scope, HtmlRenderer renderer,
        RecordingClarificationFacade recorder) : IAsyncDisposable
    {
        internal object Page { get; set; } = null!;
        internal ParameterStateHost Host { get; set; } = null!;
        internal Func<string> Html { get; set; } = null!;
        internal CircuitUserPrincipalAccessor Accessor => scope.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>();
        internal IReadOnlyList<(string GoalId, PersistedClarification Clarification)> History =>
            (IReadOnlyList<(string GoalId, PersistedClarification Clarification)>)Page.GetType()
                .GetField("_clarifications", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page)!;
        private readonly List<Task> _tasks = [];
        internal Task Track(Task task) { _tasks.Add(task); return task; }
        internal Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(Html);
        internal Task UpdateAsync(AuthenticationState state) => Track(renderer.Dispatcher.InvokeAsync(() => Host.Change(state)));
        internal Task ReleaseAsync(RecordingClarificationFacade.HistoryHold hold) => renderer.Dispatcher.InvokeAsync(hold.Release);
        internal Task RefreshEventAsync() => Track(renderer.Dispatcher.InvokeAsync(() =>
        {
            var refresh = Page.GetType().GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<Task>>(Page);
            return EventCallback.Factory.Create(Page, refresh).InvokeAsync();
        }));
        private bool _disposed;
        private readonly List<Exception> _cleanup = [];
        private int _reported;
        internal Exception[] TakeUnreportedCleanupFailures()
        {
            var result = _cleanup.Skip(_reported).ToArray(); _reported = _cleanup.Count; return result;
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return; _disposed = true;
            async Task Step(Func<Task> step) { try { await step(); } catch (Exception ex) { _cleanup.Add(ex); } }
            await Step(() => renderer.Dispatcher.InvokeAsync(recorder.ReleaseHistoryHolds));
            foreach (var task in _tasks) await Step(() => task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            await Step(() => renderer.DisposeAsync().AsTask());
            await Step(() => scope.DisposeAsync().AsTask());
            await Step(() => provider.DisposeAsync().AsTask());
        }
    }

    private sealed class Navigation : NavigationManager
    {
        internal Navigation() => Initialize("http://localhost/", "http://localhost/retained-unmatched-probe");
        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }
    private sealed class Interception : Microsoft.AspNetCore.Components.Routing.INavigationInterception
    { public Task EnableNavigationInterceptionAsync() => Task.CompletedTask; }
    private sealed class Scrolling : Microsoft.AspNetCore.Components.Routing.IScrollToLocationHash
    { public Task RefreshScrollPositionForHash(string locationAbsolute) => Task.CompletedTask; }
    private sealed class NullJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult<T>(default!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult<T>(default!);
    }
}
