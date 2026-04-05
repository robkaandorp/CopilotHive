using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

namespace CopilotHive.Dashboard;

/// <summary>Rich detail info for the goal detail page.</summary>
public sealed class GoalDetailInfo
{
    /// <summary>Goal identifier.</summary>
    public string GoalId { get; init; } = "";
    /// <summary>Goal description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Effective goal status (derived from pipeline phase when active).</summary>
    public GoalStatus Status { get; init; }
    /// <summary>Goal priority level.</summary>
    public GoalPriority Priority { get; init; }
    /// <summary>Goal scope.</summary>
    public GoalScope Scope { get; init; }
    /// <summary>Current iteration number (zero if not started).</summary>
    public int CurrentIteration { get; init; }
    /// <summary>Name of the current pipeline phase.</summary>
    public string CurrentPhase { get; init; } = "";
    /// <summary>When the goal was created.</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>When the goal completed, if finished.</summary>
    public DateTime? CompletedAt { get; init; }
    /// <summary>Currently active task ID, if any.</summary>
    public string? ActiveTaskId { get; init; }
    /// <summary>Feature branch used by the coder.</summary>
    public string? CoderBranch { get; init; }
    /// <summary>Informational notes attached to the goal.</summary>
    public List<string> Notes { get; init; } = [];
    /// <summary>IDs of goals that must complete before this goal can be dispatched.</summary>
    public List<string> DependsOn { get; init; } = [];
    /// <summary>Per-iteration detail with phases.</summary>
    public List<IterationViewInfo> Iterations { get; init; } = [];
    /// <summary>Brain conversation log.</summary>
    public List<ConversationEntry> Conversation { get; init; } = [];
    /// <summary>SHA-1 hash of the merge commit that landed this goal's changes, or <c>null</c> if not yet merged.</summary>
    public string? MergeCommitHash { get; init; }
    /// <summary>URL of the primary repository for this goal (with .git suffix removed), or <c>null</c> if not resolved.</summary>
    public string? RepositoryUrl { get; init; }
    /// <summary>Repository names associated with this goal.</summary>
    public List<string> RepositoryNames { get; init; } = [];
}
