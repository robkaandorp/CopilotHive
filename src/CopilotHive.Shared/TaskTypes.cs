using CopilotHive.Goals;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Domain representation of a task assignment. Replaces gRPC TaskAssignment in business logic.
/// </summary>
public sealed record WorkTask
{
    /// <summary>Unique identifier for this task.</summary>
    public required string TaskId { get; init; }
    /// <summary>Identifier of the goal this task belongs to.</summary>
    public required string GoalId { get; init; }
    /// <summary>Human-readable description of the goal.</summary>
    public required string GoalDescription { get; init; }
    /// <summary>The prompt to send to the worker.</summary>
    public required string Prompt { get; init; }
    /// <summary>Worker role that will execute the task.</summary>
    public required WorkerRole Role { get; init; }
    /// <summary>Optional model ID for this task (e.g., "claude-sonnet-4.6").</summary>
    public string Model { get; init; } = "";
    /// <summary>Branch information for git operations, or <c>null</c> if not applicable.</summary>
    public BranchSpec? BranchInfo { get; set; }
    /// <summary>Repositories the worker should operate on.</summary>
    public required List<TargetRepository> Repositories { get; init; }
    /// <summary>Current iteration number.</summary>
    public int Iteration { get; init; }
    /// <summary>Additional key-value metadata for the task.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Domain representation of task completion. Replaces gRPC TaskComplete in business logic.
/// </summary>
public sealed record TaskResult
{
    /// <summary>Identifier of the completed task.</summary>
    public required string TaskId { get; init; }
    /// <summary>Outcome status of the task.</summary>
    public required TaskOutcome Status { get; init; }
    /// <summary>Worker output text.</summary>
    public string Output { get; init; } = "";
    /// <summary>Structured metrics from the task execution.</summary>
    public TaskMetrics? Metrics { get; init; }
    /// <summary>Git diff statistics from the task execution.</summary>
    public GitChangeSummary? GitStatus { get; init; }
}

/// <summary>Domain-level task completion status.</summary>
public enum TaskOutcome
{
    /// <summary>Task completed successfully.</summary>
    Completed,
    /// <summary>Task failed.</summary>
    Failed,
    /// <summary>Task was cancelled.</summary>
    Cancelled,
}

/// <summary>
/// Domain representation of task metrics. Replaces gRPC TaskMetrics in business logic.
/// </summary>
public sealed record TaskMetrics
{
    /// <summary>Overall verdict string (e.g. "PASS", "FAIL", "APPROVE").</summary>
    public string Verdict { get; init; } = "PASS";
    /// <summary>Whether the build succeeded.</summary>
    public bool BuildSuccess { get; init; }
    /// <summary>Total number of tests executed.</summary>
    public int TotalTests { get; init; }
    /// <summary>Number of tests that passed.</summary>
    public int PassedTests { get; init; }
    /// <summary>Number of tests that failed.</summary>
    public int FailedTests { get; init; }
    /// <summary>Code coverage percentage.</summary>
    public double CoveragePercent { get; init; }
    /// <summary>List of issue descriptions.</summary>
    public List<string> Issues { get; init; } = [];
}

/// <summary>
/// Domain representation of git diff status. Replaces gRPC GitStatus in business logic.
/// </summary>
public sealed record GitChangeSummary
{
    /// <summary>Number of files changed.</summary>
    public int FilesChanged { get; init; }
    /// <summary>Total lines inserted.</summary>
    public int Insertions { get; init; }
    /// <summary>Total lines deleted.</summary>
    public int Deletions { get; init; }
    /// <summary>Whether the changes were pushed to the remote.</summary>
    public bool Pushed { get; init; }
}

/// <summary>Domain-level branch action.</summary>
public enum BranchAction
{
    /// <summary>No branch action specified.</summary>
    Unspecified,
    /// <summary>Create a new feature branch.</summary>
    Create,
    /// <summary>Check out an existing branch.</summary>
    Checkout,
    /// <summary>Merge branches.</summary>
    Merge,
}

/// <summary>
/// Domain representation of branch information. Replaces gRPC BranchInfo in business logic.
/// </summary>
public sealed record BranchSpec
{
    /// <summary>The base branch to create from or merge into.</summary>
    public required string BaseBranch { get; init; }
    /// <summary>The feature branch name.</summary>
    public required string FeatureBranch { get; init; }
    /// <summary>The branch action to perform.</summary>
    public required BranchAction Action { get; set; }
}
