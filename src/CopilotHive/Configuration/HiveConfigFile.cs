namespace CopilotHive.Configuration;

/// <summary>
/// Represents the YAML configuration file (hive-config.yaml) from the config repository.
/// </summary>
public sealed class HiveConfigFile
{
    /// <summary>Schema version of the config file.</summary>
    public string Version { get; set; } = "1.0";
    /// <summary>List of repositories this hive operates on.</summary>
    public List<RepositoryConfig> Repositories { get; set; } = [];
    /// <summary>Per-role worker configuration keyed by role name.</summary>
    public Dictionary<string, WorkerConfig> Workers { get; set; } = [];
    /// <summary>Orchestrator-level configuration.</summary>
    public OrchestratorConfig Orchestrator { get; set; } = new();

    /// <summary>
    /// Resolves the model to use for a given role.
    /// Returns the per-role override if configured, otherwise the orchestrator's default model.
    /// </summary>
    public string GetModelForRole(string roleName) =>
        Workers.TryGetValue(roleName.ToLowerInvariant(), out var wc) && !string.IsNullOrEmpty(wc.Model)
            ? wc.Model
            : Orchestrator.Model;

    /// <summary>
    /// Resolves the premium model configured for a given role, or <c>null</c> if none is set.
    /// </summary>
    /// <param name="roleName">Role name (e.g. "coder", "reviewer").</param>
    /// <returns>The premium model identifier for the role, or <c>null</c> if not configured.</returns>
    public string? GetPremiumModelForRole(string roleName) =>
        Workers.TryGetValue(roleName.ToLowerInvariant(), out var wc) && !string.IsNullOrEmpty(wc.PremiumModel)
            ? wc.PremiumModel
            : null;
}

/// <summary>
/// Configuration for a single source repository.
/// </summary>
public sealed class RepositoryConfig
{
    /// <summary>Short name used to identify the repository within the hive.</summary>
    public required string Name { get; set; }
    /// <summary>Remote clone URL of the repository.</summary>
    public required string Url { get; set; }
    /// <summary>Default branch to use (e.g. "main").</summary>
    public string DefaultBranch { get; set; } = "main";
}

/// <summary>
/// Per-role worker configuration.
/// </summary>
public sealed class WorkerConfig
{
    /// <summary>Model override for this worker role; <c>null</c> means use the global default.</summary>
    public string? Model { get; set; }

    /// <summary>Premium model override for this worker role, selected when the Brain requests the 'premium' tier.</summary>
    public string? PremiumModel { get; set; }
}

/// <summary>
/// Orchestrator-level configuration from the config file.
/// </summary>
public sealed class OrchestratorConfig
{
    /// <summary>Model used by the orchestrator LLM.</summary>
    public string Model { get; set; } = Constants.DefaultWorkerModel;
    /// <summary>Maximum number of goal iterations before giving up.</summary>
    public int MaxIterations { get; set; } = Constants.DefaultMaxIterations;
    /// <summary>Maximum number of retries per individual task.</summary>
    public int MaxRetriesPerTask { get; set; } = Constants.DefaultMaxRetriesPerTask;
    /// <summary>When <c>true</c>, the improver runs after every iteration even on success.</summary>
    public bool AlwaysImprove { get; set; }
    /// <summary>When <c>true</c>, enables verbose logging of prompts, worker output, and Brain reasoning.</summary>
    public bool VerboseLogging { get; set; }
}
