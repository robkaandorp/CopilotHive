using CopilotHive.Persistence;

using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// The NARROW, INJECTABLE FACE of the restored-attempt adopter: it exists so
/// <see cref="HiveOrchestratorService"/> — a PUBLIC type — can take the capability as a constructor
/// parameter without naming an internal type (C# forbids the inconsistent accessibility).
/// <para>
/// PUBLIC BY NECESSITY, NARROW BY DESIGN: this interface carries ONE operation and no state — the
/// caller supplies the registering worker id, the task id the caller claims, and its own
/// registration negotiation facts. The concrete adopter, the assignment store it reads and the
/// registry/queue evidence it composes all remain internal.
/// </para>
/// <para>
/// IT IS OPTIONAL FOR REGISTRATION: with no adopter configured nothing is adopted (the caller
/// registers exactly as it does today), and the answer is
/// <see cref="RestoredAttemptAdoptionOutcome.NoAdopter"/>.
/// </para>
/// </summary>
public interface IRestoredAttemptAdopter
{
    /// <summary>
    /// ATTEMPTS TO ADOPT the held restored attempt named by <paramref name="taskId"/> on behalf of
    /// the registering worker <paramref name="workerId"/>.
    /// </summary>
    /// <remarks>
    /// This is FAIL-CLOSED: every precondition must hold before anything is committed, and any
    /// refusal returns a result — never an exception — so the caller can fall back to ordinary
    /// registration with <see cref="RestoredAttemptAdoptionResult.Adopted"/> <c>false</c>. See
    /// <see cref="RestoredAttemptAdopter"/> for the exhaustive precondition list and the commit
    /// order.
    /// </remarks>
    /// <param name="workerId">The caller-supplied worker id; must be non-blank.</param>
    /// <param name="taskId">The caller-supplied task id; must be non-blank.</param>
    /// <param name="capabilities">The capabilities the registering worker advertised.</param>
    /// <param name="requestCompletionReceiptAck">Whether this registration requested durable completion-receipt acknowledgement.</param>
    /// <param name="completionReceiptAckEnabled">The orchestrator's enablement decision for this registration.</param>
    /// <returns>The adoption outcome, with the registered worker and task on the successful one.</returns>
    /// <exception cref="ArgumentException"><paramref name="workerId"/> or <paramref name="taskId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is <c>null</c>.</exception>
    RestoredAttemptAdoptionResult TryAdoptRestoredAttempt(
        string workerId,
        string taskId,
        string[] capabilities,
        bool requestCompletionReceiptAck,
        bool completionReceiptAckEnabled);
}

/// <summary>
/// The outcome of <see cref="IRestoredAttemptAdopter.TryAdoptRestoredAttempt"/>, exhaustively.
/// Exactly one value describes one invocation.
/// </summary>
public enum RestoredAttemptAdoptionOutcome
{
    /// <summary>The attempt was adopted and its worker is registered BUSY with it.</summary>
    Adopted,
    /// <summary>A precondition did not hold; <see cref="RestoredAttemptAdoptionResult.Refusal"/> names the check.</summary>
    Refused,
    /// <summary>
    /// The hold was already gone by the time the adoption was attempted (the release sweep won the
    /// race): nothing was adopted and nothing was mutated.
    /// </summary>
    HoldAlreadyReleased,
    /// <summary>
    /// An active queue entry already existed for the task, so the busy registration was refused;
    /// that entry was left untouched and the attempt was put back under hold.
    /// </summary>
    ActiveQueueEntryExists,
    /// <summary>
    /// A worker was already registered under the id, so the busy registration was refused; the
    /// registered instance was left untouched and the attempt was put back under hold.
    /// </summary>
    DuplicateWorkerId,
    /// <summary>
    /// The commit threw after the attempt had been adopted. The attempt was put back under hold and
    /// the ORIGINAL exception is carried in
    /// <see cref="RestoredAttemptAdoptionResult.CommitException"/>.
    /// </summary>
    CommitFailed,
    /// <summary>
    /// No adopter was configured for this caller: nothing was read and nothing was adopted. The
    /// caller registers exactly as it does today.
    /// </summary>
    NoAdopter,
}

