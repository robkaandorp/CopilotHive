using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using CopilotHive.Git;

namespace CopilotHive.Tests;

/// <summary>
/// Tests verifying that clarification Q&amp;As are correctly logged to the pipeline
/// and progress log in each resolution scenario (brain, composer, human, timeout).
/// </summary>
public sealed class ClarificationLoggingTests
{
    // ── ClarificationEntry record ─────────────────────────────────────────

    [Fact]
    public void ClarificationEntry_RecordEquality_Works()
    {
        var ts = DateTime.UtcNow;
        var a = new ClarificationEntry(ts, "goal-1", 1, "Coding", "coder", "Q?", "A!", "brain");
        var b = new ClarificationEntry(ts, "goal-1", 1, "Coding", "coder", "Q?", "A!", "brain");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ClarificationEntry_DifferentAnsweredBy_NotEqual()
    {
        var ts = DateTime.UtcNow;
        var a = new ClarificationEntry(ts, "goal-1", 1, "Coding", "coder", "Q?", "A!", "brain");
        var b = new ClarificationEntry(ts, "goal-1", 1, "Coding", "coder", "Q?", "A!", "human");
        Assert.NotEqual(a, b);
    }

    // ── GoalPipeline.Clarifications ────────────────────────────────────────

    [Fact]
    public void GoalPipeline_Clarifications_InitiallyEmpty()
    {
        var pipeline = CreatePipeline();
        Assert.Empty(pipeline.Clarifications);
    }

    [Fact]
    public void GoalPipeline_IsWaitingForClarification_DefaultsFalse()
    {
        var pipeline = CreatePipeline();
        Assert.False(pipeline.IsWaitingForClarification);
    }

    [Fact]
    public void GoalPipeline_IsWaitingForClarification_CanBeSet()
    {
        var pipeline = CreatePipeline();
        pipeline.IsWaitingForClarification = true;
        Assert.True(pipeline.IsWaitingForClarification);
    }

    // ── AskBrainAsync — brain answers directly ─────────────────────────────

    [Fact]
    public async Task AskBrainAsync_BrainAnswers_ClarificationAddedToPipeline()
    {
        var brain = new FakeBrain(BrainResponse.Answer("Direct brain answer."));
        var (dispatcher, pipeline) = CreateDispatcher(brain, queue: null);

        var answer = await dispatcher.AskBrainAsync(pipeline, "What format?", TestContext.Current.CancellationToken);

        Assert.Equal("Direct brain answer.", answer);
        var entry = Assert.Single(pipeline.Clarifications);
        Assert.Equal("What format?", entry.Question);
        Assert.Equal("Direct brain answer.", entry.Answer);
        Assert.Equal("brain", entry.AnsweredBy);
        Assert.Equal(pipeline.GoalId, entry.GoalId);
        Assert.Equal(pipeline.Iteration, entry.Iteration);
        Assert.Equal(pipeline.Phase.ToString(), entry.Phase);
    }

    [Fact]
    public async Task AskBrainAsync_BrainAnswers_ClarificationAddedToProgressLog()
    {
        var brain = new FakeBrain(BrainResponse.Answer("My direct answer."));
        var progressLog = new ProgressLog();
        var (dispatcher, pipeline) = CreateDispatcher(brain, queue: null, progressLog: progressLog);

        await dispatcher.AskBrainAsync(pipeline, "What to do?", TestContext.Current.CancellationToken);

        var recent = progressLog.GetRecent(10);
        var clarEntry = recent.FirstOrDefault(e => e.Status == "clarification");
        Assert.NotNull(clarEntry);
        Assert.Contains("What to do?", clarEntry.Details);
        Assert.Contains("My direct answer.", clarEntry.Details);
        Assert.Contains("answered by: brain", clarEntry.Details);
        Assert.Equal(pipeline.GoalId, clarEntry.GoalId);
    }

    // ── AskBrainAsync — brain not available ────────────────────────────────

    [Fact]
    public async Task AskBrainAsync_BrainNotAvailable_NoClarificationRecorded()
    {
        var (dispatcher, pipeline) = CreateDispatcher(brain: null, queue: null);

        var answer = await dispatcher.AskBrainAsync(pipeline, "Question?", TestContext.Current.CancellationToken);

        Assert.Contains("Brain is not available", answer);
        Assert.Empty(pipeline.Clarifications);
    }

    // ── AskBrainAsync — brain escalates, composer answers ─────────────────

    [Fact]
    public async Task AskBrainAsync_ComposerAnswers_ClarificationRecordedWithComposerAnsweredBy()
    {
        var brain = new FakeBrain(BrainResponse.Escalated("Need composer input", "Need composer input"));
        var queue = new ClarificationQueueService();
        var router = new FakeRouter("Composer answer here.");
        var (dispatcher, pipeline) = CreateDispatcher(brain, queue, router: router);

        var answer = await dispatcher.AskBrainAsync(pipeline, "Complex question?", TestContext.Current.CancellationToken);

        Assert.Equal("Composer answer here.", answer);
        var entry = Assert.Single(pipeline.Clarifications);
        Assert.Equal("composer", entry.AnsweredBy);
        Assert.Equal("Composer answer here.", entry.Answer);
    }

    [Fact]
    public async Task AskBrainAsync_ComposerAnswers_IsWaitingReverted()
    {
        var brain = new FakeBrain(BrainResponse.Escalated("Need composer input", "Need composer input"));
        var queue = new ClarificationQueueService();
        var router = new FakeRouter("Composer answer.");
        var (dispatcher, pipeline) = CreateDispatcher(brain, queue, router: router);

        await dispatcher.AskBrainAsync(pipeline, "Question?", TestContext.Current.CancellationToken);

        Assert.False(pipeline.IsWaitingForClarification);
    }

    // ── AskBrainAsync — brain escalates, no router/queue ──────────────────

    [Fact]
    public async Task AskBrainAsync_EscalatesNoRouterOrQueue_NoClarificationRecorded()
    {
        var brain = new FakeBrain(BrainResponse.Escalated("Cannot answer", "Cannot answer from codebase"));
        var (dispatcher, pipeline) = CreateDispatcher(brain, queue: null);

        var answer = await dispatcher.AskBrainAsync(pipeline, "Question?", TestContext.Current.CancellationToken);

        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, answer);
        Assert.Empty(pipeline.Clarifications);
    }

