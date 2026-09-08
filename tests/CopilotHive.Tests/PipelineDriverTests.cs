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

        var logger = new CapturingLogger<GoalDispatcher>();
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

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
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

        // Assert: the Coding PhaseResult was marked failed with the no-op reason.
        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        Assert.Equal("Coder produced no file changes (no-op)", codingEntry.WorkerOutput);
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
        Assert.Equal("Coder produced no file changes (no-op)", codingInSummary.WorkerOutput);

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

        // Assert: the Coding PhaseResult was marked failed with the no-op reason BEFORE terminal
        // exit, so FinalizeGoalAsync's summary includes the failed Coding phase.
        Assert.Equal(PhaseOutcome.Fail, codingEntry.Result);
        Assert.Equal("Coder produced no file changes (no-op)", codingEntry.WorkerOutput);
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
        Assert.Contains("no-op", codingInSummary.WorkerOutput);

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

    // ── Helpers ──────────────────────────────────────────────────────────

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
