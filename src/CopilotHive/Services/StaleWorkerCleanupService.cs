using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Hosted background service that periodically scans the worker pool for stale workers
/// (workers whose heartbeat has not been received within <see cref="CleanupDefaults.StaleTimeoutMinutes"/>
/// minutes) and removes them. When a stale worker had an active task, the task is
/// re-enqueued for reassignment to another worker.
/// </summary>
public sealed class StaleWorkerCleanupService : BackgroundService
{
    private readonly IWorkerPool _workerPool;
    private readonly TaskQueue _taskQueue;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly GoalDispatcher? _goalDispatcher;
    private readonly ILogger<StaleWorkerCleanupService> _logger;

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
        GoalDispatcher? goalDispatcher = null)
    {
        _workerPool = workerPool;
        _taskQueue = taskQueue;
        _pipelineManager = pipelineManager;
        _logger = logger;
        _goalDispatcher = goalDispatcher;
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

        foreach (var worker in removed)
        {
            _logger.LogWarning("Removing stale worker {WorkerId} (lastHeartbeat={LastHeartbeat})",
                worker.Id, worker.LastHeartbeat);

            if (worker.IsBusy && worker.CurrentTaskId is not null)
            {
                RescheduleAbandonedTask(worker.Id, worker.CurrentTaskId);
            }
        }

        return Task.CompletedTask;
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
