using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Covers the OAuth ADMISSION contract that the multi-user foundation must NOT change: only the
/// ORIGINAL/bootstrap activated identity is admitted, and every other identity is rejected.
/// <para>
/// The <c>OnCreatingTicket</c> handler lives in <c>Program.cs</c> as a lambda that cannot be
/// invoked without booting the host, so this file proves the contract in TWO complementary ways:
/// </para>
/// <list type="number">
/// <item>
/// <b>BEHAVIOUR</b>: the handler's decision procedure — read the activated-account count, compare
/// the signing-in GitHub id against the original account's, and refuse anything else — is
/// reproduced against the REAL <see cref="UserService"/> and its REAL database. This is what
/// proves an invited Admin (or any other invited identity) is never admitted.
/// </item>
/// <item>
/// <b>STRUCTURE</b>: <c>Program.cs</c>'s handler is inspected to prove it actually delegates to
/// those same calls and to the failure path — so the behavioural proof above is anchored to the
/// production handler rather than to a re-implementation of it.
/// </item>
/// </list>
/// </summary>
public sealed class ProgramOAuthAdmissionTests
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
    /// The production failure text the OAuth handler uses to reject a non-original identity.
    /// </summary>
    private const string AdmissionFailureMessage = "Only one user (admin) is allowed in this version.";

    /// <summary>
    /// THE ADMISSION DECISION, exactly as <c>Program.cs</c>'s <c>OnCreatingTicket</c> handler makes
    /// it: the count of ACTIVATED accounts decides whether this is the bootstrap, and afterwards
    /// only the ORIGINAL account's GitHub id is admitted.
    /// </summary>
    private enum AdmissionOutcome
    {
        /// <summary>The identity was admitted (the bootstrap, or the original account signing in again).</summary>
        Admitted,

        /// <summary>The identity was rejected — the production handler calls <c>context.Fail(...)</c>.</summary>
        Rejected,
    }

    /// <summary>
    /// Reproduces the production admission procedure against the real service: the count read, the
    /// original-account comparison and the sign-in seam, with the handler's failure branch mapped
    /// onto a <see cref="UserDirectoryException"/> (the refusal the service raises for a distinct
    /// identity).
    /// </summary>
    private static async Task<AdmissionOutcome> AdmitAsync(
        UserService service,
        string githubId,
        string username,
        string token)
    {
        var userCount = await service.GetUserCountAsync(Ct);
        if (userCount == 0)
        {
            await service.CreateOrUpdateUserAsync(
                githubId, username, null, null, null, token, null, null, Ct);
            return AdmissionOutcome.Admitted;
        }

        var admin = await service.GetAdminUserAsync(Ct);
        if (admin is not null && admin.GitHubId == githubId)
        {
            await service.CreateOrUpdateUserAsync(
                githubId, username, null, null, null, token, null, null, Ct);
            return AdmissionOutcome.Admitted;
        }

        return AdmissionOutcome.Rejected;
    }

    // ── Behaviour: the admission contract ───────────────────────────────────

    /// <summary>
    /// THE FIRST IDENTITY IS ADMITTED AND BOOTSTRAPS THE ORIGINAL ACCOUNT — the count read is 0, so
    /// the sign-in seam creates the single original account.
    /// </summary>
    [Fact]
    public async Task Admission_OnAFreshInstallation_AdmitsTheFirstIdentityAsTheOriginalAccount()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var outcome = await AdmitAsync(service, "111", "original", "original-token");

        Assert.Equal(AdmissionOutcome.Admitted, outcome);
        Assert.Equal(1, await service.GetUserCountAsync(Ct));
        Assert.Equal("original-token", await service.GetActiveAccessTokenAsync(Ct));
    }

    /// <summary>
    /// THE ORIGINAL IDENTITY IS ADMITTED AGAIN AFTER BOOTSTRAP — a returning admin is refreshed in
    /// place, and the account count does not grow.
    /// </summary>
    [Fact]
    public async Task Admission_AfterBootstrap_AdmitsTheOriginalIdentityAgain()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await AdmitAsync(service, "111", "original", "original-token");

        var outcome = await AdmitAsync(service, "111", "original", "rotated-token");

        Assert.Equal(AdmissionOutcome.Admitted, outcome);
        Assert.Equal(1, await service.GetUserCountAsync(Ct));
        Assert.Equal("rotated-token", await service.GetActiveAccessTokenAsync(Ct));
    }

    /// <summary>
    /// EVERY NON-ORIGINAL IDENTITY IS REJECTED AFTER BOOTSTRAP. The identity is the GitHub NUMERIC
    /// id, so any other numeric id is a different person and must be refused — including one that
    /// arrives with the original's own login text.
    /// </summary>
    [Theory]
    [InlineData("222", "intruder")]
    [InlineData("222", "original")]
    [InlineData("112", "intruder")]
    public async Task Admission_AfterBootstrap_RejectsEveryNonOriginalIdentity(string githubId, string username)
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await AdmitAsync(service, "111", "original", "original-token");

        var outcome = await AdmitAsync(service, githubId, username, "intruder-token");

        Assert.Equal(AdmissionOutcome.Rejected, outcome);

        // The rejected identity was NEVER inserted, and the original's credential is untouched.
        Assert.Equal(1, await service.GetUserCountAsync(Ct));
        Assert.Null(await service.GetByGitHubIdAsync(githubId, Ct));
        Assert.Equal("original-token", await service.GetActiveAccessTokenAsync(Ct));
    }

    /// <summary>
    /// THE SAME IDENTITY WITH A RENAMED LOGIN IS STILL THE ORIGINAL: the admission comparison is on
    /// the GitHub NUMERIC id, so a renamed account is refreshed in place rather than refused, and
    /// the account count does not grow.
    /// </summary>
    [Fact]
    public async Task Admission_AfterBootstrap_AdmitsTheOriginalIdentityUnderANewLogin()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await AdmitAsync(service, "111", "original", "original-token");

        var outcome = await AdmitAsync(service, "111", "original-renamed", "rotated-token");

        Assert.Equal(AdmissionOutcome.Admitted, outcome);
        Assert.Equal(1, await service.GetUserCountAsync(Ct));

        var stored = await service.GetAdminUserAsync(Ct);
        Assert.NotNull(stored);
        Assert.Equal("original-renamed", stored!.Username);
        Assert.Equal("rotated-token", stored.AccessToken);
    }

    /// <summary>
    /// AN INVITED ADMIN IS NEVER ADMITTED — not before it is activated, and not after. The
    /// invitation table is not an account table, so the count read stays 0 and the FIRST sign-in is
    /// still the only one admitted; after activation the invited account exists but is NOT the
    /// original, so its own sign-in is refused.
    /// </summary>
    [Fact]
    public async Task Admission_AnInvitedAdminIsNeverAdmitted_BeforeOrAfterActivation()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        // A pending Admin invitation: no account exists, so the count read is 0.
        await service.InviteUserAsync("invitedadmin", UserRole.Admin, Ct);
        Assert.Equal(0, await service.GetUserCountAsync(Ct));

        // The original admin signs in first and is admitted.
        Assert.Equal(AdmissionOutcome.Admitted, await AdmitAsync(service, "111", "original", "original-token"));

        // The invited Admin is activated — it now EXISTS, but it is not the ORIGINAL account.
        var activated = await service.ActivateInvitationAsync("invitedadmin", "222", null, null, null, Ct);

        // Its own sign-in is REFUSED: only the original identity is admitted.
        Assert.Equal(AdmissionOutcome.Rejected, await AdmitAsync(service, "222", "invitedadmin", "invited-token"));

        // The invitation did not take over the global token.
        Assert.Equal("original-token", await service.GetActiveAccessTokenAsync(Ct));
        var original = await service.GetAdminUserAsync(Ct);
        Assert.NotNull(original);
        Assert.NotEqual(activated.Id, original!.Id);
        Assert.Equal("111", original.GitHubId);
    }

    // ── Structure: the production handler really delegates to those calls ────

    /// <summary>
    /// The production <c>OnCreatingTicket</c> handler reads the ACTIVATED-account count, compares
    /// the signing-in GitHub id against the ORIGINAL account's, and REFUSES everything else. This
    /// anchors the behavioural proof above to the real handler: without these calls the behaviour
    /// under test would not be the production behaviour.
    /// </summary>
    [Fact]
    public void ProgramCs_OnCreatingTicket_AdmitsOnlyTheOriginalIdentity()
    {
        var handler = ExtractOnCreatingTicketHandler();

        Assert.NotNull(handler);
        Assert.Contains("GetUserCountAsync", handler, StringComparison.Ordinal);
        Assert.Contains("GetAdminUserAsync", handler, StringComparison.Ordinal);
        Assert.Contains("admin.GitHubId == githubId", handler, StringComparison.Ordinal);
        Assert.Contains("context.Fail(", handler, StringComparison.Ordinal);
        Assert.Contains(AdmissionFailureMessage, handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// The production handler NEVER consults the invitation directory: an invitation is not an
    /// account, so no invitation can influence who is admitted. The invitation table and the
    /// invitation operations are absent from the handler body.
    /// </summary>
    [Fact]
    public void ProgramCs_OnCreatingTicket_NeverConsultsTheInvitationDirectory()
    {
        var handler = ExtractOnCreatingTicketHandler();

        Assert.NotNull(handler);
        Assert.DoesNotContain("Invitation", handler, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ActivateInvitationAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ListInvitationsAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("GetInvitationByUsernameAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("UserRole", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// The production handler's bootstrap branch calls the ATOMIC sign-in seam
    /// (<c>CreateOrUpdateUserAsync</c>) — not a read-then-write of its own — so the atomic
    /// bootstrap semantics apply to the real admission path.
    /// </summary>
    [Fact]
    public void ProgramCs_OnCreatingTicket_DelegatesToTheAtomicBootstrapSeam()
    {
        var handler = ExtractOnCreatingTicketHandler();

        Assert.NotNull(handler);
        Assert.Equal(2, TestSourceScanner.CountOccurrences(handler, "CreateOrUpdateUserAsync"));
    }

    /// <summary>
    /// Extracts the body of <c>Program.cs</c>'s <c>OnCreatingTicket</c> handler. The marker is
    /// unique in the file, and the brace matching is driven by the STRING-AWARE mask, so a brace
    /// inside a literal cannot end the block early.
    /// </summary>
    private static string? ExtractOnCreatingTicketHandler()
    {
        var programPath = Path.Combine(TestRepoRoot.Find(), "src", "CopilotHive", "Program.cs");
        Assert.True(File.Exists(programPath), $"Program.cs not found at {programPath}");

        var source = File.ReadAllText(programPath).ReplaceLineEndings("\n");

        return TestSourceScanner.ExtractMethodBlockPreservingLiterals(
            source,
            "options.Events.OnCreatingTicket = async context =>");
    }
}
