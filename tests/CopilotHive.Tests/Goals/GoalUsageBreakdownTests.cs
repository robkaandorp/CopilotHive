using System.Globalization;

using CopilotHive.Goals;
using CopilotHive.Services;

using SharpCoder;

namespace CopilotHive.Tests.Goals;

/// <summary>
/// The display contract of the goal-level usage breakdown: <see cref="GoalUsageBreakdown.Create"/>
/// builds the goal's grand total (workers + stored Brain usage + stored pre-execution review usage)
/// and <see cref="UsageFormat.FormatGoalUsageLines"/> renders it as the exact line list both surfaces
/// print.
/// <para>
/// No timing, no wall-clock: every assertion is a pure function of the fixtures below.
/// </para>
/// </summary>
public sealed class GoalUsageBreakdownTests
{
    // ── Fixtures ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The WORKER phase report: input 1,000, cached input PARTIAL (1 of 3 calls), reasoning UNKNOWN
    /// (0 of 3).
    /// </summary>
    private static UsageSummary WorkerUsage() => new(
    [
        new UsageEntry(UsageSource.Agent, "model-w", new TokenUsage
        {
            InputTokens = 1_000,
            OutputTokens = 200,
            CachedInputTokens = 30,
            ReasoningTokens = 40,
            Calls = 3,
            CachedInputReportedCalls = 1,
            ReasoningReportedCalls = 0,
        }),
    ]);

    /// <summary>The goal's stored Brain usage: a bucket no worker phase produced.</summary>
    private static UsageSummary BrainUsage() => new(
    [
        new UsageEntry(UsageSource.SubAgent, "model-b", new TokenUsage
        {
            InputTokens = 50,
            OutputTokens = 5,
            CachedInputTokens = 7,
            ReasoningTokens = 9,
            Calls = 2,
            CachedInputReportedCalls = 2,
            ReasoningReportedCalls = 2,
            Estimated = new EstimatedTokenBreakdown { InputEstimatedCalls = 4, OutputEstimatedCalls = 5 },
        }),
    ]);

    /// <summary>The goal's stored pre-execution review usage, with a <c>null</c> model.</summary>
    private static UsageSummary ReviewUsage() => new(
    [
        new UsageEntry(UsageSource.Compaction, null, new TokenUsage
        {
            InputTokens = 8,
            OutputTokens = 1,
            Calls = 1,
            CachedInputReportedCalls = 1,
            ReasoningReportedCalls = 1,
        }),
    ]);

    /// <summary>A worker phase record carrying (or not) a usage report.</summary>
    private static PhaseResult Phase(GoalPhase name, UsageSummary? usage) => new()
    {
        Name = name,
        Result = PhaseOutcome.Pass,
        Iteration = 1,
        Occurrence = 1,
        Usage = usage,
    };

    /// <summary>One worker phase that reported, plus one that did not — so the missing count is observable.</summary>
    private static PhaseUsageTotals.Result WorkerTotals() =>
        PhaseUsageTotals.Sum([Phase(GoalPhase.Coding, WorkerUsage()), Phase(GoalPhase.Testing, usage: null)]);

    /// <summary>
    /// Every value of a summary as text, entry by entry: the field-level snapshot a non-mutation
    /// assertion compares before and after <see cref="GoalUsageBreakdown.Create"/>.
    /// </summary>
    private static string Snapshot(UsageSummary? usage) =>
        usage is null
            ? "<none>"
            : string.Join(" | ", usage.Entries.Select(e =>
                $"{e.Source}/{e.Model ?? "<null>"}: in={e.Usage.InputTokens} out={e.Usage.OutputTokens}" +
                $" cached={e.Usage.CachedInputTokens}/{e.Usage.CachedInputReportedCalls}" +
                $" reason={e.Usage.ReasoningTokens}/{e.Usage.ReasoningReportedCalls}" +
                $" calls={e.Usage.Calls} estIn={e.Usage.Estimated.InputEstimatedCalls} estOut={e.Usage.Estimated.OutputEstimatedCalls}"));

    /// <summary>One bucket's exact counts plus the summary total, as text.</summary>
    private static string Describe(GoalUsageBreakdown? breakdown) =>
        breakdown is null
            ? "<none>"
            : $"workers={breakdown.Workers.PhasesWithUsage}/{breakdown.Workers.PhasesWithoutUsage}" +
              $" brain={Snapshot(breakdown.Brain)} review={Snapshot(breakdown.Review)}" +
              $" total={Snapshot(breakdown.Total)}";

