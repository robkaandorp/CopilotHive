using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcTaskComplete = CopilotHive.Shared.Grpc.TaskComplete;

namespace CopilotHive.Tests;

/// <summary>
/// THE BOUNDED COMPLETION/IDLE-RELEASE GUARD, driven through the REAL
/// <see cref="HiveOrchestratorService.WorkStream"/> loop.
/// <para>
/// A completion is acted on ONLY when BOTH ownership authorities still agree: the pool (the pinned
/// instance is registered, busy and executing the completing task) and the queue (an ACTIVE entry
/// for that exact task id, assigned to this worker). Every other observation — missing entry,
/// foreign assigned_worker, a stale instance while a successor holds a DISTINCT task id — is
/// IGNORED: nothing released, nothing removed, nothing notified.
/// </para>
/// <para>
/// A Ready is likewise ignored while the observed task still has an ACTIVE queue entry; the
/// initial Ready and the Ready that follows an ACCEPTED completion keep working.
/// </para>
/// </summary>
/// <remarks>
/// Every vector uses a real <c>WorkStream</c>, deterministic signals (the production log lines and
/// the notifier itself) and BOUNDED waits — no sleeps and no live dependencies.
/// </remarks>
public sealed class CompletionTransportOwnershipTests
{
    /// <summary>Bound applied to every await; a hang becomes a named failure, never a stall.</summary>
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    private const string WorkerId = "ownership-worker";

    // ═══════════════════════════════════════════════════════════════════════
    // (1) THE ACCEPTED COMPLETION — the positive control
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// VALID ACTIVE OWNERSHIP IS ACCEPTED: the worker is released, its model cleared, that exact
    /// active entry removed, and exactly one completion notified.
    /// </summary>
    [Fact]
    public async Task Completion_WithValidActiveOwnership_ReleasesAndNotifies()
    {
        await using var h = Harness.Create();
        h.Assign("task-valid", model: "assigned-model");

        var result = await h.CompleteAndAwaitNotificationAsync("task-valid");

        Assert.Equal("task-valid", result.TaskId);
        Assert.Equal("assigned-model", result.Model);

        Assert.False(h.Worker.IsBusy);
        Assert.Null(h.Worker.CurrentTaskId);
        Assert.Null(h.Worker.CurrentModel);
        Assert.Null(h.Queue.GetActiveTask("task-valid"));
        Assert.Equal(1, h.NotificationCount);
    }

    /// <summary>
    /// HasModel SEMANTICS SURVIVE THE GUARD for valid active ownership: an EXPLICIT empty value
    /// wins over the queue's model rather than falling back to it.
    /// </summary>
    [Fact]
    public async Task Completion_ExplicitEmptyModel_BeatsQueueFallback()
    {
        await using var h = Harness.Create();
        h.Assign("task-explicit-empty", model: "queue-model");

        var result = await h.CompleteAndAwaitNotificationAsync(
            "task-explicit-empty", model: "", modelPresent: true);

        Assert.Equal("", result.Model);
    }

    /// <summary>
    /// HasModel SEMANTICS SURVIVE THE GUARD: an ABSENT field falls back to the VALIDATED active
    /// queue entry's model.
    /// </summary>
    [Fact]
    public async Task Completion_AbsentModel_UsesValidatedQueueEntryModel()
    {
        await using var h = Harness.Create();
        h.Assign("task-absent-model", model: "queue-model");

        var result = await h.CompleteAndAwaitNotificationAsync("task-absent-model");

        Assert.Equal("queue-model", result.Model);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) THE REFUSALS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A MISSING ACTIVE ENTRY is refused: the worker really owns the task, but the queue does not,
    /// so nothing is released and nothing is notified.
    /// </summary>
    [Fact]
    public async Task Completion_WithoutActiveQueueEntry_IsIgnored()
    {
        await using var h = Harness.Create();

        // Pool ownership WITHOUT queue activation.
        h.Pool.MarkBusy(WorkerId, "task-unqueued");
        h.Worker.CurrentModel = "assigned-model";

        await h.CompleteAndAwaitIgnoredAsync(
            "task-unqueued", HiveOrchestratorService.OwnershipRefusalReasons.NoActiveQueueEntry);

        Assert.True(h.Worker.IsBusy);
        Assert.Equal("task-unqueued", h.Worker.CurrentTaskId);
        Assert.Equal("assigned-model", h.Worker.CurrentModel);
        Assert.Equal(0, h.NotificationCount);
    }

