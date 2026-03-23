using CopilotHive.Services;

namespace CopilotHive.Orchestration;

/// <summary>
/// LLM-powered brain for the distributed orchestrator.
/// The Brain has two jobs: plan iteration phases and craft worker prompts.
/// Workers report structured verdicts; the state machine drives sequencing.
/// </summary>
public interface IDistributedBrain
{
    /// <summary>Connect to the LLM provider and initialise the chat client.</summary>
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

    /// <summary>Removes the session for a completed/failed goal and frees resources.</summary>
    Task CleanupGoalSessionAsync(string goalId);

    /// <summary>
    /// Restores a session for an in-progress goal on restart.
    /// Replays conversation history or loads a persisted session file.
    /// </summary>
    Task ReprimeSessionAsync(GoalPipeline pipeline, CancellationToken ct);
}
