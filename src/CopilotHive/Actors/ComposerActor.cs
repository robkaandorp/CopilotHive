using CopilotHive.Orchestration;

using Microsoft.Extensions.Logging;

using SharpCoder;

namespace CopilotHive.Actors;

/// <summary>
/// Channel-based actor owning the Composer's streaming and session lifecycle. All mutable
/// state (streaming task, CTS, content, streaming flag) is accessed only from
/// <see cref="HandleAsync"/> (the mailbox thread) or the background streaming task, so no
/// locking is required. Streaming work runs outside the mailbox and posts results back via
/// self-<c>Tell</c> so the mailbox never blocks on the LLM stream.
/// </summary>
internal sealed class ComposerActor : Actor<IComposerMessage>
{
    private readonly ComposerAgentService _agentService;
    private readonly Func<CancellationToken, Task> _saveSession;
    private readonly Action<string> _refreshRegistry;
    private readonly Action<string> _onStreamingUpdate;
    private readonly Action<int> _onStreamingFinished;
    private readonly Action<string> _onStreamingError;
    private readonly Action _onOverflowRecovery;
    private readonly ILogger _logger;

    // Cross-thread state. The mailbox loop and the streaming task both read/write these, so
    // every one is volatile: the streaming task's failed-Tell path latches _terminated and
    // clears _isStreaming on ITS thread, and OnShutdownAsync must observe those writes on the
    // mailbox-loop thread (a stale `_terminated == false` would re-run terminal cleanup).
    //
    // OWNERSHIP: the mailbox loop is the SOLE owner of _streamingTask/_streamingCts — only it
    // may dispose the CTS or null either field. The streaming task never nulls its own
    // reference, so a snapshot taken by OnShutdownAsync stays valid until the task (including
    // its terminal callbacks) has fully completed.
    private volatile CancellationTokenSource? _streamingCts;
    private volatile Task? _streamingTask;
    private string _streamingContent = "";
    private volatile bool _isStreaming;
    private volatile bool _terminated;

