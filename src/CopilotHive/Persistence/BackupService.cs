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
    public async Task<string> CreateBackupAsync(CancellationToken ct = default)
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
            var archivePath = Path.Combine(BackupDirectory, $"copilothive-backup-{timestamp}.tar.gz");
            await CreateArchiveAsync(tempDir, archivePath, ct);

            _logger.LogInformation("Backup created at {ArchivePath}", archivePath);

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
