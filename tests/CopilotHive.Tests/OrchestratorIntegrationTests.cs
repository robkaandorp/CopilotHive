using CopilotHive.Configuration;
using CopilotHive.Orchestration;
using CopilotHive.Tests.Fakes;
using CopilotHive.Workers;

namespace CopilotHive.Tests;

public class OrchestratorIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public OrchestratorIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-orch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        // Create agents directory with required AGENTS.md files
        var agentsDir = Path.Combine(_tempDir, "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "coder.agents.md"), "# Coder\nYou write code.");
        File.WriteAllText(Path.Combine(agentsDir, "tester.agents.md"), "# Tester\nYou test code.");
        File.WriteAllText(Path.Combine(agentsDir, "reviewer.agents.md"), "# Reviewer\nYou review code.");
    }

    private HiveConfiguration CreateConfig(int maxIterations = 1, int maxRetries = 0) => new()
    {
        Goal = "Write a hello world program",
        WorkspacePath = Path.Combine(_tempDir, "workspaces"),
        AgentsPath = Path.Combine(_tempDir, "agents"),
        MetricsPath = Path.Combine(_tempDir, "metrics"),
        MaxIterations = maxIterations,
        MaxRetriesPerTask = maxRetries,
        GitHubToken = "fake-token",
    };

    private const string ApproveReview = """
        REVIEW_REPORT:
        verdict: APPROVE
        issues_count: 0
        critical_issues: 0
        summary: Code looks good.
        issues:
        """;

    private const string PassingTestReport = """
        TEST_REPORT:
        build_success: true
        unit_tests_total: 5
        unit_tests_passed: 5
        integration_tests_total: 1
        integration_tests_passed: 1
        runtime_verified: true
        coverage_percent: 85
        verdict: PASS
        summary: All tests pass.
        issues:
        """;

    [Fact]
    public async Task FullIteration_CoderThenTester_SpawnsCorrectRoles()
    {
        var workerManager = new FakeWorkerManager();
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            if (prompt.Contains("You are working on this goal"))
                return "I wrote the code and unit tests. All committed.";
            if (prompt.Contains("reviewing code changes"))
                return ApproveReview;
            return PassingTestReport;
        });

        var config = CreateConfig();
        await using var orchestrator = new Orchestrator(config, workerManager, clientFactory);
        await orchestrator.RunAsync();

        Assert.Contains(workerManager.SpawnHistory, s => s.Role == WorkerRole.Coder);
        Assert.Contains(workerManager.SpawnHistory, s => s.Role == WorkerRole.Reviewer);
        Assert.Contains(workerManager.SpawnHistory, s => s.Role == WorkerRole.Tester);
    }

    [Fact]
    public async Task FullIteration_PassingTests_MergesToMain()
    {
        var workerManager = new FakeWorkerManager();
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            if (prompt.Contains("You are working on this goal"))
                return "Code written and committed.";
            if (prompt.Contains("reviewing code changes"))
                return ApproveReview;
            return PassingTestReport;
        });

        var config = CreateConfig();
        await using var orchestrator = new Orchestrator(config, workerManager, clientFactory);
        await orchestrator.RunAsync();

        // Coder(1) + Reviewer(1) + Tester(1) + Post-merge verifier(1) = 4 worker spawns
        Assert.Equal(4, workerManager.SpawnHistory.Count);
    }

    [Fact]
    public async Task FailingTests_TriggersRetryLoop()
    {
        var testCallCount = 0;
        var workerManager = new FakeWorkerManager();
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            if (prompt.Contains("You are working on this goal"))
                return "Code written.";
            if (prompt.Contains("reviewing code changes"))
                return ApproveReview;
            if (prompt.Contains("tester found issues") || prompt.Contains("reviewer found issues"))
                return "Fixed the bugs.";

            testCallCount++;
            // First test run fails, second passes
            if (testCallCount <= 1)
                return """
                    TEST_REPORT:
                    build_success: true
                    unit_tests_total: 5
                    unit_tests_passed: 3
                    integration_tests_total: 0
                    integration_tests_passed: 0
                    runtime_verified: false
                    coverage_percent: 60
                    verdict: FAIL
                    summary: Two tests failing, app crashes on startup.
                    issues:
                    - NullReferenceException in Program.Main
                    - Missing error handling in FileReader
                    """;

            return PassingTestReport;
        });

        var config = CreateConfig(maxRetries: 2);
        await using var orchestrator = new Orchestrator(config, workerManager, clientFactory);
        await orchestrator.RunAsync();

        // Should have: coder + reviewer + tester(fail) + coder(fix) + tester(pass) + post-merge-verify
        Assert.True(workerManager.SpawnHistory.Count >= 5);
    }

    [Fact]
    public async Task WorkersAreStoppedAfterUse()
    {
        var workerManager = new FakeWorkerManager();
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            if (prompt.Contains("reviewing code changes"))
                return ApproveReview;
            return PassingTestReport;
        });

        var config = CreateConfig();
        await using var orchestrator = new Orchestrator(config, workerManager, clientFactory);
        await orchestrator.RunAsync();

        // All spawned workers should have been stopped
        Assert.Empty(workerManager.Workers);
        Assert.Equal(workerManager.SpawnHistory.Count, workerManager.StopHistory.Count);
    }

    [Fact]
    public async Task ParseTestReport_ExtractsAllFields()
    {
        var workerManager = new FakeWorkerManager();
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            if (prompt.Contains("You are working on this goal"))
                return "Code done.";
            if (prompt.Contains("reviewing code changes"))
                return ApproveReview;

            return """
                Some preamble text here.

                TEST_REPORT:
                build_success: true
                unit_tests_total: 12
                unit_tests_passed: 10
                integration_tests_total: 3
                integration_tests_passed: 3
                runtime_verified: true
                coverage_percent: 78.5
                verdict: PARTIAL
                summary: Build works, most tests pass, but 2 unit tests failing.
                issues:
                - TimeoutException in async test
                - Off-by-one error in pagination
                """;
        });

        var config = CreateConfig();
        await using var orchestrator = new Orchestrator(config, workerManager, clientFactory);
        await orchestrator.RunAsync();

        // Verify metrics were recorded — check the metrics file
        var metricsDir = Path.Combine(_tempDir, "metrics");
        Assert.True(Directory.Exists(metricsDir));
        var metricsFiles = Directory.GetFiles(metricsDir, "*.json");
        Assert.NotEmpty(metricsFiles);

        var json = await File.ReadAllTextAsync(metricsFiles[0]);
        Assert.Contains("\"totalTests\": 12", json);
        Assert.Contains("\"passedTests\": 10", json);
        Assert.Contains("\"integrationTestsTotal\": 3", json);
        Assert.Contains("\"runtimeVerified\": true", json);
        Assert.Contains("\"verdict\": \"PARTIAL\"", json);
    }

    [Fact]
    public async Task MultipleIterations_StopsOnAllTestsPassing()
    {
        var iterationCount = 0;
        var workerManager = new FakeWorkerManager();
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            if (prompt.Contains("You are working on this goal"))
            {
                iterationCount++;
                return $"Iteration {iterationCount} code.";
            }
            if (prompt.Contains("reviewing code changes"))
                return ApproveReview;
            return PassingTestReport;
        });

        var config = CreateConfig(maxIterations: 5);
        await using var orchestrator = new Orchestrator(config, workerManager, clientFactory);
        await orchestrator.RunAsync();

        // Should stop after first iteration since all tests pass
        Assert.Equal(1, iterationCount);
    }

    [Fact]
    public async Task BackwardsCompatible_OldMetricsFormat_StillParsed()
    {
        var workerManager = new FakeWorkerManager();
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            if (prompt.Contains("You are working on this goal"))
                return "Done.";
            if (prompt.Contains("reviewing code changes"))
                return ApproveReview;

            // Old format without TEST_REPORT block
            return """
                METRICS:
                total_tests: 8
                passed_tests: 8
                failed_tests: 0
                coverage_percent: 75
                """;
        });

        var config = CreateConfig();
        await using var orchestrator = new Orchestrator(config, workerManager, clientFactory);
        await orchestrator.RunAsync();

        // Should still parse and record — verify no crash
        var metricsFiles = Directory.GetFiles(Path.Combine(_tempDir, "metrics"), "*.json");
        Assert.NotEmpty(metricsFiles);

        var json = await File.ReadAllTextAsync(metricsFiles[0]);
        Assert.Contains("\"totalTests\": 8", json);
        Assert.Contains("\"passedTests\": 8", json);
    }

    [Fact]
    public async Task ClientFactory_CreatesClientPerWorker()
    {
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            if (prompt.Contains("reviewing code changes"))
                return ApproveReview;
            return PassingTestReport;
        });

        var config = CreateConfig();
        await using var orchestrator = new Orchestrator(config, new FakeWorkerManager(), clientFactory);
        await orchestrator.RunAsync();

        // Each worker gets its own client (coder + reviewer + tester + post-merge verifier = 4)
        Assert.Equal(4, clientFactory.CreatedClients.Count);
        Assert.All(clientFactory.CreatedClients, c => Assert.True(c.Connected));
        Assert.All(clientFactory.CreatedClients, c => Assert.True(c.Disposed));
    }

    [Fact]
    public async Task PostMergeVerificationFail_RevertsAndSendsToCoderForFix()
    {
        var prompts = new List<string>();
        var workerManager = new FakeWorkerManager();
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            prompts.Add(prompt);

            // Phase 1: Coder writes code
            if (prompt.Contains("You are working on this goal"))
                return "Code written and committed.";

            // Phase 1.5: Reviewer approves
            if (prompt.Contains("reviewing code changes"))
                return ApproveReview;

            // Phase 2: Pre-merge tester passes
            if (prompt.Contains("You are testing code for this goal"))
                return """
                    TEST_REPORT:
                    build_success: true
                    unit_tests_total: 5
                    unit_tests_passed: 5
                    integration_tests_total: 0
                    integration_tests_passed: 0
                    runtime_verified: true
                    coverage_percent: 80
                    verdict: PASS
                    summary: All tests pass.
                    issues:
                    """;

            // Phase 3b: Post-merge verifier FAILS
            if (prompt.Contains("verifying the merged code"))
                return """
                    TEST_REPORT:
                    build_success: true
                    unit_tests_total: 5
                    unit_tests_passed: 3
                    integration_tests_total: 0
                    integration_tests_passed: 0
                    runtime_verified: false
                    coverage_percent: 60
                    verdict: FAIL
                    summary: Merge broke two tests.
                    issues:
                    - Conflicting method signature after merge
                    """;

            // Phase 3c: Coder fixes post-merge issue
            if (prompt.Contains("FAILED after merging to main"))
                return "Fixed the merge issues.";

            // Phase 3d: Re-test passes
            if (prompt.Contains("re-testing code after a post-merge fix"))
                return """
                    TEST_REPORT:
                    build_success: true
                    unit_tests_total: 5
                    unit_tests_passed: 5
                    integration_tests_total: 0
                    integration_tests_passed: 0
                    runtime_verified: true
                    coverage_percent: 85
                    verdict: PASS
                    summary: All fixed.
                    issues:
                    """;

            return "Unknown prompt.";
        });

        var config = CreateConfig();
        await using var orchestrator = new Orchestrator(config, workerManager, clientFactory);
        await orchestrator.RunAsync();

        // Should have: coder + tester + post-merge-verify(fail) + coder-fix + retest + (implicit re-merge)
        Assert.True(workerManager.SpawnHistory.Count >= 5,
            $"Expected >= 5 workers, got {workerManager.SpawnHistory.Count}");

        // Coder received post-merge failure feedback
        Assert.Contains(prompts, p => p.Contains("FAILED after merging to main"));

        // Re-test was triggered
        Assert.Contains(prompts, p => p.Contains("re-testing code after a post-merge fix"));
    }

    [Fact]
    public async Task ReviewRequestsChanges_SendsFeedbackToCoder()
    {
        var prompts = new List<string>();
        var reviewCallCount = 0;
        var workerManager = new FakeWorkerManager();
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            prompts.Add(prompt);

            if (prompt.Contains("You are working on this goal"))
                return "Code written.";

            if (prompt.Contains("reviewing code changes"))
            {
                reviewCallCount++;
                // First review rejects, second approves
                if (reviewCallCount <= 1)
                    return """
                        REVIEW_REPORT:
                        verdict: REQUEST_CHANGES
                        issues_count: 2
                        critical_issues: 1
                        summary: Found a null reference bug and missing validation.
                        issues:
                        - [CRITICAL] Null reference in ParseConfig when config file missing
                        - [MAJOR] No input validation on user-supplied paths
                        """;
                return ApproveReview;
            }

            // Coder fixes review issues
            if (prompt.Contains("reviewer found issues"))
                return "Fixed the review issues.";

            return PassingTestReport;
        });

        var config = CreateConfig(maxRetries: 1);
        await using var orchestrator = new Orchestrator(config, workerManager, clientFactory);
        await orchestrator.RunAsync();

        // Coder received review feedback
        Assert.Contains(prompts, p => p.Contains("reviewer found issues"));

        // Reviewer was spawned at least twice (initial + re-review)
        Assert.True(workerManager.SpawnHistory.Count(s => s.Role == WorkerRole.Reviewer) >= 2);
    }

    [Fact]
    public async Task PerRoleModels_CorrectModelPassedToEachWorker()
    {
        var workerManager = new FakeWorkerManager();
        var clientFactory = new FakeCopilotClientFactory(prompt =>
        {
            if (prompt.Contains("You are working on this goal"))
                return "Code written.";
            if (prompt.Contains("reviewing code changes"))
                return ApproveReview;
            return PassingTestReport;
        });

        var config = new HiveConfiguration
        {
            Goal = "Write hello world",
            WorkspacePath = Path.Combine(_tempDir, "workspaces"),
            AgentsPath = Path.Combine(_tempDir, "agents"),
            MetricsPath = Path.Combine(_tempDir, "metrics"),
            MaxIterations = 1,
            MaxRetriesPerTask = 0,
            GitHubToken = "fake-token",
            CoderModel = "test-coder-model",
            ReviewerModel = "test-reviewer-model",
            TesterModel = "test-tester-model",
        };

        await using var orchestrator = new Orchestrator(config, workerManager, clientFactory);
        await orchestrator.RunAsync();

        var coderSpawn = workerManager.SpawnHistory.First(s => s.Role == WorkerRole.Coder);
        var reviewerSpawn = workerManager.SpawnHistory.First(s => s.Role == WorkerRole.Reviewer);
        var testerSpawn = workerManager.SpawnHistory.First(s => s.Role == WorkerRole.Tester);

        Assert.Equal("test-coder-model", coderSpawn.Model);
        Assert.Equal("test-reviewer-model", reviewerSpawn.Model);
        Assert.Equal("test-tester-model", testerSpawn.Model);
    }

    [Fact]
    public void ParseReviewReport_ExtractsApproveVerdict()
    {
        var metrics = new CopilotHive.Metrics.IterationMetrics();
        var verdict = Orchestrator.ParseReviewReport("""
            Some analysis text...

            REVIEW_REPORT:
            verdict: APPROVE
            issues_count: 0
            critical_issues: 0
            summary: Code looks good, well-structured.
            issues:
            """, metrics);

        Assert.Equal("APPROVE", verdict);
        Assert.Equal("APPROVE", metrics.ReviewVerdict);
        Assert.Equal(0, metrics.ReviewIssuesFound);
        Assert.Empty(metrics.ReviewIssues);
    }

    [Fact]
    public void ParseReviewReport_ExtractsRequestChangesWithIssues()
    {
        var metrics = new CopilotHive.Metrics.IterationMetrics();
        var verdict = Orchestrator.ParseReviewReport("""
            REVIEW_REPORT:
            verdict: REQUEST_CHANGES
            issues_count: 3
            critical_issues: 1
            summary: Several issues found.
            issues:
            - [CRITICAL] SQL injection vulnerability
            - [MAJOR] Missing error handling
            - [MINOR] Inconsistent naming
            """, metrics);

        Assert.Equal("REQUEST_CHANGES", verdict);
        Assert.Equal(3, metrics.ReviewIssuesFound);
        Assert.Equal(3, metrics.ReviewIssues.Count);
        Assert.Contains(metrics.ReviewIssues, i => i.Contains("SQL injection"));
    }

    [Fact]
    public void ParseReviewReport_EmptyResponse_ReturnsEmptyVerdict()
    {
        var metrics = new CopilotHive.Metrics.IterationMetrics();
        var verdict = Orchestrator.ParseReviewReport("No review report here.", metrics);

        Assert.Equal("", verdict);
    }

    public void Dispose()
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (!Directory.Exists(_tempDir)) return;
                foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_tempDir, recursive: true);
                return;
            }
            catch (Exception) when (i < 4)
            {
                Thread.Sleep(200 * (i + 1));
            }
        }
    }
}
