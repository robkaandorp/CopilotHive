using CopilotHive.Services;

namespace CopilotHive.Orchestration;

/// <summary>
/// LLM-powered brain for the distributed orchestrator. Unlike <see cref="IOrchestratorBrain"/>
/// (which spawns its own container), this interface is designed for the server-mode orchestrator
/// where the Brain runs alongside the server process and supports multiple concurrent goals.
/// </summary>
public interface IDistributedBrain
{
    /// <summary>Connect to the local Copilot CLI instance.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Plan which phases are needed for a goal. Returns the first action to take.
    /// The pipeline's conversation context is used for continuity.
    /// </summary>
    Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default);

    /// <summary>
    /// Craft a context-aware prompt for a specific worker role.
    /// Uses the pipeline's accumulated state and conversation history.
    /// </summary>
    Task<string> CraftPromptAsync(
        GoalPipeline pipeline,
        string workerRole,
        string? additionalContext = null,
        CancellationToken ct = default);

    /// <summary>
    /// Interpret worker output and extract structured verdict/metrics.
    /// Updates the pipeline's conversation context.
    /// </summary>
    Task<OrchestratorDecision> InterpretOutputAsync(
        GoalPipeline pipeline,
        string workerRole,
        string workerOutput,
        CancellationToken ct = default);

    /// <summary>
    /// Decide the next step given the pipeline's current state and phase context.
    /// </summary>
    Task<OrchestratorDecision> DecideNextStepAsync(
        GoalPipeline pipeline,
        string context,
        CancellationToken ct = default);

    /// <summary>
    /// Send a status update to the Brain for a specific goal's context.
    /// </summary>
    Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default);
}
