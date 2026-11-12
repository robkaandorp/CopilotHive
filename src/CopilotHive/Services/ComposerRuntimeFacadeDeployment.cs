using CopilotHive.Orchestration;
using CopilotHive.Security;

using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Services;

/// <summary>
/// THE DEPLOYMENT DECISION for the per-user Composer path on the live facade UI circuit: the
/// EXPLICIT HOST AUTHENTICATION MODE alone decides it.
/// </summary>
/// <remarks>
/// <para>
/// THE FINAL CONTRACT — THERE IS NO OFF PATH WHILE AUTHENTICATION IS ENABLED:
/// <list type="bullet">
/// <item><b>Authenticated</b> — the integrated per-user path is ALWAYS selected: the recovery facade
/// (<see cref="ComposerRuntimeFacade"/>) serves <see cref="IComposerFacade"/> and the ComposerChat
/// circuit binds the caller's OWN <see cref="ComposerUserRuntime"/>. There is no configuration value
/// that can move traffic back onto the legacy singleton in this mode.</item>
/// <item><b>Open mode</b> — behaviour is the legacy single-operator installation, byte-identical to
/// before: the legacy singleton <see cref="ComposerFacade"/> serves <see cref="IComposerFacade"/> and
/// the circuit binds the legacy singleton <see cref="Composer"/>.</item>
/// </list>
/// </para>
/// <para>
/// THE HISTORICAL GATE VARIABLE IS RETIRED: <see cref="EnvironmentVariable"/> is kept only so the
/// retired name stays discoverable (and so tests can prove it no longer decides anything). Its value
/// — unset, <c>"true"</c>, <c>"false"</c> or anything else — is IGNORED: it can no longer create a
/// split runtime, and in particular it can no longer keep the legacy singleton live while
/// authentication is enabled.
/// </para>
/// <para>
/// THE DECISION IS DERIVED FROM THE SAME EXPLICIT MODE THE RESOLVER USES
/// (<see cref="HiveAuthMode"/>): <see cref="ForAuthMode"/> is the one conversion, and
/// <see cref="FromEnvironment"/> resolves the host mode from the OAuth environment predicate
/// <c>Program.cs</c> uses, so a host that has not yet computed its mode cannot disagree with the
/// host that has.
/// </para>
/// </remarks>
/// <param name="PerUserRuntimeEnabled">
/// The DECLARED preference for the per-user runtime path. It is only ever an INPUT: the effective
/// decision is always <see cref="ForAuthMode"/> of the registered <see cref="HiveAuthMode"/>, so a
/// declaration that asks for the legacy singleton while authentication is enabled is a NO-OP rather
/// than an off switch.
/// </param>
public sealed record ComposerRuntimeFacadeDeployment(bool PerUserRuntimeEnabled)
{
    /// <summary>
    /// The historical gate variable, now RETIRED. It is never read as a decision input; the name is
    /// kept so an operator (and the tests) can point at the variable this slice retired.
    /// </summary>
    public const string EnvironmentVariable = "COMPOSER_PER_USER_RUNTIME";

    /// <summary>The OPEN-MODE deployment: the legacy singleton Composer serves every operation.</summary>
    public static ComposerRuntimeFacadeDeployment LegacyOnly { get; } = new(PerUserRuntimeEnabled: false);

    /// <summary>The AUTHENTICATED deployment: every caller uses their OWN per-user runtime.</summary>
    public static ComposerRuntimeFacadeDeployment PerUserRuntime { get; } = new(PerUserRuntimeEnabled: true);

    /// <summary>
    /// THE ONE CONVERSION from the explicit host authentication mode to the deployment decision: an
    /// authenticated installation is ALWAYS the integrated per-user path, an open installation is
    /// ALWAYS the legacy singleton.
    /// </summary>
    /// <param name="mode">The EXPLICIT host authentication mode (the same instance the resolver holds).</param>
    /// <returns>The deployment decision for <paramref name="mode"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mode"/> is null.</exception>
    public static ComposerRuntimeFacadeDeployment ForAuthMode(HiveAuthMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);

        return mode.AuthEnabled ? PerUserRuntime : LegacyOnly;
    }

    /// <summary>
    /// Reads the host authentication mode from the SAME environment predicate <c>Program.cs</c> and
    /// the resolver use, then returns its deployment decision.
    /// <para>
    /// <see cref="EnvironmentVariable"/> is deliberately NOT consulted: a retired variable can no
    /// longer enable — or, more importantly, disable — the per-user path.
    /// </para>
    /// </summary>
    /// <returns>The deployment decision of the mode the current environment describes.</returns>
    public static ComposerRuntimeFacadeDeployment FromEnvironment() => ForAuthMode(HiveAuthMode.FromEnvironment());
}

