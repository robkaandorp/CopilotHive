using CopilotHive.Goals;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for <see cref="FileGoalSource"/> YAML round-trip and file-backed flows.
/// </summary>
public sealed class FileGoalSourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fgs-tests-{Guid.NewGuid():N}");

    public FileGoalSourceTests() => Directory.CreateDirectory(_tempDir);

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

    /// <summary>
    /// Verifies that <see cref="FileGoalSource.UpdateGoalStatusAsync"/> round-trips
    /// <c>total_duration_seconds</c> through the YAML file correctly.
    /// </summary>
    [Fact]
    public async Task UpdateGoalStatusAsync_RoundTripsTotalDurationSeconds()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTempYaml(
            """
            goals:
              - id: round-trip-test
                description: Test
                started_at: "2025-01-01T00:00:00Z"
            """);

        var source = new FileGoalSource(path);

        var startedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var completedAt = startedAt.AddMinutes(5).AddSeconds(32);
        await source.UpdateGoalStatusAsync("round-trip-test", GoalStatus.Completed, new GoalUpdateMetadata
        {
            StartedAt = startedAt,
            CompletedAt = completedAt,
            TotalDurationSeconds = 332,
        }, ct);

        // Verify the yaml contains total_duration_seconds: 332
        var yaml = await File.ReadAllTextAsync(path, ct);
        Assert.Contains("total_duration_seconds: 332", yaml);

        // Also verify full round-trip: re-read the goal and check the value
        var source2 = new FileGoalSource(path);
        var goals = await source2.ReadGoalsAsync(ct);
        var goal = Assert.Single(goals);
        Assert.Equal(332, goal.TotalDurationSeconds);
    }

    /// <summary>
    /// Verifies that <c>started_at</c> is round-tripped correctly through
    /// the YAML file and survives serialization/deserialization.
    /// </summary>
    [Fact]
    public async Task UpdateGoalStatusAsync_RoundTripsStartedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTempYaml(
            """
            goals:
              - id: started-at-test
                description: Test start time
            """);

        var source = new FileGoalSource(path);

        var startedAt = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        await source.UpdateGoalStatusAsync("started-at-test", GoalStatus.InProgress, new GoalUpdateMetadata
        {
            StartedAt = startedAt,
        }, ct);

        // Re-read and verify
        var source2 = new FileGoalSource(path);
        var goals = await source2.ReadGoalsAsync(ct);
        var goal = Assert.Single(goals);
        Assert.NotNull(goal.StartedAt);
        Assert.Equal(startedAt, goal.StartedAt.Value);
    }

    /// <summary>
    /// Verifies that <c>depends_on</c> is round-tripped correctly through
    /// the YAML file and survives serialization/deserialization.
    /// </summary>
    [Fact]
    public async Task DependsOn_FileGoalSourceRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTempYaml(
            """
            goals:
              - id: dep-test
                description: Test dependencies
                depends_on:
                  - goal-x
            """);

        var source = new FileGoalSource(path);
        var goals = await source.ReadGoalsAsync(ct);
        var goal = Assert.Single(goals);
        Assert.Single(goal.DependsOn);
        Assert.Contains("goal-x", goal.DependsOn);

        // Now update and re-read to verify round-trip through write
        await source.UpdateGoalStatusAsync("dep-test", GoalStatus.InProgress, ct: ct);

        var source2 = new FileGoalSource(path);
        var goals2 = await source2.ReadGoalsAsync(ct);
        var goal2 = Assert.Single(goals2);
        Assert.Single(goal2.DependsOn);
        Assert.Contains("goal-x", goal2.DependsOn);
    }
}
