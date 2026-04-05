namespace CopilotHive.Dashboard;

/// <summary>Pipeline state for the dashboard.</summary>
public sealed class PipelineInfo
{
    /// <summary>Goal identifier.</summary>
    public string GoalId { get; init; } = "";
    /// <summary>Goal description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Current phase name.</summary>
    public string Phase { get; init; } = "";
    /// <summary>Ordered phase names from the iteration plan.</summary>
    public List<string> Phases { get; init; } = [];
    /// <summary>Current iteration number.</summary>
    public int Iteration { get; init; }
    /// <summary>Active task ID, if any.</summary>
    public string? ActiveTaskId { get; init; }
    /// <summary>Pipeline creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>When the goal started executing (first non-planning phase).</summary>
    public DateTime? GoalStartedAt { get; init; }
    /// <summary>When the pipeline completed or failed, if finished.</summary>
    public DateTime? CompletedAt { get; init; }
    /// <summary>Total test count.</summary>
    public int TotalTests { get; init; }
    /// <summary>Passed test count.</summary>
    public int PassedTests { get; init; }
    /// <summary>Failed test count.</summary>
    public int FailedTests { get; init; }
    /// <summary>Code coverage percentage.</summary>
    public double CoveragePercent { get; init; }
}
