using Microsoft.Extensions.AI;

namespace CopilotHive.Shared.AI;

/// <summary>
/// An <see cref="IChatClient"/> decorator that clamps <see cref="ReasoningEffort.ExtraHigh"/>
/// down to a provider-supported maximum before delegating to the inner client.
/// </summary>
/// <remarks>
/// <para>
/// Some providers (e.g. GitHub Models) reject reasoning-effort values above <c>high</c>.
/// Rather than teaching the whole system about per-provider ceilings, the internal
/// <see cref="ReasoningEffort.ExtraHigh"/> value is clamped at the provider boundary here.
/// </para>
/// <para>
/// <b>Clone strategy (Microsoft.Extensions.AI 10.9.0):</b> neither <see cref="ChatOptions"/> nor
/// <see cref="ReasoningOptions"/> is a record in this version — both are plain sealed/derivable
/// classes with mutable properties, so a <c>with { }</c> expression is not available.
/// <see cref="ChatOptions.Clone"/> is public and produces a shallow copy of every member
/// (including a fresh <see cref="ReasoningOptions"/> instance), while
/// <c>ReasoningOptions.Clone()</c> is internal and therefore not callable from here.
/// The strategy is therefore: call <see cref="ChatOptions.Clone"/> (which already gives us a
/// detached <see cref="ReasoningOptions"/> preserving <c>Effort</c> and <c>Output</c>) and then
/// overwrite only <see cref="ReasoningOptions.Effort"/> on the clone. If a future version turns
/// these types into records, this can be simplified to
/// <c>options with { Reasoning = options.Reasoning with { Effort = max } }</c>.
/// </para>
/// <para>
/// Cloning happens only when a clamp is actually applied; for every other value the caller's
/// original <see cref="ChatOptions"/> instance is returned unchanged and is never mutated.
/// </para>
/// </remarks>
internal sealed class ReasoningEffortClampingClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly ReasoningEffort _maxEffort;

    /// <summary>
    /// Creates a clamping wrapper around <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The inner client. This wrapper owns its disposal.</param>
    /// <param name="maxEffort">The highest reasoning effort the provider accepts.</param>
    public ReasoningEffortClampingClient(IChatClient inner, ReasoningEffort maxEffort)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _maxEffort = maxEffort;
    }

    /// <summary>
    /// Returns options whose <see cref="ReasoningOptions.Effort"/> is clamped to the configured
    /// maximum. When no clamp is needed the caller's instance is returned unchanged.
    /// </summary>
    /// <param name="options">The caller-supplied options. Never mutated.</param>
    /// <returns>The original options, or a clamped clone.</returns>
    internal ChatOptions? ClampReasoning(ChatOptions? options)
    {
        if (options?.Reasoning?.Effort is not { } effort) return options;
        if (effort <= _maxEffort) return options;

        var clone = options.Clone();

        // ChatOptions.Clone() shallow-copies Reasoning into a new instance, so mutating the
        // clone's Reasoning cannot affect the caller's options. Guard anyway: a derived
        // ChatOptions could in theory share (or drop) the Reasoning instance.
        if (clone.Reasoning is null || ReferenceEquals(clone.Reasoning, options.Reasoning))
            clone.Reasoning = new ReasoningOptions { Output = options.Reasoning.Output };

        clone.Reasoning.Effort = _maxEffort;
        return clone;
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetResponseAsync(messages, ClampReasoning(options), cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetStreamingResponseAsync(messages, ClampReasoning(options), cancellationToken);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    /// <summary>Gets the inner client's metadata.</summary>
    public ChatClientMetadata? Metadata => _inner.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;

    /// <summary>Disposes the inner client — this wrapper owns it.</summary>
    public void Dispose() => _inner.Dispose();
}
