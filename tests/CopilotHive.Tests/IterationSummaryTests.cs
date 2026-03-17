using CopilotHive.Goals;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for <see cref="IterationSummary"/> structure, YAML serialisation, and round-tripping
/// via <see cref="FileGoalSource"/>.
/// </summary>
public sealed class IterationSummaryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"itersum-{Guid.NewGuid():N}");

    public IterationSummaryTests() => Directory.CreateDirectory(_tempDir);

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

    // ── Model structure ──────────────────────────────────────────────────────

    [Fact]
    public void IterationSummary_HasExpectedProperties()
    {
        var summary = new IterationSummary
        {
            Iteration = 2,
            Phases =
            [
                new PhaseResult { Name = "Coding",  Result = "pass", DurationSeconds = 90.0 },
                new PhaseResult { Name = "Testing", Result = "fail", DurationSeconds = 12.5 },
            ],
            TestCounts = new TestCounts { Total = 20, Passed = 18, Failed = 2 },
            ReviewVerdict = "approve",
            Notes = ["improver skipped: timeout"],
        };

        Assert.Equal(2, summary.Iteration);
        Assert.Equal(2, summary.Phases.Count);
        Assert.Equal("Coding",  summary.Phases[0].Name);
        Assert.Equal("pass",    summary.Phases[0].Result);
        Assert.Equal(90.0,      summary.Phases[0].DurationSeconds);
        Assert.Equal("fail",    summary.Phases[1].Result);
        Assert.NotNull(summary.TestCounts);
        Assert.Equal(20, summary.TestCounts.Total);
        Assert.Equal(18, summary.TestCounts.Passed);
        Assert.Equal(2,  summary.TestCounts.Failed);
        Assert.Equal("approve", summary.ReviewVerdict);
        Assert.Single(summary.Notes);
    }

    // ── GoalFileEntry round-trip ─────────────────────────────────────────────

    [Fact]
    public void GoalFileEntry_IterationSummaries_RoundTripsViaYaml()
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
            Id = "round-trip-goal",
            Status = "completed",
            IterationSummaries =
            [
                new FileGoalSource.IterationSummaryEntry
                {
                    Iteration = 1,
                    Phases =
                    [
                        new FileGoalSource.PhaseResultEntry { Name = "Coding",  Result = "pass", DurationSeconds = 60.0 },
                        new FileGoalSource.PhaseResultEntry { Name = "Testing", Result = "pass", DurationSeconds = 20.0 },
                    ],
                    TestCounts = new FileGoalSource.TestCountEntry { Total = 10, Passed = 10, Failed = 0 },
                    ReviewVerdict = "approve",
                    Notes = ["all good"],
                },
            ],
        };

        var yaml = serializer.Serialize(entry);
        var roundTripped = deserializer.Deserialize<FileGoalSource.GoalFileEntry>(yaml);

        Assert.NotNull(roundTripped.IterationSummaries);
        Assert.Single(roundTripped.IterationSummaries);

        var s = roundTripped.IterationSummaries[0];
        Assert.Equal(1, s.Iteration);
        Assert.Equal(2, s.Phases!.Count);
        Assert.Equal("Coding",  s.Phases[0].Name);
        Assert.Equal("pass",    s.Phases[0].Result);
        Assert.Equal(60.0,      s.Phases[0].DurationSeconds);
        Assert.NotNull(s.TestCounts);
        Assert.Equal(10, s.TestCounts.Total);
        Assert.Equal("approve", s.ReviewVerdict);
        Assert.Equal("all good", s.Notes![0]);
    }

    // ── FileGoalSource integration ───────────────────────────────────────────

    [Fact]
    public async Task FileGoalSource_WritesIterationSummaryToYaml()
    {
        var path = WriteTempYaml("""
            goals:
              - id: my-goal
                description: "Test goal"
            """);

        var source = new FileGoalSource(path);
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = "Coding",  Result = "pass", DurationSeconds = 100.0 },
                new PhaseResult { Name = "Testing", Result = "pass", DurationSeconds = 25.0  },
            ],
            TestCounts = new TestCounts { Total = 5, Passed = 5, Failed = 0 },
            ReviewVerdict = "approve",
            Notes = [],
        };

        var metadata = new GoalUpdateMetadata
        {
            CompletedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Iterations = 1,
            IterationSummary = summary,
        };

        await source.UpdateGoalStatusAsync("my-goal", GoalStatus.Completed, metadata);

        var yaml = await File.ReadAllTextAsync(path);
        Assert.Contains("iteration_summaries", yaml);
        Assert.Contains("iteration: 1", yaml);
        Assert.Contains("Coding", yaml);
        Assert.Contains("review_verdict: approve", yaml);
        Assert.Contains("test_counts", yaml);
    }

    [Fact]
    public async Task FileGoalSource_ReadsIterationSummariesFromYaml()
    {
        // Write a goal with a summary, then read it back to verify round-trip
        var path = WriteTempYaml("""
            goals:
              - id: goal-with-summaries
                description: "Goal that already has summaries"
                status: in_progress
            """);

        var source = new FileGoalSource(path);

        // Write a summary via UpdateGoalStatusAsync
        var metadata = new GoalUpdateMetadata
        {
            CompletedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Iterations = 1,
            IterationSummary = new IterationSummary
            {
                Iteration = 1,
                Phases =
                [
                    new PhaseResult { Name = "Coding",  Result = "pass", DurationSeconds = 45.0 },
                    new PhaseResult { Name = "Testing", Result = "pass", DurationSeconds = 10.0 },
                ],
                TestCounts = new TestCounts { Total = 8, Passed = 7, Failed = 1 },
                ReviewVerdict = "reject",
                Notes = ["improver was skipped"],
            },
        };
        await source.UpdateGoalStatusAsync("goal-with-summaries", GoalStatus.Completed, metadata);

        // Now read back and verify
        var goals = await source.ReadGoalsAsync();

        Assert.Single(goals);
        var goal = goals[0];
        Assert.Single(goal.IterationSummaries);

        var s = goal.IterationSummaries[0];
        Assert.Equal(1, s.Iteration);
        Assert.Equal(2, s.Phases.Count);
        Assert.Equal("Coding", s.Phases[0].Name);
        Assert.Equal("pass",   s.Phases[0].Result);
        Assert.Equal(45.0,     s.Phases[0].DurationSeconds);
        Assert.NotNull(s.TestCounts);
        Assert.Equal(8, s.TestCounts.Total);
        Assert.Equal(7, s.TestCounts.Passed);
        Assert.Equal(1, s.TestCounts.Failed);
        Assert.Equal("reject", s.ReviewVerdict);
        Assert.Single(s.Notes);
        Assert.Contains("improver", s.Notes[0]);
    }

    [Fact]
    public async Task FileGoalSource_AppendsIterationSummary_WhenExistingEntriesPresent()
    {
        var path = WriteTempYaml("""
            goals:
              - id: multi-iter-goal
                description: "Multi iteration goal"
                status: in_progress
                iteration_summaries:
                  - iteration: 1
                    phases:
                      - name: Coding
                        result: pass
                        duration_seconds: 30.0
            """);

        var source = new FileGoalSource(path);
        var metadata = new GoalUpdateMetadata
        {
            CompletedAt = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc),
            Iterations = 2,
            IterationSummary = new IterationSummary
            {
                Iteration = 2,
                Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 55.0 }],
            },
        };

        await source.UpdateGoalStatusAsync("multi-iter-goal", GoalStatus.Completed, metadata);

        var goals = await source.ReadGoalsAsync();
        Assert.Single(goals);
        Assert.Equal(2, goals[0].IterationSummaries.Count);
        Assert.Equal(1, goals[0].IterationSummaries[0].Iteration);
        Assert.Equal(2, goals[0].IterationSummaries[1].Iteration);
    }

    [Fact]
    public async Task FileGoalSource_NoIterationSummary_DoesNotWriteIterationSummariesKey()
    {
        var path = WriteTempYaml("""
            goals:
              - id: plain-goal
                description: "No summaries"
            """);

        var source = new FileGoalSource(path);
        var metadata = new GoalUpdateMetadata { CompletedAt = DateTime.UtcNow };

        await source.UpdateGoalStatusAsync("plain-goal", GoalStatus.Completed, metadata);

        var yaml = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("iteration_summaries", yaml);
    }

    [Fact]
    public void IterationSummary_NullTestCounts_WhenNoTestsRan()
    {
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 80.0 }],
            TestCounts = null,
            ReviewVerdict = null,
        };

        Assert.Null(summary.TestCounts);
        Assert.Null(summary.ReviewVerdict);
    }
}
