using System.Text.Json;
using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Goals;

/// <summary>
/// Integration tests for JSON round-trip compatibility after the PhaseResult refactor
/// (string → GoalPhase/PhaseOutcome enums). These tests verify that:
/// 1. New enum-typed PhaseResult persists correctly through SqliteGoalStore
/// 2. Old JSON data with string values deserializes correctly into the new enum types
/// 3. New serialization produces the same JSON format as before the refactor
/// 4. IterationPlan with GoalPhase values round-trips through PipelineStore
/// 5. FileGoalSource YAML mapping between string DTO and enum-typed PhaseResult works
/// </summary>
public sealed class PhaseResultSerializationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteGoalStore _store;

    public PhaseResultSerializationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _store = new SqliteGoalStore(_connection, NullLogger<SqliteGoalStore>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    /// <summary>
    /// JsonSerializerOptions matching SqliteGoalStore's internal configuration.
    /// </summary>
    private static readonly JsonSerializerOptions SqliteJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ─── Test 1: SqliteGoalStore persistence round-trip ────────────────────────

    [Fact]
    public async Task SqliteGoalStore_RoundTrip_PreservesEnumValues()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal
        {
            Id = "enum-roundtrip-test",
            Description = "Test goal for enum round-trip",
            Status = GoalStatus.InProgress,
        };
        await _store.CreateGoalAsync(goal, ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 45.2 },
                new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Fail, DurationSeconds = 30.0 },
                new PhaseResult { Name = GoalPhase.Improve, Result = PhaseOutcome.Skip, DurationSeconds = 0.0 },
                new PhaseResult { Name = GoalPhase.Review, Result = PhaseOutcome.Pass, DurationSeconds = 15.5 },
            ],
            TestCounts = new TestCounts { Total = 20, Passed = 18, Failed = 2 },
            ReviewVerdict = "approve",
        };
        await _store.AddIterationAsync(goal.Id, summary, ct);

        // Read back through the public API
        var loadedGoal = await _store.GetGoalAsync(goal.Id, ct);
        Assert.NotNull(loadedGoal);
        var loadedSummary = Assert.Single(loadedGoal.IterationSummaries);

        Assert.Equal(4, loadedSummary.Phases.Count);

        // Verify each PhaseResult preserved its enum values
        Assert.Equal(GoalPhase.Coding, loadedSummary.Phases[0].Name);
        Assert.Equal(PhaseOutcome.Pass, loadedSummary.Phases[0].Result);
        Assert.Equal(45.2, loadedSummary.Phases[0].DurationSeconds);

        Assert.Equal(GoalPhase.Testing, loadedSummary.Phases[1].Name);
        Assert.Equal(PhaseOutcome.Fail, loadedSummary.Phases[1].Result);
        Assert.Equal(30.0, loadedSummary.Phases[1].DurationSeconds);

        Assert.Equal(GoalPhase.Improve, loadedSummary.Phases[2].Name);
        Assert.Equal(PhaseOutcome.Skip, loadedSummary.Phases[2].Result);
        Assert.Equal(0.0, loadedSummary.Phases[2].DurationSeconds);

        Assert.Equal(GoalPhase.Review, loadedSummary.Phases[3].Name);
        Assert.Equal(PhaseOutcome.Pass, loadedSummary.Phases[3].Result);
        Assert.Equal(15.5, loadedSummary.Phases[3].DurationSeconds);
    }

    // ─── Test 2: Backward compatibility with old JSON data ──────────────────────

    [Fact]
    public void OldJsonFormat_NameString_DeserializesToGoalPhase()
    {
        // This is the JSON format that was stored BEFORE the refactor.
        // "name":"Coding" was a string, now it must deserialize to GoalPhase.Coding
        var oldJson = """{"name":"Coding","result":"pass","durationSeconds":45.2}""";

        var phaseResult = JsonSerializer.Deserialize<PhaseResult>(oldJson, SqliteJsonOptions);

        Assert.NotNull(phaseResult);
        Assert.Equal(GoalPhase.Coding, phaseResult.Name);
        Assert.Equal(PhaseOutcome.Pass, phaseResult.Result);
        Assert.Equal(45.2, phaseResult.DurationSeconds);
    }

    [Fact]
    public void OldJsonFormat_ResultPass_DeserializesToPhaseOutcomePass()
    {
        var oldJson = """{"name":"Testing","result":"pass","durationSeconds":10.0}""";
        var phaseResult = JsonSerializer.Deserialize<PhaseResult>(oldJson, SqliteJsonOptions);

        Assert.NotNull(phaseResult);
        Assert.Equal(GoalPhase.Testing, phaseResult.Name);
        Assert.Equal(PhaseOutcome.Pass, phaseResult.Result);
    }

    [Fact]
    public void OldJsonFormat_ResultFail_DeserializesToPhaseOutcomeFail()
    {
        var oldJson = """{"name":"Review","result":"fail","durationSeconds":5.0}""";
        var phaseResult = JsonSerializer.Deserialize<PhaseResult>(oldJson, SqliteJsonOptions);

        Assert.NotNull(phaseResult);
        Assert.Equal(GoalPhase.Review, phaseResult.Name);
        Assert.Equal(PhaseOutcome.Fail, phaseResult.Result);
    }

    [Fact]
    public void OldJsonFormat_ResultSkip_DeserializesToPhaseOutcomeSkip()
    {
        var oldJson = """{"name":"Improve","result":"skip","durationSeconds":0.0}""";
        var phaseResult = JsonSerializer.Deserialize<PhaseResult>(oldJson, SqliteJsonOptions);

        Assert.NotNull(phaseResult);
        Assert.Equal(GoalPhase.Improve, phaseResult.Name);
        Assert.Equal(PhaseOutcome.Skip, phaseResult.Result);
    }

    [Fact]
    public void OldJsonFormat_AllPhaseNames_DeserializeCorrectly()
    {
        // Test every GoalPhase value that could appear in old stored data
        var testCases = new (string jsonName, GoalPhase expected)[]
        {
            ("Planning", GoalPhase.Planning),
            ("Coding", GoalPhase.Coding),
            ("Review", GoalPhase.Review),
            ("Testing", GoalPhase.Testing),
            ("DocWriting", GoalPhase.DocWriting),
            ("Improve", GoalPhase.Improve),
            ("Merging", GoalPhase.Merging),
            ("Done", GoalPhase.Done),
            ("Failed", GoalPhase.Failed),
        };

        foreach (var (jsonName, expected) in testCases)
        {
            var oldJson = $"{{\"name\":\"{jsonName}\",\"result\":\"pass\",\"durationSeconds\":1.0}}";
            var phaseResult = JsonSerializer.Deserialize<PhaseResult>(oldJson, SqliteJsonOptions);
            Assert.NotNull(phaseResult);
            Assert.Equal(expected, phaseResult.Name);
        }
    }

    [Fact]
    public void OldJsonFormat_PhaseResultList_DeserializesCorrectly()
    {
        // Test a full list of PhaseResult objects in old format (as stored in phases_json column)
        var oldJson = """
            [
                {"name":"Coding","result":"pass","durationSeconds":45.2},
                {"name":"Testing","result":"fail","durationSeconds":30.0},
                {"name":"Improve","result":"skip","durationSeconds":0.0}
            ]
            """;

        var phases = JsonSerializer.Deserialize<List<PhaseResult>>(oldJson, SqliteJsonOptions);

        Assert.NotNull(phases);
        Assert.Equal(3, phases.Count);

        Assert.Equal(GoalPhase.Coding, phases[0].Name);
        Assert.Equal(PhaseOutcome.Pass, phases[0].Result);
        Assert.Equal(45.2, phases[0].DurationSeconds);

        Assert.Equal(GoalPhase.Testing, phases[1].Name);
        Assert.Equal(PhaseOutcome.Fail, phases[1].Result);
        Assert.Equal(30.0, phases[1].DurationSeconds);

        Assert.Equal(GoalPhase.Improve, phases[2].Name);
        Assert.Equal(PhaseOutcome.Skip, phases[2].Result);
        Assert.Equal(0.0, phases[2].DurationSeconds);
    }

    // ─── Test 3: New serialization produces expected JSON format ────────────────

    [Fact]
    public void NewSerialization_GoalPhase_ProducesPascalCaseString()
    {
        // GoalPhase uses JsonStringEnumConverter (no naming policy) so it should
        // serialize as PascalCase: "Coding", "Testing", etc.
        var phaseResult = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            DurationSeconds = 45.2,
        };

        var json = JsonSerializer.Serialize(phaseResult, SqliteJsonOptions);

        Assert.Contains("\"name\":\"Coding\"", json);
    }

    [Fact]
    public void NewSerialization_PhaseOutcomePass_ProducesLowercaseString()
    {
        // PhaseOutcome uses CamelCasePhaseOutcomeConverter so it should
        // serialize as camelCase: "pass", "fail", "skip"
        var phaseResult = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            DurationSeconds = 45.2,
        };

        var json = JsonSerializer.Serialize(phaseResult, SqliteJsonOptions);

        Assert.Contains("\"result\":\"pass\"", json);
    }

    [Fact]
    public void NewSerialization_PhaseOutcomeFail_ProducesLowercaseString()
    {
        var phaseResult = new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Fail,
            DurationSeconds = 10.0,
        };

        var json = JsonSerializer.Serialize(phaseResult, SqliteJsonOptions);

        Assert.Contains("\"result\":\"fail\"", json);
    }

    [Fact]
    public void NewSerialization_PhaseOutcomeSkip_ProducesLowercaseString()
    {
        var phaseResult = new PhaseResult
        {
            Name = GoalPhase.Improve,
            Result = PhaseOutcome.Skip,
            DurationSeconds = 0.0,
        };

        var json = JsonSerializer.Serialize(phaseResult, SqliteJsonOptions);

        Assert.Contains("\"result\":\"skip\"", json);
    }

    [Fact]
    public void NewSerialization_FullPhaseResult_MatchesOldFormat()
    {
        // Verify that the complete JSON output matches what would have been
        // produced before the refactor (backward compatibility)
        var phaseResult = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            DurationSeconds = 45.2,
        };

        var json = JsonSerializer.Serialize(phaseResult, SqliteJsonOptions);

        // The JSON should contain the same key-value pairs as before the refactor
        Assert.Contains("\"name\":\"Coding\"", json);
        Assert.Contains("\"result\":\"pass\"", json);
        Assert.Contains("\"durationSeconds\":45.2", json);
    }

    // ─── Test 4: IterationPlan GoalPhase serialization ─────────────────────────

    [Fact]
    public void IterationPlan_GoalPhases_RoundTripThroughPipelineStore()
    {
        var store = new PipelineStore(":memory:", NullLogger<PipelineStore>.Instance);

        var goal = new Goal
        {
            Id = "pipeline-phase-test",
            Description = "Test goal",
            Status = GoalStatus.InProgress,
        };

        var pipeline = new GoalPipeline(goal, 3);
        pipeline.SetPlan(new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Coding],
        });

        store.SavePipeline(pipeline);

        // Load back
        var snapshots = store.LoadActivePipelines();
        var snap = Assert.Single(snapshots);
        Assert.NotNull(snap.Plan);
        Assert.Equal(4, snap.Plan.Phases.Count);
        Assert.Equal(GoalPhase.Coding, snap.Plan.Phases[0]);
        Assert.Equal(GoalPhase.Testing, snap.Plan.Phases[1]);
        Assert.Equal(GoalPhase.Review, snap.Plan.Phases[2]);
        Assert.Equal(GoalPhase.Coding, snap.Plan.Phases[3]);
    }

    [Fact]
    public void IterationPlan_SerializedGoalPhases_AreStringValues()
    {
        // Verify that GoalPhase values in IterationPlan serialize as strings,
        // not integers — this is critical for backward compatibility
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing],
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var json = JsonSerializer.Serialize(plan, jsonOptions);

        // Should contain string "Coding" and "Testing", not numeric values like 0, 1
        Assert.Contains("\"Coding\"", json);
        Assert.Contains("\"Testing\"", json);
        Assert.DoesNotContain("\"phases\":[0,", json);
        Assert.DoesNotContain("\"phases\":[1,", json);
    }

    // ─── Test 5: FileGoalSource YAML round-trip ───────────────────────────────

    [Fact]
    public void FileGoalSource_PhaseResultEntry_MapsToPhaseResultCorrectly()
    {
        // Simulate what FileGoalSource does: map from string DTO (PhaseResultEntry)
        // to enum-typed PhaseResult
        var entry = new PhaseResultEntry
        {
            Name = "Coding",
            Result = "pass",
            DurationSeconds = 30.0,
        };

        // Replicate the mapping logic from FileGoalSource.MapIterationSummary
        var phaseResult = new PhaseResult
        {
            Name = Enum.Parse<GoalPhase>(entry.Name ?? string.Empty),
            Result = Enum.Parse<PhaseOutcome>(entry.Result ?? throw new InvalidOperationException("Result must not be null"), ignoreCase: true),
            DurationSeconds = entry.DurationSeconds,
        };

        Assert.Equal(GoalPhase.Coding, phaseResult.Name);
        Assert.Equal(PhaseOutcome.Pass, phaseResult.Result);
        Assert.Equal(30.0, phaseResult.DurationSeconds);
    }

    [Fact]
    public void FileGoalSource_PhaseResult_MapsBackToPhaseResultEntryCorrectly()
    {
        // Simulate the reverse mapping: enum PhaseResult → string PhaseResultEntry
        var phaseResult = new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Fail,
            DurationSeconds = 15.0,
        };

        // Replicate the mapping logic from FileGoalSource.MapIterationSummaryEntry
        var entry = new PhaseResultEntry
        {
            Name = phaseResult.Name.ToString(),
            Result = phaseResult.Result.ToString().ToLowerInvariant(),
            DurationSeconds = phaseResult.DurationSeconds,
        };

        Assert.Equal("Testing", entry.Name);
        Assert.Equal("fail", entry.Result);
        Assert.Equal(15.0, entry.DurationSeconds);
    }

    [Fact]
    public void FileGoalSource_RoundTrip_AllPhaseOutcomes()
    {
        // Verify round-trip fidelity for all PhaseOutcome values
        var outcomes = new[] { PhaseOutcome.Pass, PhaseOutcome.Fail, PhaseOutcome.Skip };

        foreach (var outcome in outcomes)
        {
            var phaseResult = new PhaseResult
            {
                Name = GoalPhase.Coding,
                Result = outcome,
                DurationSeconds = 5.0,
            };

            // PhaseResult → PhaseResultEntry (string DTO for YAML)
            var entry = new PhaseResultEntry
            {
                Name = phaseResult.Name.ToString(),
                Result = phaseResult.Result.ToString().ToLowerInvariant(),
                DurationSeconds = phaseResult.DurationSeconds,
            };

            // PhaseResultEntry → PhaseResult (enum)
            var roundTripped = new PhaseResult
            {
                Name = Enum.Parse<GoalPhase>(entry.Name ?? string.Empty),
                Result = Enum.Parse<PhaseOutcome>(entry.Result ?? throw new InvalidOperationException(), ignoreCase: true),
                DurationSeconds = entry.DurationSeconds,
            };

            Assert.Equal(phaseResult.Name, roundTripped.Name);
            Assert.Equal(phaseResult.Result, roundTripped.Result);
            Assert.Equal(phaseResult.DurationSeconds, roundTripped.DurationSeconds);
        }
    }

    [Fact]
    public void FileGoalSource_RoundTrip_AllGoalPhases()
    {
        // Verify round-trip fidelity for all GoalPhase values that could appear in YAML
        var phases = new[]
        {
            GoalPhase.Planning,
            GoalPhase.Coding,
            GoalPhase.Review,
            GoalPhase.Testing,
            GoalPhase.DocWriting,
            GoalPhase.Improve,
            GoalPhase.Merging,
        };

        foreach (var goalPhase in phases)
        {
            var phaseResult = new PhaseResult
            {
                Name = goalPhase,
                Result = PhaseOutcome.Pass,
                DurationSeconds = 1.0,
            };

            // PhaseResult → PhaseResultEntry
            var entry = new PhaseResultEntry
            {
                Name = phaseResult.Name.ToString(),
                Result = phaseResult.Result.ToString().ToLowerInvariant(),
                DurationSeconds = phaseResult.DurationSeconds,
            };

            // PhaseResultEntry → PhaseResult
            var roundTripped = new PhaseResult
            {
                Name = Enum.Parse<GoalPhase>(entry.Name ?? string.Empty),
                Result = Enum.Parse<PhaseOutcome>(entry.Result ?? throw new InvalidOperationException(), ignoreCase: true),
                DurationSeconds = entry.DurationSeconds,
            };

            Assert.Equal(phaseResult.Name, roundTripped.Name);
            Assert.Equal(phaseResult.Result, roundTripped.Result);
        }
    }

    /// <summary>
    /// Mimics the internal PhaseResultEntry DTO from FileGoalSource for testing.
    /// </summary>
    private sealed class PhaseResultEntry
    {
        public string? Name { get; set; }
        public string? Result { get; set; }
        public double DurationSeconds { get; set; }
    }
}