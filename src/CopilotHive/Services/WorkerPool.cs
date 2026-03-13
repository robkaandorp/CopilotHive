using System.Collections.Concurrent;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

/// <summary>
/// Thread-safe registry of currently connected workers. Supports registration,
/// lookup, heartbeat tracking, and busy/idle state management.
/// </summary>
public sealed class WorkerPool
{
    private readonly ConcurrentDictionary<string, ConnectedWorker> _workers = new();

    /// <summary>
    /// Registers a new worker with the pool and returns the created <see cref="ConnectedWorker"/>.
    /// Throws if a worker with the same ID is already registered.
    /// </summary>
    /// <param name="id">Unique identifier for the worker.</param>
    /// <param name="role">Role of the worker.</param>
    /// <param name="capabilities">Capabilities advertised by the worker.</param>
    /// <returns>The newly created <see cref="ConnectedWorker"/>.</returns>
    public ConnectedWorker RegisterWorker(string id, WorkerRole role, string[] capabilities)
    {
        var worker = new ConnectedWorker
        {
            Id = id,
            Role = role,
            Capabilities = capabilities,
        };

        if (!_workers.TryAdd(id, worker))
            throw new InvalidOperationException($"Worker '{id}' is already registered.");

        return worker;
    }

    /// <summary>
    /// Removes a worker from the pool and closes its message channel.
    /// </summary>
    /// <param name="id">Identifier of the worker to remove.</param>
    /// <returns><c>true</c> if the worker was found and removed; <c>false</c> otherwise.</returns>
    public bool RemoveWorker(string id)
    {
        if (!_workers.TryRemove(id, out var worker))
            return false;

        worker.MessageChannel.Writer.TryComplete();
        return true;
    }

    /// <summary>
    /// Returns the first idle worker with the specified role, or <c>null</c> when none is available.
    /// </summary>
    /// <param name="role">The required worker role.</param>
    /// <returns>An idle <see cref="ConnectedWorker"/>, or <c>null</c>.</returns>
    public ConnectedWorker? GetIdleWorker(WorkerRole role)
    {
        foreach (var kvp in _workers)
        {
            var w = kvp.Value;
            if (w.Role == role && !w.IsBusy)
                return w;
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
    /// Updates the last heartbeat timestamp for the specified worker.
    /// </summary>
    /// <param name="id">Identifier of the worker.</param>
    public void UpdateHeartbeat(string id)
    {
        if (_workers.TryGetValue(id, out var worker))
            worker.LastHeartbeat = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the specified worker as busy with a task.
    /// </summary>
    /// <param name="id">Identifier of the worker.</param>
    /// <param name="taskId">Identifier of the task the worker is executing.</param>
    public void MarkBusy(string id, string taskId)
    {
        if (_workers.TryGetValue(id, out var worker))
        {
            worker.IsBusy = true;
            worker.CurrentTaskId = taskId;
        }
    }

    /// <summary>
    /// Marks the specified worker as idle, clearing its current task identifier.
    /// </summary>
    /// <param name="id">Identifier of the worker.</param>
    public void MarkIdle(string id)
    {
        if (_workers.TryGetValue(id, out var worker))
        {
            worker.IsBusy = false;
            worker.CurrentTaskId = null;
        }
    }
}
