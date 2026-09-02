using System.Collections.Concurrent;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Why a <see cref="GoalPipelineManager.TryRegisterTask"/> call refused to take ownership of a
/// task → goal mapping.
/// </summary>
internal enum TaskRegistrationFailure
{
    /// <summary>No failure — the mapping is owned by the requesting goal.</summary>
    None,

    /// <summary>
    /// The mapping could not be claimed: the argument was blank, this manager already held an
    /// in-memory entry for the task, or the persisted row belongs to a different goal.
    /// </summary>
    DuplicateMapping,

    /// <summary>The persisted write threw; the exception is carried on the result.</summary>
    PersistenceFailed,
}

/// <summary>Outcome of an ownership-checked task registration.</summary>
/// <param name="Success"><c>true</c> only when the mapping is owned by the requesting goal.</param>
/// <param name="Cause">Why the registration failed, or <see cref="TaskRegistrationFailure.None"/>.</param>
/// <param name="PersistenceException">The store exception when <see cref="Cause"/> is
/// <see cref="TaskRegistrationFailure.PersistenceFailed"/>; otherwise <c>null</c>.</param>
internal record TaskRegistrationResult(bool Success, TaskRegistrationFailure Cause, Exception? PersistenceException);

/// <summary>Outcome of an ownership-checked task unregistration.</summary>
/// <param name="MemoryRemoved"><c>true</c> when the in-memory entry for the pair was removed.</param>
/// <param name="PersistenceRemoved">
/// <c>true</c> when the persisted row was removed (or there was nothing to persist);
/// <c>false</c> signals persisted RESIDUE — the row was absent, owned by another goal, or the
/// delete threw. The residue is reported honestly rather than silently ignored.
/// </param>
internal record TaskUnregisterResult(bool MemoryRemoved, bool PersistenceRemoved);

/// <summary>
/// Singleton that holds all active goal pipelines and provides lookup by goalId or taskId.
/// Persists pipeline state to SQLite via <see cref="PipelineStore"/>.
/// </summary>
public sealed class GoalPipelineManager
{
    private readonly ConcurrentDictionary<string, GoalPipeline> _pipelines = new();
    private readonly ConcurrentDictionary<string, string> _taskToGoal = new();
    private readonly PipelineStore? _store;
    private readonly ILogger<GoalPipelineManager>? _logger;

    /// <summary>
    /// Initialises a new <see cref="GoalPipelineManager"/>.
    /// </summary>
    /// <param name="store">Optional persistence store; when provided, pipeline state is saved to SQLite.</param>
    /// <param name="logger">
    /// Optional logger for the ownership-checked registration APIs. Production instances are
    /// currently constructed WITHOUT a logger — wiring it through DI is a follow-up slice.
    /// </param>
    public GoalPipelineManager(PipelineStore? store = null, ILogger<GoalPipelineManager>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Create and register a new pipeline for a goal.</summary>
    public GoalPipeline CreatePipeline(Goal goal, int maxRetries = Constants.DefaultMaxRetriesPerTask, int maxIterations = Constants.DefaultMaxIterations)
    {
        var pipeline = new GoalPipeline(goal, maxRetries, maxIterations);
        if (!_pipelines.TryAdd(goal.Id, pipeline))
            throw new InvalidOperationException($"Pipeline already exists for goal '{goal.Id}'");

        _store?.SavePipeline(pipeline);
        return pipeline;
    }

    /// <summary>Get a pipeline by goal ID.</summary>
    public GoalPipeline? GetByGoalId(string goalId) =>
        _pipelines.TryGetValue(goalId, out var p) ? p : null;

    /// <summary>Get a pipeline by its currently active task ID.</summary>
    public GoalPipeline? GetByTaskId(string taskId) =>
        _taskToGoal.TryGetValue(taskId, out var goalId) ? GetByGoalId(goalId) : null;

    /// <summary>Register a mapping from taskId → goalId so we can look up pipelines by task.</summary>
    public void RegisterTask(string taskId, string goalId)
    {
        _taskToGoal[taskId] = goalId;
        _store?.SaveTaskMapping(taskId, goalId);
    }

    /// <summary>
    /// Restore a single pipeline from the persistent store by goal ID, regardless of phase.
    /// If the pipeline is already in memory, returns the existing instance.
    /// Returns null if the store is unavailable or no pipeline is found.
    /// </summary>
    public GoalPipeline? RestorePipeline(string goalId)
    {
        // If already in memory, return existing
        if (_pipelines.TryGetValue(goalId, out var existing))
            return existing;

        if (_store is null)
            return null;

        var snapshot = _store.LoadPipeline(goalId);
        if (snapshot is null)
            return null;

        var pipeline = new GoalPipeline(snapshot);
        if (_pipelines.TryAdd(goalId, pipeline))
        {
            foreach (var (taskId, gid) in snapshot.TaskMappings)
                _taskToGoal[taskId] = gid;
        }
        else
        {
            // Another thread added it — return their instance
            return _pipelines[goalId];
        }

        return pipeline;
    }

    /// <summary>
    /// Remove a task mapping from both the in-memory dictionary and the persistent store.
    /// </summary>
    public void UnregisterTask(string taskId)
    {
        _taskToGoal.TryRemove(taskId, out _);
        _store?.DeleteTaskMapping(taskId);
    }

    /// <summary>
    /// Ownership-checked registration of a task → goal mapping: claims the mapping in memory AND
    /// (when a store is present) in the persisted <c>task_mappings</c> row, refusing rather than
    /// stealing a mapping that already belongs to someone else.
    /// </summary>
    /// <remarks>
    /// The algorithm, in order:
    /// <list type="number">
    ///   <item><description>Blank argument → a safe no-op refusal. No memory write, no SQL.</description></item>
    ///   <item><description>This manager already holds an entry for the task → refusal. The SQL is NEVER executed on this path.</description></item>
    ///   <item><description>No store → in-memory-only success.</description></item>
    ///   <item><description>The conditional write claims (or re-claims) the row → success.</description></item>
    ///   <item><description>The row belongs to another goal → PAIR-BASED rollback of our own in-memory
    ///     entry, leaving the competing row INTACT.</description></item>
    ///   <item><description>The write threw → pair-based rollback, and the exception is CARRIED on the result.</description></item>
    /// </list>
    /// NOTE: no cross-manager reconciliation is performed or claimed. The real system has exactly
    /// ONE manager (the singleton); a second manager observing the same database is a test-fixture
    /// artifact, and all this method promises there is that OUR memory matches OUR refusal.
    /// </remarks>
    /// <param name="taskId">The worker task id to map.</param>
    /// <param name="goalId">The goal that claims the task.</param>
    /// <returns>The registration outcome.</returns>
    internal TaskRegistrationResult TryRegisterTask(string taskId, string goalId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(goalId))
        {
            _logger?.LogDebug(
                "TryRegisterTask called with blank taskId or goalId — no-op refusal (taskId='{TaskId}', goalId='{GoalId}')",
                taskId, goalId);
            return new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null);
        }

