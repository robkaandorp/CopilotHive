using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Direct tests for <see cref="PipelineHelpers"/> — the extracted static helper methods.
/// These verify that the extracted class is correct.
/// </summary>
public sealed class PipelineHelpersTests
{
    // ── BuildSquashCommitMessage ────────────────────────────────────────────

    [Fact]
    public void BuildSquashCommitMessage_ShortDescription_ReturnsSingleLine()
    {
        var result = PipelineHelpers.BuildSquashCommitMessage("g-123", "Fix bug");

        Assert.Equal("Goal: g-123 — Fix bug", result);
    }

    [Fact]
    public void BuildSquashCommitMessage_LongDescription_TruncatesSubject()
    {
        var longDesc = new string('x', 200);
        var result = PipelineHelpers.BuildSquashCommitMessage("g-1", longDesc);

        // Subject line should be truncated to 120 chars
        var subjectLine = result.Split('\n')[0];
        Assert.True(subjectLine.Length <= 120, $"Subject line is {subjectLine.Length} chars, expected <= 120");
    }

    [Fact]
    public void BuildSquashCommitMessage_MultilineDescription_UsesFirstLineInSubject()
    {
        var desc = "First line of description\nSecond line of description";
        var result = PipelineHelpers.BuildSquashCommitMessage("g-42", desc);

        Assert.StartsWith("Goal: g-42 — First line of description", result);
    }

    // ── InjectTokenIntoUrl ──────────────────────────────────────────────────

