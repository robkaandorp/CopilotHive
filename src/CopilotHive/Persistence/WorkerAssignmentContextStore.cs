using System.Globalization;

using CopilotHive.Persistence.Entities;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Persistence;

/// <summary>
/// THE WORKER-ASSIGNMENT-CONTEXT STORE — the durable, INSERT-ONCE typed-column record of the
/// SERVER's INTENDED binding for one dispatched task.
/// <para>
/// WHAT A RECORDED ROW MEANS, HONESTLY, AND WHAT IT DOES NOT. A recorded assignment is the server's
/// INTENDED binding at the moment it was captured. It does NOT prove that the assignment was
/// delivered, that the binding is still currently authorized, or that the worker is still alive;
/// nothing here is a completion receipt, an acknowledgement, or a replay permission. The recorded
/// timestamp is the first CONFIRMED PERSISTENCE INTENT — never an observed worker start and never a
/// delivery time.
/// </para>
/// <para>
/// ONE NARROW STATEMENT, AND THE DATABASE ARBITRATES. <see cref="InsertOnce"/> issues exactly one
/// parameterized <c>INSERT … ON CONFLICT(task_id) DO NOTHING</c> against
/// <c>worker_assignment_contexts</c> inside ONE explicit transaction. There is no SELECT before the
/// INSERT, no UPDATE, no DELETE, no process lock and no automatic retry: the table's PRIMARY KEY
/// (<c>task_id</c>) is the single arbiter of competing inserts. A returned count of exactly one is
/// the only success count; exactly zero is the only duplicate candidate; every other count — a
/// negative provider count just as much as a count above one — is WRITE UNCERTAINTY, never a success
/// and never a duplicate.
/// </para>
/// <para>
/// THE CONTEXT IS FROZEN BEFORE THE DATABASE IS TOUCHED. The candidate is validated (by
/// <see cref="WorkerAssignmentContext"/>'s constructor) before a store call ever happens, and
/// <see cref="InsertOnce"/> then captures ONE set of scalar values and generates ONE first-assigned
/// instant BEFORE the context is acquired. The invariant round-trip (<c>"O"</c>) timestamp text and
/// the lowercase enum texts are bound EXPLICITLY as parameters, because raw SQL bypasses the EF value
/// converters that would otherwise produce exactly those texts.
/// </para>
/// <para>
/// READ ERRORS AND WRITE UNCERTAINTY ARE DIFFERENT TRUTHS, AND STAY DIFFERENT.
/// <see cref="WorkerAssignmentWriteStatus.Indeterminate"/> is reserved for the write attempt — a
/// throw from the INSERT / <c>SaveChanges</c> / <c>Commit</c> sequence, or an affected-row count other
/// than 0/1 — and carries the EXACT caught exception object (or an
/// <see cref="InvalidOperationException"/> describing the count). It makes NO claim about rollback or
/// about whether anything was mutated. Everything AFTER a confirmed zero-row insert — a vanished
/// duplicate row, a corrupt or inconsistent stored context, an unmaterializable timestamp, a failed
/// read query — THROWS as a read or integrity error and is never dressed up as a write outcome with a
/// fabricated exception. The uncertainty handler wraps the write sequence alone, so those errors
/// cannot be swallowed by it.
/// </para>
/// <para>
/// IDS ARE OPAQUE. Existing legacy task ids and new suffixed forms are accepted verbatim; nothing
/// parses, rewrites or reconstructs them. This store makes NO claim to detect or protect against a
/// caller deliberately REUSING an arbitrary explicit task id: the first writer's row is kept and every
/// later differing context for that id is reported as
/// <see cref="WorkerAssignmentWriteStatus.Conflict"/> — a refusal, never a rebind.
/// </para>
/// <para>
/// SCOPE. <see cref="WorkerAssignmentContextStore"/> is registered in production DI and is used by
/// <see cref="WorkerAssignmentPublisher"/> before Ready/eager assignment publication; its rows are
/// loaded by <see cref="WorkerCompletionRecorder"/>. A recorded row records the server's INTENDED
/// assignment context — it is NOT proof of delivery, current authorization, worker liveness, or
/// completion processing. The store itself does not publish, retry, or reconcile.
/// </para>
/// </summary>
internal sealed class WorkerAssignmentContextStore
{
    private readonly IDbContextFactory<CopilotHiveDbContext> _dbContextFactory;
    private readonly ILogger<WorkerAssignmentContextStore> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// THE DISPOSAL SEAM for the factory-owned contexts. <see cref="CopilotHiveDbContext"/> is sealed,
    /// so its <c>Dispose</c> cannot be overridden and EF never closes an externally supplied
    /// connection — the failure is otherwise not genuinely injectable. When installed, it SUBSTITUTES
    /// the fallible dispose operation; the GUARD itself (the try/catch, the <c>context-dispose</c>
    /// warning, the swallow and the outcome preservation) remains production code the tests exercise
    /// for real, and the null-default branch calls the REAL <c>Dispose()</c>, so every non-injected
    /// caller is unchanged. Instance-scoped on purpose: each store instance carries its own seam (no
    /// static, no cross-test pollution). A test that injects a failing disposer still owns the real
    /// resources through its factory, so nothing is leaked by the injection.
    /// </summary>
    internal Action<CopilotHiveDbContext>? ContextDisposerForTest;

