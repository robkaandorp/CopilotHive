using CopilotHive.Persistence;
using CopilotHive.Shared.Grpc;

// THE ROLE ALIAS. Both CopilotHive.Workers and CopilotHive.Shared.Grpc declare a `WorkerRole`
// (the domain enum and its protobuf twin), so the namespace import alone is ambiguous. Only the
// DOMAIN role is ever meant here — the transport type is reached exclusively through GrpcMapper.
using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Services;

/// <summary>
/// The NARROW, INJECTABLE FACE of the assignment recorder/publisher.
/// <para>
/// It exists so a caller can depend on the capability rather than the concrete publisher — which is
/// what lets a fixture substitute its own narrow implementation for injection. It deliberately
/// carries ONE operation and no state: the caller supplies the already-PINNED
/// <see cref="ConnectedWorker"/> instance it dequeued the work for, the ACTUAL dequeued
/// <see cref="WorkTask"/>, and its own <see cref="CancellationToken"/>.
/// </para>
/// <para>
/// PUBLIC BY NECESSITY, NARROW BY DESIGN: <see cref="HiveOrchestratorService"/> is a PUBLIC type, so
/// an appended constructor parameter cannot be an internal type (C# forbids the inconsistent
/// accessibility). This interface is the widest thing that parameter may name, and it exposes
/// nothing but the single publish operation — the concrete publisher, the recording-failure type and
/// the store all remain internal.
/// </para>
/// <para>
/// THE CONCRETE PUBLISHER IS MANDATORY FOR TASK SENDS. Both live delivery paths — the Ready-driven
/// send and the eager send in <see cref="GrpcWorkerGateway"/> — delegate their final channel write
/// to it. There is NO fallback to a raw channel write: a caller that cannot resolve a publisher
/// must fail closed with the same explicit no-send disposition (see
/// <see cref="WorkerAssignmentRecordingException.MissingPublisher"/>) rather than publish an
/// unrecorded assignment.
/// </para>
/// </summary>
public interface IWorkerAssignmentPublisher
{
    /// <summary>
    /// Records the delivered task's assignment context EXACTLY ONCE and, only when that record is
    /// confirmed (<see cref="WorkerAssignmentWriteStatus.Recorded"/> or
    /// <see cref="WorkerAssignmentWriteStatus.AlreadyRecorded"/>), publishes the matching assignment
    /// to the pinned worker's message channel.
    /// </summary>
    /// <param name="worker">The PINNED worker instance the assignment belongs to.</param>
    /// <param name="task">The ACTUAL dequeued work task being delivered.</param>
    /// <param name="cancellationToken">The caller's cancellation token, observed before recording and
    /// before publication.</param>
    /// <returns>A task that completes once this invocation's channel write has completed.</returns>
    /// <exception cref="WorkerAssignmentRecordingException">
    /// Any recording refusal: <see cref="WorkerAssignmentRecordingFailureReason.InvalidContext"/>,
    /// <see cref="WorkerAssignmentRecordingFailureReason.StoreError"/>,
    /// <see cref="WorkerAssignmentRecordingFailureReason.Conflict"/> or
    /// <see cref="WorkerAssignmentRecordingFailureReason.Indeterminate"/>. No assignment is published
    /// for any of them.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The caller's own cancellation was observed before recording or before publication.
    /// </exception>
    Task PublishAsync(ConnectedWorker worker, WorkTask task, CancellationToken cancellationToken);
}

