using CopilotHive.Security;
using CopilotHive.Services;

namespace CopilotHive.Tests.Security;

/// <summary>
/// THE OUTCOME MATRIX of <see cref="HivePrincipalResolver.ResolveByUserIdAsync(int, CancellationToken)"/>:
/// one discriminating test per outcome, so every row of the matrix is observable on its own.
/// <para>
/// The resolver is the ONLY identity source the Composer authorization path uses, so each case reads
/// the REAL <see cref="UserService"/> over a real SQLite database (see <see cref="HiveSecurityTestDirectory"/>).
/// A refusal must carry NO principal — that is what makes "refused" different from "allowed with a
/// reduced role", and each case asserts it explicitly.
/// </para>
/// <para>
/// CANCELLATION IS NOT A REFUSAL: a cancelled caller is the caller's own signal, so it propagates as
/// an <see cref="OperationCanceledException"/> and never becomes an authorization answer.
/// </para>
/// </summary>
public sealed class HivePrincipalResolverByUserIdMatrixTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// OUTCOME 1 — ACTIVATED ACCOUNT: the resolution succeeds and the principal is derived from the
    /// CURRENT persisted row: the id, the GitHub identity, the persisted role, the enabled flag, and
    /// the ORIGINALITY flag derived from the <c>GetAdminUserAsync</c> id comparison.
    /// </summary>
    [Fact]
    public async Task Outcome_ActivatedAccount_ResolvesWithDerivedRoleIdEnabledAndOriginality()
    {
        using var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("4242", UserRole.Admin, username: "original");
        await directory.SeedActivatedAccountAsync("4343", UserRole.Viewer, username: "later");

        var resolution = await directory.AuthEnabledResolver().ResolveByUserIdAsync(original.Id, Ct);

        Assert.True(resolution.IsResolved);
        Assert.Equal(PrincipalRefusalReason.None, resolution.RefusalReason);

        var principal = Assert.IsType<ActingUserPrincipal>(resolution.Principal);
        Assert.Equal(original.Id, principal.UserId);
        Assert.Equal("4242", principal.GitHubId);
        Assert.Equal(UserRole.Admin, principal.Role);
        Assert.True(principal.Enabled);
        // Originality is DERIVED, never asserted from the caller's own knowledge.
        Assert.True(principal.IsOriginal);
        Assert.False(principal.IsLocalOperator);
    }

    /// <summary>
    /// OUTCOME 1b — A LATER ACCOUNT IS NOT ORIGINAL: the originality flag is exactly the
    /// <c>GetAdminUserAsync</c> id comparison, so the second activated account resolves with
    /// <c>IsOriginal == false</c> even though it is fully enabled and activated.
    /// </summary>
    [Fact]
    public async Task Outcome_LaterActivatedAccount_ResolvesWithoutOriginality()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("4242", UserRole.Admin, username: "original");
        var later = await directory.SeedActivatedAccountAsync("4343", UserRole.Member, username: "later");

        var resolution = await directory.AuthEnabledResolver().ResolveByUserIdAsync(later.Id, Ct);

        Assert.True(resolution.IsResolved);
        var principal = Assert.IsType<ActingUserPrincipal>(resolution.Principal);
        Assert.False(principal.IsOriginal);
        Assert.Equal(UserRole.Member, principal.Role);
    }

    /// <summary>
    /// OUTCOME 2 — MISSING ACCOUNT: an id with no persisted row is refused as an unknown account and
    /// carries NO principal. It is never turned into a fresh/default identity.
    /// </summary>
    [Fact]
    public async Task Outcome_MissingAccount_RefusesWithNoPrincipal()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("4242", UserRole.Admin);

        var resolution = await directory.AuthEnabledResolver().ResolveByUserIdAsync(9999, Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.UnknownAccount, resolution.RefusalReason);
    }

    /// <summary>
    /// OUTCOME 3 — DISABLED ACCOUNT: a disabled row is refused and carries NO principal, even when the
    /// account is the ORIGINAL/bootstrap one (originality never overrides disabled state).
    /// </summary>
    [Fact]
    public async Task Outcome_DisabledAccount_RefusesWithNoPrincipal()
    {
        using var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("4242", UserRole.Admin, username: "original");
        await directory.SetEnabledAsync("4242", enabled: false);

        var resolution = await directory.AuthEnabledResolver().ResolveByUserIdAsync(original.Id, Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DisabledAccount, resolution.RefusalReason);
    }

    /// <summary>
    /// OUTCOME 4 — UNKNOWN PERSISTED ROLE: the row's role text is not a supported role, so the
    /// resolution is refused as <see cref="PrincipalRefusalReason.UnknownRole"/> — the SAME
    /// classification the claims path produces — and carries NO principal. It is never defaulted to
    /// Admin or to any other role.
    /// </summary>
    /// <param name="roleText">The unsupported persisted role text.</param>
    [Theory]
    [InlineData("superuser")]
    [InlineData("2")]
    [InlineData("")]
    public async Task Outcome_UnknownPersistedRole_RefusesAsUnknownRoleWithNoPrincipal(string roleText)
    {
        using var directory = new HiveSecurityTestDirectory();
        directory.SeedUnsupportedPersistedRole("4242", roleText);

        var resolution = await directory.AuthEnabledResolver().ResolveByUserIdAsync(1, Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.UnknownRole, resolution.RefusalReason);
    }

    /// <summary>
    /// OUTCOME 5 — DIRECTORY FAILURE: a directory that cannot be read proves nothing, so the
    /// resolution is a <see cref="PrincipalRefusalReason.DirectoryFailure"/> refusal carrying NO
    /// principal. It is never thrown at the caller and never treated as a success.
    /// </summary>
    [Fact]
    public async Task Outcome_DirectoryFailure_RefusesWithNoPrincipal()
    {
        var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("4242", UserRole.Admin);
        var resolver = directory.AuthEnabledResolver();

        // The directory is gone: every later read fails.
        directory.Dispose();

        var resolution = await resolver.ResolveByUserIdAsync(original.Id, Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DirectoryFailure, resolution.RefusalReason);
    }

    /// <summary>
    /// OUTCOME 6 — CALLER CANCELLATION PROPAGATES: cancellation is the caller's own signal, never an
    /// authorization answer. The resolver must rethrow it rather than converting it into a refusal
    /// (which would silently turn "the caller went away" into "the account is not allowed").
    /// </summary>
    [Fact]
    public async Task Outcome_CancelledCaller_PropagatesInsteadOfRefusing()
    {
        using var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("4242", UserRole.Admin);
        var resolver = directory.AuthEnabledResolver();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveByUserIdAsync(original.Id, cts.Token));

        // The cancellation keeps its IDENTITY (the caller's token), not just its type.
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    /// <summary>
    /// OUTCOME 6b — CANCELLATION IS NOT CONVERTED INTO A REFUSAL AT THE AUTHORIZATION LAYER EITHER:
    /// the central helper rethrows the caller's cancellation instead of answering "denied", because a
    /// refusal is an authorization verdict and a cancelled caller never asked for one.
    /// </summary>
    [Fact]
    public async Task Outcome_CancelledCaller_IsNotConvertedIntoARefusalByTheAuthorizationHelper()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("4242", UserRole.Admin, username: "original");
        var helper = new ComposerToolAuthorization(
            directory.AuthEnabledResolver(),
            new HivePolicy(),
            directory.Service,
            HiveAuthMode.AuthenticationEnabled,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ComposerToolAuthorization>.Instance);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => helper.AuthorizeAsync(1, "list_goals", arguments: null, cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    /// <summary>
    /// OUTCOME 7 — A NON-POSITIVE ID IS REFUSED WITHOUT A LOOKUP: the activated-account projection
    /// cannot carry such a row, so the id is refused as an unknown account and no principal is
    /// produced.
    /// </summary>
    /// <param name="userId">The non-positive account id.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    [InlineData(int.MinValue)]
    public async Task Outcome_NonPositiveId_RefusesWithNoPrincipal(int userId)
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("4242", UserRole.Admin);

        var resolution = await directory.AuthEnabledResolver().ResolveByUserIdAsync(userId, Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.UnknownAccount, resolution.RefusalReason);
    }
}
