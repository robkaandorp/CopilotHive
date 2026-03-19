namespace CopilotHive.Services;

/// <summary>
/// Singleton that bridges task completion events from the gRPC service
/// (scoped per-call) to the GoalDispatcher (singleton).
/// </summary>
/// <remarks>
/// Because the gRPC service is registered as a scoped dependency while the
/// <c>GoalDispatcher</c> is a singleton, they cannot be injected into each
/// other directly. <see cref="TaskCompletionNotifier"/> acts as the
/// intermediary: the gRPC service calls <see cref="NotifyAsync"/> to
/// propagate a <see cref="TaskResult"/> message, and the
/// <c>GoalDispatcher</c> subscribes to <see cref="OnTaskCompleted"/> at
/// application startup to react to those messages.
/// </remarks>
public sealed class TaskCompletionNotifier
{
    /// <summary>
    /// Raised when a worker reports that a task has been completed.
    /// </summary>
    /// <remarks>
    /// Subscribers receive the full <see cref="TaskResult"/> domain message
    /// and are expected to return a <see cref="Task"/> so that asynchronous
    /// handling is supported. If no subscribers are registered (i.e. the
    /// event is <see langword="null"/>), the notification is silently dropped.
    /// </remarks>
    public event Func<TaskResult, Task>? OnTaskCompleted;

    /// <summary>
    /// Fires the <see cref="OnTaskCompleted"/> event with the given result.
    /// </summary>
    /// <param name="result">
    /// The task result converted from the gRPC completion message. Must not
    /// be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> that represents the completion of the last
    /// delegate in the invocation chain. Because <see cref="OnTaskCompleted"/>
    /// is a multicast delegate, all subscribers are invoked, but only the
    /// <see cref="Task"/> returned by the final subscriber is awaited. If no
    /// handler is subscribed the returned task completes immediately.
    /// </returns>
    /// <remarks>
    /// <see cref="OnTaskCompleted"/> supports multiple subscribers via
    /// multicast delegate semantics. All registered subscribers are invoked
    /// in subscription order; however, only the <see cref="Task"/> returned
    /// by the last subscriber in the invocation chain is awaited. Earlier
    /// subscribers' returned tasks are not observed by this method.
    /// </remarks>
    public async Task NotifyAsync(TaskResult result)
    {
        if (OnTaskCompleted is not null)
            await OnTaskCompleted(result);
    }
}
