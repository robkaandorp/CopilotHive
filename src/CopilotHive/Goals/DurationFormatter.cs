namespace CopilotHive.Goals;

/// <summary>
/// Provides human-readable formatting for elapsed time durations.
/// </summary>
public static class DurationFormatter
{
    /// <summary>
    /// Formats a <see cref="TimeSpan"/> into a concise human-readable string.
    /// </summary>
    /// <remarks>
    /// Format rules:
    /// <list type="bullet">
    ///   <item>When total hours &gt; 0: <c>{h}h {m}m {s}s</c></item>
    ///   <item>When total minutes &gt; 0: <c>{m}m {s}s</c></item>
    ///   <item>Otherwise: <c>{s}s</c></item>
    /// </list>
    /// Seconds are always included even when zero (e.g. "1m 0s", "1h 0m 0s").
    /// </remarks>
    /// <param name="duration">The duration to format.</param>
    /// <returns>A human-readable elapsed time string.</returns>
    public static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = (int)Math.Floor(Math.Abs(duration.TotalSeconds));
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;

        if (hours > 0)
            return $"{hours}h {minutes}m {seconds}s";

        if (minutes > 0)
            return $"{minutes}m {seconds}s";

        return $"{seconds}s";
    }

    /// <summary>
    /// Formats a duration expressed as a number of seconds into a concise human-readable string.
    /// </summary>
    /// <param name="seconds">The duration in seconds.</param>
    /// <returns>A human-readable elapsed time string.</returns>
    public static string FormatDurationSeconds(double seconds) =>
        FormatDuration(TimeSpan.FromSeconds(seconds));
}
