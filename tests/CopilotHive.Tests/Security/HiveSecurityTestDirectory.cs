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
/// <para>
/// ONE NAMED SHARED-CACHE DATABASE, A KEEPER CONNECTION, AND A FRESH CONNECTION PER CONTEXT. Each
/// directory instance creates its own uniquely named in-memory database
/// (<c>hive-security-users-{guid}</c>) and holds a KEEPER connection open for its whole life, so the
/// data survives between contexts while every <see cref="CopilotHiveDbContext"/> opens its OWN
/// connection to that same named database.
/// </para>
/// <para>
/// WHY NOT ONE SHARED <see cref="SqliteConnection"/> (the previous shape): EF's SQLite provider
/// registers its scalar functions on the connection while it initialises it
/// (<c>SqliteRelationalConnection.InitializeDbConnection</c> → <c>SqliteConnection.CreateFunction</c>),
/// so two contexts created CONCURRENTLY over one connection race inside SQLite and one of them fails
/// with <c>SQLite Error 5: 'database is locked'</c>. One connection per context removes the shared
/// initialization: every context initialises its own connection, and only the DATA is shared — the
/// shape <c>ComposerTestUserDirectory</c> already proved.
/// </para>
/// <para>
/// DISPOSAL IS A LATCH, NOT A RESET: <see cref="Dispose"/> is idempotent and closes the keeper, and
/// <see cref="CreateDbContext"/> REJECTS with <see cref="ObjectDisposedException"/> once disposed. It
/// must reject rather than silently reopen, because with a NAMED shared-cache database a fresh
/// connection would quietly create a new, EMPTY database — a directory that answers "no such account"
/// instead of "the directory is unreadable". The rejection is what keeps the
/// unreadable-directory contract honest: the production resolver's broad non-cancellation catch
/// classifies it as <see cref="PrincipalRefusalReason.DirectoryFailure"/>.
/// </para>
/// </summary>
internal sealed class HiveSecurityTestDirectory : IDisposable
{
    /// <summary>The unique shared-cache in-memory database name; one per directory instance.</summary>
    private readonly string _databaseName = $"hive-security-users-{Guid.NewGuid():N}";

    /// <summary>
    /// Holds the shared-cache database alive for the directory's whole life. Every context opens its
    /// own connection to the same named database; this one exists purely so the database is not
    /// dropped between them.
    /// </summary>
    private readonly SqliteConnection _keeper;

    /// <summary>
    /// The disposal latch: 0 while the directory is live, 1 once <see cref="Dispose"/> ran. One
    /// exchange makes disposal idempotent (a repeated or concurrent dispose closes the keeper at most
    /// once) and the volatile read makes the post-disposal rejection visible to every thread.
    /// </summary>
    private int _disposed;

    internal HiveSecurityTestDirectory()
    {
        _keeper = new SqliteConnection(ConnectionString);
        _keeper.Open();

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

    /// <summary>
    /// The connection string shared by the keeper and every context: the SAME named shared-cache
    /// in-memory database, with <c>Pooling=False</c> so no pooled handle silently outlives the
    /// connection that opened it (which would keep the database alive past the keeper).
    /// </summary>
    private string ConnectionString =>
        $"Data Source={_databaseName};Mode=Memory;Cache=Shared;Pooling=False";

    /// <summary>
    /// A FRESH context over its OWN connection to the shared-cache database. The connection is owned
    /// by the context (so it closes with it) and is never shared with another context — which is what
    /// lets contexts initialise concurrently.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The directory has been disposed.</exception>
    internal CopilotHiveDbContext CreateDbContext()
    {
        // REJECT, never silently reopen: a fresh connection to a named shared-cache database would
        // create a new EMPTY database, so a post-disposal read would answer "no such account"
        // instead of "the directory is unreadable".
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(ConnectionString)
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

    /// <summary>
    /// Idempotent: the FIRST dispose closes the keeper (dropping the database only once every other
    /// context connection is closed too) and latches the directory closed; later calls do nothing.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _keeper.Dispose();
    }

    /// <summary>An <see cref="IDbContextFactory{TContext}"/> handing out ONE FRESH CONNECTION per context.</summary>
    private sealed class FactoryImpl(HiveSecurityTestDirectory owner)
        : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() => owner.CreateDbContext();
    }
}
