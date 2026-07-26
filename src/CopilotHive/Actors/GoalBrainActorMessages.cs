namespace CopilotHive.Actors;

/// <summary>Marker interface for all goal-brain actor messages.</summary>
internal interface IGoalBrainMessage { }

/// <summary>Executes a prompt against the goal's coding agent.</summary>
internal sealed record ExecutePromptMessage(
    string Prompt,
    CancellationToken Ct,
    TaskCompletionSource<GoalBrainExecutionResult> Reply) : IGoalBrainMessage;

/// <summary>Injects a user note into the goal session history.</summary>
internal sealed record InjectNoteMessage(string Note, TaskCompletionSource<bool> Reply) : IGoalBrainMessage;

/// <summary>Requests a snapshot of the goal actor state.</summary>
internal sealed record GetGoalStateMessage(TaskCompletionSource<GoalBrainActorState> Reply) : IGoalBrainMessage;

/// <summary>Result of an executed prompt: response text plus any recorded tool call.</summary>
internal sealed record GoalBrainExecutionResult(string Text, GoalBrainToolCallResult? ToolCall);

/// <summary>Base type for results captured from goal-brain tool calls.</summary>
internal abstract record GoalBrainToolCallResult(string ToolName);

/// <summary>Result of an <c>escalate_to_composer</c> tool call.</summary>
internal sealed record EscalateToolResult(string Question, string Reason)
    : GoalBrainToolCallResult("escalate_to_composer");

/// <summary>Result of a <c>report_iteration_plan</c> tool call.</summary>
internal sealed record PlanToolResult(string[] Phases, string PhaseInstructions, string Reason, string? ModelTiers)
    : GoalBrainToolCallResult("report_iteration_plan");

/// <summary>Immutable snapshot of goal-brain actor state.</summary>
internal sealed record GoalBrainActorState(string GoalId, int MessageCount, long EstimatedTokens, string Model);

/// <summary>Factory helpers for messages that carry reply channels.</summary>
internal static class GoalBrainActorMessages
{
    private static TaskCompletionSource<T> NewReply<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates an execute-prompt message with an asynchronous reply source.</summary>
    internal static ExecutePromptMessage CreateExecutePromptMessage(string prompt, CancellationToken ct) =>
        new(prompt, ct, NewReply<GoalBrainExecutionResult>());

    /// <summary>Creates an inject-note message with an asynchronous reply source.</summary>
    internal static InjectNoteMessage CreateInjectNoteMessage(string note) => new(note, NewReply<bool>());

    /// <summary>Creates a get-goal-state message with an asynchronous reply source.</summary>
    internal static GetGoalStateMessage CreateGetGoalStateMessage() => new(NewReply<GoalBrainActorState>());
}
