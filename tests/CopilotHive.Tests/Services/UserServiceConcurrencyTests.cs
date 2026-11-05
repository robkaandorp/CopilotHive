using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// DETERMINISTIC concurrency tests for the user directory: every test uses SEPARATE
/// <see cref="UserService"/> instances, each with its own <see cref="IDbContextFactory{TContext}"/>
/// over its OWN <see cref="SqliteConnection"/> to ONE file-backed database.
/// <para>
/// Coordination is by explicit rendezvous (a <see cref="Barrier"/> plus task joins), never by
/// scheduling luck or elapsed time: each pair of operations is launched from a barrier so both
/// sides are known to have STARTED, and every observation is made after both have COMPLETED. The
/// assertions are about the outcome the database arbitrated — not about which side won.
/// </para>
/// </summary>
public sealed class UserServiceConcurrencyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public UserServiceConcurrencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"copilothive-userdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "copilothive.db");

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        // Pooled connections must be released or Windows keeps the file locked.
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort: a failure to delete the temp dir must never fail a test.
        }
    }

    private CopilotHiveDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new CopilotHiveDbContext(options);
    }

    /// <summary>
    /// A service instance with its OWN connection settings, so two instances never share an
    /// in-process connection object. SQLite's write lock is what arbitrates between them.
    /// </summary>
    private UserService NewService() =>
        new(new IndependentDbContextFactory(_dbPath), NullLogger<UserService>.Instance);

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// Runs two asynchronous operations started from a shared barrier and awaits BOTH, returning each
    /// side's outcome. A rendezvous (not a sleep) establishes that both were in flight. The two sides
    /// may have different result types — the outcomes are deliberately compared as outcomes, never by
    /// which task happened to finish first.
    /// </summary>
    private static async Task<(TFirst First, TSecond Second)> RunConcurrentlyAsync<TFirst, TSecond>(
        Func<Task<TFirst>> first,
        Func<Task<TSecond>> second)
    {
        using var barrier = new Barrier(2);

        var firstTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await first();
        });

        var secondTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await second();
        });

        return (await firstTask, await secondTask);
    }

    // ── Bootstrap ────────────────────────────────────────────────────────────

    /// <summary>
    /// TWO DISTINCT SIMULTANEOUS FIRST IDENTITIES → EXACTLY ONE ORIGINAL ACCOUNT. The loser is
    /// REJECTED and never inserted. Run through separate service instances and separate SQLite
    /// connections to the same file, with a barrier so both are genuinely in flight.
    /// </summary>
    [Fact]
    public async Task Bootstrap_TwoDistinctIdentitiesConcurrently_ProducesExactlyOneAccountAndRejectsTheLoser()
    {
        var serviceA = NewService();
        var serviceB = NewService();

        var (outcomeA, outcomeB) = await RunConcurrentlyAsync(
            () => Capture("100", () => serviceA.CreateOrUpdateUserAsync(
                "100", "alpha", null, null, null, "token-alpha", null, null, Ct)),
            () => Capture("200", () => serviceB.CreateOrUpdateUserAsync(
                "200", "beta", null, null, null, "token-beta", null, null, Ct)));

        // EXACTLY ONE side created the account; the other was REFUSED with the directory refusal.
        var winners = new[] { outcomeA, outcomeB }.Where(o => o.User is not null).ToList();
        var losers = new[] { outcomeA, outcomeB }.Where(o => o.Refusal is not null).ToList();

        Assert.Single(winners);
        Assert.Single(losers);
        Assert.IsType<UserDirectoryException>(losers[0].Refusal);

        // THE DATABASE — not either process's memory — holds exactly one account.
        await using var verification = NewContext();
        var accounts = await verification.Users.AsNoTracking().ToListAsync(Ct);
        var account = Assert.Single(accounts);
        Assert.Equal(winners[0].ResultGithubId, account.GitHubId);
        Assert.Equal(UserRole.Admin, account.Role);
        Assert.True(account.Enabled);

        // The winner's credential is the one stored; the loser's never reached the table.
        Assert.Equal(winners[0].ResultGithubId == "100" ? "token-alpha" : "token-beta", account.AccessToken);

        // The rejected identity has NO row of its own.
        var loserGithubId = losers[0].GithubId;
        Assert.NotNull(loserGithubId);
        Assert.NotEqual(account.GitHubId, loserGithubId);
        Assert.DoesNotContain(accounts, u => u.GitHubId == loserGithubId);
    }

    /// <summary>
    /// BOOTSTRAP vs ACTIVATION RACE: an activation started concurrently with the first bootstrap can
    /// never produce the ORIGINAL account. Either the bootstrap commits first (activation then gets a
    /// higher id and a non-Admin role) or the activation refuses for lack of a bootstrap account. In
    /// neither case is the invited identity the original/token account.
    /// </summary>
    [Fact]
    public async Task BootstrapAndActivationConcurrently_InvitedAccountNeverBecomesTheOriginal()
    {
        var bootstrapService = NewService();
        var activationService = NewService();

        await bootstrapService.InviteUserAsync("invited", UserRole.Admin, Ct);

        var (bootstrap, activation) = await RunConcurrentlyAsync(
            () => Capture("100", () => bootstrapService.CreateOrUpdateUserAsync(
                "100", "original", null, null, null, "original-token", null, null, Ct)),
            () => CaptureActivation("200", () => activationService.ActivateInvitationAsync(
                "invited", "200", null, null, null, Ct)));

        Assert.NotNull(bootstrap.User);
        Assert.Equal(UserRole.Admin, bootstrap.User!.Role);

        // The original/token account is the bootstrap identity, selected by the LOWEST id.
        await using var verification = NewContext();
        var original = await verification.Users.AsNoTracking().OrderBy(u => u.Id).FirstAsync(Ct);
        Assert.Equal("100", original.GitHubId);
        Assert.Equal("original-token", original.AccessToken);
        Assert.Equal(UserRole.Admin, original.Role);

        // Whatever the activation outcome, the invited account (if created at all) has a HIGHER id
        // and therefore never wins the original-account selector.
        var invited = await verification.Users.AsNoTracking()
            .Where(u => u.GitHubId == "200").ToListAsync(Ct);

        // The activation either refused (no row) or produced exactly one non-selector row.
        Assert.True(invited.Count <= 1);
        if (invited.Count == 1)
        {
            Assert.True(invited[0].Id > original.Id);
            Assert.Equal(activation.ResultGithubId, invited[0].GitHubId);
        }
        else
        {
            Assert.IsType<UserDirectoryException>(activation.Refusal);
        }

        // The global token is still the bootstrap account's.
        Assert.Equal("original-token", await bootstrapService.GetActiveAccessTokenAsync(Ct));
    }

    /// <summary>
    /// A CONCURRENT SECOND BOOTSTRAP IS IDEMPOTENT FOR THE SAME IDENTITY: two sign-ins of the SAME
    /// GitHub identity (the OAuth handler always names the same person) both succeed and produce ONE
    /// row — they cannot both create the bootstrap account.
    /// </summary>
    [Fact]
    public async Task Bootstrap_SameIdentityConcurrently_ProducesOneAccountAndBothSucceed()
    {
        var serviceA = NewService();
        var serviceB = NewService();

        var (first, second) = await RunConcurrentlyAsync(
            () => serviceA.CreateOrUpdateUserAsync("100", "octocat", null, null, null, "token-a", null, null, Ct),
            () => serviceB.CreateOrUpdateUserAsync("100", "octocat", null, null, null, "token-b", null, null, Ct));

        Assert.Equal(first.Id, second.Id);

        await using var verification = NewContext();
        var account = Assert.Single(await verification.Users.AsNoTracking().ToListAsync(Ct));
        Assert.Equal("100", account.GitHubId);
        Assert.Equal(UserRole.Admin, account.Role);
    }

    // ── Matching-invitation consumption ──────────────────────────────────────

    /// <summary>
    /// MATCHING-INVITATION CONSUMPTION IS ATOMIC: two service instances bootstrapping the SAME
    /// identity against a database holding exactly one matching invitation consume it ONCE, into ONE
    /// account, and never create a second.
    /// </summary>
    [Fact]
    public async Task BootstrapWithMatchingInvitationConcurrently_ConsumesTheInvitationExactlyOnce()
    {
        var seeding = NewService();
        var invitation = await seeding.InviteUserAsync("octocat", UserRole.Member, Ct);

        var serviceA = NewService();
        var serviceB = NewService();

        var (first, second) = await RunConcurrentlyAsync(
            () => serviceA.CreateOrUpdateUserAsync("100", "octocat", null, null, null, "token-a", null, null, Ct),
            () => serviceB.CreateOrUpdateUserAsync("100", "octocat", null, null, null, "token-b", null, null, Ct));

        Assert.Equal(first.Id, second.Id);

        await using var verification = NewContext();
        var account = Assert.Single(await verification.Users.AsNoTracking().ToListAsync(Ct));

        var consumed = Assert.Single(await verification.UserInvitations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(invitation.Id, consumed.Id);
        Assert.Equal(account.Id, consumed.ActivatedUserId);
        Assert.False(string.IsNullOrEmpty(consumed.ActivatedAt));

        // KEEPING ADMIN: even a Member invitation cannot demote the bootstrap account.
        Assert.Equal(UserRole.Admin, account.Role);
    }

    // ── Activation ───────────────────────────────────────────────────────────

    /// <summary>
    /// TWO CONCURRENT ACTIVATIONS OF THE SAME INVITATION → ONE ACTIVATED ACCOUNT. The database
    /// arbitrates: one side binds, the other is refused because the invitation is already activated.
    /// </summary>
    [Fact]
    public async Task Activation_SameInvitationConcurrently_ProducesOneActivatedAccount()
    {
        var seeding = NewService();
        await seeding.CreateOrUpdateUserAsync("1", "original", null, null, null, "original-token", null, null, Ct);
        await seeding.InviteUserAsync("newcomer", UserRole.Operator, Ct);

        var serviceA = NewService();
        var serviceB = NewService();

        var (first, second) = await RunConcurrentlyAsync(
            () => CaptureActivation("200", () => serviceA.ActivateInvitationAsync("newcomer", "200", null, null, null, Ct)),
            () => CaptureActivation("300", () => serviceB.ActivateInvitationAsync("newcomer", "300", null, null, null, Ct)));

        var winners = new[] { first, second }.Where(o => o.Summary is not null).ToList();
        var losers = new[] { first, second }.Where(o => o.Refusal is not null).ToList();
        Assert.Single(winners);
        Assert.Single(losers);

        await using var verification = NewContext();
        var accounts = await verification.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(Ct);
        Assert.Equal(2, accounts.Count);
        Assert.Equal("1", accounts[0].GitHubId);

        // Exactly one invited account exists, carrying the invitation's role.
        var invited = Assert.Single(accounts, u => u.GitHubId != "1");
        Assert.Equal(winners[0].ResultGithubId, invited.GitHubId);
        Assert.Equal(UserRole.Operator, invited.Role);

        // The rejected side's GitHub id never reached the table.
        var loserGithubId = losers[0].GithubId;
        Assert.NotEqual(invited.GitHubId, loserGithubId);
        Assert.DoesNotContain(accounts, u => u.GitHubId == loserGithubId);

        var invitation = Assert.Single(await verification.UserInvitations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(invited.Id, invitation.ActivatedUserId);
    }

    /// <summary>
    /// CONCURRENT ACTIVATIONS OF TWO DIFFERENT INVITATIONS both succeed and bind ONE identity each —
    /// the "one identity to one activated user" invariant holds under contention.
    /// </summary>
    [Fact]
    public async Task Activation_DifferentInvitationsConcurrently_BindsOneIdentityEach()
    {
        var seeding = NewService();
        await seeding.CreateOrUpdateUserAsync("1", "original", null, null, null, "original-token", null, null, Ct);
        await seeding.InviteUserAsync("member-user", UserRole.Member, Ct);
        await seeding.InviteUserAsync("viewer-user", UserRole.Viewer, Ct);

        var serviceA = NewService();
        var serviceB = NewService();

        var (first, second) = await RunConcurrentlyAsync(
            () => serviceA.ActivateInvitationAsync("member-user", "200", null, null, null, Ct),
            () => serviceB.ActivateInvitationAsync("viewer-user", "300", null, null, null, Ct));

        Assert.NotEqual(first.Id, second.Id);

        await using var verification = NewContext();
        var accounts = await verification.Users.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(3, accounts.Count);
        Assert.Equal(UserRole.Member, Assert.Single(accounts, u => u.GitHubId == "200").Role);
        Assert.Equal(UserRole.Viewer, Assert.Single(accounts, u => u.GitHubId == "300").Role);

        var invitations = await verification.UserInvitations.AsNoTracking().ToListAsync(Ct);
        Assert.All(invitations, i => Assert.NotNull(i.ActivatedUserId));
        Assert.Equal(2, invitations.Select(i => i.ActivatedUserId).Distinct().Count());
    }

    // ── Duplicate detection ─────────────────────────────────────────────────

    /// <summary>
    /// TWO CONCURRENT INVITATIONS FOR THE SAME NORMALIZED LOGIN → EXACTLY ONE ROW. The UNIQUE index
    /// is the arbiter, so the loser gets the same explicit duplicate refusal the sequential path
    /// produces. This is the "duplicate detection is atomic" contract.
    /// </summary>
    [Fact]
    public async Task Invite_DuplicateConcurrently_ProducesOneInvitation()
    {
        var serviceA = NewService();
        var serviceB = NewService();

        var (first, second) = await RunConcurrentlyAsync(
            () => CaptureInvitation(() => serviceA.InviteUserAsync("octocat", UserRole.Member, Ct)),
            () => CaptureInvitation(() => serviceB.InviteUserAsync("OCTOCAT", UserRole.Admin, Ct)));

        var winners = new[] { first, second }.Where(o => o.Invitation is not null).ToList();
        var losers = new[] { first, second }.Where(o => o.Refusal is not null).ToList();
        Assert.Single(winners);
        Assert.Single(losers);
        Assert.IsType<UserDirectoryException>(losers[0].Refusal);

        await using var verification = NewContext();
        var invitation = Assert.Single(await verification.UserInvitations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal("octocat", invitation.NormalizedUsername);
        Assert.Equal(winners[0].Invitation!.Role, invitation.Role);
    }

    /// <summary>
    /// CONCURRENT INVITE + ACTIVATION FOR THE SAME LOGIN: invitation insertion and activation both
    /// write, so the database serialises them. Either the invitation does not exist yet (activation
    /// refuses) or it does and is consumed — never two activated accounts for one login.
    /// </summary>
    [Fact]
    public async Task InviteAndActivateConcurrently_NeverProducesTwoAccountsForOneLogin()
    {
        var seeding = NewService();
        await seeding.CreateOrUpdateUserAsync("1", "original", null, null, null, "original-token", null, null, Ct);

        var inviteService = NewService();
        var activationService = NewService();

        var (invite, activation) = await RunConcurrentlyAsync(
            () => CaptureInvitation(() => inviteService.InviteUserAsync("newcomer", UserRole.Member, Ct)),
            () => CaptureActivation("200", () => activationService.ActivateInvitationAsync("newcomer", "200", null, null, null, Ct)));

        await using var verification = NewContext();
        var newcomerAccounts = await verification.Users.AsNoTracking()
            .Where(u => u.GitHubId == "200").ToListAsync(Ct);

        // At most ONE account for the login — never a second, regardless of which side won.
        Assert.True(newcomerAccounts.Count <= 1);

        // If the invitation landed before the activation, the activation consumed it exactly once;
        // otherwise the activation refused and the invitation is still pending.
        var invitation = await verification.UserInvitations.AsNoTracking()
            .SingleOrDefaultAsync(i => i.NormalizedUsername == "newcomer", Ct);

        if (invitation is null)
        {
            Assert.Null(invite.Invitation);
            Assert.NotNull(activation.Refusal);
        }
        else if (activation.Summary is not null)
        {
            Assert.Equal(activation.Summary.Id, invitation.ActivatedUserId);
        }
        else
        {
            Assert.NotNull(activation.Refusal);
            Assert.Null(invitation.ActivatedUserId);
        }
    }

    // ── Last enabled Admin protection ───────────────────────────────────────

    /// <summary>
    /// TWO CONCURRENT OPERATIONS THAT WOULD EACH REMOVE AN ENABLED ADMIN CANNOT BOTH SUCCEED when only
    /// two enabled Admins exist: the database serializer makes the second one observe the first's
    /// committed change, so at least one enabled Admin always survives.
    /// <para>
    /// A PENDING Admin invitation exists throughout, proving it does not satisfy the protection.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LastAdminProtection_ConcurrentRemovalsOfTwoAdmins_NeverLeavesZeroEnabledAdmins()
    {
        var seeding = NewService();
        var original = await seeding.CreateOrUpdateUserAsync(
            "1", "original", null, null, null, "original-token", null, null, Ct);
        await seeding.InviteUserAsync("second", UserRole.Admin, Ct);
        var second = await seeding.ActivateInvitationAsync("second", "2", null, null, null, Ct);

        // A PENDING Admin invitation — it must NOT be counted as a surviving enabled Admin.
        await seeding.InviteUserAsync("pending-admin", UserRole.Admin, Ct);

        Assert.True(original.Id < second.Id);

        var serviceA = NewService();
        var serviceB = NewService();

        var (demote, disable) = await RunConcurrentlyAsync(
            () => CaptureUser(() => serviceA.ChangeUserRoleAsync(original.Id, UserRole.Viewer, Ct)),
            () => CaptureUser(() => serviceB.SetUserEnabledAsync(second.Id, enabled: false, Ct)));

        await using var verification = NewContext();
        var enabledAdmins = await verification.Users.AsNoTracking()
            .CountAsync(u => u.Role == UserRole.Admin && u.Enabled, Ct);

        // THE INVARIANT: at least one ENABLED Admin always survives, and the pending Admin
        // invitation is not it.
        Assert.True(
            enabledAdmins >= 1,
            $"Expected at least one enabled Admin to survive; demote failure='{demote.Refusal?.Message}' " +
            $"disable failure='{disable.Refusal?.Message}'");

        // The pending invitation was never touched by either path.
        var pending = await verification.UserInvitations.AsNoTracking()
            .SingleAsync(i => i.NormalizedUsername == "pending-admin", Ct);
        Assert.Null(pending.ActivatedUserId);
        Assert.True(pending.Enabled);

        _ = demote;
        _ = disable;
    }

    /// <summary>
    /// The SAME guard under contention from the other direction: two DISABLES of two enabled Admins.
    /// The second one must observe the first's commit and refuse; a serialised outcome, verified by
    /// counting ENABLED Admins in the database rather than by inspecting which task finished first.
    /// </summary>
    [Fact]
    public async Task LastAdminProtection_ConcurrentDisablesOfTwoAdmins_LeavesOneEnabledAdmin()
    {
        var seeding = NewService();
        var original = await seeding.CreateOrUpdateUserAsync(
            "1", "original", null, null, null, "original-token", null, null, Ct);
        await seeding.InviteUserAsync("second", UserRole.Admin, Ct);
        var second = await seeding.ActivateInvitationAsync("second", "2", null, null, null, Ct);

        var serviceA = NewService();
        var serviceB = NewService();

        var (first, secondResult) = await RunConcurrentlyAsync(
            () => CaptureUser(() => serviceA.SetUserEnabledAsync(original.Id, enabled: false, Ct)),
            () => CaptureUser(() => serviceB.SetUserEnabledAsync(second.Id, enabled: false, Ct)));

        await using var verification = NewContext();
        var enabledAdmins = await verification.Users.AsNoTracking()
            .CountAsync(u => u.Role == UserRole.Admin && u.Enabled, Ct);

        Assert.Equal(1, enabledAdmins);

        // Exactly one operation succeeded and the other was refused by the last-admin guard.
        var successes = new[] { first, secondResult }.Count(o => o.Succeeded);
        var refusals = new[] { first, secondResult }.Count(o => o.Refusal is UserDirectoryException);
        Assert.Equal(1, successes);
        Assert.Equal(1, refusals);
    }

    // ── Outcome capture helpers ─────────────────────────────────────────────

    /// <summary>
    /// Captures the outcome of one ACCOUNT-WRITING attempt (bootstrap/activation), tagging it with the
    /// GitHub id the attempt used so a refusal can be attributed to a side without inspecting which
    /// task finished first.
    /// </summary>
    private static async Task<UserOutcome> Capture(string attemptedGithubId, Func<Task<UserEntity>> operation)
    {
        try
        {
            return new UserOutcome(attemptedGithubId, await operation(), null, null);
        }
        catch (UserDirectoryException ex)
        {
            return new UserOutcome(attemptedGithubId, null, null, ex);
        }
    }

    /// <summary>Captures the activated-account/refusal outcome of one activation.</summary>
    private static async Task<UserOutcome> CaptureActivation(
        string attemptedGithubId,
        Func<Task<ActivatedUserSummary>> operation)
    {
        try
        {
            var activated = await operation();
            return new UserOutcome(attemptedGithubId, null, activated, null);
        }
        catch (UserDirectoryException ex)
        {
            return new UserOutcome(attemptedGithubId, null, null, ex);
        }
    }

    /// <summary>
    /// Captures the summary/refusal outcome of an account DIRECTORY operation (role change, enable or
    /// disable). The attempted GitHub id is not known here — the operation addresses an account by its
    /// internal id — so the outcome is attributed by its success/failure shape alone.
    /// </summary>
    private static async Task<UserOutcome> CaptureUser(Func<Task<ActivatedUserSummary>> operation)
    {
        try
        {
            return new UserOutcome(null, null, await operation(), null);
        }
        catch (UserDirectoryException ex)
        {
            return new UserOutcome(null, null, null, ex);
        }
    }

    /// <summary>Captures the invitation/refusal outcome of one invitation write.</summary>
    private static async Task<InvitationOutcome> CaptureInvitation(Func<Task<UserInvitationSummary>> operation)
    {
        try
        {
            return new InvitationOutcome(await operation(), null);
        }
        catch (UserDirectoryException ex)
        {
            return new InvitationOutcome(null, ex);
        }
    }

    /// <summary>
    /// The captured outcome of one account-writing attempt. Exactly one of <see cref="User"/>,
    /// <see cref="Summary"/> and <see cref="Refusal"/> is non-null; <see cref="Succeeded"/> is the
    /// shape test every caller uses.
    /// </summary>
    private sealed record UserOutcome(
        string? GithubId,
        UserEntity? User,
        ActivatedUserSummary? Summary,
        UserDirectoryException? Refusal)
    {
        /// <summary>Whether the operation was applied (as opposed to refused).</summary>
        internal bool Succeeded => Refusal is null;

        /// <summary>The GitHub id the operation produced, whichever account shape it returned.</summary>
        internal string? ResultGithubId => User?.GitHubId ?? Summary?.GitHubId;
    }

    /// <summary>The captured outcome of one invitation-writing attempt (exactly one member is set).</summary>
    private sealed record InvitationOutcome(UserInvitationSummary? Invitation, UserDirectoryException? Refusal);

    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> over its OWN SQLite connection to the shared file.
    /// Two factories therefore hold two independent connections, so contention is real SQLite
    /// contention rather than shared in-process state.
    /// </summary>
    private sealed class IndependentDbContextFactory(string dbPath)
        : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            return new CopilotHiveDbContext(options);
        }
    }
}
