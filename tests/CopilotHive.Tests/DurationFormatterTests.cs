using CopilotHive.Goals;

namespace CopilotHive.Tests;

public sealed class DurationFormatterTests
{
    [Fact]
    public void FormatDuration_SecondsOnly_ReturnsSecondsString()
    {
        var result = DurationFormatter.FormatDuration(new TimeSpan(0, 0, 32));
        Assert.Equal("32s", result);
    }

    [Fact]
    public void FormatDuration_MinutesAndSeconds_ReturnsMinutesSecondsString()
    {
        var result = DurationFormatter.FormatDuration(new TimeSpan(0, 4, 32));
        Assert.Equal("4m 32s", result);
    }

    [Fact]
    public void FormatDuration_HoursMinutesSeconds_ReturnsFullString()
    {
        var result = DurationFormatter.FormatDuration(new TimeSpan(1, 12, 5));
        Assert.Equal("1h 12m 5s", result);
    }

    [Fact]
    public void FormatDuration_Zero_ReturnsZeroSeconds()
    {
        var result = DurationFormatter.FormatDuration(TimeSpan.Zero);
        Assert.Equal("0s", result);
    }

    [Fact]
    public void FormatDuration_ExactlyOneMinute_ShowsMinutesAndZeroSeconds()
    {
        var result = DurationFormatter.FormatDuration(new TimeSpan(0, 1, 0));
        Assert.Equal("1m 0s", result);
    }

    [Fact]
    public void FormatDuration_ExactlyOneHour_ShowsHoursZeroMinutesZeroSeconds()
    {
        var result = DurationFormatter.FormatDuration(new TimeSpan(1, 0, 0));
        Assert.Equal("1h 0m 0s", result);
    }

    [Fact]
    public void FormatDurationSeconds_DelegatesToFormatDuration()
    {
        var result = DurationFormatter.FormatDurationSeconds(272.0); // 4m 32s
        Assert.Equal("4m 32s", result);
    }

    [Fact]
    public void FormatDurationSeconds_SecondsOnly()
    {
        var result = DurationFormatter.FormatDurationSeconds(32.0);
        Assert.Equal("32s", result);
    }

    [Fact]
    public void FormatDurationSeconds_HoursMinutesSeconds()
    {
        var result = DurationFormatter.FormatDurationSeconds(4325.0); // 1h 12m 5s
        Assert.Equal("1h 12m 5s", result);
    }
}
