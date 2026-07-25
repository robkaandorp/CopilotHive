using CopilotHive.Services;

namespace CopilotHive.Tests;

public sealed class RetryBudgetTests
{
    [Fact]
    public void Constructor_NegativeInput_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryBudget(-1));
    }

    [Fact]
    public void TryConsume_ReturnsTrueAndDecrementsRemaining_UntilExhausted()
    {
        var budget = new RetryBudget(3);

        Assert.True(budget.TryConsume());
        Assert.Equal(2, budget.Remaining);

        Assert.True(budget.TryConsume());
        Assert.Equal(1, budget.Remaining);

        Assert.True(budget.TryConsume());
        Assert.Equal(0, budget.Remaining);

        Assert.False(budget.TryConsume());
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void TryConsume_ReturnsFalse_WhenExhausted()
    {
        var budget = new RetryBudget(1);
        budget.TryConsume();

        Assert.False(budget.TryConsume());
    }

    [Fact]
    public void IsExhausted_True_WhenRemainingIsZero()
    {
        var budget = new RetryBudget(2);

        Assert.False(budget.IsExhausted);
        budget.TryConsume();
        Assert.False(budget.IsExhausted);
        budget.TryConsume();
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void Used_Remaining_Allowed_CorrectThroughoutLifecycle()
    {
        var budget = new RetryBudget(3);

        Assert.Equal(0, budget.Used);
        Assert.Equal(3, budget.Remaining);
        Assert.Equal(3, budget.Allowed);

        budget.TryConsume();
        Assert.Equal(1, budget.Used);
        Assert.Equal(2, budget.Remaining);
        Assert.Equal(3, budget.Allowed);

        budget.TryConsume();
        Assert.Equal(2, budget.Used);
        Assert.Equal(1, budget.Remaining);

        budget.TryConsume();
        Assert.Equal(3, budget.Used);
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void ZeroBudget_ExhaustedImmediately_TryConsumeAlwaysReturnsFalse()
    {
        var budget = new RetryBudget(0);

        Assert.True(budget.IsExhausted);
        Assert.Equal(0, budget.Allowed);
        Assert.Equal(0, budget.Remaining);
        Assert.Equal(0, budget.Used);
        Assert.False(budget.TryConsume());
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public async Task TryConsume_ConcurrentCallers_ExactlyOneSucceeds()
    {
        var budget = new RetryBudget(1);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Consumer()
        {
            await barrier.Task;
            return budget.TryConsume();
        }

        var t1 = Consumer();
        var t2 = Consumer();
        barrier.SetResult();

        var results = await Task.WhenAll(t1, t2);

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(1, results.Count(r => !r));
        Assert.True(budget.IsExhausted);
        Assert.Equal(0, budget.Remaining);
        Assert.Equal(1, budget.Used);
    }

    private const int MaxCap = int.MaxValue - 1;

    [Fact]
    public void TopUp_AfterExhaustion_GrantsExactlyNConsumes()
    {
        var budget = new RetryBudget(3);
        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.True(budget.IsExhausted);

        budget.TopUp(5);
        Assert.Equal(5, budget.Remaining);

        for (var i = 0; i < 5; i++)
            Assert.True(budget.TryConsume());
        Assert.False(budget.TryConsume());

        Assert.Equal(8, budget.Used);
        Assert.Equal(8, budget.Allowed);
    }

    [Fact]
    public async Task TopUp_RacingWithTryConsume_NoLostUpdates()
    {
        var budget = new RetryBudget(1);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Consumer()
        {
            await barrier.Task;
            return budget.TryConsume();
        }

        async Task TopUpper()
        {
            await barrier.Task;
            budget.TopUp(10);
        }

        var c1 = Consumer();
        var c2 = Consumer();
        var c3 = Consumer();
        var t = TopUpper();

        barrier.SetResult();

        var results = await Task.WhenAll(c1, c2, c3);
        await t;

        // At least one consume succeeded from the original budget of 1.
        Assert.True(budget.Used >= 1);
        // Invariant: Remaining + Used == Allowed (no lost updates).
        Assert.Equal(budget.Allowed, budget.Remaining + budget.Used);
    }

    [Fact]
    public void TopUp_SaturationPreservesInvariants()
    {
        var budget = new RetryBudget(MaxCap);
        Assert.Equal(MaxCap, budget.Allowed);
        Assert.Equal(MaxCap, budget.Remaining);
        Assert.Equal(0, budget.Used);

        budget.TopUp(1000);
        Assert.Equal(MaxCap, budget.Allowed); // saturated, no overflow
        Assert.Equal(MaxCap, budget.Remaining);
        Assert.Equal(0, budget.Used);

        budget.TopUp(1);
        Assert.Equal(MaxCap, budget.Allowed);
        Assert.True(budget.Remaining <= budget.Allowed);
        Assert.True(budget.Used >= 0);
    }

    [Fact]
    public void TopUp_ConstructorCapsAtMaxCap()
    {
        var budget = new RetryBudget(int.MaxValue);

        Assert.Equal(int.MaxValue - 1, budget.Allowed);
        Assert.Equal(int.MaxValue - 1, budget.Remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    [InlineData(int.MaxValue)]
    public void TopUp_InvalidInput_Throws(int additional)
    {
        var budget = new RetryBudget(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TopUp(additional));
    }

    [Fact]
    public void TopUp_NormalizesNegativeRemaining()
    {
        var budget = new RetryBudget(2);
        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.False(budget.TryConsume()); // _remaining is now -1

        budget.TopUp(5);

        Assert.Equal(5, budget.Remaining); // normalized to 0, then +5
        Assert.Equal(7, budget.Allowed);
        Assert.Equal(2, budget.Used);
    }

    [Fact]
    public void TopUp_AfterExhaustion_ThenConsumeUntilExhaustedAgain()
    {
        var budget = new RetryBudget(3);
        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.True(budget.IsExhausted);

        budget.TopUp(5);

        for (var i = 0; i < 5; i++)
            Assert.True(budget.TryConsume());

        Assert.False(budget.TryConsume()); // 6th consume fails

        Assert.Equal(8, budget.Allowed);
        Assert.Equal(8, budget.Used);
        Assert.Equal(0, budget.Remaining);
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void TopUp_MultipleTimes_AccumulatesCorrectly()
    {
        var budget = new RetryBudget(2);
        budget.TopUp(3);
        budget.TopUp(4);

        Assert.Equal(9, budget.Allowed);
        Assert.Equal(9, budget.Remaining);

        for (var i = 0; i < 9; i++)
            Assert.True(budget.TryConsume());

        Assert.False(budget.TryConsume()); // 10th fails
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public async Task TopUp_ConcurrentTopUps_AllUnitsAdded()
    {
        var budget = new RetryBudget(0);
        const int numTasks = 10;
        const int topUpAmount = 10;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task TopUpOnce()
        {
            await barrier.Task;
            budget.TopUp(topUpAmount);
        }

        var tasks = new List<Task>();
        for (var i = 0; i < numTasks; i++)
            tasks.Add(TopUpOnce());

        barrier.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(numTasks * topUpAmount, budget.Allowed);
        Assert.Equal(numTasks * topUpAmount, budget.Remaining);
    }

    [Fact]
    public void TopUp_NormalizesNegativeRemaining_Deterministic()
    {
        // Single-threaded and fully deterministic — no scheduler dependence.
        //
        // TryConsume uses Interlocked.Decrement unconditionally, so over-consuming drives the raw
        // _remaining field BELOW zero even though it returns false. We push it to -2, then TopUp(10).
        //
        //   With normalization  (normalized = current < 0 ? 0 : current):  _remaining = 0  + 10 = 10
        //   WITHOUT normalization (using current directly):                 _remaining = -2 + 10 =  8
        //
        // So the number of subsequent successful consumes distinguishes the two implementations:
        // exactly 10 with normalization, exactly 8 without. Asserting == 10 fails if the
        // normalize-negative-remaining line is removed.
        var budget = new RetryBudget(3);

        Assert.True(budget.TryConsume());  // _remaining 3 -> 2
        Assert.True(budget.TryConsume());  // _remaining 2 -> 1
        Assert.True(budget.TryConsume());  // _remaining 1 -> 0
        Assert.True(budget.IsExhausted);

        Assert.False(budget.TryConsume()); // _remaining 0 -> -1
        Assert.False(budget.TryConsume()); // _remaining -1 -> -2 (raw is now -2)

        budget.TopUp(10);

        // Remaining masks the raw value, so probe observable consume behaviour instead.
        var successes = 0;
        while (budget.TryConsume())
            successes++;

        // PROVING ASSERTION: normalization clamps -2 to 0 before adding 10, granting exactly 10
        // consumes. Without normalization only 8 would succeed.
        Assert.Equal(10, successes);

        Assert.Equal(13, budget.Allowed);   // 3 initial + 10 top-up
        Assert.Equal(0, budget.Remaining);
        Assert.Equal(13, budget.Used);
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void TopUp_ConcurrentTopUps_NoLostUpdates()
    {
        // Proves the CAS loop on _initial: a large number of parallel TopUp(1) calls must all
        // survive. We use dedicated OS threads (not the thread pool) and a very high per-thread
        // iteration count so the read-modify-write windows overlap continuously for the whole run.
        //
        // If the CAS on _initial were replaced with a plain `_initial = _initial + additional`,
        // two threads reading the same value and writing back would lose one update. Across
        // hundreds of thousands of contended increments on a multi-core box this is a near-certain,
        // repeatable failure — Allowed ends up strictly less than the expected total.
        var budget = new RetryBudget(0);
        const int threadCount = 8;
        const int roundsPerThread = 200_000;
        const int expectedAllowed = threadCount * roundsPerThread; // 1,600,000

        using var barrier = new Barrier(threadCount);

        void TopUpper()
        {
            barrier.SignalAndWait();
            for (var i = 0; i < roundsPerThread; i++)
                budget.TopUp(1);
        }

        var threads = new List<Thread>();
        for (var i = 0; i < threadCount; i++)
        {
            var t = new Thread(TopUpper) { IsBackground = true };
            threads.Add(t);
            t.Start();
        }
        foreach (var t in threads)
            t.Join();

        // PROVING ASSERTION: no TopUp increment may be lost. A non-atomic `+=` loses updates under
        // this sustained contention, yielding Allowed < expectedAllowed.
        Assert.Equal(expectedAllowed, budget.Allowed);

        // Invariants: nothing was consumed, so all budget remains available and the counter is intact.
        Assert.Equal(expectedAllowed, budget.Remaining);
        Assert.Equal(0, budget.Used);
        Assert.Equal(budget.Allowed, budget.Remaining + budget.Used);
    }
}
