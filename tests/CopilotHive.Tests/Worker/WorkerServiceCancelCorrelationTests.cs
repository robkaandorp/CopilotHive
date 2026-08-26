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
/// Drives the REAL <c>WorkerService.ProcessMessagesAsync</c> loop and proves a
/// <c>CancelTask</c> is correlated to the ACTIVE assignment by task ID.
/// <para>
/// The rejected revision only logged <c>CancelTask.TaskId</c> and then unconditionally cancelled
/// whatever assignment happened to be active. A LATE cancel for task A — arriving after A had
/// already completed and B had been assigned — therefore aborted B and consumed B's single-flight
/// Ready claim, stranding B and desynchronising the orchestrator. The iteration-3 test ordered
/// A's late cancel BEFORE B's assignment, so it never covered that reordering.
/// </para>
/// <para>
/// These tests deliver messages in the FAILING order (assign A → complete A → assign B → cancel A)
/// and gate purely on <see cref="TaskCompletionSource"/>, never on timing delays.
/// </para>
/// </summary>
public sealed class WorkerServiceCancelCorrelationTests
{
    /// <summary>
    /// A late cancel naming an already-completed task must NOT cancel the currently active task,
    /// and must NOT consume its Ready claim.
    /// </summary>
    [Fact]
    public async Task LateCancelForCompletedTask_DoesNotCancelActiveAssignment()
    {
        var runner = new AssignmentTrackingRunner();
        using var service = BuildService(runner);

        var responses = new ScriptedResponseStream();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);

        // 1. Assign A and let it finish, so it emits its own Ready.
        responses.Push(Assignment("task-A"));
        await runner.PromptStarted("task-A");
        runner.Release("task-A");
        await runner.PromptFinished("task-A");

        // 2. Assign B and hold it inside the prompt.
        responses.Push(Assignment("task-B"));
        await runner.PromptStarted("task-B");

        // 3. The LATE cancel for A arrives while B is active.
        responses.Push(new OrchestratorMessage
        {
            Cancel = new CancelTask { TaskId = "task-A", Reason = "late" },
        });

        // 4. Prove the cancel was processed by pushing a follow-up the loop must also handle.
        //    Once the tool response is observed, the cancel ahead of it is definitely done.
        responses.Push(new OrchestratorMessage
        {
            ToolResponse = new ToolCallResponse { RequestId = "probe", Success = true, ResultJson = "{}" },
        });
        await responses.Consumed(4);

        // B is untouched: still running, its token never cancelled.
        Assert.False(runner.WasCancelled("task-B"), "A late cancel for task-A must not cancel task-B.");
        Assert.False(runner.IsFinished("task-B"));

        // Let B finish normally; it must still be able to claim its own Ready.
        runner.Release("task-B");
        await runner.PromptFinished("task-B");

        responses.Complete();
        await loop;

        // Exactly two Ready messages: one for A, one for B. If the late cancel had consumed B's
        // claim, B's own completion would have found the claim taken and emitted none.
        Assert.Equal(2, requests.ReadyCount);
    }

    /// <summary>
    /// A cancel naming the ACTIVE task retains iteration-3 behaviour: the assignment is cancelled
    /// and exactly one Ready is emitted for it.
    /// </summary>
    [Fact]
    public async Task CancelForActiveTask_CancelsItAndEmitsSingleReady()
    {
        var runner = new AssignmentTrackingRunner();
        using var service = BuildService(runner);

        var responses = new ScriptedResponseStream();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);

        responses.Push(Assignment("task-A"));
        await runner.PromptStarted("task-A");

        responses.Push(new OrchestratorMessage
        {
            Cancel = new CancelTask { TaskId = "task-A", Reason = "operator" },
        });

        // The runner observes cancellation through the assignment token and unwinds.
        await runner.PromptFinished("task-A");

        responses.Complete();
        await loop;

        Assert.True(runner.WasCancelled("task-A"), "A cancel naming the active task must cancel it.");
        Assert.Equal(1, requests.ReadyCount);
    }

    /// <summary>
    /// A cancel arriving with nothing in flight still emits exactly one Ready, so the
    /// orchestrator's idle view stays accurate (preserved from iteration 3).
    /// </summary>
    [Fact]
    public async Task CancelWithNothingInFlight_EmitsSingleReady()
    {
        var runner = new AssignmentTrackingRunner();
        using var service = BuildService(runner);

        var responses = new ScriptedResponseStream();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);

        responses.Push(new OrchestratorMessage
        {
            Cancel = new CancelTask { TaskId = "task-none", Reason = "spurious" },
        });
        await responses.Consumed(1);

        responses.Complete();
        await loop;

        Assert.Equal(1, requests.ReadyCount);
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

    /// <summary>
    /// A runner whose <c>SendPromptAsync</c> parks until released, recording per-task whether the
    /// ASSIGNMENT'S token was observed as cancelled. This is what makes the correlation assertion
    /// meaningful: it distinguishes "B kept running" from "B was aborted".
    /// </summary>
    private sealed class AssignmentTrackingRunner : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _finished = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private readonly HashSet<string> _cancelled = [];
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

        public bool WasCancelled(string taskId)
        {
            lock (_gate) return _cancelled.Contains(taskId);
        }

        public bool IsFinished(string taskId) => Slot(_finished, taskId).Task.IsCompleted;

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
                lock (_gate) _cancelled.Add(id);
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
        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
            => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Feeds scripted messages and reports how many the loop has consumed.</summary>
    private sealed class ScriptedResponseStream : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly Channel<OrchestratorMessage> _channel =
            Channel.CreateUnbounded<OrchestratorMessage>();

        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _consumedWaiters = [];
        private int _consumed;

        public OrchestratorMessage Current { get; private set; } = null!;

        public void Push(OrchestratorMessage message) => _channel.Writer.TryWrite(message);

        public void Complete() => _channel.Writer.TryComplete();

        /// <summary>Completes once the loop has pulled at least <paramref name="count"/> messages.</summary>
        public Task Consumed(int count)
        {
            lock (_gate)
            {
                if (_consumed >= count) return Task.CompletedTask;
                if (!_consumedWaiters.TryGetValue(count, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _consumedWaiters[count] = tcs;
                }
                return tcs.Task;
            }
        }

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
                return false;

            if (!_channel.Reader.TryRead(out var message))
                return false;

            Current = message;

            List<TaskCompletionSource> ready = [];
            lock (_gate)
            {
                _consumed++;
                foreach (var (threshold, tcs) in _consumedWaiters)
                {
                    if (_consumed >= threshold) ready.Add(tcs);
                }
            }
            foreach (var tcs in ready) tcs.TrySetResult();

            return true;
        }
    }

    /// <summary>
    /// Counts <c>WorkerReady</c> messages. Implements the cancellable overload explicitly: the
    /// default interface method on <see cref="IAsyncStreamWriter{T}"/> throws for any token that
    /// can be cancelled, and the worker writes Ready with the live stream token.
    /// </summary>
    private sealed class RecordingRequestStream : IClientStreamWriter<WorkerMessage>
    {
        private int _readyCount;

        public int ReadyCount => Volatile.Read(ref _readyCount);

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message)
        {
            if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Ready)
                Interlocked.Increment(ref _readyCount);
            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }

        public Task CompleteAsync() => Task.CompletedTask;
    }
}
