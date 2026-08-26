namespace CopilotHive.Orchestration;

/// <summary>
/// The observable connection state of the Composer as owned by
/// <see cref="LlmConnectionCoordinator"/>. The coordinator publishes every transition through
/// its internal state-change observer, so the set below is the complete, exact vocabulary of
/// states a Composer connection can be in.
/// </summary>
public enum ComposerState
{
    /// <summary>
    /// No configured model (a model-less Composer shell). Never transitions on its own —
    /// only shutdown moves it to <see cref="Stopped"/>.
    /// </summary>
    Absent,

    /// <summary>A connect attempt is in flight (transient).</summary>
    Connecting,

    /// <summary>
    /// An attempt is in flight AND shutdown was requested: the <c>ConnectAsync</c> task is
    /// winding down and its result will be discarded. Ends only in <see cref="Stopped"/>.
    /// </summary>
    Cancelling,

    /// <summary>
    /// A connect is queued/pending — either the initial deferral (no GitHub Copilot token yet)
    /// or the deferred retry-queued state after a fault.
    /// </summary>
    PendingConnect,

    /// <summary>The Composer connected successfully.</summary>
    Connected,

    /// <summary>
    /// The most recent connect attempt threw. Connect-attempt failures ONLY — shutdown never
    /// produces this state.
    /// </summary>
    Faulted,

    /// <summary>
    /// Terminal. No retries and no further observer notifications happen after this state.
    /// </summary>
    Stopped,
}
