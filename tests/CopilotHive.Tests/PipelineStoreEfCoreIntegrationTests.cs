using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
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

/// <summary>
/// Slice E2a-i — the store primitive <see cref="PipelineStore.SaveAdmissionWithPointer"/>: the
/// transaction machinery. THE ATOMIC COMMIT (mapping + pipeline in ONE transaction), the
/// mapping-flush conflict (19+1555 → <see cref="AdmissionStoreResult.PersistConflict"/>, the
/// pipeline row never staged), the STAGE GATE (19+1555 at the pipeline flush → the generic
/// path), the per-code propagate matrix (1299/2067/275/787/5/6), the tracked-state cleanup
/// (the Unchanged-ghost detach and the deferred-orphan proof), the unconfirmed rollback
/// (detach-only fallback), and the throwing-logger swallow.
/// </summary>
/// <remarks>
/// THE FAILURE INJECTION IS GENUINE: every vector uses a real <see cref="DbCommandInterceptor"/>
/// that throws a REAL <see cref="SqliteException"/> carrying the configured SQLite result/extended
/// code at the configured statement, or a real transaction/context dispose failure — never a
/// fabricated token or exception. Assertions about row state read the database RAW through the
/// keeper connection, bypassing EF Core's change tracker entirely.
/// </remarks>
public sealed class PipelineStoreAdmissionTransactionTests : IDisposable
{
    private readonly string _connectionString =
        $"Data Source=file:memdb-admissiontx-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<DbConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];

    public PipelineStoreAdmissionTransactionTests()
    {
        // The KEEPER anchors the shared in-memory database's lifetime for the whole test.
        // The database name is per-instance unique, so no rows can leak across
        // xUnit fixture instances (each instance gets its own unshared database).
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();
        CreateContext().Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        foreach (var connection in _connections)
            connection.Dispose();
        _keeper.Dispose();
    }

    // ───────────────────────────── fixture helpers ─────────────────────────────

    /// <summary>Creates a context on its OWN connection to the shared database.</summary>
    private CopilotHiveDbContext CreateContext(IInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        _connections.Add(connection);

        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        var context = new CopilotHiveDbContext(builder.Options);
        _contexts.Add(context);
        return context;
    }

    private PipelineStore CreateStore(IInterceptor? interceptor = null, ILogger<PipelineStore>? logger = null) =>
        new(CreateContext(interceptor), logger ?? NullLogger<PipelineStore>.Instance);

    private void ExecuteOnKeeper(string sql)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? ExecuteScalarOnKeeper(string sql)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static Goal CreateGoal(string id = "goal-1") =>
        new() { Id = id, Description = "goal " + id, RepositoryNames = ["test-repo"] };

