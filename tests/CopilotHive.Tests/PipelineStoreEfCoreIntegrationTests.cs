using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests that verify specific EF Core behaviors of the rewritten
/// <see cref="PipelineStore"/>: seq auto-increment, conversation-replace transaction,
/// cascade delete across 3 tables, and SavePipelineState preserving conversation.
/// </summary>
public sealed class PipelineStoreEfCoreIntegrationTests : IAsyncDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly PipelineStore _store;

    public PipelineStoreEfCoreIntegrationTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new PipelineStore(_dbContext, NullLogger<PipelineStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        await _dbContext.DisposeAsync();
    }

    private static Goal CreateGoal(string id = "goal-1", string desc = "Test goal") =>
        new() { Id = id, Description = desc, RepositoryNames = ["test-repo"] };

    private static GoalPipeline CreatePipeline(string id = "goal-1", string desc = "Test goal", int maxRetries = 3)
    {
        var goal = CreateGoal(id, desc);
        return new GoalPipeline(goal, maxRetries);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test 1: AppendConversation seq auto-increment
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AppendConversation_SeqAutoIncrement_ThreeEntriesSequential()
    {
        // Save a pipeline so the goal exists in the pipelines table
        var pipeline = CreatePipeline("goal-a", "Goal A");
        _store.SavePipeline(pipeline);

        // Append three conversation entries for the same goal
        _store.AppendConversation("goal-a", new ConversationEntry("user", "First"));
        _store.AppendConversation("goal-a", new ConversationEntry("assistant", "Second"));
        _store.AppendConversation("goal-a", new ConversationEntry("user", "Third"));

        // Verify via GetConversation that all three are present and in order
        var conversation = _store.GetConversation("goal-a");
        Assert.Equal(3, conversation.Count);
        Assert.Equal("First", conversation[0].Content);
        Assert.Equal("Second", conversation[1].Content);
        Assert.Equal("Third", conversation[2].Content);

        // Verify seq values are 0, 1, 2 by querying the DbContext directly
        var entries = _dbContext.ConversationEntries
            .Where(e => e.GoalId == "goal-a")
            .OrderBy(e => e.Seq)
            .ToList();
        Assert.Equal(3, entries.Count);
        Assert.Equal(0, entries[0].Seq);
        Assert.Equal(1, entries[1].Seq);
        Assert.Equal(2, entries[2].Seq);
    }

    [Fact]
    public void AppendConversation_DifferentGoal_StartsAtSeqZero()
    {
        // Save pipelines for two different goals so both exist
        var pipelineA = CreatePipeline("goal-a", "Goal A");
        _store.SavePipeline(pipelineA);
        var pipelineB = CreatePipeline("goal-b", "Goal B");
        _store.SavePipeline(pipelineB);

        // Append entries for goal-a first
        _store.AppendConversation("goal-a", new ConversationEntry("user", "A1"));
        _store.AppendConversation("goal-a", new ConversationEntry("assistant", "A2"));

        // Now append an entry for goal-b — should start at seq 0, not continue from goal-a
        _store.AppendConversation("goal-b", new ConversationEntry("user", "B1"));

        // Verify goal-a has 2 entries (seq 0, 1)
        var conversationA = _store.GetConversation("goal-a");
        Assert.Equal(2, conversationA.Count);
        var entriesA = _dbContext.ConversationEntries
            .Where(e => e.GoalId == "goal-a")
            .OrderBy(e => e.Seq)
            .ToList();
        Assert.Equal(0, entriesA[0].Seq);
        Assert.Equal(1, entriesA[1].Seq);

        // Verify goal-b has 1 entry with seq 0
        var conversationB = _store.GetConversation("goal-b");
        Assert.Single(conversationB);
        Assert.Equal("B1", conversationB[0].Content);
        var entriesB = _dbContext.ConversationEntries
            .Where(e => e.GoalId == "goal-b")
            .ToList();
        Assert.Single(entriesB);
        Assert.Equal(0, entriesB[0].Seq);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test 2: SavePipeline upsert + conversation-replace transaction
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SavePipeline_UpsertAndConversationReplace_Transactional()
    {
        var pipeline = CreatePipeline("goal-x", "Goal X");
        pipeline.Conversation.Add(new ConversationEntry("user", "Original 1"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Original 2"));
        pipeline.Conversation.Add(new ConversationEntry("user", "Original 3"));

        // First save — creates the pipeline row and 3 conversation entries
        _store.SavePipeline(pipeline);

        // Verify initial state
        var snap1 = Assert.Single(_store.LoadActivePipelines());
        Assert.Equal(3, snap1.Conversation.Count);
        Assert.Equal("Original 1", snap1.Conversation[0].Content);

        // Modify the pipeline: change phase and replace conversation with 2 different entries
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.Conversation.Clear();
        pipeline.Conversation.Add(new ConversationEntry("user", "New 1"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "New 2"));

        // Second save — should upsert the pipeline row and replace conversation (delete all + reinsert)
        _store.SavePipeline(pipeline);

        // Verify the pipeline was upserted (not duplicated) and conversation was replaced
        var snapshots = _store.LoadActivePipelines();
        var snap2 = Assert.Single(snapshots);

        // Phase should be updated
        Assert.Equal(GoalPhase.Coding, snap2.Phase);

        // Conversation should have exactly 2 entries (not 5 — old ones deleted, not appended)
        Assert.Equal(2, snap2.Conversation.Count);
        Assert.Equal("New 1", snap2.Conversation[0].Content);
        Assert.Equal("user", snap2.Conversation[0].Role);
        Assert.Equal("New 2", snap2.Conversation[1].Content);
        Assert.Equal("assistant", snap2.Conversation[1].Role);

        // Also verify at the DB level that there are exactly 2 conversation entries for this goal
        var dbEntries = _dbContext.ConversationEntries
            .Where(e => e.GoalId == "goal-x")
            .OrderBy(e => e.Seq)
            .ToList();
        Assert.Equal(2, dbEntries.Count);
        Assert.Equal(0, dbEntries[0].Seq);
        Assert.Equal(1, dbEntries[1].Seq);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test 3: RemovePipeline cascade delete across 3 tables
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void RemovePipeline_CascadeDeletesAcrossThreeTables()
    {
        var pipeline = CreatePipeline("goal-del", "Goal to delete");
        pipeline.Conversation.Add(new ConversationEntry("user", "Conversation entry 1"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Conversation entry 2"));

        // Save pipeline (creates rows in pipelines + conversation_entries)
        _store.SavePipeline(pipeline);

        // Save task mapping (creates row in task_mappings)
        _store.SaveTaskMapping("task-del-1", "goal-del");

        // Verify data exists before deletion
        Assert.Single(_store.LoadActivePipelines());
        Assert.Equal(2, _store.GetConversation("goal-del").Count);
        var mappings = _dbContext.TaskMappings.Where(t => t.GoalId == "goal-del").ToList();
        Assert.Single(mappings);

        // Remove the pipeline — should cascade-delete across all 3 tables
        _store.RemovePipeline("goal-del");

        // Verify LoadActivePipelines returns empty
        Assert.Empty(_store.LoadActivePipelines());

        // Verify GetConversation returns empty for the deleted goal
        Assert.Empty(_store.GetConversation("goal-del"));

        // Verify at the DB level that all rows across 3 tables are gone
        Assert.Empty(_dbContext.Pipelines.Where(p => p.GoalId == "goal-del").ToList());
        Assert.Empty(_dbContext.ConversationEntries.Where(e => e.GoalId == "goal-del").ToList());
        Assert.Empty(_dbContext.TaskMappings.Where(t => t.GoalId == "goal-del").ToList());
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test 4: SavePipelineState preserves conversation
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SavePipelineState_PreservesConversationEntries()
    {
        var pipeline = CreatePipeline("goal-state", "Goal state test");
        pipeline.Conversation.Add(new ConversationEntry("user", "Entry 1"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Entry 2"));

        // First save — creates the pipeline and 2 conversation entries
        _store.SavePipeline(pipeline);

        // Verify conversation was saved
        var snap1 = Assert.Single(_store.LoadActivePipelines());
        Assert.Equal(2, snap1.Conversation.Count);

        // Now advance the phase and call SavePipelineState (NOT SavePipeline)
        pipeline.AdvanceTo(GoalPhase.Coding);

        _store.SavePipelineState(pipeline);

        // Load again and verify conversation still has 2 entries
        var snap2 = Assert.Single(_store.LoadActivePipelines());

        // Phase should be updated
        Assert.Equal(GoalPhase.Coding, snap2.Phase);

        // Conversation must NOT be touched by SavePipelineState
        Assert.Equal(2, snap2.Conversation.Count);
        Assert.Equal("Entry 1", snap2.Conversation[0].Content);
        Assert.Equal("user", snap2.Conversation[0].Role);
        Assert.Equal("Entry 2", snap2.Conversation[1].Content);
        Assert.Equal("assistant", snap2.Conversation[1].Role);

        // Verify at the DB level that conversation entries are unchanged
        var dbEntries = _dbContext.ConversationEntries
            .Where(e => e.GoalId == "goal-state")
            .OrderBy(e => e.Seq)
            .ToList();
        Assert.Equal(2, dbEntries.Count);
        Assert.Equal("Entry 1", dbEntries[0].Content);
        Assert.Equal("Entry 2", dbEntries[1].Content);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Slice B: the coherent (phase, occurrence) pair persisted per save
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>The repeated-phase plan used across the slice-B store vectors.</summary>
    private static readonly List<GoalPhase> SliceBPlan =
        [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Merging];

    /// <summary>
    /// Drives a pipeline to the SECOND Coding of a repeated-phase plan (in-memory AND on the
    /// state machine, kept in sync exactly as the dispatcher does) so PersistFull captures
    /// the coherent machine pair (Coding, 2) that matches the pipeline phase.
    /// </summary>
    private static GoalPipeline CreateMidSecondCodingPipeline(string id, IterationPlan plan)
    {
        var pipeline = CreatePipeline(id, "Mid-second-coding pipeline");
        pipeline.SetPlan(plan);
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.StateMachine.StartIteration(plan.Phases);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Coding → Testing
        pipeline.AdvanceTo(GoalPhase.Testing);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Testing → Coding (second)
        pipeline.AdvanceTo(GoalPhase.Coding);                   // pipeline phase follows the machine
        return pipeline;
    }

    /// <summary>
    /// THE COHERENT PAIR WRITTEN AND READ: PersistFull of a mid-second-Coding pipeline writes
    /// phase_occurrence == 2 AND machine_phase == 'Coding'; LoadPipeline surfaces the pair as
    /// the snapshot's (MachinePhase, PhaseOccurrence) — and a restore via the constructor puts
    /// the machine back at (Coding, 2), the round-trip invariant.
    /// </summary>
    [Fact]
    public void SavePipeline_MidSecondCoding_PersistsCoherentPairAndRoundTrips()
    {
        var plan = new IterationPlan { Phases = [.. SliceBPlan] };
        var pipeline = CreateMidSecondCodingPipeline("goal-occ2", plan);

        _store.SavePipeline(pipeline);

        // The ROW carries the coherent pair, readable via the tracked context.
        var row = _dbContext.Pipelines.Find("goal-occ2");
        Assert.NotNull(row);
        Assert.Equal(2, row!.PhaseOccurrence);
        Assert.Equal("Coding", row.MachinePhase);

        // The SNAPSHOT surfaces the parsed pair.
        var snapshot = _store.LoadPipeline("goal-occ2");
        Assert.NotNull(snapshot);
        Assert.Equal(GoalPhase.Coding, snapshot!.MachinePhase);
        Assert.Equal(2, snapshot.PhaseOccurrence);

        // THE ROUND-TRIP INVARIANT: the restored machine sits at (Coding, 2) again.
        var restored = new GoalPipeline(snapshot);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 2, OccurrenceFound: true),
            restored.CaptureMachinePosition());
        Assert.Equal([GoalPhase.Merging], restored.StateMachine.RemainingPhases);
    }

    /// <summary>
    /// THE HONEST-PLANNING SAVE: a pipeline in the re-plan window (no installed plan) writes
    /// machine_phase NULL and phase_occurrence 1 — the null contract's write side — and its
    /// restore takes the legacy path (today's semantics: queue empty of the phase, machine
    /// agreeing with the restored phase, capture honestly not-found).
    /// </summary>
    [Fact]
    public void SavePipeline_PlanningWindowNoInstalledPlan_WritesNullMachinePhaseAndRestoresLegacy()
    {
        var pipeline = CreatePipeline("goal-planning", "Re-plan window pipeline");
        // No SetPlan: no installed phases → OccurrenceFound == false at capture.

        _store.SavePipeline(pipeline);

        // THE NULL CONTRACT, write side.
        var row = _dbContext.Pipelines.Find("goal-planning");
        Assert.NotNull(row);
        Assert.Null(row!.MachinePhase);
        Assert.Equal(1, row.PhaseOccurrence);

        var snapshot = _store.LoadPipeline("goal-planning");
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.MachinePhase);

        // Restore → legacy path: the machine agrees with the restored phase.
        var restored = new GoalPipeline(snapshot);
        Assert.Equal(GoalPhase.Planning, restored.Phase);
        Assert.Null(restored.Plan);
        Assert.Empty(restored.StateMachine.RemainingPhases);
        Assert.False(restored.CaptureMachinePosition().OccurrenceFound);
    }

    /// <summary>
    /// THE OLD-ROW PATH: a hand-inserted row with machine_phase NULL (as every pre-existing
    /// row has) restores via the legacy fallback — exactly today's behavior.
    /// </summary>
    [Fact]
    public void LoadPipeline_OldRowWithNullMachinePhase_RestoresViaLegacyFallback()
    {
        // Hand-insert an "old" row: no machine_phase, phase_occurrence at its default (1),
        // a plan, and a Coding phase — exactly what a pre-slice-B row looks like.
        var planJson = """{"phases":["Coding","Testing","Coding","Merging"],"phaseInstructions":{},"phaseTiers":{}}""";
        _dbContext.Pipelines.Add(new PipelineEntity
        {
            GoalId = "goal-oldrow",
            Description = "Pre-slice-B row",
            GoalJson = """{"id":"goal-oldrow","description":"old row goal","repositories":[]}""",
            Phase = "Coding",
            PlanJson = planJson,
            MetricsJson = "{}",
            CreatedAt = "2025-06-15T10:00:00.0000000Z",
            RoleSessionsJson = "{}",
            PhaseOccurrence = 2,
            MachinePhase = null,
        });
        _dbContext.SaveChanges();

        var snapshot = _store.LoadPipeline("goal-oldrow");
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.MachinePhase);
        Assert.Equal(2, snapshot.PhaseOccurrence); // carried but NOT trusted

        var restored = new GoalPipeline(snapshot);
        // THE LEGACY FALLBACK: byte-identical to today — queue [Testing, Merging],
        // completed empty, capture (Coding, 1, true) — the occurrence is NOT honored.
        Assert.Equal([GoalPhase.Testing, GoalPhase.Merging], restored.StateMachine.RemainingPhases);
        Assert.Empty(restored.StateMachine.CompletedPhases);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true),
            restored.CaptureMachinePosition());
    }

    /// <summary>
    /// THE NUMERIC-STRING CORRUPTION VECTOR: a row whose machine_phase is a pure numeric
    /// string ("999") must parse to MachinePhase == null — Enum.TryParse alone would accept
    /// "999" as a non-null undefined GoalPhase — and the restore constructor must take the
    /// LEGACY path exactly as an old row: the corrupt occurrence is never trusted.
    /// </summary>
    [Theory]
    [InlineData("999")]        // numeric, undefined value — the Enum.TryParse defect vector
    [InlineData("0")]          // numeric, defined ordinal (Planning) but still rejected
    [InlineData("1")]          // numeric, defined ordinal (Coding) but still rejected
    [InlineData("+1")]         // signed numeric — TryParse yields the DEFINED value Coding
    [InlineData("-1")]         // signed numeric, undefined value
    [InlineData(" 1 ")]        // whitespace-padded numeric — TryParse yields DEFINED Coding
    [InlineData("not-a-phase")]// non-numeric garbage
    [InlineData("")]           // empty string
    [InlineData("   ")]        // blank string
    [InlineData("١")]          // non-ASCII (Arabic-Indic) digit
    [InlineData("１")]          // non-ASCII (full-width) digit
    public void LoadPipeline_NumericMachinePhase_ParsesNull_FollowsLegacyRestorePath(string corruptMachinePhase)
    {
        AssertCorruptMachinePhaseFollowsLegacyRestorePath("goal-corrupt", corruptMachinePhase);
    }

    /// <summary>
    /// THE COMMA-EXPRESSION CORRUPTION VECTOR (the remaining false-acceptance class).
    /// <c>Enum.TryParse</c> accepts comma-separated enum NAMES and combines their underlying
    /// values by bitwise OR even though <c>GoalPhase</c> is not a flags enum, so
    /// <c>Enum.IsDefined</c> cannot detect the corruption: "Planning,Coding" and
    /// "Coding,Coding" yield the DEFINED value <c>Coding</c>, while "Coding,Review",
    /// "Coding, Testing" and "coding,testing" yield the DEFINED value <c>Testing</c>.
    /// A corrupt row carrying such a marker must NOT become a trusted matched pair: it must
    /// load as null and restore through the LEGACY path.
    /// </summary>
    [Theory]
    [InlineData("Planning,Coding")]  // → DEFINED Coding: would falsely MATCH the Coding row
    [InlineData("Coding,Coding")]    // → DEFINED Coding: same false match
    [InlineData("Coding,Review")]    // → DEFINED Testing: the defined-value trap
    [InlineData("Coding, Testing")]  // → DEFINED Testing, with a space after the comma
    [InlineData("coding,testing")]   // → DEFINED Testing, lower-case
    [InlineData("Coding,")]          // trailing comma — TryParse rejects, so must we
    [InlineData(",Coding")]          // leading comma — TryParse rejects, so must we
    public void LoadPipeline_CommaMachinePhase_ParsesNull_FollowsLegacyRestorePath(string corruptMachinePhase)
    {
        AssertCorruptMachinePhaseFollowsLegacyRestorePath("goal-comma", corruptMachinePhase);
    }

    /// <summary>
    /// THE WHITESPACE CONTRACT, pinned. <c>Enum.TryParse</c> tolerates SURROUNDING whitespace
    /// (" Coding", "Coding ", "\tCoding", "Coding\n" all parse to <c>Coding</c>), while
    /// INTERNAL whitespace ("Cod ing") is rejected by it. The persisted value is always written
    /// by <c>Phase.ToString()</c>, so ANY whitespace means a corrupt row — every form below
    /// therefore loads as null and restores through the LEGACY path.
    /// </summary>
    [Theory]
    [InlineData(" Coding")]     // leading space — TryParse WOULD accept it
    [InlineData("Coding ")]     // trailing space — TryParse WOULD accept it
    [InlineData("  Coding  ")]  // surrounded — TryParse WOULD accept it
    [InlineData("\tCoding")]    // leading tab — TryParse WOULD accept it
    [InlineData("Coding\t")]    // trailing tab — TryParse WOULD accept it
    [InlineData("Coding\n")]    // trailing newline — TryParse WOULD accept it
    [InlineData("Cod ing")]     // internal space — TryParse rejects, so must we
    [InlineData("Cod\ting")]    // internal tab — TryParse rejects, so must we
    public void LoadPipeline_WhitespaceMachinePhase_ParsesNull_FollowsLegacyRestorePath(string corruptMachinePhase)
    {
        AssertCorruptMachinePhaseFollowsLegacyRestorePath("goal-whitespace", corruptMachinePhase);
    }

    /// <summary>
    /// THE SHARED CORRUPT-ROW ASSERTION: a persisted row carrying <paramref name="corruptMachinePhase"/>
    /// with the repeated-phase plan and phase_occurrence 2 must load with
    /// <c>MachinePhase == null</c> (the occurrence still CARRIED but NOT trusted) and restore
    /// through the LEGACY <c>RestoreFromPlan</c> path — queue [Testing, Merging], completed
    /// empty, capture (Coding, 1, true) — exactly as an old row with machine_phase NULL.
    /// </summary>
    private void AssertCorruptMachinePhaseFollowsLegacyRestorePath(string goalId, string corruptMachinePhase)
    {
        var planJson = """{"phases":["Coding","Testing","Coding","Merging"],"phaseInstructions":{},"phaseTiers":{}}""";
        _dbContext.Pipelines.Add(new PipelineEntity
        {
            GoalId = goalId,
            Description = "Corrupt machine_phase row",
            GoalJson = $$"""{"id":"{{goalId}}","description":"corrupt row goal","repositories":[]}""",
            Phase = "Coding",
            PlanJson = planJson,
            MetricsJson = "{}",
            CreatedAt = "2025-06-15T10:00:00.0000000Z",
            RoleSessionsJson = "{}",
            PhaseOccurrence = 2,
            MachinePhase = corruptMachinePhase,
        });
        _dbContext.SaveChanges();

        var snapshot = _store.LoadPipeline(goalId);
        Assert.NotNull(snapshot);
        // THE PARSING CONTRACT: every unrecognized value — numeric (any form), comma
        // expression, whitespace form, or garbage — yields MachinePhase == null, never an
        // invented (or falsely combined) machine phase.
        Assert.Null(snapshot!.MachinePhase);
        // The occurrence is still carried faithfully on the snapshot…
        Assert.Equal(2, snapshot.PhaseOccurrence);

        // …but the restore takes the LEGACY path: the pair does not match (null != Coding),
        // so the untrustworthy occurrence is NOT honored.
        var restored = new GoalPipeline(snapshot);
        Assert.Equal([GoalPhase.Testing, GoalPhase.Merging], restored.StateMachine.RemainingPhases);
        Assert.Empty(restored.StateMachine.CompletedPhases);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true),
            restored.CaptureMachinePosition());
    }

    /// <summary>
    /// Case-insensitive name parsing is PRESERVED by the strict recognition: a row whose
    /// machine_phase is a defined name in any case still parses to the matching phase and
    /// the matched pair takes the occurrence-aware restore.
    /// </summary>
    [Theory]
    [InlineData("CODING")]
    [InlineData("coding")]
    [InlineData("CoDiNg")]
    public void LoadPipeline_CaseVariantMachinePhase_ParsesPreserved_AndTakesPairMatchPath(string machinePhase)
    {
        var planJson = """{"phases":["Coding","Testing","Coding","Merging"],"phaseInstructions":{},"phaseTiers":{}}""";
        _dbContext.Pipelines.Add(new PipelineEntity
        {
            GoalId = "goal-case-" + machinePhase.ToLowerInvariant(),
            Description = "Case-variant machine phase",
            GoalJson = """{"id":"case","description":"case row goal","repositories":[]}""",
            Phase = "Coding",
            PlanJson = planJson,
            MetricsJson = "{}",
            CreatedAt = "2025-06-15T10:00:00.0000000Z",
            RoleSessionsJson = "{}",
            PhaseOccurrence = 2,
            MachinePhase = machinePhase,
        });
        _dbContext.SaveChanges();

        var goalId = "goal-case-" + machinePhase.ToLowerInvariant();
        var snapshot = _store.LoadPipeline(goalId);
        Assert.NotNull(snapshot);
        // Strict recognition keeps the case-insensitive parse for DEFINED names.
        Assert.Equal(GoalPhase.Coding, snapshot!.MachinePhase);

        // The matched pair takes the occurrence-aware path: restored at occurrence 2.
        var restored = new GoalPipeline(snapshot);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 2, OccurrenceFound: true),
            restored.CaptureMachinePosition());
        Assert.Equal([GoalPhase.Merging], restored.StateMachine.RemainingPhases);
        Assert.Equal([GoalPhase.Coding, GoalPhase.Testing], restored.StateMachine.CompletedPhases);
    }

    /// <summary>
    /// EVERY canonical <see cref="GoalPhase"/> name still round-trips through the parser —
    /// the canonical-name recognition must accept the full member set (and its lower-case
    /// form), not just the phases the other vectors happen to use.
    /// </summary>
    [Fact]
    public void LoadPipeline_EveryCanonicalPhaseName_ParsesBackToItsPhase()
    {
        foreach (var phase in Enum.GetValues<GoalPhase>())
        {
            var goalId = $"goal-canon-{phase}";
            _dbContext.Pipelines.Add(new PipelineEntity
            {
                GoalId = goalId,
                Description = "Canonical name row",
                GoalJson = $$"""{"id":"{{goalId}}","description":"canonical row goal","repositories":[]}""",
                Phase = "Coding",
                MetricsJson = "{}",
                CreatedAt = "2025-06-15T10:00:00.0000000Z",
                RoleSessionsJson = "{}",
                PhaseOccurrence = 1,
                MachinePhase = phase.ToString(),
            });
            _dbContext.SaveChanges();

            var snapshot = _store.LoadPipeline(goalId);
            Assert.NotNull(snapshot);
            Assert.Equal(phase, snapshot!.MachinePhase);

            // …and the lower-case form of the same name parses identically.
            var lowerId = $"goal-canon-lower-{phase}";
            _dbContext.Pipelines.Add(new PipelineEntity
            {
                GoalId = lowerId,
                Description = "Canonical name row (lower-case)",
                GoalJson = $$"""{"id":"{{lowerId}}","description":"canonical row goal","repositories":[]}""",
                Phase = "Coding",
                MetricsJson = "{}",
                CreatedAt = "2025-06-15T10:00:00.0000000Z",
                RoleSessionsJson = "{}",
                PhaseOccurrence = 1,
                MachinePhase = phase.ToString().ToLowerInvariant(),
            });
            _dbContext.SaveChanges();

            var lowerSnapshot = _store.LoadPipeline(lowerId);
            Assert.NotNull(lowerSnapshot);
            Assert.Equal(phase, lowerSnapshot!.MachinePhase);
        }
    }
}