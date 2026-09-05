using System.Data.Common;

using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Covers the PREPARATORY mapping-ownership APIs of the work-slot integrity chain:
/// <see cref="GoalPipeline.ClearActiveTaskIfCurrent"/>, the conditional persisted operations
/// (<see cref="PipelineStore.TrySaveTaskMappingIfUnowned"/> /
/// <see cref="PipelineStore.DeleteTaskMappingIfForGoal"/>) and the ownership-checked
/// <see cref="GoalPipelineManager.TryRegisterTask"/> / <see cref="GoalPipelineManager.TryUnregisterTask"/>.
/// </summary>
/// <remarks>
/// All persisted vectors run against a SINGLE shared-cache in-memory SQLite database, anchored by a
/// long-lived keeper connection. The "racer" always uses its OWN context on its OWN connection to
/// that same database, so a competing row is genuinely another writer's row rather than a change
/// tracker artifact. Assertions about row state read the database RAW (through the keeper
/// connection), bypassing EF Core entirely.
/// </remarks>
public sealed class WorkSlotMappingOwnershipTests : IDisposable
{
    private readonly string _connectionString =
        $"Data Source=file:memdb-workslotmapping-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<SqliteConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];

    public WorkSlotMappingOwnershipTests()
    {
        // The KEEPER anchors the shared-cache in-memory database's lifetime for the whole test.
        // The database name is per-instance unique, so no rows can leak across xUnit fixture
        // instances (each instance gets its own unshared database).
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();

        CreateContext().Database.EnsureCreated();
        ExecuteOnKeeper("DELETE FROM task_mappings");
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

    private PipelineStore CreateStore(IInterceptor? interceptor = null) =>
        new(CreateContext(interceptor), NullLogger<PipelineStore>.Instance);

    /// <summary>The racer: an independent store on its own context performing the LEGACY unconditional write.</summary>
    private void RacerWritesMapping(string taskId, string goalId) =>
        CreateStore().SaveTaskMapping(taskId, goalId);

    private void ExecuteOnKeeper(string sql)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Reads the persisted goal id for a task RAW — no EF Core, no change tracker.</summary>
    private string? ReadPersistedGoalId(string taskId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "SELECT goal_id FROM task_mappings WHERE task_id = $taskId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$taskId";
        parameter.Value = taskId;
        command.Parameters.Add(parameter);
        return command.ExecuteScalar() as string;
    }

    private static Goal CreateGoal(string id) =>
        new() { Id = id, Description = "goal " + id, RepositoryNames = ["test-repo"] };

    private static GoalPipeline CreatePipeline(string goalId = "goal-1") =>
        new(CreateGoal(goalId));

    // ═══════════════════════════════════════════════════════════════════════
    // (1) GoalPipeline.ClearActiveTaskIfCurrent — the four cases
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ClearActiveTaskIfCurrent_MatchingTask_ClearsPointerAndReturnsTrue()
    {
        var pipeline = CreatePipeline();
        pipeline.SetActiveTask("task-1");

        Assert.True(pipeline.ClearActiveTaskIfCurrent("task-1"));
        Assert.Null(pipeline.ActiveTaskId);
    }

    [Fact]
    public void ClearActiveTaskIfCurrent_DifferentTask_LeavesPointerAndReturnsFalse()
    {
        var pipeline = CreatePipeline();
        pipeline.SetActiveTask("task-live");

        // Removing the ownership check would clear a LIVE dispatch's pointer here.
        Assert.False(pipeline.ClearActiveTaskIfCurrent("task-stale"));
        Assert.Equal("task-live", pipeline.ActiveTaskId);
    }

    [Fact]
    public void ClearActiveTaskIfCurrent_NullPointer_ReturnsFalse()
    {
        var pipeline = CreatePipeline();
        Assert.Null(pipeline.ActiveTaskId);

        Assert.False(pipeline.ClearActiveTaskIfCurrent("task-1"));
        Assert.Null(pipeline.ActiveTaskId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ClearActiveTaskIfCurrent_BlankArgument_ReturnsFalseAndLeavesPointer(string blank)
    {
        var pipeline = CreatePipeline();
        pipeline.SetActiveTask("task-live");

        Assert.False(pipeline.ClearActiveTaskIfCurrent(blank));
        Assert.Equal("task-live", pipeline.ActiveTaskId);
    }

    [Fact]
    public void ClearActiveTaskIfCurrent_NullArgument_ReturnsFalseAndLeavesPointer()
    {
        var pipeline = CreatePipeline();
        pipeline.SetActiveTask("task-live");

        Assert.False(pipeline.ClearActiveTaskIfCurrent(null!));
        Assert.Equal("task-live", pipeline.ActiveTaskId);
    }

    /// <summary>
    /// PRESERVATION: the pre-existing unconditional <see cref="GoalPipeline.ClearActiveTask"/>
    /// still clears whatever pointer is set, regardless of which task set it.
    /// </summary>
    [Fact]
    public void ClearActiveTask_UnconditionalBehaviorPreserved()
    {
        var pipeline = CreatePipeline();
        pipeline.SetActiveTask("task-live");

        pipeline.ClearActiveTask();

        Assert.Null(pipeline.ActiveTaskId);

        // …and it remains a no-throw no-op when nothing is active.
        pipeline.ClearActiveTask();
        Assert.Null(pipeline.ActiveTaskId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) PipelineStore.TrySaveTaskMappingIfUnowned
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void TrySaveTaskMappingIfUnowned_AbsentRow_InsertsAndReturnsTrue()
    {
        var store = CreateStore();

        Assert.True(store.TrySaveTaskMappingIfUnowned("save-absent", "goal-ours"));
        Assert.Equal("goal-ours", ReadPersistedGoalId("save-absent"));
    }

    [Fact]
    public void TrySaveTaskMappingIfUnowned_OwnRow_IsIdempotentAndReturnsTrue()
    {
        var store = CreateStore();
        Assert.True(store.TrySaveTaskMappingIfUnowned("save-own", "goal-ours"));

        Assert.True(store.TrySaveTaskMappingIfUnowned("save-own", "goal-ours"));
        Assert.Equal("goal-ours", ReadPersistedGoalId("save-own"));
    }

    /// <summary>
    /// The ownership guard: the <c>WHERE goal_id = @goalId</c> on the conflict branch. Removing it
    /// would let this call STEAL the competing row — the assertion on the row's unchanged value
    /// (not merely the returned false) is what pins the physical-schema SQL down.
    /// </summary>
    [Fact]
    public void TrySaveTaskMappingIfUnowned_OtherGoalsRow_ReturnsFalseAndLeavesRowIntact()
    {
        RacerWritesMapping("save-conflict", "goal-racer");
        var store = CreateStore();

        Assert.False(store.TrySaveTaskMappingIfUnowned("save-conflict", "goal-ours"));
        Assert.Equal("goal-racer", ReadPersistedGoalId("save-conflict"));
    }

    [Fact]
    public void TrySaveTaskMappingIfUnowned_StoreFailure_Propagates()
    {
        var sentinel = new InvalidOperationException("save-sentinel");
        var store = CreateStore(new SentinelThrowingInterceptor(sentinel, "INSERT"));

        var thrown = Assert.Throws<InvalidOperationException>(
            () => store.TrySaveTaskMappingIfUnowned("save-throw", "goal-ours"));

        Assert.Same(sentinel, thrown);
        // The interceptor throws BEFORE the statement runs — nothing was written.
        Assert.Null(ReadPersistedGoalId("save-throw"));
    }

    /// <summary>
    /// DETACH RULE (refusal path): a stale tracked entity must not survive the conditional write.
    /// Removing the detach makes the post-call <c>Find</c> hand back the pre-race copy.
    /// </summary>
    [Fact]
    public void TrySaveTaskMappingIfUnowned_RefusalPath_DetachesStaleTrackedEntity()
    {
        RacerWritesMapping("save-detach-refuse", "goal-a");

        var db = CreateContext();
        var store = new PipelineStore(db, NullLogger<PipelineStore>.Instance);

        var tracked = db.TaskMappings.Find("save-detach-refuse");
        Assert.Equal("goal-a", tracked!.GoalId);

        // The racer moves the row behind the store's back.
        RacerWritesMapping("save-detach-refuse", "goal-b");

        Assert.False(store.TrySaveTaskMappingIfUnowned("save-detach-refuse", "goal-c"));

        // No ChangeTracker.Clear() here on purpose: the detach itself must make the re-read honest.
        var observed = db.TaskMappings.Find("save-detach-refuse");
        Assert.Equal("goal-b", observed!.GoalId);
    }

    /// <summary>DETACH RULE (success path): the tracked copy is dropped after a successful claim.</summary>
    [Fact]
    public void TrySaveTaskMappingIfUnowned_SuccessPath_DetachesStaleTrackedEntity()
    {
        RacerWritesMapping("save-detach-ok", "goal-a");

        var db = CreateContext();
        var store = new PipelineStore(db, NullLogger<PipelineStore>.Instance);

        var tracked = db.TaskMappings.Find("save-detach-ok");
        Assert.Equal("goal-a", tracked!.GoalId);

        RacerWritesMapping("save-detach-ok", "goal-b");

        // Re-claiming goal-b's own row is idempotent — true, row unchanged.
        Assert.True(store.TrySaveTaskMappingIfUnowned("save-detach-ok", "goal-b"));

        var observed = db.TaskMappings.Find("save-detach-ok");
        Assert.Equal("goal-b", observed!.GoalId);
        Assert.Equal("goal-b", ReadPersistedGoalId("save-detach-ok"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) PipelineStore.DeleteTaskMappingIfForGoal
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void DeleteTaskMappingIfForGoal_OwnRow_DeletesAndReturnsTrue()
    {
        RacerWritesMapping("del-own", "goal-ours");
        var store = CreateStore();

        Assert.True(store.DeleteTaskMappingIfForGoal("del-own", "goal-ours"));
        Assert.Null(ReadPersistedGoalId("del-own"));
    }

    /// <summary>The goal-id predicate: removing it would delete another goal's row.</summary>
    [Fact]
    public void DeleteTaskMappingIfForGoal_OtherGoalsRow_ReturnsFalseAndLeavesRowIntact()
    {
        RacerWritesMapping("del-other", "goal-racer");
        var store = CreateStore();

        Assert.False(store.DeleteTaskMappingIfForGoal("del-other", "goal-ours"));
        Assert.Equal("goal-racer", ReadPersistedGoalId("del-other"));
    }

    [Fact]
    public void DeleteTaskMappingIfForGoal_AbsentRow_ReturnsFalse()
    {
        var store = CreateStore();

        Assert.False(store.DeleteTaskMappingIfForGoal("del-absent", "goal-ours"));
        Assert.Null(ReadPersistedGoalId("del-absent"));
    }

    [Fact]
    public void DeleteTaskMappingIfForGoal_SecondCall_ReturnsFalse()
    {
        RacerWritesMapping("del-twice", "goal-ours");
        var store = CreateStore();

        Assert.True(store.DeleteTaskMappingIfForGoal("del-twice", "goal-ours"));
        Assert.False(store.DeleteTaskMappingIfForGoal("del-twice", "goal-ours"));
    }

    [Fact]
    public void DeleteTaskMappingIfForGoal_StoreFailure_Propagates()
    {
        RacerWritesMapping("del-throw", "goal-ours");
        var sentinel = new InvalidOperationException("delete-sentinel");
        var store = CreateStore(new SentinelThrowingInterceptor(sentinel, "DELETE"));

        var thrown = Assert.Throws<InvalidOperationException>(
            () => store.DeleteTaskMappingIfForGoal("del-throw", "goal-ours"));

        Assert.Same(sentinel, thrown);
        Assert.Equal("goal-ours", ReadPersistedGoalId("del-throw"));
    }

    /// <summary>DETACH RULE (success path): the deleted row must not linger as a tracked entity.</summary>
    [Fact]
    public void DeleteTaskMappingIfForGoal_SuccessPath_DetachesTrackedEntity()
    {
        RacerWritesMapping("del-detach-ok", "goal-ours");

        var db = CreateContext();
        var store = new PipelineStore(db, NullLogger<PipelineStore>.Instance);

        var tracked = db.TaskMappings.Find("del-detach-ok");
        Assert.Equal("goal-ours", tracked!.GoalId);

        Assert.True(store.DeleteTaskMappingIfForGoal("del-detach-ok", "goal-ours"));

        // Without the detach this Find would return the stale tracked copy of a deleted row.
        Assert.Null(db.TaskMappings.Find("del-detach-ok"));
    }

    /// <summary>DETACH RULE (0-row path): a stale tracked entity is dropped even when nothing was deleted.</summary>
    [Fact]
    public void DeleteTaskMappingIfForGoal_ZeroRowPath_DetachesStaleTrackedEntity()
    {
        RacerWritesMapping("del-detach-zero", "goal-a");

        var db = CreateContext();
        var store = new PipelineStore(db, NullLogger<PipelineStore>.Instance);

        var tracked = db.TaskMappings.Find("del-detach-zero");
        Assert.Equal("goal-a", tracked!.GoalId);

        RacerWritesMapping("del-detach-zero", "goal-b");

        Assert.False(store.DeleteTaskMappingIfForGoal("del-detach-zero", "goal-a"));

        var observed = db.TaskMappings.Find("del-detach-zero");
        Assert.Equal("goal-b", observed!.GoalId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) GoalPipelineManager.TryRegisterTask
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void TryRegisterTask_FreshTask_SucceedsAndPersistsOwnership()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal("goal-ours"));

        var result = manager.TryRegisterTask("reg-fresh", "goal-ours");

        Assert.Equal(new TaskRegistrationResult(true, TaskRegistrationFailure.None, null), result);
        Assert.Same(pipeline, manager.GetByTaskId("reg-fresh"));
        Assert.Equal("goal-ours", ReadPersistedGoalId("reg-fresh"));
    }

    [Theory]
    [InlineData("", "goal-ours")]
    [InlineData("   ", "goal-ours")]
    [InlineData("reg-blank", "")]
    [InlineData("reg-blank", "  ")]
    public void TryRegisterTask_BlankArgument_IsNoOpRefusalWithoutSql(string taskId, string goalId)
    {
        var counter = new TaskMappingCommandCounter();
        var manager = new GoalPipelineManager(CreateStore(counter), new TestLogger<GoalPipelineManager>());
        counter.Start();

        var result = manager.TryRegisterTask(taskId, goalId);

        Assert.Equal(new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null), result);
        Assert.Empty(counter.Commands);
        Assert.Null(manager.GetByTaskId(taskId));
    }

    /// <summary>
    /// NULL vectors of the null/blank no-op contract — the genuine <c>null</c> path, not the
    /// empty-string path. The guard must refuse before <c>TryAdd</c> ever sees the argument:
    /// with the guard removed a null taskId reaches <c>ConcurrentDictionary.TryAdd</c> and throws,
    /// and a null goalId is claimed in memory and carried into the SQL — either way the exact
    /// result record, the zero command count and the untouched witness mapping all break.
    /// </summary>
    [Theory]
    [InlineData(null, "goal-ours")]
    [InlineData("reg-null", null)]
    [InlineData(null, null)]
    public void TryRegisterTask_NullArgument_IsNoOpRefusalWithoutSql(string? taskId, string? goalId)
    {
        var counter = new TaskMappingCommandCounter();
        var manager = new GoalPipelineManager(CreateStore(counter), new TestLogger<GoalPipelineManager>());
        var witnessPipeline = manager.CreatePipeline(CreateGoal("goal-ours"));

        // A pre-existing mapping that must survive the refusal untouched.
        manager.RegisterTask("reg-null-witness", "goal-ours");
        counter.Start();

        var result = manager.TryRegisterTask(taskId!, goalId!);

        Assert.Equal(new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null), result);
        Assert.Empty(counter.Commands);

        // Memory unchanged: the witness still resolves, and nothing was claimed for the argument.
        Assert.Same(witnessPipeline, manager.GetByTaskId("reg-null-witness"));
        Assert.Equal("goal-ours", ReadPersistedGoalId("reg-null-witness"));
        if (taskId is not null)
        {
            Assert.Null(manager.GetByTaskId(taskId));
            Assert.Null(ReadPersistedGoalId(taskId));
        }
    }

    /// <summary>
    /// In-memory duplicate: this manager already holds an entry for the task, so the refusal must
    /// happen BEFORE any statement reaches the database (proved by the zero command count).
    /// </summary>
    [Fact]
    public void TryRegisterTask_InMemoryDuplicate_RefusesWithoutExecutingSql()
    {
        var counter = new TaskMappingCommandCounter();
        var manager = new GoalPipelineManager(CreateStore(counter), new TestLogger<GoalPipelineManager>());
        var first = manager.CreatePipeline(CreateGoal("goal-first"));
        manager.CreatePipeline(CreateGoal("goal-second"));
        manager.RegisterTask("reg-dup", "goal-first");

        counter.Start();
        var result = manager.TryRegisterTask("reg-dup", "goal-second");

        Assert.Equal(new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null), result);
        Assert.Empty(counter.Commands);
        Assert.Same(first, manager.GetByTaskId("reg-dup"));
        Assert.Equal("goal-first", ReadPersistedGoalId("reg-dup"));
    }

    /// <summary>
    /// DATABASE-CONFLICT vector: the competing row is pre-arranged by the racer's separate context,
    /// so this manager's <c>TryAdd</c> succeeds and only the conditional statement can catch the
    /// conflict. Removing the pair-based rollback leaves this manager's memory claiming a mapping
    /// it does not own — the <c>GetByTaskId</c> assertion is what fails then.
    /// </summary>
    [Fact]
    public void TryRegisterTask_DatabaseConflict_RollsBackMemoryAndLeavesRowIntact()
    {
        RacerWritesMapping("reg-conflict", "goal-racer");

        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        manager.CreatePipeline(CreateGoal("goal-ours"));

        var result = manager.TryRegisterTask("reg-conflict", "goal-ours");

        Assert.Equal(new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null), result);
        Assert.Equal("goal-racer", ReadPersistedGoalId("reg-conflict"));
        Assert.Null(manager.GetByTaskId("reg-conflict"));
    }

    /// <summary>
    /// FAILURE vector: the store throws a DISTINCT sentinel before the statement runs. The result
    /// must CARRY that exact exception instance and the in-memory claim must be rolled back.
    /// </summary>
    [Fact]
    public void TryRegisterTask_PersistenceFailure_RollsBackAndCarriesException()
    {
        var sentinel = new InvalidOperationException("register-sentinel");
        var logger = new TestLogger<GoalPipelineManager>();
        var manager = new GoalPipelineManager(
            CreateStore(new SentinelThrowingInterceptor(sentinel, "INSERT")), logger);
        manager.CreatePipeline(CreateGoal("goal-ours"));

        var result = manager.TryRegisterTask("reg-fail", "goal-ours");

        Assert.False(result.Success);
        Assert.Equal(TaskRegistrationFailure.PersistenceFailed, result.Cause);
        Assert.Same(sentinel, result.PersistenceException);
        Assert.Null(manager.GetByTaskId("reg-fail"));
        Assert.Null(ReadPersistedGoalId("reg-fail"));
        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Error && ReferenceEquals(e.Exception, sentinel));
    }

