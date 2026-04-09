namespace CopilotHive.Dashboard;

/// <summary>Orchestrator system info for the dashboard.</summary>
public sealed class OrchestratorInfo
{
    /// <summary>Server uptime.</summary>
    public TimeSpan Uptime { get; init; }
    /// <summary>CopilotHive assembly version.</summary>
    public string Version { get; init; } = "";
    /// <summary>SharpCoder assembly version.</summary>
    public string? SharpCoderVersion { get; init; }
    /// <summary>Current UTC server time.</summary>
    public DateTime ServerTime { get; init; }
    /// <summary>Model configured for the Brain.</summary>
    public string BrainModel { get; init; } = "";
    /// <summary>Model configured for the Composer.</summary>
    public string ComposerModel { get; init; } = "";
    /// <summary>Model configured per worker role.</summary>
    public Dictionary<string, string> RoleModels { get; init; } = [];
    /// <summary>Model configured for context compaction summaries, or null if using the main model.</summary>
    public string? CompactionModel { get; init; }
}
