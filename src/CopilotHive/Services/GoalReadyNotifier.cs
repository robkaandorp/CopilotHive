namespace CopilotHive.Services;

/// <summary>
/// Provides a wake-up signal used by the <see cref="GoalDispatcher"/> polling loop.
/// When a goal transitions to <c>GoalStatus.Pending</c>, callers invoke
/// <see cref="NotifyGoalReady()"/> so the dispatcher can run the next iteration
/// immediately instead of waiting for the full poll interval.
/// </summary>
public sealed class GoalReadyNotifier
{
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    /// <summary>
    /// Releases the wake signal non-blocking. Multiple rapid calls coalesce into
    /// a single signal because the semaphore's max count is one.
    /// </summary>
    public void NotifyGoalReady()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Signal is already set; another loop iteration will pick up the work.
        }
    }

    /// <summary>
    /// Waits for a wake signal up to the specified timeout, or until cancellation.
    /// Returns <c>true</c> if the signal was received before the timeout expired.
    /// </summary>
    public Task<bool> WaitForSignalAsync(TimeSpan timeout, CancellationToken ct)
        => _wakeSignal.WaitAsync(timeout, ct);
}
