namespace CopilotHive.Goals;

/// <summary>
/// The goal-level token-usage breakdown a human-facing surface renders: the goal's worker-phase
/// totals, its stored Brain usage and its stored pre-execution review usage, plus the GRAND TOTAL of
/// all three. It is the single model both surfaces (the Composer's <c>get_goal</c> report and the
/// dashboard goal detail page) build, so the two agree on what "the goal's usage" is.
/// <para>
/// <b>Absent versus zero.</b> A missing report is <c>null</c>, never a zero total. A stored summary
/// with ZERO entries is treated exactly like <c>null</c>: the store never writes an empty summary
/// (an empty summary would be indistinguishable from "measured, zero model calls"), so it carries no
/// measurement to show. An EMPTY worker total is different — it comes from
/// <see cref="PhaseUsageTotals.Sum"/> and is counted as reported-with-zero through
/// <see cref="PhaseUsageTotals.Result.PhasesWithUsage"/>, exactly as the per-phase rule does.
/// </para>
/// <para>
/// <b>Merging.</b> <see cref="Total"/> merges the three sources with
/// <see cref="PhaseUsageTotals.Merge"/> (per <c>(source, model)</c>, ordinal model comparison), so
/// every bucket's counts sum — including <c>Calls</c>, both <c>*ReportedCalls</c> counters and the
/// estimated breakdown — and a category that was partial in one source stays detectable as partial in
/// the total. The inputs are never mutated: the merged summary is built on fresh accumulators.
/// </para>
/// </summary>
internal sealed class GoalUsageBreakdown
{
    /// <summary>
    /// The goal's worker-phase totals — the same result <see cref="PhaseUsageTotals.Sum"/> produces
    /// for the goal's phase records, so <see cref="PhaseUsageTotals.Result.PhasesWithoutUsage"/> is
    /// reported alongside the tokens rather than being silently treated as zero.
    /// </summary>
    public required PhaseUsageTotals.Result Workers { get; init; }

    /// <summary>
    /// The goal's stored Brain usage, or <c>null</c> when nothing was recorded — a legacy row, a goal
    /// whose Brain has not completed a call, or a stored summary with zero entries (treated as absent,
    /// never as a zero measurement).
    /// </summary>
    public required SharpCoder.UsageSummary? Brain { get; init; }

    /// <summary>
    /// The goal's stored pre-execution review usage, or <c>null</c> when nothing was recorded — a goal
    /// that was never reviewed, a review that has not completed a call, or a stored summary with zero
    /// entries (treated as absent, never as a zero measurement).
    /// </summary>
    public required SharpCoder.UsageSummary? Review { get; init; }

    /// <summary>
    /// The GRAND TOTAL: the per-<c>(source, model)</c> merge of <see cref="Workers"/>,
    /// <see cref="Brain"/> and <see cref="Review"/>, in that order. Detached from all three inputs.
    /// </summary>
    public required SharpCoder.UsageSummary Total { get; init; }

    /// <summary>
    /// Builds the breakdown for a goal, or <c>null</c> meaning "render nothing" when NO usage was
    /// reported anywhere: no worker phase reported (<c>workers.PhasesWithUsage == 0</c>) AND both
    /// Brain and review usage are absent.
    /// <para>
    /// A goal with only Brain or only review usage therefore yields a NON-null breakdown — the goal
    /// did consume tokens even though no worker phase has run yet (e.g. a goal still in Planning).
    /// </para>
    /// <para>
    /// Pure: the inputs are never mutated. <see cref="PhaseUsageTotals.Merge"/> sums onto fresh
    /// accumulators and <see cref="Workers"/> is carried through as the very result it was given.
    /// </para>
    /// </summary>
    /// <param name="workers">The goal's summed worker-phase totals; must not be <c>null</c>.</param>
    /// <param name="brain">The goal's stored Brain usage, or <c>null</c> when nothing was recorded.</param>
    /// <param name="review">The goal's stored review usage, or <c>null</c> when nothing was recorded.</param>
    /// <returns>The breakdown, or <c>null</c> when nothing at all was reported.</returns>
    internal static GoalUsageBreakdown? Create(
        PhaseUsageTotals.Result workers, SharpCoder.UsageSummary? brain, SharpCoder.UsageSummary? review)
    {
        ArgumentNullException.ThrowIfNull(workers);

        // A zero-entry summary is the same "nothing recorded" state as null: the store never writes
        // one, so nothing was measured and there is no number to show.
        var brainUsage = AbsentOrNull(brain);
        var reviewUsage = AbsentOrNull(review);

        if (workers.PhasesWithUsage == 0 && brainUsage is null && reviewUsage is null)
            return null;

        return new GoalUsageBreakdown
        {
            Workers = workers,
            Brain = brainUsage,
            Review = reviewUsage,
            // Merge skips nulls and empty summaries, and sums per (source, model) onto fresh
            // accumulators — so the total is detached and the three inputs stay untouched.
            Total = PhaseUsageTotals.Merge([workers.Usage, brainUsage, reviewUsage]),
        };
    }

    /// <summary>Normalises "nothing recorded": <c>null</c> stays <c>null</c>, and a zero-entry summary becomes <c>null</c>.</summary>
    private static SharpCoder.UsageSummary? AbsentOrNull(SharpCoder.UsageSummary? usage) =>
        usage is null || usage.Entries.Count == 0 ? null : usage;
}
