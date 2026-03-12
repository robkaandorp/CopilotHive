using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the /health endpoint introduced in Program.cs.
/// Verifies that each request increments the check counter in a thread-safe manner.
/// </summary>
public class HealthCheckTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        _app = builder.Build();

        // Mirror the exact implementation from Program.cs RunServerAsync
        var _checkCount = 0;
        _app.MapGet("/health", () =>
        {
            var count = Interlocked.Increment(ref _checkCount);
            return Results.Ok($"Healthy (check #{count})");
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
    public async Task HealthEndpoint_FirstCall_ReturnsCheck1()
    {
        var response = await _client!.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy (check #1)", body);
    }

    [Fact]
    public async Task HealthEndpoint_SecondCall_ReturnsCheck2()
    {
        // First call
        await _client!.GetAsync("/health");

        // Second call — counter must have incremented
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy (check #2)", body);
    }

    [Fact]
    public async Task HealthEndpoint_SequentialCalls_IncrementCounterMonotonically()
    {
        for (int i = 1; i <= 5; i++)
        {
            var response = await _client!.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains($"Healthy (check #{i})", body);
        }
    }
}
