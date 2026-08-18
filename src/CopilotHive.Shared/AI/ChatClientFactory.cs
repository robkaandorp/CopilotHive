#pragma warning disable CS1591
#pragma warning disable OPENAI001 // ResponsesClient.AsIChatClient is experimental
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Http.Resilience;

using OllamaSharp;

using OpenAI;

using Polly;

using System.ClientModel;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotHive.Shared.AI;

/// <summary>
/// Creates <see cref="IChatClient"/> instances for various LLM providers.
/// Shared between Worker and Orchestrator (Brain).
/// </summary>
public static class ChatClientFactory
{
    private static Func<string?>? _tokenProvider;

    /// <summary>
    /// Registers a provider that supplies the GitHub access token to use for the Copilot
    /// provider. When set and it returns a non-null value, the provided token is used in
    /// preference to the <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> environment variables. This is the
    /// bridge that lets the orchestrator inject the OAuth access token stored in the database
    /// without this shared class depending on the main project.
    /// </summary>
    public static void SetTokenProvider(Func<string?> provider) => _tokenProvider = provider;

    /// <summary>
    /// Creates an <see cref="IChatClient"/> for the given model string.
    /// The model string may include a provider prefix (e.g. "copilot/claude-sonnet-4.6").
    /// Reasoning effort is applied at the <see cref="ChatOptions"/> level, not via the model name.
    /// </summary>
    public static IChatClient Create(string? modelOverride = null)
    {
        var (provider, model) = ParseProviderAndModel(modelOverride);

        switch (provider)
        {
            case "ollama-cloud":
                {
                    var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
                    if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("OLLAMA_API_KEY is required for ollama-cloud provider");

                    var httpClient = new HttpClient(CreateOllamaHandlerChain())
                    {
                        BaseAddress = new Uri("https://ollama.com"),
                        Timeout = Timeout.InfiniteTimeSpan,
                    };
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                    model ??= Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "gpt-oss:120b";
                    var ollamaClient = new OllamaApiClient(httpClient);
                    ollamaClient.SelectedModel = model;

                    // OllamaSharp collapses ExtraHigh into "high" during ChatOptions→request
                    // mapping, so the distinction must be preserved before serialization.
                    // See OllamaExtraHighReasoningClient for the investigation and mechanism.
                    //
                    // The wrapper is also given ownership of httpClient: OllamaSharp only disposes
                    // an HTTP client it created itself, so an injected one would otherwise leak its
                    // handler chain and sockets.
                    return new OllamaExtraHighReasoningClient(ollamaClient, httpClient);
                }

            case "ollama-local":
                {
                    var url = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
                    model ??= Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3";

                    // Converted from `new OllamaApiClient(new Uri(url))` to HttpClient-based
                    // construction so the reasoning-effort mapping handler can be injected into the
                    // transport chain (see CreateOllamaHandlerChain). OllamaApiClient(HttpClient)
                    // uses the client's BaseAddress as the API endpoint, so behaviour is otherwise
                    // identical apart from the (previously 100 s) default timeout, which is now
                    // infinite — consistent with every other provider here, since reasoning models
                    // routinely exceed the default. The conversion also moves HttpClient ownership
                    // to us (OllamaSharp only disposes clients it created itself), which is why the
                    // returned wrapper is handed the instance to dispose.
                    var httpClient = new HttpClient(CreateOllamaHandlerChain())
                    {
                        BaseAddress = new Uri(url),
                        Timeout = Timeout.InfiniteTimeSpan,
                    };

                    var ollamaClient = new OllamaApiClient(httpClient);
                    ollamaClient.SelectedModel = model;

                    // See the ollama-cloud branch / OllamaExtraHighReasoningClient for why the
                    // ExtraHigh mapping must happen at the ChatOptions level, and why the wrapper
                    // takes ownership of the injected httpClient.
                    return new OllamaExtraHighReasoningClient(ollamaClient, httpClient);
                }

            case "github":
                {
                    var token = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                    if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("GH_TOKEN or GITHUB_TOKEN is required for github provider");

                    model ??= Environment.GetEnvironmentVariable("GITHUB_MODEL") ?? "openai/gpt-4.1";

                    var openAiClient = new OpenAIClient(
                        new ApiKeyCredential(token),
                        new OpenAIClientOptions { Endpoint = new Uri("https://models.github.ai") }
                    );

                    // GitHub Models does not accept a reasoning effort above "high"; clamp the
                    // internal ExtraHigh level at the provider boundary. The wrapper owns the
                    // inner client's disposal.
                    return new ReasoningEffortClampingClient(
                        openAiClient.GetChatClient(model).AsIChatClient(), ReasoningEffort.High);
                }

            case "copilot":
                return CreateCopilotClient(model ?? Environment.GetEnvironmentVariable("COPILOT_MODEL") ?? "claude-sonnet-4.6");

            default:
                throw new InvalidOperationException($"Unknown LLM provider: '{provider}'");
        }
    }

