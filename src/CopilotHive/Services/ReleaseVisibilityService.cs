using CopilotHive.Goals;

namespace CopilotHive.Services;

/// <summary>
/// Result of partitioning a single repo-group's admitted releases into the set
/// rendered by default and the set hidden behind the "show older releases" toggle.
/// </summary>
/// <param name="VisibleReleases">Releases rendered by default, in render order.</param>
/// <param name="CollapsedReleases">Releases hidden behind the toggle, in render order.</param>
/// <param name="HiddenCount">Number of collapsed releases (0 when nothing is hidden).</param>
public sealed record ReleaseGroupPartition(
    IReadOnlyList<Release> VisibleReleases,
    IReadOnlyList<Release> CollapsedReleases,
    int HiddenCount);

/// <summary>
/// Pure helper that decides which releases of a repo-group are shown by default
/// and which are collapsed behind a per-group toggle on the Releases page.
/// </summary>
/// <remarks>
/// The helper holds no state and always returns the same partition for a given
/// admitted set: the owning component tracks per-group expansion and decides whether
/// to render <see cref="ReleaseGroupPartition.CollapsedReleases"/>.
/// </remarks>
public static class ReleaseVisibilityService
{
    /// <summary>
    /// Maximum number of most-recently-released (dated) releases shown per repo-group
    /// before the remainder is collapsed.
    /// </summary>
    public const int VisibleRecentReleasedCount = 12;

    /// <summary>
    /// Partitions a single repo-group's admitted releases into visible and collapsed sets.
    /// The partition is independent of any expansion state — expansion only controls
    /// whether the caller renders the collapsed set.
    /// </summary>
    /// <param name="groupReleases">
    /// Admitted releases for one repo-group (already filtered by the active repo and status filters).
    /// </param>
    /// <returns>The visible/collapsed partition for the group.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="groupReleases"/> is <c>null</c>.</exception>
    public static ReleaseGroupPartition PartitionGroup(IEnumerable<Release> groupReleases)
    {
        ArgumentNullException.ThrowIfNull(groupReleases);

        var admitted = groupReleases.ToList();

        // Planning (non-Released) releases are always visible, newest-created first.
        var planning = admitted
            .Where(r => r.Status != ReleaseStatus.Released)
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        // Released releases with a ReleasedAt date participate in the recency sort + cap.
        var datedReleased = admitted
            .Where(r => r.Status == ReleaseStatus.Released && r.ReleasedAt.HasValue)
            .OrderByDescending(r => r.ReleasedAt!.Value)
            .ThenByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        // Released releases without a ReleasedAt date are never part of the recent set.
        var undatedReleased = admitted
            .Where(r => r.Status == ReleaseStatus.Released && !r.ReleasedAt.HasValue)
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        var recent = datedReleased.Take(VisibleRecentReleasedCount).ToList();
        var older = datedReleased.Skip(VisibleRecentReleasedCount).ToList();

        var visible = new List<Release>(planning.Count + recent.Count);
        visible.AddRange(planning);
        visible.AddRange(recent);

        var collapsed = new List<Release>(older.Count + undatedReleased.Count);
        collapsed.AddRange(older);
        collapsed.AddRange(undatedReleased);

        return new ReleaseGroupPartition(visible, collapsed, collapsed.Count);
    }
}
