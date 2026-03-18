using System.Text.Json.Serialization;

namespace CopilotHive.Workers;

/// <summary>Verdict from a code review.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewVerdict
{
    /// <summary>Code is approved and ready for the next phase.</summary>
    Approve,
    /// <summary>Changes are required before proceeding.</summary>
    RequestChanges,
}

/// <summary>Extension methods for <see cref="ReviewVerdict"/>.</summary>
public static class ReviewVerdictExtensions
{
    /// <summary>Converts a <see cref="ReviewVerdict"/> to its canonical uppercase string.</summary>
    public static string ToVerdictString(this ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Approve => "APPROVE",
        ReviewVerdict.RequestChanges => "REQUEST_CHANGES",
        _ => throw new InvalidOperationException($"Unhandled ReviewVerdict: {verdict}"),
    };

    /// <summary>Parses a verdict string to <see cref="ReviewVerdict"/>.</summary>
    /// <returns>The parsed verdict, or <c>null</c> if the string doesn't match.</returns>
    public static ReviewVerdict? ParseReviewVerdict(string? value) => value?.ToUpperInvariant() switch
    {
        "APPROVE" => ReviewVerdict.Approve,
        "REQUEST_CHANGES" => ReviewVerdict.RequestChanges,
        _ => null,
    };
}