    /// <summary>
    /// THE ONE WRITE STATEMENT. Narrow, fully parameterized, and conflict-tolerant: the
    /// <c>ON CONFLICT(task_id) DO NOTHING</c> clause makes a competing insert a ZERO-ROW result rather
    /// than an error, so the duplicate decision is made by the database itself. The <c>role</c> and
    /// <c>phase</c> texts and the <c>first_assigned_at_utc</c> text are supplied by the caller because
    /// raw SQL bypasses the EF value converters.
    /// </summary>
    private const string InsertStatement =
        """
        INSERT INTO worker_assignment_contexts
            (task_id, goal_id, worker_id, role, phase, model, iteration, occurrence, attempt, first_assigned_at_utc)
        VALUES
            (@taskId, @goalId, @workerId, @role, @phase, @model, @iteration, @occurrence, @attempt, @firstAssignedAtUtc)
        ON CONFLICT(task_id) DO NOTHING
        """;

    private const string InsertUncertainTemplate =
        "WorkerAssignmentContextStore: insert-uncertain task={TaskId} — the assignment-context write did not confirm; nothing is inferred about the row's presence: {Message}";

    private const string ContextDisposeTemplate =
        "WorkerAssignmentContextStore: context-dispose task={TaskId} — the factory-owned context dispose failed; the recorded outcome stands: {Message}";

    private const string TransactionDisposeTemplate =
        "WorkerAssignmentContextStore: transaction-dispose task={TaskId} — the transaction dispose failed; the recorded outcome stands: {Message}";

    /// <summary>
    /// Initialises a new <see cref="WorkerAssignmentContextStore"/> over a DbContext factory. There is
    /// deliberately NO borrowed/direct-context mode: every operation owns one short-lived context
    /// created by the factory and disposes it before returning.
    /// </summary>
    /// <param name="dbContextFactory">Factory used to create transient <see cref="CopilotHiveDbContext"/> instances.</param>
    /// <param name="logger">Logger for the guarded diagnostics; defaults to a no-op logger.</param>
    /// <param name="timeProvider">Clock for the first-assigned timestamp; defaults to <see cref="TimeProvider.System"/>.</param>
    internal WorkerAssignmentContextStore(
        IDbContextFactory<CopilotHiveDbContext> dbContextFactory,
        ILogger<WorkerAssignmentContextStore>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dbContextFactory);

