using CopilotHive.Goals;

namespace CopilotHive.Tests;

public sealed class GoalTests
{
    // ── ResolveTargetRepositoryNames ─────────────────────────────────────

    [Fact]
    public void ResolveTargetRepositoryNames_Null_ReturnsAllRepositories()
    {
        var result = Goal.ResolveTargetRepositoryNames(null, ["repo-a", "repo-b"]);

        Assert.Equal(["repo-a", "repo-b"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_EmptyString_ReturnsAllRepositories()
    {
        var result = Goal.ResolveTargetRepositoryNames("", ["repo-a", "repo-b"]);

        Assert.Equal(["repo-a", "repo-b"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_WhitespaceOnly_ReturnsAllRepositories()
    {
        var result = Goal.ResolveTargetRepositoryNames("   ,  , ", ["repo-a", "repo-b"]);

        Assert.Equal(["repo-a", "repo-b"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_NullAndEmptyRepositories_ReturnsEmptyList()
    {
        var result = Goal.ResolveTargetRepositoryNames(null, []);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_NonEmptyAndEmptyRepositories_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Goal.ResolveTargetRepositoryNames("repo-a", []));
    }

    [Fact]
    public void ResolveTargetRepositoryNames_ValidEntries_ReturnsCanonicalInInputOrder()
    {
        var result = Goal.ResolveTargetRepositoryNames("repo-b, repo-a", ["repo-a", "repo-b", "repo-c"]);

        Assert.Equal(["repo-b", "repo-a"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_CaseInsensitiveMatch_UsesCanonicalSpelling()
    {
        var result = Goal.ResolveTargetRepositoryNames("REPO-B, repo-a", ["Repo-A", "Repo-B"]);

        Assert.Equal(["Repo-B", "Repo-A"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_Deduplicates()
    {
        var result = Goal.ResolveTargetRepositoryNames("repo-a, repo-a, repo-b", ["repo-a", "repo-b"]);

        Assert.Equal(["repo-a", "repo-b"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_UnknownEntry_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Goal.ResolveTargetRepositoryNames("repo-x", ["repo-a", "repo-b"]));
    }

    [Fact]
    public void ResolveTargetRepositoryNames_TrimsEntries()
    {
        var result = Goal.ResolveTargetRepositoryNames("  repo-a , repo-b  ", ["repo-a", "repo-b"]);

        Assert.Equal(["repo-a", "repo-b"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_MalformedCommaOnly_ReturnsAllRepos()
    {
        // Malformed "," (just a comma, zero non-empty entries) → all repos, no exception.
        var result = Goal.ResolveTargetRepositoryNames(",", ["A", "B"]);

        Assert.Equal(["A", "B"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_SpecExample_CanonicalDeduplicatedInInputOrder()
    {
        // Spec example: "a, A, B" with repos ["A","B"] → ["A","B"] (canonical, deduplicated, target input order).
        // "a" matches canonical "A", then "A" is a duplicate of "a", then "B" is new.
        var result = Goal.ResolveTargetRepositoryNames("a, A, B", ["A", "B"]);

        Assert.Equal(["A", "B"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_SingleRepoNull_AllReposTargeted()
    {
        // Backward compatibility: single-repo with null targets → that one repo is targeted.
        var result = Goal.ResolveTargetRepositoryNames(null, ["only-repo"]);

        Assert.Equal(["only-repo"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_MultiRepoNull_AllReposTargeted()
    {
        // Multi-repo with null targets → all repos targeted.
        var result = Goal.ResolveTargetRepositoryNames(null, ["repo-1", "repo-2", "repo-3"]);

        Assert.Equal(["repo-1", "repo-2", "repo-3"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_MultiRepoExplicitOne_OnlyThatTarget()
    {
        // Multi-repo explicit (one entry) → only that one target returned.
        var result = Goal.ResolveTargetRepositoryNames("repo-2", ["repo-1", "repo-2", "repo-3"]);

        Assert.Equal(["repo-2"], result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_EmptyReposAndMalformedTargets_ReturnsEmptyList()
    {
        // Empty RepositoryNames + null/malformed targets → empty list (no exception).
        var result = Goal.ResolveTargetRepositoryNames(",", []);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveTargetRepositoryNames_EmptyReposAndNonEmptyTargets_Throws()
    {
        // Empty RepositoryNames + non-empty targets → ArgumentException.
        Assert.Throws<ArgumentException>(() =>
            Goal.ResolveTargetRepositoryNames("repo-a", []));
    }

    [Fact]
    public void ResolveTargetRepositoryNames_InvalidTarget_ThrowsArgumentException()
    {
        // Invalid target (entry not in RepositoryNames) → ArgumentException.
        Assert.Throws<ArgumentException>(() =>
            Goal.ResolveTargetRepositoryNames("repo-x", ["A", "B"]));
    }

    // ── ApiGoalSource ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiGoalSource_AddAndGetPending()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "g1", Description = "First goal" });
        source.AddGoal(new Goal { Id = "g2", Description = "Second goal" });

        var pending = await source.GetPendingGoalsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public async Task ApiGoalSource_UpdateStatus_RemovesFromPending()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "g1", Description = "Goal one" });

        await source.UpdateGoalStatusAsync("g1", GoalStatus.Completed, ct: TestContext.Current.CancellationToken);

        var pending = await source.GetPendingGoalsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(pending);

        var all = source.GetAllGoals();
        Assert.Single(all);
        Assert.Equal(GoalStatus.Completed, all[0].Status);
    }

    [Fact]
    public async Task ApiGoalSource_DuplicateId_Throws()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "dup", Description = "First" });

        Assert.Throws<InvalidOperationException>(
            () => source.AddGoal(new Goal { Id = "dup", Description = "Second" }));

        var pending = await source.GetPendingGoalsAsync(TestContext.Current.CancellationToken);
        Assert.Single(pending);
    }

    // ── GoalManager ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GoalManager_GetNextGoal_ReturnsHighestPriority()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "low", Description = "Low", Priority = GoalPriority.Low });
        source.AddGoal(new Goal { Id = "critical", Description = "Critical", Priority = GoalPriority.Critical });
        source.AddGoal(new Goal { Id = "high", Description = "High", Priority = GoalPriority.High });

        var manager = new GoalManager();
        manager.AddSource(source);

        var next = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(next);
        Assert.Equal("critical", next.Id);
    }

    [Fact]
    public async Task GoalManager_CompleteGoal_UpdatesStatus()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "task-1", Description = "Do something" });

        var manager = new GoalManager();
        manager.AddSource(source);

        // GetNextGoalAsync registers the goal in the source map
        var goal = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(goal);

        await manager.CompleteGoalAsync("task-1", ct: TestContext.Current.CancellationToken);

        Assert.Equal(GoalStatus.Completed, source.GetGoal("task-1")!.Status);
    }

    [Fact]
    public async Task GoalManager_FailGoal_UpdatesStatus()
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "task-2", Description = "Will fail" });

        var manager = new GoalManager();
        manager.AddSource(source);

        _ = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);
        await manager.FailGoalAsync("task-2", "tests failed", ct: TestContext.Current.CancellationToken);

        Assert.Equal(GoalStatus.Failed, source.GetGoal("task-2")!.Status);
    }

    [Fact]
    public async Task GoalManager_NoGoals_ReturnsNull()
    {
        var manager = new GoalManager();
        manager.AddSource(new ApiGoalSource());

        var next = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);
        Assert.Null(next);
    }

    [Fact]
    public async Task GoalManager_MultipleSources_PrioritizesAcross()
    {
        var fileSource = new ApiGoalSource();
        fileSource.AddGoal(new Goal { Id = "file-high", Description = "File high", Priority = GoalPriority.High });

        var apiSource = new ApiGoalSource();
        apiSource.AddGoal(new Goal { Id = "api-critical", Description = "API critical", Priority = GoalPriority.Critical });

        var manager = new GoalManager();
        manager.AddSource(fileSource);
        manager.AddSource(apiSource);

        var next = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(next);
        Assert.Equal("api-critical", next.Id);
    }

    // ── Priority ordering ────────────────────────────────────────────────────

    [Theory]
    [InlineData(GoalPriority.Critical, GoalPriority.High)]
    [InlineData(GoalPriority.High, GoalPriority.Normal)]
    [InlineData(GoalPriority.Normal, GoalPriority.Low)]
    [InlineData(GoalPriority.Critical, GoalPriority.Low)]
    public async Task PriorityOrdering_HigherPrioritySelected(GoalPriority higher, GoalPriority lower)
    {
        var source = new ApiGoalSource();
        source.AddGoal(new Goal { Id = "lower", Description = "Lower", Priority = lower });
        source.AddGoal(new Goal { Id = "higher", Description = "Higher", Priority = higher });

        var manager = new GoalManager();
        manager.AddSource(source);

        var next = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(next);
        Assert.Equal("higher", next.Id);
    }

}