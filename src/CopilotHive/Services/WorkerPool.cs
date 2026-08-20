using System.Collections.Concurrent;
using CopilotHive.Models;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>Aggregate statistics about the worker pool at a point in time.</summary>
public sealed record WorkerPoolStats
{
    /// <summary>Total number of registered workers.</summary>
    public required int TotalWorkers { get; init; }
    /// <summary>Number of workers currently executing a task.</summary>
    public required int BusyWorkers { get; init; }
    /// <summary>Number of workers that are idle and available for tasks.</summary>
    public required int IdleWorkers { get; init; }
    /// <summary>Count of workers grouped by their role string.</summary>
    public required IReadOnlyDictionary<string, int> WorkersByRole { get; init; }
}

/// <summary>
/// Thread-safe registry of currently connected workers. Supports registration,
/// lookup, heartbeat tracking, and busy/idle state management.
/// </summary>
public sealed class WorkerPool : IWorkerPool
{
    private readonly ConcurrentDictionary<string, ConnectedWorker> _workers = new();

    /// <summary>
    /// Guards all reads and writes of mutable worker state: <see cref="ConnectedWorker.IsBusy"/>,
    /// <see cref="ConnectedWorker.CurrentTaskId"/>, <see cref="ConnectedWorker.CurrentTaskStartedAt"/>,
    /// <see cref="ConnectedWorker.LastActivityAt"/>, <see cref="ConnectedWorker.LastHeartbeat"/> and
    /// <see cref="ConnectedWorker.ContextUsagePercent"/>.
    /// <para>
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> only makes the dictionary itself thread-safe —
    /// it establishes no happens-before relationship for mutable fields on the stored values. Without
    /// this lock the stream thread's activity write may never become visible to the cleanup thread,
    /// and inactivity-based reclamation could evict a worker that has just reported progress.
    /// </para>
    /// <para>
    /// The lock also covers dictionary membership changes made on the strength of that state
    /// (<see cref="TryRemoveTimedOutWorker"/> and <see cref="PurgeStaleWorkers"/>), so a worker is
    /// never transiently absent from the pool while its state is being evaluated. A transient
    /// absence would make <see cref="TouchActivity"/> silently drop a stream activity update and
    /// let the following inactivity scan evict a worker that was, in fact, active.
    /// </para>
    /// </summary>
    private readonly Lock _activityLock = new();

    /// <summary>
    /// Registers a new worker with the pool and returns the created <see cref="ConnectedWorker"/>.
    /// Throws if a worker with the same ID is already registered.
    /// </summary>
    /// <param name="id">Unique identifier for the worker.</param>
    /// <param name="capabilities">Capabilities advertised by the worker.</param>
    /// <returns>The newly created <see cref="ConnectedWorker"/>.</returns>
    public ConnectedWorker RegisterWorker(string id, string[] capabilities)
    {
        var worker = new ConnectedWorker
        {
            Id = id,
            Role = WorkerRole.Unspecified,
            Capabilities = capabilities,
        };

        if (!_workers.TryAdd(id, worker))
            throw new InvalidOperationException($"Worker '{id}' is already registered.");

        return worker;
    }

    /// <summary>
    /// Removes a worker from the pool by ID and closes its message channel.
    /// </summary>
    /// <remarks>
    /// NOT instance-safe: if a replacement worker has re-registered under the same ID (ABA),
    /// this removes the replacement. Use <see cref="RemoveWorker(ConnectedWorker)"/> for
    /// removal that may race with re-registration.
    /// </remarks>
    /// <param name="id">Identifier of the worker to remove.</param>
    /// <returns><c>true</c> if a worker with the ID was found and removed; <c>false</c> otherwise.</returns>
    public bool RemoveWorker(string id)
    {
        if (!_workers.TryRemove(id, out var worker))
            return false;

        worker.MessageChannel.Writer.TryComplete();
        return true;
    }

    /// <summary>
    /// Removes the given worker instance from the pool, but only if that exact instance is
    /// still registered under its ID. Instance-aware: a replacement instance registered
    /// under the same ID (ABA) is never removed by this call.
    /// </summary>
    /// <param name="worker">The exact <see cref="ConnectedWorker"/> instance to remove.</param>
    /// <returns>
    /// <c>true</c> if the exact instance was found and removed (and its message channel
    /// completed); <c>false</c> if a different instance — or nothing — is registered.
    /// </returns>
    public bool RemoveWorker(ConnectedWorker worker)
    {
        if (!_workers.TryRemove(new KeyValuePair<string, ConnectedWorker>(worker.Id, worker)))
            return false;

        worker.MessageChannel.Writer.TryComplete();
        return true;
    }

