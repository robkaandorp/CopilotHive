using CopilotHive.Services;

namespace CopilotHive.Persistence.Entities;

/// <summary>
/// EF Core entity mapping for the <c>user_invitations</c> table: the administrator's PENDING
/// invitation for one GitHub login, kept deliberately SEPARATE from the activated
/// <see cref="UserEntity"/> rows.
/// <para>
/// The separation is a storage invariant, not a preference. <c>users.github_id</c> is
/// <c>NOT NULL</c> and uniquely indexed, and <c>DatabaseMigration.ReconcileColumns</c> only ADDs
/// columns, so a pending invitation can never be represented as a users row: it has no GitHub
/// numeric id and no OAuth token yet. An invitation therefore lives here, keyed by the
/// NORMALIZED GitHub username, and carries NO OAuth tokens of any kind.
/// </para>
/// <para>
/// An invitation is never an account: it grants nothing by itself in this slice, it is never
/// selected by the legacy OAuth-admission or global-token account selector, and it never counts
/// towards <c>UserService.GetUserCountAsync</c>.
/// </para>
/// </summary>
public sealed class UserInvitationEntity
{
    /// <summary>Primary key — auto-incrementing identifier, stable for the invitation's lifetime.</summary>
    public int Id { get; set; }

    /// <summary>
    /// The NORMALIZED GitHub login this invitation is for (trimmed, case-folded). Unique: one
    /// invitation per identity, enforced by a unique database index rather than a read-check.
    /// </summary>
    public string NormalizedUsername { get; set; } = string.Empty;

    /// <summary>The role the invited account will carry once activated.</summary>
    public UserRole Role { get; set; } = UserRole.Viewer;

    /// <summary>
    /// Whether the invitation is enabled. A revoked invitation is <c>false</c> and can never be
    /// activated until it is explicitly re-enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>UTC creation timestamp (ISO 8601 string).</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the last state change (ISO 8601 string).</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp of the revocation (ISO 8601 string), or null while the invitation is enabled.
    /// Revocation keeps the row (and its history) instead of deleting it.
    /// </summary>
    public string? RevokedAt { get; set; }

    /// <summary>
    /// The <see cref="UserEntity.Id"/> of the activated account this invitation was consumed into,
    /// or null while the invitation is still pending.
    /// </summary>
    public int? ActivatedUserId { get; set; }

    /// <summary>UTC timestamp of the activation (ISO 8601 string), or null while still pending.</summary>
    public string? ActivatedAt { get; set; }
}
