using CopilotHive.Actors;
using Xunit;

namespace CopilotHive.Tests.Actors;

public class ActorTests
{
    internal sealed record TestMessage(TaskCompletionSource? Reply = null)
    {
        public bool Handled;
    }

    internal sealed class TestActor : Actor<TestMessage>
    {
        private readonly Func<TestMessage, CancellationToken, Task>? _handler;

        /// <summary>
        /// The clock is REQUIRED, deliberately not optional-with-a-<see cref="TimeProvider.System"/>
        /// fallback: every actor in this class must run on a test-owned manual clock, so the dispose
        /// window can only fire when a test advances it. A silent default would re-introduce exactly
        /// the wall-clock race this suite is removing.
        /// </summary>
        public TestActor(ControlledTimeProvider clock, Func<TestMessage, CancellationToken, Task>? handler = null)
            : base(clock)
            => _handler = handler;

        public int LoopStartedCount;
        public int HandleCallCount;
        public int ShutdownCount;
        public int UnstartedDisposeCount;
        public int DisposeTimeoutCount;
        public int UnhandledCount;
        public readonly List<TestMessage> Canceled = [];
        public readonly TaskCompletionSource EnteredHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteMailboxForTest() => CompleteMailbox();

        /// <summary>Test-only exposure of the protected loop token, so a test can register a
        /// cancellation callback that observes the exact disposal ordering.</summary>
        public CancellationToken LoopTokenForTest => LoopToken;

        protected override async Task HandleAsync(TestMessage message, CancellationToken ct)
        {
            Interlocked.Increment(ref HandleCallCount);
            EnteredHandler.TrySetResult();
            if (_handler is not null)
            {
                await _handler(message, ct);
            }

            message.Handled = true;
            message.Reply?.TrySetResult();
        }

        protected override void CancelReply(TestMessage message)
        {
            lock (Canceled) { Canceled.Add(message); }
            message.Reply?.TrySetCanceled();
        }

        protected override void OnUnhandledException(TestMessage message, Exception ex)
        {
            Interlocked.Increment(ref UnhandledCount);
            base.OnUnhandledException(message, ex);
        }

        protected override void OnLoopStarted() => Interlocked.Increment(ref LoopStartedCount);

        protected override Task OnShutdownAsync()
        {
            Interlocked.Increment(ref ShutdownCount);
            return Task.CompletedTask;
        }

        protected override void OnUnstartedDispose() => Interlocked.Increment(ref UnstartedDisposeCount);

        protected override void OnDisposeTimeout() => Interlocked.Increment(ref DisposeTimeoutCount);
    }

    /// <summary>
    /// Bounded hang-guard around an awaited signal. There is no real-time RACE here: every task
    /// passed in is completed by correct production code without any wall-clock time passing, so
    /// the only way this can expire is a genuine hang — never scheduler load. The manual clock the
    /// actor runs on means even a blocking handler cannot tie with this budget.
    /// </summary>
    private static async Task AwaitAsync(Task task)
        => await task.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

