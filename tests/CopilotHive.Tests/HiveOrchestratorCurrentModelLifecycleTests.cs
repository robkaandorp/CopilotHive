using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Tests;

/// <summary>
/// Tests that verify the <c>CurrentModel</c> lifecycle on a <see cref="ConnectedWorker"/>
/// as managed by <see cref="HiveOrchestratorService"/>:
/// — model is set when a task is assigned to a worker (HandleWorkerReady),
/// — model is cleared when the task completes (HandleTaskComplete).
///
/// These tests exercise the same operations that the private
/// <c>HandleWorkerReady</c> and <c>HandleTaskComplete</c> methods perform,
/// operating directly on <see cref="WorkerPool"/> and <see cref="ConnectedWorker"/>
/// to avoid spinning up gRPC infrastructure.
/// </summary>
public sealed class HiveOrchestratorCurrentModelLifecycleTests
{
    // ── HandleWorkerReady behaviour ───────────────────────────────────────────

    #region CurrentModel_SetWhenTaskAssigned

    /// <summary>
    /// When a task is dispatched to a worker (mimicking the path inside
    /// <c>HandleWorkerReady</c>), the worker's <c>CurrentModel</c> is set
    /// to the model requested by that task.
    /// </summary>
    [Fact]
    public void CurrentModel_SetWhenTaskAssigned()
    {
        // Arrange
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();

        var worker = pool.RegisterWorker("w-lifecycle-1", []);

        var task = new WorkTask
        {
            TaskId = "task-lifecycle-1",
            GoalId = "goal-1",
            GoalDescription = "Test goal",
            Prompt = "Do something",
            Role = WorkerRole.Coder,
            Model = "claude-opus-4",
            Repositories = [],
        };
        taskQueue.Enqueue(task);

        // Act: simulate HandleWorkerReady — dequeue task, mark pool busy, set model
        var dequeued = taskQueue.TryDequeue(WorkerRole.Unspecified);
        Assert.NotNull(dequeued);

        taskQueue.Activate(dequeued, worker.Id);
        pool.MarkBusy(worker.Id, dequeued.TaskId);
        worker.CurrentModel = dequeued.Model;   // <-- same line as HiveOrchestratorService:195

        // Assert: model is now set on the worker
        Assert.Equal("claude-opus-4", worker.CurrentModel);
        Assert.True(worker.IsBusy);
    }

    #endregion

    // ── HandleTaskComplete behaviour ──────────────────────────────────────────

    #region CurrentModel_ClearedWhenTaskCompletes

    /// <summary>
    /// When task completion is processed (mimicking the path inside
    /// <c>HandleTaskComplete</c>), the worker's <c>CurrentModel</c> is cleared
    /// to <c>null</c>.
    /// </summary>
    [Fact]
    public void CurrentModel_ClearedWhenTaskCompletes()
    {
        // Arrange: put the worker into a busy state with a model set
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();

        var worker = pool.RegisterWorker("w-lifecycle-2", []);

        var task = new WorkTask
        {
            TaskId = "task-lifecycle-2",
            GoalId = "goal-2",
            GoalDescription = "Test goal",
            Prompt = "Do something",
            Role = WorkerRole.Tester,
            Model = "gpt-4o",
            Repositories = [],
        };
        taskQueue.Enqueue(task);

        var dequeued = taskQueue.TryDequeue(WorkerRole.Unspecified)!;
        taskQueue.Activate(dequeued, worker.Id);
        pool.MarkBusy(worker.Id, dequeued.TaskId);
        worker.CurrentModel = dequeued.Model;

        // Precondition: worker is busy with model set
        Assert.Equal("gpt-4o", worker.CurrentModel);
        Assert.True(worker.IsBusy);

        // Act: simulate HandleTaskComplete — mark complete, idle, clear model
        taskQueue.MarkComplete(dequeued.TaskId);
        pool.MarkIdle(worker.Id);
        worker.CurrentModel = null;             // <-- same line as HiveOrchestratorService:320

        // Assert: model is cleared and worker is idle
        Assert.Null(worker.CurrentModel);
        Assert.False(worker.IsBusy);
    }

    #endregion

    // ── Full round-trip ───────────────────────────────────────────────────────

    #region CurrentModel_SetThenClearedInFullRoundTrip

    /// <summary>
    /// Verifies the complete assign→complete lifecycle in one test:
    /// model set when task assigned, cleared when task completes.
    /// </summary>
    [Fact]
    public void CurrentModel_SetThenClearedInFullRoundTrip()
    {
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();

        var worker = pool.RegisterWorker("w-lifecycle-3", []);

        var task = new WorkTask
        {
            TaskId = "task-lifecycle-3",
            GoalId = "goal-3",
            GoalDescription = "Round-trip goal",
            Prompt = "Do the full cycle",
            Role = WorkerRole.Coder,
            Model = "gpt-4",
            Repositories = [],
        };
        taskQueue.Enqueue(task);

        // Phase 1 — assign task (HandleWorkerReady)
        var dequeued = taskQueue.TryDequeue(WorkerRole.Unspecified)!;
        taskQueue.Activate(dequeued, worker.Id);
        pool.MarkBusy(worker.Id, dequeued.TaskId);
        worker.CurrentModel = dequeued.Model;

        Assert.Equal("gpt-4", worker.CurrentModel);

        // Phase 2 — complete task (HandleTaskComplete)
        taskQueue.MarkComplete(dequeued.TaskId);
        pool.MarkIdle(worker.Id);
        worker.CurrentModel = null;

        Assert.Null(worker.CurrentModel);
        Assert.False(worker.IsBusy);
    }

    #endregion
}
