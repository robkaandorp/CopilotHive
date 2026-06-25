using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Persistence;

/// <summary>
/// Creates compressed (tar.gz) backups of the CopilotHive runtime state, including the
/// SQLite database, Brain session files, the Composer session file, metrics, and data
/// protection keys. Backups are stored under <c>{stateDir}/backups</c> and the most recent
/// ten archives are retained.
/// </summary>
public sealed class BackupService
{
    private readonly string _stateDir;
    private readonly IDbContextFactory<CopilotHiveDbContext> _dbContextFactory;
    private readonly ILogger<BackupService> _logger;

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Creates a new <see cref="BackupService"/>.</summary>
    /// <param name="stateDir">The state directory whose contents are backed up.</param>
    /// <param name="dbContextFactory">Factory used to resolve the SQLite database path.</param>
    /// <param name="logger">Logger instance.</param>
    public BackupService(
        string stateDir,
        IDbContextFactory<CopilotHiveDbContext> dbContextFactory,
        ILogger<BackupService> logger)
    {
        _stateDir = stateDir;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>The directory where backup archives are stored.</summary>
    public string BackupDirectory => Path.Combine(_stateDir, "backups");

    /// <summary>Metadata describing a single backup archive.</summary>
    /// <param name="FileName">The archive file name.</param>
    /// <param name="SizeBytes">The archive size in bytes.</param>
    /// <param name="CreatedAt">The UTC creation time of the archive.</param>
    public sealed record BackupInfo(string FileName, long SizeBytes, DateTime CreatedAt);

    /// <summary>
    /// Creates a new tar.gz backup archive and returns its full path.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="archivePrefix">
    /// The archive file name prefix. Defaults to <c>"copilothive-backup"</c>. When a non-default
    /// prefix is supplied (e.g. <c>"pre-restore"</c>), the archive is excluded from normal pruning.
    /// </param>
    public async Task<string> CreateBackupAsync(CancellationToken ct = default, string archivePrefix = "copilothive-backup")
    {
        Directory.CreateDirectory(BackupDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture);
        var tempDir = Path.Combine(BackupDirectory, $"tmp-{timestamp}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Database backup
            var databaseBackedUp = BackupDatabase(Path.Combine(tempDir, "copilothive.db"));

            // 2. Brain master session
            var brainMasterSession = CopyIfExists(
                Path.Combine(_stateDir, "brain-master.json"),
                Path.Combine(tempDir, "brain-master.json"));

            // 3. Brain goal sessions
            var brainGoalSessionCount = 0;
            if (Directory.Exists(_stateDir))
            {
                foreach (var file in Directory.GetFiles(_stateDir, "brain-goal-*.json"))
                {
                    File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)), overwrite: true);
                    brainGoalSessionCount++;
                }
            }

            // 4. Composer session
            var composerSession = CopyIfExists(
                Path.Combine(_stateDir, "composer-session.json"),
                Path.Combine(tempDir, "composer-session.json"));

            // 5. Metrics directory
            var metricsCount = CopyDirectory(
                Path.Combine(_stateDir, "metrics"),
                Path.Combine(tempDir, "metrics"));

            // 6. Keys directory
            var keysCount = CopyDirectory(
                Path.Combine(_stateDir, "keys"),
                Path.Combine(tempDir, "keys"));

