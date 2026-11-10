using System.Security.Claims;

using CopilotHive.Security;

using Microsoft.AspNetCore.Components.Authorization;

namespace CopilotHive.Services;

/// <summary>
/// The ONE way a Blazor component obtains the circuit identity it hands to an identity-aware facade,
/// initialises the scoped <see cref="CircuitUserPrincipalAccessor"/>, and asks an authorization
/// question about the CURRENT circuit user.
/// <para>
/// The identity returned here is a <see cref="ClaimsPrincipal"/> — an IDENTITY only, never
/// authorization truth. The facades re-resolve the caller's current role/enabled state from the
/// trusted directory on every operation, so a stale page can never grant anything: a demoted or
/// disabled account is refused on its very next call.
/// </para>
/// <para>
/// OPEN MODE / SIGNED OUT: <c>Program.cs</c> registers <c>AddCascadingAuthenticationState</c> ONLY
/// when authentication is enabled, so in open mode there is no cascading
/// <see cref="AuthenticationState"/> at all and this type never fabricates one. A missing cascade
/// yields a <c>null</c> identity (the resolver then answers with the open-mode local operator) and
/// grants the OPEN-MODE UI capability answer, which is exactly the legacy single-operator
/// installation. Nothing here throws when authentication services are absent.
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
    /// Performs the MINIMAL circuit identity acquisition for one component render/session: awaits the
    /// cascading state if there is one, initialises the scoped
    /// <see cref="CircuitUserPrincipalAccessor"/> with it EXACTLY ONCE per circuit, and returns the
    /// identity to hand to an identity-aware facade.
    /// <para>
    /// The accessor stores only the claims identity; every question asked of it re-resolves current
    /// stored state, so initialising once cannot pin a stale role. When no cascade exists (open mode)
    /// the accessor is deliberately left UNINITIALISED — in open mode it needs no initialisation and
    /// still reports the local operator, and it must never be seeded with a fabricated state.
    /// </para>
    /// </summary>
    /// <param name="authenticationState">The cascading state, or <c>null</c> when it was not cascaded.</param>
    /// <param name="accessor">The SCOPED circuit accessor of the calling component.</param>
    /// <returns>The circuit's authenticated principal, or <c>null</c> in open mode / when signed out.</returns>
    public static async ValueTask<ClaimsPrincipal?> AcquireAsync(
        Task<AuthenticationState>? authenticationState,
        CircuitUserPrincipalAccessor? accessor)
    {
        if (authenticationState is null)
            return null;

        var state = await authenticationState;

        // Initialise ONCE per circuit: re-initialising on every call would discard nothing, but an
        // un-initialised accessor that the page never seeds could never answer a capability question.
        if (accessor is { IsInitialized: false })
            accessor.Initialize(state);

        return state.User;
    }

    /// <summary>
    /// Answers whether the CURRENT circuit user holds <paramref name="capability"/> right now.
    /// <para>
    /// In open mode (no cascade) no authentication state exists, so the question is answered from the
    /// EXPLICIT open-mode decision <c>Program.cs</c> already made — the local operator, which holds
    /// every capability — WITHOUT inventing an <see cref="AuthenticationState"/> for the accessor.
    /// With a cascade, the SCOPED accessor re-resolves the principal from the trusted directory on
    /// every call, so a demotion or disablement is reflected immediately.
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

        // Open mode: the host registered NO authentication services, so there is no cascade to ask and
        // no circuit principal to resolve. This mirrors the facade path, where the resolver yields the
        // full local operator in that same explicit mode.
        if (authenticationState is null)
            return true;

        if (accessor is null)
            return false;

        await AcquireAsync(authenticationState, accessor);

        return await accessor.IsAllowedAsync(capability, ct);
    }
}
