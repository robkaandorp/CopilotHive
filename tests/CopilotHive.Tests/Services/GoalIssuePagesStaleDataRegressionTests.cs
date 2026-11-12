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
/// THE FOCUSED STALE-DATA REGRESSION for the three named pages (<c>Goals.razor</c>,
/// <c>GoalDetail.razor</c>, <c>Issues.razor</c>), exercised BEHAVIOURALLY: the REAL pages are
/// rendered through the framework's own <see cref="HtmlRenderer"/> over the REAL facades, the
/// REAL <see cref="HivePrincipalResolver"/>/<see cref="HivePolicy"/> pair and the REAL
/// <see cref="UserService"/> directory.
/// <para>
/// THE CONTRACT: when a refresh is DENIED, the page presents NO stale data. Every previously
/// loaded goal / stored goal / live detail / progress / dependency / iteration / release /
/// expanded-issue value is cleared, the denial is surfaced, and NO direct store read happens as a
/// fallback.
/// </para>
/// <para>
/// <b>EVERY ADVERTISED STATE IS POPULATED THROUGH THE PRODUCTION PATH BEFORE THE DENIAL</b>, and
/// the baseline is PROVEN nonempty (or present) by an explicit assertion BEFORE the denial is
/// triggered. A state that was never populated would make its later "was cleared" assertion
/// vacuous, so every row asserts BOTH halves: the baseline was really there, and it is really
/// gone afterwards.
/// </para>
/// <para>
/// THE SCENARIO IS DETERMINISTIC: the account is authorized for the FIRST load (so the page
/// really holds data), the directory is then changed, and the page's OWN production refresh path
/// is triggered. No sleeps, no polling, no scheduling assumptions.
/// </para>
/// </summary>
public sealed class GoalIssuePagesStaleDataRegressionTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "pages-viewer";
    private const string OperatorId = "pages-operator";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    /// <summary>The markers the assertions look for in the rendered markup.</summary>
    private const string GoalDescriptionMarker = "STALE-GOAL-DESCRIPTION";
    private const string DependencyMarker = "stale-dependency-marker";
    private const string IssueTitleMarker = "STALE-ISSUE-TITLE";
    private const string IssueIdMarker = "stale-issue-marker";
    private const string PlanningReleaseTag = "v9.9.9-planning";
    private const string ReleasedReleaseTag = "v9.9.9-released";
    private const string PlanningReleaseId = "rel-planning-marker";
    private const string ReleasedReleaseId = "rel-released-marker";
    private const string ProgressDetailsMarker = "STALE-PROGRESS-DETAILS";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── Host ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The host over the PRODUCTION seam: the real resolver/policy over the real directory, the
    /// real facades, the real dashboard state service, the real notifier and the real scoped
    /// circuit accessor.
    /// </summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        internal Host(
            ServiceProvider provider,
            DashboardNotifier notifier,
            DashboardStateService dashboardState,
            ProgressLog progressLog,
            GoalPipelineManager pipelineManager,
            RecordingGoalStore goalStore,
            RecordingIssueStore issueStore)
        {
            _provider = provider;
            Notifier = notifier;
            DashboardState = dashboardState;
            ProgressLog = progressLog;
            PipelineManager = pipelineManager;
            GoalStore = goalStore;
            IssueStore = issueStore;
        }

        internal ServiceProvider Provider => _provider;

        internal DashboardNotifier Notifier { get; }

        internal DashboardStateService DashboardState { get; }

        /// <summary>The REAL progress log the dashboard and the progress service share.</summary>
        internal ProgressLog ProgressLog { get; }

        /// <summary>The REAL pipeline manager the dashboard reads live detail from.</summary>
        internal GoalPipelineManager PipelineManager { get; }

        internal RecordingGoalStore GoalStore { get; }

        internal RecordingIssueStore IssueStore { get; }

        public async ValueTask DisposeAsync()
        {
            // The dashboard service owns a polling timer: it is disposed BEFORE the provider so
            // no callback can outlive the services it touches.
            DashboardState.Dispose();
            await _provider.DisposeAsync();
        }
    }

    /// <summary>Builds the host for the given auth mode.</summary>
    /// <param name="mode">The explicit authentication mode.</param>
    /// <param name="liveGoalStore">
    /// When <c>true</c>, the dashboard state service is wired to the goal store, so the page's
    /// LIVE detail read resolves a real <see cref="GoalDetailInfo"/> and the page takes its LIVE
    /// branch. When <c>false</c> the dashboard carries no store, the live read resolves nothing
    /// and the page takes its STORED branch.
    /// </param>
    private Host NewHost(HiveAuthMode mode, bool liveGoalStore = false)
    {
        var goalStore = new RecordingGoalStore();
        var issueStore = new RecordingIssueStore();
        var notifier = new DashboardNotifier();

        // The live dashboard state carries the goal store ONLY when the LIVE branch is under
        // test: with no store the live detail resolves to "no live state" and the page renders
        // its stored path — exactly the path that loads the stored goal, its iterations, its
        // dependency statuses and its linked issues.
        var pipelineManager = new GoalPipelineManager();
        var progressLog = new ProgressLog();
        var dashboardState = new DashboardStateService(
            new WorkerPool(),
            pipelineManager,
            new DashboardLogSink(),
            progressLog,
            notifier,
            goalStore: liveGoalStore ? goalStore : null);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(notifier);
        services.AddSingleton(dashboardState);
        services.AddSingleton(progressLog);
        services.AddSingleton(pipelineManager);
        services.AddSingleton(goalStore);
        services.AddSingleton<IGoalStore>(goalStore);
        services.AddSingleton(issueStore);
        services.AddSingleton<IIssueStore>(issueStore);
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton(new GoalsFilterState());
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

        services.AddSingleton<IGoalFacade>(sp => new GoalFacade(
            sp.GetRequiredService<IGoalStore>(),
            sp.GetRequiredService<IIssueStore>(),
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
            NullLogger<GoalFacade>.Instance,
            repoManager: null,
            docCleanup: null,
            goalReadyNotifier: null,
            dispatcher: null,
            dashboardState: sp.GetRequiredService<DashboardStateService>(),
            // THE REAL progress service over the SAME progress log the dashboard holds, so a
            // progress entry recorded through the production log really reaches the page.
            progressReports: new ProgressReportService(
                sp.GetRequiredService<GoalPipelineManager>(),
                sp.GetRequiredService<ProgressLog>())));

        services.AddSingleton<IIssueFacade>(sp => new IssueFacade(
            sp.GetRequiredService<IIssueStore>(),
            eventBus: null,
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            NullLogger<IssueFacade>.Instance));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, notifier, dashboardState, progressLog, pipelineManager, goalStore, issueStore);
    }

    // ── Rendering helpers ───────────────────────────────────────────────────

    /// <summary>One rendered page: its markup accessor, its scope and its renderer.</summary>
    private sealed class Rendered : IAsyncDisposable
    {
        private readonly HtmlRenderer _renderer;
        private readonly IServiceScope _scope;
        private readonly Func<string> _rootHtml;
        private readonly object _page;

        internal Rendered(
            HtmlRenderer renderer,
            IServiceScope scope,
            Func<string> rootHtml,
            object page)
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

        /// <summary>
        /// Asks the page to re-render, exactly as the framework does after an event handler —
        /// the private page methods the rows invoke directly do not re-render on their own.
        /// <c>StateHasChanged</c> is protected, so it is invoked the way the framework does.
        /// </summary>
        internal Task RequestRenderAsync() => _renderer.Dispatcher.InvokeAsync(() =>
        {
            var stateHasChanged = _page.GetType().GetMethod(
                "StateHasChanged",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

    /// <summary>
    /// Renders one page over the production seam for one circuit identity and returns the
    /// retained root's markup accessor together with the page instance.
    /// </summary>
    private async Task<Rendered> RenderAsync<TComponent>(
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
                        {
                            // The sequence number is the SOURCE ORDER of the attribute. Only ONE
                            // parameter is ever passed, so the literal 0 is that source order.
                            builder.AddAttribute(0, key, value);
                        }
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
        return new Rendered(renderer, scope, rootHtml!, page!);
    }

    // ── 1. Goals.razor ──────────────────────────────────────────────────────

    /// <summary>
    /// <c>Goals.razor</c>: the page loads the goal list AND the release dropdown through the
    /// authorized facade (the non-vacuity control — both really are presented), and after the
    /// account is disabled the page's OWN refresh path clears the list, the release options and
    /// the release status map, presents NO stale goal and performs NO direct store read as a
    /// fallback.
    /// <para>
    /// The release options are DISTINCTIVE (a Planning and a Released release with unique tags),
    /// so their removal is observable in the rendered markup — the goal id alone would not prove
    /// the release state was cleared.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GoalsPage_DeniedRefresh_ClearsTheLoadedGoalListAndReleaseOptions_AndDoesNotFallBackToTheStore()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled);

        host.GoalStore.AddGoal(new Goal
        {
            Id = "goals-page-goal",
            Description = GoalDescriptionMarker,
            Status = GoalStatus.Draft,
            CreatedAt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        });
        host.GoalStore.AddRelease(new Release
        {
            Id = PlanningReleaseId,
            Tag = PlanningReleaseTag,
            Status = ReleaseStatus.Planning,
            CreatedAt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        });
        host.GoalStore.AddRelease(new Release
        {
            Id = ReleasedReleaseId,
            Tag = ReleasedReleaseTag,
            Status = ReleaseStatus.Released,
            CreatedAt = new DateTime(2024, 5, 7, 7, 8, 9, DateTimeKind.Utc),
        });

        await using var rendered = await RenderAsync<CopilotHive.Components.Pages.Goals>(host, ViewerId);

        // THE NON-VACUITY CONTROL: the authorized load really presented the goal AND both
        // release options.
        var before = await rendered.HtmlAsync();
        Assert.Contains(GoalDescriptionMarker, before, StringComparison.Ordinal);
        Assert.Contains(PlanningReleaseTag, before, StringComparison.Ordinal);
        Assert.Contains(ReleasedReleaseTag, before, StringComparison.Ordinal);
        Assert.NotEmpty(Assert.IsAssignableFrom<List<Goal>>(rendered.Field("_allGoals")));
        Assert.NotEmpty(Assert.IsAssignableFrom<List<Goal>>(rendered.Field("_filtered")));
        Assert.NotEmpty(Assert.IsAssignableFrom<List<Release>>(rendered.Field("_allReleases")));
        Assert.NotEmpty(Assert.IsAssignableFrom<Dictionary<string, ReleaseStatus>>(rendered.Field("_releaseStatusById")));

        var readsBefore = host.GoalStore.GetAllCalls;
        var releaseReadsBefore = host.GoalStore.ReleaseReads;

        // The account is DISABLED in the directory.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);

        // THE PAGE'S OWN REFRESH PATH: the dashboard state notification the page subscribes to.
        host.Notifier.NotifyStateChanged();
        await rendered.DrainAsync();

        var after = await rendered.HtmlAsync();

        // NO stale goal data is presented.
        Assert.DoesNotContain(GoalDescriptionMarker, after, StringComparison.Ordinal);

        // NO stale RELEASE data is presented either — the release selector's options are gone.
        Assert.DoesNotContain(PlanningReleaseTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain(ReleasedReleaseTag, after, StringComparison.Ordinal);

        // The page's own loaded state is EMPTY.
        Assert.Empty(Assert.IsAssignableFrom<List<Goal>>(rendered.Field("_allGoals")));
        Assert.Empty(Assert.IsAssignableFrom<List<Goal>>(rendered.Field("_filtered")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, GoalStatus>>(rendered.Field("_goalStatusById")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(rendered.Field("_allReleases")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, ReleaseStatus>>(rendered.Field("_releaseStatusById")));
        Assert.False(Assert.IsAssignableFrom<bool>(rendered.Field("_loaded")));

        // The denial IS surfaced (the page shows the facade's error text).
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);

        // NO direct-store fallback: neither the goal list nor the release list was read again.
        Assert.Equal(readsBefore, host.GoalStore.GetAllCalls);
        Assert.Equal(releaseReadsBefore, host.GoalStore.ReleaseReads);
    }

    // ── 2. GoalDetail.razor — the stored path ───────────────────────────────

    /// <summary>
    /// <c>GoalDetail.razor</c> — THE STORED PATH: the page loads the stored goal, its iterations,
    /// its progress feed, its dependency statuses, its linked issues AND the planning-release
    /// dropdown through the authorized facade (the non-vacuity control), and after the account is
    /// DISABLED the page's OWN refresh path clears EVERY one of those values — no stale stored
    /// goal, no stale iteration, no stale progress entry, no stale dependency status, no stale
    /// linked issue, no stale release option — and performs NO direct store read as a fallback.
    /// </summary>
    [Fact]
    public async Task GoalDetailPage_DeniedRefresh_ClearsEveryLoadedValue_AndDoesNotFallBackToTheStore()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled);

        // A COMPLETED, UNASSIGNED goal: that is the only state in which the page loads the
        // PLANNING-RELEASE dropdown, so the dropdown baseline is really populated.
        host.GoalStore.AddGoal(new Goal
        {
            Id = "detail-page-goal",
            Description = GoalDescriptionMarker,
            Status = GoalStatus.Completed,
            DependsOn = [DependencyMarker],
            IterationSummaries = [new IterationSummary { Iteration = 7 }],
            CreatedAt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        });
        // The dependency exists and is COMPLETED, so its status icon ("✅") is only present when
        // the dependency-status map really was loaded.
        host.GoalStore.AddGoal(new Goal
        {
            Id = DependencyMarker,
            Description = "Dependency",
            Status = GoalStatus.Completed,
            CreatedAt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        });
        // A PLANNING release with a distinctive tag: the dropdown is populated from the store's
        // release read, so its tag in the markup proves the dropdown really was loaded.
        host.GoalStore.AddRelease(new Release
        {
            Id = PlanningReleaseId,
            Tag = PlanningReleaseTag,
            Status = ReleaseStatus.Planning,
            CreatedAt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        });
        host.IssueStore.AddIssue(new Issue
        {
            Id = IssueIdMarker,
            Title = IssueTitleMarker,
            Description = "desc",
            Type = IssueType.Bug,
            Severity = IssueSeverity.Medium,
            Status = IssueStatus.Open,
            SourceGoalId = "detail-page-goal",
        });

        // A REAL progress entry for the goal, recorded through the production progress log the
        // dashboard and the progress service share — so the progress baseline is really
        // populated (the iteration-1 fixture used an empty log, which made the progress
        // assertion vacuous).
        host.ProgressLog.Add("worker-1", "detail-page-goal", "working", ProgressDetailsMarker);

        await using var rendered = await RenderAsync<CopilotHive.Components.Pages.GoalDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["GoalId"] = "detail-page-goal" });

        // THE NON-VACUITY CONTROL: the authorized load really presented every value.
        var before = await rendered.HtmlAsync();
        Assert.Contains(GoalDescriptionMarker, before, StringComparison.Ordinal);
        Assert.Contains(DependencyMarker, before, StringComparison.Ordinal);
        Assert.Contains(IssueTitleMarker, before, StringComparison.Ordinal);
        Assert.Contains(PlanningReleaseTag, before, StringComparison.Ordinal);
        Assert.Contains("Iteration 7", before, StringComparison.Ordinal);

        // …and every advertised state really was POPULATED.
        Assert.NotNull(rendered.Field("_storedGoal"));
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<IterationSummary>>(rendered.Field("_storedIterations")));
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<ProgressEntry>>(rendered.Field("_progress")));
        Assert.NotEmpty(Assert.IsAssignableFrom<Dictionary<string, GoalStatus>>(rendered.Field("_dependencyStatuses")));
        Assert.NotEmpty(Assert.IsAssignableFrom<List<LinkedIssueDto>>(rendered.Field("_linkedIssues")));
        Assert.NotEmpty(Assert.IsAssignableFrom<List<Release>>(rendered.Field("_planningReleases")));

        var readsBefore = host.GoalStore.ReadCalls.Count;

        // The account is DISABLED in the directory. A disablement — not a demotion — is the
        // change that DENIES this page's reads: every read on this surface needs shared read,
        // and a demotion to Member would still hold it, so it would not be a denial at all.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);

        // THE PAGE'S OWN REFRESH PATH: the dashboard state notification the page subscribes to.
        host.Notifier.NotifyStateChanged();
        await rendered.DrainAsync();

        var after = await rendered.HtmlAsync();

        // NO stale goal / iteration / progress / dependency / linked-issue / release data is
        // presented.
        Assert.DoesNotContain(GoalDescriptionMarker, after, StringComparison.Ordinal);
        Assert.DoesNotContain(DependencyMarker, after, StringComparison.Ordinal);
        Assert.DoesNotContain(IssueTitleMarker, after, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanningReleaseTag, after, StringComparison.Ordinal);
        Assert.DoesNotContain("Iteration 7", after, StringComparison.Ordinal);

        // The dependency-status icon for a COMPLETED dependency is gone too.
        Assert.DoesNotContain("✅", after, StringComparison.Ordinal);

        // The page's own loaded state is EMPTY for EVERY value.
        Assert.Null(rendered.Field("_detail"));
        Assert.Null(rendered.Field("_storedGoal"));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<IterationSummary>>(rendered.Field("_storedIterations")));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ProgressEntry>>(rendered.Field("_progress")));
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.ICollection>(rendered.Field("_timeline")));
        Assert.Empty(Assert.IsAssignableFrom<Dictionary<string, GoalStatus>>(rendered.Field("_dependencyStatuses")));
        Assert.Empty(Assert.IsAssignableFrom<List<LinkedIssueDto>>(rendered.Field("_linkedIssues")));
        Assert.Empty(Assert.IsAssignableFrom<List<Release>>(rendered.Field("_planningReleases")));

        // The denial IS surfaced.
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);

        // NO direct-store fallback: the facade performed no further goal read.
        Assert.Equal(readsBefore, host.GoalStore.ReadCalls.Count);
    }

    // ── 3. GoalDetail.razor — the LIVE branch ───────────────────────────────

    /// <summary>
    /// <c>GoalDetail.razor</c> — THE LIVE BRANCH: with the dashboard wired to the goal store AND a
    /// real live pipeline, the page's LIVE detail read resolves a real
    /// <see cref="GoalDetailInfo"/> with a real TIMELINE (the pipeline's progress reports become
    /// the timeline entries), so the page holds LIVE data rather than the stored path. A denied
    /// refresh must clear the LIVE detail and the TIMELINE too — a page that kept the live
    /// detail would present a stale live dashboard to a caller that may no longer read it.
    /// </summary>
    [Fact]
    public async Task GoalDetailPage_DeniedRefresh_ClearsTheLiveDetailAndTheTimeline()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled, liveGoalStore: true);

        var goal = new Goal
        {
            Id = "live-detail-goal",
            Description = GoalDescriptionMarker,
            Status = GoalStatus.InProgress,
            DependsOn = [DependencyMarker],
            CreatedAt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        };
        host.GoalStore.AddGoal(goal);
        host.GoalStore.AddGoal(new Goal
        {
            Id = DependencyMarker,
            Description = "Dependency",
            Status = GoalStatus.Completed,
            CreatedAt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        });

        // A REAL live pipeline for the goal, so the dashboard's live detail really resolves and
        // really carries the phase log and the progress reports the TIMELINE is built from.
        var pipeline = host.PipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        pipeline.AddProgressReport("worker-1", "working", ProgressDetailsMarker);

        await using var rendered = await RenderAsync<CopilotHive.Components.Pages.GoalDetail>(
            host, ViewerId, new Dictionary<string, object?> { ["GoalId"] = "live-detail-goal" });

        // THE NON-VACUITY CONTROL: the authorized load really presented the LIVE detail.
        var before = await rendered.HtmlAsync();
        Assert.Contains(GoalDescriptionMarker, before, StringComparison.Ordinal);
        Assert.Contains(DependencyMarker, before, StringComparison.Ordinal);
        Assert.NotNull(rendered.Field("_detail"));

        // …and the TIMELINE really was populated (the iteration-1 fixture could not populate it
        // from the stored path, so its timeline assertion was vacuous).
        Assert.NotEmpty(Assert.IsAssignableFrom<System.Collections.ICollection>(rendered.Field("_timeline")));

        // The account is DISABLED in the directory.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);

        // THE PAGE'S OWN REFRESH PATH.
        host.Notifier.NotifyStateChanged();
        await rendered.DrainAsync();

        var after = await rendered.HtmlAsync();

        // The LIVE detail and the TIMELINE are gone.
        Assert.Null(rendered.Field("_detail"));
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.ICollection>(rendered.Field("_timeline")));
        Assert.DoesNotContain(GoalDescriptionMarker, after, StringComparison.Ordinal);
        Assert.DoesNotContain(DependencyMarker, after, StringComparison.Ordinal);
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);
    }

    // ── 4. Issues.razor ────────────────────────────────────────────────────

    /// <summary>
    /// <c>Issues.razor</c>: the page loads the issue list through the authorized facade and the
    /// issue is ACTUALLY EXPANDED (the non-vacuity control for the expanded-detail state), and
    /// after the account is disabled the page's OWN load path clears the list AND the expanded
    /// detail, presents NO stale issue and performs NO direct store read as a fallback.
    /// </summary>
    [Fact]
    public async Task IssuesPage_DeniedRefresh_ClearsTheLoadedIssueListAndTheExpandedDetail_AndDoesNotFallBackToTheStore()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var host = NewHost(HiveAuthMode.AuthenticationEnabled);

        host.IssueStore.AddIssue(new Issue
        {
            Id = IssueIdMarker,
            Title = IssueTitleMarker,
            Description = "STALE-ISSUE-DESCRIPTION-MARKER",
            Type = IssueType.Bug,
            Severity = IssueSeverity.Medium,
            Status = IssueStatus.Open,
        });

        await using var rendered = await RenderAsync<CopilotHive.Components.Pages.Issues>(host, ViewerId);

        // THE NON-VACUITY CONTROL: the authorized load really presented the issue.
        var before = await rendered.HtmlAsync();
        Assert.Contains(IssueTitleMarker, before, StringComparison.Ordinal);
        Assert.NotEmpty(Assert.IsAssignableFrom<List<LinkedIssueDto>>(rendered.Field("_issues")));

        // THE ISSUE IS REALLY EXPANDED, so the expanded-detail baseline is populated (the
        // iteration-1 fixture never expanded an issue, so its _expandedIssueId assertion was
        // vacuous). The page's OWN loaded DTO is handed to the toggle, exactly as the row
        // click does.
        var loadedIssue = Assert.Single(Assert.IsAssignableFrom<List<LinkedIssueDto>>(rendered.Field("_issues")));
        await rendered.InvokePrivateAsync("ToggleDetail", IssueIdMarker, loadedIssue);
        await rendered.RequestRenderAsync();
        await rendered.DrainAsync();

        var expanded = await rendered.HtmlAsync();
        Assert.Equal(IssueIdMarker, rendered.Field("_expandedIssueId"));
        Assert.Contains("STALE-ISSUE-DESCRIPTION-MARKER", expanded, StringComparison.Ordinal);

        var queriesBefore = host.IssueStore.Queries.Count;

        // The account is DISABLED in the directory.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);

        // THE PAGE'S OWN LOAD PATH: the private loader the page calls on every refresh.
        await rendered.InvokePrivateAsync("LoadIssues");
        await rendered.DrainAsync();

        var after = await rendered.HtmlAsync();

        // NO stale issue data is presented.
        Assert.DoesNotContain(IssueTitleMarker, after, StringComparison.Ordinal);
        Assert.DoesNotContain(IssueIdMarker, after, StringComparison.Ordinal);

        // The page's own loaded state is EMPTY — including the EXPANDED DETAIL.
        Assert.Empty(Assert.IsAssignableFrom<List<LinkedIssueDto>>(rendered.Field("_issues")));
        Assert.Null(rendered.Field("_expandedIssueId"));
        Assert.False(Assert.IsAssignableFrom<bool>(rendered.Field("_loaded")));

        // The denial IS surfaced.
        Assert.Contains(ForbiddenMessage, after, StringComparison.Ordinal);

        // NO direct-store fallback: the facade performed no further issue query.
        Assert.Equal(queriesBefore, host.IssueStore.Queries.Count);
    }

    public void Dispose() => _auth.Dispose();

    // ── Test doubles ────────────────────────────────────────────────────────

    /// <summary>A JS runtime that answers nothing (the pages only use it for confirm/scroll).</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);
    }

    /// <summary>A navigation manager that records nothing (the pages only navigate on delete).</summary>
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/goals");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            // Navigation is not exercised by these rows.
        }
    }
}