        _dbContextFactory = dbContextFactory;
        _logger = logger ?? NullLogger<WorkerAssignmentContextStore>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Records the INTENDED assignment binding for a task EXACTLY ONCE, reporting one of four explicit
    /// outcomes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE ORDER IS THE CONTRACT. The null-argument guard, the single set of scalar values and the
    /// single first-assigned instant all happen BEFORE the context is acquired: an acquisition failure
    /// (or a clock failure) PROPAGATES the exact exception having created no row and issued no
    /// statement — it is never reported as a write outcome.
    /// </para>
    /// <para>
    /// ONE EXPLICIT TRANSACTION. The INSERT, the <c>SaveChanges</c> and the <c>Commit</c> run inside a
    /// transaction the store opens for itself. A <c>BeginTransaction</c> failure is PRE-STATEMENT: it
    /// propagates exactly (it is neither a write outcome nor a read error), because nothing was
    /// attempted yet.
    /// </para>
    /// <para>
    /// THE WRITE-UNCERTAINTY HANDLER wraps the INSERT / <c>SaveChanges</c> / <c>Commit</c> sequence and
    /// nothing else. A throw there returns <see cref="WorkerAssignmentWriteStatus.Indeterminate"/>
    /// carrying that EXACT exception object; an affected-row count other than 0/1 returns
    /// <see cref="WorkerAssignmentWriteStatus.Indeterminate"/> carrying an
    /// <see cref="InvalidOperationException"/> that names the count. Neither claims rollback and
    /// neither claims that no row exists.
    /// </para>
    /// <para>
    /// THE ZERO-ROW PATH is a read, and reads are authoritative: the existing row is read
    /// <c>AsNoTracking</c>, materialized and validated. A vanished row, a corrupt stored context or a
    /// failed read THROWS. A VALID existing row whose every context value (goal, worker, slot, role,
    /// model) is identical — compared ordinally/value-wise, EXCLUDING first-assigned time — is
    /// <see cref="WorkerAssignmentWriteStatus.AlreadyRecorded"/>; any actually differing context is
    /// <see cref="WorkerAssignmentWriteStatus.Conflict"/>, which also changes nothing. The existing row
    /// is never overwritten or rebound, and its first-assigned time is never refreshed.
    /// </para>
    /// </remarks>
    /// <param name="context">The INTENDED assignment context to record.</param>
    /// <returns>The recorded write outcome, with exception evidence only for the uncertain one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The duplicate row is missing or corrupt, or the read query itself failed.
    /// </exception>
    internal WorkerAssignmentWriteResult InsertOnce(WorkerAssignmentContext context)
    {
        // PHASE 0 — THE ARGUMENT GUARD, before anything else exists. The context's own constructor
        // already proved every field rule (non-blank identities, a non-null slot/position, a
        // worker-backed phase with its mapped role, positive iteration/occurrence/attempt, and a
        // non-null verbatim model), so no context that reaches this line can be invalid.
        ArgumentNullException.ThrowIfNull(context);

        // PHASE 1 — ONE IMMUTABLE SET OF SCALAR VALUES, captured before the context is acquired.
        // Nothing below this point reads the caller's object again.
        var slot = context.Slot;
        var position = slot.Position;
        var taskId = slot.TaskId;
        var goalId = context.GoalId;
        var workerId = context.WorkerId;
        // The lowercase enum texts mirror the EF converter's write side exactly: raw SQL parameters
        // never pass through a value converter.
        var roleText = context.Role.ToString().ToLowerInvariant();
        var phaseText = position.Phase.ToString().ToLowerInvariant();
        var model = context.Model;
        var iteration = position.Iteration;
        var occurrence = position.Occurrence;
        var attempt = slot.Attempt;

        // PHASE 2 — THE ONE INSTANT, formatted with the invariant round-trip format because the raw SQL
        // bypasses the EF value converter that would otherwise write exactly this text. It records
        // first confirmed persistence INTENT, not an observed worker start.
        var firstAssignedText = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        CopilotHiveDbContext? db = null;
        IDbContextTransaction? transaction = null;
        try
        {
            // PHASE 3 — THE CONTEXT. An acquisition failure propagates with nothing to clean up.
            db = _dbContextFactory.CreateDbContext();

            // PHASE 4 — THE EXPLICIT TRANSACTION. A begin failure propagates: it is pre-statement, so it
            // is neither write uncertainty nor a read error.
            transaction = db.Database.BeginTransaction();

            // PHASE 5 — THE WRITE SEQUENCE. ONLY this block sits inside the uncertainty handler: a throw
            // here IS the write-uncertainty truth and the EXACT caught object is retained. The read path
            // below is deliberately OUTSIDE the handler, so a read or integrity failure can never be
            // reported as write uncertainty.
            int inserted;
            try
            {
                inserted = db.Database.ExecuteSqlRaw(
                    InsertStatement,
                    new SqliteParameter("@taskId", taskId),
                    new SqliteParameter("@goalId", goalId),
                    new SqliteParameter("@workerId", workerId),
                    new SqliteParameter("@role", roleText),
                    new SqliteParameter("@phase", phaseText),
                    new SqliteParameter("@model", model),
                    new SqliteParameter("@iteration", iteration),
                    new SqliteParameter("@occurrence", occurrence),
                    new SqliteParameter("@attempt", attempt),
                    new SqliteParameter("@firstAssignedAtUtc", firstAssignedText));

                // THE COUNT IS JUDGED BEFORE THE COMMIT. Not a success and not a duplicate: every count
                // other than 0 and 1 — a negative provider count just as much as a count above one — is an
                // integrity surprise, so the transaction is abandoned (the finally's disposal rolls it
                // back) rather than committed behind an unverified count. The caller gets the uncertainty
                // AND the readback that establishes the truth.
                if (inserted is not (0 or 1))
                {
                    throw new InvalidOperationException(
                        $"Worker assignment context insert affected {inserted} rows for task '{taskId}' (expected exactly 0 or 1).");
                }

                db.SaveChanges();
                transaction.Commit();
            }
            catch (Exception writeException)
            {
                // THE TRANSACTION'S FATE AND THE ROW'S PRESENCE ARE BOTH UNRESOLVED — nothing is
                // inferred from the disposal that follows.
                SafeWarning(InsertUncertainTemplate, taskId, writeException);
                return WorkerAssignmentWriteResult.Indeterminate(writeException);
            }

            if (inserted == 1)
            {
                // THE ONLY SUCCESS COUNT — a confirmed, returned statement result.
                return WorkerAssignmentWriteResult.Recorded();
            }

            // PHASE 6 — THE ZERO-ROW PATH: the primary key arbitrated the insert, so a row exists. Every
            // failure below is a READ/INTEGRITY error and THROWS — never Conflict, never
            // AlreadyRecorded, never Indeterminate.
            var existing = FindRow(db, taskId)
                ?? throw new InvalidOperationException(
                    $"Worker assignment context row for task '{taskId}' is missing after a zero-row insert.");

            var stored = Materialize(existing);

            // THE ORDINAL / VALUE-WISE COMPARISON of the whole context: first-assigned time is NOT part
            // of the context and NOT part of this decision, so an equivalent stored row with a different
            // recording instant is the SAME assignment.
            return stored.Context.Equals(context)
                ? WorkerAssignmentWriteResult.AlreadyRecorded()
                : WorkerAssignmentWriteResult.Conflict();
        }
        finally
        {
            // THE GUARDED CLEANUP — the outcome or the propagating exception stays authoritative even
            // when the transaction or context disposal (or the warning it logs) fails.
            if (transaction is not null)
                DisposeOwnedTransaction(transaction, taskId);
            if (db is not null)
                DisposeOwnedContext(db, taskId);
        }
    }

