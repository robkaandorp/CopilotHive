using System.Collections.Concurrent;
using System.Diagnostics;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Unit tests for <see cref="GoalReadyNotifier"/> — the wake-up signal service used
/// by <see cref="GoalDispatcher"/> to dispatch goals immediately when they become Pending.
/// </summary>
public sealed class GoalReadyNotifierUnitTests
{
    [Fact]
    public async Task NotifyGoalReady_ThenWaitForSignal_ReturnsTrue()
    {
        var notifier = new GoalReadyNotifier();
        notifier.NotifyGoalReady();

        var result = await notifier.WaitForSignalAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task WaitForSignal_NoNotification_ReturnsFalseOnTimeout()
    {
        var notifier = new GoalReadyNotifier();

        var sw = Stopwatch.StartNew();
        var result = await notifier.WaitForSignalAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        sw.Stop();

        Assert.False(result);
        // Verify the timeout actually elapsed (at least ~80ms)
        Assert.True(sw.ElapsedMilliseconds >= 80, $"Expected timeout delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task MultipleNotifyGoalReady_Coalesce_OnlyOneWaitSucceeds()
    {
        var notifier = new GoalReadyNotifier();
        notifier.NotifyGoalReady();
        notifier.NotifyGoalReady();
        notifier.NotifyGoalReady();

        // First wait should succeed (consumes the single signal)
        var first = await notifier.WaitForSignalAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(first);

        // Second wait should time out — the extra Notify calls were coalesced
        var second = await notifier.WaitForSignalAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.False(second);
    }

    [Fact]
    public async Task ManyNotifyGoalReadyCalls_DoNotThrow()
    {
        var notifier = new GoalReadyNotifier();

        // Calling NotifyGoalReady many times rapidly should not throw
        // even though the semaphore max count is 1 (SemaphoreFullException is swallowed).
        for (int i = 0; i < 1000; i++)
        {
            notifier.NotifyGoalReady();
        }

        // One wait should succeed
        var result = await notifier.WaitForSignalAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(result);

        // Further waits time out — only one signal was retained
        var timeout = await notifier.WaitForSignalAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.False(timeout);
    }

    [Fact]
    public async Task WaitForSignal_Cancellation_ThrowsOperationCanceledException()
    {
        var notifier = new GoalReadyNotifier();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            notifier.WaitForSignalAsync(TimeSpan.FromSeconds(5), cts.Token));
    }

    [Fact]
    public async Task NotifyAfterConsumed_AllowsNextWaitToSucceed()
    {
        var notifier = new GoalReadyNotifier();
        notifier.NotifyGoalReady();
        await notifier.WaitForSignalAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        // After the signal was consumed, a new notify allows a new wait
        notifier.NotifyGoalReady();
        var result = await notifier.WaitForSignalAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(result);
    }
}

/// <summary>
/// Integration tests for the event-driven dispatch behaviour in <see cref="GoalDispatcher"/>.
/// <para>
/// Each test is written so that it FAILS if the feature under test is removed from
/// production code:
/// </para>
/// <list type="bullet">
/// <item>Signal-driven tests use a 30-second poll interval — completing via the timeout
/// path is impossible within the assertion window.</item>
/// <item>The timeout-driven test uses a 300 ms poll interval — a wait that blocks forever
/// makes the test fail.</item>
/// <item>The idempotency test asserts the exact number of dispatches (1), not "at least one".</item>
/// </list>
/// </summary>
public sealed class GoalReadyNotifierDispatcherIntegrationTests
{
    /// <summary>Poll interval used when the loop must be woken by a signal, never by a timeout.</summary>
    private static readonly TimeSpan LongPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for signal-driven reactions — far below <see cref="LongPollInterval"/>.</summary>
    private static readonly TimeSpan SignalWindow = TimeSpan.FromSeconds(8);

    private static HiveConfigFile Config(int maxParallelGoals = 1) =>
        new()
        {
            Orchestrator = new OrchestratorConfig { MaxParallelGoals = maxParallelGoals },
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "test-repo",
                    Url = "https://github.com/test/test-repo",
                    DefaultBranch = "main",
                },
            ],
        };

