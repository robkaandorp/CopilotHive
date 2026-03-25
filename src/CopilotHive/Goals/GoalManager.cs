using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Goals;

/// <summary>
/// Aggregates multiple <see cref="IGoalSource"/> instances and dispatches goals by priority.
/// Respects <see cref="Goal.DependsOn"/> — goals whose dependencies are not yet completed
/// are skipped until all required goals reach <see cref="GoalStatus.Completed"/>.
/// Thread-safe.
/// </summary>
public sealed class GoalManager
{
    private static readonly GoalPriority[] PriorityOrder =
        [GoalPriority.Critical, GoalPriority.High, GoalPriority.Normal, GoalPriority.Low];

    private readonly List<IGoalSource> _sources = [];
    private readonly Dictionary<string, IGoalSource> _goalSourceMap = [];
    private readonly Lock _lock = new();
    private readonly ILogger<GoalManager> _logger;

    /// <summary>
    /// Initialises a new <see cref="GoalManager"/> with an optional logger.
    /// </summary>
    /// <param name="logger">
    /// Logger instance used for dependency-blocking diagnostics.
    /// Defaults to <see cref="NullLogger{T}.Instance"/> when not provided.
    /// </param>
    public GoalManager(ILogger<GoalManager>? logger = null)
    {
        _logger = logger ?? NullLogger<GoalManager>.Instance;
    }

    /// <summary>
    /// Registers a new goal source with this manager.
    /// </summary>
    /// <param name="source">The source to add.</param>
    public void AddSource(IGoalSource source)
    {
        lock (_lock)
        {
            _sources.Add(source);
        }
    }

    /// <summary>A snapshot of all currently registered goal sources.</summary>
    public IReadOnlyList<IGoalSource> Sources
    {
        get
        {
            lock (_lock) { return _sources.ToList().AsReadOnly(); }
        }
    }

    /// <summary>
    /// Queries all registered sources and returns the highest-priority pending goal
    /// whose dependencies (if any) are all satisfied, or <c>null</c> when no such
    /// goal exists.  Goals with unsatisfied dependencies are skipped and logged at
    /// Debug level.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The next eligible goal to process, or <c>null</c>.</returns>
    public async Task<Goal?> GetNextGoalAsync(CancellationToken ct = default)
    {
        List<IGoalSource> snapshot;
        lock (_lock) { snapshot = [.. _sources]; }

        // Collect ALL pending goals first so the source map is fully populated
        // before we start evaluating dependency status.
        var allPending = new List<Goal>();

        foreach (var source in snapshot)
        {
            var pending = await source.GetPendingGoalsAsync(ct);
            foreach (var goal in pending)
            {
                lock (_lock) { _goalSourceMap.TryAdd(goal.Id, source); }
                allPending.Add(goal);
            }
        }

        // Evaluate which goals are eligible (all dependencies satisfied)
        Goal? best = null;

        foreach (var goal in allPending)
        {
            if (goal.DependsOn is not { Count: > 0 })
            {
                // No dependencies — unconditionally eligible
                if (best is null || ComparePriority(goal.Priority, best.Priority) > 0)
                    best = goal;
                continue;
            }

            var unsatisfied = await GetUnsatisfiedDependenciesAsync(goal.DependsOn, snapshot, ct);

            if (unsatisfied.Count > 0)
            {
                _logger.LogDebug(
                    "Goal '{GoalId}' blocked — unsatisfied dependencies: {Unsatisfied}",
                    goal.Id, string.Join(", ", unsatisfied));
                continue; // blocked — skip this goal
            }

            // All dependencies satisfied
            _logger.LogDebug(
                "Goal '{GoalId}' eligible — all dependencies satisfied",
                goal.Id);

            if (best is null || ComparePriority(goal.Priority, best.Priority) > 0)
                best = goal;
        }

        return best;
    }

