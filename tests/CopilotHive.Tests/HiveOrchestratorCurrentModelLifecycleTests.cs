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
    /// Calling <see cref="HiveOrchestratorService.ApplyTaskCompletion"/> for the worker's OWN
    /// current task reports success and clears <see cref="ConnectedWorker.CurrentModel"/> to
    /// <c>null</c> INSIDE the checked release.
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
        Assert.True(service.ApplyTaskCompletion(worker, dequeued.TaskId));

        // Assert: model is cleared, worker is idle and the active entry is gone
        Assert.Null(worker.CurrentModel);
        Assert.False(worker.IsBusy);
        Assert.Null(worker.CurrentTaskId);
        Assert.Null(taskQueue.GetActiveTask(dequeued.TaskId));
    }

    #endregion

    #region ApplyTaskCompletion — checked release refusals

    /// <summary>
    /// A completion for a DIFFERENT task id than the one the worker is executing is REFUSED: the
    /// busy state, the current task, the model and the successor's active queue entry all survive.
    /// </summary>
    [Fact]
    public void ApplyTaskCompletion_ForeignTaskId_IsRefusedAndMutatesNothing()
    {
        var (service, pool, taskQueue) = CreateService();
        var worker = pool.RegisterWorker("w-complete-foreign", []);

        var successor = new WorkTask
        {
            TaskId = "task-successor",
            GoalId = "goal-foreign",
            GoalDescription = "Successor",
            Prompt = "Work",
            Role = WorkerRole.Coder,
            Model = "successor-model",
            Repositories = [],
        };
        taskQueue.Enqueue(successor);
        var dequeued = taskQueue.TryDequeue(WorkerRole.Unspecified)!;
        service.ApplyTaskAssignment(worker, dequeued);

        // A LATE predecessor completion arrives for a task this worker no longer owns.
        Assert.False(service.ApplyTaskCompletion(worker, "task-predecessor"));

        Assert.True(worker.IsBusy);
        Assert.Equal("task-successor", worker.CurrentTaskId);
        Assert.Equal("successor-model", worker.CurrentModel);
        Assert.NotNull(taskQueue.GetActiveTask("task-successor"));
    }

    /// <summary>
    /// A completion delivered against an ALREADY-REPLACED worker instance (ABA) is REFUSED, and
    /// the replacement registered under the same ID keeps its own assignment untouched.
    /// </summary>
    [Fact]
    public void ApplyTaskCompletion_ReplacedWorkerInstance_IsRefused()
    {
        var (service, pool, taskQueue) = CreateService();
        var stale = pool.RegisterWorker("w-aba", []);

        var task = new WorkTask
        {
            TaskId = "task-aba",
            GoalId = "goal-aba",
            GoalDescription = "ABA",
            Prompt = "Work",
            Role = WorkerRole.Coder,
            Model = "stale-model",
            Repositories = [],
        };
        taskQueue.Enqueue(task);
        var dequeued = taskQueue.TryDequeue(WorkerRole.Unspecified)!;
        service.ApplyTaskAssignment(stale, dequeued);

        // The stale instance is replaced under the SAME id.
        Assert.True(pool.RemoveWorker(stale));
        var replacement = pool.RegisterWorker("w-aba", []);
        pool.MarkBusy("w-aba", "task-aba");
        replacement.CurrentModel = "replacement-model";

        Assert.False(service.ApplyTaskCompletion(stale, "task-aba"));

        // The replacement was never released, and the active entry was never removed.
        Assert.True(replacement.IsBusy);
        Assert.Equal("task-aba", replacement.CurrentTaskId);
        Assert.Equal("replacement-model", replacement.CurrentModel);
        Assert.NotNull(taskQueue.GetActiveTask("task-aba"));
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
        Assert.True(service.ApplyTaskCompletion(worker, dequeued.TaskId));
        Assert.Null(worker.CurrentModel);
        Assert.False(worker.IsBusy);
    }

    #endregion
}
