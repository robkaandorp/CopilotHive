namespace CopilotHive.Configuration;

/// <summary>
/// Optional release automation configuration for a repository.
/// </summary>
public sealed class ReleaseRepoConfig
{
    /// <summary>Branch to merge the feature branch into (e.g. "main").</summary>
    public string? MergeTo { get; set; }

    /// <summary>Branch to tag releases from (e.g. "main").</summary>
    public string? TagBranch { get; set; }
}
