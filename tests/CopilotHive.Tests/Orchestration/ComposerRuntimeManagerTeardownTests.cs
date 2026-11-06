using System.Reflection;
using System.Runtime.CompilerServices;
using CopilotHive.Actors;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Teardown and shutdown semantics of <see cref="ComposerRuntimeManager"/>: the Removing state blocks
/// same-user recreation until the ACTUAL actor/agent teardown finished (driven through the real deferred
/// <c>!_actor.IsCompleted</c> path of <see cref="Composer.DisposeAsync"/>), shutdown refuses creation and awaits
/// every entry, and removal is isolated from other users and never deletes persisted files.
/// <para>
/// The stuck actor is real: a chat client that ignores cancellation keeps the actor's streaming task alive, the
/// actor's 5 s dispose timeout is armed on a <see cref="ControlledTimeProvider"/> (installed on the Composer's
/// own actor), and the test rendezvouses on that timer before advancing the clock. Milestones are caused by
/// production (the manager reaching its join), every wait is bounded and fails loudly, and no test sleeps.
/// </para>
/// </summary>
public sealed partial class ComposerRuntimeManagerTests
{
    /// <summary>A chat client whose stream ignores cancellation until released, keeping the Composer actor busy.</summary>
    private sealed class StuckStream : IDisposable
    {
        private readonly TaskCompletionSource _block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IChatClient Client { get; }

        public StuckStream() => Client = new Impl(this);
        public void Release() => _block.TrySetResult();
        public void Dispose() => Release();

        private sealed class Impl(StuckStream owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                CancellationToken cancellationToken = default) => throw new NotImplementedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                owner.Started.TrySetResult();
                await owner._block.Task; // deliberately ignores cancellationToken
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done") { FinishReason = ChatFinishReason.Stop };
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
    }

    /// <summary>Replaces the Composer actor's dispose-timeout clock so the 5 s window is driven, not waited.</summary>
    private static void InstallActorClock(Composer composer, TimeProvider clock) =>
        typeof(Actor<IComposerMessage>).GetField("_timeProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Field<ComposerActor>(composer, "_actor"), clock);

    /// <summary>Records every teardown task the manager blocks on and signals when a given one is reached.</summary>
    private sealed class JoinRecorder
    {
        private readonly object _lock = new();
        private readonly List<(int UserId, Task Task)> _joins = [];
        private readonly List<(Func<Task, bool> Match, TaskCompletionSource Reached)> _waiters = [];

        public (int UserId, Task Task)[] Joins { get { lock (_lock) return [.. _joins]; } }

        public void Record(int userId, Task task)
        {
            lock (_lock)
            {
                _joins.Add((userId, task));
                foreach (var (match, reached) in _waiters.ToArray())
                    if (match(task)) reached.TrySetResult();
            }
        }

        public Task When(Func<Task, bool> match)
        {
            lock (_lock)
            {
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_joins.Any(j => match(j.Task))) reached.TrySetResult();
                else _waiters.Add((match, reached));
                return reached.Task;
            }
        }
    }

    private static async Task DriveActorDisposeTimeoutAsync(ControlledTimeProvider clock)
    {
        // Rendezvous on the REAL 5 s dispose timer armed by Actor.DisposeAsync, then advance exactly that window.
        await clock.WaitForPendingTimerAsync(Actor<IComposerMessage>.DisposeTimeout, Ct);
        clock.Advance(Actor<IComposerMessage>.DisposeTimeout);
    }

    // ── Removing state: recreation blocks until ACTUAL actor/agent teardown ──

    [Fact]
    public async Task Removal_WhenDisposeReturnsEarly_BlocksSameUserRecreationUntilTheActualActorTeardownCompletes()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        using var stuck = new StuckStream();
        var clock = new ControlledTimeProvider();
        var recorder = new JoinRecorder();
        manager.OnJoiningTeardown = recorder.Record;
        Composer? old = null;
        var observedAtRecreation = new List<(bool ActorCompleted, bool TeardownCompleted, int RegistryRows)>();
        _connectHook = async (c, ct) =>
        {
            if (old is null) { old = c; InstallActorClock(c, clock); }
            await c.ConnectAsync(ct);
        };
        _clientFactory = _ =>
        {
            if (Volatile.Read(ref _clientCreations) == 1) return stuck.Client;
            observedAtRecreation.Add((ActorCompleted(old!), old!.TeardownCompletion.IsCompleted,
                sp.GetRequiredService<LlmSessionRegistry>().GetAll().Count));
            return new Mock<IChatClient>().Object;
        };

