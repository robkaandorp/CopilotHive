using CopilotHive.Persistence;

namespace CopilotHive.Services;

/// <summary>
/// The NARROW, INJECTABLE FACE of the completion-receipt recorder.
/// <para>
/// IT EXISTS SO A CALLER CAN DEPEND ON THE CAPABILITY rather than the concrete recorder — which is
/// what lets a fixture substitute its own narrow implementation for injection. It deliberately
/// carries ONE SYNCHRONOUS operation and no state: the caller supplies the pinned worker id, the
/// VALIDATED active <see cref="WorkTask"/> and the already-mapped domain
/// <see cref="TaskResult"/>.
/// </para>
/// <para>
/// A SUCCESSFUL RETURN MEANS CONFIRMED EVIDENCE RETENTION ONLY. It is not durable worker
/// authorization, it is not an acknowledgement, it is not a replay permission and it does not mean
/// the completion was processed or that a phase advanced. It says exactly one thing: the completion
/// receipt for this validated task was retained (or was already retained identically) in durable
/// storage.
/// </para>
/// <para>
/// PUBLIC BY NECESSITY, NARROW BY DESIGN: <see cref="HiveOrchestratorService"/> is a PUBLIC type, so
/// an appended constructor parameter cannot be an internal type (C# forbids the inconsistent
/// accessibility). This interface is the widest thing that parameter may name, and it exposes
/// nothing but the single record operation — the concrete recorder, the recording-failure type and
/// both stores all remain internal.
/// </para>
/// <para>
/// THE CONCRETE RECORDER IS MANDATORY FOR INCOMING COMPLETIONS. <see cref="HiveOrchestratorService"/>
/// fails closed (see <see cref="WorkerCompletionRecordingException.MissingRecorder"/>) when it cannot
/// resolve one, rather than silently taking the old unrecorded path.
/// </para>
/// </summary>
public interface IWorkerCompletionRecorder
{
    /// <summary>
    /// Retains the completion receipt for a completion this transport has already validated:
    /// loads the STORED assignment context for the task id, proves that context agrees ordinally
    /// with the pinned worker and the active task, and records the receipt EXACTLY ONCE through the
    /// existing insert-once store.
    /// </summary>
    /// <param name="workerId">The pinned worker id the completion was delivered by.</param>
    /// <param name="task">The VALIDATED active <see cref="WorkTask"/> the completion belongs to.</param>
    /// <param name="result">
    /// The already-mapped domain result — carrying the model the transport selected. The stored
    /// assignment's own model is never consulted, compared or substituted here.
    /// </param>
    /// <exception cref="WorkerCompletionRecordingException">
    /// Any refusal: <see cref="WorkerCompletionRecordingFailureReason.InvalidContext"/> (a missing or
    /// mismatched stored context, or an unrepresentable receipt),
    /// <see cref="WorkerCompletionRecordingFailureReason.StoreError"/> (a read, codec or query
    /// failure), <see cref="WorkerCompletionRecordingFailureReason.Conflict"/> or
    /// <see cref="WorkerCompletionRecordingFailureReason.Indeterminate"/>. Nothing is retained for
    /// any of them.
    /// </exception>
    void Record(string workerId, WorkTask task, TaskResult result);
}