/// <summary>
/// WHICH check refused an adoption — <see cref="RestoredAttemptAdoptionOutcome.Refused"/>. The
/// names are the check names, so a caller's warning can name the check it actually evaluated.
/// </summary>
public enum RestoredAttemptAdoptionRefusal
{
    /// <summary>No check refused: the result is not a refusal.</summary>
    None,
    /// <summary>No pipeline is registered for the claimed task id.</summary>
    NoPipeline,
    /// <summary>The pipeline is not (or is no longer) under the restored active-attempt hold.</summary>
    NotHeld,
    /// <summary>The pipeline's active-task pointer does not name the claimed task id.</summary>
    PointerMismatch,
    /// <summary>
    /// The restore-time registry classification is not <c>Restored</c> — the registry evidence is
    /// MISSING or was REJECTED (missing, decode-rejected or domain-rejected) and therefore cannot
    /// corroborate the claimed attempt.
    /// </summary>
    RegistryEvidenceUntrusted,
    /// <summary>The restored active-pointer classification is not <c>ActiveSlotPending</c>.</summary>
    SlotNotPending,
    /// <summary>No recorded assignment context exists for the claimed task id.</summary>
    NoAssignmentContext,
    /// <summary>The recorded assignment context names a different worker.</summary>
    WorkerMismatch,
    /// <summary>The recorded assignment context names a different goal than the pipeline's.</summary>
    GoalMismatch,
    /// <summary>
    /// The recorded assignment context's slot does not agree with the restored registry's slot for
    /// the task (task id, iteration, phase, occurrence or attempt).
    /// </summary>
    SlotMismatch,
    /// <summary>
    /// The pipeline phase, the state-machine phase and the recorded slot's phase do not all agree,
    /// or the phase is one with no worker (Planning, Done or Failed).
    /// </summary>
    PhaseMismatch,
    /// <summary>
    /// A read of the recorded assignment context or of the restored registry failed. The failure
    /// is NOT logged by the adopter: it is carried in
    /// <see cref="RestoredAttemptAdoptionResult.ReadException"/> so the caller emits the ONE
    /// warning naming this check (with a sanitized cause), and it never escapes as an exception.
    /// </summary>
    ReadFailed,

    /// <summary>
    /// Every read-only precondition held, but by the SYNCHRONIZED commit the LIVE attempt was no
    /// longer the recorded one: a cancellation or terminal transition moved the phase, the pointer
    /// was cleared or moved, or the slot was admitted (Claimed) or retired (Abandoned) in between.
    /// Nothing was adopted, nothing was mutated and the hold is untouched — the invalidating
    /// transition won, exactly as it would have if it had landed before the precondition reads.
    /// </summary>
    AttemptNoLongerValid,

    /// <summary>
    /// Every read-only precondition and the live evidence held, but by the SYNCHRONIZED commit the
    /// claimed task no longer routed to this pipeline: it was removed from the manager (for example
    /// by a user retry clearing the goal's runtime state, which performs no terminal transition and
    /// so invalidates none of the pointer/phase/slot evidence). The route is proven inside the same
    /// pipeline-lock span as the CAS, immediately before it. Nothing was adopted, nothing was mutated,
    /// the hold is untouched, and no busy worker is published for an attempt whose completion could
    /// never reach its pipeline.
    /// </summary>
    RouteNoLongerValid,
}

/// <summary>
/// The result of one adoption attempt. It is a RESULT, NEVER AUTHORIZATION: it reports what this
/// invocation reached, and <see cref="Adopted"/> says exactly one thing — that the exact attempt
/// named by the claimed task id is now owned by a freshly registered BUSY worker instance.
/// </summary>
public sealed record RestoredAttemptAdoptionResult
{
    /// <summary>Which disposition this invocation reached.</summary>
    public required RestoredAttemptAdoptionOutcome Outcome { get; init; }

    /// <summary>
    /// Which check refused, for <see cref="RestoredAttemptAdoptionOutcome.Refused"/>; otherwise
    /// <see cref="RestoredAttemptAdoptionRefusal.None"/>.
    /// </summary>
    public RestoredAttemptAdoptionRefusal Refusal { get; init; } = RestoredAttemptAdoptionRefusal.None;

