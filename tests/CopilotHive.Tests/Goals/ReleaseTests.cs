using CopilotHive.Goals;
using CopilotHive.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Goals;

/// <summary>
/// Tests for Release CRUD operations and Goal.ReleaseId functionality.
/// </summary>
public sealed class ReleaseTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;

    public ReleaseTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    public void Dispose() => _dbContext.Dispose();

    // ── Release CRUD ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRelease_PersistsAndReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release
        {
            Id = "v1.0.0",
            Tag = "v1.0.0",
            Notes = "First release",
        };

        var result = await _store.CreateReleaseAsync(release, ct);
        Assert.Equal("v1.0.0", result.Id);
        Assert.Equal("v1.0.0", result.Tag);
        Assert.Equal(ReleaseStatus.Planning, result.Status);
    }

    [Fact]
    public async Task CreateRelease_WithRepositories_Persists()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release
        {
            Id = "v2.0.0",
            Tag = "v2.0.0",
            RepositoryNames = ["CopilotHive", "CopilotHive-Config"],
        };

        await _store.CreateReleaseAsync(release, ct);
        var fetched = await _store.GetReleaseAsync("v2.0.0", ct);

        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.RepositoryNames.Count);
        Assert.Contains("CopilotHive", fetched.RepositoryNames);
        Assert.Contains("CopilotHive-Config", fetched.RepositoryNames);
    }

    [Fact]
    public async Task GetRelease_NotFound_ReturnsNull()
    {
        var result = await _store.GetReleaseAsync("nonexistent", TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetReleases_ReturnsOrderedByCreatedAtDescending()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create releases with slight time differences
        var r1 = new Release { Id = "v1.0.0", Tag = "v1.0.0", CreatedAt = DateTime.UtcNow.AddHours(-2) };
        var r2 = new Release { Id = "v1.1.0", Tag = "v1.1.0", CreatedAt = DateTime.UtcNow.AddHours(-1) };
        var r3 = new Release { Id = "v2.0.0", Tag = "v2.0.0", CreatedAt = DateTime.UtcNow };

        await _store.CreateReleaseAsync(r1, ct);
        await _store.CreateReleaseAsync(r2, ct);
        await _store.CreateReleaseAsync(r3, ct);

        var releases = await _store.GetReleasesAsync(ct);
        Assert.Equal(3, releases.Count);

        // Should be ordered by CreatedAt descending
        Assert.Equal("v2.0.0", releases[0].Id);
        Assert.Equal("v1.1.0", releases[1].Id);
        Assert.Equal("v1.0.0", releases[2].Id);
    }

    [Fact]
    public async Task UpdateRelease_ModifiesMutableFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release
        {
            Id = "v1.0.0",
            Tag = "v1.0.0",
            Notes = "Initial notes",
        };

        await _store.CreateReleaseAsync(release, ct);

        // Update mutable fields
        release.Status = ReleaseStatus.Released;
        release.Notes = "Updated notes";
        release.ReleasedAt = DateTime.UtcNow;
        release.RepositoryNames.Add("CopilotHive");

        await _store.UpdateReleaseAsync(release, ct);

        var fetched = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.NotNull(fetched);
        Assert.Equal(ReleaseStatus.Released, fetched!.Status);
        Assert.Equal("Updated notes", fetched.Notes);
        Assert.NotNull(fetched.ReleasedAt);
        Assert.Single(fetched.RepositoryNames);
    }

    [Fact]
    public async Task UpdateRelease_NotFound_Throws()
    {
        var release = new Release { Id = "nonexistent", Tag = "nonexistent" };
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _store.UpdateReleaseAsync(release, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteRelease_RemovesRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0" };
        await _store.CreateReleaseAsync(release, ct);

        var deleted = await _store.DeleteReleaseAsync("v1.0.0", ct);
        Assert.True(deleted);

        var fetched = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task DeleteRelease_NotFound_ReturnsFalse()
    {
        var deleted = await _store.DeleteReleaseAsync("nonexistent", TestContext.Current.CancellationToken);
        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteRelease_ReleasedRelease_ReturnsFalseAndDoesNotDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        // Create a release in Released status — cannot be deleted.
        var release = new Release { Id = "v2.0.0", Tag = "v2.0.0", Status = ReleaseStatus.Released };
        await _store.CreateReleaseAsync(release, ct);

        var deleted = await _store.DeleteReleaseAsync("v2.0.0", ct);
        Assert.False(deleted);

        // Release must still exist in the store.
        var fetched = await _store.GetReleaseAsync("v2.0.0", ct);
        Assert.NotNull(fetched);
        Assert.Equal(ReleaseStatus.Released, fetched!.Status);
    }

    [Fact]
    public async Task DeleteRelease_PlanningRelease_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        // Planning release can always be deleted.
        var release = new Release { Id = "v3.0.0-plan", Tag = "v3.0.0-plan", Status = ReleaseStatus.Planning };
        await _store.CreateReleaseAsync(release, ct);

        var deleted = await _store.DeleteReleaseAsync("v3.0.0-plan", ct);
        Assert.True(deleted);

        var fetched = await _store.GetReleaseAsync("v3.0.0-plan", ct);
        Assert.Null(fetched);
    }

    // ── Goal.ReleaseId ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGoal_WithReleaseId_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = new Goal
        {
            Id = "goal-with-release",
            Description = "Goal with release assignment",
            ReleaseId = "v1.0.0",
            CreatedAt = DateTime.UtcNow,
        };

        await _store.CreateGoalAsync(goal, ct);
        var fetched = await _store.GetGoalAsync("goal-with-release", ct);

        Assert.NotNull(fetched);
        Assert.Equal("v1.0.0", fetched!.ReleaseId);
    }

    [Fact]
    public async Task UpdateGoal_WithReleaseId_Persists()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = new Goal
        {
            Id = "goal-update-release",
            Description = "Goal to update",
            CreatedAt = DateTime.UtcNow,
        };
        await _store.CreateGoalAsync(goal, ct);

        goal.ReleaseId = "v2.0.0";
        await _store.UpdateGoalAsync(goal, ct);

        var fetched = await _store.GetGoalAsync("goal-update-release", ct);
        Assert.NotNull(fetched);
        Assert.Equal("v2.0.0", fetched!.ReleaseId);
    }

    [Fact]
    public async Task CreateGoal_WithoutReleaseId_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = new Goal
        {
            Id = "goal-no-release",
            Description = "Goal without release",
            CreatedAt = DateTime.UtcNow,
        };

        await _store.CreateGoalAsync(goal, ct);
        var fetched = await _store.GetGoalAsync("goal-no-release", ct);

        Assert.NotNull(fetched);
        Assert.Null(fetched!.ReleaseId);
    }

    // ── GetGoalsByReleaseAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetGoalsByRelease_ReturnsOnlyGoalsWithMatchingReleaseId()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create release
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0" };
        await _store.CreateReleaseAsync(release, ct);

        // Create goals
        await _store.CreateGoalAsync(new Goal
        {
            Id = "goal-1",
            Description = "Goal 1",
            ReleaseId = "v1.0.0",
            CreatedAt = DateTime.UtcNow,
        }, ct);

        await _store.CreateGoalAsync(new Goal
        {
            Id = "goal-2",
            Description = "Goal 2",
            ReleaseId = "v1.0.0",
            CreatedAt = DateTime.UtcNow,
        }, ct);

        await _store.CreateGoalAsync(new Goal
        {
            Id = "goal-3",
            Description = "Goal 3 - unassigned",
            ReleaseId = null,
            CreatedAt = DateTime.UtcNow,
        }, ct);

        var goals = await _store.GetGoalsByReleaseAsync("v1.0.0", ct);

        Assert.Equal(2, goals.Count);
        Assert.All(goals, g => Assert.Equal("v1.0.0", g.ReleaseId));
    }

    [Fact]
    public async Task GetGoalsByRelease_NoMatchingGoals_ReturnsEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        var goals = await _store.GetGoalsByReleaseAsync("nonexistent-release", ct);
        Assert.Empty(goals);
    }

    // ── ReleaseStatus Enum ────────────────────────────────────────────────

    [Fact]
    public async Task ReleaseStatus_Planning_IsLowercaseInDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release
        {
            Id = "status-test",
            Tag = "status-test",
            Status = ReleaseStatus.Planning,
        };

        // Status should serialize to lowercase 'planning'
        await _store.CreateReleaseAsync(release, ct);

        using var cmd = _dbContext.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT status FROM releases WHERE id = 'status-test'";
        if (cmd.Connection?.State != System.Data.ConnectionState.Open)
            cmd.Connection?.Open();
        var status = cmd.ExecuteScalar() as string;
        Assert.Equal("planning", status);
    }

    [Fact]
    public async Task ReleaseStatus_Released_IsLowercaseInDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release
        {
            Id = "status-released",
            Tag = "status-released",
            Status = ReleaseStatus.Released,
        };

        await _store.CreateReleaseAsync(release, ct);

        using var cmd = _dbContext.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT status FROM releases WHERE id = 'status-released'";
        if (cmd.Connection?.State != System.Data.ConnectionState.Open)
            cmd.Connection?.Open();
        var status = cmd.ExecuteScalar() as string;
        Assert.Equal("released", status);
    }

    [Fact]
    public async Task ReleaseStatus_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release
        {
            Id = "status-roundtrip",
            Tag = "status-roundtrip",
            Status = ReleaseStatus.Released,
            ReleasedAt = DateTime.UtcNow,
        };

        await _store.CreateReleaseAsync(release, ct);
        var fetched = await _store.GetReleaseAsync("status-roundtrip", ct);

        Assert.NotNull(fetched);
        Assert.Equal(ReleaseStatus.Released, fetched!.Status);
        Assert.NotNull(fetched.ReleasedAt);
    }

    [Fact]
    public async Task UpdateRelease_CanTransitionReleasedToPlanning()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Released };
        await _store.CreateReleaseAsync(release, ct);

        release.Status = ReleaseStatus.Planning;
        await _store.UpdateReleaseAsync(release, ct);

        var fetched = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.NotNull(fetched);
        Assert.Equal(ReleaseStatus.Planning, fetched!.Status);
    }
}