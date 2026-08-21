using YamlDotNet.Serialization;

namespace CopilotHive.Configuration;

/// <summary>
/// A single NuGet package watched for publish events.
/// </summary>
public sealed class NuGetPackageEntry
{
    /// <summary>NuGet package ID (e.g. "My.Library").</summary>
    [YamlMember(Alias = "package_id")]
    public string PackageId { get; set; } = "";
}

/// <summary>
/// NuGet publish monitoring configuration for a repository.
/// </summary>
public sealed class NuGetPublishConfig
{
    /// <summary>Packages to watch for publish events.</summary>
    public List<NuGetPackageEntry> Packages { get; set; } = new();
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
    /// <summary>Whether CI monitoring is enabled for this repository.</summary>
    public bool MonitorCi { get; set; } = false;
    /// <summary>Timeout in minutes before a CI run is considered failed.</summary>
    public int CiTimeoutMinutes { get; set; } = 30;

    /// <summary>Optional release automation configuration for this repository.</summary>
    public ReleaseRepoConfig? Release { get; set; }

    /// <summary>Optional NuGet publish monitoring configuration for this repository.</summary>
    [YamlMember(Alias = "publish_nuget")]
    public NuGetPublishConfig? PublishNuGet { get; set; }
}
