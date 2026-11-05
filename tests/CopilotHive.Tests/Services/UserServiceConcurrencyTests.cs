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
    /// <summary>
    /// The BOUNDED HANG-GUARD for every concurrent operation. A task that never completes — a
    /// deadlocked write transaction, a lock that is never released — fails the test as a NAMED
    /// failure instead of hanging the whole run forever. The bound is generous (the operations under
    /// test are milliseconds of SQLite work) so it can only trip on a genuine hang.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The FIXED clock every service in this file runs on. A controlled clock keeps the timestamps
    /// deterministic — no assertion in this file depends on elapsed wall-clock time — and proves
    /// the services do not reach for the system clock.
    /// </summary>
    private static readonly DateTimeOffset FixedNow = new(2033, 3, 3, 3, 3, 3, TimeSpan.Zero);

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

        // FILE-ONLY best-effort. The narrow catch is deliberate: deleting the temp database can
        // legitimately fail on a lock (IOException) or a permissions quirk (UnauthorizedAccessException),
        // and neither should fail a test that already made its assertions. Anything else — a
        // programming error, a malformed path — must still surface instead of being swallowed.
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
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
    /// in-process connection object. SQLite's write lock is what arbitrates between them. Every
    /// service runs on the SAME controlled clock, so the timestamps they write are deterministic.
    /// </summary>
    private UserService NewService() =>
        new(new IndependentDbContextFactory(_dbPath), NullLogger<UserService>.Instance, new FixedTimeProvider(FixedNow));

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// Runs two asynchronous operations started from a shared barrier and awaits BOTH, returning each
    /// side's outcome. A rendezvous (not a sleep) establishes that both were in flight. The two sides
    /// may have different result types — the outcomes are deliberately compared as outcomes, never by
    /// which task happened to finish first.
    /// <para>
    /// Both tasks are awaited under the BOUNDED hang-guard, and the guard applies to the WHOLE
    /// rendezvous: a side that never completes fails the test with a named timeout rather than
    /// hanging the run. Both tasks are always observed, so a failure on one side never leaves the
    /// other side unobserved.
    /// </para>
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

        // Await BOTH sides under the guard: a refusal on one side must not hide a hang on the other.
        await Task.WhenAll(firstTask, secondTask).WaitAsync(HangGuard);
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

    /// <summary>
    /// A MATCHING VIEWER INVITATION UNDER CONTENTION ALSO KEEPS THE BOOTSTRAP ACCOUNT ADMIN. The
    /// invitation's role is consumed but NEVER applied to the account, so the least-privileged
    /// matching invitation cannot demote the original admin.
    /// </summary>
    [Theory]
    [InlineData(UserRole.Viewer)]
    [InlineData(UserRole.Member)]
    public async Task BootstrapWithMatchingInvitationConcurrently_KeepsTheBootstrapAccountAdmin(UserRole invitationRole)
    {
        var seeding = NewService();
        var invitation = await seeding.InviteUserAsync("octocat", invitationRole, Ct);

        var serviceA = NewService();
        var serviceB = NewService();

        var (first, second) = await RunConcurrentlyAsync(
            () => serviceA.CreateOrUpdateUserAsync("100", "octocat", null, null, null, "token-a", null, null, Ct),
            () => serviceB.CreateOrUpdateUserAsync("100", "octocat", null, null, null, "token-b", null, null, Ct));

        Assert.Equal(first.Id, second.Id);

        await using var verification = NewContext();
        var account = Assert.Single(await verification.Users.AsNoTracking().ToListAsync(Ct));

        // The account is an ENABLED ADMIN regardless of the invitation's role.
        Assert.Equal(UserRole.Admin, account.Role);
        Assert.True(account.Enabled);

        // The invitation was consumed exactly once, into THAT account.
        var consumed = Assert.Single(await verification.UserInvitations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(invitation.Id, consumed.Id);
        Assert.Equal(account.Id, consumed.ActivatedUserId);
    }

    /// <summary>
    /// THE MATCHING-INVITATION CONSUMPTION AND THE ACCOUNT INSERT ARE ONE ATOMIC STEP: under
    /// contention, the invitation is NEVER left consumed without an account, and never activated
    /// into an account that does not exist. The database is read back for the FULL state, so a
    /// partial write would be visible.
    /// </summary>
    [Fact]
    public async Task BootstrapWithMatchingInvitationConcurrently_NeverConsumesTheInvitationWithoutAnAccount()
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
        var accounts = await verification.Users.AsNoTracking().ToListAsync(Ct);
        var invitations = await verification.UserInvitations.AsNoTracking().ToListAsync(Ct);

        var stored = Assert.Single(invitations);
        Assert.Equal(invitation.Id, stored.Id);

        if (stored.ActivatedUserId is null)
        {
            // Not consumed: the invitation is still pending — but then no account may claim it.
            Assert.True(stored.Enabled);
            Assert.Null(stored.ActivatedAt);
        }
        else
        {
            // Consumed: the referenced account MUST exist and carry the original id.
            Assert.NotNull(stored.ActivatedAt);
            Assert.Contains(accounts, u => u.Id == stored.ActivatedUserId);
            Assert.Equal(first.Id, stored.ActivatedUserId);
        }

        // Whatever happened, at most ONE account exists for the identity.
        Assert.True(accounts.Count <= 1);
    }

    /// <summary>
    /// ACTIVATION DUPLICATE DETECTION IS ATOMIC WHEN BOTH SIDES USE THE SAME GITHUB ID: two
    /// separate service instances activate TWO DIFFERENT invitations that carry the SAME GitHub
    /// numeric id. At most ONE account can exist for that id, so one side must be refused and the
    /// refused side must leave NO row behind.
    /// </summary>
    [Fact]
    public async Task Activation_SameGitHubIdAcrossDifferentInvitationsConcurrently_ProducesOneAccount()
    {
        var seeding = NewService();
        await seeding.CreateOrUpdateUserAsync("1", "original", null, null, null, "original-token", null, null, Ct);
        await seeding.InviteUserAsync("first-invitee", UserRole.Member, Ct);
        await seeding.InviteUserAsync("second-invitee", UserRole.Viewer, Ct);

        var serviceA = NewService();
        var serviceB = NewService();

        var (first, second) = await RunConcurrentlyAsync(
            () => CaptureActivation("200", () => serviceA.ActivateInvitationAsync("first-invitee", "200", null, null, null, Ct)),
            () => CaptureActivation("200", () => serviceB.ActivateInvitationAsync("second-invitee", "200", null, null, null, Ct)));

        var winners = new[] { first, second }.Where(o => o.Summary is not null).ToList();
        var losers = new[] { first, second }.Where(o => o.Refusal is not null).ToList();
        Assert.Single(winners);
        Assert.Single(losers);
        Assert.IsType<UserDirectoryException>(losers[0].Refusal);

        // The winner is the side whose account was actually created.
        var winnerSummary = winners[0].Summary!;
        var loserSummary = losers[0].Summary;
        Assert.Null(loserSummary);

        await using var verification = NewContext();
        var accounts = await verification.Users.AsNoTracking().ToListAsync(Ct);

        // EXACTLY ONE account carries the shared GitHub id — the UNIQUE index arbitrated.
        var bound = Assert.Single(accounts, u => u.GitHubId == "200");
        Assert.Equal(winnerSummary.Id, bound.Id);
        Assert.Equal(2, accounts.Count);

        // The loser's invitation was NOT consumed — the refusal commits nothing.
        var invitations = await verification.UserInvitations.AsNoTracking().ToListAsync(Ct);
        var consumed = invitations.Where(i => i.ActivatedUserId is not null).ToList();
        Assert.Single(consumed);
        Assert.Equal(bound.Id, consumed[0].ActivatedUserId);

        // Every invitation that WAS consumed points at a real, existing account.
        foreach (var invitation in invitations)
        {
            if (invitation.ActivatedUserId is not null)
                Assert.Contains(accounts, u => u.Id == invitation.ActivatedUserId);
        }
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
    /// THE BOOTSTRAP RACE, DETERMINISTICALLY STAGED: the second identity is released only AFTER the
    /// first has COMMITTED, so the "both in flight" ordering is not left to scheduling luck. The
    /// invariant is the same — exactly ONE original account, and the late loser is REJECTED and never
    /// inserted — but it is now proven for a precisely known interleaving as well.
    /// <para>
    /// The gate is a task completion, not a sleep: the test thread awaits the first operation's
    /// completion and only then releases the second.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bootstrap_LateDistinctIdentity_IsRejectedAndNeverInserted()
    {
        var serviceA = NewService();
        var serviceB = NewService();

        // The first identity commits on its own.
        var winner = await serviceA.CreateOrUpdateUserAsync(
            "100", "alpha", null, null, null, "token-alpha", null, null, Ct);

        // The second, DISTINCT identity arrives afterwards and is refused.
        var refusal = await Assert.ThrowsAsync<UserDirectoryException>(
            () => serviceB.CreateOrUpdateUserAsync(
                "200", "beta", null, null, null, "token-beta", null, null, Ct));

        Assert.Contains("Only the original activated account may sign in", refusal.Message);

        await using var verification = NewContext();
        var accounts = await verification.Users.AsNoTracking().ToListAsync(Ct);

        // EXACTLY ONE account: the loser's row was never inserted.
        var account = Assert.Single(accounts);
        Assert.Equal(winner.Id, account.Id);
        Assert.Equal("100", account.GitHubId);
        Assert.Equal("token-alpha", account.AccessToken);
        Assert.Equal(UserRole.Admin, account.Role);

        // The loser's identity has no row and no credential.
        Assert.DoesNotContain(accounts, u => u.GitHubId == "200");
        Assert.Null(await serviceB.GetByGitHubIdAsync("200", Ct));

        // The global token is still the original account's.
        Assert.Equal("token-alpha", await serviceA.GetActiveAccessTokenAsync(Ct));
    }

    /// <summary>
    /// TWO CONCURRENT OPERATIONS THAT WOULD EACH REMOVE AN ENABLED ADMIN CANNOT BOTH SUCCEED when only
    /// two enabled Admins exist: the database serializer makes the second one observe the first's
    /// committed change, so EXACTLY ONE enabled Admin always survives.
    /// <para>
    /// The refusal is asserted to be the LAST-ADMIN GUARD specifically (not "some directory
    /// refusal"), and the survivor is asserted to be the account whose operation was refused — so a
    /// guard that refused for an unrelated reason, or a lost update where both writes landed, both
    /// fail here.
    /// </para>
    /// <para>
    /// A PENDING Admin invitation exists throughout, proving it does not satisfy the protection.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LastAdminProtection_ConcurrentRemovalsOfTwoAdmins_LeavesExactlyTheRefusedAdminsEnabled()
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
            .Where(u => u.Role == UserRole.Admin && u.Enabled)
            .ToListAsync(Ct);

        // EXACTLY ONE enabled Admin survives: not zero (the guard held), not two (a write was lost).
        var survivor = Assert.Single(enabledAdmins);

        // EXACTLY ONE operation applied and the other was REFUSED BY THE LAST-ADMIN GUARD — a refusal
        // for any other reason would mean the guard itself was never exercised.
        var applied = new[] { demote, disable }.Where(o => o.Succeeded).ToList();
        var refused = new[] { demote, disable }.Where(o => o.Refusal is not null).ToList();
        Assert.Single(applied);
        Assert.Single(refused);

        var refusal = refused[0].Refusal!;
        Assert.IsType<UserDirectoryException>(refusal);
        Assert.Contains("last enabled Admin", refusal.Message);
        Assert.Contains(
            "pending Admin invitation does not count as an enabled Admin",
            refusal.Message);

        // The survivor is the account whose operation was REFUSED — the other one really did change,
        // so this is not a pair of no-ops that happened to leave the state alone.
        Assert.Equal(refused[0] == demote ? original.Id : second.Id, survivor.Id);

        // The applied side's change is really persisted (the complement of the survivor).
        var changed = await verification.Users.AsNoTracking()
            .SingleAsync(u => u.Id != survivor.Id, Ct);
        Assert.True(changed.Role != UserRole.Admin || !changed.Enabled);

        // The pending invitation was never touched by either path.
        var pending = await verification.UserInvitations.AsNoTracking()
            .SingleAsync(i => i.NormalizedUsername == "pending-admin", Ct);
        Assert.Null(pending.ActivatedUserId);
        Assert.True(pending.Enabled);
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
            .Where(u => u.Role == UserRole.Admin && u.Enabled)
            .ToListAsync(Ct);

        // EXACTLY ONE enabled Admin survives, and it is the account whose disable was REFUSED — so a
        // refusal for an unrelated reason, or both disables landing, cannot pass.
        var survivor = Assert.Single(enabledAdmins);
        var refused = new[] { first, secondResult }.Single(o => o.Refusal is not null);
        Assert.Single(new[] { first, secondResult }, o => o.Succeeded);

        Assert.Contains("last enabled Admin", refused.Refusal!.Message);
        Assert.Equal(refused == first ? original.Id : second.Id, survivor.Id);

        // The other account really is disabled — the guard refused a genuine change.
        var disabled = await verification.Users.AsNoTracking().SingleAsync(u => u.Id != survivor.Id, Ct);
        Assert.False(disabled.Enabled);
    }

    // ── Normalized-login uniqueness under contention ─────────────────────────

    /// <summary>
    /// A RENAME RACING AN INVITATION FOR THE SAME NORMALIZED LOGIN NEVER PRODUCES TWO CLAIMS ON
    /// THAT LOGIN. The normalized-login uniqueness rule is a SHARED invariant across activated
    /// accounts and invitations, and both sides write, so the database serialises them. Whichever
    /// side commits first, the other must observe it: either the invitation is refused because the
    /// account now owns the login, or the rename is refused because the invitation reserves it.
    /// <para>
    /// The assertion is on the INVARIANT, never on which side won: at most one activated account and
    /// at most one invitation may hold the normalized login "bob", and the two must never BOTH hold
    /// it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RenameAndInviteConcurrently_ForTheSameNormalizedLogin_NeverProduceTwoClaimsOnIt()
    {
        var seeding = NewService();
        var original = await seeding.CreateOrUpdateUserAsync(
            "1", "alice", null, null, null, "original-token", null, null, Ct);

        var renameService = NewService();
        var inviteService = NewService();

        var (rename, invite) = await RunConcurrentlyAsync(
            () => CaptureBootstrap(() => renameService.BootstrapOrRefreshAsync(
                "1", "bob", null, null, null, "rotated-token", null, null, Ct)),
            () => CaptureInvitation(() => inviteService.InviteUserAsync("bob", UserRole.Member, Ct)));

        await using var verification = NewContext();

        var accounts = await verification.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(Ct);
        var invitations = await verification.UserInvitations.AsNoTracking().ToListAsync(Ct);

        // THE INVARIANT: the normalized login "bob" is claimed by AT MOST ONE activated account...
        var accountsHoldingBob = accounts
            .Where(u => UserNormalization.NormalizedEquals(u.Username, "bob"))
            .ToList();
        Assert.True(accountsHoldingBob.Count <= 1);

        // ...and by AT MOST ONE invitation, and NEVER by both at once.
        var invitationsHoldingBob = invitations
            .Where(i => i.NormalizedUsername == "bob")
            .ToList();
        Assert.True(invitationsHoldingBob.Count <= 1);
        Assert.False(accountsHoldingBob.Count == 1 && invitationsHoldingBob.Count == 1);

        // The ORIGINAL account is never lost, and it always keeps its GitHub id.
        var stored = await verification.Users.AsNoTracking().SingleAsync(u => u.Id == original.Id, Ct);
        Assert.Equal("1", stored.GitHubId);

        // The sign-in was NEVER rejected — the original's sign-in is anchored to its GitHub id.
        Assert.NotNull(rename.Outcome);
        Assert.Equal(original.Id, rename.Outcome!.User.Id);

        // Exactly one of the two sides applied and the other was refused — never both, never neither.
        var renameApplied = rename.Outcome!.UsernameConflict is null
            && string.Equals(rename.Outcome.User.Username, "bob", StringComparison.Ordinal);
        var inviteApplied = invite.Invitation is not null;
        Assert.True(
            renameApplied ^ inviteApplied,
            $"Expected exactly one side to claim 'bob'; renameApplied={renameApplied} " +
            $"inviteApplied={inviteApplied} conflict={rename.Outcome.UsernameConflict}");

        // If the rename applied, the invitation must have been refused with a clear message.
        if (renameApplied)
        {
            Assert.NotNull(invite.Refusal);
            Assert.IsType<UserDirectoryException>(invite.Refusal);
        }
        else
        {
            // The rename was refused: the invitation holds the login and the original kept its name.
            Assert.NotNull(rename.Outcome.UsernameConflict);
            Assert.NotNull(rename.Outcome.UsernameConflict!.ConflictingInvitationId);
            Assert.Equal("alice", stored.Username);

            // The invitation that holds the login is the one the conflict names, and it is still
            // pending — nothing was consumed.
            var holding = await verification.UserInvitations.AsNoTracking()
                .SingleAsync(i => i.Id == rename.Outcome.UsernameConflict!.ConflictingInvitationId, Ct);
            Assert.Equal("bob", holding.NormalizedUsername);
            Assert.Null(holding.ActivatedUserId);
        }
    }

    /// <summary>
    /// A RENAME RACING AN ACTIVATION NEVER HIDES THE ACTIVATED ACCOUNT. The activated account must
    /// stay resolvable by its own login, and the original must keep a login that resolves to it —
    /// whichever order the database commits them in.
    /// </summary>
    [Fact]
    public async Task RenameAndActivationConcurrently_NeverHideTheActivatedAccount()
    {
        var seeding = NewService();
        var original = await seeding.CreateOrUpdateUserAsync(
            "1", "alice", null, null, null, "original-token", null, null, Ct);
        await seeding.InviteUserAsync("bob", UserRole.Member, Ct);

        var renameService = NewService();
        var activationService = NewService();

        var (rename, activation) = await RunConcurrentlyAsync(
            () => CaptureBootstrap(() => renameService.BootstrapOrRefreshAsync(
                "1", "bob", null, null, null, "rotated-token", null, null, Ct)),
            () => CaptureActivation("200", () => activationService.ActivateInvitationAsync(
                "bob", "200", null, null, null, Ct)));

        await using var verification = NewContext();
        var accounts = await verification.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(Ct);

        // THE INVARIANT: the normalized login "bob" is held by AT MOST ONE activated account.
        var holders = accounts
            .Where(u => UserNormalization.NormalizedEquals(u.Username, "bob"))
            .ToList();
        Assert.True(holders.Count <= 1);

        // The original account always survives with its GitHub id.
        var storedOriginal = Assert.Single(accounts, u => u.Id == original.Id);
        Assert.Equal("1", storedOriginal.GitHubId);

        // The sign-in was never rejected.
        Assert.NotNull(rename.Outcome);
        Assert.Equal(original.Id, rename.Outcome!.User.Id);

        // If the activation bound the login, the original must have kept its own login and the
        // activated account must be resolvable by "bob".
        if (activation.Summary is not null)
        {
            var activated = Assert.Single(holders);
            Assert.Equal(activation.Summary.Id, activated.Id);
            Assert.NotEqual(original.Id, activated.Id);
            Assert.Equal("alice", storedOriginal.Username);

            // ...and it really is resolvable through the directory resolver.
            var service = NewService();
            Assert.Equal(activated.Id, (await service.GetActivatedUserByUsernameAsync("bob", Ct))!.Id);
        }
        else
        {
            // The activation was refused (the rename took the login first): the invitation stays
            // pending and the original holds the login.
            Assert.NotNull(activation.Refusal);
            Assert.Single(holders);
            Assert.Equal(original.Id, holders[0].Id);
            var invitation = await verification.UserInvitations.AsNoTracking()
                .SingleAsync(i => i.NormalizedUsername == "bob", Ct);
            Assert.Null(invitation.ActivatedUserId);
        }
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
    /// Captures the outcome of one BOOTSTRAP/REFRESH attempt, keeping the full outcome so the
    /// reported username conflict is observable. A refresh of the ORIGINAL account must never be
    /// rejected, so this helper records a refusal only if one actually happened.
    /// </summary>
    private static async Task<BootstrapOutcome> CaptureBootstrap(Func<Task<UserBootstrapOutcome>> operation)
    {
        try
        {
            return new BootstrapOutcome(await operation(), null);
        }
        catch (UserDirectoryException ex)
        {
            return new BootstrapOutcome(null, ex);
        }
    }

    /// <summary>
    /// The captured outcome of one bootstrap/refresh attempt: the full outcome (when it succeeded) or
    /// the directory refusal (when it did not).
    /// </summary>
    private sealed record BootstrapOutcome(
        UserBootstrapOutcome? Outcome,
        UserDirectoryException? Refusal);

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

    /// <summary>
    /// A fully manual clock pinned to one instant, so every timestamp written under contention is
    /// deterministic. No assertion in this file depends on elapsed time — the guard is there to keep
    /// the written values reproducible, not to measure anything.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
