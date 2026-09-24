using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Persistence;

/// <summary>
/// JSON converter for legacy numeric keys in PhaseInstructions dictionaries.
/// Old pipelines stored integer enum values ("0", "1", etc.) as keys.
/// This converter converts them to lowercase phase names for backward compatibility.
/// </summary>
internal sealed class LegacyPhaseInstructionsConverter : JsonConverter<Dictionary<string, string>>
{
    // Map from GoalPhase enum ordinal to lowercase name
    private static readonly string[] PhaseOrdinalToName =
    [
        "planning",  // 0
        "coding",     // 1
        "review",     // 2
        "testing",    // 3
        "docwriting", // 4
        "improve",    // 5
        "merging",    // 6
    ];

    public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return null;

        var result = new Dictionary<string, string>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var key = reader.GetString() ?? "";
            var value = "";

            if (reader.Read() && reader.TokenType == JsonTokenType.String)
            {
                value = reader.GetString() ?? "";
            }

            // Convert legacy numeric keys to lowercase phase names
            if (int.TryParse(key, out var ordinal) && ordinal >= 0 && ordinal < PhaseOrdinalToName.Length)
            {
                key = PhaseOrdinalToName[ordinal];
            }

            result[key] = value;
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var kvp in value)
        {
            writer.WriteString(kvp.Key, kvp.Value);
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// Persists GoalPipeline state via EF Core so the orchestrator can recover after restarts.
/// Uses an <see cref="IDbContextFactory{TContext}"/> in production, creating a short-lived
/// context per operation. A test constructor accepts a single owned context directly.
/// </summary>
public sealed class PipelineStore : IAsyncDisposable
{
    private readonly IDbContextFactory<CopilotHiveDbContext>? _dbContextFactory;
    private readonly CopilotHiveDbContext? _directDbContext;
    private readonly ILogger<PipelineStore> _logger;

    /// <summary>
    /// THE DISPOSAL SEAM for the factory-owned contexts, used by the admission/ownership
    /// TRANSACTION paths that resolve a per-operation context AND dispose it through this seam:
    /// the admission route (<c>SaveAdmissionWithPointer</c>, both forms, which share one transaction
    /// body), the ownership-transaction family that commits a detached ownership candidate or its
    /// durable inverse (<see cref="CommitAdmissionOwnership"/> and
    /// <c>CommitPendingAdmissionRollback</c>, through their shared scaffolding), and the
    /// ownership-checked pointer clear <see cref="ClearActiveTaskIdIfMatches"/>. It is deliberately
    /// NOT the disposal point of every context-resolving operation: the ordinary saves and reads here
    /// dispose a factory-owned context directly, without this seam. When
    /// installed, it SUBSTITUTES the fallible
    /// dispose operation (<see cref="CopilotHiveDbContext"/> is sealed, so its
    /// <c>Dispose</c> cannot be overridden and EF never closes an externally supplied
    /// connection — the failure is otherwise not genuinely injectable). The GUARD itself —
    /// the try/catch, the <c>admission-context-dispose</c> warning, the swallow and the
    /// outcome preservation — remains production code the tests exercise for real, and the
    /// null-default branch calls the REAL <c>Dispose()</c>, so every non-injected caller and
    /// the production path are unchanged. Instance-scoped on purpose: each store instance
    /// carries its own seam (no static, no cross-test pollution).
    /// </summary>
    internal Action<CopilotHiveDbContext>? ContextDisposerForTest;

    /// <summary>Test seam: substitutes the tracker-detach step for the hygiene phases of
    /// ClearActiveTaskIdIfMatches (whose null-default is the single-entry DetachTrackedPipeline)
    /// and CommitAdmissionOwnership (whose null-default is the all-states
    /// DetachTrackedPipelinesForGoal). Null in production (the real detach runs); a test-installed
    /// delegate replaces the whole step (the forced-failure injection).</summary>
    internal Action<CopilotHiveDbContext, string>? TrackerDetachForTest;

    private const string PointerRollbackFailureTemplate =
        "WorkSlotIntegrity: pointer-rollback-failure goal={GoalId} task={TaskId} — the persisted pointer's rollback failed; a restart may restore the stale pointer; the completion-protocol successor owns the durable reconciliation";
    private const string PointerRollbackCleanupTemplate =
        "WorkSlotIntegrity: pointer-rollback-cleanup goal={GoalId} task={TaskId} — the post-update cleanup failed; pointer-cleared={Cleared}; the context state is suspect";
    private const string CheckpointCleanupCompletedTemplate =
        "WorkSlotIntegrity: ownership-checkpoint-cleanup goal={GoalId} — the failed checkpoint's tracker hygiene completed ({PrimaryMessage})";
    private const string CheckpointCleanupReloadFailedTemplate =
        "WorkSlotIntegrity: ownership-checkpoint-cleanup goal={GoalId} — the existing-row reload failed, so the rejected checkpoint entry was DETACHED instead; the context's safe reuse is SUSPECT and no conversation preservation is claimed: {Message}";
    private const string CheckpointCleanupRowMissingTemplate =
        "WorkSlotIntegrity: ownership-checkpoint-cleanup goal={GoalId} — the reload found NO durable row, so EF detached the principal; the rejected checkpoint cannot be flushed, but no dependent preservation and no reusable tracking are claimed: {Message}";
    private const string CheckpointCleanupSuspectTemplate =
        "WorkSlotIntegrity: ownership-checkpoint-cleanup goal={GoalId} — the failed checkpoint's tracker hygiene did not complete; the context state is SUSPECT and its further usability is NOT guaranteed, so no safe reuse and no conversation preservation are claimed: {Message}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new LegacyPhaseInstructionsConverter() },
    };

    /// <summary>
    /// Initialises a new <see cref="PipelineStore"/> using a DbContext factory (production/DI).
    /// </summary>
    /// <param name="dbContextFactory">Factory used to create transient <see cref="CopilotHiveDbContext"/> instances.</param>
    /// <param name="logger">Logger instance.</param>
    public PipelineStore(IDbContextFactory<CopilotHiveDbContext> dbContextFactory, ILogger<PipelineStore> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _logger.LogInformation("PipelineStore initialized with DbContext factory");
    }

    /// <summary>
    /// Initialises a new <see cref="PipelineStore"/> using a single owned <see cref="CopilotHiveDbContext"/>.
    /// Intended for testing. The store does NOT dispose the context; the test owns it.
    /// </summary>
    /// <param name="dbContext">An open context. The store does not take ownership.</param>
    /// <param name="logger">Logger instance.</param>
    internal PipelineStore(CopilotHiveDbContext dbContext, ILogger<PipelineStore> logger)
    {
        _directDbContext = dbContext;
        _logger = logger;
        _logger.LogInformation("PipelineStore initialized with existing context");
    }

    /// <summary>
    /// Resolves a context for an operation. When a direct (test-owned) context is set, returns it
    /// with <c>ownsContext = false</c> so the caller does not dispose it. Otherwise creates a transient
    /// context via the factory with <c>ownsContext = true</c>.
    /// </summary>
    private (CopilotHiveDbContext Db, bool OwnsContext) ResolveDbContext()
    {
        if (_directDbContext is not null)
            return (_directDbContext, false);
        return (_dbContextFactory!.CreateDbContext(), true);
    }

    /// <summary>Insert or replace the full pipeline state.</summary>
    /// <remarks>
    /// <para>
    /// THE CONVERSATION-REPLACEMENT OWNERSHIP CONTRACT. Conversation replacement stages through
    /// <c>SaveConversationCore</c>, which DELETES the goal's durable entries and ADDS the pipeline's
    /// own. From the instant that replacement is ATTEMPTED, this operation OWNS the target goal's
    /// conversation tracking scope: a failure cleanup DISCARDS all tracked
    /// <see cref="ConversationEntryEntity"/> state for that goal — the CALLER's already-pending edits
    /// for that same goal INCLUDED — and does NOT restore a previous caller unit of work. Other
    /// goals' conversations and every other entity type and pending value are untouched, and a
    /// cross-goal reassignment of tracked conversation entities is outside the supported scope.
    /// Preserving same-goal preexisting caller edits would need an EF snapshot/restore framework,
    /// which is deliberately NOT built here.
    /// </para>
    /// <para>
    /// THE CLEANUP ITSELF is a targeted EF tracking detach only — no SQL, no conversation reload, no
    /// <c>SaveChanges</c> and no whole-tracker clear. It is TRACKING HYGIENE, not durable rollback: it
    /// does not claim that a thrown <c>SaveChanges</c> left the database unchanged.
    /// </para>
    /// </remarks>
    public void SavePipeline(GoalPipeline pipeline)
    {
        var (db, ownsContext) = ResolveDbContext();

        // THE ATTEMPT FLAG, set BEFORE the staging call so a throw from INSIDE SaveConversationCore
        // (a partial staging: deletes already tracked, maybe an addition too) is covered as well.
        var conversationReplacementAttempted = false;
        try
        {
            UpsertPipelineCore(db, pipeline);
            conversationReplacementAttempted = true;
            SaveConversationCore(db, pipeline);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            // THE CONVERSATION-SCOPE CLEANUP, independent of the diagnostics below: only a BORROWED
            // (caller-owned) context survives this call, so only there can rejected conversation
            // tracking be flushed later; and only once replacement was ATTEMPTED can any of the
            // goal's conversation tracking be this operation's to discard.
            if (!ownsContext && conversationReplacementAttempted)
                OnConversationReplacementFailure(db, pipeline.GoalId, ex);

            // THE GUARDED DIAGNOSTIC: the emit is wrapped locally so a throwing ILogger can never
            // replace the primary exception bare-thrown on the next line — the level, template,
            // arguments and the EXACT exception object are unchanged.
            try
            {
                _logger.LogError(ex, "Failed to save pipeline for goal {GoalId}", pipeline.GoalId);
            }
            catch
            {
                // SILENT swallow — the primary exception stays authoritative.
            }

            throw;
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>Persist only the pipeline's scalar state (phase, iteration, retries, etc.).</summary>
    public void SavePipelineState(GoalPipeline pipeline)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            UpsertPipelineCore(db, pipeline);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save pipeline state for goal {GoalId}", pipeline.GoalId);
            throw;
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>
    /// THE ELIGIBLE ORDINARY CHECKPOINT, FULL SAVE: persists the pipeline exactly as
    /// <see cref="SavePipeline(GoalPipeline)"/> does — scalars, machine position AND the
    /// conversation — but writes the ACTIVE POINTER and the COMPLETE WORK-SLOT REGISTRY (slots and
    /// per-position counters) from the ONE detached <paramref name="ownership"/> capture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE POINT OF THE PAIR. <paramref name="ownership"/> is captured ONCE under the pipeline's own
    /// monitor by the caller (<c>GoalPipeline.CaptureAdmissionOwnership</c>) and handed over
    /// ALREADY DETACHED, so the pointer column and the registry column of this write come from the
    /// SAME instant. A captured <c>null</c> pointer is written as SQL NULL — never re-read live —
    /// and the registry is the capture's own value, so a domain mutation landing after the capture
    /// cannot reach this row. Ordinary checkpoints may legitimately carry Pending, Claimed, Recorded
    /// and Abandoned slots and a null pointer; no Pending-only validation is applied here.
    /// </para>
    /// <para>
    /// VALIDATE-AND-ENCODE BEFORE ANY DATABASE TOUCH. The carrier's goal must equal the pipeline's
    /// (<see cref="ArgumentException"/> with no context and no statement otherwise), and
    /// <see cref="WorkSlotRegistryCodec.Encode"/> runs EXACTLY ONCE here — BEFORE a context is
    /// resolved — so an encoding failure propagates having created no context and issued no write.
    /// No validator is duplicated, no schema is added and no task ID is parsed.
    /// </para>
    /// <para>
    /// ONE ROW WRITE. Both captured values travel through the existing pipeline-row upsert and are
    /// flushed by that row's single <c>SaveChanges</c>. The legacy
    /// <see cref="SaveWorkSlotRegistry"/> is deliberately NOT called afterwards: this checkpoint is
    /// not two independent writes.
    /// </para>
    /// <para>
    /// SCOPE, honestly — this is a CHECKPOINT, not whole-pipeline atomicity. Only the pointer and
    /// the registry share the one capture; every other mutable field (plan, phase, metrics, the
    /// phase log, the conversation) keeps its existing capture timing and may change afterwards.
    /// The result is not a durably-consistent whole-pipeline snapshot, no admission/rollback
    /// semantics are changed, and no checkpoint written here authorizes recovery: a HELD restore
    /// hydrates a stored registry as EVIDENCE ONLY (see
    /// <see cref="GoalPipeline.RestoredRegistryClassification"/>) and never as permission to
    /// dispatch, replay or resume. A throwing
    /// <c>SaveChanges</c> does NOT by itself prove a rollback: after-commit/provider uncertainty is
    /// the existing limitation.
    /// </para>
    /// <para>
    /// THE CONVERSATION-REPLACEMENT OWNERSHIP CONTRACT is the legacy full save's, verbatim: once
    /// conversation replacement is ATTEMPTED, this operation OWNS the target goal's conversation
    /// tracking scope, and a failure cleanup DISCARDS all tracked
    /// <see cref="ConversationEntryEntity"/> state for that goal — the CALLER's already-pending
    /// same-goal edits INCLUDED — without restoring a previous caller unit of work. Other goals'
    /// conversations and every other entity type and pending value are untouched; cross-goal
    /// reassignment of tracked conversation entities is outside the supported scope. Because the
    /// context is BORROWED here rather than factory-owned, this is precisely the route where
    /// rejected conversation tracking would otherwise outlive the failed call. It is tracking
    /// hygiene only — no SQL, no reload, no <c>SaveChanges</c>, no whole-tracker clear — and it runs
    /// BEFORE the pipeline checkpoint hook, which keeps its own cleanup unchanged.
    /// </para>
    /// </remarks>
    /// <param name="pipeline">The pipeline whose row receives the ordinary fields and the checkpoint.</param>
    /// <param name="ownership">The detached ownership capture supplying BOTH columns.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> or
    /// <paramref name="ownership"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The carrier's goal does not match the pipeline's.</exception>
    /// <exception cref="WorkSlotRegistryCodecException">The captured registry cannot be encoded.</exception>
    internal void SavePipeline(GoalPipeline pipeline, AdmissionOwnershipSnapshot ownership)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var checkpoint = FreezeOwnershipCheckpoint(pipeline, ownership);

        var (db, ownsContext) = ResolveDbContext();

        // THE ATTEMPT FLAG, set BEFORE the staging call so a throw from INSIDE SaveConversationCore
        // (a partial staging: deletes already tracked, maybe an addition too) is covered as well.
        var conversationReplacementAttempted = false;
        try
        {
            UpsertPipelineCore(db, pipeline, activeTaskIdOverride: null, checkpoint);
            conversationReplacementAttempted = true;
            SaveConversationCore(db, pipeline);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            // THE CONVERSATION-SCOPE CLEANUP — run INDEPENDENTLY and FIRST, ahead of the pipeline
            // checkpoint hook below, which keeps its pointer/blob hygiene exactly as it was. Same
            // contract as the legacy overload: only a borrowed context, and only once replacement
            // was attempted, so rejected conversation tracking cannot be flushed by a LATER state-only
            // save on this same caller-owned context. Tracking hygiene only — no SQL, no reload, no
            // SaveChanges, no whole-tracker clear.
            if (!ownsContext && conversationReplacementAttempted)
                OnConversationReplacementFailure(db, pipeline.GoalId, ex);

            // THE BORROWED-CONTEXT HYGIENE: a failed row write must not leave a half-staged
            // checkpoint in the tracker, where a LATER legacy save on the same caller-owned context
            // could flush the rejected blob. An Unchanged/Modified entry is reloaded IN PLACE, which
            // discards its pending pointer/registry values for database truth without detaching the
            // principal (so its pending conversation dependents are not lost); Added/Deleted entries,
            // and any entry whose reload threw, are DETACHED — a fallback that makes no preservation
            // claim. The primary exception is preserved and rethrown verbatim.
            OnCheckpointSaveFailure(db, pipeline.GoalId, ex);
            // THE GUARDED DIAGNOSTIC: the emit goes through the file's existing no-throw helper, so
            // a throwing ILogger can never replace the row-write exception rethrown on the next line
            // — which is exactly the guarantee this method's contract makes. The primary exception's
            // own text is read through the existing no-throw guard for the same reason.
            BestEffortWarning(
                "Failed to save pipeline for goal {GoalId} — the ownership checkpoint's row write failed; the primary exception is rethrown unchanged: {PrimaryMessage}",
                pipeline.GoalId, CleanupMessageOrPlaceholder(ex));
            throw;
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>
    /// THE ELIGIBLE ORDINARY CHECKPOINT, STATE SAVE: the scalar-only sibling of
    /// <see cref="SavePipeline(GoalPipeline, AdmissionOwnershipSnapshot)"/>. It writes the SAME
    /// pointer/registry checkpoint in the SAME single row write, but — like
    /// <see cref="SavePipelineState(GoalPipeline)"/> — it never touches the conversation.
    /// </summary>
    /// <remarks>
    /// Scope and error semantics are those of the full-save overload; the ONLY difference is the
    /// absent conversation write, which is what keeps a state-only save conversation-neutral.
    /// </remarks>
    /// <param name="pipeline">The pipeline whose row receives the ordinary fields and the checkpoint.</param>
    /// <param name="ownership">The detached ownership capture supplying BOTH columns.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> or
    /// <paramref name="ownership"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The carrier's goal does not match the pipeline's.</exception>
    /// <exception cref="WorkSlotRegistryCodecException">The captured registry cannot be encoded.</exception>
    internal void SavePipelineState(GoalPipeline pipeline, AdmissionOwnershipSnapshot ownership)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var checkpoint = FreezeOwnershipCheckpoint(pipeline, ownership);

        var (db, ownsContext) = ResolveDbContext();
        try
        {
            UpsertPipelineCore(db, pipeline, activeTaskIdOverride: null, checkpoint);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            OnCheckpointSaveFailure(db, pipeline.GoalId, ex);
            // THE GUARDED DIAGNOSTIC: see the full-save overload — a throwing ILogger must never
            // replace the primary row-write exception rethrown on the next line.
            BestEffortWarning(
                "Failed to save pipeline state for goal {GoalId} — the ownership checkpoint's row write failed; the primary exception is rethrown unchanged: {PrimaryMessage}",
                pipeline.GoalId, CleanupMessageOrPlaceholder(ex));
            throw;
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>
    /// Validates the carrier's goal and ENCODES the captured registry EXACTLY ONCE — both BEFORE
    /// any database interaction. The encode is the single point where a malformed capture can fail,
    /// and it fails with no context created and no statement issued: an encoding failure can never
    /// touch the database.
    /// </summary>
    /// <param name="pipeline">The pipeline the carrier must belong to.</param>
    /// <param name="ownership">The detached capture to freeze.</param>
    /// <returns>The frozen pair the row write installs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ownership"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The carrier's goal differs from the pipeline's.</exception>
    /// <exception cref="WorkSlotRegistryCodecException">The captured registry cannot be encoded.</exception>
    private static FrozenOwnershipCheckpoint FreezeOwnershipCheckpoint(
        GoalPipeline pipeline, AdmissionOwnershipSnapshot? ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        // THE GOAL-IDENTITY GUARD: a carrier captured from a different pipeline would install a
        // foreign pointer and a foreign registry. It is detected from the carrier's own goal
        // identity — never by reading the database.
        if (!string.Equals(ownership.GoalId, pipeline.GoalId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The ownership capture's goal '{ownership.GoalId}' does not match the pipeline's goal '{pipeline.GoalId}'.",
                nameof(ownership));
        }

        // THE SINGLE ENCODE, before any context exists. A codec failure escapes here.
        return new FrozenOwnershipCheckpoint(ownership.ActiveTaskId, WorkSlotRegistryCodec.Encode(ownership.Registry));
    }

    /// <summary>
    /// THE BORROWED-CONTEXT HYGIENE on a failed checkpoint row write: the affected goal's tracked
    /// pipeline entries are put back into a state that CANNOT flush the rejected checkpoint later —
    /// WHEN THAT HYGIENE ACTUALLY COMPLETES (the reload succeeded, or the targeted fallback detach
    /// did). If BOTH the reload and its fallback fail, that guarantee does NOT hold: the rejected
    /// tracking may still be staged, the context is reported SUSPECT, and no safe-reuse or
    /// preservation claim is made.
    /// <para>
    /// WHY IT IS NEEDED: on the direct (test-owned) context path the pipeline entity stays tracked
    /// after a failed <c>SaveChanges</c>, still carrying the staged pointer/registry values. A
    /// subsequent LEGACY save on the SAME context (whose <c>Find</c> hands back that very tracked
    /// copy) would then flush the rejected blob as a side effect of an unrelated write.
    /// </para>
    /// <para>
    /// AN EXISTING-ROW ENTRY IS RELOADED IN PLACE, NOT DETACHED AND RE-FOUND. An
    /// Unchanged/Modified entry is refreshed through <c>EntityEntry.Reload</c> on THAT SAME entry:
    /// a successful reload restores the principal's property AND original values from database
    /// truth and leaves the entry Unchanged WITHOUT ever detaching it — so the dependents tracked
    /// under it (the caller's pending conversation work included) need not be lost, and the caller
    /// still observes the pipeline through the tracker. Neither <c>OriginalValues</c> nor a
    /// pre-call in-memory snapshot is replayed: with the row in front of it, neither is necessarily
    /// the durable pointer/blob baseline — the reload reads it. The entry is located by ORDINAL goal
    /// identity and the enumeration is MATERIALISED BEFORE the first tracking change.
    /// </para>
    /// <para>
    /// WHY THE OTHER STATES ARE NOT RELOADED. Unchanged/Modified does NOT by itself prove a durable
    /// row exists, and Deleted does NOT prove one is absent. Added/Deleted entries therefore keep
    /// the EXISTING rejection-DETACH (a blind reload of an Added entity with no row can be a no-op
    /// that leaves the rejected checkpoint insertable), and a reload that finds NO row at all makes
    /// EF detach the principal on its own — observed and reported, never dressed up as preservation.
    /// Those paths promise NEITHER dependent preservation NOR reusable tracking: no principal is
    /// reconstructed, no dependent is re-added and no temporary key is restored by hand — there is
    /// deliberately no EF state ledger here.
    /// </para>
    /// <para>
    /// GUARDED AND NEVER MASKING: a THROWING reload falls back to that same targeted detach under
    /// its OWN guard, so a read failure does not simply leave the rejected pointer/registry staged.
    /// Three honest outcomes are reported: a clean pass, a reload failure whose fallback DID remove
    /// the rejected change (no safe reuse and no conversation preservation claimed — the durable row
    /// was not restored), and a fault that could not be cleaned up at all (the SUSPECT/unconfirmed
    /// branch, where unconditional non-leakage is not promised either). In every case the PRIMARY
    /// exception keeps propagating unchanged. This is a targeted, key-scoped fix — deliberately NOT
    /// a general tracker-cleanup redesign, with no <c>SaveChanges</c>, no write, no retry and no
    /// compensation.
    /// </para>
    /// </summary>
    /// <param name="db">The context whose row write failed.</param>
    /// <param name="goalId">The affected goal.</param>
    /// <param name="primary">The failure being preserved (its message is logged only).</param>
    private void OnCheckpointSaveFailure(CopilotHiveDbContext db, string goalId, Exception primary)
    {
        try
        {
            // THE MATERIALISED LOOKUP: the loop below changes tracking, so the ordinal-goal matches
            // are enumerated ONCE, up front.
            var entries = db.ChangeTracker.Entries<PipelineEntity>()
                .Where(e => string.Equals(e.Entity.GoalId, goalId, StringComparison.Ordinal))
                .ToList();

            // THE FIRST CLEANUP FAULT IS THE REPORTED ONE: a reload failure names the reload (the
            // fallback it triggered must not overwrite that evidence), and only a fault whose
            // fallback ALSO failed escalates to the unconfirmed/leak-possible diagnostic.
            Exception? reloadFault = null;
            Exception? unconfirmedFault = null;

            // THE MISSING-ROW LIMITATION: a reload with no durable row does not throw — EF simply
            // DETACHES the principal. That is observed (the state transition), reported honestly,
            // and never turned into a preservation claim.
            var reloadFoundNoRow = false;

            foreach (var entry in entries)
            {
                if (entry.State is EntityState.Unchanged or EntityState.Modified)
                {
                    // THE EXISTING-ROW PATH — THE SAME ENTRY, RELOADED IN PLACE. The reload makes it
                    // Unchanged holding database truth without detaching the principal, so its
                    // tracked dependents survive; pending scalar edits on the principal are
                    // deliberately discarded to the durable row, exactly as the previous
                    // detach/re-find intended.
                    try
                    {
                        entry.Reload();
                        if (entry.State == EntityState.Detached)
                            reloadFoundNoRow = true;
                        continue;
                    }
                    catch (Exception reloadEx)
                    {
                        reloadFault ??= reloadEx;
                    }
                }

                // THE REJECTION-DETACH: Added/Deleted entries are DISCARDED (they are not reloaded —
                // see the summary's boundary note), and a FAILED reload falls back here under this
                // step's own guard, so the rejected pointer/registry cannot be flushed later.
                try
                {
                    db.Entry(entry.Entity).State = EntityState.Detached;
                }
                catch (Exception detachEx)
                {
                    unconfirmedFault ??= detachEx;
                }
            }

            if (reloadFault is null && unconfirmedFault is null && !reloadFoundNoRow)
            {
                // THE GUARDED DIAGNOSTIC: the emit goes through the file's existing no-throw helper,
                // so a throwing ILogger degrades to silence instead of turning a COMPLETED cleanup
                // into the SUSPECT branch (and it can never escape the caller's failure path).
                BestEffortWarning(
                    CheckpointCleanupCompletedTemplate,
                    goalId, CleanupMessageOrPlaceholder(primary));
            }
            else if (unconfirmedFault is null && reloadFoundNoRow)
            {
                // THE MISSING ROW: the rejected checkpoint is not flushable (EF detached the
                // principal itself), but the row the caller observed is GONE — so no dependent
                // preservation and no reusable tracking are claimed.
                BestEffortWarning(
                    CheckpointCleanupRowMissingTemplate,
                    goalId, CleanupMessageOrPlaceholder(primary));
            }
            else if (unconfirmedFault is null)
            {
                // THE FALLBACK HELD: the rejected change is off the tracker, but the context was not
                // restored to the durable row the caller observed — so no reuse/preservation claim.
                BestEffortWarning(
                    CheckpointCleanupReloadFailedTemplate,
                    goalId, CleanupMessageOrPlaceholder(reloadFault!));
            }
            else
            {
                // THE UNCONFIRMED CLEANUP: the rejected tracking itself may still be staged, so
                // unconditional non-leakage is NOT promised either.
                BestEffortWarning(
                    CheckpointCleanupSuspectTemplate,
                    goalId, CleanupMessageOrPlaceholder(unconfirmedFault));
            }
        }
        catch (Exception cleanupEx)
        {
            // Best-effort hygiene: the diagnostic must never replace the primary failure.
            BestEffortWarning(
                CheckpointCleanupSuspectTemplate,
                goalId, CleanupMessageOrPlaceholder(cleanupEx));
        }
    }

    /// <summary>
    /// THE CONVERSATION-SCOPE HYGIENE on a failed FULL save: EVERY tracked
    /// <see cref="ConversationEntryEntity"/> whose CURRENT <c>GoalId</c> equals the target goal is
    /// detached, so the rejected replacement cannot be flushed by a later state-only save on the same
    /// borrowed context.
    /// <para>
    /// WHY IT IS NEEDED: conversation replacement deletes the goal's durable entries and adds the
    /// pipeline's own. On the direct (caller-owned) context path those mutations stay tracked after a
    /// failed <c>SaveChanges</c> — and a following <see cref="SavePipelineState(GoalPipeline)"/> on the
    /// SAME context issues its own <c>SaveChanges</c>, which would flush every one of them even though
    /// it never asked for a conversation write.
    /// </para>
    /// <para>
    /// WHAT IT DISCARDS: the whole goal-scoped conversation unit of work — Added, Deleted, Modified
    /// and Unchanged entries, temporary-key additions included — because the operation that attempted
    /// the replacement owns that scope. A caller's own already-pending edit for the SAME goal is
    /// discarded too, and no previous caller unit of work is restored: this is a DELIBERATE narrow
    /// ownership transfer, not an EF snapshot/restore. Every OTHER goal's conversation and every other
    /// entity type and pending value is left tracked and untouched. The filter compares the entity's
    /// CURRENT <c>GoalId</c> ordinally; neither <c>GoalId</c> nor <c>Seq</c> is assumed to be the
    /// entity key.
    /// </para>
    /// <para>
    /// WHAT IT DOES NOT DO: no SQL, no conversation reload, no <c>SaveChanges</c> and no
    /// whole-<c>ChangeTracker</c> clear. Tracking hygiene only — so it makes no durable-rollback claim:
    /// a thrown <c>SaveChanges</c> may still have committed something underneath, and nothing here
    /// undoes an after-commit effect.
    /// </para>
    /// <para>
    /// GUARDED AND NEVER MASKING: the whole step is wrapped, so a cleanup fault degrades to a warning
    /// that reports the context as SUSPECT — not safely reusable — and the PRIMARY exception keeps
    /// propagating unchanged.
    /// </para>
    /// </summary>
    /// <param name="db">The borrowed context whose full save failed.</param>
    /// <param name="goalId">The goal whose conversation tracking scope is discarded.</param>
    /// <param name="primary">The failure being preserved (its message is logged only).</param>
    private void OnConversationReplacementFailure(CopilotHiveDbContext db, string goalId, Exception primary)
    {
        try
        {
            // The tracker is materialised ONCE and every matching entry detached in ANY state —
            // Added/Deleted/Modified/Unchanged, temporary-key additions included — because the goal's
            // conversation scope belongs to the failed operation while the replacement was attempted.
            var entries = db.ChangeTracker.Entries<ConversationEntryEntity>()
                .Where(e => string.Equals(e.Entity.GoalId, goalId, StringComparison.Ordinal))
                .ToList();

            foreach (var entry in entries)
                db.Entry(entry.Entity).State = EntityState.Detached;

            // THE GUARDED DIAGNOSTIC: the emit goes through the file's existing no-throw helper, so a
            // throwing ILogger degrades to silence rather than escaping the caller's failure path.
            BestEffortWarning(
                "WorkSlotIntegrity: full-save-conversation-cleanup goal={GoalId} — the rejected conversation replacement's tracker hygiene completed ({PrimaryMessage})",
                goalId, CleanupMessageOrPlaceholder(primary));
        }
        catch (Exception cleanupEx)
        {
            // Best-effort hygiene: the diagnostic must never replace the primary failure.
            BestEffortWarning(
                "WorkSlotIntegrity: full-save-conversation-cleanup goal={GoalId} — the rejected conversation replacement's tracker cleanup did not complete; the context state is SUSPECT and its further usability is NOT guaranteed: {Message}",
                goalId, CleanupMessageOrPlaceholder(cleanupEx));
        }
    }

    /// <summary>Append a single conversation entry without rewriting the full conversation.</summary>
    public void AppendConversation(string goalId, ConversationEntry entry)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var maxSeq = db.ConversationEntries
                .Where(e => e.GoalId == goalId)
                .Select(e => (int?)e.Seq)
                .Max() ?? -1;

            db.ConversationEntries.Add(new ConversationEntryEntity
            {
                GoalId = goalId,
                Seq = maxSeq + 1,
                Role = entry.Role,
                Content = entry.Content,
                Iteration = entry.Iteration,
                Purpose = entry.Purpose,
            });
            db.SaveChanges();
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>Register a task → goal mapping for recovery.</summary>
    public void SaveTaskMapping(string taskId, string goalId)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var existing = db.TaskMappings.Find(taskId);
            if (existing is not null)
            {
                existing.GoalId = goalId;
            }
            else
            {
                db.TaskMappings.Add(new TaskMappingEntity { TaskId = taskId, GoalId = goalId });
            }
            db.SaveChanges();
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>
    /// Conditionally claims the <c>task_mappings</c> row for <paramref name="taskId"/> on behalf of
    /// <paramref name="goalId"/>, NEVER overwriting a row that belongs to a different goal.
    /// </summary>
    /// <remarks>
    /// A SINGLE-STATEMENT conditional upsert executed as parameterized raw SQL — there is no read,
    /// no tracked entity, and therefore no check-then-write window a competing writer could slip
    /// through. The <c>WHERE goal_id = @goalId</c> on the conflict branch is the ownership guard:
    /// re-claiming our own row is idempotent, while another goal's row is left INTACT and reported
    /// as a refusal. A store failure PROPAGATES — nothing is swallowed here.
    /// </remarks>
    /// <param name="taskId">The task id (primary key of <c>task_mappings</c>).</param>
    /// <param name="goalId">The goal claiming the mapping.</param>
    /// <returns><c>true</c> when the row is ours after the statement; <c>false</c> when it belongs to another goal.</returns>
    public bool TrySaveTaskMappingIfUnowned(string taskId, string goalId)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var affected = db.Database.ExecuteSqlRaw(
                """
                INSERT INTO task_mappings (task_id, goal_id) VALUES (@taskId, @goalId)
                ON CONFLICT(task_id) DO UPDATE SET goal_id = @goalId WHERE goal_id = @goalId
                """,
                new SqliteParameter("@taskId", taskId),
                new SqliteParameter("@goalId", goalId));

            DetachTrackedTaskMapping(db, taskId);
            return affected == 1;
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>
    /// Conditionally deletes the <c>task_mappings</c> row for <paramref name="taskId"/> ONLY when it
    /// still belongs to <paramref name="goalId"/>.
    /// </summary>
    /// <remarks>
    /// A single statement carrying BOTH predicates (task id AND goal id), so a row that has since
    /// been claimed by another goal survives untouched and the call reports <c>false</c>.
    /// A store failure PROPAGATES.
    /// </remarks>
    /// <param name="taskId">The task id whose mapping to remove.</param>
    /// <param name="goalId">The goal that must own the row for the delete to happen.</param>
    /// <returns><c>true</c> when a row was deleted; <c>false</c> when no row matched both predicates.</returns>
    public bool DeleteTaskMappingIfForGoal(string taskId, string goalId)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var affected = db.TaskMappings
                .Where(t => t.TaskId == taskId && t.GoalId == goalId)
                .ExecuteDelete();

            DetachTrackedTaskMapping(db, taskId);
            return affected >= 1;
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>
    /// Detaches any tracked <see cref="TaskMappingEntity"/> carrying <paramref name="taskId"/> so a
    /// later read cannot observe the pre-statement (now stale) tracked copy. Applied on BOTH the
    /// success and refusal paths — the raw statement bypassed the change tracker either way.
    /// </summary>
    private static void DetachTrackedTaskMapping(CopilotHiveDbContext db, string taskId)
    {
        foreach (var entry in db.ChangeTracker.Entries<TaskMappingEntity>().ToList())
        {
            if (string.Equals(entry.Entity.TaskId, taskId, StringComparison.Ordinal))
                db.Entry(entry.Entity).State = EntityState.Detached;
        }
    }

    /// <summary>The real tracker-detach step the seam's null-default invokes: the ChangeTracker
    /// lookup for the goal's tracked PipelineEntity and the established EntityState.Detached
    /// assignment (the repository's established detach form).</summary>
    private static void DetachTrackedPipeline(CopilotHiveDbContext db, string goalId)
    {
        var entry = db.ChangeTracker.Entries<PipelineEntity>()
            .FirstOrDefault(e => e.Entity.GoalId == goalId);
        if (entry is not null)
            db.Entry(entry.Entity).State = EntityState.Detached;
    }

    /// <summary>
    /// Detaches EVERY tracked <see cref="PipelineEntity"/> carrying <paramref name="goalId"/>, in
    /// ANY state (Added/Modified/Deleted/Unchanged) — the key-scoped form
    /// <see cref="CommitAdmissionOwnership"/> needs after its raw statements bypassed the change
    /// tracker. Detaching an Added/Modified/Deleted entry INTENTIONALLY DISCARDS its pending change
    /// so it can never be flushed later; entries for other goals are left tracked untouched.
    /// </summary>
    private static void DetachTrackedPipelinesForGoal(CopilotHiveDbContext db, string goalId)
    {
        foreach (var entry in db.ChangeTracker.Entries<PipelineEntity>().ToList())
        {
            if (string.Equals(entry.Entity.GoalId, goalId, StringComparison.Ordinal))
                db.Entry(entry.Entity).State = EntityState.Detached;
        }
    }

    /// <summary>Remove a completed/failed pipeline from the store.</summary>
    public void RemovePipeline(string goalId)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var conversations = db.ConversationEntries.Where(e => e.GoalId == goalId).ToList();
            if (conversations.Count > 0)
                db.ConversationEntries.RemoveRange(conversations);

            var mappings = db.TaskMappings.Where(t => t.GoalId == goalId).ToList();
            if (mappings.Count > 0)
                db.TaskMappings.RemoveRange(mappings);

            var pipeline = db.Pipelines.Find(goalId);
            if (pipeline is not null)
                db.Pipelines.Remove(pipeline);

            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove pipeline for goal {GoalId}", goalId);
            throw;
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>Load all non-terminal pipelines for restart recovery.</summary>
    public List<PipelineSnapshot> LoadActivePipelines()
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var entities = db.Pipelines
                .Where(p => p.Phase != "Done" && p.Phase != "Failed")
                .ToList();

            var results = new List<PipelineSnapshot>();
            foreach (var entity in entities)
            {
                var snapshot = ToSnapshot(entity);
                snapshot.Conversation = LoadConversationCore(db, entity.GoalId);
                snapshot.TaskMappings = LoadTaskMappingsCore(db, entity.GoalId);
                results.Add(snapshot);
            }

            _logger.LogInformation("Loaded {Count} active pipeline(s) from store", results.Count);
            return results;
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>
    /// TRUE iff a persisted row already exists for <paramref name="goalId"/> — the narrow EXISTENCE
    /// probe the manager's eligibility decision needs.
    /// </summary>
    /// <remarks>
    /// It answers only "is there a row?"; it does not load, deserialize or interpret any column, so
    /// it can never fail on a malformed stored value the way a full snapshot load could. The query
    /// is a server-side <c>EXISTS</c> and returns no entity, so it stages and tracks nothing — a
    /// caller that only wants the provenance fact cannot disturb the context's tracker with it.
    /// </remarks>
    /// <param name="goalId">The goal whose pipeline row is probed.</param>
    /// <returns><c>true</c> when the row exists.</returns>
    internal bool PipelineRowExists(string goalId)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            return db.Pipelines.Any(p => p.GoalId == goalId);
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>
    /// Load a single pipeline by goal ID regardless of phase (including Done/Failed).
    /// Returns null if no pipeline is found.
    /// </summary>
    public PipelineSnapshot? LoadPipeline(string goalId)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var entity = db.Pipelines.Find(goalId);
            if (entity is null)
                return null;

            var snapshot = ToSnapshot(entity);
            snapshot.Conversation = LoadConversationCore(db, goalId);
            snapshot.TaskMappings = LoadTaskMappingsCore(db, goalId);
            return snapshot;
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>
    /// Delete a single task mapping by task ID from the persisted store.
    /// </summary>
    public void DeleteTaskMapping(string taskId)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var mapping = db.TaskMappings.Find(taskId);
            if (mapping is not null)
            {
                db.TaskMappings.Remove(mapping);
                db.SaveChanges();
            }
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>Loads the conversation entries for a specific goal from the store.</summary>
    /// <param name="goalId">The goal ID whose conversation entries to retrieve.</param>
    /// <returns>The conversation entries, or an empty list if no entries exist.</returns>
    public List<ConversationEntry> GetConversation(string goalId)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            return LoadConversationCore(db, goalId);
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>The two flushed stages of the admission transaction, used as the conflict stage gate.</summary>
    private enum AdmissionStage
    {
        /// <summary>The task-mapping insert is being flushed.</summary>
        MappingFlush,
        /// <summary>The mapping flush succeeded; the pipeline row is being flushed.</summary>
        PipelineFlush,
    }

    /// <summary>
    /// Atomically registers a task admission: the <c>task_mappings</c> row (the insert IS the
    /// existence check) AND the pipeline row in ONE explicit transaction. A mapping row that
    /// already exists (primary-key violation at the mapping flush) rolls everything back and
    /// reports <see cref="AdmissionStoreResult.PersistConflict"/> — the pipeline row is never
    /// staged in that case. Every other failure PROPAGATES (the original exception, never
    /// reclassified), and the finally's guarded cleanup never masks either outcome.
    /// </summary>
    /// <remarks>
    /// THE LEGACY, UNCHANGED ROUTE. This overload validates against the pipeline's LIVE
    /// <c>ActiveTaskId</c>, writes no ownership checkpoint (the frozen pair is <c>null</c>, so the
    /// <c>work_slot_registry_json</c> column is left exactly as it was — an existing blob survives
    /// byte-for-byte) and DELEGATES the whole transaction to
    /// <see cref="SaveAdmissionWithPointerCore"/> with no checkpoint. The body is SHARED with the
    /// ownership-aware overload below: it is never copied, and there is exactly one outcome surface
    /// (<see cref="AdmissionStoreResult"/>).
    /// <para>
    /// THE POINTER IS THE VALIDATED OVERRIDE, NOT A LATE LIVE READ: the non-blank
    /// <paramref name="taskId"/> this overload validated above is passed through to the row write as
    /// the <c>activeTaskIdOverride</c>, so the column receives EXACTLY the id the caller supplied —
    /// the pipeline's live <c>ActiveTaskId</c> is never re-read at write time. (The pipeline's live
    /// value is read by this overload's own equality VALIDATION only; it is not the written value.)
    /// </para>
    /// </remarks>
    /// <param name="pipeline">The pipeline whose pointer is persisted alongside the mapping.</param>
    /// <param name="taskId">The task id being admitted; MUST equal <c>pipeline.ActiveTaskId</c>.</param>
    /// <returns><see cref="AdmissionStoreResult.Committed"/> or <see cref="AdmissionStoreResult.PersistConflict"/>.</returns>
    internal AdmissionStoreResult SaveAdmissionWithPointer(GoalPipeline pipeline, string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task id must be a non-blank value.", nameof(taskId));

        if (pipeline.ActiveTaskId != taskId)
            throw new ArgumentException(
                $"Task id '{taskId}' does not match the pipeline's active task id '{pipeline.ActiveTaskId}' (goal={pipeline.GoalId}).",
                nameof(taskId));

        return SaveAdmissionWithPointerCore(pipeline, taskId, checkpoint: null);
    }

    /// <summary>
    /// The convenience form of the ownership-aware admission route, for callers that do not need
    /// the invocation-local rollback evidence: it DELEGATES to the
    /// <c>out encodedRegistryJson</c> overload and discards the carrier. There is exactly ONE
    /// implementation — this overload never validates, freezes or encodes anything itself.
    /// </summary>
    /// <param name="pipeline">The pipeline whose row receives the ordinary fields and the checkpoint.</param>
    /// <param name="taskId">The task id being admitted; MUST equal the CAPTURE's active task id.</param>
    /// <param name="ownership">The detached ownership capture supplying the pointer AND the registry.</param>
    /// <returns><see cref="AdmissionStoreResult.Committed"/> or <see cref="AdmissionStoreResult.PersistConflict"/>.</returns>
    internal AdmissionStoreResult SaveAdmissionWithPointer(
        GoalPipeline pipeline, string taskId, AdmissionOwnershipSnapshot ownership) =>
        SaveAdmissionWithPointer(pipeline, taskId, ownership, out _);

    /// <summary>
    /// THE OWNERSHIP-AWARE ADMISSION ROUTE, with the invocation-local rollback evidence: the same
    /// atomic <c>task_mappings</c> + pipeline-row transaction as
    /// <see cref="SaveAdmissionWithPointer(GoalPipeline, string)"/>, but the pipeline row is written
    /// from ONE detached <paramref name="ownership"/> capture — the frozen active-task pointer AND
    /// the complete encoded work-slot registry — inside the SAME row write and the SAME transaction
    /// as the mapping insert, and the EXACT frozen registry token that write installs is returned to
    /// the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EVERYTHING HERE HAPPENS BEFORE ANY CONTEXT EXISTS, in this exact order: (1) the SAME
    /// non-blank <paramref name="taskId"/> guard as the legacy overload (identical exception shape);
    /// (2) <see cref="GoalPipeline.PreflightAdmissionOwnership"/> — REUSED verbatim, never
    /// duplicated — validates the detached candidate (goal identity, complete registry history and
    /// the matching Pending active slot); (3) the CAPTURED active task must equal
    /// <paramref name="taskId"/> ordinally (<see cref="ArgumentException"/> otherwise); (4) the
    /// EXISTING <see cref="FreezeOwnershipCheckpoint"/> performs the goal-agreement check and the
    /// SINGLE <see cref="WorkSlotRegistryCodec.Encode"/>. Only after all four does the shared core
    /// resolve a context.
    /// </para>
    /// <para>
    /// THE VALIDATED DETACHED CARRIER IS USED FROM THERE ON — live ownership is NEVER re-read. A
    /// domain mutation landing after the capture therefore cannot change the committed pointer or
    /// registry; the frozen pair is installed by the existing stage-2 pipeline-row write
    /// (<c>UpsertPipelineCore</c> → <c>ApplyToEntity</c>) and is NOT a second, independent registry
    /// write.
    /// </para>
    /// <para>
    /// THE EVIDENCE IS THE ONE FROZEN TOKEN, NOT A SECOND ENCODE:
    /// <paramref name="encodedRegistryJson"/> is read straight off the SAME
    /// <see cref="FrozenOwnershipCheckpoint"/> instance that is passed into the shared transaction
    /// body, so it is the ORIGINAL string that write installs. It is never derived from a later
    /// live capture, from post-abandon state, from a fresh database read or from a
    /// decode/re-encode round trip, and this route performs no additional encode merely to produce
    /// it. The value describes the ROW WRITE'S expectation for this invocation only; the caller
    /// decides what to do with it, and a rejection escapes before it is assigned at all.
    /// </para>
    /// <para>
    /// NO NEW VALIDATOR, SCHEMA, CODEC, ATTEMPT ALLOCATION OR EXPECTED-BLOB CACHE is introduced, and
    /// there is no second outcome surface: the result is the same <see cref="AdmissionStoreResult"/>
    /// (<see cref="AdmissionStoreResult.Committed"/> / <see cref="AdmissionStoreResult.PersistConflict"/>)
    /// with the same mapping-insert-IS-the-check semantics, the same mapping-flush-only primary-key
    /// conflict classification, the same original-exception propagation and the same guarded
    /// rollback / tracker-hygiene / transaction-dispose / owned-context-dispose sequence.
    /// </para>
    /// </remarks>
    /// <param name="pipeline">The pipeline whose row receives the ordinary fields and the checkpoint.</param>
    /// <param name="taskId">The task id being admitted; MUST equal the CAPTURE's active task id.</param>
    /// <param name="ownership">The detached ownership capture supplying the pointer AND the registry.</param>
    /// <param name="encodedRegistryJson">The EXACT frozen registry token this row write installs —
    /// the same value threaded into the shared transaction body. Assigned only once every
    /// pre-context validation has passed; unreachable on a rejection (the exception escapes
    /// first).</param>
    /// <returns><see cref="AdmissionStoreResult.Committed"/> or <see cref="AdmissionStoreResult.PersistConflict"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ownership"/> (or a required member of
    /// its registry) is <c>null</c> — from the reused preflight.</exception>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> is blank, the capture is
    /// malformed or has no matching Pending slot, the captured active task differs from
    /// <paramref name="taskId"/>, or the capture belongs to another goal.</exception>
    internal AdmissionStoreResult SaveAdmissionWithPointer(
        GoalPipeline pipeline, string taskId, AdmissionOwnershipSnapshot ownership,
        out string? encodedRegistryJson)
    {
        // (1) THE SAME NON-BLANK GUARD as the legacy overload — identical shape and ParamName.
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task id must be a non-blank value.", nameof(taskId));

        // (2) THE REUSED PREFLIGHT: the detached candidate's goal identity, its COMPLETE registry
        //     history and its matching Pending active slot are validated by GoalPipeline's own
        //     validator — no duplicate validator lives here. Every rejection escapes with no
        //     context created and no statement issued.
        var validated = GoalPipeline.PreflightAdmissionOwnership(ownership);

        // (3) THE CAPTURED-POINTER AGREEMENT: the admission must be for the task the capture
        //     actually owns — compared ORDINALLY against the captured value, never against a live
        //     read of the pipeline.
        if (!string.Equals(validated.ActiveTaskId, taskId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Task id '{taskId}' does not match the ownership capture's active task id '{validated.ActiveTaskId}' (goal={pipeline.GoalId}).",
                nameof(taskId));
        }

        // (4) THE REUSED FREEZE: the goal-agreement check and the SINGLE registry encode, both
        //     before any context exists. The VALIDATED carrier is frozen from here on.
        var checkpoint = FreezeOwnershipCheckpoint(pipeline, validated);

        // THE EVIDENCE — read STRAIGHT OFF the very checkpoint the shared body installs. No second
        // encode, no live re-read, no round trip.
        encodedRegistryJson = checkpoint.EncodedRegistryJson;

        return SaveAdmissionWithPointerCore(pipeline, taskId, checkpoint);
    }

    /// <summary>
    /// THE ONE SHARED TRANSACTION BODY of all three <c>SaveAdmissionWithPointer</c> overloads. The
    /// <paramref name="checkpoint"/> — the frozen pointer/registry pair, or <c>null</c> on the
    /// legacy route — is threaded into the EXISTING stage-2 pipeline-row write; nothing else about
    /// the transaction differs between the routes.
    /// </summary>
    /// <param name="pipeline">The pipeline whose row is written.</param>
    /// <param name="taskId">The validated task id the mapping row is inserted for.</param>
    /// <param name="checkpoint">The frozen ownership pair to install, or <c>null</c> for the legacy
    /// route (no override: the registry column is left untouched and the pointer keeps its validated
    /// task-id override — or, when a direct caller passes none, the existing late read).</param>
    /// <returns><see cref="AdmissionStoreResult.Committed"/> or <see cref="AdmissionStoreResult.PersistConflict"/>.</returns>
    private AdmissionStoreResult SaveAdmissionWithPointerCore(
        GoalPipeline pipeline, string taskId, FrozenOwnershipCheckpoint? checkpoint)
    {
        var (db, ownsContext) = ResolveDbContext();
        IDbContextTransaction? transaction = null;
        var stage = AdmissionStage.MappingFlush;
        AdmissionStoreResult result;
        var commitSucceeded = false; // the finally's rollback gate (definite-assignment safe)
        try
        {
            transaction = db.Database.BeginTransaction();

            // STAGE 1 — THE MAPPING (the insert IS the check; no preflight Any/Exists query).
            db.TaskMappings.Add(new TaskMappingEntity { TaskId = taskId, GoalId = pipeline.GoalId });
            db.SaveChanges();
            stage = AdmissionStage.PipelineFlush;

            // STAGE 2 — THE PIPELINE ROW. The frozen ownership pair travels through the EXISTING
            // pipeline-row upsert (its checkpoint parameter already installs the pair in this same
            // row write); the legacy route passes null and therefore leaves the pointer/registry
            // columns exactly as it always did.
            UpsertPipelineCore(db, pipeline, taskId, checkpoint);
            db.SaveChanges();

            // STAGE 3 — THE COMMIT.
            transaction.Commit();
            commitSucceeded = true;
            result = AdmissionStoreResult.Committed;
        }
        catch (DbUpdateException dbex) when (stage == AdmissionStage.MappingFlush && IsPrimaryKeyViolation(dbex))
        {
            // THE CONFLICT — ONLY from the mapping's flush (the stage gate). The pipeline row
            // was never staged; the finally's rollback makes the aborted insert invisible.
            result = AdmissionStoreResult.PersistConflict;
        }
        catch
        {
            throw; // the generic failure path — the ORIGINAL propagates after the finally's cleanup
        }
        finally
        {
            // The guarded (a)-(d) sequence — EVERY path, in this exact order. Each guard logs a
            // BestEffortWarning on failure and swallows; the outcome/exception is NEVER masked.

            // (a) Rollback — ONLY when the commit did not succeed. A rollback failure is warned
            //     and swallowed, and selects the detach-only cleanup fallback.
            var rollbackConfirmed = false;
            if (transaction is not null && !commitSucceeded)
            {
                try
                {
                    transaction.Rollback();
                    rollbackConfirmed = true;
                }
                catch (Exception rollbackEx)
                {
                    SafeCleanupWarning(
                        "WorkSlotIntegrity: admission-rollback goal={GoalId} task={TaskId} — the rollback step failed (unconfirmed; the detach-only cleanup will be used): {Message}",
                        pipeline.GoalId, taskId, rollbackEx);
                }
            }

            // (b) Tracked-state cleanup. The mapping entity is DETACHED UNCONDITIONALLY (any
            //     state). The pipeline entity: reload-if-tracked (only when the rollback
            //     CONFIRMED — the fresh Find discards the stale tracked copy and surfaces the
            //     durable pointer) / detach-if-Added / detach-if-tracked (the unconfirmed
            //     fallback, no reload).
            try
            {
                foreach (var entry in db.ChangeTracker.Entries<TaskMappingEntity>().ToList())
                    db.Entry(entry.Entity).State = EntityState.Detached;

                var pipelineEntry = db.ChangeTracker.Entries<PipelineEntity>().ToList();
                if (pipelineEntry.Count > 0)
                {
                    if (rollbackConfirmed)
                    {
                        // The rollback is CONFIRMED: the database holds the pre-existing row (or
                        // nothing). DETACH the stale tracked copy first (a Find alone would hand
                        // back the in-flight copy), then reload fresh through the tracker.
                        foreach (var entry in pipelineEntry)
                            db.Entry(entry.Entity).State = EntityState.Detached;
                        db.Pipelines.Find(pipeline.GoalId);
                    }
                    else
                    {
                        foreach (var entry in pipelineEntry)
                            db.Entry(entry.Entity).State = EntityState.Detached;
                    }
                }
            }
            catch (Exception cleanupEx)
            {
                SafeCleanupWarning(
                    "WorkSlotIntegrity: admission-cleanup goal={GoalId} task={TaskId} — the tracked-state cleanup failed: {Message}",
                    pipeline.GoalId, taskId, cleanupEx);
            }

            // (c) Guarded transaction disposal — EVERY path INCLUDING THE SUCCESS PATH (a
            //     Commit'ed transaction must not leak). On the success path a dispose failure
            //     is warned and swallowed but Committed is STILL RETURNED: the commit already
            //     succeeded and the row is durable — a dispose failure is a connection-state
            //     concern, not an admission failure. On the failure paths the original
            //     outcome/exception is preserved.
            try
            {
                transaction?.Dispose();
            }
            catch (Exception disposeEx)
            {
                SafeCleanupWarning(
                    "WorkSlotIntegrity: admission-dispose goal={GoalId} task={TaskId} — the transaction dispose failed: {Message}",
                    pipeline.GoalId, taskId, disposeEx);
            }

            // (d) Factory-owned context disposal — the caller-owned direct context is NEVER
            //     disposed here (the ownsContext gate). The disposer is the internal seam
            //     (see <see cref="ContextDisposerForTest"/>); the null-default calls the
            //     real Dispose().
            if (ownsContext)
            {
                try
                {
                    (ContextDisposerForTest ?? (context => context.Dispose()))(db);
                }
                catch (Exception contextDisposeEx)
                {
                    SafeCleanupWarning(
                        "WorkSlotIntegrity: admission-context-dispose goal={GoalId} task={TaskId} — the context dispose failed: {Message}",
                        pipeline.GoalId, taskId, contextDisposeEx);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Ownership-checked persisted-pointer clear. Sets the pipeline row's active_task_id to
    /// NULL iff the row's current value equals <paramref name="taskId"/> (a newer/different pointer
    /// NEVER erased — the WHERE clause the ownership check). PARAMETERIZED SQL. NEVER THROWS (every
    /// phase guarded, the acquisition included).
    /// </summary>
    /// <remarks>
    /// ITS USE: <c>GoalPipelineManager.RollbackPersistedPointer</c> is this method's caller, and it
    /// is reached from the production dispatch — <c>TaskDispatchService</c>'s enqueue-failure
    /// rollback clears the pointer it committed moments earlier. It is NOT an unused/future API.
    /// </remarks>
    internal PointerRollbackResult ClearActiveTaskIdIfMatches(string goalId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(goalId) || string.IsNullOrWhiteSpace(taskId))
            return PointerRollbackResult.NotMatched;          // the blank no-op: no SQL, no log

        CopilotHiveDbContext? dbOrNull = null;
        var ownsContext = false;
        var cleared = false;
        try
        {
            (dbOrNull, ownsContext) = ResolveDbContext();     // PHASE 1 — INSIDE the guard; a factory
            var db = dbOrNull!;                               // throw → the catch (dbOrNull stays null);
                                                              // the post-acquisition non-null local

            var pGoal = new SqliteParameter("$goal", goalId); // PHASE 2 — THE SQL (parameterized)
            var pTask = new SqliteParameter("$task", taskId);
            cleared = db.Database.ExecuteSqlRaw(
                "UPDATE pipelines SET active_task_id = NULL WHERE goal_id = $goal AND active_task_id = $task",
                pGoal, pTask) > 0;

            try                                              // PHASE 3 — THE TRACKER HYGIENE (best-effort)
            {
                var entry = db.ChangeTracker.Entries<PipelineEntity>()
                    .FirstOrDefault(e => e.Entity.GoalId == goalId);
                if (entry is not null)
                    (TrackerDetachForTest ?? DetachTrackedPipeline)(db, goalId);
            }
            catch
            {
                BestEffortWarning(PointerRollbackCleanupTemplate, goalId, taskId, cleared);
                // the entity REMAINS TRACKED (its stale in-memory ActiveTaskId); the DB's truth
                // (NULL) stands; the SQL's result stands — the best-effort cleanup
            }
        }
        catch (Exception ex)
        {
            BestEffortWarning(PointerRollbackFailureTemplate, goalId, taskId);
            _ = ex;                                          // the structured exception optional
            return PointerRollbackResult.Failed;             // the row's state UNKNOWN
        }
        finally
        {
            if (ownsContext && dbOrNull is not null)          // PHASE 5 — the owned disposal (guarded)
            {
                try { (ContextDisposerForTest ?? (static ctx => ctx.Dispose()))(dbOrNull); }
                catch { BestEffortWarning(PointerRollbackCleanupTemplate, goalId, taskId, cleared); }
            }                                                 // db null (the acquisition failure) → skipped;
                                                              // the caller-owned direct context NEVER disposed
        }
        return cleared ? PointerRollbackResult.Cleared : PointerRollbackResult.NotMatched;
    }

    /// <summary>
    /// THE ADMISSION-OWNERSHIP GUARD statement: takes the active pointer AND installs the encoded
    /// registry blob on an EXISTING pipeline row, but ONLY while the row still carries no pointer
    /// and its stored registry text is EXACTLY the expected prior text.
    /// <para>
    /// <c>IS ... COLLATE BINARY</c> is the null-safe BYTE-EXACT comparison: SQL NULL matches only a
    /// <c>null</c> expectation, an empty string matches only an empty string, and an encoded empty
    /// registry (a version-1 envelope with two empty arrays) matches neither. Two JSON-EQUIVALENT
    /// but textually different payloads do NOT match — no decode, no normalization, no repair.
    /// </para>
    /// </summary>
    private const string AdmissionOwnershipGuardSql =
        """
        UPDATE pipelines
        SET active_task_id = $task, work_slot_registry_json = $registry
        WHERE goal_id = $goal
          AND active_task_id IS NULL
          AND work_slot_registry_json IS $expected COLLATE BINARY
        """;

    /// <summary>
    /// THE ADMISSION MAPPING statement: inserts the task → goal routing row, doing NOTHING when a
    /// row for the task id already exists. NEVER a REPLACE, an upsert or a steal — an existing row
    /// (even one already pointing at the SAME goal) yields zero inserted rows and is a refusal
    /// candidate, so re-admission of an already-mapped task is never silently idempotent.
    /// </summary>
    private const string AdmissionOwnershipMappingSql =
        """
        INSERT INTO task_mappings (task_id, goal_id) VALUES ($task, $goal)
        ON CONFLICT(task_id) DO NOTHING
        """;

    /// <summary>
    /// THE PENDING-ROLLBACK GUARD statement: clears the active pointer AND installs the
    /// Abandoned-state replacement registry blob on an EXISTING pipeline row, but ONLY while the
    /// row's active pointer is STILL this task and its stored registry text is EXACTLY the
    /// ORIGINAL supplied expectation.
    /// <para>
    /// <c>IS ... COLLATE BINARY</c> is the null-safe BYTE-EXACT comparison — the same textual
    /// compare-and-swap the admission guard uses. SQL NULL matches only a <c>null</c> expectation;
    /// two JSON-EQUIVALENT but textually different payloads do NOT match. The pointer predicate is
    /// an ordinal equality against the supplied task id — a null pointer (an already-cleared or
    /// never-owned row) and a newer pointer both fail it. NO repair, retry or reconstruction.
    /// </para>
    /// </summary>
    private const string PendingRollbackGuardSql =
        """
        UPDATE pipelines
        SET active_task_id = NULL, work_slot_registry_json = $registry
        WHERE goal_id = $goal
          AND active_task_id = $task
          AND work_slot_registry_json IS $expected COLLATE BINARY
        """;

    /// <summary>
    /// THE PENDING-ROLLBACK MAPPING statement: deletes the task → goal routing row ONLY when it
    /// STILL belongs to the goal. A row claimed by a newer goal (or already deleted) matches zero
    /// rows — a refusal candidate that rolls the preceding update back.
    /// </summary>
    private const string PendingRollbackMappingSql =
        """
        DELETE FROM task_mappings WHERE task_id = $task AND goal_id = $goal
        """;

    private const string PendingRollbackDetachTemplate =
        "WorkSlotIntegrity: pending-rollback-detach goal={GoalId} task={TaskId} — the key-scoped tracker detach failed; the context state is SUSPECT and its further usability is NOT guaranteed: {Message}";
    private const string PendingRollbackTransactionDisposeTemplate =
        "WorkSlotIntegrity: pending-rollback-transaction-dispose goal={GoalId} task={TaskId} — the transaction dispose failed; the outcome already recorded stands: {Message}";
    private const string PendingRollbackContextDisposeTemplate =
        "WorkSlotIntegrity: pending-rollback-context-dispose goal={GoalId} task={TaskId} — the factory-owned context dispose failed; the outcome already recorded stands: {Message}";

    /// <summary>
    /// THE SHARED TRANSACTION SCAFFOLDING of the admission-ownership family: validates a detached
    /// candidate, encodes its registry EXACTLY ONCE, resolves the context, opens ONE explicit
    /// transaction, and — after the caller's body has produced an outcome — runs the rollback /
    /// commit sequencing and the guarded key-scoped cleanup in the established order. Every caller
    /// keeps its own SQL, its own refusal rule and its own diagnostics; the scaffolding owns only
    /// the SHARED machinery (the outcome precedence, the exception identity, the tracker hygiene,
    /// the never-masked disposal). It CHANGES NO existing admission semantics.
    /// </summary>
    /// <remarks>
    /// THE BODY CONTRACT. The body receives the open context, the validated goal id, the validated
    /// task id and the ENCODED registry blob (already computed) and returns either a
    /// <see cref="OwnershipBodyOutcome{TOutcome}"/> with <c>Refused</c> set (a confirmed-refusal
    /// candidate; the scaffolding rolls back and hands the refusal back to the caller) or with
    /// <c>Committed</c> set (the scaffolding commits and returns it). ANY exception the body throws
    /// is CAPTURED by the scaffolding and routed through the shared outcome precedence — never
    /// rethrown from inside the body frame.
    /// </remarks>
    /// <typeparam name="TOutcome">The caller's outcome type.</typeparam>
    /// <param name="candidate">The detached carrier to validate and encode.</param>
    /// <param name="expectedRegistryJson">The RAW registry text carried verbatim into the caller's SQL.</param>
    /// <param name="body">The caller's statement body (see the remarks).</param>
    /// <param name="diagnostics">The caller's warning templates for the guarded cleanup steps.</param>
    /// <returns>The recorded outcome (commit or refusal), or the exact exception that propagates.</returns>
    private (TOutcome? Outcome, Exception? ToThrow) RunOwnershipTransaction<TOutcome>(
        AdmissionOwnershipSnapshot? candidate,
        string? expectedRegistryJson,
        Func<CopilotHiveDbContext, string, string, string, OwnershipBodyOutcome<TOutcome>> body,
        OwnershipTransactionDiagnostics diagnostics)
        where TOutcome : class
    {
        // PHASE 1 — THE PREFLIGHT (reused verbatim) and THE SINGLE ENCODE, both BEFORE any
        // database interaction. Every rejection escapes here: no context, no statement, no refusal.
        var validated = GoalPipeline.PreflightAdmissionOwnership(candidate);
        var encoded = WorkSlotRegistryCodec.Encode(validated.Registry);

        var goalId = validated.GoalId;
        var taskId = validated.ActiveTaskId!; // the preflight proved it non-null and non-blank

        // PHASE 2 — THE CONTEXT. An acquisition failure propagates with nothing to clean up.
        var (db, ownsContext) = ResolveDbContext();

        IDbContextTransaction? transaction = null;
        var sqlAttempted = false;
        try
        {
            // PHASE 3 — THE TRANSACTION. A Begin failure is still the pre-body outcome: it
            // propagates, and the finally below only disposes what was actually acquired.
            transaction = db.Database.BeginTransaction();

            // PHASE 4 — THE BODY. Every fault is CAPTURED (never rethrown from inside), so the
            // rollback/commit sequencing below decides the outcome in one place.
            var bodyException = default(Exception?);
            OwnershipBodyOutcome<TOutcome>? bodyOutcome = null;
            try
            {
                sqlAttempted = true;
                bodyOutcome = body(db, goalId, taskId, encoded);
            }
            catch (Exception ex)
            {
                bodyException = ex; // the EXACT object; never re-created, never unwrapped
            }

            // PHASE 5 — THE COMMIT, only when the body both succeeded and did not refuse.
            Exception? commitException = null;
            var commitConfirmed = false;
            if (bodyException is null && bodyOutcome is not null && !bodyOutcome.Value.Refused)
            {
                try
                {
                    transaction.Commit();
                    commitConfirmed = true; // recorded IMMEDIATELY — nothing may downgrade it
                }
                catch (Exception ex)
                {
                    commitException = ex;
                }
            }

            // PHASE 6 — THE ROLLBACK. Required for every error/refusal, and attempted best-effort
            // after a throwing commit (which may already have committed underneath).
            Exception? rollbackException = null;
            if (!commitConfirmed)
            {
                try
                {
                    transaction.Rollback();
                }
                catch (Exception ex)
                {
                    rollbackException = ex;
                }
            }

            // PHASE 7 — THE OUTCOME, in the exact precedence order: commit → commit-throw →
            // rollback-throw → body error → refusal.
            if (commitConfirmed)
                return (bodyOutcome!.Value.Committed, null);

            if (commitException is not null)
            {
                // The commit MAY have landed underneath; a rollback that "succeeded" afterwards
                // proves nothing, so neither success nor no-change is inferred.
                throw new OwnershipUncertainException(commitException, rollbackException);
            }

            if (rollbackException is not null)
            {
                // Overrides every ordinary error/refusal outcome, even when the body had not
                // mutated anything: the transaction's fate is unknown.
                throw new OwnershipUncertainException(bodyException, rollbackException);
            }

            // The rollback is CONFIRMED from here on.
            if (bodyException is not null)
            {
                // Ordinary body errors propagate UNCHANGED — the exact object, its stack kept.
                ExceptionDispatchInfo.Capture(bodyException).Throw();
            }

            if (bodyOutcome!.Value.Refused)
                return (bodyOutcome.Value.Refusal, null); // the confirmed-rollback refusal, caller-shaped

            // Unreachable: a non-refusing, non-throwing body always attempts the commit above.
            throw new InvalidOperationException(
                $"Ownership transaction for goal '{goalId}' reached no outcome (task '{taskId}').");
        }
        catch (OwnershipUncertainException uncertain)
        {
            // The uncertain outcome, carried with its exact evidence, through the guarded cleanup.
            return (default, uncertain);
        }
        finally
        {
            // THE GUARDED CLEANUP — every step independently, on EVERY path, never masking the
            // recorded outcome or a propagating exception.

            // (a) THE KEY-SCOPED TRACKER HYGIENE, only once a statement was actually attempted.
            //     The raw statements bypassed the tracker, so the affected goal's and task's
            //     tracked copies are DETACHED IN EVERY STATE — intentionally DISCARDING their
            //     pending changes. Unrelated tracked entities are untouched and unflushed.
            if (sqlAttempted)
            {
                try
                {
                    (TrackerDetachForTest ?? DetachTrackedPipelinesForGoal)(db, goalId);
                }
                catch (Exception detachEx)
                {
                    SafeCleanupWarning(diagnostics.DetachTemplate, goalId, taskId, detachEx);
                }

                try
                {
                    DetachTrackedTaskMapping(db, taskId);
                }
                catch (Exception detachEx)
                {
                    SafeCleanupWarning(diagnostics.DetachTemplate, goalId, taskId, detachEx);
                }
            }

            // (b) THE TRANSACTION disposal — including after a confirmed commit (a committed
            //     transaction must not leak). Never present when Begin itself failed.
            try
            {
                transaction?.Dispose();
            }
            catch (Exception disposeEx)
            {
                SafeCleanupWarning(diagnostics.TransactionDisposeTemplate, goalId, taskId, disposeEx);
            }

            // (c) THE CONTEXT disposal — factory-owned only. The caller-owned direct context is
            //     NEVER disposed here (the ownsContext gate).
            if (ownsContext)
            {
                try
                {
                    (ContextDisposerForTest ?? (context => context.Dispose()))(db);
                }
                catch (Exception contextDisposeEx)
                {
                    SafeCleanupWarning(diagnostics.ContextDisposeTemplate, goalId, taskId, contextDisposeEx);
                }
            }
        }
    }

    /// <summary>
    /// THE FINAL GUARD of the never-masked guarantee for the SHARED scaffolding's cleanup: a
    /// cleanup exception whose <see cref="Exception.Message"/> getter itself THROWS (the message is
    /// a virtual property, injectable through the tracker/context-disposal test seams) must never
    /// escape the finally — the whole message access is INSIDE the no-throw guard, so the recorded
    /// outcome or the propagating exception stays authoritative. The cleanup-exception text still
    /// reaches the log when obtainable; an unreadable message logs a static placeholder instead,
    /// never becoming the authoritative result.
    /// </summary>
    private void SafeCleanupWarning(string template, string goalId, string taskId, Exception cleanupException)
    {
        try
        {
            BestEffortWarning(template, goalId, taskId,
                CleanupMessageOrPlaceholder(cleanupException));
        }
        catch
        {
            // SILENT swallow — the cleanup diagnostic must never mask the authoritative outcome.
        }
    }

    /// <summary>
    /// Reads <see cref="Exception.Message"/> inside its own no-throw guard: an exception whose
    /// <c>Message</c> getter throws yields a static placeholder — the diagnostic degrades, the
    /// never-masked guarantee does not.
    /// </summary>
    private static string CleanupMessageOrPlaceholder(Exception cleanupException)
    {
        try
        {
            return cleanupException.Message;
        }
        catch (Exception messageEx)
        {
            return $"<message getter threw: {messageEx.GetType().Name}>";
        }
    }

    /// <summary>The body outcome the shared scaffolding's caller returns.</summary>
    /// <typeparam name="TOutcome">The caller's outcome type.</typeparam>
    private readonly record struct OwnershipBodyOutcome<TOutcome>(
        bool Refused, TOutcome? Committed, TOutcome? Refusal)
        where TOutcome : class
    {
        /// <summary>The confirmed-rollback refusal outcome.</summary>
        public static OwnershipBodyOutcome<TOutcome> RefusedOutcome(TOutcome refusal) => new(true, default, refusal);

        /// <summary>The confirmed-commit outcome.</summary>
        public static OwnershipBodyOutcome<TOutcome> CommittedOutcome(TOutcome committed) => new(false, committed, default);
    }

    /// <summary>The caller's diagnostic templates for the shared scaffolding's guarded cleanup.</summary>
    private sealed record OwnershipTransactionDiagnostics(
        string DetachTemplate,
        string TransactionDisposeTemplate,
        string ContextDisposeTemplate);

    /// <summary>
    /// The internal carrier for an unresolved transaction outcome (a throwing rollback, or ANY
    /// exception from the transaction's <c>Commit</c>, which can land AFTER the underlying
    /// commit) — the exact primary and rollback evidence, carried through the guarded cleanup so
    /// the caller can shape its own result record.
    /// </summary>
    private sealed class OwnershipUncertainException : Exception
    {
        public OwnershipUncertainException(Exception? primary, Exception? rollback)
        {
            Primary = primary;
            Rollback = rollback;
        }

        public Exception? Primary { get; }
        public Exception? Rollback { get; }
    }

    private const string AdmissionOwnershipDetachTemplate =
        "WorkSlotIntegrity: admission-ownership-detach goal={GoalId} task={TaskId} — the key-scoped tracker detach failed; the context state is SUSPECT and its further usability is NOT guaranteed: {Message}";
    private const string AdmissionOwnershipTransactionDisposeTemplate =
        "WorkSlotIntegrity: admission-ownership-transaction-dispose goal={GoalId} task={TaskId} — the transaction dispose failed; the outcome already recorded stands: {Message}";
    private const string AdmissionOwnershipContextDisposeTemplate =
        "WorkSlotIntegrity: admission-ownership-context-dispose goal={GoalId} task={TaskId} — the factory-owned context dispose failed; the outcome already recorded stands: {Message}";

    /// <summary>
    /// Commits a VALIDATED admission ownership candidate — the active-task pointer, the encoded
    /// work-slot registry blob and the task → goal mapping row — in ONE explicit transaction built
    /// from TWO narrow parameterized statements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// VALIDATE-AND-ENCODE BEFORE ANY CONTEXT EXISTS. <see cref="GoalPipeline.PreflightAdmissionOwnership"/>
    /// (reused verbatim — the validator is never duplicated here) proves the detached candidate,
    /// then <see cref="WorkSlotRegistryCodec.Encode"/> runs EXACTLY ONCE, and only afterwards is a
    /// context resolved. No temporary pipeline is built, no live pipeline is read, no task or
    /// attempt is allocated, no counter is reconstructed and no task id is parsed. A null, blank or
    /// malformed candidate therefore THROWS through the existing preflight/codec contracts
    /// (<see cref="ArgumentNullException"/> / <see cref="ArgumentException"/> /
    /// <c>WorkSlotRegistryCodecException</c>) having created no context and issued no statement — it
    /// is NEVER reported as a database refusal.
    /// </para>
    /// <para>
    /// THE EXPECTED PRIOR TEXT IS CARRIED VERBATIM. <paramref name="expectedRegistryJson"/> is
    /// passed to the guard exactly as supplied — never decoded, re-encoded, trimmed or repaired —
    /// and compared byte-exactly and null-safely (see <see cref="AdmissionOwnershipGuardSql"/>).
    /// </para>
    /// <para>
    /// THE BODY. Statement 1 is the conditional pointer+blob UPDATE; statement 2 is the
    /// conflict-tolerant mapping INSERT. EXACTLY ONE affected row is the only success count for
    /// EITHER statement, and EXACTLY ZERO is the only REFUSAL CANDIDATE — no read-back is performed
    /// to invent a reason. EVERY other count (a negative provider count such as <c>-1</c> just as
    /// much as a count above 1) and every other SQL failure is an ERROR, never a refusal and never
    /// a commit. The commit happens ONLY after both statements succeeded;
    /// a refusal rolls the earlier UPDATE back. Nothing else is ever written: no new pipeline row,
    /// no scalars, phase, plan, timestamps, conversation entries or historical mappings.
    /// </para>
    /// <para>
    /// THE OUTCOME PRECEDENCE, exactly: (A) anything failing BEFORE the transaction is acquired —
    /// validation, encoding, context acquisition, <c>BeginTransaction</c> — propagates the EXACT
    /// caught exception, and the guarded owned-context disposal cannot mask it; (B) once a
    /// transaction exists, a THROWING rollback yields <see cref="AdmissionOwnershipCommitStatus.Indeterminate"/>
    /// even if the body mutated nothing, retaining the body exception (or <c>null</c>) plus the
    /// exact rollback exception; (C) with a CONFIRMED rollback and no commit attempt only a
    /// zero-row guard or zero-row mapping insert is <see cref="AdmissionOwnershipCommitStatus.Refused"/>
    /// while ordinary body errors propagate unchanged; (D) ANY commit exception is
    /// <see cref="AdmissionOwnershipCommitStatus.Indeterminate"/> carrying that exact exception —
    /// a throw can happen AFTER the underlying commit, so a later rollback success proves nothing;
    /// (E) once <c>Commit</c> RETURNS, the confirmation is recorded immediately and
    /// <see cref="AdmissionOwnershipCommitStatus.Committed"/> is returned — later tracker, dispose
    /// or logger failures are guarded warnings that can never downgrade or undo it.
    /// </para>
    /// <para>
    /// EXCEPTION IDENTITY is preserved: the exact object caught (EF wrapper included) is retained or
    /// rethrown; no message is reconstructed, no sentinel is unwrapped and no
    /// <see cref="AggregateException"/> is substituted. No retry, repair, reconciliation or caller
    /// policy is introduced here.
    /// </para>
    /// <para>
    /// THE DIRECT-CONTEXT TRACKER POLICY. After the body's first SQL attempt the tracked entries for
    /// THIS goal and THIS task are detached in EVERY state (Added/Modified/Deleted/Unchanged),
    /// because the raw statements bypassed the change tracker: a stale pointer/blob copy or a
    /// pending mapping change must never be flushed by a later unrelated <c>SaveChanges</c>. This
    /// INTENTIONALLY DISCARDS pending changes on those affected tracked entities. Unrelated tracked
    /// entities are left alone and are never flushed here. No candidate state is installed into the
    /// tracker before the commit and no assumed state is reloaded after uncertainty. Each cleanup
    /// step is guarded independently; a hygiene failure warns that the context is SUSPECT and does
    /// NOT guarantee that the context stays usable.
    /// </para>
    /// <para>
    /// LIFETIME. A caller-owned direct context is NEVER disposed (the <c>ownsContext</c> gate);
    /// factory-owned contexts and the transaction are disposed on ALL paths, always guarded so the
    /// recorded outcome or the propagating exception is never masked.
    /// </para>
    /// <para>
    /// SCOPE, honestly. The mapping row is task → goal ROUTING only — not durable worker identity
    /// and not a payload. This is not a whole-pipeline or phase checkpoint, not an enqueue
    /// transaction, not a completion receipt, not Claimed/Recorded persistence and not a restart
    /// activation. INERT BY DESIGN: nothing in production calls this yet.
    /// </para>
    /// </remarks>
    /// <param name="candidate">The detached admission-ownership candidate to validate and persist.</param>
    /// <param name="expectedRegistryJson">The RAW registry text the row must currently hold, or
    /// <c>null</c> when the column must currently be SQL NULL.</param>
    /// <returns>The recorded outcome and, when the outcome is uncertain, the exception evidence.</returns>
    /// <exception cref="ArgumentNullException">The candidate or one of its required members is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The candidate is blank, malformed, or has no matching Pending slot.</exception>
    internal AdmissionOwnershipCommitResult CommitAdmissionOwnership(
        AdmissionOwnershipSnapshot? candidate,
        string? expectedRegistryJson)
    {
        var (outcome, toThrow) = RunOwnershipTransaction<AdmissionOwnershipCommitResult>(
            candidate,
            expectedRegistryJson,
            (db, goalId, taskId, encoded) =>
            {
                try
                {
                    var pipelineRows = db.Database.ExecuteSqlRaw(
                        AdmissionOwnershipGuardSql,
                        new SqliteParameter("$goal", goalId),
                        new SqliteParameter("$task", taskId),
                        new SqliteParameter("$registry", encoded),
                        new SqliteParameter("$expected", (object?)expectedRegistryJson ?? DBNull.Value));

                    if (pipelineRows is not (0 or 1))
                    {
                        // NOT a refusal: the goal id is the primary key, so ANY count other than the
                        // two legal ones — a negative provider count (e.g. -1, "unknown") just as
                        // much as a count above 1 — is an integrity surprise the caller must see as an
                        // ERROR. It must never reach the commit and must never become a refusal.
                        throw new InvalidOperationException(
                            $"Admission ownership guard updated {pipelineRows} pipeline rows for goal '{goalId}' (expected exactly 0 or 1).");
                    }

                    if (pipelineRows == 0)
                    {
                        // THE REFUSAL CANDIDATE — the row is missing, the pointer is already taken, or
                        // the stored registry text is not the expected one. No read-back is issued to
                        // guess WHICH: the store reports a refusal, not a reason taxonomy.
                        return OwnershipBodyOutcome<AdmissionOwnershipCommitResult>.RefusedOutcome(AdmissionOwnershipCommitResult.RefusedResult());
                    }

                    // EXACTLY ONE pipeline row was updated — the only success count.
                    var mappingRows = db.Database.ExecuteSqlRaw(
                        AdmissionOwnershipMappingSql,
                        new SqliteParameter("$task", taskId),
                        new SqliteParameter("$goal", goalId));

                    if (mappingRows is not (0 or 1))
                    {
                        // The same rule as the guard: every count other than 0 and 1 — negative
                        // counts included — is an ERROR, never a refusal and never a commit.
                        throw new InvalidOperationException(
                            $"Admission ownership mapping insert affected {mappingRows} rows for task '{taskId}' (expected exactly 0 or 1).");
                    }

                    // Zero inserted rows means a mapping for this task ALREADY exists — for ANY
                    // goal, the same one included. A refusal candidate, never a steal. Exactly one
                    // inserted row is the only success count.
                    return mappingRows == 0
                        ? OwnershipBodyOutcome<AdmissionOwnershipCommitResult>.RefusedOutcome(
                            AdmissionOwnershipCommitResult.RefusedResult())
                        : OwnershipBodyOutcome<AdmissionOwnershipCommitResult>.CommittedOutcome(
                            AdmissionOwnershipCommitResult.Committed());
                }
                finally
                {
                    // (the scaffolding owns the fault capture; nothing is swallowed here)
                }
            },
            new OwnershipTransactionDiagnostics(
                AdmissionOwnershipDetachTemplate,
                AdmissionOwnershipTransactionDisposeTemplate,
                AdmissionOwnershipContextDisposeTemplate));

        return FinishOwnershipOutcome(outcome, toThrow);
    }

    private static AdmissionOwnershipCommitResult FinishOwnershipOutcome(
        AdmissionOwnershipCommitResult? outcome, Exception? toThrow) =>
        toThrow switch
        {
            null when outcome is null =>
                throw new InvalidOperationException("The ownership transaction produced no outcome."),
            null => outcome!,
            OwnershipUncertainException uncertain =>
                AdmissionOwnershipCommitResult.Uncertain(uncertain.Primary, uncertain.Rollback),
            _ => throw toThrow,
        };

    /// <summary>
    /// THE STORE-ONLY DURABLE INVERSE of an intact pending admission: atomically ABANDONS the
    /// matching <see cref="WorkSlotState.Pending"/> slot in the persisted work-slot registry,
    /// clears the pipeline row's active-task pointer and deletes the task → goal mapping row —
    /// in ONE explicit transaction built from TWO narrow parameterized statements — but ONLY
    /// while the row still carries THIS task's pointer and the EXACT registry text supplied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SCOPE, honestly: this is a deliberately NARROW durable inverse. Its ONE production caller is
    /// <c>GoalPipelineManager.RollbackPendingAdmission</c>, reached from the dispatch's
    /// enqueue-failure path for an ELIGIBLE admission that carries its own invocation-local
    /// evidence — a bounded best-effort unwind of THAT admission, nothing more. It does NOT retire
    /// Claimed/Recorded attempts, it does NOT invalidate workers on restart, it issues NO completion
    /// receipt, and it makes NO durable receipt/replay or restart-safety claim. A persisted Pending
    /// slot alone does NOT prove that no completion has claimed the attempt in live memory — no such
    /// ordering is asserted here; the CALLER fences the live slot before it calls in.
    /// </para>
    /// <para>
    /// VALIDATE-BEFORE-ANY-I/O. <paramref name="goalId"/> and <paramref name="taskId"/> must be
    /// non-blank; <paramref name="expectedRegistryJson"/> is DECODED through
    /// <see cref="WorkSlotRegistryCodec.Decode"/> (null, malformed or unsupported payloads throw
    /// <see cref="WorkSlotRegistryCodecException"/>), and the COMPLETE detached registry plus the
    /// matching Pending task are validated through
    /// <see cref="GoalPipeline.PreflightAdmissionOwnership"/> (reused verbatim — the validator is
    /// NEVER duplicated here). Every rejection escapes BEFORE a context is resolved or any I/O
    /// happens: it is never reported as a database refusal. The SUPPLIED JSON — not the decoded
    /// registry, not a re-encoding — is the exact textual compare-and-swap expectation below.
    /// </para>
    /// <para>
    /// THE REPLACEMENT is built from the VALIDATED prior registry: ONLY the matching slot's state
    /// changes from Pending to Abandoned; task id, position and attempt identity, every other slot
    /// (in order) and every high-water entry are preserved EXACTLY. No pre-admission blob is
    /// restored and no task is allocated. The replacement is encoded BEFORE any database I/O.
    /// </para>
    /// <para>
    /// THE BODY, in ONE explicit transaction. Statement 1 is the conditional UPDATE of ONLY the
    /// existing pipeline row (<c>active_task_id = NULL</c>, the replacement blob), guarded by the
    /// goal id, the still-equal active pointer and the byte-exact raw-text equality against the
    /// ORIGINAL supplied JSON. Statement 2 — ONLY after statement 1 updated exactly one row —
    /// deletes the task mapping guarded by BOTH task id and goal id. EXACTLY ONE affected row from
    /// EACH statement is required for the commit; EXACTLY ZERO on either statement is the only
    /// refusal candidate (missing pipeline, null or newer pointer, stale registry text, missing or
    /// foreign mapping, or an already-retired repeated request — all refuse WITHOUT repair, retry
    /// or reconstruction, and a missing/foreign mapping ROLLS THE UPDATE BACK). EVERY other count,
    /// negative counts included, is an ERROR, never a refusal and never a commit. Nothing else is
    /// written: no SELECT-then-write guard, no upsert, no <c>SaveChanges</c>, no conversation,
    /// phase or scalar writes, no mutation of unrelated rows.
    /// </para>
    /// <para>
    /// THE OUTCOME PRECEDENCE mirrors <see cref="CommitAdmissionOwnership"/> exactly: preflight,
    /// context-acquisition and transaction-begin failures PROPAGATE the exact exception; a body
    /// error with a CONFIRMED rollback propagates the EXACT store-caught exception with its stack
    /// preserved; a confirmed-rollback clean refusal returns
    /// <see cref="AdmissionOwnershipCommitStatus.Refused"/> with no exception evidence; ANY throwing
    /// <c>Commit</c> is <see cref="AdmissionOwnershipCommitStatus.Indeterminate"/> even when it
    /// committed underneath; a THROWING rollback is
    /// <see cref="AdmissionOwnershipCommitStatus.Indeterminate"/> carrying the exact primary AND
    /// rollback evidence; and a confirmed Commit remains
    /// <see cref="AdmissionOwnershipCommitStatus.Committed"/> despite any later cleanup failure.
    /// </para>
    /// <para>
    /// HYGIENE preserved from <see cref="CommitAdmissionOwnership"/>: independently guarded
    /// transaction and factory-context disposal in finally blocks with never-masked guarantees,
    /// caller-owned context lifetime, key-scoped all-state tracker detachment after the SQL
    /// attempts (a later <c>SaveChanges</c> can never resurrect rolled-back state), diagnostics
    /// that never log a refused/rolled-back operation as committed.
    /// </para>
    /// </remarks>
    /// <param name="goalId">The goal id whose pipeline row and mapping row are rolled back.</param>
    /// <param name="taskId">The task id whose Pending slot is abandoned and whose pointer must
    /// still be on the row.</param>
    /// <param name="expectedRegistryJson">The RAW registry text the row must currently hold — the
    /// ORIGINAL supplied string, compared byte-exactly, never re-encoded.</param>
    /// <returns>The recorded outcome and, when the outcome is uncertain, the exception evidence.</returns>
    /// <exception cref="ArgumentException">A blank identity, a malformed decoded registry, or a
    /// target that is absent or not Pending.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="expectedRegistryJson"/> is <c>null</c>.</exception>
    /// <exception cref="WorkSlotRegistryCodecException">The supplied JSON is malformed or unsupported.</exception>
    internal AdmissionOwnershipCommitResult CommitPendingAdmissionRollback(
        string? goalId,
        string? taskId,
        string? expectedRegistryJson)
    {
        // PHASE 0 — THE ARGUMENT GUARDS, before ANY decode (the codec throws on null JSON; the
        // identities are checked first so blank identity + null JSON still reports identity).
        if (string.IsNullOrWhiteSpace(goalId))
            throw new ArgumentException(
                $"Pending-admission rollback goal ID must be a non-blank string ('{goalId}').", nameof(goalId));
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException(
                $"Pending-admission rollback task ID must be a non-blank string ('{taskId}').", nameof(taskId));
        ArgumentNullException.ThrowIfNull(expectedRegistryJson);

        // PHASE 1 — THE DECODE and THE FULL DETACHED VALIDATION, both BEFORE any database
        // interaction (the decode is the malformed/unsupported-JSON guard; the preflight is the
        // registry-history and Pending-target guard). Every rejection escapes here.
        var decoded = WorkSlotRegistryCodec.Decode(expectedRegistryJson);

        var candidate = new AdmissionOwnershipSnapshot(goalId, taskId, decoded);
        var validated = GoalPipeline.PreflightAdmissionOwnership(candidate);

        // THE REPLACEMENT — built from the VALIDATED prior registry. ONLY the matching slot's
        // state changes (Pending → Abandoned); identity, order, other slots and every high-water
        // entry are preserved exactly. Encoded BEFORE any database I/O.
        var replacement = new WorkSlotRegistrySnapshot(
            validated.Registry.Slots
                .Select(view => string.Equals(view.Slot.TaskId, taskId, StringComparison.Ordinal)
                    ? new WorkSlotView(view.Slot, WorkSlotState.Abandoned)
                    : view)
                .ToList(),
            validated.Registry.DispatchAttempts.ToList());
        var replacementEncoded = WorkSlotRegistryCodec.Encode(replacement);

        // THE CAS EXPECTATION IS THE ORIGINAL SUPPLIED TEXT — never the replacement's encoding,
        // never the decode-reencode round trip.
        var expected = expectedRegistryJson;

        var (outcome, toThrow) = RunOwnershipTransaction<AdmissionOwnershipCommitResult>(
            validated,
            expected,
            (db, validatedGoalId, validatedTaskId, _) =>
            {
                // STATEMENT 1 — the conditional pointer-clear + replacement-blob UPDATE.
                var pipelineRows = db.Database.ExecuteSqlRaw(
                    PendingRollbackGuardSql,
                    new SqliteParameter("$goal", validatedGoalId),
                    new SqliteParameter("$task", validatedTaskId),
                    new SqliteParameter("$registry", replacementEncoded),
                    new SqliteParameter("$expected", (object?)expected ?? DBNull.Value));

                if (pipelineRows is not (0 or 1))
                {
                    // NOT a refusal: the goal id is the primary key, so ANY count other than the
                    // two legal ones — negative provider counts just as much as counts above 1 —
                    // is an integrity surprise the caller must see as an ERROR.
                    throw new InvalidOperationException(
                        $"Pending admission rollback guard updated {pipelineRows} pipeline rows for goal '{validatedGoalId}' (expected exactly 0 or 1).");
                }

                if (pipelineRows == 0)
                {
                    // THE REFUSAL CANDIDATE — the row is missing, the pointer is null or newer,
                    // the registry text is stale, or the request is a repeated already-retired
                    // one. No read-back, no repair, no retry.
                    return OwnershipBodyOutcome<AdmissionOwnershipCommitResult>.RefusedOutcome(AdmissionOwnershipCommitResult.RefusedResult());
                }

                // EXACTLY ONE pipeline row was updated — STATEMENT 2 may now run.
                var mappingRows = db.Database.ExecuteSqlRaw(
                    PendingRollbackMappingSql,
                    new SqliteParameter("$task", validatedTaskId),
                    new SqliteParameter("$goal", validatedGoalId));

                if (mappingRows is not (0 or 1))
                {
                    // The same rule: every count other than 0 and 1 is an ERROR.
                    throw new InvalidOperationException(
                        $"Pending admission rollback mapping delete affected {mappingRows} rows for task '{validatedTaskId}' (expected exactly 0 or 1).");
                }

                // Zero deleted rows means the mapping is missing or FOREIGN (a newer goal
                // claimed it) — a refusal candidate that ROLLS THE UPDATE BACK.
                return mappingRows == 0
                    ? OwnershipBodyOutcome<AdmissionOwnershipCommitResult>.RefusedOutcome(
                        AdmissionOwnershipCommitResult.RefusedResult())
                    : OwnershipBodyOutcome<AdmissionOwnershipCommitResult>.CommittedOutcome(
                        AdmissionOwnershipCommitResult.Committed());
            },
            new OwnershipTransactionDiagnostics(
                PendingRollbackDetachTemplate,
                PendingRollbackTransactionDisposeTemplate,
                PendingRollbackContextDisposeTemplate));

        return FinishOwnershipOutcome(outcome, toThrow);
    }

    /// <summary>
    /// Persists an encoded work-slot registry snapshot onto an EXISTING pipeline row, writing ONLY
    /// the <c>work_slot_registry_json</c> column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ENCODE-BEFORE-TOUCH: the snapshot is encoded BEFORE any database interaction, so an encode
    /// failure propagates having performed NO write at all (and having created no context).
    /// </para>
    /// <para>
    /// EXISTING ROWS ONLY: a single column-scoped <c>ExecuteUpdate</c> matches the row by goal id.
    /// A missing row affects zero rows and is reported as <c>false</c> — an incomplete pipeline row
    /// is NEVER created as a side effect. Nothing else is written: no scalars, no task mappings, no
    /// conversation, no timestamps, and not the active pointer.
    /// </para>
    /// <para>
    /// TRACKER COHERENCE: <c>ExecuteUpdate</c> bypasses the change tracker, so on the direct
    /// (test-owned) context path a previously tracked <see cref="PipelineEntity"/> would keep the
    /// stale pre-write value and hand it back to the next <c>Find</c>. The tracked copy is
    /// therefore refreshed in place and the refreshed property is marked unmodified so it is not
    /// re-flushed by an unrelated later <c>SaveChanges</c>.
    /// </para>
    /// <para>
    /// FAILURES PROPAGATE: a store failure is logged and rethrown; the previously persisted blob is
    /// left exactly as it was.
    /// </para>
    /// <para>
    /// INERT BY DESIGN: nothing in production calls this yet. Storing a snapshot is NOT a decision
    /// to restore one — the restore validation stays with <c>GoalPipeline.RestoreRegistry</c>,
    /// which a HELD restore now invokes to hydrate stored evidence; that hydration confers no
    /// recovery, replay or dispatch authority and never makes a held instance checkpoint-eligible.
    /// </para>
    /// </remarks>
    /// <param name="goalId">The goal id whose pipeline row receives the snapshot.</param>
    /// <param name="snapshot">The registry snapshot to encode and store.</param>
    /// <returns><c>true</c> when the row existed and was updated; <c>false</c> when no row matched.</returns>
    /// <exception cref="ArgumentException"><paramref name="goalId"/> is null, empty, or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
    internal bool SaveWorkSlotRegistry(string goalId, WorkSlotRegistrySnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentNullException.ThrowIfNull(snapshot);

        // PHASE 1 — the encode, BEFORE any database interaction. A codec failure escapes here with
        // no context created and no statement issued.
        var encoded = WorkSlotRegistryCodec.Encode(snapshot);

        // PHASE 2 — the column-scoped write.
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            var affected = db.Pipelines
                .Where(p => p.GoalId == goalId)
                .ExecuteUpdate(setters => setters.SetProperty(p => p.WorkSlotRegistryJson, encoded));

            if (affected <= 0)
                return false;

            RefreshTrackedRegistryBlob(db, goalId, encoded);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save work-slot registry snapshot for goal {GoalId}", goalId);
            throw;
        }
        finally
        {
            if (ownsContext)
                db.Dispose();
        }
    }

    /// <summary>
    /// Re-synchronises a tracked <see cref="PipelineEntity"/> with the value just written by
    /// <c>ExecuteUpdate</c>, so the tracker cannot hand back the stale blob to a later
    /// <c>Find</c>/load on the direct-context path.
    /// <para>
    /// BOTH VALUES ARE MOVED. The row genuinely holds the encoded blob now, so it is the tracked
    /// entity's ORIGINAL value as well as its current one. Assigning only the current value would
    /// leave the property looking modified, and clearing <c>IsModified</c> afterwards would then
    /// roll the current value back to the stale original — the refresh would silently undo itself.
    /// With both values moved, the column matches the row and no unrelated later
    /// <c>SaveChanges</c> re-flushes it.
    /// </para>
    /// </summary>
    private static void RefreshTrackedRegistryBlob(CopilotHiveDbContext db, string goalId, string encoded)
    {
        var tracked = db.ChangeTracker.Entries<PipelineEntity>()
            .FirstOrDefault(e => string.Equals(e.Entity.GoalId, goalId, StringComparison.Ordinal));
        if (tracked is null)
            return;

        // Added/Deleted entities have no meaningful persisted original value to move; only the
        // current value is synchronised for them.
        var hasPersistedOriginal = tracked.State is EntityState.Unchanged or EntityState.Modified;

        var property = tracked.Property(p => p.WorkSlotRegistryJson);
        if (hasPersistedOriginal)
            property.OriginalValue = encoded;
        property.CurrentValue = encoded;
        if (hasPersistedOriginal)
            property.IsModified = false;
    }

    /// <summary>
    /// TRUE iff the exception chain carries a <see cref="SqliteException"/> with
    /// <c>SqliteErrorCode == 19</c> AND <c>SqliteExtendedErrorCode == 1555</c> (SQLITE_CONSTRAINT_PRIMARYKEY).
    /// Every other code — NOTNULL (1299), UNIQUE (2067), CHECK (275), FK (787), BUSY (5),
    /// LOCKED (6) — stays on the generic propagate path.
    /// </summary>
    private static bool IsPrimaryKeyViolation(DbUpdateException exception)
    {
        for (var inner = (Exception?)exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqliteException sqlite
                && sqlite.SqliteErrorCode == 19
                && sqlite.SqliteExtendedErrorCode == 1555)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// THE FINAL GUARD of the never-masked guarantee: a throwing LOGGER must never mask the
    /// original outcome or the original exception, so the warning write itself is wrapped in a
    /// try/catch with a SILENT swallow (there is no deeper channel to report to).
    /// </summary>
    private void BestEffortWarning(string template, params object[] args)
    {
        try
        {
            _logger.LogWarning(template, args);
        }
        catch
        {
            // SILENT swallow — see the summary above.
        }
    }

    /// <summary>
    /// THE FROZEN OWNERSHIP PAIR an eligible ordinary checkpoint writes: the captured active-task
    /// pointer (which may legitimately be <c>null</c>) and the ALREADY-ENCODED registry blob, both
    /// taken from ONE detached <see cref="AdmissionOwnershipSnapshot"/>. It exists because the
    /// ADMISSION's validated snapshot carries the pointer separately from the frozen pair: the
    /// pair's presence IS the override, so a captured <c>null</c> pointer is written as SQL NULL
    /// and never falls back to a later live-pointer read of <see cref="GoalPipeline.ActiveTaskId"/>.
    /// </summary>
    private readonly record struct FrozenOwnershipCheckpoint(string? ActiveTaskId, string EncodedRegistryJson);

    private static void UpsertPipelineCore(CopilotHiveDbContext db, GoalPipeline pipeline) =>
        UpsertPipelineCore(db, pipeline, activeTaskIdOverride: null, checkpoint: null);

    private static void UpsertPipelineCore(CopilotHiveDbContext db, GoalPipeline pipeline, string? activeTaskIdOverride) =>
        UpsertPipelineCore(db, pipeline, activeTaskIdOverride, checkpoint: null);

    private static void UpsertPipelineCore(
        CopilotHiveDbContext db, GoalPipeline pipeline, string? activeTaskIdOverride,
        FrozenOwnershipCheckpoint? checkpoint)
    {
        var existing = db.Pipelines.Find(pipeline.GoalId);
        if (existing is not null)
        {
            ApplyToEntity(pipeline, existing, activeTaskIdOverride, checkpoint);
        }
        else
        {
            var entity = new PipelineEntity
            {
                GoalId = pipeline.GoalId,
            };
            ApplyToEntity(pipeline, entity, activeTaskIdOverride, checkpoint);
            db.Pipelines.Add(entity);
        }
    }

    private static void ApplyToEntity(
        GoalPipeline pipeline, PipelineEntity entity, string? activeTaskIdOverride,
        FrozenOwnershipCheckpoint? checkpoint)
    {
        entity.Description = pipeline.Description;
        entity.GoalJson = JsonSerializer.Serialize(pipeline.Goal, JsonOptions);
        entity.Phase = pipeline.Phase.ToString();
        entity.Iteration = pipeline.Iteration;
        entity.ReviewRetries = pipeline.ReviewRetries;
        entity.TestRetries = pipeline.TestRetries;
        entity.MaxRetries = pipeline.MaxRetries;
        entity.MaxIterations = pipeline.MaxIterations;
        // THE OVERRIDE, in precedence order:
        //   (1) a frozen checkpoint → its captured ActiveTaskId VERBATIM, including null (SQL NULL),
        //       so a captured null can never fall back to a later live-pointer read of
        //       pipeline.ActiveTaskId. (A captured NULL is reachable ONLY from an ORDINARY eligible
        //       checkpoint — SavePipeline/SavePipelineState — where an idle pointer is legitimate.
        //       The eligible ADMISSION route can never freeze a null: its reused preflight rejects a
        //       blank active task AND requires a matching Pending slot, so a captured null is
        //       refused there before any context exists.)
        //   (2) the admission's IMMUTABLE validated task id (passed through by
        //       SaveAdmissionWithPointer from GoalPipelineManager.PersistAdmission on the production
        //       dispatch path) → the exact value the caller supplied and the store validated; it is
        //       never re-read live at this point;
        //   (3) neither → the existing LATE read at this point: behavior IDENTICAL under all
        //       concurrency (the capture point unchanged) for every legacy/direct caller.
        entity.ActiveTaskId = checkpoint is { } frozen
            ? frozen.ActiveTaskId
            : activeTaskIdOverride ?? pipeline.ActiveTaskId;

        // THE SAME-ROW REGISTRY CHECKPOINT. When the caller is an eligible manager save, the blob
        // captured alongside that very pointer is installed in this SAME pipeline-row write — no
        // second, independent write. When it is not (every legacy/direct caller, and every
        // ineligible restore-constructed pipeline), the column is left EXACTLY as it was: this
        // method never reads, captures, clears or re-encodes it, so an existing blob survives
        // byte-for-byte.
        if (checkpoint is { } ownership)
            entity.WorkSlotRegistryJson = ownership.EncodedRegistryJson;

        entity.CoderBranch = pipeline.CoderBranch;
        entity.PlanJson = pipeline.Plan is not null ? JsonSerializer.Serialize(pipeline.Plan, JsonOptions) : null;
        entity.MetricsJson = JsonSerializer.Serialize(pipeline.Metrics, JsonOptions);
        entity.CreatedAt = pipeline.CreatedAt.ToString("O");
        entity.CompletedAt = pipeline.CompletedAt.HasValue ? pipeline.CompletedAt.Value.ToString("O") : null;
        entity.GoalStartedAt = pipeline.GoalStartedAt.HasValue ? pipeline.GoalStartedAt.Value.ToString("O") : null;
        entity.MergeCommitHash = pipeline.MergeCommitHash;
        entity.RoleSessionsJson = JsonSerializer.Serialize(
            pipeline.RoleSessions.GetAll().ToDictionary(kv => kv.Key, kv => kv.Value), JsonOptions);
        entity.IterationStartSha = pipeline.IterationStartSha;
        entity.PhaseLogJson = pipeline.PhaseLog.Count > 0
            ? JsonSerializer.Serialize(pipeline.PhaseLog, JsonOptions)
            : null;

        // THE COHERENT PAIR. The machine-captured position — phase AND occurrence together,
        // computed under the machine lock by CaptureMachinePosition — is persisted as one
        // atomic pair. entity.Phase stays the pipeline property; the divergence between the
        // two views is resolved at RESTORE (the pair-match rule), not here.
        // THE NULL CONTRACT: whenever OccurrenceFound == false (no installed plan at save
        // time — e.g. the honest re-plan window), MachinePhase is persisted NULL: the honest
        // "no position" marker that routes the restore down the legacy path.
        var machinePosition = pipeline.CaptureMachinePosition();
        entity.MachinePhase = machinePosition.OccurrenceFound ? machinePosition.Phase.ToString() : null;
        entity.PhaseOccurrence = machinePosition.OccurrenceFound ? Math.Max(1, machinePosition.Occurrence) : 1;
    }

    private static PipelineSnapshot ToSnapshot(PipelineEntity entity)
    {
        return new PipelineSnapshot
        {
            GoalId = entity.GoalId,
            Description = entity.Description,
            Goal = JsonSerializer.Deserialize<Goal>(entity.GoalJson, JsonOptions)!,
            Phase = Enum.Parse<GoalPhase>(entity.Phase),
            Iteration = entity.Iteration,
            ReviewRetries = entity.ReviewRetries,
            TestRetries = entity.TestRetries,
            MaxRetries = entity.MaxRetries,
            MaxIterations = entity.MaxIterations,
            ActiveTaskId = entity.ActiveTaskId,
            CoderBranch = entity.CoderBranch,
            Metrics = JsonSerializer.Deserialize<IterationMetrics>(entity.MetricsJson, JsonOptions) ?? new() { Iteration = 1 },
            CreatedAt = DateTime.Parse(entity.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            CompletedAt = entity.CompletedAt is null ? null : DateTime.Parse(entity.CompletedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            GoalStartedAt = entity.GoalStartedAt is null ? null : DateTime.Parse(entity.GoalStartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Plan = entity.PlanJson is null ? null : JsonSerializer.Deserialize<IterationPlan>(entity.PlanJson, JsonOptions),
            MergeCommitHash = entity.MergeCommitHash,
            RoleSessions = JsonSerializer.Deserialize<Dictionary<string, string>>(
                string.IsNullOrEmpty(entity.RoleSessionsJson) ? "{}" : entity.RoleSessionsJson, JsonOptions) ?? [],
            IterationStartSha = entity.IterationStartSha,
            PhaseLog = entity.PhaseLogJson is null ? []
                : JsonSerializer.Deserialize<List<PhaseResult>>(entity.PhaseLogJson, JsonOptions) ?? [],
            PhaseOccurrence = entity.PhaseOccurrence,
            MachinePhase = ParseMachinePhase(entity.MachinePhase),
            // THE RAW CARRIER: copied VERBATIM, never parsed or decoded here. A SQL NULL stays
            // null (the legacy-absence marker) and any stored text — including malformed text —
            // is handed over unchanged for an explicit caller to decode later.
            WorkSlotRegistryJson = entity.WorkSlotRegistryJson,
        };
    }

    /// <summary>
    /// Parses the persisted machine-phase name back into a <see cref="GoalPhase"/>, or
    /// <c>null</c> for a null or unrecognized value — an unreadable marker must fall through
    /// to the legacy restore path, never invent a phase.
    /// <para>
    /// CANONICAL-NAME RECOGNITION ONLY — the exact inverse of the write side, which always
    /// emits <c>Phase.ToString()</c>. The value is accepted ONLY when it equals one canonical
    /// <see cref="GoalPhase"/> name under an ordinal case-insensitive comparison; NO enum
    /// expression parsing is performed at all. This closes every
    /// <c>Enum.TryParse</c> false-acceptance class in one rule:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>COMMA-SEPARATED EXPRESSIONS. <c>Enum.TryParse</c> combines
    ///     comma-separated names by bitwise OR even though <see cref="GoalPhase"/> is not a
    ///     flags enum — "Planning,Coding" and "Coding,Coding" yield the DEFINED value
    ///     <c>Coding</c>, "Coding,Review" and "Coding, Testing" yield the DEFINED value
    ///     <c>Testing</c> — so <c>Enum.IsDefined</c> cannot detect them. Rejected here.</description></item>
    ///   <item><description>NUMERIC STRINGS. "999" parses to an UNDEFINED value; "0", "1",
    ///     "+1" parse to DEFINED members. No numeric form is a canonical name. Rejected.</description></item>
    ///   <item><description>WHITESPACE FORMS. <c>Enum.TryParse</c> tolerates surrounding
    ///     whitespace (" Coding", "Coding ", "\tCoding", "Coding\n"). The persisted value is
    ///     always written by <c>Phase.ToString()</c>, so any whitespace means a corrupt
    ///     row. Rejected.</description></item>
    ///   <item><description>Empty, blank, garbage ("not-a-phase") and non-ASCII digit forms
    ///     never match a name. Rejected.</description></item>
    /// </list>
    /// <para>
    /// PRESERVED: case-insensitive recognition of a defined name — "CODING", "coding" and
    /// "CoDiNg" all yield <see cref="GoalPhase.Coding"/>.
    /// </para>
    /// </summary>
    private static GoalPhase? ParseMachinePhase(string? machinePhase)
    {
        if (string.IsNullOrEmpty(machinePhase))
            return null;

        foreach (var phase in Enum.GetValues<GoalPhase>())
        {
            if (string.Equals(phase.ToString(), machinePhase, StringComparison.OrdinalIgnoreCase))
                return phase;
        }

        return null;
    }

    private static void SaveConversationCore(CopilotHiveDbContext db, GoalPipeline pipeline)
    {
        var existing = db.ConversationEntries.Where(e => e.GoalId == pipeline.GoalId).ToList();
        if (existing.Count > 0)
            db.ConversationEntries.RemoveRange(existing);

        for (var i = 0; i < pipeline.Conversation.Count; i++)
        {
            var entry = pipeline.Conversation[i];
            db.ConversationEntries.Add(new ConversationEntryEntity
            {
                GoalId = pipeline.GoalId,
                Seq = i,
                Role = entry.Role,
                Content = entry.Content,
                Iteration = entry.Iteration,
                Purpose = entry.Purpose,
            });
        }
    }

    private static List<ConversationEntry> LoadConversationCore(CopilotHiveDbContext db, string goalId)
    {
        return db.ConversationEntries
            .Where(e => e.GoalId == goalId)
            .OrderBy(e => e.Seq)
            .Select(e => new ConversationEntry(e.Role, e.Content, e.Iteration, e.Purpose))
            .ToList();
    }

    private static List<(string TaskId, string GoalId)> LoadTaskMappingsCore(CopilotHiveDbContext db, string goalId)
    {
        return db.TaskMappings
            .Where(t => t.GoalId == goalId)
            .Select(t => new { t.TaskId, t.GoalId })
            .AsEnumerable()
            .Select(t => (t.TaskId, t.GoalId))
            .ToList();
    }

    /// <summary>No-op: contexts are either factory-created and disposed per operation, or test-owned.</summary>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Snapshot of a persisted pipeline for restart recovery.
/// </summary>
public sealed class PipelineSnapshot
{
    /// <summary>Unique identifier of the goal this pipeline tracks.</summary>
    public required string GoalId { get; init; }
    /// <summary>Human-readable description of the goal.</summary>
    public required string Description { get; init; }
    /// <summary>The goal this pipeline is working toward.</summary>
    public required Goal Goal { get; init; }
    /// <summary>Current phase of the pipeline at the time it was persisted.</summary>
    public GoalPhase Phase { get; init; }
    /// <summary>Current iteration number.</summary>
    public int Iteration { get; init; }
    /// <summary>Number of review retries consumed so far.</summary>
    public int ReviewRetries { get; init; }
    /// <summary>Number of test retries consumed so far.</summary>
    public int TestRetries { get; init; }
    /// <summary>Maximum retries allowed per task.</summary>
    public int MaxRetries { get; init; } = Constants.DefaultMaxRetriesPerTask;
    /// <summary>Maximum iterations allowed before the goal is failed.</summary>
    public int MaxIterations { get; init; } = Constants.DefaultMaxIterations;
    /// <summary>Task ID currently assigned to a worker, or <c>null</c> when idle.</summary>
    public string? ActiveTaskId { get; init; }
    /// <summary>Feature branch created by the coder, or <c>null</c> if coding has not started.</summary>
    public string? CoderBranch { get; init; }
    /// <summary>Brain-determined iteration plan, or <c>null</c> if not yet planned.</summary>
    public IterationPlan? Plan { get; init; }
    /// <summary>Metrics captured during this iteration.</summary>
    public IterationMetrics Metrics { get; init; } = new() { Iteration = 1 };
    /// <summary>UTC timestamp when the pipeline was created.</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>UTC timestamp when the pipeline completed, or <c>null</c> if still active.</summary>
    public DateTime? CompletedAt { get; init; }
    /// <summary>UTC timestamp when the goal was started (captured at dispatch time).</summary>
    public DateTime? GoalStartedAt { get; init; }
    /// <summary>Conversation history for the Brain session associated with this pipeline.</summary>
    public List<ConversationEntry> Conversation { get; set; } = [];
    /// <summary>List of (TaskId, GoalId) pairs for task-to-goal resolution.</summary>
    public List<(string TaskId, string GoalId)> TaskMappings { get; set; } = [];
    /// <summary>SHA-1 hash of the merge commit for this pipeline's changes, or <c>null</c> if not yet merged.</summary>
    public string? MergeCommitHash { get; init; }
    /// <summary>Persisted agent session JSON blobs, keyed by role name.</summary>
    public Dictionary<string, string> RoleSessions { get; init; } = [];
    /// <summary>
    /// HEAD SHA of the target repository captured on the worker's feature-branch clone immediately
    /// before the coder agent ran for the current iteration. Used to compute an iteration-scoped
    /// diff (<c>git diff {sha}..HEAD</c>) for reviewers. <c>null</c> when not yet captured or not applicable.
    /// </summary>
    public string? IterationStartSha { get; init; }
    /// <summary>Append-only log of phase entries recorded during this pipeline's execution.</summary>
    public List<PhaseResult> PhaseLog { get; init; } = [];
    /// <summary>
    /// 1-based occurrence of the pipeline's phase within the persisted plan, captured from the
    /// state machine at save time. Defaults to 1 for pre-existing rows.
    /// </summary>
    public int PhaseOccurrence { get; init; } = 1;
    /// <summary>
    /// The machine-captured phase PAIRED with <see cref="PhaseOccurrence"/> at the same save,
    /// or <c>null</c> when the capture found no installed plan (the honest "no position"
    /// marker — e.g. the re-plan window). Null also on old rows predating this column.
    /// The pair-match rule at restore trusts <see cref="PhaseOccurrence"/> only when this
    /// phase equals the snapshot's pipeline phase; null or mismatched → the legacy path.
    /// </summary>
    public GoalPhase? MachinePhase { get; init; }

    /// <summary>
    /// The RAW, still-encoded work-slot registry blob exactly as it sits in the
    /// <c>work_slot_registry_json</c> column, or <c>null</c> when the column is SQL NULL.
    /// <para>
    /// A CARRIER ONLY — the load path copies it verbatim and NEVER decodes it, so a malformed value
    /// cannot break a load. SQL NULL ("no snapshot supplied / legacy absence") stays distinct from a
    /// stored version-1 payload holding two empty collections ("an empty registry was captured").
    /// </para>
    /// <para>
    /// INTERNAL on purpose: no public surface exposes the registry domain. The RESTORE path now
    /// reads this carrier, but ONLY for a HELD instance
    /// (<see cref="GoalPipeline.IsRestoredActiveAttemptHold"/>), which decodes it and hydrates the
    /// domain-valid slot/counter evidence as EVIDENCE ONLY — no recovery, replay or dispatch
    /// authority is conferred, held instances never become checkpoint-eligible, and the existing
    /// full/state saves still preserve these raw bytes byte-for-byte. UNHELD (null-pointer or
    /// terminal-phase) restores and fresh <c>Goal</c>-created pipelines ignore it entirely, exactly
    /// as before. The LOAD path itself is unchanged and still never decodes it.
    /// </para>
    /// </summary>
    internal string? WorkSlotRegistryJson { get; init; }
}

/// <summary>
/// The outcome of <see cref="PipelineStore.SaveAdmissionWithPointer(GoalPipeline, string)"/>, shared
/// by both of its routes (the legacy and the ownership-aware overloads).
/// </summary>
internal enum AdmissionStoreResult
{
    /// <summary>The mapping row and the pipeline row landed atomically.</summary>
    Committed,
    /// <summary>The mapping row already exists — nothing was written (the store's admission
    /// refusal).</summary>
    PersistConflict,
}

/// <summary>The persisted-pointer rollback's outcome — the three distinguishable truths.</summary>
/// <remarks>Cleared: the row's pointer matched and is now NULL (the SQL updated it).
/// NotMatched: the row was ABSENT or its pointer did NOT equal the expected taskId — the
/// ownership-check invariant held; NOTHING FURTHER TO UNDO.
/// Failed: a guarded phase threw (the acquisition, the SQL) — the row's state is UNKNOWN; the
/// (a) WARNING emitted; the durable-reconciliation successor owns the residue.</remarks>
internal enum PointerRollbackResult { Cleared, NotMatched, Failed }

/// <summary>
/// The recorded outcome of <see cref="PipelineStore.CommitAdmissionOwnership"/> — three truths,
/// deliberately including the one that cannot be resolved from inside the store.
/// </summary>
internal enum AdmissionOwnershipCommitStatus
{
    /// <summary>
    /// <c>Commit</c> RETURNED: the pointer, the registry blob and the mapping row landed together.
    /// Recorded the instant the commit returned — no later cleanup, disposal or logging failure can
    /// downgrade or undo it.
    /// </summary>
    Committed,

    /// <summary>
    /// A guard said no and the rollback was CONFIRMED: either the conditional pipeline UPDATE
    /// matched no row (missing row, pointer already taken, or a registry text that is not the
    /// expected one) or the mapping insert inserted nothing because a row for the task already
    /// existed (for ANY goal, the same one included). NO reason taxonomy is offered and no
    /// read-back is performed to invent one. The no-durable-change guarantee applies ONLY to this
    /// confirmed-rollback path.
    /// </summary>
    Refused,

    /// <summary>
    /// The transaction's fate is UNKNOWN — a throwing rollback (whatever the body did, even
    /// nothing) or ANY exception from <c>Commit</c>, which can be raised AFTER the underlying
    /// commit already landed. Neither success nor absence of change may be inferred; the store
    /// performs no retry, repair or reconciliation, and the caller owns the policy.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// The result of <see cref="PipelineStore.CommitAdmissionOwnership"/>: the recorded
/// <see cref="Status"/> plus, for the uncertain outcomes, the EXACT exception objects the store
/// caught.
/// </summary>
/// <remarks>
/// EXCEPTION IDENTITY, not description: both properties carry the exact caught instances (any EF
/// wrapper included). Nothing is re-created, re-messaged, unwrapped or aggregated, so a caller can
/// assert identity. Both are <c>null</c> for <see cref="AdmissionOwnershipCommitStatus.Committed"/>
/// and <see cref="AdmissionOwnershipCommitStatus.Refused"/>; for
/// <see cref="AdmissionOwnershipCommitStatus.Indeterminate"/>, <see cref="PrimaryException"/> is
/// the commit exception or the original body exception when one exists (otherwise <c>null</c> — a
/// rollback can throw after a clean body) and <see cref="RollbackException"/> is retained
/// SEPARATELY.
/// </remarks>
/// <param name="Status">The recorded outcome.</param>
/// <param name="PrimaryException">The exact commit or body exception, when one was caught.</param>
/// <param name="RollbackException">The exact rollback exception, when the rollback threw.</param>
internal sealed record AdmissionOwnershipCommitResult(
    AdmissionOwnershipCommitStatus Status,
    Exception? PrimaryException,
    Exception? RollbackException)
{
    /// <summary>The confirmed-commit result — no exception evidence.</summary>
    internal static AdmissionOwnershipCommitResult Committed() =>
        new(AdmissionOwnershipCommitStatus.Committed, null, null);

    /// <summary>The confirmed-rollback guard refusal — no exception evidence, no reason.</summary>
    internal static AdmissionOwnershipCommitResult RefusedResult() =>
        new(AdmissionOwnershipCommitStatus.Refused, null, null);

    /// <summary>The unresolvable outcome, retaining the exact evidence objects as caught.</summary>
    /// <param name="primary">The commit or body exception, or <c>null</c> when there was none.</param>
    /// <param name="rollback">The rollback exception, or <c>null</c> when the rollback did not throw.</param>
    internal static AdmissionOwnershipCommitResult Uncertain(Exception? primary, Exception? rollback) =>
        new(AdmissionOwnershipCommitStatus.Indeterminate, primary, rollback);
}
