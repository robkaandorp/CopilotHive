using CopilotHive.Metrics;
using CopilotHive.Orchestration;

namespace CopilotHive.Tests;

public class ParseTestReportTests
{
    private static IterationMetrics Parse(string response)
    {
        var metrics = new IterationMetrics();
        Orchestrator.ParseTestReport(response, metrics);
        return metrics;
    }

    [Fact]
    public void ValidPassReport_ParsesAllFields()
    {
        const string report = """
            Some preamble from the tester.

            TEST_REPORT:
            build_success: true
            unit_tests_total: 10
            unit_tests_passed: 10
            integration_tests_total: 3
            integration_tests_passed: 3
            runtime_verified: true
            coverage_percent: 92.5
            verdict: PASS
            summary: All tests pass, no issues found.
            issues:
            """;

        var metrics = Parse(report);

        Assert.True(metrics.BuildSuccess);
        Assert.Equal(10, metrics.TotalTests);
        Assert.Equal(10, metrics.PassedTests);
        Assert.Equal(0, metrics.FailedTests);
        Assert.Equal(3, metrics.IntegrationTestsTotal);
        Assert.Equal(3, metrics.IntegrationTestsPassed);
        Assert.True(metrics.RuntimeVerified);
        Assert.Equal(92.5, metrics.CoveragePercent);
        Assert.Equal("PASS", metrics.Verdict);
        Assert.Equal("All tests pass, no issues found.", metrics.TestReportSummary);
        Assert.Empty(metrics.Issues);
    }

    [Fact]
    public void ValidFailReport_ParsesIssues()
    {
        const string report = """
            TEST_REPORT:
            build_success: true
            unit_tests_total: 8
            unit_tests_passed: 5
            integration_tests_total: 2
            integration_tests_passed: 1
            runtime_verified: false
            coverage_percent: 61.0
            verdict: FAIL
            summary: Three unit tests failing, runtime crash on startup.
            issues:
            - NullReferenceException in Program.Main
            - Missing error handling in FileReader
            - Integration test timeout after 5s
            """;

        var metrics = Parse(report);

        Assert.True(metrics.BuildSuccess);
        Assert.Equal(8, metrics.TotalTests);
        Assert.Equal(5, metrics.PassedTests);
        Assert.Equal(3, metrics.FailedTests);
        Assert.Equal(2, metrics.IntegrationTestsTotal);
        Assert.Equal(1, metrics.IntegrationTestsPassed);
        Assert.False(metrics.RuntimeVerified);
        Assert.Equal(61.0, metrics.CoveragePercent);
        Assert.Equal("FAIL", metrics.Verdict);
        Assert.Equal("Three unit tests failing, runtime crash on startup.", metrics.TestReportSummary);
        Assert.Equal(3, metrics.Issues.Count);
        Assert.Contains("NullReferenceException in Program.Main", metrics.Issues);
        Assert.Contains("Missing error handling in FileReader", metrics.Issues);
        Assert.Contains("Integration test timeout after 5s", metrics.Issues);
    }

    [Fact]
    public void OldMetricsFormat_BackwardsCompatible()
    {
        const string report = """
            METRICS:
            total_tests: 6
            passed_tests: 6
            failed_tests: 0
            coverage_percent: 80
            """;

        var metrics = Parse(report);

        Assert.Equal(6, metrics.TotalTests);
        Assert.Equal(6, metrics.PassedTests);
        Assert.Equal(0, metrics.FailedTests);
        Assert.Equal(80.0, metrics.CoveragePercent);
    }

    [Fact]
    public void PartialOrMalformedInput_ParsesWhatIsPossible()
    {
        // Missing several fields, has an unrecognised key, coverage uses comma decimal (should not parse)
        const string report = """
            TEST_REPORT:
            build_success: true
            unit_tests_total: 4
            verdict: PARTIAL
            unrecognised_key: some value
            coverage_percent: not-a-number
            """;

        var metrics = Parse(report);

        Assert.True(metrics.BuildSuccess);
        Assert.Equal(4, metrics.TotalTests);
        Assert.Equal("PARTIAL", metrics.Verdict);
        // Coverage should stay at default because value is not parseable
        Assert.Equal(0.0, metrics.CoveragePercent);
        // Unset fields keep their defaults
        Assert.Equal(0, metrics.PassedTests);
        Assert.False(metrics.RuntimeVerified);
        Assert.Empty(metrics.Issues);
    }

    [Fact]
    public void EmptyInput_LeavesMetricsAtDefaults()
    {
        var metrics = Parse(string.Empty);

        Assert.False(metrics.BuildSuccess);
        Assert.Equal(0, metrics.TotalTests);
        Assert.Equal(0, metrics.PassedTests);
        Assert.Equal(0, metrics.FailedTests);
        Assert.Equal(0, metrics.IntegrationTestsTotal);
        Assert.Equal(0, metrics.IntegrationTestsPassed);
        Assert.False(metrics.RuntimeVerified);
        Assert.Equal(0.0, metrics.CoveragePercent);
        Assert.Equal("", metrics.Verdict);
        Assert.Equal("", metrics.TestReportSummary);
        Assert.Empty(metrics.Issues);
    }

    [Fact]
    public void StatusFieldAlias_MapsToVerdict()
    {
        const string report = """
            TEST_REPORT:
            build_success: true
            unit_tests_total: 2
            unit_tests_passed: 2
            status: PASS
            """;

        var metrics = Parse(report);

        Assert.Equal("PASS", metrics.Verdict);
    }

    [Fact]
    public void ShortFormAliases_Parsed()
    {
        const string report = """
            TEST_REPORT:
            total: 5
            passed: 4
            failed: 1
            """;

        var metrics = Parse(report);

        Assert.Equal(5, metrics.TotalTests);
        Assert.Equal(4, metrics.PassedTests);
        Assert.Equal(1, metrics.FailedTests);
    }

    [Fact]
    public void TextBeforeReport_IsIgnored()
    {
        const string report = """
            Here is my analysis of the code.
            build_success: false
            verdict: FAIL
            These lines appear before the marker so should be ignored.

            TEST_REPORT:
            build_success: true
            verdict: PASS
            """;

        var metrics = Parse(report);

        // Only lines inside the TEST_REPORT block should be parsed
        Assert.True(metrics.BuildSuccess);
        Assert.Equal("PASS", metrics.Verdict);
    }

    [Fact]
    public void ConclusionFieldAlias_MapsToVerdict()
    {
        const string report = """
            TEST_REPORT
            ===========
            Total:     96
            Passed:    96
            Failed:      0
            Conclusion: PASS — All tests green.
            """;

        var metrics = Parse(report);

        Assert.Equal(96, metrics.TotalTests);
        Assert.Equal(96, metrics.PassedTests);
        Assert.True(Orchestrator.IsPassingVerdict(metrics.Verdict));
    }

    [Fact]
    public void NoVerdictField_InferredFromTestNumbers()
    {
        const string report = """
            TEST_REPORT
            ===========
            Total:     42
            Passed:    42
            Failed:      0
            Duration:  2.7s
            """;

        var metrics = Parse(report);

        Assert.Equal("PASS", metrics.Verdict);
        Assert.Equal(42, metrics.TotalTests);
    }

    [Fact]
    public void NoVerdictField_WithFailures_NoInference()
    {
        const string report = """
            TEST_REPORT
            ===========
            Total:     42
            Passed:    40
            Failed:      2
            """;

        var metrics = Parse(report);

        // Should NOT infer PASS when there are failures
        Assert.Equal("", metrics.Verdict);
    }
}
