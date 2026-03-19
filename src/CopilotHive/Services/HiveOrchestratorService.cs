using Grpc.Core;
using Microsoft.Extensions.Logging;
using CopilotHive.Agents;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Services;

/// <summary>
/// gRPC service implementation for worker registration, bidirectional task streaming, and heartbeats.
/// </summary>
public sealed class HiveOrchestratorService(
    WorkerPool workerPool,
    TaskQueue taskQueue,
    GoalPipelineManager pipelineManager,
    TaskCompletionNotifier completionNotifier,
    GoalDispatcher goalDispatcher,
    ILogger<HiveOrchestratorService> logger,
    AgentsManager? agentsManager = null) : HiveOrchestrator.HiveOrchestratorBase
{


    /// <summary>
    /// Registers a worker with the orchestrator and assigns it an ID.
    /// </summary>
    /// <param name="request">Registration request containing the worker's role and capabilities.</param>
    /// <param name="context">Server call context.</param>
    /// <returns>A <see cref="RegisterResponse"/> indicating whether registration was accepted.</returns>
    public override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        var workerId = string.IsNullOrWhiteSpace(request.WorkerId)
            ? $"worker-{Guid.NewGuid():N}"[..24]
            : request.WorkerId;

        try
        {
            workerPool.RegisterWorker(workerId, [.. request.Capabilities]);
            logger.LogInformation("Worker registered: {WorkerId}", workerId);

            return Task.FromResult(new RegisterResponse
            {
                Accepted = true,
                OrchestratorVersion = Constants.OrchestratorVersion,
                AssignedWorkerId = workerId,
            });
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Registration rejected — duplicate worker ID: {WorkerId}", workerId);
            return Task.FromResult(new RegisterResponse
            {
                Accepted = false,
                OrchestratorVersion = Constants.OrchestratorVersion,
                AssignedWorkerId = workerId,
            });
        }
    }

    /// <summary>
    /// Opens a bidirectional streaming RPC through which the orchestrator sends task assignments
    /// and the worker reports progress and completion.
    /// </summary>
    /// <param name="requestStream">Stream of messages from the worker.</param>
    /// <param name="responseStream">Stream used to send messages to the worker.</param>
    /// <param name="context">Server call context.</param>
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

                    case WorkerMessage.PayloadOneofCase.ToolRequest:
                        _ = HandleToolCallRequestAsync(worker, message.ToolRequest, ct);
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

    /// <summary>
    /// Receives a heartbeat from a worker and updates its last-seen timestamp.
    /// </summary>
    /// <param name="request">Heartbeat request containing the worker's current status.</param>
    /// <param name="context">Server call context.</param>
    /// <returns>An acknowledged <see cref="HeartbeatResponse"/>.</returns>
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
        logger.LogInformation("Worker {WorkerId} is ready", worker.Id);

        // Dequeue a task for this worker
        var task = taskQueue.TryDequeue(worker.Role);
        if (task is not null)
        {
            // Set the worker's role from the task and send agents.md
            var taskRoleName = task.Role.ToString().ToLowerInvariant();
            worker.Role = task.Role;
            logger.LogInformation("Worker {WorkerId} assigned role {Role} for task {TaskId}",
                worker.Id, taskRoleName, task.TaskId);

            if (agentsManager is not null)
                await SendAgentsMdAsync(worker, task.Role, cancellationToken);

            taskQueue.Activate(task, worker.Id);
            workerPool.MarkBusy(worker.Id, task.TaskId);
            logger.LogInformation("Assigning task {TaskId} to worker {WorkerId}", task.TaskId, worker.Id);

            await worker.MessageChannel.Writer.WriteAsync(
                new OrchestratorMessage { Assignment = task },
                cancellationToken);
        }
    }

    private async Task SendAgentsMdAsync(ConnectedWorker worker, GrpcWorkerRole grpcRole, CancellationToken ct)
    {
        var role = grpcRole.ToDomainRole();
        var agentsContent = agentsManager?.GetAgentsMd(role);
        if (string.IsNullOrEmpty(agentsContent)) return;

        var roleName = role.ToRoleName();
        try
        {
            await worker.MessageChannel.Writer.WriteAsync(
                new OrchestratorMessage
                {
                    UpdateAgents = new UpdateAgents
                    {
                        AgentsMdContent = agentsContent,
                        Role = roleName,
                    }
                }, ct);
            logger.LogInformation("Sent AGENTS.md to worker {WorkerId} (role={Role})", worker.Id, roleName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send AGENTS.md to worker {WorkerId}", worker.Id);
        }
    }

    private void HandleTaskProgress(ConnectedWorker worker, TaskProgress progress)
    {
        logger.LogInformation("Task {TaskId} progress from {WorkerId}: {Status} ({Percent:F0}%) — {Message}",
            progress.TaskId, worker.Id, progress.Status, progress.ProgressPercent, progress.Message);
    }

    private async Task HandleToolCallRequestAsync(ConnectedWorker worker, ToolCallRequest request, CancellationToken ct)
    {
        logger.LogInformation("Tool call '{Tool}' from {WorkerId} (task={TaskId})",
            request.ToolName, worker.Id, request.TaskId);

        try
        {
            string resultJson;

            switch (request.ToolName)
            {
                case "ask_orchestrator":
                    var pipeline = pipelineManager.GetByTaskId(request.TaskId);
                    if (pipeline is null)
                    {
                        resultJson = System.Text.Json.JsonSerializer.Serialize(
                            new { answer = "No active pipeline found for this task." });
                        break;
                    }

                    // Parse question from arguments
                    var args = System.Text.Json.JsonDocument.Parse(request.ArgumentsJson);
                    var question = args.RootElement.GetProperty("question").GetString() ?? "";

                    logger.LogInformation("Worker {WorkerId} asks: {Question}", worker.Id, question);

                    // Route to GoalDispatcher's Brain for an answer
                    var answer = await goalDispatcher.AskBrainAsync(pipeline, question, ct);
                    resultJson = System.Text.Json.JsonSerializer.Serialize(new { answer });
                    break;

                case "report_progress":
                    var progressArgs = System.Text.Json.JsonDocument.Parse(request.ArgumentsJson);
                    var status = progressArgs.RootElement.GetProperty("status").GetString() ?? "";
                    var details = progressArgs.RootElement.GetProperty("details").GetString() ?? "";
                    logger.LogInformation("Progress from {WorkerId}: [{Status}] {Details}",
                        worker.Id, status, details);
                    resultJson = System.Text.Json.JsonSerializer.Serialize(new { acknowledged = true });
                    break;

                default:
                    resultJson = System.Text.Json.JsonSerializer.Serialize(
                        new { error = $"Unknown tool: {request.ToolName}" });
                    break;
            }

            await worker.MessageChannel.Writer.WriteAsync(new OrchestratorMessage
            {
                ToolResponse = new ToolCallResponse
                {
                    RequestId = request.RequestId,
                    ResultJson = resultJson,
                    Success = true,
                },
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tool call '{Tool}' failed for {WorkerId}", request.ToolName, worker.Id);
            await worker.MessageChannel.Writer.WriteAsync(new OrchestratorMessage
            {
                ToolResponse = new ToolCallResponse
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Error = ex.Message,
                },
            }, ct);
        }
    }

    private void HandleTaskComplete(ConnectedWorker worker, TaskComplete complete)
    {
        logger.LogInformation("Task {TaskId} completed by {WorkerId}: {Status}",
            complete.TaskId, worker.Id, complete.Status);

        // Capture role before MarkIdle resets it to Unspecified
        var workerRole = worker.Role;

        taskQueue.MarkComplete(complete.TaskId);
        workerPool.MarkIdle(worker.Id);

        // Update pipeline state
        var pipeline = pipelineManager.GetByTaskId(complete.TaskId);
        if (pipeline is not null)
        {
            pipeline.ClearActiveTask();
            if (workerRole != GrpcWorkerRole.Unspecified)
            {
                pipeline.RecordOutput(
                    workerRole.ToDomainRole(),
                    pipeline.Iteration,
                    complete.Output);
            }
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
