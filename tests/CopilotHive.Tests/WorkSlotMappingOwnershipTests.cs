using System.Data.Common;
using System.Reflection;

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

/// <summary>
/// Slice E2a-ii-α — <see cref="GoalPipelineManager.PersistAdmission"/> (the complete API, UNUSED
/// in production this slice) and the SINGLE-LOCK POLICY over the manager's mapping surface.
/// </summary>
/// <remarks>
/// The API vectors cover every branch of the admission algorithm — the committed transaction, the
/// two memory-conflict shapes (refused BEFORE any store call), the persisted conflict's pair-based
/// rollback, the store failure's carried exception + rollback, the no-store claim, and the three
/// pre-lock validations — each pinned by the EXACT log template a capturing logger observed.
/// The two POLICY vectors prove mutual exclusion honestly: one blocks INSIDE the store call while
/// <c>_mappingLock</c> is held (an external gate in an EF interceptor), the other blocks the
/// private monitor directly via reflection. Neither asserts an unguaranteeable ordering between a
/// return value's observation and another thread's mutation — only mutual exclusion (the
/// bounded-window NON-completion) plus the final consistent state.
/// </remarks>
public sealed class WorkSlotAdmissionCommitTests : IDisposable
{
    private readonly string _connectionString =
        $"Data Source=file:memdb-workslotadmission-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<SqliteConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];

