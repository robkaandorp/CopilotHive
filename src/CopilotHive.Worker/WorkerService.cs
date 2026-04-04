using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

namespace CopilotHive.Worker;

/// <summary>
/// Core worker lifecycle: register, heartbeat, stream tasks, execute, report.
/// Implements <see cref="IToolCallBridge"/> so custom tools can communicate
/// with the orchestrator mid-task via the existing bidirectional gRPC stream.
/// </summary>
public sealed class WorkerService(
    string orchestratorUrl,
    string workerId,
    string[] capabilities) : IToolCallBridge, ISessionClient, IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly IAgentRunner _agentRunner = new SharpCoderRunner();
    private readonly WorkerLogger _log = new("Worker");

    // Pending tool calls awaiting orchestrator responses, keyed by request_id
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolCallResponse>> _pendingToolCalls = new();

    // Current task state — read by heartbeat, written by message loop
    private volatile string? _currentTaskId;
    private volatile string? _currentRole;

    // The gRPC stream reference, set during WorkStream processing
    private AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>? _stream;
    private string? _assignedId;

    // The gRPC client, set after successful registration — used by session RPCs
    private HiveOrchestrator.HiveOrchestratorClient? _client;

    /// <summary>
    /// Runs the full worker lifecycle: connects to Copilot, registers with the orchestrator,
    /// opens a bidirectional gRPC stream, and processes task assignments until cancelled.
    /// </summary>
    /// <param name="ct">Cancellation token that stops the worker.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        // Connect to the AI agent engine before registering with orchestrator
        _log.Info("Connecting to SharpCoder agent engine...");
        await _agentRunner.ConnectAsync(ct);

        // Enable HTTP/2 over plaintext (required for gRPC without TLS in Docker network)
        using var channel = GrpcChannel.ForAddress(orchestratorUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
            }
        });
        var client = new HiveOrchestrator.HiveOrchestratorClient(channel);
        _client = client;

        // 1. Register
        var registerRequest = new RegisterRequest
        {
            WorkerId = workerId,
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
        var heartbeatTask = RunHeartbeatAsync(client, _assignedId, heartbeatCts.Token);

        try
        {
            // 3. Open bidirectional work stream
            using var stream = client.WorkStream(cancellationToken: ct);
            _stream = stream;

            // 4. Send WorkerReady
            await SendWorkerReady(stream, _assignedId, ct);

            // 5. Main message loop
            await ProcessMessagesAsync(stream, _assignedId, ct);
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
                    var domainTask = GrpcMapper.ToDomain(assignment);
                    _log.Info($"Received task {domainTask.TaskId}: {domainTask.GoalDescription}");

                    // Mark busy before async execution so heartbeats reflect the real state
                    _currentTaskId = domainTask.TaskId;
                    _currentRole = domainTask.Role.ToRoleName();

                    // Reset Copilot session with per-task model (if specified by orchestrator)
                    var taskModel = string.IsNullOrEmpty(domainTask.Model) ? null : domainTask.Model;
                    _log.Info($"Task model from orchestrator: '{domainTask.Model}' → resolved: '{taskModel ?? "(SDK default)"}'");
                    await _agentRunner.ResetSessionAsync(taskModel, ct);

                    // Run task execution concurrently so message loop can process
                    // ToolCallResponse messages from the orchestrator during execution
                    var localCts = taskCts;
                    activeTask = Task.Run(async () =>
                    {
                        try
                        {
                            var executor = new TaskExecutor(_agentRunner, this, sessionClient: this);
                            var result = await executor.ExecuteAsync(domainTask, localCts.Token);

                            await stream.RequestStream.WriteAsync(new WorkerMessage
                            {
                                WorkerId = assignedId,
                                Complete = GrpcMapper.ToGrpc(result),
                            }, ct);

                            _log.Info($"Task {domainTask.TaskId} completed ({result.Status})");
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            _log.Error($"Task execution failed: {ex.Message}");
                        }
                        finally
                        {
                            _currentTaskId = null;
                            _currentRole = null;
                        }

                        await SendWorkerReady(stream, assignedId, ct);
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
                    _currentTaskId = null;
                    _currentRole = null;
                    await SendWorkerReady(stream, assignedId, ct);
                    break;

                case OrchestratorMessage.PayloadOneofCase.UpdateAgents:
                    var update = message.UpdateAgents;
                    _log.Info($"Updating custom agent for role: {update.Role}");
                    var parsedRole = WorkerRoleExtensions.ParseRole(update.Role)
                        ?? throw new InvalidOperationException($"Unknown role in UpdateAgents: '{update.Role}'");
                    _agentRunner.SetCustomAgent(parsedRole, update.AgentsMdContent);
                    break;

                case OrchestratorMessage.PayloadOneofCase.ToolResponse:
                    var response = message.ToolResponse;
                    if (_pendingToolCalls.TryRemove(response.RequestId, out var tcs))
                    {
                        tcs.TrySetResult(response);
                    }
                    else
                    {
                        // Expected for fire-and-forget tools like report_progress
                        _log.Debug($"Received ToolCallResponse for untracked request: {response.RequestId}");
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
    public async Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolCallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingToolCalls[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());

        try
        {
            await SendToolCallRequest(requestId, taskId, "request_clarification",
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

    /// <inheritdoc/>
    public async Task<string> GetGoalAsync(string taskId, string goalId, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolCallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingToolCalls[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());

        try
        {
            await SendToolCallRequest(requestId, taskId, "get_goal",
                System.Text.Json.JsonSerializer.Serialize(new { goal_id = goalId }), ct);

            var response = await tcs.Task;
            return response.Success ? response.ResultJson : $"Error: {response.Error}";
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
        }
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

    #region Session management

    /// <summary>
    /// Retrieves a persisted session from the orchestrator for the given session ID.
    /// Uses the gRPC channel directly (not the bidirectional stream).
    /// </summary>
    /// <param name="sessionId">The session identifier in format "goalId:roleName".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The session JSON if found, or <c>null</c> if no session exists for the given ID.
    /// </returns>
    public async Task<string?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException("Not connected to orchestrator");

        var response = await _client.GetSessionAsync(
            new GetSessionRequest { SessionId = sessionId },
            cancellationToken: ct);

        return response.Found ? response.SessionJson : null;
    }

    /// <summary>
    /// Persists a session to the orchestrator for the given session ID.
    /// Uses the gRPC channel directly (not the bidirectional stream).
    /// </summary>
    /// <param name="sessionId">The session identifier in format "goalId:roleName".</param>
    /// <param name="sessionJson">The serialised session JSON to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveSessionAsync(string sessionId, string sessionJson, CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException("Not connected to orchestrator");

        await _client.SaveSessionAsync(
            new SaveSessionRequest { SessionId = sessionId, SessionJson = sessionJson },
            cancellationToken: ct);
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

    private async Task RunHeartbeatAsync(
        HiveOrchestrator.HiveOrchestratorClient client,
        string assignedId,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var taskId = _currentTaskId;
                await client.HeartbeatAsync(new HeartbeatRequest
                {
                    WorkerId = assignedId,
                    Busy = taskId is not null,
                    CurrentTaskId = taskId ?? "",
                    CurrentRole = _currentRole ?? "",
                    ContextUsagePercent = taskId is not null
                        ? _agentRunner.GetContextUsagePercent()
                        : 0,
                }, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Worker] Heartbeat failed: {ex.Message}");
            }
        }
    }

    private static async IAsyncEnumerable<T> ReadMessages<T>(
        IAsyncStreamReader<T> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.MoveNext(ct))
        {
            yield return reader.Current;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Dispose the agent runner (which disposes the IChatClient) so each
        // retry gets a fresh connection without leaking the previous one.
        _agentRunner.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
    }
}
