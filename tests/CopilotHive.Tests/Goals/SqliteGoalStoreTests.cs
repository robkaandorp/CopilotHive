using CopilotHive.Goals;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Goals;

public sealed class SqliteGoalStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteGoalStore _store;

    public SqliteGoalStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _store = new SqliteGoalStore(_connection, NullLogger<SqliteGoalStore>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static Goal MakeGoal(string id = "test-goal", string desc = "Do the thing",
        GoalStatus status = GoalStatus.Pending, GoalPriority priority = GoalPriority.Normal)
        => new()
        {
            Id = id,
            Description = desc,
            Status = status,
            Priority = priority,
            CreatedAt = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
        };

    // ── CRUD ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGoal_PersistsAndReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = MakeGoal();
        var result = await _store.CreateGoalAsync(goal, ct);

        Assert.Equal("test-goal", result.Id);

        var fetched = await _store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Equal("Do the thing", fetched.Description);
        Assert.Equal(GoalStatus.Pending, fetched.Status);
        Assert.Equal(GoalPriority.Normal, fetched.Priority);
    }

    [Fact]
    public async Task CreateGoal_DuplicateId_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        await Assert.ThrowsAsync<SqliteException>(() =>
            _store.CreateGoalAsync(MakeGoal(), ct));
    }

    [Fact]
    public async Task GetGoal_NotFound_ReturnsNull()
    {
        var result = await _store.GetGoalAsync("nonexistent", TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateGoal_ModifiesFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = MakeGoal();
        await _store.CreateGoalAsync(goal, ct);

        goal.Status = GoalStatus.Completed;
        goal.FailureReason = null;
        goal.CompletedAt = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        goal.Iterations = 3;
        await _store.UpdateGoalAsync(goal, ct);

        var fetched = await _store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Equal(GoalStatus.Completed, fetched.Status);
        Assert.Equal(3, fetched.Iterations);
        Assert.NotNull(fetched.CompletedAt);
    }

    [Fact]
    public async Task UpdateGoal_NotFound_Throws()
    {
        var goal = MakeGoal(id: "ghost");
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _store.UpdateGoalAsync(goal, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteGoal_RemovesGoal()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        var deleted = await _store.DeleteGoalAsync("test-goal", ct);
        Assert.True(deleted);

        var fetched = await _store.GetGoalAsync("test-goal", ct);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task DeleteGoal_NotFound_ReturnsFalse()
    {
        var deleted = await _store.DeleteGoalAsync("nonexistent", TestContext.Current.CancellationToken);
        Assert.False(deleted);
    }

    // ── Status queries ────────────────────────────────────────────────────

    [Fact]
    public async Task GetPendingGoals_ReturnsOnlyPending()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal("g1", "Pending one", GoalStatus.Pending), ct);
        await _store.CreateGoalAsync(MakeGoal("g2", "In progress", GoalStatus.InProgress), ct);
        await _store.CreateGoalAsync(MakeGoal("g3", "Pending two", GoalStatus.Pending), ct);

        var pending = await _store.GetPendingGoalsAsync(ct);
        Assert.Equal(2, pending.Count);
        Assert.All(pending, g => Assert.Equal(GoalStatus.Pending, g.Status));
    }

    [Fact]
    public async Task GetGoalsByStatus_FiltersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal("g1", status: GoalStatus.Failed), ct);
        await _store.CreateGoalAsync(MakeGoal("g2", status: GoalStatus.Completed), ct);
        await _store.CreateGoalAsync(MakeGoal("g3", status: GoalStatus.Failed), ct);

        var failed = await _store.GetGoalsByStatusAsync(GoalStatus.Failed, ct);
        Assert.Equal(2, failed.Count);
    }

    [Fact]
    public async Task GetAllGoals_ReturnsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal("g1", status: GoalStatus.Pending), ct);
        await _store.CreateGoalAsync(MakeGoal("g2", status: GoalStatus.Completed), ct);
        await _store.CreateGoalAsync(MakeGoal("g3", status: GoalStatus.Failed), ct);

        var all = await _store.GetAllGoalsAsync(ct);
        Assert.Equal(3, all.Count);
    }

    // ── Search ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchGoals_MatchesDescription()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal("g1", "Add logging to worker"), ct);
        await _store.CreateGoalAsync(MakeGoal("g2", "Fix build pipeline"), ct);
        await _store.CreateGoalAsync(MakeGoal("g3", "Add logging to brain"), ct);

        var results = await _store.SearchGoalsAsync("logging", ct: ct);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchGoals_MatchesId()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal("add-logging", "Do stuff"), ct);
        await _store.CreateGoalAsync(MakeGoal("fix-build", "Do other stuff"), ct);

        var results = await _store.SearchGoalsAsync("logging", ct: ct);
        Assert.Single(results);
        Assert.Equal("add-logging", results[0].Id);
    }

    [Fact]
    public async Task SearchGoals_WithStatusFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal("g1", "Add logging", GoalStatus.Pending), ct);
        await _store.CreateGoalAsync(MakeGoal("g2", "Add logging too", GoalStatus.Completed), ct);

        var results = await _store.SearchGoalsAsync("logging", GoalStatus.Pending, ct);
        Assert.Single(results);
        Assert.Equal("g1", results[0].Id);
    }

    // ── UpdateGoalStatusAsync ─────────────────────────────────────────────

    [Fact]
    public async Task UpdateGoalStatus_UpdatesStatusAndMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        await _store.UpdateGoalStatusAsync("test-goal", GoalStatus.InProgress,
            new GoalUpdateMetadata
            {
                StartedAt = new DateTime(2025, 1, 15, 11, 0, 0, DateTimeKind.Utc),
            }, ct);

        var goal = await _store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(goal);
        Assert.Equal(GoalStatus.InProgress, goal.Status);
        Assert.NotNull(goal.StartedAt);
    }

    [Fact]
    public async Task UpdateGoalStatus_WithIterationSummary_AppendsSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 45.2 }],
            TestCounts = new TestCounts { Total = 10, Passed = 10, Failed = 0 },
            ReviewVerdict = "approve",
        };

        await _store.UpdateGoalStatusAsync("test-goal", GoalStatus.Completed,
            new GoalUpdateMetadata { IterationSummary = summary, Iterations = 1 }, ct);

        var goal = await _store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(goal);
        Assert.Single(goal.IterationSummaries);
        Assert.Equal("approve", goal.IterationSummaries[0].ReviewVerdict);
        Assert.Equal(10, goal.IterationSummaries[0].TestCounts!.Total);
    }

    [Fact]
    public async Task UpdateGoalStatus_GoalNotFound_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _store.UpdateGoalStatusAsync("ghost", GoalStatus.Failed, ct: TestContext.Current.CancellationToken));
    }

    // ── Iterations ────────────────────────────────────────────────────────

    [Fact]
    public async Task AddIteration_And_GetIterations()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        var s1 = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 30 }],
            ReviewVerdict = "reject",
            Notes = ["reviewer said: missing tests"],
        };
        var s2 = new IterationSummary
        {
            Iteration = 2,
            Phases =
            [
                new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 25 },
                new PhaseResult { Name = "Testing", Result = "pass", DurationSeconds = 60 },
            ],
            TestCounts = new TestCounts { Total = 50, Passed = 50, Failed = 0 },
            ReviewVerdict = "approve",
        };

        await _store.AddIterationAsync("test-goal", s1, ct);
        await _store.AddIterationAsync("test-goal", s2, ct);

        var iterations = await _store.GetIterationsAsync("test-goal", ct);
        Assert.Equal(2, iterations.Count);
        Assert.Equal(1, iterations[0].Iteration);
        Assert.Equal("reject", iterations[0].ReviewVerdict);
        Assert.Single(iterations[0].Notes);
        Assert.Equal(2, iterations[1].Iteration);
        Assert.Equal("approve", iterations[1].ReviewVerdict);
        Assert.Equal(50, iterations[1].TestCounts!.Total);
    }

    // ── Import ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportGoals_ImportsNewGoals()
    {
        var ct = TestContext.Current.CancellationToken;
        var goals = new[]
        {
            MakeGoal("import-1", "First imported"),
            MakeGoal("import-2", "Second imported"),
        };

        var count = await _store.ImportGoalsAsync(goals, ct);
        Assert.Equal(2, count);

        var all = await _store.GetAllGoalsAsync(ct);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task ImportGoals_SkipsExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal("existing", "Already here"), ct);

        var goals = new[]
        {
            MakeGoal("existing", "Duplicate"),
            MakeGoal("new-one", "Brand new"),
        };

        var count = await _store.ImportGoalsAsync(goals, ct);
        Assert.Equal(1, count);

        var all = await _store.GetAllGoalsAsync(ct);
        Assert.Equal(2, all.Count);
    }

    // ── Repositories & Metadata roundtrip ─────────────────────────────────

    [Fact]
    public async Task CreateGoal_PreservesRepositoriesAndMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = MakeGoal();
        goal.RepositoryNames.Add("CopilotHive");
        goal.RepositoryNames.Add("CopilotHive-Config");
        goal.Metadata["source"] = "smoke-test";
        goal.Metadata["iteration_target"] = "3";

        await _store.CreateGoalAsync(goal, ct);
        var fetched = await _store.GetGoalAsync("test-goal", ct);

        Assert.NotNull(fetched);
        Assert.Equal(2, fetched.RepositoryNames.Count);
        Assert.Contains("CopilotHive", fetched.RepositoryNames);
        Assert.Contains("CopilotHive-Config", fetched.RepositoryNames);
        Assert.Equal("smoke-test", fetched.Metadata["source"]);
        Assert.Equal("3", fetched.Metadata["iteration_target"]);
    }

    // ── DependsOn round-trip ───────────────────────────────────────────────

    [Fact]
    public async Task DependsOn_SqliteRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = new Goal
        {
            Id = "dep-goal",
            Description = "Goal with dependencies",
            DependsOn = ["goal-a", "goal-b"],
            CreatedAt = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
        };

        await _store.CreateGoalAsync(goal, ct);
        var fetched = await _store.GetGoalAsync("dep-goal", ct);

        Assert.NotNull(fetched);
        Assert.Equal(2, fetched.DependsOn.Count);
        Assert.Contains("goal-a", fetched.DependsOn);
        Assert.Contains("goal-b", fetched.DependsOn);
    }

    [Fact]
    public async Task DependsOn_EmptyList_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = MakeGoal();

        await _store.CreateGoalAsync(goal, ct);
        var fetched = await _store.GetGoalAsync("test-goal", ct);

        Assert.NotNull(fetched);
        Assert.Empty(fetched.DependsOn);
    }

    // ── MergeCommitHash ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateGoal_WithMergeCommitHash_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = MakeGoal();
        goal.MergeCommitHash = "abc123def456";
        await _store.CreateGoalAsync(goal, ct);

        var fetched = await _store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Equal("abc123def456", fetched.MergeCommitHash);
    }

    [Fact]
    public async Task UpdateGoalAsync_WithMergeCommitHash_Persists()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = MakeGoal();
        await _store.CreateGoalAsync(goal, ct);

        goal.MergeCommitHash = "deadbeef00112233";
        await _store.UpdateGoalAsync(goal, ct);

        var fetched = await _store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Equal("deadbeef00112233", fetched.MergeCommitHash);
    }

    [Fact]
    public async Task UpdateGoalStatusAsync_WithMergeCommitHash_Persists()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        await _store.UpdateGoalStatusAsync("test-goal", GoalStatus.Completed, new GoalUpdateMetadata
        {
            MergeCommitHash = "cafebabe1234",
        }, ct);

        var fetched = await _store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Equal("cafebabe1234", fetched.MergeCommitHash);
    }

    [Fact]
    public async Task UpdateGoalStatusAsync_NullMergeCommitHash_DoesNotOverwrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = MakeGoal();
        goal.MergeCommitHash = "original-hash";
        await _store.CreateGoalAsync(goal, ct);

        // Update without providing MergeCommitHash — should not overwrite existing value
        await _store.UpdateGoalStatusAsync("test-goal", GoalStatus.Completed, new GoalUpdateMetadata
        {
            MergeCommitHash = null,
        }, ct);

        // The existing hash should be preserved (SQL UPDATE only runs when not null)
        var fetched = await _store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Equal("original-hash", fetched.MergeCommitHash);
    }

    [Fact]
    public async Task CreateGoal_WithoutMergeCommitHash_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        var fetched = await _store.GetGoalAsync("test-goal", ct);
        Assert.NotNull(fetched);
        Assert.Null(fetched.MergeCommitHash);
    }

    [Fact]
    public async Task Migration_AddsMergeCommitHashColumnToExistingDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        using var oldConn = new SqliteConnection("Data Source=:memory:");
        oldConn.Open();

        // Create old schema WITHOUT merge_commit_hash column
        using (var createCmd = oldConn.CreateCommand())
        {
            createCmd.CommandText = """
                CREATE TABLE goals (
                    id                    TEXT PRIMARY KEY,
                    description           TEXT NOT NULL,
                    title                 TEXT,
                    status                TEXT NOT NULL DEFAULT 'pending',
                    priority              TEXT NOT NULL DEFAULT 'normal',
                    repositories          TEXT,
                    metadata              TEXT,
                    created_at            TEXT NOT NULL,
                    started_at            TEXT,
                    completed_at          TEXT,
                    iterations            INTEGER,
                    failure_reason        TEXT,
                    notes                 TEXT,
                    phase_durations       TEXT,
                    total_duration_seconds REAL,
                    source_conversation_id TEXT,
                    depends_on             TEXT
                );
                """;
            createCmd.ExecuteNonQuery();
        }

        // Insert a goal using old schema
        using (var insertCmd = oldConn.CreateCommand())
        {
            insertCmd.CommandText = """
                INSERT INTO goals (id, description, status, priority, created_at)
                VALUES ('old-goal', 'Pre-migration goal', 'pending', 'normal', '2025-01-01T00:00:00Z')
                """;
            insertCmd.ExecuteNonQuery();
        }

        // Create store — triggers migration
        var store = new SqliteGoalStore(oldConn, NullLogger<SqliteGoalStore>.Instance);

        // Old goal should have null merge_commit_hash
        var oldGoal = await store.GetGoalAsync("old-goal", ct);
        Assert.NotNull(oldGoal);
        Assert.Null(oldGoal.MergeCommitHash);

        // New goal with hash should round-trip
        var newGoal = new Goal
        {
            Id = "new-goal",
            Description = "Post-migration goal",
            MergeCommitHash = "post-migration-hash",
            CreatedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        await store.CreateGoalAsync(newGoal, ct);

        var fetched = await store.GetGoalAsync("new-goal", ct);
        Assert.NotNull(fetched);
        Assert.Equal("post-migration-hash", fetched.MergeCommitHash);
    }

    // ── Name property ─────────────────────────────────────────────────────

    [Fact]
    public void Name_IsSqlite()
    {
        Assert.Equal("sqlite", _store.Name);
    }

    // ── Schema migration ──────────────────────────────────────────────────

    [Fact]
    public async Task Migration_AddsColumnToExistingDatabase()
    {
        // Simulate an older database that doesn't have the depends_on column
        var ct = TestContext.Current.CancellationToken;
        using var oldConn = new SqliteConnection("Data Source=:memory:");
        oldConn.Open();

        // Create old schema WITHOUT depends_on
        using (var createCmd = oldConn.CreateCommand())
        {
            createCmd.CommandText = """
                CREATE TABLE goals (
                    id                    TEXT PRIMARY KEY,
                    description           TEXT NOT NULL,
                    title                 TEXT,
                    status                TEXT NOT NULL DEFAULT 'pending',
                    priority              TEXT NOT NULL DEFAULT 'normal',
                    repositories          TEXT,
                    metadata              TEXT,
                    created_at            TEXT NOT NULL,
                    started_at            TEXT,
                    completed_at          TEXT,
                    iterations            INTEGER,
                    failure_reason        TEXT,
                    notes                 TEXT,
                    phase_durations       TEXT,
                    total_duration_seconds REAL,
                    source_conversation_id TEXT
                );
                """;
            createCmd.ExecuteNonQuery();
        }

        // Insert a goal using old schema
        using (var insertCmd = oldConn.CreateCommand())
        {
            insertCmd.CommandText = """
                INSERT INTO goals (id, description, status, priority, created_at)
                VALUES ('old-goal', 'Pre-migration goal', 'pending', 'normal', '2025-01-01T00:00:00Z')
                """;
            insertCmd.ExecuteNonQuery();
        }

        // Now create a SqliteGoalStore which triggers InitSchema + MigrateSchema
        var store = new SqliteGoalStore(oldConn, NullLogger<SqliteGoalStore>.Instance);

        // Verify old goal can still be read (depends_on should be empty)
        var oldGoal = await store.GetGoalAsync("old-goal", ct);
        Assert.NotNull(oldGoal);
        Assert.Empty(oldGoal.DependsOn);

        // Verify new goals with depends_on work
        var newGoal = new Goal
        {
            Id = "new-goal",
            Description = "Post-migration goal",
            DependsOn = ["old-goal"],
            CreatedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        await store.CreateGoalAsync(newGoal, ct);

        var fetched = await store.GetGoalAsync("new-goal", ct);
        Assert.NotNull(fetched);
        Assert.Single(fetched.DependsOn);
        Assert.Contains("old-goal", fetched.DependsOn);
    }
}