    [Fact]
    public void TryRegisterTask_NullStore_SucceedsInMemoryOnly()
    {
        var manager = new GoalPipelineManager(store: null, new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal("goal-ours"));

        var result = manager.TryRegisterTask("reg-nostore", "goal-ours");

        Assert.Equal(new TaskRegistrationResult(true, TaskRegistrationFailure.None, null), result);
        Assert.Same(pipeline, manager.GetByTaskId("reg-nostore"));
        Assert.Null(ReadPersistedGoalId("reg-nostore"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) GoalPipelineManager.TryUnregisterTask
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("", "goal-ours")]
    [InlineData("   ", "goal-ours")]
    [InlineData("unreg-blank", "")]
    [InlineData("unreg-blank", "   ")]
    public void TryUnregisterTask_BlankArgument_IsNoOp(string taskId, string goalId)
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());

        var result = manager.TryUnregisterTask(taskId, goalId);

        Assert.Equal(new TaskUnregisterResult(false, false), result);
    }

    /// <summary>
    /// NULL vectors of the unregistration no-op contract — the genuine <c>null</c> path.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF: with the null/blank guard deleted, a null <c>taskId</c> reaches the pair-based
    /// <c>TryRemove</c> and throws, so both null-taskId cases fail. The null-<c>goalId</c>-only case
    /// is honestly NOT mutation-proof — a pair whose value is null can never match a live entry, so
    /// the guard and the pair remove agree on <c>(false, false)</c>. It is kept because the goal
    /// requires the vector, and its witness assertions still pin down that no mapping is disturbed.
    /// </remarks>
    [Theory]
    [InlineData(null, "goal-ours")]
    [InlineData("unreg-null", null)]
    [InlineData(null, null)]
    public void TryUnregisterTask_NullArgument_IsNoOp(string? taskId, string? goalId)
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var witnessPipeline = manager.CreatePipeline(CreateGoal("goal-ours"));
        manager.RegisterTask("unreg-null-witness", "goal-ours");

        var result = manager.TryUnregisterTask(taskId!, goalId!);

        Assert.Equal(new TaskUnregisterResult(false, false), result);

        // Memory unchanged: the pre-existing mapping and its persisted row both survive.
        Assert.Same(witnessPipeline, manager.GetByTaskId("unreg-null-witness"));
        Assert.Equal("goal-ours", ReadPersistedGoalId("unreg-null-witness"));
    }

    /// <summary>
    /// PRE-REPLACED vector: the in-memory entry was already re-pointed at another goal (arranged
    /// deterministically, no mid-operation interleaving). A key-only remove would evict the other
    /// goal's live mapping; the pair-based remove refuses and reports <c>(false, false)</c>.
    /// </summary>
    [Fact]
    public void TryUnregisterTask_PreReplacedMapping_RemovesNothing()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        manager.CreatePipeline(CreateGoal("goal-ours"));
        var other = manager.CreatePipeline(CreateGoal("goal-other"));
        manager.RegisterTask("unreg-replaced", "goal-other");

        var result = manager.TryUnregisterTask("unreg-replaced", "goal-ours");

        Assert.Equal(new TaskUnregisterResult(false, false), result);
        Assert.Same(other, manager.GetByTaskId("unreg-replaced"));
        Assert.Equal("goal-other", ReadPersistedGoalId("unreg-replaced"));
    }

    [Fact]
    public void TryUnregisterTask_OwnMapping_RemovesMemoryAndRow()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        manager.CreatePipeline(CreateGoal("goal-ours"));
        Assert.True(manager.TryRegisterTask("unreg-own", "goal-ours").Success);

        var result = manager.TryUnregisterTask("unreg-own", "goal-ours");

        Assert.Equal(new TaskUnregisterResult(true, true), result);
        Assert.Null(manager.GetByTaskId("unreg-own"));
        Assert.Null(ReadPersistedGoalId("unreg-own"));
    }

    /// <summary>
    /// The honest partial signal: memory was ours and is gone, but the persisted row had been
    /// stolen by another goal, so the conditional delete matched 0 rows and left it INTACT.
    /// </summary>
    [Fact]
    public void TryUnregisterTask_RowOwnedByAnotherGoal_ReportsPersistedResidue()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        manager.CreatePipeline(CreateGoal("goal-ours"));
        manager.RegisterTask("unreg-residue", "goal-ours");

        // The racer takes over the persisted row.
        RacerWritesMapping("unreg-residue", "goal-racer");

        var result = manager.TryUnregisterTask("unreg-residue", "goal-ours");

        Assert.Equal(new TaskUnregisterResult(true, false), result);
        Assert.Null(manager.GetByTaskId("unreg-residue"));
        Assert.Equal("goal-racer", ReadPersistedGoalId("unreg-residue"));
    }

    [Fact]
    public void TryUnregisterTask_DeleteFailure_ReportsResidueAndLogsWithoutThrowing()
    {
        var sentinel = new InvalidOperationException("unregister-sentinel");
        var logger = new TestLogger<GoalPipelineManager>();
        var interceptor = new SentinelThrowingInterceptor(sentinel, "DELETE");
        var manager = new GoalPipelineManager(CreateStore(interceptor), logger);
        manager.CreatePipeline(CreateGoal("goal-ours"));
        manager.RegisterTask("unreg-throw", "goal-ours");

        var result = manager.TryUnregisterTask("unreg-throw", "goal-ours");

        Assert.Equal(new TaskUnregisterResult(true, false), result);
        Assert.Null(manager.GetByTaskId("unreg-throw"));
        Assert.Equal("goal-ours", ReadPersistedGoalId("unreg-throw"));
        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Error && ReferenceEquals(e.Exception, sentinel));
    }

    [Fact]
    public void TryUnregisterTask_NullStore_ReportsVacuousPersistenceSuccess()
    {
        var manager = new GoalPipelineManager(store: null, new TestLogger<GoalPipelineManager>());
        manager.CreatePipeline(CreateGoal("goal-ours"));
        manager.RegisterTask("unreg-nostore", "goal-ours");

        var result = manager.TryUnregisterTask("unreg-nostore", "goal-ours");

        Assert.Equal(new TaskUnregisterResult(true, true), result);
        Assert.Null(manager.GetByTaskId("unreg-nostore"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (6) The honest remnant + existing-API preservation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// LEGACY-STEAL vector — the declared remnant of this slice: the conditional write protects a
    /// row only against OTHER conditional writers. The legacy unconditional
    /// <see cref="PipelineStore.SaveTaskMapping"/> (still the active dispatch writer until the
    /// follow-up slice migrates it) overwrites the row regardless of ownership, and this test
    /// records that fact rather than pretending otherwise.
    /// </summary>
    /// <remarks>
    /// CAPTURE-LEVEL PROTECTION: in the real flow this race cannot arise, because a dispatch must
    /// first capture its work-slot POSITION (A1b's <c>CaptureDispatchPosition</c>), and a position
    /// that is already live refuses a second capture — two dispatches for one position can never
    /// both reach the mapping write. The remnant here is therefore a property of the store API in
    /// isolation, not of the orchestrator's dispatch path.
    /// </remarks>
    [Fact]
    public void LegacyUnconditionalWrite_StealsRowOwnedByConditionalWriter()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        manager.CreatePipeline(CreateGoal("goal-ours"));

        Assert.Equal(
            new TaskRegistrationResult(true, TaskRegistrationFailure.None, null),
            manager.TryRegisterTask("steal-me", "goal-ours"));
        Assert.Equal("goal-ours", ReadPersistedGoalId("steal-me"));

        // The racer's separate context performs the LEGACY unconditional write.
        RacerWritesMapping("steal-me", "goal-racer");

        Assert.Equal("goal-racer", ReadPersistedGoalId("steal-me"));
    }

    /// <summary>PRESERVATION: the existing unconditional writers behave exactly as before.</summary>
    [Fact]
    public void RegisterTask_AndUnregisterTask_KeepUnconditionalBehavior()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var first = manager.CreatePipeline(CreateGoal("goal-first"));
        var second = manager.CreatePipeline(CreateGoal("goal-second"));

        manager.RegisterTask("legacy-task", "goal-first");
        Assert.Same(first, manager.GetByTaskId("legacy-task"));
        Assert.Equal("goal-first", ReadPersistedGoalId("legacy-task"));

        // Unconditional overwrite — no ownership check on the legacy path.
        manager.RegisterTask("legacy-task", "goal-second");
        Assert.Same(second, manager.GetByTaskId("legacy-task"));
        Assert.Equal("goal-second", ReadPersistedGoalId("legacy-task"));

        manager.UnregisterTask("legacy-task");
        Assert.Null(manager.GetByTaskId("legacy-task"));
        Assert.Null(ReadPersistedGoalId("legacy-task"));
    }
}