/// <summary>
/// THE ASSIGNMENT PUBLISHER — the single production path that records the server's
/// INTENDED assignment binding and only then publishes the assignment to the pinned worker.
/// It is the recording+publication leg of BOTH live delivery paths: the Ready-driven send in
/// <see cref="HiveOrchestratorService"/> and the eager send in <see cref="GrpcWorkerGateway"/>.
/// <para>
/// IT RESOLVES FROM THE DELIVERED TASK, NEVER FROM A GUESS. The pipeline is resolved with
/// <see cref="GoalPipelineManager.GetByTaskId"/> using the DELIVERED
/// <see cref="WorkTask.TaskId"/>, and the pipeline's goal identity must equal the delivered
/// <see cref="WorkTask.GoalId"/>. The Pending <see cref="WorkSlot"/> and the current pointer come
/// from <see cref="GoalPipeline.CaptureAdmissionOwnership"/> followed by
/// <see cref="GoalPipeline.PreflightAdmissionOwnership"/>.
/// </para>
/// <para>
/// THE SLOT IS AUTHORITATIVE. The recorded position (iteration, phase, occurrence) and attempt are
/// the values the pipeline's OWN registry holds for the delivered task at capture time. They are
/// NEVER reconstructed from the task ID's text, from the pipeline's current phase, or from any other
/// field on the delivered task — the task id serves ONLY as the lookup key — so a caller that passes
/// a stale or unpopulated task cannot make the recorded position disagree with the registry. A
/// delivery for which no matching Pending slot / pointer exists is REFUSED; no context is ever
/// synthesized.
/// </para>
/// <para>
/// THE PINNED-INSTANCE CHECK IS A POINT-IN-TIME OBSERVATION, NOT ATOMIC RESERVATION.
/// <see cref="WorkerPool.GetWorker"/> is consulted once, immediately before recording, and the
/// delivered instance must be the SAME INSTANCE the pool currently holds under that id. That proves
/// only that the identity held at the instant of the read; it does NOT reserve the worker, cannot
/// stop a removal or a same-id re-registration (ABA) from being committed a moment later, and says
/// nothing about whether the channel write that follows is received. Because the assignment is
/// written to the PINNED instance's own channel, a later replacement can at most make the message
/// stale — never route it to the replacement.
/// </para>
/// <para>
/// THE RECORD IS INSERT-ONCE, AND THE OUTCOME DECIDES THE SEND.
/// <see cref="WorkerAssignmentContextStore.InsertOnce"/> is called EXACTLY ONCE.
/// <see cref="WorkerAssignmentWriteStatus.Recorded"/> and
/// <see cref="WorkerAssignmentWriteStatus.AlreadyRecorded"/> both allow THIS invocation's channel
/// publication: an <see cref="WorkerAssignmentWriteStatus.AlreadyRecorded"/> outcome is ONLY
/// identical-intent evidence about a row that already exists, so duplicate caller invocations CAN
/// legitimately resend under the existing live delivery model. This type therefore promises NO
/// exactly-once send and adds NO autonomous or restart-driven replay.
/// <see cref="WorkerAssignmentWriteStatus.Conflict"/> and
/// <see cref="WorkerAssignmentWriteStatus.Indeterminate"/> PROHIBIT publication.
/// </para>
/// <para>
/// NO HIDDEN RECONCILIATION. After a successful record this type performs NO extra production
/// readback (the store's <c>Load</c> is deliberately not called), no automatic retry, no row
/// deletion or rebinding, and no timestamp refresh. A write failure retains whatever the database
/// committed: the recorded context is left exactly as it is.
/// </para>
/// <para>
/// SCOPE, HONESTLY. This type records and publishes; it does no delivery authorization, no
/// acknowledgement, no reconciliation and no replay, and a completed channel write is NOT proof of
/// receipt. Both live delivery paths (Ready-driven and eager) delegate their publication here, so
/// live assignment-recording coverage is complete.
/// </para>
/// </summary>
internal sealed class WorkerAssignmentPublisher : IWorkerAssignmentPublisher
{
    private readonly GoalPipelineManager _pipelineManager;
    private readonly WorkerPool _workerPool;
    private readonly WorkerAssignmentContextStore _store;