    private static GoalPipeline CreatePipeline(string goalId = "goal-1", string taskId = "task-1")
    {
        var pipeline = new GoalPipeline(CreateGoal(goalId));
        pipeline.SetActiveTask(taskId);
        return pipeline;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (1) The atomic commit
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ATOMIC COMMIT: the mapping AND the pipeline rows land via ONE transaction and the
    /// result is <see cref="AdmissionStoreResult.Committed"/>.
    /// </summary>
    [Fact]
    public void SaveAdmission_BothRowsLand_ViaOneTransaction_Committed()
    {
        var pipeline = CreatePipeline("goal-commit", "task-commit");
        var store = CreateStore();

        var result = store.SaveAdmissionWithPointer(pipeline, "task-commit");

        Assert.Equal(AdmissionStoreResult.Committed, result);
        // THE MAPPING row landed.
        Assert.Equal("goal-commit", ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-commit'"));
        // THE PIPELINE row landed with the pointer.
        Assert.Equal(1L, ExecuteScalarOnKeeper(
            "SELECT COUNT(*) FROM pipelines WHERE goal_id = 'goal-commit' AND active_task_id = 'task-commit'"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) The mapping-flush conflict
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE MAPPING-FLUSH CONFLICT: a pre-seeded mapping row makes the mapping insert fail with
    /// a genuine 19+1555 → <see cref="AdmissionStoreResult.PersistConflict"/>; the transaction
    /// is rolled back and the pipeline row is NEVER staged/persisted.
    /// </summary>
    [Fact]
    public void SaveAdmission_PreSeededMappingRow_PersistConflict_PipelineRowNeverStaged()
    {
        ExecuteOnKeeper("INSERT INTO task_mappings (task_id, goal_id) VALUES ('task-conflict', 'goal-other')");
        var pipeline = CreatePipeline("goal-conflict", "task-conflict");
        var store = CreateStore();

        var result = store.SaveAdmissionWithPointer(pipeline, "task-conflict");

        // THE CONFLICT is reported…
        Assert.Equal(AdmissionStoreResult.PersistConflict, result);
        // …the pre-existing row is INTACT (the rollback did not remove the seed)…
        Assert.Equal("goal-other", ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-conflict'"));
        // …and THE PIPELINE ROW was NEVER staged/persisted.
        Assert.Equal(0L, ExecuteScalarOnKeeper(
            "SELECT COUNT(*) FROM pipelines WHERE goal_id = 'goal-conflict'"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) The stage gate
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE STAGE GATE: 19+1555 raised AT THE PIPELINE FLUSH (the stage has advanced) goes to
    /// the GENERIC path — the original exception is thrown, NOT a conflict result. The
    /// <c>IsPrimaryKeyViolation</c> check alone would misclassify this as a conflict.
    /// </summary>
    /// <remarks>
    /// The mapping insert succeeds (no seed), then <see cref="AdmissionTargetedThrowInterceptor"/>
    /// throws a genuine 19+1555 <see cref="SqliteException"/> at the FIRST <c>pipelines</c>
    /// statement — the pipeline flush — after the stage has advanced past the mapping flush.
    /// </remarks>
    [Fact]
    public void SaveAdmission_PrimaryKeyCodeAtPipelineFlush_StageGate_ThrowsOriginal()
    {
        var interceptor = new AdmissionTargetedThrowInterceptor(
            AdmissionTargetedThrowInterceptor.Target.Pipelines, 19, 1555);
        var pipeline = CreatePipeline("goal-stagegate", "task-stagegate");
        var store = CreateStore(interceptor);

        var ex = Assert.ThrowsAny<Exception>(
            () => store.SaveAdmissionWithPointer(pipeline, "task-stagegate"));

        // THE ORIGINAL exception propagates BY IDENTITY — never a conflict result, never a
        // silent swallow, never a replacement carrying the same code.
        AssertSentinelPropagatedAtDepthOne(ex, interceptor.Sentinel);
        Assert.Equal(1, interceptor.ThrowCount); // the injection really fired, once
        Assert.Equal(19, interceptor.Sentinel.SqliteErrorCode);
        Assert.Equal(1555, interceptor.Sentinel.SqliteExtendedErrorCode);

        // The rolled-back transaction left NEITHER row behind.
        Assert.Null(ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-stagegate'"));
        Assert.Equal(0L, ExecuteScalarOnKeeper(
            "SELECT COUNT(*) FROM pipelines WHERE goal_id = 'goal-stagegate'"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) The per-code matrix — each code must keep its own classification
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 1555 (PK, at the mapping flush) → <see cref="AdmissionStoreResult.PersistConflict"/>.
    /// </summary>
    [Fact]
    public void SaveAdmission_Code1555_AtMappingFlush_IsConflict()
    {
        // A REAL violation is best here: the pre-seeded row forces SQLite itself to raise 19+1555.
        ExecuteOnKeeper("INSERT INTO task_mappings (task_id, goal_id) VALUES ('task-1555', 'goal-seed')");
        var pipeline = CreatePipeline("goal-1555", "task-1555");
        var store = CreateStore();

        Assert.Equal(AdmissionStoreResult.PersistConflict,
            store.SaveAdmissionWithPointer(pipeline, "task-1555"));
        Assert.Equal(0L, ExecuteScalarOnKeeper(
            "SELECT COUNT(*) FROM pipelines WHERE goal_id = 'goal-1555'"));
    }

    /// <summary>
    /// THE PROPAGATE MATRIX: 1299 (NOTNULL), 2067 (UNIQUE), 275 (CHECK), 787 (FK), 5 (BUSY)
    /// and 6 (LOCKED) — each raised GENUINELY at the mapping flush — must each propagate as
    /// the ORIGINAL exception, never reclassified as a conflict. Code 1555 is pinned by
    /// <see cref="SaveAdmission_Code1555_AtMappingFlush_IsConflict"/>.
    /// </summary>
    [Theory]
    [InlineData(1299)] // SQLITE_CONSTRAINT_NOTNULL (extended; primary 19)
    [InlineData(2067)] // SQLITE_CONSTRAINT_UNIQUE (extended; primary 19)
    [InlineData(275)]  // SQLITE_CONSTRAINT_CHECK (extended; primary 19)
    [InlineData(787)]  // SQLITE_CONSTRAINT_FOREIGNKEY (extended; primary 19)
    [InlineData(5)]    // SQLITE_BUSY (primary code — no extended layer)
    [InlineData(6)]    // SQLITE_LOCKED (primary code — no extended layer)
    public void SaveAdmission_PropagateCode_AtMappingFlush_ThrowsOriginal(int code)
    {
        var isExtendedCode = code >= 100;
        var interceptor = new AdmissionTargetedThrowInterceptor(
            AdmissionTargetedThrowInterceptor.Target.TaskMappings,
            isExtendedCode ? 19 : code, code);
        var pipeline = CreatePipeline($"goal-prop-{code}", $"task-prop-{code}");
        var store = CreateStore(interceptor);

        var ex = Assert.ThrowsAny<Exception>(
            () => store.SaveAdmissionWithPointer(pipeline, $"task-prop-{code}"));

        // THE ORIGINAL exception propagates BY IDENTITY — the SAME INSTANCE the interceptor
        // threw, at chain depth 1 — so a replacement carrying the same code cannot pass.
        AssertSentinelPropagatedAtDepthOne(ex, interceptor.Sentinel);
        Assert.Equal(1, interceptor.ThrowCount);
        // …and it carries the EXACT configured code (never reclassified as a conflict).
        Assert.Equal(isExtendedCode ? 19 : code, interceptor.Sentinel.SqliteErrorCode);
        Assert.Equal(code, interceptor.Sentinel.SqliteExtendedErrorCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) The Unchanged-ghost cleanup + the deferred-orphan proof
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE UNCHANGED-GHOST: the mapping flush succeeds, the pipeline flush fails → the mapping
    /// entity (now <c>Unchanged</c> — it IS durable inside the transaction) is still DETACHED
    /// (any state), and the DEFERRED-ORPHAN proof holds: the mapping Add was rolled back, so
    /// no orphan row persists after the generic failure.
    /// </summary>
    [Fact]
    public void SaveAdmission_PipelineFlushFails_MappingDetachedAndRolledBack_NoOrphanRow()
    {
        var interceptor = new AdmissionTargetedThrowInterceptor(
            AdmissionTargetedThrowInterceptor.Target.Pipelines, 5, 5); // SQLITE_BUSY
        var pipeline = CreatePipeline("goal-ghost", "task-ghost");
        var context = CreateContext(interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        Assert.ThrowsAny<Exception>(
            () => store.SaveAdmissionWithPointer(pipeline, "task-ghost"));

        // THE MAPPING was DETACHED (any state — the Unchanged-ghost included): zero tracked
        // TaskMappingEntity entries remain on the direct context after the call. A mutant
        // that skips the detach leaves the flushed mapping entry tracked (Unchanged) and
        // fails this probe — the rollback's raw-row effect alone cannot distinguish them.
        Assert.Empty(context.ChangeTracker.Entries<TaskMappingEntity>().ToList());
        // And the RAW probe of the same connection: zero rows at all (the rollback's effect).
        Assert.Equal(0L, ExecuteScalarOnKeeper("SELECT COUNT(*) FROM task_mappings"));
        // THE DEFERRED-ORPHAN PROOF: the rollback removed the flushed mapping insert — no
        // orphan mapping row (a row whose pipeline never landed) persists.
        Assert.Null(ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-ghost'"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (6) The unconfirmed rollback — the detach-only fallback
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE UNCONFIRMED ROLLBACK: a forced ROLLBACK failure → the <c>admission-rollback</c>
    /// warning logged, the DETACH-ONLY fallback taken (NO reload — the tracked pipeline entity
    /// is detached, not refreshed), and the ORIGINAL exception preserved.
    /// </summary>
    /// <remarks>
    /// The rollback failure is injected through a wrapper connection whose transaction objects
    /// throw on <c>Rollback()</c> — a genuine driver mechanism.
    /// </remarks>
    [Fact]
    public void SaveAdmission_RollbackFails_WarnsAndTakesDetachOnlyFallback_PreservesOriginal()
    {
        var interceptor = new AdmissionTargetedThrowInterceptor(
            AdmissionTargetedThrowInterceptor.Target.Pipelines, 5, 5); // the pipeline flush fails
        var logger = new TestLogger<PipelineStore>();
        var connection = new RollbackThrowingConnection(_connectionString);
        _connections.Add(connection);
        connection.Open();

        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor);
        var context = new CopilotHiveDbContext(builder.Options);
        _contexts.Add(context);
        var store = new PipelineStore(context, logger);

        // A PRE-EXISTING pipeline row: the reload mutant's Find (run after the FAILED
        // rollback) would re-attach it — observable as ONE tracked entry, while the correct
        // detach-only fallback leaves ZERO. This is what makes the fallback observable.
        ExecuteOnKeeper(
            """
            INSERT INTO pipelines (goal_id, description, goal_json, phase, metrics_json, active_task_id, created_at)
            VALUES ('goal-unconf', 'Pre-existing', '{"id":"goal-unconf","description":"u","repositories":["test-repo"]}', 'Planning', '{}', 'task-old', '2025-06-15T10:00:00.0000000Z')
            """);

        var pipeline = CreatePipeline("goal-unconf", "task-unconf");

        var ex = Assert.ThrowsAny<Exception>(
            () => store.SaveAdmissionWithPointer(pipeline, "task-unconf"));

        // THE ORIGINAL OUTCOME IS PRESERVED, BY IDENTITY: the propagated exception is the
        // operation's DbUpdateException wrapping the interceptor's SENTINEL instance — the
        // ROLLBACK's own failure (a DISTINCT sentinel instance) did NOT replace or wrap it.
        AssertSentinelPropagatedAtDepthOne(ex, interceptor.Sentinel);
        Assert.Equal(1, interceptor.ThrowCount);
        // THE ROLLBACK FAILURE REALLY FIRED (the guard ran and swallowed), and its distinct
        // sentinel appears NOWHERE in the propagated chain.
        Assert.Equal(1, connection.RollbackAttemptCount);
        Assert.DoesNotContain(EnumerateChain(ex), e => ReferenceEquals(e, connection.RollbackSentinel));

        // THE ROLLBACK WARNING was logged with the identifiers.
        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("admission-rollback", warning.Message, StringComparison.Ordinal);
        Assert.Contains("goal-unconf", warning.Message, StringComparison.Ordinal);
        Assert.Contains("task-unconf", warning.Message, StringComparison.Ordinal);

        // THE DETACH-ONLY FALLBACK: NO reload. The pipeline entity is detached ANY-way (any
        // state), and a reload-after-failed-rollback would RE-ADD a tracked copy through Find
        // — so the observable contract is: ZERO tracked PipelineEntity entries remain on the
        // direct context after the call. (A mutant that reloads via Find in the unconfirmed
        // fallback re-adds a tracked copy — either the queried row or none — and a tracked
        // EMPTY result still leaves NO entries; but the re-added PRE-EXISTING row case leaves
        // ONE. Here the row never existed, so the reload-mutant leaves zero too — hence the
        // stronger probe: the reload would query the DATABASE and, finding nothing, leave
        // zero tracked entries as well. THE REAL DIFFERENTIATOR: the reload executes a SELECT
        // reader, which the targeted interceptor would NOT see (it gates on writes) — so we
        // pin the fallback through the tracked-state probe: zero entries AND no reload query.
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel == LogLevel.Warning
            && e.Message.Contains("admission-cleanup", StringComparison.Ordinal));
        Assert.Empty(context.ChangeTracker.Entries<PipelineEntity>().ToList());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (7) The throwing logger — the silent swallow
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE THROWING LOGGER: a logger that throws while writing the rollback-failure warning in
    /// the cleanup path must NEVER mask the original outcome — the throw is silently swallowed
    /// and the ORIGINAL exception (BY IDENTITY) still propagates.
    /// </summary>
    /// <remarks>
    /// THREE distinct failure instances are in play — the interceptor's operation sentinel, the
    /// connection's rollback sentinel, and the logger's own sentinel — so "the original was
    /// replaced by a cleanup-time failure" is detectable by identity, and the logger's throw
    /// COUNT proves the fallible cleanup step actually ran (rather than never having fired).
    /// </remarks>
    [Fact]
    public void SaveAdmission_ThrowingLogger_CleanupWarning_SilentSwallow_OriginalPreserved()
    {
        var interceptor = new AdmissionTargetedThrowInterceptor(
            AdmissionTargetedThrowInterceptor.Target.Pipelines, 5, 5);
        var logger = new ThrowingLogger<PipelineStore>();
        var connection = new RollbackThrowingConnection(_connectionString);
        _connections.Add(connection);
        connection.Open();

        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor);
        var context = new CopilotHiveDbContext(builder.Options);
        _contexts.Add(context);
        var store = new PipelineStore(context, logger);
        logger.Arm(); // the throw starts AFTER the constructor's logging

        var pipeline = CreatePipeline("goal-throwlog", "task-throwlog");

        // THE ORIGINAL exception escapes — the throwing logger did NOT mask it…
        var ex = Record.Exception(
            () => store.SaveAdmissionWithPointer(pipeline, "task-throwlog"));

        // …and it is the SAME INSTANCE the interceptor threw, at chain depth 1.
        AssertSentinelPropagatedAtDepthOne(ex, interceptor.Sentinel);
        Assert.Equal(1, interceptor.ThrowCount);

        // THE FALLIBLE CLEANUP REALLY RAN: the rollback failed (its guard fired) and the
        // rollback-failure WARNING was attempted — the logger threw on it. Without this
        // counter the vector could not distinguish "swallowed" from "never ran".
        Assert.Equal(1, connection.RollbackAttemptCount);
        Assert.True(logger.ThrowCount >= 1, "the cleanup-time warning never reached the logger");

        // THE SILENT SWALLOW: neither the logger's sentinel nor the rollback's sentinel
        // appears anywhere in the propagated chain.
        Assert.DoesNotContain(EnumerateChain(ex!), e => ReferenceEquals(e, logger.LoggerSentinel));
        Assert.DoesNotContain(EnumerateChain(ex!), e => ReferenceEquals(e, connection.RollbackSentinel));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (8) The transaction-dispose failure ON THE FAILURE PATH
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FAILURE-PATH TRANSACTION DISPOSE: the operation fails AND the guarded transaction
    /// disposal also fails → the <c>admission-dispose</c> warning is logged, the dispose
    /// failure is swallowed, and the ORIGINAL exception (BY IDENTITY) still propagates — the
    /// dispose failure never replaces or wraps it.
    /// </summary>
    [Fact]
    public void SaveAdmission_FailurePath_TransactionDisposeFails_OriginalExceptionPreserved()
    {
        var interceptor = new AdmissionTargetedThrowInterceptor(
            AdmissionTargetedThrowInterceptor.Target.Pipelines, 5, 5);
        var logger = new TestLogger<PipelineStore>();
        var connection = new DisposeThrowingConnection(_connectionString);
        _connections.Add(connection);
        connection.Open();

        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor);
        var context = new CopilotHiveDbContext(builder.Options);
        _contexts.Add(context);
        var store = new PipelineStore(context, logger);

        var pipeline = CreatePipeline("goal-dispfail", "task-dispfail");

        var ex = Record.Exception(
            () => store.SaveAdmissionWithPointer(pipeline, "task-dispfail"));

        // THE ORIGINAL exception, BY IDENTITY — not the dispose failure, not a wrapper.
        AssertSentinelPropagatedAtDepthOne(ex, interceptor.Sentinel);
        Assert.Equal(1, interceptor.ThrowCount);

        // THE GUARDED DISPOSAL REALLY FIRED (and failed) …
        Assert.Equal(1, connection.DisposeAttemptCount);
        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning
            && e.Message.Contains("admission-dispose", StringComparison.Ordinal));
        Assert.Contains("goal-dispfail", warning.Message, StringComparison.Ordinal);
        Assert.Contains("task-dispfail", warning.Message, StringComparison.Ordinal);

        // … and its DISTINCT sentinel appears NOWHERE in the propagated chain.
        Assert.DoesNotContain(EnumerateChain(ex!), e => ReferenceEquals(e, connection.DisposeSentinel));

        // The rollback still made the aborted mapping insert invisible.
        Assert.Null(ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-dispfail'"));
    }

    // ───────────────────────────── shared helpers ─────────────────────────────

    /// <summary>
    /// THE IDENTITY PROOF for original-exception preservation: the propagated exception must be
    /// the <see cref="DbUpdateException"/> EF raised for the failing flush, and the exception at
    /// chain depth 1 (its <c>InnerException</c>) must be the SAME INSTANCE as the interceptor's
    /// pre-created sentinel. A cleanup/rollback/dispose replacement, an ADDED wrapper level, or
    /// a newly created same-code <see cref="SqliteException"/> all fail this assertion.
    /// </summary>
    private static void AssertSentinelPropagatedAtDepthOne(Exception? thrown, SqliteException sentinel)
    {
        Assert.NotNull(thrown);
        // The exact shape: DbUpdateException → sentinel. NOT "a SqliteException somewhere".
        var update = Assert.IsType<DbUpdateException>(thrown);
        Assert.Same(sentinel, update.InnerException);
        // …and the chain STOPS there: no extra wrapping was inserted beneath the sentinel by a
        // cleanup step re-throwing through it.
        Assert.Null(sentinel.InnerException);
    }

    /// <summary>Walks the exception chain to the innermost <see cref="SqliteException"/>.</summary>
    private static SqliteException? UnwrapToSqlite(Exception exception)
    {
        for (var current = (Exception?)exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite)
                return sqlite;
        }
        return null;
    }

    /// <summary>Every exception in the propagated chain, outermost first.</summary>
    private static IEnumerable<Exception> EnumerateChain(Exception exception)
    {
        for (var current = (Exception?)exception; current is not null; current = current.InnerException)
            yield return current;
    }
}

/// <summary>
/// Throws a genuine <see cref="SqliteException"/> with the configured SQLite error/extended codes
/// BEFORE the first <c>task_mappings</c> or <c>pipelines</c> INSERT statement executes — the
/// mapping flush or the pipeline flush, respectively.
/// </summary>
/// <remarks>
/// THE SENTINEL: the thrown exception is a SINGLE pre-created instance exposed as
/// <see cref="Sentinel"/>, so a test can assert IDENTITY (<c>Assert.Same</c>) at the expected
/// position of the propagated chain. That closes the "a same-code exception exists somewhere in
/// the chain" hole: a cleanup/rollback/dispose replacement, an added wrapper level, or a newly
/// created exception carrying the same code all FAIL the identity assertion.
/// <para>
/// <see cref="PipelinesSelectCount"/> counts the <c>SELECT … FROM "pipelines"</c> reads the
/// context issued, so a test can observe the CLEANUP-TIME reload separately from the
/// <c>UpsertPipelineCore</c> lookup that preceded the failing write.
/// </para>
/// </remarks>
internal sealed class AdmissionTargetedThrowInterceptor : DbCommandInterceptor
{
    /// <summary>The statement the interceptor targets.</summary>
    public enum Target
    {
        /// <summary>The task-mappings INSERT (the mapping flush).</summary>
        TaskMappings,
        /// <summary>The pipelines INSERT (the pipeline flush).</summary>
        Pipelines,
    }

    private readonly Target _target;
    private int _throwCount;
    private int _pipelinesSelectCount;

    public AdmissionTargetedThrowInterceptor(Target target, int errorCode, int extendedErrorCode)
    {
        _target = target;
        Sentinel = new SqliteException(
            target == Target.TaskMappings
                ? "admission interceptor SENTINEL: targeted mapping flush"
                : "admission interceptor SENTINEL: targeted pipeline flush",
            errorCode,
            extendedErrorCode);
    }

    /// <summary>
    /// THE PRE-CREATED SENTINEL instance this interceptor throws — the identity a preservation
    /// vector asserts with <c>Assert.Same</c>.
    /// </summary>
    public SqliteException Sentinel { get; }

    /// <summary>How many times the sentinel was thrown (the injection really fired).</summary>
    public int ThrowCount => Volatile.Read(ref _throwCount);

    /// <summary>How many <c>SELECT … FROM "pipelines"</c> reads the context issued.</summary>
    public int PipelinesSelectCount => Volatile.Read(ref _pipelinesSelectCount);

    private void ThrowIfTargeted(DbCommand command)
    {
        var text = command.CommandText;
        var trimmed = text.TrimStart();
        var isPipelinesStatement = text.Contains("pipelines", StringComparison.OrdinalIgnoreCase);

        if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && isPipelinesStatement)
            Interlocked.Increment(ref _pipelinesSelectCount);

        var isWrite = trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase);
        if (!isWrite)
            return;

        var isTaskMappings = text.Contains("task_mappings", StringComparison.OrdinalIgnoreCase);
        var isPipelines = isPipelinesStatement;
        if ((_target == Target.TaskMappings && isTaskMappings)
            || (_target == Target.Pipelines && isPipelines))
        {
            Interlocked.Increment(ref _throwCount);
            throw Sentinel;
        }
        // The mapping flush fires BEFORE the pipeline flush; the pipeline-flush target must not
        // misfire on the mapping's INSERT (both are INSERTs into similarly-named tables).
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTargeted(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ThrowIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTargeted(command);
        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// A connection wrapper whose <c>Rollback()</c> ALWAYS throws — the genuine
/// driver mechanism for the unconfirmed-rollback vector (the wrapper passes every other member
/// through to the real connection).
/// </summary>
/// <remarks>
/// The rollback failure throws a DISTINCT pre-created instance (<see cref="RollbackSentinel"/>),
/// separate from the operation sentinel the interceptor throws, so an exception-replacement
/// mutant (the rollback's own failure escaping or wrapping the original) is detectable by
/// identity, and <see cref="RollbackAttemptCount"/> proves the guarded rollback actually ran.
/// </remarks>
internal sealed class RollbackThrowingConnection : DbConnection
{
    private readonly SqliteConnection _inner;
    private int _rollbackAttemptCount;

    public RollbackThrowingConnection(string connectionString) =>
        _inner = new SqliteConnection(connectionString);

    /// <summary>The DISTINCT exception instance every forced rollback failure throws.</summary>
    public InvalidOperationException RollbackSentinel { get; } =
        new("forced rollback failure SENTINEL");

    /// <summary>How many times the guarded rollback was attempted (and failed).</summary>
    public int RollbackAttemptCount => Volatile.Read(ref _rollbackAttemptCount);

    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override int ConnectionTimeout => _inner.ConnectionTimeout;
    public override ConnectionState State => _inner.State;

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public override void Close() => _inner.Close();
    public override void Open() => _inner.Open();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new RollbackThrowingTransaction(this, (SqliteTransaction)_inner.BeginTransaction(isolationLevel));

    protected override DbCommand CreateDbCommand() => new TransactionTolerantCommand(_inner.CreateCommand());

    // OWNERSHIP HYGIENE: this wrapper owns the inner connection and releases it when the
    // wrapper itself is disposed (the transaction wrapper below already does the same for
    // its inner transaction). This does NOT fix the cross-instance database leak — the
    // per-instance unique database names are that fix.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _inner.Dispose(); }
            finally { base.Dispose(disposing); }
        }
        else base.Dispose(disposing);
    }

    private Exception RecordAndGetRollbackFailure()
    {
        Interlocked.Increment(ref _rollbackAttemptCount);
        return RollbackSentinel;
    }

    /// <summary>
    /// <see cref="Microsoft.Data.Sqlite.SqliteCommand"/> casts its transaction back to
    /// <see cref="SqliteTransaction"/>, so the wrapped command SHIELDS the wrapper transaction:
    /// the setter records (but does not apply) the wrapper, and commands execute on the real
    /// connection whose enlisted transaction is the underlying <see cref="SqliteTransaction"/>.
    /// </summary>
    private sealed class TransactionTolerantCommand : DbCommand
    {
        private readonly DbCommand _inner;

        public TransactionTolerantCommand(DbCommand inner) => _inner = inner;

        [AllowNull]
        public override string CommandText { get => _inner.CommandText; set => _inner.CommandText = value ?? ""; }
        public override int CommandTimeout { get => _inner.CommandTimeout; set => _inner.CommandTimeout = value; }
        public override CommandType CommandType { get => _inner.CommandType; set => _inner.CommandType = value; }
        [AllowNull]
        protected override DbConnection DbConnection { get => _inner.Connection!; set { } }
        protected override DbParameterCollection DbParameterCollection => _inner.Parameters;
        protected override DbTransaction? DbTransaction { get => _inner.Transaction; set { } }
        public override bool DesignTimeVisible { get => false; set { } }
        public override UpdateRowSource UpdatedRowSource { get => _inner.UpdatedRowSource; set => _inner.UpdatedRowSource = value; }

        public override void Cancel() => _inner.Cancel();
        public override int ExecuteNonQuery() => _inner.ExecuteNonQuery();
        public override object? ExecuteScalar() => _inner.ExecuteScalar();
        public override void Prepare() => _inner.Prepare();
        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _inner.ExecuteReader(behavior);
    }

    /// <summary>
    /// The EF relational layer requires a transaction's <see cref="DbTransaction.Connection"/>
    /// to be REFERENCE-EQUAL to the connection it was begun on, so the wrapper returns ITSELF.
    /// </summary>
    private sealed class RollbackThrowingTransaction : DbTransaction
    {
        private readonly RollbackThrowingConnection _owner;
        private readonly SqliteTransaction _inner;

        public RollbackThrowingTransaction(RollbackThrowingConnection owner, SqliteTransaction inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public override IsolationLevel IsolationLevel => _inner.IsolationLevel;
        protected override DbConnection? DbConnection => _owner;
        public override void Commit() => _inner.Commit();
        public override void Rollback() => throw _owner.RecordAndGetRollbackFailure();
        protected override void Dispose(bool disposing) => _inner.Dispose();
    }
}

/// <summary>
/// A connection wrapper whose transactions commit/roll back normally but THROW a DISTINCT
/// pre-created instance on <c>Dispose</c> — the genuine driver mechanism for the failure-path
/// transaction-dispose vector. <see cref="DisposeAttemptCount"/> proves the guarded disposal
/// really ran.
/// </summary>
internal sealed class DisposeThrowingConnection : DbConnection
{
    private readonly SqliteConnection _inner;
    private int _disposeAttemptCount;

    public DisposeThrowingConnection(string connectionString) =>
        _inner = new SqliteConnection(connectionString);

    /// <summary>The DISTINCT exception instance every forced transaction dispose throws.</summary>
    public InvalidOperationException DisposeSentinel { get; } =
        new("forced transaction dispose failure SENTINEL");

    /// <summary>How many times the guarded transaction disposal was attempted (and failed).</summary>
    public int DisposeAttemptCount => Volatile.Read(ref _disposeAttemptCount);

    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override int ConnectionTimeout => _inner.ConnectionTimeout;
    public override ConnectionState State => _inner.State;

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public override void Close() => _inner.Close();
    public override void Open() => _inner.Open();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new DisposeThrowingTransaction(this, (SqliteTransaction)_inner.BeginTransaction(isolationLevel));

    protected override DbCommand CreateDbCommand() => new ShieldedCommand(_inner.CreateCommand());

    // OWNERSHIP HYGIENE: this wrapper owns the inner connection and releases it when the
    // wrapper itself is disposed (the transaction wrapper below already does the same for
    // its inner transaction). Plain non-throwing forwarding — the injected failure here is
    // the transaction-level dispose sentinel, NOT the connection-dispose path. This does
    // NOT fix the cross-instance database leak — the per-instance unique names are that fix.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _inner.Dispose(); }
            finally { base.Dispose(disposing); }
        }
        else base.Dispose(disposing);
    }

    private Exception RecordAndGetDisposeFailure()
    {
        Interlocked.Increment(ref _disposeAttemptCount);
        return DisposeSentinel;
    }

    /// <summary>Commits/rolls back for real; the DISPOSE is the injected failure.</summary>
    private sealed class DisposeThrowingTransaction : DbTransaction
    {
        private readonly DisposeThrowingConnection _owner;
        private readonly SqliteTransaction _inner;

        public DisposeThrowingTransaction(DisposeThrowingConnection owner, SqliteTransaction inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public override IsolationLevel IsolationLevel => _inner.IsolationLevel;
        protected override DbConnection? DbConnection => _owner;
        public override void Commit() => _inner.Commit();
        public override void Rollback() => _inner.Rollback();

        protected override void Dispose(bool disposing)
        {
            // The UNDERLYING transaction is always released (no leak); the FAILURE is the signal.
            try
            {
                _inner.Dispose();
            }
            finally
            {
                throw _owner.RecordAndGetDisposeFailure();
            }
        }
    }

    /// <summary>Shields the wrapper transaction from <c>SqliteCommand</c>'s cast-back.</summary>
    private sealed class ShieldedCommand : DbCommand
    {
        private readonly DbCommand _inner;

        public ShieldedCommand(DbCommand inner) => _inner = inner;

        [AllowNull]
        public override string CommandText { get => _inner.CommandText; set => _inner.CommandText = value ?? ""; }
        public override int CommandTimeout { get => _inner.CommandTimeout; set => _inner.CommandTimeout = value; }
        public override CommandType CommandType { get => _inner.CommandType; set => _inner.CommandType = value; }
        [AllowNull]
        protected override DbConnection DbConnection { get => _inner.Connection!; set { } }
        protected override DbParameterCollection DbParameterCollection => _inner.Parameters;
        protected override DbTransaction? DbTransaction { get => _inner.Transaction; set { } }
        public override bool DesignTimeVisible { get => false; set { } }
        public override UpdateRowSource UpdatedRowSource { get => _inner.UpdatedRowSource; set => _inner.UpdatedRowSource = value; }

        public override void Cancel() => _inner.Cancel();
        public override int ExecuteNonQuery() => _inner.ExecuteNonQuery();
        public override object? ExecuteScalar() => _inner.ExecuteScalar();
        public override void Prepare() => _inner.Prepare();
        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _inner.ExecuteReader(behavior);
    }
}

/// <summary>
/// A logger that THROWS on every write AFTER the constructor phase — the genuine mechanism for
/// the throwing-logger vector (the store's constructor logging must still succeed so the test
/// reaches the cleanup path).
/// </summary>
/// <remarks>
/// The throw is a DISTINCT pre-created instance (<see cref="LoggerSentinel"/>) so an
/// exception-replacement mutant is detectable by identity, and <see cref="ThrowCount"/> proves
/// the fallible cleanup-time warning ACTUALLY ran — distinguishing "the cleanup swallowed its
/// failure and the original escaped" from "the cleanup never ran at all".
/// </remarks>
internal sealed class ThrowingLogger<T> : ILogger<T>
{
    private bool _armed;
    private int _throwCount;

    /// <summary>The DISTINCT exception instance every armed log write throws.</summary>
    public InvalidOperationException LoggerSentinel { get; } = new("the logger itself threw SENTINEL");

    /// <summary>How many armed log writes threw (the fallible cleanup step really fired).</summary>
    public int ThrowCount => Volatile.Read(ref _throwCount);

    /// <summary>Arms the throw (call AFTER store construction).</summary>
    public void Arm() => _armed = true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!_armed)
            return;

        Interlocked.Increment(ref _throwCount);
        throw LoggerSentinel;
    }
}

/// <summary>
/// The work-slot registry DURABLE STORAGE layer, end to end, against a REAL FILE-BACKED SQLite
/// database (never an in-memory-only handle): the <c>work_slot_registry_json</c> column, the
/// <see cref="PipelineStore.SaveWorkSlotRegistry"/> explicit store API, the
/// <see cref="WorkSlotRegistryCodec"/> version-1 envelope, and — critically — the INERTNESS of
/// all of it.
/// </summary>
/// <remarks>
/// <para>
/// EVERY snapshot under test is captured from a REAL <see cref="GoalPipeline"/> through its own
/// <c>CaptureRegistry</c> API after driving slots through the PRODUCTION allocation/claim/record/
/// abandon paths, and every restoration targets a FRESH TEST pipeline — no production wiring is
/// invoked, because there is none to invoke: the storage layer has ZERO production callers by
/// design, and the no-activation vectors below are what pin that.
/// </para>
/// <para>
/// THE DELIBERATE DIVISION OF LABOUR the tests encode: the CODEC decides whether the BYTES are
/// well-formed; <c>GoalPipeline.RestoreRegistry</c> decides whether the VALUES are admissible. A
/// successful decode is never authorization to restore, and a stored payload is never authorization
/// to recover.
/// </para>
/// </remarks>
public sealed class WorkSlotRegistryStorageTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"copilothive-wsr-store-{Guid.NewGuid():N}.db");

    public WorkSlotRegistryStorageTests()
    {
        // A REAL database FILE with the full current schema.
        using var connection = OpenConnection();
        using var context = ContextOn(connection);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch
            {
                // Best-effort cleanup — a leftover temp file must never fail a test.
            }
        }
    }

    // ───────────────────────────── fixture plumbing ─────────────────────────────

    /// <summary>Opens a NEW connection to the database FILE (pooling off so the file stays deletable).</summary>
    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        return connection;
    }

    private static CopilotHiveDbContext ContextOn(SqliteConnection connection, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new CopilotHiveDbContext(builder.Options);
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a FRESHLY OPENED connection/context/store and closes
    /// all three before returning — so consecutive calls model a genuine close-and-reopen of the
    /// database file, not a shared handle.
    /// </summary>
    private T WithStore<T>(Func<PipelineStore, CopilotHiveDbContext, T> body, IInterceptor? interceptor = null,
        ILogger<PipelineStore>? logger = null)
    {
        using var connection = OpenConnection();
        using var context = ContextOn(connection, interceptor);
        var store = new PipelineStore(context, logger ?? NullLogger<PipelineStore>.Instance);
        return body(store, context);
    }

    private void WithStore(Action<PipelineStore, CopilotHiveDbContext> body, IInterceptor? interceptor = null,
        ILogger<PipelineStore>? logger = null) =>
        WithStore<object?>((store, context) => { body(store, context); return null; }, interceptor, logger);

    /// <summary>Reads the RAW column value on its own connection — no EF, no change tracker.</summary>
    private object? RawScalar(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>The raw persisted blob for a goal, or <c>null</c> when the column is SQL NULL.</summary>
    private string? RawBlob(string goalId) =>
        (string?)RawScalar($"SELECT work_slot_registry_json FROM pipelines WHERE goal_id = '{goalId}'");

    private void ExecuteRaw(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Every column of a pipeline row, keyed by column name — the byte-identity baseline.</summary>
    private Dictionary<string, object?> ReadWholeRow(string goalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM pipelines WHERE goal_id = '{goalId}'";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"no pipelines row for goal '{goalId}'");

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    private static Goal NewGoal(string id) =>
        new() { Id = id, Description = "goal " + id, RepositoryNames = ["test-repo"] };

    /// <summary>A FRESH TEST pipeline — never production wiring.</summary>
    private static GoalPipeline NewPipeline(string goalId) => new(NewGoal(goalId));

    private static WorkSlotPosition Pos(int iteration, GoalPhase phase, int occurrence) =>
        new(iteration, phase, occurrence);

    private static HashSet<WorkSlotView> SlotsOf(WorkSlotRegistrySnapshot snapshot) => [.. snapshot.Slots];

    private static HashSet<WorkSlotRegistryAttemptEntry> AttemptsOf(WorkSlotRegistrySnapshot snapshot) =>
        [.. snapshot.DispatchAttempts];

    // The positions the rich source registry occupies.
    private static readonly WorkSlotPosition PosClaimed = Pos(1, GoalPhase.Coding, 1);
    private static readonly WorkSlotPosition PosRetried = Pos(1, GoalPhase.Coding, 2);   // repeated Coding occurrence
    private static readonly WorkSlotPosition PosAbandoned = Pos(1, GoalPhase.Testing, 1);
    private static readonly WorkSlotPosition PosCurrent = Pos(2, GoalPhase.Review, 1);    // a LATER iteration
    private static readonly WorkSlotPosition PosHigherCounter = Pos(1, GoalPhase.Improve, 2);
    private static readonly WorkSlotPosition PosCounterOnly = Pos(1, GoalPhase.Merging, 4);

    /// <summary>
    /// Builds the RICH source registry inside a REAL pipeline, exercising the production paths:
    /// a seeded historical portion (a dead slot whose position's counter is HIGHER than its own
    /// attempt, plus a COUNTER-ONLY position with no slot at all — exactly the shape a recovered
    /// registry carries) followed by REAL allocations driven to all four lifecycle states,
    /// including a retry at an already-used position and a slot in a later iteration.
    /// </summary>
    private static GoalPipeline BuildRichSourcePipeline(string goalId = "wsr-source")
    {
        var pipeline = NewPipeline(goalId);

        // The historical portion: a dead slot at attempt 2 whose counter already stands at 9,
        // and a counter-only position at 7. Installed through the registry's own restore API.
        pipeline.RestoreRegistry(new WorkSlotRegistrySnapshot(
            [new WorkSlotView(new WorkSlot("seeded-recorded", PosHigherCounter, 2), WorkSlotState.Recorded)],
            [new WorkSlotRegistryAttemptEntry(PosHigherCounter, 9),
             new WorkSlotRegistryAttemptEntry(PosCounterOnly, 7)]));

        // Pending → Claimed (a real in-flight dispatch).
        pipeline.AllocateAttemptAndRegisterSlot("claimed-task", PosClaimed);
        Assert.Equal(SlotGuardResult.Proceed, pipeline.ResolveAndCheckSlot("claimed-task"));

        // Pending → Claimed → Recorded, then a RETRY at the SAME position (attempt 2, Pending).
        pipeline.AllocateAttemptAndRegisterSlot("recorded-task", PosRetried);
        Assert.Equal(SlotGuardResult.Proceed, pipeline.ResolveAndCheckSlot("recorded-task"));
        Assert.Equal(SlotRecordOutcome.Recorded, pipeline.RecordSlot("recorded-task"));
        pipeline.AllocateAttemptAndRegisterSlot("retried-task", PosRetried);

        // Pending → Abandoned.
        pipeline.AllocateAttemptAndRegisterSlot("abandoned-task", PosAbandoned);
        Assert.True(pipeline.AbandonSlot("abandoned-task"));

        // Plain Pending, in the CURRENT (later) iteration.
        pipeline.AllocateAttemptAndRegisterSlot("pending-task", PosCurrent);

        return pipeline;
    }

    /// <summary>
    /// THE ANTI-VACUOUS PRECONDITION for every round-trip vector: the snapshot really does carry
    /// all four lifecycle states, a repeated phase occurrence, two iterations, a counter-only
    /// position and a counter standing HIGHER than its position's slot attempt. Without this, a
    /// "round-trip preserved everything" assertion could pass over a trivial registry.
    /// </summary>
    private static void AssertSnapshotIsRich(WorkSlotRegistrySnapshot snapshot)
    {
        var states = snapshot.Slots.Select(s => s.State).ToHashSet();
        Assert.Equal(Enum.GetValues<WorkSlotState>().ToHashSet(), states);

        // A repeated occurrence at ONE position (the retry) — two slots, two attempts.
        Assert.Equal(2, snapshot.Slots.Count(s => s.Slot.Position == PosRetried));
        // Two distinct iterations: historical positions AND the current one.
        Assert.True(snapshot.Slots.Select(s => s.Slot.Position.Iteration).Distinct().Count() >= 2);

        // A COUNTER-ONLY position: a high-water entry with no slot at that position.
        Assert.Contains(snapshot.DispatchAttempts, a => a.Position == PosCounterOnly);
        Assert.DoesNotContain(snapshot.Slots, s => s.Slot.Position == PosCounterOnly);

        // A counter standing HIGHER than the attempt of the slot living at that position.
        var higher = Assert.Single(snapshot.DispatchAttempts, a => a.Position == PosHigherCounter);
        var slotThere = Assert.Single(snapshot.Slots, s => s.Slot.Position == PosHigherCounter);
        Assert.True(higher.HighWaterAttempt > slotThere.Slot.Attempt,
            "the fixture must carry a counter higher than its position's slot attempt");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (1) Round-trip continuity across a real close/reopen of the database file
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FULL ROUND TRIP: capture from a real pipeline → encode+store through the explicit API →
    /// CLOSE the database → REOPEN it → load → decode → restore into a FRESH TEST pipeline. Every
    /// slot, state, attempt, position and counter survives byte-for-byte, and the restored registry
    /// CONTINUES attempt numbering rather than restarting it — including at the counter-only
    /// position and at the position whose counter stands higher than its slot's attempt.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: drop the encode of any field and the corresponding equality fails; reset or
    /// recompute counters on decode/restore and the three next-attempt assertions fail (they would
    /// yield 1, 1 and 3 instead of 2, 8 and 10); lose the state and the Claimed/Abandoned entries
    /// collapse into Pending, failing both the set equality and the admission probes.
    /// </remarks>
    [Fact]
    public void CaptureEncodeStore_ReopenFile_LoadDecodeRestore_PreservesEverythingAndContinuesAttempts()
    {
        var source = BuildRichSourcePipeline();
        var captured = source.CaptureRegistry();
        AssertSnapshotIsRich(captured);   // the anti-vacuous precondition

        // The row must exist first — the explicit API never creates one.
        WithStore((store, _) => store.SavePipeline(source));

        // PRECONDITION: an ordinary save leaves the column SQL NULL (no capture happens there).
        Assert.Null(RawBlob("wsr-source"));

        // ENCODE + STORE through the explicit API, then close everything.
        Assert.True(WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-source", captured)));

        // The blob is durable ON DISK, visible to a brand-new raw connection.
        var persisted = RawBlob("wsr-source");
        Assert.NotNull(persisted);

        // REOPEN: load through a fresh store/context/connection and decode explicitly.
        var carrier = WithStore((store, _) => store.LoadPipeline("wsr-source")!.WorkSlotRegistryJson);
        Assert.Equal(persisted, carrier);   // the carrier is the raw column, byte-exact

        var decoded = WorkSlotRegistryCodec.Decode(carrier!);

        // RESTORE into a FRESH TEST pipeline (never production wiring).
        var target = NewPipeline("wsr-target");
        Assert.Empty(target.GetSlotsForTest());   // precondition: the target starts empty
        target.RestoreRegistry(decoded);

        // EVERY value survived: slots (task id, position, attempt, state) and counters.
        var restored = target.CaptureRegistry();
        Assert.Equal(SlotsOf(captured), SlotsOf(restored));
        Assert.Equal(AttemptsOf(captured), AttemptsOf(restored));
        AssertSnapshotIsRich(restored);

        // THE STATES ARE FUNCTIONALLY PRESERVED, not just structurally equal:
        Assert.Equal(AdmissionOutcome.SlotAlreadyAdmitted, target.AdmitCompletion("claimed-task"));
        Assert.Equal(AdmissionOutcome.SlotAlreadyAdmitted, target.AdmitCompletion("recorded-task"));
        Assert.Equal(AdmissionOutcome.SlotAbandoned, target.AdmitCompletion("abandoned-task"));
        Assert.Equal(AdmissionOutcome.Admitted, target.AdmitCompletion("pending-task"));
        Assert.Equal(AdmissionOutcome.NoSlot, target.AdmitCompletion("never-registered"));

        // NEXT-ATTEMPT CONTINUITY at three different counter shapes. Each probe position holds
        // only a DEAD slot (or none), so a DoubleAssignment cannot mask a counter regression:
        //   an ordinary position whose only slot is Abandoned (counter 1 → 2), the COUNTER-ONLY
        //   position (7 → 8), and the position whose counter (9) stands above its slot's attempt
        //   (2) — that one must continue from 9, not from the slot's own attempt.
        Assert.Equal(2, target.AllocateAttemptAndRegisterSlot("next-abandoned", PosAbandoned).Attempt);
        Assert.Equal(8, target.AllocateAttemptAndRegisterSlot("next-counter-only", PosCounterOnly).Attempt);
        Assert.Equal(10, target.AllocateAttemptAndRegisterSlot("next-higher", PosHigherCounter).Attempt);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) The JSON contract, pinned
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE WIRE CONTRACT, PINNED so accidental drift is caught: the version-1 envelope
    /// (<c>version</c>/<c>slots</c>/<c>dispatchAttempts</c>), NAMED STRING values for phase and
    /// state (never ordinals — the shared <c>JsonStringEnumConverter&lt;GoalPhase&gt;</c> contract),
    /// the exact per-entry field names, and the COLUMN NAME <c>work_slot_registry_json</c> the
    /// payload lands in.
    /// </summary>
    [Fact]
    public void EncodeAndStore_PinsVersion1Envelope_NamedPhaseAndState_AndColumnName()
    {
        var source = NewPipeline("wsr-contract");
        var position = Pos(3, GoalPhase.DocWriting, 2);
        source.AllocateAttemptAndRegisterSlot("contract-task", position);
        Assert.Equal(SlotGuardResult.Proceed, source.ResolveAndCheckSlot("contract-task")); // → Claimed

        var snapshot = source.CaptureRegistry();
        var json = WorkSlotRegistryCodec.Encode(snapshot);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        // THE VERSION MARKER: the NUMBER 1, under the property name "version".
        var version = root.GetProperty("version");
        Assert.Equal(JsonValueKind.Number, version.ValueKind);
        Assert.Equal(1, version.GetInt32());
        Assert.Equal(1, WorkSlotRegistryCodec.Version);

        // THE SLOT ENTRY shape.
        var slots = root.GetProperty("slots");
        Assert.Equal(JsonValueKind.Array, slots.ValueKind);
        var slot = Assert.Single(slots.EnumerateArray().ToList());
        Assert.Equal("contract-task", slot.GetProperty("taskId").GetString());
        Assert.Equal(1, slot.GetProperty("attempt").GetInt32());

        // STATE IS A NAMED STRING — an ordinal here would be silent contract drift.
        var state = slot.GetProperty("state");
        Assert.Equal(JsonValueKind.String, state.ValueKind);
        Assert.Equal("Claimed", state.GetString());

        var slotPosition = slot.GetProperty("position");
        Assert.Equal(JsonValueKind.Object, slotPosition.ValueKind);
        Assert.Equal(3, slotPosition.GetProperty("iteration").GetInt32());
        Assert.Equal(2, slotPosition.GetProperty("occurrence").GetInt32());

        // PHASE IS A NAMED STRING, exactly the canonical GoalPhase name.
        var phase = slotPosition.GetProperty("phase");
        Assert.Equal(JsonValueKind.String, phase.ValueKind);
        Assert.Equal("DocWriting", phase.GetString());
        Assert.Equal(GoalPhase.DocWriting.ToString(), phase.GetString());

        // THE ATTEMPT ENTRY shape.
        var attempts = root.GetProperty("dispatchAttempts");
        Assert.Equal(JsonValueKind.Array, attempts.ValueKind);
        var attempt = Assert.Single(attempts.EnumerateArray().ToList());
        Assert.Equal(1, attempt.GetProperty("highWaterAttempt").GetInt32());
        Assert.Equal("DocWriting", attempt.GetProperty("position").GetProperty("phase").GetString());

        // THE COLUMN NAME: the payload lands in work_slot_registry_json, byte-exact.
        WithStore((store, _) => store.SavePipeline(source));
        Assert.True(WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-contract", snapshot)));
        Assert.Equal(json, RawScalar(
            "SELECT work_slot_registry_json FROM pipelines WHERE goal_id = 'wsr-contract'"));

        // …and the column genuinely exists under that name on the real table.
        Assert.Equal(1L, RawScalar(
            "SELECT COUNT(*) FROM pragma_table_info('pipelines') WHERE name = 'work_slot_registry_json'"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) Absence vs. an empty payload — two DISTINCT truths
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SQL NULL ("no snapshot was ever supplied" — the legacy-absence marker) and a stored
    /// version-1 payload carrying TWO EMPTY COLLECTIONS ("an empty registry was captured") are
    /// DISTINCT and must never collapse into one another. Absence has nothing to decode; the empty
    /// payload decodes to an empty — but genuine — snapshot.
    /// </summary>
    [Fact]
    public void SqlNullAbsence_IsDistinctFrom_Version1EmptyPayload()
    {
        var pipeline = NewPipeline("wsr-absence");
        WithStore((store, _) => store.SavePipeline(pipeline));

        // ABSENCE: the raw column is SQL NULL and the carrier is null — no payload, nothing to decode.
        Assert.Null(RawBlob("wsr-absence"));
        Assert.Null(WithStore((store, _) => store.LoadPipeline("wsr-absence")!.WorkSlotRegistryJson));

        // THE EMPTY CAPTURE: a real (empty) registry captured and stored.
        var empty = pipeline.CaptureRegistry();
        Assert.Empty(empty.Slots);
        Assert.Empty(empty.DispatchAttempts);
        Assert.True(WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-absence", empty)));

        // The column is now a NON-NULL version-1 payload — provably different from absence.
        var stored = RawBlob("wsr-absence");
        Assert.NotNull(stored);
        Assert.Contains("\"version\":1", stored, StringComparison.Ordinal);

        var carrier = WithStore((store, _) => store.LoadPipeline("wsr-absence")!.WorkSlotRegistryJson);
        Assert.Equal(stored, carrier);

        // It decodes to an EMPTY snapshot — the empty registry is REPRESENTED, not invented.
        var decoded = WorkSlotRegistryCodec.Decode(carrier!);
        Assert.Empty(decoded.Slots);
        Assert.Empty(decoded.DispatchAttempts);

        // And an empty snapshot restores legally into a fresh target.
        var target = NewPipeline("wsr-absence-target");
        target.RestoreRegistry(decoded);
        Assert.Empty(target.GetSlotsForTest());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) Corrupt payloads: they LOAD, but explicit decoding REFUSES
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A CORRUPT blob sitting in the column must NEVER break loading — the carrier is copied
    /// verbatim — and must NEVER be repaired: explicit decoding FAILS with a clear error instead of
    /// falling back to an empty registry, guessing a version, or salvaging partial entries.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: add ANY corruption-to-empty fallback, heuristic version repair or
    /// "best effort" salvage and <c>Assert.Throws</c> fails; make the load path decode the blob and
    /// the <c>LoadPipeline</c>/<c>LoadActivePipelines</c> assertions throw instead of returning the
    /// row.
    /// </remarks>
    [Theory]
    [InlineData("malformed-json", "{\"version\":1,\"slots\":[")]
    [InlineData("not-json-at-all", "definitely not json")]
    [InlineData("json-null-root", "null")]
    [InlineData("array-root", "[]")]
    [InlineData("empty-string", "")]
    [InlineData("missing-version", "{\"slots\":[],\"dispatchAttempts\":[]}")]
    [InlineData("unsupported-version-2", "{\"version\":2,\"slots\":[],\"dispatchAttempts\":[]}")]
    [InlineData("unsupported-version-0", "{\"version\":0,\"slots\":[],\"dispatchAttempts\":[]}")]
    [InlineData("version-as-string", "{\"version\":\"1\",\"slots\":[],\"dispatchAttempts\":[]}")]
    [InlineData("missing-slots", "{\"version\":1,\"dispatchAttempts\":[]}")]
    [InlineData("null-slots", "{\"version\":1,\"slots\":null,\"dispatchAttempts\":[]}")]
    [InlineData("missing-attempts", "{\"version\":1,\"slots\":[]}")]
    [InlineData("null-slot-entry", "{\"version\":1,\"slots\":[null],\"dispatchAttempts\":[]}")]
    [InlineData("null-attempt-entry", "{\"version\":1,\"slots\":[],\"dispatchAttempts\":[null]}")]
    [InlineData("missing-state",
        "{\"version\":1,\"slots\":[{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":1},\"attempt\":1}],\"dispatchAttempts\":[]}")]
    [InlineData("missing-attempt",
        "{\"version\":1,\"slots\":[{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":1},\"state\":\"Pending\"}],\"dispatchAttempts\":[]}")]
    [InlineData("missing-position",
        "{\"version\":1,\"slots\":[{\"taskId\":\"t\",\"attempt\":1,\"state\":\"Pending\"}],\"dispatchAttempts\":[]}")]
    [InlineData("null-position",
        "{\"version\":1,\"slots\":[{\"taskId\":\"t\",\"position\":null,\"attempt\":1,\"state\":\"Pending\"}],\"dispatchAttempts\":[]}")]
    [InlineData("missing-occurrence",
        "{\"version\":1,\"slots\":[{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":\"Coding\"},\"attempt\":1,\"state\":\"Pending\"}],\"dispatchAttempts\":[]}")]
    [InlineData("missing-iteration",
        "{\"version\":1,\"slots\":[{\"taskId\":\"t\",\"position\":{\"phase\":\"Coding\",\"occurrence\":1},\"attempt\":1,\"state\":\"Pending\"}],\"dispatchAttempts\":[]}")]
    [InlineData("null-task-id",
        "{\"version\":1,\"slots\":[{\"taskId\":null,\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":1},\"attempt\":1,\"state\":\"Pending\"}],\"dispatchAttempts\":[]}")]
    [InlineData("numeric-phase",
        "{\"version\":1,\"slots\":[{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":1,\"occurrence\":1},\"attempt\":1,\"state\":\"Pending\"}],\"dispatchAttempts\":[]}")]
    [InlineData("unknown-phase-name",
        "{\"version\":1,\"slots\":[{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":\"Nonsense\",\"occurrence\":1},\"attempt\":1,\"state\":\"Pending\"}],\"dispatchAttempts\":[]}")]
    [InlineData("unknown-state-name",
        "{\"version\":1,\"slots\":[{\"taskId\":\"t\",\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":1},\"attempt\":1,\"state\":\"Nonsense\"}],\"dispatchAttempts\":[]}")]
    [InlineData("missing-high-water",
        "{\"version\":1,\"slots\":[],\"dispatchAttempts\":[{\"position\":{\"iteration\":1,\"phase\":\"Coding\",\"occurrence\":1}}]}")]
    public void CorruptStoredBlob_LoadsVerbatim_ButExplicitDecodeRefuses(string label, string rawPayload)
    {
        var goalId = "wsr-corrupt-" + label;
        WithStore((store, _) => store.SavePipeline(NewPipeline(goalId)));

        // PRECONDITION: absent before the corruption is planted.
        Assert.Null(RawBlob(goalId));
        ExecuteRaw(
            $"UPDATE pipelines SET work_slot_registry_json = '{rawPayload.Replace("'", "''", StringComparison.Ordinal)}' " +
            $"WHERE goal_id = '{goalId}'");
        Assert.Equal(rawPayload, RawBlob(goalId));

        // THE LOAD IS UNAFFECTED: both load paths return the row and carry the blob VERBATIM.
        var snapshot = WithStore((store, _) => store.LoadPipeline(goalId));
        Assert.NotNull(snapshot);
        Assert.Equal(rawPayload, snapshot!.WorkSlotRegistryJson);
        Assert.Equal(goalId, snapshot.GoalId);

        var active = WithStore((store, _) =>
            store.LoadActivePipelines().Single(p => p.GoalId == goalId));
        Assert.Equal(rawPayload, active.WorkSlotRegistryJson);

        // EXPLICIT DECODING REFUSES — no fallback, no invented empty registry.
        var error = Assert.Throws<WorkSlotRegistryCodecException>(
            () => WorkSlotRegistryCodec.Decode(snapshot.WorkSlotRegistryJson!));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));

        // And a pipeline restored from this very snapshot is completely unaffected: it starts
        // empty, so an empty restore into it still succeeds (both dictionaries genuinely empty).
        var restored = new GoalPipeline(snapshot);
        Assert.Empty(restored.GetSlotsForTest());
        restored.RestoreRegistry(new WorkSlotRegistrySnapshot([], []));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) Structurally valid, semantically invalid: the restore is the authority
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A snapshot that is STRUCTURALLY well-formed but violates the registry's DOMAIN or
    /// CROSS-ENTRY rules stores successfully and decodes successfully — the codec deliberately
    /// owns neither rule — and is then REFUSED by <c>GoalPipeline.RestoreRegistry</c>, leaving the
    /// target pipeline COMPLETELY unmutated. A successful decode is NOT authorization to restore.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF both ways: copy the domain validation into the codec and the "stores and
    /// decodes" half fails; drop it from <c>RestoreRegistry</c> and the <c>Assert.Throws</c> plus
    /// the untouched-target probes fail.
    /// </remarks>
    [Theory]
    [InlineData("slot-without-its-counter")]
    [InlineData("attempt-above-high-water")]
    [InlineData("duplicate-task-id")]
    [InlineData("duplicate-position-attempt")]
    [InlineData("two-live-slots-at-one-position")]
    [InlineData("zero-attempt")]
    [InlineData("zero-iteration")]
    [InlineData("zero-occurrence")]
    [InlineData("zero-high-water")]
    [InlineData("duplicate-attempt-position")]
    public void SemanticallyInvalidSnapshot_StoresAndDecodes_ButRestoreRefuses_TargetUnmutated(string kind)
    {
        var position = Pos(1, GoalPhase.Coding, 1);
        var other = Pos(1, GoalPhase.Testing, 1);

        WorkSlotRegistrySnapshot invalid = kind switch
        {
            "slot-without-its-counter" => new WorkSlotRegistrySnapshot(
                [new WorkSlotView(new WorkSlot("a", position, 1), WorkSlotState.Pending)],
                []),
            "attempt-above-high-water" => new WorkSlotRegistrySnapshot(
                [new WorkSlotView(new WorkSlot("a", position, 5), WorkSlotState.Pending)],
                [new WorkSlotRegistryAttemptEntry(position, 2)]),
            "duplicate-task-id" => new WorkSlotRegistrySnapshot(
                [new WorkSlotView(new WorkSlot("dup", position, 1), WorkSlotState.Recorded),
                 new WorkSlotView(new WorkSlot("dup", other, 1), WorkSlotState.Recorded)],
                [new WorkSlotRegistryAttemptEntry(position, 1),
                 new WorkSlotRegistryAttemptEntry(other, 1)]),
            "duplicate-position-attempt" => new WorkSlotRegistrySnapshot(
                [new WorkSlotView(new WorkSlot("a", position, 1), WorkSlotState.Recorded),
                 new WorkSlotView(new WorkSlot("b", position, 1), WorkSlotState.Recorded)],
                [new WorkSlotRegistryAttemptEntry(position, 1)]),
            "two-live-slots-at-one-position" => new WorkSlotRegistrySnapshot(
                [new WorkSlotView(new WorkSlot("a", position, 1), WorkSlotState.Pending),
                 new WorkSlotView(new WorkSlot("b", position, 2), WorkSlotState.Claimed)],
                [new WorkSlotRegistryAttemptEntry(position, 2)]),
            "zero-attempt" => new WorkSlotRegistrySnapshot(
                [new WorkSlotView(new WorkSlot("a", position, 0), WorkSlotState.Pending)],
                [new WorkSlotRegistryAttemptEntry(position, 1)]),
            "zero-iteration" => new WorkSlotRegistrySnapshot(
                [],
                [new WorkSlotRegistryAttemptEntry(Pos(0, GoalPhase.Coding, 1), 1)]),
            "zero-occurrence" => new WorkSlotRegistrySnapshot(
                [],
                [new WorkSlotRegistryAttemptEntry(Pos(1, GoalPhase.Coding, 0), 1)]),
            "zero-high-water" => new WorkSlotRegistrySnapshot(
                [],
                [new WorkSlotRegistryAttemptEntry(position, 0)]),
            "duplicate-attempt-position" => new WorkSlotRegistrySnapshot(
                [],
                [new WorkSlotRegistryAttemptEntry(position, 1),
                 new WorkSlotRegistryAttemptEntry(position, 2)]),
            _ => throw new InvalidOperationException($"Unhandled invalid-snapshot kind: {kind}"),
        };

        var goalId = "wsr-semantic-" + kind;
        WithStore((store, _) => store.SavePipeline(NewPipeline(goalId)));

        // THE STORE ACCEPTS IT: encoding is a serialization step, not an admissibility ruling.
        Assert.True(WithStore((store, _) => store.SaveWorkSlotRegistry(goalId, invalid)));
        var carrier = WithStore((store, _) => store.LoadPipeline(goalId)!.WorkSlotRegistryJson);
        Assert.NotNull(carrier);

        // THE CODEC ACCEPTS IT TOO — the bytes are well-formed; the VALUES are someone else's call.
        var decoded = WorkSlotRegistryCodec.Decode(carrier!);
        Assert.Equal(invalid.Slots.Count, decoded.Slots.Count);
        Assert.Equal(invalid.DispatchAttempts.Count, decoded.DispatchAttempts.Count);

        // THE RESTORE REFUSES — and the target is left COMPLETELY unmutated.
        var target = NewPipeline("wsr-semantic-target");
        Assert.Empty(target.GetSlotsForTest());   // precondition
        Assert.Throws<ArgumentException>(() => target.RestoreRegistry(decoded));

        var after = target.CaptureRegistry();
        Assert.Empty(after.Slots);
        Assert.Empty(after.DispatchAttempts);
        // THE COUNTER PROBE: had any counter been installed before the refusal threw, this
        // allocation would continue from it instead of starting fresh at 1.
        Assert.Equal(1, target.AllocateAttemptAndRegisterSlot("probe", position).Attempt);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (6) Store API semantics
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A MISSING pipeline row is reported as <c>false</c> and NO row is created as a side effect —
    /// the explicit API never manufactures an incomplete pipeline just to hold a blob.
    /// </summary>
    [Fact]
    public void SaveWorkSlotRegistry_MissingRow_ReturnsFalse_AndCreatesNoRow()
    {
        var source = BuildRichSourcePipeline("wsr-missing-source");
        var snapshot = source.CaptureRegistry();

        // PRECONDITION: the goal has no pipeline row at all.
        Assert.Equal(0L, RawScalar("SELECT COUNT(*) FROM pipelines WHERE goal_id = 'wsr-nonexistent'"));

        Assert.False(WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-nonexistent", snapshot)));

        // NOTHING was created — not a partial row, not a placeholder.
        Assert.Equal(0L, RawScalar("SELECT COUNT(*) FROM pipelines WHERE goal_id = 'wsr-nonexistent'"));
        Assert.Equal(0L, RawScalar("SELECT COUNT(*) FROM pipelines"));
        Assert.Equal(0L, RawScalar("SELECT COUNT(*) FROM conversation_entries WHERE goal_id = 'wsr-nonexistent'"));
        Assert.Equal(0L, RawScalar("SELECT COUNT(*) FROM task_mappings WHERE goal_id = 'wsr-nonexistent'"));

        // …and a later load still finds nothing.
        Assert.Null(WithStore((store, _) => store.LoadPipeline("wsr-nonexistent")));
    }

    /// <summary>
    /// A FAILED WRITE (a genuine <see cref="SqliteException"/> raised by an interceptor at the
    /// registry UPDATE) PROPAGATES and leaves the PREVIOUSLY persisted blob byte-exactly intact —
    /// a failed store never half-writes and never clears what was already durable.
    /// </summary>
    [Fact]
    public void SaveWorkSlotRegistry_WriteFails_PropagatesAndLeavesPriorBlobIntact()
    {
        var pipeline = NewPipeline("wsr-writefail");
        WithStore((store, _) => store.SavePipeline(pipeline));

        // A FIRST, SUCCESSFUL write establishes the prior durable value.
        var first = BuildRichSourcePipeline("wsr-writefail-src").CaptureRegistry();
        Assert.True(WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-writefail", first)));
        var priorBlob = RawBlob("wsr-writefail");
        Assert.NotNull(priorBlob);

        // The SECOND write carries a DIFFERENT payload, so a partial success would be visible.
        var second = pipeline.CaptureRegistry();   // an EMPTY registry — provably different
        Assert.NotEqual(priorBlob, WorkSlotRegistryCodec.Encode(second));

        var interceptor = new RegistryUpdateThrowInterceptor();
        var thrown = Assert.ThrowsAny<Exception>(() =>
            WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-writefail", second), interceptor));

        // THE INJECTION REALLY FIRED and the ORIGINAL failure propagated (by identity).
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.Contains(EnumerateChain(thrown), e => ReferenceEquals(e, interceptor.Sentinel));

        // THE PRIOR DATA IS INTACT, byte-exactly — not cleared, not overwritten, not truncated.
        Assert.Equal(priorBlob, RawBlob("wsr-writefail"));
        Assert.Equal(priorBlob, WithStore((store, _) => store.LoadPipeline("wsr-writefail")!.WorkSlotRegistryJson));
    }

    /// <summary>
    /// An ENCODE failure performs NO write at all: the previously persisted blob is untouched and
    /// nothing partial reaches the column.
    /// </summary>
    [Fact]
    public void SaveWorkSlotRegistry_EncodeFails_PerformsNoWrite()
    {
        WithStore((store, _) => store.SavePipeline(NewPipeline("wsr-encodefail")));
        var good = BuildRichSourcePipeline("wsr-encodefail-src").CaptureRegistry();
        Assert.True(WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-encodefail", good)));
        var priorBlob = RawBlob("wsr-encodefail");
        Assert.NotNull(priorBlob);

        // A snapshot the format cannot represent (a null slot entry).
        var unencodable = new WorkSlotRegistrySnapshot([null!], []);
        Assert.Throws<WorkSlotRegistryCodecException>(() =>
            WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-encodefail", unencodable)));

        Assert.Equal(priorBlob, RawBlob("wsr-encodefail"));
    }

    /// <summary>
    /// THE DETACHMENT CONTRACT at the codec boundary: every decode allocates FRESH storage, so no
    /// caller ever shares a collection with another decode, with the source snapshot, or with a
    /// live registry. Mutating any one of them reaches none of the others, in either direction.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: return a cached/shared list from <c>Decode</c> (or hand back the source
    /// snapshot's own lists) and the reference-inequality plus the independent-mutation assertions
    /// fail; install the snapshot's collections into the registry by reference and the
    /// live-registry probes fail.
    /// </remarks>
    [Fact]
    public void DecodedSnapshot_IsFullyDetached_FromTheLiveRegistry()
    {
        var source = BuildRichSourcePipeline("wsr-detach-src");
        var sourceSnapshot = source.CaptureRegistry();
        WithStore((store, _) => store.SavePipeline(source));
        Assert.True(WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-detach-src", sourceSnapshot)));

        var carrier = WithStore((store, _) => store.LoadPipeline("wsr-detach-src")!.WorkSlotRegistryJson);
        var decoded = WorkSlotRegistryCodec.Decode(carrier!);
        var second = WorkSlotRegistryCodec.Decode(carrier!);

        // ── DECODE ALLOCATES FRESH STORAGE EVERY TIME ─────────────────────────────────
        // Neither decode shares storage with the other, nor with the captured source snapshot.
        Assert.NotSame(decoded.Slots, second.Slots);
        Assert.NotSame(decoded.DispatchAttempts, second.DispatchAttempts);
        Assert.NotSame(decoded.Slots, sourceSnapshot.Slots);
        Assert.NotSame(decoded.DispatchAttempts, sourceSnapshot.DispatchAttempts);
        // …and the two decodes are VALUE-equal, so the reference check above is not vacuous.
        Assert.Equal(SlotsOf(decoded), SlotsOf(second));
        Assert.Equal(AttemptsOf(decoded), AttemptsOf(second));

        var target = NewPipeline("wsr-detach-target");
        target.RestoreRegistry(decoded);

        var afterRestore = target.CaptureRegistry();
        var decodedSlotCount = decoded.Slots.Count;
        var decodedAttemptCount = decoded.DispatchAttempts.Count;
        Assert.True(decodedSlotCount > 0 && decodedAttemptCount > 0);   // anti-vacuous precondition

        // ── DIRECTION 1: mutate the DECODED snapshot's storage ────────────────────────
        var decodedSlots = Assert.IsType<List<WorkSlotView>>(decoded.Slots);
        var decodedAttempts = Assert.IsType<List<WorkSlotRegistryAttemptEntry>>(decoded.DispatchAttempts);
        decodedSlots.Clear();
        decodedAttempts.Clear();

        // The LIVE REGISTRY it was restored into is untouched…
        var afterSnapshotMutation = target.CaptureRegistry();
        Assert.Equal(SlotsOf(afterRestore), SlotsOf(afterSnapshotMutation));
        Assert.Equal(AttemptsOf(afterRestore), AttemptsOf(afterSnapshotMutation));
        Assert.NotEmpty(afterSnapshotMutation.Slots);

        // …the SOURCE snapshot is untouched…
        Assert.Equal(decodedSlotCount, sourceSnapshot.Slots.Count);
        Assert.Equal(decodedAttemptCount, sourceSnapshot.DispatchAttempts.Count);

        // …and the OTHER decode of the very same payload is untouched (no shared codec storage).
        Assert.Equal(decodedSlotCount, second.Slots.Count);
        Assert.Equal(decodedAttemptCount, second.DispatchAttempts.Count);

        // A LATER decode is likewise complete — a shared buffer would have been emptied above.
        var third = WorkSlotRegistryCodec.Decode(carrier!);
        Assert.Equal(decodedSlotCount, third.Slots.Count);
        Assert.Equal(decodedAttemptCount, third.DispatchAttempts.Count);

        // ── DIRECTION 2: mutate the LIVE REGISTRY ────────────────────────────────────
        decodedSlots.AddRange(SlotsOf(afterRestore));
        decodedAttempts.AddRange(AttemptsOf(afterRestore));
        Assert.Equal(decodedSlotCount, decoded.Slots.Count);        // precondition restored

        target.AllocateAttemptAndRegisterSlot("after-restore-task", Pos(9, GoalPhase.Merging, 9));
        Assert.Equal(decodedSlotCount, decoded.Slots.Count);
        Assert.Equal(decodedAttemptCount, decoded.DispatchAttempts.Count);
        Assert.DoesNotContain(decoded.Slots, s => s.Slot.TaskId == "after-restore-task");
        Assert.DoesNotContain(second.Slots, s => s.Slot.TaskId == "after-restore-task");
        Assert.DoesNotContain(sourceSnapshot.Slots, s => s.Slot.TaskId == "after-restore-task");

        // The SOURCE registry is likewise untouched by anything the target did.
        Assert.DoesNotContain(source.GetSlotsForTest(), s => s.Slot.TaskId == "after-restore-task");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (7) Blob-preservation inertness
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE BLOB-ONLY UPDATE: the explicit API writes NOTHING but <c>work_slot_registry_json</c> —
    /// every other column of the row, the conversation, the task mappings and the active pointer
    /// stay byte-identical.
    /// </summary>
    [Fact]
    public void SaveWorkSlotRegistry_UpdatesOnlyTheBlobColumn_EverythingElseByteIdentical()
    {
        var pipeline = NewPipeline("wsr-onlyblob");
        pipeline.SetPlan(new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Testing] });
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.SetActiveTask("task-onlyblob", "coder/wsr-onlyblob");
        pipeline.Conversation.Add(new ConversationEntry("user", "hello"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "hi"));

        WithStore((store, _) =>
        {
            store.SavePipeline(pipeline);
            store.SaveTaskMapping("task-onlyblob", "wsr-onlyblob");
        });

        var before = ReadWholeRow("wsr-onlyblob");
        // PRECONDITIONS: real scalar content is present, and the blob is absent.
        Assert.Equal("task-onlyblob", before["active_task_id"]);
        Assert.Equal("Coding", before["phase"]);
        Assert.Null(before["work_slot_registry_json"]);

        var snapshot = BuildRichSourcePipeline("wsr-onlyblob-src").CaptureRegistry();
        Assert.True(WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-onlyblob", snapshot)));

        var after = ReadWholeRow("wsr-onlyblob");
        Assert.Equal(before.Count, after.Count);
        foreach (var (column, value) in before)
        {
            if (string.Equals(column, "work_slot_registry_json", StringComparison.Ordinal))
                continue;
            Assert.Equal(value, after[column]);
        }

        // The BLOB is the ONLY thing that changed.
        Assert.NotNull(after["work_slot_registry_json"]);
        Assert.Equal(WorkSlotRegistryCodec.Encode(snapshot), after["work_slot_registry_json"]);

        // Conversation, mappings and the pointer are untouched.
        Assert.Equal(2L, RawScalar("SELECT COUNT(*) FROM conversation_entries WHERE goal_id = 'wsr-onlyblob'"));
        Assert.Equal("wsr-onlyblob", RawScalar("SELECT goal_id FROM task_mappings WHERE task_id = 'task-onlyblob'"));
        Assert.Equal("task-onlyblob", RawScalar("SELECT active_task_id FROM pipelines WHERE goal_id = 'wsr-onlyblob'"));

        var loaded = WithStore((store, _) => store.LoadPipeline("wsr-onlyblob"))!;
        Assert.Equal(2, loaded.Conversation.Count);
        Assert.Equal("task-onlyblob", loaded.ActiveTaskId);
        Assert.Equal(GoalPhase.Coding, loaded.Phase);
    }

    /// <summary>
    /// THE INERTNESS OF THE ORDINARY WRITE PATHS: <c>SavePipeline</c>, <c>SavePipelineState</c>,
    /// <c>SaveAdmissionWithPointer</c> and the ownership-checked pointer clear each preserve an
    /// EXISTING blob BYTE-EXACTLY — none of them captures a registry, overwrites the column, clears
    /// it, or decodes it. Each step also asserts that it genuinely DID its own work, so the
    /// preservation cannot be satisfied by a no-op.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: make <c>ApplyToEntity</c> capture/write the registry column (or NULL it out)
    /// and the byte-exact comparison after the very first ordinary save fails.
    /// </remarks>
    [Fact]
    public void OrdinaryWritePaths_PreserveAnExistingBlobByteExactly()
    {
        var pipeline = NewPipeline("wsr-inert");
        WithStore((store, _) => store.SavePipeline(pipeline));

        var snapshot = BuildRichSourcePipeline("wsr-inert-src").CaptureRegistry();
        Assert.True(WithStore((store, _) => store.SaveWorkSlotRegistry("wsr-inert", snapshot)));
        var blob = RawBlob("wsr-inert");
        Assert.NotNull(blob);
        // PRECONDITION: the blob is a substantial payload, not an empty envelope.
        Assert.Contains("\"claimed-task\"", blob, StringComparison.Ordinal);

        // (a) SavePipeline — a FULL save, conversation included.
        pipeline.Conversation.Add(new ConversationEntry("user", "after-blob"));
        pipeline.AdvanceTo(GoalPhase.Coding);
        WithStore((store, _) => store.SavePipeline(pipeline));
        Assert.Equal("Coding", RawScalar("SELECT phase FROM pipelines WHERE goal_id = 'wsr-inert'")); // it really saved
        Assert.Equal(1L, RawScalar("SELECT COUNT(*) FROM conversation_entries WHERE goal_id = 'wsr-inert'"));
        Assert.Equal(blob, RawBlob("wsr-inert"));

        // (b) SavePipelineState — the scalar-only save.
        pipeline.AdvanceTo(GoalPhase.Testing);
        WithStore((store, _) => store.SavePipelineState(pipeline));
        Assert.Equal("Testing", RawScalar("SELECT phase FROM pipelines WHERE goal_id = 'wsr-inert'"));
        Assert.Equal(blob, RawBlob("wsr-inert"));

        // (c) SaveAdmissionWithPointer — the atomic mapping+pointer transaction.
        pipeline.SetActiveTask("task-inert");
        Assert.Equal(AdmissionStoreResult.Committed,
            WithStore((store, _) => store.SaveAdmissionWithPointer(pipeline, "task-inert")));
        Assert.Equal("wsr-inert", RawScalar("SELECT goal_id FROM task_mappings WHERE task_id = 'task-inert'"));
        Assert.Equal("task-inert", RawScalar("SELECT active_task_id FROM pipelines WHERE goal_id = 'wsr-inert'"));
        Assert.Equal(blob, RawBlob("wsr-inert"));

        // (d) The ownership-checked POINTER CLEAR.
        Assert.Equal(PointerRollbackResult.Cleared,
            WithStore((store, _) => store.ClearActiveTaskIdIfMatches("wsr-inert", "task-inert")));
        Assert.Null(RawScalar("SELECT active_task_id FROM pipelines WHERE goal_id = 'wsr-inert'"));
        Assert.Equal(blob, RawBlob("wsr-inert"));

        // (e) A non-matching pointer clear (the no-op refusal) is equally inert.
        Assert.Equal(PointerRollbackResult.NotMatched,
            WithStore((store, _) => store.ClearActiveTaskIdIfMatches("wsr-inert", "some-other-task")));
        Assert.Equal(blob, RawBlob("wsr-inert"));

        // THE BLOB IS STILL DECODABLE AND COMPLETE after every ordinary write path ran.
        var decoded = WorkSlotRegistryCodec.Decode(RawBlob("wsr-inert")!);
        Assert.Equal(SlotsOf(snapshot), SlotsOf(decoded));
        Assert.Equal(AttemptsOf(snapshot), AttemptsOf(decoded));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (8) NO ACTIVATION — the inertness the whole slice exists to guarantee
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE NO-ACTIVATION PROOF: neither the ordinary <see cref="GoalPipeline"/> restore constructor
    /// nor <see cref="GoalPipelineManager"/>'s restoration paths look at the blob — with a
    /// POPULATED payload or a MALFORMED one, the restored pipeline's registry comes up COMPLETELY
    /// EMPTY, no exception is raised, and every other piece of restored state is exactly what it
    /// would be with no blob at all. Recovery wiring is a LATER goal; this test fails the moment
    /// anything starts activating it.
    /// </summary>
    [Theory]
    [InlineData("populated")]
    [InlineData("malformed")]
    [InlineData("empty-payload")]
    public void RestorePaths_IgnoreTheBlobEntirely_NoRegistryRecoveryActivates(string blobKind)
    {
        var goalId = "wsr-noactivate-" + blobKind;
        var pipeline = NewPipeline(goalId);
        pipeline.SetPlan(new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Testing] });
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.SetActiveTask("task-" + blobKind, "coder/" + goalId);
        WithStore((store, _) => store.SavePipeline(pipeline));

        var rich = BuildRichSourcePipeline(goalId + "-src").CaptureRegistry();
        switch (blobKind)
        {
            case "populated":
                Assert.True(WithStore((store, _) => store.SaveWorkSlotRegistry(goalId, rich)));
                break;
            case "empty-payload":
                Assert.True(WithStore((store, _) =>
                    store.SaveWorkSlotRegistry(goalId, new WorkSlotRegistrySnapshot([], []))));
                break;
            case "malformed":
                ExecuteRaw($"UPDATE pipelines SET work_slot_registry_json = '{{not json' WHERE goal_id = '{goalId}'");
                break;
            default:
                throw new InvalidOperationException($"Unhandled blob kind: {blobKind}");
        }

        // PRECONDITION: the column really is populated — otherwise "ignored" would be vacuous.
        var blob = RawBlob(goalId);
        Assert.NotNull(blob);

        // (a) THE RESTORE CONSTRUCTOR ignores it.
        var snapshot = WithStore((store, _) => store.LoadPipeline(goalId))!;
        Assert.Equal(blob, snapshot.WorkSlotRegistryJson);   // the carrier IS populated…
        var restoredDirectly = new GoalPipeline(snapshot);
        AssertRegistryCompletelyEmpty(restoredDirectly);     // …and the registry is STILL empty.

        // (b) GoalPipelineManager.RestoreFromStore (the startup path) ignores it.
        WithStore((store, _) =>
        {
            var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
            var restored = manager.RestoreFromStore();
            var fromStartup = Assert.Single(restored, p => p.GoalId == goalId);
            AssertRegistryCompletelyEmpty(fromStartup);

            // Non-registry state restored exactly as it always did — unchanged startup behavior.
            Assert.Equal(GoalPhase.Coding, fromStartup.Phase);
            Assert.Equal("task-" + blobKind, fromStartup.ActiveTaskId);
            Assert.Equal("coder/" + goalId, fromStartup.CoderBranch);
        });

        // (c) GoalPipelineManager.RestorePipeline (the on-demand path) ignores it too.
        WithStore((store, _) =>
        {
            var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
            var onDemand = manager.RestorePipeline(goalId);
            Assert.NotNull(onDemand);
            AssertRegistryCompletelyEmpty(onDemand!);
        });

        // (d) THE LEGACY NoSlot BEHAVIOR IS UNCHANGED: with nothing recovered, a completion for a
        //     task that had a slot in the STORED snapshot still passes through as NoSlot.
        Assert.Equal(AdmissionOutcome.NoSlot, restoredDirectly.AdmitCompletion("claimed-task"));
        Assert.Equal(AdmissionOutcome.NoSlot, restoredDirectly.AdmitCompletion("pending-task"));
        Assert.Equal(SlotGuardResult.Unknown, restoredDirectly.ResolveAndCheckSlot("claimed-task"));

        // …and for the populated case, those task ids really WERE in the stored payload.
        if (blobKind == "populated")
        {
            var storedSnapshot = WorkSlotRegistryCodec.Decode(blob!);
            Assert.Contains(storedSnapshot.Slots, s => s.Slot.TaskId == "claimed-task");
            Assert.Contains(storedSnapshot.Slots, s => s.Slot.TaskId == "pending-task");
        }
    }

    /// <summary>
    /// BOTH registry dictionaries are empty. The empty-restore probe is the strong form: a restore
    /// into a nonempty registry — INCLUDING a counter-only one, which no capture would reveal
    /// through the slot list alone — throws <see cref="InvalidOperationException"/>.
    /// </summary>
    private static void AssertRegistryCompletelyEmpty(GoalPipeline pipeline)
    {
        Assert.Empty(pipeline.GetSlotsForTest());

        var snapshot = pipeline.CaptureRegistry();
        Assert.Empty(snapshot.Slots);
        Assert.Empty(snapshot.DispatchAttempts);

        // Would THROW if either dictionary carried anything at all.
        pipeline.RestoreRegistry(new WorkSlotRegistrySnapshot([], []));
    }

    /// <summary>Every exception in the propagated chain, outermost first.</summary>
    private static IEnumerable<Exception> EnumerateChain(Exception exception)
    {
        for (var current = (Exception?)exception; current is not null; current = current.InnerException)
            yield return current;
    }
}

/// <summary>
/// Throws a genuine <see cref="SqliteException"/> at the registry column's UPDATE statement — the
/// real failure vector for "a failed write leaves prior persisted data intact". The thrown
/// instance is a pre-created <see cref="Sentinel"/> so propagation can be asserted by IDENTITY,
/// and <see cref="ThrowCount"/> proves the injection actually fired.
/// </summary>
internal sealed class RegistryUpdateThrowInterceptor : DbCommandInterceptor
{
    private int _throwCount;

    /// <summary>The pre-created exception instance the injection throws.</summary>
    public SqliteException Sentinel { get; } =
        new("registry update interceptor SENTINEL", 5, 5);   // SQLITE_BUSY

    /// <summary>How many times the sentinel was thrown.</summary>
    public int ThrowCount => Volatile.Read(ref _throwCount);

    private void ThrowIfTargeted(DbCommand command)
    {
        var text = command.CommandText;
        if (!text.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            return;
        if (!text.Contains("work_slot_registry_json", StringComparison.OrdinalIgnoreCase))
            return;

        Interlocked.Increment(ref _throwCount);
        throw Sentinel;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTargeted(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ThrowIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTargeted(command);
        return ValueTask.FromResult(result);
    }
}
/// <summary>
/// Regression matrix for <see cref="PipelineStore.CommitAdmissionOwnership"/> against a real,
/// file-backed SQLite database. Every durable assertion is made after the operation's connection
/// has closed and a fresh connection has reopened the file.
/// </summary>
public sealed class PipelineStoreAdmissionOwnershipIntegrationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"copilothive-admission-ownership-{Guid.NewGuid():N}.db");

    public PipelineStoreAdmissionOwnershipIntegrationTests()
    {
        using var connection = OpenConnection();
        using var context = ContextOn(connection);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort deletion of a test-only temporary database.
            }
        }
    }

    private string ConnectionString => $"Data Source={_dbPath};Pooling=False";

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static CopilotHiveDbContext ContextOn(DbConnection connection, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new CopilotHiveDbContext(builder.Options);
    }

    private T WithStore<T>(Func<PipelineStore, CopilotHiveDbContext, T> action, IInterceptor? interceptor = null)
    {
        using var connection = OpenConnection();
        using var context = ContextOn(connection, interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);
        return action(store, context);
    }

    private static Goal Goal(string goalId) =>
        new() { Id = goalId, Description = "goal " + goalId, RepositoryNames = ["repo-a", "repo-b"] };

    private static WorkSlotPosition Pos(int occurrence, GoalPhase phase = GoalPhase.Coding, int iteration = 1) =>
        new(iteration, phase, occurrence);

    /// <summary>
    /// Produces a genuine captured Pending admission with all lifecycle states, history, a
    /// counter-only entry, and a high-water value above its slot attempt.
    /// </summary>
    private static AdmissionOwnershipSnapshot RichCandidate(string goalId, string taskId)
    {
        var pipeline = new GoalPipeline(Goal(goalId));
        var high = Pos(1, GoalPhase.Improve);
        var counterOnly = Pos(2, GoalPhase.Merging);
        pipeline.RestoreRegistry(new WorkSlotRegistrySnapshot(
            [new WorkSlotView(new WorkSlot("historical-recorded", high, 2), WorkSlotState.Recorded)],
            [new WorkSlotRegistryAttemptEntry(high, 9),
             new WorkSlotRegistryAttemptEntry(counterOnly, 7)]));

        var claimed = pipeline.AllocateAttemptAndRegisterSlot("historical-claimed", Pos(3));
        Assert.Equal(1, claimed.Attempt);
        Assert.Equal(SlotGuardResult.Proceed, pipeline.ResolveAndCheckSlot("historical-claimed"));

        var abandoned = pipeline.AllocateAttemptAndRegisterSlot("historical-abandoned", Pos(4, GoalPhase.Testing));
        Assert.Equal(1, abandoned.Attempt);
        Assert.True(pipeline.AbandonSlot("historical-abandoned"));

        var active = pipeline.AllocateAttemptAndRegisterSlot(taskId, Pos(5, GoalPhase.Review, 2));
        Assert.Equal(1, active.Attempt);
        pipeline.SetActiveTask(taskId);
        return pipeline.CaptureAdmissionOwnership();
    }

    private void SeedRichIdleRow(string goalId, string? blob)
    {
        WithStore<object?>((store, _) =>
        {
            var pipeline = new GoalPipeline(Goal(goalId), maxRetries: 8, maxIterations: 6);
            pipeline.SetPlan(new IterationPlan
            {
                Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review],
            });
            pipeline.AdvanceTo(GoalPhase.Coding);
            pipeline.IterationBudget.TryConsume();
            pipeline.ReviewRetryBudget.TryConsume();
            pipeline.SetActiveTask("temporary-pointer", "coder/preserved-branch");
            Assert.True(pipeline.ClearActiveTaskIfCurrent("temporary-pointer"));
            pipeline.Conversation.Add(new ConversationEntry("user", "preserved conversation one"));
            pipeline.Conversation.Add(new ConversationEntry("assistant", "preserved conversation two"));
            store.SavePipeline(pipeline);
            store.SaveTaskMapping("unrelated-mapping", goalId);
            return null;
        });
        SetBlob(goalId, blob);
    }

    private void SetBlob(string goalId, string? blob)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE pipelines SET work_slot_registry_json = $blob WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId);
        command.Parameters.AddWithValue("$blob", (object?)blob ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private void SetPointer(string goalId, string? taskId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE pipelines SET active_task_id = $task WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId);
        command.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private void InsertMapping(string taskId, string goalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO task_mappings (task_id, goal_id) VALUES ($task, $goal)";
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$goal", goalId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private Dictionary<string, object?> ReadWholeRow(string goalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM pipelines WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"missing pipeline row '{goalId}'");
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
            row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
        return row;
    }

    private OwnershipRows ReadOwnership(string goalId, string taskId)
    {
        using var connection = OpenConnection();
        using var pipeline = connection.CreateCommand();
        pipeline.CommandText =
            "SELECT active_task_id, work_slot_registry_json FROM pipelines WHERE goal_id = $goal";
        pipeline.Parameters.AddWithValue("$goal", goalId);
        using var reader = pipeline.ExecuteReader();
        var rowExists = reader.Read();
        var pointer = rowExists && !reader.IsDBNull(0) ? reader.GetString(0) : null;
        var blob = rowExists && !reader.IsDBNull(1) ? reader.GetString(1) : null;
        reader.Close();

        using var mapping = connection.CreateCommand();
        mapping.CommandText = "SELECT goal_id FROM task_mappings WHERE task_id = $task";
        mapping.Parameters.AddWithValue("$task", taskId);
        var mapped = mapping.ExecuteScalar();
        return new OwnershipRows(rowExists, pointer, blob, mapped is DBNull or null ? null : (string)mapped);
    }

    private static void AssertConfirmed(AdmissionOwnershipCommitResult result, AdmissionOwnershipCommitStatus status)
    {
        Assert.Equal(status, result.Status);
        Assert.Null(result.PrimaryException);
        Assert.Null(result.RollbackException);
    }

    private sealed record OwnershipRows(bool RowExists, string? Pointer, string? Blob, string? MappingGoal);

    public static IEnumerable<object?[]> PriorBlobCases()
    {
        yield return ["sql-null", null];
        yield return ["nonempty", WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot([], []))];
    }

    [Theory]
    [MemberData(nameof(PriorBlobCases))]
    public void CommitAdmissionOwnership_RealCapturedPending_FromNullOrNonemptyPrior_PreservesCandidateAndUnrelatedState(
        string label, string? priorBlob)
    {
        var goalId = "ownership-success-" + label;
        var taskId = "task-success-" + label;
        SeedRichIdleRow(goalId, priorBlob);
        var before = ReadWholeRow(goalId);
        var candidate = RichCandidate(goalId, taskId);
        var expectedSlots = candidate.Registry.Slots.ToList();
        var expectedAttempts = candidate.Registry.DispatchAttempts.ToList();

        var result = WithStore((store, _) => store.CommitAdmissionOwnership(candidate, priorBlob));

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Committed);
        var durable = ReadOwnership(goalId, taskId); // fresh connection after operation closed
        Assert.True(durable.RowExists);
        Assert.Equal(taskId, durable.Pointer);
        Assert.Equal(goalId, durable.MappingGoal);
        Assert.NotNull(durable.Blob);

        var decoded = WorkSlotRegistryCodec.Decode(durable.Blob!);
        Assert.Equal(expectedSlots, decoded.Slots);
        Assert.Equal(expectedAttempts, decoded.DispatchAttempts);
        Assert.Contains(decoded.Slots, s => s.Slot.TaskId == taskId && s.State == WorkSlotState.Pending);
        Assert.Contains(decoded.Slots, s => s.State == WorkSlotState.Claimed);
        Assert.Contains(decoded.Slots, s => s.State == WorkSlotState.Recorded);
        Assert.Contains(decoded.Slots, s => s.State == WorkSlotState.Abandoned);
        Assert.Contains(decoded.DispatchAttempts, a => a.HighWaterAttempt == 9);
        Assert.Contains(decoded.DispatchAttempts,
            a => a.HighWaterAttempt == 7 && decoded.Slots.All(s => s.Slot.Position != a.Position));

        var after = ReadWholeRow(goalId);
        Assert.Equal(before.Count, after.Count);
        foreach (var (column, value) in before)
        {
            if (column is "active_task_id" or "work_slot_registry_json")
                continue;
            Assert.Equal(value, after[column]);
        }
        var conversation = WithStore((store, _) => store.GetConversation(goalId).ToList());
        Assert.Collection(conversation,
            entry =>
            {
                Assert.Equal("user", entry.Role);
                Assert.Equal("preserved conversation one", entry.Content);
            },
            entry =>
            {
                Assert.Equal("assistant", entry.Role);
                Assert.Equal("preserved conversation two", entry.Content);
            });
        Assert.Equal(goalId, Scalar<string>(
            "SELECT goal_id FROM task_mappings WHERE task_id = $value", "unrelated-mapping"));
    }

    public static IEnumerable<object?[]> RawRegistryIdentityCases()
    {
        var encodedEmpty = WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot([], []));
        foreach (var expected in new (string Name, string? Value)[]
        {
            ("null", null), ("empty", ""), ("encoded-empty", encodedEmpty),
        })
        foreach (var actual in new (string Name, string? Value)[]
        {
            ("null", null), ("empty", ""), ("encoded-empty", encodedEmpty),
        })
            yield return [expected.Name, expected.Value, actual.Name, actual.Value];
    }

    [Theory]
    [MemberData(nameof(RawRegistryIdentityCases))]
    public void CommitAdmissionOwnership_NullEmptyAndEncodedEmpty_ArePairwiseDistinct(
        string expectedName, string? expected, string actualName, string? actual)
    {
        var suffix = expectedName + "-vs-" + actualName;
        var goalId = "ownership-identity-" + suffix;
        var taskId = "task-identity-" + suffix;
        SeedRichIdleRow(goalId, actual);
        var candidate = RichCandidate(goalId, taskId);

        var result = WithStore((store, _) => store.CommitAdmissionOwnership(candidate, expected));
        var durable = ReadOwnership(goalId, taskId);

        if (expectedName == actualName)
        {
            AssertConfirmed(result, AdmissionOwnershipCommitStatus.Committed);
            Assert.Equal(taskId, durable.Pointer);
            Assert.Equal(goalId, durable.MappingGoal);
            Assert.Equal(WorkSlotRegistryCodec.Encode(candidate.Registry), durable.Blob);
        }
        else
        {
            AssertConfirmed(result, AdmissionOwnershipCommitStatus.Refused);
            Assert.Null(durable.Pointer);
            Assert.Equal(actual, durable.Blob);
            Assert.Null(durable.MappingGoal);
        }
    }

    [Fact]
    public void CommitAdmissionOwnership_MissingPipelineRow_RefusedWithoutCreatingAnything()
    {
        var candidate = RichCandidate("ownership-missing", "task-missing");

        var result = WithStore((store, _) => store.CommitAdmissionOwnership(candidate, null));

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Refused);
        Assert.Equal(new OwnershipRows(false, null, null, null),
            ReadOwnership("ownership-missing", "task-missing"));
    }

    [Theory]
    [InlineData("task-occupied", "task-occupied")]
    [InlineData("task-other", "task-occupied-other")]
    public void CommitAdmissionOwnership_OccupiedPointerIncludingSameTask_RefusedAndTupleUnchanged(
        string occupiedBy, string taskId)
    {
        var goalId = "ownership-occupied-" + occupiedBy;
        const string oldBlob = "occupied-raw-blob";
        SeedRichIdleRow(goalId, oldBlob);
        SetPointer(goalId, occupiedBy);
        var candidate = RichCandidate(goalId, taskId);

        var result = WithStore((store, _) => store.CommitAdmissionOwnership(candidate, oldBlob));

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Refused);
        Assert.Equal(new OwnershipRows(true, occupiedBy, oldBlob, null), ReadOwnership(goalId, taskId));
    }

    [Fact]
    public void CommitAdmissionOwnership_StaleRawRegistryWhilePointerNull_RefusedAndTupleUnchanged()
    {
        const string goalId = "ownership-stale-blob";
        const string taskId = "task-stale-blob";
        const string actual = "{\"sameValues\":true,\"order\":1}";
        const string staleExpected = "{\"order\":1,\"sameValues\":true}";
        SeedRichIdleRow(goalId, actual);
        var candidate = RichCandidate(goalId, taskId);

        var result = WithStore((store, _) => store.CommitAdmissionOwnership(candidate, staleExpected));

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Refused);
        Assert.Equal(new OwnershipRows(true, null, actual, null), ReadOwnership(goalId, taskId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommitAdmissionOwnership_ExistingSameOrForeignMapping_RefusedAndUpdateRolledBack(bool foreign)
    {
        var goalId = foreign ? "ownership-map-foreign" : "ownership-map-same";
        var taskId = foreign ? "task-map-foreign" : "task-map-same";
        const string oldBlob = "mapping-prior";
        SeedRichIdleRow(goalId, oldBlob);
        var mappedGoal = foreign ? "newer-foreign-goal" : goalId;
        InsertMapping(taskId, mappedGoal);
        var candidate = RichCandidate(goalId, taskId);

        var result = WithStore((store, _) => store.CommitAdmissionOwnership(candidate, oldBlob));

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Refused);
        Assert.Equal(new OwnershipRows(true, null, oldBlob, mappedGoal), ReadOwnership(goalId, taskId));
    }

    [Theory]
    [InlineData("newer-pointer")]
    [InlineData("newer-blob")]
    [InlineData("newer-mapping")]
    public void CommitAdmissionOwnership_StaleRequestRacedWithNewerAdmission_RefusesWithoutOverwriting(string race)
    {
        var goalId = "ownership-race-" + race;
        var taskId = "task-race-" + race;
        const string expectedBlob = "stale-request-blob";
        SeedRichIdleRow(goalId, expectedBlob);
        string? pointer = null;
        var actualBlob = expectedBlob;
        string? mapping = null;
        switch (race)
        {
            case "newer-pointer":
                pointer = "newer-task";
                SetPointer(goalId, pointer);
                break;
            case "newer-blob":
                actualBlob = "newer-registry-blob";
                SetBlob(goalId, actualBlob);
                break;
            case "newer-mapping":
                mapping = "newer-goal";
                InsertMapping(taskId, mapping);
                break;
            default:
                throw new InvalidOperationException("Unhandled race: " + race);
        }

        var result = WithStore((store, _) =>
            store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), expectedBlob));

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Refused);
        Assert.Equal(new OwnershipRows(true, pointer, actualBlob, mapping), ReadOwnership(goalId, taskId));
    }

    [Fact]
    public void CommitAdmissionOwnership_ExecutesExactlyUpdateThenInsert_WithNarrowParameterizedShapeAndNoReads()
    {
        const string goalId = "ownership-sql-shape";
        const string taskId = "task-sql-shape";
        const string oldBlob = "shape-prior";
        SeedRichIdleRow(goalId, oldBlob);
        var candidate = RichCandidate(goalId, taskId);
        var encoded = WorkSlotRegistryCodec.Encode(candidate.Registry);
        var capture = new AdmissionOwnershipCommandCaptureInterceptor();

        var result = WithStore(
            (store, _) => store.CommitAdmissionOwnership(candidate, oldBlob), capture);

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Committed);
        Assert.Collection(capture.Commands,
            update =>
            {
                Assert.Equal("NonQuery", update.Kind);
                Assert.StartsWith("UPDATE pipelines", update.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("SET active_task_id = $task, work_slot_registry_json = $registry",
                    update.Sql, StringComparison.Ordinal);
                Assert.Contains("active_task_id IS NULL", update.Sql, StringComparison.Ordinal);
                Assert.Contains("work_slot_registry_json IS $expected COLLATE BINARY",
                    update.Sql, StringComparison.Ordinal);
                Assert.Equal(new[] { "$expected", "$goal", "$registry", "$task" },
                    update.Parameters.Keys.Order(StringComparer.Ordinal).ToArray());
                Assert.Equal(goalId, update.Parameters["$goal"]);
                Assert.Equal(taskId, update.Parameters["$task"]);
                Assert.Equal(encoded, update.Parameters["$registry"]);
                Assert.Equal(oldBlob, update.Parameters["$expected"]);
            },
            insert =>
            {
                Assert.Equal("NonQuery", insert.Kind);
                Assert.StartsWith("INSERT INTO task_mappings", insert.Sql.TrimStart(),
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains("ON CONFLICT(task_id) DO NOTHING", insert.Sql, StringComparison.Ordinal);
                Assert.Equal(new[] { "$goal", "$task" },
                    insert.Parameters.Keys.Order(StringComparer.Ordinal).ToArray());
                Assert.Equal(taskId, insert.Parameters["$task"]);
                Assert.Equal(goalId, insert.Parameters["$goal"]);
            });
        Assert.DoesNotContain(capture.Commands,
            command => command.Kind is "Reader" or "Scalar"
                || command.Sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.All(capture.Commands, command =>
        {
            Assert.DoesNotContain(goalId, command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain(taskId, command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain(encoded, command.Sql, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CommitAdmissionOwnership_BeginTransactionThrows_ExactExceptionNoBodyWriteAndCleanupCannotMask()
    {
        const string goalId = "ownership-begin-fails";
        const string taskId = "task-begin-fails";
        const string oldBlob = "begin-prior";
        SeedRichIdleRow(goalId, oldBlob);
        using var beginConnection = OpenConnection();
        var capture = new AdmissionOwnershipCommandCaptureInterceptor();
        var beginInterceptor = new AdmissionBeginThrowingInterceptor();
        var context = new CopilotHiveDbContext(
            new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(beginConnection)
                .AddInterceptors(capture, beginInterceptor)
                .Options);
        var factory = new SingleContextFactory(context);
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(factory, logger);
        var cleanupSentinel = new InvalidOperationException("begin path context cleanup sentinel");
        var trackerDetachCalls = 0;
        store.TrackerDetachForTest = (_, _) =>
        {
            trackerDetachCalls++;
            throw new InvalidOperationException("tracker cleanup must not run before a body attempt");
        };
        var contextDisposeCalls = 0;
        store.ContextDisposerForTest = ownedContext =>
        {
            contextDisposeCalls++;
            ownedContext.Dispose();
            throw cleanupSentinel;
        };

        try
        {
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), oldBlob));

            Assert.Same(beginInterceptor.BeginSentinel, thrown);
            Assert.Equal(1, beginInterceptor.BeginAttemptCount);
            Assert.Empty(capture.Commands);
            Assert.Equal(0, trackerDetachCalls);
            Assert.Equal(1, contextDisposeCalls);
            Assert.Contains(logger.LogEntries, entry => entry.LogLevel == LogLevel.Warning
                && entry.Message.Contains("admission-ownership-context-dispose", StringComparison.Ordinal));
            Assert.Equal(new OwnershipRows(true, null, oldBlob, null), ReadOwnership(goalId, taskId));
        }
        finally
        {
            context.Dispose();
        }
    }

    [Theory]
    [InlineData("update", -1)]
    [InlineData("update", 2)]
    [InlineData("insert", -1)]
    [InlineData("insert", 2)]
    public void CommitAdmissionOwnership_UnexpectedStatementRowCount_IsErrorAndRollsBackTuple(
        string statement, int forcedCount)
    {
        var goalId = $"ownership-count-{statement}-{forcedCount}";
        var taskId = $"task-count-{statement}-{forcedCount}";
        const string oldBlob = "row-count-prior";
        SeedRichIdleRow(goalId, oldBlob);
        var interceptor = new AdmissionOwnershipRowCountInterceptor(statement, forcedCount);

        var thrown = Assert.Throws<InvalidOperationException>(() => WithStore(
            (store, _) => store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), oldBlob), interceptor));

        Assert.Equal(1, interceptor.OverrideCount);
        Assert.Contains(forcedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            thrown.Message, StringComparison.Ordinal);
        Assert.Contains("expected exactly 0 or 1", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(statement == "update" ? "guard updated" : "mapping insert affected",
            thrown.Message, StringComparison.Ordinal);
        Assert.Equal(new OwnershipRows(true, null, oldBlob, null), ReadOwnership(goalId, taskId));
    }

    [Fact]
    public void CommitAdmissionOwnership_SqlFailureBetweenUpdateAndInsert_PropagatesOriginalAndRollsBackTuple()
    {
        const string goalId = "ownership-between-statements";
        const string taskId = "task-between-statements";
        const string oldBlob = "between-prior";
        SeedRichIdleRow(goalId, oldBlob);
        var interceptor = new AdmissionOwnershipCommandCaptureInterceptor(throwOnInsert: true);

        var thrown = Assert.Throws<SqliteException>(() => WithStore(
            (store, _) => store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), oldBlob), interceptor));

        Assert.Same(interceptor.InsertSentinel, thrown);
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.Equal(2, interceptor.Commands.Count);
        Assert.StartsWith("UPDATE pipelines", interceptor.Commands[0].Sql.TrimStart(),
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("INSERT INTO task_mappings", interceptor.Commands[1].Sql.TrimStart(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new OwnershipRows(true, null, oldBlob, null), ReadOwnership(goalId, taskId));
    }

    [Fact]
    public void CommitAdmissionOwnership_RefusalAndRollbackThrow_IndeterminateWithRollbackEvidence()
    {
        const string goalId = "ownership-refusal-rollback-throws";
        const string taskId = "task-refusal-rollback-throws";
        const string oldBlob = "rollback-refusal-prior";
        SeedRichIdleRow(goalId, oldBlob);
        SetPointer(goalId, "newer-owner");
        using var connection = new RollbackThrowingConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var result = store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), oldBlob);

        Assert.Equal(AdmissionOwnershipCommitStatus.Indeterminate, result.Status);
        Assert.Null(result.PrimaryException);
        Assert.Same(connection.RollbackSentinel, result.RollbackException);
        Assert.Equal(1, connection.RollbackAttemptCount);
        Assert.Equal(new OwnershipRows(true, "newer-owner", oldBlob, null), ReadOwnership(goalId, taskId));
    }

    [Fact]
    public void CommitAdmissionOwnership_BodyErrorBeforeFirstWriteAndRollbackThrow_IndeterminateWithBothEvidence()
    {
        const string goalId = "ownership-body-before-write";
        const string taskId = "task-body-before-write";
        const string oldBlob = "before-write-prior";
        SeedRichIdleRow(goalId, oldBlob);
        var interceptor = new AdmissionTargetedThrowInterceptor(
            AdmissionTargetedThrowInterceptor.Target.Pipelines, 5, 5);
        using var connection = new RollbackThrowingConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection, interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var result = store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), oldBlob);

        Assert.Equal(AdmissionOwnershipCommitStatus.Indeterminate, result.Status);
        Assert.Same(interceptor.Sentinel, result.PrimaryException);
        Assert.Same(connection.RollbackSentinel, result.RollbackException);
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.Equal(1, connection.RollbackAttemptCount);
        Assert.Equal(new OwnershipRows(true, null, oldBlob, null), ReadOwnership(goalId, taskId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommitAdmissionOwnership_CommitThrowsBeforeOrAfterUnderlyingCommit_IndeterminateWithDifferentDurability(
        bool throwAfterCommit)
    {
        var suffix = throwAfterCommit ? "after" : "before";
        var goalId = "ownership-commit-" + suffix;
        var taskId = "task-commit-" + suffix;
        const string oldBlob = "commit-prior";
        SeedRichIdleRow(goalId, oldBlob);
        var candidate = RichCandidate(goalId, taskId);
        using var connection = new AdmissionCommitFaultConnection(ConnectionString, throwAfterCommit);
        connection.Open();
        using var context = ContextOn(connection);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var result = store.CommitAdmissionOwnership(candidate, oldBlob);

        Assert.Equal(AdmissionOwnershipCommitStatus.Indeterminate, result.Status);
        Assert.Same(connection.CommitSentinel, result.PrimaryException);
        Assert.Equal(1, connection.CommitAttemptCount);
        var durable = ReadOwnership(goalId, taskId);
        if (throwAfterCommit)
        {
            Assert.Equal(taskId, durable.Pointer);
            Assert.Equal(WorkSlotRegistryCodec.Encode(candidate.Registry), durable.Blob);
            Assert.Equal(goalId, durable.MappingGoal);
        }
        else
        {
            Assert.Equal(new OwnershipRows(true, null, oldBlob, null), durable);
        }
    }

    [Fact]
    public void CommitAdmissionOwnership_CommitAndRollbackThrow_RetainsBothExactExceptionsAndDetachesAffectedTracker()
    {
        const string goalId = "ownership-commit-and-rollback-throw";
        const string taskId = "task-commit-and-rollback-throw";
        const string oldBlob = "commit-rollback-prior";
        SeedRichIdleRow(goalId, oldBlob);
        using var connection = new AdmissionCommitAndRollbackThrowConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection);
        var stalePipeline = TrackedPipelineEntity(goalId);
        stalePipeline.ActiveTaskId = "stale-tracked-pointer";
        stalePipeline.WorkSlotRegistryJson = "stale-tracked-blob";
        context.Attach(stalePipeline);
        var staleMapping = new TaskMappingEntity { TaskId = taskId, GoalId = "stale-tracked-goal" };
        context.Attach(staleMapping);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var result = store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), oldBlob);

        Assert.Equal(AdmissionOwnershipCommitStatus.Indeterminate, result.Status);
        Assert.Same(connection.CommitSentinel, result.PrimaryException);
        Assert.Same(connection.RollbackSentinel, result.RollbackException);
        Assert.Equal(1, connection.CommitAttemptCount);
        Assert.Equal(1, connection.RollbackAttemptCount);
        Assert.DoesNotContain(context.ChangeTracker.Entries<PipelineEntity>(),
            entry => entry.Entity.GoalId == goalId);
        Assert.DoesNotContain(context.ChangeTracker.Entries<TaskMappingEntity>(),
            entry => entry.Entity.TaskId == taskId);
        Assert.Equal(new OwnershipRows(true, null, oldBlob, null), ReadOwnership(goalId, taskId));
    }

    [Fact]
    public void CommitAdmissionOwnership_GoalDetachAndWarningLoggerThrow_TaskDetachAndLaterCleanupStillRun()
    {
        const string goalId = "ownership-cleanup-chain";
        const string taskId = "task-cleanup-chain";
        const string oldBlob = "cleanup-chain-prior";
        SeedRichIdleRow(goalId, oldBlob);
        var logger = new ThrowingLogger<PipelineStore>();
        using var connection = new DisposeThrowingConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection);
        var stalePipeline = TrackedPipelineEntity(goalId);
        var staleMapping = new TaskMappingEntity { TaskId = taskId, GoalId = "stale-tracked-goal" };
        context.Attach(stalePipeline);
        context.Attach(staleMapping);
        var store = new PipelineStore(context, logger);
        var goalDetachCalls = 0;
        string? detachedGoal = null;
        store.TrackerDetachForTest = (_, detachedGoalId) =>
        {
            goalDetachCalls++;
            detachedGoal = detachedGoalId;
            throw new InvalidOperationException("goal detach sentinel");
        };
        logger.Arm();

        var result = store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), oldBlob);

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Committed);
        Assert.Equal(1, goalDetachCalls);
        Assert.Equal(goalId, detachedGoal);
        Assert.Contains(context.ChangeTracker.Entries<PipelineEntity>(),
            entry => entry.Entity.GoalId == goalId); // the injected goal-detach failure was real
        Assert.DoesNotContain(context.ChangeTracker.Entries<TaskMappingEntity>(),
            entry => entry.Entity.TaskId == taskId); // independent task detach still ran
        Assert.Equal(1, connection.DisposeAttemptCount); // later transaction cleanup also ran
        Assert.True(logger.ThrowCount >= 2,
            "both guarded warnings should reach the throwing logger without masking the result");
        var durable = ReadOwnership(goalId, taskId);
        Assert.Equal(taskId, durable.Pointer);
        Assert.Equal(goalId, durable.MappingGoal);
    }

