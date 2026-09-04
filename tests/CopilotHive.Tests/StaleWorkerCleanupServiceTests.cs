using CopilotHive.Git;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace CopilotHive.Tests;

/// <summary>Unit tests for <see cref="StaleWorkerCleanupService"/>.</summary>
public sealed class StaleWorkerCleanupServiceTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ConnectedWorker MakeWorker(string id) => new()
    {
        Id = id,
        Role = WorkerRole.Coder,
        Capabilities = [],
    };

    /// <summary>
    /// Creates an <see cref="IWorkerPool"/> mock that honours the interface contract: collection
    /// returning members yield an empty list rather than <c>null</c> unless a test overrides them.
    /// </summary>
    private static Mock<IWorkerPool> MakePoolMock()
    {
        var mock = new Mock<IWorkerPool>();
        mock.Setup(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>())).Returns([]);
        mock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([]);
        return mock;
    }

    private static StaleWorkerCleanupService CreateService(        IWorkerPool pool,
        ILogger<StaleWorkerCleanupService>? logger = null)
    {
        logger ??= Mock.Of<ILogger<StaleWorkerCleanupService>>();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        return new StaleWorkerCleanupService(pool, taskQueue, pipelineManager, logger);
    }

    // ── (a) No stale workers → nothing removed ────────────────────────────────

    [Fact]
    public async Task RunCleanupCycle_NoStaleWorkers_PurgeCalledWithEmptyResult()
    {
        var poolMock = MakePoolMock();
        poolMock
            .Setup(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>()))
            .Returns([]);

        var svc = CreateService(poolMock.Object);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>()), Times.Once);
    }

    // ── (b) One stale worker → removed and warning logged ─────────────────────

    [Fact]
    public async Task RunCleanupCycle_OneStaleWorker_RemovedAndWarningLogged()
    {
        var staleWorker = MakeWorker("worker-1");
        var poolMock = MakePoolMock();
        poolMock
            .Setup(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>()))
            .Returns([staleWorker]);

        var loggerMock = new Mock<ILogger<StaleWorkerCleanupService>>();

        var svc = CreateService(poolMock.Object, loggerMock.Object);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>()), Times.Once);
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("worker-1")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ── (c) Multiple stale workers → all removed ──────────────────────────────

    [Fact]
    public async Task RunCleanupCycle_MultipleStaleWorkers_AllRemoved()
    {
        var workers = new[]
        {
            MakeWorker("worker-a"),
            MakeWorker("worker-b"),
            MakeWorker("worker-c"),
        };

        var poolMock = MakePoolMock();
        poolMock
            .Setup(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>()))
            .Returns(workers);

        var loggerMock = new Mock<ILogger<StaleWorkerCleanupService>>();
        var svc = CreateService(poolMock.Object, loggerMock.Object);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>()), Times.Once);
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(3));
    }

    // ── (d) PurgeStaleWorkers called with the correct timeout ─────────────────

    [Fact]
    public async Task RunCleanupCycle_CallsPurgeStaleWorkers_WithCorrectTimeout()
    {
        var expectedTimeout = TimeSpan.FromMinutes(CleanupDefaults.StaleTimeoutMinutes);

        var poolMock = MakePoolMock();
        poolMock
            .Setup(p => p.PurgeStaleWorkers(expectedTimeout))
            .Returns([])
            .Verifiable();

        var svc = CreateService(poolMock.Object);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.PurgeStaleWorkers(expectedTimeout), Times.Once);
    }

    // ── (e) Exception from cleanup cycle is caught and logged, not rethrown ───

    [Fact]
    public async Task ExecuteAsync_WhenCleanupThrows_ExceptionCaughtAndErrorLogged()
    {
        var poolMock = MakePoolMock();
        var loggerMock = new Mock<ILogger<StaleWorkerCleanupService>>();

        // Track whether the error log has been emitted so we can wait for it deterministically.
        var errorLoggedTcs = new TaskCompletionSource();
        loggerMock
            .Setup(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(() => errorLoggedTcs.TrySetResult());

        var callCount = 0;
        poolMock
            .Setup(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    throw new InvalidOperationException("pool error");
                return new List<ConnectedWorker>().AsReadOnly();
            });

        var svc = CreateService(poolMock.Object, loggerMock.Object);
        // Zero delay so each loop iteration runs without a 60-second wait.
        svc.CleanupDelay = TimeSpan.Zero;

        await svc.StartAsync(CancellationToken.None);

        // Wait (up to 5 s) for the error to be logged; this ensures no timing flakiness.
        await errorLoggedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await svc.StopAsync(CancellationToken.None);

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception?>(ex => ex != null && ex.Message == "pool error"),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    // ── StaleTimeoutMinutes and CleanupIntervalSeconds constants ──────────────

    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal(60, CleanupDefaults.CleanupIntervalSeconds);
        Assert.Equal(2, CleanupDefaults.StaleTimeoutMinutes);
    }

    // ── Cancellation: service stops cleanly when token cancelled before delay ─

    [Fact]
    public async Task ExecuteAsync_WhenCancelledImmediately_StopsWithoutCallingPool()
    {
        var poolMock = MakePoolMock();

        var svc = CreateService(poolMock.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately — delay is never awaited

        await svc.StartAsync(cts.Token);
        await svc.StopAsync(CancellationToken.None);

        poolMock.Verify(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>()), Times.Never);
    }

    // ── Hung-task reclamation (worker still heartbeating) ─────────────────────

    private static StaleWorkerCleanupService CreateServiceWithConfig(
        IWorkerPool pool,
        int timeoutMinutes,
        ILogger<StaleWorkerCleanupService>? logger = null)
    {
        logger ??= Mock.Of<ILogger<StaleWorkerCleanupService>>();
        var config = new CopilotHive.Configuration.HiveConfigFile
        {
            Orchestrator = new CopilotHive.Configuration.OrchestratorConfig { WorkerTaskTimeoutMinutes = timeoutMinutes },
        };
        return new StaleWorkerCleanupService(pool, new TaskQueue(), new GoalPipelineManager(),
            logger, goalDispatcher: null, config: config);
    }

    /// <summary>
    /// A worker whose task has been inactive (no task-specific stream messages) past the
    /// configured timeout is still heartbeating, so PurgeStaleWorkers ignores it. It must
    /// still be evicted and its task reclaimed — otherwise the pipeline keeps its
    /// ActiveTaskId and holds a parallel-goal slot forever.
    /// </summary>
    [Fact]
    public async Task RunCleanupCycle_TaskExceedsTimeout_WorkerRemovedAndTaskReclaimed()
    {
        var hung = MakeWorker("worker-hung");
        hung.IsBusy = true;
        hung.CurrentTaskId = "task-hung";
        hung.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([hung]);
        poolMock.Setup(p => p.TryRemoveTimedOutWorker("worker-hung", It.IsAny<TimeSpan>())).Returns(true);

        var svc = CreateServiceWithConfig(poolMock.Object, timeoutMinutes: 60);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60)), Times.Once);
        // Eviction must go through the atomic re-check, never the unconditional RemoveWorker.
        poolMock.Verify(p => p.TryRemoveTimedOutWorker("worker-hung", TimeSpan.FromMinutes(60)), Times.Once);
        poolMock.Verify(p => p.RemoveWorker(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// TOCTOU guard at the service level: when the atomic re-check reports the candidate is no
    /// longer timed out (activity arrived after selection), the worker must NOT be logged as
    /// reclaimed and its task must NOT be rescheduled.
    /// </summary>
    [Fact]
    public async Task RunCleanupCycle_ActivityArrivedAfterSelection_NotReclaimedOrLogged()
    {
        var candidate = MakeWorker("worker-active");
        candidate.IsBusy = true;
        candidate.CurrentTaskId = "task-active";
        candidate.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([candidate]);
        // Activity arrived between selection and removal → atomic re-check refuses.
        poolMock.Setup(p => p.TryRemoveTimedOutWorker("worker-active", It.IsAny<TimeSpan>())).Returns(false);

        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var loggerMock = new Mock<ILogger<StaleWorkerCleanupService>>();
        var config = new CopilotHive.Configuration.HiveConfigFile
        {
            Orchestrator = new CopilotHive.Configuration.OrchestratorConfig { WorkerTaskTimeoutMinutes = 60 },
        };
        var svc = new StaleWorkerCleanupService(poolMock.Object, taskQueue, pipelineManager,
            loggerMock.Object, goalDispatcher: null, config: config);

        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.TryRemoveTimedOutWorker("worker-active", TimeSpan.FromMinutes(60)), Times.Once);
        // No reclaim warning at all — the worker was spared.
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("reclaiming")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
        // And its task was not re-enqueued.
        Assert.Null(taskQueue.TryDequeue(WorkerRole.Unspecified));
    }

    /// <summary>
    /// The reclaim log message must reference <see cref="ConnectedWorker.LastActivityAt"/>
    /// (inactivity-based), not <see cref="ConnectedWorker.CurrentTaskStartedAt"/> (wall-clock).
    /// Verifies the log message contains "inactive" and the worker's LastActivityAt timestamp.
    /// </summary>
    [Fact]
    public async Task RunCleanupCycle_TaskExceedsTimeout_LogReferencesLastActivityAt()
    {
        var hung = MakeWorker("worker-hung");
        hung.IsBusy = true;
        hung.CurrentTaskId = "task-hung";
        var inactiveSince = DateTime.UtcNow.AddMinutes(-90);
        hung.LastActivityAt = inactiveSince;
        // Set a different, much older task-start to prove the log uses LastActivityAt, not start.
        hung.CurrentTaskStartedAt = DateTime.UtcNow.AddMinutes(-180);

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([hung]);
        poolMock.Setup(p => p.TryRemoveTimedOutWorker("worker-hung", It.IsAny<TimeSpan>())).Returns(true);

        var loggerMock = new Mock<ILogger<StaleWorkerCleanupService>>();
        var svc = CreateServiceWithConfig(poolMock.Object, timeoutMinutes: 60, logger: loggerMock.Object);
        await svc.RunCleanupCycleAsync();

        // The reclaim warning must mention "inactive" and the LastActivityAt timestamp —
        // NOT "exceeded" or the CurrentTaskStartedAt value. ILogger's FormattedLogValues
        // formats DateTime with the invariant culture, so the predicate must match that.
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("inactive since") &&
                    v.ToString()!.Contains(inactiveSince.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture))),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCleanupCycle_TimeoutDisabled_DoesNotQueryTimedOutTasks()
    {
        var poolMock = MakePoolMock();

        var svc = CreateServiceWithConfig(poolMock.Object, timeoutMinutes: 0);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>()), Times.Never);
        poolMock.Verify(p => p.TryRemoveTimedOutWorker(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        poolMock.Verify(p => p.RemoveWorker(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// A busy worker whose task has recent activity must not be touched, even if the task
    /// started long ago — activity, not task start, drives the timeout.
    /// </summary>
    [Fact]
    public void GetWorkersWithTimedOutTasks_OnlyReturnsInactiveBusyWorkers()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("fresh", []);
        pool.RegisterWorker("hung", []);
        pool.RegisterWorker("idle", []);

        pool.MarkBusy("fresh", "task-fresh");
        pool.MarkBusy("hung", "task-hung");

        var hung = pool.GetWorker("hung");
        Assert.NotNull(hung);
        hung.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var timedOut = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));

        var only = Assert.Single(timedOut);
        Assert.Equal("hung", only.Id);
    }

    /// <summary>
    /// Regression: a task that started long ago is NOT reclaimed when the worker has
    /// recent task-specific stream activity.
    /// </summary>
    [Fact]
    public void GetWorkersWithTimedOutTasks_OldTaskStartButRecentActivity_NotReclaimed()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        var worker = pool.GetWorker("w1")!;
        worker.CurrentTaskStartedAt = DateTime.UtcNow.AddMinutes(-90); // old start
        worker.LastActivityAt = DateTime.UtcNow;                        // recent activity

        var timedOut = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));

        Assert.Empty(timedOut);
    }

    /// <summary>
    /// A recent heartbeat must NOT count as task activity: a worker that heartbeats but
    /// sends no task-specific stream messages is still reclaimed.
    /// </summary>
    [Fact]
    public void GetWorkersWithTimedOutTasks_RecentHeartbeatButOldActivity_StillReclaimed()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        var worker = pool.GetWorker("w1")!;
        worker.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);
        pool.UpdateHeartbeat("w1"); // recent heartbeat — must NOT reset LastActivityAt

        var timedOut = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));

        var only = Assert.Single(timedOut);
        Assert.Equal("w1", only.Id);
    }

    /// <summary>
    /// MarkIdle must clear the task start timestamp, otherwise a worker that finishes normally
    /// and later picks up a new task could be reclaimed using the previous task's start time.
    /// </summary>
    [Fact]
    public void MarkIdle_ClearsTaskStartedAt()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w1", []);

        pool.MarkBusy("w1", "task-1");
        Assert.NotNull(pool.GetWorker("w1")!.CurrentTaskStartedAt);

        pool.MarkIdle("w1");
        Assert.Null(pool.GetWorker("w1")!.CurrentTaskStartedAt);

        Assert.Empty(pool.GetWorkersWithTimedOutTasks(TimeSpan.Zero));
    }

    // ── Work-slot abandonment on reschedule ───────────────────────────────────

    /// <summary>
    /// A reclaimed task's WORK SLOT is abandoned alongside the active-task pointer, so the
    /// redispatch's fresh capture is not blocked by the dead dispatch's live slot. Asserted on
    /// the timed-out (hung-worker) branch; the stale-worker branch is covered below.
    /// </summary>
    [Fact]
    public async Task RunCleanupCycle_TaskReclaimed_AbandonsWorkSlotAndClearsPointer()
    {
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(new CopilotHive.Goals.Goal
        {
            Id = "goal-reclaim",
            Description = "Reclaim goal",
            RepositoryNames = ["test-repo"],
        });

        var plan = IterationPlan.Default(includeImprove: true);
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var slot = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        pipeline.SetActiveTask(slot.TaskId);
        pipelineManager.RegisterTask(slot.TaskId, pipeline.GoalId);

        var hung = MakeWorker("worker-hung");
        hung.IsBusy = true;
        hung.CurrentTaskId = slot.TaskId;
        hung.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([hung]);
        poolMock.Setup(p => p.TryRemoveTimedOutWorker("worker-hung", It.IsAny<TimeSpan>())).Returns(true);

        var config = new CopilotHive.Configuration.HiveConfigFile
        {
            Orchestrator = new CopilotHive.Configuration.OrchestratorConfig { WorkerTaskTimeoutMinutes = 60 },
        };
        var svc = new StaleWorkerCleanupService(
            poolMock.Object, new TaskQueue(), pipelineManager,
            Mock.Of<ILogger<StaleWorkerCleanupService>>(), goalDispatcher: null, config: config);

        await svc.RunCleanupCycleAsync();

        var view = Assert.Single(pipeline.GetSlotsForTest());
        Assert.Equal(slot.TaskId, view.Slot.TaskId);
        Assert.Equal(WorkSlotState.Abandoned, view.State);
        Assert.Null(pipeline.ActiveTaskId);

        // THE DEAD RULE: the freed position accepts a fresh capture (attempt 2).
        var redispatch = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.Equal(2, redispatch.Attempt);
        Assert.Equal(slot.Position, redispatch.Position);
    }

    /// <summary>
    /// THE (true, false) UNCONFIRMED PERSISTED REMOVAL: an interceptor forces the DELETE on the
    /// persisted <c>task_mappings</c> row to throw, so the unregister reports memoryRemoved=True
    /// persistenceRemoved=False — the EXACT conservative reclaim-unregister WARNING (verbatim),
    /// and the CONTINUATION still ran.
    /// </summary>
    [Fact]
    public async Task Reclaim_PersistenceRemovalDoesNotConfirm_ExactWarningAndContinuation()
    {
        var manager = new GoalPipelineManager(
            CreateStoreForCleanup(new InvalidOperationException("delete-sentinel")),
            new TestLogger<GoalPipelineManager>());
        var (pipeline, slot) = ArrangeAdmission(manager, "goal-d2-unconfirmed", "d2-unused");
        var taskId = slot.TaskId;

        var queue = new TaskQueue();
        var dispatcher = CreateDispatcher(manager, queue);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var svc = CreateStaleEvictionService(
            pool: null!, taskQueue: queue, pipelineManager: manager, taskId, logger, dispatcher);

        await svc.RunCleanupCycleAsync();

        // THE EXACT WARNING, VERBATIM (conservative wording — no row-survival claim).
        var expected =
            "WorkSlotIntegrity: reclaim-unregister goal=goal-d2-unconfirmed task=" + taskId +
            " memoryRemoved=True persistenceRemoved=False — the mapping's persisted removal did not confirm; a restart may resolve the retired task to this pipeline; the completion-protocol successor owns the durable reconciliation";
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == expected);
        // The DEBUG record for the same result is also present.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Debug &&
            e.Message == $"WorkSlotIntegrity: reclaim-unregister goal=goal-d2-unconfirmed task={taskId} memoryRemoved=True persistenceRemoved=False");

        // THE CONTINUATION: the redispatch still ran.
        Assert.Equal(["goal-d2-unconfirmed"], QueuedRedispatches(dispatcher));
        // Memory ownership IS removed; the persisted residue is left honestly.
        Assert.Null(manager.GetByTaskId(taskId));
    }

    /// <summary>
    /// A PipelineStore over a per-instance shared-cache in-memory SQLite database whose DELETE on
    /// <c>task_mappings</c> throws through the caller-supplied interceptor.
    /// </summary>
    private static PipelineStore CreateStoreForCleanup(Exception deleteSentinel)
    {
        var connectionString =
            $"Data Source=file:memdb-cleanup-{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keeper = new SqliteConnection(connectionString);
        keeper.Open();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        builder.AddInterceptors(new SentinelThrowingInterceptor(deleteSentinel, "DELETE"));
        var context = new CopilotHiveDbContext(builder.Options);
        context.Database.EnsureCreated();
        return new PipelineStore(context, NullLogger<PipelineStore>.Instance);
    }

    // ── D2: the replacement reclaim shape (retire + unregister + redispatch) ───

    /// <summary>
    /// Reads the dispatcher's private redispatch queue via reflection — the suite's established
    /// precedent (<c>GoalDispatcherTests</c> reflects <c>_redispatchQueue</c> the same way). The
    /// production surface stays seam-free.
    /// </summary>
    internal static IReadOnlyList<string> QueuedRedispatches(GoalDispatcher dispatcher)
    {
        var field = typeof(GoalDispatcher).GetField("_redispatchQueue", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_redispatchQueue field not found on GoalDispatcher");
        var queue = (ConcurrentQueue<string>)field.GetValue(dispatcher)!;
        return [.. queue];
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D2 — THE REPLACEMENT RECLAIM (retire + unregister + redispatch)
    // ═══════════════════════════════════════════════════════════════════════
    //
    // The interim's GetActiveTask → MarkComplete + Enqueue re-enqueue is RETIRED: the reclaim
    // completes the queue entry unconditionally (no re-queue), retires the D1 slot, unregisters
    // the durable mapping and redispatches the goal. These vectors pin that shape end to end.

    /// <summary>
    /// Builds a real GoalDispatcher whose redispatch queue a test can read via reflection.
    /// </summary>
    private static GoalDispatcher CreateDispatcher(GoalPipelineManager pipelineManager, TaskQueue taskQueue) =>
        new(
            new CopilotHive.Goals.GoalManager(),
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

    /// <summary>
    /// Arranges a pipeline with ONE live coder slot for <paramref name="taskId"/>, pointed at and
    /// mapped in the manager — the full admission state a reclaim is expected to dismantle.
    /// </summary>
    private static (GoalPipeline Pipeline, WorkSlot Slot) ArrangeAdmission(
        GoalPipelineManager pipelineManager, string goalId, string taskId)
    {
        var pipeline = pipelineManager.CreatePipeline(new CopilotHive.Goals.Goal
        {
            Id = goalId,
            Description = "D2 reclaim goal",
            RepositoryNames = ["test-repo"],
        });
        var plan = IterationPlan.Default(includeImprove: true);
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var built = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        pipeline.SetActiveTask(built.TaskId);
        pipelineManager.RegisterTask(built.TaskId, pipeline.GoalId);
        return (pipeline, new WorkSlot(built.TaskId, built.Position, built.Attempt));
    }

    /// <summary>
    /// Builds a service whose pool, on the FIRST purge, yields one stale worker holding
    /// <paramref name="taskId"/> (the STALE-EVICTION branch).
    /// </summary>
    private static StaleWorkerCleanupService CreateStaleEvictionService(
        IWorkerPool pool, TaskQueue taskQueue, GoalPipelineManager pipelineManager,
        string taskId, ILogger<StaleWorkerCleanupService>? logger = null,
        GoalDispatcher? dispatcher = null)
    {
        var staleWorker = MakeWorker("worker-dead");
        staleWorker.IsBusy = true;
        staleWorker.CurrentTaskId = taskId;

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>())).Returns([staleWorker]);

        return new StaleWorkerCleanupService(
            poolMock.Object, taskQueue, pipelineManager,
            logger ?? Mock.Of<ILogger<StaleWorkerCleanupService>>(),
            goalDispatcher: dispatcher);
    }

    /// <summary>
    /// THE STALE-EVICTION RECLAIM: the queue entry is completed (NOT re-queued), the slot is
    /// retired, the owned pointer is cleared, the mapping is unregistered (true, true) and the
    /// retired+dispatcher log is emitted.
    /// </summary>
    [Fact]
    public async Task Reclaim_StaleEviction_EntryGoneNotRequeuedSlotRetiredMappingUnregistered()
    {
        var pipelineManager = new GoalPipelineManager();
        var (pipeline, slot) = ArrangeAdmission(pipelineManager, "goal-d2-stale", "d2-unused");
        var taskId = slot.TaskId;

        // The task is ACTIVE in the queue — the interim would have re-enqueued it here.
        var queue = new TaskQueue();
        queue.Enqueue(new WorkTask { TaskId = taskId, GoalId = "goal-x", GoalDescription = "x", Prompt = "x", Role = WorkerRole.Coder, Repositories = [] });
        queue.Activate(queue.TryDequeueAny()!, "worker-dead");

        var dispatcher = CreateDispatcher(pipelineManager, queue);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var svc = CreateStaleEvictionService(
            pool: null!, taskQueue: queue, pipelineManager, taskId, logger, dispatcher);

        await svc.RunCleanupCycleAsync();

        // (1) THE ENTRY IS GONE, NOT RE-QUEUED — the double-dispatch defect closed.
        Assert.Null(queue.GetActiveTask(taskId));
        Assert.Null(queue.TryDequeueAny());

        // (2) THE RETIRE: the slot is Abandoned and the pointer is cleared.
        var view = Assert.Single(pipeline.GetSlotsForTest());
        Assert.Equal(WorkSlotState.Abandoned, view.State);
        Assert.Null(pipeline.ActiveTaskId);

        // (3) THE MAPPING: removed from memory — (true, true) with no store.
        Assert.Null(pipelineManager.GetByTaskId(taskId));

        // (4) THE REDISPATCH: the goal is queued exactly once.
        Assert.Equal(["goal-d2-stale"], QueuedRedispatches(dispatcher));

        // (5) THE RETIRED+DISPATCHER LOG.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Information &&
            e.Message == $"Worker worker-dead task {taskId} reclaimed — slot retired; queued for re-dispatch (goal goal-d2-stale)");
    }

    /// <summary>
    /// THE TIMEOUT-RECLAIM (the hung-worker branch) takes the SAME replacement shape.
    /// </summary>
    [Fact]
    public async Task Reclaim_TimeoutReclaim_SameShape()
    {
        var pipelineManager = new GoalPipelineManager();
        var (_, slot) = ArrangeAdmission(pipelineManager, "goal-d2-timeout", "d2-unused");
        var taskId = slot.TaskId;

        var queue = new TaskQueue();
        queue.Enqueue(new WorkTask { TaskId = taskId, GoalId = "goal-x", GoalDescription = "x", Prompt = "x", Role = WorkerRole.Coder, Repositories = [] });
        queue.Activate(queue.TryDequeueAny()!, "worker-hung");

        var hung = MakeWorker("worker-hung");
        hung.IsBusy = true;
        hung.CurrentTaskId = taskId;
        hung.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([hung]);
        poolMock.Setup(p => p.TryRemoveTimedOutWorker("worker-hung", It.IsAny<TimeSpan>())).Returns(true);

        var config = new CopilotHive.Configuration.HiveConfigFile
        {
            Orchestrator = new CopilotHive.Configuration.OrchestratorConfig { WorkerTaskTimeoutMinutes = 60 },
        };
        var dispatcher = CreateDispatcher(pipelineManager, queue);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var svc = new StaleWorkerCleanupService(
            poolMock.Object, queue, pipelineManager, logger,
            goalDispatcher: dispatcher, config: config);

        await svc.RunCleanupCycleAsync();

        // The same shape as the stale-eviction branch.
        Assert.Null(queue.GetActiveTask(taskId));
        Assert.Null(queue.TryDequeueAny());
        Assert.Null(pipelineManager.GetByTaskId(taskId));
        Assert.Equal(["goal-d2-timeout"], QueuedRedispatches(dispatcher));
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Information &&
            e.Message == $"Worker worker-hung task {taskId} reclaimed — slot retired; queued for re-dispatch (goal goal-d2-timeout)");
    }

    /// <summary>
    /// THE DISPATCHER-NULL VARIANT: the "no dispatcher available" log, NO redispatch, and the
    /// slot + mapping still retired.
    /// </summary>
    [Fact]
    public async Task Reclaim_DispatcherNull_NoRedispatchButSlotAndMappingStillRetired()
    {
        var pipelineManager = new GoalPipelineManager();
        var (_, slot) = ArrangeAdmission(pipelineManager, "goal-d2-nodisp", "d2-unused");
        var taskId = slot.TaskId;

        var queue = new TaskQueue();
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var svc = CreateStaleEvictionService(
            pool: null!, taskQueue: queue, pipelineManager, taskId, logger, dispatcher: null);

        await svc.RunCleanupCycleAsync();

        Assert.Null(pipelineManager.GetByTaskId(taskId));
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Information &&
            e.Message == $"Worker worker-dead task {taskId} reclaimed — slot retired; no dispatcher available for re-dispatch (goal goal-d2-nodisp)");
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("queued for re-dispatch", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE ALREADY-ABANDONED RE-RECLAIM: the false outcome appears in the log, and the
    /// CONTINUATION still ran — the mapping unregister and the redispatch.
    /// </summary>
    [Fact]
    public async Task Reclaim_AlreadyAbandoned_FalseOutcomeLogged_ContinuationStillRuns()
    {
        var pipelineManager = new GoalPipelineManager();
        var (pipeline, slot) = ArrangeAdmission(pipelineManager, "goal-d2-again", "d2-unused");
        var taskId = slot.TaskId;

        // Pre-retire the slot: the reclaim must observe AlreadyAbandoned, not Retired.
        Assert.Equal(SlotRetirementOutcome.Retired, pipeline.RetireSlotAndClearIfCurrent(taskId));

        var queue = new TaskQueue();
        var dispatcher = CreateDispatcher(pipelineManager, queue);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var svc = CreateStaleEvictionService(
            pool: null!, taskQueue: queue, pipelineManager, taskId, logger, dispatcher);

        await svc.RunCleanupCycleAsync();

        // THE FALSE OUTCOME in the log.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Information &&
            e.Message == $"Worker worker-dead task {taskId} reclaimed — slot already retired or absent (outcome=AlreadyAbandoned); queued for re-dispatch (goal goal-d2-again)");
        // THE CONTINUATION: the unregister still ran and the redispatch still fired.
        Assert.Null(pipelineManager.GetByTaskId(taskId));
        Assert.Equal(["goal-d2-again"], QueuedRedispatches(dispatcher));
    }

    /// <summary>
    /// THE SLOT-ABSENT (pre-registry) RECLAIM: the false outcome; the continuation still ran.
    /// </summary>
    [Fact]
    public async Task Reclaim_SlotAbsent_FalseOutcomeLogged_ContinuationStillRuns()
    {
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(new CopilotHive.Goals.Goal
        {
            Id = "goal-d2-absent",
            Description = "D2 slot-absent goal",
            RepositoryNames = ["test-repo"],
        });
        // NO slot captured — the pipeline exists but its registry is empty (pre-registry).
        const string taskId = "goal-d2-absent-coder-001-01-001";
        pipelineManager.RegisterTask(taskId, pipeline.GoalId);

        var queue = new TaskQueue();
        var dispatcher = CreateDispatcher(pipelineManager, queue);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var svc = CreateStaleEvictionService(
            pool: null!, taskQueue: queue, pipelineManager, taskId, logger, dispatcher);

        await svc.RunCleanupCycleAsync();

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Information &&
            e.Message == $"Worker worker-dead task {taskId} reclaimed — slot already retired or absent (outcome=SlotAbsent); queued for re-dispatch (goal goal-d2-absent)");
        // The continuation ran even though nothing was retired.
        Assert.Null(pipelineManager.GetByTaskId(taskId));
        Assert.Equal(["goal-d2-absent"], QueuedRedispatches(dispatcher));
    }

    /// <summary>
    /// THE ORPHAN: no pipeline resolves the task — the exact orphan log and NO redispatch.
    /// </summary>
    [Fact]
    public async Task Reclaim_Orphan_NoPipeline_ExactLogAndNoRedispatch()
    {
        var pipelineManager = new GoalPipelineManager();
        const string taskId = "orphan-task-1";

        var queue = new TaskQueue();
        queue.Enqueue(new WorkTask { TaskId = taskId, GoalId = "goal-x", GoalDescription = "x", Prompt = "x", Role = WorkerRole.Coder, Repositories = [] });
        queue.Activate(queue.TryDequeueAny()!, "worker-dead");

        var dispatcher = CreateDispatcher(pipelineManager, queue);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var svc = CreateStaleEvictionService(
            pool: null!, taskQueue: queue, pipelineManager, taskId, logger, dispatcher);

        await svc.RunCleanupCycleAsync();

        // The active entry is gone...
        Assert.Null(queue.GetActiveTask(taskId));
        // ...with the EXACT orphan log...
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == $"Worker worker-dead task {taskId} reclaimed with no pipeline — the active entry removed; no re-dispatch (orphan; a persisted mapping, if any, survives for the successor's reconciliation)");
        // ...and NO redispatch.
        Assert.Empty(QueuedRedispatches(dispatcher));
    }

    /// <summary>
    /// THE NEWER-POINTER RACE: a delayed reclaim — the active pointer has already moved on to a
    /// NEWER task when the old task is reclaimed. The pointer is NOT erased, the old slot is
    /// retired, and the new slot is untouched.
    /// </summary>
    [Fact]
    public async Task Reclaim_DelayedReclaimWithNewerPointer_PointerPreservedNewSlotUntouched()
    {
        var pipelineManager = new GoalPipelineManager();
        var (pipeline, oldSlot) = ArrangeAdmission(pipelineManager, "goal-d2-race", "d2-unused");
        var oldTaskId = oldSlot.TaskId;

        // THE RACE: a newer dispatch moved the pointer and added a new slot BEFORE the reclaim.
        // The new slot occupies the NEXT attempt's position, so it cannot collide with the old
        // dispatch's position (the double-assignment refusal would fire on a same-position seed).
        const string newTaskId = "goal-d2-race-coder-001-01-002";
        Assert.True(pipeline.SeedSlotForTest(
            newTaskId, new WorkSlotPosition(1, GoalPhase.Coding, 1), 2, WorkSlotState.Pending));
        pipeline.SetActiveTask(newTaskId);
        pipelineManager.RegisterTask(newTaskId, pipeline.GoalId);
        Assert.Equal(newTaskId, pipeline.ActiveTaskId);

        var queue = new TaskQueue();
        var dispatcher = CreateDispatcher(pipelineManager, queue);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var svc = CreateStaleEvictionService(
            pool: null!, taskQueue: queue, pipelineManager, oldTaskId, logger, dispatcher);

        await svc.RunCleanupCycleAsync();

        // THE POINTER IS NOT ERASED — it still names the newer task.
        Assert.Equal(newTaskId, pipeline.ActiveTaskId);
        // The OLD slot is retired; the NEW slot is untouched.
        Assert.Equal(WorkSlotState.Abandoned, Assert.Single(pipeline.GetSlotsForTest(), s => s.Slot.TaskId == oldTaskId).State);
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipeline.GetSlotsForTest(), s => s.Slot.TaskId == newTaskId).State);
        // The old mapping is gone; the newer mapping survives.
        Assert.Null(pipelineManager.GetByTaskId(oldTaskId));
        Assert.NotNull(pipelineManager.GetByTaskId(newTaskId));
        // The outcome was Retired (the old slot WAS live), with the dispatcher tail.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Information &&
            e.Message == $"Worker worker-dead task {oldTaskId} reclaimed — slot retired; queued for re-dispatch (goal goal-d2-race)");
    }
}
