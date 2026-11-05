using System.Security.Claims;

using CopilotHive.Security;
using CopilotHive.Services;

namespace CopilotHive.Tests.Security;

/// <summary>
/// Covers the AUTH-ENABLED ClaimsPrincipal resolver and the explicit open-mode branch: the GitHub
/// <see cref="ClaimTypes.NameIdentifier"/> claim is the only claim consulted (as an identity, never as
/// authorization truth), the account is re-read from the real directory on every resolution, and every
/// unresolvable case fails CLOSED with an explicit refusal that carries no principal.
/// <para>
/// All directory reads go through the production <see cref="UserService"/> over the production EF
/// model, so the tests prove the resolver uses the required trusted lookups rather than a stand-in.
/// </para>
/// </summary>
public sealed class HivePrincipalResolverTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// A signed-in identity backed by an activated account resolves to that account's CURRENT persisted
    /// role — the forged Admin role claim carried by the cookie has no effect.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_AuthenticatedIdentity_ResolvesPersistedRoleAndIgnoresCookieRoleClaim()
    {
        using var directory = new HiveSecurityTestDirectory();
        var account = await directory.SeedActivatedAccountAsync("4242", UserRole.Viewer);

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("4242"), Ct);

        Assert.True(resolution.IsResolved);
        Assert.Equal(PrincipalRefusalReason.None, resolution.RefusalReason);
        var principal = Assert.IsType<ActingUserPrincipal>(resolution.Principal);
        Assert.Equal(account.Id, principal.UserId);
        Assert.Equal("4242", principal.GitHubId);
        Assert.Equal(UserRole.Viewer, principal.Role);
        Assert.True(principal.Enabled);
        Assert.False(principal.IsLocalOperator);
    }

    /// <summary>
    /// ORIGINALITY IS DERIVED, NOT CLAIMED: the lowest-id activated account is the original, a later
    /// activated account is not. The id comparison is the only input.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_OriginalIsTheLowestIdActivatedAccount_AndLaterAccountsAreNot()
    {
        using var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        var invited = await directory.SeedActivatedAccountAsync("2", UserRole.Member, username: "invited");

        var originalResolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), Ct);
        var invitedResolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("2"), Ct);

        Assert.True(originalResolution.Principal!.IsOriginal);
        Assert.Equal(original.Id, originalResolution.Principal.UserId);

        Assert.False(invitedResolution.Principal!.IsOriginal);
        Assert.Equal(invited.Id, invitedResolution.Principal.UserId);
    }

    /// <summary>
    /// ROLE AND ENABLED STATE ARE READ FRESH: an admin demoted to Viewer between two resolutions
    /// resolves as a Viewer on the second call, with the SAME resolver instance (a singleton in
    /// production, so no cached state is allowed).
    /// </summary>
    [Fact]
    public async Task ResolveAsync_DemotedAccount_ResolvesTheNewRoleOnTheNextCall()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var resolver = directory.AuthEnabledResolver();
        var claimsPrincipal = HiveSecurityTestDirectory.GitHubPrincipal("1");

        var before = await resolver.ResolveAsync(claimsPrincipal, Ct);
        Assert.Equal(UserRole.Admin, before.Principal!.Role);

        await directory.SetRoleAsync("1", UserRole.Viewer);

        var after = await resolver.ResolveAsync(claimsPrincipal, Ct);
        Assert.Equal(UserRole.Viewer, after.Principal!.Role);
    }

    /// <summary>
    /// A DISABLED account is REFUSED. The role claim (Admin) and the account's own former role never
    /// survive it, and the refusal carries no principal at all.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_DisabledAccount_IsRefusedWithNoPrincipal()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, enabled: false);

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DisabledAccount, resolution.RefusalReason);
    }

    /// <summary>
    /// THE ORIGINAL IDENTITY NEVER OVERRIDES DISABLED STATE: the bootstrap account itself is refused
    /// while disabled, so no "original" privilege path exists.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_DisabledOriginalAccount_IsRefusedLikeAnyOtherAccount()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        await directory.SeedActivatedAccountAsync("2", UserRole.Member, username: "invited");
        await directory.SetEnabledAsync("1", enabled: false);

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), Ct);

        Assert.False(resolution.IsResolved);
        Assert.Equal(PrincipalRefusalReason.DisabledAccount, resolution.RefusalReason);
    }

    /// <summary>
    /// THE ORIGINAL IDENTITY NEVER OVERRIDES A DEMOTION: the demoted original resolves as a Viewer and
    /// therefore holds no global-admin capability, while remaining flagged as the original account.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_DemotedOriginalAccount_KeepsNoAdministrativeAuthority()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Viewer, username: "original");
        var policy = new HivePolicy();

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), Ct);

        var principal = resolution.Principal!;
        Assert.True(principal.IsOriginal);
        Assert.False(policy.IsAllowed(principal, HiveCapability.GlobalAdmin));
        Assert.True(policy.IsAllowed(principal, HiveCapability.SharedRead));
    }

    /// <summary>Every unauthenticated shape is refused as unauthenticated — never as an open-mode caller.</summary>
    /// <param name="nullPrincipal">Whether the caller supplied null rather than an unauthenticated principal.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveAsync_UnauthenticatedCaller_IsRefusedAndNeverBecomesTheLocalOperator(bool nullPrincipal)
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);

        var resolution = await directory.AuthEnabledResolver().ResolveAsync(
            nullPrincipal ? null : HiveSecurityTestDirectory.AnonymousPrincipal(),
            Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.Unauthenticated, resolution.RefusalReason);

        // The decisive anti-open-mode assertion: there is NO principal to evaluate, so an anonymous
        // caller cannot hold global-admin authority (an open-mode local operator always would).
        Assert.True(new HivePolicy().IsAllowed(ActingUserPrincipal.LocalOperator(), HiveCapability.GlobalAdmin));
    }

    /// <summary>A missing, empty or whitespace-only GitHub identity claim is refused, not looked up.</summary>
    /// <param name="gitHubId">The claim value supplied (null means no claim at all).</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAsync_MissingOrBlankGitHubIdentityClaim_IsRefused(string? gitHubId)
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal(gitHubId), Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.MissingGitHubIdentity, resolution.RefusalReason);
    }

    /// <summary>
    /// An authenticated identity with NO persisted activated account (never invited, or an invitation
    /// still pending) is refused rather than admitted.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_IdentityWithoutActivatedAccount_IsRefused()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("999"), Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.UnknownAccount, resolution.RefusalReason);
    }

    /// <summary>
    /// An UNRECOGNISED persisted role is refused — never defaulted to a role. The row is written as
    /// raw text because the model's own conversion will not persist such a value.
    /// </summary>
    /// <param name="roleText">The unsupported persisted role text.</param>
    [Theory]
    [InlineData("superuser")]
    [InlineData("2")]
    [InlineData("")]
    public async Task ResolveAsync_UnrecognisedPersistedRole_IsRefusedInsteadOfDefaulted(string roleText)
    {
        using var directory = new HiveSecurityTestDirectory();
        directory.SeedUnsupportedPersistedRole("1", roleText);

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.UnknownRole, resolution.RefusalReason);
    }

    /// <summary>
    /// A directory that cannot be read proves nothing, so the resolution is refused as a directory
    /// failure — it is never treated as a resolved caller and never thrown at the caller.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_UnreadableDirectory_IsRefusedRatherThanThrown()
    {
        var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var resolver = directory.AuthEnabledResolver();

        directory.Dispose();

        var resolution = await resolver.ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DirectoryFailure, resolution.RefusalReason);
    }

    /// <summary>
    /// CANCELLATION PROPAGATES: a cancelled caller is never converted into a refusal, because a
    /// refusal is an authorization answer and cancellation is not one.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_CancelledCaller_PropagatesInsteadOfRefusing()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => directory.AuthEnabledResolver()
                .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), cts.Token));
    }

    /// <summary>
    /// OPEN MODE IS AN EXPLICIT INPUT: the same resolver configured for open mode yields the full
    /// local-operator principal even with NO authenticated caller and NO accounts at all, which is the
    /// legacy backward-compatible behaviour.
    /// </summary>
    /// <param name="nullPrincipal">Whether the caller supplied null rather than an unauthenticated principal.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveAsync_OpenMode_YieldsTheFullLocalOperatorWithoutAnyAccount(bool nullPrincipal)
    {
        using var directory = new HiveSecurityTestDirectory();
        var resolver = directory.Resolver(HiveAuthMode.Open);

        var resolution = await resolver.ResolveAsync(
            nullPrincipal ? null : HiveSecurityTestDirectory.AnonymousPrincipal(),
            Ct);

        var principal = resolution.Principal!;
        Assert.True(resolution.IsResolved);
        Assert.True(principal.IsLocalOperator);
        Assert.Equal(UserRole.Admin, principal.Role);
        Assert.Equal(ActingUserPrincipal.LocalOperatorUserId, principal.UserId);
        Assert.Equal(string.Empty, principal.GitHubId);

        var policy = new HivePolicy();
        Assert.True(policy.IsAllowed(principal, HiveCapability.GlobalAdmin));
        Assert.True(policy.IsAllowed(principal, HiveCapability.ExecutionOperator));
        Assert.True(policy.IsAllowed(principal, HiveCapability.InteractiveMember));
        Assert.True(policy.IsAllowed(principal, HiveCapability.SharedRead));
    }

    /// <summary>
    /// The auth mode is an EXPLICIT host input that mirrors <c>Program.cs</c>: enabled only when BOTH
    /// OAuth values are non-empty, so a half-configured deployment stays in open mode exactly as the
    /// OAuth registration does.
    /// </summary>
    /// <param name="clientId">The supplied client id (null = unset).</param>
    /// <param name="clientSecret">The supplied client secret (null = unset).</param>
    /// <param name="expectedAuthEnabled">The expected mode.</param>
    [Theory]
    [InlineData("id", "secret", true)]
    [InlineData("id", null, false)]
    [InlineData(null, "secret", false)]
    [InlineData("id", "", false)]
    [InlineData("", "secret", false)]
    [InlineData(null, null, false)]
    public void FromValues_MirrorsTheProgramAuthEnabledPredicate(
        string? clientId,
        string? clientSecret,
        bool expectedAuthEnabled)
    {
        var mode = HiveAuthMode.FromValues(clientId, clientSecret);

        Assert.Equal(expectedAuthEnabled, mode.AuthEnabled);
        Assert.Same(
            expectedAuthEnabled ? HiveAuthMode.AuthenticationEnabled : HiveAuthMode.Open,
            mode);
    }
}
