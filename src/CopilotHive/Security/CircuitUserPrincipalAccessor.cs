using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;

namespace CopilotHive.Security;

/// <summary>
/// The observable lifecycle state of one circuit's identity record (see
/// <see cref="CircuitUserPrincipalAccessor"/>). It records only what the accessor has OBSERVED — it
/// is never authorization truth.
/// </summary>
public enum CircuitIdentityState
{
    /// <summary>No authentication state has ever been observed for this circuit.</summary>
    Uninitialized = 0,

    /// <summary>An identity has been established and no sign-out or replacement has been observed since.</summary>
    Initialized = 1,

    /// <summary>
    /// An explicit clear/sign-out was recorded. PERMANENT: only a NEW scope/accessor (explicit
    /// reauthentication) can establish an identity again.
    /// </summary>
    SignedOut = 2,

    /// <summary>
    /// A DIFFERENT identity (or a signed-out state) was observed after an identity was established.
    /// PERMANENT: the replacement is never adopted and the original is never restored.
    /// </summary>
    IdentityReplaced = 3,
}

/// <summary>
/// THE SCOPED CIRCUIT PRINCIPAL ACCESSOR for one Blazor circuit (one browser session). It records
/// the identity it OBSERVES and holds NO authorization truth of its own.
/// <para>
/// IDENTITY STATE MACHINE (<see cref="IdentityState"/>), one-way and permanent per accessor:
/// <list type="bullet">
/// <item><see cref="CircuitIdentityState.Uninitialized"/> → <see cref="CircuitIdentityState.Initialized"/>
/// when <see cref="Initialize"/>/<see cref="Observe"/> sees the first <see cref="AuthenticationState"/>;</item>
/// <item><see cref="CircuitIdentityState.Initialized"/> → <see cref="CircuitIdentityState.SignedOut"/>
/// on <see cref="Clear"/> or when an explicit signed-out state is observed;</item>
/// <item><see cref="CircuitIdentityState.Initialized"/> → <see cref="CircuitIdentityState.IdentityReplaced"/>
/// when a DIFFERENT identity is observed;</item>
/// <item><see cref="CircuitIdentityState.Uninitialized"/> → <see cref="CircuitIdentityState.SignedOut"/>
/// on <see cref="Clear"/> (an explicit clear is recorded and is distinguishable from never-initialised).</item>
/// </list>
/// <c>SignedOut</c> and <c>IdentityReplaced</c> are terminal: the replacement identity is never adopted,
/// the former identity is never restored from a stale state, and every later
/// <see cref="Initialize"/>/<see cref="Observe"/> is ignored. Recovery requires a NEW scope/accessor.
/// All transitions are atomic, so a stale acquisition task that resumes after a sign-out cannot
/// reintroduce an identity.
/// </para>
/// <para>
/// There is NO authorization cache: EVERY operation RE-RESOLVES the current principal through
/// <see cref="HivePrincipalResolver"/>. <see cref="GetPrincipalAsync"/> and <see cref="IsAllowedAsync"/>
/// never reuse a principal from a previous operation; a role change or a disabled account in the
/// database takes effect on the very next question, and the recorded claims identity is only ever the
/// IDENTITY to re-resolve. There is NO automatic cascading-state wiring: pages supply the state
/// explicitly (see <see cref="CopilotHive.Services.ComponentActingUser"/>).
/// </para>
/// <para>
/// FAIL-CLOSED: while authentication is enabled, a never-initialised accessor, a signed-out accessor
/// and an identity-replaced accessor all refuse with
/// <see cref="PrincipalRefusalReason.AccessorNotInitialized"/>; an initialised one refuses whenever
/// the resolver refuses. A refusal yields no principal. In explicit open (no-auth) mode the accessor
/// mirrors the resolver and reports the local operator without needing an initialisation, because
/// that mode carries no per-circuit identity (so there is nothing to invalidate).
/// </para>
/// <para>
/// SINGLETON SAFETY: this type is registered SCOPED (one instance per circuit) precisely so that
/// nothing global captures a user principal. Singletons and facades must never hold an
/// <see cref="ActingUserPrincipal"/> or this accessor: principals flow EXPLICITLY, per operation.
/// </para>
/// </summary>
public sealed class CircuitUserPrincipalAccessor
{
    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy;
    private readonly object _gate = new();

