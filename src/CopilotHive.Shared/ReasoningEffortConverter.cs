using Microsoft.Extensions.AI;

namespace CopilotHive.Services;

/// <summary>
/// Converts between <see cref="ReasoningEffort"/> values and their canonical wire string
/// representations. Used at the gRPC boundary where proto3 represents "unset" as an empty string.
/// </summary>
public static class ReasoningEffortConverter
{
    /// <summary>
    /// Parses a wire string into a <see cref="ReasoningEffort"/>.
    /// </summary>
    /// <param name="value">The wire value, or <c>null</c>/empty/whitespace for "unset".</param>
    /// <returns>The parsed <see cref="ReasoningEffort"/>, or <c>null</c> when unset.</returns>
    /// <exception cref="ArgumentException">Thrown when the value is not a recognized reasoning effort.</exception>
    public static ReasoningEffort? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "extra_high" => ReasoningEffort.ExtraHigh,
            _ => throw new ArgumentException($"Unknown reasoning effort: '{value}'", nameof(value)),
        };
    }

    /// <summary>
    /// Formats a <see cref="ReasoningEffort"/> into its canonical wire string.
    /// </summary>
    /// <param name="value">The reasoning effort, or <c>null</c> for "unset".</param>
    /// <returns>The canonical wire string, or <c>null</c> when <paramref name="value"/> is <c>null</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when the enum value is not a recognized reasoning effort.</exception>
    public static string? Format(ReasoningEffort? value)
    {
        if (value is null) return null;

        return value.Value switch
        {
            ReasoningEffort.None => "none",
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => "extra_high",
            _ => throw new ArgumentException($"Unknown reasoning effort: '{value.Value}'", nameof(value)),
        };
    }
}
