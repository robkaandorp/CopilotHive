using System.Globalization;

using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Services;

using SharpCoder;

namespace CopilotHive.Tests.Dashboard;

/// <summary>
/// Serializes the tests that mutate the PROCESS-WIDE current culture, exactly like
/// <c>EnvVarMutationCollection</c> does for environment variables, so a temporary de-DE current
/// culture can never be observed by a test running in parallel and cannot corrupt its rendering.
/// </summary>
[CollectionDefinition("CultureMutation", DisableParallelization = true)]
public sealed class CultureMutationCollection { }

/// <summary>
/// The culture-invariance contract of <see cref="UsageFormat.FormatCompactTotal"/>: run under the
/// serialized culture-mutation collection, because the de-DE current culture is PROCESS-WIDE.
/// </summary>
[Collection("CultureMutation")]
public sealed class UsageFormatCultureTests
{
    /// <summary>
    /// <see cref="UsageFormat.FormatCompactTotal"/> at its boundaries, under a NON-invariant current
    /// culture: de-DE uses "," as the decimal separator and "." as the group separator, so a helper
    /// that fell back to the ambient culture would render "1,0K tok" / "1.000 tok" and fail. Every
    /// expected string is asserted against the invariant rendering too.
    /// </summary>
    [Theory]
    [InlineData(0, 0, "0 tok")]
    [InlineData(999, 0, "999 tok")]
    [InlineData(1_000, 0, "1.0K tok")]
    [InlineData(12_345, 0, "12.3K tok")]
    [InlineData(1_234_567, 0, "1.2M tok")]
    [InlineData(0, 950, "950 tok")]
    public void FormatCompactTotal_Boundaries_AreInvariant(long input, long output, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // Non-vacuous guard: the current culture must ACTUALLY differ from the invariant one, or
            // the assertions below would pass even for an ambient-culture implementation.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
            Assert.Equal(".", CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator);

            var usage = new TokenUsage { InputTokens = input, OutputTokens = output };

            Assert.Equal(expected, UsageFormat.FormatCompactTotal(usage));

            // …and the ambient culture cannot change the invariant rendering.
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(expected, UsageFormat.FormatCompactTotal(usage));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// The FULL usage line and the entry lines are culture-invariant too: under de-DE the thousands
    /// separators must stay invariant (a comma), not become dots.
    /// </summary>
    [Fact]
    public void FormatUsageLineAndEntryLines_AreInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var usage = new TokenUsage
            {
                InputTokens = 12_345,
                OutputTokens = 678,
                Calls = 1,
                CachedInputReportedCalls = 1,
                ReasoningReportedCalls = 1,
            };

            Assert.Equal(
                "input 12,345 / 0 cached input / output 678 / 0 reasoning / 1 calls",
                UsageFormat.FormatUsageLine(usage));

            var entryLines = UsageFormat.FormatEntryLines(new UsageSummary(
                [new UsageEntry(UsageSource.Agent, "model-a", usage)]));
            Assert.Equal("Agent / model-a: input 12,345 / 0 cached input / output 678 / 0 reasoning / 1 calls",
                Assert.Single(entryLines));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

/// <summary>
/// The persisted-usage presentation contract of the goal detail page: <see cref="UsageFormat"/> (the
/// shared single home of every usage rendering), the usage fields of the dashboard view models, and
/// the totals <see cref="GoalDetailViewBuilder"/> attaches to an iteration and to the whole goal.
/// <para>
/// No bUnit and no timing: the markup-relevant strings are asserted through mirrors of the exact
/// compositions the razor page makes, for BOTH the <c>_detail</c> path (view models built by
/// <see cref="GoalDetailViewBuilder"/>) and the stored fallback path (<see cref="IterationSummary"/>
/// records straight from the store, with the page's own <c>SumWorkerUsage</c> call).
/// </para>
/// </summary>
public sealed class GoalDetailUsageTests
{
    // ── Fixtures ───────────────────────────────────────────────────────────────

    /// <summary>A worker phase record with the given reported usage (or <c>null</c> for "not reported").</summary>
    private static PhaseResult Phase(GoalPhase name, UsageSummary? usage, int iteration = 1, int? occurrence = 1) => new()
    {
        Name = name,
        Result = PhaseOutcome.Pass,
        Iteration = iteration,
        Occurrence = occurrence,
        Usage = usage,
    };

    /// <summary>
    /// A one-entry usage summary whose numbers are all DISTINCT, so a total computed over the wrong
    /// phase set cannot coincidentally match the expected one.
    /// </summary>
    private static UsageSummary OneEntry(
        UsageSource source,
        string? model,
        long input,
        long output,
        int calls,
        long cached = 0,
        int cachedReported = 0,
        long reasoning = 0,
        int reasoningReported = 0) =>
        new([new UsageEntry(source, model, new TokenUsage
        {
            InputTokens = input,
            OutputTokens = output,
            CachedInputTokens = cached,
            CachedInputReportedCalls = cachedReported,
            ReasoningTokens = reasoning,
            ReasoningReportedCalls = reasoningReported,
            Calls = calls,
        })]);

    /// <summary>
    /// A two-entry summary spanning two sources and a <c>null</c> model, with distinct counts: the
    /// shape the phase-detail "Tokens" line and the per-(source, model) entry lines must render,
    /// including the <c>(unknown model)</c> case.
    /// </summary>
    private static UsageSummary TwoEntryUsage() => new(
    [
        new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage
        {
            InputTokens = 1_000,
            OutputTokens = 200,
            CachedInputTokens = 30,
            ReasoningTokens = 40,
            Calls = 3,
            CachedInputReportedCalls = 1,   // PARTIAL
            ReasoningReportedCalls = 0,     // UNKNOWN
        }),
        new UsageEntry(UsageSource.SubAgent, null, new TokenUsage
        {
            InputTokens = 5,
            OutputTokens = 7,
            CachedInputTokens = 11,
            ReasoningTokens = 13,
            Calls = 2,
            CachedInputReportedCalls = 2,
            ReasoningReportedCalls = 2,
        }),
    ]);

    private static Goal WorkerPhaseGoal(params PhaseResult[] phases) => new()
    {
        Id = "goal-usage",
        Description = "Goal with usage",
        IterationSummaries = [new IterationSummary { Iteration = 1, Phases = [.. phases] }],
    };

    // ── Mirrors of the GoalDetail.razor usage markup ───────────────────────────

    /// <summary>Mirrors the phase-box usage label the razor renders next to the duration.</summary>
    private static string? PhaseBoxLabel(UsageSummary? usage) => UsageFormat.FormatPhaseBoxLabel(usage);

    /// <summary>Mirrors the phase-detail "Tokens" block: the total line plus one line per entry.</summary>
    private static IReadOnlyList<string> PhaseDetailLines(UsageSummary? usage) =>
        usage is null
            ? []
            : [$"Tokens: {UsageFormat.FormatUsageLine(usage.Total)}", .. UsageFormat.FormatEntryLines(usage)];

    /// <summary>Mirrors the "Iteration total" line the razor renders under the plan reason.</summary>
    private static string? IterationTotalLine(PhaseUsageTotals.Result? totals) =>
        totals is null
            ? null
            : $"Iteration total: {UsageFormat.FormatUsageLine(totals.Usage.Total)}{UsageFormat.FormatMissingPhasesNote(totals)}";

    /// <summary>
    /// Mirrors the left-panel "Token Usage" card body: the lines
    /// <see cref="UsageFormat.FormatGoalUsageLines"/> produces for the breakdown, with depth-1 lines
    /// indented exactly as the razor indents them.
    /// </summary>
    private static IReadOnlyList<string> GoalCardLines(GoalUsageBreakdown? breakdown, bool includesLiveUsage = false) =>
        breakdown is null
            ? []
            : UsageFormat.FormatGoalUsageLines(breakdown, includesLiveUsage)
                .Select(l => l.Depth == 1 ? $"    {l.Text}" : l.Text)
                .ToList();

    // ── UsageFormat: rendered lines ────────────────────────────────────────────

    /// <summary>
    /// The moved <see cref="UsageFormat.FormatUsageLine"/> behaviour: an optional category whose
    /// reported-calls counter is zero renders <c>unknown</c>, one that is partially reported renders
    /// <c>≥{n} (partial)</c>, and a complete one renders its number.
    /// </summary>
    [Fact]
    public void FormatUsageLine_UnknownAndPartialCategories_RenderAsBefore()
    {
        var partialAndUnknown = new TokenUsage
        {
            InputTokens = 12_345,
            OutputTokens = 678,
            CachedInputTokens = 1_234,
            ReasoningTokens = 90,
            Calls = 4,
            CachedInputReportedCalls = 3,
            ReasoningReportedCalls = 0,
        };

        Assert.Equal(
            "input 12,345 / ≥1,234 (partial) cached input / output 678 / unknown reasoning / 4 calls",
            UsageFormat.FormatUsageLine(partialAndUnknown));

        var complete = new TokenUsage
        {
            InputTokens = 100,
            OutputTokens = 20,
            CachedInputTokens = 5,
            ReasoningTokens = 7,
            Calls = 2,
            CachedInputReportedCalls = 2,
            ReasoningReportedCalls = 2,
        };

        Assert.Equal(
            "input 100 / 5 cached input / output 20 / 7 reasoning / 2 calls",
            UsageFormat.FormatUsageLine(complete));

        // Zero calls: nothing was left unreported, so BOTH optional categories show their number.
        Assert.Equal("input 0 / 0 cached input / output 0 / 0 reasoning / 0 calls",
            UsageFormat.FormatUsageLine(new TokenUsage()));
    }

    /// <summary>
    /// Entry lines carry the source, the model (or <c>(unknown model)</c> for a null model) and the
    /// per-entry usage line, in the summary's own order.
    /// </summary>
    [Fact]
    public void FormatEntryLines_NullModel_RendersUnknownModelPlaceholder()
    {
        var lines = UsageFormat.FormatEntryLines(TwoEntryUsage());

        Assert.Equal(2, lines.Count);
        Assert.Equal(
            "Agent / model-a: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls",
            lines[0]);
        Assert.Equal(
            "SubAgent / (unknown model): input 5 / 11 cached input / output 7 / 13 reasoning / 2 calls",
            lines[1]);
    }

    /// <summary><see cref="UsageFormat.FormatPhaseBoxLabel"/> is null for "not reported" and non-null even for an empty report.</summary>
    [Fact]
    public void FormatPhaseBoxLabel_NullUsageIsNull_EmptyUsageIsZero()
    {
        Assert.Null(UsageFormat.FormatPhaseBoxLabel(null));
        Assert.Equal("0 tok", UsageFormat.FormatPhaseBoxLabel(new UsageSummary()));
    }

    // ── Builder: usage copied onto phases ──────────────────────────────────────

    /// <summary>
    /// The summarised path copies each worker phase's reported usage verbatim, while the synthetic
    /// Planning row has none (the brain is not a worker phase).
    /// </summary>
    [Fact]
    public void BuildPhasesFromSummary_CopiesUsageForWorkerPhases_PlanningStaysNull()
    {
        var codingUsage = OneEntry(UsageSource.Agent, "model-a", 11, 22, 1);
        var testingUsage = new UsageSummary();   // a real zero-call measurement
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                Phase(GoalPhase.Coding, codingUsage),
                Phase(GoalPhase.Testing, testingUsage),
                Phase(GoalPhase.DocWriting, usage: null),
            ],
        };

