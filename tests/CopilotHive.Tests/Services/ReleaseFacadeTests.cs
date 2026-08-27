using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="ReleaseFacade"/> — the facade the seven release endpoints use instead of
/// touching <see cref="IGoalStore"/> and <see cref="ReleaseExecutionService"/> directly.
/// </summary>
/// <remarks>
/// The load-bearing contracts pinned down here mirror the pre-facade handlers exactly:
/// <list type="bullet">
///   <item>Status validation order is frozen: not-found → blank → numeric → unknown →
///   comma-combined → Released→Released conflict → Released→Planning revert → Planning→Planning
///   no-op → execution.</item>
///   <item>The FIVE <see cref="ReleaseExecutionFailure"/> mappings produce the exact
///   <see cref="FacadeErrorKind"/> values the endpoint mapped to HTTP statuses.</item>
///   <item>Cancellation flows ONLY to <see cref="ReleaseExecutionService.ExecuteReleaseAsync"/>
///   and the knowledge-document cleanup — the initial read, success re-read and
///   <see cref="IGoalStore.UpdateReleaseAsync(Release, CancellationToken)"/> keep default tokens.</item>
///   <item>Create validates the version FIRST; a duplicate ID surfaces the store's
///   <see cref="InvalidOperationException"/> → <see cref="FacadeErrorKind.Conflict"/>.</item>
///   <item>Notes accepts a null value that CLEARS the notes.</item>
///   <item>Tag/repositories catch <see cref="KeyNotFoundException"/> and
///   <see cref="InvalidOperationException"/> exactly as the handlers did.</item>
///   <item>Delete has FIVE failure outcomes: not-found, non-Planning, executing, attached goals,
///   concurrent-state-change.</item>
///   <item>Validate without an execution service yields <c>{valid:true}</c>.</item>
///   <item>Side effects (dashboard notification, event publication, NuGet monitors, knowledge-doc
///   cleanup) run EXACTLY once per operation.</item>
/// </list>
/// </remarks>
public sealed class ReleaseFacadeTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;

    public ReleaseFacadeTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    public void Dispose() => _dbContext.Dispose();

    private static HiveConfigFile CreateConfig() => new()
    {
        Repositories =
        [
            new RepositoryConfig
            {
                Name = "repo1", Url = "https://github.com/test/repo1", DefaultBranch = "main",
                Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" },
            },
        ],
    };

    private ReleaseExecutionService CreateExecutionService(
        HiveConfigFile config, IBrainRepoManager repoManager) =>
        new(_store, config, repoManager, NullLogger<ReleaseExecutionService>.Instance);

    private static ReleaseFacade CreateFacade(
        IGoalStore store,
        ReleaseExecutionService? executionService = null,
        IEventBus? eventBus = null,
        NuGetPublishMonitorService? nuGetMonitor = null,
        HiveConfigFile? hiveConfig = null,
        IHostApplicationLifetime? appLifetime = null,
        KnowledgeDocumentCleanupService? docCleanup = null,
        DashboardNotifier? notifier = null)
        => new(
            store,
            notifier ?? new DashboardNotifier(),
            NullLogger<ReleaseFacade>.Instance,
            executionService,
            eventBus,
            nuGetMonitor,
            hiveConfig,
            appLifetime,
            docCleanup);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SeedReleaseAsync(
        GoalStore store, string id, string tag = "v1.0.0", ReleaseStatus status = ReleaseStatus.Planning,
        ReleaseExecutionState executionState = ReleaseExecutionState.None, List<string>? repos = null)
        => await store.CreateReleaseAsync(new Release
        {
            Id = id,
            Tag = tag,
            Status = status,
            ExecutionState = executionState,
            RepositoryNames = repos ?? [],
        }, TestContext.Current.CancellationToken);

    // xUnit1051: every call site passes Ct explicitly below.

    // ── UpdateReleaseStatusAsync: validation ordering ─────────────────────────

    [Fact]
    public async Task UpdateStatus_ReleaseNotFound_ReturnsNotFoundOutcome()
    {
        var facade = CreateFacade(_store);

        var outcome = await facade.UpdateReleaseStatusAsync("missing", new UpdateReleaseStatusRequest("Released"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.NotFound, failure.Kind);
        Assert.Equal("Release 'missing' not found.", failure.Error);
        Assert.Null(failure.Detail);
        Assert.Empty(failure.Errors);
        Assert.Empty(failure.Results);
    }

    [Fact]
    public async Task UpdateStatus_BlankStatus_ReturnsBadRequest()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        var facade = CreateFacade(_store);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest(""), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.BadRequest, failure.Kind);
        Assert.Equal("Status is required.", failure.Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task UpdateStatus_NumericStatus_ReturnsBadRequest(string numericStatus)
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        var facade = CreateFacade(_store);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest(numericStatus), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.BadRequest, failure.Kind);
        Assert.Equal($"Invalid status '{numericStatus}'. Valid values: Planning, Released.", failure.Error);
    }

    [Fact]
    public async Task UpdateStatus_UnknownStatus_ReturnsBadRequest()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        var facade = CreateFacade(_store);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Bogus"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.BadRequest, failure.Kind);
        Assert.Equal("Invalid status 'Bogus'. Valid values: Planning, Released.", failure.Error);
    }

    [Fact]
    public async Task UpdateStatus_CommaCombinedStatus_ReturnsBadRequest()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        var facade = CreateFacade(_store);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released,Planning"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.BadRequest, failure.Kind);
        Assert.Equal("Invalid status 'Released,Planning'. Valid values: Planning, Released.", failure.Error);
    }

    [Fact]
    public async Task UpdateStatus_ReleasedToReleased_ReturnsConflict()
    {
        await SeedReleaseAsync(_store, "v1.0.0", status: ReleaseStatus.Released);
        var facade = CreateFacade(_store);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.Conflict, failure.Kind);
        Assert.Equal("Release is already in 'Released' status.", failure.Error);
    }

    [Fact]
    public async Task UpdateStatus_ReleasedToPlanning_ReturnsConflict()
    {
        await SeedReleaseAsync(_store, "v1.0.0", status: ReleaseStatus.Released);
        var facade = CreateFacade(_store);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Planning"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.Conflict, failure.Kind);
        Assert.Equal("Cannot revert a Released release back to Planning.", failure.Error);
    }

    [Fact]
    public async Task UpdateStatus_PlanningToPlanning_ReturnsNoOpOutcome()
    {
        await SeedReleaseAsync(_store, "v1.0.0", repos: ["repo1"]);
        var facade = CreateFacade(_store);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Planning"), Ct);

        var noOp = Assert.IsType<PlanningNoOpOutcome>(outcome);
        Assert.Equal("v1.0.0", noOp.Release.Id);
        Assert.Equal(ReleaseStatus.Planning, noOp.Release.Status);
    }

    // ── UpdateReleaseStatusAsync: missing execution service → 503 ────────────

    [Fact]
    public async Task UpdateStatus_MissingExecutionService_ReturnsServiceUnavailable()
    {
        await SeedReleaseAsync(_store, "v1.0.0", repos: ["repo1"]);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, Ct);
        var facade = CreateFacade(_store, executionService: null);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.ServiceUnavailable, failure.Kind);
        Assert.Equal("Release execution service is not available.", failure.Detail);
        Assert.Null(failure.Error);
    }

    // ── UpdateReleaseStatusAsync: the FIVE ReleaseExecutionFailure mappings ───

    [Fact]
    public async Task UpdateStatus_ExecutionFailureNotFound_ReturnsNotFound()
    {
        // The facade's initial read succeeds, but the execution service's internal re-read
        // cannot find the release → ReleaseExecutionFailure.NotFound.
        var store = new StatefulRecordingStore();
        store.UpsertRelease(new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] });
        store.GoalsByRelease["v1.0.0"] =
            [new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }];
        store.SecondGetReleaseReturnsNull = true;

        var config = CreateConfig();
        var execService = new ReleaseExecutionService(
            store, config, new ConfigurableFakeRepoManager { CreateTagResult = true },
            NullLogger<ReleaseExecutionService>.Instance);
        var facade = CreateFacade(store, execService);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.NotFound, failure.Kind);
        Assert.Equal("Release not found.", failure.Error);
    }

    [Fact]
    public async Task UpdateStatus_ExecutionFailureAlreadyReleased_ReturnsConflict()
    {
        // The facade reads Planning, but the execution service's re-read sees Released.
        var store = new StatefulRecordingStore();
        store.UpsertRelease(new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] });
        store.GoalsByRelease["v1.0.0"] =
            [new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }];
        store.SecondGetReleaseStatus = ReleaseStatus.Released;

        var config = CreateConfig();
        var execService = new ReleaseExecutionService(
            store, config, new ConfigurableFakeRepoManager { CreateTagResult = true },
            NullLogger<ReleaseExecutionService>.Instance);
        var facade = CreateFacade(store, execService);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.Conflict, failure.Kind);
        Assert.Equal("Release is already Released.", failure.Error);
    }

    [Fact]
    public async Task UpdateStatus_ExecutionFailureAlreadyExecuting_ReturnsConflict()
    {
        // The facade reads Planning, but the execution service's re-read sees Executing.
        var store = new StatefulRecordingStore();
        store.UpsertRelease(new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] });
        store.GoalsByRelease["v1.0.0"] =
            [new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }];
        store.SecondGetReleaseExecutionState = ReleaseExecutionState.Executing;

        var config = CreateConfig();
        var execService = new ReleaseExecutionService(
            store, config, new ConfigurableFakeRepoManager { CreateTagResult = true },
            NullLogger<ReleaseExecutionService>.Instance);
        var facade = CreateFacade(store, execService);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.Conflict, failure.Kind);
        Assert.Equal("Release is already being executed.", failure.Error);
    }

    [Fact]
    public async Task UpdateStatus_ExecutionFailureValidation_ReturnsBadRequestWithErrors()
    {
        await SeedReleaseAsync(_store, "v1.0.0", repos: ["repo1"]);
        // Incomplete goal → validation fails.
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.InProgress }, Ct);

        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        var execService = CreateExecutionService(CreateConfig(), fake);
        var facade = CreateFacade(_store, execService);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.BadRequest, failure.Kind);
        Assert.Null(failure.Error);
        Assert.NotEmpty(failure.Errors);
        Assert.Contains(failure.Errors, e => e!.Contains("not completed", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(failure.Results);
        // No git operations ran for a validation failure.
        Assert.Empty(fake.MergeCalls);
    }

    [Fact]
    public async Task UpdateStatus_ExecutionFailureExecution_ReturnsInternalWithDetailAndResults()
    {
        await SeedReleaseAsync(_store, "v1.0.0", repos: ["repo1"]);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, Ct);

        var fake = new ConfigurableFakeRepoManager
        {
            MergeCallback = (_, _, _) => throw new InvalidOperationException("merge blew up"),
        };
        var execService = CreateExecutionService(CreateConfig(), fake);
        var facade = CreateFacade(_store, execService);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released"), Ct);

        var failure = Assert.IsType<StatusFailureOutcome>(outcome);
        Assert.Equal(FacadeErrorKind.Internal, failure.Kind);
        Assert.NotNull(failure.Detail);
        Assert.Contains("repo1", failure.Detail);
        Assert.NotEmpty(failure.Results);
        Assert.Equal("repo1", failure.Results[0].RepoName);
        Assert.False(failure.Results[0].Success);
        Assert.Null(failure.Error);
        Assert.Empty(failure.Errors);
    }

    // ── UpdateReleaseStatusAsync: success path ───────────────────────────────

    [Fact]
    public async Task UpdateStatus_PlanningToReleased_ReturnsExecutionSuccessOutcome()
    {
        await SeedReleaseAsync(_store, "v1.0.0", repos: ["repo1"]);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, Ct);

        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        var execService = CreateExecutionService(CreateConfig(), fake);
        var eventBus = new RecordingEventBus();
        var notifier = new DashboardNotifier();
        var notifyCount = 0;
        notifier.OnStateChanged += () => notifyCount++;
        var facade = CreateFacade(_store, execService, eventBus: eventBus, notifier: notifier);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released"), Ct);

        var success = Assert.IsType<ExecutionSuccessOutcome>(outcome);
        Assert.Equal(ReleaseStatus.Released, success.Release.Status);
        Assert.Equal(ReleaseExecutionState.Completed, success.Release.ExecutionState);
        Assert.NotNull(success.Release.ReleasedAt);
        Assert.True(success.Result.Success);
        Assert.Equal(ReleaseExecutionFailure.None, success.Result.Failure);
        Assert.Single(success.Result.Results);
        Assert.Equal("repo1", success.Result.Results[0].RepoName);

        // The release is persisted as Released.
        var stored = await _store.GetReleaseAsync("v1.0.0", Ct);
        Assert.NotNull(stored);
        Assert.Equal(ReleaseStatus.Released, stored!.Status);
        Assert.Equal(ReleaseExecutionState.Completed, stored.ExecutionState);

        // The event bus received exactly one ReleaseCompleted event.
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.ReleaseCompleted, evt.Type);
        Assert.Equal("v1.0.0", evt.ReleaseId);

        // The dashboard was notified exactly once.
        Assert.Equal(1, notifyCount);

        // The repo manager received the merge and tag calls.
        Assert.Contains(fake.MergeCalls, c => c is { Repo: "repo1", Source: "main", Target: "main" });
        Assert.Contains(fake.CreateTagCalls, c => c is { Repo: "repo1", Tag: "v1.0.0", Branch: "main" });
    }

    [Fact]
    public async Task UpdateStatus_Success_NullEventBusSkipsPublication()
    {
        await SeedReleaseAsync(_store, "v1.0.0", repos: ["repo1"]);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, Ct);

        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        var execService = CreateExecutionService(CreateConfig(), fake);
        var facade = CreateFacade(_store, execService, eventBus: null);

        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released"), Ct);

        Assert.IsType<ExecutionSuccessOutcome>(outcome);
    }

    // ── UpdateReleaseStatusAsync: cancellation exactness ─────────────────────

    [Fact]
    public async Task UpdateStatus_CancellationFlowsOnlyToExecuteAndCleanup()
    {
        var store = new StatefulRecordingStore();
        store.UpsertRelease(new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] });
        store.GoalsByRelease["v1.0.0"] =
            [new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }];

        var config = CreateConfig();
        var execService = new ReleaseExecutionService(
            store, config, new ConfigurableFakeRepoManager { CreateTagResult = true },
            NullLogger<ReleaseExecutionService>.Instance);
        var docCleanup = new KnowledgeDocumentCleanupService(new KnowledgeGraph(), NullLogger<KnowledgeDocumentCleanupService>.Instance);
        var facade = CreateFacade(store, execService, docCleanup: docCleanup);

        using var cts = new CancellationTokenSource();
        var outcome = await facade.UpdateReleaseStatusAsync("v1.0.0", new UpdateReleaseStatusRequest("Released"), cts.Token);

        Assert.IsType<ExecutionSuccessOutcome>(outcome);

        // Call order for GetReleaseAsync:
        //   0: facade initial read (default token)
        //   1: execution service internal re-read (caller token)
        //   2: facade success re-read (default token)
        Assert.Equal(3, store.GetReleaseTokens.Count);
        Assert.Equal(CancellationToken.None, store.GetReleaseTokens[0]);
        Assert.Equal(cts.Token, store.GetReleaseTokens[1]);
        Assert.Equal(CancellationToken.None, store.GetReleaseTokens[2]);

        // The execution service's two UpdateReleaseAsync calls (Executing + Completed) carry the
        // caller token; the facade's timestamp update carries the default token.
        Assert.Equal(3, store.UpdateReleaseTokens.Count);
        Assert.Equal(cts.Token, store.UpdateReleaseTokens[0]);
        Assert.Equal(cts.Token, store.UpdateReleaseTokens[1]);
        Assert.Equal(CancellationToken.None, store.UpdateReleaseTokens[2]);

        // GetGoalsByReleaseAsync: the execution service's validation read carries the caller
        // token; the facade's cleanup read carries the default token.
        Assert.Equal(2, store.GetGoalsByReleaseTokens.Count);
        Assert.Equal(cts.Token, store.GetGoalsByReleaseTokens[0]);
        Assert.Equal(CancellationToken.None, store.GetGoalsByReleaseTokens[1]);
    }

    // ── CreateReleaseAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRelease_ValidRequest_ReturnsCreatedDtoAndNotifies()
    {
        var notifier = new DashboardNotifier();
        var notifyCount = 0;
        notifier.OnStateChanged += () => notifyCount++;
        var facade = CreateFacade(_store, notifier: notifier);

        var result = await facade.CreateReleaseAsync(new CreateReleaseRequest("v2.0.0"));

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Equal("v2.0.0", result.Value!.Id);
        Assert.Equal("v2.0.0", result.Value!.Tag);
        Assert.Equal(ReleaseStatus.Planning, result.Value!.Status);
        Assert.Empty(result.Value!.RepositoryNames);
        Assert.Equal(1, notifyCount);
    }

    [Fact]
    public async Task CreateRelease_WithRepository_SetsRepositoryNames()
    {
        var facade = CreateFacade(_store);

        var result = await facade.CreateReleaseAsync(new CreateReleaseRequest("v2.0.0", Repository: "repo1"));

        Assert.True(result.Success);
        Assert.Equal(["repo1"], result.Value!.RepositoryNames);
    }

    [Fact]
    public async Task CreateRelease_BlankVersion_ReturnsBadRequest()
    {
        var facade = CreateFacade(_store);

        var result = await facade.CreateReleaseAsync(new CreateReleaseRequest("  "));

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Version is required.", result.Error);
    }

    [Fact]
    public async Task CreateRelease_DuplicateId_ReturnsConflict()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        var facade = CreateFacade(_store);

        // The real GoalStore converts the duplicate-ID DbUpdateException into
        // InvalidOperationException — the same exception the pre-facade handler caught.
        var result = await facade.CreateReleaseAsync(new CreateReleaseRequest("v1.0.0"));

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Conflict, result.Kind);
        Assert.Contains("already exist", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── UpdateReleaseNotesAsync ───────────────────────────────────────────────

    [Fact]
    public async Task UpdateNotes_SetsNotesAndNotifies()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        var notifier = new DashboardNotifier();
        var notifyCount = 0;
        notifier.OnStateChanged += () => notifyCount++;
        var facade = CreateFacade(_store, notifier: notifier);

        var result = await facade.UpdateReleaseNotesAsync("v1.0.0", new UpdateReleaseNotesRequest("New notes"));

        Assert.True(result.Success);
        Assert.Equal("New notes", result.Value!.Notes);
        Assert.Equal(1, notifyCount);
    }

    [Fact]
    public async Task UpdateNotes_Null_ClearsNotes()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        await _store.UpdateReleaseAsync("v1.0.0", new ReleaseUpdateData { Notes = "Old notes" }, Ct);
        var facade = CreateFacade(_store);

        var result = await facade.UpdateReleaseNotesAsync("v1.0.0", new UpdateReleaseNotesRequest(null));

        Assert.True(result.Success);
        Assert.Null(result.Value!.Notes);
    }

    [Fact]
    public async Task UpdateNotes_UnknownId_ReturnsNotFound()
    {
        var facade = CreateFacade(_store);

        var result = await facade.UpdateReleaseNotesAsync("missing", new UpdateReleaseNotesRequest("notes"));

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Release 'missing' not found.", result.Error);
    }

    // ── UpdateReleaseTagAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTag_BlankTag_ReturnsBadRequest()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        var facade = CreateFacade(_store);

        var result = await facade.UpdateReleaseTagAsync("v1.0.0", new UpdateReleaseTagRequest("  "));

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Tag is required.", result.Error);
    }

    [Fact]
    public async Task UpdateTag_SetsTagAndNotifies()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        var notifier = new DashboardNotifier();
        var notifyCount = 0;
        notifier.OnStateChanged += () => notifyCount++;
        var facade = CreateFacade(_store, notifier: notifier);

        var result = await facade.UpdateReleaseTagAsync("v1.0.0", new UpdateReleaseTagRequest("  v2.0.0  "));

        Assert.True(result.Success);
        Assert.Equal("v2.0.0", result.Value!.Tag); // trimmed
        Assert.Equal(1, notifyCount);
    }

    [Fact]
    public async Task UpdateTag_UnknownId_ReturnsNotFound()
    {
        var facade = CreateFacade(_store);

        var result = await facade.UpdateReleaseTagAsync("missing", new UpdateReleaseTagRequest("v2.0.0"));

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Release 'missing' not found.", result.Error);
    }

    [Fact]
    public async Task UpdateTag_NonPlanningRelease_ReturnsBadRequest()
    {
        await SeedReleaseAsync(_store, "v1.0.0", status: ReleaseStatus.Released);
        var facade = CreateFacade(_store);

        var result = await facade.UpdateReleaseTagAsync("v1.0.0", new UpdateReleaseTagRequest("v2.0.0"));

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Contains("Only Planning releases can be edited", result.Error);
    }

    // ── UpdateReleaseRepositoriesAsync ────────────────────────────────────────

    [Fact]
    public async Task UpdateRepositories_SetsListAndNotifies()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        var notifier = new DashboardNotifier();
        var notifyCount = 0;
        notifier.OnStateChanged += () => notifyCount++;
        var facade = CreateFacade(_store, notifier: notifier);

        var result = await facade.UpdateReleaseRepositoriesAsync(
            "v1.0.0", new UpdateReleaseRepositoriesRequest(["repo1", "repo2"]));

        Assert.True(result.Success);
        Assert.Equal(["repo1", "repo2"], result.Value!.RepositoryNames);
        Assert.Equal(1, notifyCount);
    }

    [Fact]
    public async Task UpdateRepositories_Null_IsNoOpUpdateWith200()
    {
        await SeedReleaseAsync(_store, "v1.0.0", repos: ["repo1"]);
        var facade = CreateFacade(_store);

        // A null list reaches ReleaseUpdateData as a no-op update followed by a 200 — preserved
        // exactly from the pre-facade handler (the store applies only non-null fields).
        var result = await facade.UpdateReleaseRepositoriesAsync("v1.0.0", new UpdateReleaseRepositoriesRequest(null));

        Assert.True(result.Success);
        // The repository list is unchanged.
        Assert.Equal(["repo1"], result.Value!.RepositoryNames);
    }

    [Fact]
    public async Task UpdateRepositories_UnknownId_ReturnsNotFound()
    {
        var facade = CreateFacade(_store);

        var result = await facade.UpdateReleaseRepositoriesAsync(
            "missing", new UpdateReleaseRepositoriesRequest(["repo1"]));

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Release 'missing' not found.", result.Error);
    }

    [Fact]
    public async Task UpdateRepositories_NonPlanningRelease_ReturnsBadRequest()
    {
        await SeedReleaseAsync(_store, "v1.0.0", status: ReleaseStatus.Released);
        var facade = CreateFacade(_store);

        var result = await facade.UpdateReleaseRepositoriesAsync(
            "v1.0.0", new UpdateReleaseRepositoriesRequest(["repo1"]));

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Contains("Only Planning releases can be edited", result.Error);
    }

    // ── DeleteReleaseAsync: the FIVE failure outcomes + success ───────────────

    [Fact]
    public async Task DeleteRelease_UnknownId_ReturnsNotFound()
    {
        var facade = CreateFacade(_store);

        var result = await facade.DeleteReleaseAsync("missing", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Release 'missing' not found.", result.Error);
    }

    [Fact]
    public async Task DeleteRelease_NonPlanning_ReturnsBadRequest()
    {
        await SeedReleaseAsync(_store, "v1.0.0", status: ReleaseStatus.Released);
        var facade = CreateFacade(_store);

        var result = await facade.DeleteReleaseAsync("v1.0.0", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Only Planning releases can be deleted.", result.Error);
    }

    [Fact]
    public async Task DeleteRelease_Executing_ReturnsConflict()
    {
        await SeedReleaseAsync(_store, "v1.0.0", executionState: ReleaseExecutionState.Executing);
        var facade = CreateFacade(_store);

        var result = await facade.DeleteReleaseAsync("v1.0.0", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Conflict, result.Kind);
        Assert.Equal("Release is currently executing — cannot delete.", result.Error);
    }

    [Fact]
    public async Task DeleteRelease_GoalsAttached_ReturnsBadRequest()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-1", Description = "Attached", ReleaseId = "v1.0.0" }, Ct);
        var facade = CreateFacade(_store);

        var result = await facade.DeleteReleaseAsync("v1.0.0", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Contains("1 goal(s)", result.Error);
    }

    [Fact]
    public async Task DeleteRelease_ConcurrentStateChange_ReturnsConflict()
    {
        // A release that satisfies every pre-check: Planning, not Executing, no goals.
        // The store nevertheless returns false from DeleteReleaseAsync — exactly as the real
        // atomic ExecuteDeleteAsync would when a concurrent change invalidates a precondition.
        var store = new StatefulRecordingStore { DeleteReturnsFalse = true };
        store.UpsertRelease(new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = [] });

        var notifier = new DashboardNotifier();
        var notifyCount = 0;
        notifier.OnStateChanged += () => notifyCount++;
        var facade = CreateFacade(store, notifier: notifier);

        var result = await facade.DeleteReleaseAsync("v1.0.0", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Conflict, result.Kind);
        Assert.Contains("concurrent state change", result.Error, StringComparison.OrdinalIgnoreCase);
        // The dashboard is NOT notified on this branch.
        Assert.Equal(0, notifyCount);
    }

    [Fact]
    public async Task DeleteRelease_Success_ReturnsRemovedResultAndNotifies()
    {
        await SeedReleaseAsync(_store, "v1.0.0");
        var notifier = new DashboardNotifier();
        var notifyCount = 0;
        notifier.OnStateChanged += () => notifyCount++;
        var facade = CreateFacade(_store, notifier: notifier);

        var result = await facade.DeleteReleaseAsync("v1.0.0", Ct);

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.True(result.Value!.Removed);
        Assert.Equal(1, notifyCount);

        // The release is gone from the store.
        Assert.Null(await _store.GetReleaseAsync("v1.0.0", Ct));
    }

    [Fact]
    public async Task DeleteRelease_ForwardsCancellationTokenToStoreCalls()
    {
        var store = new StatefulRecordingStore();
        store.UpsertRelease(new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = [] });
        var facade = CreateFacade(store);

        using var cts = new CancellationTokenSource();
        var result = await facade.DeleteReleaseAsync("v1.0.0", cts.Token);

        Assert.True(result.Success);

        // Every store call received the caller's token.
        Assert.Equal(cts.Token, Assert.Single(store.GetReleaseTokens));
        Assert.Equal(cts.Token, Assert.Single(store.GetGoalsByReleaseTokens));
        Assert.Equal(cts.Token, Assert.Single(store.DeleteReleaseTokens));
    }

    // ── ValidateReleaseAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task Validate_WithExecutionService_ReturnsValidationResult()
    {
        await SeedReleaseAsync(_store, "v1.0.0", repos: ["repo1"]);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, Ct);

        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        var execService = CreateExecutionService(CreateConfig(), fake);
        var facade = CreateFacade(_store, execService);

        var result = await facade.ValidateReleaseAsync("v1.0.0", Ct);

        Assert.True(result.Success);
        Assert.True(result.Value!.Valid);
        Assert.Empty(result.Value!.Errors);
    }

    [Fact]
    public async Task Validate_WithExecutionService_InvalidRelease_ReturnsErrors()
    {
        await SeedReleaseAsync(_store, "v1.0.0", repos: ["repo1"]);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.InProgress }, Ct);

        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        var execService = CreateExecutionService(CreateConfig(), fake);
        var facade = CreateFacade(_store, execService);

        var result = await facade.ValidateReleaseAsync("v1.0.0", Ct);

        Assert.True(result.Success);
        Assert.False(result.Value!.Valid);
        Assert.Contains(result.Value!.Errors, e => e.Contains("not completed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_MissingExecutionService_ReturnsValidTrue()
    {
        await SeedReleaseAsync(_store, "v1.0.0", repos: ["repo1"]);
        var facade = CreateFacade(_store, executionService: null);

        var result = await facade.ValidateReleaseAsync("v1.0.0", Ct);

        Assert.True(result.Success);
        Assert.True(result.Value!.Valid);
        Assert.Empty(result.Value!.Errors);
    }

    [Fact]
    public async Task Validate_UnknownId_ReturnsNotFound()
    {
        var facade = CreateFacade(_store);

        var result = await facade.ValidateReleaseAsync("missing", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Release 'missing' not found.", result.Error);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    /// <summary>A recording <see cref="IEventBus"/> that captures all published events.</summary>
    private sealed class RecordingEventBus : IEventBus
    {
        public List<SystemEvent> Published { get; } = [];

        public event Action<SystemEvent>? OnEvent;

        public void Publish(SystemEvent evt)
        {
            Published.Add(evt);
            OnEvent?.Invoke(evt);
        }
    }

    /// <summary>
    /// Stateful in-memory <see cref="IGoalStore"/> that records every call and token, and lets a
    /// test simulate the store-level failure modes the facade must translate: a second read that
    /// returns a different release state (for the execution service's internal re-read), a
    /// <c>false</c> delete (concurrent state change), and duplicate-ID create.
    /// </summary>
    private sealed class StatefulRecordingStore : IGoalStore
    {
        private readonly Dictionary<string, Release> _releases = new();

        public string Name => "stateful-recording-store";

        /// <summary>When true, the SECOND <see cref="GetReleaseAsync(string, CancellationToken)"/>
        /// call returns null (simulating the execution service's re-read failing to find the release).</summary>
        public bool SecondGetReleaseReturnsNull { get; set; }

        /// <summary>When set, the second <see cref="GetReleaseAsync(string, CancellationToken)"/>
        /// call returns the stored release with this status.</summary>
        public ReleaseStatus? SecondGetReleaseStatus { get; set; }

        /// <summary>When set, the second <see cref="GetReleaseAsync(string, CancellationToken)"/>
        /// call returns the stored release with this execution state.</summary>
        public ReleaseExecutionState? SecondGetReleaseExecutionState { get; set; }

        /// <summary>When true, <see cref="DeleteReleaseAsync"/> returns <c>false</c>.</summary>
        public bool DeleteReturnsFalse { get; set; }

        /// <summary>Goals reported by <see cref="GetGoalsByReleaseAsync"/>.</summary>
        public Dictionary<string, IReadOnlyList<Goal>> GoalsByRelease { get; } = [];

        public List<CancellationToken> GetReleaseTokens { get; } = [];

        public List<CancellationToken> UpdateReleaseTokens { get; } = [];

        public List<CancellationToken> GetGoalsByReleaseTokens { get; } = [];

        public List<CancellationToken> DeleteReleaseTokens { get; } = [];

        public void UpsertRelease(Release release) => _releases[release.Id] = Clone(release);

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
        {
            GetReleaseTokens.Add(ct);
            if (!_releases.TryGetValue(releaseId, out var release))
                return Task.FromResult<Release?>(null);

            // The second call (index 1) is the execution service's internal re-read. Apply the
            // simulated state deviations to the returned copy.
            if (GetReleaseTokens.Count == 2)
            {
                if (SecondGetReleaseReturnsNull)
                    return Task.FromResult<Release?>(null);

                if (SecondGetReleaseStatus is { } status)
                    release = Clone(release, status: status);

                if (SecondGetReleaseExecutionState is { } state)
                    release = Clone(release, executionState: state);
            }

            return Task.FromResult<Release?>(Clone(release));
        }

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
        {
            if (_releases.ContainsKey(release.Id))
                throw new InvalidOperationException(
                    $"Failed to create release '{release.Id}'; a release with the same ID may already exist.");
            _releases[release.Id] = Clone(release);
            return Task.FromResult(release);
        }

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
        {
            UpdateReleaseTokens.Add(ct);
            if (!_releases.ContainsKey(release.Id))
                throw new KeyNotFoundException($"Release '{release.Id}' not found in SQLite store.");
            _releases[release.Id] = Clone(release);
            return Task.CompletedTask;
        }

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default)
        {
            if (!_releases.TryGetValue(releaseId, out var release))
                throw new KeyNotFoundException($"Release '{releaseId}' not found in SQLite store.");
            if (release.Status != ReleaseStatus.Planning)
                throw new InvalidOperationException(
                    $"Release '{releaseId}' cannot be edited because it is in '{release.Status}' status. Only Planning releases can be edited.");

            _releases[releaseId] = new Release
            {
                Id = release.Id,
                Tag = update.Tag ?? release.Tag,
                Status = release.Status,
                Notes = update.Notes ?? release.Notes,
                CreatedAt = release.CreatedAt,
                ReleasedAt = release.ReleasedAt,
                RepositoryNames = update.Repositories ?? [.. release.RepositoryNames],
                ExecutionState = release.ExecutionState,
            };
            return Task.CompletedTask;
        }

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default)
        {
            DeleteReleaseTokens.Add(ct);
            if (DeleteReturnsFalse)
                return Task.FromResult(false);
            return Task.FromResult(_releases.Remove(releaseId));
        }

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default)
        {
            GetGoalsByReleaseTokens.Add(ct);
            return Task.FromResult(GoalsByRelease.TryGetValue(releaseId, out var goals) ? goals : []);
        }

        // ── Unused IGoalStore members (not exercised by these tests) ──────────

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<Goal?>(null);

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
            => Task.FromResult(goal);

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
            string query, GoalStatus? statusFilter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(
            string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IterationSummary>>([]);

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Release>>(_releases.Values.ToList());

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
            string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
            int? limit = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);

        private static Release Clone(
            Release release,
            ReleaseStatus? status = null,
            ReleaseExecutionState? executionState = null) => new()
            {
                Id = release.Id,
                Tag = release.Tag,
                Status = status ?? release.Status,
                Notes = release.Notes,
                CreatedAt = release.CreatedAt,
                ReleasedAt = release.ReleasedAt,
                RepositoryNames = [.. release.RepositoryNames],
                ExecutionState = executionState ?? release.ExecutionState,
            };
    }
}
