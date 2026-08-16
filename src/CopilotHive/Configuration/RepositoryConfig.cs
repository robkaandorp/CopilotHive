namespace CopilotHive.Configuration;

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
    /// <summary>Whether CI monitoring is enabled for this repository.</summary>
    public bool MonitorCi { get; set; } = false;
    /// <summary>Timeout in minutes before a CI run is considered failed.</summary>
    public int CiTimeoutMinutes { get; set; } = 30;

    /// <summary>Optional release automation configuration for this repository.</summary>
    public ReleaseRepoConfig? Release { get; set; }
}
