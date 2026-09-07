using System.Net;
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

    private static ModelDiscoveryService CreateService(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new ModelDiscoveryService(NullLogger<ModelDiscoveryService>.Instance, factory.Object);
    }

    private static ModelDiscoveryService CreateService(
        HttpMessageHandler handler,
        Func<CancellationToken, Task<string?>>? storedTokenLookup)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new ModelDiscoveryService(
            NullLogger<ModelDiscoveryService>.Instance, factory.Object, storedTokenLookup);
    }

    private static ModelDiscoveryService CreateService(
        HttpMessageHandler handler,
        Func<CancellationToken, Task<string?>>? storedTokenLookup,
        ILogger<ModelDiscoveryService> logger)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new ModelDiscoveryService(logger, factory.Object, storedTokenLookup);
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

    [Fact]
    public async Task DiscoverCopilotModelsAsync_CancellationDuringHttp_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var svc = CreateService(
            new CancellingHttpMessageHandler(() => cts.Cancel()),
            storedTokenLookup: _ => Task.FromResult<string?>("stored-oauth-token"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.DiscoverCopilotModelsAsync(cts.Token));
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
        Program.AddModelDiscovery(services);

        await using var provider = services.BuildServiceProvider();

        var userService = provider.GetRequiredService<UserService>();
        var discovery = provider.GetRequiredService<ModelDiscoveryService>();

        // Before any sign-in: no stored token and no env fallback — no Copilot request.
        Assert.Null(await userService.GetActiveAccessTokenAsync(TestContext.Current.CancellationToken));
        var withoutToken = await discovery.DiscoverCopilotModelsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(withoutToken);
        Assert.Equal(0, handler.RequestCount);
        Assert.Null(handler.LastAuthorizationParameter);

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

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
        LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
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
