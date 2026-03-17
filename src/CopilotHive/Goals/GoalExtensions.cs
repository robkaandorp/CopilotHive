using System;

namespace CopilotHive.Goals;

/// <summary>Extension methods for <see cref="GoalPriority"/> and <see cref="GoalStatus"/>.</summary>
public static class GoalExtensions
{
    /// <summary>Returns a human-readable display name for the priority.</summary>
    /// <param name="priority">The <see cref="GoalPriority"/> value.</param>
    /// <returns>A display name string for the given priority.</returns>
    public static string ToDisplayName(this GoalPriority priority) => priority switch
    {
        GoalPriority.Critical => "Critical",
        GoalPriority.High => "High",
        GoalPriority.Normal => "Normal",
        GoalPriority.Low => "Low",
        _ => throw new InvalidOperationException($"Unknown GoalPriority: {priority}")
    };

    /// <summary>Returns a human-readable display name for the status.</summary>
    /// <param name="status">The <see cref="GoalStatus"/> value.</param>
    /// <returns>A display name string for the given status.</returns>
    public static string ToDisplayName(this GoalStatus status) => status switch
    {
        GoalStatus.Pending => "Pending",
        GoalStatus.InProgress => "In Progress",
        GoalStatus.Completed => "Completed",
        GoalStatus.Failed => "Failed",
        GoalStatus.Cancelled => "Cancelled",
        _ => throw new InvalidOperationException($"Unknown GoalStatus: {status}")
    };
}
