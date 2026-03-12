using System.Collections.Concurrent;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

public sealed class TaskQueue
{
    private readonly ConcurrentQueue<TaskAssignment> _pending = new();
    private readonly ConcurrentDictionary<string, TaskAssignment> _active = new();

    public void Enqueue(TaskAssignment task) => _pending.Enqueue(task);

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

    public void MarkActive(string taskId, string workerId)
    {
        if (_active.TryGetValue(taskId, out var task))
            task.Metadata["assigned_worker"] = workerId;
    }

    public void MarkComplete(string taskId) => _active.TryRemove(taskId, out _);

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
