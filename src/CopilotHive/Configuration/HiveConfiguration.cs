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

    /// <summary>Model for the orchestrator's output interpretation (default: claude-sonnet-4.6).</summary>
    public string? OrchestratorModel { get; init; }

    /// <summary>
    /// Resolves the model to use for a given agent role.
    /// Falls back to <see cref="Model"/> when no role-specific override is set.
    /// </summary>
    /// <param name="agentRole">Role name (e.g. "coder", "reviewer").</param>
    /// <returns>The model identifier string for the specified role.</returns>
    public string GetModelForRole(string agentRole) => agentRole.ToLowerInvariant() switch
    {
        "coder" => CoderModel ?? Model,
        "reviewer" => ReviewerModel ?? Constants.DefaultReviewerModel,
        "tester" => TesterModel ?? Constants.DefaultWorkerModel,
        "improver" => ImproverModel ?? Constants.DefaultWorkerModel,
        "orchestrator" => OrchestratorModel ?? Constants.DefaultWorkerModel,
        _ => Model,
    };
}
