namespace CopilotHive.Dashboard;

/// <summary>Worker state for the dashboard.</summary>
public sealed class WorkerInfo
{
    /// <summary>Worker identifier.</summary>
    public string Id { get; init; } = "";
    /// <summary>Current role name.</summary>
    public string Role { get; init; } = "";
    /// <summary>Whether the worker is busy.</summary>
    public bool IsBusy { get; init; }
    /// <summary>Current task ID, if any.</summary>
    public string? CurrentTaskId { get; init; }
    /// <summary>Last heartbeat timestamp.</summary>
    public DateTime LastHeartbeat { get; init; }
    /// <summary>Connection timestamp.</summary>
    public DateTime ConnectedAt { get; init; }
    /// <summary>Model used for the current task, or <c>null</c> when idle.</summary>
    public string? CurrentModel { get; init; }
    /// <summary>Estimated context window usage as a percentage (0–100), or 0 when idle.</summary>
    public int ContextUsagePercent { get; init; }

    /// <summary>
    /// Returns the model string to display for this worker: the task-specific
    /// <see cref="CurrentModel"/> if set, otherwise the role-default from
    /// <paramref name="roleModels"/>, or <c>null</c> if neither is available.
    /// </summary>
    /// <param name="roleModels">Role-to-model mapping from <see cref="OrchestratorInfo.RoleModels"/>.</param>
    /// <returns>The display model string, or <c>null</c>.</returns>
    public string? GetDisplayModel(IDictionary<string, string> roleModels)
    {
        return CurrentModel
            ?? (Role != "Unspecified" && roleModels.TryGetValue(Role.ToLowerInvariant(), out var m) ? m : null);
    }
}
