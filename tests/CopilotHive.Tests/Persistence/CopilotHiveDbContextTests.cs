using System.Data.Common;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Persistence;

/// <summary>
/// Tests for <see cref="CopilotHiveDbContext"/> verifying entity mappings, value converters,
/// enum storage format, JSON round-tripping, and schema compatibility with existing SQL stores.
/// </summary>
public sealed class CopilotHiveDbContextTests
{
    // ── Helper ────────────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves the underlying <see cref="SqliteConnection"/> from a DbContext
    /// so raw SQL can be executed against the same in-memory database.
    /// </summary>
    private static SqliteConnection GetSqliteConnection(CopilotHiveDbContext ctx)
    {
        var dbConn = ctx.Database.GetDbConnection();
        return (SqliteConnection)dbConn;
    }

    // ── 1. Goal round-trip ────────────────────────────────────────────────

    [Fact]
    public void DbContext_CanCreateGoal_AndRetrieveIt()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();

        var created = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var started = new DateTime(2025, 6, 15, 11, 0, 0, DateTimeKind.Utc);
        var completed = new DateTime(2025, 6, 15, 14, 0, 0, DateTimeKind.Utc);

        var goal = new Goal
        {
            Id = "goal-rt-1",
            Description = "Round-trip test goal",
            Status = GoalStatus.InProgress,
            Priority = GoalPriority.High,
            Scope = GoalScope.Feature,
            RepositoryNames = ["repo-alpha", "repo-beta"],
            DependsOn = ["goal-dep-1", "goal-dep-2"],
            Metadata = new() { ["env"] = "test", ["version"] = "2" },
            CreatedAt = created,
            StartedAt = started,
            CompletedAt = completed,
            Iterations = 3,
            FailureReason = "timeout",
            Notes = ["note-1", "note-2"],
            PhaseDurations = new() { ["planning"] = 5.0, ["coding"] = 120.5 },
            TotalDurationSeconds = 9000.5,
            MergeCommitHash = "abc123def456",
            ReleaseId = "release-rt-1",
            Documents = ["doc-1", "doc-2"],
            BranchCleanedUp = true,
        };

        ctx.Goals.Add(goal);
        ctx.SaveChanges();

        // Detach the tracked entity so retrieval hits the database
        ctx.Entry(goal).State = EntityState.Detached;

        var retrieved = ctx.Goals.Find("goal-rt-1");

