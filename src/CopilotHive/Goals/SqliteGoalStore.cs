using System.Text.Json;
using CopilotHive.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Goals;

/// <summary>
/// SQLite-backed implementation of <see cref="IGoalStore"/>.
/// Primary source of truth for goal state, iteration history, and search.
/// </summary>
public sealed class SqliteGoalStore : IGoalStore
{
    private readonly SqliteConnection _db;
    private readonly ILogger<SqliteGoalStore> _logger;
    private readonly Lock _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <inheritdoc />
    public string Name => "sqlite";

    /// <summary>Creates a new <see cref="SqliteGoalStore"/> at the given database path.</summary>
    public SqliteGoalStore(string dbPath, ILogger<SqliteGoalStore> logger)
    {
        _logger = logger;
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitSchema();
        _logger.LogInformation("SqliteGoalStore initialised at {DbPath}", dbPath);
    }

    /// <summary>Creates a store using an already-open connection (for testing).</summary>
    internal SqliteGoalStore(SqliteConnection connection, ILogger<SqliteGoalStore> logger)
    {
        _logger = logger;
        _db = connection;
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS goals (
                id                    TEXT PRIMARY KEY,
                description           TEXT NOT NULL,
                title                 TEXT,
                status                TEXT NOT NULL DEFAULT 'pending',
                priority              TEXT NOT NULL DEFAULT 'normal',
                repositories          TEXT,
                metadata              TEXT,
                created_at            TEXT NOT NULL,
                started_at            TEXT,
                completed_at          TEXT,
                iterations            INTEGER,
                failure_reason        TEXT,
                notes                 TEXT,
                phase_durations       TEXT,
                total_duration_seconds REAL,
                source_conversation_id TEXT,
                depends_on             TEXT
            );

            CREATE TABLE IF NOT EXISTS goal_iterations (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                goal_id        TEXT NOT NULL REFERENCES goals(id) ON DELETE CASCADE,
                iteration      INTEGER NOT NULL,
                phases_json    TEXT,
                test_total     INTEGER,
                test_passed    INTEGER,
                test_failed    INTEGER,
                review_verdict TEXT,
                notes_json     TEXT,
                created_at     TEXT NOT NULL,
                UNIQUE(goal_id, iteration)
            );

            CREATE INDEX IF NOT EXISTS idx_goals_status ON goals(status);
            CREATE INDEX IF NOT EXISTS idx_goal_iterations_goal ON goal_iterations(goal_id);
            """;
        cmd.ExecuteNonQuery();
    }

    // ── IGoalSource ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
        => GetGoalsByStatusAsync(GoalStatus.Pending, ct);

    /// <inheritdoc />
    public async Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE goals SET status = @status";
                cmd.Parameters.AddWithValue("@status", status.ToString().ToLowerInvariant());

                var sets = new List<string>();

                if (metadata is not null)
                {
                    if (metadata.StartedAt.HasValue)
                    {
                        cmd.CommandText += ", started_at = @startedAt";
                        cmd.Parameters.AddWithValue("@startedAt", metadata.StartedAt.Value.ToString("O"));
                    }
                    if (metadata.CompletedAt.HasValue)
                    {
                        cmd.CommandText += ", completed_at = @completedAt";
                        cmd.Parameters.AddWithValue("@completedAt", metadata.CompletedAt.Value.ToString("O"));
                    }
                    if (metadata.Iterations.HasValue)
                    {
                        cmd.CommandText += ", iterations = @iterations";
                        cmd.Parameters.AddWithValue("@iterations", metadata.Iterations.Value);
                    }
                    if (metadata.FailureReason is not null)
                    {
                        cmd.CommandText += ", failure_reason = @failureReason";
                        cmd.Parameters.AddWithValue("@failureReason", metadata.FailureReason);
                    }
                    if (metadata.Notes is { Count: > 0 })
                    {
                        // Append notes to existing
                        cmd.CommandText += ", notes = CASE WHEN notes IS NULL THEN @newNotes ELSE notes || ',' || @newNotes END";
                        cmd.Parameters.AddWithValue("@newNotes", JsonSerializer.Serialize(metadata.Notes, JsonOptions));
                    }
                    if (metadata.PhaseDurations is { Count: > 0 })
                    {
                        cmd.CommandText += ", phase_durations = @phaseDurations";
                        cmd.Parameters.AddWithValue("@phaseDurations", JsonSerializer.Serialize(metadata.PhaseDurations, JsonOptions));
                    }
                    if (metadata.TotalDurationSeconds.HasValue)
                    {
                        cmd.CommandText += ", total_duration_seconds = @totalDuration";
                        cmd.Parameters.AddWithValue("@totalDuration", metadata.TotalDurationSeconds.Value);
                    }
                }

                cmd.CommandText += " WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", goalId);

                var rows = cmd.ExecuteNonQuery();
                if (rows == 0)
                    throw new KeyNotFoundException($"Goal '{goalId}' not found in SQLite store.");

                // Append iteration summary if provided
                if (metadata?.IterationSummary is { } summary)
                    InsertIterationCore(goalId, summary, tx);

                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
        }

        await Task.CompletedTask;
    }

    // ── IGoalStore CRUD ──────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var goals = ReadGoalsCore("SELECT * FROM goals ORDER BY created_at DESC");
            return Task.FromResult<IReadOnlyList<Goal>>(goals);
        }
    }

    /// <inheritdoc />
    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM goals WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", goalId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<Goal?>(null);

            var goal = ReadGoalFromRow(reader);
            goal.IterationSummaries = LoadIterationsCore(goalId);
            return Task.FromResult<Goal?>(goal);
        }
    }

    /// <inheritdoc />
    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        GoalId.Validate(goal.Id);

        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO goals (id, description, title, status, priority, repositories, metadata, created_at, started_at, completed_at, iterations, failure_reason, notes, phase_durations, total_duration_seconds, source_conversation_id, depends_on)
                VALUES (@id, @desc, @title, @status, @priority, @repos, @meta, @createdAt, @startedAt, @completedAt, @iterations, @failureReason, @notes, @phaseDurations, @totalDuration, @sourceConvId, @dependsOn)
                """;
            BindGoalParams(cmd, goal);
            cmd.ExecuteNonQuery();
        }

