using System.Net;
using System.Net.Http.Json;
using CopilotHive.Configuration;
using CopilotHive.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the available-models REST endpoints that exercise the actual
/// service implementations (not the service-null path). Uses a custom
/// <see cref="WebApplicationFactory{TEntryPoint}"/> that registers
/// <see cref="ConfigModelService"/> and <see cref="ModelDiscoveryService"/> with a
/// <see cref="FakeConfigRepoManager"/> so the endpoints can perform real CRUD operations.
/// </summary>
[Collection("HiveIntegration")]
public class AvailableModelsEndpointTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CustomEndpointFactory _factory;
    private readonly HttpClient _client;

    public AvailableModelsEndpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-ep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _factory = new CustomEndpointFactory(_tempDir);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── POST /api/config/available-models — 200 success ──────────────────────

    [Fact]
    public async Task PostAvailableModel_Success_Returns200()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "copilot/test-model", contextWindow = 128000, reasoningEffort = "high" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── POST /api/config/available-models — 409 duplicate ─────────────────────

    [Fact]
    public async Task PostAvailableModel_Duplicate_Returns409()
    {
        // First add succeeds
        await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "dup-model", contextWindow = (int?)null, reasoningEffort = (string?)null },
            TestContext.Current.CancellationToken);

        // Second add of same name (case-insensitive) should return 409 Conflict
        var response = await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "DUP-MODEL", contextWindow = (int?)null, reasoningEffort = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── PUT /api/config/available-models/{name} — 200 success ─────────────────

    [Fact]
    public async Task PutAvailableModel_Success_Returns200()
    {
        // Add a model first
        await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "edit-model", contextWindow = 100000, reasoningEffort = "low" },
            TestContext.Current.CancellationToken);

        // Update it
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/edit-model",
            new { name = "edit-model", contextWindow = 200000, reasoningEffort = "high" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── PUT /api/config/available-models/{name} — 404 not found ───────────────

    [Fact]
    public async Task PutAvailableModel_NotFound_Returns404()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/missing-model",
            new { name = "missing-model", contextWindow = (int?)null, reasoningEffort = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── DELETE /api/config/available-models/{name} — 200 success ──────────────

    [Fact]
    public async Task DeleteAvailableModel_Success_Returns200()
    {
        // Add a model first
        await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "delete-model", contextWindow = (int?)null, reasoningEffort = (string?)null },
            TestContext.Current.CancellationToken);

        // Delete it
        var response = await _client.DeleteAsync(
            "/api/config/available-models/delete-model",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── DELETE /api/config/available-models/{name} — 404 not found ────────────

    [Fact]
    public async Task DeleteAvailableModel_NotFound_Returns404()
    {
        var response = await _client.DeleteAsync(
            "/api/config/available-models/no-such-model",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── GET /api/config/models/discover — 200 success (empty when no tokens) ──

    [Fact]
    public async Task GetDiscover_Returns200()
    {
        var response = await _client.GetAsync(
            "/api/config/models/discover",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── PUT /api/config/available-models/{name} — URL-encoded slash ──────────

    [Fact]
    public async Task PutAvailableModel_UrlEncodedSlash_Returns200()
    {
        // Add a model with a slash in the name first
        await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "copilot/gemini-3.5-flash", contextWindow = 100000, reasoningEffort = (string?)null },
            TestContext.Current.CancellationToken);

        // PUT with URL-encoded slash (%2F) — the endpoint must decode it back to "/"
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/copilot%2Fgemini-3.5-flash",
            new { name = "copilot/gemini-3.5-flash", contextWindow = 200000, reasoningEffort = "high" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── DELETE /api/config/available-models/{name} — URL-encoded slash ────────

    [Fact]
    public async Task DeleteAvailableModel_UrlEncodedSlash_Returns200()
    {
        // Add a model with a slash in the name first
        await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "copilot/ollama-model", contextWindow = (int?)null, reasoningEffort = (string?)null },
            TestContext.Current.CancellationToken);

        // DELETE with URL-encoded slash (%2F) — the endpoint must decode it back to "/"
        var response = await _client.DeleteAsync(
            "/api/config/available-models/copilot%2Follama-model",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── POST /api/config/available-models — suffix stripped via endpoint ─────

    [Fact]
    public async Task PostAvailableModel_WithSuffix_StripsAndStoresReasoningEffort()
    {
        // POST a model whose name carries a known reasoning suffix
        var postResponse = await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "test-model:high", contextWindow = 128000, reasoningEffort = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);

        // GET /api/config/models and verify the suffix was stripped
        var getResponse = await _client.GetAsync("/api/config/models", TestContext.Current.CancellationToken);
        getResponse.EnsureSuccessStatusCode();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await getResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        var availableModels = doc.RootElement.GetProperty("availableModels");
        var found = false;
        foreach (var entry in availableModels.EnumerateArray())
        {
            var entryName = entry.GetProperty("name").GetString();
            if (entryName == "test-model")
            {
                Assert.Equal("high", entry.GetProperty("reasoningEffort").GetString());
                found = true;
                break;
            }
        }
        Assert.True(found, "Expected a model with Name='test-model' and ReasoningEffort='high' in availableModels");
    }
}

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that registers
/// <see cref="ConfigModelService"/> and <see cref="ModelDiscoveryService"/>
/// with a <see cref="FakeConfigRepoManager"/> and a fresh <see cref="HiveConfigFile"/>.
/// This allows the available-models endpoints to execute real CRUD operations
/// instead of returning the service-null 500 response.
/// </summary>
internal sealed class CustomEndpointFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir;
    private readonly string _stateDir;
    private readonly HiveConfigFile _config;
    private readonly FakeConfigRepoManager _repo;

    public CustomEndpointFactory(string tempDir)
    {
        _tempDir = tempDir;
        _stateDir = Path.Combine(tempDir, "state");
        Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
        _config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        _repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Register the config file, repo, and services so the endpoints resolve them
            services.AddSingleton(_config);
            services.AddSingleton<ConfigRepoManager>(_repo);
            services.AddSingleton<ConfigModelService>();
            services.AddSingleton<ModelDiscoveryService>();
        });
    }
}