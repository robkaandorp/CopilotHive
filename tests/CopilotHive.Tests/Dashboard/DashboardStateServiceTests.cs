using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Services;
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
