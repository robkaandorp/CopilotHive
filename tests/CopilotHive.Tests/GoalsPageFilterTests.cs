using CopilotHive.Goals;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the repository-filter logic used by the Goals Blazor page.
/// Verifies the same predicates and computed-list logic as Goals.razor without
/// requiring bUnit or a live browser environment.
/// </summary>
public sealed class GoalsPageFilterTests
{
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
    /// Computes the sorted, deduplicated repository list exactly as
    /// <c>Goals.razor _repositories</c> does.
    /// </summary>
    private static List<string> ComputeRepositories(List<Goal> goals) =>
        goals
            .SelectMany(g => g.RepositoryNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
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
}
