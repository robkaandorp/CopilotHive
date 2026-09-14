using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Threading.Channels;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// The bounded characterization of the PER-CONNECTION tool-response lifetime: a pending
/// response-bearing tool wait is OWNED by the <see cref="WorkerConnection"/> its request was written
/// on, registration/completion/removal/<c>EndToolResponses</c> are coordinated under ONE lock, and
/// <c>EndToolResponses</c> is one-way, idempotent and faults every unresolved wait with the EXISTING
/// disconnected category/message (never a fabricated negative response).
/// <para>
/// The bridge cases drive the REAL <c>WorkerService</c> bridge against a published connection, and
/// the loop cases drive the REAL private <c>ProcessMessagesAsync</c> through the existing
/// connection/stream seams (<see cref="TestConnectionFactory"/>, <see cref="ChannelResponseReader"/>,
/// <see cref="FakeClientStreamWriter{T}"/>, <see cref="GatedOverlapDetectingRequestStream"/>,
/// <see cref="SendGateObserver"/>) — no new broker or lifecycle harness is introduced. Every gate is
/// a TCS or a counted write: no sleeps, and every await is bounded by <see cref="Failsafe"/> purely
/// as a failure guard.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerConnectionToolCallLifetimeTests
{
    private const string WorkerId = "worker-lifetime";

    /// <summary>Generous failsafe bound; never an ordering device.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(15);

    /// <summary>A process-wide client channel: no RPC is ever issued through it in these tests.</summary>
    private static readonly GrpcChannel Channel = GrpcChannel.ForAddress("http://localhost:9999");

    // ══════════════════════════════════════════════════════════════════════════
    // (1) The per-connection registry itself.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A genuine response settles its wait and REMOVES the entry; an unknown or late request ID is
    /// IGNORED rather than creating, resurrecting or re-settling anything.
    /// </summary>
    [Fact]
    public async Task ResponseSettlesItsWaitAndRemovesTheEntry_UnknownAndLateResponsesAreIgnored()
    {
        var connection = NewConnection();

        var wait = connection.RegisterToolResponse("req-1");
        Assert.Equal(1, connection.PendingToolResponseCount);

        // UNKNOWN: a response for an ID this connection never registered settles nothing.
        Assert.False(connection.TryCompleteToolResponse(
            new ToolCallResponse { RequestId = "somebody-elses", Success = true }));
        Assert.Equal(1, connection.PendingToolResponseCount);
        Assert.False(wait.IsCompleted);

        // GENUINE: the matching response resolves the wait with exactly the payload received.
        var payload = new ToolCallResponse { RequestId = "req-1", Success = true, ResultJson = "{\"k\":1}" };
        Assert.True(connection.TryCompleteToolResponse(payload));
        Assert.Same(payload, await wait);

        // The settled entry is REMOVED, not retained as history — so a LATE duplicate is ignored.
        Assert.Equal(0, connection.PendingToolResponseCount);
        Assert.False(connection.TryCompleteToolResponse(payload));
    }

    /// <summary>
    /// <c>EndToolResponses</c> closes registration, clears every entry and FAULTS each unresolved
    /// wait with the EXISTING disconnected category/message — never a synthesized negative
    /// <see cref="ToolCallResponse"/>, which would imply the remote effect did not happen. It is
    /// idempotent.
    /// </summary>
    [Fact]
    public async Task EndToolResponses_FaultsUnresolvedWaitsWithDisconnectedCategory_AndIsIdempotent()
    {
        var connection = NewConnection();

        var first = connection.RegisterToolResponse("req-a");
        var second = connection.RegisterToolResponse("req-b");
        Assert.Equal(2, connection.PendingToolResponseCount);

        connection.EndToolResponses();

        foreach (var wait in new[] { first, second })
        {
            Assert.True(wait.IsFaulted);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
            Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);
        }

        // Cleared, closed and idempotent.
        Assert.Equal(0, connection.PendingToolResponseCount);
        connection.EndToolResponses();

        // Registration is now CLOSED for the rest of this connection's life. The rejection is
        // synchronous — nothing is registered and nothing is written.
        var rejected = Assert.Throws<InvalidOperationException>(
            () => { _ = connection.RegisterToolResponse("req-c"); });
        Assert.Equal(WorkerConnection.DisconnectedMessage, rejected.Message);
        Assert.Equal(0, connection.PendingToolResponseCount);
    }

    /// <summary>
    /// Retirement ends the response lifetime too, as an IDEMPOTENT fallback for a teardown that never
    /// entered the message loop — and a retired connection refuses new registrations with the same
    /// existing error.
    /// </summary>
    [Fact]
    public async Task Retire_AlsoEndsToolResponses_AndRefusesNewRegistrations()
    {
        var connection = NewConnection();
        var wait = connection.RegisterToolResponse("req-retire");

        connection.Retire();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);

        // Idempotent: a second retirement changes nothing.
        connection.Retire();
        Assert.Equal(0, connection.PendingToolResponseCount);

        // The same existing error, raised synchronously before anything is registered.
        var rejected = Assert.Throws<InvalidOperationException>(
            () => { _ = connection.RegisterToolResponse("req-after"); });
        Assert.Equal(WorkerConnection.DisconnectedMessage, rejected.Message);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (2) The real bridge: registration, completion, cancellation, send failure.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CALLER-CANCELLATION-FIRST: the caller's own token settles the wait, the request is not
    /// replayed, and a response arriving afterwards (the late loser of the race) is ignored.
    /// </summary>
    [Fact]
    public async Task CallerCancellation_SettlesTheWait_AndALateResponseIsIgnored()
    {
        using var service = NewService();
        var requests = new RecordingToolRequestStream();
        var connection = Publish(service, requests);

        using var callerCts = new CancellationTokenSource();
        var call = service.RequestClarificationAsync("task-c", "why?", callerCts.Token);

        var requestId = (await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken))
            .ToolRequest.RequestId;
        Assert.Equal(1, connection.PendingToolResponseCount);
        Assert.False(call.IsCompleted);

        await callerCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

        // The cancelled wait was removed, so the late response has nothing to settle.
        Assert.Equal(0, connection.PendingToolResponseCount);
        Assert.False(connection.TryCompleteToolResponse(
            new ToolCallResponse { RequestId = requestId, Success = true, ResultJson = "{}" }));
        Assert.Equal(1, requests.WriteCount);
    }

    /// <summary>
    /// RESPONSE-FIRST: a genuine server response resolves the bridge call and preserves the existing
    /// success/error string conversion exactly, and the settled entry is removed.
    /// </summary>
    [Fact]
    public async Task GenuineResponse_ResolvesTheBridgeCall_WithExistingStringConversion()
    {
        using var service = NewService();
        var requests = new RecordingToolRequestStream();
        var connection = Publish(service, requests);

        var successCall = service.GetGoalAsync("task-g", "goal-1", TestContext.Current.CancellationToken);
        var firstId = (await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken))
            .ToolRequest.RequestId;
        Assert.True(connection.TryCompleteToolResponse(new ToolCallResponse
        {
            RequestId = firstId,
            Success = true,
            ResultJson = "{\"goal\":\"payload\"}",
        }));
        Assert.Equal("{\"goal\":\"payload\"}", await successCall.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        Assert.Equal(0, connection.PendingToolResponseCount);

        // The ERROR conversion is unchanged for a genuine unsuccessful response.
        var errorCall = service.RaiseIssueAsync("task-g", "bug", "t", "d", "low", TestContext.Current.CancellationToken);
        var secondId = (await requests.WaitForWriteAsync(1, TestContext.Current.CancellationToken))
            .ToolRequest.RequestId;
        Assert.True(connection.TryCompleteToolResponse(new ToolCallResponse
        {
            RequestId = secondId,
            Success = false,
            Error = "orchestrator refused",
        }));
        Assert.Equal("Error: orchestrator refused", await errorCall.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        Assert.Equal(0, connection.PendingToolResponseCount);
    }

    /// <summary>
    /// SEND-FAILURE: the ORIGINAL exception propagates to the caller (not the pending task's fault,
    /// and not a synthesized result), the entry is removed, and there is NO automatic resend — the
    /// remote outcome of a lost response stays unknown.
    /// </summary>
    [Fact]
    public async Task SendFailure_PropagatesOriginalException_RemovesEntry_AndNeverResends()
    {
        var gated = new GatedOverlapDetectingRequestStream();
        var injected = new InvalidOperationException("simulated gRPC write failure");
        gated.FailNextWrite = injected;

        using var service = NewService();
        var connection = Publish(service, gated);

        var call = service.RaiseIssueAsync("task-f", "bug", "t", "d", "low", CancellationToken.None);
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

        // The ORIGINAL exception identity, not a substitute.
        Assert.Same(injected, caught);
        Assert.Equal(0, connection.PendingToolResponseCount);
        Assert.Equal(1, gated.EnteredWriteCount); // exactly one attempt — no resend

        // The gate released its permit, so the connection stays usable for a later send.
        var after = service.ReportNarrativeAsync("task-f", "n", CancellationToken.None);
        await gated.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
        gated.ReleaseCurrentWrite();
        await after.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        Assert.Equal(2, gated.EnteredWriteCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (3) Send-gate interaction.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A RESPONSE-BEARING send that was queued behind the shared send gate checks the response
    /// lifetime AFTER acquiring the gate and BEFORE beginning its write: with the lifetime closed
    /// while it waited, it writes NOTHING and fails with the existing disconnected error. The extra
    /// check is restricted to response-bearing sends, so a fire-and-forget send still writes on the
    /// same connection, and the shared gate is neither bypassed nor abandoned.
    /// </summary>
    [Fact]
    public async Task QueuedResponseBearingSend_WithClosedResponseLifetime_WritesNothing_WhilePlainSendStillWrites()
    {
        var gated = new GatedOverlapDetectingRequestStream();
        using var service = NewService();
        var connection = Publish(service, gated);

        Task? holder = null;
        Task<string>? queued = null;
        Task? plain = null;
        try
        {
            // The holder takes the gate's only permit and parks inside the writer.
            holder = service.ReportProgressAsync("task-q", "running", "holder", CancellationToken.None);
            await gated.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);

            // The response-bearing call registers, then parks AT the send gate (it has written
            // nothing yet).
            queued = service.GetGoalAsync("task-q", "goal-1", CancellationToken.None);
            await WaitForSendGateWaitersAsync(service, 1, TestContext.Current.CancellationToken);
            Assert.Equal(1, gated.EnteredWriteCount);

            // The response lifetime closes while the queued request is still waiting for the gate.
            connection.EndToolResponses();

            gated.ReleaseCurrentWrite();
            await holder.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => queued.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);

            // The queued request never began its write: the holder's write is still the only one.
            Assert.Equal(1, gated.EnteredWriteCount);

            // ...and the restriction is to response-bearing sends: a plain send on the SAME
            // (unretired) connection still writes.
            plain = service.ReportNarrativeAsync("task-q", "plain", CancellationToken.None);
            await gated.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
            gated.ReleaseCurrentWrite();
            await plain.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, gated.EnteredWriteCount);
        }
        finally
        {
            gated.EnterTeardownMode();
            gated.ReleaseAllParkedWrites();
            await ObserveAsync(holder);
            await ObserveAsync(queued);
            await ObserveAsync(plain);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (4) The real message loop.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// LOOP-ORDERING: the loop's <c>finally</c> ends this connection's response waits BEFORE the
    /// assignment cancellation/drain, so a tool wait bound to an INDEPENDENT live token (never the
    /// assignment's) cannot hold the drain — and retirement — off. The loop therefore completes, the
    /// pending wait ends with the disconnected error, and the connection is retired.
    /// <para>
    /// REMOVAL-PROOFNESS: without the response closure (or with it moved after the drain) the bridge
    /// wait is unreachable by both the response reader (EOF) and the caller's token, so the drain
    /// never finishes and this test fails on its bounded loop join.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LoopFinally_EndsResponseWaitsBeforeTheDrain_SoAnIndependentTokenCannotBlockRetirement()
    {
        var runner = new BridgeWaitingRunner();
        using var service = NewService(runner);
        runner.Service = service;

        var requests = new RecordingToolRequestStream();
        var responses = new ChannelResponseReader();
        var connection = Publish(service, requests, responses);

        var loop = InvokeLoop(service, connection, TestContext.Current.CancellationToken);
        try
        {
            responses.Push(Assignment("task-bridge"));
            await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken);

            // The bridge wait is parked on the connection, bound to CancellationToken.None.
            Assert.Equal(1, connection.PendingToolResponseCount);
            Assert.False(loop.IsCompleted);

            // EOF: no response can ever arrive. The loop's own teardown must release the wait.
            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(0, connection.PendingToolResponseCount);
            Assert.True(connection.IsRetired);
            Assert.NotNull(runner.ObservedBridgeFailure);
            Assert.Equal(WorkerConnection.DisconnectedMessage, runner.ObservedBridgeFailure!.Message);
        }
        finally
        {
            responses.TryComplete();
            await ObserveAsync(loop);
        }
    }

    /// <summary>
    /// DISPATCH THROUGH THE LOOP'S OWN CONNECTION: a <c>ToolResponse</c> is dispatched through the
    /// connection supplied to <c>ProcessMessagesAsync</c>, never a service-global map. A response
    /// delivered to ANOTHER connection's loop does not resolve this connection's wait; the SAME
    /// response delivered through the owning connection's loop does.
    /// <para>
    /// REMOVAL-PROOFNESS: with a service-global request-ID map the foreign loop would find and settle
    /// the entry, so the "still pending" assertion fails by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ToolResponse_IsDispatchedThroughTheLoopConnection_NotAServiceGlobalMap()
    {
        using var service = NewService();

        var requestsA = new RecordingToolRequestStream();
        var responsesA = new ChannelResponseReader();

        // A is the PUBLISHED connection the bridge snapshots; B is a DIFFERENT connection with its
        // own response loop, built through the same seam but never published.
        var connectionA = Publish(service, requestsA, responsesA);
        var requestsB = new RecordingToolRequestStream();
        var responsesB = new ChannelResponseReader();
        var connectionB = BuildConnection(requestsB, responsesB);

        var loopA = InvokeLoop(service, connectionA, TestContext.Current.CancellationToken);
        var loopB = InvokeLoop(service, connectionB, TestContext.Current.CancellationToken);
        try
        {
            var call = service.RequestClarificationAsync("task-d", "why?", TestContext.Current.CancellationToken);
            var requestId = (await requestsA.WaitForWriteAsync(0, TestContext.Current.CancellationToken))
                .ToolRequest.RequestId;

            // The response arrives on the WRONG connection's loop: it must resolve nothing.
            responsesB.Push(ToolResponse(requestId, "from-b"));
            await responsesB.Consumed(1);
            Assert.Equal(1, connectionA.PendingToolResponseCount);
            Assert.Equal(0, connectionB.PendingToolResponseCount);
            Assert.False(call.IsCompleted);

            // The same response through the OWNING connection's loop resolves it.
            responsesA.Push(ToolResponse(requestId, "from-a"));
            await responsesA.Consumed(1);
            Assert.Contains("from-a", await call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(0, connectionA.PendingToolResponseCount);
        }
        finally
        {
            responsesA.TryComplete();
            responsesB.TryComplete();
            await ObserveAsync(loopA);
            await ObserveAsync(loopB);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Harness.
    // ══════════════════════════════════════════════════════════════════════════

    private static WorkerService NewService() =>
        new("http://localhost:9999", WorkerId, ["coder"], configRepoDir: CreateTempConfigRepoDir());

    /// <summary>Builds a service whose agent runner is the supplied fake (disposing the default).</summary>
    private static WorkerService NewService(IAgentRunner runner)
    {
        var service = NewService();

        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);

        return service;
    }

    private static string CreateTempConfigRepoDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tool-lifetime-{Guid.NewGuid():N}", "config-repo");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Builds a connection carrying NO provisioner, for the registry-only cases.</summary>
    private static WorkerConnection NewConnection() => BuildConnection(null, null);

    /// <summary>
    /// Builds (but does not publish) a connection over the given writer/reader, carrying NO
    /// provisioner so the legacy, seam-free executor branch applies.
    /// </summary>
    private static WorkerConnection BuildConnection(
        IClientStreamWriter<WorkerMessage>? requests, IAsyncStreamReader<OrchestratorMessage>? responses) =>
        new(WorkerId, new HiveOrchestrator.HiveOrchestratorClient(Channel), Stream(requests, responses),
            provisionerOverride: null, includeProductionProvisioner: false);

    /// <summary>Publishes a connection over the given writer/reader and returns it.</summary>
    private static WorkerConnection Publish(
        WorkerService service, IClientStreamWriter<WorkerMessage> requests,
        IAsyncStreamReader<OrchestratorMessage>? responses = null)
    {
        var connection = BuildConnection(requests, responses);
        service.PublishConnection(connection);
        return connection;
    }

    private static AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> Stream(
        IClientStreamWriter<WorkerMessage>? requests, IAsyncStreamReader<OrchestratorMessage>? responses) =>
        new(requests ?? new RecordingToolRequestStream(),
            responses ?? new ChannelResponseReader(),
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

    private static OrchestratorMessage Assignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-lifetime",
            GoalDescription = "exercise the tool-response lifetime",
            Prompt = "ask the orchestrator",
            Role = GrpcWorkerRole.Coder,
        },
    };

    private static OrchestratorMessage ToolResponse(string requestId, string resultJson) => new()
    {
        ToolResponse = new ToolCallResponse
        {
            RequestId = requestId,
            Success = true,
            ResultJson = $"{{\"answer\":\"{resultJson}\"}}",
        },
    };

    private static Task InvokeLoop(WorkerService service, WorkerConnection connection, CancellationToken ct) =>
        (Task)typeof(WorkerService)
            .GetMethod("ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [connection, ct])!;

    /// <summary>The production send gate (observation only — never mutated).</summary>
    private static Task WaitForSendGateWaitersAsync(WorkerService service, int count, CancellationToken ct)
    {
        var gate = (SemaphoreSlim)typeof(WorkerService)
            .GetField("_sendGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;
        return SendGateObserver.WaitForWaitersAsync(gate, count, ct);
    }

    /// <summary>Teardown join that never masks an assertion failure and never hangs the suite.</summary>
    private static async Task ObserveAsync(Task? producer)
    {
        if (producer is null) return;

        try
        {
            await producer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // Teardown only: the producer's real outcome was asserted in the try body.
        }
    }

    /// <summary>
    /// Records every request write and lets a test await a specific write deterministically. Uses the
    /// shared base's explicit cancellable-write implementation, since production writes with the live
    /// stream token.
    /// </summary>
    private sealed class RecordingToolRequestStream : FakeClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _writes = [];
        private readonly Dictionary<int, TaskCompletionSource<WorkerMessage>> _waiters = [];

        internal int WriteCount { get { lock (_gate) return _writes.Count; } }

        public override Task WriteAsync(WorkerMessage message)
        {
            Record(message);
            return Task.CompletedTask;
        }

        internal Task<WorkerMessage> WaitForWriteAsync(int index, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_writes.Count > index)
                    return Task.FromResult(_writes[index]);

                if (!_waiters.TryGetValue(index, out var waiter))
                {
                    waiter = new TaskCompletionSource<WorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[index] = waiter;
                }

                return waiter.Task.WaitAsync(Failsafe, ct);
            }
        }

        public override Task CompleteAsync() => Task.CompletedTask;

        private void Record(WorkerMessage message)
        {
            TaskCompletionSource<WorkerMessage>? waiter;
            lock (_gate)
            {
                var index = _writes.Count;
                _writes.Add(message);
                _waiters.TryGetValue(index, out waiter);
            }

            waiter?.TrySetResult(message);
        }
    }

    /// <summary>
    /// An <see cref="IAgentRunner"/> whose prompt parks inside the REAL bridge on a tool call bound to
    /// <see cref="CancellationToken.None"/> — deliberately NOT the assignment's token, so only the
    /// connection's response closure can end the wait.
    /// </summary>
    private sealed class BridgeWaitingRunner : IAgentRunner
    {
        private string? _taskId;

        internal WorkerService? Service { get; set; }

        /// <summary>The failure the parked bridge wait ended with, once it has ended.</summary>
        internal InvalidOperationException? ObservedBridgeFailure { get; private set; }

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var bridge = (IToolCallBridge)(Service
                ?? throw new InvalidOperationException("The runner was not given the service under test."));

            var pending = bridge.RequestClarificationAsync(_taskId ?? "(unknown)", "why?", CancellationToken.None);
            try
            {
                await pending;
            }
            catch (InvalidOperationException ex)
            {
                ObservedBridgeFailure = ex;
                throw;
            }

            return "unreachable";
        }

        public void SetCurrentTaskId(string? taskId) => _taskId = taskId;

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
}
