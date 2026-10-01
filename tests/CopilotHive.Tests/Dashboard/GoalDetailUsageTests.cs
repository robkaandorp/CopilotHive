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

    /// <summary>Mirrors the left-panel "Token Usage" card body: total line plus entry lines.</summary>
    private static IReadOnlyList<string> GoalCardLines(PhaseUsageTotals.Result? totals) =>
        totals is null
            ? []
            : [$"Total: {UsageFormat.FormatUsageLine(totals.Usage.Total)}{UsageFormat.FormatMissingPhasesNote(totals)}",
               .. UsageFormat.FormatEntryLines(totals.Usage)];

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

        // GOAL CARD: the same total, plus the same two entry lines.
        Assert.Equal(
            [
                "Total: input 1,005 / ≥41 (partial) cached input / output 207 / ≥53 (partial) reasoning / 5 calls",
                "Agent / model-a: input 1,000 / ≥30 (partial) cached input / output 200 / unknown reasoning / 3 calls",
                "SubAgent / (unknown model): input 5 / 11 cached input / output 7 / 13 reasoning / 2 calls",
            ],
            GoalCardLines(detail.UsageTotals));
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
        Assert.Empty(GoalCardLines(detail.UsageTotals));
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

        // Stored path: the page sums the stored phase records through the builder's shared helper.
        var storedIterationTotals = GoalDetailViewBuilder.SumWorkerUsage(storedIteration.Phases);
        var storedGoalTotals = GoalDetailViewBuilder.SumWorkerUsage(storedIterations.SelectMany(i => i.Phases));

        var storedPhase = Assert.Single(storedIteration.Phases, p => p.Name == GoalPhase.Coding);

        Assert.Equal(PhaseBoxLabel(detailIteration.Phases.Single(p => p.Name == "Coding").Usage), PhaseBoxLabel(storedPhase.Usage));
        Assert.Equal("1.2K tok", PhaseBoxLabel(storedPhase.Usage));
        Assert.Equal(PhaseDetailLines(detailIteration.Phases.Single(p => p.Name == "Coding").Usage), PhaseDetailLines(storedPhase.Usage));
        Assert.Equal(IterationTotalLine(detailIteration.UsageTotals), IterationTotalLine(storedIterationTotals));
        Assert.Equal(GoalCardLines(detail.UsageTotals), GoalCardLines(storedGoalTotals));

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
        Assert.Empty(GoalCardLines(GoalDetailViewBuilder.SumWorkerUsage(storedIterations.SelectMany(i => i.Phases))));
    }
}