        var before = (await Request(manager, second)).Runtime!;
        Assert.Same(old, before.Composer);
        Assert.False(old!.TeardownCompletion.IsCompleted); // not torn down until DisposeAsync ran
        // The AGENT disposal is the second half of "actual teardown": record what the completion task says each
        // time the agent is really disposed (CodingAgent.DisposeAsync is invoked right after this hook).
        var agentDisposals = new List<(bool TeardownCompleted, bool ActorCompleted)>();
        Field<ComposerAgentService>(old, "_agentService").OnAgentDisposing = _ =>
        {
            lock (agentDisposals) agentDisposals.Add((old.TeardownCompletion.IsCompleted, ActorCompleted(old)));
        };
        await old.SendMessageWithEventsAsync("keep the actor busy");
        await GuardAsync(stuck.Started.Task, "stuck stream started inside the actor");

        var removal = Remove(manager, second.UserId);
        await DriveActorDisposeTimeoutAsync(clock);
        await GuardAsync(recorder.When(t => ReferenceEquals(t, old.TeardownCompletion)),
            "manager blocked on the Composer's actual teardown completion");

        // DisposeAsync RETURNED (the manager is past it and joining the teardown), yet the actor is still running.
        Assert.False(ActorCompleted(old));
        Assert.False(old.TeardownCompletion.IsCompleted);
        Assert.False(removal.IsCompleted);
        lock (agentDisposals) Assert.Empty(agentDisposals); // agent disposal is deferred behind the still-running actor

        // Same-user recreation now: the entry is Removing and its operation has settled, so it WAITS for the teardown
        // (it is neither refused nor allowed to build a second composer:2 actor).
        var recreation = Request(manager, second);
        Assert.False(recreation.IsCompleted);
        Assert.Equal(1, _clientCreations);
        var entryJoin = recorder.Joins.Last();
        Assert.Equal(second.UserId, entryJoin.UserId);
        Assert.NotSame(old.TeardownCompletion, entryJoin.Task); // the entry's teardown, which itself waits on the actor's
        Assert.False(entryJoin.Task.IsCompleted);
        Assert.Equal(["composer:2"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId));

        stuck.Release(); // the actor really finishes; only then may anything below proceed
        Assert.True(await Bounded(removal, "removal after actual teardown"));
        var after = await Bounded(recreation, "recreation after actual teardown");

