using System.Net;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the Orchestrator REST API endpoints (<c>/api/orchestrator</c>).
/// </summary>
public class OrchestratorApiEndpointTests : IClassFixture<HiveTestFactory>
{
    private readonly HiveTestFactory _factory;
    private readonly HttpClient _client;

    /// <summary>Initialises the test with a shared <see cref="HiveTestFactory"/> fixture.</summary>
    /// <param name="factory">The shared test factory.</param>
    public OrchestratorApiEndpointTests(HiveTestFactory factory)
    {
        _factory = factory;
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

    /// <summary>
    /// <c>POST /api/orchestrator/reset</c> returns 200 when Brain is registered.
    /// Uses <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>
    /// to inject a stub <see cref="IDistributedBrain"/>.
    /// </summary>
    [Fact]
    public async Task PostOrchestratorReset_WhenBrainEnabled_Returns200()
    {
        using var brainFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IDistributedBrain>(new StubBrainForReset());
            });
        });
        using var client = brainFactory.CreateClient();

        var response = await client.PostAsync(
            "/api/orchestrator/reset", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// Minimal <see cref="IDistributedBrain"/> stub that tracks <see cref="ResetSessionAsync"/> calls.
/// Used in integration tests to verify the reset endpoint returns 200 when a Brain is registered.
/// </summary>
file sealed class StubBrainForReset : IDistributedBrain
{
    public int ResetCalls { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ResetSessionAsync(CancellationToken ct = default) { ResetCalls++; return Task.CompletedTask; }
    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());
    public Task<string> CraftPromptAsync(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult($"Work on {pipeline.Description}");
    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
        Task.CompletedTask;
    public BrainStats? GetStats() => null;
}
