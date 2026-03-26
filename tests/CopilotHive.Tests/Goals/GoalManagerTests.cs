using System.Collections.Concurrent;
using CopilotHive.Goals;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Tests.Goals;

/// <summary>
/// Tests for <see cref="GoalManager.GetNextGoalAsync"/> dependency-filtering logic.
/// </summary>
public sealed class GoalManagerDependencyTests
{
    // ── Test 1: Goal with unsatisfied dependency is skipped ─────────────────

    /// <summary>
    /// When a child goal depends on a parent that is InProgress (not yet Completed),
    /// <see cref="GoalManager.GetNextGoalAsync"/> must not return the child.
    /// Setup: parent is seeded into the source map via a first call, then moved to
    /// InProgress. On the second call parent is no longer pending, child is still
    /// pending but its dependency is unsatisfied → returns null.
    /// </summary>
    [Fact]
    public async Task GoalWithUnsatisfiedDependency_Skipped()
    {
        var parent = new Goal { Id = "dep-parent", Description = "Parent goal" };
        var child = new Goal
        {
            Id = "dep-child",
            Description = "Child goal",
            DependsOn = ["dep-parent"],
        };

        var sourceA = new DependencyTestGoalStore("sourceA");
        sourceA.AddGoal(child);

        var sourceB = new DependencyTestGoalStore("sourceB");
        sourceB.AddGoal(parent); // parent starts Pending

        var manager = new GoalManager();
        manager.AddSource(sourceA);
        manager.AddSource(sourceB);

        // First call: seeds source map with parent (Pending). Parent has no deps → eligible.
        // Child has unsatisfied dep on parent (still Pending) → blocked.
        // Result: parent is returned.
        var first = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);
        Assert.Equal("dep-parent", first?.Id); // parent eligible, child blocked

        // Move parent to InProgress (picked up but not done)
        sourceB.SetStatus("dep-parent", GoalStatus.InProgress);

