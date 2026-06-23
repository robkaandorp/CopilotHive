namespace CopilotHive.Persistence.Entities;

/// <summary>
/// EF Core entity mapping for the pipelines table.
/// Stored as a plain POCO; domain logic uses the pipeline domain model.
/// </summary>
public sealed class PipelineEntity
{
    /// <summary>Primary key — references the goal id.</summary>
    public string GoalId { get; set; } = string.Empty;

    /// <summary>Human-readable description of the pipeline.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>JSON-serialised goal instance.</summary>
    public string GoalJson { get; set; } = string.Empty;

    /// <summary>Current pipeline phase name.</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>One-based iteration number.</summary>
    public int Iteration { get; set; }

    /// <summary>Number of review retries performed.</summary>
    public int ReviewRetries { get; set; }

    /// <summary>Number of test retries performed.</summary>
    public int TestRetries { get; set; }

    /// <summary>Number of improver retries performed.</summary>
    public int ImproverRetries { get; set; }

    /// <summary>Maximum retries allowed per task.</summary>
    public int MaxRetries { get; set; }

    /// <summary>Maximum iterations allowed for the pipeline.</summary>
    public int MaxIterations { get; set; }

    /// <summary>Active worker task id, or null if no task is in flight.</summary>
    public string? ActiveTaskId { get; set; }

    /// <summary>Git branch used by the coder, or null.</summary>
    public string? CoderBranch { get; set; }

    /// <summary>JSON-serialised plan instructions, or null.</summary>
    public string? PlanJson { get; set; }

    /// <summary>JSON-serialised phase outputs.</summary>
    public string PhaseOutputs { get; set; } = string.Empty;

    /// <summary>JSON-serialised metrics.</summary>
    public string MetricsJson { get; set; } = string.Empty;

    /// <summary>UTC creation timestamp (ISO 8601 string).</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>UTC completion timestamp (ISO 8601 string), or null.</summary>
    public string? CompletedAt { get; set; }

    // ── Migrated columns added after the initial schema ─────────────────

    /// <summary>UTC timestamp when goal processing started, or null.</summary>
    public string? GoalStartedAt { get; set; }

    /// <summary>SHA-1 hash of the merge commit, or null.</summary>
    public string? MergeCommitHash { get; set; }

    /// <summary>JSON-serialised role sessions.</summary>
    public string RoleSessionsJson { get; set; } = string.Empty;

    /// <summary>Iteration start SHA, or null.</summary>
    public string? IterationStartSha { get; set; }

    /// <summary>1-based occurrence index of the current phase.</summary>
    public int PhaseOccurrence { get; set; }

    /// <summary>JSON-serialised phase log, or null.</summary>
    public string? PhaseLogJson { get; set; }
}
