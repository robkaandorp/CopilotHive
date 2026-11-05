using System.Reflection;
using System.Security.Claims;

using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Components.Authorization;

namespace CopilotHive.Tests.Security;

/// <summary>
/// THE DETERMINISTIC ACCEPTANCE MATRIX for the multi-user authorization foundation: one named test
/// per cell of the design matrix, driven through the REAL production primitives over the REAL
/// production <see cref="UserService"/> over an in-memory SQLite database.
/// <para>
/// Every directory read goes through the production <see cref="UserService"/> over the production EF
/// model (see <see cref="HiveSecurityTestDirectory"/>); the directory itself is never mocked. Only
/// the CALLER's input surface — a <see cref="ClaimsPrincipal"/> / <see cref="AuthenticationState"/> —
/// is constructed directly, because that is what a cookie would hand the application.
/// </para>
/// <para>
/// THE CELLS:
/// <list type="bullet">
/// <item>(a) 16 policy cells — each of the four roles against each of the four capabilities.</item>
/// <item>(b) 7 resolver fail-closed cells — every unresolvable auth-enabled identity.</item>
/// <item>(c) the explicit open/no-auth cell, plus the "open mode is never reached" invariant.</item>
/// <item>(d) 3 originality cells — lowest id, later id, disabled original.</item>
/// <item>(e) 4 accessor cells — scope isolation, demotion refresh, disablement refresh, uninitialised.</item>
/// <item>(f) the positive control — a valid enabled admin IS granted, so every denial above is
/// non-vacuous.</item>
/// </list>
/// </para>
/// </summary>
public sealed class HiveAuthorizationAcceptanceMatrixTests
{
    /// <summary>The four capabilities of the design matrix, in ascending authority.</summary>
    private static readonly HiveCapability[] AllCapabilities =
    [
        HiveCapability.SharedRead,
        HiveCapability.InteractiveMember,
        HiveCapability.ExecutionOperator,
        HiveCapability.GlobalAdmin,
    ];

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Wraps a supplied <see cref="ClaimsPrincipal"/> as one circuit's authentication state.</summary>
    private static AuthenticationState StateOf(ClaimsPrincipal user) => new(user);

    /// <summary>Builds an ENABLED, non-original principal of the given role.</summary>
    private static ActingUserPrincipal PrincipalOf(UserRole role) =>
        ActingUserPrincipal.ForActivatedAccount(7, "4242", role, enabled: true, isOriginal: false);

    /// <summary>
    /// THE THRESHOLD WITNESS: for one capability, the role AT its minimum holds it and the role
    /// immediately BELOW it (when one exists) does not. It proves the evaluator's answer for a cell
    /// is caused by that cell's role and not by a degenerate evaluator that denies everything (or
    /// grants everything).
    /// </summary>
    /// <param name="policy">The catalog under test.</param>
    /// <param name="capability">The capability whose threshold is witnessed.</param>
    private static void AssertThresholdWitness(HivePolicy policy, HiveCapability capability)
    {
        var minimum = HivePolicy.MinimumRole(capability);

        var atMinimum = ActingUserPrincipal.ForActivatedAccount(
            1, "1", minimum, enabled: true, isOriginal: false);
        Assert.True(policy.IsAllowed(atMinimum, capability));

        if (minimum > UserRole.Viewer)
        {
            var below = ActingUserPrincipal.ForActivatedAccount(
                2, "2", minimum - 1, enabled: true, isOriginal: false);
            Assert.False(policy.IsAllowed(below, capability));
        }
        else
        {
            // The lowest capability is held FROM the lowest role upwards, so the next role up holds
            // it too — there is no role below the threshold to witness the denial side.
            var above = ActingUserPrincipal.ForActivatedAccount(
                2, "2", minimum + 1, enabled: true, isOriginal: false);
            Assert.True(policy.IsAllowed(above, capability));
        }
    }

