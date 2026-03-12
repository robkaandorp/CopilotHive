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
    public string Model { get; init; } = "claude-opus-4.6";
    public bool AlwaysImprove { get; init; }
    public required string GitHubToken { get; init; }
}
