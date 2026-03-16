using System.Collections.Concurrent;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

/// <summary>
/// Thread-safe queue of pending and active task assignments.
/// Supports role-based dequeue so workers only receive tasks matching their role.
/// </summary>
public sealed class TaskQueue
{
    private readonly ConcurrentQueue<TaskAssignment> _pending = new();
    private readonly ConcurrentDictionary<string, TaskAssignment> _active = new();

    /// <summary>
    /// Adds a task to the pending queue.
    /// </summary>
    /// <param name="task">The task assignment to enqueue.</param>
    public void Enqueue(TaskAssignment task)
    {
        _pending.Enqueue(task);
        OnEnqueue?.Invoke(task);
    }

    /// <summary>
    /// Optional callback invoked synchronously after each enqueue.
    /// Intended for test hooks that need to observe or react to dispatched tasks.
    /// </summary>
    public Action<TaskAssignment>? OnEnqueue { get; set; }

    /// <summary>
    /// Dequeue a pending task that matches the requested worker role.
    /// Returns <c>null</c> if no matching task is available.
    /// </summary>
    public TaskAssignment? TryDequeue(WorkerRole role)
    {
        // Drain and re-enqueue non-matching items (bounded by queue size).
        var skipped = new List<TaskAssignment>();

        while (_pending.TryDequeue(out var task))
        {
            if (task.Role == role)
            {
                // Re-enqueue everything we skipped.
                foreach (var s in skipped)
                    _pending.Enqueue(s);

                return task;
            }

            skipped.Add(task);
        }

        // Nothing matched — put everything back.
        foreach (var s in skipped)
            _pending.Enqueue(s);

        return null;
    }

    /// <summary>
    /// Records that a task is now being handled by the specified worker.
    /// </summary>
    /// <param name="taskId">Identifier of the task to mark as active.</param>
    /// <param name="workerId">Identifier of the worker that accepted the task.</param>
    public void MarkActive(string taskId, string workerId)
    {
        if (_active.TryGetValue(taskId, out var task))
            task.Metadata["assigned_worker"] = workerId;
    }

    /// <summary>
    /// Removes a task from the active dictionary when it has been completed.
    /// </summary>
    /// <param name="taskId">Identifier of the task to remove.</param>
    public void MarkComplete(string taskId) => _active.TryRemove(taskId, out _);

    /// <summary>
    /// Looks up a currently active task by its identifier.
    /// </summary>
    /// <param name="taskId">Identifier of the task to retrieve.</param>
    /// <returns>The active task, or <c>null</c> if not found.</returns>
    public TaskAssignment? GetActiveTask(string taskId) =>
        _active.GetValueOrDefault(taskId);

    /// <summary>
    /// Move a task from the pending dequeue result into the active dictionary.
    /// </summary>
    public void Activate(TaskAssignment task, string workerId)
    {
        _active[task.TaskId] = task;
        task.Metadata["assigned_worker"] = workerId;
    }
}
