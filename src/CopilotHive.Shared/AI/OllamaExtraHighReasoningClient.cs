using Microsoft.Extensions.AI;

namespace CopilotHive.Shared.AI;

/// <summary>
/// An <see cref="IChatClient"/> decorator that preserves the <see cref="ReasoningEffort.ExtraHigh"/>
/// distinction across OllamaSharp's <see cref="ChatOptions"/>→request mapping, so the Ollama API
/// receives <c>"think":"max"</c> instead of the collapsed <c>"think":"high"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists (and why an HTTP <see cref="DelegatingHandler"/> cannot do the job).</b>
/// </para>
/// <para>
/// <b>Investigation method:</b> (1) decompiled <c>OllamaSharp.dll</c> 5.4.30 with <c>ilspycmd</c>
/// and read <c>OllamaSharp.MicrosoftAi.AbstractionMapper.ToOllamaSharpChatRequest</c>; (2) confirmed
/// the finding with a request-capture test — an <see cref="OllamaSharp.OllamaApiClient"/> built over an
/// <see cref="HttpClient"/> whose terminal handler recorded the outgoing body, driven through
/// <c>IChatClient.GetResponseAsync</c>/<c>GetStreamingResponseAsync</c> for every
/// <see cref="ReasoningEffort"/> value.
/// </para>
/// <para>
/// <b>Finding:</b> the mapper contains
/// <c>request.Think = options.Reasoning.Effort switch { null =&gt; null, None =&gt; false, Low =&gt; "low",
/// Medium =&gt; "medium", _ =&gt; "high" }</c>. The <c>_</c> arm swallows both <c>High</c> and
/// <c>ExtraHigh</c>, so <b>both serialize to the identical body</b>
/// <c>{"...","think":"high"}</c>. The collapse happens <em>inside</em> OllamaSharp before any HTTP
/// body exists, so a <see cref="DelegatingHandler"/> can never distinguish the two: by the time the
/// body reaches the transport, <c>ExtraHigh</c> and <c>High</c> are byte-identical, and rewriting
/// <c>"high"</c> would silently upgrade genuine <see cref="ReasoningEffort.High"/> requests.
/// The fix therefore has to act at the <see cref="ChatOptions"/> level, before serialization.
/// </para>
/// <para>
/// <b>Chosen mechanism:</b> the mapper begins with
/// <c>request = options.RawRepresentationFactory?.Invoke(chatClient) is ChatRequest raw ? raw : new ChatRequest()</c>
/// and thereafter only fills members that are still <see langword="null"/> — including
/// <c>Think</c>, which the <c>Reasoning.Effort</c> switch sets only when
/// <c>!request.Think.HasValue</c>. So supplying a <c>ChatRequest</c> whose <c>Think</c> is already
/// <c>"max"</c> makes the collapse switch a no-op and puts <c>"think":"max"</c> on the wire.
/// This wrapper therefore <em>composes</em> the caller's
/// <see cref="ChatOptions.RawRepresentationFactory"/>: the replacement invokes the original factory
/// exactly once, and fills <c>Think</c> on that very result only when the caller left it unset.
/// </para>
/// <para>
/// <b>Why composition rather than inspection:</b> the framework contract asks a factory to return a
/// fresh instance per invocation, and factories may be stateful or non-deterministic. Calling the
/// factory once to inspect it and letting OllamaSharp call it again to build the request would
/// evaluate caller code twice and could observe a different <c>Think</c> in each call — overwriting
/// a real explicit value or wrongly suppressing <c>max</c>. Composition evaluates the caller's
/// factory exactly once, on the same object that is actually serialized, so the decision and the
/// outcome can never disagree. The composed factory also forwards the <see cref="IChatClient"/>
/// argument it receives untouched: OllamaSharp passes the underlying <c>OllamaApiClient</c>
/// (verified), so factories that inspect the supplied client still see what they expect rather than
/// this decorator.
/// </para>
/// <para>
/// <b>Alternative considered and rejected:</b> <c>ChatOptions.AdditionalProperties["think"]</c>,
/// which the mapper applies via <c>TryAddOllamaOption(OllamaOption.Think, …)</c>. That route runs
/// <em>after</em> the raw request is adopted, so it unconditionally overwrites an explicit
/// <c>ChatRequest.Think</c> supplied through <c>RawRepresentationFactory</c> (verified by request
/// capture: raw <c>Think="low"</c> plus the additional property emitted <c>"think":"max"</c>).
/// It is therefore unusable for a wrapper that must respect caller intent. An explicit
/// <c>AdditionalProperties["think"]</c> set by the caller still wins, because it is applied last —
/// this wrapper short-circuits on it so the caller's options are returned untouched.
/// </para>
/// <para>
/// <b>Scope of mutation:</b> only <see cref="ReasoningEffort.ExtraHigh"/> is intercepted, and only
/// when the caller has not already expressed an explicit <c>think</c> through either mechanism —
/// an explicit caller value always wins. The caller's <see cref="ChatOptions"/> is never mutated:
/// a clone carries the composed factory, matching the contract of
/// <see cref="ReasoningEffortClampingClient"/>.
/// </para>
/// </remarks>
internal sealed class OllamaExtraHighReasoningClient : IChatClient
{
    /// <summary>
    /// The <c>AdditionalProperties</c> key OllamaSharp reads for the thinking level
    /// (<c>OllamaSharp.Models.OllamaOption.Think.Name</c>).
    /// </summary>
    internal const string ThinkPropertyName = ChatClientFactory.OllamaReasoningPropertyName;