        // The in-memory claim comes FIRST: a task this manager already tracks is refused before a
        // single statement reaches the database.
        if (!_taskToGoal.TryAdd(taskId, goalId))
        {
            _logger?.LogWarning(
                "Task {TaskId} is already mapped to goal {ExistingGoalId}; refusing registration for goal {GoalId}",
                taskId, _taskToGoal.TryGetValue(taskId, out var existing) ? existing : "(unknown)", goalId);
            return new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null);
        }

        if (_store is null)
            return new TaskRegistrationResult(true, TaskRegistrationFailure.None, null);

        bool persisted;
        try
        {
            persisted = _store.TrySaveTaskMappingIfUnowned(taskId, goalId);
        }
        catch (Exception ex)
        {
            // Pair-based rollback removes OUR entry only — never another goal's.
            _taskToGoal.TryRemove(KeyValuePair.Create(taskId, goalId));
            _logger?.LogError(ex, "Failed to persist task mapping {TaskId} → {GoalId}", taskId, goalId);
            return new TaskRegistrationResult(false, TaskRegistrationFailure.PersistenceFailed, ex);
        }

        if (persisted)
            return new TaskRegistrationResult(true, TaskRegistrationFailure.None, null);

        // The persisted row is another goal's. Roll our own in-memory entry back so this manager's
        // memory agrees with the refusal; the competing row stays INTACT.
        _taskToGoal.TryRemove(KeyValuePair.Create(taskId, goalId));
        _logger?.LogWarning(
            "Task mapping {TaskId} is already owned by another goal in the store; refusing registration for goal {GoalId}",
            taskId, goalId);
        return new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null);
    }

    /// <summary>
    /// Ownership-checked removal of a task → goal mapping. NEVER THROWS.
    /// </summary>
    /// <remarks>
    /// The in-memory removal is PAIR-BASED: an entry that has already been re-pointed at another
    /// goal is left alone and reported as <c>(false, false)</c>. When the memory entry was ours,
    /// the persisted row is deleted conditionally; a 0-row delete (row absent or another goal's)
    /// and a store exception both yield <c>(true, false)</c> — the residue is SIGNALLED in the
    /// record rather than silently swallowed. With no store there is nothing to persist, so the
    /// persistence flag is vacuously <c>true</c>.
    /// </remarks>
    /// <param name="taskId">The worker task id to unmap.</param>
    /// <param name="goalId">The goal that must own the mapping.</param>
    /// <returns>The unregistration outcome.</returns>
    internal TaskUnregisterResult TryUnregisterTask(string taskId, string goalId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(goalId))
        {
            _logger?.LogDebug(
                "TryUnregisterTask called with blank taskId or goalId — no-op (taskId='{TaskId}', goalId='{GoalId}')",
                taskId, goalId);
            return new TaskUnregisterResult(false, false);
        }

        if (!_taskToGoal.TryRemove(KeyValuePair.Create(taskId, goalId)))
        {
            _logger?.LogDebug(
                "Task {TaskId} is not mapped to goal {GoalId} in memory — nothing removed",
                taskId, goalId);
            return new TaskUnregisterResult(false, false);
        }

        if (_store is null)
            return new TaskUnregisterResult(true, true);

        try
        {
            var deleted = _store.DeleteTaskMappingIfForGoal(taskId, goalId);
            if (!deleted)
            {
                _logger?.LogWarning(
                    "Task mapping {TaskId} for goal {GoalId} was not deleted (absent or owned by another goal); persisted residue remains",
                    taskId, goalId);
            }
            return new TaskUnregisterResult(true, deleted);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex, "Failed to delete persisted task mapping {TaskId} → {GoalId}; persisted residue remains", taskId, goalId);
            return new TaskUnregisterResult(true, false);
        }
    }

    /// <summary>Persist the current state of a pipeline (call after state mutations).</summary>
    public void PersistState(GoalPipeline pipeline) => _store?.SavePipelineState(pipeline);

    /// <summary>Persist the full pipeline including conversation.</summary>
    public void PersistFull(GoalPipeline pipeline) => _store?.SavePipeline(pipeline);

    /// <summary>Get all active (non-completed) pipelines.</summary>
    public IReadOnlyList<GoalPipeline> GetActivePipelines() =>
        _pipelines.Values
            .Where(p => p.Phase is not (GoalPhase.Done or GoalPhase.Failed))
            .ToList()
            .AsReadOnly();

    /// <summary>Get all pipelines regardless of state.</summary>
    public IReadOnlyList<GoalPipeline> GetAllPipelines() =>
        _pipelines.Values.ToList().AsReadOnly();

    /// <summary>Remove a completed pipeline to free memory and clean up storage.</summary>
    public bool RemovePipeline(string goalId)
    {
        var wasInMemory = _pipelines.TryRemove(goalId, out _);
        if (wasInMemory)
        {
            foreach (var key in _taskToGoal.Where(kv => kv.Value == goalId).Select(kv => kv.Key).ToList())
                _taskToGoal.TryRemove(key, out _);
        }
        _store?.RemovePipeline(goalId);  // always clean up the store, even if not in memory
        return wasInMemory;
    }

    /// <summary>Restore pipelines from persistent store (called once at startup).</summary>
    public List<GoalPipeline> RestoreFromStore()
    {
        if (_store is null) return [];

        var snapshots = _store.LoadActivePipelines();
        var restored = new List<GoalPipeline>();

        foreach (var snap in snapshots)
        {
            var pipeline = new GoalPipeline(snap);
            if (_pipelines.TryAdd(snap.GoalId, pipeline))
            {
                foreach (var (taskId, goalId) in snap.TaskMappings)
                    _taskToGoal[taskId] = goalId;
                restored.Add(pipeline);
            }
        }

        return restored;
    }

    /// <summary>
    /// Returns the persisted session JSON for the specified role in a goal's pipeline,
    /// or <c>null</c> if the goal or role session does not exist.
    /// </summary>
    /// <param name="goalId">The goal whose pipeline to look up.</param>
    /// <param name="roleName">The role name whose session to retrieve (case-insensitive).</param>
    /// <returns>The session JSON, or <c>null</c>.</returns>
    public string? GetRoleSession(string goalId, string roleName)
    {
        var pipeline = GetByGoalId(goalId);
        return pipeline?.GetRoleSession(roleName);
    }

    /// <summary>
    /// Stores the session JSON for the specified role in a goal's pipeline,
    /// then flushes the updated pipeline state to the persistent store so sessions
    /// survive orchestrator restarts.
    /// Does nothing if the goal pipeline does not exist.
    /// </summary>
    /// <param name="goalId">The goal whose pipeline to update.</param>
    /// <param name="roleName">The role name whose session to store (case-insensitive).</param>
    /// <param name="sessionJson">The serialised session JSON to persist.</param>
    public void SetRoleSession(string goalId, string roleName, string sessionJson)
    {
        var pipeline = GetByGoalId(goalId);
        if (pipeline is null) return;

        pipeline.SetRoleSession(roleName, sessionJson);
        PersistState(pipeline);
    }
}
