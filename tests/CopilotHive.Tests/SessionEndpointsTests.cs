using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CopilotHive.Dashboard;

using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>Integration tests for the LLM sessions REST endpoint registered by <see cref="ApiEndpoints.MapSessionEndpoints"/>.</summary>
[Collection("HiveIntegration")]
public class SessionEndpointsTests
{
    private readonly HttpClient _client;
    private readonly HiveTestFactory _factory;

    /// <summary>Receives the shared factory and creates an <see cref="HttpClient"/> backed by the test server.</summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/> fixture for this test class.</param>
    public SessionEndpointsTests(HiveTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetSessions_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/sessions", TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSessions_ReturnsOkWithEmptyList()
    {
        var response = await _client.GetAsync("/api/sessions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(content);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Empty(document.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task GetSessions_ReturnsRegisteredSessions()
    {
        var registry = _factory.Services.GetRequiredService<LlmSessionRegistry>();
        var session = new LlmSessionInfo
        {
            SessionId = "test-session-1",
            SessionType = "brain",
            Model = "test-model",
            Status = "active",
            CurrentTokens = 1000,
            MaxTokens = 10000,
        };
        registry.RegisterOrUpdate(session);

        var response = await _client.GetAsync("/api/sessions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sessions = await response.Content.ReadFromJsonAsync<List<LlmSessionInfo>>(TestContext.Current.CancellationToken);
        Assert.NotNull(sessions);
        Assert.Single(sessions);
        Assert.Equal("test-session-1", sessions[0].SessionId);
        Assert.Equal("brain", sessions[0].SessionType);
        Assert.Equal("test-model", sessions[0].Model);
        Assert.Equal("active", sessions[0].Status);
    }
}
