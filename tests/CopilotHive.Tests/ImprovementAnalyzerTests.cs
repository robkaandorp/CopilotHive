using CopilotHive.Improvement;
using CopilotHive.Metrics;
using CopilotHive.Workers;

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
            Verdict = TaskVerdict.Pass,
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
        var metrics = new IterationMetrics { Verdict = TaskVerdict.Fail };
        Assert.True(_analyzer.ShouldImprove(metrics));
    }

    [Fact]
    public void ShouldImprove_PartialVerdict_ReturnsTrue()
    {
        var metrics = new IterationMetrics { Verdict = TaskVerdict.Partial };
        Assert.True(_analyzer.ShouldImprove(metrics));
    }

    [Fact]
    public void ShouldImprove_PassWithRetries_ReturnsTrue()
    {
        var metrics = new IterationMetrics
        {
            Verdict = TaskVerdict.Pass,
            RetryCount = 1,
        };
        Assert.True(_analyzer.ShouldImprove(metrics));
    }

    [Fact]
    public void ShouldImprove_PassWithIssues_ReturnsTrue()
    {
        var metrics = new IterationMetrics { Verdict = TaskVerdict.Pass };
        metrics.Issues.Add("Some issue found");
        Assert.True(_analyzer.ShouldImprove(metrics));
    }

    [Fact]
    public void ShouldImprove_PassWithFailedTests_ReturnsTrue()
    {
        var metrics = new IterationMetrics
        {
            Verdict = TaskVerdict.Pass,
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
        var metrics = new IterationMetrics { Verdict = TaskVerdict.Fail, Iteration = 1 };
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
            Verdict = TaskVerdict.Fail,
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
        Assert.Contains("Verdict: Fail", prompt);
        Assert.Contains("7/10", prompt);
        Assert.Contains("Total retries: 2", prompt);
        Assert.Contains("NullReferenceException in Parser", prompt);
    }

    [Fact]
    public void BuildAnalysis_IncludesHistoryTrends()
    {
        var current = new IterationMetrics { Iteration = 3, Verdict = TaskVerdict.Fail };
        var history = new List<IterationMetrics>
        {
            new() { Iteration = 1, Verdict = TaskVerdict.Fail, PassedTests = 5, TotalTests = 10, RetryCount = 3, CoveragePercent = 40 },
            new() { Iteration = 2, Verdict = TaskVerdict.Partial, PassedTests = 8, TotalTests = 10, RetryCount = 1, CoveragePercent = 60 },
            current,
        };

        var analysis = _analyzer.BuildAnalysis(current, history);

        Assert.Contains("Metrics Trend", analysis);
        Assert.Contains("Iteration 1: Fail", analysis);
        Assert.Contains("Iteration 2: Partial", analysis);
    }

    [Fact]
    public void BuildAnalysis_IdentifiesRecurringIssues()
    {
        var issue = "Format mismatch in TEST_REPORT";
        var current = new IterationMetrics { Iteration = 3, Verdict = TaskVerdict.Fail };
        current.Issues.Add(issue);

        var prev = new IterationMetrics { Iteration = 2, Verdict = TaskVerdict.Fail };
        prev.Issues.Add(issue);

        var history = new List<IterationMetrics> { prev, current };

        var analysis = _analyzer.BuildAnalysis(current, history);

        Assert.Contains("Recurring Issues", analysis);
        Assert.Contains(issue, analysis);
    }

    // -------------------------------------------------------------------------
    // AnalyzeRole / GetRolePriorities
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeRole_SingleRoleAttribution_ReviewerSignals()
    {
        var current = new IterationMetrics
        {
            Verdict = TaskVerdict.Fail,
            ReviewRetryCount = 1,
            ReviewIssuesFound = 2,
            BuildSuccess = true,
            FailedTests = 0,
            TestRetryCount = 0,
        };
        var history = new List<IterationMetrics>();

        var reviewer = _analyzer.AnalyzeRole(WorkerRole.Reviewer, current, history);
        var coder = _analyzer.AnalyzeRole(WorkerRole.Coder, current, history);
        var tester = _analyzer.AnalyzeRole(WorkerRole.Tester, current, history);

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
            Verdict = TaskVerdict.Fail,
            ReviewRetryCount = 1,
            ReviewIssuesFound = 2,
            FailedTests = 4,
            TestRetryCount = 2,
            BuildSuccess = true,
        };
        var history = new List<IterationMetrics>();

        var reviewer = _analyzer.AnalyzeRole(WorkerRole.Reviewer, current, history);
        var tester = _analyzer.AnalyzeRole(WorkerRole.Tester, current, history);
        var coder = _analyzer.AnalyzeRole(WorkerRole.Coder, current, history);

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

        var withHistory = _analyzer.AnalyzeRole(WorkerRole.Reviewer, current, new List<IterationMetrics>
        {
            new() { ReviewIssuesFound = 1, ReviewRetryCount = 1 },
            new() { ReviewIssuesFound = 2, ReviewRetryCount = 1 },
        });
        var withoutHistory = _analyzer.AnalyzeRole(WorkerRole.Reviewer, current, new List<IterationMetrics>());

        // Without history only current signals (50 pts); with history score should be higher
        Assert.Equal(50, withoutHistory.ConfidenceScore);
        Assert.True(withHistory.ConfidenceScore > withoutHistory.ConfidenceScore);
    }

    [Fact]
    public void AnalyzeRole_NoIssues_AllRolesScoreZeroAndShouldNotImprove()
    {
        var current = new IterationMetrics
        {
            Verdict = TaskVerdict.Pass,
            BuildSuccess = true,
            TestRetryCount = 0,
            ReviewIssuesFound = 0,
            ReviewRetryCount = 0,
        };
        var history = new List<IterationMetrics>();

        foreach (var role in new[] { WorkerRole.Coder, WorkerRole.Reviewer, WorkerRole.Tester })
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

        var withHistory = _analyzer.AnalyzeRole(WorkerRole.Tester, current, history);
        var withoutHistory = _analyzer.AnalyzeRole(WorkerRole.Tester, current, new List<IterationMetrics>());

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

        Assert.Equal(6, priorities.Count);
        // reviewer should be first (highest score)
        Assert.Equal(WorkerRole.Reviewer, priorities[0].Role);
        // all results should be in descending score order, ties broken alphabetically
        for (int i = 1; i < priorities.Count; i++)
        {
            Assert.True(priorities[i - 1].ConfidenceScore >= priorities[i].ConfidenceScore);
            if (priorities[i - 1].ConfidenceScore == priorities[i].ConfidenceScore)
                Assert.True(priorities[i - 1].Role.CompareTo(priorities[i].Role) <= 0);
        }
    }
}
