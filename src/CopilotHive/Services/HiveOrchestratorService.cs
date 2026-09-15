using System.Reflection;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using CopilotHive.Agents;
using CopilotHive.Configuration;
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
    IIssueStore? issueStore = null,
    IEventBus? eventBus = null,
    UserService? userService = null,
    ConfigRepoManager? configRepoManager = null,
    IWorkerAssignmentPublisher? assignmentPublisher = null) : HiveOrchestrator.HiveOrchestratorBase
{
    private readonly DashboardNotifier? _dashboardNotifier = dashboardNotifier;
    private readonly IIssueStore? _issueStore = issueStore;
    private readonly IEventBus? _eventBus = eventBus;
    private readonly UserService? _userService = userService;
    private readonly ConfigRepoManager? _configRepoManager = configRepoManager;

    /// <summary>
    /// THE MANDATORY READY-SEND RECORDER. Optional in the constructor signature only so unrelated
    /// fixtures that never exercise a Ready send keep compiling; the production container always
    /// supplies it. It is never a fall-back-to-raw-write switch: when it is absent the Ready send
    /// FAILS CLOSED (see <see cref="LogAssignmentBlocked"/>), because publishing an unrecorded
    /// assignment is exactly what this slice exists to prevent.
    /// </summary>
    private readonly IWorkerAssignmentPublisher? _assignmentPublisher = assignmentPublisher;

    /// <summary>
    /// Reads an orchestrator process environment variable. Overridable for tests so
    /// provisioning can be verified without mutating the real process environment.
    /// </summary>
    internal Func<string, string?> _readEnv = Environment.GetEnvironmentVariable;

    private readonly Dictionary<string, (DateTime LastNotify, bool WasBusy, int LastNotifiedCtx)> _heartbeatState = new();
    private readonly object _heartbeatLock = new();

    /// <summary>Clock used for heartbeat throttling. Overridable for tests.</summary>
    internal Func<DateTime> _now = () => DateTime.UtcNow;

    /// <summary>Maximum number of tracked heartbeat entries before the oldest is evicted.</summary>
    internal int MaxHeartbeatEntries { get; set; } = 200;

    /// <summary>
    /// THE DISTINCT REFUSAL REASONS of the bounded completion/idle-release guard. Each names ONE
    /// guard, so a diagnostic (and a test synchronizing on it) identifies exactly which
    /// observation refused the delivery rather than merely that something did.
    /// </summary>
    internal static class OwnershipRefusalReasons
    {
        /// <summary>The pinned instance is no longer the one registered under its ID (ABA).</summary>
        public const string PinnedInstanceReplaced =
            "the pinned worker is no longer the instance registered under its ID";

        /// <summary>The pool does not hold this worker busy with the completing task.</summary>
        public const string WorkerNotBusyWithTask = "the worker is not busy with that task";

        /// <summary>The queue holds no active entry for the completing task.</summary>
        public const string NoActiveQueueEntry = "the task has no active queue entry";

        /// <summary>The active queue entry names a different assigned worker.</summary>
        public const string ForeignAssignedWorker =
            "the active queue entry is assigned to a different worker";

        /// <summary>The checked release was refused at the mutation point.</summary>
        public const string CheckedReleaseRefused =
            "the checked release was refused — the worker's ownership changed after validation";

        /// <summary>A Ready arrived while the observed task still has an active queue entry.</summary>
        public const string ReadyTaskStillActive =
            "the worker's task is still active in the queue; no completion released it";

        /// <summary>The checked idle was refused at the mutation point.</summary>
        public const string ReadyCheckedIdleRefused =
            "the worker's ownership changed or is inconsistent; the checked idle was refused";
    }


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
        // The exact ConnectedWorker instance this stream is pinned to. All handlers operate on
        // this instance, and removal in the finally block is instance-aware, so a replacement
        // worker that re-registers under the same ID (ABA) is never evicted by this stream.
        ConnectedWorker? pinnedWorker = null;

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
                if (pinnedWorker is null)
                {
                    // First message: pin the exact instance registered for this worker ID.
                    pinnedWorker = workerPool.GetWorker(message.WorkerId);
                    if (pinnedWorker is null)
                    {
                        logger.LogWarning("WorkStream message from unknown worker: {WorkerId}", message.WorkerId);
                        break;
                    }

                    workerRef = pinnedWorker;
                }
                else
                {
                    // Every subsequent message must come from the exact pinned instance. A null or
                    // different/replacement instance under the same ID means the worker re-registered
                    // (ABA): this stream is stale and must end without processing anything further.
                    var current = workerPool.GetWorker(message.WorkerId);
                    if (!ReferenceEquals(current, pinnedWorker))
                    {
                        logger.LogWarning(
                            "WorkStream message from worker {WorkerId} does not match pinned instance — ending stream",
                            message.WorkerId);
                        break;
                    }
                }

                switch (message.PayloadCase)
                {
                    case WorkerMessage.PayloadOneofCase.Ready:
                        await HandleWorkerReady(pinnedWorker, responseStream, ct);
                        break;

                    case WorkerMessage.PayloadOneofCase.Progress:
                        workerPool.TouchActivity(pinnedWorker.Id);
                        HandleTaskProgress(pinnedWorker, message.Progress);
                        break;

                    case WorkerMessage.PayloadOneofCase.Complete:
                        workerPool.TouchActivity(pinnedWorker.Id);
                        HandleTaskComplete(pinnedWorker, message.Complete);
                        break;

                    case WorkerMessage.PayloadOneofCase.ToolRequest:
                        workerPool.TouchActivity(pinnedWorker.Id);
                        _ = HandleToolCallRequestAsync(pinnedWorker, message.ToolRequest, ct);
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
            if (pinnedWorker is not null)
            {
                // Instance-aware removal: only succeeds if this exact instance is still registered.
                // If a replacement registered under the same ID, removal returns false and we must
                // NOT touch the pool or heartbeat state — the replacement owns them now.
                var removed = workerPool.RemoveWorker(pinnedWorker);
                if (removed)
                {
                    lock (_heartbeatLock)
                    {
                        _heartbeatState.Remove(pinnedWorker.Id);
                    }
                    _dashboardNotifier?.NotifyStateChanged();
                }

                logger.LogInformation("Worker disconnected from WorkStream: {WorkerId}", pinnedWorker.Id);
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

    /// <summary>
    /// Provisions a worker's LLM configuration so worker containers need no LLM credentials
    /// of their own.
    /// <para>
    /// The <c>github_token</c> comes from the STORED ADMIN OAuth RECORD via
    /// <see cref="UserService.GetActiveAccessTokenAsync"/> — it is NEVER read from the
    /// orchestrator environment. Every other field (provider settings) comes from the
    /// orchestrator's own process environment.
    /// </para>
    /// <para>
    /// Each field is OMITTED (proto3 optional presence unset) when its source value is null
    /// or whitespace, which tells the worker "nothing provisioned — keep using your own env".
    /// The response is logged by field NAME only; provisioned VALUES are never logged.
    /// </para>
    /// </summary>
    /// <param name="request">Request carrying the requesting worker's ID (used for logging).</param>
    /// <param name="context">Server call context.</param>
    /// <returns>A <see cref="GetWorkerConfigResponse"/> with only the available fields set.</returns>
    public override async Task<GetWorkerConfigResponse> GetWorkerConfig(
        GetWorkerConfigRequest request, ServerCallContext context)
    {
        var response = new GetWorkerConfigResponse();
        var provisioned = new List<string>();

        // The token comes from the stored admin OAuth record — never from the environment.
        var token = _userService is null
            ? null
            : await _userService.GetActiveAccessTokenAsync(context.CancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            response.GithubToken = token;
            provisioned.Add(WorkerConfigFields.GithubToken);
        }

        // Provider settings come from the orchestrator's own environment.
        SetIfPresent("LLM_PROVIDER", WorkerConfigFields.LlmProvider, v => response.LlmProvider = v);
        SetIfPresent("OLLAMA_URL", WorkerConfigFields.OllamaUrl, v => response.OllamaUrl = v);
        SetIfPresent("OLLAMA_API_KEY", WorkerConfigFields.OllamaApiKey, v => response.OllamaApiKey = v);
        SetIfPresent("OLLAMA_MODEL", WorkerConfigFields.OllamaModel, v => response.OllamaModel = v);
        SetIfPresent("GITHUB_MODEL", WorkerConfigFields.GithubModel, v => response.GithubModel = v);

        // The config repo URL is the SANITIZED operator value (ConfigRepoUrlSanitizer) held by
        // the ConfigRepoManager — never a credential-bearing clone URL. It is omitted entirely
        // when no config repo is configured.
        if (_configRepoManager?.ConfigRepoUrl is not null)
        {
            response.ConfigRepoUrl = _configRepoManager.ConfigRepoUrl;
            provisioned.Add(WorkerConfigFields.ConfigRepoUrl);
        }

        logger.LogInformation(
            "GetWorkerConfig for worker_id={WorkerId} provisioned fields: [{Fields}]",
            request.WorkerId,
            provisioned.Count == 0 ? "(none)" : string.Join(", ", provisioned));

        return response;

        void SetIfPresent(string envName, string fieldName, Action<string> assign)
        {
            var value = _readEnv(envName);
            if (string.IsNullOrWhiteSpace(value)) return;
            assign(value);
            provisioned.Add(fieldName);
        }
    }

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
    /// Applies task completion to a worker through the pool's CHECKED release: the captured
    /// instance is only marked idle (and its <see cref="ConnectedWorker.CurrentModel"/> cleared,
    /// INSIDE that release) when it is still the registered instance, still busy, and still
    /// executing <paramref name="taskId"/>. ONLY a successful release removes that exact task id
    /// from the active queue.
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <remarks>
    /// A REFUSAL MUTATES NOTHING: no idle reset, no model write and no queue removal. That is what
    /// keeps a late completion from releasing a SUCCESSOR's assignment — including a successor that
    /// is already busy with a DIFFERENT task id, whose active queue entry must survive untouched.
    /// </remarks>
    /// <param name="worker">The worker that completed the task.</param>
    /// <param name="taskId">The identifier of the completed task.</param>
    /// <returns><c>true</c> when the release was applied; <c>false</c> when it was refused.</returns>
    internal bool ApplyTaskCompletion(ConnectedWorker worker, string taskId)
    {
        if (!workerPool.TryReleaseCompletedTask(worker, taskId))
            return false;

        // ONLY after an accepted release: remove the EXACT task id that was released.
        taskQueue.MarkComplete(taskId);
        return true;
    }

    private async Task HandleWorkerReady(
        ConnectedWorker worker,
        IServerStreamWriter<OrchestratorMessage> responseStream,
        CancellationToken cancellationToken)
    {
        // ── THE SYNCHRONIZED OWNERSHIP OBSERVATION ───────────────────────────────────────────
        // One lock-consistent read of the instance, its busy flag and its current task. Reading
        // those three facts separately could mix a successor's busy flag with a predecessor's task
        // id and release an assignment that is still being worked on.
        if (!workerPool.TryGetWorkerSnapshot(worker.Id, out var observed)
            || !ReferenceEquals(observed.Worker, worker))
        {
            // GONE OR REPLACED (ABA): this stream no longer owns the registered worker, so it must
            // not idle, dequeue or assign anything on its behalf.
            LogReadyIgnored(worker.Id, null, OwnershipRefusalReasons.PinnedInstanceReplaced);
            return;
        }

        // ── THE STILL-OWNED TASK GATE ────────────────────────────────────────────────────────
        // A Ready that arrives while the observed task STILL has an active queue entry is a Ready
        // for work nobody has released. Idling here would silently abandon a held assignment (and
        // hand the worker a second task), so the handler returns BEFORE any idle or dequeue.
        if (observed.CurrentTaskId is not null
            && taskQueue.GetActiveTask(observed.CurrentTaskId) is not null)
        {
            LogReadyIgnored(
                worker.Id, observed.CurrentTaskId, OwnershipRefusalReasons.ReadyTaskStillActive);
            return;
        }

        // ── THE CHECKED IDLE ─────────────────────────────────────────────────────────────────
        // Re-validated against the SAME observation under the pool's activity lock: a changed
        // instance, a changed task id, or an inconsistent busy/task shape is refused and nothing
        // is mutated. CurrentModel is deliberately untouched here — the Ready path's existing
        // model behaviour is preserved.
        if (!workerPool.TryMarkIdleForReady(observed, queueEntryAbsent: true))
        {
            LogReadyIgnored(
                worker.Id, observed.CurrentTaskId, OwnershipRefusalReasons.ReadyCheckedIdleRefused);
            return;
        }

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

            // THE READY-DRIVEN RECORDED PUBLICATION. The dequeue, the agents.md update and the
            // activation/busy-marking above keep their existing order; ONLY the final raw channel
            // write is replaced. The publisher records the delivered assignment's context exactly
            // once and PUBLISHES it only once that record is confirmed, so an unrecorded assignment
            // is never delivered.
            //
            // ONLY the recording failure is handled here; everything else — a real stream/caller
            // cancellation and any post-record send fault — keeps its existing teardown semantics and
            // propagates out of this method unchanged.
            try
            {
                if (_assignmentPublisher is null)
                {
                    // FAIL CLOSED. There is deliberately NO fallback to the old raw write: a missing
                    // recorder means the assignment was not recorded, which is exactly the reason it
                    // must not be delivered either.
                    throw WorkerAssignmentRecordingException.MissingPublisher();
                }

                await _assignmentPublisher.PublishAsync(worker, task, cancellationToken);

                // A COMPLETED CHANNEL WRITE IS NOT PROOF OF RECEIPT — the worker may never consume
                // it — so this line deliberately records intent, not delivery.
                logger.LogInformation(
                    "Assignment published to worker {WorkerId} for task {TaskId}", worker.Id, task.TaskId);
            }
            catch (WorkerAssignmentRecordingException ex)
            {
                LogAssignmentBlocked(worker, task, ex);

                // RETURN NORMALLY: the pinned worker, the active task and the busy state are
                // deliberately retained. This stream is NOT unwound and the worker is NOT removed.
                //
                // WHAT DOES *NOT* RELEASE THIS HOLD: explicit goal cancellation.
                // GoalDispatcher.CancelGoalAsync is LOGICAL cancellation ONLY — it fails the goal
                // and removes the pipeline, but it does NOT stop the worker and does NOT release
                // transport ownership: the worker's busy flag, its CurrentTaskId and the task's
                // active TaskQueue entry all survive it. Only the enabled task-timeout policy (or
                // worker recovery) actually reclaims the hold. No requeue, no fabricated completion,
                // no wrong-goal failure, no new timer/retry/reconciliation happens here.
            }
        }
        else
        {
            _dashboardNotifier?.NotifyStateChanged();
        }
    }

    /// <summary>
    /// THE ONE ACTIONABLE WARNING for a refused Ready assignment recording, carrying the goal
    /// contract's exact disposition wording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE WARNING IS GUARDED. The whole diagnostic — the failure-reason formatting, the
    /// <see cref="Exception.Message"/> read and the logger call INCLUDED — sits inside its own
    /// no-throw guard, so a logger (or a message getter) that itself throws cannot escape and mask
    /// the handled disposition. The recording refusal is the authoritative outcome here; the
    /// diagnostic must never replace it.
    /// </para>
    /// <para>
    /// WHY THERE IS NO SUCCESS LOG ON THIS PATH: the assignment was NOT published. Emitting an
    /// assignment/ success line would misreport the delivery, and no recovery activity was performed
    /// either — the task is left HELD. It is deliberately NOT released by explicit goal
    /// cancellation: <see cref="GoalDispatcher.CancelGoalAsync"/> is LOGICAL cancellation only (it
    /// removes the pipeline but leaves the worker running and leaves the busy flag, the current
    /// task id and the active queue entry in place), which is exactly why this warning tells the
    /// operator that worker recovery may be required.
    /// </para>
    /// </remarks>
    /// <param name="worker">The pinned worker whose assignment was refused.</param>
    /// <param name="task">The delivered task that was retained.</param>
    /// <param name="failure">The recording failure; its exact message is included as evidence.</param>
    private void LogAssignmentBlocked(ConnectedWorker worker, WorkTask task, WorkerAssignmentRecordingException failure)
    {
        try
        {
            // THE EXACT DISPOSITION WORDING, plus the refusal's own reason/category text as
            // actionable detail. No success wording appears anywhere in this message.
            logger.LogWarning(
                "Worker {WorkerId} task {TaskId}: assignment blocked; no assignment published; " +
                "task retained; logical cancellation alone does not release transport ownership; " +
                "worker recovery may be required (reason={Reason}) — {Detail}",
                worker.Id,
                task.TaskId,
                failure.Reason,
                MessageOrPlaceholder(failure));
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the handled disposition.
        }
    }

    /// <summary>
    /// THE GUARDED READY-REFUSAL DIAGNOSTIC: a Ready that was ignored because the worker's
    /// observed ownership was missing, foreign or still held by the queue.
    /// </summary>
    /// <remarks>
    /// GUARDED like every other diagnostic on a refusal path: the whole log call sits inside its
    /// own no-throw guard, so a throwing logger can never turn an ignored Ready into an escaping
    /// exception that would unwind the worker's stream.
    /// </remarks>
    /// <param name="workerId">The worker whose Ready was ignored.</param>
    /// <param name="observedTaskId">The task observed on that worker, or <c>null</c> when idle.</param>
    /// <param name="reason">Why the Ready was ignored.</param>
    private void LogReadyIgnored(string workerId, string? observedTaskId, string reason)
    {
        try
        {
            logger.LogWarning(
                "Worker {WorkerId} ready ignored (task={TaskId}): {Reason}; no task was dequeued or " +
                "assigned and no ownership was released",
                workerId,
                observedTaskId ?? "(none)",
                reason);
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the handled disposition.
        }
    }

    /// <summary>
    /// THE GUARDED COMPLETION-REFUSAL DIAGNOSTIC: a completion that was ignored because the
    /// worker's or the queue's observed ownership did not match the completing task.
    /// </summary>
    /// <param name="workerId">The worker that delivered the completion.</param>
    /// <param name="taskId">The task the completion claimed.</param>
    /// <param name="reason">Why the completion was ignored.</param>
    private void LogCompletionIgnored(string workerId, string taskId, string reason)
    {
        try
        {
            logger.LogWarning(
                "Worker {WorkerId} completion for task {TaskId} ignored: {Reason}; nothing was " +
                "released, removed or notified",
                workerId,
                taskId,
                reason);
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
                            parsedIssueType = IssueIdGenerator.ParseIssueType(issueType);
                        }
                        catch (ArgumentException ex)
                        {
                            resultJson = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
                            break;
                        }

                        IssueSeverity parsedIssueSeverity;
                        try
                        {
                            parsedIssueSeverity = IssueIdGenerator.ParseIssueSeverity(issueSeverity);
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
                        _eventBus?.Publish(new SystemEvent(
                            Type: EventType.IssueRaised,
                            Message: issue.Title,
                            IssueId: issue.Id,
                            GoalId: issueGoalId));
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
        // ══ THE OWNERSHIP VALIDATION, BEFORE ANY CLEANUP OR NOTIFICATION ═════════════════════
        // A completion is only acted on when BOTH authorities still agree that THIS stream's
        // worker owns THIS task:
        //   (1) the pool: a lock-consistent snapshot whose instance is the pinned one, that is
        //       still busy, and whose CurrentTaskId is the completing task; and
        //   (2) the queue: an ACTIVE entry for that exact task id whose assigned_worker is this
        //       worker.
        // Anything else — a missing entry, a foreign owner, a stale/duplicate delivery — returns
        // here, so no successor's assignment is ever released on its behalf.
        if (!workerPool.TryGetWorkerSnapshot(worker.Id, out var observed)
            || !ReferenceEquals(observed.Worker, worker))
        {
            LogCompletionIgnored(
                worker.Id, complete.TaskId, OwnershipRefusalReasons.PinnedInstanceReplaced);
            return;
        }

        if (!observed.IsBusy
            || !string.Equals(observed.CurrentTaskId, complete.TaskId, StringComparison.Ordinal))
        {
            LogCompletionIgnored(
                worker.Id,
                complete.TaskId,
                $"{OwnershipRefusalReasons.WorkerNotBusyWithTask} (busy={observed.IsBusy}, " +
                $"currentTask={observed.CurrentTaskId ?? "(none)"})");
            return;
        }

        var activeTask = taskQueue.GetActiveTask(complete.TaskId);
        if (activeTask is null)
        {
            LogCompletionIgnored(
                worker.Id, complete.TaskId, OwnershipRefusalReasons.NoActiveQueueEntry);
            return;
        }

        if (!activeTask.Metadata.TryGetValue("assigned_worker", out var assignedWorker)
            || !string.Equals(assignedWorker, worker.Id, StringComparison.Ordinal))
        {
            LogCompletionIgnored(
                worker.Id,
                complete.TaskId,
                $"{OwnershipRefusalReasons.ForeignAssignedWorker} " +
                $"(assignedWorker={assignedWorker ?? "(none)"})");
            return;
        }

        // MODEL PROVENANCE SELECTION — performed BEFORE the release removes the active task,
        // because the fallback reads that entry (the VALIDATED one resolved above).
        //
        // Field 7's explicit presence is the ONLY trustworthy signal:
        //   * HasModel == true  → an upgraded sender reported the ORIGINAL ASSIGNED model. That
        //     value wins unconditionally, EVEN when empty/whitespace and EVEN when the queue
        //     disagrees. An explicit wire value is never overwritten by the volatile queue.
        //   * HasModel == false → a legacy sender. Fall back to the queue's active task model.
        // Absence is never inferred from empty/whitespace content.
        var completedTaskModel = complete.HasModel ? complete.Model : activeTask.Model;
        logger.LogInformation("Task {TaskId} completed by {WorkerId}: {Status} (model={Model})",
            complete.TaskId, worker.Id, complete.Status,
            string.IsNullOrEmpty(completedTaskModel) ? "unknown" : completedTaskModel);

        // THE BOUNDARY MAPPING HAPPENS BEFORE THE CLEANUP. A mapping failure (e.g. an unknown
        // wire status) must not leave the worker released and the queue entry removed with no
        // result to notify — it returns locally instead, keeping the held task and this stream.
        TaskResult result;
        try
        {
            // Convert to domain type at the boundary, injecting the SAME selected model used for
            // the log line above — never a second, independently derived value.
            result = GrpcMapper.ToDomain(complete) with { Model = completedTaskModel };
        }
        catch (Exception ex)
        {
            LogCompletionMappingFailed(worker.Id, complete.TaskId, ex);

            // RETURN NORMALLY: the assignment, the active entry and this stream are retained. No
            // fabricated failure result is notified on the worker's behalf.
            return;
        }

        // ══ THE CHECKED RELEASE ══════════════════════════════════════════════════════════════
        // Re-validated at the mutation point: a refusal releases nothing, removes nothing and
        // notifies nothing.
        if (!ApplyTaskCompletion(worker, complete.TaskId))
        {
            LogCompletionIgnored(
                worker.Id, complete.TaskId, OwnershipRefusalReasons.CheckedReleaseRefused);
            return;
        }

        // MUTATION OWNERSHIP: transport does NOT touch the pipeline. The active-task pointer and
        // the phase entry's worker output are owned exclusively by the ADMITTED completion path
        // (TaskCompletionService's guards + admission, and the PipelineDriver it drives). A
        // pre-admission write here could clear a SUCCESSOR's live pointer and overwrite its phase
        // output on behalf of a duplicate completion that admission subsequently rejects.

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

    /// <summary>
    /// THE GUARDED MAPPING-FAILURE DIAGNOSTIC: the completion could not be mapped to its domain
    /// result, so nothing was released and nothing was notified.
    /// </summary>
    /// <param name="workerId">The worker that delivered the completion.</param>
    /// <param name="taskId">The task the completion claimed.</param>
    /// <param name="failure">The mapping failure; its message is included as evidence.</param>
    private void LogCompletionMappingFailed(string workerId, string taskId, Exception failure)
    {
        try
        {
            logger.LogWarning(
                "Worker {WorkerId} completion for task {TaskId} could not be mapped; the task and " +
                "the stream are retained and no completion was notified — {Detail}",
                workerId,
                taskId,
                MessageOrPlaceholder(failure));
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the handled disposition.
        }
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

/// <summary>
/// Canonical field NAMES of the <c>GetWorkerConfigResponse</c> provisioning message.
/// <para>
/// These names — and only these names — are safe to write to a log. The orchestrator logs
/// which fields it provisioned; it must never log the provisioned VALUES, which are secrets
/// (a GitHub OAuth token, an Ollama API key) or operator configuration.
/// </para>
/// </summary>
internal static class WorkerConfigFields
{
    /// <summary>The admin OAuth access token, sourced from the stored user record (never env).</summary>
    public const string GithubToken = "github_token";

    /// <summary>The provider selector, sourced from the orchestrator env <c>LLM_PROVIDER</c>.</summary>
    public const string LlmProvider = "llm_provider";

    /// <summary>The Ollama endpoint, sourced from the orchestrator env <c>OLLAMA_URL</c>.</summary>
    public const string OllamaUrl = "ollama_url";

    /// <summary>The Ollama Cloud API key, sourced from the orchestrator env <c>OLLAMA_API_KEY</c>.</summary>
    public const string OllamaApiKey = "ollama_api_key";

    /// <summary>The Ollama model, sourced from the orchestrator env <c>OLLAMA_MODEL</c>.</summary>
    public const string OllamaModel = "ollama_model";

    /// <summary>The GitHub Models model, sourced from the orchestrator env <c>GITHUB_MODEL</c>.</summary>
    public const string GithubModel = "github_model";

    /// <summary>
    /// The config repository URL, sourced from the orchestrator's <c>ConfigRepoManager</c>
    /// (the sanitized <c>--config-repo</c> operator value — never credential-bearing).
    /// </summary>
    public const string ConfigRepoUrl = "config_repo_url";
}
