using System.Text.Json;

using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;

namespace CopilotHive.Tests;

/// <summary>
/// Shared fixtures for the phase-usage slice: builders for multi-entry, multi-source/model
/// <see cref="UsageSummary"/> values whose numbers are all DISTINCT, plus equivalence assertions.
/// Kept in one place so the stamping tests, the totals tests and the formatting tests exercise the
/// same shapes.
/// </summary>
internal static class PhaseUsageFixtures
{
    /// <summary>
    /// Builds a three-entry summary spanning three sources, one of which has a <c>null</c> model,
    /// with every count distinct and every completeness case represented:
    /// <list type="bullet">
    ///   <item><c>Agent/model-a</c>: 7 calls — cached input reported 5× (PARTIAL), reasoning 7× (complete);</item>
    ///   <item><c>SubAgent/null</c>: 9 calls — cached input reported 0× (UNKNOWN), reasoning 4× (partial);</item>
    ///   <item><c>Compaction/model-c</c>: 2 calls — both optional categories complete.</item>
    /// </list>
    /// </summary>
    internal static UsageSummary BuildFullUsage() => new(
    [
        new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage
        {
            InputTokens = 101,
            OutputTokens = 202,
            CachedInputTokens = 303,
            ReasoningTokens = 404,
            Calls = 7,
            CachedInputReportedCalls = 5,
            ReasoningReportedCalls = 7,
            Estimated = new EstimatedTokenBreakdown
            {
                SystemPrompt = 11,
                ToolDefinitions = 12,
                UserText = 13,
                AssistantText = 14,
                ToolCalls = 15,
                ToolResults = 16,
                Reasoning = 17,
                Images = 18,
                OutputText = 19,
                OutputToolCalls = 20,
                OutputReasoning = 21,
                InputEstimatedCalls = 22,
                OutputEstimatedCalls = 23,
            },
        }),
        new UsageEntry(UsageSource.SubAgent, null, new TokenUsage
        {
            InputTokens = 1_111,
            OutputTokens = 2_222,
            CachedInputTokens = 3_333,
            ReasoningTokens = 4_444,
            Calls = 9,
            CachedInputReportedCalls = 0,
            ReasoningReportedCalls = 4,
            Estimated = new EstimatedTokenBreakdown
            {
                SystemPrompt = 31,
                ToolDefinitions = 32,
                UserText = 33,
                AssistantText = 34,
                ToolCalls = 35,
                ToolResults = 36,
                Reasoning = 37,
                Images = 38,
                OutputText = 39,
                OutputToolCalls = 40,
                OutputReasoning = 41,
                InputEstimatedCalls = 42,
                OutputEstimatedCalls = 43,
            },
        }),
        new UsageEntry(UsageSource.Compaction, "model-c", new TokenUsage
        {
            InputTokens = 51,
            OutputTokens = 62,
            CachedInputTokens = 73,
            ReasoningTokens = 84,
            Calls = 2,
            CachedInputReportedCalls = 2,
            ReasoningReportedCalls = 2,
            Estimated = new EstimatedTokenBreakdown
            {
                SystemPrompt = 91,
                ToolDefinitions = 92,
                UserText = 93,
                AssistantText = 94,
                ToolCalls = 95,
                ToolResults = 96,
                Reasoning = 97,
                Images = 98,
                OutputText = 99,
                OutputToolCalls = 100,
                OutputReasoning = 101,
                InputEstimatedCalls = 102,
                OutputEstimatedCalls = 103,
            },
        }),
    ]);

    /// <summary>
    /// The exact ALL-13 estimated properties plus BOTH coverage counters of one bucket. Declared as
    /// an independent <c>readonly</c> constant — deliberately NOT derived from
    /// <see cref="BuildFullUsage"/> — so that changing a fixture value makes the round-trip
    /// assertions FAIL rather than silently tracking the change.
    /// </summary>
    internal sealed record ExpectedEstimate(
        long SystemPrompt, long ToolDefinitions, long UserText, long AssistantText,
        long ToolCalls, long ToolResults, long Reasoning, long Images,
        long OutputText, long OutputToolCalls, long OutputReasoning,
        int InputEstimatedCalls, int OutputEstimatedCalls);

    /// <summary>
    /// The exact expected values of one bucket: the four token sums, the three call counters and the
    /// full estimated breakdown.
    /// </summary>
    internal sealed record ExpectedEntry(
        long Input, long Output, long Cached, long Reasoning,
        int Calls, int CachedReported, int ReasoningReported,
        ExpectedEstimate Estimated);

    internal static readonly ExpectedEntry AgentExpected = new(
        101, 202, 303, 404, 7, 5, 7,
        new ExpectedEstimate(11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23));

    internal static readonly ExpectedEntry SubAgentExpected = new(
        1_111, 2_222, 3_333, 4_444, 9, 0, 4,
        new ExpectedEstimate(31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43));

    internal static readonly ExpectedEntry CompactionExpected = new(
        51, 62, 73, 84, 2, 2, 2,
        new ExpectedEstimate(91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103));

    /// <summary>
    /// Asserts that <paramref name="actual"/> carries EXACTLY the counts of
    /// <see cref="BuildFullUsage"/>, entry by entry, including ALL 13 estimated properties, BOTH
    /// coverage counters (<c>InputEstimatedCalls</c> / <c>OutputEstimatedCalls</c>) and both
    /// reported-calls counters. Comparisons are per field: the SharpCoder types have no value
    /// equality, so a whole-object comparison would silently pass. Every assertion this helper makes
    /// is exercised by <c>PhaseUsagePersistenceTests.AssertFullUsage_IsSensitiveToEveryEstimatedProperty</c>.
    /// </summary>
    internal static void AssertFullUsage(UsageSummary? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(3, actual!.Entries.Count);

        var agent = Assert.Single(actual.Entries, e => e.Source == UsageSource.Agent);
        Assert.Equal("model-a", agent.Model);
        AssertEntry(agent.Usage, AgentExpected);

        var subAgent = Assert.Single(actual.Entries, e => e.Source == UsageSource.SubAgent);
        // The null model must survive as null — not as the empty string.
        Assert.Null(subAgent.Model);
        AssertEntry(subAgent.Usage, SubAgentExpected);

        var compaction = Assert.Single(actual.Entries, e => e.Source == UsageSource.Compaction);
        Assert.Equal("model-c", compaction.Model);
        AssertEntry(compaction.Usage, CompactionExpected);
    }

    /// <summary>
    /// Asserts the aggregate <c>Total</c> of one <see cref="BuildFullUsage"/> summary — the shape a
    /// real store round-trip restores and re-aggregates. Like <see cref="AssertEntry"/> it covers all
    /// 13 estimated properties and both coverage counters, so a dropped or misassigned estimate
    /// cannot pass this either.
    /// </summary>
    internal static void AssertFullUsageTotal(TokenUsage total)
    {
        var input = AgentExpected.Input + SubAgentExpected.Input + CompactionExpected.Input;
        var output = AgentExpected.Output + SubAgentExpected.Output + CompactionExpected.Output;
        var cached = AgentExpected.Cached + SubAgentExpected.Cached + CompactionExpected.Cached;
        var reasoning = AgentExpected.Reasoning + SubAgentExpected.Reasoning + CompactionExpected.Reasoning;
        var calls = AgentExpected.Calls + SubAgentExpected.Calls + CompactionExpected.Calls;
        var cachedReported = AgentExpected.CachedReported + SubAgentExpected.CachedReported + CompactionExpected.CachedReported;
        var reasoningReported = AgentExpected.ReasoningReported + SubAgentExpected.ReasoningReported + CompactionExpected.ReasoningReported;

        Assert.Equal(input, total.InputTokens);
        Assert.Equal(output, total.OutputTokens);
        Assert.Equal(cached, total.CachedInputTokens);
        Assert.Equal(reasoning, total.ReasoningTokens);
        Assert.Equal(calls, total.Calls);
        Assert.Equal(cachedReported, total.CachedInputReportedCalls);
        Assert.Equal(reasoningReported, total.ReasoningReportedCalls);

        var estimated = total.Estimated;
        Assert.Equal(AgentExpected.Estimated.SystemPrompt + SubAgentExpected.Estimated.SystemPrompt + CompactionExpected.Estimated.SystemPrompt, estimated.SystemPrompt);
        Assert.Equal(AgentExpected.Estimated.ToolDefinitions + SubAgentExpected.Estimated.ToolDefinitions + CompactionExpected.Estimated.ToolDefinitions, estimated.ToolDefinitions);
        Assert.Equal(AgentExpected.Estimated.UserText + SubAgentExpected.Estimated.UserText + CompactionExpected.Estimated.UserText, estimated.UserText);
        Assert.Equal(AgentExpected.Estimated.AssistantText + SubAgentExpected.Estimated.AssistantText + CompactionExpected.Estimated.AssistantText, estimated.AssistantText);
        Assert.Equal(AgentExpected.Estimated.ToolCalls + SubAgentExpected.Estimated.ToolCalls + CompactionExpected.Estimated.ToolCalls, estimated.ToolCalls);
        Assert.Equal(AgentExpected.Estimated.ToolResults + SubAgentExpected.Estimated.ToolResults + CompactionExpected.Estimated.ToolResults, estimated.ToolResults);
        Assert.Equal(AgentExpected.Estimated.Reasoning + SubAgentExpected.Estimated.Reasoning + CompactionExpected.Estimated.Reasoning, estimated.Reasoning);
        Assert.Equal(AgentExpected.Estimated.Images + SubAgentExpected.Estimated.Images + CompactionExpected.Estimated.Images, estimated.Images);
        Assert.Equal(AgentExpected.Estimated.OutputText + SubAgentExpected.Estimated.OutputText + CompactionExpected.Estimated.OutputText, estimated.OutputText);
        Assert.Equal(AgentExpected.Estimated.OutputToolCalls + SubAgentExpected.Estimated.OutputToolCalls + CompactionExpected.Estimated.OutputToolCalls, estimated.OutputToolCalls);
        Assert.Equal(AgentExpected.Estimated.OutputReasoning + SubAgentExpected.Estimated.OutputReasoning + CompactionExpected.Estimated.OutputReasoning, estimated.OutputReasoning);
        Assert.Equal(AgentExpected.Estimated.InputEstimatedCalls + SubAgentExpected.Estimated.InputEstimatedCalls + CompactionExpected.Estimated.InputEstimatedCalls, estimated.InputEstimatedCalls);
        Assert.Equal(AgentExpected.Estimated.OutputEstimatedCalls + SubAgentExpected.Estimated.OutputEstimatedCalls + CompactionExpected.Estimated.OutputEstimatedCalls, estimated.OutputEstimatedCalls);
    }

    /// <summary>
    /// Asserts one bucket's exact token counts, call counters, ALL 13 estimated properties and BOTH
    /// coverage counters.
    /// </summary>
    internal static void AssertEntry(TokenUsage usage, ExpectedEntry expected)
    {
        Assert.Equal(expected.Input, usage.InputTokens);
        Assert.Equal(expected.Output, usage.OutputTokens);
        Assert.Equal(expected.Cached, usage.CachedInputTokens);
        Assert.Equal(expected.Reasoning, usage.ReasoningTokens);
        Assert.Equal(expected.Calls, usage.Calls);
        Assert.Equal(expected.CachedReported, usage.CachedInputReportedCalls);
        Assert.Equal(expected.ReasoningReported, usage.ReasoningReportedCalls);

        // The estimated breakdown: all EIGHT input categories.
        var estimated = usage.Estimated;
        Assert.Equal(expected.Estimated.SystemPrompt, estimated.SystemPrompt);
        Assert.Equal(expected.Estimated.ToolDefinitions, estimated.ToolDefinitions);
        Assert.Equal(expected.Estimated.UserText, estimated.UserText);
        Assert.Equal(expected.Estimated.AssistantText, estimated.AssistantText);
        Assert.Equal(expected.Estimated.ToolCalls, estimated.ToolCalls);
        Assert.Equal(expected.Estimated.ToolResults, estimated.ToolResults);
        Assert.Equal(expected.Estimated.Reasoning, estimated.Reasoning);
        Assert.Equal(expected.Estimated.Images, estimated.Images);
        // …all THREE output categories…
        Assert.Equal(expected.Estimated.OutputText, estimated.OutputText);
        Assert.Equal(expected.Estimated.OutputToolCalls, estimated.OutputToolCalls);
        Assert.Equal(expected.Estimated.OutputReasoning, estimated.OutputReasoning);
        // …and BOTH coverage counters, which say how many calls contributed an estimate at all.
        Assert.Equal(expected.Estimated.InputEstimatedCalls, estimated.InputEstimatedCalls);
        Assert.Equal(expected.Estimated.OutputEstimatedCalls, estimated.OutputEstimatedCalls);
    }

    /// <summary>Asserts that <paramref name="actual"/> is an EMPTY, non-null summary.</summary>
    internal static void AssertEmptySummary(UsageSummary? actual)
    {
        Assert.NotNull(actual);
        Assert.Empty(actual!.Entries);
        Assert.Equal(0, actual.Total.Calls);
    }

    /// <summary>
    /// Asserts that <paramref name="stored"/> is a DETACHED copy of the completing result's usage:
    /// not the same instance, and — the observable half — replacing the result's entries with one
    /// distinctive replacement afterwards leaves the stored counts exactly as
    /// <see cref="BuildFullUsage"/> describes them. A stamp that aliased the <see cref="TaskResult"/>'s
    /// instance fails here, because the replacement reaches the stored summary.
    /// </summary>
    internal static void AssertDetachedFromResult(TaskResult result, UsageSummary? stored)
    {
        Assert.NotNull(result.Usage);
        Assert.NotNull(stored);
        Assert.NotSame(result.Usage, stored);

        result.Usage!.Entries =
        [
            new UsageEntry(UsageSource.Agent, "post-hoc-replacement", new TokenUsage { InputTokens = 999_999, Calls = 1 }),
        ];

        AssertFullUsage(stored);
    }
}

