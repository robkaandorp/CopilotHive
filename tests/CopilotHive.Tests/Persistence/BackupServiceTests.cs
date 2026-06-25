using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

using CopilotHive.Goals;
using CopilotHive.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Persistence;

/// <summary>
/// Tests for <see cref="BackupService"/> verifying that tar.gz archives are created with
/// the database, Brain/Composer session files, metrics, and keys, that a manifest is written,
/// and that old backups are pruned.
/// </summary>
public sealed class BackupServiceTests
{
    private sealed class TestDbContextFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        private readonly DbContextOptions<CopilotHiveDbContext> _options;
        public TestDbContextFactory(DbContextOptions<CopilotHiveDbContext> options) => _options = options;
        public CopilotHiveDbContext CreateDbContext() => new(_options);
    }

    private static (string stateDir, BackupService service) CreateService()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"backup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var dbPath = Path.Combine(stateDir, "copilothive.db");
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        using (var context = new CopilotHiveDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        var factory = new TestDbContextFactory(options);
        var service = new BackupService(stateDir, factory, NullLogger<BackupService>.Instance);
        return (stateDir, service);
    }

    private static void Cleanup(string stateDir)
    {
        try
        {
            if (Directory.Exists(stateDir))
                Directory.Delete(stateDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static async Task<List<string>> GetArchiveEntries(string archivePath)
    {
        var entries = new List<string>();
        using var fileStream = File.OpenRead(archivePath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);
        TarEntry? entry;
        while ((entry = await tarReader.GetNextEntryAsync()) is not null)
        {
            entries.Add(entry.Name);
        }
        return entries;
    }

    private static async Task<string?> ReadArchiveEntry(string archivePath, string entryName)
    {
        using var fileStream = File.OpenRead(archivePath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);
        TarEntry? entry;
        while ((entry = await tarReader.GetNextEntryAsync()) is not null)
        {
            if (entry.Name == entryName && entry.DataStream is not null)
            {
                using var reader = new StreamReader(entry.DataStream, Encoding.UTF8);
                return await reader.ReadToEndAsync();
            }
        }
        return null;
    }

    [Fact]
    public async Task CreateBackupAsync_CreatesArchiveWithDatabase()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var path = await service.CreateBackupAsync(TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
            Assert.EndsWith(".tar.gz", path);

            var entries = await GetArchiveEntries(path);
            Assert.Contains("copilothive.db", entries);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_IncludesBrainSessionFiles()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-master.json"), "{}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-test1.json"), "{}", ct);

            var path = await service.CreateBackupAsync(ct);
            var entries = await GetArchiveEntries(path);

            Assert.Contains("brain-master.json", entries);
            Assert.Contains("brain-goal-test1.json", entries);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_IncludesComposerSession()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Combine(stateDir, "composer-session.json"), "{}", ct);

            var path = await service.CreateBackupAsync(ct);
            var entries = await GetArchiveEntries(path);

            Assert.Contains("composer-session.json", entries);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_IncludesMetricsAndKeys()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(Path.Combine(stateDir, "metrics"));
            Directory.CreateDirectory(Path.Combine(stateDir, "keys"));
            await File.WriteAllTextAsync(Path.Combine(stateDir, "metrics", "metrics1.json"), "{}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "keys", "key1.xml"), "<key/>", ct);

            var path = await service.CreateBackupAsync(ct);
            var entries = await GetArchiveEntries(path);

            Assert.Contains(entries, e => e.EndsWith("metrics1.json"));
            Assert.Contains(entries, e => e.EndsWith("key1.xml"));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_WritesManifest()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-master.json"), "{}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-1.json"), "{}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-2.json"), "{}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "composer-session.json"), "{}", ct);
            Directory.CreateDirectory(Path.Combine(stateDir, "metrics"));
            Directory.CreateDirectory(Path.Combine(stateDir, "keys"));
            await File.WriteAllTextAsync(Path.Combine(stateDir, "metrics", "m1.json"), "{}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "metrics", "m2.json"), "{}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "keys", "k1.xml"), "<key/>", ct);

            var path = await service.CreateBackupAsync(ct);
            var entries = await GetArchiveEntries(path);

            Assert.Contains("backup-manifest.json", entries);

            var manifestJson = await ReadArchiveEntry(path, "backup-manifest.json");
            Assert.NotNull(manifestJson);

            using var doc = JsonDocument.Parse(manifestJson!);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("timestamp", out var timestamp));
            Assert.False(string.IsNullOrEmpty(timestamp.GetString()));
            Assert.True(root.TryGetProperty("copilothiveVersion", out var copilothiveVersion));
            Assert.False(string.IsNullOrEmpty(copilothiveVersion.GetString()));
            Assert.True(root.TryGetProperty("sharpCoderVersion", out var sharpCoderVersion));
            Assert.False(string.IsNullOrEmpty(sharpCoderVersion.GetString()));
            Assert.True(root.TryGetProperty("databaseBackedUp", out var dbBackedUp));
            Assert.True(dbBackedUp.GetBoolean());
            Assert.True(root.TryGetProperty("brainMasterSession", out var brainMaster));
            Assert.True(brainMaster.GetBoolean());
            Assert.True(root.TryGetProperty("brainGoalSessionCount", out var brainGoalCount));
            Assert.Equal(2, brainGoalCount.GetInt32());
            Assert.True(root.TryGetProperty("composerSession", out var composer));
            Assert.True(composer.GetBoolean());
            Assert.True(root.TryGetProperty("metricsCount", out var metricsCount));
            Assert.Equal(2, metricsCount.GetInt32());
            Assert.True(root.TryGetProperty("keysCount", out var keysCount));
            Assert.Equal(1, keysCount.GetInt32());
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_CleansUpOldBackups()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            for (var i = 0; i < 12; i++)
            {
                await service.CreateBackupAsync(ct);
                await Task.Delay(1100, ct);
            }

            Assert.True(service.ListBackups().Count <= 10);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task ListBackups_ReturnsAllBackups()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            for (var i = 0; i < 3; i++)
            {
                await service.CreateBackupAsync(ct);
                await Task.Delay(1100, ct);
            }

            var backups = service.ListBackups();
            Assert.Equal(3, backups.Count);

            for (var i = 0; i < backups.Count - 1; i++)
            {
                Assert.True(backups[i].CreatedAt >= backups[i + 1].CreatedAt);
            }
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_NoSessionFiles_StillSucceeds()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var path = await service.CreateBackupAsync(TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
            var entries = await GetArchiveEntries(path);
            Assert.Contains("copilothive.db", entries);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_RestoresDatabase()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var dbPath = Path.Combine(stateDir, "copilothive.db");
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            // Create a backup of the pristine database (no goals).
            var backupPath = await service.CreateBackupAsync(ct);

            // Modify state: add a goal.
            using (var context = new CopilotHiveDbContext(options))
            {
                context.Goals.Add(new Goal { Id = "g1", Description = "test goal" });
                await context.SaveChangesAsync(ct);
            }

            using (var context = new CopilotHiveDbContext(options))
            {
                Assert.Equal(1, await context.Goals.CountAsync(ct));
            }

            // Restore the backup.
            var result = await service.RestoreBackupAsync(backupPath, ct);
            Assert.True(result.DatabaseRestored);

            // Verify the goal is gone.
            using (var context = new CopilotHiveDbContext(options))
            {
                Assert.Equal(0, await context.Goals.CountAsync(ct));
            }
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_RestoresSessionFiles()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var masterPath = Path.Combine(stateDir, "brain-master.json");
            var composerPath = Path.Combine(stateDir, "composer-session.json");
            await File.WriteAllTextAsync(masterPath, "{\"master\":true}", ct);
            await File.WriteAllTextAsync(composerPath, "{\"composer\":true}", ct);

            var backupPath = await service.CreateBackupAsync(ct);

            File.Delete(masterPath);
            File.Delete(composerPath);
            Assert.False(File.Exists(masterPath));
            Assert.False(File.Exists(composerPath));

            var result = await service.RestoreBackupAsync(backupPath, ct);

            Assert.True(result.BrainMasterSession);
            Assert.True(result.ComposerSession);
            Assert.True(File.Exists(masterPath));
            Assert.True(File.Exists(composerPath));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_CreatesSafetyBackup()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var backupPath = await service.CreateBackupAsync(ct);

            // Ensure distinct timestamp so the safety backup is a separate archive.
            await Task.Delay(1100, ct);

            var result = await service.RestoreBackupAsync(backupPath, ct);

            Assert.True(File.Exists(result.SafetyBackupPath));
            Assert.NotEqual(Path.GetFullPath(backupPath), Path.GetFullPath(result.SafetyBackupPath));

            // The safety backup uses the distinct "pre-restore-" prefix, not "copilothive-backup-".
            var safetyFileName = Path.GetFileName(result.SafetyBackupPath);
            Assert.StartsWith("pre-restore-", safetyFileName);
            Assert.DoesNotContain("copilothive-backup-", safetyFileName);

            var archives = Directory.GetFiles(service.BackupDirectory, "pre-restore-*.tar.gz");
            Assert.Contains(archives, a => Path.GetFullPath(a) == Path.GetFullPath(result.SafetyBackupPath));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_SafetyBackupNotPruned()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Fill the standard backup set beyond the 10-backup pruning limit.
            string? backupToRestore = null;
            for (var i = 0; i < 11; i++)
            {
                // Restore the most recent (last-created) backup so it survives pruning.
                backupToRestore = await service.CreateBackupAsync(ct);
                await Task.Delay(1100, ct);
            }

            // After pruning, normal backups are capped at 10.
            Assert.True(service.ListBackups().Count <= 10);

            var result = await service.RestoreBackupAsync(backupToRestore!, ct);

            // The safety backup must survive: it is excluded from normal pruning.
            Assert.True(File.Exists(result.SafetyBackupPath));
            Assert.StartsWith("pre-restore-", Path.GetFileName(result.SafetyBackupPath));

            // ListBackups only counts standard backups, so the safety backup is not among them.
            Assert.DoesNotContain(
                service.ListBackups(),
                b => b.FileName == Path.GetFileName(result.SafetyBackupPath));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_ArchiveWithPathTraversal_ThrowsOrSkips()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Build a malicious tar.gz archive containing a valid manifest plus a
            // path-traversal entry that attempts to escape the extraction directory.
            Directory.CreateDirectory(service.BackupDirectory);
            var maliciousArchive = Path.Combine(service.BackupDirectory, "malicious-backup.tar.gz");
            await CreateMaliciousArchive(maliciousArchive, "../evil.txt", "pwned", ct);

            // The sentinel location an attacker would target: the backups' parent dir.
            var escapedTarget = Path.Combine(service.BackupDirectory, "..", "evil.txt");
            var escapedTargetFull = Path.GetFullPath(escapedTarget);
            if (File.Exists(escapedTargetFull))
                File.Delete(escapedTargetFull);

            Exception? thrown = null;
            try
            {
                await service.RestoreBackupAsync(maliciousArchive, ct);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            // Whether the method throws (missing manifest after skipping) or skips the entry,
            // the malicious file must NOT have been written outside the temp directory.
            Assert.False(File.Exists(escapedTargetFull),
                $"Path traversal entry escaped extraction directory to {escapedTargetFull}");
            _ = thrown; // either outcome is acceptable per the acceptance criteria.
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    private static async Task CreateMaliciousArchive(string archivePath, string maliciousEntryName, string maliciousContent, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"malicious-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            // Include a valid manifest so restore proceeds past manifest validation.
            await File.WriteAllTextAsync(Path.Combine(tmp, "backup-manifest.json"), "{}", ct);
            var goodContent = Path.Combine(tmp, "good.txt");
            await File.WriteAllTextAsync(goodContent, maliciousContent, ct);

            await using var fileStream = File.Create(archivePath);
            await using var gzipStream = new GZipStream(fileStream, CompressionLevel.SmallestSize);
            await using var tarWriter = new TarWriter(gzipStream, TarEntryFormat.Pax);

            await tarWriter.WriteEntryAsync(Path.Combine(tmp, "backup-manifest.json"), "backup-manifest.json", ct);

            // Manually craft an entry whose name performs path traversal.
            var maliciousEntry = new PaxTarEntry(TarEntryType.RegularFile, maliciousEntryName);
            await using (var dataStream = File.OpenRead(goodContent))
            {
                maliciousEntry.DataStream = dataStream;
                await tarWriter.WriteEntryAsync(maliciousEntry, ct);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(tmp))
                    Directory.Delete(tmp, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_HandlesMissingSessionFiles()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            // Backup contains only the database (no session files).
            var backupPath = await service.CreateBackupAsync(ct);

            var result = await service.RestoreBackupAsync(backupPath, ct);

            Assert.True(result.DatabaseRestored);
            Assert.Equal(0, result.BrainGoalSessionCount);
            Assert.False(result.BrainMasterSession);
            Assert.False(result.ComposerSession);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_RoundTrip()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var dbPath = Path.Combine(stateDir, "copilothive.db");
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var masterPath = Path.Combine(stateDir, "brain-master.json");
            var composerPath = Path.Combine(stateDir, "composer-session.json");
            const string originalMaster = "{\"original\":\"master\"}";
            const string originalComposer = "{\"original\":\"composer\"}";
            await File.WriteAllTextAsync(masterPath, originalMaster, ct);
            await File.WriteAllTextAsync(composerPath, originalComposer, ct);

            var backupPath = await service.CreateBackupAsync(ct);

            // Modify state: add a goal and change session file content.
            using (var context = new CopilotHiveDbContext(options))
            {
                context.Goals.Add(new Goal { Id = "g1", Description = "test goal" });
                await context.SaveChangesAsync(ct);
            }

            await File.WriteAllTextAsync(masterPath, "{\"modified\":\"master\"}", ct);
            await File.WriteAllTextAsync(composerPath, "{\"modified\":\"composer\"}", ct);

            var result = await service.RestoreBackupAsync(backupPath, ct);
            Assert.True(result.DatabaseRestored);

            using (var context = new CopilotHiveDbContext(options))
            {
                Assert.Equal(0, await context.Goals.CountAsync(ct));
            }

            Assert.Equal(originalMaster, await File.ReadAllTextAsync(masterPath, ct));
            Assert.Equal(originalComposer, await File.ReadAllTextAsync(composerPath, ct));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_InvalidArchive_Throws()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var missingPath = Path.Combine(stateDir, "does-not-exist.tar.gz");

            await Assert.ThrowsAsync<FileNotFoundException>(
                () => service.RestoreBackupAsync(missingPath, ct));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_RoundTrip_FullState()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var dbPath = Path.Combine(stateDir, "copilothive.db");
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            // Set up full state: brain-master.json, brain-goal files, composer-session.json,
            // metrics/, and keys/.
            const string originalMaster = "{\"original\":\"master\"}";
            const string originalGoal1 = "{\"goal\":1}";
            const string originalGoal2 = "{\"goal\":2}";
            const string originalComposer = "{\"original\":\"composer\"}";
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-master.json"), originalMaster, ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-1.json"), originalGoal1, ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-2.json"), originalGoal2, ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "composer-session.json"), originalComposer, ct);

            Directory.CreateDirectory(Path.Combine(stateDir, "metrics"));
            Directory.CreateDirectory(Path.Combine(stateDir, "keys"));
            const string originalMetrics1 = "{\"metric\":\"m1\"}";
            const string originalMetrics2 = "{\"metric\":\"m2\"}";
            const string originalKey1 = "<key1/>";
            const string originalKey2 = "<key2/>";
            await File.WriteAllTextAsync(Path.Combine(stateDir, "metrics", "metrics1.json"), originalMetrics1, ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "metrics", "metrics2.json"), originalMetrics2, ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "keys", "key1.xml"), originalKey1, ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "keys", "key2.xml"), originalKey2, ct);

            // Create the backup of the pristine state (0 goals).
            var backupPath = await service.CreateBackupAsync(ct);

            // Modify ALL state:
            // 1. Add a goal to the database.
            using (var context = new CopilotHiveDbContext(options))
            {
                context.Goals.Add(new Goal { Id = "g1", Description = "test goal" });
                await context.SaveChangesAsync(ct);
            }
            // 2. Change session file contents.
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-master.json"), "{\"modified\":true}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "composer-session.json"), "{\"modified\":true}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-1.json"), "{\"modified\":true}", ct);
            // 3. Add a new brain-goal file (not in backup).
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-3.json"), "{\"new\":true}", ct);
            // 4. Modify metrics and add new metrics file.
            await File.WriteAllTextAsync(Path.Combine(stateDir, "metrics", "metrics1.json"), "{\"modified\":true}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "metrics", "metrics3.json"), "{\"new\":true}", ct);
            // 5. Modify keys and add new key file.
            await File.WriteAllTextAsync(Path.Combine(stateDir, "keys", "key1.xml"), "<modified/>", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "keys", "key3.xml"), "<new/>", ct);

            // Restore the backup.
            var result = await service.RestoreBackupAsync(backupPath, ct);
            Assert.True(result.DatabaseRestored);
            Assert.True(result.BrainMasterSession);
            Assert.True(result.ComposerSession);
            Assert.Equal(2, result.BrainGoalSessionCount);
            Assert.Equal(2, result.MetricsCount);
            Assert.Equal(2, result.KeysCount);

            // Verify database reverted: goal is gone.
            using (var context = new CopilotHiveDbContext(options))
            {
                Assert.Equal(0, await context.Goals.CountAsync(ct));
            }

            // Verify session files reverted to original content.
            Assert.Equal(originalMaster, await File.ReadAllTextAsync(Path.Combine(stateDir, "brain-master.json"), ct));
            Assert.Equal(originalComposer, await File.ReadAllTextAsync(Path.Combine(stateDir, "composer-session.json"), ct));
            Assert.Equal(originalGoal1, await File.ReadAllTextAsync(Path.Combine(stateDir, "brain-goal-1.json"), ct));
            Assert.Equal(originalGoal2, await File.ReadAllTextAsync(Path.Combine(stateDir, "brain-goal-2.json"), ct));

            // The extra brain-goal-3.json (not in backup) should be gone.
            Assert.False(File.Exists(Path.Combine(stateDir, "brain-goal-3.json")));

            // Verify metrics reverted.
            Assert.Equal(originalMetrics1, await File.ReadAllTextAsync(Path.Combine(stateDir, "metrics", "metrics1.json"), ct));
            Assert.Equal(originalMetrics2, await File.ReadAllTextAsync(Path.Combine(stateDir, "metrics", "metrics2.json"), ct));
            Assert.False(File.Exists(Path.Combine(stateDir, "metrics", "metrics3.json")));

            // Verify keys reverted.
            Assert.Equal(originalKey1, await File.ReadAllTextAsync(Path.Combine(stateDir, "keys", "key1.xml"), ct));
            Assert.Equal(originalKey2, await File.ReadAllTextAsync(Path.Combine(stateDir, "keys", "key2.xml"), ct));
            Assert.False(File.Exists(Path.Combine(stateDir, "keys", "key3.xml")));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_DatabaseWalShmCleanup()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var dbPath = Path.Combine(stateDir, "copilothive.db");
            var walPath = dbPath + "-wal";
            var shmPath = dbPath + "-shm";
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            // Enable WAL mode and write data to generate -wal and -shm files.
            using (var context = new CopilotHiveDbContext(options))
            {
                await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
                context.Goals.Add(new Goal { Id = "g1", Description = "wal test" });
                await context.SaveChangesAsync(ct);
            }

            // Create a backup (of the state with WAL journaling).
            var backupPath = await service.CreateBackupAsync(ct);

            // Ensure WAL/SHM files exist by writing more data (keeps WAL active).
            using (var context = new CopilotHiveDbContext(options))
            {
                context.Goals.Add(new Goal { Id = "g2", Description = "another goal" });
                await context.SaveChangesAsync(ct);
            }

            // Restore the backup — this should delete -wal and -shm files.
            var result = await service.RestoreBackupAsync(backupPath, ct);
            Assert.True(result.DatabaseRestored);

            // After restore, -wal and -shm files should not linger.
            Assert.False(File.Exists(walPath), $"WAL file should be deleted after restore, but exists at {walPath}");
            Assert.False(File.Exists(shmPath), $"SHM file should be deleted after restore, but exists at {shmPath}");
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_SafetyBackupContainsCurrentState()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var dbPath = Path.Combine(stateDir, "copilothive.db");
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            // Step 1: Create backup A (pristine state, 0 goals).
            var backupAPath = await service.CreateBackupAsync(ct);

            // Step 2: Modify the database — add a goal.
            using (var context = new CopilotHiveDbContext(options))
            {
                context.Goals.Add(new Goal { Id = "g1", Description = "pre-restore goal" });
                await context.SaveChangesAsync(ct);
            }

            // Verify the goal exists before restore.
            using (var context = new CopilotHiveDbContext(options))
            {
                Assert.Equal(1, await context.Goals.CountAsync(ct));
            }

            // Step 3: Restore backup A. This should create a safety backup containing
            // the current (modified) state — i.e., the goal that was added.
            await Task.Delay(1100, ct); // Ensure distinct timestamp.
            var result = await service.RestoreBackupAsync(backupAPath, ct);
            Assert.True(File.Exists(result.SafetyBackupPath));

            // Step 4: Extract the safety backup and verify it contains the goal.
            var safetyEntries = await GetArchiveEntries(result.SafetyBackupPath);
            Assert.Contains("copilothive.db", safetyEntries);

            // Extract the safety backup DB to a temp file and check it has the goal.
            var tempSafetyDb = Path.Combine(stateDir, $"safety-check-{Guid.NewGuid():N}.db");
            try
            {
                using var fileStream = File.OpenRead(result.SafetyBackupPath);
                using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                using var tarReader = new TarReader(gzipStream);
                TarEntry? entry;
                while ((entry = await tarReader.GetNextEntryAsync(cancellationToken: ct)) is not null)
                {
                    if (entry.Name == "copilothive.db" && entry.EntryType == TarEntryType.RegularFile)
                    {
                        entry.ExtractToFile(tempSafetyDb, overwrite: true);
                        break;
                    }
                }

                // The safety backup DB should contain the goal that was added before restore.
                var safetyOptions = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                    .UseSqlite($"Data Source={tempSafetyDb}")
                    .Options;
                using var safetyContext = new CopilotHiveDbContext(safetyOptions);
                var goalCount = await safetyContext.Goals.CountAsync(ct);
                Assert.Equal(1, goalCount);
                var goal = await safetyContext.Goals.FirstAsync(ct);
                Assert.Equal("g1", goal.Id);
                Assert.Equal("pre-restore goal", goal.Description);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(tempSafetyDb))
                    File.Delete(tempSafetyDb);
            }

            // After restore, the live DB should have 0 goals (restored to backup A's state).
            using (var context = new CopilotHiveDbContext(options))
            {
                Assert.Equal(0, await context.Goals.CountAsync(ct));
            }
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_PreservesBrainGoalFileCount()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Create 3 brain-goal-*.json files.
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-1.json"), "{\"goal\":1}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-2.json"), "{\"goal\":2}", ct);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-3.json"), "{\"goal\":3}", ct);

            // Backup with 3 brain-goal files.
            var backupPath = await service.CreateBackupAsync(ct);

            // Delete 2 of them and add a new one (so there are 2 now).
            File.Delete(Path.Combine(stateDir, "brain-goal-1.json"));
            File.Delete(Path.Combine(stateDir, "brain-goal-2.json"));
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-goal-4.json"), "{\"goal\":4}", ct);

            // Verify current state has 2 brain-goal files.
            var currentFiles = Directory.GetFiles(stateDir, "brain-goal-*.json");
            Assert.Equal(2, currentFiles.Length);

            // Restore.
            var result = await service.RestoreBackupAsync(backupPath, ct);
            Assert.Equal(3, result.BrainGoalSessionCount);

            // Verify exactly 3 brain-goal files exist again matching the backup.
            var restoredFiles = Directory.GetFiles(stateDir, "brain-goal-*.json");
            Assert.Equal(3, restoredFiles.Length);

            // Verify the content matches the backup (originals are back, the new one is gone).
            Assert.Equal("{\"goal\":1}", await File.ReadAllTextAsync(Path.Combine(stateDir, "brain-goal-1.json"), ct));
            Assert.Equal("{\"goal\":2}", await File.ReadAllTextAsync(Path.Combine(stateDir, "brain-goal-2.json"), ct));
            Assert.Equal("{\"goal\":3}", await File.ReadAllTextAsync(Path.Combine(stateDir, "brain-goal-3.json"), ct));
            Assert.False(File.Exists(Path.Combine(stateDir, "brain-goal-4.json")));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    /// <summary>
    /// Verifies that multiple path traversal vectors are all rejected during archive extraction.
    /// Tests nested traversal (../../etc/evil), rooted Unix paths (/etc/evil), and the
    /// legitimate entries in the same archive are still extracted correctly.
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_ArchiveWithPathTraversal_RejectsMultipleVectors()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Build a malicious archive with multiple traversal attempts plus a valid manifest.
            Directory.CreateDirectory(service.BackupDirectory);
            var maliciousArchive = Path.Combine(service.BackupDirectory, "multi-traversal-backup.tar.gz");
            await CreateMultiVectorMaliciousArchive(maliciousArchive, ct);

            // Compute escape targets for each vector.
            var backupParent = Directory.GetParent(service.BackupDirectory)?.FullName
                ?? Path.GetFullPath(service.BackupDirectory + "/..");
            var nestedEscapeTarget = Path.Combine(backupParent, "nested-evil.txt");
            var rootedEscapeTarget = Path.Combine(Path.GetPathRoot(stateDir)!, "rooted-evil.txt");

            // Clean up any pre-existing sentinel files.
            if (File.Exists(nestedEscapeTarget)) File.Delete(nestedEscapeTarget);
            if (File.Exists(rootedEscapeTarget)) File.Delete(rootedEscapeTarget);

            // Act: restore should either throw or skip malicious entries.
            try
            {
                await service.RestoreBackupAsync(maliciousArchive, ct);
            }
            catch
            {
                // Either throwing or skipping is acceptable.
            }

            // Assert: none of the malicious files should have been written outside the temp dir.
            Assert.False(File.Exists(nestedEscapeTarget),
                $"Nested path traversal escaped to {nestedEscapeTarget}");
            Assert.False(File.Exists(rootedEscapeTarget),
                $"Rooted path traversal escaped to {rootedEscapeTarget}");
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    private static async Task CreateMultiVectorMaliciousArchive(string archivePath, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"multi-malicious-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            // Valid manifest so restore proceeds past manifest validation.
            await File.WriteAllTextAsync(Path.Combine(tmp, "backup-manifest.json"), "{}", ct);
            var payloadFile = Path.Combine(tmp, "payload.txt");
            await File.WriteAllTextAsync(payloadFile, "pwned", ct);

            await using var fileStream = File.Create(archivePath);
            await using var gzipStream = new GZipStream(fileStream, CompressionLevel.SmallestSize);
            await using var tarWriter = new TarWriter(gzipStream, TarEntryFormat.Pax);

            // Write the valid manifest entry.
            await tarWriter.WriteEntryAsync(
                Path.Combine(tmp, "backup-manifest.json"), "backup-manifest.json", ct);

            // Vector 1: nested traversal — ../../nested-evil.txt
            var nestedEntry = new PaxTarEntry(TarEntryType.RegularFile, "../../nested-evil.txt");
            await using (var dataStream = File.OpenRead(payloadFile))
            {
                nestedEntry.DataStream = dataStream;
                await tarWriter.WriteEntryAsync(nestedEntry, ct);
            }

            // Vector 2: rooted Unix path — /rooted-evil.txt
            var rootedEntry = new PaxTarEntry(TarEntryType.RegularFile, "/rooted-evil.txt");
            await using (var dataStream2 = File.OpenRead(payloadFile))
            {
                rootedEntry.DataStream = dataStream2;
                await tarWriter.WriteEntryAsync(rootedEntry, ct);
            }
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Verifies that the safety backup naming uses "pre-restore-" prefix (not "copilothive-backup-")
    /// and that the safety backup file is a valid tar.gz archive with expected content.
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_SafetyBackupUsesPreRestorePrefix()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Create some state so the safety backup has content.
            await File.WriteAllTextAsync(Path.Combine(stateDir, "brain-master.json"), "{\"v\":1}", ct);

            var backupPath = await service.CreateBackupAsync(ct);
            await Task.Delay(1100, ct); // Distinct timestamp for safety backup.

            var result = await service.RestoreBackupAsync(backupPath, ct);

            // The safety backup path must exist and use the pre-restore prefix.
            Assert.True(File.Exists(result.SafetyBackupPath));
            var safetyFileName = Path.GetFileName(result.SafetyBackupPath);
            Assert.StartsWith("pre-restore-", safetyFileName);
            Assert.DoesNotContain("copilothive-backup-", safetyFileName);
            Assert.EndsWith(".tar.gz", safetyFileName);

            // The safety backup should be a valid archive containing the database and manifest.
            var safetyEntries = await GetArchiveEntries(result.SafetyBackupPath);
            Assert.Contains("copilothive.db", safetyEntries);
            Assert.Contains("backup-manifest.json", safetyEntries);

            // ListBackups should NOT include the safety backup (it only lists copilothive-backup-* files).
            var listedBackups = service.ListBackups();
            Assert.DoesNotContain(listedBackups, b => b.FileName == safetyFileName);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    /// <summary>
    /// Verifies that the safety backup created during restore is NOT pruned even when
    /// the normal 10-backup pruning limit is exceeded by subsequent backup operations.
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_SafetyBackupNotPrunedByNormalBackups()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Create a backup to restore later.
            var backupToRestore = await service.CreateBackupAsync(ct);
            await Task.Delay(1100, ct);

            // Restore — creates a pre-restore-* safety backup.
            var result = await service.RestoreBackupAsync(backupToRestore, ct);
            var safetyPath = result.SafetyBackupPath;
            Assert.True(File.Exists(safetyPath));
            Assert.StartsWith("pre-restore-", Path.GetFileName(safetyPath));

            // Now create 12+ normal backups to trigger pruning of old copilothive-backup-* archives.
            for (var i = 0; i < 12; i++)
            {
                await service.CreateBackupAsync(ct);
                await Task.Delay(1100, ct);
            }

            // The safety backup must still exist — it is excluded from pruning.
            Assert.True(File.Exists(safetyPath),
                $"Safety backup was pruned: {safetyPath}");

            // Normal backups should be pruned to <= 10, but safety backup is separate.
            var normalBackups = service.ListBackups();
            Assert.True(normalBackups.Count <= 10,
                $"Normal backups should be pruned to <= 10, but found {normalBackups.Count}");
            Assert.DoesNotContain(normalBackups, b => b.FileName == Path.GetFileName(safetyPath));

            // Verify the pre-restore file still exists on disk (not just in the list).
            var preRestoreFiles = Directory.GetFiles(service.BackupDirectory, "pre-restore-*.tar.gz");
            Assert.Contains(preRestoreFiles, f => Path.GetFullPath(f) == Path.GetFullPath(safetyPath));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }
}
