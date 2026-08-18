using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Hosted background service that periodically scans the worker pool for stale workers
/// (workers whose heartbeat has not been received within <see cref="CleanupDefaults.StaleTimeoutMinutes"/>
/// minutes) and removes them. When a stale worker had an active task, the task is
/// re-enqueued for reassignment to another worker.
/// It also reclaims tasks that exceed <see cref="Configuration.OrchestratorConfig.WorkerTaskTimeoutMinutes"/>
/// even while the worker keeps heartbeating, which covers hung LLM calls.
/// </summary>
public sealed class StaleWorkerCleanupService : BackgroundService
{
    private readonly IWorkerPool _workerPool;
    private readonly TaskQueue _taskQueue;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly GoalDispatcher? _goalDispatcher;
    private readonly Configuration.HiveConfigFile? _config;
    private readonly ILogger<StaleWorkerCleanupService> _logger;
    private readonly Dashboard.DashboardNotifier? _dashboardNotifier;

    /// <summary>
    /// Delay between cleanup passes. Defaults to <see cref="CleanupDefaults.CleanupIntervalSeconds"/>.
    /// Settable internally to enable fast-cycle testing without waiting 60 s.
    /// </summary>
    internal TimeSpan CleanupDelay { get; set; } = TimeSpan.FromSeconds(CleanupDefaults.CleanupIntervalSeconds);

    /// <summary>
    /// Initialises the service with the worker pool, task queue, pipeline manager, and a logger.
    /// </summary>
    public StaleWorkerCleanupService(
        IWorkerPool workerPool,
        TaskQueue taskQueue,
        GoalPipelineManager pipelineManager,
        ILogger<StaleWorkerCleanupService> logger,
        GoalDispatcher? goalDispatcher = null,
        Configuration.HiveConfigFile? config = null,
        Dashboard.DashboardNotifier? dashboardNotifier = null)
    {
        _workerPool = workerPool;
        _taskQueue = taskQueue;
        _pipelineManager = pipelineManager;
        _logger = logger;
        _goalDispatcher = goalDispatcher;
        _config = config;
        _dashboardNotifier = dashboardNotifier;
    }

    /// <summary>
    /// Runs a cleanup loop that waits <see cref="CleanupDefaults.CleanupIntervalSeconds"/> seconds between
    /// passes. On each pass it finds and removes all workers that have not sent a heartbeat
    /// within <see cref="CleanupDefaults.StaleTimeoutMinutes"/> minutes, logging a warning per eviction.
    /// The loop exits when <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    /// <param name="stoppingToken">Token that signals the host is stopping.</param>
    /// <returns>A <see cref="Task"/> that completes when the service has stopped.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupDelay, stoppingToken);
                await RunCleanupCycleAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is stopping — exit cleanly.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup cycle failed");
            }
        }
    }

    /// <summary>
    /// Performs a single cleanup pass: atomically purges all stale workers and
    /// logs a warning for each eviction.
    /// </summary>
    /// <returns>A <see cref="Task"/> that completes when the pass is finished.</returns>
    internal Task RunCleanupCycleAsync()
    {
        var removed = _workerPool.PurgeStaleWorkers(TimeSpan.FromMinutes(CleanupDefaults.StaleTimeoutMinutes));

        var anyRemoval = removed.Count > 0;

        foreach (var worker in removed)
        {
            _logger.LogWarning("Removing stale worker {WorkerId} (lastHeartbeat={LastHeartbeat})",
                worker.Id, worker.LastHeartbeat);

            if (worker.IsBusy && worker.CurrentTaskId is not null)
            {
                RescheduleAbandonedTask(worker.Id, worker.CurrentTaskId);
            }
        }

        var reclaimResult = ReclaimTimedOutTasks();
        anyRemoval = anyRemoval || reclaimResult;

        if (anyRemoval)
            _dashboardNotifier?.NotifyStateChanged();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Detects workers whose tasks have been inactive (no task-specific stream messages) for
    /// longer than the configured timeout. Still-heartbeating workers with silent tasks are
    /// reclaimed — this catches hung LLM calls.
    /// <para>
    /// Selection and eviction are two separate steps, so a candidate may report activity in
    /// between. Eviction therefore goes through <see cref="IWorkerPool.TryRemoveTimedOutWorker"/>,
    /// which atomically re-checks the inactivity condition; candidates that became active again
    /// are skipped without being logged or rescheduled.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> if at least one worker was removed.</returns>
    private bool ReclaimTimedOutTasks()
    {
        var timeoutMinutes = _config?.Orchestrator?.WorkerTaskTimeoutMinutes
            ?? CleanupDefaults.WorkerTaskTimeoutMinutes;

        if (timeoutMinutes <= 0)
            return false;

        var timeout = TimeSpan.FromMinutes(timeoutMinutes);
        var timedOut = _workerPool.GetWorkersWithTimedOutTasks(timeout);

        var anyRemoved = false;
        foreach (var worker in timedOut)
        {
            // Capture the task before eviction so the reschedule cannot race the worker's own state.
            var taskId = worker.CurrentTaskId;
            if (taskId is null)
                continue;

            // Atomically re-check inactivity and drop the hung worker so it cannot report a late
            // completion for a re-dispatched task. Returns false if activity arrived since selection.
            if (!_workerPool.TryRemoveTimedOutWorker(worker.Id, timeout))
                continue;

            anyRemoved = true;

            _logger.LogWarning(
                "Worker {WorkerId} task {TaskId} inactive since {LastActivityAt} — reclaiming",
                worker.Id, taskId, worker.LastActivityAt);

            RescheduleAbandonedTask(worker.Id, taskId);
        }

        return anyRemoved;
    }

    private void RescheduleAbandonedTask(string workerId, string taskId)
    {
        var task = _taskQueue.GetActiveTask(taskId);
        if (task is null)
        {
            _logger.LogWarning("Stale worker {WorkerId} had task {TaskId} but it is not in the active queue — clearing pipeline",
                workerId, taskId);
        }
        else
        {
            _taskQueue.MarkComplete(taskId);
            _taskQueue.Enqueue(task);
            _logger.LogWarning("Re-enqueued task {TaskId} from dead worker {WorkerId} for reassignment",
                taskId, workerId);
        }

        // Clear the pipeline's active task and signal the dispatcher to re-dispatch
        var pipeline = _pipelineManager.GetByTaskId(taskId);
        if (pipeline is not null)
        {
            pipeline.ClearActiveTask();
            _goalDispatcher?.EnqueueRedispatch(pipeline.GoalId);
            _logger.LogInformation("Cleared active task on pipeline {GoalId} — queued for re-dispatch",
                pipeline.GoalId);
        }
    }
}