    /// <summary>Creates a composer actor bound to the given agent service and callbacks.</summary>
    internal ComposerActor(
        ComposerAgentService agentService,
        Func<CancellationToken, Task> saveSession,
        Action<string> refreshRegistry,
        Action<string> onStreamingUpdate,
        Action<int> onStreamingFinished,
        Action<string> onStreamingError,
        Action onOverflowRecovery,
        ILogger logger)
    {
        _agentService = agentService;
        _saveSession = saveSession;
        _refreshRegistry = refreshRegistry;
        _onStreamingUpdate = onStreamingUpdate;
        _onStreamingFinished = onStreamingFinished;
        _onStreamingError = onStreamingError;
        _onOverflowRecovery = onOverflowRecovery;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task HandleAsync(IComposerMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case ComposerConnectMessage m:
                try
                {
                    await _agentService.ConnectAsync(m.Ct);
                    m.Reply.TrySetResult(true);
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

            case ComposerSendMessageMessage m:
                if (_isStreaming)
                {
                    _logger.LogWarning("Send ignored — already streaming");
                    break;
                }

                // Reset the terminal latch so the actor is reusable: without this a second
                // stream could never complete (its terminal message would be swallowed).
                _terminated = false;
                _isStreaming = true;
                _streamingContent = "";
                _streamingCts = new CancellationTokenSource();
                _refreshRegistry("streaming");
                _streamingTask = Task.Run(() => RunStreamingAsync(m.WrappedMessage, _streamingCts.Token), LoopToken);
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

                try
                {
                    await DoResetAsync(m.Ct);
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

            case ComposerCompactMessage m:
                // The facade's gate check is a TOCTOU probe; the mailbox is the authority,
                // so a compact that races an admitted send is rejected here.
                if (_isStreaming)
                {
                    m.Reply.TrySetException(new InvalidOperationException("Cannot compact while streaming."));
                    break;
                }

                try
                {
                    var result = await DoCompactAsync(m.Ct);
                    m.Reply.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    m.Reply.TrySetResult(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Compact failed");
                    m.Reply.TrySetResult(false);
                }
                break;

            case ComposerCompactPartialMessage m:
                if (_isStreaming)
                {
                    m.Reply.TrySetException(new InvalidOperationException("Cannot compact while streaming."));
                    break;
                }

                try
                {
                    var result = await DoCompactPartialAsync(m.Percent, m.Ct);
                    m.Reply.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    m.Reply.TrySetResult(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Compact failed");
                    m.Reply.TrySetResult(false);
                }
                break;

            case ComposerStreamingUpdateMessage m:
                // The mailbox is the single owner of _streamingContent — the streaming task
                // accumulates into a local and ships whole snapshots, so nothing is lost.
                _streamingContent = m.Content;
                _onStreamingUpdate(m.Content);
                break;

            case ComposerStreamingCompleteMessage m:
                await HandleStreamingCompleteAsync(m);
                // Mailbox loop owns the task/CTS — release them here, never from the
                // streaming task (see HandleStreamingCompleteAsync's remarks).
                ReleaseStreamingResources();
                break;

            case ComposerStreamingErrorMessage m:
                HandleStreamingError(m);
                ReleaseStreamingResources();
                break;
        }
    }

    /// <summary>
    /// The terminal sequence for a completed stream. Shared by the mailbox handler and by the
    /// streaming task's failed-<c>Tell</c> path, so a mailbox that closes during shutdown loses
    /// none of the terminal semantics (save classification, overflow recovery, callbacks).
    /// Idempotent: the <c>_terminated</c> latch guarantees it runs at most once per stream.
    /// <para>
    /// Deliberately does NOT dispose the CTS or null <c>_streamingTask</c>/<c>_streamingCts</c>:
    /// this can run on the streaming task, and nulling its own task reference would let
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

        // Release the facade's admission gate before the callbacks: a throwing callback must
        // never leave the actor (or the facade) stuck in the streaming state.
        _isStreaming = false;

        TryInvoke(() => _refreshRegistry("idle"), nameof(_refreshRegistry));
        TryInvoke(() => _onStreamingFinished(m.LastToolCalls), nameof(_onStreamingFinished));
        if (m.OverflowRecovered)
        {
            // Must run even on the failed-Tell path: the facade deletes the stale session file
            // and clears the compaction flags here, so skipping it would let the overflowing
            // session be reloaded on the next start.
            TryInvoke(_onOverflowRecovery, nameof(_onOverflowRecovery));
        }
    }

    /// <summary>
    /// The terminal sequence for a failed stream. Shared by the mailbox handler and by the
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

        // State before callbacks — see the completion case above.
        _isStreaming = false;

        TryInvoke(() => _onStreamingError(m.Error), nameof(_onStreamingError));
        TryInvoke(() => _refreshRegistry("idle"), nameof(_refreshRegistry));
        TryInvoke(() => _onStreamingFinished(0), nameof(_onStreamingFinished));
    }

    /// <summary>
    /// Releases the streaming task/CTS owned by the mailbox loop. ONLY the mailbox loop may
    /// call this: disposing the CTS or nulling the task from the streaming task itself would
    /// invalidate the reference <see cref="OnShutdownAsync"/> relies on to await that task.
    /// </summary>
    private void ReleaseStreamingResources()
    {
        _streamingCts?.Dispose();
        _streamingCts = null;
        _streamingTask = null;
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
    /// identical on both paths. A failure BEFORE any terminal <c>Tell</c> is attempted (e.g.
    /// overflow recovery's session reset throwing) is reported as a real error.
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
            try
            {
                await _agentService.ResetSessionAsync();
                PostTerminal(new ComposerStreamingCompleteMessage(0, true, false));
            }
            catch (Exception resetEx)
            {
                // Recovery itself failed (agent disposal is allowed to propagate). Report it
                // as a real error — silently finishing would hide a broken session from the UI.
                _logger.LogError(resetEx, "Composer overflow recovery failed");
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
            // Reached when a terminal Tell was REJECTED, or when an unexpected failure escaped
            // before any terminal was attempted. No mailbox handler will run, so the terminal
            // sequence is executed here — with the SAME message, through the SAME handler.
            if (!accepted && !_terminated)
            {
                var terminal = pendingTerminal;
                if (terminal is null)
                {
                    _logger.LogWarning("Composer streaming ended without a terminal message — cleaning up directly");
                    terminal = new ComposerStreamingErrorMessage("Composer streaming ended unexpectedly.");
                }

                // Runs on the STREAMING TASK: invoke the shared terminal handler only. The
                // task/CTS fields belong to the mailbox loop, so they are deliberately left
                // untouched here — nulling _streamingTask would invalidate the reference
                // OnShutdownAsync is awaiting and let completion be signalled underneath
                // these very callbacks.
                switch (terminal)
                {
                    case ComposerStreamingCompleteMessage complete:
                        await HandleStreamingCompleteAsync(complete);
                        break;
                    case ComposerStreamingErrorMessage error:
                        HandleStreamingError(error);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unhandled terminal message type '{terminal.GetType().Name}'.");
                }
            }
        }
    }

    /// <inheritdoc />
    protected override void CancelReply(IComposerMessage message)
    {
        switch (message)
        {
            case ComposerConnectMessage m: m.Reply.TrySetCanceled(); break;
            case ComposerResetSessionMessage m: m.Reply.TrySetCanceled(); break;
            case ComposerSwitchModelMessage m: m.Reply.TrySetCanceled(); break;
            case ComposerCompactMessage m: m.Reply.TrySetCanceled(); break;
            case ComposerCompactPartialMessage m: m.Reply.TrySetCanceled(); break;
        }
    }

    /// <inheritdoc />
    protected override void OnUnhandledException(IComposerMessage message, Exception ex)
    {
        switch (message)
        {
            case ComposerConnectMessage m: m.Reply.TrySetException(ex); break;
            case ComposerResetSessionMessage m: m.Reply.TrySetException(ex); break;
            case ComposerSwitchModelMessage m: m.Reply.TrySetException(ex); break;
            case ComposerCompactMessage m: m.Reply.TrySetException(ex); break;
            case ComposerCompactPartialMessage m: m.Reply.TrySetException(ex); break;
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
        // these fields (see HandleStreamingCompleteAsync), so the captured task reference stays
        // valid until that task — callbacks included — has fully finished.
        var streamingTask = _streamingTask;
        var streamingCts = _streamingCts;

        try { streamingCts?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // The mailbox terminal case disposed it first — cancellation is moot either way.
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
        if (!_terminated)
        {
            _terminated = true;
            _isStreaming = false;
            TryInvoke(() => _onStreamingFinished(0), nameof(_onStreamingFinished));
            TryInvoke(() => _refreshRegistry("idle"), nameof(_refreshRegistry));
        }

        // Only now — after the task and its callbacks are done — may the owner release the
        // captured CTS and clear the fields. Dispose is idempotent, so a mailbox terminal case
        // that already released them is harmless.
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
        var result = await compactor.ForceCompactAsync(_agentService.Session, _agentService.AgentOptions, ct);
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
        var result = await compactor.CompactOldestPercentAsync(_agentService.Session, _agentService.AgentOptions, percent, ct);
        if (result)
        {
            await PersistCompactionAsync(ct);
        }
        return result;
    }

    /// <summary>Persists a successful compaction and refreshes the registry entry.</summary>
    private async Task PersistCompactionAsync(CancellationToken ct)
    {
        await _saveSession(ct);
        TryInvoke(() => _refreshRegistry("idle"), nameof(_refreshRegistry));
    }
}
