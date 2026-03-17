using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotHive.Metrics;

/// <summary>
/// Records all quality metrics captured during a single orchestration iteration.
/// </summary>
public sealed class IterationMetrics
{
    /// <summary>One-based iteration number within the current goal session.</summary>
    public int Iteration { get; set; }
    /// <summary>UTC timestamp when this iteration was recorded.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    /// <summary>Wall-clock duration of the iteration.</summary>
    public TimeSpan Duration { get; set; }

    // Build
    /// <summary>Whether the project built without errors.</summary>
    public bool BuildSuccess { get; set; }

    // Unit tests (written by coder, run by tester)
    /// <summary>Total number of unit tests discovered.</summary>
    public int TotalTests { get; set; }
    /// <summary>Number of unit tests that passed.</summary>
    public int PassedTests { get; set; }
    /// <summary>Number of unit tests that failed.</summary>
    public int FailedTests { get; set; }
    /// <summary>Code coverage percentage as reported by the test runner.</summary>
    public double CoveragePercent { get; set; }

    // Integration tests (written and run by tester)
    /// <summary>Total number of integration tests discovered.</summary>
    public int IntegrationTestsTotal { get; set; }
    /// <summary>Number of integration tests that passed.</summary>
    public int IntegrationTestsPassed { get; set; }

    // Runtime verification
    /// <summary>Whether runtime smoke-tests confirmed the application starts correctly.</summary>
    public bool RuntimeVerified { get; set; }

    // Tester verdict: PASS, PARTIAL, FAIL
    /// <summary>Overall tester verdict: PASS, PARTIAL, or FAIL.</summary>
    public string Verdict { get; set; } = "";
    /// <summary>Short human-readable summary from the test report.</summary>
    public string TestReportSummary { get; set; } = "";
    /// <summary>List of issues identified during testing.</summary>
    public List<string> Issues { get; set; } = [];

    /// <summary>Number of issues found during code review.</summary>
    public int ReviewIssuesFound { get; set; }
    /// <summary>Detailed list of review issues.</summary>
    public List<string> ReviewIssues { get; set; } = [];
    /// <summary>Reviewer's verdict string (e.g. "APPROVED", "CHANGES_REQUESTED").</summary>
    public string ReviewVerdict { get; set; } = "";
    /// <summary>Number of task retries consumed during this iteration (review + test retries).</summary>
    public int RetryCount { get; set; }
    /// <summary>Number of review-phase retries specifically.</summary>
    public int ReviewRetryCount { get; set; }
    /// <summary>Number of test-phase retries specifically.</summary>
    public int TestRetryCount { get; set; }

    /// <summary>Wall-clock duration of each pipeline phase, keyed by phase name (e.g. "Coding", "Review").</summary>
    public Dictionary<string, TimeSpan> PhaseDurations { get; set; } = [];

    /// <summary>Prompt tokens consumed during this iteration (0 until SDK exposes token metrics).</summary>
    public int PromptTokens { get; set; }
    /// <summary>Completion tokens consumed during this iteration (0 until SDK exposes token metrics).</summary>
    public int CompletionTokens { get; set; }

    /// <summary>Version identifiers of the AGENTS.md files active during this iteration, keyed by role.</summary>
    public Dictionary<string, string> AgentsMdVersions { get; set; } = [];
    /// <summary>Arbitrary custom key/value metrics recorded by workers.</summary>
    public Dictionary<string, object> Custom { get; set; } = [];

    /// <summary>Whether the improve phase was skipped due to a non-critical failure (e.g. Brain timeout).</summary>
    public bool ImproverSkipped { get; set; }
    /// <summary>Reason the improver was skipped, if applicable.</summary>
    public string? ImproverSkipReason { get; set; }

    /// <summary>Ratio of passed unit tests to total unit tests (0 when no tests exist).</summary>
    [JsonIgnore]
    public double PassRate => TotalTests > 0 ? (double)PassedTests / TotalTests : 0;

    /// <summary>Ratio of passed integration tests to total integration tests (0 when none exist).</summary>
    [JsonIgnore]
    public double IntegrationPassRate => IntegrationTestsTotal > 0
        ? (double)IntegrationTestsPassed / IntegrationTestsTotal : 0;
}
