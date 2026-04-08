using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public sealed class GoalPipelinePhaseLogTests
{
    private static Goal CreateGoal(string id = "goal-1") =>
        new() { Id = id, Description = "Test goal", RepositoryNames = ["repo"] };

    private static GoalPipeline CreatePipeline(string id = "goal-1") =>
        new(CreateGoal(id));

    [Fact]
    public void PhaseLog_StartsEmpty_OnFreshPipeline()
    {
        var pipeline = CreatePipeline();

        Assert.Empty(pipeline.PhaseLog);
    }

    [Fact]
    public void CurrentPhaseEntry_IsNull_WhenPhaseLogIsEmpty()
    {
        var pipeline = CreatePipeline();

        Assert.Null(pipeline.CurrentPhaseEntry);
    }

    [Fact]
    public void PhaseLog_AppendingEntries_AccumulatesInOrder()
    {
        var pipeline = CreatePipeline();

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
        });

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
        });

        Assert.Equal(2, pipeline.PhaseLog.Count);
        Assert.Equal(GoalPhase.Coding, pipeline.PhaseLog[0].Name);
        Assert.Equal(GoalPhase.Testing, pipeline.PhaseLog[1].Name);
    }

    [Fact]
    public void CurrentPhaseEntry_ReturnsLastEntry()
    {
        var pipeline = CreatePipeline();

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Occurrence = 1,
        });

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Fail,
            Occurrence = 1,
        });

        Assert.NotNull(pipeline.CurrentPhaseEntry);
        Assert.Equal(GoalPhase.Testing, pipeline.CurrentPhaseEntry!.Name);
        Assert.Equal(PhaseOutcome.Fail, pipeline.CurrentPhaseEntry.Result);
    }

    [Fact]
    public void StartedAt_IsSet_OnNewEntry_CompletedAt_StartsNull()
    {
        var before = DateTime.UtcNow;
        var entry = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            StartedAt = DateTime.UtcNow,
        };
        var after = DateTime.UtcNow;

        Assert.NotNull(entry.StartedAt);
        Assert.True(entry.StartedAt >= before && entry.StartedAt <= after);
        Assert.Null(entry.CompletedAt);
    }

    [Fact]
    public void DurationSeconds_ComputedFromTimestamps_WhenBothSet()
    {
        var start = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2025, 1, 1, 10, 1, 30, DateTimeKind.Utc);

        var entry = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            StartedAt = start,
            CompletedAt = end,
        };

        // 90 seconds
        Assert.Equal(90.0, entry.DurationSeconds);
    }

    [Fact]
    public void DurationSeconds_UsesBackingField_WhenTimestampsNotSet()
    {
        var entry = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            DurationSeconds = 42.5,
        };

        Assert.Null(entry.StartedAt);
        Assert.Null(entry.CompletedAt);
        Assert.Equal(42.5, entry.DurationSeconds);
    }

    [Fact]
    public void DurationSeconds_TimestampsOverrideBackingField()
    {
        var start = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2025, 1, 1, 10, 0, 10, DateTimeKind.Utc);

        var entry = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            DurationSeconds = 999.0, // backing field set
            StartedAt = start,
            CompletedAt = end,
        };

        // Timestamps win: 10 seconds
        Assert.Equal(10.0, entry.DurationSeconds);
    }

    [Fact]
    public void DurationSeconds_UsesBackingField_WhenOnlyStartedAtSet()
    {
        var entry = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            DurationSeconds = 5.0,
            StartedAt = DateTime.UtcNow,
        };

        // CompletedAt is null, so backing field is returned
        Assert.Equal(5.0, entry.DurationSeconds);
    }

    [Fact]
    public void Verdict_IsSetable()
    {
        var entry = new PhaseResult
        {
            Name = GoalPhase.Review,
            Result = PhaseOutcome.Pass,
            Verdict = "APPROVE",
        };

        Assert.Equal("APPROVE", entry.Verdict);
    }

    [Fact]
    public void PhaseLog_RestoredFromSnapshot()
    {
        var snapshot = new PipelineSnapshot
        {
            GoalId = "g1",
            Description = "Snapshot test",
            Goal = CreateGoal("g1"),
            PhaseLog =
            [
                new PhaseResult
                {
                    Name = GoalPhase.Coding,
                    Result = PhaseOutcome.Pass,
                    Occurrence = 1,
                    StartedAt = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2025, 1, 1, 10, 5, 0, DateTimeKind.Utc),
                    Verdict = "PASS",
                },
                new PhaseResult
                {
                    Name = GoalPhase.Testing,
                    Result = PhaseOutcome.Fail,
                    Occurrence = 1,
                    StartedAt = new DateTime(2025, 1, 1, 10, 5, 0, DateTimeKind.Utc),
                    Verdict = "FAIL",
                },
            ],
            Conversation = [],
            TaskMappings = [],
        };

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal(2, pipeline.PhaseLog.Count);
        Assert.Equal(GoalPhase.Coding, pipeline.PhaseLog[0].Name);
        Assert.Equal("PASS", pipeline.PhaseLog[0].Verdict);
        Assert.Equal(300.0, pipeline.PhaseLog[0].DurationSeconds); // 5 minutes
        Assert.Equal(GoalPhase.Testing, pipeline.PhaseLog[1].Name);
        Assert.Equal("FAIL", pipeline.PhaseLog[1].Verdict);
        Assert.Equal(PhaseOutcome.Fail, pipeline.PhaseLog[1].Result);
    }
}

