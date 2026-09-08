using System.Text.Json;
using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Goals;

/// <summary>
/// Integration tests for JSON round-trip compatibility after the PhaseResult refactor
/// (string → GoalPhase/PhaseOutcome enums). These tests verify that:
/// 1. New enum-typed PhaseResult persists correctly through GoalStore
/// 2. Old JSON data with string values deserializes correctly into the new enum types
/// 3. New serialization produces the same JSON format as before the refactor
/// 4. IterationPlan with GoalPhase values round-trips through PipelineStore
/// 5. PhaseResult string-DTO mapping for removed YAML source compatibility
/// </summary>
public sealed class PhaseResultSerializationTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;

    public PhaseResultSerializationTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    /// <summary>
    /// JsonSerializerOptions matching GoalStore's internal configuration.
    /// </summary>
    private static readonly JsonSerializerOptions SqliteJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ─── Test 1: GoalStore persistence round-trip ────────────────────────

    [Fact]
    public async Task GoalStore_RoundTrip_PreservesEnumValues()
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
        var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);

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

    // ─── Test 5: String-DTO mapping round-trip ───────────────────────────────

    [Fact]
    public void PhaseResultEntry_MapsToPhaseResultCorrectly()
    {
        // Map from string DTO (PhaseResultEntry) to enum-typed PhaseResult
        var entry = new PhaseResultEntry
        {
            Name = "Coding",
            Result = "pass",
            DurationSeconds = 30.0,
        };

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
    public void PhaseResult_MapsBackToPhaseResultEntryCorrectly()
    {
        // Map from enum PhaseResult → string PhaseResultEntry
        var phaseResult = new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Fail,
            DurationSeconds = 15.0,
        };

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
    public void PhaseResultEntry_RoundTrip_AllPhaseOutcomes()
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

            // PhaseResult → PhaseResultEntry (string DTO)
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
    public void PhaseResultEntry_RoundTrip_AllGoalPhases()
    {
        // Verify round-trip fidelity for all GoalPhase values
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

    // ─── Test 6: Narratives — legacy compatibility and exact round-trip ───────

    /// <summary>
    /// Legacy JSON stored BEFORE the Narratives property existed must still load: the
    /// optional property stays <c>null</c> and every legacy field is preserved. Without
    /// backward compatibility this would fail the dashboard's stored-iteration reads.
    /// </summary>
    [Fact]
    public void OldJsonFormat_WithoutNarrativesProperty_DeserializesWithNullNarratives()
    {
        // The pre-slice format: no "narratives" key at all.
        var oldJson = """{"name":"Coding","result":"pass","durationSeconds":45.2,"workerOutput":"legacy output"}""";

        var phaseResult = JsonSerializer.Deserialize<PhaseResult>(oldJson, SqliteJsonOptions);

        Assert.NotNull(phaseResult);
        Assert.Null(phaseResult!.Narratives);
        Assert.Equal(GoalPhase.Coding, phaseResult.Name);
        Assert.Equal(PhaseOutcome.Pass, phaseResult.Result);
        Assert.Equal(45.2, phaseResult.DurationSeconds);
        Assert.Equal("legacy output", phaseResult.WorkerOutput);
    }

    /// <summary>
    /// A populated Narratives list round-trips through the exact JSON options GoalStore uses
    /// (camelCase): every NarrativeEntry field and the chronological ordering survive verbatim.
    /// </summary>
    [Fact]
    public void RoundTrip_PopulatedNarratives_PreservesExactFieldValuesAndOrdering()
    {
        // Timestamps with sub-second precision so a lossy round-trip is detectable.
        var t1 = new DateTime(2025, 1, 1, 10, 0, 0, 123, DateTimeKind.Utc);
        var t2 = new DateTime(2025, 1, 1, 10, 0, 5, 456, DateTimeKind.Utc);

        var phaseResult = new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Fail,
            DurationSeconds = 30.0,
            Narratives =
            [
                new NarrativeEntry
                {
                    Timestamp = t1,
                    WorkerId = "worker-early",
                    TaskId = "task-42",
                    Content = "First narrative\nwith a new line\tand a tab ...",
                },
                new NarrativeEntry
                {
                    Timestamp = t2,
                    WorkerId = "worker-late",
                    TaskId = "task-42",
                    // Long multi-line content with trailing whitespace that must survive verbatim.
                    Content = new string('x', 6_000) + "\nTRAILING-SPACE-END " ,
                },
            ],
        };

        var json = JsonSerializer.Serialize(phaseResult, SqliteJsonOptions);
        var roundTripped = JsonSerializer.Deserialize<PhaseResult>(json, SqliteJsonOptions);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped!.Narratives);
        Assert.Equal(2, roundTripped.Narratives!.Count);

        Assert.Equal(t1, roundTripped.Narratives[0].Timestamp);
        Assert.Equal("worker-early", roundTripped.Narratives[0].WorkerId);
        Assert.Equal("task-42", roundTripped.Narratives[0].TaskId);
        Assert.Equal("First narrative\nwith a new line\tand a tab ...", roundTripped.Narratives[0].Content);

        Assert.Equal(t2, roundTripped.Narratives[1].Timestamp);
        Assert.Equal("worker-late", roundTripped.Narratives[1].WorkerId);
        Assert.Equal("task-42", roundTripped.Narratives[1].TaskId);
        Assert.Equal(new string('x', 6_000) + "\nTRAILING-SPACE-END ", roundTripped.Narratives[1].Content);

        // Ordering is part of the contract: the serialized array must carry entry 1 before entry 2.
        Assert.True(json.IndexOf("worker-early", StringComparison.Ordinal) < json.IndexOf("worker-late", StringComparison.Ordinal));
    }

    /// <summary>
    /// Pins the three-way representation contract that <see cref="PhaseResult.Narratives"/>
    /// documents, plus the separation-from-WorkerOutput invariant:
    /// <list type="bullet">
    ///   <item>null Narratives round-trips as null ("never captured");</item>
    ///   <item>an EMPTY list round-trips as an empty list, NOT null ("captured, zero
    ///     narratives") — the two states must stay distinguishable through persistence;</item>
    ///   <item>narrative content is never merged into, nor read from, WorkerOutput.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void RoundTrip_NullVersusEmptyNarratives_StaysDistinguishableAndSeparateFromWorkerOutput()
    {
        // ── null: "never captured" ───────────────────────────────────────────
        var neverCaptured = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            DurationSeconds = 1.0,
            WorkerOutput = "the authoritative worker report",
            Narratives = null,
        };

        var nullJson = JsonSerializer.Serialize(neverCaptured, SqliteJsonOptions);
        var nullRoundTripped = JsonSerializer.Deserialize<PhaseResult>(nullJson, SqliteJsonOptions);

        Assert.NotNull(nullRoundTripped);
        Assert.Null(nullRoundTripped!.Narratives);
        // WorkerOutput is untouched by the narrative property being absent.
        Assert.Equal("the authoritative worker report", nullRoundTripped.WorkerOutput);

        // ── empty: "captured, zero narratives" ───────────────────────────────
        var capturedEmpty = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            DurationSeconds = 1.0,
            WorkerOutput = "the authoritative worker report",
            Narratives = [],
        };

        var emptyJson = JsonSerializer.Serialize(capturedEmpty, SqliteJsonOptions);
        var emptyRoundTripped = JsonSerializer.Deserialize<PhaseResult>(emptyJson, SqliteJsonOptions);

        Assert.NotNull(emptyRoundTripped);
        // The critical distinction: empty must NOT collapse into null through persistence.
        Assert.NotNull(emptyRoundTripped!.Narratives);
        Assert.Empty(emptyRoundTripped.Narratives!);
        Assert.Equal("the authoritative worker report", emptyRoundTripped.WorkerOutput);

        // ── separation: narrative content never leaks into WorkerOutput ──────
        const string narrativeContent = "NARRATIVE-ONLY-MARKER: the blocker was a missing key.";
        const string workerOutputText = "WORKER-OUTPUT-ONLY-MARKER: structured report.";
        var populated = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Fail,
            DurationSeconds = 2.0,
            WorkerOutput = workerOutputText,
            Narratives =
            [
                new NarrativeEntry
                {
                    Timestamp = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
                    WorkerId = "worker-a",
                    TaskId = "task-sep",
                    Content = narrativeContent,
                },
            ],
        };

        var populatedRoundTripped = JsonSerializer.Deserialize<PhaseResult>(
            JsonSerializer.Serialize(populated, SqliteJsonOptions), SqliteJsonOptions);

        Assert.NotNull(populatedRoundTripped);
        // WorkerOutput carries ONLY the worker report — no narrative concatenation.
        Assert.Equal(workerOutputText, populatedRoundTripped!.WorkerOutput);
        Assert.DoesNotContain(narrativeContent, populatedRoundTripped.WorkerOutput!);
        // The narrative carries ONLY its own content — it did not absorb the worker output.
        var singleNarrative = Assert.Single(populatedRoundTripped.Narratives!);
        Assert.Equal(narrativeContent, singleNarrative.Content);
        Assert.DoesNotContain(workerOutputText, singleNarrative.Content);
    }

    /// <summary>
    /// Stand-in for the internal PhaseResultEntry DTO previously used by the removed YAML source.
    /// </summary>
    private sealed class PhaseResultEntry
    {
        public string? Name { get; set; }
        public string? Result { get; set; }
        public double DurationSeconds { get; set; }
    }
}