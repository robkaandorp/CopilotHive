using CopilotHive.Shared;

namespace CopilotHive.Tests;

public class ToolValidationTests
{
    [Fact]
    public void Check_AllValid_ReturnsNull()
    {
        var result = ToolValidation.Check(
            (true, "should not appear"),
            (true, "also fine"));
        Assert.Null(result);
    }

    [Fact]
    public void Check_SingleFailure_ReturnsError()
    {
        var result = ToolValidation.Check(
            (true, "ok"),
            (false, "bad value"));
        Assert.NotNull(result);
        Assert.Contains("bad value", result);
        Assert.Contains("ERROR", result);
    }

    [Fact]
    public void Check_MultipleFailures_ReturnsAll()
    {
        var result = ToolValidation.Check(
            (false, "first issue"),
            (true, "ok"),
            (false, "second issue"));
        Assert.NotNull(result);
        Assert.Contains("first issue", result);
        Assert.Contains("second issue", result);
    }

    [Fact]
    public void Check_NoRules_ReturnsNull()
    {
        var result = ToolValidation.Check();
        Assert.Null(result);
    }

    [Fact]
    public void Check_ErrorFormat_InstructsAgentToRetry()
    {
        var result = ToolValidation.Check((false, "verdict must be PASS or FAIL"));
        Assert.NotNull(result);
        Assert.Contains("Fix these and call the tool again", result);
    }

    // ---- report_test_results validation scenarios ----

    [Theory]
    [InlineData("PASS")]
    [InlineData("FAIL")]
    public void TestResults_ValidVerdict_Passes(string verdict)
    {
        var error = ToolValidation.Check(
            (verdict is "PASS" or "FAIL", "verdict must be 'PASS' or 'FAIL'"));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MAYBE")]
    [InlineData("pass")]
    [InlineData("Pass")]
    public void TestResults_InvalidVerdict_Fails(string verdict)
    {
        var error = ToolValidation.Check(
            (verdict is "PASS" or "FAIL", "verdict must be 'PASS' or 'FAIL'"));
        Assert.NotNull(error);
    }

    [Fact]
    public void TestResults_NegativeTestCount_Fails()
    {
        var error = ToolValidation.Check(
            (-1 >= 0, "totalTests must be >= 0"));
        Assert.NotNull(error);
    }

    [Fact]
    public void TestResults_PassedPlusFailedExceedsTotal_Fails()
    {
        int total = 10, passed = 8, failed = 5;
        var error = ToolValidation.Check(
            (passed + failed <= total, $"passedTests + failedTests exceeds totalTests"));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(-1)]   // sentinel
    [InlineData(0)]
    [InlineData(50.5)]
    [InlineData(100)]
    public void TestResults_ValidCoverage_Passes(double coverage)
    {
        var error = ToolValidation.Check(
            (coverage is >= -1 and <= 100, "coverage out of range"));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(100.1)]
    [InlineData(999)]
    public void TestResults_InvalidCoverage_Fails(double coverage)
    {
        var error = ToolValidation.Check(
            (coverage is >= -1 and <= 100, "coverage out of range"));
        Assert.NotNull(error);
    }
}
