namespace CopilotHive.Dashboard;

/// <summary>Detail for a single iteration in the goal timeline.</summary>
public sealed class IterationViewInfo
{
    /// <summary>One-based iteration number.</summary>
    public int Number { get; init; }
    /// <summary>Phases executed in this iteration.</summary>
    public List<PhaseViewInfo> Phases { get; init; } = [];
    /// <summary>Whether this is the currently executing iteration.</summary>
    public bool IsCurrent { get; init; }
    /// <summary>Brain's reasoning for the iteration plan, or null if not yet planned.</summary>
    public string? PlanReason { get; init; }
    /// <summary>Brain prompt (user message) sent during the planning phase, or null if not available.</summary>
    public string? PlanningBrainPrompt { get; init; }
    /// <summary>Brain response (assistant message) from the planning phase, or null if not available.</summary>
    public string? PlanningBrainResponse { get; init; }
}