    /// <summary>
    /// Extracts an optional provider prefix from the model string.
    /// "copilot/claude-sonnet-4.6" → ("copilot", "claude-sonnet-4.6")
    /// "claude-sonnet-4.6" → (env LLM_PROVIDER, "claude-sonnet-4.6")
    /// null → (env LLM_PROVIDER, null)
    /// </summary>
    public static (string provider, string? model) ParseProviderAndModel(string? modelOverride)
    {
        var defaultProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        if (string.IsNullOrEmpty(modelOverride))
            return (defaultProvider, null);

        var slashIndex = modelOverride.IndexOf('/');
        if (slashIndex <= 0)
            return (defaultProvider, modelOverride);

        var prefix = modelOverride.Substring(0, slashIndex).ToLowerInvariant();

        // Only treat as provider prefix if it matches a known provider.
        if (prefix is "copilot" or "ollama-cloud" or "ollama-local" or "github")
            return (prefix, modelOverride.Substring(slashIndex + 1));

        return (defaultProvider, modelOverride);
    }

    /// <summary>
    /// Models that must use the /responses endpoint instead of /chat/completions.
    /// </summary>
    public static bool RequiresResponsesEndpoint(string model)
    {
        return model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The value the Ollama API uses for the highest thinking level, mapped from the internal
    /// <c>extra_high</c> wire value.
    /// </summary>
    internal const string OllamaExtraHighMapping = "max";

    /// <summary>The Ollama request property carrying the reasoning/thinking level.</summary>
    internal const string OllamaReasoningPropertyName = "think";

    /// <summary>
    /// Builds the Ollama transport chain: <c>ResilienceHandler → ReasoningEffortMappingHandler →
    /// HttpClientHandler</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is defence-in-depth only. The load-bearing ExtraHigh mechanism for Ollama is
    /// <see cref="OllamaExtraHighReasoningClient"/>, which acts at the <see cref="ChatOptions"/>
    /// level — see that type for the full investigation. The handler still runs so that a
    /// hand-built body (or a future OllamaSharp that emits the canonical <c>extra_high</c> value,
    /// or a <c>reasoning_effort</c>/<c>reasoning.effort</c> property) is normalized too. It never
    /// rewrites <c>"high"</c>, which is indistinguishable from a genuine
    /// <see cref="ReasoningEffort.High"/> request.
    /// </para>
    /// </remarks>
    private static HttpMessageHandler CreateOllamaHandlerChain() =>
        CreateResilientHandler(BuildHandlerChain(
            new ReasoningEffortMappingHandler(OllamaExtraHighMapping, customPropertyName: OllamaReasoningPropertyName),
            new HttpClientHandler()));

    /// <summary>
    /// Composes an <see cref="HttpMessageHandler"/> chain from outermost to innermost without
    /// disturbing links that are already wired up by the caller.
    /// </summary>
    /// <param name="handlers">
    /// The handlers in outermost→innermost order. Every element except the last must be a
    /// <see cref="DelegatingHandler"/>; the last element is the terminal handler (typically an
    /// <see cref="HttpClientHandler"/> or another handler that actually performs the transport).
    /// </param>
    /// <returns>The outermost handler of the composed chain.</returns>
    /// <remarks>
    /// This replaces the previous pattern where <see cref="CreateResilientHandler"/> assigned
    /// <c>InnerHandler</c> on a caller-supplied handler that might already have had one — silently
    /// disconnecting the existing chain. <c>BuildHandlerChain</c> links each handler to its
    /// successor exactly once, so the caller decides the full order.
    /// </remarks>
    internal static HttpMessageHandler BuildHandlerChain(params HttpMessageHandler[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        if (handlers.Length == 0)
            throw new ArgumentException("At least one handler is required.", nameof(handlers));

        for (var i = 0; i < handlers.Length - 1; i++)
        {
            if (handlers[i] is not DelegatingHandler delegating)
                throw new ArgumentException(
                    $"Handler at index {i} ({handlers[i].GetType().Name}) must be a DelegatingHandler because it is not the terminal handler.",
                    nameof(handlers));

            delegating.InnerHandler = handlers[i + 1];
        }

        return handlers[0];
    }

    /// <summary>
    /// Creates a resilient <see cref="HttpMessageHandler"/> chain with per-attempt timeout
    /// and retry policy.
    /// </summary>
    /// <param name="innerHandler">
    /// Optional handler placed <em>beneath</em> the resilience handler. When <see langword="null"/>,
    /// a fresh <see cref="HttpClientHandler"/> is used. The supplied handler's own
    /// <c>InnerHandler</c> is never overwritten, so callers can pass an already-composed chain
    /// (e.g. built with <see cref="BuildHandlerChain"/>).
    /// </param>
    /// <param name="retryDelay">
    /// Optional override for the base retry delay. Production uses the default 5 s; tests pass a
    /// near-zero value so retry behaviour can be exercised without slowing the suite. The retry
    /// count, backoff type and timeout are identical either way, so tests still exercise the real
    /// strategy.
    /// </param>
    internal static HttpMessageHandler CreateResilientHandler(
        HttpMessageHandler? innerHandler = null, TimeSpan? retryDelay = null)
    {
        var retryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = retryDelay ?? TimeSpan.FromSeconds(5),
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = static args =>
                {
                    Console.Error.WriteLine(
                        $"[Resilience] HTTP retry #{args.AttemptNumber} after {args.RetryDelay.TotalSeconds:F0}s — " +
                        (args.Outcome.Exception?.Message ?? $"HTTP {(int?)args.Outcome.Result?.StatusCode}"));
                    return default;
                },
            })
            .AddTimeout(TimeSpan.FromMinutes(10))
            .Build();

        return BuildHandlerChain(
            new ResilienceHandler(retryPipeline),
            innerHandler ?? new HttpClientHandler());
    }