            // 7. Manifest
            var manifest = new
            {
                timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                copilothiveVersion = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                sharpCoderVersion = typeof(SharpCoder.CodingAgent).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                databaseBackedUp,
                brainMasterSession,
                brainGoalSessionCount,
                composerSession,
                metricsCount,
                keysCount,
            };
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "backup-manifest.json"),
                JsonSerializer.Serialize(manifest, ManifestOptions),
                ct);

            // 8. Create the tar.gz archive
            var archivePath = Path.Combine(BackupDirectory, $"{archivePrefix}-{timestamp}.tar.gz");
            await CreateArchiveAsync(tempDir, archivePath, ct);

            _logger.LogInformation("Backup created at {ArchivePath}", archivePath);

            // Only prune the standard backup set; custom-prefixed archives (e.g. pre-restore
            // safety backups) are excluded from normal pruning entirely.
            if (archivePrefix == "copilothive-backup")
                PruneOldBackups();

            return archivePath;
        }
        finally
        {
            // 9. Clean up the temp directory
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temp backup directory {TempDir}", tempDir);
            }
        }
    }

    /// <summary>Describes the outcome of a restore operation.</summary>
    /// <param name="DatabaseRestored">Whether the SQLite database was restored.</param>
    /// <param name="BrainMasterSession">Whether the Brain master session file was restored.</param>
    /// <param name="BrainGoalSessionCount">Number of Brain goal session files restored.</param>
    /// <param name="ComposerSession">Whether the Composer session file was restored.</param>
    /// <param name="MetricsCount">Number of metrics files restored.</param>
    /// <param name="KeysCount">Number of data protection key files restored.</param>
    /// <param name="SafetyBackupPath">Full path to the safety backup created before restoring.</param>
    public sealed record RestoreResult(
        bool DatabaseRestored,
        bool BrainMasterSession,
        int BrainGoalSessionCount,
        bool ComposerSession,
        int MetricsCount,
        int KeysCount,
        string SafetyBackupPath);

    /// <summary>
    /// Restores a previously created tar.gz backup archive, extracting and replacing the
    /// database, Brain/Composer session files, metrics, and keys. A safety backup of the
    /// current state is created before any files are replaced.
    /// </summary>
    /// <param name="archivePath">Full path to the backup archive to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="FileNotFoundException">Thrown when the archive file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the archive is missing its manifest.</exception>
    public async Task<RestoreResult> RestoreBackupAsync(string archivePath, CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Backup archive not found.", archivePath);

        Directory.CreateDirectory(BackupDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture);
        var tempDir = Path.Combine(BackupDirectory, $"restore-tmp-{timestamp}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Extract the tar.gz archive into the temp directory.
            await ExtractArchiveAsync(archivePath, tempDir, ct);

            // 2. Verify the manifest exists.
            var manifestPath = Path.Combine(tempDir, "backup-manifest.json");
            if (!File.Exists(manifestPath))
                throw new InvalidOperationException("Backup archive is missing its manifest (backup-manifest.json).");

            // 3. Create a safety backup of the current state before replacing files.
            var safetyBackupPath = await CreateBackupAsync(ct, "pre-restore");

            // 4. Restore the database.
            var databaseRestored = RestoreDatabase(tempDir);

            // 5. Restore the Brain master session.
            var brainMasterSession = ReplaceFile(
                Path.Combine(tempDir, "brain-master.json"),
                Path.Combine(_stateDir, "brain-master.json"));

            // 6. Restore the Brain goal sessions.
            foreach (var existing in Directory.Exists(_stateDir)
                ? Directory.GetFiles(_stateDir, "brain-goal-*.json")
                : [])
            {
                File.Delete(existing);
            }

            var brainGoalSessionCount = 0;
            foreach (var file in Directory.GetFiles(tempDir, "brain-goal-*.json"))
            {
                File.Copy(file, Path.Combine(_stateDir, Path.GetFileName(file)), overwrite: true);
                brainGoalSessionCount++;
            }

            // 7. Restore the Composer session.
            var composerSession = ReplaceFile(
                Path.Combine(tempDir, "composer-session.json"),
                Path.Combine(_stateDir, "composer-session.json"));

            // 8. Restore the metrics directory.
            var metricsCount = ReplaceDirectory(
                Path.Combine(tempDir, "metrics"),
                Path.Combine(_stateDir, "metrics"));

            // 9. Restore the keys directory.
            var keysCount = ReplaceDirectory(
                Path.Combine(tempDir, "keys"),
                Path.Combine(_stateDir, "keys"));

            _logger.LogInformation(
                "Restore complete from {ArchivePath}: database={DatabaseRestored}, brainMaster={BrainMasterSession}, "
                + "brainGoals={BrainGoalSessionCount}, composer={ComposerSession}, metrics={MetricsCount}, keys={KeysCount}. "
                + "Safety backup at {SafetyBackupPath}",
                archivePath, databaseRestored, brainMasterSession, brainGoalSessionCount,
                composerSession, metricsCount, keysCount, safetyBackupPath);

            return new RestoreResult(
                databaseRestored,
                brainMasterSession,
                brainGoalSessionCount,
                composerSession,
                metricsCount,
                keysCount,
                safetyBackupPath);
        }
        finally
        {
            // Clean up the temp extraction directory.
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temp restore directory {TempDir}", tempDir);
            }
        }
    }

    /// <summary>Lists all backup archives, most recent first.</summary>
    public IReadOnlyList<BackupInfo> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory))
            return [];

        return Directory
            .GetFiles(BackupDirectory, "copilothive-backup-*.tar.gz")
            .Select(file => new BackupInfo(
                Path.GetFileName(file),
                new FileInfo(file).Length,
                File.GetCreationTimeUtc(file)))
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    private bool BackupDatabase(string destPath)
    {
        try
        {
            string? dbPath;
            using (var context = _dbContextFactory.CreateDbContext())
            {
                var connectionString = context.Database.GetConnectionString();
                dbPath = ExtractDataSource(connectionString);
            }

            if (string.IsNullOrEmpty(dbPath) || dbPath == ":memory:" || !File.Exists(dbPath))
            {
                _logger.LogWarning("Database file not found for backup (path: {DbPath})", dbPath ?? "<null>");
                return false;
            }

            using (var sourceConn = new SqliteConnection($"Data Source={dbPath}"))
            using (var backupConn = new SqliteConnection($"Data Source={destPath}"))
            {
                sourceConn.Open();
                backupConn.Open();
                sourceConn.BackupDatabase(backupConn);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to back up database");
            return false;
        }
    }

    private bool RestoreDatabase(string tempDir)
    {
        var extractedDb = Path.Combine(tempDir, "copilothive.db");
        if (!File.Exists(extractedDb))
            return false;

        try
        {
            string? dbPath;
            // Dispose the DbContext to close any open database connections.
            using (var context = _dbContextFactory.CreateDbContext())
            {
                var connectionString = context.Database.GetConnectionString();
                dbPath = ExtractDataSource(connectionString);
            }

            if (string.IsNullOrEmpty(dbPath) || dbPath == ":memory:")
            {
                _logger.LogWarning("Cannot restore database: invalid target path (path: {DbPath})", dbPath ?? "<null>");
                return false;
            }

            // Drop pooled connections so file handles are released before deleting.
            SqliteConnection.ClearAllPools();

            // Delete the old database and its sidecar files.
            DeleteIfExists(dbPath);
            DeleteIfExists(dbPath + "-wal");
            DeleteIfExists(dbPath + "-shm");

            var targetDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Copy(extractedDb, dbPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore database");
            return false;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private bool ReplaceFile(string sourcePath, string destPath)
    {
        DeleteIfExists(destPath);

        if (!File.Exists(sourcePath))
            return false;

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        File.Copy(sourcePath, destPath, overwrite: true);
        return true;
    }

    private static int ReplaceDirectory(string sourceDir, string destDir)
    {
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, recursive: true);

        return CopyDirectory(sourceDir, destDir);
    }

    private async Task ExtractArchiveAsync(string archivePath, string tempDir, CancellationToken ct)
    {
        var tempDirFull = Path.GetFullPath(tempDir).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var fileStream = File.OpenRead(archivePath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);
        TarEntry? entry;
        while ((entry = await tarReader.GetNextEntryAsync(cancellationToken: ct)) is not null)
        {
            if (entry.EntryType != TarEntryType.RegularFile)
                continue;

            var entryName = entry.Name;

            // Reject entries with parent-directory traversal segments.
            if (entryName.Split('/', '\\').Any(segment => segment == ".."))
            {
                _logger.LogWarning("Skipping archive entry with path traversal segment: {EntryName}", entryName);
                continue;
            }

            // Reject rooted/absolute entry names (e.g. "/etc/passwd" or "C:\\...").
            var normalizedName = entryName.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedName))
            {
                _logger.LogWarning("Skipping archive entry with rooted path: {EntryName}", entryName);
                continue;
            }

            var destPath = Path.GetFullPath(Path.Combine(tempDir, normalizedName));

            // Ensure the resolved path stays within the temp extraction directory.
            if (!destPath.StartsWith(tempDirFull, StringComparison.Ordinal))
            {
                _logger.LogWarning("Skipping archive entry that escapes the extraction directory: {EntryName}", entryName);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private static string? ExtractDataSource(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            return null;

        var builder = new SqliteConnectionStringBuilder(connectionString);
        return builder.DataSource;
    }

    private bool CopyIfExists(string sourcePath, string destPath)
    {
        if (!File.Exists(sourcePath))
            return false;

        File.Copy(sourcePath, destPath, overwrite: true);
        return true;
    }

    private static int CopyDirectory(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir))
            return 0;

        var count = 0;
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }

        return count;
    }

    private static async Task CreateArchiveAsync(string tempDir, string archivePath, CancellationToken ct)
    {
        await using var fileStream = File.Create(archivePath);
        await using var gzipStream = new GZipStream(fileStream, CompressionLevel.SmallestSize);
        await using var tarWriter = new TarWriter(gzipStream, TarEntryFormat.Pax);

        foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
        {
            var entryName = Path.GetRelativePath(tempDir, file).Replace(Path.DirectorySeparatorChar, '/');
            await tarWriter.WriteEntryAsync(file, entryName, ct);
        }
    }

    private void PruneOldBackups()
    {
        var oldBackups = Directory
            .GetFiles(BackupDirectory, "copilothive-backup-*.tar.gz")
            .OrderByDescending(f => f)
            .Skip(10)
            .ToList();

        foreach (var oldBackup in oldBackups)
        {
            try
            {
                File.Delete(oldBackup);
                _logger.LogDebug("Removed old backup {BackupPath}", oldBackup);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove old backup {BackupPath}", oldBackup);
            }
        }
    }
}
