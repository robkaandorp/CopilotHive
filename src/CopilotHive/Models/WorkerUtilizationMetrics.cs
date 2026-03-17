namespace CopilotHive.Models;

/// <summary>Snapshot of worker utilization across the pool.</summary>
public sealed class WorkerUtilizationMetrics
{
    /// <summary>Fraction of all workers currently busy (0.0–1.0).</summary>
    public double OverallUtilization { get; set; }

    /// <summary>Per-role utilization fraction (0.0–1.0).</summary>
    public Dictionary<string, double> RoleBreakdown { get; set; } = new();

    /// <summary>Role names where utilization exceeds 0.8.</summary>
    public List<string> BottleneckRoles { get; set; } = new();
}
