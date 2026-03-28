namespace CopilotHive.Goals;

/// <summary>
/// Lifecycle status values for a release.
/// </summary>
public enum ReleaseStatus
{
    /// <summary>Release is being planned; goals can be assigned to it.</summary>
    Planning,
    /// <summary>Release has been published.</summary>
    Released,
}

/// <summary>
/// Represents a named release that groups completed goals together.
/// </summary>
public sealed class Release
{
    /// <summary>Unique identifier for this release (e.g. "v1.2.0").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable tag or version label (e.g. "v1.2.0").</summary>
    public required string Tag { get; init; }

    /// <summary>Current lifecycle status of the release.</summary>
    public ReleaseStatus Status { get; set; } = ReleaseStatus.Planning;

    /// <summary>Optional notes or changelog summary for this release.</summary>
    public string? Notes { get; set; }

    /// <summary>UTC timestamp when the release was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when the release was published, or <c>null</c> if not yet released.</summary>
    public DateTime? ReleasedAt { get; set; }

    /// <summary>Names of repositories this release applies to.</summary>
    public List<string> RepositoryNames { get; init; } = [];
}
