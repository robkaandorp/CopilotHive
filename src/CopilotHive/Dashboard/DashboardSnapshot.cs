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
    /// <summary>
    /// Workers NOT currently executing tasks. This INCLUDES workers withheld from selection (awaiting
    /// their own accepted Ready, or still publishing a completion), so it is NOT a count of
    /// assignable capacity — see <see cref="AvailableWorkers"/> for that.
    /// </summary>
    public int IdleWorkers { get; init; }
    /// <summary>
    /// Workers available at the captured instant: not busy, carrying no task, holding no completion
    /// publication and not awaiting their own accepted Ready. Defaults to <c>0</c>.
    /// </summary>
    /// <remarks>An OBSERVATION, NOT A RESERVATION AND NOT A DELIVERY GUARANTEE.</remarks>
    public int AvailableWorkers { get; init; }
    /// <summary>
    /// Workers awaiting their own accepted Ready at the captured instant — withheld from selection
    /// and unavailable for assignment. Defaults to <c>0</c>.
    /// </summary>
    /// <remarks>An OBSERVATION ONLY, carrying no reservation and no delivery guarantee.</remarks>
    public int AwaitingReadyWorkers { get; init; }
}
