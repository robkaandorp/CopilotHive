using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

/// <summary>
/// gRPC implementation of <see cref="IWorkerGateway"/>. Converts domain types to
/// protobuf messages and writes them to the worker's gRPC message channel.
/// </summary>
public sealed class GrpcWorkerGateway(WorkerPool workerPool) : IWorkerGateway
{
    /// <inheritdoc/>
    public async Task SendTaskAsync(string workerId, WorkTask task, CancellationToken ct = default)
    {
        var worker = workerPool.GetWorker(workerId)
            ?? throw new InvalidOperationException($"Worker '{workerId}' not found.");

        var grpcAssignment = GrpcMapper.ToGrpc(task);
        await worker.MessageChannel.Writer.WriteAsync(
            new OrchestratorMessage { Assignment = grpcAssignment }, ct);
    }

    /// <inheritdoc/>
    public async Task SendCancelAsync(string workerId, string taskId, string reason, CancellationToken ct = default)
    {
        var worker = workerPool.GetWorker(workerId)
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
        var worker = workerPool.GetWorker(workerId)
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
    public ConnectedWorker? GetIdleWorker() => workerPool.GetIdleWorker();

    /// <inheritdoc/>
    public IReadOnlyList<ConnectedWorker> GetAllWorkers() => workerPool.GetAllWorkers();

    /// <inheritdoc/>
    public void MarkBusy(string workerId, string taskId) => workerPool.MarkBusy(workerId, taskId);
}