    // The IDENTITY recorded by the observation that initialised this accessor: null when never
    // initialised or after sign-out/replacement. It is deliberately the ClaimsPrincipal (an
    // identity), NOT an ActingUserPrincipal (authorization truth), so no stale role/enabled state
    // can be replayed. It is the ONLY ClaimsPrincipal-typed field of this type.
    private ClaimsPrincipal? _claimsPrincipal;

    // The comparison key of the recorded identity: null for a signed-out (unauthenticated) state.
    private string? _identityKey;
    private CircuitIdentityState _state = CircuitIdentityState.Uninitialized;

    /// <summary>
    /// Initialises a new <see cref="CircuitUserPrincipalAccessor"/>.
    /// </summary>
    /// <param name="resolver">The resolver every operation re-resolves through.</param>
    /// <param name="policy">The central policy evaluator.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public CircuitUserPrincipalAccessor(HivePrincipalResolver resolver, HivePolicy policy)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>Whether an identity is currently recorded (initialised and neither signed out nor replaced).</summary>
    public bool IsInitialized => IdentityState == CircuitIdentityState.Initialized;

    /// <summary>The recorded lifecycle state; sign-out/replacement are distinguishable from never-initialised.</summary>
    public CircuitIdentityState IdentityState
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>
    /// Whether this circuit's identity was permanently invalidated (explicit sign-out or a different
    /// identity observed). Once <c>true</c> it never becomes <c>false</c> again.
    /// </summary>
    public bool IsInvalidated => IdentityState is CircuitIdentityState.SignedOut or CircuitIdentityState.IdentityReplaced;

    /// <summary>
    /// The established AUTHENTICATED claims identity, or <c>null</c> when none was established, the
    /// recorded state was signed-out, or the identity was invalidated. It is an identity only — never
    /// authorization truth.
    /// </summary>
    public ClaimsPrincipal? EstablishedIdentity
    {
        get
        {
            lock (_gate)
            {
                return _state == CircuitIdentityState.Initialized && _identityKey is not null
                    ? _claimsPrincipal
                    : null;
            }
        }
    }

    /// <summary>The explicit authentication mode the resolver was configured with.</summary>
    public bool AuthEnabled => _resolver.Mode.AuthEnabled;

    /// <summary>
    /// EXPLICIT initialisation entry point: records the circuit's authentication state (see
    /// <see cref="Observe"/> for the exact transition rules). It stores only the state's claims
    /// identity — no role, no enabled flag and no capability is cached.
    /// </summary>
    /// <param name="authenticationState">The circuit's authentication state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="authenticationState"/> is null.</exception>
    public void Initialize(AuthenticationState authenticationState) => _ = Observe(authenticationState);