    /// <summary>
    /// Rendezvous on <see cref="Actor{TMessage}.DisposeTimeout"/> being armed on the manual clock.
    /// The failure is NAMED: if the dispose window never reaches the injected
    /// <see cref="TimeProvider"/> (e.g. production kept a real-time CancellationTokenSource), this
    /// test fails here with the missing-timer message — not with an anonymous hang-guard timeout
    /// that could be misread as a slow machine.
    /// </summary>
    private static async Task RendezvousOnDisposeTimeoutAsync(ControlledTimeProvider clock)
    {
        try
        {
            await clock.WaitForPendingTimerAsync(
                Actor<TestMessage>.DisposeTimeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException ex)
        {
            Assert.Fail(
                $"dispose timeout ({Actor<TestMessage>.DisposeTimeout.TotalSeconds:0} s) never registered with the controlled clock: {ex.Message}");
        }
    }

    [Fact]
    public async Task StartCalledTwice_OnlyOneLoopRuns()
    {
        var clock = new ControlledTimeProvider();
        var actor = new TestActor(clock);
        actor.Start();
        actor.Start();

        var message = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(message));
        await AwaitAsync(message.Reply!.Task);

        Assert.Equal(1, actor.LoopStartedCount);

        await AwaitAsync(actor.DisposeAsync().AsTask());
        Assert.Equal(0, actor.DisposeTimeoutCount);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task ConcurrentStartAndDispose_DoesNotDeadlock()
    {
        var clock = new ControlledTimeProvider();
        var actor = new TestActor(clock);

        // Worker lifetime is owned by this test, not by the outer wait. Every blocking point inside
        // a worker (the barrier rendezvous and the dispose join) is bounded by the hang-guard AND by
        // this cleanup token, so on ANY failure path the finally below can cancel it and join both
        // workers: no helper thread is ever left parked on a manual clock that will never advance.
        // The barrier and the token source are disposed only after both workers have terminated.
        var barrier = new Barrier(2);
        var workerCleanup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var workerToken = workerCleanup.Token;
        Task? startTask = null;
        Task? disposeTask = null;
        var workersTerminated = false;
        var outerGuardExpired = false;
        try
        {
            startTask = Task.Factory.StartNew(() =>
            {
                SignalAndWaitBounded(barrier, workerToken, "start");
                actor.Start();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            disposeTask = Task.Factory.StartNew(() =>
            {
                SignalAndWaitBounded(barrier, workerToken, "dispose");
                try
                {
                    // Bounded INSIDE the worker: a dispose that never completes faults this worker
                    // with a named TimeoutException instead of blocking its thread forever.
                    actor.DisposeAsync().AsTask()
                        .WaitAsync(ControlledTimeProvider.HangGuard, workerToken)
                        .GetAwaiter().GetResult();
                }
                catch (TimeoutException ex)
                {
                    throw new TimeoutException(
                        $"{nameof(ConcurrentStartAndDispose_DoesNotDeadlock)}: the dispose worker's DisposeAsync did not complete within the hang guard ({ControlledTimeProvider.HangGuard.TotalSeconds:0} s).",
                        ex);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            var workers = Task.WhenAll(startTask, disposeTask);
            try
            {
                await AwaitAsync(workers);
            }
            catch (TimeoutException) when (!workers.IsCompleted)
            {
                // The OUTER guard expired while a worker was still running (a worker's own named
                // TimeoutException completes `workers` and is rethrown unchanged instead). The
                // failure is reported only AFTER the finally has cancelled and joined the workers,
                // so the diagnostic states whether cleanup actually terminated them.
                outerGuardExpired = true;
            }
        }
        finally
        {
            // Release every bounded wait a worker could still be inside (barrier or dispose join),
            // then join both workers. Only once both have terminated is it safe to dispose the
            // barrier and the token source they use; otherwise they are deliberately left alive.
            workerCleanup.Cancel();
            workersTerminated = await JoinWorkersQuietlyAsync(startTask, disposeTask);
            if (workersTerminated)
            {
                barrier.Dispose();
                workerCleanup.Dispose();
            }
        }

        Assert.False(
            outerGuardExpired,
            $"{nameof(ConcurrentStartAndDispose_DoesNotDeadlock)}: the start/dispose workers did not finish within the hang guard ({ControlledTimeProvider.HangGuard.TotalSeconds:0} s); after cleanup cancellation workersTerminated={workersTerminated}.");
        Assert.True(
            workersTerminated,
            $"{nameof(ConcurrentStartAndDispose_DoesNotDeadlock)}: a start/dispose worker did not terminate after cleanup cancellation.");

        // No deadlock, no crash — the actor is disposed and Completion is signalled.
        await AwaitAsync(actor.Completion);
        Assert.True(actor.IsCompleted);

        // What this race proves: no deadlock and no crash, whichever side wins. Dispose may win the
        // Barrier race, in which case Start() returns without launching a loop and DisposeAsync takes
        // the UNSTARTED path (no dispose timer is ever created); or Start() wins and DisposeAsync takes
        // the STARTED path and joins the loop. In EITHER outcome the dispose-timeout hook must not have
        // fired and no timer may be left pending on the manual clock. This test does NOT prove which
        // path ran, so it is not evidence that a started loop was joined or that the dispose CTS
        // released its timer — that is proven by the deterministically started tests below
        // (e.g. DisposeWhileHandling_DrainsQueuedMessages, DisposeAfterStart_TellFromLoopTokenCallback_IsRejected).
        Assert.Equal(0, actor.DisposeTimeoutCount);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// Bounded barrier rendezvous for a helper worker: fails the worker with a named
    /// <see cref="TimeoutException"/> if its partner never arrives within the hang guard, and
    /// exits promptly (OperationCanceledException) when the owning test cancels cleanup.
    /// </summary>
    private static void SignalAndWaitBounded(Barrier barrier, CancellationToken cleanup, string worker)
    {
        if (!barrier.SignalAndWait(ControlledTimeProvider.HangGuard, cleanup))
        {
            throw new TimeoutException(
                $"{nameof(ConcurrentStartAndDispose_DoesNotDeadlock)}: the {worker} worker's partner never reached the barrier within the hang guard ({ControlledTimeProvider.HangGuard.TotalSeconds:0} s).");
        }
    }

    /// <summary>
    /// Joins helper workers for use exclusively in a <c>finally</c>: bounded, outcome discarded so
    /// the test's own assertion/failure remains the one reported. Returns whether every started
    /// worker actually terminated, which gates disposal of the resources they use.
    /// </summary>
    private static async Task<bool> JoinWorkersQuietlyAsync(params Task?[] workers)
    {
        var started = workers.OfType<Task>().ToArray();
        var all = Task.WhenAll(started);
        try
        {
            await all.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
        }
        catch (Exception)
        {
            // Joining only: worker faults/cancellations are already surfaced (or deliberately
            // secondary to the failure that sent us down this path).
        }

        return all.IsCompleted;
    }

    [Fact]
    public async Task DisposeWithoutStart_DrainsMessagesAndCallsHook()
    {
        var clock = new ControlledTimeProvider();
        var actor = new TestActor(clock);
        var message = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(message));

        await actor.DisposeAsync();

        Assert.True(message.Reply!.Task.IsCanceled);
        Assert.False(message.Handled);
        Assert.Equal(1, actor.UnstartedDisposeCount);
        Assert.Equal(0, actor.LoopStartedCount);
        Assert.True(actor.IsCompleted);
        Assert.Contains(message, actor.Canceled);
    }

    /// <summary>
    /// Proves the dispose window is armed on the injected <see cref="TimeProvider"/>: the handler
    /// never returns, so dispose can only complete after the test OBSERVES the 5 s timer registered
    /// on the manual clock and then advances it. No wall-clock time passes anywhere — the rendezvous
    /// is what keeps "the timeout has not fired yet" a fact rather than a race.
    /// </summary>
    [Fact]
    public async Task DisposeWithBlockedHandler_CallsDisposeTimeoutHook()
    {
        var clock = new ControlledTimeProvider();
        var actor = new TestActor(clock, (_, _) => new TaskCompletionSource().Task);
        actor.Start();
        Assert.True(actor.Tell(new TestMessage()));
        await AwaitAsync(actor.EnteredHandler.Task);

        // Started but NOT awaited: the dispose task must be pending on the timer rendezvous below.
        var disposeTask = actor.DisposeAsync().AsTask();

        await RendezvousOnDisposeTimeoutAsync(clock);

        clock.Advance(Actor<TestMessage>.DisposeTimeout);
        await AwaitAsync(disposeTask);

        Assert.Equal(1, actor.DisposeTimeoutCount);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task MailboxCompleted_CallsShutdownHook()
    {
        var clock = new ControlledTimeProvider();
        var actor = new TestActor(clock);
        actor.Start();
        var message = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(message));
        await AwaitAsync(message.Reply!.Task);

        actor.CompleteMailboxForTest();

        await AwaitAsync(actor.Completion);
        Assert.Equal(1, actor.ShutdownCount);
        await AwaitAsync(actor.DisposeAsync().AsTask());
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task HandlerThrows_RoutesToUnhandledExceptionHook()
    {
        var clock = new ControlledTimeProvider();
        var actor = new TestActor(clock, (_, _) => throw new InvalidOperationException("boom"));
        actor.Start();
        var failing = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(failing));

        await AwaitAsync(failing.Reply!.Task.ContinueWith(_ => { }, TaskScheduler.Default));

        Assert.True(failing.Reply.Task.IsCanceled);
        Assert.Equal(1, actor.UnhandledCount);

        // The loop keeps running after a handler failure.
        var next = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(next));
        await AwaitAsync(actor.DisposeAsync().AsTask());
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task DisposeWhileHandling_DrainsQueuedMessages()
    {
        var clock = new ControlledTimeProvider();
        var gate = new TaskCompletionSource();
        var actor = new TestActor(clock, async (_, ct) => await gate.Task.WaitAsync(ct));
        actor.Start();

        var first = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(first));
        await AwaitAsync(actor.EnteredHandler.Task);

        var queued = new List<TestMessage>();
        for (var i = 0; i < 3; i++)
        {
            var m = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            queued.Add(m);
            Assert.True(actor.Tell(m));
        }

        await AwaitAsync(actor.DisposeAsync().AsTask());

        Assert.True(first.Reply!.Task.IsCanceled);
        foreach (var m in queued)
        {
            Assert.True(m.Reply!.Task.IsCanceled);
            Assert.False(m.Handled);
        }

        Assert.Equal(0, actor.DisposeTimeoutCount);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// Exercises the pre-dispatch <c>ct.IsCancellationRequested</c> check in the message loop.
    /// The first handler blocks on a NON-cancelable gate, so cancellation does not throw inside
    /// it: the handler returns normally and the loop advances to the next buffered message while
    /// the token is already canceled. That message must be canceled before dispatch, never handled.
    /// </summary>
    [Fact]
    public async Task BufferedMessageAtCancellation_IsCanceledBeforeDispatch()
    {
        // Non-cancelable gate — the handler awaits it directly, ignoring the loop token.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ControlledTimeProvider();
        var actor = new TestActor(clock, async (_, _) => await gate.Task);
        actor.Start();

        var blocking = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(blocking));
        await AwaitAsync(actor.EnteredHandler.Task);

        // Buffered while the loop is still inside the first handler.
        var buffered = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(buffered));

        // Disposal cancels the token and completes the writer, then awaits the loop.
        var disposeTask = actor.DisposeAsync().AsTask();

        // Release the first handler so the loop resumes with an already-canceled token.
        gate.TrySetResult();

        await AwaitAsync(disposeTask);
        await disposeTask;

        Assert.True(blocking.Handled);
        Assert.Equal(1, actor.HandleCallCount);
        Assert.False(buffered.Handled);
        Assert.True(buffered.Reply!.Task.IsCanceled);
        Assert.Contains(buffered, actor.Canceled);
        Assert.Equal(0, actor.DisposeTimeoutCount);
        Assert.Equal(0, actor.UnhandledCount);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// Disposal ORDERING regression guard (unstarted actor): <c>DisposeAsync</c> must close
    /// mailbox admission BEFORE cancelling the loop token, so any <c>Tell</c> issued from a
    /// loop-token cancellation callback is deterministically REJECTED. If cancellation ran
    /// first, such a message would be accepted into a mailbox nobody will handle, and the
    /// sender would wrongly believe it was delivered.
    /// <para>Fully synchronous: the callback runs on this thread inside <c>_cts.Cancel()</c>,
    /// so there are no timing assumptions.</para>
    /// </summary>
    [Fact]
    public async Task DisposeWithoutStart_TellFromLoopTokenCallback_IsRejected()
    {
        var actor = new TestActor(new ControlledTimeProvider());
        var rejected = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var callbackRan = false;
        bool? tellResult = null;

        // Not disposed: the registration outlives the actor's CTS, and letting it go is
        // harmless — the callback has already run by the time disposal returns.
        _ = actor.LoopTokenForTest.Register(() =>
        {
            callbackRan = true;
            tellResult = actor.Tell(rejected);
        });

        await actor.DisposeAsync();

        Assert.True(callbackRan, "The loop-token cancellation callback never ran.");
        Assert.False(tellResult, "Tell from a cancellation callback must be rejected: the mailbox must be closed before cancellation.");

        // Never admitted, therefore never drained/cancelled by the unstarted dispose path.
        Assert.DoesNotContain(rejected, actor.Canceled);
        Assert.False(rejected.Handled);
        Assert.Equal(1, actor.UnstartedDisposeCount);
        Assert.True(actor.IsCompleted);
    }

    /// <summary>
    /// Disposal ORDERING regression guard (started actor): same invariant as the unstarted
    /// case, but with the message loop provably running and parked inside a handler, so the
    /// read/drain path is established before disposal begins. The cancellation callback runs
    /// synchronously inside <c>DisposeAsync</c>'s <c>_cts.Cancel()</c> on this thread, so the
    /// assertion has no timing assumptions.
    /// </summary>
    [Fact]
    public async Task DisposeAfterStart_TellFromLoopTokenCallback_IsRejected()
    {
        // Cancelable gate: the handler parks here so the loop is inside the read/handle path,
        // and the loop token release lets disposal complete without the timeout hook.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ControlledTimeProvider();
        var actor = new TestActor(clock, async (_, ct) => await gate.Task.WaitAsync(ct));

        // Hoisted so the finally can await the ORIGINAL disposal task even when an assertion
        // above throws: a second DisposeAsync sees _disposed and returns early, so only the
        // first call's task guarantees cancellation/cleanup actually completed before the
        // test exits.
        Task? disposeTask = null;

        // Set only after every try-block assertion has passed. If the try block failed, a
        // fault on the disposal join below is secondary and must not mask the assertion
        // that failed first.
        var tryBlockPassed = false;
        try
        {
            actor.Start();
            var blocking = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            Assert.True(actor.Tell(blocking));
            await AwaitAsync(actor.EnteredHandler.Task);

            var callbackRan = false;
            bool? tellResult = null;
            _ = actor.LoopTokenForTest.Register(() =>
            {
                callbackRan = true;
                tellResult = actor.Tell(new TestMessage());
            });

            // Disposal is started here and the ORIGINAL task is retained (hoisted above);
            // a second DisposeAsync would return early because _disposed is already set.
            disposeTask = actor.DisposeAsync().AsTask();

            Assert.True(callbackRan, "The loop-token cancellation callback never ran.");
            Assert.False(tellResult, "Tell from a cancellation callback must be rejected: the mailbox must be closed before cancellation.");

            await AwaitAsync(disposeTask);
            await disposeTask;

            Assert.Equal(0, actor.DisposeTimeoutCount);
            Assert.True(actor.IsCompleted);
            Assert.Equal(0, clock.PendingTimerCount);

            tryBlockPassed = true;
        }
        finally
        {
            gate.TrySetResult();
            // Await the original disposal task if it was started, so cleanup/cancellation
            // completes even when an intermediate assertion failed (e.g. under the required
            // old-order mutation check). The second DisposeAsync call returns early without
            // joining the loop, so the first task is the only authoritative join.
            if (disposeTask is not null)
            {
                try { await AwaitAsync(disposeTask); }
                catch (Exception) when (!tryBlockPassed)
                {
                    // The try block already failed; THAT exception is the one under test and
                    // must win. A disposal fault here is secondary cleanup noise — swallowed
                    // only on this failure path so it cannot replace the assertion failure.
                    // On a passing path a fault propagates normally.
                }
            }
        }
    }
}
