using System.Reflection;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Tests that verify <see cref="DashboardNotifier"/> is wired into the worker-related
/// producers: <see cref="HiveOrchestratorService"/> and <see cref="StaleWorkerCleanupService"/>.
/// </summary>
public sealed class DashboardNotifierWorkerWiringTests
{
    private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;

    /// <summary>
    /// Reads the production <c>_heartbeatState</c> dictionary via reflection so tests can
    /// assert on the throttle state actually maintained by production code.
    /// </summary>
    private static IDictionary<string, (DateTime LastNotify, bool WasBusy, int LastNotifiedCtx)>
        HeartbeatState(HiveOrchestratorService service)
    {
        var dict = typeof(HiveOrchestratorService)
            .GetField("_heartbeatState", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)
            as IDictionary<string, (DateTime LastNotify, bool WasBusy, int LastNotifiedCtx)>;
        Assert.NotNull(dict);
        return dict!;
    }

    private static (HiveOrchestratorService service, WorkerPool pool, TaskQueue queue, int[] count)
        CreateService(DashboardNotifier notifier)
    {
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var completionNotifier = new TaskCompletionNotifier();
        var goalManager = new GoalManager();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(pool),
            completionNotifier,
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        var counter = new int[1];
        notifier.OnStateChanged += () => Interlocked.Increment(ref counter[0]);

        var service = new HiveOrchestratorService(
            pool,
            taskQueue,
            pipelineManager,
            completionNotifier,
            dispatcher,
            NullLogger<HiveOrchestratorService>.Instance,
            dashboardNotifier: notifier);

        return (service, pool, taskQueue, counter);
    }

    // ── Register ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_Success_NotifiesOnce()
    {
        var notifier = new DashboardNotifier();
        var (service, _, _, count) = CreateService(notifier);

        var response = await service.Register(
            new RegisterRequest { WorkerId = "w1" }, MockContext());

        Assert.True(response.Accepted);
        Assert.Equal(1, count[0]);
    }

    [Fact]
    public async Task Register_Duplicate_DoesNotNotifyAgain()
    {
        var notifier = new DashboardNotifier();
        var (service, _, _, count) = CreateService(notifier);

        await service.Register(new RegisterRequest { WorkerId = "w1" }, MockContext());
        var second = await service.Register(new RegisterRequest { WorkerId = "w1" }, MockContext());

        Assert.False(second.Accepted);
        Assert.Equal(1, count[0]);
    }

    // ── ApplyTaskAssignment ───────────────────────────────────────────────────

