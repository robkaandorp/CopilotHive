using System.Globalization;
using SharpCoder;

namespace CopilotHive.Goals;

/// <summary>
/// The SINGLE home for rendering token-usage values on human-facing surfaces: the Composer's
/// <c>get_goal</c> report and the dashboard goal detail page. Both surfaces call these helpers,
/// so an entry line, a total line, an optional-category rule or a compact box label exists exactly
/// once and renders identically everywhere.
/// <para>
/// Everything here is a PURE function of its inputs and culture-INVARIANT: counts are formatted
/// with <see cref="CultureInfo.InvariantCulture"/> explicitly, never with the ambient culture, so
/// the rendered text cannot change under a different locale.
/// </para>
/// </summary>
internal static class UsageFormat
{
    /// <summary>
    /// Renders one usage bucket as a compact token line, e.g.
    /// <c>input 12,345 / ≥1,234 (partial) cached input / output 5,678 / unknown reasoning / 7 calls</c>.
    /// Every level of the <c>get_goal</c> report shares this helper — the per-phase suffix, the
    /// per-iteration total, and the per-goal total and per-(source, model) lines — so the category
    /// rules exist exactly once.
    /// <para>
    /// Counts are formatted with <c>N0</c> under <see cref="CultureInfo.InvariantCulture"/>, so the
    /// returned text never depends on the ambient culture. Input and output tokens are always
    /// measured, so they show their number. Cached input and reasoning tokens are optional provider
    /// categories, so their reported-calls counter decides the rendering:
    /// <list type="bullet">
    ///   <item>counter equals <c>Calls</c> → every call reported the category, show the number;</item>
    ///   <item>counter is zero (with calls made) → no call reported it, show <c>unknown</c>;</item>
    ///   <item>counter is somewhere between → PARTIAL, show <c>≥{number} (partial)</c>.</item>
    /// </list>
    /// The call count is always shown.
    /// </para>
    /// </summary>
    /// <param name="usage">The summed usage bucket to render; must not be <c>null</c>.</param>
    /// <returns>The rendered token line, without a leading or trailing separator.</returns>
    internal static string FormatUsageLine(TokenUsage usage)
    {
        var calls = usage.Calls;
        return $"input {FormatTokenCount(usage.InputTokens)}"
            + $" / {FormatOptionalCategory(usage.CachedInputTokens, usage.CachedInputReportedCalls, calls)} cached input"
            + $" / output {FormatTokenCount(usage.OutputTokens)}"
            + $" / {FormatOptionalCategory(usage.ReasoningTokens, usage.ReasoningReportedCalls, calls)} reasoning"
            + $" / {FormatTokenCount(calls)} calls";
    }

    /// <summary>Formats a token count with thousands separators, culture-independently.</summary>
    internal static string FormatTokenCount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Renders an optional provider category (cached input or reasoning tokens) according to how many
    /// calls actually reported it — see <see cref="FormatUsageLine"/> for the three cases.
    /// </summary>
    internal static string FormatOptionalCategory(long value, int reportedCalls, int calls)
    {
        if (reportedCalls >= calls)
            return FormatTokenCount(value);
        if (reportedCalls <= 0)
            return "unknown";
        return $"≥{FormatTokenCount(value)} (partial)";
    }

    /// <summary>
    /// Renders the trailing note naming how many worker phases contributed no usage at all, or an
    /// empty string when every worker phase reported. Totals never present "not reported" as zero,
    /// so the count always travels with them.
    /// </summary>
    internal static string FormatMissingPhasesNote(PhaseUsageTotals.Result totals) =>
        totals.PhasesWithoutUsage > 0
            ? $" ({totals.PhasesWithoutUsage} worker phase(s) without usage reported)"
            : "";

    /// <summary>
    /// Renders the total of a usage summary as a SHORT label for tight spaces (a phase box on the
    /// dashboard), covering input plus output tokens: <c>950 tok</c>, <c>12.3K tok</c> or
    /// <c>1.2M tok</c>.
    /// <para>
    /// The thresholds are exclusive on the shorter unit: below one thousand tokens the plain count is
    /// shown, below one million the value is shown in thousands with ONE decimal place, and at or
    /// above one million in millions with one decimal place. Both scalings are formatted with
    /// <c>0.0</c> under <see cref="CultureInfo.InvariantCulture"/> explicitly, so the label never
    /// picks up a locale decimal separator.
    /// </para>
    /// </summary>
    /// <param name="usage">The aggregate usage whose input+output total is labelled; must not be <c>null</c>.</param>
    /// <returns>The compact label, e.g. <c>12.3K tok</c>.</returns>
    internal static string FormatCompactTotal(TokenUsage usage)
    {
        var total = usage.InputTokens + usage.OutputTokens;
        if (total < 1_000)
            return $"{FormatTokenCount(total)} tok";
        if (total < 1_000_000)
            return $"{(total / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture)}K tok";
        return $"{(total / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture)}M tok";
    }

