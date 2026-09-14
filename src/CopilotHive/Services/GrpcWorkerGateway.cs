using CopilotHive.Shared.Grpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Services;

/// <summary>
/// gRPC implementation of <see cref="IWorkerGateway"/>. Converts domain types to
/// protobuf messages and writes them to the worker's gRPC message channel.
/// <para>
/// THE TASK DELIVERY IS DELEGATED, NOT WRITTEN HERE. <see cref="SendTaskAsync"/> resolves the
/// worker and then hands the whole publication to the EXISTING
/// <see cref="IWorkerAssignmentPublisher"/> — the single production path that records the
/// delivered assignment's context and only then writes the same
/// <see cref="OrchestratorMessage.Assignment"/> to the pinned worker's channel. There is no raw
/// assignment write on this path and no fallback to one: a caller that cannot resolve a publisher
/// fails closed (see <see cref="WorkerAssignmentRecordingException.MissingPublisher"/>) and reports
/// <see cref="WorkerTaskSendOutcome.Blocked"/>. Cancellation, agents.md updates and cancel
/// requests remain direct channel writes, exactly as before.
/// </para>
/// </summary>
public sealed class GrpcWorkerGateway : IWorkerGateway
{
    private readonly WorkerPool _workerPool;

    /// <summary>
    /// THE MANDATORY PRODUCTION RECORDER, optional in the constructor signature only so the many
    /// existing <c>new GrpcWorkerGateway(new WorkerPool())</c> fixtures keep compiling. It is never
    /// a fall-back-to-raw-write switch: when it is absent <see cref="SendTaskAsync"/> FAILS CLOSED
    /// with <see cref="WorkerTaskSendOutcome.Blocked"/>.
    /// </summary>
    private readonly IWorkerAssignmentPublisher? _publisher;

    private readonly ILogger<GrpcWorkerGateway> _logger;