    private static Goal NewGoal(string id, GoalStatus status = GoalStatus.Pending) => new()
    {
        Id = id,
        Description = $"Goal {id}",
        Status = status,
        RepositoryNames = ["test-repo"],
        CreatedAt = DateTime.UtcNow,
    };

    private static GoalDispatcher CreateDispatcher(
        GoalManager goalManager,
        GoalPipelineManager pipelineManager,
        IGoalStore goalStore,
        GoalReadyNotifier? notifier,
        HiveConfigFile config) =>
        new(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            new StubBrain(),
            config: config,
            startupDelay: TimeSpan.Zero,
            goalStore: goalStore,
            goalReadyNotifier: notifier);

    // ── AC9: notification wakes the loop long before the poll interval ────────

    [Fact]
    public async Task NotifyGoalReady_WakesDispatcher_BeforePollIntervalElapses()
    {
        var originalPollInterval = GoalDispatcher.PollInterval;
        GoalDispatcher? dispatcher = null;
        CancellationTokenSource? cts = null;

        try
        {
            GoalDispatcher.PollInterval = LongPollInterval;

            var notifier = new GoalReadyNotifier();
            var store = new FakeGoalStore();
            var goalManager = new GoalManager();
            goalManager.AddSource(store);
            var pipelineManager = new GoalPipelineManager();

            dispatcher = CreateDispatcher(goalManager, pipelineManager, store, notifier, Config());
            cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            await dispatcher.StartAsync(cts.Token);

            // The unconditional first iteration must have FINISHED its pending-goal query
            // (result snapshotted and returned) before we make anything dispatchable.
            await WaitForAsync(() => store.CompletedPollCount >= 1, SignalWindow,
                "Dispatcher never completed its first poll");

            var completedPollsBeforeSignal = store.CompletedPollCount;

            // Only NOW does a goal become Pending — a plain Task.Delay(30s) could not see it.
            var goal = NewGoal($"goal-signal-{Guid.NewGuid():N}");
            store.Add(goal);

            var sw = Stopwatch.StartNew();
            notifier.NotifyGoalReady();

            await WaitForAsync(() => pipelineManager.GetByGoalId(goal.Id) is not null, SignalWindow,
                "The goal was not dispatched after NotifyGoalReady — the loop did not wake on the signal");
            sw.Stop();

            // Observable second completed poll, caused only by the notification.
            Assert.True(store.CompletedPollCount > completedPollsBeforeSignal,
                $"Expected an additional COMPLETED dispatch poll after the notification " +
                $"(before={completedPollsBeforeSignal}, after={store.CompletedPollCount})");
            Assert.NotNull(pipelineManager.GetByGoalId(goal.Id));
            Assert.True(sw.Elapsed < SignalWindow,
                $"Signal-driven dispatch took {sw.ElapsedMilliseconds}ms — the loop apparently waited for the {LongPollInterval.TotalSeconds}s poll interval");
        }
        finally
        {
            GoalDispatcher.PollInterval = originalPollInterval;
            await ShutdownAsync(dispatcher, cts);
        }
    }

    // ── AC10: without any notification the loop still runs via the timeout ────

