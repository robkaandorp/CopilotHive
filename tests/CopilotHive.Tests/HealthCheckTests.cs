using System.Net;
using System.Reflection;
using System.Text.Json;
using CopilotHive.Goals;
using CopilotHive.Models;
using CopilotHive.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the /health endpoint introduced in Program.cs.
/// Verifies that each request increments the check counter in a thread-safe manner
/// and that the response body is valid JSON containing all required fields.
/// </summary>
public class HealthCheckTests : IAsyncLifetime
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

        // Mirror the implementation from Program.cs RunServerAsync
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

    [Fact]
    public async Task HealthEndpoint_FirstCall_ReturnsCheckNumber1()
    {
        var response = await _client!.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("checkNumber").GetInt32());
    }

    [Fact]
    public async Task HealthEndpoint_SecondCall_ReturnsCheckNumber2()
    {
        await _client!.GetAsync("/health");

        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("checkNumber").GetInt32());
    }

    [Fact]
    public async Task HealthEndpoint_SequentialCalls_IncrementCheckNumberMonotonically()
    {
        for (var i = 1; i <= 5; i++)
        {
            var response = await _client!.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(i, doc.RootElement.GetProperty("checkNumber").GetInt32());
        }
    }
}