/// <summary>
/// The stamping contract at each <see cref="PipelineDriver"/> completion site: the completing task's
/// reported usage lands on the LAST phase entry matching the executing phase and iteration, as a
/// DETACHED snapshot, with null preserved as null and an empty summary preserved as empty.
/// </summary>
public sealed class PhaseUsageStampingTests
{
    // ── Normal completion (PipelineDriver.DriveNextPhaseAsync, normal bookkeeping) ──

    /// <summary>
    /// Normal completion: the usage lands on the matching phase entry with every count preserved,
    /// and the stored value is a detached copy — replacing the completing result's entries
    /// afterwards does not change the stored summary.
    /// </summary>
    [Fact]
    public async Task NormalCompletion_StampsDetachedUsageOnMatchingEntry()
    {
        var (dispatcher, pipeline, taskId) = PipelineDriverWorkerOutputTests.CreateDispatcher(GoalPhase.Review);
        PipelineDriverWorkerOutputTests.AddPhaseEntry(pipeline, GoalPhase.Review);
        var reviewEntry = Assert.Single(pipeline.PhaseLog, e => e.Name == GoalPhase.Review);

        var result = new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "raw review output",
            Metrics = new TaskMetrics { Verdict = "APPROVE", Summary = "structured review summary" },
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        };

        await dispatcher.HandleTaskCompletionAsync(result, TestContext.Current.CancellationToken);

        // The usage landed on the completing phase's entry, field by field.
        PhaseUsageFixtures.AssertFullUsage(reviewEntry.Usage);

