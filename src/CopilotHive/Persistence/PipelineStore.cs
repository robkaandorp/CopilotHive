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
