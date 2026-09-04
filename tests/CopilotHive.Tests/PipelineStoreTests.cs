using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public sealed class PipelineStoreTests : IAsyncDisposable
{
    private readonly PipelineStore _store;

    public PipelineStoreTests()
    {
        // Use in-memory SQLite for test isolation
        _store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
    }

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static Goal CreateGoal(string id = "goal-1", string desc = "Test goal") =>
        new() { Id = id, Description = desc, RepositoryNames = ["test-repo"] };

    private static GoalPipeline CreatePipeline(string id = "goal-1", string desc = "Test goal", int maxRetries = 3)
    {
        var goal = CreateGoal(id, desc);
        return new GoalPipeline(goal, maxRetries);
    }

    #region SavePipeline / LoadActivePipelines — Round-trip

    [Fact]
    public void SavePipeline_ThenLoad_RestoresScalarState()
    {
        var pipeline = CreatePipeline("g1", "Implement feature");
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.IterationBudget.TryConsume();
        pipeline.ReviewRetryBudget.TryConsume();
        pipeline.TestRetryBudget.TryConsume();
        pipeline.SetActiveTask("task-42", "feature/g1");

        _store.SavePipeline(pipeline);
        var snapshots = _store.LoadActivePipelines();

        var snap = Assert.Single(snapshots);
        Assert.Equal("g1", snap.GoalId);
        Assert.Equal("Implement feature", snap.Description);
        Assert.Equal(GoalPhase.Coding, snap.Phase);
        Assert.Equal(2, snap.Iteration);
        Assert.Equal(1, snap.ReviewRetries);
        Assert.Equal(1, snap.TestRetries);
        Assert.Equal(3, snap.MaxRetries);
        Assert.Equal("task-42", snap.ActiveTaskId);
        Assert.Equal("feature/g1", snap.CoderBranch);
    }

    [Fact]
    public void SavePipeline_ThenLoad_RestoresGoalObject()
    {
        var pipeline = CreatePipeline("g2", "Fix bug in parser");

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Equal("g2", snap.Goal.Id);
        Assert.Equal("Fix bug in parser", snap.Goal.Description);
        Assert.Contains("test-repo", snap.Goal.RepositoryNames);
    }

    [Fact]
    public void SavePipeline_ThenLoad_RestoresConversation()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "Hello Brain"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Hello! Ready."));
        pipeline.Conversation.Add(new ConversationEntry("user", "Plan this goal"));

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Equal(3, snap.Conversation.Count);
        Assert.Equal("user", snap.Conversation[0].Role);
        Assert.Equal("Hello Brain", snap.Conversation[0].Content);
        Assert.Equal("assistant", snap.Conversation[1].Role);
        Assert.Equal("Hello! Ready.", snap.Conversation[1].Content);
        Assert.Equal("user", snap.Conversation[2].Role);
    }

    [Fact]
    public void SavePipeline_ThenLoad_RestoresConversationMetadata()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "Plan now", Iteration: 1, Purpose: "planning"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Here is the plan", Iteration: 1, Purpose: "planning"));
        pipeline.Conversation.Add(new ConversationEntry("system", "Error occurred", Iteration: 1, Purpose: "error"));

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Equal(1, snap.Conversation[0].Iteration);
        Assert.Equal("planning", snap.Conversation[0].Purpose);
        Assert.Equal(1, snap.Conversation[1].Iteration);
        Assert.Equal("planning", snap.Conversation[1].Purpose);
        Assert.Equal(1, snap.Conversation[2].Iteration);
        Assert.Equal("error", snap.Conversation[2].Purpose);
    }

    [Fact]
    public void SavePipeline_ThenLoad_HandlesNullMetadataForLegacyRows()
    {
        var pipeline = CreatePipeline();
        // Legacy-style entry with no metadata
        pipeline.Conversation.Add(new ConversationEntry("user", "Legacy message"));

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Single(snap.Conversation);
        Assert.Null(snap.Conversation[0].Iteration);
        Assert.Null(snap.Conversation[0].Purpose);
    }

    [Fact]
    public void SavePipeline_ThenLoad_RestoresPhaseLog()
    {
        var pipeline = CreatePipeline();
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "code output",
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "test output",
        });

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Equal(2, snap.PhaseLog.Count);
        Assert.Equal("code output", snap.PhaseLog[0].WorkerOutput);
        Assert.Equal("test output", snap.PhaseLog[1].WorkerOutput);
    }

    [Fact]
    public void SavePipeline_ThenLoad_RestoresMetrics()
    {
        var pipeline = CreatePipeline();
        pipeline.Metrics.BuildSuccess = true;
        pipeline.Metrics.TotalTests = 50;
        pipeline.Metrics.PassedTests = 48;
        pipeline.Metrics.FailedTests = 2;
        pipeline.Metrics.CoveragePercent = 85.5;

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.True(snap.Metrics.BuildSuccess);
        Assert.Equal(50, snap.Metrics.TotalTests);
        Assert.Equal(48, snap.Metrics.PassedTests);
        Assert.Equal(2, snap.Metrics.FailedTests);
        Assert.Equal(85.5, snap.Metrics.CoveragePercent);
    }

    [Fact]
    public void SavePipeline_ThenLoad_RestoresTimestamps()
    {
        var pipeline = CreatePipeline();
        var createdAt = pipeline.CreatedAt;

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        // Round-trip through ISO 8601 may lose sub-millisecond precision
        Assert.Equal(createdAt, snap.CreatedAt, TimeSpan.FromMilliseconds(1));
        Assert.Null(snap.CompletedAt);
    }

    #endregion

    #region LoadActivePipelines — Filtering

    [Fact]
    public void LoadActivePipelines_ExcludesDoneAndFailed()
    {
        var active = CreatePipeline("g-active", "Active");
        active.AdvanceTo(GoalPhase.Coding);

        var done = CreatePipeline("g-done", "Done");
        done.AdvanceTo(GoalPhase.Done);

        var failed = CreatePipeline("g-failed", "Failed");
        failed.AdvanceTo(GoalPhase.Failed);

        _store.SavePipeline(active);
        _store.SavePipeline(done);
        _store.SavePipeline(failed);

        var snapshots = _store.LoadActivePipelines();

        Assert.Single(snapshots);
        Assert.Equal("g-active", snapshots[0].GoalId);
    }

    [Fact]
    public void LoadActivePipelines_EmptyStore_ReturnsEmpty()
    {
        var snapshots = _store.LoadActivePipelines();

        Assert.Empty(snapshots);
    }

    #endregion

    #region SavePipelineState — State-only update

    [Fact]
    public void SavePipelineState_UpdatesScalarsWithoutTouchingConversation()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "Hello"));
        _store.SavePipeline(pipeline);

        // Mutate state and save state-only
        pipeline.AdvanceTo(GoalPhase.Review);
        pipeline.IterationBudget.TryConsume();
        _store.SavePipelineState(pipeline);

        var snap = Assert.Single(_store.LoadActivePipelines());
        Assert.Equal(GoalPhase.Review, snap.Phase);
        Assert.Equal(2, snap.Iteration);
        // Conversation should still be intact
        Assert.Single(snap.Conversation);
        Assert.Equal("Hello", snap.Conversation[0].Content);
    }

    #endregion

    #region AppendConversation

    [Fact]
    public void AppendConversation_AddsEntriesToExistingConversation()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "First"));
        _store.SavePipeline(pipeline);

        _store.AppendConversation("goal-1", new ConversationEntry("assistant", "Second"));
        _store.AppendConversation("goal-1", new ConversationEntry("user", "Third"));

        var snap = Assert.Single(_store.LoadActivePipelines());
        Assert.Equal(3, snap.Conversation.Count);
        Assert.Equal("First", snap.Conversation[0].Content);
        Assert.Equal("Second", snap.Conversation[1].Content);
        Assert.Equal("Third", snap.Conversation[2].Content);
    }

    [Fact]
    public void AppendConversation_PersistsMetadata()
    {
        var pipeline = CreatePipeline();
        _store.SavePipeline(pipeline);

        _store.AppendConversation("goal-1", new ConversationEntry("user", "Craft prompt", Iteration: 2, Purpose: "craft-prompt"));

        var snap = Assert.Single(_store.LoadActivePipelines());
        Assert.Single(snap.Conversation);
        Assert.Equal(2, snap.Conversation[0].Iteration);
        Assert.Equal("craft-prompt", snap.Conversation[0].Purpose);
    }

    #endregion

    #region SaveTaskMapping / LoadActivePipelines with TaskMappings

    [Fact]
    public void SaveTaskMapping_ThenLoad_RestoresTaskMappings()
    {
        var pipeline = CreatePipeline();
        _store.SavePipeline(pipeline);

        _store.SaveTaskMapping("task-1", "goal-1");
        _store.SaveTaskMapping("task-2", "goal-1");

        var snap = Assert.Single(_store.LoadActivePipelines());
        Assert.Equal(2, snap.TaskMappings.Count);
        Assert.Contains(("task-1", "goal-1"), snap.TaskMappings);
        Assert.Contains(("task-2", "goal-1"), snap.TaskMappings);
    }

    #endregion

    #region RemovePipeline

    [Fact]
    public void RemovePipeline_DeletesAllRelatedData()
    {
        var pipeline = CreatePipeline();
        pipeline.Conversation.Add(new ConversationEntry("user", "test"));
        _store.SavePipeline(pipeline);
        _store.SaveTaskMapping("task-1", "goal-1");

        _store.RemovePipeline("goal-1");

        Assert.Empty(_store.LoadActivePipelines());
    }

    [Fact]
    public void RemovePipeline_NonexistentGoal_DoesNotThrow()
    {
        var ex = Record.Exception(() => _store.RemovePipeline("nonexistent"));

        Assert.Null(ex);
    }

    #endregion

    #region Upsert behavior

    [Fact]
    public void SavePipeline_CalledTwice_UpsertsPipelineRow()
    {
        var pipeline = CreatePipeline();
        _store.SavePipeline(pipeline);

        pipeline.AdvanceTo(GoalPhase.Testing);
        pipeline.IterationBudget.TryConsume();
        _store.SavePipeline(pipeline);

        var snap = Assert.Single(_store.LoadActivePipelines());
        Assert.Equal(GoalPhase.Testing, snap.Phase);
        Assert.Equal(2, snap.Iteration);
    }

    #endregion

    #region MergeCommitHash round-trip

    [Fact]
    public void SavePipeline_WithMergeCommitHash_RoundTrips()
    {
        var pipeline = CreatePipeline("g-hash-1");
        pipeline.AdvanceTo(GoalPhase.Merging);
        pipeline.MergeCommitHash = "abc123def456";

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Equal("abc123def456", snap.MergeCommitHash);
    }

    [Fact]
    public void SavePipeline_WithNullMergeCommitHash_RoundTripsAsNull()
    {
        var pipeline = CreatePipeline("g-hash-2");

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Null(snap.MergeCommitHash);
    }

    [Fact]
    public void SavePipelineState_UpdatesMergeCommitHash()
    {
        var pipeline = CreatePipeline("g-hash-3");
        _store.SavePipeline(pipeline);

        pipeline.AdvanceTo(GoalPhase.Merging);
        pipeline.MergeCommitHash = "updated-merge-hash";
        _store.SavePipelineState(pipeline);

        var snap = Assert.Single(_store.LoadActivePipelines());
        Assert.Equal("updated-merge-hash", snap.MergeCommitHash);
    }

    [Fact]
    public void GoalPipeline_RestoredFromSnapshot_PreservesMergeCommitHash()
    {
        var pipeline = CreatePipeline("g-hash-4");
        pipeline.AdvanceTo(GoalPhase.Merging);
        pipeline.MergeCommitHash = "cafebabe9876";

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());
        var restored = new GoalPipeline(snap);

        Assert.Equal("cafebabe9876", restored.MergeCommitHash);
    }

    #endregion

    #region LoadPipeline / DeleteTaskMapping

    [Fact]
    public void LoadPipeline_LoadsFailedPipeline()
    {
        var pipeline = CreatePipeline("g-failed", "Failed goal");
        _store.SavePipeline(pipeline);
        pipeline.AdvanceTo(GoalPhase.Failed);
        _store.SavePipelineState(pipeline);

        var snap = _store.LoadPipeline("g-failed");

        Assert.NotNull(snap);
        Assert.Equal(GoalPhase.Failed, snap!.Phase);
    }

    [Fact]
    public void LoadPipeline_NonexistentGoal_ReturnsNull()
    {
        var snap = _store.LoadPipeline("nonexistent");

        Assert.Null(snap);
    }

    [Fact]
    public void LoadPipeline_IncludesConversationAndTaskMappings()
    {
        var pipeline = CreatePipeline("g-full", "Full load");
        pipeline.Conversation.Add(new ConversationEntry("user", "Hello"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Hi"));
        _store.SavePipeline(pipeline);
        _store.SaveTaskMapping("task-x", "g-full");

        var snap = _store.LoadPipeline("g-full");

        Assert.NotNull(snap);
        Assert.Equal(2, snap!.Conversation.Count);
        Assert.Equal("Hello", snap.Conversation[0].Content);
        Assert.Contains(("task-x", "g-full"), snap.TaskMappings);
    }

    [Fact]
    public void DeleteTaskMapping_RemovesMapping()
    {
        var pipeline = CreatePipeline("g-del", "Delete mapping");
        _store.SavePipeline(pipeline);
        _store.SaveTaskMapping("task-del", "g-del");

        _store.DeleteTaskMapping("task-del");

        var snap = _store.LoadPipeline("g-del");
        Assert.NotNull(snap);
        Assert.Empty(snap!.TaskMappings);
    }

    [Fact]
    public void DeleteTaskMapping_NonexistentTask_DoesNotThrow()
    {
        var ex = Record.Exception(() => _store.DeleteTaskMapping("nonexistent-task"));

        Assert.Null(ex);
    }

    #endregion

    #region GetConversation — Per-goal conversation retrieval

    [Fact]
    public void GetConversation_ReturnsEntriesForGoal()
    {
        var pipeline1 = CreatePipeline("goal-1", "First goal");
        pipeline1.Conversation.Add(new ConversationEntry("user", "Hello from goal 1"));
        pipeline1.Conversation.Add(new ConversationEntry("assistant", "Hi there!"));

        var pipeline2 = CreatePipeline("goal-2", "Second goal");
        pipeline2.Conversation.Add(new ConversationEntry("user", "Hello from goal 2"));

        _store.SavePipeline(pipeline1);
        _store.SavePipeline(pipeline2);

        var conversation1 = _store.GetConversation("goal-1");
        var conversation2 = _store.GetConversation("goal-2");

        Assert.Equal(2, conversation1.Count);
        Assert.Equal("user", conversation1[0].Role);
        Assert.Equal("Hello from goal 1", conversation1[0].Content);
        Assert.Equal("assistant", conversation1[1].Role);

        Assert.Single(conversation2);
        Assert.Equal("Hello from goal 2", conversation2[0].Content);
    }

    [Fact]
    public void GetConversation_NoEntries_ReturnsEmptyList()
    {
        var pipeline = CreatePipeline("goal-empty", "Empty goal");
        _store.SavePipeline(pipeline);

        var conversation = _store.GetConversation("goal-empty");

        Assert.Empty(conversation);
    }

    [Fact]
    public void GetConversation_NonExistentGoal_ReturnsEmptyList()
    {
        var conversation = _store.GetConversation("nonexistent-goal");

        Assert.Empty(conversation);
    }

    [Fact]
    public void GetConversation_PreservesMetadata()
    {
        var pipeline = CreatePipeline("goal-meta", "Goal with metadata");
        pipeline.Conversation.Add(new ConversationEntry("user", "Plan request", Iteration: 1, Purpose: "planning"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Here's the plan", Iteration: 1, Purpose: "planning"));
        pipeline.Conversation.Add(new ConversationEntry("user", "Brain prompt for coder", Iteration: 2, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Worker task for coder", Iteration: 2, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("coder", "Done!", Iteration: 2, Purpose: "worker-output"));

        _store.SavePipeline(pipeline);

        var conversation = _store.GetConversation("goal-meta");

        Assert.Equal(5, conversation.Count);
        Assert.Equal(1, conversation[0].Iteration);
        Assert.Equal("planning", conversation[0].Purpose);
        Assert.Equal(2, conversation[2].Iteration);
        Assert.Equal("craft-prompt", conversation[2].Purpose);
        Assert.Equal("worker-output", conversation[4].Purpose);
    }

    #endregion
}

public sealed class GoalPipelineSnapshotRestorationTests
{
    private static Goal CreateGoal(string id = "goal-1", string desc = "Test goal") =>
        new() { Id = id, Description = desc, RepositoryNames = ["test-repo"] };

    [Fact]
    public void GoalPipeline_FromSnapshot_RestoresAllState()
    {
        var snapshot = new PipelineSnapshot
        {
            GoalId = "g1",
            Description = "Restore test",
            Goal = CreateGoal("g1", "Restore test"),
            Phase = GoalPhase.Review,
            Iteration = 3,
            ReviewRetries = 1,
            TestRetries = 2,
            MaxRetries = 5,
            ActiveTaskId = "task-99",
            CoderBranch = "feature/restore",
            PhaseLog =
            [
                new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, Iteration = 1, Occurrence = 1, WorkerOutput = "output1" },
                new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Pass, Iteration = 2, Occurrence = 1, WorkerOutput = "output2" },
            ],
            Metrics = new() { Iteration = 3, BuildSuccess = true, TotalTests = 42, PassedTests = 40, FailedTests = 2 },
            CreatedAt = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            CompletedAt = null,
            Conversation = [new("user", "Hello"), new("assistant", "Hi")],
            TaskMappings = [("task-99", "g1")],
        };

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal("g1", pipeline.GoalId);
        Assert.Equal("Restore test", pipeline.Description);
        Assert.Equal(GoalPhase.Review, pipeline.Phase);
        Assert.Equal(3, pipeline.Iteration);
        Assert.Equal(1, pipeline.ReviewRetries);
        Assert.Equal(2, pipeline.TestRetries);
        Assert.Equal(5, pipeline.MaxRetries);
        Assert.Equal("task-99", pipeline.ActiveTaskId);
        Assert.Equal("feature/restore", pipeline.CoderBranch);
        Assert.Equal(2, pipeline.PhaseLog.Count);
        Assert.Equal("output1", pipeline.PhaseLog[0].WorkerOutput);
        Assert.True(pipeline.Metrics.BuildSuccess);
        Assert.Equal(42, pipeline.Metrics.TotalTests);
        Assert.Equal(2, pipeline.Conversation.Count);
        Assert.Equal(new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc), pipeline.CreatedAt);
        Assert.Null(pipeline.CompletedAt);
    }

    [Fact]
    public void GoalPipeline_FromSnapshot_CanContinueStateTransitions()
    {
        var snapshot = new PipelineSnapshot
        {
            GoalId = "g2",
            Description = "Continue test",
            Goal = CreateGoal("g2", "Continue test"),
            Phase = GoalPhase.Coding,
            Iteration = 1,
            MaxRetries = 3,
            Conversation = [],
            TaskMappings = [],
        };

        var pipeline = new GoalPipeline(snapshot);

        pipeline.AdvanceTo(GoalPhase.Review);
        Assert.Equal(GoalPhase.Review, pipeline.Phase);

        pipeline.IterationBudget.TryConsume();
        Assert.Equal(2, pipeline.Iteration);

        pipeline.SetActiveTask("new-task", "feature/cont");
        Assert.Equal("new-task", pipeline.ActiveTaskId);
    }
}

public sealed class GoalPipelineManagerPersistenceTests : IAsyncDisposable{
    private readonly PipelineStore _store;
    private readonly GoalPipelineManager _manager;

    public GoalPipelineManagerPersistenceTests()
    {
        _store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        _manager = new GoalPipelineManager(_store);
    }

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static Goal CreateGoal(string id = "goal-1", string desc = "Test goal") =>
        new() { Id = id, Description = desc, RepositoryNames = ["test-repo"] };

    [Fact]
    public void CreatePipeline_AutomaticallySavesToStore()
    {
        _manager.CreatePipeline(CreateGoal("g1", "Persisted"));

        var snapshots = _store.LoadActivePipelines();
        Assert.Single(snapshots);
        Assert.Equal("g1", snapshots[0].GoalId);
    }

    [Fact]
    public void RegisterTask_SavesTaskMappingToStore()
    {
        _manager.CreatePipeline(CreateGoal("g1", "Task mapping"));
        _manager.RegisterTask("task-1", "g1");

        var snapshots = _store.LoadActivePipelines();
        var snap = Assert.Single(snapshots);
        Assert.Contains(("task-1", "g1"), snap.TaskMappings);
    }

    [Fact]
    public void PersistState_SavesUpdatedStateToStore()
    {
        var pipeline = _manager.CreatePipeline(CreateGoal("g1", "State persist"));
        pipeline.AdvanceTo(GoalPhase.Testing);
        pipeline.IterationBudget.TryConsume();

        _manager.PersistState(pipeline);

        var snap = Assert.Single(_store.LoadActivePipelines());
        Assert.Equal(GoalPhase.Testing, snap.Phase);
        Assert.Equal(2, snap.Iteration);
    }

    [Fact]
    public void RemovePipeline_AlsoRemovesFromStore()
    {
        _manager.CreatePipeline(CreateGoal("g1", "To remove"));
        _manager.RegisterTask("task-1", "g1");

        _manager.RemovePipeline("g1");

        Assert.Empty(_store.LoadActivePipelines());
    }

    [Fact]
    public void RestoreFromStore_RebuildsInMemoryState()
    {
        // Save a pipeline directly via the store (simulating prior session)
        var original = new GoalPipeline(CreateGoal("g-restored", "Restored pipeline"));
        original.AdvanceTo(GoalPhase.Review);
        original.SetActiveTask("task-old", "feature/restored");
        _store.SavePipeline(original);
        _store.SaveTaskMapping("task-old", "g-restored");

        // Create a fresh manager with the same store
        var freshManager = new GoalPipelineManager(_store);
        var restored = freshManager.RestoreFromStore();

        Assert.Single(restored);
        Assert.Equal("g-restored", restored[0].GoalId);
        Assert.Equal(GoalPhase.Review, restored[0].Phase);

        // Verify in-memory lookups work
        Assert.NotNull(freshManager.GetByGoalId("g-restored"));
        Assert.NotNull(freshManager.GetByTaskId("task-old"));
    }

    [Fact]
    public void RestoreFromStore_WithNoActivePipelines_ReturnsEmpty()
    {
        var freshManager = new GoalPipelineManager(_store);
        var restored = freshManager.RestoreFromStore();

        Assert.Empty(restored);
    }

    [Fact]
    public void RestoreFromStore_SkipsTerminalPipelines()
    {
        var done = new GoalPipeline(CreateGoal("g-done", "Completed"));
        done.AdvanceTo(GoalPhase.Done);
        _store.SavePipeline(done);

        var active = new GoalPipeline(CreateGoal("g-active", "In progress"));
        active.AdvanceTo(GoalPhase.Coding);
        _store.SavePipeline(active);

        var freshManager = new GoalPipelineManager(_store);
        var restored = freshManager.RestoreFromStore();

        Assert.Single(restored);
        Assert.Equal("g-active", restored[0].GoalId);
    }
}

public sealed class PipelineStoreRoleSessionTests : IAsyncDisposable
{
    private readonly PipelineStore _store;

    public PipelineStoreRoleSessionTests()
    {
        _store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
    }

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static Goal CreateGoal(string id = "goal-1") =>
        new() { Id = id, Description = "Test goal", RepositoryNames = ["repo"] };

    private static GoalPipeline CreatePipeline(string id = "goal-1")
    {
        var goal = CreateGoal(id);
        return new GoalPipeline(goal);
    }

    [Fact]
    public void SavePipeline_ThenLoad_RestoresRoleSessions()
    {
        var pipeline = CreatePipeline("g1");
        pipeline.SetRoleSession("coder", """{"msg":"hello"}""");
        pipeline.SetRoleSession("reviewer", """{"msg":"reviewed"}""");

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Equal("""{"msg":"hello"}""", snap.RoleSessions["coder"]);
        Assert.Equal("""{"msg":"reviewed"}""", snap.RoleSessions["reviewer"]);
    }

    [Fact]
    public void SavePipeline_NoRoleSessions_SnapshotHasEmptyDictionary()
    {
        var pipeline = CreatePipeline("g1");

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Empty(snap.RoleSessions);
    }

    [Fact]
    public void SavePipelineState_ThenLoad_RoleSessionsArePreserved()
    {
        var pipeline = CreatePipeline("g1");
        _store.SavePipeline(pipeline);

        // Now set a session and save state only
        pipeline.SetRoleSession("tester", "tester-session");
        _store.SavePipelineState(pipeline);

        var snap = Assert.Single(_store.LoadActivePipelines());
        Assert.Equal("tester-session", snap.RoleSessions["tester"]);
    }

    [Fact]
    public void RoundTrip_ViaGoalPipeline_SessionsSurviveSnapshotRestore()
    {
        var pipeline = CreatePipeline("g1");
        pipeline.SetRoleSession("coder", "coder-data");
        pipeline.SetRoleSession("Tester", "tester-data");

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());
        var restored = new GoalPipeline(snap);

        Assert.Equal("coder-data",  restored.GetRoleSession("coder"));
        Assert.Equal("tester-data", restored.GetRoleSession("tester"));
    }

    [Fact]
    public void RoundTrip_RoleSessionsAreCaseInsensitiveAfterRestore()
    {
        var pipeline = CreatePipeline("g1");
        pipeline.SetRoleSession("CODER", "uppercase-stored");

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());
        var restored = new GoalPipeline(snap);

        // Any casing should work
        Assert.Equal("uppercase-stored", restored.GetRoleSession("coder"));
        Assert.Equal("uppercase-stored", restored.GetRoleSession("Coder"));
        Assert.Equal("uppercase-stored", restored.GetRoleSession("CODER"));
    }

    [Fact]
    public void RoundTrip_IterationStartSha_IsPersistedAndRestored()
    {
        // Arrange — pipeline with a non-null IterationStartSha
        const string sha = "abc123def456789012345678901234567890abcd";
        var pipeline = CreatePipeline("g-sha-persist");
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.IterationStartSha = sha;

        // Act — save and reload
        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        // Assert — snapshot carries the SHA
        Assert.Equal(sha, snap.IterationStartSha);

        // Assert — restored pipeline also carries the SHA
        var restored = new GoalPipeline(snap);
        Assert.Equal(sha, restored.IterationStartSha);
    }

    [Fact]
    public void RoundTrip_IterationStartSha_NullIsPreservedAsNull()
    {
        // Arrange — pipeline without a SHA (empty repo or first-dispatch edge case)
        var pipeline = CreatePipeline("g-sha-null");
        pipeline.AdvanceTo(GoalPhase.Coding);
        // IterationStartSha is not set — defaults to null

        // Act
        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        // Assert — null survives the round-trip
        Assert.Null(snap.IterationStartSha);
        var restored = new GoalPipeline(snap);
        Assert.Null(restored.IterationStartSha);
    }

    [Fact]
    public void RoundTrip_IterationStartSha_UpdatedValueOverwritesPrevious()
    {
        // Arrange — pipeline whose SHA is updated between saves (new iteration)
        const string sha1 = "1111111111111111111111111111111111111111";
        const string sha2 = "2222222222222222222222222222222222222222";
        var pipeline = CreatePipeline("g-sha-update");
        pipeline.AdvanceTo(GoalPhase.Coding);

        pipeline.IterationStartSha = sha1;
        _store.SavePipeline(pipeline);

        // Simulate a new iteration: update SHA
        pipeline.IterationStartSha = sha2;
        _store.SavePipeline(pipeline);

        // Act — reload
        var snap = Assert.Single(_store.LoadActivePipelines());

        // Assert — latest SHA wins
        Assert.Equal(sha2, snap.IterationStartSha);
    }
}


