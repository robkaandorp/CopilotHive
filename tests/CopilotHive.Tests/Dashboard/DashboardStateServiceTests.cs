using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Dashboard;

/// <summary>
/// Tests for <see cref="DashboardStateService"/> view-model types and
/// the mapping logic that populates them from <see cref="Goal"/> instances.
/// </summary>
public sealed class DashboardStateServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteGoalStore _store;

    /// <summary>Initialises an in-memory SQLite goal store for each test.</summary>
    public DashboardStateServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _store = new SqliteGoalStore(_conn, NullLogger<SqliteGoalStore>.Instance);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _conn.Dispose();
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.DependsOn"/> is populated correctly
    /// when the underlying <see cref="Goal"/> has dependency IDs set.
    /// </summary>
    [Fact]
    public void GoalDetailInfo_DependsOn_PopulatedFromGoal()
    {
        var goal = new Goal
        {
            Id = "child-goal",
            Description = "A goal that depends on others",
            DependsOn = ["parent-goal-1", "parent-goal-2"],
        };

        var detail = new GoalDetailInfo
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Status = goal.Status,
            Priority = goal.Priority,
            DependsOn = goal.DependsOn,
        };

        Assert.Equal(2, detail.DependsOn.Count);
        Assert.Contains("parent-goal-1", detail.DependsOn);
        Assert.Contains("parent-goal-2", detail.DependsOn);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="GoalDetailInfo.DependsOn"/> with the correct dependency IDs
    /// for a goal that was stored with dependencies.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_GoalWithDependencies_PopulatesDependsOn()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange: persist a goal that depends on two other goals
        var dependencyGoalId = "dep-goal-alpha";
        var parentGoal = new Goal
        {
            Id = "parent-goal-beta",
            Description = "Parent goal depending on another",
            DependsOn = [dependencyGoalId],
        };
        await _store.CreateGoalAsync(parentGoal, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);

        // Act
        var detail = service.GetGoalDetail("parent-goal-beta");

        // Assert
        Assert.NotNull(detail);
        Assert.Single(detail.DependsOn);
        Assert.Contains(dependencyGoalId, detail.DependsOn);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.DependsOn"/> defaults to an
    /// empty list when the goal has no dependencies.
    /// </summary>
    [Fact]
    public void GoalDetailInfo_DependsOn_EmptyWhenNoDependencies()
    {
        var goal = new Goal
        {
            Id = "standalone-goal",
            Description = "No dependencies",
        };

        var detail = new GoalDetailInfo
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Status = goal.Status,
            Priority = goal.Priority,
            DependsOn = goal.DependsOn,
        };

        Assert.Empty(detail.DependsOn);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="GoalDetailInfo.DependsOn"/> from the goal stored in the
    /// <see cref="IGoalStore"/> when no active pipeline exists.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_PopulatesDependsOn_FromStoredGoal()
    {
        var ct = TestContext.Current.CancellationToken;

        // Store a goal with dependencies
        var goal = new Goal
        {
            Id = "service-dep-goal",
            Description = "Goal with deps via service",
            DependsOn = ["dep-one", "dep-two"],
        };
        await _store.CreateGoalAsync(goal, ct);

        // Build the DashboardStateService with required dependencies
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);

        // Exercise the service method under test
        var detail = service.GetGoalDetail("service-dep-goal");

        Assert.NotNull(detail);
        Assert.Equal(2, detail.DependsOn.Count);
        Assert.Contains("dep-one", detail.DependsOn);
        Assert.Contains("dep-two", detail.DependsOn);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> returns
    /// an empty <see cref="GoalDetailInfo.DependsOn"/> list when the stored
    /// goal has no dependencies.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_EmptyDependsOn_WhenGoalHasNoDeps()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "no-dep-service-goal",
            Description = "No dependencies via service",
        };
        await _store.CreateGoalAsync(goal, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);

        var detail = service.GetGoalDetail("no-dep-service-goal");

        Assert.NotNull(detail);
        Assert.Empty(detail.DependsOn);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.MergeCommitHash"/> is populated from
    /// the <see cref="Goal.MergeCommitHash"/> stored in the <see cref="IGoalStore"/>
    /// when no active pipeline exists.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_PopulatesMergeCommitHash_FromStoredGoal()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "merged-goal",
            Description = "Goal that was merged",
            MergeCommitHash = "cafebabe0123",
        };
        await _store.CreateGoalAsync(goal, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);

        var detail = service.GetGoalDetail("merged-goal");

        Assert.NotNull(detail);
        Assert.Equal("cafebabe0123", detail.MergeCommitHash);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.MergeCommitHash"/> is null when
    /// the stored goal has no merge commit hash.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_MergeCommitHash_NullWhenNotMerged()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "unmerged-goal",
            Description = "Goal not yet merged",
        };
        await _store.CreateGoalAsync(goal, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);

        var detail = service.GetGoalDetail("unmerged-goal");

        Assert.NotNull(detail);
        Assert.Null(detail.MergeCommitHash);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo"/> exposes a <c>MergeCommitHash</c>
    /// property that can be set via object initialization.
    /// </summary>
    [Fact]
    public void GoalDetailInfo_MergeCommitHash_CanBeSet()
    {
        var detail = new GoalDetailInfo
        {
            GoalId = "test-goal",
            Description = "Test",
            MergeCommitHash = "deadbeef1234",
        };

        Assert.Equal("deadbeef1234", detail.MergeCommitHash);
    }

    // ── RepositoryUrl / GetRepositoryUrl ─────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetRepositoryUrl"/> strips the
    /// <c>.git</c> suffix from a repository URL.
    /// </summary>
    [Fact]
    public void GetRepositoryUrl_StripsGitSuffix_WhenPresent()
    {
        var config = new HiveConfigFile
        {
            Repositories = [new RepositoryConfig { Name = "my-repo", Url = "https://github.com/org/my-repo.git" }],
        };

        var goal = new Goal { Id = "g1", Description = "desc", RepositoryNames = ["my-repo"] };

        using var service = BuildService(config: config);
        var url = service.GetRepositoryUrl(goal);

        Assert.Equal("https://github.com/org/my-repo", url);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetRepositoryUrl"/> returns
    /// the URL unchanged when it does not end with <c>.git</c>.
    /// </summary>
    [Fact]
    public void GetRepositoryUrl_NoGitSuffix_ReturnsUrlUnchanged()
    {
        var config = new HiveConfigFile
        {
            Repositories = [new RepositoryConfig { Name = "my-repo", Url = "https://github.com/org/my-repo" }],
        };

        var goal = new Goal { Id = "g2", Description = "desc", RepositoryNames = ["my-repo"] };

        using var service = BuildService(config: config);
        var url = service.GetRepositoryUrl(goal);

        Assert.Equal("https://github.com/org/my-repo", url);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetRepositoryUrl"/> returns
    /// <c>null</c> when the goal has no repository names.
    /// </summary>
    [Fact]
    public void GetRepositoryUrl_NoRepositoryNames_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Repositories = [new RepositoryConfig { Name = "my-repo", Url = "https://github.com/org/my-repo.git" }],
        };

        var goal = new Goal { Id = "g3", Description = "desc" };

        using var service = BuildService(config: config);
        var url = service.GetRepositoryUrl(goal);

        Assert.Null(url);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetRepositoryUrl"/> returns
    /// <c>null</c> when the goal's repository name is not found in config.
    /// </summary>
    [Fact]
    public void GetRepositoryUrl_UnknownRepoName_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Repositories = [new RepositoryConfig { Name = "known-repo", Url = "https://github.com/org/known-repo.git" }],
        };

        var goal = new Goal { Id = "g4", Description = "desc", RepositoryNames = ["unknown-repo"] };

        using var service = BuildService(config: config);
        var url = service.GetRepositoryUrl(goal);

        Assert.Null(url);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetRepositoryUrl"/> returns
    /// <c>null</c> when no config is provided.
    /// </summary>
    [Fact]
    public void GetRepositoryUrl_NullConfig_ReturnsNull()
    {
        var goal = new Goal { Id = "g5", Description = "desc", RepositoryNames = ["my-repo"] };

        using var service = BuildService(config: null);
        var url = service.GetRepositoryUrl(goal);

        Assert.Null(url);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.RepositoryUrl"/> is populated from
    /// the hive config when a stored goal has a matching repository name.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_PopulatesRepositoryUrl_FromConfig()
    {
        var ct = TestContext.Current.CancellationToken;

        var config = new HiveConfigFile
        {
            Repositories = [new RepositoryConfig { Name = "target-repo", Url = "https://github.com/org/target-repo.git" }],
        };

        var goal = new Goal
        {
            Id = "repo-url-goal",
            Description = "Goal with repository",
            RepositoryNames = ["target-repo"],
        };
        await _store.CreateGoalAsync(goal, ct);

        using var service = BuildService(config: config);

        var detail = service.GetGoalDetail("repo-url-goal");

        Assert.NotNull(detail);
        Assert.Equal("https://github.com/org/target-repo", detail.RepositoryUrl);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.RepositoryUrl"/> is <c>null</c>
    /// when no config is provided to the service.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_RepositoryUrl_NullWhenNoConfig()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "no-config-goal",
            Description = "Goal without config",
            RepositoryNames = ["some-repo"],
        };
        await _store.CreateGoalAsync(goal, ct);

        using var service = BuildService(config: null);

        var detail = service.GetGoalDetail("no-config-goal");

        Assert.NotNull(detail);
        Assert.Null(detail.RepositoryUrl);
    }

    // ── RepositoryNames ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="GoalDetailInfo.RepositoryNames"/> from the stored goal's
    /// <see cref="Goal.RepositoryNames"/>.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_PopulatesRepositoryNames()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "repo-names-goal",
            Description = "Goal with repositories",
            RepositoryNames = ["repo-alpha", "repo-beta"],
        };
        await _store.CreateGoalAsync(goal, ct);

        using var service = BuildService(config: null);

        var detail = service.GetGoalDetail("repo-names-goal");

        Assert.NotNull(detail);
        Assert.Equal(2, detail.RepositoryNames.Count);
        Assert.Contains("repo-alpha", detail.RepositoryNames);
        Assert.Contains("repo-beta", detail.RepositoryNames);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.RepositoryNames"/> is an empty list
    /// (not null) when the stored goal has no repository names.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_RepositoryNames_EmptyWhenGoalHasNoRepos()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "no-repos-goal",
            Description = "Goal without repositories",
            // RepositoryNames defaults to empty list
        };
        await _store.CreateGoalAsync(goal, ct);

        using var service = BuildService(config: null);

        var detail = service.GetGoalDetail("no-repos-goal");

        Assert.NotNull(detail);
        Assert.NotNull(detail.RepositoryNames);
        Assert.Empty(detail.RepositoryNames);
    }

    // ── GetSnapshot — CurrentModel mapping ───────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetSnapshot"/> maps
    /// <c>ConnectedWorker.CurrentModel</c> from the pool
    /// to <c>WorkerInfo.CurrentModel</c> on the snapshot.
    /// </summary>
    [Fact]
    public void GetSnapshot_MapsCurrentModelFromWorkerToInfo()
    {
        // Arrange: register a busy worker and set its CurrentModel
        var workerPool = new WorkerPool();
        var worker = workerPool.RegisterWorker("w-model-test", []);
        workerPool.MarkBusy("w-model-test", "task-1");
        worker.CurrentModel = "gpt-4";

        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        // Act
        var snapshot = service.GetSnapshot();

        // Assert: the WorkerInfo in the snapshot carries the model
        var info = snapshot.Workers.Single(w => w.Id == "w-model-test");
        Assert.Equal("gpt-4", info.CurrentModel);
    }

    /// <summary>
    /// Verifies that <c>WorkerInfo.CurrentModel</c> is <c>null</c> when the
    /// underlying <c>ConnectedWorker.CurrentModel</c>
    /// has not been set (i.e., the worker is idle).
    /// </summary>
    [Fact]
    public void GetSnapshot_CurrentModel_IsNullWhenWorkerIsIdle()
    {
        // Arrange: register a worker but do NOT set CurrentModel (idle state)
        var workerPool = new WorkerPool();
        workerPool.RegisterWorker("w-idle-test", []);

        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        // Act
        var snapshot = service.GetSnapshot();

        // Assert: idle worker has no model
        var info = snapshot.Workers.Single(w => w.Id == "w-idle-test");
        Assert.Null(info.CurrentModel);
    }

    // ── WorkerOutput Priority Tests ─────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> uses
    /// <c>PhaseResult.WorkerOutput</c> (persisted in goal_iterations) when displaying
    /// completed goals that no longer have an active pipeline.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_UsesPersistedWorkerOutput_WhenNoPipeline()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a goal with a completed iteration that has WorkerOutput persisted
        var goal = new Goal
        {
            Id = "completed-goal",
            Description = "Goal with persisted output",
            Status = GoalStatus.Completed,
        };
        await _store.CreateGoalAsync(goal, ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult
                {
                    Name = "Coding",
                    Result = "pass",
                    DurationSeconds = 60.0,
                    WorkerOutput = "Persisted coder output from database.",
                },
                new PhaseResult
                {
                    Name = "Testing",
                    Result = "pass",
                    DurationSeconds = 30.0,
                    WorkerOutput = "All tests passed.",
                },
            ],
        };
        await _store.UpdateGoalStatusAsync("completed-goal", GoalStatus.Completed,
            new GoalUpdateMetadata { Iterations = 1, IterationSummary = summary }, ct);

        // Build service WITHOUT a pipeline for this goal (simulating completed state)
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);

        // Verify the iteration is persisted correctly in the store
        var storedGoal = await _store.GetGoalAsync("completed-goal", ct);
        Assert.NotNull(storedGoal);
        Assert.Single(storedGoal.IterationSummaries);
        Assert.Equal("Persisted coder output from database.", storedGoal.IterationSummaries[0].Phases[0].WorkerOutput);

        // GetGoalDetail must load IterationSummaries from the store for completed goals
        var detail = service.GetGoalDetail("completed-goal");

        Assert.NotNull(detail);
        Assert.Single(detail.Iterations);
        var iteration = detail.Iterations[0];
        Assert.Equal(1, iteration.Number);
        Assert.Equal(3, iteration.Phases.Count);

        var codingPhase = iteration.Phases.First(p => p.Name == "Coding");
        Assert.Equal("Persisted coder output from database.", codingPhase.WorkerOutput);

        var testingPhase = iteration.Phases.First(p => p.Name == "Testing");
        Assert.Equal("All tests passed.", testingPhase.WorkerOutput);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> prefers
    /// <c>PhaseResult.WorkerOutput</c> over live pipeline <c>PhaseOutputs</c> when both exist.
    /// This ensures completed goals display the persisted output even if a stale pipeline exists.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_PrefersPersistedWorkerOutput_OverLivePipeline()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a goal with a completed iteration that has WorkerOutput persisted
        var goal = new Goal
        {
            Id = "prefers-persisted-goal",
            Description = "Goal testing output priority",
            Status = GoalStatus.InProgress,
        };
        await _store.CreateGoalAsync(goal, ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult
                {
                    Name = "Coding",
                    Result = "pass",
                    DurationSeconds = 45.0,
                    WorkerOutput = "Persisted output (authoritative).",
                },
            ],
            PhaseOutputs = new Dictionary<string, string>
            {
                ["coder-1"] = "Persisted output (authoritative).",
            },
        };
        await _store.UpdateGoalStatusAsync("prefers-persisted-goal", GoalStatus.InProgress,
            new GoalUpdateMetadata { Iterations = 1, IterationSummary = summary }, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();

        // Create a pipeline with live output for iteration 2
        // (iteration 1 output comes from persisted summary)
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        // Advance to iteration 2 so the pipeline's iteration-1 outputs don't conflict
        pipeline.IncrementIteration(); // Now at iteration 2
        pipeline.RecordOutput(WorkerRole.Coder, 2, "Live output from iteration 2 (should NOT be shown for iteration 1)");
        pipeline.Metrics.PhaseDurations["Coding"] = TimeSpan.FromSeconds(30);

        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);

        // Verify the iteration summary is persisted correctly
        var storedGoal = await _store.GetGoalAsync("prefers-persisted-goal", ct);
        Assert.NotNull(storedGoal);
        Assert.Single(storedGoal.IterationSummaries);
        Assert.Equal("Persisted output (authoritative).", storedGoal.IterationSummaries[0].Phases[0].WorkerOutput);

        var detail = service.GetGoalDetail("prefers-persisted-goal");

        Assert.NotNull(detail);

        // Iteration 1 from persisted summaries should show the persisted WorkerOutput
        var iteration1 = detail.Iterations.FirstOrDefault(i => i.Number == 1);
        Assert.NotNull(iteration1);
        var codingPhaseIter1 = iteration1.Phases.FirstOrDefault(p => p.Name == "Coding");
        Assert.NotNull(codingPhaseIter1);
        Assert.Equal("Persisted output (authoritative).", codingPhaseIter1.WorkerOutput);

        // Iteration 2 from the live pipeline should be a separate iteration
        var iteration2 = detail.Iterations.FirstOrDefault(i => i.Number == 2);
        Assert.NotNull(iteration2);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> falls back to
    /// live pipeline <c>PhaseOutputs</c> when <c>PhaseResult.WorkerOutput</c> is null or empty.
    /// </summary>
    [Fact]
    public void GetGoalDetail_FallsBackToPipelinePhaseOutputs_WhenWorkerOutputIsNull()
    {
        var goal = new Goal
        {
            Id = "fallback-goal",
            Description = "Goal testing fallback to pipeline",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();

        // Create a pipeline with live output (no persisted WorkerOutput)
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        // Set a plan and advance to Coding so worker phases are visible
        var plan = new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging] };
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);
        // Iteration defaults to 1
        pipeline.RecordOutput(WorkerRole.Coder, 1, "Live pipeline output should be used.");
        pipeline.Metrics.PhaseDurations["Coding"] = TimeSpan.FromSeconds(30);

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = service.GetGoalDetail("fallback-goal");

        Assert.NotNull(detail);
        // Current iteration is built from pipeline when no summary exists
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration); // Pipeline iteration should be marked current

        var codingPhase = currentIteration.Phases.FirstOrDefault(p => p.Name == "Coding");
        // Coding phase exists in current iteration when pipeline has output
        Assert.NotNull(codingPhase);
        // Pipeline output should be used when WorkerOutput is not persisted
        Assert.Equal("Live pipeline output should be used.", codingPhase.WorkerOutput);
    }

    // ── Planning Phase Visibility Tests ─────────────────────────────────

    /// <summary>
    /// During <see cref="GoalPhase.Planning"/> with no Plan set, ONLY the
    /// Planning phase should be shown with <c>"active"</c> status.
    /// </summary>
    [Fact]
    public void GetGoalDetail_DuringPlanning_OnlyShowsPlanningPhaseActive()
    {
        var goal = new Goal
        {
            Id = "planning-only-goal",
            Description = "Goal in planning phase",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        // Pipeline starts in Planning phase by default, no plan set

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = service.GetGoalDetail("planning-only-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);
        // Only the Planning phase should be shown
        Assert.Single(currentIteration.Phases);
        Assert.Equal("Planning", currentIteration.Phases[0].Name);
        Assert.Equal("active", currentIteration.Phases[0].Status);
        // PlanReason should be null since no plan is set
        Assert.Null(currentIteration.PlanReason);
    }

    /// <summary>
    /// After planning completes and a Plan is set, the iteration should show
    /// Planning (<c>"completed"</c>) plus worker phases, and
    /// <see cref="IterationViewInfo.PlanReason"/> should carry the plan's reason.
    /// </summary>
    [Fact]
    public void GetGoalDetail_AfterPlanningComplete_ShowsCompletedPlanningPlusWorkerPhases()
    {
        var goal = new Goal
        {
            Id = "post-planning-goal",
            Description = "Goal past planning",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set a plan with a reason and advance to Coding
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging],
            Reason = "Brain decided to skip review this iteration.",
        };
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = service.GetGoalDetail("post-planning-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);
        // Planning (completed) + 3 worker phases = 4 total
        Assert.Equal(4, currentIteration.Phases.Count);
        Assert.Equal("Planning", currentIteration.Phases[0].Name);
        Assert.Equal("completed", currentIteration.Phases[0].Status);
        Assert.Equal("Coding", currentIteration.Phases[1].Name);
        Assert.Equal("active", currentIteration.Phases[1].Status);
        // PlanReason should be populated from the plan
        Assert.Equal("Brain decided to skip review this iteration.", currentIteration.PlanReason);
    }

    /// <summary>
    /// When the pipeline is past Planning but <see cref="GoalPipeline.Plan"/>
    /// is <c>null</c>, fallback phases (Coding, Testing, Review, Merging)
    /// must still be shown alongside the completed Planning phase.
    /// </summary>
    [Fact]
    public void GetGoalDetail_PastPlanningWithNullPlan_ShowsFallbackWorkerPhases()
    {
        var goal = new Goal
        {
            Id = "null-plan-goal",
            Description = "Goal past planning without a plan",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Advance past Planning WITHOUT setting a plan (Plan remains null)
        pipeline.AdvanceTo(GoalPhase.Coding);

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = service.GetGoalDetail("null-plan-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);
        // Planning (completed) + 4 fallback worker phases = 5 total
        Assert.Equal(5, currentIteration.Phases.Count);
        Assert.Equal("Planning", currentIteration.Phases[0].Name);
        Assert.Equal("completed", currentIteration.Phases[0].Status);
        // Fallback phases: Coding, Testing, Review, Merging
        Assert.Equal("Coding", currentIteration.Phases[1].Name);
        Assert.Equal("Testing", currentIteration.Phases[2].Name);
        Assert.Equal("Review", currentIteration.Phases[3].Name);
        Assert.Equal("Merging", currentIteration.Phases[4].Name);
        // PlanReason should be null since no plan was set
        Assert.Null(currentIteration.PlanReason);
    }

    // ── CurrentModel UI fallback tests ───────────────────────────────────────
    //
    // Workers.razor and WorkerDetail.razor call WorkerInfo.GetDisplayModel(roleModels)
    // to resolve the model to display. These tests exercise that real production method.

    /// <summary>
    /// Verifies that <see cref="CopilotHive.Dashboard.WorkerInfo.GetDisplayModel"/> returns the
    /// role-default model when <see cref="CopilotHive.Dashboard.WorkerInfo.CurrentModel"/> is null
    /// and the worker has a role with a configured default model.
    /// </summary>
    [Fact]
    public void WorkerModelFallback_CurrentModelNull_ReturnRoleDefault()
    {
        var worker = new CopilotHive.Dashboard.WorkerInfo
        {
            Id = "w-fallback-1",
            Role = "Coder",      // non-Unspecified role
            CurrentModel = null, // idle — no active task model
        };

        var roleModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["coder"] = "claude-opus-4",
        };

        // Call the real production helper used by Workers.razor / WorkerDetail.razor
        var modelStr = worker.GetDisplayModel(roleModels);

        Assert.Equal("claude-opus-4", modelStr);
    }

    /// <summary>
    /// Verifies that <see cref="CopilotHive.Dashboard.WorkerInfo.GetDisplayModel"/> returns the
    /// task-specific model when <see cref="CopilotHive.Dashboard.WorkerInfo.CurrentModel"/> is set,
    /// taking precedence over the role config default.
    /// </summary>
    [Fact]
    public void WorkerModelFallback_CurrentModelSet_TaskModelTakesPrecedence()
    {
        var worker = new CopilotHive.Dashboard.WorkerInfo
        {
            Id = "w-fallback-2",
            Role = "Coder",
            CurrentModel = "gpt-4o",  // task-specific model
        };

        var roleModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["coder"] = "claude-opus-4",   // role default — must NOT override the task model
        };

        var modelStr = worker.GetDisplayModel(roleModels);

        Assert.Equal("gpt-4o", modelStr);
    }

    /// <summary>
    /// Verifies that <see cref="CopilotHive.Dashboard.WorkerInfo.GetDisplayModel"/> returns <c>null</c>
    /// when <see cref="CopilotHive.Dashboard.WorkerInfo.CurrentModel"/> is null AND the worker role
    /// is <c>"Unspecified"</c> (no model to display).
    /// </summary>
    [Fact]
    public void WorkerModelFallback_UnspecifiedRole_ReturnsNull()
    {
        var worker = new CopilotHive.Dashboard.WorkerInfo
        {
            Id = "w-fallback-3",
            Role = "Unspecified",
            CurrentModel = null,
        };

        var roleModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["coder"] = "claude-opus-4",
        };

        var modelStr = worker.GetDisplayModel(roleModels);

        Assert.Null(modelStr);
    }

    /// <summary>
    /// Verifies that <see cref="CopilotHive.Dashboard.WorkerInfo.GetDisplayModel"/> returns <c>null</c>
    /// when the worker has a valid role but the role is not present in the role models map.
    /// </summary>
    [Fact]
    public void WorkerModelFallback_RoleNotInMap_ReturnsNull()
    {
        var worker = new CopilotHive.Dashboard.WorkerInfo
        {
            Id = "w-fallback-4",
            Role = "Reviewer",
            CurrentModel = null,
        };

        // Role map does NOT contain "reviewer"
        var roleModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["coder"] = "claude-opus-4",
        };

        var modelStr = worker.GetDisplayModel(roleModels);

        Assert.Null(modelStr);
    }

    /// <summary>
    /// Verifies that <see cref="CopilotHive.Dashboard.WorkerInfo.GetDisplayModel"/> works
    /// with an empty role models dictionary, returning only the <see cref="CopilotHive.Dashboard.WorkerInfo.CurrentModel"/>
    /// when set, or <c>null</c> when idle.
    /// </summary>
    [Fact]
    public void WorkerModelFallback_EmptyRoleModels_ReturnsCurrentModelOrNull()
    {
        var emptyRoleModels = new Dictionary<string, string>();

        var busyWorker = new CopilotHive.Dashboard.WorkerInfo
        {
            Id = "w-fallback-5a",
            Role = "Coder",
            CurrentModel = "gpt-4o",
        };
        Assert.Equal("gpt-4o", busyWorker.GetDisplayModel(emptyRoleModels));

        var idleWorker = new CopilotHive.Dashboard.WorkerInfo
        {
            Id = "w-fallback-5b",
            Role = "Coder",
            CurrentModel = null,
        };
        Assert.Null(idleWorker.GetDisplayModel(emptyRoleModels));
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetOrchestratorInfo"/> populates
    /// <see cref="OrchestratorInfo.RoleModels"/> for all known roles, so the Workers.razor
    /// fallback has model data available for every valid role.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_PopulatesRoleModels_ForAllKnownRoles()
    {
        var config = new HiveConfigFile
        {
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        // The Workers.razor fallback uses role.ToLowerInvariant() as the lookup key.
        // All five roles must be present so the fallback works for any active worker.
        Assert.True(info.RoleModels.ContainsKey("coder"),     "RoleModels missing 'coder'");
        Assert.True(info.RoleModels.ContainsKey("tester"),    "RoleModels missing 'tester'");
        Assert.True(info.RoleModels.ContainsKey("reviewer"),  "RoleModels missing 'reviewer'");
        Assert.True(info.RoleModels.ContainsKey("docwriter"), "RoleModels missing 'docwriter'");
        Assert.True(info.RoleModels.ContainsKey("improver"),  "RoleModels missing 'improver'");
    }

    /// <summary>
    /// Helper that constructs a <see cref="DashboardStateService"/> with the given
    /// optional config and an in-memory SQLite goal store.
    /// </summary>
    private DashboardStateService BuildService(HiveConfigFile? config)
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        return new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store, config: config);
    }

    // ── GetVersion ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetVersion"/> returns a non-empty version string.
    /// </summary>
    [Fact]
    public void GetVersion_ReturnsNonEmptyString()
    {
        using var service = BuildService(config: null);
        var version = service.GetVersion();
        Assert.NotNull(version);
        Assert.NotEmpty(version);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetVersion"/> returns the same version
    /// on repeated calls (idempotent).
    /// </summary>
    [Fact]
    public void GetVersion_ReturnsSameValueOnRepeatedCalls()
    {
        using var service = BuildService(config: null);
        var version1 = service.GetVersion();
        var version2 = service.GetVersion();
        Assert.Equal(version1, version2);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetVersion"/> returns the same version
    /// as <see cref="OrchestratorInfo.Version"/>.
    /// </summary>
    [Fact]
    public void GetVersion_MatchesOrchestratorInfoVersion()
    {
        using var service = BuildService(config: null);
        var version = service.GetVersion();
        var orchestratorInfo = service.GetOrchestratorInfo();
        Assert.Equal(version, orchestratorInfo.Version);
    }

    // ── ExtractPlanningPrompts ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.ExtractPlanningPrompts"/> returns null for
    /// both values when there are no planning entries for the given iteration.
    /// </summary>
    [Fact]
    public void ExtractPlanningPrompts_NoEntries_ReturnsNulls()
    {
        var entries = new List<ConversationEntry>
        {
            new ConversationEntry("user", "Some other message", 1, "craft-prompt"),
        };

        var (userPrompt, assistantResponse) = DashboardStateService.ExtractPlanningPrompts(entries, 1);

        Assert.Null(userPrompt);
        Assert.Null(assistantResponse);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.ExtractPlanningPrompts"/> correctly extracts
    /// the user prompt and assistant response for a given iteration.
    /// </summary>
    [Fact]
    public void ExtractPlanningPrompts_WithPlanningEntries_ExtractsBothPrompts()
    {
        var entries = new List<ConversationEntry>
        {
            new ConversationEntry("user", "Plan this goal now", 1, "planning"),
            new ConversationEntry("assistant", "Here is my plan: ...", 1, "planning"),
        };

        var (userPrompt, assistantResponse) = DashboardStateService.ExtractPlanningPrompts(entries, 1);

        Assert.Equal("Plan this goal now", userPrompt);
        Assert.Equal("Here is my plan: ...", assistantResponse);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.ExtractPlanningPrompts"/> only returns entries
    /// matching the requested iteration, ignoring entries from other iterations.
    /// </summary>
    [Fact]
    public void ExtractPlanningPrompts_FiltersToRequestedIteration()
    {
        var entries = new List<ConversationEntry>
        {
            new ConversationEntry("user", "Iter 1 prompt", 1, "planning"),
            new ConversationEntry("assistant", "Iter 1 response", 1, "planning"),
            new ConversationEntry("user", "Iter 2 prompt", 2, "planning"),
            new ConversationEntry("assistant", "Iter 2 response", 2, "planning"),
        };

        var (userPrompt, assistantResponse) = DashboardStateService.ExtractPlanningPrompts(entries, 2);

        Assert.Equal("Iter 2 prompt", userPrompt);
        Assert.Equal("Iter 2 response", assistantResponse);
    }

    // ── ExtractCraftPrompts ────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.ExtractCraftPrompts"/> returns an empty
    /// dictionary when there are no craft-prompt or worker-output entries.
    /// </summary>
    [Fact]
    public void ExtractCraftPrompts_NoEntries_ReturnsEmpty()
    {
        var entries = new List<ConversationEntry>
        {
            new ConversationEntry("user", "Planning prompt", 1, "planning"),
        };

        var result = DashboardStateService.ExtractCraftPrompts(entries, 1);

        Assert.Empty(result);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.ExtractCraftPrompts"/> associates
    /// a craft-prompt pair with the subsequent worker-output role.
    /// </summary>
    [Fact]
    public void ExtractCraftPrompts_SingleWorker_AssociatesCorrectly()
    {
        var entries = new List<ConversationEntry>
        {
            new ConversationEntry("user", "Write code for this", 1, "craft-prompt"),
            new ConversationEntry("assistant", "Your task: implement feature X", 1, "craft-prompt"),
            new ConversationEntry("coder", "I implemented feature X.", 1, "worker-output"),
        };

        var result = DashboardStateService.ExtractCraftPrompts(entries, 1);

        Assert.True(result.ContainsKey("coder"));
        Assert.Equal("Write code for this", result["coder"].BrainPrompt);
        Assert.Equal("Your task: implement feature X", result["coder"].WorkerPrompt);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.ExtractCraftPrompts"/> correctly associates
    /// separate craft-prompt pairs with each worker role in order.
    /// </summary>
    [Fact]
    public void ExtractCraftPrompts_MultipleWorkers_AssociatesEachCorrectly()
    {
        var entries = new List<ConversationEntry>
        {
            new ConversationEntry("user", "Brain asks coder", 1, "craft-prompt"),
            new ConversationEntry("assistant", "Crafted coder prompt", 1, "craft-prompt"),
            new ConversationEntry("coder", "Coder done.", 1, "worker-output"),
            new ConversationEntry("user", "Brain asks tester", 1, "craft-prompt"),
            new ConversationEntry("assistant", "Crafted tester prompt", 1, "craft-prompt"),
            new ConversationEntry("tester", "Tester done.", 1, "worker-output"),
        };

        var result = DashboardStateService.ExtractCraftPrompts(entries, 1);

        Assert.True(result.ContainsKey("coder"));
        Assert.Equal("Brain asks coder", result["coder"].BrainPrompt);
        Assert.Equal("Crafted coder prompt", result["coder"].WorkerPrompt);

        Assert.True(result.ContainsKey("tester"));
        Assert.Equal("Brain asks tester", result["tester"].BrainPrompt);
        Assert.Equal("Crafted tester prompt", result["tester"].WorkerPrompt);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.ExtractCraftPrompts"/> filters to the
    /// requested iteration and ignores entries from other iterations.
    /// </summary>
    [Fact]
    public void ExtractCraftPrompts_FiltersToRequestedIteration()
    {
        var entries = new List<ConversationEntry>
        {
            new ConversationEntry("user", "Iter 1 brain prompt", 1, "craft-prompt"),
            new ConversationEntry("assistant", "Iter 1 worker prompt", 1, "craft-prompt"),
            new ConversationEntry("coder", "Iter 1 output", 1, "worker-output"),
            new ConversationEntry("user", "Iter 2 brain prompt", 2, "craft-prompt"),
            new ConversationEntry("assistant", "Iter 2 worker prompt", 2, "craft-prompt"),
            new ConversationEntry("coder", "Iter 2 output", 2, "worker-output"),
        };

        var result = DashboardStateService.ExtractCraftPrompts(entries, 2);

        Assert.True(result.ContainsKey("coder"));
        Assert.Equal("Iter 2 brain prompt", result["coder"].BrainPrompt);
        Assert.Equal("Iter 2 worker prompt", result["coder"].WorkerPrompt);
        // Should not contain iter 1 overwriting iter 2
        Assert.Single(result);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.ExtractCraftPrompts"/> handles the case where
    /// a worker-output appears without a preceding craft-prompt (legacy or missing entries).
    /// </summary>
    [Fact]
    public void ExtractCraftPrompts_WorkerOutputWithoutCraftPrompt_HasNullPrompts()
    {
        var entries = new List<ConversationEntry>
        {
            new ConversationEntry("coder", "Coder just did it.", 1, "worker-output"),
        };

        var result = DashboardStateService.ExtractCraftPrompts(entries, 1);

        Assert.True(result.ContainsKey("coder"));
        Assert.Null(result["coder"].BrainPrompt);
        Assert.Null(result["coder"].WorkerPrompt);
    }

    // ── Regression: ExtractPlanningPrompts — multiple entries ──────────────

    /// <summary>
    /// Regression: when <c>PlanIterationAsync</c> is called more than once (nudge/retry),
    /// multiple planning entries are appended. <see cref="DashboardStateService.ExtractPlanningPrompts"/>
    /// must return the LAST pair, not the first.
    /// </summary>
    [Fact]
    public void ExtractPlanningPrompts_MultiplePlanningEntries_ReturnsLastPair()
    {
        var entries = new List<ConversationEntry>
        {
            // First attempt (should be ignored)
            new ConversationEntry("user",      "Plan attempt 1",      1, "planning"),
            new ConversationEntry("assistant", "Response attempt 1",  1, "planning"),
            // Second (nudge) attempt — this is the authoritative one
            new ConversationEntry("user",      "Plan attempt 2 nudge", 1, "planning"),
            new ConversationEntry("assistant", "Response attempt 2",   1, "planning"),
        };

        var (userPrompt, assistantResponse) = DashboardStateService.ExtractPlanningPrompts(entries, 1);

        Assert.Equal("Plan attempt 2 nudge", userPrompt);
        Assert.Equal("Response attempt 2",   assistantResponse);
    }

    // ── Regression: ExtractCraftPrompts — AskBrainAsync mid-task follow-ups ──

    /// <summary>
    /// Regression: <c>AskBrainAsync</c> adds more craft-prompt entries after the initial
    /// dispatch pair. <see cref="DashboardStateService.ExtractCraftPrompts"/> must keep
    /// only the FIRST pair (the dispatch prompt), not the follow-up Q&amp;A.
    /// </summary>
    [Fact]
    public void ExtractCraftPrompts_MidTaskFollowUp_ShowsDispatchPromptNotFollowUp()
    {
        var entries = new List<ConversationEntry>
        {
            // Initial dispatch pair (first — should be retained)
            new ConversationEntry("user",      "Dispatch: write the feature",  1, "craft-prompt"),
            new ConversationEntry("assistant", "Task: implement feature X",    1, "craft-prompt"),
            // Mid-task follow-up via AskBrainAsync (should NOT overwrite the dispatch prompt)
            new ConversationEntry("user",      "Follow-up question",           1, "craft-prompt"),
            new ConversationEntry("assistant", "Follow-up answer",             1, "craft-prompt"),
            // Worker reports output after the follow-up
            new ConversationEntry("coder",     "Coder finished with help.",    1, "worker-output"),
        };

        var result = DashboardStateService.ExtractCraftPrompts(entries, 1);

        Assert.True(result.ContainsKey("coder"));
        // Must show the FIRST (dispatch) prompt, not the follow-up
        Assert.Equal("Dispatch: write the feature", result["coder"].BrainPrompt);
        Assert.Equal("Task: implement feature X",   result["coder"].WorkerPrompt);
    }

    // ── GetGoalDetail: inline prompts in phases ──────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="PhaseViewInfo.BrainPrompt"/> and <see cref="PhaseViewInfo.WorkerPrompt"/>
    /// for worker phases from the pipeline conversation.
    /// </summary>
    [Fact]
    public void GetGoalDetail_PopulatesPhasePrompts_FromPipelineConversation()
    {
        var goal = new Goal
        {
            Id = "prompt-goal",
            Description = "Goal with phase prompts",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        var plan = new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging] };
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);

        // Add craft-prompt + worker-output conversation entries
        pipeline.Conversation.Add(new ConversationEntry("user", "Brain prompt for coder", 1, "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Crafted coder task", 1, "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("coder", "Coder finished.", 1, "worker-output"));

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = service.GetGoalDetail("prompt-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);

        var codingPhase = currentIteration.Phases.FirstOrDefault(p => p.Name == "Coding");
        Assert.NotNull(codingPhase);
        Assert.Equal("Brain prompt for coder", codingPhase.BrainPrompt);
        Assert.Equal("Crafted coder task", codingPhase.WorkerPrompt);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="IterationViewInfo.PlanningBrainPrompt"/> and <see cref="IterationViewInfo.PlanningBrainResponse"/>
    /// from planning entries in the pipeline conversation.
    /// </summary>
    [Fact]
    public void GetGoalDetail_PopulatesPlanningPrompts_FromPipelineConversation()
    {
        var goal = new Goal
        {
            Id = "planning-prompt-goal",
            Description = "Goal testing planning prompts",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Add planning conversation entries
        pipeline.Conversation.Add(new ConversationEntry("user", "Please plan iteration 1", 1, "planning"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "I will code and test.", 1, "planning"));

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = service.GetGoalDetail("planning-prompt-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);

        Assert.Equal("Please plan iteration 1", currentIteration.PlanningBrainPrompt);
        Assert.Equal("I will code and test.", currentIteration.PlanningBrainResponse);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> returns null for
    /// <see cref="IterationViewInfo.PlanningBrainPrompt"/> and <see cref="IterationViewInfo.PlanningBrainResponse"/>
    /// when no planning entries exist in the pipeline conversation.
    /// </summary>
    [Fact]
    public void GetGoalDetail_NullPlanningPrompts_WhenNoPlanningEntriesExist()
    {
        var goal = new Goal
        {
            Id = "no-planning-prompt-goal",
            Description = "Goal with no planning entries",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        pipelineManager.CreatePipeline(goal, maxRetries: 3);

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = service.GetGoalDetail("no-planning-prompt-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);

        Assert.Null(currentIteration.PlanningBrainPrompt);
        Assert.Null(currentIteration.PlanningBrainResponse);
    }

    /// <summary>
    /// Verifies that phase prompts are null when no conversation entries exist.
    /// </summary>
    [Fact]
    public void GetGoalDetail_NullPhasePrompts_WhenNoConversationEntries()
    {
        var goal = new Goal
        {
            Id = "no-conv-prompt-goal",
            Description = "Goal with no conversation",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        var plan = new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Merging] };
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);
        // No conversation entries added

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = service.GetGoalDetail("no-conv-prompt-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);

        var codingPhase = currentIteration.Phases.FirstOrDefault(p => p.Name == "Coding");
        Assert.NotNull(codingPhase);
        Assert.Null(codingPhase.BrainPrompt);
        Assert.Null(codingPhase.WorkerPrompt);
    }

    /// <summary>
    /// Verifies that completed iterations (no live pipeline) show null prompts
    /// because there's no conversation to extract from.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_CompletedIteration_ShowsNullPrompts()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a completed goal with iteration summary but NO pipeline
        var goal = new Goal
        {
            Id = "completed-iteration-goal",
            Description = "Completed goal with iteration summary",
            Status = GoalStatus.Completed,
        };
        await _store.CreateGoalAsync(goal, ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult
                {
                    Name = "Coding",
                    Result = "pass",
                    DurationSeconds = 60.0,
                    WorkerOutput = "Coder completed work.",
                },
            ],
        };
        await _store.UpdateGoalStatusAsync("completed-iteration-goal", GoalStatus.Completed,
            new GoalUpdateMetadata { Iterations = 1, IterationSummary = summary }, ct);

        // Build service WITHOUT a pipeline for this goal (simulating completed state)
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);

        var detail = service.GetGoalDetail("completed-iteration-goal");

        Assert.NotNull(detail);
        Assert.Single(detail.Iterations);
        var iteration = detail.Iterations[0];
        Assert.False(iteration.IsCurrent); // Not current - completed

        // Planning prompts should be null (no pipeline conversation)
        Assert.Null(iteration.PlanningBrainPrompt);
        Assert.Null(iteration.PlanningBrainResponse);

        // Phase prompts should be null (no pipeline conversation)
        var codingPhase = iteration.Phases.FirstOrDefault(p => p.Name == "Coding");
        Assert.NotNull(codingPhase);
        Assert.Null(codingPhase.BrainPrompt);
        Assert.Null(codingPhase.WorkerPrompt);
        // But WorkerOutput should still be populated from the stored summary
        Assert.Equal("Coder completed work.", codingPhase.WorkerOutput);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.Scope"/> is populated from the underlying <see cref="Goal"/>.
    /// </summary>
    [Fact]
    public void GoalDetailInfo_Scope_ReflectsGoalScope()
    {
        var detail = new GoalDetailInfo
        {
            GoalId = "scoped-goal",
            Description = "A feature goal",
            Status = GoalStatus.Pending,
            Priority = GoalPriority.Normal,
            Scope = GoalScope.Feature,
        };

        Assert.Equal(GoalScope.Feature, detail.Scope);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="GoalDetailInfo.Scope"/> from a stored goal.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_GoalWithScope_PopulatesScope()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "breaking-goal",
            Description = "Breaking change",
            Scope = GoalScope.Breaking,
            Status = GoalStatus.Pending,
        };
        await _store.CreateGoalAsync(goal, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);

        var detail = service.GetGoalDetail("breaking-goal");

        Assert.NotNull(detail);
        Assert.Equal(GoalScope.Breaking, detail!.Scope);
    }

    // ── Release methods ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetReleasesAsync"/> returns all releases
    /// when no repository filter is applied.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_NoFilter_ReturnsAllReleases()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0" }, ct);
        await _store.CreateReleaseAsync(new Release { Id = "v2.0.0", Tag = "v2.0.0" }, ct);

        using var service = CreateService();

        var releases = await service.GetReleasesAsync(ct: ct);

        Assert.Equal(2, releases.Count);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetReleasesAsync"/> filters by repository name.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_WithRepoFilter_ReturnsMatchingReleases()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo-a"] }, ct);
        await _store.CreateReleaseAsync(new Release { Id = "v2.0.0", Tag = "v2.0.0", RepositoryNames = ["repo-b"] }, ct);

        using var service = CreateService();

        var releases = await service.GetReleasesAsync(repository: "repo-a", ct: ct);

        Assert.Single(releases);
        Assert.Equal("v1.0.0", releases[0].Id);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetReleasesAsync"/> returns empty list
    /// when goal store is not configured.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_NoGoalStore_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var releases = await service.GetReleasesAsync(ct: ct);

        Assert.Empty(releases);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetReleaseDetailAsync"/> returns the release by ID.
    /// </summary>
    [Fact]
    public async Task GetReleaseDetailAsync_ExistingRelease_ReturnsRelease()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v3.0.0", Tag = "v3.0.0" }, ct);

        using var service = CreateService();

        var release = await service.GetReleaseDetailAsync("v3.0.0", ct);

        Assert.NotNull(release);
        Assert.Equal("v3.0.0", release!.Id);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetReleaseDetailAsync"/> returns null for unknown IDs.
    /// </summary>
    [Fact]
    public async Task GetReleaseDetailAsync_UnknownRelease_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        using var service = CreateService();

        var release = await service.GetReleaseDetailAsync("no-such-release", ct);

        Assert.Null(release);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.CreateReleaseAsync"/> creates a release
    /// with the given version and repository.
    /// </summary>
    [Fact]
    public async Task CreateReleaseAsync_ValidParams_CreatesRelease()
    {
        var ct = TestContext.Current.CancellationToken;

        using var service = CreateService();

        var release = await service.CreateReleaseAsync("my-repo", "v4.0.0", ct);

        Assert.Equal("v4.0.0", release.Id);
        Assert.Equal("v4.0.0", release.Tag);
        Assert.Contains("my-repo", release.RepositoryNames);
        Assert.Equal(ReleaseStatus.Planning, release.Status);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.UpdateReleaseAsync"/> persists changes.
    /// </summary>
    [Fact]
    public async Task UpdateReleaseAsync_ChangesStatus_Persists()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v5.0.0", Tag = "v5.0.0" }, ct);

        using var service = CreateService();

        var release = await _store.GetReleaseAsync("v5.0.0", ct);
        release!.Status = ReleaseStatus.Released;
        await service.UpdateReleaseAsync(release, ct);

        var updated = await _store.GetReleaseAsync("v5.0.0", ct);
        Assert.Equal(ReleaseStatus.Released, updated!.Status);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalsByReleaseAsync"/> returns goals for a release.
    /// </summary>
    [Fact]
    public async Task GetGoalsByReleaseAsync_AssignedGoals_ReturnsGoals()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v6.0.0", Tag = "v6.0.0" }, ct);
        var goal = new Goal { Id = "release-goal-1", Description = "A goal in v6.0.0", ReleaseId = "v6.0.0" };
        await _store.CreateGoalAsync(goal, ct);

        using var service = CreateService();

        var goals = await service.GetGoalsByReleaseAsync("v6.0.0", ct);

        Assert.Single(goals);
        Assert.Equal("release-goal-1", goals[0].Id);
    }

    private DashboardStateService CreateService()
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();
        return new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);
    }
}

/// <summary>
/// Minimal <see cref="IGoalSource"/> for testing worker output fallback without SQLite.
/// </summary>
file sealed class FakeGoalSourceForOutputTests : IGoalSource
{
    private readonly Goal _goal;

    public FakeGoalSourceForOutputTests(Goal goal) => _goal = goal;

    public string Name => "fake-output-test";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;
}
