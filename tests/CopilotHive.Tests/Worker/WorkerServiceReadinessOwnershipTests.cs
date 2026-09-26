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
/// BEHAVIORAL coverage of the SEPARATION BETWEEN AN ASSIGNMENT'S READINESS DECISION AND ITS
/// REPORTING, driven through the ACTUAL <c>WorkerService.ProcessMessagesAsync</c> loop.
/// <para>
/// WHAT IS BEING PINNED. Reporting no longer owns the <c>WorkerReady</c> write: it observes the
/// ORIGINAL execution and PUBLISHES — at exactly the condition that gated the old ordinary-Ready
/// attempt — the one fact its owner settles into the assignment's single readiness write. So a
/// COMPLETELY QUIET response stream still reaches EXACTLY ONE Ready, reporting terminates without
/// waiting on any transport, and the loop keeps consuming <c>ToolResponse</c> and receipt-ACK
/// messages while that write is still held.
/// </para>
/// <para>
/// Every milestone is a <see cref="TaskCompletionSource"/> gate owned by the fixture: NO sleeps, NO
/// polling and NO <c>Task.Delay</c>. Every started task — the loop and every assignment it owns — is
/// hoisted and joined in the test's <c>finally</c>, so nothing is abandoned and no service is
/// disposed under live work. <c>Complete</c> writes are recorded and released immediately unless a
/// test explicitly holds them; every <c>Ready</c> write is gateable, which is what makes a HELD
/// readiness write a deterministic observation.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerServiceReadinessOwnershipTests
{
    /// <summary>Failsafe bound; it only turns a regression into a named failure, never orders anything.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(10);

    private const string TaskA = "task-A";
    private const string TaskB = "task-B";

    // ══════════════════════════════════════════════════════════════════════════
    // 1. Reporting no longer owns the Ready write.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE QUIET STREAM — the core regression this change exists for. With NO further orchestrator
    /// message after the assignment, the loop must still notice that the report terminated and write
    /// EXACTLY ONE Ready, for the ENABLED and the DISABLED negotiated answer alike (ACK negotiation
    /// stays tracking-only).
    /// <para>
    /// REMOVAL PROOF. Without the loop's second wait the readiness fact is never observed, so the
    /// gated Ready entry below fails BY NAME on its bound rather than hanging.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietStream_NoAck_ReachesExactlyOneReady(bool ackEnabled)
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner, ackEnabled);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            reader.Push(Assignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // From here on NOTHING is pushed: the response stream is completely quiet.
            runner.Release(TaskA);

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            readinessWrite = CaptureReadinessWrite(
                service, "A quiet stream must still reach a started, retained readiness write.");
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(
                reporting.IsCompletedSuccessfully,
                "Reporting must terminate independently of the readiness write.");
            Assert.Equal(1, writer.ReadyCount);
            Assert.Single(writer.Completes);
            Assert.Equal(TaskA, GetActiveTaskId(service));
            Assert.False(connection.IsRetired);

            // The loop keeps consuming AFTER settling readiness: a further message is handled.
            reader.Push(Probe("after-ready"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);

            writer.ReleaseReady(0);
            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// WHILE THE READY WRITE IS HELD the ORIGINAL reporting is already TERMINAL — the two are
    /// SEPARATELY OWNED tasks — and the loop STILL consumes a <c>ToolResponse</c> and a
    /// completion-receipt ACK, latching the receipt without touching the held write.
    /// <para>
    /// REMOVAL PROOF. A readiness write awaited on the loop's own path, or still owned by reporting,
    /// cannot satisfy this: reporting would be incomplete at the gate, or the ACK and probe behind
    /// the held write would never be consumed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HeldReadyWrite_ReportingTerminal_AndAckAndToolResponsesStillConsumed()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            readinessWrite = CaptureReadinessWrite(
                service, "The loop must have started and retained the assignment's readiness write.");

            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "The ORIGINAL reporting must be terminal while the readiness write is still held.");
            Assert.NotSame(reporting, readinessWrite);
            Assert.False(readinessWrite.IsCompleted, "The readiness write must still be held.");

            var receipt = GetOwnerReceipt(service);
            Assert.True(GetReceiptArmed(receipt));

            // A tool response and a matching ACK, both consumed while the readiness write is parked —
            // proved by a following message the sequential loop can only reach afterwards.
            reader.Push(Probe("tool-response"));
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            reader.Push(Probe("after-ack"));
            await reader.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(GetReceiptConfirmed(receipt), "An ACK must be consumed while the Ready write is held.");
            Assert.False(readinessWrite.IsCompleted, "The ACK must not settle or finish the readiness write.");
            Assert.Equal(1, writer.ReadyCount);

            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(readinessWrite.IsCompletedSuccessfully);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
            Assert.Single(writer.Completes);
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// AN EARLY ACK ARRIVING DURING A HELD COMPLETE DOES NOT SETTLE READINESS. The ACK is latched;
    /// nothing is started, nothing is claimed, and exactly one Ready follows only once the Complete
    /// write is released and the report terminates.
    /// <para>
    /// REMOVAL PROOF. Any readiness decision derived from the ACK — or from "a result exists" —
    /// starts the write here, so the zero-Ready / unsettled-slot / unconsumed-claim assertions fail
    /// by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EarlyAckDuringHeldComplete_DoesNotSettleReadiness()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter { HoldCompletes = true };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receipt = GetOwnerReceipt(service);
            var readyClaim = GetOwnerReadyClaim(service);
            var ordinaryReady = GetOwnerOrdinaryReady(service);

            Assert.True(GetReceiptArmed(receipt));
            Assert.Equal(0, writer.ReadyCount);
            Assert.Null(GetRetainedReadinessWrite(service));
            Assert.False(IsOrdinaryReadySettled(ordinaryReady));

            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            reader.Push(Probe("acked"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(GetReceiptConfirmed(receipt));
            Assert.Equal(0, writer.ReadyCount);
            Assert.Null(GetRetainedReadinessWrite(service));
            Assert.False(IsOrdinaryReadySettled(ordinaryReady));
            Assert.Equal(0, GetReadyClaimState(readyClaim));

            // ONLY the report's own termination settles readiness.
            writer.ReleaseComplete(0);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            readinessWrite = CaptureReadinessWrite(
                service, "The report's termination must settle and start the readiness write.");
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(1, writer.ReadyCount);
            Assert.Equal(1, GetReadyClaimState(readyClaim));
            Assert.Single(writer.Completes);

            writer.ReleaseReady(0);
            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// A HANDLED PROVISIONING FAILURE PRODUCES NO RESULT YET STILL REACHES READINESS: eligibility is
    /// published because the EXECUTION TERMINATED NORMALLY (its own sanitized handler swallowed the
    /// failure), never because a result exists. Exactly one Ready follows, with no Complete.
    /// </summary>
    [Fact]
    public async Task ProvisioningFailureWithoutResult_StillEligible_OneReadyNoComplete()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        service.TestProvisioner = FailingProvisioner();

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            reader.Push(Assignment(TaskA));
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            readinessWrite = CaptureReadinessWrite(
                service, "A no-result but normally terminated execution must still start the readiness write.");
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Null(GetRetainedResult(service));
            Assert.True(execution.IsCompletedSuccessfully);
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "Reporting must terminate independently of the readiness write.");
            Assert.Equal(1, writer.ReadyCount);
            Assert.Empty(writer.Completes);

            writer.ReleaseReady(0);
            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
            Assert.Empty(writer.Completes);
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// AN ESCAPING EXECUTION DIAGNOSTIC LEAVES THE ASSIGNMENT INELIGIBLE: the execution FAULTED, so
    /// nothing is published, the shared claim stays UNCONSUMED and the matching cancel owns the ONE
    /// fallback Ready.
    /// <para>
    /// The delegate unwinds through its <c>finally</c> without reaching publication because its own
    /// sanitized failure diagnostic is written to a DEGRADED <c>Console.Error</c> that throws for
    /// that line — the same production-visible mechanism the pre-split fixtures used. A version that
    /// inferred eligibility from "the execution task terminated" would ALSO be eligible here, so the
    /// single-Ready identity is not what discriminates; what discriminates is that the execution
    /// FAULTED (asserted directly) and that the ONE Ready came from the cancel fallback, i.e. from
    /// the unconsumed claim.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EscapingExecutionDiagnostic_Ineligible_OnlyTheCancelFallbackWritesReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        service.TestProvisioner = FailingProvisioner();

        var originalErr = Console.Error;
        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        try
        {
            // Break ONLY the execution's own failure line, from BEFORE the assignment is handled.
            Console.SetError(new MarkerThrowingErrorWriter(
                "Task execution failed", new StringWriter(),
                new ReadinessSentinelException("degraded execution diagnostic")));

            reader.Push(Assignment(TaskA));
            reader.Push(Probe("installed"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE EXECUTION FAULTED — its own sanitized failure diagnostic escaped — so the report
            // observed a producer that did NOT terminate normally and published NO eligibility.
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            var executionFault = await Assert.ThrowsAsync<ReadinessSentinelException>(
                () => execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.NotNull(executionFault);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The matching cancel therefore owns the ONE fallback Ready, from the free claim.
            reader.Push(MatchingCancel(TaskA));

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            writer.ReleaseReady(0);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(1, writer.ReadyCount);
            Assert.Empty(writer.Completes);
            Assert.Null(GetActiveAssignment(service));
        }
        finally
        {
            Console.SetError(originalErr);
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. Ownership transitions, joins and races.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// REPLACEMENT AT THE ACTUAL CALL SITE: the successor's handler may not reset the runner, start
    /// its body or install its owner until ALL of the predecessor's owned tasks — INCLUDING its
    /// separately owned readiness write — have been joined by the handler's OWN
    /// <c>await DrainRetainedForReplacementAsync()</c>. Each assignment still produces EXACTLY ONE
    /// Ready.
    /// <para>
    /// WHY A DEQUEUE MILESTONE IS NOT ENOUGH, AND WHAT REPLACES IT. <c>Consumed</c> only proves a
    /// message left the channel, so it cannot establish that B's handler has entered its pre-drain
    /// window; on a legal schedule a mutant that omits or detaches the call-site drain could start
    /// only after the test released A and still satisfy a dequeue-gated fixture. This fixture
    /// therefore holds A's COMPLETE write instead, which keeps A's readiness eligibility UNPUBLISHED
    /// while B is delivered. The loop consequently CANNOT settle A's readiness write in its
    /// read/readiness race — there is nothing yet to settle — so the ONLY participant that can ever
    /// start it is <c>DrainAssignmentAsync</c>, reached exclusively through the handler's call-site
    /// drain, after that drain's own reporting join observed A's report publish the eligibility.
    /// </para>
    /// <para>
    /// THE POSITIVE IN-DRAIN RENDEZVOUS. A's readiness write ENTERING the writer is therefore
    /// production-visible proof that B's handler advanced past dequeue INTO its pre-drain window and
    /// is now parked inside the drain. The fixture awaits that entry — never a dequeue — before it
    /// releases anything destructive, and only then takes its pre-release observations.
    /// </para>
    /// <para>
    /// THE SCHEDULE-INDEPENDENT CALL-SITE DISCRIMINATOR. At B's reset entry — the FIRST production
    /// step after the call-site drain — the fixture captures the OWNERSHIP SLOT on the handler's own
    /// stack. A correct call site has <c>ClearActiveAssignment()</c>d A before returning, so the slot
    /// reads EMPTY; a handler whose <c>await</c> was REMOVED runs the reset with A STILL INSTALLED,
    /// and a DETACHED (<c>_ = ...</c>) drain likewise reaches the reset before its own continuation
    /// could clear. Both mutants are rejected by name regardless of how the scheduler interleaves,
    /// because the observation is taken inside production, not by a racing test continuation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Replacement_JoinsPredecessorReadinessWriteBeforeReset_EachAssignmentOneReady()
    {
        var runner = new GatedRunner();

        // HOLDING THE COMPLETE WRITE is what makes the rendezvous positive: A's report cannot reach
        // the old ordinary-Ready point, so its eligibility stays unpublished and the loop can never
        // settle A's readiness write on its own. Only the call-site drain can.
        var writer = new GatedWriter { HoldCompletes = true };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? executionA = null;
        Task? reportingA = null;
        Task? readinessA = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            // A's COMPLETE write is HELD, so A's report has NOT reached the ordinary-Ready point.
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            executionA = GetActiveExecution(service);
            reportingA = GetActiveReporting(service);
            var ownerA = GetActiveAssignment(service);
            var ordinaryReadyA = GetOwnerOrdinaryReady(service);
            await executionA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // NON-VACUITY: nothing is settled and no readiness write exists, so a later entry can
            // only have been produced by the drain.
            Assert.False(reportingA.IsCompleted, "A's report must still be inside its held Complete write.");
            Assert.False(IsOrdinaryReadySettled(ordinaryReadyA));
            Assert.Null(GetRetainedReadinessWrite(service));
            Assert.Equal(0, writer.ReadyCount);

            // THE CALL-SITE DISCRIMINATOR, captured on production's own stack at B's reset entry —
            // the FIRST production step after the handler's drain. Correct code has already cleared
            // A; an omitted or detached drain still has A installed here.
            var bResetReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var slotAtBReset = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            runner.OnResetEntered = _ =>
            {
                if (runner.ResetCount >= 2)
                {
                    slotAtBReset.TrySetResult(GetActiveAssignment(service));
                    bResetReached.TrySetResult();
                }
            };

            // THE LAST-INSTANT CAPTURE from inside A's held readiness write.
            object? ownerAtWriteRelease = null;
            var bStartedAtWriteRelease = true;
            writer.OnReadyReleasing = index =>
            {
                if (index != 0)
                    return;
                ownerAtWriteRelease = GetActiveAssignment(service);
                bStartedAtWriteRelease = runner.HasPromptStarted(TaskB);
            };

            // Deliver B while A's Complete — and therefore A's eligibility — is still held.
            reader.Push(ResultAssignment(TaskB));

            // Release A's Complete: A's report now publishes eligibility and terminates. The loop is
            // NOT in its readiness race (it is inside B's handler), so the only settler is the drain.
            writer.ReleaseComplete(0);

            // THE POSITIVE IN-HANDLER RENDEZVOUS: A's readiness write ENTERING proves B's handler
            // reached its pre-drain window and is parked inside the drain's readiness join. Nothing
            // else can produce it — the loop cannot settle an eligibility that was unpublished when
            // B was dispatched — so its absence IS the omitted/detached call-site drain.
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "A's readiness write never entered, so B's handler never reached the call-site "
                + "drain: the handler's await DrainRetainedForReplacementAsync() was removed or "
                + "detached, leaving A's readiness write unsettled and unjoined.");
            readinessA = CaptureReadinessWrite(
                service, "The replacement drain must have started and retained A's readiness write.");
            await reportingA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // PRE-RELEASE, with A's readiness write provably held INSIDE the call-site drain.
            Assert.False(readinessA.IsCompleted, "A's readiness write must still be held.");
            Assert.False(loop.IsCompleted, "The loop must not finish while A's readiness write is held.");
            Assert.Same(ownerA, GetActiveAssignment(service));
            Assert.Equal(TaskA, GetActiveTaskId(service));
            Assert.False(connection.IsRetired);
            Assert.False(
                runner.HasPromptStarted(TaskB),
                "B must not start while A's readiness write is still running.");
            Assert.False(
                bResetReached.Task.IsCompleted,
                "B's session reset was reached while A's readiness write was still parked — the "
                + "replacement drain did not join it, was detached, or was not awaited.");

            // ONLY NOW release A's readiness write: the drain can finish joining and proceed.
            writer.ReleaseReady(0);
            await readinessA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE CALL-SITE VERDICT: at B's reset the slot must already be EMPTY, which only the
            // handler's own awaited drain can produce.
            await bResetReached.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(
                await slotAtBReset.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // The held-write capture agrees: A still owned the slot and B had not started.
            Assert.Same(ownerA, ownerAtWriteRelease);
            Assert.False(
                bStartedAtWriteRelease,
                "B must not have started while A's readiness write was still alive.");

            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(TaskB, GetActiveTaskId(service));
            runner.Release(TaskB);
            await writer.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            writer.ReleaseComplete(1);

            await writer.ReadyEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            TrackReadinessWrite(GetRetainedReadinessWrite(service));
            writer.ReleaseReady(1);
            await writer.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);

            Assert.Equal(2, writer.ReadyCount);
            Assert.Equal(2, writer.Completes.Count);
            Assert.Equal(1, runner.MaxConcurrentPrompts);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, writer.ReadyCount);
            Assert.Equal(0, GetSlotOccupancy(service));
        }
        finally
        {
            runner.OnResetEntered = null;
            writer.OnReadyReleasing = null;
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution A", executionA),
                ("assignment reporting A", reportingA),
                ("readiness write A", readinessA),
                ("loop", loop));
        }
    }

    /// <summary>
    /// THE REPLACEMENT TRANSITION ITSELF is synchronously driven to its first incomplete await after
    /// A was installed through the real loop. With A's execution/report already terminal and its
    /// readiness write HELD, the only legal incomplete await is that exact write's join. This closes
    /// the scheduler gap in a message-dequeue-only replacement observation: a join-removal mutant
    /// clears A before <see cref="InvokeReplacementDrain"/> even returns and fails every immediate
    /// pre-release assertion by name.
    /// </summary>
    [Fact]
    public async Task ReplacementTransition_HeldReadinessWriteIsJoinedBeforeOwnerClear()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? executionA = null;
        Task? reportingA = null;
        Task? readinessA = null;
        var replacementDrain = Task.CompletedTask;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var ownerA = GetActiveAssignment(service);
            var retainedA = GetRetainedResult(service);
            var ownerCts = GetOwnerCts(service);
            executionA = GetActiveExecution(service);
            reportingA = GetActiveReporting(service);
            readinessA = CaptureReadinessWrite(
                service, "The loop must have retained A's separately owned readiness write.");
            await executionA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reportingA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            object? ownerAtWriteRelease = null;
            writer.OnReadyReleasing = index =>
            {
                if (index == 0)
                    ownerAtWriteRelease = GetActiveAssignment(service);
            };

            // Async methods run synchronously to their first incomplete await. Cancellation is not
            // requested, and both original-task joins are complete, so correct code can return from
            // this call only after reaching A's HELD readiness-write join.
            replacementDrain = InvokeReplacementDrain(service);

            Assert.False(
                replacementDrain.IsCompleted,
                "Replacement must be parked on A's held readiness-write join.");
            Assert.False(readinessA.IsCompleted, "A's readiness write must still be held.");
            Assert.Same(ownerA, GetActiveAssignment(service));
            Assert.Same(retainedA, GetRetainedResult(service));
            Assert.False(connection.IsRetired);
            Assert.Null(Record.Exception(() => _ = ownerCts.Token));

            writer.ReleaseReady(0);
            await readinessA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await replacementDrain.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Same(ownerA, ownerAtWriteRelease);
            Assert.Null(GetActiveAssignment(service));
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
            Assert.False(connection.IsRetired);
            Assert.Equal(1, writer.ReadyCount);

            // The actual loop remains usable after the transition and installs B with independent
            // execution/report/readiness ownership; no runner overlap or duplicate A Ready occurs.
            reader.Push(ResultAssignment(TaskB));
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskB);
            await writer.ReadyEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            TrackReadinessWrite(GetRetainedReadinessWrite(service));
            writer.ReleaseReady(1);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, writer.ReadyCount);
            Assert.Equal(1, runner.MaxConcurrentPrompts);
        }
        finally
        {
            writer.OnReadyReleasing = null;
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution A", executionA),
                ("assignment reporting A", reportingA),
                ("readiness write A", readinessA),
                ("replacement drain", replacementDrain),
                ("loop", loop));
        }
    }

    /// <summary>
    /// A MATCHING CANCEL THAT SETTLES READINESS ITSELF must still yield EXACTLY ONE Ready: the
    /// ownership transition settles the eligibility and joins the write it starts, and the cancel
    /// FALLBACK cannot duplicate it because the shared claim was consumed by that same settlement.
    /// <para>
    /// THE SCHEDULE IS FORCED BY AN IN-HANDLER RENDEZVOUS, not by a read milestone. A dequeue
    /// milestone would only say the cancel message left the channel, which leaves a legal schedule
    /// where the Complete write is released before the handler even enters its drain — and then the
    /// NORMAL loop could settle readiness, letting a drain-settlement removal survive. Instead the
    /// fixture registers a benign callback on the assignment's OWN source: the matching-cancel
    /// drain's FIRST action is <c>CaptureCancellationFailureAsync</c>, so that callback fires from
    /// INSIDE the handler, once the drain has provably begun. Only then is the Complete write
    /// released, so the report can terminate only while the loop is parked inside the drain's
    /// reporting join — the loop cannot be at its readiness observation at all, which is confirmed
    /// positively by the read counter not having advanced (production re-arms its ONE pending read
    /// only after a handler returns).
    /// </para>
    /// <para>
    /// REMOVAL PROOFS. A drain that does not settle readiness writes ZERO Ready (the loop already
    /// left the observation and the owner is cleared before it returns), and a drain that settles
    /// but lets the fallback write too produces TWO. A drain that settles but does NOT JOIN the
    /// write it started is killed by the last-instant capture taken from inside the held write: at
    /// that instant the owner must still be installed and the loop still running.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MatchingCancelSettlingBeforeLoopObservation_ExactlyOneReadyNoFallbackDuplicate()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter { HoldCompletes = true };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        var drainEnteredRegistration = default(CancellationTokenRegistration);
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var owner = GetActiveAssignment(service);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.False(reporting.IsCompleted, "The report must still be inside its held Complete write.");
            Assert.False(IsOrdinaryReadySettled(GetOwnerOrdinaryReady(service)));
            Assert.Equal(0, writer.ReadyCount);

            // THE IN-HANDLER RENDEZVOUS. A benign callback on the assignment's OWN source: the
            // matching-cancel drain requests cancellation as its very first step, so this fires from
            // inside the handler and nowhere else. It records only — it changes no outcome.
            var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drainEnteredRegistration = GetOwnerCts(service).Token.Register(() => drainEntered.TrySetResult());

            // THE LAST-INSTANT CAPTURE, taken from inside the held readiness write — i.e. on the
            // joiner's own stack while the write is provably alive. A drain that started the write
            // without joining it has already disposed, cleared and returned by then, so it can never
            // record an installed owner here.
            object? ownerAtWriteRelease = null;
            var loopAliveAtWriteRelease = false;
            writer.OnReadyReleasing = _ =>
            {
                ownerAtWriteRelease = GetActiveAssignment(service);
                loopAliveAtWriteRelease = !loop.IsCompleted;
            };

            var readsBeforeCancel = reader.ReadsStarted;

            // Deliver the matching cancel and wait for the HANDLER — not the dequeue — to begin.
            reader.Push(MatchingCancel(TaskA));
            await drainEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // POSITIVE IN-HANDLER EVIDENCE. The loop re-arms its ONE pending read only after a
            // handler returns, so an unadvanced read count proves the loop is INSIDE the cancel
            // handler and therefore cannot be sitting at its readiness observation.
            Assert.Equal(readsBeforeCancel, reader.ReadsStarted);
            Assert.False(reporting.IsCompleted);
            Assert.False(IsOrdinaryReadySettled(GetOwnerOrdinaryReady(service)));
            Assert.Equal(0, writer.ReadyCount);

            // ONLY NOW release the Complete write: the report terminates and publishes its
            // eligibility while the loop is provably parked inside the drain, so the DRAIN — and
            // nothing else — can settle it.
            writer.ReleaseComplete(0);

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessWrite = CaptureReadinessWrite(
                service, "The matching-cancel drain must settle and start the assignment's readiness write.");

            // PRE-RELEASE, with the write still HELD: the transition may not have finished, cleared
            // ownership or retired the connection.
            Assert.False(readinessWrite.IsCompleted, "The readiness write must still be held.");
            Assert.False(loop.IsCompleted, "The loop must not finish while the readiness write is held.");
            Assert.Same(owner, GetActiveAssignment(service));
            Assert.False(connection.IsRetired, "A matching cancel never retires the connection.");
            Assert.Equal(readsBeforeCancel, reader.ReadsStarted);

            writer.ReleaseReady(0);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // THE JOIN PROOF: the capture was taken while the write was alive, and it saw the exact
            // original owner still installed with the loop still running.
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Same(owner, ownerAtWriteRelease);
            Assert.True(
                loopAliveAtWriteRelease,
                "The loop must still have been running while the readiness write it started was alive.");

            reader.Push(Probe("after-cancel"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(1, writer.ReadyCount);
            Assert.Single(writer.Completes);
            Assert.Null(GetActiveAssignment(service));
            Assert.False(connection.IsRetired, "A matching cancel never retires the connection.");

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            drainEnteredRegistration.Dispose();
            writer.OnReadyReleasing = null;
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// WHEN THE PENDING READ AND READINESS OBSERVATION ARE BOTH COMPLETE BEFORE THE REAL LOOP'S
    /// <c>WhenAny</c>, the read wins because production lists and checks it first — and the losing
    /// readiness fact remains available for the NEXT iteration, where it starts exactly one Ready.
    /// <para>
    /// THE TIE IS CONSTRUCTED INSIDE THE REAL READ PATH. From A's reset handler the fixture installs
    /// a one-shot <see cref="ChannelResponseReader.BeforeNextRead"/> hook. On read #2 that hook uses
    /// the slot's production publication member and enqueues a probe, then returns synchronously;
    /// consequently both <c>pendingRead</c> and <c>readinessWait</c> are complete before
    /// <c>Task.WhenAny</c> is evaluated. The Ready-entry hook records that read #3 was already armed,
    /// proving the probe's handler path won first. If observation consumed the losing readiness fact,
    /// Ready never enters; if readiness won the tie, its entry sees only two reads.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SimultaneouslyCompletedReadWins_AndLosingReadinessRemainsAvailable()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        var tiePrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyEntryCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readsAtReadyEntry = -1;
        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            // Install the tie constructor from INSIDE A's handler. Read #1 already owns the
            // assignment message; production starts read #2 only after this handler returns.
            runner.OnResetEntered = _ =>
            {
                if (runner.ResetCount != 1)
                    return;

                reader.BeforeNextRead = () =>
                {
                    // Both facts become complete synchronously on the real read's stack, before
                    // ReadNextMessageAsync returns its already-completed pendingRead to the loop.
                    PublishOrdinaryReadyEligibility(GetOwnerOrdinaryReady(service));
                    reader.Push(Probe("simultaneous-read-wins"));
                    tiePrepared.TrySetResult();
                    return Task.CompletedTask;
                };
            };

            writer.OnReadyEntering = _ =>
            {
                readsAtReadyEntry = reader.ReadsStarted;
                readyEntryCaptured.TrySetResult();
            };

            reader.Push(ResultAssignment(TaskA));
            await tiePrepared.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await readyEntryCaptured.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            readinessWrite = CaptureReadinessWrite(
                service, "The losing readiness fact must remain available for the next loop iteration.");

            // Read #3 is armed only AFTER the probe from read #2 was dispatched. This is positive
            // evidence that the completed read won the tie before the losing readiness fact settled.
            Assert.Equal(3, readsAtReadyEntry);
            Assert.Equal(3, reader.ReadsStarted);
            Assert.True(reader.Consumed(2).IsCompleted);
            Assert.False(readinessWrite.IsCompleted, "The one readiness write is genuinely held.");
            Assert.Equal(1, writer.ReadyCount);

            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Let the real execution/reporting path finish. Its later idempotent publication must not
            // create another Ready after the losing fact was settled once.
            runner.Release(TaskA);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            runner.OnResetEntered = null;
            writer.OnReadyEntering = null;
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// THE LOSING READINESS SIGNAL REMAINS AVAILABLE. Production races ONE owned pending read
    /// against the assignment's readiness observation; when the read wins a tie, the readiness fact
    /// must survive for a later iteration, because OBSERVING it is not what consumes it — SETTLING
    /// it is.
    /// <para>
    /// THE OBSERVATION WINDOW IS DETERMINISTIC, not a schedule. Every observation below is taken
    /// while the loop is provably parked INSIDE the matching-cancel handler's drain — signalled from
    /// inside that handler by a benign callback on the assignment's own source, and confirmed by the
    /// read counter not having advanced (production re-arms its ONE pending read only after a
    /// handler returns). In that window the loop is not at its readiness race at all, so it cannot
    /// settle the slot underneath the assertions.
    /// </para>
    /// <para>
    /// WHAT IS PINNED, all through the slot's OWN production members:
    /// <list type="bullet">
    ///   <item><description>after publication, EVERY armed observation is already complete while the
    ///   slot is still UNSETTLED — so a loop iteration that discarded its readiness signal in favour
    ///   of a completed read loses nothing;</description></item>
    ///   <item><description>the FIRST settlement starts exactly one write and consumes the shared
    ///   claim; a SECOND settlement returns the SAME write and starts nothing;</description></item>
    ///   <item><description>after settlement an armed observation NEVER completes — so the loop
    ///   cannot spin on an already-observed fact.</description></item>
    /// </list>
    /// A slot that consumed the fact on observation fails the first group by name; one that settled
    /// twice fails the second; one that kept signalling after settlement fails the third. The
    /// end-to-end outcome — EXACTLY ONE Ready, joined before the ownership clear — is still asserted
    /// through the real loop.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReadinessSignalSurvivesObservation_AndSettlesExactlyOnce()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter { HoldCompletes = true };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        var drainEnteredRegistration = default(CancellationTokenRegistration);
        try
        {
            // Hold the Complete write so the report cannot terminate: the assignment is installed
            // and running, and its slot is NOT yet eligible.
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            var slot = GetOwnerOrdinaryReady(service);
            var readyClaim = GetOwnerReadyClaim(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // BEFORE PUBLICATION an armed observation cannot complete, so a loop iteration can never
            // settle an assignment that has not reached the old ordinary-Ready point.
            Assert.False(ArmOrdinaryReady(slot).IsCompleted);
            Assert.False(IsOrdinaryReadySettled(slot));
            Assert.Equal(0, GetReadyClaimState(readyClaim));

            // PARK THE LOOP INSIDE THE CANCEL HANDLER'S DRAIN. The drain requests cancellation as
            // its very first step, so this benign callback fires from inside the handler; with the
            // Complete write still held, the drain then parks on its reporting join and stays there.
            var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drainEnteredRegistration = GetOwnerCts(service).Token.Register(() => drainEntered.TrySetResult());

            var readsBeforeCancel = reader.ReadsStarted;
            reader.Push(MatchingCancel(TaskA));
            await drainEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // POSITIVE IN-HANDLER EVIDENCE: the loop has not re-armed its pending read, so it is
            // inside the handler and cannot be at its readiness race.
            Assert.Equal(readsBeforeCancel, reader.ReadsStarted);
            Assert.False(reporting.IsCompleted, "The report is still held inside its Complete write.");

            // PUBLISH through the slot's OWN production transition — the identical call reporting
            // makes at the old ordinary-Ready point. The loop is parked in the drain, so nothing can
            // settle underneath the observations below.
            PublishOrdinaryReadyEligibility(slot);

            // THE LOSING-SIGNAL PROPERTY: repeated observation NEVER consumes the fact. Each armed
            // observation is already complete while the slot is still unsettled — exactly the state
            // a tie-losing loop iteration leaves behind.
            Assert.True(ArmOrdinaryReady(slot).IsCompleted);
            Assert.True(ArmOrdinaryReady(slot).IsCompleted);
            Assert.True(ArmOrdinaryReady(slot).IsCompleted);
            Assert.False(IsOrdinaryReadySettled(slot), "Observation must not settle the slot.");
            Assert.Equal(0, GetReadyClaimState(readyClaim));
            Assert.Equal(0, writer.ReadyCount);

            // SETTLEMENT is the one-shot: it starts exactly one write and consumes the claim.
            var firstWrite = InvokeSettleOrdinaryReady(service, slot);
            Assert.NotNull(firstWrite);
            TrackReadinessWrite(firstWrite);
            readinessWrite = firstWrite;
            Assert.True(IsOrdinaryReadySettled(slot), "Settlement is what consumes the fact.");
            Assert.Equal(1, GetReadyClaimState(readyClaim));

            var secondWrite = InvokeSettleOrdinaryReady(service, slot);
            Assert.Same(firstWrite, secondWrite);
            Assert.Equal(1, GetReadyClaimState(readyClaim));

            // AFTER SETTLEMENT an armed observation never completes: no spin is possible.
            Assert.False(
                ArmOrdinaryReady(slot).IsCompleted,
                "A settled slot must hand out a never-completing observation, so the loop cannot spin.");

            Assert.Same(firstWrite, GetRetainedReadinessWrite(service));

            // Let the held Complete write go: the report terminates, the drain joins it and then
            // finds the readiness already settled — so it joins THIS write rather than starting one.
            // The Complete write also holds production's send gate, so the settled readiness write
            // can only ENTER the writer once it is released.
            writer.ReleaseComplete(0);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // PRE-RELEASE: the drain may not clear ownership while the write is still held.
            Assert.False(readinessWrite.IsCompleted, "The readiness write must still be held.");
            Assert.False(loop.IsCompleted, "The loop must not finish while the readiness write is held.");
            Assert.NotNull(GetActiveAssignment(service));

            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // EXACTLY ONE Ready for the assignment, and the cancel fallback could not add another.
            reader.Push(Probe("after-settlement"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
            Assert.Single(writer.Completes);
            Assert.Null(GetActiveAssignment(service));

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            drainEnteredRegistration.Dispose();
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// A STALE CANCEL — naming a task that already finished and was replaced — must not touch the
    /// successor nor consume its readiness claim: the successor still produces its OWN single Ready.
    /// </summary>
    [Fact]
    public async Task StaleCancelForFinishedTask_SuccessorKeepsItsOwnSingleReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            TrackReadinessWrite(GetRetainedReadinessWrite(service));
            writer.ReleaseReady(0);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            reader.Push(ResultAssignment(TaskB));
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            reader.Push(MatchingCancel(TaskA));
            reader.Push(Probe("stale"));
            await reader.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.False(runner.WasCancelled(TaskB), "A stale cancel must not cancel the successor.");
            Assert.Equal(TaskB, GetActiveTaskId(service));
            Assert.False(writer.CompleteEntered(1).IsCompleted, "The successor must still be running.");

            runner.Release(TaskB);
            await writer.ReadyEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            TrackReadinessWrite(GetRetainedReadinessWrite(service));
            writer.ReleaseReady(1);
            await writer.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);

            Assert.Equal(2, writer.ReadyCount);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, writer.ReadyCount);
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
        }
    }

    /// <summary>An IDLE cancel — nothing in flight — still emits its own single Ready.</summary>
    [Fact]
    public async Task IdleCancel_EmitsSingleReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        try
        {
            reader.Push(MatchingCancel("task-idle"));
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            writer.ReleaseReady(0);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            Assert.Equal(1, writer.ReadyCount);
            Assert.Null(GetActiveAssignment(service));

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
        }
    }

    /// <summary>
    /// EOF WITH A LIVE TOKEN still permits the draining assignment's single Ready, and the TEARDOWN
    /// drain must JOIN that write before it clears ownership. RETIREMENT ORDER IS CHANGED BY DESIGN:
    /// the stream-loss path retires the connection at the read-await site as its VERY FIRST action,
    /// so the connection is already retired while the drain joins the write (the drain's own
    /// settlement is unaffected — this assignment had already claimed its ordinary Ready, so it is
    /// NOT carried and today's cancel-and-drain path runs exactly as before).
    /// <para>
    /// THE JOIN PROOF IS TAKEN FROM INSIDE THE HELD WRITE. Before releasing it the test asserts the
    /// loop is still incomplete and the owner is still installed; then the write's own release
    /// callback re-records those same facts on the joiner's stack, at the last instant the write is
    /// provably alive. A teardown that started the write without joining it would already have
    /// cleared by then, so it can never produce that capture — and the loop would be complete while a
    /// readiness write was still in flight.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EofAfterReportTerminated_TeardownDrainWritesTheSingleReadyBeforeRetirement()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            var owner = GetActiveAssignment(service);
            var ownerCts = GetOwnerCts(service);

            // THE LAST-INSTANT CAPTURE, recorded on the joiner's own stack while the write is alive.
            object? ownerAtWriteRelease = null;
            var loopAliveAtWriteRelease = false;
            var retiredAtWriteRelease = true;
            writer.OnReadyReleasing = _ =>
            {
                ownerAtWriteRelease = GetActiveAssignment(service);
                loopAliveAtWriteRelease = !loop.IsCompleted;
                retiredAtWriteRelease = connection.IsRetired;
            };

            // Finish execution/reporting first so the ordinary readiness write is genuinely held.
            // The matching-cancel race separately proves transition-side settlement; this test pins
            // EOF's responsibility to JOIN an already-started write before clear/retirement.
            runner.Release(TaskA);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessWrite = CaptureReadinessWrite(
                service, "The loop must retain the assignment's ordinary readiness write.");
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Pre-cancel the assignment source so teardown's cancellation phase is synchronous, then
            // use response-lifetime closure as the positive rendezvous that EOF entered finally.
            await ownerCts.CancelAsync();
            var responseLifetime = connection.RegisterToolResponse("eof-drain-rendezvous");
            reader.TryComplete();
            var responseClosure = await Record.ExceptionAsync(
                () => responseLifetime.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.NotNull(responseClosure);
            Assert.IsNotType<TimeoutException>(responseClosure);

            // PRE-RELEASE, with the write still HELD and EOF positively inside teardown: teardown may
            // not have finished or cleared ownership. The connection, however, is ALREADY RETIRED —
            // the stream-loss path retires it FIRST, before the drain, by design.
            Assert.False(readinessWrite.IsCompleted, "The readiness write must still be held.");
            Assert.False(
                loop.IsCompleted,
                "The loop must not finish while the readiness write its teardown started is held.");
            Assert.Same(owner, GetActiveAssignment(service));

            writer.ReleaseReady(0);

            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE JOIN PROOF: the capture saw the exact original owner installed and the loop running
            // while the write was still alive. The connection is retired from the START of this
            // teardown (the early retire), so the capture records THAT fact rather than its absence.
            Assert.Same(owner, ownerAtWriteRelease);
            Assert.True(
                loopAliveAtWriteRelease,
                "Teardown must still have been running while the readiness write it started was alive.");
            Assert.True(
                retiredAtWriteRelease,
                "The stream-loss path retires the connection BEFORE the drain, by design.");

            Assert.Equal(1, writer.ReadyCount);
            Assert.Single(writer.Completes);
            Assert.True(connection.IsRetired);
            Assert.Equal(0, GetSlotOccupancy(service));
        }
        finally
        {
            writer.OnReadyReleasing = null;
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// THE SHARED TEARDOWN TRANSITION (used by EOF and reader-fault cleanup) is driven directly after
    /// the real loop installed A. With cancellation and both original-task joins already complete,
    /// its only incomplete await is A's held readiness write. This makes the transition's join proof
    /// independent of the scheduler that resumes an EOF/faulting read: a no-join mutant clears the
    /// owner and completes before <see cref="InvokeTeardownDrain"/> returns.
    /// </summary>
    [Fact]
    public async Task TeardownTransition_HeldReadinessWriteIsJoinedBeforeOwnerClear()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        var teardownDrain = Task.CompletedTask;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var owner = GetActiveAssignment(service);
            var retained = GetRetainedResult(service);
            var ownerCts = GetOwnerCts(service);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            readinessWrite = CaptureReadinessWrite(
                service, "The loop must retain the assignment's readiness write.");
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await ownerCts.CancelAsync();

            object? ownerAtWriteRelease = null;
            writer.OnReadyReleasing = _ => ownerAtWriteRelease = GetActiveAssignment(service);

            teardownDrain = InvokeTeardownDrain(service);

            // IMMEDIATE, schedule-independent PRE-RELEASE proof: all earlier transition work was
            // synchronous, so correct code can return only from the held readiness-write await.
            Assert.False(teardownDrain.IsCompleted, "Teardown must be parked on the held write.");
            Assert.False(readinessWrite.IsCompleted);
            Assert.Same(owner, GetActiveAssignment(service));
            Assert.Same(retained, GetRetainedResult(service));
            Assert.False(connection.IsRetired);
            Assert.Null(Record.Exception(() => _ = ownerCts.Token));

            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await teardownDrain.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Same(owner, ownerAtWriteRelease);
            Assert.Null(GetActiveAssignment(service));
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
            Assert.False(connection.IsRetired, "The transition drains ownership; the loop owns retirement.");

            // Let the actual loop perform the remaining EOF response-ending/retirement stages.
            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(connection.IsRetired);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            writer.OnReadyReleasing = null;
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("teardown drain", teardownDrain),
                ("loop", loop));
        }
    }

    /// <summary>
    /// THE CANCELLATION BOUNDARY FOR THE SEPARATELY OWNED WRITE. With an ordinary readiness write
    /// genuinely HELD, the loop's stream token is cancelled: the write must receive THAT token,
    /// observe the cancellation while parked, and still be JOINED by the teardown drain before
    /// ownership clears and the connection retires.
    /// <para>
    /// WHAT THIS KILLS. (1) A fake writer whose cancellable overload forwarded
    /// <c>CancellationToken.None</c> — or production writing Ready with a token other than the
    /// connection's stream token — never cancels the parked write, so the in-write cancellation
    /// capture never fires and the test fails by name. (2) A drain that does NOT join the cancelled
    /// write: the write is held inside its cancellation unwind, so a non-joining teardown completes
    /// the loop and clears/retires while that write is still alive — which the pre-release
    /// assertions and the unwind-time capture both reject.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StreamTokenCancelledWhileReadinessWriteHeld_WriteObservesTokenAndIsJoined()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var loop = InvokeProcessMessages(service, connection, loopCts.Token);

        // The cancelled write is HELD inside its unwind so the test can observe it still alive.
        var cancellationUnwind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.ReadyCancellationUnwind = cancellationUnwind;

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            var owner = GetActiveAssignment(service);

            // POSITIVE EVIDENCE that the FORWARDED token reached the parked write, captured on the
            // cancellation's own stack together with the state at that instant.
            var writeObservedCancellation = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            object? ownerAtCancellation = null;
            var loopAliveAtCancellation = false;
            var retiredAtCancellation = true;
            writer.OnReadyCancelled = _ =>
            {
                ownerAtCancellation = GetActiveAssignment(service);
                loopAliveAtCancellation = !loop.IsCompleted;
                retiredAtCancellation = connection.IsRetired;
                writeObservedCancellation.TrySetResult();
            };

            runner.Release(TaskA);

            // The readiness write is genuinely HELD — entered and parked on its gate.
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessWrite = CaptureReadinessWrite(
                service, "The loop must have started and retained the assignment's readiness write.");
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.False(readinessWrite.IsCompleted, "The readiness write must be held before cancellation.");

            // CANCEL THE STREAM TOKEN while that write is held. The write is parked on a wait bound
            // to the token production forwarded, so it must observe the cancellation itself.
            await loopCts.CancelAsync();

            await writeObservedCancellation.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // AT THE CANCELLATION INSTANT the write is provably alive, so no transition may have
            // cleared ownership or retired the connection yet.
            Assert.Same(owner, ownerAtCancellation);
            Assert.True(
                loopAliveAtCancellation,
                "The loop must still have been running while its cancelled readiness write was alive.");
            Assert.False(
                retiredAtCancellation,
                "The connection must not retire while the cancelled readiness write is still alive.");

            // The write is held inside its cancellation unwind: teardown cannot be finished.
            Assert.False(readinessWrite.IsCompleted, "The cancelled write is held inside its unwind.");
            Assert.False(
                loop.IsCompleted,
                "Teardown must JOIN the cancelled readiness write before the loop can finish.");
            Assert.NotNull(GetActiveAssignment(service));
            Assert.False(connection.IsRetired);

            // Release the unwind: the cancelled write terminates and teardown completes.
            cancellationUnwind.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            var outcome = await Record.ExceptionAsync(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.IsNotType<TimeoutException>(outcome);
            Assert.IsAssignableFrom<OperationCanceledException>(outcome);

            // ONE attempt, never retried, and the whole teardown still completed.
            Assert.Equal(1, writer.ReadyCount);
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.True(connection.IsRetired);
        }
        finally
        {
            writer.OnReadyCancelled = null;
            cancellationUnwind.TrySetResult();
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. Error precedence.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN ORDINARY READINESS FAULT IS NONFATAL. The write fails, but the loop stays HEALTHY — a
    /// following message is still consumed and the loop finishes normally — and the failure is
    /// reported only in sanitized form, never as the loop's outcome.
    /// <para>
    /// REMOVAL PROOF. Letting an ordinary readiness failure propagate (or otherwise become the
    /// loop's outcome) makes the following probe unconsumable and this test fails by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OrdinaryReadinessFault_IsNonfatal_LoopStaysHealthy()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            Console.SetError(stdErr);
            writer.FailNextReadyWrite = new ReadinessWritePrimaryException("injected readiness failure");

            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessWrite = CaptureReadinessWrite(
                service, "The loop must have started and retained the assignment's readiness write.");
            writer.ReleaseReady(0);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The write really did FAIL: its fault is the readiness write's own, never the loop's.
            await Assert.ThrowsAsync<ReadinessWritePrimaryException>(
                () => readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // NONFATAL: the loop is still alive and keeps dispatching.
            reader.Push(Probe("after-ordinary-fault"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.False(loop.IsCompleted, "An ordinary readiness failure must not fault the loop.");
            Assert.Equal(1, writer.ReadyCount);

            // The failure is OBSERVED with the drain-style sanitized treatment: its own message is
            // rendered nowhere, so it can never leak transport payloads or replace an outcome.
            Assert.DoesNotContain(
                "injected readiness failure", stdErr.ToString(), StringComparison.Ordinal);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            Console.SetError(originalErr);
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// A MATCHING-CANCEL FALLBACK READY FAILURE REMAINS PRIMARY OVER A DEFERRED
    /// CANCELLATION-CALLBACK ERROR: the fallback write's EXCEPTION IDENTITY propagates out of the
    /// loop while the callback evidence is merely reported in sanitized, guarded form.
    /// </summary>
    [Fact]
    public async Task MatchingCancelFallbackReadyFault_IsPrimaryOverDeferredCallbackError()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        service.TestProvisioner = FailingProvisioner();

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        var injected = new ReadinessWritePrimaryException("injected fallback readiness failure");
        CancellationTokenRegistration callbackRegistration = default;

        // HOISTED so the finally joins the REAL response loop even when an assertion fails: the
        // degraded diagnostic sink must be installed before the loop starts, but the loop's task
        // must never be scoped to the try.
        var loop = Task.CompletedTask;
        WorkerConnection? connection = null;
        Task? execution = null;
        Task? reporting = null;
        try
        {
            // A DEGRADED execution diagnostic makes the execution FAULT, so nothing is published and
            // the claim stays free for the cancel fallback.
            Console.SetError(new MarkerThrowingErrorWriter(
                "Task execution failed", stdErr,
                new ReadinessSentinelException("degraded execution diagnostic")));

            connection = TestConnectionFactory.Attach(
                service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
            loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

            // The FAILING provisioner means the executor never runs, so the assignment handler's own
            // completion is observed through a CONSUMED probe rather than a prompt milestone.
            reader.Push(Assignment(TaskA));
            reader.Push(Probe("installed"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var ownerCts = GetOwnerCts(service);
            var callbackFailure = new ReadinessSentinelException("throwing cancellation callback");
            callbackRegistration = ownerCts.Token.Register(() => throw callbackFailure);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, writer.ReadyCount);

            // NON-VACUITY: the assignment is INELIGIBLE (its execution faulted), so no readiness
            // write exists and the shared claim is genuinely free for the cancel fallback.
            Assert.Null(GetRetainedReadinessWrite(service));
            Assert.Equal(0, GetReadyClaimState(GetOwnerReadyClaim(service)));

            writer.FailNextReadyWrite = injected;
            reader.Push(MatchingCancel(TaskA));

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The FALLBACK write is the cancel handler's OWN, not a settled readiness write.
            Assert.False(
                loop.IsCompleted,
                "The cancel handler must still be inside its single fallback Ready write.");
            writer.ReleaseReady(0);

            var propagated = await Assert.ThrowsAnyAsync<Exception>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.IsNotType<TimeoutException>(propagated);
            Assert.Same(injected, propagated);
            Assert.Equal(1, writer.ReadyCount);

            // Everything still cleaned up, and the deferred callback evidence survives in sanitized
            // form rather than being discarded.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.True(connection.IsRetired);
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
            var diagnostics = stdErr.ToString();
            Assert.Contains("Task cancellation cleanup failed", diagnostics, StringComparison.Ordinal);
            Assert.Contains(nameof(ReadinessSentinelException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(callbackFailure.Message, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(injected.Message, diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
            callbackRegistration.Dispose();
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("loop", loop));
        }
    }

    /// <summary>
    /// A READER FAULT REMAINS PRIMARY even though a readiness write is also outstanding and fails:
    /// the reader exception surfaces UNCHANGED, the fault-driven drain JOINS the outstanding
    /// readiness write before clearing and retiring, and the readiness failure is never promoted
    /// over the reader primary.
    /// <para>
    /// THE JOIN PROOF. The readiness write is HELD across the reader fault; before releasing it the
    /// test asserts the loop is still incomplete, the owner is installed and the connection is not
    /// retired, and the write's own release callback re-records those facts on the joiner's stack. A
    /// drain that skipped the readiness join would already be finished — the loop would have
    /// propagated the reader fault while the write was still in flight.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReaderFaultWithFailingReadiness_ReaderPrimarySurvivesAndEverythingIsJoined()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new FaultingResponseReader();
        var service = BuildService(runner);

        var original = new ReadinessReaderPrimaryException("reader fault");
        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            // The readiness write is armed to FAIL before the loop can possibly start it.
            writer.FailNextReadyWrite = new ReadinessWritePrimaryException("injected readiness failure");

            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            var owner = GetActiveAssignment(service);

            var ownerCts = GetOwnerCts(service);

            // THE LAST-INSTANT CAPTURE, recorded on the joiner's own stack while the write is alive.
            object? ownerAtWriteRelease = null;
            var loopAliveAtWriteRelease = false;
            var retiredAtWriteRelease = true;
            writer.OnReadyReleasing = _ =>
            {
                ownerAtWriteRelease = GetActiveAssignment(service);
                loopAliveAtWriteRelease = !loop.IsCompleted;
                retiredAtWriteRelease = connection.IsRetired;
            };

            runner.Release(TaskA);

            // The loop settles readiness and its write ENTERS — and is HELD across the fault below.
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessWrite = CaptureReadinessWrite(
                service, "The loop must have started and retained the assignment's readiness write.");
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Remove cancellation-dispatch scheduling from the proof: once the assignment source is
            // already cancelled, teardown's CancelAsync and both terminal original-task joins are
            // synchronous, so its FIRST incomplete await can only be this held readiness write.
            await ownerCts.CancelAsync();

            // EndToolResponses is the first operation in the reader-fault finally. A registered wait
            // therefore gives a positive production rendezvous after the fault entered teardown;
            // from that point correct code runs synchronously to the held-write await, while a
            // no-join mutant clears/retires and completes before yielding.
            var responseLifetime = connection.RegisterToolResponse("reader-fault-drain-rendezvous");
            reader.ArmFault(original);
            var responseClosure = await Record.ExceptionAsync(
                () => responseLifetime.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.NotNull(responseClosure);
            Assert.IsNotType<TimeoutException>(responseClosure);

            // PRE-RELEASE, with the write still HELD and the loop POSITIVELY inside its teardown
            // drain: the fault-driven teardown may not have
            // finished, cleared ownership or retired the connection.
            Assert.False(readinessWrite.IsCompleted, "The readiness write must still be held.");
            Assert.False(
                loop.IsCompleted,
                "The reader fault must not complete the loop while the readiness write is held.");
            Assert.Same(owner, GetActiveAssignment(service));
            Assert.False(connection.IsRetired, "Retirement must follow the readiness-write join.");

            writer.ReleaseReady(0);

            var propagated = await Assert.ThrowsAsync<ReadinessReaderPrimaryException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(original, propagated);

            // THE JOIN PROOF: the capture saw the exact original owner installed, the loop running
            // and the connection unretired, all while the failing write was still alive.
            Assert.Same(owner, ownerAtWriteRelease);
            Assert.True(
                loopAliveAtWriteRelease,
                "The fault-driven drain must still have been running while the readiness write was alive.");
            Assert.False(
                retiredAtWriteRelease,
                "The connection must not have been retired while the readiness write was still alive.");

            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(execution.IsCompleted, "The execution must have been joined by the drain.");
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.True(connection.IsRetired);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            writer.OnReadyReleasing = null;
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// A THROWING CANCELLATION CALLBACK PROPAGATES AFTER CLEANUP when nothing else failed, even
    /// though a readiness write was also outstanding and failed: the joins and the disposal still
    /// happen, the readiness failure is merely REPORTED through the existing sanitized drain
    /// diagnostic, and the callback evidence is the authoritative outcome.
    /// </summary>
    [Fact]
    public async Task Teardown_ThrowingCallback_PropagatesAfterCleanupDespiteFailedReadiness()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        CancellationTokenRegistration callbackRegistration = default;
        CancellationTokenRegistration drainEnteredRegistration = default;
        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            Console.SetError(stdErr);

            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            var owner = GetActiveAssignment(service);

            var ownerCts = GetOwnerCts(service);
            var callbackFailure = new ReadinessSentinelException("throwing callback");
            callbackRegistration = ownerCts.Token.Register(() => throw callbackFailure);
            var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drainEnteredRegistration = ownerCts.Token.Register(() => drainEntered.TrySetResult());

            // THE LAST-INSTANT CAPTURE, recorded on the joiner's own stack while the write is alive.
            object? ownerAtWriteRelease = null;
            var loopAliveAtWriteRelease = false;
            var retiredAtWriteRelease = true;
            writer.OnReadyReleasing = _ =>
            {
                ownerAtWriteRelease = GetActiveAssignment(service);
                loopAliveAtWriteRelease = !loop.IsCompleted;
                retiredAtWriteRelease = connection.IsRetired;
            };

            writer.FailNextReadyWrite = new ReadinessWritePrimaryException("injected readiness failure");
            runner.Release(TaskA);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessWrite = CaptureReadinessWrite(
                service, "The loop must have started and retained the assignment's readiness write.");

            // EOF drives the TEARDOWN drain, which is what requests cancellation on the assignment's
            // own source — the source the throwing callback is registered on. The loop's own token is
            // never cancelled here, so the callback's exception cannot land on this test's thread: it
            // is CAPTURED by the drain and surfaced through the loop as the deferred failure.
            reader.TryComplete();
            await drainEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // PRE-RELEASE, with the write still HELD and teardown POSITIVELY inside its drain:
            // teardown may not finish or clear ownership while the readiness write it started is
            // outstanding — not even when it is already carrying a deferred cancellation-callback
            // failure. The connection, however, is ALREADY RETIRED: the stream-loss path retires it
            // FIRST, before the drain, by design.
            Assert.False(readinessWrite.IsCompleted, "The readiness write must still be held.");
            Assert.False(
                loop.IsCompleted,
                "Teardown must not finish while the readiness write it started is held.");
            Assert.Same(owner, GetActiveAssignment(service));
            Assert.True(
                connection.IsRetired,
                "The stream-loss path retires the connection BEFORE the drain, by design.");

            writer.ReleaseReady(0);

            var surfaced = await Assert.ThrowsAnyAsync<Exception>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.IsNotType<TimeoutException>(surfaced);
            Assert.Contains(callbackFailure, Flatten(surfaced));

            // THE JOIN PROOF: the capture saw the exact original owner installed, the loop running
            // and the connection unretired, all while the failing write was still alive.
            Assert.Same(owner, ownerAtWriteRelease);
            Assert.True(
                loopAliveAtWriteRelease,
                "Teardown must still have been running while the readiness write it started was alive.");
            Assert.True(
                retiredAtWriteRelease,
                "The stream-loss path retires the connection BEFORE the drain, by design.");

            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.True(connection.IsRetired);
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);

            // With NO prior primary the callback evidence PROPAGATES; it is never downgraded to a
            // report. The failed readiness write is merely OBSERVED — reported through the existing
            // sanitized drain diagnostic, never as the outcome and never with its raw message.
            var diagnostics = stdErr.ToString();
            Assert.DoesNotContain("Task cancellation cleanup failed", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Task drain observed a fault", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(callbackFailure.Message, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("injected readiness failure", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
            writer.OnReadyReleasing = null;
            drainEnteredRegistration.Dispose();
            callbackRegistration.Dispose();
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("readiness write", readinessWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// SEQUENTIAL CONNECTION ISOLATION: A's readiness write lands on A's OWN writer and B's on B's,
    /// even though both loops share one service instance and one ownership slot; a retired
    /// connection can never receive the successor's readiness.
    /// </summary>
    [Fact]
    public async Task SequentialLoops_EachReadinessWriteTargetsItsOwnConnection()
    {
        var runner = new GatedRunner();
        var service = BuildService(runner);

        var writerA = new GatedWriter();
        var readerA = new ChannelResponseReader();
        var writerB = new GatedWriter();
        var readerB = new ChannelResponseReader();

        var loopA = Task.CompletedTask;
        var loopB = Task.CompletedTask;
        try
        {
            var connectionA = TestConnectionFactory.Attach(
                service, "worker-1", BuildStream(writerA, readerA), service.TestProvisioner);
            loopA = InvokeProcessMessages(service, connectionA, TestContext.Current.CancellationToken);

            readerA.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writerA.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            TrackReadinessWrite(GetRetainedReadinessWrite(service));
            writerA.ReleaseReady(0);
            await writerA.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            readerA.TryComplete();
            await loopA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(connectionA.IsRetired);
            Assert.Equal(1, writerA.ReadyCount);
            Assert.Equal(0, writerB.ReadyCount);

            var connectionB = TestConnectionFactory.Attach(
                service, "worker-1", BuildStream(writerB, readerB), service.TestProvisioner);
            loopB = InvokeProcessMessages(service, connectionB, TestContext.Current.CancellationToken);

            readerB.Push(ResultAssignment(TaskB));
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskB);
            await writerB.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            TrackReadinessWrite(GetRetainedReadinessWrite(service));
            writerB.ReleaseReady(0);
            await writerB.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            readerB.TryComplete();
            await loopB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(1, writerA.ReadyCount);
            Assert.Equal(1, writerB.ReadyCount);
            Assert.True(connectionB.IsRetired);
        }
        finally
        {
            runner.ReleaseAll();
            writerA.ReleaseAll();
            writerB.ReleaseAll();
            readerA.TryComplete();
            readerB.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop A", loopA), ("loop B", loopB));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Harness
    // ══════════════════════════════════════════════════════════════════════════

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

    private static OrchestratorMessage ResultAssignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-1",
            GoalDescription = "desc",
            Prompt = "prompt",
            Role = GrpcWorkerRole.Coder,
            Model = "test-model",
        },
    };

    private static OrchestratorMessage Probe(string requestId) => new()
    {
        ToolResponse = new ToolCallResponse { RequestId = requestId, Success = true, ResultJson = "{}" },
    };

    private static OrchestratorMessage MatchingCancel(string taskId) => new()
    {
        Cancel = new CancelTask { TaskId = taskId, Reason = "matching" },
    };

    private static OrchestratorMessage ReceiptAck(string taskId, string workerId) => new()
    {
        CompletionReceiptAck = new CompletionReceiptAck { TaskId = taskId, WorkerId = workerId },
    };

    /// <summary>A provisioner whose provisioning call always fails, WITHOUT a result ever existing.</summary>
    private static WorkerConfigProvisioner FailingProvisioner() => new(
        "worker-1",
        (_, _) => Task.FromException<GetWorkerConfigResponse>(
            new InvalidOperationException("injected provisioning failure")),
        _ => null,
        (_, _) => { });

    private static WorkerService BuildService(IAgentRunner runner)
    {
        var service = new WorkerService("http://localhost:9999", "worker-1", ["coder"]);

        var field = typeof(WorkerService).GetField(
            "_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);

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
        WorkerService service, WorkerConnection connection, CancellationToken ct)
    {
        var method = typeof(WorkerService).GetMethod(
            "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, [connection, ct])!;
    }

    /// <summary>
    /// Invokes the exact ownership transition the assignment handler awaits before resetting and
    /// installing a successor. Used only after A was installed through the actual message loop.
    /// </summary>
    private static Task InvokeReplacementDrain(WorkerService service)
    {
        var method = typeof(WorkerService).GetMethod(
            "DrainRetainedForReplacementAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, null)!;
    }

    /// <summary>The exact assignment drain/clear transition shared by EOF and reader-fault teardown.</summary>
    private static Task InvokeTeardownDrain(WorkerService service)
    {
        var method = typeof(WorkerService).GetMethod(
            "DrainRetainedForTeardownAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, null)!;
    }

    private static object? GetActiveAssignment(WorkerService service) =>
        typeof(WorkerService).GetField("_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    private static string GetActiveTaskId(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (string)active.GetType().GetProperty("TaskId")!.GetValue(active)!;
    }

    private static Task GetActiveExecution(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (Task)active.GetType().GetProperty("Execution")!.GetValue(active)!;
    }

    private static Task GetActiveReporting(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (Task)active.GetType().GetProperty("Reporting")!.GetValue(active)!;
    }

    private static object GetOwnerOrdinaryReady(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return active.GetType().GetProperty("OrdinaryReady")!.GetValue(active)!;
    }

    private static Task? GetRetainedReadinessWrite(WorkerService service)
    {
        var active = GetActiveAssignment(service);
        if (active is null)
            return null;
        var slot = active.GetType().GetProperty("OrdinaryReady")!.GetValue(active)!;
        return (Task?)slot.GetType().GetProperty("Write")!.GetValue(slot);
    }

    // ── Readiness-write registry (owner-slot independent) ─────────────────────
    //
    // The retained readiness write is reachable through the ownership slot ONLY while that slot is
    // occupied. A join-removal mutant clears the slot early, which would make the write invisible
    // to teardown exactly when it is most likely to be orphaned. Every capture is therefore ALSO
    // recorded here, in fixture-owned state that no production transition can clear, and teardown
    // joins this registry rather than re-reading the slot.

    /// <summary>Every readiness write this test ever observed, independent of the ownership slot.</summary>
    private readonly List<Task> _startedReadinessWrites = [];

    /// <summary>
    /// CAPTURES the ACTUAL retained readiness write for the currently retained assignment and
    /// records it in the owner-slot-independent registry, so teardown can still join it after any
    /// ownership clear. Fails by name when no write has been started.
    /// </summary>
    private Task CaptureReadinessWrite(WorkerService service, string because)
    {
        var write = GetRetainedReadinessWrite(service)
            ?? throw new Xunit.Sdk.XunitException(because);

        TrackReadinessWrite(write);
        return write;
    }

    /// <summary>Records a readiness write in the registry. Idempotent by reference.</summary>
    private void TrackReadinessWrite(Task? write)
    {
        if (write is null)
            return;

        lock (_startedReadinessWrites)
        {
            foreach (var tracked in _startedReadinessWrites)
            {
                if (ReferenceEquals(tracked, write))
                    return;
            }

            _startedReadinessWrites.Add(write);
        }
    }

    /// <summary>
    /// Awaits a PRODUCTION-VISIBLE rendezvous and converts a non-arrival into a NAMED, diagnostic
    /// failure instead of a bare <see cref="TimeoutException"/>.
    /// </summary>
    /// <remarks>
    /// A rendezvous that never arrives is the signature of a missing production step, not of a slow
    /// machine: the fixture supplies every gate the correct path needs, so the only way the signal
    /// can fail to appear is that production never reached the point that emits it. Reporting that
    /// as the stated regression keeps a mutant's failure self-diagnosing rather than anonymous.
    /// </remarks>
    private static async Task AwaitRendezvousAsync(Task rendezvous, string because)
    {
        try
        {
            await rendezvous.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(because);
        }
    }

    /// <summary>A snapshot of every recorded readiness write, oldest first.</summary>
    private IReadOnlyList<Task> TrackedReadinessWrites
    {
        get { lock (_startedReadinessWrites) return [.. _startedReadinessWrites]; }
    }

    private static bool IsOrdinaryReadySettled(object slot) =>
        (bool)slot.GetType().GetProperty("IsSettled")!.GetValue(slot)!;

    /// <summary>
    /// Invokes the slot's OWN production <c>Arm</c> — the identical call the loop makes to obtain
    /// its readiness observation — so a test can inspect what a loop iteration would see.
    /// </summary>
    private static Task ArmOrdinaryReady(object slot) =>
        (Task)slot.GetType().GetMethod("Arm")!.Invoke(slot, null)!;

    /// <summary>
    /// Invokes the slot's OWN production <c>PublishEligibility</c> — the identical call reporting
    /// makes at the old ordinary-Ready point — so a test can publish at a controlled instant while
    /// the loop is provably parked elsewhere.
    /// </summary>
    private static void PublishOrdinaryReadyEligibility(object slot) =>
        slot.GetType().GetMethod("PublishEligibility")!.Invoke(slot, null);

    /// <summary>
    /// Invokes the production settlement for a slot and returns the ONE retained write (or
    /// <c>null</c> when the assignment is not eligible / the claim was already taken). This is the
    /// identical entry point the loop and every ownership transition use.
    /// </summary>
    private static Task? InvokeSettleOrdinaryReady(WorkerService service, object slot) =>
        (Task?)typeof(WorkerService)
            .GetMethod("SettleOrdinaryReady", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [slot]);

    private static object GetOwnerReceipt(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return active.GetType().GetProperty("Receipt")!.GetValue(active)!;
    }

    private static bool GetReceiptArmed(object receipt) =>
        (bool)receipt.GetType().GetProperty("IsArmed")!.GetValue(receipt)!;

    private static bool GetReceiptConfirmed(object receipt) =>
        (bool)receipt.GetType().GetProperty("IsConfirmed")!.GetValue(receipt)!;

    private static object GetOwnerReadyClaim(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return active.GetType().GetProperty("Ready")!.GetValue(active)!;
    }

    private static int GetReadyClaimState(object readyClaim) =>
        (int)readyClaim.GetType().GetField("_claimed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(readyClaim)!;

    private static CancellationTokenSource GetOwnerCts(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (CancellationTokenSource)active.GetType().GetProperty("Cts")!.GetValue(active)!;
    }

    private static TaskResult? GetRetainedResult(WorkerService service)
    {
        var active = GetActiveAssignment(service);
        if (active is null)
            return null;
        var holder = active.GetType().GetProperty("TerminalResult")!.GetValue(active)!;
        return (TaskResult?)holder.GetType().GetProperty("Result")!.GetValue(holder);
    }

    private static int GetSlotOccupancy(WorkerService service) =>
        GetActiveAssignment(service) is null ? 0 : 1;

    private static IReadOnlyList<Exception> Flatten(Exception exception) =>
        exception is AggregateException aggregate
            ? [.. aggregate.Flatten().InnerExceptions]
            : [exception];

    /// <summary>
    /// JOINS every started task under its own bounded wait and reports the NAMES of any that were
    /// still live, so an abandoned producer is a loud, named teardown failure rather than a silent
    /// return. A faulted or cancelled producer is quiescent, which is all teardown requires.
    /// </summary>
    /// <remarks>
    /// THREE SOURCES, NONE OF THEM THE OWNER SLOT ALONE. The explicitly listed producers (every
    /// loop and every assignment task the test started), the registry of readiness writes this
    /// fixture captured — which survives an ownership clear, so a join-removal mutant that cleared
    /// the slot early cannot hide an orphaned write — and finally whatever the slot still holds.
    /// </remarks>
    private async Task JoinAllForTeardownAsync(
        WorkerService service, params (string Name, Task? Producer)[] producers)
    {
        List<string> live = [];
        foreach (var (name, producer) in producers)
            await JoinOneAsync(name, producer);

        // THE OWNER-SLOT-INDEPENDENT READINESS WRITES. Captured by the test as they were started,
        // so they are still discoverable after any ownership clear.
        var trackedWrites = TrackedReadinessWrites;
        for (var index = 0; index < trackedWrites.Count; index++)
            await JoinOneAsync($"readiness write #{index}", trackedWrites[index]);

        // The retained assignment's own tasks, so a failure path can never leave one behind.
        if (GetActiveAssignment(service) is { } active)
        {
            foreach (var property in new[] { "Execution", "Reporting" })
                await JoinOneAsync($"active assignment {property}", (Task?)active.GetType().GetProperty(property)!.GetValue(active));

            var slot = active.GetType().GetProperty("OrdinaryReady")!.GetValue(active)!;
            await JoinOneAsync(
                "active assignment readiness write",
                (Task?)slot.GetType().GetProperty("Write")!.GetValue(slot));
        }

        if (live.Count > 0)
            throw new Xunit.Sdk.XunitException(
                $"Teardown left still-live producers: {string.Join(", ", live)}.");

        async Task JoinOneAsync(string name, Task? producer)
        {
            if (producer is null)
                return;

            try
            {
                await producer.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }
            catch (Exception)
            {
                // Quiescent: the real outcome is asserted on the test's normal path.
            }

            if (!producer.IsCompleted)
                live.Add(name);
        }
    }

    // ── Doubles ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A gated request writer. EVERY <c>Ready</c> write parks until the test releases it BY INDEX,
    /// which is what makes a HELD readiness write a deterministic, observable milestone; a
    /// <c>Complete</c> write is recorded and released immediately unless
    /// <see cref="HoldCompletes"/> is set. All observables are gate/TCS-based: no sleeps, no
    /// polling. The cancellable <c>WriteAsync</c> overload is implemented EXPLICITLY, because the
    /// default interface member throws for the live stream token the worker writes with.
    /// </summary>
    private sealed class GatedWriter : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _completes = [];
        private readonly List<WorkerMessage> _readies = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyCountWaiters = [];
        private bool _releaseImmediately;
        private Exception? _failNextReadyWrite;

        /// <summary>When set, <c>Complete</c> writes park until released by index like <c>Ready</c>.</summary>
        internal bool HoldCompletes { get; init; }

        internal IReadOnlyList<WorkerMessage> Completes
        {
            get { lock (_gate) return [.. _completes]; }
        }

        internal int ReadyCount { get { lock (_gate) return _readies.Count; } }

        internal Exception? FailNextReadyWrite
        {
            get { lock (_gate) return _failNextReadyWrite; }
            set { lock (_gate) _failNextReadyWrite = value; }
        }

        /// <summary>
        /// THE LAST-INSTANT CAPTURE HOOK. Invoked with the Ready index AFTER that write's gate
        /// opened but BEFORE the write returns — i.e. on the PRODUCTION call stack of whoever is
        /// awaiting it, at the last instant the write is provably still alive.
        /// </summary>
        /// <remarks>
        /// This is what makes a JOIN removal-proof and schedule-independent: a transition that did
        /// NOT join this write has already disposed, cleared and (for teardown) retired by the time
        /// the write completes, so a capture taken here records that broken state. A post-hoc
        /// assertion could not distinguish the two.
        /// </remarks>
        internal Action<int>? OnReadyReleasing { get; set; }

        /// <summary>
        /// THE ENTRY CAPTURE HOOK. Invoked with the Ready index at write ENTRY, before the write
        /// parks — used to record what had already happened at the instant the readiness write
        /// actually started (for example whether a racing inbound message had been dispatched).
        /// </summary>
        internal Action<int>? OnReadyEntering { get; set; }

        /// <summary>
        /// THE CANCELLATION CAPTURE HOOK. Invoked with the Ready index when a PARKED write observes
        /// its FORWARDED token as cancelled, on that cancellation's own stack — again the last
        /// instant the write is provably alive. It never fires when the fixture releases the write
        /// normally, so it is positive evidence that the cancellable overload's token reached the
        /// parked write.
        /// </summary>
        internal Action<int>? OnReadyCancelled { get; set; }

        /// <summary>
        /// OPTIONAL UNWIND HOLD for a CANCELLED write: when set, a write whose forwarded token
        /// cancelled parks here before rethrowing, so a test can observe that the cancelled write is
        /// still ALIVE and prove its joiner is waiting on it. Always released by
        /// <see cref="ReleaseAll"/>, so it can never strand a producer.
        /// </summary>
        internal TaskCompletionSource? ReadyCancellationUnwind { get; set; }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message) => WriteCoreAsync(message, CancellationToken.None);

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken ct)
            => WriteCoreAsync(message, ct);

        private async Task WriteCoreAsync(WorkerMessage message, CancellationToken ct)
        {
            if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Complete)
            {
                int index;
                TaskCompletionSource entered;
                TaskCompletionSource? release;
                lock (_gate)
                {
                    index = _completes.Count;
                    _completes.Add(message);
                    entered = Slot(_completeEntered, index);
                    release = HoldCompletes ? Slot(_completeRelease, index) : null;
                    if (_releaseImmediately && release is not null)
                        release.TrySetResult();
                }

                entered.TrySetResult();
                if (release is not null)
                    await release.Task.WaitAsync(ct);

                return;
            }

            if (message.PayloadCase != WorkerMessage.PayloadOneofCase.Ready)
            {
                // Tool requests and other messages are not gated by this fixture.
                return;
            }

            int readyIndex;
            TaskCompletionSource readyEntered;
            TaskCompletionSource readyRelease;
            Exception? failure;
            List<(int Threshold, TaskCompletionSource Waiter)> satisfied = [];
            lock (_gate)
            {
                readyIndex = _readies.Count;
                _readies.Add(message);
                readyEntered = Slot(_readyEntered, readyIndex);
                readyRelease = Slot(_readyRelease, readyIndex);
                if (_releaseImmediately)
                    readyRelease.TrySetResult();

                failure = _failNextReadyWrite;
                _failNextReadyWrite = null;

                foreach (var (threshold, waiter) in _readyCountWaiters)
                {
                    if (_readies.Count >= threshold)
                        satisfied.Add((threshold, waiter));
                }

                foreach (var (threshold, _) in satisfied)
                    _readyCountWaiters.Remove(threshold);
            }

            readyEntered.TrySetResult();
            foreach (var (_, waiter) in satisfied)
                waiter.TrySetResult();

            OnReadyEntering?.Invoke(readyIndex);

            try
            {
                await readyRelease.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // THE FORWARDED TOKEN CANCELLED A PARKED WRITE. Recorded on the cancellation's own
                // stack, at the last instant this write is provably alive, then rethrown verbatim —
                // exactly how a real gRPC writer unwinds a cancelled in-flight write. An optional
                // unwind hold keeps the write alive so a test can observe its joiner waiting.
                OnReadyCancelled?.Invoke(readyIndex);

                if (ReadyCancellationUnwind is { } unwind)
                    await unwind.Task;

                throw;
            }

            // Recorded on the releasing caller's stack, BEFORE this write returns.
            OnReadyReleasing?.Invoke(readyIndex);

            // The ATTEMPT is recorded above BEFORE the injected failure applies, so a test can prove
            // the write really was issued and is never retried.
            if (failure is not null)
                throw failure;
        }

        internal Task CompleteEntered(int index)
        {
            lock (_gate)
                return _completes.Count > index ? Task.CompletedTask : Slot(_completeEntered, index).Task;
        }

        internal Task ReadyEntered(int index)
        {
            lock (_gate)
                return _readies.Count > index ? Task.CompletedTask : Slot(_readyEntered, index).Task;
        }

        internal Task WaitForReadyCountAsync(int count, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_readies.Count >= count)
                    return Task.CompletedTask;
                if (!_readyCountWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _readyCountWaiters[count] = waiter;
                }

                return waiter.Task.WaitAsync(Failsafe, ct);
            }
        }

        internal void ReleaseComplete(int index)
        {
            lock (_gate) Slot(_completeRelease, index).TrySetResult();
        }

        internal void ReleaseReady(int index)
        {
            lock (_gate) Slot(_readyRelease, index).TrySetResult();
        }

        internal void ReleaseAll()
        {
            lock (_gate)
            {
                // TEARDOWN MODE: writes that arrive after this sweep are released at entry, so a
                // producer that was still queued on the production send gate cannot park here.
                _releaseImmediately = true;
                foreach (var source in _completeRelease.Values) source.TrySetResult();
                foreach (var source in _readyRelease.Values) source.TrySetResult();
            }

            // A cancelled write parked in its unwind hold is released too, so teardown can never
            // leave one alive.
            ReadyCancellationUnwind?.TrySetResult();
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

        public Task CompleteAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// A channel-backed reader for driving the REAL loop. <c>Consumed</c> is a READ/DEQUEUE
    /// milestone — it says a message left the channel, NOT that its handler ran — so tests that need
    /// handler evidence use an in-handler rendezvous instead (see the matching-cancel test).
    /// <see cref="ReadsStarted"/> is the complementary fact: production re-arms its ONE pending read
    /// only AFTER a handler returns, so a read count that has NOT advanced is positive evidence that
    /// the loop is still inside the handler it last entered.
    /// </summary>
    private sealed class ChannelResponseReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly Channel<OrchestratorMessage> _channel =
            Channel.CreateUnbounded<OrchestratorMessage>();

        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _consumedWaiters = [];
        private readonly Dictionary<int, TaskCompletionSource> _readStartedWaiters = [];
        private int _consumed;
        private int _readsStarted;

        public OrchestratorMessage Current { get; private set; } = null!;

        /// <summary>
        /// How many reads production has STARTED. It advances once per <c>MoveNext</c> entry, and
        /// production arms exactly one pending read per dispatched message, so this is the
        /// loop's "I have left the previous handler" counter.
        /// </summary>
        internal int ReadsStarted { get { lock (_gate) return _readsStarted; } }

        /// <summary>
        /// ONE-SHOT ordering hook, awaited INSIDE <c>MoveNext</c> at entry — i.e. on the loop's own
        /// stack, in the window between production re-arming its pending read and arming its
        /// readiness observation. A test uses it to make BOTH arms of that race complete before the
        /// race is evaluated, which is what turns the read-first/losing-signal ordering into a
        /// deterministic observation instead of a schedule.
        /// </summary>
        internal Func<Task>? BeforeNextRead { get; set; }

        internal void Push(OrchestratorMessage message) => _channel.Writer.TryWrite(message);

        internal void TryComplete() => _channel.Writer.TryComplete();

        internal Task Consumed(int count)
        {
            lock (_gate)
            {
                if (_consumed >= count)
                    return Task.CompletedTask;
                if (!_consumedWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _consumedWaiters[count] = waiter;
                }

                return waiter.Task;
            }
        }

        /// <summary>Completes once production has STARTED at least <paramref name="count"/> reads.</summary>
        internal Task ReadStarted(int count)
        {
            lock (_gate)
            {
                if (_readsStarted >= count)
                    return Task.CompletedTask;
                if (!_readStartedWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _readStartedWaiters[count] = waiter;
                }

                return waiter.Task;
            }
        }

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            List<TaskCompletionSource> readReady = [];
            Func<Task>? before;
            lock (_gate)
            {
                _readsStarted++;
                before = BeforeNextRead;
                BeforeNextRead = null;
                foreach (var (threshold, waiter) in _readStartedWaiters)
                {
                    if (_readsStarted >= threshold)
                        readReady.Add(waiter);
                }
            }

            foreach (var waiter in readReady)
                waiter.TrySetResult();

            if (before is not null)
                await before();

            if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
                return false;

            if (!_channel.Reader.TryRead(out var message))
                return false;

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
    /// A channel-backed reader that behaves like <see cref="ChannelResponseReader"/> until
    /// <see cref="ArmFault"/> is called, then throws the ORIGINAL exception from the next
    /// <c>MoveNext</c> — modelling a reader fault whose identity the loop must propagate.
    /// </summary>
    private sealed class FaultingResponseReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly Channel<OrchestratorMessage> _channel =
            Channel.CreateUnbounded<OrchestratorMessage>();

        private Exception? _fault;

        public OrchestratorMessage Current { get; private set; } = null!;

        internal void Push(OrchestratorMessage message) => _channel.Writer.TryWrite(message);

        internal void TryComplete() => _channel.Writer.TryComplete();

        /// <summary>
        /// One-shot: the next <c>MoveNext</c> outcome surfaces <paramref name="fault"/>. The channel
        /// is also completed, so a loop ALREADY parked inside <c>WaitToReadAsync</c> wakes
        /// deterministically and reaches the fault check.
        /// </summary>
        internal void ArmFault(Exception fault)
        {
            _fault = fault;
            _channel.Writer.TryComplete();
        }

        private Exception? TakeFault()
        {
            var fault = _fault;
            _fault = null;
            return fault;
        }

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (TakeFault() is { } preFault)
                throw preFault;

            if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                if (TakeFault() is { } postFault)
                    throw postFault;

                return false;
            }

            if (!_channel.Reader.TryRead(out var message))
                return false;

            Current = message;
            return true;
        }
    }

    /// <summary>
    /// A runner whose prompt parks until the test releases it, recording per-task cancellation and
    /// the MAXIMUM CONCURRENT PROMPT count (the no-runner-overlap invariant) without polling.
    /// </summary>
    private sealed class GatedRunner : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private readonly HashSet<string> _cancelled = [];
        private readonly HashSet<string> _startedIds = [];
        private int _concurrentPrompts;
        private int _maxConcurrentPrompts;
        private int _resetCount;
        private string? _taskId;
        private bool _teardown;

        internal int MaxConcurrentPrompts => Volatile.Read(ref _maxConcurrentPrompts);

        /// <summary>How many session resets production has ENTERED. Distinguishes A's reset from B's.</summary>
        internal int ResetCount => Volatile.Read(ref _resetCount);

        /// <summary>
        /// THE IN-HANDLER RENDEZVOUS HOOK. Invoked from INSIDE <c>ResetSessionAsync</c> — the FIRST
        /// production step after the replacement drain — on the assignment handler's own stack, so a
        /// test can record what had already happened at that exact production instant.
        /// </summary>
        internal Action<string?>? OnResetEntered { get; set; }

        internal bool WasCancelled(string taskId)
        {
            lock (_gate) return _cancelled.Contains(taskId);
        }

        /// <summary>Whether a prompt for <paramref name="taskId"/> has ever ENTERED (not merely been gated).</summary>
        internal bool HasPromptStarted(string taskId)
        {
            lock (_gate) return _startedIds.Contains(taskId);
        }

        internal Task PromptStarted(string taskId) => Slot(_started, taskId).Task;

        internal void Release(string taskId) => Slot(_release, taskId).TrySetResult();

        internal void ReleaseAll()
        {
            lock (_gate)
            {
                // TEARDOWN LATCH: gates created from now on are completed AT CREATION, so a producer
                // that starts after the sweep can never park on a gate nobody will release.
                _teardown = true;
                foreach (var source in _release.Values) source.TrySetResult();
            }
        }

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var id = _taskId ?? "(unknown)";
            lock (_gate) _startedIds.Add(id);
            Slot(_started, id).TrySetResult();

            var concurrent = Interlocked.Increment(ref _concurrentPrompts);
            InterlockedMax(ref _maxConcurrentPrompts, concurrent);
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
                Interlocked.Decrement(ref _concurrentPrompts);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)))
            {
                if (Interlocked.CompareExchange(ref target, value, current) == current)
                    return;
            }
        }

        private TaskCompletionSource Slot(Dictionary<string, TaskCompletionSource> map, string key)
        {
            lock (_gate)
            {
                if (!map.TryGetValue(key, out var source))
                {
                    source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (_teardown)
                        source.TrySetResult();
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

        /// <summary>
        /// The session reset — the FIRST production step of an assignment handler, and therefore
        /// the first step AFTER a replacement drain. The hook fires from inside it, on the handler's
        /// own stack, which is what makes the replacement ordering observable positively.
        /// </summary>
        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _resetCount);
            OnResetEntered?.Invoke(model);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A partially degraded <c>Console.Error</c>: lines containing <c>marker</c> throw while
    /// everything else is forwarded, so ONE diagnostic can be broken while the rest stay observable.
    /// </summary>
    private sealed class MarkerThrowingErrorWriter(
        string marker, System.IO.TextWriter inner, Exception failure) : System.IO.TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (value is not null && value.Contains(marker, StringComparison.Ordinal))
                throw failure;

            inner.WriteLine(value);
        }

        public override void Write(string? value)
        {
            if (value is not null && value.Contains(marker, StringComparison.Ordinal))
                throw failure;

            inner.Write(value);
        }

        public override void Write(char value) => inner.Write(value);
    }

    private sealed class ReadinessReaderPrimaryException(string message) : Exception(message);

    private sealed class ReadinessWritePrimaryException(string message) : Exception(message);

    private sealed class ReadinessSentinelException(string message) : Exception(message);
}
