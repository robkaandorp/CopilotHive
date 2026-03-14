using CopilotHive.Shared.Grpc;

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
/// propagate a <see cref="TaskComplete"/> message, and the
/// <c>GoalDispatcher</c> subscribes to <see cref="OnTaskCompleted"/> at
/// application startup to react to those messages.
/// </remarks>
public sealed class TaskCompletionNotifier
{
    /// <summary>
    /// Raised when a worker reports that a task has been completed.
    /// </summary>
    /// <remarks>
    /// Subscribers receive the full <see cref="TaskComplete"/> protobuf
    /// message and are expected to return a <see cref="Task"/> so that
    /// asynchronous handling is supported. If no subscribers are registered
    /// (i.e. the event is <see langword="null"/>), the notification is
    /// silently dropped.
    /// </remarks>
    public event Func<TaskComplete, Task>? OnTaskCompleted;

    /// <summary>
    /// Fires the <see cref="OnTaskCompleted"/> event with the given
    /// completion message, awaiting all registered handlers.
    /// </summary>
    /// <param name="complete">
    /// The task completion message received from the worker via gRPC. Must
    /// not be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> that completes once the subscribed handler (if
    /// any) has finished processing the notification. If no handler is
    /// subscribed the returned task completes immediately.
    /// </returns>
    /// <remarks>
    /// Only a single handler delegate is supported by the underlying event
    /// pattern. If multiple listeners are required, the handler itself is
    /// responsible for fan-out.
    /// </remarks>
    public async Task NotifyAsync(TaskComplete complete)
    {
        if (OnTaskCompleted is not null)
            await OnTaskCompleted(complete);
    }
}
