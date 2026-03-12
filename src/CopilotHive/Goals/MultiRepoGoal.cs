namespace CopilotHive.Goals;

public sealed class MultiRepoGoal
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required List<TargetRepository> Repositories { get; init; }
    public string? BranchPrefix { get; init; }
    public GoalPriority Priority { get; init; } = GoalPriority.Normal;
    public GoalStatus Status { get; set; } = GoalStatus.Pending;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class TargetRepository
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string DefaultBranch { get; init; } = "main";
    public string? SpecificInstructions { get; init; }
}
