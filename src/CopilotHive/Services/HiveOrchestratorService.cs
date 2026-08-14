using System.Reflection;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using CopilotHive.Agents;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

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
    AgentsManager? agentsManager = null,
    IGoalStore? goalStore = null,
    DashboardNotifier? dashboardNotifier = null,
    IIssueStore? issueStore = null) : HiveOrchestrator.HiveOrchestratorBase
{
    private readonly DashboardNotifier? _dashboardNotifier = dashboardNotifier;
    private readonly IIssueStore? _issueStore = issueStore;

    private readonly Dictionary<string, (DateTime LastNotify, bool WasBusy, int LastNotifiedCtx)> _heartbeatState = new();
    private readonly object _heartbeatLock = new();

    /// <summary>Clock used for heartbeat throttling. Overridable for tests.</summary>
    internal Func<DateTime> _now = () => DateTime.UtcNow;

    /// <summary>Maximum number of tracked heartbeat entries before the oldest is evicted.</summary>
    internal int MaxHeartbeatEntries { get; set; } = 200;


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

            lock (_heartbeatLock)
            {
                _heartbeatState.Remove(workerId);
            }

            _dashboardNotifier?.NotifyStateChanged();

            return Task.FromResult(new RegisterResponse
            {
                Accepted = true,
                OrchestratorVersion = VersionHelper.InformationalVersion,
                AssignedWorkerId = workerId,
            });
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Registration rejected — duplicate worker ID: {WorkerId}", workerId);
            return Task.FromResult(new RegisterResponse
            {
                Accepted = false,
                OrchestratorVersion = VersionHelper.InformationalVersion,
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
                var removed = workerPool.RemoveWorker(workerId);
                lock (_heartbeatLock)
                {
                    _heartbeatState.Remove(workerId);
                }
                logger.LogInformation("Worker disconnected from WorkStream: {WorkerId}", workerId);
                if (removed) _dashboardNotifier?.NotifyStateChanged();
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
        workerPool.UpdateHeartbeat(request.WorkerId, request.ContextUsagePercent);
        logger.LogDebug("Heartbeat from {WorkerId} (busy={Busy}, role={Role}, task={TaskId}, ctx={Ctx}%)",
            request.WorkerId, request.Busy, request.CurrentRole, request.CurrentTaskId, request.ContextUsagePercent);

        if (workerPool.GetWorker(request.WorkerId) is not null)
        {
            var shouldNotify = false;
            lock (_heartbeatLock)
            {
                if (!_heartbeatState.TryGetValue(request.WorkerId, out var entry))
                {
                    if (_heartbeatState.Count >= MaxHeartbeatEntries)
                    {
                        var oldestKey = _heartbeatState
                            .OrderBy(kv => kv.Value.LastNotify)
                            .Select(kv => kv.Key)
                            .FirstOrDefault();
                        if (oldestKey is not null)
                            _heartbeatState.Remove(oldestKey);
                    }

                    _heartbeatState[request.WorkerId] =
                        (_now(), request.Busy, request.ContextUsagePercent);
                    shouldNotify = true;
                }
                else if (request.Busy != entry.WasBusy)
                {
                    _heartbeatState[request.WorkerId] =
                        (_now(), request.Busy, request.ContextUsagePercent);
                    shouldNotify = true;
                }
                else if (Math.Abs(request.ContextUsagePercent - entry.LastNotifiedCtx) >= 5)
                {
                    _heartbeatState[request.WorkerId] =
                        (_now(), request.Busy, request.ContextUsagePercent);
                    shouldNotify = true;
                }
                else if ((_now() - entry.LastNotify).TotalSeconds >= 30)
                {
                    _heartbeatState[request.WorkerId] =
                        (_now(), request.Busy, request.ContextUsagePercent);
                    shouldNotify = true;
                }
            }

            if (shouldNotify)
                _dashboardNotifier?.NotifyStateChanged();
        }

        return Task.FromResult(new HeartbeatResponse { Acknowledged = true });
    }

    /// <summary>
    /// Retrieves a persisted role session for the given session ID.
    /// </summary>
    /// <param name="request">Request containing the session ID in format "goalId:roleName".</param>
    /// <param name="context">Server call context.</param>
    /// <returns>A <see cref="GetSessionResponse"/> with the session JSON and a found flag.</returns>
    public override Task<GetSessionResponse> GetSession(GetSessionRequest request, ServerCallContext context)
    {
        var (goalId, roleName) = ParseSessionId(request.SessionId);
        var sessionJson = pipelineManager.GetRoleSession(goalId, roleName);

        if (sessionJson is not null)
        {
            logger.LogDebug("GetSession hit for session_id={SessionId}", request.SessionId);
            return Task.FromResult(new GetSessionResponse { Found = true, SessionJson = sessionJson });
        }

        logger.LogDebug("GetSession miss for session_id={SessionId}", request.SessionId);
        return Task.FromResult(new GetSessionResponse { Found = false, SessionJson = "" });
    }

    /// <summary>
    /// Persists a role session for the given session ID.
    /// </summary>
    /// <param name="request">Request containing the session ID and serialised session JSON.</param>
    /// <param name="context">Server call context.</param>
    /// <returns>A <see cref="SaveSessionResponse"/> indicating success.</returns>
    public override Task<SaveSessionResponse> SaveSession(SaveSessionRequest request, ServerCallContext context)
    {
        var (goalId, roleName) = ParseSessionId(request.SessionId);
        pipelineManager.SetRoleSession(goalId, roleName, request.SessionJson);
        logger.LogDebug("SaveSession stored for session_id={SessionId}", request.SessionId);
        return Task.FromResult(new SaveSessionResponse { Success = true });
    }

    /// <summary>
    /// Parses a session ID in the format "goalId:roleName" into its components.
    /// </summary>
    /// <param name="sessionId">The session ID to parse.</param>
    /// <returns>A tuple of (goalId, roleName).</returns>
    private static (string goalId, string roleName) ParseSessionId(string sessionId)
    {
        var idx = sessionId.IndexOf(':');
        if (idx < 0)
            throw new ArgumentException($"Invalid session_id format '{sessionId}': expected 'goalId:roleName'.", nameof(sessionId));
        return (sessionId[..idx], sessionId[(idx + 1)..]);
    }

    private static IssueType ParseIssueType(string value) => value.ToLowerInvariant().Trim() switch
    {
        "code_quality" or "codequality" => IssueType.CodeQuality,
        "bug" => IssueType.Bug,
        "suggestion" => IssueType.Suggestion,
        "concern" => IssueType.Concern,
        "workflow" => IssueType.Workflow,
        _ => throw new ArgumentException($"Unknown issue type '{value}'"),
    };
    private static IssueSeverity ParseIssueSeverity(string value) => value.ToLowerInvariant().Trim() switch
    {
        "low" => IssueSeverity.Low,
        "medium" => IssueSeverity.Medium,
        "high" => IssueSeverity.High,
        _ => throw new ArgumentException($"Unknown severity '{value}'"),
    };

    /// <summary>
    /// Applies a task assignment to a worker: activates the task in the queue, marks the worker
    /// busy, and sets <see cref="ConnectedWorker.CurrentModel"/> from the task's requested model.
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <param name="worker">The worker that will execute the task.</param>
    /// <param name="task">The task being assigned.</param>
    internal void ApplyTaskAssignment(ConnectedWorker worker, WorkTask task)
    {
        taskQueue.Activate(task, worker.Id);
        workerPool.MarkBusy(worker.Id, task.TaskId);
        worker.CurrentModel = task.Model;
        _dashboardNotifier?.NotifyStateChanged();
    }

    /// <summary>
    /// Applies task completion to a worker: marks the task complete in the queue, marks the
    /// worker idle, and clears <see cref="ConnectedWorker.CurrentModel"/> to <c>null</c>.
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <param name="worker">The worker that completed the task.</param>
    /// <param name="taskId">The identifier of the completed task.</param>
    internal void ApplyTaskCompletion(ConnectedWorker worker, string taskId)
    {
        taskQueue.MarkComplete(taskId);
        workerPool.MarkIdle(worker.Id);
        worker.CurrentModel = null;
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
            var taskRoleName = task.Role.ToRoleName();
            worker.Role = task.Role;
            logger.LogInformation("Worker {WorkerId} assigned role {Role} for task {TaskId}",
                worker.Id, taskRoleName, task.TaskId);

            if (agentsManager is not null)
                await SendAgentsMdAsync(worker, task.Role, cancellationToken);

            ApplyTaskAssignment(worker, task);
            logger.LogInformation("Assigning task {TaskId} to worker {WorkerId}", task.TaskId, worker.Id);

            await worker.MessageChannel.Writer.WriteAsync(
                new OrchestratorMessage { Assignment = GrpcMapper.ToGrpc(task) },
                cancellationToken);
        }
        else
        {
            _dashboardNotifier?.NotifyStateChanged();
        }
    }

    private async Task SendAgentsMdAsync(ConnectedWorker worker, Workers.WorkerRole role, CancellationToken ct)
    {
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

    /// <summary>
    /// Handles a tool call request from a worker (e.g. report_progress, report_narrative, get_goal).
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal async Task HandleToolCallRequestAsync(ConnectedWorker worker, ToolCallRequest request, CancellationToken ct)
    {
        logger.LogInformation("Tool call '{Tool}' from {WorkerId} (task={TaskId})",
            request.ToolName, worker.Id, request.TaskId);

        try
        {
            string resultJson;

            switch (request.ToolName)
            {
                case "request_clarification":
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
                    var progressPipeline = pipelineManager.GetByTaskId(request.TaskId);
                    progressPipeline?.AddProgressReport(worker.Id, status, details);
                    resultJson = System.Text.Json.JsonSerializer.Serialize(new { acknowledged = true });
                    break;

                case "report_narrative":
                    var narrativeArgs = System.Text.Json.JsonDocument.Parse(request.ArgumentsJson);
                    var narrative = narrativeArgs.RootElement.GetProperty("narrative").GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(narrative))
                    {
                        logger.LogInformation("Narrative from {WorkerId}: {Narrative}", worker.Id, narrative);
                        var narrativePipeline = pipelineManager.GetByTaskId(request.TaskId);
                        narrativePipeline?.AddNarrativeEntry(worker.Id, request.TaskId, narrative);
                    }
                    resultJson = System.Text.Json.JsonSerializer.Serialize(new { acknowledged = true });
                    break;

                case "get_goal":
                    var getGoalArgs = System.Text.Json.JsonDocument.Parse(request.ArgumentsJson);
                    var targetGoalId = getGoalArgs.RootElement.GetProperty("goal_id").GetString() ?? "";
                    var targetGoal = goalStore != null ? await goalStore.GetGoalAsync(targetGoalId, ct) : null;
                    if (targetGoal is null)
                    {
                        resultJson = System.Text.Json.JsonSerializer.Serialize(
                            new { error = $"Goal '{targetGoalId}' not found." });
                        break;
                    }

                    // Look up the pipeline to get the current phase instruction
                    string? currentPhaseInstruction = null;
                    var getGoalPipeline = pipelineManager.GetByTaskId(request.TaskId);
                    if (getGoalPipeline?.Plan is not null)
                    {
                        var currentPhase = getGoalPipeline.Phase;
                        var occurrenceIndex = getGoalPipeline.StateMachine.GetCurrentPhaseOccurrence(getGoalPipeline.Plan.Phases);
                        currentPhaseInstruction = getGoalPipeline.Plan.GetPhaseInstruction(currentPhase, occurrenceIndex);
                    }

                    resultJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id = targetGoal.Id,
                        status = targetGoal.Status.ToString(),
                        description = targetGoal.Description,
                        repositories = targetGoal.RepositoryNames,
                        priority = targetGoal.Priority.ToString(),
                        current_phase_instruction = currentPhaseInstruction,
                    });
                    break;

                case "raise_issue":
                    try
                    {
                        if (_issueStore is null)
                        {
                            resultJson = System.Text.Json.JsonSerializer.Serialize(
                                new { error = "Issue tracking not available." });
                            break;
                        }

                        // Parse arguments (type, title, description, severity with default "low")
                        var issueArgs = System.Text.Json.JsonDocument.Parse(request.ArgumentsJson);
                        var issueType = issueArgs.RootElement.TryGetProperty("type", out var typeEl)
                            ? typeEl.GetString() ?? ""
                            : "";
                        var issueTitle = issueArgs.RootElement.TryGetProperty("title", out var titleEl)
                            ? titleEl.GetString() ?? ""
                            : "";
                        var issueDescription = issueArgs.RootElement.TryGetProperty("description", out var descEl)
                            ? descEl.GetString() ?? ""
                            : "";
                        var issueSeverity = issueArgs.RootElement.TryGetProperty("severity", out var sevEl)
                            ? sevEl.GetString() ?? "low"
                            : "low";

                        // Validate required fields
                        if (string.IsNullOrWhiteSpace(issueType))
                        {
                            resultJson = System.Text.Json.JsonSerializer.Serialize(
                                new { error = "Missing required field: type" });
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(issueTitle))
                        {
                            resultJson = System.Text.Json.JsonSerializer.Serialize(
                                new { error = "Missing required field: title" });
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(issueDescription))
                        {
                            resultJson = System.Text.Json.JsonSerializer.Serialize(
                                new { error = "Missing required field: description" });
                            break;
                        }

                        // Parse type/severity enums
                        IssueType parsedIssueType;
                        try
                        {
                            parsedIssueType = ParseIssueType(issueType);
                        }
                        catch (ArgumentException ex)
                        {
                            resultJson = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
                            break;
                        }

                        IssueSeverity parsedIssueSeverity;
                        try
                        {
                            parsedIssueSeverity = ParseIssueSeverity(issueSeverity);
                        }
                        catch (ArgumentException ex)
                        {
                            resultJson = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
                            break;
                        }

                        // Source context from the pipeline and worker role
                        var issuePipeline = pipelineManager.GetByTaskId(request.TaskId);
                        var issueGoalId = issuePipeline?.GoalId;
                        var issueRole = worker.Role.ToString().ToLowerInvariant();
                        var issueIteration = issuePipeline?.Iteration;

                        // Generate ID (slug-based with collision handling)
                        var issueId = await IssueIdGenerator.GenerateAsync(issueTitle, _issueStore, ct);

                        Issue BuildIssue(string id) => new()
                        {
                            Id = id,
                            Type = parsedIssueType,
                            Title = issueTitle,
                            Description = issueDescription,
                            Severity = parsedIssueSeverity,
                            Status = IssueStatus.Open,
                            RepositoryNames = issuePipeline?.Goal.RepositoryNames ?? [],
                            SourceGoalId = issueGoalId,
                            SourceRole = issueRole,
                            SourceIteration = issueIteration,
                        };

                        var issue = BuildIssue(issueId);

                        try
                        {
                            await _issueStore.CreateIssueAsync(issue, ct);
                        }
                        catch (InvalidOperationException)
                        {
                            // Duplicate ID (race): retry with a GUID-based ID.
                            issueId = $"issue-{Guid.NewGuid():N}";
                            issue = BuildIssue(issueId);
                            await _issueStore.CreateIssueAsync(issue, ct);
                        }

                        _dashboardNotifier?.NotifyStateChanged();
                        resultJson = System.Text.Json.JsonSerializer.Serialize(
                            new { acknowledged = true, issue_id = issueId });
                        break;
                    }
                    catch (Exception)
                    {
                        // Malformed JSON, unexpected persistence errors, or retry failure:
                        // propagate to the outer catch → Success = false.
                        throw;
                    }

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
        var completedTaskModel = taskQueue.GetActiveTask(complete.TaskId)?.Model;
        logger.LogInformation("Task {TaskId} completed by {WorkerId}: {Status} (model={Model})",
            complete.TaskId, worker.Id, complete.Status,
            string.IsNullOrEmpty(completedTaskModel) ? "unknown" : completedTaskModel);

        // Capture role before MarkIdle resets it to Unspecified
        var workerRole = worker.Role;

        ApplyTaskCompletion(worker, complete.TaskId);

        // Update pipeline state
        var pipeline = pipelineManager.GetByTaskId(complete.TaskId);
        if (pipeline is not null)
        {
            pipeline.ClearActiveTask();
            if (workerRole != Workers.WorkerRole.Unspecified && complete.Status != Shared.Grpc.TaskStatus.Failed)
            {
                var outputText = !string.IsNullOrWhiteSpace(complete.Metrics?.Summary)
                    ? complete.Metrics.Summary
                    : complete.Output;
                if (pipeline.CurrentPhaseEntry is { } entry)
                    entry.WorkerOutput = outputText;
            }
        }

        // Convert to domain type at the boundary, injecting the model retrieved above
        var result = GrpcMapper.ToDomain(complete) with { Model = completedTaskModel ?? "" };
        _dashboardNotifier?.NotifyStateChanged();
        _ = Task.Run(async () =>
        {
            try
            {
                await completionNotifier.NotifyAsync(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in task completion handler for {TaskId}", complete.TaskId);
            }
        });
    }

}

/// <summary>
/// Provides the assembly informational version for use at runtime without hardcoded strings.
/// </summary>
internal static class VersionHelper
{
    /// <summary>
    /// Gets the informational version from <see cref="AssemblyInformationalVersionAttribute"/>,
    /// falling back to the assembly version or <c>"unknown"</c> if neither is available.
    /// </summary>
    public static readonly string InformationalVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";
}
