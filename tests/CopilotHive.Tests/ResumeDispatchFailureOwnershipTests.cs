using System.Collections.Concurrent;
using System.Reflection;

using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// THE RESUME DISPATCH-FAILURE DISPOSITION: <see cref="GoalDispatcher.ResumeGoalAsync"/>'s initial
/// resumed-dispatch catch must NEVER destroy a SURVIVING active pointer, and must add NO redispatch
/// while the observed pointer is non-null.
/// </summary>
/// <remarks>
/// <para>
/// EVERY VECTOR HERE ENTERS THROUGH THE REAL BOUNDARY: <see cref="GoalDispatcher.ResumeGoalAsync"/>
/// → <see cref="TaskDispatchService.DispatchToRole"/> → the REAL
/// <see cref="GrpcWorkerGateway"/> → the REAL <see cref="WorkerPool"/>, with the REAL
/// <see cref="WorkerAssignmentPublisher"/> and the REAL
/// <see cref="WorkerAssignmentContextStore"/> over an in-memory SQLite database (the shared
/// <see cref="EagerAssignmentRecording"/> wiring). The delivered assignment is therefore an
/// observable ROW plus an observable CHANNEL message, never a "send happened" flag.
/// </para>
/// <para>
/// THE DISCRIMINATION IS THE POINTER. The pre-fix catch cleared <c>ActiveTaskId</c> unconditionally
/// and enqueued the goal; every retained-ownership assertion below (verbatim pointer, retained
/// mapping, Pending slot, retained counters and active queue entry, empty redispatch queue) fails
/// on that code.
/// </para>
/// <para>
/// NO sleeps: every milestone is deterministic (a completed channel, a decorator that throws after
/// the real publish, an enqueue hook that arms the selection fault).
/// </para>
/// </remarks>
public sealed class ResumeDispatchFailureOwnershipTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    // ═══════════════════════════ (1) the post-record channel-write fault ═══════════════════════

    /// <summary>
    /// A POST-RECORD CHANNEL-WRITE FAULT on the resumed dispatch: the real publisher records the
    /// assignment row, then its channel write fails because the pinned worker's channel is already
    /// completed. The pointer, the mapping, the Pending slot, the attempt counter, the active
    /// worker/queue entry and the recorded assignment row are ALL retained, and NO redispatch is
    /// added.
    /// </summary>
    [Fact]
    public async Task ResumeDispatch_PostRecordChannelFault_RetainsOwnershipAndAddsNoRedispatch()
    {
        using var fixture = new ResumeOwnershipFixture();
        const string goalId = "resume-own-channel-fault";
        var goal = NewFailedGoal(goalId, "Review rejected the changes");
        fixture.GoalStore.AddGoal(goal);
        var pipeline = NewFailedPipeline(fixture.Manager, goal, branchBacked: true);

        // THE PINNED WORKER, selected by the real pool and then made UNWRITABLE post-record.
        var worker = fixture.Pool.RegisterWorker("worker-resume-fault", []);

        var dispatcher = fixture.BuildDispatcher(publisherOverride: null);
        dispatcher.BranchListerForTest = (_, _) => Task.FromResult(new List<string>());

        worker.MessageChannel.Writer.TryComplete();

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);

        // THE SURVIVING OWNERSHIP IS PRESERVED VERBATIM.
        var activeTaskId = pipeline.ActiveTaskId;
        Assert.NotNull(activeTaskId);
        Assert.Equal(activeTaskId, worker.CurrentTaskId);
        Assert.Equal(activeTaskId, Assert.Single(pipeline.GetSlotsForTest()).Slot.TaskId);
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipeline.GetSlotsForTest()).State);
        Assert.Same(pipeline, fixture.Manager.GetByTaskId(activeTaskId!));

        // The counters, the active worker and the active queue entry are retained.
        var attempt = Assert.Single(pipeline.CaptureRegistry().DispatchAttempts);
        Assert.Equal(1, attempt.HighWaterAttempt);
        Assert.Equal(GoalPhase.Coding, attempt.Position.Phase);
        Assert.True(worker.IsBusy);
        Assert.NotNull(fixture.Queue.GetActiveTask(activeTaskId!));
        Assert.Null(fixture.Queue.TryDequeueAny());

        // THE RECORDED ASSIGNMENT ROW SURVIVES the post-record fault, naming the pinned worker.
        var row = fixture.Recording.Store.Load(activeTaskId!);
        Assert.NotNull(row);
        Assert.Equal(goalId, row!.Context.GoalId);
        Assert.Equal(worker.Id, row.Context.WorkerId);
        Assert.Equal(WorkerRole.Coder, row.Context.Role);

        // NO REDISPATCH: a non-null observed pointer never queues.
        Assert.Empty(QueueEntries(dispatcher));

        // THE DISPOSITION RECORD describes the RETAINED ownership and carries the ORIGINAL fault.
        var disposition = Assert.Single(RetentionEntries(fixture.DispatcherLogger));
        Assert.Contains(goalId, disposition.Message, StringComparison.Ordinal);
        Assert.Contains(activeTaskId!, disposition.Message, StringComparison.Ordinal);
        Assert.IsType<System.Threading.Channels.ChannelClosedException>(disposition.Exception);

        // THE FRESH-STORE READBACK: the final pointer is retained durably.
        Assert.Equal(activeTaskId, fixture.ReadPersistedPointer(goalId));
    }

    // ═══════════════ (2) the decorator: real publish observed, then an exact sentinel ═══════════════

    /// <summary>
    /// THE REAL PUBLICATION IS OBSERVED, THEN THE EXACT SENTINEL IS THROWN. A test-only publisher
    /// decorator awaits the REAL successful publish (recording the row AND writing the channel
    /// message) and only then throws its own exact sentinel — so the fixture sees the ACTUAL single
    /// <c>Assignment</c> message and the exact forwarded instances, not a fake "send happened" flag.
    /// The surviving ownership is retained and no redispatch is added.
    /// </summary>
    /// <remarks>
    /// THE OWNERSHIP SNAPSHOT IS TAKEN INSIDE THE THROWN DELIVERY — on the production stack frame
    /// after the record and the channel write — and compared with the state after
    /// <c>ResumeGoalAsync</c> returns. Any catch-side attempt, phase-log entry, budget consume or
    /// pointer/registry mutation would break that comparison.
    /// </remarks>
    [Fact]
    public async Task ResumeDispatch_FaultAfterRealPublish_ObservesSingleAssignmentAndRetainsOwnership()
    {
        var sentinel = new ResumeDispatchSentinelException("resume-post-publish-sentinel");

        using var fixture = new ResumeOwnershipFixture();
        const string goalId = "resume-own-post-publish";
        var goal = NewFailedGoal(goalId, "Review rejected the changes");
        fixture.GoalStore.AddGoal(goal);
        var pipeline = NewFailedPipeline(fixture.Manager, goal, branchBacked: true);
        var worker = fixture.Pool.RegisterWorker("worker-resume-publish", []);

        var decorator = new ThrowAfterRealPublish(fixture.Recording.Publisher, sentinel);
        var dispatcher = fixture.BuildDispatcher(publisherOverride: decorator);
        dispatcher.BranchListerForTest = (_, _) => Task.FromResult(new List<string>());

        OwnershipObservation? observed = null;
        decorator.AfterRealPublish = () => observed = OwnershipObservation.Capture(pipeline);

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);

        // THE EXACT SENTINEL REALLY LEFT THE DECORATOR — the throw is not a stand-in for the publish.
        Assert.Same(sentinel, decorator.ThrownException);
        var published = Assert.Single(decorator.Published);
        Assert.Same(worker, published.Worker);

        // THE ACTUAL SINGLE ASSIGNMENT REACHED THE PINNED WORKER'S CHANNEL.
        var messages = DrainMessages(worker);
        var assignment = Assert.Single(messages);
        Assert.NotNull(assignment.Assignment);
        var deliveredTaskId = assignment.Assignment.TaskId;
        Assert.Equal(published.Task.TaskId, deliveredTaskId);

        // THE RETAINED OWNERSHIP NAMES EXACTLY THAT DELIVERED TASK.
        var activeTaskId = pipeline.ActiveTaskId;
        Assert.Equal(deliveredTaskId, activeTaskId);
        Assert.Equal(deliveredTaskId, worker.CurrentTaskId);
        Assert.True(worker.IsBusy);
        Assert.Same(pipeline, fixture.Manager.GetByTaskId(deliveredTaskId));
        Assert.NotNull(fixture.Queue.GetActiveTask(deliveredTaskId));
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipeline.GetSlotsForTest()).State);

        // THE RECORDED ROW IS THE DELIVERED ASSIGNMENT'S OWN CONTEXT.
        var row = fixture.Recording.Store.Load(deliveredTaskId);
        Assert.NotNull(row);
        Assert.Equal(goalId, row!.Context.GoalId);
        Assert.Equal(worker.Id, row.Context.WorkerId);
        Assert.Equal(WorkerRole.Coder, row.Context.Role);

        // NO REDISPATCH.
        Assert.Empty(QueueEntries(dispatcher));

        // THE DISPOSITION RECORD carries the ORIGINAL sentinel instance verbatim.
        var disposition = Assert.Single(RetentionEntries(fixture.DispatcherLogger));
        Assert.Contains("active ownership is RETAINED", disposition.Message, StringComparison.Ordinal);
        Assert.Contains("NOT enqueued for redispatch", disposition.Message, StringComparison.Ordinal);
        Assert.Same(sentinel, disposition.Exception);

        // NO ATTEMPT, PHASE-LOG OR BUDGET MUTATION BY THE CATCH — compared against the snapshot
        // taken on the thrown delivery's own stack frame.
        Assert.NotNull(observed);
        OwnershipObservation.AssertUnchanged(observed!, OwnershipObservation.Capture(pipeline));

        // FRESH-STORE READBACK.
        Assert.Equal(deliveredTaskId, fixture.ReadPersistedPointer(goalId));
    }

    // ══════════════ (3) admitted-but-pending: the failure lands at worker selection ══════════════

    /// <summary>
    /// THE ADMITTED-BUT-PENDING SHAPE: the resumed dispatch admits its task (pointer claimed, slot
    /// registered, mapping committed, task enqueued) and only THEN fails, at the delivery's worker
    /// SELECTION stage. The work is genuinely pending rather than published, and the retained
    /// pointer still forbids both a destructive clear and an extra redispatch.
    /// </summary>
    [Fact]
    public async Task ResumeDispatch_AdmittedPendingWorkFailsAtSelection_RetainsOwnershipAndAddsNoRedispatch()
    {
        using var fixture = new ResumeOwnershipFixture();
        const string goalId = "resume-own-pending-selection";
        var goal = NewFailedGoal(goalId, "Review rejected the changes");
        fixture.GoalStore.AddGoal(goal);
        var pipeline = NewFailedPipeline(fixture.Manager, goal, branchBacked: true);
        fixture.Pool.RegisterWorker("worker-resume-pending", []);

        var probe = new ThrowingIdleProbeGateway(fixture.Gateway);
        var dispatcher = fixture.BuildDispatcher(gatewayOverride: probe);
        dispatcher.BranchListerForTest = (_, _) => Task.FromResult(new List<string>());

        // THE DETERMINISTIC MILESTONE: the admission's own enqueue arms the selection fault, so the
        // failure provably lands AFTER the enqueue.
        var enqueued = 0;
        fixture.Queue.OnEnqueue = _ =>
        {
            enqueued++;
            probe.ThrowOnSelection = true;
        };

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(1, enqueued);
        Assert.Equal(0, probe.SelectionCalls);

        // THE ADMITTED WORK IS RETAINED AND STILL PENDING (never published).
        var activeTaskId = pipeline.ActiveTaskId;
        Assert.NotNull(activeTaskId);
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipeline.GetSlotsForTest()).State);
        Assert.Same(pipeline, fixture.Manager.GetByTaskId(activeTaskId!));
        Assert.Equal(activeTaskId, Assert.Single(fixture.DrainPending()).TaskId);
        Assert.Null(fixture.Queue.GetActiveTask(activeTaskId!));
        Assert.Null(fixture.Recording.Store.Load(activeTaskId!));
        Assert.Equal(activeTaskId, fixture.ReadPersistedPointer(goalId));

        // NO REDISPATCH FOR A NON-NULL OBSERVED POINTER.
        Assert.Empty(QueueEntries(dispatcher));

        var disposition = Assert.Single(RetentionEntries(fixture.DispatcherLogger));
        Assert.Contains("active ownership is RETAINED", disposition.Message, StringComparison.Ordinal);
        Assert.Same(probe.Sentinel, disposition.Exception);
    }

    // ═══════════════ (4) the cross-pipeline delivery: older A published, B pending ═══════════════

    /// <summary>
    /// THE CROSS-PIPELINE CASE. Resumed B admits its task, but the eager delivery's role-aware FIFO
    /// hands the pinned worker an OLDER queued task of goal A. The REAL publish for A completes and
    /// the decorator then throws. A remains the active owner (its pointer, mapping, Pending slot,
    /// active queue entry and recorded assignment row), B remains admitted and Pending, and NO B
    /// redispatch is added.
    /// </summary>
    [Fact]
    public async Task ResumeDispatch_CrossPipelineDeliveryOfOlderTask_KeepsBothAdmissionsAndAddsNoBRedispatch()
    {
        var sentinel = new ResumeDispatchSentinelException("resume-cross-pipeline-sentinel");

        using var fixture = new ResumeOwnershipFixture();
        const string goalAId = "resume-own-cross-a";
        const string goalBId = "resume-own-cross-b";

        // ── A is admitted FIRST with NO idle worker, so its Coder task is only ENQUEUED. ──
        var goalA = NewGoal(goalAId);
        var pipelineA = fixture.Manager.CreatePipeline(goalA);
        ArrangeForDispatch(pipelineA, GoalPhase.Coding);

        WorkTask? admittedA = null;
        fixture.Queue.OnEnqueue = task => admittedA ??= task;

        await CreateDispatchService(
                fixture.Manager, fixture.Queue, fixture.Config,
                new GrpcWorkerGateway(new WorkerPool()), goalA)
            .DispatchToRole(pipelineA, WorkerRole.Coder, "Code A", TestContext.Current.CancellationToken);

        var taskA = pipelineA.ActiveTaskId;
        Assert.NotNull(taskA);
        Assert.NotNull(admittedA);
        Assert.Equal(taskA, admittedA!.TaskId);
        fixture.Queue.OnEnqueue = null;

        // ── B is resumed with ONE idle worker, and its eager delivery really publishes A. ──
        var goalB = NewFailedGoal(goalBId, "Review rejected the changes");
        fixture.GoalStore.AddGoal(goalB);
        var pipelineB = NewFailedPipeline(fixture.Manager, goalB, branchBacked: true);
        var worker = fixture.Pool.RegisterWorker("worker-resume-cross", []);

        var decorator = new ThrowAfterRealPublish(fixture.Recording.Publisher, sentinel);
        var dispatcher = fixture.BuildDispatcher(publisherOverride: decorator);
        dispatcher.BranchListerForTest = (_, _) => Task.FromResult(new List<string>());

        var resumed = await dispatcher.ResumeGoalAsync(goalBId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);

        // THE DELIVERED TASK IS A'S — the older queued task of the requested role.
        var published = Assert.Single(decorator.Published);
        Assert.Equal(taskA, published.Task.TaskId);
        Assert.NotEqual(pipelineB.ActiveTaskId, published.Task.TaskId);
        var assignment = Assert.Single(DrainMessages(worker));
        Assert.Equal(taskA, assignment.Assignment.TaskId);

        // A'S OWNERSHIP IS INTACT — active queue entry, pointer, mapping, Pending slot, recorded row.
        Assert.Equal(taskA, pipelineA.ActiveTaskId);
        Assert.NotNull(fixture.Queue.GetActiveTask(taskA!));
        Assert.Same(pipelineA, fixture.Manager.GetByTaskId(taskA!));
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipelineA.GetSlotsForTest()).State);
        var rowA = fixture.Recording.Store.Load(taskA!);
        Assert.NotNull(rowA);
        Assert.Equal(goalAId, rowA!.Context.GoalId);
        Assert.Equal(WorkerRole.Coder, rowA.Context.Role);

        // B'S ADMISSION IS INTACT AND STILL PENDING, with NO assignment row and NO redispatch.
        var taskB = pipelineB.ActiveTaskId;
        Assert.NotNull(taskB);
        Assert.NotEqual(taskA, taskB);
        Assert.Same(pipelineB, fixture.Manager.GetByTaskId(taskB!));
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipelineB.GetSlotsForTest()).State);
        Assert.Null(fixture.Recording.Store.Load(taskB!));
        Assert.Null(fixture.Queue.GetActiveTask(taskB!));
        Assert.Equal(taskB, Assert.Single(fixture.DrainPending()).TaskId);
        Assert.Empty(QueueEntries(dispatcher));

        // BOTH durable admissions survive in the store.
        Assert.Equal(taskA, fixture.ReadPersistedPointer(goalAId));
        Assert.Equal(taskB, fixture.ReadPersistedPointer(goalBId));

        var disposition = Assert.Single(RetentionEntries(fixture.DispatcherLogger));
        Assert.Contains("active ownership is RETAINED", disposition.Message, StringComparison.Ordinal);
        Assert.Same(sentinel, disposition.Exception);
    }

    // ═════════════ (5) the TRUE pre-admission failure: null pointer, one redispatch ═════════════

    /// <summary>
    /// A GENUINE PRE-ADMISSION FAILURE: the coder role has NO configured model, so the dispatch
    /// refuses before the capture, the claim, the admission and the enqueue. No pointer was ever
    /// observed, so the EXISTING enqueue-only policy applies — exactly ONE redispatch entry and no
    /// slot registered. The input is then repaired and the queue is ACTUALLY DRAINED, proving the
    /// single entry yields one valid NEW dispatch.
    /// </summary>
    [Fact]
    public async Task ResumeDispatch_PreAdmissionMissingModel_PreservesNullPointerAndEnqueuesOnce_ThenDrains()
    {
        using var fixture = new ResumeOwnershipFixture(seedCoderModel: false);
        const string goalId = "resume-own-pre-admission";
        var goal = NewFailedGoal(goalId, "Review rejected the changes");
        fixture.GoalStore.AddGoal(goal);
        var pipeline = NewFailedPipeline(fixture.Manager, goal, branchBacked: true);

        var dispatcher = fixture.BuildDispatcher();
        dispatcher.BranchListerForTest = (_, _) => Task.FromResult(new List<string>());

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);

        // NO ADMISSION HAPPENED AT ALL: the pointer is null and no slot is registered.
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Empty(pipeline.GetSlotsForTest());
        Assert.Empty(pipeline.CaptureRegistry().DispatchAttempts);

        // EXACTLY ONE REDISPATCH ENTRY, from the NULL-observed-pointer arm.
        Assert.Equal([goalId], QueueEntries(dispatcher));
        var disposition = Assert.Single(Entries(fixture.DispatcherLogger, LogLevel.Error));
        Assert.Contains("no active-task pointer was observed", disposition.Message, StringComparison.Ordinal);
        Assert.Contains("enqueued for redispatch", disposition.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(disposition.Exception);

        // ── REPAIR THE INPUT, THEN ACTUALLY DRAIN THE QUEUE. ──
        fixture.Config.Workers["coder"].Model = "coder-model";

        await InvokeDrainRedispatchQueueAsync(dispatcher, TestContext.Current.CancellationToken);

        // THE ONE ENTRY PRODUCED EXACTLY ONE VALID NEW DISPATCH.
        Assert.Empty(QueueEntries(dispatcher));
        var admittedTaskId = pipeline.ActiveTaskId;
        Assert.NotNull(admittedTaskId);
        Assert.Equal(admittedTaskId, Assert.Single(pipeline.GetSlotsForTest()).Slot.TaskId);
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipeline.GetSlotsForTest()).State);
        Assert.Same(pipeline, fixture.Manager.GetByTaskId(admittedTaskId!));
        Assert.Equal(1, Assert.Single(pipeline.CaptureRegistry().DispatchAttempts).HighWaterAttempt);
        Assert.Equal(admittedTaskId, fixture.ReadPersistedPointer(goalId));
    }

    // ═══════════════ (6) the branchless variant keeps the same retained-ownership shape ═══════════════

    /// <summary>
    /// VARIANT B (branchless exhaustion resume) reaches the same catch through its own path: a
    /// post-record channel fault after the admission must retain the ownership and add NO redispatch.
    /// </summary>
    [Fact]
    public async Task BranchlessResumeDispatch_PostRecordChannelFault_RetainsOwnershipAndAddsNoRedispatch()
    {
        using var fixture = new ResumeOwnershipFixture();
        const string goalId = "resume-own-branchless";
        var goal = NewFailedGoal(goalId, "Exceeded max iterations");
        fixture.GoalStore.AddGoal(goal);
        var pipeline = NewFailedPipeline(fixture.Manager, goal, branchBacked: false);

        var worker = fixture.Pool.RegisterWorker("worker-resume-branchless", []);
        var dispatcher = fixture.BuildDispatcher();

        // Variant B must never observe branches.
        var listerInvoked = false;
        dispatcher.BranchListerForTest = (_, _) =>
        {
            listerInvoked = true;
            return Task.FromResult(new List<string>());
        };

        worker.MessageChannel.Writer.TryComplete();

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.False(listerInvoked);
        // Variant B dispatched a CREATE action (no pre-existing branch); the claim's
        // first-assignment semantics set the canonical feature branch as accepted residue.
        Assert.Equal($"copilothive/{goalId}", pipeline.CoderBranch);

        var activeTaskId = pipeline.ActiveTaskId;
        Assert.NotNull(activeTaskId);
        Assert.Equal(activeTaskId, worker.CurrentTaskId);
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipeline.GetSlotsForTest()).State);
        Assert.NotNull(fixture.Recording.Store.Load(activeTaskId!));
        Assert.Empty(QueueEntries(dispatcher));
        Assert.Equal(activeTaskId, fixture.ReadPersistedPointer(goalId));
    }

    // ═════════════ (7) the guarded diagnostic: a throwing provider cannot skip persistence ═════════════

    /// <summary>
    /// THE LOCAL DIAGNOSTIC GUARD. The disposition record goes through a no-throw guard, so a
    /// logging provider that THROWS on the Error write can neither replace the retained-ownership
    /// disposition nor skip the final <c>PersistFull</c> — the preserved pointer is still durably
    /// written.
    /// </summary>
    /// <remarks>
    /// THE THROWING PROVIDER IS PROVEN TO HAVE BEEN ASKED: the same logger records the entry before
    /// throwing, so this vector fails if the guard is removed (the throw would escape the catch and
    /// the fresh-store readback below would never see the retained pointer).
    /// </remarks>
    [Fact]
    public async Task ResumeDispatch_ThrowingLogger_StillRetainsOwnershipAndPersists()
    {
        using var fixture = new ResumeOwnershipFixture();
        const string goalId = "resume-own-throwing-logger";
        var goal = NewFailedGoal(goalId, "Review rejected the changes");
        fixture.GoalStore.AddGoal(goal);
        var pipeline = NewFailedPipeline(fixture.Manager, goal, branchBacked: true);
        var worker = fixture.Pool.RegisterWorker("worker-resume-logger", []);

        var throwing = new ThrowingDispatcherLogger();
        var dispatcher = fixture.BuildDispatcher(loggerOverride: throwing);
        dispatcher.BranchListerForTest = (_, _) => Task.FromResult(new List<string>());

        worker.MessageChannel.Writer.TryComplete();

        var resumed = await dispatcher.ResumeGoalAsync(goalId, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);

        // THE PROVIDER REALLY WAS ASKED AND REALLY THREW.
        Assert.Equal(1, throwing.ThrowCount);
        var asked = Assert.Single(throwing.Emitted);
        Assert.Contains("active ownership is RETAINED", asked, StringComparison.Ordinal);

        // THE OWNERSHIP IS RETAINED AND STILL DURABLY WRITTEN — the throw did not skip the save.
        var activeTaskId = pipeline.ActiveTaskId;
        Assert.NotNull(activeTaskId);
        Assert.Equal(activeTaskId, fixture.ReadPersistedPointer(goalId));
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipeline.GetSlotsForTest()).State);
        Assert.Empty(QueueEntries(dispatcher));
    }

    // ═══════════════════════════════════ fixture ═══════════════════════════════════

    /// <summary>
    /// THE REAL-CHAIN FIXTURE: a store-backed <see cref="GoalPipelineManager"/> over a private
    /// shared-cache in-memory SQLite database, a real <see cref="WorkerPool"/>, a real
    /// <see cref="GrpcWorkerGateway"/> and the shared real-publisher/real-store recording wiring.
    /// </summary>
    private sealed class ResumeOwnershipFixture : IDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly List<SqliteConnection> _connections = [];
        private readonly List<CopilotHiveDbContext> _contexts = [];

        public ResumeOwnershipFixture(bool seedCoderModel = true)
        {
            ConnectionString =
                $"Data Source=file:memdb-resume-ownership-{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keeper = new SqliteConnection(ConnectionString);
            _keeper.Open();
            using (var context = CreateContext())
                context.Database.EnsureCreated();

            Manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
            Recording = EagerAssignmentRecording.Start(Manager, Pool);
            Gateway = new GrpcWorkerGateway(Pool, Recording.Publisher);

            var config = new HiveConfigFile
            {
                Repositories =
                [
                    new RepositoryConfig
                    {
                        Name = "test-repo",
                        Url = "https://github.com/test/repo.git",
                        DefaultBranch = "main",
                    },
                ],
                Workers = { ["coder"] = new WorkerConfig { Model = seedCoderModel ? "coder-model" : null } },
            };
            Config = config;
        }

        public string ConnectionString { get; }

        public GoalPipelineManager Manager { get; }

        public TaskQueue Queue { get; } = new();

        public WorkerPool Pool { get; } = new();

        public EagerAssignmentRecording.Session Recording { get; }

        public HiveConfigFile Config { get; }

        public InMemoryGoalStore GoalStore { get; } = new();

        public TestLogger<GoalDispatcher> DispatcherLogger { get; } = new();

        public ResumeOwnershipBrain Brain { get; } = new();

        public GrpcWorkerGateway Gateway { get; }

        private CopilotHiveDbContext CreateContext()
        {
            var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            _connections.Add(connection);
            var context = new CopilotHiveDbContext(
                new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection).Options);
            _contexts.Add(context);
            return context;
        }

        private PipelineStore CreateStore() =>
            new(CreateContext(), NullLogger<PipelineStore>.Instance);

        /// <summary>
        /// Builds the REAL dispatcher under test. The gateway and the publisher are replaceable ONLY
        /// so a fixture can wrap the real boundary — never to bypass it.
        /// </summary>
        public GoalDispatcher BuildDispatcher(
            IWorkerGateway? gatewayOverride = null,
            IWorkerAssignmentPublisher? publisherOverride = null,
            ILogger<GoalDispatcher>? loggerOverride = null)
        {
            var gateway = gatewayOverride ?? (publisherOverride is null
                ? Gateway
                : new GrpcWorkerGateway(Pool, publisherOverride));

            var goalManager = new GoalManager();
            goalManager.AddSource(GoalStore);

            var dispatcher = new GoalDispatcher(
                goalManager,
                Manager,
                Queue,
                gateway,
                new TaskCompletionNotifier(),
                loggerOverride ?? DispatcherLogger,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
                brain: Brain,
                config: Config,
                goalStore: GoalStore);
            dispatcher.ResumeTimeout = TimeSpan.FromMilliseconds(50);
            return dispatcher;
        }

        /// <summary>The FRESH-STORE readback of the persisted active-task pointer.</summary>
        public string? ReadPersistedPointer(string goalId) =>
            CreateStore().LoadPipeline(goalId)?.ActiveTaskId;

        /// <summary>Drains everything currently pending from the queue.</summary>
        public IReadOnlyList<WorkTask> DrainPending()
        {
            var drained = new List<WorkTask>();
            while (Queue.TryDequeueAny() is { } task)
                drained.Add(task);
            return drained;
        }

        public void Dispose()
        {
            Recording.Dispose();
            foreach (var context in _contexts)
                context.Dispose();
            foreach (var connection in _connections)
                connection.Dispose();
            _keeper.Dispose();
        }
    }

    // ═══════════════════════════════════ helpers ═══════════════════════════════════

    private static Goal NewGoal(string goalId) => new()
    {
        Id = goalId,
        Description = "Resume ownership goal",
        RepositoryNames = ["test-repo"],
    };

    private static Goal NewFailedGoal(string goalId, string reason) => new()
    {
        Id = goalId,
        Description = "Resume ownership goal",
        Status = GoalStatus.Failed,
        FailureReason = reason,
        RepositoryNames = ["test-repo"],
    };

    /// <summary>
    /// Creates a pipeline, exhausts its iteration budget and moves it to Failed — the coherent
    /// arrangement a resumable goal really has. A branch-backed pipeline also carries the canonical
    /// branch and a Coding phase-log entry (variant A); a branchless one carries neither (variant B).
    /// </summary>
    private static GoalPipeline NewFailedPipeline(GoalPipelineManager manager, Goal goal, bool branchBacked)
    {
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        while (pipeline.IterationBudget.TryConsume()) { }
        if (branchBacked)
        {
            pipeline.CoderBranch = $"copilothive/{goal.Id}";
            pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, 1, 1));
        }

        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);
        return pipeline;
    }

    /// <summary>Installs the default plan and parks BOTH the pipeline and the machine on the phase.</summary>
    private static void ArrangeForDispatch(GoalPipeline pipeline, GoalPhase phase)
    {
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, phase);
        pipeline.AdvanceTo(phase);
    }

    /// <summary>
    /// The minimal real <see cref="TaskDispatchService"/> wiring (the same construction the
    /// established wiring fixtures use) — needed only to seed ANOTHER goal's earlier queued task.
    /// </summary>
    private static TaskDispatchService CreateDispatchService(
        GoalPipelineManager manager, TaskQueue queue, HiveConfigFile config,
        IWorkerGateway gateway, Goal goal)
    {
        var logger = NullLogger<TaskDispatchService>.Instance;

        var goalManager = new GoalManager();
        goalManager.AddSource(new SingleGoalSource(goal));
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var lifecycle = new GoalLifecycleService(goalManager, logger);
        var maintenance = new DispatcherMaintenance(
            manager, goalManager, queue, gateway,
            brain: null, agentsManager: null, configRepo: null,
            new ConcurrentQueue<string>(), logger, config: config);

        return new TaskDispatchService(
            queue, gateway, new TaskBuilder(new BranchCoordinator()), config,
            logger, manager, lifecycle, maintenance);
    }

    /// <summary>Reads the dispatcher's private redispatch queue — the suite's established precedent.</summary>
    private static IReadOnlyList<string> QueueEntries(GoalDispatcher dispatcher)
    {
        var field = typeof(GoalDispatcher).GetField(
            "_redispatchQueue", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_redispatchQueue field not found on GoalDispatcher");
        return [.. (ConcurrentQueue<string>)field.GetValue(dispatcher)!];
    }

    /// <summary>Invokes the private redispatch drain (the poll loop's own call).</summary>
    private static Task InvokeDrainRedispatchQueueAsync(GoalDispatcher dispatcher, CancellationToken ct)
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "DrainRedispatchQueueAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DrainRedispatchQueueAsync not found on GoalDispatcher");
        return (Task)method.Invoke(dispatcher, [ct])!;
    }

    private static IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries(
        TestLogger<GoalDispatcher> logger, LogLevel level) =>
        [.. logger.LogEntries.Where(entry => entry.LogLevel == level)];

    /// <summary>The RETAINED-ownership disposition records (the non-null observed-pointer arm).</summary>
    private static IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> RetentionEntries(
        TestLogger<GoalDispatcher> logger) =>
        [.. logger.LogEntries.Where(entry =>
            entry.LogLevel == LogLevel.Error
            && entry.Message.Contains("active ownership is RETAINED", StringComparison.Ordinal))];

    /// <summary>Drains every message the pinned worker's channel currently holds.</summary>
    private static List<CopilotHive.Shared.Grpc.OrchestratorMessage> DrainMessages(ConnectedWorker worker)
    {
        var messages = new List<CopilotHive.Shared.Grpc.OrchestratorMessage>();
        while (worker.MessageChannel.Reader.TryRead(out var message))
            messages.Add(message);
        return messages;
    }

    /// <summary>
    /// A value-comparable OBSERVATION of the state the catch must not touch: the pointer, the
    /// complete slot registry, the per-position attempt counters, the phase-log length, the
    /// iteration budget and the iteration number.
    /// </summary>
    private sealed record OwnershipObservation(
        string? ActiveTaskId,
        IReadOnlyList<WorkSlotView> Slots,
        IReadOnlyList<WorkSlotRegistryAttemptEntry> Attempts,
        int PhaseLogCount,
        int BudgetRemaining,
        int Iteration)
    {
        public static OwnershipObservation Capture(GoalPipeline pipeline) => new(
            pipeline.ActiveTaskId,
            [.. pipeline.GetSlotsForTest()],
            [.. pipeline.CaptureRegistry().DispatchAttempts],
            pipeline.PhaseLog.Count,
            pipeline.IterationBudget.Remaining,
            pipeline.Iteration);

        public static void AssertUnchanged(OwnershipObservation before, OwnershipObservation after)
        {
            Assert.Equal(before.ActiveTaskId, after.ActiveTaskId);
            Assert.Equal(before.Slots, after.Slots);
            Assert.Equal(before.Attempts, after.Attempts);
            Assert.Equal(before.PhaseLogCount, after.PhaseLogCount);
            Assert.Equal(before.BudgetRemaining, after.BudgetRemaining);
            Assert.Equal(before.Iteration, after.Iteration);
        }
    }

    // ═══════════════════════════════════ doubles ═══════════════════════════════════

    /// <summary>The exact sentinel a fixture throws AFTER the real publish completes.</summary>
    private sealed class ResumeDispatchSentinelException(string message) : Exception(message);

    /// <summary>
    /// THE OBSERVING DECORATOR: it awaits the REAL publish (so the assignment row and the channel
    /// message are the PRODUCTION ones), snapshots the ownership, and only then throws its own exact
    /// sentinel. It adds no recording and no write of its own.
    /// </summary>
    private sealed class ThrowAfterRealPublish(IWorkerAssignmentPublisher inner, Exception sentinel)
        : IWorkerAssignmentPublisher
    {
        public Action? AfterRealPublish { get; set; }

        public List<(ConnectedWorker Worker, WorkTask Task)> Published { get; } = [];

        public Exception? ThrownException { get; private set; }

        public async Task PublishAsync(ConnectedWorker worker, WorkTask task, CancellationToken cancellationToken)
        {
            await inner.PublishAsync(worker, task, cancellationToken);

            Published.Add((worker, task));
            AfterRealPublish?.Invoke();

            ThrownException = sentinel;
            throw sentinel;
        }
    }

    /// <summary>
    /// A gateway that forwards everything to the REAL gateway but can fail the delivery's worker
    /// SELECTION deterministically — the admitted-but-pending fault vector.
    /// </summary>
    private sealed class ThrowingIdleProbeGateway(GrpcWorkerGateway inner) : IWorkerGateway
    {
        public Exception Sentinel { get; } = new InvalidOperationException("worker-selection-sentinel");

        public bool ThrowOnSelection { get; set; }

        public int SelectionCalls { get; private set; }

        public ConnectedWorker? GetIdleWorker()
        {
            if (ThrowOnSelection)
                throw Sentinel;

            SelectionCalls++;
            return inner.GetIdleWorker();
        }

        public bool TryClaimAndActivate(ConnectedWorker expected, WorkTask task, TaskQueue queue) =>
            inner.TryClaimAndActivate(expected, task, queue);

        public Task<WorkerTaskSendOutcome> SendTaskAsync(
            ConnectedWorker worker, WorkTask task, CancellationToken ct = default) =>
            inner.SendTaskAsync(worker, task, ct);

        public Task<WorkerTaskSendOutcome> SendTaskAsync(
            string workerId, WorkTask task, CancellationToken ct = default) =>
            inner.SendTaskAsync(workerId, task, ct);

        public Task SendAgentsUpdateAsync(
            ConnectedWorker worker, string role, string content, CancellationToken ct = default) =>
            inner.SendAgentsUpdateAsync(worker, role, content, ct);

        public Task SendAgentsUpdateAsync(
            string workerId, string role, string content, CancellationToken ct = default) =>
            inner.SendAgentsUpdateAsync(workerId, role, content, ct);

        public Task SendCancelAsync(
            string workerId, string taskId, string reason, CancellationToken ct = default) =>
            inner.SendCancelAsync(workerId, taskId, reason, ct);

        public IReadOnlyList<ConnectedWorker> GetAllWorkers() => inner.GetAllWorkers();

        public void MarkBusy(string workerId, string taskId) => inner.MarkBusy(workerId, taskId);
    }

    /// <summary>
    /// A dispatcher logger that RECORDS every Error entry's message and then THROWS — the
    /// logging-provider failure the catch's local diagnostic guard must contain.
    /// </summary>
    private sealed class ThrowingDispatcherLogger : ILogger<GoalDispatcher>
    {
        public List<string> Emitted { get; } = [];

        public int ThrowCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Error)
                return;

            Emitted.Add(formatter(state, exception));
            ThrowCount++;
            throw new InvalidOperationException("dispatcher-logger-sentinel");
        }
    }

    /// <summary>Minimal goal source returning a single pre-configured goal.</summary>
    private sealed class SingleGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "resume-ownership-fake";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Brain stub: inert successes plus a default (Coding-first) plan.</summary>
    private sealed class ResumeOwnershipBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null,
            CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult("summary");

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(
            string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question,
            CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task UpdateModelAsync(
            string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort,
            CancellationToken ct) => Task.CompletedTask;

        public BrainStats? GetStats() => null;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
