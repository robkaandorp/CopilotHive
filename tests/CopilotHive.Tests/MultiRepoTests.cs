using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;
using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

public class MultiRepoTests
{
    // ──────────────────────────────────────────
    //  BranchCoordinator tests
    // ──────────────────────────────────────────

    [Theory]
    [InlineData("feature-xyz", "copilothive/feature-xyz")]
    [InlineData("fix-auth", "copilothive/fix-auth")]
    [InlineData("upgrade-db", "copilothive/upgrade-db")]
    [InlineData("my-goal", "copilothive/my-goal")]
    public void BranchCoordinator_GetFeatureBranch_GeneratesCorrectName(
        string goalId, string expected)
    {
        var coordinator = new BranchCoordinator();
        var branch = coordinator.GetFeatureBranch(goalId);
        Assert.Equal(expected, branch);
    }

    [Fact]
    public void BranchCoordinator_RecordAndGetBranches_TracksPerGoal()
    {
        var coordinator = new BranchCoordinator();

        coordinator.RecordBranchCreated("goal-a", "repo-1", "copilothive/goal-a");
        coordinator.RecordBranchCreated("goal-a", "repo-2", "copilothive/goal-a");
        coordinator.RecordBranchCreated("goal-b", "repo-1", "copilothive/goal-b");

        var goalABranches = coordinator.GetBranchesForGoal("goal-a");
        Assert.Equal(2, goalABranches.Count);
        Assert.Contains(("repo-1", "copilothive/goal-a"), goalABranches);
        Assert.Contains(("repo-2", "copilothive/goal-a"), goalABranches);

        var goalBBranches = coordinator.GetBranchesForGoal("goal-b");
        Assert.Single(goalBBranches);
    }

    [Fact]
    public void BranchCoordinator_GetBranchesForGoal_ReturnsEmptyForUnknownGoal()
    {
        var coordinator = new BranchCoordinator();
        var branches = coordinator.GetBranchesForGoal("nonexistent");
        Assert.Empty(branches);
    }

    [Fact]
    public void BranchCoordinator_RecordBranch_IgnoresDuplicates()
    {
        var coordinator = new BranchCoordinator();

        coordinator.RecordBranchCreated("goal-1", "repo-1", "branch-a");
        coordinator.RecordBranchCreated("goal-1", "repo-1", "branch-a");

        var branches = coordinator.GetBranchesForGoal("goal-1");
        Assert.Single(branches);
    }

    [Fact]
    public void BranchCoordinator_GetBranchInfo_ReturnsCorrectProto()
    {
        var coordinator = new BranchCoordinator();
        var info = coordinator.GetBranchInfo("goal-1", BranchAction.Create, "develop");

        Assert.Equal("develop", info.BaseBranch);
        Assert.Equal("copilothive/goal-1", info.FeatureBranch);
        Assert.Equal(BranchAction.Create, info.Action);
    }

    // ──────────────────────────────────────────
    //  TaskBuilder tests
    // ──────────────────────────────────────────

    [Fact]
    public void TaskBuilder_Build_CreatesAssignmentWithMultipleRepos()
    {
        var coordinator = new BranchCoordinator();
        var builder = new TaskBuilder(coordinator);

        var repos = new List<TargetRepository>
        {
            new() { Name = "api-service", Url = "https://github.com/org/api-service.git" },
            new() { Name = "web-app", Url = "https://github.com/org/web-app.git", DefaultBranch = "develop" },
        };

        var assignment = builder.Build(
            goalId: "multi-svc",
            goalDescription: "Add cross-service feature",
            role: WorkerRole.Coder,
            iteration: 1,
            repositories: repos,
            prompt: "Implement the new endpoint",
            branchAction: BranchAction.Create);

        Assert.Equal(2, assignment.Repositories.Count);
        Assert.Equal("api-service", assignment.Repositories[0].Name);
        Assert.Equal("https://github.com/org/api-service.git", assignment.Repositories[0].Url);
        Assert.Equal("main", assignment.Repositories[0].DefaultBranch);
        Assert.Equal("web-app", assignment.Repositories[1].Name);
        Assert.Equal("develop", assignment.Repositories[1].DefaultBranch);
    }

