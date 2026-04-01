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
    /// <summary>Composer agent configuration. When set, the Composer is enabled.</summary>
    public ComposerConfig? Composer { get; set; }

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
    /// <summary>
    /// Maximum number of goals to execute in parallel. Default: 1 (sequential).
    /// Set to a value &gt; 1 to enable concurrent goal execution. When multiple goals
    /// run in parallel, each has its own Brain session forked from the master.
    /// </summary>
    public int MaxParallelGoals { get; set; } = 1;
    /// <summary>When <c>true</c>, the improver runs after every iteration even on success.</summary>
    public bool AlwaysImprove { get; set; }
    /// <summary>When <c>true</c>, enables verbose logging of prompts, worker output, and Brain reasoning.</summary>
    public bool VerboseLogging { get; set; }
    /// <summary>Maximum context window size in tokens for the Brain model. Used for compaction decisions.</summary>
    public int BrainContextWindow { get; set; } = Constants.DefaultBrainContextWindow;
    /// <summary>Maximum tool-call steps the Brain agent may take per request.</summary>
    public int BrainMaxSteps { get; set; } = Constants.DefaultBrainMaxSteps;
}

/// <summary>
/// Configuration for the Composer conversational agent.
/// </summary>
public sealed class ComposerConfig
{
    /// <summary>Model used by the Composer (e.g. "copilot/claude-sonnet-4.6"). Falls back to orchestrator model if empty.</summary>
    public string? Model { get; set; }
    /// <summary>Additional models available for switching at runtime. The <see cref="Model"/> entry is always first.</summary>
    public List<string>? Models { get; set; }
    /// <summary>Maximum context window size in tokens.</summary>
    public int ContextWindow { get; set; } = Constants.DefaultBrainContextWindow;
    /// <summary>Maximum tool-call steps per Composer request.</summary>
    public int MaxSteps { get; set; } = Constants.DefaultBrainMaxSteps;

    /// <summary>
    /// Returns a merged, deduplicated list of available models, with <see cref="Model"/> as the first entry.
    /// Falls back to <paramref name="fallback"/> if neither <see cref="Model"/> nor <see cref="Models"/> is set.
    /// </summary>
    /// <param name="fallback">Model to return when neither <see cref="Model"/> nor <see cref="Models"/> is configured.</param>
    /// <returns>A non-empty list of model identifiers.</returns>
    public List<string> GetAvailableModels(string fallback)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        if (!string.IsNullOrEmpty(Model))
        {
            result.Add(Model);
            seen.Add(Model);
        }

        if (Models is not null)
        {
            foreach (var m in Models)
            {
                if (!string.IsNullOrEmpty(m) && seen.Add(m))
                    result.Add(m);
            }
        }

        if (result.Count == 0)
        {
            result.Add(fallback);
        }

        return result;
    }
}
