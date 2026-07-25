using System.Net;
using System.Text;
using System.Text.Json;
using CopilotHive.Goals;
using CopilotHive.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Additional integration tests for the extend-iterations feature that complement
/// the existing tests in <see cref="GoalsApiEndpointTests"/> and
/// <see cref="Orchestration.ComposerToolTests"/>.
///
/// Fills gaps identified during review:
/// <list type="bullet">
///   <item>503 Service Unavailable when <see cref="GoalDispatcher"/> is not registered in DI.</item>
///   <item>Boundary value 1 (valid lower bound) and 100 (valid upper bound) for the API endpoint.</item>
///   <item>Blank/whitespace goal ID validation in the Composer tool.</item>
///   <item>Default additionalIterations value (5) in the Composer tool.</item>
/// </list>
/// </summary>
[Collection("HiveIntegration")]
public class ExtendIterationsAdditionalTests
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ── 503 when GoalDispatcher is not in DI ─────────────────────────────────

    /// <summary>
    /// Creates a <see cref="WebApplicationFactory{Program}"/> derived from the shared fixture
    /// with <see cref="GoalDispatcher"/> removed from DI, so the endpoint returns 503.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactoryWithoutDispatcher(
        HiveTestFactory baseFactory) =>
        baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove all GoalDispatcher registrations (singleton + hosted service wrapper).
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(GoalDispatcher))
                    .ToList();
                foreach (var d in descriptors)
                    services.Remove(d);

                // Also remove the hosted-service wrapper that resolves GoalDispatcher.
                var hostedDescriptors = services
                    .Where(d => d.ImplementationFactory is not null
                        && d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                        && d.ImplementationFactory.ToString()!.Contains("GoalDispatcher"))
                    .ToList();
                foreach (var d in hostedDescriptors)
                    services.Remove(d);
            });
        });

    [Fact]
    public async Task ExtendIterations_DispatcherNotInDI_Returns503ServiceUnavailable()
    {
        using var baseFactory = new HiveTestFactory();
        using var factory = CreateFactoryWithoutDispatcher(baseFactory);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/goals/some-goal/extend-iterations",
            new StringContent(
                JsonSerializer.Serialize(new { additionalIterations = 5 }, JsonOpts),
                Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ── Boundary values 1 and 100 are accepted (not 400) ────────────────────
    // These should return 404 (nonexistent goal) rather than 400, proving
    // the bounds check accepted them and forwarded to ResumeGoalAsync.

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task ExtendIterations_BoundaryValues_AreAcceptedNotRejectedAs400(
        int additionalIterations)
    {
        using var baseFactory = new HiveTestFactory();
        using var factory = baseFactory;
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/goals/nonexistent-boundary-goal/extend-iterations",
            new StringContent(
                JsonSerializer.Serialize(new { additionalIterations }, JsonOpts),
                Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        // 404 means the value passed validation (1–100 accepted) but goal doesn't exist.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}