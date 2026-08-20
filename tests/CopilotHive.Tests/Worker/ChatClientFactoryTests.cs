using CopilotHive.Shared.AI;

using Microsoft.Extensions.AI;

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

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
/// Tests confirming that model strings are parsed into provider + plain model name only.
/// Reasoning effort is never derived from a model-name suffix: any trailing colon segment
/// (including Ollama tags like <c>:120b</c> and legacy levels like <c>:high</c>) stays part
/// of the model name.
/// </summary>
public sealed class ChatClientFactoryReasoningTests
{
    [Fact]
    public void ParseProviderAndModel_LegacyHighSuffix_IsNotStripped()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot/claude-sonnet-4.6:high");

        Assert.Equal("copilot", provider);
        Assert.Equal("claude-sonnet-4.6:high", model);
    }

    [Fact]
    public void ParseProviderAndModel_OllamaModelTag_IsPreserved()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("ollama-cloud/gpt-oss:120b");

        Assert.Equal("ollama-cloud", provider);
        Assert.Equal("gpt-oss:120b", model);
    }

    [Fact]
    public void ParseProviderAndModel_PlainModel_ReturnsModelUnchanged()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot/claude-sonnet-4.6");

        Assert.Equal("copilot", provider);
        Assert.Equal("claude-sonnet-4.6", model);
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

/// <summary>
/// Tests for <see cref="ChatClientFactory.OwnedCopilotChatClient"/>: the decorator that owns
/// disposal of both the inner <see cref="IChatClient"/> and the injected <see cref="HttpClient"/>,
/// and for the <see cref="ChatClientFactory.CreateCopilotClientForTestFull"/> test seam that builds
/// the full production Copilot stack (handler chain + owned client).
/// </summary>
/// <remarks>
/// Every test in this class is free of process-wide state mutation, so the class stays safely
/// parallelizable. The behavioural proof that the seam has no token dependency lives here too
/// (<see cref="OwnedCopilotChatClientTests.CreateCopilotClientForTestFull_ContributesNoCredentialMaterial"/>)
/// and needs no environment mutation; the complementary cleared-environment variant lives in
/// <see cref="CopilotClientSeamTokenIndependenceTests"/>, which joins the serialized
/// <c>EnvVarMutation</c> collection precisely because it does mutate the process environment.
/// </remarks>
public sealed class OwnedCopilotChatClientTests
{
    // ── Test doubles ────────────────────────────────────────────────────────

    /// <summary>A single recorded call to one of the two chat entry points.</summary>
    private sealed record ChatCall(
        IEnumerable<ChatMessage> Messages, ChatOptions? Options, CancellationToken CancellationToken);

    /// <summary>
    /// Records every call to every entry point <b>independently</b>, so a decorator that drops one
    /// entry point (or answers it without consulting the inner client) cannot hide behind another
    /// entry point's recording.
    /// </summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatCall> UnaryCalls { get; } = [];
        public List<ChatCall> StreamingCalls { get; } = [];
        public List<(Type ServiceType, object? ServiceKey)> ServiceRequests { get; } = [];

        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("recording", new Uri("https://example.invalid"), "test-model");

        /// <summary>The exact instance <see cref="GetResponseAsync"/> hands back.</summary>
        public ChatResponse UnaryResponse { get; } = new(new ChatMessage(ChatRole.Assistant, "unary-response"));

        /// <summary>The exact update instances <see cref="GetStreamingResponseAsync"/> yields, in order.</summary>
        public ChatResponseUpdate[] StreamingUpdates { get; } =
        [
            new(ChatRole.Assistant, "streaming-update-1"),
            new(ChatRole.Assistant, "streaming-update-2"),
        ];

        /// <summary>A sentinel service instance returned only for a specific type+key pair.</summary>
        public object KeyedService { get; } = new();

