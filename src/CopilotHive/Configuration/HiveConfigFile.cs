namespace CopilotHive.Configuration;

/// <summary>
/// Represents the YAML configuration file (hive-config.yaml) from the config repository.
/// </summary>
public sealed class HiveConfigFile
{
    public string Version { get; set; } = "1.0";
    public List<RepositoryConfig> Repositories { get; set; } = [];
    public Dictionary<string, WorkerConfig> Workers { get; set; } = [];
    public OrchestratorConfig Orchestrator { get; set; } = new();
}

public sealed class RepositoryConfig
{
    public required string Name { get; set; }
    public required string Url { get; set; }
    public string DefaultBranch { get; set; } = "main";
}

public sealed class WorkerConfig
{
    public string? Model { get; set; }
}

public sealed class OrchestratorConfig
{
    public string Model { get; set; } = "claude-sonnet-4.6";
    public int MaxIterations { get; set; } = 10;
    public int MaxRetriesPerTask { get; set; } = 3;
    public bool AlwaysImprove { get; set; }
}
