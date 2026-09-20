using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// THE ROUND-1 CONSTRUCTOR-CLASSIFICATION FIXTURE: what the restoring
/// <see cref="GoalPipeline(PipelineSnapshot)"/> constructor does with the persisted
/// <c>WorkSlotRegistryJson</c> text for a HELD instance
/// (<see cref="GoalPipeline.IsRestoredActiveAttemptHold"/>).
/// <para>
/// THE EVIDENCE is the ACTUAL registry content after the constructor returns — read back through
/// the production <see cref="GoalPipeline.CaptureRegistry"/> (slots, states, positions, attempts
/// and per-position high-water counters) — never the classification label alone.
/// </para>
/// <para>
/// THE REJECTIONS (SQL NULL, empty text, unsupported JSON, malformed JSON, and a structurally
/// decodable but domain-invalid LATE entry) each assert NO PARTIAL INSTALL: the capture stays
/// exactly empty (the strong form — a counter-only residue would also fail), the hold stays
/// <c>true</c>, and <see cref="GoalPipeline.OwnershipCheckpointEligible"/> stays <c>false</c>.
/// </para>
/// <para>
/// Both REAL manager restore routes (<see cref="GoalPipelineManager.RestorePipeline"/> and
/// <see cref="GoalPipelineManager.RestoreFromStore"/>) are exercised over the same blobs, with the
/// "no new manager/store I/O or writes" claim proven by a fresh RAW readback of the unchanged row
/// and a second manager reconstructing the identical evidence.
/// </para>
/// </summary>
public sealed class RestoredRegistryHydrationTests : IDisposable
{
    private readonly string _connectionString =
        $"Data Source=file:memdb-hydration-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<SqliteConnection> _connections = [];

