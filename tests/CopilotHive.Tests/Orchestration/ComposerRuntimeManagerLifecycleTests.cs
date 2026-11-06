using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CopilotHive.Tests.Orchestration;

public sealed partial class ComposerRuntimeManagerTests
{
    // The existing agent-disposal hook is synchronous. Keep this hold disarmed unless a test
    // explicitly asks to stop the REAL deferred continuation before CodingAgent.DisposeAsync.
    private sealed class AgentDisposalHold : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _armed;
        private int _timedOut;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public void Release() => _release.Set();
        public void Observe()
        {
            if (Volatile.Read(ref _armed) == 0) return;
            Entered.TrySetResult();
            try
            {
                if (!_release.Wait(TimeSpan.FromSeconds(30))) Interlocked.Exchange(ref _timedOut, 1);
            }
            finally { Exited.TrySetResult(); }
        }
        public void AssertHealthy() => Assert.True(Volatile.Read(ref _timedOut) == 0,
            "Timed out: release deferred agent-disposal hold (production must not swallow a test timeout)");
        public void Dispose() => _release.Dispose();
    }

    private sealed record DeferredRemovalContext(
        ComposerRuntimeManager Manager, ServiceProvider Provider,
        ActingUserPrincipal First, ActingUserPrincipal Second, ComposerUserRuntime Runtime,
        StuckStream Stream, AgentDisposalHold AgentHold, ControlledTimeProvider Clock,
        JoinRecorder Recorder, Task<bool> Removal);

    private async Task WithDeferredRemovalAsync(Func<DeferredRemovalContext, Task> body)
    {
        var (first, second) = await TwoUsersAsync();
        var provider = BuildProvider();
        var manager = NewManager(provider);
        using var stream = new StuckStream();
        using var agentHold = new AgentDisposalHold();
        var clock = new ControlledTimeProvider();
        var recorder = new JoinRecorder();
        manager.OnJoiningTeardown = recorder.Record;
        _clientFactory = _ => Volatile.Read(ref _clientCreations) == 1 ? stream.Client : new Mock<IChatClient>().Object;
        _connectHook = async (composer, token) =>
        {
            InstallActorClock(composer, clock);
            await composer.ConnectAsync(token);
        };
        ComposerUserRuntime? runtime = null;
        Task<bool>? removal = null;
        ExceptionDispatchInfo? primary = null;
        Exception? cleanupFailure = null;
        try
        {
            runtime = (await Request(manager, second)).Runtime!;
            Field<ComposerAgentService>(runtime.Composer, "_agentService").OnAgentDisposing = _ => agentHold.Observe();
            await runtime.Composer.SendMessageWithEventsAsync("force actual deferred actor teardown");
            await GuardAsync(stream.Started.Task, "real stream entered cancellation-ignoring provider");
            Assert.True(runtime.Composer.IsStreaming);
            removal = Remove(manager, second.UserId);
            await DriveActorDisposeTimeoutAsync(clock);
            await GuardAsync(recorder.When(task => ReferenceEquals(task, runtime.Composer.TeardownCompletion)),
                "DisposeAsync returned early and manager joined exact Composer.TeardownCompletion");
            // Load-bearing pre-check: the clock fired, DisposeAsync returned, and the ACTUAL
            // teardown is still pending. No body can pass with a fortuitously fast teardown.
            Assert.False(ActorCompleted(runtime.Composer));
            Assert.False(runtime.Composer.TeardownCompletion.IsCompleted);
            Assert.False(removal.IsCompleted);
            await body(new(manager, provider, first, second, runtime, stream, agentHold, clock, recorder, removal));
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            // Close admission first, so an abandoned recreation cannot start a new runtime during
            // cleanup. Release BOTH production holds before observing ANY task, on every exit path.
            var shutdown = manager.ShutdownAsync();
            agentHold.Release();
            stream.Release();
            try
            {
                await CompleteCleanupAsync(CleanupSteps());
            }
            catch (Exception ex) { cleanupFailure = ex; }

            IEnumerable<Func<Task>> CleanupSteps()
            {
                if (removal is not null) yield return () => GuardAsync(removal, "deferred helper removal completion");
                yield return () => GuardAsync(shutdown, "deferred helper joins manager shutdown");
                foreach (var request in _requests.ToArray()) yield return () => JoinRequestAsync(request);
                if (runtime is not null)
                {
                    yield return () => GuardAsync(runtime.Composer.TeardownCompletion, "deferred helper actual teardown completion");
                    yield return () => { Assert.True(ActorCompleted(runtime.Composer)); return Task.CompletedTask; };
                }
                if (agentHold.Entered.Task.IsCompleted)
                    yield return () => GuardAsync(agentHold.Exited.Task, "deferred agent-disposal hook exited");
                yield return () => { agentHold.AssertHealthy(); return Task.CompletedTask; };
                yield return () => { Assert.Equal(0, clock.PendingTimerCount); return Task.CompletedTask; };
            }
        }
        if (primary is not null)
        {
            if (cleanupFailure is not null) primary.SourceException.Data["CleanupFailure"] = cleanupFailure;
            primary.Throw();
        }
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    [Fact]
    public async Task Removal_WhenActorFinishedButAgentDisposalBlocked_KeepsRemovingAndRegistryUntilActualCompletion()
    {
        await WithDeferredRemovalAsync(async context =>
        {
            var old = context.Runtime.Composer;
            var registry = context.Provider.GetRequiredService<LlmSessionRegistry>();
            context.AgentHold.Arm();
            context.Stream.Release();
            await GuardAsync(context.AgentHold.Entered.Task, "deferred continuation reached real agent disposal");
            // A second load-bearing window: actor finished, but the agent has NOT been disposed.
            Assert.True(ActorCompleted(old));
            Assert.False(old.TeardownCompletion.IsCompleted);
            Assert.False(context.Removal.IsCompleted);
            var oldRow = Assert.Single(registry.GetAll());
            Assert.Equal("composer:2", oldRow.SessionId);
            var creationSnapshots = new List<(bool ActorDone, bool ActualTeardownDone, int RegistryRows)>();
            _clientFactory = _ =>
            {
                creationSnapshots.Add((ActorCompleted(old), old.TeardownCompletion.IsCompleted, registry.GetAll().Count));
                return new Mock<IChatClient>().Object;
            };
            var enteredEntryJoin = context.Recorder.When(task => !ReferenceEquals(task, old.TeardownCompletion));
            var recreate = Request(context.Manager, context.Second);
            // Correct production publishes its entry-join milestone; a bypass instead returns
            // a runtime/refusal while the old agent is held. Neither case relies on scheduling.
            await GuardAsync(Task.WhenAny(enteredEntryJoin, recreate), "recreation joined teardown or returned prematurely");
            Assert.False(recreate.IsCompleted, "Same-user recreation completed while actual deferred actor/agent teardown is still held");
            await GuardAsync(enteredEntryJoin, "recreation entered the real Removing-entry teardown wait");
            var otherWaiter = Request(context.Manager, context.Second);
            Assert.False(otherWaiter.IsCompleted);
            Assert.Equal(1, _clientCreations);
            Assert.Same(oldRow, Assert.Single(registry.GetAll())); // no replacement registration while old agent lives
            var entryJoin = context.Recorder.Joins.Last();
            Assert.Equal(context.Second.UserId, entryJoin.UserId);
            Assert.NotSame(old.TeardownCompletion, entryJoin.Task);
            Assert.False(entryJoin.Task.IsCompleted);

            context.AgentHold.Release();
            await GuardAsync(old.TeardownCompletion, "old Composer actor AND agent actually completed");
            Assert.True(await Bounded(context.Removal, "removal after agent disposal"));
            var results = await Bounded(Task.WhenAll(recreate, otherWaiter), "recreation waiters after actual completion");
            Assert.All(results, result => Assert.Equal(ComposerRuntimeStatus.Connected, result.Status));
            Assert.Same(results[0].Runtime, results[1].Runtime);
            Assert.NotSame(context.Runtime, results[0].Runtime);
            Assert.NotSame(oldRow, Assert.Single(registry.GetAll()));
            Assert.Equal("composer:2", Assert.Single(registry.GetAll()).SessionId);
            var snapshot = Assert.Single(creationSnapshots);
            Assert.True(snapshot.ActorDone);
            Assert.True(snapshot.ActualTeardownDone);
            Assert.Equal(0, snapshot.RegistryRows); // old row removed BEFORE the new registration can be emitted
            Assert.Equal(2, _clientCreations);
        });
    }

    [Fact]
    public async Task Shutdown_WhileRecreationAlreadyWaitsOnRemoving_RefusesItAfterActualTeardown()
    {
        await WithDeferredRemovalAsync(async context =>
        {
            // Explicit negative control on the same manager before shutdown: other-user creation works.
            var other = await Request(context.Manager, context.First);
            Assert.Equal(ComposerRuntimeStatus.Connected, other.Status);
            Assert.True(other.Runtime!.Composer.IsConnected);
            var waiting = Request(context.Manager, context.Second);
            Assert.False(waiting.IsCompleted);
            Assert.False(context.Recorder.Joins.Last().Task.IsCompleted);
            var shutdown = context.Manager.ShutdownAsync();
            Assert.False(shutdown.IsCompleted);
            foreach (var call in new[] { Request(context.Manager, context.Second),
                         Request(context.Manager, context.First), Retry(context.Manager, context.Second) })
            {
                var refused = await Bounded(call, "shutdown admission refuses instead of deadlocking");
                Assert.Equal(ComposerRuntimeStatus.Refused, refused.Status);
                Assert.Equal(ComposerRuntimeRefusal.ShuttingDown, refused.Refusal);
                Assert.False(refused.IsGranted);
                Assert.Null(refused.Runtime);
                Assert.Null(refused.Failure);
            }
            context.AgentHold.Arm();
            context.Stream.Release();
            await GuardAsync(context.AgentHold.Entered.Task, "shutdown still waiting while deferred agent disposal is held");
            Assert.True(ActorCompleted(context.Runtime.Composer));
            Assert.False(context.Runtime.Composer.TeardownCompletion.IsCompleted);
            Assert.False(shutdown.IsCompleted);
            Assert.False(waiting.IsCompleted);
            context.AgentHold.Release();
            await GuardAsync(shutdown, "shutdown joined actual agent completion");
            var pendingResult = await Bounded(waiting, "pre-shutdown recreation rechecks shutdown after wait");
            Assert.Equal(ComposerRuntimeStatus.Refused, pendingResult.Status);
            Assert.Equal(ComposerRuntimeRefusal.ShuttingDown, pendingResult.Refusal);
            Assert.Null(pendingResult.Runtime);
            Assert.True(context.Runtime.Composer.TeardownCompletion.IsCompleted);
            Assert.True(other.Runtime.Composer.TeardownCompletion.IsCompleted);
            Assert.Empty(context.Provider.GetRequiredService<LlmSessionRegistry>().GetAll());
            Assert.Equal(2, _clientCreations);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecreationBlockedByRemoval_RechecksFreshAccountBeforeRecreating(bool invalidateRole)
    {
        await WithDeferredRemovalAsync(async context =>
        {
            context.AgentHold.Arm();
            context.Stream.Release();
            await GuardAsync(context.AgentHold.Entered.Task, "account recheck vector held at real deferred agent disposal");
            Assert.True(ActorCompleted(context.Runtime.Composer));
            Assert.False(context.Runtime.Composer.TeardownCompletion.IsCompleted);
            var waiting = Request(context.Manager, context.Second);
            Assert.False(waiting.IsCompleted);
            if (invalidateRole) await SetStoredRoleAsync(context.Second, "retired-role");
            else await _users.SetUserEnabledAsync(context.Second.UserId, false, Ct);
            Assert.True(context.Second.Enabled);
            Assert.Equal(UserRole.Member, context.Second.Role);
            context.AgentHold.Release();
            Assert.True(await Bounded(context.Removal, "removal before fresh-account recreation check"));
            if (invalidateRole) await AssertInvalidRoleAsync(waiting);
            else
            {
                var refused = await Bounded(waiting, "disabled account refused after teardown wait");
                Assert.Equal(ComposerRuntimeStatus.Refused, refused.Status);
                Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, refused.Refusal);
                Assert.Null(refused.Runtime);
            }
            Assert.Equal(1, _clientCreations); // invalid fresh state never starts another actor/client
            if (invalidateRole) await SetStoredRoleAsync(context.Second, "member");
            else await _users.SetUserEnabledAsync(context.Second.UserId, true, Ct);
            var repaired = await Request(context.Manager, context.Second);
            Assert.Equal(ComposerRuntimeStatus.Connected, repaired.Status);
            Assert.NotSame(context.Runtime, repaired.Runtime);
            Assert.Same(repaired.Runtime, (await Request(context.Manager, context.Second)).Runtime);
            Assert.True(context.Runtime.Composer.TeardownCompletion.IsCompleted);
            Assert.Equal(2, _clientCreations);
        });
    }

    [Fact]
    public async Task DeferredTeardownHelper_WhenAssertionThrows_ReleasesAllWorkAndPreservesPrimaryFailure()
    {
        DeferredRemovalContext? captured = null;
        var sentinel = new InvalidOperationException("deliberate assertion failure while deferred agent teardown is held");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WithDeferredRemovalAsync(async context =>
        {
            captured = context;
            context.AgentHold.Arm();
            context.Stream.Release();
            await GuardAsync(context.AgentHold.Entered.Task, "cleanup failure probe entered actual deferred agent disposal");
            Assert.True(ActorCompleted(context.Runtime.Composer));
            Assert.False(context.Runtime.Composer.TeardownCompletion.IsCompleted);
            _ = Request(context.Manager, context.Second); // own a blocked recreation before deliberately aborting
            throw sentinel;
        }));
        Assert.Same(sentinel, error);
        Assert.False(error.Data.Contains("CleanupFailure"));
        Assert.NotNull(captured);
        Assert.True(captured.Removal.IsCompletedSuccessfully);
        Assert.True(captured.Runtime.Composer.TeardownCompletion.IsCompletedSuccessfully);
        Assert.True(ActorCompleted(captured.Runtime.Composer));
        Assert.Equal(0, captured.Clock.PendingTimerCount);
        Assert.All(_requests, task => Assert.True(task.IsCompleted));
        Assert.True(captured.Manager.ShutdownAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Shutdown_WithTwoDeferredActors_WaitsForEachActualTeardownIndependently()
    {
        var (first, second) = await TwoUsersAsync();
        var provider = BuildProvider();
        var manager = NewManager(provider);
        using var streamA = new StuckStream();
        using var streamB = new StuckStream();
        var clockA = new ControlledTimeProvider();
        var clockB = new ControlledTimeProvider();
        var recorder = new JoinRecorder();
        manager.OnJoiningTeardown = recorder.Record;
        _clientFactory = _ => Volatile.Read(ref _clientCreations) == 1 ? streamA.Client : streamB.Client;
        _connectHook = async (composer, token) =>
        {
            InstallActorClock(composer, Volatile.Read(ref _clientCreations) == 0 ? clockA : clockB);
            await composer.ConnectAsync(token);
        };
        var a = (await Request(manager, first)).Runtime!;
        var b = (await Request(manager, second)).Runtime!;
        try
        {
            await a.Composer.SendMessageWithEventsAsync("A holds shutdown");
            await b.Composer.SendMessageWithEventsAsync("B independently holds shutdown");
            await GuardAsync(Task.WhenAll(streamA.Started.Task, streamB.Started.Task), "both real streams entered providers");
            var shutdown = manager.ShutdownAsync();
            await DriveActorDisposeTimeoutAsync(clockA);
            await DriveActorDisposeTimeoutAsync(clockB);
            await GuardAsync(recorder.When(task => ReferenceEquals(task, a.Composer.TeardownCompletion)), "shutdown joined A's actual completion");
            await GuardAsync(recorder.When(task => ReferenceEquals(task, b.Composer.TeardownCompletion)), "shutdown joined B's actual completion");
            Assert.False(ActorCompleted(a.Composer));
            Assert.False(ActorCompleted(b.Composer));
            Assert.False(a.Composer.TeardownCompletion.IsCompleted);
            Assert.False(b.Composer.TeardownCompletion.IsCompleted);
            Assert.False(shutdown.IsCompleted);
            streamA.Release();
            await GuardAsync(a.Composer.TeardownCompletion, "first actor and agent teardown completed");
            Assert.True(ActorCompleted(a.Composer));
            Assert.False(ActorCompleted(b.Composer));
            Assert.False(b.Composer.TeardownCompletion.IsCompleted);
            Assert.False(shutdown.IsCompleted); // awaiting just A is NOT sufficient
            streamB.Release();
            await GuardAsync(shutdown, "second actual teardown releases shutdown");
            Assert.True(b.Composer.TeardownCompletion.IsCompletedSuccessfully);
            Assert.True(ActorCompleted(b.Composer));
            Assert.Empty(provider.GetRequiredService<LlmSessionRegistry>().GetAll());
        }
        finally
        {
            streamA.Release();
            streamB.Release();
            await GuardAsync(manager.ShutdownAsync(), "two-actor test cleanup joins actual shutdown");
            Assert.Equal(0, clockA.PendingTimerCount);
            Assert.Equal(0, clockB.PendingTimerCount);
        }
    }

    private sealed class StreamTurn(string reply)
    {
        public string Reply { get; } = reply;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class StreamingScript(params StreamTurn[] turns) : IChatClient
    {
        private int _next;
        public int Calls => Volatile.Read(ref _next);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _next) - 1;
            Assert.InRange(index, 0, turns.Length - 1);
            var turn = turns[index];
            turn.Entered.TrySetResult();
            await turn.Release.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, turn.Reply) { FinishReason = ChatFinishReason.Stop };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void ReleaseAll() { foreach (var turn in turns) turn.Release.TrySetResult(); }
        public void Dispose() => ReleaseAll();
    }

    private sealed class TerminalWatch : IDisposable
    {
        private readonly Composer _composer;
        private int _started;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TerminalWatch(Composer composer)
        {
            _composer = composer;
            composer.OnStreamingUpdate += Observe;
        }
        private void Observe()
        {
            if (_composer.IsStreaming) Interlocked.Exchange(ref _started, 1);
            else if (Volatile.Read(ref _started) != 0) Completed.TrySetResult();
        }
        public void Dispose() => _composer.OnStreamingUpdate -= Observe;
    }
}
