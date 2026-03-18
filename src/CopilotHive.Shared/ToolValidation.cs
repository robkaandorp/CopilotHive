namespace CopilotHive.Shared;

/// <summary>
/// Lightweight validation helper for tool call parameters.
/// Returns a formatted error message for the agent to fix and retry,
/// or null if all checks pass.
/// </summary>
public static class ToolValidation
{
    /// <summary>
    /// Validates a set of rules. Returns null if all pass, or a formatted error
    /// message listing all failures so the agent can fix and re-call the tool.
    /// </summary>
    /// <param name="rules">Tuples of (isValid, errorMessage). All failing rules are reported.</param>
    /// <returns>Null if valid; error message string if any rule fails.</returns>
    public static string? Check(params (bool isValid, string error)[] rules)
    {
        var errors = new List<string>();
        foreach (var (isValid, error) in rules)
        {
            if (!isValid)
                errors.Add(error);
        }

        if (errors.Count == 0)
            return null;

        return "ERROR: Invalid parameters. Fix these and call the tool again:\n"
            + string.Join("\n", errors.Select(e => $"  - {e}"));
    }
}
