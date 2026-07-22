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

    /// <summary>
    /// Verifies that <see cref="FileGoalSource.UpdateGoalStatusAsync"/> propagates
    /// <see cref="GoalUpdateMetadata.MergeCommitHash"/> to the YAML file (round-trip).
    /// </summary>
    [Fact]
    public async Task UpdateGoalStatusAsync_PropagatesMergeCommitHash()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTempYaml(
            """
            goals:
              - id: merge-hash-test
                description: Test merge hash
            """);

        var source = new FileGoalSource(path);
        await source.UpdateGoalStatusAsync("merge-hash-test", GoalStatus.Completed, new GoalUpdateMetadata
        {
            MergeCommitHash = "abc123merge",
        }, ct);

        // Re-read: MergeCommitHash is persisted to YAML and should survive a round-trip.
        var goals = await source.ReadGoalsAsync(ct);
        var goal = Assert.Single(goals);
        Assert.Equal("abc123merge", goal.MergeCommitHash);
    }

    /// <summary>
    /// Verifies that <see cref="GoalUpdateMetadata.MergeCommitHash"/> being null
    /// does not overwrite an existing hash on the goal.
    /// </summary>
    [Fact]
    public async Task UpdateGoalStatusAsync_NullMergeCommitHash_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTempYaml(
            """
            goals:
              - id: hash-noop-test
                description: Test null hash
                merge_commit_hash: existing-hash
            """);

        var source = new FileGoalSource(path);
        // Passing null MergeCommitHash must not overwrite the existing value
        await source.UpdateGoalStatusAsync("hash-noop-test", GoalStatus.Completed, new GoalUpdateMetadata
        {
            MergeCommitHash = null,
        }, ct);

        var goals = await source.ReadGoalsAsync(ct);
        var goal = Assert.Single(goals);
        Assert.Equal("existing-hash", goal.MergeCommitHash);
    }

    [Fact]
    public async Task ReadGoalsAsync_WithScope_ParsesCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTempYaml(
            """
            goals:
              - id: scope-patch
                description: Patch goal
                scope: patch
              - id: scope-feature
                description: Feature goal
                scope: feature
              - id: scope-breaking
                description: Breaking goal
                scope: breaking
              - id: scope-missing
                description: No scope field
            """);

        var source = new FileGoalSource(path);
        var goals = await source.ReadGoalsAsync(ct);

        Assert.Equal(4, goals.Count);
        Assert.Equal(GoalScope.Patch, goals.First(g => g.Id == "scope-patch").Scope);
        Assert.Equal(GoalScope.Feature, goals.First(g => g.Id == "scope-feature").Scope);
        Assert.Equal(GoalScope.Breaking, goals.First(g => g.Id == "scope-breaking").Scope);
        Assert.Equal(GoalScope.Patch, goals.First(g => g.Id == "scope-missing").Scope);
    }

    [Fact]
    public async Task WriteGoalsAsync_NonPatchScope_WritesFieldToYaml()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTempYaml(
            """
            goals:
              - id: write-scope-test
                description: Feature goal
                scope: feature
            """);

        var source = new FileGoalSource(path);
        // Trigger a write by updating status
        await source.UpdateGoalStatusAsync("write-scope-test", GoalStatus.Completed, null, ct);

        var yaml = await File.ReadAllTextAsync(path, ct);
        Assert.Contains("scope: feature", yaml);
    }

    [Fact]
    public async Task WriteGoalsAsync_PatchScope_OmitsFieldFromYaml()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTempYaml(
            """
            goals:
              - id: patch-scope-test
                description: Patch goal
            """);

        var source = new FileGoalSource(path);
        await source.UpdateGoalStatusAsync("patch-scope-test", GoalStatus.Completed, null, ct);

        var yaml = await File.ReadAllTextAsync(path, ct);
        // Patch is the default; it should be omitted (null) to keep YAML clean
        Assert.DoesNotContain("scope: patch", yaml);
    }

    /// <summary>
    /// Verifies that <c>review_status</c> round-trips through YAML serialization
    /// for all non-default values (Approved, Pending, NeedsChanges).
    /// </summary>
    [Fact]
    public async Task WriteGoalsAsync_ReviewStatus_RoundTripsThroughYaml()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTempYaml(
            """
            goals:
              - id: review-approved
                description: Approved goal
                review_status: approved
              - id: review-pending
                description: Pending goal
                review_status: pending
              - id: review-needs-changes
                description: NeedsChanges goal
                review_status: needschanges
            """);

        var source = new FileGoalSource(path);

        // Read goals and verify review_status parsed correctly
        var goals = await source.ReadGoalsAsync(ct);
        Assert.Equal(3, goals.Count);
        Assert.Equal(ReviewStatus.Approved, goals.First(g => g.Id == "review-approved").ReviewStatus);
        Assert.Equal(ReviewStatus.Pending, goals.First(g => g.Id == "review-pending").ReviewStatus);
        Assert.Equal(ReviewStatus.NeedsChanges, goals.First(g => g.Id == "review-needs-changes").ReviewStatus);

        // Trigger a write by updating status, then re-read to verify round-trip
        await source.UpdateGoalStatusAsync("review-approved", GoalStatus.Completed, null, ct);
        await source.UpdateGoalStatusAsync("review-pending", GoalStatus.Completed, null, ct);
        await source.UpdateGoalStatusAsync("review-needs-changes", GoalStatus.Completed, null, ct);

        // Verify the YAML contains the review_status fields
        var yaml = await File.ReadAllTextAsync(path, ct);
        Assert.Contains("review_status: approved", yaml);
        Assert.Contains("review_status: pending", yaml);
        Assert.Contains("review_status: needschanges", yaml);

        // Re-read and verify values survived the write round-trip
        var source2 = new FileGoalSource(path);
        var goals2 = await source2.ReadGoalsAsync(ct);
        Assert.Equal(3, goals2.Count);
        Assert.Equal(ReviewStatus.Approved, goals2.First(g => g.Id == "review-approved").ReviewStatus);
        Assert.Equal(ReviewStatus.Pending, goals2.First(g => g.Id == "review-pending").ReviewStatus);
        Assert.Equal(ReviewStatus.NeedsChanges, goals2.First(g => g.Id == "review-needs-changes").ReviewStatus);
    }

    /// <summary>
    /// Verifies that a goal with <c>ReviewStatus.None</c> omits the
    /// <c>review_status</c> field from YAML, and that reading YAML without a
    /// <c>review_status</c> field defaults to <c>ReviewStatus.None</c>.
    /// </summary>
    [Fact]
    public async Task WriteGoalsAsync_ReviewStatusNone_OmitsFieldFromYaml()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteTempYaml(
            """
            goals:
              - id: review-none
                description: Goal with no review status
            """);

        var source = new FileGoalSource(path);

        // Read: should default to None when field is absent
        var goals = await source.ReadGoalsAsync(ct);
        var goal = Assert.Single(goals);
        Assert.Equal(ReviewStatus.None, goal.ReviewStatus);

        // Trigger a write by updating status
        await source.UpdateGoalStatusAsync("review-none", GoalStatus.Completed, null, ct);

        // Verify the YAML does NOT contain review_status (None is the default, omitted)
        var yaml = await File.ReadAllTextAsync(path, ct);
        Assert.DoesNotContain("review_status", yaml);

        // Re-read and verify it still defaults to None
        var source2 = new FileGoalSource(path);
        var goals2 = await source2.ReadGoalsAsync(ct);
        var goal2 = Assert.Single(goals2);
        Assert.Equal(ReviewStatus.None, goal2.ReviewStatus);
    }
}