    /// <summary>The Ollama thinking level that <see cref="ReasoningEffort.ExtraHigh"/> maps to.</summary>
    internal const string ExtraHighThinkValue = ChatClientFactory.OllamaExtraHighMapping;

    private readonly IChatClient _inner;
    private readonly IDisposable? _ownedTransport;

    /// <summary>
    /// Creates a wrapper around <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The inner client. This wrapper owns its disposal.</param>
    /// <param name="ownedTransport">
    /// An optional additional resource this wrapper takes ownership of — in practice the
    /// <see cref="HttpClient"/> injected into <see cref="OllamaSharp.OllamaApiClient"/>.
    /// <b>Ownership rationale:</b> OllamaSharp 5.4.30 disposes its HTTP client only when it created
    /// it itself (<c>_disposeHttpClient</c> is set solely by the <c>Configuration</c>/<c>Uri</c>
    /// constructors); the <c>OllamaApiClient(HttpClient, …)</c> constructor leaves ownership with the
    /// caller. Since <see cref="ChatClientFactory"/> constructs that <see cref="HttpClient"/> — and
    /// with it the whole resilience/mapping handler chain and its sockets — something must dispose
    /// it, and this wrapper is the only object the factory hands back. Pass <see langword="null"/>
    /// when the inner client owns its own transport.
    /// </param>
    public OllamaExtraHighReasoningClient(IChatClient inner, IDisposable? ownedTransport = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _ownedTransport = ownedTransport;
    }

    /// <summary>
    /// Returns options whose <see cref="ChatOptions.RawRepresentationFactory"/> supplies
    /// <c>Think = "max"</c> when the requested effort is <see cref="ReasoningEffort.ExtraHigh"/> and
    /// the caller has not expressed an explicit <c>think</c>. In every other case the caller's
    /// instance is returned unchanged, and it is never mutated.
    /// </summary>
    internal ChatOptions? ApplyExtraHighThink(ChatOptions? options)
    {
        if (options?.Reasoning?.Effort is not ReasoningEffort.ExtraHigh) return options;

        // An explicit additional property is applied last by the mapper and therefore always wins.
        // Short-circuit so the caller's options are returned untouched.
        if (options.AdditionalProperties?.ContainsKey(ThinkPropertyName) == true) return options;

        var originalFactory = options.RawRepresentationFactory;

        var clone = options.Clone();
        clone.RawRepresentationFactory = chatClient => ComposeRawRepresentation(originalFactory, chatClient);
        return clone;
    }

    /// <summary>
    /// Invokes the caller's factory (when present) exactly once and fills <c>Think</c> on its result
    /// only when the caller left it unset.
    /// </summary>
    /// <remarks>
    /// When the caller had no factory, their factory yields <see langword="null"/>, or it yields an
    /// object the mapper cannot use, a fresh <c>ChatRequest</c> carrying only <c>Think</c> is
    /// supplied; the mapper fills every other member as usual. Substituting in the unusable-value
    /// case loses nothing: the mapper's adoption step is
    /// <c>… is ChatRequest raw ? raw : new ChatRequest()</c>, so a non-<c>ChatRequest</c> result is
    /// discarded outright (verified by request capture) — passing it through would forfeit
    /// <c>max</c> for no benefit.
    /// </remarks>
    private static object? ComposeRawRepresentation(
        Func<IChatClient, object?>? originalFactory, IChatClient chatClient)
    {
        // Forward the client OllamaSharp supplied, not this decorator, so a caller's factory sees
        // exactly the instance it would have seen without this wrapper.
        var raw = originalFactory?.Invoke(chatClient);

        switch (raw)
        {
            case OllamaSharp.Models.Chat.ChatRequest { Think: not null }:
                // Explicit caller value — never override intent.
                return raw;

            case OllamaSharp.Models.Chat.ChatRequest request:
                request.Think = new OllamaSharp.Models.Chat.ThinkValue(ExtraHighThinkValue);
                return request;

            default:
                return new OllamaSharp.Models.Chat.ChatRequest
                {
                    Think = new OllamaSharp.Models.Chat.ThinkValue(ExtraHighThinkValue),
                };
        }
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetResponseAsync(messages, ApplyExtraHighThink(options), cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetStreamingResponseAsync(messages, ApplyExtraHighThink(options), cancellationToken);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    /// <summary>Gets the inner client's metadata.</summary>
    public ChatClientMetadata? Metadata => _inner.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;

    /// <summary>
    /// Disposes the inner client and, when this wrapper was given ownership of one, the injected
    /// transport.
    /// </summary>
    /// <remarks>
    /// Both cleanups are always attempted. A plain <c>try/finally</c> would not do: if both throw,
    /// the transport's exception would replace — and thereby hide — the inner client's. The failures
    /// are captured instead, so a single failure propagates as-is and a double failure surfaces as an
    /// <see cref="AggregateException"/> that still carries the original/primary error first.
    /// </remarks>
    public void Dispose()
    {
        Exception? innerFailure = null;
        Exception? transportFailure = null;

        try
        {
            _inner.Dispose();
        }
        catch (Exception ex)
        {
            innerFailure = ex;
        }

        try
        {
            _ownedTransport?.Dispose();
        }
        catch (Exception ex)
        {
            transportFailure = ex;
        }

        if (innerFailure is not null && transportFailure is not null)
            throw new AggregateException(
                "Both the inner Ollama client and its owned transport failed to dispose.",
                innerFailure, transportFailure);

        if (innerFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(innerFailure).Throw();

        if (transportFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(transportFailure).Throw();
    }
}
