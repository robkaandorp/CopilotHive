using System.Collections.Concurrent;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="DispatcherMaintenance.CleanupMergedBranchesAsync"/>.
/// </summary>
public sealed class DispatcherMaintenanceTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static DispatcherMaintenance CreateMaintenance(
        IGoalStore? goalStore = null,
        IBrainRepoManager? repoManager = null,
        HiveConfigFile? config = null)
    {
        var pipelineManager = new GoalPipelineManager();
        var goalManager = new GoalManager();
        var taskQueue = new TaskQueue();
        var workerGateway = new GrpcWorkerGateway(new WorkerPool());

        return new DispatcherMaintenance(
            pipelineManager,
            goalManager,
            taskQueue,
            workerGateway,
            brain: null,
            agentsManager: null,
            configRepo: null,
            dispatchedGoals: new ConcurrentDictionary<string, bool>(),
            redispatchQueue: new ConcurrentQueue<string>(),
            logger: NullLogger.Instance,
            knowledgeGraph: null,
            goalStore: goalStore,
            repoManager: repoManager,
            config: config);
    }

    private static Goal MakeCompletedGoal(
        string id,
        DateTime completedAt,
        string? mergeCommitHash = "abc123",
        bool branchCleanedUp = false,
        string repoName = "my-repo")
        => new()
        {
            Id = id,
            Description = "Test goal",
            Status = GoalStatus.Completed,
            CompletedAt = completedAt,
            MergeCommitHash = mergeCommitHash,
            BranchCleanedUp = branchCleanedUp,
            RepositoryNames = [repoName],
        };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupMergedBranches_RepoManagerNull_ReturnsEarly()
    {
        // When repoManager is null, method returns immediately without touching goalStore
        var goalStore = new Mock<IGoalStore>();
        var maintenance = CreateMaintenance(goalStore: goalStore.Object, repoManager: null);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        goalStore.Verify(s => s.GetGoalsByStatusAsync(It.IsAny<GoalStatus>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupMergedBranches_GoalStoreNull_ReturnsEarly()
    {
        // When goalStore is null, method returns immediately
        var repoManager = new Mock<IBrainRepoManager>();
        var maintenance = CreateMaintenance(goalStore: null, repoManager: repoManager.Object);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        repoManager.Verify(r => r.DeleteRemoteBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupMergedBranches_GoalCompletedWithinDelayWindow_Skipped()
    {
        // Goal completed 10 hours ago, delay is 48h → within window → skip
        var goal = MakeCompletedGoal("recent-goal", completedAt: DateTime.UtcNow.AddHours(-10));

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalsByStatusAsync(GoalStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)[goal]);

        var repoManager = new Mock<IBrainRepoManager>();
        var config = new HiveConfigFile { Orchestrator = { BranchCleanupDelayHours = 48 } };
        var maintenance = CreateMaintenance(goalStore.Object, repoManager.Object, config);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        repoManager.Verify(r => r.DeleteRemoteBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupMergedBranches_GoalPastDelayWindowWithMergeHash_DeletesBranch()
    {
        // Goal completed 72 hours ago, delay is 48h → past window → delete
        var goal = MakeCompletedGoal("old-goal", completedAt: DateTime.UtcNow.AddHours(-72));

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalsByStatusAsync(GoalStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)[goal]);

        var repoManager = new Mock<IBrainRepoManager>();
        var config = new HiveConfigFile { Orchestrator = { BranchCleanupDelayHours = 48 } };
        var maintenance = CreateMaintenance(goalStore.Object, repoManager.Object, config);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        repoManager.Verify(r => r.DeleteRemoteBranchAsync("my-repo", "copilothive/old-goal", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanupMergedBranches_GoalPastDelayWindow_SetsBranchCleanedUpAndPersists()
    {
        var goal = MakeCompletedGoal("old-goal", completedAt: DateTime.UtcNow.AddHours(-72));

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalsByStatusAsync(GoalStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)[goal]);

        var repoManager = new Mock<IBrainRepoManager>();
        var config = new HiveConfigFile { Orchestrator = { BranchCleanupDelayHours = 48 } };
        var maintenance = CreateMaintenance(goalStore.Object, repoManager.Object, config);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        Assert.True(goal.BranchCleanedUp);
        goalStore.Verify(s => s.UpdateGoalAsync(goal, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanupMergedBranches_GoalWithoutMergeCommitHash_Skipped()
    {
        // No merge commit hash → was never merged → skip
        var goal = MakeCompletedGoal("unmerged-goal", completedAt: DateTime.UtcNow.AddHours(-72), mergeCommitHash: null);

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalsByStatusAsync(GoalStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)[goal]);

        var repoManager = new Mock<IBrainRepoManager>();
        var config = new HiveConfigFile { Orchestrator = { BranchCleanupDelayHours = 48 } };
        var maintenance = CreateMaintenance(goalStore.Object, repoManager.Object, config);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        repoManager.Verify(r => r.DeleteRemoteBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupMergedBranches_GoalWithEmptyMergeCommitHash_Skipped()
    {
        // Empty merge commit hash → treated as not merged → skip
        var goal = MakeCompletedGoal("empty-hash-goal", completedAt: DateTime.UtcNow.AddHours(-72), mergeCommitHash: "");

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalsByStatusAsync(GoalStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)[goal]);

        var repoManager = new Mock<IBrainRepoManager>();
        var config = new HiveConfigFile { Orchestrator = { BranchCleanupDelayHours = 48 } };
        var maintenance = CreateMaintenance(goalStore.Object, repoManager.Object, config);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        repoManager.Verify(r => r.DeleteRemoteBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupMergedBranches_DelayZero_ImmediateCleanup()
    {
        // With delay = 0, any completed goal with a merge hash should be cleaned up
        var goal = MakeCompletedGoal("just-completed", completedAt: DateTime.UtcNow.AddSeconds(-1));

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalsByStatusAsync(GoalStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)[goal]);

        var repoManager = new Mock<IBrainRepoManager>();
        var config = new HiveConfigFile { Orchestrator = { BranchCleanupDelayHours = 0 } };
        var maintenance = CreateMaintenance(goalStore.Object, repoManager.Object, config);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        repoManager.Verify(r => r.DeleteRemoteBranchAsync("my-repo", "copilothive/just-completed", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanupMergedBranches_AlreadyCleanedUp_Skipped()
    {
        // BranchCleanedUp = true → already done, skip
        var goal = MakeCompletedGoal("done-goal", completedAt: DateTime.UtcNow.AddHours(-72), branchCleanedUp: true);

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalsByStatusAsync(GoalStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)[goal]);

        var repoManager = new Mock<IBrainRepoManager>();
        var config = new HiveConfigFile { Orchestrator = { BranchCleanupDelayHours = 48 } };
        var maintenance = CreateMaintenance(goalStore.Object, repoManager.Object, config);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        repoManager.Verify(r => r.DeleteRemoteBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupMergedBranches_FailedDeletion_LogsWarningButContinues()
    {
        // Two goals: first fails, second succeeds. Second should still be cleaned up.
        var goal1 = MakeCompletedGoal("fail-goal", completedAt: DateTime.UtcNow.AddHours(-72), repoName: "repo-a");
        var goal2 = MakeCompletedGoal("ok-goal", completedAt: DateTime.UtcNow.AddHours(-72), repoName: "repo-b");

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalsByStatusAsync(GoalStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)[goal1, goal2]);

        var repoManager = new Mock<IBrainRepoManager>();
        // First goal's repo throws
        repoManager.Setup(r => r.DeleteRemoteBranchAsync("repo-a", "copilothive/fail-goal", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("git push failed"));
        // Second goal's repo succeeds
        repoManager.Setup(r => r.DeleteRemoteBranchAsync("repo-b", "copilothive/ok-goal", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var config = new HiveConfigFile { Orchestrator = { BranchCleanupDelayHours = 48 } };
        var maintenance = CreateMaintenance(goalStore.Object, repoManager.Object, config);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        // Second goal was cleaned up despite first failing
        repoManager.Verify(r => r.DeleteRemoteBranchAsync("repo-b", "copilothive/ok-goal", It.IsAny<CancellationToken>()), Times.Once);

        // BranchCleanedUp persisted for second goal only (first failed)
        Assert.False(goal1.BranchCleanedUp);
        Assert.True(goal2.BranchCleanedUp);
        goalStore.Verify(s => s.UpdateGoalAsync(goal2, It.IsAny<CancellationToken>()), Times.Once);
        goalStore.Verify(s => s.UpdateGoalAsync(goal1, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupMergedBranches_DefaultConfig_Uses48HourDelay()
    {
        // No config provided → default 48h delay. Goal 47h old → skip. Goal 49h old → clean.
        var recentGoal = MakeCompletedGoal("recent", completedAt: DateTime.UtcNow.AddHours(-47));
        var oldGoal = MakeCompletedGoal("old", completedAt: DateTime.UtcNow.AddHours(-49));

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalsByStatusAsync(GoalStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)[recentGoal, oldGoal]);

        var repoManager = new Mock<IBrainRepoManager>();
        var maintenance = CreateMaintenance(goalStore.Object, repoManager.Object, config: null);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        repoManager.Verify(r => r.DeleteRemoteBranchAsync("my-repo", "copilothive/recent", It.IsAny<CancellationToken>()), Times.Never);
        repoManager.Verify(r => r.DeleteRemoteBranchAsync("my-repo", "copilothive/old", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanupMergedBranches_MultipleRepos_DeletesFromAll()
    {
        // Goal has two repos — should delete from both
        var goal = new Goal
        {
            Id = "multi-repo-goal",
            Description = "Multi repo",
            Status = GoalStatus.Completed,
            CompletedAt = DateTime.UtcNow.AddHours(-72),
            MergeCommitHash = "abc123",
            BranchCleanedUp = false,
            RepositoryNames = ["repo-a", "repo-b"],
        };

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalsByStatusAsync(GoalStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)[goal]);

        var repoManager = new Mock<IBrainRepoManager>();
        var config = new HiveConfigFile { Orchestrator = { BranchCleanupDelayHours = 48 } };
        var maintenance = CreateMaintenance(goalStore.Object, repoManager.Object, config);

        await maintenance.CleanupMergedBranchesAsync(CancellationToken.None);

        repoManager.Verify(r => r.DeleteRemoteBranchAsync("repo-a", "copilothive/multi-repo-goal", It.IsAny<CancellationToken>()), Times.Once);
        repoManager.Verify(r => r.DeleteRemoteBranchAsync("repo-b", "copilothive/multi-repo-goal", It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(goal.BranchCleanedUp);
    }
}
