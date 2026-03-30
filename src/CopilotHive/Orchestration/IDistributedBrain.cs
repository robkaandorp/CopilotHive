using CopilotHive.Services;

namespace CopilotHive.Orchestration;

/// <summary>
/// Discriminated result returned by <see cref="IDistributedBrain.AskQuestionAsync"/>.
/// Either carries a text answer (<see cref="IsEscalation"/> is <c>false</c>) or
/// signals that the Brain wants the question routed to the Composer
/// (<see cref="IsEscalation"/> is <c>true</c>).
/// </summary>
/// <param name="IsEscalation">When <c>true</c>, the Brain cannot answer and requests escalation.</param>
/// <param name="Text">The answer text when <see cref="IsEscalation"/> is <c>false</c>; otherwise <c>null</c>.</param>
/// <param name="EscalationQuestion">The original question forwarded to the Composer; <c>null</c> when not escalating.</param>
/// <param name="EscalationReason">The reason the Brain is escalating; <c>null</c> when not escalating.</param>
public record BrainResponse(bool IsEscalation, string? Text, string? EscalationQuestion, string? EscalationReason)
{
    /// <summary>Creates a direct-answer response.</summary>
    /// <param name="text">The answer text.</param>
    public static BrainResponse Answer(string text) => new(false, text, null, null);

    /// <summary>Creates an escalation response.</summary>
    /// <param name="question">The question to forward to the Composer.</param>
    /// <param name="reason">Why the Brain is escalating.</param>
    public static BrainResponse Escalated(string question, string reason) => new(true, null, question, reason);
}

/// <summary>
/// LLM-powered brain for the distributed orchestrator.
/// The Brain maintains a single persistent session across all goals, using
/// CodingAgent for LLM communication with automatic context compaction.
/// It has two jobs: plan iteration phases and craft worker prompts.
/// </summary>
public interface IDistributedBrain
{
    /// <summary>Connect to the LLM provider, create the CodingAgent, and load any persisted session.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Plan the workflow phases for the current iteration. Called at the start of each
    /// iteration (new goal or after failure loop-back). Returns an IterationPlan with
    /// the ordered phase sequence and per-phase instructions.
    /// </summary>
    /// <param name="pipeline">The goal pipeline containing iteration state and context.</param>
    /// <param name="additionalContext">Optional extra context injected at the top of the planning prompt (e.g. retry context).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default);

    /// <summary>
    /// Craft a context-aware prompt for a specific phase's worker.
    /// Uses the pipeline's accumulated state and conversation history.
    /// </summary>
    Task<string> CraftPromptAsync(
        GoalPipeline pipeline,
        GoalPhase phase,
        string? additionalContext = null,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a concise squash-merge commit message for the specified pipeline.
    /// Returns a short subject (~72 chars) with 2–4 bullet body lines, or <c>null</c>
    /// if the Brain is not connected or any error occurs.
    /// </summary>
    /// <param name="pipeline">The goal pipeline providing context for the message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A concise commit message body (without the "Goal:" prefix), or <c>null</c> on failure.</returns>
    Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default);

    /// <summary>
    /// Ensures the Brain's read-only clone for a target repository exists and is
    /// up-to-date. Called by GoalDispatcher before starting a goal so the Brain
    /// has file access to the latest base branch code.
    /// </summary>
    Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default);

    /// <summary>
    /// Injects the orchestrator AGENTS.md instructions into the Brain session as a
    /// policy-update message. Called when the config repo agents file changes and
    /// after context compaction to ensure the Brain always has fresh rules.
    /// </summary>
    Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default);

    /// <summary>
    /// Asks the Brain a question on behalf of a worker, returning a structured response that
    /// either contains an answer or signals escalation to the Composer.
    /// </summary>
    /// <param name="goalId">The goal identifier this question relates to.</param>
    /// <param name="iteration">The current iteration number.</param>
    /// <param name="phase">The current pipeline phase name.</param>
    /// <param name="workerRole">The worker role asking the question.</param>
    /// <param name="question">The question text from the worker.</param>
    /// <returns>
    /// A <see cref="BrainResponse"/> that is either a direct answer (<see cref="BrainResponse.IsEscalation"/> = <c>false</c>)
    /// or an escalation request (<see cref="BrainResponse.IsEscalation"/> = <c>true</c>).
    /// </returns>
    Task<BrainResponse> AskQuestionAsync(string goalId, int iteration, string phase, string workerRole, string question);

    /// <summary>Returns current Brain context and usage statistics, or null if not connected.</summary>
    BrainStats? GetStats();

    /// <summary>
    /// Resets the Brain session by reloading orchestrator instructions from disk,
    /// clearing message history, and creating a fresh <see cref="SharpCoder.AgentSession"/>.
    /// Also deletes the persisted session file. Thread-safe via the Brain call gate.
    /// </summary>
    Task ResetSessionAsync(CancellationToken ct = default);
}

/// <summary>Snapshot of the Brain's context and usage state.</summary>
public sealed class BrainStats
{
    /// <summary>Model identifier.</summary>
    public string Model { get; init; } = "";
    /// <summary>Number of messages in the conversation history.</summary>
    public int MessageCount { get; init; }
    /// <summary>Estimated current context size in tokens.</summary>
    public long EstimatedContextTokens { get; init; }
    /// <summary>Maximum context window size.</summary>
    public long MaxContextTokens { get; init; }
    /// <summary>Context usage percentage (0–100).</summary>
    public int ContextUsagePercent { get; init; }
    /// <summary>Cumulative input tokens consumed.</summary>
    public long CumulativeInputTokens { get; init; }
    /// <summary>Cumulative output tokens generated.</summary>
    public long CumulativeOutputTokens { get; init; }
    /// <summary>Maximum tool-call steps per request.</summary>
    public int MaxSteps { get; init; }
    /// <summary>Whether the Brain is connected to an LLM provider.</summary>
    public bool IsConnected { get; init; }
}