    /// <summary>
    /// Initialises a new <see cref="WorkerAssignmentPublisher"/>.
    /// </summary>
    /// <param name="pipelineManager">The routing authority for the delivered task id.</param>
    /// <param name="workerPool">The pool consulted for the point-in-time pinned-instance check.</param>
    /// <param name="store">The INSERT-ONCE assignment-context store.</param>
    /// <exception cref="ArgumentNullException">Any dependency is <c>null</c>.</exception>
    internal WorkerAssignmentPublisher(
        GoalPipelineManager pipelineManager,
        WorkerPool workerPool,
        WorkerAssignmentContextStore store)
    {
        ArgumentNullException.ThrowIfNull(pipelineManager);
        ArgumentNullException.ThrowIfNull(workerPool);
        ArgumentNullException.ThrowIfNull(store);

        _pipelineManager = pipelineManager;
        _workerPool = workerPool;
        _store = store;
    }

    /// <inheritdoc />
    public async Task PublishAsync(ConnectedWorker worker, WorkTask task, CancellationToken cancellationToken)
    {
        // THE NULL GUARDS are caller-bug refusals, not recording failures: a null worker or task
        // names no assignment at all, so no recording outcome is invented for it.
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(task);

        // ── (1) THE ROUTE. The DELIVERED task id is the only lookup key; a missing pipeline is a
        //        refusal, and a pipeline that does not own the delivered goal is a refusal too.
        //        Nothing is looked up by goal id or reconstructed from the task-id's text. ──
        var pipeline = _pipelineManager.GetByTaskId(task.TaskId);
        if (pipeline is null)
        {
            throw WorkerAssignmentRecordingException.InvalidContext(
                $"no pipeline is registered for the delivered task '{task.TaskId}'");
        }

        if (!string.Equals(pipeline.GoalId, task.GoalId, StringComparison.Ordinal))
        {
            throw WorkerAssignmentRecordingException.InvalidContext(
                $"the pipeline owning the delivered task '{task.TaskId}' tracks goal '{pipeline.GoalId}' " +
                $"but the delivered task names goal '{task.GoalId}'");
        }

        // ── (2) THE CAPTURE AND PREFLIGHT. The detached ownership carrier is validated by the
        //        pipeline's own pure preflight — the SAME validator the restore path uses, so no
        //        registry rule is duplicated here. A throw from either step is a refusal whose
        //        EXACT caught exception is retained as evidence. Neither step takes a token, so no
        //        cancellation can originate here: the caller's own cancellation is observed
        //        explicitly below, outside this wrapper. ──
        AdmissionOwnershipSnapshot validated;
        try
        {
            var captured = pipeline.CaptureAdmissionOwnership();
            validated = GoalPipeline.PreflightAdmissionOwnership(captured);
        }
        catch (Exception captureOrPreflight)
        {
            throw WorkerAssignmentRecordingException.InvalidContext(
                $"the admission ownership snapshot for task '{task.TaskId}' could not be validated " +
                $"({captureOrPreflight.GetType().Name})",
                captureOrPreflight);
        }

        // ── (3) THE POINTER. The pointer must name the DELIVERED task: a delivery whose task is no
        //        longer the pipeline's active task is refused, never recorded. ──
        if (!string.Equals(validated.ActiveTaskId, task.TaskId, StringComparison.Ordinal))
        {
            throw WorkerAssignmentRecordingException.InvalidContext(
                $"the pipeline's active-task pointer is '{validated.ActiveTaskId}' but the delivered " +
                $"task is '{task.TaskId}'");
        }

        // ── (4) THE SLOT. The preflight has already proven the pointer has a matching PENDING slot
        //        (an absent, retired or non-Pending slot is one of its own refusals); the lookup here
        //        only RE-LOCATES that validated slot so its authoritative position and attempt are
        //        carried verbatim. The null arm is therefore unreachable in practice and is still an
        //        explicit refusal rather than an assumption. ──
        var slot = FindPendingSlot(validated, task.TaskId)
            ?? throw WorkerAssignmentRecordingException.InvalidContext(
                $"task '{task.TaskId}' has no pending work slot in the pipeline's registry");

        // ── (5) THE ROLE. The delivered role must be the one the SLOT's phase maps to. A phase
        //        with no worker mapping is the same refusal, and the mapping's own exception is kept
        //        as the diagnostic cause. ──
        WorkerRole derivedRole;
        try
        {
            derivedRole = slot.Position.Phase.ToWorkerRole();
        }
        catch (InvalidOperationException noMapping)
        {
            throw WorkerAssignmentRecordingException.InvalidContext(
                $"the slot for task '{task.TaskId}' carries phase '{slot.Position.Phase}', which has no " +
                "worker role",
                noMapping);
        }

        if (task.Role != derivedRole)
        {
            throw WorkerAssignmentRecordingException.InvalidContext(
                $"the delivered role '{task.Role}' does not match role '{derivedRole}' mapped from the " +
                $"slot's phase '{slot.Position.Phase}' for task '{task.TaskId}'");
        }

        // ── (6) THE CONTEXT. The EXISTING carrier is constructed with the pinned worker id, the
        //        delivered role and the VERBATIM model; the goal identity is the pipeline's captured
        //        identity (already proven equal to the delivered goal) and the slot is the registry's
        //        own immutable value. Its constructor is the single validation authority; a refusal
        //        there is retained with its exact exception. The constructor does no I/O and takes no
        //        token, so no cancellation can originate here. ──
        WorkerAssignmentContext context;
        try
        {
            context = new WorkerAssignmentContext(
                validated.GoalId,
                worker.Id,
                task.Role,
                slot,
                task.Model);
        }
        catch (Exception contextValidation)
        {
            throw WorkerAssignmentRecordingException.InvalidContext(
                $"the assignment context for task '{task.TaskId}' failed its own validation " +
                $"({contextValidation.GetType().Name})",
                contextValidation);
        }

        // ── (7) THE PINNED-INSTANCE CHECK — a POINT-IN-TIME read, deliberately NOT atomic
        //        reservation. It runs BEFORE the record so a delivery whose pinned worker is no
        //        longer the pool's instance cannot leave a recorded context behind. ──
        if (!ReferenceEquals(_workerPool.GetWorker(worker.Id), worker))
        {
            throw WorkerAssignmentRecordingException.InvalidContext(
                $"the pinned worker instance for '{worker.Id}' is no longer the instance registered " +
                "in the pool");
        }

        // ── (8) THE PRE-RECORDING CALLER-CANCELLATION OBSERVATION. A caller-cancelled invocation
        //        must reach the store with nothing recorded and nothing published. The caller's own
        //        OperationCanceledException propagates: it is never a blocked-result wrapper. ──
        cancellationToken.ThrowIfCancellationRequested();

        // ── (9) THE ONE INSERT. Exactly one call, ever. The store takes no cancellation token, so
        //        nothing inside it can observe a caller cancellation: the ONLY cancellation this
        //        method can surface is one of the two explicit caller observations around this call,
        //        and neither is inside this wrapper. Every throw the store produces — its argument
        //        validation, the context acquisition, the transaction begin, the write and the
        //        duplicate readback — is therefore a genuine STORE failure and retains its EXACT
        //        exception. (The store reports a throwing write as
        //        WorkerAssignmentWriteStatus.Indeterminate instead, handled below.) ──
        WorkerAssignmentWriteResult result;
        try
        {
            result = _store.InsertOnce(context);
        }
        catch (Exception storeFailure)
        {
            throw WorkerAssignmentRecordingException.StoreError(
                $"recording the assignment context for task '{task.TaskId}' threw " +
                $"({storeFailure.GetType().Name})",
                storeFailure);
        }

        switch (result.Status)
        {
            case WorkerAssignmentWriteStatus.Recorded:
            case WorkerAssignmentWriteStatus.AlreadyRecorded:
                // Both outcomes permit THIS invocation's publication. AlreadyRecorded is only
                // identical-intent evidence: it says the stored row already carries this exact
                // context, not that the assignment was ever delivered — a duplicate invocation may
                // therefore legitimately resend, and no exactly-once send is promised.
                break;
            case WorkerAssignmentWriteStatus.Conflict:
                // A DIFFERENT context is already recorded for this task id: a refusal to rebind.
                // Nothing was changed and nothing is published. No provider cause is invented.
                throw WorkerAssignmentRecordingException.Conflict(
                    $"a different assignment context is already recorded for task '{task.TaskId}'");
            case WorkerAssignmentWriteStatus.Indeterminate:
                // The write did not confirm. Publication is prohibited and the store's exact
                // exception object is carried as the evidence.
                throw WorkerAssignmentRecordingException.Indeterminate(
                    $"the assignment-context write for task '{task.TaskId}' did not confirm",
                    result.WriteException!);
            default:
                throw new InvalidOperationException(
                    $"WorkerAssignmentPublisher: unhandled WorkerAssignmentWriteStatus '{result.Status}'.");
        }

        // ── (10) THE PRE-PUBLICATION CALLER-CANCELLATION OBSERVATION. It is made AFTER the record
        //         on purpose: a cancellation observed here cancels the SEND only, and the already
        //         committed context is deliberately retained (nothing is deleted or rebound). ──
        cancellationToken.ThrowIfCancellationRequested();

        // ── (11) THE PUBLICATION. Everything from here is POST-RECORD: a mapping or channel-write
        //         failure propagates UNCHANGED (it is never re-labelled as a recording failure), and
        //         the recorded context stays exactly as it was committed. The write targets the
        //         PINNED instance's own channel, so a same-id replacement can never receive it. ──
        await worker.MessageChannel.Writer.WriteAsync(
            new OrchestratorMessage { Assignment = GrpcMapper.ToGrpc(task) },
            cancellationToken);
    }

