namespace CopilotHive.Goals;

public sealed class Goal
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public GoalPriority Priority { get; init; } = GoalPriority.Normal;
    public GoalStatus Status { get; set; } = GoalStatus.Pending;
    public List<string> RepositoryNames { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public enum GoalPriority { Low, Normal, High, Critical }

public enum GoalStatus { Pending, InProgress, Completed, Failed, Cancelled }
