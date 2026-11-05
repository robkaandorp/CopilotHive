using CopilotHive.Persistence.Entities;

namespace CopilotHive.Services;

/// <summary>
/// The SECRET-FREE projection of one ACTIVATED user account, returned by the
/// <see cref="UserService"/> directory listings and resolvers.
/// <para>
/// It deliberately carries no OAuth material — not the access token, not the refresh token, not the
/// token expiry. A directory read is an administrative view of WHO exists, never of the credentials
/// that back them, so the tokens cannot leak through a listing by construction.
/// </para>
/// </summary>
/// <param name="Id">The stable internal account id (the original/bootstrap account is the lowest).</param>
/// <param name="GitHubId">The GitHub numeric user id.</param>
/// <param name="Username">The GitHub login as stored at sign-in/activation.</param>
/// <param name="DisplayName">Optional GitHub display name.</param>
/// <param name="AvatarUrl">Optional GitHub avatar URL.</param>
/// <param name="Email">Optional GitHub email.</param>
/// <param name="Role">The validated account role.</param>
/// <param name="Enabled">Whether the account is enabled (disabling retains credentials and history).</param>
/// <param name="CreatedAt">UTC creation timestamp (ISO 8601 string).</param>
/// <param name="LastLoginAt">UTC last-login timestamp (ISO 8601 string), or null.</param>
public sealed record ActivatedUserSummary(
    int Id,
    string GitHubId,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    string? Email,
    UserRole Role,
    bool Enabled,
    string CreatedAt,
    string? LastLoginAt)
{
    /// <summary>Projects a stored <see cref="UserEntity"/> onto its secret-free summary.</summary>
    internal static ActivatedUserSummary FromEntity(UserEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new ActivatedUserSummary(
            entity.Id,
            entity.GitHubId,
            entity.Username,
            entity.DisplayName,
            entity.AvatarUrl,
            entity.Email,
            entity.Role,
            entity.Enabled,
            entity.CreatedAt,
            entity.LastLoginAt);
    }
}

/// <summary>
/// The SECRET-FREE projection of one administrator invitation. Invitations never hold OAuth
/// material at all (they live in their own table), so this projection is a straight read — the type
/// simply makes the "no credentials here" contract explicit for every directory caller.
/// </summary>
/// <param name="Id">The stable internal invitation id.</param>
/// <param name="NormalizedUsername">The normalized GitHub login this invitation is for.</param>
/// <param name="Role">The role the activated account will carry.</param>
/// <param name="Enabled">Whether the invitation is enabled; a revoked invitation is disabled.</param>
/// <param name="CreatedAt">UTC creation timestamp (ISO 8601 string).</param>
/// <param name="UpdatedAt">UTC timestamp of the last state change (ISO 8601 string).</param>
/// <param name="RevokedAt">UTC revocation timestamp (ISO 8601 string), or null.</param>
/// <param name="ActivatedUserId">The activated account id this invitation was consumed into, or null while pending.</param>
/// <param name="ActivatedAt">UTC activation timestamp (ISO 8601 string), or null while pending.</param>
public sealed record UserInvitationSummary(
    int Id,
    string NormalizedUsername,
    UserRole Role,
    bool Enabled,
    string CreatedAt,
    string UpdatedAt,
    string? RevokedAt,
    int? ActivatedUserId,
    string? ActivatedAt)
{
    /// <summary>
    /// Whether the invitation is still PENDING: enabled and not yet consumed into an account.
    /// A pending Admin invitation is never an activated Admin.
    /// </summary>
    public bool IsPending => Enabled && ActivatedUserId is null;

    /// <summary>Projects a stored <see cref="UserInvitationEntity"/> onto its summary.</summary>
    internal static UserInvitationSummary FromEntity(UserInvitationEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new UserInvitationSummary(
            entity.Id,
            entity.NormalizedUsername,
            entity.Role,
            entity.Enabled,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.RevokedAt,
            entity.ActivatedUserId,
            entity.ActivatedAt);
    }
}

