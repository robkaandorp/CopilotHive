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
                    {
                        // Table already exists — reconcile any columns the EF model added since
                        // the table was originally created (e.g. a new property on an entity).
                        ReconcileColumns(connection, tableName, statement, logger);
                        continue;
                    }

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
    /// Reconciles the columns of an existing table with the EF Core model by adding any
    /// columns present in the generated <c>CREATE TABLE</c> statement that are missing from
    /// the live table. Each added column uses the full type and constraint text (including
    /// any <c>DEFAULT</c> clause) from the DDL so that existing rows receive the default value.
    /// This is a general solution that works for any missing column on any existing table.
    /// </summary>
    /// <param name="connection">Open database connection.</param>
    /// <param name="tableName">Name of the existing table to reconcile.</param>
    /// <param name="createStatement">The EF Core-generated <c>CREATE TABLE</c> statement for the table.</param>
    /// <param name="logger">Logger for reporting added columns.</param>
    private static void ReconcileColumns(
        System.Data.Common.DbConnection connection,
        string tableName,
        string createStatement,
        ILogger logger)
    {
        // Discover the columns that already exist on the live table.
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk
                existingColumns.Add(reader.GetString(1));
            }
        }

        // Parse the column definitions from the CREATE TABLE statement body.
        foreach (var (columnName, definition) in ParseColumnDefinitions(createStatement))
        {
            if (existingColumns.Contains(columnName))
                continue;

            // SQLite cannot ALTER TABLE ADD a NOT NULL column without a DEFAULT when the table
            // already has rows ("Cannot add a NOT NULL column with default value NULL"). Skip such
            // columns — they cannot be reconciled in place and require a full table rebuild/migration.
            var isNotNull = definition.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase);
            var hasDefault = definition.Contains("DEFAULT", StringComparison.OrdinalIgnoreCase);
            if (isNotNull && !hasDefault)
            {
                logger.LogWarning(
                    "Skipping reconciliation of column {ColumnName} on table {TableName}: NOT NULL without a DEFAULT cannot be added via ALTER TABLE.",
                    columnName, tableName);
                continue;
            }

            var alter = $"ALTER TABLE \"{tableName}\" ADD COLUMN {definition}";
            logger.LogInformation("Adding missing column {ColumnName} to table {TableName}", columnName, tableName);
            ExecuteRaw(connection, alter);
        }
    }

    /// <summary>
    /// Parses individual column definitions from the body of an EF Core-generated
    /// <c>CREATE TABLE</c> statement (the text between the outermost parentheses). Returns
    /// each column's unquoted name paired with its full definition text (name + type +
    /// constraints, with the identifier normalised to double quotes). Table-level constraints
    /// such as <c>PRIMARY KEY</c>, <c>FOREIGN KEY</c>, <c>UNIQUE</c>, and <c>CONSTRAINT</c> are
    /// skipped because they are not column definitions.
    /// </summary>
    private static IEnumerable<(string ColumnName, string Definition)> ParseColumnDefinitions(string createStatement)
    {
        var open = createStatement.IndexOf('(');
        var close = createStatement.LastIndexOf(')');
        if (open < 0 || close <= open)
            yield break;

        var body = createStatement.Substring(open + 1, close - open - 1);

        // Split on commas that are not nested inside parentheses (e.g. DECIMAL(10,2)).
        foreach (var rawPart in SplitTopLevel(body))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
                continue;

            // Skip table-level constraints — these are not column definitions.
            if (part.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columnName = ExtractLeadingIdentifier(part);
            if (columnName is null)
                continue;

            // Normalise the identifier to double-quoted form for the ALTER statement.
            var remainder = part[FindIdentifierEnd(part)..].TrimStart();
            var definition = $"\"{columnName}\" {remainder}".TrimEnd();
            yield return (columnName, definition);
        }
    }

    /// <summary>Splits a string on top-level commas, ignoring commas nested inside parentheses.</summary>
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (c == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    /// <summary>
    /// Extracts the leading quoted identifier from a column definition. Supports bracket
    /// (<c>[col]</c>) and double-quote (<c>"col"</c>) quoting. Returns the unquoted name,
    /// or <c>null</c> if the part does not begin with a quoted identifier.
    /// </summary>
    private static string? ExtractLeadingIdentifier(string part)
    {
        if (part.Length == 0)
            return null;

        if (part[0] == '[')
        {
            var close = part.IndexOf(']', 1);
            return close < 0 ? null : part.Substring(1, close - 1);
        }

        if (part[0] == '"')
        {
            var close = part.IndexOf('"', 1);
            return close < 0 ? null : part.Substring(1, close - 1);
        }

        return null;
    }

    /// <summary>Returns the index just past the leading quoted identifier in a column definition.</summary>
    private static int FindIdentifierEnd(string part)
    {
        if (part.Length == 0)
            return 0;

        if (part[0] == '[')
        {
            var close = part.IndexOf(']', 1);
            return close < 0 ? part.Length : close + 1;
        }

        if (part[0] == '"')
        {
            var close = part.IndexOf('"', 1);
            return close < 0 ? part.Length : close + 1;
        }

        return 0;
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