        var phases = GoalDetailViewBuilder.BuildPhasesFromSummary("goal-usage", summary, pipeline: null);

        Assert.Same(codingUsage, Assert.Single(phases, p => p.Name == "Coding").Usage);
        Assert.Same(testingUsage, Assert.Single(phases, p => p.Name == "Testing").Usage);
        Assert.Null(Assert.Single(phases, p => p.Name == "Doc Writing").Usage);

        // The Planning row is synthetic: it is never a worker phase, so it carries no usage.
        var planning = Assert.Single(phases, p => p.Name == "Planning");
        Assert.Equal("brain", planning.RoleName);
        Assert.Null(planning.Usage);
    }

    /// <summary>
    /// The live path copies each logged phase entry's usage, and a phase that has not run yet
    /// (pending) carries none.
    /// </summary>
    [Fact]
    public void BuildPhasesFromPipeline_CopiesPhaseLogUsage_PendingPhasesStayNull()
    {
        var goal = new Goal { Id = "goal-live-usage", Description = "Live usage" };
        var pipeline = new GoalPipeline(goal, maxRetries: 3, maxIterations: 5);
        pipeline.SetPlan(new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging] });
        pipeline.AdvanceTo(GoalPhase.Testing);

        var codingUsage = OneEntry(UsageSource.Agent, "model-a", 100, 200, 4, cached: 30, cachedReported: 2);
        pipeline.PhaseLog.Add(Phase(GoalPhase.Coding, codingUsage, iteration: pipeline.Iteration));

        var phases = GoalDetailViewBuilder.BuildPhasesFromPipeline("goal-live-usage", pipeline, pipeline.Iteration);

        Assert.Same(codingUsage, Assert.Single(phases, p => p.Name == "Coding").Usage);

        // Testing has not been logged yet — it is pending, so it carries no usage.
        var pending = Assert.Single(phases, p => p.Name == "Testing");
        Assert.Equal("pending", pending.Status);
        Assert.Null(pending.Usage);

        Assert.Null(Assert.Single(phases, p => p.Name == "Planning").Usage);
    }

    // ── Builder: iteration and goal totals ─────────────────────────────────────

    /// <summary>
    /// The goal total is the sum over the union of exactly the phase sets the iterations were built
    /// from — a persisted summary, an in-memory-only summary, AND the live iteration's own PhaseLog
    /// entries — and each iteration's own total matches its own set. Distinct numbers per phase make
    /// any wrong phase set visible.
    /// <para>
    /// TWO PREMISES ARE ASSERTED EXPLICITLY, because the fixture is only meaningful when they hold:
    /// <list type="number">
    ///   <item>
    ///   The pipeline is in a REAL WORKER PHASE (Review), not the default Planning phase — in
    ///   Planning, <c>BuildPhasesFromPipeline</c> deliberately builds only the synthetic Planning row,
    ///   so the live worker views this test asserts would never be built. The live Coding/Review rows
    ///   are asserted TOGETHER WITH their <c>Usage</c> values, which proves the top-level
    ///   <c>Build</c> wires the live views and the usage totals to the SAME current-iteration records.
    ///   </item>
    ///   <item>
    ///   The earlier iterations really DO have summaries (iteration 1 persisted, iteration 2
    ///   in-memory), while their worker PhaseLog records are STILL RETAINED in the pipeline. Those
    ///   retained records carry distinct nonzero usage, so a total that wrongly includes them cannot
    ///   coincidentally match: only the current-iteration filter keeps them out, which is exactly the
    ///   dedup-by-construction the goal union relies on.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public void Build_TotalsSumExactlyTheBuiltPhaseSets_IncludingTheLiveIteration()
    {
        var persistedIterationOne = OneEntry(UsageSource.Agent, "model-a", 1, 2, 1);
        var inMemoryIterationTwo = OneEntry(UsageSource.Agent, "model-b", 10, 20, 1);
        var liveCoding = OneEntry(UsageSource.SubAgent, null, 100, 200, 1);
        var liveReview = OneEntry(UsageSource.Compaction, "model-live-review", 300, 400, 1);
        // Retained usage of the ALREADY-SUMMARISED iterations 1 and 2: nonzero, from different
        // sources/models and with values distinct from every live and summary figure above.
        var historicIterationOne = OneEntry(UsageSource.SubAgentCompaction, "model-historic-1", 5_000, 6_000, 1);
        var historicIterationTwo = OneEntry(UsageSource.SubAgentCompaction, "model-historic-2", 7_000, 8_000, 1);

        var fullGoal = WorkerPhaseGoal(Phase(GoalPhase.Coding, persistedIterationOne, iteration: 1));

        var lightGoal = new Goal { Id = "goal-usage", Description = "Goal with usage" };
        var pipeline = new GoalPipeline(lightGoal, maxRetries: 3, maxIterations: 5);
        while (pipeline.Iteration < 3)
            pipeline.IterationBudget.TryConsume();
        Assert.Equal(3, pipeline.Iteration);

        pipeline.CompletedIterationSummaries.Add(new IterationSummary
        {
            Iteration = 2,
            Phases = [Phase(GoalPhase.Testing, inMemoryIterationTwo, iteration: 2)],
        });

        // The pipeline is driven into a real worker phase with an installed plan, so the live branch
        // builds the worker rows this test asserts.
        pipeline.SetPlan(new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.DocWriting, GoalPhase.Review, GoalPhase.Merging],
            Reason = "live plan reason",
        });
        pipeline.AdvanceTo(GoalPhase.Review);

        // PREMISE 1: a real worker phase — not Planning, where only the synthetic row is built.
        Assert.Equal(GoalPhase.Review, pipeline.Phase);
        Assert.NotEqual(GoalPhase.Planning, pipeline.Phase);
        // PREMISE 2: the earlier iterations have summaries, and the live iteration does NOT.
        Assert.Single(fullGoal.IterationSummaries, s => s.Iteration == 1);
        Assert.Single(pipeline.CompletedIterationSummaries, s => s.Iteration == 2);
        Assert.DoesNotContain(pipeline.CompletedIterationSummaries, s => s.Iteration == 3);
        // PREMISE 2b: the historical reports are nonzero and cannot be mistaken for the live figures.
        Assert.NotEqual(0, historicIterationOne.Total.InputTokens);
        Assert.NotEqual(0, historicIterationTwo.Total.InputTokens);
        Assert.NotEqual(liveCoding.Total.InputTokens + liveReview.Total.InputTokens, historicIterationOne.Total.InputTokens);
        Assert.NotEqual(liveCoding.Total.InputTokens + liveReview.Total.InputTokens, historicIterationTwo.Total.InputTokens);

        // The RETAINED PhaseLog of the two summarised iterations, plus the live iteration's entries.
        pipeline.PhaseLog.Add(Phase(GoalPhase.Coding, historicIterationOne, iteration: 1));
        pipeline.PhaseLog.Add(Phase(GoalPhase.Testing, historicIterationTwo, iteration: 2));

        // A non-worker phase carrying usage must NOT contribute to any total.
        pipeline.PhaseLog.Add(Phase(GoalPhase.Merging, OneEntry(UsageSource.Compaction, "model-ignored", 999_999, 0, 1), iteration: 3));
        pipeline.PhaseLog.Add(Phase(GoalPhase.Coding, liveCoding, iteration: 3));
        // A worker phase with NO report: named as missing, contributing no tokens.
        pipeline.PhaseLog.Add(Phase(GoalPhase.DocWriting, usage: null, iteration: 3));
        pipeline.PhaseLog.Add(Phase(GoalPhase.Review, liveReview, iteration: 3));

        var detail = GoalDetailViewBuilder.Build(lightGoal, "goal-usage", pipeline, fullGoal, config: null);

        Assert.NotNull(detail);
        var iterations = detail!.Iterations;
        Assert.Equal([1, 2, 3], iterations.Select(i => i.Number));

        var first = Assert.Single(iterations, i => i.Number == 1);
        Assert.NotNull(first.UsageTotals);
        Assert.Equal(1, first.UsageTotals!.PhasesWithUsage);
        Assert.Equal(0, first.UsageTotals.PhasesWithoutUsage);
        Assert.Equal(1, first.UsageTotals.Usage.Total.InputTokens);
        Assert.Equal(2, first.UsageTotals.Usage.Total.OutputTokens);
        // The summarised iteration is accounted from its SUMMARY, never from the retained log.
        Assert.DoesNotContain(first.UsageTotals.Usage.Entries, e => e.Model == "model-historic-1");

        var second = Assert.Single(iterations, i => i.Number == 2);
        Assert.NotNull(second.UsageTotals);
        Assert.Equal(10, second.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(20, second.UsageTotals.Usage.Total.OutputTokens);
        Assert.DoesNotContain(second.UsageTotals.Usage.Entries, e => e.Model == "model-historic-2");

        // THE LIVE ITERATION: its worker rows and their usage come from the SAME current-iteration
        // records the totals are summed over.
        var third = Assert.Single(iterations, i => i.Number == 3);
        Assert.True(third.IsCurrent);

        var liveCodingRow = Assert.Single(third.Phases, p => p.Name == "Coding");
        Assert.Same(liveCoding, liveCodingRow.Usage);
        var liveReviewRow = Assert.Single(third.Phases, p => p.Name == "Review");
        Assert.Same(liveReview, liveReviewRow.Usage);
        // The unreported worker row is present and carries nothing.
        Assert.Null(Assert.Single(third.Phases, p => p.Name == "Doc Writing").Usage);
        // …and the non-worker row carries its report but is excluded from the totals below.
        Assert.NotNull(Assert.Single(third.Phases, p => p.Name == "Merging").Usage);

        Assert.NotNull(third.UsageTotals);
        Assert.Equal(liveCoding.Total.InputTokens + liveReview.Total.InputTokens, third.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(liveCoding.Total.OutputTokens + liveReview.Total.OutputTokens, third.UsageTotals.Usage.Total.OutputTokens);
        // The unreported DocWriting phase is named, and the non-worker Merging phase is in neither
        // count — nor are the two RETAINED historical records for iterations 1 and 2.
        Assert.Equal(2, third.UsageTotals.PhasesWithUsage);
        Assert.Equal(1, third.UsageTotals.PhasesWithoutUsage);
        Assert.DoesNotContain(third.UsageTotals.Usage.Entries, e => e.Model == "model-ignored");
        Assert.DoesNotContain(third.UsageTotals.Usage.Entries, e => e.Model == "model-historic-1");
        Assert.DoesNotContain(third.UsageTotals.Usage.Entries, e => e.Model == "model-historic-2");
        Assert.DoesNotContain(third.UsageTotals.Usage.Entries, e => e.Model == "model-a");
        Assert.DoesNotContain(third.UsageTotals.Usage.Entries, e => e.Model == "model-b");

        var goal = detail.UsageTotals;
        Assert.NotNull(goal);
        Assert.Equal(1 + 10 + liveCoding.Total.InputTokens + liveReview.Total.InputTokens, goal!.Usage.Total.InputTokens);
        Assert.Equal(2 + 20 + liveCoding.Total.OutputTokens + liveReview.Total.OutputTokens, goal.Usage.Total.OutputTokens);
        // Each iteration is counted exactly once: 2 live + 1 persisted + 1 in-memory.
        Assert.Equal(4, goal.PhasesWithUsage);
        Assert.Equal(1, goal.PhasesWithoutUsage);
        Assert.DoesNotContain(goal.Usage.Entries, e => e.Model == "model-ignored");
        // THE DEDUP-BY-CONSTRUCTION PROOF: the retained historical records are excluded, so the
        // already-summarised iterations are never counted a second time through the PhaseLog.
        Assert.DoesNotContain(goal.Usage.Entries, e => e.Model == "model-historic-1");
        Assert.DoesNotContain(goal.Usage.Entries, e => e.Model == "model-historic-2");
        Assert.NotEqual(5_000 + 7_000, goal.Usage.Total.InputTokens);
        Assert.NotEqual(6_000 + 8_000, goal.Usage.Total.OutputTokens);
    }

    /// <summary>
    /// A persisted summary WINS over the in-memory summary with the same iteration number, and the
    /// losing entry contributes nothing — the iteration is counted exactly once. The in-memory
    /// numbers are far larger, so a double-count is unmistakable.
    /// </summary>
    [Fact]
    public void Build_PersistedAndInMemorySummaryForSameIteration_IsCountedOnce()
    {
        var persisted = OneEntry(UsageSource.Agent, "model-persisted", 1, 2, 1);
        var inMemory = OneEntry(UsageSource.Agent, "model-in-memory", 1_000, 2_000, 1);

        var lightGoal = new Goal { Id = "goal-dup", Description = "Duplicate summary" };
        var fullGoal = new Goal
        {
            Id = "goal-dup",
            Description = "Duplicate summary",
            IterationSummaries = [new IterationSummary { Iteration = 1, Phases = [Phase(GoalPhase.Coding, persisted)] }],
        };

        var pipeline = new GoalPipeline(lightGoal, maxRetries: 3, maxIterations: 5);
        pipeline.CompletedIterationSummaries.Add(new IterationSummary
        {
            Iteration = 1,
            Phases = [Phase(GoalPhase.Coding, inMemory)],
        });

        var detail = GoalDetailViewBuilder.Build(lightGoal, "goal-dup", pipeline, fullGoal, config: null);

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        Assert.NotNull(iteration.UsageTotals);
        Assert.Equal(1, iteration.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(2, iteration.UsageTotals.Usage.Total.OutputTokens);
        Assert.DoesNotContain(iteration.UsageTotals.Usage.Entries, e => e.Model == "model-in-memory");

        Assert.NotNull(detail.UsageTotals);
        Assert.Equal(1, detail.UsageTotals!.Usage.Total.InputTokens);
        Assert.DoesNotContain(detail.UsageTotals.Usage.Entries, e => e.Model == "model-in-memory");
    }

    /// <summary>
    /// A pipeline whose CURRENT iteration already has a summary does not add that iteration's
    /// PhaseLog usage on top: the summarised iteration is the only contributor, so the live
    /// PhaseLog entry is never counted twice.
    /// </summary>
    [Fact]
    public void Build_CurrentIterationAlreadySummarised_DoesNotAddPhaseLogUsage()
    {
        var summaryUsage = OneEntry(UsageSource.Agent, "model-summary", 7, 9, 1);
        var stalePhaseLogUsage = OneEntry(UsageSource.Agent, "model-phase-log", 500_000, 600_000, 1);

        var lightGoal = new Goal { Id = "goal-summarised-current", Description = "Summarised current iteration" };
        var fullGoal = new Goal
        {
            Id = "goal-summarised-current",
            Description = "Summarised current iteration",
            IterationSummaries = [new IterationSummary { Iteration = 1, Phases = [Phase(GoalPhase.Coding, summaryUsage)] }],
        };

        var pipeline = new GoalPipeline(lightGoal, maxRetries: 3, maxIterations: 5);
        Assert.Equal(1, pipeline.Iteration);
        pipeline.PhaseLog.Add(Phase(GoalPhase.Coding, stalePhaseLogUsage, iteration: 1));

        var detail = GoalDetailViewBuilder.Build(lightGoal, "goal-summarised-current", pipeline, fullGoal, config: null);

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        Assert.False(iteration.IsCurrent);
        Assert.Equal(7, iteration.UsageTotals!.Usage.Total.InputTokens);
        Assert.DoesNotContain(iteration.UsageTotals.Usage.Entries, e => e.Model == "model-phase-log");

        Assert.Equal(7, detail.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(9, detail.UsageTotals.Usage.Total.OutputTokens);
        Assert.DoesNotContain(detail.UsageTotals.Usage.Entries, e => e.Model == "model-phase-log");
    }

    /// <summary>
    /// A pipeline whose CURRENT iteration has only an IN-MEMORY summary (not yet persisted) also
    /// suppresses the live PhaseLog branch for that number: the timeline carries ONE row for the
    /// iteration and the PhaseLog usage is not counted on top. The in-memory-only shape is what makes
    /// the suppression check observable as "any summary", not just "a persisted one".
    /// </summary>
    [Fact]
    public void Build_CurrentIterationHasOnlyInMemorySummary_SuppressesTheLiveBranch()
    {
        var summaryUsage = OneEntry(UsageSource.Agent, "model-in-memory", 3, 4, 1);
        var phaseLogUsage = OneEntry(UsageSource.Agent, "model-phase-log", 700_000, 800_000, 1);

        var lightGoal = new Goal { Id = "goal-in-memory-current", Description = "In-memory current summary" };
        var pipeline = new GoalPipeline(lightGoal, maxRetries: 3, maxIterations: 5);
        Assert.Equal(1, pipeline.Iteration);
        pipeline.CompletedIterationSummaries.Add(new IterationSummary
        {
            Iteration = 1,
            Phases = [Phase(GoalPhase.Coding, summaryUsage)],
        });
        pipeline.PhaseLog.Add(Phase(GoalPhase.Coding, phaseLogUsage, iteration: 1));

        var detail = GoalDetailViewBuilder.Build(lightGoal, "goal-in-memory-current", pipeline, fullGoalWithSummaries: null, config: null);

        Assert.NotNull(detail);
        // Exactly ONE row for iteration 1 — the in-memory summary's row, not a second live row.
        var iteration = Assert.Single(detail!.Iterations);
        Assert.Equal(1, iteration.Number);
        Assert.Equal(3, iteration.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(4, iteration.UsageTotals.Usage.Total.OutputTokens);
        Assert.DoesNotContain(iteration.UsageTotals.Usage.Entries, e => e.Model == "model-phase-log");

        Assert.Equal(3, detail.UsageTotals!.Usage.Total.InputTokens);
        Assert.DoesNotContain(detail.UsageTotals.Usage.Entries, e => e.Model == "model-phase-log");
    }

    /// <summary>
    /// Legacy data — worker phases that report nothing, plus a goal whose phases are all non-worker —
    /// yields NULL totals everywhere, which is what suppresses every usage line on the page.
    /// </summary>
    [Fact]
    public void Build_NoWorkerPhaseReportedUsage_TotalsAreNull()
    {
        var fullGoal = new Goal
        {
            Id = "goal-legacy",
            Description = "Legacy goal",
            IterationSummaries =
            [
                new IterationSummary
                {
                    Iteration = 1,
                    Phases =
                    [
                        Phase(GoalPhase.Coding, usage: null),
                        Phase(GoalPhase.Merging, OneEntry(UsageSource.Compaction, "model-ignored", 999_999, 0, 1)),
                    ],
                },
            ],
        };

        var detail = GoalDetailViewBuilder.Build(fullGoal, "goal-legacy", pipeline: null, fullGoal, config: null);

        Assert.NotNull(detail);
        Assert.Null(Assert.Single(detail!.Iterations).UsageTotals);
        Assert.Null(detail.UsageTotals);
    }

    /// <summary>
    /// A worker phase with usage next to a worker phase without it: exactly one missing phase is
    /// counted, the reported tokens still total, and the rendered note names the missing count.
    /// </summary>
    [Fact]
    public void Build_OneWorkerPhaseWithoutUsage_CountsAndRendersTheMissingNote()
    {
        var fullGoal = WorkerPhaseGoal(
            Phase(GoalPhase.Coding, OneEntry(UsageSource.Agent, "model-a", 11, 22, 1)),
            Phase(GoalPhase.Testing, usage: null));

        var detail = GoalDetailViewBuilder.Build(fullGoal, "goal-usage", pipeline: null, fullGoal, config: null);

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        Assert.NotNull(iteration.UsageTotals);
        Assert.Equal(1, iteration.UsageTotals!.PhasesWithUsage);
        Assert.Equal(1, iteration.UsageTotals.PhasesWithoutUsage);
        Assert.Equal(11, iteration.UsageTotals.Usage.Total.InputTokens);
        Assert.Equal(22, iteration.UsageTotals.Usage.Total.OutputTokens);

        Assert.Equal(1, detail.UsageTotals!.PhasesWithoutUsage);
        Assert.Equal(" (1 worker phase(s) without usage reported)", UsageFormat.FormatMissingPhasesNote(detail.UsageTotals));
        Assert.Contains(
            "(1 worker phase(s) without usage reported)",
            IterationTotalLine(iteration.UsageTotals));
    }

    /// <summary>
    /// An EMPTY (non-null) summary is a real zero-call MEASUREMENT: the phase counts as reported,
    /// so the totals are non-null with zero tokens (and the box label is "0 tok", not nothing).
    /// </summary>
    [Fact]
    public void Build_EmptySummaryCountsAsReported_WithZeroTokens()
    {
        var empty = new UsageSummary();
        var fullGoal = WorkerPhaseGoal(Phase(GoalPhase.Coding, empty));

        var detail = GoalDetailViewBuilder.Build(fullGoal, "goal-usage", pipeline: null, fullGoal, config: null);

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        Assert.NotNull(iteration.UsageTotals);
        Assert.Equal(1, iteration.UsageTotals!.PhasesWithUsage);
        Assert.Equal(0, iteration.UsageTotals.PhasesWithoutUsage);
        Assert.Empty(iteration.UsageTotals.Usage.Entries);
        Assert.Equal(0, iteration.UsageTotals.Usage.Total.InputTokens);
        Assert.Equal(0, iteration.UsageTotals.Usage.Total.OutputTokens);

        Assert.NotNull(detail.UsageTotals);
        Assert.Equal(1, detail.UsageTotals!.PhasesWithUsage);
        Assert.Equal("0 tok", PhaseBoxLabel(empty));
        Assert.Equal(
            [
                "Tokens: input 0 / 0 cached input / output 0 / 0 reasoning / 0 calls",
            ],
            PhaseDetailLines(empty));
    }

    // ── Mirrored presentation: the _detail path ───────────────────────────────

    /// <summary>
    /// The <c>_detail</c> path renders, from the view models alone: the phase-box label, the phase
    /// "Tokens" line plus per-(source, model) lines (including <c>(unknown model)</c>), the iteration
    /// total under the plan reason, and the goal card total plus entry lines.
    /// </summary>
    [Fact]
    public void DetailPath_RendersPhaseBoxPhaseDetailIterationTotalAndGoalCard()
    {
        var fullGoal = WorkerPhaseGoal(Phase(GoalPhase.Coding, TwoEntryUsage()));

        var detail = GoalDetailViewBuilder.Build(fullGoal, "goal-usage", pipeline: null, fullGoal, config: null);
        Assert.NotNull(detail);

        var iteration = Assert.Single(detail!.Iterations);
        var phase = Assert.Single(iteration.Phases, p => p.Name == "Coding");

        // PHASE BOX: the compact total of the phase's own report — 1,000 + 200 + 5 + 7 = 1,212.
        Assert.Equal("1.2K tok", PhaseBoxLabel(phase.Usage));

        // PHASE DETAIL: the total line, then one line per entry. The total's own counters sum both
        // entries, so a category that is UNKNOWN in one entry becomes PARTIAL once the other reports
        // it — while the per-entry lines below keep their individual verdicts.
        Assert.Equal(
            [
                "Tokens: input 1,005 / ≥41 (partial) cached input / output 207 / ≥53 (partial) reasoning / 5 calls",
                "Agent / model-a: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls",
                "SubAgent / (unknown model): input 5 / 11 cached input / output 7 / 13 reasoning / 2 calls",
            ],
            PhaseDetailLines(phase.Usage));

        // ITERATION TOTAL: the summed counters differ from the single-entry lines above — 41 cached
        // input of 5 calls is still PARTIAL, and 53 reasoning reported by 2 of the 5 calls is PARTIAL
        // too (the unknown single-entry category becomes partial once another entry reports it).
        Assert.Equal(
            "Iteration total: input 1,005 / ≥41 (partial) cached input / output 207 / ≥53 (partial) reasoning / 5 calls",
            IterationTotalLine(iteration.UsageTotals));
        Assert.DoesNotContain("without usage reported", IterationTotalLine(iteration.UsageTotals));

        // GOAL CARD: the grand total, the Workers line (the same total — this goal has no Brain or
        // review usage and no missing worker phase), then the same two entry lines INDENTED. The card
        // is driven by the breakdown, and the worker-only total it reports is unchanged.
        Assert.NotNull(detail.GoalUsage);
        Assert.Null(detail.GoalUsage!.Brain);
        Assert.Null(detail.GoalUsage.Review);
        Assert.Equal(
            [
                "Total: input 1,005 / ≥41 (partial) cached input / output 207 / ≥53 (partial) reasoning / 5 calls",
                "Workers: input 1,005 / ≥41 (partial) cached input / output 207 / ≥53 (partial) reasoning / 5 calls",
                "    Agent / model-a: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls",
                "    SubAgent / (unknown model): input 5 / 11 cached input / output 7 / 13 reasoning / 2 calls",
            ],
            GoalCardLines(detail.GoalUsage));
    }

    /// <summary>
    /// The <c>_detail</c> path suppresses everything for legacy data: no phase-box label, no phase
    /// detail lines, no iteration total, no goal card.
    /// </summary>
    [Fact]
    public void DetailPath_LegacyPhases_RenderNoUsageMarkup()
    {
        var fullGoal = WorkerPhaseGoal(Phase(GoalPhase.Coding, usage: null), Phase(GoalPhase.Testing, usage: null));

        var detail = GoalDetailViewBuilder.Build(fullGoal, "goal-usage", pipeline: null, fullGoal, config: null);
        Assert.NotNull(detail);

        var iteration = Assert.Single(detail!.Iterations);
        foreach (var phase in iteration.Phases)
            Assert.Null(PhaseBoxLabel(phase.Usage));

        Assert.Empty(PhaseDetailLines(null));
        Assert.Null(IterationTotalLine(iteration.UsageTotals));
        // NOTHING reported anywhere → no breakdown → no card at all.
        Assert.Null(detail.GoalUsage);
        Assert.Empty(GoalCardLines(detail.GoalUsage));
    }

    // ── Mirrored presentation: the stored fallback path ───────────────────────

    /// <summary>
    /// The stored fallback path (the razor's <c>_storedIterations</c> branch) renders through the SAME
    /// shared helpers, so for equivalent data it produces exactly the same strings as the
    /// <c>_detail</c> path — including the iteration total computed from the stored phase records.
    /// </summary>
    [Fact]
    public void StoredPath_RendersTheSameStringsAsTheDetailPath()
    {
        var storedIteration = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                Phase(GoalPhase.Coding, TwoEntryUsage()),
                Phase(GoalPhase.Testing, usage: null),
            ],
        };
        var storedIterations = new List<IterationSummary> { storedIteration };

        // Detail path over the SAME phase records.
        var detail = GoalDetailViewBuilder.Build(
            new Goal { Id = "goal-usage", Description = "Goal with usage" },
            "goal-usage",
            pipeline: null,
            new Goal { Id = "goal-usage", Description = "Goal with usage", IterationSummaries = storedIterations },
            config: null);
        Assert.NotNull(detail);
        var detailIteration = Assert.Single(detail!.Iterations);

        // Stored path: the page sums the stored phase records through the builder's shared helper and
        // builds its breakdown from that sum plus the STORED goal's own Brain/review usage.
        var storedIterationTotals = GoalDetailViewBuilder.SumWorkerUsage(storedIteration.Phases);
        var storedGoalTotals = GoalDetailViewBuilder.SumWorkerUsage(storedIterations.SelectMany(i => i.Phases));
        var storedBreakdown = GoalUsageBreakdown.Create(
            PhaseUsageTotals.Sum(storedIterations.SelectMany(i => i.Phases)),
            brain: null,
            review: null);

        var storedPhase = Assert.Single(storedIteration.Phases, p => p.Name == GoalPhase.Coding);

        Assert.Equal(PhaseBoxLabel(detailIteration.Phases.Single(p => p.Name == "Coding").Usage), PhaseBoxLabel(storedPhase.Usage));
        Assert.Equal("1.2K tok", PhaseBoxLabel(storedPhase.Usage));
        Assert.Equal(PhaseDetailLines(detailIteration.Phases.Single(p => p.Name == "Coding").Usage), PhaseDetailLines(storedPhase.Usage));
        Assert.Equal(IterationTotalLine(detailIteration.UsageTotals), IterationTotalLine(storedIterationTotals));
        Assert.Equal(GoalCardLines(detail.GoalUsage), GoalCardLines(storedBreakdown));

        // The one missing worker phase is named on BOTH totals, so the note is identical too.
        Assert.Equal(" (1 worker phase(s) without usage reported)", UsageFormat.FormatMissingPhasesNote(storedGoalTotals!));
        Assert.Contains("without usage reported", IterationTotalLine(storedIterationTotals));
    }

    /// <summary>The stored path suppresses every usage line for legacy data too.</summary>
    [Fact]
    public void StoredPath_LegacyIterations_RenderNoUsageMarkup()
    {
        var storedIterations = new List<IterationSummary>
        {
            new() { Iteration = 1, Phases = [Phase(GoalPhase.Coding, usage: null)] },
        };

        foreach (var phase in storedIterations[0].Phases)
            Assert.Null(PhaseBoxLabel(phase.Usage));

        Assert.Null(IterationTotalLine(GoalDetailViewBuilder.SumWorkerUsage(storedIterations[0].Phases)));
        Assert.Empty(GoalCardLines(GoalUsageBreakdown.Create(
            PhaseUsageTotals.Sum(storedIterations.SelectMany(i => i.Phases)), brain: null, review: null)));
    }

    // ── Mirrored presentation: the goal CARD with Brain / review usage ─────────

    /// <summary>The goal's stored Brain usage — a bucket no worker phase produced.</summary>
    private static UsageSummary BrainUsageBucket() => OneEntry(UsageSource.SubAgent, "model-brain", 50, 5, 2, cached: 7, cachedReported: 2, reasoning: 9, reasoningReported: 2);

    /// <summary>The goal's stored pre-execution review usage, with a <c>null</c> model.</summary>
    private static UsageSummary ReviewUsageBucket() => OneEntry(UsageSource.Compaction, null, 8, 1, 1, cachedReported: 1, reasoningReported: 1);

    /// <summary>
    /// A goal whose ONLY usage is Brain/review usage — a goal still in Planning, with no worker phase
    /// at all — still gets a non-null breakdown, so its card appears. There is no <c>Workers:</c> line
    /// (no worker phase reported) and no missing-phases note (no worker phase is missing), while the
    /// goal's worker-only total stays <c>null</c> and unchanged.
    /// </summary>
    [Fact]
    public void Build_BrainAndReviewUsageWithNoWorkerPhases_ProducesABreakdownWithoutAWorkersLine()
    {
        var fullGoal = new Goal
        {
            Id = "goal-brain-only",
            Description = "Planned and reviewed, not executed",
            BrainUsage = BrainUsageBucket(),
            ReviewUsage = ReviewUsageBucket(),
        };

        var detail = GoalDetailViewBuilder.Build(fullGoal, "goal-brain-only", pipeline: null, fullGoal, config: null);

        Assert.NotNull(detail);
        // The goal's OTHER total stays worker-only and therefore null: only the breakdown shows the
        // Brain and review figures.
        Assert.Null(detail!.UsageTotals);
        Assert.NotNull(detail.GoalUsage);
        Assert.False(detail.IncludesLiveUsage);

        Assert.Equal(
            [
                "Total: input 58 / 7 cached input / output 6 / 9 reasoning / 3 calls",
                "Brain: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls",
                "    SubAgent / model-brain: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls",
                "Pre-execution review: input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls",
                "    Compaction / (unknown model): input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls",
            ],
            GoalCardLines(detail.GoalUsage));
    }

    /// <summary>
    /// A worker phase that reported NOTHING next to Brain/review usage: the grand total still carries
    /// the missing-phases note (one worker phase's tokens are absent from it), the <c>Workers:</c>
    /// line is suppressed because no worker phase reported, and the note never appears on the Brain or
    /// review lines — it describes the WORKER phases only.
    /// </summary>
    [Fact]
    public void Build_MissingWorkerPhaseNextToBrainUsage_KeepsTheNoteOnTheTotalLineOnly()
    {
        var fullGoal = new Goal
        {
            Id = "goal-missing-worker",
            Description = "Reviewed, planning phase logged but no report",
            BrainUsage = BrainUsageBucket(),
            IterationSummaries =
            [
                new IterationSummary { Iteration = 1, Phases = [Phase(GoalPhase.Coding, usage: null)] },
            ],
        };

        var detail = GoalDetailViewBuilder.Build(fullGoal, "goal-missing-worker", pipeline: null, fullGoal, config: null);

        Assert.NotNull(detail);
        Assert.Null(detail!.UsageTotals);
        var lines = GoalCardLines(detail.GoalUsage);
        Assert.DoesNotContain(lines, l => l.StartsWith("Workers:", StringComparison.Ordinal));
        Assert.Equal(
            "Total: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls (1 worker phase(s) without usage reported)",
            lines[0]);
        Assert.DoesNotContain("without usage reported", lines[1]);
    }

    /// <summary>
    /// Worker usage next to Brain and review usage: the card's Total is the GRAND total, the Workers
    /// line repeats the worker-only figure (which the goal's <see cref="GoalDetailInfo.UsageTotals"/>
    /// still carries unchanged), and the missing-phases note travels with the grand total only.
    /// </summary>
    [Fact]
    public void Build_WorkerUsageNextToBrainAndReview_TotalIsTheGrandTotal()
    {
        var fullGoal = new Goal
        {
            Id = "goal-all-three",
            Description = "Workers plus Brain and review",
            // 1,000 + 200 input/output from the worker phase, one missing worker phase.
            BrainUsage = BrainUsageBucket(),
            ReviewUsage = ReviewUsageBucket(),
            IterationSummaries =
            [
                new IterationSummary
                {
                    Iteration = 1,
                    Phases = [Phase(GoalPhase.Coding, TwoEntryUsage()), Phase(GoalPhase.Testing, usage: null)],
                },
            ],
        };

        var detail = GoalDetailViewBuilder.Build(fullGoal, "goal-all-three", pipeline: null, fullGoal, config: null);

        Assert.NotNull(detail);
        // The worker-only total is UNCHANGED by the Brain/review usage.
        Assert.Equal(1_005, detail!.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(1, detail.UsageTotals.PhasesWithoutUsage);

        var lines = GoalCardLines(detail.GoalUsage);
        Assert.Equal(
            [
                "Total: input 1,063 / ≥48 (partial) cached input / output 213 / ≥62 (partial) reasoning / 8 calls (1 worker phase(s) without usage reported)",
                "Workers: input 1,005 / ≥41 (partial) cached input / output 207 / ≥53 (partial) reasoning / 5 calls",
                "    Agent / model-a: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls",
                "    SubAgent / (unknown model): input 5 / 11 cached input / output 7 / 13 reasoning / 2 calls",
                "Brain: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls",
                "    SubAgent / model-brain: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls",
                "Pre-execution review: input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls",
                "    Compaction / (unknown model): input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls",
            ],
            lines);
    }

    /// <summary>
    /// The SNAPSHOT goal is used when no full goal is loaded (the <c>fullGoalWithSummaries: null</c>
    /// call shape): its own Brain/review usage reaches the breakdown, because the snapshot goal comes
    /// from a store read with NO projection and therefore carries the usage columns.
    /// </summary>
    [Fact]
    public void Build_WithoutAFullGoal_FallsBackToTheSnapshotGoalsOwnUsage()
    {
        var snapshotGoal = new Goal
        {
            Id = "goal-snapshot-usage",
            Description = "Snapshot goal carrying its own usage",
            BrainUsage = BrainUsageBucket(),
        };

        var detail = GoalDetailViewBuilder.Build(
            snapshotGoal, "goal-snapshot-usage", pipeline: null, fullGoalWithSummaries: null, config: null);

        Assert.NotNull(detail);
        Assert.Equal(
            [
                "Total: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls",
                "Brain: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls",
                "    SubAgent / model-brain: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls",
            ],
            GoalCardLines(detail!.GoalUsage));
    }

    /// <summary>
    /// The STORED fallback path renders the SAME card lines from the stored iterations plus the STORED
    /// goal's own Brain/review usage, with no live marker — the page builds its breakdown from exactly
    /// those inputs.
    /// </summary>
    [Fact]
    public void StoredPath_IncludesTheStoredGoalBrainAndReviewUsage()
    {
        var storedGoal = new Goal
        {
            Id = "goal-stored-usage",
            Description = "Stored goal with usage",
            BrainUsage = BrainUsageBucket(),
            ReviewUsage = ReviewUsageBucket(),
        };
        var storedIterations = new List<IterationSummary>
        {
            new() { Iteration = 1, Phases = [Phase(GoalPhase.Coding, TwoEntryUsage())] },
        };

        var storedBreakdown = GoalUsageBreakdown.Create(
            PhaseUsageTotals.Sum(storedIterations.SelectMany(i => i.Phases)),
            storedGoal.BrainUsage,
            storedGoal.ReviewUsage);

        Assert.Equal(
            [
                "Total: input 1,063 / ≥48 (partial) cached input / output 213 / ≥62 (partial) reasoning / 8 calls",
                "Workers: input 1,005 / ≥41 (partial) cached input / output 207 / ≥53 (partial) reasoning / 5 calls",
                "    Agent / model-a: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls",
                "    SubAgent / (unknown model): input 5 / 11 cached input / output 7 / 13 reasoning / 2 calls",
                "Brain: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls",
                "    SubAgent / model-brain: input 50 / 7 cached input / output 5 / 9 reasoning / 2 calls",
                "Pre-execution review: input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls",
                "    Compaction / (unknown model): input 8 / 0 cached input / output 1 / 0 reasoning / 1 calls",
            ],
            GoalCardLines(storedBreakdown));

        // The stored path can never carry the live marker.
        Assert.DoesNotContain(
            GoalCardLines(storedBreakdown),
            l => l.Contains("includes running phase", StringComparison.Ordinal));
    }

    /// <summary>
    /// A COMPLETED worker phase record (<see cref="PhaseResult.CompletedAt"/> set) with the given
    /// persisted usage — the shape a FINISHED phase has in the live <see cref="GoalPipeline.PhaseLog"/>.
    /// </summary>
    private static PhaseResult CompletedPhase(GoalPhase name, UsageSummary? usage, int iteration = 1, int? occurrence = 1) => new()
    {
        Name = name,
        Result = PhaseOutcome.Pass,
        Iteration = iteration,
        Occurrence = occurrence,
        StartedAt = DateTime.UtcNow.AddSeconds(-5),
        CompletedAt = DateTime.UtcNow,
        Usage = usage,
    };

    /// <summary>
    /// A pipeline in <paramref name="iteration"/> whose installed plan is
    /// Coding → Testing → Review → Merging and whose current phase is <paramref name="phase"/>. The
    /// worker phase is a PREMISE of every live-usage fixture below: in Planning the live branch builds
    /// only the synthetic Planning row, so no worker row could receive live usage at all.
    /// </summary>
    private static GoalPipeline LivePipeline(Goal goal, GoalPhase phase, int iteration = 1)
    {
        var pipeline = new GoalPipeline(goal, maxRetries: 3, maxIterations: 5);
        while (pipeline.Iteration < iteration)
            pipeline.IterationBudget.TryConsume();

        pipeline.SetPlan(new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
            Reason = "live plan reason",
        });
        pipeline.AdvanceTo(phase);
        return pipeline;
    }

    // ── Mirrors of the GoalDetail.razor LIVE usage markup ──────────────────────

    /// <summary>Mirrors the phase-box label including the live marker the razor appends.</summary>
    private static string? LivePhaseBoxLabel(PhaseViewInfo phase) =>
        UsageFormat.FormatPhaseBoxLabel(phase.Usage) is { } label
            ? $"{label}{(phase.IsLiveUsage ? " (live)" : "")}"
            : null;

    /// <summary>Mirrors the phase-detail usage block, including the live wording on the total line.</summary>
    private static IReadOnlyList<string> LivePhaseDetailLines(PhaseViewInfo phase) =>
        phase.Usage is null
            ? []
            : [$"Tokens{(phase.IsLiveUsage ? " (so far, live)" : "")}: {UsageFormat.FormatUsageLine(phase.Usage.Total)}",
               .. UsageFormat.FormatEntryLines(phase.Usage)];

    /// <summary>Mirrors the "Iteration total" line including the live note the razor appends.</summary>
    private static string? LiveIterationTotalLine(IterationViewInfo iteration) =>
        iteration.UsageTotals is { } totals
            ? $"Iteration total: {UsageFormat.FormatUsageLine(totals.Usage.Total)}{UsageFormat.FormatMissingPhasesNote(totals)}" +
              $"{(iteration.IncludesLiveUsage ? " (includes running phase)" : "")}"
            : null;

    /// <summary>
    /// Mirrors the left-panel "Token Usage" card body for a BUILT view: the breakdown's lines with
    /// the view's own live flag, indented exactly as the razor indents them.
    /// </summary>
    private static IReadOnlyList<string> LiveGoalCardLines(GoalDetailInfo detail) =>
        GoalCardLines(detail.GoalUsage, detail.IncludesLiveUsage);

    /// <summary>
    /// Every usage-relevant value of a built view, as text: the per-iteration totals and live flags,
    /// every row's usage and live flag, and the goal total with its live flag. Used to prove that an
    /// omitted or explicit-<c>null</c> <c>runningTaskUsage</c> produces IDENTICAL output.
    /// </summary>
    private static List<string> UsageProjection(GoalDetailInfo detail) =>
    [
        .. detail.Iterations.Select(iteration =>
            $"iteration {iteration.Number}: totals={Describe(iteration.UsageTotals)} includesLive={iteration.IncludesLiveUsage}"),
        .. detail.Iterations.SelectMany(iteration => iteration.Phases)
            .Select(phase => $"phase {phase.Name}/{phase.RoleName} [{phase.Status}]: usage={Describe(phase.Usage)} isLive={phase.IsLiveUsage}"),
        $"goal: totals={Describe(detail.UsageTotals)} includesLive={detail.IncludesLiveUsage}",
        $"goal breakdown: {Describe(detail.GoalUsage)}",
    ];

    private static string Describe(UsageSummary? usage) =>
        usage is null ? "<none>" : $"entries={usage.Entries.Count} | {UsageFormat.FormatUsageLine(usage.Total)}";

    private static string Describe(PhaseUsageTotals.Result? totals) =>
        totals is null ? "<none>" : $"{Describe(totals.Usage)} | with={totals.PhasesWithUsage} without={totals.PhasesWithoutUsage}";

    private static string Describe(GoalUsageBreakdown? breakdown) =>
        breakdown is null
            ? "<none>"
            : $"workers={Describe(breakdown.Workers)} brain={Describe(breakdown.Brain)} review={Describe(breakdown.Review)}" +
              $" total={Describe(breakdown.Total)}";

    // ── Live running-phase usage ───────────────────────────────────────────────

    /// <summary>
    /// (a) The running entry — the LAST current-iteration entry that has not completed, carries no
    /// persisted usage and belongs to a worker phase — receives the caller's live usage and is the ONLY
    /// row marked live. The iteration and the goal totals both include it (and the iteration's own
    /// total counts it under <c>PhasesWithUsage</c>, not <c>PhasesWithoutUsage</c>), and both report
    /// <c>IncludesLiveUsage</c>.
    /// <para>
    /// The fixture is built so each selection rule is observable: the completed Coding entry (persisted
    /// usage) must keep it, the earlier unfinished Doc Writing entry must NOT be used even though it
    /// qualifies, and the non-worker Merging entry appended LAST must be ignored despite coming after
    /// the running Review entry.
    /// </para>
    /// </summary>
    [Fact]
    public void Build_RunningEntryWithLiveUsage_MarksOnlyTheLastQualifyingWorkerRow_AndIncludesItInBothTotals()
    {
        var persistedIterationOne = OneEntry(UsageSource.Agent, "model-history", 1, 2, 1);
        var completedCoding = OneEntry(UsageSource.Agent, "model-completed-coding", 10, 20, 1);
        var liveUsage = OneEntry(UsageSource.SubAgent, "model-live", 1_000, 2_000, 3, cached: 30, cachedReported: 1);

        var fullGoal = WorkerPhaseGoal(Phase(GoalPhase.Coding, persistedIterationOne));
        var lightGoal = new Goal { Id = "goal-usage", Description = "Goal with usage" };
        var pipeline = LivePipeline(lightGoal, GoalPhase.Review, iteration: 2);

        // PREMISES: a real worker phase with an installed plan, and NO summary for the live iteration
        // (otherwise the live branch would be suppressed for that number and nothing could be marked).
        Assert.Equal(2, pipeline.Iteration);
        Assert.Equal(GoalPhase.Review, pipeline.Phase);
        Assert.DoesNotContain(pipeline.CompletedIterationSummaries, s => s.Iteration == 2);

        pipeline.PhaseLog.Add(CompletedPhase(GoalPhase.Coding, completedCoding, iteration: 2));
        pipeline.PhaseLog.Add(Phase(GoalPhase.DocWriting, usage: null, iteration: 2));
        pipeline.PhaseLog.Add(Phase(GoalPhase.Review, usage: null, iteration: 2));
        pipeline.PhaseLog.Add(Phase(GoalPhase.Merging, usage: null, iteration: 2));

        var detail = GoalDetailViewBuilder.Build(
            lightGoal, "goal-usage", pipeline, fullGoal, config: null, runningTaskUsage: liveUsage);

        Assert.NotNull(detail);
        var iterations = detail!.Iterations;
        Assert.Equal([1, 2], iterations.Select(i => i.Number));

        var firstIteration = Assert.Single(iterations, i => i.Number == 1);
        Assert.False(firstIteration.IncludesLiveUsage);
        Assert.False(Assert.Single(firstIteration.Phases, p => p.Name == "Coding").IsLiveUsage);

        var liveIteration = Assert.Single(iterations, i => i.Number == 2);
        Assert.True(liveIteration.IsCurrent);

        // The completed row keeps its PERSISTED usage and is not marked live.
        var codingRow = Assert.Single(liveIteration.Phases, p => p.Name == "Coding");
        Assert.Same(completedCoding, codingRow.Usage);
        Assert.False(codingRow.IsLiveUsage);

        // An unfinished WORKER row that is NOT the last qualifying one stays untouched.
        var docWritingRow = Assert.Single(liveIteration.Phases, p => p.Name == "Doc Writing");
        Assert.Null(docWritingRow.Usage);
        Assert.False(docWritingRow.IsLiveUsage);

        // THE RUNNING ROW: the live usage, marked live.
        var runningRow = Assert.Single(liveIteration.Phases, p => p.Name == "Review");
        Assert.Same(liveUsage, runningRow.Usage);
        Assert.True(runningRow.IsLiveUsage);

        // …and the non-worker row appended after it is never the running entry.
        var mergingRow = Assert.Single(liveIteration.Phases, p => p.Name == "Merging");
        Assert.Null(mergingRow.Usage);
        Assert.False(mergingRow.IsLiveUsage);
        Assert.Null(Assert.Single(liveIteration.Phases, p => p.Name == "Planning").Usage);

        // ITERATION TOTAL: the running entry counts as REPORTED (with the live usage) and the truly
        // unreported Doc Writing row is the only one counted as missing. The non-worker Merging row is
        // in neither count.
        Assert.NotNull(liveIteration.UsageTotals);
        Assert.True(liveIteration.IncludesLiveUsage);
        Assert.Equal(2, liveIteration.UsageTotals!.PhasesWithUsage);
        Assert.Equal(1, liveIteration.UsageTotals.PhasesWithoutUsage);
        Assert.Equal(completedCoding.Total.InputTokens + liveUsage.Total.InputTokens, liveIteration.UsageTotals.Usage.Total.InputTokens);
        Assert.Equal(completedCoding.Total.OutputTokens + liveUsage.Total.OutputTokens, liveIteration.UsageTotals.Usage.Total.OutputTokens);
        Assert.Contains(liveIteration.UsageTotals.Usage.Entries, e => e.Model == "model-live");

        // GOAL TOTAL: the summary's phase, the completed phase and the running phase, each counted once.
        Assert.NotNull(detail.UsageTotals);
        Assert.True(detail.IncludesLiveUsage);
        Assert.Equal(1 + 10 + 1_000, detail.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(2 + 20 + 2_000, detail.UsageTotals.Usage.Total.OutputTokens);
        Assert.Equal(3, detail.UsageTotals.PhasesWithUsage);
        Assert.Equal(1, detail.UsageTotals.PhasesWithoutUsage);

        // RENDERED TEXT: the live markers and the live WORDING, through the same helpers the razor uses.
        Assert.Equal("30 tok", LivePhaseBoxLabel(codingRow));
        Assert.Equal("3.0K tok (live)", LivePhaseBoxLabel(runningRow));
        Assert.Equal(
            [
                "Tokens (so far, live): input 1,000 / ≥30 (partial) cached input / output 2,000 / unknown reasoning / 3 calls",
                "SubAgent / model-live: input 1,000 / ≥30 (partial) cached input / output 2,000 / unknown reasoning / 3 calls",
            ],
            LivePhaseDetailLines(runningRow));
        Assert.Equal(
            "Iteration total: input 1,010 / ≥30 (partial) cached input / output 2,020 / unknown reasoning / 4 calls" +
            " (1 worker phase(s) without usage reported) (includes running phase)",
            LiveIterationTotalLine(liveIteration));
        Assert.Equal(
            "Total: input 1,011 / ≥30 (partial) cached input / output 2,022 / unknown reasoning / 5 calls" +
            " (1 worker phase(s) without usage reported) (includes running phase)",
            LiveGoalCardLines(detail)[0]);
        // The live bucket's entry line is a DEPTH-1 line, so the card indents it.
        Assert.Contains(
            "    SubAgent / model-live: input 1,000 / ≥30 (partial) cached input / output 2,000 / unknown reasoning / 3 calls",
            LiveGoalCardLines(detail));
    }

    /// <summary>
    /// A live iteration with NO running worker entry — every logged phase is either completed or
    /// already carries persisted usage — applies NO live usage: the completed rows keep their own
    /// reports, nothing is marked live, and neither the iteration nor the goal reports
    /// <c>IncludesLiveUsage</c>.
    /// </summary>
    [Fact]
    public void Build_RunningTaskUsageWithoutARunningWorkerEntry_IsIgnoredEntirely()
    {
        var completedCoding = OneEntry(UsageSource.Agent, "model-completed-coding", 10, 20, 1);
        var liveUsage = OneEntry(UsageSource.SubAgent, "model-never-used", 900_000, 800_000, 1);

        var lightGoal = new Goal { Id = "goal-no-running-entry", Description = "No running entry" };
        var pipeline = LivePipeline(lightGoal, GoalPhase.Review);
        pipeline.PhaseLog.Add(CompletedPhase(GoalPhase.Coding, completedCoding));
        pipeline.PhaseLog.Add(CompletedPhase(GoalPhase.Review, usage: null));

        var detail = GoalDetailViewBuilder.Build(
            lightGoal, "goal-no-running-entry", pipeline, fullGoalWithSummaries: null, config: null, runningTaskUsage: liveUsage);

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        Assert.False(iteration.IncludesLiveUsage);
        Assert.False(detail.IncludesLiveUsage);
        Assert.NotNull(iteration.UsageTotals);
        Assert.Equal(10, iteration.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(1, iteration.UsageTotals.PhasesWithUsage);
        Assert.Equal(1, iteration.UsageTotals.PhasesWithoutUsage);
        Assert.DoesNotContain(iteration.UsageTotals.Usage.Entries, e => e.Model == "model-never-used");
        Assert.All(iteration.Phases, p => Assert.False(p.IsLiveUsage));
        Assert.Equal(10, detail.UsageTotals!.Usage.Total.InputTokens);
        Assert.DoesNotContain(detail.UsageTotals.Usage.Entries, e => e.Model == "model-never-used");
    }

    /// <summary>
    /// (b) The COMPLETION BOUNDARY: the running entry already carries a PERSISTED usage while the
    /// pipeline still points at it. The persisted report is never overwritten by the live figure — the
    /// row keeps it, is not marked live, and neither total counts the live usage, so nothing is double
    /// counted.
    /// </summary>
    [Fact]
    public void Build_RunningEntryAlreadyCarryingPersistedUsage_IsNeverOverwrittenAndNeverDoubleCounted()
    {
        var persistedUsage = OneEntry(UsageSource.Agent, "model-persisted", 7, 9, 1);
        var liveUsage = OneEntry(UsageSource.SubAgent, "model-live", 900_000, 800_000, 1);

        var lightGoal = new Goal { Id = "goal-boundary", Description = "Completion boundary" };
        var pipeline = LivePipeline(lightGoal, GoalPhase.Coding);

        // The completion-boundary shape: the entry is STILL the pipeline's current one (not completed)
        // but its worker result already wrote a usage report.
        var runningEntry = Phase(GoalPhase.Coding, persistedUsage);
        pipeline.PhaseLog.Add(runningEntry);

        var detail = GoalDetailViewBuilder.Build(
            lightGoal, "goal-boundary", pipeline, fullGoalWithSummaries: null, config: null, runningTaskUsage: liveUsage);

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);

        var codingRow = Assert.Single(iteration.Phases, p => p.Name == "Coding");
        Assert.Same(persistedUsage, codingRow.Usage);
        Assert.False(codingRow.IsLiveUsage);

        Assert.False(iteration.IncludesLiveUsage);
        Assert.False(detail.IncludesLiveUsage);
        Assert.NotNull(iteration.UsageTotals);
        Assert.Equal(1, iteration.UsageTotals!.PhasesWithUsage);
        Assert.Equal(0, iteration.UsageTotals.PhasesWithoutUsage);
        Assert.Equal(7, iteration.UsageTotals.Usage.Total.InputTokens);
        Assert.Equal(9, iteration.UsageTotals.Usage.Total.OutputTokens);
        Assert.DoesNotContain(iteration.UsageTotals.Usage.Entries, e => e.Model == "model-live");
        Assert.Equal(7, detail.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(9, detail.UsageTotals.Usage.Total.OutputTokens);
        Assert.DoesNotContain(detail.UsageTotals.Usage.Entries, e => e.Model == "model-live");

        // The pipeline's own entry is untouched: the persisted report is still the SAME instance.
        Assert.Same(persistedUsage, Assert.Single(pipeline.PhaseLog).Usage);
    }

    /// <summary>
    /// (c) Without live usage — the parameter omitted entirely AND passed explicitly as <c>null</c> —
    /// every usage-relevant value of the built view is IDENTICAL, and every live flag stays
    /// <c>false</c>: the running row shows no usage and both totals are persisted-data-only.
    /// </summary>
    [Fact]
    public void Build_NoRunningTaskUsage_ProducesIdenticalOutputWithAllLiveFlagsFalse()
    {
        var persistedIterationOne = OneEntry(UsageSource.Agent, "model-history", 1, 2, 1);
        var completedCoding = OneEntry(UsageSource.Agent, "model-completed-coding", 10, 20, 1);

        var fullGoal = WorkerPhaseGoal(Phase(GoalPhase.Coding, persistedIterationOne));
        var lightGoal = new Goal { Id = "goal-usage", Description = "Goal with usage" };

        GoalPipeline NewFixture()
        {
            var pipeline = LivePipeline(lightGoal, GoalPhase.Review, iteration: 2);
            pipeline.PhaseLog.Add(CompletedPhase(GoalPhase.Coding, completedCoding, iteration: 2));
            pipeline.PhaseLog.Add(Phase(GoalPhase.DocWriting, usage: null, iteration: 2));
            pipeline.PhaseLog.Add(Phase(GoalPhase.Review, usage: null, iteration: 2));
            return pipeline;
        }

        // Today's call shape (the parameter omitted entirely)…
        var omitted = GoalDetailViewBuilder.Build(lightGoal, "goal-usage", NewFixture(), fullGoal, config: null);
        // …versus the same call with the new parameter passed explicitly as null.
        var explicitNull = GoalDetailViewBuilder.Build(
            lightGoal, "goal-usage", NewFixture(), fullGoal, config: null, runningTaskUsage: null);

        Assert.NotNull(omitted);
        Assert.NotNull(explicitNull);
        Assert.Equal(UsageProjection(omitted!), UsageProjection(explicitNull!));

        // The running row is present but carries nothing, and no flag anywhere is set.
        var iteration = Assert.Single(omitted!.Iterations, i => i.Number == 2);
        Assert.Null(Assert.Single(iteration.Phases, p => p.Name == "Review").Usage);
        Assert.All(omitted.Iterations, i => Assert.False(i.IncludesLiveUsage));
        Assert.All(omitted.Iterations.SelectMany(i => i.Phases), p => Assert.False(p.IsLiveUsage));
        Assert.False(omitted.IncludesLiveUsage);

        // The totals are the persisted ones: the summary's phase and the completed live-iteration phase
        // count, the two unreported rows do not.
        Assert.NotNull(iteration.UsageTotals);
        Assert.Equal(10, iteration.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(20, iteration.UsageTotals.Usage.Total.OutputTokens);
        Assert.Equal(1, iteration.UsageTotals.PhasesWithUsage);
        Assert.Equal(2, iteration.UsageTotals.PhasesWithoutUsage);
        Assert.Equal("Iteration total: input 10 / unknown cached input / output 20 / unknown reasoning / 1 calls" +
                     " (2 worker phase(s) without usage reported)",
            LiveIterationTotalLine(iteration));
        Assert.Equal(1 + 10, omitted.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(2 + 20, omitted.UsageTotals.Usage.Total.OutputTokens);
        Assert.Equal(2, omitted.UsageTotals.PhasesWithUsage);
        Assert.Equal(2, omitted.UsageTotals.PhasesWithoutUsage);
        Assert.Equal(
            "Total: input 11 / unknown cached input / output 22 / unknown reasoning / 2 calls" +
            " (2 worker phase(s) without usage reported)",
            LiveGoalCardLines(omitted)[0]);
    }

    /// <summary>
    /// (d) The live usage lands ONLY on the live iteration's running row: the live iteration's COMPLETED
    /// entry and every row of a SUMMARISED iteration keep their own (persisted or absent) reports, and
    /// neither of those totals reports <c>IncludesLiveUsage</c>.
    /// </summary>
    [Fact]
    public void Build_LiveUsage_IsNeverAppliedToCompletedEntriesOrSummarisedIterations()
    {
        var summaryUsage = OneEntry(UsageSource.Agent, "model-summary", 1, 2, 1);
        var completedTesting = OneEntry(UsageSource.Agent, "model-completed-testing", 300, 400, 1);
        var liveUsage = OneEntry(UsageSource.SubAgent, "model-live", 5_000, 6_000, 1);

        var fullGoal = WorkerPhaseGoal(Phase(GoalPhase.Coding, summaryUsage));
        var lightGoal = new Goal { Id = "goal-scope", Description = "Live usage scope" };
        var pipeline = LivePipeline(lightGoal, GoalPhase.Testing, iteration: 2);

        pipeline.PhaseLog.Add(CompletedPhase(GoalPhase.Coding, completedTesting, iteration: 2));
        pipeline.PhaseLog.Add(Phase(GoalPhase.Testing, usage: null, iteration: 2));

        var detail = GoalDetailViewBuilder.Build(
            lightGoal, "goal-scope", pipeline, fullGoal, config: null, runningTaskUsage: liveUsage);

        Assert.NotNull(detail);

        // THE SUMMARISED ITERATION is untouched: its row keeps the persisted report, its total is its
        // own and it reports no live usage.
        var summarised = Assert.Single(detail!.Iterations, i => i.Number == 1);
        Assert.False(summarised.IncludesLiveUsage);
        var summarisedCoding = Assert.Single(summarised.Phases, p => p.Name == "Coding");
        Assert.Same(summaryUsage, summarisedCoding.Usage);
        Assert.False(summarisedCoding.IsLiveUsage);
        Assert.Equal(1, summarised.UsageTotals!.Usage.Total.InputTokens);
        Assert.DoesNotContain(summarised.UsageTotals.Usage.Entries, e => e.Model == "model-live");

        // THE LIVE ITERATION: the completed row keeps its own report and is not live; only the running
        // Testing row is.
        var liveIteration = Assert.Single(detail.Iterations, i => i.Number == 2);
        var completedCodingRow = Assert.Single(liveIteration.Phases, p => p.Name == "Coding");
        Assert.Same(completedTesting, completedCodingRow.Usage);
        Assert.False(completedCodingRow.IsLiveUsage);
        var runningRow = Assert.Single(liveIteration.Phases, p => p.Name == "Testing");
        Assert.Same(liveUsage, runningRow.Usage);
        Assert.True(runningRow.IsLiveUsage);

        Assert.True(liveIteration.IncludesLiveUsage);
        Assert.Equal(300 + 5_000, liveIteration.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(2, liveIteration.UsageTotals.PhasesWithUsage);

        // The goal total unions the three distinct contributors exactly once each.
        Assert.True(detail.IncludesLiveUsage);
        Assert.Equal(1 + 300 + 5_000, detail.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(3, detail.UsageTotals.PhasesWithUsage);
    }

    /// <summary>
    /// (e) NO MUTATION: after <see cref="GoalDetailViewBuilder.Build"/> applied a live usage, every
    /// entry in the pipeline's own <see cref="GoalPipeline.PhaseLog"/> is the SAME instance with the
    /// SAME (null) usage and the same completion state — the live figure reached the view models through
    /// a detached substitute only.
    /// </summary>
    [Fact]
    public void Build_RunningEntryWithLiveUsage_LeavesThePipelinePhaseLogUnmutated()
    {
        var liveUsage = OneEntry(UsageSource.SubAgent, "model-live", 12, 34, 1);

        var lightGoal = new Goal { Id = "goal-no-mutation", Description = "No mutation" };
        var pipeline = LivePipeline(lightGoal, GoalPhase.Review, iteration: 2);

        var completedEntry = CompletedPhase(GoalPhase.Coding, OneEntry(UsageSource.Agent, "model-completed", 1, 2, 1), iteration: 2);
        var runningEntry = Phase(GoalPhase.Review, usage: null, iteration: 2);
        pipeline.PhaseLog.Add(completedEntry);
        pipeline.PhaseLog.Add(runningEntry);
        var snapshotBefore = pipeline.PhaseLog.ToList();

        var detail = GoalDetailViewBuilder.Build(
            lightGoal, "goal-no-mutation", pipeline, fullGoalWithSummaries: null, config: null, runningTaskUsage: liveUsage);

        Assert.NotNull(detail);
        Assert.Equal(snapshotBefore, pipeline.PhaseLog);
        Assert.Same(runningEntry, pipeline.PhaseLog[1]);
        Assert.Null(runningEntry.Usage);
        Assert.Null(runningEntry.CompletedAt);
        // The COMPLETED entry still carries exactly the persisted report it was built with.
        Assert.Same(snapshotBefore[0].Usage, completedEntry.Usage);
        Assert.NotNull(completedEntry.CompletedAt);

        // The running row DID receive the live usage — which is only possible because the row holds a
        // detached summary, since the entry itself still reports none.
        var runningRow = Assert.Single(Assert.Single(detail!.Iterations).Phases, p => p.Name == "Review");
        Assert.Same(liveUsage, runningRow.Usage);
        Assert.Null(pipeline.PhaseLog[1].Usage);
    }

    // ── Live usage cannot reach the stored (no-pipeline) path or a summarised current iteration ──

    /// <summary>
    /// The STORED (no-pipeline) path can never show live usage: with <c>pipeline: null</c> there is no
    /// live iteration and no running entry at all, so a caller-supplied live usage is ignored entirely
    /// and the stored totals are persisted-data-only with every flag <c>false</c>.
    /// </summary>
    [Fact]
    public void Build_StoredPathWithoutAPipeline_NeverAppliesLiveUsage()
    {
        var storedUsage = OneEntry(UsageSource.Agent, "model-stored", 5, 6, 1);
        var liveUsage = OneEntry(UsageSource.SubAgent, "model-live", 900_000, 800_000, 1);

        var fullGoal = WorkerPhaseGoal(Phase(GoalPhase.Coding, storedUsage));
        var withoutLiveUsage = GoalDetailViewBuilder.Build(fullGoal, "goal-usage", pipeline: null, fullGoal, config: null);
        var withLiveUsage = GoalDetailViewBuilder.Build(
            fullGoal, "goal-usage", pipeline: null, fullGoal, config: null, runningTaskUsage: liveUsage);

        Assert.NotNull(withoutLiveUsage);
        Assert.NotNull(withLiveUsage);

        // The stored path ignores the live figure COMPLETELY: every value is the same as before.
        Assert.Equal(UsageProjection(withoutLiveUsage!), UsageProjection(withLiveUsage!));
        Assert.False(withLiveUsage!.IncludesLiveUsage);
        Assert.All(withLiveUsage.Iterations.SelectMany(i => i.Phases), p => Assert.False(p.IsLiveUsage));
        Assert.DoesNotContain(withLiveUsage.UsageTotals!.Usage.Entries, e => e.Model == "model-live");
    }

    /// <summary>
    /// A SUMMARISED current iteration suppresses the live branch for its number, so a caller-supplied
    /// live usage cannot reach it either: the iteration keeps its summary's phases and total, no row is
    /// marked live, and the goal total is unchanged.
    /// </summary>
    [Fact]
    public void Build_CurrentIterationAlreadySummarised_IgnoresCallerSuppliedLiveUsage()
    {
        var summaryUsage = OneEntry(UsageSource.Agent, "model-summary", 3, 4, 1);
        var liveUsage = OneEntry(UsageSource.SubAgent, "model-live", 900_000, 800_000, 1);

        var lightGoal = new Goal { Id = "goal-summarised-live", Description = "Summarised current iteration" };
        var fullGoal = new Goal
        {
            Id = "goal-summarised-live",
            Description = "Summarised current iteration",
            IterationSummaries = [new IterationSummary { Iteration = 1, Phases = [Phase(GoalPhase.Coding, summaryUsage)] }],
        };

        var pipeline = new GoalPipeline(lightGoal, maxRetries: 3, maxIterations: 5);
        Assert.Equal(1, pipeline.Iteration);
        pipeline.PhaseLog.Add(Phase(GoalPhase.Coding, usage: null, iteration: 1));

        var detail = GoalDetailViewBuilder.Build(
            lightGoal, "goal-summarised-live", pipeline, fullGoal, config: null, runningTaskUsage: liveUsage);

        Assert.NotNull(detail);
        var iteration = Assert.Single(detail!.Iterations);
        Assert.False(iteration.IsCurrent);
        Assert.False(iteration.IncludesLiveUsage);
        Assert.False(detail.IncludesLiveUsage);
        Assert.All(iteration.Phases, p => Assert.False(p.IsLiveUsage));
        Assert.Equal(3, iteration.UsageTotals!.Usage.Total.InputTokens);
        Assert.Equal(1, iteration.UsageTotals.PhasesWithUsage);
        Assert.Equal(0, iteration.UsageTotals.PhasesWithoutUsage);
        Assert.DoesNotContain(iteration.UsageTotals.Usage.Entries, e => e.Model == "model-live");
        Assert.Equal(3, detail.UsageTotals!.Usage.Total.InputTokens);
        Assert.DoesNotContain(detail.UsageTotals.Usage.Entries, e => e.Model == "model-live");
    }

    // ── Markup contract: the live markers really are wired into GoalDetail.razor ────────────────    //
    // No bUnit: like the other page-contract tests in this repo, these read the ACTUAL razor source
    // and scope every assertion to the region it belongs to, so a marker removed from the _detail
    // path (while some other occurrence still exists elsewhere) cannot pass.

    /// <summary>
    /// Reads the actual <c>GoalDetail.razor</c> source by walking up from the current directory to
    /// the repo root (identified by the presence of a <c>*.slnx</c> file).
    /// </summary>
    private static string ReadGoalDetailRazorSource()
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }

        Assert.NotNull(repoRoot);
        var razorPath = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", "GoalDetail.razor");
        Assert.True(File.Exists(razorPath), $"Source file not found at {razorPath}");
        return File.ReadAllText(razorPath);
    }

    /// <summary>Extracts the region of <paramref name="source"/> between two markers, exclusively.</summary>
    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found in GoalDetail.razor: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found after the start marker in GoalDetail.razor: {endMarker}");
        return source[start..end];
    }

    /// <summary>
    /// The live markers exist in the <c>_detail</c> iteration region and nowhere in the stored region:
    /// the phase box appends <c>(live)</c> to the SHARED box label, the phase detail uses the
    /// "so far, live" wording, and the iteration total line appends the running-phase note — all
    /// driven by <c>phase.IsLiveUsage</c> / <c>iter.IncludesLiveUsage</c>. The stored iteration block
    /// (which reads persisted <see cref="PhaseResult"/> records that can never be live) carries none of
    /// them.
    /// </summary>
    [Fact]
    public void RazorSource_TheDetailPathCarriesTheLiveMarkers_AndTheStoredPathDoesNot()
    {
        var source = ReadGoalDetailRazorSource();

        const string detailIterations = "@* _detail iterations *@";
        const string storedIterations = "@* _storedIterations (from SQLite) *@";

        var detailRegion = ExtractBetween(source, detailIterations, storedIterations);

        // PHASE BOX: the shared label helper is still the source of the label, and the live marker is
        // appended from the row's own flag.
        Assert.Contains("UsageFormat.FormatPhaseBoxLabel(phase.Usage)", detailRegion);
        Assert.Contains("@phaseUsageLabel@(phase.IsLiveUsage ? \" (live)\" : \"\")", detailRegion);

        // PHASE DETAIL: the same helper lines, with the live wording on the total line only.
        Assert.Contains("<div>Tokens@(phase.IsLiveUsage ? \" (so far, live)\" : \"\"): @UsageFormat.FormatUsageLine(phase.Usage.Total)</div>", detailRegion);
        Assert.Contains("@foreach (var usageEntryLine in UsageFormat.FormatEntryLines(phase.Usage))", detailRegion);

        // ITERATION TOTAL: the running-phase note, appended after the shared missing-phases note.
        Assert.Contains("@(iter.IncludesLiveUsage ? \" (includes running phase)\" : \"\")", detailRegion);

        var storedRegion = source[source.IndexOf(storedIterations, StringComparison.Ordinal)..];
        Assert.DoesNotContain("IsLiveUsage", storedRegion);
        Assert.DoesNotContain("IncludesLiveUsage", storedRegion);
        Assert.DoesNotContain("(live)", storedRegion);
        Assert.DoesNotContain("(so far, live)", storedRegion);
        Assert.DoesNotContain("(includes running phase)", storedRegion);
    }

    /// <summary>
    /// The goal card renders the BREAKDOWN through the shared line helper and passes the running-phase
    /// flag from the <c>_detail</c> model ONLY — never from the stored path, which reads completed
    /// summaries that can never carry a live figure. The stored path builds its own breakdown from the
    /// stored iterations and the stored goal's own Brain/review usage.
    /// </summary>
    [Fact]
    public void RazorSource_GoalCardRendersTheBreakdownWithTheDetailFlagPassedToTheSharedHelper()
    {
        var source = ReadGoalDetailRazorSource();

        var cardRegion = ExtractBetween(
            source,
            "@* Token Usage — the goal breakdown (workers + Brain + pre-execution review), rendered whenever any of the three reported *@",
            "@* RIGHT PANEL — iterations *@");

        // The live flag comes from _detail and nowhere else, and it is handed to the shared line helper
        // — which is where the "(includes running phase)" marker is composed, for the worker-derived
        // lines only. The razor composes no total line (and no missing-phases note) of its own: a
        // hand-rolled one could drift from the section get_goal prints.
        Assert.Contains("goalIncludesLiveUsage = _detail.IncludesLiveUsage;", cardRegion);
        Assert.Contains("UsageFormat.FormatGoalUsageLines(usageBreakdown, goalIncludesLiveUsage)", cardRegion);
        Assert.DoesNotContain("FormatUsageLine", cardRegion);
        Assert.DoesNotContain("FormatMissingPhasesNote", cardRegion);
        Assert.DoesNotContain("(includes running phase)", cardRegion);
        Assert.DoesNotContain("_storedGoal?.IncludesLiveUsage", cardRegion);

        // The _detail path reads the builder's breakdown; the stored path builds one from the stored
        // iterations plus the stored goal's OWN Brain and review usage with includesLiveUsage: false.
        Assert.Contains("goalUsageBreakdown = _detail.GoalUsage;", cardRegion);
        Assert.Contains("GoalUsageBreakdown.Create(", cardRegion);
        Assert.Contains("PhaseUsageTotals.Sum(_storedIterations.SelectMany(i => i.Phases))", cardRegion);
        Assert.Contains("storedGoalForUsage.BrainUsage", cardRegion);
        Assert.Contains("storedGoalForUsage.ReviewUsage", cardRegion);

        // Depth-1 lines are indented; depth-0 lines are not.
        Assert.Contains("style=\"@(depth == 1 ? \"padding-left:1rem\" : null)\"", cardRegion);
    }
}
