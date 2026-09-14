using System.Data.Common;
using System.Reflection;
using System.Threading.Channels;

using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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