using System.Security.Claims;

using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Security;

/// <summary>
/// A REAL user directory (SQLite in-memory, the production <see cref="UserService"/> over the
/// production EF model) plus the primitives under test, so every security test exercises the exact
/// directory reads the resolver is required to use — no mocked identity source.
/// </summary>
internal sealed class HiveSecurityTestDirectory : IDisposable
{
    private readonly SqliteConnection _connection;

    internal HiveSecurityTestDirectory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var ctx = CreateDbContext();
        ctx.Database.EnsureCreated();

        Service = new UserService(Factory, NullLogger<UserService>.Instance);
    }

    internal UserService Service { get; }

    internal static CancellationToken Ct => CancellationToken.None;

    /// <summary>The production resolver over this directory, with the supplied EXPLICIT auth mode.</summary>
    internal HivePrincipalResolver Resolver(HiveAuthMode mode) =>
        new(Service, mode, NullLogger<HivePrincipalResolver>.Instance);

    /// <summary>The production resolver in authentication-enabled mode.</summary>
    internal HivePrincipalResolver AuthEnabledResolver() => Resolver(HiveAuthMode.AuthenticationEnabled);

    /// <summary>The production scoped accessor over a fresh authentication-enabled resolver.</summary>
    internal CircuitUserPrincipalAccessor Accessor(HiveAuthMode mode) =>
        new(Resolver(mode), new HivePolicy());

    internal CopilotHiveDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new CopilotHiveDbContext(options);
    }

    /// <summary>
    /// Seeds an ACTIVATED account directly (bypassing the bootstrap seam) so a test can create any
    /// number of accounts with arbitrary roles and ids, exactly as an activated invitation would.
    /// </summary>
    internal async Task<UserEntity> SeedActivatedAccountAsync(
        string gitHubId,
        UserRole role,
        bool enabled = true,
        string username = "user")
    {
        using var db = CreateDbContext();
        var entity = new UserEntity
        {
            GitHubId = gitHubId,
            Username = username,
            AccessToken = "token-" + gitHubId,
            Role = role,
            Enabled = enabled,
            CreatedAt = "2024-01-01T00:00:00.0000000Z",
        };
        db.Users.Add(entity);
        await db.SaveChangesAsync(Ct);
        return entity;
    }

    /// <summary>
    /// Seeds an account whose PERSISTED role text is not a supported role, written as raw SQL because
    /// the model's conversion refuses to write such a value. This is the legacy/foreign-data shape the
    /// resolver must refuse instead of defaulting.
    /// </summary>
    internal void SeedUnsupportedPersistedRole(string gitHubId, string roleText)
    {
        using var db = CreateDbContext();
        db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "users" (github_id, username, access_token, role, enabled, created_at)
            VALUES ({0}, {1}, {2}, {3}, 1, '2024-01-01T00:00:00.0000000Z')
            """,
            gitHubId,
            "legacy-" + gitHubId,
            "token-" + gitHubId,
            roleText);
    }

    /// <summary>Changes the persisted role of an account, as an administrator would.</summary>
    internal async Task SetRoleAsync(string gitHubId, UserRole role)
    {
        using var db = CreateDbContext();
        var entity = await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct);
        entity.Role = role;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Enables or disables an account, as an administrator would.</summary>
    internal async Task SetEnabledAsync(string gitHubId, bool enabled)
    {
        using var db = CreateDbContext();
        var entity = await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct);
        entity.Enabled = enabled;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Builds the authenticated GitHub principal a cookie would produce for one identity, optionally
    /// carrying EXTRA claims (a role claim in particular) so a test can prove they are ignored.
    /// </summary>
    internal static ClaimsPrincipal GitHubPrincipal(
        string? gitHubId,
        params Claim[] extraClaims)
    {
        var claims = new List<Claim>
        {
            // A forged/spoofed role claim: authorization truth never comes from this.
            new(ClaimTypes.Role, "Admin"),
            new("urn:github:name", "someone"),
        };

        if (gitHubId is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, gitHubId));
        }

        claims.AddRange(extraClaims);

        var identity = new ClaimsIdentity(claims, authenticationType: "Cookies");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>A signed-out (unauthenticated) principal, as an anonymous request carries.</summary>
    internal static ClaimsPrincipal AnonymousPrincipal() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], authenticationType: null));

    private IDbContextFactory<CopilotHiveDbContext> Factory => new FactoryImpl(this);

    public void Dispose() => _connection.Dispose();

    /// <summary>An <see cref="IDbContextFactory{TContext}"/> over the shared open connection.</summary>
    private sealed class FactoryImpl(HiveSecurityTestDirectory owner)
        : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() => owner.CreateDbContext();
    }
}
