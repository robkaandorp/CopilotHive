using System.Globalization;
using CopilotHive.Services;

namespace CopilotHive.Orchestration;

/// <summary>
/// The validated identity of one per-user Composer, and the ONE place that decides where Composer
/// state lives and how it is addressed. Every consumer (<see cref="Composer"/>,
/// <see cref="ComposerAgentService"/>, <see cref="ComposerAttachmentService"/>) resolves its
/// session file, attachments root and registry id through the static resolvers here; a
/// <c>null</c> identity selects the legacy single-admin layout unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Layout for an identity-bound instance: <c>{stateDir}/composer/users/N/session.json</c> and
/// <c>{stateDir}/composer/users/N/attachments</c>, registered as <c>composer:N</c>. <c>N</c> is
/// only ever a positive internal <c>UserEntity.Id</c> that resolved to an activated account through
/// <see cref="UserService.GetActivatedUserByIdAsync"/> — never a username, GitHub id or path text,
/// and never inferred to be the legacy owner. Instances can only be obtained through
/// <see cref="ResolveAsync(UserService, int, CancellationToken)"/> or its text overload, so an
/// unvalidated id cannot reach a path.
/// </para>
/// </remarks>
public sealed class ComposerUserState
{
    private const string LegacySessionFileName = "composer-session.json";
    private const string LegacyAttachmentsDirectoryName = "composer-attachments";
    private const string LegacyRegistryId = "composer";

    private ComposerUserState(int userId) => UserId = userId;

    /// <summary>The validated, positive, activated internal account id.</summary>
    public int UserId { get; }

    /// <summary>
    /// Validates <paramref name="userId"/>: it must be positive AND resolve to an activated account.
    /// </summary>
    /// <param name="users">The persisted user directory.</param>
    /// <param name="userId">Candidate internal account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">The id is zero or negative.</exception>
    /// <exception cref="InvalidOperationException">No activated account has this id.</exception>
    public static async Task<ComposerUserState> ResolveAsync(UserService users, int userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(users);

        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId), userId, "A Composer user id must be a positive internal account id.");

        var summary = await users.GetActivatedUserByIdAsync(userId, ct);
        if (summary is null || summary.Id != userId)
            throw new InvalidOperationException($"No activated account has internal id {userId}; refusing to create Composer user state.");

        return new ComposerUserState(userId);
    }

    /// <summary>
    /// Validates an id supplied as text. Only plain ASCII digits parse; usernames, GitHub ids with
    /// decoration, signs, whitespace, separators and path text are rejected before any lookup.
    /// </summary>
    /// <param name="users">The persisted user directory.</param>
    /// <param name="rawUserId">Candidate internal id as text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">The text is not a plain integer.</exception>
    public static Task<ComposerUserState> ResolveAsync(UserService users, string? rawUserId, CancellationToken ct)
    {
        if (rawUserId is null || rawUserId.Length == 0 || !rawUserId.All(char.IsAsciiDigit)
            || !int.TryParse(rawUserId, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
        {
            throw new ArgumentException("A Composer user id must be a plain integer internal account id.", nameof(rawUserId));
        }

        return ResolveAsync(users, userId, ct);
    }

    /// <summary>Session file for <paramref name="user"/>, or the legacy single-file path when <c>null</c>.</summary>
    public static string SessionFilePath(string stateDir, ComposerUserState? user) =>
        user is null
            ? Path.Combine(stateDir, LegacySessionFileName)
            : Path.Combine(UserRoot(stateDir, user), "session.json");

    /// <summary>Attachments root (full path) for <paramref name="user"/>, or the legacy root when <c>null</c>.</summary>
    public static string AttachmentsRootPath(string stateDir, ComposerUserState? user) =>
        Path.GetFullPath(user is null
            ? Path.Combine(stateDir, LegacyAttachmentsDirectoryName)
            : Path.Combine(UserRoot(stateDir, user), "attachments"));

    /// <summary>LLM session registry id: <c>composer:N</c> for an identity, the literal <c>composer</c> when <c>null</c>.</summary>
    public static string RegistryId(ComposerUserState? user) =>
        user is null ? LegacyRegistryId : $"{LegacyRegistryId}:{user.UserId.ToString(CultureInfo.InvariantCulture)}";

    private static string UserRoot(string stateDir, ComposerUserState user) =>
        Path.Combine(stateDir, "composer", "users", user.UserId.ToString(CultureInfo.InvariantCulture));
}
