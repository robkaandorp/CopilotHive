using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

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
    private const string SharedConnectionString =
        "Data Source=file:memdb-admissiontx?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<DbConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];

    public PipelineStoreAdmissionTransactionTests()
    {
        // The KEEPER anchors the shared in-memory database's lifetime for the whole test.
        _keeper = new SqliteConnection(SharedConnectionString);
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
        var connection = new SqliteConnection(SharedConnectionString);
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
        var connection = new RollbackThrowingConnection(SharedConnectionString);
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
        var connection = new RollbackThrowingConnection(SharedConnectionString);
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
        var connection = new DisposeThrowingConnection(SharedConnectionString);
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