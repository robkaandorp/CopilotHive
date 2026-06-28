namespace CopilotHive.Persistence.Entities;

/// <summary>
/// EF Core entity mapping for the users table.
/// CopilotHive uses a single-user (admin) model: the first GitHub user to authenticate
/// becomes the admin, and the stored OAuth access token is used by the AI clients.
/// </summary>
public sealed class UserEntity
{
    /// <summary>Primary key — auto-incrementing identifier.</summary>
    public int Id { get; set; }

    /// <summary>GitHub numeric user ID (unique across all users).</summary>
    public string GitHubId { get; set; } = string.Empty;

    /// <summary>GitHub login / username.</summary>
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

    /// <summary>Role of the user. Currently always "admin".</summary>
    public string Role { get; set; } = "admin";

    /// <summary>UTC creation timestamp (ISO 8601 string).</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the last login (ISO 8601 string), or null.</summary>
    public string? LastLoginAt { get; set; }
}