        // It is a DETACHED copy: the stored summary is not the result's instance, and replacing the
        // result's entries afterwards leaves the stored counts untouched.
        Assert.NotSame(result.Usage, reviewEntry.Usage);
        var storedEntriesBefore = reviewEntry.Usage!.Entries.Count;
        result.Usage!.Entries =
        [
            new UsageEntry(UsageSource.Agent, "replacement-model", new TokenUsage { InputTokens = 999_999, Calls = 1 }),
        ];
        Assert.Equal(storedEntriesBefore, reviewEntry.Usage.Entries.Count);
        PhaseUsageFixtures.AssertFullUsage(reviewEntry.Usage);
    }

    /// <summary>
    /// Selection is <c>LastOrDefault</c>: with an older-iteration entry, an earlier occurrence in the
    /// current iteration and a last occurrence, ONLY the last current-iteration occurrence is stamped.
    /// </summary>
    [Fact]
    public async Task NormalCompletion_MultipleMatchingEntries_StampsOnlyTheLastOccurrence()
    {
        var (dispatcher, pipeline, taskId) = PipelineDriverWorkerOutputTests.CreateDispatcher(GoalPhase.Review);
        PipelineDriverWorkerOutputTests.AddPhaseEntry(pipeline, GoalPhase.Review);

        var olderIterationEntry = new PhaseResult
        {
            Name = GoalPhase.Review,
            Result = PhaseOutcome.Pass,
            Iteration = 0,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        pipeline.PhaseLog.Insert(0, olderIterationEntry);
        var earlierOccurrence = pipeline.PhaseLog[1];
        var lastOccurrence = PhaseResult.Create(GoalPhase.Review, pipeline.Iteration, 2);
        pipeline.PhaseLog.Add(lastOccurrence);

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "raw review output",
            Metrics = new TaskMetrics { Verdict = "APPROVE" },
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        }, TestContext.Current.CancellationToken);

        PhaseUsageFixtures.AssertFullUsage(lastOccurrence.Usage);
        Assert.Null(olderIterationEntry.Usage);
        Assert.Null(earlierOccurrence.Usage);
    }

    /// <summary>
    /// A completion that reports no usage stores <c>null</c> — the honest "not reported" marker —
    /// while an EMPTY summary is stored as an empty, non-null summary (zero model calls).
    /// <para>
    /// NON-VACUOUS BY CONSTRUCTION: the target entry is seeded with a pre-existing populated usage
    /// first, so a mutant that SKIPS stamping when the result's usage is null would leave the seed in
    /// place and fail the "replaced with null" assertion. The empty branch additionally proves the
    /// seed is replaced by an EMPTY summary rather than being left alone.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalCompletion_NullAndEmptyUsageReplaceTheSeededValue(bool reportEmpty)
    {
        var (dispatcher, pipeline, taskId) = PipelineDriverWorkerOutputTests.CreateDispatcher(GoalPhase.Review);
        PipelineDriverWorkerOutputTests.AddPhaseEntry(pipeline, GoalPhase.Review);
        var reviewEntry = pipeline.CurrentPhaseEntry!;
        // The pre-existing value a skipped stamp would leave behind.
        reviewEntry.Usage = PhaseUsageFixtures.BuildFullUsage();

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "raw review output",
            Metrics = new TaskMetrics { Verdict = "APPROVE" },
            Usage = reportEmpty ? new UsageSummary() : null,
        }, TestContext.Current.CancellationToken);

        if (reportEmpty)
            PhaseUsageFixtures.AssertEmptySummary(reviewEntry.Usage);
        else
            Assert.Null(reviewEntry.Usage);
    }

    // ── Worker failure (non-Improve): terminal, usage is billed ──

    [Fact]
    public async Task WorkerFailure_NonImprovePhase_StampsUsageOnTheFailingEntry()
    {
        var (driver, pipeline, _, _) = PipelineDriverFailedWorkerTests.CreateFailedWorkerDriver(GoalPhase.Testing);
        pipeline.SetPlan(IterationPlan.Default());
        pipeline.AdvanceTo(GoalPhase.Testing);
        var testingEntry = PhaseResult.Create(GoalPhase.Testing, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(testingEntry);

        var result = new TaskResult
        {
            TaskId = "task-usage-failed",
            Status = TaskOutcome.Failed,
            Output = "crash in the tester",
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        };

        await driver.DriveNextPhaseAsync(pipeline, result, TestContext.Current.CancellationToken);

        // The failure is terminal (unchanged behavior) AND the billed usage is retained.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(PhaseOutcome.Fail, testingEntry.Result);
        PhaseUsageFixtures.AssertFullUsage(testingEntry.Usage);

        Assert.NotSame(result.Usage, testingEntry.Usage);
        result.Usage!.Entries = [];
        PhaseUsageFixtures.AssertFullUsage(testingEntry.Usage);
    }

    /// <summary>
    /// No matching entry: no synthetic entry is created, the terminal failure is unchanged, and no
    /// unrelated trailing entry gains usage.
    /// </summary>
    [Fact]
    public async Task WorkerFailure_NoMatchingEntry_CreatesNothingAndStampsNothing()
    {
        var (driver, pipeline, _, _) = PipelineDriverFailedWorkerTests.CreateFailedWorkerDriver(GoalPhase.Coding);
        pipeline.SetPlan(IterationPlan.Default());
        pipeline.AdvanceTo(GoalPhase.Coding);
        var unrelated = new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
        };
        pipeline.PhaseLog.Add(unrelated);
        var phaseLogCountBefore = pipeline.PhaseLog.Count;

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-usage-failed-nomatch",
            Status = TaskOutcome.Failed,
            Output = "crash with no matching entry",
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        }, TestContext.Current.CancellationToken);

        Assert.Equal(phaseLogCountBefore, pipeline.PhaseLog.Count);
        Assert.Null(unrelated.Usage);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    /// <summary>
    /// Worker failure (non-Improve), null/empty vectors, both SEEDED so a stamp skipped on a
    /// null/empty result cannot pass vacuously: the pre-existing populated usage must be REPLACED
    /// (with null) or REPLACED BY EMPTY, never left alone.
    /// </summary>
    /// <param name="usageKind">0 = result.Usage is null, 1 = result.Usage is an empty summary.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WorkerFailure_NonImprovePhase_NullAndEmptyUsageReplaceTheSeededValue(int usageKind)
    {
        var (driver, pipeline, _, _) = PipelineDriverFailedWorkerTests.CreateFailedWorkerDriver(GoalPhase.Testing);
        pipeline.SetPlan(IterationPlan.Default());
        pipeline.AdvanceTo(GoalPhase.Testing);
        var testingEntry = PhaseResult.Create(GoalPhase.Testing, pipeline.Iteration, 1);
        testingEntry.Usage = PhaseUsageFixtures.BuildFullUsage();
        pipeline.PhaseLog.Add(testingEntry);

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-usage-failed-null",
            Status = TaskOutcome.Failed,
            Output = "crash in the tester",
            Usage = usageKind == 1 ? new UsageSummary() : null,
        }, TestContext.Current.CancellationToken);

        // The failure remains terminal, and the seeded value is gone: null stays null, empty stays
        // empty-and-non-null.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(PhaseOutcome.Fail, testingEntry.Result);
        if (usageKind == 1)
            PhaseUsageFixtures.AssertEmptySummary(testingEntry.Usage);
        else
            Assert.Null(testingEntry.Usage);
    }

    /// <summary>
    /// Occurrence/isolation for the WORKER-FAILURE exit, with BOTH predicates isolated: an entry from
    /// the WRONG iteration of the same phase is placed LAST in the log (so a loop that ignored the
    /// iteration predicate would stamp IT), and an earlier same-iteration occurrence precedes the
    /// correct entry (so an implementation that took the FIRST match would stamp that instead). Both
    /// decoys are pre-seeded with a distinctive populated usage, so a wrong selection is visible.
    /// </summary>
    [Fact]
    public async Task WorkerFailure_OccurrenceSelection_StampsOnlyTheLastCurrentIterationEntry()
    {
        var (driver, pipeline, _, _) = PipelineDriverFailedWorkerTests.CreateFailedWorkerDriver(GoalPhase.Testing);
        pipeline.SetPlan(IterationPlan.Default());
        pipeline.AdvanceTo(GoalPhase.Testing);

        var decoyEarlierOccurrence = PhaseResult.Create(GoalPhase.Testing, pipeline.Iteration, 1);
        decoyEarlierOccurrence.Usage = DecoyUsage();
        pipeline.PhaseLog.Add(decoyEarlierOccurrence);

        var target = PhaseResult.Create(GoalPhase.Testing, pipeline.Iteration, 2);
        pipeline.PhaseLog.Add(target);

        // The WRONG-ITERATION decoy is last: only the iteration predicate keeps the stamp off it.
        var decoyWrongIteration = PhaseResult.Create(GoalPhase.Testing, pipeline.Iteration + 1, 1);
        decoyWrongIteration.Usage = DecoyUsage();
        pipeline.PhaseLog.Add(decoyWrongIteration);

        var result = new TaskResult
        {
            TaskId = "task-usage-failed-occurrence",
            Status = TaskOutcome.Failed,
            Output = "crash in the tester",
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        };

        await driver.DriveNextPhaseAsync(pipeline, result, TestContext.Current.CancellationToken);

        PhaseUsageFixtures.AssertFullUsage(target.Usage);
        AssertDecoyUntouched(decoyEarlierOccurrence);
        AssertDecoyUntouched(decoyWrongIteration);
    }

    // ── Failed Improve: non-blocking, the goal continues ──

    [Fact]
    public async Task FailedImprove_NonBlocking_StampsUsageAndStillContinues()
    {
        var (driver, pipeline, entry, syncCount, _) = PipelineDriverImproveSkipTests.CreateImproveDriver();

        var result = new TaskResult
        {
            TaskId = "task-usage-improve-failed",
            Status = TaskOutcome.Failed,
            Output = "improver could not publish its guidance update",
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        };

        await driver.DriveNextPhaseAsync(pipeline, result, TestContext.Current.CancellationToken);

        // The Improve failure stays non-blocking (it advanced past Improve) …
        Assert.Equal(GoalPhase.Merging, pipeline.StateMachine.Phase);
        Assert.Equal(0, syncCount[0]);
        // … and the billed usage is still recorded on the Improve entry, DETACHED from the result.
        PhaseUsageFixtures.AssertFullUsage(entry.Usage);
        PhaseUsageFixtures.AssertDetachedFromResult(result, entry.Usage);
    }

    /// <summary>
    /// Failed Improve, null/empty vectors, both SEEDED: the pre-existing usage must be REPLACED, so a
    /// mutant that skips stamping on a null/empty result fails. The goal still continues in both.
    /// </summary>
    /// <param name="usageKind">0 = result.Usage is null, 1 = result.Usage is an empty summary.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FailedImprove_NullAndEmptyUsageReplaceTheSeededValue(int usageKind)
    {
        var (driver, pipeline, entry, syncCount, _) = PipelineDriverImproveSkipTests.CreateImproveDriver();
        entry.Usage = PhaseUsageFixtures.BuildFullUsage();

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-usage-improve-null",
            Status = TaskOutcome.Failed,
            Output = "improver could not publish its guidance update",
            Usage = usageKind == 1 ? new UsageSummary() : null,
        }, TestContext.Current.CancellationToken);

        // Non-blocking behavior is unchanged …
        Assert.Equal(GoalPhase.Merging, pipeline.StateMachine.Phase);
        Assert.Equal(0, syncCount[0]);
        // … and the seeded value was replaced, not left behind.
        if (usageKind == 1)
            PhaseUsageFixtures.AssertEmptySummary(entry.Usage);
        else
            Assert.Null(entry.Usage);
    }

    /// <summary>
    /// Occurrence/isolation for the FAILED-IMPROVE exit, with the wrong-iteration decoy LAST in the
    /// log (isolating the iteration predicate) and an earlier same-iteration occurrence before the
    /// target (isolating the LastOrDefault selection). Both decoys carry a distinctive populated
    /// usage so a wrong selection is visible.
    /// </summary>
    [Fact]
    public async Task FailedImprove_OccurrenceSelection_StampsOnlyTheLastCurrentIterationEntry()
    {
        var (driver, pipeline, entry, _, _) = PipelineDriverImproveSkipTests.CreateImproveDriver();

        // entry is the first same-iteration occurrence; give it a decoy value and push it first.
        pipeline.PhaseLog.Remove(entry);
        entry.Usage = DecoyUsage();
        pipeline.PhaseLog.Add(entry);

        var target = PhaseResult.Create(GoalPhase.Improve, pipeline.Iteration, 2);
        pipeline.PhaseLog.Add(target);

        var decoyWrongIteration = PhaseResult.Create(GoalPhase.Improve, pipeline.Iteration + 1, 1);
        decoyWrongIteration.Usage = DecoyUsage();
        pipeline.PhaseLog.Add(decoyWrongIteration);

        var result = new TaskResult
        {
            TaskId = "task-usage-improve-occurrence",
            Status = TaskOutcome.Failed,
            Output = "improver publication failed",
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        };

        await driver.DriveNextPhaseAsync(pipeline, result, TestContext.Current.CancellationToken);

        PhaseUsageFixtures.AssertFullUsage(target.Usage);
        AssertDecoyUntouched(entry);
        AssertDecoyUntouched(decoyWrongIteration);
    }

    // ── No-op Coding: retry exit and terminal budget-exhausted exit ──

    [Fact]
    public async Task NoOpCodingRetry_StampsUsageOnTheFailedCodingEntry()
    {
        var (driver, pipeline, _) = PipelineDriverNoOpRetryTests.CreateNoOpDriver();
        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);
        pipeline.Conversation.Add(new ConversationEntry("user", "retry craft", 2, "craft-prompt"));

        var result = new TaskResult
        {
            TaskId = "task-usage-noop-retry",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        };

        await driver.DriveNextPhaseAsync(pipeline, result, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        PhaseUsageFixtures.AssertFullUsage(codingEntry.Usage);
        PhaseUsageFixtures.AssertDetachedFromResult(result, codingEntry.Usage);
    }

    [Fact]
    public async Task NoOpCodingTerminal_StampsUsageOnTheFailedCodingEntryAndKeepsItInTheSummary()
    {
        var (driver, pipeline, _) = PipelineDriverNoOpRetryTests.CreateNoOpDriver();
        for (var i = 0; i < 4; i++)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);

        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        var result = new TaskResult
        {
            TaskId = "task-usage-noop-terminal",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        };

        await driver.DriveNextPhaseAsync(pipeline, result, TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        PhaseUsageFixtures.AssertFullUsage(codingEntry.Usage);
        // DETACHED on the terminal no-op exit too: a post-hoc replacement of the result's entries
        // must not reach the stored snapshot (the summary assertion below re-runs after it).
        PhaseUsageFixtures.AssertDetachedFromResult(result, codingEntry.Usage);

        // The terminal iteration summary carries the same entry (and therefore the usage).
        var summary = Assert.Single(pipeline.CompletedIterationSummaries);
        var codingInSummary = Assert.Single(summary.Phases, p => p.Name == GoalPhase.Coding);
        PhaseUsageFixtures.AssertFullUsage(codingInSummary.Usage);
    }

    /// <summary>
    /// Terminal no-op, null/empty vectors, both SEEDED so a skipped stamp on a null/empty result
    /// fails instead of passing vacuously: the seeded populated usage must be REPLACED.
    /// </summary>
    /// <param name="usageKind">0 = result.Usage is null, 1 = result.Usage is an empty summary.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NoOpCodingTerminal_NullAndEmptyUsageReplaceTheSeededValue(int usageKind)
    {
        var (driver, pipeline, _) = PipelineDriverNoOpRetryTests.CreateNoOpDriver();
        for (var i = 0; i < 4; i++)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);

        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        codingEntry.Usage = PhaseUsageFixtures.BuildFullUsage();
        pipeline.PhaseLog.Add(codingEntry);

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-usage-noop-terminal-null",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Usage = usageKind == 1 ? new UsageSummary() : null,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        if (usageKind == 1)
            PhaseUsageFixtures.AssertEmptySummary(codingEntry.Usage);
        else
            Assert.Null(codingEntry.Usage);
    }

    /// <summary>
    /// Occurrence/isolation for the TERMINAL no-op exit, with the wrong-iteration decoy LAST in the
    /// log (isolating the iteration predicate) and an earlier same-iteration occurrence before the
    /// target (isolating the LastOrDefault selection). Both decoys carry a distinctive populated
    /// usage so a wrong selection is visible.
    /// </summary>
    [Fact]
    public async Task NoOpCodingTerminal_OccurrenceSelection_StampsOnlyTheLastCurrentIterationEntry()
    {
        var (driver, pipeline, _) = PipelineDriverNoOpRetryTests.CreateNoOpDriver();
        for (var i = 0; i < 4; i++)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);

        var decoyEarlierOccurrence = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        decoyEarlierOccurrence.Usage = DecoyUsage();
        pipeline.PhaseLog.Add(decoyEarlierOccurrence);

        var target = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 2);
        pipeline.PhaseLog.Add(target);

        var decoyWrongIteration = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration + 1, 1);
        decoyWrongIteration.Usage = DecoyUsage();
        pipeline.PhaseLog.Add(decoyWrongIteration);

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-usage-noop-terminal-occurrence",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        }, TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        PhaseUsageFixtures.AssertFullUsage(target.Usage);
        AssertDecoyUntouched(decoyEarlierOccurrence);
        AssertDecoyUntouched(decoyWrongIteration);
    }

    /// <summary>
    /// No-op with MULTIPLE matching Coding entries: only the last current-iteration occurrence gains
    /// the usage (the same <c>LastOrDefault</c> selection the narrative capture uses).
    /// </summary>
    [Fact]
    public async Task NoOpCoding_HistoricalEntries_StayUntouchedByUsageCapture()
    {
        var (driver, pipeline, _) = PipelineDriverNoOpRetryTests.CreateNoOpDriver();

        var olderIterationEntry = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = 0,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        pipeline.PhaseLog.Add(olderIterationEntry);
        var earlierOccurrence = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
        };
        pipeline.PhaseLog.Add(earlierOccurrence);
        var lastOccurrence = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 2);
        pipeline.PhaseLog.Add(lastOccurrence);

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-usage-noop-occurrence",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        }, TestContext.Current.CancellationToken);

        PhaseUsageFixtures.AssertFullUsage(lastOccurrence.Usage);
        Assert.Null(olderIterationEntry.Usage);
        Assert.Null(earlierOccurrence.Usage);
    }

    /// <summary>
    /// A no-op whose completion reports no usage stores null; the no-op bookkeeping is unaffected.
    /// SEEDED so the assertion is not vacuous: the pre-existing populated usage must be gone.
    /// </summary>
    [Fact]
    public async Task NoOpCodingRetry_WithoutUsage_ReplacesTheSeededValueWithNull()
    {
        var (driver, pipeline, _) = PipelineDriverNoOpRetryTests.CreateNoOpDriver();
        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        codingEntry.Usage = PhaseUsageFixtures.BuildFullUsage();
        pipeline.PhaseLog.Add(codingEntry);
        pipeline.Conversation.Add(new ConversationEntry("user", "retry craft", 2, "craft-prompt"));

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-usage-noop-null",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        Assert.Null(codingEntry.Usage);
    }

    /// <summary>
    /// A no-op retry whose completion carries an EMPTY summary stores an empty, non-null summary —
    /// "reported, zero model calls" — replacing any seeded value. The two states stay distinct.
    /// </summary>
    [Fact]
    public async Task NoOpCodingRetry_EmptyUsageStaysEmptyNonNull()
    {
        var (driver, pipeline, _) = PipelineDriverNoOpRetryTests.CreateNoOpDriver();
        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        codingEntry.Usage = PhaseUsageFixtures.BuildFullUsage();
        pipeline.PhaseLog.Add(codingEntry);
        pipeline.Conversation.Add(new ConversationEntry("user", "retry craft", 2, "craft-prompt"));

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-usage-noop-empty",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Usage = new UsageSummary(),
        }, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        PhaseUsageFixtures.AssertEmptySummary(codingEntry.Usage);
    }

    /// <summary>
    /// A distinctive populated usage placed on a DECOY entry that must never be selected: one entry
    /// whose counts cannot be confused with the fixture's.
    /// </summary>
    private static UsageSummary DecoyUsage() => new(
    [
        new UsageEntry(UsageSource.Agent, "decoy-model", new TokenUsage
        {
            InputTokens = 777_777,
            OutputTokens = 777,
            CachedInputTokens = 77,
            ReasoningTokens = 7,
            Calls = 77,
            CachedInputReportedCalls = 7,
            ReasoningReportedCalls = 77,
            Estimated = new EstimatedTokenBreakdown { SystemPrompt = 777, InputEstimatedCalls = 77, OutputEstimatedCalls = 7 },
        }),
    ]);

    /// <summary>
    /// Asserts a decoy entry's usage is EXACTLY the untouched <see cref="DecoyUsage"/> value — the
    /// same single entry with the same counts — so a stamp that landed on the wrong entry is visible.
    /// </summary>
    private static void AssertDecoyUntouched(PhaseResult decoy)
    {
        Assert.NotNull(decoy.Usage);
        var entry = Assert.Single(decoy.Usage!.Entries);
        Assert.Equal(UsageSource.Agent, entry.Source);
        Assert.Equal("decoy-model", entry.Model);
        Assert.Equal(777_777, entry.Usage.InputTokens);
        Assert.Equal(777, entry.Usage.OutputTokens);
        Assert.Equal(77, entry.Usage.CachedInputTokens);
        Assert.Equal(7, entry.Usage.ReasoningTokens);
        Assert.Equal(77, entry.Usage.Calls);
        Assert.Equal(7, entry.Usage.CachedInputReportedCalls);
        Assert.Equal(77, entry.Usage.ReasoningReportedCalls);
        Assert.Equal(777, entry.Usage.Estimated.SystemPrompt);
        Assert.Equal(77, entry.Usage.Estimated.InputEstimatedCalls);
        Assert.Equal(7, entry.Usage.Estimated.OutputEstimatedCalls);
    }
}

