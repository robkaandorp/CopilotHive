using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="GoalLifecycleService"/> event publishing via <see cref="IEventBus"/>.
/// Uses a real <see cref="GoalManager"/> with a custom <see cref="IGoalStore"/> fake
/// to verify that lifecycle events are published (or not) at the right times.
/// </summary>
public sealed class EventBusProducerTests
{
    /// <summary>
    /// A minimal <see cref="IGoalStore"/> fake that records status updates and can throw
    /// on demand. Only the methods used by <see cref="GoalManager"/> for status updates
    /// are implemented; the rest throw <see cref="NotImplementedException"/>.
    /// </summary>
    private sealed class FakeGoalStore : IGoalStore
    {
        public string Name => "fake";

        public List<(string GoalId, GoalStatus Status, GoalUpdateMetadata? Metadata)> Updates { get; } = [];

        public Exception? ThrowOnUpdateStatus { get; set; }

        private readonly Dictionary<string, Goal> _goals = [];

        public void AddGoal(Goal goal) => _goals[goal.Id] = goal;

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

        public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            if (ThrowOnUpdateStatus is not null)
                throw ThrowOnUpdateStatus;
            Updates.Add((goalId, status, metadata));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult(_goals.TryGetValue(goalId, out var g) ? g : null);

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            _goals[goal.Id] = goal;
            return Task.FromResult(goal);
        }

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            _goals[goal.Id] = goal;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
        {
            return Task.FromResult(_goals.Remove(goalId));
        }

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult<Release?>(null);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>(Array.Empty<(string, PersistedClarification)>());
    }

    /// <summary>
    /// A recording <see cref="IEventBus"/> that captures all published events.
    /// </summary>
    private sealed class RecordingEventBus : IEventBus
    {
        public List<SystemEvent> Published { get; } = [];
        public event Action<SystemEvent>? OnEvent;

        public void Publish(SystemEvent evt)
        {
            Published.Add(evt);
            OnEvent?.Invoke(evt);
        }
    }

    private static GoalPipeline CreatePipeline(string goalId = "test-goal-1")
    {
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        return new GoalPipeline(goal);
    }

    [Fact]
    public async Task FinalizeGoalAsync_Completed_PublishesGoalCompletedWithGoalId()
    {
        var store = new FakeGoalStore();
        store.AddGoal(new Goal { Id = "test-goal-1", Description = "Test goal" });
        var manager = new GoalManager();
        manager.AddSource(store);
        var eventBus = new RecordingEventBus();

        var service = new GoalLifecycleService(
            manager,
            NullLogger.Instance,
            eventBus: eventBus);

        var pipeline = CreatePipeline("test-goal-1");
        pipeline.AdvanceTo(GoalPhase.Done);

        await service.FinalizeGoalAsync(
            pipeline,
            GoalStatus.Completed,
            failureReason: null,
            mergeCommitHash: "abc123",
            TestContext.Current.CancellationToken);

        Assert.Single(eventBus.Published);
        var evt = eventBus.Published[0];
        Assert.Equal(EventType.GoalCompleted, evt.Type);
        Assert.Equal("test-goal-1", evt.GoalId);
        Assert.Equal("Goal merged successfully", evt.Message);
        // Status update must have been called (event published after persistence).
        Assert.Single(store.Updates);
        Assert.Equal(GoalStatus.Completed, store.Updates[0].Status);
    }

    [Fact]
    public async Task FinalizeGoalAsync_Failed_PublishesGoalFailedWithGoalIdAndReason()
    {
        var store = new FakeGoalStore();
        store.AddGoal(new Goal { Id = "failed-goal", Description = "Test goal" });
        var manager = new GoalManager();
        manager.AddSource(store);
        var eventBus = new RecordingEventBus();

        var service = new GoalLifecycleService(
            manager,
            NullLogger.Instance,
            eventBus: eventBus);

        var pipeline = CreatePipeline("failed-goal");
        pipeline.AdvanceTo(GoalPhase.Failed);

        await service.FinalizeGoalAsync(
            pipeline,
            GoalStatus.Failed,
            failureReason: "Build failed after 3 retries",
            mergeCommitHash: null,
            TestContext.Current.CancellationToken);

        Assert.Single(eventBus.Published);
        var evt = eventBus.Published[0];
        Assert.Equal(EventType.GoalFailed, evt.Type);
        Assert.Equal("failed-goal", evt.GoalId);
        Assert.Equal("Build failed after 3 retries", evt.Message);
        Assert.Single(store.Updates);
        Assert.Equal(GoalStatus.Failed, store.Updates[0].Status);
    }

    [Fact]
    public async Task FinalizeGoalAsync_WithNullEventBus_DoesNotPublishAndStillCompletes()
    {
        var store = new FakeGoalStore();
        store.AddGoal(new Goal { Id = "null-bus-goal", Description = "Test goal" });
        var manager = new GoalManager();
        manager.AddSource(store);

        // No eventBus — backward compatibility.
        var service = new GoalLifecycleService(manager, NullLogger.Instance);

        var pipeline = CreatePipeline("null-bus-goal");
        pipeline.AdvanceTo(GoalPhase.Done);

        await service.FinalizeGoalAsync(
            pipeline,
            GoalStatus.Completed,
            failureReason: null,
            mergeCommitHash: "abc123",
            TestContext.Current.CancellationToken);

        // The goal status must still be persisted.
        Assert.Single(store.Updates);
        Assert.Equal(GoalStatus.Completed, store.Updates[0].Status);
        // No event bus means no crash — the test passing proves backward compatibility.
    }

    [Fact]
    public async Task FinalizeGoalAsync_WhenUpdateGoalStatusThrows_DoesNotPublishEventAndPropagatesException()
    {
        var store = new FakeGoalStore();
        store.AddGoal(new Goal { Id = "throw-goal", Description = "Test goal" });
        var throwEx = new InvalidOperationException("DB connection lost");
        store.ThrowOnUpdateStatus = throwEx;

        var manager = new GoalManager();
        manager.AddSource(store);
        var eventBus = new RecordingEventBus();

        var service = new GoalLifecycleService(
            manager,
            NullLogger.Instance,
            eventBus: eventBus);

        var pipeline = CreatePipeline("throw-goal");
        pipeline.AdvanceTo(GoalPhase.Failed);

        // The exception must propagate (pre-existing semantics) — no silent swallow.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.FinalizeGoalAsync(
                pipeline,
                GoalStatus.Failed,
                failureReason: "some reason",
                mergeCommitHash: null,
                TestContext.Current.CancellationToken));

        Assert.Same(throwEx, ex);
        // No event must have been published since the status persistence failed.
        Assert.Empty(eventBus.Published);
    }
}