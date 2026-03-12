using CopilotHive.Goals;

namespace CopilotHive.Tests;

public sealed class GoalTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"goaltests-{Guid.NewGuid():N}");

    public GoalTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteTempYaml(string content)
    {
        var path = Path.Combine(_tempDir, $"goals-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    // ── FileGoalSource ───────────────────────────────────────────────────────

    [Fact]
    public async Task FileGoalSource_ParsesValidYaml()
    {
        var path = WriteTempYaml("""
            goals:
              - id: add-dark-mode
                description: "Add dark mode support"
                priority: high
                repositories:
                  - my-app
              - id: fix-api-timeout
                description: "Fix timeout issue"
                priority: critical
                repositories:
                  - my-api
            """);

        var source = new FileGoalSource(path);
        var goals = await source.GetPendingGoalsAsync();

        Assert.Equal(2, goals.Count);
        Assert.Equal("add-dark-mode", goals[0].Id);
        Assert.Equal(GoalPriority.High, goals[0].Priority);
        Assert.Equal("fix-api-timeout", goals[1].Id);
        Assert.Equal(GoalPriority.Critical, goals[1].Priority);
        Assert.Equal(["my-api"], goals[1].RepositoryNames);
    }

    [Fact]
    public async Task FileGoalSource_EmptyFile_ReturnsEmpty()
    {
        var path = WriteTempYaml("");
        var source = new FileGoalSource(path);

        var goals = await source.GetPendingGoalsAsync();

        Assert.Empty(goals);
    }

    [Fact]
    public async Task FileGoalSource_MissingFile_ReturnsEmpty()
    {
        var source = new FileGoalSource(Path.Combine(_tempDir, "nonexistent.yaml"));
        var goals = await source.GetPendingGoalsAsync();

        Assert.Empty(goals);
    }

    [Fact]
    public async Task FileGoalSource_MissingFields_UsesDefaults()
    {
        var path = WriteTempYaml("""
            goals:
              - id: minimal-goal
            """);

        var source = new FileGoalSource(path);
        var goals = await source.GetPendingGoalsAsync();

        Assert.Single(goals);
        Assert.Equal("minimal-goal", goals[0].Id);
        Assert.Equal(string.Empty, goals[0].Description);
        Assert.Equal(GoalPriority.Normal, goals[0].Priority);
        Assert.Equal(GoalStatus.Pending, goals[0].Status);
        Assert.Empty(goals[0].RepositoryNames);
    }

    [Fact]
    public async Task FileGoalSource_UpdateStatus_PersistsToFile()
    {
        var path = WriteTempYaml("""
            goals:
              - id: task-1
                description: "A task"
                priority: normal
            """);

        var source = new FileGoalSource(path);
        await source.UpdateGoalStatusAsync("task-1", GoalStatus.Completed);

        // Re-read from file to confirm persistence
        var source2 = new FileGoalSource(path);
        var all = await source2.ReadGoalsAsync();

        Assert.Single(all);
        Assert.Equal(GoalStatus.Completed, all[0].Status);
    }

    [Fact]
    public async Task FileGoalSource_UpdateStatus_UnknownGoal_Throws()
    {
        var path = WriteTempYaml("""
            goals:
              - id: existing
                description: "exists"
            """);

        var source = new FileGoalSource(path);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => source.UpdateGoalStatusAsync("nonexistent", GoalStatus.Failed));
    }

    // ── ApiGoalSource ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiGoalSource_AddAndGetPending()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "g1", Description = "First goal" });
        source.AddGoal(new Goal { Id = "g2", Description = "Second goal" });

        var pending = await source.GetPendingGoalsAsync();
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public async Task ApiGoalSource_UpdateStatus_RemovesFromPending()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "g1", Description = "Goal one" });

        await source.UpdateGoalStatusAsync("g1", GoalStatus.Completed);

        var pending = await source.GetPendingGoalsAsync();
        Assert.Empty(pending);

        var all = source.GetAllGoals();
        Assert.Single(all);
        Assert.Equal(GoalStatus.Completed, all[0].Status);
    }

    [Fact]
    public async Task ApiGoalSource_DuplicateId_Throws()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "dup", Description = "First" });

        Assert.Throws<InvalidOperationException>(
            () => source.AddGoal(new Goal { Id = "dup", Description = "Second" }));

        var pending = await source.GetPendingGoalsAsync();
        Assert.Single(pending);
    }

    // ── GoalManager ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GoalManager_GetNextGoal_ReturnsHighestPriority()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "low", Description = "Low", Priority = GoalPriority.Low });
        source.AddGoal(new Goal { Id = "critical", Description = "Critical", Priority = GoalPriority.Critical });
        source.AddGoal(new Goal { Id = "high", Description = "High", Priority = GoalPriority.High });

        var manager = new GoalManager();
        manager.AddSource(source);

        var next = await manager.GetNextGoalAsync();

        Assert.NotNull(next);
        Assert.Equal("critical", next.Id);
    }

    [Fact]
    public async Task GoalManager_CompleteGoal_UpdatesStatus()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "task-1", Description = "Do something" });

        var manager = new GoalManager();
        manager.AddSource(source);

        // GetNextGoalAsync registers the goal in the source map
        var goal = await manager.GetNextGoalAsync();
        Assert.NotNull(goal);

        await manager.CompleteGoalAsync("task-1");

        Assert.Equal(GoalStatus.Completed, source.GetGoal("task-1")!.Status);
    }

    [Fact]
    public async Task GoalManager_FailGoal_UpdatesStatus()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "task-2", Description = "Will fail" });

        var manager = new GoalManager();
        manager.AddSource(source);

        _ = await manager.GetNextGoalAsync();
        await manager.FailGoalAsync("task-2", "tests failed");

        Assert.Equal(GoalStatus.Failed, source.GetGoal("task-2")!.Status);
    }

    [Fact]
    public async Task GoalManager_NoGoals_ReturnsNull()
    {
        var manager = new GoalManager();
        manager.AddSource(new ApiGoalSource());

        var next = await manager.GetNextGoalAsync();
        Assert.Null(next);
    }

    [Fact]
    public async Task GoalManager_MultipleSources_PrioritizesAcross()
    {
        var fileSource = new ApiGoalSource();
        fileSource.AddGoal(new Goal { Id = "file-high", Description = "File high", Priority = GoalPriority.High });

        var apiSource = new ApiGoalSource();
        apiSource.AddGoal(new Goal { Id = "api-critical", Description = "API critical", Priority = GoalPriority.Critical });

        var manager = new GoalManager();
        manager.AddSource(fileSource);
        manager.AddSource(apiSource);

        var next = await manager.GetNextGoalAsync();

        Assert.NotNull(next);
        Assert.Equal("api-critical", next.Id);
    }

    // ── Priority ordering ────────────────────────────────────────────────────

    [Theory]
    [InlineData(GoalPriority.Critical, GoalPriority.High)]
    [InlineData(GoalPriority.High, GoalPriority.Normal)]
    [InlineData(GoalPriority.Normal, GoalPriority.Low)]
    [InlineData(GoalPriority.Critical, GoalPriority.Low)]
    public async Task PriorityOrdering_HigherPrioritySelected(GoalPriority higher, GoalPriority lower)
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "lower", Description = "Lower", Priority = lower });
        source.AddGoal(new Goal { Id = "higher", Description = "Higher", Priority = higher });

        var manager = new GoalManager();
        manager.AddSource(source);

        var next = await manager.GetNextGoalAsync();

        Assert.NotNull(next);
        Assert.Equal("higher", next.Id);
    }
}
