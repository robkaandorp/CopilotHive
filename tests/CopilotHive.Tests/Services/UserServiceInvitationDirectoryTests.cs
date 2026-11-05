using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Focused tests for the SECRET-FREE invitation directory: the projections that expose WHO exists
/// without exposing any credential, the identity-conflict rules, and the fact that a PENDING
/// Admin invitation never counts as an activated Admin.
/// <para>
/// These assertions are deliberately about the OBSERVABLE contract — the projection's property
/// set, the persisted text, the bootstrap count — rather than about the implementation, so a
/// rename that made a credential reachable through a listing would fail here.
/// </para>
/// </summary>
public sealed class UserServiceInvitationDirectoryTests
{
    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> over a single shared, open SQLite in-memory
    /// connection, so contexts created from it all observe the same database.
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

    private static (UserService Service, SharedConnectionFactory Factory) CreateService()
    {
        var factory = new SharedConnectionFactory();
        return (new UserService(factory, NullLogger<UserService>.Instance), factory);
    }

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// The EXACT property set of the activated-account projection. Asserting the whole set — not
    /// merely "no property is named *Token*" — means a credential renamed to <c>Secret</c> or
    /// <c>Credential</c> would also fail here: the projection may carry only these members.
    /// </summary>
    private static readonly string[] ActivatedUserSummaryProperties =
    [
        "Id", "GitHubId", "Username", "DisplayName", "AvatarUrl", "Email",
        "Role", "Enabled", "CreatedAt", "LastLoginAt",
    ];

    /// <summary>The EXACT property set of the invitation projection.</summary>
    private static readonly string[] UserInvitationSummaryProperties =
    [
        "Id", "NormalizedUsername", "Role", "Enabled", "CreatedAt", "UpdatedAt",
        "RevokedAt", "ActivatedUserId", "ActivatedAt", "IsPending",
    ];

    // ── Secret-free projections ─────────────────────────────────────────────

