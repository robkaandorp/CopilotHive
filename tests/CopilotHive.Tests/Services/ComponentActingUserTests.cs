using System.Security.Claims;

using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components.Authorization;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE CIRCUIT IDENTITY ACQUISITION CONTRACT of <see cref="ComponentActingUser"/> — the ONE seam
/// the Blazor pages use to obtain the identity they hand to an identity-aware facade, to initialise
/// the scoped <see cref="CircuitUserPrincipalAccessor"/>, and to ask a capability question.
/// <para>
/// Three behaviours are pinned here, because each is a distinct way the UI round could break:
/// </para>
/// <list type="bullet">
/// <item><b>OPEN MODE TOLERANCE</b> — with NO cascading authentication state at all (the host
/// registers no authentication services), the helper must NOT throw and must yield the null
/// identity that the facade turns into the local operator;</item>
/// <item><b>CASCADE INITIALISATION</b> — with a cascade, the scoped accessor is initialised from
/// the circuit's <see cref="AuthenticationState"/>, exactly once, and never with a fabricated
/// state;</item>
/// <item><b>THE CAPABILITY QUESTION</b> — the question is answered against CURRENT stored state,
/// so a demotion or a disablement between two questions is reflected immediately.</item>
/// </list>
/// </summary>
public sealed class ComponentActingUserTests : IDisposable
{
    private const string AdminId = "cau-admin";
    private const string MemberId = "cau-member";
    private const string ViewerId = "cau-viewer";

    private readonly FacadeAuthorizationHarness _auth = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A circuit accessor over the harness's directory in authentication-enabled mode — the shape
    /// the host registers (one SCOPED instance per circuit).
    /// </summary>
    private CircuitUserPrincipalAccessor Accessor(HiveAuthMode? mode = null) =>
        new(_auth.Resolver(mode ?? HiveAuthMode.AuthenticationEnabled), _auth.Policy);

