using CopilotHive.Goals;
using CopilotHive.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Persistence;

/// <summary>
/// Tests for EF Core migration application via <c>Database.MigrateAsync</c>
/// layered on top of <see cref="DatabaseMigration.EnsureSchemaUpToDate"/>. Verifies that running
/// migrations on fresh, complete, and partial databases creates the expected schema and the
/// <c>__EFMigrationsHistory</c> tracking table without losing existing data.
/// </summary>
public sealed class MigrationTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a DbContext backed by an open in-memory SQLite connection WITHOUT calling
    /// <c>EnsureCreated()</c>. This simulates a database that exists but has no tables.
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

    /// <summary>
    /// Retrieves the underlying <see cref="SqliteConnection"/> from a DbContext
    /// so raw SQL can be executed against the same in-memory database.
    /// </summary>
    private static SqliteConnection GetSqliteConnection(CopilotHiveDbContext ctx)
    {
        var dbConn = ctx.Database.GetDbConnection();
        return (SqliteConnection)dbConn;
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

    // ── 1. Fresh DB: schema + history table ───────────────────────────────

    [Fact]
    public async Task Migrate_FreshDb_CreatesSchemaAndHistoryTable()
    {
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        // Sanity: no tables exist yet.
        Assert.Empty(GetAllTableNames(conn));

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);
        await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var tableNames = GetAllTableNames(conn);
        Assert.Contains("goals", tableNames);
        Assert.Contains("releases", tableNames);
        Assert.Contains("goal_iterations", tableNames);
        Assert.Contains("pipelines", tableNames);
        Assert.Contains("conversation_entries", tableNames);
        Assert.Contains("task_mappings", tableNames);

        // EF Core's migration history table must exist.
        Assert.Contains("__EFMigrationsHistory", tableNames);
    }

    // ── 2. Existing DB with all tables: no-op, preserves data ─────────────

    [Fact]
    public async Task Migrate_ExistingDbWithAllTables_IsNoOp()
    {
        using var ctx = CopilotHiveDbContext.CreateInMemory();
        var conn = GetSqliteConnection(ctx);

        // Insert a goal so we can verify data preservation across migration.
        ctx.Goals.Add(new Goal
        {
            Id = "goal-migrate-noop-1",
            Description = "Migration no-op test goal",
            Status = GoalStatus.Pending,
            Priority = GoalPriority.Normal,
            Scope = GoalScope.Patch,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        ctx.SaveChanges();

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);
        await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // The pre-existing goal data must still be present.
        var retrieved = ctx.Goals.Find("goal-migrate-noop-1");
        Assert.NotNull(retrieved);
        Assert.Equal("Migration no-op test goal", retrieved!.Description);

        // EF Core's migration history table must exist.
        var tableNames = GetAllTableNames(conn);
        Assert.Contains("__EFMigrationsHistory", tableNames);
    }

    // ── 3. Existing DB missing a table: creates missing, preserves data ───

    [Fact]
    public async Task Migrate_ExistingDbMissingTable_CreatesMissingTable()
    {
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        // Simulate an old database that only has a minimal pipelines table with a row.
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE pipelines (goal_id TEXT NOT NULL PRIMARY KEY, description TEXT)");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO pipelines (goal_id, description) VALUES ('goal-keep-1', 'preserved description')");

        Assert.Contains("pipelines", GetAllTableNames(conn));

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);
        await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var tableNames = GetAllTableNames(conn);

        // The missing goals table was created.
        Assert.Contains("goals", tableNames);

        // EF Core's migration history table must exist.
        Assert.Contains("__EFMigrationsHistory", tableNames);

        // The pre-existing pipelines data must still be intact.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT description FROM pipelines WHERE goal_id = 'goal-keep-1'";
        var result = cmd.ExecuteScalar();
        Assert.Equal("preserved description", result);
    }
}