    [Fact]
    public void InjectTokenIntoUrl_NoToken_ReturnsOriginal()
    {
        var url = "https://github.com/owner/repo.git";
        // Clear GH_TOKEN if set
        var originalToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            var result = PipelineHelpers.InjectTokenIntoUrl(url);
            Assert.Equal(url, result);
        }
        finally
        {
            if (originalToken is not null)
                Environment.SetEnvironmentVariable("GH_TOKEN", originalToken);
        }
    }

    [Fact]
    public void InjectTokenIntoUrl_NonGitHubUrl_ReturnsOriginal()
    {
        var url = "https://gitlab.com/owner/repo.git";
        var originalToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", "test-token");
            var result = PipelineHelpers.InjectTokenIntoUrl(url);
            Assert.Equal(url, result);
        }
        finally
        {
            if (originalToken is not null)
                Environment.SetEnvironmentVariable("GH_TOKEN", originalToken);
            else
                Environment.SetEnvironmentVariable("GH_TOKEN", null);
        }
    }

    // ── GetLastCraftPromptFromConversation ───────────────────────────────────

    [Fact]
    public void GetLastCraftPromptFromConversation_NoEntries_ReturnsNull()
    {
        var goal = new Goal { Id = "g-1", Description = "Test" };
        var pipeline = new GoalPipeline(goal);

        var result = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);
        Assert.Null(result);
    }

    [Fact]
    public void GetLastCraftPromptFromConversation_WithCraftPromptEntry_ReturnsContent()
    {
        var goal = new Goal { Id = "g-1", Description = "Test" };
        var pipeline = new GoalPipeline(goal);
        pipeline.Conversation.Add(new ConversationEntry("user", "Craft the prompt", 1, "craft-prompt"));

        var result = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);
        Assert.Equal("Craft the prompt", result);
    }

    // ── GetPlanningPromptsFromConversation ──────────────────────────────────

    [Fact]
    public void GetPlanningPromptsFromConversation_NoEntries_ReturnsNulls()
    {
        var goal = new Goal { Id = "g-1", Description = "Test" };
        var pipeline = new GoalPipeline(goal);

        var (prompt, response) = PipelineHelpers.GetPlanningPromptsFromConversation(pipeline);
        Assert.Null(prompt);
        Assert.Null(response);
    }

    [Fact]
    public void GetPlanningPromptsFromConversation_WithPlanningEntries_ReturnsPromptAndResponse()
    {
        var goal = new Goal { Id = "g-1", Description = "Test" };
        var pipeline = new GoalPipeline(goal);
        pipeline.Conversation.Add(new ConversationEntry("user", "Plan this iteration", 1, "planning"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Here's the plan", 1, "planning"));

        var (prompt, response) = PipelineHelpers.GetPlanningPromptsFromConversation(pipeline);
        Assert.Equal("Plan this iteration", prompt);
        Assert.Equal("Here's the plan", response);
    }

    // ── BuildWorkerOutputSummary ─────────────────────────────────────────────

    [Fact]
    public void BuildWorkerOutputSummary_WithMetrics_IncludesTestCounts()
    {
        var result = new TaskResult
        {
            TaskId = "task-1",
            Status = TaskOutcome.Completed,
            Output = "Tests passed",
            Metrics = new TaskMetrics
            {
                TotalTests = 100,
                PassedTests = 95,
                FailedTests = 5,
                Summary = "95/100 passed",
            },
        };
        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Testing, "PASS", result);

        Assert.Contains("95/100 passed", summary);
        Assert.Contains("Tests: 95/100 passed, 5 failed", summary);
    }

    [Fact]
    public void BuildWorkerOutputSummary_WithPushFailure_IncludesWarning()
    {
        var result = new TaskResult
        {
            TaskId = "task-2",
            Status = TaskOutcome.Completed,
            Output = "Some output",
            GitStatus = new GitChangeSummary { FilesChanged = 3, Insertions = 10, Deletions = 5, Pushed = false },
        };
        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Coding, "PASS", result);

        Assert.Contains("Git push FAILED", summary);
    }

    [Fact]
    public void BuildWorkerOutputSummary_IncludesTestMetrics()
    {
        var result = new TaskResult
        {
            TaskId = "task-3",
            Status = TaskOutcome.Completed,
            Output = "Test output",
            Metrics = new TaskMetrics { TotalTests = 10, PassedTests = 8, FailedTests = 2 },
        };
        var summary = PipelineHelpers.BuildWorkerOutputSummary(GoalPhase.Testing, "PASS", result);

        Assert.Contains("8/10", summary);
    }

    // ── BuildIterationSummary ───────────────────────────────────────────────

    [Fact]
    public void BuildIterationSummary_CapturesTestCounts()
    {
        var goal = new Goal { Id = "g-1", Description = "Test" };
        var pipeline = new GoalPipeline(goal);
        pipeline.Metrics.TotalTests = 50;
        pipeline.Metrics.PassedTests = 48;
        pipeline.Metrics.FailedTests = 2;
        pipeline.Metrics.BuildSuccess = true;

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Iteration = 1,
            Occurrence = 1,
            Result = PhaseOutcome.Pass,
            Verdict = "PASS",
        });

        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        Assert.NotNull(summary.TestCounts);
        Assert.Equal(50, summary.TestCounts.Total);
        Assert.Equal(48, summary.TestCounts.Passed);
        Assert.Equal(2, summary.TestCounts.Failed);
        Assert.True(summary.BuildSuccess);
    }

    [Fact]
    public void BuildIterationSummary_CapturesIterationNumber()
    {
        var goal = new Goal { Id = "g-1", Description = "Test" };
        var pipeline = new GoalPipeline(goal);
        pipeline.Metrics.TotalTests = 10;
        pipeline.Metrics.PassedTests = 10;
        pipeline.Metrics.FailedTests = 0;
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Iteration = 1,
            Result = PhaseOutcome.Pass,
        });

        var summary = PipelineHelpers.BuildIterationSummary(pipeline);

        // Iteration number should be captured in summary
        Assert.Equal(pipeline.Iteration, summary.Iteration);
    }
}

/// <summary>
/// Integration tests for <see cref="GoalLifecycleService"/> — verifying the extracted
/// lifecycle methods behave correctly when called directly.
/// </summary>
public sealed class GoalLifecycleServiceTests
{
    // ── PopulateAgentsMdVersions ────────────────────────────────────────────

    [Fact]
    public void PopulateAgentsMdVersions_WithAgentsManager_PopulatesVersions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"agents-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var agentsManager = new AgentsManager(tempDir, NullLogger<AgentsManager>.Instance);

            // Write agents.md files so GetHistory returns versions
            foreach (var role in WorkerRoles.AgentRoles)
            {
                var roleName = role.ToRoleName();
                var filePath = Path.Combine(tempDir, $"{roleName}.agents.md");
                File.WriteAllText(filePath, $"# {roleName} agent instructions v1");
            }

