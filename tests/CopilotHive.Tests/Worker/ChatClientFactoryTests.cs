extern alias WorkerAssembly;

using System.Net;
using WorkerAssembly::CopilotHive.SDK;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="ChatClientFactory.ParseProviderAndModel"/>: verifies
/// that provider prefixes are extracted correctly for every known provider, that
/// plain model names and edge-case inputs are handled without throwing, and that
/// the returned tuple always contains the exact expected provider and model values.
/// </summary>
public sealed class ChatClientFactoryTests
{
    // ── Known provider prefixes ──────────────────────────────────────────────

    #region copilot/ prefix — extracts "copilot" provider and bare model name

    /// <summary>
    /// When the model string begins with the "copilot/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "copilot" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_CopilotPrefix_ReturnsCopilotProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot/claude-sonnet-4.6");

        Assert.Equal("copilot", provider);
        Assert.Equal("claude-sonnet-4.6", model);
    }

    #endregion

    #region ollama-cloud/ prefix — extracts "ollama-cloud" provider and bare model name

    /// <summary>
    /// When the model string begins with the "ollama-cloud/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "ollama-cloud" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_OllamaCloudPrefix_ReturnsOllamaCloudProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("ollama-cloud/gpt-oss:120b");

        Assert.Equal("ollama-cloud", provider);
        Assert.Equal("gpt-oss:120b", model);
    }

    #endregion

    #region ollama-local/ prefix — extracts "ollama-local" provider and bare model name

    /// <summary>
    /// When the model string begins with the "ollama-local/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "ollama-local" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_OllamaLocalPrefix_ReturnsOllamaLocalProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("ollama-local/llama3");

        Assert.Equal("ollama-local", provider);
        Assert.Equal("llama3", model);
    }

    #endregion

    #region github/ prefix — extracts "github" provider and bare model name

    /// <summary>
    /// When the model string begins with the "github/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "github" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_GithubPrefix_ReturnsGithubProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("github/openai/gpt-4.1");

        Assert.Equal("github", provider);
        Assert.Equal("openai/gpt-4.1", model);
    }

    #endregion

    // ── No prefix / plain model name ────────────────────────────────────────

    #region No prefix — returns default provider and the full model string as model

    /// <summary>
    /// When the model string contains no recognised provider prefix (no slash,
    /// or an unrecognised prefix), <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must return the default provider from the <c>LLM_PROVIDER</c> environment
    /// variable (defaulting to "copilot") and the original model string as the model.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_PlainModelName_ReturnsDefaultProviderAndFullModelName()
    {
        var expectedProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        var (provider, model) = ChatClientFactory.ParseProviderAndModel("gpt-4o");

        Assert.Equal(expectedProvider, provider);
        Assert.Equal("gpt-4o", model);
    }

    #endregion

    // ── Edge cases ───────────────────────────────────────────────────────────

    #region Empty string — returns default provider and null model without throwing

    /// <summary>
    /// When the input is an empty string, <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must not throw and must return the default provider (from <c>LLM_PROVIDER</c> or
    /// "copilot") with a <see langword="null"/> model, identical to the behaviour for
    /// a <see langword="null"/> input.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_EmptyString_ReturnsDefaultProviderAndNullModel()
    {
        var expectedProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        var (provider, model) = ChatClientFactory.ParseProviderAndModel(string.Empty);

        Assert.Equal(expectedProvider, provider);
        Assert.Null(model);
    }

    #endregion

    #region Prefix with no model after slash — returns known provider and empty model string

    /// <summary>
    /// When the input is a known provider prefix followed immediately by a slash but no
    /// model name (e.g. "copilot/"), <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must extract the provider correctly and return an empty string as the model, because
    /// <c>Substring(slashIndex + 1)</c> on "copilot/" yields "".
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_KnownPrefixWithNoModel_ReturnsProviderAndEmptyModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot/");

        Assert.Equal("copilot", provider);
        Assert.Equal(string.Empty, model);
    }

    #endregion

    #region Double slash — returns known provider and the remainder including leading slash

    /// <summary>
    /// When the input contains a double slash (e.g. "copilot//model"), the first slash is
    /// used to split off the prefix. <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must return the known provider and the remainder after the first slash as the model,
    /// which will begin with an additional slash character.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_DoubleSlash_ReturnsProviderAndRemainderAfterFirstSlash()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot//model");

        Assert.Equal("copilot", provider);
        Assert.Equal("/model", model);
    }

    #endregion

    // ── Null input ───────────────────────────────────────────────────────────

    #region Null input — returns default provider and null model without throwing

    /// <summary>
    /// When the input is <see langword="null"/>, <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must not throw and must return the default provider (from <c>LLM_PROVIDER</c> or
    /// "copilot") paired with a <see langword="null"/> model.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_NullInput_ReturnsDefaultProviderAndNullModel()
    {
        var expectedProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        var (provider, model) = ChatClientFactory.ParseProviderAndModel(null);

        Assert.Equal(expectedProvider, provider);
        Assert.Null(model);
    }

    #endregion
}

