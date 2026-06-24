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
}