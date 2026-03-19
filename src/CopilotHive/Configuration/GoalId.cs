using System.Text.RegularExpressions;

namespace CopilotHive.Configuration;

/// <summary>
/// Utilities for validating goal ID strings.
/// </summary>
public static class GoalId
{
    private static readonly Regex ValidPattern = new(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.Compiled);

    /// <summary>
    /// Validates that <paramref name="id"/> is a well-formed goal identifier.
    /// A valid ID is non-empty, contains only lowercase letters (a–z), digits (0–9), and hyphens,
    /// and must not start or end with a hyphen.
    /// </summary>
    /// <param name="id">The goal ID to validate.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="id"/> is null, empty, or does not match the required format.
    /// </exception>
    public static void Validate(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("Goal ID must not be null or empty.", nameof(id));

        if (!ValidPattern.IsMatch(id))
            throw new ArgumentException(
                $"Goal ID '{id}' is invalid. IDs must contain only lowercase letters (a-z), digits (0-9), " +
                "and hyphens (-), and must not start or end with a hyphen.",
                nameof(id));
    }
}
