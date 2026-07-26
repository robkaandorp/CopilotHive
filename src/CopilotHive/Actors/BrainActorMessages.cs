using CopilotHive.Services;

namespace CopilotHive.Actors;

/// <summary>Marker interface for all brain actor messages.</summary>
internal interface IBrainMessage { }

/// <summary>Loads or creates the master Brain session.</summary>
internal sealed record ConnectMessage(TaskCompletionSource<bool> Reply) : IBrainMessage;

/// <summary>Forks the master session for a goal.</summary>
internal sealed record ForkSessionMessage(string GoalId, TaskCompletionSource<bool> Reply) : IBrainMessage;

/// <summary>Deletes a goal session file.</summary>
internal sealed record DeleteSessionMessage(string GoalId, TaskCompletionSource<bool> Reply) : IBrainMessage;

/// <summary>Merges a goal summary into the master session.</summary>
internal sealed record MergeSummaryMessage(string GoalId, string Summary, TaskCompletionSource<bool> Reply) : IBrainMessage;

/// <summary>Updates the model and optionally the max context tokens.</summary>
internal sealed record UpdateModelMessage(string Model, int? MaxContextTokens, TaskCompletionSource<bool> Reply) : IBrainMessage;

/// <summary>Registers an active pipeline. Fire-and-forget.</summary>
internal sealed record RegisterPipelineMessage(string GoalId, GoalPipeline Pipeline) : IBrainMessage;

/// <summary>Deregisters an active pipeline. Fire-and-forget.</summary>
internal sealed record DeregisterPipelineMessage(string GoalId) : IBrainMessage;

/// <summary>Requests the pipeline registered for a goal, if any.</summary>
internal sealed record GetPipelineMessage(string GoalId, TaskCompletionSource<GoalPipeline?> Reply) : IBrainMessage;

/// <summary>Requests a snapshot of brain statistics.</summary>
internal sealed record GetStatsMessage(TaskCompletionSource<BrainActorStats?> Reply) : IBrainMessage;

/// <summary>Checks whether a goal session file exists on disk.</summary>
internal sealed record GoalSessionExistsMessage(string GoalId, TaskCompletionSource<bool> Reply) : IBrainMessage;

/// <summary>Registers an already-existing goal session, forking one if the file is missing.</summary>
internal sealed record RegisterExistingSessionMessage(string GoalId, TaskCompletionSource<bool> Reply) : IBrainMessage;

/// <summary>Injects orchestrator instructions into the actor state.</summary>
internal sealed record InjectOrchestratorInstructionsMessage(string Instructions, TaskCompletionSource<bool> Reply) : IBrainMessage;

/// <summary>Executes a prompt on the goal's child actor.</summary>
internal sealed record ExecutePromptOnChildMessage(
    string GoalId,
    string Prompt,
    CancellationToken Ct,
    TaskCompletionSource<GoalBrainExecutionResult> Reply) : IBrainMessage;

/// <summary>Injects a note into the goal's child actor session.</summary>
internal sealed record InjectNoteOnChildMessage(
    string GoalId,
    string Note,
    TaskCompletionSource<bool> Reply) : IBrainMessage;

/// <summary>Immutable snapshot of brain actor statistics.</summary>
internal sealed record BrainActorStats(string Model, int MessageCount, long ContextTokens, long MaxContextTokens, bool IsConnected);

/// <summary>Factory helpers for messages that carry reply channels.</summary>
internal static class BrainActorMessages
{
    private static TaskCompletionSource<T> NewReply<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a connect message with an asynchronous reply source.</summary>
    internal static ConnectMessage CreateConnectMessage() => new(NewReply<bool>());

    /// <summary>Creates a fork-session message with an asynchronous reply source.</summary>
    internal static ForkSessionMessage CreateForkSessionMessage(string goalId) => new(goalId, NewReply<bool>());

    /// <summary>Creates a delete-session message with an asynchronous reply source.</summary>
    internal static DeleteSessionMessage CreateDeleteSessionMessage(string goalId) => new(goalId, NewReply<bool>());

    /// <summary>Creates a merge-summary message with an asynchronous reply source.</summary>
    internal static MergeSummaryMessage CreateMergeSummaryMessage(string goalId, string summary) =>
        new(goalId, summary, NewReply<bool>());

    /// <summary>Creates an update-model message with an asynchronous reply source.</summary>
    internal static UpdateModelMessage CreateUpdateModelMessage(string model, int? maxContextTokens) =>
        new(model, maxContextTokens, NewReply<bool>());

    /// <summary>Creates a get-pipeline message with an asynchronous reply source.</summary>
    internal static GetPipelineMessage CreateGetPipelineMessage(string goalId) => new(goalId, NewReply<GoalPipeline?>());

    /// <summary>Creates a get-stats message with an asynchronous reply source.</summary>
    internal static GetStatsMessage CreateGetStatsMessage() => new(NewReply<BrainActorStats?>());

    /// <summary>Creates a goal-session-exists message with an asynchronous reply source.</summary>
    internal static GoalSessionExistsMessage CreateGoalSessionExistsMessage(string goalId) =>
        new(goalId, NewReply<bool>());

    /// <summary>Creates a register-existing-session message with an asynchronous reply source.</summary>
    internal static RegisterExistingSessionMessage CreateRegisterExistingSessionMessage(string goalId) =>
        new(goalId, NewReply<bool>());

    /// <summary>Creates an inject-orchestrator-instructions message with an asynchronous reply source.</summary>
    internal static InjectOrchestratorInstructionsMessage CreateInjectOrchestratorInstructionsMessage(string instructions) =>
        new(instructions, NewReply<bool>());

    /// <summary>Creates an execute-prompt-on-child message with an asynchronous reply source.</summary>
    internal static ExecutePromptOnChildMessage CreateExecutePromptOnChildMessage(string goalId, string prompt, CancellationToken ct) =>
        new(goalId, prompt, ct, NewReply<GoalBrainExecutionResult>());

    /// <summary>Creates an inject-note-on-child message with an asynchronous reply source.</summary>
    internal static InjectNoteOnChildMessage CreateInjectNoteOnChildMessage(string goalId, string note) =>
        new(goalId, note, NewReply<bool>());
}
