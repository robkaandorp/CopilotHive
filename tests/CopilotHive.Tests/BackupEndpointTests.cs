using System.Net;
using System.Text.Json;

using CopilotHive.Persistence;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Wire-contract tests for the backup REST API (<c>GET /api/backup</c> and
/// <c>POST /api/backup</c>) after they were migrated onto <c>IBackupFacade</c>. They pin the
/// external contract that must NOT change: 200 with the <c>BackupInfo</c> shape
/// (<c>fileName</c>, <c>sizeBytes</c>, <c>createdAt</c>) for both list entries and create.
/// </summary>
/// <remarks>
/// Derived from the shared <see cref="HiveTestFactory"/> via <c>WithWebHostBuilder</c> so the
/// process-wide <c>STATE_DIR</c> of the <c>HiveIntegration</c> collection fixture is never
/// clobbered. The app's <see cref="BackupService"/> registration is replaced with one rooted at
/// a per-test temporary directory, so real archives are written somewhere disposable and the
/// shared fixture's state directory stays clean.
/// </remarks>
[Collection("HiveIntegration")]
public sealed class BackupEndpointTests
{
    private readonly HiveTestFactory _baseFactory;

    /// <summary>Receives the shared <see cref="HiveTestFactory"/> fixture for this collection.</summary>
    /// <param name="factory">The shared test factory.</param>
    public BackupEndpointTests(HiveTestFactory factory)
    {
        _baseFactory = factory;
    }

    private WebApplicationFactory<Program> CreateFactory(string backupStateDir)
    {
        return _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(BackupService))
                    .ToList();
                foreach (var d in descriptors)
                    services.Remove(d);

                services.AddSingleton(sp => new BackupService(
                    backupStateDir,
                    sp.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>(),
                    sp.GetRequiredService<ILogger<BackupService>>()));
            });
        });
    }

    private static string NewStateDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"backup-endpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream,
            cancellationToken: TestContext.Current.CancellationToken);
        return doc.RootElement.Clone();
    }

    /// <summary>Asserts that an element carries exactly the <c>BackupInfo</c> wire shape.</summary>
    private static void AssertBackupInfoShape(JsonElement element)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);

        Assert.Equal(
            new[] { "fileName", "sizeBytes", "createdAt" },
            element.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.EndsWith(".tar.gz", element.GetProperty("fileName").GetString());
        Assert.True(element.GetProperty("sizeBytes").GetInt64() > 0);
        Assert.True(element.GetProperty("createdAt").TryGetDateTime(out _));
    }

    // ── GET /api/backup ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetBackups_NoBackups_Returns200WithEmptyArray()
    {
        var stateDir = NewStateDir();
        try
        {
            using var factory = CreateFactory(stateDir);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/api/backup", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await ReadJsonAsync(response);
            Assert.Equal(JsonValueKind.Array, json.ValueKind);
            Assert.Empty(json.EnumerateArray());
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task GetBackups_AfterCreate_Returns200WithBackupInfoArrayShape()
    {
        var stateDir = NewStateDir();
        try
        {
            using var factory = CreateFactory(stateDir);
            using var client = factory.CreateClient();

            var createResponse = await client.PostAsync("/api/backup", content: null,
                TestContext.Current.CancellationToken);
            var created = await ReadJsonAsync(createResponse);
            var createdFileName = created.GetProperty("fileName").GetString();

            var response = await client.GetAsync("/api/backup", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await ReadJsonAsync(response);
            Assert.Equal(JsonValueKind.Array, json.ValueKind);
            var entry = Assert.Single(json.EnumerateArray().ToArray());
            AssertBackupInfoShape(entry);
            Assert.Equal(createdFileName, entry.GetProperty("fileName").GetString());
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    // ── POST /api/backup ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateBackup_Returns200WithBackupInfoShape()
    {
        var stateDir = NewStateDir();
        try
        {
            using var factory = CreateFactory(stateDir);
            using var client = factory.CreateClient();

            var response = await client.PostAsync("/api/backup", content: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await ReadJsonAsync(response);
            AssertBackupInfoShape(json);

            // The reported archive really exists on disk under the backup directory.
            var fileName = json.GetProperty("fileName").GetString()!;
            var archivePath = Path.Combine(stateDir, "backups", fileName);
            Assert.True(File.Exists(archivePath));
            Assert.Equal(new FileInfo(archivePath).Length, json.GetProperty("sizeBytes").GetInt64());
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    [Fact]
    public async Task CreateBackup_ReturnsTheSameShapeAsAListEntry()
    {
        var stateDir = NewStateDir();
        try
        {
            using var factory = CreateFactory(stateDir);
            using var client = factory.CreateClient();

            var createResponse = await client.PostAsync("/api/backup", content: null,
                TestContext.Current.CancellationToken);
            var created = await ReadJsonAsync(createResponse);

            var listResponse = await client.GetAsync("/api/backup", TestContext.Current.CancellationToken);
            var listed = Assert.Single((await ReadJsonAsync(listResponse)).EnumerateArray().ToArray());

            Assert.Equal(
                listed.EnumerateObject().Select(p => p.Name).ToArray(),
                created.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(listed.GetProperty("fileName").GetString(), created.GetProperty("fileName").GetString());
            Assert.Equal(listed.GetProperty("sizeBytes").GetInt64(), created.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(
                listed.GetProperty("createdAt").GetDateTime(),
                created.GetProperty("createdAt").GetDateTime());
        }
        finally
        {
            Cleanup(stateDir);
        }
    }
}
