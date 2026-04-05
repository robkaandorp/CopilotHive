using CopilotHive.Goals;

namespace CopilotHive.Dashboard;

/// <summary>Snapshot of all dashboard state at a point in time.</summary>
public sealed class DashboardSnapshot
{
    /// <summary>All known goals.</summary>
    public List<Goal> Goals { get; init; } = [];
    /// <summary>Connected workers.</summary>
    public List<WorkerInfo> Workers { get; init; } = [];
    /// <summary>Active goal pipelines.</summary>
    public List<PipelineInfo> Pipelines { get; init; } = [];
    /// <summary>Count of draft goals.</summary>
    public int DraftGoals { get; init; }
    /// <summary>Count of pending goals.</summary>
    public int PendingGoals { get; init; }
    /// <summary>Count of in-progress goals.</summary>
    public int ActiveGoals { get; init; }
    /// <summary>Count of completed goals.</summary>
    public int CompletedGoals { get; init; }
    /// <summary>Count of failed goals.</summary>
    public int FailedGoals { get; init; }
    /// <summary>Total connected workers.</summary>
    public int TotalWorkers { get; init; }
    /// <summary>Workers currently executing tasks.</summary>
    public int BusyWorkers { get; init; }
    /// <summary>Workers waiting for work.</summary>
    public int IdleWorkers { get; init; }
}
