using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotHive.Shared.AI;

/// <summary>
/// A <see cref="DelegatingHandler"/> that maps the internal wire value <c>"extra_high"</c>
/// to a provider-specific reasoning-effort value at the HTTP boundary.
/// </summary>
/// <remarks>
/// <para>
/// The internal wire format (YAML/gRPC, see <c>ReasoningEffortConverter</c>) always uses
/// <c>"extra_high"</c>. Provider APIs use their own spellings (e.g. the GitHub Copilot API
/// expects <c>"xhigh"</c>). This handler performs that translation as late as possible —
/// on the serialized JSON request body — so nothing else in the system has to know about
/// provider-specific spellings.
/// </para>
/// <para>
/// The replacement is deliberately narrow: the body is parsed as JSON and only the
/// reasoning-effort properties are considered. A property is rewritten only when its value
/// is exactly the string <c>"extra_high"</c>. Occurrences of <c>"extra_high"</c> anywhere
/// else in the payload (prompts, tool-call arguments, arrays, …) are left untouched.
/// </para>
/// <para>
/// Targeted properties:
/// <list type="bullet">
///   <item><description><c>reasoning_effort</c> — top-level string (chat/completions API).</description></item>
///   <item><description><c>reasoning.effort</c> — string nested in a top-level <c>reasoning</c> object (responses API).</description></item>
///   <item><description>An optional <c>customPropertyName</c> — top-level string (e.g. <c>think</c> for Ollama).
///   The custom property <em>supplements</em> the two defaults; it never replaces them.</description></item>
/// </list>
/// </para>
/// <para>
/// Non-JSON content passes through untouched, and malformed JSON passes through untouched as
/// well (the handler never throws on a body it cannot parse — transport-level translation must
/// not break requests it does not understand).
/// </para>
/// </remarks>
internal sealed class ReasoningEffortMappingHandler : DelegatingHandler
{
    /// <summary>The internal wire value that gets mapped. Only an exact match is replaced.</summary>
    internal const string SourceValue = "extra_high";

    private const string JsonMediaType = "application/json";
    private const string ReasoningEffortProperty = "reasoning_effort";
    private const string ReasoningProperty = "reasoning";
    private const string EffortProperty = "effort";

    private readonly string _mappedValue;
    private readonly string? _customPropertyName;

    /// <summary>
    /// Creates a handler that maps <c>"extra_high"</c> to <paramref name="mappedValue"/>.
    /// </summary>
    /// <param name="mappedValue">The provider-specific replacement value (e.g. <c>"xhigh"</c>).</param>
    /// <param name="customPropertyName">
    /// Optional additional top-level property to inspect (e.g. <c>"think"</c> for the Ollama API).
    /// This supplements — it does not replace — the default <c>reasoning_effort</c> and
    /// <c>reasoning.effort</c> targets.
    /// </param>
    public ReasoningEffortMappingHandler(string mappedValue, string? customPropertyName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(mappedValue);
        _mappedValue = mappedValue;
        _customPropertyName = customPropertyName;
    }

    /// <summary>
    /// Creates a handler that maps <c>"extra_high"</c> to <paramref name="mappedValue"/> and
    /// forwards to <paramref name="innerHandler"/>.
    /// </summary>
    /// <param name="mappedValue">The provider-specific replacement value (e.g. <c>"xhigh"</c>).</param>
    /// <param name="innerHandler">The next handler in the chain.</param>
    /// <param name="customPropertyName">Optional additional top-level property to inspect.</param>
    public ReasoningEffortMappingHandler(string mappedValue, HttpMessageHandler innerHandler, string? customPropertyName = null)
        : base(innerHandler)
    {
        ArgumentException.ThrowIfNullOrEmpty(mappedValue);
        _mappedValue = mappedValue;
        _customPropertyName = customPropertyName;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var content = request.Content;

        // Only JSON bodies are candidates. Anything else (multipart, form, SSE, no body)
        // is forwarded untouched and, importantly, is never disposed by this handler.
        if (content is not null && IsJsonContent(content))
        {
            var body = await content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (TryMapBody(body, out var mapped))
            {
                // A replacement happened: the original content is no longer referenced by the
                // request, so this handler owns its disposal. The new StringContent sets its own
                // Content-Type and Content-Length; no headers are copied from the original, since
                // any length/encoding header from the original would now be wrong.
                content.Dispose();
                request.Content = new StringContent(mapped, Encoding.UTF8, JsonMediaType);
            }
        }

        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static bool IsJsonContent(HttpContent content) =>
        string.Equals(content.Headers.ContentType?.MediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to rewrite the reasoning-effort properties of a JSON body.
    /// </summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="mapped">The rewritten body when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when at least one property was replaced.</returns>
    internal bool TryMapBody(string body, out string mapped)
    {
        mapped = body;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            // Malformed JSON: pass through unchanged rather than failing the request.
            return false;
        }

        if (root is not JsonObject obj) return false;

        var modified = false;

        // Non-short-circuiting |= so every target is evaluated.
        modified |= TryReplaceStringProperty(obj, ReasoningEffortProperty);

        if (obj[ReasoningProperty] is JsonObject reasoning)
            modified |= TryReplaceStringProperty(reasoning, EffortProperty);

        if (!string.IsNullOrEmpty(_customPropertyName))
            modified |= TryReplaceStringProperty(obj, _customPropertyName);

        if (!modified) return false;

        mapped = obj.ToJsonString();
        return true;
    }

    /// <summary>
    /// Replaces <paramref name="propertyName"/> on <paramref name="obj"/> when its value is
    /// exactly the string <see cref="SourceValue"/>.
    /// </summary>
    private bool TryReplaceStringProperty(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is not JsonValue value) return false;
        if (!value.TryGetValue<string>(out var current)) return false;
        if (!string.Equals(current, SourceValue, StringComparison.Ordinal)) return false;

        obj[propertyName] = _mappedValue;
        return true;
    }
}