        Assert.NotNull(retrieved);
        Assert.Equal("goal-rt-1", retrieved!.Id);
        Assert.Equal("Round-trip test goal", retrieved.Description);
        Assert.Equal(GoalStatus.InProgress, retrieved.Status);
        Assert.Equal(GoalPriority.High, retrieved.Priority);
        Assert.Equal(GoalScope.Feature, retrieved.Scope);
        Assert.Equal(["repo-alpha", "repo-beta"], retrieved.RepositoryNames);
        Assert.Equal(["goal-dep-1", "goal-dep-2"], retrieved.DependsOn);
        Assert.Equal("test", retrieved.Metadata["env"]);
        Assert.Equal("2", retrieved.Metadata["version"]);
        Assert.Equal(created, retrieved.CreatedAt);
        Assert.Equal(started, retrieved.StartedAt);
        Assert.Equal(completed, retrieved.CompletedAt);
        Assert.Equal(3, retrieved.Iterations);
        Assert.Equal("timeout", retrieved.FailureReason);
        Assert.Equal(["note-1", "note-2"], retrieved.Notes);
        Assert.NotNull(retrieved.PhaseDurations);
        Assert.Equal(5.0, retrieved.PhaseDurations!["planning"]);
        Assert.Equal(120.5, retrieved.PhaseDurations["coding"]);
        Assert.Equal(9000.5, retrieved.TotalDurationSeconds);
        Assert.Equal("abc123def456", retrieved.MergeCommitHash);
        Assert.Equal("release-rt-1", retrieved.ReleaseId);
        Assert.Equal(["doc-1", "doc-2"], retrieved.Documents);
        Assert.True(retrieved.BranchCleanedUp);
    }

    // ── 2. Release round-trip ─────────────────────────────────────────────

    [Fact]
    public void DbContext_CanCreateRelease_AndRetrieveIt()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();

        var created = new DateTime(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var released = new DateTime(2025, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        var release = new Release
        {
            Id = "rel-rt-1",
            Tag = "v2.0.0",
            Status = ReleaseStatus.Released,
            Notes = "Major release with new features",
            CreatedAt = created,
            ReleasedAt = released,
            RepositoryNames = ["repo-x", "repo-y"],
        };

        ctx.Releases.Add(release);
        ctx.SaveChanges();

        ctx.Entry(release).State = EntityState.Detached;

        var retrieved = ctx.Releases.Find("rel-rt-1");

        Assert.NotNull(retrieved);
        Assert.Equal("rel-rt-1", retrieved!.Id);
        Assert.Equal("v2.0.0", retrieved.Tag);
        Assert.Equal(ReleaseStatus.Released, retrieved.Status);
        Assert.Equal("Major release with new features", retrieved.Notes);
        Assert.Equal(created, retrieved.CreatedAt);
        Assert.Equal(released, retrieved.ReleasedAt);
        Assert.Equal(["repo-x", "repo-y"], retrieved.RepositoryNames);
    }

    // ── 3. IterationSummaryEntity round-trip ──────────────────────────────

    [Fact]
    public void DbContext_CanCreateIterationSummaryEntity_AndRetrieveIt()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();

        ctx.Goals.Add(new Goal
        {
            Id = "goal-iter-1",
            Description = "iteration parent",
            Status = GoalStatus.Pending,
            Priority = GoalPriority.Normal,
            Scope = GoalScope.Patch,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        ctx.SaveChanges();

        var entity = new IterationSummaryEntity
        {
            GoalId = "goal-iter-1",
            Iteration = 2,
            PhasesJson = """[{"name":"coding","result":"pass"}]""",
            TestTotal = 10,
            TestPassed = 8,
            TestFailed = 2,
            ReviewVerdict = "approve",
            NotesJson = """["improver skipped: timeout"]""",
            PhaseOutputsJson = """{"coder-1":"output text"}""",
            ClarificationsJson = """[{"question":"q?","answer":"a"}]""",
            BuildSuccess = true,
            CreatedAt = "2025-06-15T10:30:00.0000000Z",
        };

        ctx.IterationSummaries.Add(entity);
        ctx.SaveChanges();

        ctx.Entry(entity).State = EntityState.Detached;

        var retrieved = ctx.IterationSummaries
            .OrderBy(e => e.Id)
            .First(e => e.GoalId == "goal-iter-1");

        Assert.Equal("goal-iter-1", retrieved.GoalId);
        Assert.Equal(2, retrieved.Iteration);
        Assert.Equal("""[{"name":"coding","result":"pass"}]""", retrieved.PhasesJson);
        Assert.Equal(10, retrieved.TestTotal);
        Assert.Equal(8, retrieved.TestPassed);
        Assert.Equal(2, retrieved.TestFailed);
        Assert.Equal("approve", retrieved.ReviewVerdict);
        Assert.Equal("""["improver skipped: timeout"]""", retrieved.NotesJson);
        Assert.Equal("""{"coder-1":"output text"}""", retrieved.PhaseOutputsJson);
        Assert.Equal("""[{"question":"q?","answer":"a"}]""", retrieved.ClarificationsJson);
        Assert.True(retrieved.BuildSuccess);
        Assert.Equal("2025-06-15T10:30:00.0000000Z", retrieved.CreatedAt);
    }

    // ── 4. PipelineEntity round-trip ──────────────────────────────────────

    [Fact]
    public void DbContext_CanCreatePipelineEntity_AndRetrieveIt()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();

        var entity = new PipelineEntity
        {
            GoalId = "goal-pipe-1",
            Description = "Pipeline for goal-pipe-1",
            GoalJson = """{"id":"goal-pipe-1"}""",
            Phase = "Coding",
            Iteration = 1,
            ReviewRetries = 0,
            TestRetries = 1,
            ImproverRetries = 0,
            MaxRetries = 3,
            MaxIterations = 10,
            ActiveTaskId = "task-abc",
            CoderBranch = "coder/goal-pipe-1",
            PlanJson = """{"steps":["step1"]}""",
            PhaseOutputs = """{"coder-1":"done"}""",
            MetricsJson = """{"total":100}""",
            CreatedAt = "2025-06-15T10:00:00.0000000Z",
            CompletedAt = "2025-06-15T12:00:00.0000000Z",
            GoalStartedAt = "2025-06-15T10:05:00.0000000Z",
            MergeCommitHash = "merge123",
            RoleSessionsJson = """{"coder":"session-1"}""",
            IterationStartSha = "sha-abc",
            PhaseOccurrence = 1,
            PhaseLogJson = """[{"phase":"coding"}]""",
        };

        ctx.Pipelines.Add(entity);
        ctx.SaveChanges();

        ctx.Entry(entity).State = EntityState.Detached;

        var retrieved = ctx.Pipelines.Find("goal-pipe-1");

        Assert.NotNull(retrieved);
        Assert.Equal("goal-pipe-1", retrieved!.GoalId);
        Assert.Equal("Pipeline for goal-pipe-1", retrieved.Description);
        Assert.Equal("""{"id":"goal-pipe-1"}""", retrieved.GoalJson);
        Assert.Equal("Coding", retrieved.Phase);
        Assert.Equal(1, retrieved.Iteration);
        Assert.Equal(0, retrieved.ReviewRetries);
        Assert.Equal(1, retrieved.TestRetries);
        Assert.Equal(0, retrieved.ImproverRetries);
        Assert.Equal(3, retrieved.MaxRetries);
        Assert.Equal(10, retrieved.MaxIterations);
        Assert.Equal("task-abc", retrieved.ActiveTaskId);
        Assert.Equal("coder/goal-pipe-1", retrieved.CoderBranch);
        Assert.Equal("""{"steps":["step1"]}""", retrieved.PlanJson);
        Assert.Equal("""{"coder-1":"done"}""", retrieved.PhaseOutputs);
        Assert.Equal("""{"total":100}""", retrieved.MetricsJson);
        Assert.Equal("2025-06-15T10:00:00.0000000Z", retrieved.CreatedAt);
        Assert.Equal("2025-06-15T12:00:00.0000000Z", retrieved.CompletedAt);
        Assert.Equal("2025-06-15T10:05:00.0000000Z", retrieved.GoalStartedAt);
        Assert.Equal("merge123", retrieved.MergeCommitHash);
        Assert.Equal("""{"coder":"session-1"}""", retrieved.RoleSessionsJson);
        Assert.Equal("sha-abc", retrieved.IterationStartSha);
        Assert.Equal(1, retrieved.PhaseOccurrence);
        Assert.Equal("""[{"phase":"coding"}]""", retrieved.PhaseLogJson);
    }

    // ── 5. ConversationEntry ordering by seq ──────────────────────────────

    [Fact]
    public void DbContext_CanCreateConversationEntry_AndRetrieveIt()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();

        ctx.Pipelines.Add(new PipelineEntity
        {
            GoalId = "goal-conv-1",
            Description = "conversation parent",
            GoalJson = "{}",
            Phase = "Planning",
            Iteration = 1,
            ReviewRetries = 0,
            TestRetries = 0,
            ImproverRetries = 0,
            MaxRetries = 3,
            MaxIterations = 10,
            PhaseOutputs = "{}",
            MetricsJson = "{}",
            CreatedAt = "2025-06-15T10:00:00.0000000Z",
            RoleSessionsJson = "{}",
            PhaseOccurrence = 1,
        });
        ctx.SaveChanges();

        // Insert out of order to verify seq ordering on retrieval
        var entries = new[]
        {
            new ConversationEntryEntity
            {
                GoalId = "goal-conv-1",
                Seq = 3,
                Role = "assistant",
                Content = "third message",
                Iteration = 1,
                Purpose = "response",
            },
            new ConversationEntryEntity
            {
                GoalId = "goal-conv-1",
                Seq = 1,
                Role = "system",
                Content = "first message",
                Iteration = null,
                Purpose = null,
            },
            new ConversationEntryEntity
            {
                GoalId = "goal-conv-1",
                Seq = 2,
                Role = "user",
                Content = "second message",
                Iteration = 1,
                Purpose = "prompt",
            },
        };

        foreach (var entry in entries)
            ctx.ConversationEntries.Add(entry);
        ctx.SaveChanges();

        var retrieved = ctx.ConversationEntries
            .Where(e => e.GoalId == "goal-conv-1")
            .OrderBy(e => e.Seq)
            .ToList();

        Assert.Equal(3, retrieved.Count);
        Assert.Equal(1, retrieved[0].Seq);
        Assert.Equal("first message", retrieved[0].Content);
        Assert.Equal("system", retrieved[0].Role);
        Assert.Null(retrieved[0].Iteration);
        Assert.Null(retrieved[0].Purpose);

        Assert.Equal(2, retrieved[1].Seq);
        Assert.Equal("user", retrieved[1].Role);
        Assert.Equal("second message", retrieved[1].Content);
        Assert.Equal(1, retrieved[1].Iteration);
        Assert.Equal("prompt", retrieved[1].Purpose);

        Assert.Equal(3, retrieved[2].Seq);
        Assert.Equal("assistant", retrieved[2].Role);
        Assert.Equal("third message", retrieved[2].Content);
        Assert.Equal("response", retrieved[2].Purpose);
    }

    // ── 6. Enum stored as lowercase string ────────────────────────────────

    [Fact]
    public void DbContext_EnumStoredAsLowercaseString()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();

        var goal = new Goal
        {
            Id = "goal-enum-1",
            Description = "Enum test",
            Status = GoalStatus.InProgress,
            Priority = GoalPriority.High,
            Scope = GoalScope.Feature,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        ctx.Goals.Add(goal);
        ctx.SaveChanges();

        // Read raw column values via raw SQL to verify the converter stored lowercase strings
        var conn = GetSqliteConnection(ctx);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, priority, scope FROM goals WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", "goal-enum-1");

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        var statusValue = reader.GetString(0);
        var priorityValue = reader.GetString(1);
        var scopeValue = reader.GetString(2);

        // The LowercaseEnumConverter does e.ToString().ToLowerInvariant()
        // GoalStatus.InProgress -> "inprogress"
        // GoalPriority.High -> "high"
        // GoalScope.Feature -> "feature"
        Assert.Equal("inprogress", statusValue);
        Assert.Equal("high", priorityValue);
        Assert.Equal("feature", scopeValue);
    }

    // ── 7. JSON fields round-trip ─────────────────────────────────────────

    [Fact]
    public void DbContext_JsonFieldsRoundTrip()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();

        var goal = new Goal
        {
            Id = "goal-json-1",
            Description = "JSON fields test",
            Status = GoalStatus.Pending,
            Priority = GoalPriority.Normal,
            Scope = GoalScope.Patch,
            RepositoryNames = ["repo-a", "repo-b"],
            DependsOn = ["goal-1", "goal-2"],
            Documents = ["doc-1"],
            Metadata = new Dictionary<string, string> { ["key1"] = "value1" },
            Notes = ["note-1"],
            PhaseDurations = new Dictionary<string, double> { ["coding"] = 12.5 },
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        ctx.Goals.Add(goal);
        ctx.SaveChanges();

        ctx.Entry(goal).State = EntityState.Detached;

        var retrieved = ctx.Goals.Find("goal-json-1");

        Assert.NotNull(retrieved);

        // RepositoryNames
        Assert.Equal(["repo-a", "repo-b"], retrieved!.RepositoryNames);

        // DependsOn
        Assert.Equal(["goal-1", "goal-2"], retrieved.DependsOn);

        // Documents
        Assert.Equal(["doc-1"], retrieved.Documents);

        // Metadata
        Assert.Single(retrieved.Metadata);
        Assert.Equal("value1", retrieved.Metadata["key1"]);

        // Notes
        Assert.Equal(["note-1"], retrieved.Notes);

        // PhaseDurations
        Assert.NotNull(retrieved.PhaseDurations);
        Assert.Single(retrieved.PhaseDurations!);
        Assert.Equal(12.5, retrieved.PhaseDurations["coding"]);
    }

    // ── 8. Table and column names match existing schema ───────────────────

    [Fact]
    public void DbContext_TableAndColumnNamesMatchExistingSchema()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();
        var conn = GetSqliteConnection(ctx);

        // Verify all expected tables exist
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                tableNames.Add(reader.GetString(0));
        }

        Assert.Contains("goals", tableNames);
        Assert.Contains("releases", tableNames);
        Assert.Contains("goal_iterations", tableNames);
        Assert.Contains("pipelines", tableNames);
        Assert.Contains("conversation_entries", tableNames);

        // Verify key columns in goals table
        var goalColumns = GetTableColumns(conn, "goals");
        Assert.Contains("depends_on", goalColumns);
        Assert.Contains("documents", goalColumns);
        Assert.Contains("branch_cleaned_up", goalColumns);
        Assert.Contains("merge_commit_hash", goalColumns);
        Assert.Contains("release_id", goalColumns);
        Assert.Contains("scope", goalColumns);
        Assert.Contains("title", goalColumns);
        Assert.Contains("source_conversation_id", goalColumns);

        // Verify key columns in goal_iterations table
        var iterColumns = GetTableColumns(conn, "goal_iterations");
        Assert.Contains("phase_outputs_json", iterColumns);
        Assert.Contains("clarifications_json", iterColumns);
        Assert.Contains("build_success", iterColumns);

        // Verify UNIQUE index on goal_iterations(goal_id, iteration)
        var iterIndexes = GetIndexes(conn, "goal_iterations");
        Assert.Contains(iterIndexes, idx =>
            idx.Name.Contains("goal_iterations_goal_iteration", StringComparison.OrdinalIgnoreCase) &&
            idx.IsUnique &&
            idx.Columns.Contains("goal_id", StringComparer.OrdinalIgnoreCase) &&
            idx.Columns.Contains("iteration", StringComparer.OrdinalIgnoreCase));



        // Verify pipelines defaults for numeric/string NOT NULL columns
        var pipelineDefaults = GetTableDefaults(conn, "pipelines");
        Assert.Equal("'{}'", pipelineDefaults["phase_outputs"]);
        Assert.Equal("'{}'", pipelineDefaults["metrics_json"]);
        Assert.Equal("'{}'", pipelineDefaults["role_sessions_json"]);
        Assert.Equal("0", pipelineDefaults["improver_retries"]);
        Assert.Equal("10", pipelineDefaults["max_iterations"]);
        Assert.Equal("3", pipelineDefaults["max_retries"]);
        Assert.Equal("1", pipelineDefaults["iteration"]);
        Assert.Equal("0", pipelineDefaults["review_retries"]);
        Assert.Equal("0", pipelineDefaults["test_retries"]);
        Assert.Equal("'Planning'", pipelineDefaults["phase"]);
        Assert.Equal("1", pipelineDefaults["phase_occurrence"]);

        // Verify pipelines JSON columns are NOT NULL
        var pipelineColumns = GetTableColumnInfo(conn, "pipelines");
        Assert.True(pipelineColumns["phase_outputs"].NotNull);
        Assert.True(pipelineColumns["metrics_json"].NotNull);
        Assert.True(pipelineColumns["role_sessions_json"].NotNull);

        // Verify FK conversation_entries.goal_id -> pipelines.goal_id
        var convForeignKeys = GetForeignKeys(conn, "conversation_entries");
        Assert.Contains(convForeignKeys, fk =>
            string.Equals(fk.FromColumn, "goal_id", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.ToTable, "pipelines", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.ToColumn, "goal_id", StringComparison.OrdinalIgnoreCase));

        // Verify FK goal_iterations.goal_id -> goals.id ON DELETE CASCADE using PRAGMA columns directly
        AssertGoalIterationsForeignKeyCascade(conn);
    }

    private static void AssertGoalIterationsForeignKeyCascade(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_list('goal_iterations')";
        using var reader = cmd.ExecuteReader();
        bool foundCascade = false;
        while (reader.Read())
        {
            var referencedTable = reader.GetString(2);
            var fromColumn = reader.GetString(3);
            var onDelete = reader.GetString(6);
            if (referencedTable == "goals" &&
                fromColumn == "goal_id" &&
                onDelete == "CASCADE")
            {
                foundCascade = true;
                break;
            }
        }

        Assert.True(foundCascade, "goal_iterations must have FK goal_id -> goals(id) ON DELETE CASCADE");
    }

    /// <summary>
    /// Represents a single SQLite index read from the schema.
    /// </summary>
    private sealed record IndexInfo(string Name, bool IsUnique, List<string> Columns);

    /// <summary>
    /// Represents a single SQLite foreign key read from the schema.
    /// </summary>
    private sealed record ForeignKeyInfo(string FromColumn, string ToTable, string ToColumn, string OnDelete, string OnUpdate);

    /// <summary>
    /// Represents a SQLite column's nullability and default value.
    /// </summary>
    private sealed record ColumnInfo(bool NotNull, string? DefaultValue);

    private static Dictionary<string, string> GetTableDefaults(SqliteConnection conn, string tableName)
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var dflt = reader.IsDBNull(4) ? null : reader.GetString(4);
            defaults[name] = dflt ?? string.Empty;
        }
        return defaults;
    }

    private static Dictionary<string, ColumnInfo> GetTableColumnInfo(SqliteConnection conn, string tableName)
    {
        var columns = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var notNull = reader.GetInt32(3) == 1;
            var dflt = reader.IsDBNull(4) ? null : reader.GetString(4);
            columns[name] = new ColumnInfo(notNull, dflt);
        }
        return columns;
    }

    private static List<IndexInfo> GetIndexes(SqliteConnection conn, string tableName)
    {
        var indexes = new List<IndexInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA index_list({tableName})";
        using var reader = cmd.ExecuteReader();
        var indexList = new List<(string Name, bool Unique)>();
        while (reader.Read())
        {
            indexList.Add((reader.GetString(1), reader.GetInt32(2) == 1));
        }

        foreach (var (name, unique) in indexList)
        {
            var columns = new List<string>();
            using var colCmd = conn.CreateCommand();
            colCmd.CommandText = $"PRAGMA index_info({name})";
            using var colReader = colCmd.ExecuteReader();
            while (colReader.Read())
                columns.Add(colReader.GetString(2));
            indexes.Add(new IndexInfo(name, unique, columns));
        }
        return indexes;
    }

    private static List<ForeignKeyInfo> GetForeignKeys(SqliteConnection conn, string tableName)
    {
        var fks = new List<ForeignKeyInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA foreign_key_list({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var toTable = reader.GetString(2);
            var fromColumn = reader.GetString(3);
            var toColumn = reader.GetString(4);
            var onUpdate = reader.IsDBNull(5) ? "NO ACTION" : reader.GetString(5);
            var onDelete = reader.IsDBNull(6) ? "NO ACTION" : reader.GetString(6);
            fks.Add(new ForeignKeyInfo(fromColumn, toTable, toColumn, onDelete, onUpdate));
        }
        return fks;
    }

    /// <summary>
    /// Returns the set of column names for the specified table using PRAGMA table_info.
    /// </summary>
    private static HashSet<string> GetTableColumns(SqliteConnection conn, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1)); // column name is at index 1
        return columns;
    }

    // ── 9. Schema reconciliation (EnsureSchemaUpToDate) ───────────────────

    /// <summary>
    /// Creates a DbContext backed by an open in-memory SQLite connection WITHOUT calling
    /// <c>EnsureCreated()</c>. This simulates a database file that exists but is missing tables,
    /// allowing <see cref="DatabaseMigration.EnsureSchemaUpToDate"/> to be exercised against it.
    /// The connection is kept open by the returned DbContext for the lifetime of the database.
    /// </summary>
    private static CopilotHiveDbContext CreateEmptyDbContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new CopilotHiveDbContext(options);
    }

    private static HashSet<string> GetAllTableNames(SqliteConnection conn)
    {
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tableNames.Add(reader.GetString(0));
        return tableNames;
    }

    [Fact]
    public void EnsureSchema_FreshDb_CreatesAllTables()
    {
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        // Sanity: no tables exist yet.
        Assert.Empty(GetAllTableNames(conn));

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);

        var tableNames = GetAllTableNames(conn);
        Assert.Contains("goals", tableNames);
        Assert.Contains("releases", tableNames);
        Assert.Contains("goal_iterations", tableNames);
        Assert.Contains("pipelines", tableNames);
        Assert.Contains("conversation_entries", tableNames);
    }

    [Fact]
    public void EnsureSchema_ExistingDb_MissingGoalsTable_CreatesMissingTables()
    {
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        // Simulate an old database that only has the pipelines table.
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE pipelines (goal_id TEXT NOT NULL PRIMARY KEY, description TEXT)");

        Assert.Contains("pipelines", GetAllTableNames(conn));

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);

        var tableNames = GetAllTableNames(conn);
        Assert.Contains("goals", tableNames);
        Assert.Contains("releases", tableNames);
        Assert.Contains("goal_iterations", tableNames);
        Assert.Contains("conversation_entries", tableNames);

        // The pre-existing pipelines table must still be present and unchanged
        // (i.e. our minimal definition, NOT replaced by the EF schema).
        Assert.Contains("pipelines", tableNames);
        var pipelineColumns = GetTableColumns(conn, "pipelines");
        Assert.Contains("goal_id", pipelineColumns);
        Assert.Contains("description", pipelineColumns);
        // Columns that exist in the EF schema but not in the minimal one must NOT have been added.
        Assert.DoesNotContain("phase", pipelineColumns);
    }

    [Fact]
    public void EnsureSchema_UpToDateDb_IsNoOp()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();
        var conn = GetSqliteConnection(ctx);

        // All tables already exist via EnsureCreated; reconciliation must be a no-op.
        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);

        var tableNames = GetAllTableNames(conn);
        Assert.Contains("goals", tableNames);
        Assert.Contains("releases", tableNames);
        Assert.Contains("goal_iterations", tableNames);
        Assert.Contains("pipelines", tableNames);
        Assert.Contains("conversation_entries", tableNames);
    }

    [Fact]
    public void EnsureSchema_PreservesExistingData()
    {
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        // Simulate an old database with a pipelines table containing a row.
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE pipelines (goal_id TEXT NOT NULL PRIMARY KEY, description TEXT)");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO pipelines (goal_id, description) VALUES ('goal-keep-1', 'preserved description')");

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);

        // Missing tables were added.
        var tableNames = GetAllTableNames(conn);
        Assert.Contains("goals", tableNames);

        // The pre-existing pipeline data must still be intact.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT description FROM pipelines WHERE goal_id = 'goal-keep-1'";
        var result = cmd.ExecuteScalar();
        Assert.Equal("preserved description", result);
    }

    /// <summary>
    /// Verifies that indexes defined in the EF Core model are created by
    /// <see cref="DatabaseMigration.EnsureSchemaUpToDate"/> on a fresh database — specifically the
    /// unique composite index <c>idx_goal_iterations_goal_iteration</c> on
    /// <c>goal_iterations(goal_id, iteration)</c>.
    /// </summary>
    [Fact]
    public void EnsureSchema_FreshDb_CreatesIndexes()
    {
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        // Sanity: no indexes exist yet.
        Assert.Empty(GetAllIndexNames(conn));

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);

        var indexNames = GetAllIndexNames(conn);

        // The EF model defines a unique composite index on goal_iterations(goal_id, iteration).
        Assert.Contains("idx_goal_iterations_goal_iteration", indexNames);

        // Also verify the single-column index on goal_iterations(goal_id).
        Assert.Contains("idx_goal_iterations_goal", indexNames);

        // Verify the index is unique by inserting a goal, then an iteration, then attempting
        // a duplicate (goal_id, iteration) pair — SQLite should reject it.
        ctx.Goals.Add(new Goal
        {
            Id = "g-idx-1",
            Description = "Index test goal",
            Status = GoalStatus.Pending,
            Priority = GoalPriority.Normal,
            Scope = GoalScope.Patch,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();

        ctx.IterationSummaries.Add(new IterationSummaryEntity
        {
            GoalId = "g-idx-1",
            Iteration = 1,
            BuildSuccess = false,
            CreatedAt = DateTime.UtcNow.ToString("o"),
        });
        ctx.SaveChanges();

        // Attempting a second row with the same (goal_id, iteration) must fail due to uniqueness.
        ctx.IterationSummaries.Add(new IterationSummaryEntity
        {
            GoalId = "g-idx-1",
            Iteration = 1,
            BuildSuccess = false,
            CreatedAt = DateTime.UtcNow.ToString("o"),
        });
        Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
    }

    /// <summary>
    /// Verifies that after EnsureSchemaUpToDate on a fresh database, the goals table
    /// can be queried via the DbContext — exercising the full EF Core pipeline to confirm
    /// the created schema is compatible with EF's expectations (not just raw SQL).
    /// </summary>
    [Fact]
    public void EnsureSchema_FreshDb_DbContextCanQueryGoals()
    {
        using var ctx = CreateEmptyDbContext();

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);

        // This would throw "no such table: goals" if the schema were not reconciled.
        var goals = ctx.Goals.ToList();
        Assert.Empty(goals);

        // Insert via DbContext and verify round-trip.
        ctx.Goals.Add(new Goal
        {
            Id = "integration-goal-1",
            Description = "Integration test goal",
            Status = GoalStatus.Pending,
            Priority = GoalPriority.Normal,
            Scope = GoalScope.Patch,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();

        var retrieved = ctx.Goals.Single(g => g.Id == "integration-goal-1");
        Assert.Equal("Integration test goal", retrieved.Description);
    }

    private static HashSet<string> GetAllIndexNames(SqliteConnection conn)
    {
        var indexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            indexNames.Add(reader.GetString(0));
        return indexNames;
    }
}