    /// <summary>
    /// Observes one <see cref="AuthenticationState"/> atomically and applies the identity state machine:
    /// the first state establishes the identity; the SAME identity later observed changes nothing; a
    /// DIFFERENT identity or an explicit signed-out state after an established identity permanently
    /// invalidates the circuit; an already invalidated circuit ignores every observation (nothing is
    /// adopted or restored).
    /// </summary>
    /// <param name="authenticationState">The observed state.</param>
    /// <returns>
    /// The identity established for this circuit, or <c>null</c> when the circuit is (now) invalidated.
    /// A first-observed signed-out state is recorded as an initialised anonymous identity and returned
    /// (the resolver refuses it as unauthenticated).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authenticationState"/> is null.</exception>
    public ClaimsPrincipal? Observe(AuthenticationState authenticationState)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);

        var user = authenticationState.User;
        var key = IdentityKeyOf(user);

        lock (_gate)
        {
            switch (_state)
            {
                case CircuitIdentityState.SignedOut:
                case CircuitIdentityState.IdentityReplaced:
                    // Permanent: never adopt a replacement, never restore a former identity.
                    return null;

                case CircuitIdentityState.Uninitialized:
                    _claimsPrincipal = user;
                    _identityKey = key;
                    _state = CircuitIdentityState.Initialized;
                    return user;

                default:
                    if (string.Equals(_identityKey, key, StringComparison.Ordinal))
                        return user;

                    // A signed-out state after an established identity is a sign-out; any other
                    // difference is a replacement. Either way the recorded identity is dropped.
                    _state = key is null && _identityKey is not null
                        ? CircuitIdentityState.SignedOut
                        : CircuitIdentityState.IdentityReplaced;
                    _claimsPrincipal = null;
                    _identityKey = null;
                    return null;
            }
        }
    }

    /// <summary>
    /// Records an explicit sign-out/clear. PERMANENT: the recorded identity is forgotten, the state
    /// becomes <see cref="CircuitIdentityState.SignedOut"/> (distinguishable from never-initialised)
    /// and no later <see cref="Initialize"/>/<see cref="Observe"/> can establish an identity again —
    /// recovery requires a NEW scope/accessor. An identity-replaced accessor stays replaced.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _claimsPrincipal = null;
            _identityKey = null;
            if (_state != CircuitIdentityState.IdentityReplaced)
                _state = CircuitIdentityState.SignedOut;
        }
    }

    /// <summary>
    /// Resolves the CURRENT acting principal for this circuit, re-reading the account from the
    /// directory on every call (no cache).
    /// </summary>
    /// <param name="ct">Cancellation token; cancellation is never converted into a refusal.</param>
    /// <returns>A resolved principal, or a fail-closed refusal that carries no principal.</returns>
    public Task<PrincipalResolution> GetPrincipalAsync(CancellationToken ct) =>
        ResolveAsync(ct);

    /// <summary>
    /// Evaluates one capability for the CURRENT principal of this circuit, re-resolving it first so a
    /// role change or disablement is honoured immediately.
    /// </summary>
    /// <param name="capability">The capability being asked about.</param>
    /// <param name="ct">Cancellation token; cancellation is never converted into a refusal.</param>
    /// <returns><c>true</c> only when the current principal is resolved AND satisfies the capability.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is not a defined capability.</exception>
    public async Task<bool> IsAllowedAsync(HiveCapability capability, CancellationToken ct)
    {
        // Validate the capability before any I/O so a programming error is never masked as a refusal.
        _ = HivePolicy.MinimumRole(capability);

        var resolution = await ResolveAsync(ct).ConfigureAwait(false);
        return resolution.Principal is { } principal && _policy.IsAllowed(principal, capability);
    }

    /// <summary>
    /// The comparison key of an identity: <c>null</c> for an unauthenticated (signed-out) principal,
    /// otherwise the GitHub identity claim the resolver itself looks accounts up by (ordinal,
    /// verbatim — a difference of any kind is treated as a DIFFERENT identity, the fail-closed
    /// direction).
    /// </summary>
    private static string? IdentityKeyOf(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty
            : null;

    /// <summary>
    /// Re-resolves through <see cref="HivePrincipalResolver"/>, or refuses when this circuit has no
    /// valid identity while authentication is enabled. The invalidation state is read BEFORE the
    /// directory read and AGAIN after it, so a sign-out that lands while the read is in flight is
    /// never answered with a principal.
    /// </summary>
    private async Task<PrincipalResolution> ResolveAsync(CancellationToken ct)
    {
        ClaimsPrincipal? recorded;
        CircuitIdentityState state;
        lock (_gate)
        {
            recorded = _claimsPrincipal;
            state = _state;
        }

        if (_resolver.Mode.AuthEnabled && state != CircuitIdentityState.Initialized)
        {
            return state == CircuitIdentityState.Uninitialized
                ? PrincipalResolution.Refused(
                    PrincipalRefusalReason.AccessorNotInitialized,
                    "The circuit principal accessor has not been initialised from an AuthenticationState.")
                : PrincipalResolution.Refused(
                    PrincipalRefusalReason.AccessorNotInitialized,
                    "The circuit identity was permanently invalidated (sign-out or a different identity); a new scope is required.");
        }

        // Open mode carries no per-circuit identity, so it needs no initialisation; the resolver
        // yields the local operator and ignores the claims.
        var resolution = await _resolver.ResolveAsync(
            state == CircuitIdentityState.Initialized ? recorded : null, ct).ConfigureAwait(false);

        if (_resolver.Mode.AuthEnabled && resolution.IsResolved && IdentityState != CircuitIdentityState.Initialized)
        {
            return PrincipalResolution.Refused(
                PrincipalRefusalReason.AccessorNotInitialized,
                "The circuit identity was permanently invalidated while the principal was being resolved.");
        }

        return resolution;
    }
}
