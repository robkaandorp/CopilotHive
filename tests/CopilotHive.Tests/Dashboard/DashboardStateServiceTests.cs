using System.Reflection;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using CopilotHive.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests.Dashboard;

/// <summary>
/// Tests for <see cref="DashboardStateService"/> view-model types and
/// the mapping logic that populates them from <see cref="Goal"/> instances.
/// </summary>
public sealed class DashboardStateServiceTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;

    /// <summary>Initialises an in-memory SQLite goal store for each test.</summary>
    public DashboardStateServiceTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _dbContext.Dispose();
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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

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
    /// Verifies that <see cref="GoalDetailInfo.Documents"/> is populated correctly
    /// when the underlying <see cref="Goal"/> has document IDs set.
    /// </summary>
    [Fact]
    public void GoalDetailInfo_Documents_PopulatedFromGoal()
    {
        var goal = new Goal
        {
            Id = "doc-goal",
            Description = "A goal with linked documents",
            Documents = ["doc-1", "doc-2"],
        };

        var detail = new GoalDetailInfo
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Status = goal.Status,
            Priority = goal.Priority,
            Documents = goal.Documents,
        };

        Assert.Equal(2, detail.Documents.Count);
        Assert.Contains("doc-1", detail.Documents);
        Assert.Contains("doc-2", detail.Documents);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="GoalDetailInfo.Documents"/> with the correct document IDs
    /// for a goal that was stored with linked documents.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_GoalWithDocuments_PopulatesDocuments()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange: persist a goal that has linked documents
        var documentId = "architecture-brain";
        var goal = new Goal
        {
            Id = "doc-linked-goal",
            Description = "Goal linked to a document",
            Documents = [documentId],
        };
        await _store.CreateGoalAsync(goal, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

        // Act
        var detail = await service.GetGoalDetail("doc-linked-goal");

        // Assert
        Assert.NotNull(detail);
        Assert.Single(detail.Documents);
        Assert.Contains(documentId, detail.Documents);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.Documents"/> defaults to an
    /// empty list when the goal has no documents.
    /// </summary>
    [Fact]
    public void GoalDetailInfo_Documents_EmptyWhenNoDocuments()
    {
        var goal = new Goal
        {
            Id = "no-doc-goal",
            Description = "No documents",
        };

        var detail = new GoalDetailInfo
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Status = goal.Status,
            Priority = goal.Priority,
        };

        Assert.Empty(detail.Documents);
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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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

    // ── Live running-phase usage (heartbeat-attributed) ─────────────────────────

    /// <summary>
    /// A one-entry usage summary whose numbers are all DISTINCT from the other fixture, so a value
    /// taken from the wrong source cannot coincidentally match.
    /// </summary>
    private static SharpCoder.UsageSummary HeartbeatUsage(string model, long input, long output) => new(
    [
        new SharpCoder.UsageEntry(SharpCoder.UsageSource.Agent, model, new SharpCoder.TokenUsage
        {
            InputTokens = input,
            OutputTokens = output,
            Calls = 1,
            CachedInputReportedCalls = 1,
            ReasoningReportedCalls = 1,
        }),
    ]);

    /// <summary>
    /// Builds a pipeline whose LIVE iteration has ONE completed worker entry carrying a persisted usage
    /// report and a RUNNING Coding entry (not completed, no persisted usage), and registers it with the
    /// manager, so <see cref="DashboardStateService.GetGoalDetail"/> sees a real pipeline.
    /// <para>
    /// The completed entry is part of the fixture on purpose: it keeps every total NON-NULL, so a test
    /// that expects no live usage can still show that the running entry stays counted as a worker phase
    /// WITHOUT usage (<c>PhasesWithoutUsage</c>) instead of disappearing from the totals. The premise is
    /// asserted too: a Planning pipeline would build only the synthetic Planning row, so no worker row
    /// could show live usage at all.
    /// </para>
    /// </summary>
    private static GoalPipeline RunningCodingPipeline(GoalPipelineManager pipelineManager, string goalId)
    {
        var goal = new Goal
        {
            Id = goalId,
            Description = "Goal with a running worker phase",
            Status = GoalStatus.InProgress,
        };

        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.SetPlan(new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Merging],
            Reason = "plan for the live-usage test",
        });
        pipeline.AdvanceTo(GoalPhase.Coding);

        // A COMPLETED worker entry with a persisted report, then the RUNNING Coding entry with none.
        var completedAt = DateTime.UtcNow.AddSeconds(-5);
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.DocWriting,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = completedAt.AddSeconds(-10),
            CompletedAt = completedAt,
            Usage = PersistedDocWritingUsage,
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
        });

        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
        Assert.NotEqual(GoalPhase.Planning, pipeline.Phase);
        Assert.Equal(2, pipeline.PhaseLog.Count);
        return pipeline;
    }

    /// <summary>The persisted usage of the fixture's COMPLETED worker entry, distinct from every live figure.</summary>
    private static readonly SharpCoder.UsageSummary PersistedDocWritingUsage = HeartbeatUsage("model-persisted", 700, 70);

    /// <summary>
    /// Asserts that <paramref name="actual"/> reports exactly the same figures as
    /// <paramref name="expected"/> — value equality, not reference equality: the pool hands out a
    /// DETACHED copy of a heartbeat's usage (<c>Snapshot()</c>), so the instance a consumer sees is
    /// deliberately never the one that was stored.
    /// </summary>
    private static void AssertUsageFigures(SharpCoder.UsageSummary expected, SharpCoder.UsageSummary? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(UsageFormat.FormatUsageLine(expected.Total), UsageFormat.FormatUsageLine(actual!.Total));
        Assert.Equal(UsageFormat.FormatEntryLines(expected), UsageFormat.FormatEntryLines(actual));
    }

    /// <summary>
    /// The heartbeat-attributed live usage reaches the RUNNING Coding row: the row carries that exact
    /// summary, is marked live, and the iteration and goal totals include it (so the goal card renders
    /// the "(includes running phase)" note). The heartbeat id is compared ORDINALLY against the
    /// pipeline's active task id, and the worker is a REAL <see cref="WorkerPool"/> registration whose
    /// heartbeat is delivered through <see cref="WorkerPool.UpdateHeartbeat"/>.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_HeartbeatTaggedWithTheActiveTaskId_ShowsLiveUsageOnTheRunningRow()
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = RunningCodingPipeline(pipelineManager, "live-usage-goal");

        pipeline.SetActiveTask("task-1");
        workerPool.RegisterWorker("w1", []);
        var liveUsage = HeartbeatUsage("model-live", 4_200, 1_300);
        workerPool.UpdateHeartbeat("w1", 42, "task-1", liveUsage);

        // PREMISE: the pool really attributes that usage to task-1, so the assertions below are not
        // passing merely because the heartbeat was dropped.
        var captured = Assert.Single(workerPool.CaptureWorkerStatus(), w => w.Id == "w1");
        Assert.Equal("task-1", captured.LiveUsageTaskId);
        AssertUsageFigures(liveUsage, captured.LiveUsage);
        // CaptureWorkerStatus takes a Snapshot() at capture time, so the captured summary is never
        // the caller's instance; the pool itself stores the reference it was given.
        Assert.NotSame(liveUsage, captured.LiveUsage);

        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();
        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

        var detail = await service.GetGoalDetail("live-usage-goal");

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        Assert.True(iteration.IsCurrent);

        var codingRow = Assert.Single(iteration.Phases, p => p.Name == "Coding");
        Assert.True(codingRow.IsLiveUsage);
        AssertUsageFigures(liveUsage, codingRow.Usage);

        // The COMPLETED row keeps its own persisted report and is NOT live.
        var docWritingRow = Assert.Single(iteration.Phases, p => p.Name == "Doc Writing");
        AssertUsageFigures(PersistedDocWritingUsage, docWritingRow.Usage);
        Assert.False(docWritingRow.IsLiveUsage);

        // No OTHER row received the live usage — the pending worker phases stay untouched.
        Assert.All(iteration.Phases.Where(p => p.Name is not ("Coding" or "Doc Writing")), p =>
        {
            Assert.False(p.IsLiveUsage);
            Assert.Null(p.Usage);
        });

        // The totals include the running phase's live usage and say so: the running entry counts as
        // REPORTED (with live usage), so only the pending phases are absent from these counts — they
        // were never dispatched and are not in the phase log at all.
        Assert.True(iteration.IncludesLiveUsage);
        Assert.NotNull(iteration.UsageTotals);
        Assert.Equal(2, iteration.UsageTotals!.PhasesWithUsage);
        Assert.Equal(0, iteration.UsageTotals.PhasesWithoutUsage);
        Assert.Equal(700 + 4_200, iteration.UsageTotals.Usage.Total.InputTokens);
        Assert.Equal(70 + 1_300, iteration.UsageTotals.Usage.Total.OutputTokens);

        Assert.True(detail.IncludesLiveUsage);
        Assert.NotNull(detail.UsageTotals);
        Assert.Equal(700 + 4_200, detail.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(70 + 1_300, detail.UsageTotals.Usage.Total.OutputTokens);
        Assert.Equal(2, detail.UsageTotals.PhasesWithUsage);

        // …and the entry the pipeline still owns reports NOTHING: the live figure lives only in the view.
        Assert.Null(Assert.Single(pipeline.PhaseLog, e => e.Name == GoalPhase.Coding).Usage);
    }

    /// <summary>
    /// A heartbeat tagged with a DIFFERENT task id than the pipeline's active task id contributes no
    /// live usage: the running row stays unmarked and the totals stay persisted-data-only, so a stale
    /// report from a task the worker is no longer running can never be shown as the running phase.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_HeartbeatTaggedWithADifferentTaskId_ShowsNoLiveUsage()
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = RunningCodingPipeline(pipelineManager, "stale-usage-goal");

        pipeline.SetActiveTask("task-1");
        workerPool.RegisterWorker("w1", []);
        // A real, non-empty usage — reported for a DIFFERENT task id.
        workerPool.UpdateHeartbeat("w1", 42, "task-other", HeartbeatUsage("model-stale", 999_000, 888_000));

        // PREMISE: the usage really is stored, tagged with the OTHER id — otherwise this test would pass
        // even if the heartbeat had been dropped.
        var captured = Assert.Single(workerPool.CaptureWorkerStatus(), w => w.Id == "w1");
        Assert.Equal("task-other", captured.LiveUsageTaskId);
        Assert.NotNull(captured.LiveUsage);

        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();
        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

        var detail = await service.GetGoalDetail("stale-usage-goal");

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        var codingRow = Assert.Single(iteration.Phases, p => p.Name == "Coding");
        Assert.False(codingRow.IsLiveUsage);
        Assert.Null(codingRow.Usage);
        Assert.False(iteration.IncludesLiveUsage);
        Assert.False(detail.IncludesLiveUsage);

        // The running phase still counts as a worker phase WITHOUT a report, and the stale figure is in
        // neither the totals nor the entry lines.
        Assert.NotNull(iteration.UsageTotals);
        Assert.Equal(1, iteration.UsageTotals!.PhasesWithUsage);
        Assert.Equal(1, iteration.UsageTotals.PhasesWithoutUsage);
        Assert.Equal(700, iteration.UsageTotals.Usage.Total.InputTokens);
        Assert.DoesNotContain(iteration.UsageTotals.Usage.Entries, e => e.Model == "model-stale");
        Assert.NotNull(detail.UsageTotals);
        Assert.Equal(1, detail.UsageTotals!.PhasesWithoutUsage);
        Assert.DoesNotContain(detail.UsageTotals.Usage.Entries, e => e.Model == "model-stale");
    }

    /// <summary>
    /// WITHOUT an active task id the pipeline attributes nothing, so a heartbeat that names SOME task
    /// contributes no live usage — even though the usage is genuinely stored in the pool. The pipeline
    /// pointer is the attribution key: no pointer, no live usage.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_NoActiveTaskId_ShowsNoLiveUsageEvenWhenAHeartbeatExists()
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = RunningCodingPipeline(pipelineManager, "no-active-task-goal");

        // PREMISE: no active task AND a worker whose heartbeat really does carry usage for "task-1".
        Assert.Null(pipeline.ActiveTaskId);
        workerPool.RegisterWorker("w1", []);
        workerPool.UpdateHeartbeat("w1", 42, "task-1", HeartbeatUsage("model-live", 4_200, 1_300));
        var captured = Assert.Single(workerPool.CaptureWorkerStatus(), w => w.Id == "w1");
        Assert.Equal("task-1", captured.LiveUsageTaskId);
        Assert.NotNull(captured.LiveUsage);

        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();
        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

        var detail = await service.GetGoalDetail("no-active-task-goal");

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        var codingRow = Assert.Single(iteration.Phases, p => p.Name == "Coding");
        Assert.False(codingRow.IsLiveUsage);
        Assert.Null(codingRow.Usage);
        Assert.False(iteration.IncludesLiveUsage);
        Assert.False(detail.IncludesLiveUsage);
        Assert.NotNull(iteration.UsageTotals);
        Assert.Equal(1, iteration.UsageTotals!.PhasesWithUsage);
        Assert.Equal(1, iteration.UsageTotals.PhasesWithoutUsage);
        Assert.DoesNotContain(iteration.UsageTotals.Usage.Entries, e => e.Model == "model-live");
        Assert.DoesNotContain(detail.UsageTotals!.Usage.Entries, e => e.Model == "model-live");
    }

    /// <summary>
    /// The task-id comparison is ORDINAL: a heartbeat whose id differs only by CASE from the pipeline's
    /// active task id is a DIFFERENT task, so it contributes no live usage. A case-insensitive
    /// comparison would show the usage here, making the task-id attribution weaker than the ids
    /// themselves.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_HeartbeatTaskIdDifferingOnlyByCase_ShowsNoLiveUsage()
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = RunningCodingPipeline(pipelineManager, "case-sensitive-usage-goal");

        pipeline.SetActiveTask("task-1");
        workerPool.RegisterWorker("w1", []);
        workerPool.UpdateHeartbeat("w1", 42, "TASK-1", HeartbeatUsage("model-case", 5_000, 500));

        // PREMISE: the differently-cased usage really is stored — the refusal is about the COMPARISON,
        // not about a dropped heartbeat.
        var captured = Assert.Single(workerPool.CaptureWorkerStatus(), w => w.Id == "w1");
        Assert.Equal("TASK-1", captured.LiveUsageTaskId);
        AssertUsageFigures(HeartbeatUsage("model-case", 5_000, 500), captured.LiveUsage);

        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();
        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

        var detail = await service.GetGoalDetail("case-sensitive-usage-goal");

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        var codingRow = Assert.Single(iteration.Phases, p => p.Name == "Coding");
        Assert.False(codingRow.IsLiveUsage);
        Assert.Null(codingRow.Usage);
        Assert.False(iteration.IncludesLiveUsage);
        Assert.False(detail.IncludesLiveUsage);
        Assert.DoesNotContain(iteration.UsageTotals!.Usage.Entries, e => e.Model == "model-case");
        Assert.DoesNotContain(detail.UsageTotals!.Usage.Entries, e => e.Model == "model-case");
    }

    /// <summary>
    /// TWO workers reporting usage for the same active task id make the attribution ambiguous, so NO
    /// live usage is shown: the usage is never summed and never guessed, and both figures stay out of
    /// the totals.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_TwoWorkersReportingTheSameActiveTaskId_ShowsNoLiveUsage()
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var pipeline = RunningCodingPipeline(pipelineManager, "ambiguous-usage-goal");

        pipeline.SetActiveTask("task-1");
        workerPool.RegisterWorker("w1", []);
        workerPool.RegisterWorker("w2", []);
        var first = HeartbeatUsage("model-first", 1_000, 100);
        var second = HeartbeatUsage("model-second", 2_000, 200);
        workerPool.UpdateHeartbeat("w1", 42, "task-1", first);
        workerPool.UpdateHeartbeat("w2", 43, "task-1", second);

        // PREMISE: both heartbeats really are stored for the active task id — otherwise the ambiguity
        // this test is about would not exist.
        Assert.Equal(2, workerPool.CaptureWorkerStatus().Count(w => w.LiveUsageTaskId == "task-1"));

        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();
        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

        var detail = await service.GetGoalDetail("ambiguous-usage-goal");

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        var codingRow = Assert.Single(iteration.Phases, p => p.Name == "Coding");
        Assert.False(codingRow.IsLiveUsage);
        Assert.Null(codingRow.Usage);
        Assert.False(iteration.IncludesLiveUsage);
        Assert.False(detail.IncludesLiveUsage);
        Assert.NotNull(iteration.UsageTotals);
        Assert.Equal(1, iteration.UsageTotals!.PhasesWithoutUsage);
        // Neither figure was summed into the totals.
        Assert.DoesNotContain(iteration.UsageTotals.Usage.Entries, e => e.Model is "model-first" or "model-second");
        Assert.DoesNotContain(detail.UsageTotals!.Usage.Entries, e => e.Model is "model-first" or "model-second");
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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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

    // ── OrchestratorInfo.CompactionModel ────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.CompactionModel"/> returns the value
    /// it was constructed with.
    /// </summary>
    [Fact]
    public void OrchestratorInfo_CompactionModel_Set_ReturnsValue()
    {
        var info = new OrchestratorInfo
        {
            CompactionModel = "some-model",
        };

        Assert.Equal("some-model", info.CompactionModel);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.CompactionModel"/> defaults to null
    /// when not explicitly set.
    /// </summary>
    [Fact]
    public void OrchestratorInfo_CompactionModel_DefaultIsNull()
    {
        var info = new OrchestratorInfo();

        Assert.Null(info.CompactionModel);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetOrchestratorInfo"/> populates
    /// <see cref="OrchestratorInfo.CompactionModel"/> from the config's
    /// <see cref="ModelsConfig.CompactionModel"/> when configured.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_CompactionModel_PopulatedFromConfig()
    {
        var config = new HiveConfigFile
        {
            Repositories = [],
            Models = new ModelsConfig { CompactionModel = "copilot/gpt-5.4-mini" },
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal("copilot/gpt-5.4-mini", info.CompactionModel);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.CompactionModel"/> is null when
    /// the config's <see cref="HiveConfigFile.Models"/> section is absent.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_CompactionModel_NullWhenModelsSectionAbsent()
    {
        var config = new HiveConfigFile
        {
            Repositories = [],
            // Models is null by default
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Null(info.CompactionModel);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.CompactionModel"/> is null when
    /// the config itself is null.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_CompactionModel_NullWhenConfigIsNull()
    {
        using var service = BuildService(config: null);
        var info = service.GetOrchestratorInfo();

        Assert.Null(info.CompactionModel);
    }

    /// <summary>
    /// Verifies that an EMPTY-STRING compaction model flows through the synchronized
    /// accessor into the <see cref="OrchestratorInfo.CompactionModel"/> projection
    /// verbatim (no normalization to null), matching the raw-scalar contract of
    /// <see cref="HiveConfigFile.GetCompactionModel"/>.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_CompactionModel_EmptyString_PropagatesVerbatim()
    {
        var config = new HiveConfigFile
        {
            Repositories = [],
        };
        config.SetCompactionModel(string.Empty);

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal(string.Empty, info.CompactionModel);
    }

    /// <summary>
    /// Verifies that a WHITESPACE-ONLY compaction model flows through the synchronized
    /// accessor into the <see cref="OrchestratorInfo.CompactionModel"/> projection
    /// verbatim — no trimming or validation is applied to the raw scalar.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_CompactionModel_Whitespace_PropagatesVerbatim()
    {
        var config = new HiveConfigFile
        {
            Repositories = [],
        };
        config.SetCompactionModel("   ");

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal("   ", info.CompactionModel);
    }

    /// <summary>
    /// Verifies that a synchronized <see cref="HiveConfigFile.SetCompactionModel"/> change
    /// made AFTER the service was constructed is reflected in a FRESH read of the
    /// <see cref="CopilotHive.Dashboard.DashboardStateService.GetOrchestratorInfo"/>
    /// projection: the projection reads through the live
    /// config reference at call time, not a construction-time snapshot.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_CompactionModel_SetAfterConstruction_ReflectedInFreshRead()
    {
        var config = new HiveConfigFile
        {
            Repositories = [],
            // No Models section initially — the projection reads null at construction.
        };

        using var service = BuildService(config);
        Assert.Null(service.GetOrchestratorInfo().CompactionModel);

        config.SetCompactionModel("copilot/gpt-5.4-mini-late");

        Assert.Equal("copilot/gpt-5.4-mini-late", service.GetOrchestratorInfo().CompactionModel);
    }

    // ── OrchestratorInfo reasoning effort fields ────────────────────────────

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.BrainReasoningEffort"/> is populated
    /// from <see cref="OrchestratorConfig.ReasoningEffort"/> when configured.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_BrainReasoningEffort_PopulatedFromConfig()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { ReasoningEffort = "high" },
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal(ReasoningEffort.High, info.BrainReasoningEffort);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.BrainReasoningEffort"/> is null when
    /// no reasoning effort is configured on the orchestrator.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_BrainReasoningEffort_NullWhenNotConfigured()
    {
        var config = new HiveConfigFile
        {
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Null(info.BrainReasoningEffort);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.ComposerReasoningEffort"/> uses the
    /// Composer's own reasoning effort when it is non-blank, taking precedence over
    /// the orchestrator's value.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_ComposerReasoningEffort_BlankAwareFallback()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { ReasoningEffort = "medium" },
            Composer = new ComposerConfig { ReasoningEffort = "high" },
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal(ReasoningEffort.High, info.ComposerReasoningEffort);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.ComposerReasoningEffort"/> falls back
    /// to the orchestrator's reasoning effort when the Composer's value is blank
    /// (whitespace-only).
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_ComposerReasoningEffort_FallsBackToOrchestrator()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { ReasoningEffort = "medium" },
            Composer = new ComposerConfig { ReasoningEffort = "  " },
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal(ReasoningEffort.Medium, info.ComposerReasoningEffort);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.ComposerReasoningEffort"/> is null when
    /// neither the Composer nor the Orchestrator has a reasoning effort configured.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_ComposerReasoningEffort_NullWhenNeitherConfigured()
    {
        var config = new HiveConfigFile
        {
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Null(info.ComposerReasoningEffort);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.RoleReasoningEfforts"/> is populated
    /// from each worker role's <see cref="WorkerConfig.ReasoningEffort"/>.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_RoleReasoningEfforts_PopulatedFromWorkers()
    {
        var config = new HiveConfigFile
        {
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new WorkerConfig { ReasoningEffort = "high" },
                ["reviewer"] = new WorkerConfig { ReasoningEffort = "low" },
            },
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal(ReasoningEffort.High, info.RoleReasoningEfforts["coder"]);
        Assert.Equal(ReasoningEffort.Low, info.RoleReasoningEfforts["reviewer"]);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.RoleReasoningEfforts"/>,
    /// <see cref="OrchestratorInfo.RolePremiumReasoningEfforts"/>, and
    /// <see cref="OrchestratorInfo.RolePremiumModels"/> are empty (not null) when no
    /// workers are configured.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_RoleReasoningEfforts_EmptyWhenWorkersNull()
    {
        var config = new HiveConfigFile
        {
            Repositories = [],
            Workers = null!,  // explicitly null — exercises the actual null-Workers path
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.NotNull(info.RoleReasoningEfforts);
        Assert.Empty(info.RoleReasoningEfforts);
        Assert.Empty(info.RolePremiumReasoningEfforts);
        Assert.Empty(info.RolePremiumModels);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.RoleModels"/> renders "(not configured)"
    /// for all five roles when no per-role worker configs exist (no orchestrator fallback).
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_RoleModels_NotConfiguredWhenWorkersNull()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "fallback-model" },
            Repositories = [],
            Workers = null!,  // explicitly null — exercises the actual null-Workers path
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal(5, info.RoleModels.Count);
        foreach (var role in new[] { "coder", "tester", "reviewer", "docwriter", "improver" })
        {
            Assert.Equal("(not configured)", info.RoleModels[role]);
        }
    }

    /// <summary>
    /// Removal-proof guard test: with <see cref="HiveConfigFile.Workers"/> explicitly
    /// <c>null</c>, every role model must render "(not configured)" and all
    /// role-keyed dictionaries must be empty. If the <c>_config?.Workers is null</c>
    /// guard were removed, <c>GetModelForRole</c> would throw a
    /// <see cref="NullReferenceException"/> and this test would fail.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_RoleModels_NotConfiguredWhenWorkersExplicitlyNull()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "explicit-fallback" },
            Repositories = [],
            Workers = null!,
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        foreach (var role in new[] { "coder", "tester", "reviewer", "docwriter", "improver" })
        {
            Assert.True(info.RoleModels.ContainsKey(role));
            Assert.Equal("(not configured)", info.RoleModels[role]);
        }

        Assert.Empty(info.RoleReasoningEfforts);
        Assert.Empty(info.RolePremiumReasoningEfforts);
        Assert.Empty(info.RolePremiumModels);
    }

    /// <summary>
    /// A role with a configured model renders its OWN model; a role without one renders
    /// "(not configured)" — no orchestrator/constant fallback.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_RoleModels_OwnModelOrNotConfigured()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "orchestrator-model" },
            Repositories = [],
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new WorkerConfig { Model = "coder-model" },
                ["tester"] = new WorkerConfig { Model = "   " },
            },
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal("coder-model", info.RoleModels["coder"]);
        Assert.Equal("(not configured)", info.RoleModels["tester"]);
        Assert.Equal("(not configured)", info.RoleModels["reviewer"]);
        Assert.Equal("(not configured)", info.RoleModels["docwriter"]);
        Assert.Equal("(not configured)", info.RoleModels["improver"]);
    }

    /// <summary>
    /// Verifies that an invalid reasoning effort string in the config parses leniently
    /// to null rather than throwing.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_InvalidReasoningString_ParsesToNull()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { ReasoningEffort = "INVALID_VALUE" },
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Null(info.BrainReasoningEffort);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.RolePremiumModels"/> is populated
    /// from each worker role's <see cref="WorkerConfig.PremiumModel"/>.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_RolePremiumModels_PopulatedFromWorkers()
    {
        var config = new HiveConfigFile
        {
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new WorkerConfig { PremiumModel = "premium-coder-model" },
            },
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal("premium-coder-model", info.RolePremiumModels["coder"]);
    }

    /// <summary>
    /// Verifies that <see cref="OrchestratorInfo.RolePremiumReasoningEfforts"/> is
    /// populated from each worker role's <see cref="WorkerConfig.PremiumReasoningEffort"/>.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_RolePremiumReasoningEfforts_PopulatedFromWorkers()
    {
        var config = new HiveConfigFile
        {
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["tester"] = new WorkerConfig { PremiumReasoningEffort = "extra_high" },
            },
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Equal(ReasoningEffort.ExtraHigh, info.RolePremiumReasoningEfforts["tester"]);
    }

    /// <summary>
    /// Verifies that an invalid per-role reasoning effort string parses leniently
    /// to null rather than throwing.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_InvalidRoleReasoningString_ParsesToNull()
    {
        var config = new HiveConfigFile
        {
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new WorkerConfig { ReasoningEffort = "BOGUS" },
            },
            Repositories = [],
        };

        using var service = BuildService(config);
        var info = service.GetOrchestratorInfo();

        Assert.Null(info.RoleReasoningEfforts["coder"]);
    }

    /// <summary>
    /// Helper that constructs a <see cref="DashboardStateService"/> with the given
    /// optional config and an in-memory SQLite goal store.
    /// </summary>
    private DashboardStateService BuildService(HiveConfigFile? config)
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        return new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store,
            config: config);
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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

        var detail = await service.GetGoalDetail("breaking-goal");

        Assert.NotNull(detail);
        Assert.Equal(GoalScope.Breaking, detail!.Scope);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.ReviewStatus"/> is populated from the underlying <see cref="Goal"/>.
    /// </summary>
    [Fact]
    public void GoalDetailInfo_ReviewStatus_ReflectsGoalReviewStatus()
    {
        var detail = new GoalDetailInfo
        {
            GoalId = "reviewed-goal",
            Description = "An approved goal",
            Status = GoalStatus.Pending,
            Priority = GoalPriority.Normal,
            Scope = GoalScope.Feature,
            ReviewStatus = ReviewStatus.Approved,
        };

        Assert.Equal(ReviewStatus.Approved, detail.ReviewStatus);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="GoalDetailInfo.ReviewStatus"/> from a stored goal.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_GoalWithReviewStatus_PopulatesReviewStatus()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "pending-review-goal",
            Description = "Goal pending review",
            ReviewStatus = ReviewStatus.Pending,
            Status = GoalStatus.Pending,
        };
        await _store.CreateGoalAsync(goal, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

        var detail = await service.GetGoalDetail("pending-review-goal");

        Assert.NotNull(detail);
        Assert.Equal(ReviewStatus.Pending, detail!.ReviewStatus);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDetailInfo.FailureReason"/> is populated from the underlying <see cref="Goal"/>.
    /// </summary>
    [Fact]
    public void GoalDetailInfo_FailureReason_ReflectsGoalFailureReason()
    {
        var detail = new GoalDetailInfo
        {
            GoalId = "failed-goal",
            Description = "A failed goal",
            Status = GoalStatus.Failed,
            Priority = GoalPriority.Normal,
            Scope = GoalScope.Feature,
            FailureReason = "Exceeded max iterations",
        };

        Assert.Equal("Exceeded max iterations", detail.FailureReason);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> populates
    /// <see cref="GoalDetailInfo.FailureReason"/> from a stored failed goal.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_GoalWithFailureReason_PopulatesFailureReason()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "iteration-exhausted-goal",
            Description = "Goal that exhausted iterations",
            Status = GoalStatus.Failed,
            FailureReason = "Exceeded max iterations",
        };
        await _store.CreateGoalAsync(goal, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

        var detail = await service.GetGoalDetail("iteration-exhausted-goal");

        Assert.NotNull(detail);
        Assert.Equal("Exceeded max iterations", detail!.FailureReason);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetGoalDetail"/> leaves
    /// <see cref="GoalDetailInfo.FailureReason"/> null for a non-failed goal.
    /// </summary>
    [Fact]
    public async Task GetGoalDetail_NonFailedGoal_FailureReasonIsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "completed-goal",
            Description = "Goal that completed",
            Status = GoalStatus.Completed,
        };
        await _store.CreateGoalAsync(goal, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

        var detail = await service.GetGoalDetail("completed-goal");

        Assert.NotNull(detail);
        Assert.Null(detail!.FailureReason);
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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var goalSource = new FakeGoalSourceForOutputTests(goal);
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null);

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
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();
        return new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);
    }

    // ── DashboardNotifier + cache removal tests ───────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetSnapshot"/> returns goals
    /// directly from the configured goal store when the obsolete cached-pending list is gone.
    /// </summary>
    [Fact]
    public async Task GetSnapshot_WithoutCache_ReturnsDbGoalsDirectly()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "db-direct-goal",
            Description = "Goal read directly from store",
            Status = GoalStatus.Pending,
        };
        await _store.CreateGoalAsync(goal, ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: _store);

        var snapshot = await service.GetSnapshot();

        var snapshotGoal = snapshot.Goals.Single(g => g.Id == "db-direct-goal");
        Assert.Equal("Goal read directly from store", snapshotGoal.Description);
        Assert.Equal(GoalStatus.Pending, snapshotGoal.Status);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardNotifier.NotifyStateChanged"/> relays the event
    /// through <see cref="DashboardStateService.OnStateChanged"/>.
    /// </summary>
    [Fact]
    public void DashboardNotifier_NotifyStateChanged_TriggersOnStateChanged()
    {
        var notifier = new DashboardNotifier();
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            notifier: notifier);

        var triggered = false;
        service.OnStateChanged += () => triggered = true;

        notifier.NotifyStateChanged();

        Assert.True(triggered);
    }

    /// <summary>
    /// Verifies that the timer polling period is exposed as ten seconds for tests.
    /// </summary>
    [Fact]
    public void TimerPeriod_IsTenSeconds()
    {
        using var service = CreateService();
        Assert.Equal(TimeSpan.FromSeconds(10), service.TimerPeriod);
    }

    /// <summary>
    /// Verifies that constructing the service without a notifier works and the timer-only
    /// path does not throw.
    /// </summary>
    [Fact]
    public async Task Constructor_WithoutNotifier_WorksTimerOnly()
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog);

        var snapshot = await service.GetSnapshot();
        Assert.NotNull(snapshot);
    }

    /// <summary>
    /// Verifies that disposing the service unsubscribes from the notifier so that subsequent
    /// notifications do not trigger the service's <see cref="DashboardStateService.OnStateChanged"/> event.
    /// </summary>
    [Fact]
    public void Dispose_UnsubscribesFromNotifier_NoNotificationAfterDispose()
    {
        var notifier = new DashboardNotifier();
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            notifier: notifier);

        var triggered = false;
        service.OnStateChanged += () => triggered = true;

        service.Dispose();
        notifier.NotifyStateChanged();

        Assert.False(triggered);
    }

    /// <summary>
    /// Verifies that a subscriber exception is swallowed and does not propagate to the caller,
    /// and that other subscribers still receive the notification.
    /// </summary>
    [Fact]
    public void DashboardNotifier_NotifyStateChanged_IsolatesSubscriberExceptions()
    {
        var notifier = new DashboardNotifier();
        var firstFired = false;
        var secondFired = false;

        notifier.OnStateChanged += () => { firstFired = true; throw new InvalidOperationException("boom"); };
        notifier.OnStateChanged += () => secondFired = true;

        notifier.NotifyStateChanged();

        Assert.True(firstFired);
        Assert.True(secondFired);
    }

    /// <summary>
    /// Genuinely races <see cref="DashboardNotifier.NotifyStateChanged"/> against concurrent
    /// unsubscribe/resubscribe using a <see cref="Barrier"/>. Asserts no exception escapes and
    /// that at least one captured handler still ran while it was logically unsubscribed,
    /// proving the <c>GetInvocationList()</c> snapshot semantics.
    /// </summary>
    [Fact]
    public void DashboardNotifier_NotifyStateChanged_ConcurrentUnsubscribe_SnapshotSemanticsHold()
    {
        Assert.True(DashboardNotifierRaceHelper.RaceUnsubscribe());
    }

    // ── Knowledge Graph ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetKnowledgeDocumentCount"/>
    /// returns the correct number of documents when a KnowledgeGraph is provided.
    /// </summary>
    [Fact]
    public async Task GetKnowledgeDocumentCount_WithKnowledgeGraph_ReturnsDocumentCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var knowledgeGraph = new KnowledgeGraph();
        await knowledgeGraph.CreateDocumentAsync("doc-a", "Document A", DocumentType.Feature, "content", ct: ct);
        await knowledgeGraph.CreateDocumentAsync("doc-b", "Document B", DocumentType.Idea, "content", ct: ct);
        await knowledgeGraph.CreateDocumentAsync("doc-c", "Document C", DocumentType.Memory, "content", ct: ct);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null,
            knowledgeGraph: knowledgeGraph);

        Assert.Equal(3, service.GetKnowledgeDocumentCount());
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetKnowledgeDocumentCount"/>
    /// returns 0 when no KnowledgeGraph is provided.
    /// </summary>
    [Fact]
    public void GetKnowledgeDocumentCount_WithoutKnowledgeGraph_ReturnsZero()
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null,
            knowledgeGraph: null);

        Assert.Equal(0, service.GetKnowledgeDocumentCount());
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.KnowledgeGraph"/> returns
    /// the instance when one is provided.
    /// </summary>
    [Fact]
    public void KnowledgeGraph_WhenProvided_ReturnsInstance()
    {
        var knowledgeGraph = new KnowledgeGraph();

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null,
            knowledgeGraph: knowledgeGraph);

        Assert.NotNull(service.KnowledgeGraph);
        Assert.Same(knowledgeGraph, service.KnowledgeGraph);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.KnowledgeGraph"/> returns
    /// null when no KnowledgeGraph is provided.
    /// </summary>
    [Fact]
    public void KnowledgeGraph_WhenNotProvided_ReturnsNull()
    {
        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null,
            knowledgeGraph: null);

        Assert.Null(service.KnowledgeGraph);
    }

    /// <summary>
    /// Verifies that <see cref="DashboardStateService.GetKnowledgeDocumentCount"/>
    /// returns 0 for an empty KnowledgeGraph.
    /// </summary>
    [Fact]
    public void GetKnowledgeDocumentCount_EmptyKnowledgeGraph_ReturnsZero()
    {
        var knowledgeGraph = new KnowledgeGraph();

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            logSink,
            progressLog,
            goalStore: null,
            knowledgeGraph: knowledgeGraph);

        Assert.Equal(0, service.GetKnowledgeDocumentCount());
    }

    // ── Brain/Composer model sentinel rendering (Slice 3a) ───────────────────

    /// <summary>
    /// With no Brain and no Composer, <see cref="OrchestratorInfo.BrainModel"/> and
    /// <see cref="OrchestratorInfo.ComposerModel"/> render the display-only
    /// "(not configured)" sentinel.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_NoBrainNoComposer_RendersNotConfigured()
    {
        using var service = BuildService(config: null);
        var info = service.GetOrchestratorInfo();

        Assert.Equal("(not configured)", info.BrainModel);
        Assert.Equal("(not configured)", info.ComposerModel);
    }

    /// <summary>
    /// A Brain whose stats carry a null/whitespace model renders "(not configured)".
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_BrainWhitespaceModel_RendersNotConfigured()
    {
        var brain = new SentinelBrain(new BrainStats { Model = "   " });

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, logSink, progressLog,
            goalStore: _store, brain: brain);

        var info = service.GetOrchestratorInfo();
        Assert.Equal("(not configured)", info.BrainModel);
    }

    /// <summary>
    /// A Brain with a configured model renders that model verbatim.
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_BrainConfiguredModel_RendersModel()
    {
        var brain = new SentinelBrain(new BrainStats { Model = "copilot/brain-model" });

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, logSink, progressLog,
            goalStore: _store, brain: brain);

        var info = service.GetOrchestratorInfo();
        Assert.Equal("copilot/brain-model", info.BrainModel);
    }

    /// <summary>
    /// A disconnected Composer shell (no model) renders "(not configured)".
    /// </summary>
    [Fact]
    public void GetOrchestratorInfo_ComposerDisconnectedShell_RendersNotConfigured()
    {
        var composer = new Composer(
            null,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            chatClientFactory: _ => new Moq.Mock<IChatClient>().Object);

        var workerPool = new WorkerPool();
        var pipelineManager = new GoalPipelineManager();
        var logSink = new DashboardLogSink();
        var progressLog = new ProgressLog();

        using var service = new DashboardStateService(
            workerPool, pipelineManager, logSink, progressLog,
            goalStore: _store, composer: composer);

        var info = service.GetOrchestratorInfo();
        Assert.Equal("(not configured)", info.ComposerModel);
    }

    // ── GetSnapshot goal merge — side-effect-free display rows ────────────────
    //
    // The merge derives a pipeline-backed row's Status from the pipeline phase. These theories pin
    // that the derivation lands on a SEPARATE display instance: the store-returned goal and the live
    // GoalPipeline.Goal are both inputs this merge only READS. Each theory asserts the premises that
    // make it non-vacuous (the store really hits/misses with the exact seeded instance, the pipeline
    // really holds its own goal, the seeded statuses really disagree) and then asserts on the INPUT
    // INSTANCES those premises handed to the merge — never on a fresh read substituted for them.

    /// <summary>The pipeline-backed goal id used by the store-MISS theory (no store row exists).</summary>
    private const string MergeMissGoalId = "merge-miss-pipeline-goal";

    /// <summary>The pipeline-backed goal id used by the store-HIT theory (a store row exists).</summary>
    private const string MergeHitGoalId = "merge-hit-store-goal";

    /// <summary>A store-only control goal id whose Pending status is unaffected by the merge.</summary>
    private const string MergeControlPendingId = "merge-control-pending";

    /// <summary>
    /// A store-only control goal id whose Cancelled status is counted by NO snapshot counter — so its
    /// row proves the merge leaves pipeline-less rows alone rather than inflating unrelated totals.
    /// </summary>
    private const string MergeControlCancelledId = "merge-control-cancelled";

    /// <summary>The worker registered in both merge theories, so the worker projection is pinned too.</summary>
    private const string MergeWorkerId = "worker-merge";

    /// <summary>
    /// Fixed UTC creation timestamps for the merge fixtures. <see cref="Goal.CreatedAt"/> otherwise
    /// defaults to <c>DateTime.UtcNow</c>, which would make the exact timestamp parity assertion
    /// non-deterministic AND would let a store-hit row sourced from the LIVE goal pass when the two
    /// happened to be created in the same tick. Every value here is distinct, including the two
    /// same-ID store-hit goals.
    /// </summary>
    private static readonly DateTime MergeMissLiveCreatedAt =
        new(2021, 4, 5, 6, 7, 8, DateTimeKind.Utc);
    private static readonly DateTime MergeHitStoredCreatedAt =
        new(2022, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private static readonly DateTime MergeHitLiveCreatedAt =
        new(2023, 6, 7, 8, 9, 10, DateTimeKind.Utc);
    private static readonly DateTime MergeControlPendingCreatedAt =
        new(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static readonly DateTime MergeControlCancelledCreatedAt =
        new(2019, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    /// <summary>
    /// One iteration summary with distinguishable content, used to tell a stored summary list from a
    /// live one by BOTH its count and its notes.
    /// </summary>
    private static IterationSummary MergeIterationSummary(int iteration, string note) => new()
    {
        Iteration = iteration,
        Phases =
        [
            new PhaseResult
            {
                Name = GoalPhase.Coding,
                Result = PhaseOutcome.Pass,
                WorkerOutput = note,
            },
        ],
        TestCounts = new TestCounts { Total = 10 + iteration, Passed = 10 + iteration - 1, Failed = 1 },
        BuildSuccess = true,
        Notes = [note],
    };

    /// <summary>
    /// Asserts that a pipeline-backed DISPLAY row carries EVERY non-status <see cref="Goal"/> field from
    /// its source, by VALUE equality for scalars and by REFERENCE for the collection-valued fields.
    /// The production helper copies each field explicitly, so a field omitted by a future merge shape
    /// is caught here even when the literal precedence assertions below do not read it. Status is
    /// deliberately excluded: it is the one derived field asserted separately.
    /// </summary>
    private static void AssertAllNonStatusFieldsCopied(Goal source, Goal row)
    {
        Assert.Equal(source.Id, row.Id);
        Assert.Equal(source.Description, row.Description);
        Assert.Equal(source.Priority, row.Priority);
        Assert.Equal(source.Scope, row.Scope);
        Assert.Same(source.RepositoryNames, row.RepositoryNames);
        Assert.Equal(source.TargetRepositoryNames, row.TargetRepositoryNames);
        Assert.Same(source.DependsOn, row.DependsOn);
        Assert.Same(source.Metadata, row.Metadata);
        Assert.Equal(source.CreatedAt, row.CreatedAt);
        Assert.Equal(source.StartedAt, row.StartedAt);
        Assert.Equal(source.CompletedAt, row.CompletedAt);
        Assert.Equal(source.Iterations, row.Iterations);
        Assert.Equal(source.FailureReason, row.FailureReason);
        Assert.Same(source.Notes, row.Notes);
        Assert.Same(source.PhaseDurations, row.PhaseDurations);
        Assert.Same(source.IterationSummaries, row.IterationSummaries);
        Assert.Equal(source.TotalDurationSeconds, row.TotalDurationSeconds);
        Assert.Equal(source.MergeCommitHash, row.MergeCommitHash);
        Assert.Equal(source.ReleaseId, row.ReleaseId);
        Assert.Same(source.Documents, row.Documents);
        Assert.Equal(source.BranchCleanedUp, row.BranchCleanedUp);
        Assert.Equal(source.ReviewStatus, row.ReviewStatus);
        Assert.Same(source.BrainUsage, row.BrainUsage);
        Assert.Same(source.ReviewUsage, row.ReviewUsage);
    }

    /// <summary>
    /// Builds the CONFIGURED <see cref="InMemoryGoalStore"/> shared by both merge theories: the two
    /// store-only controls, added in this order so the snapshot's row order is observable, and — for
    /// the hit theory — the pipeline-backed store row first.
    /// </summary>
    private static (InMemoryGoalStore Store, Goal Pending, Goal Cancelled) MergeStore(
        Goal? pipelineBackedStoreRow = null)
    {
        var store = new InMemoryGoalStore();
        if (pipelineBackedStoreRow is not null)
            store.AddGoal(pipelineBackedStoreRow);

        var pending = new Goal
        {
            Id = MergeControlPendingId,
            Description = "Store-only control, Pending and pipeline-less",
            Status = GoalStatus.Pending,
            RepositoryNames = ["control-repo"],
            Metadata = new Dictionary<string, string> { ["source"] = "control" },
            CreatedAt = MergeControlPendingCreatedAt,
        };
        var cancelled = new Goal
        {
            Id = MergeControlCancelledId,
            Description = "Store-only control, Cancelled and pipeline-less",
            Status = GoalStatus.Cancelled,
            CreatedAt = MergeControlCancelledCreatedAt,
        };

        store.AddGoal(pending);
        store.AddGoal(cancelled);
        return (store, pending, cancelled);
    }

    /// <summary>
    /// Builds the service plus the real pipeline manager used by both merge theories: one busy worker
    /// (so the worker projection is pinned) and a real pipeline for <paramref name="liveGoal"/> advanced
    /// to <paramref name="phase"/>.
    /// </summary>
    private static (DashboardStateService Service, GoalPipelineManager PipelineManager, GoalPipeline Pipeline)
        MergeService(IGoalStore store, Goal liveGoal, GoalPhase phase)
    {
        var workerPool = new WorkerPool();
        workerPool.RegisterWorker(MergeWorkerId, []);
        workerPool.MarkBusy(MergeWorkerId, "task-merge");

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(liveGoal);
        pipeline.AdvanceTo(phase);

        var service = new DashboardStateService(
            workerPool,
            pipelineManager,
            new DashboardLogSink(),
            new ProgressLog(),
            goalStore: store);

        return (service, pipelineManager, pipeline);
    }

    /// <summary>
    /// STORE-MISS BRANCH: the store is configured but holds NO row for the pipeline's goal, so the
    /// display row's non-status fields must come from the LIVE pipeline goal — while the phase-derived
    /// status is written onto the separate display instance ONLY. The live goal keeps its own status
    /// and every rich field, and the store-only control rows keep their own statuses, order and
    /// counts.
    /// </summary>
    /// <remarks>
    /// The seeded live status deliberately DISAGREES with the expected display status, so a merge that
    /// mutated the live goal (the pre-change behaviour) would be caught by the
    /// <c>liveGoal.Status</c> assertion rather than coincidentally agreeing with it.
    /// </remarks>
    [Theory]
    [InlineData(GoalPhase.Planning, GoalStatus.InProgress, GoalStatus.Failed)]
    [InlineData(GoalPhase.Done, GoalStatus.Completed, GoalStatus.Pending)]
    [InlineData(GoalPhase.Failed, GoalStatus.Failed, GoalStatus.Completed)]
    public async Task GetSnapshot_StoreMiss_LeavesLiveGoalUnchanged(
        GoalPhase phase, GoalStatus expectedDisplayStatus, GoalStatus liveGoalStatus)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (store, controlPending, controlCancelled) = MergeStore();

        var liveGoal = new Goal
        {
            Id = MergeMissGoalId,
            Description = "LIVE description (the store holds no row for this goal)",
            Status = liveGoalStatus,
            Priority = GoalPriority.High,
            Scope = GoalScope.Feature,
            RepositoryNames = ["live-repo"],
            Metadata = new Dictionary<string, string> { ["source"] = "live" },
            Iterations = 2,
            FailureReason = null,
            Notes = ["live note"],
            IterationSummaries = [MergeIterationSummary(1, "live iteration one")],
            BrainUsage = HeartbeatUsage("model-live-brain", 4_200, 1_300),
            ReviewUsage = HeartbeatUsage("model-live-review", 44, 4),
            TotalDurationSeconds = 321.5,
            MergeCommitHash = "live-merge-sha",
            TargetRepositoryNames = "live-repo",
            DependsOn = ["live-dependency"],
            Documents = ["live-doc"],
            PhaseDurations = new Dictionary<string, double> { ["Coding"] = 7.5 },
            ReleaseId = "live-release",
            BranchCleanedUp = true,
            ReviewStatus = ReviewStatus.Approved,
            CreatedAt = MergeMissLiveCreatedAt,
            StartedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            CompletedAt = new DateTime(2025, 1, 3, 3, 4, 5, DateTimeKind.Utc),
        };

        var merge = MergeService(store, liveGoal, phase);
        using var service = merge.Service;
        var pipelineManager = merge.PipelineManager;
        var pipeline = merge.Pipeline;

        // PREMISES — the miss is real and the pipeline really holds the exact live instance.
        Assert.Null(await store.GetGoalAsync(MergeMissGoalId, ct));
        Assert.DoesNotContain(await store.GetAllGoalsAsync(ct), g => g.Id == MergeMissGoalId);
        Assert.Same(liveGoal, pipeline.Goal);
        Assert.Same(liveGoal, pipelineManager.GetByGoalId(MergeMissGoalId)!.Goal);
        Assert.Equal(phase, pipeline.Phase);
        // …and the seeded live status really disagrees with the status the phase derives.
        Assert.NotEqual(expectedDisplayStatus, liveGoalStatus);
        // …and CreatedAt is a FIXED UTC value, not the wall-clock default, so timestamp parity is
        // deterministic and a display row that fell back to DateTime.UtcNow cannot pass by accident.
        Assert.Equal(MergeMissLiveCreatedAt, liveGoal.CreatedAt);

        var snapshot = await service.GetSnapshot();

        // THE SOURCE-MUTATION REGRESSION ASSERTIONS COME FIRST: they read the ORIGINAL live Goal instance
        // and the pipeline's own Goal reference, so restoring the old write-through merge fails HERE —
        // on Status — rather than being masked by a later display-identity assertion. Every other
        // assertion below is retained unchanged and still runs on the fixed implementation.
        Assert.Equal(liveGoalStatus, liveGoal.Status);
        Assert.Equal(liveGoalStatus, pipeline.Goal.Status);
        Assert.Same(liveGoal, pipeline.Goal);

        // ONE display row per ID, on a SEPARATE instance carrying the phase-derived status. The ID
        // inventory comes FIRST: Assert.Single(predicate) would throw on a duplicate before any
        // downstream distinct/Count assertion could report it.
        Assert.Equal(3, snapshot.Goals.Count);
        Assert.Equal(3, snapshot.Goals.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(snapshot.Goals, g => g.Id == MergeMissGoalId);
        Assert.Contains(snapshot.Goals, g => g.Id == MergeControlPendingId);
        Assert.Contains(snapshot.Goals, g => g.Id == MergeControlCancelledId);
        var row = Assert.Single(snapshot.Goals, g => g.Id == MergeMissGoalId);
        Assert.Equal(expectedDisplayStatus, row.Status);
        Assert.NotSame(liveGoal, row);
        AssertAllNonStatusFieldsCopied(liveGoal, row);

        // NEITHER input changed: the live goal keeps its status (asserted above) and its rich fields.
        Assert.Equal("LIVE description (the store holds no row for this goal)", liveGoal.Description);
        Assert.Equal("live-repo", Assert.Single(liveGoal.RepositoryNames));
        Assert.Equal("live", liveGoal.Metadata["source"]);
        Assert.Single(liveGoal.IterationSummaries);
        Assert.Equal("live-merge-sha", liveGoal.MergeCommitHash);

        // NON-STATUS FIELDS come from the LIVE goal, because the store had no match: values AND the
        // rich collections (summaries, metadata, repositories, usage) are the live ones.
        Assert.Equal("LIVE description (the store holds no row for this goal)", row.Description);
        Assert.Equal(GoalPriority.High, row.Priority);
        Assert.Equal(GoalScope.Feature, row.Scope);
        Assert.Same(liveGoal.RepositoryNames, row.RepositoryNames);
        Assert.Equal("live-repo", Assert.Single(row.RepositoryNames));
        Assert.Same(liveGoal.Metadata, row.Metadata);
        Assert.Equal("live", row.Metadata["source"]);
        Assert.Same(liveGoal.IterationSummaries, row.IterationSummaries);
        var summary = Assert.Single(row.IterationSummaries);
        Assert.Equal("live iteration one", Assert.Single(summary.Notes));
        Assert.Same(liveGoal.Notes, row.Notes);
        Assert.Equal("live note", Assert.Single(row.Notes));
        Assert.Equal(2, row.Iterations);
        Assert.Equal(321.5, row.TotalDurationSeconds);
        Assert.Equal("live-merge-sha", row.MergeCommitHash);
        Assert.Same(liveGoal.BrainUsage, row.BrainUsage);
        AssertUsageFigures(HeartbeatUsage("model-live-brain", 4_200, 1_300), row.BrainUsage);
        Assert.Equal("live-release", row.ReleaseId);
        Assert.Equal(ReviewStatus.Approved, row.ReviewStatus);
        Assert.Equal(["live-doc"], row.Documents);
        Assert.Equal(["live-dependency"], row.DependsOn);
        Assert.Equal("live-repo", row.TargetRepositoryNames);
        Assert.Equal(7.5, row.PhaseDurations!["Coding"]);
        Assert.Equal(MergeMissLiveCreatedAt, row.CreatedAt);

        // ORDER: the store's rows in store order, then the pipeline-only goal — exactly the merge's
        // insertion order. The store-only controls are published as-is: no pipeline derives their status.
        Assert.Equal(
            [MergeControlPendingId, MergeControlCancelledId, MergeMissGoalId],
            snapshot.Goals.Select(g => g.Id));
        var pendingRow = Assert.Single(snapshot.Goals, g => g.Id == MergeControlPendingId);
        var cancelledRow = Assert.Single(snapshot.Goals, g => g.Id == MergeControlCancelledId);
        Assert.Same(controlPending, pendingRow);
        Assert.Same(controlCancelled, cancelledRow);
        Assert.Equal(GoalStatus.Pending, pendingRow.Status);
        Assert.Equal(GoalStatus.Cancelled, cancelledRow.Status);

        // The DISPLAYED status drives the exact counts; the controls contribute only their own.
        Assert.Equal(1, snapshot.PendingGoals);
        Assert.Equal(0, snapshot.DraftGoals);
        Assert.Equal(expectedDisplayStatus == GoalStatus.InProgress ? 1 : 0, snapshot.ActiveGoals);
        Assert.Equal(expectedDisplayStatus == GoalStatus.Completed ? 1 : 0, snapshot.CompletedGoals);
        Assert.Equal(expectedDisplayStatus == GoalStatus.Failed ? 1 : 0, snapshot.FailedGoals);

        // The worker and pipeline projections are unchanged.
        var worker = Assert.Single(snapshot.Workers);
        Assert.Equal(MergeWorkerId, worker.Id);
        Assert.True(worker.IsBusy);
        Assert.Equal("task-merge", worker.CurrentTaskId);
        var pipelineInfo = Assert.Single(snapshot.Pipelines);
        Assert.Equal(MergeMissGoalId, pipelineInfo.GoalId);
        Assert.Equal(phase.ToString(), pipelineInfo.Phase);
        // The pipeline projection keeps reading the LIVE goal's own description (captured by the
        // pipeline constructor), not the display row — the merge must not rewrite it either.
        Assert.Equal(liveGoal.Description, pipelineInfo.Description);
        Assert.Equal("LIVE description (the store holds no row for this goal)", pipeline.Description);
    }

    /// <summary>
    /// STORE-HIT BRANCH: the store and a live pipeline are BOTH present for the same goal id, with
    /// deliberately CONFLICTING non-status fields. The display row must carry the STORE row's fields
    /// and the phase-derived status, while neither the store-returned goal nor the live pipeline goal
    /// changes — the store row keeps the lifecycle status that
    /// <see cref="DashboardStateService.GetGoalDetail"/> reads as its authoritative resume input.
    /// </summary>
    /// <remarks>
    /// Both seeded statuses deliberately DISAGREE with the expected display status, and the stored and
    /// live rich fields disagree with each other on every field the assertions read, so neither
    /// precedence nor non-mutation can pass by coincidence.
    /// </remarks>
    [Theory]
    [InlineData(GoalPhase.Planning, GoalStatus.InProgress, GoalStatus.Completed, GoalStatus.Failed)]
    [InlineData(GoalPhase.Done, GoalStatus.Completed, GoalStatus.InProgress, GoalStatus.Pending)]
    [InlineData(GoalPhase.Failed, GoalStatus.Failed, GoalStatus.Draft, GoalStatus.Completed)]
    public async Task GetSnapshot_StoreHit_PreservesStoredFieldsWithoutMutatingEitherGoal(
        GoalPhase phase, GoalStatus expectedDisplayStatus, GoalStatus storedStatus, GoalStatus liveStatus)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var storedGoal = new Goal
        {
            Id = MergeHitGoalId,
            Description = "STORED description",
            Status = storedStatus,
            Priority = GoalPriority.High,
            Scope = GoalScope.Feature,
            RepositoryNames = ["stored-repo"],
            Metadata = new Dictionary<string, string> { ["source"] = "store" },
            Iterations = 3,
            FailureReason = "stored failure reason",
            Notes = ["stored note"],
            IterationSummaries =
            [
                MergeIterationSummary(1, "stored iteration one"),
                MergeIterationSummary(2, "stored iteration two"),
            ],
            BrainUsage = HeartbeatUsage("model-stored-brain", 111, 11),
            ReviewUsage = HeartbeatUsage("model-stored-review", 222, 22),
            TotalDurationSeconds = 1_234.5,
            MergeCommitHash = "stored-merge-sha",
            TargetRepositoryNames = "stored-repo",
            DependsOn = ["stored-dependency"],
            Documents = ["stored-doc"],
            PhaseDurations = new Dictionary<string, double> { ["Testing"] = 99.5 },
            ReleaseId = "stored-release",
            BranchCleanedUp = false,
            ReviewStatus = ReviewStatus.NeedsChanges,
            CreatedAt = MergeHitStoredCreatedAt,
            StartedAt = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            CompletedAt = new DateTime(2024, 2, 4, 4, 5, 6, DateTimeKind.Utc),
        };

        var (store, controlPending, controlCancelled) = MergeStore(storedGoal);

        var liveGoal = new Goal
        {
            Id = MergeHitGoalId,
            Description = "LIVE description",
            Status = liveStatus,
            Priority = GoalPriority.Low,
            Scope = GoalScope.Patch,
            RepositoryNames = ["live-repo"],
            Metadata = new Dictionary<string, string> { ["source"] = "live" },
            Iterations = 1,
            FailureReason = "live failure reason",
            Notes = ["live note"],
            IterationSummaries = [MergeIterationSummary(1, "live iteration one")],
            BrainUsage = HeartbeatUsage("model-live-brain", 999, 99),
            ReviewUsage = HeartbeatUsage("model-live-review", 888, 88),
            TotalDurationSeconds = 42.0,
            MergeCommitHash = "live-merge-sha",
            TargetRepositoryNames = "live-repo",
            DependsOn = ["live-dependency"],
            Documents = ["live-doc"],
            PhaseDurations = new Dictionary<string, double> { ["Coding"] = 3.25 },
            ReleaseId = "live-release",
            BranchCleanedUp = true,
            ReviewStatus = ReviewStatus.Approved,
            CreatedAt = MergeHitLiveCreatedAt,
            StartedAt = new DateTime(2023, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            CompletedAt = new DateTime(2023, 3, 5, 5, 6, 7, DateTimeKind.Utc),
        };

        var merge = MergeService(store, liveGoal, phase);
        using var service = merge.Service;
        var pipelineManager = merge.PipelineManager;
        var pipeline = merge.Pipeline;

        // PREMISES — the hit returns the EXACT seeded instance and the pipeline holds its own goal.
        Assert.Same(storedGoal, await store.GetGoalAsync(MergeHitGoalId, ct));
        Assert.Same(liveGoal, pipeline.Goal);
        Assert.Same(liveGoal, pipelineManager.GetByGoalId(MergeHitGoalId)!.Goal);
        Assert.Equal(phase, pipeline.Phase);
        // …the two sources really conflict, and both really disagree with the derived display status.
        Assert.NotEqual(expectedDisplayStatus, storedStatus);
        Assert.NotEqual(expectedDisplayStatus, liveStatus);
        Assert.NotEqual(storedGoal.Description, liveGoal.Description);
        Assert.NotEqual(storedGoal.IterationSummaries.Count, liveGoal.IterationSummaries.Count);
        // …and the two same-ID goals carry DISTINCT, FIXED UTC creation timestamps, so the display
        // row's timestamp parity identifies which source it came from rather than a shared clock tick.
        Assert.Equal(MergeHitStoredCreatedAt, storedGoal.CreatedAt);
        Assert.Equal(MergeHitLiveCreatedAt, liveGoal.CreatedAt);
        Assert.NotEqual(storedGoal.CreatedAt, liveGoal.CreatedAt);

        var snapshot = await service.GetSnapshot();

        // THE SOURCE-MUTATION REGRESSION ASSERTIONS COME FIRST: they read the EXACT store-returned Goal
        // and the EXACT live Goal instance, so restoring the old write-through merge fails HERE — on
        // the stored lifecycle Status — rather than being masked by a later display assertion. Every
        // assertion below is retained unchanged and still runs on the fixed implementation.
        Assert.Equal(storedStatus, storedGoal.Status);
        Assert.Equal(liveStatus, liveGoal.Status);
        Assert.Equal(liveStatus, pipeline.Goal.Status);
        Assert.Same(liveGoal, pipeline.Goal);

        // ONE display row per ID, on a SEPARATE instance carrying the phase-derived status. A same-ID
        // store row and live pipeline goal must NOT produce two rows: the ID inventory is asserted
        // BEFORE Assert.Single(predicate), which would otherwise throw on the duplicate itself.
        Assert.Equal(3, snapshot.Goals.Count);
        Assert.Equal(3, snapshot.Goals.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(snapshot.Goals, g => g.Id == MergeHitGoalId);
        Assert.Contains(snapshot.Goals, g => g.Id == MergeControlPendingId);
        Assert.Contains(snapshot.Goals, g => g.Id == MergeControlCancelledId);
        var row = Assert.Single(snapshot.Goals, g => g.Id == MergeHitGoalId);
        Assert.Equal(expectedDisplayStatus, row.Status);
        Assert.NotSame(liveGoal, row);
        Assert.NotSame(storedGoal, row);
        AssertAllNonStatusFieldsCopied(storedGoal, row);
        // The complete-field parity above must be read against the LIVE goal too: on a hit, the live
        // goal's conflicting values must NOT be the row's values.
        Assert.NotEqual(liveGoal.Description, row.Description);
        Assert.NotSame(liveGoal.RepositoryNames, row.RepositoryNames);
        Assert.NotSame(liveGoal.Metadata, row.Metadata);
        Assert.NotSame(liveGoal.IterationSummaries, row.IterationSummaries);
        Assert.NotSame(liveGoal.Notes, row.Notes);
        Assert.NotSame(liveGoal.BrainUsage, row.BrainUsage);

        // NEITHER input changed: the store row keeps its lifecycle status (asserted above) and every
        // rich field…
        Assert.Equal("STORED description", storedGoal.Description);
        Assert.Equal(GoalPriority.High, storedGoal.Priority);
        Assert.Equal("stored-repo", Assert.Single(storedGoal.RepositoryNames));
        Assert.Equal("store", storedGoal.Metadata["source"]);
        Assert.Equal(2, storedGoal.IterationSummaries.Count);
        Assert.Equal("stored-merge-sha", storedGoal.MergeCommitHash);
        // …and so does the live pipeline goal (its Status was asserted above).
        Assert.Equal("LIVE description", liveGoal.Description);
        Assert.Equal(GoalPriority.Low, liveGoal.Priority);
        Assert.Equal("live-repo", Assert.Single(liveGoal.RepositoryNames));
        Assert.Equal("live", liveGoal.Metadata["source"]);
        Assert.Single(liveGoal.IterationSummaries);
        Assert.Equal("live-merge-sha", liveGoal.MergeCommitHash);

        // STORED ROW PRECEDENCE: every non-status field on the display row comes from the STORE row, not
        // from the live goal that shares its id.
        Assert.Equal("STORED description", row.Description);
        Assert.Equal(GoalPriority.High, row.Priority);
        Assert.Equal(GoalScope.Feature, row.Scope);
        Assert.Same(storedGoal.RepositoryNames, row.RepositoryNames);
        Assert.Equal("stored-repo", Assert.Single(row.RepositoryNames));
        Assert.Same(storedGoal.Metadata, row.Metadata);
        Assert.Equal("store", row.Metadata["source"]);
        Assert.Same(storedGoal.IterationSummaries, row.IterationSummaries);
        Assert.Equal(2, row.IterationSummaries.Count);
        Assert.Equal(
            ["stored iteration one", "stored iteration two"],
            row.IterationSummaries.Select(s => Assert.Single(s.Notes)));
        Assert.Same(storedGoal.Notes, row.Notes);
        Assert.Equal("stored note", Assert.Single(row.Notes));
        Assert.Equal(3, row.Iterations);
        Assert.Equal("stored failure reason", row.FailureReason);
        Assert.Equal(1_234.5, row.TotalDurationSeconds);
        Assert.Equal("stored-merge-sha", row.MergeCommitHash);
        Assert.Same(storedGoal.BrainUsage, row.BrainUsage);
        AssertUsageFigures(HeartbeatUsage("model-stored-brain", 111, 11), row.BrainUsage);
        Assert.Equal("stored-release", row.ReleaseId);
        Assert.Equal(ReviewStatus.NeedsChanges, row.ReviewStatus);
        Assert.Equal(["stored-doc"], row.Documents);
        Assert.Equal(["stored-dependency"], row.DependsOn);
        Assert.Equal("stored-repo", row.TargetRepositoryNames);
        Assert.Equal(99.5, row.PhaseDurations!["Testing"]);
        Assert.Equal(MergeHitStoredCreatedAt, row.CreatedAt);
        Assert.NotEqual(MergeHitLiveCreatedAt, row.CreatedAt);

        // ORDER: the store's rows in store order — the pipeline-backed goal first, then the controls.
        Assert.Equal(
            [MergeHitGoalId, MergeControlPendingId, MergeControlCancelledId],
            snapshot.Goals.Select(g => g.Id));
        var pendingRow = Assert.Single(snapshot.Goals, g => g.Id == MergeControlPendingId);
        var cancelledRow = Assert.Single(snapshot.Goals, g => g.Id == MergeControlCancelledId);
        Assert.Same(controlPending, pendingRow);
        Assert.Same(controlCancelled, cancelledRow);
        Assert.Equal(GoalStatus.Pending, pendingRow.Status);
        Assert.Equal(GoalStatus.Cancelled, cancelledRow.Status);

        // The DISPLAYED status drives the exact counts; the controls contribute only their own — the
        // Cancelled control is counted by none of the five, and the live/stored statuses contribute
        // nothing at all.
        Assert.Equal(1, snapshot.PendingGoals);
        Assert.Equal(0, snapshot.DraftGoals);
        Assert.Equal(expectedDisplayStatus == GoalStatus.InProgress ? 1 : 0, snapshot.ActiveGoals);
        Assert.Equal(expectedDisplayStatus == GoalStatus.Completed ? 1 : 0, snapshot.CompletedGoals);
        Assert.Equal(expectedDisplayStatus == GoalStatus.Failed ? 1 : 0, snapshot.FailedGoals);

        // The worker and pipeline projections are unchanged.
        var worker = Assert.Single(snapshot.Workers);
        Assert.Equal(MergeWorkerId, worker.Id);
        Assert.True(worker.IsBusy);
        Assert.Equal("task-merge", worker.CurrentTaskId);
        var pipelineInfo = Assert.Single(snapshot.Pipelines);
        Assert.Equal(MergeHitGoalId, pipelineInfo.GoalId);
        Assert.Equal(phase.ToString(), pipelineInfo.Phase);
        // The pipeline display keeps the LIVE goal's own description (captured by the constructor),
        // NOT the stored row that wins the goal-row merge and NOT a rewritten live goal.
        Assert.Equal(liveGoal.Description, pipelineInfo.Description);
        Assert.Equal("LIVE description", pipeline.Description);
    }
}

/// <summary>
/// Minimal <see cref="IDistributedBrain"/> stub whose <see cref="GetStats"/> returns a
/// fixed <see cref="BrainStats"/> (used to verify dashboard sentinel rendering).
/// </summary>
file sealed class SentinelBrain : IDistributedBrain
{
    private readonly BrainStats _stats;

    public SentinelBrain(BrainStats stats) => _stats = stats;

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<PromptResult> CraftPromptAsync(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct)
        => Task.CompletedTask;
    public Task<BrainResponse> AskQuestionAsync(string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task UpdateModelAsync(string model, int? maxContextTokens, ReasoningEffort? reasoningEffort, CancellationToken ct)
        => Task.CompletedTask;
    public BrainStats? GetStats() => _stats;
    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default)
        => Task.CompletedTask;
    public bool GoalSessionExists(string goalId) => false;
    public Task ResetSessionAsync(CancellationToken ct = default)
        => Task.CompletedTask;
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
