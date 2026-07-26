using System.Threading.Channels;

namespace CopilotHive.Actors;

/// <summary>
/// Channel-based actor base class. All messages are processed sequentially through a
/// single-reader mailbox. Derived classes implement <see cref="HandleAsync"/> and
/// <see cref="CancelReply"/>; optional virtual hooks customize lifecycle behavior.
/// </summary>
public abstract class Actor<TMessage> : IAsyncDisposable
    where TMessage : class
{
    private readonly Channel<TMessage> _mailbox = Channel.CreateUnbounded<TMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _loopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycleLock = new();
    private readonly CancellationToken _loopToken;
    private Task? _loopTask;
    private bool _disposed;

    /// <summary>Initializes the mailbox and lifecycle cancellation token.</summary>
    protected Actor() => _loopToken = _cts.Token;

    /// <summary>Completes when the message loop has exited.</summary>
    public Task Completion => _loopCompletion.Task;
    /// <summary>True once the message loop has exited.</summary>
    public bool IsCompleted => Completion.IsCompleted;
    /// <summary>Enqueues a message. Returns false once the mailbox is closed.</summary>
    public bool Tell(TMessage message) => _mailbox.Writer.TryWrite(message);
    internal bool IsStarted { get { lock (_lifecycleLock) { return _loopTask is not null; } } }

    /// <summary>Token cancelled when the actor is disposed.</summary>
    protected CancellationToken LoopToken => _loopToken;
    /// <summary>Closes the mailbox for writing so the loop drains and exits.</summary>
    protected void CompleteMailbox() => _mailbox.Writer.TryComplete();

    /// <summary>Starts the message loop. Safe to call concurrently; only one loop runs.</summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_loopTask is not null || _disposed) return;
            _loopTask = Task.Run(() => MessageLoopAsync(_loopToken));
        }
    }

    /// <summary>Handles a single message. Called sequentially by the loop.</summary>
    protected abstract Task HandleAsync(TMessage message, CancellationToken ct);
    /// <summary>Cancels any pending reply carried by the message.</summary>
    protected abstract void CancelReply(TMessage message);

    /// <summary>Invoked when the message handler throws. Defaults to cancelling the reply.</summary>
    protected virtual void OnUnhandledException(TMessage message, Exception ex) => CancelReply(message);
    /// <summary>Invoked once when the message loop begins.</summary>
    protected virtual void OnLoopStarted() { }
    /// <summary>Invoked after the loop drains, before completion is signalled.</summary>
    protected virtual Task OnShutdownAsync() => Task.CompletedTask;
    /// <summary>Invoked when the actor is disposed before it was ever started.</summary>
    protected virtual void OnUnstartedDispose() { }
    /// <summary>Invoked when the loop does not finish within the dispose timeout.</summary>
    protected virtual void OnDisposeTimeout() { }

    private async Task MessageLoopAsync(CancellationToken ct)
    {
        try
        {
            OnLoopStarted();
            await foreach (var message in _mailbox.Reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested)
                {
                    CancelReply(message);
                    break;
                }
                try { await HandleAsync(message, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    CancelReply(message);
                    break;
                }
                catch (Exception ex) { OnUnhandledException(message, ex); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        finally
        {
            while (_mailbox.Reader.TryRead(out var message))
                CancelReply(message);
            try { await OnShutdownAsync(); } catch { }
            _loopCompletion.TrySetResult();
        }
    }

    /// <summary>Stops the actor, cancelling any pending replies.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try
        {
            _cts.Cancel();
            _mailbox.Writer.TryComplete();

            if (!IsStarted)
            {
                while (_mailbox.Reader.TryRead(out var message))
                    CancelReply(message);
                OnUnstartedDispose();
                _loopCompletion.TrySetResult();
            }
            else
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _loopCompletion.Task.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    OnDisposeTimeout();
                }
            }
        }
        finally { _cts.Dispose(); }
    }
}
