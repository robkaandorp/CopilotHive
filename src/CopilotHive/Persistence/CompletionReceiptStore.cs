using System.Globalization;

using CopilotHive.Persistence.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Persistence;

/// <summary>
/// THE COMPLETION-RECEIPT STORE — the durable, INSERT-ONLY evidence-retention primitive for a
/// <see cref="CompletionReceipt"/>.
/// <para>
/// ONE NARROW STATEMENT, AND THE DATABASE ARBITRATES. <see cref="InsertOnce"/> executes exactly one
/// parameterized <c>INSERT … ON CONFLICT(task_id) DO NOTHING</c> against
/// <c>completion_receipts</c>. There is no SELECT before the INSERT, no <c>SaveChanges</c>, no
/// UPDATE/DELETE, no explicit transaction, no process lock, no automatic retry and no compensation
/// step: the table's PRIMARY KEY (<c>task_id</c>) is the single arbiter of concurrent inserts. A
/// returned count of exactly one is the only success count; exactly zero is the only duplicate
/// candidate; every other count — a negative provider count just as much as a count above one — is
/// WRITE UNCERTAINTY, never a success and never a duplicate.
/// </para>
/// <para>
/// THE PAYLOAD IS FROZEN BEFORE THE DATABASE IS TOUCHED. <see cref="CompletionReceiptCodec.Encode"/>
/// runs EXACTLY ONCE, BEFORE any context is acquired, and its canonical text is the only evidence
/// the write path ever uses; the row identities come from DECODING that just-encoded text, never
/// from re-reading the caller's graph. A mutation the caller (or a factory callback) makes after
/// the encode has begun therefore cannot change what is stored, and a refused receipt can never
/// leave a partial row behind. The first-stored timestamp is generated once, from
/// <see cref="TimeProvider.GetUtcNow"/>, and written with the invariant round-trip (<c>"O"</c>)
/// format because raw SQL bypasses the EF value converter that would otherwise produce exactly
/// that text.
/// </para>
/// <para>
/// READ ERRORS AND WRITE UNCERTAINTY ARE DIFFERENT TRUTHS, AND STAY DIFFERENT.
/// <see cref="CompletionReceiptWriteResult.Indeterminate"/> is reserved for the INSERT itself — a
/// throw from the statement, or an affected-row count other than 0/1 — and carries the EXACT caught
/// exception object (or an <see cref="InvalidOperationException"/> describing the count). It makes
/// NO claim about rollback or about whether anything was mutated. Everything AFTER a confirmed
/// zero-row insert — a vanished duplicate row, a malformed or unsupported payload, a row/payload
/// identity mismatch, an unmaterializable timestamp, a failed read query — THROWS as a read or
/// integrity error and is never dressed up as a write outcome with a fabricated exception. The
/// uncertainty handler wraps the INSERT call alone, so those errors cannot be swallowed by it.
/// </para>
/// <para>
/// SCOPE, HONESTLY. <see cref="CompletionReceiptWriteStatus.Stored"/> is EVIDENCE RETENTION ONLY:
/// it is not durable worker authorization, it does not prove the worker owned the attempt, and it
/// does not advance a phase. This slice has NO production DI registration and NO production callers.
/// There is no enumeration, no processed marker, no receipt deletion, no registry claim, no pointer
/// release, no acknowledgement/replay and no lifetime cleanup — deliberate pipeline-reset task-id
/// reuse is a later integration concern, and the store performs no hidden reconciliation of any
/// kind: a caller that saw <see cref="CompletionReceiptWriteStatus.Indeterminate"/> owns the retry.
/// </para>
/// </summary>
internal sealed class CompletionReceiptStore
{
    private readonly IDbContextFactory<CopilotHiveDbContext> _dbContextFactory;
    private readonly ILogger<CompletionReceiptStore> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// THE DISPOSAL SEAM for the factory-owned contexts. <see cref="CopilotHiveDbContext"/> is
    /// sealed, so its <c>Dispose</c> cannot be overridden and EF never closes an externally
    /// supplied connection — the failure is otherwise not genuinely injectable. When installed, it
    /// SUBSTITUTES the fallible dispose operation; the GUARD itself (the try/catch, the
    /// <c>context-dispose</c> warning, the swallow and the outcome preservation) remains production
    /// code the tests exercise for real, and the null-default branch calls the REAL
    /// <c>Dispose()</c>, so every non-injected caller is unchanged. Instance-scoped on purpose:
    /// each store instance carries its own seam (no static, no cross-test pollution). A test that
    /// injects a failing disposer still owns the real resources through its factory, so nothing is
    /// leaked by the injection.
    /// </summary>
    internal Action<CopilotHiveDbContext>? ContextDisposerForTest;