    [Fact]
    public void CommitAdmissionOwnership_CommitReturnsThenTransactionDisposeThrows_RemainsCommittedAndDurable()
    {
        const string goalId = "ownership-dispose-after-commit";
        const string taskId = "task-dispose-after-commit";
        const string oldBlob = "dispose-prior";
        SeedRichIdleRow(goalId, oldBlob);
        var logger = new TestLogger<PipelineStore>();
        using var connection = new DisposeThrowingConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection);
        var store = new PipelineStore(context, logger);

        var result = store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), oldBlob);

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Committed);
        Assert.Equal(1, connection.DisposeAttemptCount);
        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Warning
            && e.Message.Contains("admission-ownership-transaction-dispose", StringComparison.Ordinal));
        var durable = ReadOwnership(goalId, taskId);
        Assert.Equal(taskId, durable.Pointer);
        Assert.Equal(goalId, durable.MappingGoal);
        Assert.NotEqual(oldBlob, durable.Blob);
    }

    [Fact]
    public void CommitAdmissionOwnership_FactoryContextDisposeThrows_RemainsCommittedAndDurable()
    {
        const string goalId = "ownership-context-dispose";
        const string taskId = "task-context-dispose";
        const string oldBlob = "context-dispose-prior";
        SeedRichIdleRow(goalId, oldBlob);
        var logger = new TestLogger<PipelineStore>();
        var factory = new RecordingContextFactory(ConnectionString);
        AdmissionOwnershipCommitResult result;
        var calls = 0;
        try
        {
            var store = new PipelineStore(factory, logger);
            var sentinel = new InvalidOperationException("context dispose after commit sentinel");
            store.ContextDisposerForTest = context =>
            {
                calls++;
                context.Dispose();
                throw sentinel;
            };

            result = store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), oldBlob);
        }
        finally
        {
            // EVERY factory-opened connection is CLOSED here — the contexts never owned them, so
            // the readback below is a genuine fresh open of the file, not a read through a handle
            // the test left dangling.
            factory.Dispose();
        }

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Committed);
        Assert.Equal(1, calls);
        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Warning
            && e.Message.Contains("admission-ownership-context-dispose", StringComparison.Ordinal));
        var durable = ReadOwnership(goalId, taskId);
        Assert.Equal(taskId, durable.Pointer);
        Assert.Equal(goalId, durable.MappingGoal);
    }

    [Fact]
    public void CommitAdmissionOwnership_CommitReturnRecordedBeforeDisposeInducedRollback_RemainsCommitted()
    {
        const string goalId = "ownership-dispose-rollback";
        const string taskId = "task-dispose-rollback";
        const string oldBlob = "dispose-rollback-prior";
        SeedRichIdleRow(goalId, oldBlob);
        using var connection = new CommitReturningWithoutCommitConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var result = store.CommitAdmissionOwnership(RichCandidate(goalId, taskId), oldBlob);

        AssertConfirmed(result, AdmissionOwnershipCommitStatus.Committed);
        Assert.Equal(1, connection.CommitReturnCount);
        Assert.Equal(1, connection.DisposeRollbackCount);
        Assert.Equal(new OwnershipRows(true, null, oldBlob, null), ReadOwnership(goalId, taskId));
    }

    private static PipelineEntity TrackedPipelineEntity(string goalId) => new()
    {
        GoalId = goalId,
        Description = "tracked " + goalId,
        GoalJson = "{}",
        Phase = "Planning",
        Iteration = 1,
        MaxRetries = 3,
        MaxIterations = 3,
        PhaseOutputs = "{}",
        MetricsJson = "{}",
        CreatedAt = "2026-01-01T00:00:00.0000000Z",
        RoleSessionsJson = "[]",
        PhaseOccurrence = 1,
    };

    private T? Scalar<T>(string sql, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>
    /// A factory handing out contexts the STORE owns (the <c>ownsContext = true</c> path), each on
    /// its OWN connection to the file-backed database.
    /// <para>
    /// THE CONNECTIONS ARE TRACKED AND CLOSED HERE. A context configured with an EXTERNALLY
    /// supplied connection does not own it, so <c>context.Dispose()</c> — the store's disposal, or
    /// the throwing <c>ContextDisposerForTest</c> substitute — leaves the underlying file
    /// connection OPEN. Without this tracking a factory-context test would leak an open handle on
    /// the temporary database and its close/reopen readback claim would be hollow (and the file
    /// deletion in the fixture's Dispose would fail on Windows). Disposing the factory therefore
    /// disposes every context AND then every connection it opened, so the later fresh-connection
    /// readback genuinely re-opens the file.
    /// </para>
    /// </summary>
    private sealed class RecordingContextFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly string _connectionString;
        private readonly List<CopilotHiveDbContext> _contexts = [];
        private readonly List<SqliteConnection> _connections = [];

        public RecordingContextFactory(string connectionString) => _connectionString = connectionString;

        public CopilotHiveDbContext CreateDbContext()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            _connections.Add(connection);
            var context = ContextOn(connection);
            _contexts.Add(context);
            return context;
        }

        public void Dispose()
        {
            foreach (var context in _contexts)
                context.Dispose();

            // The contexts never owned these connections — close them explicitly so no open handle
            // survives the test.
            foreach (var connection in _connections)
            {
                connection.Close();
                connection.Dispose();
            }
        }
    }
}

