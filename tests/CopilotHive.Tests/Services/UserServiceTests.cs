using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using CopilotHive.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Unit tests for <see cref="UserService"/>: the OAuth bootstrap seam, lookup/count/token retrieval,
/// and the trusted internal directory operations (activated accounts and the separate invitation
/// table) against an in-memory SQLite database.
/// </summary>
public sealed class UserServiceTests
{
    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> that creates fresh contexts over a single
    /// shared, open SQLite in-memory connection. Disposing a created context does not destroy
    /// the database because the underlying connection stays open for the lifetime of the factory.
    /// </summary>
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

        public CopilotHiveDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new CopilotHiveDbContext(options);
        }

        public void Dispose() => _connection.Dispose();
    }

    private static (UserService Service, SharedConnectionFactory Factory) CreateService(TimeProvider? timeProvider = null)
    {
        var factory = new SharedConnectionFactory();
        return (new UserService(factory, NullLogger<UserService>.Instance, timeProvider), factory);
    }

    /// <summary>Bootstraps the original account (the ONLY identity <c>CreateOrUpdateUserAsync</c> allows).</summary>
    private static Task<UserEntity> BootstrapAsync(
        UserService service,
        string githubId = "12345",
        string username = "octocat",
        string token = "token-abc") =>
        service.CreateOrUpdateUserAsync(
            githubId, username, "The Octocat", "https://avatar/octocat.png",
            "octocat@example.com", token, "refresh-xyz", "2030-01-01T00:00:00.0000000Z",
            CancellationToken.None);

    private static CancellationToken Ct => CancellationToken.None;

    // ── Bootstrap seam ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateOrUpdateUserAsync_CreatesBootstrapAdminAccount()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var user = await BootstrapAsync(service);

        Assert.True(user.Id > 0);
        Assert.Equal("12345", user.GitHubId);
        Assert.Equal("octocat", user.Username);
        Assert.Equal("The Octocat", user.DisplayName);
        Assert.Equal("https://avatar/octocat.png", user.AvatarUrl);
        Assert.Equal("octocat@example.com", user.Email);
        Assert.Equal("token-abc", user.AccessToken);
        Assert.Equal("refresh-xyz", user.RefreshToken);
        Assert.Equal("2030-01-01T00:00:00.0000000Z", user.TokenExpiresAt);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.True(user.Enabled);
        Assert.False(string.IsNullOrEmpty(user.CreatedAt));
        Assert.False(string.IsNullOrEmpty(user.LastLoginAt));
    }

    [Fact]
    public async Task CreateOrUpdateUserAsync_RefreshesTheOriginalAccount()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var created = await BootstrapAsync(service);
        var originalCreatedAt = created.CreatedAt;

        var updated = await service.CreateOrUpdateUserAsync(
            "12345", "octocat-renamed", "Renamed", "https://avatar/new.png",
            "new@example.com", "token-new", "refresh-new", null, Ct);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("octocat-renamed", updated.Username);
        Assert.Equal("Renamed", updated.DisplayName);
        Assert.Equal("token-new", updated.AccessToken);
        Assert.Equal("refresh-new", updated.RefreshToken);
        Assert.Equal(originalCreatedAt, updated.CreatedAt);
        Assert.Equal(1, await service.GetUserCountAsync(Ct));
    }

    /// <summary>
    /// THE LOSER IS REJECTED, NEVER INSERTED: a SECOND distinct identity must not become an account
    /// through the sign-in seam. This replaces the old "create two unrelated accounts" misuse.
    /// </summary>
    [Fact]
    public async Task CreateOrUpdateUserAsync_SecondDistinctIdentity_IsRejectedAndNotInserted()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.CreateOrUpdateUserAsync(
                "99999", "intruder", null, null, null, "other-token", null, null, Ct));

        Assert.Contains("Only the original activated account may sign in", refusal.Message);
        Assert.Equal(1, await service.GetUserCountAsync(Ct));

        // The original account is untouched — in particular its stored credential is unchanged.
        var stored = await service.GetAdminUserAsync(Ct);
        Assert.NotNull(stored);
        Assert.Equal(bootstrap.Id, stored!.Id);
        Assert.Equal("12345", stored.GitHubId);
        Assert.Equal("token-abc", stored.AccessToken);
        Assert.Null(await service.GetByGitHubIdAsync("99999", Ct));
    }

    /// <summary>A rejected second identity commits NOTHING — not even a matching invitation.</summary>
    [Fact]
    public async Task CreateOrUpdateUserAsync_SecondIdentityWithMatchingInvitation_IsRejectedAndConsumesNothing()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service);
        var invitation = await service.InviteUserAsync("intruder", UserRole.Admin, Ct);

        await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.CreateOrUpdateUserAsync(
                "99999", "intruder", null, null, null, "other-token", null, null, Ct));

        var unchanged = await service.GetInvitationByUsernameAsync("intruder", Ct);
        Assert.NotNull(unchanged);
        Assert.Equal(invitation.Id, unchanged!.Id);
        Assert.True(unchanged.IsPending);
        Assert.Null(unchanged.ActivatedUserId);
    }

    // ── Lookups, count, token ────────────────────────────────────────────────

    [Fact]
    public async Task GetByGitHubIdAsync_ReturnsUser_WhenExists()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service, githubId: "777", username: "user777");

        var found = await service.GetByGitHubIdAsync("777", Ct);

        Assert.NotNull(found);
        Assert.Equal("user777", found!.Username);
    }

    [Fact]
    public async Task GetByGitHubIdAsync_ReturnsNull_WhenNotExists()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        Assert.Null(await service.GetByGitHubIdAsync("does-not-exist", Ct));
    }

    [Fact]
    public async Task GetAdminUserAsync_ReturnsTheOriginalLowestIdAccount()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service, githubId: "100", username: "first");
        await service.InviteUserAsync("second", UserRole.Admin, Ct);
        var activated = await service.ActivateInvitationAsync(
            "second", "200", null, null, null, Ct);

        var admin = await service.GetAdminUserAsync(Ct);

        Assert.NotNull(admin);
        Assert.Equal(bootstrap.Id, admin!.Id);
        Assert.Equal("first", admin.Username);
        // The invited Admin has a HIGHER id, so it can never win the original-account selector.
        Assert.True(activated.Id > bootstrap.Id);
    }

    [Fact]
    public async Task GetAdminUserAsync_ReturnsNull_WhenNoUsers()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        Assert.Null(await service.GetAdminUserAsync(Ct));
    }

    [Fact]
    public async Task GetUserCountAsync_ReturnsZero_WhenEmpty()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        Assert.Equal(0, await service.GetUserCountAsync(Ct));
    }

    /// <summary>
    /// The count is ACTIVATED accounts only: invitations of every role — including Admin — never
    /// count, so the bootstrap gate can never be satisfied by invitations alone.
    /// </summary>
    [Fact]
    public async Task GetUserCountAsync_CountsActivatedUsersOnly_NotInvitations()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        foreach (var role in new[] { UserRole.Viewer, UserRole.Member, UserRole.Operator, UserRole.Admin })
        {
            await service.InviteUserAsync($"invited-{role}", role, Ct);
        }

        Assert.Equal(0, await service.GetUserCountAsync(Ct));

        await BootstrapAsync(service);
        Assert.Equal(1, await service.GetUserCountAsync(Ct));

        await service.ActivateInvitationAsync("invited-member", "2", null, null, null, Ct);
        Assert.Equal(2, await service.GetUserCountAsync(Ct));
    }

    [Fact]
    public async Task GetActiveAccessTokenAsync_ReturnsToken_WhenUserExists()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service, githubId: "55", username: "tokenuser", token: "active-token");

        Assert.Equal("active-token", await service.GetActiveAccessTokenAsync(Ct));
    }

    [Fact]
    public async Task GetActiveAccessTokenAsync_ReturnsNull_WhenNoUsers()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        Assert.Null(await service.GetActiveAccessTokenAsync(Ct));
    }

    /// <summary>
    /// DISABLING/DEMOTING THE ORIGINAL ACCOUNT NEVER RE-ROUTES ITS LEGACY TOKEN: the lowest-id
    /// account keeps supplying the global token, no fallback identity is admitted, and the invited
    /// Admin's credential — which does not exist at all — can never be substituted.
    /// </summary>
    [Fact]
    public async Task GetActiveAccessTokenAsync_OriginalAccountDisabledOrDemoted_StillReturnsItsToken()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service, githubId: "1", username: "original", token: "original-token");

        // A second enabled Admin exists, so the last-admin protection permits the change.
        await service.InviteUserAsync("second", UserRole.Admin, Ct);
        var second = await service.ActivateInvitationAsync("second", "2", null, null, null, Ct);

        await service.ChangeUserRoleAsync(bootstrap.Id, UserRole.Viewer, Ct);
        Assert.Equal("original-token", await service.GetActiveAccessTokenAsync(Ct));

        await service.SetUserEnabledAsync(bootstrap.Id, enabled: false, Ct);
        Assert.Equal("original-token", await service.GetActiveAccessTokenAsync(Ct));

        // The invited Admin never becomes the token account even though it is the only enabled Admin.
        Assert.NotEqual(second.Id, (await service.GetAdminUserAsync(Ct))!.Id);
        Assert.Null((await service.GetActivatedUserByIdAsync(second.Id, Ct))!.LastLoginAt);
    }

    /// <summary>
    /// AN INVITED ADMIN NEVER BECOMES THE GLOBAL-TOKEN FALLBACK. The seeded row models an
    /// invited administrator that was activated and later received a credential of its own: a
    /// HIGHER internal id, the Admin role, enabled, and a DIFFERENT access token. The legacy
    /// selector is the LOWEST-id activated account, so the ORIGINAL account's token is returned —
    /// an invited Admin can never take over the global token, no matter how many Admins exist.
    /// </summary>
    [Fact]
    public async Task GetActiveAccessTokenAsync_InvitedAdminWithHigherIdAndOwnToken_IsNeverTheFallback()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service, githubId: "1", username: "original", token: "original-token");

        using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new UserEntity
            {
                GitHubId = "2",
                Username = "invitedadmin",
                AccessToken = "invited-admin-token",
                Role = UserRole.Admin,
                Enabled = true,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
            });
            await db.SaveChangesAsync(Ct);
        }

        // The ORIGINAL account's token is the global token — never the invited Admin's.
        Assert.Equal("original-token", await service.GetActiveAccessTokenAsync(Ct));

        // ...and the original-account selector still names the original account.
        var original = await service.GetAdminUserAsync(Ct);
        Assert.NotNull(original);
        Assert.Equal(bootstrap.Id, original!.Id);
        Assert.Equal("original", original.Username);
        Assert.Equal("1", original.GitHubId);
    }

    /// <summary>
    /// THE TOKEN SELECTOR NEVER FALLS BACK TO AN INVITED ACCOUNT — not even when the ORIGINAL
    /// account holds no usable token. The original account is the only credential source: an invited
    /// Admin that owns a token of its own must never be substituted, because that would re-route the
    /// legacy global token to a different identity.
    /// <para>
    /// The ORIGINAL account's token is EMPTY here, so the selector has nothing to return from it. A
    /// selector that grew a fallback branch would return the invited Admin's token here — which is
    /// exactly what this assertion forbids.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetActiveAccessTokenAsync_OriginalAccountWithoutAToken_DoesNotFallBackToAnInvitedAdmin()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        // The ORIGINAL account exists but holds NO token — the case in which a fallback could be
        // introduced.
        await BootstrapAsync(service, githubId: "1", username: "original", token: string.Empty);

        // A HIGHER-id invited Admin that owns a real credential.
        using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new UserEntity
            {
                GitHubId = "2",
                Username = "invitedadmin",
                AccessToken = "invited-admin-token",
                Role = UserRole.Admin,
                Enabled = true,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
            });
            await db.SaveChangesAsync(Ct);
        }

        // The invited Admin's token is NEVER returned.
        var token = await service.GetActiveAccessTokenAsync(Ct);
        Assert.NotEqual("invited-admin-token", token);

        // The selector returned the ORIGINAL account's own (empty) credential — nothing else.
        Assert.Equal(string.Empty, token);
    }

    /// <summary>
    /// A WHITESPACE-ONLY original token is likewise never replaced by an invited account's
    /// credential: the selector returns the original account's own value, not somebody else's.
    /// </summary>
    [Fact]
    public async Task GetActiveAccessTokenAsync_OriginalAccountWithWhitespaceToken_IsNotReplacedByAnInvitedAdmin()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service, githubId: "1", username: "original", token: "   ");

        using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new UserEntity
            {
                GitHubId = "2",
                Username = "invitedadmin",
                AccessToken = "invited-admin-token",
                Role = UserRole.Admin,
                Enabled = true,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
            });
            await db.SaveChangesAsync(Ct);
        }

        var token = await service.GetActiveAccessTokenAsync(Ct);

        Assert.NotEqual("invited-admin-token", token);
        Assert.Equal("   ", token);
    }

    /// <summary>
    /// THE TOKEN SELECTOR NEVER CONSULTS THE INVITATION DIRECTORY AT ALL. The invitation table is a
    /// different table with no credentials in it, so a selector that never mentions it cannot
    /// possibly return an invited account's token. This is the structural form of the same
    /// invariant, and it fails the moment the selector grows an invitation branch.
    /// </summary>
    [Fact]
    public void GetActiveAccessTokenAsync_SelectorNeverConsultsTheInvitationDirectory()
    {
        var body = TestSourceScanner.ExtractMethodBlockPreservingLiterals(
            ReadUserServiceSource(),
            "public async Task<string?> GetActiveAccessTokenAsync");

        Assert.NotNull(body);
        Assert.Contains("GetAdminUserAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Invitation", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UserInvitations", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Role == UserRole.Admin", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE ORIGINAL-ACCOUNT SELECTOR IS THE LOWEST-ID ACCOUNT, regardless of role or enabled state:
    /// it never consults the invitation directory either.
    /// </summary>
    [Fact]
    public void GetAdminUserAsync_SelectorNeverConsultsTheInvitationDirectory()
    {
        var body = TestSourceScanner.ExtractMethodBlockPreservingLiterals(
            ReadUserServiceSource(),
            "public async Task<UserEntity?> GetAdminUserAsync");

        Assert.NotNull(body);
        Assert.Contains("OrderBy(u => u.Id)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Invitation", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads <c>UserService.cs</c> from the repository's production source tree.</summary>
    private static string ReadUserServiceSource() =>
        File.ReadAllText(
                Path.Combine(TestRepoRoot.Find(), "src", "CopilotHive", "Services", "UserService.cs"))
            .ReplaceLineEndings("\n");

    // ── Secret-free listings ─────────────────────────────────────────────────

    [Fact]
    public async Task ListActivatedUsersAsync_IsSecretFreeAndOrderedByInternalId()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service, token: "gho_secret_token");
        await service.InviteUserAsync("second", UserRole.Operator, Ct);
        await service.ActivateInvitationAsync("second", "2", "Second Display", null, "second@example.com", Ct);

        var listed = await service.ListActivatedUsersAsync(Ct);

        Assert.Equal(2, listed.Count);
        Assert.Equal([bootstrap.Id, listed[1].Id], listed.Select(u => u.Id));
        Assert.Equal(UserRole.Admin, listed[0].Role);
        Assert.Equal(UserRole.Operator, listed[1].Role);
        Assert.All(listed, u => Assert.True(u.Enabled));

        // The projection type carries NO credential: neither the secret nor its column can appear.
        var rendered = string.Join('|', listed.Select(u => $"{u.Id}:{u.GitHubId}:{u.Username}:{u.Role}:{u.Enabled}"));
        Assert.DoesNotContain("gho_secret_token", rendered);
        Assert.DoesNotContain("refresh-xyz", rendered);
        Assert.DoesNotContain(
            "Token",
            string.Join(',', typeof(ActivatedUserSummary).GetProperties().Select(p => p.Name)));
    }

    [Fact]
    public async Task ListInvitationsAsync_IsSecretFreeForEveryRoleAndKeepsRevokedRows()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        foreach (var role in new[] { UserRole.Viewer, UserRole.Member, UserRole.Operator, UserRole.Admin })
        {
            await service.InviteUserAsync($"invited-{role}", role, Ct);
        }

        await service.RevokeInvitationAsync("invited-member", Ct);

        var listed = await service.ListInvitationsAsync(Ct);

        Assert.Equal(4, listed.Count);
        Assert.Equal(
            ["invited-admin", "invited-member", "invited-operator", "invited-viewer"],
            listed.Select(i => i.NormalizedUsername).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(
            [UserRole.Admin, UserRole.Member, UserRole.Operator, UserRole.Viewer],
            listed.OrderBy(i => i.NormalizedUsername, StringComparer.Ordinal).Select(i => i.Role));

        // A revoked invitation is retained (never deleted) and is not pending.
        var revoked = Assert.Single(listed, i => !i.Enabled);
        Assert.Equal("invited-member", revoked.NormalizedUsername);
        Assert.NotNull(revoked.RevokedAt);
        Assert.False(revoked.IsPending);
        Assert.Null(revoked.ActivatedUserId);

        // The invitation projection type has no credential-bearing property at all.
        Assert.DoesNotContain(
            "Token",
            string.Join(',', typeof(UserInvitationSummary).GetProperties().Select(p => p.Name)));
    }

    [Fact]
    public async Task ListInvitationsAsync_EmptyDatabase_ReturnsEmpty()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        Assert.Empty(await service.ListInvitationsAsync(Ct));
        Assert.Empty(await service.ListActivatedUsersAsync(Ct));
    }

    // ── Resolvers ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveByInternalIdAndGitHubIdentity_ReturnsTheSameAccount()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service, githubId: "4242", username: "OctoCat");

        var byId = await service.GetActivatedUserByIdAsync(bootstrap.Id, Ct);
        var byUsername = await service.GetActivatedUserByUsernameAsync("octocat", Ct);
        var byMixedCase = await service.GetActivatedUserByUsernameAsync("  OCTOCAT ", Ct);

        Assert.NotNull(byId);
        Assert.NotNull(byUsername);
        Assert.NotNull(byMixedCase);
        Assert.Equal(byId, byUsername);
        Assert.Equal(byId, byMixedCase);
    }

    [Fact]
    public async Task ResolveByInternalIdOrGitHubIdentity_Unknown_ReturnsNull()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service);

        Assert.Null(await service.GetActivatedUserByIdAsync(9999, Ct));
        Assert.Null(await service.GetActivatedUserByUsernameAsync("nobody", Ct));
        Assert.Null(await service.GetInvitationByUsernameAsync("nobody", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetActivatedUserByUsernameAsync("  ", Ct));
    }

    // ── Invitation directory: create / role / revoke / enable ────────────────

    [Fact]
    public async Task InviteUserAsync_NormalizesUsernameConsistently()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var invitation = await service.InviteUserAsync("  OctoCat  ", UserRole.Member, Ct);

        Assert.Equal("octocat", invitation.NormalizedUsername);
        Assert.Equal(UserRole.Member, invitation.Role);
        Assert.True(invitation.Enabled);
        Assert.True(invitation.IsPending);
        Assert.False(string.IsNullOrEmpty(invitation.CreatedAt));
        Assert.Equal(invitation.CreatedAt, invitation.UpdatedAt);
        Assert.Null(invitation.RevokedAt);
        Assert.Null(invitation.ActivatedUserId);
        Assert.Null(invitation.ActivatedAt);

        // Lookup by any casing resolves the SAME row.
        var resolved = await service.GetInvitationByUsernameAsync("OCTOCAT", Ct);
        Assert.NotNull(resolved);
        Assert.Equal(invitation.Id, resolved!.Id);
    }

    [Fact]
    public async Task InviteUserAsync_Duplicate_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var first = await service.InviteUserAsync("octocat", UserRole.Member, Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.InviteUserAsync("OCTOCAT", UserRole.Admin, Ct));

        Assert.Contains("already exists", refusal.Message);
        var listed = Assert.Single(await service.ListInvitationsAsync(Ct));
        Assert.Equal(first.Id, listed.Id);
        Assert.Equal(UserRole.Member, listed.Role);
    }

    [Fact]
    public async Task InviteUserAsync_RevokedDuplicate_IsStillRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.InviteUserAsync("octocat", UserRole.Member, Ct);
        await service.RevokeInvitationAsync("octocat", Ct);

        // A revoked invitation is re-enabled, never duplicated.
        await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.InviteUserAsync("octocat", UserRole.Operator, Ct));
        Assert.Single(await service.ListInvitationsAsync(Ct));
    }

    [Fact]
    public async Task InviteUserAsync_AlreadyActivatedAccount_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service, username: "octocat");

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.InviteUserAsync("octocat", UserRole.Admin, Ct));

        Assert.Contains("activated account already exists", refusal.Message);
        Assert.Empty(await service.ListInvitationsAsync(Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task InviteUserAsync_BlankUsername_IsRejected(string? username)
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.InviteUserAsync(username!, UserRole.Member, Ct));
    }

    [Fact]
    public async Task InviteUserAsync_UndefinedRole_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.InviteUserAsync("octocat", (UserRole)99, Ct));
        Assert.Empty(await service.ListInvitationsAsync(Ct));
    }

    [Fact]
    public async Task ChangeInvitationRoleAsync_UpdatesAPendingInvitation()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var invitation = await service.InviteUserAsync("octocat", UserRole.Viewer, Ct);

        var changed = await service.ChangeInvitationRoleAsync("OCTOCAT", UserRole.Operator, Ct);

        Assert.Equal(invitation.Id, changed.Id);
        Assert.Equal(UserRole.Operator, changed.Role);
        Assert.Equal(invitation.CreatedAt, changed.CreatedAt);
        Assert.NotEqual(invitation.UpdatedAt, changed.UpdatedAt);
    }

    [Fact]
    public async Task ChangeInvitationRoleAsync_UnknownInvitation_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ChangeInvitationRoleAsync("nobody", UserRole.Admin, Ct));

        Assert.Contains("No invitation exists", refusal.Message);
    }

    [Fact]
    public async Task ChangeInvitationRoleAsync_SameRole_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.InviteUserAsync("octocat", UserRole.Member, Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ChangeInvitationRoleAsync("octocat", UserRole.Member, Ct));

        Assert.Contains("already carries role 'member'", refusal.Message);
    }

    [Fact]
    public async Task RevokeThenEnableInvitationAsync_RestoresPendingState()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.InviteUserAsync("octocat", UserRole.Member, Ct);

        var revoked = await service.RevokeInvitationAsync("octocat", Ct);
        Assert.False(revoked.Enabled);
        Assert.NotNull(revoked.RevokedAt);
        Assert.False(revoked.IsPending);

        var enabled = await service.EnableInvitationAsync("octocat", Ct);
        Assert.True(enabled.Enabled);
        Assert.Null(enabled.RevokedAt);
        Assert.True(enabled.IsPending);
        Assert.Equal(revoked.Id, enabled.Id);
        Assert.Equal(UserRole.Member, enabled.Role);
    }

    [Fact]
    public async Task RevokeInvitationAsync_Twice_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.InviteUserAsync("octocat", UserRole.Member, Ct);
        await service.RevokeInvitationAsync("octocat", Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.RevokeInvitationAsync("octocat", Ct));

        Assert.Contains("already revoked", refusal.Message);
    }

    [Fact]
    public async Task EnableInvitationAsync_AlreadyEnabled_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.InviteUserAsync("octocat", UserRole.Member, Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.EnableInvitationAsync("octocat", Ct));

        Assert.Contains("already enabled", refusal.Message);
    }

    [Fact]
    public async Task RevokeOrEnableActivatedInvitation_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service);
        await service.InviteUserAsync("second", UserRole.Member, Ct);
        await service.ActivateInvitationAsync("second", "2", null, null, null, Ct);

        await Assert.ThrowsAsync<UserDirectoryException>(() => service.RevokeInvitationAsync("second", Ct));
        await Assert.ThrowsAsync<UserDirectoryException>(() => service.EnableInvitationAsync("second", Ct));
        await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ChangeInvitationRoleAsync("second", UserRole.Admin, Ct));
    }

    // ── Activation ───────────────────────────────────────────────────────────

    /// <summary>
    /// ACTIVATION BEFORE BOOTSTRAP FAILS CLEARLY. A fresh installation may hold invitations, but the
    /// original admin must sign in first — an invited account can never become the first account.
    /// </summary>
    [Fact]
    public async Task ActivateInvitationAsync_BeforeBootstrap_FailsClearlyAndCreatesNothing()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.InviteUserAsync("invited", UserRole.Admin, Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ActivateInvitationAsync("invited", "777", null, null, null, Ct));

        Assert.Equal(UserService.ActivationRequiresBootstrapMessage, refusal.Message);
        Assert.Equal(0, await service.GetUserCountAsync(Ct));
        Assert.Null(await service.GetByGitHubIdAsync("777", Ct));
        Assert.Null(await service.GetAdminUserAsync(Ct));

        // The invitation is untouched: still pending, still unactivated.
        var invitation = await service.GetInvitationByUsernameAsync("invited", Ct);
        Assert.NotNull(invitation);
        Assert.True(invitation!.IsPending);
        Assert.Null(invitation.ActivatedUserId);
    }

    /// <summary>
    /// After bootstrap, activation binds ONE identity to exactly ONE activated account carrying the
    /// invitation's assigned role, and records the activation on the invitation.
    /// </summary>
    [Fact]
    public async Task ActivateInvitationAsync_AfterBootstrap_BindsOneIdentityToOneAccountWithAssignedRole()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service);
        var invitation = await service.InviteUserAsync("newcomer", UserRole.Operator, Ct);

        var activated = await service.ActivateInvitationAsync(
            "NEWCOMER", "55555", "New Comer", "https://avatar/newcomer.png", "newcomer@example.com", Ct);

        Assert.True(activated.Id > bootstrap.Id);
        Assert.Equal("55555", activated.GitHubId);
        Assert.Equal("NEWCOMER", activated.Username);
        Assert.Equal(UserRole.Operator, activated.Role);
        Assert.True(activated.Enabled);
        Assert.Equal("New Comer", activated.DisplayName);
        Assert.Equal("newcomer@example.com", activated.Email);

        var consumed = await service.GetInvitationByUsernameAsync("newcomer", Ct);
        Assert.NotNull(consumed);
        Assert.Equal(invitation.Id, consumed!.Id);
        Assert.Equal(activated.Id, consumed.ActivatedUserId);
        Assert.False(string.IsNullOrEmpty(consumed.ActivatedAt));
        Assert.False(consumed.IsPending);

        Assert.Equal(2, await service.GetUserCountAsync(Ct));

        // ONE identity, ONE account: a repeat activation of the same invitation is refused.
        await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ActivateInvitationAsync("newcomer", "66666", null, null, null, Ct));
        Assert.Equal(2, await service.GetUserCountAsync(Ct));
        Assert.Null(await service.GetByGitHubIdAsync("66666", Ct));
    }

    [Fact]
    public async Task ActivateInvitationAsync_UnknownInvitation_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ActivateInvitationAsync("nobody", "2", null, null, null, Ct));

        Assert.Contains("No invitation exists", refusal.Message);
        Assert.Equal(1, await service.GetUserCountAsync(Ct));
    }

    [Fact]
    public async Task ActivateInvitationAsync_RevokedInvitation_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service);
        await service.InviteUserAsync("revoked", UserRole.Admin, Ct);
        await service.RevokeInvitationAsync("revoked", Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ActivateInvitationAsync("revoked", "2", null, null, null, Ct));

        Assert.Contains("is revoked", refusal.Message);
        Assert.Equal(1, await service.GetUserCountAsync(Ct));
    }

    [Fact]
    public async Task ActivateInvitationAsync_DuplicateGitHubId_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service, githubId: "1", username: "original");
        await service.InviteUserAsync("second", UserRole.Member, Ct);

        var byId = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ActivateInvitationAsync("second", "1", null, null, null, Ct));
        Assert.Contains("already exists for GitHub id '1'", byId.Message);

        Assert.Equal(1, await service.GetUserCountAsync(Ct));
        Assert.True((await service.GetInvitationByUsernameAsync("second", Ct))!.IsPending);
    }

    /// <summary>
    /// The normalized-login guard is what makes activation's "one identity, one account" atomic even
    /// when an account row was written by something other than activation (a legacy row, or a
    /// concurrent writer). A pre-existing account whose login case-folds to the invited name is
    /// refused, and the invitation stays pending.
    /// </summary>
    [Fact]
    public async Task ActivateInvitationAsync_DuplicateNormalizedUsername_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service, githubId: "1", username: "original");
        await service.InviteUserAsync("Second", UserRole.Member, Ct);

        // Insert an account whose login differs only by casing from the invited (normalized) login.
        using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new UserEntity
            {
                GitHubId = "3",
                Username = "SECOND",
                AccessToken = "other-token",
                Role = UserRole.Member,
                Enabled = true,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
            });
            await db.SaveChangesAsync(Ct);
        }

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ActivateInvitationAsync("second", "2", null, null, null, Ct));

        Assert.Contains("already exists for GitHub login 'second'", refusal.Message);
        Assert.Null(await service.GetByGitHubIdAsync("2", Ct));
        Assert.True((await service.GetInvitationByUsernameAsync("second", Ct))!.IsPending);
    }

    // ── Account role / enabled operations and last-admin protection ──────────

    [Fact]
    public async Task ChangeUserRoleAsync_DemotesANonLastAdmin()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service, githubId: "1", username: "original");
        await service.InviteUserAsync("second", UserRole.Admin, Ct);
        var second = await service.ActivateInvitationAsync("second", "2", null, null, null, Ct);

        var demoted = await service.ChangeUserRoleAsync(second.Id, UserRole.Viewer, Ct);

        Assert.Equal(UserRole.Viewer, demoted.Role);
        Assert.NotEqual(bootstrap.Id, demoted.Id);
        Assert.Equal(UserRole.Admin, (await service.GetAdminUserAsync(Ct))!.Role);
    }

    /// <summary>
    /// THE LAST ENABLED ADMIN CANNOT BE DEMOTED. A PENDING Admin invitation does NOT satisfy the
    /// protection: it is not an activated Admin, so the demotion is still refused.
    /// </summary>
    [Fact]
    public async Task ChangeUserRoleAsync_LastEnabledAdminWithPendingAdminInvitation_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service, githubId: "1", username: "original");
        await service.InviteUserAsync("pending-admin", UserRole.Admin, Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ChangeUserRoleAsync(bootstrap.Id, UserRole.Operator, Ct));

        Assert.Contains("last enabled Admin", refusal.Message);
        Assert.Contains("pending Admin invitation does not count", refusal.Message);

        // Nothing changed: still an enabled Admin, and the invitation is still pending.
        var unchanged = await service.GetActivatedUserByIdAsync(bootstrap.Id, Ct);
        Assert.Equal(UserRole.Admin, unchanged!.Role);
        Assert.True((await service.GetInvitationByUsernameAsync("pending-admin", Ct))!.IsPending);
    }

    [Fact]
    public async Task ChangeUserRoleAsync_SameRole_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ChangeUserRoleAsync(bootstrap.Id, UserRole.Admin, Ct));

        Assert.Contains("already carries role 'admin'", refusal.Message);
    }

    [Fact]
    public async Task ChangeUserRoleAsync_UnknownAccount_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ChangeUserRoleAsync(4242, UserRole.Admin, Ct));

        Assert.Contains("No activated account exists with internal id 4242", refusal.Message);
    }

    [Fact]
    public async Task SetUserEnabledAsync_DisablesAndReenablesRetainingCredentialsAndHistory()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service, githubId: "1", username: "original", token: "original-token");
        await service.InviteUserAsync("second", UserRole.Admin, Ct);
        await service.ActivateInvitationAsync("second", "2", null, null, null, Ct);

        var disabled = await service.SetUserEnabledAsync(bootstrap.Id, enabled: false, Ct);
        Assert.False(disabled.Enabled);

        // Credentials and history are RETAINED across the disable.
        var stored = await service.GetByGitHubIdAsync("1", Ct);
        Assert.NotNull(stored);
        Assert.Equal("original-token", stored!.AccessToken);
        Assert.Equal("refresh-xyz", stored.RefreshToken);
        Assert.Equal(disabled.CreatedAt, stored.CreatedAt);
        Assert.Equal(disabled.LastLoginAt, stored.LastLoginAt);

        var reEnabled = await service.SetUserEnabledAsync(bootstrap.Id, enabled: true, Ct);
        Assert.True(reEnabled.Enabled);
        Assert.Equal(disabled.Id, reEnabled.Id);
    }

    [Fact]
    public async Task SetUserEnabledAsync_SameState_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.SetUserEnabledAsync(bootstrap.Id, enabled: true, Ct));

        Assert.Contains("already enabled", refusal.Message);
    }

    /// <summary>
    /// THE LAST ENABLED ADMIN CANNOT BE DISABLED — and a pending Admin invitation does NOT count.
    /// </summary>
    [Fact]
    public async Task SetUserEnabledAsync_LastEnabledAdminWithPendingAdminInvitation_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service);
        await service.InviteUserAsync("pending-admin", UserRole.Admin, Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.SetUserEnabledAsync(bootstrap.Id, enabled: false, Ct));

        Assert.Contains("last enabled Admin", refusal.Message);
        Assert.True((await service.GetActivatedUserByIdAsync(bootstrap.Id, Ct))!.Enabled);
    }

    /// <summary>A DISABLED Admin does not count towards the protection — only ENABLED Admins do.</summary>
    [Fact]
    public async Task SetUserEnabledAsync_TwoAdmins_AllowsDisablingOne_ThenProtectsTheLast()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await BootstrapAsync(service, githubId: "1", username: "original");
        await service.InviteUserAsync("second", UserRole.Admin, Ct);
        var second = await service.ActivateInvitationAsync("second", "2", null, null, null, Ct);

        // With two enabled Admins either one may be disabled.
        await service.SetUserEnabledAsync(second.Id, enabled: false, Ct);

        // Now only the original remains enabled → it is protected.
        await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.SetUserEnabledAsync(bootstrap.Id, enabled: false, Ct));

        // ...and the disabled Admin cannot be demoted into a state that would leave none either;
        // re-enabling it restores two enabled Admins, after which demotion of one is allowed.
        await service.SetUserEnabledAsync(second.Id, enabled: true, Ct);
        var demoted = await service.ChangeUserRoleAsync(second.Id, UserRole.Member, Ct);
        Assert.Equal(UserRole.Member, demoted.Role);

        // The original is now the only enabled Admin again → demoting it is refused.
        await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ChangeUserRoleAsync(bootstrap.Id, UserRole.Viewer, Ct));
    }

    // ── Matching-bootstrap invitation consumption ────────────────────────────

    /// <summary>
    /// A MATCHING ENABLED PENDING INVITATION IS CONSUMED BY THE BOOTSTRAP SIGN-IN, but the bootstrap
    /// account KEEPS Admin regardless of the invitation role — a Viewer/Member invitation can never
    /// demote the original admin.
    /// </summary>
    [Theory]
    [InlineData(UserRole.Viewer)]
    [InlineData(UserRole.Member)]
    [InlineData(UserRole.Operator)]
    [InlineData(UserRole.Admin)]
    public async Task CreateOrUpdateUserAsync_MatchingPendingInvitation_IsConsumedAndBootstrapStaysAdmin(UserRole invitationRole)
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var invitation = await service.InviteUserAsync("octocat", invitationRole, Ct);

        var user = await BootstrapAsync(service, githubId: "12345", username: "OctoCat");

        // The account is the bootstrap Admin — NOT the invitation's role.
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.True(user.Enabled);

        // The invitation is consumed into exactly this account and is no longer pending.
        var consumed = await service.GetInvitationByUsernameAsync("octocat", Ct);
        Assert.NotNull(consumed);
        Assert.Equal(invitation.Id, consumed!.Id);
        Assert.Equal(user.Id, consumed.ActivatedUserId);
        Assert.False(string.IsNullOrEmpty(consumed.ActivatedAt));
        Assert.False(consumed.IsPending);

        // ONE activated account only: consumption never creates a second row.
        Assert.Equal(1, await service.GetUserCountAsync(Ct));
        Assert.Single(await service.ListActivatedUsersAsync(Ct));
    }

    /// <summary>
    /// A REVOKED MATCHING INVITATION IS NOT SILENTLY CONSUMED: the bootstrap account is still created
    /// (admission is unchanged), the invitation stays revoked and unactivated, and the conflict is
    /// REPORTED on the returned outcome.
    /// </summary>
    [Fact]
    public async Task BootstrapOrRefreshAsync_RevokedMatchingInvitation_IsReportedNotConsumed()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var invitation = await service.InviteUserAsync("octocat", UserRole.Admin, Ct);
        var revoked = await service.RevokeInvitationAsync("octocat", Ct);

        var outcome = await service.BootstrapOrRefreshAsync(
            "12345", "octocat", null, null, null, "token-abc", null, null, Ct);

        Assert.True(outcome.CreatedNewAccount);
        Assert.Equal(UserRole.Admin, outcome.User.Role);
        Assert.Null(outcome.ConsumedInvitation);

        var conflict = outcome.ConflictingInvitation;
        Assert.NotNull(conflict);
        Assert.Equal(invitation.Id, conflict!.Id);
        Assert.False(conflict.Enabled);
        Assert.Null(conflict.ActivatedUserId);

        // The stored invitation is untouched — revocation is not undone by a sign-in.
        var stored = await service.GetInvitationByUsernameAsync("octocat", Ct);
        Assert.NotNull(stored);
        Assert.False(stored!.Enabled);
        Assert.Null(stored.ActivatedUserId);
        Assert.Equal(revoked.RevokedAt, stored.RevokedAt);
        Assert.Equal(revoked.UpdatedAt, stored.UpdatedAt);
    }

    /// <summary>A bootstrap login with no invitation consumes nothing and reports no conflict.</summary>
    [Fact]
    public async Task BootstrapOrRefreshAsync_NoMatchingInvitation_ConsumesNothing()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.InviteUserAsync("someone-else", UserRole.Admin, Ct);

        var outcome = await service.BootstrapOrRefreshAsync(
            "12345", "octocat", null, null, null, "token-abc", null, null, Ct);

        Assert.True(outcome.CreatedNewAccount);
        Assert.Null(outcome.ConsumedInvitation);
        Assert.Null(outcome.ConflictingInvitation);
        Assert.True((await service.GetInvitationByUsernameAsync("someone-else", Ct))!.IsPending);
    }

    /// <summary>
    /// A refresh (not a create) never consumes an invitation: consumption is a BOOTSTRAP effect only,
    /// so an invitation that happens to match the original login after the fact stays pending.
    /// </summary>
    [Fact]
    public async Task BootstrapOrRefreshAsync_RefreshAfterBootstrap_DoesNotConsumeInvitations()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await BootstrapAsync(service, githubId: "1", username: "original");

        // Written straight into the invitation table: the directory's own invite path refuses a login
        // that already has an activated account, so this row models an invitation that existed BEFORE
        // the account was activated/renamed.
        using (var db = factory.CreateDbContext())
        {
            db.UserInvitations.Add(new UserInvitationEntity
            {
                NormalizedUsername = "original",
                Role = UserRole.Member,
                Enabled = true,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
                UpdatedAt = "2024-01-01T00:00:00.0000000Z",
            });
            await db.SaveChangesAsync(Ct);
        }

        var outcome = await service.BootstrapOrRefreshAsync(
            "1", "original", null, null, null, "rotated-token", null, null, Ct);

        Assert.False(outcome.CreatedNewAccount);
        Assert.Null(outcome.ConsumedInvitation);
        Assert.Null(outcome.ConflictingInvitation);
        Assert.Equal("rotated-token", outcome.User.AccessToken);
        Assert.True((await service.GetInvitationByUsernameAsync("original", Ct))!.IsPending);
    }

    // ── TimeProvider ────────────────────────────────────────────────────────

    /// <summary>Every new timestamp comes from the injected clock — never the wall clock.</summary>
    [Fact]
    public async Task Timestamps_ComeFromTheInjectedTimeProvider()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero));
        var (service, factory) = CreateService(clock);
        using var _ = factory;

        var invitation = await service.InviteUserAsync("octocat", UserRole.Admin, Ct);
        Assert.Equal("2031-05-06T07:08:09.0000000Z", invitation.CreatedAt);

        clock.Now = new DateTimeOffset(2032, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var user = await BootstrapAsync(service);
        Assert.Equal("2032-01-02T03:04:05.0000000Z", user.CreatedAt);
        Assert.Equal(user.CreatedAt, user.LastLoginAt);

        var consumed = await service.GetInvitationByUsernameAsync("octocat", Ct);
        Assert.Equal("2032-01-02T03:04:05.0000000Z", consumed!.ActivatedAt);
        Assert.Equal("2031-05-06T07:08:09.0000000Z", consumed.CreatedAt);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
