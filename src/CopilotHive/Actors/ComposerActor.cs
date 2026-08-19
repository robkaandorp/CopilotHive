using CopilotHive.Orchestration;

using Microsoft.Extensions.Logging;

using SharpCoder;

namespace CopilotHive.Actors;

/// <summary>
/// Channel-based actor owning the Composer's streaming and session lifecycle. All mutable
/// state (streaming task, CTS, content, streaming flag, pending-notification queue) is
/// accessed only from <see cref="HandleAsync"/> (the mailbox thread) or the background
/// streaming task, so no locking is required. Streaming work runs outside the mailbox and
/// posts results back via self-<c>Tell</c> so the mailbox never blocks on the LLM stream.
/// </summary>
internal sealed class ComposerActor : Actor<IComposerMessage>
{
    /// <summary>Maximum number of queued active notifications; oldest are dropped beyond this.</summary>
    private const int MaxPendingNotifications = 10;

    private readonly ComposerAgentService _agentService;
    private readonly Func<CancellationToken, Task> _saveSession;
    private readonly Action<string> _refreshRegistry;
    private readonly Action<string> _onStreamingUpdate;
    private readonly Action _onStreamingStarted;
    private readonly Action<int, bool> _onStreamingTransition;
    private readonly Action<string> _onStreamingError;
    private readonly Action _onOverflowRecovery;
    private readonly Action _onCompactingStarted;
    private readonly Action<bool> _onCompactingFinished;
    private readonly Action<bool> _onSessionLoaded;
    private readonly Action<string> _onSubmitAnswer;
    private readonly Action _onCancelQuestion;
    private readonly ILogger _logger;

    // Cross-thread state. The mailbox loop and the streaming task both read/write these, so
    // every one is volatile: the streaming task's failed-Tell path latches _terminalCleanupDone
    // and clears _isStreaming on ITS thread, and OnShutdownAsync must observe those writes on
    // the mailbox-loop thread (a stale `_terminalCleanupDone == false` would re-run terminal
    // cleanup).
    //
    // OWNERSHIP: the mailbox loop is the SOLE owner of _streamingTask — only it may null that
    // field. The streaming task never nulls its own task reference, so a snapshot taken by
    // OnShutdownAsync stays valid until the task (including its terminal callbacks) has fully
    // completed. The CTS may be disposed by either the mailbox loop or the streaming task's
    // failed-Tell fallback; OnShutdownAsync guards its own cancel with ObjectDisposedException.
    private volatile CancellationTokenSource? _streamingCts;
    private volatile Task? _streamingTask;
    private string _streamingContent = "";
    private volatile bool _isStreaming;
    private volatile bool _terminated;

    /// <summary>
    /// Latch guarding the final terminal cleanup (transition, idle registry, queue clear).
    /// Reset in <see cref="StartStream"/> and set after the final transition of a stream —
    /// either by the mailbox handler's terminal sequence or by the streaming task's
    /// failed-<c>Tell</c> fallback. <see cref="OnShutdownAsync"/> checks it to avoid
    /// double-running the terminal sequence.
    /// </summary>
    private volatile bool _terminalCleanupDone;

    /// <summary>
    /// Queue of active notifications waiting for the current stream to finish. Access is
    /// confined to the mailbox thread. Bounded by <see cref="MaxPendingNotifications"/>;
    /// the oldest entry is dropped when full.
    /// </summary>
    private readonly Queue<string> _pendingNotifications = new();

