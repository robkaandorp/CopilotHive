using CopilotHive.Models;

namespace CopilotHive.Services;

/// <summary>
/// Computes worker utilization metrics from ONE detached capture of the <see cref="WorkerPool"/>.
/// </summary>
/// <remarks>
/// <see cref="WorkerPool.CaptureWorkerStatus"/> is called exactly once per result, so the overall
/// counts, every per-role fraction and the bottleneck list all describe the SAME captured instant
/// instead of mixing facts read at different times. The capture is a DETACHED copy of pool-owned
/// values, and all grouping and arithmetic happens after it, outside any pool lock.
/// </remarks>
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
    /// Returns a <see cref="WorkerUtilizationMetrics"/> snapshot computed from ONE capture of the
    /// worker pool state.
    /// </summary>
    /// <returns>
    /// A <see cref="WorkerUtilizationMetrics"/> instance containing overall utilization,
    /// per-role utilization fractions, and any roles whose utilization exceeds 0.8.
    /// </returns>
    public WorkerUtilizationMetrics GetUtilization()
    {
        // THE ONE CAPTURE: every value below is derived from this single detached local — no second
        // pool read, so no two figures of one result can describe different instants.
        var captured = _workerPool.CaptureWorkerStatus();

        var totalWorkers = captured.Count;
        var busyWorkers = captured.Count(w => w.IsBusy);

        var overallUtilization = totalWorkers == 0 ? 0.0 : (double)busyWorkers / totalWorkers;

        var roleGroups = captured.GroupBy(w => w.Role.ToString());
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
