namespace CopilotHive.Persistence.Entities;

/// <summary>
/// EF Core entity mapping for the task_mappings table.
/// Maps a worker task id to its owning goal id for restart recovery.
/// </summary>
public sealed class TaskMappingEntity
{
    /// <summary>Primary key — the worker task id.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>The goal id this task belongs to.</summary>
    public string GoalId { get; set; } = string.Empty;
}