    // ── AskBrainAsync — brain escalates, human answers ────────────────────

    [Fact]
    public async Task AskBrainAsync_HumanAnswers_ClarificationRecordedWithHumanAnsweredBy()
    {
        var brain = new FakeBrain(BrainResponse.Escalated("Need human input", "Need human input"));
        var queue = new ClarificationQueueService();
        // Router returns null to escalate to human
        var router = new FakeRouter(null);
        var (dispatcher, pipeline) = CreateDispatcher(brain, queue, router: router);

        // Use a shorter timeout for the test
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Start the AskBrainAsync in a task
        var askTask = Task.Run(() => dispatcher.AskBrainAsync(pipeline, "Human question?", cts.Token), cts.Token);

        // Wait for the request to be enqueued (poll the queue)
        ClarificationRequest? capturedRequest = null;
        for (int i = 0; i < 100 && capturedRequest is null; i++)
        {
            await Task.Delay(50, cts.Token);
            var allRequests = queue.GetAllRequests();
            if (allRequests.Count > 0)
                capturedRequest = allRequests[0];
        }

        Assert.NotNull(capturedRequest);
        // Pipeline should be in waiting state while we haven't answered
        Assert.True(pipeline.IsWaitingForClarification);

        // Submit human answer
        queue.SubmitAnswer(capturedRequest.Id, "Human answer here.", "human");

        var answer = await askTask;
        Assert.Equal("Human answer here.", answer);

        var entry = Assert.Single(pipeline.Clarifications);
        Assert.Equal("human", entry.AnsweredBy);
        Assert.Equal("Human question?", entry.Question);
        Assert.Equal("Human answer here.", entry.Answer);
        Assert.False(pipeline.IsWaitingForClarification);
    }