    /// <summary>
    /// Re-locates the validated PENDING slot for <paramref name="taskId"/> in the preflight-validated
    /// registry by ORDINAL task-id comparison, returning the existing immutable
    /// <see cref="WorkSlot"/> value verbatim.
    /// </summary>
    /// <remarks>
    /// The preflight has already enforced that the active task has a matching Pending slot and that
    /// the whole registry is well-formed, so the only way this can return <c>null</c> is a change in
    /// the preflight's own contract — which is why the caller still refuses explicitly instead of
    /// assuming a match. No identity is parsed or reconstructed from the task id, and the returned
    /// slot carries the registry's own position and attempt.
    /// </remarks>
    /// <param name="validated">The detached, preflight-validated ownership carrier.</param>
    /// <param name="taskId">The delivered task id to match ordinally.</param>
    /// <returns>The matching Pending <see cref="WorkSlot"/>, or <c>null</c> when none matches.</returns>
    private static WorkSlot? FindPendingSlot(AdmissionOwnershipSnapshot validated, string taskId)
    {
        foreach (var view in validated.Registry.Slots)
        {
            if (view is null || view.Slot is null)
                continue;
            if (view.State != WorkSlotState.Pending)
                continue;
            if (!string.Equals(view.Slot.TaskId, taskId, StringComparison.Ordinal))
                continue;

            return view.Slot;
        }

        return null;
    }
}

