using CopilotHive.Services;

namespace CopilotHive.Goals;

/// <summary>
/// Derives token-usage totals from persisted <see cref="PhaseResult"/> records.
/// <para>
/// <b>Worker phases only.</b> Only phases that dispatched to a worker are counted — an entry whose
/// <see cref="GoalPhaseExtensions.ToRoleName(GoalPhase)"/> is non-empty. Planning, Merging, Done
/// and Failed entries are ignored entirely: no worker was billed for them, so they can neither
/// contribute tokens nor be reported as "missing".
/// </para>
/// <para>
/// <b>Merging.</b> Entries are merged by <c>(source, model)</c> with an ORDINAL model comparison, so
/// <c>"model-a"</c> and <c>"MODEL-A"</c> stay separate buckets, and a null model groups under null
/// only. The token counts are summed with <see cref="SharpCoder.TokenUsage.Add"/> onto a fresh
/// accumulator, which sums <c>Calls</c>, both <c>*ReportedCalls</c> counters and the estimated
/// breakdown — so a category that was partial in a phase stays detectable as partial in the total.
/// <see cref="SharpCoder.UsageSummary.Add(SharpCoder.UsageEvent)"/> is deliberately NOT used: it
/// records ONE model call per event and would count each summed phase as a single call.
/// </para>
/// <para>
/// <b>Not-reported versus zero.</b> A worker phase whose <see cref="PhaseResult.Usage"/> is
/// <c>null</c> adds to <see cref="Result.PhasesWithoutUsage"/> and contributes no tokens. The totals
/// never silently treat "not reported" as zero: the missing count is carried alongside the total
/// so callers can report it. An EMPTY (non-null) summary is a real measurement of zero calls and
/// contributes nothing while still counting as reported.
/// </para>
/// <para>
/// The input phase records are never mutated: every count is summed onto a new accumulator, and the
/// summary is built through <see cref="SharpCoder.UsageSummary(IEnumerable{SharpCoder.UsageEntry})"/>,
/// which deep-copies its entries.
/// </para>
/// </summary>
internal static class PhaseUsageTotals
{
    /// <summary>
    /// The summed usage of a phase list, together with how many worker phases reported usage and how
    /// many did not. <see cref="PhasesWithUsage"/> + <see cref="PhasesWithoutUsage"/> equals the
    /// number of WORKER phases in the input; non-worker phases appear in neither count.
    /// </summary>
    internal sealed class Result
    {
        /// <summary>
        /// The merged summary: one entry per distinct <c>(source, model)</c> pair seen in the input,
        /// each carrying the summed counts. Empty (with zero entries) when no worker phase reported
        /// usage — an empty summary, never <c>null</c>.
        /// </summary>
        public required SharpCoder.UsageSummary Usage { get; init; }

        /// <summary>Number of worker phases whose <see cref="PhaseResult.Usage"/> was non-null.</summary>
        public required int PhasesWithUsage { get; init; }

        /// <summary>
        /// Number of worker phases whose <see cref="PhaseResult.Usage"/> was <c>null</c> — usage was
        /// not reported for them (legacy data, a legacy worker or test double). Their tokens are
        /// absent from <see cref="Usage"/>, so this count must be reported alongside the total.
        /// </summary>
        public required int PhasesWithoutUsage { get; init; }
    }

    /// <summary>
    /// Sums the reported usage of the given phase records, counting worker phases with and without a
    /// usage report. Iteration totals are <c>Sum(iteration.Phases)</c>; goal totals are
    /// <c>Sum(allIterations.SelectMany(i =&gt; i.Phases))</c>.
    /// </summary>
    /// <param name="phases">The phase records to sum; may be empty.</param>
    /// <returns>The merged usage plus the worker-phase report counts.</returns>
    internal static Result Sum(IEnumerable<PhaseResult> phases)
    {
        var phasesWithUsage = 0;
        var phasesWithoutUsage = 0;
        var reported = new List<SharpCoder.UsageSummary?>();

        foreach (var phase in phases)
        {
            // Non-worker phases are ignored entirely — they were never billed to a worker.
            if (string.IsNullOrEmpty(phase.Name.ToRoleName()))
                continue;

            if (phase.Usage is null)
            {
                phasesWithoutUsage++;
                continue;
            }

            phasesWithUsage++;
            reported.Add(phase.Usage);
        }

        return new Result
        {
            Usage = Merge(reported),
            PhasesWithUsage = phasesWithUsage,
            PhasesWithoutUsage = phasesWithoutUsage,
        };
    }