/// <summary>
/// The derived-totals contract of <see cref="PhaseUsageTotals"/>: entries merge by
/// <c>(source, model)</c> with ordinal model comparison, every count is SUMMED (so a partial
/// category stays partial), only worker phases are counted, and the inputs are never mutated.
/// </summary>
public sealed class PhaseUsageTotalsTests
{
    private static PhaseResult Phase(GoalPhase name, UsageSummary? usage = null, int iteration = 1) => new()
    {
        Name = name,
        Result = PhaseOutcome.Pass,
        Iteration = iteration,
        Occurrence = 1,
        Usage = usage,
    };

    private static UsageSummary OneEntry(UsageSource source, string? model, TokenUsage usage) =>
        new([new UsageEntry(source, model, usage)]);

    [Fact]
    public void Sum_MergesSameSourceModelAndKeepsDistinctPairsSeparate()
    {
        // Two phases contributing to Agent/model-a, one to SubAgent/model-b, one to Agent/model-b.
        var first = Phase(GoalPhase.Coding, new UsageSummary(
        [
            new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 100, Calls = 2, CachedInputReportedCalls = 1, ReasoningReportedCalls = 2 }),
            new UsageEntry(UsageSource.SubAgent, "model-b", new TokenUsage { InputTokens = 10, Calls = 1, CachedInputReportedCalls = 1, ReasoningReportedCalls = 1 }),
        ]));
        var second = Phase(GoalPhase.Testing, new UsageSummary(
        [
            new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 200, Calls = 3, CachedInputReportedCalls = 2, ReasoningReportedCalls = 1 }),
            new UsageEntry(UsageSource.Agent, "model-b", new TokenUsage { InputTokens = 1_000, Calls = 5, CachedInputReportedCalls = 5, ReasoningReportedCalls = 5 }),
        ]));

        var totals = PhaseUsageTotals.Sum([first, second]);

        Assert.Equal(2, totals.PhasesWithUsage);
        Assert.Equal(0, totals.PhasesWithoutUsage);
        Assert.Equal(3, totals.Usage.Entries.Count);

        var mergedAgentA = Assert.Single(totals.Usage.Entries, e => e.Source == UsageSource.Agent && e.Model == "model-a");
        Assert.Equal(300, mergedAgentA.Usage.InputTokens);
        Assert.Equal(5, mergedAgentA.Usage.Calls);
        Assert.Equal(3, mergedAgentA.Usage.CachedInputReportedCalls);
        Assert.Equal(3, mergedAgentA.Usage.ReasoningReportedCalls);

        var subAgentB = Assert.Single(totals.Usage.Entries, e => e.Source == UsageSource.SubAgent && e.Model == "model-b");
        Assert.Equal(10, subAgentB.Usage.InputTokens);

        var agentB = Assert.Single(totals.Usage.Entries, e => e.Source == UsageSource.Agent && e.Model == "model-b");
        Assert.Equal(1_000, agentB.Usage.InputTokens);
    }

    /// <summary>
    /// The reported-calls counters are SUMMED, not recomputed, so a category that was partial in a
    /// phase (or in every phase) stays partial in the total — detectable by comparing the counter
    /// with <c>Calls</c>.
    /// </summary>
    [Fact]
    public void Sum_PartialCategoryStaysPartialInTheTotal()
    {
        var partialOnly = Phase(GoalPhase.Coding, OneEntry(UsageSource.Agent, "model-a", new TokenUsage
        {
            InputTokens = 100,
            Calls = 4,
            CachedInputReportedCalls = 1,  // 3 calls did not report
            ReasoningReportedCalls = 4,
        }));
        var partialAndComplete = Phase(GoalPhase.Testing, OneEntry(UsageSource.Agent, "model-a", new TokenUsage
        {
            InputTokens = 300,
            Calls = 2,
            CachedInputReportedCalls = 2,
            ReasoningReportedCalls = 1,
        }));

        var totals = PhaseUsageTotals.Sum([partialOnly, partialAndComplete]);
        var merged = Assert.Single(totals.Usage.Entries);

        Assert.Equal(6, merged.Usage.Calls);
        Assert.Equal(3, merged.Usage.CachedInputReportedCalls);
        Assert.NotEqual(merged.Usage.Calls, merged.Usage.CachedInputReportedCalls);
        Assert.Equal(5, merged.Usage.ReasoningReportedCalls);
        Assert.NotEqual(merged.Usage.Calls, merged.Usage.ReasoningReportedCalls);
    }

    /// <summary>
    /// The estimated breakdown is summed too, coverage counters included, so a total's estimate
    /// reports how many contributing calls actually produced one.
    /// </summary>
    [Fact]
    public void Sum_SumsTheEstimatedBreakdownAndItsCoverageCounters()
    {
        var first = Phase(GoalPhase.Coding, OneEntry(UsageSource.Agent, "model-a", new TokenUsage
        {
            Calls = 2,
            Estimated = new EstimatedTokenBreakdown
            {
                SystemPrompt = 11, ToolDefinitions = 12, UserText = 13, AssistantText = 14,
                ToolCalls = 15, ToolResults = 16, Reasoning = 17, Images = 18,
                OutputText = 19, OutputToolCalls = 20, OutputReasoning = 21,
                InputEstimatedCalls = 22, OutputEstimatedCalls = 23,
            },
        }));
        var second = Phase(GoalPhase.Testing, OneEntry(UsageSource.Agent, "model-a", new TokenUsage
        {
            Calls = 1,
            Estimated = new EstimatedTokenBreakdown
            {
                SystemPrompt = 1, ToolDefinitions = 2, UserText = 3, AssistantText = 4,
                ToolCalls = 5, ToolResults = 6, Reasoning = 7, Images = 8,
                OutputText = 9, OutputToolCalls = 10, OutputReasoning = 11,
                InputEstimatedCalls = 12, OutputEstimatedCalls = 13,
            },
        }));

        var estimated = Assert.Single(PhaseUsageTotals.Sum([first, second]).Usage.Entries).Usage.Estimated;

        Assert.Equal(12, estimated.SystemPrompt);
        Assert.Equal(14, estimated.ToolDefinitions);
        Assert.Equal(16, estimated.UserText);
        Assert.Equal(18, estimated.AssistantText);
        Assert.Equal(20, estimated.ToolCalls);
        Assert.Equal(22, estimated.ToolResults);
        Assert.Equal(24, estimated.Reasoning);
        Assert.Equal(26, estimated.Images);
        Assert.Equal(28, estimated.OutputText);
        Assert.Equal(30, estimated.OutputToolCalls);
        Assert.Equal(32, estimated.OutputReasoning);
        Assert.Equal(34, estimated.InputEstimatedCalls);
        Assert.Equal(36, estimated.OutputEstimatedCalls);
    }

    /// <summary>
    /// Only worker phases are counted: Planning, Merging, Done and Failed entries contribute no
    /// tokens and appear in neither report count, even when they carry a populated usage.
    /// </summary>
    [Fact]
    public void Sum_IgnoresNonWorkerPhasesEntirely()
    {
        var full = PhaseUsageFixtures.BuildFullUsage();
        var nonWorkerPhases = new[]
        {
            Phase(GoalPhase.Planning, full),
            Phase(GoalPhase.Merging, full),
            Phase(GoalPhase.Done, full),
            Phase(GoalPhase.Failed, full),
        };
        var workerPhase = Phase(GoalPhase.Coding, OneEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 7, Calls = 1 }));

        var totals = PhaseUsageTotals.Sum([.. nonWorkerPhases, workerPhase]);

        Assert.Equal(1, totals.PhasesWithUsage);
        Assert.Equal(0, totals.PhasesWithoutUsage);
        var merged = Assert.Single(totals.Usage.Entries);
        Assert.Equal(7, merged.Usage.InputTokens);
        Assert.Equal(1, merged.Usage.Calls);

        // The same phase list WITHOUT the worker phase yields no entries and no worker counts.
        var nonWorkerOnly = PhaseUsageTotals.Sum(nonWorkerPhases);
        Assert.Equal(0, nonWorkerOnly.PhasesWithUsage);
        Assert.Equal(0, nonWorkerOnly.PhasesWithoutUsage);
        Assert.Empty(nonWorkerOnly.Usage.Entries);
    }

    /// <summary>
    /// A worker phase with no usage report adds to <c>PhasesWithoutUsage</c> and contributes no
    /// tokens; an EMPTY report counts as reported-with-zero, not as missing.
    /// </summary>
    [Fact]
    public void Sum_CountsWorkerPhasesWithAndWithoutUsageSeparately()
    {
        var reported = Phase(GoalPhase.Coding, OneEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 5, Calls = 1 }));
        var missing = Phase(GoalPhase.Testing, usage: null);
        var reportedEmpty = Phase(GoalPhase.Review, new UsageSummary());

        var totals = PhaseUsageTotals.Sum([reported, missing, reportedEmpty]);

        Assert.Equal(2, totals.PhasesWithUsage);
        Assert.Equal(1, totals.PhasesWithoutUsage);
        Assert.Equal(5, Assert.Single(totals.Usage.Entries).Usage.InputTokens);
    }

    /// <summary>
    /// Inputs are never mutated: the phase summaries keep their own counts and instances after the
    /// sum, and adding to the result cannot reach back into them.
    /// </summary>
    [Fact]
    public void Sum_NeverMutatesTheInputPhases()
    {
        var firstUsage = OneEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 100, Calls = 2, CachedInputReportedCalls = 1 });
        var secondUsage = OneEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 200, Calls = 3, CachedInputReportedCalls = 2 });
        var first = Phase(GoalPhase.Coding, firstUsage);
        var second = Phase(GoalPhase.Testing, secondUsage);

        var totals = PhaseUsageTotals.Sum([first, second]);

        // Each input keeps its own counts and its own instance.
        Assert.Same(firstUsage, first.Usage);
        Assert.Same(secondUsage, second.Usage);
        Assert.Equal(100, Assert.Single(firstUsage.Entries).Usage.InputTokens);
        Assert.Equal(200, Assert.Single(secondUsage.Entries).Usage.InputTokens);
        Assert.Equal(2, Assert.Single(firstUsage.Entries).Usage.Calls);
        Assert.NotSame(Assert.Single(firstUsage.Entries).Usage, Assert.Single(totals.Usage.Entries).Usage);

        // The result is detached: mutating the accumulator's source entries afterwards cannot
        // change what the totals reported.
        Assert.Equal(300, Assert.Single(totals.Usage.Entries).Usage.InputTokens);
    }

    /// <summary>
    /// Merging is by <c>(source, model)</c> with an ORDINAL model comparison: model names differing
    /// only by case stay SEPARATE buckets, and a null model groups only with null.
    /// </summary>
    [Fact]
    public void Sum_ModelComparisonIsOrdinalAndNullGroupsAlone()
    {
        var lower = Phase(GoalPhase.Coding, OneEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 1, Calls = 1 }));
        var upper = Phase(GoalPhase.Testing, OneEntry(UsageSource.Agent, "MODEL-A", new TokenUsage { InputTokens = 2, Calls = 1 }));
        var nullModel = Phase(GoalPhase.Review, OneEntry(UsageSource.Agent, null, new TokenUsage { InputTokens = 3, Calls = 1 }));
        var nullModelAgain = Phase(GoalPhase.DocWriting, OneEntry(UsageSource.Agent, null, new TokenUsage { InputTokens = 4, Calls = 1 }));

        var totals = PhaseUsageTotals.Sum([lower, upper, nullModel, nullModelAgain]);

        Assert.Equal(3, totals.Usage.Entries.Count);
        Assert.Equal(1, Assert.Single(totals.Usage.Entries, e => e.Model == "model-a").Usage.InputTokens);
        Assert.Equal(2, Assert.Single(totals.Usage.Entries, e => e.Model == "MODEL-A").Usage.InputTokens);
        Assert.Equal(7, Assert.Single(totals.Usage.Entries, e => e.Model is null).Usage.InputTokens);

        // Distinct SOURCES with the same model stay separate too.
        var otherSource = Phase(GoalPhase.Improve, OneEntry(UsageSource.Compaction, "model-a", new TokenUsage { InputTokens = 9, Calls = 1 }));
        var withOtherSource = PhaseUsageTotals.Sum([lower, otherSource]);
        Assert.Equal(2, withOtherSource.Usage.Entries.Count);
    }

    /// <summary>An empty input yields an EMPTY, non-null summary and zero worker counts.</summary>
    [Fact]
    public void Sum_EmptyInput_YieldsEmptySummary()
    {
        var totals = PhaseUsageTotals.Sum([]);

        PhaseUsageFixtures.AssertEmptySummary(totals.Usage);
        Assert.Equal(0, totals.PhasesWithUsage);
        Assert.Equal(0, totals.PhasesWithoutUsage);
    }

    /// <summary>
    /// The result is a REAL functioning summary whose <c>Total</c> aggregates the merged entries —
    /// the shape the Composer's per-goal total line renders.
    /// </summary>
    [Fact]
    public void Sum_TotalAggregatesTheMergedEntries()
    {
        var totals = PhaseUsageTotals.Sum(
        [
            Phase(GoalPhase.Coding, PhaseUsageFixtures.BuildFullUsage()),
            Phase(GoalPhase.Testing, PhaseUsageFixtures.BuildFullUsage()),
        ]);

        Assert.Equal(3, totals.Usage.Entries.Count);
        var total = totals.Usage.Total;
        Assert.Equal(2 * (101 + 1_111 + 51), total.InputTokens);
        Assert.Equal(2 * (202 + 2_222 + 62), total.OutputTokens);
        Assert.Equal(2 * (303 + 3_333 + 73), total.CachedInputTokens);
        Assert.Equal(2 * (404 + 4_444 + 84), total.ReasoningTokens);
        Assert.Equal(2 * (7 + 9 + 2), total.Calls);
        // Partial stays partial after summing identical summaries: 10 of 36 calls, 0 of 36.
        Assert.Equal(2 * (5 + 0 + 2), total.CachedInputReportedCalls);
        Assert.Equal(2 * (7 + 4 + 2), total.ReasoningReportedCalls);
    }
}

