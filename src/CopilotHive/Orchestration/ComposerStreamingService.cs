using Microsoft.Extensions.AI;

using SharpCoder;

namespace CopilotHive.Orchestration;

/// <summary>
/// Manages the Composer's streaming response loop and streaming state.
/// </summary>
internal sealed class ComposerStreamingService(
    ComposerAgentService agentService,
    ILogger logger,
    Func<CancellationToken, Task>? saveSession = null,
    Action<string, long?>? refreshRegistry = null,
    Action? onStreamingUpdate = null,
    Action? onOverflowRecovery = null) : IAsyncDisposable
{
    private string _streamingContent = "";
    private bool _isStreaming;
    private int _lastToolCalls;
    private CancellationTokenSource? _streamCts;
    private Task? _streamingTask;

    /// <summary>Whether the Composer is currently streaming a response.</summary>
    public bool IsStreaming => _isStreaming;

    /// <summary>The accumulated streaming text (partial response in progress).</summary>
    public string StreamingContent => _streamingContent;

    /// <summary>Tool call count from the last completed response.</summary>
    public int LastToolCalls => _lastToolCalls;

    /// <summary>
    /// Sends a message and starts streaming the response in the background.
    /// </summary>
    /// <param name="userMessage">The user message to send.</param>
    /// <exception cref="InvalidOperationException">Thrown when the agent is not connected.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a stream is already in progress.</exception>
    public void SendMessage(string userMessage)
    {
        if (agentService.Agent is null)
            throw new InvalidOperationException("Composer not connected. Call ConnectAsync first.");
        if (_isStreaming)
            throw new InvalidOperationException("Composer is already streaming a response.");

        _streamingContent = "";
        _isStreaming = true;
        _lastToolCalls = 0;
        _streamCts = new CancellationTokenSource();

        _streamingTask = RunStreamingAsync(userMessage, _streamCts.Token);
    }

    /// <summary>
    /// Cancels the current streaming response if one is in progress.
    /// </summary>
    public void CancelStreaming()
    {
        _streamCts?.Cancel();
    }

    private async Task RunStreamingAsync(string userMessage, CancellationToken ct)
    {
        logger.LogInformation("Composer streaming response for: {Message}",
            userMessage.Length > 100 ? userMessage[..100] + "…" : userMessage);

        try
        {
            refreshRegistry?.Invoke("streaming", null);

            await foreach (var update in agentService.Agent!.ExecuteStreamingAsync(agentService.Session, userMessage, ct))
            {
                switch (update.Kind)
                {
                    case StreamingUpdateKind.TextDelta:
                        _streamingContent += update.Text;
                        onStreamingUpdate?.Invoke();
                        break;

                    case StreamingUpdateKind.Completed:
                        // SharpCoder reports most provider failures as a FINAL Completed update
                        // whose Result.Status is "Error" (Result.Message carries the provider
                        // message); only OperationCanceledException, HttpRequestException and
                        // ObjectDisposedException still propagate as real exceptions. Re-raise
                        // it from INSIDE this try so the EXISTING overflow/generic catch blocks
                        // below own the recovery — never duplicate those paths here.
                        var completed = update.Result;
                        if (completed is not null && completed.Status == "Error")
                        {
                            throw new InvalidOperationException(completed.Message);
                        }

                        _lastToolCalls = completed?.ToolCallCount ?? 0;
                        break;
                }
            }

            if (saveSession is not null)
                await saveSession(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Composer streaming cancelled");
        }
        catch (Exception ex) when (IsContextOverflowError(ex))
        {
            logger.LogWarning(ex, "Composer context overflow detected — resetting session");
            _streamingContent += "\n\n⚠️ Context limit reached. Session has been reset automatically. Please repeat your request.";
            await agentService.ResetSessionAsync();
            onOverflowRecovery?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Composer streaming failed");
            _streamingContent += $"\n\n❌ Error: {ex.Message}";
        }
        finally
        {
            _isStreaming = false;
            _streamCts?.Dispose();
            _streamCts = null;

            refreshRegistry?.Invoke("idle", null);
            onStreamingUpdate?.Invoke();
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the exception (or any inner exception) represents a context
    /// overflow error from the LLM provider, identified by the
    /// <c>model_max_prompt_tokens_exceeded</c> error code in the message.
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns><c>true</c> when the exception indicates a context-window overflow.</returns>
    internal static bool IsContextOverflowError(Exception? ex)
    {
        while (ex is not null)
        {
            if (IsContextOverflowErrorMessage(ex.Message))
                return true;
            ex = ex.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="message"/> is a context overflow error from the LLM
    /// provider, identified by the <c>model_max_prompt_tokens_exceeded</c> error code.
    /// <para>
    /// The string overload exists because SharpCoder reports provider failures as a final
    /// <c>Completed</c> update carrying <c>AgentResult.Status == "Error"</c> and the provider text
    /// in <c>AgentResult.Message</c>, with no exception object available. It applies EXACTLY the
    /// same matching rule as the exception overload (which delegates here), so both shapes of the
    /// same provider failure are classified identically.
    /// </para>
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    /// <returns><c>true</c> when the message indicates a context-window overflow.</returns>
    internal static bool IsContextOverflowErrorMessage(string? message)
        => message is not null
           && message.Contains("model_max_prompt_tokens_exceeded", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var cts = _streamCts;
        _streamCts = null;
        var task = _streamingTask;
        Exception? cancelEx = null;

        try
        {
            // Cancellation is isolated so a failure here never skips the task await below —
            // the streaming loop must always complete before the agent service is disposed.
            try
            {
                cts?.Cancel();
            }
            catch (Exception ex)
            {
                cancelEx = ex;
            }

            if (task is not null)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelled.
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Composer streaming task faulted during disposal");
                }
            }
        }
        finally
        {
            cts?.Dispose();
        }

        if (cancelEx is not null)
            throw cancelEx;
    }
}
