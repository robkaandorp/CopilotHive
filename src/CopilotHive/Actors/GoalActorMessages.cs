using CopilotHive.Goals;
using CopilotHive.Services;

namespace CopilotHive.Actors;

/// <summary>Marker interface for all goal actor messages.</summary>
public interface IMessage { }

/// <summary>Sets the goal status.</summary>
public sealed record SetStatusMessage(GoalStatus Status) : IMessage;

/// <summary>Sets the current pipeline phase.</summary>
public sealed record SetPhaseMessage(GoalPhase Phase) : IMessage;

/// <summary>Sets the currently active task id.</summary>
public sealed record SetActiveTaskMessage(string? TaskId) : IMessage;

/// <summary>Sets the current iteration number.</summary>
public sealed record SetIterationMessage(int Iteration) : IMessage;

/// <summary>Requests cancellation of the goal, replying with the final state.</summary>
public sealed record CancelMessage(TaskCompletionSource<GoalActorState> Reply) : IMessage;

/// <summary>Requests a snapshot of the current actor state.</summary>
public sealed record GetStateMessage(TaskCompletionSource<GoalActorState> Reply) : IMessage;

/// <summary>Immutable snapshot of a goal actor's state.</summary>
public sealed record GoalActorState(string GoalId, GoalStatus Status, GoalPhase? Phase, int Iteration, string? ActiveTaskId);

/// <summary>Factory helpers for messages that carry reply channels.</summary>
public static class GoalActorMessages
{
    /// <summary>Creates a state query message with an asynchronous reply source.</summary>
    public static GetStateMessage CreateGetStateMessage() =>
        new(new TaskCompletionSource<GoalActorState>(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Creates a cancel message with an asynchronous reply source.</summary>
    public static CancelMessage CreateCancelMessage() =>
        new(new TaskCompletionSource<GoalActorState>(TaskCreationOptions.RunContinuationsAsynchronously));
}
