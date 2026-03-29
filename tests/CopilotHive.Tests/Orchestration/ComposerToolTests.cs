using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;

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

    [Fact]
    public async Task CreateGoal_WithScope_SetsScope()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("scoped-goal", "Add new capability", scope: "Feature");

        var goal = await _store.GetGoalAsync("scoped-goal", ct);
        Assert.NotNull(goal);
        Assert.Equal(GoalScope.Feature, goal!.Scope);
    }

    [Fact]
    public async Task CreateGoal_WithBreakingScope_IncludesScopeInResponse()
    {
        var result = await _composer.CreateGoalAsync("breaking-goal", "Breaking change", scope: "Breaking");

        Assert.Contains("✅", result);
        Assert.Contains("Breaking", result);
        Assert.Contains("Scope:", result);
    }

    [Fact]
    public async Task CreateGoal_DefaultScope_IsPatch()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("default-scope-goal", "Patch something");

        var goal = await _store.GetGoalAsync("default-scope-goal", ct);
        Assert.NotNull(goal);
        Assert.Equal(GoalScope.Patch, goal!.Scope);
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

    [Fact]
    public async Task UpdateGoal_Status_InvalidTransition_DraftToCompleted_ReturnsError()
    {
        // Goal starts in Draft; transitioning to Completed is blocked by the valid-values guard
        await _composer.CreateGoalAsync("transition-invalid1", "Test goal");
        var result = await _composer.UpdateGoalAsync("transition-invalid1", "status", "Completed");

        Assert.Contains("❌", result);
        Assert.Contains("Can only set status to Draft or Pending", result);
    }

    [Fact]
    public async Task UpdateGoal_Status_InvalidTransition_PendingToCompleted_ReturnsError()
    {
        // Pending→Completed is blocked by the valid-values guard
        await _composer.CreateGoalAsync("transition-invalid2", "Test goal");
        await _composer.UpdateGoalAsync("transition-invalid2", "status", "Pending");
        var result = await _composer.UpdateGoalAsync("transition-invalid2", "status", "Completed");

        Assert.Contains("❌", result);
        Assert.Contains("Can only set status to Draft or Pending", result);
    }

    [Fact]
    public async Task UpdateGoal_Status_InvalidTransition_DraftToDraft_ReturnsError()
    {
        // Draft→Draft is not a valid transition
        await _composer.CreateGoalAsync("transition-invalid3", "Test goal");
        var result = await _composer.UpdateGoalAsync("transition-invalid3", "status", "Draft");

        Assert.Contains("❌", result);
        Assert.Contains("Invalid transition", result);
    }

    [Fact]
    public async Task UpdateGoal_Status_InvalidTransition_PendingToPending_ReturnsError()
    {
        // Pending→Pending is not a valid transition
        await _composer.CreateGoalAsync("transition-invalid4", "Test goal");
        await _composer.UpdateGoalAsync("transition-invalid4", "status", "Pending"); // Draft→Pending
        var result = await _composer.UpdateGoalAsync("transition-invalid4", "status", "Pending");

        Assert.Contains("❌", result);
        Assert.Contains("Invalid transition", result);
    }

    [Fact]
    public async Task UpdateGoal_Status_ValidTransition_PendingToDraft_ReturnsSuccess()
    {
        // Draft→Pending→Draft is a valid round-trip
        await _composer.CreateGoalAsync("transition-valid1", "Test goal");
        await _composer.UpdateGoalAsync("transition-valid1", "status", "Pending");
        var result = await _composer.UpdateGoalAsync("transition-valid1", "status", "Draft");

        Assert.Contains("✅", result);
    }

    [Fact]
    public async Task UpdateGoal_Status_InvalidTransition_InProgressToDraft_ReturnsError()
    {
        // Set goal to InProgress directly then try to update to Draft
        await _composer.CreateGoalAsync("transition-inprogress1", "Test goal");
        var ct = TestContext.Current.CancellationToken;
        var goal = await _store.GetGoalAsync("transition-inprogress1", ct);
        Assert.NotNull(goal);
        goal!.Status = GoalStatus.InProgress;
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync("transition-inprogress1", "status", "Draft");

        Assert.Contains("❌", result);
        Assert.Contains("Invalid transition", result);
    }

    [Fact]
    public async Task UpdateGoal_Status_InvalidTransition_CompletedToDraft_ReturnsError()
    {
        // Set goal to Completed directly then try to update to Draft
        await _composer.CreateGoalAsync("transition-completed1", "Test goal");
        var ct = TestContext.Current.CancellationToken;
        var goal = await _store.GetGoalAsync("transition-completed1", ct);
        Assert.NotNull(goal);
        goal!.Status = GoalStatus.Completed;
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync("transition-completed1", "status", "Draft");

        Assert.Contains("❌", result);
        Assert.Contains("Invalid transition", result);
    }

    [Fact]
    public async Task UpdateGoal_Status_InvalidTransition_FailedToDraft_ReturnsError()
    {
        // Set goal to Failed directly then try to update to Draft
        await _composer.CreateGoalAsync("transition-failed1", "Test goal");
        var ct = TestContext.Current.CancellationToken;
        var goal = await _store.GetGoalAsync("transition-failed1", ct);
        Assert.NotNull(goal);
        goal!.Status = GoalStatus.Failed;
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync("transition-failed1", "status", "Draft");

        Assert.Contains("❌", result);
        Assert.Contains("Invalid transition", result);
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

    [Fact]
    public async Task DeleteGoal_FailedGoal_WithRepoManager_CallsDeleteRemoteBranchForEachRepo()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a failed goal with two repositories
        await _composer.CreateGoalAsync("failed-goal-branches", "Will fail", repositories: "repo-a, repo-b");
        var goal = (await _store.GetGoalAsync("failed-goal-branches", ct))!;
        goal.Status = GoalStatus.Failed;
        await _store.UpdateGoalAsync(goal, ct);

        // Mock the repo manager
        var mockRepoManager = new Mock<IBrainRepoManager>();
        mockRepoManager.Setup(r => r.DeleteRemoteBranchAsync("repo-a", "copilothive/failed-goal-branches", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();
        mockRepoManager.Setup(r => r.DeleteRemoteBranchAsync("repo-b", "copilothive/failed-goal-branches", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: mockRepoManager.Object,
            stateDir: Path.GetTempPath());

        var result = await composer.DeleteGoalAsync("failed-goal-branches");

        // Verify goal deleted successfully
        Assert.Contains("✅", result);
        Assert.Contains("deleted", result);
        var deletedGoal = await _store.GetGoalAsync("failed-goal-branches", ct);
        Assert.Null(deletedGoal);

        // Verify DeleteRemoteBranchAsync was called for each repository
        mockRepoManager.Verify(r => r.DeleteRemoteBranchAsync("repo-a", "copilothive/failed-goal-branches", It.IsAny<CancellationToken>()), Times.Once);
        mockRepoManager.Verify(r => r.DeleteRemoteBranchAsync("repo-b", "copilothive/failed-goal-branches", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteGoal_DraftGoal_WithRepoManager_DoesNotAttemptBranchCleanup()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a draft goal with a repository
        await _composer.CreateGoalAsync("draft-no-branch", "Draft goal", repositories: "repo-a");

        // Mock the repo manager
        var mockRepoManager = new Mock<IBrainRepoManager>();
        mockRepoManager.Setup(r => r.DeleteRemoteBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Should not be called for Draft goals"));

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: mockRepoManager.Object,
            stateDir: Path.GetTempPath());

        var result = await composer.DeleteGoalAsync("draft-no-branch");

        Assert.Contains("✅", result);
        // Goal is removed from store (Draft goals delete fine with no branch cleanup)
        var deletedGoal = await _store.GetGoalAsync("draft-no-branch", ct);
        Assert.Null(deletedGoal);

        // Verify DeleteRemoteBranchAsync was never called for Draft goals
        mockRepoManager.Verify(r => r.DeleteRemoteBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGoal_FailedGoal_BestEffortCleanup_StillSucceedsWhenDeleteRemoteBranchThrows()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a failed goal with repositories
        await _composer.CreateGoalAsync("failed-cleanup", "Will fail", repositories: "repo-a");
        var goal = (await _store.GetGoalAsync("failed-cleanup", ct))!;
        goal.Status = GoalStatus.Failed;
        await _store.UpdateGoalAsync(goal, ct);

        // Mock the repo manager to throw when DeleteRemoteBranchAsync is called
        var mockRepoManager = new Mock<IBrainRepoManager>();
        mockRepoManager.Setup(r => r.DeleteRemoteBranchAsync("repo-a", "copilothive/failed-cleanup", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Git operation failed"))
            .Verifiable();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: mockRepoManager.Object,
            stateDir: Path.GetTempPath());

        // DeleteRemoteBranchAsync will throw, but goal deletion should still succeed - this is "best-effort"
        var result = await composer.DeleteGoalAsync("failed-cleanup");

        // Verify the goal was deleted despite branch cleanup issues
        Assert.Contains("✅", result);
        Assert.Contains("deleted", result);
        var deletedGoal = await _store.GetGoalAsync("failed-cleanup", ct);
        Assert.Null(deletedGoal);

        // Verify that the cleanup was attempted (even though it threw)
        mockRepoManager.Verify(r => r.DeleteRemoteBranchAsync("repo-a", "copilothive/failed-cleanup", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── cancel_goal ──

    [Fact]
    public async Task CancelGoal_InProgressGoal_ReturnsSuccessMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Create goal and set to InProgress
            await _composer.CreateGoalAsync("cancel-inprogress", "Goal to cancel");
            var goal = await _store.GetGoalAsync("cancel-inprogress", ct);
            Assert.NotNull(goal);
            goal!.Status = GoalStatus.InProgress;
            await _store.UpdateGoalAsync(goal, ct);

            // Create dispatcher and composer with GoalDispatcher
            // Pass real store so cancellation updates propagate to the persisted goal
            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var goalManager = new GoalManager();
            goalManager.AddSource(new FakeGoalSource(goal, _store));
            var pipelineManager = new GoalPipelineManager();
            var dispatcher = new GoalDispatcher(
                goalManager,
                pipelineManager,
                new TaskQueue(),
                new GrpcWorkerGateway(new WorkerPool()),
                new TaskCompletionNotifier(),
                NullLogger<GoalDispatcher>.Instance,
                repoManager);

            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir,
                goalDispatcher: dispatcher);

            var result = await composer.CancelGoalAsync("cancel-inprogress");

            Assert.Contains("✅", result);
            Assert.Contains("cancelled", result);

            // Verify the persisted goal state was updated
            var persistedGoal = await _store.GetGoalAsync("cancel-inprogress", ct);
            Assert.NotNull(persistedGoal);
            Assert.Equal(GoalStatus.Failed, persistedGoal!.Status);
            Assert.Equal("Cancelled by user", persistedGoal.FailureReason);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task CancelGoal_PendingGoal_ReturnsSuccessMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Create goal in Pending status
            await _composer.CreateGoalAsync("cancel-pending", "Goal to cancel");
            await _composer.ApproveGoalAsync("cancel-pending"); // Draft → Pending

            var goal = await _store.GetGoalAsync("cancel-pending", ct);
            Assert.NotNull(goal);

            // Create dispatcher and composer with GoalDispatcher
            // Pass real store so cancellation updates propagate to the persisted goal
            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var goalManager = new GoalManager();
            goalManager.AddSource(new FakeGoalSource(goal!, _store));
            var pipelineManager = new GoalPipelineManager();
            var dispatcher = new GoalDispatcher(
                goalManager,
                pipelineManager,
                new TaskQueue(),
                new GrpcWorkerGateway(new WorkerPool()),
                new TaskCompletionNotifier(),
                NullLogger<GoalDispatcher>.Instance,
                repoManager);

            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir,
                goalDispatcher: dispatcher);

            var result = await composer.CancelGoalAsync("cancel-pending");

            Assert.Contains("✅", result);
            Assert.Contains("cancelled", result);

            // Verify the persisted goal state was updated
            var persistedGoal = await _store.GetGoalAsync("cancel-pending", ct);
            Assert.NotNull(persistedGoal);
            Assert.Equal(GoalStatus.Failed, persistedGoal!.Status);
            Assert.Equal("Cancelled by user", persistedGoal.FailureReason);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task CancelGoal_CompletedGoal_ReturnsErrorMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create goal and set to Completed
        await _composer.CreateGoalAsync("cancel-completed", "Completed goal");
        var goal = await _store.GetGoalAsync("cancel-completed", ct);
        Assert.NotNull(goal);
        goal!.Status = GoalStatus.Completed;
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.CancelGoalAsync("cancel-completed");

        Assert.Contains("❌", result);
        Assert.Contains("Only InProgress or Pending goals can be cancelled", result);
    }

    [Fact]
    public async Task CancelGoal_NonExistentGoal_ReturnsErrorMessage()
    {
        var result = await _composer.CancelGoalAsync("nonexistent-goal");

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    // ── get_goal — iteration detail format ──

    [Fact]
    public async Task GetGoal_WithIterations_ShowsPerPhaseDetail()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("iter-detail", "Goal with iterations");

        var summary = new IterationSummary
        {
            Iteration = 1,
            ReviewVerdict = "reject",
            TestCounts = new TestCounts { Passed = 840, Total = 840, Failed = 0 },
            Phases =
            [
                new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 45.2 },
                new PhaseResult { Name = "Testing", Result = "pass", DurationSeconds = 120.1 },
                new PhaseResult { Name = "Review", Result = "fail", DurationSeconds = 30.5 },
            ],
        };
        await _store.AddIterationAsync("iter-detail", summary, ct);

        var result = await _composer.GetGoalAsync("iter-detail");

        // Header uses new per-iteration format
        Assert.Contains("### Iteration 1 (review: reject)", result);
        // Per-phase lines with duration
        Assert.Contains("- Coding: pass (45.2s)", result);
        Assert.Contains("- Testing: pass (120.1s) — 840/840", result);
        Assert.Contains("- Review: fail (30.5s)", result);
        // Old summary format must not be present
        Assert.DoesNotContain("**Iteration 1:**", result);
    }

    [Fact]
    public async Task GetGoal_WithIterations_NoReviewVerdict_OmitsSuffix()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("iter-no-review", "Goal without review");
        var summary = new IterationSummary
        {
            Iteration = 1,
            ReviewVerdict = null,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 10.0 }],
        };
        await _store.AddIterationAsync("iter-no-review", summary, ct);

        var result = await _composer.GetGoalAsync("iter-no-review");

        Assert.Contains("### Iteration 1\n", result);
        Assert.DoesNotContain("(review:", result);
    }

    [Fact]
    public async Task GetGoal_TestingPhaseWithNoTestCounts_OmitsTestSuffix()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("iter-no-counts", "Goal without test counts");
        var summary = new IterationSummary
        {
            Iteration = 1,
            TestCounts = null,
            Phases = [new PhaseResult { Name = "Testing", Result = "fail", DurationSeconds = 5.0 }],
        };
        await _store.AddIterationAsync("iter-no-counts", summary, ct);

        var result = await _composer.GetGoalAsync("iter-no-counts");

        // Testing line without test counts suffix
        Assert.Contains("- Testing: fail (5.0s)\n", result);
        Assert.DoesNotContain(" — ", result);
    }

    // ── get_phase_output ──

    [Fact]
    public async Task GetPhaseOutput_ReturnsWorkerOutputFromPhaseResult()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-out", "Goal for phase output");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 10.0, WorkerOutput = "coder log line 1\ncoder log line 2" },
            ],
        };
        await _store.AddIterationAsync("phase-out", summary, ct);

        var result = await _composer.GetPhaseOutputAsync("phase-out", 1, "Coding");

        Assert.Contains("coder log line 1", result);
        Assert.Contains("coder log line 2", result);
    }

    [Fact]
    public async Task GetPhaseOutput_CaseInsensitivePhase_Matches()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-case", "Goal for case test");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Testing", Result = "pass", DurationSeconds = 5.0, WorkerOutput = "test output" }],
        };
        await _store.AddIterationAsync("phase-case", summary, ct);

        var result = await _composer.GetPhaseOutputAsync("phase-case", 1, "testing");

        Assert.Contains("test output", result);
    }

    [Fact]
    public async Task GetPhaseOutput_FallsBackToPhaseOutputs_WhenWorkerOutputNull()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-fallback", "Goal for fallback test");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 10.0, WorkerOutput = null }],
            PhaseOutputs = new Dictionary<string, string> { ["coder-1"] = "fallback coder output" },
        };
        await _store.AddIterationAsync("phase-fallback", summary, ct);

        var result = await _composer.GetPhaseOutputAsync("phase-fallback", 1, "Coding");

        Assert.Contains("fallback coder output", result);
    }

    [Theory]
    [InlineData("Coding", "coder")]
    [InlineData("Testing", "tester")]
    [InlineData("Review", "reviewer")]
    [InlineData("DocWriting", "docwriter")]
    [InlineData("Improve", "improver")]
    public async Task GetPhaseOutput_RoleKeyMapping_FallsBackCorrectly(string phaseName, string rolePrefix)
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = $"phase-map-{phaseName.ToLower()}";

        await _composer.CreateGoalAsync(goalId, "Role key mapping test");
        var summary = new IterationSummary
        {
            Iteration = 2,
            Phases = [new PhaseResult { Name = phaseName, Result = "pass", DurationSeconds = 1.0, WorkerOutput = null }],
            PhaseOutputs = new Dictionary<string, string> { [$"{rolePrefix}-2"] = $"output for {phaseName}" },
        };
        await _store.AddIterationAsync(goalId, summary, ct);

        var result = await _composer.GetPhaseOutputAsync(goalId, 2, phaseName);

        Assert.Contains($"output for {phaseName}", result);
    }

    [Fact]
    public async Task GetPhaseOutput_TruncatesToMaxLines()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-trunc", "Truncation test");
        var longOutput = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"Line {i}"));
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 1.0, WorkerOutput = longOutput }],
        };
        await _store.AddIterationAsync("phase-trunc", summary, ct);

        var result = await _composer.GetPhaseOutputAsync("phase-trunc", 1, "Coding", max_lines: 10);

        Assert.Contains("truncated", result);
        Assert.Contains("300 lines total", result);
        Assert.DoesNotContain("Line 300", result);
        Assert.Contains("Line 1", result);
    }

    [Fact]
    public async Task GetPhaseOutput_GoalNotFound_ReturnsMessage()
    {
        var result = await _composer.GetPhaseOutputAsync("nonexistent-goal", 1, "Coding");

        Assert.Equal("Goal not found", result);
    }

    [Fact]
    public async Task GetPhaseOutput_IterationNotFound_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-no-iter", "No iterations");

        var result = await _composer.GetPhaseOutputAsync("phase-no-iter", 5, "Coding");

        Assert.Equal("Iteration 5 not found", result);
    }

    [Fact]
    public async Task GetPhaseOutput_PhaseNotFound_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-no-phase", "Has iteration but no such phase");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 1.0 }],
        };
        await _store.AddIterationAsync("phase-no-phase", summary, ct);

        var result = await _composer.GetPhaseOutputAsync("phase-no-phase", 1, "Review");

        Assert.Equal("Phase 'Review' not found in iteration 1", result);
    }

    [Fact]
    public async Task GetPhaseOutput_UnknownPhase_ReturnsFailFastMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-unknown", "Unknown phase test");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Planning", Result = "pass", DurationSeconds = 1.0, WorkerOutput = null }],
        };
        await _store.AddIterationAsync("phase-unknown", summary, ct);

        var result = await _composer.GetPhaseOutputAsync("phase-unknown", 1, "Planning");

        Assert.Contains("Unknown phase 'Planning'", result);
        Assert.Contains("Coding, Testing, Review, DocWriting, Improve", result);
    }

    [Fact]
    public async Task GetPhaseOutput_NoOutputRecorded_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-no-output", "No output recorded");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 1.0, WorkerOutput = null }],
            // PhaseOutputs is empty — no fallback
        };
        await _store.AddIterationAsync("phase-no-output", summary, ct);

        var result = await _composer.GetPhaseOutputAsync("phase-no-output", 1, "Coding");

        Assert.Equal("No output recorded for phase Coding in iteration 1", result);
    }

    [Fact]
    public async Task GetPhaseOutput_InvalidIteration_ReturnsValidationError()
    {
        var result = await _composer.GetPhaseOutputAsync("some-goal", 0, "Coding");

        Assert.Equal("Iteration must be a positive number", result);
    }

    [Fact]
    public async Task GetPhaseOutput_EmptyId_ReturnsValidationError()
    {
        var result = await _composer.GetPhaseOutputAsync("", 1, "Coding");

        Assert.Equal("Goal ID is required", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetPhaseOutput_EmptyOrWhitespacePhase_ReturnsValidationError(string phase)
    {
        var result = await _composer.GetPhaseOutputAsync("some-goal", 1, phase);

        Assert.Equal("ERROR: Invalid parameters: phase is required", result);
    }

    // ── GetPhaseOutputAsync with content parameter (brain_prompt / worker_prompt) ──

    [Fact]
    public async Task GetPhaseOutput_ContentOutput_DefaultBehaviorUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("content-output", "Goal for content output test");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = "Coding", Result = "pass", DurationSeconds = 10.0, WorkerOutput = "coder output here" }],
        };
        await _store.AddIterationAsync("content-output", summary, ct);

        // Explicitly passing "output" should behave same as default
        var result = await _composer.GetPhaseOutputAsync("content-output", 1, "Coding", content: "output");
        Assert.Contains("coder output here", result);
    }

    [Fact]
    public async Task GetPhaseOutput_BrainPrompt_NoPipelineConversation_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("no-pipeline", "Goal without pipeline conversation");

        // Without PipelineStore wired, there's no conversation to retrieve
        var result = await _composer.GetPhaseOutputAsync("no-pipeline", 1, "Coding", content: "brain_prompt");

        Assert.Contains("No pipeline conversation available", result);
    }

    [Fact]
    public async Task GetPhaseOutput_WorkerPrompt_NoPipelineConversation_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("no-pipeline-wp", "Goal without pipeline conversation");

        var result = await _composer.GetPhaseOutputAsync("no-pipeline-wp", 1, "Coding", content: "worker_prompt");

        Assert.Contains("No pipeline conversation available", result);
    }

    // ── GetPhaseOutputAsync with PipelineStore for brain_prompt/worker_prompt ──

    [Fact]
    public async Task GetPhaseOutput_BrainPrompt_WithPipelineStore_ReturnsPrompt()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a PipelineStore and wire it to a new SqliteGoalStore
        var pipelineStore = new PipelineStore(":memory:", NullLogger<PipelineStore>.Instance);
        var storeWithPipeline = new SqliteGoalStore(_connection, NullLogger<SqliteGoalStore>.Instance, pipelineStore);
        var composerWithPipeline = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            storeWithPipeline,
            stateDir: Path.GetTempPath());

        // Create a goal and pipeline with conversation
        await composerWithPipeline.CreateGoalAsync("brain-prompt-goal", "Goal with brain prompt");
        var goal = await storeWithPipeline.GetGoalAsync("brain-prompt-goal", ct);
        Assert.NotNull(goal);

        var pipeline = new GoalPipeline(goal, maxRetries: 3);
        pipeline.Conversation.Add(new ConversationEntry("user", "Brain asks coder to implement X", Iteration: 1, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Your task: implement X", Iteration: 1, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("coder", "Coder completed.", Iteration: 1, Purpose: "worker-output"));

        pipelineStore.SavePipeline(pipeline);

        // Now test GetPhaseOutputAsync with content: "brain_prompt"
        var result = await composerWithPipeline.GetPhaseOutputAsync("brain-prompt-goal", 1, "Coding", content: "brain_prompt");

        Assert.Contains("Brain asks coder to implement X", result);
    }

    [Fact]
    public async Task GetPhaseOutput_WorkerPrompt_WithPipelineStore_ReturnsPrompt()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a PipelineStore and wire it to a new SqliteGoalStore
        var pipelineStore = new PipelineStore(":memory:", NullLogger<PipelineStore>.Instance);
        var storeWithPipeline = new SqliteGoalStore(_connection, NullLogger<SqliteGoalStore>.Instance, pipelineStore);
        var composerWithPipeline = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            storeWithPipeline,
            stateDir: Path.GetTempPath());

        // Create a goal and pipeline with conversation
        await composerWithPipeline.CreateGoalAsync("worker-prompt-goal", "Goal with worker prompt");
        var goal = await storeWithPipeline.GetGoalAsync("worker-prompt-goal", ct);
        Assert.NotNull(goal);

        var pipeline = new GoalPipeline(goal, maxRetries: 3);
        pipeline.Conversation.Add(new ConversationEntry("user", "Brain prompt for tester", Iteration: 1, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Your task: test the code", Iteration: 1, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("tester", "Tests passed!", Iteration: 1, Purpose: "worker-output"));

        pipelineStore.SavePipeline(pipeline);

        // Now test GetPhaseOutputAsync with content: "worker_prompt"
        var result = await composerWithPipeline.GetPhaseOutputAsync("worker-prompt-goal", 1, "Testing", content: "worker_prompt");

        Assert.Contains("Your task: test the code", result);
    }

    [Fact]
    public async Task GetPhaseOutput_BrainPrompt_NoPromptForPhase_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a PipelineStore and wire it
        var pipelineStore = new PipelineStore(":memory:", NullLogger<PipelineStore>.Instance);
        var storeWithPipeline = new SqliteGoalStore(_connection, NullLogger<SqliteGoalStore>.Instance, pipelineStore);
        var composerWithPipeline = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            storeWithPipeline,
            stateDir: Path.GetTempPath());

        // Create a goal and pipeline with conversation for Coding phase only
        await composerWithPipeline.CreateGoalAsync("partial-prompt-goal", "Goal with partial prompts");
        var goal = await storeWithPipeline.GetGoalAsync("partial-prompt-goal", ct);
        Assert.NotNull(goal);

        var pipeline = new GoalPipeline(goal, maxRetries: 3);
        pipeline.Conversation.Add(new ConversationEntry("user", "Brain for coder", Iteration: 1, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Task for coder", Iteration: 1, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("coder", "Done", Iteration: 1, Purpose: "worker-output"));

        pipelineStore.SavePipeline(pipeline);

        // Request brain_prompt for Review phase - should fail as no craft-prompt for reviewer
        var result = await composerWithPipeline.GetPhaseOutputAsync("partial-prompt-goal", 1, "Review", content: "brain_prompt");

        Assert.Contains("No brain prompt found", result);
    }

    [Fact]
    public void BuildComposerTools_IncludesGetPhaseOutput()
    {
        var tools = _composer.BuildComposerTools();
        var toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains("get_phase_output", toolNames);
    }

    [Fact]
    public void SystemPrompt_IncludesDrillIntoPhaseOutput()
    {
        Assert.Contains("get_phase_output", _composer.GetSystemPrompt());
        Assert.Contains("Drill into worker phase output", _composer.GetSystemPrompt());
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

    // ── git tools — path traversal prevention ──

    [Fact]
    public async Task GitLog_PathTraversal_ReturnsDenied()
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

            var result = await composer.GitLogAsync("../../../etc", cancellationToken: ct);

            Assert.Contains("Access denied", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── git tools — option injection prevention ──

    [Fact]
    public async Task GitLog_BranchStartingWithDash_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
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

            var result = await composer.GitLogAsync("test-repo", branch: "--output=/tmp/evil", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("cannot start with '-'", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task GitDiff_Ref1StartingWithDash_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitDiffAsync("any-repo", "--output=/tmp/evil", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("cannot start with '-'", result);
    }

    [Fact]
    public async Task GitDiff_Ref2StartingWithDash_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitDiffAsync("any-repo", "HEAD", ref2: "--output=/tmp/evil", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("cannot start with '-'", result);
    }

    [Fact]
    public async Task GitShow_RefStartingWithDash_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitShowAsync("any-repo", "--output=/tmp/evil", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("cannot start with '-'", result);
    }

    [Fact]
    public async Task GitBranch_PatternStartingWithDash_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitBranchAsync("any-repo", pattern: "--delete", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("cannot start with '-'", result);
    }

    // ── web_search — no API key ──

    [Fact]
    public async Task WebSearch_NoApiKey_ReturnsError()
    {
        // Composer created without ollamaApiKey — web_search should return an error
        var result = await _composer.WebSearchAsync("test query");

        Assert.Contains("❌", result);
        Assert.Contains("OLLAMA_API_KEY", result);
    }

    [Fact]
    public async Task WebFetch_NoApiKey_ReturnsError()
    {
        // Composer created without ollamaApiKey — web_fetch should return an error
        var result = await _composer.WebFetchAsync("https://example.com");

        Assert.Contains("❌", result);
        Assert.Contains("OLLAMA_API_KEY", result);
    }

    // ── web_search — with API key (mocked HTTP) ──

    [Fact]
    public async Task WebSearch_WithApiKey_FormatsResults()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"results":[{"title":"Test Page","url":"https://example.com","content":"Some content here."}]}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebSearchAsync("test query", 3);

        Assert.Contains("Test Page", result);
        Assert.Contains("https://example.com", result);
        Assert.Contains("Some content here.", result);
        // Short content (under 500 chars) should NOT have truncation marker
        Assert.DoesNotContain("…", result);
    }

    [Fact]
    public async Task WebSearch_TruncatesLongContent()
    {
        // Create content longer than 500 characters
        var longContent = new string('A', 600); // 600 chars, exceeds the 500 char limit
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            $$"""{"results":[{"title":"Long Result","url":"https://example.com/long","content":"{{longContent}}"}]}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebSearchAsync("test query", 3);

        // Verify truncation marker is present
        Assert.Contains("…", result);
        // Verify the original full content does NOT appear (it was truncated)
        Assert.DoesNotContain(new string('A', 600), result);
        // Verify truncated content is present (500 chars + "…")
        Assert.Contains(new string('A', 500) + "…", result);
    }

    [Fact]
    public async Task WebSearch_WithApiKey_SendsAuthorizationHeader()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"results":[]}""",
            req => { capturedRequest = req; });
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "my-secret-key");

        await composer.WebSearchAsync("something");

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("my-secret-key", capturedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task WebSearch_HttpError_ReturnsError()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebSearchAsync("test");

        Assert.Contains("❌", result);
        Assert.Contains("500", result);
    }

    [Fact]
    public async Task WebSearch_ClampsMaxResults()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        string? capturedBody = null;
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"results":[]}""",
            null,
            async req => { capturedBody = await req.Content!.ReadAsStringAsync(); });
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        // max_results=50 should be clamped to 10
        await composer.WebSearchAsync("test", 50);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"max_results\":10", capturedBody);
    }

    // ── web_fetch — with API key (mocked HTTP) ──

    [Fact]
    public async Task WebFetch_WithApiKey_FormatsResponse()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"title":"Example Domain","content":"This domain is for use in illustrative examples.","links":["https://www.iana.org/domains/example"]}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebFetchAsync("https://example.com");

        Assert.Contains("# Example Domain", result);
        Assert.Contains("This domain is for use in illustrative examples.", result);
        Assert.Contains("## Links", result);
        Assert.Contains("https://www.iana.org/domains/example", result);
    }

    [Fact]
    public async Task WebFetch_TruncatesLongContent()
    {
        // Build a response with many lines
        var manyLines = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"Line {i}"));
        var json = System.Text.Json.JsonSerializer.Serialize(new { title = "Long Page", content = manyLines, links = Array.Empty<string>() });

        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, json);
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebFetchAsync("https://example.com", max_lines: 10);

        Assert.Contains("truncated", result);
        Assert.DoesNotContain("Line 300", result);
    }

    [Fact]
    public async Task WebFetch_HttpError_ReturnsError()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.NotFound, "");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebFetchAsync("https://example.com");

        Assert.Contains("❌", result);
        Assert.Contains("404", result);
    }

    // ── web_search — additional edge cases ──

    [Fact]
    public async Task WebSearch_EmptyQuery_ReturnsError()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"results":[]}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebSearchAsync("");

        Assert.Contains("❌", result);
        Assert.Contains("query is required", result);
    }

    [Fact]
    public async Task WebSearch_WhitespaceQuery_ReturnsError()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"results":[]}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebSearchAsync("   ");

        Assert.Contains("❌", result);
        Assert.Contains("query is required", result);
    }

    [Fact]
    public async Task WebSearch_MultipleResults_FormatsAll()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"results":[{"title":"First","url":"https://a.com","content":"Content A"},{"title":"Second","url":"https://b.com","content":"Content B"},{"title":"Third","url":"https://c.com","content":"Content C"}]}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebSearchAsync("test", 10);

        Assert.Contains("### First", result);
        Assert.Contains("https://a.com", result);
        Assert.Contains("Content A", result);
        Assert.Contains("### Second", result);
        Assert.Contains("https://b.com", result);
        Assert.Contains("Content B", result);
        Assert.Contains("### Third", result);
        Assert.Contains("https://c.com", result);
        Assert.Contains("Content C", result);
    }

    [Fact]
    public async Task WebSearch_EmptyResults_ReturnsNoResults()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"results":[]}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebSearchAsync("nonexistent");

        Assert.Contains("No results found", result);
    }

    [Fact]
    public async Task WebSearch_MissingFieldsInResults_HandlesGracefully()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        // Some results missing title, url, or content fields
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"results":[{"title":"Has Title","url":"https://example.com"},{"url":"https://missing-title.com","content":"No title"}]}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebSearchAsync("test");

        // Should not throw and should still format available fields
        Assert.Contains("https://example.com", result);
        Assert.Contains("https://missing-title.com", result);
    }

    [Fact]
    public async Task WebSearch_NetworkError_ReturnsErrorMessage()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(
            req => throw new HttpRequestException("Network error"));
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebSearchAsync("test");

        Assert.Contains("❌", result);
        Assert.Contains("Network error", result);
    }

    // ── web_fetch — additional edge cases ──

    [Fact]
    public async Task WebFetch_EmptyUrl_ReturnsError()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"title":"","content":""}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebFetchAsync("");

        Assert.Contains("❌", result);
        Assert.Contains("url is required", result);
    }

    [Fact]
    public async Task WebFetch_WhitespaceUrl_ReturnsError()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"title":"","content":""}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebFetchAsync("   ");

        Assert.Contains("❌", result);
        Assert.Contains("url is required", result);
    }

    [Fact]
    public async Task WebFetch_WithApiKey_SendsAuthorizationHeader()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"title":"Test","content":"Content"}""",
            req => { capturedRequest = req; });
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "my-fetch-key");

        await composer.WebFetchAsync("https://example.com");

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("my-fetch-key", capturedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task WebFetch_NoLinks_OmitsLinksSection()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"title":"Simple Page","content":"Just content","links":[]}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebFetchAsync("https://example.com");

        Assert.Contains("# Simple Page", result);
        Assert.Contains("Just content", result);
        Assert.DoesNotContain("## Links", result);
    }

    [Fact]
    public async Task WebFetch_MissingLinksField_OmitsLinksSection()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        // Response without "links" field at all
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"title":"No Links","content":"Content without links"}""");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebFetchAsync("https://example.com");

        Assert.Contains("# No Links", result);
        Assert.DoesNotContain("## Links", result);
    }

    [Fact]
    public async Task WebFetch_NetworkError_ReturnsErrorMessage()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(
            req => throw new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebFetchAsync("https://example.com");

        Assert.Contains("❌", result);
        Assert.Contains("Connection refused", result);
    }

    [Fact]
    public async Task WebFetch_JsonError_ReturnsErrorMessage()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, "invalid json");
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebFetchAsync("https://example.com");

        Assert.Contains("❌", result);
    }

    // ── system prompt ──

    [Fact]
    public void SystemPrompt_WithApiKey_IncludesWebCapabilities()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        Assert.Contains("web_search", composer.GetSystemPrompt());
        Assert.Contains("web_fetch", composer.GetSystemPrompt());
    }

    [Fact]
    public void SystemPrompt_WithoutApiKey_DoesNotIncludeWebCapabilities()
    {
        // _composer is created without ollamaApiKey in the test constructor
        Assert.DoesNotContain("web_search", _composer.GetSystemPrompt());
        Assert.DoesNotContain("web_fetch", _composer.GetSystemPrompt());
    }

    [Fact]
    public void BuildComposerTools_WithoutApiKey_DoesNotIncludeWebTools()
    {
        var composer = new Composer("model", NullLogger<Composer>.Instance, _store, ollamaApiKey: null);
        var tools = composer.BuildComposerTools();
        var toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.DoesNotContain("web_search", toolNames);
        Assert.DoesNotContain("web_fetch", toolNames);
    }

    [Fact]
    public async Task WebSearch_Timeout_ReturnsErrorMessage()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(
            req => throw new TaskCanceledException("The request timed out."));
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebSearchAsync("test query");

        Assert.Contains("❌", result);
        // Should return an error message, not throw an exception
        Assert.Contains("timed out", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebFetch_Timeout_ReturnsErrorMessage()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(
            req => throw new TaskCanceledException("The request timed out."));
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://ollama.com/") };
        mockFactory.Setup(f => f.CreateClient("ollama-web")).Returns(httpClient);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: mockFactory.Object,
            ollamaApiKey: "test-key");

        var result = await composer.WebFetchAsync("https://example.com");

        Assert.Contains("❌", result);
        // Should return an error message, not throw an exception
        Assert.Contains("timed out", result, StringComparison.OrdinalIgnoreCase);
    }

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

    // ── system prompt — repo injection ──

    [Fact]
    public void SystemPrompt_WithConfiguredRepos_IncludesRepoSection()
    {
        var hiveConfig = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "my-repo", Url = "https://github.com/org/my-repo.git", DefaultBranch = "main" },
            ],
        };

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig);

        var prompt = composer.GetSystemPrompt();
        Assert.Contains("Configured repositories:", prompt);
        Assert.Contains("my-repo", prompt);
        Assert.Contains("https://github.com/org/my-repo.git", prompt);
        Assert.Contains("default branch: main", prompt);
    }

    [Fact]
    public void SystemPrompt_WithMultipleRepos_ListsAllRepos()
    {
        var hiveConfig = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "repo-a", Url = "https://github.com/org/repo-a.git", DefaultBranch = "main" },
                new RepositoryConfig { Name = "repo-b", Url = "https://github.com/org/repo-b.git", DefaultBranch = "develop" },
            ],
        };

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig);

        var prompt = composer.GetSystemPrompt();
        Assert.Contains("repo-a", prompt);
        Assert.Contains("repo-b", prompt);
        Assert.Contains("default branch: develop", prompt);
    }

    [Fact]
    public void SystemPrompt_WithNullHiveConfig_DoesNotIncludeRepoSection()
    {
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            hiveConfig: null);

        var prompt = composer.GetSystemPrompt();
        Assert.DoesNotContain("Configured repositories:", prompt);
    }

    [Fact]
    public void SystemPrompt_WithEmptyRepositoriesList_DoesNotIncludeRepoSection()
    {
        var hiveConfig = new HiveConfigFile { Repositories = [] };

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig);

        var prompt = composer.GetSystemPrompt();
        Assert.DoesNotContain("Configured repositories:", prompt);
    }

    [Fact]
    public void SystemPrompt_WithoutHiveConfig_StillIncludesDefaultContent()
    {
        // Backward-compatible: constructor without hiveConfig param still works
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath());

        var prompt = composer.GetSystemPrompt();
        Assert.Contains("You are the Composer", prompt);
        Assert.DoesNotContain("Configured repositories:", prompt);
    }

    // ── list_repositories ──

    [Fact]
    public async Task ListRepositories_NullHiveConfig_ReturnsNoRepositoriesMessage()
    {
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            hiveConfig: null);

        var result = await composer.ListRepositoriesAsync();

        Assert.Equal("No repositories configured.", result);
    }

    [Fact]
    public async Task ListRepositories_EmptyList_ReturnsNoRepositoriesMessage()
    {
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            hiveConfig: new HiveConfigFile { Repositories = [] });

        var result = await composer.ListRepositoriesAsync();

        Assert.Equal("No repositories configured.", result);
    }

    [Fact]
    public async Task ListRepositories_WithRepos_ReturnsFormattedList()
    {
        var hiveConfig = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "my-repo", Url = "https://github.com/org/my-repo.git", DefaultBranch = "main" },
            ],
        };
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig);

        var result = await composer.ListRepositoriesAsync();

        Assert.Contains("## Configured Repositories (1)", result);
        Assert.Contains("my-repo", result);
        Assert.Contains("https://github.com/org/my-repo.git", result);
        Assert.Contains("branch: main", result);
    }

    [Fact]
    public async Task ListRepositories_WithMultipleRepos_ListsAllRepos()
    {
        var hiveConfig = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "repo-a", Url = "https://github.com/org/repo-a.git", DefaultBranch = "main" },
                new RepositoryConfig { Name = "repo-b", Url = "https://github.com/org/repo-b.git", DefaultBranch = "develop" },
            ],
        };
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig);

        var result = await composer.ListRepositoriesAsync();

        Assert.Contains("## Configured Repositories (2)", result);
        Assert.Contains("repo-a", result);
        Assert.Contains("repo-b", result);
        Assert.Contains("branch: main", result);
        Assert.Contains("branch: develop", result);
    }

    [Fact]
    public void BuildComposerTools_IncludesListRepositoriesTool()
    {
        var tools = _composer.BuildComposerTools();
        var names = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains("list_repositories", names);
    }

    [Fact]
    public void SystemPrompt_MentionsListRepositoriesCapability()
    {
        var prompt = _composer.GetSystemPrompt();
        Assert.Contains("list_repositories", prompt);
    }

    // ── ask_user tool ──

    [Fact]
    public void BuildComposerTools_IncludesAskUserTool()
    {
        var tools = _composer.BuildComposerTools();
        var names = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains("ask_user", names);
    }

    [Fact]
    public void SystemPrompt_MentionsAskUserCapability()
    {
        var prompt = _composer.GetSystemPrompt();
        Assert.Contains("ask_user", prompt);
    }

    [Fact]
    public async Task AskUser_MissingQuestion_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.AskUserAsync("", cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("question is required", result);
    }

    [Fact]
    public async Task AskUser_InvalidType_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.AskUserAsync("Do you agree?", type: "InvalidType", cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("InvalidType", result);
    }

    [Fact]
    public async Task AskUser_SingleChoiceWithNoOptions_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.AskUserAsync("Pick one?", type: "SingleChoice", options: null, cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("Options are required", result);
    }

    [Fact]
    public async Task AskUser_YesNo_SetsPendingQuestionAndReturnsOnSubmit()
    {
        var ct = TestContext.Current.CancellationToken;

        // Start asking (will suspend until answered)
        var askTask = _composer.AskUserAsync("Confirm?", type: "YesNo", cancellationToken: ct);

        // Wait for PendingQuestion to be populated
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_composer.PendingQuestion is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);

        var pending = _composer.PendingQuestion;
        Assert.NotNull(pending);
        Assert.Equal("Confirm?", pending!.Text);
        Assert.Equal(QuestionType.YesNo, pending.Type);
        Assert.Equal(["Yes", "No"], _composer.PendingQuestion?.Options);
        Assert.Equal(2, pending.Options.Count);
        Assert.Contains("Yes", pending.Options);
        Assert.Contains("No", pending.Options);

        // Submit an answer
        _composer.SubmitAnswer("Yes — looks good");

        var result = await askTask;
        Assert.Equal("Yes — looks good", result);
        Assert.Null(_composer.PendingQuestion);
    }

    [Fact]
    public async Task AskUser_CancelQuestion_ReturnsCancellationMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var askTask = _composer.AskUserAsync("Are you sure?", type: "YesNo", cancellationToken: ct);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_composer.PendingQuestion is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);

        Assert.NotNull(_composer.PendingQuestion);

        _composer.CancelQuestion();

        var result = await askTask;
        Assert.Equal("User cancelled the question without answering.", result);
        Assert.Null(_composer.PendingQuestion);
    }

    [Fact]
    public async Task AskUser_SingleChoice_SetsPendingWithOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var askTask = _composer.AskUserAsync("Pick one?", type: "SingleChoice", options: "Alpha, Beta, Gamma", cancellationToken: ct);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_composer.PendingQuestion is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);

        var pending = _composer.PendingQuestion;
        Assert.NotNull(pending);
        Assert.Equal(QuestionType.SingleChoice, pending!.Type);
        Assert.Equal(3, pending.Options.Count);
        Assert.Contains("Alpha", pending.Options);

        _composer.SubmitAnswer("Beta");
        await askTask;
    }

    [Fact]
    public async Task AskUser_MultiChoice_SetsPendingWithOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var askTask = _composer.AskUserAsync("Pick all that apply?", type: "MultiChoice", options: "A, B, C", cancellationToken: ct);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_composer.PendingQuestion is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);

        var pending = _composer.PendingQuestion;
        Assert.NotNull(pending);
        Assert.Equal(QuestionType.MultiChoice, pending!.Type);

        _composer.SubmitAnswer("A, C");
        await askTask;
    }

    [Fact]
    public void SubmitAnswer_NoPendingQuestion_DoesNotThrow()
    {
        Assert.Null(_composer.PendingQuestion);
        _composer.SubmitAnswer("anything"); // Should not throw
    }

    [Fact]
    public void CancelQuestion_NoPendingQuestion_DoesNotThrow()
    {
        Assert.Null(_composer.PendingQuestion);
        _composer.CancelQuestion(); // Should not throw
    }

    [Fact]
    public async Task AskUser_OnQuestionAsked_EventIsRaised()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventRaised = false;
        _composer.OnQuestionAsked += () => { eventRaised = true; };

        var askTask = _composer.AskUserAsync("Event test?", type: "YesNo", cancellationToken: ct);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!eventRaised && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);

        Assert.True(eventRaised);

        _composer.SubmitAnswer("Yes");
        await askTask;
    }

    // ── IsContextOverflowError ──

    [Fact]
    public void IsContextOverflowError_NullException_ReturnsFalse()
    {
        Assert.False(Composer.IsContextOverflowError(null));
    }

    [Fact]
    public void IsContextOverflowError_UnrelatedMessage_ReturnsFalse()
    {
        var ex = new InvalidOperationException("some other error");
        Assert.False(Composer.IsContextOverflowError(ex));
    }

    [Fact]
    public void IsContextOverflowError_ExactToken_ReturnsTrue()
    {
        var ex = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        Assert.True(Composer.IsContextOverflowError(ex));
    }

    [Fact]
    public void IsContextOverflowError_UpperCase_ReturnsTrue()
    {
        var ex = new InvalidOperationException("MODEL_MAX_PROMPT_TOKENS_EXCEEDED: context limit hit");
        Assert.True(Composer.IsContextOverflowError(ex));
    }

    [Fact]
    public void IsContextOverflowError_TokenInInnerException_ReturnsTrue()
    {
        var inner = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        var outer = new InvalidOperationException("LLM call failed", inner);
        Assert.True(Composer.IsContextOverflowError(outer));
    }

    [Fact]
    public void IsContextOverflowError_NestedInnerExceptionWithToken_ReturnsTrue()
    {
        var innermost = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        var middle = new InvalidOperationException("Middle layer", innermost);
        var outer = new InvalidOperationException("Outer layer", middle);
        Assert.True(Composer.IsContextOverflowError(outer));
    }

    [Fact]
    public void IsContextOverflowError_OuterHasUnrelatedInnerHasToken_ReturnsTrue()
    {
        var inner = new Exception("model_max_prompt_tokens_exceeded: limit reached");
        var outer = new Exception("Request failed", inner);
        Assert.True(Composer.IsContextOverflowError(outer));
    }

    [Fact]
    public void IsContextOverflowError_NeitherOuterNorInnerHasToken_ReturnsFalse()
    {
        var inner = new InvalidOperationException("inner error");
        var outer = new InvalidOperationException("outer error", inner);
        Assert.False(Composer.IsContextOverflowError(outer));
    }

    // ── Session reset on context overflow ──

    /// <summary>
    /// Helper that uses reflection to inject a fake <see cref="IChatClient"/> into a
    /// <see cref="Composer"/> instance and then builds its internal <c>CodingAgent</c>
    /// by calling the private <c>RecreateAgent()</c> method.  Call this BEFORE
    /// <c>SendMessage</c> — no <c>ConnectAsync</c> call is needed, making the test
    /// fully hermetic (no real LLM endpoint required).
    /// </summary>
    private static void InjectFakeChatClient(Composer composer, IChatClient fakeClient)
    {
        var composerType = typeof(Composer);

        // Replace the private _chatClient field
        var chatClientField = composerType.GetField("_chatClient",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_chatClient field not found on Composer");
        chatClientField.SetValue(composer, fakeClient);

        // Rebuild the CodingAgent with the injected client
        var recreateAgent = composerType.GetMethod("RecreateAgent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecreateAgent method not found on Composer");
        recreateAgent.Invoke(composer, null);
    }

    [Fact]
    public async Task RunStreaming_ContextOverflow_ResetsSessionAndAppendsWarning()
    {
        // Arrange: create a Composer whose AI client throws a context-overflow error
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Write a fake session file so we can verify it gets deleted on reset
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            await File.WriteAllTextAsync(sessionFile, "{}", ct);

            var testLogger = new TestLogger<Composer>();
            var composer = new Composer(
                "test-model",
                testLogger,
                _store,
                stateDir: tmpDir);

            // Inject the fake IChatClient BEFORE any agent is created so we never
            // call SDK.ChatClientFactory.Create (which requires a real LLM endpoint).
            // InjectFakeChatClient sets _chatClient and calls RecreateAgent() internally.
            var overflowEx = new InvalidOperationException("model_max_prompt_tokens_exceeded");
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetStreamingResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Throws(overflowEx);
            // Also cover the non-streaming path in case CodingAgent uses it
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(overflowEx);

            InjectFakeChatClient(composer, mockClient.Object);

            // The session file exists before we trigger the overflow
            Assert.True(File.Exists(sessionFile));

            // Act: trigger RunStreamingAsync via SendMessage, which fires a background task
            composer.SendMessage("hello");

            // Wait for IsStreaming to become false (streaming completed or caught the overflow)
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (composer.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, CancellationToken.None);

            Assert.False(composer.IsStreaming, "Streaming should have finished after overflow");

            // Assert: session file deleted
            Assert.False(File.Exists(sessionFile), "Session file should be deleted after overflow reset");

            // Assert: warning appended to StreamingContent
            Assert.Contains("⚠️", composer.StreamingContent);
            Assert.Contains("Context limit reached", composer.StreamingContent);
            Assert.Contains("Session has been reset automatically", composer.StreamingContent);

            // Assert: stats reflect a fresh session (zero messages)
            var stats = composer.GetStats();
            Assert.NotNull(stats);
            Assert.Equal(0, stats!.MessageCount);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunStreaming_ContextOverflow_WarningIsLoggedAtWarningLevel()
    {
        // Verifies that the context-overflow catch block logs at Warning level
        // with the expected message, and does NOT log at Error level
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var ct = TestContext.Current.CancellationToken;

            var testLogger = new TestLogger<Composer>();
            var composer = new Composer(
                "test-model",
                testLogger,
                _store,
                stateDir: tmpDir);

            // Inject the fake IChatClient BEFORE any agent is created so we never
            // call SDK.ChatClientFactory.Create (which requires a real LLM endpoint).
            // InjectFakeChatClient sets _chatClient and calls RecreateAgent() internally.
            var overflowEx = new InvalidOperationException("model_max_prompt_tokens_exceeded");
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetStreamingResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Throws(overflowEx);
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(overflowEx);

            InjectFakeChatClient(composer, mockClient.Object);

            // Act: trigger the streaming path
            composer.SendMessage("hello");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (composer.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, CancellationToken.None);

            Assert.False(composer.IsStreaming, "Streaming should have finished after overflow");

            // Assert: a Warning-level log entry containing the overflow message
            var warningEntries = testLogger.LogEntries
                .Where(e => e.LogLevel == LogLevel.Warning)
                .ToList();
            Assert.NotEmpty(warningEntries);
            Assert.Contains(warningEntries,
                e => e.Message.Contains("context overflow", StringComparison.OrdinalIgnoreCase)
                  || e.Message.Contains("overflow", StringComparison.OrdinalIgnoreCase));

            // Assert: no Error-level log entry for the overflow (it must NOT be logged at Error)
            var errorEntries = testLogger.LogEntries
                .Where(e => e.LogLevel == LogLevel.Error)
                .ToList();
            // None of the Error entries should be about context overflow
            Assert.DoesNotContain(errorEntries,
                e => e.Message.Contains("model_max_prompt_tokens_exceeded", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── create_release ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRelease_ValidInput_CreatesRelease()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateReleaseAsync("v1.0.0", "v1.0.0", "Initial release");

        Assert.Contains("✅", result);
        Assert.Contains("v1.0.0", result);
        Assert.Contains("Planning", result);

        var release = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.NotNull(release);
        Assert.Equal("v1.0.0", release!.Id);
        Assert.Equal("v1.0.0", release.Tag);
        Assert.Equal("Initial release", release.Notes);
        Assert.Equal(ReleaseStatus.Planning, release.Status);
    }

    [Fact]
    public async Task CreateRelease_WithRepositories_StoresRepoList()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateReleaseAsync("v1.0.0", "v1.0.0", repositories: "CopilotHive, CopilotHive-Config");

        var release = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.NotNull(release);
        Assert.Equal(2, release!.RepositoryNames.Count);
        Assert.Contains("CopilotHive", release.RepositoryNames);
        Assert.Contains("CopilotHive-Config", release.RepositoryNames);
    }

    [Fact]
    public async Task CreateRelease_DuplicateId_ReturnsError()
    {
        await _composer.CreateReleaseAsync("v1.0.0", "v1.0.0");
        var result = await _composer.CreateReleaseAsync("v1.0.0", "v1.0.1");

        Assert.Contains("❌", result);
        Assert.Contains("already exists", result);
    }

    [Fact]
    public async Task CreateRelease_MissingId_ReturnsError()
    {
        var result = await _composer.CreateReleaseAsync("", "v1.0.0");

        Assert.Contains("ERROR", result);
        Assert.Contains("id is required", result);
    }

    [Fact]
    public async Task CreateRelease_MissingTag_ReturnsError()
    {
        var result = await _composer.CreateReleaseAsync("v1.0.0", "");

        Assert.Contains("ERROR", result);
        Assert.Contains("tag is required", result);
    }

    // ── list_releases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListReleases_NoReleases_ReturnsEmptyMessage()
    {
        var result = await _composer.ListReleasesAsync();

        Assert.Contains("No releases found", result);
    }

    [Fact]
    public async Task ListReleases_WithReleases_ListsAll()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create releases
        await _composer.CreateReleaseAsync("v1.0.0", "v1.0.0");
        await _composer.CreateReleaseAsync("v2.0.0", "v2.0.0");

        // Create goals assigned to releases
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

        var result = await _composer.ListReleasesAsync();

        Assert.Contains("2 release(s)", result);
        Assert.Contains("v1.0.0", result);
        Assert.Contains("v2.0.0", result);
        Assert.Contains("[Planning]", result);
        Assert.Contains("2 goal(s)", result);
    }
}

/// <summary>
/// Minimal <see cref="IGoalSource"/> and <see cref="IGoalStore"/> used for cancel tests that need GoalDispatcher.
/// Optionally delegates status updates to a real store for integration testing.
/// </summary>
internal sealed class FakeGoalSource : IGoalSource, IGoalStore
{
    private readonly Goal _goal;
    private readonly IGoalStore? _realStore;

    public FakeGoalSource(Goal goal, IGoalStore? realStore = null)
    {
        _goal = goal;
        _realStore = realStore;
    }

    public string Name => "fake-source";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        _goal.Status == GoalStatus.Pending
            ? Task.FromResult<IReadOnlyList<Goal>>([_goal])
            : Task.FromResult<IReadOnlyList<Goal>>([]);

    public async Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        if (goalId == _goal.Id)
        {
            _goal.Status = status;
            if (metadata?.FailureReason is not null)
                _goal.FailureReason = metadata.FailureReason;
        }

        // Also delegate to real store if provided (for integration testing)
        if (_realStore is not null)
        {
            var storeGoal = await _realStore.GetGoalAsync(goalId, ct);
            if (storeGoal is not null)
            {
                storeGoal.Status = status;
                if (metadata?.FailureReason is not null)
                    storeGoal.FailureReason = metadata.FailureReason;
                await _realStore.UpdateGoalAsync(storeGoal, ct);
            }
        }
    }

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<Goal?>(_goal.Id == goalId ? _goal : null);

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goal.Status == status ? [_goal] : []);

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? status = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) =>
        Task.FromResult(goal);

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>([]);

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>([]);

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>([]);
}

/// <summary>
/// Simple <see cref="HttpMessageHandler"/> that returns a fixed response for unit tests.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string? _body;
    private readonly Action<HttpRequestMessage>? _onRequest;
    private readonly Func<HttpRequestMessage, Task>? _onRequestAsync;
    private readonly Func<HttpRequestMessage, Exception>? _throwException;

    public FakeHttpMessageHandler(
        HttpStatusCode statusCode,
        string body,
        Action<HttpRequestMessage>? onRequest = null,
        Func<HttpRequestMessage, Task>? onRequestAsync = null)
    {
        _statusCode = statusCode;
        _body = body;
        _onRequest = onRequest;
        _onRequestAsync = onRequestAsync;
    }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, Exception> throwException)
    {
        _throwException = throwException;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_throwException is not null)
            throw _throwException(request);

        _onRequest?.Invoke(request);
        if (_onRequestAsync is not null)
            await _onRequestAsync(request);

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body ?? "", System.Text.Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>
/// Test logger that captures log entries for verification.
/// </summary>
internal sealed class TestLogger<T> : ILogger<T>
{
    public List<(LogLevel LogLevel, string Message, Exception? Exception)> LogEntries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        LogEntries.Add((logLevel, formatter(state, exception), exception));
    }
}
