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

    private static StaleWorkerCleanupService CreateService(
        IWorkerPool pool,
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
        var poolMock = new Mock<IWorkerPool>();
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
        var poolMock = new Mock<IWorkerPool>();
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

        var poolMock = new Mock<IWorkerPool>();
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

        var poolMock = new Mock<IWorkerPool>();
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
        var poolMock = new Mock<IWorkerPool>();
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
        var poolMock = new Mock<IWorkerPool>();

        var svc = CreateService(poolMock.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately — delay is never awaited

        await svc.StartAsync(cts.Token);
        await svc.StopAsync(CancellationToken.None);

        poolMock.Verify(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>()), Times.Never);
    }
}
