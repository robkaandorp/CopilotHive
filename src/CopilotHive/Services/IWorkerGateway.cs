namespace CopilotHive.Services;

/// <summary>
/// Abstracts communication with worker containers. Business logic uses this interface
/// instead of constructing transport-specific messages directly.
/// </summary>
public interface IWorkerGateway
{
    /// <summary>Sends a task assignment to the specified worker.</summary>
    Task SendTaskAsync(string workerId, WorkTask task, CancellationToken ct = default);

    /// <summary>Sends a cancellation request to the specified worker.</summary>
    Task SendCancelAsync(string workerId, string taskId, string reason, CancellationToken ct = default);

    /// <summary>Sends an agents.md update to the specified worker.</summary>
    Task SendAgentsUpdateAsync(string workerId, string role, string content, CancellationToken ct = default);

    /// <summary>Returns the first idle worker, or null if none available.</summary>
    ConnectedWorker? GetIdleWorker();

    /// <summary>Returns all connected workers.</summary>
    IReadOnlyList<ConnectedWorker> GetAllWorkers();

    /// <summary>Marks a worker as busy with the given task.</summary>
    void MarkBusy(string workerId, string taskId);
}
