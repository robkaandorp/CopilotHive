using CopilotHive.Shared;

namespace CopilotHive.Tests;

public class CopilotRetryPolicyTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_SucceedsOnFirstAttempt_ReturnsResult()
    {
        var result = await CopilotRetryPolicy.ExecuteAsync(
            () => Task.FromResult(42), delayFunc: NoDelay, ct: TestContext.Current.CancellationToken);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ExecuteAsync_SucceedsAfterTransientFailures_ReturnsResult()
    {
        var attempts = 0;
        var result = await CopilotRetryPolicy.ExecuteAsync(() =>
        {
            attempts++;
            if (attempts < 3)
                throw new InvalidOperationException("Transient error");
            return Task.FromResult(99);
        }, delayFunc: NoDelay, ct: TestContext.Current.CancellationToken);

        Assert.Equal(99, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_InvokesOnRetryCallback()
    {
        var retryAttempts = new List<int>();
        var attempts = 0;

        await CopilotRetryPolicy.ExecuteAsync(() =>
        {
            attempts++;
            if (attempts < 2)
                throw new InvalidOperationException("fail");
            return Task.FromResult(0);
        },
        onRetry: (attempt, _, _) => retryAttempts.Add(attempt),
        delayFunc: NoDelay,
        ct: TestContext.Current.CancellationToken);

        Assert.Single(retryAttempts);
        Assert.Equal(1, retryAttempts[0]);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsRealCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CopilotRetryPolicy.ExecuteAsync(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            },
            ct: cts.Token, delayFunc: NoDelay));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    [InlineData(5, 160)]
    [InlineData(6, 300)] // capped at MaxDelay (5 min)
    [InlineData(9, 300)] // capped at MaxDelay (5 min)
    public void GetDelay_ReturnsExponentialBackoffCappedAtMax(int attempt, double expectedSeconds)
    {
        var delay = CopilotRetryPolicy.GetDelay(attempt);
        Assert.Equal(expectedSeconds, delay.TotalSeconds);
    }

    [Fact]
    public void Constants_AreExpected()
    {
        Assert.Equal(10, CopilotRetryPolicy.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(5), CopilotRetryPolicy.InitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(5), CopilotRetryPolicy.MaxDelay);
    }

    /// <summary>
    /// <see cref="KeyNotFoundException"/> is the missing-child / missing-goal signal and is NEVER
    /// transient: it must propagate immediately on the FIRST attempt — exactly one action
    /// invocation, no delay, no onRetry callback — otherwise a cancelled goal's missing child
    /// actor burns the entire retry budget (with multi-minute backoffs) instead of failing at once.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_KeyNotFoundException_NotRetried_NoDelayAndSingleAttempt()
    {
        var attempts = 0;
        var delayInvocations = 0;
        var retryCallbacks = 0;
        var thrown = new KeyNotFoundException("No child actor for goal 'goal-cancelled'.");

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CopilotRetryPolicy.ExecuteAsync<int>(
                () =>
                {
                    attempts++;
                    throw thrown;
                },
                onRetry: (_, _, _) => retryCallbacks++,
                delayFunc: (_, _) => { delayInvocations++; return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken));

        // The exact instance propagates — never wrapped, never replaced.
        Assert.Same(thrown, ex);
        Assert.Equal(1, attempts);
        Assert.Equal(0, delayInvocations);
        Assert.Equal(0, retryCallbacks);
    }

    /// <summary>
    /// The complement of the test above: a generic (transient-looking) exception keeps its
    /// current retry behaviour — one initial attempt plus <see cref="CopilotRetryPolicy.MaxRetries"/>
    /// retries, each preceded by its backoff delay.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GenericException_StillRetried()
    {
        var attempts = 0;
        var delayInvocations = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CopilotRetryPolicy.ExecuteAsync<int>(
                () =>
                {
                    attempts++;
                    throw new InvalidOperationException("provider timeout");
                },
                delayFunc: (_, _) => { delayInvocations++; return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken));

        Assert.Equal("provider timeout", ex.Message);
        Assert.Equal(CopilotRetryPolicy.MaxRetries + 1, attempts);
        Assert.Equal(CopilotRetryPolicy.MaxRetries, delayInvocations);
    }

    /// <summary>
    /// The non-generic overload routes through the same retry loop, so it inherits the
    /// never-retried <see cref="KeyNotFoundException"/> contract too.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_VoidOverload_KeyNotFoundException_NotRetried()
    {
        var attempts = 0;
        var delayInvocations = 0;

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CopilotRetryPolicy.ExecuteAsync(
                () =>
                {
                    attempts++;
                    throw new KeyNotFoundException("missing goal");
                },
                delayFunc: (_, _) => { delayInvocations++; return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Equal(0, delayInvocations);
    }

    [Fact]
    public async Task ExecuteAsync_VoidOverload_Works()
    {
        var called = false;
        await CopilotRetryPolicy.ExecuteAsync(() =>
        {
            called = true;
            return Task.CompletedTask;
        }, delayFunc: NoDelay, ct: TestContext.Current.CancellationToken);
        Assert.True(called);
    }

    [Fact]
    public async Task ExecuteAsync_PassesDelaysToDelayFunc()
    {
        var recordedDelays = new List<TimeSpan>();
        var attempts = 0;

        await CopilotRetryPolicy.ExecuteAsync(() =>
        {
            attempts++;
            if (attempts <= 3)
                throw new InvalidOperationException("fail");
            return Task.FromResult(0);
        },
        delayFunc: (delay, _) => { recordedDelays.Add(delay); return Task.CompletedTask; },
        ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, recordedDelays.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), recordedDelays[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), recordedDelays[1]);
        Assert.Equal(TimeSpan.FromSeconds(20), recordedDelays[2]);
    }
}