    /// <summary>
    /// THE SHARED FAIL-CLOSED ASSERTION for every auth-enabled refusal cell: the resolver refuses
    /// with the expected reason and NO principal, the scoped accessor refuses identically, EVERY
    /// capability is denied, and — so the denial is non-vacuous — the very same evaluator grants
    /// every capability to a valid enabled admin.
    /// </summary>
    /// <param name="directory">The real directory under test.</param>
    /// <param name="principal">The caller identity that must fail closed.</param>
    /// <param name="expectedReason">The exact refusal reason the production code returns.</param>
    private static async Task AssertFailsClosedAsync(
        HiveSecurityTestDirectory directory,
        ClaimsPrincipal principal,
        PrincipalRefusalReason expectedReason)
    {
        var resolver = directory.AuthEnabledResolver();
        var resolution = await resolver.ResolveAsync(principal, Ct);

        // THE REFUSAL RESULT SHAPE the production code actually returns.
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(expectedReason, resolution.RefusalReason);
        Assert.False(string.IsNullOrWhiteSpace(resolution.RefusalDetail));

        // The same failure observed through the scoped circuit accessor.
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(principal));

        var accessorResolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(accessorResolution.IsResolved);
        Assert.Null(accessorResolution.Principal);
        Assert.Equal(expectedReason, accessorResolution.RefusalReason);

        // EVERY capability is denied — explicitly, not merely "no exception was thrown".
        foreach (var capability in AllCapabilities)
        {
            Assert.False(await accessor.IsAllowedAsync(capability, Ct));
        }

