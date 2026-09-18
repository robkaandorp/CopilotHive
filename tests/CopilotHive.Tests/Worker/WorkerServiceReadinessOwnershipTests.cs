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
        try
        {
            reader.Push(Assignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // From here on NOTHING is pushed: the response stream is completely quiet.
            runner.Release(TaskA);

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
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
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
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
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            var readinessWrite = GetRetainedReadinessWrite(service)
                ?? throw new Xunit.Sdk.XunitException(
                    "The loop must have started and retained the assignment's readiness write.");

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
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
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
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
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
        try
        {
            reader.Push(Assignment(TaskA));
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
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
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
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
    /// REPLACEMENT: two sequential assignments each produce EXACTLY ONE Ready, the successor starts
    /// only after ALL of the predecessor's owned tasks — including its readiness write — were joined,
    /// and the runner never has two prompts in flight.
    /// </summary>
    [Fact]
    public async Task Replacement_EachAssignmentExactlyOneReady_NoRunnerOverlap()
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
            writer.ReleaseReady(0);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            reader.Push(ResultAssignment(TaskB));
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskB);
            await writer.ReadyEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
        }
    }

    /// <summary>
    /// A MATCHING CANCEL THAT SETTLES READINESS BEFORE THE LOOP EVER REACHED ITS READINESS WAIT must
    /// still yield EXACTLY ONE Ready: the ownership transition settles the eligibility ITSELF and
    /// joins the write it starts, and the cancel FALLBACK cannot duplicate it because the shared
    /// claim was consumed by that same settlement.
    /// <para>
    /// THE SCHEDULE IS FORCED BY PRODUCTION ORDERING, not by luck. The Complete write is HELD, so the
    /// report has not published eligibility when the cancel is delivered; the loop therefore reads
    /// the cancel and parks inside the drain's reporting join BEFORE any readiness observation could
    /// happen. Releasing the Complete write terminates the report, which lets the drain — not the
    /// loop — settle readiness. A version that lost the eligibility writes ZERO Ready; one that also
    /// let the fallback write produces TWO. Both fail by name.
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
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.False(reporting.IsCompleted, "The report must still be inside its held Complete write.");
            Assert.False(IsOrdinaryReadySettled(GetOwnerOrdinaryReady(service)));
            Assert.Equal(0, writer.ReadyCount);

            // Deliver the matching cancel: the loop's handler parks inside the drain's reporting join.
            reader.Push(MatchingCancel(TaskA));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Still nothing settled, and the loop has NOT settled it (it never re-entered its
            // readiness wait): the drain is the only participant that can.
            Assert.False(reporting.IsCompleted);
            Assert.Equal(0, writer.ReadyCount);

            // Releasing the Complete write lets the report terminate, which is exactly what lets the
            // DRAIN settle readiness and start/join the single write.
            writer.ReleaseComplete(0);

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            writer.ReleaseReady(0);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

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
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
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
    /// EOF WITH A LIVE TOKEN still permits the draining assignment's single Ready: the loop exits,
    /// the TEARDOWN drain settles and joins the readiness write, and only THEN is the connection
    /// retired — so exactly one Ready lands on a still-usable stream and the write is never
    /// abandoned.
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
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // Finish the work and reach EOF in the same breath, so the loop's exit and the report's
            // termination race: the TEARDOWN drain is what settles and joins the write.
            runner.Release(TaskA);
            reader.TryComplete();

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            writer.ReleaseReady(0);

            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(1, writer.ReadyCount);
            Assert.Single(writer.Completes);
            Assert.True(connection.IsRetired, "Retirement must follow the join of the readiness write.");
            Assert.Equal(0, GetSlotOccupancy(service));
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
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
        try
        {
            Console.SetError(stdErr);
            writer.FailNextReadyWrite = new ReadinessWritePrimaryException("injected readiness failure");

            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            writer.ReleaseReady(0);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

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
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
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
        Task? execution = null;
        Task? reporting = null;
        try
        {
            // A DEGRADED execution diagnostic makes the execution FAULT, so nothing is published and
            // the claim stays free for the cancel fallback.
            Console.SetError(new MarkerThrowingErrorWriter(
                "Task execution failed", stdErr,
                new ReadinessSentinelException("degraded execution diagnostic")));

            var connection = TestConnectionFactory.Attach(
                service, "worker-1", BuildStream(writer, reader), service.TestProvisioner);
            var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

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

            writer.FailNextReadyWrite = injected;
            reader.Push(MatchingCancel(TaskA));

            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
                ("assignment execution", execution), ("assignment reporting", reporting));
        }
    }

    /// <summary>
    /// A READER FAULT REMAINS PRIMARY even though a readiness write is also outstanding and fails:
    /// the reader exception surfaces UNCHANGED, every owned task is joined, and the connection
    /// retires — so a readiness failure can never replace a reader primary.
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
        try
        {
            // The readiness write is armed to FAIL before the loop can possibly start it.
            writer.FailNextReadyWrite = new ReadinessWritePrimaryException("injected readiness failure");

            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            runner.Release(TaskA);

            // The loop settles readiness and its write enters — then fails once released.
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            writer.ReleaseReady(0);

            // NOW the reader faults: a REAL loop primary, with the failed readiness write behind it.
            reader.ArmFault(original);

            var propagated = await Assert.ThrowsAsync<ReadinessReaderPrimaryException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(original, propagated);

            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(execution.IsCompleted, "The execution must have been joined by the drain.");
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.True(connection.IsRetired);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
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
        Task? execution = null;
        Task? reporting = null;
        try
        {
            Console.SetError(stdErr);

            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            var ownerCts = GetOwnerCts(service);
            var callbackFailure = new ReadinessSentinelException("throwing callback");
            callbackRegistration = ownerCts.Token.Register(() => throw callbackFailure);

            writer.FailNextReadyWrite = new ReadinessWritePrimaryException("injected readiness failure");
            runner.Release(TaskA);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            writer.ReleaseReady(0);

            // EOF drives the TEARDOWN drain, which is what requests cancellation on the assignment's
            // own source — the source the throwing callback is registered on. The loop's own token is
            // never cancelled here, so the callback's exception cannot land on this test's thread: it
            // is CAPTURED by the drain and surfaced through the loop as the deferred failure.
            reader.TryComplete();

            var surfaced = await Assert.ThrowsAnyAsync<Exception>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.IsNotType<TimeoutException>(surfaced);
            Assert.Contains(callbackFailure, Flatten(surfaced));

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
            callbackRegistration.Dispose();
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
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

    private static bool IsOrdinaryReadySettled(object slot) =>
        (bool)slot.GetType().GetProperty("IsSettled")!.GetValue(slot)!;

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
    private static async Task JoinAllForTeardownAsync(
        WorkerService service, params (string Name, Task? Producer)[] producers)
    {
        List<string> live = [];
        foreach (var (name, producer) in producers)
        {
            if (producer is null)
                continue;

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

        // The retained assignment's own tasks, so a failure path can never leave one behind.
        if (GetActiveAssignment(service) is { } active)
        {
            foreach (var property in new[] { "Execution", "Reporting" })
            {
                var owned = (Task?)active.GetType().GetProperty(property)!.GetValue(active);
                if (owned is null)
                    continue;

                try
                {
                    await owned.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                }
                catch (Exception)
                {
                    // Quiescent.
                }

                if (!owned.IsCompleted)
                    live.Add($"active assignment {property}");
            }
        }

        if (live.Count > 0)
            throw new Xunit.Sdk.XunitException(
                $"Teardown left still-live producers: {string.Join(", ", live)}.");
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

            await readyRelease.Task.WaitAsync(ct);

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
    /// A channel-backed reader for driving the REAL loop; <c>Consumed</c> is the deterministic
    /// barrier proving a message was fully dispatched.
    /// </summary>
    private sealed class ChannelResponseReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly Channel<OrchestratorMessage> _channel =
            Channel.CreateUnbounded<OrchestratorMessage>();

        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _consumedWaiters = [];
        private int _consumed;

        public OrchestratorMessage Current { get; private set; } = null!;

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

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
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
        private int _concurrentPrompts;
        private int _maxConcurrentPrompts;
        private string? _taskId;
        private bool _teardown;

        internal int MaxConcurrentPrompts => Volatile.Read(ref _maxConcurrentPrompts);

        internal bool WasCancelled(string taskId)
        {
            lock (_gate) return _cancelled.Contains(taskId);
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
        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
            => Task.CompletedTask;
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