    /// <summary>
    /// THE ONE WRITE STATEMENT. Narrow, fully parameterized, and conflict-tolerant: the
    /// <c>ON CONFLICT(task_id) DO NOTHING</c> clause makes a competing insert a ZERO-ROW result
    /// rather than an error, so the duplicate decision is made by the database itself.
    /// </summary>
    private const string InsertStatement =
        """
        INSERT INTO completion_receipts (task_id, goal_id, payload_json, first_stored_at_utc)
        VALUES (@taskId, @goalId, @payloadJson, @firstStoredAtUtc)
        ON CONFLICT(task_id) DO NOTHING
        """;

    private const string InsertUncertainTemplate =
        "CompletionReceiptStore: insert-uncertain task={TaskId} — the completion-receipt INSERT did not confirm; nothing is inferred about the row's presence: {Message}";

    private const string ContextDisposeTemplate =
        "CompletionReceiptStore: context-dispose task={TaskId} — the factory-owned context dispose failed; the recorded outcome stands: {Message}";

    /// <summary>
    /// Initialises a new <see cref="CompletionReceiptStore"/> over a DbContext factory. There is
    /// deliberately NO borrowed/direct-context mode: every operation owns one short-lived context
    /// created by the factory and disposes it before returning.
    /// </summary>
    /// <param name="dbContextFactory">Factory used to create transient <see cref="CopilotHiveDbContext"/> instances.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="timeProvider">Clock for the first-stored timestamp; defaults to <see cref="TimeProvider.System"/>.</param>
    internal CompletionReceiptStore(
        IDbContextFactory<CopilotHiveDbContext> dbContextFactory,
        ILogger<CompletionReceiptStore> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dbContextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger.LogInformation("CompletionReceiptStore initialized with DbContext factory");
    }

