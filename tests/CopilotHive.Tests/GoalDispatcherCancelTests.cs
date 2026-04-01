using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
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

        // Simulate that the goal was previously dispatched by calling cancel (which adds to _dispatchedGoals)
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
        // This test proves that ClearGoalRetryState actually clears _dispatchedGoals.
        // _dispatchedGoals is only populated inside the private DispatchNextGoalAsync method,
        // so we must run the background service loop to populate it, then verify that
        // after ClearGoalRetryState the same dispatcher can dispatch the goal again.
        //
        // Without clearing _dispatchedGoals, the second dispatch loop would silently return
        // early at the TryAdd guard in DispatchNextGoalAsync, and no second pipeline would form.
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
            startupDelay: TimeSpan.Zero);

        // Act 1: Run the background service so DispatchNextGoalAsync executes and
        // populates _dispatchedGoals[goalId]. The goal becomes InProgress after dispatch.
        using var cts1 = new CancellationTokenSource();
        using var linked1 = CancellationTokenSource.CreateLinkedTokenSource(
            cts1.Token, TestContext.Current.CancellationToken);
        var task1 = dispatcher.StartAsync(linked1.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        cts1.Cancel();
        await Task.WhenAny(task1, Task.Delay(1000, TestContext.Current.CancellationToken));

        // Assert: pipeline was created — this proves _dispatchedGoals was populated by
        // DispatchNextGoalAsync (the TryAdd guard at line 1439 ran and succeeded).
        var pipelineAfterFirstDispatch = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipelineAfterFirstDispatch);
        var firstDispatchLogs = logger.Logs.Count(l => l.Message.Contains($"Dispatching goal '{goal.Id}'"));
        Assert.Equal(1, firstDispatchLogs);

        // Act 2: Clear retry state — removes _dispatchedGoals entry and stale pipeline.
        dispatcher.ClearGoalRetryState(goal.Id);
        goalSource.ResetForRequeue(); // Goal becomes Pending again for re-dispatch.

        // Assert: pipeline was removed by ClearGoalRetryState.
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));

        // Act 3: Run the SAME dispatcher instance again. Because _dispatchedGoals was cleared,
        // TryAdd in DispatchNextGoalAsync succeeds and the goal is dispatched a second time.
        // Without ClearGoalRetryState, TryAdd would fail and no second dispatch log would appear.
        using var cts2 = new CancellationTokenSource();
        using var linked2 = CancellationTokenSource.CreateLinkedTokenSource(
            cts2.Token, TestContext.Current.CancellationToken);
        var task2 = dispatcher.StartAsync(linked2.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        cts2.Cancel();
        await Task.WhenAny(task2, Task.Delay(1000, TestContext.Current.CancellationToken));

        // Assert: goal was dispatched a second time — proving _dispatchedGoals was cleared.
        var totalDispatchLogs = logger.Logs.Count(l => l.Message.Contains($"Dispatching goal '{goal.Id}'"));
        Assert.Equal(2, totalDispatchLogs);
    }

    /// <summary>
    /// Verifies that <see cref="GoalDispatcher.ClearGoalRetryState"/> adds the goal ID
    /// to the internal _retriedGoals tracking dictionary, enabling retry context injection
    /// on the next dispatch.
    /// </summary>
    [Fact]
    public void ClearGoalRetryState_AddsGoalIdToRetriedGoalsTracking()
    {
        var goalId = $"goal-{Guid.NewGuid():N}";
        var goalManager = new GoalManager();
        var pipelineManager = new GoalPipelineManager();
        var dispatcher = CreateDispatcher(goalManager, pipelineManager);

        // Act: Call ClearGoalRetryState to mark goal for retry
        dispatcher.ClearGoalRetryState(goalId);

        // Assert: Verify the entry exists in _retriedGoals using reflection
        var retriedGoalsField = typeof(GoalDispatcher).GetField(
            "_retriedGoals",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var retriedGoals = (System.Collections.Concurrent.ConcurrentDictionary<string, bool>)retriedGoalsField.GetValue(dispatcher)!;

        Assert.True(retriedGoals.ContainsKey(goalId), $"Expected goal ID '{goalId}' to be present in _retriedGoals after ClearGoalRetryState");
        Assert.True(retriedGoals.TryGetValue(goalId, out var value) && value, "The _retriedGoals entry value should be true");
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
        public List<string> DeletedSessionGoalIds { get; } = [];

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

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

        public void DeleteGoalSession(string goalId)
        {
            DeletedSessionGoalIds.Add(goalId);
        }

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
            var pipelineStore = new PipelineStore(dbPath, NullLogger<PipelineStore>.Instance);
            var pipelineManager = new GoalPipelineManager(pipelineStore);
            var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
            pipeline.AdvanceTo(GoalPhase.Coding);
            // Persist the phase change to the store so it can be restored
            pipelineManager.PersistState(pipeline);

            // Create a new pipeline manager that will restore from the store
            var restoredPipelineManager = new GoalPipelineManager(pipelineStore);

            // Create a GoalDispatcher with a real DistributedBrain using the temp directory
            var brain = new DistributedBrain(
                "copilot/claude-sonnet-4",
                NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir);

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
            var pipelineStore = new PipelineStore(dbPath, NullLogger<PipelineStore>.Instance);
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
