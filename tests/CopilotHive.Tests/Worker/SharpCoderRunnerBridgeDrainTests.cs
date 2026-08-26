using CopilotHive.Goals;
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
/// Production-path drain test: drives the REAL <see cref="SharpCoderRunner"/> (with its production
/// <c>BuildCustomTools(ct)</c> token-forwarding wiring) through the real
/// <c>WorkerService.ProcessMessagesAsync</c> loop, and proves that cancelling an assignment
/// whose turn is blocked inside a pending bridge call releases the drain — no deadlock.
/// <para>
/// The iteration-3 bug: <c>BuildCustomTools</c> passed <see cref="CancellationToken.None"/> to
/// every bridge call. <c>WorkerService</c>'s bridge arms each pending tool request with
/// <c>ct.Register(() =&gt; tcs.TrySetCanceled())</c>, so a <c>None</c>-bound wait could never be
/// released. Cancelling the assignment left <c>request_clarification</c> hanging, so the drain
/// in <c>ProcessMessagesAsync</c> blocked forever while the runner held the full-turn client
/// lease.
/// </para>
/// <para>
/// The iteration-3 test masked this by using a <c>ToolRoundTripRunner</c> fake that called the
/// bridge directly and forwarded its own <c>SendPromptAsync</c> token. This test uses the REAL
/// runner: it captures the <c>AgentOptions.CustomTools</c> produced by the production
/// <c>BuildCustomTools(ct)</c> path (via the <c>OnAgentOptionsCreated</c> seam) and invokes the
/// captured <c>request_clarification</c> tool from inside the real <c>CodingAgent</c> turn, so the
/// production <c>ct</c> is the ONLY thing that can carry the cancellation to the bridge.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class SharpCoderRunnerBridgeDrainTests
{
    /// <summary>
    /// Drives the real <c>ProcessMessagesAsync</c> loop with a real <see cref="SharpCoderRunner"/>.
    /// The runner's <c>SendPromptAsync</c> builds tools via the production
    /// <c>BuildCustomTools(ct)</c> and creates a <c>CodingAgent</c>. The
    /// <c>OnAgentOptionsCreated</c> seam captures the <c>AgentOptions.CustomTools</c> — the
    /// production tool set carrying the assignment token. A fake <c>IChatClient</c> that parks
    /// during streaming gives the test a window to invoke the captured
    /// <c>request_clarification</c> tool, which blocks on the real <c>WorkerService</c> bridge
    /// (whose pending TCS is armed on the forwarded <c>ct</c>). A <c>CancelTask</c> then
    /// cancels the assignment token; the bridge TCS releases; the drain completes.
/// </summary>
    [Fact]
    public async Task ProcessMessages_CancelWhileBridgePending_DrainCompletesViaRealTokenForwarding()
    {
        const string AssignedId = "worker-drain";

        var bridgeEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridgeReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolsCaptured = new TaskCompletionSource<IList<AITool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientStreaming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStreaming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new SharpCoderRunner("/config-repo");
        var workDir = CreateTempWorkDir();

        runner.ClientCreationSeam = _ => new ParkingChatClient(clientStreaming, releaseStreaming);
        // No bridge set here — TaskExecutor.ExecuteAsync sets the REAL WorkerService as the bridge.
        runner.SetCustomAgent(CopilotHive.Workers.WorkerRole.Coder, "coder");

        // Capture the tools built by the production BuildCustomTools(ct) path. The tool
        // delegates close over the assignment's ct — the production wiring under test.
        runner.OnAgentOptionsCreated = options =>
        {
            toolsCaptured.TrySetResult(options.CustomTools);
        };

        var service = new WorkerService("http://localhost:9999", AssignedId, ["coder"]);
        ReplaceRunner(service, runner);

        var steps = new[]
        {
            new StreamStep(new OrchestratorMessage
            {
                Assignment = new TaskAssignment
                {
                    TaskId = "task-drain",
                    GoalId = "goal-drain",
                    GoalDescription = "Drain test",
                    Prompt = "ask the orchestrator",
                    Role = GrpcWorkerRole.Coder,
                },
            }),
            new StreamStep(new OrchestratorMessage
            {
                Cancel = new CancelTask { TaskId = "task-drain", Reason = "operator cancel" },
            }),
            new StreamStep(null),
        };
        var responses = new GatedResponseStream(steps);
        var requests = new ReadyCountingRequestStream();
        using var stream = CreateDuplex(requests, responses);
        AttachToolStream(service, stream, AssignedId);

        Task processTask = Task.CompletedTask;
        ValueTask<object?>? toolTask = null;
        try
        {
            processTask = InvokeProcessMessages(service, stream, TestContext.Current.CancellationToken);

            // Deliver the assignment. The runner enters SendPromptAsync → AcquireClientLeaseAsync
            // → RunPromptTurnAsync → BuildCustomTools(ct) → CodingAgent → IChatClient streaming.
            await steps[0].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[0].Release.TrySetResult(true);

            // Wait for the production tools (built by BuildCustomTools(ct)) to be captured.
            var tools = await toolsCaptured.Task.WaitAsync(TestContext.Current.CancellationToken);

            // Wait for the IChatClient to start streaming (the CodingAgent turn is active and
            // the client lifecycle lease is held).
            await clientStreaming.Task.WaitAsync(TestContext.Current.CancellationToken);

            // Invoke the production request_clarification tool
            var clarificationTool = (AIFunction)tools.Single(t => t is AIFunction f && f.Name == "request_clarification");
            toolTask = clarificationTool.InvokeAsync(
                new AIFunctionArguments { ["question"] = "why?" },
                TestContext.Current.CancellationToken);

            // The tool called the real WorkerService bridge, which wrote a ToolRequest to the
            // gRPC stream and parked on its pending TCS (armed with the assignment token).
            var toolRequest = await requests.WaitForToolRequestAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal("task-drain", toolRequest.ToolRequest.TaskId);
            Assert.False(toolTask!.Value.IsCompleted, "The bridge call must still be pending.");
            Assert.False(processTask.IsCompleted, "ProcessMessagesAsync must still be running.");

            // Deliver the cancel. DrainAssignmentAsync(cancelFirst: true) cancels the assignment
            // token. BuildCustomTools(ct) forwarded that token to the bridge, so the bridge's
            // pending TCS is released via ct.Register. The tool throws, the turn unwinds, the
            // drain completes.
            await steps[1].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[1].Release.TrySetResult(true);

            // The tool call observed cancellation — the assignment token was forwarded by the
            // production BuildCustomTools(ct) wiring.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await toolTask!.Value);

            // The drain completed: the cancel handler emitted exactly one Ready.
            await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // Release the streaming client so the CodingAgent turn can unwind.
            releaseStreaming.TrySetResult(true);

            // Let the loop finish.
            await steps[2].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[2].Release.TrySetResult(true);
            await processTask;

            Assert.Equal(1, requests.ReadyCount);
        }
        finally
        {
            foreach (var step in steps)
                step.Release.TrySetResult(true);
            releaseStreaming.TrySetResult(true);
            try { await processTask; } catch (OperationCanceledException) { }
            service.Dispose();
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// A second scenario: the SAME runner is reused for a follow-up assignment after the drain
    /// completes, proving the runner is not left in a half-disposed state and can create a fresh
    /// client for the next task.
    /// </summary>
    [Fact]
    public async Task ProcessMessages_AfterDrainedBridgeCancel_NextAssignmentCreatesFreshClient()
    {
        const string AssignedId = "worker-drain-2";
        var workDir = CreateTempWorkDir();

        var clientFactoryCalls = 0;
        var firstToolsCaptured = new TaskCompletionSource<IList<AITool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstClientStreaming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReleaseStreaming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondClientCreated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReleaseStreaming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new SharpCoderRunner("/config-repo");
        runner.SetCustomAgent(CopilotHive.Workers.WorkerRole.Coder, "coder");
        runner.OnAgentOptionsCreated = options =>
        {
            if (Volatile.Read(ref clientFactoryCalls) == 1)
                firstToolsCaptured.TrySetResult(options.CustomTools);
        };

        runner.ClientCreationSeam = _ =>
        {
            var call = Interlocked.Increment(ref clientFactoryCalls);
            return call switch
            {
                1 => new ParkingChatClient(firstClientStreaming, firstReleaseStreaming),
                _ => new SignalingChatClient(secondClientCreated, secondReleaseStreaming),
            };
        };

        var service = new WorkerService("http://localhost:9999", AssignedId, ["coder"]);
        ReplaceRunner(service, runner);

        var steps = new[]
        {
            new StreamStep(new OrchestratorMessage
            {
                Assignment = new TaskAssignment
                {
                    TaskId = "task-1", GoalId = "goal-1", GoalDescription = "First task",
                    Prompt = "ask", Role = GrpcWorkerRole.Coder,
                },
            }),
            new StreamStep(new OrchestratorMessage
            {
                Cancel = new CancelTask { TaskId = "task-1", Reason = "cancel" },
            }),
            new StreamStep(new OrchestratorMessage
            {
                Assignment = new TaskAssignment
                {
                    TaskId = "task-2", GoalId = "goal-1", GoalDescription = "Second task",
                    Prompt = "just work", Role = GrpcWorkerRole.Coder,
                },
            }),
            new StreamStep(null),
        };
        var responses = new GatedResponseStream(steps);
        var requests = new ReadyCountingRequestStream();
        using var stream = CreateDuplex(requests, responses);
        AttachToolStream(service, stream, AssignedId);

        Task processTask = Task.CompletedTask;
        ValueTask<object?>? toolTask = null;
        try
        {
            processTask = InvokeProcessMessages(service, stream, TestContext.Current.CancellationToken);

            // Task 1: assignment → invoke production tool → bridge pending → cancel → drain.
            await steps[0].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[0].Release.TrySetResult(true);

            var tools = await firstToolsCaptured.Task.WaitAsync(TestContext.Current.CancellationToken);
            await firstClientStreaming.Task.WaitAsync(TestContext.Current.CancellationToken);

            var clarificationTool = (AIFunction)tools.Single(t => t is AIFunction f && f.Name == "request_clarification");
            toolTask = clarificationTool.InvokeAsync(
                new AIFunctionArguments { ["question"] = "why?" },
                TestContext.Current.CancellationToken);

            await requests.WaitForToolRequestAsync(0, TestContext.Current.CancellationToken);

            await steps[1].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[1].Release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await toolTask!.Value);
            await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);
            firstReleaseStreaming.TrySetResult(true);

            // Task 2: fresh assignment must create a fresh client.
            await steps[2].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[2].Release.TrySetResult(true);
            await secondClientCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
            secondReleaseStreaming.TrySetResult(true);
            await requests.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);

            await steps[3].MoveNextEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            steps[3].Release.TrySetResult(true);
            await processTask;

            Assert.Equal(2, requests.ReadyCount);
            Assert.Equal(2, clientFactoryCalls);
        }
        finally
        {
            foreach (var step in steps)
                step.Release.TrySetResult(true);
            firstReleaseStreaming.TrySetResult(true);
            secondReleaseStreaming.TrySetResult(true);
            try { await processTask; } catch (OperationCanceledException) { }
            service.Dispose();
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CreateTempWorkDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bridge-drain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void ReplaceRunner(WorkerService service, IAgentRunner runner)
    {
        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);
    }

    private static void AttachToolStream(
        WorkerService service,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId)
    {
        typeof(WorkerService).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, stream);
        typeof(WorkerService).GetField("_assignedId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, assignedId);
    }

    private static Task InvokeProcessMessages(
        WorkerService service,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        CancellationToken ct)
    {
        var method = typeof(WorkerService).GetMethod(
            "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, [stream, "worker-drain", ct])!;
    }

    private static AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> CreateDuplex(
        IClientStreamWriter<WorkerMessage> requests,
        IAsyncStreamReader<OrchestratorMessage> responses) =>
        new(requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class StreamStep(OrchestratorMessage? message)
    {
        internal OrchestratorMessage? Message { get; } = message;
        internal TaskCompletionSource<bool> MoveNextEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (next >= steps.Count) return false;
            var step = steps[next];
            step.MoveNextEntered.TrySetResult(true);
            await step.Release.Task.WaitAsync(cancellationToken);
            _current = step.Message;
            return _current is not null;
        }
    }

    private sealed class ReadyCountingRequestStream : IClientStreamWriter<WorkerMessage>
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

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(
            WorkerMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
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

        internal Task WaitForReadyCountAsync(int count, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_readyCount >= count) return Task.CompletedTask;
                if (!_readyWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _readyWaiters[count] = waiter;
                }
                return waiter.Task.WaitAsync(ct);
            }
        }

        public Task CompleteAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// An <c>IChatClient</c> that signals when streaming starts, then parks until released. This
    /// gives the test a window to invoke the captured production tool while the CodingAgent turn
    /// is active.
    /// </summary>
    private sealed class ParkingChatClient(
        TaskCompletionSource<bool> streamingStarted,
        TaskCompletionSource<bool> releaseStreaming) : IChatClient
    {
        public ChatClientMetadata Metadata => new("parking", null, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken ct = default) => StreamAsync(ct);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            streamingStarted.TrySetResult(true);
            await releaseStreaming.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")])
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// An <c>IChatClient</c> that signals creation, then completes after release.
    /// </summary>
    private sealed class SignalingChatClient(
        TaskCompletionSource<bool> created,
        TaskCompletionSource<bool> release) : IChatClient
    {
        public ChatClientMetadata Metadata => new("signaling", null, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken ct = default)
        {
            created.TrySetResult(true);
            return StreamAsync(release, ct);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            TaskCompletionSource<bool> release,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await release.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")])
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