/// <summary>
/// The ONE GitHub-username normalization used by every <see cref="UserService"/> directory
/// operation, so an invitation and an activated account can never disagree about identity because
/// of casing or surrounding whitespace.
/// <para>
/// Normalization is a pure, culture-invariant case fold plus a trim: GitHub logins are
/// case-insensitive, so <c>"OctoCat"</c> and <c>" octocat "</c> are the same identity and must map
/// onto one invitation row (enforced by the unique index on the normalized column).
/// </para>
/// </summary>
internal static class UserNormalization
{
    /// <summary>
    /// Normalizes a GitHub login, or returns <c>null</c> when it is null/blank. Used by the OAuth
    /// bootstrap seam, which must never reject a sign-in for a missing login claim.
    /// </summary>
    internal static string? TryNormalize(string? username) =>
        string.IsNullOrWhiteSpace(username) ? null : username.Trim().ToLowerInvariant();

    /// <summary>
    /// Normalizes a caller-supplied GitHub login, rejecting a blank value or one containing
    /// whitespace (neither can be a GitHub login).
    /// </summary>
    /// <param name="username">The caller-supplied login.</param>
    /// <param name="parameterName">The caller's parameter name, used for the exception.</param>
    /// <exception cref="ArgumentException">The login is blank or contains whitespace.</exception>
    internal static string Normalize(string? username, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("A GitHub username is required.", parameterName);
        }

        var normalized = username.Trim().ToLowerInvariant();
        if (normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "A GitHub username must not contain whitespace.",
                parameterName);
        }

        return normalized;
    }

    /// <summary>
    /// Compares two logins by their normalized form. A blank side never matches, so an account
    /// stored without a login can never collide with an invitation.
    /// </summary>
    internal static bool NormalizedEquals(string? left, string? right) =>
        TryNormalize(left) is { } normalizedLeft
        && TryNormalize(right) is { } normalizedRight
        && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
}

/// <summary>
/// Raised when a trusted internal directory operation is REJECTED: a duplicate identity, an
/// unknown or ineligible target, an invitation that must not be silently consumed, or an operation
/// that would remove the last activated enabled Admin.
/// <para>
/// It derives from <see cref="InvalidOperationException"/> so existing callers that already treat
/// an impossible state as an invalid operation keep working, while the distinct type lets a caller
/// (and a test) tell a directory REFUSAL apart from an arbitrary failure. A refusal never partially
/// applies: the rejecting operation commits nothing.
/// </para>
/// </summary>
public sealed class UserDirectoryException : InvalidOperationException
{
    /// <summary>Creates a refusal with the given explanatory message.</summary>
    /// <param name="message">The refusal message shown to the operator.</param>
    public UserDirectoryException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a refusal with the given explanatory message and underlying cause.</summary>
    /// <param name="message">The refusal message shown to the operator.</param>
    /// <param name="innerException">The cause that made the operation impossible.</param>
    public UserDirectoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The outcome of the OAuth bootstrap seam (<see cref="UserService.CreateOrUpdateUserAsync"/>),
/// carrying the reported side effect on a MATCHING pending invitation.
/// </summary>
/// <param name="User">The committed account (the bootstrap account, or the refreshed original).</param>
/// <param name="CreatedNewAccount">Whether this call created the bootstrap account.</param>
/// <param name="ConsumedInvitation">The matching pending invitation atomically marked activated, or null.</param>
/// <param name="ConflictingInvitation">
/// The matching invitation that was NOT consumed because it is revoked or already activated, or
/// null. A conflict is reported, never applied silently.
/// </param>
/// <param name="UsernameConflict">
/// Set when the ORIGINAL account signed in under a new login whose normalized form already belongs to
/// another activated account or invitation. The sign-in still succeeded (it is anchored to the
/// immutable GitHub id): the original's existing username was preserved and every other field was
/// refreshed. Null when there was no collision.
/// </param>
internal sealed record UserBootstrapOutcome(
    UserEntity User,
    bool CreatedNewAccount,
    UserInvitationEntity? ConsumedInvitation,
    UserInvitationEntity? ConflictingInvitation,
    UsernameConflict? UsernameConflict = null);

/// <summary>
/// A reported (never thrown) collision between the login an original account tried to rename to and an
/// identity that already owns that normalized login.
/// </summary>
/// <param name="RequestedUsername">The (trimmed) login the sign-in carried and that was NOT applied.</param>
/// <param name="ConflictingUserId">The activated account holding that login, or null.</param>
/// <param name="ConflictingInvitationId">The invitation reserving that login, or null.</param>
internal sealed record UsernameConflict(
    string RequestedUsername,
    int? ConflictingUserId,
    int? ConflictingInvitationId);
