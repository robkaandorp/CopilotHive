#pragma warning disable CS1591
#pragma warning disable OPENAI001 // ResponsesClient.AsIChatClient is experimental
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using OllamaSharp;

namespace CopilotHive.SDK;

/// <summary>
/// Creates <see cref="IChatClient"/> instances for various LLM providers.
/// Shared between Worker and Orchestrator (Brain).
/// </summary>
public static class ChatClientFactory
{
    /// <summary>
    /// Creates an <see cref="IChatClient"/> for the given model string.
    /// The model string may include a provider prefix and reasoning suffix
    /// (e.g. "copilot/claude-sonnet-4.6:high"). The reasoning suffix is stripped
    /// before creating the client — reasoning is applied at the <see cref="ChatOptions"/> level.
    /// </summary>
    public static IChatClient Create(string? modelOverride = null)
    {
        var (provider, model, _) = ParseProviderModelAndReasoning(modelOverride);

        switch (provider)
        {
            case "ollama-cloud":
            {
                var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
                if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("OLLAMA_API_KEY is required for ollama-cloud provider");

                var httpClient = new HttpClient { BaseAddress = new Uri("https://ollama.com"), Timeout = TimeSpan.FromMinutes(10) };
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                model ??= Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "gpt-oss:120b";
                var ollamaClient = new OllamaApiClient(httpClient);
                ollamaClient.SelectedModel = model;
                return ollamaClient;
            }

            case "ollama-local":
            {
                var url = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
                model ??= Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3";
                var ollamaClient = new OllamaApiClient(new Uri(url));
                ollamaClient.SelectedModel = model;
                return ollamaClient;
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

                return openAiClient.GetChatClient(model).AsIChatClient();
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
    private static readonly HashSet<string> KnownReasoningLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "low", "medium", "high", "extra_high"
    };

    /// <summary>
    /// Parses a model string into provider, model name, and optional reasoning effort.
    /// <para>
    /// Format: <c>[provider/]model[:reasoning_level]</c>
    /// </para>
    /// <para>
    /// The reasoning level is extracted from the last colon-separated segment if it matches
    /// a known level (none, low, medium, high, extra_high). This avoids ambiguity with
    /// Ollama model tags like <c>gpt-oss:120b</c>.
    /// </para>
    /// </summary>
    public static (string provider, string? model, ReasoningEffort? reasoning) ParseProviderModelAndReasoning(string? modelOverride)
    {
        var (provider, model) = ParseProviderAndModel(modelOverride);

        if (model is null)
            return (provider, null, null);

        var lastColon = model.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == model.Length - 1)
            return (provider, model, null);

        var suffix = model.Substring(lastColon + 1);
        if (!KnownReasoningLevels.Contains(suffix))
            return (provider, model, null);

        var cleanModel = model.Substring(0, lastColon);
        var effort = suffix.ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "extra_high" => ReasoningEffort.ExtraHigh,
            _ => (ReasoningEffort?)null,
        };

        return (provider, cleanModel, effort);
    }

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

    private static IChatClient CreateCopilotClient(string model)
    {
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(ghToken)) throw new InvalidOperationException("GH_TOKEN or GITHUB_TOKEN is required for copilot provider");

        if (RequiresResponsesEndpoint(model))
        {
            var handler = new CopilotResponsesHandler(new HttpClientHandler());
            var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(ghToken),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://api.githubcopilot.com"),
                    Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
                }
            );
            return openAiClient.GetResponsesClient().AsIChatClient(model);
        }
        else
        {
            var handler = new CopilotChoiceMergingHandler(new HttpClientHandler());
            var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(ghToken),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://api.githubcopilot.com"),
                    Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
                }
            );
            return openAiClient.GetChatClient(model).AsIChatClient();
        }
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
            Environment.GetEnvironmentVariable("DIAGNOSTICS_DIR") ?? "/app/diagnostics";

        public CopilotChoiceMergingHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var seq = Interlocked.Increment(ref _requestCount);

            if (request.Content != null)
            {
                var reqBody = await request.Content.ReadAsStringAsync();
                LogCompletionsExchange(seq, "request", reqBody);
                // Restore consumed content
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
            Environment.GetEnvironmentVariable("DIAGNOSTICS_DIR") ?? "/app/diagnostics";

        public CopilotResponsesHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var seq = Interlocked.Increment(ref _requestCount);

            if (request.Content != null && request.RequestUri?.AbsolutePath?.Contains("responses") == true)
            {
                var body = await request.Content.ReadAsStringAsync();
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
                    if (obj["input"] is JsonArray currentInput)
                    {
                        foreach (var item in currentInput)
                        {
                            var clone = item!.DeepClone();
                            combined.Add(clone);
                            _turnHistory.Add(item.DeepClone());
                        }
                    }

                    obj["input"] = combined;
                    body = obj.ToJsonString();
                    request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                }
                else if (json is JsonObject firstObj)
                {
                    // First request — save the original input as the base context
                    _baseInput = firstObj["input"]?.DeepClone() as JsonArray;
                }

                LogResponsesExchange(seq, "request", body);
            }

            var response = await base.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var respBody = await response.Content.ReadAsStringAsync();
                var respJson = JsonNode.Parse(respBody);

                // Accumulate response output into turn history
                if (respJson?["output"] is JsonArray outputArray)
                    foreach (var item in outputArray)
                        _turnHistory.Add(item!.DeepClone());

                response.Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json");

                LogResponsesExchange(seq, "response", respBody);
            }
            else
            {
                var errBody = await response.Content.ReadAsStringAsync();
                LogResponsesExchange(seq, "error", $"HTTP {(int)response.StatusCode}: {errBody}");
            }

            return response;
        }

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