    [Fact]
    public async Task NoNotify_DispatcherStillRuns_AfterPollIntervalTimeout()
    {
        var originalPollInterval = GoalDispatcher.PollInterval;
        GoalDispatcher? dispatcher = null;
        CancellationTokenSource? cts = null;

        try
        {
            GoalDispatcher.PollInterval = TimeSpan.FromMilliseconds(300);

            var notifier = new GoalReadyNotifier();
            var store = new FakeGoalStore();
            var goalManager = new GoalManager();
            goalManager.AddSource(store);
            var pipelineManager = new GoalPipelineManager();

            // MaxParallelGoals > 1 so the parallelism gate does not short-circuit later
            // iterations — every timeout-driven iteration really re-queries the store.
            dispatcher = CreateDispatcher(goalManager, pipelineManager, store, notifier, Config(maxParallelGoals: 5));
            cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            await dispatcher.StartAsync(cts.Token);

            // The unconditional first iteration must have FINISHED with nothing to do.
            // This proves the goal below was NOT available during the first dispatch.
            await WaitForAsync(() => store.CompletedPollCount >= 1, TimeSpan.FromSeconds(10),
                "Dispatcher never completed its first poll");

            var completedPollsBeforeGoal = store.CompletedPollCount;

            // Add the goal only after the loop is already waiting, and never notify.
            var goal = NewGoal($"goal-timeout-{Guid.NewGuid():N}");
            store.Add(goal);

            // If WaitForSignalAsync never returned without a signal, this would time out.
            await WaitForAsync(() => pipelineManager.GetByGoalId(goal.Id) is not null, TimeSpan.FromSeconds(10),
                "The goal was never dispatched — the wait did not fall through on timeout");

            Assert.NotNull(pipelineManager.GetByGoalId(goal.Id));
            Assert.True(store.CompletedPollCount > completedPollsBeforeGoal,
                "The dispatch must have come from a later completed poll, not the first iteration");

            // And the timeout path keeps driving further iterations without any signal.
            var completedPollsAfterDispatch = store.CompletedPollCount;
            await WaitForAsync(() => store.CompletedPollCount > completedPollsAfterDispatch, TimeSpan.FromSeconds(10),
                "No further timeout-driven dispatch polls — the loop stopped without a signal");
            Assert.True(store.CompletedPollCount > completedPollsAfterDispatch);
        }
        finally
        {
            GoalDispatcher.PollInterval = originalPollInterval;
            await ShutdownAsync(dispatcher, cts);
        }
    }

    // ── AC11: notification/timeout race stays serialized and idempotent ───────

    [Fact]
    public async Task ConcurrentNotifyAndPolling_NoConcurrentDispatch_SameGoalNotDispatchedTwice()
    {
        var originalPollInterval = GoalDispatcher.PollInterval;
        GoalDispatcher? dispatcher = null;
        CancellationTokenSource? cts = null;

        try
        {
            // Very short interval so timeouts and signals genuinely race with each other.
            GoalDispatcher.PollInterval = TimeSpan.FromMilliseconds(20);

            var notifier = new GoalReadyNotifier();
            // Sticky: the goal is reported as Pending on every poll, so the ONLY thing that
            // prevents a second dispatch is the pipeline-existence check in DispatchNextGoalAsync.
            var store = new FakeGoalStore { StickyPending = true };
            var goalManager = new GoalManager();
            goalManager.AddSource(store);
            var pipelineManager = new GoalPipelineManager();

            var goal = NewGoal($"goal-race-{Guid.NewGuid():N}");
            store.Add(goal);

            // MaxParallelGoals > 1 so the parallelism gate never short-circuits the
            // selection path — every iteration really re-selects the same goal.
            dispatcher = CreateDispatcher(goalManager, pipelineManager, store, notifier, Config(maxParallelGoals: 5));
            cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            await dispatcher.StartAsync(cts.Token);

            // Hammer the notifier while the loop is timing out, creating a real race.
            var floodCts = cts;
            var flood = Task.Run(async () =>
            {
                for (var i = 0; i < 300 && !floodCts.IsCancellationRequested; i++)
                {
                    notifier.NotifyGoalReady();
                    await Task.Delay(2, CancellationToken.None);
                }
            }, CancellationToken.None);

            await WaitForAsync(() => pipelineManager.GetByGoalId(goal.Id) is not null, TimeSpan.FromSeconds(10),
                "The goal was never dispatched");

            await flood;

            // Give the loop plenty of extra iterations to (incorrectly) re-dispatch.
            await WaitForAsync(() => store.CompletedPollCount >= 30, TimeSpan.FromSeconds(10),
                "Dispatch loop did not iterate repeatedly under the notification flood");

            // Exactly one pipeline for the goal — not "at most one".
            var pipelines = pipelineManager.GetActivePipelines().Where(p => p.GoalId == goal.Id).ToList();
            Assert.Single(pipelines);

            // Exactly one dispatch: the goal moved to InProgress exactly once even though
            // it stayed selectable on every single poll.
            Assert.Equal(1, store.InProgressTransitions(goal.Id));

            // Consumers are serialized: only one dispatch cycle ever runs at a time.
            Assert.Equal(1, store.MaxConcurrentPolls);
        }
        finally
        {
            GoalDispatcher.PollInterval = originalPollInterval;
            await ShutdownAsync(dispatcher, cts);
        }
    }