/// <summary>
/// Slice E2a-i — <see cref="PipelineStore.SaveAdmissionWithPointer"/> on the DIRECT-CONTEXT
/// paths: the two validation refusals (no write on either), the pre-existing row's reload
/// after a CONFIRMED rollback, the new row's Added-state detach and the deferred-orphan
/// proof, the SUCCESS-PATH dispose failure (the durable commit still returns
/// <see cref="AdmissionStoreResult.Committed"/>), and the factory-context guard.
/// </summary>
public sealed class PipelineStoreAdmissionDirectContextTests : IDisposable
{
    private const string SharedConnectionString =
        "Data Source=file:memdb-admissiondirect?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<DbConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];
    private readonly List<IDisposable> _disposables = [];

    public PipelineStoreAdmissionDirectContextTests()
    {
        _keeper = new SqliteConnection(SharedConnectionString);
        _keeper.Open();
        CreateContext().Database.EnsureCreated();
        // The shared named database survives across class instances (the keeper is
        // per-instance); each test starts from a clean slate so the "no write" proofs and
        // count assertions observe only their own call's effects.
        ExecuteOnKeeper("DELETE FROM task_mappings");
        ExecuteOnKeeper("DELETE FROM pipelines");
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        foreach (var connection in _connections)
            connection.Dispose();
        foreach (var disposable in _disposables)
            disposable.Dispose();
        _keeper.Dispose();
    }

