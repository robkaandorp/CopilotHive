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
    IWorkerAssignmentPublisher? assignmentPublisher = null,
    IWorkerCompletionRecorder? completionRecorder = null) : HiveOrchestrator.HiveOrchestratorBase
{
    private readonly DashboardNotifier? _dashboardNotifier = dashboardNotifier;
    private readonly IIssueStore? _issueStore = issueStore;
    private readonly IEventBus? _eventBus = eventBus;
    private readonly UserService? _userService = userService;
    private readonly ConfigRepoManager? _configRepoManager = configRepoManager;

    /// <summary>
    /// THE MANDATORY COMPLETION-RECEIPT RECORDER. Optional in the constructor signature only so
    /// unrelated fixtures that never deliver a completion keep compiling; the production container
    /// always supplies it.
    /// <para>
    /// IT IS NOT A FALL-BACK-TO-UNRECORDED SWITCH. When it is absent an INCOMING COMPLETION FAILS
    /// CLOSED (see <see cref="LogCompletionNotRecorded"/>): the completion is retained on the worker
    /// and nothing is released, because a completion whose evidence was never retained is exactly what
    /// this slice exists to prevent.
    /// </para>
    /// </summary>
    private readonly IWorkerCompletionRecorder? _completionRecorder = completionRecorder;

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

    /// <summary>
    /// THE WINDOW BETWEEN THE COMPLETE ARM'S ACTIVITY DECISION AND ITS HANDLER CALL, made observable
    /// so a test can mutate ownership INSIDE the real <see cref="WorkStream"/> boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY IT EXISTS. The property under test is that ONE classification drives BOTH the activity
    /// refresh and the handler's routing. That is only provable by changing ownership strictly between
    /// those two statements while the SAME inbound message is in flight — and the read loop is
    /// synchronous there, so no amount of external scheduling can land a mutation in that gap. This
    /// hook is the smallest thing that makes the gap addressable without restructuring the arm.
    /// </para>
    /// <para>
    /// IT IS NOT A BEHAVIOUR. It is <c>null</c> in production and in every fixture that does not
    /// explicitly install one, so the arm is EXACTLY the classification, the conditional refresh and
    /// the handler call — the null-conditional invoke compiles to a branch that is never taken. It
    /// carries no state, returns nothing, decides nothing, and is read once per delivery at the point
    /// it documents. It is emphatically NOT a writer abstraction, a timer, retry machinery or an
    /// outbox, and it can neither change the carried routing value nor any ACK/slot/notification
    /// semantics: everything it could touch is re-validated by the guards that follow it.
    /// </para>
    /// <para>
    /// A HOOK THAT THROWS FAULTS THE STREAM, deliberately. It is test-only, so a fault is a test bug
    /// that must be loud rather than swallowed into the transport's ordinary refusal paths.
    /// </para>
    /// </remarks>
    internal Action<ConnectedWorker, string>? _afterCompletionActivityDecisionForTest;

    /// <summary>
    /// THE COMPLETION PUBLICATION'S OWN WINDOW: the instant AFTER an ordinary completion's checked
    /// release has been applied (and its queue entry removed) and BEFORE the stream-local
    /// acknowledgement eligibility is advanced and the acknowledgement is enqueued — with NO pool
    /// lock held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY IT EXISTS. The property under test is that the just-released worker cannot be NEWLY
    /// SELECTED by an independently running dispatcher while its acknowledgement is still being
    /// published. That interval is otherwise unaddressable from a test: the read loop is
    /// synchronous, so no external scheduling can land inside it. This hook is the smallest thing
    /// that makes the interval reachable — and the ONLY hook that sits at this exact point.
    /// </para>
    /// <para>
    /// IT IS NOT A BEHAVIOUR. It is <c>null</c> in production and in every fixture that does not
    /// install one, so the completion path is EXACTLY the release, the eligibility advance, the
    /// best-effort enqueue and the ordinary notification. It carries no state, returns nothing and
    /// decides nothing; the only thing it can do is OBSERVE and DELAY, because everything it could
    /// touch is re-validated by the guards that follow it.
    /// </para>
    /// <para>
    /// THE PRE-EXISTING CLASSIFICATION HOOK IS TOO EARLY FOR THIS, AND A RESPONSE-WRITER GATE IS TOO
    /// LATE: the first runs before the handler is even entered (nothing has been released yet), and
    /// the second runs after the acknowledgement has already been queued and forwarded. This one is
    /// the only point that is genuinely post-release and pre-enqueue.
    /// </para>
    /// <para>
    /// IT IS INSIDE THE PUBLICATION'S GUARDED SCOPE, DELIBERATELY: a hook that waits and then throws
    /// is a test bug that must be loud, and the enclosing <c>finally</c> still ends the
    /// completion-publication hold before the fault leaves — so a faulty hook can never strand the
    /// instance unselectable.
    /// </para>
    /// </remarks>
    internal Action<ConnectedWorker, string>? _afterCompletionReleaseBeforeAckForTest;

    /// <summary>
    /// THE READY CLAIM'S OWN WINDOW: the instant AFTER the task has been dequeued and the caller's
    /// cancellation has been observed, and BEFORE the pool's checked claim — with NO pool lock held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY IT EXISTS. The property under test is that a Ready whose candidate has gone stale in this
    /// window cannot take ownership: the claim must refuse it and leave the ACTUAL dequeued task
    /// pending exactly once while the competing owner stays untouched. That interval is otherwise
    /// unaddressable from a test, because the stream's read loop is synchronous and nothing external
    /// can schedule into it. This hook is the smallest thing that makes it reachable.
    /// </para>
    /// <para>
    /// IT IS NOT A BEHAVIOUR. It is <c>null</c> in production and in every fixture that does not
    /// install one, so the Ready path is EXACTLY the dequeue, the cancellation check, the claim and
    /// then the guidance/publication — the null-conditional invoke compiles to a branch that is never
    /// taken. It carries no state, returns nothing, decides nothing, performs no await, and runs
    /// OUTSIDE any lock, so it can neither hold the pool's activity lock nor re-enter it. Everything
    /// it could touch is re-validated by the claim that follows it.
    /// </para>
    /// <para>
    /// A HOOK THAT THROWS FAULTS THE STREAM, deliberately: it is test-only, so a fault is a test bug
    /// that must be loud rather than swallowed into the transport's ordinary refusal paths.
    /// </para>
    /// </remarks>
    internal Action? OnBeforeReadyClaimForTest;

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

        /// <summary>
        /// The checked claim was REFUSED for this Ready's dequeued task: the pinned instance is gone
        /// or replaced, is no longer idle with a null task, or is still holding its completion
        /// publication. The task went back to the queue exactly once and nothing was published.
        /// </summary>
        public const string ReadyClaimRefused =
            "the worker's ownership changed before the claim, or its completion publication is " +
            "still in flight; no assignment was published";

        /// <summary>
        /// A SECOND stream tried to attach to an instance an earlier stream already claimed. The
        /// loser returns normally: the winner owns the instance, its state and its channel.
        /// </summary>
        public const string WorkStreamAlreadyAttached =
            "the worker instance is already attached to an existing WorkStream";

        /// <summary>
        /// A completion arrived from a registration that NEGOTIATED completion-receipt
        /// acknowledgements but carried no model presence at all. An enabled registration must report
        /// the model it was assigned, so an absent field is refused locally rather than being
        /// silently answered from the volatile queue.
        /// </summary>
        public const string ModelPresenceRequired =
            "the completion carries no model presence, which this registration's negotiated " +
            "receipt acknowledgement requires";

        /// <summary>
        /// The duplicate's latest eligible task is STILL HELD — it is active in the queue, or the
        /// pinned instance is still executing it — so the recheck refused and nothing was
        /// acknowledged.
        /// </summary>
        public const string LatestEligibleTaskStillHeld =
            "the latest eligible task is still active in the queue or still held by the pinned worker";
    }


    /// <summary>
    /// Registers a worker with the orchestrator and assigns it an ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE COMPLETION-RECEIPT ACK IS NEGOTIATED, CONSERVATIVELY. The worker's request is recorded as
    /// an immutable per-registration fact (see <see cref="ConnectedWorker.RequestCompletionReceiptAck"/>)
    /// and the orchestrator's ANSWER is recorded as a SECOND, separate immutable fact (see
    /// <see cref="ConnectedWorker.CompletionReceiptAckEnabled"/>), decided BEFORE the pool publishes
    /// the instance.
    /// </para>
    /// <para>
    /// ACK IS ENABLED ONLY FOR AN ACCEPTED REGISTRATION THAT EXPLICITLY REQUESTED IT <em>AND</em> WAS
    /// ANSWERED BY AN ORCHESTRATOR WITH A CONFIGURED COMPLETION RECORDER. Every other shape stays
    /// DISABLED: a missing/false request, an absent recorder, and any rejected duplicate reply. Support
    /// is never inferred from the worker's capabilities, model or version, and a rejected duplicate
    /// changes nothing on the instance already registered.
    /// </para>
    /// <para>
    /// THE REPLY IS BUILT FROM THE EXACT RETURNED REGISTRATION INSTANCE, never from a later lookup, so
    /// the answer a worker is told can never disagree with the instance the pool actually published.
    /// The same instance drives <see cref="RegisterResponse.CompletionReadyRequired"/>, which
    /// advertises that this registration's NEXT ordinary assignment waits for an accepted Ready after
    /// a successful negotiated completion release. It advertises that requirement only; it confirms no
    /// acknowledgement, guarantees no delivery and gates no initial registration.
    /// </para>
    /// </remarks>
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
            // THE ENABLEMENT DECISION, taken from the REQUEST and the orchestrator's OWN capability —
            // nothing else. The recorder must be configured here, at registration time, because it is
            // the recorder that retains the evidence an acknowledgement would be about.
            var requested = request.RequestCompletionReceiptAck;
            var ackEnabled = requested && _completionRecorder is not null;

            var registered = workerPool.RegisterWorker(
                workerId, [.. request.Capabilities], requested, ackEnabled);

            logger.LogInformation("Worker registered: {WorkerId}", workerId);

            lock (_heartbeatLock)
            {
                _heartbeatState.Remove(workerId);
            }

            _dashboardNotifier?.NotifyStateChanged();

            // THE REPLY COMES FROM THE REGISTERED INSTANCE ITSELF: the readiness advertisement is
            // derived from the SAME instance's own enablement decision, never from a later lookup and
            // never inferred from capabilities, the model or any version, so the two answers a worker
            // is told can never disagree with the instance the pool actually published.
            return Task.FromResult(new RegisterResponse
            {
                Accepted = true,
                OrchestratorVersion = VersionHelper.InformationalVersion,
                AssignedWorkerId = workerId,
                CompletionReceiptAckEnabled = registered.CompletionReceiptAckEnabled,
                CompletionReadyRequired = registered.CompletionReceiptAckEnabled,
            });
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Registration rejected — duplicate worker ID: {WorkerId}", workerId);

            // A REJECTED DUPLICATE ADVERTISES NOTHING: the instance already registered is untouched,
            // so this reply is DISABLED — and advertises no readiness requirement — regardless of what
            // the duplicate asked for.
            return Task.FromResult(new RegisterResponse
            {
                Accepted = false,
                OrchestratorVersion = VersionHelper.InformationalVersion,
                AssignedWorkerId = workerId,
                CompletionReceiptAckEnabled = false,
                CompletionReadyRequired = false,
            });
        }
    }

    /// <summary>
    /// Opens a bidirectional streaming RPC through which the orchestrator sends task assignments
    /// and the worker reports progress and completion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE STREAM PER REGISTERED INSTANCE. The first known-worker message must claim
    /// <see cref="ConnectedWorker.TryAttachWorkStream"/> BEFORE the pinned worker/pump reference is
    /// published or that message is processed. A stream that LOSES the claim logs a guarded warning,
    /// quiesces ONLY its own unbound channel pump and RETURNS NORMALLY — a clean RPC completion, not
    /// an <see cref="RpcException"/>. The loser consumes no channel message, handles nothing,
    /// removes nothing, clears no heartbeat state and never notifies disconnection for the winner.
    /// </para>
    /// <para>
    /// CLEANUP OWNERSHIP IS EARNED, NOT ASSUMED: the pinned reference is assigned only AFTER a
    /// successful claim, so a losing stream can never enter the teardown that removes the winner's
    /// instance. The winner keeps the existing current-instance/ABA checks and instance-aware
    /// removal.
    /// </para>
    /// </remarks>
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
        // It is assigned ONLY once this stream has successfully CLAIMED the instance, so a losing
        // stream never treats the winner's worker as its own.
        ConnectedWorker? pinnedWorker = null;

        // ── THE ONE LATEST-ELIGIBLE HOLDER OF THIS INVOCATION ────────────────────────────────
        // A plain LOCAL of this WorkStream call, allocated ONCE outside the read loop and owned by
        // nothing else: not a service field, not static state, not an attachment on the worker, not
        // a dictionary keyed by anything, and never reallocated per message. Its lifetime is
        // EXACTLY this invocation's, so a different stream — including a replacement registration's
        // fresh stream under the same worker id — has its own, empty one and can never be answered
        // from this stream's eligibility. It is passed EXPLICITLY into every consumer below.
        var completionAckState = new WorkStreamCompletionAckState();

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
                        try
                        {
                            await responseStream.WriteAsync(msg, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            // REAL CALLER/SERVER CANCELLATION keeps its existing meaning and is
                            // handled by the pump's own outer catch below — never by the
                            // acknowledgement-specific guard.
                            throw;
                        }
                        catch (Exception ex) when (msg.PayloadCase
                            == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck)
                        {
                            // AN ACKNOWLEDGEMENT-SPECIFIC RESPONSE WRITE FAILURE IS ISOLATED: the
                            // failure of an advisory acknowledgement must not tear down the worker's
                            // stream or discard the messages queued behind it. It is reported in a
                            // guarded diagnostic and the pump continues with the next message.
                            //
                            // WHAT THIS DOES NOT CLAIM: a genuinely broken connection is not
                            // recovered by this. The next write will fail too, and the general
                            // stream semantics remain exactly what they were.
                            LogReceiptAckWriteFailed(ex, workerRef.Id, msg.CompletionReceiptAck);
                        }
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
                    var candidate = workerPool.GetWorker(message.WorkerId);
                    if (candidate is null)
                    {
                        logger.LogWarning("WorkStream message from unknown worker: {WorkerId}", message.WorkerId);
                        break;
                    }

                    // THE EXCLUSIVE ATTACHMENT CLAIM, taken BEFORE the pinned reference and the pump
                    // binding below are published and before this message is processed.
                    if (!candidate.TryAttachWorkStream())
                    {
                        // A second stream for the SAME instance. This stream RETURNS NORMALLY: the
                        // winner owns the instance, its state and its channel, so nothing here may be
                        // handled, removed, cleaned or notified. Only this stream's own unbound pump
                        // is quiesced.
                        LogWorkStreamAlreadyAttached(candidate.Id);
                        await QuiesceUnboundChannelPumpAsync(cts, channelTask);
                        return;
                    }

                    // CLEANUP OWNERSHIP IS ESTABLISHED ONLY HERE, after a successful claim.
                    pinnedWorker = candidate;
                    workerRef = candidate;
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
                        // ── ONE CLASSIFICATION, COMPUTED ONCE, USED FOR BOTH DECISIONS ──────────
                        // The activity decision and the handler's routing MUST NOT be able to
                        // disagree, so they are driven by a SINGLE observation taken here and CARRIED
                        // into the handler. Re-observing mutable ownership in the handler would leave
                        // a window in which a re-dispatch between the two observations could make the
                        // loop suppress the activity refresh for a delivery the handler then treats as
                        // ordinary — or, far worse, let an OLD duplicate be processed as the newly
                        // re-dispatched task's completion and release/notify that new assignment.
                        //
                        // A DUPLICATE ATTEMPT ON THE STREAM'S LATEST ELIGIBLE TASK IS NOT ACTIVITY.
                        // It is about an OLD, no-longer-held task, so refreshing the pinned worker's
                        // activity clock for it would extend a SUCCESSOR's inactivity-derived lifetime
                        // on behalf of work that worker is not doing. Every other completion — a
                        // genuinely held or RE-DISPATCHED task included — keeps the existing refresh.
                        var completionRouting = ClassifyCompletionDelivery(
                            pinnedWorker, message.Complete.TaskId, completionAckState);

                        if (!completionRouting.IsLatestEligibleDuplicate)
                            workerPool.TouchActivity(pinnedWorker.Id);

                        // THE WINDOW ITSELF, OBSERVABLE. Null in production (the default), so this is
                        // exactly the path above followed by the handler call below — see the field's
                        // own documentation for why it exists and what it may not do.
                        _afterCompletionActivityDecisionForTest?.Invoke(
                            pinnedWorker, message.Complete.TaskId);

                        HandleClassifiedTaskComplete(
                            pinnedWorker, message.Complete, completionRouting, completionAckState);
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
    /// Applies a task assignment to a worker through the pool's CHECKED CLAIM: the ACTUAL dequeued
    /// task is activated in the queue and the CAPTURED instance is published busy with it — task id,
    /// task start, activity clock, role and <see cref="ConnectedWorker.CurrentModel"/>, all in ONE
    /// activity-lock span — or NOTHING is mutated at all.
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE RETURN VALUE IS THE CLAIM'S OWN OUTCOME and the ONLY thing that authorises the rest of a
    /// Ready delivery. <c>false</c> means the claim was REFUSED — the instance was no longer the one
    /// registered under its ID, was not idle, or was still holding its completion publication — and
    /// nothing was activated, marked busy, published or notified on its behalf.
    /// </para>
    /// <para>
    /// THE NOTIFICATION IS FOR AN ACCEPTED CLAIM ONLY, EXACTLY ONCE, AND OUTSIDE THE POOL LOCK: the
    /// claim owns the activity lock and has already returned by the time this fires, so a dashboard
    /// subscriber can never run while the pool is locked.
    /// </para>
    /// </remarks>
    /// <param name="worker">The exact instance the caller validated — the ONLY instance mutated.</param>
    /// <param name="task">The task dequeued for this assignment.</param>
    /// <returns><c>true</c> when the claim was taken; <c>false</c> when it was refused.</returns>
    internal bool ApplyTaskAssignment(ConnectedWorker worker, WorkTask task)
    {
        if (!workerPool.TryClaimAndActivate(worker, task, taskQueue))
            return false;

        _dashboardNotifier?.NotifyStateChanged();
        return true;
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
    /// <para>
    /// THE EXISTING ROUTE, PRESERVED VERBATIM IN EFFECT: it installs NO completion-publication hold,
    /// so every existing/direct caller keeps the unchanged release behaviour and never has unrelated
    /// selection state written on its behalf.
    /// </para>
    /// <para>
    /// THE NEGOTIATED ORDINARY COMPLETION PATH DOES NOT USE IT: it calls the pool's holding route
    /// directly (see <see cref="HandleClassifiedTaskComplete"/>) because that path must also place the
    /// active-queue removal INSIDE the guarded scope that ends the hold.
    /// </para>
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
            // ── CALLER CANCELLATION, OBSERVED BEFORE THE CLAIM ───────────────────────────────
            // The task is ALREADY OUT of the queue at this point, so a cancelled delivery puts the
            // ACTUAL dequeued instance back EXACTLY ONCE and then propagates the ORIGINAL
            // cancellation unchanged. Nothing is published, recorded, written or sent here, and no
            // role is written — this delivery never acquired ownership.
            if (cancellationToken.IsCancellationRequested)
            {
                // THE ONE INSERT IS ALREADY COMPLETE WHEN A HOOK FAULTS: Enqueue inserts BEFORE it
                // invokes its hook, so a hook fault is post-insert and the task really is back in
                // the queue. There is deliberately NO retry — a second insert would duplicate it.
                try
                {
                    taskQueue.Enqueue(task);
                }
                catch (Exception)
                {
                    // EVERY hook fault is CONTAINED — an OperationCanceledException included. A hook's
                    // exception (and its foreign or default token) must never become this path's
                    // outcome: the caller's cancellation is primary and is thrown below instead.
                }

                // THE CALLER'S CANCELLATION IS THE OUTCOME OF RECORD, carrying the CALLER token.
                throw new OperationCanceledException(cancellationToken);
            }

            // THE WINDOW ITSELF, OBSERVABLE. Null in production (the default), so this is exactly
            // the dequeue cancellation check above followed by the claim below — see the field's own
            // documentation for why it exists and what it may not do.
            OnBeforeReadyClaimForTest?.Invoke();

            // ── THE CHECKED CLAIM: THE ONE AND ONLY PUBLICATION POINT ────────────────────────
            // Busy, task id, task start, activity clock, role, model AND the queue activation are
            // all published inside the pool's single activity-lock span — or none of them are. The
            // pre-claim role write and the unconditional ID-based busy marking are gone: a stale
            // Ready candidate can no longer overwrite newer ownership or bypass a hold.
            if (!workerPool.TryClaimAndActivate(worker, task, taskQueue))
            {
                // REFUSED. The instance is gone or replaced (ABA), is no longer idle with a null
                // task, or is still holding its completion publication — so the ACTUAL dequeued
                // task goes back EXACTLY ONCE and this Ready ends NORMALLY with NO publication, NO
                // assignment recording, NO guidance send, NO admission/mapping/slot mutation and NO
                // role write: a loser never writes Role.
                //
                // THE SINGLE INSERT IS THE WHOLE RECOVERY. TaskQueue.Enqueue inserts BEFORE it
                // invokes its OnEnqueue hook, so there is deliberately NO retry and NO alternate
                // recovery here: an exception from that hook propagates unchanged AFTER the insert.
                // Nothing below may claim the task was not requeued — it was.
                taskQueue.Enqueue(task);
                LogReadyClaimRefused(worker, task);
                return;
            }

            var taskRoleName = task.Role.ToRoleName();

            // THE POST-CLAIM SUCCESS LINE IS GUARDED. The claim above is already published, so a
            // throwing logger (or log provider) must not be able to interrupt the accepted assignment
            // or skip the dashboard notification below.
            LogInformationSafely(
                "Worker {WorkerId} assigned role {Role} for task {TaskId}",
                worker.Id, taskRoleName, task.TaskId);

            // THE ACCEPTED CLAIM IS WHAT THE DASHBOARD IS TOLD ABOUT — EXACTLY ONCE, and OUTSIDE any
            // pool lock (the claim owns _activityLock and has already returned). A REFUSED claim
            // notifies nothing: no assignment was published.
            _dashboardNotifier?.NotifyStateChanged();

            // ── POST-CLAIM GUIDANCE, BEST EFFORT FOR NON-CANCELLATION FAILURES ONLY ──────────
            // The assignment is already claimed, so a guidance fault must never undo it and must
            // never requeue the task. Caller cancellation is the ONE exception: it is propagated
            // unchanged rather than contained as a best-effort failure.
            if (agentsManager is not null)
            {
                try
                {
                    await SendAgentsMdAsync(worker, task.Role, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // THE CALLER'S CANCELLATION, PROPAGATED UNCHANGED — never swallowed as a
                    // best-effort fault, and never a requeue: the claim stands.
                    throw;
                }
                catch (Exception ex)
                {
                    LogGuidanceBestEffortFailed(worker, task, ex);
                }
            }

            // A HELPER THAT SWALLOWED THE CALLER'S CANCELLATION INTERNALLY MUST NOT TURN A
            // CANCELLED DELIVERY INTO A PUBLISHED ASSIGNMENT, so the caller token is observed
            // explicitly before publication. Post-claim cancellation never requeues.
            cancellationToken.ThrowIfCancellationRequested();

            // GUARDED LIKE THE OTHER TWO POST-CLAIM SUCCESS LINES: the caller-token observation above
            // stays the ONLY authority on the cancellation outcome, and a logger-thrown
            // OperationCanceledException (default or foreign token) can never be reinterpreted as
            // this caller's cancellation.
            LogInformationSafely("Assigning task {TaskId} to worker {WorkerId}", task.TaskId, worker.Id);

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
                // it — so this line deliberately records intent, not delivery. The logger call ALONE
                // is guarded: PublishAsync above has already returned, so its failure must not turn
                // the completed publication into a failure (and must not emit this line when
                // publication itself failed).
                LogInformationSafely(
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
    /// THE GUARDED READY-CLAIM-REFUSAL DIAGNOSTIC: a Ready whose dequeued task could not be claimed,
    /// so the task went back to the queue exactly once and no assignment was published.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GUARDED like every other refusal diagnostic: the whole log call sits inside its own no-throw
    /// guard, so a throwing logger can never replace the refusal outcome with an escaping exception.
    /// </para>
    /// <para>
    /// THE WORDING IS DELIBERATELY EXPLICIT ABOUT THE REQUEUE. The task WAS put back — inserting
    /// before its hook — so nothing here may imply the requeue did not happen, and no success or
    /// assignment wording appears either.
    /// </para>
    /// </remarks>
    /// <param name="worker">The pinned worker whose claim was refused.</param>
    /// <param name="task">The dequeued task that went back to the queue.</param>
    private void LogReadyClaimRefused(ConnectedWorker worker, WorkTask task)
    {
        try
        {
            logger.LogWarning(
                "Worker {WorkerId} task {TaskId}: claim refused; no assignment published; the task " +
                "was requeued exactly once — {Reason}",
                worker.Id,
                task.TaskId,
                OwnershipRefusalReasons.ReadyClaimRefused);
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the handled disposition.
        }
    }

    /// <summary>
    /// THE GUARDED BEST-EFFORT GUIDANCE DIAGNOSTIC: a POST-CLAIM agents.md update failed for a
    /// non-cancellation reason.
    /// </summary>
    /// <remarks>
    /// GUARDED like every other diagnostic. The assignment is ALREADY claimed and is retained: this
    /// warning records a degraded, best-effort step and can neither replace the claimed outcome nor
    /// cause a requeue. Caller cancellation never reaches here — it is propagated unchanged.
    /// <para>
    /// THE DETAIL IS SANITIZED AND BOUNDED (see <see cref="SanitizedFailureDetail"/>): the failure's
    /// text is untrusted, so it is rendered through the shared control-character sanitizer rather
    /// than passed — or logged as an exception OBJECT — straight to the logger.
    /// </para>
    /// </remarks>
    /// <param name="worker">The pinned worker whose guidance update failed.</param>
    /// <param name="task">The task it was already assigned.</param>
    /// <param name="failure">The non-cancellation failure; its sanitized message is included.</param>
    private void LogGuidanceBestEffortFailed(ConnectedWorker worker, WorkTask task, Exception failure)
    {
        try
        {
            logger.LogWarning(
                "Worker {WorkerId} task {TaskId}: agents.md update failed after the assignment was " +
                "claimed; continuing to publish the claimed assignment — {Detail}",
                worker.Id,
                task.TaskId,
                SanitizedFailureDetail(failure));
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

    /// <summary>Upper bound on a rendered failure detail, so one exception cannot flood a log.</summary>
    private const int MaxFailureDetailLength = 512;

    /// <summary>
    /// THE SANITIZED, BOUNDED RENDERING of an untrusted failure for a single-line log message:
    /// <see cref="MessageOrPlaceholder"/>'s no-throw read, then
    /// <see cref="LogSanitizer.SanitizeText"/>, then a length cap.
    /// </summary>
    /// <remarks>
    /// EXCEPTION TEXT IS UNTRUSTED INPUT. It can carry newlines, CR, ESC, DEL or C1 characters that
    /// would forge additional log lines, so the rendered detail — never the exception OBJECT, whose
    /// message and stack the logger would render raw — is what reaches the logger.
    /// </remarks>
    /// <param name="failure">The failure to render.</param>
    /// <returns>Single-line, bounded, control-character-free text.</returns>
    private static string SanitizedFailureDetail(Exception failure)
    {
        var detail = LogSanitizer.SanitizeText(MessageOrPlaceholder(failure));
        return detail.Length <= MaxFailureDetailLength
            ? detail
            : detail[..MaxFailureDetailLength] + "…(truncated)";
    }

    /// <summary>
    /// THE GUARDED POST-CLAIM SUCCESS EMISSION: one <see cref="LogLevel.Information"/> line emitted
    /// through a no-throw guard, so a logger (or log provider) that throws — including one that
    /// throws an <see cref="OperationCanceledException"/> carrying the default or a foreign token —
    /// can never interrupt an already-accepted assignment, skip its dashboard notification, skip the
    /// publisher call, or turn a COMPLETED publication into a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT GUARDS THE LOGGING CALL AND NOTHING ELSE. Every caller keeps its operational statements —
    /// the claim, the notification, the guidance step, the caller-token observation, the publisher
    /// null check and <c>PublishAsync</c> — OUTSIDE this guard, so an operational exception is never
    /// swallowed merely because it happens near a log.
    /// </para>
    /// <para>
    /// THE SWALLOW IS DELIBERATE AND TOTAL, <see cref="OperationCanceledException"/> INCLUDED: the
    /// level, wording, argument values and order are unchanged from the unguarded emission, and a
    /// logging fault must never be reinterpreted as caller cancellation — the caller token
    /// observation in the Ready path stays the only authority on that outcome.
    /// </para>
    /// </remarks>
    /// <param name="message">The unchanged message template of the guarded emission.</param>
    /// <param name="args">The unchanged, in-order arguments of the guarded emission.</param>
    private void LogInformationSafely(string message, params object?[] args)
    {
        try
        {
            logger.LogInformation(message, args);
        }
        catch
        {
            // SILENT swallow — a logging fault must never replace the accepted assignment's outcome.
        }
    }

    /// <summary>
    /// THE GUARDED ALREADY-ATTACHED DIAGNOSTIC: a second WorkStream lost the exclusive attachment
    /// claim for an instance another stream already owns, so this stream is ending normally without
    /// touching the instance, its assignments, its channel or the winner's stream.
    /// </summary>
    /// <remarks>
    /// GUARDED like every other refusal diagnostic: the whole log call sits inside its own no-throw
    /// guard, so a throwing logger can never turn a clean rejection into an escaping exception that
    /// would fault the losing RPC.
    /// </remarks>
    /// <param name="workerId">Identifier of the instance that is already attached.</param>
    private void LogWorkStreamAlreadyAttached(string workerId)
    {
        try
        {
            logger.LogWarning(
                "WorkStream for worker {WorkerId} rejected: {Reason}; this stream ends normally and " +
                "nothing was handled, removed or notified",
                workerId,
                OwnershipRefusalReasons.WorkStreamAlreadyAttached);
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the handled disposition.
        }
    }

    /// <summary>
    /// QUIESCES A LOSING STREAM'S OWN UNBOUND CHANNEL PUMP: cancels the stream's own token and joins
    /// exactly its own pump task, which never bound a worker and therefore cannot consume the
    /// winner's channel messages.
    /// </summary>
    /// <remarks>
    /// THIS IS NOT A TEARDOWN REDESIGN. It touches nothing but this stream's own task: no worker
    /// removal, no heartbeat state, no dashboard notification and no cancellation of the winner's
    /// stream. The join is unbounded-safe because cancellation is requested before awaiting, and the
    /// pump's only blocking waits are cancellation-aware channel reads and delays.
    /// </remarks>
    /// <param name="cts">This stream's own linked cancellation source.</param>
    /// <param name="channelTask">This stream's own pump task — the only task joined.</param>
    private static async Task QuiesceUnboundChannelPumpAsync(
        CancellationTokenSource cts, Task channelTask)
    {
        await cts.CancelAsync();
        try
        {
            await channelTask;
        }
        catch (OperationCanceledException)
        {
            // Expected: the pump unblocks by cancellation.
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
            LogAgentsMdSendFailed(worker, ex);
        }
    }

    /// <summary>
    /// THE GUARDED AGENTS.MD SEND-FAILURE DIAGNOSTIC: the guidance send faulted, so a degraded,
    /// best-effort line is emitted and NOTHING escapes this method because of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE WHOLE EMISSION IS GUARDED — the sanitized detail construction AND the logger call. An
    /// unguarded logger fault here would leave <see cref="SendAgentsMdAsync"/> and reach the Ready
    /// path's caller-cancellation filter; a logger-thrown <see cref="OperationCanceledException"/>
    /// (carrying a FOREIGN or default token) would then be rethrown in place of the caller's
    /// cancellation, replacing its instance, token and message. The caller token observation that
    /// follows the guidance step stays the only authority on that outcome.
    /// </para>
    /// <para>
    /// THE EXCEPTION OBJECT IS DELIBERATELY NOT LOGGED: the logger would render its raw message and
    /// stack, so untrusted text could forge log lines. Only the sanitized, bounded detail is emitted.
    /// </para>
    /// </remarks>
    /// <param name="worker">The worker whose guidance send failed.</param>
    /// <param name="failure">The send failure; only its sanitized message is included.</param>
    private void LogAgentsMdSendFailed(ConnectedWorker worker, Exception failure)
    {
        try
        {
            logger.LogWarning(
                "Failed to send AGENTS.md to worker {WorkerId} — {Detail}",
                worker.Id,
                SanitizedFailureDetail(failure));
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the handled disposition.
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

    /// <summary>
    /// Handles one incoming completion, classifying the delivery ONCE and then routing on that single
    /// decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE ENTRY POINT FOR A CALLER THAT DID NOT ALREADY CLASSIFY — it exists so a direct
    /// invocation still goes through exactly one classification. The <c>WorkStream</c> read loop does
    /// NOT use it: the loop must classify BEFORE deciding the activity refresh, so it calls
    /// <see cref="HandleClassifiedTaskComplete"/> with the decision it already made. There is never
    /// more than one classification per delivery on either route.
    /// </para>
    /// <para>
    /// THE ELIGIBILITY HOLDER IS THE CALLER'S, ALWAYS EXPLICIT. There is deliberately no default, no
    /// optional parameter and no allocation here: the holder belongs to ONE <c>WorkStream</c>
    /// invocation, so a caller that is not that invocation must state which holder it owns rather
    /// than being silently handed a fresh or shared one.
    /// </para>
    /// </remarks>
    /// <param name="worker">The pinned instance the completion was delivered on.</param>
    /// <param name="complete">The completion payload.</param>
    /// <param name="ackState">The caller-owned latest-eligible holder this delivery is routed against.</param>
    private void HandleTaskComplete(
        ConnectedWorker worker, TaskComplete complete, WorkStreamCompletionAckState ackState) =>
        HandleClassifiedTaskComplete(
            worker,
            complete,
            ClassifyCompletionDelivery(worker, complete.TaskId, ackState),
            ackState);

    /// <summary>
    /// Handles one incoming completion, routed by the PER-DELIVERY classification the read loop
    /// already computed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE ROUTING DECISION IS CARRIED, NOT RE-DERIVED. <paramref name="routing"/> holds both the
    /// single ownership observation this delivery was classified from and the duplicate/ordinary
    /// answer itself, so the handler's branch selection is IDENTICAL to the one the loop used for its
    /// activity decision. Re-observing here would reopen exactly the divergence this parameter exists
    /// to close.
    /// </para>
    /// <para>
    /// THE CARRIED OBSERVATION IS NOT A SUBSTITUTE FOR THE LATER GUARDS. It is a point-in-time read,
    /// so every mutation this handler performs is still made through a CHECKED operation that
    /// re-validates at the mutation point: <see cref="ApplyTaskCompletion"/> for the release, and the
    /// duplicate branch's own post-read recheck before any acknowledgement.
    /// </para>
    /// </remarks>
    /// <param name="worker">The pinned instance the completion was delivered on.</param>
    /// <param name="complete">The completion payload.</param>
    /// <param name="routing">The per-delivery classification and the observation it was made from.</param>
    /// <param name="ackState">
    /// The caller-owned latest-eligible holder: the SAME one the classification was made against, so
    /// the entry condition, the advance below and the duplicate branch all read one holder.
    /// </param>
    private void HandleClassifiedTaskComplete(
        ConnectedWorker worker,
        TaskComplete complete,
        CompletionDeliveryRouting routing,
        WorkStreamCompletionAckState ackState)
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
        //
        // THE SNAPSHOT IS THE CLASSIFICATION'S OWN, carried in rather than taken again: one
        // observation decides the activity refresh, the branch routing and these gates.
        if (!routing.ObservationValid)
        {
            LogCompletionIgnored(
                worker.Id, complete.TaskId, OwnershipRefusalReasons.PinnedInstanceReplaced);
            return;
        }

        var observed = routing.Observed;

        // ══ THE SAME-STREAM DUPLICATE RE-ACKNOWLEDGEMENT ═════════════════════════════════════
        // A duplicate delivery of the stream's ONE latest eligible completion is answered from the
        // RETAINED evidence instead of being processed again. It is a strictly READ-ONLY branch:
        // no Record, no release, no queue removal, no pipeline mutation, no dashboard success
        // notification, no completion notification — and it NEVER falls through into the ordinary
        // completion path below.
        //
        // ONLY THE STILL-CURRENT CLAIMED PINNED INSTANCE OF AN ENABLED NEGOTIATION MAY ENTER: the
        // classification was made against the same pinned-instance check above, and a disabled
        // registration (an existing/legacy worker) is refused without even reading the holder. The
        // holder belongs to ONE WorkStream invocation, so a fresh stream — including the one a
        // replacement registration under the same id opens — starts with an EMPTY holder and can
        // never be authorized by the earlier stream's evidence.
        //
        // THE TASK MUST BE ORDINAL-EXACT AGAINST THE SLOT, and MUST NO LONGER BE HELD — see
        // CompletionDeliveryRouting. Every other name, and every genuinely re-dispatched task, takes
        // the ORDINARY path below.
        if (routing.IsLatestEligibleDuplicate)
        {
            HandleLatestEligibleDuplicate(worker, complete);
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
        //
        // ── THE ENABLED-REGISTRATION MODEL REQUIREMENT ───────────────────────────────────────
        // A registration that NEGOTIATED completion-receipt acknowledgements must report the model
        // it was assigned, because the durable evidence an acknowledgement is about carries that
        // model. For such a registration an ABSENT field is a guarded LOCAL refusal, taken BEFORE
        // the receipt is recorded and therefore before any acknowledgement could become eligible:
        // nothing is released, removed, notified or acknowledged. A PRESENT-EMPTY or whitespace value
        // is still perfectly valid — presence, not content, is what is required.
        //
        // A DISABLED registration keeps the original legacy behaviour exactly: the absent field
        // falls back to the validated active task's model.
        if (worker.CompletionReceiptAckEnabled && !complete.HasModel)
        {
            LogCompletionIgnored(
                worker.Id, complete.TaskId, OwnershipRefusalReasons.ModelPresenceRequired);
            return;
        }

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

        // ══ THE COMPLETION-RECEIPT RECORDING ═════════════════════════════════════════════════
        // The receipt is retained AFTER the ownership validation and the boundary mapping/model
        // selection above, and BEFORE the checked release below, so a release can never happen for a
        // completion whose durable evidence was not confirmed.
        //
        // WHAT IS RECORDED IS EVIDENCE, NOT PROGRESS. A confirmed retention is NOT a phase
        // advancement, NOT an acknowledgement and NOT a replay permission: the admitted domain path
        // (TaskCompletionService's guards and the PipelineDriver it drives) owns every pipeline
        // mutation, exactly as before.
        //
        // NO PIPELINE PRECONDITION: the recorder never consults a pipeline. Logical cancellation may
        // remove the pipeline while valid transport ownership remains, so requiring one here would
        // discard the evidence this slice exists to retain.
        //
        // ONLY A NORMAL RETURN FROM THE RECORDER PERMITS THE RELEASE. Every other outcome — a
        // missing recorder (fail CLOSED, never the old unrecorded path), a missing or mismatched
        // stored context, a Conflict, an unconfirmed write, a codec/read/query failure, or any
        // unexpected throw — returns LOCALLY from this handler: no release, no queue removal, no
        // dashboard success notification and no completion notification. This stream is NOT unwound.
        //
        // A RECEIPT STORED BY A PREVIOUS INVOCATION IS NEVER DELETED, COMPENSATED OR REBOUND here,
        // and nothing is retried or read back after a write uncertainty.
        try
        {
            if (_completionRecorder is null)
            {
                // FAIL CLOSED. There is deliberately NO fallback to the old unrecorded completion
                // path: a completion whose evidence was never retained is exactly what must not be
                // released either.
                throw WorkerCompletionRecordingException.MissingRecorder();
            }

            // The ACTIVE task validated above and the ALREADY-MAPPED result are handed over as-is:
            // the recorder derives the receipt's identity from the STORED assignment context and the
            // result's own selected model, never from a second, independently derived value.
            _completionRecorder.Record(worker.Id, activeTask, result);
        }
        catch (WorkerCompletionRecordingException refusal)
        {
            LogCompletionNotRecorded(worker.Id, complete.TaskId, refusal.Reason.ToString(), refusal);

            // RETURN NORMALLY: the pinned worker, the active task, the busy state and this stream are
            // deliberately retained. The worker keeps owning the completion until the existing
            // timeout policy or worker recovery reclaims it — logical cancellation does not.
            return;
        }
        catch (Exception unexpected)
        {
            // FAIL CLOSED FOR ANY OTHER THROW TOO: only a normal return is a confirmed retention, so
            // an unexpected failure is a refusal rather than a reason to release without evidence.
            LogCompletionNotRecorded(worker.Id, complete.TaskId, unexpected.GetType().Name, unexpected);
            return;
        }

        // ══ THE CHECKED RELEASE, WITH THE COMPLETION-PUBLICATION HOLD ═════════════════════════
        // Re-validated at the mutation point: a refusal releases nothing, removes nothing and
        // notifies nothing, and — crucially — INSTALLS NO HOLD.
        //
        // THE HOLD IS THE NEGOTIATED PATH'S ALONE. It is requested ONLY when this registration
        // negotiated completion-receipt acknowledgements, because only such a registration has an
        // acknowledgement to publish after the release. A legacy/disabled registration takes the
        // EXISTING route (idle reset, model clear, queue removal) with NO selection state written on
        // its behalf, so its runtime is exactly what it was.
        //
        // THE RELEASE INSTALLS THE HOLD INSIDE THE SAME POOL LOCK SPAN THAT APPLIES THE IDLE RESET,
        // so the released worker is NEVER observable as idle-and-selectable in the interval before
        // its acknowledgement is queued — a dispatcher that NEWLY SELECTS workers (the eager push
        // path) can therefore no longer pick it up in that gap.
        //
        // THE HOLD IS A NARROW SELECTION HOLD: not a task reservation, not an acknowledgement
        // delivery confirmation, and no permission to make the worker wait for anything.
        var holdForCompletionPublication = worker.CompletionReceiptAckEnabled;

        var completionPublicationHeld = holdForCompletionPublication
            ? workerPool.TryReleaseCompletedTaskHoldingForPublication(worker, complete.TaskId)
            : ApplyTaskCompletion(worker, complete.TaskId);

        if (!completionPublicationHeld)
        {
            LogCompletionIgnored(
                worker.Id, complete.TaskId, OwnershipRefusalReasons.CheckedReleaseRefused);
            return;
        }

        // ── THE PUBLICATION'S OWN GUARDED SCOPE ──────────────────────────────────────────────
        // EVERYTHING after the acquisition is inside ONE try/finally, so the hold is ended on
        // SUCCESS, on a refusal and on an EXCEPTION alike: the active-queue removal, the test hook,
        // the eligibility advance and the enqueue can each fault without leaving the instance
        // stranded unselectable. A REFUSED release never reaches this block at all, so it can never
        // clear a hold it did not acquire.
        //
        // WHAT IS *NOT* DONE HERE: no database work, no response WriteAsync, no notification, no
        // arbitrary callback and no logger call happens while the pool's activity lock is held. The
        // lock is held only inside the pool operations themselves; this whole block runs with no pool
        // lock held.
        //
        // MUTATION OWNERSHIP: transport still does NOT touch the pipeline. The active-task pointer
        // and the phase entry's worker output remain owned exclusively by the ADMITTED completion
        // path (TaskCompletionService's guards + admission, and the PipelineDriver it drives). A
        // pre-admission write here could clear a SUCCESSOR's live pointer and overwrite its phase
        // output on behalf of a duplicate completion that admission subsequently rejects.
        try
        {
            // THE EXACT ACTIVE-QUEUE REMOVAL, inside the guarded scope on the holding route. It runs
            // OUTSIDE the pool's lock and after the hold is already installed, so even a fault here
            // ends the hold through the finally below. On the NON-holding route this removal already
            // happened inside the preserved two-argument operation, so it is deliberately not
            // repeated.
            if (holdForCompletionPublication)
                taskQueue.MarkComplete(complete.TaskId);

            // THE POST-RELEASE, PRE-ACKNOWLEDGEMENT WINDOW ITSELF, OBSERVABLE. Null in production
            // (the default), so this is exactly the release and removal above followed by the
            // eligibility advance and the best-effort enqueue below — see the field's own
            // documentation for why it exists, where exactly it sits, and what it may not do. It runs
            // OUTSIDE the pool's lock: the release has already been applied and the hold is already
            // installed, so the instance is already unselectable while this runs.
            _afterCompletionReleaseBeforeAckForTest?.Invoke(worker, complete.TaskId);

            // ══ THE ACKNOWLEDGEMENT ELIGIBILITY ══════════════════════════════════════════════
            // THE CONSERVATIVE EMISSION BOUNDARY: only a confirmed Record FOLLOWED BY an APPLIED
            // checked release reaches here, so only such a completion can create eligibility. A
            // mapping failure, a recording refusal and a refused checked release all returned
            // above, so none of them creates eligibility and none of them emits an acknowledgement.
            //
            // THE CALLER'S HOLDER IS ADVANCED BEFORE THE ENQUEUE, deliberately: the eligibility
            // fact must exist even if the best-effort publication below fails, because it is state
            // about what happened, not about what could be delivered. It is the SAME holder the
            // classification was made against — never a fresh or shared one.
            //
            // A DISABLED registration stores nothing and acknowledges nothing, which is what keeps
            // an existing (legacy) worker's runtime completely unchanged.
            if (worker.CompletionReceiptAckEnabled)
            {
                ackState.AdvanceLatestEligible(complete.TaskId);

                // THE BEST-EFFORT ACKNOWLEDGEMENT ENQUEUE. Its failure is ISOLATED: it must never
                // suppress the ordinary dashboard/downstream notification below, must never undo the
                // release that already happened, and must never cause another Record or notification.
                //
                // THE ENQUEUE ATTEMPT — NOT ITS DELIVERY — IS WHAT THE HOLD COVERS. Whatever this
                // returns or throws, the hold ends below: a refused enqueue, a closed channel, a
                // throwing diagnostic and a response writer that fails later in the pump all end the
                // hold no later than the attempt, so the worker is never stranded unselectable on
                // behalf of an acknowledgement nobody may ever receive.
                TryPublishCompletionReceiptAck(worker, complete.TaskId);
            }
        }
        finally
        {
            // THE HOLD IS ENDED FOR THE EXACT STILL-REGISTERED INSTANCE, and ONLY when THIS
            // invocation acquired one: a release refusal or a legacy/disabled registration never
            // clears anything. Only the hold flag is cleared — no role, model, task id or activity
            // state is touched, and an ABA replacement is left untouched. It is a single
            // lock-guarded flag write — no timeout, no retry loop, no lease expiry — and it
            // deliberately does NOT wait for the acknowledgement to be written to the network or
            // acknowledged by the worker.
            //
            // IT ENDS THE SHORT PUBLICATION HOLD ONLY. The readiness wait the same release installed
            // is a SEPARATE, LONGER fact and survives this finally: an enqueue refusal, an isolated
            // response-write failure and a throwing diagnostic leave it installed, and only a live
            // accepted Ready clears it.
            if (holdForCompletionPublication)
                workerPool.ClearCompletionPublicationHold(worker);
        }

        // THE ORDINARY DASHBOARD/DOWNSTREAM NOTIFICATION, AFTER the hold is finished: the
        // notification path never runs while the instance is still withheld from selection.
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
    /// THE GUARDED COMPLETION-RECORDING DIAGNOSTIC: a completion whose receipt was NOT confirmed, so
    /// the completion was retained on the worker and nothing was released, removed or notified.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE WARNING IS GUARDED. The whole diagnostic — the reason text, the
    /// <see cref="Exception.Message"/> read and the logger call INCLUDED — sits inside its own
    /// no-throw guard, so a logger (or a message getter) that itself throws cannot escape and unwind
    /// the worker's stream. The recording refusal is the authoritative outcome here; the diagnostic
    /// must never replace it.
    /// </para>
    /// <para>
    /// THE DISPOSITION WORDING IS CANONICAL and is emitted, with the task and the worker, for every
    /// refusal family: a missing recorder, a missing or mismatched stored context, a Conflict, an
    /// unconfirmed write and any store read/codec/query failure. It tells the operator the
    /// truth this slice is about — logical cancellation alone does NOT release the transport hold.
    /// </para>
    /// </remarks>
    /// <param name="workerId">The pinned worker whose completion was not recorded.</param>
    /// <param name="taskId">The completing task's identifier.</param>
    /// <param name="reason">The refusal family (or the unexpected exception's type name).</param>
    /// <param name="failure">The refusal; its exact message is included as evidence.</param>
    private void LogCompletionNotRecorded(string workerId, string taskId, string reason, Exception failure)
    {
        try
        {
            logger.LogWarning(
                "Worker {WorkerId} task {TaskId}: completion retained on worker; receipt not confirmed; " +
                "logical cancellation alone does not release transport ownership (reason={Reason}) — " +
                "{Detail}",
                workerId,
                taskId,
                reason,
                MessageOrPlaceholder(failure));
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the handled disposition.
        }
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

    /// <summary>
    /// THE ONE ENTRY CLASSIFICATION of a single completion delivery: whether it is a duplicate of the
    /// stream's latest eligible task, decided ONCE from ONE lock-consistent ownership observation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT IS COMPUTED EXACTLY ONCE PER DELIVERY, in the read loop, and then CARRIED to the completion
    /// handler. That is the whole point of the type: the activity decision and
    /// the routing decision consume the SAME value, so they cannot diverge. Re-deriving the answer in
    /// the handler would reopen a window in which a re-dispatch landing between the two observations
    /// makes the loop suppress the activity refresh for a delivery the handler then processes as an
    /// ordinary completion — releasing and notifying the NEW assignment on the strength of an OLD
    /// duplicate.
    /// </para>
    /// <para>
    /// ALL FOUR FACTS MUST HOLD for <see cref="IsLatestEligibleDuplicate"/>, and the last two are what
    /// keep a GENUINELY LIVE completion on the ordinary path:
    /// </para>
    /// <list type="number">
    ///   <item><description>the pinned instance's negotiation is ENABLED — a legacy registration is
    ///     never routed here;</description></item>
    ///   <item><description>the submitted id is ORDINAL-EXACT against the single latest eligible task
    ///     id, so only that one task can ever reach a confirmation read;</description></item>
    ///   <item><description>the worker is NOT currently executing that task; and</description></item>
    ///   <item><description>that task has NO active queue entry any more.</description></item>
    /// </list>
    /// <para>
    /// IT IS PER DELIVERY AND NEVER OUTLIVES IT. The value is created for one message, passed by value
    /// to that message's handler call, and discarded; nothing stores it, so it cannot leak across
    /// deliveries or widen a later one.
    /// </para>
    /// <para>
    /// IT IS AN OBSERVATION, NOT AN AUTHORIZATION. It mutates nothing and publishes nothing, and the
    /// branch it selects still performs every one of its own guards — including the POST-READ recheck,
    /// which answers the DIFFERENT question of whether ownership changed while the confirmation read
    /// was in flight.
    /// </para>
    /// </remarks>
    /// <param name="Observed">
    /// The lock-consistent ownership observation the classification was made from, reused by the
    /// handler so it never re-observes mutable state for this delivery.
    /// </param>
    /// <param name="ObservationValid">
    /// Whether <paramref name="Observed"/> is meaningful: <c>false</c> when no worker was registered
    /// under the id, or the registered instance was no longer the pinned one, at classification time.
    /// </param>
    /// <param name="IsLatestEligibleDuplicate">
    /// Whether this delivery is a duplicate of the latest eligible, no-longer-held task — the single
    /// fact that drives BOTH the activity suppression and the handler's routing.
    /// </param>
    private readonly record struct CompletionDeliveryRouting(
        WorkerOwnershipSnapshot Observed,
        bool ObservationValid,
        bool IsLatestEligibleDuplicate);

    /// <summary>
    /// Classifies ONE completion delivery from a SINGLE ownership observation, producing the decision
    /// the read loop and the completion handler both consume.
    /// </summary>
    /// <remarks>
    /// THE OBSERVATION IS TAKEN HERE AND ONLY HERE for this delivery. It is returned alongside the
    /// decision precisely so the handler can validate and route from the same instant rather than
    /// taking a second, potentially different, look at mutable ownership.
    /// </remarks>
    /// <param name="pinned">The instance the stream is pinned to.</param>
    /// <param name="taskId">The task id the completion names.</param>
    /// <param name="ackState">The caller-owned latest-eligible holder the id is compared against.</param>
    /// <returns>The per-delivery routing decision and the observation it was made from.</returns>
    private CompletionDeliveryRouting ClassifyCompletionDelivery(
        ConnectedWorker pinned, string taskId, WorkStreamCompletionAckState ackState)
    {
        if (!workerPool.TryGetWorkerSnapshot(pinned.Id, out var observed)
            || !ReferenceEquals(observed.Worker, pinned))
        {
            // GONE OR REPLACED (ABA). There is nothing to classify: the handler refuses the delivery
            // on this same observation, and an invalid observation is never a duplicate, so the
            // activity refresh follows the ordinary path.
            return new CompletionDeliveryRouting(observed, ObservationValid: false, IsLatestEligibleDuplicate: false);
        }

        return new CompletionDeliveryRouting(
            observed,
            ObservationValid: true,
            IsLatestEligibleDuplicate: IsLatestEligibleDuplicate(pinned, taskId, observed, ackState));
    }

    /// <summary>
    /// THE DUPLICATE ENTRY CONDITION, evaluated against a caller-supplied lock-consistent ownership
    /// observation. See <see cref="CompletionDeliveryRouting"/> for the four facts and why the
    /// not-held ones are part of the ENTRY condition rather than only of the post-read recheck.
    /// </summary>
    /// <param name="pinned">The instance the stream is pinned to.</param>
    /// <param name="taskId">The task id the completion names.</param>
    /// <param name="observed">The caller's lock-consistent ownership observation of that instance.</param>
    /// <param name="ackState">
    /// The caller-owned latest-eligible holder — the ONE holder of the invocation this delivery
    /// belongs to, never a shared or per-message one.
    /// </param>
    /// <returns><c>true</c> when the delivery is a duplicate of the latest eligible, no-longer-held task.</returns>
    private bool IsLatestEligibleDuplicate(
        ConnectedWorker pinned,
        string taskId,
        WorkerOwnershipSnapshot observed,
        WorkStreamCompletionAckState ackState)
    {
        if (!pinned.CompletionReceiptAckEnabled)
            return false;

        var latestEligible = ackState.LatestEligibleTaskId;
        if (latestEligible is null
            || !string.Equals(latestEligible, taskId, StringComparison.Ordinal))
        {
            return false;
        }

        // ── THE INITIAL NOT-HELD GATE ─────────────────────────────────────────────────────────
        // The worker must not be executing this very task, and the queue must no longer hold an
        // active entry for it. A DIFFERENT successor being busy is irrelevant — that is the ordinary
        // shape a duplicate arrives in.
        //
        // A latest-eligible id that has been RE-DISPATCHED is a live assignment again, and the
        // completion that follows it is a REAL completion that must be RECORDED and RELEASED.
        // Entering the read-only branch for it would compare it against the OLD retained receipt and
        // discard it — silently swallowing a completion the worker genuinely produced.
        if (string.Equals(observed.CurrentTaskId, taskId, StringComparison.Ordinal))
            return false;

        return taskQueue.GetActiveTask(taskId) is null;
    }

    /// <summary>
    /// THE SAME-STREAM DUPLICATE RE-ACKNOWLEDGEMENT: answers a repeated delivery of this stream's ONE
    /// latest eligible completion from the RETAINED evidence, without processing anything again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT IT IS, HONESTLY. It is a BOUNDED re-acknowledgement of exactly one task: the id held by
    /// THIS <c>WorkStream</c> invocation's own holder, put there by the most recent ordinary
    /// completion whose evidence was confirmed and whose checked release succeeded. It is NOT a
    /// one-retransmission limit, NOT a history cache, NOT reconnect/resume authorization and NOT a
    /// stored-receipt replay service — the holder keeps one id and advances only on the next
    /// successfully released ordinary completion, and a failed duplicate attempt never clears or
    /// advances it.
    /// </para>
    /// <para>
    /// ITS ENTRY CONDITION IS <see cref="IsLatestEligibleDuplicate"/>, which has ALREADY established —
    /// before this method is called and therefore before any mapping or confirmation read — that the
    /// negotiation is enabled, that the id is ordinal-exact against the single holder, and that the task
    /// is NO LONGER HELD (not the worker's current task and no active queue entry). A re-dispatched,
    /// genuinely live task therefore never reaches this method at all.
    /// </para>
    /// <para>
    /// THE ORDER IS THE CONTRACT, and every step is a refusal that emits nothing:
    /// </para>
    /// <list type="number">
    ///   <item><description>EXPLICIT MODEL PRESENCE is required, exactly as for the ordinary enabled
    ///     path. The mapping is the SAME boundary mapper, so the supplied evidence is mapped
    ///     faithfully — and nothing is ever filled in from the queue, from the worker's current model,
    ///     or from the retained row. An unknown wire status is a malformed input and is handled
    ///     locally.</description></item>
    ///   <item><description><see cref="IWorkerCompletionRecorder.ConfirmStoredReceipt"/> is the ONLY
    ///     AUTHORIZATION. <c>true</c> alone permits another acknowledgement; <c>false</c> and every
    ///     refusal exception produce none. Nothing is written, released or notified either
    ///     way.</description></item>
    ///   <item><description>THE ELIGIBILITY IS RECHECKED AFTER THE READ, before the enqueue. This is a
    ///     SECOND gate with a DIFFERENT job from the entry condition: the entry gate asked whether the
    ///     task was already held when the delivery arrived, while this one asks whether the ownership
    ///     CHANGED WHILE THE READ WAS IN FLIGHT. The pinned instance must still be the one registered
    ///     under its id — a replacement registered during the read must never be answered on its
    ///     predecessor's evidence — and the old task must still be unheld. A DIFFERENT successor being
    ///     busy is fine: that is the ordinary shape this branch exists for.</description></item>
    /// </list>
    /// <para>
    /// THE READ IS NOT REFRESHED AS ACTIVITY for the successor's task: this method deliberately never
    /// calls <c>TouchActivity</c>, because the duplicate is about an OLD task and must not extend a
    /// successor's inactivity-derived lifetime.
    /// </para>
    /// </remarks>
    /// <param name="worker">The pinned instance the duplicate was delivered on.</param>
    /// <param name="complete">The duplicate completion payload.</param>
    private void HandleLatestEligibleDuplicate(ConnectedWorker worker, TaskComplete complete)
    {
        // ── (1) EXPLICIT MODEL PRESENCE, required exactly as on the ordinary enabled path ──────
        if (!complete.HasModel)
        {
            LogReceiptAckDuplicateIgnored(
                worker.Id, complete.TaskId, OwnershipRefusalReasons.ModelPresenceRequired);
            return;
        }

        // ── (2) THE SUPPLIED EVIDENCE IS MAPPED, AND NOTHING ELSE IS SUBSTITUTED ───────────────
        // The SAME boundary mapper is reused, and the result carries the payload's own explicitly
        // present model verbatim. There is deliberately NO fallback to the queue's model, to the
        // worker's current model, or to the retained row's result: a comparison against substituted
        // evidence would be no comparison at all. An unknown wire status is malformed input and is
        // handled locally, exactly like a mapping failure on the ordinary path.
        TaskResult evidence;
        try
        {
            evidence = GrpcMapper.ToDomain(complete) with { Model = complete.Model };
        }
        catch (Exception ex)
        {
            LogCompletionMappingFailed(worker.Id, complete.TaskId, ex);
            return;
        }

        // ── (3) THE READ-ONLY CONFIRMATION IS THE ONLY AUTHORIZATION ───────────────────────────
        bool confirmed;
        try
        {
            if (_completionRecorder is null)
            {
                // No recorder means nothing that could have retained evidence, so nothing can be
                // confirmed. This is a local refusal, never a fall-through into the ordinary path.
                LogReceiptAckDuplicateIgnored(
                    worker.Id, complete.TaskId, nameof(WorkerCompletionRecordingFailureReason.MissingRecorder));
                return;
            }

            confirmed = _completionRecorder.ConfirmStoredReceipt(worker.Id, complete.TaskId, evidence);
        }
        catch (WorkerCompletionRecordingException refusal)
        {
            LogReceiptAckDuplicateIgnored(worker.Id, complete.TaskId, refusal.Reason.ToString());
            return;
        }
        catch (Exception unexpected)
        {
            LogReceiptAckDuplicateIgnored(worker.Id, complete.TaskId, unexpected.GetType().Name);
            return;
        }

        if (!confirmed)
        {
            // The retained evidence does NOT match this completion, so it is not acknowledged. No
            // Conflict or Indeterminate is fabricated for a read-only operation, and nothing is
            // re-recorded in the hope of making a later attempt succeed.
            LogReceiptAckDuplicateIgnored(
                worker.Id, complete.TaskId, nameof(WorkerCompletionRecordingFailureReason.InvalidContext));
            return;
        }

        // ── (4) THE ELIGIBILITY RECHECK, AFTER THE READ AND BEFORE THE ENQUEUE ─────────────────
        // A SECOND gate, answering a DIFFERENT question from the entry condition. The entry gate
        // already proved the task was not held when the delivery arrived; this one proves the
        // ownership did not CHANGE while the confirmation read was in flight:
        //   (a) the pinned instance is still the one registered under its id — an ABA replacement
        //       that landed during the confirmation read must NOT be answered on behalf of the
        //       instance whose completion was confirmed; and
        //   (b) the old task is STILL not held — no active queue entry AND not the worker's current
        //       task. A DIFFERENT successor being busy is irrelevant.
        if (!workerPool.TryGetWorkerSnapshot(worker.Id, out var current)
            || !ReferenceEquals(current.Worker, worker))
        {
            LogReceiptAckDuplicateIgnored(
                worker.Id, complete.TaskId, OwnershipRefusalReasons.PinnedInstanceReplaced);
            return;
        }

        if (taskQueue.GetActiveTask(complete.TaskId) is not null
            || string.Equals(current.CurrentTaskId, complete.TaskId, StringComparison.Ordinal))
        {
            LogReceiptAckDuplicateIgnored(
                worker.Id, complete.TaskId, OwnershipRefusalReasons.LatestEligibleTaskStillHeld);
            return;
        }

        // ── (5) THE ACKNOWLEDGEMENT, THROUGH THE SAME EXISTING CHANNEL — BEST EFFORT ───────────
        // The identity is echoed verbatim and the publication shares the exact guarded path the
        // ordinary completion uses. Nothing else happens: no Record, no release, no queue removal, no
        // pipeline mutation, no dashboard success notification and no completion notification.
        //
        // THE SUCCESS LINE IS EMITTED ONLY WHEN THE MESSAGE WAS ACTUALLY QUEUED, and it says QUEUED:
        // queue acceptance is not delivery, and a lost enqueue already has its own guarded diagnostic.
        if (TryPublishCompletionReceiptAck(worker, complete.TaskId))
        {
            logger.LogInformation(
                "Worker {WorkerId}: latest eligible completion re-acknowledgement queued for task {TaskId}",
                worker.Id,
                complete.TaskId);
        }
    }

    /// <summary>
    /// THE GUARDED DUPLICATE-REFUSAL DIAGNOSTIC: a duplicate delivery of the stream's latest eligible
    /// completion was refused, so no re-acknowledgement was published and nothing was processed again.
    /// </summary>
    /// <remarks>
    /// GUARDED, like every other refusal diagnostic: the whole log call sits inside its own no-throw
    /// guard, so a throwing logger can never turn a local refusal into an escaping exception that
    /// would unwind the worker's stream.
    /// </remarks>
    /// <param name="workerId">The pinned worker that delivered the duplicate.</param>
    /// <param name="taskId">The opaque task id the duplicate named.</param>
    /// <param name="reason">Why the duplicate was refused.</param>
    private void LogReceiptAckDuplicateIgnored(string workerId, string taskId, string reason)
    {
        try
        {
            logger.LogWarning(
                "Worker {WorkerId} duplicate completion for latest eligible task {TaskId} ignored: " +
                "{Reason}; no acknowledgement was published and nothing was released, removed, " +
                "recorded or notified",
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
    /// PUBLISHES ONE COMPLETION-RECEIPT ACKNOWLEDGEMENT through the PINNED INSTANCE'S OWN EXISTING
    /// message channel — the single channel the stream's own pump already forwards from. No new
    /// writer, no direct gRPC write, no timer, no retry and no durable outbox.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE IDENTITY IS ECHOED VERBATIM: the exact opaque task id and the exact pinned worker id, with
    /// no trimming, splitting, parsing or normalization of either.
    /// </para>
    /// <para>
    /// IT IS BEST-EFFORT AND FULLY ISOLATED. A closed channel, a full/broken channel or a throwing
    /// channel writer is caught here, reported in a guarded diagnostic and swallowed: the ordinary
    /// dashboard/downstream notification of the SAME completion still runs, the release that was
    /// already applied is not undone, and nothing is recorded or notified a second time.
    /// </para>
    /// <para>
    /// QUEUE SUCCESS IS NOT PROOF OF DELIVERY. A successful <c>TryWrite</c> says only that the message
    /// was queued for the pump; the worker may never consume it, so no delivery is ever claimed here.
    /// </para>
    /// </remarks>
    /// <param name="worker">The pinned instance whose channel the acknowledgement travels on.</param>
    /// <param name="taskId">The opaque task id of the just-released completion.</param>
    /// <returns>
    /// <c>true</c> when the message was QUEUED on the instance's channel; <c>false</c> when the channel
    /// refused it or the enqueue threw. It is NOT a delivery claim in either direction.
    /// </returns>
    private bool TryPublishCompletionReceiptAck(ConnectedWorker worker, string taskId)
    {
        try
        {
            var ack = new OrchestratorMessage
            {
                CompletionReceiptAck = new CompletionReceiptAck
                {
                    TaskId = taskId,
                    WorkerId = worker.Id,
                },
            };

            if (worker.MessageChannel.Writer.TryWrite(ack))
                return true;

            LogReceiptAckNotQueued(null, worker.Id, taskId);
            return false;
        }
        catch (Exception ex)
        {
            // THE ENQUEUE FAILURE IS ISOLATED: nothing about the ordinary completion path changes
            // because an advisory acknowledgement could not be queued.
            LogReceiptAckNotQueued(ex, worker.Id, taskId);
            return false;
        }
    }

    /// <summary>
    /// THE GUARDED ACKNOWLEDGEMENT-ENQUEUE DIAGNOSTIC: the acknowledgement could not be queued on the
    /// pinned instance's channel, so nothing was published for it.
    /// </summary>
    /// <remarks>
    /// GUARDED like every other diagnostic on a refusal path, and for the same reason: the whole log
    /// call sits inside its own no-throw guard, so a throwing logger can never turn a lost
    /// acknowledgement into an escaping exception that would unwind the completion handler — or, on
    /// the pump path, the worker's stream.
    /// </remarks>
    /// <param name="failure">The enqueue failure, or <c>null</c> when the channel simply refused the write.</param>
    /// <param name="workerId">The pinned worker the acknowledgement was addressed to.</param>
    /// <param name="taskId">The opaque task id the acknowledgement was about.</param>
    private void LogReceiptAckNotQueued(Exception? failure, string workerId, string taskId)
    {
        try
        {
            if (failure is null)
            {
                logger.LogWarning(
                    "Worker {WorkerId}: completion-receipt acknowledgement for task {TaskId} was " +
                    "not queued; the completion itself is unaffected and no delivery is claimed",
                    workerId,
                    taskId);
                return;
            }

            logger.LogWarning(
                "Worker {WorkerId}: completion-receipt acknowledgement for task {TaskId} could not " +
                "be queued; the completion itself is unaffected and no delivery is claimed — {Detail}",
                workerId,
                taskId,
                MessageOrPlaceholder(failure));
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the handled disposition.
        }
    }

    /// <summary>
    /// THE GUARDED ACKNOWLEDGEMENT-WRITE DIAGNOSTIC for the pump: forwarding an
    /// acknowledgement to the response writer failed, so it was dropped while the pump carried on.
    /// </summary>
    /// <remarks>
    /// GUARDED, so a throwing logger cannot convert an isolated acknowledgement-write failure into a
    /// faulted stream. NO RECOVERY IS CLAIMED: a genuinely broken connection is not repaired by this,
    /// and the general stream teardown is deliberately unchanged.
    /// </remarks>
    /// <param name="failure">The response write failure; its message is included as evidence.</param>
    /// <param name="workerId">The pinned worker whose stream was being written to.</param>
    /// <param name="ack">The acknowledgement that could not be forwarded.</param>
    private void LogReceiptAckWriteFailed(
        Exception failure, string workerId, CompletionReceiptAck? ack)
    {
        try
        {
            logger.LogWarning(
                "Worker {WorkerId}: completion-receipt acknowledgement for task {TaskId} could not " +
                "be written to the worker's stream and was dropped; a broken connection is not " +
                "recovered here — {Detail}",
                workerId,
                ack?.TaskId ?? "(unknown)",
                MessageOrPlaceholder(failure));
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the handled disposition.
        }
    }

}

/// <summary>
/// THE BOUNDED ACKNOWLEDGEMENT ELIGIBILITY OF ONE <see cref="HiveOrchestratorService.WorkStream"/>
/// INVOCATION: at most ONE task id — the most recent completion on THAT invocation whose durable
/// evidence was confirmed and whose checked release SUCCEEDED.
/// </summary>
/// <remarks>
/// <para>
/// ITS OWNER IS LITERALLY ONE <c>WorkStream</c> CALL. The instance is allocated as a LOCAL of that
/// invocation, outside its read loop, and is passed EXPLICITLY to every consumer of the delivery it
/// belongs to. It is not a field, not static state, not attached to a <see cref="ConnectedWorker"/>
/// and not registered anywhere, so its lifetime is exactly the invocation's: a second stream — the
/// losing attachment, a fresh stream after a re-registration under the same worker id, or a stream
/// for any other worker — has its OWN, EMPTY holder and can never read this one. Nothing resets,
/// reuses or transfers it; when the invocation returns, it is simply gone.
/// </para>
/// <para>
/// WHAT ADVANCES IT: only an ORDINARY completion that passed every validated ownership check, was
/// RECORDED first, and then had its checked release APPLIED. A mapping failure, a recording refusal
/// and a refused checked release all leave it exactly as it was — no eligibility is ever created by
/// a failure.
/// </para>
/// <para>
/// IT IS DELIBERATELY JUST ONE OPAQUE ID. It is not a history, not a receipt cache, not a retry
/// budget and not a reconnect/resume authorization: the id is stored so a later duplicate attempt on
/// the SAME stream can be answered from retained evidence, and the holder simply advances on the next
/// successful ordinary completion.
/// </para>
/// <para>
/// THREADING: the WorkStream read loop handles messages strictly sequentially, so the ordinary
/// completion path is single-threaded here. The lock nevertheless makes the read/write pair atomic
/// for any observer, because the value is read on paths that must not tear against a concurrent
/// advance.
/// </para>
/// </remarks>
internal sealed class WorkStreamCompletionAckState
{
    private readonly Lock _gate = new();
    private string? _latestEligibleTaskId;

    /// <summary>
    /// The most recent task id made acknowledgement-eligible by a successful ordinary completion on
    /// the owning invocation, or <c>null</c> when no ordinary completion has been released on it yet.
    /// </summary>
    internal string? LatestEligibleTaskId
    {
        get
        {
            lock (_gate)
                return _latestEligibleTaskId;
        }
    }

    /// <summary>
    /// Advances the single latest-eligible slot to <paramref name="taskId"/>, REPLACING any previous
    /// id rather than accumulating one.
    /// </summary>
    /// <param name="taskId">The opaque task id of the just-released completion. Never blank.</param>
    internal void AdvanceLatestEligible(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        lock (_gate)
            _latestEligibleTaskId = taskId;
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