        // NON-VACUITY: the same evaluator grants every capability to a valid enabled admin, so the
        // denials above come from the seeded condition and not from an evaluator that denies all.
        var policy = new HivePolicy();
        var enabledAdmin = ActingUserPrincipal.ForActivatedAccount(
            1, "1", UserRole.Admin, enabled: true, isOriginal: true);
        foreach (var capability in AllCapabilities)
        {
            Assert.True(policy.IsAllowed(enabledAdmin, capability));
        }
    }

    // ── (a) The policy matrix: 4 roles × 4 capabilities, one named test per cell ────────────────

    /// <summary>CELL (Viewer, shared-read): held by every role, so a Viewer is granted it.</summary>
    [Fact]
    public void PolicyMatrix_Viewer_SharedRead_IsGranted()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Viewer);

        Assert.True(policy.IsAllowed(principal, HiveCapability.SharedRead));
        Assert.True(policy.EvaluateAll(principal)[HiveCapability.SharedRead]);
        AssertThresholdWitness(policy, HiveCapability.SharedRead);
    }

    /// <summary>CELL (Viewer, interactive-Member): a Viewer is not a Member, so it is denied.</summary>
    [Fact]
    public void PolicyMatrix_Viewer_InteractiveMember_IsDenied()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Viewer);

        Assert.False(policy.IsAllowed(principal, HiveCapability.InteractiveMember));
        Assert.False(policy.EvaluateAll(principal)[HiveCapability.InteractiveMember]);
        AssertThresholdWitness(policy, HiveCapability.InteractiveMember);
    }

    /// <summary>CELL (Viewer, execution-Operator): a Viewer never dispatches work, so it is denied.</summary>
    [Fact]
    public void PolicyMatrix_Viewer_ExecutionOperator_IsDenied()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Viewer);

        Assert.False(policy.IsAllowed(principal, HiveCapability.ExecutionOperator));
        Assert.False(policy.EvaluateAll(principal)[HiveCapability.ExecutionOperator]);
        AssertThresholdWitness(policy, HiveCapability.ExecutionOperator);
    }

    /// <summary>CELL (Viewer, global-Admin): only an Admin is a global admin, so it is denied.</summary>
    [Fact]
    public void PolicyMatrix_Viewer_GlobalAdmin_IsDenied()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Viewer);

        Assert.False(policy.IsAllowed(principal, HiveCapability.GlobalAdmin));
        Assert.False(policy.EvaluateAll(principal)[HiveCapability.GlobalAdmin]);
        AssertThresholdWitness(policy, HiveCapability.GlobalAdmin);
    }

    /// <summary>CELL (Member, shared-read): held by every role, so a Member is granted it.</summary>
    [Fact]
    public void PolicyMatrix_Member_SharedRead_IsGranted()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Member);

        Assert.True(policy.IsAllowed(principal, HiveCapability.SharedRead));
        Assert.True(policy.EvaluateAll(principal)[HiveCapability.SharedRead]);
        AssertThresholdWitness(policy, HiveCapability.SharedRead);
    }

    /// <summary>CELL (Member, interactive-Member): a Member is the minimum role, so it is granted.</summary>
    [Fact]
    public void PolicyMatrix_Member_InteractiveMember_IsGranted()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Member);

        Assert.True(policy.IsAllowed(principal, HiveCapability.InteractiveMember));
        Assert.True(policy.EvaluateAll(principal)[HiveCapability.InteractiveMember]);
        AssertThresholdWitness(policy, HiveCapability.InteractiveMember);
    }

    /// <summary>CELL (Member, execution-Operator): only an Operator dispatches work, so it is denied.</summary>
    [Fact]
    public void PolicyMatrix_Member_ExecutionOperator_IsDenied()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Member);

        Assert.False(policy.IsAllowed(principal, HiveCapability.ExecutionOperator));
        Assert.False(policy.EvaluateAll(principal)[HiveCapability.ExecutionOperator]);
        AssertThresholdWitness(policy, HiveCapability.ExecutionOperator);
    }

    /// <summary>CELL (Member, global-Admin): only an Admin is a global admin, so it is denied.</summary>
    [Fact]
    public void PolicyMatrix_Member_GlobalAdmin_IsDenied()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Member);

        Assert.False(policy.IsAllowed(principal, HiveCapability.GlobalAdmin));
        Assert.False(policy.EvaluateAll(principal)[HiveCapability.GlobalAdmin]);
        AssertThresholdWitness(policy, HiveCapability.GlobalAdmin);
    }

    /// <summary>CELL (Operator, shared-read): held by every role, so an Operator is granted it.</summary>
    [Fact]
    public void PolicyMatrix_Operator_SharedRead_IsGranted()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Operator);

        Assert.True(policy.IsAllowed(principal, HiveCapability.SharedRead));
        Assert.True(policy.EvaluateAll(principal)[HiveCapability.SharedRead]);
        AssertThresholdWitness(policy, HiveCapability.SharedRead);
    }

    /// <summary>CELL (Operator, interactive-Member): an Operator is at least a Member, so it is granted.</summary>
    [Fact]
    public void PolicyMatrix_Operator_InteractiveMember_IsGranted()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Operator);

        Assert.True(policy.IsAllowed(principal, HiveCapability.InteractiveMember));
        Assert.True(policy.EvaluateAll(principal)[HiveCapability.InteractiveMember]);
        AssertThresholdWitness(policy, HiveCapability.InteractiveMember);
    }

    /// <summary>CELL (Operator, execution-Operator): an Operator is the minimum role, so it is granted.</summary>
    [Fact]
    public void PolicyMatrix_Operator_ExecutionOperator_IsGranted()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Operator);

        Assert.True(policy.IsAllowed(principal, HiveCapability.ExecutionOperator));
        Assert.True(policy.EvaluateAll(principal)[HiveCapability.ExecutionOperator]);
        AssertThresholdWitness(policy, HiveCapability.ExecutionOperator);
    }

    /// <summary>CELL (Operator, global-Admin): only an Admin is a global admin, so it is denied.</summary>
    [Fact]
    public void PolicyMatrix_Operator_GlobalAdmin_IsDenied()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Operator);

        Assert.False(policy.IsAllowed(principal, HiveCapability.GlobalAdmin));
        Assert.False(policy.EvaluateAll(principal)[HiveCapability.GlobalAdmin]);
        AssertThresholdWitness(policy, HiveCapability.GlobalAdmin);
    }

    /// <summary>CELL (Admin, shared-read): held by every role, so an Admin is granted it.</summary>
    [Fact]
    public void PolicyMatrix_Admin_SharedRead_IsGranted()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Admin);

        Assert.True(policy.IsAllowed(principal, HiveCapability.SharedRead));
        Assert.True(policy.EvaluateAll(principal)[HiveCapability.SharedRead]);
        AssertThresholdWitness(policy, HiveCapability.SharedRead);
    }

    /// <summary>CELL (Admin, interactive-Member): an Admin is at least a Member, so it is granted.</summary>
    [Fact]
    public void PolicyMatrix_Admin_InteractiveMember_IsGranted()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Admin);

        Assert.True(policy.IsAllowed(principal, HiveCapability.InteractiveMember));
        Assert.True(policy.EvaluateAll(principal)[HiveCapability.InteractiveMember]);
        AssertThresholdWitness(policy, HiveCapability.InteractiveMember);
    }

    /// <summary>CELL (Admin, execution-Operator): an Admin is at least an Operator, so it is granted.</summary>
    [Fact]
    public void PolicyMatrix_Admin_ExecutionOperator_IsGranted()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Admin);

        Assert.True(policy.IsAllowed(principal, HiveCapability.ExecutionOperator));
        Assert.True(policy.EvaluateAll(principal)[HiveCapability.ExecutionOperator]);
        AssertThresholdWitness(policy, HiveCapability.ExecutionOperator);
    }

    /// <summary>CELL (Admin, global-Admin): an Admin is the minimum role, so it is granted.</summary>
    [Fact]
    public void PolicyMatrix_Admin_GlobalAdmin_IsGranted()
    {
        var policy = new HivePolicy();
        var principal = PrincipalOf(UserRole.Admin);

        Assert.True(policy.IsAllowed(principal, HiveCapability.GlobalAdmin));
        Assert.True(policy.EvaluateAll(principal)[HiveCapability.GlobalAdmin]);
        AssertThresholdWitness(policy, HiveCapability.GlobalAdmin);
    }

    /// <summary>
    /// THE EVALUATOR'S SECOND GUARD TERM: a DISABLED principal holds NO capability — not even the
    /// shared read every role has — regardless of the role it carries. The resolver already refuses
    /// disabled accounts, so this cell proves the evaluator itself enforces the rule.
    /// </summary>
    [Fact]
    public void PolicyMatrix_DisabledAdminPrincipal_HoldsNoCapability()
    {
        var policy = new HivePolicy();
        var disabledAdmin = ActingUserPrincipal.ForActivatedAccount(
            1, "1", UserRole.Admin, enabled: false, isOriginal: true);

        Assert.False(disabledAdmin.Enabled);
        foreach (var capability in AllCapabilities)
        {
            Assert.False(policy.IsAllowed(disabledAdmin, capability));
            Assert.False(policy.EvaluateAll(disabledAdmin)[capability]);
        }

        // The very same principal with the enabled flag restored is granted everything: the denial
        // is caused by the disabled state alone.
        var enabledAdmin = ActingUserPrincipal.ForActivatedAccount(
            1, "1", UserRole.Admin, enabled: true, isOriginal: true);
        foreach (var capability in AllCapabilities)
        {
            Assert.True(policy.IsAllowed(enabledAdmin, capability));
        }
    }

    // ── (b) Resolver fail-closed in auth-enabled mode ─────────────────────────────────────────
    /// <summary>
    /// CELL (b, unauthenticated): an unauthenticated <see cref="ClaimsPrincipal"/> is refused as
    /// unauthenticated, never as the open-mode local operator.
    /// </summary>
    [Fact]
    public async Task ResolverFailClosed_UnauthenticatedPrincipal_DeniesEveryCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");

        await AssertFailsClosedAsync(
            directory,
            HiveSecurityTestDirectory.AnonymousPrincipal(),
            PrincipalRefusalReason.Unauthenticated);
    }

    /// <summary>
    /// CELL (b, missing identity claim): an authenticated principal with NO
    /// <see cref="ClaimTypes.NameIdentifier"/> claim is refused — the claim is never defaulted.
    /// </summary>
    [Fact]
    public async Task ResolverFailClosed_MissingGitHubIdentityClaim_DeniesEveryCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");

        await AssertFailsClosedAsync(
            directory,
            HiveSecurityTestDirectory.GitHubPrincipal(gitHubId: null),
            PrincipalRefusalReason.MissingGitHubIdentity);
    }

    /// <summary>CELL (b, empty identity claim): an EMPTY claim value is refused, never looked up.</summary>
    [Fact]
    public async Task ResolverFailClosed_EmptyGitHubIdentityClaim_DeniesEveryCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");

        await AssertFailsClosedAsync(
            directory,
            HiveSecurityTestDirectory.GitHubPrincipal(string.Empty),
            PrincipalRefusalReason.MissingGitHubIdentity);
    }

    /// <summary>
    /// CELL (b, whitespace identity claim): a WHITESPACE-ONLY claim value is refused, never looked up
    /// and never trimmed into a lookup.
    /// </summary>
    [Fact]
    public async Task ResolverFailClosed_WhitespaceOnlyGitHubIdentityClaim_DeniesEveryCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");

        await AssertFailsClosedAsync(
            directory,
            HiveSecurityTestDirectory.GitHubPrincipal("   "),
            PrincipalRefusalReason.MissingGitHubIdentity);
    }

    /// <summary>
    /// CELL (b, no persisted account): an authenticated identity with NO persisted activated
    /// account (never invited, or still pending) is refused as an unknown account.
    /// </summary>
    [Fact]
    public async Task ResolverFailClosed_NoPersistedAccountForGitHubId_DeniesEveryCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");

        await AssertFailsClosedAsync(
            directory,
            HiveSecurityTestDirectory.GitHubPrincipal("999"),
            PrincipalRefusalReason.UnknownAccount);
    }

    /// <summary>
    /// CELL (b, disabled account): a DISABLED persisted account is refused as disabled — the account's
    /// own Admin role never survives the refusal.
    /// </summary>
    [Fact]
    public async Task ResolverFailClosed_DisabledPersistedAccount_DeniesEveryCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync(
            "1", UserRole.Admin, enabled: false, username: "disabled-admin");

        await AssertFailsClosedAsync(
            directory,
            HiveSecurityTestDirectory.GitHubPrincipal("1"),
            PrincipalRefusalReason.DisabledAccount);
    }

    /// <summary>
    /// CELL (b, unknown persisted role): a row whose persisted role text is not a supported role is
    /// REFUSED, never defaulted to a role. The bad value is written as raw SQL, bypassing the
    /// model's own validated conversion.
    /// </summary>
    [Fact]
    public async Task ResolverFailClosed_UnknownPersistedRole_DeniesEveryCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        directory.SeedUnsupportedPersistedRole("1", "superuser");

        await AssertFailsClosedAsync(
            directory,
            HiveSecurityTestDirectory.GitHubPrincipal("1"),
            PrincipalRefusalReason.UnknownRole);
    }

    // ── (c) The explicit open/no-auth mode ─────────────────────────────────────────────────────

    /// <summary>
    /// CELL (c, open mode): the EXPLICIT no-auth mode yields the full local-operator principal,
    /// granting ALL FOUR capabilities, with no account behind it.
    /// </summary>
    [Fact]
    public async Task OpenMode_ExplicitNoAuth_YieldsFullGlobalAdminLocalOperator()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");

        var resolution = await directory.Resolver(HiveAuthMode.Open).ResolveAsync(
            claimsPrincipal: null, Ct);

        Assert.True(resolution.IsResolved);
        Assert.Equal(PrincipalRefusalReason.None, resolution.RefusalReason);

        var principal = resolution.Principal!;
        Assert.True(principal.IsLocalOperator);
        Assert.Equal(UserRole.Admin, principal.Role);
        Assert.True(principal.Enabled);
        Assert.Equal(string.Empty, principal.GitHubId);

        // The local operator has NO account id behind it: the literal zero, asserted against the
        // LITERAL (not against the constant, which would compare a value with itself) — so a
        // principal that claimed a real activated-account id would fail here.
        Assert.Equal(0, principal.UserId);
        Assert.Equal(0, ActingUserPrincipal.LocalOperatorUserId);
        Assert.NotEqual(1, principal.UserId);

        var policy = new HivePolicy();
        foreach (var capability in AllCapabilities)
        {
            Assert.True(policy.IsAllowed(principal, capability));
        }

        // The same open mode through the scoped accessor needs no initialisation and reports the
        // local operator with the full matrix.
        var accessor = directory.Accessor(HiveAuthMode.Open);
        Assert.False(accessor.IsInitialized);
        var accessorResolution = await accessor.GetPrincipalAsync(Ct);
        Assert.True(accessorResolution.IsResolved);
        Assert.True(accessorResolution.Principal!.IsLocalOperator);
        foreach (var capability in AllCapabilities)
        {
            Assert.True(await accessor.IsAllowedAsync(capability, Ct));
        }
    }

    /// <summary>
    /// THE ANTI-ESCALATION INVARIANT: open mode is NEVER reached from an auth-enabled failure
    /// path. Every unresolvable identity — absent, unauthenticated, malformed, unknown, disabled,
    /// unknown role — is refused with NO principal, so no failure can select the local operator.
    /// </summary>
    [Fact]
    public async Task OpenMode_IsNeverReachedFromAnyAuthEnabledFailurePath()
    {
        using var directory = new HiveSecurityTestDirectory();

        // "1" is DISABLED, "2" is a normal enabled account, "3" carries an unsupported role text.
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, enabled: false, username: "disabled");
        await directory.SeedActivatedAccountAsync("2", UserRole.Viewer, username: "viewer");
        directory.SeedUnsupportedPersistedRole("3", "superuser");

        var failingIdentities = new (string Name, ClaimsPrincipal? Principal)[]
        {
            ("null principal", null),
            ("unauthenticated principal", HiveSecurityTestDirectory.AnonymousPrincipal()),
            ("missing identity claim", HiveSecurityTestDirectory.GitHubPrincipal(gitHubId: null)),
            ("empty identity claim", HiveSecurityTestDirectory.GitHubPrincipal(string.Empty)),
            ("whitespace identity claim", HiveSecurityTestDirectory.GitHubPrincipal("   ")),
            ("unknown account", HiveSecurityTestDirectory.GitHubPrincipal("999")),
            ("disabled account", HiveSecurityTestDirectory.GitHubPrincipal("1")),
            ("unknown persisted role", HiveSecurityTestDirectory.GitHubPrincipal("3")),
        };

        var resolver = directory.AuthEnabledResolver();

        foreach (var (name, principal) in failingIdentities)
        {
            var resolution = await resolver.ResolveAsync(principal, Ct);

            Assert.False(resolution.IsResolved, $"'{name}' must not resolve.");
            Assert.True(resolution.Principal is null, $"'{name}' must not yield a principal.");
            Assert.NotEqual(PrincipalRefusalReason.None, resolution.RefusalReason);
            Assert.False(string.IsNullOrWhiteSpace(resolution.RefusalDetail));
        }

        // ...and the un-INITIALISED scoped accessor is refused as such, never as the local operator.
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        var accessorResolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(accessorResolution.IsResolved);
        Assert.Null(accessorResolution.Principal);
        Assert.Equal(PrincipalRefusalReason.AccessorNotInitialized, accessorResolution.RefusalReason);
    }

    // ── (d) Originality ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CELL (d, original): the LOWEST-id activated account resolves with <c>IsOriginal</c> true —
    /// the flag is derived from the real directory, not supplied.
    /// </summary>
    [Fact]
    public async Task IsOriginal_LowestIdActivatedAccount_ResolvesTrue()
    {
        using var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        var second = await directory.SeedActivatedAccountAsync("2", UserRole.Member, username: "second");

        Assert.True(original.Id < second.Id);

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), Ct);

        Assert.True(resolution.IsResolved);
        var principal = resolution.Principal!;
        Assert.True(principal.IsOriginal);
        Assert.Equal(original.Id, principal.UserId);
        Assert.Equal("1", principal.GitHubId);
        Assert.Equal(UserRole.Admin, principal.Role);
        Assert.False(principal.IsLocalOperator);
    }

    /// <summary>
    /// CELL (d, non-original): a SECOND activated account resolves with <c>IsOriginal</c> false, even
    /// though it is enabled and holds a real role.
    /// </summary>
    [Fact]
    public async Task IsOriginal_SecondActivatedAccount_ResolvesFalse()
    {
        using var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        var second = await directory.SeedActivatedAccountAsync("2", UserRole.Member, username: "second");

        Assert.True(original.Id < second.Id);

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("2"), Ct);

        Assert.True(resolution.IsResolved);
        var principal = resolution.Principal!;
        Assert.False(principal.IsOriginal);
        Assert.Equal(second.Id, principal.UserId);
        Assert.Equal("2", principal.GitHubId);
        Assert.Equal(UserRole.Member, principal.Role);
    }

    /// <summary>
    /// CELL (d, disabled original): a DISABLED original account fails closed ENTIRELY — originality
    /// never overrides disabled state, so there is no principal at all and no capability.
    /// </summary>
    [Fact]
    public async Task IsOriginal_DisabledOriginalAccount_FailsClosedEntirely()
    {
        using var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        await directory.SeedActivatedAccountAsync("2", UserRole.Member, username: "second");
        await directory.SetEnabledAsync("1", enabled: false);

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), Ct);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DisabledAccount, resolution.RefusalReason);

        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));
        foreach (var capability in AllCapabilities)
        {
            Assert.False(await accessor.IsAllowedAsync(capability, Ct));
        }
    }

    // ── (e) The explicitly-initialisable scoped circuit accessor ────────────────────────────────

    /// <summary>
    /// CELL (e, scope isolation): TWO separately supplied <see cref="AuthenticationState"/> values over
    /// TWO separately initialised accessor instances resolve their OWN identities, never share
    /// principal state, and never capture one another. No <c>HttpContext</c> is involved anywhere.
    /// </summary>
    [Fact]
    public async Task Accessor_TwoCircuitsWithSeparateAuthenticationStates_NeverSharePrincipalState()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        await directory.SeedActivatedAccountAsync("2", UserRole.Member, username: "second");

        var firstCircuit = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        var secondCircuit = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        Assert.NotSame(firstCircuit, secondCircuit);

        firstCircuit.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));
        secondCircuit.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2")));

        // Each circuit resolves ITS OWN identity from the directory.
        var first = await firstCircuit.GetPrincipalAsync(Ct);
        var second = await secondCircuit.GetPrincipalAsync(Ct);

        Assert.Equal("1", first.Principal!.GitHubId);
        Assert.Equal(UserRole.Admin, first.Principal.Role);
        Assert.Equal("2", second.Principal!.GitHubId);
        Assert.Equal(UserRole.Member, second.Principal.Role);

        // Clearing one circuit does not touch the other, AND the cleared circuit forgets its own
        // identity: the cleared accessor must NOT keep resolving the previous caller's account.
        firstCircuit.Clear();
        Assert.Equal(
            PrincipalRefusalReason.AccessorNotInitialized,
            (await firstCircuit.GetPrincipalAsync(Ct)).RefusalReason);
        Assert.Equal("2", (await secondCircuit.GetPrincipalAsync(Ct)).Principal!.GitHubId);

        // SIGN-OUT FORGETS THE IDENTITY: the cleared accessor retains no claims principal at all, so
        // a stale identity can never be replayed by a later path that reads it.
        Assert.Null(RetainedClaimsPrincipal(firstCircuit));

        // A cleared circuit holds no identity at all: re-initialising it with the SECOND circuit's
        // identity must resolve THAT identity, never the one it was cleared from.
        firstCircuit.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2")));
        Assert.Equal("2", (await firstCircuit.GetPrincipalAsync(Ct)).Principal!.GitHubId);

        // ...and re-initialising with an identity that does not exist fails closed rather than
        // falling back to the account it was cleared from.
        firstCircuit.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("999")));
        var afterUnknown = await firstCircuit.GetPrincipalAsync(Ct);
        Assert.False(afterUnknown.IsResolved);
        Assert.Null(afterUnknown.Principal);
        Assert.Equal(PrincipalRefusalReason.UnknownAccount, afterUnknown.RefusalReason);

        // The SINGLETON resolver never captures a principal either: two identities through ONE
        // resolver instance (its production lifetime) resolve to their own principals.
        var sharedResolver = directory.AuthEnabledResolver();
        var viaSharedResolverFirst = await sharedResolver
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), Ct);
        var viaSharedResolverSecond = await sharedResolver
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("2"), Ct);

        Assert.Equal("1", viaSharedResolverFirst.Principal!.GitHubId);
        Assert.Equal("2", viaSharedResolverSecond.Principal!.GitHubId);
        Assert.NotSame(viaSharedResolverFirst.Principal, viaSharedResolverSecond.Principal);

        // NO HttpContext ANYWHERE: the accessor neither takes one nor stores one.
        AssertNoHttpContextDependency();
    }

    /// <summary>
    /// CELL (e, demotion refresh): after a successful resolution the account is DEMOTED directly in
    /// the database, and the NEXT accessor operation uses the NEW state — the principal captured at
    /// initialisation is never reused as authorization truth.
    /// </summary>
    [Fact]
    public async Task Accessor_AfterDatabaseDemotion_NextOperationUsesTheNewRole()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        // First operation: the Admin account is granted global admin.
        var before = await accessor.GetPrincipalAsync(Ct);
        Assert.True(before.IsResolved);
        Assert.Equal(UserRole.Admin, before.Principal!.Role);
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

        // The account is demoted DIRECTLY in the database, between operations.
        await directory.SetRoleAsync("1", UserRole.Viewer);

        // The NEXT operation re-reads the directory and answers from the NEW state.
        var after = await accessor.GetPrincipalAsync(Ct);
        Assert.True(after.IsResolved);
        Assert.Equal(UserRole.Viewer, after.Principal!.Role);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
    }

    /// <summary>
    /// CELL (e, disablement refresh): after a successful resolution the account is DISABLED directly
    /// in the database, and the NEXT accessor operation FAILS CLOSED — it grants no capability at
    /// all, even the one it held a moment earlier.
    /// </summary>
    [Fact]
    public async Task Accessor_AfterDatabaseDisablement_NextOperationFailsClosed()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

        // The account is disabled DIRECTLY in the database, between operations.
        await directory.SetEnabledAsync("1", enabled: false);

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DisabledAccount, resolution.RefusalReason);

        foreach (var capability in AllCapabilities)
        {
            Assert.False(await accessor.IsAllowedAsync(capability, Ct));
        }
    }

    /// <summary>
    /// CELL (e, un-initialised): an accessor that was never initialised fails closed in
    /// auth-enabled mode — it holds no capability, not even the shared read every role has.
    /// </summary>
    [Fact]
    public async Task Accessor_UninitializedInAuthEnabledMode_FailsClosed()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        Assert.False(accessor.IsInitialized);
        Assert.True(accessor.AuthEnabled);

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.AccessorNotInitialized, resolution.RefusalReason);

        foreach (var capability in AllCapabilities)
        {
            Assert.False(await accessor.IsAllowedAsync(capability, Ct));
        }
    }

    // ── (f) The positive control ────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE POSITIVE CONTROL: a valid, ENABLED admin principal IS granted every capability, and the
    /// same accessor/resolver pair that refuses the failure cases above grants them here. Every
    /// fail-closed assertion in this file is therefore non-vacuous: the denials come from the
    /// seeded condition, not from an evaluator or a directory that denies everything.
    /// </summary>
    [Fact]
    public async Task PositiveControl_ValidEnabledAdminPrincipal_IsGrantedEveryCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        var admin = await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("1"), Ct);

        Assert.True(resolution.IsResolved);
        Assert.Equal(PrincipalRefusalReason.None, resolution.RefusalReason);
        var principal = resolution.Principal!;
        Assert.Equal(admin.Id, principal.UserId);
        Assert.Equal(UserRole.Admin, principal.Role);
        Assert.True(principal.Enabled);
        Assert.True(principal.IsOriginal);
        Assert.False(principal.IsLocalOperator);

        var policy = new HivePolicy();
        foreach (var capability in AllCapabilities)
        {
            Assert.True(policy.IsAllowed(principal, capability));
        }

        // The scoped accessor grants them too, over the real directory.
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));
        foreach (var capability in AllCapabilities)
        {
            Assert.True(await accessor.IsAllowedAsync(capability, Ct));
        }
    }

    /// <summary>
    /// Reads the claims principal an accessor INSTANCE currently retains, if any. Used only to assert
    /// that a cleared accessor holds NO identity — the observable, security-relevant fact.
    /// </summary>
    /// <param name="accessor">The accessor to inspect.</param>
    /// <returns>The retained claims principal, or null when none is retained.</returns>
    private static ClaimsPrincipal? RetainedClaimsPrincipal(CircuitUserPrincipalAccessor accessor)
    {
        var field = typeof(CircuitUserPrincipalAccessor)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(ClaimsPrincipal));

        return (ClaimsPrincipal?)field.GetValue(accessor);
    }

    /// <summary>
    /// Asserts that the scoped accessor neither takes nor stores an <c>HttpContext</c> (nor any
    /// ambient-context accessor), so a circuit's identity can only come from the explicitly
    /// supplied <see cref="AuthenticationState"/>.
    /// </summary>
    private static void AssertNoHttpContextDependency()
    {
        var type = typeof(CircuitUserPrincipalAccessor);

        var fieldTypes = type
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.FieldType)
            .ToArray();

        var constructorParameterTypes = type
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToArray();

        Assert.DoesNotContain(
            fieldTypes.Concat(constructorParameterTypes),
            dependency => dependency.Name.Contains("HttpContext", StringComparison.OrdinalIgnoreCase));
    }
}
