using System.Text.Json.Serialization;

namespace CopilotHive.Workers;

/// <summary>Verdict for test execution or code/doc change tasks.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskVerdict
{
    /// <summary>All tests passed / changes completed successfully.</summary>
    Pass,
    /// <summary>Tests failed / changes could not be completed.</summary>
    Fail,
    /// <summary>Partial success — most tests pass but some issues remain.</summary>
    Partial,
}

/// <summary>Extension methods for <see cref="TaskVerdict"/>.</summary>
public static class TaskVerdictExtensions
{
    /// <summary>Converts a <see cref="TaskVerdict"/> to its canonical uppercase string (e.g. "PASS").</summary>
    public static string ToVerdictString(this TaskVerdict verdict) => verdict switch
    {
        TaskVerdict.Pass => "PASS",
        TaskVerdict.Fail => "FAIL",
        TaskVerdict.Partial => "PARTIAL",
        _ => throw new InvalidOperationException($"Unhandled TaskVerdict: {verdict}"),
    };

    /// <summary>Parses a verdict string to <see cref="TaskVerdict"/>.</summary>
    /// <returns>The parsed verdict, or <c>null</c> if the string doesn't match.</returns>
    public static TaskVerdict? ParseTaskVerdict(string? value) => value?.ToUpperInvariant() switch
    {
        "PASS" => TaskVerdict.Pass,
        "FAIL" => TaskVerdict.Fail,
        "PARTIAL" => TaskVerdict.Partial,
        _ => null,
    };
}
