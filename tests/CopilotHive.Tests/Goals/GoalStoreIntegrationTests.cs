using System.Data.Common;

using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        Assert.Null(retrieved.TargetRepositoryNames);
    }

    [Fact]
    public async Task CreateGoalAsync_TargetRepositoryNames_Null_PersistsNull()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "targets-null",
            Description = "Null targets",
            RepositoryNames = ["repo-a", "repo-b"],
        };

        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var retrieved = await store.GetGoalAsync("targets-null", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Null(retrieved!.TargetRepositoryNames);
    }

    [Fact]
    public async Task CreateGoalAsync_TargetRepositoryNames_WhitespaceOnly_PersistsNull()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "targets-whitespace",
            Description = "Whitespace targets",
            RepositoryNames = ["repo-a", "repo-b"],
            TargetRepositoryNames = "  , ,  ",
        };

        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var retrieved = await store.GetGoalAsync("targets-whitespace", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Null(retrieved!.TargetRepositoryNames);
    }

    [Fact]
    public async Task CreateGoalAsync_TargetRepositoryNames_CanonicalizesAndPersists()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "targets-canonical",
            Description = "Canonical targets",
            RepositoryNames = ["Repo-A", "Repo-B", "Repo-C"],
            TargetRepositoryNames = "repo-b, REPO-A, repo-b",
        };

        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var retrieved = await store.GetGoalAsync("targets-canonical", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Repo-B,Repo-A", retrieved!.TargetRepositoryNames);
    }

    [Fact]
    public async Task CreateGoalAsync_TargetRepositoryNames_UnknownEntry_ThrowsArgumentException()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "targets-invalid",
            Description = "Invalid targets",
            RepositoryNames = ["repo-a"],
            TargetRepositoryNames = "repo-x",
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreateGoalAsync(goal, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateGoalAsync_TargetRepositoryNames_NoRepos_ThrowsArgumentException()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "targets-norepos",
            Description = "Targets without repos",
            TargetRepositoryNames = "repo-a",
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreateGoalAsync(goal, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateGoalAsync_TargetRepositoryNames_CanonicalizesAndPersists()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "update-targets",
            Description = "Update targets",
            RepositoryNames = ["repo-a", "repo-b"],
        };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var fetched = await store.GetGoalAsync("update-targets", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        fetched!.TargetRepositoryNames = "repo-b, repo-a, repo-b";
        await store.UpdateGoalAsync(fetched, TestContext.Current.CancellationToken);

        var retrieved = await store.GetGoalAsync("update-targets", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("repo-b,repo-a", retrieved!.TargetRepositoryNames);
        // The canonical format must be "A,B" (no space), not "A, B".
        Assert.DoesNotContain(" ", retrieved.TargetRepositoryNames!);
    }

    [Fact]
    public async Task UpdateGoalAsync_TargetRepositoryNames_Empty_PersistsNull()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "update-targets-empty",
            Description = "Update targets to empty",
            RepositoryNames = ["repo-a", "repo-b"],
            TargetRepositoryNames = "repo-a",
        };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var fetched = await store.GetGoalAsync("update-targets-empty", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        fetched!.TargetRepositoryNames = "";
        await store.UpdateGoalAsync(fetched, TestContext.Current.CancellationToken);

        var retrieved = await store.GetGoalAsync("update-targets-empty", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Null(retrieved!.TargetRepositoryNames);
    }

    [Fact]
    public async Task UpdateGoalAsync_TargetRepositoryNames_InvalidEntry_ThrowsArgumentException()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "update-targets-invalid",
            Description = "Update targets invalid",
            RepositoryNames = ["repo-a"],
        };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var fetched = await store.GetGoalAsync("update-targets-invalid", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        fetched!.TargetRepositoryNames = "repo-x";
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpdateGoalAsync(fetched, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateGoalAsync_TargetRepositoryNames_MalformedCommaOnly_PersistsNull()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "targets-malformed-comma",
            Description = "Malformed comma targets",
            RepositoryNames = ["repo-a", "repo-b"],
            TargetRepositoryNames = ",",
        };

        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var retrieved = await store.GetGoalAsync("targets-malformed-comma", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Null(retrieved!.TargetRepositoryNames);
    }

    [Fact]
    public async Task CreateGoalAsync_TargetRepositoryNames_SpecExample_PersistsCanonicalCommaSeparated()
    {
        // Spec example: "a, A, B" with repos ["A","B"] → "A,B" persisted (canonical comma-separated).
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "targets-spec-example",
            Description = "Spec example targets",
            RepositoryNames = ["A", "B"],
            TargetRepositoryNames = "a, A, B",
        };

        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var retrieved = await store.GetGoalAsync("targets-spec-example", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("A,B", retrieved!.TargetRepositoryNames);
        // The canonical format must be "A,B" (no space), not "A, B".
        Assert.DoesNotContain(" ", retrieved.TargetRepositoryNames!);
    }

    [Fact]
    public async Task CreateGoalAsync_TargetRepositoryNames_RoundTrip_ExplicitTargets()
    {
        // Persistence round-trip: create with explicit targets, read back, verify value.
        // The canonical serialized format must be "A,B" (no space), not "A, B".
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal
        {
            Id = "targets-roundtrip",
            Description = "Round-trip explicit targets",
            RepositoryNames = ["repo-a", "repo-b", "repo-c"],
            TargetRepositoryNames = "repo-c, repo-a",
        };

        var created = await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Equal("repo-c,repo-a", created.TargetRepositoryNames);
        Assert.DoesNotContain(" ", created.TargetRepositoryNames!);

        var retrieved = await store.GetGoalAsync("targets-roundtrip", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("repo-c,repo-a", retrieved!.TargetRepositoryNames);
        Assert.DoesNotContain(" ", retrieved.TargetRepositoryNames!);
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
    public async Task UpdateGoalStatusAsync_ReplaceExistingIterationSummary_DoesNotThrowAndUpdatesValues()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var goal = new Goal { Id = "update-iter-replace", Description = "Update replace test" };
        await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);

        var original = new IterationSummary
        {
            Iteration = 1,
            BuildSuccess = false,
            ReviewVerdict = "reject",
        };
        await store.AddIterationAsync("update-iter-replace", original, TestContext.Current.CancellationToken);

        var replacement = new IterationSummary
        {
            Iteration = 1,
            BuildSuccess = true,
            ReviewVerdict = "approve",
        };

        await store.UpdateGoalStatusAsync(
            "update-iter-replace",
            GoalStatus.Failed,
            new GoalUpdateMetadata { Iterations = 1, IterationSummary = replacement },
            TestContext.Current.CancellationToken);

        var iterations = await store.GetIterationsAsync("update-iter-replace", TestContext.Current.CancellationToken);
        Assert.Single(iterations);
        Assert.Equal(1, iterations[0].Iteration);
        Assert.True(iterations[0].BuildSuccess);
        Assert.Equal("approve", iterations[0].ReviewVerdict);

        var updatedGoal = await store.GetGoalAsync("update-iter-replace", TestContext.Current.CancellationToken);
        Assert.NotNull(updatedGoal);
        Assert.Equal(GoalStatus.Failed, updatedGoal!.Status);
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
    public async Task DeleteReleaseAsync_PlanningReleaseNoGoalsExecutionStateNone_DeletesAndReturnsTrue()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release
        {
            Id = "rel-delete-clean",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Planning,
            ExecutionState = ReleaseExecutionState.None,
        };
        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);

        // An unrelated goal (assigned to another release) must not block the delete.
        await store.CreateGoalAsync(
            new Goal { Id = "goal-other-release", Description = "Other", ReleaseId = "rel-unrelated" },
            TestContext.Current.CancellationToken);

        var deleted = await store.DeleteReleaseAsync("rel-delete-clean", TestContext.Current.CancellationToken);

        Assert.True(deleted);
        Assert.Null(await store.GetReleaseAsync("rel-delete-clean", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteReleaseAsync_PlanningReleaseWithGoals_ReturnsFalseAndKeepsRelease()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release
        {
            Id = "rel-delete-with-goals",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Planning,
            ExecutionState = ReleaseExecutionState.None,
        };
        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);
        await store.CreateGoalAsync(
            new Goal { Id = "goal-attached", Description = "Attached", ReleaseId = "rel-delete-with-goals" },
            TestContext.Current.CancellationToken);

        var deleted = await store.DeleteReleaseAsync("rel-delete-with-goals", TestContext.Current.CancellationToken);

        Assert.False(deleted);
        // The goals-attached precondition must live in the ExecuteDeleteAsync WHERE clause.
        // If it were dropped (or done as a separate check-then-delete that races), the DELETE
        // would remove the row and both assertions below would fail.
        Assert.NotNull(await store.GetReleaseAsync("rel-delete-with-goals", TestContext.Current.CancellationToken));
        // Exactly one release row remains — nothing was partially deleted.
        Assert.Equal(1, await db.Releases.CountAsync(TestContext.Current.CancellationToken));
        // The attached goal is untouched as well.
        var remainingGoals = await store.GetGoalsByReleaseAsync("rel-delete-with-goals", TestContext.Current.CancellationToken);
        Assert.Single(remainingGoals);
    }

    [Fact]
    public async Task DeleteReleaseAsync_ExecutingRelease_ReturnsFalseAndKeepsRelease()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        var release = new Release
        {
            Id = "rel-delete-executing",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Planning,
            ExecutionState = ReleaseExecutionState.Executing,
        };
        await store.CreateReleaseAsync(release, TestContext.Current.CancellationToken);

        var deleted = await store.DeleteReleaseAsync("rel-delete-executing", TestContext.Current.CancellationToken);

        Assert.False(deleted);
        // The not-Executing precondition must live in the ExecuteDeleteAsync WHERE clause.
        // Removing it would delete this Planning release and fail the assertions below.
        var stored = await store.GetReleaseAsync("rel-delete-executing", TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(ReleaseExecutionState.Executing, stored!.ExecutionState);
        Assert.Equal(ReleaseStatus.Planning, stored.Status);
        Assert.Equal(1, await db.Releases.CountAsync(TestContext.Current.CancellationToken));
    }

    // ── Atomicity: preconditions must live INSIDE the DELETE statement ───────
    //
    // The tests above establish the outcome for a stable, sequential arrangement, but they
    // cannot by themselves distinguish an atomic conditional DELETE from a check-then-delete
    // (read the precondition, then unconditionally delete) — under a stable arrangement both
    // shapes return false. The tests below close that gap by mutating the database at the
    // exact instant between "when a pre-check would have run" and "when the DELETE executes",
    // which is precisely the TOCTOU window a check-then-delete implementation leaves open.

    [Fact]
    public async Task DeleteReleaseAsync_GoalInsertedRacingTheDelete_DoesNotDeleteRelease()
    {
        var ct = TestContext.Current.CancellationToken;

        // A goal row is INSERTed on the same connection immediately before the release DELETE
        // is executed. At the moment any pre-check would have run the release had NO goals, so
        // a check-then-delete implementation would see "no goals attached", proceed, and drop
        // the release. Only a DELETE that carries the NOT EXISTS predicate in its own WHERE
        // clause observes the racing goal and deletes nothing.
        using var db = CreateInMemoryWithInterceptor(
            new MidDeleteSqlInjector(releaseId: "rel-race-goal", injectGoal: true),
            out var connection);
        using var _ = connection;
        var store = CreateStore(db);

        await store.CreateReleaseAsync(new Release
        {
            Id = "rel-race-goal",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Planning,
            ExecutionState = ReleaseExecutionState.None,
        }, ct);

        // Precondition: the release genuinely has no goals before the delete begins, so every
        // precondition a pre-check could evaluate is satisfied at that point.
        Assert.Empty(await store.GetGoalsByReleaseAsync("rel-race-goal", ct));

        var deleted = await store.DeleteReleaseAsync("rel-race-goal", ct);

        Assert.False(deleted);
        Assert.Equal(1, await CountReleasesAsync(connection, "rel-race-goal", ct));
    }

    [Fact]
    public async Task DeleteReleaseAsync_ExecutionStateFlippedRacingTheDelete_DoesNotDeleteRelease()
    {
        var ct = TestContext.Current.CancellationToken;

        // Same idea for the execution-state guard: the release is Planning/None when the delete
        // starts, and is flipped to Executing on the same connection immediately before the
        // DELETE runs. A check-then-delete would have already read "not executing" and would
        // delete a release that is now mid-execution.
        using var db = CreateInMemoryWithInterceptor(
            new MidDeleteSqlInjector(releaseId: "rel-race-exec", injectGoal: false),
            out var connection);
        using var _ = connection;
        var store = CreateStore(db);

        await store.CreateReleaseAsync(new Release
        {
            Id = "rel-race-exec",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Planning,
            ExecutionState = ReleaseExecutionState.None,
        }, ct);

        var before = await store.GetReleaseAsync("rel-race-exec", ct);
        Assert.Equal(ReleaseExecutionState.None, before!.ExecutionState);

        var deleted = await store.DeleteReleaseAsync("rel-race-exec", ct);

        Assert.False(deleted);
        Assert.Equal(1, await CountReleasesAsync(connection, "rel-race-exec", ct));
    }

    [Fact]
    public async Task DeleteReleaseAsync_EligibleRelease_IssuesExactlyOneDeleteStatementWithAllPredicates()
    {
        var ct = TestContext.Current.CancellationToken;

        // Command-level proof that the three preconditions are compiled into a single DELETE
        // rather than executed as separate reads: the recorder captures every command the store
        // issues during the delete.
        var recorder = new DeleteCommandRecorder();
        using var db = CreateInMemoryWithInterceptor(recorder, out var connection);
        using var _ = connection;
        var store = CreateStore(db);

        await store.CreateReleaseAsync(new Release
        {
            Id = "rel-sql-shape",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Planning,
            ExecutionState = ReleaseExecutionState.None,
        }, ct);

        recorder.Start();
        var deleted = await store.DeleteReleaseAsync("rel-sql-shape", ct);
        recorder.Stop();

        Assert.True(deleted);

        // Exactly one round-trip: no separate existence/state pre-query, no SELECT-then-DELETE.
        var command = Assert.Single(recorder.Commands);
        Assert.StartsWith("DELETE", command.TrimStart(), StringComparison.OrdinalIgnoreCase);

        // …and that single statement carries all three preconditions plus the id filter.
        Assert.Contains("\"status\" = 'planning'", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"execution_state\" <> 'executing'", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT EXISTS", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"goals\"", command, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates an in-memory SQLite context wired with <paramref name="interceptor"/>. The
    /// underlying connection is returned so the test can assert against the raw database
    /// without going through EF Core's change tracker.
    /// </summary>
    private static CopilotHiveDbContext CreateInMemoryWithInterceptor(
        IInterceptor interceptor, out SqliteConnection connection)
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        var context = new CopilotHiveDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    /// <summary>Counts release rows with the given id straight from SQLite.</summary>
    private static async Task<long> CountReleasesAsync(
        SqliteConnection connection, string releaseId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM releases WHERE id = $id";
        command.Parameters.AddWithValue("$id", releaseId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
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
/// <summary>
/// Mutates the database on the store's own connection at the exact instant the release DELETE
/// is about to execute — i.e. inside the window a check-then-delete implementation would leave
/// between reading a precondition and issuing the delete.
/// </summary>
/// <remarks>
/// Running on the same connection and transaction guarantees the mutation is visible to the
/// DELETE that follows, making the race deterministic instead of timing-dependent.
/// </remarks>
internal sealed class MidDeleteSqlInjector : DbCommandInterceptor
{
    private readonly string _releaseId;
    private readonly bool _injectGoal;
    private bool _done;

    /// <param name="releaseId">Release the competing write targets.</param>
    /// <param name="injectGoal">
    /// When true a goal row is attached to the release; when false the release's
    /// <c>execution_state</c> is flipped to <c>executing</c>.
    /// </param>
    public MidDeleteSqlInjector(string releaseId, bool injectGoal)
    {
        _releaseId = releaseId;
        _injectGoal = injectGoal;
    }

    private void InjectBefore(DbCommand command)
    {
        if (_done || !command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            return;

        _done = true;

        using var competing = command.Connection!.CreateCommand();
        competing.Transaction = command.Transaction;
        if (_injectGoal)
        {
            competing.CommandText =
                """
                INSERT INTO goals (id, description, priority, scope, status, repositories, depends_on,
                                   metadata, created_at, notes, documents, branch_cleaned_up,
                                   review_status, release_id)
                VALUES ($gid, 'racing goal', 'medium', 'feature', 'pending', '[]', '[]', '{}',
                        '2024-01-01T00:00:00Z', '[]', '[]', 0, 'none', $rid)
                """;
            AddParameter(competing, "$gid", "racing-goal-" + _releaseId);
        }
        else
        {
            competing.CommandText = "UPDATE releases SET execution_state = 'executing' WHERE id = $rid";
        }

        AddParameter(competing, "$rid", _releaseId);
        competing.ExecuteNonQuery();
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        InjectBefore(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        InjectBefore(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        InjectBefore(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        InjectBefore(command);
        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// Records the SQL a store operation issues between <see cref="Start"/> and <see cref="Stop"/>,
/// so a test can assert how many round-trips were made and what the statements contain.
/// </summary>
internal sealed class DeleteCommandRecorder : DbCommandInterceptor
{
    private bool _recording;

    /// <summary>Commands captured while recording was enabled.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Begins capturing SQL.</summary>
    public void Start()
    {
        Commands.Clear();
        _recording = true;
    }

    /// <summary>Stops capturing SQL.</summary>
    public void Stop() => _recording = false;

    private void Record(DbCommand command)
    {
        if (_recording)
            Commands.Add(command.CommandText);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }
}
