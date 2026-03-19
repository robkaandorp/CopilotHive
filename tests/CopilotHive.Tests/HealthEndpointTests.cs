using System.Net;
using System.Text.Json;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the structured JSON response returned by <c>GET /health</c>.
/// Uses <see cref="HiveTestFactory"/> (a <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// subclass) to boot the real application from <c>Program.cs</c> — no routes are re-implemented here.
/// </summary>
public class HealthEndpointTests : IClassFixture<HiveTestFactory>
{
    private readonly HttpClient _client;
    private readonly HiveTestFactory _factory;

    /// <summary>Receives the shared factory and creates an <see cref="HttpClient"/> backed by the test server.</summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/> fixture for this test class.</param>
    public HealthEndpointTests(HiveTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Sends GET /health and parses the JSON response body.</summary>
    /// <returns>A parsed <see cref="JsonDocument"/> of the response body.</returns>
    private async Task<JsonDocument> GetHealthJsonAsync()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetHealth_Returns200()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_ReturnsJsonContentType()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task GetHealth_HasStatusField()
    {
        using var json = await GetHealthJsonAsync();
        Assert.True(json.RootElement.TryGetProperty("status", out var val));
        Assert.False(string.IsNullOrEmpty(val.GetString()));
    }

    [Fact]
    public async Task GetHealth_HasUptimeField()
    {
        using var json = await GetHealthJsonAsync();
        Assert.True(json.RootElement.TryGetProperty("uptime", out var val));
        Assert.False(string.IsNullOrEmpty(val.GetString()));
    }

    [Fact]
    public async Task GetHealth_HasActiveGoalsField()
    {
        using var json = await GetHealthJsonAsync();
        Assert.True(json.RootElement.TryGetProperty("activeGoals", out var val));
        Assert.True(val.GetInt32() >= 0);
    }

    [Fact]
    public async Task GetHealth_HasCompletedGoalsField()
    {
        using var json = await GetHealthJsonAsync();
        Assert.True(json.RootElement.TryGetProperty("completedGoals", out var val));
        Assert.True(val.GetInt32() >= 0);
    }

    [Fact]
    public async Task GetHealth_HasConnectedWorkersField()
    {
        using var json = await GetHealthJsonAsync();
        Assert.True(json.RootElement.TryGetProperty("connectedWorkers", out var val));
        Assert.True(val.GetInt32() >= 0);
    }

    [Fact]
    public async Task GetHealth_HasVersionField()
    {
        using var json = await GetHealthJsonAsync();
        Assert.True(json.RootElement.TryGetProperty("version", out var val));
        Assert.False(string.IsNullOrEmpty(val.GetString()));
    }

    [Fact]
    public async Task GetHealth_HasServerTimeField()
    {
        using var json = await GetHealthJsonAsync();
        Assert.True(json.RootElement.TryGetProperty("serverTime", out var val));
        Assert.True(DateTime.TryParse(val.GetString(), out _));
    }

    [Fact]
    public async Task GetHealth_ActiveGoals_ReflectsAddedPendingGoal()
    {
        // Get baseline count before adding a goal.
        using var before = await GetHealthJsonAsync();
        var baselineActive = before.RootElement.GetProperty("activeGoals").GetInt32();

        // Add a pending goal via the singleton ApiGoalSource.
        var goalSource = _factory.Services.GetRequiredService<ApiGoalSource>();
        goalSource.AddGoal(new Goal { Id = "test-goal-" + Guid.NewGuid(), Description = "behavior test goal" });

        // Verify the count increased.
        using var after = await GetHealthJsonAsync();
        var newActive = after.RootElement.GetProperty("activeGoals").GetInt32();
        Assert.True(newActive > baselineActive,
            $"Expected activeGoals to increase from {baselineActive}, but got {newActive}");
    }

    [Fact]
    public async Task GetHealth_ConnectedWorkers_ReflectsRegisteredWorker()
    {
        // Get baseline count before registering a worker.
        using var before = await GetHealthJsonAsync();
        var baselineWorkers = before.RootElement.GetProperty("connectedWorkers").GetInt32();

        // Register a worker via the singleton WorkerPool.
        var workerPool = _factory.Services.GetRequiredService<WorkerPool>();
        workerPool.RegisterWorker("test-worker-" + Guid.NewGuid(), []);

        // Verify the count increased.
        using var after = await GetHealthJsonAsync();
        var newWorkers = after.RootElement.GetProperty("connectedWorkers").GetInt32();
        Assert.True(newWorkers > baselineWorkers,
            $"Expected connectedWorkers to increase from {baselineWorkers}, but got {newWorkers}");
    }
}