        /// <summary>The key <see cref="KeyedService"/> is registered under.</summary>
        public static readonly object ServiceKeySentinel = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            UnaryCalls.Add(new ChatCall(messages, options, cancellationToken));
            return Task.FromResult(UnaryResponse);
        }

        // Deliberately NOT an async iterator: the call must be recorded when the method is invoked,
        // so a decorator that returns an unrelated (or empty) stream is still observed as "did not
        // forward" rather than silently producing no recording at all.
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            StreamingCalls.Add(new ChatCall(messages, options, cancellationToken));
            return EnumerateAsync();
        }

        private async IAsyncEnumerable<ChatResponseUpdate> EnumerateAsync()
        {
            foreach (var update in StreamingUpdates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ServiceRequests.Add((serviceType, serviceKey));

            if (ReferenceEquals(serviceKey, ServiceKeySentinel))
                return serviceType == typeof(object) ? KeyedService : null;

            if (serviceKey is not null) return null;
            if (serviceType == typeof(ChatClientMetadata)) return Metadata;
            if (serviceType == typeof(RecordingChatClient)) return this;
            return null;
        }

        public void Dispose()
        {
            DisposeCount++;
            Disposed = true;
        }
    }

    /// <summary>
    /// Throws a freshly-created exception from a distinctly named, non-inlined frame, recording the
    /// instance first. This makes BOTH halves of the propagation contract testable:
    /// <list type="bullet">
    /// <item><description><b>Identity</b> — <see cref="Thrown"/> can be compared with
    /// <c>Assert.Same</c>, so a re-wrapped or substituted exception fails.</description></item>
    /// <item><description><b>Stack preservation</b> — <see cref="OriginFrame"/> must still be on the
    /// propagated exception's stack. This is exactly what the production
    /// <c>ExceptionDispatchInfo</c> rethrow (and the bare <c>throw;</c> in the construction-failure
    /// path) guarantees; a stack-resetting <c>throw ex;</c> would drop this frame.</description></item>
    /// </list>
    /// The exception is created <em>at</em> the throw site rather than pre-created and stored,
    /// because <c>throw storedException;</c> resets the stack trace before the production code ever
    /// observes it, which would make the stack assertion vacuous.
    /// </summary>
    private sealed class SentinelThrower
    {
        private readonly string _message;

        public SentinelThrower(string message) => _message = message;

        /// <summary>The exact exception instance that was thrown, or null if not yet thrown.</summary>
        public Exception? Thrown { get; private set; }

        /// <summary>The frame name that must survive on the propagated exception's stack.</summary>
        public static string OriginFrame => nameof(ThrowFromSentinelOrigin);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void ThrowFromSentinelOrigin()
        {
            var ex = new InvalidOperationException(_message);
            Thrown = ex;
            throw ex;
        }
    }

    /// <summary>
    /// Asserts that <paramref name="thrown"/> is the very instance the sentinel raised AND that the
    /// sentinel's original throw site is still on its stack.
    /// </summary>
    private static void AssertOriginalFailureSurvived(SentinelThrower sentinel, Exception thrown)
    {
        Assert.NotNull(sentinel.Thrown);
        Assert.Same(sentinel.Thrown, thrown);
        Assert.NotNull(thrown.StackTrace);
        Assert.Contains(SentinelThrower.OriginFrame, thrown.StackTrace);
    }

    /// <summary>An inner client whose disposal throws from a sentinel origin frame.</summary>
    private sealed class ThrowingDisposalChatClient : IChatClient
    {
        public SentinelThrower Sentinel { get; }

        public ThrowingDisposalChatClient(string message = "inner disposal failed")
            => Sentinel = new SentinelThrower(message);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Sentinel.ThrowFromSentinelOrigin();
    }

    /// <summary>
    /// Terminal handler whose disposal records the attempt and then throws from a sentinel origin
    /// frame, so "cleanup was attempted" and "cleanup failed" can both be asserted.
    /// </summary>
    private sealed class ThrowingDisposalTerminalHandler : HttpMessageHandler
    {
        public SentinelThrower Sentinel { get; }

        public ThrowingDisposalTerminalHandler(string message = "transport disposal failed")
            => Sentinel = new SentinelThrower(message);

        /// <summary>Set before the failure is raised, proving disposal was actually attempted.</summary>
        public bool DisposeAttempted { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            DisposeAttempted = true;
            Sentinel.ThrowFromSentinelOrigin();
        }
    }

    /// <summary>Tracks whether the terminal handler was disposed (chain disposal reached it).</summary>
    private sealed class TrackingTerminalHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Terminal handler that records the outgoing request body <em>and</em> its
    /// <c>Authorization</c> header, then answers with a canned JSON body.
    /// </summary>
    private sealed class ProbeTerminalHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public ProbeTerminalHandler(string responseBody = "{}") => _responseBody = responseBody;

        public string? LastBody { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuthorization { get; private set; }
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastAuthorization = request.Headers.Authorization;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An inner client that actually sends a raw request through the supplied
    /// <see cref="HttpClient"/>, so the production handler chain inside it is exercised for real.
    /// </summary>
    private sealed class HttpProbeChatClient : IChatClient
    {
        private readonly HttpClient _httpClient;

        public HttpProbeChatClient(HttpClient httpClient) => _httpClient = httpClient;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
            {
                Content = new StringContent(
                    """{"model":"gpt-5","reasoning":{"effort":"extra_high"},"input":[]}""",
                    Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // ── Delegation ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GetResponseAsync</c> forwards the exact messages, options and cancellation token to the
    /// inner client and hands back the inner client's exact response — and does not touch the
    /// streaming entry point.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_ForwardsEveryArgumentAndResultToInner()
    {
        var inner = new RecordingChatClient();
        using var wrapper = new ChatClientFactory.OwnedCopilotChatClient(
            inner, new HttpClient(new TrackingTerminalHandler()));

        var messages = new List<ChatMessage> { new(ChatRole.User, "unary-question") };
        var options = new ChatOptions { ModelId = "gpt-5" };
        using var cts = new CancellationTokenSource();

        var response = await wrapper.GetResponseAsync(messages, options, cts.Token);

        // Exactly one unary call, and the streaming path was not used as a substitute.
        var call = Assert.Single(inner.UnaryCalls);
        Assert.Empty(inner.StreamingCalls);

        Assert.Same(messages, call.Messages);
        Assert.Same(options, call.Options);
        Assert.Equal(cts.Token, call.CancellationToken);
        Assert.NotEqual(CancellationToken.None, call.CancellationToken);

        // The inner client's exact response instance is handed back unmodified.
        Assert.Same(inner.UnaryResponse, response);
    }

    /// <summary>
    /// <c>GetStreamingResponseAsync</c> forwards the exact messages, options and cancellation token
    /// and yields the inner client's exact update sequence — and does not touch the unary entry
    /// point. A decorator that returned an unrelated or empty stream would fail here.
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_ForwardsEveryArgumentAndUpdatesFromInner()
    {
        var inner = new RecordingChatClient();
        using var wrapper = new ChatClientFactory.OwnedCopilotChatClient(
            inner, new HttpClient(new TrackingTerminalHandler()));

        var messages = new List<ChatMessage> { new(ChatRole.User, "streaming-question") };
        var options = new ChatOptions { ModelId = "gpt-5-streaming" };
        using var cts = new CancellationTokenSource();

        var received = new List<ChatResponseUpdate>();
        await foreach (var update in wrapper.GetStreamingResponseAsync(messages, options, cts.Token))
            received.Add(update);

        // Exactly one streaming call, and the unary path was not used as a substitute.
        var call = Assert.Single(inner.StreamingCalls);
        Assert.Empty(inner.UnaryCalls);

        Assert.Same(messages, call.Messages);
        Assert.Same(options, call.Options);
        Assert.Equal(cts.Token, call.CancellationToken);
        Assert.NotEqual(CancellationToken.None, call.CancellationToken);

        // The inner client's exact updates, in order — not a substituted or empty stream.
        Assert.Equal(inner.StreamingUpdates.Length, received.Count);
        for (var i = 0; i < received.Count; i++)
            Assert.Same(inner.StreamingUpdates[i], received[i]);
    }

    /// <summary>
    /// <c>GetService</c> forwards both the service type and a non-null service key verbatim, and
    /// returns the inner client's exact answer for each.
    /// </summary>
    [Fact]
    public void GetService_ForwardsTypeAndNonNullKeyToInner()
    {
        var inner = new RecordingChatClient();
        using var wrapper = new ChatClientFactory.OwnedCopilotChatClient(
            inner, new HttpClient(new TrackingTerminalHandler()));

        // Keyless resolution.
        Assert.Same(inner.Metadata, wrapper.GetService(typeof(ChatClientMetadata)));
        Assert.Same(inner, wrapper.GetService(typeof(RecordingChatClient)));

        // Keyed resolution: the sentinel key must reach the inner client unchanged, and the
        // keyed answer must come back unchanged.
        Assert.Same(inner.KeyedService, wrapper.GetService(typeof(object), RecordingChatClient.ServiceKeySentinel));

        // A key that the inner client does not recognise still round-trips (null answer forwarded).
        Assert.Null(wrapper.GetService(typeof(string), "unknown-key"));

        Assert.Equal(
            [
                (typeof(ChatClientMetadata), (object?)null),
                (typeof(RecordingChatClient), (object?)null),
                (typeof(object), RecordingChatClient.ServiceKeySentinel),
                (typeof(string), (object?)"unknown-key"),
            ],
            inner.ServiceRequests);
    }

    /// <summary>
    /// The decorator declares no <c>Metadata</c> property: <see cref="IChatClient"/> has none, and
    /// callers must resolve metadata through <c>GetService(typeof(ChatClientMetadata))</c>.
    /// </summary>
    [Fact]
    public void OwnedCopilotChatClient_DeclaresNoMetadataProperty()
    {
        Assert.Null(typeof(ChatClientFactory.OwnedCopilotChatClient).GetProperty(
            "Metadata",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static));
    }

    // ── Dispose semantics ───────────────────────────────────────────────────

    /// <summary>Disposing the wrapper disposes both the inner client and the owned HttpClient.</summary>
    [Fact]
    public void Dispose_DisposesInnerAndOwnedHttpClient()
    {
        var inner = new RecordingChatClient();
        var terminal = new TrackingTerminalHandler();
        using var wrapper = new ChatClientFactory.OwnedCopilotChatClient(inner, new HttpClient(terminal));

        wrapper.Dispose();

        Assert.True(inner.Disposed);
        Assert.True(terminal.Disposed, "Disposing the owned HttpClient must reach the handler chain.");
    }

    /// <summary>
    /// Dispose is idempotent: the inner client and the HttpClient (via its terminal handler)
    /// are each disposed <b>exactly once</b> even when Dispose is called multiple times.
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotent()
    {
        var inner = new RecordingChatClient();
        var terminal = new TrackingTerminalHandler();
        var wrapper = new ChatClientFactory.OwnedCopilotChatClient(inner, new HttpClient(terminal));

        wrapper.Dispose();
        wrapper.Dispose();
        wrapper.Dispose();

        Assert.True(inner.Disposed);
        Assert.True(terminal.Disposed);
        // The Interlocked guard in OwnedCopilotChatClient.Dispose must ensure each cleanup
        // runs exactly once, even across repeated calls.
        Assert.Equal(1, inner.DisposeCount);
        Assert.Equal(1, terminal.DisposeCount);
    }

    /// <summary>
    /// An inner-client disposal failure must not prevent the owned HttpClient from being disposed,
    /// and the original exception <b>instance</b> — with its original stack — must surface.
    /// </summary>
    [Fact]
    public void Dispose_InnerThrows_StillDisposesHttpClientAndRethrowsSameInstance()
    {
        var inner = new ThrowingDisposalChatClient();
        var terminal = new TrackingTerminalHandler();
        var wrapper = new ChatClientFactory.OwnedCopilotChatClient(inner, new HttpClient(terminal));

        var thrown = Assert.Throws<InvalidOperationException>(wrapper.Dispose);

        // Identity + original stack: a re-wrapped, substituted or stack-resetting rethrow fails.
        AssertOriginalFailureSurvived(inner.Sentinel, thrown);
        Assert.Equal("inner disposal failed", thrown.Message);

        Assert.True(terminal.Disposed, "The owned HttpClient must still be disposed when the inner client throws.");
    }

    /// <summary>
    /// A transport-only failure must still surface — as the same instance, with its original
    /// stack — when the inner client disposes cleanly.
    /// </summary>
    [Fact]
    public void Dispose_HttpClientThrows_RethrowsSameInstanceAndStillDisposesInner()
    {
        var inner = new RecordingChatClient();
        var terminal = new ThrowingDisposalTerminalHandler();
        var wrapper = new ChatClientFactory.OwnedCopilotChatClient(inner, new HttpClient(terminal));

        var thrown = Assert.Throws<InvalidOperationException>(wrapper.Dispose);

        AssertOriginalFailureSurvived(terminal.Sentinel, thrown);
        Assert.Equal("transport disposal failed", thrown.Message);
        Assert.True(inner.Disposed, "The inner client must still be disposed when the transport throws.");
    }

    /// <summary>
    /// When BOTH cleanups fail, both original exception <b>instances</b> must be carried by the
    /// <see cref="AggregateException"/>, with the inner client's failure first.
    /// </summary>
    [Fact]
    public void Dispose_BothThrow_AggregatesBothOriginalInstances()
    {
        var inner = new ThrowingDisposalChatClient();
        var terminal = new ThrowingDisposalTerminalHandler();
        var wrapper = new ChatClientFactory.OwnedCopilotChatClient(inner, new HttpClient(terminal));

        var thrown = Assert.Throws<AggregateException>(wrapper.Dispose);

        Assert.Equal(2, thrown.InnerExceptions.Count);

        // Both original instances, in order (inner/primary first), with their stacks intact.
        AssertOriginalFailureSurvived(inner.Sentinel, thrown.InnerExceptions[0]);
        AssertOriginalFailureSurvived(terminal.Sentinel, thrown.InnerExceptions[1]);
        Assert.Equal("inner disposal failed", thrown.InnerExceptions[0].Message);
        Assert.Equal("transport disposal failed", thrown.InnerExceptions[1].Message);
    }

    /// <summary>Once disposal has run (even with a failure), later calls are no-ops.</summary>
    [Fact]
    public void Dispose_AfterFailure_IsIdempotent()
    {
        var inner = new ThrowingDisposalChatClient();
        var wrapper = new ChatClientFactory.OwnedCopilotChatClient(
            inner, new HttpClient(new ThrowingDisposalTerminalHandler()));

        Assert.Throws<AggregateException>(wrapper.Dispose);
        wrapper.Dispose(); // must not throw again
    }

    // ── CreateCopilotClientForTestFull ──────────────────────────────────────

    /// <summary>
    /// The test seam builds the FULL production stack: the owned client over the production handler
    /// chain, with the injectable inner-client factory receiving the constructed HttpClient.
    /// </summary>
    [Fact]
    public void CreateCopilotClientForTestFull_BuildsFullProductionStack()
    {
        HttpClient? factoryHttpClient = null;
        var terminal = new TrackingTerminalHandler();
        var inner = new RecordingChatClient();

        var client = ChatClientFactory.CreateCopilotClientForTestFull(
            useResponsesApi: false, terminal,
            httpClient =>
            {
                factoryHttpClient = httpClient;
                return inner;
            });

        Assert.IsType<ChatClientFactory.OwnedCopilotChatClient>(client);
        Assert.NotNull(factoryHttpClient);
        Assert.Equal(Timeout.InfiniteTimeSpan, factoryHttpClient!.Timeout);

        // Disposing the wrapper disposes both the inner client and the owned HttpClient.
        client.Dispose();
        Assert.True(inner.Disposed);
        Assert.True(terminal.Disposed);

        // Dispose is idempotent even after a successful disposal.
        client.Dispose();
        Assert.Equal(1, inner.DisposeCount);
        Assert.Equal(1, terminal.DisposeCount);
    }

    /// <summary>
    /// The full chain (resilience → Copilot handler → reasoning-effort mapping with
    /// <see cref="ChatClientFactory.CopilotExtraHighMapping"/>) is present and live: a real request
    /// sent through the owned HttpClient reaches the terminal with <c>"xhigh"</c> for ExtraHigh.
    /// </summary>
    [Fact]
    public async Task CreateCopilotClientForTestFull_ExtraHigh_ReachesTerminalAsXhigh()
    {
        var terminal = new ProbeTerminalHandler("""{"output":[]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTestFull(
            useResponsesApi: true, terminal, httpClient => new HttpProbeChatClient(httpClient));

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("xhigh", sent["reasoning"]!["effort"]!.GetValue<string>());
    }

    /// <summary>
    /// The seam has no token dependency, proven without mutating any process-wide state: it
    /// contributes <b>no credential material at all</b>. The <see cref="HttpClient"/> it hands to
    /// the factory carries no default headers (in particular no <c>Authorization</c>) and no
    /// <see cref="HttpClient.BaseAddress"/>, and a real request driven through the entire chain
    /// reaches the terminal handler without an <c>Authorization</c> header. Nothing in the seam can
    /// therefore be reading — let alone requiring — <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c>.
    /// </summary>
    [Fact]
    public async Task CreateCopilotClientForTestFull_ContributesNoCredentialMaterial()
    {
        var terminal = new ProbeTerminalHandler("""{"output":[]}""");
        HttpClient? factoryHttpClient = null;

        using var client = ChatClientFactory.CreateCopilotClientForTestFull(
            useResponsesApi: true, terminal,
            httpClient =>
            {
                factoryHttpClient = httpClient;
                return new HttpProbeChatClient(httpClient);
            });

        Assert.NotNull(factoryHttpClient);
        Assert.Null(factoryHttpClient!.DefaultRequestHeaders.Authorization);
        Assert.Empty(factoryHttpClient.DefaultRequestHeaders);
        Assert.Null(factoryHttpClient.BaseAddress);

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(terminal.LastBody);
        Assert.Null(terminal.LastAuthorization);
    }

    /// <summary>
    /// Factory failure must dispose the constructed HttpClient and propagate the <b>same</b>
    /// exception instance, with its original stack intact.
    /// </summary>
    [Fact]
    public void CreateCopilotClientForTestFull_FactoryThrows_DisposesHttpClientAndPropagatesSameInstance()
    {
        var terminal = new TrackingTerminalHandler();
        var factorySentinel = new SentinelThrower("factory boom");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, terminal,
                _ =>
                {
                    factorySentinel.ThrowFromSentinelOrigin();
                    throw new UnreachableException();
                }));

        AssertOriginalFailureSurvived(factorySentinel, thrown);
        Assert.Equal("factory boom", thrown.Message);
        Assert.True(terminal.Disposed, "The constructed HttpClient must be disposed when the factory throws.");
    }

    /// <summary>
    /// The cleanup on construction failure must never mask the original error: when the factory
    /// throws AND the best-effort disposal of the constructed HttpClient <em>also</em> throws, the
    /// caller must still receive the original factory exception instance, unchanged (same instance,
    /// same message, original stack) — while the cleanup is nevertheless attempted.
    /// </summary>
    /// <remarks>
    /// Without the throwing terminal handler, an implementation whose cleanup exception escaped and
    /// replaced the factory failure would go unnoticed, because a nonthrowing disposal can never
    /// produce a masking exception in the first place.
    /// </remarks>
    [Fact]
    public void CreateCopilotClientForTestFull_FactoryThrowsAndCleanupThrows_OriginalErrorSurvivesUnchanged()
    {
        var terminal = new ThrowingDisposalTerminalHandler("cleanup disposal failed");
        var factorySentinel = new SentinelThrower("factory boom");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, terminal,
                _ =>
                {
                    factorySentinel.ThrowFromSentinelOrigin();
                    throw new UnreachableException();
                }));

        // The original factory failure survives, byte for byte.
        AssertOriginalFailureSurvived(factorySentinel, thrown);
        Assert.Equal("factory boom", thrown.Message);

        // The disposal failure must be swallowed: never surfaced, never aggregated over the
        // original, never attached as an inner exception.
        Assert.NotSame(terminal.Sentinel.Thrown, thrown);
        Assert.IsNotType<AggregateException>(thrown);
        Assert.Null(thrown.InnerException);

        // ...and the cleanup must genuinely have been attempted, not skipped.
        Assert.True(terminal.DisposeAttempted,
            "The constructed HttpClient must still be disposed (best-effort) when the factory throws.");
        Assert.NotNull(terminal.Sentinel.Thrown);
    }

    /// <summary>A null factory result is rejected and the HttpClient is disposed.</summary>
    [Fact]
    public void CreateCopilotClientForTestFull_FactoryReturnsNull_DisposesHttpClientAndThrows()
    {
        var terminal = new TrackingTerminalHandler();

        var thrown = Assert.Throws<ArgumentNullException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, terminal, _ => null!));

        Assert.Equal("inner", thrown.ParamName);
        Assert.True(terminal.Disposed, "The constructed HttpClient must be disposed when the factory result is null.");
    }

    /// <summary>
    /// A null result whose cleanup also throws must still surface the null-guard failure, not the
    /// cleanup failure.
    /// </summary>
    [Fact]
    public void CreateCopilotClientForTestFull_FactoryReturnsNullAndCleanupThrows_NullGuardSurvives()
    {
        var terminal = new ThrowingDisposalTerminalHandler("cleanup disposal failed");

        var thrown = Assert.Throws<ArgumentNullException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, terminal, _ => null!));

        Assert.Equal("inner", thrown.ParamName);
        Assert.NotSame(terminal.Sentinel.Thrown, thrown);
        Assert.True(terminal.DisposeAttempted);
    }

    /// <summary>Null arguments are rejected up front.</summary>
    [Fact]
    public void CreateCopilotClientForTestFull_NullTerminalHandler_Throws()
    {
        var thrown = Assert.Throws<ArgumentNullException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, null!, _ => new RecordingChatClient()));

        Assert.Equal("terminalHandler", thrown.ParamName);
    }

    /// <summary>Null factory is rejected up front, before any HttpClient is created.</summary>
    [Fact]
    public void CreateCopilotClientForTestFull_NullFactory_Throws()
    {
        var terminal = new TrackingTerminalHandler();

        var thrown = Assert.Throws<ArgumentNullException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, terminal, null!));

        Assert.Equal("clientFactory", thrown.ParamName);

        // Validation happens before construction, so no transport was ever built (and therefore
        // none had to be torn down).
        Assert.False(terminal.Disposed);
    }
}

/// <summary>
/// The one seam test that genuinely needs to observe a <em>cleared</em> ambient environment, and
/// therefore mutates the process-wide <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> variables. It joins the
/// repository's serialized <c>EnvVarMutation</c> collection
/// (see <c>EnvVarMutationCollection</c>, <c>DisableParallelization = true</c>) so it cannot corrupt
/// — or be corrupted by — token-dependent tests running on other threads.
/// </summary>
/// <remarks>
/// The non-mutating half of the proof (the seam contributes no credential material at all) lives in
/// <see cref="OwnedCopilotChatClientTests.CreateCopilotClientForTestFull_ContributesNoCredentialMaterial"/>.
/// </remarks>
[Collection("EnvVarMutation")]
public sealed class CopilotClientSeamTokenIndependenceTests : IDisposable
{
    private readonly string? _origGhToken;
    private readonly string? _origGithubToken;

    public CopilotClientSeamTokenIndependenceTests()
    {
        _origGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _origGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _origGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _origGithubToken);
    }

    /// <summary>Terminal handler that never transmits; only its construction matters here.</summary>
    private sealed class InertTerminalHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>Minimal inner client for construction-only assertions.</summary>
    private sealed class InertChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// With both token variables cleared — the state under which the token-dependent Copilot path
    /// refuses to build a client — the seam still constructs successfully.
    /// </summary>
    [Fact]
    public void CreateCopilotClientForTestFull_WithTokensCleared_StillConstructs()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        using var client = ChatClientFactory.CreateCopilotClientForTestFull(
            useResponsesApi: false, new InertTerminalHandler(), _ => new InertChatClient());

        Assert.IsType<ChatClientFactory.OwnedCopilotChatClient>(client);
    }
}
