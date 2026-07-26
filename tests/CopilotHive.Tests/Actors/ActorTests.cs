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
}
