using System.Net;
using System.Reflection;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CopilotHive.Tests;

[Collection("EnvVarMutation")]
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

    /// <summary>
    /// Stub per-account endpoint resolver passed to EVERY service created in this suite: it
    /// answers with the provider's default endpoint and performs NO network I/O, so the real
    /// per-account lookup is never reached from a test. The returned base mirrors
    /// <c>ChatClientFactory.DefaultCopilotApiEndpoint</c> (trailing slash), so <c>models</c>
    /// resolves to <c>https://api.githubcopilot.com/models</c> exactly as before.
    /// </summary>
    private static Task<Uri> StubCopilotEndpointResolver(string token, CancellationToken ct)
        => Task.FromResult(new Uri("https://api.githubcopilot.com/"));

    private static ModelDiscoveryService CreateService(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new ModelDiscoveryService(
            NullLogger<ModelDiscoveryService>.Instance, factory.Object,
            resolveCopilotEndpointAsync: StubCopilotEndpointResolver);
    }

    private static ModelDiscoveryService CreateService(
        HttpMessageHandler handler,
        Func<CancellationToken, Task<string?>>? storedTokenLookup)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new ModelDiscoveryService(
            NullLogger<ModelDiscoveryService>.Instance, factory.Object, storedTokenLookup,
            StubCopilotEndpointResolver);
    }

    private static ModelDiscoveryService CreateService(
        HttpMessageHandler handler,
        Func<CancellationToken, Task<string?>>? storedTokenLookup,
        ILogger<ModelDiscoveryService> logger)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new ModelDiscoveryService(
            logger, factory.Object, storedTokenLookup, StubCopilotEndpointResolver);
    }

    /// <summary>Body describing a single Copilot model for discovery tests.</summary>
    private const string SingleCopilotModelBody =
        """{ "data": [ { "id": "claude", "name": "Claude", "policy": { "state": "enabled" } } ] }""";

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

    /// <summary>
    /// Asserts the exact <c>Copilot-Integration-Id</c> integration header contract on a
    /// captured Copilot /models request: the header exists EXACTLY ONCE with the exact
    /// value <c>copilot-developer-cli</c>. Duplicate or differently-valued occurrences fail.
    /// Kept adjacent to the header tests so both credential paths assert the same contract.
    /// </summary>
    private static void AssertCopilotIntegrationHeader(HttpRequestMessage request)
    {
        Assert.True(
            request.Headers.TryGetValues("Copilot-Integration-Id", out var values),
            "The Copilot /models request must carry a Copilot-Integration-Id header.");
        Assert.Equal(["copilot-developer-cli"], values!.ToArray());
    }

    /// <summary>
    /// Asserts the captured request carries NO <c>Copilot-Integration-Id</c> header at all —
    /// the header is Copilot-specific and must never leak onto Ollama discovery.
    /// </summary>
    private static void AssertCopilotIntegrationHeaderAbsent(HttpRequestMessage request)
    {
        Assert.False(
            request.Headers.Contains("Copilot-Integration-Id"),
            "The request must NOT carry a Copilot-Integration-Id header.");
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
        // The integration header rides along on the SAME request — exactly one instance
        // with the exact value, alongside the unchanged Authorization/Api-Version pair.
        AssertCopilotIntegrationHeader(captured);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_StoredOAuth_SendsCorrectHeaders()
    {
        // Stored-OAuth credential path: the integration header must be present with the
        // SAME exact contract as the environment-token path above.
        HttpRequestMessage? captured = null;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, """{ "data": [] }""", req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>("stored-oauth-token"));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal("stored-oauth-token", captured.Headers.Authorization?.Parameter);
        Assert.True(captured.Headers.TryGetValues("X-GitHub-Api-Version", out var versions));
        Assert.Contains("2025-04-01", versions!);
        AssertCopilotIntegrationHeader(captured);
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
        Assert.Equal("ollama-local/llama3.2", models[0].Id);
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
        // The integration header is Copilot-specific: Ollama discovery must stay header-free.
        AssertCopilotIntegrationHeaderAbsent(captured);
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
        // The integration header is Copilot-specific: Ollama discovery must stay header-free.
        AssertCopilotIntegrationHeaderAbsent(captured);
    }

    [Fact]
    public async Task DiscoverOllamaModelsAsync_WithApiKey_PrefixesOllamaCloud()
    {
        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", "ollama-key");
        const string body = """
        {
            "models": [ { "name": "kimi-k3" } ]
        }
        """;
        var svc = CreateService(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var models = await svc.DiscoverOllamaModelsAsync(TestContext.Current.CancellationToken);

        Assert.Single(models);
        Assert.Equal("ollama-cloud/kimi-k3", models[0].Id);
    }

    [Fact]
    public async Task DiscoverOllamaModelsAsync_WithUrlOnly_PrefixesOllamaLocal()
    {
        Environment.SetEnvironmentVariable("OLLAMA_URL", "http://localhost:11434");
        const string body = """
        {
            "models": [ { "name": "mistral" } ]
        }
        """;
        var svc = CreateService(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var models = await svc.DiscoverOllamaModelsAsync(TestContext.Current.CancellationToken);

        Assert.Single(models);
        Assert.Equal("ollama-local/mistral", models[0].Id);
    }

    [Fact]
    public async Task DiscoverOllamaModelsAsync_WithApiKeyAndUrl_PrefersCloudPrefix()
    {
        // When both OLLAMA_API_KEY and OLLAMA_URL are set, the API key wins (cloud prefix).
        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", "ollama-key");
        Environment.SetEnvironmentVariable("OLLAMA_URL", "http://localhost:11434");
        const string body = """
        {
            "models": [ { "name": "kimi-k3" } ]
        }
        """;
        var svc = CreateService(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var models = await svc.DiscoverOllamaModelsAsync(TestContext.Current.CancellationToken);

        Assert.Single(models);
        Assert.Equal("ollama-cloud/kimi-k3", models[0].Id);
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
        Assert.Equal("ollama-cloud/llama3.2", models[1].Id);
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

    [Fact]
    public async Task DiscoverAllAsync_CombinesCopilotAndLocalOllamaModels()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("OLLAMA_URL", "http://localhost:11434");

        const string copilotBody = """
        { "data": [ { "id": "claude", "name": "Claude", "policy": { "state": "enabled" } } ] }
        """;
        const string ollamaBody = """
        { "models": [ { "name": "mistral" } ] }
        """;

        var handler = new RoutingHttpMessageHandler(req =>
            req.RequestUri!.Host.Contains("githubcopilot")
                ? (HttpStatusCode.OK, copilotBody)
                : (HttpStatusCode.OK, ollamaBody));
        var svc = CreateService(handler);

        var models = await svc.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, models.Count);
        Assert.Equal("copilot/claude", models[0].Id);
        Assert.Equal("ollama-local/mistral", models[1].Id);
        Assert.Equal("ollama", models[1].Vendor);
    }

    [Fact]
    public async Task DiscoverAllAsync_CopilotModelsUnchanged_PrefixesWithCopilot()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "test-token");

        const string copilotBody = """
        {
            "data": [
                { "id": "gpt-5", "name": "GPT-5", "vendor": "OpenAI", "capabilities": { "limits": { "max_context_window_tokens": 128000 } }, "policy": { "state": "enabled" } },
                { "id": "claude-sonnet-4", "name": "Claude Sonnet 4", "vendor": "Anthropic", "capabilities": { "limits": { "max_context_window_tokens": 200000 } }, "policy": { "state": "enabled" } }
            ]
        }
        """;
        var handler = new RoutingHttpMessageHandler(_ => (HttpStatusCode.OK, copilotBody));
        var svc = CreateService(handler);

        var models = await svc.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, models.Count);
        Assert.All(models, m => Assert.StartsWith("copilot/", m.Id));
        Assert.Equal("copilot/gpt-5", models[0].Id);
        Assert.Equal("copilot/claude-sonnet-4", models[1].Id);
        Assert.Equal("OpenAI", models[0].Vendor);
        Assert.Equal("Anthropic", models[1].Vendor);
    }

    // ── OAuth-aware credential resolution ────────────────────────────────────

    [Fact]
    public async Task DiscoverCopilotModelsAsync_StoredOAuthOnly_UsesStoredTokenAsBearer()
    {
        HttpRequestMessage? captured = null;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>("stored-oauth-token"));

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.Single(models);
        Assert.Equal("copilot/claude", models[0].Id);
        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal("stored-oauth-token", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_AllSourcesPresent_StoredOAuthWins()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "env-gh-token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "env-github-token");
        HttpRequestMessage? captured = null;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>("stored-oauth-token"));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("stored-oauth-token", captured!.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_BlankStoredOAuth_FallsThroughToGhToken()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "env-gh-token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "env-github-token");
        HttpRequestMessage? captured = null;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>("   "));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("env-gh-token", captured!.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_BlankStoredAndGhToken_FallsThroughToGithubToken()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "   ");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "env-github-token");
        HttpRequestMessage? captured = null;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>(null));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("env-github-token", captured!.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_NoTokenAnywhere_SendsNoCopilotRequest()
    {
        var requests = 0;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, _ => requests++),
            storedTokenLookup: _ => Task.FromResult<string?>(null));

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(models);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_StoredTokenRotation_ObservedOnSecondCall()
    {
        var storedToken = "stored-token-v1";
        HttpRequestMessage? captured = null;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>(storedToken));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("stored-token-v1", captured!.Headers.Authorization?.Parameter);

        // Rotate the stored token between the two discovery calls on the SAME service instance.
        storedToken = "stored-token-v2";
        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("stored-token-v2", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_StoredTokenRemoval_FallsBackToEnvironmentOnSecondCall()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "env-gh-token");
        var storedToken = "stored-token-v1";
        HttpRequestMessage? captured = null;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>(storedToken));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("stored-token-v1", captured!.Headers.Authorization?.Parameter);

        // Removal between calls: the same instance must observe the env fallback.
        storedToken = null;
        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("env-gh-token", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_LookupFailure_WarnsAndFallsBackToEnvironment()
    {
        // Distinct sentinels: none of these may ever surface in a log entry.
        const string EnvToken = "env-github-token-SENTINEL-4f1c9a";
        const string ExceptionDetail =
            "oauth failure detail with Bearer super-secret-credential-SENTINEL-b72e51";

        Environment.SetEnvironmentVariable("GITHUB_TOKEN", EnvToken);
        HttpRequestMessage? captured = null;
        var logger = new TestLogger<ModelDiscoveryService>();
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => throw new InvalidOperationException(ExceptionDetail),
            logger);

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        // Discovery proceeds with the environment fallback — the lookup failure is not fatal.
        Assert.Single(models);
        Assert.Equal("copilot/claude", models[0].Id);
        Assert.NotNull(captured);
        Assert.Equal(EnvToken, captured!.Headers.Authorization?.Parameter);

        // The FIXED credential-free warning must be emitted for the lookup failure.
        Assert.False(string.IsNullOrWhiteSpace(ModelDiscoveryService.OAuthLookupFailedWarning));
        var warning = Assert.Single(
            logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning
                 && e.Message == ModelDiscoveryService.OAuthLookupFailedWarning);

        // No exception object may ride along — its ToString() can carry credentials.
        Assert.Null(warning.Exception);
        Assert.All(logger.LogEntries, e => Assert.Null(e.Exception));

        // No credential-bearing content anywhere in the captured logs: not the lookup
        // exception message, not the fallback token value, not the outgoing header content.
        foreach (var entry in logger.LogEntries)
        {
            Assert.DoesNotContain(ExceptionDetail, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SENTINEL", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(EnvToken, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-credential", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", entry.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_CancellationBeforeLookup_ThrowsOperationCanceled()
    {
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody),
            storedTokenLookup: _ => throw new InvalidOperationException("lookup must never run"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => svc.DiscoverCopilotModelsAsync(cts.Token));
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_CancellationDuringLookup_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody),
            storedTokenLookup: ct =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<string?>("stored-oauth-token");
            });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => svc.DiscoverCopilotModelsAsync(cts.Token));
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_LookupCancelsThenReturnsNull_ThrowsInsteadOfEmptyList()
    {
        // No environment fallback exists, so a missing cancellation check after resolution
        // would take the no-token branch and return an EMPTY LIST for a cancelled caller.
        Assert.Null(Environment.GetEnvironmentVariable("GH_TOKEN"));
        Assert.Null(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

        using var cts = new CancellationTokenSource();
        var requests = 0;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, _ => requests++),
            storedTokenLookup: _ =>
            {
                cts.Cancel();
                return Task.FromResult<string?>(null);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.DiscoverCopilotModelsAsync(cts.Token));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_LookupCancelsThenThrowsNonCancellation_ThrowsInsteadOfEmptyList()
    {
        // The lookup cancels the caller token and then fails for a NON-cancellation reason:
        // the failure degrades to the (absent) environment chain, but the caller cancellation
        // must still terminate the call rather than produce an empty list.
        Assert.Null(Environment.GetEnvironmentVariable("GH_TOKEN"));
        Assert.Null(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

        using var cts = new CancellationTokenSource();
        var requests = 0;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, _ => requests++),
            storedTokenLookup: _ =>
            {
                cts.Cancel();
                throw new InvalidOperationException("lookup failed after cancellation");
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.DiscoverCopilotModelsAsync(cts.Token));
        Assert.Equal(0, requests);
    }

    // ── Per-account Copilot endpoint resolver ────────────────────────────────

    /// <summary>
    /// Creates a service with an EXPLICIT recording resolver: every invocation records the
    /// credential it was handed and answers with <paramref name="endpointBase"/>, so the
    /// "/models request goes to the resolved host" contract is observed without any network I/O.
    /// </summary>
    private static (ModelDiscoveryService Service, List<(string? Token, Uri Base)> Calls)
        CreateServiceWithRecordingResolver(
            HttpMessageHandler handler,
            Func<CancellationToken, Task<string?>>? storedTokenLookup,
            Uri endpointBase,
            ILogger<ModelDiscoveryService>? logger = null)
    {
        var calls = new List<(string? Token, Uri Base)>();
        CopilotEndpointResolver resolver = (token, _) =>
        {
            calls.Add((token, endpointBase));
            return Task.FromResult(endpointBase);
        };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        var service = new ModelDiscoveryService(
            logger ?? NullLogger<ModelDiscoveryService>.Instance,
            factory.Object, storedTokenLookup, resolver);
        return (service, calls);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_SendsModelsRequestToResolvedEndpointHost()
    {
        const string ResolvedBase = "https://api.business.githubcopilot.com/";
        const string ExpectedRequestUri = "https://api.business.githubcopilot.com/models";

        HttpRequestMessage? captured = null;
        var (svc, calls) = CreateServiceWithRecordingResolver(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>(null),
            endpointBase: new Uri(ResolvedBase));

        Environment.SetEnvironmentVariable("GH_TOKEN", "business-account-token");

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        // The /models catalogue is served by the per-account endpoint: the request host must be
        // the RESOLVED host, never the fixed api.githubcopilot.com default.
        Assert.NotNull(captured);
        Assert.Equal(ExpectedRequestUri, captured!.RequestUri?.ToString());
        Assert.Single(calls);
        Assert.Equal("business-account-token", calls[0].Token);
        Assert.Equal(new Uri(ResolvedBase), calls[0].Base);

        // Parsing on the resolved host is unchanged: the model body is read exactly as before.
        Assert.Single(models);
        Assert.Equal("copilot/claude", models[0].Id);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_ResolvesPathAgainstTrailingSlashBase()
    {
        HttpRequestMessage? captured = null;
        var (svc, _) = CreateServiceWithRecordingResolver(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>(null),
            endpointBase: new Uri("https://api.business.githubcopilot.com/"));

        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "tenant-token");

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        // "models" resolved against the base — one path segment, no double slash.
        Assert.Equal("https://api.business.githubcopilot.com/models", captured!.RequestUri?.ToString());
        Assert.Single(models);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_ResolvedEndpointKeepsAllExistingHeaders()
    {
        HttpRequestMessage? captured = null;
        var (svc, _) = CreateServiceWithRecordingResolver(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>("stored-oauth-token"),
            endpointBase: new Uri("https://api.business.githubcopilot.com/"));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("https://api.business.githubcopilot.com/models", captured!.RequestUri?.ToString());
        // ALL existing headers ride along unchanged on the per-account request.
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("stored-oauth-token", captured.Headers.Authorization?.Parameter);
        Assert.True(captured.Headers.TryGetValues("X-GitHub-Api-Version", out var versions));
        Assert.Contains("2025-04-01", versions!);
        AssertCopilotIntegrationHeader(captured);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_ResolverReceivesStoredOAuthToken()
    {
        const string Stored = "stored-oauth-SENTINEL-e1a2b3";
        var (svc, calls) = CreateServiceWithRecordingResolver(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody),
            storedTokenLookup: _ => Task.FromResult<string?>(Stored),
            endpointBase: new Uri("https://api.githubcopilot.com/"));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        // The resolver must receive EXACTLY the SELECTED credential — the stored OAuth token.
        Assert.Single(calls);
        Assert.Equal(Stored, calls[0].Token);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_ResolverReceivesGhToken()
    {
        const string EnvToken = "gh-env-SENTINEL-d4c5b6";
        Environment.SetEnvironmentVariable("GH_TOKEN", EnvToken);
        var (svc, calls) = CreateServiceWithRecordingResolver(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody),
            storedTokenLookup: _ => Task.FromResult<string?>(null),
            endpointBase: new Uri("https://api.githubcopilot.com/"));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.Single(calls);
        Assert.Equal(EnvToken, calls[0].Token);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_ResolverReceivesGithubToken()
    {
        const string EnvToken = "github-env-SENTINEL-7e8f9a";
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", EnvToken);
        var (svc, calls) = CreateServiceWithRecordingResolver(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody),
            storedTokenLookup: _ => Task.FromResult<string?>(null),
            endpointBase: new Uri("https://api.githubcopilot.com/"));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.Single(calls);
        Assert.Equal(EnvToken, calls[0].Token);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_ResolverReceivesSelectedTokenForEachChainPosition()
    {
        // One test per credential-chain position is authoritative; this aggregate pins the
        // END-TO-END chain contract: three sequential calls on ONE service instance, each
        // rotating the selected credential (stored → GH_TOKEN → GITHUB_TOKEN), each resolving
        // the endpoint for exactly the credential selected for THAT call.
        const string Stored = "chain-stored-SENTINEL-aa11";
        const string Gh = "chain-gh-SENTINEL-bb22";
        const string Github = "chain-github-SENTINEL-cc33";

        Environment.SetEnvironmentVariable("GH_TOKEN", Gh);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", Github);

        // The stored-token lookup answers through a MUTABLE holder so the credential chain can
        // be rotated between calls on the same service instance (mirrors stored-token rotation).
        string? stored = Stored;
        HttpRequestMessage? captured = null;
        var resolverCalls = new List<string?>();
        var svc = new ModelDiscoveryService(
            NullLogger<ModelDiscoveryService>.Instance, CreateFactory(
                new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody,
                    req => captured = req)).Object,
            getStoredAccessTokenAsync: _ => Task.FromResult<string?>(stored),
            resolveCopilotEndpointAsync: (token, _) =>
            {
                resolverCalls.Add(token);
                return Task.FromResult(new Uri("https://api.githubcopilot.com/"));
            });

        // Call 1: stored OAuth wins.
        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Stored, captured!.Headers.Authorization?.Parameter);

        // Rotate to the GH_TOKEN position: the stored lookup now answers null.
        stored = null;
        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Gh, captured.Headers.Authorization?.Parameter);

        // Rotate to the GITHUB_TOKEN position.
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Github, captured.Headers.Authorization?.Parameter);

        var recordedTokens = resolverCalls.Select(t => t ?? string.Empty).ToArray();
        Assert.Equal(3, recordedTokens.Length);
        Assert.Equal(new[] { Stored, Gh, Github }, recordedTokens);
    }

    // ── No-token short-circuit must precede endpoint resolution ─────────────

    [Fact]
    public async Task DiscoverCopilotModelsAsync_NoToken_ResolverNeverInvoked()
    {
        // No stored token, no GH_TOKEN, no GITHUB_TOKEN (cleared by the fixture ctor).
        var handlerRequests = 0;
        var logger = new TestLogger<ModelDiscoveryService>();
        var (svc, calls) = CreateServiceWithRecordingResolver(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, _ => handlerRequests++),
            storedTokenLookup: _ => Task.FromResult<string?>(null),
            endpointBase: new Uri("https://api.githubcopilot.com/"),
            logger: logger);

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        // The no-token short-circuit precedes endpoint resolution: the resolver must NEVER run.
        Assert.Empty(calls);
        Assert.Equal(0, handlerRequests);
        Assert.Empty(models);
        // ...and the documented warning identifies the no-token short-circuit.
        var warning = Assert.Single(
            logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning
                 && e.Message ==
                 "No stored GitHub OAuth token, GH_TOKEN or GITHUB_TOKEN set — skipping Copilot model discovery.");
        Assert.Null(warning.Exception);
    }

    // ── Cancellation rules at the endpoint-resolution seam ───────────────────

    /// <summary>
    /// A shared IHttpClientFactory stub over a fixed handler, matching the pattern of the
    /// CreateService helpers above but usable when a custom resolver is injected directly.
    /// </summary>
    private static Mock<IHttpClientFactory> CreateFactory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_ResolverCancelsCallerToken_PropagatesWithoutModelsRequest()
    {
        using var cts = new CancellationTokenSource();
        var requests = 0;
        CopilotEndpointResolver resolver = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new Uri("https://api.githubcopilot.com/"));
        };
        var svc = new ModelDiscoveryService(
            NullLogger<ModelDiscoveryService>.Instance, CreateFactory(
                new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, _ => requests++)).Object,
            getStoredAccessTokenAsync: _ => Task.FromResult<string?>("stored-oauth-token"),
            resolveCopilotEndpointAsync: resolver);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.DiscoverCopilotModelsAsync(cts.Token));

        // Rule (a): the cancellation raised BY the resolver terminates the caller and the
        // /models request is never sent.
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_CancelledAfterResolverReturns_ThrowsBeforeModelsRequest()
    {
        using var cts = new CancellationTokenSource();
        var requests = 0;
        var resolverInvocations = 0;
        CopilotEndpointResolver resolver = (_, _) =>
        {
            resolverInvocations++;
            cts.Cancel(); // cancellation arrives while the resolver runs
            return Task.FromResult(new Uri("https://api.githubcopilot.com/"));
        };
        var svc = new ModelDiscoveryService(
            NullLogger<ModelDiscoveryService>.Instance, CreateFactory(
                new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, _ => requests++)).Object,
            getStoredAccessTokenAsync: _ => Task.FromResult<string?>("stored-oauth-token"),
            resolveCopilotEndpointAsync: resolver);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.DiscoverCopilotModelsAsync(cts.Token));

        // Rule (b): even though the resolver returned a valid endpoint, the cancellation it
        // observed must terminate the caller BEFORE the /models request is sent.
        Assert.Equal(1, resolverInvocations);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_NonCancellationResolverErrorWhileCancelled_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var requests = 0;
        CopilotEndpointResolver resolver = (_, _) =>
        {
            cts.Cancel();
            throw new InvalidOperationException("resolver failed after cancellation");
        };
        var svc = new ModelDiscoveryService(
            NullLogger<ModelDiscoveryService>.Instance, CreateFactory(
                new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, _ => requests++)).Object,
            getStoredAccessTokenAsync: _ => Task.FromResult<string?>("stored-oauth-token"),
            resolveCopilotEndpointAsync: resolver);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.DiscoverCopilotModelsAsync(cts.Token));

        // Rule (c): classification uses the CALLER'S cancellation state at the catch point,
        // not the exception type — a non-cancellation resolver exception with an already-
        // cancelled caller is still a cancellation, NOT a "discovery failed" empty list.
        Assert.IsType<OperationCanceledException>(exception.GetBaseException());
        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_NonCancellationResolverError_LogsAndReturnsEmptyList()
    {
        const string Detail = "endpoint lookup failed for a NON-cancellation reason";
        var logger = new TestLogger<ModelDiscoveryService>();
        var requests = 0;
        Exception? resolverException = null;
        CopilotEndpointResolver resolver = (_, _) =>
        {
            resolverException = new InvalidOperationException(Detail);
            throw resolverException;
        };
        var svc = new ModelDiscoveryService(
            logger, CreateFactory(
                new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, _ => requests++)).Object,
            getStoredAccessTokenAsync: _ => Task.FromResult<string?>("stored-oauth-token"),
            resolveCopilotEndpointAsync: resolver);

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        // Rule (d): an ordinary resolver failure is logged and answered with the SAME
        // empty-list contract as every other discovery failure — and no request is sent.
        Assert.Empty(models);
        Assert.Equal(0, requests);
        var failure = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Error);
        Assert.Same(resolverException, failure.Exception);
        // The failure log identifies the discovery step; the resolver's own message rides on
        // the attached exception object (not interpolated into the log template).
        Assert.Equal("Failed to discover Copilot models.", failure.Message);
        Assert.Equal(Detail, failure.Exception!.Message);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_CancellationDuringHttp_ThrowsOperationCanceled()
    {
        // RESTORED (iteration-2): caller cancellation arriving DURING SendAsync must propagate
        // — never be swallowed into an empty "discovery failed" list. The resolver answers
        // normally first, so the cancellation fires inside the HTTP phase itself, exercising
        // the send/catch block and NOT the endpoint-resolution seam (covered separately by
        // the rule (a)-(c) tests above). Uses the same handler pattern as the pre-existing
        // CancellationDuringHttp test this file always carried.
        using var cts = new CancellationTokenSource();
        var resolverInvocations = 0;
        CopilotEndpointResolver countingResolver = (_, _) =>
        {
            resolverInvocations++;
            return Task.FromResult(new Uri("https://api.githubcopilot.com/"));
        };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(
                   new CancellingHttpMessageHandler(() => cts.Cancel()), disposeHandler: false));
        var svc = new ModelDiscoveryService(
            NullLogger<ModelDiscoveryService>.Instance, factory.Object,
            getStoredAccessTokenAsync: _ => Task.FromResult<string?>("stored-oauth-token"),
            resolveCopilotEndpointAsync: countingResolver);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.DiscoverCopilotModelsAsync(cts.Token));

        // The exact cancellation the transport observed — the caller's token — is the one
        // that surfaced; no successful /models result can be returned on this path.
        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Equal(1, resolverInvocations);
    }

    // ── Default-resolver fallback (structural, offline) ──────────────────────

    [Fact]
    public void Constructor_WithoutExplicitResolver_CarriesProviderDefaultResolver()
    {
        // The default-resolver fallback is a STRUCTURAL invariant: production passes null so
        // the service must carry the SDK delegate. A behavioural test would perform the real
        // per-account endpoint-resolution network I/O against api.github.com — forbidden by
        // this goal — so the seam is verified by delegate EQUALITY on the private field
        // instead: two method-group conversions to the same static method are distinct
        // delegate INSTANCES but equal per Delegate.Equals, which is exactly the contract
        // (the field must hold a delegate bound to the provider's static entry point).
        var field = typeof(ModelDiscoveryService).GetField(
            "_resolveCopilotEndpointAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var service = new ModelDiscoveryService(NullLogger<ModelDiscoveryService>.Instance);

        var carried = (CopilotEndpointResolver)field!.GetValue(service)!;
        Assert.Equal(
            (CopilotEndpointResolver)SharpCoder.Providers.ChatClientFactory.GetCopilotApiEndpointAsync,
            carried);
    }

    [Fact]
    public void Constructor_WithExplicitResolver_UsesExplicitResolverInsteadOfDefault()
    {
        // Control for the equality assertion above: the explicit stub must take precedence
        // over the provider default, so the '??' direction cannot be silently swapped. An
        // EQUAL-identity default would fail this control, so both tests together pin the
        // exact fallback wiring.
        var field = typeof(ModelDiscoveryService).GetField(
            "_resolveCopilotEndpointAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        CopilotEndpointResolver explicitResolver =
            (_, _) => Task.FromResult(new Uri("https://api.githubcopilot.com/"));
        var service = new ModelDiscoveryService(
            NullLogger<ModelDiscoveryService>.Instance,
            resolveCopilotEndpointAsync: explicitResolver);

        var carried = (CopilotEndpointResolver)field!.GetValue(service)!;
        Assert.Same(explicitResolver, carried);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_HttpAuthFailure_DoesNotFallBackToAnotherToken()
    {
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "env-github-token");
        HttpRequestMessage? captured = null;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "unauthorized", req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>("stored-oauth-token"));

        var models = await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        // The chosen non-blank credential received an authorization failure — NO retry with
        // another token: exactly one request was made with the stored OAuth credential, and
        // the existing empty-list behavior is retained.
        Assert.Empty(models);
        Assert.NotNull(captured);
        Assert.Equal("stored-oauth-token", captured!.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task DiscoverCopilotModelsAsync_SelectedCredentialIsPreservedUnchanged()
    {
        // GitCredentialResolver guarantees the raw selection — surrounding whitespace inside
        // a non-blank candidate is part of the credential and must reach the Bearer header.
        Environment.SetEnvironmentVariable("GH_TOKEN", "  padded-token  ");
        HttpRequestMessage? captured = null;
        var svc = CreateService(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody, req => captured = req),
            storedTokenLookup: _ => Task.FromResult<string?>(null));

        await svc.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("  padded-token  ", captured!.Headers.Authorization?.Parameter);
    }

    // ── Production DI wiring (Program.cs) ────────────────────────────────────

    [Fact]
    public async Task Program_AddModelDiscovery_WiresDiscoveryToUserService()
    {
        // No environment tokens exist (cleared by the fixture ctor), so the ONLY credential
        // source is the stored OAuth token reached through the production DI lookup. If
        // Program.AddModelDiscovery omitted its UserService lookup, the post-sign-in
        // assertions below would fail (no request, no Bearer header).
        Assert.Null(Environment.GetEnvironmentVariable("GH_TOKEN"));
        Assert.Null(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        // The fake transport is registered WITH DI, so the production-resolved discovery
        // singleton uses it — the request counter observes the real production instance.
        var handler = new CountingHttpMessageHandler(HttpStatusCode.OK, SingleCopilotModelBody);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(factory.Object);
        services.AddSingleton<IDbContextFactory<CopilotHiveDbContext>>(_ =>
        {
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(connection)
                .Options;
            return new SharedDbContextFactory(connection, options);
        });
        services.AddSingleton<UserService>();

        // Endpoint-resolver seam: registered BEFORE Program.AddModelDiscovery so the production
        // registration resolves THIS delegate. The stub performs no network I/O and records the
        // credential it was handed, which is how the production wiring's "resolver receives the
        // SELECTED token" contract is observed.
        var resolvedTokens = new List<string?>();
        CopilotEndpointResolver stubResolver = (token, _) =>
        {
            resolvedTokens.Add(token);
            return Task.FromResult(new Uri("https://api.githubcopilot.com/"));
        };
        services.AddSingleton<CopilotEndpointResolver>(stubResolver);

        Program.AddModelDiscovery(services);

        await using var provider = services.BuildServiceProvider();

        var userService = provider.GetRequiredService<UserService>();
        var discovery = provider.GetRequiredService<ModelDiscoveryService>();

        // Before any sign-in: no stored token and no env fallback — no Copilot request, and the
        // no-token path must not consult the endpoint resolver at all.
        Assert.Null(await userService.GetActiveAccessTokenAsync(TestContext.Current.CancellationToken));
        var withoutToken = await discovery.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(withoutToken);
        Assert.Equal(0, handler.RequestCount);
        Assert.Null(handler.LastAuthorizationParameter);
        Assert.Empty(handler.LastCopilotIntegrationIdValues);
        Assert.Empty(resolvedTokens);

        // After a GitHub sign-in the SAME production-resolved singleton must pick up the
        // stored token through the live UserService lookup.
        await userService.CreateOrUpdateUserAsync(
            "1", "octocat", null, null, null, "stored-oauth-token", null, null,
            TestContext.Current.CancellationToken);

        Assert.Same(discovery, provider.GetRequiredService<ModelDiscoveryService>());
        var models = await discovery.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);

        Assert.Single(models);
        Assert.Equal("copilot/claude", models[0].Id);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("stored-oauth-token", handler.LastAuthorizationParameter);
        // The production wiring handed the resolver the credential SELECTED for this call.
        Assert.Equal(["stored-oauth-token"], resolvedTokens);
        // The production DI-resolved singleton must send the exact integration header too.
        Assert.Equal(["copilot-developer-cli"], handler.LastCopilotIntegrationIdValues);
    }
}

/// <summary>
/// Shared in-memory SQLite <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TC}"/> for wiring tests, mirroring
/// the factory shape used by the <c>UserServiceTests</c> suite.
/// </summary>
internal sealed class SharedDbContextFactory : IDbContextFactory<CopilotHiveDbContext>
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CopilotHiveDbContext> _options;

    public SharedDbContextFactory(SqliteConnection connection, DbContextOptions<CopilotHiveDbContext> options)
    {
        _connection = connection;
        _options = options;

        using var ctx = new CopilotHiveDbContext(options);
        ctx.Database.EnsureCreated();
    }

    public CopilotHiveDbContext CreateDbContext() => new(_options);
}

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that counts requests and captures the outgoing
/// Authorization header.
/// </summary>
internal sealed class CountingHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;

    public CountingHttpMessageHandler(HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body = body;
    }

    public int RequestCount { get; private set; }

    public string? LastAuthorizationParameter { get; private set; }

    public string? LastAuthorizationScheme { get; private set; }

    public IReadOnlyList<string> LastCopilotIntegrationIdValues { get; private set; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
        LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
        LastCopilotIntegrationIdValues = request.Headers.TryGetValues("Copilot-Integration-Id", out var integration)
            ? integration.ToArray()
            : [];
        return Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that cancels the current token on send, simulating
/// cancellation arriving during the Copilot HTTP call.
/// </summary>
internal sealed class CancellingHttpMessageHandler : HttpMessageHandler
{
    private readonly Action _cancel;

    public CancellingHttpMessageHandler(Action cancel) => _cancel = cancel;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _cancel();
        cancellationToken.ThrowIfCancellationRequested();
        throw new System.Diagnostics.UnreachableException(
            "SendAsync must throw OperationCanceledException before returning.");
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
