using CopilotHive.Services;

namespace CopilotHive.Persistence.Entities;

/// <summary>
/// EF Core entity mapping for the users table: one row per ACTIVATED OAuth account.
/// <para>
/// The ORIGINAL/BOOTSTRAP account is the activated row with the LOWEST <see cref="Id"/> — that
/// selector is independent of role ordering and of the enabled flag, and it stays the legacy OAuth
/// admission identity and the account whose stored token the AI clients use. Pending invitations
/// are stored in the separate <c>user_invitations</c> table and never participate in it.
/// </para>
/// <para>
/// Rows are only ever added through <c>UserService.CreateOrUpdateUserAsync</c> (the bootstrap
/// identity) or by activating an invitation, so the integer ids and stored credentials of
/// existing installations are preserved across the additive upgrade.
/// </para>
/// </summary>
public sealed class UserEntity
{
    /// <summary>Primary key — auto-incrementing identifier.</summary>
    public int Id { get; set; }

    /// <summary>GitHub numeric user ID (unique across all users).</summary>
    public string GitHubId { get; set; } = string.Empty;

    /// <summary>GitHub login / username, exactly as GitHub reported it at sign-in.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Optional display name from the GitHub profile.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Optional avatar URL from the GitHub profile.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>Optional email address from the GitHub profile.</summary>
    public string? Email { get; set; }

    /// <summary>The OAuth access token issued by GitHub.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Optional OAuth refresh token issued by GitHub.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>Optional ISO 8601 timestamp when the access token expires.</summary>
    public string? TokenExpiresAt { get; set; }

    /// <summary>
    /// The validated account role, persisted as lowercase text (see <see cref="UserRoleText"/>).
    /// The default is <see cref="UserRole.Admin"/> so a legacy row created by the previous
    /// single-admin model reads back as an enabled Admin, and the column default stays the legacy
    /// <c>DEFAULT 'admin'</c>.
    /// </summary>
    public UserRole Role { get; set; } = UserRole.Admin;

    /// <summary>
    /// Whether the account is enabled. Added ADDITIVELY with a persisted default of enabled, so
    /// every pre-existing row upgrades to an enabled Admin. Disabling an account never deletes the
    /// row and never clears its credentials.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>UTC creation timestamp (ISO 8601 string).</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the last login (ISO 8601 string), or null.</summary>
    public string? LastLoginAt { get; set; }
}
