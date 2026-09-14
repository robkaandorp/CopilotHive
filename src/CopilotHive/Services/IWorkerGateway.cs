namespace CopilotHive.Services;

/// <summary>
/// Abstracts communication with worker containers. Business logic uses this interface
/// instead of constructing transport-specific messages directly.
/// </summary>
public interface IWorkerGateway
{
    /// <summary>
    /// Sends a task assignment to the specified worker, reporting whether the assignment was
    /// actually published or refused (blocked) by the assignment-recording contract.
    /// </summary>
    /// <param name="workerId">The id of the worker the assignment is for.</param>
    /// <param name="task">The delivered work task.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// <see cref="WorkerTaskSendOutcome.Published"/> once the assignment's channel publication has
    /// completed; <see cref="WorkerTaskSendOutcome.Blocked"/> when the assignment was refused by the
    /// recording contract and deliberately NOT published. A missing worker, the caller's own
    /// cancellation and any post-record mapping/channel failure remain EXCEPTIONS and are never
    /// reported as <see cref="WorkerTaskSendOutcome.Blocked"/>.
    /// </returns>
    Task<WorkerTaskSendOutcome> SendTaskAsync(string workerId, WorkTask task, CancellationToken ct = default);

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

/// <summary>
/// The outcome of <see cref="IWorkerGateway.SendTaskAsync"/>.
/// </summary>
public enum WorkerTaskSendOutcome
{
    /// <summary>
    /// THE ASSIGNMENT WAS REFUSED AND NOT PUBLISHED. The recording contract refused it (or no
    /// publisher was configured), so nothing reached the worker's channel; the caller retains the
    /// pinned worker, the active task and the busy state and performs no recovery. This is NOT a
    /// transport failure and NOT a cancellation.
    /// </summary>
    Blocked,

    /// <summary>
    /// THE ASSIGNMENT'S CHANNEL PUBLICATION COMPLETED. This reports publication, NOT confirmed
    /// receipt: the worker may never consume the message.
    /// </summary>
    Published,
}
