using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Threading.Channels;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Characterizations of the REAL <c>WorkerService.ProcessMessagesAsync</c> loop's assignment
/// OWNERSHIP SLOT: the retained-assignment state the loop installs on assignment, retains
/// through body completion (never cleared on completion), and clears only on replacement, a
/// matching cancel or loop teardown.
/// <para>
/// These tests reuse the existing loop-invocation doubles — the channel-backed
/// <see cref="ChannelResponseReader"/> response reader (from
/// <c>WorkerStreamTestDoubles.cs</c>, unchanged) and a gated fake runner with an
/// immediate-recording request writer — and gate purely on
/// <see cref="TaskCompletionSource"/>; there are no sleeps and no polling. A local
/// fault-injecting reader covers the reader-fault teardown trigger without touching the
/// frozen shared doubles.
/// </para>
/// <para>
/// Assertions observe ACTUAL handler completion (a counted <c>WorkerReady</c> write) or a
/// subsequent message boundary the loop must consume — message-delivery and prompt-finished
/// signals alone are never sufficient evidence.
/// </para>
/// <para>
/// CLEANUP CONTRACT: every test keeps its started loop(s) and reader(s) in variables
/// reachable by its <c>finally</c>, and the finally releases all runner gates,
/// completes/faults the reader as the scenario requires, and OBSERVES every started loop and
/// producer BEFORE the service's using-disposal runs — even when an assertion fails.
/// </para>
/// </summary>
public sealed class WorkerServiceAssignmentOwnershipTests
{
    /// <summary>
    /// Generous failsafe bound for awaitable observations. Never an ordering device: tests
    /// order themselves through gates, and this bound only converts a regression (a Ready
    /// that never comes) into a named failure instead of a hung run.
    /// </summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Token cancellation held at the unwind: after prompt entry the loop's token is
    /// cancelled, the body OBSERVES the cancellation, and the loop CANNOT finish — its
    /// teardown is draining the retained body, which the fake holds inside its unwind gate.
    /// Only when the unwind is released can the drain complete and the loop join. The
    /// ownership slot is then empty and the heartbeat state cleared.
    /// </summary>
    [Fact]
    public async Task TokenCancellationWhileBodyDraining_LoopCannotFinishUntilUnwindReleases()
    {
        var runner = new GatedPromptRunner();
        using var service = BuildService(runner);
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", loopCts.Token);
        try
        {
            // Assign A and park its body inside the prompt.
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");

            // Cancel the LOOP token: the reader unwinds, the loop's finally cancels the
            // retained assignment, and the body observes the cancellation.
            await loopCts.CancelAsync();
            await runner.CancelObserved("task-A");

            // The body is parked in its unwind gate, so the teardown drain cannot finish and
            // the loop CANNOT be complete. This is deterministic: the drain awaits the body
            // task, which cannot complete before the gate is released.
            Assert.False(loop.IsCompleted, "The loop must not finish while the retained body is still unwinding.");

            // Release the unwind: the drain completes, the loop joins.
            runner.ReleaseUnwind();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);

            // Ownership slot empty after loop cleanup and heartbeat state cleared. The body's
            // Ready was written with the (now cancelled) loop token, so no Ready is observable.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Equal(0, requests.ReadyCount);
        }
        finally
        {
            // Teardown even after an assertion failure: release gates, let the reader end, and
            // OBSERVE the loop before the service's using-disposal runs.
            runner.ReleaseAll();
            responses.TryComplete();
            await ObserveLoopForTeardownAsync(loop);
        }
    }

    /// <summary>
    /// EOF held at the unwind: with the body parked inside the prompt, the reader stream is
    /// COMPLETED (EOF), so the loop exits its iteration, cancels and drains the RETAINED,
    /// STILL-RUNNING assignment, and CANNOT finish while the body is held in the unwind gate.
    /// Only when the unwind is released does the drain complete and the loop join. The
    /// ownership slot is then empty and the heartbeat state cleared.
    /// </summary>
    [Fact]
    public async Task EofWhileBodyDraining_LoopCannotFinishUntilUnwindReleases()
    {
        var runner = new GatedPromptRunner();
        using var service = BuildService(runner);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            // Assign A and park its body inside the prompt: a RETAINED, running assignment.
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");

            // EOF: complete the reader stream while the body is still running.
            responses.TryComplete();

            // The loop's finally cancels the retained assignment; the body observes it.
            await runner.CancelObserved("task-A");

            // The body is parked in its unwind gate, so the teardown drain cannot finish and
            // the loop CANNOT be complete — deterministic, since the drain awaits the body.
            Assert.False(loop.IsCompleted, "The loop must not finish while the retained body is still unwinding.");

            // Release the unwind: the drain completes, the loop joins.
            runner.ReleaseUnwind();
            await loop;

            // Ownership slot empty after loop cleanup and heartbeat state cleared. Exactly ONE
            // Ready: the stream token is still live under EOF, so the drained body's
            // single-flight claim emits its own Ready during unwind (teardown never claims).
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Equal(1, requests.ReadyCount);
        }
        finally
        {
            runner.ReleaseAll();
            responses.TryComplete();
            await ObserveLoopForTeardownAsync(loop);
        }
    }

    /// <summary>
    /// Reader fault held at the unwind: with the body parked inside the prompt, the reader
    /// throws the ORIGINAL exception, the loop cancels and drains the RETAINED, STILL-RUNNING
    /// assignment, and CANNOT finish while the body is held in the unwind gate. Once the
    /// unwind is released, the loop propagates the ORIGINAL reader exception identity out of
    /// the join (consistent with the loop's reader-fault behavior), and the ownership slot is
    /// empty with the heartbeat state cleared.
    /// </summary>
    [Fact]
    public async Task ReaderFaultWhileBodyDraining_LoopCannotFinishUntilUnwindReleases()
    {
        var runner = new GatedPromptRunner();
        using var service = BuildService(runner);

        var original = new InvalidOperationException("reader fault");
        var responses = new FaultingResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            // Assign A and park its body inside the prompt: a RETAINED, running assignment.
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");

            // Reader fault: the next MoveNext throws the original exception.
            responses.ArmFault(original);

            // The loop's finally cancels the retained assignment; the body observes it.
            await runner.CancelObserved("task-A");

            // The body is parked in its unwind gate, so the teardown drain cannot finish and
            // the loop CANNOT be complete — deterministic, since the drain awaits the body.
            Assert.False(loop.IsCompleted, "The loop must not finish while the retained body is still unwinding.");

            // Release the unwind: the drain completes, the loop joins and surfaces the fault.
            runner.ReleaseUnwind();
            var propagated = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => loop);
            Assert.Same(original, propagated);

            // Ownership slot empty after loop cleanup and heartbeat state cleared. Exactly ONE
            // Ready: the stream token is still live (only the reader faulted), so the drained
            // body's single-flight claim emits its own Ready during unwind.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Equal(1, requests.ReadyCount);
        }
        finally
        {
            runner.ReleaseAll();
            responses.TryComplete();
            await ObserveLoopForTeardownAsync(loop);
        }
    }

    /// <summary>
    /// A COMPLETED assignment stays retained: a subsequent matching cancel finds it in the
    /// slot and drains it WITHOUT emitting a duplicate Ready (the body already claimed and
    /// wrote its own), and the following idle cancel — the slot now empty — emits exactly one
    /// Ready of its own. Total: exactly two Ready messages.
    /// </summary>
    [Fact]
    public async Task CompletedAssignmentRetained_MatchingCancelNoDuplicateReady_IdleCancelEmitsReady()
    {
        var runner = new GatedPromptRunner();
        using var service = BuildService(runner);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            // 1. Assign A and let it FINISH normally, writing its own Ready — it stays retained.
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");
            runner.Release("task-A");
            await runner.PromptFinished("task-A");
            await requests.WaitForReadyCountAsync(1);

            // 2. The matching cancel for the still-retained A must not duplicate the Ready.
            responses.Push(new OrchestratorMessage
            {
                Cancel = new CancelTask { TaskId = "task-A", Reason = "matching" },
            });

            // 3. The idle cancel — the slot was cleared by the matching cancel — emits its own.
            responses.Push(new OrchestratorMessage
            {
                Cancel = new CancelTask { TaskId = "task-idle", Reason = "idle" },
            });

            // 4. A boundary message proves both cancels were fully processed.
            responses.Push(new OrchestratorMessage
            {
                ToolResponse = new ToolCallResponse { RequestId = "probe", Success = true, ResultJson = "{}" },
            });
            await responses.Consumed(4);
            await requests.WaitForReadyCountAsync(2);

            responses.TryComplete();
            await loop;

            Assert.Equal(2, requests.ReadyCount);
            Assert.Equal(0, GetSlotOccupancy(service));
        }
        finally
        {
            // Release gates, let the reader end, and OBSERVE the loop before the service's
            // using-disposal runs — even after an assertion failure.
            runner.ReleaseAll();
            responses.TryComplete();
            await ObserveLoopForTeardownAsync(loop);
        }
    }

    /// <summary>
    /// The ownership slot is per-INVOCATION state left EMPTY after loop exit: a second
    /// sequential private-loop invocation on the same service instance starts clean, takes the
    /// idle-cancel branch directly, and completes normally. (A test seam only — NOT a
    /// supported RunAsync reconnect contract.)
    /// <para>
    /// Both started loops and both readers are retained in variables reachable by the
    /// test's <c>finally</c>, which releases gates, completes both readers and observes both
    /// loops BEFORE the service's using-disposal runs — even when an assertion fails.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SecondSequentialLoopInvocation_StartsWithEmptySlotAndCompletes()
    {
        var runner = new GatedPromptRunner();
        using var service = BuildService(runner);

        // Hoisted so the finally can release and join EVERY started producer, even after an
        // assertion failure mid-way through either sequential block.
        var firstResponses = new ChannelResponseReader();
        var secondResponses = new ChannelResponseReader();
        var firstLoop = Task.CompletedTask;
        var secondLoop = Task.CompletedTask;

        try
        {
            // First loop: one full assignment cycle, then EOF.
            {
                var requests = new RecordingRequestStream();
                var stream = BuildStream(requests, firstResponses);

                firstLoop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);

                firstResponses.Push(Assignment("task-A"));
                await runner.PromptStarted("task-A");
                runner.Release("task-A");
                await runner.PromptFinished("task-A");
                await requests.WaitForReadyCountAsync(1);

                firstResponses.TryComplete();
                await firstLoop;

                Assert.Equal(0, GetSlotOccupancy(service));
            }

            // Second loop on the SAME instance: the slot is empty, so an idle cancel takes the
            // no-assignment branch and emits Ready directly.
            {
                var requests = new RecordingRequestStream();
                var stream = BuildStream(requests, secondResponses);

                secondLoop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);

                secondResponses.Push(new OrchestratorMessage
                {
                    Cancel = new CancelTask { TaskId = "task-idle", Reason = "second-loop" },
                });
                secondResponses.Push(new OrchestratorMessage
                {
                    ToolResponse = new ToolCallResponse { RequestId = "probe", Success = true, ResultJson = "{}" },
                });
                await secondResponses.Consumed(2);
                await requests.WaitForReadyCountAsync(1);

                secondResponses.TryComplete();
                await secondLoop;

                Assert.Equal(0, GetSlotOccupancy(service));
            }
        }
        finally
        {
            // Release gates, end both readers, and observe BOTH loops before the service's
            // using-disposal runs — even when an assertion failed inside either block.
            runner.ReleaseAll();
            firstResponses.TryComplete();
            secondResponses.TryComplete();
            await ObserveLoopForTeardownAsync(firstLoop);
            await ObserveLoopForTeardownAsync(secondLoop);
        }
    }

    /// <summary>
    /// A ResetSessionAsync failure fails the loop BEFORE any install: the ownership slot stays
    /// empty, the heartbeat state is cleared by the loop's finally, no Ready is written (the
    /// body never started), and the loop task faults with the reset failure.
    /// </summary>
    [Fact]
    public async Task ResetFailure_NoAssignmentInstalledAndHeartbeatStateCleared()
    {
        var runner = new GatedPromptRunner(resetFails: true);
        using var service = BuildService(runner);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            // An assignment whose session reset fails — before the CTS, claim and body exist.
            responses.Push(Assignment("task-A"));
            await runner.ResetAttempted();

            // The reset failure propagates out of the message loop; nothing was installed.
            var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => loop);

            // No installed assignment, cleared heartbeat state, and no Ready (nothing ran).
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Equal(0, requests.ReadyCount);
            Assert.IsType<InvalidOperationException>(failure);
        }
        finally
        {
            // Release gates, let the reader end, and OBSERVE the (already faulted) loop before
            // the service's using-disposal runs — even after an assertion failure. The loop
            // has already been awaited in the try, so this join is instantaneous.
            runner.ReleaseAll();
            responses.TryComplete();
            await ObserveLoopForTeardownAsync(loop);
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static OrchestratorMessage Assignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-1",
            GoalDescription = "desc",
            Prompt = "prompt",
            Role = GrpcWorkerRole.Coder,
        },
    };

    private static WorkerService BuildService(IAgentRunner runner)
    {
        var service = new WorkerService("http://localhost:9999", "worker-1", ["coder"]);

        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);

        typeof(WorkerService).GetField("_assignedId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, "worker-1");

        return service;
    }

    private static AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> BuildStream(
        IClientStreamWriter<WorkerMessage> requests, IAsyncStreamReader<OrchestratorMessage> responses)
        => new(requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

    private static Task InvokeProcessMessages(
        WorkerService service,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        CancellationToken ct)
    {
        var method = typeof(WorkerService).GetMethod(
            "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, [stream, assignedId, ct])!;
    }

    /// <summary>Reflects the service-owned ownership slot: 1 when occupied, 0 when empty.</summary>
    private static int GetSlotOccupancy(WorkerService service)
    {
        var slot = typeof(WorkerService).GetField(
            "_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service);
        return slot is null ? 0 : 1;
    }

    /// <summary>Reflects the heartbeat busy state: the current task ID, or null.</summary>
    private static string? GetHeartbeatTaskId(WorkerService service)
        => (string?)typeof(WorkerService).GetField(
            "_currentTaskId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service);

    /// <summary>
    /// Teardown-only observation of a started loop: settles it WITHOUT propagating its
    /// outcome, so a faulting loop (cancelled teardown, reader fault, reset failure) can
    /// never mask the assertion that failed inside the test body. The loop's actual outcome
    /// is always asserted in the try body on the normal path; this join exists purely to
    /// guarantee every producer is quiescent before the service's using-disposal runs.
    /// </summary>
    private static async Task ObserveLoopForTeardownAsync(Task loop)
    {
        try
        {
            await loop;
        }
        catch
        {
            // Expected on a failure path: the loop faults with OCE (cancelled teardown) or
            // the scenario's reader fault. Swallowed HERE ONLY, never on the assert path.
        }
    }

    /// <summary>
    /// A runner whose <c>SendPromptAsync</c> parks until released and whose session reset can
    /// be made to fail. Gates are TCS-only: no sleeps, no polling. After the assignment token
    /// cancels a parked prompt, the unwind is HELD in a dedicated gate until
    /// <see cref="ReleaseUnwind"/> — that is what lets a test prove the loop's teardown drain
    /// cannot complete while the body is still unwinding.
    /// </summary>
    private sealed class GatedPromptRunner(bool resetFails = false) : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _finished = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private readonly Dictionary<string, TaskCompletionSource> _cancelObserved = [];
        private readonly TaskCompletionSource _unwindGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resetAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _taskId;

        private TaskCompletionSource Slot(Dictionary<string, TaskCompletionSource> map, string key)
        {
            lock (_gate)
            {
                if (!map.TryGetValue(key, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    map[key] = tcs;
                }
                return tcs;
            }
        }

        public Task PromptStarted(string taskId) => Slot(_started, taskId).Task;
        public Task PromptFinished(string taskId) => Slot(_finished, taskId).Task;
        public void Release(string taskId) => Slot(_release, taskId).TrySetResult();

        /// <summary>Completes when the body has OBSERVED its token as cancelled (pre-unwind).</summary>
        public Task CancelObserved(string taskId) => Slot(_cancelObserved, taskId).Task;

        /// <summary>Releases the held unwind so a cancelled body can finish draining.</summary>
        public void ReleaseUnwind() => _unwindGate.TrySetResult();

        /// <summary>Teardown failsafe: releases every gate a parked producer could hold.</summary>
        public void ReleaseAll()
        {
            ReleaseUnwind();
            lock (_gate)
            {
                foreach (var tcs in _release.Values) tcs.TrySetResult();
            }
            _resetAttempted.TrySetResult();
        }

        public Task ResetAttempted() => _resetAttempted.Task;

        public void SetCurrentTaskId(string? taskId) => _taskId = taskId;

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var id = _taskId ?? "(unknown)";
            Slot(_started, id).TrySetResult();
            try
            {
                await Slot(_release, id).Task.WaitAsync(ct);
                return "done";
            }
            catch (OperationCanceledException)
            {
                // The cancellation is OBSERVED; the unwind is HELD until the test releases it,
                // so the loop's drain stays parked here for the duration of the assertion.
                Slot(_cancelObserved, id).TrySetResult();
                await _unwindGate.Task;
                throw;
            }
            finally
            {
                Slot(_finished, id).TrySetResult();
            }
        }

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) { }
        public object? GetSession() => null;
        public void SetMaxContextTokens(int maxTokens) { }
        public int GetContextUsagePercent() => 0;
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) { }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task ResetSessionAsync(
            string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
        {
            _resetAttempted.TrySetResult();
            if (resetFails)
                throw new InvalidOperationException("reset failed");
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Counts <c>WorkerReady</c> messages and lets tests await a specific count
    /// deterministically. Implements the cancellable overload explicitly: the default
    /// interface method on <see cref="IAsyncStreamWriter{T}"/> throws for any token that can
    /// be cancelled, and the worker writes Ready with the live stream token.
    /// </summary>
    private sealed class RecordingRequestStream : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _waiters = [];
        private int _readyCount;

        /// <summary>Snapshot of how many Ready messages were written so far.</summary>
        public int ReadyCount
        {
            get { lock (_gate) return _readyCount; }
        }

        /// <summary>Completes once at least <paramref name="count"/> Ready messages were written.</summary>
        public Task WaitForReadyCountAsync(int count)
        {
            lock (_gate)
            {
                if (_readyCount >= count) return Task.CompletedTask;
                if (!_waiters.TryGetValue(count, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[count] = tcs;
                }
                return tcs.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message)
        {
            if (message.PayloadCase != WorkerMessage.PayloadOneofCase.Ready)
                return Task.CompletedTask;

            List<TaskCompletionSource> ready = [];
            List<int> satisfied = [];
            lock (_gate)
            {
                _readyCount++;
                foreach (var (threshold, tcs) in _waiters)
                {
                    if (_readyCount >= threshold)
                    {
                        ready.Add(tcs);
                        satisfied.Add(threshold);
                    }
                }
                foreach (var threshold in satisfied) _waiters.Remove(threshold);
            }
            foreach (var tcs in ready) tcs.TrySetResult();
            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }

        public Task CompleteAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// A channel-backed reader that behaves like <see cref="ChannelResponseReader"/> until
    /// <see cref="ArmFault"/> is called, then throws the ORIGINAL exception from the next
    /// <c>MoveNext</c> — modelling a reader fault whose identity the loop must propagate.
    /// Local to these tests so the frozen shared doubles in WorkerStreamTestDoubles.cs stay
    /// byte-for-byte unchanged.
    /// </summary>
    private sealed class FaultingResponseReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly Channel<OrchestratorMessage> _channel =
            Channel.CreateUnbounded<OrchestratorMessage>();

        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _consumedWaiters = [];
        private int _consumed;
        private Exception? _fault;

        public OrchestratorMessage Current { get; private set; } = null!;

        public void Push(OrchestratorMessage message) => _channel.Writer.TryWrite(message);

        public void TryComplete() => _channel.Writer.TryComplete();

        /// <summary>
        /// One-shot: the next <c>MoveNext</c> outcome surfaces <paramref name="fault"/>. Also
        /// completes the channel, so a loop ALREADY parked inside <c>WaitToReadAsync</c> wakes
        /// deterministically (no further message is required) and reaches the fault check.
        /// </summary>
        public void ArmFault(Exception fault)
        {
            lock (_gate) _fault = fault;
            _channel.Writer.TryComplete();
        }

        public Task Consumed(int count)
        {
            lock (_gate)
            {
                if (_consumed >= count) return Task.CompletedTask;
                if (!_consumedWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _consumedWaiters[count] = waiter;
                }
                return waiter.Task;
            }
        }

        /// <summary>Consumes and throws the armed fault, if any. One-shot.</summary>
        private Exception? TakeFault()
        {
            lock (_gate)
            {
                var fault = _fault;
                _fault = null;
                return fault;
            }
        }

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            // Pre-check: covers a fault armed while the loop was BETWEEN MoveNext calls.
            if (TakeFault() is { } preFault)
                throw preFault;

            if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                // Post-check: covers a fault armed while THIS call was parked inside
                // WaitToReadAsync — TryComplete woke the park, and the fault must fire
                // instead of a plain EOF.
                if (TakeFault() is { } postFault)
                    throw postFault;

                return false;
            }

            if (!_channel.Reader.TryRead(out var message))
            {
                // The reader signalled data but none was left (a concurrent complete): fall
                // through the fault check so an armed fault still fires rather than EOF.
                if (TakeFault() is { } emptyFault)
                    throw emptyFault;

                return false;
            }

            Current = message;
            List<TaskCompletionSource> ready = [];
            lock (_gate)
            {
                _consumed++;
                foreach (var (threshold, waiter) in _consumedWaiters)
                {
                    if (_consumed >= threshold)
                        ready.Add(waiter);
                }
            }
            foreach (var waiter in ready)
                waiter.TrySetResult();

            return true;
        }
    }
}