/// <summary>
/// THE COMPLETION-RECEIPT RECORDER — the single production path that retains the durable evidence of
/// one RETURNED worker completion, from the STORED assignment context.
/// <para>
/// WHAT A RETAINED RECEIPT MEANS, HONESTLY, AND WHAT IT DOES NOT. It means the completion's evidence
/// was written to durable storage for a task whose recorded assignment context agreed with the
/// delivering worker and the active task. It does NOT mean the completion was processed, that the
/// pipeline advanced, that the worker's transport ownership was released, that the delivery was
/// acknowledged, or that a later identical message may be replayed.
/// </para>
/// <para>
/// IT REUSES THE EXISTING STORES AND INVENTS NOTHING:
/// <see cref="WorkerAssignmentContextStore.Load"/> for the recorded assignment context and
/// <see cref="CompletionReceiptStore.InsertOnce"/> for the receipt. There is no new SQL, no second
/// codec, no explicit transaction, no retry and no readback: a write outcome that did not confirm is
/// reported exactly as the store reported it.
/// </para>
/// <para>
/// IDENTITY COMES FROM THE STORED CONTEXT, NEVER FROM TEXT. The receipt's goal, worker, role and the
/// FULL <see cref="WorkSlot"/> are the STORED context's own values; nothing is reconstructed from the
/// task id's text, from <see cref="WorkTask.Iteration"/>, from the pipeline's current phase or from a
/// guessed default. The task id is used only as the OPAQUE lookup key and as an ordinal agreement
/// check.
/// </para>
/// <para>
/// NO PIPELINE PRECONDITION. The recorder holds no pipeline manager and consults no pipeline: LOGICAL
/// cancellation removes a pipeline while valid transport ownership remains, so requiring a live
/// pipeline here would discard the very evidence this slice exists to retain. Recorded assignment
/// context is HISTORICAL INTENT, not new authorization — the existing transport guards and the
/// downstream <c>TaskCompletionService</c> guards still apply unchanged.
/// </para>
/// <para>
/// THE MODEL IS THE TRANSPORT'S SELECTION, PRESERVED VERBATIM. The receipt carries the mapped result
/// as handed over — including an empty or whitespace model — and never rewrites or rejects it because
/// the stored assignment's <see cref="WorkerAssignmentContext.Model"/> differs.
/// </para>
/// <para>
/// SCOPE, HONESTLY. This type retains evidence. It performs no acknowledgement, no replay, no
/// processed marker, no downstream retry, no compensation and no deletion — a receipt that was
/// stored stays stored even when the caller's subsequent release refuses.
/// </para>
/// </summary>
internal sealed class WorkerCompletionRecorder : IWorkerCompletionRecorder
{
    private readonly WorkerAssignmentContextStore _assignmentStore;
    private readonly CompletionReceiptStore _receiptStore;

    /// <summary>
    /// Initialises a new <see cref="WorkerCompletionRecorder"/> over the two EXISTING stores.
    /// </summary>
    /// <param name="assignmentStore">The insert-once assignment-context store the recorded context is loaded from.</param>
    /// <param name="receiptStore">The insert-once completion-receipt store the receipt is retained in.</param>
    /// <exception cref="ArgumentNullException">Any dependency is <c>null</c>.</exception>
    internal WorkerCompletionRecorder(
        WorkerAssignmentContextStore assignmentStore,
        CompletionReceiptStore receiptStore)
    {
        ArgumentNullException.ThrowIfNull(assignmentStore);
        ArgumentNullException.ThrowIfNull(receiptStore);

        _assignmentStore = assignmentStore;
        _receiptStore = receiptStore;
    }

