using CopilotHive.Improvement;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;

namespace CopilotHive.Tests;

public class ImprovementAnalyzerTests
{
    private readonly ImprovementAnalyzer _analyzer = new();

    // -------------------------------------------------------------------------
    // ShouldImprove
    // -------------------------------------------------------------------------

    [Fact]
    public void ShouldImprove_CleanPass_ReturnsFalse()
    {
        var metrics = new IterationMetrics
        {
            Verdict = "PASS",
            TotalTests = 10,
            PassedTests = 10,
            FailedTests = 0,
            RetryCount = 0,
        };

        Assert.False(_analyzer.ShouldImprove(metrics));
    }

    [Fact]
    public void ShouldImprove_FailVerdict_ReturnsTrue()
    {
        var metrics = new IterationMetrics { Verdict = "FAIL" };
        Assert.True(_analyzer.ShouldImprove(metrics));
    }

    [Fact]
    public void ShouldImprove_PartialVerdict_ReturnsTrue()
    {
        var metrics = new IterationMetrics { Verdict = "PARTIAL" };
        Assert.True(_analyzer.ShouldImprove(metrics));
    }

    [Fact]
    public void ShouldImprove_PassWithRetries_ReturnsTrue()
    {
        var metrics = new IterationMetrics
        {
            Verdict = "PASS",
            RetryCount = 1,
        };
        Assert.True(_analyzer.ShouldImprove(metrics));
    }

    [Fact]
    public void ShouldImprove_PassWithIssues_ReturnsTrue()
    {
        var metrics = new IterationMetrics { Verdict = "PASS" };
        metrics.Issues.Add("Minor formatting issue");
        Assert.True(_analyzer.ShouldImprove(metrics));
    }

    [Fact]
    public void ShouldImprove_PassWithFailedTests_ReturnsTrue()
    {
        var metrics = new IterationMetrics
        {
            Verdict = "PASS",
            FailedTests = 1,
        };
        Assert.True(_analyzer.ShouldImprove(metrics));
    }

