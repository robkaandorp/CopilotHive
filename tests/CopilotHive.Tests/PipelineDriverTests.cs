using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Logger that records the last logged exception so tests can prove a swallowed drive/finalize
/// error did not occur (namespace-scope so multiple test classes in this file can use it).
/// </summary>
internal sealed class PipelineDriverCapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    internal Exception? LastException { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (exception is not null)
            LastException = exception;
    }
}

/// <summary>
/// Tests that <see cref="PipelineDriver.DriveNextPhaseAsync"/> preserves the complete
/// authoritative phase report for every worker phase: a nonblank
/// <see cref="TaskMetrics.Summary"/> when present, otherwise <see cref="TaskResult.Output"/>.
/// </summary>
public sealed class PipelineDriverWorkerOutputTests
{
    // ── Test 1: Summary is preferred over Output ─────────────────────────

    [Fact]
    public async Task DriveNextPhaseAsync_WhenMetricsSummaryPresent_UsesMetricsSummaryAsWorkerOutput()
    {
        // Arrange
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review);
        AddPhaseEntry(pipeline, GoalPhase.Review);

        const string summaryText = "Detailed review findings: 3 issues found.";
        const string rawOutput = "changes"; // single word the LLM emits

        // Act
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            Metrics = new TaskMetrics
            {
                Verdict = "REQUEST_CHANGES",
                Summary = summaryText,
            },
        }, TestContext.Current.CancellationToken);

        // Assert: WorkerOutput should be the summary, not the raw LLM output
        Assert.Equal(summaryText, pipeline.PhaseLog[0].WorkerOutput);
        Assert.NotEqual(rawOutput, pipeline.PhaseLog[0].WorkerOutput);
    }

    // ── Test 2: Falls back to Output when Summary is absent or whitespace ─

    [Theory]
    [InlineData(null)]       // Metrics is null
    [InlineData("")]         // Summary is empty string
    [InlineData("   ")]      // Summary is whitespace only
    public async Task DriveNextPhaseAsync_WhenMetricsSummaryAbsentOrWhitespace_UsesRawOutput(string? summary)
    {
        // Arrange
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review);
        AddPhaseEntry(pipeline, GoalPhase.Review);

        const string rawOutput = "All looks good.";

        // Act
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            Metrics = summary is null
                ? null
                : new TaskMetrics { Verdict = "APPROVE", Summary = summary },
        }, TestContext.Current.CancellationToken);

        // Assert: WorkerOutput should be the raw output when Summary is absent/whitespace
        Assert.Equal(rawOutput, pipeline.PhaseLog[0].WorkerOutput);
    }

    // ── Legacy 4,000-character boundary regressions ──────────────────────

    [Theory]
    [InlineData(3_999)]
    [InlineData(4_000)]
    [InlineData(4_001)]
    public async Task DriveNextPhaseAsync_WhenReviewSummaryCrossesLegacyBoundary_PreservesExactly(int length)
    {
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review);
        AddPhaseEntry(pipeline, GoalPhase.Review);
        var summary = BuildExactLengthReport(length, 'S');

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "different raw review output",
            Metrics = new TaskMetrics { Verdict = "APPROVE", Summary = summary },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(summary, pipeline.PhaseLog[0].WorkerOutput);
    }

    [Theory]
    [InlineData(3_999)]
    [InlineData(4_000)]
    [InlineData(4_001)]
    public async Task DriveNextPhaseAsync_WhenReviewRawOutputCrossesLegacyBoundary_PreservesExactly(int length)
    {
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review);
        AddPhaseEntry(pipeline, GoalPhase.Review);
        var rawOutput = BuildExactLengthReport(length, 'O');

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            Metrics = new TaskMetrics { Verdict = "APPROVE" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(rawOutput, pipeline.PhaseLog[0].WorkerOutput);
    }

    // ── All five normal worker phases preserve both selection paths ──────

    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.Review)]
    [InlineData(GoalPhase.DocWriting)]
    [InlineData(GoalPhase.Improve)]
    public async Task DriveNextPhaseAsync_AllWorkerPhases_PreserveRealisticStructuredSummaryExactly(GoalPhase phase)
    {
        var (dispatcher, pipeline, taskId) = CreateDispatcher(phase);
        AddPhaseEntry(pipeline, phase);
        var summary = BuildRealisticPhaseReport(phase, "structured-summary");
        Assert.True(summary.Length > 8 * 1024);
        var rawOutput = $"DIFFERENT-RAW-OUTPUT:{phase}";

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = phase == GoalPhase.Coding
                ? new GitChangeSummary { FilesChanged = 2, Pushed = true, ChangedFiles = ["src/Feature.cs", "tests/FeatureTests.cs"] }
                : null,
            Metrics = new TaskMetrics
            {
                Verdict = phase == GoalPhase.Review ? "APPROVE" : "PASS",
                Summary = summary,
            },
        }, TestContext.Current.CancellationToken);

        var completedEntry = pipeline.PhaseLog[0];
        Assert.Equal(summary, completedEntry.WorkerOutput);
        Assert.Equal(PhaseOutcome.Pass, completedEntry.Result);
        Assert.NotNull(completedEntry.CompletedAt);
        if (phase == GoalPhase.Coding)
            Assert.NotEqual("Coder produced no file changes (no-op)", completedEntry.WorkerOutput);
    }

    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.Review)]
    [InlineData(GoalPhase.DocWriting)]
    [InlineData(GoalPhase.Improve)]
    public async Task DriveNextPhaseAsync_AllWorkerPhases_PreserveRealisticRawOutputFallbackExactly(GoalPhase phase)
    {
        var (dispatcher, pipeline, taskId) = CreateDispatcher(phase);
        AddPhaseEntry(pipeline, phase);
        var rawOutput = BuildRealisticPhaseReport(phase, "raw-output-fallback");
        Assert.True(rawOutput.Length > 8 * 1024);

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = phase == GoalPhase.Coding
                ? new GitChangeSummary { FilesChanged = 1, Pushed = true, ChangedFiles = ["src/Feature.cs"] }
                : null,
            Metrics = new TaskMetrics { Verdict = phase == GoalPhase.Review ? "APPROVE" : "PASS" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(rawOutput, pipeline.PhaseLog[0].WorkerOutput);
    }

    // ── Testing phase: FULL report preservation (no truncation, no suffix) ──

    [Theory]
    [InlineData(3999)]  // below the legacy cap
    [InlineData(4000)]  // exactly at the legacy cap
    [InlineData(4001)]  // just above the legacy cap
    [InlineData(8_500)] // realistic tester report size
    public async Task DriveNextPhaseAsync_WhenTestingSummary_PreservesFullSummaryExactly(int length)
    {
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Testing);
        AddPhaseEntry(pipeline, GoalPhase.Testing);

        // Distinctive evidence in every region: head, past char 4000, and the very end.
        var summary = "HEAD:" + new string('S', length - 10) + "TAIL:";

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "raw",
            Metrics = new TaskMetrics
            {
                Verdict = "PASS",
                Summary = summary,
            },
        }, TestContext.Current.CancellationToken);

        // EXACT equality — not prefix, not length, not merely absence of the suffix.
        Assert.Equal(summary, pipeline.PhaseLog[0].WorkerOutput);
        Assert.Equal(length, pipeline.PhaseLog[0].WorkerOutput!.Length);
    }

    [Theory]
    [InlineData(3999)]
    [InlineData(4000)]
    [InlineData(4001)]
    [InlineData(8_500)]
    public async Task DriveNextPhaseAsync_WhenTestingRawOutputFallback_PreservesFullRawOutputExactly(int length)
    {
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Testing);
        AddPhaseEntry(pipeline, GoalPhase.Testing);

        var rawOutput = "HEAD:" + new string('O', length - 10) + "TAIL:";

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            Metrics = new TaskMetrics { Verdict = "FAIL" }, // no Summary
        }, TestContext.Current.CancellationToken);

        Assert.Equal(rawOutput, pipeline.PhaseLog[0].WorkerOutput);
        Assert.Equal(length, pipeline.PhaseLog[0].WorkerOutput!.Length);
    }

    /// <summary>
    /// A realistic 6–10KB tester report with distinctive mutation evidence beyond character
    /// 4,000 and at the very end must survive EXACTLY — this is the incident: mutation evidence
    /// past 4,000 chars was being cut off, forcing reviewer clarification.
    /// </summary>
    [Fact]
    public async Task DriveNextPhaseAsync_WhenTestingRealisticReport_PreservesEvidencePast4000AndTrailing()
    {
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Testing);
        AddPhaseEntry(pipeline, GoalPhase.Testing);

        var report = BuildRealisticTesterReport();

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "PASS",
            Metrics = new TaskMetrics
            {
                Verdict = "PASS",
                Summary = report,
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(report, pipeline.PhaseLog[0].WorkerOutput);
        Assert.Contains("MUTATION-KILL-EVIDENCE-BEYOND-4000", pipeline.PhaseLog[0].WorkerOutput);
        Assert.EndsWith("TRAILING-EVIDENCE-AT-END: all 636 tests green.", pipeline.PhaseLog[0].WorkerOutput);
        Assert.DoesNotContain("chars total", pipeline.PhaseLog[0].WorkerOutput);
    }

    /// <summary>
    /// Null/empty/whitespace summary and absent metrics fall back to the raw output during
    /// Testing, and the raw output is preserved in full.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DriveNextPhaseAsync_WhenTestingSummaryAbsentOrWhitespace_PreservesFullRawOutput(string? summary)
    {
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Testing);
        AddPhaseEntry(pipeline, GoalPhase.Testing);

        var rawOutput = new string('O', 6000) + "\nTAIL-EVIDENCE";

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            Metrics = summary is null
                ? null
                : new TaskMetrics { Verdict = "FAIL", Summary = summary },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(rawOutput, pipeline.PhaseLog[0].WorkerOutput);
    }

    /// <summary>
    /// A FAILED Testing completion preserves the full report too — verdict must not gate the
    /// preservation.
    /// </summary>
    [Fact]
    public async Task DriveNextPhaseAsync_WhenTestingFails_PreservesFullReport()
    {
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Testing);
        AddPhaseEntry(pipeline, GoalPhase.Testing);

        var report = BuildRealisticTesterReport();

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "FAIL",
            Metrics = new TaskMetrics
            {
                Verdict = "FAIL",
                Summary = report,
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(report, pipeline.PhaseLog[0].WorkerOutput);
        Assert.Equal("FAIL", pipeline.PhaseLog[0].Verdict);
        Assert.Equal(PhaseOutcome.Fail, pipeline.PhaseLog[0].Result);
    }

    /// <summary>
    /// A nonblank summary still wins over a DIFFERENT raw output in Testing (selection
    /// semantics unchanged — only the truncation was removed).
    /// </summary>
    [Fact]
    public async Task DriveNextPhaseAsync_WhenTestingNonBlankSummaryStillWinsOverRawOutput()
    {
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Testing);
        AddPhaseEntry(pipeline, GoalPhase.Testing);

        var summary = "Summary with distinctive content: " + new string('S', 5000);
        var rawOutput = "DIFFERENT raw text entirely";

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = summary },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(summary, pipeline.PhaseLog[0].WorkerOutput);
        Assert.NotEqual(rawOutput, pipeline.PhaseLog[0].WorkerOutput);
    }

    // ── Continuous completion-to-SQLite and explicit live-snapshot storage ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleTaskCompletionAsync_ReviewRequestChanges_PersistsCompleteSelectedReportToGoalStore(
        bool useStructuredSummary)
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var (pipeline, report) = await CompleteReviewRequestChangesAsync(goalStore, useStructuredSummary);

        dbContext.ChangeTracker.Clear();
        var summaries = await goalStore.GetIterationsAsync(
            pipeline.GoalId, TestContext.Current.CancellationToken);
        var persisted = Assert.Single(summaries);
        Assert.Equal(1, persisted.Iteration);
        Assert.Equal(report,
            Assert.Single(persisted.Phases, p => p.Name == GoalPhase.Review).WorkerOutput);
        Assert.Equal(report, persisted.PhaseOutputs["reviewer-1"]);
        Assert.Equal(report, persisted.PhaseOutputs["reviewer-1-1"]);

        // REQUEST_CHANGES with retry budget must end iteration 1 and start Coding in iteration 2.
        // These assertions ensure a caught drive/persistence error cannot masquerade as success.
        Assert.Equal(2, pipeline.Iteration);
        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
        Assert.Equal(GoalPhase.Coding, pipeline.StateMachine.Phase);
    }

    [Fact]
    public async Task CompletionProducedPipeline_ExplicitPipelineStoreRoundTrip_PreservesCompleteReviewReport()
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var (pipeline, report) = await CompleteReviewRequestChangesAsync(goalStore, useStructuredSummary: true);
        await using var pipelineStore = new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance);

        // This explicitly exercises the live SavePipeline/LoadPipeline store path after the real
        // completion. The fixture does not claim automatic production persistence, process restart,
        // or deployment recovery verification.
        pipelineStore.SavePipeline(pipeline);
        dbContext.ChangeTracker.Clear();
        var snapshot = pipelineStore.LoadPipeline(pipeline.GoalId);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Iteration);
        Assert.Equal(GoalPhase.Coding, snapshot.Phase);
        Assert.Equal(report,
            Assert.Single(snapshot.PhaseLog,
                p => p.Name == GoalPhase.Review && p.Iteration == 1 && p.Occurrence == 1).WorkerOutput);
    }

    private static string BuildExactLengthReport(int length, char fill)
    {
        const string head = "HEAD:\n";
        const string tail = "\nTAIL";
        return head + new string(fill, length - head.Length - tail.Length) + tail;
    }

    private static string BuildRealisticPhaseReport(GoalPhase phase, string representation)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {phase} report ({representation})");
        sb.AppendLine("Build and verification evidence follows exactly as emitted by the worker.");
        while (sb.Length < 4_100)
            sb.AppendLine("Evidence row: deterministic finding, path, assertion, and observed result.");
        sb.AppendLine($"UNIQUE-EVIDENCE-BEYOND-OLD-BOUNDARY:{phase}:{representation}");
        while (sb.Length < 8_600)
            sb.AppendLine("Additional multiline detail: regression reasoning and reproducible observation.");
        sb.Append($"UNIQUE-TAIL-EVIDENCE:{phase}:{representation}");
        return sb.ToString();
    }

    private static async Task<(GoalPipeline Pipeline, string Report)> CompleteReviewRequestChangesAsync(
        GoalStore goalStore,
        bool useStructuredSummary)
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = new Goal
        {
            Id = $"goal-sqlite-report-{Guid.NewGuid():N}",
            Description = "Persist the complete review report",
            RepositoryNames = ["CopilotHive"],
        };
        await goalStore.CreateGoalAsync(goal, ct);

        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);
        Assert.Equal(goal.Id, (await goalManager.GetNextGoalAsync(ct))?.Id);

        // Deliberately omit PipelineStore from the manager. Iteration persistence below therefore
        // proves the GoalDispatcher → GoalStore chain; the separate test explicitly saves the live
        // completion-produced pipeline through PipelineStore.
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Review);
        pipeline.AdvanceTo(GoalPhase.Review);
        AddPhaseEntry(pipeline, GoalPhase.Review);

        var taskId = $"task-review-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);
        Assert.True(pipeline.SeedSlotForTest(
            taskId,
            new WorkSlotPosition(pipeline.Iteration, GoalPhase.Review, 1),
            attempt: 1,
            WorkSlotState.Pending));

        var logger = new PipelineDriverCapturingLogger<GoalDispatcher>();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: new LocalFakeBrain(),
            config: BuildDispatcherConfig());

        var report = BuildRealisticPhaseReport(
            GoalPhase.Review,
            useStructuredSummary ? "sqlite-structured-summary" : "sqlite-raw-fallback");
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = useStructuredSummary ? "different raw output" : report,
            Metrics = new TaskMetrics
            {
                Verdict = "REQUEST_CHANGES",
                Summary = useStructuredSummary ? report : "   ",
            },
        }, ct);

        Assert.False(pipeline.Phase == GoalPhase.Failed, logger.LastException?.ToString() ?? pipeline.Goal.FailureReason);
        Assert.Equal(WorkSlotState.Recorded,
            Assert.Single(pipeline.GetSlotsForTest(), s => s.Slot.TaskId == taskId).State);
        return (pipeline, report);
    }

    /// <summary>
    /// Builds a realistic ~8KB tester report with structured sections, newlines, and mutation
    /// evidence both just past char 4,000 and at the very end.
    /// </summary>
    private static string BuildRealisticTesterReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Test Report — iteration 1");
        sb.AppendLine("Build: success. Tests: 636 passed, 0 failed. Coverage: 78%.");
        while (sb.Length < 4_100)
            sb.AppendLine("Filler analysis line with stable content for realistic report shape.");
        sb.AppendLine("MUTATION-KILL-EVIDENCE-BEYOND-4000: mutant PipelineDriver.TruncateTesting removed → suite red.");
        while (sb.Length < 7_800)
            sb.AppendLine("Further section detail — metrics table rows and issue enumeration.");
        sb.Append("TRAILING-EVIDENCE-AT-END: all 636 tests green.");
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static HiveConfigFile BuildDispatcherConfig() => new()
    {
        Repositories =
        [
            new RepositoryConfig
            {
                Name = "CopilotHive",
                Url = "https://example.invalid/CopilotHive.git",
                DefaultBranch = "main",
            },
        ],
        Workers =
        {
            ["coder"] = new WorkerConfig { Model = "test-coder-model" },
            ["tester"] = new WorkerConfig { Model = "test-tester-model" },
            ["reviewer"] = new WorkerConfig { Model = "test-reviewer-model" },
            ["docwriter"] = new WorkerConfig { Model = "test-docwriter-model" },
            ["improver"] = new WorkerConfig { Model = "test-improver-model" },
        },
    };

    /// <summary>
    /// Builds a minimal self-contained <see cref="GoalDispatcher"/> for testing
    /// <c>DriveNextPhaseAsync</c> WorkerOutput assignment.
    /// </summary>
    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string taskId)
        CreateDispatcher(GoalPhase phase)
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["CopilotHive"],
        };

        var goalSource = new LocalFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // A one-phase plan reaches normal bookkeeping and then completes without invoking merge
        // infrastructure. Failed/request-changes verdicts still exercise the real retry path.
        var plan = new IterationPlan { Phases = [phase] };
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, phase);
        pipeline.AdvanceTo(phase);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);
        pipeline.SeedSlotForTest(
            taskId,
            new WorkSlotPosition(pipeline.Iteration, phase, 1),
            attempt: 1,
            WorkSlotState.Pending);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: new LocalFakeBrain(),
            config: BuildDispatcherConfig());

        return (dispatcher, pipeline, taskId);
    }

    /// <summary>
    /// Adds a <see cref="PhaseResult"/> for <paramref name="phase"/> to the pipeline's
    /// PhaseLog so that <c>CurrentPhaseEntry</c> is non-null when DriveNextPhaseAsync runs.
    /// </summary>
    private static void AddPhaseEntry(GoalPipeline pipeline, GoalPhase phase)
    {
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = phase,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
        });
    }

    /// <summary>Minimal goal source that returns a single pre-configured goal.</summary>
    private sealed class LocalFakeGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "local-fake";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>Minimal brain stub for pipeline driver tests.</summary>
    private sealed class LocalFakeBrain : IDistributedBrain
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

