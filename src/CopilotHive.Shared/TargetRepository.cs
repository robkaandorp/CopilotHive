namespace CopilotHive.Goals;

/// <summary>
/// Describes a single repository targeted by a goal.
/// </summary>
public sealed class TargetRepository
{
    /// <summary>Short name used to identify the repository.</summary>
    public required string Name { get; init; }
    /// <summary>Remote clone URL of the repository.</summary>
    public required string Url { get; init; }
    /// <summary>Default branch of the repository (e.g. "main").</summary>
    public string DefaultBranch { get; init; } = "main";
    /// <summary>Optional per-repository instructions appended to the worker prompt.</summary>
    public string? SpecificInstructions { get; init; }
}
