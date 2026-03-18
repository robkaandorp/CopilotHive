namespace CopilotHive.Shared;

/// <summary>
/// Exponential backoff retry policy for Copilot SDK calls.
/// Shared between orchestrator (Brain) and worker (CopilotRunner).
/// </summary>
public static class CopilotRetryPolicy
{
    /// <summary>Maximum number of retry attempts after the first failure.</summary>
    public const int MaxRetries = 10;

    /// <summary>Initial delay before the first retry.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    /// <summary>Cap on the maximum delay between retries.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>Calculates the delay for a given retry attempt (0-based) with exponential backoff, capped at <see cref="MaxDelay"/>.</summary>
    public static TimeSpan GetDelay(int attempt)
    {
        var delay = InitialDelay * Math.Pow(2, attempt);
        return delay > MaxDelay ? MaxDelay : delay;
    }

    /// <summary>
    /// Executes an async operation with exponential backoff retries.
    /// On the last attempt the exception propagates to the caller.
    /// </summary>
    /// <param name="action">The async operation to execute.</param>
    /// <param name="onRetry">Optional callback invoked before each retry wait (attempt 1-based, delay, exception).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="delayFunc">Optional delay function for testing. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        Action<int, TimeSpan, Exception>? onRetry = null,
        CancellationToken ct = default,
        Func<TimeSpan, CancellationToken, Task>? delayFunc = null)
    {
        delayFunc ??= Task.Delay;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Real cancellation — don't retry
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                var delay = GetDelay(attempt);
                onRetry?.Invoke(attempt + 1, delay, ex);
                await delayFunc(delay, ct);
            }
        }

        // Unreachable — the last attempt's exception propagates via the catch filter
        throw new InvalidOperationException("Retry loop completed without result");
    }

    /// <summary>
    /// Executes an async void operation with exponential backoff retries.
    /// </summary>
    public static async Task ExecuteAsync(
        Func<Task> action,
        Action<int, TimeSpan, Exception>? onRetry = null,
        CancellationToken ct = default,
        Func<TimeSpan, CancellationToken, Task>? delayFunc = null)
    {
        await ExecuteAsync(async () => { await action(); return 0; }, onRetry, ct, delayFunc);
    }
}