        _logger.LogInformation("Created goal {GoalId}: {Description}", goal.Id, Truncate(goal.Description, 80));
        return Task.FromResult(goal);
    }

    /// <inheritdoc />
    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE goals SET
                    description = @desc, title = @title, status = @status, priority = @priority,
                    repositories = @repos, metadata = @meta,
                    started_at = @startedAt, completed_at = @completedAt,
                    iterations = @iterations, failure_reason = @failureReason,
                    notes = @notes, phase_durations = @phaseDurations,
                    total_duration_seconds = @totalDuration, source_conversation_id = @sourceConvId,
                    depends_on = @dependsOn
                WHERE id = @id
                """;
            BindGoalParams(cmd, goal);

            var rows = cmd.ExecuteNonQuery();
            if (rows == 0)
                throw new KeyNotFoundException($"Goal '{goal.Id}' not found in SQLite store.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM goals WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", goalId);
            var rows = cmd.ExecuteNonQuery();

            if (rows > 0)
                _logger.LogInformation("Deleted goal {GoalId}", goalId);

            return Task.FromResult(rows > 0);
        }
    }

    // ── Search ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var sql = "SELECT * FROM goals WHERE (id LIKE @q OR description LIKE @q OR failure_reason LIKE @q)";
            if (statusFilter.HasValue)
                sql += " AND status = @status";
            sql += " ORDER BY created_at DESC";

            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@q", $"%{query}%");
            if (statusFilter.HasValue)
                cmd.Parameters.AddWithValue("@status", statusFilter.Value.ToString().ToLowerInvariant());

            var goals = new List<Goal>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                goals.Add(ReadGoalFromRow(reader));

            return Task.FromResult<IReadOnlyList<Goal>>(goals);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var goals = ReadGoalsCore(
                "SELECT * FROM goals WHERE status = @status ORDER BY created_at DESC",
                ("@status", status.ToString().ToLowerInvariant()));
            return Task.FromResult<IReadOnlyList<Goal>>(goals);
        }
    }

    // ── Iterations ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default)
    {
        lock (_lock)
        {
            InsertIterationCore(goalId, summary, transaction: null);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var summaries = LoadIterationsCore(goalId);
            return Task.FromResult<IReadOnlyList<IterationSummary>>(summaries);
        }
    }

    // ── Import ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default)
    {
        var imported = 0;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            try
            {
                foreach (var goal in goals)
                {
                    using var check = _db.CreateCommand();
                    check.Transaction = tx;
                    check.CommandText = "SELECT COUNT(1) FROM goals WHERE id = @id";
                    check.Parameters.AddWithValue("@id", goal.Id);
                    var exists = Convert.ToInt64(check.ExecuteScalar()) > 0;
                    if (exists) continue;

                    using var cmd = _db.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO goals (id, description, title, status, priority, repositories, metadata, created_at, started_at, completed_at, iterations, failure_reason, notes, phase_durations, total_duration_seconds, source_conversation_id, depends_on)
                        VALUES (@id, @desc, @title, @status, @priority, @repos, @meta, @createdAt, @startedAt, @completedAt, @iterations, @failureReason, @notes, @phaseDurations, @totalDuration, @sourceConvId, @dependsOn)
                        """;
                    BindGoalParams(cmd, goal);
                    cmd.ExecuteNonQuery();

                    foreach (var summary in goal.IterationSummaries)
                        InsertIterationCore(goal.Id, summary, tx);

                    imported++;
                }
                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
        }

        if (imported > 0)
            _logger.LogInformation("Imported {Count} goals into SQLite store", imported);

        return Task.FromResult(imported);
    }

    // ── Internals ────────────────────────────────────────────────────────

    private List<Goal> ReadGoalsCore(string sql, params (string name, object value)[] parameters)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        var goals = new List<Goal>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            goals.Add(ReadGoalFromRow(reader));

        return goals;
    }

    private static Goal ReadGoalFromRow(SqliteDataReader reader)
    {
        var goal = new Goal
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Description = reader.GetString(reader.GetOrdinal("description")),
            Status = Enum.Parse<GoalStatus>(reader.GetString(reader.GetOrdinal("status")), ignoreCase: true),
            Priority = Enum.Parse<GoalPriority>(reader.GetString(reader.GetOrdinal("priority")), ignoreCase: true),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        };

        var reposOrd = reader.GetOrdinal("repositories");
        if (!reader.IsDBNull(reposOrd))
        {
            var repos = JsonSerializer.Deserialize<List<string>>(reader.GetString(reposOrd), JsonOptions);
            if (repos is not null)
                goal.RepositoryNames.AddRange(repos);
        }

        var metaOrd = reader.GetOrdinal("metadata");
        if (!reader.IsDBNull(metaOrd))
        {
            var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(metaOrd), JsonOptions);
            if (meta is not null)
                foreach (var kvp in meta) goal.Metadata[kvp.Key] = kvp.Value;
        }

        var startedOrd = reader.GetOrdinal("started_at");
        if (!reader.IsDBNull(startedOrd))
            goal.StartedAt = DateTime.Parse(reader.GetString(startedOrd));

        var completedOrd = reader.GetOrdinal("completed_at");
        if (!reader.IsDBNull(completedOrd))
            goal.CompletedAt = DateTime.Parse(reader.GetString(completedOrd));

        var iterOrd = reader.GetOrdinal("iterations");
        if (!reader.IsDBNull(iterOrd))
            goal.Iterations = reader.GetInt32(iterOrd);

        var failOrd = reader.GetOrdinal("failure_reason");
        if (!reader.IsDBNull(failOrd))
            goal.FailureReason = reader.GetString(failOrd);

        var notesOrd = reader.GetOrdinal("notes");
        if (!reader.IsDBNull(notesOrd))
        {
            var raw = reader.GetString(notesOrd);
            var notes = JsonSerializer.Deserialize<List<string>>(raw, JsonOptions);
            if (notes is not null) goal.Notes = notes;
        }

        var phaseDurOrd = reader.GetOrdinal("phase_durations");
        if (!reader.IsDBNull(phaseDurOrd))
            goal.PhaseDurations = JsonSerializer.Deserialize<Dictionary<string, double>>(reader.GetString(phaseDurOrd), JsonOptions);

        var totalDurOrd = reader.GetOrdinal("total_duration_seconds");
        if (!reader.IsDBNull(totalDurOrd))
            goal.TotalDurationSeconds = reader.GetDouble(totalDurOrd);

        var dependsOnOrd = reader.GetOrdinal("depends_on");
        if (!reader.IsDBNull(dependsOnOrd))
        {
            var deps = JsonSerializer.Deserialize<List<string>>(reader.GetString(dependsOnOrd), JsonOptions);
            if (deps is not null)
                goal.DependsOn.AddRange(deps);
        }

        return goal;
    }

    private static void BindGoalParams(SqliteCommand cmd, Goal goal)
    {
        cmd.Parameters.AddWithValue("@id", goal.Id);
        cmd.Parameters.AddWithValue("@desc", goal.Description);
        cmd.Parameters.AddWithValue("@title", (object?)null ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", goal.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@priority", goal.Priority.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@repos", goal.RepositoryNames.Count > 0
            ? JsonSerializer.Serialize(goal.RepositoryNames, JsonOptions) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@meta", goal.Metadata.Count > 0
            ? JsonSerializer.Serialize(goal.Metadata, JsonOptions) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", goal.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@startedAt", goal.StartedAt.HasValue ? goal.StartedAt.Value.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@completedAt", goal.CompletedAt.HasValue ? goal.CompletedAt.Value.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@iterations", goal.Iterations.HasValue ? goal.Iterations.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@failureReason", (object?)goal.FailureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@notes", goal.Notes.Count > 0
            ? JsonSerializer.Serialize(goal.Notes, JsonOptions) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@phaseDurations", goal.PhaseDurations is { Count: > 0 }
            ? JsonSerializer.Serialize(goal.PhaseDurations, JsonOptions) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@totalDuration", goal.TotalDurationSeconds.HasValue ? goal.TotalDurationSeconds.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceConvId", (object?)null ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dependsOn", goal.DependsOn.Count > 0
            ? JsonSerializer.Serialize(goal.DependsOn, JsonOptions) : (object)DBNull.Value);
    }

    private void InsertIterationCore(string goalId, IterationSummary summary, SqliteTransaction? transaction)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO goal_iterations (goal_id, iteration, phases_json, test_total, test_passed, test_failed, review_verdict, notes_json, created_at)
            VALUES (@goalId, @iteration, @phases, @testTotal, @testPassed, @testFailed, @reviewVerdict, @notes, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@goalId", goalId);
        cmd.Parameters.AddWithValue("@iteration", summary.Iteration);
        cmd.Parameters.AddWithValue("@phases", JsonSerializer.Serialize(summary.Phases, JsonOptions));
        cmd.Parameters.AddWithValue("@testTotal", summary.TestCounts?.Total ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@testPassed", summary.TestCounts?.Passed ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@testFailed", summary.TestCounts?.Failed ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@reviewVerdict", (object?)summary.ReviewVerdict ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@notes", summary.Notes.Count > 0
            ? JsonSerializer.Serialize(summary.Notes, JsonOptions) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private List<IterationSummary> LoadIterationsCore(string goalId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM goal_iterations WHERE goal_id = @goalId ORDER BY iteration";
        cmd.Parameters.AddWithValue("@goalId", goalId);

        var summaries = new List<IterationSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var phasesJson = reader.GetString(reader.GetOrdinal("phases_json"));
            var phases = JsonSerializer.Deserialize<List<PhaseResult>>(phasesJson, JsonOptions) ?? [];

            TestCounts? testCounts = null;
            var ttOrd = reader.GetOrdinal("test_total");
            if (!reader.IsDBNull(ttOrd))
            {
                testCounts = new TestCounts
                {
                    Total = reader.GetInt32(ttOrd),
                    Passed = reader.IsDBNull(reader.GetOrdinal("test_passed")) ? 0 : reader.GetInt32(reader.GetOrdinal("test_passed")),
                    Failed = reader.IsDBNull(reader.GetOrdinal("test_failed")) ? 0 : reader.GetInt32(reader.GetOrdinal("test_failed")),
                };
            }

            var notesOrd = reader.GetOrdinal("notes_json");
            var notes = reader.IsDBNull(notesOrd)
                ? []
                : JsonSerializer.Deserialize<List<string>>(reader.GetString(notesOrd), JsonOptions) ?? [];

            var rvOrd = reader.GetOrdinal("review_verdict");
            summaries.Add(new IterationSummary
            {
                Iteration = reader.GetInt32(reader.GetOrdinal("iteration")),
                Phases = phases,
                TestCounts = testCounts,
                ReviewVerdict = reader.IsDBNull(rvOrd) ? null : reader.GetString(rvOrd),
                Notes = notes,
            });
        }

        return summaries;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}