    /// <summary>
    /// Loads the recorded assignment context for a task. Returns <c>null</c> ONLY when no row exists.
    /// </summary>
    /// <remarks>
    /// The returned context is freshly allocated and immutable, and it shares no mutable state with any
    /// stored row. The same validation the zero-row write path applies is applied here: a corrupt or
    /// inconsistent stored context and an unmaterializable timestamp THROW, as does a failed read query.
    /// Absence is the only <c>null</c>.
    /// </remarks>
    /// <param name="taskId">The OPAQUE task id whose context to load; must be non-blank.</param>
    /// <returns>The detached context and its first-assigned UTC instant, or <c>null</c> when no row exists.</returns>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> is null, empty or blank.</exception>
    /// <exception cref="InvalidOperationException">The stored context is corrupt or inconsistent, or the read failed.</exception>
    internal WorkerAssignmentContextReadResult? Load(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        CopilotHiveDbContext? db = null;
        try
        {
            db = _dbContextFactory.CreateDbContext();

            var entity = FindRow(db, taskId);

            // ABSENCE IS THE ONLY NULL — nothing else is ever reported as "no recorded context".
            return entity is null ? null : Materialize(entity);
        }
        finally
        {
            if (db is not null)
                DisposeOwnedContext(db, taskId);
        }
    }

    /// <summary>
    /// THE ONE READ SHAPE both operations share: the row is read <c>AsNoTracking</c> (no tracked entity
    /// can escape the operation) and materialized. A failed query propagates its original exception.
    /// </summary>
    private static WorkerAssignmentContextEntity? FindRow(CopilotHiveDbContext db, string taskId) =>
        db.WorkerAssignmentContexts.AsNoTracking().FirstOrDefault(entity => entity.TaskId == taskId);

