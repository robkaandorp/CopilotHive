using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotHive.Metrics;

public sealed class IterationMetrics
{
    public int Iteration { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public TimeSpan Duration { get; set; }
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public double CoveragePercent { get; set; }
    public int ReviewIssuesFound { get; set; }
    public int RetryCount { get; set; }
    public Dictionary<string, string> AgentsMdVersions { get; set; } = [];
    public Dictionary<string, object> Custom { get; set; } = [];

    [JsonIgnore]
    public double PassRate => TotalTests > 0 ? (double)PassedTests / TotalTests : 0;
}