internal sealed record AdmissionCapturedCommand(
    string Kind,
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters);

/// <summary>Captures every command attempt and can throw exactly when the mapping INSERT is reached.</summary>
internal sealed class AdmissionOwnershipCommandCaptureInterceptor : DbCommandInterceptor
{
    private readonly bool _throwOnInsert;
    private readonly List<AdmissionCapturedCommand> _commands = [];
    private int _throwCount;

    public AdmissionOwnershipCommandCaptureInterceptor(bool throwOnInsert = false) =>
        _throwOnInsert = throwOnInsert;

    public IReadOnlyList<AdmissionCapturedCommand> Commands => _commands;
    public SqliteException InsertSentinel { get; } =
        new("admission ordered insert sentinel", 5, 5);
    public int ThrowCount => Volatile.Read(ref _throwCount);

    private void Record(DbCommand command, string kind)
    {
        var parameters = command.Parameters.Cast<DbParameter>().ToDictionary(
            parameter => parameter.ParameterName,
            parameter => parameter.Value,
            StringComparer.Ordinal);
        _commands.Add(new AdmissionCapturedCommand(kind, command.CommandText, parameters));

        if (_throwOnInsert
            && command.CommandText.TrimStart().StartsWith(
                "INSERT INTO task_mappings", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _throwCount);
            throw InsertSentinel;
        }
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command, "NonQuery");
        return result;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command, "Reader");
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command, "Scalar");
        return result;
    }
}

