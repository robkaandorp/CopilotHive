using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;

namespace CopilotHive.Security;

/// <summary>
/// THE SCOPED CIRCUIT PRINCIPAL ACCESSOR for one Blazor circuit (one browser session). It is
/// EXPLICITLY INITIALISABLE and holds NO authorization truth of its own.
/// <para>
/// <see cref="Initialize"/> supplies the circuit's <see cref="AuthenticationState"/> — nothing is
/// inferred, and there is NO automatic cascading-state wiring: no component, route or
/// <c>AuthorizeView</c> calls this today, so later route/UI integration must explicitly initialise it
/// from the cascading <see cref="AuthenticationState"/> (for example in the layout/route host) and
/// must therefore treat this accessor as un-initialised until it does.
/// </para>
/// <para>
/// EVERY operation RE-RESOLVES the current principal through <see cref="HivePrincipalResolver"/>:
/// <see cref="GetPrincipalAsync"/> and <see cref="IsAllowedAsync"/> never reuse the principal from a
/// previous operation or from <see cref="Initialize"/>. A role change or a disabled account in the
/// database therefore takes effect on the very next question asked, and the principal supplied at
/// initialisation is only ever used as the IDENTITY to re-resolve — never as authorization truth.
/// </para>
/// <para>
/// FAIL-CLOSED: while authentication is enabled, an un-initialised accessor and
/// <see cref="Clear"/>d accessor refuse with
/// <see cref="PrincipalRefusalReason.AccessorNotInitialized"/>, and an initialised one refuses
/// whenever the resolver refuses (see <see cref="HivePrincipalResolver"/>). A refusal yields no
/// principal, so a caller cannot fall through to an anonymous identity. In open (no-auth) mode the
/// accessor mirrors the resolver and reports the local operator without needing an initialisation,
/// because that mode carries no per-circuit identity.
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

    // The IDENTITY supplied by the last Initialize call: null when un-initialised or cleared. It is
    // deliberately the ClaimsPrincipal (an identity), NOT an ActingUserPrincipal (authorization
    // truth), so no stale role/enabled state can be replayed.
    private ClaimsPrincipal? _claimsPrincipal;
    private bool _initialized;

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

    /// <summary>Whether <see cref="Initialize"/> has been called and not yet cleared.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>The explicit authentication mode the resolver was configured with.</summary>
    public bool AuthEnabled => _resolver.Mode.AuthEnabled;

    /// <summary>
    /// EXPLICIT initialisation entry point: records the circuit's authentication state so later
    /// operations can re-resolve it. It stores only the state's claims identity — no role, no
    /// enabled flag and no capability is cached, so a later database change is never masked.
    /// </summary>
    /// <param name="authenticationState">The circuit's authentication state (a signed-out state is accepted and fails closed).</param>
    /// <exception cref="ArgumentNullException"><paramref name="authenticationState"/> is null.</exception>
    public void Initialize(AuthenticationState authenticationState)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);

        _claimsPrincipal = authenticationState.User;
        _initialized = true;
    }

    /// <summary>
    /// Clears the initialisation (for example on sign-out). After this, an authentication-enabled
    /// accessor refuses until <see cref="Initialize"/> is called again.
    /// </summary>
    public void Clear()
    {
        _claimsPrincipal = null;
        _initialized = false;
    }

    /// <summary>
    /// Resolves the CURRENT acting principal for this circuit, re-reading the account from the
    /// directory on every call.
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
    /// Re-resolves through <see cref="HivePrincipalResolver"/>, or refuses when this circuit has no
    /// initialisation while authentication is enabled.
    /// </summary>
    private async Task<PrincipalResolution> ResolveAsync(CancellationToken ct)
    {
        if (!_initialized)
        {
            // Open mode carries no per-circuit identity, so it needs no initialisation; the resolver
            // itself refuses in every other case.
            if (_resolver.Mode.AuthEnabled)
            {
                return PrincipalResolution.Refused(
                    PrincipalRefusalReason.AccessorNotInitialized,
                    "The circuit principal accessor has not been initialised from an AuthenticationState.");
            }

            return await _resolver.ResolveAsync(claimsPrincipal: null, ct).ConfigureAwait(false);
        }

        // The recorded ClaimsPrincipal is used as IDENTITY only: the resolver re-reads role, enabled
        // state and originality from the directory, so nothing cached here can grant authority.
        return await _resolver.ResolveAsync(_claimsPrincipal, ct).ConfigureAwait(false);
    }
}
