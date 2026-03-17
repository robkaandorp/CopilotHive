using CopilotHive.Models;

namespace CopilotHive.Services;

/// <summary>
/// Computes worker utilization metrics from the current state of the <see cref="WorkerPool"/>.
/// </summary>
public sealed class WorkerUtilizationService
{
    private readonly WorkerPool _workerPool;

    /// <summary>
    /// Initialises the service with the required <see cref="WorkerPool"/> dependency.
    /// </summary>
    /// <param name="workerPool">The pool of connected workers to analyse.</param>
    public WorkerUtilizationService(WorkerPool workerPool)
    {
        _workerPool = workerPool;
    }

    /// <summary>
    /// Returns a <see cref="WorkerUtilizationMetrics"/> snapshot computed from the current worker pool state.
    /// </summary>
    /// <returns>
    /// A <see cref="WorkerUtilizationMetrics"/> instance containing overall utilization,
    /// per-role utilization fractions, and any roles whose utilization exceeds 0.8.
    /// </returns>
    public WorkerUtilizationMetrics GetUtilization()
    {
        var workers = _workerPool.GetAllWorkers();
        var totalWorkers = workers.Count;
        var busyWorkers = workers.Count(w => w.IsBusy);

        var overallUtilization = totalWorkers == 0 ? 0.0 : (double)busyWorkers / totalWorkers;

        var roleGroups = workers.GroupBy(w => w.Role.ToString());
        var roleBreakdown = new Dictionary<string, double>();
        var bottleneckRoles = new List<string>();

        foreach (var group in roleGroups)
        {
            var totalInRole = group.Count();
            var busyInRole = group.Count(w => w.IsBusy);
            var roleUtilization = (double)busyInRole / totalInRole;

            roleBreakdown[group.Key] = roleUtilization;

            if (roleUtilization > 0.8)
                bottleneckRoles.Add(group.Key);
        }

        return new WorkerUtilizationMetrics
        {
            OverallUtilization = overallUtilization,
            RoleBreakdown = roleBreakdown,
            BottleneckRoles = bottleneckRoles,
        };
    }
}
