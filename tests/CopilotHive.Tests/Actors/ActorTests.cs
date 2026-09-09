using CopilotHive.Actors;
using Xunit;

namespace CopilotHive.Tests.Actors;

public class ActorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal sealed record TestMessage(TaskCompletionSource? Reply = null)
    {
        public bool Handled;
    }

    internal sealed class TestActor : Actor<TestMessage>
    {
        private readonly Func<TestMessage, CancellationToken, Task>? _handler;

        public TestActor(Func<TestMessage, CancellationToken, Task>? handler = null) => _handler = handler;

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

    private static async Task AwaitAsync(Task task)
    {
        await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(task.IsCompleted, "Task did not complete in time.");
    }

    [Fact]
    public async Task StartCalledTwice_OnlyOneLoopRuns()
    {
        await using var actor = new TestActor();
        actor.Start();
        actor.Start();

        var message = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(message));
        await AwaitAsync(message.Reply!.Task);

        Assert.Equal(1, actor.LoopStartedCount);
    }

    [Fact]
    public async Task ConcurrentStartAndDispose_DoesNotDeadlock()
    {
        var actor = new TestActor();
        using var barrier = new Barrier(2);

        var startTask = Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            actor.Start();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var disposeTask = Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            actor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await AwaitAsync(Task.WhenAll(startTask, disposeTask));

        // No deadlock, no crash — the actor is disposed and its loop completed.
        await AwaitAsync(actor.Completion);
        Assert.True(actor.IsCompleted);
    }

    [Fact]
    public async Task DisposeWithoutStart_DrainsMessagesAndCallsHook()
    {
        var actor = new TestActor();
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

    [Fact]
    public async Task DisposeWithBlockedHandler_CallsDisposeTimeoutHook()
    {
        var actor = new TestActor((_, _) => new TaskCompletionSource().Task);
        actor.Start();
        Assert.True(actor.Tell(new TestMessage()));
        await AwaitAsync(actor.EnteredHandler.Task);

        await actor.DisposeAsync();

        Assert.Equal(1, actor.DisposeTimeoutCount);
    }

    [Fact]
    public async Task MailboxCompleted_CallsShutdownHook()
    {
        var actor = new TestActor();
        actor.Start();
        var message = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(message));
        await AwaitAsync(message.Reply!.Task);

        actor.CompleteMailboxForTest();

        await AwaitAsync(actor.Completion);
        Assert.Equal(1, actor.ShutdownCount);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task HandlerThrows_RoutesToUnhandledExceptionHook()
    {
        var actor = new TestActor((_, _) => throw new InvalidOperationException("boom"));
        actor.Start();
        var failing = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(failing));

        await AwaitAsync(failing.Reply!.Task.ContinueWith(_ => { }, TaskScheduler.Default));

        Assert.True(failing.Reply.Task.IsCanceled);
        Assert.Equal(1, actor.UnhandledCount);

        // The loop keeps running after a handler failure.
        var next = new TestMessage(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(actor.Tell(next));
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task DisposeWhileHandling_DrainsQueuedMessages()
    {
        var gate = new TaskCompletionSource();
        var actor = new TestActor(async (_, ct) => await gate.Task.WaitAsync(ct));
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

        await actor.DisposeAsync();

        Assert.True(first.Reply!.Task.IsCanceled);
        foreach (var m in queued)
        {
            Assert.True(m.Reply!.Task.IsCanceled);
            Assert.False(m.Handled);
        }
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
        var actor = new TestActor(async (_, _) => await gate.Task);
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
        var actor = new TestActor();
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
        var actor = new TestActor(async (_, ct) => await gate.Task.WaitAsync(ct));

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
                try { await disposeTask; }
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
