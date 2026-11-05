using CopilotHive.Security;
using CopilotHive.Services;

namespace CopilotHive.Tests.Security;

/// <summary>
/// Covers the CENTRAL POLICY CATALOG and its pure evaluator: the four design-matrix capabilities, the
/// minimum role that holds each, and the two properties no role table can express on its own — a
/// DISABLED principal holds nothing, and ORIGINAL identity confers no authority.
/// <para>
/// These are catalog/evaluator tests only: the policy enforces nothing at any existing boundary in
/// this slice.
/// </para>
/// </summary>
public sealed class HivePolicyTests
{
    private readonly HivePolicy _policy = new();

    /// <summary>Builds an ENABLED, non-original principal of the given role.</summary>
    private static ActingUserPrincipal PrincipalOf(UserRole role, bool enabled = true, bool isOriginal = false) =>
        ActingUserPrincipal.ForActivatedAccount(7, "4242", role, enabled, isOriginal);

    /// <summary>
    /// THE DESIGN MATRIX, role by role and capability by capability. Every row is a distinct
    /// expectation, so a threshold that slipped by one role fails at least two assertions.
    /// </summary>
    /// <param name="role">The acting account's role.</param>
    /// <param name="sharedRead">Expected shared-read outcome.</param>
    /// <param name="interactiveMember">Expected interactive-member outcome.</param>
    /// <param name="executionOperator">Expected execution-operator outcome.</param>
    /// <param name="globalAdmin">Expected global-admin outcome.</param>
    [Theory]
    [InlineData(UserRole.Viewer, true, false, false, false)]
    [InlineData(UserRole.Member, true, true, false, false)]
    [InlineData(UserRole.Operator, true, true, true, false)]
    [InlineData(UserRole.Admin, true, true, true, true)]
    public void IsAllowed_AppliesTheCapabilityMatrixPerRole(
        UserRole role,
        bool sharedRead,
        bool interactiveMember,
        bool executionOperator,
        bool globalAdmin)
    {
        var principal = PrincipalOf(role);

        Assert.Equal(sharedRead, _policy.IsAllowed(principal, HiveCapability.SharedRead));
        Assert.Equal(interactiveMember, _policy.IsAllowed(principal, HiveCapability.InteractiveMember));
        Assert.Equal(executionOperator, _policy.IsAllowed(principal, HiveCapability.ExecutionOperator));
        Assert.Equal(globalAdmin, _policy.IsAllowed(principal, HiveCapability.GlobalAdmin));
    }

    /// <summary>The catalog's minimum roles are exactly Viewer / Member / Operator / Admin.</summary>
    [Theory]
    [InlineData(HiveCapability.SharedRead, UserRole.Viewer)]
    [InlineData(HiveCapability.InteractiveMember, UserRole.Member)]
    [InlineData(HiveCapability.ExecutionOperator, UserRole.Operator)]
    [InlineData(HiveCapability.GlobalAdmin, UserRole.Admin)]
    public void MinimumRole_IsTheDesignMatrixThreshold(HiveCapability capability, UserRole expected)
    {
        Assert.Equal(expected, HivePolicy.MinimumRole(capability));
    }

    /// <summary>
    /// A DISABLED principal holds NO capability, even the shared read every role has. The evaluator
    /// must not rely on the resolver alone for that: a hand-built disabled principal is refused here.
    /// </summary>
    [Fact]
    public void IsAllowed_DisabledAdminPrincipal_HoldsNoCapabilityAtAll()
    {
        var disabledAdmin = PrincipalOf(UserRole.Admin, enabled: false);

        Assert.False(_policy.IsAllowed(disabledAdmin, HiveCapability.SharedRead));
        Assert.False(_policy.IsAllowed(disabledAdmin, HiveCapability.InteractiveMember));
        Assert.False(_policy.IsAllowed(disabledAdmin, HiveCapability.ExecutionOperator));
        Assert.False(_policy.IsAllowed(disabledAdmin, HiveCapability.GlobalAdmin));
    }

