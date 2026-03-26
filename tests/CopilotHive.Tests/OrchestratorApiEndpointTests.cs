using System.Net;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the Orchestrator REST API endpoints (<c>/api/orchestrator</c>).
/// </summary>
public class OrchestratorApiEndpointTests : IClassFixture<HiveTestFactory>
{
    private readonly HttpClient _client;

    /// <summary>Initialises the test with a shared <see cref="HiveTestFactory"/> fixture.</summary>
    /// <param name="factory">The shared test factory.</param>
    public OrchestratorApiEndpointTests(HiveTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    /// <summary>
    /// <c>POST /api/orchestrator/reset</c> returns 503 when Brain is not configured
    /// (no <c>BRAIN_MODEL</c> environment variable set in the test environment).
    /// </summary>
    [Fact]
    public async Task PostOrchestratorReset_WhenBrainNotEnabled_Returns503()
    {
        var response = await _client.PostAsync(
            "/api/orchestrator/reset", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// <c>POST /api/orchestrator/reset</c> endpoint exists and returns a non-404 response,
    /// confirming the route is registered.
    /// </summary>
    [Fact]
    public async Task PostOrchestratorReset_RouteExists_DoesNotReturn404()
    {
        var response = await _client.PostAsync(
            "/api/orchestrator/reset", null, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