    /// <summary>
    /// Marks the specified goal as <see cref="GoalStatus.Completed"/> in its source.
    /// </summary>
    /// <param name="goalId">Identifier of the goal to complete.</param>
    /// <param name="metadata">Optional metadata (timestamps, iterations).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CompleteGoalAsync(string goalId, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        var source = GetSourceForGoal(goalId);
        await source.UpdateGoalStatusAsync(goalId, GoalStatus.Completed, metadata, ct);
    }

    /// <summary>
    /// Marks the specified goal as <see cref="GoalStatus.Failed"/> in its source.
    /// </summary>
    /// <param name="goalId">Identifier of the goal to fail.</param>
    /// <param name="reason">Human-readable reason for the failure.</param>
    /// <param name="metadata">Optional additional metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task FailGoalAsync(string goalId, string reason, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        var enriched = metadata is not null
            ? metadata with { FailureReason = reason }
            : new GoalUpdateMetadata { FailureReason = reason };
        var source = GetSourceForGoal(goalId);
        await source.UpdateGoalStatusAsync(goalId, GoalStatus.Failed, enriched, ct);
    }

    /// <summary>
    /// Updates the status of a goal in its originating source.
    /// </summary>
    /// <param name="goalId">Identifier of the goal to update.</param>
    /// <param name="status">The new status to apply.</param>
    /// <param name="metadata">Optional metadata (timestamps, iterations, failure reason).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        var source = GetSourceForGoal(goalId);
        await source.UpdateGoalStatusAsync(goalId, status, metadata, ct);
    }

    /// <summary>
    /// Returns the list of dependency IDs from <paramref name="dependsOn"/> that are not yet
    /// satisfied.  A dependency is considered satisfied only when the backing source knows about
    /// it and reports its status as <see cref="GoalStatus.Completed"/>.
    /// When a dependency has never been seen as pending (not in the source map), all registered
    /// <see cref="IGoalStore"/> sources are searched as a fallback so that goals already
    /// completed before the first poll are correctly resolved.
    /// </summary>
    /// <param name="dependsOn">Dependency goal IDs to check.</param>
    /// <param name="snapshot">Snapshot of all registered sources used for fallback lookups.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>IDs of dependencies that are not yet completed.</returns>
    private async Task<List<string>> GetUnsatisfiedDependenciesAsync(
        IReadOnlyList<string> dependsOn, IReadOnlyList<IGoalSource> snapshot, CancellationToken ct)
    {
        var unsatisfied = new List<string>();

        foreach (var depId in dependsOn)
        {
            IGoalSource? depSource;
            lock (_lock) { _goalSourceMap.TryGetValue(depId, out depSource); }

            if (depSource is not null)
            {
                // Source is known — check status via IGoalStore if available
                if (depSource is IGoalStore knownStore)
                {
                    var dep = await knownStore.GetGoalAsync(depId, ct);
                    if (dep is null || dep.Status != GoalStatus.Completed)
                        unsatisfied.Add(depId);
                }
                else
                {
                    // Source is not an IGoalStore — cannot query individual goal status.
                    // Fall back to conservative: treat as unsatisfied.
                    unsatisfied.Add(depId);
                }
                continue;
            }

            // Dependency was never seen as pending (not in source map).
            // It may have been completed before the first poll.
            // Search all registered IGoalStore sources as a fallback.
            var foundSatisfied = false;
            foreach (var source in snapshot)
            {
                if (source is IGoalStore fallbackStore)
                {
                    var dep = await fallbackStore.GetGoalAsync(depId, ct);
                    if (dep is not null)
                    {
                        lock (_lock) { _goalSourceMap.TryAdd(depId, source); }
                        if (dep.Status == GoalStatus.Completed)
                        {
                            foundSatisfied = true;
                        }
                        break;
                    }
                }
            }

            if (!foundSatisfied)
                unsatisfied.Add(depId);
        }

        return unsatisfied;
    }

    private IGoalSource GetSourceForGoal(string goalId)
    {
        lock (_lock)
        {
            if (_goalSourceMap.TryGetValue(goalId, out var source))
                return source;
        }

        throw new KeyNotFoundException($"Goal '{goalId}' not found in any source.");
    }

    private static int ComparePriority(GoalPriority a, GoalPriority b) =>
        Array.IndexOf(PriorityOrder, b) - Array.IndexOf(PriorityOrder, a);
}