    /// <summary>
    /// <c>true</c> ONLY for <see cref="RestoredAttemptAdoptionOutcome.Adopted"/>. Every other
    /// outcome means "register ordinarily", not "try something else".
    /// </summary>
    public bool Adopted => Outcome == RestoredAttemptAdoptionOutcome.Adopted;

    /// <summary>
    /// The instance registered for a successful adoption — the exact instance the pool now holds —
    /// or <c>null</c> for every other outcome.
    /// </summary>
    public ConnectedWorker? Worker { get; init; }

    /// <summary>
    /// The task built from the recorded assignment context and registered as active for a successful
    /// adoption, or <c>null</c> for every other outcome.
    /// </summary>
    public WorkTask? Task { get; init; }

    /// <summary>
    /// The EXACT exception carried for <see cref="RestoredAttemptAdoptionOutcome.CommitFailed"/>,
    /// or <c>null</c> for every other outcome.
    /// </summary>
    public Exception? CommitException { get; init; }

    /// <summary>
    /// The EXACT read failure carried for a <see cref="RestoredAttemptAdoptionRefusal.ReadFailed"/>
    /// refusal, or <c>null</c> for every other outcome. The adopter deliberately does NOT log it:
    /// the caller owns the single refusal warning and renders this cause (sanitized) into it, so the
    /// whole production path emits exactly ONE warning for the refusal.
    /// </summary>
    public Exception? ReadException { get; init; }
}

