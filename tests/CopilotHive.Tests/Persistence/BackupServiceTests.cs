using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

using CopilotHive.Persistence;

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
}
