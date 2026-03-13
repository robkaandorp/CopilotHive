using System.Globalization;
using System.Text.Json;
using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Persistence;

/// <summary>
/// Persists GoalPipeline state to SQLite so the orchestrator can recover after restarts.
/// Thread-safe: all writes are serialized via a lock around the single connection.
/// </summary>
public sealed class PipelineStore : IAsyncDisposable
{
    private readonly SqliteConnection _db;
    private readonly ILogger<PipelineStore> _logger;
    private readonly Lock _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Initialises a new <see cref="PipelineStore"/>, opening or creating the SQLite database at the given path.
    /// </summary>
    /// <param name="dbPath">Full path to the SQLite database file.</param>
    /// <param name="logger">Logger instance.</param>
    public PipelineStore(string dbPath, ILogger<PipelineStore> logger)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitializeSchema();
        _logger.LogInformation("PipelineStore initialized at {DbPath}", dbPath);
    }

    private void InitializeSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pipelines (
                goal_id           TEXT PRIMARY KEY,
                description       TEXT NOT NULL,
                goal_json         TEXT NOT NULL,
                phase             TEXT NOT NULL DEFAULT 'Planning',
                iteration         INTEGER NOT NULL DEFAULT 1,
                review_retries    INTEGER NOT NULL DEFAULT 0,
                test_retries      INTEGER NOT NULL DEFAULT 0,
                max_retries       INTEGER NOT NULL DEFAULT 3,
                active_task_id    TEXT,
                coder_branch      TEXT,
                phase_outputs     TEXT NOT NULL DEFAULT '{}',
                metrics_json      TEXT NOT NULL DEFAULT '{}',
                created_at        TEXT NOT NULL,
                completed_at      TEXT
            );

            CREATE TABLE IF NOT EXISTS conversation_entries (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                goal_id   TEXT NOT NULL,
                seq       INTEGER NOT NULL,
                role      TEXT NOT NULL,
                content   TEXT NOT NULL,
                FOREIGN KEY (goal_id) REFERENCES pipelines(goal_id)
            );

            CREATE INDEX IF NOT EXISTS idx_conversation_goal
                ON conversation_entries(goal_id, seq);

            CREATE TABLE IF NOT EXISTS task_mappings (
                task_id   TEXT PRIMARY KEY,
                goal_id   TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Insert or replace the full pipeline state.</summary>
    public void SavePipeline(GoalPipeline pipeline)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            try
            {
                UpsertPipelineCore(pipeline, tx);
                SaveConversationCore(pipeline, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>Persist only the pipeline's scalar state (phase, iteration, retries, etc.).</summary>
    public void SavePipelineState(GoalPipeline pipeline)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            try
            {
                UpsertPipelineCore(pipeline, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>Append a single conversation entry without rewriting the full conversation.</summary>
    public void AppendConversation(string goalId, ConversationEntry entry)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO conversation_entries (goal_id, seq, role, content)
                VALUES (@goalId, COALESCE((SELECT MAX(seq) FROM conversation_entries WHERE goal_id = @goalId), -1) + 1, @role, @content)
                """;
            cmd.Parameters.AddWithValue("@goalId", goalId);
            cmd.Parameters.AddWithValue("@role", entry.Role);
            cmd.Parameters.AddWithValue("@content", entry.Content);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Register a task → goal mapping for recovery.</summary>
    public void SaveTaskMapping(string taskId, string goalId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO task_mappings (task_id, goal_id) VALUES (@taskId, @goalId)";
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@goalId", goalId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Remove a completed/failed pipeline from the store.</summary>
    public void RemovePipeline(string goalId)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            try
            {
                Execute("DELETE FROM conversation_entries WHERE goal_id = @id", tx, ("@id", goalId));
                Execute("DELETE FROM task_mappings WHERE goal_id = @id", tx, ("@id", goalId));
                Execute("DELETE FROM pipelines WHERE goal_id = @id", tx, ("@id", goalId));
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>Load all non-terminal pipelines for restart recovery.</summary>
    public List<PipelineSnapshot> LoadActivePipelines()
    {
        lock (_lock)
        {
            var results = new List<PipelineSnapshot>();

            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT goal_id, description, goal_json, phase, iteration,
                       review_retries, test_retries, max_retries,
                       active_task_id, coder_branch, phase_outputs, metrics_json,
                       created_at, completed_at
                FROM pipelines
                WHERE phase NOT IN ('Done', 'Failed')
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var goalId = reader.GetString(0);
                results.Add(new PipelineSnapshot
                {
                    GoalId = goalId,
                    Description = reader.GetString(1),
                    Goal = JsonSerializer.Deserialize<Goal>(reader.GetString(2), JsonOptions)!,
                    Phase = Enum.Parse<GoalPhase>(reader.GetString(3)),
                    Iteration = reader.GetInt32(4),
                    ReviewRetries = reader.GetInt32(5),
                    TestRetries = reader.GetInt32(6),
                    MaxRetries = reader.GetInt32(7),
                    ActiveTaskId = reader.IsDBNull(8) ? null : reader.GetString(8),
                    CoderBranch = reader.IsDBNull(9) ? null : reader.GetString(9),
                    PhaseOutputs = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(10), JsonOptions) ?? [],
                    Metrics = JsonSerializer.Deserialize<IterationMetrics>(reader.GetString(11), JsonOptions) ?? new() { Iteration = 1 },
                    CreatedAt = DateTime.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    CompletedAt = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Conversation = LoadConversationCore(goalId),
                });
            }

            foreach (var snap in results)
                snap.TaskMappings = LoadTaskMappingsCore(snap.GoalId);

            _logger.LogInformation("Loaded {Count} active pipeline(s) from store", results.Count);
            return results;
        }
    }

    private void UpsertPipelineCore(GoalPipeline pipeline, SqliteTransaction tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO pipelines
                (goal_id, description, goal_json, phase, iteration,
                 review_retries, test_retries, max_retries,
                 active_task_id, coder_branch, phase_outputs, metrics_json,
                 created_at, completed_at)
            VALUES
                (@goalId, @desc, @goalJson, @phase, @iteration,
                 @reviewRetries, @testRetries, @maxRetries,
                 @activeTaskId, @coderBranch, @phaseOutputs, @metricsJson,
                 @createdAt, @completedAt)
            """;
        cmd.Parameters.AddWithValue("@goalId", pipeline.GoalId);
        cmd.Parameters.AddWithValue("@desc", pipeline.Description);
        cmd.Parameters.AddWithValue("@goalJson", JsonSerializer.Serialize(pipeline.Goal, JsonOptions));
        cmd.Parameters.AddWithValue("@phase", pipeline.Phase.ToString());
        cmd.Parameters.AddWithValue("@iteration", pipeline.Iteration);
        cmd.Parameters.AddWithValue("@reviewRetries", pipeline.ReviewRetries);
        cmd.Parameters.AddWithValue("@testRetries", pipeline.TestRetries);
        cmd.Parameters.AddWithValue("@maxRetries", pipeline.MaxRetries);
        cmd.Parameters.AddWithValue("@activeTaskId", (object?)pipeline.ActiveTaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@coderBranch", (object?)pipeline.CoderBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@phaseOutputs", JsonSerializer.Serialize(pipeline.PhaseOutputs, JsonOptions));
        cmd.Parameters.AddWithValue("@metricsJson", JsonSerializer.Serialize(pipeline.Metrics, JsonOptions));
        cmd.Parameters.AddWithValue("@createdAt", pipeline.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@completedAt",
            pipeline.CompletedAt.HasValue ? pipeline.CompletedAt.Value.ToString("O") : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void SaveConversationCore(GoalPipeline pipeline, SqliteTransaction tx)
    {
        Execute("DELETE FROM conversation_entries WHERE goal_id = @id", tx, ("@id", pipeline.GoalId));

        for (var i = 0; i < pipeline.Conversation.Count; i++)
        {
            var entry = pipeline.Conversation[i];
            using var ins = _db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO conversation_entries (goal_id, seq, role, content) VALUES (@goalId, @seq, @role, @content)";
            ins.Parameters.AddWithValue("@goalId", pipeline.GoalId);
            ins.Parameters.AddWithValue("@seq", i);
            ins.Parameters.AddWithValue("@role", entry.Role);
            ins.Parameters.AddWithValue("@content", entry.Content);
            ins.ExecuteNonQuery();
        }
    }

    private List<ConversationEntry> LoadConversationCore(string goalId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT role, content FROM conversation_entries WHERE goal_id = @goalId ORDER BY seq";
        cmd.Parameters.AddWithValue("@goalId", goalId);

        var entries = new List<ConversationEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            entries.Add(new ConversationEntry(reader.GetString(0), reader.GetString(1)));
        return entries;
    }

    private List<(string TaskId, string GoalId)> LoadTaskMappingsCore(string goalId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT task_id, goal_id FROM task_mappings WHERE goal_id = @goalId";
        cmd.Parameters.AddWithValue("@goalId", goalId);

        var mappings = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            mappings.Add((reader.GetString(0), reader.GetString(1)));
        return mappings;
    }

    private void Execute(string sql, SqliteTransaction tx, params (string Name, object Value)[] parameters)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Closes and disposes the underlying SQLite connection.</summary>
    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
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
    public int MaxRetries { get; init; }
    /// <summary>Task ID currently assigned to a worker, or <c>null</c> when idle.</summary>
    public string? ActiveTaskId { get; init; }
    /// <summary>Feature branch created by the coder, or <c>null</c> if coding has not started.</summary>
    public string? CoderBranch { get; init; }
    /// <summary>Brain-determined iteration plan, or <c>null</c> if not yet planned.</summary>
    public IterationPlan? Plan { get; init; }
    /// <summary>Accumulated output from each completed phase.</summary>
    public Dictionary<string, string> PhaseOutputs { get; init; } = [];
    /// <summary>Metrics captured during this iteration.</summary>
    public IterationMetrics Metrics { get; init; } = new() { Iteration = 1 };
    /// <summary>UTC timestamp when the pipeline was created.</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>UTC timestamp when the pipeline completed, or <c>null</c> if still active.</summary>
    public DateTime? CompletedAt { get; init; }
    /// <summary>Conversation history for the Brain session associated with this pipeline.</summary>
    public List<ConversationEntry> Conversation { get; init; } = [];
    /// <summary>List of (TaskId, GoalId) pairs for task-to-goal resolution.</summary>
    public List<(string TaskId, string GoalId)> TaskMappings { get; set; } = [];
}