/// <summary>
/// THE RESTORED-ATTEMPT ADOPTER: the fail-closed decision and the atomic commit by which a
/// reconnecting worker reclaims the exact attempt a restored pipeline still holds.
/// </summary>
/// <remarks>
/// <para>
/// THE TRUST MODEL, EXPLICITLY. Worker gRPC is ANONYMOUS on the private Docker network:
/// <see cref="HiveOrchestratorService"/> is mapped with <c>AllowAnonymous()</c> and
/// <c>Register</c> accepts a caller-supplied worker id, so any caller on the worker network can
/// already register under any id, receive assignments and submit completions. Worker identity is
/// trusted on that network, and NO WORKER AUTHENTICATION IS ADDED HERE — this slice deliberately
/// does not widen or narrow the transport boundary.
/// </para>
/// <para>
/// WHAT ADOPTION THEREFORE REQUIRES. Because the id is caller-supplied, adoption never trusts it:
/// the SAME caller-supplied worker id must equal the id RECORDED at assignment, together with the
/// claimed task id naming the pipeline's active attempt, a pipeline that is still HELD, a restored
/// registry whose evidence was actually hydrated, a PENDING active slot, a recovered assignment
/// context whose slot and goal agree with the pipeline's own restored registry, and agreement
/// between the pipeline phase, the state-machine phase and the slot's worker phase. Anything less
/// is REFUSED, and a refusal registers the worker ordinarily (idle) with <c>adopted_task = false</c>.
/// </para>
/// <para>
/// THE PRECONDITIONS, IN ORDER — read-only, ALL must hold, each with its own
/// <see cref="RestoredAttemptAdoptionRefusal"/>:
/// <list type="number">
///   <item><description><see cref="GoalPipelineManager.GetByTaskId"/> is non-null → else
///     <see cref="RestoredAttemptAdoptionRefusal.NoPipeline"/>;</description></item>
///   <item><description><see cref="GoalPipeline.IsRestoredActiveAttemptHold"/> → else
///     <see cref="RestoredAttemptAdoptionRefusal.NotHeld"/>;</description></item>
///   <item><description><see cref="GoalPipeline.ActiveTaskId"/> equals the claimed task id
///     ordinally → else <see cref="RestoredAttemptAdoptionRefusal.PointerMismatch"/>;</description></item>
///   <item><description><see cref="GoalPipeline.RestoredRegistryClassification"/> is
///     <see cref="RestoredRegistryOutcome.Restored"/> → else
///     <see cref="RestoredAttemptAdoptionRefusal.RegistryEvidenceUntrusted"/>. Missing, decode-rejected
///     and domain-rejected registry evidence is NEVER adopted;</description></item>
///   <item><description><see cref="GoalPipeline.RestoredActivePointerClassification"/> is
///     <see cref="RestoredActivePointerOutcome.ActiveSlotPending"/> → else
///     <see cref="RestoredAttemptAdoptionRefusal.SlotNotPending"/>;</description></item>
///   <item><description><see cref="WorkerAssignmentContextStore.Load"/> returns a context → else
///     <see cref="RestoredAttemptAdoptionRefusal.NoAssignmentContext"/>; its
///     <see cref="WorkerAssignmentContext.WorkerId"/> equals the registering id → else
///     <see cref="RestoredAttemptAdoptionRefusal.WorkerMismatch"/>; its
///     <see cref="WorkerAssignmentContext.GoalId"/> equals the pipeline's goal → else
///     <see cref="RestoredAttemptAdoptionRefusal.GoalMismatch"/>; its
///     <see cref="WorkerAssignmentContext.Slot"/> agrees — task id, iteration, phase, occurrence and
///     attempt — with the restored registry's slot for the task, read through
///     <see cref="GoalPipeline.CaptureRegistry"/> → else
///     <see cref="RestoredAttemptAdoptionRefusal.SlotMismatch"/>;</description></item>
///   <item><description>PHASE CONSISTENCY: <see cref="GoalPipeline.Phase"/> AND
///     <see cref="PipelineStateMachine.Phase"/> both equal the context slot's phase, and neither is
///     Planning, Done or Failed → else <see cref="RestoredAttemptAdoptionRefusal.PhaseMismatch"/>.</description></item>
/// </list>
/// Any exception thrown by those reads is reported as
/// <see cref="RestoredAttemptAdoptionRefusal.ReadFailed"/> carrying the exact exception in
/// <see cref="RestoredAttemptAdoptionResult.ReadException"/> — it never escapes and never adopts.
/// THE ADOPTER LOGS NO REFUSAL: the caller (<see cref="HiveOrchestratorService"/>'s Register) owns
/// the single warning that names the check, so the production path emits exactly ONE warning per
/// declined claim.
/// </para>
/// <para>
/// THE COMMIT ORDER, once every precondition holds:
/// <list type="letter">
///   <item><description><see cref="GoalPipeline.TryAdoptRestoredActiveAttemptIfStillValid"/> FIRST
///     — ONE synchronized region on the pipeline that REVALIDATES the load-bearing live evidence
///     (the pointer, the pipeline and machine phase, and the exact Pending slot) and then takes the
///     hold Held → Adopted in the same <c>_lock</c> span. An invalidating writer (cancellation,
///     terminal transition, slot admission or retirement) that lands before it yields
///     <see cref="RestoredAttemptAdoptionRefusal.AttemptNoLongerValid"/> with nothing mutated; one
///     that lands after it sees an Adopted attempt. The MANAGER ROUTE is proven inside the same span,
///     immediately before the CAS: a removal completed by then (including a retry clearing with no
///     terminal transition) yields <see cref="RestoredAttemptAdoptionRefusal.RouteNoLongerValid"/>,
///     nothing mutated. A lost hold CAS (the release sweep won) is
///     <see cref="RestoredAttemptAdoptionOutcome.HoldAlreadyReleased"/>, nothing mutated. The hold
///     leaves BEFORE the worker is visible;</description></item>
///   <item><description>the busy registration
///     (<see cref="WorkerPool.RegisterAdoptedWorker"/>) with a NEW <see cref="WorkTask"/> built from
///     the context: the slot's task id, the recorded goal and role, the recorded model VERBATIM,
///     the pipeline's description, an EMPTY prompt and NO repositories;</description></item>
///   <item><description>ROLLBACK for EVERY non-<c>Registered</c> outcome of (b) AND for any
///     exception raised between (a) and a successful (b):
///     <see cref="GoalPipeline.TryRevertRestoredActiveAttemptAdoption"/> puts the attempt back under
///     hold FIRST, and only then is the outcome (or the ORIGINAL exception) surfaced —
///     <see cref="RestoredAttemptAdoptionOutcome.ActiveQueueEntryExists"/>,
///     <see cref="RestoredAttemptAdoptionOutcome.DuplicateWorkerId"/> or
///     <see cref="RestoredAttemptAdoptionOutcome.CommitFailed"/>. An attempt is therefore never
///     stranded in the adopted state with no worker behind it;</description></item>
///   <item><description>on success, <see cref="RestoredAttemptAdoptionResult.Adopted"/> is
///     <c>true</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// WHAT IT DOES NOT DO, and what it is not: no worker authentication, no assignment replay, no
/// prompt re-delivery, no completion bypass — an adopted worker's completion flows through the
/// EXISTING completion path unchanged — no dispatch, no persistence, no goal or queue cleanup, and
/// no reconciliation of any other attempt.
/// </para>
/// </remarks>
internal sealed class RestoredAttemptAdopter : IRestoredAttemptAdopter
{
    private const string RollbackFailedTemplate =
        "RestoredAttemptAdopter: rollback-not-applied task={TaskId} worker={WorkerId} — the attempt was " +
        "not in the adopted state when its failed commit was rolled back; no transition was applied";

