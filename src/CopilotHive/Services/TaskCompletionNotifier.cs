using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

/// <summary>
/// Singleton that bridges task completion events from the gRPC service
/// (scoped per-call) to the GoalDispatcher (singleton).
/// </summary>
public sealed class TaskCompletionNotifier
{
    /// <summary>Raised when a worker reports that a task has been completed.</summary>
    public event Func<TaskComplete, Task>? OnTaskCompleted;

    /// <summary>
    /// Fires the <see cref="OnTaskCompleted"/> event with the given completion message.
    /// </summary>
    /// <param name="complete">The task completion message from the worker.</param>
    public async Task NotifyAsync(TaskComplete complete)
    {
        if (OnTaskCompleted is not null)
            await OnTaskCompleted(complete);
    }
}
