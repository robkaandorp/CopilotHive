using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Goals;

/// <summary>
/// Integration tests for <see cref="GoalStore"/> exercising the full EF Core-backed
/// implementation through real <see cref="CopilotHiveDbContext"/> in-memory SQLite instances.
/// </summary>
public sealed class GoalStoreIntegrationTests
{
    private static GoalStore CreateStore(CopilotHiveDbContext db)
        => new(db, NullLogger<GoalStore>.Instance);

    // ═══════════════════════════════════════════════════════════════════════
    // (1) Goal CRUD round-trip through DbContext
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateGoalAsync_NewGoal_PersistsAndRetrievesAllFields()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "test-goal-1",
            Description = "Test goal for CRUD round-trip",
            Priority = GoalPriority.High,
            Scope = GoalScope.Feature,
            Status = GoalStatus.Pending,
            RepositoryNames = ["CopilotHive"],
            DependsOn = ["other-goal"],
            Metadata = new() { ["key"] = "value" },
            Notes = ["initial note"],
            Documents = ["doc-1", "doc-2"],
            ReleaseId = "rel-x",
            BranchCleanedUp = false,
            ReviewStatus = ReviewStatus.Approved,
        };

        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var retrieved = await store.GetGoalAsync("test-goal-1", TestContext.Current.CancellationToken);

        Assert.NotNull(retrieved);
        Assert.Equal("test-goal-1", retrieved!.Id);
        Assert.Equal("Test goal for CRUD round-trip", retrieved.Description);
        Assert.Equal(GoalPriority.High, retrieved.Priority);
        Assert.Equal(GoalScope.Feature, retrieved.Scope);
        Assert.Equal(GoalStatus.Pending, retrieved.Status);
        Assert.Equal(["CopilotHive"], retrieved.RepositoryNames);
        Assert.Equal(["other-goal"], retrieved.DependsOn);
        Assert.Equal("value", retrieved.Metadata["key"]);
        Assert.Equal(goal.CreatedAt, retrieved.CreatedAt);
        Assert.Equal(["initial note"], retrieved.Notes);
        Assert.Equal(["doc-1", "doc-2"], retrieved.Documents);
        Assert.Equal("rel-x", retrieved.ReleaseId);
        Assert.False(retrieved.BranchCleanedUp);
        Assert.Equal(ReviewStatus.Approved, retrieved.ReviewStatus);
    }

    [Fact]
    public async Task CreateGoalAsync_GoalWithoutReviewStatus_DefaultsToNone()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "default-review-status",
            Description = "Goal without explicit ReviewStatus",
        };

        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var retrieved = await store.GetGoalAsync("default-review-status", TestContext.Current.CancellationToken);

        Assert.NotNull(retrieved);
        Assert.Equal(ReviewStatus.None, retrieved!.ReviewStatus);
    }

    [Fact]
    public async Task GetGoalAsync_NonExistentId_ReturnsNull()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var result = await store.GetGoalAsync("does-not-exist", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllGoalsAsync_MultipleGoals_ReturnsAllOrderedByCreatedAtDescending()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var oldest = new Goal { Id = "goal-old", Description = "Oldest", CreatedAt = DateTime.UtcNow.AddMinutes(-10) };
        var newest = new Goal { Id = "goal-new", Description = "Newest", CreatedAt = DateTime.UtcNow };
        var middle = new Goal { Id = "goal-mid", Description = "Middle", CreatedAt = DateTime.UtcNow.AddMinutes(-5) };

        await store.CreateGoalAsync(oldest, TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(middle, TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(newest, TestContext.Current.CancellationToken);

        var all = await store.GetAllGoalsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, all.Count);
        Assert.Equal("goal-new", all[0].Id);
        Assert.Equal("goal-mid", all[1].Id);
        Assert.Equal("goal-old", all[2].Id);
    }

    [Fact]
    public async Task UpdateGoalAsync_ExistingGoal_UpdatesAllMutableFields()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "update-goal-1",
            Description = "Original description",
            Priority = GoalPriority.Normal,
            Scope = GoalScope.Patch,
            Status = GoalStatus.Pending,
        };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        // Retrieve, mutate, and update
        var toUpdate = await store.GetGoalAsync("update-goal-1", TestContext.Current.CancellationToken);
        Assert.NotNull(toUpdate);

        toUpdate!.Description = "Updated description";
        toUpdate.Priority = GoalPriority.Critical;
        toUpdate.Scope = GoalScope.Breaking;
        toUpdate.Status = GoalStatus.InProgress;
        toUpdate.RepositoryNames = ["repo-a", "repo-b"];
        toUpdate.DependsOn = ["dep-1"];
        toUpdate.StartedAt = DateTime.UtcNow;
        toUpdate.CompletedAt = DateTime.UtcNow.AddHours(1);
        toUpdate.Iterations = 2;
        toUpdate.FailureReason = "something broke";
        toUpdate.Notes = ["note-1", "note-2"];
        toUpdate.PhaseDurations = new() { ["Coding"] = 120.5, ["Testing"] = 60.0 };
        toUpdate.TotalDurationSeconds = 180.5;
        toUpdate.MergeCommitHash = "abc123";
        toUpdate.ReleaseId = "rel-updated";
        toUpdate.Documents = ["doc-updated"];
        toUpdate.BranchCleanedUp = true;
        toUpdate.ReviewStatus = ReviewStatus.NeedsChanges;

        await store.UpdateGoalAsync(toUpdate, TestContext.Current.CancellationToken);

        var retrieved = await store.GetGoalAsync("update-goal-1", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Updated description", retrieved!.Description);
        Assert.Equal(GoalPriority.Critical, retrieved.Priority);
        Assert.Equal(GoalScope.Breaking, retrieved.Scope);
        Assert.Equal(GoalStatus.InProgress, retrieved.Status);
        Assert.Equal(["repo-a", "repo-b"], retrieved.RepositoryNames);
        Assert.Equal(["dep-1"], retrieved.DependsOn);
        Assert.NotNull(retrieved.StartedAt);
        Assert.NotNull(retrieved.CompletedAt);
        Assert.Equal(2, retrieved.Iterations);
        Assert.Equal("something broke", retrieved.FailureReason);
        Assert.Equal(["note-1", "note-2"], retrieved.Notes);
        Assert.NotNull(retrieved.PhaseDurations);
        Assert.Equal(120.5, retrieved.PhaseDurations!["Coding"]);
        Assert.Equal(60.0, retrieved.PhaseDurations["Testing"]);
        Assert.Equal(180.5, retrieved.TotalDurationSeconds);
        Assert.Equal("abc123", retrieved.MergeCommitHash);
        Assert.Equal("rel-updated", retrieved.ReleaseId);
        Assert.Equal(["doc-updated"], retrieved.Documents);
        Assert.True(retrieved.BranchCleanedUp);
        Assert.Equal(ReviewStatus.NeedsChanges, retrieved.ReviewStatus);
    }

    [Fact]
    public async Task UpdateGoalAsync_NonExistentGoal_ThrowsKeyNotFoundException()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal { Id = "no-such-goal", Description = "Non-existent" };

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.UpdateGoalAsync(goal, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteGoalAsync_ExistingGoal_ReturnsTrueAndRemovesGoal()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal { Id = "delete-goal-1", Description = "To be deleted" };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var deleted = await store.DeleteGoalAsync("delete-goal-1", TestContext.Current.CancellationToken);

        Assert.True(deleted);

        var retrieved = await store.GetGoalAsync("delete-goal-1", TestContext.Current.CancellationToken);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task DeleteGoalAsync_NonExistentGoal_ReturnsFalse()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var result = await store.DeleteGoalAsync("does-not-exist", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CreateGoalAsync_DuplicateId_ThrowsInvalidOperationException()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal { Id = "dup-goal-1", Description = "First" };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var duplicate = new Goal { Id = "dup-goal-1", Description = "Second" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateGoalAsync(duplicate, TestContext.Current.CancellationToken));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) IterationSummary add+get with JSON field deserialization
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddIterationAsync_NewSummary_PersistsAndRetrievesAllFields()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal { Id = "iter-goal-1", Description = "Iteration test" };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var clarificationTime = DateTime.UtcNow;
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 42.5 }],
            TestCounts = new TestCounts { Total = 10, Passed = 8, Failed = 2 },
            BuildSuccess = true,
            ReviewVerdict = "approve",
            Notes = ["All good"],
            PhaseOutputs = new() { ["coder-1"] = "output text" },
            Clarifications =
            [
                new PersistedClarification
                {
                    Timestamp = clarificationTime,
                    Phase = "Coding",
                    WorkerRole = "coder",
                    Question = "What files?",
                    Answer = "All of them",
                    AnsweredBy = "brain",
                },
            ],
        };

        await store.AddIterationAsync("iter-goal-1", summary, TestContext.Current.CancellationToken);

        var iterations = await store.GetIterationsAsync("iter-goal-1", TestContext.Current.CancellationToken);

        Assert.Single(iterations);
        var retrieved = iterations[0];
        Assert.Equal(1, retrieved.Iteration);

        // Phases
        Assert.Single(retrieved.Phases);
        Assert.Equal(GoalPhase.Coding, retrieved.Phases[0].Name);
        Assert.Equal(PhaseOutcome.Pass, retrieved.Phases[0].Result);
        Assert.Equal(42.5, retrieved.Phases[0].DurationSeconds);

        // TestCounts
        Assert.NotNull(retrieved.TestCounts);
        Assert.Equal(10, retrieved.TestCounts!.Total);
        Assert.Equal(8, retrieved.TestCounts.Passed);
        Assert.Equal(2, retrieved.TestCounts.Failed);

        // BuildSuccess
        Assert.True(retrieved.BuildSuccess);

        // ReviewVerdict
        Assert.Equal("approve", retrieved.ReviewVerdict);

        // Notes
        Assert.Equal(["All good"], retrieved.Notes);

        // PhaseOutputs
        Assert.Single(retrieved.PhaseOutputs);
        Assert.Equal("output text", retrieved.PhaseOutputs["coder-1"]);

        // Clarifications
        Assert.Single(retrieved.Clarifications);
        var clar = retrieved.Clarifications[0];
        Assert.Equal("Coding", clar.Phase);
        Assert.Equal("coder", clar.WorkerRole);
        Assert.Equal("What files?", clar.Question);
        Assert.Equal("All of them", clar.Answer);
        Assert.Equal("brain", clar.AnsweredBy);
    }

    [Fact]
    public async Task GetIterationsAsync_MultipleIterations_ReturnsOrderedByIterationNumber()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal { Id = "iter-goal-2", Description = "Multi-iteration test" };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var summary1 = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Fail }],
            BuildSuccess = false,
        };

        var summary2 = new IterationSummary
        {
            Iteration = 2,
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass }],
            BuildSuccess = true,
            ReviewVerdict = "approve",
        };

        // Add in reverse order to verify ordering by iteration number (not insertion order)
        await store.AddIterationAsync("iter-goal-2", summary2, TestContext.Current.CancellationToken);
        await store.AddIterationAsync("iter-goal-2", summary1, TestContext.Current.CancellationToken);

        var iterations = await store.GetIterationsAsync("iter-goal-2", TestContext.Current.CancellationToken);

        Assert.Equal(2, iterations.Count);
        Assert.Equal(1, iterations[0].Iteration);
        Assert.Equal(2, iterations[1].Iteration);
        Assert.Equal(PhaseOutcome.Fail, iterations[0].Phases[0].Result);
        Assert.Equal(PhaseOutcome.Pass, iterations[1].Phases[0].Result);
    }

    [Fact]
    public async Task AddIterationAsync_ReplaceExisting_SameGoalAndIterationNumber()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal { Id = "iter-goal-3", Description = "Replace test" };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var original = new IterationSummary
        {
            Iteration = 1,
            BuildSuccess = false,
            ReviewVerdict = "reject",
        };
        await store.AddIterationAsync("iter-goal-3", original, TestContext.Current.CancellationToken);

        var replacement = new IterationSummary
        {
            Iteration = 1,
            BuildSuccess = true,
            ReviewVerdict = "approve",
        };
        await store.AddIterationAsync("iter-goal-3", replacement, TestContext.Current.CancellationToken);

        var iterations = await store.GetIterationsAsync("iter-goal-3", TestContext.Current.CancellationToken);
        Assert.Single(iterations);
        Assert.True(iterations[0].BuildSuccess);
        Assert.Equal("approve", iterations[0].ReviewVerdict);
    }

    [Fact]
    public async Task GetIterationsAsync_GoalWithNoIterations_ReturnsEmptyList()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal { Id = "iter-goal-4", Description = "No iterations" };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var iterations = await store.GetIterationsAsync("iter-goal-4", TestContext.Current.CancellationToken);

        Assert.Empty(iterations);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) SearchGoalsAsync with tokenized multi-term query
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchGoalsAsync_MultiTermQuery_MatchesGoalsContainingBothTerms()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await store.CreateGoalAsync(
            new Goal { Id = "search-1", Description = "Rewrite goal store using ef core", Status = GoalStatus.Pending },
            TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(
            new Goal { Id = "search-2", Description = "Add pipeline store integration", Status = GoalStatus.Completed },
            TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(
            new Goal { Id = "search-3", Description = "EF Core migration tests", Status = GoalStatus.Failed, FailureReason = "ef core schema error" },
            TestContext.Current.CancellationToken);

        // "ef core" should match search-1 (description) and search-3 (description + failure_reason)
        var results = await store.SearchGoalsAsync("ef core", null, TestContext.Current.CancellationToken);

        var ids = results.Select(g => g.Id).ToList();
        Assert.Contains("search-1", ids);
        Assert.Contains("search-3", ids);
        Assert.DoesNotContain("search-2", ids);
    }

    [Fact]
    public async Task SearchGoalsAsync_SingleTerm_MatchesDescription()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await store.CreateGoalAsync(
            new Goal { Id = "search-pipeline", Description = "Add pipeline store integration" },
            TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(
            new Goal { Id = "search-unrelated", Description = "Unrelated work" },
            TestContext.Current.CancellationToken);

        var results = await store.SearchGoalsAsync("pipeline", null, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("search-pipeline", results[0].Id);
    }

    [Fact]
    public async Task SearchGoalsAsync_StatusFilter_OnlyReturnsMatchingStatus()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await store.CreateGoalAsync(
            new Goal { Id = "search-1", Description = "Rewrite goal store using ef core", Status = GoalStatus.Pending },
            TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(
            new Goal { Id = "search-2", Description = "Add pipeline store integration", Status = GoalStatus.Completed },
            TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(
            new Goal { Id = "search-3", Description = "EF Core migration tests", Status = GoalStatus.Failed, FailureReason = "ef core schema error" },
            TestContext.Current.CancellationToken);

        // "ef core" with status Completed should return empty (search-1 is Pending, search-3 is Failed)
        var results = await store.SearchGoalsAsync("ef core", GoalStatus.Completed, TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchGoalsAsync_NonMatchingQuery_ReturnsEmptyList()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await store.CreateGoalAsync(
            new Goal { Id = "search-xyz", Description = "Some description" },
            TestContext.Current.CancellationToken);

        var results = await store.SearchGoalsAsync("zzzznotfound", null, TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchGoalsAsync_MatchesAgainstIdField()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await store.CreateGoalAsync(
            new Goal { Id = "special-id-123", Description = "Generic description" },
            TestContext.Current.CancellationToken);

        var results = await store.SearchGoalsAsync("special", null, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("special-id-123", results[0].Id);
    }

    [Fact]
    public async Task SearchGoalsAsync_MatchesAgainstFailureReasonField()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await store.CreateGoalAsync(
            new Goal { Id = "search-fail", Description = "Generic description", FailureReason = "timeout occurred" },
            TestContext.Current.CancellationToken);

        var results = await store.SearchGoalsAsync("timeout", null, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("search-fail", results[0].Id);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) Release CRUD
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateReleaseAsync_NewRelease_PersistsAndRetrievesAllFields()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release
        {
            Id = "rel-1",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Planning,
            Notes = "Initial release",
            RepositoryNames = ["CopilotHive"],
        };

        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);

        var retrieved = await store.GetReleaseAsync("rel-1", TestContext.Current.CancellationToken);

        Assert.NotNull(retrieved);
        Assert.Equal("rel-1", retrieved!.Id);
        Assert.Equal("v1.0.0", retrieved.Tag);
        Assert.Equal(ReleaseStatus.Planning, retrieved.Status);
        Assert.Equal("Initial release", retrieved.Notes);
        Assert.Equal(["CopilotHive"], retrieved.RepositoryNames);
    }

    [Fact]
    public async Task GetReleaseAsync_NonExistentId_ReturnsNull()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var result = await store.GetReleaseAsync("no-such-release", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetReleasesAsync_MultipleReleases_ReturnsOrderedByCreatedAtDescending()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var oldest = new Release { Id = "rel-old", Tag = "v0.1.0", CreatedAt = DateTime.UtcNow.AddMinutes(-10) };
        var newest = new Release { Id = "rel-new", Tag = "v2.0.0", CreatedAt = DateTime.UtcNow };
        var middle = new Release { Id = "rel-mid", Tag = "v1.0.0", CreatedAt = DateTime.UtcNow.AddMinutes(-5) };

        await store.CreateReleaseAsync(oldest, TestContext.Current.CancellationToken);
        await store.CreateReleaseAsync(middle, TestContext.Current.CancellationToken);
        await store.CreateReleaseAsync(newest, TestContext.Current.CancellationToken);

        var all = await store.GetReleasesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, all.Count);
        Assert.Equal("rel-new", all[0].Id);
        Assert.Equal("rel-mid", all[1].Id);
        Assert.Equal("rel-old", all[2].Id);
    }

    [Fact]
    public async Task UpdateReleaseAsync_EntityOverload_UpdatesAllMutableFields()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release
        {
            Id = "rel-update-1",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Planning,
            Notes = "Original notes",
            RepositoryNames = ["repo-a"],
        };
        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);

        // RepositoryNames is init-only, so create a new Release with updated values
        var updated = new Release
        {
            Id = "rel-update-1",
            Tag = "v2.0.0",
            Status = ReleaseStatus.Released,
            Notes = "Updated notes",
            ReleasedAt = DateTime.UtcNow,
            RepositoryNames = ["repo-b", "repo-c"],
            CreatedAt = release.CreatedAt,
        };

        await store.UpdateReleaseAsync(updated, TestContext.Current.CancellationToken);

        var retrieved = await store.GetReleaseAsync("rel-update-1", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("v2.0.0", retrieved!.Tag);
        Assert.Equal(ReleaseStatus.Released, retrieved.Status);
        Assert.Equal("Updated notes", retrieved.Notes);
        Assert.NotNull(retrieved.ReleasedAt);
        Assert.Equal(["repo-b", "repo-c"], retrieved.RepositoryNames);
    }

    [Fact]
    public async Task UpdateReleaseAsync_PartialUpdate_ChangesOnlyNonNullFields()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release
        {
            Id = "rel-partial-1",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Planning,
            Notes = "Original notes",
            RepositoryNames = ["repo-a"],
        };
        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);

        var update = new ReleaseUpdateData
        {
            Tag = "v1.5.0",
            Notes = "Updated notes",
            Repositories = ["repo-x", "repo-y"],
        };

        await store.UpdateReleaseAsync("rel-partial-1", update, TestContext.Current.CancellationToken);

        var retrieved = await store.GetReleaseAsync("rel-partial-1", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("v1.5.0", retrieved!.Tag);
        Assert.Equal("Updated notes", retrieved.Notes);
        Assert.Equal(["repo-x", "repo-y"], retrieved.RepositoryNames);
        // Status and CreatedAt should be unchanged
        Assert.Equal(ReleaseStatus.Planning, retrieved.Status);
    }

    [Fact]
    public async Task UpdateReleaseAsync_PartialUpdate_OnlyChangesTag_LeavesOtherFieldsIntact()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release
        {
            Id = "rel-partial-2",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Planning,
            Notes = "Keep these notes",
            RepositoryNames = ["repo-original"],
        };
        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);

        var update = new ReleaseUpdateData { Tag = "v2.0.0" };

        await store.UpdateReleaseAsync("rel-partial-2", update, TestContext.Current.CancellationToken);

        var retrieved = await store.GetReleaseAsync("rel-partial-2", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("v2.0.0", retrieved!.Tag);
        Assert.Equal("Keep these notes", retrieved.Notes);
        Assert.Equal(["repo-original"], retrieved.RepositoryNames);
    }

    [Fact]
    public async Task UpdateReleaseAsync_PartialUpdate_NonPlanningRelease_ThrowsInvalidOperationException()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release
        {
            Id = "rel-released",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Released,
            ReleasedAt = DateTime.UtcNow,
        };
        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);

        var update = new ReleaseUpdateData { Tag = "v2.0.0" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateReleaseAsync("rel-released", update, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateReleaseAsync_PartialUpdate_NonExistentRelease_ThrowsKeyNotFoundException()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var update = new ReleaseUpdateData { Tag = "v2.0.0" };

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.UpdateReleaseAsync("no-such-release", update, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteReleaseAsync_PlanningRelease_ReturnsTrueAndRemovesRelease()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release { Id = "rel-delete-1", Tag = "v1.0.0", Status = ReleaseStatus.Planning };
        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);

        var deleted = await store.DeleteReleaseAsync("rel-delete-1", TestContext.Current.CancellationToken);

        Assert.True(deleted);
        var retrieved = await store.GetReleaseAsync("rel-delete-1", TestContext.Current.CancellationToken);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task DeleteReleaseAsync_ReleasedRelease_ReturnsFalse()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release
        {
            Id = "rel-delete-2",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Released,
            ReleasedAt = DateTime.UtcNow,
        };
        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);

        var deleted = await store.DeleteReleaseAsync("rel-delete-2", TestContext.Current.CancellationToken);

        Assert.False(deleted);
        // Release should still exist
        var retrieved = await store.GetReleaseAsync("rel-delete-2", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
    }

    [Fact]
    public async Task DeleteReleaseAsync_NonExistentRelease_ReturnsFalse()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var result = await store.DeleteReleaseAsync("no-such-release", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task GetGoalsByReleaseAsync_ReturnsOnlyGoalsAssignedToRelease()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release { Id = "rel-goals", Tag = "v1.0.0", Status = ReleaseStatus.Planning };
        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);

        await store.CreateGoalAsync(
            new Goal { Id = "goal-assigned", Description = "Assigned to release", ReleaseId = "rel-goals" },
            TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(
            new Goal { Id = "goal-unassigned", Description = "Not assigned to any release" },
            TestContext.Current.CancellationToken);

        var goals = await store.GetGoalsByReleaseAsync("rel-goals", TestContext.Current.CancellationToken);

        Assert.Single(goals);
        Assert.Equal("goal-assigned", goals[0].Id);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) GetAllClarificationsAsync
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetAllClarificationsAsync_MultipleGoals_ReturnsAllClarificationsWithCorrectGoalIds()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var time1 = DateTime.UtcNow.AddMinutes(-10);
        var time2 = DateTime.UtcNow.AddMinutes(-5);
        var time3 = DateTime.UtcNow;

        await store.CreateGoalAsync(new Goal { Id = "clar-goal-1", Description = "Goal 1" }, TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(new Goal { Id = "clar-goal-2", Description = "Goal 2" }, TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(new Goal { Id = "clar-goal-3", Description = "Goal 3 (no clarifications)" }, TestContext.Current.CancellationToken);

        await store.AddIterationAsync("clar-goal-1", new IterationSummary
        {
            Iteration = 1,
            Clarifications =
            [
                new PersistedClarification { Timestamp = time1, Phase = "Coding", WorkerRole = "coder", Question = "Q1", Answer = "A1", AnsweredBy = "brain" },
            ],
        }, TestContext.Current.CancellationToken);

        await store.AddIterationAsync("clar-goal-2", new IterationSummary
        {
            Iteration = 1,
            Clarifications =
            [
                new PersistedClarification { Timestamp = time2, Phase = "Review", WorkerRole = "reviewer", Question = "Q2", Answer = "A2", AnsweredBy = "brain" },
                new PersistedClarification { Timestamp = time3, Phase = "Testing", WorkerRole = "tester", Question = "Q3", Answer = "A3", AnsweredBy = "human" },
            ],
        }, TestContext.Current.CancellationToken);

        var results = await store.GetAllClarificationsAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);

        // Should be ordered by timestamp descending (most recent first)
        Assert.Equal("clar-goal-2", results[0].GoalId);
        Assert.Equal("Q3", results[0].Clarification.Question);
        Assert.Equal("clar-goal-2", results[1].GoalId);
        Assert.Equal("Q2", results[1].Clarification.Question);
        Assert.Equal("clar-goal-1", results[2].GoalId);
        Assert.Equal("Q1", results[2].Clarification.Question);
    }

    [Fact]
    public async Task GetAllClarificationsAsync_LimitParameter_RestrictsResults()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await store.CreateGoalAsync(new Goal { Id = "clar-limit-goal", Description = "Goal" }, TestContext.Current.CancellationToken);

        var baseTime = DateTime.UtcNow;
        var clarifications = new List<PersistedClarification>();
        for (var i = 0; i < 5; i++)
        {
            clarifications.Add(new PersistedClarification
            {
                Timestamp = baseTime.AddMinutes(-i),
                Phase = "Coding",
                WorkerRole = "coder",
                Question = $"Q{i}",
                Answer = $"A{i}",
                AnsweredBy = "brain",
            });
        }

        await store.AddIterationAsync("clar-limit-goal", new IterationSummary
        {
            Iteration = 1,
            Clarifications = clarifications,
        }, TestContext.Current.CancellationToken);

        var limited = await store.GetAllClarificationsAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(2, limited.Count);
        // Most recent first
        Assert.Equal("Q0", limited[0].Clarification.Question);
        Assert.Equal("Q1", limited[1].Clarification.Question);
    }

    [Fact]
    public async Task GetAllClarificationsAsync_GoalWithNoIterations_NotRepresented()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await store.CreateGoalAsync(new Goal { Id = "clar-empty-goal", Description = "No iterations" }, TestContext.Current.CancellationToken);

        var results = await store.GetAllClarificationsAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetAllClarificationsAsync_IterationWithEmptyClarifications_NotRepresented()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await store.CreateGoalAsync(new Goal { Id = "clar-empty-clar", Description = "Empty clarifications" }, TestContext.Current.CancellationToken);
        await store.AddIterationAsync("clar-empty-clar", new IterationSummary
        {
            Iteration = 1,
            Clarifications = [],
        }, TestContext.Current.CancellationToken);

        var results = await store.GetAllClarificationsAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (6) ResetGoalIterationDataAsync
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetGoalIterationDataAsync_FailedGoal_ClearsIterationDataAndPreservesIdentity()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var originalCreatedAt = DateTime.UtcNow.AddHours(-2);
        var goal = new Goal
        {
            Id = "reset-goal-1",
            Description = "Goal to reset",
            Priority = GoalPriority.High,
            Scope = GoalScope.Feature,
            DependsOn = ["dep-1", "dep-2"],
            ReleaseId = "rel-reset",
            RepositoryNames = ["repo-1"],
            CreatedAt = originalCreatedAt,
            Status = GoalStatus.Failed,
            FailureReason = "Build failed",
            Iterations = 3,
            StartedAt = DateTime.UtcNow.AddHours(-1),
            CompletedAt = DateTime.UtcNow,
            TotalDurationSeconds = 3600.0,
            Notes = ["failed note 1", "failed note 2"],
            PhaseDurations = new() { ["Coding"] = 100.0, ["Testing"] = 200.0 },
        };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        // Add iteration summaries
        await store.AddIterationAsync("reset-goal-1", new IterationSummary
        {
            Iteration = 1,
            BuildSuccess = false,
        }, TestContext.Current.CancellationToken);
        await store.AddIterationAsync("reset-goal-1", new IterationSummary
        {
            Iteration = 2,
            BuildSuccess = true,
        }, TestContext.Current.CancellationToken);

        // Verify iterations exist before reset
        var iterationsBefore = await store.GetIterationsAsync("reset-goal-1", TestContext.Current.CancellationToken);
        Assert.Equal(2, iterationsBefore.Count);

        // Act
        await store.ResetGoalIterationDataAsync("reset-goal-1", TestContext.Current.CancellationToken);

        // Assert: iteration data cleared
        var retrieved = await store.GetGoalAsync("reset-goal-1", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Null(retrieved!.FailureReason);
        // ResetGoalIterationDataAsync sets Iterations to 0 (not null) per the implementation
        Assert.Equal(0, retrieved.Iterations);
        Assert.Null(retrieved.StartedAt);
        Assert.Null(retrieved.CompletedAt);
        Assert.Null(retrieved.TotalDurationSeconds);
        // Notes are NOT cleared by ResetGoalIterationDataAsync (only iteration-specific data is reset)
        Assert.Equal(["failed note 1", "failed note 2"], retrieved.Notes);
        Assert.Null(retrieved.PhaseDurations);

        // Status is NOT reset by ResetGoalIterationDataAsync — it remains as-is (the caller
        // is responsible for setting status to Pending before/after calling reset if needed)
        Assert.Equal(GoalStatus.Failed, retrieved.Status);

        // Preserved fields
        Assert.Equal("reset-goal-1", retrieved.Id);
        Assert.Equal("Goal to reset", retrieved.Description);
        Assert.Equal(GoalPriority.High, retrieved.Priority);
        Assert.Equal(GoalScope.Feature, retrieved.Scope);
        Assert.Equal(["dep-1", "dep-2"], retrieved.DependsOn);
        Assert.Equal("rel-reset", retrieved.ReleaseId);
        Assert.Equal(["repo-1"], retrieved.RepositoryNames);
        Assert.Equal(originalCreatedAt, retrieved.CreatedAt);

        // Iteration summaries removed from DB
        var iterationsAfter = await store.GetIterationsAsync("reset-goal-1", TestContext.Current.CancellationToken);
        Assert.Empty(iterationsAfter);
    }

    [Fact]
    public async Task ResetGoalIterationDataAsync_NonExistentGoal_ThrowsKeyNotFoundException()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.ResetGoalIterationDataAsync("no-such-goal", TestContext.Current.CancellationToken));
    }
}