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
    // (5) The bridge parameterized over ALL THREE response-bearing methods: EOF.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// WHICH bridge method is exercised by the EOF theory. All three go through the ONE
    /// response-bearing helper, but each is a distinct public entry point, so each is driven
    /// separately against the real message loop.
    /// </summary>
    public enum BridgeToolCase
    {
        Clarification,
        GetGoal,
        RaiseIssue,
    }

    /// <summary>
    /// PARAMETERIZED OVER THE THREE REAL BRIDGE METHODS: the request write succeeds with a
    /// STILL-LIVE caller token (one write, one registration), then the response loop's EOF ends
    /// the wait with the EXISTING disconnected error and there is NO resend — the remote outcome
    /// of the lost response stays unknown.
    /// <para>
    /// REMOVAL-PROOFNESS: with a service-global registry (or a wait bound to the loop token) the
    /// EOF would leave the call parked forever and the bounded join fails by name; with an
    /// automatic resend a second write would appear and the write-count assertion fails by name.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(BridgeToolCase.Clarification)]
    [InlineData(BridgeToolCase.GetGoal)]
    [InlineData(BridgeToolCase.RaiseIssue)]
    public async Task BridgeCall_EofAfterSuccessfulRequest_FailsDisconnected_WithNoResend(
        BridgeToolCase toolCase)
    {
        using var service = NewService();
        var requests = new RecordingToolRequestStream();
        var responses = new ChannelResponseReader();
        var connection = Publish(service, requests, responses);

        var loop = InvokeLoop(service, connection, TestContext.Current.CancellationToken);
        try
        {
            using var callerCts = new CancellationTokenSource();
            var call = StartBridgeCall(service, toolCase, callerCts.Token);

            var written = await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerId, written.WorkerId);
            Assert.False(
                callerCts.IsCancellationRequested,
                "The caller token must still be live when the request write succeeds.");
            Assert.Equal(1, connection.PendingToolResponseCount);
            Assert.False(call.IsCompleted);

            // EOF: no response can ever arrive. The loop's teardown ends the wait owned by THIS
            // connection, independently of the caller's still-live token.
            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);

            // NO resend: exactly one request write ever happened, and the settled entry is gone.
            Assert.Equal(1, requests.WriteCount);
            Assert.Equal(0, connection.PendingToolResponseCount);
            Assert.True(connection.IsRetired);
        }
        finally
        {
            responses.TryComplete();
            await ObserveAsync(loop);
        }
    }

    /// <summary>
    /// READER-FAULT VARIANT: the loop's ORIGINAL reader exception is preserved (surfaced from the
    /// loop join by identity), while the parked bridge wait still ends with the EXISTING
    /// disconnected error — never the reader fault — and there is no resend.
    /// </summary>
    [Fact]
    public async Task ReaderFault_BridgeWaitFailsDisconnected_AndLoopPreservesTheOriginalReaderException()
    {
        var original = new InvalidOperationException("reader fault");
        using var service = NewService();
        var requests = new RecordingToolRequestStream();
        var responses = new FaultingResponseReader();
        var connection = Publish(service, requests, responses);

        var loop = InvokeLoop(service, connection, TestContext.Current.CancellationToken);
        try
        {
            var call = service.GetGoalAsync("task-rf", "goal-rf", TestContext.Current.CancellationToken);
            await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(1, connection.PendingToolResponseCount);

            // The reader faults on its next MoveNext: the loop unwinds, ends the response waits,
            // and propagates the ORIGINAL exception.
            responses.ArmFault(original);

            // The bridge wait ends with the EXISTING disconnected error — the reader fault is the
            // LOOP's outcome, not the caller's.
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);

            // The loop's ORIGINAL reader exception is preserved by IDENTITY.
            var propagated = await Assert.ThrowsAsync<InvalidOperationException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(original, propagated);

            Assert.Equal(1, requests.WriteCount); // no resend
            Assert.Equal(0, connection.PendingToolResponseCount);
            Assert.True(connection.IsRetired);
        }
        finally
        {
            responses.TryComplete();
            await ObserveAsync(loop);
        }
    }

    /// <summary>
    /// The REAL <see cref="WorkerService.RunAsync"/> production teardown seams (fake invoker +
    /// fake work stream): a bridge wait parked on the published connection is ended by the
    /// response loop's EOF through the FULL lifecycle — registration, EOF, loop finally,
    /// retirement, unpublish — with no resend and the existing disconnected error.
    /// </summary>
    [Fact]
    public async Task RunAsync_EofDuringBridgeWait_BridgeFailsDisconnected_ThroughProductionTeardown()
    {
        var runner = new BridgeWaitingRunner();
        using var service = NewService(runner);
        runner.Service = service;

        var requests = new RecordingToolRequestStream();
        var responses = new ChannelResponseReader();
        var stream = Stream(requests, responses);

        service.CallInvokerFactory = () => new RegisterAcceptedInvoker();
        service.WorkStreamFactory = (_, _) => stream;

        var run = service.RunAsync(TestContext.Current.CancellationToken);
        try
        {
            // BARRIER: the initial Ready proves publication through the real lifecycle.
            await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken);
            var connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            Assert.False(connection.IsRetired);

            var call = service.GetGoalAsync("task-run", "goal-run", TestContext.Current.CancellationToken);
            var written = await requests.WaitForWriteAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(connection.AssignedId, written.WorkerId);
            Assert.Equal(1, connection.PendingToolResponseCount);

            // EOF ends the loop; the lifecycle's finally retires and unpublishes.
            responses.TryComplete();
            await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);

            // Full teardown happened, and the lost response was NOT retried: the request write
            // count never moved past the initial Ready + one tool request.
            Assert.Equal(2, requests.WriteCount);
            Assert.Equal(0, connection.PendingToolResponseCount);
            Assert.True(connection.IsRetired);
            Assert.Null(GetPublishedConnection(service));
        }
        finally
        {
            responses.TryComplete();
            await ObserveAsync(run);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (6) End-first is final, and ownership is per connection (same worker ID).
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// END-FIRST: once <c>EndToolResponses</c> has run, the bridge wait is already failed — a
    /// response arriving late for the SAME request ID cannot resurrect it, no resend happens, and
    /// registration stays closed for the rest of this connection's life.
    /// </summary>
    [Fact]
    public async Task EndFirst_BridgeWaitCannotBeResurrectedByALateResponse()
    {
        using var service = NewService();
        var requests = new RecordingToolRequestStream();
        var connection = Publish(service, requests);

        var call = service.RequestClarificationAsync("task-end", "why?", CancellationToken.None);
        var requestId = (await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken))
            .ToolRequest.RequestId;
        Assert.Equal(1, connection.PendingToolResponseCount);

        // END FIRST: the response lifetime closes and the wait fails disconnected.
        connection.EndToolResponses();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);

        // A late response for the SAME request ID has nothing left to settle — it cannot
        // resurrect the failed wait, and it is not held as history either.
        Assert.False(connection.TryCompleteToolResponse(
            new ToolCallResponse { RequestId = requestId, Success = true, ResultJson = "{}" }));
        Assert.Equal(0, connection.PendingToolResponseCount);
        Assert.Equal(1, requests.WriteCount); // no resend

        // Registration stays CLOSED on this connection after the end.
        var rejected = Assert.Throws<InvalidOperationException>(
            () => { _ = connection.RegisterToolResponse("req-late"); });
        Assert.Equal(WorkerConnection.DisconnectedMessage, rejected.Message);
    }

    /// <summary>
    /// CONTROLLED A/B CONNECTION OWNERSHIP with the SAME worker ID on both sides: a response
    /// dispatched to A and A's <c>EndToolResponses</c> can neither settle nor remove B's pending
    /// request, and B's lifetime stays open while A's is closed. This is OWNERSHIP testing — the
    /// connections are never driven concurrently through a reconnect, and no concurrent-reconnect
    /// support is claimed.
    /// <para>
    /// REMOVAL-PROOFNESS: with a service-global or worker-ID-keyed registry, A's completion/end
    /// would find and settle B's entry, so the "still pending" assertions fail by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AbConnections_SameWorkerId_AResponseAndEndCannotSettleOrRemoveBsRequest()
    {
        using var service = NewService();
        var requestsA = new RecordingToolRequestStream();
        var connectionA = Publish(service, requestsA);

        var requestsB = new RecordingToolRequestStream();
        var connectionB = BuildConnection(requestsB, null);

        // The SAME worker ID on both sides is exactly what a worker-ID-keyed registry would
        // confuse; only per-connection ownership can keep the two lifetimes apart.
        Assert.Equal(connectionA.AssignedId, connectionB.AssignedId);

        // A owns a REAL bridge wait; B owns its own registration for a different request.
        var callA = service.GetGoalAsync("task-ab", "goal-ab", TestContext.Current.CancellationToken);
        var idA = (await requestsA.WaitForWriteAsync(0, TestContext.Current.CancellationToken))
            .ToolRequest.RequestId;
        Assert.Equal(1, connectionA.PendingToolResponseCount);

        var waitB = connectionB.RegisterToolResponse("req-B");
        Assert.Equal(1, connectionB.PendingToolResponseCount);

        // A's connection cannot settle B's wait, even for B's own request ID.
        Assert.False(connectionA.TryCompleteToolResponse(
            new ToolCallResponse { RequestId = "req-B", Success = true, ResultJson = "{}" }));
        Assert.Equal(1, connectionB.PendingToolResponseCount);
        Assert.False(waitB.IsCompleted);
        Assert.False(callA.IsCompleted);

        // A's END cannot close B's lifetime, clear its entries, or remove its wait.
        connectionA.EndToolResponses();
        var failureA = await Assert.ThrowsAsync<InvalidOperationException>(
            () => callA.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        Assert.Equal(WorkerConnection.DisconnectedMessage, failureA.Message);
        Assert.Equal(0, connectionA.PendingToolResponseCount);

        // B's wait is untouched, and B's registration is STILL OPEN.
        Assert.Equal(1, connectionB.PendingToolResponseCount);
        _ = connectionB.RegisterToolResponse("req-B2");
        Assert.Equal(2, connectionB.PendingToolResponseCount);

        // B's own response settles its own wait through B alone — with exactly the payload B
        // received — while B's other registration stays pending.
        var payloadB = new ToolCallResponse
        {
            RequestId = "req-B",
            Success = true,
            ResultJson = "{\"from\":\"B\"}",
        };
        Assert.True(connectionB.TryCompleteToolResponse(payloadB));
        Assert.Same(payloadB, await waitB);
        Assert.Equal(1, connectionB.PendingToolResponseCount);

        // B's stream never saw a write: nothing about A's traffic reached it.
        Assert.Equal(0, requestsB.WriteCount);
        Assert.Equal(1, requestsA.WriteCount); // A's single request, no resend
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (7) A real assignment on the real bridge with CancellationToken.None.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A REAL ASSIGNMENT's body waits on the REAL bridge with <see cref="CancellationToken.None"/>
    /// — no assignment token bound to the wait — and EOF releases that wait INDEPENDENTLY, while
    /// the runner's unwind gate proves the loop STILL waits for the assignment body (the ownership
    /// slot stays occupied and the connection is not retired) before clearing it and retiring.
    /// Single-final-Ready is preserved: the drained body's own claim emits exactly one Ready,
    /// written while the connection was still usable.
    /// <para>
    /// REMOVAL-PROOFNESS: without ending the response waits BEFORE the drain, the None-bound wait
    /// is unreachable by both the response reader (EOF) and any token, so the drain never finishes
    /// and the bounded loop join fails by name; without drain-before-retire, the retired
    /// connection rejects the body's Ready and the Ready-count assertion fails by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AssignmentBridgeWait_OnIndependentToken_IsReleasedByEof_WhileUnwindGateHoldsTheDrain()
    {
        var runner = new BridgeThenUnwindRunner();
        using var service = NewService(runner);
        runner.Service = service;

        var requests = new RecordingToolRequestStream();
        var responses = new ChannelResponseReader();
        var connection = Publish(service, requests, responses);

        var loop = InvokeLoop(service, connection, TestContext.Current.CancellationToken);
        try
        {
            responses.Push(Assignment("task-unwind"));
            await runner.PromptStarted("task-unwind");
            await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken);

            // The bridge wait is parked on the connection, bound to CancellationToken.None —
            // only the connection's response closure can end it.
            Assert.Equal(1, connection.PendingToolResponseCount);
            Assert.False(loop.IsCompleted);

            // EOF: no response can ever arrive.
            responses.TryComplete();

            // The wait was released INDEPENDENTLY of the assignment token, by the loop's teardown
            // ending the response waits FIRST.
            await runner.BridgeFailed("task-unwind");
            Assert.Equal(WorkerConnection.DisconnectedMessage, runner.ObservedBridgeFailure!.Message);
            Assert.Equal(0, connection.PendingToolResponseCount);

            // The body observed the assignment cancellation and is now HELD in its unwind gate:
            // the loop's drain must still wait for the body before clearing the slot or retiring.
            await runner.CancelObserved("task-unwind");
            Assert.False(loop.IsCompleted, "The loop must not finish while the body is still unwinding.");
            Assert.Equal(1, GetSlotOccupancy(service));
            Assert.False(connection.IsRetired);

            // Release the unwind: the drain completes, the slot clears, the connection retires.
            runner.ReleaseUnwind();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.True(connection.IsRetired);

            // SINGLE FINAL READY: exactly the drained body's own claim — no duplicate from the
            // teardown, and it really was written (the connection was still usable at that point).
            Assert.Equal(1, requests.ReadyCount);
            // NO resend of the tool request: exactly one request write ever happened.
            Assert.Equal(1, requests.ToolRequestCount);
        }
        finally
        {
            runner.ReleaseAll();
            responses.TryComplete();
            await ObserveAsync(loop);
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

        /// <summary>How many of the recorded writes were <c>WorkerReady</c> messages.</summary>
        internal int ReadyCount { get { lock (_gate) return _writes.Count(w => w.PayloadCase == WorkerMessage.PayloadOneofCase.Ready); } }

        /// <summary>How many of the recorded writes were tool-call requests.</summary>
        internal int ToolRequestCount { get { lock (_gate) return _writes.Count(w => w.PayloadCase == WorkerMessage.PayloadOneofCase.ToolRequest); } }

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

    /// <summary>
    /// Starts the given bridge method with the given token — the THREE response-bearing public
    /// entry points, each driven by name so the theory covers every one of them.
    /// </summary>
    private static Task<string> StartBridgeCall(WorkerService service, BridgeToolCase toolCase, CancellationToken ct) =>
        toolCase switch
        {
            BridgeToolCase.Clarification =>
                service.RequestClarificationAsync($"task-{toolCase}", "why?", ct),
            BridgeToolCase.GetGoal =>
                service.GetGoalAsync($"task-{toolCase}", "goal-1", ct),
            BridgeToolCase.RaiseIssue =>
                service.RaiseIssueAsync($"task-{toolCase}", "bug", "title", "desc", "low", ct),
            _ => throw new ArgumentOutOfRangeException(nameof(toolCase), toolCase, "Unknown bridge case."),
        };

    /// <summary>A minimal <see cref="CallInvoker"/> that accepts every registration.</summary>
    private sealed class RegisterAcceptedInvoker : CallInvoker
    {
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (method.FullName != "/copilothive.HiveOrchestrator/Register")
                throw new NotSupportedException($"Unexpected unary call {method.FullName}.");

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)(object)new RegisterResponse
                {
                    Accepted = true,
                    AssignedWorkerId = WorkerId,
                    OrchestratorVersion = "test",
                }),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected server-streaming call {method.FullName}.");

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected client-streaming call {method.FullName}.");

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException(
                $"Unexpected duplex call {method.FullName} — the fixture supplies the stream explicitly.");
    }

    /// <summary>Reflects the real published-connection field (observation only).</summary>
    private static WorkerConnection? GetPublishedConnection(WorkerService service) =>
        (WorkerConnection?)typeof(WorkerService)
            .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

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
    /// A channel-backed reader that behaves like <see cref="ChannelResponseReader"/> until
    /// <see cref="FaultingResponseReader.ArmFault"/> is called, then throws the ORIGINAL exception
    /// from the next <c>MoveNext</c> — modelling a reader fault whose identity the loop must
    /// propagate. Local to these tests so the frozen shared doubles stay byte-for-byte unchanged.
    /// </summary>
    private sealed class FaultingResponseReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly System.Threading.Channels.Channel<OrchestratorMessage> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<OrchestratorMessage>();

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

    /// <summary>
    /// An <see cref="IAgentRunner"/> whose prompt body first parks on the REAL bridge with
    /// <see cref="CancellationToken.None"/> (an independent token — only the connection's response
    /// closure can end that wait), records the bridge failure, then — after OBSERVING the
    /// assignment cancellation — holds its unwind in a dedicated gate until released. This is the
    /// assignment-ownership unwind pattern, applied to a bridge-parked body.
    /// </summary>
    private sealed class BridgeThenUnwindRunner : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private readonly Dictionary<string, TaskCompletionSource> _finished = [];
        private readonly Dictionary<string, TaskCompletionSource> _bridgeFailed = [];
        private readonly Dictionary<string, TaskCompletionSource> _cancelObserved = [];
        private readonly TaskCompletionSource _unwindGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _taskId;

        internal WorkerService? Service { get; set; }

        /// <summary>The failure the parked bridge wait ended with, once it has ended.</summary>
        internal InvalidOperationException? ObservedBridgeFailure { get; private set; }

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

        /// <summary>Completes when the bridge wait for this task has FAILED (any outcome).</summary>
        public Task BridgeFailed(string taskId) => Slot(_bridgeFailed, taskId).Task;

        /// <summary>Releases the parked body gate for this task (teardown failsafe).</summary>
        public void Release(string taskId) => Slot(_release, taskId).TrySetResult();

        /// <summary>Completes when the body has OBSERVED its assignment token as cancelled.</summary>
        public Task CancelObserved(string taskId) => Slot(_cancelObserved, taskId).Task;

        /// <summary>Releases the held unwind so the drained body can finish.</summary>
        public void ReleaseUnwind() => _unwindGate.TrySetResult();

        /// <summary>Teardown failsafe: releases every gate a parked producer could hold.</summary>
        public void ReleaseAll()
        {
            ReleaseUnwind();
            lock (_gate)
            {
                foreach (var tcs in _started.Values) tcs.TrySetResult();
                foreach (var tcs in _release.Values) tcs.TrySetResult();
                foreach (var tcs in _bridgeFailed.Values) tcs.TrySetResult();
                foreach (var tcs in _cancelObserved.Values) tcs.TrySetResult();
            }
        }

        public void SetCurrentTaskId(string? taskId) => _taskId = taskId;

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var id = _taskId ?? "(unknown)";
            Slot(_started, id).TrySetResult();

            var bridge = (IToolCallBridge)(Service
                ?? throw new InvalidOperationException("The runner was not given the service under test."));

            // An INDEPENDENT live token: the assignment's cancellation must not end this wait
            // directly — only the connection's response closure can.
            var pending = bridge.GetGoalAsync(id, "goal-unwind", CancellationToken.None);
            try
            {
                _ = await pending;
            }
            catch (Exception ex)
            {
                ObservedBridgeFailure = ex as InvalidOperationException
                    ?? new InvalidOperationException("Unexpected bridge failure type.", ex);
                Slot(_bridgeFailed, id).TrySetResult();
            }

            // The bridge wait has ended (by the teardown's response closure). The body now parks
            // on its ASSIGNMENT-bound release gate, so the EOF teardown must CANCEL it — and the
            // unwind is then HELD until the test releases it, proving the drain waits for the
            // body before clearing the slot and retiring the connection.
            try
            {
                await Slot(_release, id).Task.WaitAsync(ct);
                return "unreachable";
            }
            catch (OperationCanceledException)
            {
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
            => await Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