    /// <summary>
    /// Initialises a new <see cref="GrpcWorkerGateway"/>.
    /// </summary>
    /// <param name="workerPool">The pool the worker id is resolved against.</param>
    /// <param name="publisher">The assignment recorder/publisher that performs the recorded
    /// publication; when <c>null</c> the send fails closed.</param>
    /// <param name="logger">Logger for the guarded blocked-assignment diagnostic; defaults to a
    /// no-op logger.</param>
    public GrpcWorkerGateway(
        WorkerPool workerPool,
        IWorkerAssignmentPublisher? publisher = null,
        ILogger<GrpcWorkerGateway>? logger = null)
    {
        _workerPool = workerPool;
        _publisher = publisher;
        _logger = logger ?? NullLogger<GrpcWorkerGateway>.Instance;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// THE WORKER RESOLUTION IS AN EXCEPTION PATH and happens exactly ONCE, here — a missing worker
    /// is never reported as <see cref="WorkerTaskSendOutcome.Blocked"/>. From there the ONLY
    /// outcome that yields <see cref="WorkerTaskSendOutcome.Blocked"/> is a
    /// <see cref="WorkerAssignmentRecordingException"/>: a missing publisher or any recording
    /// refusal. The caller's own cancellation (<see cref="OperationCanceledException"/>) and every
    /// post-record mapping or channel-write failure propagate UNCHANGED.
    /// </remarks>
    public async Task<WorkerTaskSendOutcome> SendTaskAsync(
        string workerId, WorkTask task, CancellationToken ct = default)
    {
        var worker = _workerPool.GetWorker(workerId)
            ?? throw new InvalidOperationException($"Worker '{workerId}' not found.");

        // THE RECORDED PUBLICATION — the whole send is delegated. The publisher performs the very
        // same channel write this gateway used to perform itself, but only after the assignment's
        // context has been recorded.
        try
        {
            if (_publisher is null)
            {
                // FAIL CLOSED. There is deliberately NO fallback to a raw channel write: an
                // unrecorded assignment must not be delivered.
                throw WorkerAssignmentRecordingException.MissingPublisher();
            }

            await _publisher.PublishAsync(worker, task, ct);

            return WorkerTaskSendOutcome.Published;
        }
        catch (WorkerAssignmentRecordingException ex)
        {
            LogAssignmentBlocked(task, worker.Id, ex);

            // RETURN NORMALLY: the pinned worker, the active task and the busy state are retained by
            // the caller. No recovery, no requeue, no goal failure. The warning above records the
            // exact refusal cause.
            return WorkerTaskSendOutcome.Blocked;
        }
    }

    /// <summary>
    /// THE ONE ACTIONABLE WARNING for a refused eager assignment publication, mirroring the wording
    /// already used for a refused Ready publication.
    /// </summary>
    /// <remarks>
    /// THE WARNING IS GUARDED. The whole diagnostic — the failure-reason formatting, the
    /// <see cref="Exception.Message"/> read and the logger call INCLUDED — sits inside its own
    /// no-throw guard, so a logger (or a message getter) that itself throws cannot escape and turn
    /// the handled <see cref="WorkerTaskSendOutcome.Blocked"/> into an exception. No success wording
    /// appears in this message and no raw secret value is rendered.
    /// </remarks>
    /// <param name="task">The delivered task that was retained.</param>
    /// <param name="workerId">The worker the assignment was pinned to.</param>
    /// <param name="failure">The recording failure; its exact message is included as evidence.</param>
    private void LogAssignmentBlocked(
        WorkTask task, string workerId, WorkerAssignmentRecordingException failure)
    {
        try
        {
            _logger.LogWarning(
                "Worker {WorkerId} task {TaskId}: assignment blocked; no assignment published; " +
                "task retained; cancel the goal or use configured recovery " +
                "(goal={GoalId}, reason={Reason}) — {Detail}",
                workerId,
                task.TaskId,
                task.GoalId,
                failure.Reason,
                MessageOrPlaceholder(failure));
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the handled disposition.
        }
    }

    /// <summary>
    /// Reads <see cref="Exception.Message"/> inside its own no-throw guard: an exception whose
    /// <c>Message</c> getter throws yields a static placeholder, so the guarded warning above
    /// degrades while its never-masked guarantee does not.
    /// </summary>
    /// <param name="exception">The exception whose message is read.</param>
    /// <returns>The exception's message, or a static placeholder when the getter throws.</returns>
    private static string MessageOrPlaceholder(Exception exception)
    {
        try
        {
            return exception.Message;
        }
        catch (Exception messageException)
        {
            return $"<message getter threw: {messageException.GetType().Name}>";
        }
    }

    /// <inheritdoc/>
    public async Task SendCancelAsync(string workerId, string taskId, string reason, CancellationToken ct = default)
    {
        var worker = _workerPool.GetWorker(workerId)
            ?? throw new InvalidOperationException($"Worker '{workerId}' not found.");

        await worker.MessageChannel.Writer.WriteAsync(
            new OrchestratorMessage
            {
                Cancel = new CancelTask { TaskId = taskId, Reason = reason }
            }, ct);
    }

    /// <inheritdoc/>
    public async Task SendAgentsUpdateAsync(string workerId, string role, string content, CancellationToken ct = default)
    {
        var worker = _workerPool.GetWorker(workerId)
            ?? throw new InvalidOperationException($"Worker '{workerId}' not found.");

        await worker.MessageChannel.Writer.WriteAsync(
            new OrchestratorMessage
            {
                UpdateAgents = new UpdateAgents
                {
                    AgentsMdContent = content,
                    Role = role,
                }
            }, ct);
    }

    /// <inheritdoc/>
    public ConnectedWorker? GetIdleWorker() => _workerPool.GetIdleWorker();

    /// <inheritdoc/>
    public IReadOnlyList<ConnectedWorker> GetAllWorkers() => _workerPool.GetAllWorkers();

    /// <inheritdoc/>
    public void MarkBusy(string workerId, string taskId) => _workerPool.MarkBusy(workerId, taskId);
}