    /// <inheritdoc />
    public void Record(string workerId, WorkTask task, TaskResult result)
    {
        // THE NULL GUARDS are caller-bug refusals, not recording outcomes: a null task or result names
        // no completion at all, so no receipt is invented for it.
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(result);

        // ── (1) THE COMPLETING RESULT MUST BE THE ACTIVE TASK, compared ORDINALLY. ──
        if (!string.Equals(result.TaskId, task.TaskId, StringComparison.Ordinal))
        {
            throw WorkerCompletionRecordingException.InvalidContext(
                $"the completing result names task '{result.TaskId}' but the active task is " +
                $"'{task.TaskId}'");
        }

        // ── (2) THE STORED ASSIGNMENT CONTEXT, looked up by that SAME OPAQUE task id. A store read
        //        failure (its argument validation, its context acquisition, its read query or its
        //        materialization) is a STORE error and retains the EXACT caught exception. ──
        WorkerAssignmentContextReadResult? stored;
        try
        {
            stored = _assignmentStore.Load(task.TaskId);
        }
        catch (Exception storeFailure)
        {
            throw WorkerCompletionRecordingException.StoreError(
                $"reading the recorded assignment context for task '{task.TaskId}' threw " +
                $"({storeFailure.GetType().Name})",
                storeFailure);
        }

        // ── (3) A MISSING CONTEXT IS A REFUSAL. Nothing is inserted and no context is synthesized:
        //        an unrecorded assignment is exactly what cannot be evidenced. ──
        if (stored is null)
        {
            throw WorkerCompletionRecordingException.InvalidContext(
                $"no assignment context is recorded for task '{task.TaskId}'");
        }

        var context = stored.Context;

        // ── (4) THE ORDINAL AGREEMENT between the STORED context, the pinned worker and the active
        //        task. Every comparison is case-sensitive; a disagreement is a refusal, never a
        //        rebind and never a repair. ──
        if (!string.Equals(context.Slot.TaskId, task.TaskId, StringComparison.Ordinal))
        {
            throw WorkerCompletionRecordingException.InvalidContext(
                $"the stored assignment context for task '{task.TaskId}' carries slot task id " +
                $"'{context.Slot.TaskId}'");
        }

        if (!string.Equals(context.WorkerId, workerId, StringComparison.Ordinal))
        {
            throw WorkerCompletionRecordingException.InvalidContext(
                $"the stored assignment context for task '{task.TaskId}' names worker " +
                $"'{context.WorkerId}' but the completing worker is '{workerId ?? "(none)"}'");
        }

        if (!string.Equals(context.GoalId, task.GoalId, StringComparison.Ordinal))
        {
            throw WorkerCompletionRecordingException.InvalidContext(
                $"the stored assignment context for task '{task.TaskId}' names goal " +
                $"'{context.GoalId}' but the active task names goal '{task.GoalId}'");
        }

        if (context.Role != task.Role)
        {
            throw WorkerCompletionRecordingException.InvalidContext(
                $"the stored assignment context for task '{task.TaskId}' records role " +
                $"'{context.Role}' but the active task carries role '{task.Role}'");
        }

        // ── (5) THE RECEIPT, built from the STORED goal/worker/role and the FULL stored slot, plus
        //        the mapped result EXACTLY as handed over. The constructor is the single validation
        //        authority; a refusal there is retained with its exact exception. ──
        CompletionReceipt receipt;
        try
        {
            receipt = new CompletionReceipt(
                context.GoalId,
                context.WorkerId,
                context.Role,
                context.Slot,
                result);
        }
        catch (Exception receiptValidation)
        {
            throw WorkerCompletionRecordingException.InvalidContext(
                $"the completion receipt for task '{task.TaskId}' failed its own validation " +
                $"({receiptValidation.GetType().Name})",
                receiptValidation);
        }

        // ── (6) THE ONE INSERT. Called EXACTLY ONCE, ever. Every throw the store produces — its
        //        argument guard, the codec, the context acquisition, the write and the duplicate
        //        readback — is a genuine STORE failure and retains its EXACT exception. The store
        //        reports a throwing or unconfirmed write as
        //        CompletionReceiptWriteStatus.Indeterminate instead, handled below. ──
        CompletionReceiptWriteResult write;
        try
        {
            write = _receiptStore.InsertOnce(receipt);
        }
        catch (Exception storeFailure)
        {
            throw WorkerCompletionRecordingException.StoreError(
                $"retaining the completion receipt for task '{task.TaskId}' threw " +
                $"({storeFailure.GetType().Name})",
                storeFailure);
        }

        switch (write.Status)
        {
            case CompletionReceiptWriteStatus.Stored:
            case CompletionReceiptWriteStatus.AlreadyStored:
                // BOTH permit THIS invocation to proceed. AlreadyStored is ONLY identical-evidence
                // about a row that already exists: it says the stored bytes are this same receipt,
                // NOT that the completion was processed — so it is never treated as such.
                return;

            case CompletionReceiptWriteStatus.Conflict:
                // A DIFFERENT receipt is already durably retained for this task id. Nothing was
                // changed and nothing is retained; no provider cause is invented.
                throw WorkerCompletionRecordingException.Conflict(
                    $"a different completion receipt is already retained for task '{task.TaskId}'");

            case CompletionReceiptWriteStatus.Indeterminate:
                // The write did not confirm. No claim is made about the row's presence and the
                // store's EXACT write-uncertainty object is carried as the evidence. No retry and no
                // readback happen here — the caller owns any retry.
                throw WorkerCompletionRecordingException.Indeterminate(
                    $"the completion-receipt write for task '{task.TaskId}' did not confirm",
                    write.WriteException!);

            default:
                // NO SILENT FALLBACK: an undefined outcome is a contract violation and must never be
                // mistaken for a retained receipt.
                throw new InvalidOperationException(
                    $"WorkerCompletionRecorder: unhandled CompletionReceiptWriteStatus " +
                    $"'{write.Status}'.");
        }
    }
}

