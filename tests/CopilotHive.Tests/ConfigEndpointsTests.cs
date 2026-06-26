using System.Net;
using System.Net.Http.Json;

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
}
