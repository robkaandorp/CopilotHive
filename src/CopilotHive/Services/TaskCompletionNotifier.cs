using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

/// <summary>
/// Singleton that bridges task completion events from the gRPC service
/// (scoped per-call) to the GoalDispatcher (singleton).
/// </summary>
public sealed class TaskCompletionNotifier
{
    public event Func<TaskComplete, Task>? OnTaskCompleted;

    public async Task NotifyAsync(TaskComplete complete)
    {
        if (OnTaskCompleted is not null)
            await OnTaskCompleted(complete);
    }
}