/// <summary>
/// The explicit reasons a completion receipt could NOT be retained. The set is deliberately small and
/// flat — one reason per refusal family, no taxonomy framework.
/// </summary>
internal enum WorkerCompletionRecordingFailureReason
{
    /// <summary>
    /// NO RECORDER IS AVAILABLE. The caller could not resolve the mandatory
    /// <see cref="IWorkerCompletionRecorder"/>, so no receipt was retained. This is the fail-CLOSED
    /// disposition: the old unrecorded completion path is NEVER used as a fallback, because an
    /// unretained completion is exactly what this slice exists to prevent.
    /// </summary>
    MissingRecorder,

    /// <summary>
    /// THE COMPLETION DOES NOT MATCH A RECORDED ASSIGNMENT. Covers a result/task id disagreement, a
    /// MISSING stored assignment context, a stored context whose slot task id, worker id, goal id or
    /// role disagrees with the completing task and the pinned worker, and a receipt that fails its own
    /// internal-consistency validation. No context is synthesized and no receipt is retained.
    /// </summary>
    InvalidContext,

    /// <summary>
    /// A STORE FAILURE. An assignment read, a context acquisition, a codec refusal, a query failure or
    /// the duplicate readback threw; the EXACT caught exception is the inner exception. Nothing is
    /// retained and nothing is inferred about any row.
    /// </summary>
    StoreError,

    /// <summary>
    /// A DIFFERENT receipt is already durably retained for the completing task id. The store refused
    /// to rebind and changed nothing. There is no provider cause to report — the refusal is the
    /// database's duplicate decision.
    /// </summary>
    Conflict,

    /// <summary>
    /// THE WRITE DID NOT CONFIRM (a throwing INSERT, or an affected-row count other than 0/1). No
    /// claim is made about rollback or about whether a row now exists, and the store's exact
    /// <see cref="CompletionReceiptWriteResult.WriteException"/> is the inner exception. No automatic
    /// retry or readback is performed.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// THE RECORDING-FAILURE EXCEPTION: the single explicit failure contract of the completion recorder.
/// <para>
/// THE THREE EVIDENCE MEMBERS, AND WHEN EACH IS POPULATED:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Reason"/> — always present; one of the five documented
///     <see cref="WorkerCompletionRecordingFailureReason"/> values.</description></item>
///   <item><description><see cref="StoreStatus"/> — populated ONLY for
///     <see cref="WorkerCompletionRecordingFailureReason.Conflict"/> and
///     <see cref="WorkerCompletionRecordingFailureReason.Indeterminate"/> (the two outcomes the
///     database itself reported). It is <c>null</c> for every refusal this type decided
///     itself.</description></item>
///   <item><description><see cref="Exception.InnerException"/> — the EXACT caught exception for a
///     store read/write/readback failure, a receipt-validation failure, and the store's exact
///     <see cref="CompletionReceiptWriteResult.WriteException"/> for an
///     <see cref="WorkerCompletionRecordingFailureReason.Indeterminate"/> outcome. It is <c>null</c>
///     for <see cref="WorkerCompletionRecordingFailureReason.MissingRecorder"/>,
///     <see cref="WorkerCompletionRecordingFailureReason.Conflict"/> and the SIMPLE context
///     disagreements this type decides by its own comparison: no provider cause is ever invented for
///     a refusal the provider never made.</description></item>
/// </list>
/// <para>
/// THE PAIRING IS STRUCTURAL. The only constructor is PRIVATE and validating, so an inconsistent
/// combination cannot be constructed at all. Only the factories below exist. The exception carries no
/// instruction to unwind anything: a caller that catches it is expected to emit an actionable warning
/// and retain the pinned worker, the active task and the busy state.
/// </para>
/// </summary>
internal sealed class WorkerCompletionRecordingException : Exception
{
    /// <summary>The refusal family that stopped the recording.</summary>
    internal WorkerCompletionRecordingFailureReason Reason { get; }