/// <summary>
/// Tests that verify IterationSummary round-trip serialization with worker outputs
/// through the SQLite JSON path.
/// </summary>
public sealed class SqliteGoalStoreWorkerOutputTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteGoalStore _store;

    public SqliteGoalStoreWorkerOutputTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _store = new SqliteGoalStore(_connection, NullLogger<SqliteGoalStore>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private static Goal MakeGoal(string id = "worker-output-goal") => new()
    {
        Id = id,
        Description = "Goal for worker output test",
        CreatedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// PhaseResult.WorkerOutput round-trips through the SQLite JSON column (phases_json).
    /// </summary>
    [Fact]
    public async Task SqliteGoalStore_PhaseResultWorkerOutput_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal(), ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult
                {
                    Name = "Coding",
                    Result = "pass",
                    DurationSeconds = 75.0,
                    WorkerOutput = "Coder: Implemented feature X.",
                },
                new PhaseResult
                {
                    Name = "Testing",
                    Result = "pass",
                    DurationSeconds = 30.0,
                    WorkerOutput = null,
                },
            ],
        };

        await _store.UpdateGoalStatusAsync("worker-output-goal", GoalStatus.Completed,
            new GoalUpdateMetadata { Iterations = 1, IterationSummary = summary }, ct);

        var goal = await _store.GetGoalAsync("worker-output-goal", ct);
        Assert.NotNull(goal);
        Assert.Single(goal.IterationSummaries);
        var s = goal.IterationSummaries[0];
        Assert.Equal(2, s.Phases.Count);
        Assert.Equal("Coder: Implemented feature X.", s.Phases[0].WorkerOutput);
        Assert.Null(s.Phases[1].WorkerOutput);
    }

    /// <summary>
    /// IterationSummary.PhaseOutputs dictionary round-trips through the SQLite phase_outputs_json column.
    /// </summary>
    [Fact]
    public async Task SqliteGoalStore_PhaseOutputsDictionary_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal("dict-goal"), ct);

        var summary = new IterationSummary
        {
            Iteration = 3,
            Phases =
            [
                new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 50.0 },
            ],
            PhaseOutputs = new Dictionary<string, string>
            {
                ["coder-3"] = "Coder output text.",
                ["reviewer-3"] = "Reviewer approved.",
            },
        };

        await _store.UpdateGoalStatusAsync("dict-goal", GoalStatus.Completed,
            new GoalUpdateMetadata { Iterations = 3, IterationSummary = summary }, ct);

        var goal = await _store.GetGoalAsync("dict-goal", ct);
        Assert.NotNull(goal);
        Assert.Single(goal.IterationSummaries);
        var s = goal.IterationSummaries[0];
        Assert.Equal(2, s.PhaseOutputs.Count);
        Assert.Equal("Coder output text.", s.PhaseOutputs["coder-3"]);
        Assert.Equal("Reviewer approved.", s.PhaseOutputs["reviewer-3"]);
    }

    /// <summary>
    /// When IterationSummary.PhaseOutputs is empty, it round-trips as an empty dictionary.
    /// </summary>
    [Fact]
    public async Task SqliteGoalStore_EmptyPhaseOutputs_RoundTripsAsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal("empty-goal"), ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 10.0 }],
            PhaseOutputs = [],
        };

        await _store.UpdateGoalStatusAsync("empty-goal", GoalStatus.Completed,
            new GoalUpdateMetadata { Iterations = 1, IterationSummary = summary }, ct);

        var goal = await _store.GetGoalAsync("empty-goal", ct);
        Assert.NotNull(goal);
        Assert.Single(goal.IterationSummaries);
        Assert.Empty(goal.IterationSummaries[0].PhaseOutputs);
    }

    /// <summary>
    /// AddIterationAsync persists PhaseResult.WorkerOutput and PhaseOutputs,
    /// and GetIterationsAsync retrieves them correctly.
    /// </summary>
    [Fact]
    public async Task SqliteGoalStore_AddAndGetIterations_PreservesWorkerOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateGoalAsync(MakeGoal("iter-goal"), ct);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult
                {
                    Name = "Review",
                    Result = "fail",
                    DurationSeconds = 15.0,
                    WorkerOutput = "Reviewer: Missing null checks.",
                },
            ],
            PhaseOutputs = new Dictionary<string, string>
            {
                ["reviewer-1"] = "Reviewer: Missing null checks.",
            },
            ReviewVerdict = "reject",
        };

        await _store.AddIterationAsync("iter-goal", summary, ct);

        var iterations = await _store.GetIterationsAsync("iter-goal", ct);
        Assert.Single(iterations);
        var s = iterations[0];
        Assert.Equal("Reviewer: Missing null checks.", s.Phases[0].WorkerOutput);
        Assert.Equal("reject", s.ReviewVerdict);
        Assert.Single(s.PhaseOutputs);
        Assert.Equal("Reviewer: Missing null checks.", s.PhaseOutputs["reviewer-1"]);
    }
}
