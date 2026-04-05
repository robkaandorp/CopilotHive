using CopilotHive.Services;

namespace CopilotHive.Dashboard;

/// <summary>Detail for a single phase within an iteration.</summary>
public sealed class PhaseViewInfo
{
    /// <summary>Display name of the phase (e.g. "Coding", "Testing").</summary>
    public string Name { get; init; } = "";
    /// <summary>Worker role name (e.g. "coder", "tester"), empty for non-worker phases.</summary>
    public string RoleName { get; init; } = "";
    /// <summary>Phase status: "completed", "failed", "active", "pending", or "skipped".</summary>
    public string Status { get; init; } = "";
    /// <summary>Overall verdict for this phase.</summary>
    public string? Verdict { get; init; }
    /// <summary>Wall-clock duration in seconds, if available.</summary>
    public double? DurationSeconds { get; init; }
    /// <summary>Worker output for the phase, preferring persisted PhaseResult.WorkerOutput when available, falling back to pipeline PhaseOutputs.</summary>
    public string? WorkerOutput { get; init; }
    /// <summary>Total tests discovered (Testing phase only).</summary>
    public int TotalTests { get; init; }
    /// <summary>Tests that passed (Testing phase only).</summary>
    public int PassedTests { get; init; }
    /// <summary>Tests that failed (Testing phase only).</summary>
    public int FailedTests { get; init; }
    /// <summary>Code coverage percentage (Testing phase only).</summary>
    public double CoveragePercent { get; init; }
    /// <summary>Whether the build succeeded (Testing phase only).</summary>
    public bool BuildSuccess { get; init; }
    /// <summary>Review verdict string (Review phase only).</summary>
    public string? ReviewVerdict { get; init; }
    /// <summary>Number of issues found during review (Review phase only).</summary>
    public int ReviewIssuesFound { get; init; }
    /// <summary>List of issues found.</summary>
    public List<string> Issues { get; init; } = [];
    /// <summary>Progress reports during this phase.</summary>
    public List<ProgressEntry> ProgressReports { get; init; } = [];
    /// <summary>Brain prompt (user message) sent when crafting the worker prompt for this phase, or null if not available.</summary>
    public string? BrainPrompt { get; init; }
    /// <summary>Crafted worker prompt (Brain assistant message) for this phase, or null if not available.</summary>
    public string? WorkerPrompt { get; init; }
    /// <summary>Clarification Q&amp;As that occurred during this phase (matched by iteration and phase name).</summary>
    public List<ClarificationEntry> Clarifications { get; init; } = [];
    /// <summary>1-based occurrence index when the same phase appears multiple times in a multi-round plan.</summary>
    public int Occurrence { get; init; } = 1;
}
