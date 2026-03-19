namespace CopilotHive.Configuration;

/// <summary>
/// Utilities for validating goal ID strings.
/// </summary>
public static class GoalId
{
    /// <summary>
    /// Validates that <paramref name="id"/> is a well-formed goal identifier.
    /// A valid ID is non-empty, contains only lowercase letters (a–z), digits (0–9), and hyphens,
    /// and must not start or end with a hyphen.
    /// </summary>
    /// <param name="id">The goal ID to validate.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="id"/> fails any validation rule.
    /// The message describes the specific violation (null/empty, uppercase, whitespace,
    /// leading/trailing hyphen, or invalid character).
    /// </exception>
    public static void Validate(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("Goal ID must not be null or empty.", nameof(id));

        if (id.Any(char.IsUpper))
            throw new ArgumentException(
                $"Goal ID '{id}' contains uppercase letters; IDs must be lowercase.", nameof(id));

        if (id.Any(char.IsWhiteSpace))
            throw new ArgumentException(
                $"Goal ID '{id}' contains whitespace; only letters, digits, and hyphens are allowed.", nameof(id));

        if (id.StartsWith('-'))
            throw new ArgumentException(
                $"Goal ID '{id}' must not start with a hyphen.", nameof(id));

        if (id.EndsWith('-'))
            throw new ArgumentException(
                $"Goal ID '{id}' must not end with a hyphen.", nameof(id));

        if (id.Any(c => !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-'))
            throw new ArgumentException(
                $"Goal ID '{id}' contains invalid characters; only lowercase letters, digits, and hyphens are allowed.", nameof(id));
    }
}
