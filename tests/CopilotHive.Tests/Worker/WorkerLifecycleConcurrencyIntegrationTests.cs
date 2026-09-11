using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Runtime.CompilerServices;

using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Deterministic, TCS-gated integration tests for the worker's lazy-client lifecycle. These
/// tests exercise the real <see cref="SharpCoderRunner"/> and the production
/// <c>WorkerService.ProcessMessagesAsync</c> loop; no timing delays or sleep-based polling are
/// used.
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerLifecycleConcurrencyIntegrationTests
{
    /// <summary>
    /// Two prompt calls overlap while the first lazy creation is stopped inside the real
    /// provisioning callback. The second call must park behind the runner lifecycle gate: after
    /// release, both calls use the one client produced by the one factory invocation.
    /// </summary>
    [Fact]
    public async Task SharpCoderRunner_ConcurrentFirstPrompts_ConstructExactlyOneClient()
    {
        var workDir = CreateTempWorkDir();
        var provisionEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ImmediateChatClient();
        var provisionCalls = 0;
        var factoryCalls = 0;
        var runner = new SharpCoderRunner("/config-repo");

        runner.SetConfigProvisioner(async (_, ct) =>
        {
            Interlocked.Increment(ref provisionCalls);
            provisionEntered.TrySetResult(true);
            await releaseProvision.Task.WaitAsync(ct);
        });
        runner.ClientCreationSeam = _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return client;
        };

        try
        {
            var first = runner.SendPromptAsync("first", workDir, TestContext.Current.CancellationToken);
            await provisionEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            // This call executes synchronously until it parks on the held lifecycle semaphore.
            var second = runner.SendPromptAsync("second", workDir, TestContext.Current.CancellationToken);

            Assert.Equal(1, Volatile.Read(ref provisionCalls));
            Assert.Equal(0, Volatile.Read(ref factoryCalls));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);

            releaseProvision.TrySetResult(true);
            await Task.WhenAll(first, second);

            Assert.Equal(1, provisionCalls);
            Assert.Equal(1, factoryCalls);
            Assert.Equal(0, client.DisposeCount);
        }
        finally
        {
            releaseProvision.TrySetResult(true);
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }

        Assert.Equal(1, client.DisposeCount);
    }

    /// <summary>
    /// Queues assignment B while A is stopped inside the real SharpCoder streaming call. The
    /// production WorkerService loop must await A before it resets the shared runner for B. At the
    /// overlap point exactly one client is live and B's client has not been constructed; after A
    /// completes the replacement is sequential, never concurrent, and A is not disposed while
    /// still streaming.
    /// </summary>
    [Fact]
    public async Task WorkerService_QueuedAssignmentWaitsForActiveTaskBeforeReplacingClient()
    {
        var tracker = new ClientTracker();
        var runner = new SharpCoderRunner("/config-repo")
        {
            ClientCreationSeam = _ => tracker.Create(),
        };
        var service = new WorkerService("http://localhost:9999", "worker-overlap", ["coder"]);
        ReplaceRunner(service, runner);
        var steps = new[]
        {
            new StreamStep(new OrchestratorMessage { Assignment = Assignment("task-a", "model-a") }),
            new StreamStep(new OrchestratorMessage { Assignment = Assignment("task-b", "model-b") }),
            new StreamStep(null),
        };
        var responses = new GatedResponseStream(steps);
        var requests = new CapturingRequestStream();
        using var stream = CreateDuplex(requests, responses);
        var stdErr = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(stdErr);

        var connection = TestConnectionFactory.Attach(service, "worker-overlap", stream, service.TestProvisioner);

        Task processTask = Task.CompletedTask;
        try
        {
            processTask = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);
            await steps[0].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[0].Release.TrySetResult(true);
            var clientA = await tracker.WaitForClientAsync(0, TestContext.Current.CancellationToken);
            await clientA.StreamEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            await steps[1].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var deliveryReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() =>
            {
                steps[1].Release.TrySetResult(true);
                deliveryReturned.TrySetResult(true);
            }, TestContext.Current.CancellationToken);
            await deliveryReturned.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, tracker.CreatedCount);
            Assert.Equal(1, tracker.LiveCount);
            Assert.Equal(0, clientA.DisposeCount);
            Assert.False(clientA.DisposedWhileStreaming);

            clientA.ReleaseStream.TrySetResult(true);
            var clientB = await tracker.WaitForClientAsync(1, TestContext.Current.CancellationToken);
            await clientB.StreamEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, tracker.CreatedCount);
            Assert.Equal(1, tracker.LiveCount);
            Assert.Equal(1, tracker.MaxLiveCount);
            Assert.Equal(1, clientA.DisposeCount);
            Assert.False(clientA.DisposedWhileStreaming);
            Assert.False(clientB.DisposedWhileStreaming);

            clientB.ReleaseStream.TrySetResult(true);
            await requests.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);
            await steps[2].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[2].Release.TrySetResult(true);
            await processTask;

            Assert.DoesNotContain(nameof(InvalidOperationException), stdErr.ToString());
            Assert.DoesNotContain(nameof(ObjectDisposedException), stdErr.ToString());
            Assert.Equal(1, tracker.LiveCount);
        }
        finally
        {
            Console.SetError(originalErr);
            foreach (var step in steps)
                step.Release.TrySetResult(true);
            tracker.ReleaseAll();
            try { await processTask; } catch (OperationCanceledException) { }
            service.Dispose();
        }

        Assert.Equal(0, tracker.LiveCount);
        Assert.Equal(1, tracker.MaxLiveCount);
        Assert.All(tracker.Clients, client => Assert.False(client.DisposedWhileStreaming));
    }

    /// <summary>
    /// Reproduces the double-Ready/tool-response deadlock sequence against the production
    /// WorkerService loop. A completes a real bridge request, then both A's body and the cancel
    /// handler attempt to claim Ready; only one write may occur. B is then assigned and its own
    /// ToolResponse must be consumed by that same response-reading loop without the loop parking
    /// on B's active task.
    /// </summary>
    [Fact]
    public async Task WorkerService_CompletionThenCancel_EmitsSingleReadyAndProcessesNextToolResponse()
    {
        const string AssignedId = "worker-tool-deadlock";
        var runner = new ToolRoundTripRunner();
        var service = new WorkerService("http://localhost:9999", AssignedId, ["coder"]);
        ReplaceRunner(service, runner);

        var steps = new[]
        {
            new StreamStep(new OrchestratorMessage { Assignment = Assignment("task-a", "model-a") }),
            new StreamStep(new OrchestratorMessage()), // A ToolResponse, populated after request ID is known.
            new StreamStep(new OrchestratorMessage
            {
                Cancel = new CancelTask { TaskId = "task-a", Reason = "late cancel after completion" },
            }),
            new StreamStep(new OrchestratorMessage { Assignment = Assignment("task-b", "model-b") }),
            new StreamStep(new OrchestratorMessage()), // B ToolResponse, populated after request ID is known.
            new StreamStep(null),
        };
        var requests = new CapturingRequestStream();
        var responses = new GatedResponseStream(steps);
        using var stream = CreateDuplex(requests, responses);
        var connection = AttachToolStream(service, stream, AssignedId);

        Task processTask = Task.CompletedTask;
        try
        {
            processTask = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

            // A starts and waits inside WorkerService.RequestClarificationAsync's pending TCS.
            await steps[0].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[0].Release.TrySetResult(true);
            await runner.WaitForTurnEnteredAsync(0, TestContext.Current.CancellationToken);
            var toolA = await requests.WaitForToolRequestAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal("task-a", toolA.ToolRequest.TaskId);

            await steps[1].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[1].Message = ToolResponse(toolA, "answer-a");
            steps[1].Release.TrySetResult(true);
            await runner.WaitForTurnCompletedAsync(0, TestContext.Current.CancellationToken);
            await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // Late cancel drains completed A. The body already owns its Ready claim, so the cancel
            // handler must not write another Ready.
            await steps[2].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[2].Release.TrySetResult(true);
            await steps[3].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, requests.ReadyCount);

            // B starts with one in-flight turn/client. Its ToolResponse arrives on the same loop;
            // if the loop incorrectly awaited B here, this awaited round-trip would deadlock.
            steps[3].Release.TrySetResult(true);
            await runner.WaitForTurnEnteredAsync(1, TestContext.Current.CancellationToken);
            var toolB = await requests.WaitForToolRequestAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal("task-b", toolB.ToolRequest.TaskId);
            Assert.Equal(1, runner.InFlightTurns);
            Assert.Equal(1, runner.LiveClients);
            Assert.Equal(1, runner.MaxInFlightTurns);
            Assert.Equal(1, runner.MaxLiveClients);
            Assert.False(runner.ResetWhileTurnActive);

            await steps[4].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[4].Message = ToolResponse(toolB, "answer-b");
            steps[4].Release.TrySetResult(true);
            await runner.WaitForTurnCompletedAsync(1, TestContext.Current.CancellationToken);
            await requests.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);

            Assert.Equal(["answer-a", "answer-b"], runner.ToolResults);
            Assert.Equal(1, runner.MaxInFlightTurns);
            Assert.Equal(1, runner.MaxLiveClients);
            Assert.Equal(2, requests.ReadyCount); // exactly one for A, exactly one for B.

            await steps[5].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[5].Release.TrySetResult(true);
            await processTask;
        }
        finally
        {
            foreach (var step in steps)
                step.Release.TrySetResult(true);
            runner.ReleaseAll();
            try { await processTask; } catch (OperationCanceledException) { }
            service.Dispose();
        }

        Assert.Equal(0, runner.LiveClients);
        Assert.False(runner.ResetWhileTurnActive);
    }

    private static OrchestratorMessage ToolResponse(WorkerMessage request, string result) => new()
    {
        ToolResponse = new ToolCallResponse
        {
            RequestId = request.ToolRequest.RequestId,
            ResultJson = result,
            Success = true,
        },
    };

    private static string CreateTempWorkDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"runner-concurrency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static TaskAssignment Assignment(string taskId, string model) => new()
    {
        TaskId = taskId,
        GoalId = "goal-overlap",
        GoalDescription = "Exercise overlapping worker assignments",
        Prompt = $"run {taskId}",
        Role = GrpcWorkerRole.Coder,
        Model = model,
    };

    private static void ReplaceRunner(WorkerService service, IAgentRunner runner)
    {
        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._agentRunner field not found.");
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);
    }

    private static WorkerConnection AttachToolStream(
        WorkerService service,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId) =>
        TestConnectionFactory.Attach(service, assignedId, stream, service.TestProvisioner);

    private static Task InvokeProcessMessages(
        WorkerService service,
        WorkerConnection connection,
        CancellationToken ct)
    {
        var method = typeof(WorkerService).GetMethod(
            "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService.ProcessMessagesAsync not found.");
        return (Task)method.Invoke(service, [connection, ct])!;
    }

    private static AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> CreateDuplex(
        IClientStreamWriter<WorkerMessage> requests,
        IAsyncStreamReader<OrchestratorMessage> responses) =>
        new(
            requests,
            responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

    private sealed class StreamStep(OrchestratorMessage? message)
    {
        internal OrchestratorMessage? Message { get; set; } = message;
        internal TaskCompletionSource<bool> MoveNextEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Deliberately allows synchronous continuations: the overlap test uses release-return as
        // a deterministic barrier proving the production loop reached its next incomplete await.
        internal TaskCompletionSource<bool> Release { get; } = new();
    }

    private sealed class GatedResponseStream(IReadOnlyList<StreamStep> steps)
        : IAsyncStreamReader<OrchestratorMessage>
    {
        private int _index = -1;
        private OrchestratorMessage? _current;

        public OrchestratorMessage Current =>
            _current ?? throw new InvalidOperationException("No current response message.");

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var next = Interlocked.Increment(ref _index);
            if (next >= steps.Count)
                return false;

            var step = steps[next];
            step.MoveNextEntered.TrySetResult(true);
            await step.Release.Task.WaitAsync(cancellationToken);
            _current = step.Message;
            return _current is not null;
        }
    }

    private sealed class CapturingRequestStream : FakeClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource<bool>> _readyWaiters = [];
        private readonly List<WorkerMessage> _toolRequests = [];
        private readonly Dictionary<int, TaskCompletionSource<WorkerMessage>> _toolRequestWaiters = [];
        private int _readyCount;

        internal int ReadyCount { get { lock (_gate) return _readyCount; } }

        public override Task WriteAsync(WorkerMessage message)
        {
            TaskCompletionSource<bool>? readyWaiter = null;
            TaskCompletionSource<WorkerMessage>? toolWaiter = null;
            lock (_gate)
            {
                if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Ready)
                {
                    _readyCount++;
                    _readyWaiters.TryGetValue(_readyCount, out readyWaiter);
                }
                else if (message.PayloadCase == WorkerMessage.PayloadOneofCase.ToolRequest)
                {
                    var index = _toolRequests.Count;
                    _toolRequests.Add(message);
                    _toolRequestWaiters.TryGetValue(index, out toolWaiter);
                }
            }

            readyWaiter?.TrySetResult(true);
            toolWaiter?.TrySetResult(message);
            return Task.CompletedTask;
        }

        internal Task WaitForReadyCountAsync(int count, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_readyCount >= count)
                    return Task.CompletedTask;
                if (!_readyWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _readyWaiters[count] = waiter;
                }
                return waiter.Task.WaitAsync(ct);
            }
        }

        internal Task<WorkerMessage> WaitForToolRequestAsync(int index, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_toolRequests.Count > index)
                    return Task.FromResult(_toolRequests[index]);
                if (!_toolRequestWaiters.TryGetValue(index, out var waiter))
                {
                    waiter = new TaskCompletionSource<WorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _toolRequestWaiters[index] = waiter;
                }
                return waiter.Task.WaitAsync(ct);
            }
        }

        public override Task CompleteAsync() => Task.CompletedTask;
    }

    private sealed class ToolRoundTripRunner : IAgentRunner
    {
        private readonly TaskCompletionSource<bool>[] _turnEntered =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ];
        private readonly TaskCompletionSource<bool>[] _turnCompleted =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ];
        private readonly object _gate = new();
        private readonly List<string> _toolResults = [];
        private IToolCallBridge? _bridge;
        private string? _taskId;
        private int _turnCount;
        private int _inFlightTurns;
        private int _maxInFlightTurns;
        private int _liveClients;
        private int _maxLiveClients;

        internal int InFlightTurns => Volatile.Read(ref _inFlightTurns);
        internal int MaxInFlightTurns => Volatile.Read(ref _maxInFlightTurns);
        internal int LiveClients => Volatile.Read(ref _liveClients);
        internal int MaxLiveClients => Volatile.Read(ref _maxLiveClients);
        internal bool ResetWhileTurnActive { get; private set; }
        internal IReadOnlyList<string> ToolResults
        {
            get { lock (_gate) return _toolResults.ToList(); }
        }

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport { get; } = new()
        {
            TaskVerdict = TaskVerdict.Pass,
            Summary = "tool round-trip completed",
            Issues = [],
        };

        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) => _bridge = bridge;
        public void SetCurrentTaskId(string? taskId) => _taskId = taskId;
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(CopilotHive.Workers.WorkerRole role, string agentsMdContent) { }
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
        {
            if (Volatile.Read(ref _inFlightTurns) != 0)
                ResetWhileTurnActive = true;
            Interlocked.Exchange(ref _liveClients, 0);
            return Task.CompletedTask;
        }

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref _turnCount) - 1;
            if (Interlocked.CompareExchange(ref _liveClients, 1, 0) == 0)
                UpdateMax(ref _maxLiveClients, 1);

            var inFlight = Interlocked.Increment(ref _inFlightTurns);
            UpdateMax(ref _maxInFlightTurns, inFlight);
            _turnEntered[index].TrySetResult(true);
            try
            {
                var bridge = _bridge ?? throw new InvalidOperationException("Tool bridge was not set.");
                var taskId = _taskId ?? throw new InvalidOperationException("Task ID was not set.");
                var result = await bridge.RequestClarificationAsync(
                    taskId, $"question-{index}", ct);
                lock (_gate) _toolResults.Add(result);
                _turnCompleted[index].TrySetResult(true);
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightTurns);
            }
        }

        private static void UpdateMax(ref int target, int value)
        {
            int observed;
            while (value > (observed = Volatile.Read(ref target)))
                Interlocked.CompareExchange(ref target, value, observed);
        }

        internal Task WaitForTurnEnteredAsync(int index, CancellationToken ct) =>
            _turnEntered[index].Task.WaitAsync(ct);

        internal Task WaitForTurnCompletedAsync(int index, CancellationToken ct) =>
            _turnCompleted[index].Task.WaitAsync(ct);

        internal void ReleaseAll()
        {
            // Pending bridge calls are owned by WorkerService and are cancelled by stream teardown.
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _liveClients, 0);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ClientTracker
    {
        private readonly object _gate = new();
        private readonly List<TrackedChatClient> _clients = [];
        private readonly Dictionary<int, TaskCompletionSource<TrackedChatClient>> _waiters = [];
        private int _liveCount;
        private int _maxLiveCount;

        internal IReadOnlyList<TrackedChatClient> Clients
        {
            get { lock (_gate) return _clients.ToList(); }
        }

        internal int CreatedCount { get { lock (_gate) return _clients.Count; } }
        internal int LiveCount => Volatile.Read(ref _liveCount);
        internal int MaxLiveCount => Volatile.Read(ref _maxLiveCount);

        internal TrackedChatClient Create()
        {
            var client = new TrackedChatClient(this);
            TaskCompletionSource<TrackedChatClient>? waiter;
            lock (_gate)
            {
                var index = _clients.Count;
                _clients.Add(client);
                _waiters.TryGetValue(index, out waiter);
            }

            var live = Interlocked.Increment(ref _liveCount);
            int observed;
            while (live > (observed = Volatile.Read(ref _maxLiveCount)))
                Interlocked.CompareExchange(ref _maxLiveCount, live, observed);
            waiter?.TrySetResult(client);
            return client;
        }

        internal Task<TrackedChatClient> WaitForClientAsync(int index, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_clients.Count > index)
                    return Task.FromResult(_clients[index]);
                if (!_waiters.TryGetValue(index, out var waiter))
                {
                    waiter = new TaskCompletionSource<TrackedChatClient>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[index] = waiter;
                }
                return waiter.Task.WaitAsync(ct);
            }
        }

        internal void OnDisposed() => Interlocked.Decrement(ref _liveCount);

        internal void ReleaseAll()
        {
            foreach (var client in Clients)
                client.ReleaseStream.TrySetResult(true);
        }
    }

    private sealed class TrackedChatClient(ClientTracker owner) : IChatClient
    {
        private int _disposeCount;
        private int _streaming;

        internal TaskCompletionSource<bool> StreamEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseStream { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool DisposedWhileStreaming { get; private set; }
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ChatClientMetadata Metadata => new("tracked", null, "tracked-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => StreamAsync(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Exchange(ref _streaming, 1);
            StreamEntered.TrySetResult(true);
            try
            {
                await ReleaseStream.Task.WaitAsync(ct);
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
                yield return new ChatResponseUpdate
                {
                    FinishReason = ChatFinishReason.Stop,
                    Role = ChatRole.Assistant,
                };
            }
            finally
            {
                Interlocked.Exchange(ref _streaming, 0);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeCount, 1) != 0)
                return;
            DisposedWhileStreaming = Volatile.Read(ref _streaming) == 1;
            owner.OnDisposed();
        }
    }

    private sealed class ImmediateChatClient : IChatClient
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ChatClientMetadata Metadata => new("immediate", null, "immediate-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => StreamAsync(cancellationToken);

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
            yield return new ChatResponseUpdate
            {
                FinishReason = ChatFinishReason.Stop,
                Role = ChatRole.Assistant,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}

/// <summary>
/// Deterministic concurrency tests for the WorkerService outbound send boundary (per-instance
/// awaited serialization of every WorkStream <c>RequestStream.WriteAsync</c>).
/// <para>
/// Every test drives REAL WorkerService sending paths — the public <c>IToolCallBridge</c> methods,
/// the real <c>ProcessMessagesAsync</c> loop (Ready/cancel producers), and the real assignment body
/// (terminal Complete) — against <see cref="GatedOverlapDetectingRequestStream"/>, which parks each
/// underlying write and throws if a second write ever overlaps a parked one. There are NO sleeps:
/// every wait is a TCS gate with a bounded timeout used only as a failure guard, and every release
/// happens in guaranteed <c>finally</c> drains. These tests prove fake-stream write serialization
/// ONLY; they make no claim about real orchestrator restarts or real worker-cancellation experiments
/// (out of scope for this goal).
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerServiceSendSerializationTests
{
    /// <summary>
    /// Re-sweep cadence for the teardown drain. This is NOT a bail-out bound: it only decides how
    /// often the drain re-releases the fake while waiting for a producer to settle.
    /// </summary>
    private static readonly TimeSpan DrainSweepInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Teardown drain for <c>finally</c> blocks: puts the fake into teardown mode and joins every
    /// outstanding producer (message loop, bridge sends) so no producer task is left alive once
    /// the <c>finally</c> completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE LATE-ACQUIRER PROBLEM. A plain <c>ReleaseAllParkedWrites</c> sweep only frees writes
    /// already inside the fake. If a test fails while writer A is parked in the fake and writer B
    /// is still queued on the production send gate, the sweep frees A, A releases the permit, and
    /// B only then acquires it and parks in the fake — after the sweep has run. Draining B would
    /// block for its whole hang guard and still leave B's task alive.
    /// </para>
    /// <para>
    /// The fix is convergence rather than a single sweep:
    /// <see cref="GatedOverlapDetectingRequestStream.EnterTeardownMode"/> makes every FUTURE fake
    /// entry release immediately, and the loop below re-sweeps and re-waits until each producer
    /// has ACTUALLY terminated. There is deliberately NO bail-out on an incomplete producer: the
    /// loop exits only when <see cref="Task.IsCompleted"/> is true (whether the producer ran to
    /// completion, faulted, or cancelled), so the method's guarantee is the one it documents.
    /// </para>
    /// <para>
    /// WHY THIS TERMINATES. In teardown a producer can only be blocked on (i) the production send
    /// gate, (ii) a park inside the fake, (iii) the response reader, or (iv) a pending
    /// <c>ToolCallResponse</c>. Teardown mode makes (ii) non-blocking, which in turn bounds (i)
    /// because the permit holder is always inside the fake; callers cancel the loop token before
    /// draining, which ends (iii); and the tests that create (iv) cancel the originating token in
    /// the same <c>finally</c>. Every blocking condition is therefore removed BEFORE the wait
    /// begins, so each iteration makes progress and the loop converges.
    /// </para>
    /// <para>
    /// Every fault is swallowed. That is deliberate and applies to teardown ONLY: this runs in a
    /// <c>finally</c> that may be executing because an assertion already failed, and rethrowing a
    /// drain fault there would replace the PRIMARY failure with a secondary one.
    /// </para>
    /// </remarks>
    private static async Task DrainProducersAsync(
        GatedOverlapDetectingRequestStream requests, params Task?[] producers)
    {
        // Future entries no longer park, so a producer that acquires the gate late unblocks by
        // itself rather than waiting for another sweep.
        requests.EnterTeardownMode();

        foreach (var producer in producers)
        {
            if (producer is null)
                continue;

            while (!producer.IsCompleted)
            {
                // Re-sweep before each wait: a producer that parked between the previous release
                // and now is freed here.
                requests.ReleaseAllParkedWrites();

                try
                {
                    await producer.WaitAsync(DrainSweepInterval);
                }
                catch (Exception)
                {
                    // Either the producer terminated by faulting/cancelling — in which case the
                    // loop condition now sees IsCompleted and exits — or the sweep interval
                    // elapsed and we simply re-sweep and wait again. Never rethrown: teardown
                    // must not mask the test's own failure.
                }
            }
        }

        // Final sweep so nothing a just-finished producer started is left parked.
        requests.ReleaseAllParkedWrites();
    }

    /// <summary>
    /// Tool-vs-tool: the second bridge send must park behind the send gate while the first write is
    /// parked in the fake, then enter ONLY after the first write completes. Payloads, worker/task
    /// IDs and request IDs must be preserved exactly.
    /// </summary>
    [Fact]
    public async Task ToolVsTool_SecondSendWaitsForGate_NoOverlapAndPayloadsPreserved()
    {
        var harness = new SendHarness();
        Task? first = null;
        Task? second = null;
        try
        {
            first = harness.Service.ReportProgressAsync(
                "task-ser", "running", "progress-details", CancellationToken.None);
            await harness.Requests.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(1, harness.Requests.EnteredWriteCount);
            Assert.Equal(0, harness.SendGateCurrentCount); // the parked write owns the permit

            // Starts while the first write is parked; must park on the send gate, never on the wire.
            second = harness.Service.ReportNarrativeAsync("task-ser", "narrative-text", CancellationToken.None);

            // Arrival barrier: the second send is provably PARKED at the boundary — and has
            // written nothing — before the first write is released.
            await harness.WaitForSendGateWaitersAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(1, harness.Requests.EnteredWriteCount);
            Assert.False(harness.Requests.OverlapDetected);

            harness.Requests.ReleaseCurrentWrite();
            await first.WaitAsync(TestContext.Current.CancellationToken);

            await harness.Requests.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
            harness.Requests.ReleaseCurrentWrite();
            await second.WaitAsync(TestContext.Current.CancellationToken);

            Assert.False(harness.Requests.OverlapDetected);
            Assert.Equal(2, harness.Requests.EnteredWriteCount);
            Assert.Equal(2, harness.Requests.CompletedWriteCount);

            var progress = Assert.Single(harness.Requests.EnteredWrites, m => m.PayloadCase == WorkerMessage.PayloadOneofCase.ToolRequest
                && m.ToolRequest.ToolName == "report_progress");
            Assert.Equal("worker-send-ser", progress.WorkerId);
            Assert.Equal("task-ser", progress.ToolRequest.TaskId);
            Assert.NotEmpty(progress.ToolRequest.RequestId);
            Assert.Contains("progress-details", progress.ToolRequest.ArgumentsJson);
            Assert.Contains("running", progress.ToolRequest.ArgumentsJson);

            var narrative = harness.Requests.EnteredWrites[1];
            Assert.Equal(WorkerMessage.PayloadOneofCase.ToolRequest, narrative.PayloadCase);
            Assert.Equal("report_narrative", narrative.ToolRequest.ToolName);
            Assert.Equal("worker-send-ser", narrative.WorkerId);
            Assert.Equal("task-ser", narrative.ToolRequest.TaskId);
            Assert.NotEqual(progress.ToolRequest.RequestId, narrative.ToolRequest.RequestId);
            Assert.Contains("narrative-text", narrative.ToolRequest.ArgumentsJson);

            Assert.Equal(1, harness.SendGateCurrentCount);
        }
        finally
        {
            // Guaranteed release + join. The drain enters teardown mode first, so even if an
            // assertion failed while `first` was parked in the fake and `second` was still queued
            // on the send gate, `second` acquires the permit, passes straight through the fake and
            // is joined here — no producer task outlives this finally.
            await DrainProducersAsync(harness.Requests, first, second);
            await harness.DisposeAsync();
        }
    }

    /// <summary>
    /// Tool-vs-Complete: a real assignment body's terminal Complete write must park behind the gate
    /// while a tool write from the agent turn is parked in the fake, and the assignment's own Ready
    /// must only be written AFTER its Complete (Complete-before-own-Ready sequencing).
    /// <para>
    /// REMOVAL-PROOFNESS. The tool write is held parked until the Complete producer is OBSERVED
    /// parked at the send boundary (<see cref="SendHarness.WaitForSendGateWaitersAsync"/>). That
    /// observation is what the serialization boundary makes true and a bypassed gate cannot: with
    /// the gate removed, Complete writes immediately instead of enrolling as a waiter, no waiter
    /// ever appears, and this test fails on the arrival barrier. Releasing the tool write before
    /// obtaining that evidence would let the bypassed build pass.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ToolVsComplete_BodyCompletionWaitsForGate_ReadyOnlyAfterComplete()
    {
        var runner = new OneShotProgressRunner();
        var harness = new SendHarness();
        harness.ReplaceRunner(runner);

        // The PROVISIONED body path: a real WorkerConfigProvisioner (in-memory env) plus a fake
        // git launcher reporting a HEALTHY repo, so the real assignment body runs the real
        // config-repo preparation and then ExecuteAndReportAsync's terminal Complete. The repo
        // directory is the HARNESS's own temporary one, so nothing depends on a writable
        // root-level /config-repo and nothing is left behind.
        var configRepoDir = harness.ConfigRepoDir;
        harness.UseProvisioner(new ProvisionerHarness(
            "https://github.com/org/config-repo.git", "ghp_test").Provisioner);
        var launcher = new FakeGitLauncher(tokens =>
        {
            if (TokenMatches(tokens, "rev-parse", "--is-inside-work-tree"))
                return new GitProcessResult(0, "true\n", "");
            if (TokenMatches(tokens, "rev-parse", "--show-toplevel"))
                return new GitProcessResult(0, configRepoDir + "\n", "");
            if (TokenMatches(tokens, "remote", "get-url", "origin"))
                return new GitProcessResult(0, "https://github.com/org/config-repo.git\n", "");
            return new GitProcessResult(0, "", "");
        });
        using var processRunner = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);
        using var loopCts = new CancellationTokenSource();

        Task loop = Task.CompletedTask;
        try
        {
            loop = harness.InvokeProcessMessages(loopCts.Token);
            harness.Responses.Push(SendHarness.Assignment("task-a", "model-a"));

            // The runner's fire-and-forget progress write enters the fake and parks, HOLDING the
            // gate's single permit for the whole interleaving below.
            await harness.Requests.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerMessage.PayloadOneofCase.ToolRequest, harness.Requests.EnteredWrites[0].PayloadCase);
            Assert.Equal("task-a", harness.Requests.EnteredWrites[0].ToolRequest.TaskId);
            Assert.Equal(1, harness.Requests.EnteredWriteCount);
            Assert.Equal(0, harness.SendGateCurrentCount); // the parked tool write owns the permit

            // The runner turn returns immediately (the progress send is fire-and-forget), so the
            // body proceeds to its terminal Complete WHILE the tool write is still parked.
            await runner.WaitForTurnCompletedAsync(TestContext.Current.CancellationToken);

            // THE ARRIVAL BARRIER. Block until the Complete producer is provably PARKED at the
            // send boundary. Only then is the tool write released — so the ordering below is
            // caused by the gate, not by test scheduling.
            await harness.WaitForSendGateWaitersAsync(1, TestContext.Current.CancellationToken);

            // While Complete waits, it has written NOTHING: still exactly the one tool write.
            Assert.Equal(1, harness.Requests.EnteredWriteCount);
            Assert.Equal(1, harness.Requests.ParkedWriteCount);
            Assert.Equal(0, harness.Requests.CompletedWriteCount);
            Assert.False(harness.Requests.OverlapDetected);

            harness.Requests.ReleaseCurrentWrite();
            await runner.ProgressSend.WaitAsync(TestContext.Current.CancellationToken);

            // The body's terminal Complete is the write that enters next — it was parked behind
            // the gate the tool write held, and never overlapped it.
            await harness.Requests.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
            var complete = harness.Requests.EnteredWrites[1];
            Assert.Equal(WorkerMessage.PayloadOneofCase.Complete, complete.PayloadCase);
            Assert.Equal("worker-send-ser", complete.WorkerId);
            Assert.Equal("task-a", complete.Complete.TaskId);
            Assert.Contains("done-from-runner", complete.Complete.Output);
            Assert.Equal(1, harness.Requests.CompletedWriteCount); // only the tool write so far.

            harness.Requests.ReleaseCurrentWrite(); // Complete returns; body claims Ready next.

            // Complete-before-own-Ready: the Ready write enters strictly after the Complete write.
            await harness.Requests.WaitForWriteEnteredAsync(2, TestContext.Current.CancellationToken);
            var ready = harness.Requests.EnteredWrites[2];
            Assert.Equal(WorkerMessage.PayloadOneofCase.Ready, ready.PayloadCase);
            Assert.Equal("worker-send-ser", ready.WorkerId);

            harness.Requests.ReleaseCurrentWrite(); // Ready returns; all three writes completed.

            harness.Responses.Push(null);
            await loop.WaitAsync(TestContext.Current.CancellationToken);

            Assert.False(harness.Requests.OverlapDetected);
            Assert.Equal(3, harness.Requests.EnteredWriteCount);
            Assert.Equal(3, harness.Requests.CompletedWriteCount);
            Assert.Equal(1, harness.SendGateCurrentCount);
        }
        finally
        {
            // Cancel the loop, then join it and the fire-and-forget progress producer. Nothing
            // here rethrows: a drain fault must never mask an assertion failure from the body.
            //
            // `runner.ProgressSend` is a STABLE task created with the runner, so it is a valid
            // producer handle no matter where the assignment thread was preempted — including the
            // window between the fake signalling write-entry and the bridge call returning, which
            // a plain `ProgressSend = …` field assignment could not cover. If the turn never ran
            // at all, nothing would ever complete that handle, so settle it explicitly first.
            loopCts.Cancel();
            runner.CompleteProgressSendIfNotStarted();
            await DrainProducersAsync(harness.Requests, loop, runner.ProgressSend);
            await harness.DisposeAsync();
        }
    }

    /// <summary>
    /// Tool-vs-Ready: a Ready produced by the real message loop (cancel with nothing in flight)
    /// must park behind the gate while a tool write is parked, entering only after it completes.
    /// <para>
    /// REMOVAL-PROOFNESS: as in the Complete case, the parked tool write is held until the loop's
    /// Ready producer is OBSERVED waiting at the send boundary. A bypassed gate never enrols that
    /// waiter — it writes straight through — so the arrival barrier fails the test instead of the
    /// early release hiding the overlap.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ToolVsReady_LoopReadyWaitsForGatedToolWrite_NoOverlap()
    {
        var harness = new SendHarness();
        using var loopCts = new CancellationTokenSource();

        Task loop = Task.CompletedTask;
        Task? tool = null;
        try
        {
            loop = harness.InvokeProcessMessages(loopCts.Token);

            tool = harness.Service.ReportProgressAsync(
                "task-r", "running", "before-ready", CancellationToken.None);
            await harness.Requests.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(1, harness.Requests.EnteredWriteCount);
            Assert.Equal(0, harness.SendGateCurrentCount); // the parked tool write owns the permit

            // Idle cancel: the loop's Ready producer must park on the send gate (held by the
            // parked tool write) instead of writing.
            harness.Responses.Push(new OrchestratorMessage
            {
                Cancel = new CancelTask { TaskId = "task-none", Reason = "idle" },
            });

            // THE ARRIVAL BARRIER — the Ready producer is provably parked AT the boundary before
            // anything is released. With the gate bypassed no waiter ever appears and this throws.
            await harness.WaitForSendGateWaitersAsync(1, TestContext.Current.CancellationToken);

            // The waiting Ready has written nothing: still exactly the one parked tool write.
            Assert.Equal(1, harness.Requests.EnteredWriteCount);
            Assert.Equal(1, harness.Requests.ParkedWriteCount);
            Assert.Equal(0, harness.Requests.CompletedWriteCount);
            Assert.False(harness.Requests.OverlapDetected);

            harness.Requests.ReleaseCurrentWrite();
            await tool.WaitAsync(TestContext.Current.CancellationToken);

            await harness.Requests.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
            var ready = harness.Requests.EnteredWrites[1];
            Assert.Equal(WorkerMessage.PayloadOneofCase.Ready, ready.PayloadCase);
            Assert.Equal("worker-send-ser", ready.WorkerId);

            harness.Requests.ReleaseCurrentWrite();
            harness.Responses.Push(new OrchestratorMessage
            {
                ToolResponse = new ToolCallResponse { RequestId = "probe", Success = true, ResultJson = "{}" },
            });
            await harness.Responses.Consumed(2).WaitAsync(TestContext.Current.CancellationToken);

            harness.Responses.Push(null);
            await loop.WaitAsync(TestContext.Current.CancellationToken);

            Assert.False(harness.Requests.OverlapDetected);
            Assert.Equal(2, harness.Requests.EnteredWriteCount);
            Assert.Equal(2, harness.Requests.CompletedWriteCount);
            Assert.Equal(1, harness.SendGateCurrentCount);
        }
        finally
        {
            loopCts.Cancel();
            await DrainProducersAsync(harness.Requests, loop, tool);
            await harness.DisposeAsync();
        }
    }

    /// <summary>
    /// The response-reading loop must process a ToolResponse (resolving a pending clarification)
    /// while an UNRELATED outbound write is still gated in the fake — the reader never touches the
    /// send gate.
    /// <para>
    /// The clarification's OWN request write is completed first, so the call is genuinely parked on
    /// its pending response rather than on its own write (a response cannot causally precede the
    /// request write that carries its request ID). Only then is a separate send parked, and the
    /// response must still be read and the clarification still complete while that send is held.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResponseReader_ProcessesToolResponse_WhileOutboundWriteGated()
    {
        var harness = new SendHarness();
        using var loopCts = new CancellationTokenSource();

        Task loop = Task.CompletedTask;
        Task<string>? clarification = null;
        Task? unrelated = null;

        // Owned by the finally below. The clarification parks on a pending ToolCallResponse TCS
        // that ONLY a matching response or this token can resolve — cancelling the reader loop
        // cannot. Without it, a setup/assertion failure before STEP 3 would leave the bridge call
        // pending forever and the drain could not terminate.
        var clarificationCts = new CancellationTokenSource();
        try
        {
            loop = harness.InvokeProcessMessages(loopCts.Token);

            // STEP 1 — the clarification's own request write ENTERS and is RELEASED, so the call
            // is genuinely awaiting its ToolCallResponse and nothing else.
            clarification = harness.Service.RequestClarificationAsync(
                "task-x", "what next?", clarificationCts.Token);
            await harness.Requests.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);
            var requestId = harness.Requests.EnteredWrites[0].ToolRequest.RequestId;
            Assert.NotEmpty(requestId);
            harness.Requests.ReleaseCurrentWrite();

            // STEP 2 — a SEPARATE, unrelated send now parks in the fake and holds the gate. Its
            // entry is also the barrier proving the clarification's write already completed and
            // returned its permit.
            unrelated = harness.Service.ReportProgressAsync(
                "task-x", "running", "unrelated-hold", CancellationToken.None);
            await harness.Requests.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal("report_progress", harness.Requests.EnteredWrites[1].ToolRequest.ToolName);
            Assert.Equal(1, harness.Requests.CompletedWriteCount); // the clarification's write
            Assert.Equal(1, harness.Requests.ParkedWriteCount);    // the unrelated send
            Assert.Equal(0, harness.SendGateCurrentCount);         // gate held by the unrelated send
            Assert.False(clarification.IsCompleted);               // awaiting its response only

            // STEP 3 — the matching response arrives while the UNRELATED send stays parked. The
            // reader must consume it and resolve the pending call without touching the send gate.
            harness.Responses.Push(new OrchestratorMessage
            {
                ToolResponse = new ToolCallResponse
                {
                    RequestId = requestId,
                    ResultJson = "{\"answer\":\"reader-independent\"}",
                    Success = true,
                },
            });

            var result = await clarification.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Contains("reader-independent", result);

            // A follow-up message proves the loop ran PAST the response while the write is gated.
            harness.Responses.Push(new OrchestratorMessage
            {
                ToolResponse = new ToolCallResponse { RequestId = "untracked-probe", Success = true, ResultJson = "{}" },
            });
            await harness.Responses.Consumed(2).WaitAsync(TestContext.Current.CancellationToken);

            // The unrelated send is STILL parked: the reader made progress independently of it.
            Assert.Equal(2, harness.Requests.EnteredWriteCount);
            Assert.Equal(1, harness.Requests.ParkedWriteCount);
            Assert.Equal(1, harness.Requests.CompletedWriteCount);
            Assert.False(unrelated.IsCompleted);
            Assert.Equal(0, harness.SendGateCurrentCount);

            harness.Requests.ReleaseCurrentWrite();
            await unrelated.WaitAsync(TestContext.Current.CancellationToken);

            harness.Responses.Push(null);
            await loop.WaitAsync(TestContext.Current.CancellationToken);

            Assert.False(harness.Requests.OverlapDetected);
            Assert.Equal(2, harness.Requests.CompletedWriteCount);
            Assert.Equal(1, harness.SendGateCurrentCount);
        }
        finally
        {
            // Guaranteed termination for the pending bridge call: cancelling the reader loop can
            // never resolve its ToolCallResponse TCS, so this token is the only thing that can
            // unblock it if the test failed before STEP 3 delivered the response.
            clarificationCts.Cancel();
            loopCts.Cancel();
            await DrainProducersAsync(harness.Requests, loop, clarification, unrelated);
            clarificationCts.Dispose();
            await harness.DisposeAsync();
        }
    }

    /// <summary>
    /// A sender cancelled WHILE WAITING for the gate must not write, must not release a permit it
    /// never acquired (gate count stays 0 while the first write is parked), and a later send must
    /// remain usable. A pre-cancelled sender likewise writes nothing.
    /// </summary>
    [Fact]
    public async Task CancelledWaiter_DoesNotWrite_DoesNotLeakPermit()
    {
        var harness = new SendHarness();
        Task? first = null;
        Task? waiter = null;
        Task? followup = null;

        // Owned by the finally below, NOT by a `using`: if the arrival-barrier assertion fails
        // before the explicit Cancel(), this token is what stops `waiter` from waiting on the send
        // gate forever, so the drain can actually terminate.
        var waiterCts = new CancellationTokenSource();
        try
        {
            first = harness.Service.ReportProgressAsync(
                "task-c", "running", "holder", CancellationToken.None);
            await harness.Requests.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);

            waiter = harness.Service.ReportNarrativeAsync("task-c", "n", waiterCts.Token);

            // The waiter is provably PARKED at the boundary before it is cancelled, so this
            // exercises cancellation of a real gate waiter rather than of a not-yet-started send.
            await harness.WaitForSendGateWaitersAsync(1, TestContext.Current.CancellationToken);
            waiterCts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, harness.Requests.EnteredWriteCount); // the waiter never wrote
            Assert.False(harness.Requests.OverlapDetected);
            Assert.Equal(0, harness.SendGateCurrentCount); // no permit leaked by the cancelled waiter
            Assert.Equal(0, harness.SendGateWaiterCount);  // and it left the queue

            // Pre-cancelled caller: throws before acquiring the gate or writing.
            using var precancelled = new CancellationTokenSource();
            await precancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Service
                .ReportProgressAsync("task-c", "running", "never", precancelled.Token)
                .WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, harness.Requests.EnteredWriteCount);
            Assert.Equal(0, harness.SendGateCurrentCount);
            Assert.Equal(0, harness.SendGateWaiterCount);

            harness.Requests.ReleaseCurrentWrite();
            await first.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, harness.SendGateCurrentCount); // exactly one permit back

            // The gate is fully usable: a follow-up send enters and completes normally.
            followup = harness.Service.ReportNarrativeAsync("task-c", "after", CancellationToken.None);
            await harness.Requests.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
            harness.Requests.ReleaseCurrentWrite();
            await followup.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, harness.Requests.EnteredWriteCount);
            Assert.Equal(2, harness.Requests.CompletedWriteCount);
            Assert.False(harness.Requests.OverlapDetected);
        }
        finally
        {
            // Guaranteed termination for the cancelled waiter: if an assertion failed before the
            // explicit Cancel() above, this is what releases it from the send gate. Cancelling an
            // already-cancelled source is a no-op, so this is safe on the success path too.
            waiterCts.Cancel();

            // Guaranteed release + join, so an early assertion failure cannot leave the holder
            // write parked or any of the three send tasks running.
            await DrainProducersAsync(harness.Requests, first, waiter, followup);
            waiterCts.Dispose();
            await harness.DisposeAsync();
        }
    }

    /// <summary>
    /// A write cancelled WHILE IN FLIGHT (parked inside the underlying writer) must unwind with
    /// OperationCanceledException, release its permit, and leave the gate usable — a sender that
    /// was already waiting for the gate must proceed, not be cancelled by the in-flight failure.
    /// </summary>
    [Fact]
    public async Task CancelledInFlightWrite_ReleasesPermit_WaitingSenderProceeds()
    {
        var harness = new SendHarness();
        Task? inflight = null;
        Task? waiting = null;
        try
        {
            using var inflightCts = new CancellationTokenSource();
            inflight = harness.Service.ReportProgressAsync(
                "task-f", "running", "inflight", inflightCts.Token);
            await harness.Requests.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);

            // A second sender parks on the gate BEFORE the in-flight write is cancelled — proved
            // by the arrival barrier, not merely by having been started.
            waiting = harness.Service.ReportNarrativeAsync("task-f", "waiting", CancellationToken.None);
            await harness.WaitForSendGateWaitersAsync(1, TestContext.Current.CancellationToken);

            inflightCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inflight.WaitAsync(TestContext.Current.CancellationToken));

            // The permit was released in finally: the waiting sender must now enter and complete.
            await harness.Requests.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
            Assert.False(harness.Requests.OverlapDetected);
            harness.Requests.ReleaseCurrentWrite();
            await waiting.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, harness.Requests.EnteredWriteCount);
            Assert.Equal(1, harness.Requests.CompletedWriteCount); // only the survivor completed
            Assert.Equal("report_narrative", harness.Requests.EnteredWrites[1].ToolRequest.ToolName);
            Assert.Equal(1, harness.SendGateCurrentCount);
        }
        finally
        {
            await DrainProducersAsync(harness.Requests, inflight, waiting);
            await harness.DisposeAsync();
        }
    }

    /// <summary>
    /// A FAILED write must propagate its exception to the caller, release the permit in finally,
    /// lose no message and add none, and leave the gate fully usable for the next send.
    /// </summary>
    [Fact]
    public async Task FailedWrite_PropagatesReleasesPermit_NextSendUsable()
    {
        var harness = new SendHarness();
        Task? nextSend = null;
        try
        {
            harness.Requests.FailNextWrite = new InvalidOperationException("simulated gRPC write failure");

            var failed = harness.Service.ReportProgressAsync(
                "task-e", "running", "doomed", CancellationToken.None);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Contains("simulated gRPC write failure", ex.Message);

            Assert.Equal(1, harness.Requests.EnteredWriteCount);
            Assert.Equal(0, harness.Requests.CompletedWriteCount);
            Assert.False(harness.Requests.OverlapDetected);
            Assert.Equal(1, harness.SendGateCurrentCount); // permit released despite the failure

            var next = harness.Service.ReportNarrativeAsync("task-e", "recovered", CancellationToken.None);
            nextSend = next;
            await harness.Requests.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
            harness.Requests.ReleaseCurrentWrite();
            await next.WaitAsync(TestContext.Current.CancellationToken);

            Assert.False(harness.Requests.OverlapDetected);
            Assert.Equal(2, harness.Requests.EnteredWriteCount);
            Assert.Equal(1, harness.Requests.CompletedWriteCount);
            Assert.Equal("report_progress", harness.Requests.EnteredWrites[0].ToolRequest.ToolName);
            Assert.Equal("report_narrative", harness.Requests.EnteredWrites[1].ToolRequest.ToolName);
            Assert.Equal(1, harness.SendGateCurrentCount);
        }
        finally
        {
            await DrainProducersAsync(harness.Requests, nextSend);
            await harness.DisposeAsync();
        }
    }

    /// <summary>
    /// Independent WorkerService instances must not block one another: while one instance's write
    /// is parked indefinitely, another instance's send must enter and complete on ITS OWN stream —
    /// proving a per-instance gate, not a static/global one.
    /// </summary>
    [Fact]
    public async Task IndependentServices_DoNotBlockEachOther()
    {
        var blocked = new SendHarness();
        var independent = new SendHarness(workerId: "worker-send-other");
        Task? holder = null;
        Task? other = null;
        try
        {
            holder = blocked.Service.ReportProgressAsync(
                "task-b", "running", "blocker", CancellationToken.None);
            await blocked.Requests.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);

            // The independent instance's send must complete while the first write stays parked.
            other = independent.Service.ReportProgressAsync(
                "task-i", "running", "independent", CancellationToken.None);
            await independent.Requests.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);
            independent.Requests.ReleaseCurrentWrite();
            await other.WaitAsync(TestContext.Current.CancellationToken);

            Assert.False(independent.Requests.OverlapDetected);
            Assert.Equal(1, blocked.Requests.EnteredWriteCount); // still parked, untouched
            Assert.Equal(1, blocked.Requests.ParkedWriteCount);
            Assert.Equal(0, blocked.SendGateCurrentCount);
            Assert.Equal(1, independent.SendGateCurrentCount);

            blocked.Requests.ReleaseCurrentWrite();
            await holder.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, blocked.SendGateCurrentCount);
        }
        finally
        {
            await DrainProducersAsync(blocked.Requests, holder);
            await DrainProducersAsync(independent.Requests, other);
            await blocked.DisposeAsync();
            await independent.DisposeAsync();
        }
    }

    private static bool TokenMatches(IReadOnlyList<string> tokens, params string[] prefix)
    {
        if (tokens.Count < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(tokens[i], prefix[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared fixture: a WorkerService with its private <c>_stream</c>/<c>_assignedId</c> wired to
    /// a gated fake request stream and a channel-backed response reader, plus reflection access to
    /// the send gate's current count (0 while a write is parked, 1 when the gate is free).
    /// </summary>
    private sealed class SendHarness : IDisposable
    {
        private readonly string _workerId;
        internal GatedOverlapDetectingRequestStream Requests { get; } = new();
        internal ChannelResponseReader Responses { get; } = new();
        internal WorkerService Service { get; }

        /// <summary>
        /// This harness's OWN temporary config-repo directory. Passed into WorkerService so no
        /// test depends on a writable root-level <c>/config-repo</c>, and deleted on teardown so
        /// nothing is leaked.
        /// </summary>
        internal string ConfigRepoDir { get; }

        internal SendHarness(string workerId = "worker-send-ser")
        {
            _workerId = workerId;
            ConfigRepoDir = Path.Combine(
                Path.GetTempPath(), "copilothive-send-ser-" + Guid.NewGuid().ToString("N"), "config-repo");
            Directory.CreateDirectory(ConfigRepoDir);

            Service = new WorkerService("http://localhost:9999", workerId, ["coder"], ConfigRepoDir);
            Connection = TestConnectionFactory.Attach(Service, workerId, CreateDuplex(), Service.TestProvisioner);
        }

        /// <summary>The connection the fixture published for the service to drive.</summary>
        internal WorkerConnection Connection { get; private set; }

        /// <summary>
        /// Re-publishes this harness's connection carrying <paramref name="provisioner"/>. Needed by
        /// the provisioned-body test, which must install its provisioner AFTER the harness has
        /// constructed the service.
        /// </summary>
        internal void UseProvisioner(WorkerConfigProvisioner? provisioner) =>
            Connection = TestConnectionFactory.Attach(Service, _workerId, CreateDuplex(), provisioner);

        /// <summary>The production send gate instance (never mutated — read for observation only).</summary>
        private SemaphoreSlim SendGate =>
            (SemaphoreSlim)typeof(WorkerService)
                .GetField("_sendGate", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(Service)!;

        /// <summary>Current count of the production send gate: 1 free, 0 while a write is gated.</summary>
        internal int SendGateCurrentCount => SendGate.CurrentCount;

        /// <summary>Senders currently PARKED at the send boundary awaiting a permit.</summary>
        internal int SendGateWaiterCount => SendGateObserver.CountWaiters(SendGate);

        /// <summary>
        /// Completes once <paramref name="count"/> senders are provably parked AT the send
        /// boundary. This is the arrival barrier the interleaving tests take BEFORE releasing the
        /// write that holds the gate: it proves the contender reached the boundary and waited
        /// there, which a bypassed gate can never satisfy.
        /// </summary>
        internal Task WaitForSendGateWaitersAsync(int count, CancellationToken ct) =>
            SendGateObserver.WaitForWaitersAsync(SendGate, count, ct);

        internal Task InvokeProcessMessages(CancellationToken ct)
        {
            var method = typeof(WorkerService).GetMethod(
                "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("WorkerService.ProcessMessagesAsync not found.");
            return (Task)method.Invoke(Service, [Connection, ct])!;
        }

        /// <summary>Replaces the default SharpCoderRunner with a test runner (disposes the default).</summary>
        internal void ReplaceRunner(IAgentRunner runner)
        {
            var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
            if (field.GetValue(Service) is IAgentRunner existing)
                existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
            field.SetValue(Service, runner);
        }

        private AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> CreateDuplex() =>
            new(Requests, Responses,
                _ => Task.FromResult(new Metadata()),
                _ => new Status(StatusCode.OK, string.Empty),
                _ => new Metadata(),
                _ => { },
                null!);

        /// <summary>
        /// Teardown: releases every parked write FIRST so any awaited send unwinds, then completes
        /// the reader, disposes the service and removes the temporary config-repo directory. Safe
        /// to call from a <c>finally</c> after an assertion failure — nothing here throws for an
        /// already-drained or already-deleted state, so the ORIGINAL failure is never masked.
        /// </summary>
        internal async Task DisposeAsync()
        {
            Requests.ReleaseAllParkedWrites();
            Responses.TryComplete();
            Service.Dispose();
            TryDeleteConfigRepoDir();
            await Task.CompletedTask;
        }

        private void TryDeleteConfigRepoDir()
        {
            try
            {
                var root = Path.GetDirectoryName(ConfigRepoDir);
                if (root is not null && Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose() => DisposeAsync().GetAwaiter().GetResult();

        internal static OrchestratorMessage Assignment(string taskId, string model) => new()
        {
            Assignment = new TaskAssignment
            {
                TaskId = taskId,
                GoalId = "goal-send-ser",
                GoalDescription = "Exercise the outbound send boundary",
                Prompt = $"run {taskId}",
                Role = GrpcWorkerRole.Coder,
                Model = model,
            },
        };
    }

    /// <summary>
    /// A runner that fires ONE fire-and-forget progress tool call (through the real bridge) and
    /// parks its turn until released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE PUBLICATION RACE THIS AVOIDS. A plain <c>ProgressSend = bridge.ReportProgressAsync(…)</c>
    /// publishes the task only AFTER the right-hand side returns, yet the fake stream signals
    /// <c>WaitForWriteEnteredAsync(0)</c> from INSIDE that call. The assignment thread can be
    /// preempted between the fake signalling entry and the field being written, letting the test
    /// resume, fail an assertion, and evaluate a still-<c>null</c> <c>ProgressSend</c> in its
    /// <c>finally</c> — permanently losing the producer, which could then outlive teardown.
    /// </para>
    /// <para>
    /// <see cref="ProgressSend"/> is therefore a STABLE task created in the field initialiser,
    /// before this runner is ever handed to the service. Its identity exists no matter where the
    /// assignment thread is preempted — and even if the assignment never runs at all — so a drain
    /// can always join it. The inner bridge call is attached to it by
    /// <see cref="SendPromptAsync"/>; if the turn never happens,
    /// <see cref="CompleteProgressSendIfNotStarted"/> settles it during teardown so the drain
    /// still converges.
    /// </para>
    /// </remarks>
    private sealed class OneShotProgressRunner : IAgentRunner
    {
        private readonly TaskCompletionSource<bool> _progressSendCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _turnCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _promptStarted;

        /// <summary>
        /// The STABLE producer handle for the fire-and-forget progress send. Non-null from
        /// construction, so a teardown drain can never capture a null identity because of where
        /// the assignment thread happened to be preempted. It completes when the underlying
        /// bridge send completes (or is settled by teardown if the turn never ran).
        /// </summary>
        internal Task ProgressSend => _progressSendCompleted.Task;

        /// <summary>
        /// Teardown safety valve: if the prompt never started, nothing will ever complete
        /// <see cref="ProgressSend"/>, so settle it here. A no-op once the turn has started —
        /// the real send's continuation owns the completion in that case.
        /// </summary>
        internal void CompleteProgressSendIfNotStarted()
        {
            if (Volatile.Read(ref _promptStarted) == 0)
                _progressSendCompleted.TrySetResult(true);
        }

        internal Task WaitForTurnCompletedAsync(CancellationToken ct) => _turnCompleted.Task.WaitAsync(ct);

        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var bridge = Bridge ?? throw new InvalidOperationException("Tool bridge was not set.");

            // Mark BEFORE the bridge call: the fake signals write-entry from inside it, so the
            // flag must already be visible to a teardown that races this line.
            Volatile.Write(ref _promptStarted, 1);

            try
            {
                var send = bridge.ReportProgressAsync("task-a", "running", "from runner", ct);

                // Mirror the real send's terminal state onto the stable handle. The continuation
                // is attached to the task the bridge returned, so no assignment to a shared field
                // is needed and there is no window in which the producer is unobservable.
                _ = send.ContinueWith(
                    _ => _progressSendCompleted.TrySetResult(true),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception)
            {
                // A synchronous throw would leave the stable handle with nothing to complete it,
                // and the teardown drain intentionally has no bail-out timeout — so settle it here
                // rather than risk an unjoinable producer. The turn itself still reports normally;
                // the body's assertions remain the sole source of failure.
                _progressSendCompleted.TrySetResult(true);
            }

            _turnCompleted.TrySetResult(true);
            return Task.FromResult("done-from-runner");
        }

        internal IToolCallBridge? Bridge { private get; set; }

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport { get; } = new()
        {
            TaskVerdict = TaskVerdict.Pass,
            Summary = "send-serialization runner",
            Issues = [],
        };
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) => Bridge = bridge;
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(CopilotHive.Workers.WorkerRole role, string agentsMdContent) { }
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
}
