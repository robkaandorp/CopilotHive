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
            new { name = "copilot/test-model", contextWindow = 128000 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── POST /api/config/available-models — reasoningEffort ignored ──────────

    [Fact]
    public async Task PostAvailableModel_SendsReasoningEffortInBody_IsIgnored()
    {
        // AvailableModelRequest carries no ReasoningEffort property: a stray field in the
        // request body must be ignored — no 400, and nothing is persisted for the model.
        var response = await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "ignored-effort-model", contextWindow = 1000, reasoningEffort = "high" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindAvailableModelAsync("ignored-effort-model");
        Assert.NotNull(entry);
        Assert.False(entry!.Value.TryGetProperty("reasoningEffort", out _),
            "availableModels entries must not expose 'reasoningEffort'");
    }

    [Fact]
    public async Task PutAvailableModel_SendsReasoningEffortInBody_IsIgnored()
    {
        // Add a model first (no reasoning).
        await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "put-ignored-effort", contextWindow = 1000 },
            TestContext.Current.CancellationToken);

        // PUT with a stray reasoningEffort field: must be ignored, not stored or rejected.
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/put-ignored-effort",
            new { name = "put-ignored-effort", contextWindow = 2000, reasoningEffort = "extra_high" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindAvailableModelAsync("put-ignored-effort");
        Assert.NotNull(entry);
        Assert.Equal(2000, entry!.Value.GetProperty("contextWindow").GetInt32());
        Assert.False(entry.Value.TryGetProperty("reasoningEffort", out _),
            "availableModels entries must not expose 'reasoningEffort'");
    }

    // ── POST /api/config/available-models — 409 duplicate ─────────────────────

    [Fact]
    public async Task PostAvailableModel_Duplicate_Returns409()
    {
        // First add succeeds
        await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "dup-model", contextWindow = (int?)null },
            TestContext.Current.CancellationToken);

        // Second add of same name (case-insensitive) should return 409 Conflict
        var response = await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "DUP-MODEL", contextWindow = (int?)null },
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
            new { name = "edit-model", contextWindow = 100000 },
            TestContext.Current.CancellationToken);

        // Update it
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/edit-model",
            new { name = "edit-model", contextWindow = 200000 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── PUT /api/config/available-models/{name} — 404 not found ───────────────

    [Fact]
    public async Task PutAvailableModel_NotFound_Returns404()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/missing-model",
            new { name = "missing-model", contextWindow = (int?)null },
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
            new { name = "delete-model", contextWindow = (int?)null },
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

    /// <summary>
    /// An unconfigured orchestrator/composer model is JSON null on the wire (Slice 3a:
    /// the API preserves null for "unset" — never a default/fallback string).
    /// </summary>
    [Fact]
    public async Task GetModels_UnconfiguredModels_AreJsonNull()
    {
        _factory.Config.Orchestrator.Model = null;
        _factory.Config.Composer = null;

        var response = await _client.GetAsync("/api/config/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("orchestrator").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("composer").ValueKind);
    }

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

    // ── GET /api/config/models — subAgentModels[].reasoningEffort enum projection ──

    /// <summary>
    /// Reads the <c>reasoningEffort</c> element of the named <c>subAgentModels</c> entry from
    /// GET <c>/api/config/models</c>. Returns the raw <see cref="JsonElement"/> so callers can
    /// assert on <see cref="JsonValueKind"/> (null vs string) as well as the value.
    /// </summary>
    private async Task<JsonElement> GetSubAgentReasoningElementAsync(string name)
    {
        var entry = await FindSubAgentModelAsync(name);
        Assert.NotNull(entry);
        Assert.True(entry!.Value.TryGetProperty("reasoningEffort", out var reasoning),
            $"Expected 'reasoningEffort' on the subAgentModels entry '{name}'");
        return reasoning;
    }

    /// <summary>
    /// A stored reasoning effort is projected through <c>ConfigModelService.ParseLenient</c> into
    /// the <c>ReasoningEffort</c> enum, so the global converter renders it snake_case. This is the
    /// entry-level counterpart of the <c>subAgentModelReasoning</c> dictionary: both must agree.
    /// <para>
    /// Non-canonical stored spellings are included deliberately: <c>ParseLenient</c> normalizes
    /// them, so echoing the raw <c>ModelEntry</c> string would return the stored casing verbatim
    /// and fail here. A canonical-only theory would pass with or without the projection.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("none", "none")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("extra_high", "extra_high")]
    [InlineData("High", "high")]
    [InlineData("  Medium  ", "medium")]
    [InlineData("EXTRA_HIGH", "extra_high")]
    public async Task GetModels_SubAgentModelsReasoningEffort_IsSnakeCaseEnum(string stored, string expected)
    {
        _factory.Config.Models!.SubAgentModels =
            [new ModelEntry { Name = "sa-enum-model", ReasoningEffort = stored }];

        var reasoning = await GetSubAgentReasoningElementAsync("sa-enum-model");

        Assert.Equal(JsonValueKind.String, reasoning.ValueKind);
        Assert.Equal(expected, reasoning.GetString());
    }

    /// <summary>
    /// The multi-word level must serialize as <c>extra_high</c> — never the C# name
    /// <c>ExtraHigh</c> — which is what proves the value went through the enum projection and the
    /// global snake_case converter rather than being echoed back as the raw YAML string. The
    /// stored spelling is deliberately non-canonical so only the projection can produce the
    /// expected output.
    /// </summary>
    [Fact]
    public async Task GetModels_SubAgentModelsExtraHigh_UsesSnakeCaseNeverPascalCase()
    {
        _factory.Config.Models!.SubAgentModels =
            [new ModelEntry { Name = "sa-extra-high", ReasoningEffort = "Extra_High" }];

        var reasoning = await GetSubAgentReasoningElementAsync("sa-extra-high");

        Assert.Equal("extra_high", reasoning.GetString());
        Assert.NotEqual("ExtraHigh", reasoning.GetString());
    }

    /// <summary>
    /// An unset stored value stays null in the response rather than becoming an empty string.
    /// </summary>
    [Fact]
    public async Task GetModels_SubAgentModelsNullReasoningEffort_IsNull()
    {
        _factory.Config.Models!.SubAgentModels =
            [new ModelEntry { Name = "sa-null-reasoning", ReasoningEffort = null }];

        var reasoning = await GetSubAgentReasoningElementAsync("sa-null-reasoning");

        Assert.Equal(JsonValueKind.Null, reasoning.ValueKind);
    }

    /// <summary>
    /// A stored value the write endpoints would reject — left behind by a hand-edited config or
    /// an older schema — degrades to null via <c>ParseLenient</c>. Regression guard: returning
    /// the raw <c>ModelEntry</c> leaked <c>"turbo"</c> verbatim here while the sibling
    /// <c>subAgentModelReasoning</c> dictionary reported null, an internally inconsistent response.
    /// </summary>
    [Theory]
    [InlineData("turbo")]
    [InlineData("HIGHEST")]
    [InlineData("very-high")]
    [InlineData("1")]
    public async Task GetModels_SubAgentModelsInvalidStoredReasoning_DegradesToNull(string stored)
    {
        _factory.Config.Models!.SubAgentModels =
            [new ModelEntry { Name = "sa-invalid-reasoning", ReasoningEffort = stored }];

        var reasoning = await GetSubAgentReasoningElementAsync("sa-invalid-reasoning");

        Assert.Equal(JsonValueKind.Null, reasoning.ValueKind);
        Assert.NotEqual(stored, reasoning.GetString());
    }

    /// <summary>
    /// The entry-level projection and the <c>subAgentModelReasoning</c> dictionary are two views of
    /// the same stored value, so they must never disagree — including for an invalid stored value,
    /// where the raw-entity bug made exactly these two fields contradict each other.
    /// </summary>
    [Theory]
    [InlineData("extra_high")]
    [InlineData("none")]
    [InlineData(null)]
    [InlineData("turbo")]
    [InlineData("High")]
    public async Task GetModels_SubAgentEntryAndDictionaryReasoning_Agree(string? stored)
    {
        _factory.Config.Models!.SubAgentModels =
            [new ModelEntry { Name = "sa-agree-model", ReasoningEffort = stored }];

        var response = await _client.GetAsync("/api/config/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        var entryReasoning = doc.RootElement.GetProperty("subAgentModels")
            .EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "sa-agree-model")
            .GetProperty("reasoningEffort");

        var dictionaryReasoning = doc.RootElement.GetProperty("subAgentModelReasoning")
            .GetProperty("sa-agree-model");

        Assert.Equal(dictionaryReasoning.ValueKind, entryReasoning.ValueKind);
        Assert.Equal(dictionaryReasoning.GetString(), entryReasoning.GetString());
    }

    /// <summary>
    /// The projection must preserve every other entry field it replaced the raw entity for.
    /// </summary>
    [Fact]
    public async Task GetModels_SubAgentModelsProjection_PreservesAllOtherFields()
    {
        _factory.Config.Models!.SubAgentModels =
        [
            new ModelEntry
            {
                Name = "sa-full-model",
                ContextWindow = 128000,
                ReasoningEffort = "high",
                Description = "Research helper",
                SupportsVision = true
            }
        ];

        var entry = await FindSubAgentModelAsync("sa-full-model");
        Assert.NotNull(entry);

        Assert.Equal("sa-full-model", entry!.Value.GetProperty("name").GetString());
        Assert.Equal(128000, entry.Value.GetProperty("contextWindow").GetInt32());
        Assert.Equal("high", entry.Value.GetProperty("reasoningEffort").GetString());
        Assert.Equal("Research helper", entry.Value.GetProperty("description").GetString());
        Assert.True(entry.Value.GetProperty("supportsVision").GetBoolean());
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

    /// <summary>
    /// The endpoint stores the posted model name verbatim: a trailing <c>:high</c> is part of the
    /// name and is never promoted to a reasoning effort.
    /// </summary>
    [Fact]
    public async Task PostSubAgentModel_StoresNameVerbatim_WithoutInferringReasoningEffort()
    {
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "suffix-model:high", contextWindow = (int?)null, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        var entry = await FindSubAgentModelAsync("suffix-model:high");
        Assert.NotNull(entry);
        Assert.Null(await FindSubAgentModelAsync("suffix-model"));
    }

    /// <summary>
    /// The explicit <c>reasoningEffort</c> request field is the only source of reasoning effort.
    /// </summary>
    [Fact]
    public async Task PostSubAgentModel_UsesExplicitReasoningEffortField()
    {
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "explicit-effort-model", contextWindow = (int?)null, reasoningEffort = "high", description = (string?)null },
            TestContext.Current.CancellationToken);

        var entry = await FindSubAgentModelAsync("explicit-effort-model");
        Assert.NotNull(entry);
        Assert.Equal("high", entry!.Value.GetProperty("reasoningEffort").GetString());
    }

    [Fact]
    public async Task PutSubAgentModel_SnakeCaseExtraHigh_RoundTrips()
    {
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "put-extra-high", contextWindow = 1000, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        var response = await _client.PutAsJsonAsync(
            "/api/config/sub-agent-models/put-extra-high",
            new { name = "put-extra-high", contextWindow = 2000, reasoningEffort = "extra_high", description = (string?)null },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindSubAgentModelAsync("put-extra-high");
        Assert.NotNull(entry);
        Assert.Equal("extra_high", entry!.Value.GetProperty("reasoningEffort").GetString());
    }

    [Fact]
    public async Task PutSubAgentModel_InvalidReasoningEffort_Returns400()
    {
        await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "put-invalid-effort", contextWindow = 1000, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        // Invalid reasoning on PUT is rejected by the global enum converter during binding.
        var response = await _client.PutAsJsonAsync(
            "/api/config/sub-agent-models/put-invalid-effort",
            new { name = "put-invalid-effort", contextWindow = 2000, reasoningEffort = "turbo", description = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        // The existing entry must be untouched.
        var entry = await FindSubAgentModelAsync("put-invalid-effort");
        Assert.NotNull(entry);
        Assert.Equal(1000, entry!.Value.GetProperty("contextWindow").GetInt32());
    }

    /// <summary>
    /// An unrecognised reasoning effort is invalid client input and must produce a 400
    /// Bad Request — not an unhandled 500. Since <c>reasoningEffort</c> is now a
    /// <c>ReasoningEffort?</c> enum, the global snake_case JSON enum converter rejects the
    /// value during model binding. The model must not be persisted.
    /// </summary>
    [Theory]
    [InlineData("turbo")]
    [InlineData("HIGHEST")]
    [InlineData("very-high")]
    [InlineData("1")]
    public async Task PostSubAgentModel_InvalidReasoningEffort_Returns400(string effort)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = $"invalid-effort-{effort}", contextWindow = (int?)null, reasoningEffort = effort, description = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        // A rejected request must not persist anything.
        Assert.Null(await FindSubAgentModelAsync($"invalid-effort-{effort}"));
    }

    /// <summary>
    /// An integer reasoning effort is rejected too: the global converter is configured with
    /// <c>allowIntegerValues: false</c>, so a numeric wire value can never be coerced into a level.
    /// </summary>
    [Fact]
    public async Task PostSubAgentModel_IntegerReasoningEffort_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name = "int-effort-model", contextWindow = (int?)null, reasoningEffort = 3, description = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await FindSubAgentModelAsync("int-effort-model"));
    }

    /// <summary>
    /// A null reasoning effort means "unset" and is accepted, not rejected.
    /// </summary>
    [Fact]
    public async Task PostSubAgentModel_NullReasoningEffort_IsAcceptedAsUnset()
    {
        const string name = "null-effort-model";
        var response = await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name, contextWindow = (int?)null, reasoningEffort = (string?)null, description = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindSubAgentModelAsync(name);
        Assert.NotNull(entry);
        Assert.Equal(JsonValueKind.Null, entry!.Value.GetProperty("reasoningEffort").ValueKind);
    }

    /// <summary>
    /// The snake_case wire form of every level round-trips into the persisted YAML value.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("extra_high")]
    public async Task PostSubAgentModel_SnakeCaseReasoningEffort_RoundTrips(string effort)
    {
        var name = $"snake-effort-{effort}";
        var response = await _client.PostAsJsonAsync(
            "/api/config/sub-agent-models",
            new { name, contextWindow = (int?)null, reasoningEffort = effort, description = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await FindSubAgentModelAsync(name);
        Assert.NotNull(entry);
        Assert.Equal(effort, entry!.Value.GetProperty("reasoningEffort").GetString());
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
            new { name = "copilot/gemini-3.5-flash", contextWindow = 100000 },
            TestContext.Current.CancellationToken);

        // PUT with URL-encoded slash (%2F) — the endpoint must decode it back to "/"
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/copilot%2Fgemini-3.5-flash",
            new { name = "copilot/gemini-3.5-flash", contextWindow = 200000 },
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
            new { name = "copilot/ollama-model", contextWindow = (int?)null },
            TestContext.Current.CancellationToken);

        // DELETE with URL-encoded slash (%2F) — the endpoint must decode it back to "/"
        var response = await _client.DeleteAsync(
            "/api/config/available-models/copilot%2Follama-model",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── POST /api/config/available-models — model names stored verbatim ──────

    [Fact]
    public async Task PostAvailableModel_WithColonSegment_StoresNameVerbatim()
    {
        // POST a model whose name carries a legacy reasoning-looking suffix
        var postResponse = await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "test-model:high", contextWindow = 128000 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);

        // GET /api/config/models and verify the name was stored verbatim
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
            if (entryName == "test-model:high")
            {
                // The name is stored verbatim, and the available-models API contract
                // does not expose reasoningEffort.
                Assert.False(entry.TryGetProperty("reasoningEffort", out _),
                    "availableModels entries must not expose 'reasoningEffort'");
                found = true;
                break;
            }
        }
        Assert.True(found, "Expected a model with Name='test-model:high' in availableModels");
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
            new { name = "vision-am-model", contextWindow = 1000, supportsVision = vision },
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
            new { name = "put-vision-model", contextWindow = 1000 },
            TestContext.Current.CancellationToken);

        // Update with SupportsVision
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/put-vision-model",
            new { name = "put-vision-model", contextWindow = 2000, supportsVision = vision },
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
            new { name = "false-survive", contextWindow = 1000, supportsVision = true },
            TestContext.Current.CancellationToken);

        // Update to explicit false
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/false-survive",
            new { name = "false-survive", contextWindow = 2000, supportsVision = false },
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

    /// <summary>
    /// The live <see cref="HiveConfigFile"/> singleton the endpoints read. Exposed so tests can
    /// seed raw YAML-shaped state — including values the write endpoints would reject, such as
    /// an unrecognised <c>reasoning_effort</c> left behind by a hand-edited config or an older
    /// schema — which is the only way to exercise the read path's lenient parsing.
    /// </summary>
    public HiveConfigFile Config => _config;

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