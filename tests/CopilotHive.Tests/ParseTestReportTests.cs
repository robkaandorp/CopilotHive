using CopilotHive.Metrics;
using CopilotHive.Services;

namespace CopilotHive.Tests;
public class FallbackParseTestMetricsTests
{
    private static IterationMetrics Parse(string output)
    {
        var metrics = new IterationMetrics();
        GoalDispatcher.FallbackParseTestMetrics(output, metrics);
        return metrics;
    }

    [Fact]
    public void DotnetTestOutput_ExtractsMetrics()
    {
        const string output = """
            I ran the tests and here are the results:
            
            Passed!  - Failed:     0, Passed:   268, Skipped:     0, Total:   268, Duration: 46 s - CopilotHive.Tests.dll (net10.0)
            """;

        var metrics = Parse(output);

        Assert.Equal(268, metrics.TotalTests);
        Assert.Equal(268, metrics.PassedTests);
        Assert.Equal(0, metrics.FailedTests);
    }

    [Fact]
    public void DotnetTestOutput_WithFailures()
    {
        const string output = "Failed!  - Failed:     3, Passed:   265, Skipped:     0, Total:   268";

        var metrics = Parse(output);

        Assert.Equal(268, metrics.TotalTests);
        Assert.Equal(265, metrics.PassedTests);
        Assert.Equal(3, metrics.FailedTests);
    }

    [Fact]
    public void TestReportFields_Parsed()
    {
        const string output = """
            total_tests: 42
            passed_tests: 40
            failed_tests: 2
            """;

        var metrics = Parse(output);

        Assert.Equal(42, metrics.TotalTests);
        Assert.Equal(40, metrics.PassedTests);
        Assert.Equal(2, metrics.FailedTests);
    }

    [Fact]
    public void EmptyInput_LeavesDefaults()
    {
        var metrics = Parse("");

        Assert.Equal(0, metrics.TotalTests);
        Assert.Equal(0, metrics.PassedTests);
        Assert.Equal(0, metrics.FailedTests);
    }

    [Fact]
    public void NullInput_LeavesDefaults()
    {
        var metrics = new IterationMetrics();
        GoalDispatcher.FallbackParseTestMetrics(null!, metrics);

        Assert.Equal(0, metrics.TotalTests);
    }

    [Fact]
    public void MarkdownTable_ExtractsMetrics()
    {
        const string output = """
            ## TEST_REPORT

            **Branch:** `copilothive/some-branch`

            ### Build Result
            | Metric | Count |
            |--------|-------|
            | Errors | 0 |
            | Warnings | 0 |

            ### Test Result
            | Metric | Count |
            |--------|-------|
            | Total | 273 |
            | Passed | 273 |
            | Failed | 0 |
            | Skipped | 0 |

            ### Overall Verdict: PASS
            """;

        var metrics = Parse(output);

        Assert.Equal(273, metrics.TotalTests);
        Assert.Equal(273, metrics.PassedTests);
        Assert.Equal(0, metrics.FailedTests);
        Assert.True(metrics.BuildSuccess);
    }

    [Fact]
    public void BuildSucceeded_SetsBuildSuccess()
    {
        const string output = "Build succeeded.\nPassed!  - Failed:     0, Passed:   42, Skipped:     0, Total:   42";

        var metrics = Parse(output);

        Assert.True(metrics.BuildSuccess);
        Assert.Equal(42, metrics.TotalTests);
    }

    [Fact]
    public void FallbackParseBuildSuccess_EmojiSucceeded_ReturnsTrue()
    {
        var result = Parse("✅ Succeeded");
        Assert.True(result.BuildSuccess);
    }

    [Fact]
    public void FallbackParseBuildSuccess_BuildStatusEmojiSucceeded_ReturnsTrue()
    {
        var result = Parse("Build Status: ✅ Succeeded");
        Assert.True(result.BuildSuccess);
    }

    [Fact]
    public void FallbackParseBuildSuccess_BuildResultPass_ReturnsTrue()
    {
        var result = Parse("Build Result: PASS");
        Assert.True(result.BuildSuccess);
    }

    [Fact]
    public void FallbackParseBuildSuccess_BuildColonPass_ReturnsTrue()
    {
        var result = Parse("Build: PASS");
        Assert.True(result.BuildSuccess);
    }

    [Fact]
    public void FallbackParseBuildSuccess_EmojiBoldSucceeded_ReturnsTrue()
    {
        var result = Parse("✅ **Succeeded**");
        Assert.True(result.BuildSuccess);
    }

    [Fact]
    public void FallbackParseBuildSuccess_ZeroErrorParens_ReturnsTrue()
    {
        var result = Parse("0 Error(s)");
        Assert.True(result.BuildSuccess);
    }

    [Fact]
    public void FallbackParseBuildSuccess_ZeroErrorsPlain_ReturnsTrue()
    {
        var result = Parse("0 errors");
        Assert.True(result.BuildSuccess);
    }

    [Fact]
    public void FallbackParseBuildSuccess_LowercaseBuildPass_ReturnsTrue()
    {
        var result = Parse("build: pass");
        Assert.True(result.BuildSuccess);
    }

    [Fact]
    public void FallbackParseBuildSuccess_BuildFailed_ReturnsFalse()
    {
        var result = Parse("Build FAILED");
        Assert.False(result.BuildSuccess);
    }

    [Fact]
    public void FallbackParseBuildSuccess_OneErrorParens_ReturnsFalse()
    {
        var result = Parse("1 Error(s)");
        Assert.False(result.BuildSuccess);
    }

    [Fact]
    public void FallbackParse_MultiplePassedValues_KeepsLargest()
    {
        const string output = "Passed: 19\nFailed: 1\nTotal: 20\n\nPassed: 294\nFailed: 2\nTotal: 296";
        var metrics = Parse(output);
        Assert.Equal(294, metrics.PassedTests);
        Assert.Equal(296, metrics.TotalTests);
        Assert.Equal(2, metrics.FailedTests);
    }

    [Fact]
    public void FallbackParse_MultiplePassedValues_MarkdownTable_KeepsLargest()
    {
        const string output = "| Passed | 19 |\n| Failed | 1 |\n| Total | 20 |\n\n| Passed | 294 |\n| Failed | 2 |\n| Total | 296 |";
        var metrics = Parse(output);
        Assert.Equal(294, metrics.PassedTests);
        Assert.Equal(296, metrics.TotalTests);
        Assert.Equal(2, metrics.FailedTests);
    }

    [Fact]
    public void FallbackParse_FirstOccurrenceLarger_KeepsFirst()
    {
        const string output = "Passed: 294\nFailed: 2\nTotal: 296\n\nPassed: 19\nFailed: 1\nTotal: 20";
        var metrics = Parse(output);
        Assert.Equal(294, metrics.PassedTests);
        Assert.Equal(296, metrics.TotalTests);
        Assert.Equal(2, metrics.FailedTests);
    }
}
