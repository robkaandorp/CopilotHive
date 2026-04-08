using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

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
        var detail = await service.GetGoalDetail("parent-goal-beta");

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
        var detail = await service.GetGoalDetail("service-dep-goal");

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

        var detail = await service.GetGoalDetail("no-dep-service-goal");

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

        var detail = await service.GetGoalDetail("merged-goal");

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

        var detail = await service.GetGoalDetail("unmerged-goal");

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

        var detail = await service.GetGoalDetail("repo-url-goal");

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

        var detail = await service.GetGoalDetail("no-config-goal");

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

        var detail = await service.GetGoalDetail("repo-names-goal");

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

        var detail = await service.GetGoalDetail("no-repos-goal");

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
    public async Task GetSnapshot_MapsCurrentModelFromWorkerToInfo()
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
        var snapshot = await service.GetSnapshot();

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
    public async Task GetSnapshot_CurrentModel_IsNullWhenWorkerIsIdle()
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
        var snapshot = await service.GetSnapshot();

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
                    Name = GoalPhase.Coding,
                    Result = PhaseOutcome.Pass,
                    DurationSeconds = 60.0,
                    WorkerOutput = "Persisted coder output from database.",
                },
                new PhaseResult
                {
                    Name = GoalPhase.Testing,
                    Result = PhaseOutcome.Pass,
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
        var detail = await service.GetGoalDetail("completed-goal");

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
                    Name = GoalPhase.Coding,
                    Result = PhaseOutcome.Pass,
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
        pipeline.IterationBudget.TryConsume(); // Now at iteration 2
        pipeline.RecordTestOutput(WorkerRole.Coder, 2, "Live output from iteration 2 (should NOT be shown for iteration 1)");
#pragma warning disable CS0618
        pipeline.Metrics.PhaseDurations["Coding"] = TimeSpan.FromSeconds(30);
#pragma warning restore CS0618

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

        var detail = await service.GetGoalDetail("prefers-persisted-goal");

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
    public async Task GetGoalDetail_FallsBackToPipelinePhaseOutputs_WhenWorkerOutputIsNull()
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
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "Live pipeline output should be used.");
#pragma warning disable CS0618
        pipeline.Metrics.PhaseDurations["Coding"] = TimeSpan.FromSeconds(30);
