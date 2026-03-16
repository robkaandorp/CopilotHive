using System.Net;
using System.Reflection;
using System.Text.Json;
using CopilotHive.Goals;
using CopilotHive.Models;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the structured JSON response returned by <c>GET /health</c>.
/// Verifies HTTP status, content-type, and all DTO fields have sensible values.
/// </summary>
public class HealthEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ApiGoalSource>();
        builder.Services.AddSingleton<WorkerPool>();

        _app = builder.Build();

        var serverStartTime = DateTime.UtcNow;
        var checkCount = 0;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        _app.MapGet("/health", (ApiGoalSource goalSource, WorkerPool workerPool) =>
        {
            var count = Interlocked.Increment(ref checkCount);
            var uptime = DateTime.UtcNow - serverStartTime;
            var goals = goalSource.GetAllGoals();
            return Results.Ok(new HealthResponse
            {
                Status = "Healthy",
                Uptime = uptime.Days > 0
                    ? $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
                    : uptime.Hours > 0
                        ? $"{uptime.Hours}h {uptime.Minutes}m"
                        : $"{uptime.Minutes}m {uptime.Seconds}s",
                UptimeSpan = uptime,
                ActiveGoals = goals.Count(g => g.Status is GoalStatus.Pending or GoalStatus.InProgress),
                CompletedGoals = goals.Count(g => g.Status == GoalStatus.Completed),
                ConnectedWorkers = workerPool.GetAllWorkers().Count,
                Version = version,
                ServerTime = DateTime.UtcNow,
                CheckNumber = count,
            });
        });

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
            await _app.StopAsync();
    }

    private async Task<JsonDocument> GetHealthJsonAsync()
    {
        var response = await _client!.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsHttp200()
    {
        var response = await _client!.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_ContentTypeIsApplicationJson()
    {
        var response = await _client!.GetAsync("/health");
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task HealthEndpoint_StatusIsHealthy()
    {
        using var doc = await GetHealthJsonAsync();
        var status = doc.RootElement.GetProperty("status").GetString();
        Assert.False(string.IsNullOrEmpty(status), "status must not be empty");
        Assert.Equal("Healthy", status);
    }

    [Fact]
    public async Task HealthEndpoint_UptimeIsNonEmptyString()
    {
        using var doc = await GetHealthJsonAsync();
        var uptime = doc.RootElement.GetProperty("uptime").GetString();
        Assert.False(string.IsNullOrEmpty(uptime), "uptime must not be empty");
    }

    [Fact]
    public async Task HealthEndpoint_ActiveGoalsIsNonNegative()
    {
        using var doc = await GetHealthJsonAsync();
        var activeGoals = doc.RootElement.GetProperty("activeGoals").GetInt32();
        Assert.True(activeGoals >= 0, "activeGoals must be >= 0");
    }

    [Fact]
    public async Task HealthEndpoint_CompletedGoalsIsNonNegative()
    {
        using var doc = await GetHealthJsonAsync();
        var completedGoals = doc.RootElement.GetProperty("completedGoals").GetInt32();
        Assert.True(completedGoals >= 0, "completedGoals must be >= 0");
    }

    [Fact]
    public async Task HealthEndpoint_ConnectedWorkersIsNonNegative()
    {
        using var doc = await GetHealthJsonAsync();
        var connectedWorkers = doc.RootElement.GetProperty("connectedWorkers").GetInt32();
        Assert.True(connectedWorkers >= 0, "connectedWorkers must be >= 0");
    }

    [Fact]
    public async Task HealthEndpoint_VersionIsNonEmptyString()
    {
        using var doc = await GetHealthJsonAsync();
        var version = doc.RootElement.GetProperty("version").GetString();
        Assert.False(string.IsNullOrEmpty(version), "version must not be empty");
    }

    [Fact]
    public async Task HealthEndpoint_ServerTimeIsValidUtcDateTime()
    {
        using var doc = await GetHealthJsonAsync();
        var raw = doc.RootElement.GetProperty("serverTime").GetString();
        Assert.True(DateTime.TryParse(raw, out var serverTime), "serverTime must parse as a valid DateTime");
        // Sanity-check: within 5 seconds of now
        Assert.True(Math.Abs((DateTime.UtcNow - serverTime.ToUniversalTime()).TotalSeconds) < 5,
            "serverTime must be close to UTC now");
    }

    [Fact]
    public async Task HealthEndpoint_ActiveGoals_ReflectsAddedPendingGoal()
    {
        var goalSource = _app!.Services.GetService(typeof(ApiGoalSource)) as ApiGoalSource;
        Assert.NotNull(goalSource);

        goalSource!.AddGoal(new Goal { Id = "test-goal-1", Description = "Test goal" });

        using var doc = await GetHealthJsonAsync();
        Assert.Equal(1, doc.RootElement.GetProperty("activeGoals").GetInt32());
    }

    [Fact]
    public async Task HealthEndpoint_ConnectedWorkers_ReflectsRegisteredWorker()
    {
        var workerPool = _app!.Services.GetService(typeof(WorkerPool)) as WorkerPool;
        Assert.NotNull(workerPool);

        workerPool!.RegisterWorker("test-worker-1", WorkerRole.Coder, ["code"]);

        using var doc = await GetHealthJsonAsync();
        Assert.True(doc.RootElement.GetProperty("connectedWorkers").GetInt32() >= 1,
            "connectedWorkers must reflect the registered worker");
    }
}
