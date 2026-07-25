using System.ComponentModel;
using System.Text;

using CopilotHive.Dashboard;

namespace CopilotHive.Orchestration;

public sealed partial class Composer
{
    [Description("Retrieve recent application log entries from the dashboard log sink.")]
    internal async Task<string> GetRecentLogsAsync(
        [Description("Maximum number of log entries to return (default 50, max 500)")] int count = 50,
        [Description("Optional: filter by minimum log level (e.g. 'Warning' to see warnings and above)")] string? minLevel = null,
        [Description("Optional: filter by category name (e.g. 'GoalDispatcher', 'DistributedBrain')")] string? category = null,
        [Description("Optional: filter by text in the log message")] string? contains = null)
    {
        var logSink = _serviceProvider?.GetService<DashboardLogSink>();
        if (logSink is null)
            return "Log sink is not available.";

        if (count < 1) count = 50;
        if (count > 500) count = 500;

        var entries = logSink.GetRecent(count);

        LogLevel? filterLevel = null;
        if (!string.IsNullOrWhiteSpace(minLevel) && Enum.TryParse<LogLevel>(minLevel, true, out var parsed))
            filterLevel = parsed;

        var filtered = entries.AsEnumerable();
        if (filterLevel.HasValue)
            filtered = filtered.Where(e => e.Level >= filterLevel.Value);
        if (!string.IsNullOrWhiteSpace(category))
            filtered = filtered.Where(e => e.Category.Contains(category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(contains))
            filtered = filtered.Where(e => e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase));

        var result = filtered.ToList();
        if (result.Count == 0)
            return $"No log entries matching the filters (out of {entries.Count} total).";

        var sb = new StringBuilder();
        foreach (var entry in result)
        {
            sb.AppendLine($"{entry.Timestamp:HH:mm:ss} {entry.LevelLabel} [{entry.Category}] {entry.Message}");
        }
        return sb.ToString();
    }
}
