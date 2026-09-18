using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;

using SharpCoder;

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
        try
        {
            var requestId = (await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken))
                .ToolRequest.RequestId;
            Assert.Equal(1, connection.PendingToolResponseCount);
            Assert.False(call.IsCompleted);

            await callerCts.CancelAsync();

            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(callerCts.Token, cancelled.CancellationToken);

            // The cancelled wait was removed, so the late response has nothing to settle.
            Assert.Equal(0, connection.PendingToolResponseCount);
            Assert.False(connection.TryCompleteToolResponse(
                new ToolCallResponse { RequestId = requestId, Success = true, ResultJson = "{}" }));
            Assert.Equal(1, requests.WriteCount);
        }
        finally
        {
            await callerCts.CancelAsync();
            connection.EndToolResponses();
            await JoinForCleanupAsync(call, nameof(call));
        }
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
        Task<string>? successCall = null;
        Task<string>? errorCall = null;
        try
        {
            successCall = service.GetGoalAsync("task-g", "goal-1", TestContext.Current.CancellationToken);
            var firstId = (await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken))
                .ToolRequest.RequestId;
            Assert.True(connection.TryCompleteToolResponse(new ToolCallResponse
            {
                RequestId = firstId,
                Success = true,
                ResultJson = "{\"goal\":\"payload\"}",
            }));
            Assert.Equal(
                "{\"goal\":\"payload\"}",
                await successCall.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(0, connection.PendingToolResponseCount);

            // The ERROR conversion is unchanged for a genuine unsuccessful response.
            errorCall = service.RaiseIssueAsync(
                "task-g", "bug", "t", "d", "low", TestContext.Current.CancellationToken);
            var secondId = (await requests.WaitForWriteAsync(1, TestContext.Current.CancellationToken))
                .ToolRequest.RequestId;
            Assert.True(connection.TryCompleteToolResponse(new ToolCallResponse
            {
                RequestId = secondId,
                Success = false,
                Error = "orchestrator refused",
            }));
            Assert.Equal(
                "Error: orchestrator refused",
                await errorCall.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(0, connection.PendingToolResponseCount);
        }
        finally
        {
            connection.EndToolResponses();
            await JoinAllForCleanupAsync(
                (successCall, nameof(successCall)), (errorCall, nameof(errorCall)));
        }
    }

    /// <summary>
    /// SEND-FAILURE: the ORIGINAL exception propagates to the caller (not the pending task's fault,
    /// and not a synthesized result), the entry is removed, and there is NO automatic resend — the
    /// remote outcome of a lost response stays unknown.
    /// </summary>
    [Fact]
    public async Task SendFailure_RacingResponseEnd_ObservesAbandonedFault_AndPropagatesOriginalException()
    {
        var requests = new ControlledFailingResponseRequestStream();
        var injected = new InvalidOperationException("simulated gRPC write failure");
        using var service = NewService();
        var connection = Publish(service, requests);

        var matchingUnobservedFaults = 0;
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Any(
                    ex => ex is InvalidOperationException
                        && ex.Message == WorkerConnection.DisconnectedMessage))
            {
                Interlocked.Increment(ref matchingUnobservedFaults);
            }
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        Task? plainAfterFailure = null;
        try
        {
            var abandonedResponse = await ExerciseSendFailureRaceAsync(
                service, connection, requests, injected);

            // The response Task captured before EndToolResponses is now unreachable. Collection is
            // a deterministic observation boundary: without ObserveAbandonedResponse its fault is
            // published through UnobservedTaskException; with the observer, collection is silent.
            CollectUntilDead(abandonedResponse);
            Assert.Equal(0, Volatile.Read(ref matchingUnobservedFaults));

            // The shared send gate released after failure, and non-response-bearing sends are not
            // blocked by the closed response lifetime. The producer is RETAINED (never awaited
            // inline) and observed through the explicit bound, so a broken/removed gate release
            // parks it forever and this fails by name instead of hanging before `finally`.
            plainAfterFailure = service.ReportNarrativeAsync("task-f", "n", CancellationToken.None);
            await AwaitProducerWithinBoundAsync(plainAfterFailure, nameof(plainAfterFailure));
            Assert.Equal(2, requests.WriteCount);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            requests.Fail(injected);
            connection.EndToolResponses();
            await JoinForCleanupAsync(plainAfterFailure, nameof(plainAfterFailure));
        }
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
            connection.EndToolResponses();
            await JoinAllForCleanupAsync(
                (holder, nameof(holder)), (queued, nameof(queued)), (plain, nameof(plain)));
        }
    }

    /// <summary>
    /// LINEARIZATION PROOF: the openness decision and the WRITE INITIATION are ONE atomic boundary.
    /// <para>
    /// The decisive observation is taken from INSIDE the actual write initiation — the fake request
    /// stream's <c>WriteAsync</c>, reached from <c>WriteResponseBearingAsync</c> — where
    /// <see cref="Monitor.IsEntered"/> must report the per-connection response lock as HELD. That
    /// binds the invariant to the write itself rather than only to the preceding seam, so a
    /// check-then-write implementation (<c>lock { check; hook(); }</c> followed by a
    /// <c>WriteAsync</c> issued after the lock is released) fails on EVERY schedule: its initiation
    /// never runs under the lock, no matter how the request and end threads interleave. The
    /// observation's failure is captured and surfaced through <c>starterFailure</c>, so it fails the
    /// test deterministically instead of being silently recorded.
    /// </para>
    /// <para>
    /// The blocked end-thread observation and the ordered event record are retained as supporting
    /// evidence: while the seam holds the boundary, a dedicated end thread is observed waiting on the
    /// same lock and <c>end-complete</c> can only follow <c>write-start</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResponseWriteBoundary_EndCannotCompleteBetweenOpenDecisionAndWriteInitiation()
    {
        var requests = new RecordingToolRequestStream();
        using var service = NewService();
        var connection = Publish(service, requests);

        // The PRIVATE per-connection response lock — the one boundary that production must hold for
        // BOTH the openness decision and the write initiation. Resolved before any producer starts,
        // so the write-initiation observation below can consult it.
        var responseLock = typeof(WorkerConnection)
            .GetField("_toolResponsesLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(connection)!;

        using var boundaryEntered = new ManualResetEventSlim();
        using var releaseBoundary = new ManualResetEventSlim();
        using var endStarted = new ManualResetEventSlim();
        using var endCompleted = new ManualResetEventSlim();
        var eventGate = new object();
        var events = new List<string>();
        void Record(string value) { lock (eventGate) events.Add(value); }

        // The captured-failure channel. Everything is published under `eventGate`, so the test
        // thread's reads are properly ordered no matter which thread made the observation.
        Exception? starterFailure = null;
        var writeInitiatedUnderResponseLock = false;
        void CaptureFailure(Exception ex) { lock (eventGate) starterFailure ??= ex; }

        connection.OnResponseBearingWriteInitiating = () =>
        {
            Record("open-observed");
            boundaryEntered.Set();
            releaseBoundary.Wait();
            Record("boundary-released");
        };

        // THE WRITE-INITIATION OBSERVATION. This runs inside the fake writer's WriteAsync, i.e. at
        // the very instant production initiates the transport write. Asserting the response lock is
        // HELD here is what a check-then-write implementation can never satisfy. The assertion's
        // failure is captured (not thrown into production's call path) and surfaced through
        // starterFailure, which the body asserts null.
        requests.OnWrite = _ =>
        {
            Record("write-start");
            try
            {
                Assert.True(
                    Monitor.IsEntered(responseLock),
                    "The response-bearing write was INITIATED without the response lock held — the "
                        + "openness check and the write initiation are not one atomic boundary, so "
                        + "EndToolResponses can win between them.");

                lock (eventGate) writeInitiatedUnderResponseLock = true;
            }
            catch (Exception ex)
            {
                CaptureFailure(ex);
            }
        };

        Task<string>? call = null;
        var requestThread = new Thread(() =>
        {
            try { call = service.GetGoalAsync("task-linear", "goal-linear", CancellationToken.None); }
            catch (Exception ex) { CaptureFailure(ex); }
        })
        { IsBackground = true, Name = "response-write-starter" };

        var endThread = new Thread(() =>
        {
            Record("end-attempt");
            endStarted.Set();
            connection.EndToolResponses();
            Record("end-complete");
            endCompleted.Set();
        })
        { IsBackground = true, Name = "response-lifetime-ender" };

        try
        {
            requestThread.Start();
            Assert.True(
                boundaryEntered.Wait(Failsafe, TestContext.Current.CancellationToken),
                "The response-bearing write never reached its boundary.");

            // The seam contract itself says this instant is INSIDE the response lock. Prove that
            // directly from this different thread; a seam moved outside the boundary fails here.
            var unexpectedlyAcquired = Monitor.TryEnter(responseLock);
            if (unexpectedlyAcquired) Monitor.Exit(responseLock);
            Assert.False(unexpectedlyAcquired, "The response-write seam was not holding the response lock.");

            endThread.Start();
            Assert.True(
                endStarted.Wait(Failsafe, TestContext.Current.CancellationToken),
                "The end thread never started.");

            // BLOCKED-THREAD OBSERVATION, not a delay: while the seam holds the response lock, the
            // end thread must be waiting to enter that same boundary and must not have completed.
            Assert.True(
                SpinWait.SpinUntil(
                    () => endCompleted.IsSet
                        || (endThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    Failsafe),
                "The end thread was never observed blocked at the response boundary.");
            Assert.False(endCompleted.IsSet, "EndToolResponses completed inside the open→write boundary.");

            releaseBoundary.Set();
            Assert.True(requestThread.Join(Failsafe), "The request-start thread did not finish after release.");
            Assert.True(endThread.Join(Failsafe), "The end thread did not finish after write initiation.");

            lock (eventGate)
            {
                // The write-initiation observation is the decisive evidence: it must have RUN (never
                // vacuous) and it must have found the response lock held.
                Assert.Null(starterFailure);
                Assert.True(
                    writeInitiatedUnderResponseLock,
                    "The write-initiation observation never ran, so the atomic-boundary proof would be vacuous.");
            }

            Assert.NotNull(call);

            var disconnected = await Assert.ThrowsAsync<InvalidOperationException>(
                () => call!.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, disconnected.Message);

            lock (eventGate)
            {
                Assert.Equal(
                    ["open-observed", "end-attempt", "boundary-released", "write-start", "end-complete"],
                    events);
            }
            Assert.Equal(1, requests.WriteCount);
        }
        finally
        {
            releaseBoundary.Set();
            connection.OnResponseBearingWriteInitiating = null;
            requests.OnWrite = null;
            connection.EndToolResponses();
            await JoinThreadsAndTasksForCleanupAsync(
                [(requestThread, nameof(requestThread)), (endThread, nameof(endThread))],
                [(call, nameof(call))]);
        }
    }

    /// <summary>
    /// EXPLICIT-CONNECTION ROUTE: A is published and a response-bearing call registers on A while
    /// parked behind the shared send gate; B (with the SAME worker ID) is then published before the
    /// request can send. Releasing the gate must write the request on A and leave its registration
    /// on A — never re-read B from <c>CurrentConnection</c>.
    /// </summary>
    [Fact]
    public async Task ResponseBearingCall_RepublishBetweenRegistrationAndSend_UsesCapturedConnectionA()
    {
        const string sharedWorkerId = "worker-shared-response";
        var requestsA = new GatedOverlapDetectingRequestStream();
        var requestsB = new RecordingToolRequestStream();
        using var service = NewService();
        var connectionA = BuildConnection(sharedWorkerId, requestsA, null);
        var connectionB = BuildConnection(sharedWorkerId, requestsB, null);
        service.PublishConnection(connectionA);

        Task? holder = null;
        Task<string>? call = null;
        try
        {
            holder = service.ReportProgressAsync("task-ab-send", "running", "holder", CancellationToken.None);
            await requestsA.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);

            call = service.GetGoalAsync("task-ab-send", "goal-ab", CancellationToken.None);
            await WaitForSendGateWaitersAsync(service, 1, TestContext.Current.CancellationToken);
            Assert.Equal(1, connectionA.PendingToolResponseCount);
            Assert.Equal(0, connectionB.PendingToolResponseCount);

            // B replaces A after A registered but before A can acquire the send gate.
            service.PublishConnection(connectionB);
            requestsA.ReleaseCurrentWrite();
            await holder.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            await requestsA.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
            var request = requestsA.EnteredWrites[1];
            Assert.Equal(WorkerMessage.PayloadOneofCase.ToolRequest, request.PayloadCase);
            Assert.Equal("get_goal", request.ToolRequest.ToolName);
            Assert.Empty(requestsB.Writes);
            Assert.Equal(1, connectionA.PendingToolResponseCount);
            Assert.Equal(0, connectionB.PendingToolResponseCount);

            requestsA.ReleaseCurrentWrite();
            Assert.True(connectionA.TryCompleteToolResponse(new ToolCallResponse
            {
                RequestId = request.ToolRequest.RequestId,
                Success = true,
                ResultJson = "{\"owner\":\"A\"}",
            }));
            Assert.Equal("{\"owner\":\"A\"}", await call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(0, connectionA.PendingToolResponseCount);
        }
        finally
        {
            connectionA.EndToolResponses();
            connectionB.EndToolResponses();
            requestsA.EnterTeardownMode();
            requestsA.ReleaseAllParkedWrites();
            await JoinAllForCleanupAsync((holder, nameof(holder)), (call, nameof(call)));
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
            connection.EndToolResponses();
            responses.TryComplete();
            await JoinForCleanupAsync(loop, nameof(loop));
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

        Task<string>? call = null;
        var loopA = InvokeLoop(service, connectionA, TestContext.Current.CancellationToken);
        var loopB = InvokeLoop(service, connectionB, TestContext.Current.CancellationToken);
        try
        {
            call = service.RequestClarificationAsync("task-d", "why?", TestContext.Current.CancellationToken);
            var requestId = (await requestsA.WaitForWriteAsync(0, TestContext.Current.CancellationToken))
                .ToolRequest.RequestId;

            // The wrong response is followed by a deterministic LOOP-EXECUTION BARRIER: an invalid
            // UpdateAgents message that throws only after the preceding ToolResponse switch has
            // executed. Reader dequeue alone is not enough because MoveNext signals before dispatch.
            responsesB.Push(ToolResponse(requestId, "from-b"));
            responsesB.Push(new OrchestratorMessage
            {
                UpdateAgents = new UpdateAgents { Role = "not-a-worker-role", AgentsMdContent = "barrier" },
            });
            var barrier = await Assert.ThrowsAsync<InvalidOperationException>(
                () => loopB.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Contains("Unknown role in UpdateAgents", barrier.Message, StringComparison.Ordinal);

            // Because B's ToolResponse switch definitely ran before the barrier fault, these
            // negatives deterministically kill a service-global response map.
            Assert.Equal(1, connectionA.PendingToolResponseCount);
            Assert.Equal(0, connectionB.PendingToolResponseCount);
            Assert.False(call.IsCompleted);

            // The same response through the OWNING connection's loop resolves it. Awaiting the
            // bridge call itself is the positive dispatch barrier for A.
            responsesA.Push(ToolResponse(requestId, "from-a"));
            Assert.Contains("from-a", await call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(0, connectionA.PendingToolResponseCount);
        }
        finally
        {
            connectionA.EndToolResponses();
            connectionB.EndToolResponses();
            responsesA.TryComplete();
            responsesB.TryComplete();
            await JoinAllForCleanupAsync(
                (call, nameof(call)), (loopA, nameof(loopA)), (loopB, nameof(loopB)));
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

        using var callerCts = new CancellationTokenSource();
        Task<string>? call = null;
        var loop = InvokeLoop(service, connection, TestContext.Current.CancellationToken);
        try
        {
            call = StartBridgeCall(service, toolCase, callerCts.Token);

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
            await callerCts.CancelAsync();
            connection.EndToolResponses();
            responses.TryComplete();
            await JoinAllForCleanupAsync((call, nameof(call)), (loop, nameof(loop)));
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

        using var bridgeCts = new CancellationTokenSource();
        Task<string>? call = null;
        var loop = InvokeLoop(service, connection, TestContext.Current.CancellationToken);
        try
        {
            call = service.GetGoalAsync("task-rf", "goal-rf", bridgeCts.Token);
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
            await bridgeCts.CancelAsync();
            connection.EndToolResponses();
            responses.TryComplete();
            await JoinAllForCleanupAsync((call, nameof(call)), (loop, nameof(loop)));
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

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var bridgeCts = new CancellationTokenSource();
        WorkerConnection? connection = null;
        Task<string>? call = null;
        var run = service.RunAsync(runCts.Token);
        try
        {
            // BARRIER: the initial Ready proves publication through the real lifecycle.
            await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken);
            connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            Assert.False(connection.IsRetired);

            call = service.GetGoalAsync("task-run", "goal-run", bridgeCts.Token);
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
            await bridgeCts.CancelAsync();
            await runCts.CancelAsync();
            connection?.EndToolResponses();
            responses.TryComplete();
            await JoinAllForCleanupAsync((call, nameof(call)), (run, nameof(run)));
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
        try
        {
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
        finally
        {
            connection.EndToolResponses();
            await JoinForCleanupAsync(call, nameof(call));
        }
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
        Task<string>? callA = null;
        Task<ToolCallResponse>? waitB = null;
        Task<ToolCallResponse>? waitB2 = null;
        try
        {
            // The SAME worker ID on both sides is exactly what a worker-ID-keyed registry would
            // confuse; only per-connection ownership can keep the two lifetimes apart.
            Assert.Equal(connectionA.AssignedId, connectionB.AssignedId);

            // A owns a REAL bridge wait; B owns its own registration for a different request.
            callA = service.GetGoalAsync("task-ab", "goal-ab", TestContext.Current.CancellationToken);
            _ = (await requestsA.WaitForWriteAsync(0, TestContext.Current.CancellationToken))
                .ToolRequest.RequestId;
            Assert.Equal(1, connectionA.PendingToolResponseCount);

            waitB = connectionB.RegisterToolResponse("req-B");
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
            waitB2 = connectionB.RegisterToolResponse("req-B2");
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
        finally
        {
            connectionA.EndToolResponses();
            connectionB.EndToolResponses();
            await JoinAllForCleanupAsync(
                (callA, nameof(callA)), (waitB, nameof(waitB)), (waitB2, nameof(waitB2)));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (7) Response-closure exclusions: Complete, Progress, Narrative, sessions.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Closing only the TOOL-RESPONSE lifetime must not suppress an assignment's Complete or Ready
    /// writes. Those messages do not await ToolResponse and remain valid until general retirement.
    /// </summary>
    [Fact]
    public async Task ClosedResponseLifetime_StillAllowsAssignmentCompleteAndReady()
    {
        var runner = new ImmediateRunner();
        using var service = NewService(runner);
        var requests = new RecordingToolRequestStream();
        var responses = new ChannelResponseReader();
        var connection = Publish(service, requests, responses);
        connection.EndToolResponses();

        var loop = InvokeLoop(service, connection, TestContext.Current.CancellationToken);
        try
        {
            responses.Push(Assignment("task-complete-after-close"));
            var complete = await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken);
            var ready = await requests.WaitForWriteAsync(1, TestContext.Current.CancellationToken);

            Assert.Equal(WorkerMessage.PayloadOneofCase.Complete, complete.PayloadCase);
            Assert.Equal("task-complete-after-close", complete.Complete.TaskId);
            Assert.Equal(WorkerMessage.PayloadOneofCase.Ready, ready.PayloadCase);
        }
        finally
        {
            connection.EndToolResponses();
            responses.TryComplete();
            await JoinForCleanupAsync(loop, nameof(loop));
        }
    }

    /// <summary>
    /// Progress and narrative are fire-and-forget sends, so a CLOSED response lifetime does not
    /// reject them. They still use the captured, otherwise-live connection and write exactly once.
    /// </summary>
    [Fact]
    public async Task ClosedResponseLifetime_StillAllowsProgressAndNarrativeWrites()
    {
        using var service = NewService();
        var requests = new RecordingToolRequestStream();
        var connection = Publish(service, requests);
        connection.EndToolResponses();

        await service.ReportProgressAsync("task-plain", "running", "details", TestContext.Current.CancellationToken);
        await service.ReportNarrativeAsync("task-plain", "narrative", TestContext.Current.CancellationToken);

        Assert.Equal(2, requests.WriteCount);
        Assert.Equal("report_progress", (await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken)).ToolRequest.ToolName);
        Assert.Equal("report_narrative", (await requests.WaitForWriteAsync(1, TestContext.Current.CancellationToken)).ToolRequest.ToolName);
    }

    /// <summary>
    /// Unary session RPCs are outside the duplex response-lifetime registry. Closing tool responses
    /// therefore leaves GetSession/SaveSession operational on the same unretired connection.
    /// </summary>
    [Fact]
    public async Task ClosedResponseLifetime_StillAllowsUnarySessionLoadAndSave()
    {
        var invoker = new SessionInvoker();
        using var service = NewService();
        var connection = BuildConnection(
            WorkerId, new RecordingToolRequestStream(), null, invoker);
        service.PublishConnection(connection);
        connection.EndToolResponses();

        var loaded = await service.GetSessionAsync("goal:coder", TestContext.Current.CancellationToken);
        await service.SaveSessionAsync("goal:coder", "{\"turn\":2}", TestContext.Current.CancellationToken);

        Assert.Equal("{\"turn\":1}", loaded);
        Assert.Equal(1, invoker.GetCalls);
        Assert.Equal(1, invoker.SaveCalls);
        Assert.Equal("goal:coder", invoker.LastGetSessionId);
        Assert.Equal("goal:coder", invoker.LastSaveSessionId);
        Assert.Equal("{\"turn\":2}", invoker.LastSavedJson);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (8) A real assignment on the real bridge with CancellationToken.None.
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
            await runner.PromptStarted("task-unwind")
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await requests.WaitForWriteAsync(0, TestContext.Current.CancellationToken);

            // The bridge wait is parked on the connection, bound to CancellationToken.None —
            // only the connection's response closure can end it.
            Assert.Equal(1, connection.PendingToolResponseCount);
            Assert.False(loop.IsCompleted);

            // EOF: no response can ever arrive.
            responses.TryComplete();

            // The wait was released INDEPENDENTLY of the assignment token, by the loop's teardown
            // ending the response waits FIRST.
            await runner.BridgeFailed("task-unwind")
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerConnection.DisconnectedMessage, runner.ObservedBridgeFailure!.Message);
            Assert.Equal(0, connection.PendingToolResponseCount);

            // The body observed the assignment cancellation and is now HELD in its unwind gate:
            // the loop's drain must still wait for the body before clearing the slot or retiring.
            await runner.CancelObserved("task-unwind")
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
            connection.EndToolResponses();
            runner.ReleaseAll();
            responses.TryComplete();
            await JoinForCleanupAsync(loop, nameof(loop));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (9) Connection-bound assignment dependencies: binding, sessions, retirement.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// BINDING, NOT AFTER-THE-FACT LOOKUP. The dependency the REAL assignment setup installs on the
    /// real <see cref="TaskExecutor"/> (captured through the runner's <c>SetToolBridge</c> seam) is
    /// exercised against ALL FIVE bridge operations after the service's publication moved to a
    /// SECOND connection BEFORE the first bridge operation started. Every call must travel over the
    /// CAPTURED connection A — exact writer, worker ID, task ID, arguments and response — and the B
    /// connection's writer must record ZERO writes.
    /// <para>
    /// REMOVAL-PROOFNESS: handing the service itself to the executor (the pre-adapter shape) makes
    /// every bridge call resolve the CURRENT published connection, so after the publication change
    /// each call would appear on B's writer and the exact-A-value and zero-B-traffic assertions
    /// fail by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AssignmentBridgeDependency_BoundToCapturedConnection_AllFiveOperationsUseAWithZeroBTraffic_AfterRepublishBeforeFirstOperation()
    {
        const string assignedIdA = "worker-binding-a";
        var requestsA = new RecordingToolRequestStream();
        var responsesA = new ChannelResponseReader();
        var requestsB = new RecordingToolRequestStream();
        var responsesB = new ChannelResponseReader();

        var runner = new BridgeCapturingRunner();

        using var service = NewService(runner);

        // A is the connection the assignment arrives on; B is never published to the loop.
        var connectionA = BuildConnection(
            assignedIdA, requestsA, responsesA, invoker: null);
        service.PublishConnection(connectionA);
        var connectionB = BuildConnection(
            "worker-binding-b", requestsB, responsesB, invoker: null);

        var loop = InvokeLoop(service, connectionA, TestContext.Current.CancellationToken);
        Task<IToolCallBridge>? captureWait = null;
        try
        {
            var assignment = Assignment("task-binding");
            responsesA.Push(assignment);

            // The executor's assignment setup calls SetToolBridge on the runner with THE adapter
            // instance the real TaskExecutor construction produced. This is the exact dependency
            // production installed — not a hand-built stand-in.
            captureWait = runner.Captured.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var bridge = await captureWait;

            // Wait for the prompt to start so the assignment body is genuinely parked on the
            // runner, then move the service's publication to B BEFORE any bridge operation runs.
            await runner.PromptStarted("task-binding")
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, requestsA.WriteCount);
            Assert.Equal(0, requestsB.WriteCount);
            service.PublishConnection(connectionB);

            // ── The FIVE bridge operations, through the CAPTURED dependency only. ──

            // 1. report_progress (fire-and-forget).
            await bridge.ReportProgressAsync("task-binding", "binding-status", "binding details",
                    TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var progress = await requestsA.WaitForWriteAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerMessage.PayloadOneofCase.ToolRequest, progress.PayloadCase);
            Assert.Equal("report_progress", progress.ToolRequest.ToolName);
            Assert.Equal("task-binding", progress.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, progress.WorkerId);
            Assert.Equal("""{"status":"binding-status","details":"binding details"}""",
                progress.ToolRequest.ArgumentsJson);

            // 2. report_narrative (fire-and-forget).
            await bridge.ReportNarrativeAsync("task-binding", "the narrative",
                    TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var narrative = await requestsA.WaitForWriteAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal("report_narrative", narrative.ToolRequest.ToolName);
            Assert.Equal("task-binding", narrative.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, narrative.WorkerId);
            Assert.Equal("""{"narrative":"the narrative"}""",
                narrative.ToolRequest.ArgumentsJson);

            // 3. request_clarification (response-bearing, resolved through A's own loop). The
            //    TASK ID is asserted EXACTLY, and so is the returned payload — a wrong task ID or
            //    an altered response payload fails here rather than passing a substring probe.
            var clarification = bridge.RequestClarificationAsync(
                "task-binding", "why?", TestContext.Current.CancellationToken);
            var clarificationWrite = await requestsA.WaitForWriteAsync(
                2, TestContext.Current.CancellationToken);
            Assert.Equal("request_clarification", clarificationWrite.ToolRequest.ToolName);
            Assert.Equal("task-binding", clarificationWrite.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, clarificationWrite.WorkerId);
            Assert.Equal("""{"question":"why?"}""",
                clarificationWrite.ToolRequest.ArgumentsJson);
            Assert.True(connectionA.TryCompleteToolResponse(new ToolCallResponse
            {
                RequestId = clarificationWrite.ToolRequest.RequestId,
                Success = true,
                ResultJson = """{"clarification":"from-A"}""",
            }));
            Assert.Equal(
                """{"clarification":"from-A"}""",
                await clarification.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // 4. get_goal (response-bearing).
            var goal = bridge.GetGoalAsync("task-binding", "goal-binding",
                TestContext.Current.CancellationToken);
            var goalWrite = await requestsA.WaitForWriteAsync(3, TestContext.Current.CancellationToken);
            Assert.Equal("get_goal", goalWrite.ToolRequest.ToolName);
            Assert.Equal("task-binding", goalWrite.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, goalWrite.WorkerId);
            Assert.Equal("""{"goal_id":"goal-binding"}""",
                goalWrite.ToolRequest.ArgumentsJson);
            Assert.True(connectionA.TryCompleteToolResponse(new ToolCallResponse
            {
                RequestId = goalWrite.ToolRequest.RequestId,
                Success = true,
                ResultJson = """{"goal":"A-goal"}""",
            }));
            Assert.Equal("""{"goal":"A-goal"}""",
                await goal.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // 5. raise_issue (response-bearing, error conversion unchanged).
            var issue = bridge.RaiseIssueAsync(
                "task-binding", "bug", "title", "desc", "high",
                TestContext.Current.CancellationToken);
            var issueWrite = await requestsA.WaitForWriteAsync(4, TestContext.Current.CancellationToken);
            Assert.Equal("raise_issue", issueWrite.ToolRequest.ToolName);
            Assert.Equal("task-binding", issueWrite.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, issueWrite.WorkerId);
            Assert.Equal(
                """{"type":"bug","title":"title","description":"desc","severity":"high"}""",
                issueWrite.ToolRequest.ArgumentsJson);
            Assert.True(connectionA.TryCompleteToolResponse(new ToolCallResponse
            {
                RequestId = issueWrite.ToolRequest.RequestId,
                Success = false,
                Error = "orchestrator refused",
            }));
            Assert.Equal(
                "Error: orchestrator refused",
                await issue.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // EXACTLY the five writes above reached A — no resend and no extra traffic.
            Assert.Equal(5, requestsA.WriteCount);

            // ZERO B traffic: the publication change and all five operations moved NOTHING to B.
            Assert.Equal(0, requestsB.WriteCount);
            Assert.Equal(0, connectionB.PendingToolResponseCount);
        }
        finally
        {
            connectionA.EndToolResponses();
            connectionB.EndToolResponses();
            runner.ReleaseAll();
            responsesA.TryComplete();
            responsesB.TryComplete();
            await JoinAllForCleanupAsync(
                (captureWait, nameof(captureWait)), (loop, nameof(loop)));
        }
    }

    /// <summary>
    /// SESSION LOAD/SAVE BINDING. The executor's REAL session load and save (driven through the
    /// real <see cref="TaskExecutor"/> with a session-carrying assignment) resolve the CAPTURED
    /// dependency's distinct A/B clients. The service's publication moves to B AFTER the load but
    /// BEFORE the save; NEITHER call may move to B: the load's session ID and the saved JSON reach
    /// exactly A's invoker, and B's invoker saw no session RPC.
    /// </summary>
    [Fact]
    public async Task AssignmentSessionLoadAndSave_BoundToCapturedConnection_StayOnAWhenPublicationMovesBetweenLoadAndSave()
    {
        const string sessionId = "goal-session:coder";
        var invokerA = new RecordingSessionInvoker();
        var invokerB = new RecordingSessionInvoker();

        // The EXACT payload A serves, and the EXACT payload the executor must save back: the
        // executor deserializes what it loaded and re-serializes the runner's retained session
        // with the same options, so the saved bytes are fully determined here.
        var loadedJson = JsonSerializer.Serialize(
            AgentSession.Create("binding-loaded"), AIJsonUtilities.DefaultOptions);
        var expectedSavedJson = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<AgentSession>(loadedJson, AIJsonUtilities.DefaultOptions),
            AIJsonUtilities.DefaultOptions);
        invokerA.SessionToReturn = new GetSessionResponse { Found = true, SessionJson = loadedJson };

        var runner = new BridgeCapturingRunner();
        using var service = NewService(runner);

        var requestsA = new RecordingToolRequestStream();
        var requestsB = new RecordingToolRequestStream();
        var responsesA = new ChannelResponseReader();
        var responsesB = new ChannelResponseReader();
        var connectionA = BuildConnection("worker-session-a", requestsA, responsesA, invokerA);
        var connectionB = BuildConnection("worker-session-b", requestsB, responsesB, invokerB);
        service.PublishConnection(connectionA);

        var loop = InvokeLoop(service, connectionA, TestContext.Current.CancellationToken);
        try
        {
            responsesA.Push(SessionAssignment("task-session", sessionId));

            // LOAD: the executor's own session load reached A's invoker with the exact ID.
            await invokerA.AwaitAsync("load", TestContext.Current.CancellationToken);
            Assert.Equal(1, invokerA.LoadCount);
            Assert.Equal(sessionId, invokerA.LastLoadSessionId);
            Assert.Equal(0, invokerB.LoadCount);

            // The prompt starts (the executor is genuinely mid-execution with the loaded session).
            await runner.PromptStarted("task-session")
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Publication moves to B while the executor's session SAVE has not happened yet —
            // strictly after the load, strictly before the save. Deterministic, no timing.
            service.PublishConnection(connectionB);

            // The prompt returns, so the executor performs its save — on the CAPTURED dependency.
            runner.ReleaseAll();
            await invokerA.AwaitAsync("save", TestContext.Current.CancellationToken);

            // The Complete write can only begin after ExecuteAsync returned, so all executor-owned
            // session saves have finished by this point; the save-count assertion below is final,
            // not an early snapshot taken while execution could still issue a duplicate.
            var complete = await requestsA.WaitForWriteAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerMessage.PayloadOneofCase.Complete, complete.PayloadCase);

            // A saw the exact session ID and the EXACT saved JSON — byte for byte, so a
            // malformed, truncated or extra-field payload fails here.
            Assert.Equal(sessionId, invokerA.LastSaveSessionId);
            Assert.Equal(expectedSavedJson, invokerA.LastSavedJson);

            // ...and the saved payload really is the session A served (not an empty/fresh one).
            var savedSession = JsonSerializer.Deserialize<AgentSession>(
                invokerA.LastSavedJson!, AIJsonUtilities.DefaultOptions);
            Assert.Equal("binding-loaded", savedSession!.SessionId);

            // EXACTLY ONE save on A — no duplicate, no retry, no second write of the session.
            Assert.Equal(1, invokerA.SaveCount);
            Assert.Equal(1, invokerA.LoadCount);

            // B saw NOTHING: neither the load nor the save moved to the newly published connection.
            Assert.Equal(0, invokerB.LoadCount);
            Assert.Equal(0, invokerB.SaveCount);
            Assert.Equal(0, requestsB.WriteCount);
        }
        finally
        {
            runner.ReleaseAll();
            responsesA.TryComplete();
            responsesB.TryComplete();
            await JoinAllForCleanupAsync((loop, nameof(loop)));
        }
    }

    /// <summary>
    /// RETIRED-A REJECTION, THROUGH THE REAL CAPTURED ADAPTER. A REAL assignment runs on A, its
    /// executor-installed dependency is captured through the runner's <c>SetToolBridge</c> seam,
    /// publication moves to B, and A is then RETIRED. Every category invoked through that retained
    /// A-bound dependency — response-bearing, fire-and-forget and unary session — fails with the
    /// EXISTING <see cref="WorkerConnection.DisconnectedMessage"/> and starts NO transport: A's
    /// writer never moved, A's invoker issued no RPC, and B saw nothing at all.
    /// <para>
    /// REMOVAL-PROOFNESS: the assertions are non-vacuous because the operations genuinely run
    /// through the production adapter. If the adapter fell back to the CURRENT published
    /// connection, the calls would succeed on B and the disconnected-error and zero-B assertions
    /// would fail by name; if the retirement check were dropped, A's writer/invoker counts would
    /// move instead of staying at zero.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RetiredAssignmentDependency_RejectsWithDisconnectedError_AndStartsNoTransport()
    {
        var invokerA = new RecordingSessionInvoker();
        var invokerB = new RecordingSessionInvoker();
        var requestsA = new RecordingToolRequestStream();
        var requestsB = new RecordingToolRequestStream();
        var responsesA = new ChannelResponseReader();
        var responsesB = new ChannelResponseReader();

        var runner = new BridgeCapturingRunner();
        using var service = NewService(runner);

        var connectionA = BuildConnection("worker-retired-a", requestsA, responsesA, invokerA);
        var connectionB = BuildConnection("worker-retired-b", requestsB, responsesB, invokerB);
        service.PublishConnection(connectionA);

        var loop = InvokeLoop(service, connectionA, TestContext.Current.CancellationToken);
        Task<IToolCallBridge>? captureWait = null;
        try
        {
            responsesA.Push(Assignment("task-retired"));

            // THE REAL executor-installed dependency for this assignment.
            captureWait = runner.Captured.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var bridge = await captureWait;
            var sessions = Assert.IsAssignableFrom<ISessionClient>(bridge);

            await runner.PromptStarted("task-retired")
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Publication moves to B, and A is RETIRED while the assignment still holds its
            // dependency. Both transitions happen before any operation is attempted.
            service.PublishConnection(connectionB);
            connectionA.Retire();

            // No setup/reporting write has happened: the transport baseline is EXACTLY zero,
            // not merely an arbitrary count that the rejected calls must leave unchanged.
            Assert.Equal(0, requestsA.WriteCount);

            // RESPONSE-BEARING through the retained A-bound adapter.
            await ExpectDisconnectedAsync(
                () => bridge.RequestClarificationAsync(
                    "task-retired", "why?", TestContext.Current.CancellationToken),
                "request_clarification");

            await ExpectDisconnectedAsync(
                () => bridge.GetGoalAsync(
                    "task-retired", "goal-retired", TestContext.Current.CancellationToken),
                "get_goal");

            await ExpectDisconnectedAsync(
                () => bridge.RaiseIssueAsync(
                    "task-retired", "bug", "t", "d", "low", TestContext.Current.CancellationToken),
                "raise_issue");

            // FIRE-AND-FORGET through the same retained adapter: rejected at the post-gate
            // retirement check, so nothing is written either.
            await ExpectDisconnectedAsync(
                () => bridge.ReportProgressAsync(
                    "task-retired", "running", "after retirement", TestContext.Current.CancellationToken),
                "report_progress");

            await ExpectDisconnectedAsync(
                () => bridge.ReportNarrativeAsync(
                    "task-retired", "after retirement", TestContext.Current.CancellationToken),
                "report_narrative");

            // UNARY SESSIONS through the same retained adapter: checked access rejects before
            // the RPC is issued.
            await ExpectDisconnectedAsync(
                () => sessions.GetSessionAsync("goal-retired:coder", TestContext.Current.CancellationToken),
                "GetSession");

            await ExpectDisconnectedAsync(
                () => sessions.SaveSessionAsync(
                    "goal-retired:coder", "{}", TestContext.Current.CancellationToken),
                "SaveSession");

            // NO TRANSPORT ANYWHERE. A's writer remains EXACTLY empty, A's invoker issued no
            // unary RPC, and B — the newly published connection — saw nothing.
            Assert.Equal(0, requestsA.WriteCount);
            Assert.Equal(0, invokerA.LoadCount);
            Assert.Equal(0, invokerA.SaveCount);
            Assert.Equal(0, requestsB.WriteCount);
            Assert.Equal(0, invokerB.LoadCount);
            Assert.Equal(0, invokerB.SaveCount);
            Assert.Equal(0, connectionB.PendingToolResponseCount);
            Assert.True(connectionA.IsRetired);
        }
        finally
        {
            runner.ReleaseAll();
            connectionA.EndToolResponses();
            connectionB.EndToolResponses();
            responsesA.TryComplete();
            responsesB.TryComplete();
            await JoinAllForCleanupAsync(
                (captureWait, nameof(captureWait)), (loop, nameof(loop)));
        }
    }

    /// <summary>
    /// <c>EndToolResponses</c> ALONE IS NOT RETIREMENT — proved through the REAL captured
    /// assignment dependency. A REAL assignment runs on A, its executor-installed dependency is
    /// captured, publication moves to B, and ONLY A's response lifetime is closed. Through that
    /// retained A-bound dependency: the three response-bearing calls fail with the EXISTING
    /// disconnected error and write nothing, while progress/narrative STILL write on A and the
    /// unary <c>GetSession</c>/<c>SaveSession</c> STILL reach A's client with their exact
    /// arguments. B sees zero traffic throughout.
    /// <para>
    /// This is the DISTINCTION test: under retirement (see
    /// <see cref="RetiredAssignmentDependency_RejectsWithDisconnectedError_AndStartsNoTransport"/>)
    /// every category is rejected, so collapsing closure into retirement would fail the
    /// progress/narrative/session assertions here by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EndToolResponsesOnly_ResponseBearingFailsWhileProgressNarrativeAndSessionsStillWork()
    {
        const string assignedIdA = "worker-closure-a";
        var invokerA = new RecordingSessionInvoker();
        var invokerB = new RecordingSessionInvoker();

        var storedJson = JsonSerializer.Serialize(
            AgentSession.Create("closure-session"), AIJsonUtilities.DefaultOptions);
        invokerA.SessionToReturn = new GetSessionResponse { Found = true, SessionJson = storedJson };

        var requestsA = new RecordingToolRequestStream();
        var requestsB = new RecordingToolRequestStream();
        var responsesA = new ChannelResponseReader();
        var responsesB = new ChannelResponseReader();

        var runner = new BridgeCapturingRunner();
        using var service = NewService(runner);

        var connectionA = BuildConnection(assignedIdA, requestsA, responsesA, invokerA);
        var connectionB = BuildConnection("worker-closure-b", requestsB, responsesB, invokerB);
        service.PublishConnection(connectionA);

        var loop = InvokeLoop(service, connectionA, TestContext.Current.CancellationToken);
        Task<IToolCallBridge>? captureWait = null;
        try
        {
            responsesA.Push(Assignment("task-closure"));

            captureWait = runner.Captured.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var bridge = await captureWait;
            var sessions = Assert.IsAssignableFrom<ISessionClient>(bridge);

            await runner.PromptStarted("task-closure")
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Publication moves to B; then ONLY A's response lifetime closes. A itself stays
            // USABLE — it is never retired here.
            service.PublishConnection(connectionB);
            connectionA.EndToolResponses();
            Assert.False(connectionA.IsRetired);

            // ── RESPONSE-BEARING: rejected at registration, BEFORE anything is written. ──
            Assert.Equal(0, requestsA.WriteCount);

            await ExpectDisconnectedAsync(
                () => bridge.RequestClarificationAsync(
                    "task-closure", "why?", TestContext.Current.CancellationToken),
                "request_clarification");

            await ExpectDisconnectedAsync(
                () => bridge.GetGoalAsync(
                    "task-closure", "goal-closure", TestContext.Current.CancellationToken),
                "get_goal");

            await ExpectDisconnectedAsync(
                () => bridge.RaiseIssueAsync(
                    "task-closure", "bug", "t", "d", "low", TestContext.Current.CancellationToken),
                "raise_issue");

            // Not one of the three rejected calls wrote anything, on A or on B.
            Assert.Equal(0, requestsA.WriteCount);
            Assert.Equal(0, requestsB.WriteCount);

            // ── PROGRESS / NARRATIVE: STILL permitted, still on A. Each await is BOUNDED, so a
            //    dependency that instead wrote on B (or parked) fails by name rather than hanging.
            await bridge.ReportProgressAsync(
                    "task-closure", "running", "still alive", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await bridge.ReportNarrativeAsync(
                    "task-closure", "closure narrative", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var progress = await requestsA.WaitForWriteAsync(0, TestContext.Current.CancellationToken);
            var narrative = await requestsA.WaitForWriteAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal("report_progress", progress.ToolRequest.ToolName);
            Assert.Equal("task-closure", progress.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, progress.WorkerId);
            Assert.Equal("""{"status":"running","details":"still alive"}""",
                progress.ToolRequest.ArgumentsJson);
            Assert.Equal("report_narrative", narrative.ToolRequest.ToolName);
            Assert.Equal("task-closure", narrative.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, narrative.WorkerId);
            Assert.Equal("""{"narrative":"closure narrative"}""",
                narrative.ToolRequest.ArgumentsJson);

            // ── UNARY SESSIONS: STILL reach A's own client, with exact arguments. ──
            var loaded = await sessions
                .GetSessionAsync("goal-closure:coder", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(storedJson, loaded);
            Assert.Equal(1, invokerA.LoadCount);
            Assert.Equal("goal-closure:coder", invokerA.LastLoadSessionId);

            var saveJson = JsonSerializer.Serialize(
                AgentSession.Create("closure-save"), AIJsonUtilities.DefaultOptions);
            await sessions
                .SaveSessionAsync("goal-closure:coder", saveJson, TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, invokerA.SaveCount);
            Assert.Equal("goal-closure:coder", invokerA.LastSaveSessionId);
            Assert.Equal(saveJson, invokerA.LastSavedJson);

            // EXACTLY the two permitted writes reached A; B saw nothing at all.
            Assert.Equal(2, requestsA.WriteCount);
            Assert.Equal(0, requestsB.WriteCount);
            Assert.Equal(0, invokerB.LoadCount);
            Assert.Equal(0, invokerB.SaveCount);
            Assert.Equal(0, connectionB.PendingToolResponseCount);
        }
        finally
        {
            runner.ReleaseAll();
            connectionA.EndToolResponses();
            connectionB.EndToolResponses();
            responsesA.TryComplete();
            responsesB.TryComplete();
            await JoinAllForCleanupAsync(
                (captureWait, nameof(captureWait)), (loop, nameof(loop)));
        }
    }

    /// <summary>
    /// LOST-RELEASE REGRESSION for the shared test double <see cref="BridgeCapturingRunner"/> itself:
    /// by the time it signals <c>PromptStarted(id)</c>, that task's RELEASE gate must ALREADY be
    /// registered, so a <see cref="BridgeCapturingRunner.ReleaseAll"/> performed at the instant
    /// between the started signal and the release await still frees the prompt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE RACE WINDOW. <c>Slot</c> creates each gate lazily and <c>ReleaseAll</c> completes only the
    /// gates present at the moment it runs. Publishing the started slot BEFORE the release gate was
    /// created therefore opened a window in which a continuation woken by <c>PromptStarted(id)</c>
    /// could run <c>ReleaseAll()</c> while this task's release gate did not exist yet: nothing was
    /// completed for it, the gate created a moment later stayed parked, and the prompt never returned
    /// — the reported session-save timeout symptom. The
    /// <see cref="BridgeCapturingRunner.OnStartedSignalled"/> callback fires SYNCHRONOUSLY inside
    /// exactly that window, so landing there is a property of the code under test rather than of any
    /// schedule.
    /// </para>
    /// <para>
    /// REMOVAL-PROOFNESS. The callback invokes the helper's OWN <c>ReleaseAll</c>, so this drives the
    /// helper's REAL prompt path — not a mirrored reimplementation of its gates. Moving the
    /// release-gate capture back after the started signal/callback (the pre-fix order) makes that
    /// <c>ReleaseAll</c> find no gate for this task, so the prompt parks and the task-state assertion
    /// below fails promptly BY NAME — no bound is waited on, and the parked prompt is then freed in
    /// the <c>finally</c> rather than left hanging.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task BridgeCapturingRunner_ReleaseAllInsideTheStartedSignal_FreesThePrompt_ProvingTheReleaseGateIsRegisteredFirst()
    {
        var runner = new BridgeCapturingRunner();
        runner.SetCurrentTaskId("task-lost-release");

        // THE FORMER LOST-RELEASE WINDOW: this runs synchronously between the started signal and the
        // release await — exactly where a continuation woken by PromptStarted would have run.
        runner.OnStartedSignalled = runner.ReleaseAll;

        var prompt = runner.SendPromptAsync("prompt", "work-dir", CancellationToken.None);
        try
        {
            // FAIL PROMPTLY, ON TASK STATE. `SendPromptAsync` runs synchronously up to its release
            // await, and with the capture-first order the very Task it awaits was already completed
            // by the `ReleaseAll` the callback above invoked — so the prompt is terminal the moment
            // this call returns. Under the pre-fix order the release gate is created only AFTER the
            // callback, so nothing completed it and the prompt is still parked here: this assertion
            // fails deterministically without waiting on any bound.
            Assert.True(
                prompt.IsCompletedSuccessfully,
                "Releasing inside the started-signal window did not free the prompt: this task's "
                    + "release gate was not registered before PromptStarted was signalled.");

            Assert.Equal("binding-runner output", await prompt);
        }
        finally
        {
            // NEVER leave the prompt parked: release every gate, then join the ORIGINAL prompt Task
            // (bounded purely as a safeguard, so a failed assertion above cannot mask a producer
            // that is still running).
            runner.ReleaseAll();
            await JoinForCleanupAsync(prompt, nameof(prompt));
        }
    }

    /// <summary>
    /// Asserts that <paramref name="operation"/> fails with the EXISTING disconnected error,
    /// BOUNDED by the failsafe so a regression that leaves the call pending forever fails BY NAME
    /// instead of hanging the run.
    /// </summary>
    /// <remarks>
    /// The bound matters here: a dependency that resolved the CURRENT published connection instead
    /// of its captured one would not throw at all — it would register a response wait on the newly
    /// published connection and park indefinitely. Awaiting the raw task would then hang; awaiting
    /// it through this helper produces a named failure that identifies the operation.
    /// </remarks>
    /// <param name="operation">The operation to invoke through the dependency under test.</param>
    /// <param name="name">The operation's name, used in the bound-expiry diagnostic.</param>
    private static async Task ExpectDisconnectedAsync(Func<Task> operation, string name)
    {
        var call = operation();

        try
        {
            await call.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Equal(WorkerConnection.DisconnectedMessage, ex.Message);
            return;
        }
        catch (TimeoutException ex) when (!call.IsCompleted)
        {
            throw new TimeoutException(
                $"'{name}' neither failed nor completed within {Failsafe}: the dependency did not "
                    + "reject on its captured connection, so the disconnected contract did not hold.",
                ex);
        }

        Assert.Fail($"'{name}' completed successfully, but the disconnected error was required.");
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
        BuildConnection(WorkerId, requests, responses);

    private static WorkerConnection BuildConnection(
        string assignedId,
        IClientStreamWriter<WorkerMessage>? requests,
        IAsyncStreamReader<OrchestratorMessage>? responses,
        CallInvoker? invoker = null) =>
        new(assignedId,
            invoker is null
                ? new HiveOrchestrator.HiveOrchestratorClient(Channel)
                : new HiveOrchestrator.HiveOrchestratorClient(invoker),
            Stream(requests, responses), provisionerOverride: null, includeProductionProvisioner: false);

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

    /// <summary>An assignment carrying a session ID, so the executor performs a real session load and save.</summary>
    private static OrchestratorMessage SessionAssignment(string taskId, string sessionId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-session-binding",
            GoalDescription = "exercise the connection-bound session dependency",
            Prompt = "resume the session",
            Role = GrpcWorkerRole.Coder,
            SessionId = sessionId,
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

    /// <summary>
    /// BODY-PHASE bounded observation of a RETAINED producer. The producer's own outcome propagates
    /// unchanged (so the body still asserts it), and a bound expiry while the ORIGINAL task is still
    /// live is a NAMED FAILURE — never treated as success — so a producer that can no longer make
    /// progress (for example one parked forever on a send gate whose release was removed) is
    /// diagnosed instead of hanging the test before its <c>finally</c>.
    /// </summary>
    private static async Task AwaitProducerWithinBoundAsync(Task producer, string name)
    {
        try
        {
            await producer.WaitAsync(Failsafe);
        }
        catch (TimeoutException ex) when (!producer.IsCompleted)
        {
            throw new TimeoutException(
                $"Producer '{name}' was still live after {Failsafe} — it never completed, so the "
                    + "behavior under test did not hold.", ex);
        }
    }

    /// <summary>
    /// Teardown join that observes every started producer and NEVER treats a live-task timeout as
    /// successful cleanup. A producer's already-completed scenario fault is observed and ignored
    /// here because its outcome is asserted in the test body; a timeout while the ORIGINAL task is
    /// still live is rethrown with a producer-specific diagnostic and fails the test.
    /// </summary>
    private static async Task JoinForCleanupAsync(Task? producer, string name)
    {
        if (producer is null) return;

        try
        {
            await producer.WaitAsync(Failsafe);
        }
        catch (TimeoutException ex) when (!producer.IsCompleted)
        {
            throw new TimeoutException(
                $"Cleanup failed: producer '{name}' was still live after {Failsafe}.", ex);
        }
        catch (Exception) when (producer.IsCompleted)
        {
            // The ORIGINAL producer is terminal and its scenario outcome was asserted in the body.
        }
    }

    /// <summary>
    /// Joins EVERY supplied producer even when an earlier one times out, then fails with all live-task
    /// diagnostics. This prevents one bad cleanup from skipping the remaining original producers.
    /// </summary>
    private static async Task JoinAllForCleanupAsync(params (Task? Producer, string Name)[] producers)
    {
        List<Exception> failures = [];
        foreach (var (producer, name) in producers)
        {
            try { await JoinForCleanupAsync(producer, name); }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (failures.Count != 0)
            throw new AggregateException("One or more cleanup producers remained live.", failures);
    }

    private static async Task JoinThreadsAndTasksForCleanupAsync(
        (Thread Thread, string Name)[] threads,
        (Task? Producer, string Name)[] producers)
    {
        List<Exception> failures = [];
        foreach (var (thread, name) in threads)
        {
            if (thread.ThreadState == ThreadState.Unstarted)
                continue;
            if (!thread.Join(Failsafe))
                failures.Add(new TimeoutException(
                    $"Cleanup failed: thread '{name}' was still live after {Failsafe}."));
        }

        foreach (var (producer, name) in producers)
        {
            try { await JoinForCleanupAsync(producer, name); }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (failures.Count != 0)
            throw new AggregateException("One or more cleanup producers remained live.", failures);
    }

    private static async Task<WeakReference> ExerciseSendFailureRaceAsync(
        WorkerService service,
        WorkerConnection connection,
        ControlledFailingResponseRequestStream requests,
        Exception injected)
    {
        Task<string>? call = null;
        try
        {
            call = service.RaiseIssueAsync("task-f", "bug", "t", "d", "low", CancellationToken.None);
            await requests.WriteEntered.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, connection.PendingToolResponseCount);
            Assert.False(call.IsCompleted);
            var abandonedResponse = CaptureOnlyPendingResponseTask(connection);

            // END wins while the underlying write is still in flight, faulting the response task.
            // The bridge call remains parked on the ORIGINAL write, proving this is the losing,
            // abandoned response fault rather than the externally propagated outcome.
            connection.EndToolResponses();
            Assert.Equal(0, connection.PendingToolResponseCount);
            Assert.False(call.IsCompleted);

            // The write then fails with its OWN injected exception, which remains authoritative.
            requests.Fail(injected);
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(
                () => call.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(injected, caught);
            Assert.Equal(1, requests.WriteCount); // one attempt — no resend
            return abandonedResponse;
        }
        finally
        {
            requests.Fail(injected);
            connection.EndToolResponses();
            await JoinForCleanupAsync(call, nameof(call));
        }
    }

    private static WeakReference CaptureOnlyPendingResponseTask(WorkerConnection connection)
    {
        const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        var gate = typeof(WorkerConnection).GetField("_toolResponsesLock", privateInstance)?.GetValue(connection)
            ?? throw new InvalidOperationException("WorkerConnection._toolResponsesLock was not found.");
        var pending = (Dictionary<string, TaskCompletionSource<ToolCallResponse>>)(
            typeof(WorkerConnection).GetField("_toolResponses", privateInstance)?.GetValue(connection)
            ?? throw new InvalidOperationException("WorkerConnection._toolResponses was not found."));

        lock (gate)
            return new WeakReference(Assert.Single(pending).Value.Task);
    }

    private static void CollectUntilDead(WeakReference target)
    {
        for (var attempt = 0; attempt < 10 && target.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(target.IsAlive, "The abandoned response task remained rooted, so observation was not proven.");
    }

    /// <summary>
    /// Records every request write and lets a test await a specific write deterministically. Uses the
    /// shared base's explicit cancellable-write implementation, since production writes with the live
    /// stream token.
    /// <para>
    /// Shared with <see cref="WorkerServiceAssignmentConnectionBindingTests"/>, which drives the
    /// PROVISIONED executor branch through this same writer seam.
    /// </para>
    /// </summary>
    internal sealed class RecordingToolRequestStream : FakeClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _writes = [];
        private readonly Dictionary<int, TaskCompletionSource<WorkerMessage>> _waiters = [];

        internal int WriteCount { get { lock (_gate) return _writes.Count; } }
        internal IReadOnlyList<WorkerMessage> Writes { get { lock (_gate) return _writes.ToList(); } }
        internal Action<WorkerMessage>? OnWrite { get; set; }

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
            OnWrite?.Invoke(message);

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
    /// First write starts and remains in flight until <see cref="Fail"/> is called; subsequent plain
    /// writes complete immediately. This lets EndToolResponses fault the registered response task
    /// BEFORE the original write fails.
    /// </summary>
    private sealed class ControlledFailingResponseRequestStream : FakeClientStreamWriter<WorkerMessage>
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;

        internal Task WriteEntered => _entered.Task;
        internal int WriteCount => Volatile.Read(ref _writeCount);

        public override Task WriteAsync(WorkerMessage message)
        {
            var count = Interlocked.Increment(ref _writeCount);
            if (count != 1)
                return Task.CompletedTask;

            _entered.TrySetResult();
            return _firstWrite.Task;
        }

        internal void Fail(Exception error) => _firstWrite.TrySetException(error);
        public override Task CompleteAsync() => Task.CompletedTask;
    }

    /// <summary>Minimal successful runner for a real assignment's Complete/Ready sends.</summary>
    private sealed class ImmediateRunner : IAgentRunner
    {
        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct) =>
            Task.FromResult("done");
        public void SetCurrentTaskId(string? taskId) { }
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
        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default) =>
            Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Fake unary client for post-response-close session coverage.</summary>
    private sealed class SessionInvoker : CallInvoker
    {
        private int _getCalls;
        private int _saveCalls;
        private string? _lastGetSessionId;
        private string? _lastSaveSessionId;
        private string? _lastSavedJson;

        internal int GetCalls => Volatile.Read(ref _getCalls);
        internal int SaveCalls => Volatile.Read(ref _saveCalls);
        internal string? LastGetSessionId => Volatile.Read(ref _lastGetSessionId);
        internal string? LastSaveSessionId => Volatile.Read(ref _lastSaveSessionId);
        internal string? LastSavedJson => Volatile.Read(ref _lastSavedJson);

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object response = method.FullName switch
            {
                "/copilothive.HiveOrchestrator/GetSession" => GetSession(request),
                "/copilothive.HiveOrchestrator/SaveSession" => SaveSession(request),
                _ => throw new NotSupportedException($"Unexpected unary call {method.FullName}."),
            };
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)response), Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty), () => new Metadata(), () => { });
        }

        private object GetSession<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _getCalls);
            Volatile.Write(ref _lastGetSessionId, (request as GetSessionRequest)?.SessionId);
            return new GetSessionResponse { Found = true, SessionJson = "{\"turn\":1}" };
        }

        private object SaveSession<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _saveCalls);
            if (request is SaveSessionRequest save)
            {
                Volatile.Write(ref _lastSaveSessionId, save.SessionId);
                Volatile.Write(ref _lastSavedJson, save.SessionJson);
            }
            return new SaveSessionResponse { Success = true };
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected server-streaming call {method.FullName}.");
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected client-streaming call {method.FullName}.");
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected duplex call {method.FullName}.");
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

    /// <summary>
    /// A <see cref="CallInvoker"/> answering ONLY the session RPCs, recording the exact session ID
    /// and saved JSON each call carried, with a deterministic gate that can HOLD the next save
    /// until the test releases it (used to move the service's publication between load and save).
    /// </summary>
    internal sealed class RecordingSessionInvoker : CallInvoker
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _signals = [];
        private int _loadCount;
        private int _saveCount;
        private string? _lastLoadSessionId;
        private string? _lastSaveSessionId;
        private string? _lastSavedJson;

        /// <summary>The response the NEXT load returns.</summary>
        internal GetSessionResponse? SessionToReturn { get; set; }

        /// <summary>
        /// When non-null, the save parks on this source's cancellation before returning, so a test
        /// can prove the publication change happened BETWEEN the load and the save.
        /// </summary>
        internal CancellationTokenSource? HoldNextSave { get; set; }

        internal int LoadCount { get { lock (_gate) return _loadCount; } }
        internal int SaveCount { get { lock (_gate) return _saveCount; } }
        internal string? LastLoadSessionId { get { lock (_gate) return _lastLoadSessionId; } }
        internal string? LastSaveSessionId { get { lock (_gate) return _lastSaveSessionId; } }
        internal string? LastSavedJson { get { lock (_gate) return _lastSavedJson; } }

        private void Signal(string name)
        {
            lock (_gate)
            {
                if (!_signals.TryGetValue(name, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _signals[name] = tcs;
                }
                tcs.TrySetResult();
            }
        }

        /// <summary>
        /// Completes when the named signal has fired (creating the source on first use), bounded by
        /// the failsafe — the deterministic gate for "the load/save reached this invoker".
        /// </summary>
        internal Task AwaitAsync(string name, CancellationToken ct)
        {
            Task task;
            lock (_gate)
            {
                if (!_signals.TryGetValue(name, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _signals[name] = tcs;
                }
                task = tcs.Task;
            }

            return task.WaitAsync(Failsafe, ct);
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object GetSession()
            {
                Interlocked.Increment(ref _loadCount);
                lock (_gate) _lastLoadSessionId = (request as GetSessionRequest)?.SessionId;
                Signal("load");
                var payload = SessionToReturn ?? new GetSessionResponse { Found = false };
                return payload;
            }

            async Task<object> SaveSession()
            {
                Interlocked.Increment(ref _saveCount);
                lock (_gate)
                {
                    _lastSaveSessionId = (request as SaveSessionRequest)?.SessionId;
                    _lastSavedJson = (request as SaveSessionRequest)?.SessionJson;
                }
                Signal("save");

                // The HOLD: the returned Task parks until the test releases it, so the publication
                // change can land strictly between load and save.
                if (HoldNextSave is not null)
                {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var reg = HoldNextSave.Token.Register(() => release.TrySetResult());
                    await release.Task;
                }

                return new SaveSessionResponse { Success = true };
            }

            return method.FullName switch
            {
                "/copilothive.HiveOrchestrator/GetSession" => new AsyncUnaryCall<TResponse>(
                    Task.FromResult((TResponse)GetSession()), Task.FromResult(new Metadata()),
                    () => new Status(StatusCode.OK, string.Empty), () => new Metadata(), () => { }),
                "/copilothive.HiveOrchestrator/SaveSession" => new AsyncUnaryCall<TResponse>(
                    SaveSession().ContinueWith(t => (TResponse)t.Result, TaskScheduler.Default),
                    Task.FromResult(new Metadata()),
                    () => new Status(StatusCode.OK, string.Empty), () => new Metadata(), () => { }),
                _ => throw new NotSupportedException($"Unexpected unary call {method.FullName}."),
            };
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected server-streaming call {method.FullName}.");
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected client-streaming call {method.FullName}.");
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected duplex call {method.FullName}.");
    }

    /// <summary>
    /// THE SHARED ASSIGNMENT-BINDING OBSERVATION RUNNER. It records the bridge dependency the REAL
    /// assignment setup installed on the executor (<c>SetToolBridge</c>), retains whatever session
    /// the executor loaded (so the executor's own save serializes real JSON), signals when its
    /// prompt starts — keyed by the task ID the executor set — and PARKS there until released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The park is what gives every binding test its deterministic window: the assignment body is
    /// genuinely mid-execution and still owns its dependency, so the test can move the service's
    /// publication to a second connection and then exercise the RETAINED dependency. Nothing here
    /// is a stand-in for the collaborator under test — the captured object IS the dependency the
    /// real <see cref="TaskExecutor"/> construction received.
    /// </para>
    /// <para>
    /// Shared with <see cref="WorkerServiceAssignmentConnectionBindingTests"/>, which drives the
    /// PROVISIONED executor branch through the same observation points.
    /// </para>
    /// </remarks>
    internal sealed class BridgeCapturingRunner : IAgentRunner
    {
        private readonly TaskCompletionSource<IToolCallBridge> _captured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private object? _session;
        private string? _currentTaskId;

        /// <summary>
        /// The dependency the REAL assignment setup installed, completed at the executor's own
        /// <c>SetToolBridge</c> call. A <c>null</c> install is a production regression, so it is
        /// surfaced as a failure rather than silently captured.
        /// </summary>
        internal Task<IToolCallBridge> Captured => _captured.Task;

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

        /// <summary>Completes once the executor's prompt for <paramref name="taskId"/> has started.</summary>
        internal Task PromptStarted(string taskId) => Slot(_started, taskId).Task;

        /// <summary>Teardown failsafe: releases every gate a parked prompt could hold.</summary>
        internal void ReleaseAll()
        {
            lock (_gate)
            {
                foreach (var tcs in _started.Values) tcs.TrySetResult();
                foreach (var tcs in _release.Values) tcs.TrySetResult();
            }
        }

        public void SetToolBridge(IToolCallBridge? bridge) =>
            _captured.TrySetResult(bridge ?? throw new InvalidOperationException(
                "The assignment setup must install a non-null bridge dependency."));

        public void SetCurrentTaskId(string? taskId) => Volatile.Write(ref _currentTaskId, taskId);

        /// <summary>
        /// A <c>null</c>-by-default observation seam, inert in every other use of this double: it is
        /// invoked SYNCHRONOUSLY inside <see cref="SendPromptAsync"/> immediately after the started
        /// slot is signalled and before the release await, so a regression test can call
        /// <see cref="ReleaseAll"/> at exactly the former lost-release window.
        /// </summary>
        internal Action? OnStartedSignalled { get; set; }

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            // The task ID the EXECUTOR set for this assignment — never a hardcoded literal, so the
            // gate always belongs to the assignment actually running.
            var id = Volatile.Read(ref _currentTaskId)
                ?? throw new InvalidOperationException(
                    "The executor must set the current task ID before prompting.");

            // ORDERING: resolve (and thereby CREATE) this task's release gate BEFORE the started
            // slot is published, and await that same captured Task instance below. `Slot` creates a
            // gate lazily and `ReleaseAll` completes only the gates that already exist, so
            // publishing `started` first left a window in which a continuation woken by
            // `PromptStarted(id)` could run `ReleaseAll()` while this task's release gate did not
            // exist yet: nothing was completed for it, the gate created a moment later stayed parked
            // and the prompt never returned (the reported session-save timeout). With the capture
            // first, observing `PromptStarted(id)` guarantees `ReleaseAll()` already finds this
            // gate, even if the release await has not yet begun.
            var releaseGate = Slot(_release, id).Task;

            Slot(_started, id).TrySetResult();

            // The former lost-release window, exposed for the deterministic regression only.
            OnStartedSignalled?.Invoke();

            await releaseGate.WaitAsync(ct);
            return "binding-runner output";
        }

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) => _session = session;
        public object? GetSession() => _session;
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
