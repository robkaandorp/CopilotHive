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

    private ComposerUserState(int userId, bool legacyClaimEligible)
    {
        UserId = userId;
        LegacyClaimEligible = legacyClaimEligible;
    }

    private volatile bool _legacyHistorySettled;

    /// <summary>The validated, positive, activated internal account id.</summary>
    public int UserId { get; }

    /// <summary>
    /// <c>true</c> ONLY for an identity obtained through <see cref="ResolveOriginalAdminAsync"/>, i.e.
    /// verified against the persisted original/bootstrap account. Only such an identity ever reads
    /// the legacy session file.
    /// </summary>
    public bool LegacyClaimEligible { get; }

    /// <summary>
    /// Whether per-user persistence is safe: always for an identity that cannot claim legacy history;
    /// for the original admin only after <see cref="ComposerLegacyMigration.EnsureAsync"/> succeeded
    /// (claimed, already claimed, or no legacy source). A failed or not-yet-run migration keeps this
    /// <c>false</c>, which blocks per-user saves and session-file deletes so no fresh session can
    /// replace — or be mistaken for — legacy history.
    /// </summary>
    public bool PersistenceAllowed => !LegacyClaimEligible || _legacyHistorySettled;

    internal void MarkLegacyHistorySettled(bool settled) => _legacyHistorySettled = settled;

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

        return new ComposerUserState(userId, legacyClaimEligible: false);
    }

    /// <summary>
    /// Validates <paramref name="userId"/> exactly like <see cref="ResolveAsync(UserService, int, CancellationToken)"/>
    /// AND verifies it is the ORIGINAL account: the lowest-id activated row returned by
    /// <see cref="UserService.GetAdminUserAsync"/> (independent of role and enabled flag). Any other
    /// identity — another Admin, any other positive id — is rejected and may never claim legacy history.
    /// </summary>
    /// <param name="users">The persisted user directory.</param>
    /// <param name="userId">Candidate internal account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The account is not the original/bootstrap account.</exception>
    public static async Task<ComposerUserState> ResolveOriginalAdminAsync(UserService users, int userId, CancellationToken ct)
    {
        var activated = await ResolveAsync(users, userId, ct);

        var original = await users.GetAdminUserAsync(ct);
        if (original is null || original.Id != userId)
        {
            throw new InvalidOperationException(
                $"Account {userId} is not the original administrator ({(original is null ? "none" : original.Id.ToString(CultureInfo.InvariantCulture))}); " +
                "only the original account may claim the legacy Composer session.");
        }

        return new ComposerUserState(activated.UserId, legacyClaimEligible: true);
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

    /// <summary>Durable evidence that <paramref name="user"/> has claimed the legacy session.</summary>
    public static string LegacyMigrationEvidencePath(string stateDir, ComposerUserState user) =>
        Path.Combine(UserRoot(stateDir, user), "legacy-migration.json");

    private static string UserRoot(string stateDir, ComposerUserState user) =>
        Path.Combine(stateDir, "composer", "users", user.UserId.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Raised when the original admin's legacy-session claim cannot be completed safely.</summary>
internal sealed class ComposerMigrationException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>What <see cref="ComposerLegacyMigration.EnsureAsync"/> did.</summary>
internal enum ComposerMigrationOutcome
{
    /// <summary>The identity cannot claim legacy history; nothing was touched.</summary>
    NotEligible,

    /// <summary>Completion evidence already existed: the legacy file was NOT read or copied again.</summary>
    AlreadyClaimed,

    /// <summary>No legacy session exists: a genuinely new per-user session; no evidence is written.</summary>
    NoLegacySource,

    /// <summary>The legacy session was copied, verified, and the evidence recorded.</summary>
    Migrated,

    /// <summary>
    /// A byte-identical destination already existed with no evidence (an interrupted earlier claim);
    /// the evidence was recorded without rewriting the destination.
    /// </summary>
    CompletedExistingDestination,
}

/// <summary>
/// The original-admin migration seam: copies the legacy <c>composer-session.json</c> to the original
/// admin's canonical per-user session, verified, never overwriting, then records durable evidence so
/// a later reset/overflow delete of the per-user file can never resurrect legacy history. The legacy
/// file is kept. Any failure throws <see cref="ComposerMigrationException"/> having written NO evidence.
/// </summary>
/// <remarks>Test-driven seam only: production wiring is deferred to the runtime goal.</remarks>
internal static class ComposerLegacyMigration
{
    /// <summary>Claims legacy history for an eligible identity; a no-op for any other identity.</summary>
    /// <param name="stateDir">Runtime state directory.</param>
    /// <param name="user">The validated identity (only an original-admin identity migrates).</param>
    /// <param name="ct">Cancellation token; cancellation is propagated, never recorded as a failure.</param>
    /// <param name="probeSourceAttributes">
    /// Test seam for the legacy-source existence/type probe (defaults to <see cref="File.GetAttributes(string)"/>);
    /// lets tests inject a genuine filesystem fault. Inert when <c>null</c>.
    /// </param>
    public static async Task<ComposerMigrationOutcome> EnsureAsync(
        string stateDir, ComposerUserState user, CancellationToken ct,
        Func<string, FileAttributes>? probeSourceAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!user.LegacyClaimEligible)
            return ComposerMigrationOutcome.NotEligible;

        user.MarkLegacyHistorySettled(false);
        try
        {
            var outcome = await ClaimAsync(stateDir, user, ct, probeSourceAttributes ?? File.GetAttributes);
            user.MarkLegacyHistorySettled(true);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ComposerMigrationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ComposerMigrationException(
                $"Legacy Composer session migration for user {user.UserId} failed; per-user persistence is blocked: {ex.Message}", ex);
        }
    }

    private static async Task<ComposerMigrationOutcome> ClaimAsync(
        string stateDir, ComposerUserState user, CancellationToken ct, Func<string, FileAttributes> probeSourceAttributes)
    {
        var evidence = ComposerUserState.LegacyMigrationEvidencePath(stateDir, user);
        if (File.Exists(evidence))
            return ComposerMigrationOutcome.AlreadyClaimed;

        var source = ComposerUserState.SessionFilePath(stateDir, null);
        if (!IsLegacySourcePresent(source, probeSourceAttributes))
            return ComposerMigrationOutcome.NoLegacySource;

        var sourceBytes = await File.ReadAllBytesAsync(source, ct);
        await EnsureSourceIsValidSessionAsync(source, ct);

        var destination = ComposerUserState.SessionFilePath(stateDir, user);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        ComposerMigrationOutcome outcome;
        if (File.Exists(destination))
        {
            if (!(await File.ReadAllBytesAsync(destination, ct)).AsSpan().SequenceEqual(sourceBytes))
            {
                throw new ComposerMigrationException(
                    $"Cannot claim the legacy Composer session for user {user.UserId}: a different per-user session already exists at " +
                    $"'{destination}'. Neither file was changed; resolve the conflict manually.");
            }

            outcome = ComposerMigrationOutcome.CompletedExistingDestination;
        }
        else
        {
            await CopyVerifiedWithoutOverwriteAsync(sourceBytes, destination, ct);
            outcome = ComposerMigrationOutcome.Migrated;
        }

        // The source must still be what was copied before completion is recorded.
        if (!(await File.ReadAllBytesAsync(source, ct)).AsSpan().SequenceEqual(sourceBytes))
            throw new ComposerMigrationException("The legacy Composer session changed during migration; completion was not recorded.");

        await WriteEvidenceAsync(evidence, user, sourceBytes, ct);
        return outcome;
    }

    /// <summary>
    /// Distinguishes the three source states. Only a genuinely ABSENT entry (file or directory not
    /// found) is "no legacy source". A directory at the source path is an invalid path collision and
    /// fails closed. Any other probe fault (e.g. access denied) propagates — <see cref="File.Exists"/>
    /// is deliberately not used because it reports every fault as "absent".
    /// </summary>
    private static bool IsLegacySourcePresent(string source, Func<string, FileAttributes> probeSourceAttributes)
    {
        FileAttributes attributes;
        try
        {
            attributes = probeSourceAttributes(source);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new ComposerMigrationException(
                $"The legacy Composer session path '{source}' is a directory, not a session file; it cannot be migrated or treated as absent.");
        }

        return true;
    }

    /// <summary>
    /// Rejects a source that is not a USABLE session before anything is copied or recorded. Loading
    /// alone is not enough: a payload such as <c>{"messages":[null]}</c> deserializes but then fails in
    /// the consumers (token estimate, chat history, save). So after the load this walks exactly what
    /// they read — every message and every message content must be non-null, and the estimate and
    /// usage snapshot must be computable. Shape, not volume: <c>{}</c> (an empty session) stays valid.
    /// </summary>
    private static async Task EnsureSourceIsValidSessionAsync(string source, CancellationToken ct)
    {
        SharpCoder.AgentSession session;
        try
        {
            session = await SharpCoder.AgentSession.LoadAsync(source, ct);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidDataException
            or ArgumentException or NullReferenceException or InvalidOperationException)
        {
            throw new ComposerMigrationException($"The legacy Composer session '{source}' is corrupt; it was not migrated.", ex);
        }

        try
        {
            ValidateSessionStructure(session);
        }
        catch (Exception ex) when (ex is not ComposerMigrationException and not OperationCanceledException)
        {
            throw new ComposerMigrationException($"The legacy Composer session '{source}' has an unusable structure; it was not migrated.", ex);
        }
    }

    private static void ValidateSessionStructure(SharpCoder.AgentSession session)
    {
        var history = session.MessageHistory ?? throw new InvalidDataException("The session has no message history.");
        foreach (var message in history)
        {
            if (message is null)
                throw new InvalidDataException("The session contains a null message.");

            foreach (var content in message.Contents ?? [])
            {
                if (content is null)
                    throw new InvalidDataException("The session contains a message with a null content entry.");
            }

            _ = message.Role;
            _ = message.Text;
        }

        _ = session.EstimatedContextTokens;
        _ = session.Usage.Snapshot();
    }

    private static async Task CopyVerifiedWithoutOverwriteAsync(byte[] content, string destination, CancellationToken ct)
    {
        var temp = destination + $".migrating-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(temp, content, ct);
            if (!(await File.ReadAllBytesAsync(temp, ct)).AsSpan().SequenceEqual(content))
                throw new ComposerMigrationException("The migrated copy did not verify against the legacy session.");

            // overwrite:false — an existing destination (a racing writer) throws instead of being replaced.
            File.Move(temp, destination, overwrite: false);
        }
        catch
        {
            if (File.Exists(temp))
                File.Delete(temp);
            throw;
        }

        if (!(await File.ReadAllBytesAsync(destination, ct)).AsSpan().SequenceEqual(content))
            throw new ComposerMigrationException("The migrated session did not verify against the legacy session.");
    }

    private static async Task WriteEvidenceAsync(string evidence, ComposerUserState user, byte[] sourceBytes, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            userId = user.UserId,
            source = "composer-session.json",
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)),
            length = sourceBytes.Length,
        });

        // Temp + move: the evidence file either exists complete or not at all.
        var temp = evidence + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temp, json, ct);
            File.Move(temp, evidence, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp))
                File.Delete(temp);
            throw;
        }
    }
}
