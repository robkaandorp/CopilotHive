using System.Data.Common;
using System.Reflection;
using System.Threading.Channels;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests.Services;

/// <summary>
/// The READY-DRIVEN ASSIGNMENT PUBLISHER's OWN contract, focused: the route/pointer/slot/role/pinned
/// refusals, the insert-once outcome decision (<c>Recorded</c>/<c>AlreadyRecorded</c> allow THIS
/// invocation's publication; <c>Conflict</c>/<c>Indeterminate</c> prohibit it), the
/// failure-reason/<c>StoreStatus</c>/<c>InnerException</c> pairing, the two caller-cancellation
/// observations, and the post-record failure propagation.
/// </summary>
/// <remarks>
/// <para>
/// THE READY/WORKSTREAM END-TO-END PATH IS DELIBERATELY OUT OF SCOPE HERE — it belongs to the next
/// coding round per the goal's file plan. Every fixture below invokes
/// <see cref="WorkerAssignmentPublisher.PublishAsync"/> DIRECTLY over a REAL file-backed SQLite
/// database and the REAL pipeline registry, worker pool and store; nothing the publisher itself
/// consults is stubbed. The store's own <c>Load</c> is used as TEST OBSERVATION ONLY (the production
/// path performs no readback).
/// </para>
/// <para>
/// EVERY FIXTURE IS ISOLATED: the database is a per-instance temporary FILE, contexts are created
/// from a connection string with <c>Pooling=False</c> (so disposing the factory really releases the
/// file handles), and cleanup runs in <c>Dispose</c> with best-effort deletion of the database file
/// and its WAL/SHM sidecars.
/// </para>
/// </remarks>
public sealed class WorkerAssignmentPublisherTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"copilothive-wap-pub-{Guid.NewGuid():N}.db");

    private readonly List<IDisposable> _fixtures = [];

    public WorkerAssignmentPublisherTests()
    {
        using var connection = OpenConnection();
        using var context = ContextOn(connection);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
        {
            try
            {
                fixture.Dispose();
            }
            catch
            {
                // Best-effort — a leftover fixture must never fail a test.
            }
        }

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

    private string ConnectionString => $"Data Source={_dbPath};Pooling=False;Default Timeout=15";

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static CopilotHiveDbContext ContextOn(SqliteConnection connection, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new CopilotHiveDbContext(builder.Options);
    }

    private PublisherFactory NewFactory(params IInterceptor[] interceptors)
    {
        var factory = new PublisherFactory(ConnectionString, interceptors);
        _fixtures.Add(factory);
        return factory;
    }

    private static WorkerAssignmentContextStore NewStore(IDbContextFactory<CopilotHiveDbContext> factory) =>
        new(factory, NullLogger<WorkerAssignmentContextStore>.Instance);

    private long AssignmentRowCount(string taskId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM worker_assignment_contexts WHERE task_id = '{Escape(taskId)}'";
        return (long)command.ExecuteScalar()!;
    }

    private string? RawColumn(string taskId, string column)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {column} FROM worker_assignment_contexts WHERE task_id = '{Escape(taskId)}'";
        var value = command.ExecuteScalar();
        return value is DBNull ? null : (string?)value;
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// A REAL publisher over a REAL file-backed SQLite store, the manager, the pool and ONE pipeline
    /// with a Pending slot at the active-task pointer. Returns everything a fixture needs.
    /// </summary>
    private PublisherHarness NewHarness(params IInterceptor[] interceptors)
    {
        var factory = NewFactory(interceptors);
        var store = NewStore(factory);
        var manager = new GoalPipelineManager();
        var pool = new WorkerPool();
        var worker = pool.RegisterWorker("worker-1", []);

        const string goalId = "goal-pub";
        var pipeline = manager.CreatePipeline(
            new Goal { Id = goalId, Description = "publish a recorded assignment" });

        const string taskId = "task-pub-coder-001-01";
        var position = new WorkSlotPosition(1, GoalPhase.Coding, 1);
        var slotBuild = pipeline.AllocateAttemptAndRegisterSlot(taskId, position);
        pipeline.SetActiveTask(taskId);
        manager.RegisterTask(taskId, goalId);

        return new PublisherHarness(this, store, manager, pool, worker, pipeline, goalId, taskId, slotBuild);
    }

    private sealed class PublisherHarness
    {
        public PublisherHarness(
            WorkerAssignmentPublisherTests owner,
            WorkerAssignmentContextStore store,
            GoalPipelineManager manager,
            WorkerPool pool,
            ConnectedWorker worker,
            GoalPipeline pipeline,
            string goalId,
            string taskId,
            SlotBuildResult slotBuild)
        {
            Owner = owner;
            Store = store;
            Manager = manager;
            Pool = pool;
            Worker = worker;
            Pipeline = pipeline;
            GoalId = goalId;
            TaskId = taskId;
            SlotBuild = slotBuild;
        }

        public WorkerAssignmentPublisherTests Owner { get; }
        public WorkerAssignmentContextStore Store { get; }
        public GoalPipelineManager Manager { get; }
        public WorkerPool Pool { get; }
        public ConnectedWorker Worker { get; }
        public GoalPipeline Pipeline { get; }
        public string GoalId { get; }
        public string TaskId { get; }
        public SlotBuildResult SlotBuild { get; }

        /// <summary>The delivered task the publisher is invoked with.</summary>
        public WorkTask NewTask(WorkerRole? role = WorkerRole.Coder, string model = "model-pub") => new()
        {
            TaskId = TaskId,
            GoalId = GoalId,
            GoalDescription = "publish a recorded assignment",
            Prompt = "do the work",
            Role = role ?? WorkerRole.Coder,
            Model = model,
            Repositories = [new TargetRepository { Name = "repo", Url = "https://example.invalid/repo" }],
        };

        /// <summary>A delivered task whose id has NO registered pipeline.</summary>
        public WorkTask UnregisteredTask() => new()
        {
            TaskId = "task-unregistered",
            GoalId = GoalId,
            GoalDescription = "publish a recorded assignment",
            Prompt = "do the work",
            Role = WorkerRole.Coder,
            Model = "model-pub",
            Repositories = [new TargetRepository { Name = "repo", Url = "https://example.invalid/repo" }],
        };

        /// <summary>
        /// The context the publisher is EXPECTED to construct from the harness state, for seeding a
        /// row ahead of the invocation.
        /// </summary>
        public WorkerAssignmentContext ExpectedContext(
            string? workerId = null, WorkerRole? role = null, string? model = null) =>
            new(GoalId, workerId ?? Worker.Id, role ?? WorkerRole.Coder, Slot, model ?? "model-pub");

        private WorkSlot Slot => new(SlotBuild.TaskId, SlotBuild.Position, SlotBuild.Attempt);

        /// <summary>Drains the pinned worker's channel — the publication observation.</summary>
        public List<OrchestratorMessage> DrainChannel()
        {
            var messages = new List<OrchestratorMessage>();
            while (Worker.MessageChannel.Reader.TryRead(out var message))
                messages.Add(message);
            return messages;
        }

        public string? RawFirstAssigned(string raw) => Owner.RawColumn(raw, "first_assigned_at_utc");
        public string? RawWorkerId(string raw) => Owner.RawColumn(raw, "worker_id");
    }

    /// <summary>
    /// A factory handing out store-OWNED contexts, each on its own connection to the file-backed
    /// database, so disposing the factory really releases the file handles.
    /// </summary>
    private sealed class PublisherFactory(string connectionString, IInterceptor[] interceptors)
        : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly List<CopilotHiveDbContext> _contexts = [];

        public CopilotHiveDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connectionString);
            if (interceptors.Length > 0)
                builder.AddInterceptors(interceptors);
            var context = new CopilotHiveDbContext(builder.Options);
            lock (_contexts)
                _contexts.Add(context);
            return context;
        }

        public void Dispose()
        {
            List<CopilotHiveDbContext> contexts;
            lock (_contexts)
                contexts = [.. _contexts];
            foreach (var context in contexts)
            {
                try
                {
                    context.Dispose();
                }
                catch
                {
                    // Best-effort — a leftover context must never fail a test.
                }
            }
        }
    }

    /// <summary>A factory whose acquisition always throws the pre-created sentinel.</summary>
    private sealed class ThrowingPublisherContextFactory(Exception sentinel)
        : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() => throw sentinel;
    }

    /// <summary>Throws the pre-created sentinel at the assignment-context INSERT itself.</summary>
    private sealed class AssignmentInsertThrowingInterceptor(Exception sentinel) : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (command.CommandText.Contains("worker_assignment_contexts", StringComparison.OrdinalIgnoreCase))
                throw sentinel;
            return result;
        }
    }

    /// <summary>Cancels the harness CTS the instant the insert's transaction COMMITS.</summary>
    private sealed class CancelOnCommitInterceptor(CancellationTokenSource cts) : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            cts.Cancel();
    }

    /// <summary>
    /// Throws the pre-created sentinel at the assignment-context READ — the store's zero-row
    /// duplicate readback (<c>FindRow</c>/materialization).
    /// <para>
    /// IT IS ARMED EXPLICITLY, so the SEED's own write (and any set-up read) completes normally and
    /// only the publisher's readback faults. <see cref="ThrowCount"/> proves the injection really
    /// fired, so the vector can never pass vacuously.
    /// </para>
    /// </summary>
    private sealed class AssignmentReadThrowingInterceptor(Exception sentinel) : DbCommandInterceptor
    {
        private int _armed;
        private int _throwCount;

        /// <summary>Arms the fault so the NEXT assignment-context SELECT throws.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <summary>How many times the sentinel was thrown.</summary>
        public int ThrowCount => Volatile.Read(ref _throwCount);

        private void ThrowIfTargeted(DbCommand command)
        {
            if (Volatile.Read(ref _armed) == 0)
                return;

            var trimmed = command.CommandText.TrimStart();
            if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("worker_assignment_contexts", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Interlocked.Increment(ref _throwCount);
            throw sentinel;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ThrowIfTargeted(command);
            return result;
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            ThrowIfTargeted(command);
            return result;
        }
    }

    /// <summary>
    /// COUNTS the assignment-context commands the store actually issues, split by kind, so a caller
    /// that invoked <c>InsertOnce</c> twice — or performed an extra post-success readback — is caught
    /// by a COUNT rather than by a final state that an idempotent second call would reproduce.
    /// </summary>
    /// <remarks>
    /// Counting happens at <c>…Executing</c> (the ATTEMPT), not at <c>…Executed</c>, so a second
    /// attempt is counted even if it is a zero-row no-op. Only commands naming
    /// <c>worker_assignment_contexts</c> are counted, so schema/set-up traffic cannot inflate it, and
    /// <see cref="Reset"/> lets a fixture exclude its own seeding.
    /// </remarks>
    private sealed class AssignmentCommandCountingInterceptor : DbCommandInterceptor
    {
        private int _insertAttempts;
        private int _readAttempts;

        /// <summary>INSERT attempts against <c>worker_assignment_contexts</c>.</summary>
        public int InsertAttempts => Volatile.Read(ref _insertAttempts);

        /// <summary>SELECT attempts against <c>worker_assignment_contexts</c>.</summary>
        public int ReadAttempts => Volatile.Read(ref _readAttempts);

        /// <summary>Zeroes both counters so a fixture's own seeding is excluded from the assertion.</summary>
        public void Reset()
        {
            Volatile.Write(ref _insertAttempts, 0);
            Volatile.Write(ref _readAttempts, 0);
        }

        private void Count(DbCommand command)
        {
            if (!command.CommandText.Contains("worker_assignment_contexts", StringComparison.OrdinalIgnoreCase))
                return;

            var trimmed = command.CommandText.TrimStart();
            if (trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _insertAttempts);
            else if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _readAttempts);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Count(command);
            return result;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return result;
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Count(command);
            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (1) THE REFUSALS — no context is ever synthesized
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A delivered task id with NO registered pipeline is an <c>InvalidContext</c> refusal with no
    /// inner exception and no store status; nothing is recorded and nothing is published.
    /// </summary>
    [Fact]
    public async Task PublishAsync_MissingPipeline_RefusesWithoutRecordingOrPublishing()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.UnregisteredTask();

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, task, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.Contains("no pipeline is registered for the delivered task 'task-unregistered'", refusal.Message);
        Assert.DoesNotContain("conflict —", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("indeterminate —", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0L, AssignmentRowCount("task-unregistered"));
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// A pipeline that does NOT own the delivered goal is refused; the message names BOTH goal
    /// identities so the mismatch is diagnosable.
    /// </summary>
    [Fact]
    public async Task PublishAsync_ForeignGoal_RefusesWithoutRecordingOrPublishing()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var foreignTask = harness.NewTask() with { GoalId = "goal-foreign" };

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, foreignTask, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.Contains("'goal-pub'", refusal.Message);
        Assert.Contains("'goal-foreign'", refusal.Message);
        Assert.Equal(0L, AssignmentRowCount(harness.TaskId));
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// A delivery whose task is NOT the pipeline's active task is refused even though the route
    /// (the manager's mapping) resolves it.
    /// </summary>
    [Fact]
    public async Task PublishAsync_WrongPointer_RefusesWithoutRecordingOrPublishing()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);

        const string otherTaskId = "task-pub-other";
        harness.Manager.RegisterTask(otherTaskId, harness.GoalId);
        var staleTask = harness.NewTask() with { TaskId = otherTaskId };

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, staleTask, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.Contains($"'{harness.TaskId}'", refusal.Message);
        Assert.Contains($"'{otherTaskId}'", refusal.Message);
        Assert.Equal(0L, AssignmentRowCount(otherTaskId));
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// An ABSENT slot for the active task (the registry was emptied under a live pointer) is refused
    /// by the pipeline's own preflight; the EXACT caught exception is retained as the evidence.
    /// </summary>
    [Fact]
    public async Task PublishAsync_AbsentSlot_RefusesWithExactPreflightException()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);

        // THE MUTATION: the registry is emptied while the pointer stays live, so the preflight's
        // own "no matching slot" refusal fires.
        harness.Pipeline.ClearRegistryForTest();
        var task = harness.NewTask();

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, task, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        var preflight = Assert.IsType<ArgumentException>(refusal.InnerException);
        Assert.Contains("no matching slot", preflight.Message);
        Assert.Equal(0L, AssignmentRowCount(harness.TaskId));
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// A NON-PENDING slot (Claimed) for the active task is refused by the preflight with its exact
    /// exception retained.
    /// </summary>
    [Fact]
    public async Task PublishAsync_NonPendingSlot_RefusesWithExactPreflightException()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        Assert.True(harness.Pipeline.ForceSlotStateForTest(harness.TaskId, WorkSlotState.Claimed));
        var task = harness.NewTask();

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, task, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        var preflight = Assert.IsType<ArgumentException>(refusal.InnerException);
        Assert.Contains($"state {WorkSlotState.Claimed}", preflight.Message);
        Assert.Equal(0L, AssignmentRowCount(harness.TaskId));
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// A slot whose phase maps to NO worker role is refused with the mapping's own
    /// <see cref="InvalidOperationException"/> retained as the evidence.
    /// </summary>
    [Fact]
    public async Task PublishAsync_SlotPhaseWithNoWorkerRole_RefusesWithMappingException()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);

        // A registry holding ONE Pending slot whose phase has no worker role, plus its matching
        // high-water entry — installed on the harness pipeline after its registry was cleared.
        const string planningTaskId = "task-pub-planning";
        var planningPosition = new WorkSlotPosition(1, GoalPhase.Planning, 1);
        harness.Pipeline.ClearRegistryForTest();
        harness.Pipeline.RestoreRegistry(new WorkSlotRegistrySnapshot(
            [new WorkSlotView(new WorkSlot(planningTaskId, planningPosition, 1), WorkSlotState.Pending)],
            [new WorkSlotRegistryAttemptEntry(planningPosition, 1)]));
        harness.Pipeline.SetActiveTask(planningTaskId);
        harness.Manager.RegisterTask(planningTaskId, harness.GoalId);

        var planningTask = harness.NewTask() with { TaskId = planningTaskId };

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, planningTask, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        var noMapping = Assert.IsType<InvalidOperationException>(refusal.InnerException);
        Assert.Contains("does not map to a WorkerRole", noMapping.Message);
        Assert.Contains(planningTaskId, refusal.Message);
        Assert.Equal(0L, AssignmentRowCount(planningTaskId));
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// A delivered role that disagrees with the slot's phase mapping is refused; the message names
    /// both roles and the phase.
    /// </summary>
    [Fact]
    public async Task PublishAsync_RoleMismatch_RefusesWithoutRecordingOrPublishing()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask(role: WorkerRole.Tester);

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, task, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.Contains("Tester", refusal.Message);
        Assert.Contains("Coder", refusal.Message);
        Assert.Contains("Coding", refusal.Message);
        Assert.Equal(0L, AssignmentRowCount(harness.TaskId));
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// A pinned worker whose id is currently registered under a DIFFERENT instance is refused — the
    /// point-in-time check proves the pool no longer holds the delivered instance (the ABA case).
    /// </summary>
    [Fact]
    public async Task PublishAsync_PinnedInstanceNoLongerInPool_RefusesWithoutRecording()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask();

        // THE ABA SETUP: the same id is removed and re-registered, so the pool now holds a
        // DIFFERENT instance than the pinned one.
        Assert.True(harness.Pool.RemoveWorker(harness.Worker.Id));
        _ = harness.Pool.RegisterWorker(harness.Worker.Id, []);

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, task, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.Contains("no longer the instance registered in the pool", refusal.Message);
        Assert.Equal(0L, AssignmentRowCount(harness.TaskId));
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// A MISSING pinned worker is the same refusal family — the pool lookup returns null and the
    /// reference equality fails.
    /// </summary>
    [Fact]
    public async Task PublishAsync_PinnedWorkerRemoved_RefusesWithoutRecording()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask();
        Assert.True(harness.Pool.RemoveWorker(harness.Worker.Id));

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, task, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.Equal(0L, AssignmentRowCount(harness.TaskId));
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// NULL worker and task arguments are caller-bug refusals, NOT recording failures — they throw
    /// <see cref="ArgumentNullException"/> before any store or pool access.
    /// </summary>
    [Fact]
    public async Task PublishAsync_NullWorkerOrTask_ThrowsArgumentNull()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => publisher.PublishAsync(null!, task, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => publisher.PublishAsync(harness.Worker, null!, CancellationToken.None));

        Assert.Equal(0L, AssignmentRowCount(harness.TaskId));
        Assert.Empty(harness.DrainChannel());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) THE OUTCOME DECISION and the successful publication
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE HAPPY PATH: <c>Recorded</c> publishes the matching assignment. The recorded row's
    /// goal/worker/role/slot/model — read back through a FRESH store load (test observation) — are
    /// exactly the delivered task's and the registry's values, and the channel carries exactly one
    /// assignment naming the delivered task.
    /// </summary>
    [Fact]
    public async Task PublishAsync_Recorded_PublishesMatchingAssignmentAndRowReproducesTheDelivery()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask(model: "copilot/claude-sonnet-4.6");

        await publisher.PublishAsync(harness.Worker, task, CancellationToken.None);

        // THE ROW: exactly one, with the delivered values (test observation via a fresh read).
        Assert.Equal(1L, AssignmentRowCount(harness.TaskId));
        var readback = harness.Store.Load(harness.TaskId);
        Assert.NotNull(readback);
        Assert.Equal(harness.GoalId, readback!.Context.GoalId);
        Assert.Equal(harness.Worker.Id, readback.Context.WorkerId);
        Assert.Equal(WorkerRole.Coder, readback.Context.Role);
        Assert.Equal(harness.TaskId, readback.Context.Slot.TaskId);
        Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), readback.Context.Slot.Position);
        Assert.Equal(harness.SlotBuild.Attempt, readback.Context.Slot.Attempt);
        Assert.Equal("copilot/claude-sonnet-4.6", readback.Context.Model);

        // THE PUBLICATION: exactly one assignment on the pinned worker's own channel.
        var published = harness.DrainChannel();
        var assignment = Assert.Single(published).Assignment;
        Assert.Equal(task.TaskId, assignment.TaskId);
        Assert.Equal(task.GoalId, assignment.GoalId);
        Assert.Equal(task.Model, assignment.Model);
        Assert.Equal(task.Prompt, assignment.Prompt);
    }

    /// <summary>
    /// A SEEDED IDENTICAL row is <c>AlreadyRecorded</c> — which allows THIS invocation's
    /// publication (a duplicate caller invocation CAN resend) — and the stored row, including its
    /// first-assigned instant, is left byte-identical.
    /// </summary>
    [Fact]
    public async Task PublishAsync_SeededIdenticalRow_AlreadyRecorded_PublishesAndKeepsOriginalTimestamp()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);

        // THE SEED: the exact context the publisher will construct, recorded earlier.
        var seeded = harness.ExpectedContext();
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, harness.Store.InsertOnce(seeded).Status);
        var seededTimestamp = harness.RawFirstAssigned(harness.TaskId);

        var task = harness.NewTask();
        await publisher.PublishAsync(harness.Worker, task, CancellationToken.None);

        // THE PUBLICATION happened despite the duplicate, and the row is untouched.
        var assignment = Assert.Single(harness.DrainChannel()).Assignment;
        Assert.Equal(task.TaskId, assignment.TaskId);
        Assert.Equal(1L, AssignmentRowCount(harness.TaskId));
        Assert.Equal(seededTimestamp, harness.RawFirstAssigned(harness.TaskId));
    }

    /// <summary>
    /// A seeded DIFFERENT context for the same task id is <c>Conflict</c>: publication is
    /// PROHIBITED, the channel stays empty, and the existing row is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task PublishAsync_SeededDifferentWorker_Conflict_ProhibitsPublicationAndKeepsRow()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);

        var differentWorker = harness.ExpectedContext(workerId: "worker-seeded");
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, harness.Store.InsertOnce(differentWorker).Status);
        var seededTimestamp = harness.RawFirstAssigned(harness.TaskId);

        var task = harness.NewTask();
        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, task, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.Conflict, refusal.Reason);
        Assert.Equal(WorkerAssignmentWriteStatus.Conflict, refusal.StoreStatus);
        Assert.Null(refusal.InnerException);
        Assert.Contains("conflict —", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(harness.TaskId, refusal.Message);
        Assert.Empty(harness.DrainChannel());
        Assert.Equal(1L, AssignmentRowCount(harness.TaskId));
        Assert.Equal(seededTimestamp, harness.RawFirstAssigned(harness.TaskId));
        Assert.Equal("worker-seeded", harness.RawWorkerId(harness.TaskId));
    }

    /// <summary>
    /// A THROWING INSERT is write UNCERTAINTY: publication is prohibited, the refusal carries the
    /// store's <c>Indeterminate</c> status and the store's EXACT write exception object as its inner
    /// exception, and no row exists.
    /// </summary>
    [Fact]
    public async Task PublishAsync_ThrowingInsert_Indeterminate_ProhibitsPublicationAndCarriesExactWriteException()
    {
        var sentinel = new InvalidOperationException("the assignment-context insert was sabotaged");
        var harness = NewHarness(new AssignmentInsertThrowingInterceptor(sentinel));
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask();

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, task, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.Indeterminate, refusal.Reason);
        Assert.Equal(WorkerAssignmentWriteStatus.Indeterminate, refusal.StoreStatus);
        Assert.Same(sentinel, refusal.InnerException);
        Assert.Contains("indeterminate —", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(harness.TaskId, refusal.Message);
        Assert.Empty(harness.DrainChannel());
        Assert.Equal(0L, AssignmentRowCount(harness.TaskId));
    }

    /// <summary>
    /// A THROWING CONTEXT ACQUISITION is a <c>StoreError</c>: the exact caught exception is the inner
    /// exception, there is no store status (the store never reported an outcome), and nothing is
    /// recorded or published.
    /// </summary>
    [Fact]
    public async Task PublishAsync_ThrowingContextAcquisition_StoreError_CarriesExactException()
    {
        var sentinel = new InvalidOperationException("the context acquisition was sabotaged");
        var store = NewStore(new ThrowingPublisherContextFactory(sentinel));
        var manager = new GoalPipelineManager();
        var pool = new WorkerPool();
        var worker = pool.RegisterWorker("worker-store-error", []);

        const string taskId = "task-pub-storeerror";
        const string goalId = "goal-storeerror";
        var pipeline = manager.CreatePipeline(new Goal { Id = goalId, Description = "store error" });
        pipeline.AllocateAttemptAndRegisterSlot(taskId, new WorkSlotPosition(1, GoalPhase.Coding, 1));
        pipeline.SetActiveTask(taskId);
        manager.RegisterTask(taskId, goalId);

        var publisher = new WorkerAssignmentPublisher(manager, pool, store);
        var task = new WorkTask
        {
            TaskId = taskId,
            GoalId = goalId,
            GoalDescription = "store error",
            Prompt = "do the work",
            Role = WorkerRole.Coder,
            Model = "model-storeerror",
            Repositories = [new TargetRepository { Name = "repo", Url = "https://example.invalid/repo" }],
        };

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(worker, task, CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.StoreError, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Same(sentinel, refusal.InnerException);
        Assert.Contains("store-error —", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(taskId, refusal.Message);
        Assert.Empty(DrainChannel(worker));
        Assert.Equal(0L, AssignmentRowCount(taskId));
    }

    /// <summary>
    /// THE OBSERVER CONTROL: without the sabotage interceptor the same delivery records and
    /// publishes, so the Indeterminate refusal above is attributable to the injected fault alone.
    /// </summary>
    [Fact]
    public async Task PublishAsync_UngatedControl_RecordsAndPublishes()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask();

        await publisher.PublishAsync(harness.Worker, task, CancellationToken.None);

        Assert.Equal(1L, AssignmentRowCount(harness.TaskId));
        var assignment = Assert.Single(harness.DrainChannel()).Assignment;
        Assert.Equal(task.TaskId, assignment.TaskId);
    }

    /// <summary>
    /// A THROWING DUPLICATE READBACK is a <c>StoreError</c> — NOT a Conflict, not AlreadyRecorded and
    /// not Indeterminate. A row is seeded first, so the publisher's INSERT is arbitrated to ZERO rows
    /// and the store enters its READ path; that read is then faulted with a pre-created sentinel.
    /// <para>
    /// This is the publisher's OWN mapping of a read/integrity failure. A green store test is not
    /// evidence for it: the assertion here is that the refusal the PUBLISHER raises carries
    /// <c>StoreError</c>, a NULL store status (the store never reported an outcome) and the EXACT
    /// thrown object, and that nothing is published.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PublishAsync_ThrowingDuplicateReadback_StoreError_CarriesExactExceptionAndPublishesNothing()
    {
        var sentinel = new InvalidOperationException("the duplicate readback was sabotaged");
        var readFault = new AssignmentReadThrowingInterceptor(sentinel);
        var harness = NewHarness(readFault);
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);

        // THE SEED runs BEFORE the fault is armed, so the row really exists and the publisher's own
        // INSERT is arbitrated to zero rows — which is what drives the store into its READ path.
        var seeded = harness.ExpectedContext();
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, harness.Store.InsertOnce(seeded).Status);
        var seededTimestamp = harness.RawFirstAssigned(harness.TaskId);

        // ARM: from here on, the assignment-context READ throws the sentinel.
        readFault.Arm();

        var task = harness.NewTask();
        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, task, CancellationToken.None));

        // THE INJECTION REALLY FIRED — the vector cannot pass vacuously.
        Assert.True(readFault.ThrowCount > 0, "the duplicate readback was never faulted");

        // THE PUBLISHER'S MAPPING: a thrown store read is a StoreError with the EXACT object.
        Assert.Equal(WorkerAssignmentRecordingFailureReason.StoreError, refusal.Reason);
        Assert.Null(refusal.StoreStatus);
        Assert.Same(sentinel, refusal.InnerException);
        Assert.Contains("store-error —", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(harness.TaskId, refusal.Message);

        // IT IS NOT MISREPORTED as one of the store's confirmed outcomes.
        Assert.NotEqual(WorkerAssignmentRecordingFailureReason.Conflict, refusal.Reason);
        Assert.NotEqual(WorkerAssignmentRecordingFailureReason.Indeterminate, refusal.Reason);

        // NOTHING WAS PUBLISHED, and the seeded row is untouched.
        Assert.Empty(harness.DrainChannel());
        Assert.Equal(1L, AssignmentRowCount(harness.TaskId));
        Assert.Equal(seededTimestamp, harness.RawFirstAssigned(harness.TaskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2b) THE ONE-CALL / NO-EXTRA-READ CONTRACT — counted, not inferred
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FRESH <c>Recorded</c> PATH ISSUES EXACTLY ONE INSERT AND NO READ. Because a second
    /// identical <c>InsertOnce</c> would be idempotent, final row/timestamp state cannot distinguish
    /// one call from two — so the ATTEMPTS are COUNTED at the command boundary instead.
    /// <para>
    /// A single-row insert never reaches the store's zero-row read path, so the production path
    /// performs NO readback at all: any SELECT here would be the forbidden post-success production
    /// read.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PublishAsync_Recorded_IssuesExactlyOneInsertAttemptAndNoProductionReadback()
    {
        var counter = new AssignmentCommandCountingInterceptor();
        var harness = NewHarness(counter);
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask();

        // The count starts AFTER the arrangement, so only the publisher's own statements are counted.
        counter.Reset();

        await publisher.PublishAsync(harness.Worker, task, CancellationToken.None);

        Assert.Equal(1, counter.InsertAttempts);
        Assert.Equal(0, counter.ReadAttempts);

        // The delivery itself still happened — the counts are not measuring a no-op.
        Assert.Equal(1L, AssignmentRowCount(harness.TaskId));
        Assert.Equal(task.TaskId, Assert.Single(harness.DrainChannel()).Assignment.TaskId);
    }

    /// <summary>
    /// THE SEEDED <c>AlreadyRecorded</c> PATH ALSO ISSUES EXACTLY ONE INSERT ATTEMPT. The store's own
    /// zero-row arbitration performs ONE readback to compare the stored context, and the publisher
    /// adds NOTHING after that success: no second insert-once call and no extra production read.
    /// </summary>
    [Fact]
    public async Task PublishAsync_AlreadyRecorded_IssuesExactlyOneInsertAttemptAndOnlyTheStoresOwnReadback()
    {
        var counter = new AssignmentCommandCountingInterceptor();
        var harness = NewHarness(counter);
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);

        var seeded = harness.ExpectedContext();
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, harness.Store.InsertOnce(seeded).Status);

        // EXCLUDE the seeding: only the publisher's own statements are counted from here.
        counter.Reset();

        var task = harness.NewTask();
        await publisher.PublishAsync(harness.Worker, task, CancellationToken.None);

        // EXACTLY ONE insert attempt: a publisher that called InsertOnce twice would count two, even
        // though the second call is idempotent and leaves the final state identical.
        Assert.Equal(1, counter.InsertAttempts);

        // EXACTLY ONE read: the store's OWN duplicate readback. A post-success production Load would
        // make this two.
        Assert.Equal(1, counter.ReadAttempts);

        Assert.Equal(task.TaskId, Assert.Single(harness.DrainChannel()).Assignment.TaskId);
    }

    /// <summary>
    /// A BLOCKED OUTCOME ALSO CALLS THE STORE EXACTLY ONCE: the <c>Conflict</c> refusal performs one
    /// insert attempt and the store's one arbitration read, and the publisher adds no retry.
    /// </summary>
    [Fact]
    public async Task PublishAsync_Conflict_IssuesExactlyOneInsertAttemptAndNoRetry()
    {
        var counter = new AssignmentCommandCountingInterceptor();
        var harness = NewHarness(counter);
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);

        Assert.Equal(
            WorkerAssignmentWriteStatus.Recorded,
            harness.Store.InsertOnce(harness.ExpectedContext(workerId: "worker-seeded")).Status);

        counter.Reset();

        var refusal = await Assert.ThrowsAsync<WorkerAssignmentRecordingException>(
            () => publisher.PublishAsync(harness.Worker, harness.NewTask(), CancellationToken.None));

        Assert.Equal(WorkerAssignmentRecordingFailureReason.Conflict, refusal.Reason);
        Assert.Equal(1, counter.InsertAttempts);
        Assert.Equal(1, counter.ReadAttempts);
        Assert.Empty(harness.DrainChannel());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) THE CALLER-CANCELLATION OBSERVATIONS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A PRE-CANCELLED token propagates the caller's own <see cref="OperationCanceledException"/>
    /// BEFORE anything is recorded or published.
    /// </summary>
    [Fact]
    public async Task PublishAsync_CancelledBeforeRecording_PropagatesAndRecordsNothing()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(
            () => publisher.PublishAsync(harness.Worker, task, cts.Token));

        Assert.Equal(cts.Token, cancelled.CancellationToken);
        Assert.IsNotType<WorkerAssignmentRecordingException>(cancelled);
        Assert.Equal(0L, AssignmentRowCount(harness.TaskId));
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// A cancellation observed AFTER the commit (raised by a transaction interceptor at the commit
    /// instant) cancels the SEND only: the exception is the caller's own OCE, and the ALREADY
    /// COMMITTED context is deliberately retained — a fresh readback reproduces the row.
    /// </summary>
    [Fact]
    public async Task PublishAsync_CancelledAfterCommit_PropagatesAndRetainsCommittedContext()
    {
        using var cts = new CancellationTokenSource();
        var harness = NewHarness(new CancelOnCommitInterceptor(cts));
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask();

        // THE COMMIT-TIME CANCELLATION: the interceptor cancels the token the instant the insert's
        // transaction commits — INSIDE InsertOnce, so the pre-publication observation is the only
        // step that can see it. No polling, no delay.
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publisher.PublishAsync(harness.Worker, task, cts.Token));

        Assert.Equal(cts.Token, cancelled.CancellationToken);
        Assert.IsNotType<WorkerAssignmentRecordingException>(cancelled);
        // THE COMMITTED CONTEXT IS RETAINED: nothing was deleted or rebound.
        Assert.Equal(1L, AssignmentRowCount(harness.TaskId));
        var readback = harness.Store.Load(harness.TaskId);
        Assert.NotNull(readback);
        Assert.Equal(harness.Worker.Id, readback!.Context.WorkerId);
        Assert.Equal(task.Model, readback.Context.Model);
        // …and nothing was published.
        Assert.Empty(harness.DrainChannel());
    }

    /// <summary>
    /// A POST-RECORD CHANNEL FAULT propagates UNCHANGED: the completed channel's
    /// <see cref="ChannelClosedException"/> is never re-labelled as a recording failure, and the
    /// recorded row is retained.
    /// </summary>
    [Fact]
    public async Task PublishAsync_PostRecordChannelFault_PropagatesOriginalExceptionAndKeepsRow()
    {
        var harness = NewHarness();
        var publisher = new WorkerAssignmentPublisher(harness.Manager, harness.Pool, harness.Store);
        var task = harness.NewTask();
        Assert.True(harness.Worker.MessageChannel.Writer.TryComplete());

        var fault = await Assert.ThrowsAsync<ChannelClosedException>(
            () => publisher.PublishAsync(harness.Worker, task, CancellationToken.None));
        Assert.IsNotType<WorkerAssignmentRecordingException>(fault);

        Assert.Equal(1L, AssignmentRowCount(harness.TaskId));
        var readback = harness.Store.Load(harness.TaskId);
        Assert.NotNull(readback);
        Assert.Equal(task.Model, readback!.Context.Model);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) THE FAILURE CONTRACT: factories, pairing, and the fail-closed disposition
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FAIL-CLOSED DISPOSITION: the missing-publisher failure names no store status, carries no
    /// inner exception, and its message states that nothing was recorded, nothing was published and
    /// no fallback write was attempted.
    /// </summary>
    [Fact]
    public void MissingPublisherFactory_FailClosedContract()
    {
        var failure = WorkerAssignmentRecordingException.MissingPublisher();

        Assert.Equal(WorkerAssignmentRecordingFailureReason.MissingPublisher, failure.Reason);
        Assert.Null(failure.StoreStatus);
        Assert.Null(failure.InnerException);
        Assert.Contains("missing-publisher", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not recorded and was not published", failure.Message);
        Assert.Contains("no fallback write was attempted", failure.Message);
        Assert.DoesNotContain("conflict —", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("indeterminate —", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONFLICT FACTORY carries the store's Conflict status and NO inner exception; the
    /// INDETERMINATE factory carries the store's Indeterminate status and the EXACT write exception.
    /// </summary>
    [Fact]
    public void ConflictAndIndeterminateFactories_CarryStoreStatusAndExactEvidence()
    {
        var writeException = new InvalidOperationException("the write did not confirm");

        var conflict = WorkerAssignmentRecordingException.Conflict("task-x");
        Assert.Equal(WorkerAssignmentRecordingFailureReason.Conflict, conflict.Reason);
        Assert.Equal(WorkerAssignmentWriteStatus.Conflict, conflict.StoreStatus);
        Assert.Null(conflict.InnerException);
        Assert.Contains("conflict —", conflict.Message, StringComparison.Ordinal);

        var indeterminate = WorkerAssignmentRecordingException.Indeterminate("task-x", writeException);
        Assert.Equal(WorkerAssignmentRecordingFailureReason.Indeterminate, indeterminate.Reason);
        Assert.Equal(WorkerAssignmentWriteStatus.Indeterminate, indeterminate.StoreStatus);
        Assert.Same(writeException, indeterminate.InnerException);
        Assert.Contains("indeterminate —", indeterminate.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE INVALID-CONTEXT FACTORY PAIR: the simple comparison refusal carries no inner exception,
    /// and the validator-throwing refusal carries the EXACT caught exception; both refuse to record
    /// and to publish in their message.
    /// </summary>
    [Fact]
    public void InvalidContextFactories_SimpleAndThrownForms_CarryCorrectEvidence()
    {
        var simple = WorkerAssignmentRecordingException.InvalidContext("the pointer moved");
        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, simple.Reason);
        Assert.Null(simple.StoreStatus);
        Assert.Null(simple.InnerException);
        Assert.Contains("invalid-context —", simple.Message, StringComparison.Ordinal);
        Assert.Contains("the pointer moved", simple.Message);

        var cause = new InvalidOperationException("the registry was malformed");
        var thrown = WorkerAssignmentRecordingException.InvalidContext("preflight threw", cause);
        Assert.Equal(WorkerAssignmentRecordingFailureReason.InvalidContext, thrown.Reason);
        Assert.Null(thrown.StoreStatus);
        Assert.Same(cause, thrown.InnerException);
    }

    /// <summary>
    /// THE STRUCTURAL PAIRING is enforced by the ONE private validating constructor: an inconsistent
    /// combination — an undefined reason, an invented cause for MissingPublisher, a missing status
    /// for Conflict, a store status for a pre-store refusal, a missing cause for StoreError or
    /// Indeterminate, or a blank message — cannot be constructed at all.
    /// </summary>
    [Theory]
    [InlineData("undefined-reason")]
    [InlineData("missing-publisher-with-inner")]
    [InlineData("conflict-without-status")]
    [InlineData("conflict-with-inner")]
    [InlineData("invalid-context-with-status")]
    [InlineData("store-error-without-inner")]
    [InlineData("indeterminate-without-status")]
    [InlineData("indeterminate-without-inner")]
    [InlineData("blank-message")]
    public void PrivateConstructor_RejectsInconsistentEvidencePairings(string kind)
    {
        var ctor = typeof(WorkerAssignmentRecordingException)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 4);

        object?[] args = kind switch
        {
            "undefined-reason" =>
            [
                (WorkerAssignmentRecordingFailureReason)999, "reason", null, null,
            ],
            "missing-publisher-with-inner" =>
            [
                WorkerAssignmentRecordingFailureReason.MissingPublisher, "message", null,
                new InvalidOperationException("invented"),
            ],
            "conflict-without-status" =>
            [
                WorkerAssignmentRecordingFailureReason.Conflict, "message", null, null,
            ],
            "conflict-with-inner" =>
            [
                WorkerAssignmentRecordingFailureReason.Conflict, "message",
                WorkerAssignmentWriteStatus.Conflict, new InvalidOperationException("invented"),
            ],
            "invalid-context-with-status" =>
            [
                WorkerAssignmentRecordingFailureReason.InvalidContext, "message",
                WorkerAssignmentWriteStatus.Conflict, null,
            ],
            "store-error-without-inner" =>
            [
                WorkerAssignmentRecordingFailureReason.StoreError, "message", null, null,
            ],
            "indeterminate-without-status" =>
            [
                WorkerAssignmentRecordingFailureReason.Indeterminate, "message", null,
                new InvalidOperationException("the write"),
            ],
            "indeterminate-without-inner" =>
            [
                WorkerAssignmentRecordingFailureReason.Indeterminate, "message",
                WorkerAssignmentWriteStatus.Indeterminate, null,
            ],
            "blank-message" =>
            [
                WorkerAssignmentRecordingFailureReason.MissingPublisher, "   ", null, null,
            ],
            _ => throw new InvalidOperationException($"Unknown pairing kind '{kind}'."),
        };

        Assert.Throws<TargetInvocationException>(() =>
            ctor.Invoke(BindingFlags.NonPublic | BindingFlags.Instance, null, args!, null));
    }

    /// <summary>
    /// THE DEPENDENCY GUARD: the publisher's constructor is the validation authority for its own
    /// dependencies — a null dependency is a caller bug, refused eagerly.
    /// </summary>
    [Fact]
    public void PublisherConstructor_RefusesNullDependencies()
    {
        var harness = NewHarness();
        Assert.Throws<ArgumentNullException>(
            () => new WorkerAssignmentPublisher(null!, harness.Pool, harness.Store));
        Assert.Throws<ArgumentNullException>(
            () => new WorkerAssignmentPublisher(harness.Manager, null!, harness.Store));
        Assert.Throws<ArgumentNullException>(
            () => new WorkerAssignmentPublisher(harness.Manager, harness.Pool, null!));
    }

    /// <summary>Drains a worker's channel — the publication observation for a locally built worker.</summary>
    private static List<OrchestratorMessage> DrainChannel(ConnectedWorker worker)
    {
        var messages = new List<OrchestratorMessage>();
        while (worker.MessageChannel.Reader.TryRead(out var message))
            messages.Add(message);
        return messages;
    }
}

/// <summary>
/// THE PRODUCTION DI REGISTRATION (verified against the REAL Program container): the concrete
/// publisher and its narrow interface resolve to the SAME singleton, wired to the container's
/// pipeline manager, worker pool and assignment-context store — proving the mandatory recorder is
/// registered exactly as the goal's DI slice requires.
/// </summary>
[Collection("HiveIntegration")]
public sealed class WorkerAssignmentPublisherDiRegistrationTests
{
    private readonly HiveTestFactory _factory;

    public WorkerAssignmentPublisherDiRegistrationTests(HiveTestFactory factory) => _factory = factory;

    [Fact]
    public void DiResolves_ConcretePublisherInterfaceAndStore_AsTheSameSingletonGraph()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;

        var concrete = provider.GetRequiredService<WorkerAssignmentPublisher>();
        var face = provider.GetRequiredService<IWorkerAssignmentPublisher>();
        var store = provider.GetRequiredService<WorkerAssignmentContextStore>();
        var manager = provider.GetRequiredService<GoalPipelineManager>();
        var pool = provider.GetRequiredService<WorkerPool>();

        // THE SAME SINGLETON behind both service types.
        Assert.Same(concrete, face);

        // THE PUBLISHER'S DEPENDENCIES are the container's own singletons.
        Assert.Same(manager, PublisherField(concrete, "_pipelineManager"));
        Assert.Same(pool, PublisherField(concrete, "_workerPool"));
        Assert.Same(store, PublisherField(concrete, "_store"));
    }

    private static object PublisherField(WorkerAssignmentPublisher publisher, string name) =>
        typeof(WorkerAssignmentPublisher)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(publisher)!;
}

/// <summary>
/// THE REAL READY PATH'S CALLER-CANCELLATION SEMANTICS, through the REAL
/// <see cref="HiveOrchestratorService.WorkStream"/> → <c>HandleWorkerReady</c> path with the
/// PRODUCTION <see cref="WorkerAssignmentPublisher"/> over a REAL file-backed SQLite database.
/// </summary>
/// <remarks>
/// <para>
/// THE TWO OBSERVATIONS THE CONTRACT PROMISES, proven on the live transport path:
/// </para>
/// <list type="bullet">
///   <item><description>A PRE-CANCELLED caller token propagates as the caller's OWN
///     <see cref="OperationCanceledException"/> BEFORE anything is recorded or published — never as a
///     blocked-result wrapper — and the stream ends, removing the worker (the pre-existing teardown
///     semantics for a real cancellation).</description></item>
///   <item><description>A cancellation observed AT THE COMMIT INSTANT (raised by a transaction
///     interceptor the instant the insert's transaction commits — INSIDE the recording call) is
///     seen at the publisher's pre-publication observation: the caller's own OCE propagates, the
///     ALREADY COMMITTED context row deliberately SURVIVES (nothing is deleted or rebound), and no
///     assignment reaches the transport.</description></item>
/// </list>
/// <para>
/// THE HARNESS mirrors the real transport: a genuine pipeline (routing + pointer + Pending slot all
/// really registered), a real <see cref="GoalDispatcher"/>, a real queue and worker pool, and a
/// stream reader/writer pair that signals deterministically — never a sleep.
/// </para>
/// </remarks>
public sealed class WorkerAssignmentReadyCancellationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"copilothive-wap-cancel-{Guid.NewGuid():N}.db");

    private readonly List<IDisposable> _fixtures = [];

    /// <summary>Upper bound for every await in these vectors — never a fixed delay.</summary>
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    public WorkerAssignmentReadyCancellationTests()
    {
        using var connection = OpenConnection();
        using var context = ContextOn(connection);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
        {
            try
            {
                fixture.Dispose();
            }
            catch
            {
                // Best-effort — a leftover fixture must never fail a test.
            }
        }

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

    private string ConnectionString => $"Data Source={_dbPath};Pooling=False;Default Timeout=15";

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static CopilotHiveDbContext ContextOn(SqliteConnection connection, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new CopilotHiveDbContext(builder.Options);
    }

    private ReadyCancellationFactory NewFactory(params IInterceptor[] interceptors)
    {
        var factory = new ReadyCancellationFactory(ConnectionString, interceptors);
        _fixtures.Add(factory);
        return factory;
    }

    /// <summary>The recorded row's task id, read through a fresh connection; <c>null</c> when absent.</summary>
    private string? RawAssignmentTaskId()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_id FROM worker_assignment_contexts LIMIT 1";
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// THE CANCELLATION SEMANTICS HARNESS: the real transport path with the production publisher,
    /// exposing the caller token via a hook the fixture controls.
    /// </summary>
    private sealed class CancellationHarness
    {
        public required HiveOrchestratorService Service { get; init; }
        public required GoalPipelineManager Manager { get; init; }
        public required GoalPipeline Pipeline { get; init; }
        public required WorkerPool Pool { get; init; }
        public required TaskQueue Queue { get; init; }
        public required ConnectedWorker Worker { get; init; }
        public required WorkTask DeliveredTask { get; init; }
        public required string TaskId { get; init; }
        public required WorkerAssignmentContextStore Store { get; init; }
        public required CapturingReadyLogger Logger { get; init; }
        public required RecordingStreamWriter Writer { get; init; }
        public required Func<CancellationToken> CallerToken { get; init; }
        public required Task StreamTask { get; init; }
        public required ReadyCancellationStreamReader Reader { get; init; }

        public static async Task<CancellationHarness> CreateAsync(
            IDbContextFactory<CopilotHiveDbContext> storeFactory,
            CancellationTokenSource? callerCts = null)
        {
            var pool = new WorkerPool();
            var queue = new TaskQueue();
            var manager = new GoalPipelineManager();

            var goal = new Goal
            {
                Id = $"goal-cancel-{Guid.NewGuid():N}",
                Description = "Ready cancellation goal",
                RepositoryNames = ["test-repo"],
            };

            var goalManager = new GoalManager();
            goalManager.AddSource(new ReadyCancellationGoalSource(goal));
            await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

            var dispatcher = new GoalDispatcher(
                goalManager,
                manager,
                queue,
                new GrpcWorkerGateway(pool),
                new TaskCompletionNotifier(),
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
                config: new HiveConfigFile
                {
                    Orchestrator = new OrchestratorConfig { WorkerTaskTimeoutMinutes = 0 },
                },
                dashboardNotifier: new DashboardNotifier());

            var store = new WorkerAssignmentContextStore(
                storeFactory, NullLogger<WorkerAssignmentContextStore>.Instance);
            var realPublisher = new WorkerAssignmentPublisher(manager, pool, store);
            var logger = new CapturingReadyLogger();

            var service = new HiveOrchestratorService(
                pool,
                queue,
                manager,
                new TaskCompletionNotifier(),
                dispatcher,
                logger,
                dashboardNotifier: new DashboardNotifier(),
                assignmentPublisher: realPublisher);

            // THE GENUINE OWNERSHIP: routing + pointer + Pending slot, ALL really registered.
            var pipeline = manager.CreatePipeline(goal);
            pipeline.AdvanceTo(GoalPhase.Coding);
            var built = pipeline.AllocateAttemptAndRegisterSlot(
                $"task-cancel-{Guid.NewGuid():N}", new WorkSlotPosition(1, GoalPhase.Coding, 1));
            pipeline.SetActiveTask(built.TaskId);
            manager.RegisterTask(built.TaskId, goal.Id);

            var worker = pool.RegisterWorker($"worker-cancel-{Guid.NewGuid():N}", []);

            var task = new WorkTask
            {
                TaskId = built.TaskId,
                GoalId = goal.Id,
                GoalDescription = goal.Description,
                Prompt = "do the cancel work",
                Role = WorkerRole.Coder,
                Model = "copilot/claude-sonnet-4.6",
                Repositories =
                    [new TargetRepository { Name = "test-repo", Url = "https://example.invalid/repo" }],
            };
            queue.Enqueue(task);

            var writer = new RecordingStreamWriter();
            var reader = new ReadyCancellationStreamReader();
            var streamTask = service.WorkStream(reader, writer, CancellationCallContext(callerCts));

            return new CancellationHarness
            {
                Service = service,
                Manager = manager,
                Pipeline = pipeline,
                Pool = pool,
                Queue = queue,
                Worker = worker,
                DeliveredTask = task,
                TaskId = built.TaskId,
                Store = store,
                Logger = logger,
                Writer = writer,
                CallerToken = () => callerCts?.Token ?? CancellationToken.None,
                StreamTask = streamTask,
                Reader = reader,
            };
        }

        /// <summary>Pushes a real Ready without waiting for any outcome (fire, then gate separately).</summary>
        public Task PushReadyAsync()
        {
            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });
            return Task.CompletedTask;
        }

        /// <summary>Pushes a real Ready and waits until the transport has FORWARDED an assignment.</summary>
        public async Task SendReadyAndAwaitPublishedAsync()
        {
            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });
            await Writer.AssignmentForwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>Pushes a real Ready and waits for the production blocked warning.</summary>
        public async Task SendReadyAndAwaitBlockedAsync()
        {
            var signal = Logger.WaitForBlockedWarning();
            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Whether the stream has terminated at all — normally, faulted, or cancelled (the stream's
        /// own <c>catch (OperationCanceledException)</c> swallows the caller OCE and returns
        /// normally, so a cancellation can drain WITHOUT faulting).
        /// </summary>
        public bool StreamEnded { get; private set; }

        /// <summary>Whether the stream terminated with an exception rather than draining.</summary>
        public bool StreamFaulted { get; private set; }

        /// <summary>The stream's terminal exception, captured on the drain path.</summary>
        public Exception? StreamTerminalException { get; private set; }

        /// <summary>Whether the pinned worker was removed by the stream's teardown.</summary>
        public bool WorkerRemovedAfterStream { get; private set; }

        /// <summary>Ends the stream input and awaits its termination, capturing the terminal fault.</summary>
        public async Task DrainAsync()
        {
            Reader.Complete();
            try
            {
                await StreamTask.WaitAsync(BoundedWait, CancellationToken.None);
                StreamEnded = true;
                StreamFaulted = false;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("the WorkStream did not drain within the bound");
            }
            catch (Exception ex)
            {
                StreamEnded = true;
                StreamFaulted = true;
                StreamTerminalException = ex;
            }
        }

        /// <summary>Observes whether the stream's finally block removed the pinned worker.</summary>
        public void ObserveWorkerTeardown()
        {
            // THE INSTANCE-AWARE OBSERVATION: the teardown removes via the exact pinned instance, so
            // a same-id replacement would keep the id present while the PINNED instance is gone. The
            // channel-completed marker is the honest "this exact instance was removed" signal — but
            // it must be probed BEFORE the fixture's own drain completes the channel.
            WorkerRemovedAfterStream =
                !Worker.MessageChannel.Writer.TryWrite(new OrchestratorMessage());
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (1) PRE-CANCELLED caller token: the caller's own OCE, nothing recorded or published
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A caller token ALREADY cancelled when the stream starts propagates as the caller's OWN
    /// <see cref="OperationCanceledException"/> — swallowed by the stream's pre-existing catch — so
    /// the stream ends before any message is read: nothing is recorded, nothing is published, no
    /// blocked warning is emitted (a cancellation is not a refusal), and the worker is never even
    /// pinned (the read loop observed the cancelled token before the first Ready).
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_PreCancelledCallerToken_PropagatesAndRecordsNothing()
    {
        using var callerCts = new CancellationTokenSource();
        var h = await CancellationHarness.CreateAsync(NewFactory(), callerCts);

        try
        {
            // THE CANCELLATION IS ALREADY IN EFFECT before the Ready arrives.
            await callerCts.CancelAsync();

            await h.DrainAsync();

            // THE CALLER'S OWN OCE reached the stream's outer handler: the stream ENDED (the
            // production catch swallows the OCE itself and returns normally) and no
            // recording-failure wrapper was ever created.
            Assert.True(h.StreamEnded, "the pre-cancelled caller token must end the stream");

            // NOTHING was recorded and NOTHING was published.
            Assert.Null(RawAssignmentTaskId());
            Assert.Empty(h.Writer.Assignments);
            Assert.Empty(h.Writer.Messages);

            // A CALLER CANCELLATION IS NOT A REFUSAL: no blocked warning was emitted.
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));
        }
        finally
        {
            if (!h.StreamEnded)
                await h.DrainAsync();
        }
    }

    /// <summary>
    /// A caller token cancelled WHILE the stream is already pinned (the Ready is read FIRST with a
    /// live token, and the token is cancelled during the recording's transaction commit) ends the
    /// stream with the caller's OWN <see cref="OperationCanceledException"/> — swallowed by the
    /// stream's pre-existing catch — so the pinned worker is removed by the finally block (the
    /// pre-existing teardown semantics for a real cancellation), nothing is published, and no
    /// recording refusal is logged (a cancellation is not a refusal).
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_CommitTimeCancellation_TeardownRemovesPinnedWorker()
    {
        using var commitCts = new CancellationTokenSource();
        var commitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var h = await CancellationHarness.CreateAsync(
            NewFactory(new CancelOnCommitCtsInterceptor(commitCts, commitGate)), commitCts);

        try
        {
            // Push the Ready: the worker is pinned, the recording starts, the transaction commits
            // (the interceptor cancels the caller token and fires the gate), and the pre-publication
            // observation throws the caller's own OCE.
            await h.PushReadyAsync();
            await commitGate.Task.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // THE INSTANCE-AWARE TEARDOWN OBSERVATION, taken BEFORE the fixture's own drain
            // completes the channel: the stream's finally removed the PINNED instance (the
            // channel-completed marker fails only when this exact instance was removed).
            await h.DrainAsync();
            h.ObserveWorkerTeardown();

            Assert.True(h.StreamEnded, "the commit-time cancellation must end the stream");
            Assert.True(
                h.WorkerRemovedAfterStream,
                "the stream's finally must remove the pinned worker on a caller cancellation");

            // NOTHING was published and NO blocked warning was emitted (a cancellation is not a
            // refusal), while no success log can exist either.
            Assert.Empty(h.Writer.Assignments);
            Assert.Empty(h.Writer.Messages);
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));

            // THE COMMITTED CONTEXT IS DELIBERATELY RETAINED — nothing was deleted or rebound.
            Assert.Equal(h.TaskId, RawAssignmentTaskId());
            var readback = h.Store.Load(h.TaskId);
            Assert.NotNull(readback);
            Assert.Equal(h.Worker.Id, readback!.Context.WorkerId);
            Assert.Equal(WorkerRole.Coder, readback.Context.Role);
            Assert.Equal(h.TaskId, readback.Context.Slot.TaskId);
            Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), readback.Context.Slot.Position);
            Assert.Equal("copilot/claude-sonnet-4.6", readback.Context.Model);
        }
        finally
        {
            if (!h.StreamEnded)
                await h.DrainAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) COMMIT-TIME caller cancellation: the OCE propagates, the committed row SURVIVES
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A caller cancellation observed AT THE COMMIT INSTANT (the token is cancelled by a transaction
    /// interceptor the instant the insert's transaction commits — INSIDE the recording call) cancels
    /// the SEND only: the caller's own OCE propagates out of the stream, the ALREADY COMMITTED
    /// context row deliberately SURVIVES (a fresh readback reproduces it), and no assignment reaches
    /// the transport.
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_CommitTimeCancellation_PropagatesAndRetainsCommittedRow()
    {
        using var commitCts = new CancellationTokenSource();
        var harnessStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var h = await CancellationHarness.CreateAsync(
            NewFactory(new CancelOnCommitCtsInterceptor(commitCts, harnessStarted)), commitCts);

        try
        {
            // Push the Ready; the interceptor cancels the caller token AT the commit instant, so
            // the send never happens. A TCS fired by the interceptor makes the gate deterministic.
            var ready = h.PushReadyAsync();

            // The commit signal is the gate: the record's transaction has committed (and the token
            // has been cancelled) before we proceed to the stream-end observation.
            await harnessStarted.Task.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            await h.DrainAsync();
            await ready.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            Assert.True(h.StreamEnded, "the commit-time cancellation must end the stream");

            // NOTHING was published and NO blocked warning was emitted (a cancellation is not a
            // refusal), while no success log can exist either.
            Assert.Empty(h.Writer.Assignments);
            Assert.Empty(h.Writer.Messages);
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));

            // THE COMMITTED CONTEXT IS DELIBERATELY RETAINED — nothing was deleted or rebound.
            Assert.Equal(h.TaskId, RawAssignmentTaskId());
            var readback = h.Store.Load(h.TaskId);
            Assert.NotNull(readback);
            Assert.Equal(h.Worker.Id, readback!.Context.WorkerId);
            Assert.Equal(WorkerRole.Coder, readback.Context.Role);
            Assert.Equal(h.TaskId, readback.Context.Slot.TaskId);
            Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), readback.Context.Slot.Position);
            Assert.Equal("copilot/claude-sonnet-4.6", readback.Context.Model);
        }
        finally
        {
            if (!h.StreamEnded)
                await h.DrainAsync();
        }
    }

    // ───────────────────────────── fakes and helpers ─────────────────────────────

    /// <summary>Cancels the caller CTS the instant the insert's transaction COMMITS.</summary>
    private sealed class CancelOnCommitCtsInterceptor(CancellationTokenSource cts, TaskCompletionSource signal)
        : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            cts.Cancel();
            signal.TrySetResult();
        }
    }

    /// <summary>A factory handing out store-OWNED contexts on their own connections.</summary>
    private sealed class ReadyCancellationFactory(string connectionString, IInterceptor[] interceptors)
        : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly List<CopilotHiveDbContext> _contexts = [];

        public CopilotHiveDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connectionString);
            foreach (var interceptor in interceptors)
                builder.AddInterceptors(interceptor);
            var context = new CopilotHiveDbContext(builder.Options);
            lock (_contexts)
                _contexts.Add(context);
            return context;
        }

        public void Dispose()
        {
            List<CopilotHiveDbContext> contexts;
            lock (_contexts)
                contexts = [.. _contexts];
            foreach (var context in contexts)
            {
                try
                {
                    context.Dispose();
                }
                catch
                {
                    // Best-effort — a leftover context must never fail a test.
                }
            }
        }
    }

    /// <summary>A single-goal source for the real lifecycle service.</summary>
    private sealed class ReadyCancellationGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "ready-cancel-test-source";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>Records every logged message; signals the production blocked warning deterministically.</summary>
    private class CapturingReadyLogger : ILogger<HiveOrchestratorService>
    {
        private readonly List<string> _messages = [];

        // THE PENDING WAITER QUEUE: each waiter gets its OWN TaskCompletionSource, completed by the
        // NEXT matching log. A previously completed signal can therefore never satisfy a later wait,
        // so a second Ready is never "already done" before it has actually been processed.
        private readonly Queue<TaskCompletionSource> _blockedWaiters = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        /// <summary>
        /// Returns a FRESH task that completes when the NEXT blocked-disposition warning is emitted.
        /// Called BEFORE the Ready is pushed, so the signal can never be missed — and never reused.
        /// </summary>
        public Task WaitForBlockedWarning()
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_messages)
                _blockedWaiters.Enqueue(waiter);
            return waiter.Task;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            TaskCompletionSource? signal = null;
            lock (_messages)
            {
                _messages.Add(message);
                if (message.Contains("assignment blocked", StringComparison.Ordinal) && _blockedWaiters.Count > 0)
                    signal = _blockedWaiters.Dequeue();
            }

            signal?.TrySetResult();
        }
    }

    /// <summary>The delivery observation at the transport boundary, with an assignment-forwarded signal.</summary>
    private sealed class RecordingStreamWriter : IServerStreamWriter<OrchestratorMessage>
    {
        private readonly List<OrchestratorMessage> _messages = [];
        private readonly TaskCompletionSource _assignmentForwarded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriteOptions? WriteOptions { get; set; }

        public Task AssignmentForwarded => _assignmentForwarded.Task;

        public IReadOnlyList<OrchestratorMessage> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        public IReadOnlyList<TaskAssignment> Assignments =>
            [.. Messages.Where(m => m.Assignment is not null).Select(m => m.Assignment)];

        private Task RecordAsync(OrchestratorMessage message)
        {
            lock (_messages)
                _messages.Add(message);

            if (message.Assignment is not null)
                _assignmentForwarded.TrySetResult();

            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(OrchestratorMessage message) =>
            RecordAsync(message);

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(
            OrchestratorMessage message, CancellationToken cancellationToken) =>
            RecordAsync(message);
    }

    private sealed class ReadyCancellationStreamReader : IAsyncStreamReader<WorkerMessage>
    {
        private readonly System.Threading.Channels.Channel<WorkerMessage> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<WorkerMessage>(
                new System.Threading.Channels.UnboundedChannelOptions
                {
                    // THE CALLER-CANCELLATION OBSERVATION depends on the read loop seeing the
                    // cancelled token instead of waiting for the next item: ReadAllAsync must
                    // observe the cancelled stream token the moment it fires, not only when input
                    // completes.
                    SingleReader = true,
                });

        public WorkerMessage Current { get; private set; } = new();

        public void Push(WorkerMessage message) => _channel.Writer.TryWrite(message);

        public void Complete() => _channel.Writer.TryComplete();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_channel.Reader.TryRead(out var message))
                {
                    Current = message;
                    return true;
                }
            }

            return false;
        }
    }

    private static ServerCallContext CancellationCallContext(CancellationTokenSource? cts)
    {
        if (cts is null)
            return new Moq.Mock<ServerCallContext>().Object;

        // ServerCallContext.CancellationToken is a non-virtual property, so it cannot be mocked —
        // a real subclass that OVERRIDES the property is the only way to hand the stream a caller
        // token (the seam the stream's linked token source consumes).
        return new CancellationServerCallContext(cts.Token);
    }

    /// <summary>
    /// A <see cref="ServerCallContext"/> whose <see cref="ServerCallContext.CancellationToken"/> —
    /// which delegates to <see cref="ServerCallContext.CancellationTokenCore"/> — is the caller's
    /// token: the seam the stream's linked token source consumes.
    /// </summary>
    private sealed class CancellationServerCallContext(CancellationToken token) : ServerCallContext
    {
        protected override CancellationToken CancellationTokenCore => token;

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException("propagation is not used by the transport test");

        protected override string MethodCore => "/test.WorkOrchestrator/WorkStream";

        protected override string HostCore => "test-host";

        protected override string PeerCore => "test-peer";

        protected override DateTime DeadlineCore => DateTime.MaxValue;

        protected override Metadata RequestHeadersCore => [];

        protected override Metadata ResponseTrailersCore => [];

        protected override Status StatusCore { get; set; } = Status.DefaultSuccess;

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore => null!;
    }
}