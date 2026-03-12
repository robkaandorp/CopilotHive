using Grpc.Core;
using Microsoft.Extensions.Logging;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

public sealed class HiveOrchestratorService(
    WorkerPool workerPool,
    TaskQueue taskQueue,
    GoalPipelineManager pipelineManager,
    TaskCompletionNotifier completionNotifier,
    ILogger<HiveOrchestratorService> logger) : HiveOrchestrator.HiveOrchestratorBase
{
    private const string OrchestratorVersion = "1.0.0";

    public override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        var workerId = string.IsNullOrWhiteSpace(request.WorkerId)
            ? $"{request.Role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}"[..24]
            : request.WorkerId;

        if (request.Role == WorkerRole.Unspecified)
        {
            logger.LogWarning("Registration rejected — role unspecified (worker: {WorkerId})", workerId);
            return Task.FromResult(new RegisterResponse
            {
                Accepted = false,
                OrchestratorVersion = OrchestratorVersion,
                AssignedWorkerId = workerId,
            });
        }

        try
        {
            workerPool.RegisterWorker(workerId, request.Role, [.. request.Capabilities]);
            logger.LogInformation("Worker registered: {WorkerId} (role={Role})", workerId, request.Role);

            return Task.FromResult(new RegisterResponse
            {
                Accepted = true,
                OrchestratorVersion = OrchestratorVersion,
                AssignedWorkerId = workerId,
            });
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Registration rejected — duplicate worker ID: {WorkerId}", workerId);
            return Task.FromResult(new RegisterResponse
            {
                Accepted = false,
                OrchestratorVersion = OrchestratorVersion,
                AssignedWorkerId = workerId,
            });
        }
    }

    public override async Task WorkStream(
        IAsyncStreamReader<WorkerMessage> requestStream,
        IServerStreamWriter<OrchestratorMessage> responseStream,
        ServerCallContext context)
    {
        string? workerId = null;

        try
        {
            // Use a linked token so we can cancel the channel reader when the stream closes
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var ct = cts.Token;

            // Start a background task to push queued messages to the worker
            ConnectedWorker? workerRef = null;
            var channelTask = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (workerRef is null)
                        {
                            await Task.Delay(100, ct);
                            continue;
                        }

                        var msg = await workerRef.MessageChannel.Reader.ReadAsync(ct);
                        await responseStream.WriteAsync(msg, ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (System.Threading.Channels.ChannelClosedException) { }
            }, ct);

            await foreach (var message in requestStream.ReadAllAsync(ct))
            {
                workerId ??= message.WorkerId;
                var worker = workerPool.GetWorker(message.WorkerId);

                if (worker is null)
                {
                    logger.LogWarning("WorkStream message from unknown worker: {WorkerId}", message.WorkerId);
                    continue;
                }

                workerRef = worker;

                switch (message.PayloadCase)
                {
                    case WorkerMessage.PayloadOneofCase.Ready:
                        await HandleWorkerReady(worker, responseStream, ct);
                        break;

                    case WorkerMessage.PayloadOneofCase.Progress:
                        HandleTaskProgress(worker, message.Progress);
                        break;

                    case WorkerMessage.PayloadOneofCase.Complete:
                        HandleTaskComplete(worker, message.Complete);
                        break;

                    default:
                        logger.LogWarning("Unknown payload type from worker {WorkerId}: {Case}",
                            message.WorkerId, message.PayloadCase);
                        break;
                }
            }

            await cts.CancelAsync();
            try { await channelTask; } catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or server shutting down — expected.
        }
        finally
        {
            if (workerId is not null)
            {
                workerPool.RemoveWorker(workerId);
                logger.LogInformation("Worker disconnected from WorkStream: {WorkerId}", workerId);
            }
        }
    }

    public override Task<HeartbeatResponse> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        workerPool.UpdateHeartbeat(request.WorkerId);
        logger.LogDebug("Heartbeat from {WorkerId} (busy={Busy}, task={TaskId})",
            request.WorkerId, request.Busy, request.CurrentTaskId);

        return Task.FromResult(new HeartbeatResponse { Acknowledged = true });
    }

    private async Task HandleWorkerReady(
        ConnectedWorker worker,
        IServerStreamWriter<OrchestratorMessage> responseStream,
        CancellationToken cancellationToken)
    {
        workerPool.MarkIdle(worker.Id);
        logger.LogInformation("Worker {WorkerId} is ready (role={Role})", worker.Id, worker.Role);

        // Check the task queue for matching work
        var task = taskQueue.TryDequeue(worker.Role);
        if (task is not null)
        {
            taskQueue.Activate(task, worker.Id);
            workerPool.MarkBusy(worker.Id, task.TaskId);
            logger.LogInformation("Assigning task {TaskId} to worker {WorkerId}", task.TaskId, worker.Id);

            // Push through MessageChannel so writes are serialized via the channel reader task
            await worker.MessageChannel.Writer.WriteAsync(
                new OrchestratorMessage { Assignment = task },
                cancellationToken);
        }
    }

    private void HandleTaskProgress(ConnectedWorker worker, TaskProgress progress)
    {
        logger.LogInformation("Task {TaskId} progress from {WorkerId}: {Status} ({Percent:F0}%) — {Message}",
            progress.TaskId, worker.Id, progress.Status, progress.ProgressPercent, progress.Message);
    }

    private void HandleTaskComplete(ConnectedWorker worker, TaskComplete complete)
    {
        logger.LogInformation("Task {TaskId} completed by {WorkerId}: {Status}",
            complete.TaskId, worker.Id, complete.Status);

        taskQueue.MarkComplete(complete.TaskId);
        workerPool.MarkIdle(worker.Id);

        // Update pipeline state
        var pipeline = pipelineManager.GetByTaskId(complete.TaskId);
        if (pipeline is not null)
        {
            pipeline.ClearActiveTask();
            pipeline.RecordOutput(
                worker.Role.ToString().ToLowerInvariant(),
                pipeline.Iteration,
                complete.Output);
        }

        // Notify GoalDispatcher asynchronously so it can ask the Brain what to do next
        _ = Task.Run(async () =>
        {
            try
            {
                await completionNotifier.NotifyAsync(complete);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in task completion handler for {TaskId}", complete.TaskId);
            }
        });
    }

}
