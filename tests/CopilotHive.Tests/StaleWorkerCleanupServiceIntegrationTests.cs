using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Moq;
using System.Threading.Tasks;
using Xunit;

using WorkerRole = CopilotHive.Workers.WorkerRole;

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
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        return new StaleWorkerCleanupService(pool, taskQueue, pipelineManager, logger);
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
        var staleWorker = pool.RegisterWorker("stale-worker", []);
        staleWorker.LastHeartbeat = now.AddMinutes(-5); // 5 minutes old = stale

        var freshWorker = pool.RegisterWorker("fresh-worker", []);
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
        var w1 = pool.RegisterWorker("worker-1", []);
        w1.LastHeartbeat = now.AddMinutes(-5);

        var w2 = pool.RegisterWorker("worker-2", []);
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
        pool.RegisterWorker("worker-1", []);
        pool.RegisterWorker("worker-2", []);
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

    /// <summary>
    /// A STALE worker (heartbeat branch, real pool) holding a live dispatch has its task's WORK
    /// SLOT retired through the D1 primitive together with its pipeline pointer cleared, and the
    /// durable mapping UNREGISTERED, so the position is free for the redispatch's fresh capture.
    /// Without the retirement the position would still be occupied by a live slot and the next
    /// capture would be refused as a double assignment.
    /// </summary>
    [Fact]
    public async Task RunCleanupCycle_WithRealPool_StaleWorkerWithTask_RetiresWorkSlotAndUnregistersMapping()
    {
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();

        var pipeline = pipelineManager.CreatePipeline(new Goal
        {
            Id = "goal-stale-slot",
            Description = "Stale slot goal",
            RepositoryNames = ["test-repo"],
        });
        var plan = IterationPlan.Default(includeImprove: true);
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var built = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        var taskId = built.TaskId;
        pipeline.SetActiveTask(taskId);
        pipelineManager.RegisterTask(taskId, pipeline.GoalId);

        // The task is ACTIVE in the queue — the replacement must complete it, never re-enqueue it.
        var task = new WorkTask
        {
            TaskId = taskId,
            GoalId = pipeline.GoalId,
            GoalDescription = "Stale slot goal",
            Prompt = "Code it",
            Role = WorkerRole.Coder,
            Repositories = [],
        };
        taskQueue.Enqueue(task);
        taskQueue.Activate(taskQueue.TryDequeueAny()!, "stale-worker");

        var staleWorker = pool.RegisterWorker("stale-worker", []);
        staleWorker.LastHeartbeat = DateTime.UtcNow.AddMinutes(-5);
        pool.MarkBusy("stale-worker", taskId);
        staleWorker.LastHeartbeat = DateTime.UtcNow.AddMinutes(-5);

        var svc = new StaleWorkerCleanupService(
            pool, taskQueue, pipelineManager, Mock.Of<ILogger<StaleWorkerCleanupService>>());

        await svc.RunCleanupCycleAsync();

        Assert.Null(pool.GetWorker("stale-worker"));

        // THE RETIRE: the slot is Abandoned, the pointer is cleared, the mapping is unregistered.
        var view = Assert.Single(pipeline.GetSlotsForTest());
        Assert.Equal(taskId, view.Slot.TaskId);
        Assert.Equal(WorkSlotState.Abandoned, view.State);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Null(pipelineManager.GetByTaskId(taskId));

        // THE REPLACEMENT SHAPE: the entry is gone, NOT re-queued.
        Assert.Null(taskQueue.GetActiveTask(taskId));
        Assert.Null(taskQueue.TryDequeueAny());

        // THE DEAD RULE: the redispatch's fresh capture succeeds on the freed position.
        var redispatch = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.Equal(2, redispatch.Attempt);
        Assert.Equal(built.Position, redispatch.Position);
    }
}