    /// <summary>
    /// The store's reported outcome, populated ONLY for
    /// <see cref="WorkerCompletionRecordingFailureReason.Conflict"/> and
    /// <see cref="WorkerCompletionRecordingFailureReason.Indeterminate"/>; <c>null</c> otherwise.
    /// </summary>
    internal CompletionReceiptWriteStatus? StoreStatus { get; }

    /// <summary>
    /// THE ONE (private) CONSTRUCTOR, which enforces the documented pairing so a future call site
    /// cannot quietly produce a self-contradictory failure.
    /// </summary>
    /// <param name="reason">A defined <see cref="WorkerCompletionRecordingFailureReason"/>.</param>
    /// <param name="message">A non-blank diagnostic message naming the refusal.</param>
    /// <param name="storeStatus">The store outcome; non-null exactly for Conflict/Indeterminate.</param>
    /// <param name="innerException">The retained evidence, or <c>null</c> where the contract forbids it.</param>
    private WorkerCompletionRecordingException(
        WorkerCompletionRecordingFailureReason reason,
        string message,
        CompletionReceiptWriteStatus? storeStatus,
        Exception? innerException)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentException(
                $"Worker completion recording failure has undefined reason value {(int)reason}.",
                nameof(reason));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException(
                "Worker completion recording failure message must be a non-blank string.", nameof(message));
        }

        var statusExpected = reason is WorkerCompletionRecordingFailureReason.Conflict
            or WorkerCompletionRecordingFailureReason.Indeterminate;

        if (statusExpected)
        {
            if (storeStatus is null)
            {
                throw new ArgumentException(
                    $"Worker completion recording failure '{reason}' requires the store's reported status.",
                    nameof(storeStatus));
            }

            if (!Enum.IsDefined(storeStatus.Value))
            {
                throw new ArgumentException(
                    $"Worker completion recording failure has undefined store status value " +
                    $"{(int)storeStatus.Value}.", nameof(storeStatus));
            }
        }
        else if (storeStatus is not null)
        {
            throw new ArgumentException(
                $"Worker completion recording failure '{reason}' is a pre-store refusal and cannot carry " +
                "a store status.", nameof(storeStatus));
        }

        switch (reason)
        {
            case WorkerCompletionRecordingFailureReason.MissingRecorder:
            case WorkerCompletionRecordingFailureReason.Conflict:
                if (innerException is not null)
                {
                    // NO INVENTED PROVIDER CAUSE: neither refusal was made by the provider.
                    throw new ArgumentException(
                        $"Worker completion recording failure '{reason}' must not carry an inner exception.",
                        nameof(innerException));
                }

                break;

            case WorkerCompletionRecordingFailureReason.StoreError:
            case WorkerCompletionRecordingFailureReason.Indeterminate:
                if (innerException is null)
                {
                    // Both retain the exact caught evidence; without it the refusal is unaccountable.
                    throw new ArgumentException(
                        $"Worker completion recording failure '{reason}' requires the exact caught exception.",
                        nameof(innerException));
                }

                break;
        }

