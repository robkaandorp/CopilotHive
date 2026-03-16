using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
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
        return new StaleWorkerCleanupService(pool, logger);
    }

    // ── (a) No stale workers → nothing removed ────────────────────────────────

    [Fact]
    public async Task RunCleanupCycle_NoStaleWorkers_RemoveWorkerNotCalled()
    {
        var poolMock = new Mock<IWorkerPool>();
        poolMock
            .Setup(p => p.GetStaleWorkers(It.IsAny<TimeSpan>()))
            .Returns([]);

        var svc = CreateService(poolMock.Object);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.RemoveWorker(It.IsAny<string>()), Times.Never);
    }

    // ── (b) One stale worker → removed and warning logged ─────────────────────

    [Fact]
    public async Task RunCleanupCycle_OneStaleWorker_RemovedAndWarningLogged()
    {
        var staleWorker = MakeWorker("worker-1");
        var poolMock = new Mock<IWorkerPool>();
        poolMock
            .Setup(p => p.GetStaleWorkers(It.IsAny<TimeSpan>()))
            .Returns([staleWorker]);
        poolMock.Setup(p => p.RemoveWorker("worker-1")).Returns(true);

        var loggerMock = new Mock<ILogger<StaleWorkerCleanupService>>();

        var svc = CreateService(poolMock.Object, loggerMock.Object);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.RemoveWorker("worker-1"), Times.Once);
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
            .Setup(p => p.GetStaleWorkers(It.IsAny<TimeSpan>()))
            .Returns(workers);
        poolMock.Setup(p => p.RemoveWorker(It.IsAny<string>())).Returns(true);

        var svc = CreateService(poolMock.Object);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.RemoveWorker("worker-a"), Times.Once);
        poolMock.Verify(p => p.RemoveWorker("worker-b"), Times.Once);
        poolMock.Verify(p => p.RemoveWorker("worker-c"), Times.Once);
        poolMock.Verify(p => p.RemoveWorker(It.IsAny<string>()), Times.Exactly(3));
    }

    // ── (d) GetStaleWorkers called with the correct timeout ───────────────────

    [Fact]
    public async Task RunCleanupCycle_CallsGetStaleWorkers_WithCorrectTimeout()
    {
        var expectedTimeout = TimeSpan.FromMinutes(StaleWorkerCleanupService.StaleTimeoutMinutes);

        var poolMock = new Mock<IWorkerPool>();
        poolMock
            .Setup(p => p.GetStaleWorkers(expectedTimeout))
            .Returns([])
            .Verifiable();

        var svc = CreateService(poolMock.Object);
        await svc.RunCleanupCycleAsync();

        poolMock.Verify(p => p.GetStaleWorkers(expectedTimeout), Times.Once);
    }

    // ── StaleTimeoutMinutes and CleanupIntervalSeconds constants ──────────────

    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal(60, StaleWorkerCleanupService.CleanupIntervalSeconds);
        Assert.Equal(2, StaleWorkerCleanupService.StaleTimeoutMinutes);
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

        poolMock.Verify(p => p.GetStaleWorkers(It.IsAny<TimeSpan>()), Times.Never);
    }
}
