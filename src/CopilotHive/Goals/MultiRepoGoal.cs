namespace CopilotHive.Goals;

/// <summary>
/// Represents a goal that spans multiple repositories, each potentially with specific instructions.
/// </summary>
public sealed class MultiRepoGoal
{
    /// <summary>Unique identifier for this goal.</summary>
    public required string Id { get; init; }
    /// <summary>Human-readable description of what the goal requires.</summary>
    public required string Description { get; init; }
    /// <summary>List of repositories targeted by this goal.</summary>
    public required List<TargetRepository> Repositories { get; init; }
    /// <summary>Optional prefix used when naming feature branches for this goal.</summary>
    public string? BranchPrefix { get; init; }
    /// <summary>Scheduling priority for this goal.</summary>
    public GoalPriority Priority { get; init; } = GoalPriority.Normal;
    /// <summary>Current lifecycle status of the goal.</summary>
    public GoalStatus Status { get; set; } = GoalStatus.Pending;
    /// <summary>UTC timestamp when the goal was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Describes a single repository targeted by a <see cref="MultiRepoGoal"/>.
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
