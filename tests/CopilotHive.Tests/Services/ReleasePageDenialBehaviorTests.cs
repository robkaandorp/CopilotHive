using System.Reflection;
using System.Security.Claims;

using CopilotHive.Components.Pages;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE RETAINED-COMPONENT BEHAVIOUR CONTRACT for the Releases and Release Detail pages: the REAL
/// <c>Releases.razor</c> / <c>ReleaseDetail.razor</c> components are rendered through the framework's own
/// <see cref="HtmlRenderer"/> over the REAL <see cref="ReleaseFacade"/>, the REAL
/// <see cref="HivePrincipalResolver"/> / <see cref="HivePolicy"/> pair and the REAL
/// <see cref="UserService"/> directory.
/// <para>
/// THE CONTRACT: both pages read the release list, the release and its goals ONLY through the authorized
/// facade, and when a read is DENIED or FAILS they present NO stale release, goal, scope count, edit or
/// validation state — they present a SAFE message instead, and never the misleading "No releases found."
/// (list) or "Release not found." (detail, which keeps that text for a genuine NotFound) answer.
/// </para>
/// <para>
/// EVERY ROW POPULATES THE STATE THROUGH THE PRODUCTION PATH FIRST and asserts the baseline is really
/// there BEFORE triggering the denial; a value that was never populated would make its later "was
/// cleared" assertion vacuous. The denial itself is a REAL directory write (disabling the account) driven
/// through the page's own load path — no sleeps, no polling, no timing assumptions.
/// </para>
/// </summary>
public sealed class ReleasePageDenialBehaviorTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "release-pages-viewer";
    private const string OperatorId = "release-pages-operator";

    /// <summary>The exact sanitized denial text every refusal carries.</summary>
    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    /// <summary>Distinctive markers, so their presence/absence in the markup is unambiguous.</summary>
    private const string RepoTag = "release-pages-repo";
    private const string PlanningTag = "v7.7.7-planning";
    private const string ReleasedTag = "v7.7.7-released";
    private const string PlanningId = "release-pages-planning";
    private const string ReleasedId = "release-pages-released";
    private const string GoalDescription = "RELEASE-PAGE-GOAL-DESCRIPTION";

    /// <summary>
    /// The INTERNAL DETAIL a thrown read carries. It must never reach the UI: a store/facade
    /// exception can hold SQL, paths or connection data, so every thrown-read row asserts this text
    /// is absent.
    /// </summary>
    private const string SecretDetail =
        "SECRET-DETAIL: SqliteException near the Releases projection at /var/state/hive.db";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ══════════════════════════════════════════════════════════════════════════
    //  Host plumbing
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The real store double: releases, their goals and mutations are all recorded.</summary>
    private sealed class PageGoalStore : IGoalStore
    {
        private readonly Dictionary<string, Goal> _goals = new(StringComparer.Ordinal);
        private readonly List<Release> _releases = [];

        /// <summary>EVERY release-list read, so a denied path can be proven not to fall back to the store.</summary>
        internal int ReleaseReads { get; private set; }

        /// <summary>EVERY goal-by-release read, with its release id, in call order.</summary>
        internal List<string> GoalsByReleaseReads { get; } = [];

        /// <summary>Releases passed to <see cref="CreateReleaseAsync"/>.</summary>
        internal List<Release> CreatedReleases { get; } = [];

        /// <summary>Every partial update applied (tag / repositories go through this overload).</summary>
        internal List<ReleaseUpdateData> PartialUpdates { get; } = [];

        /// <summary>
        /// Every FULL-ENTITY update applied. The notes operation mutates the entity and persists it
        /// through this overload, so a notes assertion must observe it here.
        /// </summary>
        internal List<Release> EntityUpdates { get; } = [];

        public string Name => "release-pages-store";

        internal void AddGoal(Goal goal) => _goals[goal.Id] = goal;

        internal void AddRelease(Release release) => _releases.Add(release);

        /// <summary>Removes a release, so a later read really returns a shorter (or empty) list.</summary>
        internal void RemoveRelease(string releaseId) => _releases.RemoveAll(r => r.Id == releaseId);

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList());

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? goal : null);

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            _goals[goal.Id] = goal;
            return Task.FromResult(goal);
        }

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            _goals[goal.Id] = goal;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult(_goals.Remove(goalId));

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
            string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(
                _goals.Values
                    .Where(g => g.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Where(g => statusFilter is null || g.Status == statusFilter)
                    .ToList());

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.Where(g => g.Status == status).ToList());

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(
            string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IterationSummary>>(
                _goals.TryGetValue(goalId, out var goal) ? goal.IterationSummaries : []);

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            if (_goals.TryGetValue(goalId, out var goal))
                goal.Status = status;
            return Task.CompletedTask;
        }

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
        {
            CreatedReleases.Add(release);
            _releases.Add(release);
            return Task.FromResult(release);
        }

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult(_releases.FirstOrDefault(r => r.Id == releaseId));

        /// <inheritdoc />
        /// <remarks>
        /// The store's OWN order is creation-date DESCENDING, exactly like the production
        /// <c>GoalStore</c>, so the page's rendering order is exercised against the real contract.
        /// </remarks>
        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
        {
            ReleaseReads++;
            return Task.FromResult<IReadOnlyList<Release>>(
                _releases.OrderByDescending(r => r.CreatedAt).ToList());
        }

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
        {
            EntityUpdates.Add(release);
            return Task.CompletedTask;
        }

        public Task UpdateReleaseAsync(
            string releaseId, ReleaseUpdateData update, CancellationToken ct = default)
        {
            PartialUpdates.Add(update);
            var existing = _releases.FirstOrDefault(r => r.Id == releaseId);
            if (existing is null)
                throw new KeyNotFoundException($"Release '{releaseId}' not found.");
            if (existing.Status != ReleaseStatus.Planning)
                throw new InvalidOperationException("Only Planning releases can be updated.");
            if (update.Tag is not null) existing.Tag = update.Tag;
            if (update.Notes is not null) existing.Notes = update.Notes;
            if (update.Repositories is not null) existing.RepositoryNames.Clear();
            return Task.CompletedTask;
        }

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(
            string releaseId, CancellationToken ct = default)
        {
            GoalsByReleaseReads.Add(releaseId);
            return Task.FromResult<IReadOnlyList<Goal>>(
                _goals.Values.Where(g => g.ReleaseId == releaseId).ToList());
        }

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
            string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>
            GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
    }

    /// <summary>
    /// A controllable <see cref="IReleaseFacade"/> decorator over the REAL facade. Every operation is
    /// FORWARDED to the real implementation, so a refusal is always the real facade's fresh
    /// directory-backed decision; a row may additionally OVERRIDE one read's result (to synthesize a
    /// non-forbidden failure the real facade would never produce) or ARM A HOLD that parks a read AFTER
    /// its real result was computed (so a genuinely AUTHORIZED, SUCCESSFUL result can be delivered late).
    /// </summary>
    private sealed class ControlledReleaseFacade(ReleaseFacade inner) : IReleaseFacade
    {
        /// <summary>When set, replaces the release-LIST result.</summary>
        internal FacadeResult<IReadOnlyList<ReleaseDto>>? ReleasesOverride { get; set; }

        /// <summary>When set, replaces the goal-by-release result.</summary>
        internal FacadeResult<IReadOnlyList<Goal>>? GoalsOverride { get; set; }

        /// <summary>
        /// When set, the NEXT <see cref="GetReleasesAsync"/> call THROWS this exception instead of returning
        /// a result. One-shot: the arm is consumed by the first call, so a later read behaves normally.
        /// </summary>
        internal Exception? ReleasesThrow { get; private set; }

        /// <summary>When set, the NEXT <see cref="GetReleaseAsync"/> call THROWS this exception.</summary>
        internal Exception? DetailThrow { get; private set; }

        /// <summary>When set, the NEXT <see cref="GetGoalsByReleaseAsync"/> call THROWS this exception.</summary>
        internal Exception? GoalsThrow { get; private set; }

        /// <summary>Arms a one-shot THROW from the release-LIST read.</summary>
        internal void ArmReleasesThrow(Exception exception) => ReleasesThrow = exception;

        /// <summary>Arms a one-shot THROW from the release-DETAIL read.</summary>
        internal void ArmDetailThrow(Exception exception) => DetailThrow = exception;

        /// <summary>Arms a one-shot THROW from the goal-by-release read.</summary>
        internal void ArmGoalsThrow(Exception exception) => GoalsThrow = exception;

        private Hold? _releasesHold;
        private Hold? _detailHold;
        private Hold? _detailGateHold;
        private Hold? _goalsHold;

        /// <summary>
        /// Parks the FIRST release-DETAIL read BEFORE the real facade resolves it. A directory change made
        /// during the pending window therefore decides the outcome of THAT read — which is what lets a row
        /// observe the pending window and then a REFUSED completion without any timing assumption.
        /// </summary>
        internal Hold ArmDetailGate()
        {
            var hold = new Hold();
            Volatile.Write(ref _detailGateHold, hold);
            return hold;
        }

        /// <summary>
        /// Parks the FIRST release-DETAIL read AFTER its real result was computed — the park point for an
        /// A → B parameter reload, where the detail read is the load's first facade call and the pending
        /// window must show neither release.
        /// </summary>
        internal Hold ArmDetail()
        {
            var hold = new Hold();
            Volatile.Write(ref _detailHold, hold);
            return hold;
        }

        /// <summary>
        /// Parks the FIRST release-LIST read AFTER its real result was computed. The generation guard
        /// immediately after that await is the ONLY thing that can stop a late list result from publishing
        /// when the list carries no goals at all (an EMPTY list makes no goal read, so there is no later
        /// await at which a newer generation could be observed).
        /// </summary>
        internal Hold ArmReleases()
        {
            var hold = new Hold();
            Volatile.Write(ref _releasesHold, hold);
            return hold;
        }

        /// <summary>
        /// Parks the FIRST goal-by-release read AFTER its real result was computed: the park point for a
        /// NON-EMPTY list, where the load's last facade read is the goals read.
        /// </summary>
        internal Hold ArmGoals()
        {
            var hold = new Hold();
            Volatile.Write(ref _goalsHold, hold);
            return hold;
        }

        public async Task<FacadeResult<IReadOnlyList<ReleaseDto>>> GetReleasesAsync(
            ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            if (ReleasesThrow is { } thrown)
            {
                ReleasesThrow = null;
                throw thrown;
            }

            var result = ReleasesOverride is { } overridden
                ? overridden
                : await inner.GetReleasesAsync(actingUser, ct);

            var hold = Interlocked.Exchange(ref _releasesHold, null);
            if (hold is null)
                return result;
            hold.Entered.TrySetResult();
            await hold.Release.Task;
            if (hold.ThrowOnRelease is { } onRelease)
                throw onRelease;
            return result;
        }

        public async Task<FacadeResult<ReleaseDto>> GetReleaseAsync(
            string id, ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            var gate = Interlocked.Exchange(ref _detailGateHold, null);
            if (gate is not null)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task;
            }

            if (DetailThrow is { } thrown)
            {
                DetailThrow = null;
                throw thrown;
            }

            var result = await inner.GetReleaseAsync(id, actingUser, ct);

            var hold = Interlocked.Exchange(ref _detailHold, null);
            if (hold is null)
                return result;
            hold.Entered.TrySetResult();
            await hold.Release.Task;
            if (hold.ThrowOnRelease is { } onRelease)
                throw onRelease;
            return result;
        }

        public async Task<FacadeResult<IReadOnlyList<Goal>>> GetGoalsByReleaseAsync(
            string releaseId, ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            if (GoalsThrow is { } thrown)
            {
                GoalsThrow = null;
                throw thrown;
            }

            var result = GoalsOverride is { } overridden
                ? overridden
                : await inner.GetGoalsByReleaseAsync(releaseId, actingUser, ct);

            var hold = Interlocked.Exchange(ref _goalsHold, null);
            if (hold is null)
                return result;
            hold.Entered.TrySetResult();
            await hold.Release.Task;
            if (hold.ThrowOnRelease is { } onRelease)
                throw onRelease;
            return result;
        }

        public Task<ReleaseStatusOutcome> UpdateReleaseStatusAsync(
            string id, UpdateReleaseStatusRequest request, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            inner.UpdateReleaseStatusAsync(id, request, actingUser, ct);

        public Task<FacadeResult<ReleaseDto>> CreateReleaseAsync(
            CreateReleaseRequest request, ClaimsPrincipal? actingUser) =>
            inner.CreateReleaseAsync(request, actingUser);

        public Task<FacadeResult<ReleaseDto>> UpdateReleaseNotesAsync(
            string id, UpdateReleaseNotesRequest request, ClaimsPrincipal? actingUser) =>
            inner.UpdateReleaseNotesAsync(id, request, actingUser);

        public Task<FacadeResult<ReleaseDto>> UpdateReleaseTagAsync(
            string id, UpdateReleaseTagRequest request, ClaimsPrincipal? actingUser) =>
            inner.UpdateReleaseTagAsync(id, request, actingUser);

        public Task<FacadeResult<ReleaseDto>> UpdateReleaseRepositoriesAsync(
            string id, UpdateReleaseRepositoriesRequest request, ClaimsPrincipal? actingUser) =>
            inner.UpdateReleaseRepositoriesAsync(id, request, actingUser);

        public Task<FacadeResult<RemovedResult>> DeleteReleaseAsync(
            string id, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            inner.DeleteReleaseAsync(id, actingUser, ct);

        public Task<FacadeResult<ValidationDto>> ValidateReleaseAsync(
            string id, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            inner.ValidateReleaseAsync(id, actingUser, ct);

        /// <summary>The deterministic rendezvous for a parked read.</summary>
        internal sealed class Hold
        {
            internal TaskCompletionSource Entered { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>
            /// When set, the parked read THROWS this exception as it resumes instead of returning the result
            /// it already computed. This is what makes a STALE-GENERATION THROW reachable: the newer load
            /// publishes first, and only then does the older read fault.
            /// </summary>
            internal Exception? ThrowOnRelease { get; set; }

            internal void LetGo() => Release.TrySetResult();
        }
    }

    /// <summary>The host over the production seam.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        internal Host(ServiceProvider provider, PageGoalStore store, ControlledReleaseFacade facade)
        {
            _provider = provider;
            Store = store;
            Facade = facade;
        }

        internal ServiceProvider Provider => _provider;

        internal PageGoalStore Store { get; }

        internal ControlledReleaseFacade Facade { get; }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    private Host NewHost(HiveAuthMode? mode = null)
    {
        var store = new PageGoalStore();
        var resolver = _auth.Resolver(mode ?? HiveAuthMode.AuthenticationEnabled);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton<IGoalStore>(store);
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton(new HiveConfigFile
        {
            Repositories = [new RepositoryConfig { Name = RepoTag, Url = "https://example.invalid/r", DefaultBranch = "main" }],
        });
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        services.AddSingleton(_auth.Policy);
        services.AddSingleton(resolver);
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));

        var real = new ReleaseFacade(
            store,
            new CopilotHive.Dashboard.DashboardNotifier(),
            NullLogger<ReleaseFacade>.Instance,
            resolver,
            _auth.Policy,
            executionService: null,
            eventBus: null,
            nuGetMonitor: null,
            hiveConfig: null,
            appLifetime: null,
            docCleanup: null);

        var facade = new ControlledReleaseFacade(real);
        services.AddSingleton<IReleaseFacade>(facade);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, store, facade);
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

        internal Task<string> HtmlAsync() => _renderer.Dispatcher.InvokeAsync(_rootHtml);

        /// <summary>Reads a private field of the rendered page.</summary>
        internal object? Field(string name) =>
            _page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_page);

        /// <summary>Sets a private field of the rendered page.</summary>
        internal void SetField(string name, object? value) =>
            _page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_page, value);

        /// <summary>Sets a public component parameter exactly as the framework's parameter set does.</summary>
        internal void SetParameter(string name, object? value) =>
            _page.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.SetValue(_page, value);

        /// <summary>
        /// Invokes a private/protected page method and awaits it. The page's load methods carry no
        /// <c>StateHasChanged</c> call (the framework renders after the lifecycle method), so they are
        /// driven directly — which is what makes an OVERLAPPING completion deterministic.
        /// </summary>
        internal Task InvokeLoadAsync(string methodName)
        {
            var method = _page.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
            return method.Invoke(_page, null) as Task ?? Task.CompletedTask;
        }

        /// <summary>
        /// Invokes a private/protected page method with arguments and returns its result, exactly as the
        /// page's own callbacks do when the rendered control fires.
        /// </summary>
        internal object? Invoke(string methodName, params object?[] args)
        {
            var method = _page.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"'{methodName}' was not found on {_page.GetType().Name}.");
            try
            {
                return method.Invoke(_page, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw; // unreachable
            }
        }

        /// <summary>
        /// Invokes a page EVENT handler ON THE RENDERER'S DISPATCHER, exactly as the rendered control
        /// does. The mutation handlers call <c>StateHasChanged</c> themselves, which asserts dispatcher
        /// access — so a handler must never be driven from the test thread.
        /// </summary>
        internal Task InvokeHandlerAsync(string methodName, params object?[] args) =>
            _renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (Invoke(methodName, args) is Task task)
                    await task;
            });

        /// <summary>Asks the page to re-render, exactly as the framework does after an event handler.</summary>
        /// <summary>Runs one action on the renderer's dispatcher — required to trigger a real render.</summary>
        internal Task DispatchAsync(Action action) => _renderer.Dispatcher.InvokeAsync(action);

        internal Task RequestRenderAsync() => _renderer.Dispatcher.InvokeAsync(() =>
        {
            var stateHasChanged = _page.GetType().GetMethod(
                "StateHasChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(stateHasChanged);
            stateHasChanged!.Invoke(_page, null);
            return Task.CompletedTask;
        });

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            _scope.Dispose();
        }
    }

    private sealed class RenderedPage<T> : IAsyncDisposable
    {
        private readonly Rendered _inner;

        internal RenderedPage(Rendered inner, T page)
        {
            _inner = inner;
            Page = page;
        }

        internal T Page { get; }

        internal Task<string> HtmlAsync() => _inner.HtmlAsync();

        internal object? Field(string name) => _inner.Field(name);

        internal void SetField(string name, object? value) => _inner.SetField(name, value);

        internal void SetParameter(string name, object? value) => _inner.SetParameter(name, value);

        internal Task InvokeLoadAsync(string methodName) => _inner.InvokeLoadAsync(methodName);

        internal object? Invoke(string methodName, params object?[] args) => _inner.Invoke(methodName, args);

        internal Task InvokeHandlerAsync(string methodName, params object?[] args) =>
            _inner.InvokeHandlerAsync(methodName, args);

        internal Task RequestRenderAsync() => _inner.RequestRenderAsync();

        internal Task DispatchAsync(Action action) => _inner.DispatchAsync(action);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private async Task<RenderedPage<TComponent>> RenderAsync<TComponent>(
        Host host,
        string gitHubId,
        IDictionary<string, object?>? parameters = null)
        where TComponent : IComponent
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
                    builder.OpenComponent<TComponent>(0);
                    if (parameters is not null)
                    {
                        foreach (var (key, value) in parameters)
                            builder.AddAttribute(0, key, value);
                    }

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
        return new RenderedPage<TComponent>(
            new Rendered(renderer, scope, rootHtml!, page!), (TComponent)page!);
    }

    /// <summary>
    /// Renders the page the way an OPEN-MODE host does: NO cascading authentication state at all,
    /// exactly as an installation without authentication configured renders it. The facade then
    /// answers with the open-mode local operator, so every authorized read succeeds — the page must
    /// render its content exactly as in authenticated mode, with no authentication services present.
    /// </summary>
    private async Task<RenderedPage<TComponent>> RenderWithoutCascadeAsync<TComponent>(
        Host host,
        IDictionary<string, object?>? parameters = null)
        where TComponent : IComponent
    {
        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        Func<string>? rootHtml = null;
        object? page = null;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment pageFragment = childBuilder =>
            {
                // Sequence numbers are source-fixed literals (the Razor compiler does the same): the page
                // takes at most ONE parameter (ReleaseDetail's ReleaseId), so attribute 1 carries it and
                // the capture sits at 2.
                childBuilder.OpenComponent<TComponent>(0);
                if (parameters is not null)
                {
                    if (parameters.Count > 1)
                        throw new InvalidOperationException("The open-mode host carries at most one parameter.");

                    foreach (var (key, value) in parameters)
                        childBuilder.AddAttribute(1, key, value);
                }

                childBuilder.AddComponentReferenceCapture(2, captured => page = captured);
                childBuilder.CloseComponent();
            };

            var root = await renderer.RenderComponentAsync<OpenModeHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(OpenModeHost.ChildContent)] = pageFragment }));
            rootHtml = root.ToHtmlString;
        });

        Assert.NotNull(rootHtml);
        Assert.NotNull(page);
        return new RenderedPage<TComponent>(
            new Rendered(renderer, scope, rootHtml!, page!), (TComponent)page!);
    }

    /// <summary>
    /// A REAL retained host that owns a mutable route parameter and re-renders its child through the
    /// framework's own parameter-set path. Setting <see cref="ReleaseId"/> and re-rendering is exactly what a
    /// navigation from <c>/releases/A</c> to <c>/releases/B</c> does to the SAME component instance, so the
    /// parameter-update lifecycle (including its pending window) is the production one.
    /// </summary>
    private sealed class DetailParameterHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        private string _releaseId = "";

        /// <summary>
        /// The INITIAL route parameter, supplied at render time exactly as a router supplies it. It seeds
        /// <see cref="_releaseId"/> once; later updates go through <see cref="SetReleaseId"/>.
        /// </summary>
        [Microsoft.AspNetCore.Components.Parameter]
        public string InitialReleaseId { get; set; } = "";

        /// <summary>The page instance the host rendered — retained across parameter updates.</summary>
        internal ReleaseDetail? Page { get; private set; }

        internal string ReleaseId => _releaseId;

        protected override void OnInitialized() => _releaseId = InitialReleaseId;

        /// <summary>
        /// Sets the route parameter and requests a render. MUST be called on the renderer's dispatcher.
        /// </summary>
        internal void SetReleaseId(string releaseId)
        {
            _releaseId = releaseId;
            StateHasChanged();
        }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<ReleaseDetail>(0);
            builder.AddAttribute(1, nameof(ReleaseDetail.ReleaseId), _releaseId);
            builder.AddComponentReferenceCapture(2, captured => Page = (ReleaseDetail)captured);
            builder.CloseComponent();
        }
    }

    /// <summary>A host that renders its child with NO cascading parameter — the open-mode shape.</summary>
    private sealed class OpenModeHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
            builder.AddContent(0, ChildContent);
    }

    /// <summary>
    /// Renders <see cref="DetailParameterHost"/> over the production seam and returns the RETAINED host (with
    /// its page instance) together with the rendered markup accessor and the renderer's dispatcher handle.
    /// The same host is then used to push a REAL parameter update through the framework.
    /// </summary>
    private async Task<(RenderedPage<ReleaseDetail> Page, DetailParameterHost Host)> RenderDetailHostAsync(
        Host host,
        string gitHubId,
        string releaseId)
    {
        var state = Task.FromResult(new AuthenticationState(Principal(gitHubId)));

        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        Func<string>? rootHtml = null;
        DetailParameterHost? parameterHost = null;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var values = new Dictionary<string, object?>
            {
                ["Value"] = state,
                ["ChildContent"] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<DetailParameterHost>(0);
                    builder.AddAttribute(1, nameof(DetailParameterHost.InitialReleaseId), releaseId);
                    builder.AddComponentReferenceCapture(2, captured => parameterHost = (DetailParameterHost)captured);
                    builder.CloseComponent();
                }),
            };

            var root = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                ParameterView.FromDictionary(values));
            rootHtml = root.ToHtmlString;
        });

        Assert.NotNull(rootHtml);
        Assert.NotNull(parameterHost);
        Assert.NotNull(parameterHost!.Page);

        // The host's own BuildRenderTree already passed the initial ReleaseId through the framework's
        // parameter path — no manual parameter injection anywhere in this row.
        Assert.Equal(releaseId, parameterHost.ReleaseId);

        var rendered = new Rendered(renderer, scope, () => rootHtml!(), parameterHost.Page!);
        return (new RenderedPage<ReleaseDetail>(rendered, parameterHost.Page!), parameterHost);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static DateTime At(int day) => new(2024, 5, day, 7, 8, 9, DateTimeKind.Utc);

    /// <summary>
    /// One release fixture. A RELEASED release also carries a <see cref="Release.ReleasedAt"/> date:
    /// <see cref="ReleaseVisibilityService.PartitionGroup"/> puts an UNDATED released release in the
    /// collapsed set, which would hide it behind a toggle and make a markup assertion about it
    /// meaningless.
    /// </summary>
    private static Release NewRelease(
        string id, string tag, ReleaseStatus status, DateTime createdAt,
        List<string>? repositories = null, string? notes = null) =>
        new()
        {
            Id = id,
            Tag = tag,
            Status = status,
            CreatedAt = createdAt,
            ReleasedAt = status == ReleaseStatus.Released ? createdAt.AddDays(2) : null,
            Notes = notes,
            RepositoryNames = repositories ?? [RepoTag],
        };

    /// <summary>Seeds one Planning and one Released release, each carrying a marker goal.</summary>
    private static void SeedReleases(PageGoalStore store)
    {
        store.AddRelease(NewRelease(PlanningId, PlanningTag, ReleaseStatus.Planning, At(6)));
        store.AddRelease(NewRelease(ReleasedId, ReleasedTag, ReleaseStatus.Released, At(5)));
        store.AddGoal(new Goal
        {
            Id = "release-page-goal",
            Description = GoalDescription,
            ReleaseId = PlanningId,
            Scope = GoalScope.Feature,
            Status = GoalStatus.Completed,
            CreatedAt = At(6),
        });
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        internal TestNavigationManager() => Initialize("http://localhost/", "http://localhost/releases");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            // Navigation is not exercised by these rows.
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. Releases.razor — the AUTHORIZED load really populates the page
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SHARED-READ SUCCESS on the Releases page: a VIEWER (the weakest role that holds SharedRead) really
    /// receives the release list AND every release's goals, so the page renders both releases, its
    /// repo-group header, the Feature scope count of the planning release's goal and the assigned-goal
    /// markers. This is the NON-VACUITY CONTROL every denial row below depends on: without it, a "was
    /// cleared" assertion could be satisfied by a page that never loaded anything.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_SharedReadSuccess_RendersTheReleaseListAndItsGoalCounts()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);

        var html = await page.HtmlAsync();
        Assert.Contains(PlanningTag, html, StringComparison.Ordinal);
        Assert.Contains(ReleasedTag, html, StringComparison.Ordinal);
        Assert.Contains(RepoTag, html, StringComparison.Ordinal);
        // The planning release's single FEATURE goal is counted — a (0, 0, 0) fabrication would not be.
        Assert.Contains("\u2728 1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", html, StringComparison.Ordinal);

        // The COUNTS are per-release and real: the planning release carries exactly one FEATURE goal,
        // while the released release legitimately carries none.
        var counts = Assert.IsAssignableFrom<Dictionary<string, (int Breaking, int Feature, int Patch)>>(
            page.Field("_goalCountsByRelease"));
        Assert.Equal((0, 1, 0), counts[PlanningId]);
        Assert.Equal((0, 0, 0), counts[ReleasedId]);
        Assert.Null(page.Field("_loadError"));

        Assert.Equal(2, Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")).Count);
        Assert.Equal(2, Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")).Count);
        Assert.Equal(2, Assert.IsAssignableFrom<Dictionary<string, IReadOnlyList<Goal>>>(page.Field("_goalsByRelease")).Count);
        Assert.True(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));

        // The reads went through the facade (one list read + one goals read per release) — never the
        // store directly, which the page no longer even holds.
        Assert.DoesNotContain(host.Store.GoalsByReleaseReads, id => id != PlanningId && id != ReleasedId);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. Releases.razor — a DISABLED account denies the release-list read
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A DENIED RELEASE-LIST READ CLEARS THE WHOLE PAGE: the account is disabled in the real directory,
    /// the page's OWN load path re-runs, and NOTHING previously loaded is presented — no release card,
    /// no repo group, no scope count, no fabricated <c>(0, 0, 0)</c>, and NEVER "No releases found."
    /// The safe denial text is rendered instead, and the store was NOT re-read as a fallback.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_DeniedReleaseList_ClearsEverythingAndShowsTheSafeDenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);

        // THE NON-VACUITY CONTROL: the authorized load really presented both releases and the count.
        var before = await page.HtmlAsync();
        Assert.Contains(PlanningTag, before, StringComparison.Ordinal);
        Assert.Contains(ReleasedTag, before, StringComparison.Ordinal);
        Assert.Contains("\u2728 1", before, StringComparison.Ordinal);
        Assert.NotEmpty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));

        var storeReadsBefore = host.Store.ReleaseReads;

        // The account is DISABLED in the directory, then the page's OWN load path runs again.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();

        // NO stale release data is presented.
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(RepoTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, after, StringComparison.Ordinal);

        // The EMPTY state is NOT claimed for a denial — and no fabricated scope count is rendered.
        Assert.DoesNotContain("No releases found.", after, StringComparison.Ordinal);
        Assert.DoesNotContain("No goals assigned", after, StringComparison.Ordinal);

        // The safe denial IS surfaced.
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));

        // The page's own loaded state is EMPTY for EVERY value.
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, IReadOnlyList<Goal>>>(page.Field("_goalsByRelease")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, (int Breaking, int Feature, int Patch)>>(page.Field("_goalCountsByRelease")));
        Assert.Empty(Assert.IsAssignableFrom<HashSet<string>>(page.Field("_expandedGroups")));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));

        // NO direct-store fallback: the store's release list was not read again.
        Assert.Equal(storeReadsBefore, host.Store.ReleaseReads);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. Releases.razor — the per-release GOALS read is denied
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A DENIED PER-RELEASE GOAL READ CLEARS THE PAGE TOO — it must not leave the release list rendered
    /// with fabricated <c>(0, 0, 0)</c> counts, which is exactly what the old code did. The goal read is
    /// refused while the release list read still succeeds, so only the goal gate is exercised.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_DeniedGoalsRead_ClearsTheListAndCounts_AndNeverFabricatesZeroCounts()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);

        // THE NON-VACUITY CONTROL: the goal count really rendered.
        var before = await page.HtmlAsync();
        Assert.Contains("\u2728 1", before, StringComparison.Ordinal);
        Assert.NotEmpty(Assert.IsAssignableFrom<Dictionary<string, (int, int, int)>>(page.Field("_goalCountsByRelease")));

        // Only the GOAL read is refused from here on; the release list read still succeeds.
        host.Facade.GoalsOverride = new(false, null, ForbiddenMessage, FacadeErrorKind.Forbidden);

        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();

        // The release list is GONE — a refusal must not leave cards with fabricated zero counts.
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("No goals assigned", after, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", after, StringComparison.Ordinal);
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);

        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, (int Breaking, int Feature, int Patch)>>(page.Field("_goalCountsByRelease")));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4. Releases.razor — a NON-forbidden failure is handled exactly like a denial
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A NON-FORBIDDEN FAILURE (an internal error the real facade would surface only if the store threw)
    /// is handled exactly like a denial: the retained content is cleared and the failure text is shown,
    /// never the empty state.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_FailedReleaseList_ClearsRetainedContentAndShowsTheFailure()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        host.Facade.ReleasesOverride =
            new(false, null, "Releases could not be read.", FacadeErrorKind.Internal);

        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", after, StringComparison.Ordinal);
        Assert.Contains("Releases could not be read.", after, StringComparison.Ordinal);
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  5. Releases.razor — post-denial local callbacks cannot resurrect content
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AFTER A DENIAL, THE LOCAL FILTER/GROUP CALLBACKS CANNOT RESURRECT RETAINED CONTENT: re-applying
    /// the filters and toggling a group key leave the page EMPTY, because <c>_allReleases</c> is gone and
    /// the callbacks refuse to derive anything from a failed page.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_PostDenialFilterAndGroupCallbacks_CannotResurrectContent()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        // A repo group really was expanded BEFORE the denial, so the expansion baseline is populated.
        page.Invoke("ToggleGroup", RepoTag);
        Assert.Contains(RepoTag, Assert.IsAssignableFrom<HashSet<string>>(page.Field("_expandedGroups")));

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();
        Assert.Contains(ForbiddenMessage, await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.Empty(Assert.IsAssignableFrom<HashSet<string>>(page.Field("_expandedGroups")));

        // THE FIRST post-denial callback: the group toggle the rendered button would fire. It must not
        // even RETAIN an expansion key — asserted HERE, before any filter call, because ApplyFilters
        // legitimately clears the set and would hide the retention.
        page.Invoke("ToggleGroup", RepoTag);
        Assert.Empty(Assert.IsAssignableFrom<HashSet<string>>(page.Field("_expandedGroups")));

        // Then both filters, applied through the same callbacks the selects bind to.
        page.SetField("_repoFilter", RepoTag);
        page.Invoke("ApplyFilters");
        page.SetField("_repoFilter", "");
        page.SetField("_statusFilter", nameof(ReleaseStatus.Released));
        page.Invoke("ApplyFilters");
        await page.RequestRenderAsync();

        // NOTHING was resurrected by any of them.
        Assert.Empty(Assert.IsAssignableFrom<HashSet<string>>(page.Field("_expandedGroups")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, (int Breaking, int Feature, int Patch)>>(
            page.Field("_goalCountsByRelease")));

        // The grouping projection is empty too, so the markup cannot render a group either.
        var grouped = Assert.IsAssignableFrom<System.Collections.IEnumerable>(page.Invoke("GetGroupedReleases"));
        Assert.Empty(grouped.Cast<object>());

        var after = await page.HtmlAsync();
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(RepoTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", after, StringComparison.Ordinal);
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  6. Releases.razor — an older successful read cannot overwrite newer denied state
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN OLDER AUTHORIZED READ THAT COMPLETES LATE CANNOT RESTORE STALE CONTENT.
    /// <para>
    /// THE PARK POINT IS DELIBERATE: the hold sits on the release's GOALS read — the LAST facade call a
    /// load makes — so the parked load has already succeeded on its release-LIST read and has no further
    /// read left that could re-deny it. Without the generation guard it would therefore publish a
    /// complete stale page after a newer load was denied; only the guard stops it. (Parking the LIST read
    /// instead would be masked: the resumed load would still have to make its goals read, which the
    /// disabled account would refuse, clearing the page anyway — a vacuous row.)
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReleasesPage_OlderSuccessfulReadCompletingAfterNewerDenial_CannotRestoreStaleContent()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        // EXACTLY ONE release, so its single goals read is the load's LAST read.
        host.Store.AddRelease(NewRelease(PlanningId, PlanningTag, ReleaseStatus.Planning, At(6)));

        // Render WITHOUT the initial load racing: the page loads once, so give it the data first.
        await using var page = await RenderAsync<Releases>(host, ViewerId);
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        // ARM the hold, THEN start the older load: its result is computed by the REAL facade (the
        // account is still authorized at this instant) and parked before publication.
        var hold = host.Facade.ArmGoals();
        var older = page.InvokeLoadAsync("LoadAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        // The account is disabled and a NEWER load runs to completion — and is denied.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var denied = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, denied, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanningTag, denied, StringComparison.Ordinal);
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));

        // The older AUTHORIZED result is now released; it must publish NOTHING.
        hold.LetGo();
        await older;
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(RepoTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", after, StringComparison.Ordinal);
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, (int Breaking, int Feature, int Patch)>>(
            page.Field("_goalCountsByRelease")));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  7. Releases.razor — ordinary creation is unchanged
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ORDINARY CREATION IS UNCHANGED: an OPERATOR really creates a release through the existing facade
    /// call, the created release is inserted into the page's list and rendered, and creation is still
    /// gated on ExecutionOperator (a VIEWER is refused with the sanitized text and no release is
    /// created).
    /// </summary>
    [Fact]
    public async Task ReleasesPage_CreateRelease_StillWorksForAnOperator_AndStaysOperatorGated()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();

        await using var page = await RenderAsync<Releases>(host, OperatorId);

        // The operator creates a release through the page's own handler. The form is OPENED first,
        // exactly as the "+ New Release" button does, so a create error has a surface to render into.
        page.SetField("_showCreateForm", true);
        page.SetField("_createVersion", "v8.8.8");
        page.SetField("_createRepo", RepoTag);
        await page.InvokeLoadAsync("CreateRelease");
        await page.RequestRenderAsync();

        var created = Assert.Single(host.Store.CreatedReleases);
        Assert.Equal("v8.8.8", created.Id);
        Assert.Equal(RepoTag, Assert.Single(created.RepositoryNames));
        Assert.Contains("v8.8.8", await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.Null(page.Field("_createError"));
        Assert.Null(page.Field("_loadError"));
    }

    /// <summary>
    /// CREATION REMAINS <see cref="HiveCapability.ExecutionOperator"/>: a VIEWER is refused by the real
    /// facade, NOTHING is created, and the refusal is surfaced in the create form's own error field —
    /// there is no UI-only authorization substitute.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_CreateRelease_ViewerIsRefused_AndNothingIsCreated()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();

        await using var page = await RenderAsync<Releases>(host, ViewerId);

        // The form is OPENED first, exactly as the "+ New Release" button does.
        page.SetField("_showCreateForm", true);
        page.SetField("_createVersion", "v8.8.8");
        await page.InvokeLoadAsync("CreateRelease");
        await page.RequestRenderAsync();

        Assert.Empty(host.Store.CreatedReleases);
        Assert.Equal(ForbiddenMessage, page.Field("_createError"));
        Assert.Contains(ForbiddenMessage, await page.HtmlAsync(), StringComparison.Ordinal);

        // The LIST read is still authorized for a Viewer — a refused create does not clear it.
        Assert.Null(page.Field("_loadError"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  8. ReleaseDetail.razor — the AUTHORIZED load really populates the page
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SHARED-READ SUCCESS on Release Detail: a VIEWER really receives the release AND its goals, so the
    /// page renders the tag, the release's goal row and its notes, and holds the edit state the parameter
    /// reload must later clear. This is the NON-VACUITY CONTROL for the detail denial rows.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_SharedReadSuccess_RendersTheReleaseAndItsGoals()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);
        // A release WITH notes, so the page really renders a non-empty notes surface.
        host.Store.AddRelease(NewRelease(
            "notes-release", "v6.6.6", ReleaseStatus.Planning, At(4), [RepoTag], notes: "RELEASE-PAGE-NOTES-MARKER"));

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });

        var html = await page.HtmlAsync();
        Assert.Contains(PlanningTag, html, StringComparison.Ordinal);
        Assert.Contains(GoalDescription, html, StringComparison.Ordinal);
        Assert.Contains(RepoTag, html, StringComparison.Ordinal);
        Assert.Null(page.Field("_loadError"));
        Assert.NotNull(page.Field("_release"));
        Assert.Single(Assert.IsAssignableFrom<List<Goal>>(page.Field("_goals")));
        Assert.Equal(PlanningTag, page.Field("_editTag"));
        Assert.NotEmpty(Assert.IsAssignableFrom<HashSet<string>>(page.Field("_selectedRepos")));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  9. ReleaseDetail.razor — a denial clears EVERY retained value
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A DENIED DETAIL READ CLEARS EVERY RETAINED VALUE — the release, its goals, the notes/tag/repository
    /// edit state, the saved flags, the validation and execution results and the per-operation errors —
    /// and shows a SAFE denial message that is NOT the misleading "Release not found.".
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_DeniedRead_ClearsEveryRetainedValue_AndShowsTheSafeDenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });

        // THE NON-VACUITY CONTROL: the authorized load really presented the release and its goal...
        var before = await page.HtmlAsync();
        Assert.Contains(PlanningTag, before, StringComparison.Ordinal);
        Assert.Contains(GoalDescription, before, StringComparison.Ordinal);
        Assert.NotEmpty(Assert.IsAssignableFrom<HashSet<string>>(page.Field("_selectedRepos")));

        // ...and EVERY advertised retained value is really populated before the denial.
        page.SetField("_notesSaved", true);
        page.SetField("_notesError", "pre-existing notes error");
        page.SetField("_tagSaved", true);
        page.SetField("_tagError", "pre-existing tag error");
        page.SetField("_repositoriesSaved", true);
        page.SetField("_repositoriesError", "pre-existing repositories error");
        page.SetField("_isValid", true);
        page.SetField("_validationErrors", new List<string> { "pre-existing validation error" });
        page.SetField("_releaseResult", new ReleaseExecutionResultDto(false, [], "pre-existing", ReleaseExecutionFailure.Execution));
        page.SetField("_releaseError", "pre-existing release error");
        page.SetField("_executionFailed", true);
        page.SetField("_deleteError", "pre-existing delete error");

        // The account is DISABLED, then the page's OWN load path runs again.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();

        // NO stale release / goal / edit data is presented.
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, after, StringComparison.Ordinal);
        Assert.DoesNotContain(RepoTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("pre-existing validation error", after, StringComparison.Ordinal);
        Assert.DoesNotContain("pre-existing release error", after, StringComparison.Ordinal);
        Assert.DoesNotContain("pre-existing delete error", after, StringComparison.Ordinal);
        Assert.DoesNotContain("Validation passed", after, StringComparison.Ordinal);

        // NOT the misleading not-found answer for a refusal; the SAFE denial instead — and the HEADER
        // does not claim the release is missing either.
        Assert.DoesNotContain("Release not found.", after, StringComparison.Ordinal);
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));

        var header = host.Provider.GetRequiredService<PageHeaderState>();
        Assert.Equal("Release Unavailable", header.Title);

        // EVERY retained value is cleared.
        Assert.Null(page.Field("_release"));
        Assert.Empty(Assert.IsAssignableFrom<List<Goal>>(page.Field("_goals")));
        Assert.Equal("", page.Field("_editNotes"));
        Assert.Equal("", page.Field("_editTag"));
        Assert.Empty(Assert.IsAssignableFrom<HashSet<string>>(page.Field("_selectedRepos")));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_notesSaved")));
        Assert.Null(page.Field("_notesError"));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_tagSaved")));
        Assert.Null(page.Field("_tagError"));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_repositoriesSaved")));
        Assert.Null(page.Field("_repositoriesError"));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_isValid")));
        Assert.Empty(Assert.IsAssignableFrom<List<string>>(page.Field("_validationErrors")));
        Assert.Null(page.Field("_releaseResult"));
        Assert.Null(page.Field("_releaseError"));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_executionFailed")));
        Assert.Null(page.Field("_deleteError"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  10. ReleaseDetail.razor — NotFound keeps its own message, a failure does not
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A GENUINE <see cref="FacadeErrorKind.NotFound"/> KEEPS the page's own "Release not found."
    /// message — the safe-error surface must not swallow the real not-found answer.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_NotFound_KeepsTheReleaseNotFoundMessage()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = "no-such-release" });

        var html = await page.HtmlAsync();
        Assert.Contains("Release not found.", html, StringComparison.Ordinal);
        Assert.DoesNotContain(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.Null(page.Field("_loadError"));
        Assert.Null(page.Field("_release"));

        // A GENUINE not-found keeps its own header.
        Assert.Equal("Release Not Found", host.Provider.GetRequiredService<PageHeaderState>().Title);
    }

    /// <summary>
    /// A NON-NOTFOUND FAILURE shows its OWN message rather than claiming the release does not exist, and
    /// clears the retained state exactly like a denial does.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_FailedRead_ShowsTheFailure_NotReleaseNotFound()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        // The goal read fails with a NON-forbidden, NON-not-found failure: the release must not be
        // presented either, because the page may not read its goals. The text is HTML-safe so the
        // markup comparison is about the MESSAGE, not about escaping.
        const string goalsFailure = "Release goals could not be read.";
        host.Facade.GoalsOverride = new(false, null, goalsFailure, FacadeErrorKind.Internal);

        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, after, StringComparison.Ordinal);
        Assert.DoesNotContain("Release not found.", after, StringComparison.Ordinal);
        Assert.Contains(goalsFailure, after, StringComparison.Ordinal);
        Assert.Null(page.Field("_release"));
        Assert.Empty(Assert.IsAssignableFrom<List<Goal>>(page.Field("_goals")));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  11. ReleaseDetail.razor — a parameter reload clears the previous release
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A PARAMETER RELOAD CLEARS EVERY PREVIOUSLY RETAINED VALUE: navigating from one release to another
    /// re-runs the load, which first clears the old release, its goals, its edit state and its validation
    /// and execution results — so nothing from the previous route parameter survives.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_ParameterReload_ClearsThePreviousReleaseAndItsEditState()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });

        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);
        page.SetField("_isValid", true);
        page.SetField("_validationErrors", new List<string> { "STALE-VALIDATION-MARKER" });
        page.SetField("_releaseError", "STALE-RELEASE-ERROR-MARKER");
        page.SetField("_notesError", "STALE-NOTES-ERROR-MARKER");

        // The route parameter CHANGES, exactly as a navigation to another release sets it, and the
        // framework re-runs the parameter hook.
        page.SetParameter("ReleaseId", ReleasedId);
        await page.InvokeLoadAsync("OnParametersSetAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();

        // The NEW release is presented...
        Assert.Contains(ReleasedTag, after, StringComparison.Ordinal);
        // ...and NOTHING from the previous parameter survives.
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, after, StringComparison.Ordinal);
        Assert.DoesNotContain("STALE-VALIDATION-MARKER", after, StringComparison.Ordinal);
        Assert.DoesNotContain("STALE-RELEASE-ERROR-MARKER", after, StringComparison.Ordinal);
        Assert.DoesNotContain("STALE-NOTES-ERROR-MARKER", after, StringComparison.Ordinal);

        Assert.Equal(ReleasedTag, Assert.IsAssignableFrom<ReleaseDto>(page.Field("_release")).Tag);
        Assert.Empty(Assert.IsAssignableFrom<List<Goal>>(page.Field("_goals")));
        Assert.Equal(ReleasedTag, page.Field("_editTag"));
        Assert.Equal("", page.Field("_editNotes"));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_isValid")));
        Assert.Empty(Assert.IsAssignableFrom<List<string>>(page.Field("_validationErrors")));
        Assert.Null(page.Field("_releaseError"));
        Assert.Null(page.Field("_notesError"));
    }

    /// <summary>
    /// AN OLDER AUTHORIZED DETAIL READ FOR RELEASE A CANNOT OVERWRITE RELEASE B. The parameter changes
    /// while an older load is parked on its goals read; the NEWER load for the Released release runs to
    /// completion and publishes Release B; the parked older load then completes — and must publish
    /// NOTHING, so the page still shows Release B. This is the A→B twin of the denial case above: the
    /// newer load is authorized and succeeds, so nothing but the generation guard can keep the older
    /// load from clobbering it.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_OlderReadCompletingAfterNewerParameterLoad_CannotOverwriteIt()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        // Arm the hold, then start the older load for PLANNING — it parks on its (last) goals read.
        var hold = host.Facade.ArmGoals();
        var older = page.InvokeLoadAsync("LoadAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        // The parameter changes to the RELEASED release; the newer load runs to completion and wins.
        page.SetParameter("ReleaseId", ReleasedId);
        await page.InvokeLoadAsync("OnParametersSetAsync");
        await page.RequestRenderAsync();

        var afterNewer = await page.HtmlAsync();
        Assert.Contains(ReleasedTag, afterNewer, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanningTag, afterNewer, StringComparison.Ordinal);
        Assert.Equal(ReleasedTag, Assert.IsAssignableFrom<ReleaseDto>(page.Field("_release")).Tag);

        // The older load is released; it must publish NOTHING.
        hold.LetGo();
        await older;
        await page.RequestRenderAsync();

        var afterOlder = await page.HtmlAsync();
        Assert.Contains(ReleasedTag, afterOlder, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanningTag, afterOlder, StringComparison.Ordinal);
        Assert.Equal(ReleasedTag, Assert.IsAssignableFrom<ReleaseDto>(page.Field("_release")).Tag);
        Assert.Null(page.Field("_loadError"));
        Assert.True(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));
    }

    /// <summary>
    /// THE SAME PARAMETER DOES NOT RE-READ: the hook is a real reload guard, so an ordinary re-render does
    /// not issue another read — and the guard is released the moment the parameter really changes.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_UnchangedParameter_DoesNotReRead()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });

        var goalsReadsBefore = host.Store.GoalsByReleaseReads.Count;

        await page.InvokeLoadAsync("OnParametersSetAsync");
        Assert.Equal(goalsReadsBefore, host.Store.GoalsByReleaseReads.Count);

        // A REAL parameter change re-reads.
        page.SetParameter("ReleaseId", ReleasedId);
        await page.InvokeLoadAsync("OnParametersSetAsync");
        Assert.Equal(goalsReadsBefore + 1, host.Store.GoalsByReleaseReads.Count);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  12. ReleaseDetail.razor — overlapping reads cannot publish stale state
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN OLDER AUTHORIZED DETAIL READ THAT COMPLETES LATE CANNOT RESTORE STALE CONTENT.
    /// <para>
    /// THE PARK POINT IS DELIBERATE: the hold sits on the release's GOALS read — the LAST facade call a
    /// load makes — so the parked load has already succeeded on its release-DETAIL read and has no
    /// further read left that could re-deny it. Without the generation guard it would republish the whole
    /// stale release after a newer load was denied. (Parking the DETAIL read instead would be masked: the
    /// resumed load would still make its goals read, which the disabled account would refuse, clearing
    /// the page anyway — a vacuous row.)
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_OlderSuccessfulReadCompletingAfterNewerDenial_CannotRestoreStaleContent()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        // EXACTLY one marker goal, so the load's single goals read is its LAST read.
        host.Store.AddRelease(NewRelease(PlanningId, PlanningTag, ReleaseStatus.Planning, At(6)));
        host.Store.AddGoal(new Goal
        {
            Id = "release-page-goal",
            Description = GoalDescription,
            ReleaseId = PlanningId,
            Scope = GoalScope.Feature,
            Status = GoalStatus.Completed,
            CreatedAt = At(6),
        });

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        // ARM the hold, then start the older load: the real facade computes the AUTHORIZED result and
        // the decorator parks it before publication.
        var hold = host.Facade.ArmGoals();
        var older = page.InvokeLoadAsync("LoadAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        // The account is disabled and a NEWER load runs to completion — and is denied.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();
        Assert.Contains(ForbiddenMessage, await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.Null(page.Field("_release"));

        // The older AUTHORIZED result is released; it must publish NOTHING.
        hold.LetGo();
        await older;
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, after, StringComparison.Ordinal);
        Assert.DoesNotContain("Release not found.", after, StringComparison.Ordinal);
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.Null(page.Field("_release"));
        Assert.Empty(Assert.IsAssignableFrom<List<Goal>>(page.Field("_goals")));
        Assert.Equal("", page.Field("_editTag"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  13. ReleaseDetail.razor — ordinary mutations are unchanged
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ORDINARY MUTATIONS ARE UNCHANGED: an OPERATOR's Save Notes and Save Tag still go through the
    /// existing facade calls, replace <c>_release</c> with the returned DTO, set their own "saved" flag and
    /// re-render — and a VIEWER's attempt is refused by the real facade (ExecutionOperator) with that
    /// editor's own error field populated and nothing persisted.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_OrdinaryMutations_StillWorkForAnOperator_AndStayOperatorGated()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var operatorPage = await RenderAsync<ReleaseDetail>(
            host, OperatorId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });

        operatorPage.SetField("_editNotes", "OPERATOR-NOTES-MARKER");
        await operatorPage.InvokeHandlerAsync("SaveNotes");

        Assert.True(Assert.IsAssignableFrom<bool>(operatorPage.Field("_notesSaved")));
        Assert.Null(operatorPage.Field("_notesError"));
        Assert.Equal("OPERATOR-NOTES-MARKER", Assert.IsAssignableFrom<ReleaseDto>(operatorPage.Field("_release")).Notes);

        operatorPage.SetField("_editTag", "v7.7.8-operator");
        await operatorPage.InvokeHandlerAsync("SaveTag");

        Assert.True(Assert.IsAssignableFrom<bool>(operatorPage.Field("_tagSaved")));
        Assert.Null(operatorPage.Field("_tagError"));
        Assert.Equal("v7.7.8-operator", Assert.IsAssignableFrom<ReleaseDto>(operatorPage.Field("_release")).Tag);

        // The writes really reached the store through the facade: NOTES is persisted through the
        // FULL-ENTITY overload (the facade mutates the entity), TAG through the PARTIAL one.
        Assert.Contains(host.Store.EntityUpdates, r => r.Notes == "OPERATOR-NOTES-MARKER");
        Assert.Contains(host.Store.PartialUpdates, u => u.Tag == "v7.7.8-operator");
        var entityUpdates = host.Store.EntityUpdates.Count;
        var partialUpdates = host.Store.PartialUpdates.Count;

        // ── A VIEWER is refused by the REAL facade: the mutation stays ExecutionOperator. ──
        await using var viewerPage = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });

        viewerPage.SetField("_editNotes", "VIEWER-NOTES-MARKER");
        await viewerPage.InvokeHandlerAsync("SaveNotes");
        // The FAILURE path does not call StateHasChanged itself, so the framework's post-handler render
        // is requested explicitly — exactly the render the real control's event would produce.
        await viewerPage.RequestRenderAsync();

        Assert.Equal(ForbiddenMessage, viewerPage.Field("_notesError"));
        Assert.False(Assert.IsAssignableFrom<bool>(viewerPage.Field("_notesSaved")));
        Assert.Contains(ForbiddenMessage, await viewerPage.HtmlAsync(), StringComparison.Ordinal);

        // Nothing was persisted by the refused write, and the viewer's READ still rendered the release.
        Assert.Equal(entityUpdates, host.Store.EntityUpdates.Count);
        Assert.Equal(partialUpdates, host.Store.PartialUpdates.Count);
        Assert.DoesNotContain(host.Store.EntityUpdates, r => r.Notes == "VIEWER-NOTES-MARKER");
        Assert.Null(viewerPage.Field("_loadError"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  14. Open mode — no cascade at all, the pages still render their content
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OPEN MODE really renders the Releases page with NO cascading authentication state and NO
    /// authentication services at all: the facade answers for the open-mode local operator, the page
    /// lists the releases and their goal counts exactly as in authenticated mode. This is the shape an
    /// installation without authentication configured actually renders — if the page demanded a cascade
    /// or an authenticated principal, this row would fail.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_OpenMode_NoCascade_ListsReleasesAndCounts()
    {
        await using var host = NewHost(HiveAuthMode.Open);
        SeedReleases(host.Store);

        await using var page = await RenderWithoutCascadeAsync<Releases>(host);

        var html = await page.HtmlAsync();
        Assert.Contains(PlanningTag, html, StringComparison.Ordinal);
        Assert.Contains(ReleasedTag, html, StringComparison.Ordinal);
        Assert.Contains(RepoTag, html, StringComparison.Ordinal);
        Assert.Contains("✨ 1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", html, StringComparison.Ordinal);
        Assert.Null(page.Field("_loadError"));
        Assert.Equal(2, Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")).Count);
        Assert.Equal((0, 1, 0), Assert.IsAssignableFrom<Dictionary<string, (int Breaking, int Feature, int Patch)>>(
            page.Field("_goalCountsByRelease"))[PlanningId]);
    }

    /// <summary>
    /// OPEN MODE really renders the ReleaseDetail page with NO cascading authentication state at all:
    /// the release and its goals load and the edit surface is populated, exactly as for an
    /// authenticated viewer.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_OpenMode_NoCascade_RendersTheReleaseAndItsGoals()
    {
        await using var host = NewHost(HiveAuthMode.Open);
        SeedReleases(host.Store);

        await using var page = await RenderWithoutCascadeAsync<ReleaseDetail>(
            host, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });

        var html = await page.HtmlAsync();
        Assert.Contains(PlanningTag, html, StringComparison.Ordinal);
        Assert.Contains(GoalDescription, html, StringComparison.Ordinal);
        Assert.Contains(RepoTag, html, StringComparison.Ordinal);
        Assert.Null(page.Field("_loadError"));
        Assert.NotNull(page.Field("_release"));
        Assert.Single(Assert.IsAssignableFrom<List<Goal>>(page.Field("_goals")));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  15. A denial on the VERY FIRST load renders the safe state, never stale or empty claims
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A COLD-START DENIAL ON THE RELEASES PAGE leaves nothing behind: the very first render is the
    /// denial — no card, no group, no fabricated <c>(0, 0, 0)</c>, no "No releases found." — with the
    /// safe denial surfaced instead. (The denial rows above disable the account AFTER a populated
    /// load; this row proves the same holding when there is no prior content at all.)
    /// </summary>
    [Fact]
    public async Task ReleasesPage_DeniedOnFirstLoad_ShowsTheDenialAndNeverAEmptyOrStaleState()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);

        var html = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanningTag, html, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedTag, html, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No goals assigned", html, StringComparison.Ordinal);
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, IReadOnlyList<Goal>>>(page.Field("_goalsByRelease")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, (int Breaking, int Feature, int Patch)>>(page.Field("_goalCountsByRelease")));
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));

        // The store was NEVER read: the refusal happens BEFORE any goal-store access.
        Assert.Equal(0, host.Store.ReleaseReads);
        Assert.Empty(host.Store.GoalsByReleaseReads);
    }

    /// <summary>
    /// A COLD-START DENIAL ON THE DETAIL PAGE renders only the safe denial: no release, no goals, no
    /// "Release not found." (which is reserved for a genuine NotFound) and nothing retained.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_DeniedOnFirstLoad_ShowsTheDenial_NotReleaseNotFound()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });

        var html = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Release not found.", html, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanningTag, html, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, html, StringComparison.Ordinal);
        Assert.Null(page.Field("_release"));
        Assert.Empty(Assert.IsAssignableFrom<List<Goal>>(page.Field("_goals")));
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));
        Assert.Equal("Release Unavailable", host.Provider.GetRequiredService<PageHeaderState>().Title);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  16. A non-forbidden GOALS failure on the Releases page reports ITS OWN text
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A NON-FORBIDDEN GOALS FAILURE shows the facade's own error text rather than the release-list
    /// fallback text, so a reader can tell a goals-read failure apart from a list-read failure — and
    /// still never sees fabricated counts or the empty state.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_FailedGoalsRead_ShowsThatFailure_NotTheListFallbackText()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        const string goalsFailure = "Release goals could not be read.";
        host.Facade.GoalsOverride = new(false, null, goalsFailure, FacadeErrorKind.Internal);

        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var html = await page.HtmlAsync();
        Assert.Contains(goalsFailure, html, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", html, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanningTag, html, StringComparison.Ordinal);
        Assert.Equal(goalsFailure, page.Field("_loadError"));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, (int Breaking, int Feature, int Patch)>>(
            page.Field("_goalCountsByRelease")));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  17. THROWN reads (a faulted store/facade task, not a synthesized Internal result)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A THROWN release-LIST read is handled exactly like a returned failure, with a SANITIZED message.
    /// <para>
    /// THIS ROW ARMS A REAL THROW from the facade read — a faulted task carrying an exception whose text
    /// looks like internal detail — not a synthesized <see cref="FacadeErrorKind.Internal"/> result. Without
    /// the handling the exception escapes the loader, <c>_loadError</c> stays null and the PREVIOUS cards and
    /// counts remain on screen: the stale-presentation failure this row pins.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReleasesPage_ThrownReleasesRead_ClearsRetainedStateAndShowsASanitizedError()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);

        // THE NON-VACUITY CONTROL: the authorized load really presented both releases and the count.
        var before = await page.HtmlAsync();
        Assert.Contains(PlanningTag, before, StringComparison.Ordinal);
        Assert.Contains(ReleasedTag, before, StringComparison.Ordinal);
        Assert.NotEmpty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.Null(page.Field("_loadError"));

        // A REAL faulted read: the exception text is internal detail that must never reach the UI.
        host.Facade.ArmReleasesThrow(new InvalidOperationException(SecretDetail));

        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();

        // The retained cards, counts and groups are GONE...
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(RepoTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("No goals assigned", after, StringComparison.Ordinal);

        // ...the EMPTY state is not claimed for a failure...
        Assert.DoesNotContain("No releases found.", after, StringComparison.Ordinal);

        // ...and the page reports a SANITIZED error: the exception's own text is NEVER rendered.
        Assert.DoesNotContain(SecretDetail, after, StringComparison.Ordinal);
        Assert.Contains("Releases could not be loaded.", after, StringComparison.Ordinal);
        Assert.Equal("Releases could not be loaded.", page.Field("_loadError"));

        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, (int Breaking, int Feature, int Patch)>>(
            page.Field("_goalCountsByRelease")));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));
    }

    /// <summary>
    /// A THROWN goal-by-release read is handled the same way, with ITS OWN sanitized message: the failing
    /// read site is identifiable and the exception text is never rendered.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_ThrownGoalsRead_ClearsRetainedStateAndShowsItsOwnSanitizedError()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        host.Facade.ArmGoalsThrow(new InvalidOperationException(SecretDetail));

        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", after, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretDetail, after, StringComparison.Ordinal);
        Assert.Contains("Release goals could not be loaded.", after, StringComparison.Ordinal);
        Assert.Equal("Release goals could not be loaded.", page.Field("_loadError"));

        // The GOALS text is not the LIST text: the two read sites report distinctly.
        Assert.NotEqual("Releases could not be loaded.", page.Field("_loadError"));

        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));
    }

    /// <summary>
    /// A STALE THROWN release-LIST read must not clobber a NEWER load. An older list read is parked, a newer
    /// load is refused and publishes its denial, and the older read then FAULTS as it resumes: because it is
    /// no longer the active generation it may publish nothing — the newer denial and its message must
    /// survive untouched.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_StaleThrownReleasesRead_CannotClobberNewerDenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        // The OLDER list read is parked after its authorized result was computed, then faults on release.
        var hold = host.Facade.ArmReleases();
        hold.ThrowOnRelease = new InvalidOperationException(SecretDetail);
        var older = page.InvokeLoadAsync("LoadAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        // A NEWER load runs while the account is disabled: it is refused and publishes the denial.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));
        Assert.Contains(ForbiddenMessage, await page.HtmlAsync(), StringComparison.Ordinal);

        // The older read now FAULTS: nothing may change.
        hold.LetGo();
        await older;
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();

        // The NEWER denial and its message survive — the stale throw did NOT overwrite them...
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);

        // ...and the stale throw's own text never appears.
        Assert.DoesNotContain(SecretDetail, after, StringComparison.Ordinal);
        Assert.DoesNotContain("Releases could not be loaded.", after, StringComparison.Ordinal);

        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// A STALE THROWN goal-by-release read must not clobber a NEWER load either.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_StaleThrownGoalsRead_CannotClobberNewerDenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();

        // EXACTLY one release, so the parked goals read is the load's LAST read.
        host.Store.AddRelease(NewRelease(PlanningId, PlanningTag, ReleaseStatus.Planning, At(6)));

        await using var page = await RenderAsync<Releases>(host, ViewerId);
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        var hold = host.Facade.ArmGoals();
        hold.ThrowOnRelease = new InvalidOperationException(SecretDetail);
        var older = page.InvokeLoadAsync("LoadAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));

        hold.LetGo();
        await older;
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretDetail, after, StringComparison.Ordinal);
        Assert.DoesNotContain("Release goals could not be loaded.", after, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// A THROWN release-DETAIL read clears everything and shows a SANITIZED message with the UNAVAILABLE
    /// header — never "Release not found." and never the exception's own text.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_ThrownDetailRead_ClearsEverythingAndShowsASanitizedError()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        host.Facade.ArmDetailThrow(new InvalidOperationException(SecretDetail));

        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, after, StringComparison.Ordinal);
        Assert.DoesNotContain("Release not found.", after, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretDetail, after, StringComparison.Ordinal);
        Assert.Contains("The release could not be loaded.", after, StringComparison.Ordinal);
        Assert.Equal("The release could not be loaded.", page.Field("_loadError"));

        Assert.Null(page.Field("_release"));
        Assert.Empty(Assert.IsAssignableFrom<List<Goal>>(page.Field("_goals")));
        Assert.Empty(Assert.IsAssignableFrom<HashSet<string>>(page.Field("_selectedRepos")));
        Assert.Equal("Release Unavailable", host.Provider.GetRequiredService<PageHeaderState>().Title);
    }

    /// <summary>
    /// A THROWN goal-by-release read on the detail page is handled the same way, with ITS OWN sanitized
    /// message, and the release is cleared because its goals could not be read.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_ThrownGoalsRead_ClearsEverythingAndShowsItsOwnSanitizedError()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        host.Facade.ArmGoalsThrow(new InvalidOperationException(SecretDetail));

        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, after, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretDetail, after, StringComparison.Ordinal);

        // The apostrophe is HTML-encoded in the markup, so the EXACT text is asserted on the state while the
        // markup is asserted on an apostrophe-free prefix — the same message, without an escaping mismatch.
        Assert.Equal("The release's goals could not be loaded.", page.Field("_loadError"));
        Assert.Contains("goals could not be loaded.", after, StringComparison.Ordinal);
        Assert.Null(page.Field("_release"));
        Assert.Equal("Release Unavailable", host.Provider.GetRequiredService<PageHeaderState>().Title);
    }

    /// <summary>
    /// A STALE THROWN release-DETAIL read must not clobber a NEWER load: the newer refusal's state and
    /// message survive the older read's fault, and the fault's text never appears.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_StaleThrownDetailRead_CannotClobberNewerDenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        var hold = host.Facade.ArmDetail();
        hold.ThrowOnRelease = new InvalidOperationException(SecretDetail);
        var older = page.InvokeLoadAsync("LoadAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));
        Assert.Equal("Release Unavailable", host.Provider.GetRequiredService<PageHeaderState>().Title);

        hold.LetGo();
        await older;
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretDetail, after, StringComparison.Ordinal);
        Assert.DoesNotContain("The release could not be loaded.", after, StringComparison.Ordinal);
        Assert.Null(page.Field("_release"));
        Assert.Equal("Release Unavailable", host.Provider.GetRequiredService<PageHeaderState>().Title);
    }

    /// <summary>
    /// A STALE THROWN goal-by-release read on the detail page must not clobber a NEWER load.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_StaleThrownGoalsRead_CannotClobberNewerDenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();

        // EXACTLY one release, so the parked goals read is the load's LAST read.
        host.Store.AddRelease(NewRelease(PlanningId, PlanningTag, ReleaseStatus.Planning, At(6)));

        await using var page = await RenderAsync<ReleaseDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["ReleaseId"] = PlanningId });
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        var hold = host.Facade.ArmGoals();
        hold.ThrowOnRelease = new InvalidOperationException(SecretDetail);
        var older = page.InvokeLoadAsync("LoadAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));

        hold.LetGo();
        await older;
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretDetail, after, StringComparison.Ordinal);
        Assert.DoesNotContain("The release's goals could not be loaded.", after, StringComparison.Ordinal);
        Assert.Null(page.Field("_release"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  18. The filter SELECTIONS are retained state too: a denial resets them
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A DENIAL RESETS BOTH FILTER SELECTIONS, and a LATER AUTHORIZED REFRESH therefore shows EVERY release.
    /// <para>
    /// The selections are part of the retained state: leaving them set keeps a stale selection visible in the
    /// controls AND silently constrains the recovered page to the old selection — a page that reads all
    /// releases but presents only the matching subset. This row populates BOTH filters, denies, then
    /// re-authorizes on the SAME instance and proves the recovered page is unconstrained.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReleasesPage_DenialResetsBothFilterSelections_AndARecoveredRefreshShowsAllReleases()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);

        // BOTH selections are set through the page's OWN filter callback, and they really narrow the view:
        // the Released release matches, the Planning one does not.
        page.SetField("_repoFilter", RepoTag);
        page.SetField("_statusFilter", nameof(ReleaseStatus.Released));
        page.Invoke("ApplyFilters");
        await page.RequestRenderAsync();

        Assert.Equal(RepoTag, page.Field("_repoFilter"));
        Assert.Equal(nameof(ReleaseStatus.Released), page.Field("_statusFilter"));
        var selected = Assert.Single(Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")));
        Assert.Equal(ReleasedId, selected.Id);

        // THE DENIAL.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        // BOTH selections are cleared, the filtered view is empty and the denial is surfaced.
        Assert.Equal("", page.Field("_repoFilter"));
        Assert.Equal("", page.Field("_statusFilter"));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")));
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));

        // A LATER AUTHORIZED REFRESH on the SAME instance presents EVERY release — the old selections do not
        // silently constrain it.
        await _auth.SetEnabledAsync(ViewerId, enabled: true);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        Assert.Null(page.Field("_loadError"));
        Assert.Equal(2, Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")).Count);
        var recovered = await page.HtmlAsync();
        Assert.Contains(PlanningTag, recovered, StringComparison.Ordinal);
        Assert.Contains(ReleasedTag, recovered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A THROWN read resets the filter selections too: the failure path shares
    /// <c>ClearReleaseState</c>, so no stale selection survives a thrown read either.
    /// </summary>
    [Fact]
    public async Task ReleasesPage_ThrownReadAlsoResetsBothFilterSelections()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        await using var page = await RenderAsync<Releases>(host, ViewerId);

        page.SetField("_repoFilter", RepoTag);
        page.SetField("_statusFilter", nameof(ReleaseStatus.Released));
        page.Invoke("ApplyFilters");
        await page.RequestRenderAsync();
        Assert.Equal(RepoTag, page.Field("_repoFilter"));
        Assert.Equal(nameof(ReleaseStatus.Released), page.Field("_statusFilter"));

        host.Facade.ArmReleasesThrow(new InvalidOperationException(SecretDetail));

        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();

        Assert.Equal("", page.Field("_repoFilter"));
        Assert.Equal("", page.Field("_statusFilter"));
        Assert.Equal("Releases could not be loaded.", page.Field("_loadError"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  19. A real A → B parameter update on ONE instance never shows A (header or body)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A REAL PARAMETER UPDATE FROM RELEASE A TO RELEASE B ON THE SAME COMPONENT INSTANCE never shows A —
    /// neither in the body NOR IN THE SHARED HEADER — while B's read is pending, and then completes for BOTH
    /// outcomes.
    /// <para>
    /// THE PARAMETER TRAVELS THROUGH THE FRAMEWORK: the retained <see cref="DetailParameterHost"/> re-renders
    /// its child with a new <c>ReleaseId</c>, so <c>OnParametersSetAsync</c> runs exactly as it does on a real
    /// navigation. The title is a SHARED, circuit-scoped state the layout renders OUTSIDE the page, so
    /// clearing only the body would leave the PREVIOUS release's tag in the header for the whole pending
    /// window — which is precisely what this row's pending-window assertions detect.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_RealParameterUpdateAtoB_ShowsNeitherReleaseWhilePending_ThenAllowedCompletion()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        var (page, parameterHost) = await RenderDetailHostAsync(host, ViewerId, PlanningId);
        await using var _ = page;

        // THE NON-VACUITY CONTROL: release A really loaded through the same framework parameter path.
        var atA = await page.HtmlAsync();
        Assert.Contains(PlanningTag, atA, StringComparison.Ordinal);
        Assert.Contains(GoalDescription, atA, StringComparison.Ordinal);
        Assert.Contains(PlanningTag, host.Provider.GetRequiredService<PageHeaderState>().Title, StringComparison.Ordinal);

        // ARM the detail read, THEN push B through the framework: the read parks before publishing.
        var hold = host.Facade.ArmDetail();
        await page.DispatchAsync(() => parameterHost.SetReleaseId(ReleasedId));

        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // ── THE PENDING WINDOW: NEITHER release is claimed, in the body OR in the shared header. ──
        var pending = await page.HtmlAsync();
        Assert.DoesNotContain(PlanningTag, pending, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, pending, StringComparison.Ordinal);

        var pendingHeader = host.Provider.GetRequiredService<PageHeaderState>();
        Assert.DoesNotContain(PlanningTag, pendingHeader.Title, StringComparison.Ordinal);
        Assert.Equal("Release", pendingHeader.Title);
        Assert.Equal("/releases", pendingHeader.BackUrl);
        Assert.Null(page.Field("_release"));
        Assert.Empty(Assert.IsAssignableFrom<List<Goal>>(page.Field("_goals")));

        // ── ALLOWED COMPLETION: the read finishes and B is presented everywhere. ──
        hold.LetGo();
        await page.RequestRenderAsync();

        var atB = await page.HtmlAsync();
        Assert.Contains(ReleasedTag, atB, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanningTag, atB, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, atB, StringComparison.Ordinal);
        Assert.Equal(ReleasedTag, Assert.IsAssignableFrom<ReleaseDto>(page.Field("_release")).Tag);
        Assert.Contains(ReleasedTag, host.Provider.GetRequiredService<PageHeaderState>().Title, StringComparison.Ordinal);
        Assert.Null(page.Field("_loadError"));
    }

    /// <summary>
    /// THE SAME A → B PARAMETER UPDATE, DENIED: while B's read is pending neither release is claimed (body or
    /// shared header), and the denied completion presents the safe error and the unavailable header — with NO
    /// data from A or B.
    /// </summary>
    [Fact]
    public async Task ReleaseDetailPage_RealParameterUpdateAtoB_ShowsNeitherReleaseWhilePending_ThenDeniedCompletion()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();
        SeedReleases(host.Store);

        var (page, parameterHost) = await RenderDetailHostAsync(host, ViewerId, PlanningId);
        await using var _ = page;

        var atA = await page.HtmlAsync();
        Assert.Contains(PlanningTag, atA, StringComparison.Ordinal);
        Assert.Contains(PlanningTag, host.Provider.GetRequiredService<PageHeaderState>().Title, StringComparison.Ordinal);

        // The gate parks B's read BEFORE the real facade resolves, so the directory change made during the
        // pending window decides THAT read's outcome — no timing assumption.
        var gate = host.Facade.ArmDetailGate();
        await page.DispatchAsync(() => parameterHost.SetReleaseId(ReleasedId));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // ── THE PENDING WINDOW. ──
        var pending = await page.HtmlAsync();
        Assert.DoesNotContain(PlanningTag, pending, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedTag, pending, StringComparison.Ordinal);
        var pendingHeader = host.Provider.GetRequiredService<PageHeaderState>();
        Assert.DoesNotContain(PlanningTag, pendingHeader.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedTag, pendingHeader.Title, StringComparison.Ordinal);
        Assert.Equal("Release", pendingHeader.Title);

        // The account is disabled WHILE B's read is pending: THAT read is refused when it resumes.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        gate.LetGo();
        await page.RequestRenderAsync();

        // ── DENIED COMPLETION: safe error, unavailable header, NO A and NO B data. ──
        var denied = await page.HtmlAsync();
        Assert.Contains(ForbiddenMessage, denied, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanningTag, denied, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedTag, denied, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalDescription, denied, StringComparison.Ordinal);
        Assert.DoesNotContain("Release not found.", denied, StringComparison.Ordinal);

        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));
        Assert.Null(page.Field("_release"));
        Assert.Empty(Assert.IsAssignableFrom<List<Goal>>(page.Field("_goals")));

        var deniedHeader = host.Provider.GetRequiredService<PageHeaderState>();
        Assert.Equal("Release Unavailable", deniedHeader.Title);
        Assert.DoesNotContain(PlanningTag, deniedHeader.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedTag, deniedHeader.Title, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  20. The FIRST release-list generation guard, isolated (an EMPTY list has no goals read)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN OLDER AUTHORIZED EMPTY LIST COMPLETING LATE MUST NOT CLEAR A NEWER DENIAL.
    /// <para>
    /// THIS ROW ISOLATES THE FIRST GENERATION GUARD. An EMPTY release list makes no goal-by-release read, so
    /// the load's ONLY await is the list read itself: nothing after it could re-observe a newer generation.
    /// Removing just the guard immediately after <c>GetReleasesAsync</c> therefore lets the stale empty list
    /// publish — clearing the newer denial and rendering "No releases found." — where the non-empty rows
    /// above would be masked by their goals read.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReleasesPage_OlderEmptyListCompletingAfterNewerDenial_CannotClearTheDenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();

        // THE EMPTY-STORE FIXTURE IS LOAD-BEARING: with no release at all, the list read is the load's ONLY
        // facade call. The populated baseline comes from a goal-less release that the store then drops.
        var release = NewRelease(PlanningId, PlanningTag, ReleaseStatus.Planning, At(6));
        host.Store.AddRelease(release);

        await using var page = await RenderAsync<Releases>(host, ViewerId);
        Assert.Contains(PlanningTag, await page.HtmlAsync(), StringComparison.Ordinal);

        // The store is emptied, so the OLDER load's (authorized, computed) result is an EMPTY list.
        host.Store.RemoveRelease(PlanningId);

        var hold = host.Facade.ArmReleases();
        var older = page.InvokeLoadAsync("LoadAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(older.IsCompleted);

        // A NEWER load is refused and publishes the denial.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await page.InvokeLoadAsync("LoadAsync");
        await page.RequestRenderAsync();
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));

        // The older EMPTY list now completes; it must publish NOTHING.
        hold.LetGo();
        await older;
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();

        // The newer denial survives...
        Assert.Equal(ForbiddenMessage, page.Field("_loadError"));
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);

        // ...and the stale empty list did NOT claim the empty state.
        Assert.DoesNotContain("No releases found.", after, StringComparison.Ordinal);
        Assert.False(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
    }

    /// <summary>
    /// POSITIVE CONTROL for the row above: a held EMPTY list with NO newer load really PUBLISHES when it
    /// completes, so the row above observes a guard and not a load that never publishes anything.
    /// <para>
    /// THE ROW MUST OBSERVE A TRANSITION. An empty store rendered empty already satisfies every "empty"
    /// assertion, so a reload that silently DISCARDS its result would pass such a control unchanged. This row
    /// therefore starts from a production-path POPULATED render, removes the release from the store, holds the
    /// empty reload, and only then releases it: the populated-to-empty change in state AND markup can only
    /// come from the held reload publishing. The pending window is asserted too (still populated while the
    /// read is parked), so the change is attributable to the release of the hold and to nothing else.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReleasesPage_HeldEmptyListWithNoNewerDenial_PublishesTheEmptyState()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost();

        // POPULATED BASELINE through the production load path: one goal-less release.
        host.Store.AddRelease(NewRelease(PlanningId, PlanningTag, ReleaseStatus.Planning, At(6)));
        await using var page = await RenderAsync<Releases>(host, ViewerId);

        var populated = await page.HtmlAsync();
        Assert.Contains(PlanningTag, populated, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", populated, StringComparison.Ordinal);
        Assert.Equal(PlanningId, Assert.Single(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases"))).Id);
        Assert.Null(page.Field("_loadError"));
        Assert.True(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));

        // The store now holds NO release: the reload's authorized, computed result is an EMPTY list.
        host.Store.RemoveRelease(PlanningId);

        var hold = host.Facade.ArmReleases();
        var running = page.InvokeLoadAsync("LoadAsync");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(running.IsCompleted);

        // PENDING WINDOW: nothing has published yet, so the page still shows the populated baseline.
        await page.RequestRenderAsync();
        var pending = await page.HtmlAsync();
        Assert.Contains(PlanningTag, pending, StringComparison.Ordinal);
        Assert.DoesNotContain("No releases found.", pending, StringComparison.Ordinal);
        Assert.Single(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));

        // RELEASE the held reload, with NO newer load in between, and let it complete.
        hold.LetGo();
        await running;
        await page.RequestRenderAsync();

        var after = await page.HtmlAsync();

        // THE OBSERVABLE POPULATED-TO-EMPTY TRANSITION, in markup...
        Assert.DoesNotContain(PlanningTag, after, StringComparison.Ordinal);
        Assert.Contains("No releases found.", after, StringComparison.Ordinal);

        // ...and in state: the empty list PUBLISHED, no denial is reported.
        Assert.Null(page.Field("_loadError"));
        Assert.True(Assert.IsAssignableFrom<bool>(page.Field("_loaded")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_allReleases")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(page.Field("_filtered")));
    }

    public void Dispose() => _auth.Dispose();
}