    /// <summary>
    /// Materializes a stored row into the read value: the row's values are re-validated through the
    /// single existing <see cref="WorkerAssignmentContext"/> contract, so a non-worker phase, an
    /// undefined or mismatched role, a non-positive iteration/occurrence/attempt, or a null identity
    /// surfaces as a read/integrity FAILURE rather than as a silently accepted context. No role,
    /// position or model is ever guessed or repaired. The timestamp is read through the entity's value
    /// converter, so an unparseable column surfaces here as a read failure rather than as a silently
    /// defaulted value.
    /// </summary>
    private static WorkerAssignmentContextReadResult Materialize(WorkerAssignmentContextEntity entity)
    {
        var position = new WorkSlotPosition(entity.Iteration, entity.Phase, entity.Occurrence);
        var slot = new WorkSlot(entity.TaskId, position, entity.Attempt);

        try
        {
            var context = new WorkerAssignmentContext(
                entity.GoalId, entity.WorkerId, entity.Role, slot, entity.Model);

            return new WorkerAssignmentContextReadResult(context, entity.FirstAssignedAtUtc);
        }
        catch (ArgumentException corrupt)
        {
            // A STORED ROW THAT CANNOT BE MATERIALIZED IS CORRUPTION, and corruption is reported as a
            // read/integrity error — never as an absence and never as a write outcome.
            throw new InvalidOperationException(
                $"The stored worker assignment context row for task '{entity.TaskId}' is not a valid context: " +
                MessageOrPlaceholder(corrupt),
                corrupt);
        }
    }

    /// <summary>
    /// THE GUARDED, ONCE-ONLY DISPOSAL of the transaction the store opened. A disposal (or warning)
    /// failure is swallowed with a diagnostic; the recorded outcome or the propagating exception is
    /// never masked.
    /// </summary>
    private void DisposeOwnedTransaction(IDbContextTransaction transaction, string taskId)
    {
        try
        {
            transaction.Dispose();
        }
        catch (Exception disposeException)
        {
            SafeWarning(TransactionDisposeTemplate, taskId, disposeException);
        }
    }

    /// <summary>
    /// THE GUARDED, ONCE-ONLY DISPOSAL of a successfully acquired factory context. A disposal (or
    /// warning) failure is swallowed with a diagnostic; the recorded outcome or the propagating
    /// exception is never masked, and the context is never disposed twice by this store.
    /// </summary>
    private void DisposeOwnedContext(CopilotHiveDbContext context, string taskId)
    {
        try
        {
            (ContextDisposerForTest ?? (static owned => owned.Dispose()))(context);
        }
        catch (Exception disposeException)
        {
            SafeWarning(ContextDisposeTemplate, taskId, disposeException);
        }
    }

    /// <summary>
    /// THE FINAL GUARD of the never-masked guarantee for the diagnostics: the whole write — the message
    /// access INCLUDED — sits inside its own no-throw guard, so an exception whose
    /// <see cref="Exception.Message"/> getter itself throws (the message is a virtual property,
    /// injectable through the disposal seam) cannot escape and mask the authoritative outcome. The
    /// logger is equally guarded: a throwing logger is silently swallowed, because there is no deeper
    /// channel to report to.
    /// </summary>
    private void SafeWarning(string template, string taskId, Exception exception)
    {
        try
        {
            _logger.LogWarning(template, taskId, MessageOrPlaceholder(exception));
        }
        catch
        {
            // SILENT swallow — the diagnostic must never mask the authoritative outcome.
        }
    }

    /// <summary>
    /// Reads <see cref="Exception.Message"/> inside its own no-throw guard: an exception whose
    /// <c>Message</c> getter throws yields a static placeholder, so the diagnostic degrades while the
    /// never-masked guarantee does not.
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
}

