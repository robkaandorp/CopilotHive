using System.Globalization;
using System.Text.Json;
using CopilotHive.Configuration;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
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
    private readonly PipelineStore? _pipelineStore;
    private readonly Lock _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <inheritdoc />
    public string Name => "sqlite";

    /// <summary>Creates a new <see cref="SqliteGoalStore"/> at the given database path.</summary>
    /// <param name="dbPath">Full path to the SQLite database file.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="pipelineStore">Optional <see cref="PipelineStore"/> used to retrieve pipeline conversations.</param>
    public SqliteGoalStore(string dbPath, ILogger<SqliteGoalStore> logger, PipelineStore? pipelineStore = null)
    {
        _logger = logger;
        _pipelineStore = pipelineStore;
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitSchema();
        _logger.LogInformation("SqliteGoalStore initialised at {DbPath}", dbPath);
    }

    /// <summary>Creates a store using an already-open connection (for testing).</summary>
    internal SqliteGoalStore(SqliteConnection connection, ILogger<SqliteGoalStore> logger, PipelineStore? pipelineStore = null)
    {
        _logger = logger;
        _pipelineStore = pipelineStore;
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
                scope                 TEXT NOT NULL DEFAULT 'patch',
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
                phase_outputs_json TEXT,
                clarifications_json TEXT,
                build_success  INTEGER NOT NULL DEFAULT 0,
                created_at     TEXT NOT NULL,
                UNIQUE(goal_id, iteration)
            );

            CREATE TABLE IF NOT EXISTS releases (
                id              TEXT PRIMARY KEY,
                tag             TEXT NOT NULL,
                status          TEXT NOT NULL DEFAULT 'planning',
                notes           TEXT,
                created_at      TEXT NOT NULL,
                released_at     TEXT,
                repositories    TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_goals_status ON goals(status);
            CREATE INDEX IF NOT EXISTS idx_goal_iterations_goal ON goal_iterations(goal_id);
            """;
        cmd.ExecuteNonQuery();

        MigrateSchema();
    }

    /// <summary>
    /// Inspects existing table columns and adds any missing columns introduced
    /// after the initial schema. This handles the case where <c>CREATE TABLE IF NOT EXISTS</c>
    /// skips creation because the table already exists from an older version.
    /// </summary>
    private void MigrateSchema()
    {
        var existingColumns = GetTableColumns("goals");

        if (!existingColumns.Contains("depends_on"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE goals ADD COLUMN depends_on TEXT";
            alter.ExecuteNonQuery();
            _logger.LogInformation("Migrated goals table: added 'depends_on' column");
        }

        if (!existingColumns.Contains("merge_commit_hash"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE goals ADD COLUMN merge_commit_hash TEXT";
            alter.ExecuteNonQuery();
            _logger.LogInformation("Migrated goals table: added 'merge_commit_hash' column");
        }

        if (!existingColumns.Contains("scope"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE goals ADD COLUMN scope TEXT NOT NULL DEFAULT 'patch'";
            alter.ExecuteNonQuery();
            _logger.LogInformation("Migrated goals table: added 'scope' column");
        }

        if (!existingColumns.Contains("release_id"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE goals ADD COLUMN release_id TEXT";
            alter.ExecuteNonQuery();
            _logger.LogInformation("Migrated goals table: added 'release_id' column");
        }

        var iterationColumns = GetTableColumns("goal_iterations");

        if (!iterationColumns.Contains("phase_outputs_json"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE goal_iterations ADD COLUMN phase_outputs_json TEXT";
            alter.ExecuteNonQuery();
            _logger.LogInformation("Migrated goal_iterations table: added 'phase_outputs_json' column");
        }

        if (!iterationColumns.Contains("clarifications_json"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE goal_iterations ADD COLUMN clarifications_json TEXT";
            alter.ExecuteNonQuery();
            _logger.LogInformation("Migrated goal_iterations table: added 'clarifications_json' column");
        }

        if (!iterationColumns.Contains("build_success"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE goal_iterations ADD COLUMN build_success INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
            _logger.LogInformation("Migrated goal_iterations table: added 'build_success' column");
        }
    }

    /// <summary>
    /// Returns the set of column names for the specified table using <c>PRAGMA table_info</c>.
    /// </summary>
    /// <param name="tableName">Name of the SQLite table to inspect.</param>
    /// <returns>A set of column names present in the table.</returns>
    private HashSet<string> GetTableColumns(string tableName)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            columns.Add(reader.GetString(1)); // column name is at index 1
        return columns;
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
                    if (metadata.MergeCommitHash is not null)
                    {
                        cmd.CommandText += ", merge_commit_hash = @mergeCommitHash";
                        cmd.Parameters.AddWithValue("@mergeCommitHash", metadata.MergeCommitHash);
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
                INSERT INTO goals (id, description, title, status, priority, scope, repositories, metadata, created_at, started_at, completed_at, iterations, failure_reason, notes, phase_durations, total_duration_seconds, source_conversation_id, depends_on, merge_commit_hash, release_id)
                VALUES (@id, @desc, @title, @status, @priority, @scope, @repos, @meta, @createdAt, @startedAt, @completedAt, @iterations, @failureReason, @notes, @phaseDurations, @totalDuration, @sourceConvId, @dependsOn, @mergeCommitHash, @releaseId)
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
                    scope = @scope,
                    repositories = @repos, metadata = @meta,
                    started_at = @startedAt, completed_at = @completedAt,
                    iterations = @iterations, failure_reason = @failureReason,
                    notes = @notes, phase_durations = @phaseDurations,
                    total_duration_seconds = @totalDuration, source_conversation_id = @sourceConvId,
                    depends_on = @dependsOn, merge_commit_hash = @mergeCommitHash,
                    release_id = @releaseId
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
            {
                _logger.LogInformation("Deleted goal {GoalId}", goalId);

                // Clean up orphaned pipeline/conversation data
                _pipelineStore?.RemovePipeline(goalId);
            }

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
                        INSERT INTO goals (id, description, title, status, priority, scope, repositories, metadata, created_at, started_at, completed_at, iterations, failure_reason, notes, phase_durations, total_duration_seconds, source_conversation_id, depends_on, merge_commit_hash, release_id)
                        VALUES (@id, @desc, @title, @status, @priority, @scope, @repos, @meta, @createdAt, @startedAt, @completedAt, @iterations, @failureReason, @notes, @phaseDurations, @totalDuration, @sourceConvId, @dependsOn, @mergeCommitHash, @releaseId)
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

    // ── Release CRUD ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO releases (id, tag, status, notes, created_at, released_at, repositories)
                VALUES (@id, @tag, @status, @notes, @createdAt, @releasedAt, @repos)
                """;
            BindReleaseParams(cmd, release);
            cmd.ExecuteNonQuery();
        }

        _logger.LogInformation("Created release {ReleaseId}: {Tag}", release.Id, release.Tag);
        return Task.FromResult(release);
    }

    /// <inheritdoc />
    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM releases WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", releaseId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<Release?>(null);

            return Task.FromResult<Release?>(ReadReleaseFromRow(reader));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM releases ORDER BY created_at DESC";

            var releases = new List<Release>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                releases.Add(ReadReleaseFromRow(reader));

            return Task.FromResult<IReadOnlyList<Release>>(releases);
        }
    }

    /// <inheritdoc />
    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE releases SET
                    tag = @tag, status = @status, notes = @notes,
                    released_at = @releasedAt, repositories = @repos
                WHERE id = @id
                """;
            BindReleaseParams(cmd, release);

            var rows = cmd.ExecuteNonQuery();
            if (rows == 0)
                throw new KeyNotFoundException($"Release '{release.Id}' not found in SQLite store.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Fetch current status to enforce Planning-only constraint
            using var fetchCmd = _db.CreateCommand();
            fetchCmd.CommandText = "SELECT status FROM releases WHERE id = @id";
            fetchCmd.Parameters.AddWithValue("@id", releaseId);
            var statusRaw = fetchCmd.ExecuteScalar();

            if (statusRaw is null)
                throw new KeyNotFoundException($"Release '{releaseId}' not found in SQLite store.");

            var currentStatus = Enum.Parse<ReleaseStatus>((string)statusRaw, ignoreCase: true);
            if (currentStatus != ReleaseStatus.Planning)
                throw new InvalidOperationException(
                    $"Release '{releaseId}' cannot be edited because it is in '{currentStatus}' status. Only Planning releases can be edited.");

            // Build a partial UPDATE touching only the supplied fields
            var setClauses = new List<string>();
            using var cmd = _db.CreateCommand();

            if (update.Tag is not null)
            {
                setClauses.Add("tag = @tag");
                cmd.Parameters.AddWithValue("@tag", update.Tag);
            }

            if (update.Notes is not null)
            {
                setClauses.Add("notes = @notes");
                cmd.Parameters.AddWithValue("@notes", update.Notes);
            }

            if (update.Repositories is not null)
            {
                setClauses.Add("repositories = @repos");
                cmd.Parameters.AddWithValue("@repos", update.Repositories.Count > 0
                    ? JsonSerializer.Serialize(update.Repositories, JsonOptions)
                    : (object)DBNull.Value);
            }

            if (setClauses.Count == 0)
                return Task.CompletedTask; // nothing to update

            cmd.CommandText = $"UPDATE releases SET {string.Join(", ", setClauses)} WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", releaseId);
            cmd.ExecuteNonQuery();

            _logger.LogInformation("Updated release {ReleaseId} (fields: {Fields})",
                releaseId, string.Join(", ", setClauses.Select(c => c.Split('=')[0].Trim())));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Enforce Planning-only delete: fetch the release first and reject if already Released.
            using var fetchCmd = _db.CreateCommand();
            fetchCmd.CommandText = "SELECT status FROM releases WHERE id = @id";
            fetchCmd.Parameters.AddWithValue("@id", releaseId);
            var statusRaw = fetchCmd.ExecuteScalar();

            if (statusRaw is null)
                return Task.FromResult(false); // not found

            var status = Enum.Parse<ReleaseStatus>((string)statusRaw, ignoreCase: true);
            if (status != ReleaseStatus.Planning)
                return Task.FromResult(false); // only Planning releases may be deleted

            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM releases WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", releaseId);
            var rows = cmd.ExecuteNonQuery();

            if (rows > 0)
                _logger.LogInformation("Deleted release {ReleaseId}", releaseId);

            return Task.FromResult(rows > 0);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var goals = ReadGoalsCore(
                "SELECT * FROM goals WHERE release_id = @releaseId ORDER BY created_at DESC",
                ("@releaseId", releaseId));
            return Task.FromResult<IReadOnlyList<Goal>>(goals);
        }
    }

    // ── Pipeline conversation ────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default)
    {
        if (_pipelineStore is null)
            return Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        var conversation = _pipelineStore.GetConversation(goalId);
        return Task.FromResult<IReadOnlyList<ConversationEntry>>(conversation);
    }

    // ── All Clarifications ────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var sql = """
                SELECT goal_id, clarifications_json
                FROM goal_iterations
                WHERE clarifications_json IS NOT NULL AND clarifications_json != '[]'
                ORDER BY id DESC
                """;

            if (limit.HasValue)
                sql += $" LIMIT {limit.Value * 10}"; // fetch extra rows to account for multi-clarification rows

            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;

            var results = new List<(string GoalId, PersistedClarification Clarification)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var goalId = reader.GetString(0);
                var json = reader.GetString(1);
                if (string.IsNullOrWhiteSpace(json)) continue;
                var clarifications = JsonSerializer.Deserialize<List<PersistedClarification>>(json, JsonOptions);
                if (clarifications is null) continue;

                foreach (var clarification in clarifications)
                    results.Add((goalId, clarification));
            }

            // Sort by timestamp descending and apply limit
            results.Sort((a, b) => b.Clarification.Timestamp.CompareTo(a.Clarification.Timestamp));
            if (limit.HasValue && results.Count > limit.Value)
                results = results[..limit.Value];

            return Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(results);
        }
    }

    // ── Iteration reset ──────────────────────────────────────────────────

    /// <inheritdoc />
    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            try
            {
                // Clear iteration data on the goals row
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE goals SET
                        failure_reason        = NULL,
                        iterations            = 0,
                        phase_durations       = NULL,
                        total_duration_seconds = NULL,
                        started_at            = NULL,
                        completed_at          = NULL
                    WHERE id = @id
                    """;
                cmd.Parameters.AddWithValue("@id", goalId);
                var rows = cmd.ExecuteNonQuery();
                if (rows == 0)
                    throw new KeyNotFoundException($"Goal '{goalId}' not found in SQLite store.");

                // Delete all iteration summaries (phase outputs included via CASCADE / DELETE)
                using var deleteIter = _db.CreateCommand();
                deleteIter.Transaction = tx;
                deleteIter.CommandText = "DELETE FROM goal_iterations WHERE goal_id = @id";
                deleteIter.Parameters.AddWithValue("@id", goalId);
                deleteIter.ExecuteNonQuery();

                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
        }

        _logger.LogInformation("Reset iteration data for goal {GoalId}", goalId);
        return Task.CompletedTask;
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
        // scope may be absent in databases created before migration
        GoalScope goalScope = GoalScope.Patch;
        if (TryGetOrdinal(reader, "scope", out var scopeOrd) && !reader.IsDBNull(scopeOrd))
            Enum.TryParse<GoalScope>(reader.GetString(scopeOrd), ignoreCase: true, out goalScope);

        var goal = new Goal
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Description = reader.GetString(reader.GetOrdinal("description")),
            Status = Enum.Parse<GoalStatus>(reader.GetString(reader.GetOrdinal("status")), ignoreCase: true),
            Priority = Enum.Parse<GoalPriority>(reader.GetString(reader.GetOrdinal("priority")), ignoreCase: true),
            Scope = goalScope,
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
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
            goal.StartedAt = DateTime.Parse(reader.GetString(startedOrd), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var completedOrd = reader.GetOrdinal("completed_at");
        if (!reader.IsDBNull(completedOrd))
            goal.CompletedAt = DateTime.Parse(reader.GetString(completedOrd), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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

        // depends_on may be absent in databases created before migration
        if (TryGetOrdinal(reader, "depends_on", out var dependsOnOrd) && !reader.IsDBNull(dependsOnOrd))
        {
            var deps = JsonSerializer.Deserialize<List<string>>(reader.GetString(dependsOnOrd), JsonOptions);
            if (deps is not null)
                goal.DependsOn.AddRange(deps);
        }

        // merge_commit_hash may be absent in databases created before migration
        if (TryGetOrdinal(reader, "merge_commit_hash", out var mergeHashOrd) && !reader.IsDBNull(mergeHashOrd))
            goal.MergeCommitHash = reader.GetString(mergeHashOrd);

        // release_id may be absent in databases created before migration
        if (TryGetOrdinal(reader, "release_id", out var releaseIdOrd) && !reader.IsDBNull(releaseIdOrd))
            goal.ReleaseId = reader.GetString(releaseIdOrd);

        return goal;
    }

    private static void BindGoalParams(SqliteCommand cmd, Goal goal)
    {
        cmd.Parameters.AddWithValue("@id", goal.Id);
        cmd.Parameters.AddWithValue("@desc", goal.Description);
        cmd.Parameters.AddWithValue("@title", (object?)null ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", goal.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@priority", goal.Priority.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@scope", goal.Scope.ToString().ToLowerInvariant());
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
        cmd.Parameters.AddWithValue("@mergeCommitHash", (object?)goal.MergeCommitHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@releaseId", (object?)goal.ReleaseId ?? DBNull.Value);
    }

    private void InsertIterationCore(string goalId, IterationSummary summary, SqliteTransaction? transaction)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO goal_iterations (goal_id, iteration, phases_json, test_total, test_passed, test_failed, review_verdict, notes_json, phase_outputs_json, clarifications_json, build_success, created_at)
            VALUES (@goalId, @iteration, @phases, @testTotal, @testPassed, @testFailed, @reviewVerdict, @notes, @phaseOutputs, @clarifications, @buildSuccess, @createdAt)
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
        cmd.Parameters.AddWithValue("@phaseOutputs", summary.PhaseOutputs.Count > 0
            ? JsonSerializer.Serialize(summary.PhaseOutputs, JsonOptions) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@clarifications", summary.Clarifications.Count > 0
            ? JsonSerializer.Serialize(summary.Clarifications, JsonOptions) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@buildSuccess", summary.BuildSuccess ? 1 : 0);
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

            // phase_outputs_json may be absent in databases created before migration
            Dictionary<string, string> phaseOutputs = [];
            if (TryGetOrdinal(reader, "phase_outputs_json", out var poOrd) && !reader.IsDBNull(poOrd))
            {
                phaseOutputs = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    reader.GetString(poOrd), JsonOptions) ?? [];
            }

            // clarifications_json may be absent in databases created before migration
            List<PersistedClarification> clarifications = [];
            if (TryGetOrdinal(reader, "clarifications_json", out var clOrd) && !reader.IsDBNull(clOrd))
            {
                clarifications = JsonSerializer.Deserialize<List<PersistedClarification>>(
                    reader.GetString(clOrd), JsonOptions) ?? [];
            }

            // build_success may be absent in databases created before migration
            var buildSuccess = TryGetOrdinal(reader, "build_success", out var bsOrd) && !reader.IsDBNull(bsOrd)
                ? reader.GetInt32(bsOrd) == 1
                : false;

            summaries.Add(new IterationSummary
            {
                Iteration = reader.GetInt32(reader.GetOrdinal("iteration")),
                Phases = phases,
                TestCounts = testCounts,
                BuildSuccess = buildSuccess,
                ReviewVerdict = reader.IsDBNull(rvOrd) ? null : reader.GetString(rvOrd),
                Notes = notes,
                PhaseOutputs = phaseOutputs,
                Clarifications = clarifications,
            });
        }

        return summaries;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>
    /// Attempts to get the ordinal for a column by name without throwing if the column is absent.
    /// Used for backward compatibility when reading from databases that predate schema migrations.
    /// </summary>
    /// <param name="reader">The data reader to inspect.</param>
    /// <param name="columnName">Name of the column to look up.</param>
    /// <param name="ordinal">The ordinal of the column, if found.</param>
    /// <returns><c>true</c> if the column was found; <c>false</c> otherwise.</returns>
    private static bool TryGetOrdinal(SqliteDataReader reader, string columnName, out int ordinal)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
            {
                ordinal = i;
                return true;
            }
        }
        ordinal = -1;
        return false;
    }

    private static Release ReadReleaseFromRow(SqliteDataReader reader)
    {
        var release = new Release
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Tag = reader.GetString(reader.GetOrdinal("tag")),
            Status = Enum.Parse<ReleaseStatus>(reader.GetString(reader.GetOrdinal("status")), ignoreCase: true),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };

        var notesOrd = reader.GetOrdinal("notes");
        if (!reader.IsDBNull(notesOrd))
            release.Notes = reader.GetString(notesOrd);

        var releasedOrd = reader.GetOrdinal("released_at");
        if (!reader.IsDBNull(releasedOrd))
            release.ReleasedAt = DateTime.Parse(reader.GetString(releasedOrd), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var reposOrd = reader.GetOrdinal("repositories");
        if (!reader.IsDBNull(reposOrd))
        {
            var repos = JsonSerializer.Deserialize<List<string>>(reader.GetString(reposOrd), JsonOptions);
            if (repos is not null)
                release.RepositoryNames.AddRange(repos);
        }

        return release;
    }

    private static void BindReleaseParams(SqliteCommand cmd, Release release)
    {
        cmd.Parameters.AddWithValue("@id", release.Id);
        cmd.Parameters.AddWithValue("@tag", release.Tag);
        cmd.Parameters.AddWithValue("@status", release.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@notes", (object?)release.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", release.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@releasedAt", release.ReleasedAt.HasValue ? release.ReleasedAt.Value.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@repos", release.RepositoryNames.Count > 0
            ? JsonSerializer.Serialize(release.RepositoryNames, JsonOptions) : (object)DBNull.Value);
    }
}
