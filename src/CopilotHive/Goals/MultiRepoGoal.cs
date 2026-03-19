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