    /// <summary>
    /// Renders one <c>{source} / {model}: {usage line}</c> entry per bucket of a usage summary, in
    /// the summary's own entry order, so a goal's per-(source, model) breakdown reads identically on
    /// the dashboard and in the <c>get_goal</c> report. An entry with a <c>null</c> model renders as
    /// <c>(unknown model)</c> — never as an empty string.
    /// </summary>
    /// <param name="usage">The usage summary whose entries are rendered; must not be <c>null</c>.</param>
    /// <returns>One line per entry, without any bullet or leading separator.</returns>
    internal static IReadOnlyList<string> FormatEntryLines(UsageSummary usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var lines = new List<string>(usage.Entries.Count);
        foreach (var entry in usage.Entries)
            lines.Add($"{entry.Source} / {entry.Model ?? "(unknown model)"}: {FormatUsageLine(entry.Usage)}");
        return lines;
    }

    /// <summary>
    /// Renders the goal-level usage breakdown as an ORDERED list of lines, each with the nesting
    /// depth the caller indents by:
    /// <list type="number">
    ///   <item>depth 0 <c>Total: {usage line}{missing-phases note}{live}</c> — the GRAND total of all three sources;</item>
    ///   <item>when any worker phase reported, depth 0 <c>Workers: {usage line}{live}</c> and one depth 1 entry line per worker <c>(source, model)</c> bucket;</item>
    ///   <item>when Brain usage is present, depth 0 <c>Brain: {usage line}</c> and its depth 1 entry lines;</item>
    ///   <item>when review usage is present, depth 0 <c>Pre-execution review: {usage line}</c> and its depth 1 entry lines.</item>
    /// </list>
    /// <para>
    /// The <c>(includes running phase)</c> marker appears on the <c>Total:</c> and <c>Workers:</c>
    /// lines ONLY when <paramref name="includesLiveUsage"/> is <c>true</c>: both are worker-derived
    /// totals that can count a running phase's live figure, whereas Brain and review usage is
    /// persisted data that no live capture ever touches. The missing-phases note belongs to the
    /// grand total as well, because it describes the worker phases whose tokens are absent from it.
    /// </para>
    /// <para>
    /// PURE and culture-invariant, like every helper here: it composes
    /// <see cref="FormatUsageLine(TokenUsage)"/>, <see cref="FormatEntryLines(UsageSummary)"/> and
    /// <see cref="FormatMissingPhasesNote(PhaseUsageTotals.Result)"/> and adds no formatting of its
    /// own. Callers that render nothing when the breakdown is <c>null</c> keep legacy goals
    /// byte-identical.
    /// </para>
    /// </summary>
    /// <param name="usage">The goal's usage breakdown; must not be <c>null</c>.</param>
    /// <param name="includesLiveUsage">
    /// Whether the worker-derived lines count a running phase's LIVE usage, in which case the
    /// <c>(includes running phase)</c> marker is appended to them.
    /// </param>
    /// <returns>One <c>(depth, text)</c> pair per rendered line, in render order.</returns>
    internal static IReadOnlyList<(int Depth, string Text)> FormatGoalUsageLines(
        GoalUsageBreakdown usage, bool includesLiveUsage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var live = includesLiveUsage ? " (includes running phase)" : "";
        var lines = new List<(int Depth, string Text)>
        {
            (0, $"Total: {FormatUsageLine(usage.Total.Total)}{FormatMissingPhasesNote(usage.Workers)}{live}"),
        };

        // The Workers line appears only for a goal whose worker phases reported: a Brain- or
        // review-only goal has no worker figure to show, and 0 tokens from nobody is not a number.
        if (usage.Workers.PhasesWithUsage > 0)
        {
            lines.Add((0, $"Workers: {FormatUsageLine(usage.Workers.Usage.Total)}{live}"));
            foreach (var entryLine in FormatEntryLines(usage.Workers.Usage))
                lines.Add((1, entryLine));
        }

        if (usage.Brain is { } brain)
        {
            lines.Add((0, $"Brain: {FormatUsageLine(brain.Total)}"));
            foreach (var entryLine in FormatEntryLines(brain))
                lines.Add((1, entryLine));
        }

        if (usage.Review is { } review)
        {
            lines.Add((0, $"Pre-execution review: {FormatUsageLine(review.Total)}"));
            foreach (var entryLine in FormatEntryLines(review))
                lines.Add((1, entryLine));
        }

        return lines;
    }

    /// <summary>
    /// Renders the compact label for a phase box, or <c>null</c> when the phase has no usage to show
    /// — not reported (legacy data, a legacy worker or test double), a non-worker phase such as
    /// Planning, or a phase that has not run yet. Callers render the label only for a non-null
    /// result, so a phase without usage renders exactly as it did before usage reporting existed.
    /// </summary>
    /// <param name="usage">The phase's reported usage, or <c>null</c> when nothing was reported.</param>
    /// <returns>The compact label, or <c>null</c> when there is nothing to show.</returns>
    internal static string? FormatPhaseBoxLabel(UsageSummary? usage) =>
        usage is null ? null : FormatCompactTotal(usage.Total);
}
