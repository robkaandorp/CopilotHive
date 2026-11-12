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
    /// <para>
    /// It is ALSO the regression guard for the global snake_case enum converter registered in
    /// <c>Program.AddHiveJsonOptions</c>: the multi-word values would render as
    /// <c>brain_goal</c> / <c>goal_review</c> if the global converter ever outranked this
    /// enum's own type-level <c>[JsonConverter]</c> attribute.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(LlmSessionType.Brain, "Brain")]
    [InlineData(LlmSessionType.BrainGoal, "BrainGoal")]
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

    /// <summary>
    /// THE COMPOSER EXCLUSION CONTRACT of <c>GET /api/sessions</c>, together with the two carriers
    /// the previous Composer theory row provided:
    /// <list type="bullet">
    ///   <item>The endpoint REPORTS SHARED EXECUTION SESSIONS ONLY: a registered
    ///   <see cref="LlmSessionType.Composer"/> session is absent from the response, while a
    ///   simultaneously registered execution session is present (the positive control proving the
    ///   filter removes Composer rather than emptying the route).</item>
    ///   <item><see cref="LlmSessionRegistry.GetAll"/> keeps its UNCHANGED semantics — the dashboard
    ///   still sees the Composer entry, so the filter is provably endpoint-local.</item>
    ///   <item>The Composer enum value still serializes as the JSON STRING <c>"Composer"</c> through
    ///   the host's own HTTP JSON options, which is the same converter chain the endpoint serializes
    ///   with — so moving this row off the endpoint did not drop the snake_case-converter guard.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task GetSessions_ExcludesComposerSessions_FromTheEndpointButNotFromTheRegistry()
    {
        var registry = _factory.Services.GetRequiredService<LlmSessionRegistry>();
        var composerSessionId = $"test-session-{Guid.NewGuid():N}";
        var executionSessionId = $"test-session-{Guid.NewGuid():N}";
        registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = composerSessionId,
            SessionType = LlmSessionType.Composer,
            Model = "composer-model",
            Status = "active",
            CurrentTokens = 0,
            MaxTokens = 1,
        });
        registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = executionSessionId,
            SessionType = LlmSessionType.Brain,
            Model = "brain-model",
            Status = "active",
            CurrentTokens = 0,
            MaxTokens = 1,
        });

        try
        {
            var response = await _client.GetAsync("/api/sessions", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(content);
            var ids = document.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("sessionId").GetString())
                .ToList();

            // Composer is excluded; the execution session is still reported (positive control).
            Assert.DoesNotContain(composerSessionId, ids);
            Assert.Contains(executionSessionId, ids);
            Assert.DoesNotContain(
                document.RootElement.EnumerateArray(),
                e => e.GetProperty("sessionType").GetString() == nameof(LlmSessionType.Composer));

            // GetAll() semantics are UNCHANGED: the dashboard still sees the Composer entry.
            Assert.Contains(registry.GetAll(), session => session.SessionId == composerSessionId);

            // The Composer enum still renders as the JSON string "Composer" through the host's own
            // HTTP JSON options — the same converter chain the endpoint uses.
            var jsonOptions = _factory.Services
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            using var serialized = JsonDocument.Parse(
                JsonSerializer.Serialize(LlmSessionType.Composer, jsonOptions));
            Assert.Equal(JsonValueKind.String, serialized.RootElement.ValueKind);
            Assert.Equal(nameof(LlmSessionType.Composer), serialized.RootElement.GetString());
        }
        finally
        {
            registry.Unregister(composerSessionId);
            registry.Unregister(executionSessionId);
        }
    }
}
