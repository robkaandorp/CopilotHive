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
        hung.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([hung]);
        poolMock.Setup(p => p.TryRemoveTimedOutWorker("hung", It.IsAny<TimeSpan>())).Returns(true);
        var (svc, count) = CreateCleanup(poolMock.Object);

        await svc.RunCleanupCycleAsync();

        Assert.Equal(1, count[0]);
    }

    /// <summary>
    /// When the atomic re-check refuses the eviction (activity arrived after selection), nothing
    /// changed in the pool — so the dashboard must not be notified.
    /// </summary>
    [Fact]
    public async Task Cleanup_TimedOutWorkerStillActive_DoesNotNotify()
    {
        var hung = MakeWorker("hung");
        hung.IsBusy = true;
        hung.CurrentTaskId = "t";
        hung.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([hung]);
        poolMock.Setup(p => p.TryRemoveTimedOutWorker("hung", It.IsAny<TimeSpan>())).Returns(false);
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

    // ── WorkStream finally: ABA replacement (A→B) preserves B and B's heartbeat ──

    /// <summary>
    /// Drives the real <see cref="HiveOrchestratorService.WorkStream"/> RPC through an A→B ABA
    /// replacement: A opens a stream and is pinned as the stream's worker; A is then removed and
    /// B re-registers under the same ID; B publishes a heartbeat; A's stream ends. The finally
    /// block must NOT evict B (instance-aware removal of the stale A instance returns
    /// <c>false</c>), must NOT clean B's heartbeat entry, and must NOT notify the dashboard.
    /// </summary>
    [Fact]
    public async Task WorkStream_AbaReplacement_OldStreamEnds_ReplacementAndHeartbeatPreserved()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);

        // A registers and opens a stream. The first Ready message (idle branch) notifies; that
        // notify is the rendezvous proving the first message was fully processed and A is pinned.
        var a = pool.RegisterWorker("ws-aba", []);
        var firstMessageHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.OnStateChanged += () => firstMessageHandled.TrySetResult();

        var reader = new GatedStreamReader([
            new WorkerMessage { WorkerId = "ws-aba", Ready = new WorkerReady() },
            new WorkerMessage
            {
                WorkerId = "ws-aba",
                Progress = new TaskProgress
                {
                    TaskId = "t",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress,
                    Message = "m",
                },
            },
        ]);
        var streamTask = service.WorkStream(reader, new MockStreamWriter(), MockContext());

        // Wait until A's first message has been processed (pinnedWorker bound); the stream is
        // now parked on the gated second MoveNext.
        await firstMessageHandled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // A is removed (e.g. by cleanup) and B re-registers under the same ID.
        Assert.True(pool.RemoveWorker(a));
        var b = pool.RegisterWorker("ws-aba", []);
        Assert.Same(b, pool.GetWorker("ws-aba"));

        // B publishes a heartbeat → throttle entry created; that is the 2nd notify
        // (1st was the Ready message's idle-branch notify).
        await service.Heartbeat(Hb("ws-aba", false, 10), MockContext());
        var state = HeartbeatState(service);
        Assert.True(state.ContainsKey("ws-aba"));
        Assert.Equal(2, count[0]);

        // A's stream ends. The finally must NOT evict B, must NOT clean B's heartbeat entry,
        // and must NOT notify (the stale A instance is no longer registered).
        reader.Release();
        await streamTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Instance-aware removal of the stale A instance refuses: B is still registered.
        Assert.False(pool.RemoveWorker(a));
        Assert.Same(b, pool.GetWorker("ws-aba"));
        Assert.Equal(1, pool.ConnectedWorkerCount);
        // B's heartbeat entry survived A's stream end — the finally did not clean it.
        Assert.True(state.ContainsKey("ws-aba"), "B's heartbeat entry must survive A's stream end");
        // No extra notification from A's finally block.
        Assert.Equal(2, count[0]);
    }

    // ── WorkStream instance pinning — first-message bind, subsequent-message break ──

    /// <summary>
    /// The first message from a worker that is NOT in the pool must fail to pin: <c>GetWorker</c>
    /// returns null, the stream ends immediately (break), and the finally block must NOT remove
    /// anything (pinnedWorker is still null) or clean any heartbeat state. This is the
    /// "no instance captured" path — distinct from the ABA replacement where an instance was
    /// captured but later became stale.
    /// </summary>
    [Fact]
    public async Task WorkStream_FirstMessageFromUnknownWorker_NoPinningNoRemovalNoHeartbeatCleanup()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, count) = CreateService(notifier);

        // No worker registered for "ws-unknown".
        Assert.Null(pool.GetWorker("ws-unknown"));

        // Pre-seed heartbeat state as if a previous (now-removed) worker left an entry.
        var state = HeartbeatState(service);
        state["ws-unknown"] = (DateTime.UtcNow, false, 10);
        count[0] = 0;

        var reader = new FakeStreamReader([
            new WorkerMessage
            {
                WorkerId = "ws-unknown",
                Progress = new TaskProgress
                {
                    TaskId = "t",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress,
                    Message = "m",
                },
            },
        ]);

        await service.WorkStream(reader, new MockStreamWriter(), MockContext());

        // No instance was pinned, so the finally block must not have removed anything.
        // The heartbeat entry must survive — it was never associated with a pinned instance.
        Assert.True(state.ContainsKey("ws-unknown"),
            "Heartbeat entry must survive when no instance was pinned");
        // No notification from the finally block (nothing was removed).
        Assert.Equal(0, count[0]);
    }

    /// <summary>
    /// After the first message pins a worker, a subsequent message whose <c>GetWorker</c>
    /// resolves to null (the pinned instance was removed from the pool between messages, with no
    /// replacement) must end the stream without processing — the <c>ReferenceEquals(null,
    /// pinnedWorker)</c> check fails. The second message's Progress handler must NOT run.
    /// </summary>
    [Fact]
    public async Task WorkStream_SubsequentMessageNullPoolEntry_BreaksWithoutProcessing()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, _) = CreateService(notifier);

        var worker = pool.RegisterWorker("ws-null", []);
        var oldActivity = DateTime.UtcNow.AddMinutes(-90);
        worker.LastActivityAt = oldActivity;

        // First message is Ready (pins the worker, idle branch → no task → notify).
        // Second message is Progress. Before releasing the gate, we remove the worker so
        // GetWorker returns null on the second message → ReferenceEquals fails → break.
        var firstMessageHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.OnStateChanged += () => firstMessageHandled.TrySetResult();

        var reader = new GatedStreamReader([
            new WorkerMessage { WorkerId = "ws-null", Ready = new WorkerReady() },
            new WorkerMessage
            {
                WorkerId = "ws-null",
                Progress = new TaskProgress
                {
                    TaskId = "t",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress,
                    Message = "m",
                },
            },
        ]);
        var streamTask = service.WorkStream(reader, new MockStreamWriter(), MockContext());

        // Wait for the first message to be processed (worker pinned).
        await firstMessageHandled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Remove the worker (no replacement). GetWorker("ws-null") will return null.
        Assert.True(pool.RemoveWorker(worker));

        // Release the gate → second message arrives. GetWorker returns null → break.
        reader.Release();
        await streamTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The Progress handler must NOT have run — LastActivityAt must be unchanged.
        Assert.True(worker.LastActivityAt == oldActivity,
            "Second message must not be processed when the pinned instance is no longer in the pool");
    }

    /// <summary>
    /// After the first message pins a worker, a subsequent message whose <c>GetWorker</c>
    /// resolves to a DIFFERENT instance (ABA replacement) must end the stream without processing.
    /// This is the same break path as the null case but driven by <c>ReferenceEquals</c> returning
    /// false for a replacement instance. The second message's Progress handler must NOT run on
    /// the replacement.
    /// </summary>
    [Fact]
    public async Task WorkStream_SubsequentMessageReplacementInstance_BreaksWithoutProcessing()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, _) = CreateService(notifier);

        var a = pool.RegisterWorker("ws-repl", []);
        var oldActivityA = DateTime.UtcNow.AddMinutes(-90);
        a.LastActivityAt = oldActivityA;

        // First message is Ready (pins A, idle branch → notify).
        var firstMessageHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.OnStateChanged += () => firstMessageHandled.TrySetResult();

        var reader = new GatedStreamReader([
            new WorkerMessage { WorkerId = "ws-repl", Ready = new WorkerReady() },
            new WorkerMessage
            {
                WorkerId = "ws-repl",
                Progress = new TaskProgress
                {
                    TaskId = "t",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress,
                    Message = "m",
                },
            },
        ]);
        var streamTask = service.WorkStream(reader, new MockStreamWriter(), MockContext());

        // Wait for the first message to be processed (A pinned).
        await firstMessageHandled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Remove A and register B under the same ID (ABA replacement).
        Assert.True(pool.RemoveWorker(a));
        var b = pool.RegisterWorker("ws-repl", []);
        var oldActivityB = DateTime.UtcNow.AddMinutes(-90);
        b.LastActivityAt = oldActivityB;
        Assert.Same(b, pool.GetWorker("ws-repl"));

        // Release the gate → second message arrives. GetWorker returns B ≠ A → break.
        reader.Release();
        await streamTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The Progress handler must NOT have run on B — B's LastActivityAt is unchanged.
        Assert.True(b.LastActivityAt == oldActivityB,
            "Second message must not be processed when the pool has a replacement instance");
        // A's LastActivityAt is also unchanged (the second message never touched it).
        Assert.Equal(oldActivityA, a.LastActivityAt);
    }

    /// <summary>
    /// After the first message pins a worker, a subsequent message from the SAME pinned instance
    /// must pass the <c>ReferenceEquals</c> check and continue processing. This covers the
    /// non-break (continue) branch of the instance-pinning guard. Two Progress messages from the
    /// same worker: the second must reset <c>LastActivityAt</c>.
    /// </summary>
    [Fact]
    public async Task WorkStream_SubsequentMessageSamePinnedInstance_ContinuesProcessing()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, _) = CreateService(notifier);

        var worker = pool.RegisterWorker("ws-same", []);
        worker.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var reader = new FakeStreamReader([
            new WorkerMessage
            {
                WorkerId = "ws-same",
                Progress = new TaskProgress
                {
                    TaskId = "t1",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress,
                    Message = "first",
                },
            },
            new WorkerMessage
            {
                WorkerId = "ws-same",
                Progress = new TaskProgress
                {
                    TaskId = "t2",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress,
                    Message = "second",
                },
            },
        ]);

        await service.WorkStream(reader, new MockStreamWriter(), MockContext());

        // Both messages were processed: LastActivityAt was reset by TouchActivity on each.
        Assert.True(DateTime.UtcNow - worker.LastActivityAt < TimeSpan.FromSeconds(5),
            "Both messages must have been processed (LastActivityAt reset to ~now)");
    }

    // ── WorkStream activity → LastActivityAt (activity-based stale detection) ──

    /// <summary>
    /// A ToolRequest message is task-specific stream activity: it must reset
    /// <see cref="ConnectedWorker.LastActivityAt"/> so the worker is not reclaimed.
    /// </summary>
    [Fact]
    public async Task WorkStream_ToolRequest_ResetsLastActivityAt()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, _) = CreateService(notifier);
        var worker = pool.RegisterWorker("ws-tool", []);
        worker.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var reader = new FakeStreamReader([
            new WorkerMessage
            {
                WorkerId = "ws-tool",
                ToolRequest = new ToolCallRequest
                {
                    RequestId = "r1",
                    TaskId = "t",
                    ToolName = "unknown-tool",
                    ArgumentsJson = "{}",
                },
            },
        ]);

        await service.WorkStream(reader, new MockStreamWriter(), MockContext());

        Assert.True(DateTime.UtcNow - worker.LastActivityAt < TimeSpan.FromSeconds(5),
            "ToolRequest must reset LastActivityAt to ~now");
    }

    /// <summary>
    /// A Progress message is task-specific stream activity: it must reset
    /// <see cref="ConnectedWorker.LastActivityAt"/> so the worker is not reclaimed.
    /// The update must go through the pool's synchronized activity authority, so the worker
    /// immediately stops being an inactivity-reclamation candidate.
    /// </summary>
    [Fact]
    public async Task WorkStream_Progress_ResetsLastActivityAt()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, _) = CreateService(notifier);
        var worker = pool.RegisterWorker("ws-progress", []);
        pool.MarkBusy("ws-progress", "t");
        worker.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);
        // Precondition: the worker is currently a reclamation candidate.
        Assert.Single(pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60)));

        var reader = new FakeStreamReader([
            new WorkerMessage
            {
                WorkerId = "ws-progress",
                Progress = new TaskProgress
                {
                    TaskId = "t",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress,
                    Message = "m",
                },
            },
        ]);

        await service.WorkStream(reader, new MockStreamWriter(), MockContext());

        Assert.True(DateTime.UtcNow - worker.LastActivityAt < TimeSpan.FromSeconds(5),
            "Progress must reset LastActivityAt to ~now");
        // The refreshed timestamp is no longer past the timeout, so the worker would not be
        // selected for inactivity-based reclamation.
        Assert.False(DateTime.UtcNow - worker.LastActivityAt > TimeSpan.FromMinutes(60));
    }

    /// <summary>
    /// A Complete message is task-specific stream activity: it must reset
    /// <see cref="ConnectedWorker.LastActivityAt"/> so the worker is not reclaimed.
    /// </summary>
    [Fact]
    public async Task WorkStream_Complete_ResetsLastActivityAt()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, _) = CreateService(notifier);
        var worker = pool.RegisterWorker("ws-complete", []);
        worker.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var reader = new FakeStreamReader([
            new WorkerMessage
            {
                WorkerId = "ws-complete",
                Complete = new CopilotHive.Shared.Grpc.TaskComplete
                {
                    TaskId = "t",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.Completed,
                    Output = "done",
                },
            },
        ]);

        await service.WorkStream(reader, new MockStreamWriter(), MockContext());

        Assert.True(DateTime.UtcNow - worker.LastActivityAt < TimeSpan.FromSeconds(5),
            "Complete must reset LastActivityAt to ~now");
    }

    /// <summary>
    /// A Ready message is NOT task-specific stream activity: it must NOT reset
    /// <see cref="ConnectedWorker.LastActivityAt"/>.
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_DoesNotResetLastActivityAt()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, _) = CreateService(notifier);
        var worker = pool.RegisterWorker("ws-ready", []);
        var oldActivity = DateTime.UtcNow.AddMinutes(-90);
        worker.LastActivityAt = oldActivity;

        var reader = new FakeStreamReader([
            new WorkerMessage
            {
                WorkerId = "ws-ready",
                Ready = new WorkerReady(),
            },
        ]);

        await service.WorkStream(reader, new MockStreamWriter(), MockContext());

        Assert.Equal(oldActivity, worker.LastActivityAt);
    }

    /// <summary>
    /// An unknown / <see cref="WorkerMessage.PayloadOneofCase.None"/> message is NOT
    /// task-specific stream activity: it must NOT reset <see cref="ConnectedWorker.LastActivityAt"/>.
    /// </summary>
    [Fact]
    public async Task WorkStream_UnknownPayload_DoesNotResetLastActivityAt()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, _) = CreateService(notifier);
        var worker = pool.RegisterWorker("ws-none", []);
        var oldActivity = DateTime.UtcNow.AddMinutes(-90);
        worker.LastActivityAt = oldActivity;

        // No payload set → PayloadOneofCase.None → default/unknown branch
        var reader = new FakeStreamReader([
            new WorkerMessage { WorkerId = "ws-none" },
        ]);

        await service.WorkStream(reader, new MockStreamWriter(), MockContext());

        Assert.Equal(oldActivity, worker.LastActivityAt);
    }

    /// <summary>
    /// A heartbeat must NOT update <see cref="ConnectedWorker.LastActivityAt"/>: a worker
    /// that heartbeats but sends no task-specific stream messages is still reclaimed.
    /// </summary>
    [Fact]
    public async Task Heartbeat_DoesNotUpdateLastActivityAt_WorkerStillReclaimed()
    {
        var notifier = new DashboardNotifier();
        var (service, pool, _, _) = CreateService(notifier);
        var worker = pool.RegisterWorker("ws-hb", []);
        pool.MarkBusy("ws-hb", "task-hb");
        var oldActivity = DateTime.UtcNow.AddMinutes(-90);
        worker.LastActivityAt = oldActivity;

        await service.Heartbeat(
            new HeartbeatRequest { WorkerId = "ws-hb", Busy = true, ContextUsagePercent = 10 },
            MockContext());

        // Heartbeat must not count as task activity.
        Assert.Equal(oldActivity, worker.LastActivityAt);

        // The worker is still reclaimed by the inactivity-based timeout.
        var timedOut = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));
        var only = Assert.Single(timedOut);
        Assert.Equal("ws-hb", only.Id);
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

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(
            OrchestratorMessage message, CancellationToken cancellationToken)
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

    /// <summary>
    /// In-memory <see cref="IAsyncStreamReader{T}"/> that yields the first message immediately,
    /// then blocks the second <c>MoveNext</c> on a gate until <see cref="Release"/> is called,
    /// after which it completes. Used to interleave external events (re-registration, heartbeats)
    /// between the stream's messages.
    /// </summary>
    private sealed class GatedStreamReader(IReadOnlyList<WorkerMessage> messages)
        : IAsyncStreamReader<WorkerMessage>
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _index = -1;

        public WorkerMessage Current => messages[_index];

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<bool>(cancellationToken);

            _index++;
            if (_index >= messages.Count)
                return Task.FromResult(false);

            // First message passes immediately; subsequent ones wait for the gate.
            if (_index == 0)
                return Task.FromResult(true);

            return _gate.Task.ContinueWith(
                _ => true,
                cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>Releases the gate, letting the next <c>MoveNext</c> complete.</summary>
        public void Release() => _gate.TrySetResult();
    }
}