    public RestoredRegistryHydrationTests()
    {
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var connection in _connections)
            connection.Dispose();
        _keeper.Dispose();
    }

    // ═══════════════════════════════ fixture helpers ═══════════════════════════════

    private CopilotHiveDbContext CreateContext()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        _connections.Add(connection);
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        return new CopilotHiveDbContext(builder.Options);
    }

    private PipelineStore CreateStore() =>
        new(CreateContext(), NullLogger<PipelineStore>.Instance);

    private static Goal NewGoal(string id) =>
        new() { Id = id, Description = "hydration goal " + id, RepositoryNames = ["test-repo"] };

    /// <summary>A snapshot row seeded through the REAL store, then forced into the case's shape.</summary>
    private PipelineSnapshot SeedSnapshot(
        string goalId, GoalPhase phase, string? activeTaskId, string? blob,
        List<(string TaskId, string GoalId)>? mappings = null)
    {
        // A real row through the real store, so the shape matches production data.
        var store = CreateStore();
        var live = new GoalPipeline(NewGoal(goalId));
        var plan = IterationPlan.Default();
        live.SetPlan(plan);
        live.StateMachine.RestoreFromPlan(plan.Phases, phase);
        live.AdvanceTo(phase);
        store.SavePipeline(live);

        if (activeTaskId is not null)
        {
            using var pointerCommand = _keeper.CreateCommand();
            pointerCommand.CommandText =
                "UPDATE pipelines SET active_task_id = $task WHERE goal_id = $goal";
            pointerCommand.Parameters.AddWithValue("$task", activeTaskId);
            pointerCommand.Parameters.AddWithValue("$goal", goalId);
            Assert.Equal(1, pointerCommand.ExecuteNonQuery());
        }

        using var blobCommand = _keeper.CreateCommand();
        blobCommand.CommandText =
            "UPDATE pipelines SET work_slot_registry_json = $blob WHERE goal_id = $goal";
        blobCommand.Parameters.AddWithValue("$blob", (object?)blob ?? DBNull.Value);
        blobCommand.Parameters.AddWithValue("$goal", goalId);
        Assert.Equal(1, blobCommand.ExecuteNonQuery());

        // The raw row readback the constructor must be fed — the honest persisted state, read
        // through a FRESH store so the first store's change tracker cannot shadow the raw updates.
        var readback = CreateStore().LoadPipeline(goalId)!;
        Assert.Equal(blob, readback.WorkSlotRegistryJson);
        if (mappings is not null)
            readback.TaskMappings = [.. mappings];
        return readback;
    }

    private string? RawBlob(string goalId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "SELECT work_slot_registry_json FROM pipelines WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : (string?)result;
    }

    private static HashSet<WorkSlotView> SlotsOf(WorkSlotRegistrySnapshot snapshot) => [.. snapshot.Slots];

    private static HashSet<WorkSlotRegistryAttemptEntry> CountersOf(WorkSlotRegistrySnapshot snapshot) =>
        [.. snapshot.DispatchAttempts];

    // ── The shared rich evidence ─────────────────────────────────────────────────────────────

    private static readonly WorkSlotPosition PosPending = new(1, GoalPhase.Coding, 1);
    private static readonly WorkSlotPosition PosClaimed = new(1, GoalPhase.Coding, 2);
    private static readonly WorkSlotPosition PosRecordedRetry = new(1, GoalPhase.Testing, 1);  // two slots: retry
    private static readonly WorkSlotPosition PosAbandoned = new(2, GoalPhase.Review, 1);       // later iteration
    private static readonly WorkSlotPosition PosGappedHighWater = new(2, GoalPhase.Review, 5); // counter-only
    private static readonly WorkSlotPosition PosDeadSlot = new(1, GoalPhase.Improve, 3);       // counter > attempt

    /// <summary>
    /// THE RICH VALID BLOB: every one of the four slot states, a retried position with two slots
    /// and a gapped historical attempt sequence, a gapped COUNTER-ONLY position (occurrence 5 with
    /// no slot at all), a dead slot whose position's counter stands HIGHER than its own attempt,
    /// and two distinct iterations. Built through the codec from hand-built detached records and
    /// asserted task-by-task below.
    /// </summary>
    private static WorkSlotRegistrySnapshot RichRegistry() => new(
        [
            new WorkSlotView(new WorkSlot("pending-task", PosPending, 1), WorkSlotState.Pending),
            new WorkSlotView(new WorkSlot("claimed-task", PosClaimed, 3), WorkSlotState.Claimed),
            new WorkSlotView(new WorkSlot("recorded-task", PosRecordedRetry, 1), WorkSlotState.Recorded),
            new WorkSlotView(new WorkSlot("retried-task", PosRecordedRetry, 4), WorkSlotState.Pending),
            new WorkSlotView(new WorkSlot("abandoned-task", PosAbandoned, 2), WorkSlotState.Abandoned),
            new WorkSlotView(new WorkSlot("dead-task", PosDeadSlot, 2), WorkSlotState.Recorded),
        ],
        [
            new WorkSlotRegistryAttemptEntry(PosPending, 1),
            new WorkSlotRegistryAttemptEntry(PosClaimed, 3),
            new WorkSlotRegistryAttemptEntry(PosRecordedRetry, 4),
            new WorkSlotRegistryAttemptEntry(PosAbandoned, 2),
            new WorkSlotRegistryAttemptEntry(PosDeadSlot, 9),        // counter HIGHER than its slot's attempt
            new WorkSlotRegistryAttemptEntry(PosGappedHighWater, 7), // counter-ONLY: no slot at this position
        ]);

    private const string RichBlobText =
        """
        {"version":1,"slots":[
          {"taskId":"pending-task","position":{"iteration":1,"phase":"Coding","occurrence":1},"attempt":1,"state":"Pending"},
          {"taskId":"claimed-task","position":{"iteration":1,"phase":"Coding","occurrence":2},"attempt":3,"state":"Claimed"},
          {"taskId":"recorded-task","position":{"iteration":1,"phase":"Testing","occurrence":1},"attempt":1,"state":"Recorded"},
          {"taskId":"retried-task","position":{"iteration":1,"phase":"Testing","occurrence":1},"attempt":4,"state":"Pending"},
          {"taskId":"abandoned-task","position":{"iteration":2,"phase":"Review","occurrence":1},"attempt":2,"state":"Abandoned"},
          {"taskId":"dead-task","position":{"iteration":1,"phase":"Improve","occurrence":3},"attempt":2,"state":"Recorded"}
        ],
        "dispatchAttempts":[
          {"position":{"iteration":1,"phase":"Coding","occurrence":1},"highWaterAttempt":1},
          {"position":{"iteration":1,"phase":"Coding","occurrence":2},"highWaterAttempt":3},
          {"position":{"iteration":1,"phase":"Testing","occurrence":1},"highWaterAttempt":4},
          {"position":{"iteration":2,"phase":"Review","occurrence":1},"highWaterAttempt":2},
          {"position":{"iteration":1,"phase":"Improve","occurrence":3},"highWaterAttempt":9},
          {"position":{"iteration":2,"phase":"Review","occurrence":5},"highWaterAttempt":7}
        ]}
        """;

    /// <summary>THE ANTI-VACUOUS PRECONDITION: the fixture really carries everything claimed.</summary>
    private static void AssertSnapshotIsRich(WorkSlotRegistrySnapshot snapshot)
    {
        Assert.Equal(
            Enum.GetValues<WorkSlotState>().ToHashSet(),
            snapshot.Slots.Select(s => s.State).ToHashSet());
        Assert.Equal(6, snapshot.Slots.Count);
        Assert.Equal(6, snapshot.DispatchAttempts.Count);
        // The gapped counter-only position has NO slot there.
        Assert.Contains(snapshot.DispatchAttempts, a => a.Position == PosGappedHighWater);
        Assert.DoesNotContain(snapshot.Slots, s => s.Slot.Position == PosGappedHighWater);
        // The dead slot's counter stands HIGHER than its own attempt.
        var deadSlot = Assert.Single(snapshot.Slots, s => s.Slot.TaskId == "dead-task");
        var deadCounter = Assert.Single(snapshot.DispatchAttempts, a => a.Position == PosDeadSlot);
        Assert.True(deadCounter.HighWaterAttempt > deadSlot.Slot.Attempt);
    }

    private static void AssertRegistryIsEmpty(GoalPipeline pipeline)
    {
        var capture = pipeline.CaptureRegistry();
        Assert.Empty(capture.Slots);
        Assert.Empty(capture.DispatchAttempts);
        Assert.Empty(pipeline.GetSlotsForTest());
        // The strong form: an empty restore would succeed — a counter-only residue would throw.
        pipeline.RestoreRegistry(new WorkSlotRegistrySnapshot([], []));
    }

    /// <summary>
    /// THE HELD-INVARIANTS PROBE for every held case: the hold stays TRUE (even across a terminal
    /// AdvanceTo), checkpoint eligibility stays FALSE, and the durable row's blob is byte-identical.
    /// </summary>
    private void AssertHeldInvariants(GoalPipeline restored, string goalId, string? expectedBlob)
    {
        Assert.True(restored.IsRestoredActiveAttemptHold);
        Assert.False(restored.OwnershipCheckpointEligible,
            "a held restored pipeline must stay ineligible for registry checkpoint writes");
        restored.AdvanceTo(GoalPhase.Done);
        Assert.True(restored.IsRestoredActiveAttemptHold,
            "hydration must never release the hold — not even after a terminal phase transition");
        Assert.Equal(expectedBlob, RawBlob(goalId));
    }

    // ═══════════════════════════════ the classification matrix ═══════════════════════════════

    /// <summary>
    /// THE RESTORE-TIME CLASSIFICATION MATRIX over direct construction. Every outcome name is
    /// exercised with representative content, and each REJECTION case additionally asserts the NO
    /// PARTIAL INSTALL invariant through the actual <see cref="GoalPipeline.CaptureRegistry"/>
    /// contents.
    /// </summary>
    public static IEnumerable<object?[]> ConstructorClassificationCases()
    {
        // held + valid populated blob → Restored (the blob text is resolved inside the test)
        yield return ["populated", "rich", (int)RestoredRegistryOutcome.Restored, true];
        // held + a VALID EMPTY registry → Restored (never MissingRegistry)
        yield return ["empty-payload", "{\"version\":1,\"slots\":[],\"dispatchAttempts\":[]}",
            (int)RestoredRegistryOutcome.Restored, true];
        // held + SQL NULL → MissingRegistry, nothing installed
        yield return ["sql-null", null, (int)RestoredRegistryOutcome.MissingRegistry, false];
        // held + empty TEXT → a decodable-failure representation rejection (empty string is not JSON)
        yield return ["empty-text", "", (int)RestoredRegistryOutcome.DecodeRejected, false];
        // held + unsupported JSON shape (not an object / not an envelope) → DecodeRejected
        yield return ["unsupported-json", "[]", (int)RestoredRegistryOutcome.DecodeRejected, false];
        // held + unsupported version → DecodeRejected
        yield return ["unsupported-version", "{\"version\":2,\"slots\":[],\"dispatchAttempts\":[]}",
            (int)RestoredRegistryOutcome.DecodeRejected, false];
        // held + malformed JSON → DecodeRejected
        yield return ["malformed", "{not json", (int)RestoredRegistryOutcome.DecodeRejected, false];
        // held + structurally decodable but domain-invalid LATE entry → DomainRejected, nothing partial
        yield return ["domain-invalid-late",
            "{\"version\":1,\"slots\":["
            + "{\"taskId\":\"good-task\",\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":1},\"attempt\":1,\"state\":\"Pending\"},"
            + "{\"taskId\":\"bad-task\",\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":2},\"attempt\":0,\"state\":\"Pending\"}],"
            + "\"dispatchAttempts\":["
            + "{\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":1},\"highWaterAttempt\":1},"
            + "{\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":2},\"highWaterAttempt\":1}]}",
            (int)RestoredRegistryOutcome.DomainRejected, false];
    }

    [Theory]
    [MemberData(nameof(ConstructorClassificationCases))]
    public void RestoreConstructor_ClassifiesBlob_OutcomeByOutcome(
        string label, string? blob, int expectedOutcomeCode, bool expectHydratedContent)
    {
        var expectedOutcome = (RestoredRegistryOutcome)expectedOutcomeCode;
        if (blob == "rich")
            blob = RichBlobText;   // the theory row carries a sentinel; the real text is the fixture
        var goalId = $"hydration-{label}";
        var snapshot = SeedSnapshot(goalId, GoalPhase.Coding, "active-" + label, blob);
        Assert.True(snapshot.ActiveTaskId is not null);   // held precondition

        var restored = new GoalPipeline(snapshot);

        Assert.Equal(expectedOutcome, restored.RestoredRegistryClassification);
        Assert.True(restored.IsRestoredActiveAttemptHold);
        Assert.False(restored.OwnershipCheckpointEligible);
        Assert.Equal("active-" + label, restored.ActiveTaskId);

        // THE CONTENTS, not just the label: every rejection leaves both dictionaries exactly
        // empty; the Restored cases leave hydrated evidence in the capture.
        if (expectHydratedContent)
        {
            if (label == "populated")
            {
                var capture = restored.CaptureRegistry();
                AssertSnapshotIsRich(capture);
                // The EXACT decoded evidence, value-for-value.
                var expected = WorkSlotRegistryCodec.Decode(RichBlobText);
                Assert.Equal(SlotsOf(expected), SlotsOf(capture));
                Assert.Equal(CountersOf(expected), CountersOf(capture));
            }
            else
            {
                // The valid-empty case: Restored, but with empty contents.
                var capture = restored.CaptureRegistry();
                Assert.Empty(capture.Slots);
                Assert.Empty(capture.DispatchAttempts);
            }

            // The raw blob in the row is untouched by the hydration.
            Assert.Equal(blob, RawBlob(goalId));
        }
        else
        {
            // NO PARTIAL INSTALL in every rejection case — including the domain-invalid LATE
            // entry whose valid FIRST slot must not have landed either.
            AssertRegistryIsEmpty(restored);
        }

        AssertHeldInvariants(restored, goalId, blob);
    }

    [Fact]
    public void RestoreConstructor_RichBlob_HydratesExactTaskIdsStatesPositionsAttemptsAndCounters()
    {
        var goalId = "hydration-rich-exact";
        var snapshot = SeedSnapshot(goalId, GoalPhase.Coding, "retried-task", RichBlobText);

        var restored = new GoalPipeline(snapshot);

        Assert.Equal(RestoredRegistryOutcome.Restored, restored.RestoredRegistryClassification);
        var capture = restored.CaptureRegistry();
        AssertSnapshotIsRich(capture);

        // EXACT task IDs, states, positions, attempts — asserted individually, not only as a set.
        Assert.Contains(capture.Slots, s =>
            s.Slot.TaskId == "pending-task" &&
            s.State == WorkSlotState.Pending &&
            s.Slot.Position == PosPending &&
            s.Slot.Attempt == 1);
        Assert.Contains(capture.Slots, s =>
            s.Slot.TaskId == "claimed-task" &&
            s.State == WorkSlotState.Claimed &&
            s.Slot.Position == PosClaimed &&
            s.Slot.Attempt == 3);
        Assert.Contains(capture.Slots, s =>
            s.Slot.TaskId == "recorded-task" &&
            s.State == WorkSlotState.Recorded &&
            s.Slot.Position == PosRecordedRetry &&
            s.Slot.Attempt == 1);
        Assert.Contains(capture.Slots, s =>
            s.Slot.TaskId == "retried-task" &&
            s.State == WorkSlotState.Pending &&
            s.Slot.Position == PosRecordedRetry &&
            s.Slot.Attempt == 4);
        Assert.Contains(capture.Slots, s =>
            s.Slot.TaskId == "abandoned-task" &&
            s.State == WorkSlotState.Abandoned &&
            s.Slot.Position == PosAbandoned &&
            s.Slot.Attempt == 2);
        Assert.Contains(capture.Slots, s =>
            s.Slot.TaskId == "dead-task" &&
            s.State == WorkSlotState.Recorded &&
            s.Slot.Position == PosDeadSlot &&
            s.Slot.Attempt == 2);

        // GAPS AND HIGH-WATER EVIDENCE, exactly: the retried position carries attempt 4 under a
        // counter of 4 (a 1→4 gap), the dead slot's counter stands at 9 over its attempt 2, and
        // the counter-only position at occurrence 5 carries high-water 7 with NO slot at all.
        var retriedCounter = Assert.Single(capture.DispatchAttempts, a => a.Position == PosRecordedRetry);
        Assert.Equal(4, retriedCounter.HighWaterAttempt);
        var deadCounter = Assert.Single(capture.DispatchAttempts, a => a.Position == PosDeadSlot);
        Assert.Equal(9, deadCounter.HighWaterAttempt);
        var counterOnly = Assert.Single(capture.DispatchAttempts, a => a.Position == PosGappedHighWater);
        Assert.Equal(7, counterOnly.HighWaterAttempt);
        Assert.DoesNotContain(capture.Slots, s => s.Slot.Position == PosGappedHighWater);

        // THE COUNTER CONTINUITY: the next allocation at the dead slot's position continues from
        // the restored counter (10), proving the high-water evidence was genuinely installed and
        // not merely carried in a detached capture.
        Assert.Equal(10, restored.AllocateAttemptAndRegisterSlot(
            "continuity-probe", PosDeadSlot).Attempt);

        // The pointer and the raw bytes were never mutated by the hydration.
        Assert.Equal("retried-task", restored.ActiveTaskId);
        Assert.Equal(RichBlobText, RawBlob(goalId));
    }

    [Fact]
    public void RestoreConstructor_UnheldSnapshot_KeepsRegistryUntouched_AndClassifiesNotApplicable()
    {
        // An UNHELD row: nonterminal phase, NULL pointer — the positive control stays inert even
        // with a rich valid blob in the column.
        var goalId = "hydration-unheld-control";
        var snapshot = SeedSnapshot(goalId, GoalPhase.Coding, activeTaskId: null, RichBlobText);
        Assert.Null(snapshot.ActiveTaskId);

        var restored = new GoalPipeline(snapshot);

        Assert.False(restored.IsRestoredActiveAttemptHold);
        Assert.Equal(RestoredRegistryOutcome.NotApplicable, restored.RestoredRegistryClassification);
        // The blob is NOT consumed: nothing is decoded, nothing installed, the bytes untouched.
        AssertRegistryIsEmpty(restored);
        Assert.Equal(RichBlobText, RawBlob(goalId));
    }

    [Fact]
    public void RestoreConstructor_TerminalPhase_WithPointer_StaysUnheld_NotApplicable()
    {
        // A terminal phase with a pointer is not restored-held (and never consumed registry text).
        var goalId = "hydration-terminal-with-pointer";
        var snapshot = SeedSnapshot(goalId, GoalPhase.Done, "some-active-task", RichBlobText);

        var restored = new GoalPipeline(snapshot);

        Assert.False(restored.IsRestoredActiveAttemptHold);
        Assert.Equal(RestoredRegistryOutcome.NotApplicable, restored.RestoredRegistryClassification);
        AssertRegistryIsEmpty(restored);
        Assert.Equal(RichBlobText, RawBlob(goalId));
    }

    // ═══════════════════════════════ the pointer/mapping observations ═══════════════════════════════

    /// <summary>
    /// THE POINTER/MAPPING OBSERVATION MATRIX over direct construction with hydrated rich evidence.
    /// The pointer observation is a restore-time capture against the HYDRATED registry; the mapping
    /// observation reports whether the snapshot's own TaskMappings list contains the exact ordinal
    /// (active task, goal) pair.
    /// </summary>
    public static IEnumerable<object?[]> PointerMappingCases()
    {
        // The four exact matching-slot states — the pointer names a task in each state.
        yield return ["pending", "pending-task", (int)RestoredActivePointerOutcome.ActiveSlotPending, true];
        yield return ["claimed", "claimed-task", (int)RestoredActivePointerOutcome.ActiveSlotClaimed, true];
        yield return ["abandoned", "abandoned-task", (int)RestoredActivePointerOutcome.ActiveSlotAbandoned, true];
        // Non-blank pointer with NO ordinal-matching slot in the hydrated registry.
        yield return ["no-match", "no-such-task", (int)RestoredActivePointerOutcome.NoMatchingSlot, false];
        // A blank (whitespace) pointer: representable as held (non-null) but unassessable per slot.
        yield return ["blank-pointer", "   ", (int)RestoredActivePointerOutcome.BlankPointer, false];
    }

    [Theory]
    [MemberData(nameof(PointerMappingCases))]
    public void RestoreConstructor_PointerObservation_ReportsTheExactMatchingSlotState(
        string label, string activeTaskId, int expectedPointerCode, bool mappingPresent)
    {
        var expectedPointer = (RestoredActivePointerOutcome)expectedPointerCode;
        var goalId = $"hydration-pointer-{label}";
        var mappings = new List<(string, string)>();
        if (mappingPresent)
            mappings.Add((activeTaskId, goalId));
        // A FOREIGN mapping exists elsewhere in the list — absent pair is not proof of no mappings.
        mappings.Add(("foreign-task", goalId));

        var snapshot = SeedSnapshot(goalId, GoalPhase.Coding, activeTaskId, RichBlobText, mappings);
        var restored = new GoalPipeline(snapshot);

        Assert.True(restored.IsRestoredActiveAttemptHold);
        Assert.Equal(RestoredRegistryOutcome.Restored, restored.RestoredRegistryClassification);
        Assert.Equal(expectedPointer, restored.RestoredActivePointerClassification);
        Assert.Equal(mappingPresent, restored.RestoredActiveTaskMappingPresent);
        // Disagreement never discards the evidence: ALL six slots stay hydrated.
        Assert.Equal(6, restored.CaptureRegistry().Slots.Count);
        Assert.Equal(RichBlobText, RawBlob(goalId));
    }

    [Fact]
    public void RestoreConstructor_RecordedPointer_ReportsTheRecordedSlotState()
    {
        // Completes the four-state coverage: the pointer names the genuinely Recorded slot.
        var goalId = "hydration-pointer-recorded";
        var mappings = new List<(string, string)> { ("recorded-task", goalId) };
        var snapshot = SeedSnapshot(goalId, GoalPhase.Coding, "recorded-task", RichBlobText, mappings);

        var restored = new GoalPipeline(snapshot);

        Assert.Equal(RestoredActivePointerOutcome.ActiveSlotRecorded, restored.RestoredActivePointerClassification);
        Assert.True(restored.RestoredActiveTaskMappingPresent);
        Assert.Equal(6, restored.CaptureRegistry().Slots.Count);
    }

    [Fact]
    public void RestoreConstructor_MissingOrRejectedEvidence_PointerObservationIsUnassessed()
    {
        // With MISSING evidence the pointer observation is Unassessed — explicitly NOT a claim
        // that the task is missing.
        var nullGoal = "hydration-pointer-null";
        var nullSnapshot = SeedSnapshot(nullGoal, GoalPhase.Coding, "some-active-task", blob: null);
        var nullRestored = new GoalPipeline(nullSnapshot);
        Assert.Equal(RestoredRegistryOutcome.MissingRegistry, nullRestored.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.Unassessed, nullRestored.RestoredActivePointerClassification);

        // Same for DECODE-rejected evidence.
        var badGoal = "hydration-pointer-decode-rejected";
        var badSnapshot = SeedSnapshot(badGoal, GoalPhase.Coding, "some-active-task", "{not json");
        var badRestored = new GoalPipeline(badSnapshot);
        Assert.Equal(RestoredRegistryOutcome.DecodeRejected, badRestored.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.Unassessed, badRestored.RestoredActivePointerClassification);

        // Same for DOMAIN-rejected evidence (structurally decoded, domain refused).
        const string domainInvalid =
            "{\"version\":1,\"slots\":["
            + "{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":1},\"attempt\":0,\"state\":\"Pending\"}],"
            + "\"dispatchAttempts\":["
            + "{\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":1},\"highWaterAttempt\":1}]}";
        var domainGoal = "hydration-pointer-domain-rejected";
        var domainSnapshot = SeedSnapshot(domainGoal, GoalPhase.Coding, "some-active-task", domainInvalid);
        var domainRestored = new GoalPipeline(domainSnapshot);
        Assert.Equal(RestoredRegistryOutcome.DomainRejected, domainRestored.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.Unassessed, domainRestored.RestoredActivePointerClassification);

        // And for an UNHELD instance (NotApplicable), the pointer observation is Unassessed too.
        var unheldGoal = "hydration-pointer-unheld";
        var unheldSnapshot = SeedSnapshot(unheldGoal, GoalPhase.Coding, activeTaskId: null, RichBlobText);
        var unheldRestored = new GoalPipeline(unheldSnapshot);
        Assert.Equal(RestoredRegistryOutcome.NotApplicable, unheldRestored.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.Unassessed, unheldRestored.RestoredActivePointerClassification);
    }

    [Fact]
    public void RestoreConstructor_BlankPointer_MappingObservationIsFalse()
    {
        // A blank pointer can never form an ordinal (task, goal) pair — the mapping observation is
        // false regardless of what the captured mappings list contains.
        var goalId = "hydration-blank-mapping";
        var mappings = new List<(string, string)> { ("   ", goalId) };   // even a literal whitespace pair
        var snapshot = SeedSnapshot(goalId, GoalPhase.Coding, "   ", RichBlobText, mappings);

        var restored = new GoalPipeline(snapshot);

        Assert.True(restored.IsRestoredActiveAttemptHold);
        Assert.Equal(RestoredActivePointerOutcome.BlankPointer, restored.RestoredActivePointerClassification);
        Assert.False(restored.RestoredActiveTaskMappingPresent);
    }

    [Fact]
    public void RestoreConstructor_MappingObservation_UsesOrdinalExactPair_NotCaseVariants()
    {
        // A case-variant or whitespace-padded mapping is NOT the exact ordinal pair.
        var goalId = "hydration-mapping-ordinal";
        var mappings = new List<(string, string)>
        {
            ("PENDING-TASK", goalId),      // case variant
            (" pending-task", goalId),     // padded variant
            ("pending-task", "other-goal") // right task, wrong goal
        };
        var snapshot = SeedSnapshot(goalId, GoalPhase.Coding, "pending-task", RichBlobText, mappings);

        var restored = new GoalPipeline(snapshot);

        Assert.Equal(RestoredActivePointerOutcome.ActiveSlotPending, restored.RestoredActivePointerClassification);
        Assert.False(restored.RestoredActiveTaskMappingPresent,
            "only the exact ordinal (task, goal) pair satisfies the mapping observation");
    }

    // ═══════════════════════════════ both manager restore routes ═══════════════════════════════

    /// <summary>
    /// BOTH REAL manager restore routes hydrate the same rich evidence through their EXISTING
    /// constructor calls, with NO new manager/store I/O or writes: the raw row (blob and pointer)
    /// is byte-identical after the restore, and a SECOND manager reconstructing from the unchanged
    /// row reports identical classification and identical registry contents.
    /// </summary>
    [Theory]
    [InlineData("restore-pipeline")]
    [InlineData("restore-from-store")]
    public void ManagerRestoreRoutes_HydrateHeldEvidence_WithNoWrites(string route)
    {
        var goalId = $"hydration-route-{route}";
        var store = CreateStore();
        var live = new GoalPipeline(NewGoal(goalId));
        var plan = IterationPlan.Default();
        live.SetPlan(plan);
        live.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        live.AdvanceTo(GoalPhase.Coding);
        live.AllocateAttemptAndRegisterSlot("route-pending-task", new WorkSlotPosition(1, GoalPhase.Coding, 1));
        live.SetActiveTask("route-pending-task", "copilothive/" + goalId);
        // The registry blob must describe the SAME task the pointer names, so the fixture's
        // pending slot is renamed to the live pointer's task before it is seeded.
        var routeBlob = RichBlobText.Replace(
            "\"taskId\":\"pending-task\"", "\"taskId\":\"route-pending-task\"", StringComparison.Ordinal);
        // The durable mapping row the real admission transaction would have written.
        using (var mappingCommand = _keeper.CreateCommand())
        {
            mappingCommand.CommandText =
                "INSERT INTO task_mappings (task_id, goal_id) VALUES ($task, $goal)";
            mappingCommand.Parameters.AddWithValue("$task", "route-pending-task");
            mappingCommand.Parameters.AddWithValue("$goal", goalId);
            Assert.Equal(1, mappingCommand.ExecuteNonQuery());
        }
        store.SavePipeline(live);

        // Seed the registry blob RAW, exactly as a historical checkpoint would have left it.
        using (var command = _keeper.CreateCommand())
        {
            command.CommandText =
                "UPDATE pipelines SET work_slot_registry_json = $blob WHERE goal_id = $goal";
            command.Parameters.AddWithValue("$blob", routeBlob);
            command.Parameters.AddWithValue("$goal", goalId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var expectedBlob = RawBlob(goalId);
        Assert.Equal(routeBlob, expectedBlob);

        // A manager over a FRESH store (a new context, no change-tracker shadow of the raw blob
        // update) — the production shape for restore.
        var manager = new GoalPipelineManager(CreateStore(), NullLogger<GoalPipelineManager>.Instance);
        var restored = route == "restore-pipeline"
            ? manager.RestorePipeline(goalId)
            : Assert.Single(manager.RestoreFromStore(), p => p.GoalId == goalId);
        Assert.NotNull(restored);

        Assert.True(restored!.IsRestoredActiveAttemptHold);
        Assert.False(restored.OwnershipCheckpointEligible);
        Assert.Equal(RestoredRegistryOutcome.Restored, restored.RestoredRegistryClassification);
        Assert.Equal(
            RestoredActivePointerOutcome.ActiveSlotPending,
            restored.RestoredActivePointerClassification);
        Assert.True(restored.RestoredActiveTaskMappingPresent,
            "the admitted mapping (route-pending-task, goal) is in the snapshot's captured mappings");

        // THE EXACT CONTENTS through both routes.
        var capture = restored.CaptureRegistry();
        var expected = WorkSlotRegistryCodec.Decode(routeBlob);
        Assert.Equal(SlotsOf(expected), SlotsOf(capture));
        Assert.Equal(CountersOf(expected), CountersOf(capture));

        // NO WRITES: the durable row is unchanged — blob and pointer both byte-identical.
        Assert.Equal(routeBlob, RawBlob(goalId));
        using (var command = _keeper.CreateCommand())
        {
            command.CommandText =
                "SELECT active_task_id FROM pipelines WHERE goal_id = $goal";
            command.Parameters.AddWithValue("$goal", goalId);
            Assert.Equal("route-pending-task", command.ExecuteScalar());
        }

        // A SECOND manager reconstructs from the UNCHANGED row and reports IDENTICAL evidence —
        // no store write or blob rewrite between reconstructions.
        var secondManager = new GoalPipelineManager(CreateStore(), NullLogger<GoalPipelineManager>.Instance);
        var second = route == "restore-pipeline"
            ? secondManager.RestorePipeline(goalId)
            : Assert.Single(secondManager.RestoreFromStore(), p => p.GoalId == goalId);
        Assert.NotNull(second);
        Assert.True(second!.IsRestoredActiveAttemptHold);
        Assert.Equal(RestoredRegistryOutcome.Restored, second.RestoredRegistryClassification);
        var secondCapture = second.CaptureRegistry();
        Assert.Equal(SlotsOf(expected), SlotsOf(secondCapture));
        Assert.Equal(CountersOf(expected), CountersOf(secondCapture));
        Assert.Equal(routeBlob, RawBlob(goalId));
    }

    // ═══════════════════════════════ rejected bytes survive saves and readback ═══════════════════════════════

    /// <summary>
    /// A held pipeline whose evidence was REJECTED (malformed text) hydrates nothing, and the raw
    /// rejected bytes survive BOTH the manager's ordinary full and state saves byte-for-byte —
    /// the legacy blob-preserving save paths, proven on the hydration round.
    /// </summary>
    [Fact]
    public void ManagerSaves_OfHeldRejectedEvidence_PreserveTheRawBytesByteForByte()
    {
        const string malformed = "{opaque malformed registry text";
        var goalId = "hydration-rejected-saves";
        var snapshot = SeedSnapshot(goalId, GoalPhase.Coding, "some-active-task", malformed);

        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var restored = manager.RestorePipeline(goalId)!;

        Assert.True(restored.IsRestoredActiveAttemptHold);
        Assert.False(restored.OwnershipCheckpointEligible);
        Assert.Equal(RestoredRegistryOutcome.DecodeRejected, restored.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.Unassessed, restored.RestoredActivePointerClassification);
        AssertRegistryIsEmpty(restored);

        // A genuinely NEW slot post-restore — the preservation must survive it (the save's capture
        // is not taken because the instance is ineligible).
        restored.AllocateAttemptAndRegisterSlot(
            "post-restore-task", new WorkSlotPosition(1, GoalPhase.Testing, 1));

        restored.AdvanceTo(GoalPhase.Testing);
        manager.PersistFull(restored);
        Assert.Equal(malformed, RawBlob(goalId));

        restored.AdvanceTo(GoalPhase.Review);
        manager.PersistState(restored);
        Assert.Equal(malformed, RawBlob(goalId));
        Assert.Equal("some-active-task", restored.ActiveTaskId);
    }
}