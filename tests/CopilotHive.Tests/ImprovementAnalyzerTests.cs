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
        Assert.Contains("Retry count: 2", prompt);
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
}
