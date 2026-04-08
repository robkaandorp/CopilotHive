using CopilotHive.Workers;

namespace CopilotHive.Worker;

/// <summary>
/// Abstracts the AI agent engine used by workers. The default implementation
/// (<see cref="SharpCoderRunner"/>) uses SharpCoder's CodingAgent with direct
/// LLM API calls. Alternative implementations could use other agent frameworks.
/// </summary>
public interface IAgentRunner : IAsyncDisposable
{
    /// <summary>Structured test results reported by the tester via tool call, or null if not reported.</summary>
    TestResultReport? LastTestReport { get; }

    /// <summary>Structured report from reviewer/coder/doc-writer via tool call, or null if not reported.</summary>
    WorkerReport? LastWorkerReport { get; }

    /// <summary>Clears any previously reported test results. Call before starting a new task.</summary>
    void ClearTestReport();

    /// <summary>Clears any previously reported worker report. Call before starting a new task.</summary>
    void ClearWorkerReport();

    /// <summary>Sets the tool call bridge for custom tools. Must be called before session creation.</summary>
    void SetToolBridge(IToolCallBridge? bridge);

    /// <summary>Sets the current task ID for tool call context.</summary>
    void SetCurrentTaskId(string? taskId);

    /// <summary>Sets the current goal ID for the get_goal tool context.</summary>
    void SetCurrentGoalId(string? goalId);

    /// <summary>Sets the tester's structured report for this iteration. Only used by reviewer tasks.</summary>
    void SetTesterReport(string? report);

    /// <summary>Sets the custom agent configuration for the specified role.</summary>
    void SetCustomAgent(WorkerRole role, string agentsMdContent);

    /// <summary>
    /// Sets the agent session to resume in the next <see cref="SendPromptAsync"/> call.
    /// Pass <c>null</c> to start a fresh session.
    /// </summary>
    /// <param name="session">
    /// The session object to restore, or <c>null</c> to start a new session.
    /// Implementations that use <see cref="SharpCoder.AgentSession"/> must cast accordingly.
    /// </param>
    void SetSession(object? session);

    /// <summary>
    /// Returns the current agent session after the last <see cref="SendPromptAsync"/> call,
    /// or <c>null</c> if no session has been created yet.
    /// </summary>
    /// <returns>
    /// The session object (e.g. <see cref="SharpCoder.AgentSession"/>) or <c>null</c>.
    /// </returns>
    object? GetSession();

    /// <summary>
    /// Sets the context window size used for heartbeat Ctx% calculation and
    /// agent compaction threshold. Must be called before <see cref="SendPromptAsync"/>.
    /// </summary>
    /// <param name="maxTokens">Maximum context window in tokens.</param>
    void SetMaxContextTokens(int maxTokens);

    /// <summary>
    /// Returns the estimated context window usage as a percentage (0–100) relative to
    /// the configured context window. Returns 0 if no session is active.
    /// </summary>
    int GetContextUsagePercent();

    /// <summary>
    /// Sets the model to use for context compaction. Pass null to use the main model.
    /// Must be called before <see cref="SendPromptAsync"/>.
    /// </summary>
    void SetCompactionModel(string? model);

    /// <summary>Connects to the underlying AI agent engine.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Resets the current session, optionally switching to a different model.</summary>
    Task ResetSessionAsync(string? model = null, CancellationToken ct = default);

    /// <summary>Sends a prompt to the AI agent and returns its response.</summary>
    Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct);
}
