using CopilotHive.Goals;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the repository-filter logic used by the Goals Blazor page.
/// Verifies the same predicates and computed-list logic as Goals.razor without
/// requiring bUnit or a live browser environment.
/// </summary>
public sealed class GoalsPageFilterTests
{
    // ── filter constants that mirror Goals.razor ──────────────────────────────

    private const string ReleaseFilterUnreleased = "__unreleased__";
    private const string ReleaseFilterAll = "";

    // ── helpers that mirror Goals.razor private helpers ──────────────────────

    /// <summary>
    /// Applies the repository filter exactly as <c>Goals.razor ApplyFilters</c> does.
    /// </summary>
    private static List<Goal> ApplyRepositoryFilter(List<Goal> goals, string repositoryFilter)
    {
        IEnumerable<Goal> query = goals;
        if (!string.IsNullOrWhiteSpace(repositoryFilter))
            query = query.Where(g => g.RepositoryNames.Contains(repositoryFilter, StringComparer.OrdinalIgnoreCase));
        return query.ToList();
    }

    /// <summary>
    /// Applies the release filter exactly as <c>Goals.razor ApplyFilters</c> does.
    /// </summary>
    private static List<Goal> ApplyReleaseFilter(
        List<Goal> goals,
        string releaseFilter,
        Dictionary<string, ReleaseStatus> releaseStatusById)
    {
        IEnumerable<Goal> query = goals;

        if (releaseFilter == ReleaseFilterUnreleased)
        {
            query = query.Where(g =>
                string.IsNullOrEmpty(g.ReleaseId) ||
                (releaseStatusById.TryGetValue(g.ReleaseId, out var releaseStatus) && releaseStatus == ReleaseStatus.Planning));
        }
        else if (releaseFilter != ReleaseFilterAll)
        {
            query = query.Where(g => g.ReleaseId == releaseFilter);
        }

        return query.ToList();
    }

