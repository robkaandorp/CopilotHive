using CopilotHive.Goals;
using CopilotHive.Services;

namespace CopilotHive.Actors;

/// <summary>
/// Channel-based actor owning the mutable state of a single goal. All state changes are
/// serialized through a single-reader mailbox, so no locking is required by callers.
/// </summary>
public sealed class GoalActor : Actor<IMessage>
{
    private readonly string _goalId;

    private int _loopCount;
    private bool _isTerminal;

    private GoalStatus _status = GoalStatus.Pending;
    private GoalPhase? _phase;
    private int _iteration;
    private string? _activeTaskId;

    /// <summary>Creates an actor for the given goal id.</summary>
    public GoalActor(string goalId) => _goalId = goalId;

    /// <summary>Number of message loops launched. Exposed for tests; must never exceed 1.</summary>
    internal int LoopCount => Volatile.Read(ref _loopCount);

    private static bool IsTerminalStatus(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled;

    /// <inheritdoc />
    protected override void OnLoopStarted() => Interlocked.Increment(ref _loopCount);

    /// <inheritdoc />
    protected override void OnDisposeTimeout() =>
        throw new TimeoutException("GoalActor did not complete within 5 seconds.");

    /// <inheritdoc />
    protected override Task HandleAsync(IMessage message, CancellationToken ct)
    {
        if (_isTerminal)
        {
            ReplyWithSnapshot(message);
            return Task.CompletedTask;
        }

        Handle(message);
        return Task.CompletedTask;
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

    /// <inheritdoc />
    protected override void CancelReply(IMessage message)
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
        CompleteMailbox();
    }

    private GoalActorState CreateStateSnapshot() =>
        new(_goalId, _status, _phase, _iteration, _activeTaskId);
}
