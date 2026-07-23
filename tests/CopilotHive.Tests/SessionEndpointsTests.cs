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
            SessionType = LlmSessionType.Brain,
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
        Assert.Equal(LlmSessionType.Brain, registered.SessionType);
        Assert.Equal("test-model", registered.Model);
        Assert.Equal("active", registered.Status);

        // Verify the sessionType property serializes as a JSON string, not an integer.
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(content);
        var first = document.RootElement.EnumerateArray().Single(e => e.GetProperty("sessionId").GetString() == sessionId);
        var sessionType = first.GetProperty("sessionType");
        Assert.Equal(JsonValueKind.String, sessionType.ValueKind);
        Assert.Equal("Brain", sessionType.GetString());
    }

    [Fact]
    public void LlmSessionRegistry_RegisteredAsSingletonInDI()
    {
        var instance1 = _factory.Services.GetRequiredService<LlmSessionRegistry>();
        var instance2 = _factory.Services.GetRequiredService<LlmSessionRegistry>();

        Assert.True(ReferenceEquals(instance1, instance2));
    }

    /// <summary>
    /// Verifies that every <see cref="LlmSessionType"/> enum value serializes as a JSON
    /// string (e.g. "Brain") rather than its underlying integer (e.g. 0). This is the
    /// core regression guard for the JsonStringEnumConverter applied to the enum.
    /// </summary>
    [Theory]
    [InlineData(LlmSessionType.Brain, "Brain")]
    [InlineData(LlmSessionType.BrainGoal, "BrainGoal")]
    [InlineData(LlmSessionType.Composer, "Composer")]
    [InlineData(LlmSessionType.GoalReview, "GoalReview")]
    public async Task GetSessions_SerializesSessionType_AsJsonString(LlmSessionType type, string expectedJson)
    {
        var registry = _factory.Services.GetRequiredService<LlmSessionRegistry>();
        var sessionId = $"test-session-{Guid.NewGuid():N}";
        var session = new LlmSessionInfo
        {
            SessionId = sessionId,
            SessionType = type,
            Model = "test-model",
            Status = "active",
            CurrentTokens = 0,
            MaxTokens = 1,
        };
        registry.RegisterOrUpdate(session);

        try
        {
            var response = await _client.GetAsync("/api/sessions", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(content);
            var element = document.RootElement.EnumerateArray()
                .Single(e => e.GetProperty("sessionId").GetString() == sessionId);
            var sessionType = element.GetProperty("sessionType");

            // Must be a JSON string, never an integer.
            Assert.Equal(JsonValueKind.String, sessionType.ValueKind);
            Assert.Equal(expectedJson, sessionType.GetString());
        }
        finally
        {
            registry.Unregister(sessionId);
        }
    }
}