/// <summary>
/// Tests for <see cref="ChatClientFactory.ParseProviderModelAndReasoning"/>: verifies
/// that reasoning effort is correctly extracted from the last colon-separated segment
/// and that Ollama model tags (e.g. :120b) are not mistaken for reasoning levels.
/// </summary>
public sealed class ChatClientFactoryReasoningTests
{
    [Fact]
    public void ParseReasoning_HighSuffix_ExtractsReasoningEffort()
    {
        var (provider, model, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning("copilot/claude-sonnet-4.6:high");

        Assert.Equal("copilot", provider);
        Assert.Equal("claude-sonnet-4.6", model);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.High, reasoning);
    }

    [Fact]
    public void ParseReasoning_LowSuffix_ExtractsReasoningEffort()
    {
        var (_, model, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning("copilot/gpt-5.4:low");

        Assert.Equal("gpt-5.4", model);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.Low, reasoning);
    }

    [Fact]
    public void ParseReasoning_ExtraHighSuffix_ExtractsReasoningEffort()
    {
        var (_, model, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning("copilot/gpt-5.4:extra_high");

        Assert.Equal("gpt-5.4", model);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh, reasoning);
    }

    [Fact]
    public void ParseReasoning_NoneSuffix_ExtractsReasoningNone()
    {
        var (_, model, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning("copilot/claude-sonnet-4.6:none");

        Assert.Equal("claude-sonnet-4.6", model);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.None, reasoning);
    }

    [Fact]
    public void ParseReasoning_OllamaModelTag_NotMistakenForReasoning()
    {
        var (provider, model, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning("ollama-cloud/gpt-oss:120b");

        Assert.Equal("ollama-cloud", provider);
        Assert.Equal("gpt-oss:120b", model);
        Assert.Null(reasoning);
    }

    [Fact]
    public void ParseReasoning_OllamaTagWithReasoning_ExtractsLastSegment()
    {
        var (provider, model, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning("ollama-cloud/gpt-oss:120b:medium");

        Assert.Equal("ollama-cloud", provider);
        Assert.Equal("gpt-oss:120b", model);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.Medium, reasoning);
    }

    [Fact]
    public void ParseReasoning_NoSuffix_ReturnsNullReasoning()
    {
        var (provider, model, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning("copilot/claude-sonnet-4.6");

        Assert.Equal("copilot", provider);
        Assert.Equal("claude-sonnet-4.6", model);
        Assert.Null(reasoning);
    }

    [Fact]
    public void ParseReasoning_NullInput_ReturnsNullReasoning()
    {
        var (_, model, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning(null);

        Assert.Null(model);
        Assert.Null(reasoning);
    }

    [Fact]
    public void ParseReasoning_CaseInsensitive_ExtractsCorrectly()
    {
        var (_, model, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning("copilot/gpt-5.4:HIGH");

        Assert.Equal("gpt-5.4", model);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.High, reasoning);
    }

    [Fact]
    public void ParseReasoning_PlainModelNoProvider_ExtractsReasoning()
    {
        var (_, model, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning("gpt-5.4:medium");

        Assert.Equal("gpt-5.4", model);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.Medium, reasoning);
    }
}

/// <summary>
/// Tests for <see cref="ChatClientFactory.CopilotResponsesHandler"/> streaming behaviour:
/// verifies that SSE (text/event-stream) responses pass through intact without being
/// read or modified, while non-streaming JSON responses are still processed for turn
/// history tracking.
/// </summary>
public sealed class CopilotResponsesHandlerTests
{
    /// <summary>
    /// Verifies that when the Copilot API returns a streaming response (content-type
    /// text/event-stream), <see cref="ChatClientFactory.CopilotResponsesHandler"/>
    /// returns it immediately without reading or modifying the body, so the SSE stream
    /// can be consumed directly by the OpenAI SDK's streaming parser.
/// </summary>
    [Fact]
    public async Task StreamingResponse_PassesThrough_Intact()
    {
        const string sseBody = "event: done\ndata: {}\n\n";
        var handler = new ChatClientFactory.CopilotResponsesHandler(
            new StreamingFakeResponseHandler(sseBody));
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // The content-type must remain text/event-stream (not overwritten to JSON)
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // The body must NOT have been read or replaced with JSON
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("event: done", body);
    }

    /// <summary>
    /// Verifies that non-streaming (application/json) responses are still fully processed:
    /// the handler reads the body, parses it for turn history accumulation, and
    /// re-wraps it as StringContent.
/// </summary>
    [Fact]
    public async Task NonStreamingResponse_StillProcessed()
    {
        const string jsonBody = """{"output":[{"type":"message","content":[{"type":"output_text","text":"Hello"}]}]}""";
        var handler = new ChatClientFactory.CopilotResponsesHandler(
            new JsonFakeResponseHandler(jsonBody));
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Non-streaming response should be returned as JSON StringContent
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(body);
    }

    /// <summary>Fake inner handler that returns a streaming SSE response.</summary>
    private sealed class StreamingFakeResponseHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StreamingFakeResponseHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "text/event-stream"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>Fake inner handler that returns a JSON response.</summary>
    private sealed class JsonFakeResponseHandler : HttpMessageHandler
    {
        private readonly string _body;

        public JsonFakeResponseHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