    public WorkSlotAdmissionCommitTests()
    {
        // The KEEPER anchors the shared-cache in-memory database's lifetime for the whole test.
        // The database name is per-instance unique, so no rows can leak across xUnit fixture
        // instances (each instance gets its own unshared database).
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();

        CreateContext().Database.EnsureCreated();
        ExecuteOnKeeper("DELETE FROM task_mappings");
        ExecuteOnKeeper("DELETE FROM pipelines");
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

    /// <summary>Creates a manager-owned pipeline whose active task pointer is already set.</summary>
    private static GoalPipeline CreateActivePipeline(GoalPipelineManager manager, string goalId, string taskId)
    {
        var pipeline = manager.CreatePipeline(CreateGoal(goalId));
        pipeline.SetActiveTask(taskId);
        return pipeline;
    }

    private static void AssertSingleLog(
        TestLogger<GoalPipelineManager> logger, LogLevel level, string message) =>
        Assert.Single(
            logger.LogEntries,
            e => e.LogLevel == level && string.Equals(e.Message, message, StringComparison.Ordinal));

    // ═══════════════════════════════════════════════════════════════════════
    // (A) PersistAdmission — the outcome matrix
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// COMMITTED: the in-memory claim is held AND the E2a-i transaction landed BOTH rows (the
    /// mapping row and the pipeline row carrying the pointer), with the exact DEBUG template.
    /// </summary>
    [Fact]
    public void PersistAdmission_Fresh_CommitsClaimAndBothPersistedRows()
    {
        var logger = new TestLogger<GoalPipelineManager>();
        var manager = new GoalPipelineManager(CreateStore(), logger);
        var pipeline = CreateActivePipeline(manager, "goal-commit", "task-commit");

        var result = manager.PersistAdmission(pipeline, "task-commit");

        Assert.Equal(new AdmissionCommitResult(AdmissionCommitStatus.Committed), result);
        Assert.Null(result.PersistenceException);

        // THE MEMORY CLAIM.
        Assert.Same(pipeline, manager.GetByTaskId("task-commit"));
        // THE PERSISTED TRANSACTION: the mapping row AND the pipeline row's pointer.
        Assert.Equal("goal-commit", ReadPersistedGoalId("task-commit"));
        Assert.Equal(1L, ExecuteScalarOnKeeper(
            "SELECT COUNT(*) FROM pipelines WHERE goal_id = 'goal-commit' AND active_task_id = 'task-commit'"));

        AssertSingleLog(logger, LogLevel.Debug, "Admission committed goal=goal-commit task=task-commit");
    }

    /// <summary>
    /// MEMORY CONFLICT (IDENTICAL PAIR): the very same (taskId, goalId) pair is already mapped, so
    /// the admission is refused BEFORE any store call — proved by the invocation-counting seam's
    /// ZERO recorded statements — and the pre-existing claim is untouched.
    /// </summary>
    [Fact]
    public void PersistAdmission_IdenticalPairAlreadyMapped_MemoryConflictWithoutStoreCall()
    {
        var counter = new AdmissionCommandCounter();
        var logger = new TestLogger<GoalPipelineManager>();
        var manager = new GoalPipelineManager(CreateStore(counter), logger);
        var pipeline = CreateActivePipeline(manager, "goal-same", "task-same");
        manager.RegisterTask("task-same", "goal-same");

        counter.Start();
        var result = manager.PersistAdmission(pipeline, "task-same");

        Assert.Equal(new AdmissionCommitResult(AdmissionCommitStatus.MemoryConflict), result);
        // NO STORE CALL: not a single statement reached the database.
        Assert.Empty(counter.Commands);
        // THE CLAIM IS UNTOUCHED.
        Assert.Same(pipeline, manager.GetByTaskId("task-same"));
        Assert.Equal("goal-same", ReadPersistedGoalId("task-same"));

        AssertSingleLog(
            logger, LogLevel.Debug,
            "Admission refused — task task-same is already mapped (goal=goal-same); no store call made");
    }

    /// <summary>
    /// MEMORY CONFLICT (FOREIGN GOAL): the task is already mapped to ANOTHER goal → the same
    /// refusal shape, no store call, the FOREIGN goal named in the log line, and the foreign
    /// mapping left intact (a claim would have stolen it).
    /// </summary>
    [Fact]
    public void PersistAdmission_ForeignPairAlreadyMapped_MemoryConflictWithoutStoreCall()
    {
        var counter = new AdmissionCommandCounter();
        var logger = new TestLogger<GoalPipelineManager>();
        var manager = new GoalPipelineManager(CreateStore(counter), logger);
        var foreign = manager.CreatePipeline(CreateGoal("goal-foreign"));
        var pipeline = CreateActivePipeline(manager, "goal-ours", "task-foreign");
        manager.RegisterTask("task-foreign", "goal-foreign");

        counter.Start();
        var result = manager.PersistAdmission(pipeline, "task-foreign");

        Assert.Equal(new AdmissionCommitResult(AdmissionCommitStatus.MemoryConflict), result);
        Assert.Empty(counter.Commands);
        // THE FOREIGN CLAIM IS UNTOUCHED — memory and row both still the foreign goal's.
        Assert.Same(foreign, manager.GetByTaskId("task-foreign"));
        Assert.Equal("goal-foreign", ReadPersistedGoalId("task-foreign"));

        AssertSingleLog(
            logger, LogLevel.Debug,
            "Admission refused — task task-foreign is already mapped (goal=goal-foreign); no store call made");
    }

    /// <summary>
    /// PERSIST CONFLICT: a competing persisted row (written by another attempt) makes the E2a-i
    /// transaction refuse; the PAIR-BASED rollback leaves NO in-memory claim behind and the
    /// competing row INTACT, with the exact DEBUG template.
    /// </summary>
    [Fact]
    public void PersistAdmission_CompetingPersistedRow_RollsBackClaimAndLeavesRowIntact()
    {
        ExecuteOnKeeper(
            "INSERT INTO task_mappings (task_id, goal_id) VALUES ('task-conflict', 'goal-competitor')");
        var logger = new TestLogger<GoalPipelineManager>();
        var manager = new GoalPipelineManager(CreateStore(), logger);
        var pipeline = CreateActivePipeline(manager, "goal-ours", "task-conflict");

        var result = manager.PersistAdmission(pipeline, "task-conflict");

        Assert.Equal(new AdmissionCommitResult(AdmissionCommitStatus.PersistConflict), result);
        Assert.Null(result.PersistenceException);
        // THE PAIR-BASED ROLLBACK: no claim survives…
        Assert.Null(manager.GetByTaskId("task-conflict"));
        // …and the competing attempt's row was never disturbed.
        Assert.Equal("goal-competitor", ReadPersistedGoalId("task-conflict"));

        AssertSingleLog(
            logger, LogLevel.Debug,
            "Admission rolled back — task task-conflict's persisted row is owned by another attempt; the memory claim removed");
    }

    /// <summary>
    /// PERSISTENCE FAILED: a forced store failure (the EF interceptor's sentinel at the mapping
    /// INSERT) → the exception is CARRIED on the result (exact instance identity, and the same
    /// instance is the logged exception argument), the claim is rolled back, and the exact
    /// WARNING template is emitted.
    /// </summary>
    [Fact]
    public void PersistAdmission_StoreThrows_RollsBackClaimAndCarriesException()
    {
        var sentinel = new InvalidOperationException("admission-sentinel");
        var logger = new TestLogger<GoalPipelineManager>();
        var manager = new GoalPipelineManager(
            CreateStore(new SentinelThrowingInterceptor(sentinel, "INSERT")), logger);
        var pipeline = CreateActivePipeline(manager, "goal-fail", "task-fail");

        var result = manager.PersistAdmission(pipeline, "task-fail");

        Assert.Equal(AdmissionCommitStatus.PersistenceFailed, result.Status);
        Assert.NotNull(result.PersistenceException);
        // THE EXACT SHAPE: EF wraps the interceptor's throw; the SENTINEL is the direct inner.
        var update = Assert.IsType<DbUpdateException>(result.PersistenceException);
        Assert.Same(sentinel, update.InnerException);

        // THE ROLLBACK: neither the claim nor a row survives.
        Assert.Null(manager.GetByTaskId("task-fail"));
        Assert.Null(ReadPersistedGoalId("task-fail"));

        // THE WARNING carries the EXACT carried instance as the exception argument.
        var warning = Assert.Single(
            logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning
                && string.Equals(
                    e.Message,
                    "Admission failed — the store threw for task task-fail (goal=goal-fail); the memory claim removed",
                    StringComparison.Ordinal));
        Assert.Same(result.PersistenceException, warning.Exception);
    }

    /// <summary>
    /// NO STORE: the in-memory claim alone IS the admission, retained, with the exact DEBUG
    /// template and no persisted row anywhere.
    /// </summary>
    [Fact]
    public void PersistAdmission_NullStore_KeepsClaimAndReportsNoStore()
    {
        var logger = new TestLogger<GoalPipelineManager>();
        var manager = new GoalPipelineManager(store: null, logger);
        var pipeline = CreateActivePipeline(manager, "goal-nostore", "task-nostore");

        var result = manager.PersistAdmission(pipeline, "task-nostore");

        Assert.Equal(new AdmissionCommitResult(AdmissionCommitStatus.NoStore), result);
        Assert.Same(pipeline, manager.GetByTaskId("task-nostore"));
        Assert.Null(ReadPersistedGoalId("task-nostore"));

        AssertSingleLog(
            logger, LogLevel.Debug,
            "Admission committed in memory only — no store configured (goal=goal-nostore task=task-nostore)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (B) PersistAdmission — the three PRE-LOCK validations
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>VALIDATION 1: a null pipeline → <c>ArgumentNullException</c>, dictionary untouched.</summary>
    [Fact]
    public void PersistAdmission_NullPipeline_ThrowsArgumentNullException_DictionaryUntouched()
    {
        var counter = new AdmissionCommandCounter();
        var manager = new GoalPipelineManager(CreateStore(counter), new TestLogger<GoalPipelineManager>());
        var witness = manager.CreatePipeline(CreateGoal("goal-witness"));
        manager.RegisterTask("task-witness", "goal-witness");
        counter.Start();

        var ex = Assert.Throws<ArgumentNullException>(() => manager.PersistAdmission(null!, "task-any"));

        Assert.Equal("pipeline", ex.ParamName);
        // THE DICTIONARY IS UNTOUCHED: nothing claimed, the witness intact, no statement issued.
        Assert.Null(manager.GetByTaskId("task-any"));
        Assert.Same(witness, manager.GetByTaskId("task-witness"));
        Assert.Empty(counter.Commands);
    }

    /// <summary>VALIDATION 2: a null/blank task id → <c>ArgumentException</c>, dictionary untouched.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PersistAdmission_NullOrBlankTaskId_ThrowsArgumentException_DictionaryUntouched(string? blank)
    {
        var counter = new AdmissionCommandCounter();
        var manager = new GoalPipelineManager(CreateStore(counter), new TestLogger<GoalPipelineManager>());
        var witness = manager.CreatePipeline(CreateGoal("goal-witness"));
        manager.RegisterTask("task-witness", "goal-witness");
        var pipeline = CreateActivePipeline(manager, "goal-blank", "task-blank");
        counter.Start();

        var ex = Assert.Throws<ArgumentException>(() => manager.PersistAdmission(pipeline, blank!));

        Assert.Equal("taskId", ex.ParamName);
        // THE DICTIONARY IS UNTOUCHED.
        Assert.Null(manager.GetByTaskId("task-blank"));
        Assert.Same(witness, manager.GetByTaskId("task-witness"));
        Assert.Empty(counter.Commands);
    }

    /// <summary>
    /// VALIDATION 3: the pipeline's active pointer does not name the admitted task →
    /// <c>ArgumentException</c> on <c>taskId</c> carrying BOTH values, dictionary untouched.
    /// </summary>
    [Fact]
    public void PersistAdmission_ActiveTaskIdMismatch_ThrowsArgumentException_DictionaryUntouched()
    {
        var counter = new AdmissionCommandCounter();
        var manager = new GoalPipelineManager(CreateStore(counter), new TestLogger<GoalPipelineManager>());
        var witness = manager.CreatePipeline(CreateGoal("goal-witness"));
        manager.RegisterTask("task-witness", "goal-witness");
        var pipeline = CreateActivePipeline(manager, "goal-mismatch", "task-active");
        counter.Start();

        var ex = Assert.Throws<ArgumentException>(() => manager.PersistAdmission(pipeline, "task-requested"));

        Assert.Equal("taskId", ex.ParamName);
        // BOTH VALUES are named in the message.
        Assert.Contains("task-requested", ex.Message, StringComparison.Ordinal);
        Assert.Contains("task-active", ex.Message, StringComparison.Ordinal);
        // THE DICTIONARY IS UNTOUCHED.
        Assert.Null(manager.GetByTaskId("task-requested"));
        Assert.Null(manager.GetByTaskId("task-active"));
        Assert.Same(witness, manager.GetByTaskId("task-witness"));
        Assert.Empty(counter.Commands);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (C) The lock policy — mutual exclusion, proved honestly
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SERIALIZATION VECTOR: an external gate inside the EF interceptor holds
    /// <see cref="GoalPipelineManager.PersistAdmission"/> INSIDE the store call — hence INSIDE
    /// <c>_mappingLock</c>. A concurrent <see cref="GoalPipelineManager.TryUnregisterTask"/> for a
    /// DISJOINT task MUST NOT complete while the gate blocks: that bounded-window NON-completion is
    /// the mutual-exclusion proof (without the lock the disjoint unregister would finish
    /// immediately). After the release both complete within bounded waits and the final state is
    /// consistent.
    /// </summary>
    /// <remarks>
    /// THE HONEST RULE: this vector deliberately does NOT assert that <c>Committed</c> was OBSERVED
    /// before the unregister's mutation landed. The Monitor is released before the caller's thread
    /// observes the returned value, so that ordering is unguaranteeable. The claim proved here is
    /// exactly: mutual exclusion while the lock is held, plus final consistency.
    /// </remarks>
    [Fact]
    public async Task PersistAdmission_HoldsMappingLock_SerializingDisjointUnregister()
    {
        var gate = new GatedAdmissionInterceptor();
        var manager = new GoalPipelineManager(CreateStore(gate), new TestLogger<GoalPipelineManager>());
        var disjoint = manager.CreatePipeline(CreateGoal("goal-disjoint"));
        manager.RegisterTask("task-disjoint", "goal-disjoint");
        var pipeline = CreateActivePipeline(manager, "goal-serial", "task-serial");

        // The gate is inert during setup and armed only now: the vector's own admission is the
        // FIRST statement it can block.
        gate.Arm();
        var admissionTask = Task.Factory.StartNew(
            () => manager.PersistAdmission(pipeline, "task-serial"),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<TaskUnregisterResult>? unregisterTask = null;
        try
        {
            // The admission is now INSIDE the store call, holding _mappingLock.
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var unregisterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            unregisterTask = Task.Factory.StartNew(
                () =>
                {
                    unregisterStarted.SetResult();
                    return manager.TryUnregisterTask("task-disjoint", "goal-disjoint");
                },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await unregisterStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // THE MUTUAL-EXCLUSION PROOF: the DISJOINT unregister cannot proceed while the
            // admission holds the lock — it must NOT complete within the bounded window.
            await Assert.ThrowsAsync<TimeoutException>(
                () => unregisterTask.WaitAsync(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken));
            Assert.False(unregisterTask.IsCompleted);
            // …and its mutation has NOT landed while it is blocked.
            Assert.Same(disjoint, manager.GetByTaskId("task-disjoint"));
        }
        finally
        {
            gate.Release();
        }

        // BOUNDED waits — a timeout IS a failure.
        var admissionResult = await admissionTask.WaitAsync(
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var unregisterResult = await unregisterTask!.WaitAsync(
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // THE CONSISTENCY PROOF (observed AFTER both): the admission committed…
        Assert.Equal(new AdmissionCommitResult(AdmissionCommitStatus.Committed), admissionResult);
        Assert.Same(pipeline, manager.GetByTaskId("task-serial"));
        Assert.Equal("goal-serial", ReadPersistedGoalId("task-serial"));
        // …AND the disjoint unregister's mutation landed.
        Assert.Equal(new TaskUnregisterResult(true, true), unregisterResult);
        Assert.Null(manager.GetByTaskId("task-disjoint"));
        Assert.Null(ReadPersistedGoalId("task-disjoint"));
        Assert.Equal(1, gate.BlockCount);
    }

    /// <summary>
    /// THE DIRECT-MONITOR CONTENTION VECTOR: the private <c>_mappingLock</c> is obtained by
    /// reflection and held by a dedicated thread. A mapping-surface call for a DISJOINT task
    /// cannot complete while the monitor is held; after the release it completes within a bounded
    /// wait (the timeout IS the failure) with the correct final outcome.
    /// </summary>
    [Fact]
    public async Task MappingSurface_BlocksWhileTheMonitorIsHeldDirectly()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal("goal-mon"));
        manager.RegisterTask("task-mon", "goal-mon");

        var field = typeof(GoalPipelineManager).GetField(
            "_mappingLock", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var mappingLock = field!.GetValue(manager);
        Assert.NotNull(mappingLock);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new Thread(() =>
        {
            lock (mappingLock!)
            {
                held.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        })
        { IsBackground = true, Name = "mapping-lock-holder" };

        Task<TaskUnregisterResult>? unregisterTask = null;
        try
        {
            holder.Start();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            unregisterTask = Task.Factory.StartNew(
                () =>
                {
                    started.SetResult();
                    return manager.TryUnregisterTask("task-mon", "goal-mon");
                },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // BLOCKED on the monitor: neither completion nor mutation while it is held.
            await Assert.ThrowsAsync<TimeoutException>(
                () => unregisterTask.WaitAsync(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken));
            Assert.False(unregisterTask.IsCompleted);
            Assert.Same(pipeline, manager.GetByTaskId("task-mon"));
        }
        finally
        {
            release.TrySetResult();
        }

        // THE BOUNDED COMPLETION — the timeout IS the failure.
        var result = await unregisterTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(new TaskUnregisterResult(true, true), result);
        Assert.Null(manager.GetByTaskId("task-mon"));
        Assert.Null(ReadPersistedGoalId("task-mon"));
        Assert.True(holder.Join(TimeSpan.FromSeconds(5)));
    }
}

/// <summary>
/// Records EVERY statement issued while recording is enabled, so a test can prove that a refusal
/// path made no store call at all. The callback performs NO re-entrant work.
/// </summary>
internal sealed class AdmissionCommandCounter : DbCommandInterceptor
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
        if (_recording)
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

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// Blocks the FIRST <c>task_mappings</c> INSERT on an EXTERNAL gate, holding the calling thread
/// INSIDE the store's admission transaction until <see cref="Release"/> is called. The callback
/// performs NO re-entrant work into the manager — it only signals and waits on its own gate.
/// </summary>
internal sealed class GatedAdmissionInterceptor : DbCommandInterceptor
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _blockCount;
    private volatile bool _armed;

    /// <summary>Completes once the store call is blocked inside the transaction.</summary>
    public Task Entered => _entered.Task;

    /// <summary>How many times the gate actually blocked (the injection really fired).</summary>
    public int BlockCount => Volatile.Read(ref _blockCount);

    /// <summary>
    /// Arms the gate. Until this is called the interceptor is inert, so fixture SETUP writes
    /// (the seed mappings, the pipeline rows) run through untouched and only the vector's own
    /// admission is blocked.
    /// </summary>
    public void Arm() => _armed = true;

    /// <summary>Releases the blocked statement. Idempotent.</summary>
    public void Release() => _release.TrySetResult();

    private void BlockIfTargeted(DbCommand command)
    {
        if (!_armed)
            return;

        var text = command.CommandText;
        if (!text.Contains("task_mappings", StringComparison.OrdinalIgnoreCase)
            || !text.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Only the FIRST targeted statement blocks; TrySetResult is the one-shot latch.
        if (!_entered.TrySetResult())
            return;

        Interlocked.Increment(ref _blockCount);
        _release.Task.GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        BlockIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        BlockIfTargeted(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        BlockIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        BlockIfTargeted(command);
        return ValueTask.FromResult(result);
    }
}
