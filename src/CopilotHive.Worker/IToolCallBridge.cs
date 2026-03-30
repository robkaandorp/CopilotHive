namespace CopilotHive.Worker;

/// <summary>
/// Abstraction for worker→orchestrator tool calls during task execution.
/// The SDK's OnUserInputRequest handler and custom tools (e.g., report_progress)
/// use this bridge to send requests via gRPC and await orchestrator responses.
/// </summary>
public interface IToolCallBridge
{
    /// <summary>
    /// Ask the orchestrator's Brain for clarification and return the answer.
    /// Used when the Copilot model needs clarification about the task.
    /// </summary>
    Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct);

    /// <summary>
    /// Report progress back to the orchestrator for logging/monitoring.
    /// Fire-and-forget — does not block on a response.
    /// </summary>
    Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct);
}
