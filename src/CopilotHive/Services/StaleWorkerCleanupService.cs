using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Hosted background service that periodically scans the worker pool for stale workers
/// (workers whose heartbeat has not been received within <see cref="CleanupDefaults.StaleTimeoutMinutes"/>
/// minutes) and removes them. When a stale worker had an active task, the task is reclaimed:
/// its queue entry is completed (never re-enqueued — the re-enqueue interim is retired), its
/// pipeline work slot is retired atomically together with the active-task pointer, the durable
/// task→goal mapping is unregistered, and the goal is queued for a FRESH re-dispatch.
/// It also reclaims tasks that exceed <see cref="Configuration.OrchestratorConfig.WorkerTaskTimeoutMinutes"/>
/// even while the worker keeps heartbeating, which covers hung LLM calls.
/// </summary>
/// <remarks>
/// <para>
/// THE TWO BELTS. A reclaim covers the race between the reclaim and a completion (or a later
/// dispatch) through two independent belts: (1) the SLOT RETIREMENT — the D1 primitive
/// <see cref="GoalPipeline.RetireSlotAndClearIfCurrent"/>, which atomically retires the attempt's
/// work slot and clears the active-task pointer only when it still names the retired task; and
/// (2) THE MAPPING UNREGISTER — <see cref="GoalPipelineManager.TryUnregisterTask"/>, which removes
/// the durable task→goal mapping in memory and in the persisted store so the task can never
/// resolve back to this pipeline. The completion path's abandoned-slot guard
/// (<see cref="TaskCompletionService"/>) drops any completion that arrives in the retire's
/// observation window — <c>RescheduleAbandonedTask</c> is synchronous with NO seam between the
/// retire and the unregister, so the in-window echo is covered by the D1 suite's reclaim-path
/// fixture (retire linearized, mapping intact, the completion guard's drop) — referenced here,
/// NOT duplicated here.
/// </para>
/// <para>
/// THE RESTORED-POLICY SUCCESSOR. The pipeline's slot retirement and the cleared pointer are
/// IN-MEMORY: this reclaim calls NO <c>PersistFull</c> (or any other persistence) — the cleanup's
/// mutation is intentionally not persisted. A pipeline restored after a restart has an EMPTY slot
/// registry and its pointer is the snapshot's; the reconciliation of that restored state is owned
/// by the completion-protocol successor, not by this method.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Reclaims the task a removed (stale or timed-out) worker was holding, using the D2 shape:
    /// <list type="number">
    ///   <item><description><see cref="TaskQueue.MarkComplete"/> on the task's queue entry —
    ///     UNCONDITIONAL, with NO re-enqueue (the re-enqueue interim is retired; the queue entry
    ///     is dropped, not handed to another worker).</description></item>
    ///   <item><description>THE RETIRE — <see cref="GoalPipeline.RetireSlotAndClearIfCurrent"/>,
    ///     the D1 atomic primitive: the attempt's work slot is retired AND the active-task pointer
    ///     is cleared in one lock acquisition, when and only when the pointer still names the
    ///     retired task.</description></item>
    ///   <item><description>THE MAPPING UNREGISTER — the durable task→goal mapping is removed
    ///     from memory and (when a store is present) from the persisted <c>task_mappings</c>, so a
    ///     restart can never resolve the retired task back to this pipeline. The result is ALWAYS
    ///     logged at DEBUG; a <c>(true, false)</c> — memory removed but the persisted removal did
    ///     not confirm — is a WARNING in conservative wording (no row-survival claim; the
    ///     completion-protocol successor owns the durable reconciliation).</description></item>
    ///   <item><description>THE REDISPATCH — the goal is queued for a FRESH dispatch, which
    ///     captures a new position on the now-free position.</description></item>
    /// </list>
    /// The false retirement outcomes (<see cref="SlotRetirementOutcome.SlotAbsent"/> and
    /// <see cref="SlotRetirementOutcome.AlreadyAbandoned"/>) are CONTINUATIONS: the mapping
    /// unregister and the re-dispatch still run. The ORPHAN path (no pipeline for the task)
    /// removes the active entry and dispatches NOTHING — a persisted mapping, if any, survives
    /// for the successor's reconciliation.
    /// </summary>
    /// <param name="workerId">The id of the worker that was removed.</param>
    /// <param name="taskId">The task id the worker was holding.</param>
    private void RescheduleAbandonedTask(string workerId, string taskId)
    {
        // (1) THE UNCONDITIONAL COMPLETION. The queue entry is dropped, never re-enqueued: the
        // re-enqueue would double-dispatch (the old task re-enqueued AND the goal redispatched),
        // so the re-enqueue interim is retired — the replacement comes only from the fresh
        // dispatch the redispatch triggers.
        _taskQueue.MarkComplete(taskId);

        // The pipeline may not exist for the task (an orphan) — everything below is then skipped.
        var pipeline = _pipelineManager.GetByTaskId(taskId);
        if (pipeline is null)
        {
            _logger.LogWarning(
                "Worker {WorkerId} task {TaskId} reclaimed with no pipeline — the active entry removed; no re-dispatch (orphan; a persisted mapping, if any, survives for the successor's reconciliation)",
                workerId, taskId);
            return;
        }

        // (2) THE RETIRE: the D1 primitive. Slot retirement and the if-current pointer clear
        // happen inside a single lock acquisition.
        var outcome = pipeline.RetireSlotAndClearIfCurrent(taskId);

        // (3) THE MAPPING UNREGISTER — the durable belt. Retirement alone is in-memory only;
        // without the unregister a restart could still resolve the retired task to this pipeline.
        // THE IN-WINDOW ECHO: this method is synchronous with NO seam between the retire and the
        // unregister, so a completion arriving between them still resolves the pipeline and is
        // dropped by the completion guard's abandoned-slot check — covered by the D1 suite's
        // reclaim-path fixture, referenced in the class doc, NOT duplicated here.
        var unregister = new TaskUnregisterResult(false, false);
        try
        {
            unregister = _pipelineManager.TryUnregisterTask(taskId, pipeline.GoalId);
        }
        catch (Exception ex)
        {
            // THE DEFENSIVE CATCH. TryUnregisterTask's contract promises NEVER to throw; an escape
            // here is a contract violation (no runtime vector — the same sealed/non-virtual
            // treatment as the chain's established defensive paths, deliberately WITHOUT a
            // runtime-injection seam). The reclaim continues.
            _logger.LogWarning(
                ex,
                "WorkSlotIntegrity: reclaim-unregister-throw goal={GoalId} task={TaskId} — the unregister call threw; the reclaim continues",
                pipeline.GoalId, taskId);
        }

        _logger.LogDebug(
            "WorkSlotIntegrity: reclaim-unregister goal={GoalId} task={TaskId} memoryRemoved={MemoryRemoved} persistenceRemoved={PersistenceRemoved}",
            pipeline.GoalId, taskId, unregister.MemoryRemoved, unregister.PersistenceRemoved);

        if (unregister.MemoryRemoved && !unregister.PersistenceRemoved)
        {
            // THE UNCONFIRMED PERSISTED REMOVAL, in CONSERVATIVE wording: no row-survival claim —
            // the delete did not confirm; a restart may still resolve the task to this pipeline.
            // The completion-protocol successor owns the durable reconciliation.
            _logger.LogWarning(
                "WorkSlotIntegrity: reclaim-unregister goal={GoalId} task={TaskId} memoryRemoved={MemoryRemoved} persistenceRemoved={PersistenceRemoved} — the mapping's persisted removal did not confirm; a restart may resolve the retired task to this pipeline; the completion-protocol successor owns the durable reconciliation",
                pipeline.GoalId, taskId, unregister.MemoryRemoved, unregister.PersistenceRemoved);
        }

        // (4) THE REDISPATCH: the replacement comes from a FRESH dispatch on the retired slot's
        // now-free position, not from a re-enqueue of the old task.
        _goalDispatcher?.EnqueueRedispatch(pipeline.GoalId);

        // (5) THE OUTCOME-HONEST LOG.
        if (outcome is SlotRetirementOutcome.Retired)
        {
            if (_goalDispatcher is not null)
            {
                _logger.LogInformation(
                    "Worker {WorkerId} task {TaskId} reclaimed — slot retired; queued for re-dispatch (goal {GoalId})",
                    workerId, taskId, pipeline.GoalId);
            }
            else
            {
                _logger.LogInformation(
                    "Worker {WorkerId} task {TaskId} reclaimed — slot retired; no dispatcher available for re-dispatch (goal {GoalId})",
                    workerId, taskId, pipeline.GoalId);
            }
        }
        else
        {
            if (_goalDispatcher is not null)
            {
                _logger.LogInformation(
                    "Worker {WorkerId} task {TaskId} reclaimed — slot already retired or absent (outcome={Outcome}); queued for re-dispatch (goal {GoalId})",
                    workerId, taskId, outcome, pipeline.GoalId);
            }
            else
            {
                _logger.LogInformation(
                    "Worker {WorkerId} task {TaskId} reclaimed — slot already retired or absent (outcome={Outcome}); no dispatcher available for re-dispatch (goal {GoalId})",
                    workerId, taskId, outcome, pipeline.GoalId);
            }
        }
    }
}