/// <summary>
/// THE RECORDED ASSIGNMENT CONTEXT: the immutable, detached carrier of the server's INTENDED binding
/// for one dispatched task — the goal it belongs to, the worker it is meant for, the role that worker
/// was assigned, the EXISTING <see cref="WorkSlot"/> identity of the dispatch, and the ORIGINAL ASSIGNED
/// model.
/// <para>
/// It holds NO live pipeline or worker reference and it copies no <see cref="WorkTask"/>: the domain
/// value it carries (<see cref="WorkSlot"/>) is the existing immutable one, and every other member is a
/// scalar or an enum. There is no mutable collection, no prompt, no repository URL and no credential
/// here.
/// </para>
/// <para>
/// THE CONSTRUCTOR VALIDATES INTERNAL CONSISTENCY ONLY — that the identity strings are present, that
/// iteration, occurrence and attempt are positive, that the phase is one of the worker-backed phases,
/// and that the assigned role matches that phase's existing
/// <see cref="GoalPhaseExtensions.ToWorkerRole"/> mapping. It is NOT durable authorization and NOT a
/// liveness statement: a constructed context proves nothing about whether the assignment was delivered,
/// whether the binding is still authorized, or whether the worker ever started.
/// </para>
/// <para>
/// THE MODEL IS PRESERVED VERBATIM. It may be empty or whitespace; no provider normalization, no prefix
/// stripping and no filling in of a default ever happens here.
/// </para>
/// <para>
/// TASK IDS ARE OPAQUE. Legacy and new suffixed forms are accepted verbatim: the constructor never
/// parses, rewrites or reconstructs a task id, and it makes no claim to detect deliberate reuse of an
/// arbitrary explicit id.
/// </para>
/// </summary>
internal sealed class WorkerAssignmentContext : IEquatable<WorkerAssignmentContext>
{
    /// <summary>The goal the assigned task belonged to. Never <c>null</c> or blank.</summary>
    public string GoalId { get; }

    /// <summary>The worker the task was assigned to. Never <c>null</c> or blank.</summary>
    public string WorkerId { get; }

    /// <summary>
    /// The role the worker was assigned. Always a worker-backed role — one of
    /// <see cref="WorkerRole.Coder"/>, <see cref="WorkerRole.Tester"/>, <see cref="WorkerRole.Reviewer"/>,
    /// <see cref="WorkerRole.DocWriter"/> or <see cref="WorkerRole.Improver"/> — and always the one its
    /// phase maps to.
    /// </summary>
    public WorkerRole Role { get; }

    /// <summary>
    /// The existing work-slot identity of the recorded dispatch. Never <c>null</c>, and it carries a
    /// non-blank task ID, a position with a positive iteration and occurrence and a worker-backed phase,
    /// and a positive attempt.
    /// </summary>
    public WorkSlot Slot { get; }

    /// <summary>The original assigned model, preserved verbatim; may be empty or whitespace.</summary>
    public string Model { get; }

    /// <summary>
    /// Creates a recorded assignment context after validating the supplied values' INTERNAL CONSISTENCY
    /// (see the type remarks — this is not durable authorization). Every refusal happens BEFORE any state
    /// exists, so a rejected construction yields no partially built context and no store call is ever
    /// reached.
    /// </summary>
    /// <param name="goalId">The goal ID; must be non-blank.</param>
    /// <param name="workerId">The worker ID; must be non-blank.</param>
    /// <param name="role">
    /// The role the worker was assigned; must be defined, worker-backed, and equal
    /// <see cref="GoalPhaseExtensions.ToWorkerRole"/> of the slot's phase.
    /// </param>
    /// <param name="slot">The existing work-slot identity; see the property remarks.</param>
    /// <param name="model">The original assigned model, preserved verbatim; must be non-<c>null</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="slot"/> or <paramref name="model"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">A value violates one of the consistency rules above.</exception>
    internal WorkerAssignmentContext(string goalId, string workerId, WorkerRole role, WorkSlot slot, string model)
    {
        // ── THE NULL REFUSALS, first: nothing can be inspected until the carriers are present. ──
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(model);

        // ── THE SLOT'S OWN SHAPE. A slot without a position has no identity to validate. ──
        var position = slot.Position;
        if (position is null)
            throw new ArgumentException("Worker assignment context slot must carry a position.", nameof(slot));

        // ── THE IDENTITY STRINGS. All three must be present. ──
        if (string.IsNullOrWhiteSpace(goalId))
            throw new ArgumentException("Worker assignment context goal ID must be a non-blank string.", nameof(goalId));
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Worker assignment context worker ID must be a non-blank string.", nameof(workerId));
        if (string.IsNullOrWhiteSpace(slot.TaskId))
            throw new ArgumentException("Worker assignment context slot task ID must be a non-blank string.", nameof(slot));

        // ── THE POSITION NUMBERS: one-based iteration, occurrence and attempt. No inference, no parsing
        //    of the task ID, and no legacy "assume 1" fallback. ──
        if (position.Iteration <= 0)
        {
            throw new ArgumentException(
                $"Worker assignment context iteration must be positive but was {position.Iteration}.", nameof(slot));
        }
        if (position.Occurrence <= 0)
        {
            throw new ArgumentException(
                $"Worker assignment context occurrence must be positive but was {position.Occurrence}.", nameof(slot));
        }
        if (slot.Attempt <= 0)
        {
            throw new ArgumentException(
                $"Worker assignment context attempt must be positive but was {slot.Attempt}.", nameof(slot));
        }

        // ── THE WORKER-BACKED PHASE. Planning/Merging/Done/Failed have no worker, and an undefined value
        //    has no mapping at all — both are refused rather than guessed at. ──
        switch (position.Phase)
        {
            case GoalPhase.Coding:
            case GoalPhase.Testing:
            case GoalPhase.Review:
            case GoalPhase.DocWriting:
            case GoalPhase.Improve:
                break;
            case GoalPhase.Planning:
            case GoalPhase.Merging:
            case GoalPhase.Done:
            case GoalPhase.Failed:
                throw new ArgumentException(
                    $"Worker assignment context phase '{position.Phase}' has no worker and cannot hold an assignment.",
                    nameof(slot));
            default:
                throw new ArgumentException(
                    $"Worker assignment context phase has undefined GoalPhase value {(int)position.Phase}.", nameof(slot));
        }

        // ── THE ROLE. It must be a DEFINED role AND the one the phase maps to; an undefined value is
        //    named as such, and every other role (Unspecified, Orchestrator, MergeWorker) simply fails the
        //    mapping comparison. ──
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentException(
                $"Worker assignment context role has undefined WorkerRole value {(int)role}.", nameof(role));
        }