    // -------------------------------------------------------------------------
    // BuildRequests
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRequests_ReturnsOnePerRole()
    {
        var metrics = new IterationMetrics { Verdict = "FAIL", Iteration = 1 };
        var agentsMd = new Dictionary<string, string>
        {
            ["coder"] = "# Coder instructions",
            ["tester"] = "# Tester instructions",
        };

        var requests = _analyzer.BuildRequests(metrics, [], agentsMd);

        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, r => r.Role == "coder");
        Assert.Contains(requests, r => r.Role == "tester");
    }

    [Fact]
    public void BuildRequests_IncludesMetricsInPrompt()
    {
        var metrics = new IterationMetrics
        {
            Iteration = 3,
            Verdict = "FAIL",
            TotalTests = 10,
            PassedTests = 7,
            FailedTests = 3,
            RetryCount = 2,
        };
        metrics.Issues.Add("NullReferenceException in Parser");

        var agentsMd = new Dictionary<string, string>
        {
            ["coder"] = "# Coder instructions",
        };

        var requests = _analyzer.BuildRequests(metrics, [], agentsMd);

        var prompt = requests[0].Prompt;
        Assert.Contains("Iteration: 3", prompt);
        Assert.Contains("Verdict: FAIL", prompt);
        Assert.Contains("7/10", prompt);
        Assert.Contains("Total retries: 2", prompt);
        Assert.Contains("NullReferenceException in Parser", prompt);
    }

    [Fact]
    public void BuildAnalysis_IncludesHistoryTrends()
    {
        var current = new IterationMetrics { Iteration = 3, Verdict = "FAIL" };
        var history = new List<IterationMetrics>
        {
            new() { Iteration = 1, Verdict = "FAIL", PassedTests = 5, TotalTests = 10, RetryCount = 3, CoveragePercent = 40 },
            new() { Iteration = 2, Verdict = "PARTIAL", PassedTests = 8, TotalTests = 10, RetryCount = 1, CoveragePercent = 60 },
            current,
        };

        var analysis = _analyzer.BuildAnalysis(current, history);

        Assert.Contains("Metrics Trend", analysis);
        Assert.Contains("Iteration 1: FAIL", analysis);
        Assert.Contains("Iteration 2: PARTIAL", analysis);
    }

    [Fact]
    public void BuildAnalysis_IdentifiesRecurringIssues()
    {
        var issue = "Format mismatch in TEST_REPORT";
        var current = new IterationMetrics { Iteration = 3, Verdict = "FAIL" };
        current.Issues.Add(issue);

        var prev = new IterationMetrics { Iteration = 2, Verdict = "FAIL" };
        prev.Issues.Add(issue);

        var history = new List<IterationMetrics> { prev, current };

        var analysis = _analyzer.BuildAnalysis(current, history);

        Assert.Contains("Recurring Issues", analysis);
        Assert.Contains(issue, analysis);
    }

    // -------------------------------------------------------------------------
    // ParseImproverResponse
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseImproverResponse_ExtractsImprovedContent()
    {
        var response = """
            Here's my analysis...

            === IMPROVED coder.agents.md ===
            # Coder — Improved
            Be more careful with null checks.
            === END coder.agents.md ===

            === UNCHANGED tester.agents.md ===
            """;

        var result = Orchestrator.ParseImproverResponse(response);

        Assert.Single(result);
        Assert.True(result.ContainsKey("coder"));
        Assert.Contains("Be more careful with null checks", result["coder"]);
    }

    [Fact]
    public void ParseImproverResponse_MultipleRoles()
    {
        var response = """
            === IMPROVED coder.agents.md ===
            # Coder v2
            === END coder.agents.md ===

            === IMPROVED tester.agents.md ===
            # Tester v2
            === END tester.agents.md ===
            """;

        var result = Orchestrator.ParseImproverResponse(response);

        Assert.Equal(2, result.Count);
        Assert.Contains("Coder v2", result["coder"]);
        Assert.Contains("Tester v2", result["tester"]);
    }

    [Fact]
    public void ParseImproverResponse_EmptyInput_ReturnsEmpty()
    {
        var result = Orchestrator.ParseImproverResponse("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseImproverResponse_NoMarkers_ReturnsEmpty()
    {
        var result = Orchestrator.ParseImproverResponse("Just some random text without markers.");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseImproverResponse_SkipsUnchangedRoles()
    {
        var response = """
            === UNCHANGED coder.agents.md ===
            === IMPROVED tester.agents.md ===
            # Updated tester
            === END tester.agents.md ===
            """;

        var result = Orchestrator.ParseImproverResponse(response);

        Assert.Single(result);
        Assert.False(result.ContainsKey("coder"));
        Assert.True(result.ContainsKey("tester"));
    }

    [Fact]
    public void ValidateImprovement_AcceptsValidImprovement()
    {
        var original = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"Line {i}"));
        var improved = string.Join('\n', Enumerable.Range(1, 22).Select(i => $"Line {i}"));

        Assert.True(Orchestrator.ValidateImprovement(original, improved, "coder"));
    }

    [Fact]
    public void ValidateImprovement_RejectsTooFewLines()
    {
        var original = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"Line {i}"));
        var improved = "# Coder\nSummary only.";

        Assert.False(Orchestrator.ValidateImprovement(original, improved, "coder"));
    }

    [Fact]
    public void ValidateImprovement_RejectsTooShortRelativeToOriginal()
    {
        var original = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"Line {i}"));
        // 10 lines = 25% of 40 — should be rejected (<50%)
        var improved = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"Line {i}"));

        Assert.False(Orchestrator.ValidateImprovement(original, improved, "coder"));
    }

    [Fact]
    public void ValidateImprovement_AcceptsLongerThanOriginal()
    {
        var original = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"Line {i}"));
        var improved = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"Line {i}"));

        Assert.True(Orchestrator.ValidateImprovement(original, improved, "coder"));
    }

    [Fact]
    public void ValidateImprovement_AcceptsExactlyHalfLength()
    {
        var original = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"Line {i}"));
        var improved = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"Line {i}"));

        Assert.True(Orchestrator.ValidateImprovement(original, improved, "coder"));
    }

    // -------------------------------------------------------------------------
    // AnalyzeRole / GetRolePriorities
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeRole_SingleRoleAttribution_ReviewerSignals()
    {
        var current = new IterationMetrics
        {
            Verdict = "FAIL",
            ReviewIssuesFound = 3,
            ReviewRetryCount = 1,
            BuildSuccess = true,
            FailedTests = 0,
            TestRetryCount = 0,
        };
        var history = new List<IterationMetrics>();

        var reviewer = _analyzer.AnalyzeRole("reviewer", current, history);
        var coder = _analyzer.AnalyzeRole("coder", current, history);
        var tester = _analyzer.AnalyzeRole("tester", current, history);

        Assert.True(reviewer.ConfidenceScore >= 50);
        Assert.True(reviewer.ShouldImprove);
        Assert.True(coder.ConfidenceScore < 50);
        Assert.True(tester.ConfidenceScore < 50);
    }

    [Fact]
    public void AnalyzeRole_MultiRoleAttribution_ReviewerAndTesterSignals()
    {
        var current = new IterationMetrics
        {
            Verdict = "FAIL",
            ReviewIssuesFound = 2,
            ReviewRetryCount = 1,
            FailedTests = 4,
            TestRetryCount = 2,
            BuildSuccess = true,
        };
        var history = new List<IterationMetrics>();

        var reviewer = _analyzer.AnalyzeRole("reviewer", current, history);
        var tester = _analyzer.AnalyzeRole("tester", current, history);
        var coder = _analyzer.AnalyzeRole("coder", current, history);

        Assert.True(reviewer.ConfidenceScore >= 50);
        Assert.True(tester.ConfidenceScore >= 50);
        Assert.True(coder.ConfidenceScore < 50);
    }

    [Fact]
    public void AnalyzeRole_EmptyHistory_OnlyCurrentSignalsAffectScore()
    {
        var current = new IterationMetrics
        {
            ReviewIssuesFound = 1,
            ReviewRetryCount = 1,
            BuildSuccess = true,
        };

        var withHistory = _analyzer.AnalyzeRole("reviewer", current, new List<IterationMetrics>
        {
            new() { ReviewIssuesFound = 1, ReviewRetryCount = 1 },
            new() { ReviewIssuesFound = 2, ReviewRetryCount = 1 },
        });
        var withoutHistory = _analyzer.AnalyzeRole("reviewer", current, new List<IterationMetrics>());

        // Without history only current signals (50 pts); with history score should be higher
        Assert.Equal(50, withoutHistory.ConfidenceScore);
        Assert.True(withHistory.ConfidenceScore > withoutHistory.ConfidenceScore);
    }

    [Fact]
    public void AnalyzeRole_NoIssues_AllRolesScoreZeroAndShouldNotImprove()
    {
        var current = new IterationMetrics
        {
            Verdict = "PASS",
            BuildSuccess = true,
            FailedTests = 0,
            TestRetryCount = 0,
            ReviewIssuesFound = 0,
            ReviewRetryCount = 0,
        };
        var history = new List<IterationMetrics>();

        foreach (var role in new[] { "coder", "reviewer", "tester" })
        {
            var rec = _analyzer.AnalyzeRole(role, current, history);
            Assert.Equal(0, rec.ConfidenceScore);
            Assert.False(rec.ShouldImprove);
        }
    }

    [Fact]
    public void AnalyzeRole_RecurringIssuesIncreaseConfidence()
    {
        var current = new IterationMetrics { FailedTests = 2, TestRetryCount = 1, BuildSuccess = true };
        var historicalIteration = new IterationMetrics { FailedTests = 3, TestRetryCount = 1 };
        var history = new List<IterationMetrics> { historicalIteration, historicalIteration };

        var withHistory = _analyzer.AnalyzeRole("tester", current, history);
        var withoutHistory = _analyzer.AnalyzeRole("tester", current, new List<IterationMetrics>());

        Assert.True(withHistory.ConfidenceScore > withoutHistory.ConfidenceScore);
    }

    [Fact]
    public void GetRolePriorities_ReturnsSortedByConfidenceDescendingThenAlphabetical()
    {
        var current = new IterationMetrics
        {
            ReviewIssuesFound = 2,
            ReviewRetryCount = 1,
            BuildSuccess = true,
        };

        var priorities = _analyzer.GetRolePriorities(current, new List<IterationMetrics>());

        Assert.Equal(3, priorities.Count);
        // reviewer should be first (highest score)
        Assert.Equal("reviewer", priorities[0].Role);
        // remaining two should be in alphabetical order (coder before tester, both at 0)
        Assert.Equal("coder", priorities[1].Role);
        Assert.Equal("tester", priorities[2].Role);
        // descending order check
        Assert.True(priorities[0].ConfidenceScore >= priorities[1].ConfidenceScore);
        Assert.True(priorities[1].ConfidenceScore >= priorities[2].ConfidenceScore);
    }
}