        // Second call: parent is no longer pending (not in GetPendingGoalsAsync result),
        // child is pending but parent status is InProgress (not Completed) → child blocked.
        // No other eligible goals → null.
        var second = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);
        Assert.Null(second);
    }

    // ── Test 2: Goal with satisfied dependency is dispatched ────────────────

    /// <summary>
    /// When a child goal depends on a parent that is Completed, GetNextGoalAsync
    /// should return the child.
    /// </summary>
    [Fact]
    public async Task GoalWithSatisfiedDependency_Dispatched()
    {
        var parentGoal = new Goal { Id = "ready-parent", Description = "Parent" };
        var childGoal = new Goal
        {
            Id = "ready-child",
            Description = "Child",
            DependsOn = ["ready-parent"],
        };

        var storeA = new DependencyTestGoalStore("storeA");
        storeA.AddGoal(childGoal);

        var storeB = new DependencyTestGoalStore("storeB");
        storeB.AddGoal(parentGoal);

        var manager = new GoalManager();
        manager.AddSource(storeA);
        manager.AddSource(storeB);

        // First call: seeds source map; parent is Pending → child blocked.
        var firstResult = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ready-parent", firstResult?.Id); // parent returned, child blocked

        // Complete the parent
        storeB.Complete("ready-parent");

        // Second call: parent is now Completed → child eligible
        var secondResult = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(secondResult);
        Assert.Equal("ready-child", secondResult.Id);
    }

    // ── Test 3: Multiple dependencies all satisfied ──────────────────────────

    /// <summary>
    /// When a child goal depends on multiple goals and all are Completed, the child is eligible.
    /// </summary>
    [Fact]
    public async Task MultiDependency_AllSatisfied()
    {
        var depA = new Goal { Id = "dep-a", Description = "Dep A" };
        var depB = new Goal { Id = "dep-b", Description = "Dep B" };
        var child = new Goal
        {
            Id = "child-multi",
            Description = "Multi-dep child",
            DependsOn = ["dep-a", "dep-b"],
        };

        var storeA = new DependencyTestGoalStore("storeA");
        storeA.AddGoal(depA);
        storeA.AddGoal(depB);

        var storeB = new DependencyTestGoalStore("storeB");
        storeB.AddGoal(child);

        var manager = new GoalManager();
        manager.AddSource(storeA);
        manager.AddSource(storeB);

        // Seed: both deps are Pending → child blocked
        var seed = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);
        // depA and depB are eligible (no dependencies), one of them is returned
        Assert.NotNull(seed); // either dep-a or dep-b
        Assert.NotEqual("child-multi", seed.Id);

        // Complete both dependencies
        storeA.Complete("dep-a");
        storeA.Complete("dep-b");

        // Now child should be returned
        var result = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("child-multi", result.Id);
    }

    // ── Test 4: Multiple dependencies, only some satisfied ──────────────────

    /// <summary>
    /// When a child depends on two goals but only one is Completed, the child is still blocked.
    /// </summary>
    [Fact]
    public async Task MultiDependency_PartiallySatisfied()
    {
        var depA = new Goal { Id = "dep-a-partial", Description = "Dep A" };
        var depB = new Goal { Id = "dep-b-partial", Description = "Dep B" };
        var child = new Goal
        {
            Id = "child-partial",
            Description = "Partially-satisfied child",
            DependsOn = ["dep-a-partial", "dep-b-partial"],
        };

        var store = new DependencyTestGoalStore("store");
        store.AddGoal(depA);
        store.AddGoal(depB);
        store.AddGoal(child);

        var manager = new GoalManager();
        manager.AddSource(store);

        // Seed the source map
        await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        // Complete only dep-a
        store.Complete("dep-a-partial");

        var result = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        // dep-b still pending, child still blocked; dep-a is done (not pending), dep-b is returned
        Assert.NotNull(result);
        Assert.NotEqual("child-partial", result.Id);
    }

    // ── Test 5: Non-existent dependency blocks goal ──────────────────────────

    /// <summary>
    /// When a child goal depends on an ID that does not exist in any source,
    /// GetNextGoalAsync should return null (non-existent dependency treated as unsatisfied).
    /// </summary>
    [Fact]
    public async Task NonExistentDependency_Blocked()
    {
        var child = new Goal
        {
            Id = "orphan-child",
            Description = "Child depending on ghost",
            DependsOn = ["ghost-id"],
        };

        var store = new DependencyTestGoalStore("store");
        store.AddGoal(child);

        var manager = new GoalManager();
        manager.AddSource(store);

        var result = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    // ── Test 6: Empty DependsOn is a pass-through ────────────────────────────

    /// <summary>
    /// A pending goal with an empty DependsOn list is dispatched normally (no regression).
    /// </summary>
    [Fact]
    public async Task EmptyDependencies_PassThrough()
    {
        var goal = new Goal
        {
            Id = "no-deps",
            Description = "No dependencies",
            DependsOn = [],
        };

        var store = new DependencyTestGoalStore("store");
        store.AddGoal(goal);

        var manager = new GoalManager();
        manager.AddSource(store);

        var result = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("no-deps", result.Id);
    }

    // ── Test 7: Priority selection among eligible goals ──────────────────────

    /// <summary>
    /// When multiple eligible goals exist, the one with the highest priority is returned.
    /// </summary>
    [Fact]
    public async Task PriorityAmongEligible()
    {
        var highPriority = new Goal
        {
            Id = "high-prio",
            Description = "High priority goal",
            Priority = GoalPriority.High,
        };
        var normalPriority = new Goal
        {
            Id = "normal-prio",
            Description = "Normal priority goal",
            Priority = GoalPriority.Normal,
        };

        var store = new DependencyTestGoalStore("store");
        store.AddGoal(highPriority);
        store.AddGoal(normalPriority);

        var manager = new GoalManager();
        manager.AddSource(store);

        var result = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("high-prio", result.Id);
    }

    // ── Test 9: Dependency completed before first poll is resolved ───────────

    /// <summary>
    /// When a dependency is already <see cref="GoalStatus.Completed"/> before the first
    /// dispatch poll (i.e., it never appears in <c>GetPendingGoalsAsync</c> and is therefore
    /// never added to the internal source map), <see cref="GoalManager.GetNextGoalAsync"/>
    /// must still resolve it via the fallback <see cref="IGoalStore.GetGoalAsync"/> search
    /// and return the child goal.
    /// </summary>
    [Fact]
    public async Task DependencyCompletedBeforeFirstPoll_IsResolved()
    {
        // Arrange: parent is already Completed in the store (never pending)
        var parentGoal = new Goal
        {
            Id = "pre-completed-parent",
            Description = "Parent goal that was completed before any poll",
            Status = GoalStatus.Completed,
        };
        var childGoal = new Goal
        {
            Id = "pre-completed-child",
            Description = "Child goal depending on pre-completed parent",
            DependsOn = ["pre-completed-parent"],
        };

        var store = new DependencyTestGoalStore("store");
        // Add parent with Completed status — it will NOT appear in GetPendingGoalsAsync
        store.AddGoal(parentGoal);
        store.AddGoal(childGoal);

        var manager = new GoalManager();
        manager.AddSource(store);

        // Act: first (and only) call — parent is Completed and not pending, child is pending
        var result = await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        // Assert: child must be returned because parent is already Completed
        Assert.NotNull(result);
        Assert.Equal("pre-completed-child", result.Id);
    }


    /// <summary>
    /// When a goal is blocked by an unsatisfied dependency, a Debug log is emitted
    /// containing the goal ID and the unsatisfied dependency IDs.
    /// </summary>
    [Fact]
    public async Task DebugLogOnSkip()
    {
        var child = new Goal
        {
            Id = "logged-child",
            Description = "Child that should be logged as blocked",
            DependsOn = ["missing-dep"],
        };

        var store = new DependencyTestGoalStore("store");
        store.AddGoal(child);

        var logger = new CapturingLogger<GoalManager>();
        var manager = new GoalManager(logger);
        manager.AddSource(store);

        await manager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        // Verify a Debug log was emitted mentioning the goal ID and the unsatisfied dep
        var debugLog = logger.Entries.FirstOrDefault(e =>
            e.Level == LogLevel.Debug &&
            e.Message.Contains("logged-child") &&
            e.Message.Contains("missing-dep"));

        Assert.True(debugLog != default,
            $"Expected debug log for blocked goal. Logs: {string.Join("; ", logger.Entries.Select(e => e.Message))}");
    }

    // ── GetSourceForGoalAsync fallback Tests ────────────────────────────────

    /// <summary>
    /// When a goal transitions from Pending to InProgress, it's no longer in
    /// GetPendingGoalsAsync results. Status updates (FailGoalAsync, CompleteGoalAsync)
    /// must still find the goal's source by searching IGoalStore instances.
    /// </summary>
    [Fact]
    public async Task FailGoalAsync_InProgressGoalNotInPendingCache_FindsViaStore()
    {
        var goal = new Goal { Id = "inflight-1", Description = "In-progress goal" };
        var store = new DependencyTestGoalStore("store");
        store.AddGoal(goal);

        var manager = new GoalManager();
        manager.AddSource(store);

        // Simulate dispatch: move to InProgress without calling GetNextGoalAsync first
        // (so _goalSourceMap is empty)
        store.SetStatus("inflight-1", GoalStatus.InProgress);

        // This used to throw KeyNotFoundException
        await manager.FailGoalAsync("inflight-1", "timeout", ct: TestContext.Current.CancellationToken);

        var updated = await store.GetGoalAsync("inflight-1", TestContext.Current.CancellationToken);
        Assert.Equal(GoalStatus.Failed, updated!.Status);
    }

    [Fact]
    public async Task CompleteGoalAsync_InProgressGoalNotInPendingCache_FindsViaStore()
    {
        var goal = new Goal { Id = "inflight-2", Description = "In-progress goal" };
        var store = new DependencyTestGoalStore("store");
        store.AddGoal(goal);

        var manager = new GoalManager();
        manager.AddSource(store);

        store.SetStatus("inflight-2", GoalStatus.InProgress);

        await manager.CompleteGoalAsync("inflight-2", ct: TestContext.Current.CancellationToken);

        var updated = await store.GetGoalAsync("inflight-2", TestContext.Current.CancellationToken);
        Assert.Equal(GoalStatus.Completed, updated!.Status);
    }

    [Fact]
    public async Task UpdateGoalStatusAsync_GoalNeverPolled_FindsViaStore()
    {
        var goal = new Goal { Id = "never-polled", Description = "Goal added after startup" };
        var store = new DependencyTestGoalStore("store");
        store.AddGoal(goal);

        var manager = new GoalManager();
        manager.AddSource(store);

        // Directly update without ever calling GetNextGoalAsync
        await manager.UpdateGoalStatusAsync("never-polled", GoalStatus.Cancelled, ct: TestContext.Current.CancellationToken);

        var updated = await store.GetGoalAsync("never-polled", TestContext.Current.CancellationToken);
        Assert.Equal(GoalStatus.Cancelled, updated!.Status);
    }
}