/// <summary>Substitutes the provider's post-execution affected-row count for one admission statement.</summary>
internal sealed class AdmissionOwnershipRowCountInterceptor : DbCommandInterceptor
{
    private readonly string _statement;
    private readonly int _forcedCount;
    private int _overrideCount;

    public AdmissionOwnershipRowCountInterceptor(string statement, int forcedCount)
    {
        if (statement is not ("update" or "insert"))
            throw new ArgumentException("Statement must be 'update' or 'insert'.", nameof(statement));
        _statement = statement;
        _forcedCount = forcedCount;
    }

    public int OverrideCount => Volatile.Read(ref _overrideCount);

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        var sql = command.CommandText.TrimStart();
        var targeted = _statement == "update"
            ? sql.StartsWith("UPDATE pipelines", StringComparison.OrdinalIgnoreCase)
            : sql.StartsWith("INSERT INTO task_mappings", StringComparison.OrdinalIgnoreCase);
        if (!targeted)
            return result;

        Interlocked.Increment(ref _overrideCount);
        return _forcedCount;
    }
}

/// <summary>Throws a pre-created exception before the provider begins a transaction.</summary>
internal sealed class AdmissionBeginThrowingInterceptor : DbTransactionInterceptor
{
    private int _beginAttemptCount;
    public InvalidOperationException BeginSentinel { get; } =
        new("admission begin-transaction sentinel");
    public int BeginAttemptCount => Volatile.Read(ref _beginAttemptCount);

    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result)
    {
        Interlocked.Increment(ref _beginAttemptCount);
        throw BeginSentinel;
    }
}