    /// <summary>One bucket's counts as text, formatted with the INVARIANT culture so the expectation is locale-independent.</summary>
    private static string BucketLine(UsageEntry entry) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{entry.Source}/{entry.Model ?? "<null>"}: in={entry.Usage.InputTokens:N0} out={entry.Usage.OutputTokens:N0}" +
            $" cached={entry.Usage.CachedInputTokens:N0}/{entry.Usage.CachedInputReportedCalls}" +
            $" reason={entry.Usage.ReasoningTokens:N0}/{entry.Usage.ReasoningReportedCalls}" +
            $" calls={entry.Usage.Calls} estIn={entry.Usage.Estimated.InputEstimatedCalls} estOut={entry.Usage.Estimated.OutputEstimatedCalls}");

    // ── Create: null / non-null semantics ──────────────────────────────────────

    /// <summary>
    /// NOTHING REPORTED yields <c>null</c>: no worker phase reported AND both stored summaries are
    /// absent, so the renderers show no section and no card — a legacy goal is byte-identical.
    /// </summary>
    [Fact]
    public void Create_NothingReported_ReturnsNull()
    {
        var workers = PhaseUsageTotals.Sum([Phase(GoalPhase.Coding, usage: null), Phase(GoalPhase.Merging, WorkerUsage())]);

        // PREMISE: a non-worker phase's own report never counts, so this really is "nothing reported".
        Assert.Equal(0, workers.PhasesWithUsage);
        Assert.Equal(1, workers.PhasesWithoutUsage);

        Assert.Null(GoalUsageBreakdown.Create(workers, brain: null, review: null));
    }

    /// <summary>An EMPTY (zero-entry) summary is treated exactly like <c>null</c>: it carries no measurement.</summary>
    [Fact]
    public void Create_ZeroEntrySummaries_AreTreatedAsAbsent()
    {
        var workers = PhaseUsageTotals.Sum([Phase(GoalPhase.Coding, usage: null)]);

        Assert.Null(GoalUsageBreakdown.Create(workers, new UsageSummary(), new UsageSummary()));

        // …and alongside a reporting worker phase they are dropped from the breakdown itself, so no
        // "Brain:"/"Pre-execution review:" line can be rendered for a summary with nothing in it.
        var withWorkers = GoalUsageBreakdown.Create(WorkerTotals(), new UsageSummary(), new UsageSummary());
        Assert.NotNull(withWorkers);
        Assert.Null(withWorkers!.Brain);
        Assert.Null(withWorkers.Review);
    }

    /// <summary>
    /// Each SOURCE alone makes the breakdown non-null — including Brain- and review-only goals, whose
    /// worker phases have not reported at all (a goal still in Planning was already paid for).
    /// </summary>
    [Fact]
    public void Create_AnySourceAlone_YieldsABreakdown()
    {
        var noWorkers = PhaseUsageTotals.Sum([Phase(GoalPhase.Coding, usage: null)]);

        var workersOnly = GoalUsageBreakdown.Create(WorkerTotals(), brain: null, review: null);
        Assert.NotNull(workersOnly);
        Assert.Equal(1, workersOnly!.Workers.PhasesWithUsage);
        Assert.Null(workersOnly.Brain);
        Assert.Null(workersOnly.Review);

        var brain = BrainUsage();
        var brainOnly = GoalUsageBreakdown.Create(noWorkers, brain, review: null);
        Assert.NotNull(brainOnly);
        Assert.Equal(0, brainOnly!.Workers.PhasesWithUsage);
        Assert.Same(brain, brainOnly.Brain);
        Assert.Equal(50, brainOnly.Total.Total.InputTokens);

        var review = ReviewUsage();
        var reviewOnly = GoalUsageBreakdown.Create(noWorkers, brain: null, review);
        Assert.NotNull(reviewOnly);
        Assert.Equal(0, reviewOnly!.Workers.PhasesWithUsage);
        Assert.Null(reviewOnly.Brain);
        Assert.Same(review, reviewOnly.Review);

        // A Brain- or review-only goal still gets a real total: that is what was spent.
        Assert.Equal(8, reviewOnly.Total.Total.InputTokens);
        Assert.Equal(1, reviewOnly.Total.Total.Calls);
    }

    // ── Create: the grand total ────────────────────────────────────────────────

    /// <summary>
    /// ALL THREE sources merge per <c>(source, model)</c>: a bucket reported by two sources is summed
    /// in the total — including <c>Calls</c>, both <c>*ReportedCalls</c> counters and the estimated
    /// breakdown — a bucket only one source reported keeps its own counts, and the three source lines
    /// keep their own numbers untouched.
    /// </summary>
    [Fact]
    public void Create_AllThreeSources_MergesPerSourceModelIntoTheTotal()
    {
        // A worker bucket SHARED with the Brain, so the sum is observable on a bucket that appears in
        // two sources: calls 3 + 2, cached reported 1 + 2 (of 5 calls → PARTIAL), estimates 0 + 4.
        var brain = new UsageSummary(
        [
            new UsageEntry(UsageSource.Agent, "model-w", new TokenUsage
            {
                InputTokens = 10,
                OutputTokens = 1,
                CachedInputTokens = 3,
                ReasoningTokens = 4,
                Calls = 2,
                CachedInputReportedCalls = 2,
                ReasoningReportedCalls = 2,
                Estimated = new EstimatedTokenBreakdown { InputEstimatedCalls = 4, OutputEstimatedCalls = 5 },
            }),
            new UsageEntry(UsageSource.SubAgent, "model-b", new TokenUsage { InputTokens = 50, Calls = 1 }),
        ]);

        var breakdown = GoalUsageBreakdown.Create(WorkerTotals(), brain, ReviewUsage());

        Assert.NotNull(breakdown);
        var total = breakdown!.Total;

        // Per-bucket merge, in insertion order: workers' buckets first, then the Brain's new one, then
        // the review's.
        Assert.Equal(
            [
                "Agent/model-w: in=1,010 out=201 cached=33/3 reason=44/2 calls=5 estIn=4 estOut=5",
                "SubAgent/model-b: in=50 out=0 cached=0/0 reason=0/0 calls=1 estIn=0 estOut=0",
                "Compaction/<null>: in=8 out=1 cached=0/1 reason=0/1 calls=1 estIn=0 estOut=0",
            ],
            total.Entries.Select(BucketLine));

        // The aggregate total sums every bucket: workers' cached reported 1 + the Brain's 2 + the
        // review's 1 = 4 of the merged 7 calls, and reasoning 0 + 2 + 0 + 1 = 3 — both still PARTIAL.
        Assert.Equal(1_010 + 50 + 8, total.Total.InputTokens);
        Assert.Equal(201 + 0 + 1, total.Total.OutputTokens);
        Assert.Equal(5 + 1 + 1, total.Total.Calls);
        Assert.Equal(33, total.Total.CachedInputTokens);
        Assert.Equal(4, total.Total.CachedInputReportedCalls);
        Assert.Equal(3, total.Total.ReasoningReportedCalls);

        // The source breakdowns keep their OWN numbers: merging never rewrites an input.
        Assert.Equal(1_000, breakdown.Workers.Usage.Total.InputTokens);
        Assert.Equal(3, breakdown.Workers.Usage.Total.Calls);
        Assert.Equal(60, breakdown.Brain!.Total.InputTokens);
        Assert.Equal(8, breakdown.Review!.Total.InputTokens);
    }

    /// <summary>
    /// <see cref="GoalUsageBreakdown.Create"/> never mutates the sums it is given: every value of the
    /// worker totals, the Brain summary and the review summary is identical before and after, and the
    /// breakdown's <see cref="GoalUsageBreakdown.Total"/> is a DETACHED summary.
    /// </summary>
    [Fact]
    public void Create_LeavesItsInputsUnmutated_AndReturnsADetachedTotal()
    {
        var workers = WorkerTotals();
        var brain = BrainUsage();
        var review = ReviewUsage();
        var before = $"{Describe(workers)}|{Snapshot(brain)}|{Snapshot(review)}";

        var breakdown = GoalUsageBreakdown.Create(workers, brain, review);

        Assert.NotNull(breakdown);
        Assert.Equal(before, $"{Describe(workers)}|{Snapshot(brain)}|{Snapshot(review)}");

        // The inputs are carried through as the SAME instances (nothing was rebuilt or replaced)…
        Assert.Same(workers, breakdown!.Workers);
        Assert.Same(brain, breakdown.Brain);
        Assert.Same(review, breakdown.Review);

        // …while the total is its own summary, not one of them.
        Assert.NotSame(brain, breakdown.Total);
        Assert.NotSame(review, breakdown.Total);
        Assert.NotSame(workers.Usage, breakdown.Total);
    }

    /// <summary>Describes already-summed worker totals, so a non-mutation assertion can cover them too.</summary>
    private static string Describe(PhaseUsageTotals.Result totals) =>
        $"{totals.PhasesWithUsage}/{totals.PhasesWithoutUsage}:{Snapshot(totals.Usage)}";

    // ── FormatGoalUsageLines: the exact line list ───────────────────────────────

    /// <summary>
    /// The full line list for a goal with all three sources, WITHOUT live usage: the grand total (with
    /// the missing-phases note), the Workers line and its per-bucket children, then Brain and
    /// Pre-execution review with theirs — each depth-0 line a "<c>label: line</c>" and each child the
    /// shared entry rendering.
    /// </summary>
    [Fact]
    public void FormatGoalUsageLines_AllThreeSources_ListsTotalWorkersBrainAndReview()
    {
        var breakdown = GoalUsageBreakdown.Create(WorkerTotals(), BrainUsage(), ReviewUsage());
        Assert.NotNull(breakdown);

        Assert.Equal(
            [
                (0, "Total: input 1,058 / ≥37 (partial) cached input / output 206 / ≥49 (partial) reasoning / 6 calls (1 worker phase(s) without usage reported)"),
                (0, "Workers: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls"),
                (1, "Agent / model-w: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls"),
                (0, "Brain: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls"),
                (1, "SubAgent / model-b: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls"),
                (0, "Pre-execution review: input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls"),
                (1, "Compaction / (unknown model): input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls"),
            ],
            UsageFormat.FormatGoalUsageLines(breakdown!, includesLiveUsage: false));
    }

    /// <summary>
    /// The live marker lands on the worker-derived totals ONLY: <c>Total:</c> and <c>Workers:</c> get
    /// " (includes running phase)", while the Brain and review lines — persisted data no live capture
    /// ever touches — never grow the marker.
    /// </summary>
    [Fact]
    public void FormatGoalUsageLines_LiveMarkerAppearsOnTheWorkerDerivedLinesOnly()
    {
        var breakdown = GoalUsageBreakdown.Create(WorkerTotals(), BrainUsage(), ReviewUsage());
        Assert.NotNull(breakdown);

        var lines = UsageFormat.FormatGoalUsageLines(breakdown!, includesLiveUsage: true);

        var total = Assert.Single(lines, l => l.Text.StartsWith("Total: ", StringComparison.Ordinal));
        Assert.EndsWith(" (1 worker phase(s) without usage reported) (includes running phase)", total.Text);

        var workers = Assert.Single(lines, l => l.Text.StartsWith("Workers: ", StringComparison.Ordinal));
        Assert.EndsWith(" / 3 calls (includes running phase)", workers.Text);

        var brain = Assert.Single(lines, l => l.Text.StartsWith("Brain: ", StringComparison.Ordinal));
        Assert.DoesNotContain("includes running phase", brain.Text);
        var review = Assert.Single(lines, l => l.Text.StartsWith("Pre-execution review: ", StringComparison.Ordinal));
        Assert.DoesNotContain("includes running phase", review.Text);

        // The marker is the ONLY difference: stripping it back out of the live render and dropping it
        // from the depths it belongs to yields exactly the non-live render.
        static List<string> Render(IReadOnlyList<(int Depth, string Text)> rendered) =>
            rendered.Select(l => $"{l.Depth}:{l.Text}").ToList();

        var withoutLive = Render(UsageFormat.FormatGoalUsageLines(breakdown!, includesLiveUsage: false));
        var strippedLive = Render(lines)
            .Select(l => l.Replace(" (includes running phase)", "", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(withoutLive, strippedLive);

        // …and the marker really was present on the DEPTH 0 worker-derived lines and nowhere else.
        Assert.Equal(
            [true, true, false, false, false, false, false],
            lines.Select(l => l.Text.EndsWith(" (includes running phase)", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A Brain-ONLY goal gets no <c>Workers:</c> line at all — no worker phase reported, so there is
    /// no worker figure to show — and its total carries no missing-phases note, because the count of
    /// worker phases without usage is zero.
    /// </summary>
    [Fact]
    public void FormatGoalUsageLines_BrainOnly_OmitsTheWorkersLine()
    {
        var breakdown = GoalUsageBreakdown.Create(PhaseUsageTotals.Sum([]), BrainUsage(), review: null);
        Assert.NotNull(breakdown);

        Assert.Equal(
            [
                (0, "Total: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls"),
                (0, "Brain: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls"),
                (1, "SubAgent / model-b: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls"),
            ],
            UsageFormat.FormatGoalUsageLines(breakdown!, includesLiveUsage: false));
    }

    /// <summary>A review-only goal lists the total and the review block — and nothing else.</summary>
    [Fact]
    public void FormatGoalUsageLines_ReviewOnly_OmitsTheWorkersAndBrainLines()
    {
        var breakdown = GoalUsageBreakdown.Create(PhaseUsageTotals.Sum([]), brain: null, ReviewUsage());
        Assert.NotNull(breakdown);

        Assert.Equal(
            [
                (0, "Total: input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls"),
                (0, "Pre-execution review: input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls"),
                (1, "Compaction / (unknown model): input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls"),
            ],
            UsageFormat.FormatGoalUsageLines(breakdown!, includesLiveUsage: false));
    }

    /// <summary>
    /// A workers-only goal keeps BOTH worker lines, and the missing-phases note stays on the grand
    /// total only (the <c>Workers:</c> line describes the reported figure alone).
    /// </summary>
    [Fact]
    public void FormatGoalUsageLines_WorkersOnly_KeepsTheNoteOnTheTotalLineOnly()
    {
        var breakdown = GoalUsageBreakdown.Create(WorkerTotals(), brain: null, review: null);
        Assert.NotNull(breakdown);

        Assert.Equal(
            [
                (0, "Total: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls (1 worker phase(s) without usage reported)"),
                (0, "Workers: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls"),
                (1, "Agent / model-w: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls"),
            ],
            UsageFormat.FormatGoalUsageLines(breakdown!, includesLiveUsage: false));
    }

    /// <summary>
    /// A goal whose worker phases ALL reported renders no missing-phases note anywhere, and multiple
    /// worker buckets stay in the merged summary's own order as depth-1 lines.
    /// </summary>
    [Fact]
    public void FormatGoalUsageLines_AllWorkerPhasesReported_OmitsTheNoteAndKeepsEveryBucket()
    {
        var workers = PhaseUsageTotals.Sum(
        [
            Phase(GoalPhase.Coding, WorkerUsage()),
            Phase(GoalPhase.Testing, ReviewUsage()),
        ]);
        var breakdown = GoalUsageBreakdown.Create(workers, brain: null, review: null);
        Assert.NotNull(breakdown);

        var lines = UsageFormat.FormatGoalUsageLines(breakdown!, includesLiveUsage: false);

        Assert.DoesNotContain(lines, l => l.Text.Contains("without usage reported", StringComparison.Ordinal));
        Assert.Equal([0, 0, 1, 1], lines.Select(l => l.Depth));
        // The merged cached-input and reasoning categories are PARTIAL: 2 and 1 of the 4 merged calls
        // reported them, so the two entry verdicts combine exactly as PhaseUsageTotals.Merge sums them.
        Assert.Equal(
            [
                "Total: input 1,008 / ≥30 (partial) cached input / output 201 / ≥40 (partial) reasoning / 4 calls",
                "Workers: input 1,008 / ≥30 (partial) cached input / output 201 / ≥40 (partial) reasoning / 4 calls",
                "Agent / model-w: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls",
                "Compaction / (unknown model): input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls",
            ],
            lines.Select(l => l.Text));
    }

    /// <summary>An empty worker total with a zero-token report still counts as reported, so the Workers line appears.</summary>
    [Fact]
    public void FormatGoalUsageLines_EmptyWorkerSummary_StillRendersTheWorkersLine()
    {
        var workers = PhaseUsageTotals.Sum([Phase(GoalPhase.Coding, new UsageSummary())]);
        Assert.Equal(1, workers.PhasesWithUsage);

        var breakdown = GoalUsageBreakdown.Create(workers, brain: null, review: null);
        Assert.NotNull(breakdown);

        Assert.Equal(
            [
                (0, "Total: input 0 / 0 cached input / output 0 / 0 reasoning / 0 calls"),
                (0, "Workers: input 0 / 0 cached input / output 0 / 0 reasoning / 0 calls"),
            ],
            UsageFormat.FormatGoalUsageLines(breakdown!, includesLiveUsage: false));
    }
}
