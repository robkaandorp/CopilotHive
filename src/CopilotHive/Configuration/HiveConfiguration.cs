namespace CopilotHive.Configuration;

public sealed class HiveConfiguration
{
    public required string Goal { get; init; }
    public string WorkspacePath { get; init; } = "./workspaces";
    public string? SourcePath { get; init; }
    public string AgentsPath { get; init; } = "./agents";
    public string MetricsPath { get; init; } = "./metrics";
    public string DockerImage { get; init; } = "robkaandorp/copilot-acp-server:dev";
    public int MaxIterations { get; init; } = 10;
    public int MaxRetriesPerTask { get; init; } = 3;
    public int BasePort { get; init; } = 8001;
    public bool AlwaysImprove { get; init; }
    public required string GitHubToken { get; init; }

    /// <summary>Fallback model when no role-specific model is set.</summary>
    public string Model { get; init; } = "claude-opus-4.6";

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
    public string GetModelForRole(string agentRole) => agentRole.ToLowerInvariant() switch
    {
        "coder" => CoderModel ?? Model,
        "reviewer" => ReviewerModel ?? "gpt-5.3-codex",
        "tester" => TesterModel ?? "claude-sonnet-4.6",
        "improver" => ImproverModel ?? "claude-sonnet-4.6",
        "orchestrator" => OrchestratorModel ?? "claude-sonnet-4.6",
        _ => Model,
    };
}
