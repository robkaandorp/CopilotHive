using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests verifying that <see cref="TaskBuilder.Build"/> sets the
/// <see cref="WorkTask.SessionId"/> to the format "goalId:roleName".
/// </summary>
public sealed class TaskBuilderSessionIdTests
{
    private static readonly List<TargetRepository> Repos =
    [
        new() { Name = "repo", Url = "https://example.com/repo.git", DefaultBranch = "main" },
    ];

    private static TaskBuilder CreateBuilder() => new(new BranchCoordinator());

    // ── SessionId format ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("goal-abc", WorkerRole.Coder, "goal-abc:coder")]
    [InlineData("goal-xyz", WorkerRole.Tester, "goal-xyz:tester")]
    [InlineData("goal-123", WorkerRole.Reviewer, "goal-123:reviewer")]
    [InlineData("goal-imp", WorkerRole.Improver, "goal-imp:improver")]
    [InlineData("goal-doc", WorkerRole.DocWriter, "goal-doc:docwriter")]
    public void Build_SessionId_HasGoalIdColonRoleName(
        string goalId, WorkerRole role, string expectedSessionId)
    {
        var builder = CreateBuilder();

        var task = builder.Build(
            goalId: goalId,
            goalDescription: "Test goal",
            role: role,
            iteration: 1,
            repositories: Repos,
            prompt: "Do something",
            branchAction: BranchAction.Create);

        Assert.Equal(expectedSessionId, task.SessionId);
    }

    [Fact]
    public void Build_SessionId_ContainsSingleColon()
    {
        var builder = CreateBuilder();

        var task = builder.Build(
            goalId: "goal-1",
            goalDescription: "desc",
            role: WorkerRole.Coder,
            iteration: 1,
            repositories: Repos,
            prompt: "prompt",
            branchAction: BranchAction.Create);

        var parts = task.SessionId.Split(':');
        Assert.Equal(2, parts.Length);
        Assert.Equal("goal-1", parts[0]);
        Assert.Equal("coder", parts[1]);
    }

    [Fact]
    public void Build_SessionId_IsStableAcrossIterations()
    {
        var builder = CreateBuilder();

        var task1 = builder.Build("goal-stable", "d", WorkerRole.Coder, 1, Repos, "p", BranchAction.Create);
        var task2 = builder.Build("goal-stable", "d", WorkerRole.Coder, 2, Repos, "p", BranchAction.Checkout);
        var task3 = builder.Build("goal-stable", "d", WorkerRole.Coder, 5, Repos, "p", BranchAction.Checkout);

        // The TaskId varies per iteration, but SessionId must remain the same
        Assert.NotEqual(task1.TaskId, task2.TaskId);
        Assert.Equal(task1.SessionId, task2.SessionId);
        Assert.Equal(task1.SessionId, task3.SessionId);
        Assert.Equal("goal-stable:coder", task1.SessionId);
    }

    [Fact]
    public void Build_DifferentRoles_HaveDifferentSessionIds()
    {
        var builder = CreateBuilder();

        var coder = builder.Build("g", "d", WorkerRole.Coder, 1, Repos, "p", BranchAction.Create);
        var tester = builder.Build("g", "d", WorkerRole.Tester, 1, Repos, "p", BranchAction.Checkout);

        Assert.NotEqual(coder.SessionId, tester.SessionId);
        Assert.Equal("g:coder", coder.SessionId);
        Assert.Equal("g:tester", tester.SessionId);
    }
}
