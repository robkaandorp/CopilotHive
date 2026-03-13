namespace CopilotHive.Workers;

/// <summary>
/// Manages the lifecycle of worker containers/processes.
/// </summary>
public interface IWorkerManager : IAsyncDisposable
{
    /// <summary>Read-only view of all currently tracked workers, keyed by worker ID.</summary>
    IReadOnlyDictionary<string, WorkerInfo> Workers { get; }

    /// <summary>
    /// Removes stale containers from previous runs.
    /// </summary>
    Task CleanupStaleContainersAsync(CancellationToken ct = default);

    /// <summary>
    /// Spawns a new worker container for the specified role.
    /// </summary>
    /// <param name="role">Worker role to spawn.</param>
    /// <param name="clonePath">Path to the worker's git workspace clone.</param>
    /// <param name="agentsMdPath">Path to the AGENTS.md directory.</param>
    /// <param name="model">Model identifier to use inside the container.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="WorkerInfo"/> describing the spawned container.</returns>
    Task<WorkerInfo> SpawnWorkerAsync(
        WorkerRole role,
        string clonePath,
        string agentsMdPath,
        string model,
        CancellationToken ct = default);

    /// <summary>
    /// Stops and removes the container for the specified worker.
    /// </summary>
    /// <param name="workerId">Identifier of the worker to stop.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StopWorkerAsync(string workerId, CancellationToken ct = default);

    /// <summary>Stops and removes all tracked worker containers.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task StopAllWorkersAsync(CancellationToken ct = default);
}