public sealed class PipelineStorePhaseLogTests : IAsyncDisposable
{
    private readonly PipelineStore _store;

    public PipelineStorePhaseLogTests()
    {
        _store = new PipelineStore(":memory:", NullLogger<PipelineStore>.Instance);
    }

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static Goal CreateGoal(string id = "goal-1") =>
        new() { Id = id, Description = "Test goal", RepositoryNames = ["repo"] };

    private static GoalPipeline CreatePipeline(string id = "goal-1") =>
        new(CreateGoal(id));

    [Fact]
    public void PhaseLog_RoundTrips_ThroughSqlite()
    {
        var pipeline = CreatePipeline("g-phaselog");
        pipeline.AdvanceTo(GoalPhase.Coding);

        var start1 = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var end1 = new DateTime(2025, 6, 15, 10, 5, 30, DateTimeKind.Utc);
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Occurrence = 1,
            StartedAt = start1,
            CompletedAt = end1,
            Verdict = "PASS",
            WorkerOutput = "Implemented feature X",
        });

        var start2 = new DateTime(2025, 6, 15, 10, 5, 30, DateTimeKind.Utc);
        var end2 = new DateTime(2025, 6, 15, 10, 8, 0, DateTimeKind.Utc);
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Fail,
            Occurrence = 1,
            StartedAt = start2,
            CompletedAt = end2,
            Verdict = "FAIL",
        });

        _store.SavePipeline(pipeline);
        var snapshots = _store.LoadActivePipelines();

        var snap = Assert.Single(snapshots);
        Assert.Equal(2, snap.PhaseLog.Count);

        // Verify first entry
        Assert.Equal(GoalPhase.Coding, snap.PhaseLog[0].Name);
        Assert.Equal(PhaseOutcome.Pass, snap.PhaseLog[0].Result);
        Assert.Equal(1, snap.PhaseLog[0].Occurrence);
        Assert.Equal("PASS", snap.PhaseLog[0].Verdict);
        Assert.Equal("Implemented feature X", snap.PhaseLog[0].WorkerOutput);
        Assert.NotNull(snap.PhaseLog[0].StartedAt);
        Assert.NotNull(snap.PhaseLog[0].CompletedAt);
        Assert.Equal(start1, snap.PhaseLog[0].StartedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(end1, snap.PhaseLog[0].CompletedAt!.Value, TimeSpan.FromMilliseconds(1));

        // Verify second entry
        Assert.Equal(GoalPhase.Testing, snap.PhaseLog[1].Name);
        Assert.Equal(PhaseOutcome.Fail, snap.PhaseLog[1].Result);
        Assert.Equal("FAIL", snap.PhaseLog[1].Verdict);

        // Verify restored pipeline
        var restored = new GoalPipeline(snap);
        Assert.Equal(2, restored.PhaseLog.Count);
        Assert.Equal(GoalPhase.Coding, restored.PhaseLog[0].Name);
        Assert.Equal(GoalPhase.Testing, restored.PhaseLog[1].Name);
    }

    [Fact]
    public void PhaseLog_Empty_RoundTrips_AsEmptyList()
    {
        var pipeline = CreatePipeline("g-empty-phaselog");

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Empty(snap.PhaseLog);
    }

    [Fact]
    public void PhaseLog_DurationSeconds_ComputedFromTimestamps_AfterRoundTrip()
    {
        var pipeline = CreatePipeline("g-duration");
        pipeline.AdvanceTo(GoalPhase.Coding);

        var start = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2025, 6, 15, 10, 2, 0, DateTimeKind.Utc);
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            StartedAt = start,
            CompletedAt = end,
            Verdict = "PASS",
        });

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());
        var restored = new GoalPipeline(snap);

        // DurationSeconds should be computed from timestamps
        Assert.Equal(120.0, restored.PhaseLog[0].DurationSeconds, 0.1);
    }
}
