namespace CopilotHive.Shared;

/// <summary>
/// Resolves the first usable credential from an ordered list of candidates.
/// <para>
/// <b>Contract.</b> <see cref="Resolve"/> returns the FIRST candidate passing
/// <c>!string.IsNullOrWhiteSpace</c>, or <c>null</c> when every candidate is
/// <c>null</c> or whitespace (including an empty argument list).
/// </para>
/// <para>
/// <b>The selected candidate is returned UNCHANGED — it is never trimmed.</b> Any
/// surrounding whitespace is part of the resolved value. Callers that need a
/// trimmed value must trim it themselves; this type guarantees the raw selection
/// so callers can rely on byte-exact credentials.
/// </para>
/// </summary>
public static class GitCredentialResolver
{
    /// <summary>
    /// Returns the first candidate that is not <c>null</c>, empty or whitespace only,
    /// returned <b>UNCHANGED (never trimmed)</b>; <c>null</c> when all candidates are
    /// <c>null</c>/whitespace, including when no candidates are supplied at all.
    /// <para>
    /// Whitespace-is-absent applies at EACH step: any candidate that
    /// <see cref="string.IsNullOrWhiteSpace(string)"/> flags falls through to the next.
    /// </para>
    /// </summary>
    /// <param name="candidates">The ordered credential candidates; later candidates are ignored when an earlier one is present.</param>
    /// <returns>The first present candidate, unchanged, or <c>null</c> when none is present.</returns>
    public static string? Resolve(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }
}