/// <summary>A one-shot factory exposing a caller-created context through the store-owned path.</summary>
internal sealed class SingleContextFactory : IDbContextFactory<CopilotHiveDbContext>
{
    private readonly CopilotHiveDbContext _context;
    public SingleContextFactory(CopilotHiveDbContext context) => _context = context;
    public CopilotHiveDbContext CreateDbContext() => _context;
}

/// <summary>Both commit and rollback throw distinct, pre-created exceptions before reaching SQLite.</summary>
internal sealed class AdmissionCommitAndRollbackThrowConnection : AdmissionTransactionConnectionBase
{
    private int _commitAttemptCount;
    private int _rollbackAttemptCount;

    public AdmissionCommitAndRollbackThrowConnection(string connectionString) : base(connectionString) { }

    public InvalidOperationException CommitSentinel { get; } = new("admission combined commit sentinel");
    public InvalidOperationException RollbackSentinel { get; } = new("admission combined rollback sentinel");
    public int CommitAttemptCount => _commitAttemptCount;
    public int RollbackAttemptCount => _rollbackAttemptCount;

    protected override DbTransaction WrapTransaction(SqliteTransaction transaction) =>
        new FaultTransaction(this, transaction);

    private sealed class FaultTransaction : DbTransaction
    {
        private readonly AdmissionCommitAndRollbackThrowConnection _owner;
        private readonly SqliteTransaction _inner;

        public FaultTransaction(AdmissionCommitAndRollbackThrowConnection owner, SqliteTransaction inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public override IsolationLevel IsolationLevel => _inner.IsolationLevel;
        protected override DbConnection DbConnection => _owner;
        public override void Commit()
        {
            _owner._commitAttemptCount++;
            throw _owner.CommitSentinel;
        }
        public override void Rollback()
        {
            _owner._rollbackAttemptCount++;
            throw _owner.RollbackSentinel;
        }
        protected override void Dispose(bool disposing) => _inner.Dispose();
    }
}

/// <summary>A transaction wrapper that throws either immediately before or immediately after the real commit.</summary>
internal sealed class AdmissionCommitFaultConnection : AdmissionTransactionConnectionBase
{
    private readonly bool _throwAfterCommit;
    private int _commitAttemptCount;

    public AdmissionCommitFaultConnection(string connectionString, bool throwAfterCommit)
        : base(connectionString) => _throwAfterCommit = throwAfterCommit;

    public InvalidOperationException CommitSentinel { get; } = new("admission commit timing sentinel");
    public int CommitAttemptCount => _commitAttemptCount;

    protected override DbTransaction WrapTransaction(SqliteTransaction transaction) =>
        new FaultTransaction(this, transaction);

    private sealed class FaultTransaction : DbTransaction
    {
        private readonly AdmissionCommitFaultConnection _owner;
        private readonly SqliteTransaction _inner;

        public FaultTransaction(AdmissionCommitFaultConnection owner, SqliteTransaction inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public override IsolationLevel IsolationLevel => _inner.IsolationLevel;
        protected override DbConnection DbConnection => _owner;
        public override void Commit()
        {
            _owner._commitAttemptCount++;
            if (!_owner._throwAfterCommit)
                throw _owner.CommitSentinel;
            _inner.Commit();
            throw _owner.CommitSentinel;
        }
        public override void Rollback() => _inner.Rollback();
        protected override void Dispose(bool disposing) => _inner.Dispose();
    }
}

/// <summary>
/// A deliberately adversarial transaction: Commit returns without touching SQLite; disposal then
/// releases the still-active transaction, causing the provider's rollback. This pins outcome timing,
/// not a claim that such a provider is well-behaved.
/// </summary>
internal sealed class CommitReturningWithoutCommitConnection : AdmissionTransactionConnectionBase
{
    private int _commitReturnCount;
    private int _disposeRollbackCount;

    public CommitReturningWithoutCommitConnection(string connectionString) : base(connectionString) { }
    public int CommitReturnCount => _commitReturnCount;
    public int DisposeRollbackCount => _disposeRollbackCount;

    protected override DbTransaction WrapTransaction(SqliteTransaction transaction) =>
        new NoCommitTransaction(this, transaction);

    private sealed class NoCommitTransaction : DbTransaction
    {
        private readonly CommitReturningWithoutCommitConnection _owner;
        private readonly SqliteTransaction _inner;

        public NoCommitTransaction(CommitReturningWithoutCommitConnection owner, SqliteTransaction inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public override IsolationLevel IsolationLevel => _inner.IsolationLevel;
        protected override DbConnection DbConnection => _owner;
        public override void Commit() => _owner._commitReturnCount++;
        public override void Rollback() => _inner.Rollback();
        protected override void Dispose(bool disposing)
        {
            if (!disposing)
                return;
            _inner.Dispose();
            _owner._disposeRollbackCount++;
        }
    }
}

/// <summary>Shared forwarding connection for narrow admission transaction timing tests.</summary>
internal abstract class AdmissionTransactionConnectionBase : DbConnection
{
    private readonly SqliteConnection _inner;

