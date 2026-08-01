using CopilotHive.Goals;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Shared constants that need to be accessible across projects.
/// </summary>
internal static class SharedConstants
{
    /// <summary>The default context window size in tokens for the Brain model.</summary>
    public const int DefaultBrainContextWindow = 150_000;
}

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
    /// <summary>Session identifier in format "goalId:roleName", used for session persistence.</summary>
    public string SessionId { get; init; } = "";
    /// <summary>Branch information for git operations, or <c>null</c> if not applicable.</summary>
    public BranchSpec? BranchInfo { get; set; }
    /// <summary>Repositories the worker should operate on.</summary>
    public required List<TargetRepository> Repositories { get; init; }
    /// <summary>Current iteration number.</summary>
    public int Iteration { get; init; }
    /// <summary>Additional key-value metadata for the task.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
    /// <summary>Context window size in tokens for the worker's agent. Used for heartbeat Ctx% calculation and compaction threshold.</summary>
    public int MaxContextTokens { get; init; } = SharedConstants.DefaultBrainContextWindow;
    /// <summary>Model catalog for sub-agent delegation. Empty when sub-agents are disabled.</summary>
    public IReadOnlyList<SubAgentModelDto> SubAgentModels { get; init; } = [];
}

/// <summary>
/// Domain representation of a model available for sub-agent delegation.
/// Model IDs do NOT have reasoning-effort suffixes applied — sub-agents inherit the parent's reasoning.
/// </summary>
public sealed record SubAgentModelDto
{
    /// <summary>Model identifier (e.g. "copilot/claude-sonnet-4.6"). Never blank.</summary>
    public required string Id { get; init; }
    /// <summary>Context window in tokens, or null if unknown.</summary>
    public int? ContextWindow { get; init; }
    /// <summary>Human-readable description for the model.</summary>
    public string Description { get; init; } = "";
    /// <summary>Informational flag: whether the model accepts image input (vision). Defaults to false.</summary>
    public bool SupportsVision { get; init; }
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
    /// <summary>Optional model ID for the task that produced this result.</summary>
    public string Model { get; init; } = "";
    /// <summary>
    /// HEAD SHA of the worker's feature-branch clone captured immediately before the coder agent ran.
    /// Populated only for <see cref="WorkerRole.Coder"/> tasks; empty or null otherwise.
    /// Passed back so the orchestrator can store it on the pipeline for subsequent reviewer tasks.
    /// </summary>
    public string? IterationStartSha { get; init; }
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
    /// <summary>Human-readable summary from the worker's report tool call.</summary>
    public string Summary { get; init; } = "";
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
    /// <summary>Whether the changes were pushed to the remote OR — for a read-only role — no Class-B condition (a net HEAD move or capture-failure on a repo with a nonzero branch diff) was detected AND at least one repository had a usable baseline pair.</summary>
    public bool Pushed { get; init; }
    /// <summary>
    /// Repository-relative paths of the changed files. Never null. When more than one
    /// repository has changes, each path is qualified with its repository name
    /// (<c>repoName:relativePath</c>). Truncated to
    /// <c>GitOperations.ChangedFilesMaxPaths</c> entries; never contains synthetic
    /// truncation markers.
    /// </summary>
    public List<string> ChangedFiles { get; init; } = [];
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