    private readonly GoalPipelineManager _pipelineManager;
    private readonly WorkerPool _workerPool;
    private readonly TaskQueue _taskQueue;
    private readonly WorkerAssignmentContextStore _assignmentStore;
    private readonly ILogger<RestoredAttemptAdopter> _logger;

    /// <summary>
    /// THE COMMIT-WINDOW SEAM — a test-only hook invoked EXACTLY ONCE, inside the adoption, AFTER
    /// every read-only precondition has held and IMMEDIATELY BEFORE the synchronized
    /// validate-and-adopt region (<see cref="GoalPipeline.TryAdoptRestoredActiveAttemptIfStillValid"/>)
    /// is entered.
    /// <para>
    /// WHY IT EXISTS. The <see cref="RestoredAttemptAdoptionOutcome.HoldAlreadyReleased"/> and
    /// <see cref="RestoredAttemptAdoptionRefusal.AttemptNoLongerValid"/> branches are defined by a
    /// concurrent writer landing between the unsynchronized precondition reads and the commit. A
    /// post-condition cannot manufacture that interleaving — only a hook placed at the real
    /// boundary can, and the codebase uses this same shape elsewhere
    /// (<c>PipelineStateMachine.OnTransitionForTest</c>, <see cref="GoalPipeline.TaskIdNonceForTest"/>).
    /// </para>
    /// <para>
    /// IT OBSERVES; IT DOES NOT DECIDE. It cannot adopt, cannot bypass a precondition and cannot
    /// influence the outcome beyond what any concurrent writer could do anyway: its only power is to
    /// run code at the moment a real race would land.
    /// </para>
    /// <para>
    /// <c>null</c> (the production default) means the hook is absent and the commit proceeds
    /// uninterrupted, so production behavior is byte-identical with or without this member.
    /// </para>
    /// </summary>
    internal Action? BeforeAdoptCommitForTest { get; set; }

