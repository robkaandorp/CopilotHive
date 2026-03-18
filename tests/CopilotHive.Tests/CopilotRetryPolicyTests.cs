using CopilotHive.Shared;

namespace CopilotHive.Tests;

public class CopilotRetryPolicyTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_SucceedsOnFirstAttempt_ReturnsResult()
    {
        var result = await CopilotRetryPolicy.ExecuteAsync(
            () => Task.FromResult(42), delayFunc: NoDelay);
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
        }, delayFunc: NoDelay);

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
        delayFunc: NoDelay);

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

    [Fact]
    public async Task ExecuteAsync_VoidOverload_Works()
    {
        var called = false;
        await CopilotRetryPolicy.ExecuteAsync(() =>
        {
            called = true;
            return Task.CompletedTask;
        }, delayFunc: NoDelay);
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
        delayFunc: (delay, _) => { recordedDelays.Add(delay); return Task.CompletedTask; });

        Assert.Equal(3, recordedDelays.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), recordedDelays[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), recordedDelays[1]);
        Assert.Equal(TimeSpan.FromSeconds(20), recordedDelays[2]);
    }
}