/// <summary>
/// The persistence contract of <see cref="PhaseResult.Usage"/>: the value must survive the REAL
/// store round-trips (<see cref="PipelineStore"/> and <see cref="GoalStore"/>, both reflection-based
/// System.Text.Json over the existing JSON columns) field by field, null must stay distinguishable
/// from empty, and legacy JSON written before the property existed must load with null.
/// </summary>
public sealed class PhaseUsagePersistenceTests
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ── PipelineStore: save → load of a pipeline whose PhaseLog carries usage ──

    [Fact]
    public void PipelineStore_RoundTrip_PreservesEveryUsageFieldAndNullModel()
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance);

        var goal = new Goal { Id = "usage-pipeline-roundtrip", Description = "Usage round-trip" };
        var pipeline = new GoalPipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = 1,
            Occurrence = 1,
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        });

        store.SavePipeline(pipeline);
        var snapshot = Assert.Single(store.LoadActivePipelines());

        var phase = Assert.Single(snapshot.PhaseLog);
        // Every count, ALL 13 estimated properties and BOTH coverage counters survive the restart.
        PhaseUsageFixtures.AssertFullUsage(phase.Usage);

        // The restored value is a REAL functioning summary: its Total re-aggregates across entries,
        // estimated breakdown and coverage counters included.
        PhaseUsageFixtures.AssertFullUsageTotal(phase.Usage!.Total);
    }

    [Fact]
    public void PipelineStore_RoundTrip_NullVersusEmptyUsageStaysDistinguishable()
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance);

        var goal = new Goal { Id = "usage-null-vs-empty", Description = "Null vs empty" };
        var pipeline = new GoalPipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = 1,
            Occurrence = 1,
            Usage = null,
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = 1,
            Occurrence = 1,
            // Reported, zero model calls — must NOT collapse into null.
            Usage = new UsageSummary(),
        });

        store.SavePipeline(pipeline);
        var snapshot = Assert.Single(store.LoadActivePipelines());

        Assert.Equal(2, snapshot.PhaseLog.Count);
        Assert.Null(snapshot.PhaseLog[0].Usage);
        PhaseUsageFixtures.AssertEmptySummary(snapshot.PhaseLog[1].Usage);
    }

    /// <summary>
    /// Legacy row vector: a pipeline row whose <c>phase_log_json</c> was written BEFORE the usage
    /// property existed must load with <see cref="PhaseResult.Usage"/> == null (the honest
    /// "usage was not reported" marker), while every legacy field still loads.
    /// </summary>
    [Fact]
    public void PipelineStore_LegacyPhaseLogJsonWithoutUsage_LoadsWithNullUsage()
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance);

        const string legacyPhaseLogJson =
            """[{"name":"Coding","result":"pass","durationSeconds":45.2,"occurrence":1,"iteration":1,"workerOutput":"legacy output","verdict":"PASS"}]""";

        dbContext.Pipelines.Add(new PipelineEntity
        {
            GoalId = "usage-legacy-row",
            Description = "Legacy row without the usage key",
            GoalJson = """{"id":"usage-legacy-row","description":"legacy row goal","repositories":[]}""",
            Phase = "Coding",
            MetricsJson = "{}",
            CreatedAt = "2025-06-15T10:00:00.0000000Z",
            RoleSessionsJson = "{}",
            PhaseOccurrence = 1,
            PhaseLogJson = legacyPhaseLogJson,
        });
        dbContext.SaveChanges();

        var snapshot = store.LoadPipeline("usage-legacy-row");
        Assert.NotNull(snapshot);
        var phase = Assert.Single(snapshot!.PhaseLog);
        Assert.Null(phase.Usage);
        Assert.Equal(GoalPhase.Coding, phase.Name);
        Assert.Equal(PhaseOutcome.Pass, phase.Result);
        Assert.Equal(45.2, phase.DurationSeconds);
        Assert.Equal("legacy output", phase.WorkerOutput);
        Assert.Equal("PASS", phase.Verdict);
    }

    // ── GoalStore: iteration-summary save → GetIterationsAsync ──

    [Fact]
    public async Task GoalStore_RoundTrip_PreservesEveryUsageFieldThroughGetIterations()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);

        var goal = new Goal { Id = "usage-goal-roundtrip", Description = "Usage round-trip" };
        await store.CreateGoalAsync(goal, ct);

        await store.AddIterationAsync(goal.Id, new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult
                {
                    Name = GoalPhase.Coding,
                    Result = PhaseOutcome.Pass,
                    Iteration = 1,
                    Occurrence = 1,
                    Usage = PhaseUsageFixtures.BuildFullUsage(),
                },
                new PhaseResult
                {
                    Name = GoalPhase.Merging,
                    Result = PhaseOutcome.Pass,
                    Iteration = 1,
                    Occurrence = 1,
                    Usage = null,
                },
                new PhaseResult
                {
                    Name = GoalPhase.Testing,
                    Result = PhaseOutcome.Pass,
                    Iteration = 1,
                    Occurrence = 1,
                    Usage = new UsageSummary(),
                },
            ],
        }, ct);

        var loaded = Assert.Single(await store.GetIterationsAsync(goal.Id, ct));
        Assert.Equal(3, loaded.Phases.Count);
        // The Coding phase's usage survives the goal store verbatim — every count, ALL 13 estimated
        // properties and BOTH coverage counters (not just the aggregate scalars).
        PhaseUsageFixtures.AssertFullUsage(loaded.Phases[0].Usage);
        PhaseUsageFixtures.AssertFullUsageTotal(loaded.Phases[0].Usage!.Total);
        Assert.Null(loaded.Phases[1].Usage);
        PhaseUsageFixtures.AssertEmptySummary(loaded.Phases[2].Usage);
    }

    /// <summary>
    /// THE CHAIN'S OWN SENSITIVITY PROBE. <see cref="PhaseUsageFixtures.AssertFullUsage"/> is the
    /// shared assertion behind every full-usage test, so this test proves the chain actually BITES:
    /// for EACH of the 13 estimated properties and BOTH coverage counters, a copy of the fixture with
    /// that one property changed by +1 (or, for the counters, by +1) must make
    /// <c>AssertFullUsage</c> throw. Without this, a future edit that dropped an estimated assertion
    /// from the chain would silently re-open the reviewer's gap and no test would notice.
    /// </summary>
    [Fact]
    public void AssertFullUsage_IsSensitiveToEveryEstimatedProperty()
    {
        // POSITIVE CONTROL: the unmodified fixture passes the chain.
        PhaseUsageFixtures.AssertFullUsage(PhaseUsageFixtures.BuildFullUsage());

        // Each case perturbs ONE property of ONE entry, so a chain missing that property's
        // assertion would not throw. The perturbation goes through the ENTRIES SETTER (the entries
        // getter hands out detached copies, so mutating them in place would be invisible).
        var cases = new (string Label, Action<EstimatedTokenBreakdown> Bump)[]
        {
            ("SystemPrompt", e => e.SystemPrompt += 1),
            ("ToolDefinitions", e => e.ToolDefinitions += 1),
            ("UserText", e => e.UserText += 1),
            ("AssistantText", e => e.AssistantText += 1),
            ("ToolCalls", e => e.ToolCalls += 1),
            ("ToolResults", e => e.ToolResults += 1),
            ("Reasoning", e => e.Reasoning += 1),
            ("Images", e => e.Images += 1),
            ("OutputText", e => e.OutputText += 1),
            ("OutputToolCalls", e => e.OutputToolCalls += 1),
            ("OutputReasoning", e => e.OutputReasoning += 1),
            ("InputEstimatedCalls", e => e.InputEstimatedCalls += 1),
            ("OutputEstimatedCalls", e => e.OutputEstimatedCalls += 1),
        };

        Assert.Equal(13, cases.Length);
        foreach (var (label, bump) in cases)
        {
            var mutated = WithPerturbedAgentEstimate(bump);

            var exception = Record.Exception(() => PhaseUsageFixtures.AssertFullUsage(mutated));
            Assert.True(exception is Xunit.Sdk.XunitException,
                $"AssertFullUsage must FAIL when '{label}' is mis-assigned/dropped; got: {(exception is null ? "no exception" : exception.GetType().FullName)}");
        }

        // The aggregate-Total chain bites too: one bucket's estimate changed by +1 changes the total.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => PhaseUsageFixtures.AssertFullUsageTotal(WithPerturbedAgentEstimate(e => e.UserText += 1).Total));

        // The 7 scalar/counter fields were already covered; pin two of them for completeness, again
        // through the setter rather than the detached getter.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => PhaseUsageFixtures.AssertFullUsage(WithPerturbedAgentUsage(u => u.InputTokens += 1)));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => PhaseUsageFixtures.AssertFullUsage(WithPerturbedAgentUsage(u => u.CachedInputReportedCalls += 1)));

        // And the detached-copy helper: an ALIASED stored summary (the mutant M2 shape) fails,
        // because the post-hoc replacement reaches it.
        var aliased = PhaseUsageFixtures.BuildFullUsage();
        var result = new TaskResult
        {
            TaskId = "task-detachment-probe",
            Status = TaskOutcome.Completed,
            Usage = aliased,
        };
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => PhaseUsageFixtures.AssertDetachedFromResult(result, aliased));

        // POSITIVE CONTROL for the detachment helper: a detached copy passes.
        var source = PhaseUsageFixtures.BuildFullUsage();
        var detachedResult = new TaskResult
        {
            TaskId = "task-detachment-probe-detached",
            Status = TaskOutcome.Completed,
            Usage = source,
        };
        PhaseUsageFixtures.AssertDetachedFromResult(detachedResult, source.Snapshot());
    }

    /// <summary>
    /// Builds a fresh full-usage summary whose AGENT bucket has <paramref name="bump"/> applied to
    /// its estimated breakdown. Rebuilds through the <see cref="UsageSummary.Entries"/> SETTER
    /// because the getter returns detached copies — an in-place mutation of a read copy would be
    /// invisible and this probe would pass vacuously.
    /// </summary>
    private static UsageSummary WithPerturbedAgentEstimate(Action<EstimatedTokenBreakdown> bump) =>
        PerturbAgentEntry(entry =>
        {
            var clone = entry.Usage.Clone();
            bump(clone.Estimated);
            return clone;
        });

    /// <summary>
    /// Builds a fresh full-usage summary whose AGENT bucket's <see cref="TokenUsage"/> has
    /// <paramref name="bump"/> applied, again through the entries setter.
    /// </summary>
    private static UsageSummary WithPerturbedAgentUsage(Action<TokenUsage> bump) =>
        PerturbAgentEntry(entry =>
        {
            var clone = entry.Usage.Clone();
            bump(clone);
            return clone;
        });

    /// <summary>
    /// Rebuilds the full-usage summary with the Agent bucket's usage replaced by
    /// <paramref name="mutate"/>, leaving the other two buckets exactly as the fixture built them.
    /// </summary>
    private static UsageSummary PerturbAgentEntry(Func<UsageEntry, TokenUsage> mutate) =>
        new(PhaseUsageFixtures.BuildFullUsage().Entries.Select(entry =>
            entry.Source == UsageSource.Agent
                ? new UsageEntry(entry.Source, entry.Model, mutate(entry))
                : entry));

    /// <summary>
    /// Legacy row vector for the goal store: an iteration row whose <c>phases_json</c> predates the
    /// property loads with null usage through the real read path (not a hand-rolled deserialize).
    /// </summary>
    [Fact]
    public async Task GoalStore_LegacyPhasesJsonWithoutUsage_LoadsWithNullUsage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);

        var goal = new Goal { Id = "usage-legacy-iteration", Description = "Legacy iteration row" };
        await store.CreateGoalAsync(goal, ct);

        dbContext.IterationSummaries.Add(new IterationSummaryEntity
        {
            GoalId = goal.Id,
            Iteration = 1,
            PhasesJson = """[{"name":"Review","result":"fail","durationSeconds":12.5,"occurrence":1,"iteration":1,"workerOutput":"legacy review"}]""",
            CreatedAt = "2025-06-15T10:00:00.0000000Z",
        });
        await dbContext.SaveChangesAsync(ct);

        var loaded = Assert.Single(await store.GetIterationsAsync(goal.Id, ct));
        var phase = Assert.Single(loaded.Phases);
        Assert.Null(phase.Usage);
        Assert.Equal(GoalPhase.Review, phase.Name);
        Assert.Equal(PhaseOutcome.Fail, phase.Result);
        Assert.Equal("legacy review", phase.WorkerOutput);
    }

    /// <summary>
    /// The serialized form itself: the property is written under the camelCase <c>usage</c> key with
    /// the SharpCoder shape (entries/source/model/usage/estimated) and its computed <c>total</c>,
    /// which is written but ignored on read.
    /// </summary>
    [Fact]
    public void SerializedPhaseResult_WritesUsageUnderCamelCaseKey()
    {
        var phase = new PhaseResult
        {
            Name = GoalPhase.DocWriting,
            Result = PhaseOutcome.Pass,
            Usage = new UsageSummary([new UsageEntry(UsageSource.Agent, "model-x", new TokenUsage { InputTokens = 5, Calls = 1, CachedInputReportedCalls = 1, ReasoningReportedCalls = 1 })]),
        };

        var json = JsonSerializer.Serialize(phase, CamelCaseOptions);

        Assert.Contains("\"usage\":{\"entries\":[", json);
        Assert.Contains("\"source\":0", json);
        Assert.Contains("\"model\":\"model-x\"", json);
        Assert.Contains("\"inputTokens\":5", json);
        Assert.Contains("\"estimated\":{", json);
        Assert.Contains("\"total\":{", json);
    }

    /// <summary>
    /// END TO END through the real chain: a completion delivered to the real
    /// <see cref="GoalDispatcher"/> stamps the usage in memory and the REAL
    /// <see cref="PipelineStore"/> round-trip (restart survival) carries every field back, so the
    /// stored phase record — not just a hand-rolled serialize — is what is asserted.
    /// </summary>
    [Fact]
    public async Task StampedUsage_SurvivesTheRealPipelineStoreRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance);

        var goal = new Goal
        {
            Id = $"goal-usage-restart-{Guid.NewGuid():N}",
            Description = "Usage survives a restart",
            RepositoryNames = ["CopilotHive"],
        };
        var goalSource = new PhaseUsageFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(ct);

        var pipelineManager = new GoalPipelineManager(store);
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Review);
        pipeline.AdvanceTo(GoalPhase.Review);
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Review, pipeline.Iteration, 1));

        var taskId = $"task-usage-restart-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: new PhaseUsageFakeBrain(),
            config: new HiveConfigFile { Repositories = [] });

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "review done",
            Metrics = new TaskMetrics { Verdict = "APPROVE", Summary = "approved" },
            Usage = PhaseUsageFixtures.BuildFullUsage(),
        }, ct);

        // Restart survival: a fresh load from the store, not the in-memory pipeline.
        var snapshot = store.LoadPipeline(goal.Id);
        Assert.NotNull(snapshot);
        var persistedPhase = Assert.Single(snapshot!.PhaseLog, e => e.Name == GoalPhase.Review);
        PhaseUsageFixtures.AssertFullUsage(persistedPhase.Usage);
    }

    /// <summary>Minimal goal source so the dispatcher's lifecycle writes reach a known goal.</summary>
    private sealed class PhaseUsageFakeGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "usage-fake-source";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            goal.Status = status;
            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal brain that approves and produces no merge work.</summary>
    private sealed class PhaseUsageFakeBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(
            string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }
}
