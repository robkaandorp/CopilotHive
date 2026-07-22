using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CopilotHive.Dashboard;

using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>Integration tests for the LLM sessions REST endpoint registered by <see cref="ApiEndpoints.MapSessionEndpoints"/>.</summary>
[Collection("HiveIntegration")]
public class SessionEndpointsTests : IDisposable
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

    /// <summary>
    /// Clears any sessions registered during a test from the shared singleton registry so
    /// tests remain order-independent (the registry is a shared mutable singleton).
    /// </summary>
    public void Dispose()
    {
        var registry = _factory.Services.GetRequiredService<LlmSessionRegistry>();
        foreach (var session in registry.GetAll())
        {
            registry.Unregister(session.SessionId);
        }
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
        // Defensively clear the shared singleton registry before asserting emptiness,
        // since other tests may have registered sessions.
        var registry = _factory.Services.GetRequiredService<LlmSessionRegistry>();
        foreach (var session in registry.GetAll())
        {
            registry.Unregister(session.SessionId);
        }

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
        var sessionId = $"test-session-{Guid.NewGuid():N}";
        var session = new LlmSessionInfo
        {
            SessionId = sessionId,
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
        var registered = sessions.Single(s => s.SessionId == sessionId);
        Assert.Equal("brain", registered.SessionType);
        Assert.Equal("test-model", registered.Model);
        Assert.Equal("active", registered.Status);
    }

    [Fact]
    public void LlmSessionRegistry_RegisteredAsSingletonInDI()
    {
        var instance1 = _factory.Services.GetRequiredService<LlmSessionRegistry>();
        var instance2 = _factory.Services.GetRequiredService<LlmSessionRegistry>();

        Assert.True(ReferenceEquals(instance1, instance2));
    }
}
