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
    /// <summary>Pre-execution review status of the goal.</summary>
    public ReviewStatus ReviewStatus { get; init; }
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
    /// <summary>IDs of knowledge documents linked to this goal.</summary>
    public List<string> Documents { get; init; } = [];
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
    /// <summary>Reason the goal failed, if applicable.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Whether the resume action would be ACCEPTED for this goal right now — i.e. whether
    /// <see cref="GoalDispatcher.ResumeGoalAsync"/> would return <c>true</c> for it.
    /// <para>
    /// Computed by <see cref="GoalResumeEligibility.IsResumable"/> over the goal and the goal's
    /// pipeline phase/coder branch, resolved IN MEMORY OR FROM THE STORE
    /// (<see cref="GoalPipelineManager.GetPhaseAndCoderBranch"/>). It is therefore still correct
    /// after an orchestrator restart, when a Failed pipeline exists only in the database.
    /// </para>
    /// <para>
    /// <c>false</c> is the default, so a detail built without the flag (and every legacy caller)
    /// renders without the resume button. The Goal Detail page shows the button iff this is
    /// <c>true</c>: the page and the backend read ONE rule and cannot disagree.
    /// </para>
    /// </summary>
    public bool CanResume { get; init; }
    /// <summary>
    /// The goal's FULL token-usage breakdown — worker phases, stored Brain usage and stored
    /// pre-execution review usage, plus their grand total — or <c>null</c> when the goal has no usage
    /// to show at all (no worker phase reported AND both stored summaries are absent).
    /// <para>
    /// <c>null</c> means "render no Token Usage card at all", so a legacy goal without usage renders
    /// exactly as it did before. A goal with ONLY Brain or review usage yields a non-null breakdown
    /// even though no worker phase has run yet, so the card appears for a goal that was reviewed or
    /// planned but has not executed.
    /// </para>
    /// <para>
    /// INTERNAL because <see cref="GoalUsageBreakdown"/> itself is internal — a public property would
    /// be a CS0053 error. The dashboard page and <see cref="GoalDetailViewBuilder"/> live in the same
    /// assembly, so both read it directly.
    /// </para>
    /// </summary>
    internal GoalUsageBreakdown? GoalUsage { get; init; }

    /// <summary>
    /// The summed token usage of the goal's WORKER phases — the union of exactly the phase records
    /// each <see cref="IterationViewInfo"/> was built from, with every iteration counted exactly once
    /// — or <c>null</c> when no worker phase reported usage.
    /// <para>
    /// INTERNAL because <see cref="PhaseUsageTotals.Result"/> is internal — a public property would
    /// be a CS0053 error. The dashboard page and <see cref="GoalDetailViewBuilder"/> live in the same
    /// assembly, so both read it directly.
    /// </para>
    /// <para>
    /// <c>null</c> means "the goal's worker phases reported nothing", so a legacy goal without usage
    /// renders exactly as it did before. An EMPTY (non-null) summary from a worker phase is a real
    /// zero-call measurement and therefore counts as reported, exactly like
    /// <see cref="PhaseUsageTotals.Sum"/> counts it. The goal detail page no longer renders from this
    /// property (it renders the card from <see cref="GoalUsage"/> instead); it is kept for the
    /// worker-phase figure on its own.
    /// </para>
    /// </summary>
    internal PhaseUsageTotals.Result? UsageTotals { get; init; }

    /// <summary>
    /// Whether <see cref="GoalUsage"/> and <see cref="UsageTotals"/> include the RUNNING phase's LIVE
    /// usage — the running task's usage resolved from a worker heartbeat and attributed to the live
    /// iteration's running entry.
    /// <para>
    /// It is <c>true</c> only when the worker-phase total was summed over a live iteration that
    /// included such a substitute; a goal without a running phase (or whose running task's usage was
    /// not attributed) is <c>false</c> and its worker figures are persisted-data-only. Renderers
    /// append "(includes running phase)" to the worker-derived lines when it is <c>true</c>.
    /// </para>
    /// <para>
    /// INTERNAL like <see cref="UsageTotals"/>: the razor page and the builder live in the same
    /// assembly, and the flag describes an internal total.
    /// </para>
    /// </summary>
    internal bool IncludesLiveUsage { get; init; }
}
