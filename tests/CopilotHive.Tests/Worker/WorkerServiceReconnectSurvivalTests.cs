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
            try
            {
                responses.TryComplete();
                harness.Runner.ReleaseAll();
                await JoinAllForTeardownAsync(("run", harness.Run));
                TryDispose(service);
            }
            finally
            {
                // This test has its OWN teardown and never calls CarryHarness.TeardownAsync, so
                // the hermetic config-repo seam's restore handle is released HERE — even when the
                // join or the dispose above throws.
                harness.DisposeGitRestore();
            }
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
            CancellationToken? observedToken = null;
            var completesAtHook = -1;
            var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Arm the hook while execution is STILL HELD: adoption and release can now never
            // advance the reporter/delivery past this exact pre-Complete instant first.
            service.CarriedBeforeCompleteSendHook = token =>
            {
                completesAtHook = harness.AdoptedRequests!.Completes.Count;
                observedToken = token;
                hookEntered.TrySetResult();
                return hookRelease.Task;
            };

            var adoptedRequests = harness.AdoptConnection() is { } adopted ? harness.AdoptedRequests! : throw new Xunit.Sdk.XunitException("Adoption failed.");
            harness.Runner.Release("task-A");
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
            var completesAtHook = -1;
            var readiesAtHook = -1;
            var claimStateAtHook = -1;
            var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Arm the hook while execution is STILL HELD: no adopted Complete/Ready claim can
            // race past this observation point before the delivery has a target.
            service.CarriedBeforeReadyClaimHook = () =>
            {
                completesAtHook = harness.AdoptedRequests!.Completes.Count;
                readiesAtHook = harness.AdoptedRequests.AssignmentReadyCount;
                claimStateAtHook = GetReadyClaimState(GetOwnerReadyClaim(owner));
                hookEntered.TrySetResult();
                return hookRelease.Task;
            };

            var adoptedRequests = harness.AdoptConnection() is { } adopted ? harness.AdoptedRequests! : throw new Xunit.Sdk.XunitException("Adoption failed.");
            harness.Runner.Release("task-A");
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
    /// THE SHARED HERMETIC CONFIG-REPO SEAM for every fixture that runs a REAL assignment
    /// through the production provisioner path (the <see cref="CarryHarness"/> runs and the
    /// <see cref="ReconnectPlan"/> runs): a unique temp directory plus a healthy
    /// <see cref="FakeGitLauncher"/> installed through
    /// <see cref="WorkerServiceConfigRepoHarness.InstallProcessRunner"/>.
    /// </summary>
    /// <remarks>
    /// The per-assignment preparation (<c>WorkerService.PrepareConfigRepoAsync</c>) probes the
    /// config repo with real git and then creates <c>&lt;dir&gt;/agents</c>, so a fixture that
    /// hands the service an AMBIENT directory such as <c>/config-repo</c> depends on the host
    /// filesystem (that path does not exist on the CI runner) and on the real git binary. The
    /// temp directory exists and is writable everywhere, and the launcher answers the probe
    /// deterministically: worktree root = the temp directory, origin = the configured URL, and
    /// an empty successful result for every other command.
    /// </remarks>
    /// <param name="configRepoUrl">The origin URL the config-repo probe must observe.</param>
    /// <returns>
    /// The temp config-repo directory, plus the restore handle for the static
    /// <see cref="GitOperations.ProcessRunner"/> override the helper installed — the CALLER owns
    /// that handle and must dispose it on every exit path.
    /// </returns>
    private static (string ConfigRepoDir, IDisposable GitRestore) CreateHermeticConfigRepo(
        string configRepoUrl)
    {
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
                return new GitProcessResult(0, configRepoUrl + "\n", string.Empty);
            return new GitProcessResult(0, string.Empty, string.Empty);
        });
        return (configRepoDir, WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher));
    }

    /// <summary>
    /// THE CARRY HARNESS: one service, one REAL sequential run, a fake duplex stream whose
    /// pending writes fault on dispose and honor write tokens, and the reflection observers the
    /// focused tests share. The run starts in <see cref="Create"/>, so every test observes the
    /// real <see cref="WorkerService.RunAsync"/> lifecycle (registration, publication, heartbeat
    /// seam, initial Ready) rather than a synthetic publication. The service is built over the
    /// SHARED hermetic config-repo directory (<see cref="CreateHermeticConfigRepo"/>), so the
    /// production per-assignment preparation never touches the ambient filesystem; the harness
    /// owns that seam's restore handle and releases it through
    /// <see cref="DisposeGitRestore"/>.
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
            int[] disposeHoldArmed,
            IDisposable gitRestore)
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
            _gitRestore = gitRestore;
        }

        /// <summary>
        /// THE HERMETIC SEAM'S RESTORE HANDLE: the static
        /// <see cref="GitOperations.ProcessRunner"/> override
        /// <see cref="CreateHermeticConfigRepo"/> installed for THIS harness's service.
        /// </summary>
        private readonly IDisposable _gitRestore;

        /// <summary>0 = not yet disposed; 1 = disposed. Guarded by <see cref="Interlocked"/>.</summary>
        private int _gitRestoreDisposed;

        /// <summary>
        /// Releases the hermetic config-repo seam's <see cref="GitOperations.ProcessRunner"/>
        /// override. IDEMPOTENT: a second (or later) call is a harmless no-op, so a harness can
        /// be released by both a test's own teardown wrapper and <see cref="TeardownAsync"/>
        /// without a double dispose reaching the restore closure.
        /// </summary>
        internal void DisposeGitRestore()
        {
            if (Interlocked.Exchange(ref _gitRestoreDisposed, 1) != 0)
                return;

            _gitRestore.Dispose();
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
        /// <remarks>
        /// The service runs over the SHARED HERMETIC CONFIG-REPO SEAM
        /// (<see cref="CreateHermeticConfigRepo"/>), so the production per-assignment preparation
        /// never touches the ambient filesystem or the real git binary. The returned harness OWNS
        /// that seam's restore handle — released through <see cref="DisposeGitRestore"/> — and
        /// every failure path below releases it in the outer <c>finally</c>, after unwinding a run
        /// that had already started: no live producer and no leaked
        /// <see cref="GitOperations.ProcessRunner"/> override survives a failed
        /// <see cref="Create"/>.
        /// </remarks>
        internal static CarryHarness Create()
        {
            var (configRepoDir, gitRestore) = CreateHermeticConfigRepo(ConfigRepoUrl);

            // Set ONLY once the fully built harness is on its way to the caller: the outer
            // finally reads it as the ONE fact deciding whether the hermetic seam travels with
            // the returned harness or is restored here.
            var ownershipTransferred = false;

            // EVERYTHING after the helper call lives in this ONE try: service construction, the
            // runner install, the run start, the initial-Ready wait and the published-connection
            // assertion. The locals are null until their step runs, so the catch knows whether
            // there is a run to unwind and the finally always releases the hermetic seam.
            WorkerService? service = null;
            CarryRequestStream? requests = null;
            ChannelResponseReader? responses = null;
            CarryPromptRunner? runner = null;
            Task<WorkerRunOutcome>? run = null;
            try
            {
                service = new WorkerService(
                    "http://localhost:9999", WorkerId, ["coder"], configRepoDir);
                runner = new CarryPromptRunner();
                InstallRunner(service, runner);

                requests = new CarryRequestStream();
                responses = new ChannelResponseReader();
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

                run = service.RunAsync(TestContext.Current.CancellationToken);

                // The initial Ready is EXPECTED and is awaited here, so every later write
                // assertion is about the ASSIGNMENT's own writes only.
                requests.WaitForWriteCountAsync(1)
                    .WaitAsync(Failsafe, TestContext.Current.CancellationToken)
                    .GetAwaiter().GetResult();

                var connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
                var created = new CarryHarness(
                    service, requests, responses, runner, connection, invoker, streamToken, run,
                    disposeHold, disposeHoldArmed, gitRestore);

                // TRANSFER: the harness now owns the restore handle, so the finally below must
                // leave it installed for the run's own assignments.
                ownershipTransferred = true;
                return created;
            }
            catch (Exception)
            {
                // A harness failure must never strand the run: unwind through the same teardown
                // shape the tests use, so no live producer survives a failed Create(). A failure
                // BEFORE the run started (service construction, runner install) has nothing to
                // unwind.
                if (run is not null)
                {
                    responses!.TryComplete();
                    requests!.ReleaseAll();
                    runner!.ReleaseAll();
                    try
                    {
                        run.WaitAsync(Failsafe, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // The run's own outcome is not the fixture's concern here.
                    }

                    TryDispose(service!);
                }

                throw;
            }
            finally
            {
                // EVERY failure path releases the hermetic seam here, including a failure that
                // never started the run. The success path transferred the handle to the returned
                // harness instead.
                if (!ownershipTransferred)
                    gitRestore.Dispose();
            }
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
        /// an assertion failed. The hermetic config-repo seam's restore handle is released in a
        /// <c>finally</c> AFTER the service is disposed, so a failing dispose can never keep the
        /// fake launcher installed; its own exception is recorded the same way the other teardown
        /// failures are and rethrown after the joins.
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
            finally
            {
                try { TryDispose(harness.Service); }
                finally
                {
                    try { harness.DisposeGitRestore(); }
                    catch (Exception ex) { primary ??= ex; }
                }
            }

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

    /// <summary>
    /// THE ADOPTION MILESTONE: completes when the service's ADOPTION PUBLICATION holds a connection.
    /// The adopted run publishes it as its last step before the message loop, and a test that wants
    /// to make a call "after adoption" must observe that fact rather than race the run's remaining
    /// setup.
    /// </summary>
    /// <remarks>
    /// NO POLLING. It uses production's OWN adoption-change signal — the <c>_adoptionChanged</c>
    /// source that <c>PublishAdoption</c> completes and replaces on every change — read under
    /// production's <c>_adoptionLock</c> TOGETHER with the publication, exactly the way
    /// <c>AwaitAdoptedConnectionAsync</c> reads them. So there is no gap in which a publication can
    /// be missed, and the loop re-evaluates exactly ONCE per real publication change (a clear is a
    /// change too, and simply re-parks). The bound is a FAILURE GUARD only.
    /// </remarks>
    private static async Task<WorkerConnection> WaitForAdoptedConnectionAsync(
        WorkerService service, string because)
    {
        var serviceType = typeof(WorkerService);
        var adoptionLock = serviceType
            .GetField("_adoptionLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;
        var changedField = serviceType
            .GetField("_adoptionChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;

        while (true)
        {
            Task changed;
            lock (adoptionLock)
            {
                if (GetAdoptedConnectionOrNull(service) is { } adopted)
                    return adopted;

                changed = ((TaskCompletionSource)changedField.GetValue(service)!).Task;
            }

            try
            {
                await changed.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{because} (no adoption was published within the failsafe bound).");
            }
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
        private readonly List<WorkerMessage> _toolRequests = [];
        private readonly Dictionary<int, TaskCompletionSource> _toolRequestCountWaiters = [];
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
        /// Every <c>ToolRequest</c> write in arrival order — the bridge's own traffic, separate from
        /// Readies and Completes, so a retargeting assertion can name the exact tool call, its
        /// task ID and the identity it was written with.
        /// </summary>
        internal IReadOnlyList<WorkerMessage> ToolRequests
        {
            get { lock (_gate) return [.. _toolRequests]; }
        }

        /// <summary>
        /// Completes once at least <paramref name="count"/> TOOL REQUEST writes landed on this
        /// stream. It is the bounded arrival observation for a bridge call: the caller's own await
        /// already returned, so this only turns a missing write into a NAMED failure.
        /// </summary>
        internal Task WaitForToolRequestCountAsync(int count)
        {
            lock (_gate)
            {
                if (_toolRequests.Count >= count)
                    return Task.CompletedTask;
                if (!_toolRequestCountWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _toolRequestCountWaiters[count] = waiter;
                }

                return waiter.Task;
            }
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
                _initialWriteHold?.TrySetResult();
            }
        }

        /// <summary>
        /// THE INITIAL-WRITE HOLD — OFF unless <see cref="ArmInitialWriteHold"/> is called, so every
        /// existing use of this fake keeps its behavior exactly. When armed, the stream's FIRST write
        /// (the run's initial Ready) is RECORDED as usual (the write-count waiters fire), then PARKS
        /// inside the fake until <see cref="ReleaseInitialWrite"/>. Production awaits that write before
        /// its message loop enters the first read, so while it is held the write-before-first-read
        /// window is FORCED open, independent of scheduling.
        /// </summary>
        internal void ArmInitialWriteHold()
        {
            lock (_gate)
                _initialWriteHold ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Completes once the held initial write has ENTERED the fake and been recorded.</summary>
        internal Task InitialWriteHeld => _initialWriteHeld.Task;

        /// <summary>Releases a held initial write (a no-op when none is armed).</summary>
        internal void ReleaseInitialWrite()
        {
            lock (_gate)
                _initialWriteHold?.TrySetResult();
        }

        /// <summary>
        /// Whether an initial write is still PARKED in this fake (armed but not yet released).
        /// Observation only: it reads the hold source's completion state, so a released (or
        /// cancelled) hold reports <c>false</c>.
        /// </summary>
        internal bool IsInitialWriteHeld
        {
            get
            {
                lock (_gate)
                    return _initialWriteHold is { } hold && !hold.Task.IsCompleted;
            }
        }

        private TaskCompletionSource? _initialWriteHold;
        private readonly TaskCompletionSource _initialWriteHeld =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                _initialWriteHold?.TrySetCanceled();
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
            {
                // TOOL REQUESTS are recorded (a bridge call's own arrival observation) even though
                // they are never gated — the reconnect criteria assert WHICH connection saw them.
                if (message.PayloadCase == WorkerMessage.PayloadOneofCase.ToolRequest)
                {
                    lock (_gate)
                    {
                        _toolRequests.Add(message);
                        _writes.Add(message);
                        SignalToolRequestCountLocked();
                        SignalWriteCountLocked();
                    }
                }

                return;
            }

            int ordinal;
            TaskCompletionSource? readyRelease;
            TaskCompletionSource? initialHold;
            lock (_gate)
            {
                ordinal = _readies.Count - (CountsFirstReadyAsInitial ? 1 : 0);
                _readies.Add(message);
                _writes.Add(message);
                readyRelease = ordinal >= _holdReadiesFrom ? Slot(_readyRelease, ordinal) : null;
                if (_disposed && readyRelease is not null) readyRelease.TrySetCanceled();
                else if (_releaseImmediately && readyRelease is not null) readyRelease.TrySetResult();

                // THE ARMED INITIAL-WRITE HOLD applies to the stream's FIRST write only, and only
                // after it has been RECORDED above (so the write-count waiters still observe it).
                initialHold = _writes.Count == 1 ? _initialWriteHold : null;

                SignalWriteCountLocked();
                foreach (var (threshold, waiter) in _readyCountWaiters.ToArray())
                    if (_readies.Count >= threshold)
                    {
                        _readyCountWaiters.Remove(threshold);
                        waiter.TrySetResult();
                    }
                if (ordinal >= 0) Slot(_readyEntered, ordinal).TrySetResult();
            }

            if (initialHold is not null)
            {
                // The write is recorded and PENDING: the caller's WriteAsync has not returned, so the
                // message loop that follows it in production cannot have started a read yet. Honors
                // the forwarded token exactly like every other parked write in this fake.
                _initialWriteHeld.TrySetResult();
                await initialHold.Task.WaitAsync(ct);
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

        /// <summary>
        /// COMPLETES the client's half of the stream, so this fake is safe as a SOLE shutdown path:
        /// closing the request stream RELEASES the opt-in initial-write hold.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WHY IT RELEASES THE HOLD. <see cref="ArmInitialWriteHold"/> parks the stream's first write
        /// inside <see cref="WriteCoreAsync"/> until <see cref="ReleaseInitialWrite"/> (or
        /// <see cref="ReleaseAll"/>, or a disposal). A future fixture that relies on
        /// <c>CompleteAsync</c> alone to shut a run down would otherwise leave that parked write
        /// unresolved, so the run it belongs to could never reach quiescence — a hang in the fixture,
        /// not a production signal. Releasing here keeps the hold an OPT-IN WINDOW that always has a
        /// way out.
        /// </para>
        /// <para>
        /// The release is the SAME completion every existing releaser performs, so an UNARMED fake is
        /// completely unaffected: with no armed hold the field is <c>null</c> and this is a no-op,
        /// exactly as the previous <c>Task.CompletedTask</c> body was. Nothing else about the
        /// <c>_disposed</c> / <c>_releaseImmediately</c> bookkeeping changes, and no production
        /// behavior is involved — this is a test double.
        /// </para>
        /// </remarks>
        public Task CompleteAsync()
        {
            ReleaseInitialWrite();
            return Task.CompletedTask;
        }

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

        /// <summary>Signals every tool-request arrival waiter whose threshold is now met. Caller holds the gate.</summary>
        private void SignalToolRequestCountLocked()
        {
            List<int> ready = [];
            foreach (var (threshold, _) in _toolRequestCountWaiters)
            {
                if (_toolRequests.Count >= threshold)
                    ready.Add(threshold);
            }

            foreach (var threshold in ready)
            {
                if (_toolRequestCountWaiters.Remove(threshold, out var waiter))
                    waiter.TrySetResult();
            }
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
    /// THE NO-RESULT PRODUCER DOUBLE for the carried-delivery vector: a
    /// <see cref="WorkerConfigProvisioner"/> whose ONE provisioning fetch PARKS until it is released
    /// and then fails with a non-<see cref="Grpc.Core.RpcException"/> error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provisioning fetch is the FIRST thing an assignment body does that can be parked for an
    /// unbounded time, so it is the deterministic way to hold a body INSIDE its execution without any
    /// timer: the body is parked before any terminal result can exist, and releasing it makes the body
    /// terminate NORMALLY (its own failure handler contains the error) with the result holder still
    /// EMPTY — no completion is ever fabricated.
    /// </para>
    /// <para>
    /// The provisioner is installed through <see cref="WorkerService.TestProvisioner"/> BEFORE the
    /// first run's connection exists, because a connection captures that instance at construction; a
    /// later assignment of the property could not influence a body already running against the
    /// captured one.
    /// </para>
    /// </remarks>
    private sealed class ParkingProvisioner
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the assignment body has entered its provisioning fetch.</summary>
        internal Task Entered => _entered.Task;

        /// <summary>Releases the parked fetch; it then FAILS, so no result is ever produced.</summary>
        internal void Release() => _release.TrySetResult();

        /// <summary>The provisioner the service hands to its first connection.</summary>
        internal WorkerConfigProvisioner Provisioner { get; }

        internal ParkingProvisioner()
        {
            Provisioner = new WorkerConfigProvisioner(
                "worker-reconnect",
                async (_, _) =>
                {
                    _entered.TrySetResult();
                    await _release.Task;
                    throw new InvalidOperationException("fixture provisioning refused");
                });
        }
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
        private int _workerConfigFetchCount;
        private int _loadCount;
        private int _saveCount;
        private string? _lastLoadSessionId;
        private string? _lastSaveSessionId;
        private string? _lastSavedJson;

        /// <summary>Snapshots the Register requests, oldest first.</summary>
        internal IReadOnlyList<RegisterRequest> Registers
        {
            get { lock (_gate) return [.. _registers]; }
        }

        /// <summary>
        /// How many <c>GetWorkerConfig</c> fetches THIS connection's provisioner performed — the
        /// observable that names WHICH connection a provisioning call reached (the eager per-assignment
        /// site and the lazy callback both route through the connection's own checked entry point).
        /// </summary>
        internal int WorkerConfigFetchCount => Volatile.Read(ref _workerConfigFetchCount);

        /// <summary>How many session loads reached THIS connection's client.</summary>
        internal int LoadCount => Volatile.Read(ref _loadCount);

        /// <summary>How many session saves reached THIS connection's client.</summary>
        internal int SaveCount => Volatile.Read(ref _saveCount);

        internal string? LastLoadSessionId { get { lock (_gate) return _lastLoadSessionId; } }
        internal string? LastSaveSessionId { get { lock (_gate) return _lastSaveSessionId; } }
        internal string? LastSavedJson { get { lock (_gate) return _lastSavedJson; } }

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
            object payload = method.FullName switch
            {
                "/copilothive.HiveOrchestrator/Register" => RespondRegister(request),
                "/copilothive.HiveOrchestrator/GetWorkerConfig" => FetchWorkerConfig(),
                "/copilothive.HiveOrchestrator/GetSession" => LoadSession(request),
                "/copilothive.HiveOrchestrator/SaveSession" => SaveSession(request),
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

        /// <summary>
        /// ONE provisioning fetch on THIS connection. The credential-free response is deliberate: it
        /// exercises the checked provisioning path (fetch, snapshot, revert-safe apply) without
        /// touching the process environment, so a test can assert WHICH connection was provisioned
        /// through while the assignment's model stays irrelevant to the assertion.
        /// </summary>
        private object FetchWorkerConfig()
        {
            Interlocked.Increment(ref _workerConfigFetchCount);
            return new GetWorkerConfigResponse
            {
                LlmProvider = "copilot",
                ConfigRepoUrl = ConfigRepoUrl,
            };
        }

        private object LoadSession<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _loadCount);
            lock (_gate) _lastLoadSessionId = (request as GetSessionRequest)?.SessionId;
            return new GetSessionResponse { Found = false };
        }

        private object SaveSession<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _saveCount);
            lock (_gate)
            {
                _lastSaveSessionId = (request as SaveSessionRequest)?.SessionId;
                _lastSavedJson = (request as SaveSessionRequest)?.SessionJson;
            }

            return new SaveSessionResponse { Success = true };
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
            // lands after the eligibility publishes. The count of 2 awaited below includes the
            // failed carried Ready, so task-B's own Ready is the second.
            plan.Runner.Release("task-B");
            await plan.CurrentRequests.AssignmentCompleteEntered(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CurrentRequests.ReleaseAll();
            await plan.CurrentRequests.WaitForAssignmentReadyCountAsync(2)
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
    /// <see cref="WorkerProgramDecisions.DecideOutcome"/> and
    /// <see cref="WorkerProgramDecisions.DrainCarriedAssignmentFailedAsync"/> ARE extracted
    /// production decisions and are exercised directly here: WorkStreamEnded reconnects on the
    /// SAME service, RegistrationRejected stops, and a genuinely throwing drain is reported
    /// once before exactly one Dispose, with exit code 1. Program.cs still owns its top-level
    /// loop, backoff and final disposal; those effects cannot be driven in-process through the
    /// decision helpers alone. The Program-level structural check in
    /// WorkerRedactionIntegrationTests binds Program's ACTUAL drain-helper invocation (whose
    /// delegate invokes the service drain) AFTER the attempt loop and BEFORE final Dispose;
    /// the real-process fatal-path test checks top-level error handling. Together they cover
    /// the call-site wiring that this decision test's mirror cannot establish by itself.
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

    // ══════════════════════════════════════════════════════════════════════════
    // GOAL retarget-connection-bound-dependencies-after-adoption — the CALL-TIME
    // resolution of a carried assignment's bridge/session calls and of the ONE
    // service-owned lazy provisioning callback.
    // ══════════════════════════════════════════════════════════════════════════
    //
    // Everything here drives the REAL sequential RunAsync lifecycle (registration, publication,
    // heartbeat seam, a genuine assignment, EOF, the adopted registration, the adoption publication)
    // and then calls the dependency object the assignment's EXECUTOR actually received — captured
    // through the runner's own SetToolBridge seam, never through a hand-built stand-in. Every gate is
    // a TCS or a counted write; no sleeps and no polling anywhere.

    /// <summary>
    /// (a) THE CALL-TIME RETARGET, OBSERVED BETWEEN STREAMS AND AFTER ADOPTION. A carried assignment's
    /// bridge and session calls fail with the EXISTING disconnected error while nothing is adopted
    /// (the retired connection is still the only target), and after the second run adopts the task
    /// they ALL go to connection 2: progress, narrative, GetSession and SaveSession. Connection 1
    /// sees nothing after the carry, so the retarget is a real movement of new calls, not a duplicate
    /// emission.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOFNESS: with the captured binding still in place every call after the carry resolves
    /// the RETIRED connection 1, so the four adopted assertions fail by name — the writes never appear
    /// on stream 2 and the calls raise the disconnected error instead of returning. The reverse
    /// regression (a NON-carried assignment following the publication) is covered by (c) and by the
    /// unchanged binding fixture.
    /// </remarks>
    [Fact]
    public async Task RetargetCallTime_a_CarriedAssignment_BridgeAndSessionCallsFollowTheAdoptedConnection()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        try
        {
            var execution = await plan.PushAssignmentAsync("task-A");
            var bridge = await plan.Runner.BridgeCaptured
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var sessions = Assert.IsAssignableFrom<ISessionClient>(bridge);

            // Still running on run 1: the assignment is not carried yet, so the calls use the
            // CAPTURED connection 1 (proved by run 1's own writer taking them).
            await bridge.ReportProgressAsync(
                    "task-A", "pre-loss", "before the stream loss", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.Requests[0].WaitForToolRequestCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(
                "report_progress", Assert.Single(plan.Requests[0].ToolRequests).ToolRequest.ToolName);

            // STREAM LOSS: the assignment is carried and connection 1 is retired.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));
            Assert.True(plan.Connections[0].IsRetired);
            Assert.Null(GetAdoptedConnectionOrNull(plan.Service));

            // BETWEEN STREAMS (nothing adopted): both calls fail with the EXISTING disconnected error.
            var progressFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => bridge.ReportProgressAsync(
                    "task-A", "carried", "no adoption yet", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, progressFailure.Message);

            var loadFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sessions.GetSessionAsync("goal-A:coder", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, loadFailure.Message);

            // NOTHING MORE REACHED CONNECTION 1 — the failed calls wrote nothing on the retired stream.
            Assert.Single(plan.Requests[0].ToolRequests);
            Assert.Equal(0, plan.Invokers[0].LoadCount);
            Assert.Equal(0, plan.Invokers[0].SaveCount);

            // RUN 2 — the adopted registration: the adoption publication is the retarget target.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var adopted = await plan.WaitForPublishedConnectionAsync();
            Assert.Equal("task-A", Assert.Single(plan.Invokers[1].Registers).CurrentTaskId);
            Assert.Same(adopted, GetAdoptedConnection(plan.Service));

            // EXECUTION IS STILL RUNNING: the calls below are made while the assignment is STILL
            // Carried, which is exactly the state the retarget exists for.
            Assert.False(execution.IsCompleted);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // 1. PROGRESS → connection 2.
            await bridge.ReportProgressAsync(
                    "task-A", "adopted", "after adoption", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.WaitForToolRequestCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var progress = Assert.Single(plan.CurrentRequests.ToolRequests);
            Assert.Equal("report_progress", progress.ToolRequest.ToolName);
            Assert.Equal("task-A", progress.ToolRequest.TaskId);
            Assert.Equal("worker-reconnect-a", progress.WorkerId);

            // 2. NARRATIVE → connection 2.
            await bridge.ReportNarrativeAsync(
                    "task-A", "carried narrative", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.WaitForToolRequestCountAsync(2)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var narrative = plan.CurrentRequests.ToolRequests[1];
            Assert.Equal("report_narrative", narrative.ToolRequest.ToolName);
            Assert.Equal("task-A", narrative.ToolRequest.TaskId);

            // 3. GetSession → connection 2's CLIENT (the unary RPC path).
            var loaded = await sessions
                .GetSessionAsync("goal-A:coder", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(loaded);
            Assert.Equal(1, plan.Invokers[1].LoadCount);
            Assert.Equal("goal-A:coder", plan.Invokers[1].LastLoadSessionId);

            // 4. SaveSession → connection 2's CLIENT.
            await sessions
                .SaveSessionAsync("goal-A:coder", """{"turn":1}""", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, plan.Invokers[1].SaveCount);
            Assert.Equal("goal-A:coder", plan.Invokers[1].LastSaveSessionId);
            Assert.Equal("""{"turn":1}""", plan.Invokers[1].LastSavedJson);

            // NOTHING MORE WENT TO CONNECTION 1: it still holds exactly the ONE pre-loss tool request
            // and no session RPC at all.
            Assert.Single(plan.Requests[0].ToolRequests);
            Assert.Equal(0, plan.Invokers[0].LoadCount);
            Assert.Equal(0, plan.Invokers[0].SaveCount);

            // EXACTLY the two fire-and-forget bridge calls reached connection 2's writer (the
            // response-bearing ones would register a pending wait, of which there are none here).
            Assert.Equal(2, plan.CurrentRequests.ToolRequests.Count);
            Assert.Equal(0, adopted.PendingToolResponseCount);

            plan.CompleteStream();
            await plan.JoinRunAsync();
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (b) THE RETARGET FOLLOWS A SECOND LOSS. After the assignment is adopted on stream 2 it is lost
    /// again and adopted on stream 3: the SAME dependency object then sends its calls to connection 3,
    /// and neither connection 1 nor connection 2 sees any of them.
    /// </summary>
    /// <remarks>
    /// The mid-flight adopted run ends by stream loss with its carried delivery's Complete PARKED in
    /// that stream's fake (armed before the run started), so the run ends with the assignment still
    /// Carried — the second loss the carry contract supports — and the delivery returns to its wait
    /// instead of finishing. While the delivery is parked the send gate is held by its own write, so
    /// the retarget on stream 2 is proved with the UNGATED session pair (unary RPCs, outside the send
    /// gate); the bridge calls are proved on stream 3. The state cell is asserted at every step, so a
    /// mutant that retargets only for the FIRST adoption (for example by caching the first adopted
    /// connection) fails the connection-3 assertions by name.
    /// </remarks>
    [Fact]
    public async Task RetargetCallTime_b_SecondLossAndReadoption_SameDependencyFollowsConnectionThree()
    {
        var plan = ReconnectPlan.StartAsync(
            "task-A", register2Adopted: true, holdReportComplete: true);
        try
        {
            var execution = await plan.PushAssignmentAsync("task-A");
            var bridge = await plan.Runner.BridgeCaptured
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var sessions = Assert.IsAssignableFrom<ISessionClient>(bridge);

            // RUN 1 → carried (the reporter parks in its held Complete, which the disposal faults).
            plan.Runner.Release("task-A");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));
            Assert.True(plan.Connections[0].IsRetired);

            // The BODY finished with the run-1 result already retained: only the DELIVERY is still
            // outstanding, which is what keeps the assignment Carried across the next two runs.
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.NotNull(GetRetainedResult(plan.Service));

            // RUN 2 — adopted, with ITS COMPLETES HELD so the carried delivery cannot finish on this
            // stream: the assignment is retargeted onto connection 2 and stays Carried.
            plan.PendingHoldCompletesFrom = 0;
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var adopted2 = await plan.WaitForPublishedConnectionAsync();
            Assert.Same(
                adopted2,
                await WaitForAdoptedConnectionAsync(plan.Service, "The adopted run must publish the adoption."));
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE SESSION PAIR follows the adoption onto connection 2's CLIENT, while the assignment
            // is still Carried (the parked Complete has not transitioned it).
            Assert.Null(await sessions
                .GetSessionAsync("goal-A:coder", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            await sessions
                .SaveSessionAsync("goal-A:coder", """{"turn":2}""", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, plan.Invokers[1].LoadCount);
            Assert.Equal(1, plan.Invokers[1].SaveCount);
            Assert.Equal("goal-A:coder", plan.Invokers[1].LastSaveSessionId);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // NOTHING NEW REACHED CONNECTION 1.
            Assert.Equal(0, plan.Invokers[0].LoadCount);
            Assert.Equal(0, plan.Invokers[0].SaveCount);
            Assert.Empty(plan.Requests[0].ToolRequests);

            // SECOND LOSS: stream 2 ends; its disposal faults the parked Complete, so the delivery
            // returns to its wait and the assignment STAYS CARRIED for the next adoption.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.True(plan.Connections[1].IsRetired);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // BETWEEN THE TWO ADOPTED RUNS the retired connection 2 is refused: the call fails with
            // the EXISTING disconnected error rather than resurrecting the first adoption. (The
            // session pair is used deliberately here: the parked carried Complete still holds the
            // shared send gate, and these unary RPCs are entirely outside it.)
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sessions.GetSessionAsync("goal-A:coder", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);
            Assert.Null(GetAdoptedConnectionOrNull(plan.Service));

            // RUN 3 — adopted again: the SAME dependency object now sends to connection 3.
            plan.StartThirdRun(RegisterResponseFor(adopted: true));
            var adopted3 = await plan.WaitForPublishedConnectionAsync();
            await WaitForAdoptedConnectionAsync(plan.Service, "The third run must publish the adoption.");
            Assert.NotSame(adopted2, adopted3);
            await bridge.ReportProgressAsync(
                    "task-A", "on-3", "adopted by the third stream", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.WaitForToolRequestCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var progress = Assert.Single(plan.CurrentRequests.ToolRequests);
            Assert.Equal("report_progress", progress.ToolRequest.ToolName);
            Assert.Equal("worker-reconnect-a", progress.WorkerId);

            await bridge.ReportNarrativeAsync(
                    "task-A", "third stream narrative", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.WaitForToolRequestCountAsync(2)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE FIRST ADOPTION'S PUBLICATION IS GONE — a call that arrives now can only ever reach
            // the CURRENT adoption, so the assertions above cannot have passed by reaching connection 2.
            await sessions
                .SaveSessionAsync("goal-A:coder", """{"turn":3}""", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, plan.Invokers[1].SaveCount);
            Assert.Equal(1, plan.Invokers[2].SaveCount);
            Assert.Equal("""{"turn":3}""", plan.Invokers[2].LastSavedJson);

            // NEITHER EARLIER CONNECTION SAW THE RETARGETED CALLS: connection 1 saw no tool request at
            // all, connection 2 only the session pair made while IT was adopted.
            Assert.Empty(plan.Requests[0].ToolRequests);
            Assert.Empty(plan.Requests[1].ToolRequests);
            Assert.Equal(2, plan.CurrentRequests.ToolRequests.Count);

            plan.CompleteStream();
            await plan.JoinRunAsync();
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (c) A NON-CARRIED ASSIGNMENT STILL USES ITS CAPTURED CONNECTION — the retarget is scoped to the
    /// Carried/Delivered states and nothing else. While the assignment is running on run 1 (state
    /// Open), every call goes to connection 1 even though a SECOND connection is published underneath
    /// it, and connection 2's writer, client and response registry stay completely untouched.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOFNESS: this is the negative half of the retarget. A resolver that followed the
    /// merely PUBLISHED connection (or the adoption publication unconditionally) sends these calls to
    /// connection 2, and every one of the four assertions below fails by name.
    /// </remarks>
    [Fact]
    public async Task RetargetCallTime_c_NonCarriedAssignment_StillUsesItsCapturedConnection()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: false);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            var bridge = await plan.Runner.BridgeCaptured
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var sessions = Assert.IsAssignableFrom<ISessionClient>(bridge);
            Assert.Equal(CarryStates.Open, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // PUBLICATION MOVES TO A SECOND CONNECTION — the exact move the sibling binding fixture
            // performs. The assignment is NOT carried, so its dependency must keep using its captured
            // connection and send NOTHING here.
            var second = plan.PublishStandaloneConnection();
            Assert.NotSame(plan.Connections[0], second);
            Assert.Same(second, GetPublishedConnection(plan.Service));

            await bridge.ReportProgressAsync(
                    "task-A", "captured", "still on connection one", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await bridge.ReportNarrativeAsync(
                    "task-A", "captured narrative", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(await sessions
                .GetSessionAsync("goal-A:coder", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            await sessions
                .SaveSessionAsync("goal-A:coder", """{"turn":0}""", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // EVERY call landed on the CAPTURED connection 1. Each bridge call's own await already
            // returned, so its write is recorded by now: these are IMMEDIATE assertions, which is what
            // makes a misrouted call fail by NAME rather than at a bound.
            Assert.Equal(1, plan.Invokers[0].LoadCount);
            Assert.Equal(1, plan.Invokers[0].SaveCount);
            Assert.Equal("goal-A:coder", plan.Invokers[0].LastSaveSessionId);
            Assert.Equal(2, plan.Requests[0].ToolRequests.Count);
            Assert.Equal("report_progress", plan.Requests[0].ToolRequests[0].ToolRequest.ToolName);
            Assert.Equal("report_narrative", plan.Requests[0].ToolRequests[1].ToolRequest.ToolName);

            // ...and the newly published connection 2 saw NOTHING at all.
            Assert.Empty(plan.CurrentRequests.Writes);
            Assert.Equal(0, plan.Invokers[1].LoadCount);
            Assert.Equal(0, plan.Invokers[1].SaveCount);
            Assert.Equal(0, second.PendingToolResponseCount);
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (d) A RESPONSE-BEARING CALL IN FLIGHT ON CONNECTION 1 IS NEVER REPLAYED. A
    /// <c>request_clarification</c> is REGISTERED and written on connection 1 and left parked while
    /// the stream is lost: the retirement faults its wait with the EXISTING disconnected error, and
    /// the retarget — which is about NEW calls — neither re-sends the request on connection 2 nor
    /// produces a second write anywhere.
    /// </summary>
    /// <remarks>
    /// The write is HELD inside connection 1's fake, so the loss is guaranteed to happen while the
    /// call is genuinely in flight rather than merely queued. Connection 2's tool-request traffic is
    /// asserted EMPTY, which is what makes the absence of a replay observable rather than assumed.
    /// </remarks>
    [Fact]
    public async Task RetargetCallTime_d_InFlightResponseBearingCallOnConnectionOne_IsNeverReplayed()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        try
        {
            var execution = await plan.PushAssignmentAsync("task-A");
            var bridge = await plan.Runner.BridgeCaptured
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The response-bearing call is REGISTERED and its request is written on connection 1; the
            // write itself completes, so what is in flight is the RESPONSE WAIT — parked on connection
            // 1's registry until that connection can no longer deliver a response.
            var clarification = bridge.RequestClarificationAsync(
                "task-A", "in flight question", TestContext.Current.CancellationToken);
            await plan.Requests[0].WaitForToolRequestCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(
                "request_clarification",
                Assert.Single(plan.Requests[0].ToolRequests).ToolRequest.ToolName);
            Assert.Equal(1, plan.Connections[0].PendingToolResponseCount);
            Assert.False(clarification.IsCompleted, "The response-bearing call must still be in flight.");

            // STREAM LOSS while the call is in flight: the assignment is carried and connection 1
            // retires — which faults the pending wait with the EXISTING disconnected error.
            plan.CompleteStream();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => clarification.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));
            Assert.False(execution.IsCompleted);

            // ADOPT on stream 2, then release the body so the retained result is delivered.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var adopted = await plan.WaitForPublishedConnectionAsync();
            Assert.Same(adopted, GetAdoptedConnection(plan.Service));
            plan.Runner.Release("task-A");
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // NO REPLAY: the only request on connection 1 is the ORIGINAL in-flight one, and the
            // adopted connection carries exactly the carried Complete and its Ready — no tool request
            // at all.
            Assert.Single(plan.Requests[0].ToolRequests);
            Assert.Empty(plan.CurrentRequests.ToolRequests);
            Assert.Single(plan.CurrentRequests.Completes);
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
    /// (p) THE SERVICE-OWNED LAZY PROVISIONING CALLBACK. After the assignment is carried the runner
    /// holds a NON-NULL callback that is NOT the per-run connection-captured one; invoked with nothing
    /// adopted it fails with the EXISTING disconnected error WITHOUT touching the retired connection;
    /// invoked after adoption on stream 2 it provisions through connection 2's OWN checked entry point;
    /// and once the assignment is delivered and cleared, the ordinary per-run install/detach applies
    /// again.
    /// </summary>
    /// <remarks>
    /// The provisioning observable is the per-connection <c>GetWorkerConfig</c> fetch count: the
    /// eager per-assignment site and the lazy callback both route through the SAME checked
    /// <c>EnsureProvisionedAsync</c>, so a fetch landing on a given connection's client names exactly
    /// which connection performed the provisioning. Run 1's own eager provisioning is included in
    /// connection 1's count and is asserted as such, so the later assertions are read against a known
    /// baseline rather than a bare zero.
    /// </remarks>
    [Fact]
    public async Task RetargetCallTime_p_LazyProvisionerWhileCarried_FollowsTheAdoptionAndDetachesAfterDelivery()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        try
        {
            var execution = await plan.PushAssignmentAsync("task-A");

            // THE PER-RUN INSTALL: the callback connection 1 installed is the connection's own checked
            // entry point, installed by the run that owns the assignment.
            var installedAtStart = plan.Runner.Provisioners;
            Assert.Single(installedAtStart);
            Assert.NotNull(installedAtStart[0]);
            Assert.Equal(1, plan.Invokers[0].WorkerConfigFetchCount);

            // CARRY: the assignment's stream is lost and the service installs its OWN callback.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            var afterCarry = plan.Runner.Provisioners;
            Assert.Equal(2, afterCarry.Count);
            var carried = afterCarry[^1];
            Assert.NotNull(carried);
            Assert.NotSame(installedAtStart[0], carried);

            // BETWEEN STREAMS: the callback fails with the EXISTING disconnected error and the retired
            // connection 1 is NOT touched — no new provisioning fetch on it (the baseline is unchanged)
            // and no session/tool traffic either.
            var disconnected = await Assert.ThrowsAsync<InvalidOperationException>(
                () => carried("fixture-provider/fixture-model", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, disconnected.Message);
            Assert.Equal(1, plan.Invokers[0].WorkerConfigFetchCount);
            Assert.False(execution.IsCompleted);

            // RUN 2 — ADOPTED: the SAME callback now provisions through connection 2's checked entry
            // point, while connection 1's count stays exactly where the failed call left it.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var adopted = await plan.WaitForPublishedConnectionAsync();
            Assert.Same(adopted, GetAdoptedConnection(plan.Service));
            Assert.Equal(0, plan.Invokers[1].WorkerConfigFetchCount);

            await carried("fixture-provider/fixture-model", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, plan.Invokers[1].WorkerConfigFetchCount);
            Assert.Equal(1, plan.Invokers[0].WorkerConfigFetchCount);

            // THE CALLBACK SURVIVES THE ADOPTED RUN: the adopted run skipped its own install, so the
            // service-owned callback is still the installed one (nothing replaced it mid-adoption).
            Assert.Same(carried, plan.Runner.CurrentProvisioner);

            // DELIVER, then end the adopted run: the assignment becomes Delivered and the exit
            // re-check clears it, so the carried rule no longer applies to any assignment.
            plan.Runner.Release("task-A");
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(plan.Service));

            // THE ORDINARY PER-RUN INSTALL/DETACH APPLIES AGAIN: the detach installed null at the end
            // of the adopted run, and the NEXT run installs its own connection-captured callback.
            Assert.Null(plan.Runner.CurrentProvisioner);
            plan.StartThirdRun(RegisterResponseFor(adopted: false));
            await plan.CurrentRequests.WaitForWriteCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.NotNull(plan.Runner.CurrentProvisioner);
            Assert.NotSame(carried, plan.Runner.CurrentProvisioner);
            Assert.False(plan.Runner.WasCancelled("task-A"));
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (p2) THE SERVICE-OWNED CALLBACK IS INSTALLED BY THE CARRY TRANSITION ITSELF, exactly once, and
    /// NOT by any later run: a carried assignment that survives two adopted runs keeps the SAME
    /// callback instance the carry installed — no adopted run replaces it, and the detach that would
    /// have nulled it is suppressed for exactly as long as the assignment is Carried.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOFNESS: an implementation that installed the callback per run (or detached it at the
    /// end of the carrying run) changes the recorded history and the instance identity, so both
    /// assertions fail by name. The adopted run's carried delivery is HELD in that stream's fake, so
    /// the run ends with the assignment still Carried rather than delivered — which is what makes the
    /// "detach is suppressed while Carried" half observable across a SECOND run boundary.
    /// </remarks>
    [Fact]
    public async Task RetargetCallTime_p2_AdoptedRunsNeverReplaceTheCarriedCallback_AndTheDetachIsSuppressedWhileCarried()
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

            var atCarry = plan.Runner.Provisioners;
            Assert.Equal(2, atCarry.Count);
            Assert.NotNull(atCarry[^1]);

            // The carrying run's teardown DETACHED NOTHING (the only two records are the per-run
            // install and the carried install), and the assignment is genuinely still Carried.
            Assert.DoesNotContain(null, atCarry);

            // THE ADOPTED RUN adds no install of its own, and its carried delivery is HELD in stream 2
            // so the run ends with the assignment still Carried.
            plan.PendingHoldCompletesFrom = 0;
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            await plan.WaitForPublishedConnectionAsync();
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, plan.Runner.Provisioners.Count);
            Assert.Same(atCarry[^1], plan.Runner.CurrentProvisioner);

            // It ends by stream loss with the assignment still Carried: the detach was STILL suppressed
            // at the end of the adopted run and the SAME callback stayed installed.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));
            Assert.Equal(2, plan.Runner.Provisioners.Count);
            Assert.Same(atCarry[^1], plan.Runner.CurrentProvisioner);
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (i) A RETAINED ADAPTER FROM A FINISHED TASK NEVER FOLLOWS A SUCCESSOR'S ADOPTION. Task A
    /// finishes normally on connection 1 (it started its own ordinary Ready, so it is NOT carried).
    /// Successor B then runs on the same stream, is carried by the stream loss and is adopted on
    /// connection 2. A's retained dependency must stay on A's CAPTURED connection 1 — now retired,
    /// so its calls fail with the EXISTING disconnected error — and must put NOTHING on B's adopted
    /// connection 2, while B's own dependency does reach connection 2 (the positive control).
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOFNESS: a resolver that decides Carried/Delivered from the SERVICE-WIDE ownership
    /// slot sees B (Carried) when A's adapter is called, so A's progress and session calls are sent to
    /// B's adopted connection 2: the disconnected assertions and the connection-2 emptiness
    /// assertions fail by name.
    /// </remarks>
    [Fact]
    public async Task RetargetCallTime_i_FinishedTasksRetainedAdapter_NeverFollowsASuccessorsAdoption()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        try
        {
            // TASK A runs and finishes on connection 1, claiming its OWN ordinary Ready.
            await plan.PushAssignmentAsync("task-A");
            var bridgeA = await plan.Runner.BridgeInstalled(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var sessionsA = Assert.IsAssignableFrom<ISessionClient>(bridgeA);
            var ownerA = GetActiveAssignment(plan.Service);
            plan.Runner.Release("task-A");
            await plan.CurrentRequests.WaitForAssignmentReadyCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(CarryStates.ReadyStarted, GetAssignmentState(ownerA));

            // SUCCESSOR B replaces A on the same stream (the replacement drain joins A first). The
            // POST-HANDLER gate is what makes the slot read below sound: the runner's prompt entry can
            // legitimately precede InstallActiveAssignment, so `ownerB` is only read once B's handler
            // has returned.
            var readsBeforeB = await plan.CapturePostHandlerBaselineAsync();
            plan.Push(ResultAssignment("task-B"));
            await plan.Runner.PromptStarted("task-B").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.WaitForAssignmentHandlerReturnedAsync(readsBeforeB);
            var bridgeB = await plan.Runner.BridgeInstalled(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.NotSame(bridgeA, bridgeB);
            var ownerB = GetActiveAssignment(plan.Service);
            Assert.NotSame(ownerA, ownerB);
            Assert.Equal("task-B", GetOwnerTaskId(ownerB));

            // STREAM LOSS: B is carried; A stays finished (ReadyStarted, never carried).
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Same(ownerB, GetActiveAssignment(plan.Service));
            Assert.Equal(CarryStates.Carried, GetAssignmentState(ownerB));
            Assert.Equal(CarryStates.ReadyStarted, GetAssignmentState(ownerA));
            Assert.True(plan.Connections[0].IsRetired);
            var toolRequestsOnOneBefore = plan.Requests[0].ToolRequests.Count;

            // RUN 2 ADOPTS B.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var adopted = await plan.WaitForPublishedConnectionAsync();
            Assert.Same(
                adopted,
                await WaitForAdoptedConnectionAsync(plan.Service, "The adopted run must publish B's adoption."));
            Assert.Equal("task-B", Assert.Single(plan.Invokers[1].Registers).CurrentTaskId);

            // A's RETAINED ADAPTER: stays on its captured, retired connection 1 — disconnected.
            var progressFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => bridgeA.ReportProgressAsync(
                    "task-A", "stale", "finished task must not follow B", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, progressFailure.Message);
            var loadFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sessionsA.GetSessionAsync("goal-task-A:coder", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, loadFailure.Message);

            // ...and NOTHING of A's reached B's adopted connection 2 (nor anything new on connection 1).
            Assert.Empty(plan.CurrentRequests.ToolRequests);
            Assert.Equal(0, plan.Invokers[1].LoadCount);
            Assert.Equal(0, adopted.PendingToolResponseCount);
            Assert.Equal(toolRequestsOnOneBefore, plan.Requests[0].ToolRequests.Count);

            // POSITIVE CONTROL — B's OWN adapter does follow B's adoption onto connection 2.
            await bridgeB.ReportProgressAsync(
                    "task-B", "adopted", "B follows its own adoption", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var progressB = Assert.Single(plan.CurrentRequests.ToolRequests);
            Assert.Equal("task-B", progressB.ToolRequest.TaskId);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(ownerB));

            plan.CompleteStream();
            await plan.JoinRunAsync();
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (ii) A DELIVERED CARRIED ASSIGNMENT'S ADAPTER KEEPS FOLLOWING ITS OWN ADOPTION AFTER A SUCCESSOR
    /// TAKES THE SLOT. Task A is carried, adopted on connection 2 and delivered there; successor B is
    /// then accepted on the same adopted stream and replaces A in the ownership slot. While
    /// connection 2 stays live, A's retained dependency must still reach connection 2 (A is
    /// Delivered-after-carried, and connection 2 is A's own adoption) instead of failing on A's
    /// retired captured connection 1.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOFNESS: a resolver that decides from the SERVICE-WIDE slot sees B (Open) when A's
    /// adapter is called, so it returns A's captured connection 1 — retired — and the calls fail
    /// disconnected: the connection-2 arrival assertions fail by name.
    /// </remarks>
    [Fact]
    public async Task RetargetCallTime_ii_DeliveredCarriedAdapter_KeepsFollowingItsOwnAdoptionAfterASuccessorTakesTheSlot()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        try
        {
            // TASK A is carried by the stream loss while its body is still running.
            await plan.PushAssignmentAsync("task-A");
            var bridgeA = await plan.Runner.BridgeInstalled(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var sessionsA = Assert.IsAssignableFrom<ISessionClient>(bridgeA);
            var ownerA = GetActiveAssignment(plan.Service);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(ownerA));

            // RUN 2 ADOPTS A; the body finishes and the carried delivery completes on connection 2.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var adopted = await plan.WaitForPublishedConnectionAsync();
            Assert.Same(
                adopted,
                await WaitForAdoptedConnectionAsync(plan.Service, "The adopted run must publish A's adoption."));
            plan.Runner.Release("task-A");
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(ownerA));
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);

            // SUCCESSOR B is accepted on the same adopted stream and REPLACES A in the slot. The slot
            // read below is gated on B's handler having RETURNED (the runner's prompt entry can precede
            // InstallActiveAssignment).
            var readsBeforeB = await plan.CapturePostHandlerBaselineAsync();
            plan.Push(ResultAssignment("task-B"));
            await plan.Runner.PromptStarted("task-B").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.Runner.BridgeInstalled(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.WaitForAssignmentHandlerReturnedAsync(readsBeforeB);
            var ownerB = GetActiveAssignment(plan.Service);
            Assert.NotSame(ownerA, ownerB);
            Assert.Equal("task-B", GetOwnerTaskId(ownerB));
            Assert.Equal(CarryStates.Open, GetAssignmentState(ownerB));

            // A's OWN adoption is still live — the premise of the Delivered-after-carried rule.
            Assert.Same(adopted, GetAdoptedConnection(plan.Service));
            Assert.False(adopted.IsRetired);
            Assert.True(plan.Connections[0].IsRetired);

            // A's RETAINED ADAPTER still follows A's adoption onto connection 2.
            await bridgeA.ReportProgressAsync(
                    "task-A", "delivered", "still on my own adoption", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var progressA = Assert.Single(plan.CurrentRequests.ToolRequests);
            Assert.Equal("report_progress", progressA.ToolRequest.ToolName);
            Assert.Equal("task-A", progressA.ToolRequest.TaskId);

            await sessionsA
                .SaveSessionAsync("goal-task-A:coder", """{"turn":9}""", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, plan.Invokers[1].SaveCount);
            Assert.Equal("goal-task-A:coder", plan.Invokers[1].LastSaveSessionId);

            // NOTHING went to A's retired captured connection 1.
            Assert.Empty(plan.Requests[0].ToolRequests);
            Assert.Equal(0, plan.Invokers[0].SaveCount);

            plan.Runner.Release("task-B");
            plan.CompleteStream();
            await plan.JoinRunAsync();
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (i-b) A DELIVERED CARRIED TASK'S ADAPTER NEVER FOLLOWS A SUCCESSOR'S LATER ADOPTION. Task A is
    /// carried, adopted on connection 2 and delivered. Successor B is accepted on connection 2, is
    /// itself carried when stream 2 is lost, and is adopted on connection 3. A is still
    /// Delivered-after-carried, but connection 3 is B's adoption, NOT A's — so A's retained dependency
    /// must NOT reach connection 3; it falls back to A's captured (retired) connection 1 and fails
    /// with the EXISTING disconnected error. B's own dependency reaching connection 3 is the positive
    /// control.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOFNESS: this is the vector that needs the adoption's OWNER identity, not just the
    /// adapter's own state (A's state alone says "follow"). Dropping the owner check — or any resolver
    /// that follows "the current adoption" regardless of whose it is — sends A's calls to connection 3,
    /// and the disconnected and connection-3 emptiness assertions fail by name.
    /// </remarks>
    [Fact]
    public async Task RetargetCallTime_ib_DeliveredTasksAdapter_NeverFollowsASuccessorsLaterAdoption()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        try
        {
            // A is carried by stream 1's loss, adopted on connection 2 and delivered there.
            await plan.PushAssignmentAsync("task-A");
            var bridgeA = await plan.Runner.BridgeInstalled(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var sessionsA = Assert.IsAssignableFrom<ISessionClient>(bridgeA);
            var ownerA = GetActiveAssignment(plan.Service);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var adopted2 = await plan.WaitForPublishedConnectionAsync();
            await WaitForAdoptedConnectionAsync(plan.Service, "The second run must publish A's adoption.");
            plan.Runner.Release("task-A");
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "A's carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(ownerA));

            // SUCCESSOR B is accepted on connection 2 and is still running when stream 2 is lost. Both
            // B's slot read here AND the stream loss below are gated on B's handler having RETURNED:
            // ending the stream while that handler was still running would race the very publish the
            // assertions below depend on.
            var readsBeforeB = await plan.CapturePostHandlerBaselineAsync();
            plan.Push(ResultAssignment("task-B"));
            await plan.Runner.PromptStarted("task-B").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var bridgeB = await plan.Runner.BridgeInstalled(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.WaitForAssignmentHandlerReturnedAsync(readsBeforeB);
            var ownerB = GetActiveAssignment(plan.Service);
            Assert.NotSame(ownerA, ownerB);
            Assert.Equal("task-B", GetOwnerTaskId(ownerB));
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Same(ownerB, GetActiveAssignment(plan.Service));
            Assert.Equal(CarryStates.Carried, GetAssignmentState(ownerB));
            Assert.True(adopted2.IsRetired);

            // RUN 3 ADOPTS B (it claims task-B, not task-A).
            plan.StartThirdRun(RegisterResponseFor(adopted: true));
            var adopted3 = await plan.WaitForPublishedConnectionAsync();
            Assert.Same(
                adopted3,
                await WaitForAdoptedConnectionAsync(plan.Service, "The third run must publish B's adoption."));
            Assert.Equal("task-B", Assert.Single(plan.Invokers[2].Registers).CurrentTaskId);

            // A's RETAINED ADAPTER is Delivered-after-carried, yet connection 3 is not ITS adoption.
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(ownerA));
            var progressFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => bridgeA.ReportProgressAsync(
                    "task-A", "stale", "must not follow B's adoption", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, progressFailure.Message);
            var saveFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sessionsA.SaveSessionAsync(
                    "goal-task-A:coder", """{"turn":1}""", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, saveFailure.Message);
            Assert.Empty(plan.CurrentRequests.ToolRequests);
            Assert.Equal(0, plan.Invokers[2].SaveCount);

            // POSITIVE CONTROL — B's OWN adapter follows B's adoption onto connection 3.
            await bridgeB.ReportProgressAsync(
                    "task-B", "adopted", "B follows its own adoption", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal("task-B", Assert.Single(plan.CurrentRequests.ToolRequests).ToolRequest.TaskId);

            plan.Runner.Release("task-B");
            plan.CompleteStream();
            await plan.JoinRunAsync();
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// THE READ-BASELINE CONTRACT on the EXACT path the reviewer identified as a counterexample
    /// (Acceptance_g's second run): the run's first write lands BEFORE the message loop enters its
    /// first read, so a baseline sampled at that instant could be zero — and then "baseline + 1" would
    /// be satisfied by the INITIAL read instead of the read re-armed after the pushed assignment's
    /// handler returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE WINDOW IS FORCED, NEVER LEFT TO THE RACE. The fake's write-count signal fires INSIDE the
    /// initial write, so on a typical schedule the loop has already entered its first read by the time
    /// the test resumes — and then even the OLD sample-immediately form would see a read in flight and
    /// pass. The test therefore arms the stream's initial-write HOLD before releasing the runner
    /// preparation: run 2's initial Ready is recorded and PARKED, production cannot reach its first
    /// read until the test releases it, and the capture is started inside that window. Assertion (0)
    /// then requires the capture to still be PENDING with zero reads started, which the old form (it
    /// returns 0 at once) fails by name on every schedule.
    /// </para>
    /// <para>
    /// After the release, the three assertions are the contract that closes that hole, and together
    /// they are DISCRIMINATING against the old form:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   the captured baseline is at least 1 — <see cref="ReconnectPlan.CapturePostHandlerBaselineAsync"/>
    ///   will not return until the current run has a read in flight, so a zero baseline (the unsound
    ///   value the old form could sample, and which the helper now rejects by name) cannot be used;
    ///   </description></item>
    ///   <item><description>
    ///   the capture itself starts NO new read: the value it returns is the SAME pending read, which is
    ///   what makes it a baseline rather than an already-satisfied threshold;
    ///   </description></item>
    ///   <item><description>
    ///   after the push, the gate's threshold (baseline + 1) is met by a read that is STRICTLY NEWER
    ///   than the baseline — so the pending initial read alone could never have satisfied it.
    ///   </description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task RetargetCallTime_r_ReadBaseline_EstablishesTheInitialPendingReadBeforeSampling()
    {
        var plan = ReconnectPlan.StartAsync(
            "task-A", register2Adopted: false, holdReportComplete: true);
        var connectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // RUN 1: an assignment is carried so run 2 is the drain-then-connect shape below.
            await plan.PushAssignmentAsync("task-A");
            plan.Runner.Release("task-A");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();

            // RUN 2: exactly Acceptance_g's run-2 shape — the runner preparation is SUSPENDED, then the
            // run's first write (the initial Ready) is awaited, which is the instant the reviewer named.
            plan.Runner.ConnectEnteredHook = async _ =>
            {
                connectEntered.TrySetResult();
                await allowConnect.Task;
            };
            plan.StartSecondRun(RegisterResponseFor(adopted: false));
            await connectEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE FORCED WINDOW. Before the runner preparation is released, arm the stream's
            // initial-write hold: run 2's initial Ready is then RECORDED and PARKED inside the fake.
            // Production awaits that write BEFORE its message loop creates the first pending read, so
            // while it is held the run is provably in the write-before-first-read window — on EVERY
            // schedule, including the one where the first read would otherwise have won the race.
            plan.CurrentRequests.ArmInitialWriteHold();
            allowConnect.TrySetResult();
            await plan.CurrentRequests.InitialWriteHeld
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.WaitForInitialWriteAsync();

            // (0) INSIDE THE WINDOW: the write is recorded and still pending, and NO read has started.
            // A baseline sampled here would be ZERO — the unsound value — so the helper must NOT
            // complete yet: it has to wait for the initial read to be established. The old
            // sample-immediately form completes at once with 0 and fails HERE by name.
            Assert.Equal(
                WorkerMessage.PayloadOneofCase.Ready,
                Assert.Single(plan.CurrentRequests.Writes).PayloadCase);
            Assert.Equal(0, plan.Responses.ReadsStarted);
            var capture = plan.CapturePostHandlerBaselineAsync();
            Assert.False(
                capture.IsCompleted,
                "The baseline capture completed while run 2's initial Ready was still held and no read "
                + "had started: it sampled the counter before establishing the initial pending read "
                + $"(reads started {plan.Responses.ReadsStarted}).");
            Assert.Equal(0, plan.Responses.ReadsStarted);

            // RELEASE THE WRITE: the loop then enters its first read, which is what the capture waits for.
            plan.CurrentRequests.ReleaseInitialWrite();

            // (1) THE ESTABLISHED BASELINE: at least one read is in flight, and the helper's own
            // establishment is what guarantees it rather than the fixture's luck.
            var baseline = await capture;
            Assert.True(
                baseline >= 1,
                "The helper must establish the current run's initial pending read before sampling: a "
                + $"zero baseline cannot prove a handler return (observed {baseline}).");

            // (2) IT IS A PENDING READ, NOT AN ALREADY-RE-ARMED ONE: capturing starts no new read.
            Assert.Equal(baseline, plan.Responses.ReadsStarted);

            // (3) THE POST-HANDLER RE-ARM IS STRICTLY NEWER THAN THE BASELINE.
            await plan.PushAssignmentAsync("task-B");
            Assert.True(
                plan.Responses.ReadsStarted > baseline,
                "The read the handler-return gate waited for must be STRICTLY newer than the baseline: "
                + $"baseline {baseline}, reads started {plan.Responses.ReadsStarted}.");
            Assert.Equal("task-B", GetOwnerTaskId(GetActiveAssignment(plan.Service)));

            plan.Runner.Release("task-B");
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.WaitForAssignmentReadyCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal("task-B", Assert.Single(plan.CurrentRequests.Completes).Complete.TaskId);
        }
        finally
        {
            allowConnect.TrySetResult();
            plan.CurrentRequests.ReleaseInitialWrite();
            plan.Runner.ConnectEnteredHook = null;
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// THE INITIAL-WRITE HOLD IS RELEASED BY <c>CompleteAsync</c>, so the fake is safe as a SOLE
    /// shutdown path. An UNARMED stream keeps its previous no-op behavior exactly; an ARMED stream
    /// parks its first write until the hold is satisfied, and closing the client's half of the stream
    /// satisfies it — so a future fixture that only calls <c>CompleteAsync</c> can never leave that
    /// write (and the run awaiting it) parked forever.
    /// </summary>
    /// <remarks>
    /// Deterministic throughout: the parked write is observed through the fake's own
    /// <c>InitialWriteHeld</c> TCS and released by <c>CompleteAsync</c> alone — no
    /// <c>ReleaseInitialWrite</c>, no <c>ReleaseAll</c>, no disposal, no sleeps and no polling. The
    /// unarmed half is the additive-hardening proof: it fails if <c>CompleteAsync</c> ever released
    /// something that was never armed, and the armed half fails if it stopped releasing at all.
    /// </remarks>
    [Fact]
    public async Task CarryRequestStream_CompleteAsync_ReleasesAnArmedInitialWriteHold()
    {
        var requests = new CarryRequestStream();

        // UNARMED: nothing is held, and CompleteAsync is the no-op it always was.
        Assert.False(requests.IsInitialWriteHeld);
        await requests.CompleteAsync();
        Assert.False(requests.IsInitialWriteHeld);

        // ARMED: the stream's FIRST write is recorded and PARKED inside the fake.
        requests.ArmInitialWriteHold();
        var initialWrite = requests.WriteAsync(new WorkerMessage
        {
            WorkerId = "worker-reconnect-a",
            Ready = new WorkerReady(),
        });

        await requests.InitialWriteHeld.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        Assert.True(requests.IsInitialWriteHeld, "The armed hold must park the first write.");
        Assert.False(initialWrite.IsCompleted, "The held write must still be pending.");
        Assert.Equal(
            WorkerMessage.PayloadOneofCase.Ready,
            Assert.Single(requests.Writes).PayloadCase);

        // COMPLETE ALONE RELEASES IT: the write completes with no other releaser involved.
        await requests.CompleteAsync();
        Assert.False(requests.IsInitialWriteHeld, "CompleteAsync must release the armed hold.");
        await initialWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        Assert.True(initialWrite.IsCompletedSuccessfully);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // THE CARRIED-DELIVERY CONTRACT'S PRIORITY RACES. Every vector below is driven by the
    // ORDERING of the loop's own events — the ordinary Ready settlement, the reporter's
    // Complete attempt, the stream loss that carries the assignment, the adopted run's
    // carried delivery and a matching CancelTask — through deterministic TCS gates only.
    // No sleep, no polling and no true thread race is used as an ordering device.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// (a3) THE OLD-STREAM BOUNDARY RACE. The reporter is HELD immediately before its Complete
    /// write; the stream loss is observed and the connection retired while it is held; only then is
    /// the reporter released. Its Complete attempt therefore fails with the EXISTING disconnected
    /// error — NOTHING is written to the retired original stream — the Open assignment is carried,
    /// and the delivery happens EXACTLY ONCE on the adopted second stream.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOFNESS: the hook-entry assertion fails by name if the reporter never reaches the
    /// pre-write instant, and the emptiness assertions fail if the post-gate retirement check is
    /// removed — the fake records a write even after its stream was disposed, so a write that
    /// slipped through would be observed rather than assumed away.
    /// </remarks>
    [Fact]
    public async Task PriorityRace_a3_ReporterHeldBeforeCompleteSend_EofRetiresThenTheHeldWriteFailsDisconnected()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completesAtHook = -1;
        Task? reporting = null;
        try
        {
            await plan.PushAssignmentAsync("task-A");
            reporting = GetActiveReporting(plan.Service);

            // INSTALL BEFORE THE REPORTER CAN REACH THE HOOK: the hook is awaited only after the body
            // terminates, and the body is still parked in its gated prompt — so the arm can never
            // race the very instant it must hold.
            plan.Service.ReportBeforeCompleteSendHook = () =>
            {
                completesAtHook = plan.Requests[0].Completes.Count;
                hookEntered.TrySetResult();
                return hookRelease.Task;
            };

            // THE REPORTER RUNS TO THE HOOK AND PARKS THERE, before its Complete write.
            plan.Runner.Release("task-A");
            await hookEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, completesAtHook);

            // THE STREAM LOSS: EOF is observed, the connection retired, and the still-Open assignment
            // carried — all while the reporter is HELD before its Complete write.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            await WaitForRetiredAsync(
                plan.Connections[0], "The early retire must run at the read-await site.");
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));
            Assert.False(reporting.IsCompleted, "The reporter must still be held at the hook.");

            // RELEASE: the Complete attempt runs strictly AFTER the retirement, so it fails
            // disconnected and writes NOTHING on the retired stream.
            hookRelease.TrySetResult();
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            AssertNoAssignmentWrites(
                plan.Requests[0], "The retired first stream must receive no Complete and no Ready");
            Assert.NotNull(GetRetainedResult(plan.Service));

            // THE DELIVERY: exactly ONE Complete and ONE assignment Ready, on the ADOPTED stream only.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal("task-A", Assert.Single(plan.CurrentRequests.Completes).Complete.TaskId);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            AssertNoAssignmentWrites(
                plan.Requests[0], "The retired first stream must still receive nothing");
            Assert.Single(plan.Requests[0].Writes); // run 1's own initial Ready
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(GetActiveAssignment(plan.Service)));

            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
        }
        finally
        {
            hookRelease.TrySetResult();
            plan.Service.ReportBeforeCompleteSendHook = null;
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (e1) THE ORDINARY READY SETTLES AND ITS WRITE COMPLETES <em>BEFORE</em> EOF: the Complete and
    /// the assignment Ready both land on the ORIGINAL stream, the state is READYSTARTED (never
    /// Carried), and EOF takes today's cancel-and-drain teardown — the slot ends EMPTY and no carried
    /// delivery ever exists. This is the positive half of the Ready-vs-carry ordering: the hook IS
    /// entered (the settlement reached its pre-send instant) exactly once.
    /// </summary>
    [Fact]
    public async Task PriorityRace_e1_OrdinaryReadySettlesAndWritesBeforeEof_NotCarriedAndDrainedOnEof()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: false);
        var hookEntries = new int[1];
        try
        {
            plan.Service.OrdinaryReadyBeforeSendHook = () =>
            {
                Interlocked.Increment(ref hookEntries[0]);
                return Task.CompletedTask;
            };

            var execution = await plan.PushAssignmentAsync("task-A");
            plan.Runner.Release("task-A");

            // THE ORDINARY READY WINS: the Complete lands, the response loop observes the published
            // eligibility, settles the slot (CAS + shared claim) and its write lands on stream 1.
            await plan.CurrentRequests.WaitForAssignmentReadyCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal(CarryStates.ReadyStarted, GetAssignmentState(owner));
            Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal(1, Volatile.Read(ref hookEntries[0]));

            // THE WRITE ITSELF COMPLETES — BEFORE EOF. The fake RECORDING the Ready (the milestone
            // above) is NOT the write completing: it fires inside WriteCoreAsync, before that returns
            // and before StartOrdinaryReadyWrite's task completes. So the ONE retained readiness write
            // — the very task the settlement started — is joined here, successfully, before the stream
            // is ended. It is readable now: the slot assigns it inside the SAME lock its Write getter
            // takes, and the fake can only record the Ready after the settlement invoked the start
            // under that lock, so this read blocks until the assignment is published and can never
            // observe an unset slot. Only a genuinely COMPLETED write satisfies the join; a write
            // still outstanding keeps the stream open until it finishes (or fails by name).
            var readinessWrite = GetRetainedReadinessWrite(
                plan.Service, "The settled ordinary readiness write must be started and retained.");
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                readinessWrite.IsCompletedSuccessfully,
                "The ordinary Ready write must COMPLETE SUCCESSFULLY before EOF is signalled.");
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // EOF strictly AFTER the completed write: ReadyStarted is mutually exclusive with Carried.
            plan.CompleteStream();
            await WaitForRetiredAsync(
                plan.Connections[0], "The early retire must run at the read-await site.");
            await plan.JoinRunAsync();

            Assert.Equal(CarryStates.ReadyStarted, GetAssignmentState(owner));
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
            Assert.Null(GetCarriedDeliveryOrNull(plan.Service));
            Assert.Null(GetHeartbeatTaskId(plan.Service));
            Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);

            // THE SETTLEMENT IS NEVER REPEATED: the settled slot is not observable again, so the
            // pre-send instant is entered exactly ONCE for this assignment.
            Assert.Equal(1, Volatile.Read(ref hookEntries[0]));
        }
        finally
        {
            plan.Service.OrdinaryReadyBeforeSendHook = null;
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (e2) THE ORDINARY READY WINS THE CAS AND THE SHARED CLAIM BUT IS HELD BEFORE ITS WRITE, AND
    /// THEN THE STREAM IS LOST. The connection is retired BEFORE the hook is released, so the held
    /// write fails with the EXISTING disconnected error: NO assignment Ready is written after the
    /// retirement, the assignment is NOT carried (the ReadyStarted branch cancels and drains) and the
    /// slot ends EMPTY — so the next run's RECORDED RegisterRequest claims NOTHING.
    /// </summary>
    /// <remarks>
    /// The retained readiness write is read only AFTER the retirement milestone: the retirement is
    /// published by the very loop that settled the slot, so it is causally after the settlement
    /// returned — the read can never race the settle's own retention of the write.
    /// </remarks>
    [Fact]
    public async Task PriorityRace_e2_OrdinaryReadyHeldThenEof_HeldWriteFailsDisconnectedAndNothingIsCarried()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: false);
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookEntries = new int[1];
        try
        {
            plan.Service.OrdinaryReadyBeforeSendHook = () =>
            {
                Interlocked.Increment(ref hookEntries[0]);
                hookEntered.TrySetResult();
                return hookRelease.Task;
            };

            await plan.PushAssignmentAsync("task-A");
            plan.Runner.Release("task-A");

            // THE CAS, THE CHAIN AND THE CLAIM ARE ALL WON — and the write is HELD before any write.
            await hookEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal(CarryStates.ReadyStarted, GetAssignmentState(owner));
            Assert.Equal(1, GetReadyClaimState(GetOwnerReadyClaim(owner)));
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);

            // EOF: the carry CAS LOSES (ReadyStarted is not Open).
            plan.CompleteStream();
            await WaitForRetiredAsync(
                plan.Connections[0], "The early retire must run at the read-await site.");
            var readinessWrite = GetRetainedReadinessWrite(
                plan.Service, "The ordinary readiness write must be started and retained.");

            // RELEASE ONLY NOW: the held write runs against a RETIRED connection and fails with the
            // EXISTING disconnected error instead of writing on the dead stream.
            hookRelease.TrySetResult();
            await plan.JoinRunAsync();

            Assert.True(readinessWrite.IsFaulted, "The held readiness write must fail disconnected.");
            var fault = await Assert.ThrowsAsync<InvalidOperationException>(() => readinessWrite);
            Assert.Equal(WorkerConnection.DisconnectedMessage, fault.Message);

            // NOTHING was written on the original stream after the retirement, and no carry happened.
            // The Complete is the TOLERATED pre-loss write (the settlement's hook only ever runs after
            // that attempt terminated), so the stream holds exactly it and run 1's own initial Ready —
            // the HELD readiness write never reached the fake at all.
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);
            Assert.Single(plan.Requests[0].Completes); // the tolerated pre-loss Complete
            Assert.Equal(2, plan.Requests[0].Writes.Count); // the initial Ready + that Complete
            Assert.Equal(1, Volatile.Read(ref hookEntries[0]));
            Assert.Equal(CarryStates.ReadyStarted, GetAssignmentState(owner));
            Assert.Null(GetCarriedDeliveryOrNull(plan.Service));
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
            Assert.Null(GetHeartbeatTaskId(plan.Service));

            // THE NEXT RUN CLAIMS NOTHING: the recorded RegisterRequest has an EMPTY current_task_id.
            plan.StartNextRun(RegisterResponseFor(adopted: false));
            await plan.CurrentRequests.WaitForWriteCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(string.Empty, plan.Invokers[^1].Registers[0].CurrentTaskId);
        }
        finally
        {
            hookRelease.TrySetResult();
            plan.Service.OrdinaryReadyBeforeSendHook = null;
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (e3) EOF IS OBSERVED <em>BEFORE</em> THE ORDINARY READY SETTLES (the executor is still
    /// running): the assignment IS carried. When its reporting finishes afterwards — on the retired
    /// original connection, so nothing is written there — no ordinary Ready is attempted on ANY
    /// stream: the settlement CAS loses (Carried), the pre-send hook is never entered, and even a
    /// settlement driven through the production entry point claims nothing. After adoption the task is
    /// delivered with exactly ONE Complete and ONE Ready, both on stream 2.
    /// </summary>
    [Fact]
    public async Task PriorityRace_e3_EofBeforeTheOrdinaryReadySettles_IsCarriedAndOnlyStreamTwoSeesTheDelivery()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        var hookEntries = new int[1];
        Task? execution = null;
        Task? reporting = null;
        try
        {
            plan.Service.OrdinaryReadyBeforeSendHook = () =>
            {
                Interlocked.Increment(ref hookEntries[0]);
                return Task.CompletedTask;
            };

            execution = await plan.PushAssignmentAsync("task-A");
            reporting = GetActiveReporting(plan.Service);

            // EOF while the body is still parked: nothing was ever eligible, so the carry CAS WINS.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));

            // THE REPORTING FINISHES LATER. Its Complete attempt fails on the retired original
            // connection, and its eligibility publication finds no settlement: the loop is gone and a
            // settlement driven NOW — through the production entry point — loses the CAS.
            plan.Runner.Release("task-A");
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.NotNull(GetRetainedResult(plan.Service));
            Assert.Null(InvokeSettleOrdinaryReady(plan.Service, GetOwnerOrdinaryReady(owner)));
            Assert.Equal(0, Volatile.Read(ref hookEntries[0]));
            AssertNoAssignmentWrites(
                plan.Requests[0], "A carried assignment must never write on its retired original stream");
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));

            // ADOPT AND DELIVER: exactly ONE Complete and ONE assignment Ready, both on stream 2.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal("task-A", Assert.Single(plan.CurrentRequests.Completes).Complete.TaskId);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Equal(0, Volatile.Read(ref hookEntries[0]));
            AssertNoAssignmentWrites(
                plan.Requests[0], "The retired first stream must remain empty");
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));

            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
        }
        finally
        {
            plan.Service.OrdinaryReadyBeforeSendHook = null;
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (l2-i) CANCEL VS THE CARRIED COMPLETE ON THE ADOPTED STREAM. The carried delivery is held
    /// immediately before its Complete write, with the ASSIGNMENT token in hand; the matching
    /// CancelTask arrives and cancels exactly that token, so the hook returns NORMALLY and the send
    /// that follows observes cancellation — NO Complete is written on ANY stream. The assignment's
    /// single Ready is then the cancel handler's own claim-won fallback: exactly ONE assignment Ready
    /// on the adopted stream, none on the original one.
    /// </summary>
    [Fact]
    public async Task PriorityRace_l2i_CancelWhileDeliveryIsHeldBeforeItsComplete_NoCompleteAnywhereAndOneCancelReady()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        try
        {
            await plan.PushAssignmentAsync("task-A");

            // INSTALL BEFORE THE BODY IS RELEASED: the delivery reaches this instant only after its
            // reporting terminates, which the release below is what causes.
            plan.Service.CarriedBeforeCompleteSendHook = async token =>
            {
                observedToken = token;
                hookEntered.TrySetResult();

                // AWAIT THE ASSIGNMENT TOKEN ITSELF AND RETURN NORMALLY: the test never releases this
                // hook, so the send that follows must observe the cancellation for itself.
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => cancelled.TrySetResult());
                await cancelled.Task;
            };

            // EOF with the executor still running: carried, with no result yet.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // RUN 2 ADOPTS, then the body finishes: its Complete attempt on the RETIRED original
            // connection fails disconnected (stream 1 stays empty), the result is retained and the
            // delivery reaches the held instant with the assignment's own token.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            plan.Runner.Release("task-A");
            await hookEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var ownerCts = GetOwnerCts(plan.Service);
            Assert.True(observedToken.CanBeCanceled, "The hook must receive the assignment's token.");
            Assert.Equal(ownerCts.Token, observedToken);
            Assert.False(
                ownerCts.IsCancellationRequested,
                "The delivery is held with a LIVE assignment token — nothing has cancelled it yet.");
            AssertNoAssignmentWrites(
                plan.Requests[0], "The retired first stream must receive nothing");

            // THE MATCHING CANCEL: its DELIVERY to this run's loop is proved by the reader's own
            // consumed counter. The held hook can only return when the ASSIGNMENT token is cancelled,
            // and this cancel handler is the only thing that cancels it — so the ordering is
            // structural: the cancel provably arrived while the delivery was still parked there, and
            // the send that follows observes the cancelled token.
            var readsBeforeCancel = await plan.CapturePostHandlerBaselineAsync();
            plan.Push(new OrchestratorMessage
            {
                Cancel = new CancelTask { TaskId = "task-A", Reason = "cancelled before its Complete" },
            });
            await plan.Responses.Consumed(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.WaitForAssignmentHandlerReturnedAsync(readsBeforeCancel);
            await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                ownerCts.IsCancellationRequested,
                "The hook returned only because the cancel handler cancelled the assignment token.");

            // NO COMPLETE ON ANY STREAM, and exactly ONE assignment Ready — the cancel fallback's.
            Assert.Empty(plan.Requests[0].Completes);
            Assert.Empty(plan.CurrentRequests.Completes);
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Equal(0, GetSlotOccupancy(plan.Service));

            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);
        }
        finally
        {
            plan.Service.CarriedBeforeCompleteSendHook = null;
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (l2-ii) CANCEL BETWEEN THE CARRIED COMPLETE AND THE READY CLAIM. The carried Complete has
    /// already succeeded on the adopted stream and the delivery is held immediately before its Ready
    /// claim; the matching CancelTask's handler cancels the assignment while it is held (the gate) and
    /// the hook is only then released. The cancel's own
    /// drain joins the delivery BEFORE consulting the shared claim, so the delivery WINS it: exactly
    /// ONE assignment Ready on the adopted stream — never a second one from the handler — and none on
    /// the original stream.
    /// </summary>
    [Fact]
    public async Task PriorityRace_l2ii_CancelBetweenTheCarriedCompleteAndTheReadyClaim_ExactlyOneAdoptedReady()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.Service.CarriedBeforeReadyClaimHook = () =>
            {
                hookEntered.TrySetResult();
                return hookRelease.Task;
            };

            plan.CompleteStream();
            await plan.JoinRunAsync();
            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));

            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            plan.Runner.Release("task-A");

            // THE CARRIED COMPLETE SUCCEEDED and the delivery is HELD before the Ready claim.
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await hookEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));
            Assert.False(
                GetCarriedReadyStarted(owner), "The held delivery has not authorized a successor yet.");
            Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal(0, plan.CurrentRequests.AssignmentReadyCount);

            // THE CANCEL HANDLER RUNS WHILE THE DELIVERY IS HELD — and only THEN is it released. The
            // gate is NOT the reader's consumed counter (that fires inside MoveNext, before the handler
            // runs at all): it is the ASSIGNMENT TOKEN being cancelled, which only the matching-cancel
            // drain does (cancelFirst: true, as its first action, before it joins the carried
            // delivery). The token is asserted LIVE while the delivery is held, so the observation can
            // only complete from that handler's drain — proving the cancellation happened at the held
            // instant, not after the release let the claim and write run.
            var ownerCts = GetOwnerCts(plan.Service);
            Assert.False(
                ownerCts.IsCancellationRequested,
                "The delivery is held with a LIVE assignment token — nothing has cancelled it yet.");
            var (assignmentCancelled, cancelRegistration) = ObserveAssignmentCancellation(ownerCts);
            using (cancelRegistration)
            {
                var readsBeforeCancel = await plan.CapturePostHandlerBaselineAsync();
                plan.Push(new OrchestratorMessage
                {
                    Cancel = new CancelTask { TaskId = "task-A", Reason = "cancelled before the claim" },
                });
                await WaitForAssignmentCancellationAsync(
                    assignmentCancelled,
                    "The matching CancelTask handler must cancel the assignment while the delivery is held");
                Assert.False(delivery.IsCompleted, "The delivery must still be parked at the held instant.");
                Assert.False(
                    GetCarriedReadyStarted(owner),
                    "The cancellation was requested while the delivery was still before its Ready claim.");
                Assert.Equal(0, plan.CurrentRequests.AssignmentReadyCount);
                hookRelease.TrySetResult();

                // THE POST-HANDLER READ BARRIER: the re-armed read only happens after the cancel
                // handler (its drain and its claim-lost fallback) returned.
                await plan.WaitForAssignmentHandlerReturnedAsync(readsBeforeCancel);
                await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }

            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);
            AssertNoAssignmentWrites(
                plan.Requests[0], "The retired first stream must receive no assignment Ready");
            Assert.Equal(0, GetSlotOccupancy(plan.Service));

            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);
        }
        finally
        {
            hookRelease.TrySetResult();
            plan.Service.CarriedBeforeReadyClaimHook = null;
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (l2-iii) CANCEL AFTER THE READY CLAIM, WHILE THE WRITE IS HELD IN THE FAKE. The claim is
    /// consumed and the carried Ready write is PARKED inside the adopted stream's fake; the matching
    /// CancelTask's handler then cancels the assignment while the write is still held (the gate; its
    /// drain joins the parked delivery) and only then is the write released. Exactly
    /// ONE assignment Ready reaches the adopted stream and none reaches the original one.
    /// </summary>
    [Fact]
    public async Task PriorityRace_l2iii_CancelWhileTheCarriedReadyWriteIsHeld_ExactlyOneAdoptedReady()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.CompleteStream();
            await plan.JoinRunAsync();
            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));

            // RUN 2 (adopted) HOLDS ITS CARRIED READY WRITE: the hold is armed BEFORE the run starts,
            // so it can never race the write it must park.
            plan.PendingHoldReadiesFrom = 0;
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            plan.Runner.Release("task-A");

            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.AssignmentReadyEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));
            Assert.True(
                GetCarriedReadyStarted(owner),
                "The claim was won, so the carried Ready write is authorized and parked.");
            Assert.False(delivery.IsCompleted, "The delivery is parked in the held Ready write.");

            // THE CANCEL HANDLER RUNS WHILE THE READY WRITE IS HELD — THEN the held write is released.
            // The gate is the ASSIGNMENT TOKEN being cancelled, not the reader's consumed counter
            // (which fires inside MoveNext, before the handler runs). Only the matching-cancel drain
            // cancels that token (cancelFirst: true, first, before it joins the parked delivery), and
            // the parked carried Ready uses the adopted STREAM token, so this cancellation cannot
            // release the write itself: the write stays parked until the explicit release below.
            var ownerCts = GetOwnerCts(plan.Service);
            Assert.False(
                ownerCts.IsCancellationRequested,
                "The Ready write is held with a LIVE assignment token — nothing has cancelled it yet.");
            var (assignmentCancelled, cancelRegistration) = ObserveAssignmentCancellation(ownerCts);
            using (cancelRegistration)
            {
                var readsBeforeCancel = await plan.CapturePostHandlerBaselineAsync();
                plan.Push(new OrchestratorMessage
                {
                    Cancel = new CancelTask { TaskId = "task-A", Reason = "cancelled with a held Ready" },
                });
                await WaitForAssignmentCancellationAsync(
                    assignmentCancelled,
                    "The matching CancelTask handler must cancel the assignment while the Ready write is held");
                Assert.False(delivery.IsCompleted, "The delivery must still be parked in the held write.");
                plan.CurrentRequests.ReleaseAssignmentReady(0);

                // THE POST-HANDLER READ BARRIER: the re-armed read only happens after the cancel
                // handler (its drain and its claim-lost fallback) returned.
                await plan.WaitForAssignmentHandlerReturnedAsync(readsBeforeCancel);
                await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }

            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);
            AssertNoAssignmentWrites(
                plan.Requests[0], "The retired first stream must receive no assignment Ready");
            Assert.Equal(0, GetSlotOccupancy(plan.Service));

            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);
        }
        finally
        {
            plan.CurrentRequests.ReleaseAssignmentReady(0);
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (l3) CANCEL VS A NO-RESULT CARRIED DELIVERY. The carried task finishes with NO terminal result
    /// (its producer fails before one exists), so — after adoption — its delivery reaches the held
    /// Ready-claim instant WITHOUT any Complete. The matching CancelTask's handler cancels the assignment
    /// while it is held (the gate) and the hook is only then released: exactly ONE assignment Ready is written on the adopted stream (from
    /// one of the two claim paths, never both) and NO Complete is written on ANY stream.
    /// </summary>
    /// <remarks>
    /// The producer is parked in its provisioning fetch and then RELEASED to fail with a
    /// non-<c>RpcException</c>, which the assignment body's own failure handler contains: the
    /// execution terminates normally without publishing a result, so the retained holder stays EMPTY
    /// rather than carrying a fabricated completion.
    /// </remarks>
    [Fact]
    public async Task PriorityRace_l3_CancelWhileTheNoResultDeliveryIsHeldAtTheReadyClaim_NoCompleteAndOneAdoptedReady()
    {
        var parked = new ParkingProvisioner();
        var plan = ReconnectPlan.StartFresh(RegisterResponseFor(adopted: false), parked.Provisioner);
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? execution = null;
        Task? reporting = null;
        try
        {
            // THE BODY PARKS IN ITS PROVISIONING FETCH — before any result can exist. The manual push
            // plus the post-handler milestone is the only sound gate here: this assignment never
            // reaches its prompt, so the executor-settled gate cannot be used for it.
            var readsBeforePush = await plan.CapturePostHandlerBaselineAsync();
            plan.Push(ResultAssignment("task-A"));
            await parked.Entered.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.WaitForAssignmentHandlerReturnedAsync(readsBeforePush);
            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal("task-A", GetOwnerTaskId(owner));
            Assert.Equal(CarryStates.Open, GetAssignmentState(owner));
            execution = GetActiveExecution(plan.Service);
            reporting = GetActiveReporting(plan.Service);

            // EOF while the body is parked: carried with NO result and NO eligibility published.
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));

            // INSTALL THE HOOK BEFORE THE PRODUCER FAILS, so the delivery can never pass it.
            plan.Service.CarriedBeforeReadyClaimHook = () =>
            {
                hookEntered.TrySetResult();
                return hookRelease.Task;
            };

            // THE PRODUCER FAILS: no terminal result is published, and the reporting terminates too.
            parked.Release();
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetRetainedResult(plan.Service));

            // ADOPT: the delivery has NO result to send, so it reaches the held instant with NO
            // Complete on any stream.
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            await hookEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Empty(plan.Requests[0].Completes);
            Assert.Empty(plan.CurrentRequests.Completes);

            // THE CANCEL HANDLER RUNS WHILE THE DELIVERY IS HELD — and only THEN is the manually held
            // hook released. This hook does NOT observe the assignment token (unlike l2-i's): it
            // returns only when the test completes hookRelease. So the ordering is enforced by the
            // gate below, not by the hook: the test waits for the ASSIGNMENT TOKEN to be cancelled —
            // which only the matching-cancel drain does (cancelFirst: true, first, before it joins
            // this parked delivery), never the reader's consumed counter (which fires inside MoveNext,
            // before the handler runs). The token is asserted LIVE while the hook is held, so the
            // observation completing proves the cancel handler's drain ran at the held instant.
            var ownerCts = GetOwnerCts(plan.Service);
            Assert.False(
                ownerCts.IsCancellationRequested,
                "The delivery is held with a LIVE assignment token — nothing has cancelled it yet.");
            var (assignmentCancelled, cancelRegistration) = ObserveAssignmentCancellation(ownerCts);
            using (cancelRegistration)
            {
                var readsBeforeCancel = await plan.CapturePostHandlerBaselineAsync();
                plan.Push(new OrchestratorMessage
                {
                    Cancel = new CancelTask { TaskId = "task-A", Reason = "cancelled with no result to deliver" },
                });
                await WaitForAssignmentCancellationAsync(
                    assignmentCancelled,
                    "The matching CancelTask handler must cancel the assignment while the delivery is held");
                Assert.False(delivery.IsCompleted, "The delivery must still be parked at the held instant.");
                Assert.False(
                    GetCarriedReadyStarted(owner),
                    "The cancellation was requested while the delivery was still before its Ready claim.");
                Assert.Equal(0, plan.CurrentRequests.AssignmentReadyCount);
                hookRelease.TrySetResult();

                // THE POST-HANDLER READ BARRIER: the re-armed read only happens after the cancel
                // handler (its drain and its claim-lost fallback) returned.
                await plan.WaitForAssignmentHandlerReturnedAsync(readsBeforeCancel);
                await delivery.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }

            // EXACTLY ONE Ready on the adopted stream — never both claimants — and no Complete anywhere.
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Equal(0, plan.Requests[0].AssignmentReadyCount);
            Assert.Empty(plan.CurrentRequests.Completes);
            Assert.Empty(plan.Requests[0].Completes);
            Assert.Equal(0, GetSlotOccupancy(plan.Service));

            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
        }
        finally
        {
            hookRelease.TrySetResult();
            parked.Release();
            plan.Service.CarriedBeforeReadyClaimHook = null;
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (f5) THE EXIT RE-CHECK WITH A HELD CARRIED READY. The carried Complete succeeds on stream 2 and
    /// its Ready write is then HELD inside the fake with NO release gate — only the transport disposal
    /// faults it. EOF on stream 2's read finds the assignment DELIVERED, so the loop's teardown neither
    /// cancels nor joins it and the loop returns; as <c>RunCoreAsync</c> unwinds, its lexical stream
    /// disposal faults the held write, and the exit re-check then cancels, joins and clears. The run
    /// must complete within the bounded await (no deadlock) with an EMPTY slot, so the following run
    /// claims NOTHING.
    /// </summary>
    [Fact]
    public async Task PriorityRace_f5_HeldCarriedReadyWithEof_ExitReCheckClearsWithoutDeadlock()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));

            // RUN 2 (adopted) HOLDS ITS CARRIED READY WRITE: armed before the run starts.
            plan.PendingHoldReadiesFrom = 0;
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            plan.Runner.Release("task-A");

            // (1) + (2) The Complete SUCCEEDED and the Ready write is now PARKED in the fake.
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await plan.CurrentRequests.AssignmentReadyEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(GetActiveAssignment(plan.Service)));
            Assert.False(delivery.IsCompleted, "The delivery is parked in the held Ready write.");

            // (3) EOF on stream 2's read. (4) The loop teardown sees Delivered and does NOT join, so
            // the loop returns. (5) The lexical stream disposal faults the held write. (6) The exit
            // re-check cancels, joins the delivery and clears the slot — within the bounded await.
            plan.CompleteStream();
            await plan.JoinRunAsync();

            Assert.Null(GetActiveAssignmentOrNull(plan.Service));
            Assert.True(delivery.IsCompleted, "The exit re-check joined the carried delivery.");
            Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Null(GetHeartbeatTaskId(plan.Service));
            Assert.Null(GetHeartbeatRole(plan.Service));

            // THE THIRD RUN CLAIMS NOTHING: the recorded RegisterRequest has an EMPTY current_task_id.
            plan.StartThirdRun(RegisterResponseFor(adopted: false));
            await plan.CurrentRequests.WaitForWriteCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(string.Empty, plan.Invokers[^1].Registers[0].CurrentTaskId);

            plan.CompleteStream();
            await plan.JoinRunAsync();
        }
        finally
        {
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// (f4) THE CARRIED-PREDECESSOR REFUSAL ON AN ADOPTED UNGATED CONNECTION (the fixture's default
    /// registration shape). While the carried delivery is held BETWEEN its successful Complete and its
    /// Ready claim — so <c>CarriedReadyStarted</c> is NOT set — a successor assignment is REFUSED with
    /// the EXISTING fixed protocol error, BEFORE any replacement drain: the refusal throws from the
    /// handler, the run faults with exactly that message, and the successor's own replacement path is
    /// provably never reached — the runner is reset ONCE (task A's own assignment) rather than twice,
    /// and task B never enters a prompt.
    /// </summary>
    /// <remarks>
    /// WHY THE RESET COUNT IS THE DISCRIMINATOR. The successor path resets the runner only AFTER the
    /// refusal check and only after its replacement drain returned, so a refusal that was moved after
    /// that drain (or removed) makes the count 2 and lets task B's body enter. The ORDERING GATE is the
    /// predecessor's assignment-token cancellation: only the refusal's own teardown drain
    /// (<c>cancelFirst: true</c>) requests it, while an accepted successor's replacement drain
    /// (<c>cancelFirst: false</c>) never does — so the hook is held until that post-refusal milestone,
    /// with <c>CarriedReadyStarted</c> provably still unset, and a non-refusal fails by name there.
    /// The held delivery is released only after that milestone so the teardown drain can reach
    /// fixpoint; it then writes the ONE carried Ready, which also proves the cancel-side fallback
    /// never adds a second one.
    /// </remarks>
    [Fact]
    public async Task PriorityRace_f4_SuccessorBeforeCarriedReadyStarted_IsRefusedWithTheExistingError()
    {
        var plan = ReconnectPlan.StartAsync("task-A", register2Adopted: true);
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await plan.PushAssignmentAsync("task-A");
            plan.Service.CarriedBeforeReadyClaimHook = () =>
            {
                hookEntered.TrySetResult();
                return hookRelease.Task;
            };

            plan.CompleteStream();
            await plan.JoinRunAsync();
            var owner = GetActiveAssignment(plan.Service);
            Assert.Equal(CarryStates.Carried, GetAssignmentState(owner));

            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            var delivery = await WaitForCarriedDeliveryAsync(plan.Service, "The carried delivery must run.");
            plan.Runner.Release("task-A");

            // The carried Complete lands on the adopted stream; the Ready claim is NOT yet taken,
            // so no successor is authorized.
            await plan.CurrentRequests.AssignmentCompleteEntered(0)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await hookEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(CarryStates.Delivered, GetAssignmentState(owner));
            Assert.False(
                GetCarriedReadyStarted(owner), "The held delivery has not authorized a successor yet.");
            Assert.Single(plan.CurrentRequests.Completes);
            Assert.Equal(0, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Equal(1, plan.Runner.ResetCount);

            // THE SUCCESSOR ARRIVES WHILE THE PREDECESSOR IS RETAINED AND UNAUTHORIZED, AND THE HOOK
            // STAYS HELD UNTIL A POST-REFUSAL MILESTONE. The reader's consumed counter is NOT that
            // milestone (it fires inside MoveNext, before the refusal predicate even runs), so it is
            // not used. The milestone is the PREDECESSOR'S ASSIGNMENT TOKEN being cancelled:
            //   * the REFUSAL throws from the handler, the loop's catch records it, and its finally
            //     takes today's teardown (no stream loss) — DrainRetainedForTeardownAsync, whose drain
            //     cancels the predecessor's token FIRST (cancelFirst: true) and only then parks joining
            //     the still-held carried delivery;
            //   * an ACCEPTED successor instead takes the REPLACEMENT drain (cancelFirst: false), which
            //     never cancels that token and simply parks joining the held delivery.
            // The token is asserted LIVE while the hook is held, and the hook keeps the delivery before
            // its Ready claim (so CarriedReadyStarted cannot become true meanwhile), so this
            // cancellation can ONLY be produced by the refusal path — a non-refusal fails BY NAME here.
            var ownerCts = GetOwnerCts(plan.Service);
            Assert.False(
                ownerCts.IsCancellationRequested,
                "The delivery is held with a LIVE assignment token — nothing has cancelled it yet.");
            var (predecessorCancelled, cancelRegistration) = ObserveAssignmentCancellation(ownerCts);
            using (cancelRegistration)
            {
                plan.Push(ResultAssignment("task-B"));
                await WaitForAssignmentCancellationAsync(
                    predecessorCancelled,
                    "The successor must be REFUSED while the carried delivery is held before its Ready " +
                    "claim: only the refusal's teardown drain cancels the predecessor (an accepted " +
                    "successor's replacement drain does not)");

                // STILL HELD AT THE MILESTONE: the refusal and its teardown's cancellation both
                // happened with the predecessor unauthorized, the delivery parked and no Ready written.
                Assert.False(delivery.IsCompleted, "The delivery must still be parked at the held instant.");
                Assert.False(
                    GetCarriedReadyStarted(owner),
                    "The refusal was decided while the predecessor had not started its carried Ready.");
                Assert.Equal(0, plan.CurrentRequests.AssignmentReadyCount);
                Assert.Equal(1, plan.Runner.ResetCount);

                // RELEASE the held delivery only NOW, so the teardown drain can reach fixpoint.
                hookRelease.TrySetResult();
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => plan.Run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
                Assert.Equal(WorkerService.AssignmentBeforeAuthorizedReadyMessage, failure.Message);
            }

            Assert.True(delivery.IsCompleted, "The drained delivery finished.");

            // THE REPLACEMENT PATH WAS NEVER REACHED: exactly ONE runner reset (task A's assignment,
            // NOT a successor's replacement), task B's body never entered its prompt, and the slot is
            // empty because the refusal's own teardown drain cleared it.
            Assert.Equal(1, plan.Runner.ResetCount);
            Assert.False(
                plan.Runner.PromptStarted("task-B").IsCompleted,
                "No successor was ever started — its executor never entered a prompt.");
            Assert.Equal(0, GetSlotOccupancy(plan.Service));
            Assert.Equal(1, plan.CurrentRequests.AssignmentReadyCount);
            Assert.Single(plan.CurrentRequests.Completes);
            AssertNoAssignmentWrites(
                plan.Requests[0], "The retired first stream must receive nothing");
        }
        finally
        {
            hookRelease.TrySetResult();
            plan.Service.CarriedBeforeReadyClaimHook = null;
            await plan.TeardownAsync();
        }
    }

    /// <summary>
    /// THE PRODUCTION-CAUSED CANCELLATION MILESTONE: arms an observation of the retained
    /// assignment's OWN <c>Cts</c> token that completes the instant PRODUCTION requests that
    /// assignment's cancellation. It is armed on a LIVE token (the caller asserts that first), so it
    /// can only ever complete from a later <c>CancelAsync</c> on that exact source.
    /// </summary>
    /// <remarks>
    /// WHY THIS — AND NOT <c>Consumed(n)</c> — IS THE ORDERING PROOF. The reader's consumed counter
    /// fires inside <c>ChannelResponseReader.MoveNext</c>, before the message is even returned to the
    /// loop, so it proves nothing about the handler. The assignment's token, by contrast, is cancelled
    /// ONLY by <c>DrainAssignmentAsync(cancelFirst: true)</c> — the matching-cancel drain and the
    /// loop's teardown drain — as that drain's first action and BEFORE any of its joins. The linked
    /// process token is never cancelled by these tests, and the successor replacement drain uses
    /// <c>cancelFirst: false</c>. So while a test's gate is still HELD, this observation completing
    /// proves the named drain is already running and is (or is about to be) parked joining the held
    /// work — the cancellation happened at the required point, not after the gate was released.
    /// </remarks>
    /// <param name="ownerCts">The retained assignment's own cancellation source.</param>
    /// <returns>The observation and its registration (dispose it when the test is done).</returns>
    private static (Task Cancelled, CancellationTokenRegistration Registration) ObserveAssignmentCancellation(
        CancellationTokenSource ownerCts)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ownerCts.Token.Register(() => cancelled.TrySetResult());
        return (cancelled.Task, registration);
    }

    /// <summary>
    /// Awaits an <see cref="ObserveAssignmentCancellation"/> milestone with the bounded
    /// <see cref="Failsafe"/> as a HANG GUARD ONLY — correct production raises it without any time
    /// passing — and converts a missing cancellation into a failure that NAMES the expectation, so a
    /// path that never requested the cancellation fails by name rather than as a bare timeout.
    /// </summary>
    /// <param name="cancelled">The observation.</param>
    /// <param name="because">The failure message naming the production event that must cancel it.</param>
    private static async Task WaitForAssignmentCancellationAsync(Task cancelled, string because)
    {
        try
        {
            await cancelled.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"{because} (the retained assignment's token was never cancelled within the failsafe bound).");
        }
    }

    /// <summary>
    /// THE "NOTHING WRITTEN" PREDICATE for one stream's fake: no Complete and no assignment Ready.
    /// The run's own initial <c>WorkerReady</c> is deliberately NOT covered — the fake's
    /// assignment-Ready accounting already excludes it — so the predicate holds for every accepted
    /// run's stream.
    /// </summary>
    /// <param name="requests">The stream's write fake.</param>
    /// <param name="because">The failure message naming the vector's expectation.</param>
    private static void AssertNoAssignmentWrites(CarryRequestStream requests, string because)
    {
        Assert.True(
            requests.Completes.Count == 0,
            $"{because} — a Complete was written on a stream that must have received none.");
        Assert.True(
            requests.AssignmentReadyCount == 0,
            $"{because} — an assignment Ready was written on a stream that must have received none.");
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

        /// <summary>
        /// The ONE-SHOT assignment-Ready hold, consumed by the NEXT <see cref="StartNextRun"/> call.
        /// It is the Ready counterpart of <see cref="PendingHoldCompletesFrom"/>: because the hold is
        /// applied to the stream BEFORE the run starts, the run's carried Ready write is guaranteed
        /// to park in the fake — the arm can never race the write it must hold.
        /// </summary>
        internal int? PendingHoldReadiesFrom { get; set; }

        /// <summary>Starts the FIRST run: a fresh service, the initial Ready expected.</summary>
        internal static ReconnectPlan StartFresh(
            RegisterResponse firstResponse, WorkerConfigProvisioner? provisioner = null)
        {
            // THE CONFIG-REPO SEAM: the reconnect plan runs REAL assignment bodies on
            // connections whose production provisioner is live, so the config-repo
            // preparation reaches the git layer - the SHARED hermetic seam keeps that
            // deterministic and off the real filesystem.
            var (configRepoDir, gitRestore) = CreateHermeticConfigRepo(ConfigRepoUrl);

            var service = new WorkerService(
                "http://localhost:9999", WorkerId, ["coder"], configRepoDir);
            var runner = new ObservingRunner();
            InstallRunner(service, runner);
            service.HeartbeatTaskFactory = (_, _) => Task.CompletedTask;

            // THE PROVISIONER SEAM — installed BEFORE the first run's connection is constructed,
            // because the connection CAPTURES it at construction and the assignment body reaches
            // only that captured instance. A later assignment of the property therefore cannot
            // change which provisioner the first run's body uses, which is what makes a fixture
            // whose provisioner PARKS deterministic instead of racy. A null value is exactly the
            // previous behavior (the connection's own production provisioner).
            service.TestProvisioner = provisioner;

            var plan = new ReconnectPlan(service, runner, gitRestore);
            plan.StartNextRun(firstResponse);

            // THE RUN-1 STARTUP MILESTONE. The run has been STARTED above, but its registration
            // and initial-Ready writes are asynchronous: a test that observes anything BEFORE
            // they land would race correct production behavior — the classic symptoms being an
            // eager-provisioning fetch count that is still 0 (RetargetCallTime_p's baseline) or
            // an adopted run that has not published its adoption yet. Gating on the CURRENT
            // run's first write makes the plan's every later observation read settled state,
            // exactly like the handler-return milestone below gates per-message work.
            plan.WaitForInitialWriteAsync().GetAwaiter().GetResult();
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
            var holdReadiesFrom = PendingHoldReadiesFrom;
            PendingHoldReadiesFrom = null;

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

            if (holdReadiesFrom is { } holdReadyFrom)
                requests.HoldReadiesFrom = holdReadyFrom;

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
        /// PUSHES one assignment on the CURRENT run's stream and returns its execution task once —
        /// and only once — the ASSIGNMENT HANDLER for it has RETURNED.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE POST-HANDLER MILESTONE IS THE ONLY SOUND GATE HERE. Production starts the assignment's
        /// execution task (whose runner entry these fixtures observe) and only THEN builds the owned
        /// tasks and calls <c>InstallActiveAssignment</c>, all still INSIDE the handler and BEFORE the
        /// message loop re-arms its next read. So an observation made on the runner's entry can
        /// legitimately find the ownership slot EMPTY — the replacement drain has cleared the
        /// predecessor and the successor is not installed yet — and reading or acting on the slot at
        /// that instant would race correct production behavior.
        /// </para>
        /// <para>
        /// <see cref="ChannelResponseReader.ReadStarted"/> for the NEXT read is exactly the
        /// post-handler milestone: the loop re-arms that read only after the assignment handler
        /// returns, so once read N+1 has STARTED, the handler for message N has provably completed —
        /// including <c>InstallActiveAssignment</c>. Every caller of this helper therefore reads the
        /// ownership slot, observes the assignment's adapter, or ends the stream only after the
        /// successor is published, which is the happens-before relation the old runner-entry gate was
        /// missing.
        /// </para>
        /// <para>
        /// The read counter is captured BEFORE the push — but ONLY after the current run's INITIAL
        /// pending read is ESTABLISHED (see <see cref="CapturePostHandlerBaselineAsync"/>). That
        /// establishment is what makes the threshold sound on EVERY caller: "baseline + 1" is the read
        /// re-armed after THIS message's handler return only when a read was ALREADY in flight when the
        /// baseline was sampled. Production issues the run's first write (the registration/initial
        /// Ready) BEFORE the loop enters its first read, so a caller that sampled the counter straight
        /// after that write could observe no reads in flight and then await the INITIAL read instead of
        /// the post-handler one — exactly the hole this gate closes.
        /// </para>
        /// <para>
        /// A handler that faults never re-arms a read, so the bounded <see cref="Failsafe"/> turns that
        /// into a named failure instead of a hang.
        /// </para>
        /// </remarks>
        internal async Task<Task> PushAssignmentAsync(string taskId)
        {
            var readsBeforePush = await CapturePostHandlerBaselineAsync();
            Responses.Push(ResultAssignment(taskId));
            await WaitForAssignmentHandlerReturnedAsync(readsBeforePush);

            // THE EXECUTOR-SETTLED MILESTONE, on top of the handler-return one. The handler
            // guarantees the assignment is INSTALLED, but its execution task has only been
            // STARTED: whether the body has already entered (and how far) is the executor's own
            // scheduling. A test that immediately releases the runner or asserts on the
            // execution's progress would therefore race the body's entry. Gating on the
            // assignment's PROMPT-STARTED signal — the runner's SendPromptAsync entry, completed
            // by the body itself — makes the release and the later assertions deterministic:
            // the executor is provably parked inside its gated prompt before anything acts.
            await Runner.PromptStarted(taskId)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            return GetActiveExecution(Service);
        }

        /// <summary>
        /// CAPTURES THE READ BASELINE FOR ONE PUSH: it first ESTABLISHES that the CURRENT run's stream
        /// has a pending read IN FLIGHT, and only then samples the read count.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WHY THE ESTABLISHMENT COMES FIRST. Production re-arms its single pending read only AFTER a
        /// dispatched message's handler returns, so "one more read than the baseline" proves the
        /// handler returned — but only if the baseline was sampled while a read was ALREADY pending.
        /// The run's registration/initial-Ready write is issued before the message loop enters its
        /// first read, so a sample taken straight after that write can observe no reads in flight and
        /// then await the INITIAL read rather than the post-handler one. Waiting for read #1 of the
        /// CURRENT run first removes that hole for every caller: each run wires its own reader through
        /// the stream factory, so that counter starts at zero for every run.
        /// </para>
        /// <para>
        /// A run that never opens a stream (a rejected registration, a failed stream open) never starts
        /// a read, and no caller pushes an assignment on such a run — those tests assert the run's own
        /// outcome instead. If one ever did, the bounded <see cref="Failsafe"/> names the missing
        /// milestone instead of hanging.
        /// </para>
        /// </remarks>
        /// <returns>The read count, with the current run's initial read provably in flight.</returns>
        internal async Task<int> CapturePostHandlerBaselineAsync()
        {
            await Responses.ReadStarted(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            return Responses.ReadsStarted;
        }

        /// <summary>
        /// WAITS for the CURRENT run's assignment handler to RETURN — see
        /// <see cref="PushAssignmentAsync"/> for why <c>ReadStarted</c> is the milestone. Used by the
        /// successor tests, which push their messages directly so they can interleave their own gates.
        /// </summary>
        /// <param name="readsBeforePush">
        /// The baseline from <see cref="CapturePostHandlerBaselineAsync"/>, captured with a read already
        /// in flight: the awaited threshold is one MORE than it, i.e. the re-arm that only happens after
        /// that handler returned.
        /// </param>
        /// <exception cref="Xunit.Sdk.XunitException">
        /// <paramref name="readsBeforePush"/> is zero, meaning the caller sampled the counter before any
        /// read was pending. The baseline would NOT prove a handler return, so the mistake is reported
        /// by name instead of silently weakening the gate.
        /// </exception>
        internal Task WaitForAssignmentHandlerReturnedAsync(int readsBeforePush)
        {
            if (readsBeforePush < 1)
            {
                throw new Xunit.Sdk.XunitException(
                    "The read baseline was sampled before the current run had a pending read: sample it " +
                    "with CapturePostHandlerBaselineAsync, which establishes the initial read first. " +
                    "Otherwise the awaited threshold is the INITIAL read rather than the one re-armed " +
                    "after this assignment's handler returned.");
            }

            return Responses.ReadStarted(readsBeforePush + 1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// WAITS for the CURRENT run's FIRST WRITE to land — the registration/initial-Ready write a
        /// non-adopted run issues right after publication. This is the RUN-STARTUP milestone: a
        /// counted-write gate (no sleep, no polling), it makes every later observation in a test
        /// read state that production has already settled instead of racing the run's asynchronous
        /// startup. A run that is supposed to write an initial Ready always does, so the bounded
        /// <see cref="Failsafe"/> turns a stalled startup into a named failure instead of a hang.
        /// </summary>
        /// <remarks>
        /// ADOPTED runs never write an initial Ready — their first write is the carried delivery's —
        /// so callers must only await this on a run wired with an ordinary (non-adopted) register
        /// response, which is exactly what <see cref="StartFresh"/> wires.
        /// </remarks>
        internal Task WaitForInitialWriteAsync() =>
            CurrentRequests.WaitForWriteCountAsync(1)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        internal void Push(OrchestratorMessage message) => Responses.Push(message);

        /// <summary>Ends the CURRENT run's stream: the controlled stream loss.</summary>
        internal void CompleteStream() => Responses.TryComplete();

        /// <summary>
        /// PUBLISHES a standalone second connection UNDER the RUNNING run — no new run is started, so
        /// the service's single-run guard is respected. This is the exact move the sibling binding
        /// fixture makes to prove that a NON-carried assignment's captured dependency ignores a later
        /// publication. The connection is recorded in <see cref="Invokers"/>,
        /// <see cref="Requests"/> and <see cref="Connections"/> like a run's own, so the ordinary
        /// per-connection observables apply.
        /// </summary>
        internal WorkerConnection PublishStandaloneConnection()
        {
            var requests = new CarryRequestStream();
            requests.ReleaseAll();
            var responses = new ChannelResponseReader();
            responses.TryComplete();
            var invoker = new ScriptedInvoker(RegisterFor(AssignedId));
            var connection = new WorkerConnection(
                AssignedId,
                new HiveOrchestrator.HiveOrchestratorClient(invoker),
                BuildFaultingStream(requests, responses),
                provisionerOverride: null,
                includeProductionProvisioner: false,
                provisioningEnvironment: null,
                completionReceiptAckEnabled: false,
                completionReadyRequired: false);

            Invokers.Add(invoker);
            Requests.Add(requests);
            Readers.Add(responses);
            Connections.Add(connection);
            PublishedConnections.Add(Task.FromResult(connection));
            Service.PublishConnection(connection);
            return connection;
        }

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

        /// <summary>
        /// THE DEPENDENCY THE REAL ASSIGNMENT SETUP INSTALLED, completed at the executor's own
        /// <c>SetToolBridge</c> call. A <c>null</c> install is a production regression, so it is
        /// surfaced as a failure rather than silently captured.
        /// </summary>
        internal Task<IToolCallBridge> BridgeCaptured => _bridgeCaptured.Task;

        private readonly TaskCompletionSource<IToolCallBridge> _bridgeCaptured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// THE N-TH BRIDGE DEPENDENCY the real assignment setup installed (0-based, one per executed
        /// assignment), completed at that executor's own <c>SetToolBridge</c> call. The successor
        /// tests use it to hold the PREDECESSOR's adapter and the SUCCESSOR's adapter side by side.
        /// </summary>
        internal Task<IToolCallBridge> BridgeInstalled(int index)
        {
            lock (_gate)
            {
                if (!_bridgesInstalled.TryGetValue(index, out var source))
                {
                    source = new TaskCompletionSource<IToolCallBridge>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _bridgesInstalled[index] = source;
                }

                return source.Task;
            }
        }

        private readonly Dictionary<int, TaskCompletionSource<IToolCallBridge>> _bridgesInstalled = [];
        private int _bridgeInstallCount;

        /// <summary>
        /// EVERY provisioning callback <c>WorkerService</c> installed on this runner, in order —
        /// the observable that makes the carried callback's installation, its survival across a run
        /// boundary, and the ordinary per-run install/detach directly checkable.
        /// </summary>
        internal IReadOnlyList<Func<string?, CancellationToken, Task>?> Provisioners
        {
            get { lock (_gate) return [.. _provisioners]; }
        }

        /// <summary>The callback currently installed, or <c>null</c> when the last call detached it.</summary>
        internal Func<string?, CancellationToken, Task>? CurrentProvisioner
        {
            get { lock (_gate) return _provisioners.Count == 0 ? null : _provisioners[^1]; }
        }

        private readonly List<Func<string?, CancellationToken, Task>?> _provisioners = [];

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

        /// <summary>
        /// Records the installed bridge dependency — the SAME seam the sibling binding fixture uses —
        /// so a test can drive the REAL adapter the assignment's executor received.
        /// </summary>
        public void SetToolBridge(IToolCallBridge? bridge)
        {
            var installed = bridge ?? throw new InvalidOperationException(
                "The assignment setup must install a non-null bridge dependency.");
            _bridgeCaptured.TrySetResult(installed);

            TaskCompletionSource<IToolCallBridge> source;
            lock (_gate)
            {
                var index = _bridgeInstallCount++;
                if (!_bridgesInstalled.TryGetValue(index, out source!))
                {
                    source = new TaskCompletionSource<IToolCallBridge>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _bridgesInstalled[index] = source;
                }
            }

            source.TrySetResult(installed);
        }

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

        /// <summary>Records the installed provisioning callback, exactly as production installed it.</summary>
        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner)
        {
            lock (_gate) _provisioners.Add(provisioner);
        }

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