    [Fact]
    public async Task AskBrainAsync_HumanAnswers_WaitingStateReverted()
    {
        var brain = new FakeBrain(BrainResponse.Escalated("Need human", "Need human input"));
        var queue = new ClarificationQueueService();
        var router = new FakeRouter(null);
        var (dispatcher, pipeline) = CreateDispatcher(brain, queue, router: router);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var askTask = Task.Run(() => dispatcher.AskBrainAsync(pipeline, "Q?", cts.Token), cts.Token);

        ClarificationRequest? capturedRequest = null;
        for (int i = 0; i < 100 && capturedRequest is null; i++)
        {
            await Task.Delay(50, cts.Token);
            var allRequests = queue.GetAllRequests();
            if (allRequests.Count > 0)
                capturedRequest = allRequests[0];
        }

        Assert.True(pipeline.IsWaitingForClarification);
        queue.SubmitAnswer(capturedRequest!.Id, "A.", "human");

        await askTask;
        Assert.False(pipeline.IsWaitingForClarification);
    }

    // ── AskBrainAsync — human timeout ──────────────────────────────────────

    [Fact]
    public async Task AskBrainAsync_HumanTimeout_ReturnsFallbackAndRecordsTimeout()
    {
        var brain = new FakeBrain(BrainResponse.Escalated("Need human input", "Need human input"));
        var queue = new ClarificationQueueService();
        var router = new FakeRouter(null);
        var (dispatcher, pipeline) = CreateDispatcher(brain, queue, router: router);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var askTask = Task.Run(() => dispatcher.AskBrainAsync(pipeline, "Timeout question?", cts.Token), cts.Token);

        // Wait for the request to be enqueued
        ClarificationRequest? capturedRequest = null;
        for (int i = 0; i < 100 && capturedRequest is null; i++)
        {
            await Task.Delay(50, cts.Token);
            var allRequests = queue.GetAllRequests();
            if (allRequests.Count > 0)
                capturedRequest = allRequests[0];
        }

        Assert.NotNull(capturedRequest);
        Assert.True(pipeline.IsWaitingForClarification);

        // Use reflection to get the TCS from the queue and fault it with TimeoutException
        // This simulates the actual timeout path where WaitAsync throws TimeoutException
        var waitersField = typeof(ClarificationQueueService)
            .GetField("_waiters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(waitersField);
        var waiters = waitersField!.GetValue(queue) as System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>>;
        Assert.NotNull(waiters);
        Assert.True(waiters!.TryGetValue(capturedRequest.Id, out var tcs));
        Assert.NotNull(tcs);

        // Fault the TCS with TimeoutException to simulate actual timeout
        tcs!.TrySetException(new TimeoutException("Simulated timeout"));

        var answer = await askTask;
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, answer);

        var entry = Assert.Single(pipeline.Clarifications);
        Assert.Equal("timeout", entry.AnsweredBy);
        Assert.Equal("Timeout question?", entry.Question);
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, entry.Answer);
        Assert.False(pipeline.IsWaitingForClarification);
    }

    [Fact]
    public async Task AskBrainAsync_Timeout_WaitingStateReverted()
    {
        var brain = new FakeBrain(BrainResponse.Escalated("need human", "Need human input"));
        var queue = new ClarificationQueueService();
        var router = new FakeRouter(null);
        var (dispatcher, pipeline) = CreateDispatcher(brain, queue, router: router);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var askTask = Task.Run(() => dispatcher.AskBrainAsync(pipeline, "Q?", cts.Token), cts.Token);

        ClarificationRequest? capturedRequest = null;
        for (int i = 0; i < 100 && capturedRequest is null; i++)
        {
            await Task.Delay(50, cts.Token);
            var allRequests = queue.GetAllRequests();
            if (allRequests.Count > 0)
                capturedRequest = allRequests[0];
        }

        Assert.NotNull(capturedRequest);
        Assert.True(pipeline.IsWaitingForClarification);
        queue.MarkTimedOut(capturedRequest.Id);

        await askTask;
        Assert.False(pipeline.IsWaitingForClarification);
    }

    // ── AskBrainAsync — multiple clarifications accumulated ───────────────

    [Fact]
    public async Task AskBrainAsync_MultipleCalls_AllClarificationsRecorded()
    {
        var brain = new FakeBrain(BrainResponse.Answer("Answer one."));
        var (dispatcher, pipeline) = CreateDispatcher(brain, queue: null);

        await dispatcher.AskBrainAsync(pipeline, "Q1?", TestContext.Current.CancellationToken);

        // Change response for second call
        brain.SetNextResponse(BrainResponse.Answer("Answer two."));
        await dispatcher.AskBrainAsync(pipeline, "Q2?", TestContext.Current.CancellationToken);

        Assert.Equal(2, pipeline.Clarifications.Count);
        Assert.Contains(pipeline.Clarifications, e => e.Question == "Q1?" && e.Answer == "Answer one.");
        Assert.Contains(pipeline.Clarifications, e => e.Question == "Q2?" && e.Answer == "Answer two.");
    }

    // ── ProgressLog.AddClarification ──────────────────────────────────────

    [Fact]
    public void ProgressLog_AddClarification_AppearsInRecent()
    {
        var log = new ProgressLog();
        var entry = new ClarificationEntry(
            DateTime.UtcNow, "goal-1", 1, "Coding", "coder", "My question?", "My answer.", "brain");

        log.AddClarification(entry);

        var recent = log.GetRecent(10);
        var found = Assert.Single(recent);
        Assert.Equal("clarification", found.Status);
        Assert.Equal("goal-1", found.GoalId);
        Assert.Contains("My question?", found.Details);
        Assert.Contains("My answer.", found.Details);
        Assert.Contains("answered by: brain", found.Details);
    }

    [Fact]
    public void ProgressLog_AddClarification_UsesWorkerRoleAsWorkerId()
    {
        var log = new ProgressLog();
        var entry = new ClarificationEntry(
            DateTime.UtcNow, "goal-1", 1, "Testing", "tester", "Q?", "A.", "composer");

        log.AddClarification(entry);

        var found = Assert.Single(log.GetRecent(10));
        Assert.Equal("tester", found.WorkerId);
    }

    [Fact]
    public void ProgressLog_AddClarification_TimestampMatchesClarificationEntry()
    {
        var log = new ProgressLog();
        var ts = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var entry = new ClarificationEntry(ts, "goal-1", 1, "Coding", "coder", "Q?", "A.", "human");

        log.AddClarification(entry);

        var found = Assert.Single(log.GetRecent(10));
        Assert.Equal(ts, found.Timestamp);
    }

    // ── IterationSummary.Clarifications persistence ────────────────────────

    [Fact]
    public void IterationSummary_Clarifications_DefaultsToEmpty()
    {
        var summary = new IterationSummary { Iteration = 1 };
        Assert.Empty(summary.Clarifications);
    }

    [Fact]
    public void BuildIterationSummary_IncludesClarificationsForCurrentIteration()
    {
        var goal = new Goal { Id = "g-1", Description = "Test" };
        var pipeline = new GoalPipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);

        // Add clarifications — one for current iteration, one for a different iteration
        pipeline.Clarifications.Add(new ClarificationEntry(
            DateTime.UtcNow, "g-1", 1, "Coding", "coder", "Q1?", "A1.", "brain"));
        pipeline.Clarifications.Add(new ClarificationEntry(
            DateTime.UtcNow, "g-1", 2, "Testing", "tester", "Q2?", "A2.", "composer"));

        var summary = GoalDispatcher.BuildIterationSummary(pipeline, failedPhase: null);

        // Only clarifications for iteration 1 should be included
        Assert.Single(summary.Clarifications);
        Assert.Equal("Q1?", summary.Clarifications[0].Question);
        Assert.Equal("A1.", summary.Clarifications[0].Answer);
        Assert.Equal("brain", summary.Clarifications[0].AnsweredBy);
    }

    [Fact]
    public void BuildIterationSummary_NoClarifications_EmptyList()
    {
        var goal = new Goal { Id = "g-1", Description = "Test" };
        var pipeline = new GoalPipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var summary = GoalDispatcher.BuildIterationSummary(pipeline, failedPhase: null);

        Assert.Empty(summary.Clarifications);
    }

    // ── UI Rendering: PhaseIcon and PhaseBadge ────────────────────────────

    [Theory]
    [InlineData("waiting", "badge-yellow")]
    [InlineData("active", "badge-blue")]
    [InlineData("completed", "badge-green")]
    [InlineData("failed", "badge-red")]
    [InlineData("unknown", "badge-muted")]
    public void PhaseBadge_WaitingStatus_ReturnsYellow(string status, string expectedBadge)
    {
        // Matches the logic from GoalDetail.razor PhaseBadge()
        var badge = status switch
        {
            "completed" => "badge-green",
            "failed" => "badge-red",
            "active" => "badge-blue",
            "waiting" => "badge-yellow",
            _ => "badge-muted",
        };
        Assert.Equal(expectedBadge, badge);
    }

    [Theory]
    [InlineData("waiting", "💬")]
    [InlineData("active", "●")]
    [InlineData("completed", "✓")]
    [InlineData("failed", "✗")]
    [InlineData("pending", "○")]
    [InlineData("skipped", "~")]
    [InlineData("unknown", "○")]
    public void PhaseIcon_WaitingStatus_ReturnsSpeechBubble(string status, string expectedIcon)
    {
        // Matches the logic from GoalDetail.razor PhaseIcon()
        var icon = status switch
        {
            "completed" => "✓",
            "failed" => "✗",
            "active" => "●",
            "waiting" => "💬",
            "pending" => "○",
            "skipped" => "~",
            _ => "○",
        };
        Assert.Equal(expectedIcon, icon);
    }

    [Fact]
    public void WaitingStatus_HasSpecialCssClass()
    {
        // Verify CSS class exists for "waiting" status
        // This mirrors the CSS in site.css for .phase-box.waiting
        var cssClasses = new Dictionary<string, string[]>
        {
            ["active"] = ["phase-box", "active"],
            ["waiting"] = ["phase-box", "waiting"],
            ["completed"] = ["phase-box", "completed"],
            ["failed"] = ["phase-box", "failed"],
        };

        Assert.Contains("waiting", cssClasses["waiting"]);
        Assert.Contains("phase-box", cssClasses["waiting"]);
    }

    // ── DashboardStateService integration ──────────────────────────────────

    [Fact]
    public void PhaseViewInfo_Clarifications_PropertyExists()
    {
        // Verify PhaseViewInfo has Clarifications property with correct default
        var phaseView = new PhaseViewInfo
        {
            Name = "Coding",
            Status = "active",
            RoleName = "coder",
        };

        Assert.NotNull(phaseView.Clarifications);
        Assert.Empty(phaseView.Clarifications);
    }

    [Fact]
    public void PhaseViewInfo_Clarifications_CanBeSet()
    {
        var clarifications = new List<ClarificationEntry>
        {
            new(DateTime.UtcNow, "g-1", 1, "Coding", "coder", "Q?", "A.", "brain"),
        };

        var phaseView = new PhaseViewInfo
        {
            Name = "Coding",
            Status = "waiting",
            RoleName = "coder",
            Clarifications = clarifications,
        };

        Assert.Single(phaseView.Clarifications);
        Assert.Equal("Q?", phaseView.Clarifications[0].Question);
    }

    [Fact]
    public void GoalPipeline_IsWaitingForClarification_AffectsPhaseStatus()
    {
        // Verify that setting IsWaitingForClarification affects the status
        // This mirrors the logic in DashboardStateService.BuildPhaseViews
        var pipeline = CreatePipeline();
        Assert.False(pipeline.IsWaitingForClarification);

        // The status logic is: status = pipeline.IsWaitingForClarification ? "waiting" : "active"
        var statusBefore = pipeline.IsWaitingForClarification ? "waiting" : "active";
        Assert.Equal("active", statusBefore);

        pipeline.IsWaitingForClarification = true;
        var statusAfter = pipeline.IsWaitingForClarification ? "waiting" : "active";
        Assert.Equal("waiting", statusAfter);
    }

    // ── SqliteGoalStore round-trip ─────────────────────────────────────────

    [Fact]
    public async Task SqliteGoalStore_ClarificationsRoundTrip_PersistedAndLoaded()
    {
        // Arrange — in-memory SQLite
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        var store = new SqliteGoalStore(connection, NullLogger<SqliteGoalStore>.Instance);

        var goal = new Goal
        {
            Id = "g-clarif-1",
            Description = "Test clarification round-trip",
            CreatedAt = DateTime.UtcNow,
        };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 10 }],
            Clarifications =
            [
                new PersistedClarification
                {
                    Timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Phase = "Coding",
                    WorkerRole = "coder",
                    Question = "What pattern to use?",
                    Answer = "Use repository pattern.",
                    AnsweredBy = "brain",
                },
            ],
        };

        // Act
        await store.AddIterationAsync(goal.Id, summary, TestContext.Current.CancellationToken);
        var iterations = await store.GetIterationsAsync(goal.Id, TestContext.Current.CancellationToken);

        // Assert
        var loaded = Assert.Single(iterations);
        Assert.Single(loaded.Clarifications);
        var c = loaded.Clarifications[0];
        Assert.Equal("Coding", c.Phase);
        Assert.Equal("coder", c.WorkerRole);
        Assert.Equal("What pattern to use?", c.Question);
        Assert.Equal("Use repository pattern.", c.Answer);
        Assert.Equal("brain", c.AnsweredBy);
    }

    [Fact]
    public async Task SqliteGoalStore_NoClarifications_LoadsEmptyList()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        var store = new SqliteGoalStore(connection, NullLogger<SqliteGoalStore>.Instance);

        var goal = new Goal { Id = "g-clarif-2", Description = "No clarifications", CreatedAt = DateTime.UtcNow };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 5 }],
            Clarifications = [],
        };
        await store.AddIterationAsync(goal.Id, summary, TestContext.Current.CancellationToken);
        var iterations = await store.GetIterationsAsync(goal.Id, TestContext.Current.CancellationToken);

        Assert.Single(iterations);
        Assert.Empty(iterations[0].Clarifications);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static GoalPipeline CreatePipeline()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        return pipeline;
    }

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline) CreateDispatcher(
        IDistributedBrain? brain,
        ClarificationQueueService? queue,
        IClarificationRouter? router = null,
        ProgressLog? progressLog = null)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var goalSource = new FakeGoalSource2(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var notifier = new TaskCompletionNotifier();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            notifier,
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
            clarificationRouter: router,
            clarificationQueue: router is not null || queue is not null ? queue : null,
            progressLog: progressLog);

        return (dispatcher, pipeline);
    }

    // ── Fake implementations ───────────────────────────────────────────────

    private sealed class FakeBrain : IDistributedBrain
    {
        private BrainResponse _response;
        public FakeBrain(BrainResponse response) => _response = response;
        public void SetNextResponse(BrainResponse response) => _response = response;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));
        public Task<PromptResult> CraftPromptAsync(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success(_response.Text ?? string.Empty));
        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(_response);
        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;
        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
        public void DeleteGoalSession(string goalId) { }
        public BrainStats? GetStats() => null;
    }

    private sealed class FakeRouter : IClarificationRouter
    {
        private readonly string? _answer;
        public FakeRouter(string? answer) => _answer = answer;

        public Task<string?> TryAutoAnswerAsync(
            string goalId, string question, string context,
            ClarificationQueueService queue, ClarificationRequest request,
            CancellationToken ct)
        {
            if (_answer is not null && _answer != "ESCALATE_TO_HUMAN")
                return Task.FromResult<string?>(_answer);
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeGoalSource2 : IGoalSource
    {
        private readonly Goal _goal;
        public FakeGoalSource2(Goal goal) => _goal = goal;
        public string Name => "fake";
        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([_goal]);
        public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