/// <summary>
/// THE ONE REGISTRATION SEAM for the Composer facades, so production wiring and its tests cannot drift.
/// </summary>
/// <remarks>
/// <para>
/// LIFETIMES, deliberately different per dependency:
/// <list type="bullet">
/// <item><see cref="Composer"/> and <see cref="ComposerRuntimeManager"/> are SINGLETONS (the manager is
/// the sole owner and disposer of every per-user runtime);</item>
/// <item><see cref="ComposerFacade"/> is the SINGLETON legacy facade over that legacy Composer;</item>
/// <item><see cref="CircuitUserPrincipalAccessor"/> and <see cref="ComposerRuntimeAccessor"/> are
/// SCOPED (one per circuit/request scope), and <see cref="IComposerFacade"/> is SCOPED too, so the
/// facade a request uses may hold the scoped accessor WITHOUT any singleton capturing it. There is
/// deliberately no startup closure over either scoped type.</item>
/// <item><see cref="IComposerRuntimeAccessor"/> is an ALIAS of that same scoped
/// <see cref="ComposerRuntimeAccessor"/> INSTANCE (one registration, one factory, one lifetime), so a
/// component that injects the interface and a facade built from the concrete type share the very same
/// per-circuit identity rather than two divergent accessors.</item>
/// </list>
/// </para>
/// <para>
/// SELECTION IS THE AUTHENTICATION MODE, AND ONLY THAT: the effective decision is
/// <see cref="ComposerRuntimeFacadeDeployment.ForAuthMode"/> of the registered
/// <see cref="HiveAuthMode"/>, which is the same singleton the resolver is built with. A declared
/// deployment that asks for the legacy singleton while authentication is enabled is a NO-OP (it is
/// logged, never honoured), so no split-runtime path can be declared — or reconfigured — into
/// existence, and there is no OFF switch left.
/// </para>
/// </remarks>
public static class ComposerFacadeRegistration
{
    /// <summary>
    /// Registers the legacy facade, the scoped accessor (and its interface alias) and the
    /// auth-mode-selected facade.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="deployment">
    /// The host's DECLARED deployment decision (in production: <see cref="ComposerRuntimeFacadeDeployment.ForAuthMode"/>).
    /// It can only ASK for the per-user path: while the registered <see cref="HiveAuthMode"/> enables
    /// authentication a declared legacy request is ignored (it is logged and the per-user path is used).
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static IServiceCollection AddComposerRuntimeFacades(
        this IServiceCollection services, ComposerRuntimeFacadeDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(deployment);

        // The LEGACY singleton facade: stateless collaborators plus the legacy Composer. It never sees
        // the scoped circuit accessor, so it can never leak one circuit's identity.
        services.AddSingleton(sp => new ComposerFacade(
            sp.GetService<Composer>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            sp.GetRequiredService<ILogger<ComposerFacade>>()));

        // THE EFFECTIVE DECISION: the registered authentication mode is the single source of truth, so
        // the facade selection cannot drift away from what the resolver (and therefore every
        // authorization answer) really is. The declared value can only ASK for the per-user path; while
        // authentication is enabled it can never move traffic back onto the legacy singleton, which is
        // why a declared "false" there is a NO-OP (logged, and otherwise ignored) and not an off path.
        services.AddSingleton(sp =>
        {
            var mode = sp.GetRequiredService<HiveAuthMode>();
            var effective = ComposerRuntimeFacadeDeployment.ForAuthMode(mode);

            if (effective.PerUserRuntimeEnabled && !deployment.PerUserRuntimeEnabled)
            {
                // The retired gate asked for the legacy singleton in authenticated mode: the request is
                // ignored (never honoured, never thrown) and recorded so an operator can retire the setting.
                try
                {
                    sp.GetService<ILogger<ComposerRuntimeFacade>>()?.LogWarning(
                        "The declared Composer facade deployment selects the legacy singleton while authentication is "
                        + "enabled; the integrated per-user path is used instead. '{Variable}' is retired and no longer "
                        + "selects the Composer runtime.",
                        ComposerRuntimeFacadeDeployment.EnvironmentVariable);
                }
                catch
                {
                    // Diagnostics never change the deployment decision.
                }
            }

            return effective;
        });

        // SCOPED runtime accessor: the singleton manager plus the scoped circuit accessor. It is
        // resolved only from a scope, so no singleton and no startup closure can capture it.
        services.AddScoped(sp => new ComposerRuntimeAccessor(
            sp.GetRequiredService<ComposerRuntimeManager>(),
            sp.GetRequiredService<CircuitUserPrincipalAccessor>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            sp.GetRequiredService<ILogger<ComposerRuntimeAccessor>>()));

        // THE INTERFACE IS AN ALIAS OF THAT SAME SCOPED INSTANCE: the component that injects
        // IComposerRuntimeAccessor and any facade built from the concrete type resolve ONE accessor per
        // scope — never a second, separately-lived one.
        services.AddScoped<IComposerRuntimeAccessor>(sp => sp.GetRequiredService<ComposerRuntimeAccessor>());

        // SCOPED facade, resolved PER REQUEST: the authentication-mode-derived decision chooses the
        // legacy singleton (open mode) or the recovery facade over that request's own accessor.
        services.AddScoped<IComposerFacade>(sp =>
            sp.GetRequiredService<ComposerRuntimeFacadeDeployment>().PerUserRuntimeEnabled
                ? new ComposerRuntimeFacade(
                    sp.GetRequiredService<IComposerRuntimeAccessor>(),
                    sp.GetRequiredService<ComposerFacade>(),
                    sp.GetRequiredService<ILogger<ComposerRuntimeFacade>>())
                : sp.GetRequiredService<ComposerFacade>());

        return services;
    }
}
