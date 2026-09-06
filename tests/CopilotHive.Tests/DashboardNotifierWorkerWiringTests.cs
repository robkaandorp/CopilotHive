using System.Collections.Concurrent;
using System.Reflection;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
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

    // ══════════════════════════════════════════════════════════════════════════════════
    //  TRANSPORT OWNS NO PIPELINE MUTATION — the real-chain regression coverage.
    //
    //  HandleTaskComplete used to resolve the pipeline and, BEFORE the completion guards ran,
    //  unconditionally clear its active-task pointer and (for a non-Unspecified worker role and
    //  a non-Failed status) assign CurrentPhaseEntry.WorkerOutput. A duplicate completion for a
    //  task whose task→goal mapping is still registered therefore corrupted the SUCCESSOR's
    //  state even though the domain path subsequently rejected it.
    //
    //  Those mutations now belong exclusively to the admitted completion path. The vectors
    //  below drive the REAL chain — HiveOrchestratorService.WorkStream → TaskCompletionNotifier
    //  → GoalDispatcher.HandleTaskCompletionAsync → TaskCompletionService → PipelineDriver →
    //  TaskDispatchService.
    //
    //  WHICH VECTOR PROVES WHAT — stated precisely, because they are NOT interchangeable:
    //    • WorkStream_DuplicateCompletionWithRetainedMapping_… is THE OLD-BLOCK DETECTOR. It is
    //      the only vector here that goes red when the deleted transport block is restored,
    //      because it is the only one in which a LIVE successor pointer and a successor phase
    //      entry exist at the moment the duplicate arrives.
    //    • WorkStream_DuplicateCompletionDuringAdmittedDrive_… proves REGISTERED-SLOT DUPLICATE
    //      REJECTION during the Claimed/null-pointer window. It does NOT detect restoration of
    //      the old block — see its own remarks for why — and makes no such claim.
    //    • WorkStream_NoBrainCompletionFromUnspecifiedRoleWorker_… pins the deliberate
    //      normalization at the transport boundary.
    //
    //  THE ASYNC CONTRACT. HandleTaskComplete schedules the notification on Task.Run, so neither
    //  the dashboard notification nor the stream's own progress means the DOMAIN handler has
    //  finished. The harness therefore installs exactly ONE subscriber on the transport notifier
    //  which AWAITS the real dispatcher handler and only then completes that delivery's
    //  TaskCompletionSource (RunContinuationsAsynchronously, exceptions propagated). The
    //  dispatcher gets its OWN notifier so it is never double-subscribed, and no signal-only
    //  subscriber is appended — NotifyAsync awaits the LAST subscriber's task only. Because
    //  WorkStream never awaits those callbacks, RealTransportHarness.StopAsync drains EVERY
    //  registered delivery, not just the stream.
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE RETAINED-MAPPING DUPLICATE — and THE OLD-BLOCK DETECTOR. A first completion (A) is
    /// admitted, drives the pipeline and dispatches a real successor (B) which owns a registered
    /// slot and the active pointer. A's task→goal mapping is deliberately left registered —
    /// exactly what production leaves behind — so a duplicate A still RESOLVES this pipeline at
    /// the transport boundary.
    /// <para>
    /// Delivering that duplicate must change NOTHING: B keeps its pointer, its sentinel phase
    /// output, its phase and its slot state, and no additional drive or dispatch happens.
    /// </para>
    /// <para>
    /// THIS IS THE VECTOR THAT KILLS RESTORATION OF THE DELETED TRANSPORT BLOCK, and the only
    /// one of the three that does. The preconditions are what make it work: B's dispatch left a
    /// LIVE active pointer for the old block's unconditional <c>ClearActiveTask</c> to erase,
    /// and a LIVE successor phase entry for its role-gated <c>WorkerOutput</c> assignment to
    /// overwrite (the duplicate is delivered by a worker whose Role was re-assigned by B's
    /// dispatch, so the role gate is open). Both halves are asserted independently below.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorkStream_DuplicateCompletionWithRetainedMapping_LeavesSuccessorStateIntact()
    {
        var harness = await RealTransportHarness.CreateAsync();
        // Set only on the success path: a cleanup failure must never REPLACE a body failure.
        var bodySucceeded = false;
        try
        {
            // ── A is dispatched through the REAL dispatch service ────────────────────────
            var taskA = await harness.DispatchFirstPhaseAsync();
            Assert.Equal(taskA, harness.Pipeline.ActiveTaskId);
            Assert.Equal([taskA], harness.Dispatched);

            // Production's DispatchPhaseAsync appends the phase entry; the manual first
            // dispatch mirrors that so the driver has an entry to record A's output into.
            harness.Pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, harness.Pipeline.Iteration, 1));

            // ── A's completion is delivered through the real transport and fully handled ──
            await harness.DeliverCompletionAsync(taskA, output: "A-RAW-OUTPUT", summary: "A-SUMMARY");

            // A's accepted output used the SUMMARY preference (the brain-driven driver owns it).
            var codingEntry = Assert.Single(harness.Pipeline.PhaseLog, e => e.Name == GoalPhase.Coding);
            Assert.Equal("A-SUMMARY", codingEntry.WorkerOutput);

            // The successor really was dispatched by production code.
            Assert.Equal(2, harness.Dispatched.Count);
            var taskB = harness.Dispatched[1];
            Assert.NotEqual(taskA, taskB);
            Assert.Equal(taskB, harness.Pipeline.ActiveTaskId);
            Assert.Equal(GoalPhase.DocWriting, harness.Pipeline.Phase);
            var slotBBefore = SlotStateName(harness.Pipeline, taskB);
            Assert.NotNull(slotBBefore);

            // A's mapping is RETAINED — the precondition that makes the duplicate resolvable.
            Assert.Equal(harness.Pipeline.GoalId, harness.PipelineManager.GetByTaskId(taskA)?.GoalId);

            // ── The sentinel on B's phase entry ───────────────────────────────────────────
            var successorEntry = harness.Pipeline.CurrentPhaseEntry;
            Assert.NotNull(successorEntry);
            Assert.Equal(GoalPhase.DocWriting, successorEntry!.Name);
            successorEntry.WorkerOutput = SuccessorSentinel;

            var craftCallsBefore = harness.Brain.CraftPromptCalls;

            // ── THE DUPLICATE: same task id, DIFFERENT output ────────────────────────────
            await harness.DeliverCompletionAsync(
                taskA, output: "DUPLICATE-RAW-OUTPUT", summary: "DUPLICATE-SUMMARY");

            // NOTHING of the successor's state moved. The OUTPUT is asserted first and the
            // POINTER second: these are the two halves of the deleted transport block, and each
            // line independently goes red when that block is restored (verified by mutation).
            Assert.Equal(SuccessorSentinel, successorEntry.WorkerOutput);
            Assert.Equal(taskB, harness.Pipeline.ActiveTaskId);
            Assert.Equal(GoalPhase.DocWriting, harness.Pipeline.Phase);
            Assert.Equal(GoalPhase.DocWriting, harness.Pipeline.StateMachine.Phase);
            Assert.Equal(slotBBefore, SlotStateName(harness.Pipeline, taskB));
            Assert.Same(successorEntry, harness.Pipeline.CurrentPhaseEntry);

            // No extra drive and no extra dispatch.
            Assert.Equal(craftCallsBefore, harness.Brain.CraftPromptCalls);
            Assert.Equal(2, harness.Dispatched.Count);

            // A's own (accepted) output is likewise untouched by the duplicate.
            Assert.Equal("A-SUMMARY", codingEntry.WorkerOutput);

            // ── The real sequential handoff still works: B completes and C is dispatched ──
            // B carries NO summary, so this delivery also pins the driver's OUTPUT FALLBACK.
            await harness.DeliverCompletionAsync(taskB, output: "B-RAW-OUTPUT", summary: "");

            Assert.Equal("B-RAW-OUTPUT", successorEntry.WorkerOutput);
            Assert.Equal(3, harness.Dispatched.Count);
            Assert.Equal(harness.Dispatched[2], harness.Pipeline.ActiveTaskId);
            Assert.Equal(GoalPhase.Testing, harness.Pipeline.Phase);
            bodySucceeded = true;
        }
        finally
        {
            await harness.StopAsync(throwOnCleanupFailure: bodySucceeded);
        }
    }

    /// <summary>
    /// THE IN-FLIGHT DUPLICATE, deterministically gated. While A's ADMITTED drive is parked
    /// before its successor dispatch, A's slot is <c>Claimed</c> and the active pointer is
    /// <c>null</c> (the completion path released it). A duplicate A delivered in that window is
    /// refused by the REGISTERED-SLOT duplicate protection — the legacy no-slot pass-through is
    /// deliberately NOT broadened.
    /// <para>
    /// The gate is a fake Brain already reachable through the real chain
    /// (<c>PipelineDriver</c> → <c>ResolvePromptAsync</c> → <c>CraftPromptAsync</c>): no timing
    /// sleeps and no new production seam.
    /// </para>
    /// </summary>
    /// <remarks>
    /// WHAT THIS VECTOR DOES AND DOES NOT PROVE — stated honestly, because the distinction is
    /// easy to get wrong.
    /// <list type="bullet">
    ///   <item><description>IT PROVES the admission's <c>SlotAlreadyAdmitted</c> rejection of a
    ///     duplicate whose slot is <c>Claimed</c>, and that nothing is written during the
    ///     Claimed/null-pointer window. Deleting that guard makes this vector red, as does
    ///     moving the no-brain output copy ahead of the guards.</description></item>
    ///   <item><description>IT DOES NOT detect restoration of the deleted transport block, and
    ///     must not be cited as if it did. Both halves of that block are inert here: A's FIRST
    ///     completion already ran <c>ApplyTaskCompletion</c> → <c>WorkerPool.MarkIdle</c>, which
    ///     resets the worker's Role to <c>Unspecified</c>, so the old block's role-gated output
    ///     write is disabled by the time the duplicate arrives; and A's pointer is ALREADY
    ///     <c>null</c> (released by the completion path before the gate), so the old block's
    ///     unconditional <c>ClearActiveTask</c> is a no-op. The old-block evidence belongs to
    ///     <see cref="WorkStream_DuplicateCompletionWithRetainedMapping_LeavesSuccessorStateIntact"/>,
    ///     which has a live successor pointer and phase entry to corrupt. Do NOT try to force
    ///     old-block detection into this vector — that would mean abandoning the very
    ///     Claimed/null-pointer window it exists to cover.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task WorkStream_DuplicateCompletionDuringAdmittedDrive_IsRefusedAndMutatesNothing()
    {
        var harness = await RealTransportHarness.CreateAsync();
        // Set only on the success path: a cleanup failure must never REPLACE a body failure.
        var bodySucceeded = false;
        try
        {
            var taskA = await harness.DispatchFirstPhaseAsync();
            harness.Pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, harness.Pipeline.Iteration, 1));

            // Park the FIRST prompt craft — i.e. A's drive, after the phase advance and the new
            // phase entry, but BEFORE the successor dispatch.
            harness.Brain.GateFirstCraftPrompt();

            var firstDelivery = harness.BeginCompletionDelivery(
                taskA, output: "A-RAW-OUTPUT", summary: "A-SUMMARY");

            await harness.Brain.CraftPromptEntered.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // THE WINDOW, proven: the pointer is released and A's slot is Claimed.
            Assert.Null(harness.Pipeline.ActiveTaskId);
            Assert.Equal("Claimed", SlotStateName(harness.Pipeline, taskA));

            var pendingEntry = harness.Pipeline.CurrentPhaseEntry;
            Assert.NotNull(pendingEntry);
            pendingEntry!.WorkerOutput = SuccessorSentinel;

            var dispatchedBefore = harness.Dispatched.Count;
            var craftCallsBefore = harness.Brain.CraftPromptCalls;

            // ── THE DUPLICATE, delivered inside the window and awaited to REJECTION ───────
            await harness.DeliverCompletionAsync(
                taskA, output: "DUPLICATE-RAW-OUTPUT", summary: "DUPLICATE-SUMMARY");

            // The registered-slot duplicate protection refused it: no output write (the
            // admitted copy sits behind the admission), no pointer change, no slot movement,
            // and neither a drive nor a dispatch happened.
            Assert.Equal(SuccessorSentinel, pendingEntry.WorkerOutput);
            Assert.Null(harness.Pipeline.ActiveTaskId);
            Assert.Equal("Claimed", SlotStateName(harness.Pipeline, taskA));
            Assert.Equal(dispatchedBefore, harness.Dispatched.Count);
            Assert.Equal(craftCallsBefore, harness.Brain.CraftPromptCalls);

            // ── Release the parked drive and let ALL the work finish ─────────────────────
            harness.Brain.ReleaseCraftPrompt();
            await firstDelivery.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            Assert.Equal(dispatchedBefore + 1, harness.Dispatched.Count);
            Assert.Equal(harness.Dispatched[^1], harness.Pipeline.ActiveTaskId);
            bodySucceeded = true;
        }
        finally
        {
            await harness.StopAsync(throwOnCleanupFailure: bodySucceeded);
        }
    }

    /// <summary>
    /// THE NORMALIZATION'S TRANSPORT HALF. A worker that never took a role through
    /// <c>HandleWorkerReady</c> is still <see cref="WorkerRole.Unspecified"/> when its completion
    /// arrives — the exact case the deleted transport block SKIPPED. The admitted no-brain copy
    /// carries no role concept at all, so the output is now written regardless. This is the
    /// deliberate semantic normalization, pinned at the real transport boundary (its direct
    /// domain-caller twin lives in <c>GoalDispatcherTests</c>).
    /// </summary>
    [Fact]
    public async Task WorkStream_NoBrainCompletionFromUnspecifiedRoleWorker_StillWritesPhaseOutput()
    {
        var harness = await RealTransportHarness.CreateAsync(withBrain: false);
        // Set only on the success path: a cleanup failure must never REPLACE a body failure.
        var bodySucceeded = false;
        try
        {
            var taskA = await harness.DispatchFirstPhaseAsync();
            var entry = PhaseResult.Create(GoalPhase.Coding, harness.Pipeline.Iteration, 1);
            entry.WorkerOutput = SuccessorSentinel;
            harness.Pipeline.PhaseLog.Add(entry);

            // The delivering worker carries NO role — the state a worker is left in by
            // WorkerPool.MarkIdle (which resets Role to Unspecified) and the state a freshly
            // reconnected worker is in. The deleted transport block read exactly this field and
            // SKIPPED the output write for it; the admitted copy has no role concept at all.
            harness.Worker.Role = WorkerRole.Unspecified;
            Assert.Equal(WorkerRole.Unspecified, harness.Worker.Role);

            await harness.DeliverCompletionAsync(taskA, output: "UNSPECIFIED-OUTPUT", summary: "");

            Assert.Equal("UNSPECIFIED-OUTPUT", entry.WorkerOutput);
            // The no-brain lifecycle/slot contract is unchanged: goal Done, slot left Claimed.
            Assert.Equal(GoalPhase.Done, harness.Pipeline.Phase);
            Assert.Equal("Claimed", SlotStateName(harness.Pipeline, taskA));
            bodySucceeded = true;
        }
        finally
        {
            await harness.StopAsync(throwOnCleanupFailure: bodySucceeded);
        }
    }

    /// <summary>An identifiable phase output that no rejected completion may overwrite.</summary>
    private const string SuccessorSentinel = "SENTINEL-SUCCESSOR-PHASE-OUTPUT";

    /// <summary>Upper bound for every await in the transport vectors — never a fixed delay.</summary>
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The registered slot's state for <paramref name="taskId"/> rendered as a string, or
    /// <c>null</c> when no slot exists. <c>WorkSlotState</c> is internal to the production
    /// assembly's test surface; the name keeps the assertion readable without leaking the type.
    /// </summary>
    private static string? SlotStateName(GoalPipeline pipeline, string taskId) =>
        pipeline.GetSlotsForTest().FirstOrDefault(v => v.Slot.TaskId == taskId)?.State.ToString();

    /// <summary>
    /// The REAL transport → domain chain: a live <see cref="HiveOrchestratorService"/> streaming
    /// worker messages into a real <see cref="GoalDispatcher"/> (with its real completion
    /// service, pipeline driver and dispatch service) over a real pipeline.
    /// </summary>
    private sealed class RealTransportHarness
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _deliveries = new();

        public required HiveOrchestratorService Service { get; init; }
        public required GoalPipelineManager PipelineManager { get; init; }
        public required GoalDispatcher Dispatcher { get; init; }
        public required GoalPipeline Pipeline { get; init; }
        public required GatedTransportBrain Brain { get; init; }
        public required List<string> Dispatched { get; init; }
        public required string WorkerId { get; init; }
        public required ConnectedWorker Worker { get; init; }
        public required ChannelStreamReader Reader { get; init; }
        public required Task StreamTask { get; init; }

        public static async Task<RealTransportHarness> CreateAsync(bool withBrain = true)
        {
            var goal = new Goal
            {
                Id = $"goal-transport-{Guid.NewGuid():N}",
                Description = "Transport mutation-ownership goal",
                RepositoryNames = ["test-repo"],
            };

            var goalManager = new GoalManager();
            goalManager.AddSource(new TransportGoalSource(goal));
            await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

            var config = new HiveConfigFile
            {
                Repositories =
                {
                    new RepositoryConfig
                    {
                        Name = "test-repo",
                        Url = "https://example.com/test-repo.git",
                        DefaultBranch = "develop",
                    },
                },
            };
            foreach (var roleName in new[] { "coder", "docwriter", "tester", "reviewer" })
                config.Workers[roleName] = new WorkerConfig { Model = $"{roleName}-model" };

            var pool = new WorkerPool();
            var taskQueue = new TaskQueue();
            var pipelineManager = new GoalPipelineManager();
            // The brain is what selects the two production output owners: with a brain the
            // PipelineDriver records the phase output; without one the completion service's
            // no-brain copy does.
            var brain = new GatedTransportBrain();

            // TWO notifiers: the dispatcher subscribes to its own (never fired here), so the
            // transport notifier carries exactly ONE subscriber — the test's awaiting handler.
            var transportNotifier = new TaskCompletionNotifier();
            var dispatcherNotifier = new TaskCompletionNotifier();

            var dispatcher = new GoalDispatcher(
                goalManager,
                pipelineManager,
                taskQueue,
                new GrpcWorkerGateway(pool),
                dispatcherNotifier,
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
                withBrain ? brain : null,
                config);

            var service = new HiveOrchestratorService(
                pool,
                taskQueue,
                pipelineManager,
                transportNotifier,
                dispatcher,
                NullLogger<HiveOrchestratorService>.Instance);

            var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
            var plan = IterationPlan.Default();
            pipeline.SetPlan(plan);
            pipeline.StateMachine.StartIteration(plan.Phases);
            pipeline.AdvanceTo(GoalPhase.Coding);

            var dispatched = new List<string>();
            taskQueue.OnEnqueue = t => dispatched.Add(t.TaskId);

            const string workerId = "transport-worker";
            var worker = pool.RegisterWorker(workerId, []);

            var reader = new ChannelStreamReader();
            var streamTask = service.WorkStream(reader, new MockStreamWriter(), MockContext());

            var harness = new RealTransportHarness
            {
                Service = service,
                PipelineManager = pipelineManager,
                Dispatcher = dispatcher,
                Pipeline = pipeline,
                Brain = brain,
                Dispatched = dispatched,
                WorkerId = workerId,
                Worker = worker,
                Reader = reader,
                StreamTask = streamTask,
            };

            // THE SOLE SUBSCRIBER: it awaits the REAL dispatcher completion handler and only
            // then completes this delivery's TCS. Exceptions propagate to the awaiting test.
            transportNotifier.OnTaskCompleted += async result =>
            {
                var delivery = harness._deliveries[result.Output];
                try
                {
                    await harness.Dispatcher.HandleTaskCompletionAsync(result);
                    delivery.TrySetResult();
                }
                catch (Exception ex)
                {
                    delivery.TrySetException(ex);
                    throw;
                }
            };

            return harness;
        }

        /// <summary>
        /// Dispatches the pipeline's first phase through the REAL
        /// <c>TaskDispatchService.DispatchToRole</c> (reached via the dispatcher's private
        /// forwarder) and returns the task id it claimed.
        /// </summary>
        public async Task<string> DispatchFirstPhaseAsync()
        {
            var method = typeof(GoalDispatcher).GetMethod(
                "DispatchToRole", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)method.Invoke(
                Dispatcher,
                [Pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken])!;

            var taskId = Pipeline.ActiveTaskId;
            Assert.NotNull(taskId);
            return taskId!;
        }

        /// <summary>
        /// Pushes a completion onto the worker stream and returns the task that completes when
        /// this delivery's DOMAIN handling has finished. The per-delivery key is the output
        /// text, so overlapping deliveries for the SAME task id stay unambiguous.
        /// </summary>
        /// <remarks>
        /// EVERY delivery issued here is registered in <c>_deliveries</c> and is therefore
        /// drained by <see cref="StopAsync"/>, including deliveries a failing assertion never
        /// got round to awaiting.
        /// </remarks>
        public Task BeginCompletionDelivery(string taskId, string output, string summary)
        {
            var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_deliveries.TryAdd(output, delivery), "Delivery outputs must be unique");

            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = new CopilotHive.Shared.Grpc.TaskComplete
                {
                    TaskId = taskId,
                    Status = CopilotHive.Shared.Grpc.TaskStatus.Completed,
                    Output = output,
                    GitStatus = new GitStatus { FilesChanged = 2, Pushed = true },
                    Metrics = new CopilotHive.Shared.Grpc.TaskMetrics { Verdict = "PASS", Summary = summary },
                },
            });

            return delivery.Task;
        }

        /// <summary>Delivers a completion and awaits its domain handling, bounded.</summary>
        public Task DeliverCompletionAsync(string taskId, string output, string summary) =>
            BeginCompletionDelivery(taskId, output, summary)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        /// <summary>
        /// THE TEARDOWN, and it must drain MORE than the stream. <c>HandleTaskComplete</c>
        /// schedules the notification on <c>Task.Run</c> and <c>WorkStream</c> never awaits that
        /// callback — the very async-contract distinction these vectors exist to pin. So when an
        /// assertion fails (or a bounded wait expires) while a drive is still gated, ending the
        /// stream alone would let this method return while a DETACHED domain handler is still
        /// running against the pipeline the next test builds.
        /// <para>
        /// The order is deliberate: (1) release the Brain gate so any parked drive can finish,
        /// (2) end the stream and await it, then (3) await EVERY registered per-delivery task —
        /// including ones the test body never awaited. Every wait is BOUNDED (a hung callback
        /// fails fast instead of hanging the suite) and uses <c>CancellationToken.None</c>, since
        /// the test's own token may already be cancelled on the failure path.
        /// </para>
        /// <para>
        /// Callback faults and timeouts are ALWAYS observed here (awaited and caught), so nothing
        /// is left running or unobserved. They are only RETHROWN when
        /// <paramref name="throwOnCleanupFailure"/> is <c>true</c> — i.e. when the test body
        /// itself succeeded. A cleanup throw from a <c>finally</c> would otherwise REPLACE the
        /// original assertion failure and hide the real defect.
        /// </para>
        /// </summary>
        /// <param name="throwOnCleanupFailure">
        /// <c>true</c> only when the test body completed without throwing.
        /// </param>
        public async Task StopAsync(bool throwOnCleanupFailure)
        {
            var cleanupFailures = new List<Exception>();

            // (1) Unblock anything parked on the deterministic gate, so a gated drive cannot
            // keep a domain handler alive past this point.
            Brain.ReleaseCraftPrompt();

            // (2) End and drain the stream itself.
            Reader.Complete();
            try
            {
                await StreamTask.WaitAsync(BoundedWait, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // The stream's own cancellation on teardown is not a test failure.
            }
            catch (TimeoutException ex)
            {
                cleanupFailures.Add(new TimeoutException("The WorkStream did not drain within the bound.", ex));
            }

            // (3) THE DETACHED DELIVERIES. Awaiting the per-delivery TCS is what makes the
            // domain handler's completion observable — the stream never exposes it.
            foreach (var (output, delivery) in _deliveries)
            {
                try
                {
                    await delivery.Task.WaitAsync(BoundedWait, CancellationToken.None);
                }
                catch (TimeoutException ex)
                {
                    cleanupFailures.Add(new TimeoutException(
                        $"The domain handler for delivery '{output}' did not finish within the bound.", ex));
                }
                catch (Exception ex)
                {
                    // The handler faulted. Observe it — never leave it as an unobserved fault.
                    cleanupFailures.Add(new InvalidOperationException(
                        $"The domain handler for delivery '{output}' faulted.", ex));
                }
            }

            if (throwOnCleanupFailure && cleanupFailures.Count > 0)
                throw new AggregateException("Transport harness cleanup failed.", cleanupFailures);
        }
    }

    /// <summary>
    /// In-memory <see cref="IAsyncStreamReader{T}"/> backed by an unbounded channel: the test
    /// pushes messages whenever it likes and ends the stream with <see cref="Complete"/>.
    /// </summary>
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

    /// <summary>
    /// Minimal <see cref="IGoalSource"/> that returns a single pre-configured goal, so the real
    /// lifecycle service can persist status updates for the transport harness's pipeline.
    /// </summary>
    private sealed class TransportGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "transport-test-source";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// A real-chain Brain for the transport vectors: it supplies the default plan and a
    /// phase-labelled prompt so <c>PipelineDriver</c> advances and re-dispatches for real, and it
    /// carries a DETERMINISTIC GATE on <see cref="CraftPromptAsync"/>.
    /// </summary>
    /// <remarks>
    /// The gate is the in-flight window's synchronization point and needs no production seam: the
    /// driver reaches this call AFTER the phase advance and the new phase entry, but BEFORE the
    /// successor dispatch. Parking here therefore parks an ADMITTED drive exactly where the
    /// duplicate must be refused.
    /// </remarks>
    private sealed class GatedTransportBrain : IDistributedBrain
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _gateArmed;
        private int _craftPromptCalls;

        /// <summary>Number of prompt crafts performed — the drive counter for the assertions.</summary>
        public int CraftPromptCalls => Volatile.Read(ref _craftPromptCalls);

        /// <summary>Completes when the gated craft call has been entered.</summary>
        public Task CraftPromptEntered => _entered.Task;

        /// <summary>Arms the gate so the NEXT craft call parks until released.</summary>
        public void GateFirstCraftPrompt() => Interlocked.Exchange(ref _gateArmed, 1);

        /// <summary>Releases a parked craft call. Safe to call when nothing is parked.</summary>
        public void ReleaseCraftPrompt() => _release.TrySetResult();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(
            string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public async Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _craftPromptCalls);

            if (Interlocked.Exchange(ref _gateArmed, 0) == 1)
            {
                _entered.TrySetResult();
                await _release.Task;
            }

            return PromptResult.Success($"Work on {pipeline.Description} as {phase}");
        }

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>("message");

        public Task EnsureBrainRepoAsync(
            string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) => Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }
}
