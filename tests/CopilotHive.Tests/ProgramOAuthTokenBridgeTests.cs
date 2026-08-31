using CopilotHive.Configuration;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Covers the orchestrator's OAuth credential bridge wiring: the pre-Build direct-database
/// token lookup that gives the very first config-repo sync a credential, and the post-Build
/// reattachment that re-points the resolver at the live <see cref="UserService"/>.
/// </summary>
public sealed class ProgramOAuthTokenBridgeTests : IDisposable
{
    private readonly string _tempDir;

    public ProgramOAuthTokenBridgeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-oauthbridge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Path of a fresh, not-yet-created database file inside the test's temp dir.</summary>
    private string NewDbPath() => Path.Combine(_tempDir, $"{Guid.NewGuid():N}.db");

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null
            && !Directory.GetFiles(dir, "*.slnx").Any()
            && !Directory.Exists(Path.Combine(dir, "src", "CopilotHive")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        Assert.NotNull(dir);
        Assert.True(
            Directory.Exists(Path.Combine(dir, "src", "CopilotHive")),
            $"Repository root not found from {AppContext.BaseDirectory}");
        return dir;
    }

    private static int CountOccurrences(string source, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }

    /// <summary>Creates the schema at <paramref name="dbPath"/> and seeds the given users.</summary>
    private static async Task SeedAsync(string dbPath, params UserEntity[] users)
    {
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using var db = new CopilotHiveDbContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        db.Users.AddRange(users);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static UserEntity User(string username, string token, string githubId) => new()
    {
        GitHubId = githubId,
        Username = username,
        AccessToken = token,
    };

    // ── Program.cs attachment wiring ─────────────────────────────────────────

    [Fact]
    public void ProgramCs_AttachesPreBuildResolverBeforeSyncAndLiveResolverAfterBuild()
    {
        var programPath = Path.Combine(FindRepoRoot(), "src", "CopilotHive", "Program.cs");
        Assert.True(File.Exists(programPath), $"Program.cs not found at {programPath}");
        var source = File.ReadAllText(programPath).ReplaceLineEndings("\n");

        const string PreBuildAttachment =
            "configRepo.TokenResolver = ct => PreBuildOAuthTokenLookup(dbPath, ct);";
        const string InitialSync = "await configRepo.SyncRepoAsync();";
        const string Build = "var app = builder.Build();";
        const string LiveAttachment = "AttachLiveTokenResolver(app.Services, userService);";

        foreach (var fragment in new[] { PreBuildAttachment, InitialSync, Build, LiveAttachment })
        {
            Assert.True(
                CountOccurrences(source, fragment) == 1,
                $"Expected exactly one Program.cs occurrence of '{fragment}'.");
        }

        Assert.True(
            source.IndexOf(PreBuildAttachment, StringComparison.Ordinal)
                < source.IndexOf(InitialSync, StringComparison.Ordinal),
            "The pre-Build OAuth resolver must be attached before the initial config-repo sync.");
        Assert.True(
            source.IndexOf(Build, StringComparison.Ordinal)
                < source.IndexOf(LiveAttachment, StringComparison.Ordinal),
            "The live UserService resolver must be attached only after the host is built.");
    }

    // ── PreBuildOAuthTokenLookup ──────────────────────────────────────────────

    [Fact]
    public async Task PreBuildOAuthTokenLookup_AdminUserWithToken_ReturnsTheToken()
    {
        var dbPath = NewDbPath();
        await SeedAsync(dbPath, User("admin", "gho_persisted_admin_token", "1"));

        var token = await Program.PreBuildOAuthTokenLookup(dbPath, TestContext.Current.CancellationToken);

        Assert.Equal("gho_persisted_admin_token", token);
    }

    [Fact]
    public async Task PreBuildOAuthTokenLookup_MultipleUsers_ReturnsTheLowestIdUserLikeGetAdminUser()
    {
        // GetAdminUserAsync orders by Id and takes the first — the admin is the FIRST user
        // to have authenticated, not an arbitrary row.
        var dbPath = NewDbPath();
        await SeedAsync(
            dbPath,
            User("admin", "first-user-token", "1"),
            User("other", "second-user-token", "2"));

        var token = await Program.PreBuildOAuthTokenLookup(dbPath, TestContext.Current.CancellationToken);

        Assert.Equal("first-user-token", token);
    }

    [Fact]
    public async Task PreBuildOAuthTokenLookup_NoUsers_ReturnsNull()
    {
        // The declared first-run case: the schema exists but nobody has logged in yet.
        var dbPath = NewDbPath();
        await SeedAsync(dbPath);

        var token = await Program.PreBuildOAuthTokenLookup(dbPath, TestContext.Current.CancellationToken);

        Assert.Null(token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task PreBuildOAuthTokenLookup_WhitespaceToken_ReturnsNull(string blank)
    {
        var dbPath = NewDbPath();
        await SeedAsync(dbPath, User("admin", blank, "1"));

        var token = await Program.PreBuildOAuthTokenLookup(dbPath, TestContext.Current.CancellationToken);

        Assert.Null(token);
    }

    [Fact]
    public async Task PreBuildOAuthTokenLookup_DatabaseFileDoesNotExist_ReturnsNullWithoutThrowing()
    {
        // A fresh installation has no database file yet — startup must not abort.
        var dbPath = Path.Combine(_tempDir, "definitely-absent", "missing.db");

        var token = await Program.PreBuildOAuthTokenLookup(dbPath, TestContext.Current.CancellationToken);

        Assert.Null(token);
    }

    [Fact]
    public async Task PreBuildOAuthTokenLookup_CorruptDatabase_ReturnsNullWithoutThrowing()
    {
        // A file that exists but is not a SQLite database at all.
        var dbPath = NewDbPath();
        await File.WriteAllTextAsync(dbPath, "this is not a sqlite database", TestContext.Current.CancellationToken);

        var token = await Program.PreBuildOAuthTokenLookup(dbPath, TestContext.Current.CancellationToken);

        Assert.Null(token);
    }

    [Fact]
    public async Task PreBuildOAuthTokenLookup_SchemaMissing_ReturnsNullWithoutThrowing()
    {
        // A valid SQLite file with no Users table (EnsureCreated never ran).
        var dbPath = NewDbPath();
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        await using (var db = new CopilotHiveDbContext(options))
        {
            await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await db.Database.CloseConnectionAsync();
        }

        var token = await Program.PreBuildOAuthTokenLookup(dbPath, TestContext.Current.CancellationToken);

        Assert.Null(token);
    }

    [Fact]
    public async Task PreBuildOAuthTokenLookup_CancelledToken_RethrowsOperationCanceled()
    {
        // A cancellation is NEVER reported as "no token" — that would silently strip the
        // credential instead of terminating the caller.
        var dbPath = NewDbPath();
        await SeedAsync(dbPath, User("admin", "gho_persisted_admin_token", "1"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Program.PreBuildOAuthTokenLookup(dbPath, cts.Token));
    }

    [Fact]
    public async Task PreBuildOAuthTokenLookup_CancelledTokenOnUnusableDatabase_StillRethrows()
    {
        // Cancellation outranks the degrade-to-null path: a broken database must not turn a
        // cancellation into a null.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Program.PreBuildOAuthTokenLookup(Path.Combine(_tempDir, "absent", "missing.db"), cts.Token));
    }

    [Fact]
    public async Task PreBuildOAuthTokenLookup_ResultFeedsConfigRepoManagerTokenResolver()
    {
        // The end-to-end shape of the pre-sync attachment made in Program: the helper is the
        // ConfigRepoManager's TokenResolver, so the token reaches the git origin URL.
        var dbPath = NewDbPath();
        await SeedAsync(dbPath, User("admin", "gho_bridge_token", "1"));

        var local = Path.Combine(_tempDir, "clone");
        Directory.CreateDirectory(Path.Combine(local, ".git"));

        var commands = new List<string>();
        var manager = new ConfigRepoManager("https://github.com/org/config.git", local)
        {
            GitRunner = (_, args, _) =>
            {
                commands.Add(string.Join(' ', args));
                return Task.FromResult(new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty));
            },
            TokenResolver = ct => Program.PreBuildOAuthTokenLookup(dbPath, ct),
        };

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "remote set-url origin https://x-access-token:gho_bridge_token@github.com/org/config.git",
                "pull",
            ],
            commands);
    }

    // ── AttachLiveTokenResolver ───────────────────────────────────────────────

    /// <summary>
    /// Builds a service provider containing a live <see cref="UserService"/> over an in-memory
    /// SQLite database, plus (optionally) a registered <see cref="ConfigRepoManager"/>.
    /// </summary>
    private (ServiceProvider Provider, UserService Service, UserServiceTestDb Db) CreateProvider(
        ConfigRepoManager? configRepo)
    {
        var db = new UserServiceTestDb();
        var service = new UserService(db, NullLogger<UserService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(service);
        if (configRepo is not null)
            services.AddSingleton(configRepo);

        return (services.BuildServiceProvider(), service, db);
    }

    private ConfigRepoManager CreateManager(string name) =>
        new("https://github.com/org/config.git", Path.Combine(_tempDir, name));

    [Fact]
    public async Task AttachLiveTokenResolver_RepointsTheResolverAtTheLiveUserService()
    {
        var manager = CreateManager("attach");
        // Start from the pre-Build shape: a resolver that is NOT the service.
        manager.TokenResolver = _ => Task.FromResult<string?>("pre-build-token");

        var (provider, service, db) = CreateProvider(manager);
        using var _p = provider;
        using var _d = db;

        var attached = Program.AttachLiveTokenResolver(provider, service);

        Assert.True(attached);
        Assert.NotNull(manager.TokenResolver);

        // The service now has a token — the reattached resolver must return IT, proving the
        // resolver reads through the live service rather than the stale pre-Build closure.
        await service.CreateOrUpdateUserAsync(
            githubId: "1",
            username: "admin",
            displayName: null,
            avatarUrl: null,
            email: null,
            accessToken: "live-service-token",
            refreshToken: null,
            tokenExpiresAt: null,
            TestContext.Current.CancellationToken);

        var resolved = await manager.TokenResolver!(TestContext.Current.CancellationToken);

        Assert.Equal("live-service-token", resolved);
    }

    [Fact]
    public async Task AttachLiveTokenResolver_ReflectsTokenChangesAfterAttachment()
    {
        // The resolver must call the service on EVERY invocation, not capture a value once.
        var manager = CreateManager("attach-live");
        var (provider, service, db) = CreateProvider(manager);
        using var _p = provider;
        using var _d = db;

        Program.AttachLiveTokenResolver(provider, service);

        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await manager.TokenResolver!(ct));

        await service.CreateOrUpdateUserAsync("1", "admin", null, null, null, "first-token", null, null, ct);
        Assert.Equal("first-token", await manager.TokenResolver!(ct));

        await service.CreateOrUpdateUserAsync("1", "admin", null, null, null, "rotated-token", null, null, ct);
        Assert.Equal("rotated-token", await manager.TokenResolver!(ct));
    }

    [Fact]
    public void AttachLiveTokenResolver_NoConfigRepoRegistered_IsANoOpReturningFalse()
    {
        // The ConfigRepoManager is registered ONLY when a config repo is configured.
        var (provider, service, db) = CreateProvider(configRepo: null);
        using var _p = provider;
        using var _d = db;

        var attached = Program.AttachLiveTokenResolver(provider, service);

        Assert.False(attached);
    }

    [Fact]
    public async Task AttachLiveTokenResolver_ResolverIsUsedByTheConfigRepoGitOperations()
    {
        var manager = CreateManager("attach-git");
        Directory.CreateDirectory(Path.Combine(manager.LocalPath, ".git"));

        var commands = new List<string>();
        manager.GitRunner = (_, args, _) =>
        {
            commands.Add(string.Join(' ', args));
            return Task.FromResult(new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty));
        };

        var (provider, service, db) = CreateProvider(manager);
        using var _p = provider;
        using var _d = db;

        var ct = TestContext.Current.CancellationToken;
        await service.CreateOrUpdateUserAsync("1", "admin", null, null, null, "gho_live_token", null, null, ct);

        Program.AttachLiveTokenResolver(provider, service);
        await manager.SyncRepoAsync(ct);

        Assert.Equal(
            [
                "remote set-url origin https://x-access-token:gho_live_token@github.com/org/config.git",
                "pull",
            ],
            commands);
    }
}

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> over a single shared, open in-memory SQLite
/// connection, so contexts created from it all observe the same database.
/// </summary>
internal sealed class UserServiceTestDb : IDbContextFactory<CopilotHiveDbContext>, IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

    public UserServiceTestDb()
    {
        _connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var ctx = CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public CopilotHiveDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new CopilotHiveDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}
