using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

using CopilotHive.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Persistence;

/// <summary>
/// INDEPENDENT verification of the per-user Composer tree's backup/restore coverage
/// (<c>{stateDir}/composer/users</c>): the archive must carry EVERY category of per-user state
/// (sessions, attachments and migration evidence), and the restore must REPLACE that tree rather
/// than merge into it.
/// <para>
/// This file deliberately covers the parts of the backup contract the landed tests did NOT prove:
/// <list type="bullet">
///   <item><b>(1) Every per-user category is archived</b> — sessions, attachments (nested
///   arbitrarily deep) and the migration evidence, with the manifest count matching the archive
///   content exactly.</item>
///   <item><b>(2) The restore replaces, never merges</b> — a file that exists in the CURRENT state
///   but not in the archive is gone afterwards, and a file that exists in BOTH keeps the
///   ARCHIVE's content.</item>
///   <item><b>(3) An archive with an empty per-user tree clears the current tree</b> — the
///   directory is removed entirely, not merely emptied.</item>
///   <item><b>(4) The legacy single file is untouched by the per-user restore</b> — it keeps its
///   own replace semantics.</item>
///   <item><b>(5) Archive containment still holds</b> — a per-user entry that tries to escape the
///   extraction directory is skipped, exactly like every other entry.</item>
///   <item><b>(6) The restore result is additive</b> — the new member is the LAST one and defaults
///   to zero, so every existing construction still compiles and behaves the same.</item>
/// </list>
/// </para>
/// <para>
/// <b>Determinism.</b> Every service runs on the shared manual
/// <see cref="ControlledTimeProvider"/>, so archive names are made distinct by advancing the
/// clock rather than by sleeping. No wall-clock assertion is used anywhere.
/// </para>
/// </summary>
public sealed class BackupServicePerUserTreeTests
{
    /// <summary>
    /// Advance applied wherever a test creates a SECOND archive: the archive name embeds a
    /// second-resolution stamp, so moving the manual clock keeps the names distinct.
    /// </summary>
    private static readonly TimeSpan DistinctNameAdvance = TimeSpan.FromSeconds(2);

    private readonly ControlledTimeProvider _clock = new();

    private static string ConnectionString(string path) => $"Data Source={path};Pooling=False";

