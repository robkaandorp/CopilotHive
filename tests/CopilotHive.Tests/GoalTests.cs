using CopilotHive.Goals;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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

    // ── Goal filtering (skip completed/failed) ──────────────────────────────

    [Fact]
    public async Task FileGoalSource_GetPending_SkipsCompletedGoals()
    {
        var path = WriteTempYaml("""
            goals:
              - id: done-goal
                description: "Already done"
                status: completed
              - id: pending-goal
                description: "Still pending"
                status: pending
            """);

        var source = new FileGoalSource(path);
        var pending = await source.GetPendingGoalsAsync();

        Assert.Single(pending);
        Assert.Equal("pending-goal", pending[0].Id);
    }

    [Fact]
    public async Task FileGoalSource_GetPending_SkipsFailedGoals()
    {
        var path = WriteTempYaml("""
            goals:
              - id: failed-goal
                description: "Already failed"
                status: failed
                failure_reason: "tests never passed"
              - id: new-goal
                description: "Fresh goal"
            """);

        var source = new FileGoalSource(path);
        var pending = await source.GetPendingGoalsAsync();

        Assert.Single(pending);
        Assert.Equal("new-goal", pending[0].Id);
    }

    [Fact]
    public async Task FileGoalSource_GetPending_SkipsInProgressGoals()
    {
        var path = WriteTempYaml("""
            goals:
              - id: active-goal
                description: "Currently running"
                status: in_progress
              - id: waiting-goal
                description: "Not started"
                status: pending
            """);

        var source = new FileGoalSource(path);
        var pending = await source.GetPendingGoalsAsync();

        Assert.Single(pending);
        Assert.Equal("waiting-goal", pending[0].Id);
    }

    // ── YAML round-trip (serialize → deserialize preserves status fields) ────

    [Fact]
    public async Task FileGoalSource_RoundTrip_PreservesStatusFields()
    {
        var path = WriteTempYaml("""
            goals:
              - id: round-trip-goal
                description: "Test round-trip"
                priority: high
                repositories:
                  - my-repo
            """);

        var source = new FileGoalSource(path);

        // Simulate goal being picked up (in_progress with started_at)
        var startedAt = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        await source.UpdateGoalStatusAsync("round-trip-goal", GoalStatus.InProgress,
            new GoalUpdateMetadata { StartedAt = startedAt });

        // Re-read and verify
        var source2 = new FileGoalSource(path);
        var goals = await source2.ReadGoalsAsync();

        Assert.Single(goals);
        var goal = goals[0];
        Assert.Equal("round-trip-goal", goal.Id);
        Assert.Equal(GoalStatus.InProgress, goal.Status);
        Assert.Equal(startedAt, goal.StartedAt);
        Assert.Null(goal.CompletedAt);
        Assert.Null(goal.Iterations);
        Assert.Null(goal.FailureReason);
    }

    [Fact]
    public async Task FileGoalSource_RoundTrip_PreservesCompletionFields()
    {
        var path = WriteTempYaml("""
            goals:
              - id: completed-goal
                description: "Will complete"
                priority: normal
            """);

        var source = new FileGoalSource(path);

        var startedAt = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var completedAt = new DateTime(2025, 6, 15, 11, 30, 0, DateTimeKind.Utc);

        // Set in-progress
        await source.UpdateGoalStatusAsync("completed-goal", GoalStatus.InProgress,
            new GoalUpdateMetadata { StartedAt = startedAt });

        // Set completed
        await source.UpdateGoalStatusAsync("completed-goal", GoalStatus.Completed,
            new GoalUpdateMetadata { CompletedAt = completedAt, Iterations = 3 });

        // Re-read from file
        var source2 = new FileGoalSource(path);
        var goals = await source2.ReadGoalsAsync();

        Assert.Single(goals);
        var goal = goals[0];
        Assert.Equal(GoalStatus.Completed, goal.Status);
        Assert.Equal(startedAt, goal.StartedAt);
        Assert.Equal(completedAt, goal.CompletedAt);
        Assert.Equal(3, goal.Iterations);
        Assert.Null(goal.FailureReason);
    }

    [Fact]
    public async Task FileGoalSource_RoundTrip_PreservesFailureFields()
    {
        var path = WriteTempYaml("""
            goals:
              - id: failing-goal
                description: "Will fail"
            """);

        var source = new FileGoalSource(path);

        await source.UpdateGoalStatusAsync("failing-goal", GoalStatus.Failed,
            new GoalUpdateMetadata
            {
                CompletedAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
                Iterations = 5,
                FailureReason = "Exceeded max test retries",
            });

        // Re-read from file
        var source2 = new FileGoalSource(path);
        var goals = await source2.ReadGoalsAsync();

        Assert.Single(goals);
        var goal = goals[0];
        Assert.Equal(GoalStatus.Failed, goal.Status);
        Assert.Equal(5, goal.Iterations);
        Assert.Equal("Exceeded max test retries", goal.FailureReason);
    }

    [Fact]
    public async Task FileGoalSource_RoundTrip_MultipleGoalsMixedStatus()
    {
        var path = WriteTempYaml("""
            goals:
              - id: goal-a
                description: "First goal"
                priority: critical
              - id: goal-b
                description: "Second goal"
                priority: high
              - id: goal-c
                description: "Third goal"
                priority: normal
            """);

        var source = new FileGoalSource(path);

        // Complete goal-a
        await source.UpdateGoalStatusAsync("goal-a", GoalStatus.Completed,
            new GoalUpdateMetadata { CompletedAt = DateTime.UtcNow, Iterations = 2 });

        // Fail goal-b
        await source.UpdateGoalStatusAsync("goal-b", GoalStatus.Failed,
            new GoalUpdateMetadata { FailureReason = "merge conflict" });

        // goal-c stays pending

        // Re-read — only pending goals returned by GetPendingGoalsAsync
        var source2 = new FileGoalSource(path);
        var pending = await source2.GetPendingGoalsAsync();
        Assert.Single(pending);
        Assert.Equal("goal-c", pending[0].Id);

        // But all goals are in the file
        var all = await source2.ReadGoalsAsync();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task FileGoalSource_ReadGoals_ParsesExistingStatusFields()
    {
        var path = WriteTempYaml("""
            goals:
              - id: pre-existing
                description: "Already has status fields"
                status: completed
                started_at: "2025-06-15T10:00:00Z"
                completed_at: "2025-06-15T11:30:00Z"
                iterations: 4
              - id: pre-failed
                description: "Previously failed"
                status: failed
                failure_reason: "timeout exceeded"
                iterations: 7
            """);

        var source = new FileGoalSource(path);
        var goals = await source.ReadGoalsAsync();

        Assert.Equal(2, goals.Count);

        var completed = goals[0];
        Assert.Equal(GoalStatus.Completed, completed.Status);
        Assert.Equal(new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc), completed.StartedAt);
        Assert.Equal(new DateTime(2025, 6, 15, 11, 30, 0, DateTimeKind.Utc), completed.CompletedAt);
        Assert.Equal(4, completed.Iterations);

        var failed = goals[1];
        Assert.Equal(GoalStatus.Failed, failed.Status);
        Assert.Equal("timeout exceeded", failed.FailureReason);
        Assert.Equal(7, failed.Iterations);
    }

    [Fact]
    public async Task FileGoalSource_NullStatusFields_OmittedFromYaml()
    {
        var path = WriteTempYaml("""
            goals:
              - id: clean-goal
                description: "No extra fields"
            """);

        var source = new FileGoalSource(path);
        // Re-write via update to force serialization
        await source.UpdateGoalStatusAsync("clean-goal", GoalStatus.Pending);

        var yaml = await File.ReadAllTextAsync(path);

        // Null fields should not appear in the output
        Assert.DoesNotContain("started_at", yaml);
        Assert.DoesNotContain("completed_at", yaml);
        Assert.DoesNotContain("iterations", yaml);
        Assert.DoesNotContain("failure_reason", yaml);
    }

    // ── PhaseDurations ──────────────────────────────────────────────────────

    [Fact]
    public void GoalFileEntry_PhaseDurations_RoundTripsViaYaml()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var entry = new FileGoalSource.GoalFileEntry
        {
            Id = "test-goal",
            Status = "completed",
            PhaseDurations = new Dictionary<string, double>
            {
                ["Coding"] = 42.5,
                ["Review"] = 10.0,
            },
        };

        var yaml = serializer.Serialize(entry);
        var roundTripped = deserializer.Deserialize<FileGoalSource.GoalFileEntry>(yaml);

        Assert.NotNull(roundTripped.PhaseDurations);
        Assert.Equal(42.5, roundTripped.PhaseDurations["Coding"]);
        Assert.Equal(10.0, roundTripped.PhaseDurations["Review"]);
    }

    [Fact]
    public async Task FileGoalSource_WritesPhaseDurationsToYaml_WhenSet()
    {
        var path = WriteTempYaml("""
            goals:
              - id: tracked-goal
                description: "A goal with phase tracking"
            """);

        var source = new FileGoalSource(path);
        var metadata = new GoalUpdateMetadata
        {
            CompletedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PhaseDurations = new Dictionary<string, double>
            {
                ["Coding"] = 120.5,
                ["Testing"] = 30.0,
            },
        };

        await source.UpdateGoalStatusAsync("tracked-goal", GoalStatus.Completed, metadata);

        var yaml = await File.ReadAllTextAsync(path);
        Assert.Contains("phase_durations", yaml);
        Assert.Contains("Coding: 120.5", yaml);
        Assert.Contains("Testing: 30", yaml);
    }

    [Fact]
    public async Task FileGoalSource_ReadsPhaseDurationsFromYaml_Correctly()
    {
        var path = WriteTempYaml("""
            goals:
              - id: goal-with-phases
                description: "Has phase durations"
                status: completed
                phase_durations:
                  Coding: 95.0
                  Review: 20.5
            """);

        var source = new FileGoalSource(path);
        var goals = await source.ReadGoalsAsync();

        Assert.Single(goals);
        var goal = goals[0];
        Assert.NotNull(goal.PhaseDurations);
        Assert.Equal(95.0, goal.PhaseDurations["Coding"]);
        Assert.Equal(20.5, goal.PhaseDurations["Review"]);
    }

    [Fact]
    public async Task FileGoalSource_NullPhaseDurations_DoesNotWritePhaseDurationsKey()
    {
        var path = WriteTempYaml("""
            goals:
              - id: no-phases-goal
                description: "Goal without phase tracking"
            """);

        var source = new FileGoalSource(path);
        var metadata = new GoalUpdateMetadata
        {
            CompletedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PhaseDurations = null,
        };

        await source.UpdateGoalStatusAsync("no-phases-goal", GoalStatus.Completed, metadata);

        var yaml = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("phase_durations", yaml);
    }
}
