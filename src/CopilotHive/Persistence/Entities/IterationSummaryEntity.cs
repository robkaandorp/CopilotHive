namespace CopilotHive.Persistence.Entities;

/// <summary>
/// EF Core entity mapping for the goal_iterations table.
/// Stored as a plain POCO; domain logic uses <see cref="Goals.IterationSummary"/>.
/// </summary>
public sealed class IterationSummaryEntity
{
    /// <summary>Primary key — autoincrement integer.</summary>
    public int Id { get; set; }

    /// <summary>Foreign key to goals(id).</summary>
    public string GoalId { get; set; } = string.Empty;

    /// <summary>One-based iteration number.</summary>
    public int Iteration { get; set; }

    /// <summary>JSON-serialised per-phase results.</summary>
    public string? PhasesJson { get; set; }

    /// <summary>Total test count, or null if no tests were run.</summary>
    public int? TestTotal { get; set; }

    /// <summary>Passed test count, or null if no tests were run.</summary>
    public int? TestPassed { get; set; }

    /// <summary>Failed test count, or null if no tests were run.</summary>
    public int? TestFailed { get; set; }

    /// <summary>Review verdict string, or null if no review was run.</summary>
    public string? ReviewVerdict { get; set; }

    /// <summary>JSON-serialised iteration notes.</summary>
    public string? NotesJson { get; set; }

    /// <summary>JSON-serialised raw phase outputs.</summary>
    public string? PhaseOutputsJson { get; set; }

    /// <summary>JSON-serialised clarification Q&amp;As.</summary>
    public string? ClarificationsJson { get; set; }

    /// <summary>Whether the build succeeded during the iteration.</summary>
    public bool BuildSuccess { get; set; }

    /// <summary>UTC creation timestamp (ISO 8601 string).</summary>
    public string CreatedAt { get; set; } = string.Empty;
}
