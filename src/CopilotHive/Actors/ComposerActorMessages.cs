using Microsoft.Extensions.AI;

namespace CopilotHive.Actors;

/// <summary>Marker interface for all composer actor messages.</summary>
internal interface IComposerMessage { }

/// <summary>
/// Sends a user message and starts streaming a response in the background. Fire-and-forget:
/// no reply is carried, so send failures surface through the streaming callbacks.
/// </summary>
internal sealed record ComposerSendMessageMessage(string WrappedMessage) : IComposerMessage;

/// <summary>Cancels the in-flight streaming response. Fire-and-forget.</summary>
internal sealed record ComposerCancelStreamingMessage : IComposerMessage;

/// <summary>Connects the composer agent service, replying with <c>true</c> on success.</summary>
internal sealed record ComposerConnectMessage(TaskCompletionSource<bool> Reply, CancellationToken Ct) : IComposerMessage;

/// <summary>Resets the composer session, replying when complete.</summary>
internal sealed record ComposerResetSessionMessage(TaskCompletionSource Reply, CancellationToken Ct) : IComposerMessage;

/// <summary>Switches the composer model and reasoning effort, replying when complete.</summary>
internal sealed record ComposerSwitchModelMessage(
    string Model,
    ReasoningEffort ReasoningEffort,
    TaskCompletionSource Reply,
    CancellationToken Ct) : IComposerMessage;

/// <summary>Force-compacts the composer session, replying with whether compaction occurred.</summary>
internal sealed record ComposerCompactMessage(TaskCompletionSource<bool> Reply, CancellationToken Ct) : IComposerMessage;

/// <summary>Partially compacts the composer session, replying with whether compaction occurred.</summary>
internal sealed record ComposerCompactPartialMessage(
    int Percent,
    TaskCompletionSource<bool> Reply,
    CancellationToken Ct) : IComposerMessage;

/// <summary>Internal self-Tell carrying the accumulated streaming content.</summary>
internal sealed record ComposerStreamingUpdateMessage(string Content) : IComposerMessage;

/// <summary>Internal self-Tell signalling that streaming completed (normally, cancelled, or after overflow recovery).</summary>
internal sealed record ComposerStreamingCompleteMessage(int LastToolCalls, bool OverflowRecovered, bool Cancelled) : IComposerMessage;

/// <summary>Internal self-Tell signalling that streaming failed with an error.</summary>
internal sealed record ComposerStreamingErrorMessage(string Error) : IComposerMessage;
