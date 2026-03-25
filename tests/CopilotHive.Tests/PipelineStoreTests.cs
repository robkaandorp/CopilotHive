using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public sealed class PipelineStoreTests : IAsyncDisposable
{
    private readonly PipelineStore _store;

    public PipelineStoreTests()
    {
        // Use in-memory SQLite for test isolation
        _store = new PipelineStore(":memory:", NullLogger<PipelineStore>.Instance);
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
        pipeline.IncrementIteration();
        pipeline.IncrementReviewRetry();
        pipeline.IncrementTestRetry();
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
    public void SavePipeline_ThenLoad_RestoresPhaseOutputs()
    {
        var pipeline = CreatePipeline();
        pipeline.RecordOutput(WorkerRole.Coder, 1, "code output");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "test output");

        _store.SavePipeline(pipeline);
        var snap = Assert.Single(_store.LoadActivePipelines());

        Assert.Equal(2, snap.PhaseOutputs.Count);
        Assert.Equal("code output", snap.PhaseOutputs["coder-1"]);
        Assert.Equal("test output", snap.PhaseOutputs["tester-1"]);
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
        pipeline.IncrementIteration();
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
        pipeline.IncrementIteration();
        _store.SavePipeline(pipeline);

        var snap = Assert.Single(_store.LoadActivePipelines());
        Assert.Equal(GoalPhase.Testing, snap.Phase);
        Assert.Equal(2, snap.Iteration);
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
            PhaseOutputs = new() { ["coder-1"] = "output1", ["tester-2"] = "output2" },
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
        Assert.Equal(2, pipeline.PhaseOutputs.Count);
        Assert.Equal("output1", pipeline.PhaseOutputs["coder-1"]);
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

        pipeline.IncrementIteration();
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
        _store = new PipelineStore(":memory:", NullLogger<PipelineStore>.Instance);
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
        pipeline.IncrementIteration();

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

/// <summary>
/// Tests that PipelineStore automatically migrates an older SQLite schema when opened.
/// </summary>
public sealed class PipelineStoreSchemaMigrationTests
{
    private static Goal CreateGoal(string id = "goal-1") =>
        new() { Id = id, Description = "Migration test goal", RepositoryNames = ["test-repo"] };

    /// <summary>
    /// Creates a named in-memory SQLite database with the old (incomplete) schema and returns
    /// the shared-cache connection string so PipelineStore can open the same instance.
    /// The seeder connection is stored in a field to prevent the named DB from being disposed.
    /// </summary>
    private (SqliteConnection Seeder, SqliteConnection Client) CreateOldSchemaConnections()
    {
        var name = $"migration-test-{Guid.NewGuid():N}";
        var connStr = $"Data Source={name};Mode=Memory;Cache=Shared";

        // Seeder keeps the named in-memory DB alive for the test's lifetime.
        var seeder = new SqliteConnection(connStr);
        seeder.Open();

        using var cmd = seeder.CreateCommand();
        // Old schema: missing max_iterations, improver_retries, phase_outputs, metrics_json, completed_at
        cmd.CommandText = """
            CREATE TABLE pipelines (
                goal_id        TEXT PRIMARY KEY,
                description    TEXT NOT NULL,
                goal_json      TEXT NOT NULL,
                phase          TEXT NOT NULL DEFAULT 'Planning',
                iteration      INTEGER NOT NULL DEFAULT 1,
                review_retries INTEGER NOT NULL DEFAULT 0,
                test_retries   INTEGER NOT NULL DEFAULT 0,
                max_retries    INTEGER NOT NULL DEFAULT 3,
                active_task_id TEXT,
                coder_branch   TEXT,
                created_at     TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS conversation_entries (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                goal_id TEXT NOT NULL,
                seq     INTEGER NOT NULL,
                role    TEXT NOT NULL,
                content TEXT NOT NULL,
                FOREIGN KEY (goal_id) REFERENCES pipelines(goal_id)
            );
            CREATE INDEX IF NOT EXISTS idx_conversation_goal ON conversation_entries(goal_id, seq);
            CREATE TABLE IF NOT EXISTS task_mappings (
                task_id TEXT PRIMARY KEY,
                goal_id TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // A second connection to the same named DB — handed to PipelineStore.
        var client = new SqliteConnection(connStr);
        client.Open();

        return (seeder, client);
    }

    [Fact]
    public async Task MigrateSchema_WhenColumnsAreMissing_StartsWithoutError()
    {
        var (seeder, client) = CreateOldSchemaConnections();
        await using (seeder)
        {
            var store = new PipelineStore(client, NullLogger<PipelineStore>.Instance);
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task MigrateSchema_WhenColumnsAreMissing_AddsAllExpectedColumns()
    {
        var (seeder, client) = CreateOldSchemaConnections();
        await using (seeder)
        await using (var store = new PipelineStore(client, NullLogger<PipelineStore>.Instance))
        {
            // Inspect via the seeder connection after migration.
            using var cmd = seeder.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_table_info('pipelines')";
            using var reader = cmd.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                columns.Add(reader.GetString(0));

            Assert.Contains("max_iterations", columns);
            Assert.Contains("improver_retries", columns);
            Assert.Contains("phase_outputs", columns);
            Assert.Contains("metrics_json", columns);
            Assert.Contains("completed_at", columns);
            Assert.Contains("merge_commit_hash", columns);
        }
    }

    [Fact]
    public async Task MigrateSchema_CalledTwice_IsIdempotent()
    {
        var name = $"migration-test-{Guid.NewGuid():N}";
        var connStr = $"Data Source={name};Mode=Memory;Cache=Shared";

        var seeder = new SqliteConnection(connStr);
        seeder.Open();
        using var seedCmd = seeder.CreateCommand();
        seedCmd.CommandText = """
            CREATE TABLE pipelines (
                goal_id TEXT PRIMARY KEY, description TEXT NOT NULL, goal_json TEXT NOT NULL,
                phase TEXT NOT NULL DEFAULT 'Planning', iteration INTEGER NOT NULL DEFAULT 1,
                review_retries INTEGER NOT NULL DEFAULT 0, test_retries INTEGER NOT NULL DEFAULT 0,
                max_retries INTEGER NOT NULL DEFAULT 3, active_task_id TEXT, coder_branch TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS conversation_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT, goal_id TEXT NOT NULL, seq INTEGER NOT NULL,
                role TEXT NOT NULL, content TEXT NOT NULL,
                FOREIGN KEY (goal_id) REFERENCES pipelines(goal_id)
            );
            CREATE INDEX IF NOT EXISTS idx_conversation_goal ON conversation_entries(goal_id, seq);
            CREATE TABLE IF NOT EXISTS task_mappings (task_id TEXT PRIMARY KEY, goal_id TEXT NOT NULL);
            """;
        seedCmd.ExecuteNonQuery();

        await using (seeder)
        {
            // First migration pass.
            var c1 = new SqliteConnection(connStr);
            c1.Open();
            await using (var first = new PipelineStore(c1, NullLogger<PipelineStore>.Instance)) { }

            // Second migration pass on the already-migrated schema — must not throw.
            var c2 = new SqliteConnection(connStr);
            c2.Open();
            await using (var second = new PipelineStore(c2, NullLogger<PipelineStore>.Instance)) { }

            // Verify no duplicate columns.
            using var cmd = seeder.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_table_info('pipelines')";
            using var reader = cmd.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
                columns.Add(reader.GetString(0).ToLowerInvariant());

            Assert.Equal(columns.Distinct().Count(), columns.Count);
        }
    }

    [Fact]
    public async Task MigrateSchema_AfterMigration_CanPersistAndReloadPipeline()
    {
        var (seeder, client) = CreateOldSchemaConnections();
        await using (seeder)
        await using (var store = new PipelineStore(client, NullLogger<PipelineStore>.Instance))
        {
            var pipeline = new GoalPipeline(CreateGoal("mg-1"));
            pipeline.AdvanceTo(GoalPhase.Coding);
            pipeline.IncrementIteration();
            pipeline.IncrementReviewRetry();
            pipeline.Metrics.TotalTests = 7;
            pipeline.Metrics.PassedTests = 7;

            store.SavePipeline(pipeline);
            var snapshots = store.LoadActivePipelines();

            var snap = Assert.Single(snapshots);
            Assert.Equal("mg-1", snap.GoalId);
            Assert.Equal(GoalPhase.Coding, snap.Phase);
            Assert.Equal(2, snap.Iteration);
            Assert.Equal(1, snap.ReviewRetries);
            Assert.Equal(7, snap.Metrics.TotalTests);
            Assert.Equal(7, snap.Metrics.PassedTests);
        }
    }

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
}
