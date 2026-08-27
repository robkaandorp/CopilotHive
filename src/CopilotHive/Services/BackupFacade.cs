using CopilotHive.Persistence;

namespace CopilotHive.Services;

/// <summary>
/// Facade over the backup surface used by the Configuration UI: listing the existing backup
/// archives and creating a new one. Endpoint handlers (and the Blazor component) depend on
/// this interface instead of reaching into <see cref="BackupService"/> directly, so the HTTP
/// layer stays thin and the create sequence (create archive → derive file name → look up its
/// metadata) lives in exactly one place.
/// </summary>
/// <remarks>
/// <para>
/// Each operation returns a <see cref="FacadeResult{T}"/>, but ONLY for the success path
/// (<see cref="FacadeResult{T}.Success"/> is <c>true</c> and
/// <see cref="FacadeResult{T}.Kind"/> is <see cref="FacadeErrorKind.None"/>). The pre-facade
/// endpoint handlers caught NOTHING, so this facade catches nothing either: every exception —
/// including <see cref="OperationCanceledException"/> — propagates to the caller exactly as it
/// did before, which ASP.NET turns into a 500. There is deliberately no typed create-failure
/// result.
/// </para>
/// <para>
/// The download (<c>GET /api/backup/{fileName}</c>) and restore (<c>POST /api/backup/restore</c>)
/// operations are intentionally NOT part of this facade: download is a browser navigation and
/// restore is not invoked by the Configuration component, so both stay directly on
/// <see cref="BackupService"/>.
/// </para>
/// </remarks>
public interface IBackupFacade
{
    /// <summary>
    /// Lists the existing backup archives, most recent first.
    /// </summary>
    /// <returns>
    /// Always a success result (<see cref="FacadeErrorKind.None"/>) carrying the archive
    /// metadata; an empty list when no backups exist. Any exception propagates to the caller.
    /// </returns>
    FacadeResult<IReadOnlyList<BackupInfoDto>> GetBackups();

    /// <summary>
    /// Creates a new backup archive and returns its metadata.
    /// </summary>
    /// <param name="ct">Cancellation token forwarded to the backup service.</param>
    /// <returns>
    /// Always a success result (<see cref="FacadeErrorKind.None"/>) carrying the metadata of the
    /// newly created archive. Any failure — including cancellation — propagates to the caller as
    /// an exception, exactly as the pre-facade endpoint handler behaved.
    /// </returns>
    Task<FacadeResult<BackupInfoDto>> CreateBackupAsync(CancellationToken ct);
}

/// <summary>
/// Default implementation of <see cref="IBackupFacade"/> delegating to <see cref="BackupService"/>.
/// The backup service is a required dependency (it is registered unconditionally at startup), so
/// there is no "not configured" degradation path.
/// </summary>
public sealed class BackupFacade : IBackupFacade
{
    private readonly BackupService _backup;
    private readonly ILogger<BackupFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="BackupFacade"/>.
    /// </summary>
    /// <param name="backup">The backup service performing the archive work.</param>
    /// <param name="log">Logger instance.</param>
    public BackupFacade(BackupService backup, ILogger<BackupFacade> log)
    {
        _backup = backup;
        _log = log;
    }

    /// <inheritdoc />
    public FacadeResult<IReadOnlyList<BackupInfoDto>> GetBackups()
    {
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
    public async Task<FacadeResult<BackupInfoDto>> CreateBackupAsync(CancellationToken ct)
    {
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
}
