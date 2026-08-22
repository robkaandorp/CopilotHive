using CopilotHive.Actors;
using CopilotHive.Goals;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests.Actors;

public class GoalActorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static async Task<GoalActorState> AwaitReplyAsync(TaskCompletionSource<GoalActorState> reply)
    {
        await Task.WhenAny(reply.Task, Task.Delay(Timeout));
        Assert.True(reply.Task.IsCompletedSuccessfully, "Reply did not complete successfully in time.");
        return reply.Task.Result;
    }

    /// <summary>Runs on a dedicated thread so barrier-synchronized producers cannot starve the thread pool.</summary>
    private static Task StartProducer(Action action) =>
        Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static async Task AwaitCompletionAsync(GoalActor actor)
    {
        await Task.WhenAny(actor.Completion, Task.Delay(Timeout));
        Assert.True(actor.IsCompleted, "Actor did not stop in time.");
    }

    [Fact]
    public async Task ConcurrentMessagesBeforeStart_DeterministicResult()
    {
        var actor = new GoalActor("goal-1");
        using var barrier = new Barrier(100);
        var producers = new List<Task>(100);
        for (var i = 0; i < 50; i++)
        {
            producers.Add(StartProducer(() =>
            {
                barrier.SignalAndWait();
                Assert.True(actor.Tell(new SetPhaseMessage(GoalPhase.Coding)));
            }));
            producers.Add(StartProducer(() =>
            {
                barrier.SignalAndWait();
                Assert.True(actor.Tell(new SetIterationMessage(1)));
            }));
        }

        await Task.WhenAll(producers);

        actor.Tell(new SetPhaseMessage(GoalPhase.Testing));
        actor.Tell(new SetIterationMessage(2));
        var query = GoalActorMessages.CreateGetStateMessage();
        actor.Tell(query);

        actor.Start();

        var state = await AwaitReplyAsync(query.Reply);
        Assert.Equal(GoalPhase.Testing, state.Phase);
        Assert.Equal(2, state.Iteration);

        await actor.DisposeAsync();
    }

    [Fact]
    public async Task SequentialStateChanges_CorrectSnapshot()
    {
        var actor = new GoalActor("goal-2");
        actor.Start();

        actor.Tell(new SetStatusMessage(GoalStatus.InProgress));
        actor.Tell(new SetPhaseMessage(GoalPhase.Coding));
        actor.Tell(new SetIterationMessage(1));
        actor.Tell(new SetActiveTaskMessage("task-1"));
        var query = GoalActorMessages.CreateGetStateMessage();
        actor.Tell(query);

        var state = await AwaitReplyAsync(query.Reply);
        Assert.Equal("goal-2", state.GoalId);
        Assert.Equal(GoalStatus.InProgress, state.Status);
        Assert.NotNull(state.Phase);
        Assert.Equal(GoalPhase.Coding, state.Phase);
        Assert.Equal(1, state.Iteration);
        Assert.Equal("task-1", state.ActiveTaskId);

        await actor.DisposeAsync();
    }

    [Fact]
    public async Task TerminalCompleted_StopsActor()
    {
        var actor = new GoalActor("goal-3");
        actor.Tell(new SetStatusMessage(GoalStatus.Completed));
        var query = GoalActorMessages.CreateGetStateMessage();
        actor.Tell(query);

        actor.Start();

        var state = await AwaitReplyAsync(query.Reply);
        Assert.Equal(GoalStatus.Completed, state.Status);
        await AwaitCompletionAsync(actor);
    }

    [Fact]
    public async Task CancelMessage_ClearsActiveTask_StopsActor()
    {
        var actor = new GoalActor("goal-4");
        actor.Tell(new SetActiveTaskMessage("task-1"));
        var cancel = GoalActorMessages.CreateCancelMessage();
        actor.Tell(cancel);

        actor.Start();

        var state = await AwaitReplyAsync(cancel.Reply);
        Assert.Equal(GoalStatus.Cancelled, state.Status);
        Assert.Null(state.ActiveTaskId);
        await AwaitCompletionAsync(actor);
    }

    [Fact]
    public async Task TerminalCompleted_CancelMessageDoesNotOverride()
    {
        var actor = new GoalActor("goal-5");
        actor.Tell(new SetStatusMessage(GoalStatus.Completed));
        var cancel = GoalActorMessages.CreateCancelMessage();
        actor.Tell(cancel);

        actor.Start();

        var state = await AwaitReplyAsync(cancel.Reply);
        Assert.Equal(GoalStatus.Completed, state.Status);
        await AwaitCompletionAsync(actor);
    }

    [Fact]
    public async Task TellReturnsFalse_AfterCompleted()
    {
        var actor = new GoalActor("goal-6");
        actor.Start();
        actor.Tell(new SetStatusMessage(GoalStatus.Completed));

        await AwaitCompletionAsync(actor);

        Assert.False(actor.Tell(new SetPhaseMessage(GoalPhase.Coding)));
    }

    [Fact]
    public async Task StartCalledTwiceConcurrently_OneLoopRuns()
    {
        var actor = new GoalActor("goal-7");
        await Task.WhenAll(
            Task.Run(actor.Start, TestContext.Current.CancellationToken),
            Task.Run(actor.Start, TestContext.Current.CancellationToken));

        actor.Tell(new SetStatusMessage(GoalStatus.Completed));

        await AwaitCompletionAsync(actor);
        Assert.Equal(1, actor.LoopCount);
    }

    [Fact]
    public async Task DisposeAsync_RunningActor_CancelsQueuedReplies()
    {
        var actor = new GoalActor("goal-11");

        // Park the loop at a gate BEFORE it dequeues anything, so the queued reply
        // message is provably still in the mailbox when disposal begins.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.OnBeforeReadAsync = async () =>
        {
            entered.TrySetResult();
            await gate.Task;
        };

        actor.Start();
        await entered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var query = GoalActorMessages.CreateGetStateMessage();
        Assert.True(actor.Tell(query));

        // Do NOT await before releasing the gate: DisposeAsync waits for loop
        // completion while the loop waits on the gate — that would deadlock.
        var disposeTask = actor.DisposeAsync().AsTask();
        gate.TrySetResult(true);

        await disposeTask;

        Assert.True(query.Reply.Task.IsCanceled);
    }

    [Fact]
    public async Task QueuedMessageAfterTerminal_Ignored()
    {
        var actor = new GoalActor("goal-8");
        actor.Tell(new SetStatusMessage(GoalStatus.Completed));
        actor.Tell(new SetPhaseMessage(GoalPhase.Testing));
        var query = GoalActorMessages.CreateGetStateMessage();
        actor.Tell(query);

        actor.Start();

        var state = await AwaitReplyAsync(query.Reply);
        Assert.Null(state.Phase);
        Assert.Equal(GoalStatus.Completed, state.Status);
        await AwaitCompletionAsync(actor);
    }

    [Fact]
    public async Task DisposeAsyncBeforeStart_CompletesWithoutTimeout()
    {
        var actor = new GoalActor("goal-9");
        var query = GoalActorMessages.CreateGetStateMessage();
        actor.Tell(query);

        await actor.DisposeAsync();

        Assert.True(query.Reply.Task.IsCanceled);
        Assert.True(actor.IsCompleted);
    }

    [Fact]
    public async Task DisposeAsyncAfterTerminal_CompletesWithoutTimeout()
    {
        var actor = new GoalActor("goal-10");
        actor.Start();
        actor.Tell(new SetStatusMessage(GoalStatus.Completed));

        await AwaitCompletionAsync(actor);
        await actor.DisposeAsync();

        Assert.True(actor.IsCompleted);
    }
}
