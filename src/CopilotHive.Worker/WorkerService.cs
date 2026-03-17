using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Worker;

/// <summary>
/// Core worker lifecycle: register, heartbeat, stream tasks, execute, report.
/// Implements <see cref="IToolCallBridge"/> so custom Copilot tools can communicate
/// with the orchestrator mid-task via the existing bidirectional gRPC stream.
/// </summary>
public sealed class WorkerService(
    string orchestratorUrl,
    string workerId,
    string role,
    string[] capabilities,
    int copilotPort) : IToolCallBridge
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly CopilotRunner _copilotRunner = new(copilotPort);
    private readonly WorkerLogger _log = new("Worker");
    private readonly string _fixedRole = role;

    // Pending tool calls awaiting orchestrator responses, keyed by request_id
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolCallResponse>> _pendingToolCalls = new();

    // The gRPC stream reference, set during WorkStream processing
    private AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>? _stream;
    private string? _assignedId;

    /// <summary>
    /// Runs the full worker lifecycle: connects to Copilot, registers with the orchestrator,
    /// opens a bidirectional gRPC stream, and processes task assignments until cancelled.
    /// </summary>
    /// <param name="ct">Cancellation token that stops the worker.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        var workerRole = ParseRole(_fixedRole);

        // Connect to the local Copilot CLI via SDK before registering with orchestrator
        _log.Info("Connecting to local Copilot CLI...");
        await _copilotRunner.ConnectAsync(ct);

        // Enable HTTP/2 over plaintext (required for gRPC without TLS in Docker network)
        using var channel = GrpcChannel.ForAddress(orchestratorUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
            }
        });
        var client = new HiveOrchestrator.HiveOrchestratorClient(channel);

        // 1. Register
        var registerRequest = new RegisterRequest
        {
            WorkerId = workerId,
            Role = workerRole,
        };
        registerRequest.Capabilities.AddRange(capabilities);

        var registerResponse = await client.RegisterAsync(registerRequest, cancellationToken: ct);

        if (!registerResponse.Accepted)
        {
            _log.Error("Registration rejected by orchestrator.");
            return;
        }

        _assignedId = string.IsNullOrEmpty(registerResponse.AssignedWorkerId)
            ? workerId
            : registerResponse.AssignedWorkerId;

        _log.Info($"Registered as {_assignedId} (orchestrator v{registerResponse.OrchestratorVersion})");

        // 2. Start heartbeat background task
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunHeartbeatAsync(client, _assignedId, workerRole, heartbeatCts.Token);

        try
        {
            // 3. Open bidirectional work stream
            using var stream = client.WorkStream(cancellationToken: ct);
            _stream = stream;

            // 4. Send WorkerReady
            await SendWorkerReady(stream, _assignedId, ct);

            // 5. Main message loop
            await ProcessMessagesAsync(stream, _assignedId, workerRole, ct);
        }
        finally
        {
            _stream = null;
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task ProcessMessagesAsync(
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        WorkerRole workerRole,
        CancellationToken ct)
    {
        CancellationTokenSource? taskCts = null;
        Task? activeTask = null;

        await foreach (var message in ReadMessages(stream.ResponseStream, ct))
        {
            switch (message.PayloadCase)
            {
                case OrchestratorMessage.PayloadOneofCase.Assignment:
                    taskCts?.Dispose();
                    taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var assignment = message.Assignment;
                    _log.Info($"Received task {assignment.TaskId}: {assignment.GoalDescription}");

                    // Reset Copilot session with per-task model (if specified by orchestrator)
                    var taskModel = string.IsNullOrEmpty(assignment.Model) ? null : assignment.Model;
                    _log.Info($"Task model from orchestrator: '{assignment.Model}' → resolved: '{taskModel ?? "(SDK default)"}'");
                    await _copilotRunner.ResetSessionAsync(taskModel, ct);

                    // Run task execution concurrently so message loop can process
                    // ToolCallResponse messages from the orchestrator during execution
                    var localCts = taskCts;
                    activeTask = Task.Run(async () =>
                    {
                        try
                        {
                            var executor = new TaskExecutor(_copilotRunner, this);
                            var result = await executor.ExecuteAsync(assignment, localCts.Token);

                            await stream.RequestStream.WriteAsync(new WorkerMessage
                            {
                                WorkerId = assignedId,
                                Complete = result,
                            }, ct);

                            _log.Info($"Task {assignment.TaskId} completed ({result.Status})");
                            await SendWorkerReady(stream, assignedId, ct);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            _log.Error($"Task execution failed: {ex.Message}");
                        }
                    }, ct);
                    break;

                case OrchestratorMessage.PayloadOneofCase.Cancel:
                    var cancel = message.Cancel;
                    _log.Info($"Cancel requested for task {cancel.TaskId}: {cancel.Reason}");
                    await taskCts?.CancelAsync()!;
                    if (activeTask is not null)
                    {
                        try { await activeTask; } catch (OperationCanceledException) { }
                    }
                    await SendWorkerReady(stream, assignedId, ct);
                    break;

                case OrchestratorMessage.PayloadOneofCase.UpdateAgents:
                    var update = message.UpdateAgents;
                    _log.Info($"Updating custom agent for role: {update.Role}");
                    _copilotRunner.SetCustomAgent(update.Role, update.AgentsMdContent);
                    break;

                case OrchestratorMessage.PayloadOneofCase.ToolResponse:
                    var response = message.ToolResponse;
                    if (_pendingToolCalls.TryRemove(response.RequestId, out var tcs))
                    {
                        tcs.TrySetResult(response);
                    }
                    else
                    {
                        _log.Error($"Received ToolCallResponse for unknown request: {response.RequestId}");
                    }
                    break;

                case OrchestratorMessage.PayloadOneofCase.None:
                    break;
            }
        }

        taskCts?.Dispose();
    }

    #region IToolCallBridge

    /// <inheritdoc/>
    public async Task<string> AskOrchestratorAsync(string taskId, string question, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolCallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingToolCalls[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());

        try
        {
            await SendToolCallRequest(requestId, taskId, "ask_orchestrator",
                System.Text.Json.JsonSerializer.Serialize(new { question }), ct);

            var response = await tcs.Task;
            return response.Success ? response.ResultJson : $"Error: {response.Error}";
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
        }
    }

    /// <inheritdoc/>
    public async Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await SendToolCallRequest(requestId, taskId, "report_progress",
            System.Text.Json.JsonSerializer.Serialize(new { status, details }), ct);
    }

    private async Task SendToolCallRequest(string requestId, string taskId, string toolName, string argsJson, CancellationToken ct)
    {
        if (_stream is null || _assignedId is null)
            throw new InvalidOperationException("Not connected to orchestrator");

        await _stream.RequestStream.WriteAsync(new WorkerMessage
        {
            WorkerId = _assignedId,
            ToolRequest = new ToolCallRequest
            {
                RequestId = requestId,
                TaskId = taskId,
                ToolName = toolName,
                ArgumentsJson = argsJson,
            },
        }, ct);
    }

    #endregion

    private static async Task SendWorkerReady(
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        CancellationToken ct)
    {
        await stream.RequestStream.WriteAsync(new WorkerMessage
        {
            WorkerId = assignedId,
            Ready = new WorkerReady(),
        }, ct);
    }

    private static async Task RunHeartbeatAsync(
        HiveOrchestrator.HiveOrchestratorClient client,
        string assignedId,
        WorkerRole workerRole,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await client.HeartbeatAsync(new HeartbeatRequest
                {
                    WorkerId = assignedId,
                    Role = workerRole,
                    Busy = false,
                }, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Worker] Heartbeat failed: {ex.Message}");
            }
        }
    }

    private static WorkerRole ParseRole(string role) => role.ToLowerInvariant() switch
    {
        "coder" => WorkerRole.Coder,
        "reviewer" => WorkerRole.Reviewer,
        "tester" => WorkerRole.Tester,
        "improver" => WorkerRole.Improver,
        "docwriter" or "doc_writer" => WorkerRole.DocWriter,
        _ => WorkerRole.Unspecified,
    };

    private static async IAsyncEnumerable<T> ReadMessages<T>(
        IAsyncStreamReader<T> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.MoveNext(ct))
        {
            yield return reader.Current;
        }
    }
}
