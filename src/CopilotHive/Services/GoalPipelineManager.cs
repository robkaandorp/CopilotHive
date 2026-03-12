using System.Collections.Concurrent;
using CopilotHive.Goals;

namespace CopilotHive.Services;

/// <summary>
/// Singleton that holds all active goal pipelines and provides lookup by goalId or taskId.
/// </summary>
public sealed class GoalPipelineManager
{
    private readonly ConcurrentDictionary<string, GoalPipeline> _pipelines = new();
    private readonly ConcurrentDictionary<string, string> _taskToGoal = new();

    /// <summary>Create and register a new pipeline for a goal.</summary>
    public GoalPipeline CreatePipeline(Goal goal, int maxRetries = 3)
    {
        var pipeline = new GoalPipeline(goal, maxRetries);
        if (!_pipelines.TryAdd(goal.Id, pipeline))
            throw new InvalidOperationException($"Pipeline already exists for goal '{goal.Id}'");
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
    }

    /// <summary>Get all active (non-completed) pipelines.</summary>
    public IReadOnlyList<GoalPipeline> GetActivePipelines() =>
        _pipelines.Values
            .Where(p => p.Phase is not (GoalPhase.Done or GoalPhase.Failed))
            .ToList()
            .AsReadOnly();

    /// <summary>Get all pipelines regardless of state.</summary>
    public IReadOnlyList<GoalPipeline> GetAllPipelines() =>
        _pipelines.Values.ToList().AsReadOnly();

    /// <summary>Remove a completed pipeline to free memory.</summary>
    public bool RemovePipeline(string goalId)
    {
        if (_pipelines.TryRemove(goalId, out var pipeline))
        {
            // Clean up task mappings for this pipeline
            foreach (var key in _taskToGoal.Where(kv => kv.Value == goalId).Select(kv => kv.Key).ToList())
                _taskToGoal.TryRemove(key, out _);
            return true;
        }
        return false;
    }
}