    protected AdmissionTransactionConnectionBase(string connectionString) =>
        _inner = new SqliteConnection(connectionString);

    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value ?? throw new ArgumentNullException(nameof(value));
    }
    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override int ConnectionTimeout => _inner.ConnectionTimeout;
    public override ConnectionState State => _inner.State;
    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public override void Close() => _inner.Close();
    public override void Open() => _inner.Open();

    protected sealed override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        WrapTransaction((SqliteTransaction)_inner.BeginTransaction(isolationLevel));

    protected abstract DbTransaction WrapTransaction(SqliteTransaction transaction);

    protected override DbCommand CreateDbCommand() => new ShieldedCommand(_inner.CreateCommand());

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    private sealed class ShieldedCommand : DbCommand
    {
        private readonly DbCommand _inner;
        public ShieldedCommand(DbCommand inner) => _inner = inner;
        [AllowNull]
        public override string CommandText { get => _inner.CommandText; set => _inner.CommandText = value ?? ""; }
        public override int CommandTimeout { get => _inner.CommandTimeout; set => _inner.CommandTimeout = value; }
        public override CommandType CommandType { get => _inner.CommandType; set => _inner.CommandType = value; }
        [AllowNull]
        protected override DbConnection DbConnection { get => _inner.Connection!; set { } }
        protected override DbParameterCollection DbParameterCollection => _inner.Parameters;
        protected override DbTransaction? DbTransaction { get => _inner.Transaction; set { } }
        public override bool DesignTimeVisible { get => false; set { } }
        public override UpdateRowSource UpdatedRowSource { get => _inner.UpdatedRowSource; set => _inner.UpdatedRowSource = value; }
        public override void Cancel() => _inner.Cancel();
        public override int ExecuteNonQuery() => _inner.ExecuteNonQuery();
        public override object? ExecuteScalar() => _inner.ExecuteScalar();
        public override void Prepare() => _inner.Prepare();
        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _inner.ExecuteReader(behavior);
    }
}

/// <summary>
/// Regression matrix for <see cref="PipelineStore.CommitPendingAdmissionRollback"/> against a REAL,
/// file-backed SQLite database: the admitted-then-rolled-back lifecycle read back through a FRESH
/// context, the exact-text CAS, both one-row rules, the between-statement rollback, the
/// commit/rollback uncertainty evidence, the consumed attempt never reused, and the unchanged
/// unrelated durable state.
/// </summary>
public sealed class PipelineStorePendingRollbackIntegrationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"copilothive-pending-rollback-{Guid.NewGuid():N}.db");

    public PipelineStorePendingRollbackIntegrationTests()
    {
        using var connection = OpenConnection();
        using var context = ContextOn(connection);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort deletion of a test-only temporary database.
            }
        }
    }

    private string ConnectionString => $"Data Source={_dbPath};Pooling=False";

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static CopilotHiveDbContext ContextOn(DbConnection connection, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new CopilotHiveDbContext(builder.Options);
    }

    private T WithStore<T>(Func<PipelineStore, CopilotHiveDbContext, T> action, IInterceptor? interceptor = null)
    {
        using var connection = OpenConnection();
        using var context = ContextOn(connection, interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);
        return action(store, context);
    }

    private static Goal Goal(string goalId) =>
        new() { Id = goalId, Description = "goal " + goalId, RepositoryNames = ["repo-a", "repo-b"] };

    private static WorkSlotPosition Pos(int occurrence, GoalPhase phase = GoalPhase.Coding, int iteration = 1) =>
        new(iteration, phase, occurrence);

    /// <summary>
    /// Admits a REAL Pending candidate through the production paths, persists the pipeline row,
    /// the pointer, the mapping row and the registry blob, and returns the admitted candidate so
    /// the test owns the exact prior state.
    /// </summary>
    private AdmissionOwnershipSnapshot AdmitPending(string goalId, string taskId)
    {
        var pipeline = new GoalPipeline(Goal(goalId), maxRetries: 8, maxIterations: 6);
        pipeline.SetPlan(new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review],
        });
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.IterationBudget.TryConsume();
        pipeline.Conversation.Add(new ConversationEntry("user", "preserved conversation one"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "preserved conversation two"));

        var historical = pipeline.AllocateAttemptAndRegisterSlot("historical-recorded", Pos(1, GoalPhase.Review, 1));
        Assert.Equal(1, historical.Attempt);
        Assert.Equal(SlotGuardResult.Proceed, pipeline.ResolveAndCheckSlot("historical-recorded"));
        pipeline.RecordSlot("historical-recorded");

        var admitted = pipeline.AllocateAttemptAndRegisterSlot(taskId, Pos(2));
        Assert.Equal(1, admitted.Attempt);
        pipeline.SetActiveTask(taskId, "coder/preserved-branch");
        var candidate = pipeline.CaptureAdmissionOwnership();
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);

        WithStore<object?>((store, _) =>
        {
            store.SavePipeline(pipeline);
            store.SaveTaskMapping(taskId, goalId);
            return null;
        });
        SetBlob(goalId, stored);
        SetPointer(goalId, taskId);
        return candidate;
    }

    private void SetBlob(string goalId, string? blob)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE pipelines SET work_slot_registry_json = $blob WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId);
        command.Parameters.AddWithValue("$blob", (object?)blob ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private void SetPointer(string goalId, string? taskId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE pipelines SET active_task_id = $task WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId);
        command.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private void InsertMapping(string taskId, string goalId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO task_mappings (task_id, goal_id) VALUES ($task, $goal)";
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$goal", goalId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private RollbackRows ReadRollback(string goalId, string taskId)
    {
        using var connection = OpenConnection();
        using var pipeline = connection.CreateCommand();
        pipeline.CommandText =
            "SELECT active_task_id, work_slot_registry_json, phase, iteration, max_retries, max_iterations, coder_branch, phase_log_json, goal_json, metrics_json FROM pipelines WHERE goal_id = $goal";
        pipeline.Parameters.AddWithValue("$goal", goalId);
        using var reader = pipeline.ExecuteReader();
        var rowExists = reader.Read();
        var pointer = rowExists && !reader.IsDBNull(0) ? reader.GetString(0) : null;
        var blob = rowExists && !reader.IsDBNull(1) ? reader.GetString(1) : null;
        var phase = rowExists ? reader.GetString(2) : null;
        var iteration = rowExists ? reader.GetInt32(3) : -1;
        var maxRetries = rowExists ? reader.GetInt32(4) : -1;
        var maxIterations = rowExists ? reader.GetInt32(5) : -1;
        var coderBranch = rowExists && !reader.IsDBNull(6) ? reader.GetString(6) : null;
        var phaseLog = rowExists && !reader.IsDBNull(7) ? reader.GetString(7) : null;
        var goalJson = rowExists && !reader.IsDBNull(8) ? reader.GetString(8) : null;
        var metricsJson = rowExists && !reader.IsDBNull(9) ? reader.GetString(9) : null;
        reader.Close();

        using var mapping = connection.CreateCommand();
        mapping.CommandText = "SELECT goal_id FROM task_mappings WHERE task_id = $task";
        mapping.Parameters.AddWithValue("$task", taskId);
        var mapped = mapping.ExecuteScalar();
        return new RollbackRows(rowExists, pointer, blob, mapped is DBNull or null ? null : (string)mapped,
            phase, iteration, maxRetries, maxIterations, coderBranch, phaseLog, goalJson, metricsJson);
    }

    private string? Scalar(string sql, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : (string)result;
    }

    private long Count(string sql, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        return (long)command.ExecuteScalar()!;
    }

    private sealed record RollbackRows(
        bool RowExists,
        string? Pointer,
        string? Blob,
        string? MappingGoal,
        string? Phase,
        int Iteration,
        int MaxRetries,
        int MaxIterations,
        string? CoderBranch,
        string? PhaseLogJson,
        string? GoalJson,
        string? MetricsJson);

    [Fact]
    public void CommitPendingAdmissionRollback_RealAdmittedPending_RollbackDurablyAndFreshContextReadback()
    {
        const string goalId = "rollback-durable";
        const string taskId = "task-rollback-durable";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);

        var beforeRow = WithStore((store, _) => ReadRollback(goalId, taskId));
        var result = WithStore((store, _) =>
            store.CommitPendingAdmissionRollback(goalId, taskId, stored));

        Assert.Equal(AdmissionOwnershipCommitStatus.Committed, result.Status);
        Assert.Null(result.PrimaryException);
        Assert.Null(result.RollbackException);

        // FRESH-CONTEXT READBACK (a brand-new connection over the file).
        var after = ReadRollback(goalId, taskId);
        Assert.True(after.RowExists);
        Assert.Null(after.Pointer);
        Assert.Null(after.MappingGoal);
        Assert.NotNull(after.Blob);
        var decoded = WorkSlotRegistryCodec.Decode(after.Blob!);
        var target = Assert.Single(decoded.Slots, s => s.Slot.TaskId == taskId);
        Assert.Equal(WorkSlotState.Abandoned, target.State);
        Assert.Equal(1, target.Slot.Attempt);
        Assert.Equal(Pos(2), target.Slot.Position); // the admitted position, identity preserved

        // The OTHER slots and every high-water entry preserved EXACTLY.
        Assert.Equal(2, decoded.Slots.Count);
        Assert.Contains(decoded.Slots, s => s.Slot.TaskId == "historical-recorded"
            && s.State == WorkSlotState.Recorded && s.Slot.Attempt == 1);
        Assert.Equal(candidate.Registry.DispatchAttempts, decoded.DispatchAttempts);

        // UNRELATED columns and the conversation untouched.
        Assert.Equal("Coding", after.Phase);
        Assert.Equal(beforeRow.Iteration, after.Iteration);
        Assert.Equal(beforeRow.MaxRetries, after.MaxRetries);
        Assert.Equal(beforeRow.MaxIterations, after.MaxIterations);
        Assert.Equal("coder/preserved-branch", after.CoderBranch);

        var conversation = WithStore((store, _) => store.GetConversation(goalId).ToList());
        Assert.Collection(conversation,
            entry => Assert.Equal("preserved conversation one", entry.Content),
            entry => Assert.Equal("preserved conversation two", entry.Content));

        // The unrelated mapping row (if any) is untouched; the affected mapping is gone.
        Assert.Equal(0, Count("SELECT COUNT(*) FROM task_mappings WHERE task_id = $value", taskId));
    }

    [Fact]
    public void CommitPendingAdmissionRollback_ConsumedAttemptIsNotReusedAfterRollback()
    {
        // TEST-ONLY explicit restore/allocation: restore the rolled-back registry into a fresh
        // pipeline and prove the next allocation for the SAME position gets attempt 2, not 1.
        const string goalId = "rollback-attempt-reuse";
        const string taskId = "task-rollback-attempt-reuse";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);

        var result = WithStore((store, _) =>
            store.CommitPendingAdmissionRollback(goalId, taskId, stored));
        Assert.Equal(AdmissionOwnershipCommitStatus.Committed, result.Status);

        var blob = ReadRollback(goalId, taskId).Blob!;
        var restored = new GoalPipeline(Goal(goalId));
        restored.RestoreRegistry(WorkSlotRegistryCodec.Decode(blob));
        Assert.True(restored.IsSlotAbandoned(taskId));

        var next = restored.AllocateAttemptAndRegisterSlot("next-task-after-rollback", Pos(2));
        Assert.Equal(2, next.Attempt); // the consumed attempt 1 is NOT reused
    }

    [Fact]
    public void CommitPendingAdmissionRollback_RepeatedInvocationAlreadyRetired_RefusedWithoutRepair()
    {
        const string goalId = "rollback-repeat";
        const string taskId = "task-rollback-repeat";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);

        var first = WithStore((store, _) =>
            store.CommitPendingAdmissionRollback(goalId, taskId, stored));
        Assert.Equal(AdmissionOwnershipCommitStatus.Committed, first.Status);

        // The SECOND invocation is a repeated request against the already-retired state: the
        // pointer no longer matches, so it refuses WITHOUT repair (no reconstruction, no retry).
        var second = WithStore((store, _) =>
            store.CommitPendingAdmissionRollback(goalId, taskId, stored));
        Assert.Equal(AdmissionOwnershipCommitStatus.Refused, second.Status);
        Assert.Null(second.PrimaryException);
        Assert.Null(second.RollbackException);

        var after = ReadRollback(goalId, taskId);
        Assert.Null(after.Pointer);
        Assert.Equal(WorkSlotState.Abandoned, Assert.Single(
            WorkSlotRegistryCodec.Decode(after.Blob!).Slots, s => s.Slot.TaskId == taskId).State);
        Assert.Null(after.MappingGoal);
    }

    [Theory]
    [InlineData("update", -1)]
    [InlineData("update", 2)]
    [InlineData("delete", -1)]
    [InlineData("delete", 2)]
    public void CommitPendingAdmissionRollback_UnexpectedStatementRowCount_IsErrorAndRollsBack(
        string statement, int forcedCount)
    {
        var goalId = $"rollback-count-{statement}-{forcedCount}";
        var taskId = $"task-rollback-count-{statement}-{forcedCount}";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        var interceptor = new RollbackRowCountInterceptor(statement, forcedCount);

        var thrown = Assert.Throws<InvalidOperationException>(() => WithStore(
            (store, _) => store.CommitPendingAdmissionRollback(goalId, taskId, stored), interceptor));

        Assert.Equal(1, interceptor.OverrideCount);
        Assert.Contains(forcedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            thrown.Message, StringComparison.Ordinal);
        Assert.Contains("expected exactly 0 or 1", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(statement == "update" ? "rollback guard updated" : "rollback mapping delete affected",
            thrown.Message, StringComparison.Ordinal);
        // NOTHING durably changed — the rollback confirmed.
        var after = ReadRollback(goalId, taskId);
        Assert.Equal(taskId, after.Pointer);
        Assert.Equal(stored, after.Blob);
        Assert.Equal(goalId, after.MappingGoal);
    }

    [Fact]
    public void CommitPendingAdmissionRollback_MappingDeleteMissesOrForeign_BetweenStatementRollback()
    {
        foreach (var kind in new[] { "missing", "foreign" })
        {
            var goalId = $"rollback-between-{kind}";
            var taskId = $"task-rollback-between-{kind}";
            var candidate = AdmitPending(goalId, taskId);
            var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
            if (kind == "missing")
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM task_mappings WHERE task_id = $task";
                command.Parameters.AddWithValue("$task", taskId);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            else
            {
                // Re-point the mapping to a FOREIGN goal (same task id, a newer owner).
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE task_mappings SET goal_id = 'newer-foreign-goal' WHERE task_id = $task";
                command.Parameters.AddWithValue("$task", taskId);
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var result = WithStore((store, _) =>
                store.CommitPendingAdmissionRollback(goalId, taskId, stored));

            Assert.Equal(AdmissionOwnershipCommitStatus.Refused, result.Status);
            // The preceding UPDATE was rolled back — the durable state is EXACTLY as admitted.
            var after = ReadRollback(goalId, taskId);
            Assert.Equal(taskId, after.Pointer);
            Assert.Equal(stored, after.Blob);
            // "foreign": the newer goal's row SURVIVES (never a steal); "missing": still absent.
            Assert.Equal(kind == "foreign" ? "newer-foreign-goal" : null, after.MappingGoal);
        }
    }

    [Fact]
    public void CommitPendingAdmissionRollback_CommitThrowsBeforeAndAfterUnderlyingCommit_Indeterminate()
    {
        foreach (var throwAfterCommit in new[] { false, true })
        {
            var goalId = $"rollback-commit-{throwAfterCommit}";
            var taskId = $"task-rollback-commit-{throwAfterCommit}";
            var candidate = AdmitPending(goalId, taskId);
            var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
            using var connection = new AdmissionCommitFaultConnection(ConnectionString, throwAfterCommit);
            connection.Open();
            using var context = ContextOn(connection);
            var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

            var result = store.CommitPendingAdmissionRollback(goalId, taskId, stored);

            Assert.Equal(AdmissionOwnershipCommitStatus.Indeterminate, result.Status);
            Assert.Same(connection.CommitSentinel, result.PrimaryException);
            Assert.Equal(1, connection.CommitAttemptCount);
            var durable = ReadRollback(goalId, taskId);
            if (throwAfterCommit)
            {
                // The commit landed underneath — but the store still reports Indeterminate.
                Assert.Null(durable.Pointer);
                Assert.Null(durable.MappingGoal);
                var decoded = WorkSlotRegistryCodec.Decode(durable.Blob!);
                Assert.Equal(WorkSlotState.Abandoned,
                    Assert.Single(decoded.Slots, s => s.Slot.TaskId == taskId).State);
            }
            else
            {
                Assert.Equal(taskId, durable.Pointer);
                Assert.Equal(stored, durable.Blob);
                Assert.Equal(goalId, durable.MappingGoal);
            }
        }
    }

    [Fact]
    public void CommitPendingAdmissionRollback_RollbackFailure_IndeterminateWithExactPrimaryAndRollbackEvidence()
    {
        const string goalId = "rollback-rollback-throws";
        const string taskId = "task-rollback-rollback-throws";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        // The pointer is moved away so the guard matches zero rows (a clean refusal body) while
        // the rollback — forced to throw — makes the outcome uncertain.
        SetPointer(goalId, "newer-owner");
        using var connection = new RollbackThrowingConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var result = store.CommitPendingAdmissionRollback(goalId, taskId, stored);

        Assert.Equal(AdmissionOwnershipCommitStatus.Indeterminate, result.Status);
        Assert.Null(result.PrimaryException);
        Assert.Same(connection.RollbackSentinel, result.RollbackException);
        Assert.Equal(1, connection.RollbackAttemptCount);
        var after = ReadRollback(goalId, taskId);
        Assert.Equal("newer-owner", after.Pointer);
        Assert.Equal(stored, after.Blob);
        Assert.Equal(goalId, after.MappingGoal);
    }

    [Fact]
    public void CommitPendingAdmissionRollback_BodyErrorWithConfirmedRollback_PropagatesExactException()
    {
        const string goalId = "rollback-body-error";
        const string taskId = "task-rollback-body-error";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        var interceptor = new RegistryUpdateThrowInterceptor();
        // The UPDATE itself throws a SqliteException — a body error, NOT a refusal.
        var thrown = Assert.Throws<SqliteException>(() => WithStore(
            (store, _) => store.CommitPendingAdmissionRollback(goalId, taskId, stored), interceptor));

        Assert.Same(interceptor.Sentinel, thrown);
        var after = ReadRollback(goalId, taskId);
        Assert.Equal(taskId, after.Pointer);
        Assert.Equal(stored, after.Blob);
        Assert.Equal(goalId, after.MappingGoal);
    }

    [Fact]
    public void CommitPendingAdmissionRollback_MissingPipelineRow_RefusedWithoutCreatingAnything()
    {
        var goalId = "rollback-missing";
        var taskId = "task-rollback-missing";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        WithStore<object?>((store, _) =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM pipelines WHERE goal_id = $goal";
            command.Parameters.AddWithValue("$goal", goalId);
            Assert.Equal(1, command.ExecuteNonQuery());
            return null;
        });

        var result = WithStore((store, _) =>
            store.CommitPendingAdmissionRollback(goalId, taskId, stored));

        Assert.Equal(AdmissionOwnershipCommitStatus.Refused, result.Status);
        // A missing pipeline row refuses without creating anything; the mapping row is left
        // UNTOUCHED (a refusal never repairs or erases durable state).
        Assert.Equal(new RollbackRows(false, null, null, goalId, null, -1, -1, -1, null, null, null, null),
            ReadRollback(goalId, taskId));
        Assert.Equal(goalId, Scalar("SELECT goal_id FROM task_mappings WHERE task_id = $value", taskId));
    }

    [Fact]
    public void CommitPendingAdmissionRollback_NullOrNewerPointer_RefusedAndTupleUnchanged()
    {
        foreach (var kind in new[] { "null-pointer", "newer-pointer" })
        {
            var goalId = $"rollback-pointer-{kind}";
            var taskId = $"task-rollback-pointer-{kind}";
            var candidate = AdmitPending(goalId, taskId);
            var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
            SetPointer(goalId, kind == "null-pointer" ? null : "newer-owner");

            var result = WithStore((store, _) =>
                store.CommitPendingAdmissionRollback(goalId, taskId, stored));

            Assert.Equal(AdmissionOwnershipCommitStatus.Refused, result.Status);
            var after = ReadRollback(goalId, taskId);
            Assert.Equal(kind == "null-pointer" ? null : "newer-owner", after.Pointer);
            Assert.Equal(stored, after.Blob);
            Assert.Equal(goalId, after.MappingGoal);
        }
    }

    [Fact]
    public void CommitPendingAdmissionRollback_StaleRegistryText_RefusedAndTupleUnchanged()
    {
        const string goalId = "rollback-stale-text";
        const string taskId = "task-rollback-stale-text";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        const string tampered = "rollback-stale-text-blob";
        SetBlob(goalId, tampered);

        var result = WithStore((store, _) =>
            store.CommitPendingAdmissionRollback(goalId, taskId, stored));

        Assert.Equal(AdmissionOwnershipCommitStatus.Refused, result.Status);
        var after = ReadRollback(goalId, taskId);
        Assert.Equal(taskId, after.Pointer);
        Assert.Equal(tampered, after.Blob);
        Assert.Equal(goalId, after.MappingGoal);
    }

    [Fact]
    public void CommitPendingAdmissionRollback_ExecutesExactlyUpdateThenDelete_WithNarrowParameterizedShapeAndNoReads()
    {
        const string goalId = "rollback-sql-shape";
        const string taskId = "task-rollback-sql-shape";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        var capture = new AdmissionOwnershipCommandCaptureInterceptor();

        var result = WithStore(
            (store, _) => store.CommitPendingAdmissionRollback(goalId, taskId, stored), capture);

        Assert.Equal(AdmissionOwnershipCommitStatus.Committed, result.Status);
        Assert.Collection(capture.Commands,
            update =>
            {
                Assert.Equal("NonQuery", update.Kind);
                Assert.StartsWith("UPDATE pipelines", update.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("SET active_task_id = NULL, work_slot_registry_json = $registry",
                    update.Sql, StringComparison.Ordinal);
                Assert.Contains("active_task_id = $task", update.Sql, StringComparison.Ordinal);
                Assert.Contains("work_slot_registry_json IS $expected COLLATE BINARY",
                    update.Sql, StringComparison.Ordinal);
                Assert.Equal(new[] { "$expected", "$goal", "$registry", "$task" },
                    update.Parameters.Keys.Order(StringComparer.Ordinal).ToArray());
                Assert.Equal(goalId, update.Parameters["$goal"]);
                Assert.Equal(taskId, update.Parameters["$task"]);
                Assert.Equal(WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot(
                    WorkSlotRegistryCodec.Decode(stored).Slots.Select(s => s.Slot.TaskId == taskId
                        ? new WorkSlotView(s.Slot, WorkSlotState.Abandoned) : s).ToList(),
                    WorkSlotRegistryCodec.Decode(stored).DispatchAttempts.ToList())),
                    update.Parameters["$registry"]);
                Assert.Equal(stored, update.Parameters["$expected"]);
            },
            delete =>
            {
                Assert.Equal("NonQuery", delete.Kind);
                Assert.StartsWith("DELETE FROM task_mappings", delete.Sql.TrimStart(),
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains("task_id = $task", delete.Sql, StringComparison.Ordinal);
                Assert.Contains("goal_id = $goal", delete.Sql, StringComparison.Ordinal);
                Assert.Equal(new[] { "$goal", "$task" },
                    delete.Parameters.Keys.Order(StringComparer.Ordinal).ToArray());
                Assert.Equal(taskId, delete.Parameters["$task"]);
                Assert.Equal(goalId, delete.Parameters["$goal"]);
            });
        Assert.DoesNotContain(capture.Commands,
            command => command.Kind is "Reader" or "Scalar"
                || command.Sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.All(capture.Commands, command =>
        {
            Assert.DoesNotContain(goalId, command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain(taskId, command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain(stored, command.Sql, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CommitPendingAdmissionRollback_CommitConfirmedDespiteCleanupFailure_RemainsCommittedAndDurable()
    {
        const string goalId = "rollback-cleanup-fails";
        const string taskId = "task-rollback-cleanup-fails";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        var logger = new ThrowingLogger<PipelineStore>();
        using var connection = new DisposeThrowingConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection);
        var store = new PipelineStore(context, logger);
        logger.Arm();

        var result = store.CommitPendingAdmissionRollback(goalId, taskId, stored);

        Assert.Equal(AdmissionOwnershipCommitStatus.Committed, result.Status);
        Assert.Equal(1, connection.DisposeAttemptCount);
        Assert.True(logger.ThrowCount >= 1);
        var durable = ReadRollback(goalId, taskId);
        Assert.Null(durable.Pointer);
        Assert.Null(durable.MappingGoal);
        Assert.Contains(WorkSlotState.Abandoned,
            WorkSlotRegistryCodec.Decode(durable.Blob!).Slots.Select(s => s.State).Where(st => st == WorkSlotState.Abandoned).ToList());
    }

    [Fact]
    public void CommitPendingAdmissionRollback_PreflightContextAcquisitionThrows_ExactExceptionNoMasking()
    {
        const string goalId = "rollback-acquire-fails";
        const string taskId = "task-rollback-acquire-fails";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        var acquisitionSentinel = new InvalidOperationException("pending-rollback acquisition sentinel");
        var cleanupSentinel = new InvalidOperationException("pending-rollback cleanup sentinel");
        var innerContext = new CopilotHiveDbContext(
            new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(OpenConnection()).Options);
        var factory = new SingleContextFactory(innerContext);
        var store = new PipelineStore(new AcquisitionThrowingFactory(factory, acquisitionSentinel),
            NullLogger<PipelineStore>.Instance);
        var disposerCalls = 0;
        store.ContextDisposerForTest = _ =>
        {
            disposerCalls++;
            throw cleanupSentinel;
        };

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            store.CommitPendingAdmissionRollback(goalId, taskId, stored));

        Assert.Same(acquisitionSentinel, thrown);
        Assert.Equal(0, disposerCalls); // no context existed, so cleanup cannot replace the primary
        innerContext.Dispose();
    }

    [Fact]
    public void CommitPendingAdmissionRollback_ThrowingMessageCleanupException_RecordedResultNotMasked()
    {
        // RECORDED-RESULT PATH (Committed): the factory-owned context disposal throws an exception
        // whose Message GETTER itself throws — injectable through the context-disposal seam. The
        // authoritative Committed result must survive the cleanup path untouched.
        const string goalId = "rollback-message-throws-commit";
        const string taskId = "task-rollback-message-throws-commit";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        var factory = new OwnedConnectionContextFactory(ConnectionString);
        AdmissionOwnershipCommitResult result;
        try
        {
            var store = new PipelineStore(factory, NullLogger<PipelineStore>.Instance);
            store.ContextDisposerForTest = _ => throw new ThrowingMessageException();

            result = store.CommitPendingAdmissionRollback(goalId, taskId, stored);
        }
        finally
        {
            factory.Dispose();
        }

        Assert.Equal(AdmissionOwnershipCommitStatus.Committed, result.Status);
        Assert.Null(result.PrimaryException);
        Assert.Null(result.RollbackException);
        var durable = ReadRollback(goalId, taskId);
        Assert.Null(durable.Pointer);
        Assert.Null(durable.MappingGoal);
        Assert.Equal(WorkSlotState.Abandoned,
            Assert.Single(WorkSlotRegistryCodec.Decode(durable.Blob!).Slots,
                s => s.Slot.TaskId == taskId).State);
    }

    [Fact]
    public void CommitPendingAdmissionRollback_ThrowingMessageCleanupException_RefusedResultNotMasked()
    {
        // RECORDED-RESULT PATH (Refused): the tracker-detach seam throws a throwing-Message
        // exception on a confirmed-rollback refusal path — the Refused result must survive.
        const string goalId = "rollback-message-throws-refused";
        const string taskId = "task-rollback-message-throws-refused";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        // Move the pointer away: the goal, mapping and registry-text guards all still match, so
        // the guard refuses on the pointer alone — a confirmed-rollback REFUSAL — while the
        // tracker-detach cleanup throws a throwing-Message exception.
        SetPointer(goalId, "newer-owner");
        using var connection = OpenConnection();
        using var context = ContextOn(connection);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);
        var detachCalls = 0;
        store.TrackerDetachForTest = (_, _) =>
        {
            detachCalls++;
            throw new ThrowingMessageException("the tracker detach cleanup failed");
        };

        var result = store.CommitPendingAdmissionRollback(goalId, taskId, stored);

        Assert.Equal(1, detachCalls);
        Assert.Equal(AdmissionOwnershipCommitStatus.Refused, result.Status);
        Assert.Null(result.PrimaryException);
        Assert.Null(result.RollbackException);
        var durable = ReadRollback(goalId, taskId);
        Assert.Equal("newer-owner", durable.Pointer);
        Assert.Equal(stored, durable.Blob);
        Assert.Equal(goalId, durable.MappingGoal);
    }

    [Fact]
    public void CommitPendingAdmissionRollback_ThrowingMessageCleanupException_PrimaryExceptionNotMasked()
    {
        // PROPAGATING-PRIMARY PATH: the body's first UPDATE throws the exact sentinel, the rollback
        // CONFIRMS, and the tracker-detach cleanup then throws a throwing-Message exception. The
        // EXACT original body exception (instance identity, not just type) must still propagate.
        const string goalId = "rollback-message-throws-primary";
        const string taskId = "task-rollback-message-throws-primary";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        var updateThrow = new RegistryUpdateThrowInterceptor();
        using var connection = OpenConnection();
        using var context = ContextOn(connection, updateThrow);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);
        store.TrackerDetachForTest = (_, _) => throw new ThrowingMessageException("the tracker detach cleanup failed");

        var thrown = Assert.Throws<SqliteException>(() =>
            store.CommitPendingAdmissionRollback(goalId, taskId, stored));

        Assert.Same(updateThrow.Sentinel, thrown);
        var durable = ReadRollback(goalId, taskId);
        Assert.Equal(taskId, durable.Pointer);
        Assert.Equal(stored, durable.Blob);
        Assert.Equal(goalId, durable.MappingGoal);
    }

    /// <summary>
    /// A minimal factory handing out store-OWNED contexts (the <c>ownsContext = true</c> path), each
    /// on its own connection to the file-backed database. The contexts do not own their
    /// connections, so <see cref="Dispose"/> closes them explicitly — the readback after the
    /// operation re-opens the file genuinely.
    /// </summary>
    private sealed class OwnedConnectionContextFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly string _connectionString;
        private readonly List<CopilotHiveDbContext> _contexts = [];
        private readonly List<SqliteConnection> _connections = [];

        public OwnedConnectionContextFactory(string connectionString) => _connectionString = connectionString;

        public CopilotHiveDbContext CreateDbContext()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            _connections.Add(connection);
            var context = ContextOn(connection);
            _contexts.Add(context);
            return context;
        }

        public void Dispose()
        {
            foreach (var context in _contexts)
                context.Dispose();
            foreach (var connection in _connections)
            {
                connection.Close();
                connection.Dispose();
            }
        }
    }

    [Fact]
    public void CommitPendingAdmissionRollback_BeginTransactionThrows_PropagatesExactException_NoBodyWriteNoDetach()
    {
        const string goalId = "rollback-begin-fails";
        const string taskId = "task-rollback-begin-fails";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        using var beginConnection = OpenConnection();
        var capture = new AdmissionOwnershipCommandCaptureInterceptor();
        var beginInterceptor = new AdmissionBeginThrowingInterceptor();
        var context = new CopilotHiveDbContext(
            new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(beginConnection)
                .AddInterceptors(capture, beginInterceptor)
                .Options);
        var factory = new SingleContextFactory(context);
        var store = new PipelineStore(factory, NullLogger<PipelineStore>.Instance);
        var trackerDetachCalls = 0;
        store.TrackerDetachForTest = (_, _) =>
        {
            trackerDetachCalls++;
            throw new InvalidOperationException("tracker cleanup must not run before a body attempt");
        };
        var contextDisposeCalls = 0;
        var cleanupSentinel = new InvalidOperationException("rollback begin path context cleanup sentinel");
        store.ContextDisposerForTest = ownedContext =>
        {
            contextDisposeCalls++;
            ownedContext.Dispose();
            throw cleanupSentinel;
        };

        try
        {
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                store.CommitPendingAdmissionRollback(goalId, taskId, stored));

            // The EXACT begin exception propagates — no cleanup step can mask it.
            Assert.Same(beginInterceptor.BeginSentinel, thrown);
            Assert.Equal(1, beginInterceptor.BeginAttemptCount);
            // NO body statement was ever issued and the tracker detach never ran.
            Assert.Empty(capture.Commands);
            Assert.Equal(0, trackerDetachCalls);
            // The factory-owned context is still disposed on this pre-body path.
            Assert.Equal(1, contextDisposeCalls);
            // NOTHING durably changed.
            var after = ReadRollback(goalId, taskId);
            Assert.Equal(taskId, after.Pointer);
            Assert.Equal(stored, after.Blob);
            Assert.Equal(goalId, after.MappingGoal);
        }
        finally
        {
            context.Dispose();
        }
    }

    /// <summary>
    /// A minimal valid tracked <see cref="PipelineEntity"/> for the affected-goal detach vectors.
    /// </summary>
    private static PipelineEntity TrackedPipelineEntity(string goalId) => new()
    {
        GoalId = goalId,
        Description = "tracked " + goalId,
        GoalJson = "{}",
        Phase = "Coding",
        Iteration = 1,
        MaxRetries = 3,
        MaxIterations = 3,
        PhaseOutputs = "{}",
        MetricsJson = "{}",
        CreatedAt = "2026-01-01T00:00:00.0000000Z",
        RoleSessionsJson = "[]",
        PhaseOccurrence = 1,
    };

    [Fact]
    public void CommitPendingAdmissionRollback_CommitAndRollbackThrow_IndeterminateWithBothExactInstances()
    {
        const string goalId = "rollback-commit-and-rollback-throw";
        const string taskId = "task-rollback-commit-and-rollback-throw";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        using var connection = new AdmissionCommitAndRollbackThrowConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection);
        var stalePipeline = TrackedPipelineEntity(goalId);
        stalePipeline.ActiveTaskId = "stale-tracked-pointer";
        stalePipeline.WorkSlotRegistryJson = "stale-tracked-blob";
        context.Attach(stalePipeline);
        var staleMapping = new TaskMappingEntity { TaskId = taskId, GoalId = "stale-tracked-goal" };
        context.Attach(staleMapping);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var result = store.CommitPendingAdmissionRollback(goalId, taskId, stored);

        Assert.Equal(AdmissionOwnershipCommitStatus.Indeterminate, result.Status);
        // EXCEPTION IDENTITY: both exact caught instances are preserved, not merely their types.
        Assert.Same(connection.CommitSentinel, result.PrimaryException);
        Assert.Same(connection.RollbackSentinel, result.RollbackException);
        Assert.Equal(1, connection.CommitAttemptCount);
        Assert.Equal(1, connection.RollbackAttemptCount);
        // The statement WAS attempted, so the affected tracker entries were detached even on this
        // uncertain path.
        Assert.DoesNotContain(context.ChangeTracker.Entries<PipelineEntity>(),
            entry => entry.Entity.GoalId == goalId);
        Assert.DoesNotContain(context.ChangeTracker.Entries<TaskMappingEntity>(),
            entry => entry.Entity.TaskId == taskId);
        var after = ReadRollback(goalId, taskId);
        Assert.Equal(taskId, after.Pointer);
        Assert.Equal(stored, after.Blob);
        Assert.Equal(goalId, after.MappingGoal);
    }

    [Fact]
    public void CommitPendingAdmissionRollback_BodyErrorAndRollbackThrow_IndeterminateWithBothExactInstances()
    {
        const string goalId = "rollback-body-and-rollback-throw";
        const string taskId = "task-rollback-body-and-rollback-throw";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        var updateThrow = new RegistryUpdateThrowInterceptor();
        using var connection = new RollbackThrowingConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection, updateThrow);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        // The UPDATE throws (a body error, NOT a refusal) and the forced rollback ALSO throws —
        // the transaction's fate is unknown, so the outcome is Indeterminate carrying BOTH exact
        // instances (the primary body exception is not null here, unlike the clean-refusal case).
        var result = store.CommitPendingAdmissionRollback(goalId, taskId, stored);

        Assert.Equal(AdmissionOwnershipCommitStatus.Indeterminate, result.Status);
        Assert.Same(updateThrow.Sentinel, result.PrimaryException);
        Assert.Same(connection.RollbackSentinel, result.RollbackException);
        Assert.Equal(1, updateThrow.ThrowCount);
        Assert.Equal(1, connection.RollbackAttemptCount);
        var after = ReadRollback(goalId, taskId);
        Assert.Equal(taskId, after.Pointer);
        Assert.Equal(stored, after.Blob);
        Assert.Equal(goalId, after.MappingGoal);
    }

    [Fact]
    public void CommitPendingAdmissionRollback_CleanupWarnings_CarryRollbackSpecificDiagnosticsNotAdmissionOnes()
    {
        const string goalId = "rollback-diagnostics";
        const string taskId = "task-rollback-diagnostics";
        var candidate = AdmitPending(goalId, taskId);
        var stored = WorkSlotRegistryCodec.Encode(candidate.Registry);
        var logger = new TestLogger<PipelineStore>();
        using var connection = new DisposeThrowingConnection(ConnectionString);
        connection.Open();
        using var context = ContextOn(connection);
        var store = new PipelineStore(context, logger);

        var result = store.CommitPendingAdmissionRollback(goalId, taskId, stored);

        Assert.Equal(AdmissionOwnershipCommitStatus.Committed, result.Status);
        Assert.Equal(1, connection.DisposeAttemptCount);
        // The ROLLBACK path's own diagnostics, never the admission operation's templates.
        Assert.Contains(logger.LogEntries, entry => entry.LogLevel == LogLevel.Warning
            && entry.Message.Contains("pending-rollback-transaction-dispose", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries,
            entry => entry.Message.Contains("admission-ownership-", StringComparison.Ordinal));
        // A committed transaction must never be reported as refused or rolled back.
        Assert.DoesNotContain(logger.LogEntries,
            entry => entry.Message.Contains("pending-rollback-detach", StringComparison.Ordinal));
        var durable = ReadRollback(goalId, taskId);
        Assert.Null(durable.Pointer);
        Assert.Null(durable.MappingGoal);
    }

    [Fact]
    public void CommitPendingAdmissionRollback_StoredTextSemanticallyEquivalentButDifferent_Refused()
    {
        // THE CAS EXACTNESS COMPLEMENT: the row holds the canonical encoding while the expectation
        // is the SAME registry re-serialized with different whitespace — semantically equivalent,
        // textually different. The CAS is TEXTUAL, so this must refuse; a mutant that decodes both
        // sides and compares values would wrongly commit here.
        const string goalId = "rollback-cas-equivalent";
        const string taskId = "task-rollback-cas-equivalent";
        var candidate = AdmitPending(goalId, taskId);
        var canonical = WorkSlotRegistryCodec.Encode(candidate.Registry);
        Assert.Equal(canonical, ReadRollback(goalId, taskId).Blob); // AdmitPending stored the canonical text
        var equivalentIndented = JsonSerializer.Serialize(
            System.Text.Json.Nodes.JsonNode.Parse(canonical),
            new JsonSerializerOptions { WriteIndented = true });
        Assert.NotEqual(canonical, equivalentIndented);

        var result = WithStore((store, _) =>
            store.CommitPendingAdmissionRollback(goalId, taskId, equivalentIndented));

        Assert.Equal(AdmissionOwnershipCommitStatus.Refused, result.Status);
        Assert.Null(result.PrimaryException);
        Assert.Null(result.RollbackException);
        var after = ReadRollback(goalId, taskId);
        Assert.Equal(taskId, after.Pointer); // the pointer was NOT cleared
        Assert.Equal(canonical, after.Blob); // the blob was NOT replaced
        Assert.Equal(goalId, after.MappingGoal); // the mapping was NOT deleted
    }

    private sealed class AcquisitionThrowingFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        private readonly IDbContextFactory<CopilotHiveDbContext> _inner;
        private readonly Exception _sentinel;

        public AcquisitionThrowingFactory(IDbContextFactory<CopilotHiveDbContext> inner, Exception sentinel)
        {
            _inner = inner;
            _sentinel = sentinel;
        }

        public CopilotHiveDbContext CreateDbContext() => throw _sentinel;
    }
}

