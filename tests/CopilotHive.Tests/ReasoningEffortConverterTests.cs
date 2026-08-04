using CopilotHive.Services;

using Microsoft.Extensions.AI;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for <see cref="ReasoningEffortConverter"/>, covering the explicit five-value
/// case-insensitive mapping in both directions and the failure modes for unrecognized values.
/// </summary>
public sealed class ReasoningEffortConverterTests
{
    // ── Parse: null / empty / whitespace ──────────────────────────────────────

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(ReasoningEffortConverter.Parse(null));
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        Assert.Null(ReasoningEffortConverter.Parse(""));
    }

    [Fact]
    public void Parse_Whitespace_ReturnsNull()
    {
        Assert.Null(ReasoningEffortConverter.Parse("   "));
        Assert.Null(ReasoningEffortConverter.Parse("\t"));
        Assert.Null(ReasoningEffortConverter.Parse(" \r\n "));
    }

    // ── Parse: the five recognized values, case-insensitive ───────────────────

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    [InlineData("None")]
    public void Parse_None_CaseInsensitive_ReturnsNone(string input)
    {
        Assert.Equal(ReasoningEffort.None, ReasoningEffortConverter.Parse(input));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("LOW")]
    [InlineData("Low")]
    public void Parse_Low_CaseInsensitive_ReturnsLow(string input)
    {
        Assert.Equal(ReasoningEffort.Low, ReasoningEffortConverter.Parse(input));
    }

    [Theory]
    [InlineData("medium")]
    [InlineData("MEDIUM")]
    [InlineData("Medium")]
    public void Parse_Medium_CaseInsensitive_ReturnsMedium(string input)
    {
        Assert.Equal(ReasoningEffort.Medium, ReasoningEffortConverter.Parse(input));
    }

    [Theory]
    [InlineData("high")]
    [InlineData("HIGH")]
    [InlineData("High")]
    public void Parse_High_CaseInsensitive_ReturnsHigh(string input)
    {
        Assert.Equal(ReasoningEffort.High, ReasoningEffortConverter.Parse(input));
    }

    [Theory]
    [InlineData("extra_high")]
    [InlineData("EXTRA_HIGH")]
    [InlineData("Extra_High")]
    public void Parse_ExtraHigh_CaseInsensitive_ReturnsExtraHigh(string input)
    {
        Assert.Equal(ReasoningEffort.ExtraHigh, ReasoningEffortConverter.Parse(input));
    }

    /// <summary>
    /// Surrounding whitespace is tolerated (trimmed) so that sloppy YAML configuration values
    /// like <c>"  High "</c> still resolve to a canonical effort.
    /// </summary>
    [Theory]
    [InlineData("high ", ReasoningEffort.High)]
    [InlineData(" high", ReasoningEffort.High)]
    [InlineData("  High ", ReasoningEffort.High)]
    [InlineData("\textra_high\n", ReasoningEffort.ExtraHigh)]
    public void Parse_SurroundingWhitespace_IsTrimmed(string input, ReasoningEffort expected)
    {
        Assert.Equal(expected, ReasoningEffortConverter.Parse(input));
    }

    // ── Parse: invalid values ─────────────────────────────────────────────────

    [Theory]
    [InlineData("ultra")]
    [InlineData("maximum")]
    [InlineData("1")]
    [InlineData("extrahigh")]
    [InlineData("extra-high")]
    [InlineData("hi gh")]
    public void Parse_InvalidNonEmpty_ThrowsArgumentException(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => ReasoningEffortConverter.Parse(input));
        Assert.Contains(input, ex.Message, StringComparison.Ordinal);
    }

    // ── Format: null ──────────────────────────────────────────────────────────

    [Fact]
    public void Format_Null_ReturnsNull()
    {
        Assert.Null(ReasoningEffortConverter.Format(null));
    }

    // ── Format: the five recognized values ────────────────────────────────────

    [Theory]
    [InlineData(ReasoningEffort.None, "none")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "extra_high")]
    public void Format_AllFiveValues_ReturnsLowercaseString(ReasoningEffort input, string expected)
    {
        Assert.Equal(expected, ReasoningEffortConverter.Format(input));
    }

    // ── Format: unknown enum value ────────────────────────────────────────────

    [Fact]
    public void Format_UnknownEnum_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ReasoningEffortConverter.Format((ReasoningEffort)999));
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ReasoningEffort.None)]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.ExtraHigh)]
    public void FormatThenParse_RoundTrips(ReasoningEffort effort)
    {
        var formatted = ReasoningEffortConverter.Format(effort);
        Assert.Equal(effort, ReasoningEffortConverter.Parse(formatted));
    }
}