        var derivedRole = position.Phase.ToWorkerRole();
        if (role != derivedRole)
        {
            throw new ArgumentException(
                $"Worker assignment context role '{role}' does not match the role '{derivedRole}' mapped from " +
                $"phase '{position.Phase}'.", nameof(role));
        }

        // ── ONLY NOW, with every rule satisfied, does the context come into being. ──
        GoalId = goalId;
        WorkerId = workerId;
        Role = role;
        Slot = slot;
        Model = model;
    }

    /// <summary>
    /// THE VALUE-WISE CONTEXT COMPARISON the duplicate decision uses — and it deliberately EXCLUDES
    /// first-assigned time, which is not part of the context. Every string is compared ORDINALLY
    /// (case-sensitively), every enum and number by value, so two contexts are the same recorded
    /// assignment exactly when all five members agree.
    /// </summary>
    /// <param name="other">The context to compare against, or <c>null</c>.</param>
    /// <returns><c>true</c> when every context value agrees; <c>false</c> otherwise.</returns>
    public bool Equals(WorkerAssignmentContext? other) =>
        other is not null
        && string.Equals(GoalId, other.GoalId, StringComparison.Ordinal)
        && string.Equals(WorkerId, other.WorkerId, StringComparison.Ordinal)
        && Role == other.Role
        && Slot == other.Slot
        && string.Equals(Model, other.Model, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is WorkerAssignmentContext other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GoalId, WorkerId, Role, Slot, Model);
}

/// <summary>
/// The recorded outcome of <see cref="WorkerAssignmentContextStore.InsertOnce"/> — four explicit truths
/// about the server's INTENDED binding.
/// </summary>
internal enum WorkerAssignmentWriteStatus
{
    /// <summary>
    /// EXACTLY ONE row was inserted, with a confirmed returned statement result. This records
    /// persistence intent only — not delivery, not current authorization and not worker liveness.
    /// </summary>
    Recorded,

    /// <summary>
    /// ZERO rows were inserted and the VALID existing row is the SAME context: every goal, worker, slot,
    /// role and model value matches. The stored row and its first-assigned time are left exactly as they
    /// were.
    /// </summary>
    AlreadyRecorded,

    /// <summary>
    /// ZERO rows were inserted and the VALID existing row is a DIFFERENT context — a different goal,
    /// worker, slot, role or model. Nothing is changed: neither the values nor the first-assigned time.
    /// This is a refusal to rebind, NOT a claim of protection against deliberate id reuse.
    /// </summary>
    Conflict,