    /// <summary>
    /// ORIGINAL IDENTITY CONFERS NO AUTHORITY in the evaluator: a demoted original account is judged
    /// exactly like any other account of its role, while the same role on a non-original account is
    /// allowed — so the flag alone changes nothing in either direction.
    /// </summary>
    [Fact]
    public void IsAllowed_OriginalFlagNeverChangesTheOutcome()
    {
        var demotedOriginal = PrincipalOf(UserRole.Viewer, isOriginal: true);
        var nonOriginalViewer = PrincipalOf(UserRole.Viewer, isOriginal: false);
        var originalAdmin = PrincipalOf(UserRole.Admin, isOriginal: true);

        Assert.False(_policy.IsAllowed(demotedOriginal, HiveCapability.GlobalAdmin));
        Assert.False(_policy.IsAllowed(nonOriginalViewer, HiveCapability.GlobalAdmin));
        Assert.True(_policy.IsAllowed(originalAdmin, HiveCapability.GlobalAdmin));
    }

    /// <summary>
    /// The open/no-auth LOCAL OPERATOR holds the full matrix, which is the backward-compatibility
    /// requirement of open mode.
    /// </summary>
    [Fact]
    public void IsAllowed_LocalOperatorPrincipal_HoldsTheFullMatrix()
    {
        var localOperator = ActingUserPrincipal.LocalOperator();

        Assert.True(localOperator.IsLocalOperator);
        Assert.Equal(UserRole.Admin, localOperator.Role);
        Assert.True(_policy.IsAllowed(localOperator, HiveCapability.SharedRead));
        Assert.True(_policy.IsAllowed(localOperator, HiveCapability.InteractiveMember));
        Assert.True(_policy.IsAllowed(localOperator, HiveCapability.ExecutionOperator));
        Assert.True(_policy.IsAllowed(localOperator, HiveCapability.GlobalAdmin));
    }

    /// <summary>Evaluating all capabilities yields exactly the four defined ones.</summary>
    [Fact]
    public void EvaluateAll_CoversExactlyTheFourCapabilities()
    {
        var results = _policy.EvaluateAll(PrincipalOf(UserRole.Operator));

        Assert.Equal(
            new[]
            {
                HiveCapability.SharedRead,
                HiveCapability.InteractiveMember,
                HiveCapability.ExecutionOperator,
                HiveCapability.GlobalAdmin,
            }.OrderBy(c => c),
            results.Keys.OrderBy(c => c));
        Assert.False(results[HiveCapability.GlobalAdmin]);
        Assert.True(results[HiveCapability.ExecutionOperator]);
    }

    /// <summary>
    /// An UNDEFINED capability (including <c>default</c>) is a programming error, never a silent
    /// grant or a silent denial: the catalog throws instead of inventing a threshold.
    /// </summary>
    [Fact]
    public void MinimumRole_UndefinedCapability_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HivePolicy.MinimumRole((HiveCapability)0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HivePolicy.MinimumRole((HiveCapability)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _policy.IsAllowed(PrincipalOf(UserRole.Admin), (HiveCapability)99));
    }

    /// <summary>
    /// The principal factory refuses values that could never describe an activated account — in
    /// particular an UNDEFINED role is rejected at construction, so a principal can never carry a
    /// value the evaluator would have to invent a threshold for.
    /// </summary>
    [Fact]
    public void ForActivatedAccount_RejectsNonPositiveIdBlankGitHubIdAndUndefinedRole()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ActingUserPrincipal.ForActivatedAccount(0, "1", UserRole.Member, true, false));
        Assert.Throws<ArgumentException>(
            () => ActingUserPrincipal.ForActivatedAccount(1, "   ", UserRole.Member, true, false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ActingUserPrincipal.ForActivatedAccount(1, "1", (UserRole)42, true, false));
    }

    /// <summary>
    /// The immutable principal carries NO credential material: its members are exactly the acting-user
    /// facts, so an OAuth token can never ride along.
    /// </summary>
    [Fact]
    public void ActingUserPrincipal_ExposesNoCredentialMembers()
    {
        var members = typeof(ActingUserPrincipal)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(ActingUserPrincipal.Enabled),
                nameof(ActingUserPrincipal.GitHubId),
                nameof(ActingUserPrincipal.IsLocalOperator),
                nameof(ActingUserPrincipal.IsOriginal),
                nameof(ActingUserPrincipal.Role),
                nameof(ActingUserPrincipal.UserId),
            },
            members);
    }
}
