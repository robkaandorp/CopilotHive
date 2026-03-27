using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for <see cref="GoalDispatcher.CancelGoalAsync"/>.
/// </summary>
public sealed class GoalDispatcherCancelTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GoalDispatcher CreateDispatcher(
        GoalManager goalManager,
        GoalPipelineManager pipelineManager) =>
        new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, GoalManager goalManager, GoalPipelineManager pipelineManager, CancelFakeGoalSource goalSource)
        CreateInProgressDispatcher(GoalPhase phase = GoalPhase.Coding)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        // Populate goal→source map (valid in non-test helper methods)
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(phase);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var dispatcher = CreateDispatcher(goalManager, pipelineManager);
        return (dispatcher, pipeline, goalManager, pipelineManager, goalSource);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.Review)]
    [InlineData(GoalPhase.Merging)]
    public async Task CancelGoalAsync_InProgressPipeline_ReturnsTrue(GoalPhase phase)
    {
        var (dispatcher, pipeline, _, pipelineManager, _) = CreateInProgressDispatcher(phase);

        var result = await dispatcher.CancelGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.Review)]
    public async Task CancelGoalAsync_InProgressPipeline_RemovesPipelineFromManager(GoalPhase phase)
    {
        var (dispatcher, pipeline, _, pipelineManager, _) = CreateInProgressDispatcher(phase);
        var goalId = pipeline.GoalId;

        await dispatcher.CancelGoalAsync(goalId, TestContext.Current.CancellationToken);

        Assert.Null(pipelineManager.GetByGoalId(goalId));
    }

    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.Review)]
    public async Task CancelGoalAsync_InProgressPipeline_MarksPipelineAsFailed(GoalPhase phase)
    {
        var (dispatcher, pipeline, _, _, _) = CreateInProgressDispatcher(phase);

        await dispatcher.CancelGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.Review)]
    public async Task CancelGoalAsync_InProgressPipeline_UpdatesGoalStatusToFailed(GoalPhase phase)
    {
        var (dispatcher, pipeline, _, _, goalSource) = CreateInProgressDispatcher(phase);

        await dispatcher.CancelGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);

        Assert.Equal(GoalStatus.Failed, goalSource.LastUpdatedStatus);
        Assert.Equal("Cancelled by user", goalSource.LastUpdatedReason);
    }

    [Fact]
    public async Task CancelGoalAsync_AlreadyDonePipeline_ReturnsFalse()
    {
        var (dispatcher, pipeline, _, _, _) = CreateInProgressDispatcher(GoalPhase.Done);

        var result = await dispatcher.CancelGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CancelGoalAsync_AlreadyFailedPipeline_ReturnsFalse()
    {
        var (dispatcher, pipeline, _, _, _) = CreateInProgressDispatcher(GoalPhase.Failed);

        var result = await dispatcher.CancelGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CancelGoalAsync_PendingGoalNoPipeline_ReturnsTrue()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Pending goal" };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        // Populate goal→source map
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var dispatcher = CreateDispatcher(goalManager, pipelineManager);

        var result = await dispatcher.CancelGoalAsync(goal.Id, TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task CancelGoalAsync_PendingGoalNoPipeline_UpdatesStatusToFailed()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Pending goal" };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var dispatcher = CreateDispatcher(goalManager, pipelineManager);

        await dispatcher.CancelGoalAsync(goal.Id, TestContext.Current.CancellationToken);

        Assert.Equal(GoalStatus.Failed, goalSource.LastUpdatedStatus);
        Assert.Equal("Cancelled by user", goalSource.LastUpdatedReason);
    }

    [Fact]
    public async Task CancelGoalAsync_CompletedGoalNoPipeline_ReturnsFalse()
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Completed goal",
            Status = GoalStatus.Completed
        };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var pipelineManager = new GoalPipelineManager();
        var dispatcher = CreateDispatcher(goalManager, pipelineManager);

        var result = await dispatcher.CancelGoalAsync(goal.Id, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CancelGoalAsync_FailedGoalNoPipeline_ReturnsFalse()
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Failed goal",
            Status = GoalStatus.Failed
        };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var pipelineManager = new GoalPipelineManager();
        var dispatcher = CreateDispatcher(goalManager, pipelineManager);

        var result = await dispatcher.CancelGoalAsync(goal.Id, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CancelGoalAsync_UnknownGoalId_ReturnsFalse()
    {
        var goalManager = new GoalManager();
        var pipelineManager = new GoalPipelineManager();
        var dispatcher = CreateDispatcher(goalManager, pipelineManager);

        var result = await dispatcher.CancelGoalAsync("nonexistent-goal", TestContext.Current.CancellationToken);

        Assert.False(result);
    }
}

/// <summary>
/// Minimal <see cref="IGoalSource"/> and <see cref="IGoalStore"/> used by cancellation tests.
/// Tracks last status update for assertion.
/// </summary>
internal sealed class CancelFakeGoalSource : IGoalSource, IGoalStore
{
    private readonly Goal _goal;

    public CancelFakeGoalSource(Goal goal) => _goal = goal;

    public string Name => "cancel-fake";

    public GoalStatus? LastUpdatedStatus { get; private set; }
    public string? LastUpdatedReason { get; private set; }

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
    {
        if (_goal.Status == GoalStatus.Pending)
            return Task.FromResult<IReadOnlyList<Goal>>([_goal]);
        return Task.FromResult<IReadOnlyList<Goal>>([]);
    }

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        if (goalId == _goal.Id)
        {
            LastUpdatedStatus = status;
            LastUpdatedReason = metadata?.FailureReason;
            _goal.Status = status;
        }
        return Task.CompletedTask;
    }

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<Goal?>(_goal.Id == goalId ? _goal : null);

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goal.Status == status ? new List<Goal> { _goal } : []);

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
}
