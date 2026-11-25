using System.Reflection;
using System.Runtime.CompilerServices;
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
/// <item><b>CASCADE OBSERVATION</b> — a first cascade establishes the scoped identity; later
/// sign-out/replacement permanently invalidates it, with no fabricated state;</item>
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
        var stale = ComponentActingUser.AcquireAsync(gate.Task, accessor).AsTask();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => gate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(stale);
        await work.RunAsync(async () =>
        {
            Assert.False(stale.IsCompleted); // parked on the gate: the await has not resumed.
            // Another call observes the explicit sign-out while the stale task is parked.
            Assert.Null(await ComponentActingUser.AcquireAsync(SignedOut(), accessor));
            Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
            gate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));

            Assert.Null(await stale.WaitAsync(Ct));
            Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
            Assert.Null(accessor.EstablishedIdentity);
            Assert.Null(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor));
            Assert.Null(await ComponentActingUser.AcquireAsync(null, accessor));
            Assert.Null((await accessor.GetPrincipalAsync(Ct)).Principal);
            Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
            Assert.False(await ComponentActingUser.IsAllowedAsync(StateFor(AdminId), accessor, HiveCapability.SharedRead, Ct));
        });
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
        var stale = ComponentActingUser.AcquireAsync(gate.Task, accessor).AsTask();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => gate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(stale);
        await work.RunAsync(async () =>
        {
            Assert.False(stale.IsCompleted);
            accessor.Clear();
            gate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));

            Assert.Null(await stale.WaitAsync(Ct));
            Assert.False(accessor.IsInitialized);
            Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
            Assert.Null(accessor.EstablishedIdentity);
            Assert.False(await ComponentActingUser.IsAllowedAsync(StateFor(AdminId), accessor, HiveCapability.SharedRead, Ct));
        });
    }

    [Fact]
    public async Task ChildBeforeParent_FirstCascadeAcquisitionEstablishes_ParentObservesSameIdentity()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();

        var childIdentity = await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor);
        Assert.NotNull(childIdentity);
        Assert.Same(childIdentity, accessor.EstablishedIdentity);
        var parentIdentity = await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor);

        Assert.Equal(AdminId, parentIdentity!.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Same(childIdentity, accessor.EstablishedIdentity);
        Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
        Assert.False(accessor.IsInvalidated);
        Assert.True(await ComponentActingUser.IsAllowedAsync(StateFor(AdminId), accessor, HiveCapability.GlobalAdmin, Ct));
        Assert.Equal(AdminId, (await accessor.GetPrincipalAsync(Ct)).Principal!.GitHubId);
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
        var current = await accessor.GetPrincipalAsync(Ct);
        Assert.True(current.IsResolved); // Demotion is not sign-out: identity remains, role changes.
        Assert.Equal(AdminId, current.Principal!.GitHubId);
        Assert.Equal(UserRole.Viewer, current.Principal.Role);
        Assert.True(await ComponentActingUser.IsAllowedAsync(state, accessor, HiveCapability.SharedRead, Ct));
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
        var current = await accessor.GetPrincipalAsync(Ct);
        Assert.Null(current.Principal);
        Assert.Equal(PrincipalRefusalReason.DisabledAccount, current.RefusalReason);
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

    // ══════════════════════════════════════════════════════════════════════════
    //  REGISTRATION ORDER: a newer registration supersedes every older one
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A gate plus the acquisition parked on it, owned by the row that created it.</summary>
    private static TaskCompletionSource<AuthenticationState> NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Bounded, cancellation-INDEPENDENT join budget for releasing/joining parked acquisitions.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The identity a principal carries, for readable assertions.</summary>
    private static string? IdentityOf(ClaimsPrincipal? principal) =>
        principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    /// <summary>
    /// THE CORE ORDERING CELL: while a NEWER task is still PENDING, the OLDER task completes with each
    /// of the three outcomes. All three must yield <c>null</c> and must leave the accessor EXACTLY as it
    /// was — the accessor's state and established identity are compared before/after, so "unchanged" is
    /// asserted rather than "denied for some other reason".
    /// <para>
    /// The outcomes cover the whole surface: a SIGN-OUT (which would otherwise invalidate), a valid
    /// REPLACEMENT (which would otherwise permanently replace), and the SAME identity (which would
    /// otherwise be served as a harmless idempotent re-read). Because all three behave identically, the
    /// rows discriminate on supersession rather than on the identity the older task happened to carry.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("sign-out")]
    [InlineData("replacement")]
    [InlineData("same-identity")]
    public async Task SupersededOlderTask_CompletingWhileANewerIsPending_YieldsNull_AndLeavesTheAccessorUnchanged(
        string olderOutcome)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(MemberId, UserRole.Admin);
        var accessor = Accessor();

        // Establish A through the helper, exactly as a page's first acquisition does.
        var established = await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor);
        Assert.Equal(AdminId, IdentityOf(established));
        var stateBefore = accessor.IdentityState;
        var identityBefore = accessor.EstablishedIdentity;
        Assert.Equal(CircuitIdentityState.Initialized, stateBefore);
        Assert.NotNull(identityBefore);

        var olderGate = NewGate();
        var older = ComponentActingUser.AcquireAsync(olderGate.Task, accessor).AsTask();
        var newerGate = NewGate();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        work.ReleaseBeforeJoin(() => newerGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(older);
        await work.RunAsync(async () =>
        {
            Assert.False(older.IsCompleted);

            // THE NEWER REGISTRATION arrives and is still PENDING — it already superseded the older.
            var newer = work.Track(ComponentActingUser.AcquireAsync(newerGate.Task, accessor).AsTask());
            Assert.False(newer.IsCompleted);

            olderGate.SetResult(olderOutcome switch
            {
                "sign-out" => new AuthenticationState(FacadeAuthorizationHarness.Anonymous()),
                "replacement" => new AuthenticationState(FacadeAuthorizationHarness.Principal(MemberId)),
                "same-identity" => new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)),
                _ => throw new InvalidOperationException(olderOutcome),
            });

            // SUPERSEDED: null, with the accessor byte-for-byte unchanged in state AND identity.
            Assert.Null(await older.WaitAsync(Ct));
            Assert.Equal(stateBefore, accessor.IdentityState);
            Assert.Same(identityBefore, accessor.EstablishedIdentity);
            Assert.False(accessor.IsInvalidated);

            // The capability question and the principal resolution are equally unaffected.
            Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
            Assert.Equal(AdminId, (await accessor.GetPrincipalAsync(Ct)).Principal!.GitHubId);
        });
    }

    /// <summary>
    /// THE SAME ORDERING CELL, REVERSED: the NEWER task completes FIRST (winning and publishing the
    /// same identity), and the OLDER task is released afterwards. The older completion is still
    /// superseded, so it observes nothing and the newer outcome stands unchanged.
    /// </summary>
    [Theory]
    [InlineData("sign-out")]
    [InlineData("replacement")]
    [InlineData("same-identity")]
    public async Task SupersededOlderTask_CompletingAfterTheNewerOneWon_YieldsNull_AndLeavesTheAccessorUnchanged(
        string olderOutcome)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(MemberId, UserRole.Admin);
        var accessor = Accessor();

        Assert.Equal(AdminId, IdentityOf(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor)));
        var stateBefore = accessor.IdentityState;
        var identityBefore = accessor.EstablishedIdentity;

        var olderGate = NewGate();
        var newerGate = NewGate();
        var older = ComponentActingUser.AcquireAsync(olderGate.Task, accessor).AsTask();
        var newer = ComponentActingUser.AcquireAsync(newerGate.Task, accessor).AsTask();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        work.ReleaseBeforeJoin(() => newerGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(older);
        _ = work.Track(newer);
        await work.RunAsync(async () =>
        {
            // THE NEWER TASK WINS FIRST: it is the current registration, so it observes.
            newerGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));
            Assert.Equal(AdminId, IdentityOf(await newer.WaitAsync(Ct)));
            Assert.Equal(stateBefore, accessor.IdentityState);

            // THE OLDER COMPLETION ARRIVES AFTER the newer one already won: still superseded.
            olderGate.SetResult(olderOutcome switch
            {
                "sign-out" => new AuthenticationState(FacadeAuthorizationHarness.Anonymous()),
                "replacement" => new AuthenticationState(FacadeAuthorizationHarness.Principal(MemberId)),
                "same-identity" => new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)),
                _ => throw new InvalidOperationException(olderOutcome),
            });
            Assert.Null(await older.WaitAsync(Ct));

            Assert.Equal(stateBefore, accessor.IdentityState);
            Assert.Same(identityBefore, accessor.EstablishedIdentity);
            Assert.False(accessor.IsInvalidated);
            Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        });
    }

    /// <summary>
    /// ON A FRESH CIRCUIT, STALE WORK CANNOT ESTABLISH THE FIRST IDENTITY: the older task is registered
    /// and held BEFORE any identity exists; a newer registration then supersedes it while still pending.
    /// The older completion must NOT initialise the accessor — it stays
    /// <see cref="CircuitIdentityState.Uninitialized"/> with no recorded identity — and the capability
    /// question still refuses. This is what makes "first registration establishes the identity" safe
    /// under overlap.
    /// </summary>
    [Theory]
    [InlineData("sign-out")]
    [InlineData("replacement")]
    [InlineData("same-identity")]
    public async Task OnAFreshCircuit_AStaleTaskCannotEstablishTheFirstIdentity(string olderOutcome)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(MemberId, UserRole.Admin);
        var accessor = Accessor();

        Assert.Equal(CircuitIdentityState.Uninitialized, accessor.IdentityState);

        var olderGate = NewGate();
        var newerGate = NewGate();
        var older = ComponentActingUser.AcquireAsync(olderGate.Task, accessor).AsTask();
        var newer = ComponentActingUser.AcquireAsync(newerGate.Task, accessor).AsTask();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        work.ReleaseBeforeJoin(() => newerGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(older);
        _ = work.Track(newer);
        await work.RunAsync(async () =>
        {
            Assert.False(older.IsCompleted);
            Assert.False(newer.IsCompleted);

            olderGate.SetResult(olderOutcome switch
            {
                "sign-out" => new AuthenticationState(FacadeAuthorizationHarness.Anonymous()),
                "replacement" => new AuthenticationState(FacadeAuthorizationHarness.Principal(MemberId)),
                "same-identity" => new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)),
                _ => throw new InvalidOperationException(olderOutcome),
            });

            Assert.Null(await older.WaitAsync(Ct));

            // THE CIRCUIT IS STILL FRESH: no identity was fabricated from the superseded task, not even
            // by the same-identity row that would otherwise have established it.
            Assert.Equal(CircuitIdentityState.Uninitialized, accessor.IdentityState);
            Assert.False(accessor.IsInitialized);
            Assert.Null(accessor.EstablishedIdentity);
            Assert.False(accessor.IsInvalidated);
            Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
            // The no-cascade acquisition cannot stand in for a first identity either.
            Assert.Null(await ComponentActingUser.AcquireAsync(null, accessor));
            Assert.False(await ComponentActingUser.IsAllowedAsync(
                null, accessor, HiveCapability.SharedRead, Ct));
            Assert.Equal(CircuitIdentityState.Uninitialized, accessor.IdentityState);
        });
    }

    /// <summary>
    /// REACQUIRING THE EXACT SAME TASK NEVER ADVANCES ITS ORDER. The exact older task object is
    /// re-acquired while a newer one is pending, and again after the newer one has won. Both
    /// re-acquisitions must stay DENIED (they return <c>null</c>), while the exact CURRENT task object
    /// still WORKS when re-acquired — so the rule is "the same task keeps its original order", not
    /// "every re-acquisition is denied".
    /// </summary>
    [Fact]
    public async Task ReacquiringTheExactOlderTask_NeverAdvancesIt_WhileTheExactCurrentTaskStillWorks()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();
        Assert.Equal(AdminId, IdentityOf(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor)));

        var olderGate = NewGate();
        var newerGate = NewGate();
        var older = ComponentActingUser.AcquireAsync(olderGate.Task, accessor).AsTask();
        var newer = ComponentActingUser.AcquireAsync(newerGate.Task, accessor).AsTask();
        // THE REPLAY IS OWNED BEFORE any assertion can throw: a second acquisition of the EXACT older
        // task is started here and joined in the finally below even if a rendezvous assertion fails.
        var reacquiredWhilePending = ComponentActingUser.AcquireAsync(olderGate.Task, accessor).AsTask();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        work.ReleaseBeforeJoin(() => newerGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(older);
        _ = work.Track(newer);
        _ = work.Track(reacquiredWhilePending);
        await work.RunAsync(async () =>
        {
            Assert.False(reacquiredWhilePending.IsCompleted);

            newerGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));
            Assert.Equal(AdminId, IdentityOf(await newer.WaitAsync(Ct)));

            // ...and after the newer one has won: still the ORIGINAL (older) order, so still denied.
            olderGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));
            Assert.Null(await older.WaitAsync(Ct));
            Assert.Null(await reacquiredWhilePending.WaitAsync(Ct));

            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.Equal(AdminId, IdentityOf(accessor.EstablishedIdentity));

            // THE CURRENT TASK STILL WORKS when the SAME object is re-acquired: it holds the newest
            // order, so its (idempotent) observation is served.
            Assert.Equal(AdminId, IdentityOf(await newer));
            Assert.Equal(AdminId, IdentityOf(
                await ComponentActingUser.AcquireAsync(newerGate.Task, accessor)));
            Assert.Equal(AdminId, IdentityOf(accessor.EstablishedIdentity));
        });
    }

    /// <summary>
    /// REACQUIRING THE EXACT T1 TASK AFTER T2 WON: the replay row above covers a second
    /// acquisition of the exact older object only WHILE T2 is pending. This row closes the other half of
    /// the ReplaySuperseded cell: once T2 has completed and published its identity, reacquiring the
    /// EXACT older task object is still DENIED (it keeps its first, superseded order), while reacquiring
    /// the EXACT current T2 object is still ALLOWED. The accessor's state and identity are unchanged by
    /// every replay, so a call-count-only re-promote is impossible.
    /// </summary>
    [Fact]
    public async Task ReacquiringTheExactOlderTask_AfterTheNewerOneWon_StaysDenied_WhileTheExactCurrentTaskStillWorks()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();
        Assert.Equal(AdminId, IdentityOf(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor)));
        var identityBefore = accessor.EstablishedIdentity;
        Assert.NotNull(identityBefore);

        var olderGate = NewGate();
        var newerGate = NewGate();
        var exactOlder = olderGate.Task;
        var exactCurrent = newerGate.Task;
        var older = ComponentActingUser.AcquireAsync(exactOlder, accessor).AsTask();
        var newer = ComponentActingUser.AcquireAsync(exactCurrent, accessor).AsTask();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        work.ReleaseBeforeJoin(() => newerGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(older);
        _ = work.Track(newer);
        await work.RunAsync(async () =>
        {
            Assert.False(older.IsCompleted);
            Assert.False(newer.IsCompleted);

            // T2 WINS. The exact task objects are retained; this is a replay, not a new registration.
            newerGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));
            Assert.Equal(AdminId, IdentityOf(await newer.WaitAsync(Ct)));
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.Same(identityBefore, accessor.EstablishedIdentity);

            // REPLAY the EXACT older T1 object AFTER T2 WON: still superseded, still denied.
            var replayAfterWin = work.Track(ComponentActingUser.AcquireAsync(exactOlder, accessor).AsTask());
            olderGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));
            Assert.Null(await older.WaitAsync(Ct));
            Assert.Null(await replayAfterWin.WaitAsync(Ct));
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.Same(identityBefore, accessor.EstablishedIdentity);
            Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

            // REPLAY the EXACT current T2 object: still current, still served.
            Assert.Equal(AdminId, IdentityOf(
                await ComponentActingUser.AcquireAsync(exactCurrent, accessor)));
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.Same(identityBefore, accessor.EstablishedIdentity);
        });
    }

    /// <summary>
    /// CURRENT SIGN-OUT AND REPLACEMENT REMAIN PERMANENTLY INVALIDATING even with late older work and a
    /// fresh task carrying the ORIGINAL account in flight. A CURRENT sign-out (or a current
    /// replacement) invalidates; the late older task and the fresh original-account task are then both
    /// superseded-or-invalidated and restore nothing.
    /// </summary>
    [Theory]
    [InlineData("sign-out", CircuitIdentityState.SignedOut)]
    [InlineData("replacement", CircuitIdentityState.IdentityReplaced)]
    public async Task CurrentSignOutOrReplacement_StaysTerminal_DespiteLateOlderWorkAndAFreshOriginalTask(
        string terminal, CircuitIdentityState expected)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(MemberId, UserRole.Admin);
        var accessor = Accessor();
        Assert.Equal(AdminId, IdentityOf(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor)));

        // An OLDER task is parked before the CURRENT invalidating update arrives.
        var olderGate = NewGate();
        var older = ComponentActingUser.AcquireAsync(olderGate.Task, accessor).AsTask();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(older);
        await work.RunAsync(async () =>
        {
            Assert.False(older.IsCompleted);

            // THE CURRENT UPDATE invalidates the circuit permanently.
            var invalidating = terminal == "sign-out"
                ? new AuthenticationState(FacadeAuthorizationHarness.Anonymous())
                : new AuthenticationState(FacadeAuthorizationHarness.Principal(MemberId));
            Assert.Null(await ComponentActingUser.AcquireAsync(Task.FromResult(invalidating), accessor));
            Assert.Equal(expected, accessor.IdentityState);
            Assert.True(accessor.IsInvalidated);

            // The LATE OLDER task now completes with the ORIGINAL account: superseded, so it restores
            // nothing.
            olderGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));
            Assert.Null(await older.WaitAsync(Ct));
            Assert.Equal(expected, accessor.IdentityState);
            Assert.Null(accessor.EstablishedIdentity);

            // A FRESH task carrying the ORIGINAL account is the CURRENT registration and IS observed —
            // and the terminal state still refuses it, so nothing is restored.
            Assert.Null(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor));
            Assert.Equal(expected, accessor.IdentityState);
            Assert.True(accessor.IsInvalidated);
            Assert.Null(accessor.EstablishedIdentity);
            Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
            Assert.Null(await ComponentActingUser.AcquireAsync(null, accessor));
            Assert.Null((await accessor.GetPrincipalAsync(Ct)).Principal);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  FAULT / CANCELLATION OF THE NEWEST TASK
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A FAULTED NEWEST TASK: the exception PROPAGATES to the caller (it is never converted into a
    /// denial), the accessor's identity is UNCHANGED, and there is NO fallback to the older — already
    /// superseded — task, which stays denied even after it completes successfully. The circuit is NOT
    /// terminal, so a fresh valid task still recovers it.
    /// </summary>
    [Fact]
    public async Task AFaultedNewestTask_Propagates_LeavesIdentityUnchanged_AndDoesNotFallBackToTheOlderTask()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();
        Assert.Equal(AdminId, IdentityOf(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor)));
        var identityBefore = accessor.EstablishedIdentity;
        var injected = new InvalidOperationException("Inject: the cascading state provider failed.");

        var olderGate = NewGate();
        var older = ComponentActingUser.AcquireAsync(olderGate.Task, accessor).AsTask();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(older);
        await work.RunAsync(async () =>
        {
            var faulted = NewGate();
            var newest = ComponentActingUser.AcquireAsync(faulted.Task, accessor).AsTask();
            faulted.SetException(injected);

            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() => newest.WaitAsync(Ct));
            Assert.Same(injected, caught); // the caller's own fault, propagated verbatim

            Assert.Same(identityBefore, await ComponentActingUser.AcquireAsync(null, accessor));

            // NO identity change and NO fallback to the older task.
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.Same(identityBefore, accessor.EstablishedIdentity);
            Assert.False(accessor.IsInvalidated);
            Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

            // The older task completes successfully afterwards: it is SUPERSEDED, so it restores nothing
            // either — a failed newest leaves the older one superseded with no fallback.
            olderGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));
            Assert.Null(await older.WaitAsync(Ct));
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.Same(identityBefore, accessor.EstablishedIdentity);

            // RECOVERY ON THIS NON-TERMINAL CIRCUIT: a FRESH valid task is current and is observed.
            Assert.Equal(AdminId, IdentityOf(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor)));
            Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        });
    }

    /// <summary>
    /// A CANCELED NEWEST TASK: <see cref="OperationCanceledException"/> propagates (cancellation is
    /// never converted into a denial), the identity is UNCHANGED, the older task stays superseded, and a
    /// fresh valid task recovers the circuit.
    /// </summary>
    [Fact]
    public async Task ACanceledNewestTask_Propagates_LeavesIdentityUnchanged_AndDoesNotFallBackToTheOlderTask()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();
        Assert.Equal(AdminId, IdentityOf(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor)));
        var identityBefore = accessor.EstablishedIdentity;

        var olderGate = NewGate();
        var older = ComponentActingUser.AcquireAsync(olderGate.Task, accessor).AsTask();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(older);
        await work.RunAsync(async () =>
        {
            var canceled = NewGate();
            var newest = ComponentActingUser.AcquireAsync(canceled.Task, accessor).AsTask();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            canceled.SetCanceled(cancellation.Token);

            var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => newest.WaitAsync(Ct));
            Assert.Equal(cancellation.Token, caught.CancellationToken);
            Assert.Same(identityBefore, await ComponentActingUser.AcquireAsync(null, accessor));

            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.Same(identityBefore, accessor.EstablishedIdentity);
            Assert.False(accessor.IsInvalidated);
            Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

            olderGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));
            Assert.Null(await older.WaitAsync(Ct));
            Assert.Same(identityBefore, accessor.EstablishedIdentity);

            Assert.Equal(AdminId, IdentityOf(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor)));
        });
    }

    /// <summary>
    /// A FAILED NEWEST TASK DOES NOT COST THE CIRCUIT AN ALREADY ESTABLISHED IDENTITY: the genuine
    /// no-cascade ACQUISITION still returns the established identity (identity is not authorization),
    /// while the no-cascade CAPABILITY check still DENIES while authentication is enabled. The contrast
    /// between those two answers is the point: the identity survives, the authority does not.
    /// </summary>
    [Fact]
    public async Task AfterAFailedNewestTask_TheGenuineNoCascadeAcquisitionStillReturnsTheEstablishedIdentity_ButTheCapabilityCheckDenies()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();
        var established = await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor);
        Assert.Equal(AdminId, IdentityOf(established));

        var faulted = NewGate();
        var newest = ComponentActingUser.AcquireAsync(faulted.Task, accessor).AsTask();
        faulted.SetException(new InvalidOperationException("Inject: the cascading state provider failed."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => newest.WaitAsync(Ct));

        // A GENUINE no-cascade acquisition (a child component acquiring before/without its parent).
        Assert.Same(established, await ComponentActingUser.AcquireAsync(null, accessor));
        Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);

        // ...and the no-cascade CAPABILITY question still denies while authentication is enabled.
        foreach (var capability in Enum.GetValues<HiveCapability>())
            Assert.False(await ComponentActingUser.IsAllowedAsync(null, accessor, capability, Ct));
    }

    /// <summary>
    /// NO-CASCADE REFUSAL, FRESH AND TERMINAL, PLUS THE EXPLICIT OPEN SUCCESS: on a FRESH
    /// authentication-enabled circuit a missing cascade is denied as an acquisition AND as a capability,
    /// with nothing initialised; on a TERMINAL circuit it is denied too; in EXPLICIT OPEN mode the
    /// missing cascade is the local-operator decision, so it is served and allowed.
    /// </summary>
    [Fact]
    public async Task NoCascadeAcquisition_IsRefusedWhenFreshAndWhenTerminal_AndSucceedsOnlyInExplicitOpenMode()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        // FRESH: no cascade establishes nothing and answers nothing.
        var fresh = Accessor();
        Assert.Null(await ComponentActingUser.AcquireAsync(null, fresh));
        Assert.False(await ComponentActingUser.IsAllowedAsync(null, fresh, HiveCapability.SharedRead, Ct));
        Assert.Equal(CircuitIdentityState.Uninitialized, fresh.IdentityState);

        // TERMINAL: once the circuit is invalidated, the cascade-less acquisition is denied as well.
        var terminal = Accessor();
        Assert.NotNull(await ComponentActingUser.AcquireAsync(StateFor(AdminId), terminal));
        Assert.Null(await ComponentActingUser.AcquireAsync(
            Task.FromResult(new AuthenticationState(FacadeAuthorizationHarness.Anonymous())), terminal));
        Assert.True(terminal.IsInvalidated);
        Assert.Null(await ComponentActingUser.AcquireAsync(null, terminal));
        Assert.False(await ComponentActingUser.IsAllowedAsync(null, terminal, HiveCapability.SharedRead, Ct));

        // EXPLICIT OPEN: the missing cascade is the local operator, unchanged.
        var open = Accessor(HiveAuthMode.Open);
        Assert.Null(await ComponentActingUser.AcquireAsync(null, open));
        Assert.True(await ComponentActingUser.IsAllowedAsync(null, open, HiveCapability.GlobalAdmin, Ct));
        Assert.Equal(CircuitIdentityState.Uninitialized, open.IdentityState);
    }

    /// <summary>
    /// SUPERSEDED TASKS ARE NOT RETAINED FOR THE CIRCUIT'S LIFETIME. The accessor's observation-order
    /// bookkeeping is keyed WEAKLY by the task: no field of the accessor may hold a STRONG reference to a
    /// <see cref="Task{TResult}"/>, so a superseded task's entry disappears with the task instead of
    /// living for the circuit's lifetime.
    /// <para>
    /// This is asserted STRUCTURALLY (reflection over the accessor's own fields), because a collection
    /// proof is not discriminating here: a task the accessor never saw stays rooted by the calling
    /// method's own async state machine in a Debug test build, so "collected" would fail either way.
    /// The rule is enforced on the ONLY thing that could retain the task — the accessor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheAccessor_HoldsNoStrongReferenceToAnyCascadeTask()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var accessor = Accessor();

        // Exercise the ordering boundary so the bookkeeping really exists when the rule is checked.
        Assert.Equal(AdminId, IdentityOf(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor)));
        var older = NewGate();
        var stale = ComponentActingUser.AcquireAsync(older.Task, accessor).AsTask();
        var work = new CircuitObservationTestWork(JoinTimeout);
        work.ReleaseBeforeJoin(() => older.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(stale);
        await work.RunAsync(async () =>
        {
            Assert.Equal(AdminId, IdentityOf(await ComponentActingUser.AcquireAsync(StateFor(AdminId), accessor)));
            older.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId)));
            Assert.Null(await stale.WaitAsync(Ct));

            var offenders = new List<string>();
            foreach (var field in typeof(CircuitUserPrincipalAccessor)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                // A ConditionalWeakTable holds its KEY weakly and its VALUE strongly: the key may be the
                // cascade task, the value must never be one.
                if (field.FieldType.IsGenericType &&
                    field.FieldType.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>))
                {
                    var arguments = field.FieldType.GetGenericArguments();
                    Assert.Equal(typeof(Task<AuthenticationState>), arguments[0]);
                    if (HoldsATask(arguments[1])) offenders.Add($"{field.Name} values");
                    continue;
                }

                if (HoldsATask(field.FieldType)) offenders.Add(field.Name);
            }

            Assert.True(offenders.Count == 0,
                "the accessor must not retain cascade tasks for the circuit's lifetime: " + string.Join(", ", offenders));
        });
    }

    /// <summary>Whether <paramref name="type"/> is, or is built from, a <see cref="Task"/> type.</summary>
    private static bool HoldsATask(Type type)
    {
        if (type == typeof(Task) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)))
            return true;
        if (type.IsArray && HoldsATask(type.GetElementType()!)) return true;
        return type.IsGenericType && type.GetGenericArguments().Any(HoldsATask);
    }

    [Fact]
    public async Task ReplayCleanup_CanceledScenarioAndEarlyFault_StillReleasesAndJoinsReplay()
    {
        var accessor = Accessor();
        var work = new CircuitObservationTestWork(JoinTimeout);
        var t1 = NewGate();
        var t2 = NewGate();
        using var scenario = new CancellationTokenSource();
        var releaseFault = new InvalidOperationException("first release failed");
        var replayFault = new InvalidOperationException("released replay failed");
        var joinFault = new InvalidOperationException("first join failed");
        work.ReleaseBeforeJoin(() => throw releaseFault);
        work.ReleaseBeforeJoin(() => t1.TrySetException(replayFault));
        work.ReleaseBeforeJoin(() => t2.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(AdminId))));
        _ = work.Track(Task.FromException(joinFault));
        var older = work.Track(ComponentActingUser.AcquireAsync(t1.Task, accessor).AsTask());
        var current = work.Track(ComponentActingUser.AcquireAsync(t2.Task, accessor).AsTask());
        var replay = work.Track(ComponentActingUser.AcquireAsync(t1.Task, accessor).AsTask());

        var error = await Assert.ThrowsAsync<AggregateException>(() => work.RunAsync(() =>
        {
            Assert.False(older.IsCompleted);
            Assert.False(current.IsCompleted);
            Assert.False(replay.IsCompleted);
            scenario.Cancel();
            return Task.FromCanceled(scenario.Token);
        }));

        Assert.Equal(scenario.Token, Assert.IsAssignableFrom<OperationCanceledException>(error.InnerExceptions[0]).CancellationToken);
        Assert.Collection(error.InnerExceptions.Skip(1), ex => Assert.Same(releaseFault, ex),
            ex => Assert.Same(joinFault, ex), ex => Assert.Same(replayFault, ex));
        Assert.True(replay.IsFaulted);
        Assert.True(current.IsCompletedSuccessfully);
        Assert.Equal(AdminId, IdentityOf(await current));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DirectAndCurrentOrderedObservations_ApplyEquivalentTransitions(bool anonymousFirst, bool signOut)
    {
        var direct = Accessor();
        var ordered = Accessor();
        var original = anonymousFirst ? FacadeAuthorizationHarness.Anonymous() : FacadeAuthorizationHarness.Principal(AdminId);
        var same = anonymousFirst ? FacadeAuthorizationHarness.Anonymous() : FacadeAuthorizationHarness.Principal(AdminId);
        var next = signOut ? FacadeAuthorizationHarness.Anonymous() : FacadeAuthorizationHarness.Principal(MemberId);
        foreach (var user in new[] { original, same, next, original })
        {
            var state = new AuthenticationState(user);
            Assert.Same(direct.Observe(state), await ComponentActingUser.AcquireAsync(Task.FromResult(state), ordered));
            Assert.Equal(direct.IdentityState, ordered.IdentityState);
            Assert.Same(direct.EstablishedIdentity, ordered.EstablishedIdentity);
        }
        Assert.Equal((anonymousFirst, signOut) switch
        {
            (true, true) => CircuitIdentityState.Initialized,
            (false, true) => CircuitIdentityState.SignedOut,
            _ => CircuitIdentityState.IdentityReplaced,
        }, ordered.IdentityState);
    }

    private sealed class IdentityReadProbe : ClaimsPrincipal
    {
        public int Reads { get; private set; }
        public override System.Security.Principal.IIdentity? Identity
        {
            get { Reads++; return base.Identity; }
        }
    }

    [Fact]
    public void CompleteObservation_SupersededTicketDoesNotReadClaims_CurrentTicketDoes()
    {
        var accessor = Accessor();
        var staleState = new AuthenticationState(new IdentityReadProbe());
        var currentState = new AuthenticationState(new IdentityReadProbe());
        var older = accessor.RegisterObservation(Task.FromResult(staleState));
        var newer = accessor.RegisterObservation(Task.FromResult(currentState));
        Assert.Null(accessor.CompleteObservation(older, staleState));
        Assert.Equal(0, ((IdentityReadProbe)staleState.User).Reads);
        Assert.Equal(CircuitIdentityState.Uninitialized, accessor.IdentityState);
        Assert.Same(currentState.User, accessor.CompleteObservation(newer, currentState));
        Assert.Equal(1, ((IdentityReadProbe)currentState.User).Reads);
    }

    public void Dispose() => _auth.Dispose();
}
