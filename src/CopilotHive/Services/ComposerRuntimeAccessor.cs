using System.Security.Claims;

using CopilotHive.Orchestration;
using CopilotHive.Security;

using Microsoft.AspNetCore.Components.Authorization;

namespace CopilotHive.Services;

/// <summary>
/// The outcome of ONE per-user runtime selection: whether the caller was AUTHORIZED to reach a
/// Composer at all, plus the manager's typed <see cref="ComposerRuntimeResult"/> when it was.
/// </summary>
/// <remarks>
/// A denied access carries NO runtime and no manager outcome, so an unauthorized caller can never
/// observe Composer content — or even whether a runtime exists. The manager outcomes are handed
/// through untouched: this type adds the authorization answer, never a second status model.
/// </remarks>
public sealed record ComposerRuntimeAccess
{
    private ComposerRuntimeAccess(bool isAuthorized, ComposerRuntimeResult? result)
    {
        IsAuthorized = isAuthorized;
        Result = result;
    }

    /// <summary>Whether the caller currently holds <see cref="HiveCapability.InteractiveMember"/>.</summary>
    public bool IsAuthorized { get; }

    /// <summary>The manager's outcome, or <c>null</c> when the caller was not authorized.</summary>
    public ComposerRuntimeResult? Result { get; }

    /// <summary>The manager's reported status, or <see cref="ComposerRuntimeStatus.Refused"/> when not authorized.</summary>
    public ComposerRuntimeStatus Status => Result?.Status ?? ComposerRuntimeStatus.Refused;

    /// <summary>
    /// The shared per-user runtime for the granted statuses (connected, shell, deferred), or
    /// <c>null</c> when there is none (denied, refused, failed, removed).
    /// </summary>
    public ComposerUserRuntime? Runtime => Result?.Runtime;

    /// <summary>The single "not authorized" access: no runtime, no manager outcome.</summary>
    internal static ComposerRuntimeAccess Denied() => new(isAuthorized: false, result: null);

    /// <summary>The single "authorized" access carrying the manager's typed outcome.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    internal static ComposerRuntimeAccess Granted(ComposerRuntimeResult result) =>
        new(isAuthorized: true, result ?? throw new ArgumentNullException(nameof(result)));
}

/// <summary>
/// THE SCOPED RUNTIME-ACCESS SEAM: turns one caller's identity into the shared per-user
/// <see cref="ComposerUserRuntime"/> owned by the SINGLETON <see cref="ComposerRuntimeManager"/>.
/// </summary>
/// <remarks>
/// <para>
/// It exists so the facade that consumes runtime selection depends on an ABSTRACTION while the
/// production implementation (<see cref="ComposerRuntimeAccessor"/>) stays the single wiring of
/// manager + scoped circuit accessor + resolver/policy. Tests substitute an implementation to pin the
/// exact recovery mapping of every manager outcome, and they can only do so because the real manager and
/// the real accessor are also exercised elsewhere — the seam never widens what a caller may ask.
/// </para>
/// <para>
/// EVERY method re-resolves the caller from CURRENT stored state and requires
/// <see cref="HiveCapability.InteractiveMember"/>; no implementation may cache an identity or a decision
/// between calls. Caller cancellation always propagates.
/// </para>
/// </remarks>
public interface IComposerRuntimeAccessor
{
    /// <summary>
    /// The EXPLICIT host authentication mode; <c>false</c> means open/no-auth mode, in which the
    /// resolver reports the synthetic local operator (no persisted account, therefore no runtime).
    /// </summary>
    bool AuthEnabled { get; }

    /// <summary>Selects (creating on first use) the CURRENT request caller's runtime.</summary>
    /// <param name="actingUser">The request's authenticated principal, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation; it propagates.</param>
    /// <returns>A denied access, or the manager's typed outcome.</returns>
    Task<ComposerRuntimeAccess> AcquireForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>Drives the CURRENT request caller's deferred connect.</summary>
    /// <param name="actingUser">The request's authenticated principal, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation; it propagates.</param>
    /// <returns>A denied access, or the manager's typed outcome.</returns>
    Task<ComposerRuntimeAccess> ConnectForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>Selects (creating on first use) the CURRENT circuit user's runtime.</summary>
    /// <param name="authenticationState">The cascading authentication state, or <c>null</c> when there is none.</param>
    /// <param name="ct">Caller cancellation; it propagates.</param>
    /// <returns>A denied access, or the manager's typed outcome.</returns>
    Task<ComposerRuntimeAccess> AcquireForCircuitAsync(Task<AuthenticationState>? authenticationState, CancellationToken ct);