    /// <summary>Creates a composer actor bound to the given agent service and callbacks.</summary>
    internal ComposerActor(
        ComposerAgentService agentService,
        Func<CancellationToken, Task> saveSession,
        Action<string> refreshRegistry,
        Action<string> onStreamingUpdate,
        Action onStreamingStarted,
        Action<int, bool> onStreamingTransition,
        Action<string> onStreamingError,
        Action onOverflowRecovery,
        Action onCompactingStarted,
        Action<bool> onCompactingFinished,
        Action<bool> onSessionLoaded,
        Action<string> onSubmitAnswer,
        Action onCancelQuestion,
        ILogger logger)
    {
        _agentService = agentService;
        _saveSession = saveSession;
        _refreshRegistry = refreshRegistry;
        _onStreamingUpdate = onStreamingUpdate;
        _onStreamingStarted = onStreamingStarted;
        _onStreamingTransition = onStreamingTransition;
        _onStreamingError = onStreamingError;
        _onOverflowRecovery = onOverflowRecovery;
        _onCompactingStarted = onCompactingStarted;
        _onCompactingFinished = onCompactingFinished;
        _onSessionLoaded = onSessionLoaded;
        _onSubmitAnswer = onSubmitAnswer;
        _onCancelQuestion = onCancelQuestion;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task HandleAsync(IComposerMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case ComposerConnectMessage m:
                // Reset the facade's cache BEFORE the connect attempt: whatever happens next,
                // no stale "loaded from disk" value may survive into a new connection attempt
                // (the agent service resets its own flag first, but the facade cache must be
                // told before the service can flip it again). The caller's ConnectAsync waits
                // on the reply AUTHORITATIVELY (no WaitAsync(ct)), so every path below both
                // publishes the flag and settles the reply on the mailbox thread — a caller
                // can never observe a cancellation while a later `true` is still pending.
                TryInvoke(() => _onSessionLoaded(false), nameof(_onSessionLoaded));
                try
                {
                    await _agentService.ConnectAsync(m.Ct);

                    // Cancellation is checked FIRST: ConnectAsync has token-insensitive stages
                    // after the disk load, so a request cancelled during one of them must NOT
                    // publish the service's `true` — the caller asked to abandon this
                    // connection, and a loaded-from-disk `true` would outlive it.
                    if (m.Ct.IsCancellationRequested)
                    {
                        TryInvoke(() => _onSessionLoaded(false), nameof(_onSessionLoaded));
                        m.Reply.TrySetCanceled(m.Ct);
                    }
                    else
                    {
                        // The service's flag is only true when a session was actually loaded
                        // from disk AND the whole connection succeeded — mirror it into the
                        // facade, before the reply so the caller observes it in actor order.
                        TryInvoke(() => _onSessionLoaded(_agentService.SessionLoadedFromDisk), nameof(_onSessionLoaded));
                        m.Reply.TrySetResult(true);
                    }
                }
                catch (OperationCanceledException) when (m.Ct.IsCancellationRequested)
                {
                    TryInvoke(() => _onSessionLoaded(false), nameof(_onSessionLoaded));
                    m.Reply.TrySetCanceled(m.Ct);
                }
                catch (Exception ex)
                {
                    TryInvoke(() => _onSessionLoaded(false), nameof(_onSessionLoaded));
                    m.Reply.TrySetException(ex);
                }
                break;

            case ComposerSendMessageMessage m:
                if (_isStreaming)
                {
                    m.Reply.TrySetResult(false);
                    break;
                }

                StartStream(m.WrappedMessage);
                // The reply completes AFTER StartStream, so by the time the caller observes
                // `true`, _onStreamingStarted has fired (the facade's _isStreaming is set).
                m.Reply.TrySetResult(true);
                break;

            case ComposerSendActiveNotificationMessage m:
                if (_isStreaming)
                {
                    _pendingNotifications.Enqueue(m.WrappedNotification);
                    while (_pendingNotifications.Count > MaxPendingNotifications)
                        _pendingNotifications.Dequeue();
                }
                else
                {
                    StartStream(m.WrappedNotification);
                }
                break;

            case ComposerCancelStreamingMessage:
                _streamingCts?.Cancel();
                break;

            case ComposerResetSessionMessage m:
                if (_isStreaming)
                {
                    m.Reply.TrySetException(new InvalidOperationException("Cannot reset while streaming."));
                    break;
                }

                // Captured BEFORE any mutation so the outcome paths below can tell whether the
                // session was actually replaced. The facade cache is the SINGLE authority and is
                // updated from the ACTUAL session state — never re-derived from
                // _agentService.SessionLoadedFromDisk, which can be stale-`true` after a
                // late-cancelled connect (the service commits its flag while the actor publishes
                // `false` and cancels the reply).
                //
                // The unified rule for every outcome: session REPLACED → publish `false`;
                // session NOT replaced → publish nothing and preserve the facade's value.
                var sessionBefore = _agentService.Session;

                try
                {
                    await DoResetAsync(m.Ct);
                    // A successful reset always replaces the session with a fresh one, so the
                    // publish is unconditional here. It runs IN ACTOR ORDER, before the reply
                    // completes, so the facade never observes a stale `true` for the new session
                    // and an older reset can never overwrite a newer connect's publish.
                    TryInvoke(() => _onSessionLoaded(false), nameof(_onSessionLoaded));
                    m.Reply.TrySetResult();
                }
                catch (OperationCanceledException) when (m.Ct.IsCancellationRequested)
                {
                    // Pre-mutation cancellation leaves the previous (possibly disk-loaded)
                    // session intact, so the facade's existing value is still accurate and must
                    // be preserved. Only a cancellation observed AFTER the replacement describes
                    // a fresh, not-loaded-from-disk session.
                    if (!ReferenceEquals(sessionBefore, _agentService.Session))
                    {
                        TryInvoke(() => _onSessionLoaded(false), nameof(_onSessionLoaded));
                    }

                    m.Reply.TrySetCanceled(m.Ct);
                }
                catch (Exception ex)
                {
                    // Same rule as the cancellation path: a failure before the replacement (e.g.
                    // thrown from agent disposal) leaves the old session — and therefore the
                    // facade's value — correct; a failure after it (e.g. thrown from agent
                    // recreation) leaves a fresh session that was not loaded from disk.
                    if (!ReferenceEquals(sessionBefore, _agentService.Session))
                    {
                        TryInvoke(() => _onSessionLoaded(false), nameof(_onSessionLoaded));
                    }

                    m.Reply.TrySetException(ex);
                }
                break;

            case ComposerSwitchModelMessage m:
                if (_isStreaming)
                {
                    m.Reply.TrySetException(new InvalidOperationException("Cannot switch model while streaming."));
                    break;
                }

                try
                {
                    await _agentService.SwitchModelAsync(m.Model, m.ReasoningEffort, m.Ct);
                    m.Reply.TrySetResult();
                }
                catch (OperationCanceledException) when (m.Ct.IsCancellationRequested)
                {
                    m.Reply.TrySetCanceled(m.Ct);
                }
                catch (Exception ex)
                {
                    m.Reply.TrySetException(ex);
                }
                break;

            case ComposerSubmitAnswerMessage m:
                // Runs on the mailbox thread: the facade's answer capture/clear and the
                // TrySetResult that resumes the awaiting ask_user tool happen here, so a
                // submit can never race the question lifecycle.
                TryInvoke(() => _onSubmitAnswer(m.Answer), nameof(_onSubmitAnswer));
                break;

            case ComposerCancelQuestionMessage:
                TryInvoke(_onCancelQuestion, nameof(_onCancelQuestion));
                break;

            case ComposerCompactMessage m:
                // The facade's gate check is a TOCTOU probe; the mailbox is the authority,
                // so a compact that races an admitted send is rejected here.
                if (_isStreaming)
                {
                    m.Reply.TrySetException(new InvalidOperationException("Cannot compact while streaming."));
                    break;
                }

                // The actor's own callbacks are the sole source for manual-compaction state:
                // the callback-free options handed to the compactor suppress the agent's wired
                // callbacks so exactly ONE started/finished pair fires per manual compaction.
                // The finished callback runs BEFORE the reply completes (in the finally, which
                // always runs) so the facade's OnCompacted/IsCompacting/WasCompacted updates
                // are observable in actor order by the time the caller sees the outcome.
                var compactResult = false;
                try
                {
                    TryInvoke(_onCompactingStarted, nameof(_onCompactingStarted));
                    compactResult = await DoCompactAsync(m.Ct);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled — keep false.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Compact failed");
                }
                finally
                {
                    // Always runs: `true` on success, `false` on cancellation/failure (and on
                    // a successful run that simply had nothing to compact).
                    TryInvoke(() => _onCompactingFinished(compactResult), nameof(_onCompactingFinished));
                }
                m.Reply.TrySetResult(compactResult);
                break;

            case ComposerCompactPartialMessage m:
                if (_isStreaming)
                {
                    m.Reply.TrySetException(new InvalidOperationException("Cannot compact while streaming."));
                    break;
                }

                var partialResult = false;
                try
                {
                    TryInvoke(_onCompactingStarted, nameof(_onCompactingStarted));
                    partialResult = await DoCompactPartialAsync(m.Percent, m.Ct);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled — keep false.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Compact failed");
                }
                finally
                {
                    // Always runs: `true` on success, `false` on cancellation/failure (and on
                    // a successful run that simply had nothing to compact). Runs BEFORE the
                    // reply so the facade callbacks are observable in actor order.
                    TryInvoke(() => _onCompactingFinished(partialResult), nameof(_onCompactingFinished));
                }
                m.Reply.TrySetResult(partialResult);
                break;

            case ComposerStreamingUpdateMessage m:
                // The mailbox is the single owner of _streamingContent — the streaming task
                // accumulates into a local and ships whole snapshots, so nothing is lost.
                _streamingContent = m.Content;
                _onStreamingUpdate(m.Content);
                break;
            case ComposerStreamingCompleteMessage m:
                await HandleStreamingCompleteAsync(m);
                // The terminal sequence runs here, on the mailbox thread: release the
                // task/CTS, then transition (idle or next queued notification).
                RunTerminalSequence(m.LastToolCalls);
                break;

            case ComposerStreamingErrorMessage m:
                HandleStreamingError(m);
                RunTerminalSequence(0);
                break;
        }
    }

