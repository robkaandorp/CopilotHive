using System.Security.Claims;

using CopilotHive.Security;

using Microsoft.AspNetCore.Components.Authorization;

namespace CopilotHive.Services;

/// <summary>
/// The ONE way a Blazor component obtains the circuit identity it hands to an identity-aware facade,
/// observes it on the scoped <see cref="CircuitUserPrincipalAccessor"/>, and asks an authorization
/// question about the CURRENT circuit user.
/// <para>
/// The identity returned here is a <see cref="ClaimsPrincipal"/> — an IDENTITY only, never
/// authorization truth. The facades re-resolve the caller's current role/enabled state from the
/// trusted directory on every operation, so a stale page can never grant anything: a demoted or
/// disabled account is refused on its very next call. This helper adds NO authorization cache.
/// </para>
/// <para>
/// EXPLICIT STATES. <c>Program.cs</c> registers <c>AddCascadingAuthenticationState</c> ONLY when
/// authentication is enabled, so:
/// <list type="bullet">
/// <item><b>Explicit open mode</b> (<see cref="CircuitUserPrincipalAccessor.AuthEnabled"/> is
/// <c>false</c>): there is no cascade, the identity is <c>null</c> (the resolver then answers with the
/// local operator) and the capability question is answered <c>true</c>. Nothing is fabricated or
/// initialised.</item>
/// <item><b>Unknown mode</b> (no cascade and no accessor, or no cascade with authentication enabled):
/// DENIED. A missing cascade is never evidence of open mode and NEVER creates or initialises an
/// identity.</item>
/// <item><b>Cascade present</b>: the state is OBSERVED by the accessor (not initialised "once"): the
/// same identity is idempotent, while a DIFFERENT identity or a signed-out state after an established
/// identity PERMANENTLY invalidates the circuit's identity — see
/// <see cref="CircuitUserPrincipalAccessor"/>. The replacement is never adopted and the original is
/// never restored; recovery requires a new scope (explicit reauthentication).</item>
/// <item><b>Missing cascade after an established identity</b>: an acquisition (not the capability
/// question) is served the established identity only while no sign-out/replacement has been observed;
/// the facade or accessor then still re-resolves it freshly from the directory (enabled, valid role),
/// so "current" means "no observed invalidation", not browser-session authority. This keeps a child
/// component that acquires before/without its parent safe.</item>
/// </list>
/// Nothing here throws when authentication services are absent.
/// </para>
/// <para>
/// SINGLETON SAFETY: the accessor is a SCOPED per-circuit service, so it is always passed IN and
/// never captured by anything global; the singleton facades never see it.
/// </para>
/// </summary>
public static class ComponentActingUser
{
    /// <summary>
    /// Awaits the cascading authentication state and returns its user, or <c>null</c> when the
    /// cascade is absent (open mode / signed out).
    /// </summary>
    /// <param name="authenticationState">The cascading state, or <c>null</c> when it was not cascaded.</param>
    /// <returns>The authenticated principal, or <c>null</c>.</returns>
    public static async ValueTask<ClaimsPrincipal?> FromAsync(Task<AuthenticationState>? authenticationState) =>
        authenticationState is null ? null : (await authenticationState).User;

    /// <summary>
    /// Performs the circuit identity acquisition for one component render/session: awaits the cascading
    /// state if there is one, OBSERVES it on the scoped <see cref="CircuitUserPrincipalAccessor"/> and
    /// returns the identity to hand to an identity-aware facade, or <c>null</c> when there is none.
    /// <list type="bullet">
    /// <item>Cascade present: the accessor's atomic observation decides. The first state establishes the
    /// identity; the same identity is idempotent; a different identity or a signed-out state after an
    /// established identity permanently invalidates the circuit and yields <c>null</c>; an already
    /// invalidated circuit yields <c>null</c> without adopting or restoring anything. The invalidation
    /// check happens AT THE AWAIT'S RESUMPTION (inside the observation), so a stale task that completes
    /// after another call observed a sign-out writes nothing and returns <c>null</c>.</item>
    /// <item>No cascade: never initialises the accessor. Returns the already established identity ONLY
    /// when authentication is enabled and no invalidation has been observed; otherwise <c>null</c>
    /// (explicit open mode, unknown mode, never established, or invalidated).</item>
    /// </list>
    /// Every question later asked still re-resolves current stored state, so a returned identity pins
    /// no role.
    /// </summary>
    /// <param name="authenticationState">The cascading state, or <c>null</c> when it was not cascaded.</param>
    /// <param name="accessor">The SCOPED circuit accessor of the calling component.</param>
    /// <returns>The circuit's authenticated principal, or <c>null</c> in open mode / when signed out or invalidated.</returns>
    public static async ValueTask<ClaimsPrincipal?> AcquireAsync(
        Task<AuthenticationState>? authenticationState,
        CircuitUserPrincipalAccessor? accessor)
    {
        if (authenticationState is null)
        {
            // A missing cascade NEVER creates or initialises an identity. Only an identity that was
            // already established (and not invalidated) may be served, and only when auth is enabled.
            return accessor is { AuthEnabled: true } ? accessor.EstablishedIdentity : null;
        }

        var state = await authenticationState;

        // Resumption point: the accessor re-checks its CURRENT invalidation state atomically with the
        // write, so a late completion after a sign-out can neither initialise nor restore identity.
        return accessor is null ? state.User : accessor.Observe(state);
    }

    /// <summary>
    /// Answers whether the CURRENT circuit user holds <paramref name="capability"/> right now.
    /// <para>
    /// With NO cascade the answer comes from the explicit mode only: <c>true</c> ONLY when the accessor
    /// reports explicit open mode (<see cref="CircuitUserPrincipalAccessor.AuthEnabled"/> is
    /// <c>false</c>; the local operator holds every capability); <c>false</c> when there is no accessor
    /// or authentication is enabled (unknown mode denies). A missing cascade never creates or
    /// initialises an identity. With a cascade, the state is observed (see <see cref="AcquireAsync"/>)
    /// and the SCOPED accessor re-resolves the principal from the trusted directory on every call; an
    /// invalidated circuit denies.
    /// </para>
    /// </summary>
    /// <param name="authenticationState">The cascading state, or <c>null</c> when it was not cascaded.</param>
    /// <param name="accessor">The SCOPED circuit accessor of the calling component.</param>
    /// <param name="capability">The capability to evaluate.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns><c>true</c> only when the current circuit user holds the capability.</returns>
    public static async ValueTask<bool> IsAllowedAsync(
        Task<AuthenticationState>? authenticationState,
        CircuitUserPrincipalAccessor? accessor,
        HiveCapability capability,
        CancellationToken ct)
    {
        // Validate before any I/O so a programming error is never masked as a denial.
        _ = HivePolicy.MinimumRole(capability);

        // No cascade: only the EXPLICIT open-mode decision may allow. Unknown mode (no accessor, or
        // auth enabled) denies, and nothing is initialised.
        if (authenticationState is null)
            return accessor is { AuthEnabled: false };

        if (accessor is null)
            return false;

        // A null identity means the observation invalidated (or found invalidated) the circuit.
        if (await AcquireAsync(authenticationState, accessor) is null && accessor.AuthEnabled)
            return false;

        return await accessor.IsAllowedAsync(capability, ct);
    }
}
