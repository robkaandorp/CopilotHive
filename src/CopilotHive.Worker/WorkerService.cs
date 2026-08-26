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
    string[] capabilities,
    string configRepoDir = "/config-repo") : IToolCallBridge, ISessionClient, IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly IAgentRunner _agentRunner = new SharpCoderRunner(configRepoDir);
    private readonly string _configRepoDir = configRepoDir;
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
        // Prepare the agent runner. This creates NO LLM client: worker containers hold no LLM
        // credentials of their own, so the client is created lazily on the first prompt, after
        // the orchestrator has provisioned credentials.
        _log.Info("Preparing SharpCoder agent engine...");
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

        // Registration happens BEFORE the operator may have completed OAuth sign-in, so the
        // provisioning fetch is deliberately NOT performed here. It runs immediately before every
        // first LLM client creation, by which time a token committed after sign-in is visible.
        var provisioner = new WorkerConfigProvisioner(
            _assignedId,
            (request, token) => client.GetWorkerConfigAsync(request, cancellationToken: token).ResponseAsync);
        _agentRunner.SetConfigProvisioner(provisioner.EnsureProvisionedAsync);

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

    /// <summary>
    /// A single-use claim guaranteeing exactly ONE <c>WorkerReady</c> per assignment.
    /// <para>
    /// Two producers can finish an assignment: the task body itself (normal completion, failure,
    /// or observing cancellation) and the cancel handler. If both emit Ready, the orchestrator
    /// dequeues two tasks. A second assignment arriving while a first is still draining then
    /// blocks the single response-reading loop on the drain await, which is the very loop the
    /// first task needs in order to receive its <c>ToolResponse</c> — a deterministic deadlock.
    /// Single-flight Ready removes the extra dequeue that creates that interleaving.
    /// </para>
    /// </summary>
    private sealed class ReadyClaim
    {
        private int _claimed;

        /// <summary>Returns <c>true</c> for the FIRST caller only; every later caller gets <c>false</c>.</summary>
        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    }

    /// <summary>Tracks one assignment's identity, in-flight execution, cancellation scope and Ready claim.</summary>
    private sealed class ActiveAssignment(
        string taskId, Task execution, CancellationTokenSource cts, ReadyClaim readyClaim)
    {
        /// <summary>
        /// The assignment's task ID. A <c>CancelTask</c> is only applied when its
        /// <c>TaskId</c> matches this value, so a LATE cancel for an already-finished task can
        /// never abort the assignment that replaced it, nor consume its Ready claim.
        /// </summary>
        public string TaskId { get; } = taskId;

        /// <summary>The running task body.</summary>
        public Task Execution { get; } = execution;

        /// <summary>Cancellation source scoped to this assignment.</summary>
        public CancellationTokenSource Cts { get; } = cts;

        /// <summary>The shared single-flight Ready claim for this assignment.</summary>
        public ReadyClaim Ready { get; } = readyClaim;
    }

    private async Task ProcessMessagesAsync(
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        CancellationToken ct)
    {
        ActiveAssignment? active = null;

        try
        {
            await foreach (var message in ReadMessages(stream.ResponseStream, ct))
            {
                switch (message.PayloadCase)
                {
                    case OrchestratorMessage.PayloadOneofCase.Assignment:
                        // Task-assignment ownership is serialized: only one task may ever own the
                        // mutable runner and its LLM client, so the previous one is drained BEFORE
                        // the runner is reset. Single-flight Ready (above) ensures the orchestrator
                        // never has two assignments in flight against this worker at once, so this
                        // await cannot starve a previous task of its ToolResponse.
                        if (active is not null)
                        {
                            // Await WITHOUT cancelling: single-flight Ready means a new assignment
                            // only follows a Ready this assignment already emitted, so the body is
                            // finished or finishing. Cancelling here would abort work that the
                            // orchestrator still expects to complete.
                            await DrainAssignmentAsync(active, cancelFirst: false);
                            active = null;
                        }

                        var assignment = message.Assignment;
                        var domainTask = GrpcMapper.ToDomain(assignment);
                        _log.Info($"Received task {domainTask.TaskId}: {domainTask.GoalDescription}");

                        // Mark busy before async execution so heartbeats reflect the real state
                        _currentTaskId = domainTask.TaskId;
                        _currentRole = domainTask.Role.ToRoleName();

                        // Reset Copilot session with per-task model (if specified by orchestrator)
                        var taskModel = string.IsNullOrEmpty(domainTask.Model) ? null : domainTask.Model;
                        _log.Info($"Task model from orchestrator: '{domainTask.Model}' → resolved: '{taskModel ?? "(SDK default)"}'");
                        await _agentRunner.ResetSessionAsync(taskModel, domainTask.ReasoningEffort, ct);

                        var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                        // The Ready claim and the CTS are created BEFORE the body starts, so the
                        // body never observes a half-initialised assignment. (Capturing a variable
                        // assigned after Task.Run would race with the body's first statement.)
                        var readyClaim = new ReadyClaim();
                        var bodyCts = taskCts;

                        // Run task execution concurrently so message loop can process
                        // ToolCallResponse messages from the orchestrator during execution
                        var execution = Task.Run(async () =>
                        {
                            try
                            {
                                var executor = new TaskExecutor(_agentRunner, this, sessionClient: this, configRepoDir: _configRepoDir);
                                var result = await executor.ExecuteAsync(domainTask, bodyCts.Token);

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
                                // Sanitized: task execution wraps the LLM HTTP boundary, whose error
                                // payloads can echo provisioned configuration (tokens, API keys).
                                _log.Error($"Task execution failed [{SafeExceptionLog.Describe(ex)}]");
                            }
                            finally
                            {
                                _currentTaskId = null;
                                _currentRole = null;
                            }

                            // Single-flight: only emitted if the cancel handler has not already
                            // claimed Ready for this same assignment.
                            if (readyClaim.TryClaim())
                                await SendWorkerReady(stream, assignedId, ct);
                        }, ct);

                        active = new ActiveAssignment(domainTask.TaskId, execution, taskCts, readyClaim);
                        break;

                    case OrchestratorMessage.PayloadOneofCase.Cancel:
                        var cancel = message.Cancel;
                        _log.Info($"Cancel requested for task {cancel.TaskId}: {cancel.Reason}");

                        if (active is not null)
                        {
                            // Correlate by task ID. A LATE cancel for an already-completed task A
                            // must NOT abort the assignment B that replaced it, and must not
                            // consume B's single-flight Ready claim — doing so would strand B and
                            // desynchronise the orchestrator's view of this worker.
                            if (!string.Equals(active.TaskId, cancel.TaskId, StringComparison.Ordinal))
                            {
                                _log.Info(
                                    $"Ignoring stale cancel for task {cancel.TaskId} — the active task is " +
                                    $"{active.TaskId}, which keeps running.");
                                break;
                            }

                            var cancelled = active;
                            await DrainAssignmentAsync(cancelled, cancelFirst: true);
                            active = null;

                            _currentTaskId = null;
                            _currentRole = null;

                            // Single-flight: the drained body normally claims Ready itself. Only
                            // emit here if it did not (e.g. it was cancelled before reaching the
                            // claim), so a cancel never produces a second dequeue.
                            if (cancelled.Ready.TryClaim())
                                await SendWorkerReady(stream, assignedId, ct);
                        }
                        else
                        {
                            // Nothing in flight — the worker is already idle, so a single Ready
                            // keeps the orchestrator's view accurate.
                            _currentTaskId = null;
                            _currentRole = null;
                            await SendWorkerReady(stream, assignedId, ct);
                        }
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
        }
        finally
        {
            // Stream shutdown must not leave a task running: Program disposes the runner right
            // after this returns, and a still-running turn holds the client lifecycle lease.
            // Cancel then drain so the runner is quiescent before disposal.
            if (active is not null)
            {
                await DrainAssignmentAsync(active, cancelFirst: true);
                active = null;
            }

            _currentTaskId = null;
            _currentRole = null;
        }
    }

    /// <summary>
    /// Waits for an assignment's body to finish, optionally cancelling it first, then disposes its
    /// <see cref="CancellationTokenSource"/>. Never throws for cancellation — the whole point is
    /// to reach a quiescent state.
    /// </summary>
    /// <param name="assignment">The assignment to drain.</param>
    /// <param name="cancelFirst">
    /// <c>true</c> to request cancellation before awaiting (cancel handling and stream teardown);
    /// <c>false</c> to simply await an assignment that is already finishing.
    /// </param>
    private async Task DrainAssignmentAsync(ActiveAssignment assignment, bool cancelFirst)
    {
        if (cancelFirst)
        {
            try
            {
                await assignment.Cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by an earlier drain — nothing to cancel.
            }
        }

        try
        {
            await assignment.Execution;
        }
        catch (OperationCanceledException)
        {
            // Expected: this is how a cancelled body unwinds.
        }
        catch (Exception ex)
        {
            // The body already sanitizes and logs its own failures; this is a last-resort guard so
            // draining never propagates a task fault into the message loop or teardown path.
            _log.Error($"Task drain observed a fault [{SafeExceptionLog.Describe(ex)}]");
        }

        assignment.Cts.Dispose();
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
    public async Task ReportNarrativeAsync(string taskId, string narrative, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await SendToolCallRequest(requestId, taskId, "report_narrative",
            System.Text.Json.JsonSerializer.Serialize(new { narrative }), ct);
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

    /// <inheritdoc/>
    public async Task<string> RaiseIssueAsync(string taskId, string type, string title, string description, string severity, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolCallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingToolCalls[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());

        try
        {
            await SendToolCallRequest(requestId, taskId, "raise_issue",
                System.Text.Json.JsonSerializer.Serialize(new { type, title, description, severity }), ct);

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
                // Sanitized: heartbeats retry across the gRPC boundary, whose status details can
                // echo request configuration back to the worker.
                Console.Error.WriteLine($"[Worker] Heartbeat failed [{SafeExceptionLog.Describe(ex)}]");
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
        //
        // Runner disposal is deliberately fallible and PROPAGATES. GetAwaiter().GetResult()
        // rethrows the original exception rather than wrapping it in an AggregateException the
        // way Wait() does, so the sanitized handler in Program.cs classifies the real fault.
        // Program.cs runs this inside its try, so a throwing disposal is redacted, never dumped
        // raw by the runtime.
        _agentRunner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
