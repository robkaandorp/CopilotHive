using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;

using CopilotHive.Components.Layout;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
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
/// Retained real MainLayout roots. The controllable facade delegates to the real facade/resolver/
/// SQLite directory unless a row explicitly scripts an outcome. Count-result gates park the actual
/// production refresh at its final await; release runs ON the renderer dispatcher with synchronous
/// continuations so the awaiting refresh publishes/renders before the release invocation returns.
/// A dispatcher fence alone is never used as proof that an incomplete async refresh finished.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class MainLayoutAuthorizationTests : IAsyncLifetime
{
    private const string UserId = "layout-operator";
    private const string RequestId = "layout-request";
    private const string Question = "LAYOUT-AUTHORIZED-QUESTION-93C";
    private const string Draft = "LAYOUT-DRAFT-91D";
    private const string Forbidden = "The acting account is not authorized to perform this operation.";
    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly List<Circuit> _circuits = [];
    private readonly string? _clientId = Environment.GetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable);
    private readonly string? _clientSecret = Environment.GetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // NavMenu has its own environment-dependent authentication rendering, outside this slice.
        // Serialize ALL environment reads/writes/restores; no auth services are needed by NavMenu.
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, null);
        await _auth.SeedAsync(UserId, UserRole.Operator);
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var circuit in _circuits)
        {
            await circuit.DisposeAsync();
            failures.AddRange(circuit.TakeUnreportedCleanupFailures());
        }
        try { _auth.Dispose(); } catch (Exception ex) { failures.Add(ex); }
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, _clientId);
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, _clientSecret);
        try
        {
            Assert.Equal(_clientId, Environment.GetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable));
            Assert.Equal(_clientSecret, Environment.GetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable));
        }
        catch (Exception ex) { failures.Add(ex); }
        if (failures.Count != 0) throw new AggregateException("MainLayout per-test cleanup failed", failures);
    }

    private static ClarificationRequest Request(string id = RequestId, string question = Question) => new()
    {
        Id = id, GoalId = "layout-goal", WorkerRole = "tester", Question = question,
        Status = ClarificationStatus.AwaitingHuman,
        RequestedAt = new DateTime(2024, 7, 1, 14, 23, 45, DateTimeKind.Utc),
    };

    private async Task<Circuit> RenderAsync(bool open = false)
    {
        var mode = open ? HiveAuthMode.Open : HiveAuthMode.AuthenticationEnabled;
        var queue = new ClarificationQueueService();
        queue.Enqueue(Request());
        var real = new ClarificationFacade(queue, new InMemoryGoalStore(), _auth.Resolver(mode),
            _auth.Policy, NullLogger<ClarificationFacade>.Instance);
        var facade = new ControlledFacade(real);
        var dashboard = new DashboardStateService(new WorkerPool(), new GoalPipelineManager(),
            new DashboardLogSink(), new ProgressLog());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(queue);
        services.AddSingleton(dashboard);
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<NavigationManager>(new Navigation());
        services.AddSingleton<IClarificationFacade>(facade);
        services.AddScoped(_ => new CircuitUserPrincipalAccessor(_auth.Resolver(mode), _auth.Policy));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var scope = provider.CreateAsyncScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        // Own resources BEFORE rendering or asserting, including failed activation.
        var circuit = new Circuit(provider, scope, renderer, facade, queue);
        _circuits.Add(circuit);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment child = b =>
            {
                b.OpenComponent<MainLayout>(0);
                b.AddAttribute(1, "Body", (RenderFragment)(body => body.AddContent(0, "LAYOUT-BODY-CONTROL")));
                b.AddComponentReferenceCapture(2, c => circuit.Page = (MainLayout)c);
                b.CloseComponent();
            };
            if (!open)
            {
                var principal = FacadeAuthorizationHarness.Principal(UserId);
                circuit.Identity = principal;
                var root = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["Value"] = Task.FromResult(new AuthenticationState(principal)), ["ChildContent"] = child,
                    }));
                circuit.RootHtml = root.ToHtmlString;
            }
            if (open)
            {
                var root = await renderer.RenderComponentAsync<ContentHost>(
                    ParameterView.FromDictionary(new Dictionary<string, object?> { ["ChildContent"] = child }));
                circuit.RootHtml = root.ToHtmlString;
            }
        });
        Assert.NotNull(circuit.Page);
        return circuit;
    }

    private static async Task AssertPopulatedAsync(Circuit c, string question = Question, int count = 1)
    {
        var html = await c.HtmlAsync();
        Assert.Contains(question, html, StringComparison.Ordinal);
        Assert.Contains($"header-bell-badge\">{count}</span>", html, StringComparison.Ordinal);
        Assert.Contains(c.Pending, p => p.Question == question);
        Assert.Equal(count, c.Count);
    }

    private static async Task AssertClearedAsync(Circuit c)
    {
        // Markup first: the markup half independently detects stale content.
        var html = await c.HtmlAsync();
        Assert.DoesNotContain(Question, html, StringComparison.Ordinal);
        Assert.DoesNotContain(Draft, html, StringComparison.Ordinal);
        Assert.DoesNotContain("header-bell-badge", html, StringComparison.Ordinal);
        Assert.Contains("No pending clarifications.", html, StringComparison.Ordinal);
        Assert.Empty(c.Pending);
        Assert.Equal(0, c.Count);
        Assert.Empty(c.Drafts);
        Assert.False(c.Field<bool>("_drawerOpen"));
    }

    [Theory]
    [InlineData(FacadeErrorKind.BadRequest, "Answer is required.")]
    [InlineData(FacadeErrorKind.NotFound, "Clarification 'layout-request' not found.")]
    [InlineData(FacadeErrorKind.Forbidden, Forbidden)]
    public async Task SubmitAnswer_Failure_RetainsDraftAndLoadedContent_WithExactSanitizedError(FacadeErrorKind kind, string message)
    {
        await using var c = await RenderAsync();
        await c.SetDraftAsync(Draft);
        await AssertPopulatedAsync(c);
        var readCallsBeforeAnswer = c.Facade.RequestCalls.Count;
        c.Facade.AnswerOverride = new(false, null, message, kind);
        var hold = c.Facade.ArmAnswer();
        var submit = c.SubmitAsync(); // tracked before ANY rendezvous assertion
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(submit.IsCompleted);
        await c.ReleaseAsync(hold);
        await submit;
        var html = await c.HtmlAsync();
        Assert.Contains(HtmlEncoder.Default.Encode(message), html, StringComparison.Ordinal);
        Assert.Contains(Draft, html, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-EXCEPTION-SECRET", html, StringComparison.Ordinal);
        Assert.Equal(message, c.Field<string>("_answerError"));
        Assert.Equal(Draft, c.Drafts[RequestId]);
        await AssertPopulatedAsync(c);
        Assert.Equal(readCallsBeforeAnswer, c.Facade.RequestCalls.Count); // no silent refresh/clear
        Assert.Same(c.Identity, Assert.Single(c.Facade.AnswerCalls).User);
        Assert.Equal((RequestId, Draft), (c.Facade.AnswerCalls[0].Id, c.Facade.AnswerCalls[0].Answer));
        Assert.Null(c.Queue.GetRequest(RequestId)!.Answer);
    }

    [Fact]
    public async Task SubmitAnswer_Success_RemovesDraft_AndRefreshesPendingState()
    {
        await using var c = await RenderAsync();
        await c.SetDraftAsync(Draft);
        await AssertPopulatedAsync(c);
        var hold = c.Facade.ArmAnswer();
        var submit = c.SubmitAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(submit.IsCompleted);
        await c.ReleaseAsync(hold);
        await submit;
        Assert.False(c.Drafts.ContainsKey(RequestId));
        Assert.Empty(c.Pending);
        Assert.Equal(0, c.Count);
        var html = await c.HtmlAsync();
        Assert.DoesNotContain(Draft, html, StringComparison.Ordinal);
        Assert.DoesNotContain(Question, html, StringComparison.Ordinal);
        Assert.DoesNotContain("clarification-answer-error", html, StringComparison.Ordinal);
        Assert.True(c.Facade.RequestCalls.Count >= 2);
        Assert.Equal(Draft, c.Queue.GetRequest(RequestId)!.Answer);
        Assert.Equal("human", c.Queue.GetRequest(RequestId)!.ResolvedBy);
    }

    [Fact]
    public async Task SubmitAnswer_Cancellation_PropagatesWithoutBannerOrDraftRemoval()
    {
        await using var c = await RenderAsync();
        await c.SetDraftAsync(Draft);
        var hold = c.Facade.ArmAnswer();
        var submit = c.SubmitAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(submit.IsCompleted);
        var cancellation = new OperationCanceledException("RAW-EXCEPTION-SECRET");
        await c.ReleaseAsync(hold, cancellation);
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        Assert.Same(cancellation, thrown);
        c.Observed(submit); // expected fault is consumed, not silently hidden by teardown
        Assert.Equal(Draft, c.Drafts[RequestId]);
        Assert.Null(c.Field<string?>("_answerError"));
        var html = await c.HtmlAsync();
        Assert.DoesNotContain("clarification-answer-error", html, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-EXCEPTION-SECRET", html, StringComparison.Ordinal);
        Assert.Contains(Draft, html, StringComparison.Ordinal);
        await AssertPopulatedAsync(c);
    }

    [Fact]
    public async Task SubmitAnswer_NoCancellationControl_CompletesSuccessfully()
    {
        await using var c = await RenderAsync();
        await c.SetDraftAsync(Draft);
        var hold = c.Facade.ArmAnswer();
        var submit = c.SubmitAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await c.ReleaseAsync(hold);
        await submit;
        Assert.False(c.Drafts.ContainsKey(RequestId));
        Assert.Null(c.Field<string?>("_answerError"));
        Assert.Equal(Draft, c.Queue.GetRequest(RequestId)!.Answer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EventRefresh_EitherSharedReadDenial_ClearsListCountDraftsAndDrawer_NoQueueFallback(bool denyRequests)
    {
        await using var c = await RenderAsync();
        await c.SetDraftAsync(Draft);
        await c.InvokeAsync("OpenDrawer");
        await AssertPopulatedAsync(c);
        Assert.Contains(Draft, await c.HtmlAsync(), StringComparison.Ordinal);
        Assert.True(c.Field<bool>("_drawerOpen"));
        if (denyRequests) c.Facade.RequestOverride = new(false, null, Forbidden, FacadeErrorKind.Forbidden);
        else c.Facade.CountOverride = new(false, 0, Forbidden, FacadeErrorKind.Forbidden);
        var page = c.Page;
        await c.QueueEventRefreshAsync();
        Assert.Same(page, c.Page);
        await AssertClearedAsync(c);
        // The real queue still contains the data: only the facade's denied result controls this root.
        Assert.Single(c.Queue.GetPendingHumanRequests());
        Assert.Null(c.Queue.GetRequest(RequestId)!.Answer);
        await c.QueueEventRefreshAsync();
        await AssertClearedAsync(c);
    }

    [Fact]
    public async Task EventRefresh_AllowedControl_PreservesDraftAndRendersNewFacadeResult()
    {
        await using var c = await RenderAsync();
        await c.SetDraftAsync(Draft);
        c.Facade.RequestOverride = new(true, [Request(question: "NEW-FACADE-ONLY-MARKER")], null, FacadeErrorKind.None);
        c.Facade.CountOverride = new(true, 7, null, FacadeErrorKind.None);
        await c.QueueEventRefreshAsync();
        await AssertPopulatedAsync(c, "NEW-FACADE-ONLY-MARKER", 7);
        Assert.Equal(Draft, c.Drafts[RequestId]);
        Assert.Contains(Draft, await c.HtmlAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(Question, await c.HtmlAsync(), StringComparison.Ordinal);
        Assert.All(c.Facade.RequestCalls, user => Assert.Same(c.Identity, user));
    }

    [Theory]
    [InlineData(UserRole.Member)]
    [InlineData(UserRole.Viewer)]
    public async Task Demotion_AnswerForbiddenButSharedReadsStayAllowed_AndNextEventRepopulates(UserRole role)
    {
        await using var c = await RenderAsync();
        await c.SetDraftAsync(Draft);
        await AssertPopulatedAsync(c);
        await _auth.SetRoleAsync(UserId, role);
        await c.SubmitAsync();
        Assert.Equal(Forbidden, c.Field<string>("_answerError"));
        Assert.Contains(Forbidden, await c.HtmlAsync(), StringComparison.Ordinal);
        Assert.Equal(Draft, c.Drafts[RequestId]);
        Assert.Null(c.Queue.GetRequest(RequestId)!.Answer);
        await AssertPopulatedAsync(c);
        var calls = c.Facade.RequestCalls.Count;
        await c.QueueEventRefreshAsync(Request("second", "SECOND-AUTHORIZED-QUESTION"));
        Assert.True(c.Facade.RequestCalls.Count > calls);
        Assert.All(c.Facade.RequestResults, result => Assert.True(result.Success));
        Assert.All(c.Facade.CountResults, result => Assert.True(result.Success));
        await AssertPopulatedAsync(c, count: 2);
        Assert.Contains("SECOND-AUTHORIZED-QUESTION", await c.HtmlAsync(), StringComparison.Ordinal);
        Assert.Equal(Draft, c.Drafts[RequestId]);
        Assert.False(c.Accessor.IsInvalidated);
    }

    [Fact]
    public async Task OperatorControl_AnswerAndSharedReadsRemainAllowed()
    {
        await using var c = await RenderAsync();
        await c.QueueEventRefreshAsync();
        await AssertPopulatedAsync(c);
        await c.SetDraftAsync(Draft);
        await c.SubmitAsync();
        Assert.Equal(Draft, c.Queue.GetRequest(RequestId)!.Answer);
        Assert.Empty(c.Pending);
        Assert.False(c.Drafts.ContainsKey(RequestId));
    }

    [Fact]
    public async Task Disablement_AnswerAndReadsBothDenied_ThenReenableEventReauthorizes()
    {
        await using var c = await RenderAsync();
        await c.SetDraftAsync(Draft);
        await AssertPopulatedAsync(c);
        await _auth.SetEnabledAsync(UserId, false);
        await c.SubmitAsync();
        Assert.Equal(Forbidden, c.Field<string>("_answerError"));
        Assert.Equal(Draft, c.Drafts[RequestId]); // answer denial itself does NOT clear loaded content
        await AssertPopulatedAsync(c);
        var calls = c.Facade.RequestCalls.Count;
        await c.QueueEventRefreshAsync();
        Assert.Equal(calls + 1, c.Facade.RequestCalls.Count);
        Assert.False(c.Facade.RequestResults[^1].Success);
        Assert.False(c.Facade.CountResults[^1].Success);
        await AssertClearedAsync(c);
        await c.QueueEventRefreshAsync();
        await AssertClearedAsync(c);
        Assert.Single(c.Queue.GetPendingHumanRequests());
        await _auth.SetEnabledAsync(UserId, true);
        await c.QueueEventRefreshAsync();
        await AssertPopulatedAsync(c);
        Assert.True(c.Facade.RequestResults[^1].Success);
        Assert.Equal(string.Empty, c.Drafts[RequestId]);
    }

    [Fact]
    public async Task HeldOlderRefresh_AfterNewerDenial_CannotRestoreStaleContent()
    {
        await using var c = await RenderAsync();
        await c.SetDraftAsync(Draft);
        var hold = c.Facade.ArmCount();
        var older = c.InvokeAsync("RefreshAndRenderAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);
        await _auth.SetEnabledAsync(UserId, false);
        await c.QueueEventRefreshAsync();
        await AssertClearedAsync(c);
        await c.ReleaseAsync(hold);
        await older;
        await AssertClearedAsync(c);
    }

    [Fact]
    public async Task HeldRefresh_NoNewerDenialControl_PublishesAuthorizedResult()
    {
        await using var c = await RenderAsync();
        c.Facade.RequestOverride = new(true, [Request(question: "HELD-CONTROL-MARKER")], null, FacadeErrorKind.None);
        var hold = c.Facade.ArmCount();
        var refresh = c.InvokeAsync("RefreshAndRenderAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(refresh.IsCompleted);
        await c.ReleaseAsync(hold);
        await refresh;
        await AssertPopulatedAsync(c, "HELD-CONTROL-MARKER");
    }

    [Fact]
    public async Task HeldRefresh_AfterDisposal_DoesNotPublishLateResult()
    {
        await using var c = await RenderAsync();
        c.Facade.RequestOverride = new(true, [Request(question: "LATE-DISPOSED-MARKER")], null, FacadeErrorKind.None);
        var hold = c.Facade.ArmCount();
        var refresh = c.InvokeAsync("RefreshAndRenderAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(refresh.IsCompleted);
        await c.DispatchAsync(() => c.Page.Dispose());
        await c.ReleaseAsync(hold);
        await refresh;
        Assert.DoesNotContain("LATE-DISPOSED-MARKER", await c.HtmlAsync(), StringComparison.Ordinal);
        Assert.Single(c.Pending);
        Assert.Equal(Question, c.Pending[0].Question);
    }

    [Fact]
    public async Task OpenMode_NoAuthenticationServices_LocalOperatorReadsAndAnswersThroughFacade()
    {
        await using var c = await RenderAsync(open: true);
        Assert.False(c.Accessor.AuthEnabled);
        Assert.False(c.Accessor.IsInitialized);
        Assert.Null(Assert.Single(c.Facade.RequestCalls));
        await AssertPopulatedAsync(c);
        await c.SetDraftAsync(Draft);
        await c.SubmitAsync();
        Assert.Null(Assert.Single(c.Facade.AnswerCalls).User);
        Assert.Equal(Draft, c.Queue.GetRequest(RequestId)!.Answer);
        Assert.False(c.Drafts.ContainsKey(RequestId));
        Assert.Empty(c.Pending);
        Assert.DoesNotContain(Draft, await c.HtmlAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void SourceMigration_QueueIsOnlyAnEventSource_AdjunctToBehaviorRows()
    {
        var source = File.ReadAllText(Path.Combine(TestRepoRoot.Find(), "src", "CopilotHive", "Components", "Layout", "MainLayout.razor"));
        var code = TestSourceScanner.StripNonCode(source);
        Assert.Contains("ClarificationQueue.OnChanged +=", code, StringComparison.Ordinal);
        Assert.Contains("ClarificationQueue.OnChanged -=", code, StringComparison.Ordinal);
        foreach (var call in new[] { "ClarificationQueue.GetPendingHumanRequests", "ClarificationQueue.PendingHumanCount", "ClarificationQueue.SubmitAnswer", "ClarificationQueue.GetAllRequests", "ClarificationQueue.GetRequest" })
            Assert.DoesNotContain(call, code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleanup_RetainsFaultsAndJoinsLaterTasks_ReportsEachFaultOnce()
    {
        var c = await RenderAsync();
        var fault = new InvalidOperationException("cleanup sentinel");
        _ = c.Track(Task.FromException(fault));
        var later = new TaskCompletionSource();
        _ = c.Track(later.Task);
        later.SetResult();
        await c.DisposeAsync();
        Assert.Same(fault, Assert.Single(c.TakeUnreportedCleanupFailures()));
        Assert.Empty(c.TakeUnreportedCleanupFailures());
        Assert.Equal(2, c.JoinedTasks);
    }

    private sealed class Circuit(ServiceProvider provider, AsyncServiceScope scope, HtmlRenderer renderer,
        ControlledFacade facade, ClarificationQueueService queue) : IAsyncDisposable
    {
        internal MainLayout Page { get; set; } = null!;
        internal Func<string> RootHtml { get; set; } = null!;
        internal ClaimsPrincipal? Identity { get; set; }
        internal CircuitUserPrincipalAccessor Accessor => scope.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>();
        internal ControlledFacade Facade => facade;
        internal ClarificationQueueService Queue => queue;
        internal List<ClarificationRequest> Pending => Field<List<ClarificationRequest>>("_pendingClarifications");
        internal int Count => Field<int>("_pendingClarificationCount");
        internal Dictionary<string, string> Drafts => Field<Dictionary<string, string>>("_clarificationAnswers");
        internal T Field<T>(string name) => (T)typeof(MainLayout).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page)!;
        internal Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(RootHtml);
        internal Task DispatchAsync(Action action) => renderer.Dispatcher.InvokeAsync(action);
        internal async Task SetDraftAsync(string text)
        {
            await DispatchAsync(() => Drafts[RequestId] = text);
            await InvokeAsync("RefreshAndRenderAsync");
            Assert.Contains(text, await HtmlAsync(), StringComparison.Ordinal);
        }
        private readonly List<Task> _tasks = [];
        private readonly HashSet<Task> _observed = [];
        internal int JoinedTasks { get; private set; }
        internal Task Track(Task task) { _tasks.Add(task); return task; }
        internal void Observed(Task task) => _observed.Add(task);
        internal Task SubmitAsync() => InvokeAsync("SubmitClarificationAnswer", RequestId);
        internal Task InvokeAsync(string name, params object?[] args) => Track(renderer.Dispatcher.InvokeAsync(async () =>
        {
            var method = typeof(MainLayout).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (method.Invoke(Page, args) is Task task) await task;
        }));
        internal Task ReleaseAsync(Hold hold, Exception? fault = null) => DispatchAsync(() => hold.Release(fault));
        internal async Task QueueEventRefreshAsync(ClarificationRequest? eventRequest = null)
        {
            var before = facade.CountCalls.Count;
            var hold = facade.ArmCount();
            // Enqueue a NON-human event payload: the event remains real, but it must supply no data.
            await DispatchAsync(() => queue.Enqueue(eventRequest ?? new ClarificationRequest { Id = Guid.NewGuid().ToString(), Question = "EVENT-ONLY-NOT-DATA" }));
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(hold.Result.Task.IsCompleted);
            Assert.Equal(before + 1, facade.CountCalls.Count);
            await ReleaseAsync(hold);
            Assert.True(hold.Result.Task.IsCompletedSuccessfully);
        }
        private bool _disposed;
        private readonly List<Exception> _cleanup = [];
        private int _reported;
        internal Exception[] TakeUnreportedCleanupFailures()
        {
            var result = _cleanup.Skip(_reported).ToArray();
            _reported = _cleanup.Count;
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            async Task Step(Func<Task> action) { try { await action(); } catch (Exception ex) { _cleanup.Add(ex); } }
            // Release on dispatcher, join all started tasks, THEN renderer/scope/provider/directory.
            await Step(() => DispatchAsync(facade.ReleaseAll));
            foreach (var task in _tasks)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
                catch (Exception ex) { if (!_observed.Contains(task)) _cleanup.Add(ex); }
                finally { if (task.IsCompleted) JoinedTasks++; }
            }
            await Step(() => renderer.DisposeAsync().AsTask());
            await Step(() => scope.DisposeAsync().AsTask());
            await Step(() => provider.DisposeAsync().AsTask());
        }
    }

    // Synchronous continuations are deliberate. Releasing on the renderer dispatcher completes the
    // production await inline, including its assignments and StateHasChanged, not just a signal.
    private sealed class Hold
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<FacadeResult<int>> Result { get; } = new();
        internal TaskCompletionSource AnswerRelease { get; } = new();
        internal FacadeResult<int>? CountValue { get; set; }
        internal bool Answer { get; init; }
        internal void Release(Exception? fault = null)
        {
            if (Answer)
            {
                if (fault is null) AnswerRelease.TrySetResult(); else AnswerRelease.TrySetException(fault);
            }
            else if (CountValue is not null) Result.TrySetResult(CountValue);
        }
    }

    private sealed class ControlledFacade(IClarificationFacade real) : IClarificationFacade
    {
        internal List<ClaimsPrincipal?> RequestCalls { get; } = [];
        internal List<ClaimsPrincipal?> CountCalls { get; } = [];
        internal List<(string Id, string Answer, ClaimsPrincipal? User)> AnswerCalls { get; } = [];
        internal List<FacadeResult<IReadOnlyList<ClarificationRequest>>> RequestResults { get; } = [];
        internal List<FacadeResult<int>> CountResults { get; } = [];
        internal FacadeResult<IReadOnlyList<ClarificationRequest>>? RequestOverride { get; set; }
        internal FacadeResult<int>? CountOverride { get; set; }
        internal FacadeResult<ClarificationAnswerResult>? AnswerOverride { get; set; }
        private Hold? _count;
        private Hold? _answer;
        private readonly List<Hold> _holds = [];
        internal Hold ArmCount() { var h = new Hold(); _holds.Add(h); _count = h; return h; }
        internal Hold ArmAnswer() { var h = new Hold { Answer = true }; _holds.Add(h); _answer = h; return h; }
        internal void ReleaseAll() { foreach (var hold in _holds) hold.Release(); }
        public async Task<FacadeResult<IReadOnlyList<ClarificationRequest>>> GetPendingHumanRequestsAsync(ClaimsPrincipal? u, CancellationToken ct)
        {
            RequestCalls.Add(u);
            var result = RequestOverride ?? await real.GetPendingHumanRequestsAsync(u, ct);
            RequestResults.Add(result);
            return result;
        }
        public async Task<FacadeResult<int>> GetPendingHumanCountAsync(ClaimsPrincipal? u, CancellationToken ct)
        {
            CountCalls.Add(u);
            var result = CountOverride ?? await real.GetPendingHumanCountAsync(u, ct);
            CountResults.Add(result);
            var hold = Interlocked.Exchange(ref _count, null);
            if (hold is null) return result;
            hold.CountValue = result;
            hold.Entered.TrySetResult();
            return await hold.Result.Task;
        }
        public async Task<FacadeResult<ClarificationAnswerResult>> SubmitAnswerAsync(string id, string answer, ClaimsPrincipal? u, CancellationToken ct)
        {
            AnswerCalls.Add((id, answer, u));
            var hold = Interlocked.Exchange(ref _answer, null);
            if (hold is not null) { hold.Entered.TrySetResult(); await hold.AnswerRelease.Task; }
            return AnswerOverride ?? await real.SubmitAnswerAsync(id, answer, u, ct);
        }
        public Task<FacadeResult<IReadOnlyList<ClarificationRequest>>> GetAllRequestsAsync(ClaimsPrincipal? u, CancellationToken ct) => real.GetAllRequestsAsync(u, ct);
        public Task<FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>> GetPersistedHistoryAsync(ClaimsPrincipal? u, CancellationToken ct) => real.GetPersistedHistoryAsync(u, ct);
    }

    public sealed class ContentHost : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
    }
    private sealed class Navigation : NavigationManager
    {
        internal Navigation() => Initialize("http://localhost/", "http://localhost/orchestrator");
        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }
}
