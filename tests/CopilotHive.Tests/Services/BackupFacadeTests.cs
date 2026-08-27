using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="BackupFacade"/> — the facade the backup endpoints (and, in a later
/// round, the Configuration component) use instead of touching <see cref="BackupService"/>
/// directly.
/// </summary>
/// <remarks>
/// The facade is exercised over a REAL <see cref="BackupService"/> pointed at an isolated
/// temporary state directory (no mocks), so the create path really produces a tar.gz archive.
/// Every temp directory is removed in a <c>finally</c>.
/// <para>
/// The load-bearing contract these tests pin down: the facade catches NOTHING. A failing
/// backup throws out of <see cref="IBackupFacade.CreateBackupAsync"/> — it is NOT converted
/// into a failed <see cref="FacadeResult{T}"/> — so ASP.NET still turns it into a 500 exactly
/// as the pre-facade handler did.
/// </para>
/// </remarks>
public sealed class BackupFacadeTests
{
    private sealed class TestDbContextFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        private readonly DbContextOptions<CopilotHiveDbContext> _options;
        public TestDbContextFactory(DbContextOptions<CopilotHiveDbContext> options) => _options = options;
        public CopilotHiveDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Creates an isolated state directory with a real SQLite database, a real
    /// <see cref="BackupService"/> over it, and the facade under test.
    /// </summary>
    private static (string stateDir, BackupService service, IBackupFacade facade) CreateFacade()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"backup-facade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var dbPath = Path.Combine(stateDir, "copilothive.db");
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        using (var context = new CopilotHiveDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        var service = new BackupService(stateDir, new TestDbContextFactory(options), NullLogger<BackupService>.Instance);
        var facade = new BackupFacade(service, NullLogger<BackupFacade>.Instance);
        return (stateDir, service, facade);
    }

    private static void Cleanup(string stateDir)
    {
        try
        {
            if (Directory.Exists(stateDir))
                Directory.Delete(stateDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── GetBackups ───────────────────────────────────────────────────────────

    [Fact]
    public void GetBackups_NoBackupsExist_ReturnsSuccessWithEmptyList()
    {
        var (stateDir, _, facade) = CreateFacade();
        try
        {
            var result = facade.GetBackups();

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.Null(result.Error);
            Assert.NotNull(result.Value);
            Assert.Empty(result.Value);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task GetBackups_AfterCreate_ReturnsTheCreatedArchiveMetadata()
    {
        var (stateDir, service, facade) = CreateFacade();
        try
        {
            var path = await service.CreateBackupAsync(TestContext.Current.CancellationToken);
            var expectedFileName = Path.GetFileName(path);

            var result = facade.GetBackups();

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            var entry = Assert.Single(result.Value!);
            Assert.Equal(expectedFileName, entry.FileName);
            Assert.Equal(new FileInfo(path).Length, entry.SizeBytes);
            Assert.Equal(File.GetCreationTimeUtc(path), entry.CreatedAt);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task GetBackups_MultipleBackups_ProjectsEveryEntryFromTheService()
    {
        var (stateDir, service, facade) = CreateFacade();
        try
        {
            // Two real archive files; the facade must project the service's list verbatim
            // (same entries, same order) rather than re-sorting or filtering.
            await service.CreateBackupAsync(TestContext.Current.CancellationToken);
            var second = Path.Combine(service.BackupDirectory, "copilothive-backup-29991231T235959.tar.gz");
            await File.WriteAllBytesAsync(second, [1, 2, 3], TestContext.Current.CancellationToken);

            var result = facade.GetBackups();

            Assert.True(result.Success);
            Assert.Equal(2, result.Value!.Count);
            Assert.Contains(result.Value, b => b.FileName == Path.GetFileName(second) && b.SizeBytes == 3);
            Assert.Equal(
                service.ListBackups().Select(b => b.FileName).ToArray(),
                result.Value.Select(b => b.FileName).ToArray());
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    // ── CreateBackupAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateBackupAsync_Success_ReturnsMetadataOfTheNewArchive()
    {
        var (stateDir, service, facade) = CreateFacade();
        try
        {
            var result = await facade.CreateBackupAsync(TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.Null(result.Error);
            Assert.NotNull(result.Value);

            var archivePath = Path.Combine(service.BackupDirectory, result.Value.FileName);
            Assert.True(File.Exists(archivePath));
            Assert.EndsWith(".tar.gz", result.Value.FileName);
            Assert.Equal(new FileInfo(archivePath).Length, result.Value.SizeBytes);
            Assert.Equal(File.GetCreationTimeUtc(archivePath), result.Value.CreatedAt);

            // The returned entry is the one the list operation reports.
            var listed = Assert.Single(facade.GetBackups().Value!);
            Assert.Equal(listed, result.Value);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_ServiceFails_ThrowsInsteadOfReturningFailedResult()
    {
        var (stateDir, service, facade) = CreateFacade();
        try
        {
            // Make the backup directory impossible to create: a FILE already occupies its path,
            // so BackupService.CreateBackupAsync throws from Directory.CreateDirectory.
            await File.WriteAllTextAsync(service.BackupDirectory, "not a directory",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<IOException>(
                () => facade.CreateBackupAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_CancelledToken_PropagatesOperationCanceledExceptionAndCreatesNothing()
    {
        var (stateDir, service, facade) = CreateFacade();
        try
        {
            // Proves the facade forwards its OWN token to BackupService: had it called
            // CreateBackupAsync() with the default token, the backup would have succeeded.
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => facade.CreateBackupAsync(cts.Token));

            Assert.Empty(service.ListBackups());
        }
        finally
        {
            Cleanup(stateDir);
        }
    }
}
