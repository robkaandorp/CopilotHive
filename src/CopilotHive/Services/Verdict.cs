namespace CopilotHive.Services;

/// <summary>
/// Canonical verdict strings used in the worker-to-orchestrator contract.
/// Workers report these via structured metrics; the dispatcher maps them
/// to <see cref="PhaseInput"/> transitions.
/// </summary>
public static class Verdict
{
    /// <summary>Worker completed the task successfully.</summary>
    public const string Pass = "PASS";

    /// <summary>Worker encountered an error or the task failed.</summary>
    public const string Fail = "FAIL";

    /// <summary>Task was cancelled before completion.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>Reviewer approved the work without changes.</summary>
    public const string Approve = "APPROVE";

    /// <summary>Reviewer requested changes before the work can be accepted.</summary>
    public const string RequestChanges = "REQUEST_CHANGES";

    /// <summary>
    /// Returns <c>true</c> if the verdict matches the expected value,
    /// using case-insensitive comparison to handle worker variability.
    /// </summary>
    public static bool Matches(string? verdict, string expected)
        => string.Equals(verdict, expected, StringComparison.OrdinalIgnoreCase);
}