    /// <summary>
    /// Merges the entries of the given summaries by <c>(source, model)</c> with an ORDINAL model
    /// comparison, summing each entry's usage into one bucket per key.
    /// <para>
    /// The merge is per ENTRY, never per summary: each entry's <see cref="SharpCoder.TokenUsage"/> is
    /// added with <see cref="SharpCoder.TokenUsage.Add(SharpCoder.TokenUsage)"/> onto a fresh
    /// accumulator, which sums <c>Calls</c>, both <c>*ReportedCalls</c> counters and the estimated
    /// breakdown. <see cref="SharpCoder.UsageSummary.Add(SharpCoder.UsageEvent)"/> is deliberately NOT
    /// used: it records ONE model call per event and would count each summed entry as a single call.
    /// </para>
    /// <para>
    /// <c>null</c> summaries are skipped. The inputs are never mutated — reading <c>Entries</c> yields
    /// detached copies and every count is summed onto a new accumulator — and the result is a detached
    /// summary (its ctor deep-copies each entry it is given).
    /// </para>
    /// </summary>
    /// <param name="summaries">The summaries to merge; may be empty, may contain <c>null</c>s.</param>
    /// <returns>
    /// One entry per distinct <c>(source, model)</c> pair seen in the input, each carrying the summed
    /// counts; an empty summary (never <c>null</c>) when nothing was reported.
    /// </returns>
    internal static SharpCoder.UsageSummary Merge(IEnumerable<SharpCoder.UsageSummary?> summaries)
    {
        // Insertion order is preserved so the rendered entry order is deterministic.
        var order = new List<(SharpCoder.UsageSource Source, string? Model)>();
        var merged = new Dictionary<(SharpCoder.UsageSource Source, string? Model), SharpCoder.TokenUsage>(SourceModelComparer.Instance);

        foreach (var summary in summaries)
        {
            if (summary is null)
                continue;

            foreach (var entry in summary.Entries)
            {
                var key = (entry.Source, entry.Model);
                if (!merged.TryGetValue(key, out var accumulator))
                {
                    // A fresh accumulator, never the entry's own instance: the input is never mutated.
                    accumulator = new SharpCoder.TokenUsage();
                    merged[key] = accumulator;
                    order.Add(key);
                }

                accumulator.Add(entry.Usage);
            }
        }

        // The ctor deep-copies each entry, so the returned summary is detached from the accumulators.
        return new SharpCoder.UsageSummary(
            order.Select(key => new SharpCoder.UsageEntry(key.Source, key.Model, merged[key])));
    }

    /// <summary>
    /// Groups entries by <c>(source, model)</c> with an ORDINAL comparison of the model name, so
    /// model names that differ only by case or culture are distinct buckets and a null model groups
    /// only with null.
    /// </summary>
    private sealed class SourceModelComparer : IEqualityComparer<(SharpCoder.UsageSource Source, string? Model)>
    {
        internal static readonly SourceModelComparer Instance = new();

        public bool Equals((SharpCoder.UsageSource Source, string? Model) x, (SharpCoder.UsageSource Source, string? Model) y) =>
            x.Source == y.Source && string.Equals(x.Model, y.Model, StringComparison.Ordinal);

        public int GetHashCode((SharpCoder.UsageSource Source, string? Model) key) =>
            HashCode.Combine(key.Source, key.Model is null ? 0 : StringComparer.Ordinal.GetHashCode(key.Model));
    }
}