/// <summary>
/// The explicit reasons an assignment recording could NOT proceed. The set is
/// deliberately small and flat — one reason per refusal family, no taxonomy framework.
/// </summary>
internal enum WorkerAssignmentRecordingFailureReason
{
    /// <summary>
    /// NO PUBLISHER IS AVAILABLE. The caller could not resolve the mandatory
    /// <see cref="IWorkerAssignmentPublisher"/>, so the assignment was neither recorded nor
    /// published. This is the fail-CLOSED disposition: the old raw channel write is NEVER used as a
    /// fallback, because an unrecorded assignment must not be delivered.
    /// </summary>
    MissingPublisher,

    /// <summary>
    /// THE DELIVERED DELIVERY DOES NOT MATCH ITS OWNERSHIP. Covers every worker-instance, route,
    /// pointer, slot and role failure: a missing pipeline or a foreign goal, an absent, retired or
    /// non-Pending slot, a pointer that names another task, a role that disagrees with the slot's
    /// phase, a pinned worker instance the pool no longer holds, and a context that fails its own
    /// validation. No context is synthesized.
    /// <para>
    /// TWO SUB-FORMS, DISTINGUISHED ONLY BY THE EVIDENCE. A refusal this publisher decides by its own
    /// comparison (missing pipeline, foreign goal, wrong pointer, role mismatch, wrong pinned
    /// instance) carries no inner exception. A refusal raised by a validator that itself threw — the
    /// pipeline's capture/preflight (which owns the registry rules, including an absent, retired or
    /// non-Pending slot for the active task) or the context constructor — carries that EXACT caught
    /// exception, because a validator failure is evidence about the state, not an invented cause.
    /// </para>
    /// </summary>
    InvalidContext,