    /// <summary>
    /// Stores a completion receipt EXACTLY ONCE, reporting one of four explicit outcomes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE ORDER IS THE CONTRACT. The argument guard, the SINGLE <see cref="CompletionReceiptCodec.Encode"/>,
    /// the decode of that canonical text (the authoritative row identities), the single timestamp
    /// and the context acquisition all happen BEFORE any write attempt: a null argument, a codec
    /// refusal, a clock failure or an acquisition failure PROPAGATES the exact exception having
    /// created no row and issued no statement — it is never reported as a write outcome.
    /// </para>
    /// <para>
    /// THE INSERT-ONLY UNCERTAINTY HANDLER wraps <c>ExecuteSqlRaw</c> and nothing else. A throw
    /// there returns <see cref="CompletionReceiptWriteStatus.Indeterminate"/> carrying that EXACT
    /// exception object; an affected-row count other than 0/1 returns
    /// <see cref="CompletionReceiptWriteStatus.Indeterminate"/> carrying an
    /// <see cref="InvalidOperationException"/> that names the count. Neither claims rollback and
    /// neither claims that no row exists.
    /// </para>
    /// <para>
    /// THE ZERO-ROW PATH is a read, and reads are authoritative: the existing row is read
    /// <c>AsNoTracking</c>, decoded with the existing codec, checked for row/payload identity
    /// agreement and re-encoded. A vanished row, a corrupt payload, an identity mismatch or a
    /// failed read THROWS. A VALID row whose canonical text equals the captured candidate text is
    /// <see cref="CompletionReceiptWriteStatus.AlreadyStored"/> (its bytes and its first-stored time
    /// are untouched); any actually differing receipt is
    /// <see cref="CompletionReceiptWriteStatus.Conflict"/>, which also changes nothing.
    /// </para>
    /// </remarks>
    /// <param name="candidate">The receipt to store.</param>
    /// <returns>The recorded write outcome, with exception evidence only for the uncertain one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <c>null</c>.</exception>
    /// <exception cref="CompletionReceiptCodecException">The candidate cannot be represented as canonical version-1 evidence.</exception>
    /// <exception cref="InvalidOperationException">
    /// The duplicate row is missing, its payload is unusable, its row/payload identities disagree,
    /// its timestamp cannot be materialized, or the read query itself failed.
    /// </exception>
    internal CompletionReceiptWriteResult InsertOnce(CompletionReceipt candidate)
    {
        // PHASE 0 — THE ARGUMENT GUARD, before anything else exists.
        ArgumentNullException.ThrowIfNull(candidate);

        // PHASE 1 — THE SINGLE ENCODE, before any database interaction. This is the FREEZE: the
        // canonical text is captured (and the receipt validated) ONCE and is the only evidence the
        // write path ever uses. The caller's graph is never read again below.
        var canonical = CompletionReceiptCodec.Encode(candidate);

        // PHASE 2 — THE AUTHORITATIVE ROW IDENTITY, taken from DECODING the just-encoded canonical
        // text rather than from the caller's graph: the ids written into the row are provably the
        // ones the stored payload carries.
        var authoritative = CompletionReceiptCodec.Decode(canonical);
        var taskId = authoritative.Slot.TaskId;
        var goalId = authoritative.GoalId;

        // PHASE 3 — THE ONE TIMESTAMP, formatted with the invariant round-trip format because the
        // raw SQL bypasses the EF value converter that would otherwise write exactly this text.
        var firstStoredText = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        CopilotHiveDbContext? db = null;
        try
        {
            // PHASE 4 — THE CONTEXT. An acquisition failure propagates with nothing to clean up.
            db = _dbContextFactory.CreateDbContext();

            // PHASE 5 — THE ONE INSERT. ONLY this call sits inside the uncertainty handler: a throw
            // here (or an unexpected affected-row count) IS the write-uncertainty truth and the
            // EXACT caught object is retained. The read path below is deliberately OUTSIDE the
            // handler, so a read or integrity failure can never be reported as write uncertainty.
            int inserted;
            try
            {
                inserted = db.Database.ExecuteSqlRaw(
                    InsertStatement,
                    new SqliteParameter("@taskId", taskId),
                    new SqliteParameter("@goalId", goalId),
                    new SqliteParameter("@payloadJson", canonical),
                    new SqliteParameter("@firstStoredAtUtc", firstStoredText));
            }
            catch (Exception insertException)
            {
                SafeWarning(InsertUncertainTemplate, taskId, insertException);
                return CompletionReceiptWriteResult.Indeterminate(insertException);
            }

            if (inserted == 1)
            {
                // THE ONLY SUCCESS COUNT — a confirmed, returned statement result.
                return CompletionReceiptWriteResult.Stored();
            }

            if (inserted != 0)
            {
                // NOT a success and NOT a duplicate: every other count — a negative provider count
                // just as much as a count above one — is an integrity surprise the caller must see.
                var unexpected = new InvalidOperationException(
                    $"Completion receipt insert affected {inserted} rows for task '{taskId}' (expected exactly 0 or 1).");
                SafeWarning(InsertUncertainTemplate, taskId, unexpected);
                return CompletionReceiptWriteResult.Indeterminate(unexpected);
            }

            // PHASE 6 — THE ZERO-ROW PATH: the primary key arbitrated the insert, so a row exists.
            // Every failure below is a READ/INTEGRITY error and THROWS — never Conflict, never
            // AlreadyStored, never Indeterminate.
            var existing = FindRow(db, taskId)
                ?? throw new InvalidOperationException(
                    $"Completion receipt row for task '{taskId}' is missing after a zero-row insert.");

            var stored = Materialize(existing);

            // THE ORDINAL COMPARISON of canonical texts: first-stored time is NOT part of the
            // payload and NOT part of this decision, so an equivalent-but-differently-formatted
            // stored payload is the SAME receipt.
            return string.Equals(CompletionReceiptCodec.Encode(stored.Receipt), canonical, StringComparison.Ordinal)
                ? CompletionReceiptWriteResult.AlreadyStored()
                : CompletionReceiptWriteResult.Conflict();
        }
        finally
        {
            // THE GUARDED CLEANUP — the outcome or the propagating exception stays authoritative
            // even when disposal (or the warning it logs) fails.
            if (db is not null)
                DisposeOwnedContext(db, taskId);
        }
    }