    // ── AC12: stale-pipeline reset signals the dispatcher ─────────────────────

    [Fact]
    public async Task StaleResetSignal_ConsumedByFirstWaitForSignal_TriggersImmediateSecondIteration()
    {
        var originalPollInterval = GoalDispatcher.PollInterval;
        GoalDispatcher? dispatcher = null;
        CancellationTokenSource? cts = null;
        CopilotHiveDbContext? dbContext = null;
        PipelineStore? pipelineStore = null;

        try
        {
            // 30s: a second iteration within the assertion window can ONLY come from the
            // NotifyGoalReady call in DispatcherMaintenance.RestoreActivePipelinesAsync.
            GoalDispatcher.PollInterval = LongPollInterval;

            var notifier = new GoalReadyNotifier();
            var store = new FakeGoalStore();
            var goalManager = new GoalManager();
            goalManager.AddSource(store);

            // The stale goal is InProgress and its pipeline is mid-Planning with no active task.
            var staleGoal = NewGoal($"goal-stale-{Guid.NewGuid():N}", GoalStatus.InProgress);
            store.Add(staleGoal);
            // Never selectable — it must not be able to satisfy the second-iteration assertion.
            store.Suppress(staleGoal.Id);

            // A second goal that only becomes visible from the SECOND poll onwards, so it can
            // only be dispatched by a genuine second dispatch-loop iteration.
            var secondGoal = NewGoal($"goal-second-{Guid.NewGuid():N}");
            store.Add(secondGoal);
            store.VisibleFromPoll(secondGoal.Id, 2);

            dbContext = CopilotHiveDbContext.CreateInMemory();
            pipelineStore = new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance);
            var pipelineManager = new GoalPipelineManager(pipelineStore);
            var stalePipeline = pipelineManager.CreatePipeline(staleGoal, maxRetries: 3);
            pipelineManager.PersistFull(stalePipeline);
            // Drop it from memory so startup restoration reloads it from the store.
            Assert.True(RemoveFromMemoryOnly(pipelineManager, pipelineStore, stalePipeline));

            dispatcher = CreateDispatcher(goalManager, pipelineManager, store, notifier, Config(maxParallelGoals: 2));
            cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            var sw = Stopwatch.StartNew();
            await dispatcher.StartAsync(cts.Token);

            // First iteration: nothing dispatchable (stale goal suppressed, second goal hidden).
            await WaitForAsync(() => store.CompletedPollCount >= 1, SignalWindow,
                "Dispatcher never completed its first poll");

            // Second iteration happens only because the stale reset released the signal.
            await WaitForAsync(() => pipelineManager.GetByGoalId(secondGoal.Id) is not null, SignalWindow,
                $"No second dispatch iteration within {SignalWindow.TotalSeconds}s — the stale-pipeline reset did not signal the dispatcher");
            sw.Stop();

            Assert.True(store.CompletedPollCount >= 2,
                $"Expected at least 2 completed dispatch polls, saw {store.CompletedPollCount}");
            Assert.NotNull(pipelineManager.GetByGoalId(secondGoal.Id));
            Assert.True(sw.Elapsed < SignalWindow,
                $"Second iteration took {sw.ElapsedMilliseconds}ms — the loop waited for the {LongPollInterval.TotalSeconds}s poll interval instead of the stale-reset signal");

            // The stale pipeline was discarded and its goal reset to Pending.
            Assert.Null(pipelineManager.GetByGoalId(staleGoal.Id));
            var reset = await store.GetGoalAsync(staleGoal.Id, TestContext.Current.CancellationToken);
            Assert.Equal(GoalStatus.Pending, reset!.Status);
        }
        finally
        {
            GoalDispatcher.PollInterval = originalPollInterval;
            await ShutdownAsync(dispatcher, cts);
            if (pipelineStore is not null)
                await SafeAsync(async () => await pipelineStore.DisposeAsync());
            if (dbContext is not null)
                await SafeAsync(async () => await dbContext.DisposeAsync());
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes a pipeline from the manager's in-memory dictionary while keeping the
    /// persisted snapshot, so a subsequent restore reloads it as a "stale" pipeline.
    /// </summary>
    private static bool RemoveFromMemoryOnly(GoalPipelineManager manager, PipelineStore store, GoalPipeline pipeline)
    {
        var removed = manager.RemovePipeline(pipeline.GoalId);
        store.SavePipeline(pipeline); // re-persist after RemovePipeline cleaned the store
        return removed;
    }

    /// <summary>
    /// Cancels and stops the dispatcher (when one was constructed) and disposes the linked
    /// token source. Every step is independently guarded so a single failure never skips
    /// the remaining cleanup, and a setup failure before construction is a no-op.
    /// </summary>
    private static async Task ShutdownAsync(GoalDispatcher? dispatcher, CancellationTokenSource? cts)
    {
        if (cts is not null)
            await SafeAsync(() => { cts.Cancel(); return Task.CompletedTask; });

        if (dispatcher is not null)
        {
            await SafeAsync(async () =>
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await dispatcher.StopAsync(stopCts.Token);
            });
            await SafeAsync(() => { dispatcher.Dispose(); return Task.CompletedTask; });
        }

        if (cts is not null)
            await SafeAsync(() => { cts.Dispose(); return Task.CompletedTask; });
    }

