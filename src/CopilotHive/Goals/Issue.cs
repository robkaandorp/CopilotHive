namespace CopilotHive.Goals;

/// <summary>
/// A tracked issue (bug, suggestion, concern, etc.) discovered during goal execution
/// or reported by a user. Issues are persisted via <see cref="IIssueStore"/>.
/// </summary>
public sealed class Issue
{
    /// <summary>Unique caller-provided kebab-case identifier for this issue.</summary>
    public required string Id { get; init; }

    /// <summary>Category of the issue.</summary>
    public IssueType Type { get; set; } = IssueType.Suggestion;

    /// <summary>Short summary of the issue.</summary>
    public required string Title { get; set; }

    /// <summary>Detailed markdown description of the issue.</summary>
    public required string Description { get; set; }

    /// <summary>Severity of the issue.</summary>
    public IssueSeverity Severity { get; set; } = IssueSeverity.Low;

    /// <summary>Current lifecycle status of the issue.</summary>
    public IssueStatus Status { get; set; } = IssueStatus.Open;

    /// <summary>Names of repositories this issue applies to.</summary>
    public List<string> RepositoryNames { get; set; } = [];

    /// <summary>ID of the goal that produced this issue, or <c>null</c> if user-reported.</summary>
    public string? SourceGoalId { get; init; }

    /// <summary>Role that produced this issue (e.g. "reviewer", "tester"), or <c>null</c> if user-reported.</summary>
    public string? SourceRole { get; init; }

    /// <summary>Iteration number in which the issue was produced, or <c>null</c> if not applicable.</summary>
    public int? SourceIteration { get; init; }

    /// <summary>UTC timestamp when the issue was created. Set by the caller; the store does not override it.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>UTC timestamp of the last update, managed by the store.</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>UTC timestamp when the issue was resolved or closed, managed by the store.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>ID of a goal linked to this issue, or <c>null</c> if none.</summary>
    public string? LinkedGoalId { get; set; }
}

/// <summary>Category of an issue.</summary>
public enum IssueType
{
    /// <summary>Code quality concern (naming, structure, maintainability).</summary>
    CodeQuality,
    /// <summary>A defect or unexpected behaviour.</summary>
    Bug,
    /// <summary>An improvement suggestion.</summary>
    Suggestion,
    /// <summary>A concern or risk that needs attention.</summary>
    Concern,
    /// <summary>A workflow or process issue.</summary>
    Workflow,
}

/// <summary>Severity of an issue.</summary>
public enum IssueSeverity
{
    /// <summary>Minor issue with limited impact.</summary>
    Low,
    /// <summary>Moderate issue that should be addressed.</summary>
    Medium,
    /// <summary>Critical issue that blocks or significantly impacts work.</summary>
    High,
}

/// <summary>Lifecycle status values for an issue.</summary>
public enum IssueStatus
{
    /// <summary>Issue has been reported but not yet reviewed.</summary>
    Open,
    /// <summary>Issue has been triaged and prioritised.</summary>
    Triaged,
    /// <summary>Issue has been acknowledged by the team.</summary>
    Acknowledged,
    /// <summary>Work on the issue is in progress.</summary>
    InProgress,
    /// <summary>Issue has been resolved.</summary>
    Resolved,
    /// <summary>Issue has been closed without resolution (or after resolution).</summary>
    Closed,
}