    /// <summary>
    /// Returns the first idle worker. All workers are generic and accept any role.
    /// </summary>
    /// <returns>An idle <see cref="ConnectedWorker"/>, or <c>null</c>.</returns>
    public ConnectedWorker? GetIdleWorker()
    {
        foreach (var kvp in _workers)
        {
            if (!kvp.Value.IsBusy)
                return kvp.Value;
        }

        return null;
    }

    /// <summary>Returns a read-only snapshot of all currently registered workers.</summary>
    public IReadOnlyList<ConnectedWorker> GetAllWorkers() =>
        _workers.Values.ToList().AsReadOnly();

    /// <summary>
    /// Looks up a worker by its identifier.
    /// </summary>
    /// <param name="id">Identifier of the worker to retrieve.</param>
    /// <returns>The worker, or <c>null</c> if not found.</returns>
    public ConnectedWorker? GetWorker(string id) =>
        _workers.GetValueOrDefault(id);

    /// <summary>
    /// Gets the number of workers currently registered in the pool.
    /// </summary>
    /// <returns>The count of entries in the internal worker dictionary.</returns>
    public int ConnectedWorkerCount => _workers.Count;

    /// <summary>
    /// Updates the last heartbeat timestamp for the specified worker.
    /// </summary>
    /// <remarks>
    /// Taken under <c>_activityLock</c> so the write is ordered against
    /// <see cref="PurgeStaleWorkers"/>, which decides eviction on the strength of this timestamp.
    /// </remarks>
    /// <param name="id">Identifier of the worker.</param>
    /// <param name="contextUsagePercent">Estimated context window usage as a percentage (0–100).</param>
    public void UpdateHeartbeat(string id, int contextUsagePercent = 0)
    {
        lock (_activityLock)
        {
            if (!_workers.TryGetValue(id, out var worker))
                return;

            worker.LastHeartbeat = DateTime.UtcNow;
            worker.ContextUsagePercent = contextUsagePercent;
        }
    }

    /// <summary>
    /// Marks the specified worker as busy with a task.
    /// </summary>
    /// <remarks>
    /// All four fields are published under <c>_activityLock</c> so that a reader can never observe
    /// the new <see cref="ConnectedWorker.IsBusy"/>/<see cref="ConnectedWorker.CurrentTaskId"/>
    /// together with a stale <see cref="ConnectedWorker.LastActivityAt"/> from a previous task —
    /// which would make a freshly assigned worker look immediately timed out.
    /// </remarks>
    /// <param name="id">Identifier of the worker.</param>
    /// <param name="taskId">Identifier of the task the worker is executing.</param>
    public void MarkBusy(string id, string taskId)
    {
        lock (_activityLock)
        {
            if (!_workers.TryGetValue(id, out var worker))
                return;

            var now = DateTime.UtcNow;
            // Reset the activity clock BEFORE publishing busy state so the worker is never
            // observable as "busy with an old LastActivityAt".
            worker.LastActivityAt = now;
            worker.CurrentTaskStartedAt = now;
            worker.CurrentTaskId = taskId;
            worker.IsBusy = true;
        }
    }

