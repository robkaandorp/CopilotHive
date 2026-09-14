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
[Collection("EnvVarMutation")]
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
        // Clear BOTH credential variables — either one alone would inject a token.
        var originalToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        var originalGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            var result = PipelineHelpers.InjectTokenIntoUrl(url);
            Assert.Equal(url, result);
        }
        finally
        {
            if (originalToken is not null)
                Environment.SetEnvironmentVariable("GH_TOKEN", originalToken);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalGithubToken);
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

    // ── InjectTokenIntoUrl: the GITHUB_TOKEN fallback ────────────────────────

    /// <summary>
    /// Runs <paramref name="assert"/> with the two credential variables set to the given values,
    /// restoring BOTH originals afterwards.
    /// </summary>
    private static void WithTokens(string? ghToken, string? githubToken, Action assert)
    {
        var originalGh = Environment.GetEnvironmentVariable("GH_TOKEN");
        var originalGithub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", ghToken);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", githubToken);
            assert();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", originalGh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalGithub);
        }
    }

    [Fact]
    public void InjectTokenIntoUrl_OnlyGhToken_InjectsGhToken()
    {
        WithTokens("gh-token", null, () =>
            Assert.Equal(
                "https://x-access-token:gh-token@github.com/owner/repo.git",
                PipelineHelpers.InjectTokenIntoUrl("https://github.com/owner/repo.git")));
    }

    [Fact]
    public void InjectTokenIntoUrl_GhTokenAbsent_FallsBackToGithubToken()
    {
        WithTokens(null, "github-token", () =>
            Assert.Equal(
                "https://x-access-token:github-token@github.com/owner/repo.git",
                PipelineHelpers.InjectTokenIntoUrl("https://github.com/owner/repo.git")));
    }

    [Fact]
    public void InjectTokenIntoUrl_BothTokensPresent_GhTokenWins()
    {
        WithTokens("gh-token", "github-token", () =>
        {
            var result = PipelineHelpers.InjectTokenIntoUrl("https://github.com/owner/repo.git");

            Assert.Equal("https://x-access-token:gh-token@github.com/owner/repo.git", result);
            Assert.DoesNotContain("github-token", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void InjectTokenIntoUrl_BothTokensAbsent_ReturnsOriginal()
    {
        WithTokens(null, null, () =>
            Assert.Equal(
                "https://github.com/owner/repo.git",
                PipelineHelpers.InjectTokenIntoUrl("https://github.com/owner/repo.git")));
    }

    [Fact]
    public void InjectTokenIntoUrl_GithubTokenFallbackWithNonGitHubUrl_ReturnsOriginal()
    {
        // The https://github.com/ gate is unchanged — the fallback never widens it.
        WithTokens(null, "github-token", () =>
            Assert.Equal(
                "https://gitlab.com/owner/repo.git",
                PipelineHelpers.InjectTokenIntoUrl("https://gitlab.com/owner/repo.git")));
    }

    [Fact]
    public void InjectTokenIntoUrl_EmptyGhToken_FallsBackToGithubToken()
    {
        // An EMPTY GH_TOKEN is a BLANK candidate: the legacy overload now selects its candidates
        // through GitCredentialResolver.Resolve, which skips null/empty/whitespace values, so the
        // valid GITHUB_TOKEN is selected and INJECTED. (Previously the empty string won the ??
        // chain and was then rejected by the IsNullOrEmpty gate, leaving the URL unchanged — that
        // blank-swallows-the-fallback shape is exactly what this now pins as fixed.)
        WithTokens(string.Empty, "github-token", () =>
            Assert.Equal(
                "https://x-access-token:github-token@github.com/owner/repo.git",
                PipelineHelpers.InjectTokenIntoUrl("https://github.com/owner/repo.git")));
    }

    [Fact]
    public void InjectTokenIntoUrl_WhitespaceGithubTokenFallback_IsNotInjected()
    {
        // The gate is blank-AWARE: a whitespace-only credential is treated as ABSENT, so no
        // userinfo is injected and the URL is returned unchanged. A whitespace token could never
        // authenticate — injecting it only produced a corrupt URL.
        WithTokens(null, " ", () =>
            Assert.Equal(
                "https://github.com/owner/repo.git",
                PipelineHelpers.InjectTokenIntoUrl("https://github.com/owner/repo.git")));
    }

    // ── InjectTokenIntoUrl(url, credential): THE EXPLICIT-CREDENTIAL OVERLOAD ────

    [Fact]
    public void InjectTokenIntoUrlWithCredential_HttpsGitHub_InjectsEscapedCredential()
    {
        Assert.Equal(
            "https://x-access-token:oauth-token@github.com/owner/repo.git",
            PipelineHelpers.InjectTokenIntoUrl("https://github.com/owner/repo.git", "oauth-token"));
    }

    [Fact]
    public void InjectTokenIntoUrlWithCredential_NeverReadsTheEnvironment()
    {
        // BOTH aliases are set and the explicit credential is absent: the overload must still
        // leave the URL unchanged. A regression that consulted the environment would inject.
        WithTokens("gh-token", "github-token", () =>
        {
            Assert.Equal(
                "https://github.com/owner/repo.git",
                PipelineHelpers.InjectTokenIntoUrl("https://github.com/owner/repo.git", null));
            Assert.Equal(
                "https://github.com/owner/repo.git",
                PipelineHelpers.InjectTokenIntoUrl("https://github.com/owner/repo.git", "   "));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void InjectTokenIntoUrlWithCredential_MissingCredential_ReturnsOriginal(string? credential)
    {
        Assert.Equal(
            "https://github.com/owner/repo.git",
            PipelineHelpers.InjectTokenIntoUrl("https://github.com/owner/repo.git", credential));
    }

    [Fact]
    public void InjectTokenIntoUrlWithCredential_AlreadyTokenizedUrl_ReplacesUserInfoRatherThanAppending()
    {
        var result = PipelineHelpers.InjectTokenIntoUrl(
            "https://x-access-token:stale-token@github.com/owner/repo.git", "fresh-token");

        Assert.Equal("https://x-access-token:fresh-token@github.com/owner/repo.git", result);
        Assert.DoesNotContain("stale-token", result, StringComparison.Ordinal);
        // Exactly ONE userinfo separator — nothing was appended to the previous credential.
        Assert.Equal(1, result.Count(c => c == '@'));
    }

    [Fact]
    public void InjectTokenIntoUrlWithCredential_CredentialWithReservedCharacters_IsUriEscaped()
    {
        var result = PipelineHelpers.InjectTokenIntoUrl(
            "https://github.com/owner/repo.git", "p@ss/wo rd:1");

        // The RAW credential never appears verbatim — it is escaped so the URL still parses,
        // and the repository identity (host + path) survives intact.
        Assert.DoesNotContain("p@ss/wo rd:1", result, StringComparison.Ordinal);
        var parsed = new Uri(result);
        Assert.Equal("github.com", parsed.Host);
        Assert.Equal("/owner/repo.git", parsed.AbsolutePath);
        Assert.StartsWith("x-access-token:", parsed.UserInfo, StringComparison.Ordinal);
        Assert.Equal("p@ss/wo rd:1", Uri.UnescapeDataString(parsed.UserInfo["x-access-token:".Length..]));
    }

    [Fact]
    public void InjectTokenIntoUrlWithCredential_HostComparisonIsCaseInsensitiveNotSubstring()
    {
        // Case-insensitive EXACT host: an upper-case host IS eligible…
        Assert.Equal(
            "https://x-access-token:tok@github.com/owner/repo.git",
            PipelineHelpers.InjectTokenIntoUrl("https://GitHub.COM/owner/repo.git", "tok"));
    }

    [Theory]
    // …while every host that merely CONTAINS "github.com" is NOT.
    [InlineData("https://github.com.evil.test/owner/repo.git")]
    [InlineData("https://notgithub.com/owner/repo.git")]
    [InlineData("https://github.company.test/owner/repo.git")]
    [InlineData("https://gitlab.com/owner/repo.git")]
    // Non-HTTPS transports and non-443 ports.
    [InlineData("http://github.com/owner/repo.git")]
    [InlineData("https://github.com:8443/owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("/srv/local/repo.git")]
    [InlineData("../relative/repo.git")]
    public void InjectTokenIntoUrlWithCredential_IneligibleUrl_ReturnsOriginalUnchanged(string url)
    {
        var result = PipelineHelpers.InjectTokenIntoUrl(url, "oauth-token");

        Assert.Equal(url, result);
        Assert.DoesNotContain("oauth-token", result, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectTokenIntoUrlWithCredential_ExplicitDefaultPort_IsEligible()
    {
        // An EXPLICIT :443 is the DEFAULT port for https — eligible, and the normalized result
        // keeps the repository identity.
        var result = PipelineHelpers.InjectTokenIntoUrl("https://github.com:443/owner/repo.git", "tok");

        var parsed = new Uri(result);
        Assert.Equal("x-access-token:tok", parsed.UserInfo);
        Assert.Equal("github.com", parsed.Host);
        Assert.Equal("/owner/repo.git", parsed.AbsolutePath);
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

    // ── MarkGoalCompletedAsync end-to-end (no automatic AGENTS rollback) ────
    //
    // The metrics-triggered automatic AGENTS.md rollback on goal completion was
    // deliberately retired. These tests exercise the real MarkGoalCompletedAsync with
    // real AgentsManager and MetricsTracker instances in a setup that WOULD have
    // triggered the old rollback path (nonempty archive history, different
    // previous/current AGENTS version counts, non-null managers, pipeline not yet
    // Done) and assert that completion now leaves every AGENTS.md file byte-identical,
    // emits no regression/rollback/comparison announcement, and still records metrics.

    [Fact]
    public async Task MarkGoalCompletedAsync_CoverageDropVersusPrevious_DoesNotRollBackAgentsMd()
    {
        var agentsDir = Path.Combine(Path.GetTempPath(), $"agents-e2e-{Guid.NewGuid():N}");
        var metricsDir = Path.Combine(Path.GetTempPath(), $"metrics-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(agentsDir);
        try
        {
            // Scenario 1: prior measured coverage 80 vs current unmeasured/default 0,
            // with all tests passing — old code: CoverageDelta=-80 → regression → rollback.
            var fx = await CreateCompletionFixtureAsync(
                agentsDir, metricsDir,
                previousTotalTests: 10, previousPassedTests: 10, previousCoveragePercent: 80.0,
                currentTotalTests: 10, currentPassedTests: 10, currentCoveragePercent: 0.0);

            await ActAndAssertNoRollbackAsync(fx);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(agentsDir);
            TestHelpers.ForceDeleteDirectory(metricsDir);
        }
    }

    [Fact]
    public async Task MarkGoalCompletedAsync_PassRateDropAtEqualCoverage_DoesNotRollBackAgentsMd()
    {
        var agentsDir = Path.Combine(Path.GetTempPath(), $"agents-e2e-{Guid.NewGuid():N}");
        var metricsDir = Path.Combine(Path.GetTempPath(), $"metrics-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(agentsDir);
        try
        {
            // Scenario 2: pass-rate drop far larger than five percentage points at equal
            // coverage — old code: PassRateDelta=-0.5 → regression → rollback.
            var fx = await CreateCompletionFixtureAsync(
                agentsDir, metricsDir,
                previousTotalTests: 10, previousPassedTests: 10, previousCoveragePercent: 80.0,
                currentTotalTests: 10, currentPassedTests: 5, currentCoveragePercent: 80.0);

            await ActAndAssertNoRollbackAsync(fx);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(agentsDir);
            TestHelpers.ForceDeleteDirectory(metricsDir);
        }
    }

    [Fact]
    public async Task MarkGoalCompletedAsync_ZeroCurrentTestsAndDefaultCoverage_DoesNotRollBackAgentsMd()
    {
        var agentsDir = Path.Combine(Path.GetTempPath(), $"agents-e2e-{Guid.NewGuid():N}");
        var metricsDir = Path.Combine(Path.GetTempPath(), $"metrics-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(agentsDir);
        try
        {
            // Scenario 3: zero current tests / default coverage — old code: TotalTests=0
            // skipped the test check but CoverageDelta=-80 still triggered rollback.
            var fx = await CreateCompletionFixtureAsync(
                agentsDir, metricsDir,
                previousTotalTests: 10, previousPassedTests: 10, previousCoveragePercent: 80.0,
                currentTotalTests: 0, currentPassedTests: 0, currentCoveragePercent: 0.0);

            await ActAndAssertNoRollbackAsync(fx);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(agentsDir);
            TestHelpers.ForceDeleteDirectory(metricsDir);
        }
    }

    /// <summary>
    /// Runs the real <see cref="GoalLifecycleService.MarkGoalCompletedAsync"/> end-to-end and
    /// asserts the retired rollback path stays retired: every AGENTS.md file (current bytes
    /// AND archived versions) is byte-identical, no regression/rollback/comparison
    /// announcement is logged, the pipeline reaches Done, the registered goal becomes
    /// Completed, current metrics are recorded and reloadable from disk, and the normal
    /// completion notification fires exactly once.
    /// </summary>
    private static async Task ActAndAssertNoRollbackAsync(CompletionFixture fx)
    {
        // Fixture capability check: this setup must genuinely be capable of triggering
        // the OLD rollback path — otherwise the preservation assertions prove nothing.
        var coderHistory = fx.AgentsManager.GetHistory(WorkerRole.Coder);
        Assert.NotEmpty(coderHistory);                          // rollback has a version to restore
        Assert.NotEqual(GoalPhase.Done, fx.Pipeline.Phase);     // completion is not a guarded no-op
        Assert.NotEqual("v001", fx.MetricsTracker.History[^1].AgentsMdVersions["coder"]); // v000 ≠ v001
        Assert.NotNull(fx.MetricsTracker);
        Assert.NotNull(fx.AgentsManager);

        var agentsBefore = SnapshotDirectory(fx.AgentsDir);
        var capturingLogger = new TestLogger<GoalLifecycleService>();
        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var service = new GoalLifecycleService(
            goalManager: fx.GoalManager,
            logger: capturingLogger,
            metricsTracker: fx.MetricsTracker,
            agentsManager: fx.AgentsManager,
            dashboardNotifier: notifier);

        await service.MarkGoalCompletedAsync(fx.Pipeline, TestContext.Current.CancellationToken);

        // Pipeline and registered goal both reach their terminal completed state.
        Assert.Equal(GoalPhase.Done, fx.Pipeline.Phase);
        Assert.Equal(GoalStatus.Completed, fx.Source.LastStatus);

        // The normal completion notification still occurs exactly once.
        Assert.Equal(1, notificationCount);

        // Current metrics were actually recorded and can be loaded back from disk.
        Assert.NotNull(fx.MetricsTracker.Latest);
        Assert.Equal(fx.CurrentTotalTests, fx.MetricsTracker.Latest!.TotalTests);
        Assert.Equal(fx.CurrentPassedTests, fx.MetricsTracker.Latest.PassedTests);
        Assert.Equal(fx.CurrentCoveragePercent, fx.MetricsTracker.Latest.CoveragePercent);
        Assert.Equal("v001", fx.MetricsTracker.Latest.AgentsMdVersions["coder"]);
        Assert.True(File.Exists(Path.Combine(fx.MetricsDir, "iteration-001.json")),
            "Expected the current iteration's metrics file on disk");

        var reloaded = new MetricsTracker(fx.MetricsDir, NullLogger<MetricsTracker>.Instance);
        Assert.Equal(2, reloaded.History.Count);
        Assert.Equal(fx.CurrentTotalTests, reloaded.History[^1].TotalTests);
        Assert.Equal("v001", reloaded.History[^1].AgentsMdVersions["coder"]);

        // Exact current instruction bytes AND archive filenames/bytes remain unchanged.
        Assert.Equal(agentsBefore, SnapshotDirectory(fx.AgentsDir));

        // No regression/rollback/comparison announcement is emitted (old-code wording).
        string[] forbiddenFragments =
        [
            "REGRESSION DETECTED",
            "rolling back AGENTS.md",
            "Rolled back",
            "nothing to rollback",
            "Metrics comparison",
        ];
        Assert.DoesNotContain(capturingLogger.LogEntries, e =>
            forbiddenFragments.Any(f => e.Message.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Builds the shared end-to-end completion fixture: a real <see cref="AgentsManager"/>
    /// with a genuine archived Coder instruction history, a real <see cref="MetricsTracker"/>
    /// holding an older iteration that recorded the Coder at v000 (unequal to the current
    /// v001 produced from the real archive), and a registered goal behind a not-yet-Done
    /// pipeline carrying the scenario's current metrics.
    /// </summary>
    private static async Task<CompletionFixture> CreateCompletionFixtureAsync(
        string agentsDir,
        string metricsDir,
        int previousTotalTests,
        int previousPassedTests,
        double previousCoveragePercent,
        int currentTotalTests,
        int currentPassedTests,
        double currentCoveragePercent)
    {
        Directory.CreateDirectory(metricsDir);

        // Real AgentsManager with a genuine archive: the second update archives the first
        // content as history/coder/v001.agents.md and installs the current instructions.
        var agentsManager = new AgentsManager(agentsDir, NullLogger<AgentsManager>.Instance);
        agentsManager.UpdateAgentsMd(WorkerRole.Coder, "prior coder guidance — archived version");
        agentsManager.UpdateAgentsMd(WorkerRole.Coder, "current coder guidance — live version");

        // Previous iteration's metrics: coder recorded at v000 (unequal to the current v001).
        var metricsTracker = new MetricsTracker(metricsDir, NullLogger<MetricsTracker>.Instance);
        metricsTracker.RecordIteration(new IterationMetrics
        {
            Iteration = 0,
            TotalTests = previousTotalTests,
            PassedTests = previousPassedTests,
            FailedTests = Math.Max(0, previousTotalTests - previousPassedTests),
            CoveragePercent = previousCoveragePercent,
            AgentsMdVersions = new Dictionary<string, string> { ["coder"] = "v000" },
        });

        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);
        pipeline.StateMachine.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var source = new RecordingGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(source);
        // Must call GetNextGoalAsync to register the goal in the manager
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        pipeline.Metrics.TotalTests = currentTotalTests;
        pipeline.Metrics.PassedTests = currentPassedTests;
        pipeline.Metrics.FailedTests = Math.Max(0, currentTotalTests - currentPassedTests);
        pipeline.Metrics.CoveragePercent = currentCoveragePercent;

        return new CompletionFixture(
            pipeline, source, goalManager, agentsManager, metricsTracker,
            agentsDir, metricsDir, currentTotalTests, currentPassedTests, currentCoveragePercent);
    }

    /// <summary>Byte-exact snapshot of every file under a directory, keyed by relative path.</summary>
    private static Dictionary<string, byte[]> SnapshotDirectory(string root)
    {
        var snapshot = new Dictionary<string, byte[]>();
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            snapshot[relative] = File.ReadAllBytes(file);
        }
        return snapshot;
    }

    private sealed record CompletionFixture(
        GoalPipeline Pipeline,
        RecordingGoalSource Source,
        GoalManager GoalManager,
        AgentsManager AgentsManager,
        MetricsTracker MetricsTracker,
        string AgentsDir,
        string MetricsDir,
        int CurrentTotalTests,
        int CurrentPassedTests,
        double CurrentCoveragePercent);

    /// <summary>
    /// Goal source that records the last status update so tests can assert the registered
    /// goal reached <see cref="GoalStatus.Completed"/>.
    /// </summary>
    private sealed class RecordingGoalSource(Goal goal) : IGoalSource
    {
        public GoalStatus? LastStatus { get; private set; }

        public string Name => "recording-goal-source";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            LastStatus = status;
            return Task.CompletedTask;
        }
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