using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Tests for the Composer's goal management tools.
/// Uses an in-memory SQLite goal store to verify tool behaviour.
/// </summary>
public sealed class ComposerToolTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteGoalStore _store;
    private readonly Composer _composer;

    public ComposerToolTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _store = new SqliteGoalStore(_connection, NullLogger<SqliteGoalStore>.Instance);

        _composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath());
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    // ── create_goal ──

    [Fact]
    public async Task CreateGoal_ValidInput_CreatesAsDraft()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateGoalAsync("add-auth", "Add JWT authentication");

        Assert.Contains("✅", result);
        Assert.Contains("Draft", result);

        var goal = await _store.GetGoalAsync("add-auth", ct);
        Assert.NotNull(goal);
        Assert.Equal(GoalStatus.Draft, goal!.Status);
        Assert.Equal("Add JWT authentication", goal.Description);
    }

    [Fact]
    public async Task CreateGoal_WithRepositories_StoresRepoList()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("fix-bug", "Fix the parser bug", "repo-a, repo-b");

        var goal = await _store.GetGoalAsync("fix-bug", ct);
        Assert.NotNull(goal);
        Assert.Equal(2, goal!.RepositoryNames.Count);
        Assert.Contains("repo-a", goal.RepositoryNames);
        Assert.Contains("repo-b", goal.RepositoryNames);
    }

    [Fact]
    public async Task CreateGoal_WithPriority_SetsPriority()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("urgent-fix", "Fix critical bug", priority: "High");

        var goal = await _store.GetGoalAsync("urgent-fix", ct);
        Assert.NotNull(goal);
        Assert.Equal(GoalPriority.High, goal!.Priority);
    }

    [Fact]
    public async Task CreateGoal_DuplicateId_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("dup-goal", "First");
        var result = await _composer.CreateGoalAsync("dup-goal", "Second");

        Assert.Contains("❌", result);
        Assert.Contains("already exists", result);
    }

    [Fact]
    public async Task CreateGoal_InvalidId_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateGoalAsync("Invalid ID!", "Description");

        Assert.Contains("ERROR", result);
        Assert.Contains("kebab-case", result);
    }

    [Fact]
    public async Task CreateGoal_EmptyDescription_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateGoalAsync("my-goal", "");

        Assert.Contains("ERROR", result);
        Assert.Contains("description is required", result);
    }

    // ── approve_goal ──

    [Fact]
    public async Task ApproveGoal_DraftGoal_ChangesToPending()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("to-approve", "A draft goal");
        var result = await _composer.ApproveGoalAsync("to-approve");

        Assert.Contains("✅", result);
        Assert.Contains("Pending", result);

        var goal = await _store.GetGoalAsync("to-approve", ct);
        Assert.Equal(GoalStatus.Pending, goal!.Status);
    }

    [Fact]
    public async Task ApproveGoal_NonDraft_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var goal = new Goal { Id = "pending-goal", Description = "Already pending", Status = GoalStatus.Pending };
        await _store.CreateGoalAsync(goal, ct);

        var result = await _composer.ApproveGoalAsync("pending-goal");

        Assert.Contains("❌", result);
        Assert.Contains("not Draft", result);
    }

    [Fact]
    public async Task ApproveGoal_NonExistent_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.ApproveGoalAsync("does-not-exist");

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    // ── update_goal ──

    [Fact]
    public async Task UpdateGoal_Status_ChangesStatus()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("update-me", "Test goal");
        var result = await _composer.UpdateGoalAsync("update-me", "status", "Pending");

        Assert.Contains("✅", result);
        var goal = await _store.GetGoalAsync("update-me", ct);
        Assert.Equal(GoalStatus.Pending, goal!.Status);
    }

    [Fact]
    public async Task UpdateGoal_ImmutableField_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("update-me2", "Test goal");
        var result = await _composer.UpdateGoalAsync("update-me2", "description", "New desc");

        Assert.Contains("❌", result);
        Assert.Contains("cannot be changed", result);
    }

    [Fact]
    public async Task UpdateGoal_UnknownField_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("update-me3", "Test goal");
        var result = await _composer.UpdateGoalAsync("update-me3", "color", "blue");

        Assert.Contains("❌", result);
        Assert.Contains("Unknown field", result);
    }

    // ── get_goal ──

    [Fact]
    public async Task GetGoal_ExistingGoal_ReturnsDetails()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("detail-goal", "A detailed goal description");
        var result = await _composer.GetGoalAsync("detail-goal");

        Assert.Contains("detail-goal", result);
        Assert.Contains("A detailed goal description", result);
        Assert.Contains("Draft", result);
    }

    [Fact]
    public async Task GetGoal_NonExistent_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.GetGoalAsync("missing");

        Assert.Contains("not found", result);
    }

    // ── list_goals ──

    [Fact]
    public async Task ListGoals_All_ListsAllGoals()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("goal-a", "First goal");
        await _composer.CreateGoalAsync("goal-b", "Second goal");

        var result = await _composer.ListGoalsAsync();

        Assert.Contains("2 goal(s)", result);
        Assert.Contains("goal-a", result);
        Assert.Contains("goal-b", result);
    }

    [Fact]
    public async Task ListGoals_FilterByStatus_FiltersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("draft-1", "Draft goal");
        var pendingGoal = new Goal { Id = "pending-1", Description = "Pending goal", Status = GoalStatus.Pending };
        await _store.CreateGoalAsync(pendingGoal, ct);

        var result = await _composer.ListGoalsAsync("Draft");

        Assert.Contains("1 goal(s)", result);
        Assert.Contains("draft-1", result);
        Assert.DoesNotContain("pending-1", result);
    }

    [Fact]
    public async Task ListGoals_Empty_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.ListGoalsAsync();

        Assert.Contains("No goals found", result);
    }

    // ── search_goals ──

    [Fact]
    public async Task SearchGoals_MatchesDescription()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("auth-goal", "Implement JWT authentication for the API");
        await _composer.CreateGoalAsync("ui-goal", "Add dark mode toggle to settings page");

        var result = await _composer.SearchGoalsAsync("JWT");

        Assert.Contains("1 result", result);
        Assert.Contains("auth-goal", result);
        Assert.DoesNotContain("ui-goal", result);
    }

    [Fact]
    public async Task SearchGoals_NoResults_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.SearchGoalsAsync("nonexistent-term");

        Assert.Contains("No goals matching", result);
    }

    // ── Goal ID validation (via CreateGoalAsync) ──

    [Theory]
    [InlineData("valid-id")]
    [InlineData("a")]
    [InlineData("goal-123")]
    [InlineData("my-long-goal-id")]
    public async Task CreateGoal_ValidIds_Accepted(string id)
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateGoalAsync(id, "Test");

        Assert.Contains("✅", result);
    }

    [Theory]
    [InlineData("Invalid")]      // uppercase
    [InlineData("-leading")]     // leading hyphen
    [InlineData("trailing-")]    // trailing hyphen
    [InlineData("has space")]    // whitespace
    [InlineData("has_under")]    // underscore
    public async Task CreateGoal_InvalidIds_Rejected(string id)
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateGoalAsync(id, "Test");

        Assert.Contains("ERROR", result);
    }

    // ── delete_goal ──

    [Fact]
    public async Task DeleteGoal_DraftGoal_Deletes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateGoalAsync("del-draft", "To be deleted");

        var result = await _composer.DeleteGoalAsync("del-draft");

        Assert.Contains("✅", result);
        Assert.Contains("deleted", result);
        var goal = await _store.GetGoalAsync("del-draft", ct);
        Assert.Null(goal);
    }

    [Fact]
    public async Task DeleteGoal_FailedGoal_Deletes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateGoalAsync("del-failed", "Will fail");
        var goal = (await _store.GetGoalAsync("del-failed", ct))!;
        goal.Status = GoalStatus.Failed;
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.DeleteGoalAsync("del-failed");

        Assert.Contains("✅", result);
    }

    [Fact]
    public async Task DeleteGoal_PendingGoal_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateGoalAsync("del-pending", "Active goal");
        await _composer.ApproveGoalAsync("del-pending");

        var result = await _composer.DeleteGoalAsync("del-pending");

        Assert.Contains("❌", result);
        Assert.Contains("Pending", result);
        var goal = await _store.GetGoalAsync("del-pending", ct);
        Assert.NotNull(goal);
    }

    [Fact]
    public async Task CreateGoal_WithDependsOn_StoresDependencies()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateGoalAsync("dep-goal", "Goal with deps", depends_on: "goal-a, goal-b");

        Assert.Contains("✅", result);
        Assert.Contains("Dependencies: goal-a, goal-b", result);

        var goal = await _store.GetGoalAsync("dep-goal", ct);
        Assert.NotNull(goal);
        Assert.Equal(2, goal!.DependsOn.Count);
        Assert.Contains("goal-a", goal.DependsOn);
        Assert.Contains("goal-b", goal.DependsOn);
    }

    [Fact]
    public async Task CreateGoal_WithoutDependsOn_EmptyList()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("no-dep-goal", "Goal without deps");

        var goal = await _store.GetGoalAsync("no-dep-goal", ct);
        Assert.NotNull(goal);
        Assert.Empty(goal!.DependsOn);
    }

    [Fact]
    public async Task DeleteGoal_NotFound_ReturnsError()
    {
        var result = await _composer.DeleteGoalAsync("nonexistent");

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    // ── git tools — no repo manager configured ──

    [Fact]
    public async Task GitLog_NoRepoManager_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        // Composer constructed without a repoManager
        var result = await _composer.GitLogAsync("any-repo", cancellationToken: ct);

        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task GitDiff_NoRepoManager_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitDiffAsync("any-repo", "HEAD~1", cancellationToken: ct);

        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task GitShow_NoRepoManager_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitShowAsync("any-repo", "HEAD", cancellationToken: ct);

        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task GitBranch_NoRepoManager_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitBranchAsync("any-repo", cancellationToken: ct);

        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task GitBlame_NoRepoManager_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitBlameAsync("any-repo", "some/file.cs", cancellationToken: ct);

        Assert.Contains("not available", result);
    }

    // ── git tools — repo manager configured, unknown repo ──

    [Fact]
    public async Task GitLog_UnknownRepo_ReturnsNotFoundWithAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var result = await composer.GitLogAsync("nonexistent-repo", cancellationToken: ct);

            Assert.Contains("nonexistent-repo", result);
            Assert.Contains("not found", result);
            Assert.Contains("Available", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── git tools — parameter validation ──

    [Fact]
    public async Task GitLog_InvalidFormat_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitLogAsync("any-repo", format: "invalid", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("format", result);
    }

    [Fact]
    public async Task GitDiff_MissingRef1_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitDiffAsync("any-repo", "", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("ref1", result);
    }

    [Fact]
    public async Task GitShow_MissingRef_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitShowAsync("any-repo", "", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("ref", result);
    }

    [Fact]
    public async Task GitBlame_MissingPath_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitBlameAsync("any-repo", "", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("path", result);
    }

    // ── git tools — real git repo ──

    private static string InitTempGitRepo(string basePath)
    {
        var reposDir = Path.Combine(basePath, "repos");
        var repoDir = Path.Combine(reposDir, "test-repo");
        Directory.CreateDirectory(repoDir);

        static void Git(string workDir, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
        }

        Git(repoDir, "init", "-b", "main");
        Git(repoDir, "config", "user.email", "test@test.com");
        Git(repoDir, "config", "user.name", "Test");

        File.WriteAllText(Path.Combine(repoDir, "README.md"), "# Hello\nLine 2\nLine 3\n");
        Git(repoDir, "add", "README.md");
        Git(repoDir, "commit", "-m", "Initial commit");

        return basePath;
    }

    [Fact]
    public async Task GitLog_ValidRepo_ReturnsHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var result = await composer.GitLogAsync("test-repo", max_count: 5, cancellationToken: ct);

            Assert.Contains("Initial commit", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task GitBranch_ValidRepo_ListsBranches()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var result = await composer.GitBranchAsync("test-repo", cancellationToken: ct);

            Assert.Contains("main", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task GitShow_ValidRepo_ReturnsCommitDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var result = await composer.GitShowAsync("test-repo", "HEAD", cancellationToken: ct);

            Assert.Contains("Initial commit", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task GitBlame_ValidRepo_ReturnsBlameLines()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var result = await composer.GitBlameAsync("test-repo", "README.md", cancellationToken: ct);

            Assert.Contains("Hello", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}