    [Fact]
    public void ApplyTaskAssignment_Notifies()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, queue, count) = CreateService(notifier);
        var worker = pool.RegisterWorker("w-a", []);
        count[0] = 0;

        var task = new WorkTask
        {
            TaskId = "t1",
            GoalId = "g1",
            GoalDescription = "d",
            Prompt = "p",
            Role = WorkerRole.Coder,
            Model = "m",
            Repositories = [],
        };
        queue.Enqueue(task);
        var dequeued = queue.TryDequeue(WorkerRole.Unspecified)!;

        // Capture the worker's state at the exact moment the notification fires.
        // If the notification were moved before `worker.CurrentModel = task.Model`,
        // these captured values would be null/false and the assertions below fail.
        string? modelAtNotify = null;
        bool busyAtNotify = false;
        string? taskIdAtNotify = null;
        notifier.OnStateChanged += () =>
        {
            modelAtNotify = worker.CurrentModel;
            busyAtNotify = worker.IsBusy;
            taskIdAtNotify = worker.CurrentTaskId;
        };

        service.ApplyTaskAssignment(worker, dequeued);

        Assert.Equal(1, count[0]);
        Assert.Equal(task.Model, modelAtNotify);
        Assert.True(busyAtNotify);
        Assert.Equal(task.TaskId, taskIdAtNotify);
    }

    // ── Heartbeat throttling ──────────────────────────────────────────────────

    private static HeartbeatRequest Hb(string id, bool busy, int ctx) =>
        new() { WorkerId = id, Busy = busy, ContextUsagePercent = ctx };

    [Fact]
    public async Task Heartbeat_UnknownWorker_DoesNotNotify()
    {
        var notifier = new DashboardNotifier();
        var (service, _, _, count) = CreateService(notifier);

        await service.Heartbeat(Hb("ghost", false, 10), MockContext());

        Assert.Equal(0, count[0]);
        // No throttle entry may be created for an unknown worker.
        var state = HeartbeatState(service);
        Assert.False(state.ContainsKey("ghost"));
        Assert.Empty(state);
    }

    [Fact]
    public async Task Heartbeat_FirstForKnownWorker_Notifies()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);
        pool.RegisterWorker("w1", []);
        count[0] = 0;

        await service.Heartbeat(Hb("w1", false, 10), MockContext());

        Assert.Equal(1, count[0]);
    }

    [Fact]
    public async Task Heartbeat_UnchangedWithinWindow_DoesNotNotifyAgain()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);
        pool.RegisterWorker("w1", []);
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        service._now = () => now;
        count[0] = 0;

        await service.Heartbeat(Hb("w1", false, 10), MockContext());
        await service.Heartbeat(Hb("w1", false, 12), MockContext());
        await service.Heartbeat(Hb("w1", false, 14), MockContext());

        Assert.Equal(1, count[0]);
    }

    [Fact]
    public async Task Heartbeat_BusyFlagChange_Notifies()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);
        pool.RegisterWorker("w1", []);
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        service._now = () => now;
        count[0] = 0;

        await service.Heartbeat(Hb("w1", false, 10), MockContext());
        await service.Heartbeat(Hb("w1", true, 10), MockContext());

        Assert.Equal(2, count[0]);
    }

    [Fact]
    public async Task Heartbeat_ContextDeltaAtLeastFive_Notifies()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);
        pool.RegisterWorker("w1", []);
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        service._now = () => now;
        count[0] = 0;

        await service.Heartbeat(Hb("w1", false, 10), MockContext());
        await service.Heartbeat(Hb("w1", false, 15), MockContext()); // delta 5 → notify

        Assert.Equal(2, count[0]);
    }

    [Fact]
    public async Task Heartbeat_ContextDeltaMeasuredFromLastNotifiedValue()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);
        pool.RegisterWorker("w1", []);
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        service._now = () => now;
        count[0] = 0;

        await service.Heartbeat(Hb("w1", false, 10), MockContext()); // notify (new)
        await service.Heartbeat(Hb("w1", false, 12), MockContext()); // no
        await service.Heartbeat(Hb("w1", false, 14), MockContext()); // no
        await service.Heartbeat(Hb("w1", false, 16), MockContext()); // delta from 10 = 6 → notify

        Assert.Equal(2, count[0]);
    }

    [Fact]
    public async Task Heartbeat_ThirtySecondsElapsed_Notifies()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);
        pool.RegisterWorker("w1", []);
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        service._now = () => now;
        count[0] = 0;

        await service.Heartbeat(Hb("w1", false, 10), MockContext());
        now = now.AddSeconds(30);
        await service.Heartbeat(Hb("w1", false, 10), MockContext());

        Assert.Equal(2, count[0]);
    }

    [Fact]
    public async Task Heartbeat_EvictsOldestWhenAtCapacity()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);
        service.MaxHeartbeatEntries = 3;
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        service._now = () => now;

        pool.RegisterWorker("a", []);
        pool.RegisterWorker("b", []);
        pool.RegisterWorker("c", []);
        pool.RegisterWorker("d", []);
        count[0] = 0;

        // Fill exactly 3 entries with distinct, controlled timestamps.
        await service.Heartbeat(Hb("a", false, 10), MockContext());   // LastNotify = T+0 (oldest)
        now = now.AddSeconds(1);
        await service.Heartbeat(Hb("b", false, 10), MockContext());   // T+1
        now = now.AddSeconds(1);
        await service.Heartbeat(Hb("c", false, 10), MockContext());   // T+2

        var state = HeartbeatState(service);
        Assert.Equal(3, state.Count);
        Assert.Equal(3, count[0]);

        // 4th distinct worker → capacity reached → oldest ("a") must be evicted.
        now = now.AddSeconds(1);
        await service.Heartbeat(Hb("d", false, 10), MockContext());

        Assert.Equal(3, state.Count);
        Assert.False(state.ContainsKey("a"));   // oldest by LastNotify was evicted
        Assert.True(state.ContainsKey("b"));
        Assert.True(state.ContainsKey("c"));
        Assert.True(state.ContainsKey("d"));    // new entry present
        Assert.Equal(4, count[0]);              // exactly 1 notification for the 4th heartbeat
    }

    // ── StaleWorkerCleanupService ─────────────────────────────────────────────

    private static ConnectedWorker MakeWorker(string id) => new()
    {
        Id = id,
        Role = WorkerRole.Coder,
        Capabilities = [],
    };

    private static Mock<IWorkerPool> MakePoolMock()
    {
        var mock = new Mock<IWorkerPool>();
        mock.Setup(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>())).Returns([]);
        mock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([]);
        return mock;
    }

    private static (StaleWorkerCleanupService svc, int[] count) CreateCleanup(IWorkerPool pool)
    {
        var notifier = new DashboardNotifier();
        var counter = new int[1];
        notifier.OnStateChanged += () => Interlocked.Increment(ref counter[0]);
        var svc = new StaleWorkerCleanupService(
            pool, new TaskQueue(), new GoalPipelineManager(),
            NullLogger<StaleWorkerCleanupService>.Instance,
            goalDispatcher: null, config: null, dashboardNotifier: notifier);
        return (svc, counter);
    }

    [Fact]
    public async Task Cleanup_NoRemovals_DoesNotNotify()
    {
        var poolMock = MakePoolMock();
        var (svc, count) = CreateCleanup(poolMock.Object);

        await svc.RunCleanupCycleAsync();

        Assert.Equal(0, count[0]);
    }

    [Fact]
    public async Task Cleanup_StaleWorkerPurged_NotifiesOnce()
    {
        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>()))
            .Returns([MakeWorker("stale-1"), MakeWorker("stale-2")]);
        var (svc, count) = CreateCleanup(poolMock.Object);

        await svc.RunCleanupCycleAsync();

        Assert.Equal(1, count[0]);
    }

    [Fact]
    public async Task Cleanup_TimedOutWorkerRemoved_NotifiesOnce()
    {
        var hung = MakeWorker("hung");
        hung.IsBusy = true;
        hung.CurrentTaskId = "t";
        hung.CurrentTaskStartedAt = DateTime.UtcNow.AddMinutes(-90);

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([hung]);
        poolMock.Setup(p => p.RemoveWorker("hung")).Returns(true);
        var (svc, count) = CreateCleanup(poolMock.Object);

        await svc.RunCleanupCycleAsync();

        Assert.Equal(1, count[0]);
    }

    [Fact]
    public async Task Cleanup_TimedOutWorkerRemoveReturnsFalse_DoesNotNotify()
    {
        var hung = MakeWorker("hung");
        hung.IsBusy = true;
        hung.CurrentTaskId = "t";
        hung.CurrentTaskStartedAt = DateTime.UtcNow.AddMinutes(-90);

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([hung]);
        poolMock.Setup(p => p.RemoveWorker("hung")).Returns(false);
        var (svc, count) = CreateCleanup(poolMock.Object);

        await svc.RunCleanupCycleAsync();

        Assert.Equal(0, count[0]);
    }

    // ── Register resets heartbeat dict (criterion 13) ────────────────────────

    [Fact]
    public async Task Register_RemovesExistingHeartbeatEntry()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, _) = CreateService(notifier);

        // Pre-populate the heartbeat dict with an entry for "w-dict" via reflection
        var heartbeatState = typeof(HiveOrchestratorService)
            .GetField("_heartbeatState", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service) as IDictionary<string, (DateTime, bool, int)>;
        Assert.NotNull(heartbeatState);
        heartbeatState!["w-dict"] = (DateTime.UtcNow, true, 50);

        await service.Register(new RegisterRequest { WorkerId = "w-dict" }, MockContext());

        Assert.False(heartbeatState.ContainsKey("w-dict"));
    }

    // ── WorkStream finally remove (true) → 1 + dict cleaned (criterion 14) ────

    /// <summary>
    /// Drives the real <see cref="HiveOrchestratorService.WorkStream"/> RPC with an in-memory
    /// stream that yields a single progress message and then completes. The stream closing
    /// makes WorkStream fall through to its <c>finally</c> block, which is the production code
    /// under test: it calls <c>RemoveWorker</c>, cleans the heartbeat dict, and notifies when
    /// the removal succeeded. If the notification is removed from the finally block this fails.
    /// </summary>
    [Fact]
    public async Task WorkStream_RemoveTrue_NotifiesAndCleansDict()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);

        pool.RegisterWorker("ws-true", []);
        var state = HeartbeatState(service);
        state["ws-true"] = (DateTime.UtcNow, false, 10);
        count[0] = 0;

        var reader = new FakeStreamReader([
            new WorkerMessage
            {
                WorkerId = "ws-true",
                Progress = new TaskProgress { TaskId = "t", Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress, Message = "m" },
            },
        ]);

        await service.WorkStream(reader, new MockStreamWriter(), MockContext());

        // Worker really was removed by production code.
        Assert.Null(pool.GetWorker("ws-true"));
        // Heartbeat entry cleaned by production code.
        Assert.False(state.ContainsKey("ws-true"));
        // Exactly one notification from the finally block.
        Assert.Equal(1, count[0]);
    }

    // ── WorkStream finally remove (false) → 0 + dict cleaned (criterion 15) ──

    /// <summary>
    /// Same real WorkStream path, but for a worker that is not in the pool: <c>RemoveWorker</c>
    /// returns <c>false</c>, so production code must clean the heartbeat dict but must NOT notify.
    /// </summary>
    [Fact]
    public async Task WorkStream_RemoveFalse_NoNotifyButDictCleaned()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);

        // Never registered → RemoveWorker will return false.
        Assert.Null(pool.GetWorker("ws-false"));
        var state = HeartbeatState(service);
        state["ws-false"] = (DateTime.UtcNow, false, 10);
        count[0] = 0;

        var reader = new FakeStreamReader([
            new WorkerMessage
            {
                WorkerId = "ws-false",
                Progress = new TaskProgress { TaskId = "t", Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress, Message = "m" },
            },
        ]);

        await service.WorkStream(reader, new MockStreamWriter(), MockContext());

        Assert.False(state.ContainsKey("ws-false"));
        Assert.Equal(0, count[0]);
    }

    // ── HandleTaskComplete → 1 (criterion 17) ────────────────────────────────

    [Fact]
    public void HandleTaskComplete_NotifiesOnce()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, queue, count) = CreateService(notifier);

        var worker = pool.RegisterWorker("w-tc", []);
        var task = new WorkTask
        {
            TaskId = "tc-1",
            GoalId = "g1",
            GoalDescription = "d",
            Prompt = "p",
            Role = WorkerRole.Coder,
            Model = "m",
            Repositories = [],
        };
        queue.Enqueue(task);
        var dequeued = queue.TryDequeue(WorkerRole.Unspecified)!;
        service.ApplyTaskAssignment(worker, dequeued);
        count[0] = 0;

        var complete = new CopilotHive.Shared.Grpc.TaskComplete
        {
            TaskId = "tc-1",
            Status = CopilotHive.Shared.Grpc.TaskStatus.Completed,
            Output = "done",
        };

        // HandleTaskComplete is private; use reflection to invoke it
        var method = typeof(HiveOrchestratorService)
            .GetMethod("HandleTaskComplete", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(service, [worker, complete]);

        Assert.Equal(1, count[0]);
    }

    // ── HandleWorkerReady with task → exactly 1 (criterion 18) ───────────────

    [Fact]
    public async Task HandleWorkerReady_WithTask_NotifiesExactlyOnce()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, queue, count) = CreateService(notifier);

        var worker = pool.RegisterWorker("w-ready", []);
        var task = new WorkTask
        {
            TaskId = "ready-1",
            GoalId = "g1",
            GoalDescription = "d",
            Prompt = "p",
            Role = WorkerRole.Coder,
            Model = "m",
            Repositories = [],
        };
        queue.Enqueue(task);
        count[0] = 0;

        var method = typeof(HiveOrchestratorService)
            .GetMethod("HandleWorkerReady", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(service, [worker, new MockStreamWriter(), CancellationToken.None])!;

        // Exactly 1 — from ApplyTaskAssignment, NOT from the idle else-branch
        Assert.Equal(1, count[0]);
    }

    // ── HandleWorkerReady without task → exactly 1 (criterion 19) ────────────

    [Fact]
    public async Task HandleWorkerReady_WithoutTask_NotifiesExactlyOnce()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);

        var worker = pool.RegisterWorker("w-idle", []);
        count[0] = 0;

        var method = typeof(HiveOrchestratorService)
            .GetMethod("HandleWorkerReady", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(service, [worker, new MockStreamWriter(), CancellationToken.None])!;

        // Exactly 1 — from the idle else-branch (no task dequeued)
        Assert.Equal(1, count[0]);
    }

    // ── Heartbeat all dict ops under lock (concurrent) (criterion 27) ────────

    [Fact]
    public async Task Heartbeat_Concurrent_NoExceptionsAndConsistentState()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);

        // Register many workers
        var workerIds = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var id = $"cw-{i}";
            pool.RegisterWorker(id, []);
            workerIds.Add(id);
        }

        service.MaxHeartbeatEntries = 5; // Force evictions during concurrent access
        count[0] = 0;

        var barrier = new Barrier(10);
        var exceptions = new List<Exception>();
        var threads = new Task[10];

        for (var t = 0; t < 10; t++)
        {
            var threadIdx = t;
            threads[t] = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        for (var j = 0; j < 200; j++)
                        {
                            var wid = workerIds[(threadIdx + j) % workerIds.Count];
                            var busy = j % 2 == 0;
                            var ctx = j % 100;
                            service.Heartbeat(
                                new HeartbeatRequest { WorkerId = wid, Busy = busy, ContextUsagePercent = ctx },
                                MockContext()).GetAwaiter().GetResult();
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        await Task.WhenAll(threads);

        Assert.Empty(exceptions);
        // The dict should never exceed MaxHeartbeatEntries (5)
        var heartbeatState = typeof(HiveOrchestratorService)
            .GetField("_heartbeatState", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service) as IDictionary<string, (DateTime, bool, int)>;
        Assert.NotNull(heartbeatState);
        Assert.True(heartbeatState!.Count <= 5,
            $"Expected at most 5 entries but found {heartbeatState.Count}");
        // Notifications should have fired many times
        Assert.True(count[0] > 0, "Expected notifications during concurrent heartbeats");
    }

    private sealed class MockStreamWriter : IServerStreamWriter<OrchestratorMessage>
    {
        public WriteOptions? WriteOptions { get; set; }

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(OrchestratorMessage message)
            => Task.CompletedTask;
    }

    /// <summary>
    /// In-memory <see cref="IAsyncStreamReader{T}"/> that yields a fixed sequence of messages
    /// and then completes, causing <c>WorkStream</c> to exit its read loop and run the
    /// <c>finally</c> block under test.
    /// </summary>
    private sealed class FakeStreamReader(IReadOnlyList<WorkerMessage> messages)
        : IAsyncStreamReader<WorkerMessage>
    {
        private int _index = -1;

        public WorkerMessage Current => messages[_index];

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<bool>(cancellationToken);

            _index++;
            return Task.FromResult(_index < messages.Count);
        }
    }
}
