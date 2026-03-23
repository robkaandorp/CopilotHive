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
