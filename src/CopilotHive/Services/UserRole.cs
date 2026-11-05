namespace CopilotHive.Services;

/// <summary>
/// The authorised role of an ACTIVATED CopilotHive user account, or of a PENDING invitation.
/// <para>
/// This replaces the previous free-form role string. The persisted representation is the
/// lowercase role name (<c>"viewer"</c>, <c>"member"</c>, <c>"operator"</c>, <c>"admin"</c>), which
/// is byte-identical to the legacy <c>users.role</c> value <c>"admin"</c>, so existing databases
/// read back unchanged without any data rewrite.
/// </para>
/// <para>
/// <see cref="Viewer"/> is deliberately the CLR default (<c>0</c>): a default-constructed role value
/// must never silently mean <see cref="Admin"/>. Unknown persisted values never fall back to a role —
/// see <see cref="UserRoleText.Parse"/>.
/// </para>
/// </summary>
public enum UserRole
{
    /// <summary>Read-only access.</summary>
    Viewer = 0,

    /// <summary>Standard contributor access.</summary>
    Member = 1,

    /// <summary>Operational access (may run/observe orchestration work).</summary>
    Operator = 2,

    /// <summary>Full administrative access. The bootstrap/original account is always this role.</summary>
    Admin = 3,
}

/// <summary>
/// The ONE conversion between <see cref="UserRole"/> and its persisted lowercase text form.
/// <para>
/// Both sides are total and explicit: an unhandled enum value cannot be written, and an
/// unrecognised persisted value fails loudly instead of quietly becoming a role. Numeric text is
/// rejected too — a legacy/foreign <c>"2"</c> must never silently become <see cref="UserRole.Operator"/>.
/// </para>
/// </summary>
internal static class UserRoleText
{
    /// <summary>The lowercase text of a validated role. Throws for an undefined enum value.</summary>
    internal static string Format(UserRole role) => role switch
    {
        UserRole.Viewer => "viewer",
        UserRole.Member => "member",
        UserRole.Operator => "operator",
        UserRole.Admin => "admin",
        _ => throw new InvalidOperationException(
            $"The user role value '{(int)role}' has no persisted representation."),
    };

    /// <summary>
    /// Parses a persisted role text (case-insensitively, surrounding whitespace ignored).
    /// An unrecognised or absent value THROWS — it is never mapped to a fallback role.
    /// </summary>
    internal static UserRole Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "viewer" => UserRole.Viewer,
        "member" => UserRole.Member,
        "operator" => UserRole.Operator,
        "admin" => UserRole.Admin,
        _ => throw new InvalidOperationException(
            $"The persisted user role '{text}' is not a supported CopilotHive user role " +
            "(expected exactly one of: viewer, member, operator, admin)."),
    };

    /// <summary>
    /// Rejects an undefined enum value supplied by a caller (for example a cast integer), so an
    /// invalid role can never reach storage.
    /// </summary>
    /// <param name="role">The role supplied by the caller.</param>
    /// <param name="parameterName">The caller's parameter name, used for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="UserRole"/>.</exception>
    internal static void EnsureDefined(UserRole role, string parameterName)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                role,
                $"'{role}' is not a defined CopilotHive user role.");
        }
    }
}