    /// <summary>An <see cref="AuthenticationState"/> carrying the given GitHub identity.</summary>
    private static Task<AuthenticationState> StateFor(string gitHubId)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, gitHubId)],
            authenticationType: "Cookies");
        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }

    // ── Open mode: no cascade at all ─────────────────────────────────────────

    /// <summary>
    /// OPEN MODE TOLERANCE: the helper is handed NO cascading authentication state — exactly what a
    /// page sees on a host where <c>AddCascadingAuthenticationState</c> was never registered — and it
    /// must not throw. It yields a NULL identity, which is what the facades resolve into the local
    /// operator.
    /// <para>
    /// A helper that dereferenced the absent cascade would throw a
    /// <see cref="NullReferenceException"/> here, and a helper that fabricated an
    /// <see cref="AuthenticationState"/> would break the open-mode local-operator contract.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OpenMode_NoCascade_YieldsANullIdentity_AndNeverThrows()
    {
        var accessor = Accessor(HiveAuthMode.Open);

        var identity = await ComponentActingUser.AcquireAsync(authenticationState: null, accessor);

        Assert.Null(identity);
    }

    /// <summary>
    /// OPEN MODE TOLERANCE FOR THE CAPABILITY QUESTION: with no cascade, the question is answered
    /// from the host's EXPLICIT open-mode decision — the local operator holds every capability — so
    /// the global Composer settings remain editable in the single-operator installation.
    /// </summary>
    [Theory]
    [InlineData(HiveCapability.SharedRead)]
    [InlineData(HiveCapability.InteractiveMember)]
    [InlineData(HiveCapability.ExecutionOperator)]
    [InlineData(HiveCapability.GlobalAdmin)]
    public async Task OpenMode_NoCascade_HoldsEveryCapability(HiveCapability capability)
    {
        var accessor = Accessor(HiveAuthMode.Open);

        Assert.True(await ComponentActingUser.IsAllowedAsync(null, accessor, capability, Ct));
    }

    /// <summary>
    /// OPEN MODE TOLERANCE: the accessor is deliberately LEFT UNINITIALISED when there is no
    /// cascade. It needs no initialisation in open mode, and it must never be seeded with a
    /// fabricated state — so the accessor keeps reporting the local operator on its own.
    /// </summary>
    [Fact]
    public async Task OpenMode_TheAccessorIsLeftUninitialised_AndStillReportsTheLocalOperator()
    {
        var accessor = Accessor(HiveAuthMode.Open);

        await ComponentActingUser.AcquireAsync(authenticationState: null, accessor);

        Assert.False(accessor.IsInitialized);

        // The accessor answers on its own, with no initialisation at all.
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.True(resolution.IsResolved);
        Assert.True(resolution.Principal!.IsLocalOperator);
    }

    /// <summary>
    /// UNKNOWN MODE DENIES (updated obsolete expectation): with NO cascade and NO accessor the mode
    /// cannot be shown to be explicit open mode, so the capability question is DENIED. Previously this
    /// answered <c>true</c> (a missing cascade was read as open mode).
    /// </summary>
    [Fact]
    public async Task OpenMode_NoCascadeAndNoAccessor_StillAnswersTheCapabilityQuestion()
    {
        Assert.False(await ComponentActingUser.IsAllowedAsync(
            null, accessor: null, HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// UNKNOWN MODE DENIES (auth enabled): a missing cascade while authentication is enabled is NOT
    /// open mode, so every capability is denied — and the accessor is never created/initialised.
    /// </summary>
    [Theory]
    [InlineData(HiveCapability.SharedRead)]
    [InlineData(HiveCapability.InteractiveMember)]
    [InlineData(HiveCapability.ExecutionOperator)]
    [InlineData(HiveCapability.GlobalAdmin)]
    public async Task AuthEnabled_NoCascade_DeniesEveryCapability_AndNeverInitialisesTheAccessor(HiveCapability capability)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor(HiveAuthMode.AuthenticationEnabled);

        Assert.False(await ComponentActingUser.IsAllowedAsync(null, accessor, capability, Ct));

        Assert.False(accessor.IsInitialized);
        Assert.Equal(CircuitIdentityState.Uninitialized, accessor.IdentityState);
        Assert.Null(await ComponentActingUser.AcquireAsync(null, accessor));
        Assert.Equal(CircuitIdentityState.Uninitialized, accessor.IdentityState);
    }

    /// <summary>EXPLICIT OPEN still answers true with no cascade (local-operator behaviour unchanged).</summary>
    [Fact]
    public async Task ExplicitOpen_NoCascade_StillAnswersTrue_WithoutInitialising()
    {
        var accessor = Accessor(HiveAuthMode.Open);

        Assert.True(await ComponentActingUser.IsAllowedAsync(null, accessor, HiveCapability.GlobalAdmin, Ct));
        Assert.Equal(CircuitIdentityState.Uninitialized, accessor.IdentityState);
    }

    // ── Cascade: identity acquisition and accessor initialisation ────────────

    /// <summary>
    /// THE CASCADE IS USED: with a cascading authentication state, the helper yields the circuit's
    /// principal (an IDENTITY, never authorization truth) and INITIALISES the scoped accessor from
    /// that very state — so the accessor can answer capability questions about this circuit.
    /// </summary>
    [Fact]
    public async Task WithACascade_TheIdentityIsYielded_AndTheAccessorIsInitialised()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();

        Assert.False(accessor.IsInitialized);

        var identity = await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor);

        Assert.NotNull(identity);
        Assert.Equal(AdminId, identity!.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.True(accessor.IsInitialized);

        // The initialised accessor answers for the SAME circuit identity.
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// THE SAME IDENTITY IS IDEMPOTENT, A DIFFERENT ONE IS NEVER ADOPTED: re-acquiring with the same
    /// identity keeps serving it; acquiring with a DIFFERENT identity permanently invalidates the
    /// circuit (the replacement is not adopted and the original is not restored).
    /// </summary>
    [Fact]
    public async Task TheAccessorObservesTheSameIdentityIdempotently_AndADifferentOneInvalidates()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var accessor = Accessor();

        Assert.NotNull(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor));
        Assert.NotNull(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor));
        Assert.Equal(AdminId, (await accessor.GetPrincipalAsync(Ct)).Principal!.GitHubId);

        // A SECOND, different identity invalidates; it is not adopted.
        Assert.Null(await ComponentActingUser.AcquireAsync(StateFor(MemberId), accessor));

        Assert.Equal(CircuitIdentityState.IdentityReplaced, accessor.IdentityState);
        Assert.False((await accessor.GetPrincipalAsync(Ct)).IsResolved);
    }

    // ── Permanent invalidation ───────────────────────────────────────────────

    private static Task<AuthenticationState> SignedOut() =>
        Task.FromResult(new AuthenticationState(FacadeAuthorizationHarness.Anonymous()));

    /// <summary>
    /// The transitions that must permanently invalidate the circuit. Each ends with a final
    /// acquisition (<c>null</c> = missing cascade) that must be denied for identity, capability and
    /// principal resolution.
    /// </summary>
    public static TheoryData<string> InvalidatingTransitions() => new()
    {
        "A>signout>missing", "A>signout>A", "A>signout>B", "A>B>missing", "A>B>A",
    };

    private Task<AuthenticationState>? Step(string step) => step switch
    {
        "A" => StateFor(AdminId),
        "B" => StateFor(MemberId),
        "signout" => SignedOut(),
        "missing" => null,
        _ => throw new InvalidOperationException(step),
    };

    /// <summary>
    /// PERMANENT INVALIDATION: after each transition, identity (<c>AcquireAsync</c>), capability
    /// (<c>IsAllowedAsync</c>) and the accessor's principal all deny — nothing is restored from a
    /// stale state and nothing is silently adopted.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidatingTransitions))]
    public async Task APermanentlyInvalidatedCircuit_DeniesIdentityCapabilityAndPrincipal(string transition)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(MemberId, UserRole.Admin);
        var accessor = Accessor();
        var steps = transition.Split('>');

        Assert.NotNull(await ComponentActingUser.AcquireAsync(Step(steps[0]), accessor));
        Assert.True(await ComponentActingUser.IsAllowedAsync(Step(steps[0]), accessor, HiveCapability.GlobalAdmin, Ct));

        // The middle step is the observed invalidation, the last step is the attempted recovery.
        Assert.Null(await ComponentActingUser.AcquireAsync(Step(steps[1]), accessor));
        Assert.True(accessor.IsInvalidated);

        Assert.Null(await ComponentActingUser.AcquireAsync(Step(steps[2]), accessor));
        foreach (var capability in Enum.GetValues<HiveCapability>())
        {
            Assert.False(await ComponentActingUser.IsAllowedAsync(Step(steps[2]), accessor, capability, Ct));
            Assert.False(await accessor.IsAllowedAsync(capability, Ct));
        }

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Null(accessor.EstablishedIdentity);
        Assert.True(accessor.IsInvalidated);
    }

    /// <summary>
    /// A MISSING CASCADE AFTER AN ESTABLISHED IDENTITY is served the established identity ONLY while
    /// no invalidation was observed — and it never initialises anything itself.
    /// </summary>
    [Fact]
    public async Task EstablishedIdentity_ServesACascadeLessAcquisition_OnlyWithoutObservedInvalidation()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();

        var established = await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor);
        var cascadeLess = await ComponentActingUser.AcquireAsync(null, accessor);

        Assert.NotNull(cascadeLess);
        Assert.Same(established, cascadeLess);
        Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);

        await ComponentActingUser.AcquireAsync(SignedOut(), accessor);

        Assert.Null(await ComponentActingUser.AcquireAsync(null, accessor));
    }

    /// <summary>
    /// A cascade-less acquisition before any identity was established yields nothing and the accessor
    /// stays uninitialised (child-before-parent ordering is safe: the later cascade still works).
    /// </summary>
    [Fact]
    public async Task ChildBeforeParent_CascadeLessAcquisitionIsSafe_AndALaterCascadeStillEstablishes()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();

        Assert.Null(await ComponentActingUser.AcquireAsync(null, accessor));
        Assert.Equal(CircuitIdentityState.Uninitialized, accessor.IdentityState);

        Assert.NotNull(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor));
        Assert.True(await ComponentActingUser.IsAllowedAsync(StateFor(AdminId), accessor, HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// OUT-OF-ORDER GUARD: an acquisition task gated on a cascading state for A resumes AFTER another
    /// call observed a sign-out. It must not initialise or restore identity. Deterministic: the gate is
    /// a <see cref="TaskCompletionSource{TResult}"/> released only after the sign-out was observed.
    /// </summary>
    [Fact]
    public async Task AStaleAcquisitionResumingAfterSignOut_DoesNotRestoreIdentity()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();

        // Establish A, so the stale task below is the "A" observation of a live circuit.
        Assert.NotNull(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor));

        var gate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = ComponentActingUser.AcquireAsync(gate.Task, accessor);
        Assert.False(stale.IsCompleted); // parked on the gate: the await has not resumed.

        // Another call observes the explicit sign-out while the stale task is parked.
        Assert.Null(await ComponentActingUser.AcquireAsync(SignedOut(), accessor));
        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);

        gate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));

        Assert.Null(await stale);
        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
        Assert.Null(accessor.EstablishedIdentity);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
    }

    /// <summary>
    /// OUT-OF-ORDER GUARD FOR AN UNINITIALISED CIRCUIT: a stale task that is the FIRST would-be
    /// initialiser resumes after an explicit clear/sign-out and still writes nothing.
    /// </summary>
    [Fact]
    public async Task AStaleFirstAcquisitionResumingAfterClear_DoesNotInitialise()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();

        var gate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = ComponentActingUser.AcquireAsync(gate.Task, accessor);
        Assert.False(stale.IsCompleted);

        accessor.Clear();
        gate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));

        Assert.Null(await stale);
        Assert.False(accessor.IsInitialized);
        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
        Assert.False(await ComponentActingUser.IsAllowedAsync(StateFor(AdminId), accessor, HiveCapability.SharedRead, Ct));
    }

    // ── The capability question reflects CURRENT stored state ────────────────

    /// <summary>
    /// THE CAPABILITY QUESTION IS ANSWERED AGAINST CURRENT STORED STATE: an Admin who is demoted
    /// to Viewer loses the capability on the VERY NEXT question — the accessor re-resolves the
    /// account from the directory every time, so a stale page cannot keep the admin gate open.
    /// </summary>
    [Fact]
    public async Task TheCapabilityQuestion_ReflectsADemotionImmediately()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();
        var state = StateFor(AdminId);

        Assert.True(await ComponentActingUser.IsAllowedAsync(state, accessor, HiveCapability.GlobalAdmin, Ct));

        await _auth.SetRoleAsync(AdminId, UserRole.Viewer);

        Assert.False(await ComponentActingUser.IsAllowedAsync(state, accessor, HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// THE CAPABILITY QUESTION REFUSES A DISABLED ACCOUNT: a disabled account holds nothing, so the
    /// admin gate closes immediately.
    /// </summary>
    [Fact]
    public async Task TheCapabilityQuestion_RefusesADisabledAccount()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();
        var state = StateFor(AdminId);

        Assert.True(await ComponentActingUser.IsAllowedAsync(state, accessor, HiveCapability.GlobalAdmin, Ct));

        await _auth.SetEnabledAsync(AdminId, enabled: false);

        Assert.False(await ComponentActingUser.IsAllowedAsync(state, accessor, HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// A NON-ADMIN NEVER HOLDS THE GLOBAL-ADMIN CAPABILITY: a Viewer and a Member are both refused
    /// the global Composer write — the very capability the notification gate is derived from.
    /// </summary>
    [Theory]
    [InlineData(ViewerId, UserRole.Viewer)]
    [InlineData(MemberId, UserRole.Member)]
    public async Task NonAdmins_NeverHoldGlobalAdmin(string gitHubId, UserRole role)
    {
        await _auth.SeedAsync(gitHubId, role);
        var accessor = Accessor();

        Assert.False(await ComponentActingUser.IsAllowedAsync(
            StateFor(gitHubId), accessor, HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// A CASCADE WITH NO ACCESSOR IS A REFUSAL, NOT AN ALLOW: the helper refuses to answer
    /// "allowed" when it cannot evaluate the circuit's capability — the fail-closed direction. (In
    /// OPEN mode the missing cascade is the explicit local-operator decision and stays allowed.)
    /// </summary>
    [Fact]
    public async Task WithACascadeButNoAccessor_TheCapabilityQuestionIsRefused()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        Assert.False(await ComponentActingUser.IsAllowedAsync(
            StateFor(AdminId), accessor: null, HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// AN UNDEFINED CAPABILITY IS A PROGRAMMING ERROR, NOT A DENIAL: the helper validates the
    /// capability before any I/O, so an undefined capability throws rather than silently answering
    /// "not allowed" (which would look like an ordinary refusal).
    /// </summary>
    [Fact]
    public async Task AnUndefinedCapability_Throws_AndIsNotAnsweredAsARefusal()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await ComponentActingUser.IsAllowedAsync(
                StateFor(AdminId), accessor, (HiveCapability)999, Ct));
    }

    public void Dispose() => _auth.Dispose();
}