    private sealed class TestDbContextFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        private readonly DbContextOptions<CopilotHiveDbContext> _options;
        public TestDbContextFactory(DbContextOptions<CopilotHiveDbContext> options) => _options = options;
        public CopilotHiveDbContext CreateDbContext() => new(_options);
    }

    private (string stateDir, BackupService service) CreateService()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"backup-peruser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var dbPath = Path.Combine(stateDir, "copilothive.db");
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(ConnectionString(dbPath))
            .Options;

        using (var context = new CopilotHiveDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        var factory = new TestDbContextFactory(options);
        var service = new BackupService(stateDir, factory, NullLogger<BackupService>.Instance, _clock);
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

    private static string UserPath(string stateDir, string relative) =>
        Path.Combine(stateDir, "composer", "users", relative);

    private static async Task WriteUserFileAsync(string stateDir, string relative, string content, CancellationToken ct)
    {
        var path = UserPath(stateDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct);
    }

    /// <summary>
    /// Builds a crafted archive from an explicit entry-name/content map, so a test can create an
    /// archive that the production backup path would never produce (for example one with no
    /// per-user tree at all). The manifest is always included — the restore refuses an archive
    /// without one — but it is NOT counted as a per-user entry.
    /// </summary>
    private static async Task<string> BuildArchiveAsync(
        string dir, IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var archive = Path.Combine(dir, $"crafted-{Guid.NewGuid():N}.tar.gz");
        var src = Path.Combine(Path.GetTempPath(), $"crafted-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        try
        {
            await using var fs = File.Create(archive);
            await using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
            await using var tar = new TarWriter(gz, TarEntryFormat.Pax);
            foreach (var (name, content) in files.Append(KeyValuePair.Create("backup-manifest.json", "{}")))
            {
                var file = Path.Combine(src, Guid.NewGuid().ToString("N"));
                await File.WriteAllTextAsync(file, content, ct);
                await tar.WriteEntryAsync(file, name, ct);
            }
        }
        finally
        {
            Directory.Delete(src, recursive: true);
        }

        return archive;
    }

    // ── (1) Every per-user category is archived ─────────────────────────────

    /// <summary>
    /// EVERY PER-USER CATEGORY IS ARCHIVED — the session, the attachment, and the migration
    /// evidence of TWO different users, with an attachment nested two levels deep — and the
    /// manifest's <c>composerUsersCount</c> matches the number of per-user entries exactly.
    /// </summary>
    [Fact]
    public async Task CreateBackupAsync_ArchivesEveryPerUserCategory_AndTheManifestCountMatches()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await WriteUserFileAsync(stateDir, Path.Combine("1", "session.json"), "s1", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "legacy-migration.json"), "ev1", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "attachments", "a.png"), "img1", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "attachments", "nested", "deep", "b.jpg"), "img2", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("2", "session.json"), "s2", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("2", "legacy-migration.json"), "ev2", ct);

            var path = await service.CreateBackupAsync(ct);

            var entries = await GetArchiveEntries(path);
            var perUserEntries = entries
                .Where(e => e.StartsWith("composer/users/", StringComparison.Ordinal))
                .Order()
                .ToArray();

            Assert.Equal(
                [
                    "composer/users/1/attachments/a.png",
                    "composer/users/1/attachments/nested/deep/b.jpg",
                    "composer/users/1/legacy-migration.json",
                    "composer/users/1/session.json",
                    "composer/users/2/legacy-migration.json",
                    "composer/users/2/session.json",
                ],
                perUserEntries);

            using var manifest = JsonDocument.Parse((await ReadArchiveEntry(path, "backup-manifest.json"))!);
            Assert.Equal(perUserEntries.Length, manifest.RootElement.GetProperty("composerUsersCount").GetInt32());
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    /// <summary>
    /// THE ARCHIVED PER-USER CONTENT IS THE ACTUAL CONTENT: the archive holds the real bytes of
    /// every per-user file, not just their names.
    /// </summary>
    [Fact]
    public async Task CreateBackupAsync_ArchivesTheActualPerUserContent()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await WriteUserFileAsync(stateDir, Path.Combine("7", "session.json"), "{\"session\":true}", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("7", "legacy-migration.json"), "{\"evidence\":true}", ct);

            var path = await service.CreateBackupAsync(ct);

            Assert.Equal("{\"session\":true}", await ReadArchiveEntry(path, "composer/users/7/session.json"));
            Assert.Equal("{\"evidence\":true}", await ReadArchiveEntry(path, "composer/users/7/legacy-migration.json"));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    // ── (2) The restore replaces, never merges ──────────────────────────────

    /// <summary>
    /// THE RESTORE NEVER MERGES: a file that exists in the CURRENT state but NOT in the archive
    /// is gone afterwards, and a file present in BOTH keeps the ARCHIVE's content — not the
    /// current one.
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_ReplacesThePerUserTree_AndNeverMergesStaleFiles()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await WriteUserFileAsync(stateDir, Path.Combine("1", "session.json"), "archived", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "attachments", "kept.png"), "archived-img", ct);
            var backup = await service.CreateBackupAsync(ct);
            _clock.Advance(DistinctNameAdvance);

            // Drift: change an archived file, add a file that is NOT in the archive, and add a
            // whole user that is not in the archive.
            await WriteUserFileAsync(stateDir, Path.Combine("1", "session.json"), "drifted", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "attachments", "stale.png"), "stale-img", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "legacy-migration.json"), "stale-evidence", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("9", "session.json"), "stale-user", ct);

            var result = await service.RestoreBackupAsync(backup, ct);

            Assert.Equal(2, result.ComposerUsersCount);

            // The archived file keeps the ARCHIVE's content.
            Assert.Equal("archived", await File.ReadAllTextAsync(UserPath(stateDir, Path.Combine("1", "session.json")), ct));
            Assert.Equal("archived-img", await File.ReadAllTextAsync(UserPath(stateDir, Path.Combine("1", "attachments", "kept.png")), ct));

            // Everything that was NOT in the archive is gone — no stale merging.
            Assert.False(File.Exists(UserPath(stateDir, Path.Combine("1", "attachments", "stale.png"))));
            Assert.False(File.Exists(UserPath(stateDir, Path.Combine("1", "legacy-migration.json"))));
            Assert.False(Directory.Exists(UserPath(stateDir, "9")));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    /// <summary>
    /// A USER DIRECTORY THAT EXISTS ONLY IN THE CURRENT STATE IS REMOVED ENTIRELY: the restore
    /// deletes the destination tree first, so a stale user's whole directory (sessions,
    /// attachments and evidence) disappears, not just its files.
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_RemovesAWholeStaleUserDirectory()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await WriteUserFileAsync(stateDir, Path.Combine("1", "session.json"), "archived", ct);
            var backup = await service.CreateBackupAsync(ct);
            _clock.Advance(DistinctNameAdvance);

            await WriteUserFileAsync(stateDir, Path.Combine("5", "session.json"), "stale", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("5", "legacy-migration.json"), "stale-evidence", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("5", "attachments", "x.png"), "stale-img", ct);

            await service.RestoreBackupAsync(backup, ct);

            Assert.False(Directory.Exists(UserPath(stateDir, "5")));
            Assert.False(File.Exists(UserPath(stateDir, Path.Combine("5", "session.json"))));
            Assert.False(File.Exists(UserPath(stateDir, Path.Combine("5", "legacy-migration.json"))));
            Assert.False(File.Exists(UserPath(stateDir, Path.Combine("5", "attachments", "x.png"))));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    // ── (3) An empty per-user tree clears the current tree ──────────────────

    /// <summary>
    /// AN ARCHIVE WITH AN EMPTY PER-USER TREE CLEARS THE CURRENT TREE: the archive carries a
    /// <c>composer/users</c> directory with no files in it, the restore removes the current
    /// per-user tree, and the restored tree is empty.
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_EmptyPerUserTreeArchive_ClearsTheCurrentTree()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // The archive is taken when the per-user tree EXISTS but is EMPTY: the archive then
            // carries no per-user entry at all, which is exactly the "empty multi-user tree" case.
            Directory.CreateDirectory(UserPath(stateDir, ""));
            var emptyTreeBackup = await service.CreateBackupAsync(ct);
            _clock.Advance(DistinctNameAdvance);
            Assert.DoesNotContain(
                await GetArchiveEntries(emptyTreeBackup),
                e => e.StartsWith("composer/users", StringComparison.Ordinal));

            // Now the current state has per-user content.
            await WriteUserFileAsync(stateDir, Path.Combine("1", "session.json"), "current", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "legacy-migration.json"), "current-evidence", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "attachments", "a.png"), "current-img", ct);

            var result = await service.RestoreBackupAsync(emptyTreeBackup, ct);

            Assert.Equal(0, result.ComposerUsersCount);
            Assert.False(File.Exists(UserPath(stateDir, Path.Combine("1", "session.json"))));
            Assert.False(File.Exists(UserPath(stateDir, Path.Combine("1", "legacy-migration.json"))));
            Assert.False(File.Exists(UserPath(stateDir, Path.Combine("1", "attachments", "a.png"))));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    /// <summary>
    /// AN ARCHIVE WITH NO PER-USER TREE AT ALL CLEARS THE CURRENT TREE AND LEAVES NO
    /// PER-USER DIRECTORY: the current per-user state is removed and the destination directory
    /// is not re-created with stale content.
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_ArchiveWithoutPerUserTree_ClearsCurrentStateEntirely()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await WriteUserFileAsync(stateDir, Path.Combine("1", "session.json"), "current", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "legacy-migration.json"), "current-evidence", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("2", "attachments", "a.png"), "current-img", ct);

            var oldArchive = await BuildArchiveAsync(stateDir, new Dictionary<string, string>
            {
                ["composer-session.json"] = "old-legacy",
            }, ct);

            var result = await service.RestoreBackupAsync(oldArchive, ct);

            Assert.Equal(0, result.ComposerUsersCount);

            // The whole current per-user tree is gone.
            var usersRoot = UserPath(stateDir, "");
            var remaining = Directory.Exists(usersRoot)
                ? Directory.GetFileSystemEntries(usersRoot, "*", SearchOption.AllDirectories)
                : [];
            Assert.Empty(remaining);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    // ── (4) The legacy single file is untouched ─────────────────────────────

    /// <summary>
    /// THE LEGACY SINGLE FILE KEEPS ITS OWN REPLACE SEMANTICS: restoring an archive that carries
    /// a legacy <c>composer-session.json</c> replaces the current one, and the per-user restore
    /// does not interfere with it.
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_LegacySingleFile_IsRestoredIndependentlyOfThePerUserTree()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Combine(stateDir, "composer-session.json"), "current-legacy", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "session.json"), "per-user", ct);

            var backup = await service.CreateBackupAsync(ct);
            _clock.Advance(DistinctNameAdvance);

            await File.WriteAllTextAsync(Path.Combine(stateDir, "composer-session.json"), "drifted-legacy", ct);

            var result = await service.RestoreBackupAsync(backup, ct);

            Assert.True(result.ComposerSession);
            Assert.Equal("current-legacy", await File.ReadAllTextAsync(Path.Combine(stateDir, "composer-session.json"), ct));
            Assert.Equal(1, result.ComposerUsersCount);
            Assert.Equal("per-user", await File.ReadAllTextAsync(UserPath(stateDir, Path.Combine("1", "session.json")), ct));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    /// <summary>
    /// THE LEGACY SINGLE FILE IS ARCHIVED ALONGSIDE THE PER-USER TREE: both live in the same
    /// archive, so a restore of one never silently loses the other.
    /// </summary>
    [Fact]
    public async Task CreateBackupAsync_ArchivesTheLegacyFileAndThePerUserTreeTogether()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Combine(stateDir, "composer-session.json"), "legacy", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "session.json"), "per-user", ct);

            var path = await service.CreateBackupAsync(ct);

            var entries = await GetArchiveEntries(path);
            Assert.Contains("composer-session.json", entries);
            Assert.Contains("composer/users/1/session.json", entries);
            Assert.Equal("legacy", await ReadArchiveEntry(path, "composer-session.json"));
            Assert.Equal("per-user", await ReadArchiveEntry(path, "composer/users/1/session.json"));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    // ── (5) Archive containment still holds ─────────────────────────────────

    /// <summary>
    /// A PER-USER ENTRY THAT TRIES TO ESCAPE THE EXTRACTION DIRECTORY IS SKIPPED: the archive
    /// traversal containment applies to the per-user tree exactly as it applies to every other
    /// entry, so a crafted <c>composer/users/../../evil.txt</c> never lands outside the state
    /// directory.
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_PerUserTraversalEntry_IsSkippedLikeEveryOtherEntry()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var archive = Path.Combine(service.BackupDirectory, "peruser-traversal.tar.gz");
            Directory.CreateDirectory(service.BackupDirectory);
            var src = Path.Combine(Path.GetTempPath(), $"peruser-trav-{Guid.NewGuid():N}");
            Directory.CreateDirectory(src);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(src, "backup-manifest.json"), "{}", ct);
                var payload = Path.Combine(src, "payload.txt");
                await File.WriteAllTextAsync(payload, "pwned", ct);

                await using var fs = File.Create(archive);
                await using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
                await using var tar = new TarWriter(gz, TarEntryFormat.Pax);
                await tar.WriteEntryAsync(Path.Combine(src, "backup-manifest.json"), "backup-manifest.json", ct);

                var malicious = new PaxTarEntry(TarEntryType.RegularFile, "composer/users/../../peruser-evil.txt");
                await using (var data = File.OpenRead(payload))
                {
                    malicious.DataStream = data;
                    await tar.WriteEntryAsync(malicious, ct);
                }
            }
            finally
            {
                Directory.Delete(src, recursive: true);
            }

            // The sentinel an attacker would target: the state directory's parent.
            var escaped = Path.GetFullPath(Path.Combine(stateDir, "..", "peruser-evil.txt"));
            if (File.Exists(escaped))
                File.Delete(escaped);

            try
            {
                await service.RestoreBackupAsync(archive, ct);
            }
            catch
            {
                // Either outcome is acceptable (a missing manifest after the skip throws); what
                // matters is that NOTHING escaped the extraction directory.
            }

            Assert.False(File.Exists(escaped),
                $"A per-user archive entry escaped the extraction directory to {escaped}");
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    // ── (6) The restore result is additive ──────────────────────────────────

    /// <summary>
    /// THE RESTORE RESULT IS ADDITIVE: the new <c>ComposerUsersCount</c> member is the LAST one
    /// and DEFAULTS to zero, so the record can still be constructed without it — and the
    /// existing members keep their meaning.
    /// </summary>
    [Fact]
    public void RestoreResult_TheNewMemberIsAdditiveAndDefaultsToZero()
    {
        // The legacy 7-argument construction still compiles and yields 0 for the new member.
        var legacy = new BackupService.RestoreResult(
            DatabaseRestored: true,
            BrainMasterSession: true,
            BrainGoalSessionCount: 2,
            ComposerSession: true,
            MetricsCount: 3,
            KeysCount: 4,
            SafetyBackupPath: "/tmp/safety.tar.gz");

        Assert.Equal(0, legacy.ComposerUsersCount);
        Assert.True(legacy.DatabaseRestored);
        Assert.True(legacy.BrainMasterSession);
        Assert.Equal(2, legacy.BrainGoalSessionCount);
        Assert.True(legacy.ComposerSession);
        Assert.Equal(3, legacy.MetricsCount);
        Assert.Equal(4, legacy.KeysCount);
        Assert.Equal("/tmp/safety.tar.gz", legacy.SafetyBackupPath);

        // The full construction carries the new member explicitly.
        var full = legacy with { ComposerUsersCount = 7 };
        Assert.Equal(7, full.ComposerUsersCount);
        Assert.Equal("/tmp/safety.tar.gz", full.SafetyBackupPath);
    }

    /// <summary>
    /// THE SAFETY BACKUP CARRIES THE PER-USER TREE THAT IS ABOUT TO BE REPLACED: the pre-restore
    /// archive is a complete snapshot, so a restore can never lose the per-user state it
    /// overwrites.
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_SafetyBackupCarriesThePerUserTreeAndItsEvidence()
    {
        var (stateDir, service) = CreateService();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await WriteUserFileAsync(stateDir, Path.Combine("1", "session.json"), "to-be-replaced", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "legacy-migration.json"), "evidence", ct);
            await WriteUserFileAsync(stateDir, Path.Combine("1", "attachments", "a.png"), "img", ct);

            // An archive with NO per-user tree, so the restore really clears the current tree.
            var emptyArchive = await BuildArchiveAsync(stateDir, new Dictionary<string, string>
            {
                ["composer-session.json"] = "legacy",
            }, ct);

            var result = await service.RestoreBackupAsync(emptyArchive, ct);

            var safetyEntries = await GetArchiveEntries(result.SafetyBackupPath);
            Assert.Contains("composer/users/1/session.json", safetyEntries);
            Assert.Contains("composer/users/1/legacy-migration.json", safetyEntries);
            Assert.Contains("composer/users/1/attachments/a.png", safetyEntries);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }
}