    /// <summary>
    /// THE STORE ITSELF THREW. A store validation, context-acquisition, transaction-begin or
    /// read/readback failure propagated out of the recording call; the exact caught exception is the
    /// inner exception. Nothing was published.
    /// </summary>
    StoreError,

    /// <summary>
    /// A DIFFERENT context is already durably recorded for the delivered task id. The store refused
    /// to rebind and changed nothing; publication is prohibited. There is no provider cause to
    /// report — the refusal is the database's duplicate decision.
    /// </summary>
    Conflict,

    /// <summary>
    /// THE WRITE DID NOT CONFIRM (a throwing INSERT/<c>SaveChanges</c>/<c>Commit</c>, or an
    /// affected-row count other than 0/1). No claim is made about rollback or about whether a row
    /// now exists, and publication is prohibited; the store's exact
    /// <see cref="WorkerAssignmentWriteResult.WriteException"/> is the inner exception.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// THE RECORDING-FAILURE EXCEPTION: the single explicit failure contract of the assignment
/// publisher.
/// <para>
/// THE THREE EVIDENCE MEMBERS, AND WHEN EACH IS POPULATED:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Reason"/> — always present; one of the five documented
///     <see cref="WorkerAssignmentRecordingFailureReason"/> values.</description></item>
///   <item><description><see cref="StoreStatus"/> — populated ONLY for
///     <see cref="WorkerAssignmentRecordingFailureReason.Conflict"/> and
///     <see cref="WorkerAssignmentRecordingFailureReason.Indeterminate"/> (the two outcomes the
///     database itself reported). It is <c>null</c> for every refusal this type decided
///     itself.</description></item>
///   <item><description><see cref="Exception.InnerException"/> — the EXACT caught exception for a
///     capture/preflight, context-validation, store-acquisition or store-read failure, and the
///     store's exact <see cref="WorkerAssignmentWriteResult.WriteException"/> for an
///     <see cref="WorkerAssignmentRecordingFailureReason.Indeterminate"/> outcome. It is
///     <c>null</c> for <see cref="WorkerAssignmentRecordingFailureReason.MissingPublisher"/>,
///     <see cref="WorkerAssignmentRecordingFailureReason.Conflict"/> and the SIMPLE context
///     mismatches this type decides by its own comparison: no provider cause is ever invented for
///     a refusal the provider never made.</description></item>
/// </list>
/// <para>
/// THE PAIRING IS STRUCTURAL. The only constructor is PRIVATE and validating, so an inconsistent
/// combination — a <c>MissingPublisher</c> carrying an invented cause, a <c>Conflict</c> carrying a
/// store status it cannot have, or an <c>Indeterminate</c> with no evidence — cannot be constructed
/// at all. Only the factories below exist.
/// </para>
/// <para>
/// IT DOES NOT REPLACE THE EXISTING TEARDOWN SEMANTICS. A REAL stream/caller cancellation
/// (<see cref="OperationCanceledException"/>) and every POST-RECORD mapping or channel-write failure
/// propagate UNCHANGED; they are never re-labelled as this failure type. A caller that catches this
/// type is expected to emit an actionable warning and retain the pinned worker, the active task and
/// the busy state — this exception carries no instruction to unwind anything.
/// </para>
/// </summary>
internal sealed class WorkerAssignmentRecordingException : Exception
{
    /// <summary>The refusal family that stopped the recording.</summary>
    internal WorkerAssignmentRecordingFailureReason Reason { get; }

