using System.Threading.Channels;
using CopilotHive.Goals;
using CopilotHive.Services;

namespace CopilotHive.Actors;

/// <summary>
/// Channel-based actor owning the mutable state of a single goal. All state changes are
/// serialized through a single-reader mailbox, so no locking is required by callers.
/// </summary>
public sealed class GoalActor : IAsyncDisposable
{
    private readonly string _goalId;
    private readonly Channel<IMessage> _mailbox = Channel.CreateUnbounded<IMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _loopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _started;
    private int _loopCount;
    private bool _isTerminal;

    private GoalStatus _status = GoalStatus.Pending;
    private GoalPhase? _phase;
    private int _iteration;
    private string? _activeTaskId;

    /// <summary>Creates an actor for the given goal id.</summary>
    public GoalActor(string goalId) => _goalId = goalId;

    /// <summary>Completes when the message loop has exited.</summary>
    public Task Completion => _loopCompletion.Task;

    /// <summary>True once the message loop has exited.</summary>
    public bool IsCompleted => Completion.IsCompleted;

    /// <summary>Number of message loops launched. Exposed for tests; must never exceed 1.</summary>
    internal int LoopCount => Volatile.Read(ref _loopCount);

    /// <summary>Starts the message loop. Safe to call concurrently; only one loop runs.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() => MessageLoopAsync(_cts.Token));
    }

    /// <summary>Enqueues a message. Returns false once the mailbox is closed.</summary>
    public bool Tell(IMessage message) => _mailbox.Writer.TryWrite(message);

    private static bool IsTerminalStatus(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled;

    private async Task MessageLoopAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _loopCount);
        try
        {
            await foreach (var message in _mailbox.Reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested)
                {
                    CancelReply(message);
                    break;
                }

                if (_isTerminal)
                {
                    ReplyWithSnapshot(message);
                    continue;
                }

                Handle(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled — remaining items are drained below.
        }
        catch (ChannelClosedException)
        {
            // Normal exit — the mailbox was completed.
        }
        finally
        {
            while (_mailbox.Reader.TryRead(out var message))
            {
                CancelReply(message);
            }

            _loopCompletion.TrySetResult();
        }
    }

    private void Handle(IMessage message)
    {
        switch (message)
        {
            case SetStatusMessage s:
                _status = s.Status;
                if (s.Status == GoalStatus.Cancelled)
                {
                    _activeTaskId = null;
                }

                if (IsTerminalStatus(s.Status))
                {
                    InitiateTerminalShutdown();
                }

                break;

            case SetPhaseMessage p:
                _phase = p.Phase;
                break;

            case SetActiveTaskMessage t:
                _activeTaskId = t.TaskId;
                break;

            case SetIterationMessage i:
                _iteration = i.Iteration;
                break;

            case CancelMessage c:
                _status = GoalStatus.Cancelled;
                _activeTaskId = null;
                c.Reply.TrySetResult(CreateStateSnapshot());
                InitiateTerminalShutdown();
                break;

            case GetStateMessage g:
                g.Reply.TrySetResult(CreateStateSnapshot());
                break;
        }
    }

    private void ReplyWithSnapshot(IMessage message)
    {
        switch (message)
        {
            case GetStateMessage g:
                g.Reply.TrySetResult(CreateStateSnapshot());
                break;
            case CancelMessage c:
                c.Reply.TrySetResult(CreateStateSnapshot());
                break;
        }
    }

    private static void CancelReply(IMessage message)
    {
        switch (message)
        {
            case GetStateMessage g:
                g.Reply.TrySetCanceled();
                break;
            case CancelMessage c:
                c.Reply.TrySetCanceled();
                break;
        }
    }

    private void InitiateTerminalShutdown()
    {
        _isTerminal = true;
        _mailbox.Writer.TryComplete();
    }

    private GoalActorState CreateStateSnapshot() =>
        new(_goalId, _status, _phase, _iteration, _activeTaskId);

    /// <summary>Stops the actor, cancelling any pending replies.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
            _mailbox.Writer.TryComplete();

            if (Volatile.Read(ref _started) == 0)
            {
                // No loop is reading the mailbox — cancel pending replies ourselves.
                while (_mailbox.Reader.TryRead(out var message))
                {
                    CancelReply(message);
                }

                Start();
            }

            var finished = await Task.WhenAny(Completion, Task.Delay(TimeSpan.FromSeconds(5)));
            if (finished != Completion)
            {
                throw new TimeoutException("GoalActor did not complete within 5 seconds.");
            }
        }
        finally
        {
            _cts.Dispose();
        }
    }}