    [Fact]
    public void TaskBuilder_Build_SetsCorrectBranchInfoForCreate()
    {
        var coordinator = new BranchCoordinator();
        var builder = new TaskBuilder(coordinator);

        var repos = new List<TargetRepository>
        {
            new() { Name = "svc", Url = "https://example.com/svc.git", DefaultBranch = "develop" },
        };

        var assignment = builder.Build(
            "goal-x", "desc", WorkerRole.Coder, 2, repos, "do stuff", BranchAction.Create);

        Assert.Equal("develop", assignment.BranchInfo.BaseBranch);
        Assert.Equal("copilothive/goal-x", assignment.BranchInfo.FeatureBranch);
        Assert.Equal(BranchAction.Create, assignment.BranchInfo.Action);
    }

    [Fact]
    public void TaskBuilder_Build_SetsCorrectBranchInfoForCheckout()
    {
        var coordinator = new BranchCoordinator();
        var builder = new TaskBuilder(coordinator);

        var repos = new List<TargetRepository>
        {
            new() { Name = "svc", Url = "https://example.com/svc.git" },
        };

        var assignment = builder.Build(
            "goal-y", "review changes", WorkerRole.Reviewer, 5, repos, "review", BranchAction.Checkout);

        Assert.Equal(BranchAction.Checkout, assignment.BranchInfo.Action);
        Assert.Equal("copilothive/goal-y", assignment.BranchInfo.FeatureBranch);
    }

    [Fact]
    public void TaskBuilder_Build_SetsTaskIdAndMetadata()
    {
        var coordinator = new BranchCoordinator();
        var builder = new TaskBuilder(coordinator);

        var repos = new List<TargetRepository>
        {
            new() { Name = "app", Url = "https://example.com/app.git" },
        };

        var assignment = builder.Build(
            "goal-z", "test it", WorkerRole.Tester, 7, repos, "run tests", BranchAction.Checkout);

        Assert.Equal("goal-z-tester-007", assignment.TaskId);
        Assert.Equal("goal-z", assignment.GoalId);
        Assert.Equal("test it", assignment.GoalDescription);
        Assert.Equal("run tests", assignment.Prompt);
        Assert.Equal(CopilotHive.Shared.Grpc.WorkerRole.Tester, assignment.Role);
    }

    // ──────────────────────────────────────────
    //  MultiRepoGoal model tests
    // ──────────────────────────────────────────

    [Fact]
    public void MultiRepoGoal_Construction_SetsAllProperties()
    {
        var repos = new List<TargetRepository>
        {
            new()
            {
                Name = "frontend",
                Url = "https://github.com/org/frontend.git",
                DefaultBranch = "develop",
                SpecificInstructions = "Use React 19",
            },
            new()
            {
                Name = "backend",
                Url = "https://github.com/org/backend.git",
                SpecificInstructions = "Add API endpoint",
            },
        };

        var goal = new MultiRepoGoal
        {
            Id = "cross-svc-feature",
            Description = "Implement cross-service authentication",
            Repositories = repos,
            BranchPrefix = "copilothive/auth-feature",
            Priority = GoalPriority.High,
        };

        Assert.Equal("cross-svc-feature", goal.Id);
        Assert.Equal("Implement cross-service authentication", goal.Description);
        Assert.Equal(2, goal.Repositories.Count);
        Assert.Equal("copilothive/auth-feature", goal.BranchPrefix);
        Assert.Equal(GoalPriority.High, goal.Priority);
        Assert.Equal(GoalStatus.Pending, goal.Status);
    }

    [Fact]
    public void TargetRepository_Defaults_UsesMain()
    {
        var repo = new TargetRepository
        {
            Name = "my-repo",
            Url = "https://github.com/org/my-repo.git",
        };

        Assert.Equal("main", repo.DefaultBranch);
        Assert.Null(repo.SpecificInstructions);
    }
}
