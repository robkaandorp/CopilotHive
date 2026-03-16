using CopilotHive.Orchestration;

namespace CopilotHive.Tests;

/// <summary>
/// Tests that <see cref="DistributedBrain.FallbackParseTestMetrics"/> correctly extracts
/// <c>CoveragePercent</c> from Coverlet text-table output and key-value lines.
/// </summary>
public class CoverageParsingTests
{
    // Build minimal output that satisfies the "at least one recognisable pattern" requirement so
    // FallbackParseTestMetrics doesn't return null before reaching coverage parsing.
    private const string PassSuffix = "\nPassed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1";

    [Fact]
    public void ParseCoverlet_TotalRow_ReturnsLinePercent()
    {
        const string output = """
            +------------------+--------+--------+--------+
            | Module           | Line   | Branch | Method |
            +------------------+--------+--------+--------+
            | CopilotHive      | 73.5%  | 61.2%  | 80.1%  |
            +------------------+--------+--------+--------+
            | TOTAL            | 73.5%  | 61.2%  | 80.1%  |
            +------------------+--------+--------+--------+
            """ + PassSuffix;

        var result = DistributedBrain.FallbackParseTestMetrics(output);

        Assert.NotNull(result);
        Assert.Equal(73.5, result.CoveragePercent);
    }

    [Fact]
    public void ParseCoverlet_TotalRow_100Percent()
    {
        const string output = "| TOTAL | 100% | 100% | 100% |" + PassSuffix;

        var result = DistributedBrain.FallbackParseTestMetrics(output);

        Assert.NotNull(result);
        Assert.Equal(100.0, result.CoveragePercent);
    }

    [Fact]
    public void ParseCoverlet_TotalRow_0Percent()
    {
        const string output = "| TOTAL | 0% | 0% | 0% |" + PassSuffix;

        var result = DistributedBrain.FallbackParseTestMetrics(output);

        Assert.NotNull(result);
        Assert.Equal(0.0, result.CoveragePercent);
    }

    [Fact]
    public void ParseCoverlet_MissingTotalRow_Returns0()
    {
        const string output = "| Module | 55.0% | 40.0% | 60.0% |" + PassSuffix;

        var result = DistributedBrain.FallbackParseTestMetrics(output);

        Assert.NotNull(result);
        Assert.Null(result.CoveragePercent);
    }

    [Fact]
    public void ParseCoverlet_EmptyInput_Returns0()
    {
        var result = DistributedBrain.FallbackParseTestMetrics("");

        // Empty input returns null (no recognisable patterns at all)
        Assert.Null(result);
    }

    [Fact]
    public void ParseKeyValue_CoveragePercent_ReturnsValue()
    {
        const string output = "coverage_percent: 73.5" + PassSuffix;

        var result = DistributedBrain.FallbackParseTestMetrics(output);

        Assert.NotNull(result);
        Assert.Equal(73.5, result.CoveragePercent);
    }

    [Fact]
    public void ParseKeyValue_CoverageWithPercent_ReturnsValue()
    {
        const string output = "coverage: 73.5%" + PassSuffix;

        var result = DistributedBrain.FallbackParseTestMetrics(output);

        Assert.NotNull(result);
        Assert.Equal(73.5, result.CoveragePercent);
    }
}
