using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Tests that verify the <c>CurrentModel</c> lifecycle on a <see cref="ConnectedWorker"/>
/// as managed by <see cref="HiveOrchestratorService"/>:
/// — model is set when a task is assigned to a worker (<c>ApplyTaskAssignment</c>),
/// — model is cleared when the task completes (<c>ApplyTaskCompletion</c>).
///
/// These tests call the real <see cref="HiveOrchestratorService.ApplyTaskAssignment"/> and
/// <see cref="HiveOrchestratorService.ApplyTaskCompletion"/> internal methods, which are the
/// same methods used by the private <c>HandleWorkerReady</c> and <c>HandleTaskComplete</c>
/// handlers. Removing either assignment would cause these tests to fail.
/// </summary>
public sealed class HiveOrchestratorCurrentModelLifecycleTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal <see cref="HiveOrchestratorService"/> with real
    /// <see cref="WorkerPool"/>, <see cref="TaskQueue"/>, and a trivially-constructed
    /// <see cref="GoalDispatcher"/> — enough to exercise the model-lifecycle methods.
    /// </summary>
    private static (HiveOrchestratorService service, WorkerPool pool, TaskQueue taskQueue)
        CreateService()
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

        var service = new HiveOrchestratorService(
            pool,
            taskQueue,
            pipelineManager,
            completionNotifier,
            dispatcher,
            NullLogger<HiveOrchestratorService>.Instance);

        return (service, pool, taskQueue);
    }

    // ── ApplyTaskAssignment ───────────────────────────────────────────────────

    #region ApplyTaskAssignment_SetsCurrentModel

    /// <summary>
    /// Calling <see cref="HiveOrchestratorService.ApplyTaskAssignment"/> sets
    /// <see cref="ConnectedWorker.CurrentModel"/> from the assigned task's model field.
    /// If this assignment is removed from production code, this test will fail.
    /// </summary>
    [Fact]
    public void ApplyTaskAssignment_SetsCurrentModelOnWorker()
    {
        // Arrange
        var (service, pool, taskQueue) = CreateService();
        var worker = pool.RegisterWorker("w-assign-1", []);

        var task = new WorkTask
        {
            TaskId = "task-assign-1",
            GoalId = "goal-1",
            GoalDescription = "Test goal",
            Prompt = "Do something",
            Role = WorkerRole.Coder,
            Model = "claude-opus-4",
            Repositories = [],
        };
        taskQueue.Enqueue(task);
        var dequeued = taskQueue.TryDequeue(WorkerRole.Unspecified);
        Assert.NotNull(dequeued);

        // Act — call the real service method
        service.ApplyTaskAssignment(worker, dequeued);

        // Assert: model is set by the service method
        Assert.Equal("claude-opus-4", worker.CurrentModel);
        Assert.True(worker.IsBusy);
        Assert.Equal("task-assign-1", worker.CurrentTaskId);
    }

    #endregion

    #region ApplyTaskAssignment_UsesTaskModelNotHardcoded

    /// <summary>
    /// Verifies that the model stored on the worker reflects the exact model string from
    /// the task, not a default or fallback value. This catches silent substitutions.
    /// </summary>
    [Fact]
    public void ApplyTaskAssignment_UsesExactModelFromTask()
    {
        var (service, pool, taskQueue) = CreateService();
        var worker = pool.RegisterWorker("w-assign-2", []);

        var task = new WorkTask
        {
            TaskId = "task-assign-2",
            GoalId = "goal-2",
            GoalDescription = "Model precision test",
            Prompt = "Solve it",
            Role = WorkerRole.Tester,
            Model = "gpt-4o-mini",
            Repositories = [],
        };
        taskQueue.Enqueue(task);
        var dequeued = taskQueue.TryDequeue(WorkerRole.Unspecified)!;

        service.ApplyTaskAssignment(worker, dequeued);

        Assert.Equal("gpt-4o-mini", worker.CurrentModel);
    }

    #endregion

    // ── ApplyTaskCompletion ───────────────────────────────────────────────────

    #region ApplyTaskCompletion_ClearsCurrentModel

    /// <summary>
    /// Calling <see cref="HiveOrchestratorService.ApplyTaskCompletion"/> clears
    /// <see cref="ConnectedWorker.CurrentModel"/> to <c>null</c>.
    /// If this null-assignment is removed from production code, this test will fail.
    /// </summary>
    [Fact]
    public void ApplyTaskCompletion_ClearsCurrentModelOnWorker()
    {
        // Arrange: put the worker into a busy state with a model set via the real service
        var (service, pool, taskQueue) = CreateService();
        var worker = pool.RegisterWorker("w-complete-1", []);

        var task = new WorkTask
        {
            TaskId = "task-complete-1",
            GoalId = "goal-3",
            GoalDescription = "Complete test",
            Prompt = "Finish the work",
            Role = WorkerRole.Tester,
            Model = "gpt-4o",
            Repositories = [],
        };
        taskQueue.Enqueue(task);
        var dequeued = taskQueue.TryDequeue(WorkerRole.Unspecified)!;
        service.ApplyTaskAssignment(worker, dequeued);

        // Precondition: model is set after assignment
        Assert.Equal("gpt-4o", worker.CurrentModel);
        Assert.True(worker.IsBusy);

        // Act — call the real service completion method
        service.ApplyTaskCompletion(worker, dequeued.TaskId);

        // Assert: model is cleared and worker is idle
        Assert.Null(worker.CurrentModel);
        Assert.False(worker.IsBusy);
    }

    #endregion

    // ── Full round-trip ───────────────────────────────────────────────────────

    #region CurrentModel_SetThenClearedInFullRoundTrip

    /// <summary>
    /// Verifies the complete assign→complete lifecycle via the real service methods:
    /// model is set during assignment, cleared during completion.
    /// </summary>
    [Fact]
    public void ApplyTaskAssignment_ThenApplyTaskCompletion_SetsAndClearsCurrentModel()
    {
        var (service, pool, taskQueue) = CreateService();
        var worker = pool.RegisterWorker("w-roundtrip-1", []);

        var task = new WorkTask
        {
            TaskId = "task-roundtrip-1",
            GoalId = "goal-4",
            GoalDescription = "Round-trip goal",
            Prompt = "Do the full cycle",
            Role = WorkerRole.Coder,
            Model = "gpt-4",
            Repositories = [],
        };
        taskQueue.Enqueue(task);
        var dequeued = taskQueue.TryDequeue(WorkerRole.Unspecified)!;

        // Phase 1 — assign task via real service
        service.ApplyTaskAssignment(worker, dequeued);
        Assert.Equal("gpt-4", worker.CurrentModel);
        Assert.True(worker.IsBusy);

        // Phase 2 — complete task via real service
        service.ApplyTaskCompletion(worker, dequeued.TaskId);
        Assert.Null(worker.CurrentModel);
        Assert.False(worker.IsBusy);
    }

    #endregion
}
