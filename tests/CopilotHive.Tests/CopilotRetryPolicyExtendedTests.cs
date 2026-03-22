using CopilotHive.Shared;

namespace CopilotHive.Tests;

/// <summary>
/// Additional unit tests for <see cref="CopilotRetryPolicy"/> covering max-retry exhaustion,
/// non-retryable exception propagation, and delay-function invocation counts.
/// </summary>
public sealed class CopilotRetryPolicyExtendedTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    // ── Max retry count ──────────────────────────────────────────────────────

    /// <summary>
    /// When an operation always throws, it should be called exactly MaxRetries + 1 times
    /// (the initial attempt plus one per retry) before the final exception propagates.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AlwaysFails_IsCalledMaxRetriesPlusOneTimes()
    {
        var callCount = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CopilotRetryPolicy.ExecuteAsync<int>(() =>
            {
                callCount++;
                throw new InvalidOperationException("always fails");
            }, delayFunc: NoDelay, ct: TestContext.Current.CancellationToken));

        Assert.Equal(CopilotRetryPolicy.MaxRetries + 1, callCount);
        Assert.Equal("always fails", ex.Message);
    }

    /// <summary>
    /// After all retries are exhausted, the exception from the final attempt propagates
    /// to the caller rather than being swallowed.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ExhaustsRetries_PropagatesFinalException()
    {
        var attemptNumber = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CopilotRetryPolicy.ExecuteAsync<int>(() =>
            {
                attemptNumber++;
                throw new InvalidOperationException($"attempt {attemptNumber}");
            }, delayFunc: NoDelay, ct: TestContext.Current.CancellationToken));

        // The final exception carries the message from the last attempt
        Assert.Equal($"attempt {CopilotRetryPolicy.MaxRetries + 1}", ex.Message);
    }

    // ── Non-retryable exceptions ─────────────────────────────────────────────

    /// <summary>
    /// An <see cref="OperationCanceledException"/> thrown when the cancellation token is
    /// signalled must NOT be retried — it propagates immediately on the first attempt.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OperationCanceled_WithCanceledToken_NotRetried()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var callCount = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CopilotRetryPolicy.ExecuteAsync<int>(() =>
            {
                callCount++;
                throw new OperationCanceledException(cts.Token);
            }, delayFunc: NoDelay, ct: cts.Token));

        // Must not retry — only one call should have been made
        Assert.Equal(1, callCount);
    }

    // ── Delay logic ──────────────────────────────────────────────────────────

    /// <summary>
    /// The delay function is called exactly once per retry (i.e. MaxRetries times total
    /// when the operation always fails).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AlwaysFails_DelayFuncCalledMaxRetryTimes()
    {
        var delayCallCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CopilotRetryPolicy.ExecuteAsync<int>(
                () => throw new InvalidOperationException("fail"),
                delayFunc: (_, _) => { delayCallCount++; return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken));

        Assert.Equal(CopilotRetryPolicy.MaxRetries, delayCallCount);
    }

    /// <summary>
    /// Verifies that the delays passed to the delay function follow exponential backoff
    /// for the first several retries (before the cap applies).
    /// </summary>
    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    public void GetDelay_EarlyAttempts_FollowsExponentialBackoff(int attempt, double expectedSeconds)
    {
        var delay = CopilotRetryPolicy.GetDelay(attempt);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    // ── onRetry callback ─────────────────────────────────────────────────────

    /// <summary>
    /// The onRetry callback must receive each retry's 1-based attempt number in order.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AlwaysFails_OnRetryReceivesAscendingAttemptNumbers()
    {
        var reportedAttempts = new List<int>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CopilotRetryPolicy.ExecuteAsync<int>(
                () => throw new InvalidOperationException("fail"),
                onRetry: (attempt, _, _) => reportedAttempts.Add(attempt),
                delayFunc: NoDelay,
                ct: TestContext.Current.CancellationToken));

        // onRetry should be called MaxRetries times with attempts 1..MaxRetries
        Assert.Equal(CopilotRetryPolicy.MaxRetries, reportedAttempts.Count);
        Assert.Equal(Enumerable.Range(1, CopilotRetryPolicy.MaxRetries), reportedAttempts);
    }

    /// <summary>
    /// The onRetry callback receives the actual exception that caused the retry.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TransientFailure_OnRetryReceivesException()
    {
        Exception? capturedEx = null;
        var attempts = 0;

        await CopilotRetryPolicy.ExecuteAsync(() =>
        {
            attempts++;
            if (attempts == 1)
                throw new ArgumentException("bad arg");
            return Task.FromResult(0);
        },
        onRetry: (_, _, ex) => capturedEx = ex,
        delayFunc: NoDelay,
        ct: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedEx);
        Assert.IsType<ArgumentException>(capturedEx);
        Assert.Equal("bad arg", capturedEx.Message);
    }

    // ── Void overload ────────────────────────────────────────────────────────

    /// <summary>
    /// The void overload also respects MaxRetries: when the action always throws, it is
    /// called exactly MaxRetries + 1 times.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_VoidOverload_AlwaysFails_IsCalledMaxRetriesPlusOneTimes()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CopilotRetryPolicy.ExecuteAsync(() =>
            {
                callCount++;
                throw new InvalidOperationException("void always fails");
            }, delayFunc: NoDelay, ct: TestContext.Current.CancellationToken));

        Assert.Equal(CopilotRetryPolicy.MaxRetries + 1, callCount);
    }
}
