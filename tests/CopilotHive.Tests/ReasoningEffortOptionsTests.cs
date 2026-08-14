using CopilotHive.Dashboard;

using Microsoft.Extensions.AI;

namespace CopilotHive.Tests;

/// <summary>
/// Direct unit tests for <see cref="ReasoningEffortOptions"/>, the shared single source of
/// truth for reasoning-effort presentation in the dashboard UI.
/// </summary>
public sealed class ReasoningEffortOptionsTests
{
    // ── Label ────────────────────────────────────────────────────────────────

    [Fact]
    public void Label_Null_ReturnsEmDash()
    {
        Assert.Equal("—", ReasoningEffortOptions.Label(null));
    }

    [Fact]
    public void Label_High_ReturnsHigh()
    {
        Assert.Equal("High", ReasoningEffortOptions.Label(ReasoningEffort.High));
    }

    [Fact]
    public void Label_ExtraHigh_ReturnsExtraHigh()
    {
        Assert.Equal("Extra High", ReasoningEffortOptions.Label(ReasoningEffort.ExtraHigh));
    }

    [Fact]
    public void Label_None_ReturnsNone()
    {
        Assert.Equal("None", ReasoningEffortOptions.Label(ReasoningEffort.None));
    }

    [Fact]
    public void Label_Low_ReturnsLow()
    {
        Assert.Equal("Low", ReasoningEffortOptions.Label(ReasoningEffort.Low));
    }

    [Fact]
    public void Label_Medium_ReturnsMedium()
    {
        Assert.Equal("Medium", ReasoningEffortOptions.Label(ReasoningEffort.Medium));
    }

    // ── WireValue ────────────────────────────────────────────────────────────

    [Fact]
    public void WireValue_ExtraHigh_ReturnsSnakeCase()
    {
        Assert.Equal("extra_high", ReasoningEffortOptions.WireValue(ReasoningEffort.ExtraHigh));
    }

    [Fact]
    public void WireValue_None_ReturnsNone()
    {
        Assert.Equal("none", ReasoningEffortOptions.WireValue(ReasoningEffort.None));
    }

    [Fact]
    public void WireValue_High_ReturnsHigh()
    {
        Assert.Equal("high", ReasoningEffortOptions.WireValue(ReasoningEffort.High));
    }

    // ── Options ──────────────────────────────────────────────────────────────

    [Fact]
    public void Options_HasExactlyFiveEntries()
    {
        Assert.Equal(5, ReasoningEffortOptions.Options.Count);
    }

    [Fact]
    public void Options_ContainsAllFiveValuesInAscendingOrder()
    {
        Assert.Equal(
            new[]
            {
                ReasoningEffort.None,
                ReasoningEffort.Low,
                ReasoningEffort.Medium,
                ReasoningEffort.High,
                ReasoningEffort.ExtraHigh,
            },
            ReasoningEffortOptions.Options.Select(o => o.Value));
    }
}
