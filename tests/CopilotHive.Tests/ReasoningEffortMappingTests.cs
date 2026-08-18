using CopilotHive.Shared.AI;

using Microsoft.Extensions.AI;

using OllamaSharp;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for <see cref="ReasoningEffortMappingHandler"/>: the JSON-aware provider-boundary
/// translation of the internal <c>"extra_high"</c> reasoning-effort wire value.
/// </summary>
public sealed class ReasoningEffortMappingHandlerTests
{
    private const string Mapped = "xhigh";

    private static async Task<string> SendAsync(
        HttpContent content, string mappedValue = Mapped, string? customPropertyName = null)
    {
        var terminal = new CapturingTerminalHandler();
        using var client = new HttpClient(
            new ReasoningEffortMappingHandler(mappedValue, terminal, customPropertyName));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat/completions")
        {
            Content = content,
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return terminal.LastBody ?? string.Empty;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ── Targeted replacements ────────────────────────────────────────────────

    /// <summary>Top-level <c>reasoning_effort</c> with the exact source value is replaced.</summary>
    [Fact]
    public async Task TopLevelReasoningEffort_ExtraHigh_IsReplaced()
    {
        var body = await SendAsync(Json("""{"model":"gpt-5","reasoning_effort":"extra_high"}"""));

        Assert.Equal("xhigh", JsonNode.Parse(body)!["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("gpt-5", JsonNode.Parse(body)!["model"]!.GetValue<string>());
    }

    /// <summary>Nested <c>reasoning.effort</c> (responses API shape) is replaced.</summary>
    [Fact]
    public async Task NestedReasoningEffort_ExtraHigh_IsReplaced()
    {
        var body = await SendAsync(Json("""{"model":"gpt-5","reasoning":{"effort":"extra_high"}}"""));

        Assert.Equal("xhigh", JsonNode.Parse(body)!["reasoning"]!["effort"]!.GetValue<string>());
    }

    /// <summary>A configured custom property (e.g. Ollama's <c>think</c>) is replaced.</summary>
    [Fact]
    public async Task CustomProperty_ExtraHigh_IsReplaced_WhenConfigured()
    {
        var body = await SendAsync(
            Json("""{"model":"gpt-oss:20b","think":"extra_high"}"""),
            mappedValue: "max",
            customPropertyName: "think");

        Assert.Equal("max", JsonNode.Parse(body)!["think"]!.GetValue<string>());
    }

    /// <summary>The custom property supplements — it does not replace — the default targets.</summary>
    [Fact]
    public async Task CustomProperty_DoesNotDisableDefaultTargets()
    {
        var body = await SendAsync(
            Json("""{"reasoning_effort":"extra_high","reasoning":{"effort":"extra_high"},"think":"extra_high"}"""),
            mappedValue: "max",
            customPropertyName: "think");

        var json = JsonNode.Parse(body)!;
        Assert.Equal("max", json["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("max", json["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("max", json["think"]!.GetValue<string>());
    }

    /// <summary>A custom property is ignored when the handler was not configured with it.</summary>
    [Fact]
    public async Task CustomProperty_NotConfigured_IsUnchanged()
    {
        var body = await SendAsync(Json("""{"think":"extra_high"}"""));

        Assert.Equal("extra_high", JsonNode.Parse(body)!["think"]!.GetValue<string>());
    }

    // ── Negative cases: values that must survive untouched ───────────────────

    /// <summary>A reasoning effort other than the source value is never rewritten.</summary>
    [Fact]
    public async Task ReasoningEffort_High_IsUnchanged()
    {
        var body = await SendAsync(Json("""{"reasoning_effort":"high"}"""));

        Assert.Equal("high", JsonNode.Parse(body)!["reasoning_effort"]!.GetValue<string>());
    }

    /// <summary>The literal text in a user message is never rewritten.</summary>
    [Fact]
    public async Task ExtraHighInUserMessage_IsUnchanged()
    {
        const string original = """{"messages":[{"role":"user","content":"please use extra_high effort"}]}""";

        var body = await SendAsync(Json(original));

        Assert.Contains("extra_high", body, StringComparison.Ordinal);
        Assert.DoesNotContain("xhigh", body, StringComparison.Ordinal);
        Assert.Equal(original, body);
    }

    /// <summary>Tool-call arguments containing the value are never rewritten.</summary>
    [Fact]
    public async Task ExtraHighInToolArguments_IsUnchanged()
    {
        const string original =
            """{"messages":[{"role":"assistant","tool_calls":[{"function":{"name":"f","arguments":"{\u0022effort\u0022:\u0022extra_high\u0022}"}}]}]}""";

        var body = await SendAsync(Json(original));

        Assert.DoesNotContain("xhigh", body, StringComparison.Ordinal);
        Assert.Equal(original, body);
    }

    /// <summary>An array element equal to the value is never rewritten.</summary>
    [Fact]
    public async Task ExtraHighInArray_IsUnchanged()
    {
        const string original = """{"supported_efforts":["low","high","extra_high"]}""";

        var body = await SendAsync(Json(original));

        Assert.DoesNotContain("xhigh", body, StringComparison.Ordinal);
        Assert.Equal(original, body);
    }

    /// <summary>Bodies with no reasoning property at all pass through byte-identical.</summary>
    [Fact]
    public async Task NoReasoningProperty_IsUnchanged()
    {
        const string original = """{"model":"gpt-5","messages":[]}""";

        var body = await SendAsync(Json(original));

        Assert.Equal(original, body);
    }

    /// <summary>Non-JSON content is forwarded untouched even if it contains the value.</summary>
    [Fact]
    public async Task NonJsonContent_IsUnchanged()
    {
        const string original = """{"reasoning_effort":"extra_high"}""";

        var body = await SendAsync(new StringContent(original, Encoding.UTF8, "text/plain"));

        Assert.Equal(original, body);
    }

    /// <summary>Malformed JSON passes through unchanged and must not throw.</summary>
    [Fact]
    public async Task MalformedJson_IsUnchanged_AndDoesNotThrow()
    {
        const string original = """{"reasoning_effort":"extra_high" ,,, """;

        var body = await SendAsync(Json(original));

        Assert.Equal(original, body);
    }

    /// <summary>A JSON array root (valid JSON, no target properties) passes through unchanged.</summary>
    [Fact]
    public async Task JsonArrayRoot_IsUnchanged()
    {
        const string original = """["extra_high"]""";

        var body = await SendAsync(Json(original));

        Assert.Equal(original, body);
    }

    // ── Content lifetime ─────────────────────────────────────────────────────

    /// <summary>
    /// When a replacement occurs the original content is no longer referenced by the request,
    /// so the handler must dispose it and attach a fresh <see cref="StringContent"/>.
    /// </summary>
    [Fact]
    public async Task OriginalContent_IsDisposed_WhenReplacementOccurs()
    {
        var terminal = new CapturingTerminalHandler();
        using var client = new HttpClient(new ReasoningEffortMappingHandler(Mapped, terminal));

        var content = new DisposalTrackingContent("""{"reasoning_effort":"extra_high"}""");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat/completions")
        {
            Content = content,
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(content.Disposed);
        Assert.NotSame(content, request.Content);
        Assert.Equal("xhigh", JsonNode.Parse(terminal.LastBody!)!["reasoning_effort"]!.GetValue<string>());
    }

    /// <summary>When nothing is replaced the original content stays attached and undisposed.</summary>
    [Fact]
    public async Task OriginalContent_IsNotDisposed_WhenUnchanged()
    {
        var terminal = new CapturingTerminalHandler();
        using var client = new HttpClient(new ReasoningEffortMappingHandler(Mapped, terminal));

        var content = new DisposalTrackingContent("""{"reasoning_effort":"high"}""");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat/completions")
        {
            Content = content,
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.False(content.Disposed);
        Assert.Same(content, request.Content);
    }

    /// <summary>
    /// The replacement content must not inherit headers from the original — only the
    /// <see cref="StringContent"/> defaults (JSON content type, recomputed length).
    /// </summary>
    [Fact]
    public async Task ReplacementContent_DoesNotCopyOriginalHeaders()
    {
        var terminal = new CapturingTerminalHandler();
        using var client = new HttpClient(new ReasoningEffortMappingHandler(Mapped, terminal));

        var content = new DisposalTrackingContent("""{"reasoning_effort":"extra_high"}""");
        content.Headers.Add("X-Custom-Marker", "should-not-survive");
        content.Headers.ContentEncoding.Add("identity");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat/completions")
        {
            Content = content,
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.False(terminal.LastContentHeaders!.Contains("X-Custom-Marker"));
        Assert.Empty(terminal.LastContentHeaders.ContentEncoding);
        Assert.Equal("application/json", terminal.LastContentHeaders.ContentType?.MediaType);
        Assert.Equal(terminal.LastBody!.Length, terminal.LastContentHeaders.ContentLength);
    }

    /// <summary>Content that carries a disposal flag so tests can assert lifetime behaviour.</summary>
    private sealed class DisposalTrackingContent : HttpContent
    {
        private readonly byte[] _bytes;

        public DisposalTrackingContent(string body)
        {
            _bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Tests for <see cref="ReasoningEffortClampingClient"/>: the provider-boundary clamp that
/// lowers <see cref="ReasoningEffort.ExtraHigh"/> for providers that reject it.
/// </summary>
public sealed class ReasoningEffortClampingClientTests
{
    /// <summary><see cref="ReasoningEffort.ExtraHigh"/> is lowered to the configured maximum.</summary>
    [Fact]
    public async Task ExtraHigh_IsClampedToHigh()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningEffort.High, inner.LastOptions!.Reasoning!.Effort);
    }

    /// <summary>Values at or below the maximum are forwarded without cloning.</summary>
    [Theory]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.None)]
    public async Task NonExtraHighEfforts_ForwardOriginalInstance(ReasoningEffort effort)
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, TestContext.Current.CancellationToken);

        Assert.Same(options, inner.LastOptions);
        Assert.Same(options.Reasoning, inner.LastOptions!.Reasoning);
        Assert.Equal(effort, inner.LastOptions.Reasoning!.Effort);
    }

    /// <summary>Null options and options without reasoning are forwarded unchanged.</summary>
    [Fact]
    public void NullOrReasoninglessOptions_AreReturnedUnchanged()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        Assert.Null(client.ClampReasoning(null));

        var noReasoning = new ChatOptions();
        Assert.Same(noReasoning, client.ClampReasoning(noReasoning));

        var emptyReasoning = new ChatOptions { Reasoning = new ReasoningOptions() };
        Assert.Same(emptyReasoning, client.ClampReasoning(emptyReasoning));
    }

    /// <summary>The caller's options and reasoning options are never mutated by a clamp.</summary>
    [Fact]
    public void ClampReasoning_DoesNotMutateCallerOptions()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);
        var reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Full };
        var options = new ChatOptions { Reasoning = reasoning, Temperature = 0.25f, ModelId = "gpt-5" };

        var clamped = client.ClampReasoning(options);

        Assert.NotSame(options, clamped);
        Assert.NotSame(reasoning, clamped!.Reasoning);
        Assert.Equal(ReasoningEffort.ExtraHigh, reasoning.Effort);
        Assert.Same(reasoning, options.Reasoning);
        Assert.Equal(ReasoningEffort.High, clamped.Reasoning!.Effort);
    }

    /// <summary>All other options members survive the clamp clone.</summary>
    [Fact]
    public void ClampReasoning_PreservesOtherMembers()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Summary },
            Temperature = 0.5f,
            MaxOutputTokens = 1234,
            ModelId = "gpt-5",
            TopP = 0.9f,
            Seed = 42,
            StopSequences = ["stop"],
        };