    /// <summary>
    /// Loads the stored completion receipt for a task, together with the instant it was FIRST
    /// stored. Returns <c>null</c> ONLY when no row exists.
    /// </summary>
    /// <remarks>
    /// The returned receipt is DETACHED and freshly allocated (the codec's decode is a deep copy),
    /// so mutating its lists cannot affect any later load. The same validation the zero-row write
    /// path applies is applied here: a malformed or unsupported payload, a row/payload identity
    /// mismatch, an unmaterializable timestamp and a failed read query all THROW. Absence is the
    /// only <c>null</c>.
    /// </remarks>
    /// <param name="taskId">The task id whose receipt to load; must be non-blank.</param>
    /// <returns>The detached receipt and its first-stored UTC instant, or <c>null</c> when no row exists.</returns>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> is null, empty or blank.</exception>
    /// <exception cref="CompletionReceiptCodecException">The stored payload is malformed or unsupported.</exception>
    /// <exception cref="InvalidOperationException">The row/payload identities disagree, the timestamp cannot be materialized, or the read failed.</exception>
    internal CompletionReceiptReadResult? Load(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        CopilotHiveDbContext? db = null;
        try
        {
            db = _dbContextFactory.CreateDbContext();

            var entity = FindRow(db, taskId);

            // ABSENCE IS THE ONLY NULL — nothing else is ever reported as "no receipt".
            return entity is null ? null : Materialize(entity);
        }
        finally
        {
            if (db is not null)
                DisposeOwnedContext(db, taskId);
        }
    }

    /// <summary>
    /// THE ONE READ SHAPE both operations share: the row is read <c>AsNoTracking</c> (no tracked
    /// entity can escape the operation), decoded with the existing codec, and validated — the row's
    /// task id and goal id must agree ORDINALLY with the decoded payload's, and the first-stored
    /// column must materialize. Every failure here is a read/integrity error and propagates.
    /// </summary>
    private static CompletionReceiptEntity? FindRow(CopilotHiveDbContext db, string taskId) =>
        db.CompletionReceipts.AsNoTracking().FirstOrDefault(entity => entity.TaskId == taskId);

