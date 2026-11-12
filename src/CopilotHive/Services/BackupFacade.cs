using System.Security.Claims;

using CopilotHive.Persistence;
using CopilotHive.Security;

namespace CopilotHive.Services;

/// <summary>
/// Facade over the backup surface used by the Configuration UI and the backup REST API: listing the
/// existing backup archives, creating a new one, downloading one, and restoring one. Endpoint
/// handlers (and the Blazor component) depend on this interface instead of reaching into
/// <see cref="BackupService"/> directly, so the HTTP layer stays thin, the create sequence
/// (create archive → derive file name → look up its metadata) lives in exactly one place, and the
/// archive-name validation a download or a restore needs is applied in exactly one place.
/// </summary>
/// <remarks>
/// <para>
/// AUTHORIZATION IS PART OF EVERY OPERATION, AND IT RUNS FIRST. Every backup operation — list,
/// create, download and restore — requires <see cref="HiveCapability.GlobalAdmin"/>, re-resolved
/// FRESH from current stored state through <see cref="HivePrincipalResolver"/> on every call (a
/// demoted or disabled account is refused on its very next operation), and a refusal produces
/// <see cref="FacadeErrorKind.Forbidden"/> BEFORE the file system is touched. There is deliberately
/// NO identity-free overload.
/// </para>
/// <para>
/// <see cref="BackupService"/> itself stays a TRUSTED INTERNAL service with its public API
/// unchanged: this facade is the only user-facing adapter and it never reimplements the archive
/// work — it delegates to the service verbatim.
/// </para>
/// <para>
/// Each operation returns a <see cref="FacadeResult{T}"/>. The pre-facade endpoint handlers caught
/// NOTHING for list/create, so those two catch nothing either: every exception — including
/// <see cref="OperationCanceledException"/> — propagates to the caller exactly as it did before,
/// which ASP.NET turns into a 500. Download and restore DO map their own two validation outcomes
/// (<see cref="FacadeErrorKind.BadRequest"/> for an archive name that is not a bare file name inside
/// the backup directory, <see cref="FacadeErrorKind.NotFound"/> when that archive does not exist),
/// exactly as the pre-facade handlers answered <c>{"error": ...}</c> with 400/404.
/// </para>
/// </remarks>
public interface IBackupFacade
{
    /// <summary>
    /// Lists the existing backup archives, most recent first.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result (<see cref="FacadeErrorKind.None"/>) carrying the archive metadata — an empty
    /// list when no backups exist — or <see cref="FacadeErrorKind.Forbidden"/> when the caller does
    /// not currently hold <see cref="HiveCapability.GlobalAdmin"/>. Any exception propagates.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<BackupInfoDto>>> GetBackupsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Creates a new backup archive and returns its metadata.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token forwarded to the backup service.</param>
    /// <returns>
    /// A success result (<see cref="FacadeErrorKind.None"/>) carrying the metadata of the newly
    /// created archive, or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently
    /// hold <see cref="HiveCapability.GlobalAdmin"/>. Any failure — including cancellation —
    /// propagates to the caller as an exception, exactly as the pre-facade endpoint handler behaved.
    /// </returns>
    Task<FacadeResult<BackupInfoDto>> CreateBackupAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Resolves an archive for DOWNLOAD, applying the path-traversal validation the pre-facade
    /// <c>GET /api/backup/{fileName}</c> handler performed.
    /// </summary>
    /// <param name="fileName">The requested archive file name.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// A success result carrying the resolved <see cref="BackupDownload"/>, an
    /// <see cref="FacadeErrorKind.BadRequest"/> result carrying <c>"Invalid file name."</c> for a name
    /// that is not a bare file name inside the backup directory, an
    /// <see cref="FacadeErrorKind.NotFound"/> result carrying <c>"Backup not found."</c> when the
    /// archive does not exist, or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not
    /// currently hold <see cref="HiveCapability.GlobalAdmin"/>.
    /// </returns>
    Task<FacadeResult<BackupDownload>> OpenBackupForDownloadAsync(
        string fileName, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Restores a backup archive, applying the path-traversal validation the pre-facade
    /// <c>POST /api/backup/restore</c> handler performed before calling
    /// <see cref="BackupService.RestoreBackupAsync"/>.
    /// </summary>
    /// <param name="fileName">The requested archive file name.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token forwarded to the backup service.</param>
    /// <returns>
    /// A success result carrying the <see cref="BackupRestore"/> outcome, an
    /// <see cref="FacadeErrorKind.BadRequest"/> result carrying <c>"FileName is required."</c> for a
    /// blank name or <c>"Invalid file name."</c> for a name that is not a bare file name inside the
    /// backup directory, an <see cref="FacadeErrorKind.NotFound"/> result carrying
    /// <c>"Backup not found."</c> when the archive does not exist, or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.GlobalAdmin"/>. A failure of the restore itself propagates.
    /// </returns>
    Task<FacadeResult<BackupRestore>> RestoreBackupAsync(
        string fileName, ClaimsPrincipal? actingUser, CancellationToken ct);
}

/// <summary>
/// The resolved archive a DOWNLOAD serves: the absolute path plus the bare file name the response
/// uses as its download name. Only ever produced by the facade, after the caller was authorized and
/// the name validated, so a handler can serve the file without repeating either check.
/// </summary>
/// <param name="FullPath">The absolute path of the existing archive inside the backup directory.</param>
/// <param name="FileName">The bare archive file name.</param>
public sealed record BackupDownload(string FullPath, string FileName);

/// <summary>
/// The outcome of a RESTORE, projected from <see cref="BackupService.RestoreResult"/> with the exact
/// fields the pre-facade <c>POST /api/backup/restore</c> handler returned.
/// </summary>
/// <param name="DatabaseRestored">Whether the SQLite database was restored.</param>
/// <param name="BrainMasterSession">Whether the Brain master session file was restored.</param>
/// <param name="BrainGoalSessionCount">Number of Brain goal session files restored.</param>
/// <param name="ComposerSession">Whether the Composer session file was restored.</param>
/// <param name="MetricsCount">Number of metrics files restored.</param>
/// <param name="KeysCount">Number of data protection key files restored.</param>
/// <param name="SafetyBackupPath">Full path to the safety backup created before restoring.</param>
public sealed record BackupRestore(
    bool DatabaseRestored,
    bool BrainMasterSession,
    int BrainGoalSessionCount,
    bool ComposerSession,
    int MetricsCount,
    int KeysCount,
    string SafetyBackupPath)
{
    /// <summary>Projects a <see cref="BackupService.RestoreResult"/> onto its wire representation.</summary>
    /// <param name="result">The restore result produced by the backup service.</param>
    /// <returns>The DTO the restore route serializes.</returns>
    public static BackupRestore From(BackupService.RestoreResult result) => new(
        result.DatabaseRestored,
        result.BrainMasterSession,
        result.BrainGoalSessionCount,
        result.ComposerSession,
        result.MetricsCount,
        result.KeysCount,
        result.SafetyBackupPath);
}

/// <summary>
/// Default implementation of <see cref="IBackupFacade"/> delegating to <see cref="BackupService"/>.
/// The backup service is a required dependency (it is registered unconditionally at startup), so
/// there is no "not configured" degradation path. The stateless principal resolver and the pure
/// policy evaluator are required too, so there is no unauthenticated construction.
/// </summary>
public sealed class BackupFacade : IBackupFacade
{
    private readonly BackupService _backup;
    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy;
    private readonly ILogger<BackupFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="BackupFacade"/>.
    /// </summary>
    /// <param name="backup">The backup service performing the archive work.</param>
    /// <param name="resolver">
    /// The stateless principal resolver every operation re-resolves through. It is REQUIRED so there
    /// is no unauthenticated construction: the resolver and the policy are stateless singletons, and
    /// this facade never captures the scoped circuit accessor.
    /// </param>
    /// <param name="policy">The pure capability evaluator (required together with <paramref name="resolver"/>).</param>
    /// <param name="log">Logger instance.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public BackupFacade(
        BackupService backup,
        HivePrincipalResolver resolver,
        HivePolicy policy,
        ILogger<BackupFacade> log)
    {
        _backup = backup;
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<BackupInfoDto>>> GetBackupsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(GetBackupsAsync), ct))
            return Forbidden<IReadOnlyList<BackupInfoDto>>();

        // No exception handling: the pre-facade GET handler caught nothing, so an I/O failure
        // still surfaces as an exception (ASP.NET → 500).
        var backups = _backup.ListBackups();
        _log.LogDebug("Listed {Count} backup archive(s).", backups.Count);
        return new(
            true,
            backups.Select(b => new BackupInfoDto(b.FileName, b.SizeBytes, b.CreatedAt)).ToList(),
            null,
            FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<BackupInfoDto>> CreateBackupAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(CreateBackupAsync), ct))
            return Forbidden<BackupInfoDto>();

        // Mirrors the pre-facade POST handler exactly: create the archive, take its file name,
        // and find the matching entry in the freshly listed backups. Nothing is caught — a
        // failing create (or a missing entry) throws, exactly as before.
        var path = await _backup.CreateBackupAsync(ct);
        var fileName = Path.GetFileName(path);
        var info = _backup.ListBackups().First(b => b.FileName == fileName);
        _log.LogInformation("Created backup archive {FileName}.", info.FileName);
        return new(
            true,
            new BackupInfoDto(info.FileName, info.SizeBytes, info.CreatedAt),
            null,
            FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<BackupDownload>> OpenBackupForDownloadAsync(
        string fileName, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(OpenBackupForDownloadAsync), ct))
            return Forbidden<BackupDownload>();

        // The download route's own validation, moved here verbatim so a handler cannot serve a file
        // without it: a whitespace/blank name, a directory separator, a ".." segment or anything that
        // is not a bare file name is refused.
        if (!IsBareArchiveName(fileName))
            return InvalidFileName<BackupDownload>();

        if (!TryResolveArchivePath(fileName, out var fullPath))
            return InvalidFileName<BackupDownload>();

        if (!File.Exists(fullPath))
            return new(false, null, "Backup not found.", FacadeErrorKind.NotFound);

        return new(true, new BackupDownload(fullPath, fileName), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<BackupRestore>> RestoreBackupAsync(
        string fileName, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(RestoreBackupAsync), ct))
            return Forbidden<BackupRestore>();

        // The restore route's own validation, moved here verbatim (its first check reports the
        // distinct "FileName is required." message).
        if (string.IsNullOrWhiteSpace(fileName))
            return new(false, null, "FileName is required.", FacadeErrorKind.BadRequest);

        if (!IsBareArchiveName(fileName))
            return InvalidFileName<BackupRestore>();

        if (!TryResolveArchivePath(fileName, out var fullPath))
            return InvalidFileName<BackupRestore>();

        if (!File.Exists(fullPath))
            return new(false, null, "Backup not found.", FacadeErrorKind.NotFound);

        // A failure of the restore itself propagates, exactly as before the facade existed.
        var result = await _backup.RestoreBackupAsync(fullPath, ct);
        return new(true, BackupRestore.From(result), null, FacadeErrorKind.None);
    }

    // ── Authorization + shared results ──────────────────────────────────────

    /// <summary>
    /// THE ONE AUTHORIZATION CALL every backup operation makes, BEFORE any of its own work: one fresh
    /// resolution of the caller's CURRENT stored state and one
    /// <see cref="HiveCapability.GlobalAdmin"/> evaluation.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="operation">The operation name (log context only).</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates, never a denial.</param>
    /// <returns><c>true</c> only when the caller currently holds <see cref="HiveCapability.GlobalAdmin"/>.</returns>
    private Task<bool> IsAllowedAsync(ClaimsPrincipal? actingUser, string operation, CancellationToken ct) =>
        FacadeAuthorization.IsAllowedAsync(
            _resolver, _policy, actingUser, HiveCapability.GlobalAdmin, _log, operation, ct);

    /// <summary>Builds the shared denial result for a value-producing operation.</summary>
    /// <typeparam name="T">The facade value type of the operation.</typeparam>
    /// <returns>A <see cref="FacadeErrorKind.Forbidden"/> result carrying the sanitized denial text.</returns>
    private static FacadeResult<T> Forbidden<T>() =>
        new(false, default, FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden);

    /// <summary>Builds the shared invalid-file-name failure the two routes always produced.</summary>
    /// <typeparam name="T">The facade value type of the operation.</typeparam>
    /// <returns>A <see cref="FacadeErrorKind.BadRequest"/> result carrying <c>"Invalid file name."</c></returns>
    private static FacadeResult<T> InvalidFileName<T>() =>
        new(false, default, "Invalid file name.", FacadeErrorKind.BadRequest);

    /// <summary>
    /// Whether <paramref name="fileName"/> is a BARE archive file name: not blank, carrying no
    /// directory separator and no parent-directory segment, and identical to its own
    /// <see cref="Path.GetFileName(string)"/>.
    /// </summary>
    private static bool IsBareArchiveName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && !fileName.Contains('/')
        && !fileName.Contains('\\')
        && !fileName.Contains("..")
        && Path.GetFileName(fileName) == fileName;

    /// <summary>
    /// Resolves <paramref name="fileName"/> inside the backup directory and confirms the resolved
    /// path stays within it. A name that escapes the directory resolves to <c>false</c>.
    /// </summary>
    private bool TryResolveArchivePath(string fileName, out string fullPath)
    {
        var backupDir = Path.GetFullPath(_backup.BackupDirectory);
        fullPath = Path.GetFullPath(Path.Combine(backupDir, fileName));

        var backupDirWithSep = backupDir.EndsWith(Path.DirectorySeparatorChar)
            ? backupDir
            : backupDir + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(backupDirWithSep, StringComparison.Ordinal);
    }
}
