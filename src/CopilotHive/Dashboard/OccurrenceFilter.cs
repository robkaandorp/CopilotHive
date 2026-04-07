namespace CopilotHive.Dashboard;

/// <summary>
/// Reusable occurrence-filtering helpers for phase-clarification and progress-report
/// deduplication across both the summary and pipeline paths.
/// </summary>
internal static class OccurrenceFilter
{
    /// <summary>
    /// Filters a collection of entries (clarifications or progress reports) by
    /// <paramref name="occurrence"/> and falls back to all entries if no
    /// occurrence-specific entries exist (backward compat for old persisted data).
    /// </summary>
    /// <typeparam name="T">Entry type with an <c>Occurrence</c> property.</typeparam>
    /// <param name="all">All entries for the phase (already filtered by iteration and phase name).</param>
    /// <param name="occurrence">The occurrence number to filter by.</param>
    /// <returns>Occurrence-specific entries, or all entries if no occurrence-tagged entries exist.</returns>
    public static List<T> FilterByOccurrence<T>(List<T> all, int occurrence) where T : class
    {
        var occSpecific = all.Where(e => GetOccurrence(e) == occurrence).ToList();
        if (occSpecific.Count > 0)
            return occSpecific;
        // Backward compat: fall back to all entries when no occurrence-tagged entries exist
        return all.Any(e => GetOccurrence(e) > 0) ? [] : all;
    }

    private static int GetOccurrence<T>(T entry)
    {
        var prop = typeof(T).GetProperty("Occurrence");
        return prop != null ? (int)(prop.GetValue(entry) ?? 0) : 0;
    }
}
