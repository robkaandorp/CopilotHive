namespace CopilotHive.Worker;

/// <summary>
/// Structured test results reported by the tester via the <c>report_test_results</c> tool call.
/// Eliminates the need for free-text parsing and Brain interpretation of test output.
/// </summary>
public sealed record TestResultReport
{
    /// <summary>"PASS" or "FAIL".</summary>
    public required string Verdict { get; init; }
    /// <summary>Total number of tests discovered.</summary>
    public required int TotalTests { get; init; }
    /// <summary>Number of tests that passed.</summary>
    public required int PassedTests { get; init; }
    /// <summary>Number of tests that failed.</summary>
    public required int FailedTests { get; init; }
    /// <summary>Code coverage percentage, or null if not measured.</summary>
    public double? CoveragePercent { get; init; }
    /// <summary>Whether the build succeeded.</summary>
    public bool BuildSuccess { get; init; }
    /// <summary>Issues found during testing.</summary>
    public List<string> Issues { get; init; } = [];
}
