using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Net.Client;
using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Diagnostics;
using System.Reflection;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE RECONNECT-SURVIVAL FIXTURE — ONE <see cref="WorkerService"/>, REAL SEQUENTIAL
/// <see cref="WorkerService.RunAsync"/>-shaped runs over the existing
/// <see cref="WorkerService.CallInvokerFactory"/> and <see cref="WorkerService.WorkStreamFactory"/>
/// seams, plus focused unit coverage of the v0.40.0 CARRY PATH (the CORE SURVIVAL slice).
/// <para>
/// This file is the REUSABLE BASE the follow-up hardening goals and the round-2
/// acceptance-criteria suite extend: the sequential-run fixture (<see cref="CarryHarness"/>) starts
/// ONE REAL run (registration, publication, heartbeat seam, initial Ready, a genuine assignment),
/// records the stream loss that carries it, publishes adoptions through the production publication
/// seam (<see cref="CarryHarness.AdoptConnection"/>), and can re-enter further runs on the SAME
/// service (round 2). The focused unit tests use the same seam style — deterministic
/// <see cref="TaskCompletionSource"/> gates, no sleeps, no polling — while driving the REAL
/// <see cref="WorkerService"/> loop, drains, reporting and carried-delivery machinery.
/// </para>
/// <para>
/// FAKE STREAMS FAULT PENDING WRITES ON DISPOSE and honor write tokens: a write parked inside the
/// fake unwinds with <see cref="OperationCanceledException"/> when its FORWARDED token is cancelled
/// (the transport disposal cancels the call), so a carried teardown can never be held by a pending
/// old-connection write. This mirrors what gRPC's Dispose contract promises ("requests cancellation
/// of the call which should terminate all pending async operations").
/// </para>
/// <para>
/// Production code is FROZEN for this validation round: these tests observe the REAL carry
/// machinery through the existing internal test hooks
/// (<see cref="WorkerService.CarriedBeforeCompleteSendHook"/>,
/// <see cref="WorkerService.CarriedBeforeReadyClaimHook"/>), the public-ish
/// <see cref="WorkerService.DrainCarriedAssignmentAsync"/> and reflection observation only.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerServiceReconnectSurvivalTests
{
    /// <summary>A bounded FAILURE failsafe for every await; never an ordering device.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(15);

    /// <summary>The expected <see cref="WorkerRunOutcome"/> of the carried run under test.</summary>
    private const WorkerRunOutcome RunCompletion = WorkerRunOutcome.WorkStreamEnded;

    // ══════════════════════════════════════════════════════════════════════════
    // 1. The ONE CAS state machine — transitions on the retained assignment.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OPEN → CARRIED wins at the stream-loss site: the EOF teardown carries a retained
    /// assignment whose ordinary Ready never started — no cancel, no drain, no readiness write,
    /// and the slot stays occupied (Carried) with the heartbeat state REASSERTED from the
    /// assignment.
    /// </summary>
    [Fact]
    public async Task EofTeardown_CarriesOpenAssignment_NoCancelNoDrainSlotStaysCarried()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var requests = harness.Requests;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? carriedDelivery = null;
        try
        {
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // EOF while the assignment is Open (the body is still running; its Ready was never
            // started).
            responses.TryComplete();

            // THE CARRY CAS WON: the loop's teardown observes the reporting, closes the retry
            // admission, re-asserts the heartbeat state and starts the ONE carried delivery.
            carriedDelivery = await WaitForCarriedDeliveryAsync(
                service, "The carry claim must start a CarriedDelivery.");

            // NO CANCEL AND NO DRAIN: the execution and the reporting keep their pre-loss
            // identity and were not cancelled by the teardown.
            Assert.Same(execution, GetActiveExecution(service));
            Assert.Same(reporting, GetActiveReporting(service));
            Assert.False(harness.Runner.WasCancelled("task-A"), "A carried assignment is never cancelled.");

            // THE SLOT STAYS RETAINED, in the Carried state, with the assignment's identity.
            var owner = GetActiveAssignment(service);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));
            Assert.Equal("task-A", GetOwnerTaskId(owner));
            Assert.Equal("coder", GetOwnerRole(owner));
            Assert.Equal("task-A", GetHeartbeatTaskId(service));
            Assert.Equal("coder", GetHeartbeatRole(service));

            // NO ORDINARY READY BEYOND THE INITIAL ONE: the carried branch claims nothing, so no
            // drain settlement can emit one.
            Assert.Equal(0, requests.AssignmentReadyCount);

            // The connection was retired AT THE READ-AWAIT SITE (the early retire), before the
            // teardown carried the assignment.
            Assert.True(harness.Connection.IsRetired);

            // The run ends with the assignment Carried; the exit re-check must leave it alone.
            await harness.JoinRunAsync();
            var surviving = GetActiveAssignment(service);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(surviving));
            Assert.False(carriedDelivery.IsCompleted, "The delivery stays parked awaiting an adoption.");
        }
        finally
        {
            await CarryHarness.TeardownAsync(harness, execution, reporting, carriedDelivery);
        }
    }

    /// <summary>
    /// OPEN → READYSTARTED wins at the same stream-loss site: an assignment whose ordinary Ready
    /// write has already been claimed keeps today's cancel-and-drain teardown — it is NOT
    /// carried, the slot ends empty and the heartbeat state is cleared.
    /// </summary>
    [Fact]
    public async Task EofTeardown_ReadyStartedAssignment_TakesTodaysCancelAndDrainTeardown()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var requests = harness.Requests;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // The body claims its own ordinary Ready through the settlement: release the report
            // (the Complete write is NOT held, so it lands), let the eligibility publish, and
            // HOLD the STARTED write inside the fake so it is in flight at the loss.
            harness.Runner.Release("task-A");
            await requests.AssignmentCompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await requests.AssignmentReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessWrite = GetRetainedReadinessWrite(
                service, "The ordinary readiness write must be started and retained.");
            var owner = GetActiveAssignment(service);
            Assert.Equal(CarryStates.ReadyStarted, GetAssignmentState(owner));
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // EOF while the assignment is ReadyStarted (mutually exclusive with Carried).
            responses.TryComplete();

            // THE EARLY RETIRE: the read-await site retires the connection BEFORE the teardown.
            // Wait for that fact's arrival (bounded; the retire is the FIRST action, so it
            // cannot race past any later teardown step).
            await WaitForRetiredAsync(harness.Connection, "The early retire must run at the read-await site.");

            // TODAY'S TEARDOWN: the drain joins the held write; release it so the teardown can
            // reach fixpoint, then the slot is cleared and the heartbeat state is cleared.
            requests.ReleaseAll();
            await harness.JoinRunAsync();

            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Null(GetCarriedDeliveryOrNull(service));
            Assert.Equal(1, requests.AssignmentReadyCount);
        }
        finally
        {
            await CarryHarness.TeardownAsync(harness, execution, reporting, readinessWrite);
        }
    }

    /// <summary>
    /// READYSTARTED ↔ CARRIED mutual exclusion, observed at the REAL boundary: after a stream
    /// loss carried the assignment (state left Open), the production settlement — the ONE entry
    /// point every ordinary-Ready path uses — claims nothing and writes nothing, so a Carried
    /// assignment can never emit an ordinary Ready on its retired original connection.
    /// </summary>
    [Fact]
    public async Task CarriedAssignment_SettlementLosesClaimsNothingAndWritesNothing()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var requests = harness.Requests;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? carriedDelivery = null;
        try
        {
            // Assign A; carry it at EOF while the body is still running (Open → Carried wins).
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            responses.TryComplete();
            carriedDelivery = await WaitForCarriedDeliveryAsync(
                service, "The Open assignment must be carried at the stream loss.");

            // Let the reporter reach its finally so the eligibility PUBLISHES — the settlement's
            // readiness predicate then holds, which is what makes the CAS rule (and not an
            // unheld predicate) the thing under test. The Complete write fails disconnected on
            // the retired connection, which the carried contract tolerates.
            harness.Runner.Release("task-A");
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The predicate HOLDS, the state is Carried, so the production settlement LOSES the
            // Open → ReadyStarted claim: it returns nothing and retains no write.
            var owner = GetActiveAssignment(service);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));
            Assert.Null(InvokeSettleOrdinaryReady(service, GetOwnerOrdinaryReady(owner)));
            Assert.Equal(0, GetReadyClaimState(GetOwnerReadyClaim(owner)));
            Assert.Equal(0, requests.AssignmentReadyCount);

            await harness.JoinRunAsync();
        }
        finally
        {
            await CarryHarness.TeardownAsync(harness, execution, reporting, carriedDelivery);
        }
    }

    /// <summary>
    /// CARRIED → DELIVERED: the delivery task's own transition through the PRODUCTION delivery
    /// machinery — an adopted connection, the Complete send, the hook point, the claim and the
    /// carried Ready. Asserts the state change, that the heartbeat state is cleared at the
    /// transition, and that a later settlement loser can never claim.
    /// </summary>
    [Fact]
    public async Task CarriedDelivery_TransitionsCarriedToDelivered_AndLaterSettlementLoses()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? carriedDelivery = null;
        try
        {
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // Carry at EOF, then deliver on an adopted connection through the PRODUCTION
            // CarriedDelivery task.
            responses.TryComplete();
            carriedDelivery = await WaitForCarriedDeliveryAsync(
                service, "The assignment must be carried first.");
            var owner = GetActiveAssignment(service);

            var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.CarriedBeforeReadyClaimHook = () =>
            {
                // OBSERVED INSIDE the delivery, at the hook's exact contract point: after the
                // Complete write succeeded and BEFORE the Ready claim — the state is Delivered.
                Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));
                hookEntered.TrySetResult();
                return Task.CompletedTask;
            };

            // Publish the adopted-run shape; the delivery then has its target. The reporter
            // must terminate first (the delivery OBSERVES it), so release the body: its
            // Complete fails disconnected on the retired connection.
            var adopted = harness.AdoptConnection();
            harness.Runner.Release("task-A");

            await hookEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await carriedDelivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            service.CarriedBeforeReadyClaimHook = null;

            // DELIVERED: the Complete went to the ADOPTED connection (the failed old-connection
            // write never entered either fake), the state moved, and the heartbeat state was
            // cleared at the transition.
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));
            var adoptedRequests = harness.AdoptedRequests!;
            var complete = Assert.Single(adoptedRequests.Completes);
            Assert.Equal("task-A", complete.Complete.TaskId);
            Assert.Equal(adopted.AssignedId, complete.WorkerId);
            Assert.Equal(1, adoptedRequests.AssignmentReadyCount);
            Assert.Equal(adopted.AssignedId, adoptedRequests.Readies[^1].WorkerId);
            Assert.True(GetCarriedReadyStarted(owner), "The carried Ready write must have been initiated.");
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Null(GetHeartbeatRole(service));

            // A LOSER cannot claim: the state is Delivered, so the production settlement claims
            // nothing and writes nothing on the retired original connection.
            Assert.Null(InvokeSettleOrdinaryReady(service, GetOwnerOrdinaryReady(owner)));
            Assert.Equal(1, adoptedRequests.AssignmentReadyCount);

            await harness.JoinRunAsync();
        }
        finally
        {
            service.CarriedBeforeReadyClaimHook = null;
            await CarryHarness.TeardownAsync(harness, execution, reporting, carriedDelivery);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. The early retire — retire FIRST at the stream-loss site.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EARLY RETIRE-BEFORE-CARRY ordering: the connection is retired at the read-await site
    /// BEFORE the carry teardown takes any action — the carried teardown's re-asserted heartbeat
    /// state and the started delivery are both observable only AFTER the retire already happened.
    /// </summary>
    [Fact]
    public async Task StreamLoss_RetiresConnectionBeforeTheCarryTeardownActs()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var responses = harness.Responses;
        var connection = harness.Connection;

        Task? execution = null;
        Task? reporting = null;
        Task? carriedDelivery = null;
        try
        {
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            Assert.False(connection.IsRetired, "Pre-loss: the connection must still be usable.");

            responses.TryComplete();
            carriedDelivery = await WaitForCarriedDeliveryAsync(
                service, "The assignment must be carried.");

            // THE EARLY RETIRE PRECEDED THE CARRY: retirement is the FIRST action at the
            // read-await site, so by the time the carry teardown started the delivery and
            // re-asserted the heartbeat state, the connection was already retired.
            Assert.True(connection.IsRetired);
            Assert.Equal("task-A", GetHeartbeatTaskId(service));

            await harness.JoinRunAsync();
        }
        finally
        {
            await CarryHarness.TeardownAsync(harness, execution, reporting, carriedDelivery);
        }
    }

    /// <summary>
    /// THE EARLY RETIRE IN THE NO-ASSIGNMENT BRANCH: EOF with nothing retained still retires the
    /// connection at the read-await site — before the loop's teardown completes.
    /// </summary>
    [Fact]
    public async Task StreamLoss_NoAssignment_RetiresConnectionBeforeTeardownCompletes()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var responses = harness.Responses;

        try
        {
            // EOF with NO assignment: the loop's finally runs today's teardown on an empty slot.
            responses.TryComplete();

            await harness.JoinRunAsync();
            Assert.True(harness.Connection.IsRetired);
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Equal(0, GetSlotOccupancy(service));
        }
        finally
        {
            responses.TryComplete();
            harness.Runner.ReleaseAll();
            await JoinAllForTeardownAsync(("run", harness.Run));
            TryDispose(service);
        }
    }

    /// <summary>
    /// THE EARLY RETIRE IN THE READYSTARTED BRANCH: the connection is retired BEFORE the drain,
    /// so no NEW write can start on it — the pre-loss Ready is the only assignment Ready attempt
    /// ever made, and today's teardown still clears the slot and the heartbeat state.
    /// </summary>
    [Fact]
    public async Task StreamLoss_ReadyStartedBranch_EarlyRetireBeforeDrainSoNoNewWriteStarts()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var requests = harness.Requests;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            harness.Runner.Release("task-A");
            await requests.AssignmentCompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await requests.AssignmentReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessWrite = GetRetainedReadinessWrite(
                service, "The ordinary readiness write must be started and retained.");
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // EOF in the ReadyStarted branch: the early retire precedes the drain. Wait for the
            // retire's arrival (bounded), which by itself does not drain anything.
            responses.TryComplete();
            await WaitForRetiredAsync(harness.Connection, "The early retire must run at the read-await site.");

            // The drain joins the held write; release it, then the teardown reaches fixpoint.
            requests.ReleaseAll();
            await harness.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));

            // EXACTLY ONE ASSIGNMENT READY ATTEMPT: the pre-loss write. The early retire wrote
            // nothing new.
            Assert.Equal(1, requests.AssignmentReadyCount);
        }
        finally
        {
            await CarryHarness.TeardownAsync(harness, execution, reporting, readinessWrite);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. The heartbeat clear-then-recheck.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// HEARTBEAT CLEAR-THEN-RECHECK through the REAL reporting finally: the reporter's cleanup
    /// clears <c>_currentTaskId</c>/<c>_currentRole</c> first, then re-reads the state and
    /// restores them from the retained assignment ONLY for a Carried one — so the orchestrator
    /// keeps seeing the worker as busy on the SAME task with its REAL role.
    /// </summary>
    [Fact]
    public async Task ReportingFinally_ClearsThenRestoresHeartbeatStateWhenCarried()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var requests = harness.Requests;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? carriedDelivery = null;
        try
        {
            // Hold the reporter's Complete write INSIDE the fake: the body finishes, the
            // reporter enters its write, and the eligibility is NOT yet published — the
            // assignment stays Open.
            requests.HoldCompletesFrom = 0;

            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            harness.Runner.Release("task-A");
            await requests.AssignmentCompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE REPORT IS STILL HELD inside its Complete write (the pre-loss tolerated write).
            Assert.False(reporting.IsCompleted, "The report must still be held inside its Complete write.");

            // EOF while the reporter is PARKED in its Complete write: the assignment is carried.
            responses.TryComplete();
            carriedDelivery = await WaitForCarriedDeliveryAsync(
                service, "The assignment must be carried while the report is held.");
            Assert.Equal("task-A", GetHeartbeatTaskId(service));

            // Release the write: it was a tolerated pre-loss write (already past the usability
            // check), so the report terminates and its finally runs the clear-then-recheck.
            requests.ReleaseAll();
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // RESTORED, not cleared: the assignment is Carried, so the heartbeat state comes
            // back from the assignment itself — task id AND role.
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(service)));
            Assert.Equal("task-A", GetHeartbeatTaskId(service));
            Assert.Equal("coder", GetHeartbeatRole(service));

            await harness.JoinRunAsync();
        }
        finally
        {
            await CarryHarness.TeardownAsync(harness, execution, reporting, carriedDelivery);
        }
    }

    /// <summary>
    /// THE CLEAR-THEN-RECHECK IS CARRIED-ONLY: a reporter whose retained assignment is NOT
    /// Carried (here: a Delivered one) keeps today's cleared state — the recheck resurrects
    /// nothing. The retained owner is a REAL <c>ActiveAssignment</c> in the Delivered state
    /// (built through the production constructors), driven through the REAL
    /// <c>ReportAssignmentAsync</c>.
    /// </summary>
    [Fact]
    public async Task ReportingFinally_DoesNotResurrectNonCarriedHeartbeatState()
    {
        var requests = new CarryRequestStream();
        var responses = new ChannelResponseReader();
        var stream = BuildFaultingStream(requests, responses);
        var connection = TestConnectionFactory.CreateUnpublished("worker-delivered", stream);
        var service = new WorkerService("http://localhost:9999", "worker-delivered", ["coder"], "/config-repo");
        try
        {
            // THE FABRICATED DELIVERED OWNER, built through the production types: the same
            // construction the assignment handler performs, then driven to Delivered through
            // the production CAS transitions (Carried, then Delivered).
            var serviceType = typeof(WorkerService);
            var holderType = serviceType.GetNestedType("TerminalResultHolder", BindingFlags.NonPublic)!;
            var readyType = serviceType.GetNestedType("ReadyClaim", BindingFlags.NonPublic)!;
            var receiptType = serviceType.GetNestedType("CompletionReceiptTracker", BindingFlags.NonPublic)!;
            var ownerType = serviceType.GetNestedType("ActiveAssignment", BindingFlags.NonPublic)!;
            var holder = Activator.CreateInstance(holderType, nonPublic: true)!;
            var readyClaim = Activator.CreateInstance(readyType, nonPublic: true)!;
            var receipt = Activator.CreateInstance(
                receiptType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [connection],
                culture: null)!;
            var ordinaryReady = NewOrdinaryReadySlot(connection, CancellationToken.None, readyClaim);
            var ownerCts = new CancellationTokenSource();
            var owner = Activator.CreateInstance(
                ownerType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args:
                [
                    "task-A",
                    Task.CompletedTask,
                    Task.CompletedTask,
                    null,
                    ownerCts,
                    readyClaim,
                    holder,
                    receipt,
                    ordinaryReady,
                ],
                culture: null)!;
            var state = owner.GetType().GetProperty("State")!.GetValue(owner)!;
            Assert.True(
                (bool)state.GetType().GetMethod("TryCarry")!.Invoke(state, null)!,
                "The fabricated owner must reach Carried first.");
            Assert.True(
                (bool)state.GetType().GetMethod("TryDeliver")!.Invoke(state, null)!,
                "The fabricated owner must reach Delivered.");
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));

            // INSTALL the owner — this is the ONE state injection this suite performs, and it
            // models the retained Delivered assignment the real flow always produces before a
            // reporter's finally could observe it.
            serviceType
                .GetField("_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, owner);
            serviceType
                .GetField("_currentTaskId", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "task-A");
            serviceType
                .GetField("_currentRole", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "coder");

            // Drive the REAL reporting with a normally-completed (empty-result) execution.
            var domainTask = GrpcMapper.ToDomain(ResultAssignment("task-A").Assignment);
            var reporting = (Task)serviceType
                .GetMethod("ReportAssignmentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service,
                [
                    Task.CompletedTask,
                    domainTask,
                    connection,
                    holder,
                    receipt,
                    ordinaryReady,
                ])!;

            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(reporting.IsCompletedSuccessfully);

            // CLEARED AND NOT RESURRECTED: the recheck only restores a CARRIED assignment, so
            // the Delivered one stays cleared.
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Null(GetHeartbeatRole(service));

            // The settlement loser: no ordinary Ready and the claim is unconsumed.
            Assert.Null(InvokeSettleOrdinaryReady(service, ordinaryReady));
            Assert.Equal(0, GetReadyClaimState(readyClaim));
            Assert.Equal(0, requests.ReadyCount);
        }
        finally
        {
            typeof(WorkerService)
                .GetField("_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, null);
            connection.Retire();
            responses.TryComplete();
            requests.ReleaseAll();
            service.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. The exit re-check after the stream disposal.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE EXIT RE-CHECK: a run whose retained assignment is NOT Carried when
    /// <see cref="WorkerService.RunAsync"/> unwinds has it cancelled, joined (including the
    /// CarriedDelivery it owns) and CLEARED before <c>RunAsync</c> returns — observed here on a
    /// DELIVERED assignment.
    /// </summary>
    [Fact]
    public async Task ExitReCheck_ClearsNonCarriedRetainedAssignmentBeforeRunAsyncReturns()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? carriedDelivery = null;
        try
        {
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // Carry at the stream loss; the delivery will be parked awaiting an adoption.
            // ARM THE DISPOSAL HOLD FIRST: the run parks between the loop's carry teardown and
            // the exit re-check — the deterministic window in which the delivery can finish
            // while the assignment is STILL RETAINED. (Arming late would race the disposal.)
            harness.ArmDisposeHold();
            responses.TryComplete();
            carriedDelivery = await WaitForCarriedDeliveryAsync(
                service, "The assignment must be carried first.");
            var ownerCts = GetOwnerCts(service);
            Assert.Equal(1, GetSlotOccupancy(service));

            // The adopted-run shape completes the delivery while the run is held.
            harness.AdoptConnection();
            harness.Runner.Release("task-A");
            await carriedDelivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // DELIVERED and retained INSIDE the disposal-hold window: the loop's teardown has
            // already returned, and the exit re-check has not run yet.
            var owner = GetActiveAssignment(service);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));
            Assert.False(
                harness.Run.IsCompleted,
                "The run must still be held inside its stream disposal.");

            // RELEASE: the exit re-check in RunAsync's finally — which runs strictly AFTER the
            // stream disposal — must cancel, join (including the delivery) and CLEAR the
            // Delivered assignment before RunAsync returns.
            harness.ReleaseDisposeHold();
            await harness.JoinRunAsync();

            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Null(GetHeartbeatRole(service));
            Assert.True(carriedDelivery.IsCompleted, "The exit re-check joined the carried delivery.");
            Assert.True(IsDisposed(ownerCts), "The exit re-check disposed the assignment's CTS.");

            // The delivery happened exactly once, on the adopted connection.
            var adoptedRequests = harness.AdoptedRequests!;
            var complete = Assert.Single(adoptedRequests.Completes);
            Assert.Equal("task-A", complete.Complete.TaskId);
            Assert.Equal(1, adoptedRequests.AssignmentReadyCount);
        }
        finally
        {
            harness.ReleaseDisposeHold();
            await CarryHarness.TeardownAsync(harness, execution, reporting, carriedDelivery);
        }
    }

    /// <summary>
    /// THE EXIT RE-CHECK LEAVES A CARRIED ASSIGNMENT ALONE: a run that ends with the assignment
    /// still Carried (its delivery parked awaiting an adoption) returns
    /// <see cref="WorkerRunOutcome.WorkStreamEnded"/> WITHOUT cancelling, joining or clearing the
    /// retained assignment — it survives into the next sequential run.
    /// </summary>
    [Fact]
    public async Task ExitReCheck_LeavesACarriedAssignmentRetainedForTheNextRun()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? carriedDelivery = null;
        try
        {
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            responses.TryComplete();
            carriedDelivery = await WaitForCarriedDeliveryAsync(
                service, "The assignment must be carried.");

            // The run ends while the delivery is STILL PARKED awaiting an adoption.
            await harness.JoinRunAsync();

            // SURVIVED: the slot stays occupied, the delivery task is still pending, the
            // heartbeat state is still the carried task's, and the execution was NOT cancelled.
            var owner = GetActiveAssignment(service);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));
            Assert.False(
                carriedDelivery.IsCompleted,
                "The carried delivery must still be parked awaiting an adoption.");
            Assert.False(harness.Runner.WasCancelled("task-A"));
            Assert.Equal("task-A", GetHeartbeatTaskId(service));
            Assert.Equal("coder", GetHeartbeatRole(service));
            Assert.True(harness.Connection.IsRetired);

            // The delivery is now finished by the explicit final drain (the process cleanup
            // shape), which cancels and drains the retained Carried assignment.
            await service.DrainCarriedAssignmentAsync();
            Assert.Null(GetActiveAssignmentOrNull(service));
            Assert.True(carriedDelivery.IsCompleted);
            Assert.Null(GetHeartbeatTaskId(service));
        }
        finally
        {
            await CarryHarness.TeardownAsync(harness, execution, reporting, carriedDelivery);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5. DrainCarriedAssignmentAsync — the process's final cleanup drain.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="WorkerService.DrainCarriedAssignmentAsync"/> cancels and drains a RETAINED
    /// Carried assignment — including its owned CarriedDelivery — and clears the ownership slot
    /// and the heartbeat state. The drained body's own settlement cannot claim (the CAS lost:
    /// Carried is not Open), and the drain itself emits NO fallback Ready, so the ONLY Ready is
    /// the initial one.
    /// </summary>
    [Fact]
    public async Task DrainCarriedAssignmentAsync_CancelsAndDrainsRetainedAssignmentIncludingItsDelivery()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var requests = harness.Requests;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? carriedDelivery = null;
        try
        {
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            responses.TryComplete();
            carriedDelivery = await WaitForCarriedDeliveryAsync(
                service, "The assignment must be carried.");

            // THE FINAL DRAIN on the retained Carried assignment.
            await service.DrainCarriedAssignmentAsync();

            // The slot is empty, the heartbeat state is cleared, the body was cancelled, and the
            // owned delivery was joined (it is complete).
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Null(GetHeartbeatRole(service));
            Assert.True(harness.Runner.WasCancelled("task-A"), "The drain cancels the retained assignment.");
            Assert.True(carriedDelivery.IsCompleted, "The drain joins the owned CarriedDelivery.");

            // NO ASSIGNMENT READY AT ALL: the drained body's settlement lost the CAS (Carried is
            // not Open) and claimed nothing, and the final drain emits no fallback Ready of its
            // own — exactly ONE Ready total (the initial one).
            Assert.Equal(0, requests.AssignmentReadyCount);

            // NO COMPLETE EVER WROTE: the body was cancelled with no result, so no delivery was
            // possible, and a carried assignment is delivered only through the carried delivery
            // on an ADOPTED connection — never by the final drain.
            Assert.Empty(requests.Completes);
        }
        finally
        {
            await CarryHarness.TeardownAsync(harness, execution, reporting, carriedDelivery);
        }
    }

    /// <summary>
    /// <see cref="WorkerService.DrainCarriedAssignmentAsync"/> is a NO-OP when nothing is
    /// retained — the caller may invoke it unconditionally (the Program cleanup shape).
    /// </summary>
    [Fact]
    public async Task DrainCarriedAssignmentAsync_WithNothingRetained_IsANoOp()
    {
        var service = new WorkerService("http://localhost:9999", "worker-1", ["coder"], "/config-repo");
        try
        {
            await service.DrainCarriedAssignmentAsync();
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
        }
        finally
        {
            service.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6. The two internal test hooks — exact shapes, defaults, awaited points.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE TWO TEST HOOKS EXIST WITH THE EXACT SHAPES and are NULL BY DEFAULT on a fresh service.
    /// <see cref="WorkerService.CarriedBeforeCompleteSendHook"/> is
    /// <c>Func&lt;CancellationToken, Task&gt;?</c> and
    /// <see cref="WorkerService.CarriedBeforeReadyClaimHook"/> is <c>Func&lt;Task&gt;?</c>.
    /// </summary>
    [Fact]
    public void TestHooks_ExistWithExactShapes_AndAreNullByDefault()
    {
        var completeHook = typeof(WorkerService).GetProperty(
            "CarriedBeforeCompleteSendHook", BindingFlags.NonPublic | BindingFlags.Instance);
        var readyHook = typeof(WorkerService).GetProperty(
            "CarriedBeforeReadyClaimHook", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(completeHook);
        Assert.NotNull(readyHook);
        Assert.True(completeHook!.CanWrite, "CarriedBeforeCompleteSendHook must be settable.");
        Assert.True(readyHook!.CanWrite, "CarriedBeforeReadyClaimHook must be settable.");
        Assert.Equal(typeof(Func<CancellationToken, Task>), completeHook.PropertyType);
        Assert.Equal(typeof(Func<Task>), readyHook.PropertyType);

        var service = new WorkerService("http://localhost:9999", "worker-1", ["coder"], "/config-repo");
        try
        {
            Assert.Null(completeHook.GetValue(service));
            Assert.Null(readyHook.GetValue(service));
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>
    /// <see cref="WorkerService.CarriedBeforeCompleteSendHook"/> is AWAITED by the carried
    /// delivery IMMEDIATELY BEFORE its Complete <c>SendAsync</c>, and receives the ASSIGNMENT
    /// token that send then uses. Observed from inside the hook: the Complete has NOT yet been
    /// written, the token observed is the assignment's, and the delivery is parked INSIDE the
    /// hook (it is awaited, not fire-and-forget).
    /// </summary>
    [Fact]
    public async Task CarriedBeforeCompleteSendHook_IsAwaitedBeforeTheCompleteSend_WithTheAssignmentToken()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? carriedDelivery = null;
        var hookRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // Carry at EOF, then publish the adopted connection so the delivery has a target.
            responses.TryComplete();
            carriedDelivery = await WaitForCarriedDeliveryAsync(
                service, "The assignment must be carried.");
            var ownerCts = GetOwnerCts(service);
            var adoptedRequests = harness.AdoptConnection() is { } adopted ? harness.AdoptedRequests! : throw new Xunit.Sdk.XunitException("Adoption failed.");
            harness.Runner.Release("task-A");

            CancellationToken? observedToken = null;
            var completesAtHook = -1;
            var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.CarriedBeforeCompleteSendHook = token =>
            {
                completesAtHook = adoptedRequests.Completes.Count;
                observedToken = token;
                hookEntered.TrySetResult();
                return hookRelease.Task;
            };

            await hookEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE HOOK'S EXACT CONTRACT: NO Complete has been written yet, the token observed is
            // the ASSIGNMENT's token (the one the send below will use), and the delivery is
            // parked INSIDE the awaited hook.
            Assert.Equal(0, Volatile.Read(ref completesAtHook));
            Assert.True(
                observedToken!.Value.CanBeCanceled,
                "The hook must receive the assignment's cancellable token.");
            Assert.Equal(ownerCts.Token, observedToken.Value);
            Assert.False(
                carriedDelivery.IsCompleted,
                "The delivery must be parked INSIDE the awaited hook.");

            // Release the hook: the Complete send follows on the adopted connection, then the
            // carried Ready.
            hookRelease.TrySetResult();
            await carriedDelivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var complete = Assert.Single(adoptedRequests.Completes);
            Assert.Equal("task-A", complete.Complete.TaskId);
            Assert.Equal(1, adoptedRequests.AssignmentReadyCount);
        }
        finally
        {
            hookRelease.TrySetResult();
            service.CarriedBeforeCompleteSendHook = null;
            await CarryHarness.TeardownAsync(harness, execution, reporting, carriedDelivery);
        }
    }

    /// <summary>
    /// <see cref="WorkerService.CarriedBeforeReadyClaimHook"/> is AWAITED by the carried delivery
    /// AFTER its Complete write succeeded and BEFORE its Ready claim. Observed from inside the
    /// hook: the Complete is already on the adopted connection, NO Ready has been written yet,
    /// and the shared Ready claim is still unconsumed.
    /// </summary>
    [Fact]
    public async Task CarriedBeforeReadyClaimHook_IsAwaitedAfterTheCompleteAndBeforeTheReadyClaim()
    {
        var harness = CarryHarness.Create();
        var service = harness.Service;
        var responses = harness.Responses;

        Task? execution = null;
        Task? reporting = null;
        Task? carriedDelivery = null;
        var hookRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            responses.Push(ResultAssignment("task-A"));
            await harness.Runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            responses.TryComplete();
            carriedDelivery = await WaitForCarriedDeliveryAsync(
                service, "The assignment must be carried.");
            var owner = GetActiveAssignment(service);
            var adoptedRequests = harness.AdoptConnection() is { } adopted ? harness.AdoptedRequests! : throw new Xunit.Sdk.XunitException("Adoption failed.");
            harness.Runner.Release("task-A");

            var completesAtHook = -1;
            var readiesAtHook = -1;
            var claimStateAtHook = -1;
            var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.CarriedBeforeReadyClaimHook = () =>
            {
                completesAtHook = adoptedRequests.Completes.Count;
                readiesAtHook = adoptedRequests.AssignmentReadyCount;
                claimStateAtHook = GetReadyClaimState(GetOwnerReadyClaim(owner));
                hookEntered.TrySetResult();
                return hookRelease.Task;
            };

            await hookEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE HOOK'S EXACT CONTRACT: the Complete is ALREADY WRITTEN, no assignment Ready has
            // been written, and the shared Ready claim is still unconsumed.
            Assert.Equal(1, Volatile.Read(ref completesAtHook));
            Assert.Equal(0, Volatile.Read(ref readiesAtHook));
            Assert.Equal(0, Volatile.Read(ref claimStateAtHook));

            // Release the hook: the claim is won and the single carried Ready follows.
            hookRelease.TrySetResult();
            await carriedDelivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, adoptedRequests.AssignmentReadyCount);

            // DELIVERED with the started-write fact set.
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(GetActiveAssignment(service)));
            Assert.True(GetCarriedReadyStarted(GetActiveAssignment(service)));
        }
        finally
        {
            hookRelease.TrySetResult();
            service.CarriedBeforeReadyClaimHook = null;
            await CarryHarness.TeardownAsync(harness, execution, reporting, carriedDelivery);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Harness.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The AssignmentState cell's private constants, mirrored for readable assertions.</summary>
    private static class CarryStates
    {
        internal const int Open = 0;
        internal const int ReadyStarted = 1;
        internal const int Carried = 2;
        internal const int Delivered = 3;
    }

    /// <summary>
    /// THE CARRY HARNESS: one service, one REAL sequential run, a fake duplex stream whose
    /// pending writes fault on dispose and honor write tokens, and the reflection observers the
    /// focused tests share. The run starts in <see cref="Create"/>, so every test observes the
    /// real <see cref="WorkerService.RunAsync"/> lifecycle (registration, publication, heartbeat
    /// seam, initial Ready) rather than a synthetic publication.
    /// </summary>
    private sealed class CarryHarness
    {
        private const string WorkerId = "worker-reconnect";
        private const string AssignedId = "worker-reconnect-a";
        private const string ConfigRepoUrl = "https://github.com/org/config-repo.git";

        private CarryHarness(
            WorkerService service,
            CarryRequestStream requests,
            ChannelResponseReader responses,
            CarryPromptRunner runner,
            WorkerConnection connection,
            ScriptedInvoker invoker,
            CancellationToken streamToken,
            Task<WorkerRunOutcome> run,
            TaskCompletionSource disposeHold,
            int[] disposeHoldArmed)
        {
            Service = service;
            Requests = requests;
            Responses = responses;
            Runner = runner;
            Connection = connection;
            Invoker = invoker;
            StreamToken = streamToken;
            Run = run;
            _disposeHold = disposeHold;
            _disposeHoldArmed = disposeHoldArmed;
        }

        internal WorkerService Service { get; }
        internal CarryRequestStream Requests { get; }
        internal ChannelResponseReader Responses { get; }
        internal CarryPromptRunner Runner { get; }
        internal WorkerConnection Connection { get; }
        internal ScriptedInvoker Invoker { get; }

        /// <summary>
        /// THE ADOPTING RUN'S STREAM TOKEN — captured from the <c>WorkStreamFactory</c> seam, so
        /// an adoption published by <see cref="AdoptConnection"/> carries exactly the token
        /// production's reconnect path will hand <c>PublishAdoption</c> in round 2.
        /// </summary>
        internal CancellationToken StreamToken { get; }

        internal Task<WorkerRunOutcome> Run { get; }

        /// <summary>
        /// ONE REAL SEQUENTIAL RUN on a fresh service: builds the service through the EXACT
        /// internal attempt-construction shape, installs the gated runner through the same
        /// reflection seam the other fixtures use, wires the fake invoker + stream, and starts
        /// <see cref="WorkerService.RunAsync"/>. The initial Ready is EXPECTED and is awaited
        /// here, so a test's write assertions never count it.
        /// </summary>
        internal static CarryHarness Create()
        {
            var service = new WorkerService(
                "http://localhost:9999", WorkerId, ["coder"], "/config-repo");
            var runner = new CarryPromptRunner();
            InstallRunner(service, runner);

            var requests = new CarryRequestStream();
            var responses = new ChannelResponseReader();
            var disposeHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposeHoldArmed = new int[1];
            var stream = BuildFaultingStream(requests, responses, () =>
            {
                // THE STREAM-DISPOSAL HOLD. A test that arms it parks the run exactly between
                // the loop's carry teardown and RunAsync's exit re-check — the deterministic
                // window in which the delivery can complete BEFORE the re-check runs. A test
                // that never arms it gets today's immediate disposal.
                if (Volatile.Read(ref disposeHoldArmed[0]) != 0)
                    disposeHold.Task.GetAwaiter().GetResult();
            });
            var invoker = new ScriptedInvoker(RegisterFor(AssignedId));
            service.CallInvokerFactory = () => invoker;

            var streamToken = CancellationToken.None;
            service.WorkStreamFactory = (_, ct) =>
            {
                streamToken = ct;
                return stream;
            };
            service.HeartbeatTaskFactory = (_, _) => Task.CompletedTask;

            var run = service.RunAsync(TestContext.Current.CancellationToken);

            // The initial Ready is EXPECTED and is awaited here, so every later write assertion
            // is about the ASSIGNMENT's own writes only.
            try
            {
                requests.WaitForWriteCountAsync(1)
                    .WaitAsync(Failsafe, TestContext.Current.CancellationToken)
                    .GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // A harness failure must never strand the run: unwind through the same teardown
                // shape the tests use, so no live producer survives a failed Create().
                responses.TryComplete();
                requests.ReleaseAll();
                runner.ReleaseAll();
                try
                {
                    run.WaitAsync(Failsafe, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // The run's own outcome is not the fixture's concern here.
                }

                TryDispose(service);
                throw;
            }

            var connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            return new CarryHarness(
                service, requests, responses, runner, connection, invoker, streamToken, run,
                disposeHold, disposeHoldArmed);
        }

        /// <summary>
        /// THE ADOPTED-CONNECTION SHAPE — what round 2's reconnect path does after an
        /// adopted_task == true registration: it publishes the adoption for the NEW connection
        /// it just built, with the NEW run's stream token. The harness builds that adopted
        /// connection over a FRESH fake stream (an unpublished second connection, exactly like
        /// the existing <c>TestConnectionFactory.CreateUnpublished</c> shape) and publishes the
        /// adoption through the production publication seam.
        /// <para>
        /// The ORIGINAL connection is deliberately NOT usable: production refuses an adoption
        /// publication for a retired connection (<c>AwaitAdoptedConnectionAsync</c> filters
        /// retired candidates), so a carried delivery can only ever be handed a live one.
        /// </para>
        /// </summary>
        internal WorkerConnection AdoptConnection()
        {
            var adoptedRequests = new CarryRequestStream { CountsFirstReadyAsInitial = false };
            var adoptedResponses = new ChannelResponseReader();
            var adoptedStream = BuildFaultingStream(adoptedRequests, adoptedResponses);
            var adoptedConnection = new WorkerConnection(
                Connection.AssignedId,
                new HiveOrchestrator.HiveOrchestratorClient(
                    GrpcChannel.ForAddress("http://localhost:9999")),
                adoptedStream,
                provisionerOverride: null,
                includeProductionProvisioner: false,
                provisioningEnvironment: null,
                completionReceiptAckEnabled: false,
                completionReadyRequired: false);
            adoptedResponses.TryComplete();
            adoptedRequests.ReleaseAll();

            PublishAdoptionDirect(Service, adoptedConnection, StreamToken);
            Adopted = adoptedConnection;
            AdoptedRequests = adoptedRequests;
            return adoptedConnection;
        }

        /// <summary>The connection the harness published the adoption for, or <c>null</c>.</summary>
        internal WorkerConnection? Adopted { get; private set; }

        /// <summary>The adopted connection's write fake, or <c>null</c>.</summary>
        internal CarryRequestStream? AdoptedRequests { get; private set; }

        /// <summary>
        /// THE STREAM-DISPOSAL HOLD for tests that must complete work BETWEEN the loop's carry
        /// teardown and RunAsync's exit re-check: <see cref="ArmDisposeHold"/> parks the run
        /// inside the stream's disposal callback (strictly after the loop returned, strictly
        /// before the exit re-check), and <see cref="ReleaseDisposeHold"/> releases it. Unarmed,
        /// disposal is immediate — every other test's behavior is unchanged.
        /// </summary>
        internal void ArmDisposeHold()
        {
            Volatile.Write(ref _disposeHoldArmed[0], 1);
        }

        internal void ReleaseDisposeHold() => _disposeHold.TrySetResult();

        private readonly int[] _disposeHoldArmed = new int[1];
        private readonly TaskCompletionSource _disposeHold =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Joins the run and ASSERTS its outcome, so every carried test observes the SAME
        /// contract: the run returns <see cref="WorkerRunOutcome.WorkStreamEnded"/> with the
        /// retained assignment left to the caller's assertions.
        /// </summary>
        internal async Task<WorkerRunOutcome> JoinRunAsync()
        {
            var outcome = await Run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(RunCompletion, outcome);
            return outcome;
        }

        /// <summary>
        /// TEARDOWN: release every gate, end the reader, drain any retained assignment (the
        /// process cleanup shape) and join every producer under the bounded failsafe — even when
        /// an assertion failed.
        /// </summary>
        internal static async Task TeardownAsync(
            CarryHarness harness,
            Task? execution,
            Task? reporting,
            Task? carriedDelivery)
        {
            harness.ReleaseDisposeHold();
            harness.Responses.TryComplete();
            harness.Requests.ReleaseAll();
            harness.Runner.ReleaseAll();
            Exception? primary = null;
            try { await harness.Service.DrainCarriedAssignmentAsync(); }
            catch (Exception ex) { primary = ex; }
            try
            {
                await JoinAllForTeardownAsync(
                    ("assignment execution", execution),
                    ("assignment reporting", reporting),
                    ("carried delivery", carriedDelivery),
                    ("run", harness.Run));
            }
            catch (Exception ex) { primary ??= ex; }
            finally { TryDispose(harness.Service); }
            if (primary is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }

    // ── Observation seams ─────────────────────────────────────────────────────

    /// <summary>
    /// BOUNDED ARRIVAL OBSERVATION for the carried delivery — the ONE fact the carry path
    /// publishes without a dedicated signal. This is the SAME observation-only pattern the
    /// shared <c>SendGateObserver</c> uses: a cooperative yield loop whose bound is a FAILURE
    /// GUARD only (the wait returns as soon as the delivery exists), so a mutant that never
    /// carries fails BY NAME within the bound instead of hanging the suite.
    /// </summary>
    private static async Task<Task> WaitForCarriedDeliveryAsync(WorkerService service, string because)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var delivery = GetCarriedDeliveryOrNull(service);
            if (delivery is not null)
                return delivery;

            if (stopwatch.Elapsed > Failsafe)
                throw new Xunit.Sdk.XunitException(
                    $"{because} (no CarriedDelivery appeared within the failsafe bound).");

            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    /// <summary>
    /// BOUNDED ARRIVAL OBSERVATION for the early retire: the read-await site's retirement is an
    /// async consequence of the EOF, so the test waits for the fact to appear instead of racing
    /// it. The bound is a FAILURE GUARD only.
    /// </summary>
    private static async Task WaitForRetiredAsync(WorkerConnection connection, string because)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!connection.IsRetired)
        {
            if (stopwatch.Elapsed > Failsafe)
                throw new Xunit.Sdk.XunitException(
                    $"{because} (the connection was not retired within the failsafe bound).");

            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static WorkerConnection? GetPublishedConnection(WorkerService service) =>
        (WorkerConnection?)typeof(WorkerService)
            .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    private static object? GetActiveAssignmentOrNull(WorkerService service) =>
        typeof(WorkerService)
            .GetField("_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    private static object GetActiveAssignment(WorkerService service) =>
        GetActiveAssignmentOrNull(service)
            ?? throw new Xunit.Sdk.XunitException("Expected a retained assignment owner.");

    private static Task GetActiveExecution(WorkerService service)
    {
        var active = GetActiveAssignment(service);
        return (Task)active.GetType().GetProperty("Execution")!.GetValue(active)!;
    }

    private static Task GetActiveReporting(WorkerService service)
    {
        var active = GetActiveAssignment(service);
        return (Task)active.GetType().GetProperty("Reporting")!.GetValue(active)!;
    }

    private static TaskResult? GetRetainedResult(WorkerService service)
    {
        var active = GetActiveAssignment(service);
        var holder = active.GetType().GetProperty("TerminalResult")!.GetValue(active)!;
        return (TaskResult?)holder.GetType().GetProperty("Result")!.GetValue(holder);
    }

    private static CancellationTokenSource GetOwnerCts(WorkerService service)
    {
        var active = GetActiveAssignment(service);
        return (CancellationTokenSource)active.GetType().GetProperty("Cts")!.GetValue(active)!;
    }

    private static string GetOwnerTaskId(object owner) =>
        (string)owner.GetType().GetProperty("TaskId")!.GetValue(owner)!;

    private static string GetOwnerRole(object owner) =>
        (string)owner.GetType().GetProperty("Role")!.GetValue(owner)!;

    private static object GetOwnerOrdinaryReady(object owner) =>
        owner.GetType().GetProperty("OrdinaryReady")!.GetValue(owner)!;

    private static object GetOwnerReadyClaim(object owner) =>
        owner.GetType().GetProperty("Ready")!.GetValue(owner)!;

    private static int GetAssignmentState(object owner)
    {
        var state = owner.GetType().GetProperty("State")!.GetValue(owner)!;
        return (int)state.GetType().GetProperty("Value")!.GetValue(state)!;
    }

    private static bool GetCarriedReadyStarted(object owner) =>
        (bool)owner.GetType().GetProperty("CarriedReadyStarted")!.GetValue(owner)!;

    private static Task? GetCarriedDeliveryOrNull(WorkerService service)
    {
        var active = GetActiveAssignmentOrNull(service);
        return active is null
            ? null
            : (Task?)active.GetType().GetProperty("CarriedDelivery")!.GetValue(active);
    }

    private static int GetSlotOccupancy(WorkerService service) =>
        GetActiveAssignmentOrNull(service) is null ? 0 : 1;

    private static string? GetHeartbeatTaskId(WorkerService service) =>
        (string?)typeof(WorkerService)
            .GetField("_currentTaskId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    private static string? GetHeartbeatRole(WorkerService service) =>
        (string?)typeof(WorkerService)
            .GetField("_currentRole", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    private static Task GetRetainedReadinessWrite(WorkerService service, string because)
    {
        var active = GetActiveAssignment(service);
        var slot = active.GetType().GetProperty("OrdinaryReady")!.GetValue(active)!;
        var write = (Task?)slot.GetType().GetProperty("Write")!.GetValue(slot);
        return write ?? throw new Xunit.Sdk.XunitException(because);
    }

    private static int GetReadyClaimState(object readyClaim) =>
        (int)readyClaim.GetType()
            .GetField("_claimed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(readyClaim)!;

    /// <summary>
    /// Invokes the production settlement of an ordinary-readiness slot — the ONE entry point
    /// every ordinary-Ready path uses — so the CAS settlement rule is exercised through the REAL
    /// transition, not a copy.
    /// </summary>
    private static Task? InvokeSettleOrdinaryReady(WorkerService service, object slot) =>
        (Task?)typeof(WorkerService)
            .GetMethod("SettleOrdinaryReady", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [slot]);

    /// <summary>
    /// Constructs the production ordinary-readiness slot the assignment handler itself creates,
    /// for the direct-reporting vector that does not run a loop.
    /// </summary>
    private static object NewOrdinaryReadySlot(
        WorkerConnection connection, CancellationToken token, object readyClaim) =>
        Activator.CreateInstance(
            typeof(WorkerService).GetNestedType("OrdinaryReadySlot", BindingFlags.NonPublic)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [connection, token, readyClaim],
            culture: null)!;

    /// <summary>
    /// THE ADOPTED-CONNECTION OBSERVER for the reconnect tests: the run's OWN adoption record,
    /// read through reflection (observation only).
    /// </summary>
    private static WorkerConnection? GetAdoptedConnectionOrNull(WorkerService service)
    {
        var adopted = typeof(WorkerService)
            .GetField("_adopted", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);
        return adopted is null
            ? null
            : (WorkerConnection)adopted.GetType().GetProperty("Connection")!.GetValue(adopted)!;
    }

    private static WorkerConnection GetAdoptedConnection(WorkerService service) =>
        GetAdoptedConnectionOrNull(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an adopted connection record.");

    /// <summary>
    /// Publishes an adoption DIRECTLY through the production publication method (reflection) —
    /// the exact call the reconnect path will make in round 2.
    /// </summary>
    private static void PublishAdoptionDirect(
        WorkerService service, WorkerConnection connection, CancellationToken streamToken) =>
        typeof(WorkerService)
            .GetMethod("PublishAdoption", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [connection, streamToken]);

    private static void InstallRunner(WorkerService service, IAgentRunner runner)
    {
        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);
    }

    private static RegisterResponse RegisterFor(string assignedId) =>
        new() { Accepted = true, AssignedWorkerId = assignedId, OrchestratorVersion = "test" };

    private static OrchestratorMessage ResultAssignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = $"goal-{taskId}",
            GoalDescription = $"desc for {taskId}",
            Prompt = "do the thing",
            Role = GrpcWorkerRole.Coder,
            Model = "fixture-provider/fixture-model",
        },
    };

    private static AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> BuildFaultingStream(
        CarryRequestStream requests, ChannelResponseReader responses, Action? onDisposed = null) =>
        new(requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ =>
            {
                // FAULT PENDING WRITES ON DISPOSE: the transport disposal cancels the call, so
                // every write parked inside the fake unwinds with OperationCanceledException —
                // exactly what gRPC's Dispose contract promises ("requests cancellation of the
                // call which should terminate all pending async operations"). A tolerated
                // pre-loss write therefore keeps the carried delivery's STEP 0 observation
                // convergent.
                requests.FaultPendingWritesOnDispose();
                onDisposed?.Invoke();
            },
            null!);

    private static bool IsDisposed(CancellationTokenSource source)
    {
        try
        {
            _ = source.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static void TryDispose(WorkerService service)
    {
        try
        {
            service.Dispose();
        }
        catch
        {
            // Best effort: a teardown fault must never mask the test's own failure.
        }
    }

    /// <summary>
    /// Joins every started producer under the bounded failsafe, independently, collecting the
    /// failures — the SAME teardown shape the ownership fixtures use.
    /// </summary>
    private static async Task JoinAllForTeardownAsync(
        params (string Name, Task? Producer)[] producers)
    {
        List<Exception> failures = [];
        foreach (var (name, producer) in producers)
        {
            if (producer is null)
                continue;

            try
            {
                await producer.WaitAsync(Failsafe, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                failures.Add(new Xunit.Sdk.XunitException(
                    $"Teardown failed to join '{name}' within the bounded failsafe; live work remains."));
            }
            catch (Exception) when (producer.IsCompleted)
            {
                // Terminal fault/cancellation: the original task is quiescent.
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Teardown could not join every original task.", failures);
    }

    /// <summary>
    /// THE WRITE FAKE for this fixture: records every write, gates Complete and assignment Ready
    /// writes independently by index, honors write tokens (a parked write unwinds with
    /// <see cref="OperationCanceledException"/> when its forwarded token is cancelled — the
    /// dispose-faults-pending-writes contract), and supports one-shot injected failures. The
    /// INITIAL Ready is recorded too but never gated: the assignment's writes are indexed
    /// separately from it.
    /// </summary>
    private sealed class CarryRequestStream : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _completes = [];
        private readonly List<WorkerMessage> _readies = [];
        private readonly List<WorkerMessage> _writes = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _writeCountWaiters = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyCountWaiters = [];
        private bool _releaseImmediately;
        private bool _disposed;
        private int _holdCompletesFrom = int.MaxValue;
        private int _holdReadiesFrom = int.MaxValue;
        private Exception? _failNextCompleteWrite;

        /// <summary>
        /// From which COMPLETE index onward writes park until released. The reporter's old-
        /// connection Complete is index 0, the carried delivery's is index 1, and so on.
        /// <see cref="int.MaxValue"/> (the default) holds nothing.
        /// </summary>
        internal int HoldCompletesFrom
        {
            get { lock (_gate) return _holdCompletesFrom; }
            set { lock (_gate) _holdCompletesFrom = value; }
        }

        /// <summary>
        /// From which assignment-READY ordinal onward writes park until released. The initial
        /// registration Ready is ordinal -1 (never gated); the assignment's first ordinary or
        /// carried Ready is ordinal 0. <see cref="int.MaxValue"/> (the default) holds nothing.
        /// </summary>
        internal int HoldReadiesFrom
        {
            get { lock (_gate) return _holdReadiesFrom; }
            set { lock (_gate) _holdReadiesFrom = value; }
        }

        internal IReadOnlyList<WorkerMessage> Completes
        {
            get { lock (_gate) return [.. _completes]; }
        }

        internal IReadOnlyList<WorkerMessage> Readies
        {
            get { lock (_gate) return [.. _readies]; }
        }

        internal IReadOnlyList<WorkerMessage> Writes
        {
            get { lock (_gate) return [.. _writes]; }
        }

        /// <summary>
        /// Whether this stream's FIRST Ready is the run's initial registration Ready (true for
        /// the ORIGINAL connection's fake; false for an adopted connection, which never sends an
        /// initial Ready). The assignment-Ready accounting uses it.
        /// </summary>
        internal bool CountsFirstReadyAsInitial { get; set; } = true;

        internal int ReadyCount { get { lock (_gate) return _readies.Count; } }

        /// <summary>Assignment-owned Readies written so far (the initial registration one excluded).</summary>
        internal int AssignmentReadyCount
        {
            get { lock (_gate) return Math.Max(0, _readies.Count - (CountsFirstReadyAsInitial ? 1 : 0)); }
        }

        internal Exception? FailNextCompleteWrite
        {
            get { lock (_gate) return _failNextCompleteWrite; }
            set { lock (_gate) _failNextCompleteWrite = value; }
        }

        /// <summary>
        /// ONE-SHOT injected failure for the NEXT assignment-Ready write, applied AFTER the
        /// message was recorded (so the attempt still counts). Models the carried Ready write
        /// failing on the adopted connection.
        /// </summary>
        internal Exception? FailNextReadyWrite
        {
            get { lock (_gate) return _failNextReadyWrite; }
            set { lock (_gate) _failNextReadyWrite = value; }
        }

        private Exception? _failNextReadyWrite;

        /// <summary>Completes once at least <paramref name="count"/> writes of any kind landed.</summary>
        internal Task WaitForWriteCountAsync(int count)
        {
            lock (_gate)
            {
                if (_writes.Count >= count)
                    return Task.CompletedTask;
                return WriteCountWaiterLocked(count).Task;
            }
        }

        /// <summary>Completes once at least <paramref name="count"/> ASSIGNMENT Readies landed.</summary>
        internal Task WaitForAssignmentReadyCountAsync(int count)
        {
            lock (_gate)
            {
                var threshold = count + (CountsFirstReadyAsInitial ? 1 : 0);
                if (_readies.Count >= threshold)
                    return Task.CompletedTask;
                if (!_readyCountWaiters.TryGetValue(threshold, out var waiter))
                    _readyCountWaiters[threshold] = waiter =
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return waiter.Task;
            }
        }

        private TaskCompletionSource WriteCountWaiterLocked(int threshold)
        {
            if (!_writeCountWaiters.TryGetValue(threshold, out var waiter))
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _writeCountWaiters[threshold] = waiter;
            }

            return waiter;
        }

        internal Task AssignmentCompleteEntered(int index)
        {
            lock (_gate)
                return _completes.Count > index
                    ? Task.CompletedTask
                    : Slot(_completeEntered, index).Task;
        }

        /// <summary>Completes once the <paramref name="ordinal"/>-th ASSIGNMENT Ready ENTERED the fake.</summary>
        internal Task AssignmentReadyEntered(int ordinal)
        {
            lock (_gate)
                return _readies.Count > ordinal + (CountsFirstReadyAsInitial ? 1 : 0)
                    ? Task.CompletedTask
                    : Slot(_readyEntered, ordinal).Task;
        }

        internal void ReleaseComplete(int index)
        {
            lock (_gate) Slot(_completeRelease, index).TrySetResult();
        }

        internal void ReleaseAssignmentReady(int ordinal)
        {
            lock (_gate) Slot(_readyRelease, ordinal).TrySetResult();
        }

        internal void ReleaseAll()
        {
            lock (_gate)
            {
                _releaseImmediately = true;
                foreach (var source in _completeRelease.Values) source.TrySetResult();
                foreach (var source in _readyRelease.Values) source.TrySetResult();
            }
        }

        /// <summary>
        /// THE DISPOSAL FAULT: every write currently parked in this fake unwinds with
        /// <see cref="OperationCanceledException"/> (the transport disposal cancelled the call)
        /// and every write that arrives later is released at entry. This is the
        /// dispose-faults-pending-writes contract the fixture promises.
        /// </summary>
        internal void FaultPendingWritesOnDispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (var source in _completeRelease.Values) source.TrySetCanceled();
                foreach (var source in _readyRelease.Values) source.TrySetCanceled();
            }
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message) => WriteCoreAsync(message, CancellationToken.None);

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken ct) =>
            WriteCoreAsync(message, ct);

        private async Task WriteCoreAsync(WorkerMessage message, CancellationToken ct)
        {
            if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Complete)
            {
                int index;
                TaskCompletionSource? release;
                lock (_gate)
                {
                    index = _completes.Count;
                    _completes.Add(message);
                    _writes.Add(message);
                    release = index >= _holdCompletesFrom ? Slot(_completeRelease, index) : null;
                    if (_disposed && release is not null) release.TrySetCanceled();
                    else if (_releaseImmediately && release is not null) release.TrySetResult();
                    SignalWriteCountLocked();
                }

                Slot(_completeEntered, index).TrySetResult();

                if (release is not null)
                {
                    // A PARKED WRITE HONORS ITS FORWARDED TOKEN: the transport disposal cancels
                    // the call, so a pending write unwinds with OperationCanceledException
                    // exactly like gRPC's Dispose contract promises.
                    try
                    {
                        await release.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }

                var failure = Interlocked.Exchange(ref _failNextCompleteWrite, null);
                if (failure is not null)
                    throw failure;

                return;
            }

            if (message.PayloadCase != WorkerMessage.PayloadOneofCase.Ready)
                return;

            int ordinal;
            TaskCompletionSource? readyRelease;
            lock (_gate)
            {
                ordinal = _readies.Count - (CountsFirstReadyAsInitial ? 1 : 0);
                _readies.Add(message);
                _writes.Add(message);
                readyRelease = ordinal >= _holdReadiesFrom ? Slot(_readyRelease, ordinal) : null;
                if (_disposed && readyRelease is not null) readyRelease.TrySetCanceled();
                else if (_releaseImmediately && readyRelease is not null) readyRelease.TrySetResult();
                SignalWriteCountLocked();
                foreach (var (threshold, waiter) in _readyCountWaiters.ToArray())
                    if (_readies.Count >= threshold)
                    {
                        _readyCountWaiters.Remove(threshold);
                        waiter.TrySetResult();
                    }
                if (ordinal >= 0) Slot(_readyEntered, ordinal).TrySetResult();
            }

            if (readyRelease is not null)
            {
                try
                {
                    await readyRelease.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }

            // The ATTEMPT is recorded above BEFORE the injected failure applies, so a test can
            // prove the write really was issued and is never retried.
            var readyFailure = Interlocked.Exchange(ref _failNextReadyWrite, null);
            if (readyFailure is not null)
                throw readyFailure;
        }

        public Task CompleteAsync() => Task.CompletedTask;

        private void SignalWriteCountLocked()
        {
            List<TaskCompletionSource> ready = [];
            foreach (var (threshold, waiter) in _writeCountWaiters)
            {
                if (_writes.Count >= threshold)
                    ready.Add(waiter);
            }

            foreach (var (threshold, waiter) in _writeCountWaiters.ToArray())
                if (_writes.Count >= threshold)
                    _writeCountWaiters.Remove(threshold);

            foreach (var waiter in ready)
                waiter.TrySetResult();
        }

        private static TaskCompletionSource Slot(
            Dictionary<int, TaskCompletionSource> slots, int index)
        {
            if (!slots.TryGetValue(index, out var source))
            {
                source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                slots[index] = source;
            }

            return source;
        }
    }

    /// <summary>
    /// A gated runner double: prompts park until released, cancellation is observed through the
    /// assignment token, and session resets are counted. Mirrors the receipt-gate fixture's
    /// gated runner.
    /// </summary>
    private sealed class CarryPromptRunner : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private readonly HashSet<string> _cancelled = [];
        private readonly HashSet<string> _startedIds = [];
        private string? _taskId;

        internal bool WasCancelled(string taskId)
        {
            lock (_gate) return _cancelled.Contains(taskId);
        }

        internal bool HasPromptStarted(string taskId)
        {
            lock (_gate) return _startedIds.Contains(taskId);
        }

        internal Task PromptStarted(string taskId) => Slot(_started, taskId).Task;

        internal void Release(string taskId) => Slot(_release, taskId).TrySetResult();

        internal void ReleaseAll()
        {
            lock (_gate)
                foreach (var source in _release.Values)
                    source.TrySetResult();
        }

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var id = _taskId ?? "(unknown)";
            TaskCompletionSource started;
            lock (_gate)
            {
                _startedIds.Add(id);
                started = Slot(_started, id);
            }

            started.TrySetResult();

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
        }

        private TaskCompletionSource Slot(Dictionary<string, TaskCompletionSource> map, string key)
        {
            lock (_gate)
            {
                if (!map.TryGetValue(key, out var source))
                {
                    source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    map[key] = source;
                }

                return source;
            }
        }

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) => _taskId = taskId;
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
        public Task ResetSessionAsync(
            string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default) =>
            Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A fake <see cref="CallInvoker"/> answering the unary RPCs the accepted-registration
    /// lifecycle reaches, recording the Register requests so the reconnect tests (round 2) can
    /// assert the sent <c>current_task_id</c> per run. Any other call is a fixture bug.
    /// </summary>
    private sealed class ScriptedInvoker(RegisterResponse registerResponse) : CallInvoker
    {
        private const string ConfigRepoUrl = "https://github.com/org/config-repo.git";

        private readonly object _gate = new();
        private readonly List<RegisterRequest> _registers = [];
        private readonly Dictionary<int, TaskCompletionSource> _registerWaiters = [];

        /// <summary>Snapshots the Register requests, oldest first.</summary>
        internal IReadOnlyList<RegisterRequest> Registers
        {
            get { lock (_gate) return [.. _registers]; }
        }

        /// <summary>Completes once at least <paramref name="count"/> Register RPCs were made.</summary>
        internal Task WaitForRegisterCountAsync(int count, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_registers.Count >= count)
                    return Task.CompletedTask;
                if (!_registerWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _registerWaiters[count] = waiter;
                }

                return waiter.Task.WaitAsync(Failsafe, ct);
            }
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var payload = method.FullName switch
            {
                "/copilothive.HiveOrchestrator/Register" => RespondRegister(request),
                "/copilothive.HiveOrchestrator/GetWorkerConfig" => new GetWorkerConfigResponse
                {
                    GithubToken = "ghp_fixture",
                    LlmProvider = "copilot",
                    ConfigRepoUrl = ConfigRepoUrl,
                },
                "/copilothive.HiveOrchestrator/GetSession" => new GetSessionResponse { Found = false },
                "/copilothive.HiveOrchestrator/SaveSession" => new SaveSessionResponse { Success = true },
                "/copilothive.HiveOrchestrator/Heartbeat" => new HeartbeatResponse { Acknowledged = true },
                _ => throw new NotSupportedException($"Unexpected unary call {method.FullName}."),
            };

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)payload),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        private object RespondRegister<TRequest>(TRequest request)
        {
            List<TaskCompletionSource> ready = [];
            lock (_gate)
            {
                if (request is RegisterRequest registerRequest)
                    _registers.Add(registerRequest);

                foreach (var (threshold, waiter) in _registerWaiters)
                {
                    if (_registers.Count >= threshold)
                        ready.Add(waiter);
                }

                foreach (var waiter in ready)
                    _registerWaiters.Remove(waiter.Task.Id);
            }

            foreach (var waiter in ready)
                waiter.TrySetResult();

            return registerResponse;
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
    // ══════════════════════════════════════════════════════════════════════════
    // THE MANDATORY ACCEPTANCE CRITERIA (a)–(n) — the reconnect path (goal D),
    // the fail-closed lazy-provisioner interim (goal E) and Program.cs (goal F).
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// (a) A RUNNING TASK SURVIVES EOF. The second run registers with
    /// <c>current_task_id</c>, gets <c>adopted=true</c>, does NOT call
    /// <c>ConnectAsync</c>, and sends no initial Ready. The retained result is delivered
    /// exactly once on the SECOND stream (one Complete then one Ready), and the first stream
    /// has no Complete or assignment Ready. The executor is held at its prompt by a TCS until
    /// AFTER the second run's adoption publication; EOF therefore precedes its terminal result.
    /// </summary>
    [Fact]
    public async Task Acceptance_a_RunningTaskSurvivesEof_SecondRunAdoptsAndDeliversOnSecondStream()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        try
        {
            // The executor has entered its gated prompt and is STILL RUNNING at EOF.
            var execution = await plan.PushAssignmentAsync("task-A");
            Assert.False(execution.IsCompleted);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));
            Assert.False(execution.IsCompleted);
            Assert.False(plan.Runner.WasCancelled("task-A"));
            await WaitForCarriedDeliveryAsync(plan.Service, "The carry claim must start a CarriedDelivery.");
            Assert.Equal("task-A", GetHeartbeatTaskId(plan.Service));
            Assert.Equal("coder", GetHeartbeatRole(plan.Service));

            // RUN 2 (the reconnect): the adopted registration.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));

            // THE REGISTRATION CLAIM: the recorded RegisterRequest of THIS run named the task.
            var registration = Assert.Single(plan.Invokers[1].Registers);
            Assert.Equal("task-A", registration.CurrentTaskId);

            // NO RUNNER PREPARATION: the adopted run defers ConnectAsync - the count stays at
            // run 1's single call.
            Assert.Equal(1, plan.Runner.ConnectCount);
            Assert.Same(plan.CurrentConnection, GetAdoptedConnection(plan.Service));
            Assert.Empty(plan.CurrentRequests.Writes); // adoption replaced the initial Ready

            // Only AFTER adoption do we allow the original executor to finish.
            plan.Runner.Release("task-A");
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var adopted = Assert.IsType<WorkerConnection>(GetAdoptedConnection(plan.Service));
            Assert.Same(plan.CurrentConnection, adopted);
            Assert.Equal(2, plan.CurrentRequests.Writes.Count);
            Assert.Equal(
                WorkerMessage.PayloadOneofCase.Complete,
                plan.CurrentRequests.Writes[0].PayloadCase);
            Assert.Equal(
                WorkerMessage.PayloadOneofCase.Ready,
                plan.CurrentRequests.Writes[1].PayloadCase);
            var complete = Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal("task-A", complete.Complete.TaskId);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));

            // The original stream saw only run 1's initial Ready; nothing else was initiated
            // after the early retirement (no Complete and no assignment Ready).
            Assert.Empty(plan.Requests[0].Completes);
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);
            Assert.Single(plan.Requests[0].Writes);

            // END RUN 2: the exit re-check clears the Delivered assignment before RunAsync returns.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
            Assert.Null(GetHeartbeatTaskId(plan.Service));
            Assert.Null(GetHeartbeatRole(plan.Service));
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (b) The executor is held at its prompt by a TCS through EOF, then released after run 1
    /// returns but BEFORE run 2 starts. The result is retained, reporting's finally restores
    /// the busy heartbeat, and Complete goes only on the adopting second stream.
    /// </summary>
    [Fact]
    public async Task Acceptance_b_ResultBetweenEofAndReconnect_HeartbeatIdStillSet_CompleteOnAdoptingStreamOnly()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        try
        {
            var execution = await plan.PushAssignmentAsync("task-A");
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.False(execution.IsCompleted);
            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));

            // The result arises strictly AFTER EOF but BEFORE the second registration.
            plan.Runner.Release("task-A");
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await GetActiveReporting(plan.Service).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.NotNull(GetRetainedResult(plan.Service));
            // Reporting's finally must restore the heartbeat state for the carried task.
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));
            Assert.Equal("task-A", GetHeartbeatTaskId(plan.Service));
            Assert.Equal("coder", GetHeartbeatRole(plan.Service));

            // No result was sent on the original retired stream. Reconnect only NOW.
            Assert.Empty(plan.Requests[0].Completes);
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            Assert.Equal("task-A", Assert.Single(plan.Invokers[1].Registers).CurrentTaskId);
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var complete = Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal("task-A", complete.Complete.TaskId);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            // Stream 1 never saw the completion or an assignment Ready.
            Assert.Empty(plan.Requests[0].Completes);
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));

            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (b3) A MESSAGE HANDLER THROWS (an unknown-role UpdateAgents) while an assignment runs:
    /// today's cancel-and-drain teardown, NOT carried - the slot is cleared, no CarriedDelivery
    /// exists, the heartbeat state is cleared, and a following run registers with an EMPTY
    /// current_task_id and accepts a new assignment.
    /// </summary>
    [Fact]
    public async Task Acceptance_b3_HandlerThrow_TakesTodaysCancelAndDrainTeardownNotCarried()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: false);
        try
        {
            var execution = await plan.PushAssignmentAsync("task-A");

            // THE HANDLER FAULT: an UpdateAgents naming a role the production parser rejects.
            plan.Push(new OrchestratorMessage
            {
                UpdateAgents = new UpdateAgents { Role = "no-such-role", AgentsMdContent = "irrelevant" },
            });
            await plan.Responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The run FAULTS with the handler's own exception identity (today's behavior) - that
            // fault is the barrier proving the UpdateAgents handler RAN and the teardown with it.
            await Assert.ThrowsAsync<InvalidOperationException>(() => plan.JoinRunAsync());

            // TODAY'S TEARDOWN: the slot is empty, no carried delivery exists, the heartbeat
            // state is cleared, and the execution task is terminal.
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
            Assert.Null(GetCarriedDeliveryOrNull(plan.Service));
            Assert.Null(GetHeartbeatTaskId(plan.Service));

            // The NEXT run registers with EMPTY current_task_id and gets ConnectAsync + the
            // initial Ready (today's shape).
            plan.StartSecondRun(RegisterResponseFor(adopted: false));
            await plan.CurrentRequests.WaitForWriteCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(string.Empty, plan.Invokers[^1].Registers[^1].CurrentTaskId);
            Assert.Equal(2, plan.Runner.ConnectCount);

            // A NEW ASSIGNMENT IS ACCEPTED on the fresh stream: its own Complete (and its own
            // ordinary Ready) land on stream 2.
            await plan.PushAssignmentAsync("task-B");
            plan.Runner.Release("task-B");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.WaitForAssignmentReadyCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            var complete = Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal("task-B", complete.Complete.TaskId);
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (c) THE FIRST-STREAM COMPLETE WRITE FAILS BECAUSE THE STREAM ENDED: the delivery is
    /// still made on the adopting second stream (the carried delivery observes the faulted
    /// reporting and delivers the retained result).
    /// </summary>
    [Fact]
    public async Task Acceptance_c_FirstStreamCompleteFailsBecauseStreamEnded_DeliveredOnAdoptingSecondStream()
    {
        var plan = ReconnectPlan.StartAsync(
            "task-A", register2Adopted: true, holdReportComplete: true);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.Runner.Release("task-A");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Stream loss with the reporter PARKED in its Complete write: the run-1 stream
            // disposal faults that parked write - the first-stream Complete write FAILED
            // because the stream ended.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // The carried delivery observes the (now faulted) reporting and delivers the
            // retained result on the adopting SECOND stream.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            plan.Requests[0].ReleaseAll();
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var complete = Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal("task-A", complete.Complete.TaskId);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            // Stream 1 carries ONLY the tolerated pre-loss write.
            Assert.Single(plan.Requests[0].Completes);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(GetActiveAssignment(plan.Service)));

            plan.CompleteStream();
            await plan.JoinRunAsync();
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (f) THE SECOND STREAM ENDS BEFORE ITS COMPLETE SUCCEEDS: the assignment is still
    /// carried; the third registration sends current_task_id; the delivery lands on the
    /// adopting THIRD stream.
    /// </summary>
    [Fact]
    public async Task Acceptance_f_SecondStreamEndsBeforeCompleteSucceeds_ThirdStreamDelivers()
    {
        var plan = ReconnectPlan.StartAsync(
            "task-A", register2Adopted: true, holdReportComplete: true);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.Runner.Release("task-A");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();

            // RUN 2 (the reconnect, ADOPTED, with the delivery's Complete write PARKED in
            // stream 2's fake - armed before the run started - and its outcome FAULTED with a
            // NON-cancellation failure, so the delivery RETURNS TO THE WAIT instead of ending).
            plan.PendingHoldCompletesFrom = 0;
            plan.PendingFailAdoptedReady = null;
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            Assert.Equal("task-A", plan.Invokers[1].Registers[0].CurrentTaskId);

            // THE SECOND RUN ADOPTS but its stream ends before the delivery's Complete write
            // succeeds: the delivery's own Complete write is PARKED in stream 2's fake, the
            // stream loss faults it on the disposal, and the delivery returns to the wait. The
            // run ends with the assignment still Carried (the exit re-check leaves a CARRIED
            // assignment alone).
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The write is still parked with a LIVE assignment token. Only disposing the
            // transport faults it: the fake's TrySetCanceled gives the pending write an OCE
            // with an empty token, not the assignment token. The delivery must treat this as
            // a transport failure and wait for a DIFFERENT adopted connection.
            var retainedOwner = GetActiveAssignment(plan.Service);
            var ownerCts = GetOwnerCts(plan.Service);
            Assert.False(ownerCts.IsCancellationRequested);
            Assert.False(delivery.IsCompleted);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Same(retainedOwner, GetActiveAssignment(plan.Service));
            Assert.False(ownerCts.IsCancellationRequested);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(retainedOwner));
            Assert.False(delivery.IsCompleted);
            Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal(0, plan.Requests[1].AssignmentReadyCount);

            // THE THIRD RUN registers with current_task_id again and is adopted; the delivery
            // lands on the adopting THIRD stream.
            plan.StartThirdRun(RegisterResponseFor(adopted: true));
            var delivery2 = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must still run.");
            Assert.Same(delivery, delivery2);
            await delivery2.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var complete = Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal("task-A", complete.Complete.TaskId);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Single(plan.Requests[1].Completes);
            Assert.Equal(0, plan.Requests[1].AssignmentReadyCount);
            Assert.Equal("task-A", plan.Invokers[2].Registers[0].CurrentTaskId);

            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (f2) THE COMPLETE SUCCEEDS ON STREAM 2, ITS READY WRITE FAILS, THEN STREAM 2 ENDS: the
    /// assignment is cleared before the second RunAsync returns (Delivered is not Carried, so
    /// the exit re-check clears it); the third run sends EMPTY current_task_id, calls
    /// ConnectAsync and sends the initial Ready; the Complete is not resent; a new assignment
    /// is accepted.
    /// </summary>
    [Fact]
    public async Task Acceptance_f2_CarriedReadyFailsThenStreamEnds_AssignmentClearedAndThirdRunStartsFresh()
    {
        var plan = ReconnectPlan.StartAsync(
            "task-A", register2Adopted: true, holdReportComplete: true, failAdoptedReady: true);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.Runner.Release("task-A");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();

            // RUN 2 (adopted, with the injected carried-Ready failure): the delivery writes one
            // Complete on stream 2, its Ready write FAILS (injected), and the assignment is
            // Delivered with the started-write fact set.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            Assert.Equal("task-A", plan.Invokers[1].Registers[0].CurrentTaskId);
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));
            Assert.True(GetCarriedReadyStarted(owner));
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            var complete = Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal("task-A", complete.Complete.TaskId);

            // STREAM 2 ENDS: the exit re-check clears the Delivered assignment before RunAsync
            // returns.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
            Assert.Null(GetHeartbeatTaskId(plan.Service));
            Assert.Null(GetHeartbeatRole(plan.Service));

            // THE THIRD RUN: EMPTY current_task_id, ConnectAsync called again, initial Ready
            // sent.
            plan.StartThirdRun(RegisterResponseFor(adopted: false));
            await plan.CurrentRequests.WaitForWriteCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(string.Empty, plan.Invokers[2].Registers[0].CurrentTaskId);
            Assert.Equal(2, plan.Runner.ConnectCount);
            Assert.Equal(
                WorkerMessage.PayloadOneofCase.Ready,
                plan.CurrentRequests.Writes[0].PayloadCase);

            // THE COMPLETE IS NOT RESENT, and a NEW ASSIGNMENT IS ACCEPTED: its own Complete
            // AND its own ordinary Ready land on stream 3 (the body claimed them).
            Assert.Empty(plan.CurrentRequests.Completes);
            await plan.PushAssignmentAsync("task-B");
            plan.Runner.Release("task-B");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.WaitForAssignmentReadyCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            var taskBComplete = Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal("task-B", taskBComplete.Complete.TaskId);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (f2b) THE COMPLETE SUCCEEDS ON STREAM 2 AND ITS READY WRITE FAILS, BUT STREAM 2'S READ
    /// STAYS OPEN: the run does not end; no Ready retry is made; the assignment is retained as
    /// Delivered with CarriedReadyStarted set; a subsequent assignment on stream 2 is ACCEPTED
    /// (not refused) and starts after the replacement drain.
    /// </summary>
    [Fact]
    public async Task Acceptance_f2b_FailedCarriedReadyOnOpenStream_SuccessorAcceptedAfterReplacementDrain()
    {
        var plan = ReconnectPlan.StartAsync(
            "task-A", register2Adopted: true, holdReportComplete: true, failAdoptedReady: true);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.Runner.Release("task-A");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();

            // RUN 2 (adopted, carried-Ready failure injected, stream 2's read stays OPEN).
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            Assert.Equal("task-A", plan.Invokers[1].Registers[0].CurrentTaskId);
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE FAILED READY, on an OPEN stream: Delivered with the started-write fact set.
            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));
            Assert.True(GetCarriedReadyStarted(owner));
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Single(plan.CurrentRequests.Completes);

            // THE STREAM STAYS OPEN: a successor assignment is ACCEPTED (CarriedReadyStarted
            // authorizes it) and starts after the replacement drain - the reset is the FIRST
            // production step after that drain, and PromptStarted proves the successor's body
            // genuinely entered.
            plan.Push(ResultAssignment("task-B"));
            await plan.Runner.PromptStarted("task-B").WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The successor owns the slot with its own fresh state. The reset is recorded under
            // the PREDECESSOR's id (the runner's current-task id is set by the executor later
            // than the reset), so the COUNT is the observable: exactly TWO resets have entered
            // (task-A's assignment and task-B's replacement).
            var successor = GetActiveAssignment(plan.Service);
            Assert.Equal("task-B", GetOwnerTaskId(successor));
            Assert.Equal(CarryStates.Open, GetAssignmentState(successor));
            Assert.Equal(2, plan.Runner.ResetCount);

            // NO READY RETRY: still exactly ONE assignment Ready on stream 2 (the carried
            // delivery's failed attempt).
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);

            // The successor finishes: its own Complete is PARKED too (run 2's hold covers ALL
            // completes on stream 2), so release it; its own ordinary Ready is NOT held and
            // lands after the eligibility publishes.
            plan.Runner.Release("task-B");
            await plan.CurrentRequests.AssignmentCompleteEntered(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CurrentRequests.ReleaseAll();
            await plan.CurrentRequests.WaitForAssignmentReadyCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
            Assert.Equal(2, plan.CurrentRequests.Completes.Count);
            Assert.Equal(2, plan.CurrentRequests.AssignmentReadyCount);
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (g) ADOPTED == FALSE: the carried assignment is cancelled/drained BEFORE the stream
    /// opens, with NO Complete; ConnectAsync is called once per non-adopted run, AFTER the
    /// drain and BEFORE the initial Ready (order recorded with the fake); the initial Ready is
    /// sent and a subsequent assignment is accepted.
    /// </summary>
    [Fact]
    public async Task Acceptance_g_NotAdopted_DrainsBeforeStreamAndConnectsBeforeInitialReady()
    {
        var plan = ReconnectPlan.StartAsync(
            "task-A", register2Adopted: false, holdReportComplete: true);
        var connectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.Runner.Release("task-A");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();

            // Arm a gate AT the real runner preparation call: the drain must have already
            // cancelled and joined the exact owner, cleared the slot and heartbeat, and must
            // precede even the stream factory invocation. A reversed order fails HERE.
            var ownerCts = GetOwnerCts(plan.Service);
            var retainedDelivery = await WaitForCarriedDeliveryAsync(plan.Service, "Expected carried delivery.");
            bool cancelledAtConnect = false, drainedAtConnect = false, joinedAtConnect = false;
            string? taskAtConnect = "not sampled";
            int opensAtConnect = -1;
            plan.Runner.ConnectEnteredHook = async _ =>
            {
                cancelledAtConnect = ownerCts.IsCancellationRequested;
                drainedAtConnect = GetActiveAssignmentOrNull(plan.Service) is null;
                joinedAtConnect = retainedDelivery.IsCompleted;
                taskAtConnect = GetHeartbeatTaskId(plan.Service);
                opensAtConnect = plan.StreamOpenCount;
                connectEntered.TrySetResult();
                await allowConnect.Task;
            };
            plan.StartSecondRun(RegisterResponseFor(adopted: false));
            await connectEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal("task-A", Assert.Single(plan.Invokers[1].Registers).CurrentTaskId);
            Assert.True(cancelledAtConnect);
            Assert.True(drainedAtConnect);
            Assert.True(joinedAtConnect);
            Assert.Null(taskAtConnect);
            Assert.Equal(1, opensAtConnect); // only run 1's stream exists while held
            Assert.Empty(plan.CurrentRequests.Writes);
            allowConnect.TrySetResult();
            await plan.CurrentRequests.WaitForWriteCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, plan.Runner.ConnectCount);
            Assert.Equal(
                WorkerMessage.PayloadOneofCase.Ready,
                plan.CurrentRequests.Writes[0].PayloadCase);
            Assert.Empty(plan.CurrentRequests.Completes);
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
            Assert.Null(GetHeartbeatTaskId(plan.Service));

            // A SUBSEQUENT ASSIGNMENT IS ACCEPTED: its own Complete (and its own ordinary
            // Ready) land on stream 2.
            await plan.PushAssignmentAsync("task-B");
            plan.Runner.Release("task-B");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.WaitForAssignmentReadyCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            var complete = Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal("task-B", complete.Complete.TaskId);
        }
        finally
        {
            allowConnect.TrySetResult();
            plan.Runner.ConnectEnteredHook = null;
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (g2) AN ANOMALOUS adopted_task == true WITH AN EMPTY current_task_id: ConnectAsync and
    /// the initial Ready still happen, adoption is NOT published, and exactly ONE Warning is
    /// logged.
    /// </summary>
    [Fact]
    public async Task Acceptance_g2_AnomalousAdoptedWithEmptyClaim_TodaysShapeWithExactlyOneWarning()
    {
        var stdOut = Console.Out;
        var capture = new StringWriter();
        Console.SetOut(capture);
        ReconnectPlan plan;
        try
        {
            // The run answers adopted_task == true although NOTHING was claimed.
            plan = ReconnectPlan.StartFresh(RegisterResponseFor(adopted: true));
            await plan.CurrentRequests.WaitForWriteCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(stdOut);
        }
        try
        {
            // TODAY'S SHAPE: ConnectAsync ran, the INITIAL READY was sent, and NO adoption was
            // published (nothing is delivered).
            Assert.Equal(1, plan.Runner.ConnectCount);
            Assert.Equal(
                WorkerMessage.PayloadOneofCase.Ready,
                plan.CurrentRequests.Writes[0].PayloadCase);
            Assert.Null(GetAdoptedConnectionOrNull(plan.Service));
            Assert.Equal(string.Empty, plan.Invokers[0].Registers[0].CurrentTaskId);

            // EXACTLY ONE WARNING (the production _log.Warn writes to stdout).
            var warnings = capture.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("WARN", StringComparison.Ordinal))
                .ToList();
            Assert.Single(warnings);
            Assert.Contains(
                "adopted task for a registration that claimed none",
                warnings[0],
                StringComparison.Ordinal);

            plan.CompleteStream();
            await plan.JoinRunAsync();
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (h1) AFTER AN ADOPTED REGISTRATION THE STREAM OPEN FAILS: nothing is written on that
    /// connection, the assignment is still Carried, ConnectAsync was not called, and the next
    /// run re-registers with current_task_id and delivers.
    /// </summary>
    [Fact]
    public async Task Acceptance_h1_StreamOpenFailsAfterAdoptedRegistration_AssignmentStaysCarriedAndNextRunDelivers()
    {
        var plan = ReconnectPlan.StartAsync(
            "task-A", register2Adopted: true, holdReportComplete: true);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.Runner.Release("task-A");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // THE SECOND RUN's STREAM OPEN FAILS: the WorkStreamFactory throws, so no
            // connection is built, nothing is written on it, and the adoption is never
            // published.
            plan.StartThirdRun(
                RegisterResponseFor(adopted: true),
                openFailure: new InvalidOperationException("stream open failed"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => plan.Run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // THE ASSIGNMENT IS STILL CARRIED, ConnectAsync was NOT called (the count stays at
            // run 1's call), and no connection is published.
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));
            Assert.Equal(1, plan.Runner.ConnectCount);
            Assert.Null(GetPublishedConnection(plan.Service));
            Assert.Equal("task-A", plan.Invokers[^1].Registers[0].CurrentTaskId);

            // THE NEXT RUN re-registers with current_task_id and DELIVERS on its stream.
            plan.StartNextRun(RegisterResponseFor(adopted: true));
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            plan.Requests[0].ReleaseAll();
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal("task-A", plan.Invokers[^1].Registers[0].CurrentTaskId);
            var complete = Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal("task-A", complete.Complete.TaskId);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);

            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (h2) AFTER AN ADOPTED REGISTRATION THE HEARTBEAT SEAM THROWS AFTER CONNECTION
    /// PUBLICATION: nothing is written on that connection, the assignment is still Carried,
    /// ConnectAsync was not called, and the next run re-registers with current_task_id and
    /// delivers.
    /// </summary>
    [Fact]
    public async Task Acceptance_h2_HeartbeatSeamThrowsAfterPublication_NothingWrittenAndNextRunDelivers()
    {
        var plan = ReconnectPlan.StartAsync(
            "task-A", register2Adopted: true, holdReportComplete: true);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.Runner.Release("task-A");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // The heartbeat FACTORY throws synchronously after connection publication but
            // before the adoption can be published or a message can be written.
            var heartbeatFailure = new InvalidOperationException("heartbeat seam failed");
            plan.StartSecondRun(RegisterResponseFor(adopted: true), heartbeatFailure);
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => plan.Run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(heartbeatFailure, thrown);
            var failedConnection = Assert.IsType<WorkerConnection>(plan.FailedHeartbeatConnection);
            Assert.True(failedConnection.IsRetired);
            Assert.Null(GetPublishedConnection(plan.Service));
            Assert.Null(GetAdoptedConnectionOrNull(plan.Service));
            Assert.Empty(plan.CurrentRequests.Writes);
            Assert.Equal("task-A", Assert.Single(plan.Invokers[1].Registers).CurrentTaskId);
            Assert.Equal(1, plan.Runner.ConnectCount);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // A LATER adopted registration on the SAME service finally publishes adoption.
            plan.StartThirdRun(RegisterResponseFor(adopted: true));
            Assert.Equal("task-A", Assert.Single(plan.Invokers[2].Registers).CurrentTaskId);
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Equal(1, plan.Runner.ConnectCount);
            plan.CompleteStream();
            await plan.JoinRunAsync();

        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (m) EOF WITH NO ASSIGNMENT: the reconnect re-enters with ConnectAsync and the initial
    /// Ready - today's shape after the defensive entry rule.
    /// </summary>
    [Fact]
    public async Task Acceptance_m_EofWithNoAssignment_ReconnectsWithConnectAsyncAndInitialReady()
    {
        var plan = ReconnectPlan.StartFresh(RegisterResponseFor(adopted: false));
        try
        {
            // A clean, EMPTY run: the initial Ready is sent, then EOF with no assignment.
            await plan.CurrentRequests.WaitForWriteCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(string.Empty, plan.Invokers[0].Registers[0].CurrentTaskId);
            Assert.Equal(1, plan.Runner.ConnectCount);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(plan.Service));

            // THE RECONNECT: ConnectAsync runs again (a non-adopted run always prepares the
            // runner), the initial Ready is sent, and the registration claims nothing.
            plan.StartNextRun(RegisterResponseFor(adopted: false));
            await plan.CurrentRequests.WaitForWriteCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, plan.Runner.ConnectCount);
            Assert.Equal(
                WorkerMessage.PayloadOneofCase.Ready,
                plan.CurrentRequests.Writes[0].PayloadCase);
            Assert.Equal(string.Empty, plan.Invokers[1].Registers[0].CurrentTaskId);
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (n) THE PROGRAM-LEVEL DECISIONS, through the production paths Program.cs consumes.
    /// <para>
    /// LIMITATION, DISCLOSED: Program.cs is top-level statements whose attempt loop captures
    /// locals (cts, service, delay, exitCode) and exposes NO extracted loop/cleanup decision
    /// helper or test seam (verified against the actual source; production is frozen this
    /// round), so the loop's control flow itself cannot be driven in-process. The repo's
    /// existing Program-level coverage covers the loop shape two ways - the structural
    /// source-shape test (WorkerRedactionIntegrationTests.WorkerProgram_ProcessLifetimeContract)
    /// and the real-process fatal-path test - which cover the loop's retry/fatal control flow
    /// and the final-disposal policy. THIS test proves the DECISIONS the loop consumes, through
    /// the REAL service: a returned WorkStreamEnded outcome reconnects (the run guard admits
    /// the re-entry on the SAME service), a RegistrationRejected outcome is terminal, and the
    /// final cleanup runs DrainCarriedAssignmentAsync BEFORE the ONE Dispose (mirrored exactly,
    /// because the disposal must run even when the drain threw, with the exit code decided
    /// after both - the policy shape the existing ProgramFinalDisposal_ThrowingDisposal_*
    /// mirrors cover).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Acceptance_n_ReconnectLoopDecisions_DrainBeforeFinalDispose()
    {
        // These are the decisions Program.cs actually consumes (no source-text mirror).
        Assert.Equal(WorkerOutcomeAction.Reconnect,
            WorkerProgramDecisions.DecideOutcome(WorkerRunOutcome.WorkStreamEnded));
        Assert.Equal(WorkerOutcomeAction.Stop,
            WorkerProgramDecisions.DecideOutcome(WorkerRunOutcome.RegistrationRejected));
        Assert.Equal(WorkerOutcomeAction.Fatal,
            WorkerProgramDecisions.DecideOutcome((WorkerRunOutcome)int.MaxValue));

        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: false);
        try
        {
            var execution = await plan.PushAssignmentAsync("task-A");
            plan.CompleteStream();
            Assert.Equal(WorkerRunOutcome.WorkStreamEnded, await plan.JoinRunAsync());
            Assert.False(execution.IsCompleted);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // The rejection really names the STILL-CARRIED task, not a task previously
            // delivered and cleared. Rejection drains it before opening any work stream.
            plan.StartSecondRun(RegisterResponseFor(adopted: false, rejected: true));
            Assert.Equal("task-A", Assert.Single(plan.Invokers[1].Registers).CurrentTaskId);
            Assert.Equal(WorkerRunOutcome.RegistrationRejected,
                await plan.Run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
            Assert.Null(GetPublishedConnection(plan.Service));
            Assert.Empty(plan.CurrentRequests.Writes);
        }
        finally
        {
            await plan.TeardownAsync();
        }

        // The extracted final-cleanup decision is driven with an ACTUALLY THROWING drain.
        // Program.cs then runs its one final Dispose irrespective of the decision.
        var events = new List<string>();
        var failure = new InvalidOperationException("sensitive marker from carried drain");
        var runner = new ObservingRunner();
        var service = new WorkerService("http://localhost:9999", "worker-1", ["coder"], "/config-repo");
        InstallRunner(service, runner);
        var attempts = 0;
        var disposalAttempts = 0;
        try
        {
            var drainFailed = await WorkerProgramDecisions.DrainCarriedAssignmentFailedAsync(() =>
            {
                attempts++;
                events.Add("drain");
                throw failure; // truly throwing delegate, not an empty-service no-op
            });
            var exitCode = drainFailed ? 1 : 0;
            try
            {
                disposalAttempts++;
                service.Dispose();
                events.Add("dispose");
            }
            catch (Exception)
            {
                exitCode = 1;
            }
            Assert.True(drainFailed);
            Assert.Equal(1, attempts);
            Assert.Equal(1, disposalAttempts);
            Assert.Equal(1, runner.DisposeCount);
            Assert.Equal(["drain", "dispose"], events);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            if (disposalAttempts == 0) service.Dispose();
        }
    }

    // ── The multi-run reconnect plan ──────────────────────────────────────────

    /// <summary>
    /// THE SCRIPTED register response the reconnect tests answer with: an ACCEPTED registration
    /// whose explicit adopted_task answer is the vector under test.
    /// </summary>
    private static RegisterResponse RegisterResponseFor(bool adopted, bool rejected = false) => new()
    {
        Accepted = !rejected,
        AssignedWorkerId = "worker-reconnect-a",
        OrchestratorVersion = "test",
        AdoptedTask = adopted && !rejected,
    };

    /// <summary>
    /// THE RECONNECT PLAN: ONE service, REAL sequential <see cref="WorkerService.RunAsync"/>
    /// runs, each over its OWN invoker + fake stream wired through the existing seams, each
    /// with a scripted register response. This is the round-2 extension of the round-1
    /// <see cref="CarryHarness"/>: the same fault-on-dispose / token-honoring write fake and the
    /// same gated runner, plus the reconnect observables (per-run RegisterRequests, the
    /// runner's ordered event log, per-run diagnostics).
    /// </summary>
    private sealed class ReconnectPlan
    {
        private const string WorkerId = "worker-reconnect";
        private const string AssignedId = "worker-reconnect-a";
        private const string ConfigRepoUrl = "https://github.com/org/config-repo.git";

        private ReconnectPlan(WorkerService service, ObservingRunner runner, IDisposable gitRestore)
        {
            Service = service;
            Runner = runner;
            _gitRestore = gitRestore;
        }

        internal WorkerService Service { get; }
        internal ObservingRunner Runner { get; }

        internal List<ScriptedInvoker> Invokers { get; } = [];
        internal List<CarryRequestStream> Requests { get; } = [];
        internal List<ChannelResponseReader> Readers { get; } = [];
        internal List<WorkerConnection> Connections { get; } = [];
        internal List<Task<WorkerConnection>> PublishedConnections { get; } = [];
        internal WorkerConnection? FailedHeartbeatConnection { get; private set; }
        private int _streamOpenCount;
        internal int StreamOpenCount => Volatile.Read(ref _streamOpenCount);
        internal List<Task<WorkerRunOutcome>> Runs { get; } = [];
        internal Task<WorkerRunOutcome> Run => Runs[^1];
        internal CarryRequestStream CurrentRequests => Requests[^1];
        internal ChannelResponseReader Responses => Readers[^1];
        internal WorkerConnection CurrentConnection => Connections[^1];

        /// <summary>
        /// The ONE-SHOT carried-Ready failure, consumed by the NEXT <see cref="StartNextRun"/>
        /// call (the adopted run whose carried Ready write must fail).
        /// </summary>
        internal Exception? PendingFailAdoptedReady { get; set; }

        /// <summary>
        /// The ONE-SHOT Complete hold, consumed by the NEXT <see cref="StartNextRun"/> call (the
        /// adopted run whose carried Complete write must park in the fake).
        /// </summary>
        internal int? PendingHoldCompletesFrom { get; set; }

        /// <summary>Starts the FIRST run: a fresh service, the initial Ready expected.</summary>
        internal static ReconnectPlan StartFresh(RegisterResponse firstResponse)
        {
            // THE CONFIG-REPO SEAM: the reconnect plan runs REAL assignment bodies on
            // connections whose production provisioner is live, so the config-repo
            // preparation reaches the git layer - a healthy fake launcher keeps that
            // deterministic and off the real filesystem.
            var configRepoDir = Path.Combine(
                Path.GetTempPath(), "reconnect-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(configRepoDir);
            var launcher = new FakeGitLauncher(tokens =>
            {
                var command = string.Join(' ', tokens);
                if (command.Contains("rev-parse --is-inside-work-tree", StringComparison.Ordinal))
                    return new GitProcessResult(0, "true\n", string.Empty);
                if (command.Contains("rev-parse --show-toplevel", StringComparison.Ordinal))
                    return new GitProcessResult(0, configRepoDir + "\n", string.Empty);
                if (command.Contains("remote get-url origin", StringComparison.Ordinal))
                    return new GitProcessResult(0, ConfigRepoUrl + "\n", string.Empty);
                return new GitProcessResult(0, string.Empty, string.Empty);
            });
            var gitRestore = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

            var service = new WorkerService(
                "http://localhost:9999", WorkerId, ["coder"], configRepoDir);
            var runner = new ObservingRunner();
            InstallRunner(service, runner);
            service.HeartbeatTaskFactory = (_, _) => Task.CompletedTask;

            var plan = new ReconnectPlan(service, runner, gitRestore);
            plan.StartNextRun(firstResponse);
            return plan;
        }

        private readonly IDisposable _gitRestore;

        /// <summary>
        /// STARTS THE FIRST RUN as a carried assignment already in flight: a genuine assignment
        /// is pushed on the FIRST run's stream, and the test then ends that stream to record
        /// the stream loss.
        /// </summary>
        internal static ReconnectPlan StartAsync(
            string taskId,
            bool register2Adopted,
            bool holdReportComplete = false,
            bool failAdoptedReady = false)
        {
            var plan = StartFresh(RegisterResponseFor(adopted: false));
            if (holdReportComplete)
                plan.Requests[0].HoldCompletesFrom = 0;
            plan.PendingFailAdoptedReady = failAdoptedReady
                ? new InvalidOperationException("carried Ready write failed") : null;

            // The ASSIGNMENT IS PUSHED BY THE TEST (the round-1 convention), so the test can
            // interleave its own gates between the assignment's arrival and the stream loss.
            return plan;
        }

        /// <summary>
        /// Wires ONE run: a fresh invoker (with the scripted register response) and a fresh
        /// fake stream, then starts the REAL <see cref="WorkerService.RunAsync"/> on the SAME
        /// service. <paramref name="openFailure"/> (one-shot) makes the STREAM OPEN throw;
        /// <paramref name="heartbeatFailure"/> (one-shot) makes the heartbeat seam task fault;
        /// <paramref name="failAdoptedReady"/> injects ONE Ready-write failure for the carried
        /// delivery's own Ready attempt.
        /// </summary>
        internal void StartNextRun(
            RegisterResponse response,
            Exception? openFailure = null,
            Exception? heartbeatFailure = null,
            Exception? failAdoptedReady = null)
        {
            // CONSUME the pending per-run wiring the test armed for THIS run.
            failAdoptedReady ??= PendingFailAdoptedReady;
            PendingFailAdoptedReady = null;
            var holdCompletesFrom = PendingHoldCompletesFrom;
            PendingHoldCompletesFrom = null;

            // An ADOPTED run sends NO initial Ready, so nothing is subtracted; every other run
            // subtracts exactly its own initial Ready.
            var requests = new CarryRequestStream
            {
                CountsFirstReadyAsInitial = !response.AdoptedTask,
            };
            var responses = new ChannelResponseReader();
            var invoker = new ScriptedInvoker(response);

            if (failAdoptedReady is { } readyFailure)
                requests.FailNextReadyWrite = readyFailure;

            if (holdCompletesFrom is { } holdFrom)
                requests.HoldCompletesFrom = holdFrom;

            var streamToken = CancellationToken.None;
            Service.CallInvokerFactory = () => invoker;
            Service.WorkStreamFactory = (_, ct) =>
            {
                if (openFailure is { } failure)
                    throw failure;

                Interlocked.Increment(ref _streamOpenCount);
                streamToken = ct;
                var stream = BuildFaultingStream(requests, responses);
                CurrentStreamToken = ct;
                HandedReader = responses;
                return stream;
            };
            var published = new TaskCompletionSource<WorkerConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Service.HeartbeatTaskFactory = (connection, _) =>
            {
                // Production enters the heartbeat factory AFTER publishing the connection.
                // Register the waiter at construction rather than polling for publication.
                Connections.Add(connection);
                published.TrySetResult(connection);
                if (heartbeatFailure is { } failure)
                {
                    FailedHeartbeatConnection = connection;
                    throw failure; // BEFORE adoption publication, not a faulted heartbeat task
                }
                return Task.CompletedTask;
            };

            Invokers.Add(invoker);
            Requests.Add(requests);
            Readers.Add(responses);
            PublishedConnections.Add(published.Task);
            Runs.Add(Service.RunAsync(TestContext.Current.CancellationToken));
        }

        /// <summary>Wires the SECOND run (the first reconnect).</summary>
        internal void StartSecondRun(RegisterResponse response, Exception? heartbeatFailure = null) =>
            StartNextRun(response, heartbeatFailure: heartbeatFailure);

        /// <summary>Wires the THIRD run, with optional one-shot wiring for the reconnect vector.</summary>
        internal void StartThirdRun(
            RegisterResponse response,
            Exception? openFailure = null,
            Exception? heartbeatFailure = null) =>
            StartNextRun(response, openFailure: openFailure, heartbeatFailure: heartbeatFailure);

        /// <summary>The CURRENT run's stream token (captured by the factory seam).</summary>
        internal CancellationToken CurrentStreamToken { get; private set; }

        /// <summary>The reader instance the CURRENT run's factory handed into its stream.</summary>
        internal ChannelResponseReader? HandedReader { get; private set; }

        internal Task<WorkerConnection> WaitForPublishedConnectionAsync() =>
            PublishedConnections[^1].WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        /// <summary>
        /// Pushes a genuine assignment on the CURRENT run's stream and returns its execution
        /// task once the body has entered.
        /// </summary>
        internal async Task<Task> PushAssignmentAsync(string taskId)
        {
            Responses.Push(ResultAssignment(taskId));
            await Runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            return GetActiveExecution(Service);
        }

        internal void Push(OrchestratorMessage message) => Responses.Push(message);

        /// <summary>Ends the CURRENT run's stream: the controlled stream loss.</summary>
        internal void CompleteStream() => Responses.TryComplete();

        /// <summary>Joins the CURRENT run and asserts its clean outcome.</summary>
        internal async Task<WorkerRunOutcome> JoinRunAsync()
        {
            var outcome = await Run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerRunOutcome.WorkStreamEnded, outcome);
            return outcome;
        }

        /// <summary>
        /// TEARDOWN: release every run's gates, end every reader, drain any retained
        /// assignment, and join every run under the bounded failsafe.
        /// </summary>
        internal async Task TeardownAsync()
        {
            foreach (var responses in Readers)
                responses.TryComplete();
            foreach (var requests in Requests)
                requests.ReleaseAll();
            Runner.ReleaseAll();
            Exception? primary = null;
            try { await Service.DrainCarriedAssignmentAsync(); }
            catch (Exception ex) { primary = ex; }
            try
            {
                await JoinAllForTeardownAsync(
                    [.. Runs.Select((run, index) => ($"run {index}", (Task?)run))]);
            }
            catch (Exception ex) { primary ??= ex; }
            finally
            {
                try { TryDispose(Service); }
                finally
                {
                    try { _gitRestore.Dispose(); }
                    catch (Exception ex) { primary ??= ex; }
                }
            }
            if (primary is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }

    /// <summary>
    /// THE RUNNER DOUBLE for the reconnect plan: the gated prompt runner PLUS the observables
    /// the reconnect criteria need - a counted <c>ConnectAsync</c>, an ORDERED event log (the
    /// carried drain and the runner preparation are recorded in the order production reaches
    /// them), and the reset bookkeeping the (f2b) replacement-drain proof uses.
    /// </summary>
    private sealed class ObservingRunner : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private readonly HashSet<string> _cancelled = [];
        private readonly HashSet<string> _startedIds = [];
        private readonly HashSet<string> _resetIds = [];
        private int _connectCount;
        private string? _taskId;

        internal int ConnectCount => Volatile.Read(ref _connectCount);
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal IReadOnlyList<string> EventLog
        {
            get { lock (_gate) return [.. _events]; }
        }

        internal bool WasCancelled(string taskId)
        {
            lock (_gate) return _cancelled.Contains(taskId);
        }

        internal bool ResetEntered(string taskId)
        {
            lock (_gate) return _resetIds.Contains(taskId);
        }

        private int _resetCount;

        /// <summary>How many session resets production has ENTERED (keyed by order).</summary>
        internal int ResetCount => Volatile.Read(ref _resetCount);

        internal Task PromptStarted(string taskId) => Slot(_started, taskId).Task;

        internal void Release(string taskId) => Slot(_release, taskId).TrySetResult();

        internal void ReleaseAll()
        {
            lock (_gate)
                foreach (var source in _release.Values)
                    source.TrySetResult();
        }

        internal void RecordEvent(string name)
        {
            lock (_gate) _events.Add(name);
        }

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var id = _taskId ?? "(unknown)";
            TaskCompletionSource started;
            lock (_gate)
            {
                _startedIds.Add(id);
                started = Slot(_started, id);
            }

            started.TrySetResult();

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
        }

        private TaskCompletionSource Slot(Dictionary<string, TaskCompletionSource> map, string key)
        {
            lock (_gate)
            {
                if (!map.TryGetValue(key, out var source))
                {
                    source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    map[key] = source;
                }

                return source;
            }
        }

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) => _taskId = taskId;
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

        internal Func<CancellationToken, Task>? ConnectEnteredHook { get; set; }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            RecordEvent("connect");
            Interlocked.Increment(ref _connectCount);
            if (ConnectEnteredHook is { } hook)
                await hook(ct);
        }

        public Task ResetSessionAsync(
            string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
        {
            lock (_gate) _resetIds.Add(_taskId ?? "(unknown)");
            Interlocked.Increment(ref _resetCount);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