    /// <summary>
    /// The terminal sequence for a completed stream, run on the mailbox thread AFTER the
    /// terminal handler (save classification, overflow recovery, callbacks) has completed.
    /// Releases the task/CTS owned by the mailbox loop, then either transitions to the next
    /// queued active notification or reports idle.
    /// </summary>
    private void RunTerminalSequence(int toolCalls)
    {
        // Mailbox loop owns the task/CTS — release them here, never from the streaming task
        // (see HandleStreamingCompleteAsync's remarks on ownership).
        _streamingCts?.Dispose();
        _streamingCts = null;
        _streamingTask = null;

        // Shutdown discriminator: if the loop token is cancelled, OnShutdownAsync performs
        // the final state cleanup (transition, idle registry, queue clear). We return here
        // so the terminal sequence does not race the shutdown path.
        if (LoopToken.IsCancellationRequested)
        {
            return;
        }

        if (_pendingNotifications.TryDequeue(out var pending))
        {
            // Release the facade's admission gate BEFORE starting the next notification so
            // the actor stays responsive: transition(true) fires OnStreamingUpdate while
            // _isStreaming remains true (keepStreaming), then StartStream re-admits.
            TryInvoke(() => _onStreamingTransition(toolCalls, true), nameof(_onStreamingTransition));
            StartStream(pending);
        }
        else
        {
            _isStreaming = false;
            TryInvoke(() => _onStreamingTransition(toolCalls, false), nameof(_onStreamingTransition));
            TryInvoke(() => _refreshRegistry("idle"), nameof(_refreshRegistry));
            _terminalCleanupDone = true;
        }
    }

