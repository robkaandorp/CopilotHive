using CopilotHive.Goals;
using CopilotHive.Persistence.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using System.Globalization;
using System.Text.Json;

namespace CopilotHive.Persistence;

/// <summary>
/// Database schema reconciliation and legacy data migration helpers. Extracted from
/// <c>Program.cs</c> to keep startup orchestration separate from migration logic.
/// </summary>
public static class DatabaseMigration
{
    private static readonly JsonSerializerOptions MigrationJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Reconciles the database schema with the EF Core model by creating any missing tables and
    /// indexes. Unlike <c>EnsureCreated()</c>, which only creates the schema when the database
    /// file does not yet exist, this method inspects the existing database and adds only the
    /// tables and indexes that are missing — making it safe to run against databases created by
    /// older versions of the code.
    /// </summary>
    /// <param name="dbContext">The EF Core DbContext whose model defines the target schema.</param>
    /// <param name="logger">Logger for reporting created tables and indexes.</param>
    internal static void EnsureSchemaUpToDate(CopilotHiveDbContext dbContext, ILogger logger)
    {
        // Full DDL EF Core would use to create the entire schema (tables, indexes, FK constraints).
        var createScript = dbContext.Database.GenerateCreateScript();

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
            openedHere = true;
        }

        try
        {
            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    existingTables.Add(reader.GetString(0));
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    existingIndexes.Add(reader.GetString(0));
            }

