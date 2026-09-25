using System.Collections.Concurrent;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Thread-safe queue of pending and active <see cref="WorkTask"/> instances.
/// Supports role-based dequeue so workers only receive tasks matching their role.
/// </summary>
public sealed class TaskQueue
{
    private readonly ConcurrentQueue<WorkTask> _pending = new();
    private readonly ConcurrentDictionary<string, WorkTask> _active = new();

    /// <summary>
    /// Guards every read-modify-write of the <see cref="_active"/> dictionary: the
    /// presence-then-add of <see cref="TryActivateNew"/>, the presence check plus metadata write of
    /// <see cref="Activate"/>, and the reference-checked removal of
    /// <see cref="TryRemoveOwned"/>. The dictionary is only made thread-safe by
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>; it establishes nothing about a
    /// check-then-act sequence, which is exactly what would let <see cref="TryActivateNew"/>
    /// overwrite a competing entry. <see cref="MarkComplete"/> takes the same lock so a completion
    /// cannot land between another caller's presence check and its write.
    /// </summary>
    private readonly object _activeLock = new();

    /// <summary>
    /// Adds a task to the pending queue.
    /// </summary>
    /// <param name="task">The work task to enqueue.</param>
    public void Enqueue(WorkTask task)
    {
        _pending.Enqueue(task);
        OnEnqueue?.Invoke(task);
    }

    /// <summary>
    /// Optional callback invoked synchronously after each enqueue.
    /// Intended for test hooks that need to observe or react to dispatched tasks.
    /// </summary>
    public Action<WorkTask>? OnEnqueue { get; set; }

    /// <summary>
    /// Dequeue a pending task that matches the requested worker role.
    /// Returns <c>null</c> if no matching task is available.
    /// </summary>
    public WorkTask? TryDequeue(WorkerRole role)
    {
        if (role == WorkerRole.Unspecified)
            return TryDequeueAny();

        // Drain and re-enqueue non-matching items (bounded by queue size).
        var skipped = new List<WorkTask>();

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
    /// Dequeue the next pending task regardless of role. Used by generic workers.
    /// </summary>
    public WorkTask? TryDequeueAny()
    {
        return _pending.TryDequeue(out var task) ? task : null;
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
    public void MarkComplete(string taskId)
    {
        lock (_activeLock)
        {
            _active.TryRemove(taskId, out _);
        }
    }

    /// <summary>
    /// Looks up a currently active task by its identifier.
    /// </summary>
    /// <param name="taskId">Identifier of the task to retrieve.</param>
    /// <returns>The active task, or <c>null</c> if not found.</returns>
    public WorkTask? GetActiveTask(string taskId) =>
        _active.GetValueOrDefault(taskId);

    /// <summary>
    /// Move a task from the pending dequeue result into the active dictionary.
    /// </summary>
    public void Activate(WorkTask task, string workerId)
    {
        lock (_activeLock)
        {
            _active[task.TaskId] = task;
            task.Metadata["assigned_worker"] = workerId;
        }
    }

    /// <summary>
    /// THE NON-OVERWRITING ACTIVE-ENTRY INSERT: records <paramref name="task"/> as the active entry
    /// for its task id ONLY when no entry exists for that id.
    /// </summary>
    /// <remarks>
    /// WHY IT IS NOT <see cref="Activate"/>: that method's indexer assignment REPLACES whatever
    /// instance is already registered, which would silently discard a live entry (and its already
    /// recorded <c>assigned_worker</c> metadata). This primitive NEVER overwrites: the presence
    /// check and the insert happen in ONE <see cref="_activeLock"/> span, so a competing insert
    /// cannot slip between them, and a false answer means precisely "an entry already exists and
    /// nothing at all was changed".
    /// <para>
    /// The <c>assigned_worker</c> metadata is written only on the successful path, so a refused
    /// call leaves the EXISTING entry's metadata untouched — including the value written by
    /// whichever caller won.
    /// </para>
    /// </remarks>
    /// <param name="task">The task to register as active; must not be <c>null</c>.</param>
    /// <param name="workerId">Identifier of the worker the task is assigned to.</param>
    /// <returns>
    /// <c>true</c> when this call added the entry; <c>false</c> when an entry already existed for
    /// the task id and nothing was changed.
    /// </returns>
    internal bool TryActivateNew(WorkTask task, string workerId)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_activeLock)
        {
            if (_active.ContainsKey(task.TaskId))
                return false;

            task.Metadata["assigned_worker"] = workerId;
            _active[task.TaskId] = task;
            return true;
        }
    }

    /// <summary>
    /// THE REFERENCE-CHECKED ACTIVE-ENTRY REMOVAL: removes the entry for <paramref name="taskId"/>
    /// ONLY when the entry currently registered there is the SAME INSTANCE as
    /// <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// WHY IT IS NOT <c>TryRemove(KeyValuePair&lt;string, WorkTask&gt;)</c>:
    /// <see cref="WorkTask"/> is a RECORD, so the pair overload compares by VALUE — an equal-valued
    /// but DISTINCT instance (a rebuilt copy, or another caller's identically populated task) would
    /// match and be removed. This method uses <see cref="object.ReferenceEquals(object?, object?)"/> instead, so it removes
    /// exactly the instance the caller added and NEVER a successor or a copy. The whole decision is
    /// one <see cref="_activeLock"/> span, so the removal cannot land against a changed dictionary.
    /// </remarks>
    /// <param name="taskId">Identifier of the entry to remove.</param>
    /// <param name="expected">The exact instance the caller added; must not be <c>null</c>.</param>
    /// <returns>
    /// <c>true</c> when the identical instance was registered and has been removed; <c>false</c>
    /// when the id is absent, the current entry is a different instance, or the id is blank — with
    /// nothing changed.
    /// </returns>
    internal bool TryRemoveOwned(string taskId, WorkTask expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        if (string.IsNullOrWhiteSpace(taskId))
            return false;

        lock (_activeLock)
        {
            if (!_active.TryGetValue(taskId, out var current) || !ReferenceEquals(current, expected))
                return false;

            return _active.TryRemove(taskId, out _);
        }
    }
}
