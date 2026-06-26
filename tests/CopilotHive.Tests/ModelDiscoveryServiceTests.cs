using System.Net;
using CopilotHive.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CopilotHive.Tests;

public sealed class ModelDiscoveryServiceTests : IDisposable
{
    private readonly string? _origGhToken;
    private readonly string? _origGithubToken;
    private readonly string? _origOllamaApiKey;
    private readonly string? _origOllamaUrl;

    public ModelDiscoveryServiceTests()
    {
        // Capture originals so we can restore them after each test.
        _origGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _origGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        _origOllamaApiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
        _origOllamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL");

        // Start from a clean slate for every test.
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        Environment.SetEnvironmentVariable("OLLAMA_URL", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _origGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _origGithubToken);
        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", _origOllamaApiKey);
        Environment.SetEnvironmentVariable("OLLAMA_URL", _origOllamaUrl);
    }

    private static ModelDiscoveryService CreateService(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new ModelDiscoveryService(NullLogger<ModelDiscoveryService>.Instance, factory.Object);
    }

    // ── Copilot discovery ────────────────────────────────────────────────────

    [Fact]
    public async Task DiscoverCopilotModelsAsync_ParsesModelsCorrectly()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "test-token");
        const string body = """
        {
            "data": [
                {
                    "id": "claude-sonnet-4.6",
                    "name": "Claude Sonnet 4.6",
                    "vendor": "Anthropic",
                    "capabilities": { "limits": { "max_context_window_tokens": 200000 } },
                    "policy": { "state": "enabled" }
                },
                {
                    "id": "gpt-5.4",
                    "name": "GPT 5.4",
                    "vendor": "OpenAI",
                    "capabilities": { "limits": { "max_context_window_tokens": 128000 } },
                    "policy": { "state": "disabled" }
                }
            ]
        }
        """;
        var svc = CreateService(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, models.Count);
        Assert.Equal("copilot/claude-sonnet-4.6", models[0].Id);
        Assert.Equal("Claude Sonnet 4.6", models[0].Name);
        Assert.Equal("Anthropic", models[0].Vendor);
        Assert.Equal(200000, models[0].ContextWindow);
        Assert.True(models[0].Enabled);

        Assert.Equal("copilot/gpt-5.4", models[1].Id);
        Assert.Equal(128000, models[1].ContextWindow);
        Assert.False(models[1].Enabled);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_NoToken_ReturnsEmpty()
    {
        var svc = CreateService(new FakeHttpMessageHandler(HttpStatusCode.OK, "{}"));

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(models);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_HttpError_ReturnsEmpty()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "test-token");
        var svc = CreateService(new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "boom"));

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(models);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_SendsCorrectHeaders()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "test-token");
        HttpRequestMessage? captured = null;
        var svc = CreateService(new FakeHttpMessageHandler(
            HttpStatusCode.OK, """{ "data": [] }""", req => captured = req));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", captured.Headers.Authorization?.Parameter);
        Assert.True(captured.Headers.TryGetValues("X-GitHub-Api-Version", out var versions));
        Assert.Contains("2025-04-01", versions!);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_MissingCapabilities_HandledGracefully()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "test-token");
        const string body = """
        {
            "data": [
                { "id": "model-x", "name": "Model X", "policy": { "state": "enabled" } }
            ]
        }
        """;
        var svc = CreateService(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.Single(models);
        Assert.Equal("copilot/model-x", models[0].Id);
        Assert.Null(models[0].ContextWindow);
        Assert.True(models[0].Enabled);
    }

    // ── Ollama discovery ─────────────────────────────────────────────────────

    [Fact]
    public async Task DiscoverOllamaModelsAsync_ParsesModelsCorrectly()
    {
        Environment.SetEnvironmentVariable("OLLAMA_URL", "http://localhost:11434");
        const string body = """
        {
            "models": [
                { "name": "llama3.2" },
                { "name": "qwen2.5-coder" }
            ]
        }
        """;
        var svc = CreateService(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var models = await svc.DiscoverOllamaModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, models.Count);
        Assert.Equal("llama3.2", models[0].Id);
        Assert.Equal("llama3.2", models[0].Name);
        Assert.Null(models[0].ContextWindow);
        Assert.Equal("ollama", models[0].Vendor);
        Assert.True(models[0].Enabled);
        Assert.Equal("qwen2.5-coder", models[1].Name);
    }

    [Fact]
    public async Task DiscoverOllamaModelsAsync_WithApiKey_UsesOllamaCloudUrl()
    {
        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", "ollama-key");
        HttpRequestMessage? captured = null;
        var svc = CreateService(new FakeHttpMessageHandler(
            HttpStatusCode.OK, """{ "models": [] }""", req => captured = req));

        await svc.DiscoverOllamaModelsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("https://ollama.com/api/tags", captured!.RequestUri?.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("ollama-key", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task DiscoverOllamaModelsAsync_WithoutApiKey_UsesOllamaUrlEnvVar()
    {
        Environment.SetEnvironmentVariable("OLLAMA_URL", "http://custom-host:9999");
        HttpRequestMessage? captured = null;
        var svc = CreateService(new FakeHttpMessageHandler(
            HttpStatusCode.OK, """{ "models": [] }""", req => captured = req));

        await svc.DiscoverOllamaModelsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("http://custom-host:9999/api/tags", captured!.RequestUri?.ToString());
        Assert.Null(captured.Headers.Authorization);
    }

    [Fact]
    public async Task DiscoverOllamaModelsAsync_NoConfig_ReturnsEmpty()
    {
        var svc = CreateService(new FakeHttpMessageHandler(HttpStatusCode.OK, "{}"));

        var models = await svc.DiscoverOllamaModelsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(models);
    }

    [Fact]
    public async Task DiscoverOllamaModelsAsync_HttpError_ReturnsEmpty()
    {
        Environment.SetEnvironmentVariable("OLLAMA_URL", "http://localhost:11434");
        var svc = CreateService(new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "boom"));

        var models = await svc.DiscoverOllamaModelsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(models);
    }

    // ── DiscoverAllAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task DiscoverAllAsync_CombinesCopilotAndOllamaModels()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", "ollama-key");

        const string copilotBody = """
        { "data": [ { "id": "claude", "name": "Claude", "policy": { "state": "enabled" } } ] }
        """;
        const string ollamaBody = """
        { "models": [ { "name": "llama3.2" } ] }
        """;

        var handler = new RoutingHttpMessageHandler(req =>
            req.RequestUri!.Host.Contains("githubcopilot")
                ? (HttpStatusCode.OK, copilotBody)
                : (HttpStatusCode.OK, ollamaBody));
        var svc = CreateService(handler);

        var models = await svc.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, models.Count);
        Assert.Equal("copilot/claude", models[0].Id);
        Assert.Equal("llama3.2", models[1].Id);
        Assert.Equal("ollama", models[1].Vendor);
    }

    [Fact]
    public async Task DiscoverAllAsync_OnlyCopilotAvailable_WhenNoOllamaConfig()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "test-token");

        const string copilotBody = """
        { "data": [ { "id": "claude", "name": "Claude", "policy": { "state": "enabled" } } ] }
        """;
        var handler = new RoutingHttpMessageHandler(_ => (HttpStatusCode.OK, copilotBody));
        var svc = CreateService(handler);

        var models = await svc.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Single(models);
        Assert.Equal("copilot/claude", models[0].Id);
    }
}

/// <summary>
/// A test <see cref="HttpMessageHandler"/> returning a fixed status and body.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;
    private readonly Action<HttpRequestMessage>? _onRequest;

    public FakeHttpMessageHandler(HttpStatusCode statusCode, string body, Action<HttpRequestMessage>? onRequest = null)
    {
        _statusCode = statusCode;
        _body = body;
        _onRequest = onRequest;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _onRequest?.Invoke(request);
        return Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that selects a response based on the request.
/// </summary>
internal sealed class RoutingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _router;

    public RoutingHttpMessageHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> router)
    {
        _router = router;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (status, body) = _router(request);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        });
    }
}
