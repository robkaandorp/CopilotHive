using System.Net;
using System.Net.Http.Json;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the available-models configuration REST endpoints registered by
/// <c>ConfigHub.MapConfigEndpoints</c>. Uses <see cref="HiveTestFactory"/> to boot the real
/// application from <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// The test factory boots the app without a <c>--config-repo</c> argument, so
/// <c>ConfigModelService</c> and <c>ModelDiscoveryService</c> are not registered. These tests
/// therefore exercise the service-null (<c>Results.Problem</c>) path, verifying the endpoints are
/// wired up and respond gracefully instead of throwing.
/// </remarks>
[Collection("HiveIntegration")]
public class ConfigEndpointsTests
{
    private readonly HttpClient _client;

    /// <summary>Receives the shared factory and creates an <see cref="HttpClient"/> backed by the test server.</summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/> fixture for this test class.</param>
    public ConfigEndpointsTests(HiveTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ── GET /api/config/models/discover ──────────────────────────────────────

    [Fact]
    public async Task GetDiscover_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/config/models/discover", TestContext.Current.CancellationToken);

        // The route exists; it must not 404. Without a registered discovery service it returns Problem (500).
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── POST /api/config/available-models ────────────────────────────────────

    [Fact]
    public async Task PostAvailableModel_Endpoint_IsRouted()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "copilot/test-model", contextWindow = 128000, reasoningEffort = "high" },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── PUT /api/config/available-models/{name} ──────────────────────────────

    [Fact]
    public async Task PutAvailableModel_Endpoint_IsRouted()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/copilot%2Ftest-model",
            new { name = "copilot/test-model", contextWindow = 256000, reasoningEffort = "medium" },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── DELETE /api/config/available-models/{name} ───────────────────────────

    [Fact]
    public async Task DeleteAvailableModel_Endpoint_IsRouted()
    {
        var response = await _client.DeleteAsync(
            "/api/config/available-models/copilot%2Ftest-model",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── GET /api/config/repositories ─────────────────────────────────────────────

    [Fact]
    public async Task GetRepositories_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/config/repositories", TestContext.Current.CancellationToken);

        // Route exists; returns NotFound (404) when HiveConfigFile is not registered (no config repo).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── POST /api/config/repositories ───────────────────────────────────────────

    [Fact]
    public async Task PostRepository_Endpoint_IsRouted()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/config/repositories",
            new { name = "test-repo", url = "https://github.com/org/repo.git", defaultBranch = "main" },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── PUT /api/config/repositories/{name} ──────────────────────────────────────

    [Fact]
    public async Task PutRepository_Endpoint_IsRouted()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/config/repositories/test-repo",
            new { name = "test-repo", url = "https://github.com/org/repo.git", defaultBranch = "main" },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── DELETE /api/config/repositories/{name} ───────────────────────────────────

    [Fact]
    public async Task DeleteRepository_Endpoint_IsRouted()
    {
        var response = await _client.DeleteAsync(
            "/api/config/repositories/test-repo",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── RepositoryRequest.Release JSON binding (System.Text.Json) ────────────────
    // These tests deserialize JSON directly into RepositoryRequest to prove the
    // Release field binds via camelCase property names. They FAIL if the Release
    // parameter is removed from RepositoryRequest or if the JSON property names
    // (release / mergeTo / tagBranch) no longer map — coverage the route-availability
    // tests above cannot provide (those hit the null-service guard before binding).
    // Uses JsonSerializerDefaults.Web to mirror the ASP.NET minimal-API binding pipeline
    // (case-insensitive, camelCase) that actually deserializes RepositoryRequest at runtime.

    private static readonly System.Text.Json.JsonSerializerOptions WebJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void RepositoryRequest_DeserializesReleaseWithBothFields()
    {
        const string json =
            "{\"name\":\"test\",\"url\":\"https://github.com/org/repo.git\",\"defaultBranch\":\"main\"," +
            "\"release\":{\"mergeTo\":\"main\",\"tagBranch\":\"develop\"}}";

        var req = System.Text.Json.JsonSerializer.Deserialize<RepositoryRequest>(json, WebJsonOptions);

        Assert.NotNull(req);
        Assert.Equal("test", req!.Name);
        Assert.Equal("main", req.DefaultBranch);
        Assert.NotNull(req.Release);
        Assert.Equal("main", req.Release!.MergeTo);
        Assert.Equal("develop", req.Release!.TagBranch);
    }

    [Fact]
    public void RepositoryRequest_MissingReleaseField_DefaultsToNull()
    {
        const string json =
            "{\"name\":\"test\",\"url\":\"https://github.com/org/repo.git\",\"defaultBranch\":\"main\"}";

        var req = System.Text.Json.JsonSerializer.Deserialize<RepositoryRequest>(json, WebJsonOptions);

        Assert.NotNull(req);
        Assert.Null(req!.Release);
    }

    [Fact]
    public void RepositoryRequest_ExplicitNullRelease_IsNull()
    {
        const string json =
            "{\"name\":\"test\",\"url\":\"https://github.com/org/repo.git\",\"defaultBranch\":\"main\",\"release\":null}";

        var req = System.Text.Json.JsonSerializer.Deserialize<RepositoryRequest>(json, WebJsonOptions);

        Assert.NotNull(req);
        Assert.Null(req!.Release);
    }

    [Fact]
    public void RepositoryRequest_EmptyReleaseObject_HasNullFields()
    {
        const string json =
            "{\"name\":\"test\",\"url\":\"https://github.com/org/repo.git\",\"defaultBranch\":\"main\"," +
            "\"release\":{\"mergeTo\":null,\"tagBranch\":null}}";

        var req = System.Text.Json.JsonSerializer.Deserialize<RepositoryRequest>(json, WebJsonOptions);

        Assert.NotNull(req);
        Assert.NotNull(req!.Release);
        Assert.Null(req.Release!.MergeTo);
        Assert.Null(req.Release!.TagBranch);
    }

    // ── GET /api/config/orchestrator ─────────────────────────────────────────────

    [Fact]
    public async Task GetOrchestrator_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/config/orchestrator", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH /api/config/orchestrator ───────────────────────────────────────────

    [Fact]
    public async Task PatchOrchestrator_Endpoint_IsRouted()
    {
        var content = new StringContent("{\"maxIterations\":10}", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PatchAsync("/api/config/orchestrator", content, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── GET /api/config/workers ─────────────────────────────────────────────────

    [Fact]
    public async Task GetWorkers_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/config/workers", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH /api/config/workers ───────────────────────────────────────────────

    [Fact]
    public async Task PatchWorkers_Endpoint_IsRouted()
    {
        var content = new StringContent("{\"coder\":50000}", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PatchAsync("/api/config/workers", content, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── GET /api/config/composer ────────────────────────────────────────────────

    [Fact]
    public async Task GetComposer_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/config/composer", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH /api/config/composer ──────────────────────────────────────────────

    [Fact]
    public async Task PatchComposer_Endpoint_IsRouted()
    {
        var content = new StringContent("{\"contextWindow\":200000,\"maxSteps\":50}", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PatchAsync("/api/config/composer", content, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
