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
    /// </summary>
    private sealed class RecordingToolRequestStream : FakeClientStreamWriter<WorkerMessage>
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
}
