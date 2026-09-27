namespace CopilotHive.Tests;

/// <summary>
/// Contract tests for the SHARED <see cref="ControlledTimeProvider"/> fixture itself — the two
/// capabilities this round adds on top of the behaviour moved out of
/// <c>ActiveEventInjectorTests</c>.
/// <para>
/// They pin (a) coherent monotonic timestamps, so <see cref="TimeProvider.GetElapsedTime(long)"/>
/// measures the MANUAL clock instead of a real stopwatch, (b) the lock-registered due-time
/// rendezvous, so producers are told apart by the exact due time they are parked on, and
/// (c) <see cref="ControlledTimeProvider.PendingTimerCount"/>, so a test can assert that no timer
/// was left behind after cleanup. Without these the suites that drive the clock would silently
/// fall back to real time.
/// </para>
/// </summary>
public sealed class ControlledTimeProviderTests
{
    // ── (a) Coherent monotonic timestamps ──────────────────────────────────

    [Fact]
    public void GetTimestamp_AdvancesExactlyWithAdvance_SoGetElapsedTimeMeasuresTheManualClock()
    {
        var clock = new ControlledTimeProvider();
        var start = clock.GetTimestamp();

        // Nothing advanced → zero elapsed. A real stopwatch would already report non-zero here.
        Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(start));

        clock.Advance(TimeSpan.FromSeconds(90));
        clock.Advance(TimeSpan.FromMilliseconds(250));

        // Exact equality: timestamp = elapsed TimeSpan ticks, frequency = TimeSpan.TicksPerSecond.
        Assert.Equal(TimeSpan.TicksPerSecond, clock.TimestampFrequency);
        Assert.Equal(90_250 * TimeSpan.TicksPerMillisecond, clock.GetTimestamp());
        Assert.Equal(TimeSpan.FromMilliseconds(90_250), clock.GetElapsedTime(start));
        Assert.Equal(TimeSpan.FromMilliseconds(90_250), clock.Elapsed);

        // The absolute clock moves with the same advance.
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 1, 30, 250, TimeSpan.Zero), clock.GetUtcNow());
    }

    // ── (b) Due-time rendezvous ────────────────────────────────────────────

    /// <summary>
    /// Distinct due times stay individually rendezvousable before the clock is advanced — a
    /// timer COUNT could not tell the overall timeout, a poll delay, a Retry-After hint and a
    /// short probe timeout apart.
    /// </summary>
    [Fact]
    public async Task WaitForPendingTimerAsync_TellsTimersApartByExactDueTime_BeforeAnyAdvance()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();

        var delay = Task.Delay(TimeSpan.FromSeconds(7), clock, ct);
        clock.CreateTimer(_ => { }, null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);

        // Each rendezvous matches only its own due time; both resolve without advancing the clock.
        await clock.WaitForPendingTimerAsync(TimeSpan.FromSeconds(7), ct);
        await clock.WaitForPendingTimerAsync(TimeSpan.FromMinutes(10), ct);

        // Neither fired: "not yet" is a fact about parked producers, not a guess from a sleep.
        Assert.False(delay.IsCompleted);
        Assert.Equal(2, clock.PendingTimerCount);
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromMinutes(10)], clock.RequestedDelays);

        clock.Advance(TimeSpan.FromSeconds(7));
        await delay;
    }

    /// <summary>
    /// The rendezvous is registered under the timer-creation lock, so a waiter armed FIRST is
    /// satisfied by a creation that happens afterwards — it cannot miss it, and no polling is
    /// involved.
    /// </summary>
    [Fact]
    public async Task WaitForPendingTimerAsync_ResolvesForATimerCreatedAfterTheWaitStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();

        var waiter = clock.WaitForPendingTimerAsync(TimeSpan.FromSeconds(7), ct);

        clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(7), Timeout.InfiniteTimeSpan);

        await waiter;
        Assert.Equal(1, clock.PendingTimerCount);
    }

    /// <summary>Only PENDING timers count: a fired timer can no longer satisfy a rendezvous.</summary>
    [Fact]
    public async Task WaitForPendingTimerAsync_DoesNotResolveForAnAlreadyFiredTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();

        clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(7), Timeout.InfiniteTimeSpan);
        await clock.WaitForPendingTimerAsync(TimeSpan.FromSeconds(7), ct);

        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(0, clock.PendingTimerCount);

        // The timer has fired, so the identical rendezvous must now block rather than resolve.
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => clock.WaitForPendingTimerAsync(TimeSpan.FromSeconds(7), giveUp.Token));
    }

    /// <summary>
    /// The exact construction production uses for its timeout: a
    /// <see cref="CancellationTokenSource"/> bound to the injected provider only ever expires when
    /// the manual clock advances past the requested window.
    /// </summary>
    [Fact]
    public async Task CancellationTokenSource_OnTheManualClock_ExpiresOnlyWhenAdvanced()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10), clock);
        _ = cts.Token;

        await clock.WaitForPendingTimerAsync(TimeSpan.FromMinutes(10), ct);
        Assert.False(cts.IsCancellationRequested);

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(cts.IsCancellationRequested);
    }

    // ── (c) Pending timer accounting ───────────────────────────────────────

    [Fact]
    public async Task PendingTimerCount_DropsOnFire_AndOnDisposal_WithoutAdvancing()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();

        Assert.Equal(0, clock.PendingTimerCount);

        var timer = clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        await clock.WaitForPendingTimerAsync(TimeSpan.FromSeconds(3), ct);
        Assert.Equal(1, clock.PendingTimerCount);

        // Fired → no longer active. This is what a post-cleanup assertion reads.
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, clock.PendingTimerCount);

        // Disposal alone clears the pending state too — no time has to pass first.
        var longTimer = clock.CreateTimer(_ => { }, null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
        await clock.WaitForPendingTimerAsync(TimeSpan.FromMinutes(10), ct);
        Assert.Equal(1, clock.PendingTimerCount);

        await longTimer.DisposeAsync();
        Assert.Equal(0, clock.PendingTimerCount);
        timer.Dispose();
        Assert.Equal(0, clock.PendingTimerCount);
    }
}