    /// <summary>
    /// Materializes a stored row into the read value: decodes the payload through the EXISTING
    /// codec (never a duplicated validation matrix), proves the row/payload identity agreement
    /// ordinally, and carries the first-stored instant. The timestamp is read through the entity's
    /// value converter, so an unparseable column surfaces here as a read failure rather than as a
    /// silently defaulted value.
    /// </summary>
    private static CompletionReceiptReadResult Materialize(CompletionReceiptEntity entity)
    {
        var receipt = CompletionReceiptCodec.Decode(entity.PayloadJson);

        if (!string.Equals(entity.TaskId, receipt.Slot.TaskId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Completion receipt row for task '{entity.TaskId}' carries a payload whose task id is " +
                $"'{receipt.Slot.TaskId}' — the row and its payload disagree.");
        }

        if (!string.Equals(entity.GoalId, receipt.GoalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Completion receipt row for task '{entity.TaskId}' carries goal id '{entity.GoalId}' but its " +
                $"payload carries goal id '{receipt.GoalId}' — the row and its payload disagree.");
        }

        return new CompletionReceiptReadResult(receipt, entity.FirstStoredAtUtc);
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
    /// THE FINAL GUARD of the never-masked guarantee for the cleanup diagnostics: the whole write —
    /// the message access INCLUDED — sits inside its own no-throw guard, so an exception whose
    /// <see cref="Exception.Message"/> getter itself throws (the message is a virtual property,
    /// injectable through the disposal seam) cannot escape and mask the authoritative outcome. The
    /// logger is equally guarded: a throwing logger is silently swallowed, because there is no
    /// deeper channel to report to.
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
    /// <c>Message</c> getter throws yields a static placeholder, so the diagnostic degrades while
    /// the never-masked guarantee does not.
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
/// The recorded outcome of <see cref="CompletionReceiptStore.InsertOnce"/> — four explicit truths.
/// </summary>
internal enum CompletionReceiptWriteStatus
{
    /// <summary>
    /// EXACTLY ONE row was inserted, with a confirmed returned statement result. This is evidence
    /// retention only — not durable worker authorization and not a phase advancement.
    /// </summary>
    Stored,

    /// <summary>
    /// ZERO rows were inserted and the VALID existing row is the same receipt: its canonical
    /// context and result match the candidate's canonical text ordinally. The stored bytes and the
    /// first-stored time are left exactly as they were.
    /// </summary>
    AlreadyStored,

    /// <summary>
    /// ZERO rows were inserted and the VALID existing row is a DIFFERENT receipt — a different
    /// canonical context or result, including a different goal, worker or attempt. Nothing is
    /// changed: neither the payload nor the first-stored time.
    /// </summary>
    Conflict,

    /// <summary>
    /// THE WRITE IS UNRESOLVED: the INSERT threw, or it returned an affected-row count other than
    /// 0/1. NO claim is made about rollback or about whether a row now exists; the caller owns any
    /// retry, and the store performs no hidden reconciliation.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// The result of <see cref="CompletionReceiptStore.InsertOnce"/>: the recorded
/// <see cref="Status"/> plus, for the uncertain outcome only, the EXACT exception evidence.
/// </summary>
/// <remarks>
/// EXCEPTION IDENTITY, not description: <see cref="WriteException"/> carries the exact caught
/// instance (any EF wrapper included) for a throwing INSERT, or the
/// <see cref="InvalidOperationException"/> describing an unexpected affected-row count. It is
/// <c>null</c> for <see cref="CompletionReceiptWriteStatus.Stored"/>,
/// <see cref="CompletionReceiptWriteStatus.AlreadyStored"/> and
/// <see cref="CompletionReceiptWriteStatus.Conflict"/> — read and integrity errors THROW instead of
/// appearing here.
/// </remarks>
/// <param name="Status">The recorded outcome.</param>
/// <param name="WriteException">The exact write-uncertainty evidence, or <c>null</c>.</param>
internal sealed record CompletionReceiptWriteResult(
    CompletionReceiptWriteStatus Status,
    Exception? WriteException)
{
    /// <summary>The confirmed single-row insert — no exception evidence.</summary>
    internal static CompletionReceiptWriteResult Stored() =>
        new(CompletionReceiptWriteStatus.Stored, null);

    /// <summary>The confirmed equivalent duplicate — no exception evidence.</summary>
    internal static CompletionReceiptWriteResult AlreadyStored() =>
        new(CompletionReceiptWriteStatus.AlreadyStored, null);

    /// <summary>The confirmed differing receipt — no exception evidence.</summary>
    internal static CompletionReceiptWriteResult Conflict() =>
        new(CompletionReceiptWriteStatus.Conflict, null);

    /// <summary>The unresolved write, retaining the exact evidence object as caught.</summary>
    /// <param name="writeException">The exact caught (or count-describing) exception.</param>
    internal static CompletionReceiptWriteResult Indeterminate(Exception writeException) =>
        new(CompletionReceiptWriteStatus.Indeterminate, writeException);
}

/// <summary>
/// The value <see cref="CompletionReceiptStore.Load"/> returns for a row that EXISTS: the DETACHED,
/// freshly allocated receipt plus the instant the payload was FIRST stored (UTC).
/// </summary>
/// <param name="Receipt">The decoded receipt; it shares no list with any stored or caller-owned object.</param>
/// <param name="FirstStoredAtUtc">The row's first-stored UTC instant, preserved exactly.</param>
internal sealed record CompletionReceiptReadResult(
    CompletionReceipt Receipt,
    DateTime FirstStoredAtUtc);