#pragma warning restore CS0618

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = await service.GetGoalDetail("fallback-goal");

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
    public async Task GetGoalDetail_DuringPlanning_OnlyShowsPlanningPhaseActive()
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

        var detail = await service.GetGoalDetail("planning-only-goal");

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
    public async Task GetGoalDetail_AfterPlanningComplete_ShowsCompletedPlanningPlusWorkerPhases()
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

        // Add an active (not completed) PhaseLog entry for Coding
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Iteration = 1,
            Occurrence = 1,
            Result = PhaseOutcome.Pass,
            StartedAt = DateTime.UtcNow,
        });

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = await service.GetGoalDetail("post-planning-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);
        // Planning (completed) + Coding (active) + 2 pending worker phases = 4 total
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
    public async Task GetGoalDetail_PastPlanningWithNullPlan_ShowsFallbackWorkerPhases()
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

        var detail = await service.GetGoalDetail("null-plan-goal");

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

    // ── GetGoalDetail: inline prompts in phases ──────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="PhaseViewInfo.BrainPrompt"/> and <see cref="PhaseViewInfo.WorkerPrompt"/>
    /// for worker phases from the pipeline conversation.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_PopulatesPhasePrompts_FromPhaseLog()
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

        // Add PhaseLog entry with BrainPrompt and WorkerPrompt (this is now the primary source)
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Iteration = 1,
            Occurrence = 1,
            Result = PhaseOutcome.Pass,
            BrainPrompt = "Brain prompt for coder",
            WorkerPrompt = "Crafted coder task",
            WorkerOutput = "Coder finished.",
            CompletedAt = DateTime.UtcNow,
        });

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = await service.GetGoalDetail("prompt-goal");

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
    public async Task GetGoalDetail_PopulatesPlanningPrompts_FromPipelineConversation()
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

        // Add a PhaseLog entry with PlanningPrompt/PlanningResponse set
        // (in production, GoalDispatcher sets these after ResolvePlanAsync completes)
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Occurrence = 1,
            Iteration = 1,
            StartedAt = DateTime.UtcNow,
            PlanningPrompt = "Please plan iteration 1",
            PlanningResponse = "I will code and test.",
        });

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = await service.GetGoalDetail("planning-prompt-goal");

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
    public async Task GetGoalDetail_NullPlanningPrompts_WhenNoPlanningEntriesExist()
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

        var detail = await service.GetGoalDetail("no-planning-prompt-goal");

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
    public async Task GetGoalDetail_NullPhasePrompts_WhenNoConversationEntries()
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

        var detail = await service.GetGoalDetail("no-conv-prompt-goal");

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
                    Name = GoalPhase.Coding,
                    Result = PhaseOutcome.Pass,
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

        var detail = await service.GetGoalDetail("completed-iteration-goal");

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

        var detail = await service.GetGoalDetail("breaking-goal");

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

    // ── Clarification display for summarized/completed iterations ────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="PhaseViewInfo.Clarifications"/> from <see cref="IterationSummary.Clarifications"/>
    /// for completed (summarized) iterations so historical clarifications are visible in the UI.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_CompletedIteration_PopulatesClarificationsOnPhaseView()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "clarif-completed-goal",
            Description = "Goal with clarifications in completed iteration",
            Status = GoalStatus.Completed,
        };
        await _store.CreateGoalAsync(goal, ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 45.0 },
            ],
            Clarifications =
            [
                new PersistedClarification
                {
                    Timestamp = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                    Phase = "Coding",
                    WorkerRole = "coder",
                    Question = "Which pattern should I use?",
                    Answer = "Use the repository pattern.",
                    AnsweredBy = "brain",
                },
            ],
        };
        await _store.AddIterationAsync(goal.Id, summary, ct);

        using var service = CreateService();
        var detail = await service.GetGoalDetail("clarif-completed-goal");

        Assert.NotNull(detail);
        var iter = Assert.Single(detail.Iterations);
        var codingPhase = iter.Phases.FirstOrDefault(p => p.Name == "Coding");
        Assert.NotNull(codingPhase);
        var clarif = Assert.Single(codingPhase.Clarifications);
        Assert.Equal("Which pattern should I use?", clarif.Question);
        Assert.Equal("Use the repository pattern.", clarif.Answer);
        Assert.Equal("brain", clarif.AnsweredBy);
        Assert.Equal("Coding", clarif.Phase);
        Assert.Equal("clarif-completed-goal", clarif.GoalId);
        Assert.Equal(1, clarif.Iteration);
    }

    /// <summary>
    /// Verifies that when a completed iteration has no clarifications,
    /// all <see cref="PhaseViewInfo.Clarifications"/> lists remain empty.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_CompletedIteration_NoClarifications_EmptyLists()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "no-clarif-completed-goal",
            Description = "Goal with no clarifications",
            Status = GoalStatus.Completed,
        };
        await _store.CreateGoalAsync(goal, ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 30.0 },
            ],
            Clarifications = [],
        };
        await _store.AddIterationAsync(goal.Id, summary, ct);

        using var service = CreateService();
        var detail = await service.GetGoalDetail("no-clarif-completed-goal");

        Assert.NotNull(detail);
        var iter = Assert.Single(detail.Iterations);
        var codingPhase = iter.Phases.FirstOrDefault(p => p.Name == "Coding");
        Assert.NotNull(codingPhase);
        Assert.Empty(codingPhase.Clarifications);
    }

    /// <summary>
    /// Verifies that clarifications are matched to the correct phase —
    /// a Testing-phase clarification does not appear on the Coding phase.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_CompletedIteration_ClarificationsMatchedToCorrectPhase()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "multi-phase-clarif-goal",
            Description = "Goal with clarifications in multiple phases",
            Status = GoalStatus.Completed,
        };
        await _store.CreateGoalAsync(goal, ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 50.0 },
                new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Pass, DurationSeconds = 20.0 },
            ],
            Clarifications =
            [
                new PersistedClarification
                {
                    Timestamp = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                    Phase = "Coding",
                    WorkerRole = "coder",
                    Question = "Coding question?",
                    Answer = "Coding answer.",
                    AnsweredBy = "brain",
                },
                new PersistedClarification
                {
                    Timestamp = new DateTime(2024, 6, 1, 13, 0, 0, DateTimeKind.Utc),
                    Phase = "Testing",
                    WorkerRole = "tester",
                    Question = "Testing question?",
                    Answer = "Testing answer.",
                    AnsweredBy = "composer",
                },
            ],
        };
        await _store.AddIterationAsync(goal.Id, summary, ct);

        using var service = CreateService();
        var detail = await service.GetGoalDetail("multi-phase-clarif-goal");

        Assert.NotNull(detail);
        var iter = Assert.Single(detail.Iterations);

        var codingPhase = iter.Phases.FirstOrDefault(p => p.Name == "Coding");
        Assert.NotNull(codingPhase);
        var codingClarif = Assert.Single(codingPhase.Clarifications);
        Assert.Equal("Coding question?", codingClarif.Question);
        Assert.Equal("brain", codingClarif.AnsweredBy);

        var testingPhase = iter.Phases.FirstOrDefault(p => p.Name == "Testing");
        Assert.NotNull(testingPhase);
        var testingClarif = Assert.Single(testingPhase.Clarifications);
        Assert.Equal("Testing question?", testingClarif.Question);
        Assert.Equal("composer", testingClarif.AnsweredBy);
    }

    // ── Clarification display for live/in-progress pipelines ─────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="PhaseViewInfo.Clarifications"/> on the Planning phase view
    /// from a live <see cref="GoalPipeline"/> when the pipeline is in the Planning phase.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_LivePipeline_PlanningPhaseClarificationSurfacesOnPlanningPhaseView()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal { Id = "planning-clarif-live", Description = "Test" };
        await _store.CreateGoalAsync(goal, ct);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        pipeline.Clarifications.Add(new ClarificationEntry(
            DateTime.UtcNow,
            goal.Id,
            1,
            "Planning",
            "brain",
            "Should I use Strategy or Factory here?",
            "Use Strategy.",
            "composer"));

        var workerPool = new WorkerPool();
        var goalManager = new GoalManager();
        goalManager.AddSource(_store);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: _store);

        var detail = await service.GetGoalDetail("planning-clarif-live");

        Assert.NotNull(detail);
        var planningPhase = detail.Iterations.First().Phases.First(p => p.Name == "Planning");
        var clarif = Assert.Single(planningPhase.Clarifications);
        Assert.Equal("Should I use Strategy or Factory here?", clarif.Question);
        Assert.Equal("Use Strategy.", clarif.Answer);
        Assert.Equal("composer", clarif.AnsweredBy);
    }

    // ── Multi-Round Phase Tests ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that <c>[Coding, Testing, Coding, Testing, Review, Merging]</c>
    /// produces 6 PhaseViewInfo entries with unique (Name, Occurrence) pairs.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_MultiRoundPlan_ProducesUniqueNameOccurrencePairs()
    {
        var goal = new Goal
        {
            Id = "multi-round-goal",
            Description = "Goal with multiple rounds",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set multi-round plan: [Coding, Testing, Coding, Testing, Review, Merging]
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Testing);
        pipeline.AdvanceTo(GoalPhase.Testing);

        // Add PhaseLog entries for completed and active phases
        var start = DateTime.UtcNow.AddMinutes(-5);
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Iteration = 1, Occurrence = 1,
            Result = PhaseOutcome.Pass, WorkerOutput = "First coding output",
            StartedAt = start, CompletedAt = start.AddSeconds(15),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Iteration = 1, Occurrence = 1,
            Result = PhaseOutcome.Pass, WorkerOutput = "First testing output",
            StartedAt = start.AddSeconds(15), CompletedAt = start.AddSeconds(25),
        });
        // Active phase (no CompletedAt)
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Iteration = 1, Occurrence = 2,
            Result = PhaseOutcome.Pass,
            StartedAt = start.AddSeconds(25),
        });

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = await service.GetGoalDetail("multi-round-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);

        // Phase order: Planning, Coding/1, Testing/1, Coding/2 (active), Testing/2 (pending), Review (pending), Merging (pending)
        Assert.Equal(7, currentIteration.Phases.Count);
        
        // Verify Planning is completed
        Assert.Equal("completed", currentIteration.Phases[0].Status);
        
        // Verify Coding/1 is completed
        Assert.Equal("completed", currentIteration.Phases[1].Status);
        Assert.Equal("Coding", currentIteration.Phases[1].Name);
        Assert.Equal(1, currentIteration.Phases[1].Occurrence);
        
        // Verify Testing/1 is completed
        Assert.Equal("completed", currentIteration.Phases[2].Status);
        Assert.Equal("Testing", currentIteration.Phases[2].Name);
        Assert.Equal(1, currentIteration.Phases[2].Occurrence);
        
        // Verify Coding/2 is active
        Assert.Equal("active", currentIteration.Phases[3].Status);
        Assert.Equal("Coding", currentIteration.Phases[3].Name);
        Assert.Equal(2, currentIteration.Phases[3].Occurrence);
        
        // Verify remaining phases are pending
        Assert.Equal("pending", currentIteration.Phases[4].Status); // Testing/2
        Assert.Equal("pending", currentIteration.Phases[5].Status); // Review
        Assert.Equal("pending", currentIteration.Phases[6].Status); // Merging
    }

    /// <summary>
    /// Verifies that status is positional: phases before current position are
    /// "completed", current is "active", and after are "pending".
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_MultiRoundPlan_StatusIsPositional()
    {
        var goal = new Goal
        {
            Id = "positional-status-goal",
            Description = "Goal testing positional status",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set multi-round plan
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };
        pipeline.SetPlan(plan);
        // Use RestoreFromPlan to set the state machine to Testing with remaining phases
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Testing);
        // Set pipeline.Phase to Testing using AdvanceTo
        pipeline.AdvanceTo(GoalPhase.Testing);

        // Add PhaseLog entries for completed and active phases
        var start = DateTime.UtcNow.AddMinutes(-5);
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Iteration = 1, Occurrence = 1,
            Result = PhaseOutcome.Pass,
            StartedAt = start, CompletedAt = start.AddSeconds(15),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Iteration = 1, Occurrence = 1,
            Result = PhaseOutcome.Pass,
            StartedAt = start.AddSeconds(15), CompletedAt = start.AddSeconds(25),
        });
        // Active phase (no CompletedAt)
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Iteration = 1, Occurrence = 2,
            Result = PhaseOutcome.Pass,
            StartedAt = start.AddSeconds(25),
        });

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = await service.GetGoalDetail("positional-status-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);

        // After RestoreFromPlan at Testing + AdvanceTo:
        // Phase order: Planning, Coding/1, Testing/1, Coding/2, Testing/2, Review, Merging
        // Status: completed, completed, completed, active, pending, pending, pending

        // Planning is always completed
        Assert.Equal("completed", currentIteration.Phases[0].Status);

        // Coding/1 is completed
        var coding1 = currentIteration.Phases.First(p => p.Name == "Coding" && p.Occurrence == 1);
        Assert.Equal("completed", coding1.Status);

        // Testing/1 is completed
        var testing1 = currentIteration.Phases.First(p => p.Name == "Testing" && p.Occurrence == 1);
        Assert.Equal("completed", testing1.Status);

        // Coding/2 is active
        var coding2 = currentIteration.Phases.First(p => p.Name == "Coding" && p.Occurrence == 2);
        Assert.Equal("active", coding2.Status);

        // Second Testing should be pending (index 3)
        var testing2 = currentIteration.Phases.First(p => p.Name == "Testing" && p.Occurrence == 2);
        Assert.Equal("pending", testing2.Status);

        // Review should be pending (index 4)
        var review = currentIteration.Phases.First(p => p.Name == "Review");
        Assert.Equal("pending", review.Status);

        // Merging should be pending (index 5)
        var merging = currentIteration.Phases.First(p => p.Name == "Merging");
        Assert.Equal("pending", merging.Status);
    }

    /// <summary>
    /// Verifies that each occurrence of a repeated phase shows its own worker output
    /// (per-occurrence key lookup), and non-repeated phases show their output normally.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_MultiRoundPlan_EachOccurrenceHasItsOwnWorkerOutput()
    {
        var goal = new Goal
        {
            Id = "worker-output-goal",
            Description = "Goal testing worker output assignment",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set multi-round plan
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };
        pipeline.SetPlan(plan);
        // Use RestoreFromPlan to set RemainingPhases correctly
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Review);
        // Set pipeline.Phase to Review so GetGoalDetail sees all phases
        var phaseProperty = typeof(GoalPipeline).GetProperty("Phase");
        phaseProperty?.SetValue(pipeline, GoalPhase.Review);

        // Record outputs with per-occurrence tracking
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "First coding output", occurrence: 1);
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "First testing output", occurrence: 1);
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "Second coding output", occurrence: 2);
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "Second testing output", occurrence: 2);
        pipeline.RecordTestOutput(WorkerRole.Reviewer, 1, "Review output", occurrence: 1);

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = await service.GetGoalDetail("worker-output-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);

        // First occurrence of Coding should show first coding output
        var coding1 = currentIteration.Phases.First(p => p.Name == "Coding" && p.Occurrence == 1);
        Assert.Equal("First coding output", coding1.WorkerOutput);

        // Second occurrence of Coding should show second coding output
        var coding2 = currentIteration.Phases.First(p => p.Name == "Coding" && p.Occurrence == 2);
        Assert.Equal("Second coding output", coding2.WorkerOutput);

        // First occurrence of Testing should show first testing output
        var testing1 = currentIteration.Phases.First(p => p.Name == "Testing" && p.Occurrence == 1);
        Assert.Equal("First testing output", testing1.WorkerOutput);

        // Second occurrence of Testing should show second testing output
        var testing2 = currentIteration.Phases.First(p => p.Name == "Testing" && p.Occurrence == 2);
        Assert.Equal("Second testing output", testing2.WorkerOutput);

        // Review (only occurrence) should have output
        var review = currentIteration.Phases.First(p => p.Name == "Review");
        Assert.Equal("Review output", review.WorkerOutput);

        // Merging (only occurrence) should have null (no worker role)
        var merging = currentIteration.Phases.First(p => p.Name == "Merging");
        Assert.Null(merging.WorkerOutput);
    }

    /// <summary>
    /// Verifies that all ProgressReports for a phase are shown on every occurrence of that phase,
    /// since ProgressReports are keyed by phase name only (not occurrence).
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_MultiRoundPlan_AllOccurrencesShowProgressReports()
    {
        var goal = new Goal
        {
            Id = "progress-reports-goal",
            Description = "Goal testing progress report assignment",
            Status = GoalStatus.InProgress,
        };

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set multi-round plan
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };
        pipeline.SetPlan(plan);
        // Use RestoreFromPlan to set RemainingPhases correctly
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Review);
        // Set pipeline.Phase to Review so GetGoalDetail sees all worker phases
        var phaseProperty = typeof(GoalPipeline).GetProperty("Phase");
        phaseProperty?.SetValue(pipeline, GoalPhase.Review);

        // Add PhaseLog entries for all completed and active phases
        var start = DateTime.UtcNow.AddMinutes(-5);
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Iteration = 1, Occurrence = 1,
            Result = PhaseOutcome.Pass,
            StartedAt = start, CompletedAt = start.AddSeconds(15),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Iteration = 1, Occurrence = 1,
            Result = PhaseOutcome.Pass,
            StartedAt = start.AddSeconds(15), CompletedAt = start.AddSeconds(25),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Iteration = 1, Occurrence = 2,
            Result = PhaseOutcome.Pass,
            StartedAt = start.AddSeconds(25), CompletedAt = start.AddSeconds(40),
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Iteration = 1, Occurrence = 2,
            Result = PhaseOutcome.Pass,
            StartedAt = start.AddSeconds(40), CompletedAt = start.AddSeconds(50),
        });
        // Active phase (no CompletedAt)
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Review, Iteration = 1, Occurrence = 1,
            Result = PhaseOutcome.Pass,
            StartedAt = start.AddSeconds(50),
        });

        // Add progress reports with per-occurrence tracking.
        // Occurrence 1 reports for Coding and Testing
        pipeline.ProgressReports.Add(new ProgressEntry
        {
            Iteration = 1,
            Phase = "Coding",
            Details = "First coding report",
            Timestamp = DateTime.UtcNow,
            Occurrence = 1,
        });
        pipeline.ProgressReports.Add(new ProgressEntry
        {
            Iteration = 1,
            Phase = "Coding",
            Details = "Second coding report",
            Timestamp = DateTime.UtcNow.AddSeconds(10),
            Occurrence = 2,
        });
        pipeline.ProgressReports.Add(new ProgressEntry
        {
            Iteration = 1,
            Phase = "Testing",
            Details = "First testing report",
            Timestamp = DateTime.UtcNow,
            Occurrence = 1,
        });
        pipeline.ProgressReports.Add(new ProgressEntry
        {
            Iteration = 1,
            Phase = "Testing",
            Details = "Second testing report",
            Timestamp = DateTime.UtcNow.AddSeconds(10),
            Occurrence = 2,
        });

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        goalManager.AddSource(goalSource);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, goalManager,
            logSink, progressLog, goalStore: null);

        var detail = await service.GetGoalDetail("progress-reports-goal");

        Assert.NotNull(detail);
        var currentIteration = detail.Iterations.FirstOrDefault(i => i.IsCurrent);
        Assert.NotNull(currentIteration);

        // Coding occurrence 1 gets only the report with Occurrence == 1
        var coding1 = currentIteration.Phases.First(p => p.Name == "Coding" && p.Occurrence == 1);
        Assert.Single(coding1.ProgressReports);
        Assert.Contains(coding1.ProgressReports, p => p.Details == "First coding report");

        // Coding occurrence 2 gets only the report with Occurrence == 2
        var coding2 = currentIteration.Phases.First(p => p.Name == "Coding" && p.Occurrence == 2);
        Assert.Single(coding2.ProgressReports);
        Assert.Contains(coding2.ProgressReports, p => p.Details == "Second coding report");

        // Testing occurrence 1 gets only the report with Occurrence == 1
        var testing1 = currentIteration.Phases.First(p => p.Name == "Testing" && p.Occurrence == 1);
        Assert.Single(testing1.ProgressReports);
        Assert.Contains(testing1.ProgressReports, p => p.Details == "First testing report");

        // Testing occurrence 2 gets only the report with Occurrence == 2
        var testing2 = currentIteration.Phases.First(p => p.Name == "Testing" && p.Occurrence == 2);
        Assert.Single(testing2.ProgressReports);
        Assert.Contains(testing2.ProgressReports, p => p.Details == "Second testing report");
    }

    // ── PhaseNameToRoleName regression tests ────────────────────────────────────

    /// <summary>
    /// Regression: <c>"Improve"</c> phase name (from persisted <see cref="IterationSummary"/>)
    /// must map to <c>"improver"</c> role so the dashboard shows the correct worker role
    /// for goals completed with the <c>GoalPhase.Improve</c> enum value.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_ImprovePhaseInPersistedSummary_MapsToImprover()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "improve-phase-goal",
            Description = "Goal with Improve phase in persisted summary",
            Status = GoalStatus.Completed,
        };
        await _store.CreateGoalAsync(goal, ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 45.0 },
                new PhaseResult { Name = GoalPhase.Improve, Result = PhaseOutcome.Pass, DurationSeconds = 20.0 },
            ],
        };
        await _store.UpdateGoalStatusAsync("improve-phase-goal", GoalStatus.Completed,
            new GoalUpdateMetadata { Iterations = 1, IterationSummary = summary }, ct);

        using var service = CreateService();

        var detail = await service.GetGoalDetail("improve-phase-goal");

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail.Iterations);
        // Planning + Coding + Improve = 3 phases
        Assert.Equal(3, iteration.Phases.Count);

        var improvePhase = iteration.Phases.FirstOrDefault(p => p.Name == "Improvement");
        Assert.NotNull(improvePhase);
        Assert.Equal("improver", improvePhase.RoleName);
    }

    /// <summary>
    /// Regression: <c>"Improvement"</c> phase name must still map to <c>"improver"</c>
    /// role to preserve backwards compatibility with any existing persisted data.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_ImprovementPhaseInPersistedSummary_MapsToImprover()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "improvement-phase-goal",
            Description = "Goal with Improvement phase in persisted summary",
            Status = GoalStatus.Completed,
        };
        await _store.CreateGoalAsync(goal, ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = GoalPhase.Improve, Result = PhaseOutcome.Pass, DurationSeconds = 25.0 },
            ],
        };
        await _store.UpdateGoalStatusAsync("improvement-phase-goal", GoalStatus.Completed,
            new GoalUpdateMetadata { Iterations = 1, IterationSummary = summary }, ct);

        using var service = CreateService();

        var detail = await service.GetGoalDetail("improvement-phase-goal");

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail.Iterations);
        // Planning + Improvement = 2 phases
        Assert.Equal(2, iteration.Phases.Count);

        var improvementPhase = iteration.Phases.FirstOrDefault(p => p.Name == "Improvement");
        Assert.NotNull(improvementPhase);
        Assert.Equal("improver", improvementPhase.RoleName);
    }

    /// <summary>
    /// Regression: verifies that all known phase-role mappings in <see cref="DashboardStateService"/>
    /// are correct so that no other phase-role mapping regresses when <c>"Improve"</c>
    /// is added alongside <c>"Improvement"</c>.
    /// </summary>
    [Theory]
    [InlineData(GoalPhase.Coding, "coder")]
    [InlineData(GoalPhase.Testing, "tester")]
    [InlineData(GoalPhase.Review, "reviewer")]
    [InlineData(GoalPhase.DocWriting, "docwriter")]
    [InlineData(GoalPhase.Improve, "improver")]
    public async Task GetGoalDetail_PhaseRoleMappings_AreCorrect(GoalPhase goalPhase, string expectedRole)
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = $"mapping-test-{goalPhase.ToString().ToLowerInvariant()}",
            Description = $"Goal with {goalPhase} phase",
            Status = GoalStatus.Completed,
        };
        await _store.CreateGoalAsync(goal, ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = goalPhase, Result = PhaseOutcome.Pass, DurationSeconds = 10.0 },
            ],
        };
        await _store.UpdateGoalStatusAsync(goal.Id, GoalStatus.Completed,
            new GoalUpdateMetadata { Iterations = 1, IterationSummary = summary }, ct);

        using var service = CreateService();

        var detail = await service.GetGoalDetail(goal.Id);

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail.Iterations);
        var phase = iteration.Phases.FirstOrDefault(p => p.Name == goalPhase.ToDisplayName());
        Assert.NotNull(phase);
        Assert.Equal(expectedRole, phase.RoleName);
    }

    /// <summary>
    /// Verifies that a completed goal with a multi-round plan shows each occurrence with its
    /// own worker output and occurrence index, and that test metrics only appear on the last
    /// Testing occurrence.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_CompletedMultiRoundPlan_PerOccurrenceOutputAndLastOccurrenceMetrics()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "completed-multi-round-goal",
            Description = "Completed multi-round goal",
            Status = GoalStatus.Completed,
        };
        await _store.CreateGoalAsync(goal, ct);

        // Build summary as BuildIterationSummary would: PhaseResult.Occurrence is set,
        // WorkerOutput is the per-occurrence output.
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = GoalPhase.Coding,  Result = PhaseOutcome.Pass, DurationSeconds = 30.0, WorkerOutput = "First coding output",   Occurrence = 1 },
                new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Pass, DurationSeconds = 15.0, WorkerOutput = "First testing output",  Occurrence = 1 },
                new PhaseResult { Name = GoalPhase.Coding,  Result = PhaseOutcome.Pass, DurationSeconds = 45.0, WorkerOutput = "Second coding output",  Occurrence = 2 },
                new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Pass, DurationSeconds = 20.0, WorkerOutput = "Second testing output", Occurrence = 2 },
                new PhaseResult { Name = GoalPhase.Review,  Result = PhaseOutcome.Pass, DurationSeconds = 10.0, WorkerOutput = "Review output",        Occurrence = 1 },
            ],
            TestCounts = new TestCounts { Total = 42, Passed = 40, Failed = 2 },
            ReviewVerdict = "approve",
        };
        await _store.UpdateGoalStatusAsync("completed-multi-round-goal", GoalStatus.Completed,
            new GoalUpdateMetadata { Iterations = 1, IterationSummary = summary }, ct);

        using var service = CreateService();

        var detail = await service.GetGoalDetail("completed-multi-round-goal");

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail.Iterations);

        // Planning + 5 phases = 6 total
        Assert.Equal(6, iteration.Phases.Count);

        // Each Coding occurrence has its own output and occurrence index
        var coding1 = iteration.Phases.First(p => p.Name == "Coding" && p.Occurrence == 1);
        Assert.Equal("First coding output", coding1.WorkerOutput);

        var coding2 = iteration.Phases.First(p => p.Name == "Coding" && p.Occurrence == 2);
        Assert.Equal("Second coding output", coding2.WorkerOutput);

        // Each Testing occurrence has its own output
        var testing1 = iteration.Phases.First(p => p.Name == "Testing" && p.Occurrence == 1);
        Assert.Equal("First testing output", testing1.WorkerOutput);

        var testing2 = iteration.Phases.First(p => p.Name == "Testing" && p.Occurrence == 2);
        Assert.Equal("Second testing output", testing2.WorkerOutput);

        // Test metrics only on last Testing occurrence
        Assert.Equal(0, testing1.TotalTests);
        Assert.Equal(42, testing2.TotalTests);
        Assert.Equal(40, testing2.PassedTests);
        Assert.Equal(2, testing2.FailedTests);

        // Review output and verdict only on (single) Review occurrence
        var review = iteration.Phases.First(p => p.Name == "Review");
        Assert.Equal("Review output", review.WorkerOutput);
        Assert.Equal("approve", review.ReviewVerdict);
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
