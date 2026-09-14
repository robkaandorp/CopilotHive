using System.Data.Common;
using System.Reflection;

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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// THE READY-DRIVEN DELIVERY SLICE, end to end through the REAL
/// <see cref="HiveOrchestratorService.WorkStream"/> → <c>HandleWorkerReady</c> path with the
/// PRODUCTION <see cref="WorkerAssignmentPublisher"/> over a REAL file-backed SQLite database.
/// <para>
/// THESE ARE THE GOAL'S CORE VECTORS. They pin the ordering contract that is the whole point of the
/// slice: an assignment becomes available on the delivery transport ONLY AFTER its
/// assignment-context row is confirmed, and a refused record leaves the delivery blocked while the
/// pinned worker, its stream, the active task and the busy state all stay untouched.
/// </para>
/// <para>
/// WHERE THE OBSERVATION IS TAKEN, AND WHY. The transport's own channel pump forwards every
/// queued <see cref="OrchestratorMessage"/> to the server stream writer it was given, and the
/// stream's teardown closes the worker's message channel. The stream writer is therefore the ONE
/// place where "was an assignment delivered to this worker" is both observable and stable past
/// teardown — reading the channel itself after the stream ends would always look empty. Every
/// delivery assertion below is made there.
/// </para>
/// <para>
/// THE COLLABORATORS' OWN CONTRACTS ARE COVERED ELSEWHERE and are reused, not repeated:
/// <c>WorkerAssignmentContextStoreTests</c> owns the store's write/duplicate/conflict/uncertainty
/// matrix and <c>WorkerAssignmentPublisherTests</c> owns the publisher's focused outcome mapping.
/// Here the store and the publisher are the REAL collaborators of the real transport path.
/// </para>
/// </summary>
/// <remarks>
/// EVERY FIXTURE IS ISOLATED: a per-instance temporary database FILE, connection strings with
/// <c>Pooling=False</c> so disposing the factory really releases the file handles, a finite SQLite
/// busy timeout, DETERMINISTIC synchronization (the transport's own assignment-forwarded signal, the
/// production warning's own log signal, and the stream's completion — never a sleep), and best-effort
/// cleanup in <see cref="Dispose"/>.
/// </remarks>
public sealed class WorkerAssignmentPublicationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"copilothive-wap-ready-{Guid.NewGuid():N}.db");

    private readonly List<IDisposable> _fixtures = [];

    /// <summary>Upper bound for every await in these vectors — never a fixed delay.</summary>
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    public WorkerAssignmentPublicationTests()
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

    /// <summary>
    /// Builds the store factory for this fixture. When an <paramref name="observer"/> is supplied it
    /// is wired to THIS file and installed as a commit interceptor, so the observation really fires
    /// inside the production INSERT.
    /// </summary>
    private ReadyFactory NewFactory(CommitObservationInterceptor? observer = null, params IInterceptor[] interceptors)
    {
        var all = new List<IInterceptor>(interceptors);
        if (observer is not null)
        {
            observer.ProbeConnectionString = ConnectionString;
            all.Add(new CommitObservingTransactionInterceptor(observer));
        }

        var factory = new ReadyFactory(ConnectionString, [.. all]);
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

    // ═══════════════════════════════════════════════════════════════════════
    // (1) THE REAL READY PATH — record THEN publish, observed in that order
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE CORE ORDERING VECTOR. A real Ready message through a real
    /// <see cref="HiveOrchestratorService.WorkStream"/>, a real pipeline (routing + pointer + Pending
    /// slot), the PRODUCTION publisher and a real file-backed store.
    /// <para>
    /// It records the EXACT task/goal/worker/slot/role/model and delivers the MATCHING assignment;
    /// the commit-instant observer proves the row was confirmed while NO assignment had yet reached
    /// the transport, so no Assignment can be available before the record. A fresh-context readback
    /// reproduces the row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_RecordsExactContextThenPublishesMatchingAssignment()
    {
        var observer = new CommitObservationInterceptor();
        var h = await ReadyHarness.CreateAsync(NewFactory(observer), observer);

        try
        {
            Assert.Null(await h.SendReadyAndAwaitPublishedAsync());

            // ── THE COMMIT-INSTANT OBSERVATION: the row was confirmed while nothing had been sent. ──
            var observed = Assert.Single(observer.Observations);
            Assert.Equal(h.TaskId, observed.RowTaskIdAtCommit);
            Assert.True(
                observed.RowPresentAtCommit,
                "the assignment-context row was NOT present at the commit instant — the observation " +
                "did not land inside the record");
            Assert.False(
                observed.AssignmentAvailableAtCommit,
                "an assignment was already on its way to the worker AT THE COMMIT INSTANT — the record " +
                "must be confirmed before anything is published");
            Assert.True(
                CommitObservationInterceptor.HasConfirmedRowBeforeAssignment(
                    observed.AssignmentAvailableAtCommit, observed.RowPresentAtCommit),
                "the row-before-publication invariant was violated at the commit instant");

            // ── THE DELIVERY: exactly one assignment, naming the delivered task. ──
            var published = Assert.Single(h.Writer.Assignments);
            Assert.Equal(h.TaskId, published.TaskId);
            Assert.Equal(h.GoalId, published.GoalId);
            Assert.Equal("copilot/claude-sonnet-4.6", published.Model);
            Assert.Equal(h.Prompt, published.Prompt);
            Assert.Single(h.Writer.Messages);

            // ── NO RECORDING REFUSAL WAS LOGGED: this delivery really was recorded and sent. ──
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));

            // ── THE FRESH-CONTEXT READBACK reproduces every delivered value (observation only). ──
            var readback = new WorkerAssignmentContextStore(
                NewFactory(), NullLogger<WorkerAssignmentContextStore>.Instance).Load(h.TaskId);
            Assert.NotNull(readback);
            Assert.Equal(h.GoalId, readback!.Context.GoalId);
            Assert.Equal(h.Worker.Id, readback.Context.WorkerId);
            Assert.Equal(WorkerRole.Coder, readback.Context.Role);
            Assert.Equal(h.TaskId, readback.Context.Slot.TaskId);
            Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), readback.Context.Slot.Position);
            Assert.Equal(1, readback.Context.Slot.Attempt);
            Assert.Equal("copilot/claude-sonnet-4.6", readback.Context.Model);
        }
        finally
        {
            await h.StopAsync();
        }
    }

    /// <summary>
    /// THE DELIBERATELY UNGATED CONTROL, REJECTED BY THE SAME OBSERVER — the load-bearing proof that
    /// the ordering assertion is not vacuous.
    /// <para>
    /// The control publisher delivers an assignment and NEVER records. It really publishes (asserted
    /// first), the observer records NO commit instant at all, and no row exists — so applying the
    /// observer's invariant to those observed facts (<c>assignment available</c> + <c>no confirmed
    /// row</c>) yields a REJECTION. A publisher that skipped the record therefore cannot pass this
    /// suite. No source mutation is required.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_UngatedControl_IsRejectedByTheRowBeforePublicationObserver()
    {
        var observer = new CommitObservationInterceptor();
        var h = await ReadyHarness.CreateAsync(
            NewFactory(observer), observer, publisher: new UngatedRecordingSkippingPublisher());

        try
        {
            Assert.Null(await h.SendReadyAndAwaitPublishedAsync());

            // THE CONTROL REALLY DELIVERED — the rejection below cannot pass vacuously.
            var published = Assert.Single(h.Writer.Assignments);
            Assert.Equal(h.TaskId, published.TaskId);

            // ── THE SAME OBSERVER REJECTS IT, using the identical invariant. ──
            Assert.Empty(observer.Observations);
            Assert.Null(RawAssignmentTaskId());

            var assignmentAvailable = h.Writer.Assignments.Count > 0;
            var rowPresent = RawAssignmentTaskId() is not null;
            Assert.False(
                CommitObservationInterceptor.HasConfirmedRowBeforeAssignment(assignmentAvailable, rowPresent),
                "the ungated control delivered an assignment with NO confirmed row, yet the " +
                "row-before-publication invariant accepted it");
        }
        finally
        {
            await h.StopAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) THE REAL RECORDING REFUSAL — blocked, but nothing unwound
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A REAL Ready recording refusal through the REAL stream: the delivery is BLOCKED — no
    /// assignment, no success log — while the pinned worker, the stream, the active task and the busy
    /// assignment all stay INTACT. An unrelated pipeline is untouched and the task is NOT requeued.
    /// <para>
    /// The refusal is GENUINE (a seeded differing context → <c>Conflict</c>), so the production
    /// decision path is exercised rather than a stub's.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_RecordingRefusal_LeavesWorkerStreamAndAssignmentIntact()
    {
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null);

        try
        {
            // THE GENUINE REFUSAL: a DIFFERENT worker already owns this task id's recorded context.
            h.SeedConflictingRecord();

            // A SECOND, independent pipeline that must be untouched by the refusal.
            var otherPipeline = h.Manager.CreatePipeline(
                new Goal { Id = $"goal-other-{Guid.NewGuid():N}", Description = "unrelated goal" });

            await h.SendReadyAndAwaitBlockedAsync();

            // ── THE HANDLED DISPOSITION RETURNED NORMALLY: the stream did not fault. ──
            Assert.False(
                h.StreamFaulted,
                $"the handled recording refusal must not fault the stream: {h.Logger.Messages.Count} log(s)");

            // ── NOTHING WAS DELIVERED to the worker. ──
            Assert.Empty(h.Writer.Assignments);
            Assert.Empty(h.Writer.Messages);

            // ── NO SUCCESS LOG; the actionable disposition warning is present instead. ──
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));
            Assert.Contains(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));

            // ── THE STREAM AND THE WORKER SURVIVE: still in the pool, still busy on the task. ──
            Assert.Same(h.Worker, h.Pool.GetWorker(h.Worker.Id));
            Assert.True(h.Worker.IsBusy, "the blocked delivery must keep the worker's busy state");
            Assert.Equal(h.TaskId, h.Worker.CurrentTaskId);
            Assert.Equal(WorkerRole.Coder, h.Worker.Role);

            // ── THE ACTIVE TASK IS RETAINED on its own pipeline… ──
            Assert.Equal(h.TaskId, h.Pipeline.ActiveTaskId);

            // ── …and the UNRELATED pipeline is completely unaffected. ──
            Assert.Equal(GoalPhase.Planning, otherPipeline.Phase);
            Assert.Null(otherPipeline.ActiveTaskId);

            // ── NO REQUEUE: the blocked task was not put back for another worker. ──
            Assert.Null(h.Queue.TryDequeueAny());
        }
        finally
        {
            await h.StopAsync();
        }
    }

    /// <summary>
    /// THE EXISTING EXPLICIT CANCELLATION PATH STILL RELEASES A BLOCKED ASSIGNMENT, with the task
    /// timeout policy DISABLED — no fabricated completion, no automatic recovery.
    /// <para>
    /// This is the operator's real escape hatch: <c>GoalDispatcher.CancelGoalAsync</c> fails the goal
    /// and removes its pipeline. The test proves the blocked delivery is genuinely still HELD first,
    /// then that cancellation works, then that the route is gone so a further Ready delivers nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task BlockedReadyAssignment_ExistingGoalCancellation_RemainsUsable()
    {
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null);

        try
        {
            // ── THE TIMEOUT POLICY IS DISABLED: 0 minutes, so no automatic reclaim exists. ──
            Assert.Equal(0, h.Config.Orchestrator.WorkerTaskTimeoutMinutes);

            h.SeedConflictingRecord();
            await h.SendReadyAndAwaitBlockedAsync();

            // The blocked delivery is genuinely HELD: still active, still busy, nothing delivered.
            Assert.Equal(h.TaskId, h.Pipeline.ActiveTaskId);
            Assert.True(h.Worker.IsBusy);
            Assert.Empty(h.Writer.Assignments);

            // ── THE EXISTING EXPLICIT CANCELLATION PATH. ──
            Assert.True(await h.Dispatcher.CancelGoalAsync(h.GoalId, TestContext.Current.CancellationToken));

            // It failed the goal and deregistered the pipeline, in memory and for the routing lookup.
            Assert.Equal(GoalPhase.Failed, h.Pipeline.Phase);
            Assert.Null(h.Manager.GetByTaskId(h.TaskId));
            Assert.Null(h.Manager.GetByGoalId(h.GoalId));

            // ── THE ROUTE IS NOW UNRESOLVABLE: a further Ready delivers NOTHING — no fabricated
            //    recovery, no wrong-goal failure, and still no success log. ──
            h.Queue.Enqueue(h.DeliveredTask);
            await h.SendReadyAndAwaitBlockedAsync();
            Assert.Empty(h.Writer.Assignments);
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));
        }
        finally
        {
            await h.StopAsync();
        }
    }

    /// <summary>
    /// MISSING PUBLISHER FAILS CLOSED ON THE REAL PATH. A production transport service constructed
    /// WITHOUT a publisher (the unrelated-fixture shape) produces the SAME explicit no-send
    /// disposition — and NEVER the old raw write, which was removed.
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_MissingPublisher_NoSendNoRawWriteFallback()
    {
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null, withPublisher: false);

        try
        {
            await h.SendReadyAndAwaitBlockedAsync();

            // NOTHING reached the transport — a raw-write fallback would have delivered an assignment.
            Assert.Empty(h.Writer.Messages);
            Assert.Empty(h.Writer.Assignments);
            Assert.False(
                h.Worker.MessageChannel.Reader.TryRead(out _),
                "an assignment reached the channel without a publisher — the old raw write is back");

            Assert.Contains(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));

            // The delivery is still held, not released.
            Assert.Equal(h.TaskId, h.Pipeline.ActiveTaskId);
            Assert.True(h.Worker.IsBusy);
        }
        finally
        {
            await h.StopAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) THE WARNING CONTRACT — exact wording, and guarded emission
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE EXACT DISPOSITION WORDING, emitted as a WARNING, and the GUARD around it: a logger that
    /// throws while emitting the warning must not escape — the handled refusal still returns normally
    /// and nothing is delivered.
    /// </summary>
    [Fact]
    public async Task RecordingRefusal_Warning_UsesExactWording_AndIsGuardedAgainstAThrowingLogger()
    {
        var throwingLogger = new ThrowingOnWarningReadyLogger();
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null, logger: throwingLogger);

        try
        {
            h.SeedConflictingRecord();

            // MUST NOT THROW: the guarded warning swallows the logger failure, and the disposition
            // (blocked, retained) stands.
            await h.SendReadyAndAwaitBlockedAsync();

            Assert.Empty(h.Writer.Assignments);
            Assert.True(h.Worker.IsBusy);
            Assert.Equal(h.TaskId, h.Pipeline.ActiveTaskId);
        }
        finally
        {
            await h.StopAsync();
        }

        // THE EXACT WORDING, asserted against the message the throwing logger actually received.
        var warning = Assert.Single(
            throwingLogger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
        Assert.Contains(
            "assignment blocked; no assignment published; task retained; cancel the goal or use " +
            "configured recovery",
            warning,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Assignment published", warning, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) A POST-RECORD SEND FAULT KEEPS ITS EXISTING TEARDOWN SEMANTICS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A POST-RECORD CHANNEL FAULT IS NOT A RECORDING REFUSAL: the channel-closed fault is
    /// deliberately outside the handler's catch, so the stream terminates exactly as it did before
    /// this slice — while the row stays recorded and no success log is emitted.
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_PostRecordChannelFault_PropagatesAndKeepsRow()
    {
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null);

        try
        {
            // Complete the channel so the POST-RECORD write fails.
            Assert.True(h.Worker.MessageChannel.Writer.TryComplete());

            var streamFault = await h.SendReadyAndAwaitStreamEndAsync();

            // The fault propagated UNCHANGED — never re-labelled as a recording refusal, and not
            // reported as the handled blocked disposition.
            Assert.IsType<System.Threading.Channels.ChannelClosedException>(streamFault);
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));

            // The row WAS recorded (the record precedes the send) and no success log happened.
            Assert.Equal(h.TaskId, RawAssignmentTaskId());
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));
        }
        finally
        {
            await h.StopAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  harness
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REAL TRANSPORT HARNESS: a live <see cref="HiveOrchestratorService"/> over a REAL
    /// <see cref="GoalDispatcher"/>, a real <see cref="GoalPipelineManager"/>, real
    /// <see cref="WorkerPool"/> / <see cref="TaskQueue"/>, the PRODUCTION
    /// <see cref="WorkerAssignmentPublisher"/> over the fixture's file-backed store, and ONE pipeline
    /// whose routing, pointer and Pending slot are ALL genuinely registered.
    /// </summary>
    private sealed class ReadyHarness
    {
        public required HiveOrchestratorService Service { get; init; }
        public required WorkerAssignmentPublisher Publisher { get; init; }
        public required GoalDispatcher Dispatcher { get; init; }
        public required GoalPipelineManager Manager { get; init; }
        public required GoalPipeline Pipeline { get; init; }
        public required WorkerPool Pool { get; init; }
        public required TaskQueue Queue { get; init; }
        public required ConnectedWorker Worker { get; init; }
        public required WorkTask DeliveredTask { get; init; }
        public required string GoalId { get; init; }
        public required string TaskId { get; init; }
        public required string Prompt { get; init; }
        public required HiveConfigFile Config { get; init; }
        public required WorkerAssignmentContextStore Store { get; init; }
        public required CapturingReadyLogger Logger { get; init; }

        /// <summary>THE DELIVERY OBSERVATION: everything the transport forwarded to the worker.</summary>
        public required RecordingStreamWriter Writer { get; init; }

        private ChannelStreamReader Reader { get; init; } = null!;
        private Task StreamTask { get; init; } = null!;

        /// <summary>Whether the stream terminated with an exception rather than draining.</summary>
        public bool StreamFaulted { get; private set; }

        /// <summary>
        /// Builds the harness. Every collaborator is REAL except the publisher seam, which is
        /// injectable so the missing-publisher and ungated-control vectors can vary it.
        /// </summary>
        public static async Task<ReadyHarness> CreateAsync(
            IDbContextFactory<CopilotHiveDbContext> storeFactory,
            CommitObservationInterceptor? observer,
            CapturingReadyLogger? logger = null,
            IWorkerAssignmentPublisher? publisher = null,
            bool withPublisher = true)
        {
            var pool = new WorkerPool();
            var queue = new TaskQueue();
            var manager = new GoalPipelineManager();

            var goal = new Goal
            {
                Id = $"goal-ready-{Guid.NewGuid():N}",
                Description = "Ready publication goal",
                RepositoryNames = ["test-repo"],
            };

            var goalManager = new GoalManager();
            goalManager.AddSource(new ReadyGoalSource(goal));
            await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

            // THE TIMEOUT POLICY IS DISABLED: 0 minutes makes the timed-out-task reclaim an immediate
            // no-op, so nothing here can automatically release a blocked assignment.
            var config = new HiveConfigFile
            {
                Orchestrator = new OrchestratorConfig { WorkerTaskTimeoutMinutes = 0 },
            };

            var completionNotifier = new TaskCompletionNotifier();
            var dispatcher = new GoalDispatcher(
                goalManager,
                manager,
                queue,
                new GrpcWorkerGateway(pool),
                completionNotifier,
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
                config: config,
                dashboardNotifier: new DashboardNotifier());

            var store = new WorkerAssignmentContextStore(
                storeFactory, NullLogger<WorkerAssignmentContextStore>.Instance);
            var realPublisher = new WorkerAssignmentPublisher(manager, pool, store);
            var readyLogger = logger ?? new CapturingReadyLogger();

            var service = new HiveOrchestratorService(
                pool,
                queue,
                manager,
                completionNotifier,
                dispatcher,
                readyLogger,
                dashboardNotifier: new DashboardNotifier(),
                assignmentPublisher: withPublisher ? publisher ?? realPublisher : null);

            // THE GENUINE OWNERSHIP: routing + pointer + Pending slot, ALL really registered.
            const string prompt = "do the ready work";
            var pipeline = manager.CreatePipeline(goal);
            pipeline.AdvanceTo(GoalPhase.Coding);
            var built = pipeline.AllocateAttemptAndRegisterSlot(
                $"task-ready-{Guid.NewGuid():N}", new WorkSlotPosition(1, GoalPhase.Coding, 1));
            pipeline.SetActiveTask(built.TaskId);
            manager.RegisterTask(built.TaskId, goal.Id);

            var worker = pool.RegisterWorker($"worker-ready-{Guid.NewGuid():N}", []);

            // THE DELIVERED TASK — the ACTUAL instance the queue's own dequeue path hands over.
            var task = new WorkTask
            {
                TaskId = built.TaskId,
                GoalId = goal.Id,
                GoalDescription = goal.Description,
                Prompt = prompt,
                Role = WorkerRole.Coder,
                Model = "copilot/claude-sonnet-4.6",
                Repositories =
                    [new TargetRepository { Name = "test-repo", Url = "https://example.invalid/repo" }],
            };
            queue.Enqueue(task);

            var writer = new RecordingStreamWriter();
            var reader = new ChannelStreamReader();
            var streamTask = service.WorkStream(reader, writer, MockContext());

            // The commit-instant probe consults BOTH the still-queued channel entry AND everything
            // already forwarded, so a premature publish cannot slip past the observation.
            if (observer is not null)
            {
                observer.AssignmentProbe = () =>
                    worker.MessageChannel.Reader.TryPeek(out _) || writer.Assignments.Count > 0;
            }

            return new ReadyHarness
            {
                Service = service,
                Publisher = realPublisher,
                Dispatcher = dispatcher,
                Manager = manager,
                Pipeline = pipeline,
                Pool = pool,
                Queue = queue,
                Worker = worker,
                DeliveredTask = task,
                GoalId = goal.Id,
                TaskId = built.TaskId,
                Prompt = prompt,
                Config = config,
                Store = store,
                Logger = readyLogger,
                Writer = writer,
                Reader = reader,
                StreamTask = streamTask,
            };
        }

        /// <summary>Seeds a DIFFERENT context for the delivered task id — the genuine Conflict refusal.</summary>
        public void SeedConflictingRecord()
        {
            var slot = Pipeline.GetSlotsForTest().Single(v => v.Slot.TaskId == TaskId).Slot;
            var seeded = new WorkerAssignmentContext(
                GoalId, "worker-seeded-elsewhere", WorkerRole.Coder, slot, "model-seeded");
            Assert.Equal(WorkerAssignmentWriteStatus.Recorded, Store.InsertOnce(seeded).Status);
        }

        /// <summary>
        /// Pushes a real Ready and waits until the transport has FORWARDED an assignment — the
        /// deterministic signal that the delivery completed, taken from the transport itself.
        /// </summary>
        public async Task<Exception?> SendReadyAndAwaitPublishedAsync()
        {
            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });
            await Writer.AssignmentForwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            return null;
        }

        /// <summary>
        /// Pushes a real Ready and waits until the PRODUCTION WARNING has been emitted — the
        /// deterministic signal that the handled refusal path ran (it emits exactly one warning).
        /// </summary>
        public async Task SendReadyAndAwaitBlockedAsync()
        {
            var signal = Logger.WaitForBlockedWarning();
            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>Pushes a real Ready and waits for the stream to end, returning its fault (or null).</summary>
        public async Task<Exception?> SendReadyAndAwaitStreamEndAsync()
        {
            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });
            Reader.Complete();

            try
            {
                await StreamTask.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
                StreamFaulted = false;
                return null;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("the WorkStream did not drain within the bound");
            }
            catch (Exception ex)
            {
                // The stream's own termination is the observed result for the fault vector.
                StreamFaulted = true;
                return ex;
            }
        }

        public async Task StopAsync()
        {
            // End the stream, then observe (never rethrow) its termination so a failing assertion is
            // never replaced by a teardown fault.
            Reader.Complete();
            try
            {
                await StreamTask.WaitAsync(BoundedWait, CancellationToken.None);
            }
            catch (Exception)
            {
                StreamFaulted = true;
            }
        }
    }

    // ───────────────────────────── fakes and helpers ─────────────────────────────

    /// <summary>
    /// THE ROW-BEFORE-PUBLICATION OBSERVER: fired the instant a transaction COMMITS, it records
    /// whether the assignment-context row is durable and whether an assignment has already reached
    /// the transport AT THAT INSTANT.
    /// <para>
    /// EF raises the committed notification only AFTER the provider's commit returned, so the fresh
    /// connection used here really does see the committed row — which is what makes this an honest
    /// instrument rather than a guess. An observation is recorded only when the row exists, so a
    /// publisher that skips the record produces NO observation and is rejected by the invariant.
    /// </para>
    /// </summary>
    private sealed class CommitObservationInterceptor : IInterceptor
    {
        private readonly List<CommitObservation> _observations = [];

        /// <summary>The connection string of the same database the store writes to.</summary>
        public string ProbeConnectionString { get; set; } = "";

        /// <summary>Reports whether anything has already been delivered to the worker.</summary>
        public Func<bool>? AssignmentProbe { get; set; }

        /// <summary>The observations, in commit order; empty when no assignment row was committed.</summary>
        public IReadOnlyList<CommitObservation> Observations
        {
            get
            {
                lock (_observations)
                    return [.. _observations];
            }
        }

        /// <summary>
        /// THE INVARIANT this observer exists to enforce: an assignment may be available ONLY together
        /// with (or after) a confirmed row — "assignment available" IMPLIES "row confirmed".
        /// </summary>
        /// <param name="assignmentAvailable">Whether an assignment has reached the transport.</param>
        /// <param name="rowPresent">Whether the assignment-context row is durable.</param>
        /// <returns><c>true</c> when the facts are consistent with the ordering contract.</returns>
        public static bool HasConfirmedRowBeforeAssignment(bool assignmentAvailable, bool rowPresent) =>
            !assignmentAvailable || rowPresent;

        /// <summary>Records one commit-instant observation; called by the forwarding interceptor.</summary>
        public void Committed()
        {
            var rowTaskId = ProbeRowTaskId();
            if (rowTaskId is null)
                return;   // no assignment row was committed — nothing to observe

            var assignmentAvailable = AssignmentProbe?.Invoke() ?? false;
            lock (_observations)
                _observations.Add(new CommitObservation(rowTaskId, assignmentAvailable));
        }

        private string? ProbeRowTaskId()
        {
            if (string.IsNullOrEmpty(ProbeConnectionString))
                return null;

            try
            {
                using var connection = new SqliteConnection(ProbeConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT task_id FROM worker_assignment_contexts LIMIT 1";
                return command.ExecuteScalar() as string;
            }
            catch
            {
                // A probe failure must never mask the test's own outcome.
                return null;
            }
        }
    }

    /// <summary>
    /// The EF interceptor that forwards COMMITS to the shared observer. Kept separate so the observer
    /// stays a plain, testable object while EF only ever sees a normal interceptor.
    /// </summary>
    private sealed class CommitObservingTransactionInterceptor(CommitObservationInterceptor observer)
        : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            observer.Committed();
    }

    /// <summary>One commit-instant observation: the confirmed row and the transport state.</summary>
    private sealed record CommitObservation(string RowTaskIdAtCommit, bool AssignmentAvailableAtCommit)
    {
        /// <summary>Whether the assignment-context row was durable at this instant. Always <c>true</c>.</summary>
        public bool RowPresentAtCommit => true;
    }

    /// <summary>A factory handing out store-OWNED contexts on their own connections, disposed with the fixture.</summary>
    private sealed class ReadyFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly string _connectionString;
        private readonly IInterceptor[] _interceptors;
        private readonly List<CopilotHiveDbContext> _contexts = [];

        public ReadyFactory(string connectionString, IInterceptor[] interceptors)
        {
            _connectionString = connectionString;
            _interceptors = interceptors;
        }

        public CopilotHiveDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_connectionString);
            foreach (var interceptor in _interceptors)
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
                    // Best-effort.
                }
            }
        }
    }

    /// <summary>A publisher that delivers WITHOUT recording — the deliberately UNGATED control.</summary>
    private sealed class UngatedRecordingSkippingPublisher : IWorkerAssignmentPublisher
    {
        public async Task PublishAsync(ConnectedWorker worker, WorkTask task, CancellationToken cancellationToken) =>
            await worker.MessageChannel.Writer.WriteAsync(
                new OrchestratorMessage { Assignment = GrpcMapper.ToGrpc(task) }, cancellationToken);
    }

    /// <summary>
    /// THE DELIVERY OBSERVATION at the transport boundary: records every message the transport
    /// forwards, and signals deterministically the moment an assignment is forwarded.
    /// </summary>
    private sealed class RecordingStreamWriter : IServerStreamWriter<OrchestratorMessage>
    {
        private readonly List<OrchestratorMessage> _messages = [];
        private readonly TaskCompletionSource _assignmentForwarded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriteOptions? WriteOptions { get; set; }

        /// <summary>Completes when the first assignment has been forwarded to this worker.</summary>
        public Task AssignmentForwarded => _assignmentForwarded.Task;

        public IReadOnlyList<OrchestratorMessage> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        /// <summary>Every forwarded assignment, in order.</summary>
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

    /// <summary>Records every logged message, and signals the production warning deterministically.</summary>
    private class CapturingReadyLogger : ILogger<HiveOrchestratorService>
    {
        private readonly List<string> _messages = [];
        private TaskCompletionSource? _blockedSignal;

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        /// <summary>
        /// Returns a task that completes when the blocked disposition warning is emitted. Called
        /// BEFORE the Ready is pushed, so the signal can never be missed.
        /// </summary>
        public Task WaitForBlockedWarning()
        {
            lock (_messages)
            {
                _blockedSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _blockedSignal.Task;
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public virtual void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            TaskCompletionSource? signal = null;
            lock (_messages)
            {
                _messages.Add(message);
                if (message.Contains("assignment blocked", StringComparison.Ordinal))
                    signal = _blockedSignal;
            }

            signal?.TrySetResult();
        }
    }

    /// <summary>A logger that throws AFTER recording a warning — the guarded-warning vector.</summary>
    private sealed class ThrowingOnWarningReadyLogger : CapturingReadyLogger
    {
        public override void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            base.Log(logLevel, eventId, state, exception, formatter);
            if (logLevel == LogLevel.Warning)
                throw new InvalidOperationException("the logger threw while emitting the warning");
        }
    }

    /// <summary>A single-goal source so the real lifecycle service can persist status updates.</summary>
    private sealed class ReadyGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "ready-test-source";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ChannelStreamReader : IAsyncStreamReader<WorkerMessage>
    {
        private readonly System.Threading.Channels.Channel<WorkerMessage> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<WorkerMessage>();

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

    private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;
}