/// <summary>
/// Throws a caller-supplied sentinel exception BEFORE a <c>task_mappings</c> statement of the
/// configured verb executes. The callback performs NO re-entrant work — it only throws.
/// </summary>
internal sealed class SentinelThrowingInterceptor : DbCommandInterceptor
{
    private readonly Exception _sentinel;
    private readonly string _verb;

    public SentinelThrowingInterceptor(Exception sentinel, string verb)
    {
        _sentinel = sentinel;
        _verb = verb;
    }

    private void ThrowIfTargeted(DbCommand command)
    {
        var text = command.CommandText;
        if (text.Contains("task_mappings", StringComparison.OrdinalIgnoreCase)
            && text.TrimStart().StartsWith(_verb, StringComparison.OrdinalIgnoreCase))
        {
            throw _sentinel;
        }
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
/// Records every <c>task_mappings</c> statement issued while recording is enabled, so a test can
/// prove that a refusal path never reached the database. The callback performs NO re-entrant work.
/// </summary>
internal sealed class TaskMappingCommandCounter : DbCommandInterceptor
{
    private bool _recording;

    /// <summary>The captured statements.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Begins capturing.</summary>
    public void Start()
    {
        Commands.Clear();
        _recording = true;
    }

    private void Record(DbCommand command)
    {
        if (_recording && command.CommandText.Contains("task_mappings", StringComparison.OrdinalIgnoreCase))
            Commands.Add(command.CommandText);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }
}
