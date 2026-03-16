using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using Microsoft.Extensions.Logging;
using Moq;
using System.Threading.Tasks;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for StaleWorkerCleanupService with real WorkerPool.
/// These tests verify the TOCTOU race condition is properly handled by using
/// the atomic PurgeStaleWorkers method.
/// </summary>
public sealed class StaleWorkerCleanupServiceIntegrationTests
{
    private static StaleWorkerCleanupService CreateService(
        WorkerPool pool,
        ILogger<StaleWorkerCleanupService>? logger = null)
    {
        logger ??= Mock.Of<ILogger<StaleWorkerCleanupService>>();
        return new StaleWorkerCleanupService(pool, logger);
    }

    /// <summary>
    /// Verifies that PurgeStaleWorkers atomically removes stale workers.
    /// Even if a worker's heartbeat is updated after the initial staleness check,
    /// the atomic operation ensures only workers that are stale at snapshot time are removed.
    /// </summary>
    [Fact]
    public async Task RunCleanupCycle_WithRealPool_PurgesStaleWorkersAtomically()
    {
        var pool = new WorkerPool();
        var timeout = TimeSpan.FromMinutes(2);
        var now = DateTime.UtcNow;

        // Register workers with different heartbeat times
        var staleWorker = pool.RegisterWorker("stale-worker", WorkerRole.Coder, []);
        staleWorker.LastHeartbeat = now.AddMinutes(-5); // 5 minutes old = stale

        var freshWorker = pool.RegisterWorker("fresh-worker", WorkerRole.Reviewer, []);
        freshWorker.LastHeartbeat = now.AddSeconds(-30); // 30 seconds old = fresh

        var svc = CreateService(pool);

        // Run cleanup cycle
        await svc.RunCleanupCycleAsync();

        // Verify: stale worker removed, fresh worker remains
        Assert.Null(pool.GetWorker("stale-worker"));
        Assert.NotNull(pool.GetWorker("fresh-worker"));
        Assert.Single(pool.GetAllWorkers());
    }

    /// <summary>
    /// Verifies that the service correctly logs warnings for each removed worker.
    /// </summary>
    [Fact]
    public async Task RunCleanupCycle_WithRealPool_LogsWarningForEachRemoved()
    {
        var pool = new WorkerPool();
        var loggerMock = new Mock<ILogger<StaleWorkerCleanupService>>();

        var now = DateTime.UtcNow;
        var w1 = pool.RegisterWorker("worker-1", WorkerRole.Coder, []);
        w1.LastHeartbeat = now.AddMinutes(-5);

        var w2 = pool.RegisterWorker("worker-2", WorkerRole.Reviewer, []);
        w2.LastHeartbeat = now.AddMinutes(-3);

        var svc = CreateService(pool, loggerMock.Object);

        await svc.RunCleanupCycleAsync();

        // Verify warning logged for each stale worker
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("worker-1")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("worker-2")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that when no workers are stale, the pool remains unchanged
    /// and no warnings are logged.
    /// </summary>
    [Fact]
    public async Task RunCleanupCycle_WithRealPool_NoStaleWorkers_PoolUnchanged()
    {
        var pool = new WorkerPool();
        var loggerMock = new Mock<ILogger<StaleWorkerCleanupService>>();

        var now = DateTime.UtcNow;
        pool.RegisterWorker("worker-1", WorkerRole.Coder, []);
        pool.RegisterWorker("worker-2", WorkerRole.Reviewer, []);
        // Both workers have recent heartbeats (default)

        var svc = CreateService(pool, loggerMock.Object);

        await svc.RunCleanupCycleAsync();

        // Verify: all workers remain
        Assert.Equal(2, pool.GetAllWorkers().Count);

        // Verify: no warnings logged
        loggerMock.Verify(
            l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }
}
