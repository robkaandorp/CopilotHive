using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotHive.Metrics;

public sealed class IterationMetrics
{
    public int Iteration { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public TimeSpan Duration { get; set; }

    // Build
    public bool BuildSuccess { get; set; }

    // Unit tests (written by coder, run by tester)
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public double CoveragePercent { get; set; }

    // Integration tests (written and run by tester)
    public int IntegrationTestsTotal { get; set; }
    public int IntegrationTestsPassed { get; set; }

    // Runtime verification
    public bool RuntimeVerified { get; set; }

    // Tester verdict: PASS, PARTIAL, FAIL
    public string Verdict { get; set; } = "";
    public string TestReportSummary { get; set; } = "";
    public List<string> Issues { get; set; } = [];

    public int ReviewIssuesFound { get; set; }
    public List<string> ReviewIssues { get; set; } = [];
    public string ReviewVerdict { get; set; } = "";
    public int RetryCount { get; set; }
    public Dictionary<string, string> AgentsMdVersions { get; set; } = [];
    public Dictionary<string, object> Custom { get; set; } = [];

    [JsonIgnore]
    public double PassRate => TotalTests > 0 ? (double)PassedTests / TotalTests : 0;

    [JsonIgnore]
    public double IntegrationPassRate => IntegrationTestsTotal > 0
        ? (double)IntegrationTestsPassed / IntegrationTestsTotal : 0;
}
