using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for <see cref="GoalDispatcher.CancelGoalAsync"/>.
/// </summary>
public sealed class GoalDispatcherCancelTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GoalDispatcher CreateDispatcher(
        GoalManager goalManager,
        GoalPipelineManager pipelineManager,
        DashboardNotifier? dashboardNotifier = null) =>
        new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            dashboardNotifier: dashboardNotifier);

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

        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var dispatcher = CreateDispatcher(goalManager, pipelineManager, notifier);
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
        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var dispatcher = CreateDispatcher(goalManager, pipelineManager, notifier);

        await dispatcher.CancelGoalAsync(goal.Id, TestContext.Current.CancellationToken);

        Assert.Equal(GoalStatus.Failed, goalSource.LastUpdatedStatus);
        Assert.Equal("Cancelled by user", goalSource.LastUpdatedReason);
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public async Task CancelGoalAsync_InProgressPipeline_NotifiesDashboardOnce()
    {
        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipelineManager.RegisterTask($"task-{Guid.NewGuid():N}", goal.Id);

        var dispatcher = CreateDispatcher(goalManager, pipelineManager, notifier);

        await dispatcher.CancelGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);

        Assert.Equal(GoalStatus.Failed, goalSource.LastUpdatedStatus);
        Assert.Equal(1, notificationCount);
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
    public async Task CancelGoalAsync_DraftGoalNoPipeline_ReturnsFalse()
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Draft goal",
            Status = GoalStatus.Draft
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
    public async Task CancelGoalAsync_CancelledGoalNoPipeline_ReturnsFalse()
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Cancelled goal",
            Status = GoalStatus.Cancelled
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

    private static Task InvokeDispatchNextGoalAsync(GoalDispatcher dispatcher, CancellationToken ct)
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "DispatchNextGoalAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(dispatcher, [ct])!;
    }

    [Fact]
    public async Task DispatchNextGoalAsync_SkipsGoalThatAlreadyHasPipeline()
    {
        // Arrange: pending goal that already has a pipeline. MaxParallelGoals is set high
        // so the parallelism gate does NOT block, forcing the method to reach the
        // GetByGoalId guard. Without that guard the goal would be dispatched.
        var ct = TestContext.Current.CancellationToken;
        var logger = new RetryStateCollectingLogger<GoalDispatcher>();
        var goal = new Goal
        {
            Id = $"goal-skip-{Guid.NewGuid():N}",
            Description = "Skip test",
            Status = GoalStatus.Pending,
            RepositoryNames = ["test-repo"]
        };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { MaxParallelGoals = 5 },
            Repositories =
            [
                new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/test-repo", DefaultBranch = "main" }
            ],
        };

        var pipelineManager = new GoalPipelineManager();
        // Pre-create a pipeline for this goal — simulates an already-dispatched goal.
        var existingPipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        existingPipeline.AdvanceTo(GoalPhase.Coding);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            config: config,
            startupDelay: TimeSpan.Zero);

        // Act: call DispatchNextGoalAsync directly — should skip because pipeline already exists.
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Assert: no dispatch log (the GetByGoalId guard prevented dispatch).
        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains($"Dispatching goal '{goal.Id}'"));
        // Assert: goal still Pending (dispatch did not proceed).
        Assert.Equal(GoalStatus.Pending, goal.Status);
        // Assert: exactly one pipeline exists and it is the pre-existing one.
        Assert.Single(pipelineManager.GetActivePipelines());
        Assert.Same(existingPipeline, pipelineManager.GetByGoalId(goal.Id));
    }

    [Fact]
    public async Task ResumeGoalAsync_UsesSingleGlobalSemaphore()
    {
        // Verify that the dispatcher uses one shared SemaphoreSlim instance for all goals.
        var dispatcher = new GoalDispatcher(
            new GoalManager(),
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            goalStore: new ResumeFakeGoalStore(
                new Goal { Id = "g1", Description = "g1", Status = GoalStatus.Failed, FailureReason = "Exceeded max iterations" },
                new Goal { Id = "g2", Description = "g2", Status = GoalStatus.Failed, FailureReason = "Exceeded max iterations" }));

        var resumeLockField = typeof(GoalDispatcher).GetField("_resumeLock", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(resumeLockField);
        var resumeLock = Assert.IsType<SemaphoreSlim>(resumeLockField!.GetValue(dispatcher));

        Assert.Equal(1, resumeLock.CurrentCount);

        // If the lock is held, a second WaitAsync should not be able to enter immediately.
        await resumeLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var entered = await resumeLock.WaitAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            Assert.False(entered, "Global resume lock should serialize concurrent resume attempts");
        }
        finally
        {
            resumeLock.Release();
        }
    }

    [Fact]
    public async Task ResumeGoalAsync_TwoDifferentGoalsConcurrently_SerializedByGlobalLock()
    {
        // Two different goal IDs resumed concurrently — exactly one runs at a time.
        // We use a TaskCompletionSource to block the first resume inside the lock,
        // then start the second resume, verify it blocks, complete the first,
        // and verify the second proceeds.
        var goal1 = new Goal { Id = "g-concurrent-1", Description = "g1", Status = GoalStatus.Failed, FailureReason = "Exceeded max iterations" };
        var goal2 = new Goal { Id = "g-concurrent-2", Description = "g2", Status = GoalStatus.Failed, FailureReason = "Exceeded max iterations" };
        var goalStore = new ResumeFakeGoalStore(goal1, goal2);

        var pipelineManager = new GoalPipelineManager();
        // Create pipelines in Failed phase so ResumeGoalAsync can proceed
        var pipeline1 = pipelineManager.CreatePipeline(goal1, maxRetries: 3, maxIterations: 5);
        pipeline1.AdvanceTo(GoalPhase.Failed);
        var pipeline2 = pipelineManager.CreatePipeline(goal2, maxRetries: 3, maxIterations: 5);
        pipeline2.AdvanceTo(GoalPhase.Failed);

        var dispatcher = new GoalDispatcher(
            new GoalManager(),
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            // A Brain is required to plan the resumed iteration — without one, resume fails the goal.
            brain: new RetryStateFakeBrain(),
            goalStore: goalStore);

        // TCS to block the first resume while it holds the lock.
        // The first GetGoalAsync inside the lock (line 252) will await this TCS.
        var firstResumeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGoalReentered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResumeCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Track which goal is calling GetGoalAsync
        var getGoalCallCount = 0;

        goalStore.OnGetGoalAsync = async (goal, ct) =>
        {
            var currentCount = Interlocked.Increment(ref getGoalCallCount);
            if (currentCount == 2 && goal.Id == goal1.Id)
            {
                // This is the re-check inside the lock for goal1 (second call overall)
                firstGoalReentered.SetResult(true);
                await firstResumeGate.Task; // block goal1 inside the lock
            }
        };

        var ct = TestContext.Current.CancellationToken;

        // Start resume for goal1 — it should acquire the lock and block inside
        var resume1Task = dispatcher.ResumeGoalAsync(goal1.Id, additionalIterations: 5, ct);

        // Wait until goal1's re-check inside the lock has started
        await firstGoalReentered.Task;

        // Start resume for goal2 — it should block waiting for the global lock
        var resume2Task = dispatcher.ResumeGoalAsync(goal2.Id, additionalIterations: 5, ct);

        // Verify goal2 has NOT completed — it's blocked on the lock held by goal1
        var resume2Done = await Task.WhenAny(resume2Task, Task.Delay(200, ct));
        Assert.NotSame(resume2Task, resume2Done);

        // Now release goal1 — it should complete
        firstResumeGate.SetResult(true);
        await resume1Task;

        // goal2 should now proceed and complete
        var resume2Final = await Task.WhenAny(resume2Task, Task.Delay(2000, ct));
        Assert.Same(resume2Task, resume2Final);

        // Both resumes should have returned (true = resumed, or false = no-op, but not thrown)
        Assert.True(resume1Task.IsCompleted);
        Assert.True(resume2Task.IsCompleted);
    }
}