    // ───────────────────────────── fixture helpers ─────────────────────────────

    /// <summary>Creates a direct (caller-owned) context on its OWN connection to the shared database.</summary>
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
    // (1) The validation refusals — no write on either
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>THE BLANK REFUSAL: <c>ArgumentException</c> with <c>ParamName == "taskId"</c>, no DB write.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveAdmission_BlankTaskId_ThrowsWithParamName_AndDoesNotWrite(string blank)
    {
        var pipeline = CreatePipeline("goal-blank", "task-blank");
        var store = CreateStore();

        var ex = Assert.Throws<ArgumentException>(() => store.SaveAdmissionWithPointer(pipeline, blank));
        Assert.Equal("taskId", ex.ParamName);

        // NO WRITE: the mapping and the pipeline rows are both absent.
        Assert.Null(ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-blank'"));
        Assert.Equal(0L, ExecuteScalarOnKeeper("SELECT COUNT(*) FROM pipelines"));
    }

    /// <summary>
    /// THE MISMATCH REFUSAL: <c>pipeline.ActiveTaskId != taskId</c> → <c>ArgumentException</c>
    /// with <c>ParamName == "taskId"</c> AND BOTH values in the message; no DB write.
    /// </summary>
    [Fact]
    public void SaveAdmission_ActiveTaskIdMismatch_ThrowsWithBothValues_AndDoesNotWrite()
    {
        var pipeline = CreatePipeline("goal-mismatch", "task-live");
        var store = CreateStore();

        var ex = Assert.Throws<ArgumentException>(() => store.SaveAdmissionWithPointer(pipeline, "task-other"));
        Assert.Equal("taskId", ex.ParamName);
        // BOTH values are present in the message: the pipeline's pointer AND the passed id.
        Assert.Contains("task-live", ex.Message, StringComparison.Ordinal);
        Assert.Contains("task-other", ex.Message, StringComparison.Ordinal);

        // NO WRITE on the refusal.
        Assert.Null(ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-live'"));
        Assert.Null(ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-other'"));
        Assert.Equal(0L, ExecuteScalarOnKeeper("SELECT COUNT(*) FROM pipelines"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) The pre-existing row's reload after the CONFIRMED rollback
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PRE-EXISTING ROW'S RELOAD: a failure (an exception at the pipeline flush) + the
    /// CONFIRMED rollback → the pipeline entity is RELOADED (not merely detached) — a
    /// subsequent <c>Find</c> on the SAME context shows the PREVIOUS pipeline pointer intact.
    /// </summary>
    [Fact]
    public void SaveAdmission_FailureWithConfirmedRollback_ReloadShowsPreviousPointer()
    {
        // Seed the pipeline row directly with a PREVIOUS pointer.
        ExecuteOnKeeper(
            """
            INSERT INTO pipelines (goal_id, description, goal_json, phase, metrics_json, active_task_id, created_at)
            VALUES ('goal-reload', 'Previous', '{"id":"goal-reload","description":"reload goal","repositories":["test-repo"]}', 'Planning', '{}', 'task-previous', '2025-06-15T10:00:00.0000000Z')
            """);
        var interceptor = new AdmissionTargetedThrowInterceptor(
            AdmissionTargetedThrowInterceptor.Target.Pipelines, 5, 5);
        var store = CreateStore(interceptor);
        var pipeline = CreatePipeline("goal-reload", "task-new");
        pipeline.AdvanceTo(GoalPhase.Coding); // a DIFFERENT value the failed save must not leave behind

        Assert.ThrowsAny<Exception>(() => store.SaveAdmissionWithPointer(pipeline, "task-new"));

        // THE RELOAD: a Find on the SAME (direct) context surfaces the PREVIOUS row intact —
        // the stale in-flight copy was discarded through the tracker, not left ghosting.
        var row = store.LoadPipeline("goal-reload");
        Assert.NotNull(row);
        Assert.Equal("task-previous", row!.ActiveTaskId);
        Assert.Equal(GoalPhase.Planning, row.Phase);

        // And the mapping insert was rolled back — the pointer's task never persisted.
        Assert.Null(ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-new'"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) The new row: the Added-state detach + the deferred-orphan proof
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE NEW ROW: the pipeline entity is in the Added state when the pipeline flush fails →
    /// after the CONFIRMED rollback the entity is DETACHED (Find re-reads fresh: no row), and
    /// the DEFERRED-ORPHAN proof holds — the mapping Add was rolled back, so no orphan row
    /// persists.
    /// </summary>
    [Fact]
    public void SaveAdmission_NewRowPipelineFlushFails_AddedStateDetached_NoOrphanRow()
    {
        var interceptor = new AdmissionTargetedThrowInterceptor(
            AdmissionTargetedThrowInterceptor.Target.Pipelines, 5, 5);
        var store = CreateStore(interceptor);
        var pipeline = CreatePipeline("goal-newrow", "task-newrow");

        Assert.ThrowsAny<Exception>(() => store.SaveAdmissionWithPointer(pipeline, "task-newrow"));

        // THE DETACH: the direct context re-reads FRESH (the Added copy was detached).
        var row = store.LoadPipeline("goal-newrow");
        Assert.Null(row);
        // THE DEFERRED-ORPHAN PROOF: the mapping Add was rolled back.
        Assert.Null(ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-newrow'"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) The success-path dispose failure — Committed STILL RETURNED
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SUCCESS-PATH DISPOSE FAILURE: the commit SUCCEEDED, the transaction dispose is
    /// forced to fail → the <c>admission-dispose</c> warning is logged, the swallow happens,
    /// and <see cref="AdmissionStoreResult.Committed"/> IS STILL RETURNED — the durable
    /// commit is the outcome.
    /// </summary>
    [Fact]
    public void SaveAdmission_SuccessPath_TransactionDisposeFails_CommittedStillReturned()
    {
        var logger = new TestLogger<PipelineStore>();
        var connection = new ThrowingDisposeTransactionConnection(SharedConnectionString);
        _connections.Add(connection);
        connection.Open();
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        var context = new CopilotHiveDbContext(builder.Options);
        _contexts.Add(context);
        var store = new PipelineStore(context, logger);
        var pipeline = CreatePipeline("goal-dispose", "task-dispose");

        var result = store.SaveAdmissionWithPointer(pipeline, "task-dispose");

        // THE DURABLE COMMIT IS THE OUTCOME.
        Assert.Equal(AdmissionStoreResult.Committed, result);
        Assert.Equal("goal-dispose", ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-dispose'"));
        Assert.Equal(1L, ExecuteScalarOnKeeper(
            "SELECT COUNT(*) FROM pipelines WHERE goal_id = 'goal-dispose'"));

        // THE DISPOSE WARNING was logged with the identifiers.
        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("admission-dispose", warning.Message, StringComparison.Ordinal);
        Assert.Contains("goal-dispose", warning.Message, StringComparison.Ordinal);
        Assert.Contains("task-dispose", warning.Message, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) The factory-context guard — ownsContext == true
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FACTORY-CONTEXT GUARD, THROWING SEAM: the <c>ownsContext == true</c> path; the
    /// <see cref="PipelineStore.ContextDisposerForTest"/> seam throws → the
    /// <c>admission-context-dispose</c> warning is logged, the swallow happens, and the
    /// outcome (Committed) is preserved.
    /// </summary>
    [Fact]
    public void SaveAdmission_FactoryContextDisposeFails_WarnsAndPreservesOutcome()
    {
        var logger = new TestLogger<PipelineStore>();
        var store = new PipelineStore(new PlainFactory(SharedConnectionString), logger);
        store.ContextDisposerForTest = _ => throw new InvalidOperationException("forced context dispose failure");
        var pipeline = CreatePipeline("goal-factory", "task-factory");

        var result = store.SaveAdmissionWithPointer(pipeline, "task-factory");

        // THE OUTCOME is preserved.
        Assert.Equal(AdmissionStoreResult.Committed, result);
        Assert.Equal("goal-factory", ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-factory'"));

        // THE CONTEXT-DISPOSE WARNING was logged with the identifiers.
        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("admission-context-dispose", warning.Message, StringComparison.Ordinal);
        Assert.Contains("goal-factory", warning.Message, StringComparison.Ordinal);
        Assert.Contains("task-factory", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE FACTORY-CONTEXT GUARD, FAILURE PATH: the same throwing seam on a goal whose
    /// admission FAILS (a pipeline-flush exception) → the ORIGINAL exception still propagates
    /// (never masked by the dispose failure), the warning is logged, and the swallow holds.
    /// </summary>
    [Fact]
    public void SaveAdmission_FactoryContextDisposeFails_OnFailurePath_OriginalExceptionPreserved()
    {
        var logger = new TestLogger<PipelineStore>();
        var interceptor = new AdmissionTargetedThrowInterceptor(
            AdmissionTargetedThrowInterceptor.Target.Pipelines, 5, 5);
        var store = new PipelineStore(new PlainFactory(SharedConnectionString, interceptor), logger);
        store.ContextDisposerForTest = _ => throw new InvalidOperationException("forced context dispose failure");
        var pipeline = CreatePipeline("goal-factoryfail", "task-factoryfail");

        Assert.ThrowsAny<Exception>(() => store.SaveAdmissionWithPointer(pipeline, "task-factoryfail"));

        // THE WARNING was logged…
        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("admission-context-dispose", warning.Message, StringComparison.Ordinal);
        // …and the MAPPING insert was rolled back (the failure path's cleanup ran).
        Assert.Null(ExecuteScalarOnKeeper(
            "SELECT goal_id FROM task_mappings WHERE task_id = 'task-factoryfail'"));
    }

    /// <summary>
    /// THE CALLER-OWNED GUARD: with the direct (caller-owned) context, the disposer seam is
    /// invoked ZERO times — the genuine, removal-proof proof of the "the caller-owned direct
    /// context is NEVER disposed here" rule (the ownsContext-gating mutant — the guard running
    /// unconditionally — is killed by this vector).
    /// </summary>
    [Fact]
    public void SaveAdmission_DirectContext_DisposerSeamNeverInvoked()
    {
        var logger = new TestLogger<PipelineStore>();
        var store = CreateStore(logger: logger);
        var disposerCalls = 0;
        store.ContextDisposerForTest = _ => disposerCalls++;
        var pipeline = CreatePipeline("goal-nodispose", "task-nodispose");

        var result = store.SaveAdmissionWithPointer(pipeline, "task-nodispose");

        Assert.Equal(AdmissionStoreResult.Committed, result);
        Assert.Equal(0, disposerCalls);
    }

}

/// <summary>
/// A minimal <see cref="IDbContextFactory{CopilotHiveDbContext}"/> handing out real contexts
/// over the shared database (the ownsContext == true path).
/// </summary>
public sealed class PlainFactory : IDbContextFactory<CopilotHiveDbContext>
{
    private readonly string _connectionString;
    private readonly IInterceptor? _interceptor;

    public PlainFactory(string connectionString, IInterceptor? interceptor = null)
    {
        _connectionString = connectionString;
        _interceptor = interceptor;
    }

    public CopilotHiveDbContext CreateDbContext()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (_interceptor is not null)
            builder.AddInterceptors(_interceptor);
        return new CopilotHiveDbContext(builder.Options);
    }
}

/// <summary>
/// A <see cref="DbConnection"/> whose transactions commit normally but THROW on Dispose —
/// used by the success-path dispose-failure vector.
/// </summary>
public sealed class ThrowingDisposeTransactionConnection : DbConnection
{
    private readonly SqliteConnection _inner;

    public ThrowingDisposeTransactionConnection(string connectionString) =>
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

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new ThrowingDisposeTransaction(this, (SqliteTransaction)_inner.BeginTransaction(isolationLevel));

    protected override DbCommand CreateDbCommand() => new TransactionTolerantAdmissionCommand(_inner.CreateCommand());

    /// <summary>
    /// The EF relational layer requires a transaction's connection to be REFERENCE-EQUAL to the
    /// connection it was begun on; <see cref="SqliteCommand"/> casts its transaction back to
    /// <see cref="SqliteTransaction"/>, so the wrapped command shields both.
    /// </summary>
    private sealed class ThrowingDisposeTransaction : DbTransaction
    {
        private readonly ThrowingDisposeTransactionConnection _owner;
        private readonly SqliteTransaction _inner;

        public ThrowingDisposeTransaction(ThrowingDisposeTransactionConnection owner, SqliteTransaction inner)
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
                throw new InvalidOperationException("forced transaction dispose failure");
            }
        }
    }

    private sealed class TransactionTolerantAdmissionCommand : DbCommand
    {
        private readonly DbCommand _inner;

        public TransactionTolerantAdmissionCommand(DbCommand inner) => _inner = inner;

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
