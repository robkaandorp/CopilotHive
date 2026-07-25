using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Moq;

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
    /// A worker whose task has run past the wall-clock limit is still heartbeating, so
    /// PurgeStaleWorkers ignores it. It must still be evicted and its task reclaimed —
    /// otherwise the pipeline keeps its ActiveTaskId and holds a parallel-goal slot forever.
    /// </summary>
    [Fact]
    public async Task RunCleanupCycle_TaskExceedsTimeout_WorkerRemovedAndTaskReclaimed()
    {
        var hung = MakeWorker("worker-hung");
        hung.IsBusy = true;
        hung.CurrentTaskId = "task-hung";
        hung.CurrentTaskStartedAt = DateTime.UtcNow.AddMinutes(-90);

        var poolMock = MakePoolMock();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([hung]);

        var svc = CreateServiceWithConfig(poolMock.Object, timeoutMinutes: 60);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60)), Times.Once);
        poolMock.Verify(p => p.RemoveWorker("worker-hung"), Times.Once);
    }

    [Fact]
    public async Task RunCleanupCycle_TimeoutDisabled_DoesNotQueryTimedOutTasks()
    {
        var poolMock = MakePoolMock();

        var svc = CreateServiceWithConfig(poolMock.Object, timeoutMinutes: 0);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>()), Times.Never);
        poolMock.Verify(p => p.RemoveWorker(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// A busy worker whose task is still within the limit must not be touched.
    /// </summary>
    [Fact]
    public void GetWorkersWithTimedOutTasks_OnlyReturnsOverrunningBusyWorkers()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("fresh", []);
        pool.RegisterWorker("hung", []);
        pool.RegisterWorker("idle", []);

        pool.MarkBusy("fresh", "task-fresh");
        pool.MarkBusy("hung", "task-hung");

        var hung = pool.GetWorker("hung");
        Assert.NotNull(hung);
        hung.CurrentTaskStartedAt = DateTime.UtcNow.AddMinutes(-90);

        var timedOut = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));

        var only = Assert.Single(timedOut);
        Assert.Equal("hung", only.Id);
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
}
