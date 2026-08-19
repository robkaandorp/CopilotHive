using Microsoft.Extensions.AI;

namespace CopilotHive.Actors;

/// <summary>Marker interface for all composer actor messages.</summary>
internal interface IComposerMessage { }

/// <summary>
/// Sends a user message and starts streaming a response in the background. The reply
/// completes with <c>true</c> once the stream has been admitted (StartStream called and
/// <c>_onStreamingStarted</c> fired), or <c>false</c> when the send was rejected because
/// the actor is already streaming.
/// </summary>
internal sealed record ComposerSendMessageMessage(string WrappedMessage, TaskCompletionSource<bool> Reply) : IComposerMessage;

/// <summary>
/// Sends an active system notification to the Composer. If idle, the notification starts
/// streaming immediately; if streaming, it is queued (bounded, oldest dropped) and starts
/// after the current stream's terminal transition. Fire-and-forget.
/// </summary>
internal sealed record ComposerSendActiveNotificationMessage(string WrappedNotification) : IComposerMessage;

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

/// <summary>
/// Submits the user's answer to the currently pending question, resuming the streaming loop.
/// Fire-and-forget: the answer is delivered through the <c>onSubmitAnswer</c> callback.
/// </summary>
internal sealed record ComposerSubmitAnswerMessage(string Answer) : IComposerMessage;

/// <summary>
/// Cancels the currently pending question, returning a cancellation message to the LLM.
/// Fire-and-forget: the cancellation is delivered through the <c>onCancelQuestion</c> callback.
/// </summary>
internal sealed record ComposerCancelQuestionMessage : IComposerMessage;

/// <summary>Internal self-Tell carrying the accumulated streaming content.</summary>
internal sealed record ComposerStreamingUpdateMessage(string Content) : IComposerMessage;

/// <summary>Internal self-Tell signalling that streaming completed (normally, cancelled, or after overflow recovery).</summary>
internal sealed record ComposerStreamingCompleteMessage(int LastToolCalls, bool OverflowRecovered, bool Cancelled) : IComposerMessage;

/// <summary>Internal self-Tell signalling that streaming failed with an error.</summary>
internal sealed record ComposerStreamingErrorMessage(string Error) : IComposerMessage;
