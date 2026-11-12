using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// A REAL user directory for the Composer circuit tests: the production <see cref="UserService"/> over the
/// production EF model, so every resolution reads the same rows a production install would.
/// <para>
/// WHY THIS EXISTS INSTEAD OF THE SHARED <c>HiveSecurityTestDirectory</c>: that harness hands EVERY
/// <see cref="CopilotHiveDbContext"/> the SAME <see cref="SqliteConnection"/> instance. EF's SQLite provider
/// registers its scalar functions on the connection while it initialises it
/// (<c>SqliteRelationalConnection.InitializeDbConnection</c> → <c>SqliteConnection.CreateFunction</c>), so two
/// contexts created CONCURRENTLY on one connection race inside SQLite and one of them fails with
/// <c>SQLite Error 5: 'database is locked'</c>. The Composer runtime manager reads the directory from a
/// background task while the test thread reads it too, so the race is reachable — and it surfaced as a
/// non-deterministic <c>Failed</c> status in the replacement/deferred rows.
/// </para>
/// <para>
/// THE FIX IS THE SHAPE, NOT A SLEEP: ONE named in-memory database with a KEEPER connection held open for the
/// directory's whole life, and EVERY context opening its OWN connection to that same shared-cache database.
/// Each context therefore initialises its own connection (no concurrent <c>CreateFunction</c> on one
/// connection), while the data stays genuinely in-memory and is dropped when the keeper closes.
/// </para>
/// </summary>
internal sealed class ComposerTestUserDirectory : IDisposable
{
    /// <summary>The unique shared-cache in-memory database name; one per directory instance.</summary>
    private readonly string _databaseName = $"composer-test-users-{Guid.NewGuid():N}";

    /// <summary>Holds the shared-cache database alive for the directory's whole life.</summary>
    private readonly SqliteConnection _keeper;

    private bool _disposed;

    internal ComposerTestUserDirectory()
    {
        _keeper = new SqliteConnection($"Data Source={_databaseName};Mode=Memory;Cache=Shared");
        _keeper.Open();

        using var ctx = CreateDbContext();
        ctx.Database.EnsureCreated();

        Service = new UserService(Factory, NullLogger<UserService>.Instance);
    }

    /// <summary>The production user directory backing every resolution in this directory.</summary>
    internal UserService Service { get; }

    /// <summary>
    /// A FRESH context over its OWN connection to the shared-cache database. The connection is owned by the
    /// context (so it closes with it) and is never shared with another context.
    /// </summary>
    internal CopilotHiveDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite($"Data Source={_databaseName};Mode=Memory;Cache=Shared")
            .Options;
        return new CopilotHiveDbContext(options);
    }

    /// <summary>Seeds one ACTIVATED account directly and returns its persisted entity.</summary>
    internal async Task<UserEntity> SeedActivatedAccountAsync(
        string gitHubId, UserRole role, bool enabled = true, string username = "user")
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
        await db.SaveChangesAsync();
        return entity;
    }

    /// <summary>Changes the persisted role of an account, as an administrator would.</summary>
    internal async Task SetRoleAsync(string gitHubId, UserRole role)
    {
        using var db = CreateDbContext();
        var entity = await db.Users.SingleAsync(u => u.GitHubId == gitHubId);
        entity.Role = role;
        await db.SaveChangesAsync();
    }

    /// <summary>Enables or disables the account, as an administrator would.</summary>
    internal async Task SetEnabledAsync(string gitHubId, bool enabled)
    {
        using var db = CreateDbContext();
        var entity = await db.Users.SingleAsync(u => u.GitHubId == gitHubId);
        entity.Enabled = enabled;
        await db.SaveChangesAsync();
    }

    private IDbContextFactory<CopilotHiveDbContext> Factory => new FactoryImpl(this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The keeper holds the shared-cache database alive; closing it drops the database with it.
        _keeper.Dispose();
    }

    /// <summary>An <see cref="IDbContextFactory{TContext}"/> handing out ONE FRESH CONNECTION per context.</summary>
    private sealed class FactoryImpl(ComposerTestUserDirectory owner) : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() => owner.CreateDbContext();
    }
}