        Assert.True(old.TeardownCompletion.IsCompleted);
        Assert.True(ActorCompleted(old));
        // The deferred path disposed the agent exactly once, after the actor finished and BEFORE completion was
        // signalled: completion is not a surrogate that fires early.
        var disposal = Assert.Single(agentDisposals);
        Assert.True(disposal.ActorCompleted);
        Assert.False(disposal.TeardownCompleted);
        Assert.Equal(ComposerRuntimeStatus.Connected, after.Status);
        Assert.NotSame(old, after.Runtime!.Composer);
        // The new composer:2 was built only after the old actor and agent were fully gone, with no stale row.
        var created = Assert.Single(observedAtRecreation);
        Assert.True(created.ActorCompleted);
        Assert.True(created.TeardownCompleted);
        Assert.Equal(0, created.RegistryRows);
        Assert.Equal(2, _clientCreations);
        Assert.Equal(["composer:2"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId));
    }

    [Fact]
    public async Task Removal_WithCallerCancellationWhileBlocked_DoesNotAbandonTheTeardown()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        using var stuck = new StuckStream();
        var clock = new ControlledTimeProvider();
        var recorder = new JoinRecorder();
        manager.OnJoiningTeardown = recorder.Record;
        Composer? old = null;
        _connectHook = async (c, ct) => { old ??= c; InstallActorClock(c, clock); await c.ConnectAsync(ct); };
        _clientFactory = _ => Volatile.Read(ref _clientCreations) == 1 ? stuck.Client : new Mock<IChatClient>().Object;
        await (await Request(manager, second)).Runtime!.Composer.SendMessageWithEventsAsync("busy");
        await GuardAsync(stuck.Started.Task, "stuck stream started");
        var removal = Remove(manager, second.UserId);
        await DriveActorDisposeTimeoutAsync(clock);
        await GuardAsync(recorder.When(t => ReferenceEquals(t, old!.TeardownCompletion)), "manager blocked on teardown");

        // A recreation caller that gives up ends only ITS wait: teardown and removal carry on, the entry stays.
        using var cts = new CancellationTokenSource();
        var cancelled = Request(manager, second, cts.Token);
        Assert.False(cancelled.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(cancelled, "cancelled recreation wait"));
        Assert.False(removal.IsCompleted);
        Assert.False(old!.TeardownCompletion.IsCompleted);
        var stillBlocked = Request(manager, second);
        Assert.False(stillBlocked.IsCompleted);
        Assert.Equal(1, _clientCreations);

        stuck.Release();
        Assert.True(await Bounded(removal, "removal"));
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Bounded(stillBlocked, "recreation")).Status);
        Assert.Equal(2, _clientCreations);
    }

    // ── Shutdown ──

    [Fact]
    public async Task Shutdown_RefusesCreation_AndCompletesOnlyAfterEveryActorTeardownIsActuallyFinished()
    {
        var (first, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        using var stuck = new StuckStream();
        var clock = new ControlledTimeProvider();
        var recorder = new JoinRecorder();
        manager.OnJoiningTeardown = recorder.Record;
        Composer? stuckComposer = null;
        _connectHook = async (c, ct) =>
        {
            if (stuckComposer is null) { stuckComposer = c; InstallActorClock(c, clock); }
            await c.ConnectAsync(ct);
        };
        _clientFactory = _ => Volatile.Read(ref _clientCreations) == 1 ? stuck.Client : new Mock<IChatClient>().Object;
        var runtimeA = (await Request(manager, second)).Runtime!; // creation 1: the stuck one
        var runtimeB = (await Request(manager, first)).Runtime!;
        await runtimeA.Composer.SendMessageWithEventsAsync("busy");
        await GuardAsync(stuck.Started.Task, "stuck stream started");

        var shutdown = manager.ShutdownAsync();
        Assert.Same(shutdown, manager.ShutdownAsync()); // every caller joins the same shutdown
        await DriveActorDisposeTimeoutAsync(clock);
        await GuardAsync(recorder.When(t => ReferenceEquals(t, stuckComposer!.TeardownCompletion)),
            "manager blocked on the stuck Composer's actual teardown");

        Assert.False(shutdown.IsCompleted); // B is long gone, but A's actor is still running
        Assert.False(ActorCompleted(stuckComposer!));
        Assert.True(ActorCompleted(runtimeB.Composer));

        // Creation is refused for the user being torn down, for an idle user, and for every call kind.
        // Bounded: if shutdown ever admitted a creation, the call would block on the stuck actor — fail loudly instead.
        var refusedSame = await Bounded(Request(manager, second), "same-user creation refused during shutdown");
        var refusedOther = await Bounded(Request(manager, first), "other-user creation refused during shutdown");
        var refusedConnect = await Bounded(Retry(manager, second), "connect refused during shutdown");
        Assert.All(new[] { refusedSame, refusedOther, refusedConnect }, r =>
        {
            Assert.Equal(ComposerRuntimeStatus.Refused, r.Status);
            Assert.Equal(ComposerRuntimeRefusal.ShuttingDown, r.Refusal);
            Assert.Null(r.Runtime);
        });
        Assert.Equal(2, _clientCreations);

        stuck.Release();
        await GuardAsync(shutdown, "shutdown after actual teardown of all entries");

        Assert.True(ActorCompleted(stuckComposer!));
        Assert.True(stuckComposer!.TeardownCompletion.IsCompleted);
        Assert.True(runtimeB.Composer.TeardownCompletion.IsCompleted);
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll());
        Assert.Equal(ComposerRuntimeRefusal.ShuttingDown, (await Request(manager, second)).Refusal);
        Assert.False(await Remove(manager, second.UserId));
        Assert.Equal(2, _clientCreations); // nothing was resurrected
    }

    [Fact]
    public async Task Shutdown_WhileAnEntryIsAlreadyRemoving_AlsoWaitsForThatEntrysActualTeardown()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        using var stuck = new StuckStream();
        var clock = new ControlledTimeProvider();
        var recorder = new JoinRecorder();
        manager.OnJoiningTeardown = recorder.Record;
        Composer? old = null;
        _connectHook = async (c, ct) => { old ??= c; InstallActorClock(c, clock); await c.ConnectAsync(ct); };
        _clientFactory = _ => stuck.Client;
        await (await Request(manager, second)).Runtime!.Composer.SendMessageWithEventsAsync("busy");
        await GuardAsync(stuck.Started.Task, "stuck stream started");
        var removal = Remove(manager, second.UserId); // removal owns the entry first
        await DriveActorDisposeTimeoutAsync(clock);
        await GuardAsync(recorder.When(t => ReferenceEquals(t, old!.TeardownCompletion)), "removal blocked on actual teardown");

        var shutdown = manager.ShutdownAsync(); // must JOIN the in-progress teardown, not skip the Removing entry
        Assert.False(shutdown.IsCompleted);
        Assert.False(removal.IsCompleted);

        stuck.Release();
        await GuardAsync(shutdown, "shutdown joined the removing entry's actual teardown");
        Assert.True(old!.TeardownCompletion.IsCompleted);
        Assert.True(ActorCompleted(old));
        Assert.True(await Bounded(removal, "removal"));
    }

    [Fact]
    public async Task Shutdown_DuringInFlightCreation_CancelsItAndLeavesNoRuntimeBehind()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        await using var gate = Park();

        var creator = Request(manager, second);
        await GuardAsync(gate.Entered.Task, "manager entered connect");
        var shutdown = manager.ShutdownAsync();
        await GuardAsync(gate.CancelObserved.Task, "shutdown cancelled the shared creation");
        Assert.False(shutdown.IsCompleted); // the parked connect has not settled yet
        gate.Release();
        await GuardAsync(shutdown, "shutdown");

        var result = await Bounded(creator, "in-flight creator");
        Assert.Equal(ComposerRuntimeStatus.Removed, result.Status);
        Assert.Null(result.Runtime);
        Assert.True(ActorCompleted(gate.Composer0));
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll());
        Assert.Equal(ComposerRuntimeRefusal.ShuttingDown, (await Request(manager, second)).Refusal);
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task Shutdown_OfAnEmptyManager_CompletesImmediately_AndStillBlocksCreation()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());

        await GuardAsync(manager.ShutdownAsync(), "empty shutdown");

        Assert.Equal(ComposerRuntimeRefusal.ShuttingDown, (await Request(manager, second)).Refusal);
        Assert.Equal(0, _clientCreations);
        await manager.DisposeAsync(); // idempotent
    }

    // ── Isolation and persistence ──

    [Fact]
    public async Task Removal_DisposesOnlyThatUsersSubscriber_AndLeavesTheOtherUsersRegistryAndBufferIntact()
    {
        var (first, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var a = (await Request(manager, first)).Runtime!;
        var b = (await Request(manager, second)).Runtime!;
        Assert.Equal(2, _bus.SubscriberCount);
        var registry = sp.GetRequiredService<LlmSessionRegistry>();
        Assert.Equal(["composer:1", "composer:2"], registry.GetAll().Select(s => s.SessionId).Order());
        var bRow = registry.GetAll().Single(s => s.SessionId == "composer:2");
        _bus.Publish(new SystemEvent(EventType.GoalCompleted, "before removal"));
        Assert.Single(a.Subscriber!.PeekPendingEvents());
        Assert.Single(b.Subscriber!.PeekPendingEvents());

        Assert.True(await Remove(manager, first.UserId));

        Assert.Equal(1, _bus.SubscriberCount); // A's per-instance subscriber unsubscribed from the bus
        _bus.Publish(new SystemEvent(EventType.GoalFailed, "after removal"));
        Assert.Single(a.Subscriber.PeekPendingEvents()); // detached: the later event never reached it
        Assert.Equal(["before removal", "after removal"], b.Subscriber.PeekPendingEvents().Select(e => e.Message));
        Assert.Equal(["composer:2"], registry.GetAll().Select(s => s.SessionId));
        Assert.Same(bRow, registry.GetAll().Single());
        Assert.False(b.Composer.TeardownCompletion.IsCompleted);
        Assert.Same(b, (await Request(manager, second)).Runtime);
    }

    [Fact]
    public async Task Removal_AndShutdown_NeverDeletePersistedSessionOrAttachmentFiles()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        var runtime = (await Request(manager, second)).Runtime!;
        Field<ComposerAgentService>(runtime.Composer, "_agentService").Session.MessageHistory
            .Add(new ChatMessage(ChatRole.User, "persisted-history-marker"));
        await runtime.Composer.SaveSessionAsync(Ct);
        var saved = await runtime.Attachments.SaveAsync("keep.png", new MemoryStream([1, 2, 3, 4]), Ct);
        Assert.True(saved.Success);
        var sessionFile = ComposerUserState.SessionFilePath(StateDir, runtime.State);
        var attachmentFile = Path.Combine(runtime.Attachments.AttachmentsRootPath, saved.Attachment!.SavedRelativePath);
        var sessionBytes = await File.ReadAllBytesAsync(sessionFile, Ct);

        Assert.True(await Remove(manager, second.UserId));

        Assert.Equal(sessionBytes, await File.ReadAllBytesAsync(sessionFile, Ct));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(attachmentFile, Ct));
        // A recreated runtime for the same user loads that preserved history.
        var again = (await Request(manager, second)).Runtime!;
        Assert.Contains("persisted-history-marker", again.Composer.GetChatHistory().Select(m => m.Content));

        await manager.ShutdownAsync();

        Assert.Equal(sessionBytes, await File.ReadAllBytesAsync(sessionFile, Ct));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(attachmentFile, Ct));
    }

    // ── End-to-end over real in-memory UserService data ──

    [Fact]
    public async Task DifferentUserLifecycle_RemovingOneRuntimeLeavesTheOtherUnaffectedAndUsable()
    {
        var (first, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);

        var a = await Request(manager, first);
        var b = await Request(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Connected, a.Status);
        Assert.Equal(ComposerRuntimeStatus.Connected, b.Status);
        Assert.NotSame(a.Runtime, b.Runtime);
        Assert.NotSame(Field<object>(a.Runtime!.Composer, "_actor"), Field<object>(b.Runtime!.Composer, "_actor"));
        Assert.Equal(["composer:1", "composer:2"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId).Order());

        Assert.True(await Remove(manager, first.UserId));

        Assert.True(a.Runtime.Composer.TeardownCompletion.IsCompleted);
        Assert.True(ActorCompleted(a.Runtime.Composer));
        Assert.False(ActorCompleted(b.Runtime.Composer));
        var bAgain = await Request(manager, second);
        Assert.Same(b.Runtime, bAgain.Runtime);
        Assert.True(bAgain.Runtime!.Composer.IsConnected);
        await bAgain.Runtime.Composer.SaveSessionAsync(Ct); // B is still fully usable
        Assert.True(File.Exists(ComposerUserState.SessionFilePath(StateDir, b.Runtime.State)));
        Assert.Equal(["composer:2"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId));
        Assert.Equal(1, _bus.SubscriberCount);

        var aReborn = await Request(manager, first); // A can come back as a fresh runtime
        Assert.Equal(ComposerRuntimeStatus.Connected, aReborn.Status);
        Assert.NotSame(a.Runtime, aReborn.Runtime);
        Assert.Equal(3, _clientCreations);
        Assert.Equal(2, _bus.SubscriberCount);
    }
}