    /// <summary>
    /// A FOREIGN assigned_worker is refused: the active entry exists for this task id, but the
    /// queue says another worker owns it.
    /// </summary>
    [Fact]
    public async Task Completion_WithForeignAssignedWorker_IsIgnored()
    {
        await using var h = Harness.Create();
        h.Assign("task-foreign", model: "assigned-model");

        // The queue's ownership moves to somebody else while the pool still names this worker.
        h.Queue.MarkActive("task-foreign", "another-worker");

        await h.CompleteAndAwaitIgnoredAsync(
            "task-foreign", HiveOrchestratorService.OwnershipRefusalReasons.ForeignAssignedWorker);

        Assert.True(h.Worker.IsBusy);
        Assert.Equal("task-foreign", h.Worker.CurrentTaskId);
        Assert.Equal("assigned-model", h.Worker.CurrentModel);
        Assert.NotNull(h.Queue.GetActiveTask("task-foreign"));
        Assert.Equal(0, h.NotificationCount);
    }

    /// <summary>
    /// A STALE COMPLETION WHILE A SUCCESSOR HOLDS A DISTINCT TASK ID is refused, and the
    /// successor's own active queue entry is NEVER removed.
    /// </summary>
    /// <remarks>
    /// THE QUEUE AGREES WITH THE DELIVERY HERE: the predecessor's own active entry names THIS
    /// worker as its assigned_worker, exactly as a genuinely re-activated predecessor would. So
    /// the only thing that can refuse this delivery is the POOL's busy/current-task check — this
    /// vector is what proves that check, not the queue's assigned_worker one.
    /// </remarks>
    [Fact]
    public async Task Completion_StaleTaskWhileSuccessorHoldsDistinctTask_IsIgnoredAndSuccessorSurvives()
    {
        await using var h = Harness.Create();

        // The successor the worker is now executing.
        h.Assign("task-successor", model: "successor-model");

        // The predecessor's own active entry — assigned to THIS SAME worker, so the queue-side
        // checks all pass and only the pool's ownership can refuse the delivery.
        var predecessor = h.BuildTask("task-predecessor", "predecessor-model");
        h.Queue.Activate(predecessor, WorkerId);
        Assert.Equal(
            WorkerId, h.Queue.GetActiveTask("task-predecessor")!.Metadata["assigned_worker"]);

        await h.CompleteAndAwaitIgnoredAsync(
            "task-predecessor",
            HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

        // The successor's ownership is untouched, and the predecessor's entry survives too.
        Assert.True(h.Worker.IsBusy);
        Assert.Equal("task-successor", h.Worker.CurrentTaskId);
        Assert.Equal("successor-model", h.Worker.CurrentModel);
        Assert.NotNull(h.Queue.GetActiveTask("task-successor"));
        Assert.NotNull(h.Queue.GetActiveTask("task-predecessor"));
        Assert.Equal(0, h.NotificationCount);
    }

    /// <summary>
    /// A COMPLETION FROM A WORKER THAT IS NO LONGER BUSY is refused: the queue still holds an
    /// active entry naming this worker, but the pool says the assignment was already released, so
    /// nothing is removed and nothing is notified.
    /// </summary>
    [Fact]
    public async Task Completion_FromWorkerThatIsNoLongerBusy_IsIgnored()
    {
        await using var h = Harness.Create();
        h.Assign("task-released", model: "assigned-model");

        // The pool-side ownership is released while the queue entry survives.
        h.Pool.MarkIdle(WorkerId);
        Assert.False(h.Worker.IsBusy);
        Assert.NotNull(h.Queue.GetActiveTask("task-released"));

        await h.CompleteAndAwaitIgnoredAsync(
            "task-released", HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

        Assert.NotNull(h.Queue.GetActiveTask("task-released"));
        Assert.Equal(0, h.NotificationCount);
    }

    /// <summary>
    /// A MAPPING FAILURE returns locally with a guarded warning and RETAINS the held task and the
    /// stream: no release, no queue removal, no fabricated failure notification. The worker's
    /// following Ready is then correctly IGNORED, because the task is still queue-active.
    /// </summary>
    [Fact]
    public async Task Completion_MappingFailure_RetainsOwnershipAndThenReadyIsIgnored()
    {
        await using var h = Harness.Create();
        h.Assign("task-unmappable", model: "assigned-model");

        // An UNKNOWN wire status: GrpcMapper.ToDomain throws for it.
        await h.CompleteAndAwaitMappingFailureAsync(
            "task-unmappable", (CopilotHive.Shared.Grpc.TaskStatus)9999);

        // NOTHING WAS RELEASED, REMOVED OR NOTIFIED.
        Assert.True(h.Worker.IsBusy);
        Assert.Equal("task-unmappable", h.Worker.CurrentTaskId);
        Assert.Equal("assigned-model", h.Worker.CurrentModel);
        Assert.NotNull(h.Queue.GetActiveTask("task-unmappable"));
        Assert.Equal(0, h.NotificationCount);
        Assert.False(h.StreamEnded, "a mapping failure must not unwind the worker's stream");

        // THE FOLLOWING READY IS IGNORED: the task is still active in the queue.
        h.Queue.Enqueue(h.BuildTask("task-next", "next-model"));
        await h.ReadyAndAwaitIgnoredAsync();

        Assert.True(h.Worker.IsBusy);
        Assert.Equal("task-unmappable", h.Worker.CurrentTaskId);
        Assert.NotNull(h.Queue.GetActiveTask("task-unmappable"));

        // The pending task was never dequeued for this ignored Ready.
        var stillPending = h.Queue.TryDequeueAny();
        Assert.NotNull(stillPending);
        Assert.Equal("task-next", stillPending!.TaskId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) READY
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE INITIAL READY of a fresh worker still works: no task is held, so the checked idle is
    /// applied and the handler proceeds to its normal dequeue (which finds nothing here).
    /// </summary>
    [Fact]
    public async Task Ready_InitialReadyOnIdleWorker_IsAccepted()
    {
        await using var h = Harness.Create();

        await h.ReadyAndAwaitAcceptedAsync();

        Assert.False(h.Worker.IsBusy);
        Assert.Null(h.Worker.CurrentTaskId);
    }

    /// <summary>
    /// THE READY AFTER AN ACCEPTED COMPLETION still works: the completion released the ownership,
    /// so the following Ready is accepted rather than refused.
    /// </summary>
    [Fact]
    public async Task Ready_AfterAcceptedCompletion_IsAccepted()
    {
        await using var h = Harness.Create();
        h.Assign("task-then-ready", model: "assigned-model");

        await h.CompleteAndAwaitNotificationAsync("task-then-ready");
        Assert.Null(h.Queue.GetActiveTask("task-then-ready"));

        await h.ReadyAndAwaitAcceptedAsync();

        Assert.False(h.Worker.IsBusy);
        Assert.Null(h.Worker.CurrentTaskId);
    }

    /// <summary>
    /// A READY WHILE THE TASK IS STILL QUEUE-ACTIVE is IGNORED before any idle or dequeue: the
    /// held ownership survives and no pending task is handed out.
    /// </summary>
    [Fact]
    public async Task Ready_WithStillActiveQueueEntry_IsIgnoredBeforeIdlingOrDequeuing()
    {
        await using var h = Harness.Create();
        h.Assign("task-held", model: "assigned-model");
        h.Queue.Enqueue(h.BuildTask("task-pending", "pending-model"));

        await h.ReadyAndAwaitIgnoredAsync();

        Assert.True(h.Worker.IsBusy);
        Assert.Equal("task-held", h.Worker.CurrentTaskId);
        Assert.Equal("assigned-model", h.Worker.CurrentModel);
        Assert.NotNull(h.Queue.GetActiveTask("task-held"));

        // The pending task was never dequeued.
        var pending = h.Queue.TryDequeueAny();
        Assert.NotNull(pending);
        Assert.Equal("task-pending", pending!.TaskId);
    }

    /// <summary>
    /// AN INCONSISTENT OWNERSHIP SHAPE REFUSES THE READY AT THE CHECKED IDLE. The worker carries
    /// a task id while NOT busy — the shape the pool's checked idle refuses — and the task has no
    /// active queue entry, so the earlier still-active gate does not apply. The handler must
    /// therefore stop at the CHECKED IDLE: nothing is cleared and no pending task is handed out.
    /// </summary>
    [Fact]
    public async Task Ready_WithInconsistentOwnershipShape_IsRefusedByTheCheckedIdle()
    {
        await using var h = Harness.Create();

        // The inconsistent shape, with NO active queue entry for the task.
        h.Pool.MarkBusy(WorkerId, "task-inconsistent");
        h.Worker.Role = DomainWorkerRole.Coder;
        h.Worker.IsBusy = false;
        Assert.Null(h.Queue.GetActiveTask("task-inconsistent"));

        h.Queue.Enqueue(h.BuildTask("task-pending", "pending-model"));

        await h.ReadyAndAwaitRefusedIdleAsync();

        // NOTHING WAS CLEARED by the refused idle.
        Assert.Equal("task-inconsistent", h.Worker.CurrentTaskId);
        Assert.NotNull(h.Worker.CurrentTaskStartedAt);
        Assert.Equal(DomainWorkerRole.Coder, h.Worker.Role);

        // And no pending task was dequeued or assigned.
        var pending = h.Queue.TryDequeueAny();
        Assert.NotNull(pending);
        Assert.Equal("task-pending", pending!.TaskId);
        Assert.Null(h.Queue.GetActiveTask("task-pending"));
    }

    /// <summary>
    /// THE ABA DEFENCE-IN-DEPTH, exercised directly. A completion delivered on behalf of an
    /// ALREADY-REPLACED worker instance is refused by the handler's OWN pinned-instance check,
    /// naming that guard — not by the later checked release.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS INVOKED DIRECTLY RATHER THAN THROUGH THE STREAM: <c>WorkStream</c> already ends
    /// a stream whose pinned instance no longer matches the registered one, so a real stream can
    /// never deliver this case to the handler. The check inside the handler is deliberate
    /// defence-in-depth, and this is the honest way to pin it: the narrower runtime contract is
    /// asserted here, while the reachable ownership vectors above all run through the real stream.
    /// </remarks>
    [Fact]
    public async Task Completion_ForReplacedWorkerInstance_IsRefusedByThePinnedInstanceGuard()
    {
        await using var h = Harness.Create();
        h.Assign("task-aba", model: "assigned-model");
        var stale = h.Worker;

        // The pinned instance is replaced under the SAME id, and the replacement takes over the
        // very same task — so ONLY the pinned-instance check can refuse this delivery.
        Assert.True(h.Pool.RemoveWorker(stale));
        var replacement = h.Pool.RegisterWorker(WorkerId, []);
        h.Pool.MarkBusy(WorkerId, "task-aba");
        replacement.CurrentModel = "replacement-model";

        h.InvokeHandleTaskCompleteDirectly(stale, "task-aba");

        Assert.Contains(
            h.Logger.Messages,
            m => m.Contains(SignallingLogger.CompletionIgnored, StringComparison.Ordinal)
                 && m.Contains(
                     HiveOrchestratorService.OwnershipRefusalReasons.PinnedInstanceReplaced,
                     StringComparison.Ordinal));

        // The replacement's own assignment, and the queue entry, both survive.
        Assert.True(replacement.IsBusy);
        Assert.Equal("task-aba", replacement.CurrentTaskId);
        Assert.Equal("replacement-model", replacement.CurrentModel);
        Assert.NotNull(h.Queue.GetActiveTask("task-aba"));
        Assert.Equal(0, h.NotificationCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  harness
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A live <see cref="HiveOrchestratorService"/> over real collaborators and a REAL
    /// <c>WorkStream</c>, with deterministic log/notification signals.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required HiveOrchestratorService Service { get; init; }
        public required WorkerPool Pool { get; init; }
        public required TaskQueue Queue { get; init; }
        public required ConnectedWorker Worker { get; init; }
        public required SignallingLogger Logger { get; init; }
        public required TaskCompletionNotifier Notifier { get; init; }
        private ChannelStreamReader Reader { get; init; } = null!;
        private Task StreamTask { get; init; } = null!;

        private int _notificationCount;
        private readonly Queue<TaskCompletionSource<TaskResult>> _resultWaiters = new();

        /// <summary>Completion notifications the transport notifier emitted.</summary>
        public int NotificationCount => Volatile.Read(ref _notificationCount);

        /// <summary>Whether the real stream task has terminated.</summary>
        public bool StreamEnded => StreamTask.IsCompleted;

        public static Harness Create()
        {
            var pool = new WorkerPool();
            var queue = new TaskQueue();
            var pipelineManager = new GoalPipelineManager();
            var dashboard = new DashboardNotifier();

            // TWO notifiers: the dispatcher subscribes to its own, so the transport notifier
            // carries exactly ONE subscriber — the harness's own handler.
            var transportNotifier = new TaskCompletionNotifier();
            var dispatcherNotifier = new TaskCompletionNotifier();

            var dispatcher = new GoalDispatcher(
                new GoalManager(),
                pipelineManager,
                queue,
                new GrpcWorkerGateway(pool),
                dispatcherNotifier,
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

            var logger = new SignallingLogger();
            var service = new HiveOrchestratorService(
                pool,
                queue,
                pipelineManager,
                transportNotifier,
                dispatcher,
                logger,
                dashboardNotifier: dashboard);

            var worker = pool.RegisterWorker(WorkerId, []);
            var reader = new ChannelStreamReader();
            var streamTask = service.WorkStream(reader, new MockStreamWriter(), MockContext());

            var harness = new Harness
            {
                Service = service,
                Pool = pool,
                Queue = queue,
                Worker = worker,
                Logger = logger,
                Notifier = transportNotifier,
                Reader = reader,
                StreamTask = streamTask,
            };

            transportNotifier.OnTaskCompleted += result =>
            {
                Interlocked.Increment(ref harness._notificationCount);
                TaskCompletionSource<TaskResult>? waiter = null;
                lock (harness._resultWaiters)
                {
                    if (harness._resultWaiters.Count > 0)
                        waiter = harness._resultWaiters.Dequeue();
                }

                waiter?.TrySetResult(result);
                return Task.CompletedTask;
            };

            return harness;
        }

        /// <summary>Builds a task carrying this harness's shape.</summary>
        public WorkTask BuildTask(string taskId, string model) => new()
        {
            TaskId = taskId,
            GoalId = "goal-ownership",
            GoalDescription = "bounded ownership goal",
            Prompt = "do the work",
            Role = DomainWorkerRole.Coder,
            Model = model,
            Repositories = [],
        };

        /// <summary>
        /// Gives the worker GENUINE active ownership of the task through the PRODUCTION assignment
        /// path: the queue entry is activated for this worker and the pool is marked busy.
        /// </summary>
        public void Assign(string taskId, string model)
        {
            var task = BuildTask(taskId, model);
            Queue.Enqueue(task);
            var dequeued = Queue.TryDequeue(DomainWorkerRole.Unspecified);
            Assert.NotNull(dequeued);
            Service.ApplyTaskAssignment(Worker, dequeued!);

            Assert.True(Worker.IsBusy);
            Assert.Equal(taskId, Worker.CurrentTaskId);
            Assert.Equal(model, Worker.CurrentModel);
            Assert.NotNull(Queue.GetActiveTask(taskId));
        }

        private GrpcTaskComplete BuildComplete(
            string taskId,
            string? model,
            bool modelPresent,
            CopilotHive.Shared.Grpc.TaskStatus status)
        {
            var complete = new GrpcTaskComplete
            {
                TaskId = taskId,
                Status = status,
                Output = $"output-{taskId}",
            };
            if (modelPresent)
                complete.Model = model ?? "";

            Assert.Equal(modelPresent, complete.HasModel);
            return complete;
        }

        /// <summary>Delivers a completion and awaits the domain result the notifier emitted.</summary>
        public async Task<TaskResult> CompleteAndAwaitNotificationAsync(
            string taskId, string? model = null, bool modelPresent = false)
        {
            var waiter = new TaskCompletionSource<TaskResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_resultWaiters)
                _resultWaiters.Enqueue(waiter);

            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, model, modelPresent, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });

            return await waiter.Task.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Delivers a completion and awaits the PRODUCTION IGNORED warning carrying
        /// <paramref name="expectedReason"/> — the deterministic signal that THIS SPECIFIC guard
        /// refused the delivery.
        /// </summary>
        /// <remarks>
        /// WAITING ON THE REASON, not merely on "a refusal", is what keeps these vectors
        /// discriminating: the checked release at the end of the handler would otherwise refuse a
        /// mis-validated delivery too, and a generic wait would be satisfied by that late refusal
        /// even though the early validation had been removed.
        /// </remarks>
        public async Task CompleteAndAwaitIgnoredAsync(string taskId, string expectedReason)
        {
            var signal = Logger.WaitFor(expectedReason);
            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, null, false, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // The refusal really was the expected guard's, and it named this task.
            Assert.Contains(
                Logger.Messages,
                m => m.Contains(SignallingLogger.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(expectedReason, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Delivers a completion whose mapping FAILS and awaits the production mapping-failure
        /// warning.
        /// </summary>
        public async Task CompleteAndAwaitMappingFailureAsync(
            string taskId, CopilotHive.Shared.Grpc.TaskStatus status)
        {
            var signal = Logger.WaitFor(SignallingLogger.MappingFailed);
            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(taskId, null, false, status),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Pushes a Ready and awaits the production READY-IGNORED warning naming the STILL-ACTIVE
        /// queue entry as the refusing guard — never merely "some" Ready refusal.
        /// </summary>
        public async Task ReadyAndAwaitIgnoredAsync()
        {
            var signal = Logger.WaitFor(
                HiveOrchestratorService.OwnershipRefusalReasons.ReadyTaskStillActive);
            Reader.Push(new WorkerMessage { WorkerId = WorkerId, Ready = new WorkerReady() });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            Assert.Contains(
                Logger.Messages,
                m => m.Contains(SignallingLogger.ReadyIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.ReadyTaskStillActive,
                         StringComparison.Ordinal));
        }

        /// <summary>
        /// Pushes a Ready and awaits the production READY-IGNORED warning naming the CHECKED-IDLE
        /// refusal as the refusing guard.
        /// </summary>
        public async Task ReadyAndAwaitRefusedIdleAsync()
        {
            var signal = Logger.WaitFor(
                HiveOrchestratorService.OwnershipRefusalReasons.ReadyCheckedIdleRefused);
            Reader.Push(new WorkerMessage { WorkerId = WorkerId, Ready = new WorkerReady() });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            Assert.Contains(
                Logger.Messages,
                m => m.Contains(SignallingLogger.ReadyIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.ReadyCheckedIdleRefused,
                         StringComparison.Ordinal));
        }

        /// <summary>
        /// Pushes a Ready and awaits the production "is ready" line, which is emitted ONLY after
        /// the checked idle was applied.
        /// </summary>
        public async Task ReadyAndAwaitAcceptedAsync()
        {
            var signal = Logger.WaitFor(SignallingLogger.ReadyAccepted);
            Reader.Push(new WorkerMessage { WorkerId = WorkerId, Ready = new WorkerReady() });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Invokes the PRODUCTION <c>HandleTaskComplete</c> directly for an instance the real
        /// stream loop would already have rejected — the only way to exercise the handler's own
        /// defence-in-depth pinned-instance check.
        /// </summary>
        public void InvokeHandleTaskCompleteDirectly(ConnectedWorker pinned, string taskId)
        {
            var method = typeof(HiveOrchestratorService).GetMethod(
                "HandleTaskComplete",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            method!.Invoke(
                Service,
                [
                    pinned,
                    new GrpcTaskComplete
                    {
                        TaskId = taskId,
                        Status = CopilotHive.Shared.Grpc.TaskStatus.Completed,
                        Output = $"output-{taskId}",
                    },
                ]);
        }

        /// <summary>Ends the stream and joins it, bounded, on every path.</summary>
        public async ValueTask DisposeAsync()
        {
            Reader.Complete();
            try
            {
                await StreamTask.WaitAsync(BoundedWait, CancellationToken.None);
            }
            catch (Exception) when (StreamTask.IsCompleted)
            {
                // A terminal fault of a COMPLETED task is a joined termination.
            }
            catch (TimeoutException)
            {
                // Bounded: a non-terminating stream is reported by the assertion that needed it.
            }
        }
    }

    /// <summary>
    /// Records every logged message and hands out FRESH per-call signals for the production lines
    /// the vectors synchronize on. A previously emitted line can never satisfy a later wait.
    /// </summary>
    private sealed class SignallingLogger : ILogger<HiveOrchestratorService>
    {
        /// <summary>The guarded completion-refusal warning's stable fragment.</summary>
        public const string CompletionIgnored = "completion for task";

        /// <summary>The guarded mapping-failure warning's stable fragment.</summary>
        public const string MappingFailed = "could not be mapped";

        /// <summary>The guarded Ready-refusal warning's stable fragment.</summary>
        public const string ReadyIgnored = "ready ignored";

        /// <summary>The accepted-Ready information line, emitted after the checked idle.</summary>
        public const string ReadyAccepted = "is ready";

        private readonly List<string> _messages = [];
        private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        /// <summary>A FRESH signal completed by the NEXT message containing <paramref name="fragment"/>.</summary>
        public Task WaitFor(string fragment)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_messages)
                _waiters.Add((fragment, signal));
            return signal.Task;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            List<TaskCompletionSource> matched = [];
            lock (_messages)
            {
                _messages.Add(message);

                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (!message.Contains(_waiters[i].Fragment, StringComparison.Ordinal))
                        continue;

                    matched.Add(_waiters[i].Signal);
                    _waiters.RemoveAt(i);
                }
            }

            foreach (var signal in matched)
                signal.TrySetResult();
        }
    }

    private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;

    /// <summary>In-memory stream reader backed by an unbounded channel.</summary>
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

    private sealed class MockStreamWriter : IServerStreamWriter<OrchestratorMessage>
    {
        public WriteOptions? WriteOptions { get; set; }

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(OrchestratorMessage message)
            => Task.CompletedTask;

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(
            OrchestratorMessage message, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