    private static async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception) { /* cleanup is best-effort — never mask the real assertion failure */ }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        if (condition())
            return;

        Assert.Fail($"{failureMessage} (timed out after {timeout.TotalSeconds}s)");
    }
}

/// <summary>
/// Thread-safe in-memory <see cref="IGoalStore"/> used to observe dispatch-loop behaviour:
/// it counts polls, tracks maximum poll concurrency, records InProgress transitions, and
/// can hide goals until a given poll number so tests can force a specific loop iteration
/// to be the one that dispatches.
/// </summary>
file sealed class FakeGoalStore : IGoalStore
{
    private readonly ConcurrentDictionary<string, Goal> _goals = new();
    private readonly ConcurrentDictionary<string, int> _visibleFromPoll = new();
    private readonly ConcurrentDictionary<string, bool> _suppressed = new();
    private readonly ConcurrentDictionary<string, int> _inProgressTransitions = new();
    private int _pollCount;
    private int _completedPollCount;
    private int _concurrentPolls;
    private int _maxConcurrentPolls;

    public string Name => "fake";

    /// <summary>When true, goals are always reported as Pending regardless of their real status.</summary>
    public bool StickyPending { get; init; }

    /// <summary>Number of times the dispatcher STARTED asking for pending goals.</summary>
    public int PollCount => Volatile.Read(ref _pollCount);

    /// <summary>
    /// Number of pending-goal queries that FULLY COMPLETED — incremented only after the
    /// result set has been snapshotted and is about to be returned. Tests wait on this
    /// counter (never <see cref="PollCount"/>) before mutating the store, so a goal added
    /// afterwards provably could not have been visible to that poll.
    /// </summary>
    public int CompletedPollCount => Volatile.Read(ref _completedPollCount);

    /// <summary>Highest number of overlapping pending-goal queries observed.</summary>
    public int MaxConcurrentPolls => Volatile.Read(ref _maxConcurrentPolls);

    public void Add(Goal goal) => _goals[goal.Id] = goal;

    /// <summary>Hides the goal from pending queries until the given (1-based) poll number.</summary>
    public void VisibleFromPoll(string goalId, int poll) => _visibleFromPoll[goalId] = poll;

    /// <summary>Permanently hides the goal from pending queries.</summary>
    public void Suppress(string goalId) => _suppressed[goalId] = true;

    public int InProgressTransitions(string goalId) =>
        _inProgressTransitions.TryGetValue(goalId, out var n) ? n : 0;

    public async Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
    {
        var poll = Interlocked.Increment(ref _pollCount);
        var current = Interlocked.Increment(ref _concurrentPolls);
        try
        {
            int observed;
            while (current > (observed = Volatile.Read(ref _maxConcurrentPolls)))
            {
                if (Interlocked.CompareExchange(ref _maxConcurrentPolls, current, observed) == observed)
                    break;
            }

            // Widen the window so overlapping consumers would be observable.
            await Task.Delay(5, ct);

            var snapshot = _goals.Values
                .Where(g => !_suppressed.ContainsKey(g.Id))
                .Where(g => !_visibleFromPoll.TryGetValue(g.Id, out var from) || poll >= from)
                .Where(g => StickyPending || g.Status == GoalStatus.Pending)
                .Select(g => StickyPending && g.Status != GoalStatus.Pending ? Clone(g, GoalStatus.Pending) : g)
                .ToList()
                .AsReadOnly();

            // Only NOW is the poll observably complete: the result is fully materialised
            // and any later mutation of _goals cannot affect it.
            Interlocked.Increment(ref _completedPollCount);
            return snapshot;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentPolls);
        }
    }

    private static Goal Clone(Goal g, GoalStatus status) => new()
    {
        Id = g.Id,
        Description = g.Description,
        Status = status,
        RepositoryNames = g.RepositoryNames,
        DependsOn = g.DependsOn,
        CreatedAt = g.CreatedAt,
        Priority = g.Priority,
        Scope = g.Scope,
    };

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        if (status == GoalStatus.InProgress)
            _inProgressTransitions.AddOrUpdate(goalId, 1, (_, n) => n + 1);

        if (_goals.TryGetValue(goalId, out var goal))
        {
            goal.Status = status;
            if (metadata?.StartedAt is { } started) goal.StartedAt = started;
            if (metadata?.CompletedAt is { } completed) goal.CompletedAt = completed;
        }

        return Task.CompletedTask;
    }

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.TryGetValue(goalId, out var g) ? g : null);

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

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

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.TryRemove(goalId, out _));

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.Where(g => g.Status == status).ToList().AsReadOnly());

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>([]);

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) => Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>([]);

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
}

/// <summary>
/// Minimal <see cref="IDistributedBrain"/> stub that returns default plans and prompts
/// so dispatch completes without any LLM involvement.
/// </summary>
file sealed class StubBrain : IDistributedBrain
{
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
        UpdateModelAsync(model, maxContextTokens, ct);

    public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
        => Task.FromResult(PlanResult.Success(IterationPlan.Default()));
    public Task<PromptResult> CraftPromptAsync(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
        => Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));
    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;
    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) => Task.CompletedTask;
    public Task<BrainResponse> AskQuestionAsync(string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default)
        => Task.FromResult(BrainResponse.Answer("proceed"));
    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
    public bool GoalSessionExists(string goalId) => false;
    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default)
        => Task.FromResult($"Goal '{pipeline.GoalId}' completed.");
    public BrainStats? GetStats() => null;
}
