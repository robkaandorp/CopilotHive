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

        Task processTask = Task.CompletedTask;
        try
        {
            processTask = InvokeProcessMessages(service, stream, TestContext.Current.CancellationToken);
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
        AttachToolStream(service, stream, AssignedId);

        Task processTask = Task.CompletedTask;
        try
        {
            processTask = InvokeProcessMessages(service, stream, TestContext.Current.CancellationToken);

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

    private static void AttachToolStream(
        WorkerService service,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId)
    {
        var streamField = typeof(WorkerService).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._stream field not found.");
        var assignedIdField = typeof(WorkerService).GetField("_assignedId", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._assignedId field not found.");
        streamField.SetValue(service, stream);
        assignedIdField.SetValue(service, assignedId);
    }

    private static Task InvokeProcessMessages(
        WorkerService service,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        CancellationToken ct)
    {
        var method = typeof(WorkerService).GetMethod(
            "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService.ProcessMessagesAsync not found.");
        return (Task)method.Invoke(service, [stream, "worker-overlap", ct])!;
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

    private sealed class CapturingRequestStream : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource<bool>> _readyWaiters = [];
        private readonly List<WorkerMessage> _toolRequests = [];
        private readonly Dictionary<int, TaskCompletionSource<WorkerMessage>> _toolRequestWaiters = [];
        private int _readyCount;

        internal int ReadyCount { get { lock (_gate) return _readyCount; } }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message)
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

        public Task WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }

        public Task CompleteAsync() => Task.CompletedTask;
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