    /// <summary>Drives the CURRENT circuit user's deferred connect.</summary>
    /// <param name="authenticationState">The cascading authentication state, or <c>null</c> when there is none.</param>
    /// <param name="ct">Caller cancellation; it propagates.</param>
    /// <returns>A denied access, or the manager's typed outcome.</returns>
    Task<ComposerRuntimeAccess> ConnectForCircuitAsync(Task<AuthenticationState>? authenticationState, CancellationToken ct);
}

/// <summary>
/// THE SCOPED RUNTIME ACCESSOR: the ONE place a request or a circuit turns its identity into the
/// shared per-user <see cref="ComposerUserRuntime"/> owned by the SINGLETON
/// <see cref="ComposerRuntimeManager"/>.
/// </summary>
/// <remarks>
/// <para>
/// LIFETIMES: this type is registered SCOPED and wraps the SINGLETON manager plus the SCOPED
/// <see cref="CircuitUserPrincipalAccessor"/>. Nothing global (no singleton, no startup closure) may
/// capture it: a singleton holding it would leak one circuit's identity into every other circuit and
/// outlive the scope that owns it. It never holds or forwards an <c>HttpContext</c>, so no
/// request-scoped interactive state is captured either.
/// </para>
/// <para>
/// IDENTITY PER OPERATION, ALWAYS FRESH: every operation re-resolves the acting principal from
/// CURRENT stored state — through <see cref="HivePrincipalResolver.ResolveAsync"/> for the
/// per-request <see cref="ClaimsPrincipal"/> path, and through
/// <see cref="CircuitUserPrincipalAccessor.GetPrincipalAsync"/> for the circuit path (the circuit
/// accessor stores only a claims IDENTITY and re-resolves on every call). Nothing is cached between
/// operations, so a demotion or disablement lands on the very next call.
/// </para>
/// <para>
/// AUTHORIZATION IS PART OF EVERY OPERATION: the resolved principal is evaluated against
/// <see cref="HiveCapability.InteractiveMember"/> through <see cref="HivePolicy"/> BEFORE the manager
/// is asked for anything. A Viewer, a disabled account, an unreadable directory and a signed-out
/// caller all yield <see cref="ComposerRuntimeAccess.IsAuthorized"/> = <c>false</c> and NO runtime.
/// A refusal is never an exception, and caller cancellation always propagates (it is not an
/// authorization answer and is never converted into a denial).
/// </para>
/// <para>
/// TWO IDENTITY PATHS, ONE RULE:
/// <list type="number">
/// <item>
/// <see cref="AcquireForRequestAsync"/> / <see cref="ConnectForRequestAsync"/> take the caller's
/// per-request <see cref="ClaimsPrincipal"/> DIRECTLY, which is what an HTTP handler already holds.
/// They need no circuit <see cref="AuthenticationState"/> and no circuit initialisation, so a plain
/// request never has to establish circuit state first.
/// </item>
/// <item>
/// <see cref="AcquireForCircuitAsync"/> / <see cref="ConnectForCircuitAsync"/> take the OPTIONAL
/// cascading <see cref="AuthenticationState"/> (absent in open mode) and initialise the scoped
/// circuit accessor ONCE through the shared <see cref="ComponentActingUser.AcquireAsync"/> helper —
/// no component, route or layout is wired to them in this slice, so an un-initialised accessor keeps
/// refusing while authentication is enabled (fail-closed).
/// </item>
/// </list>
/// In both paths the RESOLVER-PRODUCED <see cref="ActingUserPrincipal"/> travels UNCHANGED into the
/// manager: legacy-history migration is keyed on that principal's identity
/// (<see cref="ActingUserPrincipal.IsOriginal"/>, computed from the stored account ids, never from a
/// role), and the non-original provider gate inside the factory is untouched.
/// </para>
/// <para>
/// OPEN MODE: the resolver yields the synthetic local operator, which the manager refuses
/// (<see cref="ComposerRuntimeRefusal.LocalOperator"/>) because it has no persisted account. That
/// refusal is reported here as-is; keeping open mode on the legacy singleton Composer is the
/// CALLER's decision (see the deployment gate on the Composer facade), never this accessor's.
/// </para>
/// </remarks>
public sealed class ComposerRuntimeAccessor : IComposerRuntimeAccessor
{
    private readonly ComposerRuntimeManager _manager;
    private readonly CircuitUserPrincipalAccessor _circuitAccessor;
    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy;
    private readonly ILogger<ComposerRuntimeAccessor> _log;

