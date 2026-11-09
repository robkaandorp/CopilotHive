using CopilotHive.Security;
using CopilotHive.Services;

namespace CopilotHive.Tests.Security;

/// <summary>
/// Covers <see cref="HivePrincipalResolver.ResolveByUserIdAsync"/>: the identity-by-id counterpart of the
/// claims path, used by every Composer authorization check. Each case reads the REAL
/// <see cref="UserService"/> over the real EF model (see <see cref="HiveSecurityTestDirectory"/>), so the
/// test proves the resolver uses the required trusted lookups rather than a stand-in.
/// <para>
/// The claims path (<see cref="HivePrincipalResolver.ResolveAsync"/>) is deliberately NOT re-tested here
/// beyond one equivalence check: it must stay behaviourally unchanged, and its own matrix already pins it.
/// </para>
/// </summary>
public sealed class HivePrincipalResolverByUserIdTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// An activated account resolves from CURRENT stored state: the persisted role, the persisted GitHub
    /// identity and the derived originality flag.
    /// </summary>
    [Fact]
    public async Task ResolveByUserIdAsync_ActivatedAccount_ResolvesCurrentPersistedState()
    {
        using var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("4242", UserRole.Admin, username: "original");
        await directory.SeedActivatedAccountAsync("4343", UserRole.Viewer, username: "later");

        var resolution = await directory.AuthEnabledResolver().ResolveByUserIdAsync(original.Id, Ct);

        Assert.True(resolution.IsResolved);
        Assert.Equal(PrincipalRefusalReason.None, resolution.RefusalReason);
        Assert.Null(resolution.RefusalDetail);

        var principal = Assert.IsType<ActingUserPrincipal>(resolution.Principal);
        Assert.Equal(original.Id, principal.UserId);
        Assert.Equal("4242", principal.GitHubId);
        Assert.Equal(UserRole.Admin, principal.Role);
        Assert.True(principal.Enabled);
        // Originality is DERIVED from the GetAdminUserAsync id comparison, exactly like the claims path.
        Assert.True(principal.IsOriginal);
        Assert.False(principal.IsLocalOperator);
    }

    /// <summary>
    /// A later (non-lowest-id) activated account is NOT original — the id comparison is the only input.
    /// </summary>
    [Fact]
    public async Task ResolveByUserIdAsync_LaterAccount_IsNotOriginal()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("4242", UserRole.Admin, username: "original");
        var later = await directory.SeedActivatedAccountAsync("4343", UserRole.Member, username: "later");

        var principal = (await directory.AuthEnabledResolver().ResolveByUserIdAsync(later.Id, Ct)).Principal!;

        Assert.Equal(UserRole.Member, principal.Role);
        Assert.False(principal.IsOriginal);
    }

    /// <summary>
    /// A NON-POSITIVE id is refused as an unknown account without any directory read, because the
    /// activated-account projection cannot carry such a row.
    /// </summary>
    /// <param name="userId">The non-positive id.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task ResolveByUserIdAsync_NonPositiveId_IsRefusedAsUnknownAccount(int userId)
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("4242", UserRole.Admin);

        var resolution = await directory.AuthEnabledResolver().ResolveByUserIdAsync(userId, Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.UnknownAccount, resolution.RefusalReason);
    }

    /// <summary>
    /// A positive id with NO persisted account is refused — never treated as a fresh/default identity.
    /// </summary>
    [Fact]
    public async Task ResolveByUserIdAsync_MissingAccount_IsRefused()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("4242", UserRole.Admin);

        var resolution = await directory.AuthEnabledResolver().ResolveByUserIdAsync(9999, Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.UnknownAccount, resolution.RefusalReason);
    }

    /// <summary>
    /// A DISABLED account is refused even when it is the ORIGINAL/bootstrap account: originality never
    /// overrides disabled state.
    /// </summary>
    [Fact]
    public async Task ResolveByUserIdAsync_DisabledOriginalAccount_IsRefused()
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
    /// An UNRECOGNISED persisted role is classified exactly like the claims path — through the SAME shared
    /// machinery (<c>RefuseReadFailure</c>/<c>IsUnknownPersistedRoleFailure</c>) — so it is
    /// <see cref="PrincipalRefusalReason.UnknownRole"/>, never a defaulted role.
    /// </summary>
    /// <param name="roleText">The unsupported persisted role text.</param>
    [Theory]
    [InlineData("superuser")]
    [InlineData("2")]
    [InlineData("")]
    public async Task ResolveByUserIdAsync_UnrecognisedPersistedRole_IsRefusedAsUnknownRole(string roleText)
    {
        using var directory = new HiveSecurityTestDirectory();
        directory.SeedUnsupportedPersistedRole("4242", roleText);

        var resolution = await directory.AuthEnabledResolver().ResolveByUserIdAsync(1, Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.UnknownRole, resolution.RefusalReason);
    }

    /// <summary>
    /// A directory that cannot be read proves nothing: the resolution is a DIRECTORY FAILURE refusal that
    /// carries no principal — never thrown at the caller, never treated as success.
    /// </summary>
    [Fact]
    public async Task ResolveByUserIdAsync_UnreadableDirectory_IsRefusedNotThrown()
    {
        var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("4242", UserRole.Admin);
        var resolver = directory.AuthEnabledResolver();

        directory.Dispose();

        var resolution = await resolver.ResolveByUserIdAsync(original.Id, Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DirectoryFailure, resolution.RefusalReason);
    }

    /// <summary>
    /// CALLER CANCELLATION PROPAGATES and is never converted into a refusal: a refusal is an authorization
    /// answer and cancellation is not one.
    /// </summary>
    [Fact]
    public async Task ResolveByUserIdAsync_CancelledCaller_PropagatesInsteadOfRefusing()
    {
        using var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("4242", UserRole.Admin);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => directory.AuthEnabledResolver().ResolveByUserIdAsync(original.Id, cts.Token));
    }

    /// <summary>
    /// FRESHNESS: the id path re-reads current stored state on EVERY call, so a demotion and a later
    /// promotion take effect on the next call without any new resolver.
    /// </summary>
    [Fact]
    public async Task ResolveByUserIdAsync_ReReadsCurrentState_SoADemotionAndPromotionTakeEffect()
    {
        using var directory = new HiveSecurityTestDirectory();
        var account = await directory.SeedActivatedAccountAsync("4242", UserRole.Admin, username: "original");
        var resolver = directory.AuthEnabledResolver();

        var before = (await resolver.ResolveByUserIdAsync(account.Id, Ct)).Principal!;
        await directory.SetRoleAsync("4242", UserRole.Viewer);
        var demoted = (await resolver.ResolveByUserIdAsync(account.Id, Ct)).Principal!;
        await directory.SetRoleAsync("4242", UserRole.Operator);
        var promoted = (await resolver.ResolveByUserIdAsync(account.Id, Ct)).Principal!;

        Assert.Equal(UserRole.Admin, before.Role);
        Assert.Equal(UserRole.Viewer, demoted.Role);
        Assert.Equal(UserRole.Operator, promoted.Role);
    }

    /// <summary>
    /// EQUIVALENCE with the claims path: for the same account the two entry points produce the same role,
    /// enabled flag and ORIGINALITY — the by-id path mirrors the claims path rather than inventing its own
    /// identity rules. (The claims path itself is otherwise untouched and covered by its own matrix.)
    /// </summary>
    [Fact]
    public async Task ResolveByUserIdAsync_AgreesWithTheClaimsPath_ForTheSameAccount()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("4242", UserRole.Admin, username: "original");
        var second = await directory.SeedActivatedAccountAsync("4343", UserRole.Operator, username: "second");
        var resolver = directory.AuthEnabledResolver();

        var byId = (await resolver.ResolveByUserIdAsync(second.Id, Ct)).Principal!;
        var byClaims = (await resolver.ResolveAsync(
            HiveSecurityTestDirectory.GitHubPrincipal("4343"), Ct)).Principal!;

        Assert.Equal(byClaims.UserId, byId.UserId);
        Assert.Equal(byClaims.GitHubId, byId.GitHubId);
        Assert.Equal(byClaims.Role, byId.Role);
        Assert.Equal(byClaims.Enabled, byId.Enabled);
        Assert.Equal(byClaims.IsOriginal, byId.IsOriginal);
        Assert.Equal(byClaims.IsLocalOperator, byId.IsLocalOperator);
    }

    /// <summary>
    /// OPEN MODE IS NOT REACHED BY THE ID PATH: the by-id entry point authorizes a PERSISTED account, so it
    /// never yields the synthetic local operator — a configured open mode with no accounts still refuses.
    /// </summary>
    [Fact]
    public async Task ResolveByUserIdAsync_OpenModeNeverYieldsTheLocalOperator()
    {
        using var directory = new HiveSecurityTestDirectory();
        var openResolver = directory.Resolver(HiveAuthMode.Open);

        var resolution = await openResolver.ResolveByUserIdAsync(1, Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.UnknownAccount, resolution.RefusalReason);
    }
}