            // Split the generated script into individual statements. Statements are executed via a
            // raw DbCommand (NOT ExecuteSqlRaw) so that literal braces in DDL default values such as
            // '{}' are not misinterpreted as format placeholders.
            var statements = createScript.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawStatement in statements)
            {
                var statement = rawStatement.Trim();
                if (statement.Length == 0)
                    continue;

                if (statement.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    var tableName = ExtractBracketedName(statement, "CREATE TABLE");
                    if (tableName is not null && existingTables.Contains(tableName))
                        continue;

                    logger.LogInformation("Creating missing table {TableName}", tableName ?? "<unknown>");
                    ExecuteRaw(connection, statement);
                }
                else if (statement.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase) ||
                         statement.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase))
                {
                    var prefix = statement.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase)
                        ? "CREATE UNIQUE INDEX"
                        : "CREATE INDEX";
                    var indexName = ExtractBracketedName(statement, prefix);
                    if (indexName is not null && existingIndexes.Contains(indexName))
                        continue;

                    logger.LogInformation("Creating missing index {IndexName}", indexName ?? "<unknown>");
                    ExecuteRaw(connection, statement);
                }
                else
                {
                    ExecuteRaw(connection, statement);
                }
            }
        }
        finally
        {
            if (openedHere)
                connection.Close();
        }
    }

    /// <summary>
    /// Executes a single DDL statement directly against the open connection using a raw
    /// <see cref="System.Data.Common.DbCommand"/>, avoiding EF Core's parameter parsing.
    /// </summary>
    private static void ExecuteRaw(System.Data.Common.DbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Extracts the first quoted identifier following the given DDL prefix. EF Core's create
    /// script may quote identifiers with brackets (<c>[goals]</c>) or double quotes
    /// (<c>"goals"</c>) depending on the provider; both forms are supported here.
    /// Returns the unquoted name, or <c>null</c> if no quoted identifier is found.
    /// </summary>
    private static string? ExtractBracketedName(string statement, string prefix)
    {
        var afterPrefix = statement.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        var searchStart = afterPrefix >= 0 ? afterPrefix + prefix.Length : 0;

        // Find the first opening quote of either supported style.
        var bracketOpen = statement.IndexOf('[', searchStart);
        var quoteOpen = statement.IndexOf('"', searchStart);

        var useBracket = bracketOpen >= 0 && (quoteOpen < 0 || bracketOpen < quoteOpen);
        if (useBracket)
        {
            var close = statement.IndexOf(']', bracketOpen + 1);
            if (close < 0)
                return null;
            return statement.Substring(bracketOpen + 1, close - bracketOpen - 1);
        }

        if (quoteOpen >= 0)
        {
            var close = statement.IndexOf('"', quoteOpen + 1);
            if (close < 0)
                return null;
            return statement.Substring(quoteOpen + 1, close - quoteOpen - 1);
        }

        return null;
    }

    /// <summary>
    /// Migrates goals, iteration summaries, and releases from the legacy <c>goals.db</c> database
    /// into the EF Core-managed <c>copilothive.db</c> database. After a successful migration the
    /// legacy file is renamed to <c>goals.db.migrated</c> so the migration runs only once.
    /// </summary>
    /// <param name="oldDbPath">Path to the legacy goals.db SQLite database.</param>
    /// <param name="dbContextFactory">Factory for creating the target <see cref="CopilotHiveDbContext"/>.</param>
    /// <param name="logger">Logger for reporting migration progress.</param>
    internal static void MigrateGoalsDatabase(
        string oldDbPath,
        IDbContextFactory<CopilotHiveDbContext> dbContextFactory,
        ILogger logger)
    {
        var goals = new List<Goal>();
        var iterations = new List<IterationSummaryEntity>();
        var releases = new List<Release>();

        using (var conn = new SqliteConnection($"Data Source={oldDbPath}"))
        {
            conn.Open();

            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var tablesCmd = conn.CreateCommand())
            {
                tablesCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var tablesReader = tablesCmd.ExecuteReader();
                while (tablesReader.Read())
                    existingTables.Add(tablesReader.GetString(0));
            }

            if (existingTables.Contains("goals"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM goals";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    goals.Add(ReadMigrationGoal(reader));
            }

            if (existingTables.Contains("goal_iterations"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM goal_iterations";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    iterations.Add(ReadMigrationIteration(reader));
            }

            if (existingTables.Contains("releases"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM releases";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    releases.Add(ReadMigrationRelease(reader));
            }
        }

        using (var db = dbContextFactory.CreateDbContext())
        {
            var importedGoals = 0;
            foreach (var goal in goals)
            {
                if (db.Goals.Any(g => g.Id == goal.Id)) continue;
                db.Goals.Add(goal);
                importedGoals++;
            }

            var importedIterations = 0;
            foreach (var iteration in iterations)
            {
                // Skip iterations whose goal is missing (preserve FK integrity).
                if (!goals.Any(g => g.Id == iteration.GoalId) && !db.Goals.Any(g => g.Id == iteration.GoalId))
                    continue;
                db.IterationSummaries.Add(iteration);
                importedIterations++;
            }

            var importedReleases = 0;
            foreach (var release in releases)
            {
                if (db.Releases.Any(r => r.Id == release.Id)) continue;
                db.Releases.Add(release);
                importedReleases++;
            }

            db.SaveChanges();

            logger.LogInformation(
                "Migrated {Goals} goals, {Iterations} iteration summaries, {Releases} releases from goals.db to copilothive.db",
                importedGoals, importedIterations, importedReleases);
        }

        var migratedPath = oldDbPath + ".migrated";
        if (File.Exists(migratedPath))
            File.Delete(migratedPath);
        File.Move(oldDbPath, migratedPath);
        logger.LogInformation("Renamed legacy database to {MigratedPath}", migratedPath);
    }

    private static bool MigrationColumnExists(SqliteDataReader reader, string columnName, out int ordinal)
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

    private static Goal ReadMigrationGoal(SqliteDataReader reader)
    {
        var scope = GoalScope.Patch;
        if (MigrationColumnExists(reader, "scope", out var scopeOrd) && !reader.IsDBNull(scopeOrd))
            Enum.TryParse<GoalScope>(reader.GetString(scopeOrd), ignoreCase: true, out scope);

        var goal = new Goal
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Description = reader.GetString(reader.GetOrdinal("description")),
            Status = Enum.Parse<GoalStatus>(reader.GetString(reader.GetOrdinal("status")), ignoreCase: true),
            Priority = Enum.Parse<GoalPriority>(reader.GetString(reader.GetOrdinal("priority")), ignoreCase: true),
            Scope = scope,
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };

        var reposOrd = reader.GetOrdinal("repositories");
        if (!reader.IsDBNull(reposOrd))
        {
            var repos = JsonSerializer.Deserialize<List<string>>(reader.GetString(reposOrd), MigrationJsonOptions);
            if (repos is not null) goal.RepositoryNames.AddRange(repos);
        }

        var metaOrd = reader.GetOrdinal("metadata");
        if (!reader.IsDBNull(metaOrd))
        {
            var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(metaOrd), MigrationJsonOptions);
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
            var notes = JsonSerializer.Deserialize<List<string>>(reader.GetString(notesOrd), MigrationJsonOptions);
            if (notes is not null) goal.Notes = notes;
        }

        var phaseDurOrd = reader.GetOrdinal("phase_durations");
        if (!reader.IsDBNull(phaseDurOrd))
            goal.PhaseDurations = JsonSerializer.Deserialize<Dictionary<string, double>>(reader.GetString(phaseDurOrd), MigrationJsonOptions);

        var totalDurOrd = reader.GetOrdinal("total_duration_seconds");
        if (!reader.IsDBNull(totalDurOrd))
            goal.TotalDurationSeconds = reader.GetDouble(totalDurOrd);

        if (MigrationColumnExists(reader, "depends_on", out var dependsOnOrd) && !reader.IsDBNull(dependsOnOrd))
        {
            var deps = JsonSerializer.Deserialize<List<string>>(reader.GetString(dependsOnOrd), MigrationJsonOptions);
            if (deps is not null) goal.DependsOn.AddRange(deps);
        }

        if (MigrationColumnExists(reader, "merge_commit_hash", out var mergeHashOrd) && !reader.IsDBNull(mergeHashOrd))
            goal.MergeCommitHash = reader.GetString(mergeHashOrd);

        if (MigrationColumnExists(reader, "release_id", out var releaseIdOrd) && !reader.IsDBNull(releaseIdOrd))
            goal.ReleaseId = reader.GetString(releaseIdOrd);

        if (MigrationColumnExists(reader, "documents", out var documentsOrd) && !reader.IsDBNull(documentsOrd))
        {
            var docs = JsonSerializer.Deserialize<List<string>>(reader.GetString(documentsOrd), MigrationJsonOptions);
            if (docs is not null) goal.Documents = docs;
        }

        if (MigrationColumnExists(reader, "branch_cleaned_up", out var branchCleanedUpOrd) && !reader.IsDBNull(branchCleanedUpOrd))
            goal.BranchCleanedUp = reader.GetInt32(branchCleanedUpOrd) == 1;

        return goal;
    }

    private static IterationSummaryEntity ReadMigrationIteration(SqliteDataReader reader)
    {
        var entity = new IterationSummaryEntity
        {
            GoalId = reader.GetString(reader.GetOrdinal("goal_id")),
            Iteration = reader.GetInt32(reader.GetOrdinal("iteration")),
        };

        if (MigrationColumnExists(reader, "phases_json", out var phasesOrd) && !reader.IsDBNull(phasesOrd))
            entity.PhasesJson = reader.GetString(phasesOrd);

        if (MigrationColumnExists(reader, "test_total", out var ttOrd) && !reader.IsDBNull(ttOrd))
            entity.TestTotal = reader.GetInt32(ttOrd);
        if (MigrationColumnExists(reader, "test_passed", out var tpOrd) && !reader.IsDBNull(tpOrd))
            entity.TestPassed = reader.GetInt32(tpOrd);
        if (MigrationColumnExists(reader, "test_failed", out var tfOrd) && !reader.IsDBNull(tfOrd))
            entity.TestFailed = reader.GetInt32(tfOrd);

        if (MigrationColumnExists(reader, "review_verdict", out var rvOrd) && !reader.IsDBNull(rvOrd))
            entity.ReviewVerdict = reader.GetString(rvOrd);

        if (MigrationColumnExists(reader, "notes_json", out var notesOrd) && !reader.IsDBNull(notesOrd))
            entity.NotesJson = reader.GetString(notesOrd);

        if (MigrationColumnExists(reader, "phase_outputs_json", out var poOrd) && !reader.IsDBNull(poOrd))
            entity.PhaseOutputsJson = reader.GetString(poOrd);

        if (MigrationColumnExists(reader, "clarifications_json", out var clOrd) && !reader.IsDBNull(clOrd))
            entity.ClarificationsJson = reader.GetString(clOrd);

        if (MigrationColumnExists(reader, "build_success", out var bsOrd) && !reader.IsDBNull(bsOrd))
            entity.BuildSuccess = reader.GetInt32(bsOrd) == 1;

        if (MigrationColumnExists(reader, "created_at", out var caOrd) && !reader.IsDBNull(caOrd))
            entity.CreatedAt = reader.GetString(caOrd);
        else
            entity.CreatedAt = DateTime.UtcNow.ToString("O");

        return entity;
    }

    private static Release ReadMigrationRelease(SqliteDataReader reader)
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
            var repos = JsonSerializer.Deserialize<List<string>>(reader.GetString(reposOrd), MigrationJsonOptions);
            if (repos is not null) release.RepositoryNames.AddRange(repos);
        }

        return release;
    }
}
