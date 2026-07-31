using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    // ── Sub-agent models endpoints ───────────────────────────────────────────

    [Fact]
    public async Task PostSubAgentModel_Success_Returns200()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "sa-model", contextWindow = 128000, reasoningEffort = "high", description = "Fast" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostSubAgentModel_Duplicate_Returns409()
    {
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "sa-dup", contextWindow = (int?)null, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        var response = await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "SA-DUP", contextWindow = (int?)null, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PutSubAgentModel_Success_Returns200()
    {
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "sa-edit", contextWindow = 1000, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        var response = await _client.PutAsJsonAsync(
            "/api/config/sub-agent-models/sa-edit",
            new { name = "sa-edit", contextWindow = 2000, reasoningEffort = "low", description = "Updated" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PutSubAgentModel_NotFound_Returns404()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/config/sub-agent-models/sa-missing",
            new { name = "sa-missing", contextWindow = (int?)null, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteSubAgentModel_Success_Returns200()
    {
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "sa-delete", contextWindow = (int?)null, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        var response = await _client.DeleteAsync(
            "/api/config/sub-agent-models/sa-delete",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeleteSubAgentModel_NotFound_Returns404()
    {
        var response = await _client.DeleteAsync(
            "/api/config/sub-agent-models/sa-no-such",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── GET /api/config/models — response includes subAgentModels + description ──

    [Fact]
    public async Task GetModels_ResponseIncludesSubAgentModelsField()
    {
        // Add a sub-agent model so the response carries a non-empty subAgentModels array
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "sa-get-test", contextWindow = (int?)128000, reasoningEffort = (string?)"medium", description = "Fast model for quick tasks" },
            TestContext.Current.CancellationToken);

        var getResponse = await _client.GetAsync("/api/config/models", TestContext.Current.CancellationToken);
        getResponse.EnsureSuccessStatusCode();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await getResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        // The response must include the subAgentModels element
        Assert.True(doc.RootElement.TryGetProperty("subAgentModels", out var subAgentModels),
            "Expected 'subAgentModels' field in GET /api/config/models response");
        var subEntry = Assert.Single(subAgentModels.EnumerateArray());
        Assert.Equal("sa-get-test", subEntry.GetProperty("name").GetString());
        Assert.Equal("Fast model for quick tasks", subEntry.GetProperty("description").GetString());
    }

    [Fact]
    public async Task GetModels_AvailableModelsEntriesIncludeDescription()
    {
        // Add an available model with a description
        await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "desc-model", contextWindow = (int?)64000, reasoningEffort = (string?)"low", description = "Economical batch worker" },
            TestContext.Current.CancellationToken);

        var getResponse = await _client.GetAsync("/api/config/models", TestContext.Current.CancellationToken);
        getResponse.EnsureSuccessStatusCode();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await getResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        var availableModels = doc.RootElement.GetProperty("availableModels");
        var found = false;
        foreach (var entry in availableModels.EnumerateArray())
        {
            if (entry.GetProperty("name").GetString() == "desc-model")
            {
                Assert.True(entry.TryGetProperty("description", out var desc),
                    "Expected 'description' field on availableModels entry");
                Assert.Equal("Economical batch worker", desc.GetString());
                found = true;
                break;
            }
        }
        Assert.True(found, "Expected a model with Name='desc-model' in availableModels");
    }

    // ── Sub-agent endpoints: verify actual persisted effects, not just status codes ──

    /// <summary>GETs /api/config/models and returns the sub_agent_models array (may be null).</summary>
    private async Task<JsonElement?> GetSubAgentModelsAsync()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(
            "/api/config/models", TestContext.Current.CancellationToken);
        if (!doc.TryGetProperty("subAgentModels", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        return arr;
    }

    private async Task<JsonElement?> FindSubAgentModelAsync(string name)
    {
        var arr = await GetSubAgentModelsAsync();
        if (arr is null)
            return null;
        foreach (var e in arr.Value.EnumerateArray())
        {
            if (e.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    [Fact]
    public async Task PutSubAgentModel_ActuallyUpdatesPersistedValues()
    {
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "effect-model", contextWindow = 1000, reasoningEffort = (string?)null, description = "before" },
            TestContext.Current.CancellationToken);

        var response = await _client.PutAsJsonAsync(
            "/api/config/sub-agent-models/effect-model",
            new { name = "effect-model", contextWindow = 250000, reasoningEffort = "high", description = "after" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindSubAgentModelAsync("effect-model");
        Assert.NotNull(entry);
        Assert.Equal(250000, entry!.Value.GetProperty("contextWindow").GetInt32());
        Assert.Equal("high", entry.Value.GetProperty("reasoningEffort").GetString());
        Assert.Equal("after", entry.Value.GetProperty("description").GetString());
    }

    [Fact]
    public async Task DeleteSubAgentModel_ActuallyRemovesEntry()
    {
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "gone-model", contextWindow = (int?)null, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.NotNull(await FindSubAgentModelAsync("gone-model"));

        var response = await _client.DeleteAsync(
            "/api/config/sub-agent-models/gone-model", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Null(await FindSubAgentModelAsync("gone-model"));
    }

    [Fact]
    public async Task PutSubAgentModel_UrlEncodedSlash_UnescapesRouteNameAndUpdates()
    {
        const string name = "copilot/claude-sonnet-4.6";
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name, contextWindow = 1000, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        var response = await _client.PutAsJsonAsync(
            $"/api/config/sub-agent-models/{Uri.EscapeDataString(name)}",
            new { name, contextWindow = 400000, reasoningEffort = "medium", description = "encoded update" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindSubAgentModelAsync(name);
        Assert.NotNull(entry);
        Assert.Equal(400000, entry!.Value.GetProperty("contextWindow").GetInt32());
        Assert.Equal("medium", entry.Value.GetProperty("reasoningEffort").GetString());
        Assert.Equal("encoded update", entry.Value.GetProperty("description").GetString());
    }

    [Fact]
    public async Task DeleteSubAgentModel_UrlEncodedSlash_UnescapesRouteNameAndRemoves()
    {
        const string name = "copilot/gemini-3.5-flash";
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name, contextWindow = (int?)null, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.NotNull(await FindSubAgentModelAsync(name));

        var response = await _client.DeleteAsync(
            $"/api/config/sub-agent-models/{Uri.EscapeDataString(name)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await FindSubAgentModelAsync(name));
    }

    [Fact]
    public async Task PostSubAgentModel_StripsReasoningSuffixFromName()
    {
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "suffix-model:high", contextWindow = (int?)null, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        var entry = await FindSubAgentModelAsync("suffix-model");
        Assert.NotNull(entry);
        Assert.Equal("high", entry!.Value.GetProperty("reasoningEffort").GetString());
        Assert.Null(await FindSubAgentModelAsync("suffix-model:high"));
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

    // ── SupportsVision tri-state REST round-trip (available models) ──────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task PostAvailableModel_PersistsSupportsVisionTriState(bool? vision)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "vision-am-model", contextWindow = 1000, reasoningEffort = (string?)null, supportsVision = vision },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindAvailableModelAsync("vision-am-model");
        Assert.NotNull(entry);
        if (vision is null)
        {
            // Null may be omitted or present as null in JSON
            Assert.True(!entry!.Value.TryGetProperty("supportsVision", out var sv) || sv.ValueKind == JsonValueKind.Null,
                "supportsVision should be null/absent when unset");
        }
        else
        {
            Assert.Equal(vision, entry!.Value.GetProperty("supportsVision").GetBoolean());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task PutAvailableModel_PersistsSupportsVisionTriState(bool? vision)
    {
        // Add first
        await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "put-vision-model", contextWindow = 1000, reasoningEffort = (string?)null },
            TestContext.Current.CancellationToken);

        // Update with SupportsVision
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/put-vision-model",
            new { name = "put-vision-model", contextWindow = 2000, reasoningEffort = (string?)null, supportsVision = vision },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindAvailableModelAsync("put-vision-model");
        Assert.NotNull(entry);
        if (vision is null)
        {
            Assert.True(!entry!.Value.TryGetProperty("supportsVision", out var sv) || sv.ValueKind == JsonValueKind.Null,
                "supportsVision should be null/absent when unset");
        }
        else
        {
            Assert.Equal(vision, entry!.Value.GetProperty("supportsVision").GetBoolean());
        }
    }

    [Fact]
    public async Task PutAvailableModel_ExplicitFalseSurvivesRoundTrip()
    {
        // Add with true first
        await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "false-survive", contextWindow = 1000, reasoningEffort = (string?)null, supportsVision = true },
            TestContext.Current.CancellationToken);

        // Update to explicit false
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/false-survive",
            new { name = "false-survive", contextWindow = 2000, reasoningEffort = (string?)null, supportsVision = false },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindAvailableModelAsync("false-survive");
        Assert.NotNull(entry);
        Assert.False(entry!.Value.GetProperty("supportsVision").GetBoolean());
    }

    // ── SupportsVision tri-state REST round-trip (sub-agent models) ──────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task PostSubAgentModel_PersistsSupportsVisionTriState(bool? vision)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "vision-sa-model", contextWindow = 1000, reasoningEffort = (string?)null, description = (string?)null, supportsVision = vision },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindSubAgentModelAsync("vision-sa-model");
        Assert.NotNull(entry);
        if (vision is null)
        {
            Assert.True(!entry!.Value.TryGetProperty("supportsVision", out var sv) || sv.ValueKind == JsonValueKind.Null,
                "supportsVision should be null/absent when unset");
        }
        else
        {
            Assert.Equal(vision, entry!.Value.GetProperty("supportsVision").GetBoolean());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task PutSubAgentModel_PersistsSupportsVisionTriState(bool? vision)
    {
        // Add first
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "put-sa-vision", contextWindow = 1000, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        // Update with SupportsVision
        var response = await _client.PutAsJsonAsync(
            "/api/config/sub-agent-models/put-sa-vision",
            new { name = "put-sa-vision", contextWindow = 2000, reasoningEffort = (string?)null, description = (string?)null, supportsVision = vision },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindSubAgentModelAsync("put-sa-vision");
        Assert.NotNull(entry);
        if (vision is null)
        {
            Assert.True(!entry!.Value.TryGetProperty("supportsVision", out var sv) || sv.ValueKind == JsonValueKind.Null,
                "supportsVision should be null/absent when unset");
        }
        else
        {
            Assert.Equal(vision, entry!.Value.GetProperty("supportsVision").GetBoolean());
        }
    }

    [Fact]
    public async Task PutSubAgentModel_ExplicitFalseSurvivesRoundTrip()
    {
        // Add with true first
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "sa-false-survive", contextWindow = 1000, reasoningEffort = (string?)null, description = (string?)null, supportsVision = true },
            TestContext.Current.CancellationToken);

        // Update to explicit false
        var response = await _client.PutAsJsonAsync(
            "/api/config/sub-agent-models/sa-false-survive",
            new { name = "sa-false-survive", contextWindow = 2000, reasoningEffort = (string?)null, description = (string?)null, supportsVision = false },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindSubAgentModelAsync("sa-false-survive");
        Assert.NotNull(entry);
        Assert.False(entry!.Value.GetProperty("supportsVision").GetBoolean());
    }

    /// <summary>GETs /api/config/models and finds an available model by name (case-insensitive).</summary>
    private async Task<JsonElement?> FindAvailableModelAsync(string name)
    {
        var getResponse = await _client.GetAsync("/api/config/models", TestContext.Current.CancellationToken);
        getResponse.EnsureSuccessStatusCode();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await getResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);
        if (!doc.RootElement.TryGetProperty("availableModels", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                return e.Clone();
        }
        return null;
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
    private readonly string? _previousStateDir;
    private readonly HiveConfigFile _config;
    private readonly FakeConfigRepoManager _repo;

    public CustomEndpointFactory(string tempDir)
    {
        _tempDir = tempDir;
        _stateDir = Path.Combine(tempDir, "state");
        _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
        Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
        _config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        _repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
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