    /// <summary>
    /// Computes the sorted, deduplicated repository list exactly as
    /// <c>Goals.razor _repositories</c> does.
    /// </summary>
    private static List<string> ComputeRepositories(List<Goal> goals) =>
        goals
            .SelectMany(g => g.RepositoryNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Computes the Released releases list exactly as <c>Goals.razor _releasedReleases</c> does.
    /// </summary>
    private static List<Release> ComputeReleasedReleases(List<Release> releases) =>
        releases
            .Where(r => r.Status == ReleaseStatus.Released)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

    // ── _repositories computed list ───────────────────────────────────────────

    [Fact]
    public void Repositories_EmptyGoalList_ReturnsEmpty()
    {
        var repos = ComputeRepositories([]);

        Assert.Empty(repos);
    }

    [Fact]
    public void Repositories_GoalsWithNoRepositoryNames_ReturnsEmpty()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "no repos" },
        };

        var repos = ComputeRepositories(goals);

        Assert.Empty(repos);
    }

    [Fact]
    public void Repositories_ReturnsDeduplicatedSortedList()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", RepositoryNames = ["zeta", "alpha"] },
            new() { Id = "g2", Description = "d", RepositoryNames = ["beta", "alpha"] },
        };

        var repos = ComputeRepositories(goals);

        Assert.Equal(["alpha", "beta", "zeta"], repos);
    }

    [Fact]
    public void Repositories_DedupliesCaseInsensitively()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", RepositoryNames = ["MyRepo"] },
            new() { Id = "g2", Description = "d", RepositoryNames = ["myrepo", "MYREPO"] },
        };

        var repos = ComputeRepositories(goals);

        Assert.Single(repos);
    }

    // ── repository filter ─────────────────────────────────────────────────────

    [Fact]
    public void RepositoryFilter_EmptyString_ReturnsAllGoals()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", RepositoryNames = ["repo-a"] },
            new() { Id = "g2", Description = "d", RepositoryNames = ["repo-b"] },
        };

        var result = ApplyRepositoryFilter(goals, "");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void RepositoryFilter_WhitespaceString_ReturnsAllGoals()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", RepositoryNames = ["repo-a"] },
        };

        var result = ApplyRepositoryFilter(goals, "   ");

        Assert.Single(result);
    }

    [Fact]
    public void RepositoryFilter_MatchingRepo_ReturnsOnlyMatchingGoals()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", RepositoryNames = ["repo-a"] },
            new() { Id = "g2", Description = "d", RepositoryNames = ["repo-b"] },
            new() { Id = "g3", Description = "d", RepositoryNames = ["repo-a", "repo-b"] },
        };

        var result = ApplyRepositoryFilter(goals, "repo-a");

        Assert.Equal(2, result.Count);
        Assert.All(result, g => Assert.Contains("repo-a", g.RepositoryNames, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void RepositoryFilter_CaseInsensitiveMatch()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", RepositoryNames = ["MyRepo"] },
            new() { Id = "g2", Description = "d", RepositoryNames = ["other-repo"] },
        };

        var result = ApplyRepositoryFilter(goals, "myrepo");

        Assert.Single(result);
        Assert.Equal("g1", result[0].Id);
    }

    [Fact]
    public void RepositoryFilter_NoMatch_ReturnsEmpty()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", RepositoryNames = ["repo-a"] },
        };

        var result = ApplyRepositoryFilter(goals, "nonexistent");

        Assert.Empty(result);
    }

    [Fact]
    public void RepositoryFilter_GoalWithNoRepositories_ExcludedFromResult()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", RepositoryNames = ["repo-a"] },
            new() { Id = "g2", Description = "d" }, // no repos
        };

        var result = ApplyRepositoryFilter(goals, "repo-a");

        Assert.Single(result);
        Assert.Equal("g1", result[0].Id);
    }

    // ── _releasedReleases computed list ───────────────────────────────────────

    [Fact]
    public void ReleasedReleases_EmptyList_ReturnsEmpty()
    {
        var result = ComputeReleasedReleases([]);

        Assert.Empty(result);
    }

    [Fact]
    public void ReleasedReleases_OnlyPlanningReleases_ReturnsEmpty()
    {
        var releases = new List<Release>
        {
            new() { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Planning },
            new() { Id = "v1.1.0", Tag = "v1.1.0", Status = ReleaseStatus.Planning },
        };

        var result = ComputeReleasedReleases(releases);

        Assert.Empty(result);
    }

    [Fact]
    public void ReleasedReleases_MixedStatuses_ReturnsOnlyReleased()
    {
        var releases = new List<Release>
        {
            new() { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Released, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { Id = "v1.1.0", Tag = "v1.1.0", Status = ReleaseStatus.Planning, CreatedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { Id = "v2.0.0", Tag = "v2.0.0", Status = ReleaseStatus.Released, CreatedAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
        };

        var result = ComputeReleasedReleases(releases);

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(ReleaseStatus.Released, r.Status));
    }

    [Fact]
    public void ReleasedReleases_OrderedDescendingByCreatedAt()
    {
        var releases = new List<Release>
        {
            new() { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Released, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { Id = "v3.0.0", Tag = "v3.0.0", Status = ReleaseStatus.Released, CreatedAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { Id = "v2.0.0", Tag = "v2.0.0", Status = ReleaseStatus.Released, CreatedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
        };

        var result = ComputeReleasedReleases(releases);

        Assert.Equal(["v3.0.0", "v2.0.0", "v1.0.0"], result.Select(r => r.Id).ToList());
    }

    // ── release filter: Unreleased ────────────────────────────────────────────

    [Fact]
    public void ReleaseFilter_Unreleased_GoalWithNullReleaseId_IsIncluded()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", ReleaseId = null },
        };

        var result = ApplyReleaseFilter(goals, ReleaseFilterUnreleased, []);

        Assert.Single(result);
    }

    [Fact]
    public void ReleaseFilter_Unreleased_GoalWithEmptyReleaseId_IsIncluded()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", ReleaseId = "" },
        };

        var result = ApplyReleaseFilter(goals, ReleaseFilterUnreleased, []);

        Assert.Single(result);
    }

    [Fact]
    public void ReleaseFilter_Unreleased_GoalWithPlanningRelease_IsIncluded()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", ReleaseId = "v1.0.0" },
        };
        var releaseStatusById = new Dictionary<string, ReleaseStatus>
        {
            ["v1.0.0"] = ReleaseStatus.Planning,
        };

        var result = ApplyReleaseFilter(goals, ReleaseFilterUnreleased, releaseStatusById);

        Assert.Single(result);
    }

    [Fact]
    public void ReleaseFilter_Unreleased_GoalWithReleasedRelease_IsExcluded()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", ReleaseId = "v1.0.0" },
        };
        var releaseStatusById = new Dictionary<string, ReleaseStatus>
        {
            ["v1.0.0"] = ReleaseStatus.Released,
        };

        var result = ApplyReleaseFilter(goals, ReleaseFilterUnreleased, releaseStatusById);

        Assert.Empty(result);
    }

    [Fact]
    public void ReleaseFilter_Unreleased_MixedGoals_ReturnsOnlyUnreleased()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "no release", ReleaseId = null },
            new() { Id = "g2", Description = "planning release", ReleaseId = "v1.0.0" },
            new() { Id = "g3", Description = "released release", ReleaseId = "v0.9.0" },
        };
        var releaseStatusById = new Dictionary<string, ReleaseStatus>
        {
            ["v1.0.0"] = ReleaseStatus.Planning,
            ["v0.9.0"] = ReleaseStatus.Released,
        };

        var result = ApplyReleaseFilter(goals, ReleaseFilterUnreleased, releaseStatusById);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, g => g.Id == "g1");
        Assert.Contains(result, g => g.Id == "g2");
    }

    [Fact]
    public void ReleaseFilter_Unreleased_GoalWithUnknownReleaseId_IsExcluded()
    {
        // A goal referencing a release that doesn't exist in the status map is not Planning → excluded.
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", ReleaseId = "unknown-release" },
        };

        var result = ApplyReleaseFilter(goals, ReleaseFilterUnreleased, []);

        Assert.Empty(result);
    }

    // ── release filter: All releases ──────────────────────────────────────────

    [Fact]
    public void ReleaseFilter_All_ReturnsAllGoals()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", ReleaseId = null },
            new() { Id = "g2", Description = "d", ReleaseId = "v1.0.0" },
            new() { Id = "g3", Description = "d", ReleaseId = "v0.9.0" },
        };
        var releaseStatusById = new Dictionary<string, ReleaseStatus>
        {
            ["v1.0.0"] = ReleaseStatus.Planning,
            ["v0.9.0"] = ReleaseStatus.Released,
        };

        var result = ApplyReleaseFilter(goals, ReleaseFilterAll, releaseStatusById);

        Assert.Equal(3, result.Count);
    }

    // ── release filter: specific release ID ──────────────────────────────────

    [Fact]
    public void ReleaseFilter_SpecificId_ReturnsOnlyMatchingGoals()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", ReleaseId = "v1.0.0" },
            new() { Id = "g2", Description = "d", ReleaseId = "v2.0.0" },
            new() { Id = "g3", Description = "d", ReleaseId = null },
        };

        var result = ApplyReleaseFilter(goals, "v1.0.0", []);

        Assert.Single(result);
        Assert.Equal("g1", result[0].Id);
    }

    [Fact]
    public void ReleaseFilter_SpecificId_NoMatch_ReturnsEmpty()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", ReleaseId = "v2.0.0" },
        };

        var result = ApplyReleaseFilter(goals, "v1.0.0", []);

        Assert.Empty(result);
    }

    [Fact]
    public void ReleaseFilter_SpecificId_GoalsWithNullReleaseId_AreExcluded()
    {
        var goals = new List<Goal>
        {
            new() { Id = "g1", Description = "d", ReleaseId = null },
            new() { Id = "g2", Description = "d", ReleaseId = "v1.0.0" },
        };

        var result = ApplyReleaseFilter(goals, "v1.0.0", []);

        Assert.Single(result);
        Assert.Equal("g2", result[0].Id);
    }
}