    /// <summary>
    /// Initialises a new <see cref="RestoredAttemptAdopter"/> over the existing production
    /// collaborators.
    /// </summary>
    /// <param name="pipelineManager">The routing/registry authority the claimed task id is resolved against.</param>
    /// <param name="workerPool">The pool the adopted worker is registered busy in.</param>
    /// <param name="taskQueue">The queue whose active entry must be claimed with the registration.</param>
    /// <param name="assignmentStore">The INSERT-ONCE assignment-context store the recorded binding is loaded from.</param>
    /// <param name="logger">Logger for the guarded rollback diagnostic (the adopter logs no refusal).</param>
    /// <exception cref="ArgumentNullException">Any dependency is <c>null</c>.</exception>
    internal RestoredAttemptAdopter(
        GoalPipelineManager pipelineManager,
        WorkerPool workerPool,
        TaskQueue taskQueue,
        WorkerAssignmentContextStore assignmentStore,
        ILogger<RestoredAttemptAdopter> logger)
    {
        ArgumentNullException.ThrowIfNull(pipelineManager);
        ArgumentNullException.ThrowIfNull(workerPool);
        ArgumentNullException.ThrowIfNull(taskQueue);
        ArgumentNullException.ThrowIfNull(assignmentStore);
        ArgumentNullException.ThrowIfNull(logger);

        _pipelineManager = pipelineManager;
        _workerPool = workerPool;
        _taskQueue = taskQueue;
        _assignmentStore = assignmentStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public RestoredAttemptAdoptionResult TryAdoptRestoredAttempt(
        string workerId,
        string taskId,
        string[] capabilities,
        bool requestCompletionReceiptAck,
        bool completionReceiptAckEnabled)
    {
        // THE CALLER-BUG REFUSALS precede every read: a blank identity names no attempt and no
        // caller, so no adoption outcome is invented for it.
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(capabilities);

        GoalPipeline pipeline;
        WorkerAssignmentContext context;

        // ── THE READ-ONLY EVIDENCE PHASE. EVERY failure of a store or registry read is contained
        //    HERE: it is logged and reported as ReadFailed, because an unreadable evidence set can
        //    only ever refuse — never adopt — and must not escape as an exception that the caller
        //    would have to classify. ─────────────────────────────────────────────────────────────
        try
        {
            // (1) THE ROUTE.
            var resolved = _pipelineManager.GetByTaskId(taskId);
            if (resolved is null)
                return Refused(RestoredAttemptAdoptionRefusal.NoPipeline);

            pipeline = resolved;

            // (2) THE HOLD. Only a HELD attempt is adoptable at all.
            if (!pipeline.IsRestoredActiveAttemptHold)
                return Refused(RestoredAttemptAdoptionRefusal.NotHeld);

            // (3) THE POINTER, compared ORDINALLY.
            if (!string.Equals(pipeline.ActiveTaskId, taskId, StringComparison.Ordinal))
                return Refused(RestoredAttemptAdoptionRefusal.PointerMismatch);

            // (4) THE REGISTRY EVIDENCE. Only a HYDRATED registry corroborates the attempt: evidence
            //     that was missing or rejected can never be adopted, so uncertainty refuses.
            if (pipeline.RestoredRegistryClassification != RestoredRegistryOutcome.Restored)
                return Refused(RestoredAttemptAdoptionRefusal.RegistryEvidenceUntrusted);

            // (5) THE RESTORE-TIME POINTER OBSERVATION: the exact matching slot must have been
            //     PENDING when the restore classified it.
            if (pipeline.RestoredActivePointerClassification != RestoredActivePointerOutcome.ActiveSlotPending)
                return Refused(RestoredAttemptAdoptionRefusal.SlotNotPending);

            // (6) THE RECORDED ASSIGNMENT CONTEXT — absence is the only null the store reports.
            var recorded = _assignmentStore.Load(taskId);
            if (recorded is null)
                return Refused(RestoredAttemptAdoptionRefusal.NoAssignmentContext);

            context = recorded.Context;

            // (6a) THE RECORDED WORKER ID, the whole point of the trust model: the caller-supplied
            //      id must equal the id RECORDED at assignment.
            if (!string.Equals(context.WorkerId, workerId, StringComparison.Ordinal))
                return Refused(RestoredAttemptAdoptionRefusal.WorkerMismatch);

            // (6b) THE RECORDED GOAL must be this pipeline's goal.
            if (!string.Equals(context.GoalId, pipeline.GoalId, StringComparison.Ordinal))
                return Refused(RestoredAttemptAdoptionRefusal.GoalMismatch);

            // (6c) THE SLOT, compared against the pipeline's OWN restored registry — never against
            //      the caller's text and never against a re-derived position.
            var restoredSlot = FindRegistrySlot(pipeline.CaptureRegistry(), taskId);
            if (restoredSlot is null || !SlotAgrees(context.Slot, restoredSlot))
                return Refused(RestoredAttemptAdoptionRefusal.SlotMismatch);

            // (7) PHASE CONSISTENCY: the pipeline, the machine and the recorded slot must name the
            //     SAME worker phase. A phase with no worker can never be adopted.
            var slotPhase = context.Slot.Position.Phase;
            if (slotPhase is GoalPhase.Planning or GoalPhase.Done or GoalPhase.Failed
                || pipeline.Phase != slotPhase
                || pipeline.StateMachine.Phase != slotPhase)
            {
                return Refused(RestoredAttemptAdoptionRefusal.PhaseMismatch);
            }
        }
        catch (Exception readFailure)
        {
            // CONTAINED AND CARRIED, NOT LOGGED: the caller owns the ONE refusal warning and renders
            // this exact cause into it, so the production path emits a single warning for the
            // refusal. A corrupt or unreadable store can only refuse the adoption.
            return new RestoredAttemptAdoptionResult
            {
                Outcome = RestoredAttemptAdoptionOutcome.Refused,
                Refusal = RestoredAttemptAdoptionRefusal.ReadFailed,
                ReadException = readFailure,
            };
        }

        // ── (a) THE SYNCHRONIZED VALIDATE-AND-ADOPT REGION. The load-bearing live evidence — the
        //    pointer, the pipeline and machine phase and the exact Pending slot — is REVALIDATED and
        //    the hold taken Held → Adopted in ONE pipeline-lock span, so an invalidating transition
        //    either lands before it (→ refusal, nothing mutated) or after the CAS (→ Adopted). ─────
        // THE TEST-ONLY COMMIT-WINDOW HOOK sits exactly at the boundary the race is defined by:
        // after every read-only precondition, before the synchronized region. Production leaves it null.
        BeforeAdoptCommitForTest?.Invoke();

        // THE ROUTE IS PROVEN INSIDE THE SYNCHRONIZED REGION, adjacent to the CAS — never in a
        // separate step before it. A manager removal can land with NO preceding terminal AdvanceTo
        // (GoalDispatcher.ClearGoalRetryState on a user retry), which invalidates none of the
        // pointer/phase/slot evidence; a pre-region recheck would leave a window in which such a
        // removal lands and the attempt is still adopted with no manager route. The probe is the
        // manager's lock-free lookup, compared by reference to the pipeline being adopted.
        var decision = pipeline.TryAdoptRestoredActiveAttemptIfStillValid(
            context.Slot,
            () => ReferenceEquals(_pipelineManager.GetByTaskId(taskId), pipeline));
        switch (decision)
        {
            case RestoredAttemptCommitDecision.Adopted:
                break;
            case RestoredAttemptCommitDecision.HoldAlreadyReleased:
                // The release sweep won the race: nothing was adopted and nothing was mutated.
                return new RestoredAttemptAdoptionResult
                {
                    Outcome = RestoredAttemptAdoptionOutcome.HoldAlreadyReleased,
                };
            case RestoredAttemptCommitDecision.AttemptNoLongerValid:
                // An invalidating transition won: nothing was adopted, the hold is untouched.
                return Refused(RestoredAttemptAdoptionRefusal.AttemptNoLongerValid);
            case RestoredAttemptCommitDecision.RouteNoLongerValid:
                // The pipeline lost its manager route before the CAS: nothing was adopted, the hold
                // is untouched, and no busy worker is ever published for a routeless attempt.
                return Refused(RestoredAttemptAdoptionRefusal.RouteNoLongerValid);
            default:
                throw new InvalidOperationException(
                    $"Unhandled RestoredAttemptCommitDecision: {decision}");
        }

        // ── (b) THE ATOMIC BUSY REGISTRATION, with the task built from the RECORDED context. ──────
        AdoptedRegistrationResult registration;
        WorkTask task;
        try
        {
            task = new WorkTask
            {
                TaskId = context.Slot.TaskId,
                GoalId = context.GoalId,
                GoalDescription = pipeline.Description,
                Prompt = "",
                Role = context.Role,
                Model = context.Model,
                Repositories = [],
            };

            registration = _workerPool.RegisterAdoptedWorker(
                workerId,
                capabilities,
                requestCompletionReceiptAck,
                completionReceiptAckEnabled,
                task,
                _taskQueue);
        }
        catch (Exception commitFailure)
        {
            // ROLLBACK FIRST, then surface the ORIGINAL exception: an attempt is never left adopted
            // with no worker behind it.
            RollBack(pipeline, taskId, workerId);
            return new RestoredAttemptAdoptionResult
            {
                Outcome = RestoredAttemptAdoptionOutcome.CommitFailed,
                CommitException = commitFailure,
            };
        }

        // EVERY non-Registered outcome rolls the attempt back under hold FIRST. The registration
        // primitive has already left the pool and the existing entry as they were. Each outcome is
        // enumerated explicitly — an unknown one is a programming error, surfaced after the rollback.
        switch (registration.Outcome)
        {
            case AdoptedRegistrationOutcome.Registered:
                break;
            case AdoptedRegistrationOutcome.ActiveEntryExists:
                RollBack(pipeline, taskId, workerId);
                return new RestoredAttemptAdoptionResult
                {
                    Outcome = RestoredAttemptAdoptionOutcome.ActiveQueueEntryExists,
                };
            case AdoptedRegistrationOutcome.DuplicateId:
                RollBack(pipeline, taskId, workerId);
                return new RestoredAttemptAdoptionResult
                {
                    Outcome = RestoredAttemptAdoptionOutcome.DuplicateWorkerId,
                };
            default:
                RollBack(pipeline, taskId, workerId);
                throw new InvalidOperationException(
                    $"Unhandled AdoptedRegistrationOutcome: {registration.Outcome}");
        }

        // ── (d) ADOPTED: the instance the pool now holds is the one built fully busy. ─────────────
        return new RestoredAttemptAdoptionResult
        {
            Outcome = RestoredAttemptAdoptionOutcome.Adopted,
            Worker = registration.Worker,
            Task = task,
        };
    }

    /// <summary>
    /// Puts a failed commit's attempt back under hold. The transition is a compare-and-swap FROM
    /// adopted, so it can never resurrect a released hold; a <c>false</c> is only possible when the
    /// state was not adopted, which is reported as a guarded diagnostic rather than assumed away.
    /// </summary>
    private void RollBack(GoalPipeline pipeline, string taskId, string workerId)
    {
        if (pipeline.TryRevertRestoredActiveAttemptAdoption())
            return;

        LogSafely(() => _logger.LogWarning(RollbackFailedTemplate, taskId, workerId));
    }

    /// <summary>
    /// Locates the restored registry's slot for <paramref name="taskId"/> — ordinal task-id match —
    /// in the detached snapshot, or <c>null</c> when the restored registry holds no such slot.
    /// </summary>
    private static WorkSlot? FindRegistrySlot(WorkSlotRegistrySnapshot registry, string taskId)
    {
        foreach (var view in registry.Slots)
        {
            if (string.Equals(view.Slot.TaskId, taskId, StringComparison.Ordinal))
                return view.Slot;
        }

        return null;
    }

    /// <summary>
    /// THE SLOT AGREEMENT: the recorded context's slot and the restored registry's slot must name
    /// the same task, the same position (iteration, phase and occurrence) and the same attempt.
    /// </summary>
    private static bool SlotAgrees(WorkSlot recorded, WorkSlot restored) =>
        string.Equals(recorded.TaskId, restored.TaskId, StringComparison.Ordinal)
        && recorded.Position.Iteration == restored.Position.Iteration
        && recorded.Position.Phase == restored.Position.Phase
        && recorded.Position.Occurrence == restored.Position.Occurrence
        && recorded.Attempt == restored.Attempt;

    private static RestoredAttemptAdoptionResult Refused(RestoredAttemptAdoptionRefusal refusal) =>
        new() { Outcome = RestoredAttemptAdoptionOutcome.Refused, Refusal = refusal };

    /// <summary>
    /// A DIAGNOSTIC IS NEVER ALLOWED TO DETERMINE AN OUTCOME: a throwing logger is swallowed, so
    /// the refusal it accompanies is still returned.
    /// </summary>
    private static void LogSafely(Action emit)
    {
        try
        {
            emit();
        }
        catch
        {
            // Diagnostic failure only — the reported outcome stands.
        }
    }
}