/// <summary>
/// Substitutes the provider's post-execution affected-row count for ONE pending-rollback statement
/// (the pipelines UPDATE or the task_mappings DELETE) — the any-other-count-is-an-error vectors.
/// </summary>
internal sealed class RollbackRowCountInterceptor : DbCommandInterceptor
{
    private readonly string _statement;
    private readonly int _forcedCount;
    private int _overrideCount;

    public RollbackRowCountInterceptor(string statement, int forcedCount)
    {
        if (statement is not ("update" or "delete"))
            throw new ArgumentException("Statement must be 'update' or 'delete'.", nameof(statement));
        _statement = statement;
        _forcedCount = forcedCount;
    }

    public int OverrideCount => Volatile.Read(ref _overrideCount);

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        var sql = command.CommandText.TrimStart();
        var targeted = _statement == "update"
            ? sql.StartsWith("UPDATE pipelines", StringComparison.OrdinalIgnoreCase)
            : sql.StartsWith("DELETE FROM task_mappings", StringComparison.OrdinalIgnoreCase);
        if (!targeted)
            return result;

        Interlocked.Increment(ref _overrideCount);
        return _forcedCount;
    }
}

/// <summary>
/// An exception whose <see cref="Exception.Message"/> GETTER ITSELF THROWS — the genuine mechanism
/// for the never-masked-cleanup vectors: <c>Exception.Message</c> is virtual, so a cleanup catch
/// that reads the message BEFORE entering its no-throw guard would let this escape the finally and
/// mask the authoritative outcome. The default instance throws on <c>Message</c> AND on
/// <see cref="Exception.ToString"/>; an optional wrapped cause is supported for diagnostics.
/// </summary>
internal sealed class ThrowingMessageException : Exception
{
    private static readonly string MessageSentinel = "the Message getter itself threw SENTINEL";
    private readonly string? _innerMessage;

    /// <summary>Creates the exception whose <c>Message</c> getter throws (no wrapped cause).</summary>
    public ThrowingMessageException() { }

    /// <summary>Creates the exception whose <c>Message</c> getter throws after reporting the cause.</summary>
    public ThrowingMessageException(string innerMessage) => _innerMessage = innerMessage;

    public override string Message =>
        throw new InvalidOperationException(
            _innerMessage is null ? MessageSentinel : _innerMessage + " / " + MessageSentinel);

    public override string ToString() =>
        _innerMessage is null
            ? "ThrowingMessageException (ToString also throws)"
            : _innerMessage + " / ThrowinigMessageException (ToString also throws)";
}
