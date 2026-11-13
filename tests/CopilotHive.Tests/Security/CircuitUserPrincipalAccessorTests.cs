using System.Security.Claims;

using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Components.Authorization;

namespace CopilotHive.Tests.Security;

/// <summary>
/// Covers the EXPLICITLY INITIALISABLE scoped circuit-principal accessor:
/// <list type="bullet">
/// <item>two supplied <see cref="AuthenticationState"/> values (two circuits) on separate accessor
/// instances resolve independently, with no <c>HttpContext</c> involved at all;</item>
/// <item>every operation RE-RESOLVES the current database role/enabled state, so a demotion or a
/// disablement is honoured on the next question asked;</item>
/// <item>an accessor that was never initialised (or was cleared) fails CLOSED in auth-enabled mode;
/// </item>
/// <item>no automatic cascading-state wiring is claimed: the state is always supplied explicitly.</item>
/// </list>
/// </summary>
public sealed class CircuitUserPrincipalAccessorTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Wraps a supplied <see cref="ClaimsPrincipal"/> as the circuit's authentication state.</summary>
    private static AuthenticationState StateOf(ClaimsPrincipal user) => new(user);

    /// <summary>
    /// TWO SUPPLIED CIRCUITS, TWO IDENTITIES: each accessor resolves its own initialised identity from
    /// the directory. The accessors are independent instances (as scoped registration makes them per
    /// circuit), and no ambient/<c>HttpContext</c> identity is consulted.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAsync_TwoInitializedCircuits_ResolveTheirOwnIdentitiesIndependently()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        await directory.SeedActivatedAccountAsync("2", UserRole.Member, username: "member");

        var firstCircuit = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        var secondCircuit = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        firstCircuit.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));
        secondCircuit.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2")));

        var first = await firstCircuit.GetPrincipalAsync(Ct);
        var second = await secondCircuit.GetPrincipalAsync(Ct);

        Assert.Equal("1", first.Principal!.GitHubId);
        Assert.Equal(UserRole.Admin, first.Principal.Role);
        Assert.True(first.Principal.IsOriginal);

        Assert.Equal("2", second.Principal!.GitHubId);
        Assert.Equal(UserRole.Member, second.Principal.Role);
        Assert.False(second.Principal.IsOriginal);

        // Cross-check the capability answers differ between the two circuits.
        Assert.True(await firstCircuit.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.False(await secondCircuit.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.True(await secondCircuit.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
    }

    /// <summary>
    /// EVERY OPERATION REFRESHES: one initialisation, then a demotion in the directory, then a second
    /// question answered from the NEW state. The principal captured at initialisation is never reused
    /// as authorization truth.
    /// </summary>
    [Fact]
    public async Task IsAllowedAsync_AfterDemotion_ReflectsTheNewRoleOnTheNextOperation()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

        await directory.SetRoleAsync("1", UserRole.Operator);

        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));
        Assert.Equal(UserRole.Operator, (await accessor.GetPrincipalAsync(Ct)).Principal!.Role);
    }

    /// <summary>
    /// DISABLEMENT IS HONOURED ON THE NEXT OPERATION even though the account was ENABLED and Admin at
    /// initialisation: the accessor refuses and holds no capability.
    /// </summary>
    [Fact]
    public async Task IsAllowedAsync_AfterDisablement_RefusesAndHoldsNoCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Assert.True(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

        await directory.SetEnabledAsync("1", enabled: false);

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DisabledAccount, resolution.RefusalReason);

        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// AN UN-INITIALISED ACCESSOR FAILS CLOSED in auth-enabled mode — regardless of how many accounts
    /// exist and even for an Admin. There is no implicit identity and no open-mode fall-back.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAsync_UninitializedInAuthEnabledMode_RefusesAndDeniesEveryCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        Assert.False(accessor.IsInitialized);

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.AccessorNotInitialized, resolution.RefusalReason);

        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// CLEARING (the sign-out shape) returns the accessor to the fail-closed state: the previously
    /// resolved identity grants nothing afterwards.
    /// </summary>
    [Fact]
    public async Task Clear_AfterInitialization_ReturnsToTheFailClosedState()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

        accessor.Clear();

        Assert.False(accessor.IsInitialized);
        Assert.Equal(
            PrincipalRefusalReason.AccessorNotInitialized,
            (await accessor.GetPrincipalAsync(Ct)).RefusalReason);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// A CLEAR IS RECORDED AND PERMANENT: it is distinguishable from never-initialised, and a later
    /// <c>Initialize</c> (even with the very same identity) restores nothing.
    /// </summary>
    [Fact]
    public async Task Clear_IsRecordedAsSignedOut_AndIsPermanent()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var never = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));
        Assert.Equal(CircuitIdentityState.Uninitialized, never.IdentityState);
        Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
        Assert.NotNull(accessor.EstablishedIdentity);

        accessor.Clear();

        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
        Assert.True(accessor.IsInvalidated);
        Assert.False(never.IsInvalidated);
        Assert.Null(accessor.EstablishedIdentity);

        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
        Assert.False(accessor.IsInitialized);
        Assert.False((await accessor.GetPrincipalAsync(Ct)).IsResolved);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

        // Explicit reauthentication requires a NEW scope/accessor, never revival of this circuit.
        var reauthenticated = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        Assert.NotSame(accessor, reauthenticated);
        reauthenticated.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));
        Assert.Equal("1", (await reauthenticated.GetPrincipalAsync(Ct)).Principal!.GitHubId);
        Assert.True(await reauthenticated.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
    }

    /// <summary>
    /// A DIFFERENT identity observed after initialisation PERMANENTLY invalidates the accessor: the
    /// replacement is not adopted and the original is not restored. The SAME identity is idempotent.
    /// </summary>
    [Fact]
    public async Task Observe_ADifferentIdentity_PermanentlyInvalidates_AndTheSameIdentityIsIdempotent()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        await directory.SeedActivatedAccountAsync("2", UserRole.Admin, username: "second");
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        var one = HiveSecurityTestDirectory.GitHubPrincipal("1");

        Assert.Same(one, accessor.Observe(StateOf(one)));
        Assert.NotNull(accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1"))));
        Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);

        Assert.Null(accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2"))));
        Assert.Equal(CircuitIdentityState.IdentityReplaced, accessor.IdentityState);

        // Neither B nor A is accepted afterwards, and Clear cannot resurrect or downgrade the state.
        Assert.Null(accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2"))));
        Assert.Null(accessor.Observe(StateOf(one)));
        accessor.Clear();
        Assert.Equal(CircuitIdentityState.IdentityReplaced, accessor.IdentityState);
        Assert.Equal(
            PrincipalRefusalReason.AccessorNotInitialized,
            (await accessor.GetPrincipalAsync(Ct)).RefusalReason);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

        var reauthenticated = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        Assert.NotSame(accessor, reauthenticated);
        reauthenticated.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2")));
        Assert.Equal("2", (await reauthenticated.GetPrincipalAsync(Ct)).Principal!.GitHubId);
        Assert.True(await reauthenticated.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.Equal(CircuitIdentityState.IdentityReplaced, accessor.IdentityState);
    }

    /// <summary>
    /// An explicitly SIGNED-OUT state observed after an established identity is a sign-out: permanent,
    /// and the later identity A is not restored. NO authorization cache: a demotion still lands
    /// immediately for an initialised accessor.
    /// </summary>
    [Fact]
    public async Task Observe_SignedOutAfterEstablished_IsPermanent_ButAnEstablishedAccessorStillReResolves()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        var one = HiveSecurityTestDirectory.GitHubPrincipal("1");
        accessor.Observe(StateOf(one));
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        await directory.SetRoleAsync("1", UserRole.Viewer);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

        Assert.Null(accessor.Observe(StateOf(HiveSecurityTestDirectory.AnonymousPrincipal())));
        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
        Assert.Null(accessor.Observe(StateOf(one)));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
    }

    /// <summary>
    /// A signed-out state as the FIRST observation is recorded as an initialised anonymous identity
    /// and is refused as unauthenticated; a later real identity is then a REPLACEMENT, not an adoption.
    /// </summary>
    [Fact]
    public async Task Observe_SignedOutFirst_ThenAnIdentity_IsNeverAdopted()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        accessor.Observe(StateOf(HiveSecurityTestDirectory.AnonymousPrincipal()));
        Assert.Null(accessor.EstablishedIdentity);
        Assert.Equal(PrincipalRefusalReason.Unauthenticated, (await accessor.GetPrincipalAsync(Ct)).RefusalReason);

        Assert.Null(accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1"))));
        Assert.True(accessor.IsInvalidated);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
    }

    /// <summary>
    /// A SIGNED-OUT state is accepted by the explicit initialiser and still fails closed: an
    /// unauthenticated supplied state is refused as unauthenticated, never as the local operator.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAsync_InitializedWithSignedOutState_IsRefusedAsUnauthenticated()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        accessor.Initialize(StateOf(HiveSecurityTestDirectory.AnonymousPrincipal()));

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(resolution.IsResolved);
        Assert.Equal(PrincipalRefusalReason.Unauthenticated, resolution.RefusalReason);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
    }

    /// <summary>
    /// In OPEN (no-auth) mode the accessor needs no initialisation — that mode carries no per-circuit
    /// identity — and reports the full local operator, preserving backward compatibility.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAsync_OpenModeWithoutInitialization_ResolvesTheLocalOperator()
    {
        using var directory = new HiveSecurityTestDirectory();
        var accessor = directory.Accessor(HiveAuthMode.Open);

        Assert.False(accessor.IsInitialized);
        Assert.False(accessor.AuthEnabled);

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.True(resolution.IsResolved);
        Assert.True(resolution.Principal!.IsLocalOperator);
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// An UNDEFINED capability is a programming error even when the accessor would refuse anyway: the
    /// accessor validates the capability BEFORE any identity work, so the bug is never masked as a
    /// plain "denied" answer.
    /// </summary>
    [Fact]
    public async Task IsAllowedAsync_UndefinedCapability_ThrowsEvenWhenUninitialized()
    {
        using var directory = new HiveSecurityTestDirectory();
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => accessor.IsAllowedAsync((HiveCapability)0, Ct));
    }

    /// <summary>The accessor requires a resolver, and initialisation requires a state.</summary>
    [Fact]
    public void Initialize_NullState_Throws()
    {
        using var directory = new HiveSecurityTestDirectory();
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        Assert.Throws<ArgumentNullException>(() => accessor.Initialize(null!));
        Assert.Throws<ArgumentNullException>(
            () => new CircuitUserPrincipalAccessor(null!, new HivePolicy()));
        Assert.Throws<ArgumentNullException>(
            () => new CircuitUserPrincipalAccessor(directory.AuthEnabledResolver(), null!));
    }
}
