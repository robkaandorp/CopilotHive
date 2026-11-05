using CopilotHive.Services;

namespace CopilotHive.Security;

/// <summary>
/// The IMMUTABLE, SECRET-FREE description of the user ACTING on one operation: the stable id of the
/// activated account, its GitHub identity, its validated role and enabled state, plus whether that
/// account is the installation's ORIGINAL (bootstrap) account.
/// <para>
/// It carries NO credential material — not the OAuth access token, not the refresh token, not the
/// expiry — so a principal can be passed around (per operation, never captured by a singleton) and
/// logged without leaking secrets. It is built by
/// <see cref="HivePrincipalResolver.ResolveAsync"/> from CURRENT database state on every resolution
/// and never from cookie claims.
/// </para>
/// <para>
/// This is the ACTING user only. It is deliberately not a "goal owner" and carries no ownership,
/// attribution or sharing semantics; those are separate, unimplemented slices.
/// </para>
/// </summary>
public sealed record ActingUserPrincipal
{
    /// <summary>
    /// The <see cref="UserId"/> carried by the open/no-auth LOCAL OPERATOR
    /// (<see cref="LocalOperator"/>): no persisted account backs it, so it is not an activated
    /// account id. Any consumer that needs an account id must check <see cref="IsLocalOperator"/>
    /// first.
    /// </summary>
    public const int LocalOperatorUserId = 0;

    private ActingUserPrincipal(
        int userId,
        string gitHubId,
        UserRole role,
        bool enabled,
        bool isOriginal,
        bool isLocalOperator)
    {
        UserId = userId;
        GitHubId = gitHubId;
        Role = role;
        Enabled = enabled;
        IsOriginal = isOriginal;
        IsLocalOperator = isLocalOperator;
    }

    /// <summary>
    /// The stable internal id of the ACTIVATED account this principal acts as, or
    /// <see cref="LocalOperatorUserId"/> for the open/no-auth local operator (see
    /// <see cref="IsLocalOperator"/>).
    /// </summary>
    public int UserId { get; }

    /// <summary>
    /// The GitHub numeric user id of the account as currently PERSISTED (never a claim value), or
    /// the empty string for the open/no-auth local operator, which has no GitHub identity.
    /// </summary>
    public string GitHubId { get; }

    /// <summary>
    /// The account's CURRENT persisted role, validated against <see cref="UserRole"/> (an
    /// unrecognised persisted value is refused by the resolver, never mapped to a fallback).
    /// </summary>
    public UserRole Role { get; }

    /// <summary>
    /// Whether the account was enabled when this principal was resolved. A DISABLED account never
    /// becomes a principal — the resolver refuses it — so this is always <c>true</c> for a principal
    /// produced by <see cref="HivePrincipalResolver"/>; the policy evaluator still requires it, so a
    /// hand-built principal for a disabled account can never be granted a capability.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// Whether this account IS the installation's original/bootstrap account, derived by comparing
    /// the resolved id to the original account's id (the lowest-id activated row, read through
    /// <see cref="UserService.GetAdminUserAsync"/>).
    /// <para>
    /// This flag confers NO authority of its own: original identity never overrides a disabled or
    /// demoted account, because the role and enabled state above always come from current stored
    /// state. It exists so later UI/diagnostics can label the bootstrap account.
    /// </para>
    /// </summary>
    public bool IsOriginal { get; }

    /// <summary>
    /// Whether this is the open/no-auth LOCAL OPERATOR rather than a persisted account: the explicit
    /// full-Administrator identity produced only when authentication is disabled. It is never
    /// produced while authentication is enabled, and it carries no account id, no GitHub identity and
    /// no notion of being an invited or original account.
    /// </summary>
    public bool IsLocalOperator { get; }

    /// <summary>
    /// Builds the principal of one persisted ACTIVATED account.
    /// </summary>
    /// <param name="userId">The stable internal account id (must be positive).</param>
    /// <param name="gitHubId">The persisted GitHub numeric user id (must be non-blank).</param>
    /// <param name="role">The account's current, validated role.</param>
    /// <param name="enabled">Whether the account was enabled when it was read.</param>
    /// <param name="isOriginal">Whether the account is the installation's original/bootstrap account.</param>
    /// <returns>The immutable acting principal.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="userId"/> is not positive, or <paramref name="role"/> is not a defined role.</exception>
    /// <exception cref="ArgumentException"><paramref name="gitHubId"/> is null, empty or whitespace.</exception>
    public static ActingUserPrincipal ForActivatedAccount(
        int userId,
        string gitHubId,
        UserRole role,
        bool enabled,
        bool isOriginal)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(userId), userId, "An activated account id must be positive.");
        }

        if (string.IsNullOrWhiteSpace(gitHubId))
        {
            throw new ArgumentException(
                "An activated account must carry a GitHub identity.", nameof(gitHubId));
        }

        UserRoleText.EnsureDefined(role, nameof(role));

        return new ActingUserPrincipal(userId, gitHubId, role, enabled, isOriginal, isLocalOperator: false);
    }

    /// <summary>
    /// Builds the open/no-auth LOCAL OPERATOR principal: a full <see cref="UserRole.Admin"/> that is
    /// enabled, original and NOT backed by any persisted account.
    /// <para>
    /// Callers must pass the explicit authentication mode to the resolver rather than calling this
    /// directly; it exists (and is public) only so the resolver's open-mode branch and its tests
    /// share one definition. Producing it while authentication is ENABLED would be an open-mode
    /// escalation, which is why the resolver makes that decision from its supplied
    /// <see cref="HiveAuthMode"/> alone.
    /// </para>
    /// </summary>
    /// <returns>The immutable local-operator principal.</returns>
    public static ActingUserPrincipal LocalOperator() =>
        new(
            LocalOperatorUserId,
            string.Empty,
            UserRole.Admin,
            enabled: true,
            // The local operator IS the legacy single-admin installation identity: there is no
            // directory account to compare against, and it carries full administrative authority.
            isOriginal: true,
            isLocalOperator: true);
}
