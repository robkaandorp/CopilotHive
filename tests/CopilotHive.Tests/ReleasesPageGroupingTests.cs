using CopilotHive.Goals;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the grouping logic used by the Releases Blazor page
/// (<c>GetGroupedReleases()</c>). Tests verify repo-filter behaviour without
/// requiring bUnit or a live browser environment.
/// </summary>
public sealed class ReleasesPageGroupingTests
{
    // ── Helper that mirrors Releases.razor GetGroupedReleases() ──────────────

    /// <summary>
    /// Groups filtered releases by repository, honouring the active repo filter.
    /// When <paramref name="repoFilter"/> is non-empty, each release is placed
    /// only under the matching repo group — never under sibling repos.
    /// </summary>
    private static IEnumerable<IGrouping<string, Release>> GetGroupedReleases(
        List<Release> filtered, string repoFilter)
    {
        var activeFilter = string.IsNullOrWhiteSpace(repoFilter) ? null : repoFilter;

        return filtered
            .SelectMany(r =>
            {
                if (activeFilter is not null)
                {
                    return r.RepositoryNames
                        .Where(name => string.Equals(name, activeFilter, StringComparison.OrdinalIgnoreCase))
                        .Select(name => (Key: name, Release: r));
                }

                return r.RepositoryNames.Count > 0
                    ? r.RepositoryNames.Select(name => (Key: name, Release: r))
                    : [(Key: "", Release: r)];
            })
            .GroupBy(pair => pair.Key, pair => pair.Release, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
    }

    // ── No filter — all-repo grouping ─────────────────────────────────────────

    [Fact]
    public void NoFilter_SingleRepoRelease_AppearsUnderItsRepo()
    {
        var releases = new List<Release>
        {
            new() { Id = "v1.0", Tag = "v1.0", RepositoryNames = ["repo-a"] },
        };

        var groups = GetGroupedReleases(releases, "").ToList();

        Assert.Single(groups);
        Assert.Equal("repo-a", groups[0].Key);
        Assert.Single(groups[0]);
    }

    [Fact]
    public void NoFilter_MultiRepoRelease_AppearsUnderBothGroups()
    {
        var release = new Release { Id = "v1.0", Tag = "v1.0", RepositoryNames = ["repo-a", "repo-b"] };
        var releases = new List<Release> { release };

        var groups = GetGroupedReleases(releases, "").ToList();

        Assert.Equal(2, groups.Count);
        var keys = groups.Select(g => g.Key).ToHashSet();
        Assert.Contains("repo-a", keys);
        Assert.Contains("repo-b", keys);
    }

    [Fact]
    public void NoFilter_ReleaseWithNoRepos_AppearsUnderEmptyGroup()
    {
        var release = new Release { Id = "v1.0", Tag = "v1.0", RepositoryNames = [] };
        var releases = new List<Release> { release };

        var groups = GetGroupedReleases(releases, "").ToList();

        Assert.Single(groups);
        Assert.Equal("", groups[0].Key);
        Assert.Single(groups[0]);
    }

    // ── Active repo filter ────────────────────────────────────────────────────

    [Fact]
    public void WithFilter_MultiRepoRelease_OnlyAppearsUnderFilteredRepo()
    {
        // A release belonging to both repo-a and repo-b, filtered by repo-a —
        // must appear ONLY under repo-a, not under repo-b.
        var release = new Release { Id = "v1.0", Tag = "v1.0", RepositoryNames = ["repo-a", "repo-b"] };
        var releases = new List<Release> { release };

        var groups = GetGroupedReleases(releases, "repo-a").ToList();

        Assert.Single(groups);
        Assert.Equal("repo-a", groups[0].Key);
        Assert.Single(groups[0]);
        Assert.Equal("v1.0", groups[0].First().Id);
    }

    [Fact]
    public void WithFilter_ReleaseNotMatchingFilter_DoesNotAppearInAnyGroup()
    {
        // The ApplyFilters method already excludes non-matching releases from _filtered,
        // but even if one slips through, GetGroupedReleases must not create a spurious group.
        var release = new Release { Id = "v2.0", Tag = "v2.0", RepositoryNames = ["repo-b"] };
        var releases = new List<Release> { release };

        // Simulate filtering by repo-a while the list still contains a repo-b release
        var groups = GetGroupedReleases(releases, "repo-a").ToList();

        Assert.Empty(groups);
    }

    [Fact]
    public void WithFilter_MultipleReleasesMatchingFilter_AllAppearUnderFilteredGroup()
    {
        var r1 = new Release { Id = "v1.0", Tag = "v1.0", RepositoryNames = ["repo-a"] };
        var r2 = new Release { Id = "v2.0", Tag = "v2.0", RepositoryNames = ["repo-a", "repo-b"] };
        var releases = new List<Release> { r1, r2 };

        var groups = GetGroupedReleases(releases, "repo-a").ToList();

        Assert.Single(groups);
        Assert.Equal("repo-a", groups[0].Key);
        Assert.Equal(2, groups[0].Count());
    }

    [Fact]
    public void WithFilter_CaseInsensitiveMatch()
    {
        var release = new Release { Id = "v1.0", Tag = "v1.0", RepositoryNames = ["MyRepo"] };
        var releases = new List<Release> { release };

        var groups = GetGroupedReleases(releases, "myrepo").ToList();

        Assert.Single(groups);
        // Key comes from the stored name, not the filter value
        Assert.Equal("MyRepo", groups[0].Key, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithFilter_EmptyFilteredList_ReturnsEmpty()
    {
        var groups = GetGroupedReleases([], "repo-a").ToList();
        Assert.Empty(groups);
    }

    [Fact]
    public void WithFilter_GroupsOrderedAlphabetically()
    {
        // Even with a filter there should only be one group, but verify ordering is stable.
        var r1 = new Release { Id = "v1.0", Tag = "v1.0", RepositoryNames = ["repo-a"] };
        var r2 = new Release { Id = "v2.0", Tag = "v2.0", RepositoryNames = ["repo-a"] };
        var releases = new List<Release> { r2, r1 };

        var groups = GetGroupedReleases(releases, "repo-a").ToList();

        Assert.Single(groups);
        Assert.Equal("repo-a", groups[0].Key);
    }

    // ── Whitespace filter treated as no-filter ────────────────────────────────

    [Fact]
    public void WhitespaceFilter_TreatedAsNoFilter_MultiRepoReleaseSpreadsAcrossGroups()
    {
        var release = new Release { Id = "v1.0", Tag = "v1.0", RepositoryNames = ["repo-a", "repo-b"] };
        var releases = new List<Release> { release };

        var groups = GetGroupedReleases(releases, "   ").ToList();

        // Whitespace filter = no filter → release appears under both groups
        Assert.Equal(2, groups.Count);
    }
}
