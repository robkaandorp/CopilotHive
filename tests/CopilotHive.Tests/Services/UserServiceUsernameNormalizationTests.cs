using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Regression tests for the stored-username normalization contract: a GitHub login is trimmed and
/// case-folded consistently BEFORE persistence, lookup, invitation and activation, so one normalized
/// login can never belong to two activated accounts (or an account and an invitation), and the
/// original account's sign-in is anchored to its immutable GitHub id rather than its mutable login.
/// </summary>
public sealed class UserServiceUsernameNormalizationTests
{
    private sealed class SharedConnectionFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;

        public SharedConnectionFactory()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var ctx = CreateDbContext();
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_connection).Options);

        public void Dispose() => _connection.Dispose();
    }

    private sealed class CapturingLogger : ILogger<UserService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (UserService Service, SharedConnectionFactory Factory, CapturingLogger Log, FixedTimeProvider Clock) Create()
    {
        var factory = new SharedConnectionFactory();
        var log = new CapturingLogger();
        var clock = new FixedTimeProvider(new DateTimeOffset(2034, 4, 5, 6, 7, 8, TimeSpan.Zero));
        return (new UserService(factory, log, clock), factory, log, clock);
    }

    private static CancellationToken Ct => CancellationToken.None;

    private static Task<UserEntity> SignIn(
        UserService service, string githubId, string username, string token = "tok") =>
        service.CreateOrUpdateUserAsync(githubId, username, null, null, null, token, null, null, Ct);

    /// <summary>
    /// A verbatim " OctoCat " sign-in persists the TRIMMED name (not the verbatim one) and then
    /// resolves as "octocat" in any casing/whitespace.
    /// </summary>
    [Theory]
    [InlineData(" OctoCat ")]
    [InlineData("OctoCat")]
    [InlineData("\tOCTOCAT\n")]
    public async Task BootstrapSignIn_StoresTheTrimmedUsername_AndResolvesAsTheNormalizedLogin(string verbatim)
    {
        var (service, factory, _, _) = Create();
        using var _f = factory;

        var user = await SignIn(service, "1", verbatim);

        Assert.Equal(verbatim.Trim(), user.Username);
        Assert.DoesNotContain(user.Username, char.IsWhiteSpace);

        foreach (var probe in new[] { "octocat", " OCTOCAT ", "OctoCat" })
        {
            var resolved = await service.GetActivatedUserByUsernameAsync(probe, Ct);
            Assert.NotNull(resolved);
            Assert.Equal(user.Id, resolved!.Id);
        }

        // The persisted column — read raw — holds no surrounding whitespace.
        using var db = factory.CreateDbContext();
        Assert.Equal(verbatim.Trim(), (await db.Users.AsNoTracking().SingleAsync(Ct)).Username);
    }

    /// <summary>
    /// A LEGACY row stored verbatim (written by the previous code, with whitespace) still resolves as
    /// "octocat" and still conflicts — the lookup normalizes the STORED value as well.
    /// </summary>
    [Fact]
    public async Task LegacyVerbatimStoredUsername_StillResolvesAndConflictsUnderNormalization()
    {
        var (service, factory, _, _) = Create();
        using var _f = factory;

        using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new UserEntity
            {
                GitHubId = "1",
                Username = " OctoCat ",
                AccessToken = "legacy-token",
                Role = UserRole.Admin,
                Enabled = true,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
            });
            await db.SaveChangesAsync(Ct);
        }

        Assert.NotNull(await service.GetActivatedUserByUsernameAsync("octocat", Ct));
        await Assert.ThrowsAsync<UserDirectoryException>(() => service.InviteUserAsync("octocat", UserRole.Member, Ct));

        // Legacy token behaviour is untouched.
        Assert.Equal("legacy-token", await service.GetActiveAccessTokenAsync(Ct));
    }

    /// <summary>THE EXACT REVIEWER SEQUENCE: verbatim-stored account, then inviting "octocat" fails clearly.</summary>
    [Fact]
    public async Task InviteUserAsync_NormalizedDuplicateOfAVerbatimSignedInAccount_IsRefusedClearly()
    {
        var (service, factory, _, _) = Create();
        using var _f = factory;

        await SignIn(service, "1", " OctoCat ");

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.InviteUserAsync("octocat", UserRole.Member, Ct));

        Assert.Contains("activated account already exists", refusal.Message);
        Assert.Empty(await service.ListInvitationsAsync(Ct));
        Assert.Equal(1, await service.GetUserCountAsync(Ct));
    }

    /// <summary>
    /// Activation duplicate rejection under consistent normalization: an invitation that predates the
    /// account (so invite could not refuse it) can never be activated as a second account for the
    /// same normalized login, and the refused activation commits nothing.
    /// </summary>
    [Fact]
    public async Task ActivateInvitationAsync_NormalizedDuplicateOfAVerbatimStoredAccount_IsRejected()
    {
        var (service, factory, _, _) = Create();
        using var _f = factory;

        await SignIn(service, "1", "alice");
        await service.InviteUserAsync("bob", UserRole.Member, Ct);

        // A second account whose verbatim stored login normalizes to "bob" (legacy shape).
        using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new UserEntity
            {
                GitHubId = "3", Username = " BoB ", AccessToken = "t", Role = UserRole.Member,
                Enabled = true, CreatedAt = "2024-01-01T00:00:00.0000000Z",
            });
            await db.SaveChangesAsync(Ct);
        }

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ActivateInvitationAsync("bob", "2", null, null, null, Ct));

        Assert.Contains("already exists for GitHub login 'bob'", refusal.Message);
        Assert.Null(await service.GetByGitHubIdAsync("2", Ct));
        Assert.True((await service.GetInvitationByUsernameAsync("bob", Ct))!.IsPending);
    }

    /// <summary>A non-colliding original-account rename succeeds and is persisted trimmed.</summary>
    [Fact]
    public async Task OriginalAccountRename_WithoutCollision_UpdatesTheUsernameAtomically()
    {
        var (service, factory, log, _) = Create();
        using var _f = factory;

        var created = await SignIn(service, "1", "alice", "token-1");

        var outcome = await service.BootstrapOrRefreshAsync(
            "1", "  Alice-Renamed ", "New Name", null, null, "token-2", "refresh-2", null, Ct);

        Assert.False(outcome.CreatedNewAccount);
        Assert.Null(outcome.UsernameConflict);
        Assert.Equal(created.Id, outcome.User.Id);
        Assert.Equal("Alice-Renamed", outcome.User.Username);
        Assert.Equal("token-2", outcome.User.AccessToken);

        Assert.Null(await service.GetActivatedUserByUsernameAsync("alice", Ct));
        Assert.Equal(created.Id, (await service.GetActivatedUserByUsernameAsync("ALICE-RENAMED", Ct))!.Id);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>A case-only change of the original's own login is a normal rename, never a "conflict with itself".</summary>
    [Fact]
    public async Task OriginalAccountRename_CaseOnlyChangeOfItsOwnLogin_IsNotAConflict()
    {
        var (service, factory, _, _) = Create();
        using var _f = factory;

        await SignIn(service, "1", "alice");
        var outcome = await service.BootstrapOrRefreshAsync("1", "ALICE", null, null, null, "t2", null, null, Ct);

        Assert.Null(outcome.UsernameConflict);
        Assert.Equal("ALICE", outcome.User.Username);
    }

    /// <summary>
    /// THE COLLISION: bootstrap "alice" (ID 1), invite/activate "bob" (ID 2), then refresh ID 1 as
    /// "BOB". The sign-in and token refresh SUCCEED, the original's username is PRESERVED, every other
    /// field is refreshed, the conflict is reported on the outcome and in a warning log, the invited
    /// account stays resolvable, and no second account for "bob" appears.
    /// </summary>
    [Fact]
    public async Task OriginalAccountRename_CollidingWithAnActivatedAccount_PreservesUsernameRefreshesRestAndReports()
    {
        var (service, factory, log, clock) = Create();
        using var _f = factory;

        var original = await SignIn(service, "1", "alice", "token-1");
        await service.InviteUserAsync("bob", UserRole.Operator, Ct);
        var bob = await service.ActivateInvitationAsync("bob", "2", null, null, null, Ct);
        log.Entries.Clear();

        clock.Now = new DateTimeOffset(2035, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var signalled = 0;
        service.TokenAvailable += () => signalled++;

        var outcome = await service.BootstrapOrRefreshAsync(
            "1", "BOB", "Display", "https://a/avatar", "e@x", "token-2", "refresh-2", "2040-01-01T00:00:00.0000000Z", Ct);

        // Never rejected, never thrown: the sign-in anchored to the immutable GitHub id succeeded.
        Assert.False(outcome.CreatedNewAccount);
        Assert.Equal(original.Id, outcome.User.Id);

        // Existing unique username preserved; everything else refreshed.
        Assert.Equal("alice", outcome.User.Username);
        Assert.Equal("Display", outcome.User.DisplayName);
        Assert.Equal("https://a/avatar", outcome.User.AvatarUrl);
        Assert.Equal("e@x", outcome.User.Email);
        Assert.Equal("token-2", outcome.User.AccessToken);
        Assert.Equal("refresh-2", outcome.User.RefreshToken);
        Assert.Equal("2040-01-01T00:00:00.0000000Z", outcome.User.TokenExpiresAt);
        Assert.Equal("2035-01-02T03:04:05.0000000Z", outcome.User.LastLoginAt);
        Assert.Equal(1, signalled);

        // Reported on the outcome...
        var conflict = Assert.IsType<UsernameConflict>(outcome.UsernameConflict);
        Assert.Equal("BOB", conflict.RequestedUsername);
        Assert.Equal(bob.Id, conflict.ConflictingUserId);
        Assert.Null(conflict.ConflictingInvitationId);

        // ...and in the log.
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("BOB", warning.Message);
        Assert.Contains("alice", warning.Message);

        // Persisted state: the token really refreshed, the invited account is still resolvable and
        // is NOT hidden by the lower-id original, and the legacy token selector follows the original.
        var stored = await service.GetByGitHubIdAsync("1", Ct);
        Assert.Equal("alice", stored!.Username);
        Assert.Equal("token-2", stored.AccessToken);
        Assert.Equal(bob.Id, (await service.GetActivatedUserByUsernameAsync("bob", Ct))!.Id);
        Assert.Equal(original.Id, (await service.GetActivatedUserByUsernameAsync("alice", Ct))!.Id);
        Assert.Equal(2, await service.GetUserCountAsync(Ct));
        Assert.Equal("token-2", await service.GetActiveAccessTokenAsync(Ct));

        // The public seam behaves identically (returns the account, never throws).
        var viaPublic = await service.CreateOrUpdateUserAsync("1", "bob", null, null, null, "token-3", null, null, Ct);
        Assert.Equal("alice", viaPublic.Username);
        Assert.Equal("token-3", viaPublic.AccessToken);
    }

    /// <summary>
    /// RENAME-VS-INVITATION: the original renaming to a login another invitation reserves (pending or
    /// revoked) preserves its username, reports the invitation, consumes nothing, and leaves the
    /// invitation activatable later only if the login is actually free.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalAccountRename_CollidingWithAnInvitation_PreservesUsernameAndLeavesTheInvitationUntouched(bool revoked)
    {
        var (service, factory, log, _) = Create();
        using var _f = factory;

        await SignIn(service, "1", "alice");
        var invitation = await service.InviteUserAsync("carol", UserRole.Admin, Ct);
        if (revoked)
        {
            invitation = await service.RevokeInvitationAsync("carol", Ct);
        }

        var outcome = await service.BootstrapOrRefreshAsync("1", " Carol ", null, null, null, "t2", null, null, Ct);

        Assert.Equal("alice", outcome.User.Username);
        Assert.Equal("t2", outcome.User.AccessToken);
        var conflict = Assert.IsType<UsernameConflict>(outcome.UsernameConflict);
        Assert.Equal(invitation.Id, conflict.ConflictingInvitationId);
        Assert.Null(conflict.ConflictingUserId);
        Assert.Null(outcome.ConsumedInvitation);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);

        var after = (await service.GetInvitationByUsernameAsync("carol", Ct))!;
        Assert.Equal(invitation, after);
        Assert.Null(after.ActivatedUserId);
        Assert.Equal(1, await service.GetUserCountAsync(Ct));
    }

    /// <summary>
    /// The original's OWN consumed invitation is history, not a competing claim: re-signing in under
    /// the invited login after matching-bootstrap consumption is not a conflict.
    /// </summary>
    [Fact]
    public async Task OriginalAccountRename_BackToItsOwnConsumedInvitationLogin_IsNotAConflict()
    {
        var (service, factory, _, _) = Create();
        using var _f = factory;

        await service.InviteUserAsync("octocat", UserRole.Member, Ct);
        await SignIn(service, "1", "octocat");     // consumes the invitation into account 1
        await SignIn(service, "1", "renamed");      // free rename away
        var outcome = await service.BootstrapOrRefreshAsync("1", "OctoCat", null, null, null, "t", null, null, Ct);

        Assert.Null(outcome.UsernameConflict);
        Assert.Equal("OctoCat", outcome.User.Username);
    }
}
