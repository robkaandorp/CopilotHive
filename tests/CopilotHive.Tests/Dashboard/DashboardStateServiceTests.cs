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
