using CopilotHive.Workers;

namespace CopilotHive.Configuration;

/// <summary>
/// Top-level runtime configuration for a CopilotHive orchestration session.
/// </summary>
public sealed class HiveConfiguration
{
    /// <summary>Natural-language goal that drives the entire hive session.</summary>
    public required string Goal { get; init; }
    /// <summary>Root path where per-worker git workspaces are created.</summary>
    public string WorkspacePath { get; init; } = "./workspaces";
    /// <summary>Optional path to a local source tree used to seed the bare repo.</summary>
    public string? SourcePath { get; init; }
    /// <summary>Path to the directory containing per-role AGENTS.md files.</summary>
    public string AgentsPath { get; init; } = "./agents";
    /// <summary>Path where iteration metrics JSON files are written.</summary>
    public string MetricsPath { get; init; } = "./metrics";
    /// <summary>Docker image used to spawn worker containers.</summary>
    public string DockerImage { get; init; } = "robkaandorp/copilot-acp-server:dev";
    /// <summary>Maximum number of goal iterations before the session terminates.</summary>
    public int MaxIterations { get; init; } = Constants.DefaultMaxIterations;
    /// <summary>Maximum number of task-level retries before a goal is failed.</summary>
    public int MaxRetriesPerTask { get; init; } = Constants.DefaultMaxRetriesPerTask;
    /// <summary>Starting TCP port for worker containers; each additional worker increments by one.</summary>
    public int BasePort { get; init; } = Constants.DefaultBasePort;
    /// <summary>When <c>true</c>, the improver phase runs after every iteration even on success.</summary>
    public bool AlwaysImprove { get; init; }
    /// <summary>When <c>true</c>, enables verbose logging of prompts, worker output, and Brain reasoning.</summary>
    public bool VerboseLogging { get; init; }
    /// <summary>Maximum context window size in tokens for the Brain model. Used for compaction decisions and context monitoring.</summary>
    public int BrainContextWindow { get; init; } = Constants.DefaultBrainContextWindow;
    /// <summary>Maximum tool-call steps the Brain agent may take per request.</summary>
    public int BrainMaxSteps { get; init; } = Constants.DefaultBrainMaxSteps;
    /// <summary>GitHub personal access token used to authenticate Docker container spawns and git operations.</summary>
    public required string GitHubToken { get; init; }

    /// <summary>Fallback model when no role-specific model is set.</summary>
    public string Model { get; init; } = Constants.DefaultModel;

    /// <summary>Model for the coder role (default: claude-opus-4.6).</summary>
    public string? CoderModel { get; init; }

    /// <summary>Model for the reviewer role (default: gpt-5.3-codex).</summary>
    public string? ReviewerModel { get; init; }

    /// <summary>Model for the tester role (default: claude-sonnet-4.6).</summary>
    public string? TesterModel { get; init; }

    /// <summary>Model for the improver role (default: claude-sonnet-4.6).</summary>
    public string? ImproverModel { get; init; }

    /// <summary>Model for the doc-writer role (default: claude-haiku-4.5).</summary>
    public string? DocWriterModel { get; init; }

    /// <summary>Model for the orchestrator's output interpretation (default: claude-sonnet-4.6).</summary>
    public string? OrchestratorModel { get; init; }

    /// <summary>Premium model for the coder role, used when the Brain selects the 'premium' tier.</summary>
    public string? PremiumCoderModel { get; init; }

    /// <summary>Premium model for the reviewer role, used when the Brain selects the 'premium' tier.</summary>
    public string? PremiumReviewerModel { get; init; }

    /// <summary>Premium model for the tester role, used when the Brain selects the 'premium' tier.</summary>
    public string? PremiumTesterModel { get; init; }

    /// <summary>Premium model for the improver role, used when the Brain selects the 'premium' tier.</summary>
    public string? PremiumImproverModel { get; init; }

    /// <summary>Premium model for the doc-writer role, used when the Brain selects the 'premium' tier.</summary>
    public string? PremiumDocWriterModel { get; init; }

    /// <summary>
    /// Resolves the model to use for a given agent role.
    /// Falls back to <see cref="Model"/> when no role-specific override is set.
    /// </summary>
    /// <param name="role">Worker role (e.g. Coder, Reviewer).</param>
    /// <returns>The model identifier string for the specified role.</returns>
    public string GetModelForRole(WorkerRole role) => role switch
    {
        WorkerRole.Coder => CoderModel ?? Model,
        WorkerRole.Reviewer => ReviewerModel ?? Constants.DefaultReviewerModel,
        WorkerRole.Tester => TesterModel ?? Constants.DefaultWorkerModel,
        WorkerRole.Improver => ImproverModel ?? Constants.DefaultWorkerModel,
        WorkerRole.DocWriter => DocWriterModel ?? Constants.DefaultDocWriterModel,
        WorkerRole.Orchestrator => OrchestratorModel ?? Constants.DefaultWorkerModel,
        _ => Model,
    };

    /// <summary>
    /// Resolves the premium model configured for a given agent role, or <c>null</c> if none is set.
    /// </summary>
    /// <param name="role">Worker role.</param>
    /// <returns>The premium model identifier for the role, or <c>null</c> if not configured.</returns>
    public string? GetPremiumModelForRole(WorkerRole role) => role switch
    {
        WorkerRole.Coder => PremiumCoderModel,
        WorkerRole.Reviewer => PremiumReviewerModel,
        WorkerRole.Tester => PremiumTesterModel,
        WorkerRole.Improver => PremiumImproverModel,
        WorkerRole.DocWriter => PremiumDocWriterModel,
        _ => null,
    };
}