    /// <summary>
    /// Records task-specific stream activity (ToolRequest, Progress, or Complete) for the
    /// specified worker by resetting its <see cref="ConnectedWorker.LastActivityAt"/> to now.
    /// This is the single synchronized authority for activity updates: the lookup and the write
    /// both happen under the same lock as <see cref="GetWorkersWithTimedOutTasks"/> and
    /// <see cref="TryRemoveTimedOutWorker"/>, so an activity update is always visible to — and
    /// strictly ordered against — inactivity-based reclamation. In particular, a worker can never
    /// be both touched and reclaimed: whichever operation acquires the lock first wins, and the
    /// other observes the result (fresh timestamp, or the worker already gone).
    /// </summary>
    /// <param name="id">Identifier of the worker that produced the activity.</param>
    /// <returns>
    /// <c>true</c> if the worker was still in the pool and its activity was recorded;
    /// <c>false</c> if it is no longer registered.
    /// </returns>
    public bool TouchActivity(string id)
    {
        lock (_activityLock)
        {
            if (!_workers.TryGetValue(id, out var worker))
                return false;

            worker.LastActivityAt = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Marks the specified worker as idle, clearing its current task identifier.
    /// </summary>
    /// <param name="id">Identifier of the worker.</param>
    public void MarkIdle(string id)
    {
        lock (_activityLock)
        {
            if (!_workers.TryGetValue(id, out var worker))
                return;

            worker.IsBusy = false;
            worker.CurrentTaskId = null;
            worker.CurrentTaskStartedAt = null;
            worker.Role = WorkerRole.Unspecified;
        }
    }

    /// <summary>
    /// Returns workers whose last heartbeat exceeds the given timeout (i.e., stale).
    /// </summary>
    /// <remarks>
    /// A read-only snapshot, not part of the removal path — but taken under <c>_activityLock</c> so
    /// the heartbeat values it reports are consistent with concurrent <see cref="UpdateHeartbeat"/>
    /// writes rather than possibly-unpublished ones.
    /// </remarks>
    /// <param name="timeout">Maximum acceptable time since the last heartbeat.</param>
    /// <returns>A read-only list of stale <see cref="ConnectedWorker"/> instances.</returns>
    public IReadOnlyList<ConnectedWorker> GetStaleWorkers(TimeSpan timeout)
    {
        lock (_activityLock)
        {
            var now = DateTime.UtcNow;
            return _workers.Values
                .Where(w => now - w.LastHeartbeat > timeout)
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>
    /// Returns workers that are busy with a task whose last task-specific stream activity
    /// (ToolRequest, Progress, or Complete) occurred longer ago than <paramref name="timeout"/>.
    /// Such workers are still heartbeating, so <see cref="GetStaleWorkers"/> will not report them,
    /// but their task has gone silent and would otherwise hold its pipeline slot forever.
    /// <para>
    /// This is only a <em>candidate selector</em>: the returned references are live and their
    /// activity may be refreshed before the caller acts. Callers must remove candidates via
    /// <see cref="TryRemoveTimedOutWorker"/>, which re-checks the condition atomically.
    /// </para>
    /// </summary>
    /// <param name="timeout">Maximum acceptable inactivity duration for a single task.</param>
    /// <returns>A read-only list of <see cref="ConnectedWorker"/> instances with silent tasks.</returns>
    public IReadOnlyList<ConnectedWorker> GetWorkersWithTimedOutTasks(TimeSpan timeout)
    {
        // Read the activity state under the lock so the snapshot is consistent with concurrent
        // TouchActivity/MarkBusy writes (no torn or never-published values).
        lock (_activityLock)
        {
            var now = DateTime.UtcNow;
            return _workers.Values
                .Where(w => IsTimedOutNoLock(w, now, timeout))
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>
    /// Atomically re-checks that the identified worker is still busy with a task that has been
    /// inactive for longer than <paramref name="timeout"/> and, only if so, removes it from the
    /// pool and completes its message channel.
    /// <para>
    /// This closes the time-of-check/time-of-use race in inactivity-based reclamation: between
    /// <see cref="GetWorkersWithTimedOutTasks"/> selecting a candidate and the caller acting on it,
    /// the worker may have reported new activity or finished its task. Re-checking under the same
    /// lock that <see cref="TouchActivity"/> takes guarantees such a worker is never evicted.
    /// </para>
    /// </summary>
    /// <param name="id">Identifier of the candidate worker to remove.</param>
    /// <param name="timeout">Maximum acceptable inactivity duration for a single task.</param>
    /// <returns>
    /// <c>true</c> if the worker was still timed out and has been removed; <c>false</c> if it is
    /// gone, is no longer busy with a task, or has shown activity since it was selected.
    /// </returns>
    public bool TryRemoveTimedOutWorker(string id, TimeSpan timeout)
    {
        ConnectedWorker removed;

        lock (_activityLock)
        {
            if (!_workers.TryGetValue(id, out var worker))
                return false;

            if (!IsTimedOutNoLock(worker, DateTime.UtcNow, timeout))
                return false;

            // Remove the exact instance we validated, so a re-registered worker under the same ID
            // is never removed on the strength of the old instance's timestamps.
            if (!_workers.TryRemove(new KeyValuePair<string, ConnectedWorker>(id, worker)))
                return false;

            removed = worker;
        }

        // Complete the channel outside the lock: completion can resume the worker's stream reader
        // inline, and no reader continuation should ever run while the activity lock is held.
        removed.MessageChannel.Writer.TryComplete();
        return true;
    }

    /// <summary>
    /// Evaluates the inactivity-timeout predicate. Callers must hold <c>_activityLock</c>.
    /// </summary>
    private static bool IsTimedOutNoLock(ConnectedWorker worker, DateTime now, TimeSpan timeout) =>
        worker.IsBusy
        && worker.CurrentTaskId is not null
        && now - worker.LastActivityAt > timeout;

    /// <summary>Returns aggregate statistics about the worker pool.</summary>
    /// <returns>A <see cref="WorkerPoolStats"/> snapshot.</returns>
    public WorkerPoolStats GetWorkerStats()
    {
        var workers = _workers.Values.ToList();
        var workersByRole = workers
            .GroupBy(w => w.Role.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        return new WorkerPoolStats
        {
            TotalWorkers = workers.Count,
            BusyWorkers = workers.Count(w => w.IsBusy),
            IdleWorkers = workers.Count(w => !w.IsBusy),
            WorkersByRole = workersByRole,
        };
    }

    /// <summary>
    /// Returns detailed worker pool statistics including per-worker information,
    /// suitable for the <c>/health</c> endpoint response.
    /// </summary>
    /// <returns>A <see cref="WorkerPoolStatsDto"/> snapshot with worker details.</returns>
    public WorkerPoolStatsDto GetDetailedStats()
    {
        var workers = _workers.Values.ToList();
        return new WorkerPoolStatsDto
        {
            TotalWorkers = workers.Count,
            IdleWorkers = workers.Count(w => !w.IsBusy),
            BusyWorkers = workers.Count(w => w.IsBusy),
            Workers = workers.Select(w => new WorkerInfoDto
            {
                Id = w.Id,
                Role = w.Role == WorkerRole.Unspecified ? null : w.Role.ToString(),
                IsBusy = w.IsBusy,
                CurrentTaskId = w.CurrentTaskId,
            }).ToList(),
        };
    }

    /// <summary>
    /// Removes stale workers from the pool and returns them.
    /// </summary>
    /// <remarks>
    /// The staleness check and the removal happen together under <c>_activityLock</c>, and a fresh
    /// worker is left in place rather than being removed and re-added. An earlier implementation
    /// removed every entry first and reinserted the fresh ones, which made workers transiently
    /// absent from the dictionary: a concurrent <see cref="TouchActivity"/> landing in that window
    /// returned <c>false</c> and silently dropped the activity update, after which the following
    /// inactivity scan could evict a worker that had just reported progress. Holding the lock for
    /// the whole decision makes this purge atomic with respect to <see cref="TouchActivity"/>,
    /// <see cref="UpdateHeartbeat"/>, <see cref="MarkBusy"/>, <see cref="MarkIdle"/> and
    /// <see cref="TryRemoveTimedOutWorker"/>.
    /// </remarks>
    /// <param name="timeout">Maximum acceptable time since the last heartbeat.</param>
    /// <returns>A read-only list of the removed <see cref="ConnectedWorker"/> instances.</returns>
    public IReadOnlyList<ConnectedWorker> PurgeStaleWorkers(TimeSpan timeout)
    {
        var removed = new List<ConnectedWorker>();

        lock (_activityLock)
        {
            // Snapshot now once so all staleness decisions are made against a consistent point in time.
            var now = DateTime.UtcNow;

            foreach (var key in _workers.Keys.ToList())
            {
                if (!_workers.TryGetValue(key, out var worker))
                    continue;

                // Fresh workers are never touched — no transient removal, so no window in which
                // a concurrent activity or heartbeat update could be lost.
                if (now - worker.LastHeartbeat <= timeout)
                    continue;

                // Remove the exact instance we validated as stale.
                if (_workers.TryRemove(new KeyValuePair<string, ConnectedWorker>(key, worker)))
                    removed.Add(worker);
            }
        }

        // Complete channels outside the lock: completion can resume a worker's stream reader
        // inline, and no reader continuation should ever run while the activity lock is held.
        foreach (var worker in removed)
            worker.MessageChannel.Writer.TryComplete();

        return removed.AsReadOnly();
    }
}
