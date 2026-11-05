namespace CopilotHive.Security;

/// <summary>
/// The EXPLICIT authentication-mode input every <see cref="HivePrincipalResolver"/> and
/// <see cref="CircuitUserPrincipalAccessor"/> decision is made from.
/// <para>
/// <see cref="Open"/> is the no-auth (local single-operator) mode: it yields the full
/// <see cref="ActingUserPrincipal.LocalOperator"/> principal for backward compatibility. That mode is
/// only ever SUPPLIED by the host — it is never inferred from a missing/failed identity inside
/// auth-enabled mode, so an anonymous or refused caller can never fall through to local-operator
/// authority.
/// </para>
/// <para>
/// The predicate mirrors <c>Program.cs</c> exactly (authentication is enabled only when BOTH
/// <c>GITHUB_OAUTH_CLIENT_ID</c> and <c>GITHUB_OAUTH_CLIENT_SECRET</c> are set), so the DI input and
/// the OAuth registration can never disagree about the mode.
/// </para>
/// </summary>
public sealed class HiveAuthMode
{
    /// <summary>The environment variable carrying the GitHub OAuth client id.</summary>
    public const string ClientIdEnvironmentVariable = "GITHUB_OAUTH_CLIENT_ID";

    /// <summary>The environment variable carrying the GitHub OAuth client secret.</summary>
    public const string ClientSecretEnvironmentVariable = "GITHUB_OAUTH_CLIENT_SECRET";

    private HiveAuthMode(bool authEnabled) => AuthEnabled = authEnabled;

    /// <summary>
    /// Authentication is ENABLED: every resolution must come from an authenticated GitHub identity
    /// backed by an ACTIVATED account. There is no fallback principal in this mode.
    /// </summary>
    public static HiveAuthMode AuthenticationEnabled { get; } = new(authEnabled: true);

    /// <summary>
    /// Authentication is DISABLED (open mode): the single local operator holds full administrative
    /// authority, exactly as the legacy single-user installation did.
    /// </summary>
    public static HiveAuthMode Open { get; } = new(authEnabled: false);

    /// <summary>Whether authentication is enabled for this installation.</summary>
    public bool AuthEnabled { get; }

    /// <summary>
    /// Computes the mode from the process environment, mirroring the <c>Program.cs</c> predicate
    /// exactly (<c>!string.IsNullOrEmpty</c> on both variables — a whitespace-only value counts as
    /// set, because that is what the existing OAuth registration already does).
    /// </summary>
    /// <returns>The computed mode.</returns>
    public static HiveAuthMode FromEnvironment() =>
        FromValues(
            Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable),
            Environment.GetEnvironmentVariable(ClientSecretEnvironmentVariable));

    /// <summary>
    /// Computes the mode from supplied OAuth values with the SAME predicate <c>Program.cs</c> uses,
    /// so a host that already read the environment can pass its values through without duplicating
    /// the rule.
    /// </summary>
    /// <param name="oauthClientId">The GitHub OAuth client id, or null/empty when unset.</param>
    /// <param name="oauthClientSecret">The GitHub OAuth client secret, or null/empty when unset.</param>
    /// <returns>Authentication-enabled mode when both values are set; otherwise open mode.</returns>
    public static HiveAuthMode FromValues(string? oauthClientId, string? oauthClientSecret) =>
        !string.IsNullOrEmpty(oauthClientId) && !string.IsNullOrEmpty(oauthClientSecret)
            ? AuthenticationEnabled
            : Open;
}
