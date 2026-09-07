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