/// <summary>
/// Tests for <see cref="GoalDispatcher.ClearGoalRetryState"/>.
/// </summary>
public sealed class GoalDispatcherClearRetryStateTests
{
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

    [Fact]
    public void ClearGoalRetryState_WithActivePipeline_RemovesPipelineFromManager()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Retry goal" };
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Failed);

        var goalManager = new GoalManager();
        var dispatcher = CreateDispatcher(goalManager, pipelineManager);

        dispatcher.ClearGoalRetryState(goal.Id);

        Assert.Null(pipelineManager.GetByGoalId(goal.Id));
    }

    [Fact]
    public void ClearGoalRetryState_WithActivePipeline_AllowsGoalToBeDispatchedAgain()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Retry goal" };
        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Failed);

        var goalManager = new GoalManager();
        var dispatcher = CreateDispatcher(goalManager, pipelineManager);

        // Simulate that the goal was previously dispatched by creating a pipeline.
        // We verify indirectly that the pipeline was removed (state is clear for re-dispatch).
        dispatcher.ClearGoalRetryState(goal.Id);

        // After clearing, no pipeline exists for the goal
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));
    }

    [Fact]
    public void ClearGoalRetryState_NoPipeline_DoesNotThrow()
    {
        var goalManager = new GoalManager();
        var pipelineManager = new GoalPipelineManager();
        var dispatcher = CreateDispatcher(goalManager, pipelineManager);

        // Should not throw even when the goal has no pipeline
        var ex = Record.Exception(() => dispatcher.ClearGoalRetryState("nonexistent-goal"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task ClearGoalRetryState_AfterActualDispatch_AllowsGoalToBeRedispatched()
    {
        // This test proves that ClearGoalRetryState removes the stale pipeline so the
        // goal can be dispatched again. The pipeline manager is the source of truth for
        // whether a goal is already dispatched, so we must run the background service loop
        // to create a pipeline, then verify that after ClearGoalRetryState the same
        // dispatcher can dispatch the goal again.
        //
        // Without clearing the stale pipeline, the second dispatch loop would silently return
        // early at the GetByGoalId guard in DispatchNextGoalAsync, and no second pipeline would form.
        var logger = new RetryStateCollectingLogger<GoalDispatcher>();
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Retry goal", Status = GoalStatus.Pending };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var pipelineManager = new GoalPipelineManager();

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            // A Brain is required to plan the goal — without one, dispatch fails the goal.
            brain: new RetryStateFakeBrain(),
            startupDelay: TimeSpan.Zero);

        // Act 1: Run the background service so DispatchNextGoalAsync executes and
        // creates a pipeline for the goal in the pipeline manager. The goal becomes InProgress after dispatch.
        using var cts1 = new CancellationTokenSource();
        using var linked1 = CancellationTokenSource.CreateLinkedTokenSource(
            cts1.Token, TestContext.Current.CancellationToken);
        var task1 = dispatcher.StartAsync(linked1.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        cts1.Cancel();
        await Task.WhenAny(task1, Task.Delay(1000, TestContext.Current.CancellationToken));

        // Assert: pipeline was created — this proves the pipeline manager tracks dispatched goals
        // and DispatchNextGoalAsync's GetByGoalId guard allowed the dispatch.
        var pipelineAfterFirstDispatch = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipelineAfterFirstDispatch);
        var firstDispatchLogs = logger.Logs.Count(l => l.Message.Contains($"Dispatching goal '{goal.Id}'"));
        Assert.Equal(1, firstDispatchLogs);

        // Act 2: Clear retry state — removes the stale pipeline.
        dispatcher.ClearGoalRetryState(goal.Id);
        goalSource.ResetForRequeue(); // Goal becomes Pending again for re-dispatch.

        // Assert: pipeline was removed by ClearGoalRetryState.
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));

        // Act 3: Run the SAME dispatcher instance again. Because the stale pipeline was cleared,
        // GetByGoalId in DispatchNextGoalAsync returns null and the goal is dispatched a second time.
        // Without ClearGoalRetryState, GetByGoalId would find the existing pipeline and skip dispatch.
        using var cts2 = new CancellationTokenSource();
        using var linked2 = CancellationTokenSource.CreateLinkedTokenSource(
            cts2.Token, TestContext.Current.CancellationToken);
        var task2 = dispatcher.StartAsync(linked2.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        cts2.Cancel();
        await Task.WhenAny(task2, Task.Delay(1000, TestContext.Current.CancellationToken));

        // Assert: goal was dispatched a second time — proving the stale pipeline was cleared.
        var totalDispatchLogs = logger.Logs.Count(l => l.Message.Contains($"Dispatching goal '{goal.Id}'"));
        Assert.Equal(2, totalDispatchLogs);
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

    /// <summary>
    /// Resets the goal status to Pending so GetPendingGoalsAsync returns it again.
    /// Used to simulate re-queuing after ClearGoalRetryState.
    /// </summary>
    public void ResetForRequeue() => _goal.Status = GoalStatus.Pending;
}

/// <summary>
/// Minimal <see cref="IGoalStore"/> that lets tests observe and control when
/// GoalDispatcher.ResumeGoalAsync re-reads a goal inside the global resume lock.
/// </summary>
internal sealed class ResumeFakeGoalStore : IGoalStore
{
    private readonly Goal _goal1;
    private readonly Goal _goal2;

    public ResumeFakeGoalStore(Goal goal1, Goal goal2)
    {
        _goal1 = goal1;
        _goal2 = goal2;
    }

    public Func<Goal, CancellationToken, Task>? OnGetGoalAsync { get; set; }

    public string Name => "resume-fake";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        if (goalId == _goal1.Id)
            _goal1.Status = status;
        else if (goalId == _goal2.Id)
            _goal2.Status = status;
        return Task.CompletedTask;
    }

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
    {
        var goal = goalId == _goal1.Id ? _goal1 : goalId == _goal2.Id ? _goal2 : null;
        if (goal is null)
            return Task.FromResult<Goal?>(null);

        return InvokeHookAsync(goal, ct).ContinueWith(
            _ => Task.FromResult<Goal?>(goal),
            TaskContinuationOptions.ExecuteSynchronously).Unwrap();
    }

    private Task InvokeHookAsync(Goal goal, CancellationToken ct)
    {
        if (OnGetGoalAsync is not null)
            return OnGetGoalAsync(goal, ct);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal1, _goal2]);

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

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
/// Thread-safe logger that collects log messages for assertion in
/// <see cref="GoalDispatcherClearRetryStateTests"/>.
/// </summary>
internal sealed class RetryStateCollectingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _logs = [];
    private readonly Lock _lock = new();

    /// <summary>All log messages collected so far.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Logs
    {
        get { lock (_lock) { return [.. _logs]; } }
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
        {
            _logs.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// Tests for verifying that session files are deleted when goals are cancelled
/// or when retry state is cleared.
/// </summary>
public sealed class GoalDispatcherSessionCleanupTests
{
    /// <summary>
    /// Fake brain that tracks DeleteGoalSession calls for verification.
    /// </summary>
    private sealed class SessionTrackingBrain : IDistributedBrain
    {
        public System.Collections.Concurrent.ConcurrentBag<string> DeletedSessionGoalIds { get; } = [];

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            UpdateModelAsync(model, maxContextTokens, ct);

        public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("Brain is not available. Please proceed with your best judgment."));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default)
        {
            DeletedSessionGoalIds.Add(goalId);
            return Task.CompletedTask;
        }

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }

    // ── Test 1: CancelGoalAsync deletes session file ─────────────────────────────

    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.Review)]
    public async Task CancelGoalAsync_InProgressPipeline_DeletesGoalSession(GoalPhase phase)
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SessionTrackingBrain();
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(ct); // populate internal map

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(phase);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain);

        var result = await dispatcher.CancelGoalAsync(goal.Id, ct);

        Assert.True(result);
        Assert.Contains(goal.Id, brain.DeletedSessionGoalIds);
    }

    [Fact]
    public async Task CancelGoalAsync_PendingGoalNoPipeline_DeletesGoalSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SessionTrackingBrain();
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Pending goal", Status = GoalStatus.Pending };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(ct);

        var pipelineManager = new GoalPipelineManager();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain);

        var result = await dispatcher.CancelGoalAsync(goal.Id, ct);

        Assert.True(result);
        Assert.Contains(goal.Id, brain.DeletedSessionGoalIds);
    }

    // ── Test 2: ClearGoalRetryState deletes session file ─────────────────────────

    [Fact]
    public async Task ClearGoalRetryState_WithBrain_DeletesGoalSession()
    {
        var brain = new SessionTrackingBrain();
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Retry goal" };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Failed);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain);

        dispatcher.ClearGoalRetryState(goal.Id);

        // ClearGoalRetryState deletes the goal session on a background task (fire-and-forget), so
        // poll for the observable effect with a tight deadline rather than asserting immediately.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!brain.DeletedSessionGoalIds.Contains(goal.Id) && DateTime.UtcNow < deadline)
            await Task.Delay(25, TestContext.Current.CancellationToken);

        Assert.Contains(goal.Id, brain.DeletedSessionGoalIds);
    }

    [Fact]
    public void ClearGoalRetryState_NoBrain_DoesNotThrow()
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Retry goal" };
        var goalSource = new CancelFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Failed);

        // Dispatcher WITHOUT a brain
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        // Should not throw
        var ex = Record.Exception(() => dispatcher.ClearGoalRetryState(goal.Id));
        Assert.Null(ex);
    }

    // ── Test 3: Orphaned sessions cleanup on startup ────────────────────────────

    [Fact]
    public async Task RestoreActivePipelinesAsync_DeletesOrphanedSessionFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        // Create a temporary directory for the test
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-sessions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var activeGoalId = $"goal-active-{Guid.NewGuid():N}";
            var orphanedGoalId1 = $"goal-orphaned-1-{Guid.NewGuid():N}";
            var orphanedGoalId2 = $"goal-orphaned-2-{Guid.NewGuid():N}";

            // Create fake session files in the brain's state directory
            var activeSessionFile = Path.Combine(tempDir, $"brain-goal-{activeGoalId}.json");
            var orphanedSessionFile1 = Path.Combine(tempDir, $"brain-goal-{orphanedGoalId1}.json");
            var orphanedSessionFile2 = Path.Combine(tempDir, $"brain-goal-{orphanedGoalId2}.json");

            await File.WriteAllTextAsync(activeSessionFile, "{}", ct);
            await File.WriteAllTextAsync(orphanedSessionFile1, "{}", ct);
            await File.WriteAllTextAsync(orphanedSessionFile2, "{}", ct);

            // Create a pipeline manager and store with an active pipeline
            var goal = new Goal { Id = activeGoalId, Description = "Active goal" };
            var goalSource = new CancelFakeGoalSource(goal);
            var goalManager = new GoalManager();
            goalManager.AddSource(goalSource);
            await goalManager.GetNextGoalAsync(ct);

            // PipelineStore needs a file path, not just the directory
            var dbPath = Path.Combine(tempDir, "pipelines.db");
            var pipelineStore = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
            var pipelineManager = new GoalPipelineManager(pipelineStore);
            var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
            pipeline.AdvanceTo(GoalPhase.Coding);
            // Persist the phase change to the store so it can be restored
            pipelineManager.PersistState(pipeline);

            // Create a new pipeline manager that will restore from the store
            var restoredPipelineManager = new GoalPipelineManager(pipelineStore);

            // Create a GoalDispatcher with a real DistributedBrain using the temp directory.
            // Inject a fake chat client (and factory) so the Brain never touches the process-global
            // ChatClientFactory token provider — that provider can be disposed by a sibling test,
            // causing intermittent ObjectDisposedException failures. Isolating it makes this
            // restoration test deterministic.
            var brain = new DistributedBrain(
                "copilot/claude-sonnet-4",
                NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir,
                chatClient: new FakeChatClient(),
                chatClientFactory: _ => new FakeChatClient());

            // RestoreActivePipelinesAsync calls RegisterExistingGoalSessionAsync, which requires a
            // connected Brain (EnsureConnected). Connect before invoking the restore path.
            await brain.ConnectAsync(ct);

            var dispatcher = new GoalDispatcher(
                goalManager,
                restoredPipelineManager,
                new TaskQueue(),
                new GrpcWorkerGateway(new WorkerPool()),
                new TaskCompletionNotifier(),
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
                brain);

            // Use reflection to call RestoreActivePipelinesAsync
            var restoreMethod = typeof(GoalDispatcher).GetMethod(
                "RestoreActivePipelinesAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            await (Task)restoreMethod.Invoke(dispatcher, [ct])!;

            // Assert: orphaned files are deleted, active file remains
            Assert.True(File.Exists(activeSessionFile), "Active goal session file should NOT be deleted");
            Assert.False(File.Exists(orphanedSessionFile1), "Orphaned session file 1 should be deleted");
            Assert.False(File.Exists(orphanedSessionFile2), "Orphaned session file 2 should be deleted");

            // Cleanup
            await pipelineStore.DisposeAsync();
        }
        finally
        {
            // Cleanup temp directory
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch { /* ignore cleanup failures */ }
            }
        }
    }

    [Fact]
    public async Task RestoreActivePipelinesAsync_WithNoActivePipelines_DeletesOrphanedSessionFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-sessions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create orphaned session files (no matching active pipeline)
            var orphanedGoalId1 = $"goal-orphaned-1-{Guid.NewGuid():N}";
            var orphanedGoalId2 = $"goal-orphaned-2-{Guid.NewGuid():N}";
            var orphanedSessionFile1 = Path.Combine(tempDir, $"brain-goal-{orphanedGoalId1}.json");
            var orphanedSessionFile2 = Path.Combine(tempDir, $"brain-goal-{orphanedGoalId2}.json");

            await File.WriteAllTextAsync(orphanedSessionFile1, "{}", ct);
            await File.WriteAllTextAsync(orphanedSessionFile2, "{}", ct);

            // Pipeline manager with NO pipelines stored - RestoreFromStore returns empty
            var dbPath = Path.Combine(tempDir, "pipelines.db");
            var pipelineStore = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
            var pipelineManager = new GoalPipelineManager(pipelineStore);

            var brain = new DistributedBrain(
                "copilot/claude-sonnet-4",
                NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir);

            var goalManager = new GoalManager();

            var dispatcher = new GoalDispatcher(
                goalManager,
                pipelineManager,
                new TaskQueue(),
                new GrpcWorkerGateway(new WorkerPool()),
                new TaskCompletionNotifier(),
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
                brain);

            var restoreMethod = typeof(GoalDispatcher).GetMethod(
                "RestoreActivePipelinesAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            await (Task)restoreMethod.Invoke(dispatcher, [ct])!;

            // When there are NO active pipelines, RestoreActivePipelinesAsync still calls
            // CleanupOrphanedGoalSessionsAsync before returning early. Since the active set
            // is empty, both session files are orphans and are deleted.
            Assert.False(File.Exists(orphanedSessionFile1), "Orphaned session file should be deleted");
            Assert.False(File.Exists(orphanedSessionFile2), "Orphaned session file should be deleted");

            await pipelineStore.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch { /* ignore cleanup failures */ }
            }
        }
    }

    [Fact]
    public async Task RestoreActivePipelinesAsync_NoBrain_SkipsCleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-sessions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var orphanedGoalId = $"goal-orphaned-{Guid.NewGuid():N}";
            var orphanedSessionFile = Path.Combine(tempDir, $"brain-goal-{orphanedGoalId}.json");
            await File.WriteAllTextAsync(orphanedSessionFile, "{}", ct);

            var pipelineManager = new GoalPipelineManager();
            var goalManager = new GoalManager();

            // Dispatcher WITHOUT a brain
            var dispatcher = new GoalDispatcher(
                goalManager,
                pipelineManager,
                new TaskQueue(),
                new GrpcWorkerGateway(new WorkerPool()),
                new TaskCompletionNotifier(),
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

            var restoreMethod = typeof(GoalDispatcher).GetMethod(
                "RestoreActivePipelinesAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            await (Task)restoreMethod.Invoke(dispatcher, [ct])!;

            // Session files remain because there's no brain to clean up with
            Assert.True(File.Exists(orphanedSessionFile), "Session files should remain when no brain is configured");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch { /* ignore cleanup failures */ }
            }
        }
    }
}

/// <summary>
/// Minimal <see cref="IDistributedBrain"/> that always returns a valid plan.
/// A Brain is mandatory for dispatch: without one the goal is failed instead of dispatched.
/// </summary>
file sealed class RetryStateFakeBrain : IDistributedBrain
{
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
        UpdateModelAsync(model, maxContextTokens, ct);

    public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) => Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PlanResult.Success(IterationPlan.Default()));

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) => Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("Proceed."));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}