    /// <summary>
    /// THE WRITE IS UNRESOLVED: the INSERT / <c>SaveChanges</c> / <c>Commit</c> sequence threw, or it
    /// returned an affected-row count other than 0/1. NO claim is made about rollback or about whether a
    /// row now exists; the caller owns any retry and the store performs no hidden reconciliation.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// The result of <see cref="WorkerAssignmentContextStore.InsertOnce"/>: the recorded <see cref="Status"/>
/// plus, for the uncertain outcome only, the EXACT exception evidence.
/// </summary>
/// <remarks>
/// <para>
/// EXCEPTION IDENTITY, not description: <see cref="WriteException"/> carries the exact caught instance
/// (any EF wrapper included) for a throwing write, or the <see cref="InvalidOperationException"/>
/// describing an unexpected affected-row count. It is <c>null</c> for
/// <see cref="WorkerAssignmentWriteStatus.Recorded"/>,
/// <see cref="WorkerAssignmentWriteStatus.AlreadyRecorded"/> and
/// <see cref="WorkerAssignmentWriteStatus.Conflict"/> — read and integrity errors THROW instead of
/// appearing here.
/// </para>
/// <para>
/// THE PAIRING IS STRUCTURAL, NOT MERELY DOCUMENTED. The only constructor is PRIVATE and validating, so
/// the four factories below are the sole way to obtain a result and an inconsistent pair — a confirmed
/// outcome carrying exception evidence, or an <see cref="WorkerAssignmentWriteStatus.Indeterminate"/>
/// with no evidence — cannot be constructed at all. This changes no outcome the store produces; it only
/// removes the ability to fabricate one.
/// </para>
/// </remarks>
internal sealed record WorkerAssignmentWriteResult
{
    /// <summary>The recorded outcome.</summary>
    internal WorkerAssignmentWriteStatus Status { get; }

    /// <summary>The exact write-uncertainty evidence, or <c>null</c> for the three confirmed outcomes.</summary>
    internal Exception? WriteException { get; }

    /// <summary>
    /// THE ONE (private) CONSTRUCTOR, which enforces the documented pairing: a defined status, evidence
    /// for <see cref="WorkerAssignmentWriteStatus.Indeterminate"/> and no evidence for any confirmed
    /// outcome. Every call site is one of the four factories below, so this never fires in practice — it
    /// exists so a future one cannot quietly break the invariant.
    /// </summary>
    private WorkerAssignmentWriteResult(WorkerAssignmentWriteStatus status, Exception? writeException)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentException(
                $"Worker assignment write result has undefined status value {(int)status}.", nameof(status));
        }

        if (status == WorkerAssignmentWriteStatus.Indeterminate)
        {
            // The unresolved outcome is meaningless without its evidence.
            ArgumentNullException.ThrowIfNull(writeException);
        }
        else if (writeException is not null)
        {
            // A CONFIRMED outcome never carries exception evidence: read and integrity errors THROW.
            throw new ArgumentException(
                $"Worker assignment write result '{status}' is a confirmed outcome and cannot carry write " +
                "exception evidence.", nameof(writeException));
        }

        Status = status;
        WriteException = writeException;
    }

    /// <summary>The confirmed single-row insert — no exception evidence.</summary>
    internal static WorkerAssignmentWriteResult Recorded() =>
        new(WorkerAssignmentWriteStatus.Recorded, null);

    /// <summary>The confirmed identical duplicate — no exception evidence.</summary>
    internal static WorkerAssignmentWriteResult AlreadyRecorded() =>
        new(WorkerAssignmentWriteStatus.AlreadyRecorded, null);

    /// <summary>The confirmed differing context — no exception evidence.</summary>
    internal static WorkerAssignmentWriteResult Conflict() =>
        new(WorkerAssignmentWriteStatus.Conflict, null);

    /// <summary>The unresolved write, retaining the exact evidence object as caught.</summary>
    /// <param name="writeException">The exact caught (or count-describing) exception.</param>
    internal static WorkerAssignmentWriteResult Indeterminate(Exception writeException) =>
        new(WorkerAssignmentWriteStatus.Indeterminate, writeException);
}

/// <summary>
/// The value <see cref="WorkerAssignmentContextStore.Load"/> returns for a row that EXISTS: the freshly
/// allocated, immutable context plus the instant the assignment was FIRST recorded (UTC).
/// </summary>
/// <param name="Context">The materialized context; immutable and detached from any stored or tracked row.</param>
/// <param name="FirstAssignedAtUtc">The row's first-assigned UTC instant, preserved exactly.</param>
internal sealed record WorkerAssignmentContextReadResult(
    WorkerAssignmentContext Context,
    DateTime FirstAssignedAtUtc);