/// <summary>
/// Tests that <see cref="PipelineDriver.DriveNextPhaseAsync"/>'s no-op coder retry path
/// persists the no-op iteration summary (with Coding marked failed) BEFORE consuming the
/// iteration budget, so the iteration stays visible in the dashboard tab bar — the same
/// class of bug as the merge-fail iteration display issue fixed in v0.27.0.
/// </summary>
public sealed class PipelineDriverNoOpRetryTests
{
    // ── Test 1: no-op retry persists iteration summary with Coding = Fail ──

    [Fact]
    public async Task NoOpRetry_PersistsIterationSummaryWithCodingFail()
    {
        // Arrange: pipeline in Coding with a Coding PhaseResult for iteration 1, plus a
        // craft-prompt conversation entry for the retry iteration (2) so the BrainPrompt
        // forwarding on the retry entry is observable.
        const string retryPrompt = "retry with stronger prompt";
        const string craftPrompt = "Brain craft prompt for retry";
        var (driver, pipeline, goalStore) = CreateNoOpDriver();
        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        // GetLastCraftPromptFromConversation runs AFTER TryConsume() has advanced the pipeline
        // to iteration 2, so the seed entry must carry the retry iteration number.
        pipeline.Conversation.Add(new ConversationEntry("user", craftPrompt, 2, "craft-prompt"));

        // Act: coder returns with 0 files changed → no-op retry path.
        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-1",
            Status = TaskOutcome.Completed,
            Output = "I discussed the changes but made no edits.",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
        }, TestContext.Current.CancellationToken);

        // Assert: the Coding PhaseResult was marked failed with the no-op reason and the
        // worker's raw report preserved verbatim (raw fallback — no Metrics.Summary).
        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        Assert.Equal(
            "Coder produced no file changes (no-op)\n\nI discussed the changes but made no edits.",
            codingEntry.WorkerOutput);
        Assert.NotNull(codingEntry.CompletedAt);

        // Assert: UpdateGoalStatusAsync was called while pipeline.Iteration was still the
        // PRE-consume value (1). If the persist call were moved after
        // IterationBudget.TryConsume(), pipeline.Iteration would be 2 at call time and this
        // assertion would fail — proving the summary is persisted BEFORE the budget is consumed.
        var inProgressUpdate = Assert.Single(goalStore.StatusUpdates, u => u.Status == GoalStatus.InProgress);
        Assert.Equal(1, inProgressUpdate.IterationAtUpdate);

        // Assert: the persisted summary carries the pre-consume iteration with Coding = Fail.
        var summaryUpdate = inProgressUpdate.Metadata?.IterationSummary;
        Assert.NotNull(summaryUpdate);
        Assert.Equal(1, summaryUpdate!.Iteration);
        var codingInSummary = Assert.Single(summaryUpdate.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(PhaseOutcome.Fail, codingInSummary.Result);
        Assert.Equal(
            "Coder produced no file changes (no-op)\n\nI discussed the changes but made no edits.",
            codingInSummary.WorkerOutput);

        // Assert: the summary is also in CompletedIterationSummaries and the retry iteration started.
        var completedSummary = Assert.Single(pipeline.CompletedIterationSummaries, s => s.Iteration == 1);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(completedSummary.Phases, p => p.Name == GoalPhase.Coding).Result);
        Assert.Equal(2, pipeline.Iteration);

        // Assert: a fresh Coding PhaseResult was added for the retry iteration with the exact
        // retry prompt and the forwarded Brain craft prompt. Removing the WorkerPrompt/BrainPrompt
        // assignments from PipelineDriver.cs would fail these assertions.
        Assert.Equal(2, pipeline.PhaseLog.Count);
        var retryEntry = pipeline.PhaseLog[1];
        Assert.Equal(GoalPhase.Coding, retryEntry.Name);
        Assert.Equal(2, retryEntry.Iteration);
        Assert.Equal(1, retryEntry.Occurrence);
        Assert.Equal(retryPrompt, retryEntry.WorkerPrompt);
        Assert.Equal(craftPrompt, retryEntry.BrainPrompt);
    }

    // ── Test 2: budget-exhausted terminal path fails without duplicate summary ──

    [Fact]
    public async Task NoOpRetry_BudgetExhausted_FailsWithoutDuplicateSummary()
    {
        // Arrange: exhaust the iteration budget (maxIterations = 5 → IterationBudget allows 4)
        // so the no-op path takes the terminal IsExhausted branch.
        var (driver, pipeline, goalStore) = CreateNoOpDriver();
        for (var i = 0; i < 4; i++)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);

        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        var summariesBefore = pipeline.CompletedIterationSummaries.Count;
        var iterationBefore = pipeline.Iteration; // 5 after exhausting the iteration budget

        // Act
        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-2",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
        }, TestContext.Current.CancellationToken);

        // Assert: the Coding PhaseResult was marked failed with the no-op reason plus the raw
        // report (raw fallback — no Metrics.Summary on this vector) BEFORE terminal exit, so
        // FinalizeGoalAsync's summary includes the failed Coding phase with the full report.
        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        Assert.Equal(
            "Coder produced no file changes (no-op)\n\nno changes made",
            codingEntry.WorkerOutput);
        Assert.NotNull(codingEntry.CompletedAt);

        // Assert: goal failed via the terminal path.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);

        // Assert: DriveNextPhaseAsync did NOT add a pre-retry snapshot to
        // CompletedIterationSummaries — the only addition is the terminal summary from
        // FinalizeGoalAsync (exactly one, not two).
        Assert.Equal(summariesBefore, pipeline.CompletedIterationSummaries.Count - 1);
        var summary = Assert.Single(pipeline.CompletedIterationSummaries);
        Assert.Equal(iterationBefore, summary.Iteration);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(summary.Phases, p => p.Name == GoalPhase.Coding).Result);

        // Assert: exactly ONE status update carried an IterationSummary (the Failed one from
        // FinalizeGoalAsync) — the no-op path did NOT create an InProgress one.
        var summaryUpdates = goalStore.StatusUpdates
            .Where(u => u.Metadata?.IterationSummary is not null)
            .ToList();
        var failedUpdate = Assert.Single(summaryUpdates);
        Assert.Equal(GoalStatus.Failed, failedUpdate.Status);
    }

    // ── Test 3: completed summary is not mutated by the retry's result ──

    [Fact]
    public async Task NoOpRetry_CompletedSummaryNotMutatedByRetry()
    {
        // Arrange: fake dispatch that immediately completes the retry coding task by writing
        // the completion data onto the retry's PhaseResult (what DriveNextPhaseAsync would do
        // when the retry worker reports back).
        var (driver, pipeline, _) = CreateNoOpDriver((p, role, prompt, ct) =>
        {
            var retryEntry = p.CurrentPhaseEntry!;
            retryEntry.Result = PhaseOutcome.Pass;
            retryEntry.CompletedAt = DateTime.UtcNow;
            retryEntry.WorkerOutput = "retry produced changes";
            return Task.CompletedTask;
        });

        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        // Act
        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-3",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
        }, TestContext.Current.CancellationToken);

        // Assert: the CompletedIterationSummaries entry for the no-op iteration still has
        // Coding = Fail with the no-op output — the retry's completion wrote to the NEW
        // iteration-2 PhaseResult, not the failed iteration-1 entry captured in the summary.
        var completedSummary = Assert.Single(pipeline.CompletedIterationSummaries, s => s.Iteration == 1);
        var codingInSummary = Assert.Single(completedSummary.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(PhaseOutcome.Fail, codingInSummary.Result);
        // EXACT full no-op report: the retry's completion ("retry produced changes") must not
        // have overwritten the failed iteration-1 entry's reason-plus-report.
        Assert.Equal(
            "Coder produced no file changes (no-op)\n\nno changes made",
            codingInSummary.WorkerOutput);

        // Assert: the PhaseLog holds two distinct Coding entries — the failed iteration-1 entry
        // and the retry's iteration-2 entry (which the fake dispatch completed as Pass).
        Assert.Equal(2, pipeline.PhaseLog.Count);
        var failedEntry = pipeline.PhaseLog[0];
        var retryEntry = pipeline.PhaseLog[1];
        Assert.Equal(PhaseOutcome.Fail, failedEntry.Result);
        Assert.Equal(1, failedEntry.Iteration);
        Assert.Equal(PhaseOutcome.Pass, retryEntry.Result);
        Assert.Equal(2, retryEntry.Iteration);
        Assert.Equal("retry produced changes", retryEntry.WorkerOutput);

        // Assert: the summary's Coding entry and the new PhaseLog entry are DISTINCT objects —
        // the retry's completion could not have mutated the captured summary entry. Without the
        // fresh iteration-2 PhaseResult in PipelineDriver.cs, CurrentPhaseEntry would still be
        // the failed iteration-1 entry, the fake dispatch would write Pass onto it, and the
        // summary (which references that same object) would show Pass — failing this assertion.
        Assert.NotSame(codingInSummary, retryEntry);
        // The summary's Coding entry references the failed iteration-1 PhaseResult (BuildIterationSummary
        // copies entry references), so it MUST be the same object as the failed PhaseLog entry —
        // which the retry entry (a distinct object) could not have mutated.
        Assert.Same(codingInSummary, failedEntry);
    }

    // ── Test 4: report selection — summary wins over raw output, stored verbatim ──

    /// <summary>
    /// Realistic 8KB+ multiline coder report with distinct evidence markers past char 500,
    /// past char 4,000, and at the very tail. The raw <see cref="TaskResult.Output"/> is
    /// deliberately DIFFERENT, proving summary-wins AND no concatenation; the stored string
    /// must be exactly the reason + "\n\n" + the verbatim summary (no trimming, no cap).
    /// </summary>
    [Fact]
    public async Task NoOpRetry_SummarySelected_StoresReasonPlusFullSummaryVerbatim()
    {
        var (driver, pipeline, goalStore) = CreateNoOpDriver();
        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        // Realistic multiline report: evidence beyond char 500, beyond char 4,000, and at the tail.
        const string reportHead = "NO-OP REPORT HEAD: attempted the change, hit a blocker.\n";
        const string reportBeyond500 = "EVIDENCE-BEYOND-500: a legacy 500-char preview would lose this marker.\n";
        const string reportBeyond4000 = "EVIDENCE-BEYOND-4000: a legacy 4,000-char cap would lose this marker.\n";
        const string reportTail = "TAIL-EVIDENCE-AT-END: root cause analysis concludes the fix needs a config change.";
        var report = reportHead
            + new string('a', 500 - reportHead.Length) + reportBeyond500
            + new string('b', 4_000 - 500 - reportBeyond500.Length) + reportBeyond4000
            + new string('c', 5_500) + reportTail;
        Assert.True(report.Length > 8 * 1024);
        Assert.NotEqual(500, report.IndexOf(reportBeyond500, StringComparison.Ordinal) + reportBeyond500.Length);
        Assert.Equal(reportTail, report[(report.Length - reportTail.Length)..]);

        // A DIFFERENT raw output — if it were concatenated (or won) the exact assertion fails.
        const string rawOutput = "DIFFERENT-RAW-OUTPUT: discussed the approach without editing files.";

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-summary",
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = report },
        }, TestContext.Current.CancellationToken);

        // Assert: EXACT full stored string — reason, blank line, then the verbatim summary.
        var expected = "Coder produced no file changes (no-op)\n\n" + report;
        Assert.Equal(expected, codingEntry.WorkerOutput);
        Assert.StartsWith("Coder produced no file changes (no-op)\n\n" + reportHead, codingEntry.WorkerOutput);
        Assert.EndsWith(reportTail, codingEntry.WorkerOutput);
        Assert.Contains("EVIDENCE-BEYOND-500", codingEntry.WorkerOutput);
        Assert.Contains("EVIDENCE-BEYOND-4000", codingEntry.WorkerOutput);
        // No concatenation of the competing raw output.
        Assert.DoesNotContain(rawOutput, codingEntry.WorkerOutput);

        // The persisted pre-consume summary (single InProgress update at iteration 1) carries
        // the exact same reason-plus-report string.
        var inProgressUpdate = Assert.Single(goalStore.StatusUpdates, u => u.Status == GoalStatus.InProgress);
        Assert.Equal(1, inProgressUpdate.IterationAtUpdate);
        var summaryUpdate = inProgressUpdate.Metadata?.IterationSummary;
        Assert.NotNull(summaryUpdate);
        var codingInSummary = Assert.Single(summaryUpdate!.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(expected, codingInSummary.WorkerOutput);
    }

    /// <summary>Raw fallback: null/empty/whitespace Metrics.Summary → full raw Output verbatim.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoOpRetry_SummaryAbsentOrWhitespace_StoresReasonPlusFullRawOutputVerbatim(string? summary)
    {
        var (driver, pipeline, _) = CreateNoOpDriver();
        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        // 8KB+ raw output with the same evidence placement as the summary vector.
        const string rawHead = "NO-OP RAW HEAD: no edits made, only analysis.\n";
        const string rawBeyond500 = "RAW-EVIDENCE-BEYOND-500: survives only with verbatim storage.\n";
        const string rawBeyond4000 = "RAW-EVIDENCE-BEYOND-4000: survives only with verbatim storage.\n";
        const string rawTail = "RAW-TAIL-EVIDENCE-AT-END: recommendation is to raise the timeout.";
        var rawOutput = rawHead
            + new string('x', 500 - rawHead.Length) + rawBeyond500
            + new string('y', 4_000 - 500 - rawBeyond500.Length) + rawBeyond4000
            + new string('z', 5_500) + rawTail;
        Assert.True(rawOutput.Length > 8 * 1024);
        Assert.EndsWith(rawTail, rawOutput);

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-raw",
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = summary is null
                ? null
                : new TaskMetrics { Verdict = "PASS", Summary = summary },
        }, TestContext.Current.CancellationToken);

        // Assert: EXACT full stored string — reason, blank line, then the verbatim raw output.
        var expected = "Coder produced no file changes (no-op)\n\n" + rawOutput;
        Assert.Equal(expected, codingEntry.WorkerOutput);
        Assert.EndsWith(rawTail, codingEntry.WorkerOutput);
        Assert.Contains("RAW-EVIDENCE-BEYOND-500", codingEntry.WorkerOutput);
        Assert.Contains("RAW-EVIDENCE-BEYOND-4000", codingEntry.WorkerOutput);
    }

    /// <summary>Empty or whitespace selected report → the reason alone, exactly as before.</summary>
    [Theory]
    [InlineData(null, null)]     // no Metrics, no Output
    [InlineData("", "")]         // empty summary + empty output
    [InlineData("  ", " \t\n ")] // whitespace summary + whitespace raw output → reason alone
    [InlineData(null, " \r\n ")] // whitespace raw output with no summary → reason alone
    public async Task NoOpRetry_EmptyOrWhitespaceReport_StoresReasonAlone(string? summary, string? output)
    {
        var (driver, pipeline, _) = CreateNoOpDriver();
        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-empty",
            Status = TaskOutcome.Completed,
            Output = output ?? "",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = summary is null
                ? null
                : new TaskMetrics { Verdict = "PASS", Summary = summary },
        }, TestContext.Current.CancellationToken);

        // Assert: reason alone — no trailing separators, no empty report section.
        Assert.Equal("Coder produced no file changes (no-op)", codingEntry.WorkerOutput);
        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        Assert.NotNull(codingEntry.CompletedAt);

        // Retry path still ran: budget consumed, fresh retry entry added for iteration 2.
        Assert.Equal(2, pipeline.Iteration);
        Assert.Equal(2, pipeline.PhaseLog.Count);
        Assert.Equal(GoalPhase.Coding, pipeline.PhaseLog[1].Name);
    }

    // ── Test 5: budget-exhausted path preserves the selected report too ──

    [Fact]
    public async Task NoOpRetry_BudgetExhausted_SummarySelected_StoresReasonPlusFullSummary()
    {
        // Arrange: exhaust the iteration budget so the no-op path takes the terminal branch.
        var (driver, pipeline, goalStore) = CreateNoOpDriver();
        for (var i = 0; i < 4; i++)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);

        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        var report = BuildNoOpReport("SUMMARY-SELECTED-TERMINAL");
        const string rawOutput = "DIFFERENT-RAW-OUTPUT-TERMINAL: not stored when the summary wins.";

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-terminal-summary",
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = report },
        }, TestContext.Current.CancellationToken);

        // Assert: the failed entry carries reason + verbatim summary (no truncation, no concat).
        var expected = "Coder produced no file changes (no-op)\n\n" + report;
        Assert.Equal(expected, codingEntry.WorkerOutput);

        // Assert: goal failed via the terminal path; exactly ONE summary-bearing status update
        // (the Failed one from FinalizeGoalAsync) — the no-op path added no InProgress summary.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        var summaryUpdates = goalStore.StatusUpdates
            .Where(u => u.Metadata?.IterationSummary is not null)
            .ToList();
        var failedUpdate = Assert.Single(summaryUpdates);
        Assert.Equal(GoalStatus.Failed, failedUpdate.Status);
        var codingInSummary = Assert.Single(
            failedUpdate.Metadata!.IterationSummary!.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(expected, codingInSummary.WorkerOutput);
    }

    /// <summary>Raw fallback on the terminal path — the full raw Output is preserved.</summary>
    [Fact]
    public async Task NoOpRetry_BudgetExhausted_RawFallback_StoresReasonPlusFullRawOutput()
    {
        var (driver, pipeline, goalStore) = CreateNoOpDriver();
        for (var i = 0; i < 4; i++)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);

        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        var rawOutput = BuildNoOpReport("RAW-FALLBACK-TERMINAL");

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-terminal-raw",
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = "   " },
        }, TestContext.Current.CancellationToken);

        var expected = "Coder produced no file changes (no-op)\n\n" + rawOutput;
        Assert.Equal(expected, codingEntry.WorkerOutput);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        var failedUpdate = Assert.Single(goalStore.StatusUpdates, u => u.Metadata?.IterationSummary is not null);
        Assert.Equal(GoalStatus.Failed, failedUpdate.Status);
        var codingInSummary = Assert.Single(
            failedUpdate.Metadata!.IterationSummary!.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(expected, codingInSummary.WorkerOutput);
    }

    /// <summary>
    /// The completed summary captured at the no-op iteration is never overwritten when the
    /// retry completes — full-report equality, not a substring check (extends Test 3).
    /// </summary>
    [Fact]
    public async Task NoOpRetry_RetryCompletion_CannotOverwriteFullNoOpReportInSummary()
    {
        // Arrange: fake dispatch that immediately completes the retry coding task.
        var (driver, pipeline, _) = CreateNoOpDriver((p, role, prompt, ct) =>
        {
            var retryEntry = p.CurrentPhaseEntry!;
            retryEntry.Result = PhaseOutcome.Pass;
            retryEntry.CompletedAt = DateTime.UtcNow;
            retryEntry.WorkerOutput = "retry produced changes";
            return Task.CompletedTask;
        });

        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        var report = BuildNoOpReport("MUTATION-GUARD");
        const string rawOutput = "no changes made";

        // Act
        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-guard",
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = report },
        }, TestContext.Current.CancellationToken);

        // Assert: the no-op iteration's summary still holds the EXACT reason-plus-summary report.
        var completedSummary = Assert.Single(pipeline.CompletedIterationSummaries, s => s.Iteration == 1);
        var codingInSummary = Assert.Single(completedSummary.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(PhaseOutcome.Fail, codingInSummary.Result);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, codingInSummary.WorkerOutput);

        // Assert: the retry entry is a distinct object carrying only the retry's output.
        var retryEntry = pipeline.PhaseLog[1];
        Assert.Equal(2, retryEntry.Iteration);
        Assert.Equal("retry produced changes", retryEntry.WorkerOutput);
        Assert.NotSame(codingInSummary, retryEntry);
        Assert.Same(codingInSummary, pipeline.PhaseLog[0]);
    }

    // ── Test 5b: historical entries remain untouched (acceptance criterion 4) ──

    /// <summary>
    /// Seeds an older-iteration Coding entry, an earlier occurrence of the current iteration,
    /// and the LAST current-iteration occurrence the no-op path targets, plus an unrelated
    /// trailing Testing entry. Only the last current-iteration occurrence may receive the exact
    /// reason-plus-report string (summary-selected form) — every other entry must retain its
    /// original WorkerOutput/Result/CompletedAt verbatim (retry-available path).
    /// </summary>
    [Fact]
    public async Task NoOpRetry_HistoricalEntries_RetryPath_LastOccurrenceWins_OthersUntouched()
    {
        var (driver, pipeline, goalStore) = CreateNoOpDriver();

        // Older iteration's entry (must stay untouched).
        var olderIterationCompletedAt = DateTime.UtcNow.AddMinutes(-9);
        var olderIterationEntry = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = 0,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = olderIterationCompletedAt,
            WorkerPrompt = "older iteration prompt",
            WorkerOutput = "OLDER-ITERATION-EVIDENCE: previous iteration succeeded",
        };
        pipeline.PhaseLog.Add(olderIterationEntry);

        // Earlier occurrence of the current iteration (must stay untouched).
        var earlierOccurrenceCompletedAt = DateTime.UtcNow.AddMinutes(-1);
        var earlierOccurrence = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = earlierOccurrenceCompletedAt,
            WorkerPrompt = "first occurrence prompt",
            WorkerOutput = "FIRST-OCCURRENCE-EVIDENCE: first attempt output",
        };
        pipeline.PhaseLog.Add(earlierOccurrence);

        // LAST occurrence of the current iteration — the entry the no-op path targets.
        var lastOccurrence = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 2);
        pipeline.PhaseLog.Add(lastOccurrence);

        // Unrelated trailing phase entry (must stay untouched).
        var unrelatedTrailing = new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddSeconds(-20),
            WorkerOutput = "UNRELATED-TRAILING-EVIDENCE: tester output",
        };
        pipeline.PhaseLog.Add(unrelatedTrailing);

        var report = BuildNoOpReport("HISTORICAL-RETRY");

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-historical",
            Status = TaskOutcome.Completed,
            Output = "no changes made",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = report },
        }, TestContext.Current.CancellationToken);

        // Assert: the LAST current-iteration occurrence carries the exact reason-plus-report.
        Assert.Equal(PhaseOutcome.Fail, lastOccurrence.Result);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, lastOccurrence.WorkerOutput);
        Assert.NotNull(lastOccurrence.CompletedAt);

        // Assert: the older-iteration entry retains its original state verbatim — including its
        // seeded CompletedAt, proven with EXACT equality.
        Assert.Equal(PhaseOutcome.Pass, olderIterationEntry.Result);
        Assert.Equal("OLDER-ITERATION-EVIDENCE: previous iteration succeeded", olderIterationEntry.WorkerOutput);
        Assert.Equal("older iteration prompt", olderIterationEntry.WorkerPrompt);
        Assert.Equal(olderIterationCompletedAt, olderIterationEntry.CompletedAt);

        // Assert: the earlier occurrence retains its original state verbatim (Result, timestamps).
        Assert.Equal(PhaseOutcome.Pass, earlierOccurrence.Result);
        Assert.Equal("FIRST-OCCURRENCE-EVIDENCE: first attempt output", earlierOccurrence.WorkerOutput);
        Assert.Equal("first occurrence prompt", earlierOccurrence.WorkerPrompt);
        Assert.Equal(earlierOccurrenceCompletedAt, earlierOccurrence.CompletedAt);

        // Assert: the unrelated trailing entry retains its original state verbatim.
        Assert.Equal(PhaseOutcome.Pass, unrelatedTrailing.Result);
        Assert.Equal("UNRELATED-TRAILING-EVIDENCE: tester output", unrelatedTrailing.WorkerOutput);

        // Assert: the persisted pre-consume summary (iteration 1) contains the three
        // current-iteration entries — the earlier Coding occurrence, the targeted Coding
        // occurrence (with the reason-plus-report), and the unrelated Testing entry —
        // since BuildIterationSummary filters to the current iteration. The older
        // iteration's entry is protected by the separate direct assertions above, not
        // included in this summary. (Summary content is checked below; the untouched
        // state of each entry is asserted directly on the PhaseLog objects.)
        var inProgressUpdate = Assert.Single(goalStore.StatusUpdates, u => u.Status == GoalStatus.InProgress);
        var summaryUpdate = inProgressUpdate.Metadata?.IterationSummary;
        Assert.NotNull(summaryUpdate);
        Assert.Equal(1, summaryUpdate!.Iteration);
        var codingInSummary = summaryUpdate.Phases
            .Where(p => p.Name == GoalPhase.Coding)
            .ToList();
        Assert.Equal(2, codingInSummary.Count);
        Assert.Contains(codingInSummary, p => ReferenceEquals(p, earlierOccurrence) || p.WorkerOutput == earlierOccurrence.WorkerOutput);
        var summaryLastOccurrence = Assert.Single(
            codingInSummary, p => p.Occurrence == 2);
        Assert.Equal(PhaseOutcome.Fail, summaryLastOccurrence.Result);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, summaryLastOccurrence.WorkerOutput);
        var summaryEarlierOccurrence = Assert.Single(codingInSummary, p => p.Occurrence == 1);
        Assert.Equal(PhaseOutcome.Pass, summaryEarlierOccurrence.Result);
        Assert.Equal("FIRST-OCCURRENCE-EVIDENCE: first attempt output", summaryEarlierOccurrence.WorkerOutput);
    }

    /// <summary>
    /// Historical-entry preservation on the budget-exhausted TERMINAL path: the same seeding
    /// shape, with a NONEMPTY summary-selected report on the targeted entry — the mutation-
    /// sensitive vector (an empty report would produce the identical reason-alone string under
    /// the old constant-only assignment, making the case structurally mutation-incapable).
    /// Every other entry must stay verbatim.
    /// </summary>
    [Fact]
    public async Task NoOpRetry_HistoricalEntries_BudgetExhausted_LastOccurrenceWins_OthersUntouched()
    {
        // Arrange: exhaust the iteration budget so the no-op path takes the terminal branch.
        var (driver, pipeline, goalStore) = CreateNoOpDriver();
        for (var i = 0; i < 4; i++)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);

        // Older iteration's entry (must stay untouched).
        var olderIterationCompletedAt = DateTime.UtcNow.AddMinutes(-9);
        var olderIterationEntry = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = 0,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = olderIterationCompletedAt,
            WorkerOutput = "OLDER-ITERATION-EVIDENCE-TERMINAL: previous iteration output",
        };
        pipeline.PhaseLog.Add(olderIterationEntry);

        // Earlier occurrence of the current iteration (must stay untouched).
        var earlierOccurrenceCompletedAt = DateTime.UtcNow.AddMinutes(-1);
        var earlierOccurrence = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = earlierOccurrenceCompletedAt,
            WorkerOutput = "FIRST-OCCURRENCE-EVIDENCE-TERMINAL: first attempt output",
        };
        pipeline.PhaseLog.Add(earlierOccurrence);

        // LAST occurrence of the current iteration — the entry the no-op path targets.
        var lastOccurrence = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 2);
        pipeline.PhaseLog.Add(lastOccurrence);

        // Unrelated trailing phase entry (must stay untouched).
        var unrelatedTrailing = new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddSeconds(-20),
            WorkerOutput = "UNRELATED-TRAILING-EVIDENCE-TERMINAL: tester output",
        };
        pipeline.PhaseLog.Add(unrelatedTrailing);

        // Act: NONEMPTY summary-selected report (8KB+ realistic text) — the exact
        // reason-plus-report assertion on this entry is what fails under the constant-only
        // mutation; an empty-report vector here would be mutation-incapable.
        var report = BuildNoOpReport("HISTORICAL-TERMINAL");
        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-historical-terminal",
            Status = TaskOutcome.Completed,
            Output = "DIFFERENT-RAW-OUTPUT-HISTORICAL-TERMINAL: not stored when the summary wins",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = report },
        }, TestContext.Current.CancellationToken);

        // Assert: ONLY the last current-iteration occurrence got the exact reason-plus-report.
        Assert.Equal(PhaseOutcome.Fail, lastOccurrence.Result);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, lastOccurrence.WorkerOutput);
        Assert.NotNull(lastOccurrence.CompletedAt);

        // Assert: every other entry retains its original state verbatim — seeded CompletedAt
        // values proven with EXACT equality.
        Assert.Equal(PhaseOutcome.Pass, olderIterationEntry.Result);
        Assert.Equal("OLDER-ITERATION-EVIDENCE-TERMINAL: previous iteration output", olderIterationEntry.WorkerOutput);
        Assert.Equal(olderIterationCompletedAt, olderIterationEntry.CompletedAt);
        Assert.Equal(PhaseOutcome.Pass, earlierOccurrence.Result);
        Assert.Equal("FIRST-OCCURRENCE-EVIDENCE-TERMINAL: first attempt output", earlierOccurrence.WorkerOutput);
        Assert.Equal(earlierOccurrenceCompletedAt, earlierOccurrence.CompletedAt);
        Assert.Equal(PhaseOutcome.Pass, unrelatedTrailing.Result);
        Assert.Equal("UNRELATED-TRAILING-EVIDENCE-TERMINAL: tester output", unrelatedTrailing.WorkerOutput);

        // Assert: goal failed via the terminal path; the terminal summary (the only one, from
        // FinalizeGoalAsync) contains the three current-iteration entries — the earlier Coding
        // occurrence, the targeted Coding occurrence (with the exact reason-plus-report string),
        // and the unrelated Testing entry — since BuildIterationSummary filters to the current
        // iteration. The older iteration's entry is protected by the separate direct assertions
        // above, not included in this summary. (Only the summary-selected content is asserted
        // on the summary; each entry's untouched state is asserted directly on the PhaseLog
        // objects.)
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        var failedUpdate = Assert.Single(goalStore.StatusUpdates, u => u.Metadata?.IterationSummary is not null);
        Assert.Equal(GoalStatus.Failed, failedUpdate.Status);
        var summary = failedUpdate.Metadata!.IterationSummary!;
        var codingInSummary = summary.Phases
            .Where(p => p.Name == GoalPhase.Coding)
            .ToList();
        Assert.Equal(2, codingInSummary.Count);
        var summaryLastOccurrence = Assert.Single(codingInSummary, p => p.Occurrence == 2);
        Assert.Equal(PhaseOutcome.Fail, summaryLastOccurrence.Result);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, summaryLastOccurrence.WorkerOutput);
        var summaryEarlierOccurrence = Assert.Single(codingInSummary, p => p.Occurrence == 1);
        Assert.Equal(PhaseOutcome.Pass, summaryEarlierOccurrence.Result);
        Assert.Equal("FIRST-OCCURRENCE-EVIDENCE-TERMINAL: first attempt output", summaryEarlierOccurrence.WorkerOutput);
        Assert.Contains(summary.Phases, p => p.Name == GoalPhase.Testing && p.WorkerOutput == "UNRELATED-TRAILING-EVIDENCE-TERMINAL: tester output");
    }

    // ── Test 6: SQLite end-to-end — retry path ────────────────────────────

    /// <summary>
    /// Driver-completion → real lifecycle/status persistence → SQLite
    /// <see cref="GoalStore.GetIterationsAsync"/> chain for the RETRY path: the no-op iteration
    /// summary (reason + report, summary-selected form) must be persisted by the driver's
    /// pre-consume InProgress write, exactly once.
    /// </summary>
    [Fact]
    public async Task NoOpRetry_SqliteChain_SummarySelected_PersistsReasonPlusReportForRetryIteration()
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var (driver, pipeline, goalStore) = await CreateSqliteNoOpDriver(dbContext, exhaustBudget: false);

        var report = BuildNoOpReport("SQLITE-RETRY-SUMMARY");
        const string rawOutput = "DIFFERENT-RAW-OUTPUT-SQLITE-RETRY";

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-sqlite-retry",
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = report },
        }, TestContext.Current.CancellationToken);

        // Assert: retry state machine advanced to iteration 2.
        Assert.Equal(2, pipeline.Iteration);
        Assert.Equal(GoalPhase.Coding, pipeline.Phase);

        // Assert through the REAL persistence chain: GetIterationsAsync returns exactly the
        // driver's pre-consume summary (no FinalizeGoalAsync summary on the retry path).
        dbContext.ChangeTracker.Clear();
        var summaries = await goalStore.GetIterationsAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        var persisted = Assert.Single(summaries);
        Assert.Equal(1, persisted.Iteration);
        var persistedCoding = Assert.Single(persisted.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(PhaseOutcome.Fail, persistedCoding.Result);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, persistedCoding.WorkerOutput);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, persisted.PhaseOutputs["coder-1"]);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, persisted.PhaseOutputs["coder-1-1"]);

        // The goal is still InProgress (the retry continues) — read through the real store.
        var persistedGoal = await goalStore.GetGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        Assert.NotNull(persistedGoal);
        Assert.Equal(GoalStatus.InProgress, persistedGoal!.Status);
    }

    /// <summary>SQLite retry path with raw fallback — full raw Output persisted verbatim.</summary>
    [Fact]
    public async Task NoOpRetry_SqliteChain_RawFallback_PersistsReasonPlusFullRawOutputForRetryIteration()
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var (driver, pipeline, goalStore) = await CreateSqliteNoOpDriver(dbContext, exhaustBudget: false);

        var rawOutput = BuildNoOpReport("SQLITE-RETRY-RAW");

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-sqlite-retry-raw",
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = "   " },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, pipeline.Iteration);
        Assert.Equal(GoalPhase.Coding, pipeline.Phase);

        dbContext.ChangeTracker.Clear();
        var summaries = await goalStore.GetIterationsAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        var persisted = Assert.Single(summaries);
        Assert.Equal(1, persisted.Iteration);
        var persistedCoding = Assert.Single(persisted.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(PhaseOutcome.Fail, persistedCoding.Result);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + rawOutput, persistedCoding.WorkerOutput);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + rawOutput, persisted.PhaseOutputs["coder-1"]);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + rawOutput, persisted.PhaseOutputs["coder-1-1"]);

        var persistedGoal = await goalStore.GetGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        Assert.NotNull(persistedGoal);
        Assert.Equal(GoalStatus.InProgress, persistedGoal!.Status);
    }

    /// <summary>
    /// SQLite retry path with an EMPTY selected report — the reason-ALONE form through the real
    /// persistence chain: the persisted phase and BOTH compatibility/per-occurrence mappings
    /// carry exactly "Coder produced no file changes (no-op)" (no separator), with the correct
    /// iteration and Fail outcome.
    /// </summary>
    [Fact]
    public async Task NoOpRetry_SqliteChain_EmptyReport_PersistsReasonAloneForRetryIteration()
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var (driver, pipeline, goalStore) = await CreateSqliteNoOpDriver(dbContext, exhaustBudget: false);

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-noop-sqlite-retry-empty",
            Status = TaskOutcome.Completed,
            Output = "",
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
        }, TestContext.Current.CancellationToken);

        // Assert: retry state machine advanced to iteration 2.
        Assert.Equal(2, pipeline.Iteration);
        Assert.Equal(GoalPhase.Coding, pipeline.Phase);

        // Assert through the REAL persistence chain: the reason-alone string is stored in the
        // phase entry AND in BOTH output mappings, with the correct iteration and Fail outcome.
        dbContext.ChangeTracker.Clear();
        var summaries = await goalStore.GetIterationsAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        var persisted = Assert.Single(summaries);
        Assert.Equal(1, persisted.Iteration);
        var persistedCoding = Assert.Single(persisted.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(PhaseOutcome.Fail, persistedCoding.Result);
        Assert.Equal("Coder produced no file changes (no-op)", persistedCoding.WorkerOutput);
        Assert.Equal("Coder produced no file changes (no-op)", persisted.PhaseOutputs["coder-1"]);
        Assert.Equal("Coder produced no file changes (no-op)", persisted.PhaseOutputs["coder-1-1"]);

        // The goal is still InProgress (the retry continues) — read through the real store.
        var persistedGoal = await goalStore.GetGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        Assert.NotNull(persistedGoal);
        Assert.Equal(GoalStatus.InProgress, persistedGoal!.Status);
    }

    // ── Test 7: SQLite end-to-end — budget-exhausted terminal path ─────────

    /// <summary>
    /// Budget-exhausted SQLite chain: the terminal summary comes from
    /// <see cref="GoalLifecycleService.MarkGoalFailedAsync"/> (through a real
    /// <see cref="GoalDispatcher"/>) — assert the full stored reason-plus-report string there.
    /// </summary>
    [Fact]
    public async Task NoOpRetry_SqliteChain_Exhausted_SummarySelected_PersistsReasonPlusReportOnTerminalFailure()
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var (dispatcher, pipeline, goalStore, _, capturingLogger) = await CreateSqliteNoOpDispatcher(dbContext, exhaustBudget: true);

        var report = BuildNoOpReport("SQLITE-TERMINAL-SUMMARY");
        const string rawOutput = "DIFFERENT-RAW-OUTPUT-SQLITE-TERMINAL";
        var taskId = pipeline.ActiveTaskId
            ?? throw new InvalidOperationException("the seeded task must be the pipeline's active task");

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = report },
        }, TestContext.Current.CancellationToken);

        // Assert: the drive/finalize path ran without an exception (a swallowed drive error
        // would leave the goal InProgress and would make the Failed assertions misleading).
        Assert.Null(capturingLogger.LastException);

        // Assert: terminalized as Failed by the existing lifecycle path.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        var persistedGoal = await goalStore.GetGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        Assert.NotNull(persistedGoal);
        Assert.Equal(GoalStatus.Failed, persistedGoal!.Status);
        Assert.Equal(
            "Coder produced no file changes after max iterations (no-op)",
            persistedGoal.FailureReason);

        // Assert through the REAL persistence chain: the terminal summary from FinalizeGoalAsync
        // carries the EXACT reason-plus-report string, at the terminal iteration, in BOTH the
        // compatibility and per-occurrence output mappings.
        dbContext.ChangeTracker.Clear();
        var summaries = await goalStore.GetIterationsAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        var persisted = Assert.Single(summaries);
        Assert.Equal(5, persisted.Iteration);
        var persistedCoding = Assert.Single(persisted.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(PhaseOutcome.Fail, persistedCoding.Result);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, persistedCoding.WorkerOutput);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, persisted.PhaseOutputs["coder-5"]);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + report, persisted.PhaseOutputs["coder-5-1"]);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    /// <summary>Raw fallback on the budget-exhausted SQLite path — full raw Output persisted.</summary>
    [Fact]
    public async Task NoOpRetry_SqliteChain_Exhausted_RawFallback_PersistsReasonPlusFullRawOutputOnTerminalFailure()
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var (dispatcher, pipeline, goalStore, _, capturingLogger) = await CreateSqliteNoOpDispatcher(dbContext, exhaustBudget: true);

        var rawOutput = BuildNoOpReport("SQLITE-TERMINAL-RAW");
        var taskId = pipeline.ActiveTaskId
            ?? throw new InvalidOperationException("the seeded task must be the pipeline's active task");

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = rawOutput,
            GitStatus = new GitChangeSummary { FilesChanged = 0 },
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = "" },
        }, TestContext.Current.CancellationToken);

        Assert.Null(capturingLogger.LastException);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        var persistedGoal = await goalStore.GetGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        Assert.NotNull(persistedGoal);
        Assert.Equal(GoalStatus.Failed, persistedGoal!.Status);

        dbContext.ChangeTracker.Clear();
        var summaries = await goalStore.GetIterationsAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        var persisted = Assert.Single(summaries);
        Assert.Equal(5, persisted.Iteration);
        var persistedCoding = Assert.Single(persisted.Phases, p => p.Name == GoalPhase.Coding);
        Assert.Equal(PhaseOutcome.Fail, persistedCoding.Result);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + rawOutput, persistedCoding.WorkerOutput);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + rawOutput, persisted.PhaseOutputs["coder-5"]);
        Assert.Equal("Coder produced no file changes (no-op)\n\n" + rawOutput, persisted.PhaseOutputs["coder-5-1"]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a realistic 8KB+ multiline no-op report with distinct evidence markers past
    /// char 500, past char 4,000, and at the very tail.
    /// </summary>
    private static string BuildNoOpReport(string markerPrefix)
    {
        const string head = "NO-OP REPORT HEAD: analysis only, no file edits.\n";
        var beyond500 = $"{markerPrefix}-BEYOND-500: a legacy 500-char preview would lose this marker.\n";
        var beyond4000 = $"{markerPrefix}-BEYOND-4000: a legacy 4,000-char cap would lose this marker.\n";
        var tail = $"{markerPrefix}-TAIL-EVIDENCE-AT-END: final recommendation text.";
        return head
            + new string('a', 500 - head.Length) + beyond500
            + new string('b', 4_000 - 500 - beyond500.Length) + beyond4000
            + new string('c', 5_500) + tail;
    }

    /// <summary>
    /// Creates a no-op driver wired to a REAL SQLite <see cref="GoalStore"/>: the goal is
    /// created in the store, registered as a source (mirroring GoalDispatchService's
    /// GetNextGoalAsync + InProgress write), and the pipeline is positioned in Coding.
    /// When <paramref name="exhaustBudget"/> is set, the iteration budget is consumed so the
    /// no-op path takes the terminal branch.
    /// </summary>
    private static async Task<(PipelineDriver Driver, GoalPipeline Pipeline, GoalStore Store)>
        CreateSqliteNoOpDriver(CopilotHiveDbContext dbContext, bool exhaustBudget)
    {
        var goalStore = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var goal = new Goal
        {
            Id = $"goal-noop-sqlite-{Guid.NewGuid():N}",
            Description = "No-op report retention SQLite test",
            RepositoryNames = ["CopilotHive"],
        };
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);
        // Register the goal in the source map first (Pending, as GoalDispatchService would see
        // it), then mirror GoalDispatchService's InProgress write so the later status
        // transitions apply to the stored row.
        Assert.Equal(goal.Id, (await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken))?.Id);
        await goalManager.UpdateGoalStatusAsync(goal.Id, GoalStatus.InProgress,
            new GoalUpdateMetadata { StartedAt = DateTime.UtcNow }, TestContext.Current.CancellationToken);

        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        // Mirror the production restore path (RestoreActivePipelinesAsync): the state machine
        // must be positioned in Coding BEFORE AdvanceTo, otherwise the completion is dropped
        // by the planning-window guard (StateMachine.Phase still Planning).
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);
        if (exhaustBudget)
        {
            for (var i = 0; i < 4; i++)
                pipeline.IterationBudget.TryConsume();
            Assert.True(pipeline.IterationBudget.IsExhausted);
        }

        // The phase entry the no-op completion must land on (the in-memory CreateNoOpDriver
        // tests seed this themselves; seeding here keeps both SQLite helpers self-contained).
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1));

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);

        var driver = new PipelineDriver(
            brain: new NoOpRetryFakeBrain(),
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => Task.CompletedTask,
            resolvePrompt: (_, _, _, _) => Task.FromResult("retry with stronger prompt"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return (driver, pipeline, goalStore);
    }

    /// <summary>
    /// Creates a REAL <see cref="GoalDispatcher"/> over a SQLite <see cref="GoalStore"/> with
    /// the pipeline seeded for the budget-exhausted no-op path: the completion flows through
    /// TaskCompletionService → PipelineDriver → GoalLifecycleService.MarkGoalFailedAsync,
    /// and the terminal summary is persisted by the real store.
    /// </summary>
    private static async Task<(GoalDispatcher Dispatcher, GoalPipeline Pipeline, GoalStore Store, GoalPipelineManager PipelineManager, PipelineDriverCapturingLogger<GoalDispatcher> Logger)>
        CreateSqliteNoOpDispatcher(CopilotHiveDbContext dbContext, bool exhaustBudget)
    {
        var goalStore = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var goal = new Goal
        {
            Id = $"goal-noop-sqlite-dispatcher-{Guid.NewGuid():N}",
            Description = "No-op report retention SQLite dispatcher test",
            RepositoryNames = ["CopilotHive"],
        };
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);
        Assert.Equal(goal.Id, (await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken))?.Id);
        await goalManager.UpdateGoalStatusAsync(goal.Id, GoalStatus.InProgress,
            new GoalUpdateMetadata { StartedAt = DateTime.UtcNow }, TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        // Mirror the production restore path (see CreateSqliteNoOpDriver): the state machine
        // must be positioned in Coding BEFORE AdvanceTo.
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);
        if (exhaustBudget)
        {
            for (var i = 0; i < 4; i++)
                pipeline.IterationBudget.TryConsume();
            Assert.True(pipeline.IterationBudget.IsExhausted);
        }

        // The phase entry the no-op completion must land on.
        var codingEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(codingEntry);

        var taskId = $"task-noop-sqlite-terminal-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);
        Assert.True(pipeline.SeedSlotForTest(
            taskId,
            new WorkSlotPosition(pipeline.Iteration, GoalPhase.Coding, 1),
            attempt: 1,
            WorkSlotState.Pending));

        var capturingLogger = new PipelineDriverCapturingLogger<GoalDispatcher>();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            capturingLogger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: new NoOpRetryFakeBrain(),
            config: BuildNoOpDispatcherConfig());

        return (dispatcher, pipeline, goalStore, pipelineManager, capturingLogger);
    }

    /// <summary>Dispatcher config for the SQLite no-op dispatcher tests (mirrors the failed-worker pattern).</summary>
    private static HiveConfigFile BuildNoOpDispatcherConfig() => new()
    {
        Repositories =
        [
            new RepositoryConfig
            {
                Name = "CopilotHive",
                Url = "https://example.invalid/CopilotHive.git",
                DefaultBranch = "main",
            },
        ],
        Workers =
        {
            ["coder"] = new WorkerConfig { Model = "test-coder-model" },
            ["tester"] = new WorkerConfig { Model = "test-tester-model" },
            ["reviewer"] = new WorkerConfig { Model = "test-reviewer-model" },
            ["docwriter"] = new WorkerConfig { Model = "test-docwriter-model" },
            ["improver"] = new WorkerConfig { Model = "test-improver-model" },
        },
    };

    private static (PipelineDriver Driver, GoalPipeline Pipeline, IterationCapturingGoalStore Store) CreateNoOpDriver(
        Func<GoalPipeline, WorkerRole, string?, CancellationToken, Task>? dispatchToRole = null)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "No-op retry persistence test" };
        var goalStore = new IterationCapturingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.SetPlan(IterationPlan.Default());
        goalStore.Pipeline = pipeline;

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);

        var driver = new PipelineDriver(
            brain: new NoOpRetryFakeBrain(),
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: dispatchToRole ?? ((_, _, _, _) => Task.CompletedTask),
            resolvePrompt: (_, _, _, _) => Task.FromResult("retry with stronger prompt"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return (driver, pipeline, goalStore);
    }

    /// <summary>
    /// In-memory <see cref="IGoalStore"/> that records every status update together with the
    /// pipeline's iteration counter sampled AT THE MOMENT of the call. This lets tests prove
    /// whether an update happened before or after <see cref="GoalPipeline.IterationBudget"/>
    /// was consumed: if the persist call were moved after <c>TryConsume()</c>, the captured
    /// iteration would be the new (post-consume) number and the assertion would fail.
    /// </summary>
    private sealed class IterationCapturingGoalStore(Goal goal) : IGoalStore
    {
        /// <summary>Pipeline whose <see cref="GoalPipeline.Iteration"/> is sampled on each update.</summary>
        internal GoalPipeline? Pipeline { get; set; }

        /// <summary>All status updates in the order they were applied, with the iteration at call time.</summary>
        internal List<(GoalStatus Status, GoalUpdateMetadata? Metadata, int IterationAtUpdate)> StatusUpdates { get; } = [];

        public string Name => "iteration-capturing-store";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(goal.Status == GoalStatus.Pending ? [goal] : []);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            lock (StatusUpdates)
            {
                StatusUpdates.Add((status, metadata, Pipeline?.Iteration ?? 0));
            }

            goal.Status = status;
            if (metadata?.FailureReason is not null)
                goal.FailureReason = metadata.FailureReason;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult(goalId == goal.Id ? goal : null);

        public Task<Goal> CreateGoalAsync(Goal goalToCreate, CancellationToken ct = default) => Task.FromResult(goalToCreate);

        public Task UpdateGoalAsync(Goal goalToUpdate, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
            string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IterationSummary>>([]);

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
            Task.FromResult(release);

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<Release?>(null);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Release>>([]);

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
            string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
            int? limit = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
    }

    /// <summary>Minimal brain stub that returns the default plan.</summary>
    private sealed class NoOpRetryFakeBrain : IDistributedBrain
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

/// <summary>
/// Tests that <see cref="PipelineDriver.HandleMergeFailureAsync"/> marks the Merging phase as
/// failed and persists the failed-merge iteration summary so it is visible in the dashboard
/// iteration tab bar (mirrors the <see cref="PipelineDriver.HandleNewIterationAsync"/> pattern).
/// </summary>
public sealed class PipelineDriverMergeFailurePersistenceTests
{
    // ── Test 1: merge failure after successful review persists iteration summary ──

    [Fact]
    public async Task HandleMergeFailureAsync_AfterSuccessfulReview_PersistsIterationSummaryWithMergingFail()
    {
        // Arrange: pipeline with Coding pass, Testing pass, Review pass, and a Merging PhaseResult.
        var (driver, pipeline, goalStore) = CreateDriver();

        AddPhaseEntry(pipeline, GoalPhase.Coding, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Testing, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Review, PhaseOutcome.Pass);
        var mergingEntry = PhaseResult.Create(GoalPhase.Merging, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(mergingEntry);

        const string mergeError = "conflict in Program.cs";

        // Act
        await driver.HandleMergeFailureAsync(pipeline, mergeError, TestContext.Current.CancellationToken);

        // Assert: the Merging PhaseResult was marked failed with the error.
        Assert.Equal(PhaseOutcome.Fail, mergingEntry.Result);
        Assert.Equal(mergeError, mergingEntry.WorkerOutput);
        Assert.NotNull(mergingEntry.CompletedAt);

        // Assert: UpdateGoalStatusAsync was called with an IterationSummary containing
        // Coding/Testing/Review pass and Merging fail with the error.
        var summaryUpdate = goalStore.StatusUpdates
            .Select(u => u.Metadata?.IterationSummary)
            .FirstOrDefault(s => s is not null);
        Assert.NotNull(summaryUpdate);
        Assert.Equal(1, summaryUpdate!.Iteration);

        Assert.Contains(summaryUpdate.Phases,
            p => p.Name == GoalPhase.Coding && p.Result == PhaseOutcome.Pass);
        Assert.Contains(summaryUpdate.Phases,
            p => p.Name == GoalPhase.Testing && p.Result == PhaseOutcome.Pass);
        Assert.Contains(summaryUpdate.Phases,
            p => p.Name == GoalPhase.Review && p.Result == PhaseOutcome.Pass);
        var mergingInSummary = Assert.Single(summaryUpdate.Phases, p => p.Name == GoalPhase.Merging);
        Assert.Equal(PhaseOutcome.Fail, mergingInSummary.Result);
        Assert.Contains(mergeError, mergingInSummary.WorkerOutput);

        // Assert: the summary is also in CompletedIterationSummaries and a retry iteration started.
        var completedSummary = Assert.Single(pipeline.CompletedIterationSummaries, s => s.Iteration == 1);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(completedSummary.Phases, p => p.Name == GoalPhase.Merging).Result);
        Assert.Equal(2, pipeline.Iteration);
    }

    // ── Test 2: timeline shows both the failed-merge iteration and the retry iteration ──

    [Fact]
    public async Task HandleMergeFailureAsync_RetryDispatch_TimelineShowsBothIterations()
    {
        // Arrange
        var (driver, pipeline, _) = CreateDriver();

        AddPhaseEntry(pipeline, GoalPhase.Coding, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Testing, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Review, PhaseOutcome.Pass);
        var mergingEntry = PhaseResult.Create(GoalPhase.Merging, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(mergingEntry);

        const string mergeError = "conflict in Program.cs";

        // Act
        await driver.HandleMergeFailureAsync(pipeline, mergeError, TestContext.Current.CancellationToken);

        // Assert: CompletedIterationSummaries contains the failed-merge iteration (Merging = fail).
        var failedMergeSummary = Assert.Single(pipeline.CompletedIterationSummaries, s => s.Iteration == 1);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(failedMergeSummary.Phases, p => p.Name == GoalPhase.Merging).Result);

        // Assert: a new iteration has started.
        Assert.Equal(2, pipeline.Iteration);
        Assert.Equal(IterationPlan.Default().Phases[0], pipeline.Phase);

        // Assert: the timeline shows both the failed-merge iteration and the current retry iteration.
        var iterations = GoalDetailViewBuilder.BuildIterationTimeline(pipeline.Goal, pipeline.GoalId, pipeline);
        Assert.Equal(2, iterations.Count);

        var failedIteration = Assert.Single(iterations, i => i.Number == 1);
        var mergingPhaseView = Assert.Single(failedIteration.Phases, p => p.Name == "Merging");
        Assert.Equal("failed", mergingPhaseView.Status);
        Assert.Contains(mergeError, mergingPhaseView.WorkerOutput);

        var currentIteration = Assert.Single(iterations, i => i.Number == 2);
        Assert.True(currentIteration.IsCurrent);
    }

    // ── Test 3: budget-exhausted path marks Merging failed but creates no duplicate ──

    [Fact]
    public async Task HandleMergeFailureAsync_ReviewBudgetExhausted_MarksMergingFailedWithoutDuplicateSummary()
    {
        // Arrange: exhaust the review retry budget (maxRetries = 3) so HandleMergeFailureAsync
        // takes the terminal path and lets FinalizeGoalAsync build/persist the summary.
        var (driver, pipeline, goalStore) = CreateDriver();
        for (var i = 0; i < 3; i++)
            pipeline.ReviewRetryBudget.TryConsume();

        AddPhaseEntry(pipeline, GoalPhase.Coding, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Testing, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Review, PhaseOutcome.Pass);
        var mergingEntry = PhaseResult.Create(GoalPhase.Merging, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(mergingEntry);

        const string mergeError = "conflict in Program.cs";

        // Act
        await driver.HandleMergeFailureAsync(pipeline, mergeError, TestContext.Current.CancellationToken);

        // Assert: the Merging PhaseResult was marked failed with the error BEFORE terminal exit,
        // so FinalizeGoalAsync's summary includes the failed Merging phase.
        Assert.Equal(PhaseOutcome.Fail, mergingEntry.Result);
        Assert.Equal(mergeError, mergingEntry.WorkerOutput);
        Assert.NotNull(mergingEntry.CompletedAt);

        // Assert: goal failed via the terminal path.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);

        // Assert: exactly ONE summary (the terminal one from FinalizeGoalAsync) — no duplicate
        // pre-retry snapshot was added by HandleMergeFailureAsync.
        var summary = Assert.Single(pipeline.CompletedIterationSummaries);
        Assert.Equal(1, summary.Iteration);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(summary.Phases, p => p.Name == GoalPhase.Merging).Result);

        // Assert: exactly ONE status update carried an IterationSummary (the Failed one from
        // FinalizeGoalAsync) — HandleMergeFailureAsync did NOT create an InProgress one.
        var summaryUpdates = goalStore.StatusUpdates
            .Where(u => u.Metadata?.IterationSummary is not null)
            .ToList();
        var failedUpdate = Assert.Single(summaryUpdates);
        Assert.Equal(GoalStatus.Failed, failedUpdate.Status);
    }

    // ── Test 4: iteration-budget-exhausted path marks Merging failed but creates no duplicate ──

    [Fact]
    public async Task HandleMergeFailureAsync_IterationBudgetExhausted_MarksMergingFailedWithoutDuplicateSummary()
    {
        // Arrange: exhaust the iteration budget (maxIterations = 5 → IterationBudget allows 4)
        // while the review retry budget still has room, so HandleMergeFailureAsync passes the
        // review-budget check but takes the iteration-budget terminal path.
        var (driver, pipeline, goalStore) = CreateDriver();
        for (var i = 0; i < 4; i++)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);
        Assert.False(pipeline.ReviewRetryBudget.IsExhausted);

        AddPhaseEntry(pipeline, GoalPhase.Coding, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Testing, PhaseOutcome.Pass);
        AddPhaseEntry(pipeline, GoalPhase.Review, PhaseOutcome.Pass);
        var mergingEntry = PhaseResult.Create(GoalPhase.Merging, pipeline.Iteration, 1);
        pipeline.PhaseLog.Add(mergingEntry);

        var summariesBefore = pipeline.CompletedIterationSummaries.Count;
        var iterationBefore = pipeline.Iteration; // 5 after exhausting the iteration budget

        const string mergeError = "conflict in Program.cs";

        // Act
        await driver.HandleMergeFailureAsync(pipeline, mergeError, TestContext.Current.CancellationToken);

        // Assert: the Merging PhaseResult was marked failed with the error BEFORE terminal exit,
        // so FinalizeGoalAsync's summary includes the failed Merging phase.
        Assert.Equal(PhaseOutcome.Fail, mergingEntry.Result);
        Assert.Equal(mergeError, mergingEntry.WorkerOutput);
        Assert.NotNull(mergingEntry.CompletedAt);

        // Assert: goal failed via the terminal path.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);

        // Assert: HandleMergeFailureAsync did NOT add a pre-retry snapshot to
        // CompletedIterationSummaries — the only addition is the terminal summary from
        // FinalizeGoalAsync (exactly one, not two).
        Assert.Equal(summariesBefore, pipeline.CompletedIterationSummaries.Count - 1);
        var summary = Assert.Single(pipeline.CompletedIterationSummaries);
        Assert.Equal(iterationBefore, summary.Iteration);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(summary.Phases, p => p.Name == GoalPhase.Merging).Result);

        // Assert: exactly ONE status update carried an IterationSummary (the Failed one from
        // FinalizeGoalAsync) — HandleMergeFailureAsync did NOT create an InProgress one.
        var summaryUpdates = goalStore.StatusUpdates
            .Where(u => u.Metadata?.IterationSummary is not null)
            .ToList();
        var failedUpdate = Assert.Single(summaryUpdates);
        Assert.Equal(GoalStatus.Failed, failedUpdate.Status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (PipelineDriver Driver, GoalPipeline Pipeline, PlanRejectRecordingGoalStore Store) CreateDriver()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Merge failure persistence test" };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        pipeline.AdvanceTo(GoalPhase.Merging);
        pipeline.SetPlan(IterationPlan.Default());

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);

        var driver = new PipelineDriver(
            brain: new MergeFailureFakeBrain(),
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => Task.CompletedTask,
            resolvePrompt: (_, _, _, _) => Task.FromResult("rebase and fix the conflict"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return (driver, pipeline, goalStore);
    }

    /// <summary>Adds a completed <see cref="PhaseResult"/> with the given outcome to the PhaseLog.</summary>
    private static void AddPhaseEntry(GoalPipeline pipeline, GoalPhase phase, PhaseOutcome outcome)
    {
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = phase,
            Result = outcome,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            WorkerOutput = $"{phase} output",
        });
    }

    /// <summary>Minimal brain stub that returns the default plan.</summary>
    private sealed class MergeFailureFakeBrain : IDistributedBrain
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

/// <summary>
/// Tests that <see cref="PipelineDriver.DriveNextPhaseAsync"/>'s failed-worker early-exit
/// branch preserves the FULL raw crash diagnostic on the matching phase entry, marks the
/// execution failed (<see cref="PhaseOutcome.Fail"/> + <c>CompletedAt</c>), and keeps the
/// existing terminal-failure behavior (single <c>"Worker failed: ..."</c> summary update,
/// no retry/dispatch) — before <see cref="GoalLifecycleService.MarkGoalFailedAsync"/>
/// finalizes the iteration.
/// </summary>
public sealed class PipelineDriverFailedWorkerTests
{
    // ── Test 1: end-to-end raw-diagnostic retention through the SQLite chain ──

    /// <summary>
    /// Real admitted-completion → lifecycle finalization → GoalStore.GetIterationsAsync chain:
    /// the multiline diagnostic (evidence beyond chars 300 AND 4,000, distinguishing content at
    /// the very end) must survive EXACTLY in the persisted phase report, with a different
    /// structured Metrics.Summary proving raw diagnostics win on the failed path.
    /// </summary>
    [Fact]
    public async Task DriveNextPhaseAsync_WhenWorkerFails_PersistsFullRawDiagnosticToGoalStore()
    {
        // Arrange: real SQLite-backed goal store with the goal registered as a source.
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var goal = new Goal
        {
            Id = $"goal-failed-worker-{Guid.NewGuid():N}",
            Description = "Retain failed-worker diagnostic evidence",
            RepositoryNames = ["CopilotHive"],
        };
        await goalStore.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);
        // Register the goal in the source map first (Pending, as GoalDispatchService would see
        // it), then mirror GoalDispatchService's InProgress write so the later terminal Failed
        // update transitions the stored status correctly.
        Assert.Equal(goal.Id, (await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken))?.Id);
        await goalManager.UpdateGoalStatusAsync(goal.Id, GoalStatus.InProgress,
            new GoalUpdateMetadata { StartedAt = DateTime.UtcNow }, TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Testing);
        pipeline.AdvanceTo(GoalPhase.Testing);

        // The phase entry the failed completion must land on (StartedAt must be preserved).
        var startedAt = DateTime.UtcNow.AddMinutes(-5);
        var testingEntry = new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = startedAt,
        };
        pipeline.PhaseLog.Add(testingEntry);

        var taskId = $"task-failed-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);
        Assert.True(pipeline.SeedSlotForTest(
            taskId,
            new WorkSlotPosition(pipeline.Iteration, GoalPhase.Testing, 1),
            attempt: 1,
            WorkSlotState.Pending));

        var logger = new PipelineDriverCapturingLogger<GoalDispatcher>();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: new FailedWorkerDispatcherFakeBrain(),
            config: BuildFailedWorkerDispatcherConfig());

        // Multiline diagnostic with distinctive content past chars 300 AND 4,000 and at the very
        // end — plus a DIFFERENT structured summary proving raw diagnostics win on this path.
        const string crashHead = "HEAD: unhandled exception in worker\r\n";
        const string crashBeyond4000 = "\nSTACK-EVIDENCE-BEYOND-4000: mutation detail survives only if output is stored verbatim.\n";
        const string crashTail = "\nTAIL-EVIDENCE-AT-END: exit code 137 (OOMKilled)";
        var diagnostic = crashHead + new string('X', 4_500 - crashHead.Length) + crashBeyond4000
            + new string('Y', 4_500) + crashTail;
        Assert.True(diagnostic.Length > 300);
        Assert.True(diagnostic.Length > 4_000);
        Assert.Contains("STACK-EVIDENCE-BEYOND-4000", diagnostic);
        Assert.EndsWith(crashTail, diagnostic);
        const string structuredSummary = "STRUCTURED-SUMMARY-THAT-MUST-NOT-WIN: reviewer said all fine.";

        // Act
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Failed,
            Output = diagnostic,
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = structuredSummary },
        }, TestContext.Current.CancellationToken);

        // Assert: goal terminalized as Failed by the existing lifecycle path. The lifecycle
        // persists through the store (which the dispatcher's goal instance mirrors lazily), so
        // assert on the re-read DB state.
        var persistedGoal = await goalStore.GetGoalAsync(goal.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(persistedGoal);
        Assert.Equal(GoalStatus.Failed, persistedGoal!.Status);
        Assert.StartsWith("Worker failed: ", persistedGoal.FailureReason);

        // Assert: phase entry got the EXACT raw diagnostic (not the summary, not truncated).
        Assert.Equal(PhaseOutcome.Fail, testingEntry.Result);
        Assert.Equal(diagnostic, testingEntry.WorkerOutput);
        Assert.NotEqual(structuredSummary, testingEntry.WorkerOutput);
        Assert.Equal(startedAt, testingEntry.StartedAt);
        Assert.Equal(GoalPhase.Testing, testingEntry.Name);
        Assert.Equal(1, testingEntry.Iteration);
        Assert.Equal(1, testingEntry.Occurrence);
        Assert.NotNull(testingEntry.CompletedAt);

        // Assert: the failed reason is exactly "Worker failed: " + the 300-char preview.
        var expectedPreview = diagnostic.Length > 300 ? diagnostic[..300] + "..." : diagnostic;
        Assert.Equal($"Worker failed: {expectedPreview}", persistedGoal.FailureReason);

        // Assert through the real persistence chain: GetIterationsAsync returns the terminal
        // summary from FinalizeGoalAsync with the phase report preserved EXACTLY.
        dbContext.ChangeTracker.Clear();
        var summaries = await goalStore.GetIterationsAsync(goal.Id, TestContext.Current.CancellationToken);
        var persisted = Assert.Single(summaries);
        Assert.Equal(1, persisted.Iteration);
        var persistedPhase = Assert.Single(persisted.Phases, p => p.Name == GoalPhase.Testing);
        Assert.Equal(PhaseOutcome.Fail, persistedPhase.Result);
        Assert.Equal(diagnostic, persistedPhase.WorkerOutput); // EXACT raw-output equality
        Assert.Equal(diagnostic, persisted.PhaseOutputs["tester-1"]);
        Assert.Equal(diagnostic, persisted.PhaseOutputs["tester-1-1"]);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    // ── Test 2: selection boundaries ─────────────────────────────────────

    [Fact]
    public async Task FailedWorker_LastMatchingOccurrenceWins_EarlierOccurrenceAndOtherIterationsUntouched()
    {
        var (driver, pipeline, _, goal) = CreateFailedWorkerDriver(GoalPhase.Coding);

        // Older iteration's entry (must stay untouched).
        var olderIterationEntry = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = 0,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow.AddMinutes(-9),
            WorkerOutput = "older iteration output",
        };
        pipeline.PhaseLog.Add(olderIterationEntry);

        // First occurrence of the current iteration (must stay untouched).
        var firstOccurrence = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            WorkerOutput = "first occurrence output",
        };
        pipeline.PhaseLog.Add(firstOccurrence);

        // LAST occurrence of the current iteration (the target).
        var lastOccurrence = new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 2,
            StartedAt = DateTime.UtcNow.AddSeconds(-30),
        };
        pipeline.PhaseLog.Add(lastOccurrence);

        // Unrelated trailing phase entry (must stay untouched).
        var unrelatedTrailing = new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow.AddSeconds(-20),
            WorkerOutput = "unrelated trailing output",
        };
        pipeline.PhaseLog.Add(unrelatedTrailing);

        const string diagnostic = "crash in second coding attempt";

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-failed-occ",
            Status = TaskOutcome.Failed,
            Output = diagnostic,
        }, TestContext.Current.CancellationToken);

        // The LAST matching occurrence was mutated.
        Assert.Equal(PhaseOutcome.Fail, lastOccurrence.Result);
        Assert.Equal(diagnostic, lastOccurrence.WorkerOutput);
        Assert.NotNull(lastOccurrence.CompletedAt);

        // Older iteration, earlier occurrence, and unrelated trailing entry untouched.
        Assert.Equal(PhaseOutcome.Pass, olderIterationEntry.Result);
        Assert.Equal("older iteration output", olderIterationEntry.WorkerOutput);
        Assert.Equal(PhaseOutcome.Pass, firstOccurrence.Result);
        Assert.Equal("first occurrence output", firstOccurrence.WorkerOutput);
        Assert.NotNull(firstOccurrence.CompletedAt);
        Assert.Equal(PhaseOutcome.Pass, unrelatedTrailing.Result);
        Assert.Equal("unrelated trailing output", unrelatedTrailing.WorkerOutput);

        Assert.Equal(GoalStatus.Failed, goal.Status);
    }

    [Fact]
    public async Task FailedWorker_EmptyDiagnostic_StoresEmptyStringAndFails()
    {
        var (driver, pipeline, _, goal) = CreateFailedWorkerDriver(GoalPhase.Review);
        var reviewEntry = new PhaseResult
        {
            Name = GoalPhase.Review,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
        };
        pipeline.PhaseLog.Add(reviewEntry);

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-failed-empty",
            Status = TaskOutcome.Failed,
            Output = "",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Fail, reviewEntry.Result);
        Assert.Equal("", reviewEntry.WorkerOutput);
        Assert.NotNull(reviewEntry.CompletedAt);
        Assert.Equal(GoalStatus.Failed, goal.Status);
    }

    [Fact]
    public async Task FailedWorker_NoMatchingEntry_PreservesTerminalBehaviorWithoutSyntheticEntry()
    {
        var (driver, pipeline, _, goal) = CreateFailedWorkerDriver(GoalPhase.Coding);

        // Only an unrelated-phase entry exists — no Coding entry for the current iteration.
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
        });
        var phaseLogCountBefore = pipeline.PhaseLog.Count;

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-failed-nomatch",
            Status = TaskOutcome.Failed,
            Output = "crash with no matching entry",
        }, TestContext.Current.CancellationToken);

        // NO synthetic phase entry was created — count unchanged.
        Assert.Equal(phaseLogCountBefore, pipeline.PhaseLog.Count);

        // Existing terminal-failure behavior preserved exactly.
        Assert.Equal(GoalStatus.Failed, goal.Status);
        Assert.Equal("Worker failed: crash with no matching entry", goal.FailureReason);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    // ── Test 3: branch-preservation proof (one terminal update, no retry/dispatch) ──

    [Fact]
    public async Task FailedWorker_ExactlyOneTerminalStatusUpdate_NoRetryOrDispatch()
    {
        var dispatchCalls = 0;
        var (driver, pipeline, goalStore, goal) = CreateFailedWorkerDriver(
            GoalPhase.Testing,
            dispatchToRole: (_, _, _, _) =>
            {
                dispatchCalls++;
                return Task.CompletedTask;
            });
        var testingEntry = new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
        };
        pipeline.PhaseLog.Add(testingEntry);

        var diagnostic = "fatal: " + new string('Z', 400);

        await driver.DriveNextPhaseAsync(pipeline, new TaskResult
        {
            TaskId = "task-failed-branch",
            Status = TaskOutcome.Failed,
            Output = diagnostic,
            Metrics = new TaskMetrics { Verdict = "PASS", Summary = "misleading summary" },
        }, TestContext.Current.CancellationToken);

        // Exactly ONE status update — the terminal Failed one. An upserting goal store could
        // hide duplicate calls, so the recording store counts them.
        var update = Assert.Single(goalStore.StatusUpdates);
        Assert.Equal(GoalStatus.Failed, update.Status);

        // The failure reason still uses the "Worker failed: " + 300-char preview format.
        var expectedFailureReason = $"Worker failed: {diagnostic[..300]}...";
        Assert.Equal(expectedFailureReason, update.Metadata?.FailureReason);

        // No retry/dispatch happened.
        Assert.Equal(0, dispatchCalls);

        // No new phase entry, no new iteration, no retry consumption.
        Assert.Single(pipeline.PhaseLog);
        Assert.Equal(1, pipeline.Iteration);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);

        // Only the terminal summary from FinalizeGoalAsync exists — the driver added none.
        var summary = Assert.Single(pipeline.CompletedIterationSummaries);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(summary.Phases, p => p.Name == GoalPhase.Testing).Result);
        Assert.Equal(diagnostic, Assert.Single(summary.Phases, p => p.Name == GoalPhase.Testing).WorkerOutput);

        Assert.Equal(PhaseOutcome.Fail, testingEntry.Result);
        Assert.Equal(diagnostic, testingEntry.WorkerOutput);
        Assert.NotNull(testingEntry.CompletedAt);
        Assert.Equal(GoalStatus.Failed, goal.Status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static HiveConfigFile BuildFailedWorkerDispatcherConfig() => new()
    {
        Repositories =
        [
            new RepositoryConfig
            {
                Name = "CopilotHive",
                Url = "https://example.invalid/CopilotHive.git",
                DefaultBranch = "main",
            },
        ],
        Workers =
        {
            ["coder"] = new WorkerConfig { Model = "test-coder-model" },
            ["tester"] = new WorkerConfig { Model = "test-tester-model" },
            ["reviewer"] = new WorkerConfig { Model = "test-reviewer-model" },
            ["docwriter"] = new WorkerConfig { Model = "test-docwriter-model" },
            ["improver"] = new WorkerConfig { Model = "test-improver-model" },
        },
    };

    private static (PipelineDriver Driver, GoalPipeline Pipeline, CountingGoalStore Store, Goal Goal)
        CreateFailedWorkerDriver(
            GoalPhase phase,
            Func<GoalPipeline, WorkerRole, string?, CancellationToken, Task>? dispatchToRole = null)
    {
        var goal = new Goal
        {
            Id = $"goal-failed-{Guid.NewGuid():N}",
            Description = "Failed-worker evidence-retention test",
            RepositoryNames = ["CopilotHive"],
        };
        var goalStore = new CountingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        pipeline.SetPlan(IterationPlan.Default());
        pipeline.AdvanceTo(phase);

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);

        var driver = new PipelineDriver(
            brain: new FailedWorkerFakeBrain(),
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: dispatchToRole ?? ((_, _, _, _) => Task.CompletedTask),
            resolvePrompt: (_, _, _, _) => Task.FromResult("prompt"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return (driver, pipeline, goalStore, goal);
    }

    /// <summary>
    /// In-memory <see cref="IGoalStore"/> that records every status update so tests can prove
    /// EXACTLY ONE terminal update happened (a single row alone is insufficient because an
    /// upsert would hide duplicate calls).
    /// </summary>
    private sealed class CountingGoalStore(Goal goal) : IGoalStore
    {
        internal List<(GoalStatus Status, GoalUpdateMetadata? Metadata)> StatusUpdates { get; } = [];

        public string Name => "failed-worker-counting-store";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(goal.Status == GoalStatus.Pending ? [goal] : []);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            StatusUpdates.Add((status, metadata));
            goal.Status = status;
            if (metadata?.FailureReason is not null)
                goal.FailureReason = metadata.FailureReason;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult(goalId == goal.Id ? goal : null);

        public Task<Goal> CreateGoalAsync(Goal goalToCreate, CancellationToken ct = default) => Task.FromResult(goalToCreate);

        public Task UpdateGoalAsync(Goal goalToUpdate, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
            string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IterationSummary>>([]);

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
            Task.FromResult(release);

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<Release?>(null);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Release>>([]);

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
            string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
            int? limit = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
    }

    /// <summary>Minimal brain stub that returns the default plan.</summary>
    private sealed class FailedWorkerFakeBrain : IDistributedBrain
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

    /// <summary>
    /// In-memory goal source that records status updates onto the shared goal instance so
    /// tests can observe the lifecycle's terminal write.
    /// </summary>
    private sealed class FailedWorkerGoalSource(
        Goal goal,
        Action<GoalStatus, GoalUpdateMetadata?> onStatusUpdate) : IGoalSource
    {
        public string Name => "failed-worker-goal-source";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            onStatusUpdate(status, metadata);
            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal brain stub for the failed-worker end-to-end dispatcher test.</summary>
    private sealed class FailedWorkerDispatcherFakeBrain : IDistributedBrain
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
