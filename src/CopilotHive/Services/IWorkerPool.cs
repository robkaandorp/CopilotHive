namespace CopilotHive.Services;

/// <summary>
/// Abstraction over the worker pool used by hosted services and other consumers
/// that should not depend on the concrete <see cref="WorkerPool"/> implementation.
/// </summary>
public interface IWorkerPool
{
    /// <summary>
    /// Gets the number of workers currently registered in the pool.
    /// </summary>
    /// <returns>The count of entries in the internal worker dictionary.</returns>
    int ConnectedWorkerCount { get; }

    /// <summary>
    /// Returns workers whose last heartbeat exceeds the given timeout.
    /// </summary>
    /// <param name="timeout">Maximum acceptable time since the last heartbeat.</param>
    /// <returns>A read-only list of stale <see cref="ConnectedWorker"/> instances.</returns>
    IReadOnlyList<ConnectedWorker> GetStaleWorkers(TimeSpan timeout);

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
    IReadOnlyList<ConnectedWorker> GetWorkersWithTimedOutTasks(TimeSpan timeout);

    /// <summary>
    /// Atomically re-checks that the identified worker is still busy with a task that has been
    /// inactive for longer than <paramref name="timeout"/> and, only if so, removes it from the pool.
    /// Closes the time-of-check/time-of-use race between
    /// <see cref="GetWorkersWithTimedOutTasks"/> selecting a candidate and the caller evicting it.
    /// </summary>
    /// <param name="id">Identifier of the candidate worker to remove.</param>
    /// <param name="timeout">Maximum acceptable inactivity duration for a single task.</param>
    /// <returns>
    /// <c>true</c> if the worker was still timed out and has been removed; <c>false</c> if it is
    /// gone, is no longer busy with a task, or has shown activity since it was selected.
    /// </returns>
    bool TryRemoveTimedOutWorker(string id, TimeSpan timeout);

    /// <summary>
    /// Removes a worker from the pool.
    /// </summary>
    /// <param name="id">Identifier of the worker to remove.</param>
    /// <returns><c>true</c> if the worker was found and removed; <c>false</c> otherwise.</returns>
    bool RemoveWorker(string id);

    /// <summary>
    /// Atomically removes all stale workers from the pool and returns them.
    /// A worker is stale when its last heartbeat exceeds the given staleness threshold.
    /// </summary>
    /// <param name="staleness">Maximum acceptable time since the last heartbeat.</param>
    /// <returns>A read-only list of the removed <see cref="ConnectedWorker"/> instances.</returns>
    IReadOnlyList<ConnectedWorker> PurgeStaleWorkers(TimeSpan staleness);
}