            var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
            var pipeline = new GoalPipeline(goal);
            var logger = NullLogger<GoalLifecycleService>.Instance;

            var service = new GoalLifecycleService(
                goalManager: new GoalManager(),
                logger: logger,
                agentsManager: agentsManager);

            service.PopulateAgentsMdVersions(pipeline);

            // After population, each agent role should have a version string
            foreach (var role in WorkerRoles.AgentRoles)
            {
                var roleName = role.ToRoleName();
                Assert.True(pipeline.Metrics.AgentsMdVersions.ContainsKey(roleName),
                    $"Expected version for role {roleName}");
            }
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void PopulateAgentsMdVersions_WithoutAgentsManager_DoesNotThrow()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);

        var service = new GoalLifecycleService(
            goalManager: new GoalManager(),
            logger: NullLogger<GoalLifecycleService>.Instance,
            agentsManager: null);

        // Should not throw even when AgentsManager is null
        service.PopulateAgentsMdVersions(pipeline);
        Assert.Empty(pipeline.Metrics.AgentsMdVersions);
    }

    // ── GetModifiedRoles ─────────────────────────────────────────────────────

    [Fact]
    public void GetModifiedRoles_NewVersion_ReturnsModifiedRole()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"metrics-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metricsTracker = new MetricsTracker(tempDir, NullLogger<MetricsTracker>.Instance);
            metricsTracker.RecordIteration(new IterationMetrics
            {
                Iteration = 1,
                AgentsMdVersions = new Dictionary<string, string> { ["coder"] = "v001" },
            });
            metricsTracker.RecordIteration(new IterationMetrics
            {
                Iteration = 2,
                AgentsMdVersions = new Dictionary<string, string> { ["coder"] = "v002" },
            });

            var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
            var pipeline = new GoalPipeline(goal);
            pipeline.Metrics.AgentsMdVersions["coder"] = "v002";

            var service = new GoalLifecycleService(
                goalManager: new GoalManager(),
                logger: NullLogger<GoalLifecycleService>.Instance,
                metricsTracker: metricsTracker);

            var modified = service.GetModifiedRoles(pipeline.Metrics);

            Assert.Contains(WorkerRole.Coder, modified);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void GetModifiedRoles_NoChange_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"metrics-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metricsTracker = new MetricsTracker(tempDir, NullLogger<MetricsTracker>.Instance);
            metricsTracker.RecordIteration(new IterationMetrics
            {
                Iteration = 1,
                AgentsMdVersions = new Dictionary<string, string> { ["coder"] = "v001" },
            });
            metricsTracker.RecordIteration(new IterationMetrics
            {
                Iteration = 2,
                AgentsMdVersions = new Dictionary<string, string> { ["coder"] = "v001" },
            });

            var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
            var pipeline = new GoalPipeline(goal);
            pipeline.Metrics.AgentsMdVersions["coder"] = "v001";

            var service = new GoalLifecycleService(
                goalManager: new GoalManager(),
                logger: NullLogger<GoalLifecycleService>.Instance,
                metricsTracker: metricsTracker);

            var modified = service.GetModifiedRoles(pipeline.Metrics);

            Assert.Empty(modified);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void GetModifiedRoles_InsufficientHistory_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"metrics-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Only 1 entry in history — need at least 2 for comparison
            var metricsTracker = new MetricsTracker(tempDir, NullLogger<MetricsTracker>.Instance);
            metricsTracker.RecordIteration(new IterationMetrics
            {
                Iteration = 1,
                AgentsMdVersions = new Dictionary<string, string> { ["coder"] = "v001" },
            });

            var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
            var pipeline = new GoalPipeline(goal);
            pipeline.Metrics.AgentsMdVersions["coder"] = "v002";

            var service = new GoalLifecycleService(
                goalManager: new GoalManager(),
                logger: NullLogger<GoalLifecycleService>.Instance,
                metricsTracker: metricsTracker);

            var modified = service.GetModifiedRoles(pipeline.Metrics);

            Assert.Empty(modified);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── MarkGoalFailedAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task MarkGoalFailedAsync_SetsGoalToFailed()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);
        pipeline.StateMachine.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSource(goal);
        goalManager.AddSource(goalSource);
        // Must call GetNextGoalAsync to register the goal in the manager
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var service = new GoalLifecycleService(
            goalManager: goalManager,
            logger: NullLogger<GoalLifecycleService>.Instance);

        await service.MarkGoalFailedAsync(pipeline, "Test failure", TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.True(pipeline.CompletedAt.HasValue);
    }

    [Fact]
    public async Task MarkGoalFailedAsync_NotifiesDashboardOnce()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);
        pipeline.StateMachine.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSource(goal);
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var service = new GoalLifecycleService(
            goalManager: goalManager,
            logger: NullLogger<GoalLifecycleService>.Instance,
            dashboardNotifier: notifier);

        await service.MarkGoalFailedAsync(pipeline, "Test failure", TestContext.Current.CancellationToken);

        Assert.Equal(1, notificationCount);
    }

    // ── MarkGoalCompletedAsync ──────────────────────────────────────────────

    [Fact]
    public async Task MarkGoalCompletedAsync_SetsGoalToDone()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);
        pipeline.StateMachine.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSource(goal);
        goalManager.AddSource(goalSource);
        // Must call GetNextGoalAsync to register the goal in the manager
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var service = new GoalLifecycleService(
            goalManager: goalManager,
            logger: NullLogger<GoalLifecycleService>.Instance);

        await service.MarkGoalCompletedAsync(pipeline, TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Done, pipeline.Phase);
        Assert.True(pipeline.CompletedAt.HasValue);
    }

    [Fact]
    public async Task MarkGoalCompletedAsync_FirstCompletion_NotifiesDashboardOnce()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);
        pipeline.StateMachine.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSource(goal);
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var service = new GoalLifecycleService(
            goalManager: goalManager,
            logger: NullLogger<GoalLifecycleService>.Instance,
            dashboardNotifier: notifier);

        await service.MarkGoalCompletedAsync(pipeline, TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Done, pipeline.Phase);
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public async Task MarkGoalCompletedAsync_AlreadyDone_GuardReturnsZeroNotifications()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);
        pipeline.StateMachine.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Done);

        var goalManager = new GoalManager();
        var goalSource = new FakeGoalSource(goal);
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var service = new GoalLifecycleService(
            goalManager: goalManager,
            logger: NullLogger<GoalLifecycleService>.Instance,
            dashboardNotifier: notifier);

        await service.MarkGoalCompletedAsync(pipeline, TestContext.Current.CancellationToken);

        Assert.Equal(0, notificationCount);
    }

    // ── CommitMetricsToConfigRepoAsync ───────────────────────────────────────

    [Fact]
    public async Task CommitMetricsToConfigRepoAsync_WithConfigRepo_WritesMetricsFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"metrics-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
            var pipeline = new GoalPipeline(goal);
            pipeline.Metrics.Iteration = 1;
            pipeline.Metrics.TotalTests = 10;
            pipeline.Metrics.PassedTests = 8;

            var goalManager = new GoalManager();
            var configRepo = new ConfigRepoManager("https://example.com/config.git", tempDir);
            var service = new GoalLifecycleService(
                goalManager: goalManager,
                logger: NullLogger<GoalLifecycleService>.Instance,
                configRepo: configRepo);

            await service.CommitMetricsToConfigRepoAsync(pipeline, TestContext.Current.CancellationToken);

            var metricsPath = Path.Combine(tempDir, "metrics", $"{pipeline.GoalId}.json");
            Assert.True(File.Exists(metricsPath), $"Expected metrics file at {metricsPath}");
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task CommitMetricsToConfigRepoAsync_WithoutConfigRepo_DoesNotThrow()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);

        var service = new GoalLifecycleService(
            goalManager: new GoalManager(),
            logger: NullLogger<GoalLifecycleService>.Instance,
            configRepo: null);

        // Should not throw when ConfigRepo is null
        await service.CommitMetricsToConfigRepoAsync(pipeline, TestContext.Current.CancellationToken);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed class FakeGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "fake-goal-source";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}