    /// <summary>
    /// The ACTIVATED-ACCOUNT PROJECTION CARRIES NO CREDENTIAL AT ALL — its property set is exactly
    /// the non-credential members, so no listing or resolver can select a token through it.
    /// </summary>
    [Fact]
    public void ActivatedUserSummary_ExposesExactlyTheNonCredentialMembers()
    {
        var properties = typeof(ActivatedUserSummary)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ActivatedUserSummaryProperties.OrderBy(n => n, StringComparer.Ordinal), properties);
    }

    /// <summary>
    /// The INVITATION PROJECTION CARRIES NO CREDENTIAL AT ALL — the exact property set, so a
    /// renamed credential member would also be caught.
    /// </summary>
    [Fact]
    public void UserInvitationSummary_ExposesExactlyTheNonCredentialMembers()
    {
        var properties = typeof(UserInvitationSummary)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(UserInvitationSummaryProperties.OrderBy(n => n, StringComparer.Ordinal), properties);
    }

    /// <summary>
    /// The projections carry no OAuth material not only by NAME but by TYPE: no member of either
    /// record is a token/secret-shaped value. The invitation projection in particular holds no
    /// string member that could hold a credential.
    /// </summary>
    [Fact]
    public void InvitationProjection_HoldsNoStringMemberThatCouldCarryACredential()
    {
        var stringMembers = typeof(UserInvitationSummary)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToArray();

        // Only the identity/timestamp strings may exist — no token, secret or credential member.
        Assert.Equal(
            ["ActivatedAt", "CreatedAt", "NormalizedUsername", "RevokedAt", "UpdatedAt"],
            stringMembers.OrderBy(n => n, StringComparer.Ordinal));
    }

    // ── Every role, and the bootstrap count ─────────────────────────────────

    /// <summary>
    /// EVERY ROLE CAN BE INVITED AND LISTED, and the persisted representation is the validated
    /// LOWERCASE role text — never a free-form string. A pending invitation of any role (Admin
    /// included) never affects the bootstrap count.
    /// </summary>
    [Fact]
    public async Task InviteUserAsync_EveryRole_IsPersistedAsLowercaseRoleText_AndDoesNotAffectBootstrapCount()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var roles = new[] { UserRole.Viewer, UserRole.Member, UserRole.Operator, UserRole.Admin };

        foreach (var role in roles)
        {
            var invitation = await service.InviteUserAsync($"invited-{role}", role, Ct);
            Assert.Equal(role, invitation.Role);
            Assert.True(invitation.IsPending);
        }

        // The persisted TEXT is the validated lowercase representation of each role.
        using (var db = factory.CreateDbContext())
        {
            using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT normalized_username, role FROM user_invitations ORDER BY normalized_username";
            using var reader = cmd.ExecuteReader();
            var persisted = new List<(string Username, string Role)>();
            while (reader.Read())
                persisted.Add((reader.GetString(0), reader.GetString(1)));

            Assert.Equal(roles.Length, persisted.Count);
            Assert.Equal(
                [
                    ("invited-admin", "admin"),
                    ("invited-member", "member"),
                    ("invited-operator", "operator"),
                    ("invited-viewer", "viewer"),
                ],
                persisted);
        }

        // The BOOTSTRAP COUNT is unaffected: invitations are never activated accounts.
        Assert.Equal(0, await service.GetUserCountAsync(Ct));
        Assert.Null(await service.GetAdminUserAsync(Ct));
    }

    /// <summary>
    /// The bootstrap count counts ACTIVATED accounts only — activating one invitation of every role
    /// raises the count by exactly the number of activations, and the pending remainder does not
    /// count.
    /// </summary>
    [Fact]
    public async Task GetUserCountAsync_CountsActivatedAccountsOnly_AfterActivatingOneInvitationOfEveryRole()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.CreateOrUpdateUserAsync(
            "1", "original", null, null, null, "original-token", null, null, Ct);

        var roles = new[] { UserRole.Viewer, UserRole.Member, UserRole.Operator, UserRole.Admin };
        foreach (var role in roles)
            await service.InviteUserAsync($"invited-{role}", role, Ct);

        // Only the bootstrap account so far.
        Assert.Equal(1, await service.GetUserCountAsync(Ct));

        var githubId = 100;
        foreach (var role in roles)
        {
            githubId++;
            var activated = await service.ActivateInvitationAsync($"invited-{role}", githubId.ToString(), null, null, null, Ct);
            Assert.Equal(role, activated.Role);
        }

        // Four activations plus the bootstrap account — and NOT the invitation count.
        Assert.Equal(5, await service.GetUserCountAsync(Ct));

        // The original account still supplies the global token.
        Assert.Equal("original-token", await service.GetActiveAccessTokenAsync(Ct));
    }

    // ── Identity conflicts ──────────────────────────────────────────────────

    /// <summary>
    /// AN IDENTITY CONFLICT FAILS CLEARLY even when the activated account's login differs from the
    /// invited one only by CASE: GitHub logins are case-insensitive, so casing must never produce
    /// two identities out of one login.
    /// </summary>
    [Fact]
    public async Task InviteUserAsync_ActivatedAccountWithDifferentCasing_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.CreateOrUpdateUserAsync(
            "1", "OctoCat", null, null, null, "original-token", null, null, Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.InviteUserAsync("OCTOCAT", UserRole.Admin, Ct));

        Assert.Contains("activated account already exists", refusal.Message);
        Assert.Empty(await service.ListInvitationsAsync(Ct));
        Assert.Equal(1, await service.GetUserCountAsync(Ct));
    }

    /// <summary>
    /// A login containing WHITESPACE is not a GitHub login and is rejected before anything is
    /// written.
    /// </summary>
    [Fact]
    public async Task InviteUserAsync_UsernameWithInternalWhitespace_IsRejected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.InviteUserAsync("octo cat", UserRole.Member, Ct));

        Assert.Empty(await service.ListInvitationsAsync(Ct));
    }

    /// <summary>
    /// A REVOKED INVITATION IS STILL AN IDENTITY CONFLICT: re-inviting the same login is refused,
    /// and the single existing row is retained (never duplicated, never silently replaced).
    /// </summary>
    [Fact]
    public async Task InviteUserAsync_RevokedInvitation_IsRetainedAndNotDuplicated()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var first = await service.InviteUserAsync("octocat", UserRole.Member, Ct);
        await service.RevokeInvitationAsync("octocat", Ct);

        await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.InviteUserAsync("octocat", UserRole.Operator, Ct));

        var listed = Assert.Single(await service.ListInvitationsAsync(Ct));
        Assert.Equal(first.Id, listed.Id);
        Assert.Equal(UserRole.Member, listed.Role);
        Assert.False(listed.Enabled);
    }

    /// <summary>
    /// AN INVITATION ACTIVATION MINTS NO CREDENTIAL. The activated account is created by a
    /// DIRECTORY operation, not by an OAuth sign-in, so it holds no access token, no refresh token
    /// and no expiry — it must never be a credential source. This is read back from the DATABASE
    /// (not from a summary projection), so a credential written behind the projection would be
    /// visible here.
    /// </summary>
    [Fact]
    public async Task ActivateInvitationAsync_MintsNoCredentialOnTheActivatedAccount()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.CreateOrUpdateUserAsync(
            "1", "original", null, null, null, "original-token", null, null, Ct);
        await service.InviteUserAsync("newcomer", UserRole.Member, Ct);

        var activated = await service.ActivateInvitationAsync(
            "newcomer", "55555", "New Comer", null, "newcomer@example.com", Ct);

        // Read the RAW ROW back out of the database.
        using (var db = factory.CreateDbContext())
        {
            var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Id == activated.Id, Ct);

            Assert.Equal(string.Empty, stored.AccessToken);
            Assert.Null(stored.RefreshToken);
            Assert.Null(stored.TokenExpiresAt);

            // The account has never signed in: no last-login timestamp either.
            Assert.Null(stored.LastLoginAt);
        }

        // ...and it is therefore never a credential source for the global token.
        Assert.Equal("original-token", await service.GetActiveAccessTokenAsync(Ct));
    }

    /// <summary>
    /// THE STORAGE INVARIANT: A PENDING INVITATION NEVER BECOMES A USERS ROW. The users table
    /// keeps its <c>github_id NOT NULL</c> unique constraint and holds only ACTIVATED accounts, so
    /// the activated-account count is unchanged by an invitation and no users row can exist for an
    /// invitation that has not been activated.
    /// </summary>
    [Fact]
    public async Task InviteUserAsync_APendingInvitationNeverCreatesAUsersRow()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var invitation = await service.InviteUserAsync("pending-user", UserRole.Admin, Ct);

        // No users row exists for the invited login.
        Assert.Equal(0, await service.GetUserCountAsync(Ct));
        Assert.Null(await service.GetAdminUserAsync(Ct));
        Assert.Null(await service.GetByGitHubIdAsync("pending-user", Ct));
        Assert.Null(await service.GetActivatedUserByUsernameAsync("pending-user", Ct));

        using (var db = factory.CreateDbContext())
        {
            Assert.Empty(await db.Users.AsNoTracking().ToListAsync(Ct));

            // The invitation itself lives in the SEPARATE table, keyed by the normalized login.
            var stored = await db.UserInvitations.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(invitation.Id, stored.Id);
            Assert.Equal("pending-user", stored.NormalizedUsername);
            Assert.Null(stored.ActivatedUserId);
        }
    }

    /// <summary>
    /// THE USERS TABLE KEEPS ITS <c>github_id NOT NULL</c> UNIQUE CONSTRAINT after the invitation
    /// table was added — the storage invariant that makes "a pending invitation can never be
    /// represented as a users row" true at the SCHEMA level, not only by convention.
    /// </summary>
    [Fact]
    public void UsersTable_KeepsGithubIdNotNullAndUnique_AlongsideTheInvitationTable()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        using var db = factory.CreateDbContext();
        using var conn = db.Database.GetDbConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(\"users\")";
        using var reader = cmd.ExecuteReader();
        var githubIdNotNull = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "github_id", StringComparison.OrdinalIgnoreCase))
                githubIdNotNull = reader.GetInt32(3) == 1;
        }

        Assert.True(githubIdNotNull, "users.github_id must stay NOT NULL");

        using var indexCmd = conn.CreateCommand();
        indexCmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'users' " +
            "AND name = 'IX_users_github_id' AND sql LIKE '%UNIQUE%'";
        Assert.Equal(1L, Convert.ToInt64(indexCmd.ExecuteScalar()));

        // The invitations table is a SEPARATE table with its own unique index.
        using var invitationIndexCmd = conn.CreateCommand();
        invitationIndexCmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'user_invitations' " +
            "AND name = 'idx_user_invitations_normalized_username' AND sql LIKE '%UNIQUE%'";
        Assert.Equal(1L, Convert.ToInt64(invitationIndexCmd.ExecuteScalar()));
    }

    // ── A pending Admin invitation is never an activated Admin ───────────────

    /// <summary>
    /// A REVOKED ADMIN INVITATION DOES NOT SATISFY THE LAST-ADMIN PROTECTION either — like a
    /// pending one, it is not an activated Admin, so the only enabled Admin still cannot be
    /// disabled.
    /// </summary>
    [Fact]
    public async Task SetUserEnabledAsync_RevokedAdminInvitation_DoesNotSatisfyTheLastAdminProtection()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await service.CreateOrUpdateUserAsync(
            "1", "original", null, null, null, "original-token", null, null, Ct);
        await service.InviteUserAsync("revoked-admin", UserRole.Admin, Ct);
        await service.RevokeInvitationAsync("revoked-admin", Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.SetUserEnabledAsync(bootstrap.Id, enabled: false, Ct));

        Assert.Contains("last enabled Admin", refusal.Message);

        // Nothing changed: the original account is still an enabled Admin.
        var unchanged = await service.GetActivatedUserByIdAsync(bootstrap.Id, Ct);
        Assert.NotNull(unchanged);
        Assert.True(unchanged!.Enabled);
        Assert.Equal(UserRole.Admin, unchanged.Role);
    }

    /// <summary>
    /// A DISABLED Admin account does not satisfy the protection, and a PENDING Admin invitation
    /// does not either: only ENABLED, ACTIVATED Admins count. The demotion of the only enabled
    /// Admin is refused while a pending Admin invitation exists.
    /// </summary>
    [Fact]
    public async Task ChangeUserRoleAsync_OnlyEnabledActivatedAdminsCount_TowardTheProtection()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var bootstrap = await service.CreateOrUpdateUserAsync(
            "1", "original", null, null, null, "original-token", null, null, Ct);

        // A second, ACTIVATED but DISABLED Admin: it does not count either.
        await service.InviteUserAsync("disabled-admin", UserRole.Admin, Ct);
        var disabledAdmin = await service.ActivateInvitationAsync("disabled-admin", "2", null, null, null, Ct);
        await service.SetUserEnabledAsync(disabledAdmin.Id, enabled: false, Ct);

        // ...and a PENDING Admin invitation, which is not an account at all.
        await service.InviteUserAsync("pending-admin", UserRole.Admin, Ct);

        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => service.ChangeUserRoleAsync(bootstrap.Id, UserRole.Operator, Ct));

        Assert.Contains("last enabled Admin", refusal.Message);

        // The original is still an enabled Admin, and the pending invitation is untouched.
        var unchanged = await service.GetActivatedUserByIdAsync(bootstrap.Id, Ct);
        Assert.Equal(UserRole.Admin, unchanged!.Role);
        Assert.True(unchanged.Enabled);
        Assert.True((await service.GetInvitationByUsernameAsync("pending-admin", Ct))!.IsPending);
    }

    // ── The activated account an invitation binds ───────────────────────────

    /// <summary>
    /// AN INVITATION BINDS EXACTLY ONE IDENTITY TO ONE ACTIVATED ACCOUNT, and the activated account
    /// carries the invitation's ASSIGNED role — the role the invitation recorded, not a default.
    /// </summary>
    [Fact]
    public async Task ActivateInvitationAsync_BindsOneIdentityToOneAccountCarryingTheAssignedRole()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.CreateOrUpdateUserAsync(
            "1", "original", null, null, null, "original-token", null, null, Ct);

        var invitation = await service.InviteUserAsync("newcomer", UserRole.Operator, Ct);

        var activated = await service.ActivateInvitationAsync(
            "newcomer", "55555", "New Comer", "https://avatar/newcomer.png", "newcomer@example.com", Ct);

        // The account carries the invitation's role — not Admin, not the CLR default.
        Assert.Equal(UserRole.Operator, activated.Role);

        // The invitation records the activation and is no longer pending.
        var consumed = await service.GetInvitationByUsernameAsync("newcomer", Ct);
        Assert.NotNull(consumed);
        Assert.Equal(invitation.Id, consumed!.Id);
        Assert.Equal(activated.Id, consumed.ActivatedUserId);
        Assert.NotNull(consumed.ActivatedAt);
        Assert.False(consumed.IsPending);

        // The account resolves to the SAME row through both resolvers.
        var byId = await service.GetActivatedUserByIdAsync(activated.Id, Ct);
        var byUsername = await service.GetActivatedUserByUsernameAsync("NEWCOMER", Ct);
        Assert.NotNull(byId);
        Assert.NotNull(byUsername);
        Assert.Equal(byId, byUsername);
    }
}