    /// <summary>
    /// The store's reported outcome, populated ONLY for
    /// <see cref="WorkerAssignmentRecordingFailureReason.Conflict"/> and
    /// <see cref="WorkerAssignmentRecordingFailureReason.Indeterminate"/>; <c>null</c> otherwise.
    /// </summary>
    internal WorkerAssignmentWriteStatus? StoreStatus { get; }

    /// <summary>
    /// THE ONE (private) CONSTRUCTOR, which enforces the documented pairing so a future call site
    /// cannot quietly produce a self-contradictory failure.
    /// </summary>
    /// <param name="reason">A defined <see cref="WorkerAssignmentRecordingFailureReason"/>.</param>
    /// <param name="message">A non-blank diagnostic message naming the refusal.</param>
    /// <param name="storeStatus">The store outcome; non-null exactly for Conflict/Indeterminate.</param>
    /// <param name="innerException">The retained evidence, or <c>null</c> where the contract forbids it.</param>
    /// <exception cref="ArgumentException">The reason is undefined, the message is blank, or the
    /// status/inner-exception pairing violates the documented contract.</exception>
    private WorkerAssignmentRecordingException(
        WorkerAssignmentRecordingFailureReason reason,
        string message,
        WorkerAssignmentWriteStatus? storeStatus,
        Exception? innerException)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentException(
                $"Worker assignment recording failure has undefined reason value {(int)reason}.", nameof(reason));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException(
                "Worker assignment recording failure message must be a non-blank string.", nameof(message));
        }

        var statusExpected = reason is WorkerAssignmentRecordingFailureReason.Conflict
            or WorkerAssignmentRecordingFailureReason.Indeterminate;

        if (statusExpected)
        {
            if (storeStatus is null)
            {
                throw new ArgumentException(
                    $"Worker assignment recording failure '{reason}' requires the store's reported status.",
                    nameof(storeStatus));
            }

            if (!Enum.IsDefined(storeStatus.Value))
            {
                throw new ArgumentException(
                    $"Worker assignment recording failure has undefined store status value " +
                    $"{(int)storeStatus.Value}.", nameof(storeStatus));
            }
        }
        else if (storeStatus is not null)
        {
            throw new ArgumentException(
                $"Worker assignment recording failure '{reason}' is a pre-store refusal and cannot carry a " +
                "store status.", nameof(storeStatus));
        }

        switch (reason)
        {
            case WorkerAssignmentRecordingFailureReason.MissingPublisher:
            case WorkerAssignmentRecordingFailureReason.Conflict:
                if (innerException is not null)
                {
                    // NO INVENTED PROVIDER CAUSE: neither refusal was made by the provider.
                    throw new ArgumentException(
                        $"Worker assignment recording failure '{reason}' must not carry an inner exception.",
                        nameof(innerException));
                }

                break;

            case WorkerAssignmentRecordingFailureReason.StoreError:
            case WorkerAssignmentRecordingFailureReason.Indeterminate:
                if (innerException is null)
                {
                    // Both retain the exact caught evidence; without it the refusal is unaccountable.
                    throw new ArgumentException(
                        $"Worker assignment recording failure '{reason}' requires the exact caught exception.",
                        nameof(innerException));
                }

                break;
        }

