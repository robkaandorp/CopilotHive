namespace CopilotHive.Services;

/// <summary>
/// Abstracts communication with worker containers. Business logic uses this interface
/// instead of constructing transport-specific messages directly.
/// </summary>
public interface IWorkerGateway
{
    /// <summary>
    /// THE CHECKED CLAIM, forwarded verbatim to the pool primitive
    /// <see cref="WorkerPool.TryClaimAndActivate"/>: the ACTUAL dequeued task is activated in
    /// <paramref name="queue"/> and the SUPPLIED instance is published busy with it — or NOTHING is
    /// mutated at all.
    /// </summary>
    /// <remarks>
    /// <c>false</c> means precisely "this caller did not win" and is a CONFIRMED no-mutation
    /// refusal. A THROW is a different thing entirely: it proves nothing about what was mutated, so
    /// a caller must neither requeue nor claim the activation happened.
    /// </remarks>
    /// <param name="expected">The exact instance the caller selected — the ONLY instance mutated.</param>
    /// <param name="task">The ACTUAL dequeued task this claim is for.</param>
    /// <param name="queue">The concrete queue the task was dequeued from.</param>
    /// <returns><c>true</c> when the claim was taken; <c>false</c> when it was refused.</returns>
    bool TryClaimAndActivate(ConnectedWorker expected, WorkTask task, TaskQueue queue);

    /// <summary>
    /// Sends a task assignment to the EXACT supplied worker instance, reporting whether the
    /// assignment was actually published or refused (blocked) by the assignment-recording contract.
    /// </summary>
    /// <remarks>
    /// THE SUPPLIED INSTANCE IS THE ONLY TARGET: no ID is resolved to redirect the assignment to a
    /// replacement. Outcome semantics are identical to the ID-based overload.
    /// </remarks>
    /// <param name="worker">The PINNED worker instance the assignment belongs to.</param>
    /// <param name="task">The ACTUAL delivered work task.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The publication outcome; see the ID-based overload.</returns>
    Task<WorkerTaskSendOutcome> SendTaskAsync(ConnectedWorker worker, WorkTask task, CancellationToken ct = default);

    /// <summary>
    /// Sends an agents.md update to the EXACT supplied worker instance — its own channel, never a
    /// replacement resolved by ID.
    /// </summary>
    Task SendAgentsUpdateAsync(ConnectedWorker worker, string role, string content, CancellationToken ct = default);

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
/// The outcome of <see cref="IWorkerGateway.SendTaskAsync(ConnectedWorker, WorkTask, CancellationToken)"/>
/// and its ID-based overload.
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