    /// <summary>
    /// The value the GitHub Copilot API expects for the internal <c>extra_high</c> reasoning effort.
    /// The OpenAI SDK serializes <see cref="ReasoningEffort.ExtraHigh"/> as <c>"xhigh"</c> already,
    /// but our own wire format uses <c>extra_high</c>; the mapping handler normalizes both.
    /// </summary>
    internal const string CopilotExtraHighMapping = "xhigh";

    private static IChatClient CreateCopilotClient(string model)
    {
        var oauthToken = _tokenProvider?.Invoke();
        var ghToken = oauthToken
            ?? Environment.GetEnvironmentVariable("GH_TOKEN")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(ghToken)) throw new InvalidOperationException("GH_TOKEN or GITHUB_TOKEN is required for copilot provider");

        var useResponsesApi = RequiresResponsesEndpoint(model);
        var httpClient = new HttpClient(CreateCopilotHandlerChain(
            useResponsesApi, CopilotExtraHighMapping, new HttpClientHandler()))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var openAiClient = new OpenAIClient(
            new ApiKeyCredential(ghToken),
            new OpenAIClientOptions
            {
                Endpoint = new Uri("https://api.githubcopilot.com"),
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient),
                NetworkTimeout = TimeSpan.FromMinutes(30)
            }
        );

        return useResponsesApi
            ? openAiClient.GetResponsesClient().AsIChatClient(model)
            : openAiClient.GetChatClient(model).AsIChatClient();
    }

    /// <summary>
    /// Builds the outbound handler chain used for the Copilot provider:
    /// <c>ResilienceHandler → CopilotResponsesHandler/CopilotChoiceMergingHandler →
    /// ReasoningEffortMappingHandler → terminalHandler</c>.
    /// </summary>
    /// <remarks>
    /// Order matters: the Copilot handler rewrites the request body first (tool-call argument
    /// fix-ups / responses-API input reconstruction), then the mapping handler translates
    /// <c>extra_high</c> into the provider spelling on the final body, then the terminal handler
    /// transmits it.
    /// </remarks>
    private static HttpMessageHandler CreateCopilotHandlerChain(
        bool useResponsesApi, string extraHighMapping, HttpMessageHandler terminalHandler)
        => CreateCopilotHandlerChain(useResponsesApi, extraHighMapping, terminalHandler, out _);

    /// <summary>
    /// Builds the Copilot chain and also hands back the Copilot handler instance, so tests can
    /// assert on its accumulated conversation state.
    /// </summary>
    private static HttpMessageHandler CreateCopilotHandlerChain(
        bool useResponsesApi, string extraHighMapping, HttpMessageHandler terminalHandler,
        out DelegatingHandler copilotHandler, TimeSpan? retryDelay = null)
    {
        copilotHandler = useResponsesApi
            ? new CopilotResponsesHandler()
            : new CopilotChoiceMergingHandler();

        return CreateResilientHandler(
            BuildHandlerChain(
                copilotHandler,
                new ReasoningEffortMappingHandler(extraHighMapping),
                terminalHandler),
            retryDelay);
    }

    /// <summary>
    /// Test seam: builds an <see cref="HttpClient"/> over the FULL production Copilot handler
    /// chain (resilience → Copilot handler → reasoning-effort mapping) but with an injectable
    /// terminal handler, so the chain can be exercised without any network access.
    /// </summary>
    /// <param name="useResponsesApi">Whether to use the /responses branch of the chain.</param>
    /// <param name="extraHighMapping">The provider value <c>extra_high</c> maps to.</param>
    /// <param name="terminalHandler">The innermost handler that replaces the real transport.</param>
    internal static HttpClient CreateCopilotClientForTest(
        bool useResponsesApi, string extraHighMapping, HttpMessageHandler terminalHandler)
        => CreateCopilotClientForTest(useResponsesApi, extraHighMapping, terminalHandler, out _);

    /// <summary>
    /// Test seam overload that also exposes the Copilot handler instance, so retry tests can assert
    /// that its conversation state was not corrupted or duplicated.
    /// </summary>
    /// <param name="useResponsesApi">Whether to use the /responses branch of the chain.</param>
    /// <param name="extraHighMapping">The provider value <c>extra_high</c> maps to.</param>
    /// <param name="terminalHandler">The innermost handler that replaces the real transport.</param>
    /// <param name="copilotHandler">The Copilot handler instance inside the chain.</param>
    /// <param name="retryDelay">Optional retry-delay override so retry tests run fast.</param>
    internal static HttpClient CreateCopilotClientForTest(
        bool useResponsesApi, string extraHighMapping, HttpMessageHandler terminalHandler,
        out DelegatingHandler copilotHandler, TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(terminalHandler);
        var chain = CreateCopilotHandlerChain(
            useResponsesApi, extraHighMapping, terminalHandler, out copilotHandler, retryDelay);
        return new HttpClient(chain)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>
    /// Test seam: builds the FULL production Ollama client stack —
    /// <see cref="OllamaExtraHighReasoningClient"/> over an <see cref="OllamaApiClient"/> whose
    /// transport is the production handler chain (resilience → reasoning-effort mapping) — but with
    /// an injectable terminal handler, so the real
    /// <c>ChatOptions → OllamaApiClient → handler → terminal</c> path can be exercised offline.
    /// </summary>
    /// <param name="model">The model to select on the underlying client.</param>
    /// <param name="terminalHandler">The innermost handler that replaces the real transport.</param>
    internal static IChatClient CreateOllamaClientForTest(string model, HttpMessageHandler terminalHandler)
    {
        ArgumentNullException.ThrowIfNull(terminalHandler);

        var httpClient = new HttpClient(CreateResilientHandler(BuildHandlerChain(
            new ReasoningEffortMappingHandler(OllamaExtraHighMapping, customPropertyName: OllamaReasoningPropertyName),
            terminalHandler)))
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var ollamaClient = new OllamaApiClient(httpClient);
        ollamaClient.SelectedModel = model;

        // Mirror production ownership so disposal behaviour is exercised by tests too.
        return new OllamaExtraHighReasoningClient(ollamaClient, httpClient);
    }

    /// <summary>
    /// The GitHub Copilot API splits tool_calls and text content into separate choices.
    /// The OpenAI SDK only reads choices[0], losing the tool_calls. This handler
    /// merges all choices into a single choice so the SDK sees both text and tool_calls.
    /// </summary>
    internal sealed class CopilotChoiceMergingHandler : DelegatingHandler
    {
        private int _requestCount;
        private static readonly string CompletionsLogDir =
            Environment.GetEnvironmentVariable("DIAGNOSTICS_DIR") ?? Path.Combine(Path.GetTempPath(), "copilothive-diagnostics");

        public CopilotChoiceMergingHandler() { }
        public CopilotChoiceMergingHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var seq = Interlocked.Increment(ref _requestCount);

            if (request.Content != null)
            {
                var reqBody = await request.Content.ReadAsStringAsync();
                LogCompletionsExchange(seq, "request", reqBody);
                // Fix tool_call arguments that the Copilot API proxy can't parse.
                // Claude streaming returns empty arguments ("") for parameterless calls,
                // which the SDK accumulates as the string "null". The Copilot→Anthropic
                // proxy needs a valid JSON object string for tool_use.input.
                reqBody = FixToolCallArguments(reqBody);
                request.Content = new StringContent(reqBody, System.Text.Encoding.UTF8, "application/json");
            }

            var response = await base.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                LogCompletionsExchange(seq, "error", $"HTTP {(int)response.StatusCode}: {errBody}");
                response.Content = new StringContent(errBody, System.Text.Encoding.UTF8, "application/json");
                return response;
            }

            var body = await response.Content.ReadAsStringAsync();
            LogCompletionsExchange(seq, "response", body);

            try
            {
                var json = JsonNode.Parse(body);
                var choices = json?["choices"]?.AsArray();

                // Empty choices array: Copilot API sometimes returns this after the final
                // tool result is sent back. Synthesize a minimal stop choice so the SDK
                // doesn't crash with "Index was out of range" on choices[0].
                if (choices is { Count: 0 } && json is not null)
                {
                    json["choices"] = new JsonArray(
                        JsonNode.Parse("""{"index":0,"message":{"role":"assistant","content":""},"finish_reason":"stop"}"""));
                    body = json.ToJsonString();
                    return ReplaceContent(response, body);
                }

                if (choices == null || choices.Count <= 1) return ReplaceContent(response, body);

                JsonObject? toolChoice = null;
                string? textContent = null;

                foreach (var c in choices)
                {
                    if (c == null) continue;
                    var msg = c["message"];
                    if (msg == null) continue;

                    if (msg["tool_calls"] is JsonArray { Count: > 0 })
                        toolChoice = c.AsObject();
                    else if (msg["content"] is JsonValue val && val.TryGetValue<string>(out var text) && text.Length > 0)
                        textContent = text;
                }

                if (toolChoice != null)
                {
                    if (textContent != null && toolChoice["message"] is JsonObject merged)
                    {
                        merged["content"] = textContent;
                    }

                    toolChoice.Parent?.AsArray().Remove(toolChoice);
                    json!["choices"] = new JsonArray(toolChoice);
                    body = json.ToJsonString();
                }
            }
            catch (Exception)
            {
                // If merging fails, return the original response unchanged
            }

            return ReplaceContent(response, body);
        }

        private static HttpResponseMessage ReplaceContent(HttpResponseMessage response, string body)
        {
            response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return response;
        }

        private static void LogCompletionsExchange(int seq, string phase, string content)
        {
            try
            {
                var dir = Path.Combine(CompletionsLogDir, "chat-completions");
                Directory.CreateDirectory(dir);
                var fileName = $"{seq:D4}_{phase}.json";
                File.WriteAllText(Path.Combine(dir, fileName), content);
            }
            catch { /* best-effort logging */ }
        }

        /// <summary>
        /// Fixes tool_call arguments in outgoing request messages.
        /// When Claude streams a tool call with no arguments, the SDK accumulates
        /// the empty argument chunks as the literal string "null". The Copilot API
        /// proxy expects a valid JSON object for Anthropic's tool_use.input field.
        /// </summary>
        internal static string FixToolCallArguments(string requestBody)
        {
            try
            {
                var json = JsonNode.Parse(requestBody);
                var messages = json?["messages"]?.AsArray();
                if (messages is null) return requestBody;

                bool modified = false;
                foreach (var msg in messages)
                {
                    var toolCalls = msg?["tool_calls"]?.AsArray();
                    if (toolCalls is null) continue;

                    foreach (var tc in toolCalls)
                    {
                        var func = tc?["function"];
                        if (func is null) continue;

                        var argsNode = func["arguments"];
                        var argsStr = argsNode?.GetValue<string>();
                        if (argsStr is null or "" or "null")
                        {
                            func["arguments"] = "{}";
                            modified = true;
                        }
                    }
                }

                return modified ? json!.ToJsonString() : requestBody;
            }
            catch
            {
                return requestBody;
            }
        }
    }

    /// <summary>
    /// The Copilot API doesn't support previous_response_id on the /responses endpoint.
    /// This handler inlines the previous response's output into follow-up requests.
    /// </summary>
    /// <summary>
    /// Handles Responses API requests for Copilot, which doesn't support previous_response_id.
    /// Strips previous_response_id and reconstructs the full conversation by carrying forward
    /// the original input messages (system + user) and all turn history (outputs + tool results).
    /// </summary>
    internal sealed class CopilotResponsesHandler : DelegatingHandler
    {
        private JsonArray? _baseInput;
        private readonly List<JsonNode> _turnHistory = new();
        private int _requestCount;
        private static readonly string ResponsesLogDir =
            Environment.GetEnvironmentVariable("DIAGNOSTICS_DIR") ?? Path.Combine(Path.GetTempPath(), "copilothive-diagnostics");

        /// <summary>
        /// Conversation state produced by the request transformation, staged on the request itself
        /// so it survives across resilience retry attempts and is committed by whichever attempt
        /// finally receives an authoritative response.
        /// </summary>
        private sealed class PendingConversationState
        {
            public JsonArray? BaseInput { get; init; }
            public List<JsonNode>? TurnHistory { get; init; }

            /// <summary>Guards against committing the same staged state more than once.</summary>
            public bool Committed { get; set; }
        }

        /// <summary>
        /// Carries the transformation result across retry attempts of the same request.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This handler sits <em>beneath</em> the <c>ResilienceHandler</c>, so a retry re-enters
        /// <see cref="SendAsync"/> with the <b>same</b> <see cref="HttpRequestMessage"/> instance
        /// carrying the <b>already-transformed</b> body (verified with a forced-transient-failure
        /// probe: Polly reuses the request instance and preserves both <c>request.Options</c> and any
        /// replaced <c>Content</c> across attempts). Each attempt is a fresh <see cref="SendAsync"/>
        /// stack frame, so anything staged in a local would be lost by the attempt that actually
        /// succeeds — the staged state therefore has to live on the request.
        /// </para>
        /// <para>
        /// The presence of this entry also serves as the idempotence marker. Without it a retry
        /// would see a body that no longer contains <c>previous_response_id</c> — because the first
        /// attempt removed it — and would fall into the "first request" branch, overwriting
        /// <see cref="_baseInput"/> with the fully expanded conversation and corrupting every later
        /// reconstruction. <see cref="HttpRequestOptions"/> travels with the request instance and is
        /// never sent over the wire, which makes it the right place for this attempt-scoped state.
        /// </para>
        /// </remarks>
        private static readonly HttpRequestOptionsKey<PendingConversationState> PendingStateKey =
            new("CopilotHive.CopilotResponsesHandler.PendingState");

        public CopilotResponsesHandler() { }
        public CopilotResponsesHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var seq = Interlocked.Increment(ref _requestCount);

            var isResponsesRequest = request.Content != null
                && request.RequestUri?.AbsolutePath?.Contains("responses") == true;

            // Idempotence guard: on a retry the body is already transformed and the resulting
            // conversation state is already staged on the request, so re-running the transformation
            // would mutate the body a second time. Reuse the staged state instead.
            var hasPendingState = request.Options.TryGetValue(PendingStateKey, out var pending);

            if (isResponsesRequest && !hasPendingState)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                var json = JsonNode.Parse(body);

                if (json is JsonObject obj && obj.ContainsKey("previous_response_id"))
                {
                    obj.Remove("previous_response_id");

                    var combined = new JsonArray();

                    // 1. Original system + user messages from the first request
                    if (_baseInput is not null)
                        foreach (var item in _baseInput)
                            combined.Add(item!.DeepClone());

                    // 2. All accumulated turn history (previous outputs + tool results)
                    foreach (var item in _turnHistory)
                        combined.Add(item.DeepClone());

                    // 3. Current input (new tool results from FunctionInvokingChatClient)
                    List<JsonNode>? stagedTurnHistory = null;
                    if (obj["input"] is JsonArray currentInput)
                    {
                        stagedTurnHistory = new List<JsonNode>(currentInput.Count);
                        foreach (var item in currentInput)
                        {
                            var clone = item!.DeepClone();
                            combined.Add(clone);
                            // Staged, not committed: only an authoritative response promotes these.
                            stagedTurnHistory.Add(item.DeepClone());
                        }
                    }

                    obj["input"] = combined;
                    body = obj.ToJsonString();
                    request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

                    pending = new PendingConversationState { TurnHistory = stagedTurnHistory };
                    request.Options.Set(PendingStateKey, pending);
                }
                else if (json is JsonObject firstObj)
                {
                    // First request — stage the original input as the base context. Committing it
                    // before the response would let a retry (which re-reads an already-expanded
                    // body) overwrite it with the expanded conversation.
                    pending = new PendingConversationState
                    {
                        BaseInput = firstObj["input"]?.DeepClone() as JsonArray,
                    };
                    request.Options.Set(PendingStateKey, pending);
                }

                LogResponsesExchange(seq, "request", body);
            }

            var response = await base.SendAsync(request, ct);

            var contentType = response.Content?.Headers?.ContentType?.MediaType;

            // HTTP media types are case-insensitive, and the media type may be accompanied by
            // parameters (e.g. "Text/Event-Stream; charset=utf-8"). ContentType.MediaType already
            // strips parameters but preserves the sender's casing, so the comparison must ignore
            // case. Getting this wrong is not cosmetic: a failed response whose casing differs would
            // fall through to ReadAsStringAsync below and consume an unbounded event stream,
            // blocking until timeout and preventing the resilience layer from retrying.
            var isStreaming = string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

            if (!response.IsSuccessStatusCode)
            {
                // Failure — streaming or not — must never commit conversation state. The resilience
                // handler above may retry, which re-enters this method with the same request
                // instance; the staged state makes that attempt skip the transformation and lets it
                // commit exactly once if it succeeds. If the retries are exhausted the caller sees
                // the error and no partial conversation state was ever recorded.
                //
                // This branch deliberately precedes the streaming check: an error response can
                // carry a text/event-stream content type (the Copilot API echoes the requested
                // stream mode on some 429/5xx replies), and committing on it would leave phantom
                // base input / turn history behind.
                if (isStreaming)
                {
                    // Never read a failed event stream to completion: a real SSE error body can stay
                    // open indefinitely, which would block here until the timeout fires and prevent
                    // the resilience handler above from ever observing the failure status and
                    // retrying. Log the status only and hand the response straight back.
                    LogResponsesExchange(seq, "error",
                        $"HTTP {(int)response.StatusCode}: <streaming error body not read>");
                }
                else if (response.Content is not null)
                {
                    var errBody = await response.Content.ReadAsStringAsync(ct);
                    LogResponsesExchange(seq, "error", $"HTTP {(int)response.StatusCode}: {errBody}");
                }
                else
                {
                    LogResponsesExchange(seq, "error", $"HTTP {(int)response.StatusCode}: <no content>");
                }

                return response;
            }

            // Successful streaming response: the SSE stream must be consumed directly by the
            // OpenAI SDK's streaming parser, so this is the last point at which the exchange can be
            // treated as authoritative. Commit here, before handing the untouched stream back.
            if (isStreaming)
            {
                CommitConversationState(pending);
                return response;
            }

            // Successful non-streaming response: read and parse the authoritative body BEFORE
            // committing. A 2xx carrying a missing, truncated or malformed body means the operation
            // did not actually complete, and committing first would poison every later
            // reconstruction with state from a request whose result the caller never received.
            if (response.Content is null)
                throw new InvalidOperationException(
                    $"Copilot responses endpoint returned HTTP {(int)response.StatusCode} with no content.");

            var respBody = await response.Content.ReadAsStringAsync(ct);

            JsonNode? respJson;
            try
            {
                respJson = JsonNode.Parse(respBody);
            }
            catch (JsonException ex)
            {
                LogResponsesExchange(seq, "error",
                    $"HTTP {(int)response.StatusCode}: unparseable response body: {respBody}");
                throw new InvalidOperationException(
                    "Copilot responses endpoint returned a successful status with a malformed JSON body.", ex);
            }

            // The body parsed syntactically, but that is not enough to call the exchange complete:
            // the response output must also be structurally sound. Materialize every output item
            // into a fully-detached clone FIRST, so any structural failure (e.g. a null element in
            // the output array) throws before a single piece of durable state has been touched.
            // Only once everything is validated is the state committed, atomically.
            List<JsonNode>? responseOutput = null;
            if (respJson?["output"] is JsonArray outputArray)
            {
                responseOutput = new List<JsonNode>(outputArray.Count);
                for (var i = 0; i < outputArray.Count; i++)
                {
                    var item = outputArray[i];
                    if (item is null)
                    {
                        LogResponsesExchange(seq, "error",
                            $"HTTP {(int)response.StatusCode}: output[{i}] is null: {respBody}");
                        throw new InvalidOperationException(
                            $"Copilot responses endpoint returned a successful status with a structurally invalid " +
                            $"response: output[{i}] is null.");
                    }

                    responseOutput.Add(item.DeepClone());
                }
            }

            // Response-content replacement comes next, still before any durable state is touched.
            // If building the replacement fails, the exchange has not been fully processed, so the
            // caller must see the error with the conversation state left exactly as it was.
            response.Content = ResponseContentFactory(respBody);

            LogResponsesExchange(seq, "response", respBody);

            // COMMIT — the final step. Everything fallible is done: the body is read, parsed,
            // structurally validated, materialized into detached clones, and the response content is
            // replaced. Only now is durable state mutated. Request-side state goes in first so turn
            // history stays in request→response order, then the already-materialized response
            // output. Neither can fail part-way, and nothing after this point can throw.
            CommitConversationState(pending);

            if (responseOutput is not null)
                _turnHistory.AddRange(responseOutput);

            return response;
        }

        /// <summary>
        /// Builds the replacement content for a successful non-streaming response.
        /// </summary>
        /// <remarks>
        /// Overridable as a test seam so tests can observe the exact moment of response-content
        /// replacement, and force it to fail, in order to prove that the conversation-state commit
        /// happens strictly afterwards. It is an instance member so each handler — and therefore
        /// each test — is independent, with no shared static state to reset.
        /// </remarks>
        internal Func<string, HttpContent> ResponseContentFactory { get; set; } =
            static body => new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        /// <summary>
        /// Promotes state staged for this request into the durable conversation state. Called only
        /// once an authoritative response has been received, and at most once per staged state.
        /// </summary>
        private void CommitConversationState(PendingConversationState? pending)
        {
            if (pending is null || pending.Committed) return;
            pending.Committed = true;

            if (pending.BaseInput is not null)
                _baseInput = pending.BaseInput;

            if (pending.TurnHistory is not null)
                _turnHistory.AddRange(pending.TurnHistory);
        }

        /// <summary>Test accessor: the committed base input, or <see langword="null"/> if none.</summary>
        internal JsonArray? BaseInputForTest => _baseInput;

        /// <summary>Test accessor: the committed turn history.</summary>
        internal IReadOnlyList<JsonNode> TurnHistoryForTest => _turnHistory;

        private static void LogResponsesExchange(int seq, string phase, string content)
        {
            try
            {
                var dir = Path.Combine(ResponsesLogDir, "responses-api");
                Directory.CreateDirectory(dir);
                var fileName = $"{seq:D4}_{phase}.json";
                File.WriteAllText(Path.Combine(dir, fileName), content);
            }
            catch { /* best-effort logging */ }
        }
    }
}
