using System.Net;
using System.Text.Json;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the structured JSON response returned by <c>GET /health</c>.
/// Uses <see cref="HiveTestFactory"/> (a <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// subclass) to boot the real application from <c>Program.cs</c> — no routes are re-implemented here.
/// </summary>
public class HealthEndpointTests : IClassFixture<HiveTestFactory>
{
    private readonly HttpClient _client;

    /// <summary>Receives the shared factory and creates an <see cref="HttpClient"/> backed by the test server.</summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/> fixture for this test class.</param>
    public HealthEndpointTests(HiveTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    /// <summary>Sends GET /health and parses the JSON response body.</summary>
    /// <returns>A parsed <see cref="JsonDocument"/> of the response body.</returns>
    private async Task<JsonDocument> GetHealthJsonAsync()
    {
        var response = await _client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetHealth_Returns200()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_ReturnsJsonContentType()
    {
        var response = await _client.GetAsync("/health");
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
}