    /// <summary>Initialises the accessor.</summary>
    /// <param name="manager">The SINGLETON per-user runtime manager (the sole owner of runtimes).</param>
    /// <param name="circuitAccessor">The SCOPED circuit principal accessor of the same scope.</param>
    /// <param name="resolver">The stateless principal resolver every operation re-resolves through.</param>
    /// <param name="policy">The pure capability evaluator.</param>
    /// <param name="log">Logger used for secret-free denial diagnostics.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ComposerRuntimeAccessor(
        ComposerRuntimeManager manager,
        CircuitUserPrincipalAccessor circuitAccessor,
        HivePrincipalResolver resolver,
        HivePolicy policy,
        ILogger<ComposerRuntimeAccessor> log)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _circuitAccessor = circuitAccessor ?? throw new ArgumentNullException(nameof(circuitAccessor));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// The EXPLICIT host authentication mode. <c>false</c> means open/no-auth mode: the resolver
    /// reports the synthetic local operator, which has no persisted account and therefore never gets
    /// a per-user runtime.
    /// </summary>
    public bool AuthEnabled => _resolver.Mode.AuthEnabled;

    /// <summary>
    /// Resolves the caller of the CURRENT request and returns their per-user runtime, creating it on
    /// first use.
    /// </summary>
    /// <param name="actingUser">The request's authenticated principal, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation; it propagates and is never converted into a denial.</param>
    /// <returns>
    /// A denied access (no runtime) for a Viewer, a refusal or an unreadable identity; otherwise the
    /// manager's typed outcome for the resolved account.
    /// </returns>
    public async Task<ComposerRuntimeAccess> AcquireForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        var principal = await AuthorizeRequestAsync(actingUser, nameof(AcquireForRequestAsync), ct).ConfigureAwait(false);
        return principal is null
            ? ComposerRuntimeAccess.Denied()
            : ComposerRuntimeAccess.Granted(await _manager.GetOrCreateAsync(principal, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The awaitable per-user CONNECT (deferred-retry) for the CURRENT request's caller. It is the
    /// only way a deferred runtime is connected from a mutation and never touches the legacy
    /// coordinator or its token signal.
    /// </summary>
    /// <param name="actingUser">The request's authenticated principal, or <c>null</c> when signed out.</param>
    /// <param name="ct">Caller cancellation; it propagates and is never converted into a denial.</param>
    /// <returns>
    /// A denied access (no runtime) for an unauthorized caller; otherwise the manager's typed
    /// outcome (<see cref="ComposerRuntimeRefusal.NoRuntime"/> when the user has no cached runtime).
    /// </returns>
    public async Task<ComposerRuntimeAccess> ConnectForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        var principal = await AuthorizeRequestAsync(actingUser, nameof(ConnectForRequestAsync), ct).ConfigureAwait(false);
        return principal is null
            ? ComposerRuntimeAccess.Denied()
            : ComposerRuntimeAccess.Granted(await _manager.ConnectAsync(principal, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Resolves the CURRENT circuit's user and returns their per-user runtime, creating it on first
    /// use. The circuit accessor is initialised at most ONCE per circuit from the supplied cascading
    /// state; a missing cascade (open mode) leaves it un-initialised, which the accessor handles
    /// exactly as it does today.
    /// </summary>
    /// <param name="authenticationState">The cascading authentication state, or <c>null</c> when there is none.</param>
    /// <param name="ct">Caller cancellation; it propagates and is never converted into a denial.</param>
    /// <returns>A denied access, or the manager's typed outcome for the circuit user.</returns>
    public async Task<ComposerRuntimeAccess> AcquireForCircuitAsync(
        Task<AuthenticationState>? authenticationState, CancellationToken ct)
    {
        var principal = await AuthorizeCircuitAsync(authenticationState, nameof(AcquireForCircuitAsync), ct)
            .ConfigureAwait(false);
        return principal is null
            ? ComposerRuntimeAccess.Denied()
            : ComposerRuntimeAccess.Granted(await _manager.GetOrCreateAsync(principal, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The awaitable per-user connect (deferred-retry) for the CURRENT circuit's user.
    /// </summary>
    /// <param name="authenticationState">The cascading authentication state, or <c>null</c> when there is none.</param>
    /// <param name="ct">Caller cancellation; it propagates and is never converted into a denial.</param>
    /// <returns>A denied access, or the manager's typed outcome for the circuit user.</returns>
    public async Task<ComposerRuntimeAccess> ConnectForCircuitAsync(
        Task<AuthenticationState>? authenticationState, CancellationToken ct)
    {
        var principal = await AuthorizeCircuitAsync(authenticationState, nameof(ConnectForCircuitAsync), ct)
            .ConfigureAwait(false);
        return principal is null
            ? ComposerRuntimeAccess.Denied()
            : ComposerRuntimeAccess.Granted(await _manager.ConnectAsync(principal, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The PER-REQUEST identity path: resolves the principal straight from the request's
    /// <see cref="ClaimsPrincipal"/> and requires <see cref="HiveCapability.InteractiveMember"/>. No
    /// circuit state is involved, so a plain HTTP request needs no circuit initialisation.
    /// </summary>
    private async Task<ActingUserPrincipal?> AuthorizeRequestAsync(
        ClaimsPrincipal? actingUser, string operation, CancellationToken ct)
    {
        var resolution = await _resolver.ResolveAsync(actingUser, ct).ConfigureAwait(false);
        return AllowsRuntime(resolution, operation);
    }

    /// <summary>
    /// The CIRCUIT identity path: initialises the scoped accessor at most ONCE from the optional
    /// cascading state through the shared helper, then re-resolves the current principal and requires
    /// <see cref="HiveCapability.InteractiveMember"/>. With no cascade (open mode) the accessor stays
    /// un-initialised and answers from the explicit open-mode decision inside the resolver.
    /// </summary>
    private async Task<ActingUserPrincipal?> AuthorizeCircuitAsync(
        Task<AuthenticationState>? authenticationState, string operation, CancellationToken ct)
    {
        // Initialisation only: the helper stores the claims IDENTITY once per circuit and returns it,
        // but nothing is authorised from that value — the resolution below re-reads stored state.
        _ = await ComponentActingUser.AcquireAsync(authenticationState, _circuitAccessor).ConfigureAwait(false);

        var resolution = await _circuitAccessor.GetPrincipalAsync(ct).ConfigureAwait(false);
        return AllowsRuntime(resolution, operation);
    }

    /// <summary>
    /// THE ONE capability rule: a resolved principal that currently holds
    /// <see cref="HiveCapability.InteractiveMember"/> may reach a runtime; a refusal and a principal
    /// without the capability both answer <c>null</c> (no runtime, no content). This is the same
    /// fresh-resolution-then-<see cref="HivePolicy"/> rule the facades apply through
    /// <c>FacadeAuthorization</c>; it is evaluated here on the single resolution that is then handed
    /// to the manager, so one operation performs exactly one identity read.
    /// </summary>
    private ActingUserPrincipal? AllowsRuntime(PrincipalResolution resolution, string operation)
    {
        if (resolution.Principal is not { } principal)
        {
            LogDenial(operation, $"the principal resolution refused ({resolution.RefusalReason})");
            return null;
        }

        if (!_policy.IsAllowed(principal, HiveCapability.InteractiveMember))
        {
            LogDenial(operation, $"the resolved role {principal.Role} does not hold it");
            return null;
        }

        return principal;
    }

    /// <summary>
    /// Logs one secret-free denial (operation and the account's OWN current role or the refusal
    /// reason — never a credential, claim dump or another account's data). Diagnostics never throw and
    /// never change the authorization outcome.
    /// </summary>
    private void LogDenial(string operation, string detail)
    {
        try
        {
            _log.LogWarning(
                "{Operation} was denied: the acting account does not hold {Capability} ({Detail}).",
                operation,
                HiveCapability.InteractiveMember,
                detail);
        }
        catch
        {
            // Diagnostics never change the authorization outcome.
        }
    }
}
