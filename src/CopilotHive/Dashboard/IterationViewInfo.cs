using CopilotHive.Goals;

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
    /// <summary>
    /// The summed token usage of exactly the phase records this iteration was built from, or
    /// <c>null</c> when NO worker phase in scope reported usage.
    /// <para>
    /// INTERNAL because <see cref="PhaseUsageTotals.Result"/> is internal — a public property would
    /// be a CS0053 error. The dashboard page and <see cref="GoalDetailViewBuilder"/> live in the same
    /// assembly, so both read it directly.
    /// </para>
    /// <para>
    /// <c>null</c> means "render no usage line at all", so legacy data and non-worker-only
    /// iterations render exactly as they did before usage reporting existed. A worker phase with an
    /// EMPTY (non-null) summary is a real zero-call measurement and therefore counts as reported,
    /// exactly like <see cref="PhaseUsageTotals.Sum"/> counts it — so it yields a non-null result.
    /// </para>
    /// </summary>
    internal PhaseUsageTotals.Result? UsageTotals { get; init; }

    /// <summary>
    /// Whether <see cref="UsageTotals"/> includes the RUNNING phase's LIVE usage — the running task's
    /// usage the dashboard resolved from a worker heartbeat — on top of the persisted per-phase
    /// reports.
    /// <para>
    /// It is <c>true</c> only for the live iteration whose running entry received that usage; every
    /// other iteration (summarised, stored, or a live one whose running task's usage was never
    /// attributed) is <c>false</c> and its total is persisted-data-only. Renderers append
    /// "(includes running phase)" when it is <c>true</c>.
    /// </para>
    /// <para>
    /// INTERNAL like <see cref="UsageTotals"/>: the razor page and the builder live in the same
    /// assembly, and the flag describes an internal total.
    /// </para>
    /// </summary>
    internal bool IncludesLiveUsage { get; init; }
}
