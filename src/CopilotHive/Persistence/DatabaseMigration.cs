using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Persistence;

/// <summary>
/// Database schema reconciliation helpers. Extracted from <c>Program.cs</c> to keep
/// startup orchestration separate from migration logic.
/// </summary>
public static class DatabaseMigration
{
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
}
