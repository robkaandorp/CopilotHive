using CopilotHive.Orchestration;
using CopilotHive.Security;

using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Services;

/// <summary>
/// THE DEPLOYMENT GATE for the per-user Composer runtime on the live facade path.
/// </summary>
/// <remarks>
/// <para>
/// This slice ships the recovery layer but does NOT switch live selection: with the gate OFF (the
/// DEFAULT) every <see cref="IComposerFacade"/> operation runs on the LEGACY singleton
/// <see cref="Composer"/>, exactly as before, so no split-runtime behaviour is observable. Turning the
/// gate ON is a deliberate deployment decision (a later, separately reviewed slice that also converts
/// the ComposerChat circuit); it is read from the
/// <see cref="EnvironmentVariable"/> environment variable and nothing else.
/// </para>
/// <para>
/// The default is OFF in BOTH directions: an unset, empty, mistyped or differently-cased value keeps
/// the legacy singleton. Only the exact value <c>true</c> (case-insensitive) enables the per-user
/// path, so a stray environment entry can never move traffic onto runtimes by accident.
/// </para>
/// </remarks>
/// <param name="PerUserRuntimeEnabled">
/// Whether the live facade path selects a per-user runtime from <see cref="ComposerRuntimeManager"/>.
/// </param>
public sealed record ComposerRuntimeFacadeDeployment(bool PerUserRuntimeEnabled)
{
    /// <summary>The single environment variable that turns the per-user live path on.</summary>
    public const string EnvironmentVariable = "COMPOSER_PER_USER_RUNTIME";

    /// <summary>The DEFAULT deployment: the legacy singleton Composer serves every operation.</summary>
    public static ComposerRuntimeFacadeDeployment LegacyOnly { get; } = new(PerUserRuntimeEnabled: false);

    /// <summary>Reads the deployment decision from <see cref="EnvironmentVariable"/>, defaulting to legacy.</summary>
    /// <returns>The deployment decision; <see cref="LegacyOnly"/> unless the variable is exactly <c>true</c>.</returns>
    public static ComposerRuntimeFacadeDeployment FromEnvironment() =>
        new(string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase));
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
/// </list>
/// </para>
/// <para>
/// SELECTION IS THE GATE, AND ONLY THE GATE: with <see cref="ComposerRuntimeFacadeDeployment.PerUserRuntimeEnabled"/>
/// OFF the scope resolves the LEGACY singleton facade (byte-identical behaviour); with it ON it
/// resolves <see cref="ComposerRuntimeFacade"/>. Nothing switches on identity here — the open-mode
/// legacy fallback for the synthetic local operator lives inside the recovery facade, where it is
/// explicit and cannot rescue an authenticated refusal.
/// </para>
/// </remarks>
public static class ComposerFacadeRegistration
{
    /// <summary>Registers the legacy facade, the scoped accessors and the gate-selected facades.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="deployment">The EXPLICIT deployment decision (defaults to <see cref="ComposerRuntimeFacadeDeployment.LegacyOnly"/> via the host).</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static IServiceCollection AddComposerRuntimeFacades(
        this IServiceCollection services, ComposerRuntimeFacadeDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(deployment);

        // The decision is a SINGLETON input: the scoped facade factory reads it per scope instead of
        // closing over a captured scope.
        services.AddSingleton(deployment);

        // The LEGACY singleton facade: stateless collaborators plus the legacy Composer. It never sees
        // the scoped circuit accessor, so it can never leak one circuit's identity.
        services.AddSingleton(sp => new ComposerFacade(
            sp.GetService<Composer>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            sp.GetRequiredService<ILogger<ComposerFacade>>()));

        // SCOPED runtime accessor: the singleton manager plus the scoped circuit accessor. It is
        // resolved only from a scope, so no singleton and no startup closure can capture it.
        services.AddScoped(sp => new ComposerRuntimeAccessor(
            sp.GetRequiredService<ComposerRuntimeManager>(),
            sp.GetRequiredService<CircuitUserPrincipalAccessor>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            sp.GetRequiredService<ILogger<ComposerRuntimeAccessor>>()));

        // SCOPED facade, resolved PER REQUEST: the deployment gate chooses the legacy singleton (the
        // default in this slice) or the recovery facade over that request's own accessor.
        services.AddScoped<IComposerFacade>(sp =>
            sp.GetRequiredService<ComposerRuntimeFacadeDeployment>().PerUserRuntimeEnabled
                ? new ComposerRuntimeFacade(
                    sp.GetRequiredService<ComposerRuntimeAccessor>(),
                    sp.GetRequiredService<ComposerFacade>(),
                    sp.GetRequiredService<ILogger<ComposerRuntimeFacade>>())
                : sp.GetRequiredService<ComposerFacade>());

        return services;
    }
}