        var clamped = client.ClampReasoning(options)!;

        Assert.Equal(0.5f, clamped.Temperature);
        Assert.Equal(1234, clamped.MaxOutputTokens);
        Assert.Equal("gpt-5", clamped.ModelId);
        Assert.Equal(0.9f, clamped.TopP);
        Assert.Equal(42, clamped.Seed);
        Assert.Equal(["stop"], clamped.StopSequences);
        Assert.Equal(ReasoningOutput.Summary, clamped.Reasoning!.Output);
        Assert.Equal(ReasoningEffort.High, clamped.Reasoning.Effort);
    }

    /// <summary>Streaming calls clamp too.</summary>
    [Fact]
    public async Task GetStreamingResponseAsync_ClampsEffort()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], options, TestContext.Current.CancellationToken))
        {
            // drain
        }

        Assert.Equal(ReasoningEffort.High, inner.LastOptions!.Reasoning!.Effort);
    }

    /// <summary><see cref="ReasoningEffortClampingClient.Metadata"/> comes from the inner client.</summary>
    [Fact]
    public void Metadata_IsForwardedToInner()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        Assert.Same(inner.Metadata, client.Metadata);
    }

    /// <summary><c>GetService</c> delegates verbatim to the inner client.</summary>
    [Fact]
    public void GetService_IsForwardedToInner()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        Assert.Same(inner, client.GetService(typeof(RecordingChatClient)));
        Assert.Same(inner.Metadata, client.GetService(typeof(ChatClientMetadata)));
        Assert.Null(client.GetService(typeof(string), "key"));
        Assert.Equal((typeof(string), (object?)"key"), inner.LastServiceRequest);
    }

    /// <summary>Disposing the wrapper disposes the inner client it owns.</summary>
    [Fact]
    public void Dispose_ForwardsToInner()
    {
        var inner = new RecordingChatClient();
        var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        client.Dispose();

        Assert.True(inner.Disposed);
    }

    /// <summary>A minimal <see cref="IChatClient"/> that records what it was handed.</summary>
    internal sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }
        public bool Disposed { get; private set; }
        public (Type, object?)? LastServiceRequest { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("recording", new Uri("https://example.invalid"), "test-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            LastServiceRequest = (serviceType, serviceKey);
            if (serviceKey is not null) return null;
            if (serviceType == typeof(ChatClientMetadata)) return Metadata;
            if (serviceType == typeof(RecordingChatClient)) return this;
            return null;
        }

        public void Dispose() => Disposed = true;
    }
}

