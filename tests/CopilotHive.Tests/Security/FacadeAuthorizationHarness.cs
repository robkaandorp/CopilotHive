using System.Security.Claims;

using CopilotHive.Orchestration;
using CopilotHive.Persistence.Entities;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Security;

/// <summary>
/// THE ONE HARNESS for the facade/request authorization contract: a REAL user directory (SQLite
/// in-memory over the production <see cref="UserService"/>) plus the production resolver/policy, and
/// builders that wire the real facades to them for a chosen EXPLICIT
/// <see cref="HiveAuthMode"/>.
/// <para>
/// Using the production directory and the production resolver (rather than a mocked identity source)
/// is what makes a "stale Member demoted to Viewer between two calls" test meaningful: the second
/// facade call re-reads the SAME rows and must observe the new role.
/// </para>
/// </summary>
internal sealed class FacadeAuthorizationHarness : IDisposable
{
    private readonly HiveSecurityTestDirectory _directory = new();

    /// <summary>The real user directory backing every resolution in this harness.</summary>
    internal HiveSecurityTestDirectory Directory => _directory;

    /// <summary>The pure policy evaluator (the production singleton type).</summary>
    internal HivePolicy Policy { get; } = new();

    /// <summary>A production resolver over this directory for the EXPLICIT <paramref name="mode"/>.</summary>
    internal HivePrincipalResolver Resolver(HiveAuthMode mode) => _directory.Resolver(mode);

    /// <summary>Seeds one activated account and returns its persisted entity.</summary>
    internal Task<UserEntity> SeedAsync(string gitHubId, UserRole role, bool enabled = true) =>
        _directory.SeedActivatedAccountAsync(gitHubId, role, enabled);

    /// <summary>Changes the persisted role, as an administrator would.</summary>
    internal Task SetRoleAsync(string gitHubId, UserRole role) => _directory.SetRoleAsync(gitHubId, role);

    /// <summary>Enables or disables the account, as an administrator would.</summary>
    internal Task SetEnabledAsync(string gitHubId, bool enabled) => _directory.SetEnabledAsync(gitHubId, enabled);

    /// <summary>The authenticated GitHub principal a cookie would produce for one identity.</summary>
    internal static ClaimsPrincipal Principal(string gitHubId) => HiveSecurityTestDirectory.GitHubPrincipal(gitHubId);

    /// <summary>A signed-out principal, as an anonymous request carries.</summary>
    internal static ClaimsPrincipal Anonymous() => HiveSecurityTestDirectory.AnonymousPrincipal();

    /// <summary>The production Composer facade over this directory's resolver for <paramref name="mode"/>.</summary>
    internal ComposerFacade ComposerFacade(Composer? composer, HiveAuthMode mode) =>
        new(composer, Resolver(mode), Policy, NullLogger<ComposerFacade>.Instance);

    /// <summary>The production config facade over this directory's resolver for <paramref name="mode"/>.</summary>
    internal ConfigFacade ConfigFacade(
        CopilotHive.Configuration.HiveConfigFile? config,
        ConfigModelService? service,
        HiveAuthMode mode) =>
        new(config, service, discovery: null, NullLogger<ConfigFacade>.Instance, Resolver(mode), Policy);

    public void Dispose() => _directory.Dispose();
}

/// <summary>
/// THE OPEN-MODE IDENTITY every pre-existing facade test hands to an identity-aware facade method.
/// <para>
/// Open mode registers NO authentication services, so no cascading <c>AuthenticationState</c> exists
/// and a component genuinely has no principal to forward — it passes <c>null</c>. That is NOT a
/// fail-open sentinel and NOT an identity-free overload: <see cref="HivePrincipalResolver"/> treats
/// <c>null</c> as "signed out" and, in the EXPLICIT open mode only, answers the legacy local operator;
/// while authentication is enabled the same <c>null</c> is refused. Using this named constant keeps
/// the reason visible at every migrated call site instead of an unexplained literal.
/// </para>
/// </summary>
internal static class OpenModeActingUser
{
    /// <summary>The open-mode identity: no cascade exists, so the caller forwards no principal.</summary>
    internal static System.Security.Claims.ClaimsPrincipal? User => null;
}

/// <summary>
/// The OPEN-MODE (no authentication configured) resolver/policy pair used by every pre-existing
/// facade test. In open mode the resolver consults NO directory at all, so this is exactly the
/// legacy single-local-operator behaviour; the shared instance keeps those tests unchanged.
/// </summary>
internal static class OpenModeFacadeAuth
{
    private static readonly FacadeAuthorizationHarness Harness = new();

    /// <summary>The production resolver in OPEN mode (never reads the directory).</summary>
    internal static HivePrincipalResolver Resolver { get; } = Harness.Resolver(HiveAuthMode.Open);

    /// <summary>The production policy evaluator.</summary>
    internal static HivePolicy Policy { get; } = Harness.Policy;
}