// ── Test helpers ─────────────────────────────────────────────────────────────

/// <summary>
/// Simple in-memory <see cref="IGoalStore"/> implementation for dependency tests.
/// Supports adding goals, completing them, and looking them up individually.
/// </summary>
file sealed class DependencyTestGoalStore : IGoalStore
{
    private readonly ConcurrentDictionary<string, Goal> _goals = new();

    /// <summary>Initialises the store with the given source name.</summary>
    public DependencyTestGoalStore(string name) => Name = name;

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>Adds a goal to this store.</summary>
    public void AddGoal(Goal goal) => _goals[goal.Id] = goal;

    /// <summary>Marks the goal with <paramref name="goalId"/> as Completed.</summary>
    public void Complete(string goalId) => SetStatus(goalId, GoalStatus.Completed);

    /// <summary>Sets the status of the goal with <paramref name="goalId"/>.</summary>
    public void SetStatus(string goalId, GoalStatus status)
    {
        if (_goals.TryGetValue(goalId, out var g))
            g.Status = status;
    }

    // ── IGoalSource ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(
            _goals.Values.Where(g => g.Status == GoalStatus.Pending).ToList().AsReadOnly());

    /// <inheritdoc/>
    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        if (_goals.TryGetValue(goalId, out var goal))
            goal.Status = status;
        return Task.CompletedTask;
    }

    // ── IGoalStore ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.GetValueOrDefault(goalId));

    /// <inheritdoc/>
    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

    /// <inheritdoc/>
    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.FromResult(goal);
    }

    /// <inheritdoc/>
    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
    {
        var removed = _goals.TryRemove(goalId, out _);
        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
        string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(
            _goals.Values.Where(g => g.Status == status).ToList().AsReadOnly());

    /// <inheritdoc/>
    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>([]);

    /// <inheritdoc/>
    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var g in goals)
        {
            if (_goals.TryAdd(g.Id, g))
                count++;
        }
        return Task.FromResult(count);
    }
}

/// <summary>
/// Collecting <see cref="ILogger{T}"/> that captures log entries for assertion.
/// </summary>
file sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>All captured log entries.</summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

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
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
