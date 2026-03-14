using System.Collections.Concurrent;
using CopilotHive.Goals;
using CopilotHive.Persistence;

namespace CopilotHive.Services;

/// <summary>
/// Singleton that holds all active goal pipelines and provides lookup by goalId or taskId.
/// Persists pipeline state to SQLite via <see cref="PipelineStore"/>.
/// </summary>
public sealed class GoalPipelineManager
{
    private readonly ConcurrentDictionary<string, GoalPipeline> _pipelines = new();
    private readonly ConcurrentDictionary<string, string> _taskToGoal = new();
    private readonly PipelineStore? _store;

    /// <summary>
    /// Initialises a new <see cref="GoalPipelineManager"/>.
    /// </summary>
    /// <param name="store">Optional persistence store; when provided, pipeline state is saved to SQLite.</param>
    public GoalPipelineManager(PipelineStore? store = null)
    {
        _store = store;
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
        if (_pipelines.TryRemove(goalId, out _))
        {
            foreach (var key in _taskToGoal.Where(kv => kv.Value == goalId).Select(kv => kv.Key).ToList())
                _taskToGoal.TryRemove(key, out _);
            _store?.RemovePipeline(goalId);
            return true;
        }
        return false;
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
}
