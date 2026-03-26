using CopilotHive.Services;

namespace CopilotHive.Orchestration;

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
    Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default);

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