    /// <summary>
    /// Starts a streaming response for the given wrapped message. Resets the terminal latches,
    /// publishes the streaming-started callback, registers the streaming status and launches
    /// the background streaming task.
    /// </summary>
    private void StartStream(string wrappedMessage)
    {
        // Reset the terminal latches so the actor is reusable: without this a second
        // stream could never complete (its terminal message would be swallowed).
        _terminalCleanupDone = false;
        _terminated = false;
        _isStreaming = true;
        _streamingContent = "";
        _streamingCts = new CancellationTokenSource();
        // Fires BEFORE the streaming task starts, so the facade observes _isStreaming=true
        // and cleared content before any reply completes.
        TryInvoke(_onStreamingStarted, nameof(_onStreamingStarted));
        _refreshRegistry("streaming");
        _streamingTask = Task.Run(() => RunStreamingAsync(wrappedMessage, _streamingCts.Token), LoopToken);
    }

    /// <summary>
    /// The terminal handler for a completed stream. Shared by the mailbox handler and by the
    /// streaming task's failed-<c>Tell</c> path, so a mailbox that closes during shutdown loses
    /// none of the terminal semantics (save classification, overflow recovery, callbacks).
    /// Idempotent: the <c>_terminated</c> latch guarantees it runs at most once per stream.
    /// <para>
    /// Deliberately does NOT dispose the CTS or null <c>_streamingTask</c>/<c>_streamingCts</c>,
    /// nor perform the idle transition: those belong to the mailbox loop's
    /// <see cref="RunTerminalSequence"/> (or the failed-Tell fallback). This can run on the
    /// streaming task, and nulling its own task reference would let
    /// <see cref="OnShutdownAsync"/> skip the await and signal completion while the callbacks
    /// below are still running. Only the mailbox loop owns those fields.
    /// </para>
    /// </summary>
    private async Task HandleStreamingCompleteAsync(ComposerStreamingCompleteMessage m)
    {
        if (_terminated)
        {
            return;
        }

        // Latch FIRST so OnShutdownAsync (and any second terminal message) skips. Volatile, so
        // the write is visible to the mailbox-loop thread.
        _terminated = true;

        // Cancellation leaves an incomplete response and overflow recovery has already
        // replaced the session with a fresh one — neither may be persisted.
        if (!m.Cancelled && !m.OverflowRecovered)
        {
            try
            {
                await _saveSession(LoopToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session save failed");
            }
        }

        if (m.OverflowRecovered)
        {
            // BEFORE the terminal sequence: overflow recovery replaced the session with a
            // fresh one, and this callback is what clears the facade's compaction flags and
            // _sessionLoadedFromDisk and deletes the stale session file. Running it after
            // the transition would let completion subscribers observe the fresh session
            // through a stale-true loaded-from-disk flag.
            //
            // Must run even on the failed-Tell path: skipping it would let the overflowing
            // session be reloaded on the next start.
            TryInvoke(_onOverflowRecovery, nameof(_onOverflowRecovery));
        }
    }

    /// <summary>
    /// The terminal handler for a failed stream. Shared by the mailbox handler and by the
    /// streaming task's failed-<c>Tell</c> path so the error is reported either way.
    /// Idempotent via the <c>_terminated</c> latch. Like the completion handler it never
    /// disposes or nulls the task/CTS fields — see the note there.
    /// </summary>
    private void HandleStreamingError(ComposerStreamingErrorMessage m)
    {
        if (_terminated)
        {
            return;
        }

        _terminated = true;
        _streamingContent += $"\n\n❌ Error: {m.Error}";

        TryInvoke(() => _onStreamingError(m.Error), nameof(_onStreamingError));
    }

    /// <summary>Invokes a facade callback, logging and swallowing failures so cleanup continues.</summary>
    private void TryInvoke(Action callback, string name)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Composer actor callback '{Callback}' threw", name);
        }
    }

    /// <summary>
    /// Runs the streaming response outside the mailbox and posts results back via self-<c>Tell</c>.
    /// <para>
    /// Exactly ONE terminal sequence runs per stream. When a terminal <c>Tell</c> is accepted the
    /// mailbox handler owns the sequence. When it is REJECTED (the mailbox closed during
    /// shutdown) the streaming task runs the SAME shared handler with the SAME message, so the
    /// terminal semantics — save classification, overflow recovery, error reporting — are
    /// identical on both paths, followed by the cohesive cleanup block below. A failure BEFORE
    /// any terminal <c>Tell</c> is attempted (e.g. overflow recovery's session reset throwing)
    /// is reported as a real error.
    /// </para>
    /// </summary>
    private async Task RunStreamingAsync(string userMessage, CancellationToken ct)
    {
        // The terminal message that was attempted, or null when none was ever issued. Held so
        // a rejected Tell can be replayed through the shared handler with full semantics.
        IComposerMessage? pendingTerminal = null;
        var accepted = false;

        // Local accumulator: the mailbox owns _streamingContent, so concurrent mutation
        // (and therefore lost deltas) is impossible.
        var content = "";

        // Posts a terminal message, remembering it (and whether the mailbox accepted it) so the
        // finally can replay it locally if the mailbox is gone.
        void PostTerminal(IComposerMessage message)
        {
            pendingTerminal = message;
            accepted = Tell(message);
        }

        try
        {
            var cancelled = false;

            await foreach (var update in _agentService.Agent!.ExecuteStreamingAsync(_agentService.Session, userMessage, ct))
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                if (update.Kind == StreamingUpdateKind.TextDelta)
                {
                    content += update.Text;
                    Tell(new ComposerStreamingUpdateMessage(content));
                }
                else if (update.Kind == StreamingUpdateKind.Completed)
                {
                    PostTerminal(new ComposerStreamingCompleteMessage(update.Result?.ToolCallCount ?? 0, false, false));
                    return;
                }
            }

            // The enumeration ended without a Completed update. Both the explicit token check
            // above and a provider-side `yield break` after cancellation are cancellation
            // exits, so they must be classified as cancelled (no session save).
            cancelled = cancelled || ct.IsCancellationRequested;
            PostTerminal(new ComposerStreamingCompleteMessage(0, false, cancelled));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostTerminal(new ComposerStreamingCompleteMessage(0, false, true));
        }
        catch (Exception ex) when (ComposerStreamingService.IsContextOverflowError(ex))
        {
            _logger.LogWarning(ex, "Composer context overflow — resetting session");

            // Captured BEFORE the reset so the failure branch can tell whether the session was
            // actually replaced — see the reset handler for the shared rule.
            var sessionBeforeRecovery = _agentService.Session;
            try
            {
                await _agentService.ResetSessionAsync();
                PostTerminal(new ComposerStreamingCompleteMessage(0, true, false));
            }
            catch (Exception resetEx)
            {
                // Recovery itself failed (agent disposal is allowed to propagate). The error
                // terminal below routes through HandleStreamingError, which does NOT run the
                // overflow-recovery callback, so the facade's loaded-from-disk cache must be
                // published HERE.
                //
                // Same authority rule as the reset handler: publish from the ACTUAL session
                // state, never from _agentService.SessionLoadedFromDisk (which can be
                // stale-`true` after a late-cancelled connect). A pre-replacement failure
                // (thrown from DisposeAgentAsync) leaves the old session intact, so the facade's
                // existing value is still accurate and is deliberately left untouched; only a
                // post-replacement failure (thrown from RecreateAgentAsync) describes a fresh,
                // not-loaded-from-disk session.
                //
                // Published FIRST — before the fallible log call and before the terminal — so a
                // throwing logger can never skip it, and so it lands in actor order ahead of
                // the public error/completion signal.
                if (!ReferenceEquals(sessionBeforeRecovery, _agentService.Session))
                {
                    TryInvoke(() => _onSessionLoaded(false), nameof(_onSessionLoaded));
                }

                // Reported as a real error — silently finishing would hide a broken session
                // from the UI. Swallowed: a logging failure must not displace the error
                // terminal that the UI depends on.
                try
                {
                    _logger.LogError(resetEx, "Composer overflow recovery failed");
                }
                catch
                {
                    // Logger failures are non-actionable here — the terminal below is what
                    // surfaces the failure to the user.
                }

                PostTerminal(new ComposerStreamingErrorMessage(resetEx.Message));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Composer streaming failed");
            PostTerminal(new ComposerStreamingErrorMessage(ex.Message));
        }
        finally
        {
            // Failed-Tell fallback: reached when a terminal Tell was REJECTED (the mailbox
            // closed during shutdown) and the mailbox handler never ran the terminal sequence.
            // Guarded by _terminalCleanupDone so the cleanup runs at most once per stream —
            // the mailbox handler's terminal sequence sets it, and OnShutdownAsync checks it.
            if (!accepted && !_terminalCleanupDone)
            {
                var terminal = pendingTerminal;
                if (terminal is null)
                {
                    _logger.LogWarning("Composer streaming ended without a terminal message — cleaning up directly");
                    terminal = new ComposerStreamingErrorMessage("Composer streaming ended unexpectedly.");
                }

                // Invoke the shared terminal handler for the callbacks (save classification,
                // overflow recovery, error reporting) with the SAME message.
                int toolCalls;
                switch (terminal)
                {
                    case ComposerStreamingCompleteMessage complete:
                        await HandleStreamingCompleteAsync(complete);
                        toolCalls = complete.LastToolCalls;
                        break;
                    case ComposerStreamingErrorMessage error:
                        HandleStreamingError(error);
                        toolCalls = 0;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unhandled terminal message type '{terminal.GetType().Name}'.");
                }

                // Cohesive cleanup block. Note: _streamingTask is deliberately NOT nulled here —
                // nulling it would invalidate the reference OnShutdownAsync is awaiting and let
                // completion be signalled underneath these very callbacks.
                //
                // KNOWN LIMITATION (accepted-but-discarded race): during shutdown,
                // Actor<T>.MessageLoopAsync drains queued messages without calling HandleAsync.
                // If a terminal Tell returns true but the message is drained (not handled), this
                // fallback does NOT run (accepted is true). In that rare case, save/overflow/
                // error callbacks are lost, but OnShutdownAsync performs the final state cleanup
                // (transition, idle, queue clear). The session may not be saved — acceptable
                // during shutdown.
                _streamingCts?.Dispose();
                _streamingCts = null;
                _isStreaming = false;
                _pendingNotifications.Clear();
                TryInvoke(() => _onStreamingTransition(toolCalls, false), nameof(_onStreamingTransition));
                TryInvoke(() => _refreshRegistry("idle"), nameof(_refreshRegistry));
                _terminalCleanupDone = true;
            }
        }
    }

    /// <inheritdoc />
    protected override void CancelReply(IComposerMessage message)
    {
        switch (message)
        {
            case ComposerSendMessageMessage m: m.Reply.TrySetCanceled(); break;
            case ComposerConnectMessage m: m.Reply.TrySetCanceled(); break;
            case ComposerResetSessionMessage m: m.Reply.TrySetCanceled(); break;
            case ComposerSwitchModelMessage m: m.Reply.TrySetCanceled(); break;
            case ComposerCompactMessage m: m.Reply.TrySetCanceled(); break;
            case ComposerCompactPartialMessage m: m.Reply.TrySetCanceled(); break;
            case ComposerSubmitAnswerMessage: break; // fire-and-forget — no reply to cancel
            case ComposerCancelQuestionMessage: break; // fire-and-forget — no reply to cancel
            case ComposerSendActiveNotificationMessage: break; // fire-and-forget — no reply to cancel
        }
    }

    /// <inheritdoc />
    protected override void OnUnhandledException(IComposerMessage message, Exception ex)
    {
        switch (message)
        {
            case ComposerSendMessageMessage m: m.Reply.TrySetException(ex); break;
            case ComposerConnectMessage m: m.Reply.TrySetException(ex); break;
            case ComposerResetSessionMessage m: m.Reply.TrySetException(ex); break;
            case ComposerSwitchModelMessage m: m.Reply.TrySetException(ex); break;
            case ComposerCompactMessage m: m.Reply.TrySetException(ex); break;
            case ComposerCompactPartialMessage m: m.Reply.TrySetException(ex); break;
            case ComposerSubmitAnswerMessage:
            case ComposerCancelQuestionMessage:
            case ComposerSendActiveNotificationMessage:
                _logger.LogError(ex, "Composer actor failed to handle {MessageType}", message.GetType().Name);
                break;
            default: CancelReply(message); break;
        }
    }

    /// <inheritdoc />
    protected override async Task OnShutdownAsync()
    {
        // Capture STABLE snapshots BEFORE cancelling. The streaming task's failed-Tell path
        // runs the terminal handler on the task itself, so the mailbox loop must never re-read
        // the mutable fields here: awaiting a snapshot (rather than a field that could be
        // observed as null) is what guarantees Completion is not signalled while the fallback's
        // terminal callbacks are still running. The streaming task deliberately does NOT null
        // _streamingTask (see HandleStreamingCompleteAsync), so the captured task reference
        // stays valid until that task — callbacks included — has fully finished.
        var streamingTask = _streamingTask;
        var streamingCts = _streamingCts;

        try { streamingCts?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // The mailbox terminal case or the failed-Tell fallback disposed it first —
            // cancellation is moot either way.
        }

        if (streamingTask is not null)
        {
            try
            {
                await streamingTask;
            }
            catch
            {
                // Streaming failures are surfaced via the terminal self-Tell or the fallback.
            }
        }

        // The await above guarantees the streaming task (and therefore any fallback terminal
        // sequence it ran) has fully completed, so this volatile latch read observes its write
        // and cannot re-run a terminal sequence that already happened.
        _pendingNotifications.Clear();

        if (!_terminalCleanupDone)
        {
            _isStreaming = false;
            TryInvoke(() => _onStreamingTransition(0, false), nameof(_onStreamingTransition));
            TryInvoke(() => _refreshRegistry("idle"), nameof(_refreshRegistry));
            _terminalCleanupDone = true;
        }

        // Only now — after the task and its callbacks are done — may the owner release the
        // captured CTS and clear the fields. Dispose is idempotent, so a mailbox terminal case
        // or fallback that already released them is harmless.
        streamingCts?.Dispose();
        _streamingCts = null;
        _streamingTask = null;
    }

    /// <inheritdoc />
    protected override void OnDisposeTimeout() =>
        _logger.LogWarning("Composer actor disposal timed out — streaming may still be running");

    /// <summary>
    /// Resets the composer session via the agent service.
    /// <para>
    /// <see cref="ComposerAgentService.ResetSessionAsync"/> takes no token, so the caller's
    /// cancellation is consumed HERE: once before any mutation (a pre-cancelled request must
    /// never destroy the session) and once after, so a cancellation that arrives while the
    /// reset is running is still reported as cancelled rather than a silent success.
    /// </para>
    /// </summary>
    private async Task DoResetAsync(CancellationToken ct)
    {
        // Before any mutation — a cancelled request must leave the session untouched.
        ct.ThrowIfCancellationRequested();

        await _agentService.ResetSessionAsync();

        // Observed again after the reset so cancellation during the operation is not lost.
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Force-compacts the composer session using a fresh <see cref="ContextCompactor"/>.
    /// On success the session is persisted and the registry refreshed, mirroring the original
    /// facade behaviour (a compaction that is not saved would be lost on restart).
    /// </summary>
    private async Task<bool> DoCompactAsync(CancellationToken ct)
    {
        var compactor = new ContextCompactor(_agentService.AgentOptions.CompactionClient ?? _agentService.ChatClient!, _logger);
        var result = await compactor.ForceCompactAsync(_agentService.Session, CloneOptionsWithoutCompactionCallbacks(_agentService.AgentOptions), ct);
        if (result)
        {
            await PersistCompactionAsync(ct);
        }
        return result;
    }

    /// <summary>
    /// Partially compacts the composer session using a fresh <see cref="ContextCompactor"/>.
    /// On success the session is persisted and the registry refreshed.
    /// </summary>
    private async Task<bool> DoCompactPartialAsync(int percent, CancellationToken ct)
    {
        var compactor = new ContextCompactor(_agentService.AgentOptions.CompactionClient ?? _agentService.ChatClient!, _logger);
        var result = await compactor.CompactOldestPercentAsync(_agentService.Session, CloneOptionsWithoutCompactionCallbacks(_agentService.AgentOptions), percent, ct);
        if (result)
        {
            await PersistCompactionAsync(ct);
        }
        return result;
    }

    /// <summary>
    /// Copies every property of <paramref name="original"/> into a fresh <see cref="AgentOptions"/>
    /// with <c>OnCompacting</c> and <c>OnCompacted</c> cleared. <see cref="AgentOptions"/> is a
    /// sealed class (not a record), so this manual clone is the only way to get a callback-free
    /// copy. Manual compaction (full and partial) passes this clone to
    /// <see cref="ContextCompactor"/> so the agent service's wired callbacks — which are also the
    /// streaming/automatic-compaction callbacks — are never double-fired: the actor's own
    /// <c>_onCompactingStarted</c>/<c>_onCompactingFinished</c> are the sole source for manual
    /// compaction state. Automatic compaction (during streaming) is unchanged and still uses the
    /// service's original options.
    /// </summary>
    private static AgentOptions CloneOptionsWithoutCompactionCallbacks(AgentOptions original) => new()
    {
        WorkDirectory = original.WorkDirectory,
        MaxSteps = original.MaxSteps,
        EnableBash = original.EnableBash,
        BashShellPath = original.BashShellPath,
        BashShellArgsFormat = original.BashShellArgsFormat,
        EnableFileOps = original.EnableFileOps,
        EnableFileWrites = original.EnableFileWrites,
        EnableSkills = original.EnableSkills,
        SystemPrompt = original.SystemPrompt,
        CustomInstructions = original.CustomInstructions,
        AutoLoadWorkspaceInstructions = original.AutoLoadWorkspaceInstructions,
        CompactionThreshold = original.CompactionThreshold,
        CompactionRetainRecent = original.CompactionRetainRecent,
        EnableAutoCompaction = original.EnableAutoCompaction,
        OnCompacting = null,
        OnCompacted = null,
        Logger = original.Logger,
        ReasoningEffort = original.ReasoningEffort,
        ShowToolCallsInStream = original.ShowToolCallsInStream,
        CompactionMaxTokens = original.CompactionMaxTokens,
        SubAgents = original.SubAgents,
        CustomTools = original.CustomTools,
        CompactionClient = original.CompactionClient,
        MaxContextTokens = original.MaxContextTokens,
    };

    /// <summary>Persists a successful compaction and refreshes the registry entry.</summary>
    private async Task PersistCompactionAsync(CancellationToken ct)
    {
        await _saveSession(ct);
        TryInvoke(() => _refreshRegistry("idle"), nameof(_refreshRegistry));
    }
}
