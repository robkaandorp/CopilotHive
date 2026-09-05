using System.Globalization;
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
    /// THE DISPOSAL SEAM for the factory-owned contexts — used by
    /// <see cref="SaveAdmissionWithPointer"/> and <see cref="ClearActiveTaskIdIfMatches"/>. When
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

    /// <summary>Test seam: substitutes the tracker-detach step for ClearActiveTaskIdIfMatches's
    /// hygiene phase. Null in production (the real DetachTrackedPipeline runs); a test-installed
    /// delegate replaces the whole step (the forced-failure injection).</summary>
    internal Action<CopilotHiveDbContext, string>? TrackerDetachForTest;

    private const string PointerRollbackFailureTemplate =
        "WorkSlotIntegrity: pointer-rollback-failure goal={GoalId} task={TaskId} — the persisted pointer's rollback failed; a restart may restore the stale pointer; the completion-protocol successor owns the durable reconciliation";
    private const string PointerRollbackCleanupTemplate =
        "WorkSlotIntegrity: pointer-rollback-cleanup goal={GoalId} task={TaskId} — the post-update cleanup failed; pointer-cleared={Cleared}; the context state is suspect";

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
    public void SavePipeline(GoalPipeline pipeline)
    {
        var (db, ownsContext) = ResolveDbContext();
        try
        {
            UpsertPipelineCore(db, pipeline);
            SaveConversationCore(db, pipeline);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save pipeline for goal {GoalId}", pipeline.GoalId);
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

            // STAGE 2 — THE PIPELINE ROW.
            UpsertPipelineCore(db, pipeline);
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
                    BestEffortWarning(
                        "WorkSlotIntegrity: admission-rollback goal={GoalId} task={TaskId} — the rollback step failed (unconfirmed; the detach-only cleanup will be used): {Message}",
                        pipeline.GoalId, taskId, rollbackEx.Message);
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
                BestEffortWarning(
                    "WorkSlotIntegrity: admission-cleanup goal={GoalId} task={TaskId} — the tracked-state cleanup failed: {Message}",
                    pipeline.GoalId, taskId, cleanupEx.Message);
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
                BestEffortWarning(
                    "WorkSlotIntegrity: admission-dispose goal={GoalId} task={TaskId} — the transaction dispose failed: {Message}",
                    pipeline.GoalId, taskId, disposeEx.Message);
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
                    BestEffortWarning(
                        "WorkSlotIntegrity: admission-context-dispose goal={GoalId} task={TaskId} — the context dispose failed: {Message}",
                        pipeline.GoalId, taskId, contextDisposeEx.Message);
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
    /// UNUSED BY DESIGN in this slice: the API is complete and tested, but no production caller
    /// exists yet — the dispatch path's migration onto it belongs to the successor slice.
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

    private static void UpsertPipelineCore(CopilotHiveDbContext db, GoalPipeline pipeline)
    {
        var existing = db.Pipelines.Find(pipeline.GoalId);
        if (existing is not null)
        {
            ApplyToEntity(pipeline, existing);
        }
        else
        {
            var entity = new PipelineEntity
            {
                GoalId = pipeline.GoalId,
            };
            ApplyToEntity(pipeline, entity);
            db.Pipelines.Add(entity);
        }
    }

    private static void ApplyToEntity(GoalPipeline pipeline, PipelineEntity entity)
    {
        entity.Description = pipeline.Description;
        entity.GoalJson = JsonSerializer.Serialize(pipeline.Goal, JsonOptions);
        entity.Phase = pipeline.Phase.ToString();
        entity.Iteration = pipeline.Iteration;
        entity.ReviewRetries = pipeline.ReviewRetries;
        entity.TestRetries = pipeline.TestRetries;
        entity.MaxRetries = pipeline.MaxRetries;
        entity.MaxIterations = pipeline.MaxIterations;
        entity.ActiveTaskId = pipeline.ActiveTaskId;
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
}

/// <summary>
/// The outcome of <see cref="PipelineStore.SaveAdmissionWithPointer"/>.
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
