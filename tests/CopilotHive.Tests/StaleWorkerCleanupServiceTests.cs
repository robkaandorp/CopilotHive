using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Moq;

using System.Globalization;

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
}