        Reason = reason;
        StoreStatus = storeStatus;
    }

    /// <summary>
    /// THE FAIL-CLOSED DISPOSITION for a caller that could not resolve the mandatory publisher. The
    /// assignment is neither recorded nor published, no fallback write is attempted, and there is no
    /// provider cause to report.
    /// </summary>
    /// <returns>The pre-formed <see cref="WorkerAssignmentRecordingFailureReason.MissingPublisher"/>
    /// failure.</returns>
    internal static WorkerAssignmentRecordingException MissingPublisher() =>
        new(
            WorkerAssignmentRecordingFailureReason.MissingPublisher,
            "WorkerAssignmentPublisher: missing-publisher — no worker assignment publisher is configured, so " +
            "the assignment was not recorded and was not published; no fallback write was attempted.",
            storeStatus: null,
            innerException: null);

    /// <summary>
    /// A delivery that does not match its own ownership (route, pointer, slot, role, pinned worker
    /// instance, or context validation), decided by THIS type's own comparison.
    /// </summary>
    /// <param name="detail">A SHORT diagnostic detail naming what did not match.</param>
    /// <returns>The refusal, with no inner exception.</returns>
    internal static WorkerAssignmentRecordingException InvalidContext(string detail) =>
        new(
            WorkerAssignmentRecordingFailureReason.InvalidContext,
            $"WorkerAssignmentPublisher: invalid-context — {detail}; the assignment was not recorded and " +
            "was not published.",
            storeStatus: null,
            innerException: null);

    /// <summary>
    /// A delivery refused by a validation step that itself threw (the capture/preflight or the
    /// context's own constructor), retaining that exact exception as the evidence.
    /// </summary>
    /// <param name="detail">A SHORT diagnostic detail naming the failed step.</param>
    /// <param name="cause">The EXACT caught exception.</param>
    /// <returns>The refusal, carrying the caught exception.</returns>
    internal static WorkerAssignmentRecordingException InvalidContext(string detail, Exception cause) =>
        new(
            WorkerAssignmentRecordingFailureReason.InvalidContext,
            $"WorkerAssignmentPublisher: invalid-context — {detail}; the assignment was not recorded and " +
            "was not published.",
            storeStatus: null,
            innerException: cause);

    /// <summary>
    /// The recording call itself threw (store validation, context acquisition, transaction begin or
    /// the duplicate readback), retaining that exact exception as the evidence.
    /// </summary>
    /// <param name="detail">A SHORT diagnostic detail naming the thrown step.</param>
    /// <param name="cause">The EXACT caught exception.</param>
    /// <returns>The refusal, carrying the caught exception.</returns>
    internal static WorkerAssignmentRecordingException StoreError(string detail, Exception cause) =>
        new(
            WorkerAssignmentRecordingFailureReason.StoreError,
            $"WorkerAssignmentPublisher: store-error — {detail}; the assignment was not published.",
            storeStatus: null,
            innerException: cause);

    /// <summary>
    /// The store refused to rebind: a DIFFERENT context is already recorded for the delivered task
    /// id. Publication is prohibited and there is no provider cause to report.
    /// </summary>
    /// <param name="detail">A SHORT diagnostic detail naming the conflicting task.</param>
    /// <returns>The refusal, carrying the store's <see cref="WorkerAssignmentWriteStatus.Conflict"/>
    /// status and no inner exception.</returns>
    internal static WorkerAssignmentRecordingException Conflict(string detail) =>
        new(
            WorkerAssignmentRecordingFailureReason.Conflict,
            $"WorkerAssignmentPublisher: conflict — {detail}; the existing record was left unchanged and no " +
            "assignment was published.",
            storeStatus: WorkerAssignmentWriteStatus.Conflict,
            innerException: null);

    /// <summary>
    /// The write did not confirm, retaining the store's EXACT write exception. Nothing is claimed
    /// about rollback or about whether a row now exists, and publication is prohibited.
    /// </summary>
    /// <param name="detail">A SHORT diagnostic detail naming the unconfirmed write.</param>
    /// <param name="writeException">The store's exact <see cref="WorkerAssignmentWriteResult.WriteException"/>.</param>
    /// <returns>The refusal, carrying the store's
    /// <see cref="WorkerAssignmentWriteStatus.Indeterminate"/> status and the exact evidence.</returns>
    internal static WorkerAssignmentRecordingException Indeterminate(string detail, Exception writeException) =>
        new(
            WorkerAssignmentRecordingFailureReason.Indeterminate,
            $"WorkerAssignmentPublisher: indeterminate — {detail}; nothing is inferred about the row's " +
            "presence and no assignment was published.",
            storeStatus: WorkerAssignmentWriteStatus.Indeterminate,
            innerException: writeException);
}