/// <summary>
/// Wiring tests that exercise the FULL production handler chains (including the resilience
/// handler) through <see cref="ChatClientFactory.CreateCopilotClientForTest(bool, string, HttpMessageHandler)"/> and the
/// provider wrappers, without any network access.
/// </summary>
public sealed class ReasoningEffortWiringTests
{
    /// <summary>
    /// Copilot chat/completions branch: <c>"reasoning_effort":"extra_high"</c> reaches the
    /// transport as <c>"xhigh"</c> after passing through resilience + choice-merging + mapping.
    /// </summary>
    [Fact]
    public async Task CopilotChatCompletions_ExtraHigh_ReachesTransportAsXhigh()
    {
        var terminal = new CapturingTerminalHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(
                """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"extra_high"}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("xhigh", sent["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("hi", sent["messages"]![0]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// Copilot responses branch: nested <c>"reasoning":{"effort":"extra_high"}</c> reaches the
    /// transport as <c>"xhigh"</c> after passing through resilience + responses + mapping.
    /// </summary>
    [Fact]
    public async Task CopilotResponses_ExtraHigh_ReachesTransportAsXhigh()
    {
        var terminal = new CapturingTerminalHandler("""{"output":[]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: true, ChatClientFactory.CopilotExtraHighMapping, terminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent(
                """{"model":"gpt-5","reasoning":{"effort":"extra_high"},"input":[]}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("xhigh", sent["reasoning"]!["effort"]!.GetValue<string>());
    }

    /// <summary>The Copilot chain never rewrites a genuine <c>high</c> request.</summary>
    [Fact]
    public async Task CopilotChatCompletions_High_IsUnchanged()
    {
        var terminal = new CapturingTerminalHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(
                """{"model":"gpt-5","messages":[],"reasoning_effort":"high"}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("high", JsonNode.Parse(terminal.LastBody!)!["reasoning_effort"]!.GetValue<string>());
    }

    /// <summary>
    /// <see cref="ChatClientFactory.BuildHandlerChain"/> must link handlers without discarding
    /// links the caller already established — the bug the old <c>CreateResilientHandler</c> had.
    /// </summary>
    [Fact]
    public async Task BuildHandlerChain_PreservesEveryHandlerInOrder()
    {
        var order = new List<string>();
        var terminal = new CapturingTerminalHandler();
        var outer = new MarkerHandler("outer", order);
        var middle = new MarkerHandler("middle", order);

        var chain = ChatClientFactory.BuildHandlerChain(outer, middle, terminal);
        Assert.Same(outer, chain);

        using var client = new HttpClient(chain);
        using var response = await client.GetAsync("https://example.invalid/ping", TestContext.Current.CancellationToken);

        Assert.Equal(["outer", "middle"], order);
    }

    /// <summary>
    /// <see cref="ChatClientFactory.CreateResilientHandler"/> must place the supplied chain
    /// beneath the resilience handler without overwriting its inner links.
    /// </summary>
    [Fact]
    public async Task CreateResilientHandler_DoesNotDisconnectSuppliedChain()
    {
        var order = new List<string>();
        var terminal = new CapturingTerminalHandler();
        var supplied = ChatClientFactory.BuildHandlerChain(new MarkerHandler("supplied", order), terminal);

        using var client = new HttpClient(ChatClientFactory.CreateResilientHandler(supplied));
        using var response = await client.GetAsync("https://example.invalid/ping", TestContext.Current.CancellationToken);

        Assert.Equal(["supplied"], order);
        Assert.Equal(1, terminal.CallCount);
    }

    /// <summary>Non-delegating handlers may only appear last in a chain.</summary>
    [Fact]
    public void BuildHandlerChain_NonDelegatingHandlerBeforeEnd_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ChatClientFactory.BuildHandlerChain(new CapturingTerminalHandler(), new CapturingTerminalHandler()));
    }

    /// <summary>An empty chain is a programming error.</summary>
    [Fact]
    public void BuildHandlerChain_NoHandlers_Throws()
        => Assert.Throws<ArgumentException>(() => ChatClientFactory.BuildHandlerChain());

    /// <summary>
    /// GitHub provider: the clamping wrapper lowers <see cref="ReasoningEffort.ExtraHigh"/> to
    /// <see cref="ReasoningEffort.High"/> before the inner client sees it.
    /// </summary>
    [Fact]
    public async Task GitHubClampingWrapper_ExtraHigh_InnerReceivesHigh()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using IChatClient client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningEffort.High, inner.LastOptions!.Reasoning!.Effort);
    }

    /// <summary>
    /// REAL PATH: <c>ChatOptions.ExtraHigh → OllamaApiClient → handler chain → terminal</c>.
    /// This drives the production Ollama stack end to end (no synthetic JSON injected) and proves
    /// the terminal actually receives <c>"think":"max"</c>. This is the test that a body-level-only
    /// implementation cannot pass, because OllamaSharp collapses ExtraHigh to "high" during
    /// serialization, before any HTTP body exists.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExtraHigh_TerminalReceivesThinkMax()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            TestContext.Current.CancellationToken);

        Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL PATH, streaming: the streaming entry point must map ExtraHigh the same way, since
    /// OllamaSharp maps options separately for streaming and non-streaming requests.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_Streaming_ExtraHigh_TerminalReceivesThinkMax()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            TestContext.Current.CancellationToken))
        {
            // drain
        }

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("max", sent["think"]!.GetValue<string>());
        Assert.True(sent["stream"]!.GetValue<bool>());
    }

    /// <summary>
    /// REAL PATH regression guard: a genuine <see cref="ReasoningEffort.High"/> request must still
    /// send <c>"think":"high"</c>. This is exactly the distinction a body-level rewrite of "high"
    /// would have destroyed.
    /// </summary>
    [Theory]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.Low, "low")]
    public async Task OllamaRealPath_LowerEfforts_AreUnchanged(ReasoningEffort effort, string expectedThink)
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } },
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedThink, JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>REAL PATH: <see cref="ReasoningEffort.None"/> still disables thinking entirely.</summary>
    [Fact]
    public async Task OllamaRealPath_NoneEffort_SendsThinkFalse()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } },
            TestContext.Current.CancellationToken);

        Assert.False(JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<bool>());
    }

    /// <summary>
    /// REAL PATH: the ExtraHigh interception must not disturb any other request member.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExtraHigh_PreservesOtherRequestOptions()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            Temperature = 0.25f,
            MaxOutputTokens = 77,
        };
        options.AdditionalProperties = new AdditionalPropertiesDictionary { ["keep_alive"] = "5m" };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("max", sent["think"]!.GetValue<string>());
        Assert.Equal("gpt-oss:20b", sent["model"]!.GetValue<string>());
        Assert.Equal("5m", sent["keep_alive"]!.GetValue<string>());
        Assert.Equal(0.25f, sent["options"]!["temperature"]!.GetValue<float>(), 3);
        Assert.Equal(77, sent["options"]!["num_predict"]!.GetValue<int>());
        Assert.Equal("hello", sent["messages"]![0]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL PATH: the caller's <see cref="ChatOptions"/> must not be mutated by the interception —
    /// callers frequently reuse a single options instance across many requests.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExtraHigh_DoesNotMutateCallerOptions()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        Assert.Null(options.AdditionalProperties);
        Assert.Equal(ReasoningEffort.ExtraHigh, options.Reasoning!.Effort);
    }

    /// <summary>
    /// REAL PATH: an explicit caller-supplied <c>think</c> value always wins over the ExtraHigh
    /// interception — the wrapper must never override deliberate caller intent.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExplicitThinkValue_IsNotOverridden()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };
        options.AdditionalProperties = new AdditionalPropertiesDictionary { ["think"] = "low" };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        Assert.Equal("low", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL PATH: an explicit <c>Think</c> supplied through the caller's
    /// <see cref="ChatOptions.RawRepresentationFactory"/> must win over the ExtraHigh injection.
    /// OllamaSharp applies <c>AdditionalProperties["think"]</c> AFTER adopting the raw request, so
    /// injecting blindly would silently overwrite the caller's explicit raw value.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExplicitRawRepresentationThink_IsNotOverridden()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = new OllamaSharp.Models.Chat.ThinkValue("low"),
            },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        Assert.Equal("low", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL PATH: a boolean <c>Think</c> from the raw representation (e.g. explicitly disabling
    /// thinking) must also survive the ExtraHigh injection.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExplicitRawRepresentationThinkFalse_IsNotOverridden()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = new OllamaSharp.Models.Chat.ThinkValue(false),
            },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        Assert.False(JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<bool>());
    }

    /// <summary>
    /// REAL PATH: a raw representation that does NOT set <c>Think</c> must still receive the
    /// ExtraHigh injection, and its other members must survive.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_RawRepresentationWithoutThink_StillMapsExtraHigh()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { KeepAlive = "9m" },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("max", sent["think"]!.GetValue<string>());
        Assert.Equal("9m", sent["keep_alive"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL PATH: a raw-representation factory that returns <see langword="null"/> or a non-Ollama
    /// object means "no explicit think", so the ExtraHigh injection still applies.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_RawRepresentationFactoryReturningUnusableValue_StillMapsExtraHigh()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        foreach (var factory in new Func<IChatClient, object?>[] { _ => null, _ => "not-a-chat-request" })
        {
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
                RawRepresentationFactory = factory!,
            };

            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

            Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// REAL PATH: disposing a factory-created Ollama client must dispose the whole injected
    /// transport chain. OllamaSharp 5.4.30 only disposes an HTTP client it created itself, so the
    /// wrapper has to own the injected one — otherwise the handler chain and its sockets leak.
    /// Proven by asserting the terminal handler (the innermost link) was disposed.
    /// </summary>
    [Fact]
    public void OllamaRealPath_DisposingClient_DisposesInjectedTransportChain()
    {
        var terminal = new OllamaTerminalHandler();
        var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        Assert.False(terminal.Disposed);

        client.Dispose();

        // Disposing HttpClient disposes its handler chain down to the terminal handler.
        Assert.True(terminal.Disposed);
    }

    /// <summary>
    /// The body-level mapping handler remains wired on the Ollama chain as defence in depth: a
    /// hand-built body carrying the canonical <c>extra_high</c> wire value is still normalized to
    /// <c>max</c>, while a genuine <c>"high"</c> is never rewritten.
    /// </summary>
    [Fact]
    public async Task OllamaMapping_ThinkProperty_MapsExtraHighToMax()
    {
        var terminal = new CapturingTerminalHandler("""{"done":true}""");
        using var client = new HttpClient(ChatClientFactory.CreateResilientHandler(
            ChatClientFactory.BuildHandlerChain(
                new ReasoningEffortMappingHandler(
                    ChatClientFactory.OllamaExtraHighMapping,
                    customPropertyName: ChatClientFactory.OllamaReasoningPropertyName),
                terminal)));

        using var extraHigh = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat")
        {
            Content = new StringContent("""{"model":"gpt-oss:20b","think":"extra_high"}""", Encoding.UTF8, "application/json"),
        };
        using var extraHighResponse = await client.SendAsync(extraHigh, TestContext.Current.CancellationToken);
        Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());

        using var high = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat")
        {
            Content = new StringContent("""{"model":"gpt-oss:20b","think":"high"}""", Encoding.UTF8, "application/json"),
        };
        using var highResponse = await client.SendAsync(high, TestContext.Current.CancellationToken);
        Assert.Equal("high", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// Terminal handler that speaks just enough of the Ollama <c>/api/chat</c> protocol
    /// (newline-delimited JSON with a terminating <c>done</c> record) for OllamaSharp to complete a
    /// request, while capturing the outgoing body.
    /// </summary>
    internal sealed class OllamaTerminalHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        /// <summary>Set when this handler is disposed, proving the chain's disposal reached it.</summary>
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);

            const string done = """{"model":"gpt-oss:20b","created_at":"2024-01-01T00:00:00Z","message":{"role":"assistant","content":"hi"},"done":true,"done_reason":"stop"}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(done, Encoding.UTF8, "application/x-ndjson"),
            };
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Delegating handler that records that it ran, then forwards.</summary>
    private sealed class MarkerHandler : DelegatingHandler
    {
        private readonly string _name;
        private readonly List<string> _order;

        public MarkerHandler(string name, List<string> order)
        {
            _name = name;
            _order = order;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _order.Add(_name);
            return base.SendAsync(request, ct);
        }
    }
}

/// <summary>
/// Terminal (non-delegating) handler that records the request body and content headers it
/// receives and returns a canned JSON response, so handler chains can be tested offline.
/// </summary>
internal sealed class CapturingTerminalHandler : HttpMessageHandler
{
    private readonly string _responseBody;

    public CapturingTerminalHandler(string responseBody = "{}") => _responseBody = responseBody;

    public string? LastBody { get; private set; }
    public System.Net.Http.Headers.HttpContentHeaders? LastContentHeaders { get; private set; }
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        if (request.Content is not null)
        {
            LastBody = await request.Content.ReadAsStringAsync(ct);
            LastContentHeaders = request.Content.Headers;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>
/// Additional integration tests for the reasoning-effort mapping: defensive guard branches,
/// full-chain round-trips, and provider-boundary semantics.
/// </summary>
public sealed class ReasoningEffortIntegrationTests
{
    /// <summary>
    /// When a derived <see cref="ChatOptions"/> clones itself sharing the same
    /// <see cref="ReasoningOptions"/> instance, the clamping client must detect the shared
    /// reference and allocate a fresh <see cref="ReasoningOptions"/> rather than mutating
    /// the caller's reasoning instance.
    /// </summary>
    [Fact]
    public void ClampReasoning_SharedReasoningOnClone_CreatesFreshReasoningOptions()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        var sharedReasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Full };
        var options = new SharedReasoningChatOptions(sharedReasoning, 0.5f, "gpt-5");

        var clamped = client.ClampReasoning(options)!;

        Assert.NotSame(sharedReasoning, clamped.Reasoning);
        Assert.NotSame(options.Reasoning, clamped.Reasoning);
        Assert.Equal(ReasoningEffort.ExtraHigh, sharedReasoning.Effort);
        Assert.Equal(ReasoningEffort.High, clamped.Reasoning!.Effort);
        Assert.Equal(ReasoningOutput.Full, clamped.Reasoning.Output);
        Assert.Equal(0.5f, clamped.Temperature);
        Assert.Equal("gpt-5", clamped.ModelId);
    }

    /// <summary>
    /// A <see cref="ChatOptions"/> subclass whose <see cref="ChatOptions.Clone"/> returns a
    /// clone with a null <see cref="ReasoningOptions"/> — the defensive guard must allocate
    /// a fresh one rather than NRE on <c>clone.Reasoning.Effort</c>.
    /// </summary>
    [Fact]
    public void ClampReasoning_NullReasoningOnClone_CreatesFreshReasoningOptions()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        var reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Summary };
        var options = new NullReasoningCloneChatOptions(reasoning, "test-model");

        var clamped = client.ClampReasoning(options)!;

        Assert.NotNull(clamped.Reasoning);
        Assert.Equal(ReasoningEffort.High, clamped.Reasoning!.Effort);
        Assert.Equal(ReasoningOutput.Summary, clamped.Reasoning.Output);
        Assert.NotSame(reasoning, clamped.Reasoning);
        Assert.Equal(ReasoningEffort.ExtraHigh, reasoning.Effort);
        Assert.Equal("test-model", clamped.ModelId);
    }

    /// <summary>
    /// Full Copilot chain round-trip with both target properties present in the same body:
    /// both <c>reasoning_effort</c> and <c>reasoning.effort</c> must be mapped to
    /// <c>xhigh</c> independently.
    /// </summary>
    [Fact]
    public async Task CopilotChain_BothTargetsInSameBody_AreBothMapped()
    {
        var terminal = new CapturingTerminalHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(
                """{"model":"gpt-5","reasoning_effort":"extra_high","reasoning":{"effort":"extra_high"},"messages":[]}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("xhigh", sent["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("xhigh", sent["reasoning"]!["effort"]!.GetValue<string>());
    }

    /// <summary>
    /// Full Copilot chain: a request with no reasoning properties at all must pass through
    /// with body structure unchanged (no spurious injection of reasoning keys).
    /// </summary>
    [Fact]
    public async Task CopilotChain_NoReasoningInBody_BodyStructureUnchanged()
    {
        var terminal = new CapturingTerminalHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal);

        var original = """{"model":"gpt-5","messages":[{"role":"user","content":"hello"}]}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(original, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = (JsonObject)JsonNode.Parse(terminal.LastBody!)!;
        Assert.False(sent.ContainsKey("reasoning_effort"));
        Assert.False(sent.ContainsKey("reasoning"));
        Assert.Equal("gpt-5", sent["model"]!.GetValue<string>());
    }

    /// <summary>
    /// Full Copilot chain: "extra_high" appearing inside a tool-call argument (escaped JSON
    /// string) must survive the mapping handler untouched — only top-level reasoning
    /// properties are targeted.
    /// </summary>
    [Fact]
    public async Task CopilotChain_ExtraHighInToolArguments_SurvivesMapping()
    {
        var terminal = new CapturingTerminalHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal);

        var original = """{"model":"gpt-5","reasoning_effort":"extra_high","messages":[{"role":"assistant","tool_calls":[{"function":{"name":"set_effort","arguments":"{\\u0022level\\u0022:\\u0022extra_high\\u0022}"}}]}]}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(original, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("xhigh", sent["reasoning_effort"]!.GetValue<string>());
        // The tool-call argument must still contain "extra_high", not "xhigh"
        var args = sent["messages"]![0]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>();
        Assert.Contains("extra_high", args, StringComparison.Ordinal);
        Assert.DoesNotContain("xhigh", args, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Ollama mapping handler, wired through the resilience layer, maps a canonical
    /// <c>"extra_high"</c> in the <c>think</c> property to <c>"max"</c> while also mapping
    /// <c>reasoning_effort</c> (the default target) when both are present.
    /// </summary>
    [Fact]
    public async Task OllamaMapping_BothThinkAndReasoningEffort_AreMappedToMax()
    {
        var terminal = new CapturingTerminalHandler("""{"done":true}""");
        using var client = new HttpClient(ChatClientFactory.CreateResilientHandler(
            ChatClientFactory.BuildHandlerChain(
                new ReasoningEffortMappingHandler(
                    ChatClientFactory.OllamaExtraHighMapping,
                    customPropertyName: ChatClientFactory.OllamaReasoningPropertyName),
                terminal)));

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat")
        {
            Content = new StringContent(
                """{"model":"gpt-oss:20b","reasoning_effort":"extra_high","think":"extra_high"}""",
                Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("max", sent["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("max", sent["think"]!.GetValue<string>());
    }

    /// <summary>
    /// A <see cref="ChatOptions"/> subclass whose <see cref="ChatOptions.Clone"/> shares the
    /// same <see cref="ReasoningOptions"/> instance, exercising the defensive guard.
    /// </summary>
    private sealed class SharedReasoningChatOptions : ChatOptions
    {
        private readonly ReasoningOptions _sharedReasoning;

        public SharedReasoningChatOptions(ReasoningOptions reasoning, float temperature, string modelId)
        {
            _sharedReasoning = reasoning;
            Reasoning = reasoning;
            Temperature = temperature;
            ModelId = modelId;
        }

        public override ChatOptions Clone()
        {
            // Clone shares the same Reasoning instance (simulating a buggy/derived Clone).
            var clone = new SharedReasoningChatOptions(_sharedReasoning, Temperature ?? 0f, ModelId ?? string.Empty);
            return clone;
        }
    }

    /// <summary>
    /// A <see cref="ChatOptions"/> subclass whose <see cref="ChatOptions.Clone"/> returns a
    /// clone with a null <see cref="ReasoningOptions"/>, exercising the null branch of the guard.
    /// </summary>
    private sealed class NullReasoningCloneChatOptions : ChatOptions
    {
        public NullReasoningCloneChatOptions(ReasoningOptions reasoning, string modelId)
        {
            Reasoning = reasoning;
            ModelId = modelId;
        }

        public override ChatOptions Clone()
        {
            // Clone has null Reasoning — simulating a Clone that drops the reasoning instance.
            var clone = new NullReasoningCloneChatOptions(null!, ModelId!);
            return clone;
        }
    }
}

/// <summary>
/// Retry-safety tests for <see cref="ChatClientFactory.CopilotResponsesHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// The production chain places the handler <em>beneath</em> the <c>ResilienceHandler</c>, so a
/// transient failure re-enters <c>CopilotResponsesHandler.SendAsync</c>. A probe confirmed that
/// Polly reuses the <b>same</b> <see cref="HttpRequestMessage"/> instance across attempts and that
/// both <c>request.Options</c> and a replaced <c>Content</c> persist into the retry. That makes the
/// transformation re-entrant, so it must be idempotent and must not commit conversation state until
/// an authoritative response arrives.
/// </para>
/// <para>
/// Every test here forces real transient failures through
/// <see cref="ChatClientFactory.CreateCopilotClientForTest(bool, string, HttpMessageHandler, out DelegatingHandler, TimeSpan?)"/>,
/// which exercises the genuine resilience pipeline (same retry count and backoff type as
/// production, only the base delay is shortened).
/// </para>
/// </remarks>
public sealed class CopilotResponsesHandlerRetryTests
{
    private static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(1);

    private static HttpClient CreateClient(
        HttpMessageHandler terminal, out ChatClientFactory.CopilotResponsesHandler handler)
    {
        var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: true,
            ChatClientFactory.CopilotExtraHighMapping,
            terminal,
            out var copilotHandler,
            FastRetry);

        handler = Assert.IsType<ChatClientFactory.CopilotResponsesHandler>(copilotHandler);
        return client;
    }

    private static HttpRequestMessage ResponsesRequest(string body) =>
        new(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    // ── Internal helpers for cross-class integration tests ───────────────────

    /// <summary>Creates a test client with the fast retry pipeline, exposing the Copilot handler.</summary>
    internal static HttpClient GetClientForExternalUse(
        HttpMessageHandler terminal, out ChatClientFactory.CopilotResponsesHandler handler)
        => CreateClient(terminal, out handler);

    /// <summary>Builds a responses-API request with the given JSON body.</summary>
    internal static HttpRequestMessage ResponsesRequestForExternalUse(string body) =>
        ResponsesRequest(body);

    /// <summary>
    /// A first request that is retried must record the ORIGINAL input as the base context exactly
    /// once. Before the fix, the retry re-read the (already-transformed) body and overwrote
    /// <c>_baseInput</c>, corrupting every later conversation reconstruction.
    /// </summary>
    [Fact]
    public async Task FirstRequest_RetriedAfterTransientFailure_BaseInputNotCorrupted()
    {
        var terminal = new FailThenSucceedHandler(failures: 2, """{"output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, terminal.Attempts);

        // The committed base input must be exactly the caller's original single message.
        var baseInput = handler.BaseInputForTest;
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("first", baseInput[0]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// A follow-up request that is retried must append its input to the turn history exactly once.
    /// Before the fix, state was committed during the transformation, so the input was appended on
    /// the first attempt even when that attempt failed.
    /// </summary>
    [Fact]
    public async Task FollowUpRequest_RetriedAfterTransientFailure_TurnHistoryNotDuplicated()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        // Establish the base context with a clean first request.
        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        var historyAfterFirst = handler.TurnHistoryForTest.Count;

        // Now a follow-up that fails twice before succeeding.
        terminal.ResetFailures(2);
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (var response = await client.SendAsync(followUp, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Exactly one new turn-history entry despite three attempts.
        Assert.Equal(historyAfterFirst + 1, handler.TurnHistoryForTest.Count);
        Assert.Equal("tool-result", handler.TurnHistoryForTest[^1]["output"]!.GetValue<string>());
    }

    /// <summary>
    /// The request body must be transformed exactly once, no matter how many attempts occur.
    /// A non-idempotent transformation would re-expand the input array on every retry.
    /// </summary>
    [Fact]
    public async Task RetriedFollowUp_BodyIsTransformedExactlyOnce()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[]}""");
        using var client = CreateClient(terminal, out _);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        terminal.ResetFailures(2);
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (await client.SendAsync(followUp, TestContext.Current.CancellationToken)) { }

        // Every attempt must have sent an identical body — proof the transformation did not re-run.
        Assert.Equal(3, terminal.Bodies.Count);
        Assert.Single(terminal.Bodies.Distinct());

        var sent = JsonNode.Parse(terminal.Bodies[^1])!;
        Assert.Null(sent["previous_response_id"]);

        // base input (1) + current input (1) — not duplicated per attempt.
        var input = sent["input"]!.AsArray();
        Assert.Equal(2, input.Count);
        Assert.Equal("first", input[0]!["content"]!.GetValue<string>());
        Assert.Equal("tool-result", input[1]!["output"]!.GetValue<string>());
    }

    /// <summary>
    /// When every attempt fails, no conversation state may be committed at all — a later successful
    /// request must not inherit phantom history from the failed one.
    /// </summary>
    [Fact]
    public async Task AllAttemptsFail_NoConversationStateIsCommitted()
    {
        var terminal = new FailThenSucceedHandler(failures: int.MaxValue, """{"output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"doomed"}]}"""))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        Assert.Null(handler.BaseInputForTest);
        Assert.Empty(handler.TurnHistoryForTest);
    }

    /// <summary>
    /// End-to-end proof that the retry fix preserves correct conversation reconstruction: after a
    /// retried first request, a subsequent follow-up must still rebuild the conversation from the
    /// original base input rather than from a corrupted, self-expanded copy.
    /// </summary>
    [Fact]
    public async Task RetriedFirstRequest_LaterFollowUp_ReconstructsConversationCorrectly()
    {
        var terminal = new FailThenSucceedHandler(failures: 2, """{"output":[{"type":"message","content":"answer"}]}""");
        using var client = CreateClient(terminal, out _);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        terminal.ResetFailures(0);
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (await client.SendAsync(followUp, TestContext.Current.CancellationToken)) { }

        var input = JsonNode.Parse(terminal.Bodies[^1])!["input"]!.AsArray();

        // base input (1) + prior response output (1) + current input (1) = 3, in order, no dupes.
        Assert.Equal(3, input.Count);
        Assert.Equal("first", input[0]!["content"]!.GetValue<string>());
        Assert.Equal("answer", input[1]!["content"]!.GetValue<string>());
        Assert.Equal("tool-result", input[2]!["output"]!.GetValue<string>());
    }

    /// <summary>
    /// The reasoning-effort mapping still applies on the retried attempts — the idempotence marker
    /// must not short-circuit the rest of the chain.
    /// </summary>
    [Fact]
    public async Task RetriedRequest_ExtraHighStillMappedOnEveryAttempt()
    {
        var terminal = new FailThenSucceedHandler(failures: 2, """{"output":[]}""");
        using var client = CreateClient(terminal, out _);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","reasoning":{"effort":"extra_high"},"input":[]}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(3, terminal.Bodies.Count);
        Assert.All(terminal.Bodies, body =>
            Assert.Equal("xhigh", JsonNode.Parse(body)!["reasoning"]!["effort"]!.GetValue<string>()));
    }

    /// <summary>
    /// The exact corruption described in review: a retried FOLLOW-UP request. The first attempt
    /// strips <c>previous_response_id</c>; on retry the body no longer contains it, so an
    /// unguarded handler falls into the "first request" branch and overwrites <c>_baseInput</c>
    /// with the fully expanded conversation. This asserts the base input still holds only the
    /// original message.
    /// </summary>
    [Fact]
    public async Task RetriedFollowUp_BaseInputIsNotOverwrittenWithExpandedConversation()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[{"type":"message","content":"answer"}]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        terminal.ResetFailures(2);
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (await client.SendAsync(followUp, TestContext.Current.CancellationToken)) { }

        // _baseInput must still be ONLY the original user message — not the expanded conversation.
        var baseInput = handler.BaseInputForTest;
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("first", baseInput[0]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// A FOLLOW-UP whose every attempt fails must not append anything to the turn history. This is
    /// the test that proves the deferred-commit half of the fix: committing the staged input during
    /// the transformation (rather than after an authoritative response) would leave the failed
    /// request's tool result permanently in the history, poisoning the next successful request.
    /// </summary>
    [Fact]
    public async Task FollowUpWithAllAttemptsFailing_DoesNotCommitTurnHistory()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        var historyAfterFirst = handler.TurnHistoryForTest.Count;

        terminal.ResetFailures(int.MaxValue);
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"doomed-result"}]}"""))
        using (var response = await client.SendAsync(followUp, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        // Nothing from the failed follow-up may have been recorded.
        Assert.Equal(historyAfterFirst, handler.TurnHistoryForTest.Count);
        Assert.DoesNotContain(handler.TurnHistoryForTest,
            n => n.ToJsonString().Contains("doomed-result", StringComparison.Ordinal));
    }

    /// <summary>
    /// A FIRST request whose every attempt fails with a <c>text/event-stream</c> content type must
    /// not commit conversation state. An error reply can carry the streaming content type (the API
    /// echoes the requested stream mode), and the streaming short-circuit previously ran before the
    /// success check, so the failed attempt committed <c>_baseInput</c> anyway.
    /// </summary>
    [Fact]
    public async Task FailedSseResponse_DoesNotCommitBaseInput()
    {
        var terminal = new FailThenSucceedHandler(failures: int.MaxValue, """{"output":[]}""")
        {
            FailuresUseSseContentType = true,
        };
        using var client = CreateClient(terminal, out var handler);

        using (var request = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"doomed"}]}"""))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.Null(handler.BaseInputForTest);
        Assert.Empty(handler.TurnHistoryForTest);
    }

    /// <summary>
    /// A FOLLOW-UP whose every attempt fails with an SSE content type must not append its staged
    /// input to the turn history.
    /// </summary>
    [Fact]
    public async Task FailedSseFollowUp_DoesNotCommitTurnHistory()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        var historyAfterFirst = handler.TurnHistoryForTest.Count;

        terminal.ResetFailures(int.MaxValue);
        terminal.FailuresUseSseContentType = true;

        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"sse-doomed"}]}"""))
        using (var response = await client.SendAsync(followUp, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        Assert.Equal(historyAfterFirst, handler.TurnHistoryForTest.Count);
        Assert.DoesNotContain(handler.TurnHistoryForTest,
            n => n.ToJsonString().Contains("sse-doomed", StringComparison.Ordinal));
    }

    /// <summary>
    /// An SSE attempt that fails transiently and then SUCCEEDS as SSE must commit exactly once —
    /// the fix must not swing too far and drop state for genuinely successful streaming responses.
    /// </summary>
    [Fact]
    public async Task FailedThenSuccessfulSseResponse_CommitsStateExactlyOnce()
    {
        var terminal = new FailThenSucceedHandler(failures: 2, """{"output":[]}""")
        {
            FailuresUseSseContentType = true,
            SuccessUsesSseContentType = true,
        };
        using var client = CreateClient(terminal, out var handler);

        using (var request = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // The successful SSE body must still pass through untouched for the SDK's parser.
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("event: done",
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        }

        Assert.Equal(3, terminal.Attempts);

        var baseInput = handler.BaseInputForTest;
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("first", baseInput[0]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// A successful SSE FOLLOW-UP must commit its staged input exactly once, without duplication
    /// across the failed attempts that preceded it.
    /// </summary>
    [Fact]
    public async Task SuccessfulSseFollowUpAfterFailures_CommitsTurnHistoryOnce()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        var historyAfterFirst = handler.TurnHistoryForTest.Count;

        terminal.ResetFailures(2);
        terminal.FailuresUseSseContentType = true;
        terminal.SuccessUsesSseContentType = true;

        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"sse-result"}]}"""))
        using (var response = await client.SendAsync(followUp, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(historyAfterFirst + 1, handler.TurnHistoryForTest.Count);
        Assert.Equal("sse-result", handler.TurnHistoryForTest[^1]["output"]!.GetValue<string>());
    }

    /// <summary>
    /// A failed SSE response must NOT be read to completion. A live error event stream can stay
    /// open indefinitely, so consuming it would block until the timeout and prevent the resilience
    /// handler above from ever observing the failure status and retrying. The unbounded stream here
    /// makes that hang detectable — with a body-consuming implementation this test times out.
    /// </summary>
    /// <remarks>
    /// HTTP media types are case-insensitive and may carry parameters, so every spelling a server
    /// might legitimately send has to take the no-consumption path. A case-sensitive comparison
    /// against the canonical lowercase form leaves every other casing falling through to the
    /// body-reading branch.
    /// </remarks>
    [Theory]
    [InlineData("text/event-stream")]
    [InlineData("Text/Event-Stream")]
    [InlineData("TEXT/EVENT-STREAM")]
    [InlineData("text/event-stream; charset=utf-8")]
    [InlineData("Text/Event-Stream; charset=utf-8")]
    public async Task FailedSseResponse_IsNotReadToCompletion_SoRetriesProceed(string failureContentType)
    {
        var terminal = new FailThenSucceedHandler(failures: 2, """{"output":[]}""")
        {
            FailuresUseSseContentType = true,
            UseUnboundedFailureStream = true,
            FailureContentType = failureContentType,
        };
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"first"}]}""");

        // A generous but finite budget: the point is that this completes at all.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var response = await client.SendAsync(request, timeout.Token);

        // Both failed attempts were observed and retried despite their never-ending bodies.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, terminal.Attempts);

        // The successful attempt still committed exactly once.
        var baseInput = handler.BaseInputForTest;
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
    }

    /// <summary>
    /// A failed SSE response of any casing must also leave conversation state untouched — the
    /// no-consumption path and the no-commit path must be the same path.
    /// </summary>
    [Theory]
    [InlineData("Text/Event-Stream")]
    [InlineData("TEXT/EVENT-STREAM; charset=utf-8")]
    public async Task FailedSseResponse_MixedCase_DoesNotCommitState(string failureContentType)
    {
        var terminal = new FailThenSucceedHandler(failures: int.MaxValue, """{"output":[]}""")
        {
            FailuresUseSseContentType = true,
            FailureContentType = failureContentType,
        };
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"doomed"}]}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(handler.BaseInputForTest);
        Assert.Empty(handler.TurnHistoryForTest);
    }

    /// <summary>
    /// A SUCCESSFUL SSE response of any casing must still be recognized as streaming: it must be
    /// handed back unconsumed for the SDK's parser and must commit exactly once.
    /// </summary>
    [Theory]
    [InlineData("Text/Event-Stream")]
    [InlineData("TEXT/EVENT-STREAM; charset=utf-8")]
    public async Task SuccessfulSseResponse_MixedCase_CommitsAndPassesThrough(string successContentType)
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[]}""")
        {
            SuccessUsesSseContentType = true,
            SuccessContentType = successContentType,
        };
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"first"}]}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Passed through untouched — not rewritten to application/json.
        Assert.Equal("text/event-stream",
            response.Content.Headers.ContentType?.MediaType, ignoreCase: true);
        Assert.Contains("event: done",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        var baseInput = handler.BaseInputForTest;
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
    }

    /// <summary>
    /// A 2xx whose body is malformed JSON means the operation did not actually complete, so no
    /// conversation state may be committed. Committing before parsing would poison every later
    /// reconstruction with state from a request whose result the caller never received.
    /// </summary>
    [Fact]
    public async Task MalformedSuccessBody_DoesNotCommitConversationState()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[ ,,, truncated""");
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Null(handler.BaseInputForTest);
        Assert.Empty(handler.TurnHistoryForTest);
    }

    /// <summary>
    /// A malformed 2xx on a FOLLOW-UP must not append its staged input to the turn history either.
    /// </summary>
    [Fact]
    public async Task MalformedSuccessBodyOnFollowUp_DoesNotCommitTurnHistory()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        var historyAfterFirst = handler.TurnHistoryForTest.Count;

        terminal.SuccessBody = """{"output":[ ,,, truncated""";

        using var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"malformed-doomed"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(followUp, TestContext.Current.CancellationToken));

        Assert.Equal(historyAfterFirst, handler.TurnHistoryForTest.Count);
        Assert.DoesNotContain(handler.TurnHistoryForTest,
            n => n.ToJsonString().Contains("malformed-doomed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A 2xx body that parses syntactically but is structurally invalid — here a null element in
    /// the <c>output</c> array — must not commit conversation state. Syntactic parsing alone is not
    /// enough to call the exchange complete: processing the output can still fail, and committing
    /// beforehand would leave the caller observing an error while later requests inherit poisoned
    /// state.
    /// </summary>
    [Theory]
    [InlineData("""{"output":[null]}""")]
    [InlineData("""{"output":[{"type":"message","content":"ok"},null]}""")]
    [InlineData("""{"output":[null,{"type":"message","content":"ok"}]}""")]
    public async Task StructurallyInvalidSuccessBody_DoesNotCommitConversationState(string successBody)
    {
        var terminal = new FailThenSucceedHandler(failures: 0, successBody);
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Null(handler.BaseInputForTest);
        Assert.Empty(handler.TurnHistoryForTest);
    }

    /// <summary>
    /// A structurally invalid 2xx on a FOLLOW-UP must not append its staged input to the turn
    /// history, and must not partially append response output either. The previous implementation
    /// added output items one at a time, so a null element midway through left earlier items
    /// already committed.
    /// </summary>
    [Fact]
    public async Task StructurallyInvalidSuccessBodyOnFollowUp_DoesNotPartiallyCommit()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        var historyAfterFirst = handler.TurnHistoryForTest.Count;

        // A well-formed leading item followed by a null: the old loop committed the first item
        // before throwing on the second.
        terminal.SuccessBody = """{"output":[{"type":"message","content":"leaked"},null]}""";

        using var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"structural-doomed"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(followUp, TestContext.Current.CancellationToken));

        Assert.Equal(historyAfterFirst, handler.TurnHistoryForTest.Count);
        Assert.DoesNotContain(handler.TurnHistoryForTest,
            n => n.ToJsonString().Contains("structural-doomed", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.TurnHistoryForTest,
            n => n.ToJsonString().Contains("leaked", StringComparison.Ordinal));
    }

    /// <summary>
    /// A structural failure must leave state clean enough that a later well-formed request still
    /// reconstructs the conversation correctly — proving nothing was silently poisoned.
    /// </summary>
    [Fact]
    public async Task AfterStructuralFailure_LaterRequestReconstructsCorrectly()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[null]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var doomed = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"doomed"}]}"""))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SendAsync(doomed, TestContext.Current.CancellationToken));
        }

        Assert.Null(handler.BaseInputForTest);

        // A fresh, well-formed first request establishes the base context cleanly.
        terminal.SuccessBody = """{"output":[{"type":"message","content":"answer"}]}""";
        using (var good = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"real-first"}]}"""))
        using (await client.SendAsync(good, TestContext.Current.CancellationToken)) { }

        var baseInput = handler.BaseInputForTest;
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("real-first", baseInput[0]!["content"]!.GetValue<string>());

        // And the follow-up reconstructs base + response output + current input, with no residue
        // from the structurally invalid exchange.
        terminal.ResetFailures(0);
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (await client.SendAsync(followUp, TestContext.Current.CancellationToken)) { }

        var input = JsonNode.Parse(terminal.Bodies[^1])!["input"]!.AsArray();
        Assert.Equal(3, input.Count);
        Assert.Equal("real-first", input[0]!["content"]!.GetValue<string>());
        Assert.Equal("answer", input[1]!["content"]!.GetValue<string>());
        Assert.Equal("tool-result", input[2]!["output"]!.GetValue<string>());
    }

    /// <summary>
    /// The conversation-state commit must be the FINAL step, after response-content replacement.
    /// If replacement fails, the exchange has not been fully processed, so nothing may be committed
    /// — the caller sees the error with state exactly as it was.
    /// </summary>
    /// <remarks>
    /// This test fails if the commit is moved back before response-content replacement: the commit
    /// would then run before the forced replacement failure and leave state behind.
    /// </remarks>
    [Fact]
    public async Task ResponseContentReplacementThrows_DoesNotCommitConversationState()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[{"type":"message","content":"answer"}]}""");
        using var client = CreateClient(terminal, out var handler);

        handler.ResponseContentFactory = _ => throw new InvalidOperationException("content replacement failed");

        using var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal("content replacement failed", ex.Message);

        // Neither the staged request state nor the accumulated response output may be present.
        Assert.Null(handler.BaseInputForTest);
        Assert.Empty(handler.TurnHistoryForTest);
    }

    /// <summary>
    /// The same guarantee on a FOLLOW-UP: a failed response-content replacement must not append the
    /// staged input or the response output to the turn history.
    /// </summary>
    [Fact]
    public async Task ResponseContentReplacementThrowsOnFollowUp_DoesNotCommitTurnHistory()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        var historyAfterFirst = handler.TurnHistoryForTest.Count;

        terminal.SuccessBody = """{"output":[{"type":"message","content":"replacement-leaked"}]}""";
        handler.ResponseContentFactory = _ => throw new InvalidOperationException("content replacement failed");

        using var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"replacement-doomed"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(followUp, TestContext.Current.CancellationToken));

        Assert.Equal(historyAfterFirst, handler.TurnHistoryForTest.Count);
        Assert.DoesNotContain(handler.TurnHistoryForTest,
            n => n.ToJsonString().Contains("replacement-doomed", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.TurnHistoryForTest,
            n => n.ToJsonString().Contains("replacement-leaked", StringComparison.Ordinal));
    }

    /// <summary>
    /// Directly observes the ordering: at the moment response-content replacement runs, the durable
    /// conversation state must still be untouched. This pins the sequence rather than only the
    /// failure behaviour, so moving the commit earlier is caught even when nothing throws.
    /// </summary>
    [Fact]
    public async Task CommitHappensAfterResponseContentReplacement()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[{"type":"message","content":"answer"}]}""");
        using var client = CreateClient(terminal, out var handler);

        int? baseInputCountAtReplacement = null;
        int? turnHistoryCountAtReplacement = null;

        var realFactory = handler.ResponseContentFactory;
        handler.ResponseContentFactory = body =>
        {
            baseInputCountAtReplacement = handler.BaseInputForTest?.Count ?? -1;
            turnHistoryCountAtReplacement = handler.TurnHistoryForTest.Count;
            return realFactory(body);
        };

        using var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Replacement ran, and at that point nothing had been committed yet.
        Assert.Equal(-1, baseInputCountAtReplacement);
        Assert.Equal(0, turnHistoryCountAtReplacement);

        // After the exchange completes, the commit has happened exactly once.
        var baseInput = handler.BaseInputForTest;
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("first", baseInput[0]!["content"]!.GetValue<string>());
        Assert.Single(handler.TurnHistoryForTest);
        Assert.Equal("answer", handler.TurnHistoryForTest[0]["content"]!.GetValue<string>());

        // The body still reached the caller intact.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Terminal handler that returns a configurable number of retryable failures before succeeding,
    /// recording the body of every attempt.
    /// </summary>
    internal sealed class FailThenSucceedHandler : HttpMessageHandler
    {
        private int _remainingFailures;

        public FailThenSucceedHandler(int failures, string successBody)
        {
            _remainingFailures = failures;
            SuccessBody = successBody;
        }

        /// <summary>The body returned once the failure budget is exhausted.</summary>
        public string SuccessBody { get; set; }

        public List<string> Bodies { get; } = new();
        public int Attempts => Bodies.Count;

        /// <summary>
        /// When set, failure responses are returned with a <c>text/event-stream</c> content type,
        /// reproducing an API that echoes the requested stream mode on an error reply.
        /// </summary>
        public bool FailuresUseSseContentType { get; set; }

        /// <summary>When set, the success response is returned as an SSE stream.</summary>
        public bool SuccessUsesSseContentType { get; set; }

        /// <summary>
        /// When set alongside <see cref="FailuresUseSseContentType"/>, failure bodies are backed by a
        /// stream that never completes — modelling a live error event stream the server holds open.
        /// </summary>
        public bool UseUnboundedFailureStream { get; set; }

        /// <summary>
        /// The exact <c>Content-Type</c> header used for SSE failure bodies. Defaults to the
        /// canonical lowercase form; tests override it to prove casing and parameters are handled.
        /// </summary>
        public string FailureContentType { get; set; } = "text/event-stream";

        /// <summary>The exact <c>Content-Type</c> header used for SSE success bodies.</summary>
        public string SuccessContentType { get; set; } = "text/event-stream";

        /// <summary>Resets the failure budget and the recorded bodies for the next logical request.</summary>
        public void ResetFailures(int failures)
        {
            _remainingFailures = failures;
            Bodies.Clear();
        }

        /// <summary>
        /// Builds content whose <c>Content-Type</c> is set verbatim from <paramref name="contentType"/>,
        /// preserving casing and parameters exactly as a server would send them.
        /// <see cref="StringContent"/>'s constructor normalizes the media type, so the header is
        /// assigned directly instead.
        /// </summary>
        private static HttpContent SseStringContent(string body, string contentType)
        {
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            return content;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));

            if (_remainingFailures > 0)
            {
                if (_remainingFailures != int.MaxValue) _remainingFailures--;

                HttpContent failureContent = FailuresUseSseContentType
                    ? (UseUnboundedFailureStream
                        ? NeverEndingStreamContent.CreateSse(FailureContentType)
                        : SseStringContent("event: error\ndata: {}\n\n", FailureContentType))
                    : new StringContent("""{"error":"transient"}""", Encoding.UTF8, "application/json");

                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    RequestMessage = request,
                    Content = failureContent,
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = SuccessUsesSseContentType
                    ? SseStringContent("event: done\ndata: {}\n\n", SuccessContentType)
                    : new StringContent(SuccessBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// SSE content backed by a stream that never completes, modelling a real error event stream the
    /// server holds open. Reading it to completion blocks forever, so any handler that does so is
    /// caught by a test timeout instead of silently working against finite <c>StringContent</c>.
    /// </summary>
    internal sealed class NeverEndingStreamContent : HttpContent
    {
        private readonly BlockingForeverStream _stream = new();

        public static NeverEndingStreamContent CreateSse(string contentType = "text/event-stream")
        {
            var content = new NeverEndingStreamContent();
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            return content;
        }

        /// <summary>Set once anything attempts to read the body to completion.</summary>
        public bool ReadAttempted => _stream.ReadAttempted;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            // Unknown length, exactly like a live event stream.
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);

        protected override void Dispose(bool disposing)
        {
            _stream.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>A readable stream whose reads never return, until disposed.</summary>
        private sealed class BlockingForeverStream : Stream
        {
            private readonly SemaphoreSlim _blocked = new(0, 1);

            public bool ReadAttempted { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                ReadAttempted = true;
                // Blocks until cancelled or disposed — never yields data, never reports EOF.
                await _blocked.WaitAsync(cancellationToken);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadAttempted = true;
                _blocked.Wait();
                return 0;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) _blocked.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}

/// <summary>
/// Unit tests for <see cref="OllamaExtraHighReasoningClient"/>'s options handling, complementing the
/// real-path wiring tests that drive it through <see cref="OllamaSharp.OllamaApiClient"/>.
/// </summary>
public sealed class OllamaExtraHighReasoningClientTests
{
    /// <summary>
    /// Resolves the <c>Think</c> value that the composed <see cref="ChatOptions.RawRepresentationFactory"/>
    /// will hand to OllamaSharp, invoking it exactly as OllamaSharp does.
    /// </summary>
    private static string? ResolveThink(ChatOptions options, IChatClient suppliedClient)
        => (options.RawRepresentationFactory?.Invoke(suppliedClient)
            as OllamaSharp.Models.Chat.ChatRequest)?.Think?.ToString();

    /// <summary>ExtraHigh installs a factory that supplies <c>Think = max</c>.</summary>
    [Fact]
    public void ApplyExtraHighThink_ExtraHigh_AddsThinkMax()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };

        var applied = client.ApplyExtraHighThink(options)!;

        Assert.NotSame(options, applied);
        Assert.Equal("max", ResolveThink(applied, inner));

        // The caller's options are untouched: no factory installed, no additional properties added.
        Assert.Null(options.RawRepresentationFactory);
        Assert.Null(options.AdditionalProperties);
    }

    /// <summary>Every other effort (and null options) is forwarded untouched, without cloning.</summary>
    [Theory]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.None)]
    public void ApplyExtraHighThink_OtherEfforts_ReturnOriginalInstance(ReasoningEffort effort)
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } };

        Assert.Same(options, client.ApplyExtraHighThink(options));
    }

    /// <summary>Null and reasoning-less options are returned unchanged.</summary>
    [Fact]
    public void ApplyExtraHighThink_NullOrReasoninglessOptions_AreUnchanged()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);

        Assert.Null(client.ApplyExtraHighThink(null));

        var noReasoning = new ChatOptions();
        Assert.Same(noReasoning, client.ApplyExtraHighThink(noReasoning));

        var emptyReasoning = new ChatOptions { Reasoning = new ReasoningOptions() };
        Assert.Same(emptyReasoning, client.ApplyExtraHighThink(emptyReasoning));
    }

    /// <summary>An explicit caller-supplied <c>think</c> value always wins.</summary>
    [Fact]
    public void ApplyExtraHighThink_ExplicitThink_IsPreserved()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };
        options.AdditionalProperties = new AdditionalPropertiesDictionary { ["think"] = false };

        Assert.Same(options, client.ApplyExtraHighThink(options));
    }

    /// <summary>Existing additional properties survive alongside the injected <c>think</c>.</summary>
    [Fact]
    public void ApplyExtraHighThink_PreservesOtherAdditionalProperties()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };
        options.AdditionalProperties = new AdditionalPropertiesDictionary { ["keep_alive"] = "5m" };

        var applied = client.ApplyExtraHighThink(options)!;

        Assert.Equal("max", ResolveThink(applied, inner));
        Assert.Equal("5m", applied.AdditionalProperties!["keep_alive"]);

        // The caller's own dictionary is never touched.
        Assert.False(options.AdditionalProperties.ContainsKey("think"));
        Assert.Null(options.RawRepresentationFactory);
    }

    /// <summary><c>Metadata</c>, <c>GetService</c> and <c>Dispose</c> forward to the inner client.</summary>
    [Fact]
    public void MetadataGetServiceAndDispose_ForwardToInner()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        var client = new OllamaExtraHighReasoningClient(inner);

        Assert.Same(inner.Metadata, client.Metadata);
        Assert.Same(inner.Metadata, client.GetService(typeof(ChatClientMetadata)));

        client.Dispose();
        Assert.True(inner.Disposed);
    }

    /// <summary>
    /// The wrapper disposes an owned transport in addition to the inner client. OllamaSharp only
    /// disposes an HTTP client it created itself, so without this the factory-created transport
    /// (and its handler chain and sockets) would leak.
    /// </summary>
    [Fact]
    public void Dispose_DisposesOwnedTransport()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        var transport = new DisposalTrackingDisposable();
        var client = new OllamaExtraHighReasoningClient(inner, transport);

        client.Dispose();

        Assert.True(inner.Disposed);
        Assert.True(transport.Disposed);
    }

    /// <summary>
    /// A failure while disposing the inner client must not prevent the owned transport from being
    /// disposed, and the original exception must still surface.
    /// </summary>
    [Fact]
    public void Dispose_InnerThrows_StillDisposesTransportAndPropagates()
    {
        var inner = new ThrowingDisposalChatClient();
        var transport = new DisposalTrackingDisposable();
        var client = new OllamaExtraHighReasoningClient(inner, transport);

        var ex = Assert.Throws<InvalidOperationException>(client.Dispose);

        Assert.Equal("inner disposal failed", ex.Message);
        Assert.True(transport.Disposed);
    }

    /// <summary>
    /// A transport-only failure must still surface when the inner client disposes cleanly.
    /// </summary>
    [Fact]
    public void Dispose_TransportThrows_PropagatesAndStillDisposesInner()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        var transport = new ThrowingDisposable("transport disposal failed");
        var client = new OllamaExtraHighReasoningClient(inner, transport);

        var ex = Assert.Throws<InvalidOperationException>(client.Dispose);

        Assert.Equal("transport disposal failed", ex.Message);
        Assert.True(inner.Disposed);
    }

    /// <summary>
    /// When BOTH cleanups fail, the original (inner) exception must not be lost. A plain
    /// <c>try/finally</c> would let the transport's exception replace and hide it; the failures are
    /// aggregated instead, with the primary error first.
    /// </summary>
    [Fact]
    public void Dispose_BothThrow_PreservesOriginalException()
    {
        var inner = new ThrowingDisposalChatClient();
        var transport = new ThrowingDisposable("transport disposal failed");
        var client = new OllamaExtraHighReasoningClient(inner, transport);

        var ex = Assert.Throws<AggregateException>(client.Dispose);

        Assert.Equal(2, ex.InnerExceptions.Count);

        // The inner client's failure is the primary one and comes first.
        Assert.Equal("inner disposal failed", ex.InnerExceptions[0].Message);
        Assert.Equal("transport disposal failed", ex.InnerExceptions[1].Message);
    }

    /// <summary>A disposable whose disposal always throws.</summary>
    private sealed class ThrowingDisposable : IDisposable
    {
        private readonly string _message;

        public ThrowingDisposable(string message) => _message = message;

        public void Dispose() => throw new InvalidOperationException(_message);
    }

    /// <summary>When no transport is owned, disposal is limited to the inner client.</summary>
    [Fact]
    public void Dispose_WithoutOwnedTransport_DisposesOnlyInner()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        var client = new OllamaExtraHighReasoningClient(inner);

        client.Dispose();

        Assert.True(inner.Disposed);
    }

    /// <summary>An explicit raw-representation <c>Think</c> is never overridden.</summary>
    [Fact]
    public void ApplyExtraHighThink_ExplicitRawThink_IsPreserved()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = new OllamaSharp.Models.Chat.ThinkValue("low"),
            },
        };

        var applied = client.ApplyExtraHighThink(options)!;

        Assert.Equal("low", ResolveThink(applied, inner));
    }

    /// <summary>A raw representation without <c>Think</c> gets the injection.</summary>
    [Fact]
    public void ApplyExtraHighThink_RawRepresentationWithoutThink_StillInjects()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { KeepAlive = "9m" },
        };

        var applied = client.ApplyExtraHighThink(options)!;

        Assert.NotSame(options, applied);

        var request = Assert.IsType<OllamaSharp.Models.Chat.ChatRequest>(
            applied.RawRepresentationFactory!(inner));
        Assert.Equal("max", request.Think?.ToString());
        Assert.Equal("9m", request.KeepAlive);
    }

    /// <summary>
    /// The composed factory must invoke the caller's factory exactly once per request. The previous
    /// implementation called it once for inspection and let OllamaSharp call it again, so a stateful
    /// or non-deterministic factory could expose one <c>Think</c> during inspection and another
    /// during serialization.
    /// </summary>
    [Fact]
    public void ApplyExtraHighThink_InvokesCallerFactoryExactlyOncePerRequest()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);

        var invocations = 0;
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ =>
            {
                invocations++;
                return new OllamaSharp.Models.Chat.ChatRequest();
            },
        };

        var applied = client.ApplyExtraHighThink(options)!;

        // Building the options must not have run caller code at all.
        Assert.Equal(0, invocations);

        // One serialization pass == exactly one caller-factory invocation.
        applied.RawRepresentationFactory!(inner);
        Assert.Equal(1, invocations);
    }

    /// <summary>
    /// The composed factory forwards the client OllamaSharp supplies, not this decorator, so a
    /// caller factory that inspects the client sees exactly what it would without the wrapper.
    /// </summary>
    [Fact]
    public void ApplyExtraHighThink_ForwardsSuppliedClientToCallerFactory()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);

        IChatClient? observed = null;
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = c =>
            {
                observed = c;
                return new OllamaSharp.Models.Chat.ChatRequest();
            },
        };

        var applied = client.ApplyExtraHighThink(options)!;
        applied.RawRepresentationFactory!(inner);

        Assert.Same(inner, observed);
        Assert.NotSame(client, observed);
    }

    /// <summary>
    /// A caller factory whose result varies between invocations must still have its ACTUAL
    /// serialized value respected. Under the old inspect-then-rebuild approach the first call
    /// (inspection) decided the outcome while the second call (serialization) produced the request,
    /// so the decision could be based on a value that was never sent.
    /// </summary>
    [Fact]
    public void ApplyExtraHighThink_NonDeterministicFactory_RespectsTheServializedValue()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);

        var call = 0;
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            // First invocation reports no Think, every later one reports an explicit "low".
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = call++ == 0
                    ? (OllamaSharp.Models.Chat.ThinkValue?)null
                    : new OllamaSharp.Models.Chat.ThinkValue("low"),
            },
        };

        var applied = client.ApplyExtraHighThink(options)!;

        // The single serialization invocation is the first call, which has no explicit Think,
        // so "max" is injected onto the very object that gets sent.
        Assert.Equal("max", ResolveThink(applied, inner));
        Assert.Equal(1, call);

        // A subsequent request reports an explicit "low", which must be preserved verbatim.
        Assert.Equal("low", ResolveThink(applied, inner));
        Assert.Equal(2, call);
    }

    /// <summary>
    /// A factory returning an object the mapper cannot use is replaced with a request carrying
    /// <c>Think = max</c>. OllamaSharp discards non-<c>ChatRequest</c> results outright, so passing
    /// one through would forfeit the mapping for no benefit.
    /// </summary>
    [Fact]
    public void ApplyExtraHighThink_UnusableFactoryResult_StillSuppliesThinkMax()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);

        foreach (var factory in new Func<IChatClient, object?>[] { _ => null, _ => "not-a-chat-request" })
        {
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
                RawRepresentationFactory = factory!,
            };

            Assert.Equal("max", ResolveThink(client.ApplyExtraHighThink(options)!, inner));
        }
    }

    /// <summary>Tracks disposal so ownership transfer can be asserted.</summary>
    private sealed class DisposalTrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>An inner client whose disposal throws, to prove cleanup still completes.</summary>
    private sealed class ThrowingDisposalChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => throw new InvalidOperationException("inner disposal failed");
    }

    /// <summary>The streaming entry point applies the same interception.</summary>
    [Fact]
    public async Task GetStreamingResponseAsync_AppliesThinkMax()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using IChatClient client = new OllamaExtraHighReasoningClient(inner);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            TestContext.Current.CancellationToken))
        {
            // drain
        }

        Assert.Equal("max", ResolveThink(inner.LastOptions!, inner));
    }
}

/// <summary>
/// Additional integration tests for the retry-2 iteration: Ollama cloud-style construction
/// and multi-turn conversation reconstruction after retry.
/// </summary>
public sealed class ReasoningEffortRetry2IntegrationTests
{
    /// <summary>
    /// The Ollama real path must work with a cloud-style base address (https://ollama.com) as
    /// well as the local address. The <c>think</c> mapping is address-independent, but this
    /// confirms the <c>OllamaApiClient</c> → <c>OllamaExtraHighReasoningClient</c> stack
    /// doesn't depend on the local <c>/api/chat</c> endpoint shape.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_CloudBaseAddress_ExtraHigh_TerminalReceivesThinkMax()
    {
        var terminal = new ReasoningEffortWiringTests.OllamaTerminalHandler();
        // Build the same stack as CreateOllamaClientForTest but with a cloud-style base address.
        var httpClient = new HttpClient(ChatClientFactory.CreateResilientHandler(
            ChatClientFactory.BuildHandlerChain(
                new ReasoningEffortMappingHandler(
                    ChatClientFactory.OllamaExtraHighMapping,
                    customPropertyName: ChatClientFactory.OllamaReasoningPropertyName),
                terminal)))
        {
            BaseAddress = new Uri("https://ollama.com"),
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var ollamaClient = new OllamaApiClient(httpClient);
        ollamaClient.SelectedModel = "gpt-oss:120b";
        using var client = new OllamaExtraHighReasoningClient(ollamaClient);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            TestContext.Current.CancellationToken);

        Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// Multi-turn conversation reconstruction after a retry on the first request: after the
    /// initial retried request succeeds and accumulates response output, a second follow-up
    /// request must reconstruct the conversation as base + response output + new input (exactly
    /// 3 items), proving no corruption from the retry.
    /// </summary>
    [Fact]
    public async Task MultiTurnConversation_RetryOnFirstRequest_SecondTurnReconstructsCorrectly()
    {
        var terminal = new CopilotResponsesHandlerRetryTests.FailThenSucceedHandler(
            failures: 2, """{"output":[{"type":"message","content":"answer1"}]}""");

        var client = CopilotResponsesHandlerRetryTests.GetClientForExternalUse(
            terminal, out var handler);

        // First request: fails twice, then succeeds.
        using (var first = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        // Verify base input was committed correctly despite the retry.
        Assert.NotNull(handler.BaseInputForTest);
        Assert.Single(handler.BaseInputForTest);
        Assert.Equal("first", handler.BaseInputForTest![0]!["content"]!.GetValue<string>());

        // The response output "answer1" should have been committed to turn history.
        Assert.Contains(handler.TurnHistoryForTest,
            n => n.ToJsonString().Contains("answer1", StringComparison.Ordinal));

        // Second request: no failures, must reconstruct conversation correctly.
        terminal.ResetFailures(0);
        using (var second = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (await client.SendAsync(second, TestContext.Current.CancellationToken)) { }

        var sent = JsonNode.Parse(terminal.Bodies[^1])!;
        var input = sent["input"]!.AsArray();

        // base (1) + response output (1) + current input (1) = 3, no duplicates from retry.
        Assert.Equal(3, input.Count);
        Assert.Equal("first", input[0]!["content"]!.GetValue<string>());
        Assert.Equal("answer1", input[1]!["content"]!.GetValue<string>());
        Assert.Equal("tool-result", input[2]!["output"]!.GetValue<string>());

        // previous_response_id must be stripped on the second request too.
        Assert.Null(sent["previous_response_id"]);

        client.Dispose();
    }
}

/// <summary>
/// Additional integration tests for the retry-3 iteration: failed-SSE removal-proof,
/// real-path transport disposal through the full production stack, and RawRepresentationFactory
/// streaming-path coverage.
/// </summary>
public sealed class ReasoningEffortRetry3IntegrationTests
{
    /// <summary>
    /// REMOVAL-PROOF: a failed SSE response with a follow-up that is also SSE must not corrupt
    /// the base input. This test proves the fix by sending a first request whose every attempt
    /// returns 500 with text/event-stream, then a second clean request and verifying the base
    /// input is from the SECOND request only (not the failed first).
    /// </summary>
    [Fact]
    public async Task FailedSseFirstRequest_SecondCleanRequest_BaseInputFromSecondOnly()
    {
        var terminal = new CopilotResponsesHandlerRetryTests.FailThenSucceedHandler(
            failures: int.MaxValue, """{"output":[]}""")
        {
            FailuresUseSseContentType = true,
        };

        var client = CopilotResponsesHandlerRetryTests.GetClientForExternalUse(terminal, out var handler);

        // First request: all attempts fail with SSE content type.
        using (var first = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"doomed-first"}]}"""))
        using (var response = await client.SendAsync(first, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        // No state should have been committed from the failed first request.
        Assert.Null(handler.BaseInputForTest);
        Assert.Empty(handler.TurnHistoryForTest);

        // Second request: succeeds cleanly (non-SSE).
        terminal.ResetFailures(0);
        terminal.FailuresUseSseContentType = false;
        using (var second = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"clean-second"}]}"""))
        using (await client.SendAsync(second, TestContext.Current.CancellationToken)) { }

        // Base input must be from the second request only.
        Assert.NotNull(handler.BaseInputForTest);
        Assert.Single(handler.BaseInputForTest);
        Assert.Equal("clean-second", handler.BaseInputForTest![0]!["content"]!.GetValue<string>());

        client.Dispose();
    }

    /// <summary>
    /// REAL-PATH disposal: disposing a factory-created Ollama client (via
    /// <see cref="ChatClientFactory.CreateOllamaClientForTest"/>) must dispose the entire injected
    /// transport chain including the terminal handler. This proves no socket/transport leak.
    /// The test verifies the terminal handler's Disposed flag is set after client disposal.
    /// </summary>
    [Fact]
    public void OllamaRealPath_DisposingFactoryClient_DisposesEntireTransportChain()
    {
        var terminal = new ReasoningEffortWiringTests.OllamaTerminalHandler();
        var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        Assert.False(terminal.Disposed);

        client.Dispose();

        // The entire chain (OllamaExtraHighReasoningClient → OllamaApiClient → HttpClient →
        // ResilienceHandler → ReasoningEffortMappingHandler → terminal) must be disposed.
        Assert.True(terminal.Disposed);
    }

    /// <summary>
    /// REAL-PATH streaming with RawRepresentationFactory: the streaming entry point must also
    /// respect an explicit raw Think value, not just the non-streaming path.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_Streaming_RawRepresentationThink_IsNotOverridden()
    {
        var terminal = new ReasoningEffortWiringTests.OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = new OllamaSharp.Models.Chat.ThinkValue("low"),
            },
        };

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken))
        {
            // drain
        }

        Assert.Equal("low", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }
}

/// <summary>
/// Additional integration tests for the retry-4 iteration: well-formed non-SSE success commit,
/// real-path single factory invocation, and real-path non-deterministic factory.
/// </summary>
public sealed class ReasoningEffortRetry4IntegrationTests
{
    /// <summary>
    /// A well-formed non-SSE 2xx response must commit both base input and response output to turn
    /// history exactly once. This complements the malformed-success tests by proving the happy path
    /// still commits — the fix must not swing too far and drop state for valid responses.
    /// </summary>
    [Fact]
    public async Task WellFormedNonSseSuccess_CommitsBaseInputAndTurnHistoryExactlyOnce()
    {
        var terminal = new CopilotResponsesHandlerRetryTests.FailThenSucceedHandler(
            failures: 0, """{"output":[{"type":"message","content":"answer"}]}""");
        var client = CopilotResponsesHandlerRetryTests.GetClientForExternalUse(terminal, out var handler);

        using (var request = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(request, TestContext.Current.CancellationToken)) { }

        // Base input committed from the request.
        Assert.NotNull(handler.BaseInputForTest);
        Assert.Single(handler.BaseInputForTest);
        Assert.Equal("first", handler.BaseInputForTest![0]!["content"]!.GetValue<string>());

        // Response output committed to turn history (the "answer" message).
        Assert.Contains(handler.TurnHistoryForTest,
            n => n.ToJsonString().Contains("answer", StringComparison.Ordinal));
        Assert.Single(handler.TurnHistoryForTest);

        client.Dispose();
    }

    /// <summary>
    /// REAL-PATH: the composed RawRepresentationFactory must be invoked exactly once through the
    /// full OllamaApiClient stack. The old implementation invoked it once for inspection and
    /// OllamaSharp invoked it again for serialization, so a stateful factory could observe two calls.
    /// This test drives the real path and asserts the caller's factory is invoked exactly once.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ComposedFactory_InvokedExactlyOnce()
    {
        var terminal = new ReasoningEffortWiringTests.OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var invocations = 0;
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ =>
            {
                invocations++;
                return new OllamaSharp.Models.Chat.ChatRequest();
            },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        Assert.Equal(1, invocations);
        Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL-PATH: a non-deterministic factory whose first invocation returns no Think and whose
    /// second returns an explicit "low" must have the first call's result serialized (with max
    /// injected), not the second. Under the old inspect-then-rebuild approach the inspection call
    /// (first) would decide "no Think → inject max" while the serialization call (second) would
    /// produce a request with Think="low", and the additional-property would overwrite it to "max".
    /// With composition, only one call happens, so the result is consistent.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_NonDeterministicFactory_SingleInvocationProducesMax()
    {
        var terminal = new ReasoningEffortWiringTests.OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var call = 0;
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = call++ == 0
                    ? (OllamaSharp.Models.Chat.ThinkValue?)null
                    : new OllamaSharp.Models.Chat.ThinkValue("low"),
            },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        // Only one invocation happened, and it returned no Think, so max was injected.
        Assert.Equal(1, call);
        Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }
}
