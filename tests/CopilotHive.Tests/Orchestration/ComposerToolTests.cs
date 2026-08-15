using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Text.Json;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Tests for the Composer's goal management tools.
/// Uses an in-memory SQLite goal store to verify tool behaviour.
/// </summary>
public sealed class ComposerToolTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly Composer _composer;

    public ComposerToolTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);

        _composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
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
    public async Task UpdateGoal_Description_DraftGoal_UpdatesDescription()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("update-me2", "Test goal");
        var result = await _composer.UpdateGoalAsync("update-me2", "description", "New desc");

        Assert.Contains("✅", result);
        var goal = await _store.GetGoalAsync("update-me2", ct);
        Assert.Equal("New desc", goal!.Description);
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
    public async Task UpdateGoal_Status_ValidTransition_FailedToDraft_Succeeds()
    {
        // Failed→Draft is now a valid "retry" transition
        await _composer.CreateGoalAsync("transition-failed1", "Test goal");
        var ct = TestContext.Current.CancellationToken;
        var goal = await _store.GetGoalAsync("transition-failed1", ct);
        Assert.NotNull(goal);
        goal!.Status = GoalStatus.Failed;
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync("transition-failed1", "status", "Draft");

        Assert.Contains("✅", result);
        Assert.Contains("Draft", result);
    }

    [Fact]
    public async Task UpdateGoal_Status_FailedToDraft_ResetsIterationData()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a goal with repositories and set it to Failed with iteration data
        await _composer.CreateGoalAsync("retry-reset-iter", "Test goal", repositories: "repo-a, repo-b");
        var goal = await _store.GetGoalAsync("retry-reset-iter", ct);
        Assert.NotNull(goal);
        goal!.Status = GoalStatus.Failed;
        goal.FailureReason = "Worker timed out";
        goal.Iterations = 3;
        goal.TotalDurationSeconds = 180.5;
        goal.StartedAt = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        goal.CompletedAt = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        await _store.UpdateGoalAsync(goal, ct);

        // Transition Failed → Draft
        var result = await _composer.UpdateGoalAsync("retry-reset-iter", "status", "Draft");

        Assert.Contains("✅", result);
        Assert.Contains("Draft", result);

        // Verify iteration data was cleared
        var reset = await _store.GetGoalAsync("retry-reset-iter", ct);
        Assert.NotNull(reset);
        Assert.Equal(GoalStatus.Draft, reset!.Status);
        Assert.Null(reset.FailureReason);
        Assert.Equal(0, reset.Iterations);
        Assert.Null(reset.TotalDurationSeconds);
        Assert.Null(reset.StartedAt);
        Assert.Null(reset.CompletedAt);
    }

    [Fact]
    public async Task UpdateGoal_Status_FailedToDraft_DeletesRemoteBranchesForAllRepositories()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a goal with repositories
        await _composer.CreateGoalAsync("retry-branch-cleanup", "Test goal", repositories: "repo-x, repo-y");
        var goal = await _store.GetGoalAsync("retry-branch-cleanup", ct);
        Assert.NotNull(goal);
        goal!.Status = GoalStatus.Failed;
        await _store.UpdateGoalAsync(goal, ct);

        // Mock the repo manager
        var mockRepoManager = new Mock<IBrainRepoManager>();
        mockRepoManager.Setup(r => r.DeleteRemoteBranchAsync("repo-x", "copilothive/retry-branch-cleanup", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchDeleteResult.Success)
            .Verifiable();
        mockRepoManager.Setup(r => r.DeleteRemoteBranchAsync("repo-y", "copilothive/retry-branch-cleanup", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchDeleteResult.Success)
            .Verifiable();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: mockRepoManager.Object,
            stateDir: Path.GetTempPath());

        var result = await composer.UpdateGoalAsync("retry-branch-cleanup", "status", "Draft");

        Assert.Contains("✅", result);
        Assert.Contains("Draft", result);

        // Verify DeleteRemoteBranchAsync was called for each repository
        mockRepoManager.Verify(r => r.DeleteRemoteBranchAsync("repo-x", "copilothive/retry-branch-cleanup", It.IsAny<CancellationToken>()), Times.Once);
        mockRepoManager.Verify(r => r.DeleteRemoteBranchAsync("repo-y", "copilothive/retry-branch-cleanup", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── update_goal — release field ──

    [Fact]
    public async Task UpdateGoal_Release_ValidId_SetsReleaseId()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("release-goal-set", "Goal to assign to a release");
        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0" }, ct);

        var result = await _composer.UpdateGoalAsync("release-goal-set", "release", "v1.0.0");

        Assert.Contains("✅", result);
        Assert.Contains("v1.0.0", result);

        var goal = await _store.GetGoalAsync("release-goal-set", ct);
        Assert.NotNull(goal);
        Assert.Equal("v1.0.0", goal!.ReleaseId);
    }

    [Fact]
    public async Task UpdateGoal_Release_None_ClearsReleaseId()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0" }, ct);
        await _composer.CreateGoalAsync("release-goal-clear-none", "Goal to clear release via none");
        var goal = await _store.GetGoalAsync("release-goal-clear-none", ct);
        Assert.NotNull(goal);
        goal!.ReleaseId = "v1.0.0";
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync("release-goal-clear-none", "release", "none");

        Assert.Contains("✅", result);
        Assert.Contains("cleared", result);

        var updated = await _store.GetGoalAsync("release-goal-clear-none", ct);
        Assert.Null(updated!.ReleaseId);
    }

    [Fact]
    public async Task UpdateGoal_Release_EmptyString_ClearsReleaseId()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0" }, ct);
        await _composer.CreateGoalAsync("release-goal-clear-empty", "Goal to clear release via empty string");
        var goal = await _store.GetGoalAsync("release-goal-clear-empty", ct);
        Assert.NotNull(goal);
        goal!.ReleaseId = "v1.0.0";
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync("release-goal-clear-empty", "release", "");

        Assert.Contains("✅", result);
        Assert.Contains("cleared", result);

        var updated = await _store.GetGoalAsync("release-goal-clear-empty", ct);
        Assert.Null(updated!.ReleaseId);
    }

    [Fact]
    public async Task UpdateGoal_Release_InvalidId_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("release-goal-invalid", "Goal with invalid release");

        var result = await _composer.UpdateGoalAsync("release-goal-invalid", "release", "nonexistent-release");

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);

        var goal = await _store.GetGoalAsync("release-goal-invalid", ct);
        Assert.NotNull(goal);
        Assert.Null(goal!.ReleaseId);
    }

    // ── update_goal — Draft-only editable fields ──

    [Fact]
    public async Task UpdateGoal_Priority_DraftGoal_UpdatesPriority()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("priority-draft", "Test goal");
        var result = await _composer.UpdateGoalAsync("priority-draft", "priority", "High");

        Assert.Contains("✅", result);
        var goal = await _store.GetGoalAsync("priority-draft", ct);
        Assert.Equal(GoalPriority.High, goal!.Priority);
    }

    [Fact]
    public async Task UpdateGoal_Priority_InvalidValue_ReturnsError()
    {
        await _composer.CreateGoalAsync("priority-invalid", "Test goal");
        var result = await _composer.UpdateGoalAsync("priority-invalid", "priority", "SuperHigh");

        Assert.Contains("❌", result);
        Assert.Contains("Invalid priority", result);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("999")]
    public async Task UpdateGoal_Priority_NumericString_ReturnsError(string numericValue)
    {
        await _composer.CreateGoalAsync($"priority-numeric-{numericValue}", "Test goal");
        var result = await _composer.UpdateGoalAsync($"priority-numeric-{numericValue}", "priority", numericValue);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid priority", result);
    }

    [Fact]
    public async Task UpdateGoal_Repositories_DraftGoal_UpdatesRepositories()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("repos-draft", "Test goal");
        var result = await _composer.UpdateGoalAsync("repos-draft", "repositories", "repo-a, repo-b");

        Assert.Contains("✅", result);
        var goal = await _store.GetGoalAsync("repos-draft", ct);
        Assert.Equal(2, goal!.RepositoryNames.Count);
        Assert.Contains("repo-a", goal.RepositoryNames);
        Assert.Contains("repo-b", goal.RepositoryNames);
    }

    [Fact]
    public async Task UpdateGoal_Scope_DraftGoal_UpdatesScope()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("scope-draft", "Test goal");
        var result = await _composer.UpdateGoalAsync("scope-draft", "scope", "Feature");

        Assert.Contains("✅", result);
        var goal = await _store.GetGoalAsync("scope-draft", ct);
        Assert.Equal(GoalScope.Feature, goal!.Scope);
    }

    [Fact]
    public async Task UpdateGoal_Scope_InvalidValue_ReturnsError()
    {
        await _composer.CreateGoalAsync("scope-invalid", "Test goal");
        var result = await _composer.UpdateGoalAsync("scope-invalid", "scope", "Enormous");

        Assert.Contains("❌", result);
        Assert.Contains("Invalid scope", result);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("999")]
    public async Task UpdateGoal_Scope_NumericString_ReturnsError(string numericValue)
    {
        await _composer.CreateGoalAsync($"scope-numeric-{numericValue}", "Test goal");
        var result = await _composer.UpdateGoalAsync($"scope-numeric-{numericValue}", "scope", numericValue);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid scope", result);
    }

    [Fact]
    public async Task UpdateGoal_DependsOn_DraftGoal_UpdatesDependsOn()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("depends-draft", "Test goal");
        var result = await _composer.UpdateGoalAsync("depends-draft", "depends_on", "goal-a, goal-b");

        Assert.Contains("✅", result);
        var goal = await _store.GetGoalAsync("depends-draft", ct);
        Assert.Equal(2, goal!.DependsOn.Count);
        Assert.Contains("goal-a", goal.DependsOn);
        Assert.Contains("goal-b", goal.DependsOn);
    }

    [Fact]
    public async Task UpdateGoal_DependsOn_EmptyValue_ClearsDependsOn()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("depends-clear", "Test goal", depends_on: "goal-x");
        var result = await _composer.UpdateGoalAsync("depends-clear", "depends_on", "");

        Assert.Contains("✅", result);
        var goal = await _store.GetGoalAsync("depends-clear", ct);
        Assert.Empty(goal!.DependsOn);
    }

    [Fact]
    public async Task UpdateGoal_Documents_DraftGoal_UpdatesDocuments()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("docs-draft", "Test goal");
        var result = await _composer.UpdateGoalAsync("docs-draft", "documents", "doc-1, doc-2");

        Assert.Contains("✅", result);
        var goal = await _store.GetGoalAsync("docs-draft", ct);
        Assert.Equal(2, goal!.Documents.Count);
        Assert.Contains("doc-1", goal.Documents);
        Assert.Contains("doc-2", goal.Documents);
    }

    [Fact]
    public async Task UpdateGoal_Documents_EmptyValue_ClearsDocuments()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("docs-clear", "Test goal", documents: "doc-x");
        var result = await _composer.UpdateGoalAsync("docs-clear", "documents", "");

        Assert.Contains("✅", result);
        var goal = await _store.GetGoalAsync("docs-clear", ct);
        Assert.Empty(goal!.Documents);
    }

    // ── update_goal — non-Draft rejection for all 6 editable fields ──

    [Theory]
    [InlineData("Pending")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task UpdateGoal_Description_NonDraft_ReturnsError(string statusName)
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = $"desc-nondraft-{statusName.ToLower()}";

        await _composer.CreateGoalAsync(goalId, "Original desc");
        var goal = await _store.GetGoalAsync(goalId, ct);
        goal!.Status = Enum.Parse<GoalStatus>(statusName);
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync(goalId, "description", "New desc");

        Assert.Contains("❌", result);
        Assert.Contains("Cannot edit description", result);
        Assert.Contains(statusName, result);
        Assert.Contains("Only Draft goals can be edited", result);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task UpdateGoal_Priority_NonDraft_ReturnsError(string statusName)
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = $"prio-nondraft-{statusName.ToLower()}";

        await _composer.CreateGoalAsync(goalId, "Test goal");
        var goal = await _store.GetGoalAsync(goalId, ct);
        goal!.Status = Enum.Parse<GoalStatus>(statusName);
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync(goalId, "priority", "High");

        Assert.Contains("❌", result);
        Assert.Contains("Cannot edit priority", result);
        Assert.Contains(statusName, result);
        Assert.Contains("Only Draft goals can be edited", result);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task UpdateGoal_Scope_NonDraft_ReturnsError(string statusName)
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = $"scope-nondraft-{statusName.ToLower()}";

        await _composer.CreateGoalAsync(goalId, "Test goal");
        var goal = await _store.GetGoalAsync(goalId, ct);
        goal!.Status = Enum.Parse<GoalStatus>(statusName);
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync(goalId, "scope", "Feature");

        Assert.Contains("❌", result);
        Assert.Contains("Cannot edit scope", result);
        Assert.Contains(statusName, result);
        Assert.Contains("Only Draft goals can be edited", result);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task UpdateGoal_Repositories_NonDraft_ReturnsError(string statusName)
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = $"repos-nondraft-{statusName.ToLower()}";

        await _composer.CreateGoalAsync(goalId, "Test goal");
        var goal = await _store.GetGoalAsync(goalId, ct);
        goal!.Status = Enum.Parse<GoalStatus>(statusName);
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync(goalId, "repositories", "repo-a");

        Assert.Contains("❌", result);
        Assert.Contains("Cannot edit repositories", result);
        Assert.Contains(statusName, result);
        Assert.Contains("Only Draft goals can be edited", result);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task UpdateGoal_DependsOn_NonDraft_ReturnsError(string statusName)
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = $"deps-nondraft-{statusName.ToLower()}";

        await _composer.CreateGoalAsync(goalId, "Test goal");
        var goal = await _store.GetGoalAsync(goalId, ct);
        goal!.Status = Enum.Parse<GoalStatus>(statusName);
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync(goalId, "depends_on", "other-goal");

        Assert.Contains("❌", result);
        Assert.Contains("Cannot edit depends_on", result);
        Assert.Contains(statusName, result);
        Assert.Contains("Only Draft goals can be edited", result);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task UpdateGoal_Documents_NonDraft_ReturnsError(string statusName)
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = $"docs-nondraft-{statusName.ToLower()}";

        await _composer.CreateGoalAsync(goalId, "Test goal");
        var goal = await _store.GetGoalAsync(goalId, ct);
        goal!.Status = Enum.Parse<GoalStatus>(statusName);
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync(goalId, "documents", "doc-1");

        Assert.Contains("❌", result);
        Assert.Contains("Cannot edit documents", result);
        Assert.Contains(statusName, result);
        Assert.Contains("Only Draft goals can be edited", result);
    }

    [Fact]
    public async Task UpdateGoal_Metadata_NotValidField_ReturnsUnknownFieldError()
    {
        await _composer.CreateGoalAsync("metadata-goal", "Test goal");
        var result = await _composer.UpdateGoalAsync("metadata-goal", "metadata", "key=value");

        Assert.Contains("❌", result);
        Assert.Contains("Unknown field", result);
    }

    [Fact]
    public async Task UpdateGoal_Id_NotValidField_ReturnsUnknownFieldError()
    {
        await _composer.CreateGoalAsync("id-goal", "Test goal");
        var result = await _composer.UpdateGoalAsync("id-goal", "id", "new-id");

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

    [Fact]
    public async Task GetGoal_GoalWithReviewStatus_IncludesReviewStatusInOutput()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("review-goal", "A goal with review status");
        // Set the review status via the store directly
        var goal = await _store.GetGoalAsync("review-goal", ct);
        Assert.NotNull(goal);
        goal!.ReviewStatus = ReviewStatus.Approved;
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.GetGoalAsync("review-goal");

        Assert.Contains("Review Status", result);
        Assert.Contains("Approved", result);
    }

    // ── list_goals ──

    [Fact]
    public async Task ListGoals_Default_ShowsUnreleasedFilter()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "rel-released", Tag = "v1.0", Status = ReleaseStatus.Released }, ct);

        await _composer.CreateGoalAsync("goal-a", "First goal");
        await _composer.CreateGoalAsync("goal-b", "Second goal");

        await _composer.CreateGoalAsync("released-goal", "In a released release");
        var releasedGoal = await _store.GetGoalAsync("released-goal", ct);
        Assert.NotNull(releasedGoal);
        releasedGoal!.ReleaseId = "rel-released";
        await _store.UpdateGoalAsync(releasedGoal, ct);

        var result = await _composer.ListGoalsAsync();

        Assert.Contains("2 goal(s) (release filter: unreleased)", result);
        Assert.Contains("goal-a", result);
        Assert.Contains("goal-b", result);
        Assert.DoesNotContain("released-goal", result);
    }

    [Fact]
    public async Task ListGoals_FilterByStatus_FiltersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("draft-1", "Draft goal");
        var pendingGoal = new Goal { Id = "pending-1", Description = "Pending goal", Status = GoalStatus.Pending };
        await _store.CreateGoalAsync(pendingGoal, ct);

        var result = await _composer.ListGoalsAsync("Draft");

        Assert.Contains("1 goal(s) (release filter: unreleased)", result);
        Assert.Contains("draft-1", result);
        Assert.DoesNotContain("pending-1", result);
    }

    [Fact]
    public async Task ListGoals_Empty_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.ListGoalsAsync();

        Assert.Contains("No goals (release filter: unreleased)", result);
    }

    [Fact]
    public async Task ListGoals_StripsMarkdownHeadingFromDescription()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "rel-released", Tag = "v1.0", Status = ReleaseStatus.Released }, ct);

        var goal = new Goal { Id = "heading-goal", Description = "## My Goal Title\nSome details here", Status = GoalStatus.Draft };
        await _store.CreateGoalAsync(goal, ct);

        await _composer.CreateGoalAsync("released-goal", "In a released release");
        var releasedGoal = await _store.GetGoalAsync("released-goal", ct);
        Assert.NotNull(releasedGoal);
        releasedGoal!.ReleaseId = "rel-released";
        await _store.UpdateGoalAsync(releasedGoal, ct);

        var result = await _composer.ListGoalsAsync();

        Assert.Contains("heading-goal", result);
        Assert.Contains("My Goal Title", result);
        Assert.DoesNotContain("##", result);
        Assert.DoesNotContain("released-goal", result);
    }

    [Fact]
    public async Task ListGoals_ReplacesNewlinesWithSpacesInDescription()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "rel-released", Tag = "v1.0", Status = ReleaseStatus.Released }, ct);

        var goal = new Goal { Id = "newline-goal", Description = "First line\nSecond line", Status = GoalStatus.Draft };
        await _store.CreateGoalAsync(goal, ct);

        await _composer.CreateGoalAsync("released-goal", "In a released release");
        var releasedGoal = await _store.GetGoalAsync("released-goal", ct);
        Assert.NotNull(releasedGoal);
        releasedGoal!.ReleaseId = "rel-released";
        await _store.UpdateGoalAsync(releasedGoal, ct);

        var result = await _composer.ListGoalsAsync();

        Assert.Contains("newline-goal", result);
        Assert.Contains("First line Second line", result);
        Assert.DoesNotContain("released-goal", result);
    }

    [Fact]
    public async Task ListGoals_Unreleased_IncludesNullReleaseId()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("unassigned-goal", "Not assigned to a release");

        var result = await _composer.ListGoalsAsync(release: "unreleased");

        Assert.Contains("1 goal(s) (release filter: unreleased)", result);
        Assert.Contains("unassigned-goal", result);
    }

    [Fact]
    public async Task ListGoals_Unreleased_ExcludesReleasedReleaseGoals()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Released }, ct);
        await _composer.CreateGoalAsync("released-goal", "In a released release");
        var goal = await _store.GetGoalAsync("released-goal", ct);
        Assert.NotNull(goal);
        goal!.ReleaseId = "v1.0.0";
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.ListGoalsAsync(release: "unreleased");

        Assert.Contains("No goals (release filter: unreleased)", result);
        Assert.DoesNotContain("released-goal", result);
    }

    [Fact]
    public async Task ListGoals_Unreleased_IncludesPlanningReleaseGoals()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Planning }, ct);
        await _composer.CreateGoalAsync("planning-goal", "In a planning release");
        var goal = await _store.GetGoalAsync("planning-goal", ct);
        Assert.NotNull(goal);
        goal!.ReleaseId = "v1.0.0";
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.ListGoalsAsync(release: "unreleased");

        Assert.Contains("1 goal(s) (release filter: unreleased)", result);
        Assert.Contains("planning-goal", result);
    }

    [Fact]
    public async Task ListGoals_Unreleased_ExcludesUnknownReleaseId()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("unknown-release-goal", "Unknown release");
        var goal = await _store.GetGoalAsync("unknown-release-goal", ct);
        Assert.NotNull(goal);
        goal!.ReleaseId = "no-such-release";
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.ListGoalsAsync(release: "unreleased");

        Assert.Contains("No goals (release filter: unreleased)", result);
        Assert.DoesNotContain("unknown-release-goal", result);
    }

    [Theory]
    [InlineData("unreleased")]
    [InlineData("UNRELEASED")]
    [InlineData("Unreleased")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListGoals_CaseVariantsAndBlank_TreatedAsUnreleased(string release)
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "rel-released", Tag = "v1.0", Status = ReleaseStatus.Released }, ct);

        await _composer.CreateGoalAsync("variant-goal", "Variant goal");

        await _composer.CreateGoalAsync("released-goal", "In a released release");
        var releasedGoal = await _store.GetGoalAsync("released-goal", ct);
        Assert.NotNull(releasedGoal);
        releasedGoal!.ReleaseId = "rel-released";
        await _store.UpdateGoalAsync(releasedGoal, ct);

        var result = await _composer.ListGoalsAsync(release: release);

        Assert.Contains("1 goal(s) (release filter: unreleased)", result);
        Assert.Contains("variant-goal", result);
        Assert.DoesNotContain("released-goal", result);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    public async Task ListGoals_All_IncludesEveryGoal(string release)
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Released }, ct);
        await _store.CreateReleaseAsync(new Release { Id = "v2.0.0", Tag = "v2.0.0", Status = ReleaseStatus.Planning }, ct);

        await _composer.CreateGoalAsync("unassigned-goal", "No release");

        await _composer.CreateGoalAsync("released-goal", "Released release");
        var releasedGoal = await _store.GetGoalAsync("released-goal", ct);
        Assert.NotNull(releasedGoal);
        releasedGoal!.ReleaseId = "v1.0.0";
        await _store.UpdateGoalAsync(releasedGoal, ct);

        await _composer.CreateGoalAsync("planning-goal", "Planning release");
        var planningGoal = await _store.GetGoalAsync("planning-goal", ct);
        Assert.NotNull(planningGoal);
        planningGoal!.ReleaseId = "v2.0.0";
        await _store.UpdateGoalAsync(planningGoal, ct);

        await _composer.CreateGoalAsync("unknown-release-goal", "Unknown release");
        var unknownGoal = await _store.GetGoalAsync("unknown-release-goal", ct);
        Assert.NotNull(unknownGoal);
        unknownGoal!.ReleaseId = "no-such-release";
        await _store.UpdateGoalAsync(unknownGoal, ct);

        var result = await _composer.ListGoalsAsync(release: release);

        Assert.Contains("4 goal(s) (release filter: all)", result);
        Assert.Contains("unassigned-goal", result);
        Assert.Contains("released-goal", result);
        Assert.Contains("planning-goal", result);
        Assert.Contains("unknown-release-goal", result);
    }

    [Fact]
    public async Task ListGoals_SpecificReleaseKnown_SameTagSameStatusIncluded()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0-alpha1", Tag = "v1.0.0", Status = ReleaseStatus.Planning }, ct);
        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0-alpha2", Tag = "v1.0.0", Status = ReleaseStatus.Planning }, ct);

        await _composer.CreateGoalAsync("alpha1-goal", "First alpha goal");
        var g1 = await _store.GetGoalAsync("alpha1-goal", ct);
        Assert.NotNull(g1);
        g1!.ReleaseId = "v1.0.0-alpha1";
        await _store.UpdateGoalAsync(g1, ct);

        await _composer.CreateGoalAsync("alpha2-goal", "Second alpha goal");
        var g2 = await _store.GetGoalAsync("alpha2-goal", ct);
        Assert.NotNull(g2);
        g2!.ReleaseId = "v1.0.0-alpha2";
        await _store.UpdateGoalAsync(g2, ct);

        await _composer.CreateGoalAsync("unassigned-goal", "Unassigned");

        var result = await _composer.ListGoalsAsync(release: "v1.0.0-alpha1");

        Assert.Contains("2 goal(s) (release filter: v1.0.0-alpha1)", result);
        Assert.Contains("alpha1-goal", result);
        Assert.Contains("alpha2-goal", result);
        Assert.DoesNotContain("unassigned-goal", result);
    }

    [Fact]
    public async Task ListGoals_SpecificReleaseKnown_SameTagDifferentStatusExcluded()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0-alpha1", Tag = "v1.0.0", Status = ReleaseStatus.Planning }, ct);
        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0-shipped", Tag = "v1.0.0", Status = ReleaseStatus.Released }, ct);

        await _composer.CreateGoalAsync("alpha-goal", "Alpha goal");
        var g1 = await _store.GetGoalAsync("alpha-goal", ct);
        Assert.NotNull(g1);
        g1!.ReleaseId = "v1.0.0-alpha1";
        await _store.UpdateGoalAsync(g1, ct);

        await _composer.CreateGoalAsync("shipped-goal", "Shipped goal");
        var g2 = await _store.GetGoalAsync("shipped-goal", ct);
        Assert.NotNull(g2);
        g2!.ReleaseId = "v1.0.0-shipped";
        await _store.UpdateGoalAsync(g2, ct);

        var result = await _composer.ListGoalsAsync(release: "v1.0.0-alpha1");

        Assert.Contains("1 goal(s) (release filter: v1.0.0-alpha1)", result);
        Assert.Contains("alpha-goal", result);
        Assert.DoesNotContain("shipped-goal", result);
    }

    [Fact]
    public async Task ListGoals_SpecificReleaseUnknown_FallsBackToExactId()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("unknown-rel-goal", "Goal with unknown release");
        var goal = await _store.GetGoalAsync("unknown-rel-goal", ct);
        Assert.NotNull(goal);
        goal!.ReleaseId = "unknown-rel";
        await _store.UpdateGoalAsync(goal, ct);

        await _composer.CreateGoalAsync("other-goal", "Other goal");

        var result = await _composer.ListGoalsAsync(release: "unknown-rel");

        Assert.Contains("1 goal(s) (release filter: unknown-rel)", result);
        Assert.Contains("unknown-rel-goal", result);
        Assert.DoesNotContain("other-goal", result);
    }

    [Fact]
    public async Task ListGoals_StatusAndReleaseCombination_NarrowsCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0", Tag = "v1.0.0", Status = ReleaseStatus.Planning }, ct);

        await _composer.CreateGoalAsync("draft-in-release", "Draft in release");
        var draftInRelease = await _store.GetGoalAsync("draft-in-release", ct);
        Assert.NotNull(draftInRelease);
        draftInRelease!.ReleaseId = "v1.0.0";
        await _store.UpdateGoalAsync(draftInRelease, ct);

        await _composer.CreateGoalAsync("pending-in-release", "Pending in release");
        var pendingInRelease = await _store.GetGoalAsync("pending-in-release", ct);
        Assert.NotNull(pendingInRelease);
        pendingInRelease!.Status = GoalStatus.Pending;
        pendingInRelease!.ReleaseId = "v1.0.0";
        await _store.UpdateGoalAsync(pendingInRelease, ct);

        await _composer.CreateGoalAsync("draft-unassigned", "Draft unassigned");

        var result = await _composer.ListGoalsAsync(status: "Draft", release: "v1.0.0");

        Assert.Contains("1 goal(s) (release filter: v1.0.0)", result);
        Assert.Contains("draft-in-release", result);
        Assert.DoesNotContain("pending-in-release", result);
        Assert.DoesNotContain("draft-unassigned", result);
    }

    [Fact]
    public async Task ListGoals_SpecificReleaseEmpty_NamesEffectiveFilter()
    {
        var result = await _composer.ListGoalsAsync(release: "v1.0.0");

        Assert.Contains("No goals (release filter: v1.0.0)", result);
    }

    [Fact]
    public void ListGoals_ToolRegisteredWithReleaseParameter()
    {
        var tools = _composer.BuildComposerTools();
        var listGoals = tools.OfType<AIFunction>().Single(t => t.Name == "list_goals");

        var parameters = listGoals.UnderlyingMethod!.GetParameters();
        var releaseParam = parameters.Single(p => p.Name == "release");

        var descriptionAttr = releaseParam.GetCustomAttributesData()
            .First(a => a.AttributeType.FullName == "System.ComponentModel.DescriptionAttribute");
        var description = descriptionAttr.ConstructorArguments[0].Value as string;

        Assert.NotNull(description);
        Assert.Contains("release", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListGoals_RegistrationDescription_DocumentsReleaseFilter()
    {
        var tools = _composer.BuildComposerTools();
        var listGoals = tools.OfType<AIFunction>().Single(t => t.Name == "list_goals");

        Assert.NotNull(listGoals.Description);
        Assert.Contains("unreleased", listGoals.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("all", listGoals.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release", listGoals.Description, StringComparison.OrdinalIgnoreCase);
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

    // ── search_goals — release filter (opt-in) ──
    //
    // The release parameter on search_goals is OPT-IN: when omitted (or "all"),
    // no release filtering is applied and the header/empty-message remain identical
    // to the pre-feature output. A genuinely-active filter ("unreleased", specific id,
    // or blank→unreleased) is labeled in both the header and empty message.

    [Fact]
    public async Task SearchGoals_ReleaseOmitted_NoFilterLabel()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed a Released release so a goal in it WOULD be filtered out by "unreleased".
        await _store.CreateReleaseAsync(new Release { Id = "rel-released", Tag = "v1.0", Status = ReleaseStatus.Released }, ct);

        await _composer.CreateGoalAsync("search-free", "Shared keyword for matching");
        await _composer.CreateGoalAsync("search-released", "Shared keyword for matching in released");
        var releasedGoal = await _store.GetGoalAsync("search-released", ct);
        Assert.NotNull(releasedGoal);
        releasedGoal!.ReleaseId = "rel-released";
        await _store.UpdateGoalAsync(releasedGoal, ct);

        // Omitted release → no filtering, no label.
        var result = await _composer.SearchGoalsAsync("keyword");

        Assert.Contains("**2 result(s) for 'keyword':**", result);
        Assert.DoesNotContain("release filter", result);
        Assert.Contains("search-free", result);
        Assert.Contains("search-released", result); // proves no filtering — would be excluded by unreleased

        // Empty-message variant: non-matching query, omitted release → exact message, no filter label.
        var emptyResult = await _composer.SearchGoalsAsync("zzz-nonexistent");
        Assert.Contains("No goals matching 'zzz-nonexistent'.", emptyResult);
        Assert.DoesNotContain("release filter", emptyResult);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("All")]
    public async Task SearchGoals_ReleaseAll_NoFilterLabel(string release)
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "rel-released", Tag = "v1.0", Status = ReleaseStatus.Released }, ct);

        await _composer.CreateGoalAsync("search-free", "Shared keyword for matching");
        await _composer.CreateGoalAsync("search-released", "Shared keyword for matching in released");
        var releasedGoal = await _store.GetGoalAsync("search-released", ct);
        Assert.NotNull(releasedGoal);
        releasedGoal!.ReleaseId = "rel-released";
        await _store.UpdateGoalAsync(releasedGoal, ct);

        // "all" must be byte-for-byte identical to omitted — no label, same goals, same empty message.
        var omittedResult = await _composer.SearchGoalsAsync("keyword");
        var allResult = await _composer.SearchGoalsAsync("keyword", release: release);

        Assert.Equal(omittedResult, allResult);
        Assert.Contains("**2 result(s) for 'keyword':**", allResult);
        Assert.DoesNotContain("release filter", allResult);
        Assert.Contains("search-free", allResult);
        Assert.Contains("search-released", allResult);

        var omittedEmptyResult = await _composer.SearchGoalsAsync("zzz-nonexistent");
        var allEmptyResult = await _composer.SearchGoalsAsync("zzz-nonexistent", release: release);

        Assert.Equal(omittedEmptyResult, allEmptyResult);
        Assert.Contains("No goals matching 'zzz-nonexistent'.", allEmptyResult);
        Assert.DoesNotContain("release filter", allEmptyResult);
    }

    [Fact]
    public async Task SearchGoals_ReleaseUnreleased_FiltersAndLabels()
    {
        var ct = TestContext.Current.CancellationToken;

        // Null ReleaseId → included; Released release → excluded; Planning release → included.
        await _store.CreateReleaseAsync(new Release { Id = "rel-released", Tag = "v1.0", Status = ReleaseStatus.Released }, ct);
        await _store.CreateReleaseAsync(new Release { Id = "rel-planning", Tag = "v2.0", Status = ReleaseStatus.Planning }, ct);

        await _composer.CreateGoalAsync("null-release-goal", "Shared keyword alpha");
        await _composer.CreateGoalAsync("released-goal", "Shared keyword alpha released");
        var releasedGoal = await _store.GetGoalAsync("released-goal", ct);
        Assert.NotNull(releasedGoal);
        releasedGoal!.ReleaseId = "rel-released";
        await _store.UpdateGoalAsync(releasedGoal, ct);

        await _composer.CreateGoalAsync("planning-goal", "Shared keyword alpha planning");
        var planningGoal = await _store.GetGoalAsync("planning-goal", ct);
        Assert.NotNull(planningGoal);
        planningGoal!.ReleaseId = "rel-planning";
        await _store.UpdateGoalAsync(planningGoal, ct);

        var result = await _composer.SearchGoalsAsync("alpha", release: "unreleased");

        Assert.Contains("(release filter: unreleased)", result);
        Assert.Contains("null-release-goal", result);
        Assert.Contains("planning-goal", result);
        Assert.DoesNotContain("released-goal", result);
    }

    [Fact]
    public async Task SearchGoals_ReleaseSpecificId_TagAndStatusMatch()
    {
        var ct = TestContext.Current.CancellationToken;

        // Two releases with the SAME tag but DIFFERENT statuses.
        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0-alpha1", Tag = "v1.0.0", Status = ReleaseStatus.Planning }, ct);
        await _store.CreateReleaseAsync(new Release { Id = "v1.0.0-shipped", Tag = "v1.0.0", Status = ReleaseStatus.Released }, ct);

        await _composer.CreateGoalAsync("alpha-goal", "Shared keyword beta");
        var alphaGoal = await _store.GetGoalAsync("alpha-goal", ct);
        Assert.NotNull(alphaGoal);
        alphaGoal!.ReleaseId = "v1.0.0-alpha1";
        await _store.UpdateGoalAsync(alphaGoal, ct);

        await _composer.CreateGoalAsync("shipped-goal", "Shared keyword beta shipped");
        var shippedGoal = await _store.GetGoalAsync("shipped-goal", ct);
        Assert.NotNull(shippedGoal);
        shippedGoal!.ReleaseId = "v1.0.0-shipped";
        await _store.UpdateGoalAsync(shippedGoal, ct);

        // Filter by the Planning release id — same-tag same-status included, same-tag different-status excluded.
        var result = await _composer.SearchGoalsAsync("beta", release: "v1.0.0-alpha1");

        Assert.Contains("(release filter: v1.0.0-alpha1)", result);
        Assert.Contains("alpha-goal", result);
        Assert.DoesNotContain("shipped-goal", result);
    }

    [Fact]
    public async Task SearchGoals_ReleaseUnknownId_ExactIdFallback()
    {
        var ct = TestContext.Current.CancellationToken;

        // Goal with ReleaseId pointing to an ID that does NOT match any created release.
        await _composer.CreateGoalAsync("unknown-rel-goal", "Shared keyword gamma");
        var goal = await _store.GetGoalAsync("unknown-rel-goal", ct);
        Assert.NotNull(goal);
        goal!.ReleaseId = "no-such-release";
        await _store.UpdateGoalAsync(goal, ct);

        await _composer.CreateGoalAsync("other-goal", "Shared keyword gamma other");

        var result = await _composer.SearchGoalsAsync("gamma", release: "no-such-release");

        Assert.Contains("(release filter: no-such-release)", result);
        Assert.Contains("unknown-rel-goal", result);  // exact-id fallback finds it
        Assert.DoesNotContain("other-goal", result);
    }

    [Fact]
    public async Task SearchGoals_ReleaseFilter_EmptyMessage_NamesFilter()
    {
        var result = await _composer.SearchGoalsAsync("nonexistent", release: "unreleased");

        Assert.Contains("No goals matching", result);
        Assert.Contains("(release filter: unreleased)", result);
    }

    [Fact]
    public async Task SearchGoals_ReleaseBlank_TreatedAsUnreleased()
    {
        var ct = TestContext.Current.CancellationToken;

        // Null ReleaseId → included; Released release → excluded; Planning release → included.
        await _store.CreateReleaseAsync(new Release { Id = "rel-released", Tag = "v1.0", Status = ReleaseStatus.Released }, ct);
        await _store.CreateReleaseAsync(new Release { Id = "rel-planning", Tag = "v2.0", Status = ReleaseStatus.Planning }, ct);

        await _composer.CreateGoalAsync("null-release-goal", "Shared keyword delta");
        await _composer.CreateGoalAsync("released-goal", "Shared keyword delta released");
        var releasedGoal = await _store.GetGoalAsync("released-goal", ct);
        Assert.NotNull(releasedGoal);
        releasedGoal!.ReleaseId = "rel-released";
        await _store.UpdateGoalAsync(releasedGoal, ct);

        await _composer.CreateGoalAsync("planning-goal", "Shared keyword delta planning");
        var planningGoal = await _store.GetGoalAsync("planning-goal", ct);
        Assert.NotNull(planningGoal);
        planningGoal!.ReleaseId = "rel-planning";
        await _store.UpdateGoalAsync(planningGoal, ct);

        // Whitespace-only release is non-null, so it normalizes to "unreleased" — a real labeled filter.
        var result = await _composer.SearchGoalsAsync("delta", release: "   ");

        Assert.Contains("(release filter: unreleased)", result);
        Assert.Contains("null-release-goal", result);
        Assert.Contains("planning-goal", result);
        Assert.DoesNotContain("released-goal", result);
    }

    [Fact]
    public async Task SearchGoals_StatusUnfiltered_NoReleaseLabelInHeader()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "rel-released", Tag = "v1.0", Status = ReleaseStatus.Released }, ct);

        await _composer.CreateGoalAsync("draft-free", "Shared keyword phi");
        await _composer.CreateGoalAsync("draft-released", "Shared keyword phi released");
        var releasedGoal = await _store.GetGoalAsync("draft-released", ct);
        Assert.NotNull(releasedGoal);
        releasedGoal!.ReleaseId = "rel-released";
        await _store.UpdateGoalAsync(releasedGoal, ct);

        // Status filter without release must not add a release label and must be identical to status+all.
        var statusResult = await _composer.SearchGoalsAsync("phi", status: "Draft");
        var statusAllResult = await _composer.SearchGoalsAsync("phi", status: "Draft", release: "all");

        Assert.Equal(statusResult, statusAllResult);
        Assert.Contains("**2 result(s) for 'phi':**", statusResult);
        Assert.DoesNotContain("release filter", statusResult); // header never includes status or release label
        Assert.Contains("draft-free", statusResult);
        Assert.Contains("draft-released", statusResult);

        var emptyStatusResult = await _composer.SearchGoalsAsync("zzz-nonexistent", status: "Draft");
        var emptyStatusAllResult = await _composer.SearchGoalsAsync("zzz-nonexistent", status: "Draft", release: "all");

        Assert.Equal(emptyStatusResult, emptyStatusAllResult);
        Assert.Contains("No goals matching 'zzz-nonexistent' with status Draft.", emptyStatusResult);
        Assert.DoesNotContain("release filter", emptyStatusResult);
    }

    [Fact]
    public async Task SearchGoals_StatusPlusRelease_Combines()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.CreateReleaseAsync(new Release { Id = "rel-planning", Tag = "v2.0", Status = ReleaseStatus.Planning }, ct);

        // Draft goal with null ReleaseId — matches status=Draft AND unreleased.
        await _composer.CreateGoalAsync("draft-free", "Shared keyword epsilon");

        // Pending goal with null ReleaseId — matches unreleased but NOT status=Draft.
        await _composer.CreateGoalAsync("pending-free", "Shared keyword epsilon pending");
        var pendingGoal = await _store.GetGoalAsync("pending-free", ct);
        Assert.NotNull(pendingGoal);
        pendingGoal!.Status = GoalStatus.Pending;
        await _store.UpdateGoalAsync(pendingGoal, ct);

        // Draft goal in a Planning release — matches status=Draft AND unreleased (Planning is unreleased).
        await _composer.CreateGoalAsync("draft-in-planning", "Shared keyword epsilon planning");
        var draftInPlanning = await _store.GetGoalAsync("draft-in-planning", ct);
        Assert.NotNull(draftInPlanning);
        draftInPlanning!.ReleaseId = "rel-planning";
        await _store.UpdateGoalAsync(draftInPlanning, ct);

        // Filter by status=Draft AND release=unreleased.
        var result = await _composer.SearchGoalsAsync("epsilon", status: "Draft", release: "unreleased");

        Assert.Contains("(release filter: unreleased)", result);
        Assert.Contains("draft-free", result);
        Assert.Contains("draft-in-planning", result);
        Assert.DoesNotContain("pending-free", result); // excluded by status filter
    }

    [Fact]
    public void SearchGoals_RegisteredWithReleaseParameter()
    {
        var tools = _composer.BuildComposerTools();
        var searchGoals = tools.OfType<AIFunction>().Single(t => t.Name == "search_goals");

        var parameters = searchGoals.UnderlyingMethod!.GetParameters();
        var releaseParam = parameters.Single(p => p.Name == "release");

        Assert.True(releaseParam.HasDefaultValue);
        Assert.Null(releaseParam.DefaultValue); // opt-in: default is null (no filtering)

        var descriptionAttr = releaseParam.GetCustomAttributesData()
            .First(a => a.AttributeType.FullName == "System.ComponentModel.DescriptionAttribute");
        var description = descriptionAttr.ConstructorArguments[0].Value as string;

        Assert.NotNull(description);
        Assert.Contains("release", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omitted", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchGoals_SystemPromptDocumentsReleaseFilter()
    {
        var prompt = _composer.GetSystemPrompt();

        Assert.Contains("search_goals", prompt);
        Assert.Contains("release filter", prompt, StringComparison.OrdinalIgnoreCase);
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
            .ReturnsAsync(BranchDeleteResult.Success)
            .Verifiable();
        mockRepoManager.Setup(r => r.DeleteRemoteBranchAsync("repo-b", "copilothive/failed-goal-branches", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchDeleteResult.Success)
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
    public async Task DeleteGoal_FailedGoal_BestEffortCleanup_StillSucceedsWhenDeleteRemoteBranchReturnsFailed()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a failed goal with repositories
        await _composer.CreateGoalAsync("failed-cleanup", "Will fail", repositories: "repo-a");
        var goal = (await _store.GetGoalAsync("failed-cleanup", ct))!;
        goal.Status = GoalStatus.Failed;
        await _store.UpdateGoalAsync(goal, ct);

        // Mock the repo manager to return Failed (real BrainRepoManager does not throw — returns Failed)
        var mockRepoManager = new Mock<IBrainRepoManager>();
        mockRepoManager.Setup(r => r.DeleteRemoteBranchAsync("repo-a", "copilothive/failed-cleanup", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchDeleteResult.Failed)
            .Verifiable();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: mockRepoManager.Object,
            stateDir: Path.GetTempPath());

        // DeleteRemoteBranchAsync returns Failed, but goal deletion should still succeed - this is "best-effort"
        var result = await composer.DeleteGoalAsync("failed-cleanup");

        // Verify the goal was deleted despite branch cleanup issues
        Assert.Contains("✅", result);
        Assert.Contains("deleted", result);
        var deletedGoal = await _store.GetGoalAsync("failed-cleanup", ct);
        Assert.Null(deletedGoal);

        // Verify that the cleanup was attempted (even though it returned Failed)
        mockRepoManager.Verify(r => r.DeleteRemoteBranchAsync("repo-a", "copilothive/failed-cleanup", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteGoal_RemovesKnowledgeDocuments()
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = "del-doc-cleanup";
        await _composer.CreateGoalAsync(goalId, "Goal with knowledge docs");

        // Create a knowledge graph with progress/review docs and register the cleanup service
        var kg = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        await kg.CreateDocumentAsync(
            $"progress-{goalId}", "Progress", DocumentType.Scratch, "progress content",
            topic: "progress", ct: ct);
        await kg.CreateDocumentAsync(
            $"review-{goalId}", "Review", DocumentType.Scratch, "review content",
            topic: "review", ct: ct);

        var services = new ServiceCollection();
        services.AddSingleton(kg);
        services.AddSingleton<KnowledgeDocumentCleanupService>();
        services.AddSingleton<ILogger<KnowledgeDocumentCleanupService>>(NullLogger<KnowledgeDocumentCleanupService>.Instance);
        using var sp = services.BuildServiceProvider();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            serviceProvider: sp,
            knowledgeGraph: kg);

        var result = await composer.DeleteGoalAsync(goalId);

        Assert.Contains("✅", result);
        var goal = await _store.GetGoalAsync(goalId, ct);
        Assert.Null(goal);
        Assert.Null(kg.GetDocument($"progress-{goalId}"));
        Assert.Null(kg.GetDocument($"review-{goalId}"));
    }

    [Fact]
    public async Task DeleteGoal_CleanupThrows_StillSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = "del-doc-throw";
        await _composer.CreateGoalAsync(goalId, "Goal whose doc cleanup throws");

        // Graph backed by a config repo whose DeleteFileAsync always throws OCE
        var throwingRepo = new ThrowingConfigRepoManager(Path.GetTempPath());
        var kg = new CopilotHive.Knowledge.KnowledgeGraph(throwingRepo, logger: null);
        await kg.CreateDocumentAsync(
            $"progress-{goalId}", "Progress", DocumentType.Scratch, "progress content",
            topic: "progress", ct: ct);

        var services = new ServiceCollection();
        services.AddSingleton(kg);
        services.AddSingleton<KnowledgeDocumentCleanupService>();
        services.AddSingleton<ILogger<KnowledgeDocumentCleanupService>>(NullLogger<KnowledgeDocumentCleanupService>.Instance);
        using var sp = services.BuildServiceProvider();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            serviceProvider: sp,
            knowledgeGraph: kg);

        // Cleanup throws, but goal deletion must still succeed (best-effort)
        var result = await composer.DeleteGoalAsync(goalId);

        Assert.Contains("✅", result);
        Assert.Contains("deleted", result);
        var goal = await _store.GetGoalAsync(goalId, ct);
        Assert.Null(goal);
    }

    [Fact]
    public async Task DeleteGoal_NoDocuments_SucceedsAsNoop()
    {
        var ct = TestContext.Current.CancellationToken;
        var goalId = "del-no-docs";
        await _composer.CreateGoalAsync(goalId, "Goal without knowledge docs");

        var kg = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        var services = new ServiceCollection();
        services.AddSingleton(kg);
        services.AddSingleton<KnowledgeDocumentCleanupService>();
        services.AddSingleton<ILogger<KnowledgeDocumentCleanupService>>(NullLogger<KnowledgeDocumentCleanupService>.Instance);
        using var sp = services.BuildServiceProvider();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            serviceProvider: sp,
            knowledgeGraph: kg);

        var result = await composer.DeleteGoalAsync(goalId);

        Assert.Contains("✅", result);
        Assert.Contains("deleted", result);
        var goal = await _store.GetGoalAsync(goalId, ct);
        Assert.Null(goal);
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
                serviceProvider: BuildServiceProvider(dispatcher));

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
                serviceProvider: BuildServiceProvider(dispatcher));

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

    // ── extend_goal_iterations ──

    [Fact]
    public async Task ExtendGoalIterations_ValidInput_ReturnsSuccessMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Create failed goal with iteration exhaustion
            await _composer.CreateGoalAsync("extend-iterations", "Goal to extend");
            var goal = await _store.GetGoalAsync("extend-iterations", ct);
            Assert.NotNull(goal);
            goal!.Status = GoalStatus.Failed;
            goal.FailureReason = "Exceeded max iterations";
            await _store.UpdateGoalAsync(goal, ct);

            // Use a shared PipelineStore so the dispatcher can restore the persisted Failed pipeline.
            await using var pipelineStore = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var goalManager = new GoalManager();
            goalManager.AddSource(new FakeGoalSource(goal, _store));
            var pipelineManager = new GoalPipelineManager(pipelineStore);
            var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
            while (pipeline.IterationBudget.TryConsume()) { }
            pipeline.AdvanceTo(GoalPhase.Failed);
            pipelineManager.PersistFull(pipeline);

            var dispatcher = new GoalDispatcher(
                goalManager,
                pipelineManager,
                new TaskQueue(),
                new GrpcWorkerGateway(new WorkerPool()),
                new TaskCompletionNotifier(),
                NullLogger<GoalDispatcher>.Instance,
                repoManager,
                goalStore: _store);

            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir,
                serviceProvider: BuildServiceProvider(dispatcher));

            var result = await composer.ExtendGoalIterationsAsync("extend-iterations", 5);

            Assert.Contains("✅", result);
            Assert.Contains("Extended", result);
            Assert.Contains("extend-iterations", result);
            Assert.Contains("5", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExtendGoalIterations_InvalidIterationsZero_ReturnsErrorMessage()
    {
        var result = await _composer.ExtendGoalIterationsAsync("extend-iterations-zero", additionalIterations: 0);

        Assert.Contains("ERROR", result);
        Assert.Contains("between 1 and 100", result);
    }

    [Fact]
    public async Task ExtendGoalIterations_InvalidIterationsAbove100_ReturnsErrorMessage()
    {
        var result = await _composer.ExtendGoalIterationsAsync("extend-iterations-above", additionalIterations: 101);

        Assert.Contains("ERROR", result);
        Assert.Contains("between 1 and 100", result);
    }

    [Fact]
    public async Task ExtendGoalIterations_NullServiceProvider_ReturnsDispatcherNotAvailable()
    {
        // _composer is constructed without a serviceProvider
        var result = await _composer.ExtendGoalIterationsAsync("extend-iterations-null", additionalIterations: 5);

        Assert.Contains("❌", result);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ExtendGoalIterations_NonExistentGoal_ReturnsNotFoundMessage()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var goalManager = new GoalManager();
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
                serviceProvider: BuildServiceProvider(dispatcher));

            var result = await composer.ExtendGoalIterationsAsync("nonexistent-goal", additionalIterations: 5);

            Assert.Contains("❌", result);
            Assert.Contains("not found", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExtendGoalIterations_BlankId_ReturnsValidationError()
    {
        var result = await _composer.ExtendGoalIterationsAsync("   ", additionalIterations: 5);

        Assert.Contains("ERROR", result);
        Assert.Contains("id is required", result);
    }

    [Fact]
    public async Task ExtendGoalIterations_DefaultIterations_IsFive()
    {
        // The default value for additionalIterations is 5; with a null serviceProvider
        // we can't reach the dispatcher, but we can verify the default doesn't trigger
        // the bounds error (i.e. 5 is within 1–100).
        var result = await _composer.ExtendGoalIterationsAsync("some-goal");

        // Should NOT contain bounds error — should reach the dispatcher-not-available path.
        Assert.DoesNotContain("between 1 and 100", result);
        Assert.Contains("not available", result);
    }

    [Fact]
    public void BuildComposerTools_IncludesExtendGoalIterations()
    {
        var tools = _composer.BuildComposerTools();
        var names = tools.Select(t => t.Name).ToList();
        Assert.Contains("extend_goal_iterations", names);
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
                new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 45.2 },
                new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Pass, DurationSeconds = 120.1 },
                new PhaseResult { Name = GoalPhase.Review, Result = PhaseOutcome.Fail, DurationSeconds = 30.5 },
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
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 10.0 }],
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
            Phases = [new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Fail, DurationSeconds = 5.0 }],
        };
        await _store.AddIterationAsync("iter-no-counts", summary, ct);

        var result = await _composer.GetGoalAsync("iter-no-counts");

        // Testing line without test counts suffix
        Assert.Contains("- Testing: fail (5.0s)\n", result);
        Assert.DoesNotContain(" — ", result);
    }

    [Fact]
    public async Task GetGoal_WithClarifications_DisplaysClarificationsInIteration()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("iter-clarif", "Goal with clarifications");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 30.0 }],
            Clarifications =
            [
                new PersistedClarification
                {
                    Timestamp = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                    Phase = "Coding",
                    WorkerRole = "coder",
                    Question = "Which pattern should I use?",
                    Answer = "Use repository pattern.",
                    AnsweredBy = "brain",
                },
            ],
        };
        await _store.AddIterationAsync("iter-clarif", summary, ct);

        var result = await _composer.GetGoalAsync("iter-clarif");

        // Clarifications section header
        Assert.Contains("  Clarifications:", result);
        // Clarification entry format: [AnsweredBy] WorkerRole (Phase): Q: Question
        Assert.Contains("  - [brain] coder (Coding): Q: Which pattern should I use?", result);
        // Answer line
        Assert.Contains("    A: Use repository pattern.", result);
    }

    [Fact]
    public async Task GetGoal_WithMultipleClarifications_DisplaysAll()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("iter-multi-clarif", "Goal with multiple clarifications");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 30.0 },
                new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Pass, DurationSeconds = 60.0 },
            ],
            Clarifications =
            [
                new PersistedClarification
                {
                    Timestamp = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                    Phase = "Coding",
                    WorkerRole = "coder",
                    Question = "What pattern?",
                    Answer = "Repository.",
                    AnsweredBy = "brain",
                },
                new PersistedClarification
                {
                    Timestamp = new DateTime(2024, 6, 1, 12, 30, 0, DateTimeKind.Utc),
                    Phase = "Testing",
                    WorkerRole = "tester",
                    Question = "Run integration tests?",
                    Answer = "Yes, run them.",
                    AnsweredBy = "composer",
                },
            ],
        };
        await _store.AddIterationAsync("iter-multi-clarif", summary, ct);

        var result = await _composer.GetGoalAsync("iter-multi-clarif");

        // Both clarifications should appear
        Assert.Contains("  - [brain] coder (Coding): Q: What pattern?", result);
        Assert.Contains("    A: Repository.", result);
        Assert.Contains("  - [composer] tester (Testing): Q: Run integration tests?", result);
        Assert.Contains("    A: Yes, run them.", result);
    }

    [Fact]
    public async Task GetGoal_NoClarifications_OmitsClarificationsSection()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("iter-no-clarif", "Goal without clarifications");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 10.0 }],
            Clarifications = [],
        };
        await _store.AddIterationAsync("iter-no-clarif", summary, ct);

        var result = await _composer.GetGoalAsync("iter-no-clarif");

        // No clarifications section should appear
        Assert.DoesNotContain("  Clarifications:", result);
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
                new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 10.0, WorkerOutput = "coder log line 1\ncoder log line 2" },
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
            Phases = [new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Pass, DurationSeconds = 5.0, WorkerOutput = "test output" }],
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
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 10.0, WorkerOutput = null }],
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
            Phases = [new PhaseResult { Name = Enum.Parse<GoalPhase>(phaseName), Result = PhaseOutcome.Pass, DurationSeconds = 1.0, WorkerOutput = null }],
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
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 1.0, WorkerOutput = longOutput }],
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
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 1.0 }],
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
            Phases = [new PhaseResult { Name = GoalPhase.Planning, Result = PhaseOutcome.Pass, DurationSeconds = 1.0, WorkerOutput = null }],
        };
        await _store.AddIterationAsync("phase-unknown", summary, ct);

        var result = await _composer.GetPhaseOutputAsync("phase-unknown", 1, "Blah");

        Assert.Contains("Unknown phase 'Blah'", result);
        Assert.Contains("Coding, Testing, Review, DocWriting, Improve", result);
    }

    [Fact]
    public async Task GetPhaseOutput_NumericPhaseString_ReturnsUnknownPhaseMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-numeric", "Numeric phase string test");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 1.0, WorkerOutput = "output" }],
        };
        await _store.AddIterationAsync("phase-numeric", summary, ct);

        foreach (var numericPhase in new[] { "1", "2", "999" })
        {
            var result = await _composer.GetPhaseOutputAsync("phase-numeric", 1, numericPhase);
            Assert.Contains($"Unknown phase '{numericPhase}'", result);
            Assert.Contains("Coding, Testing, Review, DocWriting, Improve", result);
        }

        // Valid phase names must still work (case-insensitive)
        var codingResult = await _composer.GetPhaseOutputAsync("phase-numeric", 1, "Coding");
        Assert.DoesNotContain("Unknown phase", codingResult);

        var testingResult = await _composer.GetPhaseOutputAsync("phase-numeric", 1, "testing");
        Assert.DoesNotContain("Unknown phase", testingResult);
    }

    [Fact]
    public async Task GetPhaseOutput_NonWorkerPhase_ReturnsNoOutputKeyMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-nonworker", "Non-worker phase test");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = GoalPhase.Planning, Result = PhaseOutcome.Pass, DurationSeconds = 1.0, WorkerOutput = null }],
        };
        await _store.AddIterationAsync("phase-nonworker", summary, ct);

        var result = await _composer.GetPhaseOutputAsync("phase-nonworker", 1, "Planning");

        Assert.Contains("does not have a worker output key", result);
    }

    [Fact]
    public async Task GetPhaseOutput_NoOutputRecorded_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("phase-no-output", "No output recorded");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 1.0, WorkerOutput = null }],
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
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 10.0, WorkerOutput = "coder output here" }],
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

        // Without persisted iteration data, there's no brain prompt to retrieve
        var result = await _composer.GetPhaseOutputAsync("no-pipeline", 1, "Coding", content: "brain_prompt");

        Assert.Contains("No brain prompt is available for phase 'Coding' in iteration 1 of goal 'no-pipeline'", result);
    }

    [Fact]
    public async Task GetPhaseOutput_WorkerPrompt_NoPipelineConversation_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("no-pipeline-wp", "Goal without pipeline conversation");

        var result = await _composer.GetPhaseOutputAsync("no-pipeline-wp", 1, "Coding", content: "worker_prompt");

        Assert.Contains("No worker prompt is available for phase 'Coding' in iteration 1 of goal 'no-pipeline-wp'", result);
    }

    // ── GetPhaseOutputAsync with PipelineStore for brain_prompt/worker_prompt ──

    [Fact]
    public async Task GetPhaseOutput_BrainPrompt_WithPipelineStore_ReturnsPrompt()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a PipelineStore and wire it to a new GoalStore
        var pipelineStore = new PipelineStore(_dbContext, NullLogger<PipelineStore>.Instance);
        var storeWithPipeline = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance, pipelineStore);
        var composerWithPipeline = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            storeWithPipeline,
            stateDir: Path.GetTempPath());

        // Create a goal with persisted iteration summary containing brain prompt
        await composerWithPipeline.CreateGoalAsync("brain-prompt-goal", "Goal with brain prompt");
        var goal = await storeWithPipeline.GetGoalAsync("brain-prompt-goal", ct);
        Assert.NotNull(goal);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult
                {
                    Name = GoalPhase.Coding,
                    Result = PhaseOutcome.Pass,
                    BrainPrompt = "Brain asks coder to implement X",
                    WorkerPrompt = "Your task: implement X",
                    WorkerOutput = "Coder completed.",
                },
            ],
        };
        await storeWithPipeline.AddIterationAsync("brain-prompt-goal", summary, ct);

        // Now test GetPhaseOutputAsync with content: "brain_prompt"
        var result = await composerWithPipeline.GetPhaseOutputAsync("brain-prompt-goal", 1, "Coding", content: "brain_prompt");

        Assert.Contains("Brain asks coder to implement X", result);
    }

    [Fact]
    public async Task GetPhaseOutput_WorkerPrompt_WithPipelineStore_ReturnsPrompt()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a PipelineStore and wire it to a new GoalStore
        var pipelineStore = new PipelineStore(_dbContext, NullLogger<PipelineStore>.Instance);
        var storeWithPipeline = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance, pipelineStore);
        var composerWithPipeline = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            storeWithPipeline,
            stateDir: Path.GetTempPath());

        // Create a goal with persisted iteration summary containing worker prompt
        await composerWithPipeline.CreateGoalAsync("worker-prompt-goal", "Goal with worker prompt");
        var goal = await storeWithPipeline.GetGoalAsync("worker-prompt-goal", ct);
        Assert.NotNull(goal);

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult
                {
                    Name = GoalPhase.Testing,
                    Result = PhaseOutcome.Pass,
                    BrainPrompt = "Brain prompt for tester",
                    WorkerPrompt = "Your task: test the code",
                    WorkerOutput = "Tests passed!",
                },
            ],
        };
        await storeWithPipeline.AddIterationAsync("worker-prompt-goal", summary, ct);

        // Now test GetPhaseOutputAsync with content: "worker_prompt"
        var result = await composerWithPipeline.GetPhaseOutputAsync("worker-prompt-goal", 1, "Testing", content: "worker_prompt");

        Assert.Contains("Your task: test the code", result);
    }

    [Fact]
    public async Task GetPhaseOutput_BrainPrompt_NoPromptForPhase_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a PipelineStore and wire it
        var pipelineStore = new PipelineStore(_dbContext, NullLogger<PipelineStore>.Instance);
        var storeWithPipeline = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance, pipelineStore);
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

        Assert.Contains("No brain prompt is available for phase 'Review' in iteration 1 of goal 'partial-prompt-goal'", result);
    }

    [Fact]
    public async Task GetPhaseOutput_BrainPrompt_TruncatesToMaxLines()
    {
        var ct = TestContext.Current.CancellationToken;

        var pipelineStore = new PipelineStore(_dbContext, NullLogger<PipelineStore>.Instance);
        var storeWithPipeline = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance, pipelineStore);
        var composerWithPipeline = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            storeWithPipeline,
            stateDir: Path.GetTempPath());

        await composerWithPipeline.CreateGoalAsync("brain-prompt-trunc", "Goal for brain_prompt truncation test");
        var goal = await storeWithPipeline.GetGoalAsync("brain-prompt-trunc", ct);
        Assert.NotNull(goal);

        // Build a brain prompt with 300 lines
        var longBrainPrompt = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"Brain line {i}"));

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult
                {
                    Name = GoalPhase.Coding,
                    Result = PhaseOutcome.Pass,
                    BrainPrompt = longBrainPrompt,
                    WorkerPrompt = "Your task: implement X",
                    WorkerOutput = "Coder completed.",
                },
            ],
        };
        await storeWithPipeline.AddIterationAsync("brain-prompt-trunc", summary, ct);

        var result = await composerWithPipeline.GetPhaseOutputAsync("brain-prompt-trunc", 1, "Coding", content: "brain_prompt", max_lines: 10);

        Assert.Contains("truncated", result);
        Assert.Contains("300 lines total", result);
        Assert.DoesNotContain("Brain line 300", result);
        Assert.Contains("Brain line 1", result);
    }

    [Fact]
    public async Task GetPhaseOutput_WorkerPrompt_TruncatesToMaxLines()
    {
        var ct = TestContext.Current.CancellationToken;

        var pipelineStore = new PipelineStore(_dbContext, NullLogger<PipelineStore>.Instance);
        var storeWithPipeline = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance, pipelineStore);
        var composerWithPipeline = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            storeWithPipeline,
            stateDir: Path.GetTempPath());

        await composerWithPipeline.CreateGoalAsync("worker-prompt-trunc", "Goal for worker_prompt truncation test");
        var goal = await storeWithPipeline.GetGoalAsync("worker-prompt-trunc", ct);
        Assert.NotNull(goal);

        // Build a worker prompt with 300 lines
        var longWorkerPrompt = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"Worker line {i}"));

        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases =
            [
                new PhaseResult
                {
                    Name = GoalPhase.Testing,
                    Result = PhaseOutcome.Pass,
                    BrainPrompt = "Brain prompt for tester",
                    WorkerPrompt = longWorkerPrompt,
                    WorkerOutput = "Tests passed!",
                },
            ],
        };
        await storeWithPipeline.AddIterationAsync("worker-prompt-trunc", summary, ct);

        var result = await composerWithPipeline.GetPhaseOutputAsync("worker-prompt-trunc", 1, "Testing", content: "worker_prompt", max_lines: 10);

        Assert.Contains("truncated", result);
        Assert.Contains("300 lines total", result);
        Assert.DoesNotContain("Worker line 300", result);
        Assert.Contains("Worker line 1", result);
    }

    [Theory]
    [InlineData("brain")]
    [InlineData("promptt")]
    [InlineData("worker")]
    [InlineData("Brain_Prompt")]
    [InlineData("invalid")]
    [InlineData("")]
    public async Task GetPhaseOutput_InvalidContent_ReturnsValidationError(string invalidContent)
    {
        await _composer.CreateGoalAsync("invalid-content", "Goal for invalid content test");
        var summary = new IterationSummary
        {
            Iteration = 1,
            Phases = [new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, DurationSeconds = 5.0, WorkerOutput = "should not see this" }],
        };
        var ct = TestContext.Current.CancellationToken;
        await _store.AddIterationAsync("invalid-content", summary, ct);

        var result = await _composer.GetPhaseOutputAsync("invalid-content", 1, "Coding", content: invalidContent);

        Assert.Contains($"Invalid content '{invalidContent}'. Valid values: output, brain_prompt, worker_prompt.", result);
        Assert.DoesNotContain("should not see this", result);
    }

    [Fact]
    public void BuildComposerTools_IncludesGetPhaseOutput()
    {
        var tools = _composer.BuildComposerTools();
        var toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains("get_phase_output", toolNames);
    }

    [Fact]
    public void BuildComposerTools_IncludesGetCurrentTime()
    {
        var tools = _composer.BuildComposerTools();
        var names = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains("get_current_time", names);
    }

    [Fact]
    public async Task GetCurrentTimeTool_ReturnsValidJsonWithExpectedFields()
    {
        var tools = _composer.BuildComposerTools();
        var tool = tools.OfType<AIFunction>().First(t => t.Name == "get_current_time");

        // Act: invoke the get_current_time tool (no arguments needed)
        var result = (await tool.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken))?.ToString() ?? "";

        // Assert: the result is valid JSON containing the expected fields
        Assert.False(string.IsNullOrWhiteSpace(result));
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("date", out var dateEl), "Result should contain 'date' field");
        Assert.True(root.TryGetProperty("time", out var timeEl), "Result should contain 'time' field");
        Assert.True(root.TryGetProperty("iso", out var isoEl), "Result should contain 'iso' field");
        Assert.True(root.TryGetProperty("timezone", out var tzEl), "Result should contain 'timezone' field");

        var date = dateEl.GetString()!;
        var time = timeEl.GetString()!;
        var iso = isoEl.GetString()!;
        var timezone = tzEl.GetString()!;

        // date must be YYYY-MM-DD format
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", date);
        // time must be HH:MM:SS format
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", time);
        // iso must be ISO 8601 format (round-trip "o" format from DateTime.UtcNow)
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", iso);
        // timezone must be "UTC"
        Assert.Equal("UTC", timezone);

        // The date should match today's UTC date
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), date);
    }

    [Fact]
    public void SystemPrompt_IncludesDrillIntoPhaseOutput()
    {
        Assert.Contains("get_phase_output", _composer.GetSystemPrompt());
        Assert.Contains("Drill into worker phase output", _composer.GetSystemPrompt());
    }

    [Fact]
    public void SystemPrompt_MentionsGetCurrentTime()
    {
        Assert.Contains("get_current_time", _composer.GetSystemPrompt());
    }

    [Fact]
    public void SystemPrompt_UpdateGoal_MentionsDraftOnlyRestriction()
    {
        var prompt = _composer.GetSystemPrompt();
        Assert.Contains("update_goal", prompt);
        Assert.Contains("Draft goals", prompt);
        Assert.Contains("description", prompt);
        Assert.Contains("priority", prompt);
        Assert.Contains("scope", prompt);
        Assert.Contains("repositories", prompt);
        Assert.Contains("depends_on", prompt);
        Assert.Contains("documents", prompt);
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
            TestHelpers.ForceDeleteDirectory(tmpDir);
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
    public async Task WebFetch_NullLinks_OmitsLinksSection()
    {
        // Tests the fix: links property is null - should NOT throw, should return empty links list
        var mockFactory = new Mock<IHttpClientFactory>();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"title":"Page With Null Links","content":"Content here","links":null}""");
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

        Assert.Contains("# Page With Null Links", result);
        Assert.Contains("Content here", result);
        // Links section should NOT appear because null links are treated as empty
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
            TestHelpers.ForceDeleteDirectory(tmpDir);
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
            TestHelpers.ForceDeleteDirectory(tmpDir);
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
            TestHelpers.ForceDeleteDirectory(tmpDir);
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
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    // ── git_fetch ──

    /// <summary>
    /// Creates a bare clone of the test-repo at <paramref name="barePath"/> and adds it as
    /// the "origin" remote of the test-repo. Returns after configuration completes.
    /// </summary>
    private static void SetupOriginRemote(string tmpDir, string barePath)
    {
        var repoDir = Path.Combine(tmpDir, "repos", "test-repo");

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

        Git(repoDir, "clone", "--bare", repoDir, barePath);
        Git(repoDir, "remote", "add", "origin", barePath);
    }

    /// <summary>
    /// Resolves the absolute path to the real git binary via <c>which git</c>, falling back to
    /// well-known locations. Used so a fake git wrapper can delegate non-intercepted commands.
    /// </summary>
    private static string ResolveRealGitPath()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("which", "git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) && File.Exists(output))
                return output;
        }
        catch
        {
            // Fall through to well-known locations.
        }

        foreach (var candidate in new[] { "/usr/bin/git", "/usr/local/bin/git", "/bin/git" })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Last resort — assume it is on PATH by bare name.
        return "git";
    }

    /// <summary>Marks a file as executable (chmod +x) on Unix-like systems.</summary>
    private static void MakeExecutable(string filePath)
    {
        if (OperatingSystem.IsWindows())
            return;

        var psi = new System.Diagnostics.ProcessStartInfo("chmod")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(filePath);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    [Fact]
    public async Task GitFetch_NoRepoManager_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.GitFetchAsync("any-repo", cancellationToken: ct);
        Assert.Contains("not available", result);
    }

    [Fact]
    public void BuildComposerTools_IncludesGitFetch()
    {
        var tools = _composer.BuildComposerTools();
        var toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains("git_fetch", toolNames);
    }

    [Fact]
    public void SystemPrompt_ContainsGitFetch()
    {
        var prompt = _composer.GetSystemPrompt();
        Assert.Contains("git_fetch", prompt);
    }

    [Fact]
    public async Task GitFetch_DefaultOrigin_FetchesAll()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var result = await composer.GitFetchAsync("test-repo", cancellationToken: ct);

            Assert.DoesNotContain("❌", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_WithBranch_CreatesRemoteTrackingRef()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var fetchResult = await composer.GitFetchAsync("test-repo", branch: "main", cancellationToken: ct);
            Assert.DoesNotContain("❌", fetchResult);

            var branches = await composer.GitBranchAsync("test-repo", remote: true, cancellationToken: ct);
            Assert.Contains("origin/main", branches);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_WithBranch_ForceUpdatesTrackingRefOnNonFastForward()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            var repoDir = Path.Combine(tmpDir, "repos", "test-repo");

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

            static string GitCapture(string workDir, params string[] args)
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
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return output;
            }

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            // Capture the original (root) commit on the remote before advancing it.
            var firstCommitSha = GitCapture(barePath, "rev-list", "--max-parents=0", "HEAD");
            Assert.False(string.IsNullOrWhiteSpace(firstCommitSha));

            // Phase 1: initial fetch — creates origin/main pointing at the first commit.
            var fetch1 = await composer.GitFetchAsync("test-repo", branch: "main", cancellationToken: ct);
            Assert.DoesNotContain("❌", fetch1);

            // Advance the remote: add a second commit locally and push it to the bare remote.
            Git(repoDir, "commit", "--allow-empty", "-m", "Second commit");
            Git(repoDir, "push", "origin", "main");

            // Phase 2: fast-forward fetch — succeeds with or without the '+' prefix.
            var fetch2 = await composer.GitFetchAsync("test-repo", branch: "main", cancellationToken: ct);
            Assert.DoesNotContain("❌", fetch2);

            // Rewind the remote's main back to the first commit — a NON-fast-forward change.
            Git(barePath, "update-ref", "refs/heads/main", firstCommitSha);

            // Phase 3: non-fast-forward fetch. WITH the leading '+' in the refspec, git forcibly
            // updates the tracking ref and succeeds. WITHOUT the '+', git rejects the update as a
            // non-fast-forward and the result contains a failure — making this test removal-proof
            // for the force-update prefix.
            var fetch3 = await composer.GitFetchAsync("test-repo", branch: "main", cancellationToken: ct);
            Assert.DoesNotContain("❌", fetch3);
            Assert.DoesNotContain("failed", fetch3);

            // The tracking ref must now point back at the first (rewound) commit.
            var show = await composer.GitShowAsync("test-repo", "origin/main", cancellationToken: ct);
            Assert.Contains("Initial commit", show);
            Assert.DoesNotContain("Second commit", show);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_URLRemote_Rejected()
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

            var result = await composer.GitFetchAsync("test-repo", remote: "https://evil.com/x", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("URL", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_OptionInjectionRemote_Rejected()
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

            var result = await composer.GitFetchAsync("test-repo", remote: "--upload-pack=evil", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("cannot start with '-'", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_RefspecLikeRemote_Rejected()
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

            var result = await composer.GitFetchAsync("test-repo", remote: "origin:evil", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("':' or '+'", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_TransportHelperRemote_Rejected()
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

            var result = await composer.GitFetchAsync("test-repo", remote: "ext::evil", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("::", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_UnconfiguredRemote_Rejected()
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

            var result = await composer.GitFetchAsync("test-repo", remote: "nonexistent", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("not configured", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_AtSymbolRejection()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var result = await composer.GitFetchAsync("test-repo", branch: "@{-1}", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("@{", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_InvalidBranchName_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var result = await composer.GitFetchAsync("test-repo", branch: "..", cancellationToken: ct);

            Assert.Contains("❌", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_CheckRefFormatOutputEquality()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        // Locate the real git binary so the fake wrapper can delegate non-intercepted commands.
        var realGit = ResolveRealGitPath();

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            // Deterministic test seam: a fake "git" that intercepts `check-ref-format --branch`
            // and echoes a NORMALIZED value that differs from the supplied branch (exit 0), while
            // delegating every other git command to the real binary. This forces the
            // stdout-equality guard in GitFetchAsync down the rejection path regardless of the
            // host git version. If that equality guard is removed, the fetch proceeds and the
            // assertions below fail — making this test removal-proof.
            var fakeGitDir = Path.Combine(tmpDir, "fakegit");
            Directory.CreateDirectory(fakeGitDir);
            var fakeGitPath = Path.Combine(fakeGitDir, "git");
            File.WriteAllText(fakeGitPath,
                "#!/bin/bash\n" +
                $"REAL_GIT={realGit}\n" +
                "if [ \"$1\" = \"check-ref-format\" ] && [ \"$2\" = \"--branch\" ]; then\n" +
                "    # Echo a normalized value that differs from the supplied branch.\n" +
                "    echo \"normalized-$3\"\n" +
                "    exit 0\n" +
                "else\n" +
                "    exec \"$REAL_GIT\" \"$@\"\n" +
                "fi\n");
            MakeExecutable(fakeGitPath);

            // Prepend the fake git dir so TryRunGitAsync / RunGitAsync (which spawn "git" via PATH)
            // pick it up. Child processes inherit this environment.
            Environment.SetEnvironmentVariable("PATH", fakeGitDir + Path.PathSeparator + originalPath);

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            // The fake git turns `check-ref-format --branch main` into stdout "normalized-main",
            // which differs from the input "main". The equality guard MUST reject it.
            var result = await composer.GitFetchAsync("test-repo", branch: "main", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("normalized", result);

            // And no tracking ref should have been created (fetch was never invoked).
            var branches = await composer.GitBranchAsync("test-repo", remote: true, cancellationToken: ct);
            Assert.DoesNotContain("origin/main", branches);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_RepositoryNameValidation()
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

            var dotDot = await composer.GitFetchAsync("../etc", cancellationToken: ct);
            Assert.Contains("❌", dotDot);
            Assert.Contains("..", dotDot);

            var slash = await composer.GitFetchAsync("with/slash", cancellationToken: ct);
            Assert.Contains("❌", slash);
            Assert.Contains("separator", slash);

            var flag = await composer.GitFetchAsync("-flag", cancellationToken: ct);
            Assert.Contains("❌", flag);
            Assert.Contains("cannot start with '-'", flag);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_BlankBranch_TreatedAsOmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var result = await composer.GitFetchAsync("test-repo", branch: "   ", cancellationToken: ct);

            Assert.DoesNotContain("❌", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_CancellationToken_Respected()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await composer.GitFetchAsync("test-repo", cancellationToken: cts.Token));
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_SshUrlRemote_Rejected()
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

            var result = await composer.GitFetchAsync("test-repo", remote: "ssh://evil.com/x", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("URL", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_GitAtUrlRemote_Rejected()
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

            var result = await composer.GitFetchAsync("test-repo", remote: "git@evil.com:org/repo.git", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("URL", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_DefaultOrigin_VerifiesFetchSucceeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            var result = await composer.GitFetchAsync("test-repo", cancellationToken: ct);

            Assert.DoesNotContain("❌", result);

            // Verify the fetch actually succeeded by listing remote tracking branches.
            var branches = await composer.GitBranchAsync("test-repo", remote: true, cancellationToken: ct);
            Assert.Contains("origin/main", branches);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_WithBranch_GitShowCanReferenceFetchedCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            // Fetch the branch.
            var fetchResult = await composer.GitFetchAsync("test-repo", branch: "main", cancellationToken: ct);
            Assert.DoesNotContain("❌", fetchResult);

            // After fetching, git_show should be able to reference the fetched commit via origin/main.
            var showResult = await composer.GitShowAsync("test-repo", "origin/main", stat_only: true, cancellationToken: ct);
            Assert.DoesNotContain("❌", showResult);
            Assert.DoesNotContain("failed", showResult, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("README.md", showResult);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_WithBranch_GitDiffCanReferenceFetchedCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            InitTempGitRepo(tmpDir);
            var barePath = Path.Combine(tmpDir, "remote.git");
            SetupOriginRemote(tmpDir, barePath);

            var repoManager = new BrainRepoManager(tmpDir, NullLogger<BrainRepoManager>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                _store,
                repoManager: repoManager,
                stateDir: tmpDir);

            // Fetch the branch.
            var fetchResult = await composer.GitFetchAsync("test-repo", branch: "main", cancellationToken: ct);
            Assert.DoesNotContain("❌", fetchResult);

            // After fetching, git_diff should be able to diff against the fetched ref.
            var diffResult = await composer.GitDiffAsync("test-repo", "origin/main", cancellationToken: ct);
            Assert.DoesNotContain("❌", diffResult);
            // An empty diff is valid (HEAD == origin/main), but it must not be an error.
            Assert.DoesNotContain("failed", diffResult, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_PlusInRemote_Rejected()
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

            var result = await composer.GitFetchAsync("test-repo", remote: "origin+evil", cancellationToken: ct);

            Assert.Contains("❌", result);
            Assert.Contains("':' or '+'", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_EmptyRepositoryName_Rejected()
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

            var result = await composer.GitFetchAsync("", cancellationToken: ct);
            Assert.Contains("❌", result);
            Assert.Contains("required", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
        }
    }

    [Fact]
    public async Task GitFetch_WhitespaceRepositoryName_Rejected()
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

            var result = await composer.GitFetchAsync("   ", cancellationToken: ct);
            Assert.Contains("❌", result);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tmpDir);
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
        Assert.Contains("Knowledge consultation", prompt);
        Assert.Contains("memory-composer-operating-procedures", prompt);
        Assert.Contains("Idea-to-Implementation Document Transition", prompt);
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
        Assert.Null(_composer.PendingQuestion);
    }

    [Fact]
    public async Task AskUser_MultiChoiceWithNoOptions_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.AskUserAsync("Pick all?", type: "MultiChoice", options: null, cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("Options are required", result);
        Assert.Null(_composer.PendingQuestion);
    }

    [Fact]
    public async Task AskUser_SingleChoiceTooFewOptions_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.AskUserAsync("Pick one?", type: "SingleChoice", options: ["Only"], cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("At least 2 options", result);
        Assert.Null(_composer.PendingQuestion);
    }

    [Fact]
    public async Task AskUser_SingleChoiceBlankOption_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.AskUserAsync("Pick one?", type: "SingleChoice", options: ["A", "   "], cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("non-blank", result);
        Assert.Null(_composer.PendingQuestion);
    }

    [Fact]
    public async Task AskUser_SingleChoiceDuplicateOption_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.AskUserAsync("Pick one?", type: "SingleChoice", options: ["Alpha", "alpha"], cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("Duplicate option", result);
        Assert.Null(_composer.PendingQuestion);
    }

    [Fact]
    public async Task AskUser_SingleChoiceOverCap_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.AskUserAsync("Pick one?", type: "SingleChoice", options: Enumerable.Range(1, 51).Select(i => $"Option {i}").ToArray(), cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("At most 50 options", result);
        Assert.Null(_composer.PendingQuestion);
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
    public async Task AskUser_YesNoSuppliedOptions_IgnoredAndSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var askTask = _composer.AskUserAsync("Confirm?", type: "YesNo", options: ["A", "B", "C"], cancellationToken: ct);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_composer.PendingQuestion is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);

        var pending = _composer.PendingQuestion;
        Assert.NotNull(pending);
        Assert.Equal(QuestionType.YesNo, pending!.Type);
        Assert.Equal(["Yes", "No"], pending.Options);

        _composer.SubmitAnswer("Yes");
        var result = await askTask;
        Assert.Equal("Yes", result);
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
        var askTask = _composer.AskUserAsync("Pick one?", type: "SingleChoice", options: ["Alpha", "Beta", "Gamma"], cancellationToken: ct);

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
        var askTask = _composer.AskUserAsync("Pick all that apply?", type: "MultiChoice", options: ["A", "B", "C"], cancellationToken: ct);

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

    [Fact]
    public void AskUserTool_OptionsParameter_SchemaIsStringArray()
    {
        var tools = _composer.BuildComposerTools();
        var askUser = tools.OfType<AIFunction>().Single(t => t.Name == "ask_user");

        var schema = askUser.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());

        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("options", out var optionsSchema));

        var typeValue = optionsSchema.GetProperty("type");
        var types = typeValue.ValueKind == JsonValueKind.Array
            ? typeValue.EnumerateArray().Select(e => e.GetString()).ToHashSet()
            : [typeValue.GetString()];
        Assert.Contains("array", types);

        var itemsTypeValue = optionsSchema.GetProperty("items").GetProperty("type");
        var itemTypes = itemsTypeValue.ValueKind == JsonValueKind.Array
            ? itemsTypeValue.EnumerateArray().Select(e => e.GetString()).ToHashSet()
            : [itemsTypeValue.GetString()];
        Assert.Contains("string", itemTypes);

        var description = optionsSchema.GetProperty("description").GetString();
        Assert.NotNull(description);
        Assert.DoesNotContain("comma-separated", description, StringComparison.OrdinalIgnoreCase);
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
    /// by calling the private <c>RecreateAgentAsync()</c> method.  Call this BEFORE
    /// <c>SendMessage</c> — no <c>ConnectAsync</c> call is needed, making the test
    /// fully hermetic (no real LLM endpoint required).
    /// </summary>
    private static async Task InjectFakeChatClient(Composer composer, IChatClient fakeClient)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        // The agent lifecycle lives on ComposerAgentService
        var agentServiceField = typeof(Composer).GetField("_agentService", flags)
            ?? throw new InvalidOperationException("_agentService field not found on Composer");
        var agentService = agentServiceField.GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null");
        var serviceType = agentService.GetType();

        var chatClientField = serviceType.GetField("_chatClient", flags)
            ?? throw new InvalidOperationException("_chatClient field not found on ComposerAgentService");
        chatClientField.SetValue(agentService, fakeClient);

        var recreateAgent = serviceType.GetMethod("RecreateAgentAsync", flags | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException("RecreateAgentAsync method not found on ComposerAgentService");
        await (Task)recreateAgent.Invoke(agentService, null)!;
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

            await InjectFakeChatClient(composer, mockClient.Object);

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

            await InjectFakeChatClient(composer, mockClient.Object);

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

    // ── get_phase_output — deleted goal data leak ──

    [Fact]
    public async Task GetPhaseOutput_BrainPrompt_DeletedGoal_ReturnsNotAvailable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a PipelineStore and wire it to a new GoalStore
        var pipelineStore = new PipelineStore(_dbContext, NullLogger<PipelineStore>.Instance);
        var storeWithPipeline = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance, pipelineStore);
        var composerWithPipeline = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            storeWithPipeline,
            stateDir: Path.GetTempPath());

        // Create a goal and pipeline with conversation
        await composerWithPipeline.CreateGoalAsync("deleted-brain-prompt", "Goal to be deleted");
        var goal = await storeWithPipeline.GetGoalAsync("deleted-brain-prompt", ct);
        Assert.NotNull(goal);

        var pipeline = new GoalPipeline(goal, maxRetries: 3);
        pipeline.Conversation.Add(new ConversationEntry("user", "Brain asks coder to implement X", Iteration: 1, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Your task: implement X", Iteration: 1, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("coder", "Coder completed.", Iteration: 1, Purpose: "worker-output"));
        pipelineStore.SavePipeline(pipeline);

        // Delete the goal
        await storeWithPipeline.DeleteGoalAsync("deleted-brain-prompt", ct);

        // Request brain_prompt for the deleted goal — should return "No ... is available" instead of leaking data
        var result = await composerWithPipeline.GetPhaseOutputAsync("deleted-brain-prompt", 1, "Coding", content: "brain_prompt");

        Assert.Contains("No brain prompt is available", result);
        Assert.DoesNotContain("Brain asks coder to implement X", result);
    }

    [Fact]
    public async Task GetPhaseOutput_WorkerPrompt_DeletedGoal_ReturnsNotAvailable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a PipelineStore and wire it to a new GoalStore
        var pipelineStore = new PipelineStore(_dbContext, NullLogger<PipelineStore>.Instance);
        var storeWithPipeline = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance, pipelineStore);
        var composerWithPipeline = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            storeWithPipeline,
            stateDir: Path.GetTempPath());

        // Create a goal and pipeline with conversation
        await composerWithPipeline.CreateGoalAsync("deleted-worker-prompt", "Goal to be deleted");
        var goal = await storeWithPipeline.GetGoalAsync("deleted-worker-prompt", ct);
        Assert.NotNull(goal);

        var pipeline = new GoalPipeline(goal, maxRetries: 3);
        pipeline.Conversation.Add(new ConversationEntry("user", "Brain prompt for tester", Iteration: 1, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Your task: test the code", Iteration: 1, Purpose: "craft-prompt"));
        pipeline.Conversation.Add(new ConversationEntry("tester", "Tests passed!", Iteration: 1, Purpose: "worker-output"));
        pipelineStore.SavePipeline(pipeline);

        // Delete the goal
        await storeWithPipeline.DeleteGoalAsync("deleted-worker-prompt", ct);

        // Request worker_prompt for the deleted goal — should return "No ... is available" instead of leaking data
        var result = await composerWithPipeline.GetPhaseOutputAsync("deleted-worker-prompt", 1, "Testing", content: "worker_prompt");

        Assert.Contains("No worker prompt is available", result);
        Assert.DoesNotContain("Your task: test the code", result);
    }

    /// <summary>
    /// Builds a minimal <see cref="IServiceProvider"/> that resolves the given
    /// <see cref="GoalDispatcher"/> — used to break the circular DI in tests.
    /// </summary>
    private static IServiceProvider BuildServiceProvider(GoalDispatcher dispatcher)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dispatcher);
        return services.BuildServiceProvider();
    }

    // ── review_goal ──

    /// <summary>Minimal <see cref="IChatClient"/> stub that returns a fixed text response.</summary>
    private sealed class StubChatClient(string replyText) : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, replyText))
            {
                FinishReason = ChatFinishReason.Stop,
            };
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return GetStreamingUpdatesAsync(replyText, cancellationToken);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingUpdatesAsync(
            string replyText, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (!cancellationToken.IsCancellationRequested)
                yield return new ChatResponseUpdate(ChatRole.Assistant, replyText) { FinishReason = ChatFinishReason.Stop };
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>Stub GoalReviewService that returns a fixed result via the chat-client factory seam.</summary>
    private sealed class StubGoalReviewService : GoalReviewService
    {
        public StubGoalReviewService(ReviewResult result)
            : base(
                null,
                null,
                null,
                null,
                null,
                Path.GetTempPath(),
                NullLogger<GoalReviewService>.Instance,
                _ => new StubChatClient(BuildJson(result)))
        {
        }

        private static string BuildJson(ReviewResult result)
        {
            var issues = result.Issues == "No issues found."
                ? ""
                : string.Join(",", result.Issues.Split('\n').Select(i =>
                {
                    var description = i.StartsWith("[MAJOR] ", StringComparison.Ordinal) ? i["[MAJOR] ".Length..] : i;
                    return $"{{\"severity\":\"MAJOR\",\"description\":\"{Escape(description)}\"}}";
                }));

            return $"{{\"verdict\":\"{result.Verdict}\",\"issues\":[{issues}],\"verified\":[],\"recommendation\":\"{Escape(result.Summary)}\"}}";
        }

        private static string Escape(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }

    [Fact]
    public void BuildComposerTools_IncludesReviewGoal()
    {
        var tools = _composer.BuildComposerTools();
        var names = tools.Select(t => t.Name).ToList();
        Assert.Contains("review_goal", names);
    }

    [Fact]
    public async Task ReviewGoal_NonDraftGoal_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            goalReviewService: new StubGoalReviewService(new ReviewResult("Approved", "No issues found", "Goal looks good")));

        await composer.CreateGoalAsync("review-nondraft", "Test goal");
        await composer.ApproveGoalAsync("review-nondraft"); // Draft → Pending

        var result = await composer.ReviewGoalAsync("review-nondraft");

        Assert.Contains("❌", result);
        Assert.Contains("Draft", result);
    }

    [Fact]
    public async Task ReviewGoal_AlreadyPending_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            goalReviewService: new StubGoalReviewService(new ReviewResult("Approved", "No issues found", "Goal looks good")));

        await composer.CreateGoalAsync("review-pending", "Test goal");
        var goal = await _store.GetGoalAsync("review-pending", ct);
        Assert.NotNull(goal);
        goal!.ReviewStatus = ReviewStatus.Pending;
        await _store.UpdateGoalAsync(goal, ct);

        var result = await composer.ReviewGoalAsync("review-pending");

        Assert.Contains("❌", result);
        Assert.Contains("already in progress", result);
    }

    [Fact]
    public async Task ReviewGoal_Approved_ReturnsSuccess()
    {
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            goalReviewService: new StubGoalReviewService(new ReviewResult("Approved", "No issues found", "Goal looks good")));

        await composer.CreateGoalAsync("review-approved-goal", "Test goal");

        var result = await composer.ReviewGoalAsync("review-approved-goal");

        Assert.Contains("✅", result);
        Assert.Contains("approved", result);
    }

    [Fact]
    public async Task ReviewGoal_NeedsChanges_ReturnsIssuesAndSummary()
    {
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            goalReviewService: new StubGoalReviewService(new ReviewResult("NeedsChanges", "Missing acceptance criteria", "Add clear acceptance criteria")));

        await composer.CreateGoalAsync("review-needs-changes-goal", "Test goal");

        var result = await composer.ReviewGoalAsync("review-needs-changes-goal");

        Assert.Contains("❌", result);
        Assert.Contains("Missing acceptance criteria", result);
        Assert.Contains("Add clear acceptance criteria", result);
        Assert.Contains("review-", result);
    }

    [Fact]
    public async Task ReviewGoal_ServiceNull_ReturnsError()
    {
        await _composer.CreateGoalAsync("review-no-service", "Test goal");

        var result = await _composer.ReviewGoalAsync("review-no-service");

        Assert.Contains("❌", result);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task UpdateGoal_Description_ResetsReviewStatus()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("review-reset-desc", "Original desc");
        var goal = await _store.GetGoalAsync("review-reset-desc", ct);
        Assert.NotNull(goal);
        goal!.ReviewStatus = ReviewStatus.Approved;
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.UpdateGoalAsync("review-reset-desc", "description", "Updated desc");

        Assert.Contains("✅", result);
        var updated = await _store.GetGoalAsync("review-reset-desc", ct);
        Assert.NotNull(updated);
        Assert.Equal(ReviewStatus.None, updated!.ReviewStatus);
    }

    // ── get_goal — Release field ──

    [Fact]
    public async Task GetGoal_ReleasePresent_IncludesReleaseLine()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("release-present", "Goal with release", repositories: "repo-a");
        await _store.CreateReleaseAsync(new Release { Id = "v2.0.0", Tag = "v2.0.0" }, ct);
        var goal = await _store.GetGoalAsync("release-present", ct);
        Assert.NotNull(goal);
        goal!.ReleaseId = "v2.0.0";
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.GetGoalAsync("release-present");

        Assert.Contains("- **Release:** v2.0.0", result);
    }

    [Fact]
    public async Task GetGoal_ReleaseAbsent_OmitsReleaseLine()
    {
        await _composer.CreateGoalAsync("release-absent", "Goal without release");

        var result = await _composer.GetGoalAsync("release-absent");

        Assert.DoesNotContain("- **Release:**", result);
        Assert.DoesNotContain("Release:", result);
    }

    // ── get_goal — Depends On field ──

    [Fact]
    public async Task GetGoal_DependsOnPresent_IncludesDependsOnLine()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("deps-present", "Goal with depends_on", depends_on: "goal-alpha, goal-beta");
        var goal = await _store.GetGoalAsync("deps-present", ct);
        Assert.NotNull(goal);
        Assert.Equal(2, goal!.DependsOn.Count);

        var result = await _composer.GetGoalAsync("deps-present");

        Assert.Contains("- **Depends On:** goal-alpha, goal-beta", result);
    }

    [Fact]
    public async Task GetGoal_DependsOnAbsent_OmitsDependsOnLine()
    {
        await _composer.CreateGoalAsync("deps-absent", "Goal without depends_on");

        var result = await _composer.GetGoalAsync("deps-absent");

        Assert.DoesNotContain("- **Depends On:**", result);
        Assert.DoesNotContain("Depends On:", result);
    }

    // ── get_goal — Documents field (unchanged) ──

    [Fact]
    public async Task GetGoal_DocumentsPresent_IncludesDocumentsLineVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("docs-present", "Goal with documents", documents: "doc-1, doc-2");
        var goal = await _store.GetGoalAsync("docs-present", ct);
        Assert.NotNull(goal);
        Assert.Equal(2, goal!.Documents.Count);

        var result = await _composer.GetGoalAsync("docs-present");

        Assert.Contains("- **Documents:** doc-1, doc-2", result);
    }

    [Fact]
    public async Task GetGoal_DocumentsAbsent_OmitsDocumentsLine()
    {
        await _composer.CreateGoalAsync("docs-absent", "Goal without documents");

        var result = await _composer.GetGoalAsync("docs-absent");

        Assert.DoesNotContain("- **Documents:**", result);
    }

    // ── get_goal — Document Links with KnowledgeGraph ──

    [Fact]
    public async Task GetGoal_DocumentLinks_WithLinks_IncludesCompactLinkLine()
    {
        var ct = TestContext.Current.CancellationToken;

        var knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            knowledgeGraph: knowledgeGraph);

        // Create two documents in the graph
        await knowledgeGraph.CreateDocumentAsync("doc-a", "Doc A", CopilotHive.Knowledge.DocumentType.Idea, "Content A", topic: "doc", ct: ct);
        await knowledgeGraph.CreateDocumentAsync("doc-b", "Doc B", CopilotHive.Knowledge.DocumentType.Idea, "Content B", topic: "doc", ct: ct);

        // doc-a has an outgoing link to doc-b
        knowledgeGraph.AddLink("doc-a", new CopilotHive.Knowledge.DocumentLink("doc-b", CopilotHive.Knowledge.LinkType.Related));
        // doc-b has an outgoing link to doc-a
        knowledgeGraph.AddLink("doc-b", new CopilotHive.Knowledge.DocumentLink("doc-a", CopilotHive.Knowledge.LinkType.DependsOn));

        // Create a goal that references both documents
        await composer.CreateGoalAsync("link-goal", "Goal with linked docs", documents: "doc-a, doc-b");
        var goal = await _store.GetGoalAsync("link-goal", ct);
        Assert.NotNull(goal);

        var result = await composer.GetGoalAsync("link-goal");

        // Document Links line should be present
        Assert.Contains("- **Document Links:**", result);
        // doc-a: 1 outgoing (→doc-b), 1 incoming (←doc-b DependsOn)
        Assert.Contains("doc-a (in:1 out:1)", result);
        // doc-b: 1 outgoing (→doc-a DependsOn), 1 incoming (←doc-a Related)
        Assert.Contains("doc-b (in:1 out:1)", result);
    }

    [Fact]
    public async Task GetGoal_DocumentLinks_NullGraph_OmitsLinkLineAndNoThrow()
    {
        // _composer is constructed without a knowledgeGraph (null by default)
        await _composer.CreateGoalAsync("null-graph-goal", "Goal with docs but null graph", documents: "doc-x");

        // Should not throw and should not contain Document Links
        var result = await _composer.GetGoalAsync("null-graph-goal");

        Assert.DoesNotContain("- **Document Links:**", result);
        Assert.DoesNotContain("Document Links:", result);
        // Documents line still present
        Assert.Contains("- **Documents:** doc-x", result);
    }

    [Fact]
    public async Task GetGoal_DocumentLinks_MissingDoc_SkipsWithoutThrowing()
    {
        var ct = TestContext.Current.CancellationToken;

        var knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            knowledgeGraph: knowledgeGraph);

        // Only create doc-existing in the graph; doc-missing does not exist as a document.
        // doc-existing has an outgoing link targeting "doc-missing" — this populates the
        // reverse index so GetIncomingLinks("doc-missing") returns a non-empty list,
        // even though "doc-missing" is not a real document.
        await knowledgeGraph.CreateDocumentAsync("doc-existing", "Existing", CopilotHive.Knowledge.DocumentType.Idea, "Content", topic: "doc", ct: ct);
        knowledgeGraph.AddLink("doc-existing", new CopilotHive.Knowledge.DocumentLink("doc-missing", CopilotHive.Knowledge.LinkType.Related));

        // Verify the edge case: doc-missing has incoming links (reverse index) but no outgoing
        Assert.NotEmpty(knowledgeGraph.GetIncomingLinks("doc-missing"));
        Assert.Empty(knowledgeGraph.GetOutgoingLinks("doc-missing"));

        // Goal references both an existing doc and a non-existing doc
        await composer.CreateGoalAsync("missing-doc-goal", "Goal with missing doc ref", documents: "doc-existing, doc-missing");
        var goal = await _store.GetGoalAsync("missing-doc-goal", ct);
        Assert.NotNull(goal);

        // Should not throw; doc-missing should be silently skipped entirely —
        // NOT shown with "(in:1 out:0)" even though it has incoming links.
        var result = await composer.GetGoalAsync("missing-doc-goal");

        Assert.Contains("- **Document Links:**", result);
        // doc-existing has 1 outgoing (→doc-missing), 0 incoming
        Assert.Contains("doc-existing (in:0 out:1)", result);
        // doc-missing should not appear in the Document Links line at all
        Assert.DoesNotContain("doc-missing (", result);
    }

    [Fact]
    public async Task GetGoal_DocumentLinks_MissingDocWithIncomingForwardLink_SkippedEntirely()
    {
        var ct = TestContext.Current.CancellationToken;

        var knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            knowledgeGraph: knowledgeGraph);

        // Create "doc-a" (a real document) with an outgoing link targeting "missing-doc"
        // (a document id that is NOT created in the graph — just a link target string).
        // This populates the reverse index so GetIncomingLinks("missing-doc") returns
        // a non-empty list, even though "missing-doc" does not exist as a document.
        await knowledgeGraph.CreateDocumentAsync("doc-a", "Doc A", CopilotHive.Knowledge.DocumentType.Idea, "Content A", topic: "doc", ct: ct);
        knowledgeGraph.AddLink("doc-a", new CopilotHive.Knowledge.DocumentLink("missing-doc", CopilotHive.Knowledge.LinkType.Related));

        // Sanity check: GetIncomingLinks("missing-doc") returns non-empty because the
        // reverse index has an entry for "missing-doc" (doc-a links to it).
        var incomingForMissing = knowledgeGraph.GetIncomingLinks("missing-doc");
        Assert.NotEmpty(incomingForMissing);
        // And GetOutgoingLinks("missing-doc") returns empty because the doc doesn't exist.
        var outgoingForMissing = knowledgeGraph.GetOutgoingLinks("missing-doc");
        Assert.Empty(outgoingForMissing);

        // Goal references ONLY the missing doc
        await composer.CreateGoalAsync("missing-incoming-goal", "Goal referencing missing doc with incoming link", documents: "missing-doc");
        var goal = await _store.GetGoalAsync("missing-incoming-goal", ct);
        Assert.NotNull(goal);

        // The missing doc must be skipped entirely — NO Document Links line at all,
        // not even "(in:1 out:0)".
        var result = await composer.GetGoalAsync("missing-incoming-goal");

        Assert.DoesNotContain("- **Document Links:**", result);
        Assert.DoesNotContain("missing-doc (", result);
    }

    [Fact]
    public async Task GetGoal_DocumentLinks_MissingDocWithIncomingLink_MixedWithExistingDoc()
    {
        var ct = TestContext.Current.CancellationToken;

        var knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            knowledgeGraph: knowledgeGraph);

        // Create "doc-a" with an outgoing link to "missing-doc" (not created in the graph).
        // The reverse index now has an entry for "missing-doc" pointing back to doc-a.
        await knowledgeGraph.CreateDocumentAsync("doc-a", "Doc A", CopilotHive.Knowledge.DocumentType.Idea, "Content A", topic: "doc", ct: ct);
        knowledgeGraph.AddLink("doc-a", new CopilotHive.Knowledge.DocumentLink("missing-doc", CopilotHive.Knowledge.LinkType.Related));

        // Verify the edge case setup: missing-doc has incoming links via the reverse index
        Assert.NotEmpty(knowledgeGraph.GetIncomingLinks("missing-doc"));

        // Goal references BOTH the existing doc and the missing doc
        await composer.CreateGoalAsync("mixed-missing-goal", "Goal with existing and missing doc", documents: "doc-a, missing-doc");
        var goal = await _store.GetGoalAsync("mixed-missing-goal", ct);
        Assert.NotNull(goal);

        var result = await composer.GetGoalAsync("mixed-missing-goal");

        // Document Links line should be present (doc-a has links)
        Assert.Contains("- **Document Links:**", result);
        // doc-a: 0 incoming, 1 outgoing (→missing-doc)
        Assert.Contains("doc-a (in:0 out:1)", result);
        // missing-doc must NOT appear in the Document Links line — skipped entirely,
        // even though GetIncomingLinks("missing-doc") is non-empty.
        Assert.DoesNotContain("missing-doc (", result);
    }

    [Fact]
    public async Task GetGoal_DocumentLinks_NoLinks_OmitsLinkLine()
    {
        var ct = TestContext.Current.CancellationToken;

        var knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            knowledgeGraph: knowledgeGraph);

        // Create documents with NO links at all
        await knowledgeGraph.CreateDocumentAsync("doc-nolink-1", "No Link 1", CopilotHive.Knowledge.DocumentType.Idea, "Content", topic: "doc", ct: ct);
        await knowledgeGraph.CreateDocumentAsync("doc-nolink-2", "No Link 2", CopilotHive.Knowledge.DocumentType.Idea, "Content", topic: "doc", ct: ct);

        await composer.CreateGoalAsync("no-links-goal", "Goal with unlinked docs", documents: "doc-nolink-1, doc-nolink-2");
        var goal = await _store.GetGoalAsync("no-links-goal", ct);
        Assert.NotNull(goal);

        var result = await composer.GetGoalAsync("no-links-goal");

        // No Document Links line should appear since no doc has any links
        Assert.DoesNotContain("- **Document Links:**", result);
        // Documents line should still be present
        Assert.Contains("- **Documents:** doc-nolink-1, doc-nolink-2", result);
    }

    // ── get_goal — plain goal regression ──

    [Fact]
    public async Task GetGoal_PlainGoal_OmitsAllNewLinesAndPreservesExistingFields()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("plain-goal", "A plain goal with no extras");
        var goal = await _store.GetGoalAsync("plain-goal", ct);
        Assert.NotNull(goal);
        goal!.Status = GoalStatus.Failed;
        goal.FailureReason = "Something went wrong";
        goal.TotalDurationSeconds = 3600;
        goal.Notes = ["This is a note"];
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.GetGoalAsync("plain-goal");

        // New conditional lines must be absent
        Assert.DoesNotContain("- **Release:**", result);
        Assert.DoesNotContain("- **Depends On:**", result);
        Assert.DoesNotContain("- **Documents:**", result);
        Assert.DoesNotContain("- **Document Links:**", result);

        // Existing fields must be present
        Assert.Contains("- **Status:** Failed", result);
        Assert.Contains("- **Review Status:**", result);
        Assert.Contains("- **Priority:**", result);
        Assert.Contains("- **Created:**", result);
        Assert.Contains("- **Repositories:** (none)", result);
        Assert.Contains("- **Description:** A plain goal with no extras", result);
        Assert.Contains("- **Failure:** Something went wrong", result);
        Assert.Contains("- **Duration:**", result);
        Assert.Contains("### Notes", result);
        Assert.Contains("- This is a note", result);
    }

    // ── get_goal — ordering of new lines ──

    [Fact]
    public async Task GetGoal_Ordering_NewLinesAppearInCorrectOrderBeforeFailure()
    {
        var ct = TestContext.Current.CancellationToken;

        var knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            knowledgeGraph: knowledgeGraph);

        // Create documents and links so Document Links line appears
        await knowledgeGraph.CreateDocumentAsync("order-doc-1", "Order Doc 1", CopilotHive.Knowledge.DocumentType.Idea, "Content", topic: "doc", ct: ct);
        await knowledgeGraph.CreateDocumentAsync("order-doc-2", "Order Doc 2", CopilotHive.Knowledge.DocumentType.Idea, "Content", topic: "doc", ct: ct);
        knowledgeGraph.AddLink("order-doc-1", new CopilotHive.Knowledge.DocumentLink("order-doc-2", CopilotHive.Knowledge.LinkType.Related));

        // Create a goal with release, depends_on, and documents — all set
        await composer.CreateGoalAsync("order-goal", "Ordering test goal", repositories: "repo-a", depends_on: "order-dep-1, order-dep-2", documents: "order-doc-1, order-doc-2");
        await _store.CreateReleaseAsync(new Release { Id = "order-release", Tag = "order-release" }, ct);
        var goal = await _store.GetGoalAsync("order-goal", ct);
        Assert.NotNull(goal);
        goal!.ReleaseId = "order-release";
        goal.Status = GoalStatus.Failed;
        goal.FailureReason = "Ordered failure";
        await _store.UpdateGoalAsync(goal, ct);

        var result = await composer.GetGoalAsync("order-goal");

        // All lines should be present
        var releaseIdx = result.IndexOf("- **Release:** order-release");
        var dependsOnIdx = result.IndexOf("- **Depends On:** order-dep-1, order-dep-2");
        var documentsIdx = result.IndexOf("- **Documents:** order-doc-1, order-doc-2");
        var documentLinksIdx = result.IndexOf("- **Document Links:**");
        var failureIdx = result.IndexOf("- **Failure:** Ordered failure");

        Assert.True(releaseIdx >= 0, "Release line not found");
        Assert.True(dependsOnIdx >= 0, "Depends On line not found");
        Assert.True(documentsIdx >= 0, "Documents line not found");
        Assert.True(documentLinksIdx >= 0, "Document Links line not found");
        Assert.True(failureIdx >= 0, "Failure line not found");

        // Assert correct ordering: Release < Depends On < Documents < Document Links < Failure
        Assert.True(releaseIdx < dependsOnIdx, $"Release ({releaseIdx}) should appear before Depends On ({dependsOnIdx})");
        Assert.True(dependsOnIdx < documentsIdx, $"Depends On ({dependsOnIdx}) should appear before Documents ({documentsIdx})");
        Assert.True(documentsIdx < documentLinksIdx, $"Documents ({documentsIdx}) should appear before Document Links ({documentLinksIdx})");
        Assert.True(documentLinksIdx < failureIdx, $"Document Links ({documentLinksIdx}) should appear before Failure ({failureIdx})");
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

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>([]);
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

// ── update_release tool tests ─────────────────────────────────────────────────

public sealed class UpdateReleaseComposerToolTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly Composer _composer;

    public UpdateReleaseComposerToolTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
        _composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath());
    }

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task UpdateRelease_UpdatesTag_ReturnsSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateReleaseAsync("v1.0.0", "v1.0.0");

        var result = await _composer.UpdateReleaseAsync("v1.0.0", "tag", "v1.0.1");

        Assert.Contains("✅", result);
        Assert.Contains("tag", result);

        var updated = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.Equal("v1.0.1", updated!.Tag);
    }

    [Fact]
    public async Task UpdateRelease_UpdatesNotes_ReturnsSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateReleaseAsync("v1.0.0", "v1.0.0");

        var result = await _composer.UpdateReleaseAsync("v1.0.0", "notes", "Initial release notes");

        Assert.Contains("✅", result);

        var updated = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.Equal("Initial release notes", updated!.Notes);
    }

    [Fact]
    public async Task UpdateRelease_UpdatesRepositories_ReturnsSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateReleaseAsync("v1.0.0", "v1.0.0");

        var result = await _composer.UpdateReleaseAsync("v1.0.0", "repositories", "repo-a, repo-b");

        Assert.Contains("✅", result);

        var updated = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.NotNull(updated);
        Assert.Equal(2, updated!.RepositoryNames.Count);
        Assert.Contains("repo-a", updated.RepositoryNames);
        Assert.Contains("repo-b", updated.RepositoryNames);
    }

    [Fact]
    public async Task UpdateRelease_ClearsRepositoriesWhenEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateReleaseAsync("v1.0.0", "v1.0.0", repositories: "repo-a");

        var result = await _composer.UpdateReleaseAsync("v1.0.0", "repositories", "");

        Assert.Contains("✅", result);

        var updated = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.NotNull(updated);
        Assert.Empty(updated!.RepositoryNames);
    }

    [Fact]
    public async Task UpdateRelease_ReleaseNotFound_ReturnsError()
    {
        var result = await _composer.UpdateReleaseAsync("nonexistent", "tag", "v1.0.1");

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task UpdateRelease_NonPlanningRelease_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        // Create as Planning then mark Released
        await _store.CreateReleaseAsync(new Release
        {
            Id = "v1.0.0",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Released,
            CreatedAt = DateTime.UtcNow,
        }, ct);

        var result = await _composer.UpdateReleaseAsync("v1.0.0", "tag", "v1.0.1");

        Assert.Contains("❌", result);
        Assert.Contains("Released", result);
    }

    [Fact]
    public async Task UpdateRelease_UnknownField_ReturnsError()
    {
        await _composer.CreateReleaseAsync("v1.0.0", "v1.0.0");

        var result = await _composer.UpdateReleaseAsync("v1.0.0", "invalid_field", "some-value");

        Assert.Contains("❌", result);
        Assert.Contains("Unknown field", result);
    }

    [Fact]
    public async Task UpdateRelease_EmptyTagValue_ReturnsError()
    {
        await _composer.CreateReleaseAsync("v1.0.0", "v1.0.0");

        var result = await _composer.UpdateReleaseAsync("v1.0.0", "tag", "");

        Assert.Contains("❌", result);
        Assert.Contains("empty", result);
    }

    [Fact]
    public async Task UpdateRelease_MissingId_ReturnsError()
    {
        var result = await _composer.UpdateReleaseAsync("", "tag", "v1.0.1");

        Assert.Contains("ERROR", result);
        Assert.Contains("id is required", result);
    }

    [Fact]
    public async Task BuildComposerTools_IncludesUpdateRelease()
    {
        var tools = _composer.BuildComposerTools();
        Assert.Contains(tools, t => t.Name == "update_release");
    }

    // ── get_release ──

    [Fact]
    public void BuildComposerTools_IncludesGetRelease()
    {
        var tools = _composer.BuildComposerTools();
        var names = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains("get_release", names);
    }

    [Fact]
    public void SystemPrompt_MentionsGetReleaseCapability()
    {
        var prompt = _composer.GetSystemPrompt();
        Assert.Contains("get_release", prompt);
    }

    [Fact]
    public async Task GetRelease_NonexistentId_ReturnsNotFound()
    {
        var result = await _composer.GetReleaseAsync("nonexistent");

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task GetRelease_NoGoals_ReturnsZeroGoals()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateReleaseAsync("v9.9.9", "v9.9.9", "Test notes");

        var result = await _composer.GetReleaseAsync("v9.9.9");

        Assert.Contains("v9.9.9", result);
        Assert.Contains("v9.9.9", result); // Tag
        Assert.Contains("Planning", result); // Status
        Assert.Contains("None", result); // ExecutionState
        Assert.Contains("CreatedAt", result);
        Assert.Contains("Test notes", result); // Notes
        Assert.Contains("0 goal(s) attached", result);
    }

    [Fact]
    public async Task GetRelease_WithGoals_ReturnsFullRecord()
    {
        var ct = TestContext.Current.CancellationToken;

        var releaseId = "v2.0.0";
        await _composer.CreateReleaseAsync(releaseId, "v2.0.0", "Release with goals", "repo-a, repo-b");

        var goal1 = new Goal
        {
            Id = "goal-for-release-1",
            Description = "First goal for the release",
            Status = GoalStatus.Pending,
            Priority = GoalPriority.High,
            Scope = GoalScope.Feature,
        };
        goal1.ReleaseId = releaseId;
        await _store.CreateGoalAsync(goal1, ct);

        var goal2 = new Goal
        {
            Id = "goal-for-release-2",
            Description = "Second goal for the release",
            Status = GoalStatus.Completed,
            Priority = GoalPriority.Normal,
            Scope = GoalScope.Patch,
        };
        goal2.ReleaseId = releaseId;
        await _store.CreateGoalAsync(goal2, ct);

        var result = await _composer.GetReleaseAsync(releaseId);

        // Release fields
        Assert.Contains(releaseId, result);
        Assert.Contains("v2.0.0", result); // Tag
        Assert.Contains("Planning", result); // Status
        Assert.Contains("None", result); // ExecutionState
        Assert.Contains("CreatedAt", result);
        Assert.Contains("(not released)", result); // ReleasedAt null for Planning release
        Assert.Contains("repo-a", result); // RepositoryNames
        Assert.Contains("repo-b", result);
        Assert.Contains("Release with goals", result); // Notes

        // Goal 1 details
        Assert.Contains("goal-for-release-1", result);
        Assert.Contains("Pending", result);
        Assert.Contains("High", result);
        Assert.Contains("Feature", result);

        // Goal 2 details
        Assert.Contains("goal-for-release-2", result);
        Assert.Contains("Completed", result);
        Assert.Contains("Normal", result);
        Assert.Contains("Patch", result);

        // Goals count
        Assert.Contains("2 goal(s) attached", result);
    }
}

/// <summary>
/// Tests for the Composer's config repo tools (list_config_files, read_config_file,
/// update_agents_md, edit_agents_md, commit_config_changes).
/// </summary>
public sealed class ComposerConfigRepoToolTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly string _configRepoDir;
    private readonly ConfigRepoManager _configRepo;
    private readonly Composer _composerWithConfigRepo;
    private readonly Composer _composerWithoutConfigRepo;

    public ComposerConfigRepoToolTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);

        // Create a real temporary directory to act as the config repo
        _configRepoDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_configRepoDir);
        Directory.CreateDirectory(Path.Combine(_configRepoDir, "agents"));

        _configRepo = new ConfigRepoManager("https://example.com/config.git", _configRepoDir);

        _composerWithConfigRepo = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            configRepo: _configRepo);

        _composerWithoutConfigRepo = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try { Directory.Delete(_configRepoDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── list_config_files ──

    [Fact]
    public async Task ListConfigFiles_NoConfigRepo_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithoutConfigRepo.ListConfigFilesAsync(cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ListConfigFiles_RootDir_ReturnsAllFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_configRepoDir, "hive-config.yaml"), "config: true", ct);
        await File.WriteAllTextAsync(Path.Combine(_configRepoDir, "agents", "coder.agents.md"), "# Coder", ct);

        var result = await _composerWithConfigRepo.ListConfigFilesAsync(cancellationToken: ct);

        Assert.Contains("hive-config.yaml", result);
        Assert.Contains("agents/coder.agents.md", result);
    }

    [Fact]
    public async Task ListConfigFiles_Subdirectory_FiltersToSubdir()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_configRepoDir, "hive-config.yaml"), "config: true", ct);
        await File.WriteAllTextAsync(Path.Combine(_configRepoDir, "agents", "coder.agents.md"), "# Coder", ct);

        var result = await _composerWithConfigRepo.ListConfigFilesAsync("agents", cancellationToken: ct);

        Assert.Contains("agents/coder.agents.md", result);
        Assert.DoesNotContain("hive-config.yaml", result);
    }

    [Fact]
    public async Task ListConfigFiles_PathTraversal_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.ListConfigFilesAsync("../../etc", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("outside the config repo", result);
    }

    [Fact]
    public async Task ListConfigFiles_NonExistentSubdir_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.ListConfigFilesAsync("nonexistent-dir", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ListConfigFiles_EmptyDir_ReturnsNoFilesMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        // Remove all files from agents dir
        foreach (var f in Directory.GetFiles(_configRepoDir, "*", SearchOption.AllDirectories))
            File.Delete(f);

        var result = await _composerWithConfigRepo.ListConfigFilesAsync(cancellationToken: ct);

        Assert.Contains("no files found", result);
    }

    // ── read_config_file ──

    [Fact]
    public async Task ReadConfigFile_NoConfigRepo_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithoutConfigRepo.ReadConfigFileAsync("agents/coder.agents.md", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ReadConfigFile_ValidFile_ReturnsContentWithLineNumbers()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = "Line one\nLine two\nLine three";
        await File.WriteAllTextAsync(Path.Combine(_configRepoDir, "agents", "coder.agents.md"), content, ct);

        var result = await _composerWithConfigRepo.ReadConfigFileAsync("agents/coder.agents.md", cancellationToken: ct);

        Assert.Contains("1: Line one", result);
        Assert.Contains("2: Line two", result);
        Assert.Contains("3: Line three", result);
    }

    [Fact]
    public async Task ReadConfigFile_WithOffset_StartsAtCorrectLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5";
        await File.WriteAllTextAsync(Path.Combine(_configRepoDir, "test.txt"), content, ct);

        var result = await _composerWithConfigRepo.ReadConfigFileAsync("test.txt", offset: 3, cancellationToken: ct);

        Assert.DoesNotContain("1: Line 1", result);
        Assert.DoesNotContain("2: Line 2", result);
        Assert.Contains("3: Line 3", result);
        Assert.Contains("4: Line 4", result);
        Assert.Contains("5: Line 5", result);
    }

    [Fact]
    public async Task ReadConfigFile_WithLimit_ReturnsOnlyRequestedLines()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"Line {i}"));
        await File.WriteAllTextAsync(Path.Combine(_configRepoDir, "many-lines.txt"), content, ct);

        var result = await _composerWithConfigRepo.ReadConfigFileAsync("many-lines.txt", limit: 3, cancellationToken: ct);

        Assert.Contains("1: Line 1", result);
        Assert.Contains("2: Line 2", result);
        Assert.Contains("3: Line 3", result);
        Assert.DoesNotContain("4: Line 4", result);
        Assert.Contains("more lines", result);
    }

    [Fact]
    public async Task ReadConfigFile_PathTraversal_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.ReadConfigFileAsync("../../etc/passwd", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("outside the config repo", result);
    }

    [Fact]
    public async Task ReadConfigFile_NonExistentFile_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.ReadConfigFileAsync("nonexistent.txt", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ReadConfigFile_EmptyPath_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.ReadConfigFileAsync("", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("path is required", result);
    }

    [Fact]
    public async Task ReadConfigFile_OffsetBeyondEnd_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_configRepoDir, "short.txt"), "One\nTwo", ct);

        var result = await _composerWithConfigRepo.ReadConfigFileAsync("short.txt", offset: 100, cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("beyond end of file", result);
    }

    // ── update_agents_md ──

    [Fact]
    public async Task UpdateAgentsMd_NoConfigRepo_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithoutConfigRepo.UpdateAgentsMdAsync("Coder", "# Content", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task UpdateAgentsMd_ValidRole_WritesFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.UpdateAgentsMdAsync("Coder", "# Coder Instructions\nDo stuff.", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("coder.agents.md", result);

        var filePath = Path.Combine(_configRepoDir, "agents", "coder.agents.md");
        Assert.True(File.Exists(filePath));
        var written = await File.ReadAllTextAsync(filePath, ct);
        Assert.Equal("# Coder Instructions\nDo stuff.", written);
    }

    [Fact]
    public async Task UpdateAgentsMd_CaseInsensitiveRole_Accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.UpdateAgentsMdAsync("tester", "# Tester", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("tester.agents.md", result);
    }

    [Fact]
    public async Task UpdateAgentsMd_InvalidRole_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.UpdateAgentsMdAsync("InvalidRole", "content", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid role", result);
        Assert.Contains("Coder", result);
    }

    [Fact]
    public async Task UpdateAgentsMd_EmptyRole_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.UpdateAgentsMdAsync("", "content", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("role is required", result);
    }

    [Fact]
    public async Task UpdateAgentsMd_UnspecifiedRole_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.UpdateAgentsMdAsync("Unspecified", "content", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid role", result);
    }

    [Theory]
    [InlineData("Coder", "coder")]
    [InlineData("Tester", "tester")]
    [InlineData("Reviewer", "reviewer")]
    [InlineData("Improver", "improver")]
    [InlineData("Orchestrator", "orchestrator")]
    [InlineData("DocWriter", "docwriter")]
    [InlineData("MergeWorker", "mergeworker")]
    public async Task UpdateAgentsMd_AllValidRoles_WriteCorrectFile(string roleInput, string expectedFileName)
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.UpdateAgentsMdAsync(roleInput, $"# {roleInput}", cancellationToken: ct);

        Assert.Contains("✅", result);
        var filePath = Path.Combine(_configRepoDir, "agents", $"{expectedFileName}.agents.md");
        Assert.True(File.Exists(filePath));
    }

    // ── edit_agents_md ──

    [Fact]
    public async Task EditAgentsMd_NoConfigRepo_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithoutConfigRepo.EditAgentsMdAsync("Coder", "old", "new", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task EditAgentsMd_ExactMatch_ReplacesText()
    {
        var ct = TestContext.Current.CancellationToken;
        var filePath = Path.Combine(_configRepoDir, "agents", "coder.agents.md");
        await File.WriteAllTextAsync(filePath, "Line A\nLine B\nLine C", ct);

        var result = await _composerWithConfigRepo.EditAgentsMdAsync("Coder", "Line B", "Line B EDITED", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("coder.agents.md", result);

        var content = await File.ReadAllTextAsync(filePath, ct);
        Assert.Contains("Line B EDITED", content);
        Assert.DoesNotContain("Line B\n", content);
    }

    [Fact]
    public async Task EditAgentsMd_OldStringNotFound_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var filePath = Path.Combine(_configRepoDir, "agents", "tester.agents.md");
        await File.WriteAllTextAsync(filePath, "Some content here", ct);

        var result = await _composerWithConfigRepo.EditAgentsMdAsync("Tester", "does not exist in file", "replacement", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
        Assert.Contains("exact text", result);
    }

    [Fact]
    public async Task EditAgentsMd_FileDoesNotExist_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.EditAgentsMdAsync("Reviewer", "something", "else", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task EditAgentsMd_InvalidRole_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.EditAgentsMdAsync("NotARole", "old", "new", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid role", result);
    }

    [Fact]
    public async Task EditAgentsMd_EmptyRole_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.EditAgentsMdAsync("", "old", "new", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("role is required", result);
    }

    [Fact]
    public async Task EditAgentsMd_EmptyOldString_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.EditAgentsMdAsync("Coder", "", "new", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("old_string is required", result);
        Assert.Contains("must not be empty", result);
    }

    [Fact]
    public async Task EditAgentsMd_NullOldString_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.EditAgentsMdAsync("Coder", null!, "new", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("old_string is required", result);
        Assert.Contains("must not be empty", result);
    }

    // ── commit_config_changes ──

    [Fact]
    public async Task CommitConfigChanges_NoConfigRepo_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithoutConfigRepo.CommitConfigChangesAsync("test commit", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task CommitConfigChanges_EmptyMessage_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.CommitConfigChangesAsync("", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("message is required", result);
    }

    [Fact]
    public async Task CommitConfigChanges_WhitespaceMessage_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composerWithConfigRepo.CommitConfigChangesAsync("   ", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("message is required", result);
    }

    [Fact]
    public async Task CommitConfigChanges_GitFailure_ReturnsErrorMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        // The config repo dir has no .git — git commit will fail
        var result = await _composerWithConfigRepo.CommitConfigChangesAsync("test commit", cancellationToken: ct);

        // Should return an error message, not throw
        Assert.Contains("❌", result);
        Assert.Contains("Failed to commit", result);
    }

    // ── BuildComposerTools — config repo tools registration ──

    [Fact]
    public void BuildComposerTools_WithConfigRepo_IncludesConfigRepoTools()
    {
        var tools = _composerWithConfigRepo.BuildComposerTools();
        var names = tools.OfType<AIFunction>().Select(t => t.Name).ToHashSet();

        Assert.Contains("list_config_files", names);
        Assert.Contains("read_config_file", names);
        Assert.Contains("update_agents_md", names);
        Assert.Contains("edit_agents_md", names);
        Assert.Contains("commit_config_changes", names);
    }

    [Fact]
    public void BuildComposerTools_WithoutConfigRepo_ExcludesConfigRepoTools()
    {
        var tools = _composerWithoutConfigRepo.BuildComposerTools();
        var names = tools.OfType<AIFunction>().Select(t => t.Name).ToHashSet();

        Assert.DoesNotContain("list_config_files", names);
        Assert.DoesNotContain("read_config_file", names);
        Assert.DoesNotContain("update_agents_md", names);
        Assert.DoesNotContain("edit_agents_md", names);
        Assert.DoesNotContain("commit_config_changes", names);
    }

    // ── System prompt ──

    [Fact]
    public void SystemPrompt_WithConfigRepo_IncludesConfigRepoSection()
    {
        var prompt = _composerWithConfigRepo.GetSystemPrompt();

        Assert.Contains("Config Repository", prompt);
        Assert.Contains("list_config_files", prompt);
        Assert.Contains("read_config_file", prompt);
        Assert.Contains("update_agents_md", prompt);
        Assert.Contains("edit_agents_md", prompt);
        Assert.Contains("commit_config_changes", prompt);
    }

    [Fact]
    public void SystemPrompt_WithoutConfigRepo_DoesNotIncludeConfigRepoSection()
    {
        var prompt = _composerWithoutConfigRepo.GetSystemPrompt();

        Assert.DoesNotContain("Config Repository", prompt);
        Assert.DoesNotContain("list_config_files", prompt);
        Assert.DoesNotContain("update_agents_md", prompt);
    }
}

/// <summary>
/// Integration tests for config repo tools that require a real git repository.
/// </summary>
public sealed class ComposerConfigRepoGitIntegrationTests : IDisposable
{
    private readonly string _configRepoDir;
    private readonly string _remoteRepoDir;
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;

    public ComposerConfigRepoGitIntegrationTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);

        // Create a temp directory structure for integration tests
        var baseDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _configRepoDir = Path.Combine(baseDir, "config-repo");
        _remoteRepoDir = Path.Combine(baseDir, "remote");

        Directory.CreateDirectory(_configRepoDir);
        Directory.CreateDirectory(_remoteRepoDir);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        // Clean up temp directories
        var baseDir = Path.GetDirectoryName(_configRepoDir);
        if (baseDir is not null)
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private void InitializeGitRepo(string path, string initialCommitMessage = "Initial commit")
    {
        // Initialize a git repo with a basic commit
        RunGit(path, "init");
        RunGit(path, "config user.email test@example.com");
        RunGit(path, "config user.name Test");
        RunGit(path, "checkout -b main");
        File.WriteAllText(Path.Combine(path, "README.md"), "# Test repo");
        RunGit(path, "add .");
        RunGit(path, $"commit -m \"{initialCommitMessage}\"");
    }

    private void RunGit(string workingDir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit();
    }

    private ConfigRepoManager CreateConfigRepoManager()
    {
        // Create a bare repo as the "remote" (bare repos accept pushes)
        RunGit(_remoteRepoDir, "init --bare");
        RunGit(_remoteRepoDir, "symbolic-ref HEAD refs/heads/main");

        // Create a working repo to make the initial commit, then push to bare
        var tempWorkingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempWorkingDir);
        RunGit(tempWorkingDir, "init");
        RunGit(tempWorkingDir, "config user.email test@example.com");
        RunGit(tempWorkingDir, "config user.name Test");
        RunGit(tempWorkingDir, "checkout -b main");
        File.WriteAllText(Path.Combine(tempWorkingDir, "README.md"), "# Test repo");
        RunGit(tempWorkingDir, "add .");
        RunGit(tempWorkingDir, "commit -m \"Initial commit\"");
        RunGit(tempWorkingDir, $"remote add origin \"{_remoteRepoDir}\"");
        RunGit(tempWorkingDir, "push -u origin main");
        try { Directory.Delete(tempWorkingDir, recursive: true); } catch { /* best-effort */ }

        // Clone the bare repo into the config repo dir
        RunGit(Path.GetDirectoryName(_configRepoDir)!, $"clone \"{_remoteRepoDir}\" \"{_configRepoDir}\"");
        RunGit(_configRepoDir, "config user.email test@example.com");
        RunGit(_configRepoDir, "config user.name Test");

        // Create agents directory and add a file
        Directory.CreateDirectory(Path.Combine(_configRepoDir, "agents"));
        File.WriteAllText(Path.Combine(_configRepoDir, "agents", "coder.agents.md"), "# Coder instructions\nOriginal content.");
        RunGit(_configRepoDir, "add .");
        RunGit(_configRepoDir, "commit -m \"Add initial agents\"");
        RunGit(_configRepoDir, "push");

        return new ConfigRepoManager(_remoteRepoDir, _configRepoDir);
    }

    [Fact]
    public async Task CommitConfigChanges_WithRealGitRepo_StagesCommitsAndPushes()
    {
        var ct = TestContext.Current.CancellationToken;
        var configRepo = CreateConfigRepoManager();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            configRepo: configRepo);

        // Modify a file using update_agents_md
        await composer.UpdateAgentsMdAsync("Coder", "# Coder instructions\nUpdated content.", ct);

        // Commit the changes
        var result = await composer.CommitConfigChangesAsync("Update coder instructions", ct);

        Assert.Contains("✅", result);
        Assert.Contains("committed and pushed", result);

        // Verify the commit exists in the local repo
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "log -1 --pretty=format:%s",
            WorkingDirectory = _configRepoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        Assert.Contains("Update coder instructions", output);
    }

    [Fact]
    public async Task EditAgentsMd_WithRealGitRepo_PerformsExactReplacement()
    {
        var ct = TestContext.Current.CancellationToken;
        var configRepo = CreateConfigRepoManager();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            configRepo: configRepo);

        // Edit using exact string replacement
        var result = await composer.EditAgentsMdAsync("Coder", "Original content.", "Modified content.", ct);

        Assert.Contains("✅", result);
        Assert.Contains("coder.agents.md", result);

        // Verify the file content was actually changed
        var filePath = Path.Combine(_configRepoDir, "agents", "coder.agents.md");
        var content = await File.ReadAllTextAsync(filePath, ct);
        Assert.Contains("Modified content.", content);
        Assert.DoesNotContain("Original content.", content);
    }

    [Fact]
    public async Task ListConfigFiles_WithRealGitRepo_ReturnsFilesFromSubdirectories()
    {
        var ct = TestContext.Current.CancellationToken;
        var configRepo = CreateConfigRepoManager();

        // Add nested subdirectory structure
        Directory.CreateDirectory(Path.Combine(_configRepoDir, "configs", "templates"));
        await File.WriteAllTextAsync(Path.Combine(_configRepoDir, "configs", "templates", "default.yaml"), "template: true", ct);
        RunGit(_configRepoDir, "add .");
        RunGit(_configRepoDir, "commit -m \"Add templates\"");

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            configRepo: configRepo);

        var result = await composer.ListConfigFilesAsync(cancellationToken: ct);

        Assert.Contains("agents/coder.agents.md", result);
        Assert.Contains("configs/templates/default.yaml", result);
    }

    [Fact]
    public async Task ReadConfigFile_WithPathTraversal_ReturnsAccessDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var configRepo = CreateConfigRepoManager();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            configRepo: configRepo);

        // Attempt to read a file outside the config repo using path traversal
        var result = await composer.ReadConfigFileAsync("../../../../etc/passwd", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("outside the config repo", result);
        Assert.Contains("Access denied", result);
    }

    [Fact]
    public async Task UpdateAgentsMd_InvalidRole_ReturnsErrorListingValidRoles()
    {
        var ct = TestContext.Current.CancellationToken;
        var configRepo = CreateConfigRepoManager();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            configRepo: configRepo);

        var result = await composer.UpdateAgentsMdAsync("NotAValidRole", "# Content", ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid role", result);
        // Verify all valid roles are listed
        Assert.Contains("Coder", result);
        Assert.Contains("Tester", result);
        Assert.Contains("Reviewer", result);
        Assert.Contains("Improver", result);
        Assert.Contains("Orchestrator", result);
        Assert.Contains("DocWriter", result);
        Assert.Contains("MergeWorker", result);
    }

    [Fact]
    public async Task EditAgentsMd_InvalidRole_ReturnsErrorListingValidRoles()
    {
        var ct = TestContext.Current.CancellationToken;
        var configRepo = CreateConfigRepoManager();

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            configRepo: configRepo);

        var result = await composer.EditAgentsMdAsync("FakeRole", "old", "new", ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid role", result);
        // Verify all valid roles are listed
        Assert.Contains("Coder", result);
        Assert.Contains("Tester", result);
    }

    [Fact]
    public async Task UpdateAgentsMd_CreatesAgentsDirectoryIfMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var configRepo = CreateConfigRepoManager();

        // Delete the agents directory
        Directory.Delete(Path.Combine(_configRepoDir, "agents"), recursive: true);
        Assert.False(Directory.Exists(Path.Combine(_configRepoDir, "agents")));

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            configRepo: configRepo);

        var result = await composer.UpdateAgentsMdAsync("Tester", "# Tester instructions", ct);

        Assert.Contains("✅", result);
        Assert.True(Directory.Exists(Path.Combine(_configRepoDir, "agents")));
        Assert.True(File.Exists(Path.Combine(_configRepoDir, "agents", "tester.agents.md")));
    }
}

/// <summary>
/// Tests for the Composer's knowledge graph tools.
/// Uses an in-memory KnowledgeGraph (no real config repo required for most tests).
/// </summary>
public sealed class ComposerKnowledgeToolTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly CopilotHive.Knowledge.KnowledgeGraph _knowledgeGraph;
    private readonly Composer _composer;

    // For tests that need a real config repo
    private readonly string _configRepoDir;
    private readonly string _remoteRepoDir;

    public ComposerKnowledgeToolTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);

        _knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        _composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            knowledgeGraph: _knowledgeGraph);

        // Set up temp dirs for integration tests
        var baseDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _configRepoDir = Path.Combine(baseDir, "config-repo");
        _remoteRepoDir = Path.Combine(baseDir, "remote");
        Directory.CreateDirectory(_configRepoDir);
        Directory.CreateDirectory(_remoteRepoDir);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        var baseDir = Path.GetDirectoryName(_configRepoDir);
        if (baseDir is not null)
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Helper: composer without knowledge graph ──

    private Composer ComposerWithoutKnowledge() =>
        new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath());

    // ── No knowledge graph configured ──

    [Fact]
    public async Task CreateDocument_NoKnowledgeGraph_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var composer = ComposerWithoutKnowledge();
        var result = await composer.CreateDocumentAsync("architecture", "brain", "Brain", "implementation", "Content", cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ReadDocument_NoKnowledgeGraph_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var composer = ComposerWithoutKnowledge();
        var result = await composer.ReadDocumentAsync("any-doc", cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task SearchKnowledge_NoKnowledgeGraph_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var composer = ComposerWithoutKnowledge();
        var result = await composer.SearchKnowledgeAsync("query", cancellationToken: ct);
        Assert.Contains("❌", result);
        Assert.Contains("not available", result);
    }

    // ── create_document ──

    [Fact]
    public async Task CreateDocument_ValidInput_ReturnsDocumentIdAndPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.CreateDocumentAsync(
            topic: "architecture",
            slug: "brain",
            title: "Brain Architecture",
            type: "implementation",
            content: "The brain is responsible for orchestration.",
            cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("architecture-brain", result);
        Assert.Contains("knowledge/architecture/brain.md", result);
    }

    [Fact]
    public async Task CreateDocument_WithSubtopic_BuildsCorrectId()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.CreateDocumentAsync(
            topic: "architecture",
            slug: "session-management",
            title: "Session Management",
            type: "feature",
            content: "Managing sessions...",
            subtopic: "distributed",
            cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("architecture-distributed-session-management", result);
    }

    [Fact]
    public async Task CreateDocument_InvalidType_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.CreateDocumentAsync(
            topic: "architecture",
            slug: "brain",
            title: "Brain",
            type: "bogustype",
            content: "Content",
            cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid type", result);
        Assert.Contains("implementation", result);
    }

    [Fact]
    public async Task CreateDocument_MissingTopic_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.CreateDocumentAsync(
            topic: "",
            slug: "brain",
            title: "Brain",
            type: "implementation",
            content: "Content",
            cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("topic is required", result);
    }

    [Fact]
    public async Task CreateDocument_MissingSlug_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.CreateDocumentAsync(
            topic: "architecture",
            slug: "",
            title: "Brain",
            type: "implementation",
            content: "Content",
            cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("slug is required", result);
    }

    [Fact]
    public async Task CreateDocument_DuplicateId_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "dup", "Dup Doc", "idea", "Content", cancellationToken: ct);
        var result = await _composer.CreateDocumentAsync("arch", "dup", "Dup Doc Again", "idea", "Content 2", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("already exists", result);
    }

    [Fact]
    public async Task CreateDocument_WithTags_StoresTags()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync(
            topic: "features",
            slug: "auth",
            title: "Auth Feature",
            type: "feature",
            content: "Auth content",
            tags: ["security", "api"],
            cancellationToken: ct);

        var doc = _knowledgeGraph.GetDocument("features-auth");
        Assert.NotNull(doc);
        Assert.Contains("security", doc!.Tags);
        Assert.Contains("api", doc.Tags);
    }

    [Fact]
    public async Task CreateDocument_WithLinksJson_ParsesLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        // First create target doc
        await _composer.CreateDocumentAsync("base", "core", "Core", "implementation", "Base content", cancellationToken: ct);

        var result = await _composer.CreateDocumentAsync(
            topic: "features",
            slug: "extension",
            title: "Extension",
            type: "feature",
            content: "Extension content",
            links: """[{"target":"base-core","type":"depends_on","description":"Needs base"}]""",
            cancellationToken: ct);

        Assert.Contains("✅", result);
        var doc = _knowledgeGraph.GetDocument("features-extension");
        Assert.NotNull(doc);
        Assert.Single(doc!.Links);
        Assert.Equal("base-core", doc.Links[0].TargetId);
        Assert.Equal(CopilotHive.Knowledge.LinkType.DependsOn, doc.Links[0].Type);
    }

    [Fact]
    public async Task CreateDocument_WithInvalidLinksJson_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.CreateDocumentAsync(
            topic: "features",
            slug: "bad-links",
            title: "Bad Links",
            type: "feature",
            content: "Content",
            links: "not valid json",
            cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid links JSON", result);
    }

    // ── read_document ──

    [Fact]
    public async Task ReadDocument_ExistingDocument_ReturnsFullContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "overview", "Architecture Overview", "implementation", "Detailed overview content here.", cancellationToken: ct);

        var result = await _composer.ReadDocumentAsync("arch-overview", cancellationToken: ct);

        Assert.Contains("Architecture Overview", result);
        Assert.Contains("arch-overview", result);
        Assert.Contains("implementation", result.ToLowerInvariant());
        Assert.Contains("Detailed overview content here.", result);
    }

    [Fact]
    public async Task ReadDocument_NonExistent_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.ReadDocumentAsync("nonexistent-doc", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ReadDocument_EmptyId_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.ReadDocumentAsync("", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("document_id is required", result);
    }

    [Fact]
    public async Task ReadDocument_WithLinks_ShowsLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "parent-doc", "Parent", "implementation", "Parent content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "child-doc", "Child", "feature", "Child content",
            links: """[{"target":"arch-parent-doc","type":"parent","description":"My parent"}]""",
            cancellationToken: ct);

        var result = await _composer.ReadDocumentAsync("arch-child-doc", cancellationToken: ct);

        Assert.Contains("arch-parent-doc", result);
        Assert.Contains("Parent", result);
    }

    // ── update_document ──

    [Fact]
    public async Task UpdateDocument_Title_UpdatesTitle()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("ideas", "my-idea", "Original Title", "idea", "Content", cancellationToken: ct);

        var result = await _composer.UpdateDocumentAsync("ideas-my-idea", title: "New Title", cancellationToken: ct);

        Assert.Contains("✅", result);
        var doc = _knowledgeGraph.GetDocument("ideas-my-idea");
        Assert.Equal("New Title", doc!.Title);
    }

    [Fact]
    public async Task UpdateDocument_Content_ReplacesContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("scratch", "notes", "Notes", "scratch", "Old content", cancellationToken: ct);

        var result = await _composer.UpdateDocumentAsync("scratch-notes", content: "New content here", cancellationToken: ct);

        Assert.Contains("✅", result);
        var doc = _knowledgeGraph.GetDocument("scratch-notes");
        Assert.Equal("New content here", doc!.Content);
    }

    [Fact]
    public async Task UpdateDocument_AppendContent_AppendsToExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("memory", "facts", "Facts", "memory", "Fact 1.", cancellationToken: ct);

        var result = await _composer.UpdateDocumentAsync("memory-facts", append_content: "Fact 2.", cancellationToken: ct);

        Assert.Contains("✅", result);
        var doc = _knowledgeGraph.GetDocument("memory-facts");
        Assert.Contains("Fact 1.", doc!.Content);
        Assert.Contains("Fact 2.", doc.Content);
    }

    [Fact]
    public async Task UpdateDocument_InvalidType_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "existing", "Existing", "implementation", "Content", cancellationToken: ct);

        var result = await _composer.UpdateDocumentAsync("arch-existing", type: "bogustype", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid type", result);
    }

    [Fact]
    public async Task UpdateDocument_InvalidStatus_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "existing2", "Existing2", "implementation", "Content", cancellationToken: ct);

        var result = await _composer.UpdateDocumentAsync("arch-existing2", status: "bogustatus", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid status", result);
    }

    [Fact]
    public async Task UpdateDocument_NonExistent_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.UpdateDocumentAsync("nonexistent-doc", title: "New Title", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task UpdateDocument_Status_ChangesStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "status-test", "Status Test", "implementation", "Content", cancellationToken: ct);

        var result = await _composer.UpdateDocumentAsync("arch-status-test", status: "active", cancellationToken: ct);

        Assert.Contains("✅", result);
        var doc = _knowledgeGraph.GetDocument("arch-status-test");
        Assert.Equal(CopilotHive.Knowledge.DocumentStatus.Active, doc!.Status);
    }

    // ── delete_document ──

    [Fact]
    public async Task DeleteDocument_ExistingDocument_Deletes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("scratch", "to-delete", "To Delete", "scratch", "Content", cancellationToken: ct);
        Assert.NotNull(_knowledgeGraph.GetDocument("scratch-to-delete"));

        var result = await _composer.DeleteDocumentAsync("scratch-to-delete", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("deleted", result);
        Assert.Null(_knowledgeGraph.GetDocument("scratch-to-delete"));
    }

    [Fact]
    public async Task DeleteDocument_NonExistent_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.DeleteDocumentAsync("nonexistent-doc", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task DeleteDocument_WithIncomingLinks_WarnsAboutDanglingLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "target", "Target", "implementation", "Content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "source", "Source", "feature", "Content",
            links: """[{"target":"arch-target","type":"depends_on"}]""",
            cancellationToken: ct);

        var result = await _composer.DeleteDocumentAsync("arch-target", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("⚠️", result);
        Assert.Contains("arch-source", result);
    }

    [Fact]
    public async Task DeleteDocument_NoIncomingLinks_NoWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("scratch", "lonely-doc", "Lonely", "scratch", "Content", cancellationToken: ct);

        var result = await _composer.DeleteDocumentAsync("scratch-lonely-doc", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.DoesNotContain("⚠️", result);
    }

    // ── search_knowledge ──

    [Fact]
    public async Task SearchKnowledge_MatchingQuery_ReturnsResults()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "search-target", "Search Target Document", "implementation", "The brain manages sessions.", cancellationToken: ct);

        var result = await _composer.SearchKnowledgeAsync("sessions", cancellationToken: ct);

        Assert.Contains("arch-search-target", result);
        Assert.Contains("Search Target Document", result);
    }

    [Fact]
    public async Task SearchKnowledge_NoMatch_ReturnsNoResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.SearchKnowledgeAsync("zzzyyyxxx", cancellationToken: ct);

        Assert.Contains("No knowledge documents found", result);
    }

    [Fact]
    public async Task SearchKnowledge_EmptyQuery_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.SearchKnowledgeAsync("", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("query is required", result);
    }

    [Fact]
    public async Task SearchKnowledge_FilterByTopic_FiltersResults()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "arch-doc", "Arch Doc", "implementation", "Brain content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("features", "feat-doc", "Feature Doc", "feature", "Brain content", cancellationToken: ct);

        var result = await _composer.SearchKnowledgeAsync("Brain", topic: "arch", cancellationToken: ct);

        Assert.Contains("arch-arch-doc", result);
        Assert.DoesNotContain("features-feat-doc", result);
    }

    [Fact]
    public async Task SearchKnowledge_FilterByType_FiltersResults()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("ideas", "my-idea2", "My Idea", "idea", "Some idea content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("memory", "my-mem", "My Memory", "memory", "Some idea content", cancellationToken: ct);

        var result = await _composer.SearchKnowledgeAsync("idea content", type: "idea", cancellationToken: ct);

        Assert.Contains("ideas-my-idea2", result);
        Assert.DoesNotContain("memory-my-mem", result);
    }

    [Fact]
    public async Task SearchKnowledge_LimitResults_CapsOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 1; i <= 5; i++)
            await _composer.CreateDocumentAsync("scratch", $"limit-test-{i}", $"Limit Test {i}", "scratch", "Limit test content", cancellationToken: ct);

        var result = await _composer.SearchKnowledgeAsync("Limit test", limit: 2, cancellationToken: ct);

        Assert.Contains("5 document(s) (showing 2)", result);
    }

    // ── link_document ──

    [Fact]
    public async Task LinkDocument_ValidLink_AddsLink()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "source-link", "Source", "implementation", "Source content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "target-link", "Target", "implementation", "Target content", cancellationToken: ct);

        var result = await _composer.LinkDocumentAsync(
            document_id: "arch-source-link",
            target_id: "arch-target-link",
            link_type: "related",
            cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("arch-source-link", result);
        Assert.Contains("arch-target-link", result);

        var doc = _knowledgeGraph.GetDocument("arch-source-link");
        Assert.Single(doc!.Links);
        Assert.Equal("arch-target-link", doc.Links[0].TargetId);
        Assert.Equal(CopilotHive.Knowledge.LinkType.Related, doc.Links[0].Type);
    }

    [Fact]
    public async Task LinkDocument_DependsOnLinkType_ParsesUnderscoreVariant()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "src2", "Src2", "implementation", "Content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "tgt2", "Tgt2", "implementation", "Content", cancellationToken: ct);

        var result = await _composer.LinkDocumentAsync("arch-src2", "arch-tgt2", "depends_on", cancellationToken: ct);

        Assert.Contains("✅", result);
        var doc = _knowledgeGraph.GetDocument("arch-src2");
        Assert.Equal(CopilotHive.Knowledge.LinkType.DependsOn, doc!.Links[0].Type);
    }

    [Fact]
    public async Task LinkDocument_InvalidLinkType_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "src3", "Src3", "implementation", "Content", cancellationToken: ct);

        var result = await _composer.LinkDocumentAsync("arch-src3", "arch-tgt3", "bogustype", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid link_type", result);
    }

    [Fact]
    public async Task LinkDocument_SourceNotFound_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.LinkDocumentAsync("nonexistent-src", "any-target", "related", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task LinkDocument_TargetNotFound_WarnsButSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "forward-src", "Forward Src", "implementation", "Content", cancellationToken: ct);

        var result = await _composer.LinkDocumentAsync("arch-forward-src", "future-doc", "related", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("⚠️", result);
        Assert.Contains("does not exist", result);
    }

    // ── unlink_document ──

    [Fact]
    public async Task UnlinkDocument_RemovesLink()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "unlink-src", "Unlink Src", "implementation", "Content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "unlink-tgt", "Unlink Tgt", "implementation", "Content", cancellationToken: ct);
        _knowledgeGraph.AddLink("arch-unlink-src", new CopilotHive.Knowledge.DocumentLink("arch-unlink-tgt", CopilotHive.Knowledge.LinkType.Related));

        var result = await _composer.UnlinkDocumentAsync("arch-unlink-src", "arch-unlink-tgt", "related", cancellationToken: ct);

        Assert.Contains("✅", result);
        var doc = _knowledgeGraph.GetDocument("arch-unlink-src");
        Assert.Empty(doc!.Links);
    }

    [Fact]
    public async Task UnlinkDocument_SourceNotFound_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.UnlinkDocumentAsync("nonexistent", "any-target", "related", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task UnlinkDocument_InvalidLinkType_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "ul-src", "UL Src", "implementation", "Content", cancellationToken: ct);

        var result = await _composer.UnlinkDocumentAsync("arch-ul-src", "any-target", "bogustype", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid link_type", result);
    }

    // ── list_documents ──

    [Fact]
    public async Task ListDocuments_All_ReturnsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "list-a", "List A", "implementation", "Content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "list-b", "List B", "feature", "Content", cancellationToken: ct);

        var result = await _composer.ListDocumentsAsync(cancellationToken: ct);

        Assert.Contains("arch-list-a", result);
        Assert.Contains("arch-list-b", result);
    }

    [Fact]
    public async Task ListDocuments_FilterByType_FiltersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "list-impl", "List Impl", "implementation", "Content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("ideas", "list-idea", "List Idea", "idea", "Content", cancellationToken: ct);

        var result = await _composer.ListDocumentsAsync(type: "idea", cancellationToken: ct);

        Assert.Contains("ideas-list-idea", result);
        Assert.DoesNotContain("arch-list-impl", result);
    }

    [Fact]
    public async Task ListDocuments_FilterByTopic_FiltersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "list-arch-doc", "Arch Doc", "implementation", "Content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("scratch", "list-scratch-doc", "Scratch Doc", "scratch", "Content", cancellationToken: ct);

        var result = await _composer.ListDocumentsAsync(topic: "arch", cancellationToken: ct);

        Assert.Contains("arch-list-arch-doc", result);
        Assert.DoesNotContain("scratch-list-scratch-doc", result);
    }

    [Fact]
    public async Task ListDocuments_NoMatch_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.ListDocumentsAsync(topic: "nonexistent-topic", cancellationToken: ct);

        Assert.Contains("No knowledge documents found", result);
    }

    [Fact]
    public async Task ListDocuments_LimitResults_CapsOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 1; i <= 5; i++)
            await _composer.CreateDocumentAsync("scratch", $"cap-doc-{i}", $"Cap Doc {i}", "scratch", "Content", cancellationToken: ct);

        var result = await _composer.ListDocumentsAsync(limit: 2, cancellationToken: ct);

        Assert.Contains("(showing 2)", result);
    }

    // ── traverse_graph ──

    [Fact]
    public async Task TraverseGraph_Outgoing_FollowsForwardLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "tg-root", "TG Root", "implementation", "Root", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "tg-child", "TG Child", "feature", "Child", cancellationToken: ct);
        _knowledgeGraph.AddLink("arch-tg-root",
            new CopilotHive.Knowledge.DocumentLink("arch-tg-child", CopilotHive.Knowledge.LinkType.Related));

        var result = await _composer.TraverseGraphAsync("arch-tg-root", depth: 1, direction: "outgoing", cancellationToken: ct);

        Assert.Contains("arch-tg-child", result);
    }

    [Fact]
    public async Task TraverseGraph_Incoming_FollowsReverseLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "tg-parent", "TG Parent", "implementation", "Parent", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "tg-dep", "TG Dep", "feature", "Dep", cancellationToken: ct);
        _knowledgeGraph.AddLink("arch-tg-dep",
            new CopilotHive.Knowledge.DocumentLink("arch-tg-parent", CopilotHive.Knowledge.LinkType.DependsOn));

        var result = await _composer.TraverseGraphAsync("arch-tg-parent", depth: 1, direction: "incoming", cancellationToken: ct);

        Assert.Contains("arch-tg-dep", result);
    }

    [Fact]
    public async Task TraverseGraph_DocumentNotFound_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _composer.TraverseGraphAsync("nonexistent-doc", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task TraverseGraph_InvalidDirection_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "tg-dir", "TG Dir", "implementation", "Content", cancellationToken: ct);

        var result = await _composer.TraverseGraphAsync("arch-tg-dir", direction: "sideways", cancellationToken: ct);

        Assert.Contains("❌", result);
        Assert.Contains("Invalid direction", result);
    }

    [Fact]
    public async Task TraverseGraph_NoLinks_ReturnsNoLinksMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "tg-isolated", "Isolated", "implementation", "Content", cancellationToken: ct);

        var result = await _composer.TraverseGraphAsync("arch-tg-isolated", cancellationToken: ct);

        Assert.Contains("No links found", result);
    }

    [Fact]
    public async Task TraverseGraph_DepthClamped_ToMax3()
    {
        var ct = TestContext.Current.CancellationToken;
        // Depth 10 should be clamped to 3 (not throw)
        await _composer.CreateDocumentAsync("arch", "tg-clamp", "TG Clamp", "implementation", "Content", cancellationToken: ct);

        var result = await _composer.TraverseGraphAsync("arch-tg-clamp", depth: 10, cancellationToken: ct);

        // Should succeed (no error)
        Assert.DoesNotContain("❌", result);
    }

    // ── BuildComposerTools includes knowledge tools when graph is available ──

    [Fact]
    public void BuildComposerTools_WithKnowledgeGraph_IncludesKnowledgeTools()
    {
        var tools = _composer.BuildComposerTools();
        var toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToList();

        Assert.Contains("create_document", toolNames);
        Assert.Contains("read_document", toolNames);
        Assert.Contains("update_document", toolNames);
        Assert.Contains("delete_document", toolNames);
        Assert.Contains("search_knowledge", toolNames);
        Assert.Contains("link_document", toolNames);
        Assert.Contains("unlink_document", toolNames);
        Assert.Contains("list_documents", toolNames);
        Assert.Contains("traverse_graph", toolNames);
    }

    [Fact]
    public void BuildComposerTools_WithoutKnowledgeGraph_ExcludesKnowledgeTools()
    {
        var composer = ComposerWithoutKnowledge();
        var tools = composer.BuildComposerTools();
        var toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToList();

        Assert.DoesNotContain("create_document", toolNames);
        Assert.DoesNotContain("search_knowledge", toolNames);
    }

    [Fact]
    public void SystemPrompt_WithKnowledgeGraph_MentionsKnowledgeTools()
    {
        Assert.Contains("create_document", _composer.GetSystemPrompt());
        Assert.Contains("knowledge graph", _composer.GetSystemPrompt(), StringComparison.OrdinalIgnoreCase);
    }

    // ── GetGoal includes Documents field ──

    [Fact]
    public async Task GetGoal_WithDocuments_ShowsDocuments()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("goal-with-docs", "Goal with knowledge documents");
        var goal = await _store.GetGoalAsync("goal-with-docs", ct);
        Assert.NotNull(goal);
        goal!.Documents = ["arch-brain", "features-auth"];
        await _store.UpdateGoalAsync(goal, ct);

        var result = await _composer.GetGoalAsync("goal-with-docs");

        Assert.Contains("arch-brain", result);
        Assert.Contains("features-auth", result);
        Assert.Contains("Documents", result);
    }

    [Fact]
    public async Task GetGoal_NoDocuments_OmitsDocumentsField()
    {
        await _composer.CreateGoalAsync("goal-no-docs", "Goal without docs");

        var result = await _composer.GetGoalAsync("goal-no-docs");

        Assert.DoesNotContain("Documents", result);
    }
}

/// <summary>
/// Additional integration tests for knowledge graph Composer tools.
/// Covers gaps in the existing test suite: persistence round-trip, 
/// combined field updates, inverse link warnings, snippet length,
/// target-side immutability, list filters, graph traversal directions,
/// and YAML frontmatter round-trip.
/// </summary>
public sealed class ComposerKnowledgeToolIntegrationTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly CopilotHive.Knowledge.KnowledgeGraph _knowledgeGraph;
    private readonly Composer _composer;

    // For persistence tests
    private readonly string _configRepoDir;
    private readonly string _remoteRepoDir;

    public ComposerKnowledgeToolIntegrationTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);

        _knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        _composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            knowledgeGraph: _knowledgeGraph);

        var baseDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _configRepoDir = Path.Combine(baseDir, "config-repo");
        _remoteRepoDir = Path.Combine(baseDir, "remote");
        Directory.CreateDirectory(_configRepoDir);
        Directory.CreateDirectory(_remoteRepoDir);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        var baseDir = Path.GetDirectoryName(_configRepoDir);
        if (baseDir is not null)
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── create_document: persistence round-trip ──

    [Fact]
    public async Task CreateDocument_PersistsToFilesystemAndReloads()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a document in memory — ID must match DeriveDocumentIdFromPath round-trip,
        // so use "features-persistence-test" (topic-slug format)
        var doc = await _knowledgeGraph.CreateDocumentAsync(
            "features-persistence-test", "Persistent Doc", CopilotHive.Knowledge.DocumentType.Feature,
            "This content should survive a round-trip.", topic: "features",
            tags: ["round-trip", "persistence"], ct: ct);

        // Commit to config repo directory (no ConfigRepoManager needed for file write)
        await _knowledgeGraph.CommitToConfigRepoAsync(_configRepoDir, "Add persistent doc", ct);

        // Verify file exists on disk
        var filePath = Path.Combine(_configRepoDir, doc.FilePath);
        Assert.True(File.Exists(filePath));

        // Read the file and verify YAML frontmatter
        var fileContent = await File.ReadAllTextAsync(filePath, ct);
        Assert.Contains("title: Persistent Doc", fileContent);
        Assert.Contains("type: feature", fileContent);
        Assert.Contains("tags: [round-trip, persistence]", fileContent);
        Assert.Contains("This content should survive a round-trip.", fileContent);

        // Reload from disk into a new KnowledgeGraph instance
        var reloadedGraph = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        await reloadedGraph.ReloadFromConfigRepoAsync(_configRepoDir, ct);

        var reloadedDoc = reloadedGraph.GetDocument("features-persistence-test");
        Assert.NotNull(reloadedDoc);
        Assert.Equal("Persistent Doc", reloadedDoc!.Title);
        Assert.Equal(CopilotHive.Knowledge.DocumentType.Feature, reloadedDoc.Type);
        Assert.Equal(CopilotHive.Knowledge.DocumentStatus.Draft, reloadedDoc.Status);
        Assert.Contains("round-trip", reloadedDoc.Tags);
        Assert.Contains("persistence", reloadedDoc.Tags);
        Assert.Contains("This content should survive a round-trip.", reloadedDoc.Content);
    }

    // ── read_document: full field verification ──

    [Fact]
    public async Task ReadDocument_ReturnsAllMetadataFields()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync(
            topic: "memory",
            slug: "full-doc",
            title: "Full Metadata Document",
            type: "memory",
            content: "The body content is here.",
            tags: ["tag1", "tag2"],
            cancellationToken: ct);
        await _composer.UpdateDocumentAsync("memory-full-doc", status: "active", cancellationToken: ct);

        var result = await _composer.ReadDocumentAsync("memory-full-doc", cancellationToken: ct);

        // Verify all fields present in the output
        Assert.Contains("memory-full-doc", result);
        Assert.Contains("Full Metadata Document", result);
        Assert.Contains("memory", result.ToLowerInvariant()); // type
        Assert.Contains("active", result.ToLowerInvariant()); // status
        Assert.Contains("tag1", result);
        Assert.Contains("tag2", result);
        Assert.Contains("The body content is here.", result);
    }

    // ── update_document: combined title, content, type, status, tags ──

    [Fact]
    public async Task UpdateDocument_CombinedFields_UpdatesAllFields()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("scratch", "combo", "Original", "scratch", "Original content", cancellationToken: ct);

        var result = await _composer.UpdateDocumentAsync(
            "scratch-combo",
            title: "Updated Title",
            content: "Updated content",
            type: "idea",
            status: "archived",
            tags: ["new-tag"],
            cancellationToken: ct);

        Assert.Contains("✅", result);
        var doc = _knowledgeGraph.GetDocument("scratch-combo");
        Assert.NotNull(doc);
        Assert.Equal("Updated Title", doc!.Title);
        Assert.Equal("Updated content", doc.Content);
        Assert.Equal(CopilotHive.Knowledge.DocumentType.Idea, doc.Type);
        Assert.Equal(CopilotHive.Knowledge.DocumentStatus.Archived, doc.Status);
        Assert.Equal(["new-tag"], doc.Tags);
    }

    // ── delete_document: warns about parent, implements, supersedes inverse links ──

    [Fact]
    public async Task DeleteDocument_WarnsAboutImplementsInverseLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "spec", "Spec", "implementation", "Spec content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "impl", "Impl", "feature", "Impl content", cancellationToken: ct);

        // impl implements spec
        _knowledgeGraph.AddLink("arch-impl",
            new CopilotHive.Knowledge.DocumentLink("arch-spec", CopilotHive.Knowledge.LinkType.Implements));

        var result = await _composer.DeleteDocumentAsync("arch-spec", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("⚠️", result);
        Assert.Contains("arch-impl", result); // inverse link source should be warned
    }

    [Fact]
    public async Task DeleteDocument_WarnsAboutSupersedesInverseLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "old", "Old Doc", "implementation", "Old content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "new", "New Doc", "implementation", "New content", cancellationToken: ct);

        // new supersedes old
        _knowledgeGraph.AddLink("arch-new",
            new CopilotHive.Knowledge.DocumentLink("arch-old", CopilotHive.Knowledge.LinkType.Supersedes));

        var result = await _composer.DeleteDocumentAsync("arch-old", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("⚠️", result);
        Assert.Contains("arch-new", result);
    }

    [Fact]
    public async Task DeleteDocument_WarnsAboutParentInverseLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "parent-doc2", "Parent2", "implementation", "Parent content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "child-doc2", "Child2", "feature", "Child content", cancellationToken: ct);

        // child-doc2's parent is parent-doc2
        _knowledgeGraph.AddLink("arch-child-doc2",
            new CopilotHive.Knowledge.DocumentLink("arch-parent-doc2", CopilotHive.Knowledge.LinkType.Parent));

        var result = await _composer.DeleteDocumentAsync("arch-parent-doc2", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("⚠️", result);
        Assert.Contains("arch-child-doc2", result);
    }

    // ── search_knowledge: 200-char snippet verification ──

    [Fact]
    public async Task SearchKnowledge_SnippetTruncated_WhenContentExceeds200Chars()
    {
        var ct = TestContext.Current.CancellationToken;
        var longContent = new string('x', 300); // 300 chars
        await _composer.CreateDocumentAsync("scratch", "long-snippet", "Long Snippet", "scratch", longContent, cancellationToken: ct);

        var result = await _composer.SearchKnowledgeAsync("long snippet", cancellationToken: ct);

        Assert.Contains("scratch-long-snippet", result);
        // The snippet should be truncated; verify it contains "…"
        Assert.Contains("…", result);
    }

    [Fact]
    public async Task SearchKnowledge_SnippetNotTruncated_WhenContentShort()
    {
        var ct = TestContext.Current.CancellationToken;
        var shortContent = "Short content";
        await _composer.CreateDocumentAsync("scratch", "short-snippet", "Short Snippet", "scratch", shortContent, cancellationToken: ct);

        var result = await _composer.SearchKnowledgeAsync("short snippet", cancellationToken: ct);

        Assert.Contains("scratch-short-snippet", result);
        Assert.Contains("Short content", result);
        Assert.DoesNotContain("…", result);
    }

    // ── link_document: target document not modified ──

    [Fact]
    public async Task LinkDocument_DoesNotModifyTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "link-src-2", "Link Src2", "implementation", "Source content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "link-tgt-2", "Link Tgt2", "feature", "Target content", cancellationToken: ct);

        var targetBeforeLink = _knowledgeGraph.GetDocument("arch-link-tgt-2");
        var targetLinksBefore = targetBeforeLink!.Links.Count;

        await _composer.LinkDocumentAsync("arch-link-src-2", "arch-link-tgt-2", "depends_on", cancellationToken: ct);

        // Verify target's links have NOT changed
        var targetAfterLink = _knowledgeGraph.GetDocument("arch-link-tgt-2");
        Assert.Equal(targetLinksBefore, targetAfterLink!.Links.Count);

        // Verify source's links HAVE changed
        var sourceAfterLink = _knowledgeGraph.GetDocument("arch-link-src-2");
        Assert.Single(sourceAfterLink!.Links);
        Assert.Equal("arch-link-tgt-2", sourceAfterLink.Links[0].TargetId);
    }

    // ── list_documents: filter by status and tag ──

    [Fact]
    public async Task ListDocuments_FilterByStatus_FiltersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("memory", "active-mem", "Active Memory", "memory", "Content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("scratch", "draft-scratch", "Draft Scratch", "scratch", "Content", cancellationToken: ct);
        // Set first doc to active status
        await _composer.UpdateDocumentAsync("memory-active-mem", status: "active", cancellationToken: ct);

        var result = await _composer.ListDocumentsAsync(status: "active", cancellationToken: ct);

        Assert.Contains("memory-active-mem", result);
        // Draft scratch should not appear (still in draft status by default)
        Assert.DoesNotContain("scratch-draft-scratch", result);
    }

    [Fact]
    public async Task ListDocuments_FilterByTag_FiltersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("ideas", "tagged-idea", "Tagged Idea", "idea", "Content", tags: ["special-tag"], cancellationToken: ct);
        await _composer.CreateDocumentAsync("ideas", "untagged-idea", "Untagged Idea", "idea", "Content", cancellationToken: ct);

        var result = await _composer.ListDocumentsAsync(tag: "special-tag", cancellationToken: ct);

        Assert.Contains("ideas-tagged-idea", result);
        Assert.DoesNotContain("ideas-untagged-idea", result);
    }

    // ── list_documents: snippet behavior ──

    [Fact]
    public async Task ListDocuments_SnippetIsPresent_OutputContainsSnippetLine()
    {
        var ct = TestContext.Current.CancellationToken;
        // Create two documents with distinct, recognisable content strings
        const string alphaContent = "UNIQUE_CONTENT_ALPHA snip-alpha";
        const string betaContent  = "UNIQUE_CONTENT_BETA  snip-beta";
        await _composer.CreateDocumentAsync("scratch", "snip-alpha", "Snip Alpha", "scratch", alphaContent, cancellationToken: ct);
        await _composer.CreateDocumentAsync("scratch", "snip-beta",  "Snip Beta",  "scratch", betaContent,  cancellationToken: ct);

        var result = await _composer.ListDocumentsAsync(cancellationToken: ct);

        // Each document must have its own Snippet: line
        Assert.Contains("  Snippet: UNIQUE_CONTENT_ALPHA snip-alpha", result);
        Assert.Contains("  Snippet: UNIQUE_CONTENT_BETA  snip-beta",  result);

        // Count occurrences: two documents → two Snippet: lines
        var snippetCount = result.Split('\n').Count(l => l.StartsWith("  Snippet:"));
        Assert.Equal(2, snippetCount);
    }

    [Fact]
    public async Task ListDocuments_Truncation_LongContentSnippetIsTruncated()
    {
        var ct = TestContext.Current.CancellationToken;
        // 201+ characters to trigger truncation
        var longContent = new string('x', 250);
        await _composer.CreateDocumentAsync("scratch", "snip-long", "Snip Long", "scratch", longContent, cancellationToken: ct);

        var result = await _composer.ListDocumentsAsync(cancellationToken: ct);

        // The snippet should be exactly 200 chars followed by "…"
        var expectedSnippet = new string('x', 200) + "…";
        Assert.Contains($"  Snippet: {expectedSnippet}", result);
    }

    [Fact]
    public async Task ListDocuments_NewlineReplacement_NewlinesBecomeSpaces()
    {
        var ct = TestContext.Current.CancellationToken;
        // Content with newlines — must be replaced by spaces in snippet
        var contentWithNewlines = "first line\nsecond line\nthird line";
        await _composer.CreateDocumentAsync("scratch", "snip-newline", "Snip Newline", "scratch", contentWithNewlines, cancellationToken: ct);

        var result = await _composer.ListDocumentsAsync(cancellationToken: ct);

        Assert.Contains("  Snippet: first line second line third line", result);
        // Verify no literal newlines appear within the snippet line itself
        var snippetLine = result.Split('\n').First(l => l.StartsWith("  Snippet:"));
        Assert.DoesNotContain('\n', snippetLine);
    }

    // ── traverse_graph: both direction ──

    [Fact]
    public async Task TraverseGraph_BothDirection_FollowsForwardAndReverseLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        // A → B (outgoing: related)
        // C → A (incoming: depends_on)
        await _composer.CreateDocumentAsync("arch", "tg-a", "TG A", "implementation", "Content A", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "tg-b", "TG B", "feature", "Content B", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "tg-c", "TG C", "idea", "Content C", cancellationToken: ct);

        _knowledgeGraph.AddLink("arch-tg-a",
            new CopilotHive.Knowledge.DocumentLink("arch-tg-b", CopilotHive.Knowledge.LinkType.Related));
        _knowledgeGraph.AddLink("arch-tg-c",
            new CopilotHive.Knowledge.DocumentLink("arch-tg-a", CopilotHive.Knowledge.LinkType.DependsOn));

        var result = await _composer.TraverseGraphAsync("arch-tg-a", depth: 1, direction: "both", cancellationToken: ct);

        // Should include both outgoing (tg-b) and incoming (tg-c) connections
        Assert.Contains("arch-tg-b", result);
        Assert.Contains("arch-tg-c", result);
    }

    // ── traverse_graph: link_types filter ──

    [Fact]
    public async Task TraverseGraph_LinkTypesFilter_FiltersToSpecifiedTypes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "tg-root2", "TG Root2", "implementation", "Root", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "tg-related", "TG Related", "feature", "Related", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "tg-dep", "TG Dep", "idea", "Dep", cancellationToken: ct);

        _knowledgeGraph.AddLink("arch-tg-root2",
            new CopilotHive.Knowledge.DocumentLink("arch-tg-related", CopilotHive.Knowledge.LinkType.Related));
        _knowledgeGraph.AddLink("arch-tg-root2",
            new CopilotHive.Knowledge.DocumentLink("arch-tg-dep", CopilotHive.Knowledge.LinkType.DependsOn));

        // Filter to only "depends_on" type links
        var result = await _composer.TraverseGraphAsync("arch-tg-root2", depth: 1, direction: "outgoing", link_types: ["depends_on"], cancellationToken: ct);

        Assert.Contains("arch-tg-dep", result);
        Assert.DoesNotContain("arch-tg-related", result);
    }

    // ── KnowledgeGraph: YAML frontmatter round-trip ──

    [Fact]
    public async Task KnowledgeGraph_YamlRoundTrip_PreservesLinksAndMetadata()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create documents with topic-prefixed IDs for round-trip correctness
        var doc1 = await _knowledgeGraph.CreateDocumentAsync(
            "features-rt-parent", "RT Parent", CopilotHive.Knowledge.DocumentType.Implementation,
            "Parent content here.", topic: "features", author: "test-author",
            tags: ["parent", "round-trip"], ct: ct);
        _knowledgeGraph.AddLink("features-rt-parent",
            new CopilotHive.Knowledge.DocumentLink("features-rt-child", CopilotHive.Knowledge.LinkType.Parent, "My child doc"));

        var doc2 = await _knowledgeGraph.CreateDocumentAsync(
            "features-rt-child", "RT Child", CopilotHive.Knowledge.DocumentType.Feature,
            "Child content here.", topic: "features", ct: ct);

        // Write to disk
        await _knowledgeGraph.CommitToConfigRepoAsync(_configRepoDir, "Add parent and child", ct);

        // Reload into new graph
        var reloaded = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        await reloaded.ReloadFromConfigRepoAsync(_configRepoDir, ct);

        var parent = reloaded.GetDocument("features-rt-parent");
        Assert.NotNull(parent);
        Assert.Equal("RT Parent", parent!.Title);
        Assert.Equal(CopilotHive.Knowledge.DocumentType.Implementation, parent.Type);
        Assert.Equal("test-author", parent.Author);
        Assert.Contains("parent", parent.Tags);
        Assert.Contains("round-trip", parent.Tags);
        Assert.Contains("Parent content here.", parent.Content);
        // Links should be round-tripped
        Assert.Single(parent.Links);
        Assert.Equal("features-rt-child", parent.Links[0].TargetId);
        Assert.Equal(CopilotHive.Knowledge.LinkType.Parent, parent.Links[0].Type);
        Assert.Equal("My child doc", parent.Links[0].Description);

        var child = reloaded.GetDocument("features-rt-child");
        Assert.NotNull(child);
        Assert.Equal("RT Child", child!.Title);
    }

    // ── KnowledgeGraph: reverse index integrity after AddLink/RemoveLink ──

    [Fact]
    public async Task KnowledgeGraph_AddLink_UpdatesReverseIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await _knowledgeGraph.CreateDocumentAsync(
            "ri-a", "RI A", CopilotHive.Knowledge.DocumentType.Implementation, "Content A", topic: "test", ct: ct);
        await _knowledgeGraph.CreateDocumentAsync(
            "ri-b", "RI B", CopilotHive.Knowledge.DocumentType.Feature, "Content B", topic: "test", ct: ct);

        _knowledgeGraph.AddLink("ri-a",
            new CopilotHive.Knowledge.DocumentLink("ri-b", CopilotHive.Knowledge.LinkType.DependsOn));

        // Check reverse index via GetDependedOnBy
        var dependedOnBy = _knowledgeGraph.GetDependedOnBy("ri-b");
        Assert.Single(dependedOnBy);
        Assert.Equal("ri-a", dependedOnBy[0].Id);
    }

    [Fact]
    public async Task KnowledgeGraph_RemoveLink_CleansUpReverseIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await _knowledgeGraph.CreateDocumentAsync(
            "rl-a", "RL A", CopilotHive.Knowledge.DocumentType.Implementation, "Content A", topic: "test", ct: ct);
        await _knowledgeGraph.CreateDocumentAsync(
            "rl-b", "RL B", CopilotHive.Knowledge.DocumentType.Feature, "Content B", topic: "test", ct: ct);

        _knowledgeGraph.AddLink("rl-a",
            new CopilotHive.Knowledge.DocumentLink("rl-b", CopilotHive.Knowledge.LinkType.DependsOn));

        // Verify link was added
        var dependedOnBy = _knowledgeGraph.GetDependedOnBy("rl-b");
        Assert.Single(dependedOnBy);

        // Remove the link
        _knowledgeGraph.RemoveLink("rl-a", "rl-b", CopilotHive.Knowledge.LinkType.DependsOn);

        // Verify reverse index is cleaned up
        var dependedOnByAfter = _knowledgeGraph.GetDependedOnBy("rl-b");
        Assert.Empty(dependedOnByAfter);

        // Verify forward link is removed
        var doc = _knowledgeGraph.GetDocument("rl-a");
        Assert.Empty(doc!.Links);
    }

    // ── create_document: persists after CommitToConfigRepoAsync with config repo ──

    [Fact]
    public async Task CreateDocument_WithCommitToConfigRepo_WritesMarkdownFile()
    {
        var ct = TestContext.Current.CancellationToken;

        // Use a KnowledgeGraph with a config repo path for writing
        var kg = new CopilotHive.Knowledge.KnowledgeGraph(configRepo: null, logger: null);
        var composerWithRepo = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            configRepo: null, // no ConfigRepoManager — just writing to disk
            knowledgeGraph: kg);

        await composerWithRepo.CreateDocumentAsync(
            topic: "ideas",
            slug: "persist-idea",
            title: "Persisted Idea",
            type: "idea",
            content: "This idea should be persisted.",
            tags: ["persist"],
            cancellationToken: ct);

        // Now commit manually
        await kg.CommitToConfigRepoAsync(_configRepoDir, "Add persisted idea", ct);

        // Verify file exists
        var doc = kg.GetDocument("ideas-persist-idea");
        Assert.NotNull(doc);
        var filePath = Path.Combine(_configRepoDir, doc!.FilePath);
        Assert.True(File.Exists(filePath));

        var content = await File.ReadAllTextAsync(filePath, ct);
        Assert.Contains("title: Persisted Idea", content);
        Assert.Contains("type: idea", content);
        Assert.Contains("tags: [persist]", content);
        Assert.Contains("This idea should be persisted.", content);
    }

    // ── update_document: update with tags replacement ──

    [Fact]
    public async Task UpdateDocument_TagsReplacedEntirely()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("scratch", "tags-test", "Tags Test", "scratch", "Content", tags: ["old1", "old2"], cancellationToken: ct);

        // Replace tags entirely
        var result = await _composer.UpdateDocumentAsync("scratch-tags-test", tags: ["new1", "new2", "new3"], cancellationToken: ct);

        Assert.Contains("✅", result);
        var doc = _knowledgeGraph.GetDocument("scratch-tags-test");
        Assert.NotNull(doc);
        Assert.Equal(3, doc!.Tags.Count);
        Assert.Contains("new1", doc.Tags);
        Assert.Contains("new2", doc.Tags);
        Assert.Contains("new3", doc.Tags);
        Assert.DoesNotContain("old1", doc.Tags);
    }

    // ── unlink_document: verify link is removed from both forward and reverse indices ──

    [Fact]
    public async Task UnlinkDocument_RemovesFromForwardAndReverseIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "ul-src2", "UL Src2", "implementation", "Content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "ul-tgt2", "UL Tgt2", "implementation", "Content", cancellationToken: ct);

        // Add a depends_on link
        await _composer.LinkDocumentAsync("arch-ul-src2", "arch-ul-tgt2", "depends_on", cancellationToken: ct);

        // Verify reverse index exists
        var dependedOnBy = _knowledgeGraph.GetDependedOnBy("arch-ul-tgt2");
        Assert.Single(dependedOnBy);

        // Unlink
        var result = await _composer.UnlinkDocumentAsync("arch-ul-src2", "arch-ul-tgt2", "depends_on", cancellationToken: ct);

        Assert.Contains("✅", result);

        // Verify forward link is removed
        var src = _knowledgeGraph.GetDocument("arch-ul-src2");
        Assert.Empty(src!.Links);

        // Verify reverse index is cleaned up
        var dependedOnByAfter = _knowledgeGraph.GetDependedOnBy("arch-ul-tgt2");
        Assert.Empty(dependedOnByAfter);
    }

    // ── UpdateGoal: response includes Documents when goal has documents ──

    [Fact]
    public async Task UpdateGoal_WithDocuments_StatusUpdate_ShowsDocuments()
    {
        var ct = TestContext.Current.CancellationToken;

        // Pre-create knowledge documents so titles are available
        await _composer.CreateDocumentAsync("arch", "brain", "Brain Architecture", "implementation", "Brain content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("features", "design", "Feature Design", "feature", "Design content", cancellationToken: ct);

        await _composer.CreateGoalAsync("goal-docs-update", "A goal with documents attached");
        var goal = await _store.GetGoalAsync("goal-docs-update", ct);
        Assert.NotNull(goal);
        goal!.Documents = ["arch-brain", "features-design"];
        await _store.UpdateGoalAsync(goal, ct);

        // Update status Draft→Pending triggers AppendDocuments
        var result = await _composer.UpdateGoalAsync("goal-docs-update", "status", "Pending");

        Assert.Contains("✅", result);

        // Should contain document IDs with titles in readable format
        Assert.Contains("arch-brain", result);
        Assert.Contains("Brain Architecture", result);
        Assert.Contains("features-design", result);
        Assert.Contains("Feature Design", result);
        Assert.Contains("Documents", result);
    }

    [Fact]
    public async Task UpdateGoal_NoDocuments_ResponseOmitsDocumentsField()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateGoalAsync("goal-no-docs-update", "A goal without documents");

        var result = await _composer.UpdateGoalAsync("goal-no-docs-update", "status", "Pending");

        Assert.Contains("✅", result);
        Assert.DoesNotContain("Documents", result);
    }

    [Fact]
    public async Task CreateGoal_DocumentsShown_WhenGoalHasDocuments()
    {
        var ct = TestContext.Current.CancellationToken;

        // Pre-create knowledge documents so titles are available
        await _composer.CreateDocumentAsync("arch", "brain2", "Brain Architecture v2", "implementation", "Brain content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("features", "auth", "Auth Feature", "feature", "Auth content", cancellationToken: ct);

        // Create goal with documents attached via the documents parameter
        var result = await _composer.CreateGoalAsync(
            "goal-with-docs-create",
            "Goal with knowledge documents",
            documents: "arch-brain2,features-auth");

        Assert.Contains("✅", result);

        // Should contain document IDs and titles in readable format
        Assert.Contains("arch-brain2", result);
        Assert.Contains("Brain Architecture v2", result);
        Assert.Contains("features-auth", result);
        Assert.Contains("Auth Feature", result);
        Assert.Contains("Documents", result);

        // Verify documents were persisted to the store
        var goal = await _store.GetGoalAsync("goal-with-docs-create", ct);
        Assert.NotNull(goal);
        Assert.Contains("arch-brain2", goal!.Documents);
        Assert.Contains("features-auth", goal.Documents);
    }

    // ── delete_document: warns about related and references inverse links ──

    [Fact]
    public async Task DeleteDocument_WarnsAboutRelatedLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "ref-src", "Ref Source", "implementation", "Source content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "ref-tgt", "Ref Target", "feature", "Target content", cancellationToken: ct);

        // ref-src has a Related link pointing to ref-tgt
        _knowledgeGraph.AddLink("arch-ref-src",
            new CopilotHive.Knowledge.DocumentLink("arch-ref-tgt", CopilotHive.Knowledge.LinkType.Related));

        var result = await _composer.DeleteDocumentAsync("arch-ref-tgt", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("⚠️", result);
        Assert.Contains("arch-ref-src", result); // the source should be warned
    }

    [Fact]
    public async Task DeleteDocument_WarnsAboutReferencesLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "refs-src", "Refs Source", "implementation", "Source content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "refs-tgt", "Refs Target", "feature", "Target content", cancellationToken: ct);

        // refs-src has a References link pointing to refs-tgt
        _knowledgeGraph.AddLink("arch-refs-src",
            new CopilotHive.Knowledge.DocumentLink("arch-refs-tgt", CopilotHive.Knowledge.LinkType.References));

        var result = await _composer.DeleteDocumentAsync("arch-refs-tgt", cancellationToken: ct);

        Assert.Contains("✅", result);
        Assert.Contains("⚠️", result);
        Assert.Contains("arch-refs-src", result);
    }

    // ── traverse_graph: incoming direction finds related and references links ──

    [Fact]
    public async Task TraverseGraph_IncomingDirection_FindsRelatedAndReferencesLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _composer.CreateDocumentAsync("arch", "tg-inc-center", "Center", "implementation", "Center content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "tg-inc-rel", "Related Source", "feature", "Related content", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "tg-inc-ref", "References Source", "idea", "References content", cancellationToken: ct);

        // Both tg-inc-rel and tg-inc-ref point to tg-inc-center via different link types
        _knowledgeGraph.AddLink("arch-tg-inc-rel",
            new CopilotHive.Knowledge.DocumentLink("arch-tg-inc-center", CopilotHive.Knowledge.LinkType.Related));
        _knowledgeGraph.AddLink("arch-tg-inc-ref",
            new CopilotHive.Knowledge.DocumentLink("arch-tg-inc-center", CopilotHive.Knowledge.LinkType.References));

        var result = await _composer.TraverseGraphAsync("arch-tg-inc-center", depth: 1, direction: "incoming", cancellationToken: ct);

        Assert.Contains("arch-tg-inc-rel", result);
        Assert.Contains("arch-tg-inc-ref", result);
    }

    // ── traverse_graph: multi-level BFS traversal ──

    [Fact]
    public async Task TraverseGraph_MultiLevel_FollowsTransitiveLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        // A → B → C (two hops)
        await _composer.CreateDocumentAsync("arch", "ml-a", "ML A", "implementation", "A", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "ml-b", "ML B", "feature", "B", cancellationToken: ct);
        await _composer.CreateDocumentAsync("arch", "ml-c", "ML C", "idea", "C", cancellationToken: ct);

        _knowledgeGraph.AddLink("arch-ml-a",
            new CopilotHive.Knowledge.DocumentLink("arch-ml-b", CopilotHive.Knowledge.LinkType.Related));
        _knowledgeGraph.AddLink("arch-ml-b",
            new CopilotHive.Knowledge.DocumentLink("arch-ml-c", CopilotHive.Knowledge.LinkType.DependsOn));

        // Depth 1 should only see direct neighbor (B)
        var result1 = await _composer.TraverseGraphAsync("arch-ml-a", depth: 1, direction: "outgoing", cancellationToken: ct);
        Assert.Contains("arch-ml-b", result1);
        Assert.DoesNotContain("arch-ml-c", result1);

        // Depth 2 should see both B and C
        var result2 = await _composer.TraverseGraphAsync("arch-ml-a", depth: 2, direction: "outgoing", cancellationToken: ct);
        Assert.Contains("arch-ml-b", result2);
        Assert.Contains("arch-ml-c", result2);
    }
}

/// <summary>
/// Tests for the Composer's issue management tools (create_issue, list_issues,
/// get_issue, update_issue).
/// </summary>
public sealed class ComposerIssueToolTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _goalStore;
    private readonly IssueStore _issueStore;
    private readonly Composer _composer;
    private readonly Composer _composerWithoutIssueStore;

    public ComposerIssueToolTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _goalStore = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
        _issueStore = new IssueStore(_dbContext, NullLogger<IssueStore>.Instance);

        _composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _goalStore,
            stateDir: Path.GetTempPath(),
            issueStore: _issueStore);

        _composerWithoutIssueStore = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _goalStore,
            stateDir: Path.GetTempPath());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    // ── create_issue ──

    [Fact]
    public async Task CreateIssue_ValidInput_CreatesIssue()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateIssueAsync(
            "bug", "Parser crashes on empty input", "The parser throws when given an empty string.",
            severity: "high", repository_names: ["repo-a", "repo-b"], ct: ct);

        Assert.Contains("Issue created:", result);

        var id = result.Replace("Issue created: ", "").Trim();
        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal(IssueType.Bug, issue!.Type);
        Assert.Equal("Parser crashes on empty input", issue.Title);
        Assert.Equal("The parser throws when given an empty string.", issue.Description);
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.Equal(IssueStatus.Open, issue.Status);
        Assert.Equal(2, issue.RepositoryNames.Count);
        Assert.Contains("repo-a", issue.RepositoryNames);
        Assert.Contains("repo-b", issue.RepositoryNames);
        Assert.Null(issue.SourceGoalId);
        Assert.Null(issue.SourceRole);
        Assert.Null(issue.SourceIteration);
    }

    [Fact]
    public async Task CreateIssue_DefaultSeverity_IsLow()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateIssueAsync("suggestion", "Add dark mode", "Nice to have", ct: ct);

        Assert.Contains("Issue created:", result);

        var id = result.Replace("Issue created: ", "").Trim();
        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal(IssueSeverity.Low, issue!.Severity);
    }

    [Fact]
    public async Task CreateIssue_NoRepositories_EmptyList()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateIssueAsync("concern", "Security concern", "Details", ct: ct);

        var id = result.Replace("Issue created: ", "").Trim();
        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Empty(issue!.RepositoryNames);
    }

    [Fact]
    public async Task CreateIssue_InvalidType_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateIssueAsync("not-a-type", "Title", "Description", ct: ct);

        Assert.Contains("Unknown issue type", result);
    }

    [Fact]
    public async Task CreateIssue_InvalidSeverity_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateIssueAsync("bug", "Title", "Description", severity: "extreme", ct: ct);

        Assert.Contains("Unknown severity", result);
    }

    [Fact]
    public async Task CreateIssue_NoIssueStore_ReturnsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composerWithoutIssueStore.CreateIssueAsync("bug", "Title", "Description", ct: ct);

        Assert.Equal("Issue tracking not available.", result);
    }

    [Fact]
    public async Task CreateIssue_CodeQualityAlias_Accepted()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.CreateIssueAsync("codequality", "Naming", "Inconsistent naming", ct: ct);

        Assert.Contains("Issue created:", result);

        var id = result.Replace("Issue created: ", "").Trim();
        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal(IssueType.CodeQuality, issue!.Type);
    }

    // ── list_issues ──

    [Fact]
    public async Task ListIssues_Empty_ReturnsNoIssuesFound()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.ListIssuesAsync(ct: ct);

        Assert.Equal("No issues found.", result);
    }

    [Fact]
    public async Task ListIssues_ReturnsFormattedSnakeCaseList()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateIssueAsync("bug", "Crash on startup", "Desc one", severity: "high", ct: ct);
        await _composer.CreateIssueAsync("code_quality", "Naming inconsistency", "Desc two", severity: "low", ct: ct);

        var result = await _composer.ListIssuesAsync(ct: ct);

        Assert.Contains("2 issue(s)", result);
        Assert.Contains("bug", result);
        Assert.Contains("code_quality", result);
        Assert.Contains("high", result);
        Assert.Contains("low", result);
        Assert.Contains("open", result);
        Assert.DoesNotContain("CodeQuality", result); // snake_case, not PascalCase
    }

    [Fact]
    public async Task ListIssues_FilterByStatus_FiltersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateIssueAsync("bug", "Open bug", "Desc", ct: ct);
        var created = await _composer.CreateIssueAsync("suggestion", "Resolved suggestion", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();
        await _composer.UpdateIssueAsync(id, status: "resolved", ct: ct);

        var result = await _composer.ListIssuesAsync(status: "resolved", ct: ct);

        Assert.Contains("1 issue(s)", result);
        Assert.Contains("Resolved suggestion", result);
        Assert.DoesNotContain("Open bug", result);
    }

    [Fact]
    public async Task ListIssues_FilterByType_FiltersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateIssueAsync("bug", "Bug one", "Desc", ct: ct);
        await _composer.CreateIssueAsync("suggestion", "Suggestion one", "Desc", ct: ct);

        var result = await _composer.ListIssuesAsync(type: "bug", ct: ct);

        Assert.Contains("1 issue(s)", result);
        Assert.Contains("Bug one", result);
        Assert.DoesNotContain("Suggestion one", result);
    }

    [Fact]
    public async Task ListIssues_FilterBySeverity_FiltersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateIssueAsync("bug", "High bug", "Desc", severity: "high", ct: ct);
        await _composer.CreateIssueAsync("bug", "Low bug", "Desc", severity: "low", ct: ct);

        var result = await _composer.ListIssuesAsync(severity: "high", ct: ct);

        Assert.Contains("1 issue(s)", result);
        Assert.Contains("High bug", result);
        Assert.DoesNotContain("Low bug", result);
    }

    [Fact]
    public async Task ListIssues_InvalidStatus_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.ListIssuesAsync(status: "bogus", ct: ct);

        Assert.Contains("Unknown status", result);
    }

    [Fact]
    public async Task ListIssues_NoIssueStore_ReturnsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composerWithoutIssueStore.ListIssuesAsync(ct: ct);

        Assert.Equal("Issue tracking not available.", result);
    }

    // ── get_issue ──

    [Fact]
    public async Task GetIssue_ExistingIssue_ReturnsAllFields()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync(
            "workflow", "Deploy step missing", "The deploy step is missing from the pipeline.",
            severity: "medium", repository_names: ["repo-x"], ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.GetIssueAsync(id, ct: ct);

        Assert.Contains($"## Issue: {id}", result);
        Assert.Contains("workflow", result);
        Assert.Contains("Deploy step missing", result);
        Assert.Contains("The deploy step is missing from the pipeline.", result);
        Assert.Contains("medium", result);
        Assert.Contains("open", result);
        Assert.Contains("repo-x", result);
        Assert.Contains("SourceGoalId", result);
        Assert.Contains("SourceRole", result);
        Assert.Contains("SourceIteration", result);
        Assert.Contains("CreatedAt", result);
        Assert.Contains("UpdatedAt", result);
        Assert.Contains("ResolvedAt", result);
        Assert.Contains("LinkedGoalId", result);
    }

    [Fact]
    public async Task GetIssue_NotFound_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.GetIssueAsync("no-such-issue", ct: ct);

        Assert.Equal("Issue 'no-such-issue' not found.", result);
    }

    [Fact]
    public async Task GetIssue_NoIssueStore_ReturnsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composerWithoutIssueStore.GetIssueAsync("any-id", ct: ct);

        Assert.Equal("Issue tracking not available.", result);
    }

    // ── update_issue ──

    [Fact]
    public async Task UpdateIssue_PartialUpdate_OnlyChangesProvidedFields()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync(
            "bug", "Original title", "Original description", severity: "low", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, status: "triaged", severity: "high", ct: ct);

        Assert.Contains($"## Issue: {id}", result);
        Assert.Contains("triaged", result);
        Assert.Contains("high", result);
        Assert.Contains("Original title", result);
        Assert.Contains("Original description", result);
        Assert.Contains("bug", result);

        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal(IssueStatus.Triaged, issue!.Status);
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.Equal(IssueType.Bug, issue.Type);
        Assert.Equal("Original title", issue.Title);
        Assert.Equal("Original description", issue.Description);
    }

    [Fact]
    public async Task UpdateIssue_ChangeTitleAndDescription_UpdatesBoth()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Old title", "Old description", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, title: "New title", description: "New description", ct: ct);

        Assert.Contains("New title", result);
        Assert.Contains("New description", result);
        Assert.DoesNotContain("Old title", result);
        Assert.DoesNotContain("Old description", result);

        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal("New title", issue!.Title);
        Assert.Equal("New description", issue.Description);
    }

    [Fact]
    public async Task UpdateIssue_ChangeType_UpdatesType()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, type: "suggestion", ct: ct);

        Assert.Contains("suggestion", result);

        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal(IssueType.Suggestion, issue!.Type);
    }

    [Fact]
    public async Task UpdateIssue_NotFound_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composer.UpdateIssueAsync("no-such-issue", status: "resolved", ct: ct);

        Assert.Equal("Issue 'no-such-issue' not found.", result);
    }

    [Fact]
    public async Task UpdateIssue_InvalidStatus_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, status: "bogus", ct: ct);

        Assert.Contains("Unknown status", result);
    }

    [Fact]
    public async Task UpdateIssue_InvalidSeverity_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, severity: "bogus", ct: ct);

        Assert.Contains("Unknown severity", result);
    }

    [Fact]
    public async Task UpdateIssue_InvalidType_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, type: "bogus", ct: ct);

        Assert.Contains("Unknown issue type", result);
    }

    [Fact]
    public async Task UpdateIssue_NoIssueStore_ReturnsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _composerWithoutIssueStore.UpdateIssueAsync("any-id", status: "resolved", ct: ct);

        Assert.Equal("Issue tracking not available.", result);
    }

    [Fact]
    public async Task UpdateIssue_Resolved_SetsResolvedAt()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, status: "resolved", ct: ct);

        Assert.Contains("resolved", result);

        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal(IssueStatus.Resolved, issue!.Status);
        Assert.NotNull(issue.ResolvedAt);
    }

    [Fact]
    public async Task UpdateIssue_InProgressAlias_Accepted()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, status: "inprogress", ct: ct);

        Assert.Contains("in_progress", result);

        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal(IssueStatus.InProgress, issue!.Status);
    }

    // ── ParseIssueStatus — all 6 values + alias + unknown ──
    // ParseIssueStatus is private static; tested through update_issue which applies
    // the parsed status to the persisted issue.

    [Theory]
    [InlineData("open", IssueStatus.Open)]
    [InlineData("triaged", IssueStatus.Triaged)]
    [InlineData("acknowledged", IssueStatus.Acknowledged)]
    [InlineData("in_progress", IssueStatus.InProgress)]
    [InlineData("resolved", IssueStatus.Resolved)]
    [InlineData("closed", IssueStatus.Closed)]
    public async Task ParseIssueStatus_AllStatusValues_ParseCorrectly(string status, IssueStatus expected)
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, status: status, ct: ct);

        Assert.Contains($"## Issue: {id}", result);

        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal(expected, issue!.Status);
    }

    [Fact]
    public async Task ParseIssueStatus_InProgressAlias_ParsesToInProgress()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, status: "inprogress", ct: ct);

        Assert.Contains("in_progress", result);

        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal(IssueStatus.InProgress, issue!.Status);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("unknown")]
    [InlineData("cancelled")]
    public async Task ParseIssueStatus_UnknownStatus_ReturnsErrorMessage(string status)
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, status: status, ct: ct);

        Assert.Contains("Unknown status", result);
    }

    // ── list_issues — null/empty filters return all ──

    [Fact]
    public async Task ListIssues_NullFilters_ReturnsAllIssues()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateIssueAsync("bug", "Bug one", "Desc", severity: "high", ct: ct);
        await _composer.CreateIssueAsync("suggestion", "Suggestion one", "Desc", severity: "low", ct: ct);
        await _composer.CreateIssueAsync("concern", "Concern one", "Desc", severity: "medium", ct: ct);

        // All filters omitted (null) → no filtering, all 3 issues returned.
        var result = await _composer.ListIssuesAsync(ct: ct);

        Assert.Contains("3 issue(s)", result);
        Assert.Contains("Bug one", result);
        Assert.Contains("Suggestion one", result);
        Assert.Contains("Concern one", result);
    }

    [Fact]
    public async Task ListIssues_EmptyStringFilters_ReturnsAllIssues()
    {
        var ct = TestContext.Current.CancellationToken;

        await _composer.CreateIssueAsync("bug", "Bug two", "Desc", severity: "high", ct: ct);
        await _composer.CreateIssueAsync("suggestion", "Suggestion two", "Desc", severity: "low", ct: ct);

        // Empty-string filters → treated as null → no filtering.
        var result = await _composer.ListIssuesAsync(status: "", type: "", severity: "", ct: ct);

        Assert.Contains("2 issue(s)", result);
        Assert.Contains("Bug two", result);
        Assert.Contains("Suggestion two", result);
    }

    // ── update_issue — re-fetches and returns updated details ──

    [Fact]
    public async Task UpdateIssue_ReFetchesAndReturnsUpdatedDetails()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Original", "Original desc", severity: "low", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, status: "triaged", severity: "high", title: "Updated title", ct: ct);

        // The returned details must reflect the updated values (re-fetched from the store).
        Assert.Contains($"## Issue: {id}", result);
        Assert.Contains("Updated title", result);
        Assert.Contains("triaged", result);
        Assert.Contains("high", result);
        Assert.Contains("Original desc", result); // description unchanged

        // Verify the store also reflects the changes (proves persistence + re-fetch).
        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal(IssueStatus.Triaged, issue!.Status);
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.Equal("Updated title", issue.Title);
        Assert.Equal("Original desc", issue.Description);
        Assert.NotNull(issue.UpdatedAt);
    }

    [Fact]
    public async Task UpdateIssue_SetsLinkedGoalId()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        var result = await _composer.UpdateIssueAsync(id, linked_goal_id: "some-goal", ct: ct);

        Assert.Contains("some-goal", result);

        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal("some-goal", issue!.LinkedGoalId);
    }

    [Fact]
    public async Task UpdateIssue_ClearsLinkedGoalId()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        // First set a linked goal, then clear it with an empty string.
        await _composer.UpdateIssueAsync(id, linked_goal_id: "existing-goal", ct: ct);

        var result = await _composer.UpdateIssueAsync(id, linked_goal_id: "", ct: ct);

        Assert.Contains("(none)", result);

        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Null(issue!.LinkedGoalId);
    }

    [Fact]
    public async Task UpdateIssue_PreservesLinkedGoalIdWhenNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _composer.CreateIssueAsync("bug", "Title", "Desc", ct: ct);
        var id = created.Replace("Issue created: ", "").Trim();

        // Set a linked goal first.
        await _composer.UpdateIssueAsync(id, linked_goal_id: "existing-goal", ct: ct);

        // Omitted (null) linked_goal_id must not change the existing value.
        var result = await _composer.UpdateIssueAsync(id, status: "triaged", ct: ct);

        Assert.Contains("existing-goal", result);

        var issue = await _issueStore.GetIssueAsync(id, ct);
        Assert.NotNull(issue);
        Assert.Equal("existing-goal", issue!.LinkedGoalId);
    }

    // ── tool registration & system prompt ──

    [Fact]
    public void IssueTools_RegisteredWhenIssueStorePresent()
    {
        var tools = _composer.BuildComposerTools();
        var names = tools.OfType<AIFunction>().Select(t => t.Name).ToList();

        Assert.Contains("create_issue", names);
        Assert.Contains("list_issues", names);
        Assert.Contains("get_issue", names);
        Assert.Contains("update_issue", names);
    }

    [Fact]
    public void IssueTools_NotRegisteredWhenIssueStoreNull()
    {
        var tools = _composerWithoutIssueStore.BuildComposerTools();
        var names = tools.OfType<AIFunction>().Select(t => t.Name).ToList();

        Assert.DoesNotContain("create_issue", names);
        Assert.DoesNotContain("list_issues", names);
        Assert.DoesNotContain("get_issue", names);
        Assert.DoesNotContain("update_issue", names);
    }

    [Fact]
    public void SystemPrompt_DocumentsIssueTools()
    {
        var prompt = _composer.GetSystemPrompt();

        Assert.Contains("create_issue", prompt);
        Assert.Contains("list_issues", prompt);
        Assert.Contains("get_issue", prompt);
        Assert.Contains("update_issue", prompt);
    }

    [Fact]
    public void CreateIssue_ToolHasDescriptionAttributes()
    {
        var tools = _composer.BuildComposerTools();
        var createIssue = tools.OfType<AIFunction>().Single(t => t.Name == "create_issue");

        Assert.NotNull(createIssue.Description);
        Assert.Contains("issue", createIssue.Description, StringComparison.OrdinalIgnoreCase);

        var parameters = createIssue.UnderlyingMethod!.GetParameters();
        var typeParam = parameters.Single(p => p.Name == "type");
        var titleParam = parameters.Single(p => p.Name == "title");
        var descriptionParam = parameters.Single(p => p.Name == "description");
        var severityParam = parameters.Single(p => p.Name == "severity");
        var repoParam = parameters.Single(p => p.Name == "repository_names");

        foreach (var p in new[] { typeParam, titleParam, descriptionParam, severityParam, repoParam })
        {
            var descriptionAttr = p.GetCustomAttributesData()
                .First(a => a.AttributeType.FullName == "System.ComponentModel.DescriptionAttribute");
            Assert.NotNull(descriptionAttr.ConstructorArguments[0].Value as string);
        }
    }
}

/// <summary>
/// Removal-proof concurrency and forwarding tests for the Composer's issue tools.
/// <para>
/// These tests deliberately avoid asserting only on caller-supplied values or on final
/// store state read back through a second query — assertions like that stay green even
/// when the production re-fetch, the duplicate-ID retry, or the update serialization is
/// deleted. Instead they use spy/capturing/gated <see cref="IIssueStore"/> fakes that
/// return <em>different</em> data than the caller supplied, record the exact
/// <see cref="CancellationToken"/> per call, and gate <c>GetIssueAsync</c> so two
/// <c>update_issue</c> calls genuinely overlap.
/// </para>
/// </summary>
public sealed class ComposerIssueToolConcurrencyTests
{
    private static Composer CreateComposer(IIssueStore issueStore, GoalStore goalStore) =>
        new(
            "test-model",
            NullLogger<Composer>.Instance,
            goalStore,
            stateDir: Path.GetTempPath(),
            issueStore: issueStore);

    // ── Test A1: create_issue end-to-end concurrent collision avoidance ──

    /// <summary>
    /// Two genuinely concurrent <c>create_issue</c> calls with the SAME title must both
    /// succeed and yield DISTINCT IDs. Backed by a real <see cref="IssueStore"/> over a
    /// file-based SQLite database so each EF context gets its own connection and the
    /// unique-primary-key constraint is really enforced.
    /// <para>
    /// Determinism: a rendezvous decorator holds BOTH callers inside the ID-probe
    /// <c>GetIssueAsync</c> until both have observed the slug as absent. Both therefore
    /// select the identical slug and both reach <c>CreateIssueAsync</c> with it, so
    /// SQLite's unique-key collision is guaranteed rather than scheduler-dependent —
    /// the second caller can no longer "legitimately" pick <c>…-2</c>.
    /// </para>
    /// <para>
    /// Removal-proof: with the production try/catch(<see cref="InvalidOperationException"/>)
    /// retry deleted, the losing insert propagates instead of returning
    /// "Issue created: …", so <c>Task.WhenAll</c> rethrows and this test fails.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreateIssue_TwoConcurrentCallsSameTitle_BothSucceedWithDistinctIds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempFileDbContextFactory();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();

        var realStore = new IssueStore(db, NullLogger<IssueStore>.Instance);

        // Hold both callers inside the ID probe until both have seen the slug absent.
        using var rendezvous = new ProbeRendezvousIssueStore(realStore, participants: 2);

        var goalStore = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        var composer = CreateComposer(rendezvous, goalStore);

        // Dedicated threads (LongRunning), not pool threads, so both callers are
        // guaranteed to be running simultaneously and can actually meet at the barrier.
        var callA = Task.Factory.StartNew(
            () => composer.CreateIssueAsync("bug", "Duplicate title race", "First report", ct: ct),
            ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        var callB = Task.Factory.StartNew(
            () => composer.CreateIssueAsync("bug", "Duplicate title race", "Second report", ct: ct),
            ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        var results = await Task.WhenAll(callA, callB).WaitAsync(TimeSpan.FromSeconds(30), ct);

        // Both probes really did observe the same absent slug…
        Assert.Equal(2, rendezvous.ProbeArrivals);

        // …and both really did attempt to insert under that identical slug, so the
        // duplicate-key collision was genuinely exercised (not sidestepped by "-2").
        Assert.Equal(2, rendezvous.CreateAttempts.Count(id => id == "duplicate-title-race"));

        // Exactly one insert lost the race and was retried by the production catch.
        Assert.Equal(1, rendezvous.DuplicateFailures);
        Assert.Equal(3, rendezvous.CreateAttempts.Count); // 2 colliding + 1 GUID retry

        Assert.All(results, r => Assert.StartsWith("Issue created: ", r));

        var ids = results.Select(r => r.Replace("Issue created: ", "").Trim()).ToList();
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());

        // The winner kept the slug; the loser was retried onto a GUID-based ID.
        Assert.Contains("duplicate-title-race", ids);
        var retriedId = Assert.Single(ids, id => id != "duplicate-title-race");
        Assert.StartsWith("issue-", retriedId);

        // Both issues must actually be persisted.
        foreach (var id in ids)
            Assert.NotNull(await realStore.GetIssueAsync(id, ct));

        var all = await realStore.GetIssuesAsync(ct: ct);
        Assert.Equal(2, all.Count);
    }

    // ── Test A2: create_issue duplicate-ID retry (deterministic) ──

    /// <summary>
    /// Deterministic proof of the duplicate-ID retry: the fake store throws
    /// <see cref="InvalidOperationException"/> on the FIRST <c>CreateIssueAsync</c>
    /// (simulating another writer inserting the probed ID first). The tool must retry
    /// with a GUID-based ID rather than surfacing the exception.
    /// </summary>
    [Fact]
    public async Task CreateIssue_DuplicateIdRace_RetriesWithGuidIdAndSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);

        var store = new RecordingIssueStore { ThrowOnCreateCount = 1 };
        var composer = CreateComposer(store, goalStore);

        var result = await composer.CreateIssueAsync(
            "code_quality", "Naming is inconsistent", "Details here",
            severity: "high", repository_names: ["repo-a", "repo-b"], ct: ct);

        Assert.StartsWith("Issue created: ", result);
        var id = result.Replace("Issue created: ", "").Trim();

        // Retried with a GUID-based ID — not the slug that lost the race.
        Assert.StartsWith("issue-", id);
        Assert.NotEqual("naming-is-inconsistent", id);
        Assert.Equal(32, id["issue-".Length..].Length);

        // Two create attempts were made: the losing one and the retry.
        Assert.Equal(2, store.CreateCalls.Count);

        // Every field is preserved on the retry.
        var retried = store.CreateCalls[^1];
        Assert.Equal(id, retried.Id);
        Assert.Equal(IssueType.CodeQuality, retried.Type);
        Assert.Equal("Naming is inconsistent", retried.Title);
        Assert.Equal("Details here", retried.Description);
        Assert.Equal(IssueSeverity.High, retried.Severity);
        Assert.Equal(IssueStatus.Open, retried.Status);
        Assert.Equal(["repo-a", "repo-b"], retried.RepositoryNames);
        Assert.Null(retried.SourceGoalId);
        Assert.Null(retried.SourceRole);
        Assert.Null(retried.SourceIteration);

        // The issue really landed in the store under the retried ID.
        Assert.NotNull(await store.GetIssueAsync(id, ct));
    }

    /// <summary>
    /// When the retry itself also collides, the exception must surface rather than being
    /// swallowed — the tool retries exactly once and does not loop forever.
    /// </summary>
    [Fact]
    public async Task CreateIssue_RetryAlsoCollides_PropagatesException()
    {
        var ct = TestContext.Current.CancellationToken;
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);

        var store = new RecordingIssueStore { ThrowOnCreateCount = int.MaxValue };
        var composer = CreateComposer(store, goalStore);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            composer.CreateIssueAsync("bug", "Always collides", "Details", ct: ct));

        Assert.Equal(2, store.CreateCalls.Count);
    }

    // ── Test B: update_issue re-fetch proof ──

    /// <summary>
    /// Removal-proof re-fetch test. The spy returns DIFFERENT data on the second
    /// <c>GetIssueAsync</c> (the re-fetch) than the caller ever supplied. The rendered
    /// details must reflect that server-side data, which is only possible if production
    /// really re-reads after the write. Deleting the re-fetch and returning
    /// <c>FormatIssueDetails(updatedIssue)</c> makes every assertion below fail.
    /// </summary>
    [Fact]
    public async Task UpdateIssue_ReturnsReFetchedData_NotLocallyConstructedIssue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);

        var store = new RecordingIssueStore();
        store.Seed(new Issue
        {
            Id = "refetch-proof",
            Type = IssueType.Bug,
            Title = "Original title",
            Description = "Original description",
            Severity = IssueSeverity.Low,
            Status = IssueStatus.Open,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        // The re-fetch (2nd GetIssueAsync) returns a server-side snapshot that shares the
        // ID but differs in every rendered field from anything the caller passed in.
        store.SecondGetResponse = new Issue
        {
            Id = "refetch-proof",
            Type = IssueType.Workflow,
            Title = "SERVER-SIDE-TITLE",
            Description = "SERVER-SIDE-DESCRIPTION",
            Severity = IssueSeverity.Medium,
            Status = IssueStatus.Closed,
            RepositoryNames = ["server-repo"],
            SourceGoalId = "server-goal",
            SourceRole = "server-role",
            SourceIteration = 7,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2025, 6, 6, 6, 6, 6, DateTimeKind.Utc),
            ResolvedAt = new DateTime(2025, 7, 7, 7, 7, 7, DateTimeKind.Utc),
            LinkedGoalId = "server-linked-goal",
        };

        var composer = CreateComposer(store, goalStore);

        var result = await composer.UpdateIssueAsync(
            "refetch-proof", status: "triaged", severity: "high", title: "CALLER-TITLE", ct: ct);

        // Production re-fetched: rendered output is the server snapshot…
        Assert.Contains("SERVER-SIDE-TITLE", result);
        Assert.Contains("SERVER-SIDE-DESCRIPTION", result);
        Assert.Contains("workflow", result);
        Assert.Contains("medium", result);
        Assert.Contains("closed", result);
        Assert.Contains("server-repo", result);
        Assert.Contains("server-goal", result);
        Assert.Contains("server-role", result);
        Assert.Contains("server-linked-goal", result);
        Assert.Contains("2025-06-06 06:06:06", result); // store-managed UpdatedAt
        Assert.Contains("2025-07-07 07:07:07", result); // store-managed ResolvedAt

        // …and NOT the locally constructed replacement entity.
        Assert.DoesNotContain("CALLER-TITLE", result);
        Assert.DoesNotContain("triaged", result);
        Assert.DoesNotContain("high", result);

        // Exactly one read before the write and one re-read after it.
        Assert.Equal(2, store.GetIssueCalls.Count);
        Assert.Single(store.UpdateCalls);
        Assert.Equal(["GetIssueAsync", "UpdateIssueAsync", "GetIssueAsync"], store.CallLog);

        // The write itself still carried the caller's values.
        Assert.Equal("CALLER-TITLE", store.UpdateCalls[0].Title);
        Assert.Equal(IssueStatus.Triaged, store.UpdateCalls[0].Status);
        Assert.Equal(IssueSeverity.High, store.UpdateCalls[0].Severity);
    }

    // ── Test C: CancellationToken forwarding ──

    /// <summary>
    /// <c>create_issue</c> must forward the caller's token to the ID-probe
    /// <c>GetIssueAsync</c> and to <c>CreateIssueAsync</c>. Uses a live (uncancelled)
    /// token from a real source so the assertion cannot pass vacuously with
    /// <see cref="CancellationToken.None"/>.
    /// </summary>
    [Fact]
    public async Task CreateIssue_ForwardsCallerCancellationTokenToEveryStoreCall()
    {
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var store = new RecordingIssueStore();
        var composer = CreateComposer(store, goalStore);

        var result = await composer.CreateIssueAsync("bug", "Token forwarding", "Details", ct: token);

        Assert.StartsWith("Issue created: ", result);

        Assert.NotEmpty(store.CapturedTokens);
        Assert.All(store.CapturedTokens, t =>
        {
            Assert.Equal(token, t);
            Assert.NotEqual(CancellationToken.None, t);
        });

        // The probe read and the create both happened, and both carried the token.
        Assert.NotEmpty(store.GetIssueCalls);
        Assert.NotEmpty(store.CreateCalls);
    }

    /// <summary>
    /// <c>update_issue</c> must forward the caller's token to the initial
    /// <c>GetIssueAsync</c>, to <c>UpdateIssueAsync</c>, and to the re-fetch
    /// <c>GetIssueAsync</c>.
    /// </summary>
    [Fact]
    public async Task UpdateIssue_ForwardsCallerCancellationTokenToReadWriteAndReFetch()
    {
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var store = new RecordingIssueStore();
        store.Seed(new Issue
        {
            Id = "token-update",
            Type = IssueType.Bug,
            Title = "Title",
            Description = "Description",
        });

        var composer = CreateComposer(store, goalStore);

        await composer.UpdateIssueAsync("token-update", status: "resolved", ct: token);

        Assert.Equal(["GetIssueAsync", "UpdateIssueAsync", "GetIssueAsync"], store.CallLog);
        Assert.Equal(3, store.CapturedTokens.Count);
        Assert.All(store.CapturedTokens, t =>
        {
            Assert.Equal(token, t);
            Assert.NotEqual(CancellationToken.None, t);
        });
    }

    /// <summary>
    /// <c>list_issues</c> and <c>get_issue</c> must forward the caller's token too.
    /// </summary>
    [Fact]
    public async Task ListAndGetIssue_ForwardCallerCancellationToken()
    {
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var store = new RecordingIssueStore();
        store.Seed(new Issue
        {
            Id = "token-read",
            Type = IssueType.Bug,
            Title = "Title",
            Description = "Description",
        });

        var composer = CreateComposer(store, goalStore);

        await composer.ListIssuesAsync(ct: token);
        await composer.GetIssueAsync("token-read", ct: token);

        Assert.Equal(["GetIssuesAsync", "GetIssueAsync"], store.CallLog);
        Assert.Equal(2, store.CapturedTokens.Count);
        Assert.All(store.CapturedTokens, t =>
        {
            Assert.Equal(token, t);
            Assert.NotEqual(CancellationToken.None, t);
        });
    }

    // ── Test D: update_issue serialized read-modify-write ──

    /// <summary>
    /// Removal-proof serialization test proving the lock spans the READ.
    /// <para>
    /// Determinism (no delays, no elapsed-time reasoning):
    /// call A is parked inside <c>UpdateIssueAsync</c> — i.e. it already holds the
    /// semaphore and has completed its read. Call B is then invoked <em>directly</em>
    /// (not via <c>Task.Run</c>): a C# async method runs synchronously up to its first
    /// INCOMPLETE await, so by the time <c>UpdateIssueAsync</c> returns its task to us,
    /// B has provably already reached <c>_issueUpdateLock.WaitAsync</c>. Its task being
    /// incomplete is therefore positive proof of lock contention — B is blocked on the
    /// semaphore, not merely unscheduled.
    /// </para>
    /// <para>
    /// Removal-proof: the ordered call-log assertion requires B's <c>GetIssueAsync</c> to
    /// come AFTER A's <c>UpdateIssueAsync</c>. With <c>_issueUpdateLock</c> deleted, B's
    /// read executes immediately while A is parked, producing
    /// <c>[Get, Get, Update, …]</c> instead of <c>[Get, Update, Get, Get, Update, Get]</c>,
    /// and B's stale full-replacement write also clobbers A's severity.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UpdateIssue_ConcurrentPartialUpdates_AreSerializedAndBothPersist()
    {
        var ct = TestContext.Current.CancellationToken;
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);

        // Gate the FIRST write: A holds the lock (having already read) while B contends.
        var store = new RecordingIssueStore { GateFirstUpdate = true };
        store.Seed(new Issue
        {
            Id = "serialize-me",
            Type = IssueType.Bug,
            Title = "Title",
            Description = "Description",
            Severity = IssueSeverity.Low,
            Status = IssueStatus.Open,
        });

        var composer = CreateComposer(store, goalStore);

        // Call A changes only the severity; call B changes only the status.
        var callA = composer.UpdateIssueAsync("serialize-me", severity: "high", ct: ct);

        // A is inside the critical section, parked in UpdateIssueAsync holding the lock.
        await store.FirstUpdateEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Single(store.GetIssueCalls); // A read; nobody else has.

        // Invoke B DIRECTLY (no Task.Run): it runs synchronously until its first
        // incomplete await, which is _issueUpdateLock.WaitAsync. Once this call returns,
        // B has definitively reached the contention point.
        var callB = composer.UpdateIssueAsync("serialize-me", status: "resolved", ct: ct);

        // B is blocked ON THE SEMAPHORE — proven structurally, not by elapsed time.
        Assert.False(callB.IsCompleted);

        // The lock spans the read: B could not read while A holds the critical section.
        Assert.Single(store.GetIssueCalls);

        // Let A commit; B then proceeds against A's committed state.
        store.ReleaseFirstUpdate();
        var results = await Task.WhenAll(callA, callB).WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.All(results, r => Assert.Contains("## Issue: serialize-me", r));

        // Neither writer clobbered the other: both partial updates survive.
        var final = await store.GetIssueAsync("serialize-me", ct);
        Assert.NotNull(final);
        Assert.Equal(IssueSeverity.High, final!.Severity);   // from call A
        Assert.Equal(IssueStatus.Resolved, final.Status);    // from call B

        // Untouched fields are preserved throughout.
        Assert.Equal("Title", final.Title);
        Assert.Equal("Description", final.Description);
        Assert.Equal(IssueType.Bug, final.Type);

        // Exact ordering proof: B's read lands AFTER A's write, never before it.
        Assert.Equal(2, store.UpdateCalls.Count);
        Assert.Equal(
            ["GetIssueAsync", "UpdateIssueAsync", "GetIssueAsync",   // call A: read, write, re-fetch
             "GetIssueAsync", "UpdateIssueAsync", "GetIssueAsync",   // call B: read, write, re-fetch
             "GetIssueAsync"],                                       // final assertion read
            store.CallLog);
    }

    /// <summary>
    /// The serialization lock must be released even when the update returns early through
    /// a validation failure, otherwise every later <c>update_issue</c> would deadlock.
    /// </summary>
    [Fact]
    public async Task UpdateIssue_ValidationFailure_StillReleasesLock()
    {
        var ct = TestContext.Current.CancellationToken;
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var goalStore = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);

        var store = new RecordingIssueStore();
        store.Seed(new Issue
        {
            Id = "lock-release",
            Type = IssueType.Bug,
            Title = "Title",
            Description = "Description",
        });

        var composer = CreateComposer(store, goalStore);

        // Invalid status → early return from inside the critical section.
        var failed = await composer.UpdateIssueAsync("lock-release", status: "bogus", ct: ct);
        Assert.Contains("Unknown status", failed);

        // Not-found → another early return from inside the critical section.
        var missing = await composer.UpdateIssueAsync("does-not-exist", status: "resolved", ct: ct);
        Assert.Equal("Issue 'does-not-exist' not found.", missing);

        // The lock was released each time, so a subsequent update still completes promptly.
        var ok = await composer
            .UpdateIssueAsync("lock-release", status: "resolved", ct: ct)
            .WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Contains("resolved", ok);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> backed by a temporary file-based SQLite
    /// database. A file (rather than <c>:memory:</c>) is required because each created context
    /// opens its OWN connection, which is what makes genuinely concurrent writes — and the
    /// resulting primary-key collisions — reachable in a test.
    /// </summary>
    private sealed class TempFileDbContextFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly string _path =
            Path.Combine(Path.GetTempPath(), $"copilothive-issues-{Guid.NewGuid():N}.db");

        public TempFileDbContextFactory()
        {
            using var ctx = CreateDbContext();
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={_path};Cache=Shared")
                .Options;
            return new CopilotHiveDbContext(options);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_path); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// In-memory <see cref="IIssueStore"/> spy that records every call (name, arguments and
    /// the exact <see cref="CancellationToken"/>), can fail a configurable number of
    /// <c>CreateIssueAsync</c> calls to simulate a duplicate-ID race, can return a
    /// different snapshot on the second <c>GetIssueAsync</c> to prove re-fetching, and can
    /// gate the first <c>GetIssueAsync</c> to force two updates to overlap.
    /// </summary>
    private sealed class RecordingIssueStore : IIssueStore
    {
        private readonly Dictionary<string, Issue> _issues = new(StringComparer.Ordinal);
        private readonly Lock _sync = new();

        /// <summary>Ordered log of store method names as they were invoked.</summary>
        public List<string> CallLog { get; } = [];

        /// <summary>Every <see cref="CancellationToken"/> the tools passed in, in call order.</summary>
        public List<CancellationToken> CapturedTokens { get; } = [];

        /// <summary>IDs passed to <c>GetIssueAsync</c>, in call order.</summary>
        public List<string> GetIssueCalls { get; } = [];

        /// <summary>Entities passed to <c>CreateIssueAsync</c>, in call order.</summary>
        public List<Issue> CreateCalls { get; } = [];

        /// <summary>Entities passed to <c>UpdateIssueAsync</c>, in call order.</summary>
        public List<Issue> UpdateCalls { get; } = [];

        /// <summary>Number of leading <c>CreateIssueAsync</c> calls that throw a duplicate error.</summary>
        public int ThrowOnCreateCount { get; init; }

        /// <summary>When set, the SECOND <c>GetIssueAsync</c> returns this instead of stored state.</summary>
        public Issue? SecondGetResponse { get; set; }

        /// <summary>When true, the first <c>GetIssueAsync</c> blocks until released.</summary>
        public bool GateFirstGet { get; init; }

        /// <summary>Completes once the first <c>GetIssueAsync</c> has been entered and parked.</summary>
        public TaskCompletionSource FirstGetEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _firstGetGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// When true, the first <c>UpdateIssueAsync</c> blocks until released. This parks the
        /// first caller inside the critical section AFTER it has completed its read, which is
        /// what lets a second caller contend for the lock at a deterministic point.
        /// </summary>
        public bool GateFirstUpdate { get; init; }

        /// <summary>Completes once the first <c>UpdateIssueAsync</c> has been entered and parked.</summary>
        public TaskCompletionSource FirstUpdateEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _firstUpdateGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _createAttempts;
        private int _getIssueAttempts;
        private int _updateAttempts;

        /// <summary>Releases a gated first <c>GetIssueAsync</c>.</summary>
        public void ReleaseFirstGet() => _firstGetGate.TrySetResult();

        /// <summary>Releases a gated first <c>UpdateIssueAsync</c>.</summary>
        public void ReleaseFirstUpdate() => _firstUpdateGate.TrySetResult();

        /// <summary>Pre-populates the store without recording a call.</summary>
        public void Seed(Issue issue)
        {
            lock (_sync)
                _issues[issue.Id] = Clone(issue);
        }

        private static Issue Clone(Issue source) => new()
        {
            Id = source.Id,
            Type = source.Type,
            Title = source.Title,
            Description = source.Description,
            Severity = source.Severity,
            Status = source.Status,
            RepositoryNames = [.. source.RepositoryNames],
            SourceGoalId = source.SourceGoalId,
            SourceRole = source.SourceRole,
            SourceIteration = source.SourceIteration,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            ResolvedAt = source.ResolvedAt,
            LinkedGoalId = source.LinkedGoalId,
        };

        private void Record(string method, CancellationToken ct)
        {
            lock (_sync)
            {
                CallLog.Add(method);
                CapturedTokens.Add(ct);
            }
        }

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
        {
            Record(nameof(GetAllIssuesAsync), ct);
            lock (_sync)
                return Task.FromResult<IReadOnlyList<Issue>>(_issues.Values.Select(Clone).ToList());
        }

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null,
            IssueType? type = null,
            IssueSeverity? severity = null,
            string? repository = null,
            string? sourceGoalId = null,
            string? linkedGoalId = null,
            CancellationToken ct = default)
        {
            Record(nameof(GetIssuesAsync), ct);
            lock (_sync)
            {
                var query = _issues.Values.AsEnumerable();
                if (status.HasValue) query = query.Where(i => i.Status == status.Value);
                if (type.HasValue) query = query.Where(i => i.Type == type.Value);
                if (severity.HasValue) query = query.Where(i => i.Severity == severity.Value);
                if (sourceGoalId is not null) query = query.Where(i => i.SourceGoalId == sourceGoalId);
                if (linkedGoalId is not null) query = query.Where(i => i.LinkedGoalId == linkedGoalId);
                if (repository is not null)
                {
                    query = query.Where(i => i.RepositoryNames
                        .Any(r => string.Equals(r, repository, StringComparison.OrdinalIgnoreCase)));
                }
                return Task.FromResult<IReadOnlyList<Issue>>(query.Select(Clone).ToList());
            }
        }

        public async Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
        {
            Record(nameof(GetIssueAsync), ct);

            int attempt;
            lock (_sync)
            {
                GetIssueCalls.Add(issueId);
                attempt = ++_getIssueAttempts;
            }

            if (GateFirstGet && attempt == 1)
            {
                FirstGetEntered.TrySetResult();
                await _firstGetGate.Task.WaitAsync(ct);
            }

            if (attempt == 2 && SecondGetResponse is not null)
                return Clone(SecondGetResponse);

            lock (_sync)
                return _issues.TryGetValue(issueId, out var found) ? Clone(found) : null;
        }

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Record(nameof(CreateIssueAsync), ct);

            int attempt;
            lock (_sync)
            {
                CreateCalls.Add(Clone(issue));
                attempt = ++_createAttempts;
            }

            if (attempt <= ThrowOnCreateCount)
            {
                throw new InvalidOperationException(
                    $"Failed to create issue '{issue.Id}'; an issue with the same ID may already exist.");
            }

            lock (_sync)
            {
                if (!_issues.TryAdd(issue.Id, Clone(issue)))
                {
                    throw new InvalidOperationException(
                        $"Failed to create issue '{issue.Id}'; an issue with the same ID may already exist.");
                }
            }

            return Task.FromResult(issue);
        }

        public async Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Record(nameof(UpdateIssueAsync), ct);

            int attempt;
            lock (_sync)
                attempt = ++_updateAttempts;

            // Park the first writer INSIDE the caller's critical section (it already holds
            // the production lock and has completed its read), so a second caller can be
            // observed contending for that lock at a deterministic point.
            if (GateFirstUpdate && attempt == 1)
            {
                FirstUpdateEntered.TrySetResult();
                await _firstUpdateGate.Task.WaitAsync(ct);
            }

            lock (_sync)
            {
                UpdateCalls.Add(Clone(issue));

                if (!_issues.TryGetValue(issue.Id, out var existing))
                    throw new InvalidOperationException($"Issue '{issue.Id}' not found in fake store.");

                // Mirror IssueStore: copy mutable fields, preserve immutable ones,
                // and manage the ResolvedAt / UpdatedAt transitions.
                var wasTerminal = existing.Status is IssueStatus.Resolved or IssueStatus.Closed;
                var isTerminal = issue.Status is IssueStatus.Resolved or IssueStatus.Closed;

                if (isTerminal && !wasTerminal) existing.ResolvedAt = DateTime.UtcNow;
                else if (!isTerminal) existing.ResolvedAt = null;

                existing.Type = issue.Type;
                existing.Title = issue.Title;
                existing.Description = issue.Description;
                existing.Severity = issue.Severity;
                existing.Status = issue.Status;
                existing.RepositoryNames = [.. issue.RepositoryNames];
                existing.LinkedGoalId = issue.LinkedGoalId;
                existing.UpdatedAt = DateTime.UtcNow;
            }
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
        {
            Record(nameof(DeleteIssueAsync), ct);
            lock (_sync)
                return Task.FromResult(_issues.Remove(issueId));
        }
    }

    /// <summary>
    /// Decorator over a REAL <see cref="IIssueStore"/> that forces a genuine duplicate-ID
    /// collision instead of leaving it to chance.
    /// <para>
    /// <see cref="IssueIdGenerator"/> probes candidate IDs via <c>GetIssueAsync</c> and
    /// picks the first absent one. Left unsynchronised, two callers usually run far enough
    /// apart that the second observes the first's row and legitimately selects
    /// <c>…-2</c> — no collision, and the production retry is never exercised. This
    /// decorator holds every probing caller at a <see cref="Barrier"/> until all
    /// participants have arrived, guaranteeing they all observe the same absent slug and
    /// all attempt to insert it.
    /// </para>
    /// </summary>
    private sealed class ProbeRendezvousIssueStore(IIssueStore inner, int participants)
        : IIssueStore, IDisposable
    {
        private readonly Barrier _probeBarrier = new(participants);
        private readonly Lock _sync = new();
        private int _probeArrivals;
        private int _duplicateFailures;

        /// <summary>Number of callers that met at the probe barrier.</summary>
        public int ProbeArrivals => Volatile.Read(ref _probeArrivals);

        /// <summary>Number of inserts rejected by the store's duplicate-ID guard.</summary>
        public int DuplicateFailures => Volatile.Read(ref _duplicateFailures);

        /// <summary>IDs passed to <c>CreateIssueAsync</c>, in call order.</summary>
        public List<string> CreateAttempts { get; } = [];

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default) =>
            inner.GetAllIssuesAsync(ct);

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null,
            IssueType? type = null,
            IssueSeverity? severity = null,
            string? repository = null,
            string? sourceGoalId = null,
            string? linkedGoalId = null,
            CancellationToken ct = default) =>
            inner.GetIssuesAsync(status, type, severity, repository, sourceGoalId, linkedGoalId, ct);

        public async Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
        {
            var result = await inner.GetIssueAsync(issueId, ct);

            // Only the first probe of each caller (the bare slug, still absent) rendezvouses;
            // barrier participants are finite, so later probes must not re-enter it.
            if (result is null && Interlocked.Increment(ref _probeArrivals) <= participants)
            {
                // Both callers now hold the identical "absent" answer for the same slug.
                _probeBarrier.SignalAndWait(ct);
            }

            return result;
        }

        public async Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            lock (_sync)
                CreateAttempts.Add(issue.Id);

            try
            {
                return await inner.CreateIssueAsync(issue, ct);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref _duplicateFailures);
                throw;
            }
        }

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default) =>
            inner.UpdateIssueAsync(issue, ct);

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default) =>
            inner.DeleteIssueAsync(issueId, ct);

        public void Dispose() => _probeBarrier.Dispose();
    }
}