        Reason = reason;
        StoreStatus = storeStatus;
    }

    /// <summary>
    /// THE FAIL-CLOSED DISPOSITION for a caller that could not resolve the mandatory recorder: no
    /// receipt is retained, the old unrecorded path is NOT taken, and there is no provider cause to
    /// report.
    /// </summary>
    /// <returns>The pre-formed <see cref="WorkerCompletionRecordingFailureReason.MissingRecorder"/> failure.</returns>
    internal static WorkerCompletionRecordingException MissingRecorder() =>
        new(
            WorkerCompletionRecordingFailureReason.MissingRecorder,
            "WorkerCompletionRecorder: missing-recorder — no worker completion recorder is configured, so " +
            "the completion receipt was not retained; no unrecorded fallback path was taken.",
            storeStatus: null,
            innerException: null);

    /// <summary>
    /// A completion that does not match a recorded assignment (a result/task disagreement, a missing
    /// context, a stored identity disagreement, or a receipt validation refusal), decided by THIS
    /// type's own comparison.
    /// </summary>
    /// <param name="detail">A SHORT diagnostic detail naming what did not match.</param>
    /// <returns>The refusal, with no inner exception.</returns>
    internal static WorkerCompletionRecordingException InvalidContext(string detail) =>
        new(
            WorkerCompletionRecordingFailureReason.InvalidContext,
            $"WorkerCompletionRecorder: invalid-context — {detail}; no completion receipt was retained.",
            storeStatus: null,
            innerException: null);

    /// <summary>
    /// A completion refused by a validation step that itself threw (the receipt's own constructor),
    /// retaining that exact exception as the evidence.
    /// </summary>
    /// <param name="detail">A SHORT diagnostic detail naming the failed step.</param>
    /// <param name="cause">The EXACT caught exception.</param>
    /// <returns>The refusal, carrying the caught exception.</returns>
    internal static WorkerCompletionRecordingException InvalidContext(string detail, Exception cause) =>
        new(
            WorkerCompletionRecordingFailureReason.InvalidContext,
            $"WorkerCompletionRecorder: invalid-context — {detail}; no completion receipt was retained.",
            storeStatus: null,
            innerException: cause);

    /// <summary>
    /// A store operation threw (a read, a context acquisition, a codec refusal or the duplicate
    /// readback), retaining that exact exception as the evidence.
    /// </summary>
    /// <param name="detail">A SHORT diagnostic detail naming the thrown step.</param>
    /// <param name="cause">The EXACT caught exception.</param>
    /// <returns>The refusal, carrying the caught exception.</returns>
    internal static WorkerCompletionRecordingException StoreError(string detail, Exception cause) =>
        new(
            WorkerCompletionRecordingFailureReason.StoreError,
            $"WorkerCompletionRecorder: store-error — {detail}; no completion receipt was retained and " +
            "nothing is inferred about the row's presence.",
            storeStatus: null,
            innerException: cause);

    /// <summary>
    /// The store refused to rebind: a DIFFERENT receipt is already retained for the completing task id.
    /// There is no provider cause to report.
    /// </summary>
    /// <param name="detail">A SHORT diagnostic detail naming the conflicting task.</param>
    /// <returns>The refusal, carrying the store's <see cref="CompletionReceiptWriteStatus.Conflict"/>
    /// status and no inner exception.</returns>
    internal static WorkerCompletionRecordingException Conflict(string detail) =>
        new(
            WorkerCompletionRecordingFailureReason.Conflict,
            $"WorkerCompletionRecorder: conflict — {detail}; the existing receipt was left unchanged.",
            storeStatus: CompletionReceiptWriteStatus.Conflict,
            innerException: null);

    /// <summary>
    /// The write did not confirm, retaining the store's EXACT write exception. Nothing is claimed about
    /// rollback or about whether a row now exists, and no retry or readback is performed.
    /// </summary>
    /// <param name="detail">A SHORT diagnostic detail naming the unconfirmed write.</param>
    /// <param name="writeException">The store's exact <see cref="CompletionReceiptWriteResult.WriteException"/>.</param>
    /// <returns>The refusal, carrying the store's
    /// <see cref="CompletionReceiptWriteStatus.Indeterminate"/> status and the exact evidence.</returns>
    internal static WorkerCompletionRecordingException Indeterminate(string detail, Exception writeException) =>
        new(
            WorkerCompletionRecordingFailureReason.Indeterminate,
            $"WorkerCompletionRecorder: indeterminate — {detail}; nothing is inferred about the row's " +
            "presence.",
            storeStatus: CompletionReceiptWriteStatus.Indeterminate,
            innerException: writeException);
}
