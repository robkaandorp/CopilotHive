using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;

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

    // ── 4. REAL legacy users table: additive upgrade preserves id/token, backfills enabled ─────

    /// <summary>
    /// The EXACT <c>users</c> DDL a pre-multi-user installation has: <c>github_id</c> NOT NULL and
    /// uniquely indexed, a <c>role</c> text column defaulting to <c>'admin'</c>, and NO <c>enabled</c>
    /// column. It is created by raw SQL — never by the current EF model — so the test cannot silently
    /// "pass" against a schema the new code generated for itself.
    /// </summary>
    private const string LegacyUsersTableSql =
        """
        CREATE TABLE "users" (
            "id" INTEGER NOT NULL CONSTRAINT "PK_users" PRIMARY KEY AUTOINCREMENT,
            "github_id" TEXT NOT NULL,
            "username" TEXT NOT NULL,
            "display_name" TEXT NULL,
            "avatar_url" TEXT NULL,
            "email" TEXT NULL,
            "access_token" TEXT NOT NULL,
            "refresh_token" TEXT NULL,
            "token_expires_at" TEXT NULL,
            "role" TEXT NOT NULL DEFAULT 'admin',
            "created_at" TEXT NOT NULL,
            "last_login_at" TEXT NULL
        );
        """;

    /// <summary>
    /// The full additive upgrade path over a REAL legacy schema: <see cref="DatabaseMigration.EnsureSchemaUpToDate"/>
    /// adds the <c>enabled</c> column (and the new invitation table), then EF <c>MigrateAsync</c> runs.
    /// The original account keeps its integer id, its credential and its Admin role, and reads back as
    /// ENABLED — the "existing rows upgrade to enabled Admins" contract.
    /// </summary>
    [Fact]
    public async Task LegacyUsersTable_AdditiveUpgrade_PreservesIdTokenAndRole_AndReadsAsEnabledAdmin()
    {
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        ctx.Database.ExecuteSqlRaw(LegacyUsersTableSql);
        ctx.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX \"IX_users_github_id\" ON \"users\" (\"github_id\")");
        ctx.Database.ExecuteSqlRaw(
            """
            INSERT INTO "users" (id, github_id, username, display_name, access_token, refresh_token, role, created_at, last_login_at)
            VALUES (7, '111', 'octocat', 'The Octocat', 'gho_original_token', 'refresh-xyz', 'admin',
                    '2024-01-01T00:00:00.0000000Z', '2024-06-01T00:00:00.0000000Z')
            """);

        // PREMISE: the column the new model maps does not exist yet.
        Assert.DoesNotContain("enabled", GetColumnNames(conn, "users"), StringComparer.OrdinalIgnoreCase);

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);
        await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // The additive column now exists, and the new invitation table was created.
        Assert.Contains("enabled", GetColumnNames(conn, "users"), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("user_invitations", GetAllTableNames(conn));

        // The ORIGINAL integer id and credential are preserved verbatim.
        var upgraded = ctx.Users.AsNoTracking().Single();
        Assert.Equal(7, upgraded.Id);
        Assert.Equal("111", upgraded.GitHubId);
        Assert.Equal("octocat", upgraded.Username);
        Assert.Equal("gho_original_token", upgraded.AccessToken);
        Assert.Equal("refresh-xyz", upgraded.RefreshToken);
        Assert.Equal("2024-01-01T00:00:00.0000000Z", upgraded.CreatedAt);

        // The legacy 'admin' TEXT maps onto the validated role enum, and the backfilled enabled
        // column reads as ENABLED.
        Assert.Equal(UserRole.Admin, upgraded.Role);
        Assert.True(upgraded.Enabled);

        // The stored role text is byte-identical to what the legacy column already held.
        using var roleCmd = conn.CreateCommand();
        roleCmd.CommandText = "SELECT role FROM users WHERE id = 7";
        Assert.Equal("admin", roleCmd.ExecuteScalar());
    }

    /// <summary>
    /// The upgrade is IDEMPOTENT: running the additive path twice changes nothing and never rewrites a
    /// value. A SECOND account that is explicitly disabled and non-Admin keeps those values.
    /// </summary>
    [Fact]
    public async Task LegacyUsersTable_RepeatedReconciliation_IsIdempotentAndKeepsExplicitValues()
    {
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        ctx.Database.ExecuteSqlRaw(LegacyUsersTableSql);
        ctx.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX \"IX_users_github_id\" ON \"users\" (\"github_id\")");
        ctx.Database.ExecuteSqlRaw(
            """
            INSERT INTO "users" (id, github_id, username, access_token, role, created_at)
            VALUES (3, '111', 'octocat', 'gho_original_token', 'admin', '2024-01-01T00:00:00.0000000Z')
            """);

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);
        await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // A later write through the model: an explicitly DISABLED, non-Admin account.
        ctx.Users.Add(new CopilotHive.Persistence.Entities.UserEntity
        {
            GitHubId = "222",
            Username = "second",
            AccessToken = "gho_second_token",
            Role = UserRole.Viewer,
            Enabled = false,
            CreatedAt = "2024-02-01T00:00:00.0000000Z",
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Re-run the additive reconciliation a second time.
        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);
        await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var accounts = ctx.Users.AsNoTracking().OrderBy(u => u.Id).ToList();
        Assert.Equal([3, 4], accounts.Select(u => u.Id));
        Assert.Equal(UserRole.Admin, accounts[0].Role);
        Assert.True(accounts[0].Enabled);
        Assert.Equal("gho_original_token", accounts[0].AccessToken);

        // The explicit false survived: the store default never overrode a real write.
        Assert.Equal(UserRole.Viewer, accounts[1].Role);
        Assert.False(accounts[1].Enabled);
        Assert.Equal("gho_second_token", accounts[1].AccessToken);
    }

    /// <summary>
    /// An unknown persisted role FAILS CLEARLY instead of silently becoming a role. This is the
    /// validated-conversion contract: no catch-all fallback exists.
    /// </summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("SuperAdmin")]
    [InlineData("2")]
    [InlineData("")]
    public async Task PersistedRole_UnknownValue_ThrowsInsteadOfBecomingARole(string persistedRole)
    {
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        ctx.Database.ExecuteSqlRaw(LegacyUsersTableSql);
        ctx.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX \"IX_users_github_id\" ON \"users\" (\"github_id\")");
        ctx.Database.ExecuteSqlRaw(
            """
            INSERT INTO "users" (id, github_id, username, access_token, role, created_at)
            VALUES (1, '111', 'octocat', 'gho_original_token', {0}, '2024-01-01T00:00:00.0000000Z')
            """,
            persistedRole);

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);
        await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => ctx.Users.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

        // The innermost cause names the offending value: a clear failure, not a silent fallback.
        var innermost = failure;
        while (innermost.InnerException is not null)
            innermost = innermost.InnerException;

        Assert.Contains("is not a supported CopilotHive user role", innermost.Message);
        _ = conn;
    }

    /// <summary>
    /// A legacy database whose <c>users.role</c> holds the legacy <c>'admin'</c> text still reads
    /// through the validated conversion: case-insensitive, so an oddly-cased legacy value is not a
    /// failure but is also not silently mapped to something else.
    /// </summary>
    [Theory]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    [InlineData("Admin")]
    public async Task PersistedRole_LegacyAdminText_MapsToAdmin(string persistedRole)
    {
        using var ctx = CreateEmptyDbContext();

        ctx.Database.ExecuteSqlRaw(LegacyUsersTableSql);
        ctx.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX \"IX_users_github_id\" ON \"users\" (\"github_id\")");
        ctx.Database.ExecuteSqlRaw(
            """
            INSERT INTO "users" (id, github_id, username, access_token, role, created_at)
            VALUES (1, '111', 'octocat', 'gho_original_token', {0}, '2024-01-01T00:00:00.0000000Z')
            """,
            persistedRole);

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);
        await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var user = await ctx.Users.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.True(user.Enabled);
    }

    /// <summary>
    /// The invitations table is created on an existing installation WITHOUT touching the users rows,
    /// and it carries NO token columns at all — the storage invariant that keeps invitations out of
    /// the users table.
    /// </summary>
    [Fact]
    public async Task LegacyUsersTable_UpgradeCreatesInvitationTableWithNoTokenColumns()
    {
        using var ctx = CreateEmptyDbContext();
        var conn = GetSqliteConnection(ctx);

        ctx.Database.ExecuteSqlRaw(LegacyUsersTableSql);
        ctx.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX \"IX_users_github_id\" ON \"users\" (\"github_id\")");
        ctx.Database.ExecuteSqlRaw(
            """
            INSERT INTO "users" (id, github_id, username, access_token, role, created_at)
            VALUES (1, '111', 'octocat', 'gho_original_token', 'admin', '2024-01-01T00:00:00.0000000Z')
            """);

        DatabaseMigration.EnsureSchemaUpToDate(ctx, NullLogger.Instance);
        await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var invitationColumns = GetColumnNames(conn, "user_invitations");
        Assert.Contains("normalized_username", invitationColumns);
        Assert.Contains("role", invitationColumns);
        Assert.Contains("enabled", invitationColumns);
        Assert.Contains("activated_user_id", invitationColumns);
        Assert.Contains("activated_at", invitationColumns);
        Assert.Contains("revoked_at", invitationColumns);

        // NO OAuth material may live in the invitation table.
        Assert.DoesNotContain(invitationColumns, c => c.Contains("token", StringComparison.OrdinalIgnoreCase));

        // The unique index on the normalized username is what arbitrates duplicates.
        using var indexCmd = conn.CreateCommand();
        indexCmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'user_invitations' AND name = 'idx_user_invitations_normalized_username'";
        Assert.Equal("idx_user_invitations_normalized_username", indexCmd.ExecuteScalar());

        // The users row was not modified beyond the additive column.
        Assert.Single(ctx.Users.AsNoTracking().ToList());
    }

    /// <summary>Returns the column names of a table via PRAGMA.</summary>
    private static List<string> GetColumnNames(SqliteConnection conn, string table)
    {
        var columns = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }
}
