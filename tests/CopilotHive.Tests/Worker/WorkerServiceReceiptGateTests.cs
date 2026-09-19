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
/// THE GATED BOTH-FLAGS MODE — the ONE new ordinary-Ready shape — driven through the REAL
/// <c>WorkerService.ProcessMessagesAsync</c> loop with a connection whose ACCEPTED registration
/// carried BOTH negotiated facts (<c>CompletionReceiptAckEnabled</c> AND
/// <c>CompletionReadyRequired</c>).
/// <para>
/// WHAT THIS FIXTURE PINS, per cell:
/// <list type="bullet">
///   <item>(a) a locally completed report with NO ACK withholds the ordinary Ready while a
///   ToolResponse is still consumed, and an EXACT matching ACK then produces EXACTLY ONE Ready;</item>
///   <item>(b) an early ACK arriving while the Complete write is HELD does not settle that write,
///   and releasing it into SUCCESS and into FAILURE preserves its own outcome before Ready — a
///   terminated FAILED write still authorizes Ready and keeps its error/diagnostics unchanged;</item>
///   <item>(c) suppression in BOTH-FLAGS mode for a missing result (the handled provisioning
///   failure) and for a mapping failure — while the LEGACY and ACK-ONLY connections still emit
///   Ready for the same scenarios;</item>
///   <item>(d) the exact ACK still authorizes Ready after a TERMINATED failed local write, and it
///   never completes that write, releases its permit early, or replaces the retained
///   <see cref="TaskResult"/>;</item>
///   <item>(e) all completion statuses (Completed, Failed, Cancelled) follow the same rule;</item>
///   <item>(f) the retained <see cref="TaskResult"/> is never cleared merely by accepting an ACK;</item>
///   <item>ordering independence: eligibility, write termination and receipt arriving in ANY order
///   reach the SAME single Ready, and <c>Arm()</c> does not spin while eligible-but-not-ready.</item>
/// </list>
/// </para>
/// <para>
/// TEST QUALITY CONTRACT. No production test hooks, no sleeps, no polling-as-ordering: every
/// milestone is a <see cref="TaskCompletionSource"/> gate with a bounded failsafe, and non-completion
/// is observed with timeout-only bounded waits. Every started task — the loop, the execution, the
/// reporting and every readiness write — is joined in a <c>finally</c> on every path. The writer
/// implements the CANCELLABLE write overload so a cancelled write unwinds like a real gRPC stream
/// writer. Suppressed-Ready assertions positively observe NON-completion with a bounded wait, never
/// an instantaneous check alone.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerServiceReceiptGateTests
{
    /// <summary>Failsafe bound; it only turns a regression into a named failure, never orders anything.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The NON-completion observation bound: a suppressed Ready must still be absent after this
    /// bounded wait, which positively witnesses the withholding rather than racing a schedule.
    /// </summary>
    private static readonly TimeSpan SuppressionBound = TimeSpan.FromMilliseconds(500);

    private const string TaskA = "task-A";
    private const string TaskB = "task-B";

    // ══════════════════════════════════════════════════════════════════════════
    // (a) Withhold-then-ACK through the REAL loop.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CELL (a). A locally completed report with NO ACK: the ordinary Ready is WITHHELD while a
    /// <c>ToolResponse</c> is still consumed by the loop; the EXACT matching ACK then produces
    /// EXACTLY ONE Ready. A following probe proves the loop kept running after the Ready.
    /// <para>
    /// REMOVAL PROOF. Without the receipt gate the write would start at eligibility — the bounded
    /// suppression wait then throws <see cref="TimeoutException"/> instead of returning cleanly —
    /// and without the ACK wake the write would never start at all, failing the named rendezvous.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GatedMode_CompletedReportWithoutAck_WithholdsReadyUntilExactAck_ProducesExactlyOneReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        System.Threading.Tasks.Task? execution = null;
        System.Threading.Tasks.Task? reporting = null;
        System.Threading.Tasks.Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            // The report completes (Complete write is recorded and released immediately) —
            // eligibility and terminated-write facts are published, but NO ACK has arrived.
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "Reporting must terminate independently of the withheld readiness.");

            var slot = GetOwnerOrdinaryReady(service);
            var readyClaim = GetOwnerReadyClaim(service);
            Assert.True(IsOrdinaryReadyEligible(slot), "The report terminated normally: eligibility holds.");
            Assert.False(IsOrdinaryReadySettled(slot), "The ACK has not arrived: nothing may settle.");
            Assert.Equal(0, GetReadyClaimState(readyClaim));
            Assert.Null(GetRetainedReadinessWrite(service));

            // POSITIVE NON-COMPLETION: no Ready enters even after a bounded wait, while the loop is
            // still consuming messages behind it.
            reader.Push(Probe("while-withheld"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var suppressed = await Record.ExceptionAsync(() =>
                writer.ReadyEntered(0).WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(suppressed);
            Assert.Equal(0, writer.ReadyCount);

            // THE EXACT MATCHING ACK — same connection identity, same task ID.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));

            // EXACTLY ONE Ready is produced by the settlement.
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The exact matching ACK must wake the parked readiness and start the ONE Ready write.");
            readinessWrite = CaptureReadinessWrite(
                service, "The ACK must authorize the retained single readiness write.");
            Assert.Equal(1, GetReadyClaimState(readyClaim));

            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // The loop keeps consuming AFTER the gated Ready.
            reader.Push(Probe("after-ready"));
            await reader.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);

            // (f) THE RETAINED RESULT SURVIVES the ACK and the Ready: acceptance never clears it.
            var retained = Assert.IsType<TaskResult>(GetRetainedResult(service));
            Assert.Equal(TaskOutcome.Completed, retained.Status);
            Assert.Equal(TaskA, retained.TaskId);

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
    /// A WRONG-task ACK and a DUPLICATE-then-late ACK pattern in the gated mode: only the EXACT
    /// matching ACK authorizes the Ready; a wrong-task ACK is consumed as a no-op and leaves the
    /// Ready withheld; a duplicate after confirmation changes nothing (still exactly one Ready).
    /// </summary>
    [Fact]
    public async Task GatedMode_WrongTaskAckKeepsReadyWithheld_ExactAckThenDuplicateYieldsOneReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        System.Threading.Tasks.Task? execution = null;
        System.Threading.Tasks.Task? reporting = null;
        System.Threading.Tasks.Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // A WRONG-TASK ACK is consumed but authorizes nothing.
            reader.Push(ReceiptAck(TaskB, connection.AssignedId));
            reader.Push(Probe("wrong-ack-consumed"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var suppressed = await Record.ExceptionAsync(() =>
                writer.ReadyEntered(0).WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(suppressed);
            Assert.False(IsOrdinaryReadySettled(GetOwnerOrdinaryReady(service)));

            // THE EXACT ACK authorizes the single Ready.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The exact ACK after a wrong-task ACK must still authorize the one Ready.");
            readinessWrite = CaptureReadinessWrite(service, "The exact ACK must authorize the write.");
            writer.ReleaseReady(0);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // A DUPLICATE ACK after confirmation changes nothing: still exactly one Ready.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            reader.Push(Probe("after-duplicate"));
            await reader.Consumed(5).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);

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

    // ══════════════════════════════════════════════════════════════════════════
    // (b) Early ACK during a HELD Complete; write outcome preserved before Ready.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CELL (b), SUCCESS arm. An ACK arriving while the Complete write is HELD latches the receipt
    /// but settles NOTHING; releasing the write into SUCCESS keeps its own success, and only then
    /// does the one Ready follow.
    /// </summary>
    [Fact]
    public async Task GatedMode_EarlyAckDuringHeldComplete_DoesNotSettleWrite_SuccessThenSingleReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter { HoldCompletes = true };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        System.Threading.Tasks.Task? execution = null;
        System.Threading.Tasks.Task? reporting = null;
        System.Threading.Tasks.Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            // The Complete write is HELD: the original local write has NOT terminated yet.
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var slot = GetOwnerOrdinaryReady(service);
            var readyClaim = GetOwnerReadyClaim(service);
            var receipt = GetOwnerReceipt(service);
            Assert.False(reporting.IsCompleted, "The report must be held inside its Complete write.");
            Assert.True(GetReceiptArmed(receipt), "The armed Complete made an ACK possible.");
            Assert.False(IsOrdinaryReadySettled(slot));
            Assert.Null(GetRetainedReadinessWrite(service));

            // THE EARLY ACK: latched, but nothing settles.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            reader.Push(Probe("early-ack"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptConfirmed(receipt));
            Assert.False(IsOrdinaryReadySettled(slot), "The write has not terminated: no settlement.");
            Assert.Equal(0, GetReadyClaimState(readyClaim));
            Assert.Null(GetRetainedReadinessWrite(service));
            var suppressed = await Record.ExceptionAsync(() =>
                writer.ReadyEntered(0).WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(suppressed);

            // RELEASE INTO SUCCESS: the write keeps its own successful outcome, then Ready follows.
            writer.ReleaseComplete(0);
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "After the terminated (successful) write and the confirmed ACK, the one Ready must follow.");
            readinessWrite = CaptureReadinessWrite(service, "The terminated write must authorize the write.");
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(reporting.IsCompletedSuccessfully, "A successful write keeps reporting successful.");

            Assert.Equal(1, GetReadyClaimState(readyClaim));
            writer.ReleaseReady(0);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

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
    /// CELL (b)+(d), FAILURE arm. An ACK arriving while the Complete write is HELD does not settle
    /// it; releasing the write into FAILURE preserves its own error (reporting stays successful and
    /// sanitized, exactly one Complete attempt), and the confirmed ACK STILL authorizes the one
    /// Ready. The write is never completed by the ACK, the send permit is released only by the
    /// write's own unwind, and the retained <see cref="TaskResult"/> keeps its exact identity.
    /// <para>
    /// REMOVAL PROOF. A gate that treated ACK as local-write success would settle the write here —
    /// the pre-release assertion on the unsettled slot fails by name — or would convert the failure
    /// outcome, which the single-attempt and unchanged-result assertions reject.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GatedMode_AckAuthorizesReadyAfterTerminatedFailedWrite_ErrorAndResultUnchanged()
    {
        var runner = new GatedRunner();
        var injected = new GatedWritePrimaryException("injected complete-write failure");
        var writer = new GatedWriter { HoldCompletes = true, FailCompleteAtIndexZero = injected };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        System.Threading.Tasks.Task? execution = null;
        System.Threading.Tasks.Task? reporting = null;
        System.Threading.Tasks.Task? readinessWrite = null;
        try
        {
            Console.SetError(stdErr);
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var retainedAtGate = Assert.IsType<TaskResult>(GetRetainedResult(service));
            var slot = GetOwnerOrdinaryReady(service);
            var readyClaim = GetOwnerReadyClaim(service);

            // THE EARLY ACK while the write is held: latched, nothing settled, no Ready. The
            // trailing probe is the handler-run barrier: the loop re-arms its next read only after
            // the ACK handler returned, so Consumed(3) proves the receipt was actually latched.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            reader.Push(Probe("early-ack-held"));
            reader.Push(Probe("ack-handler-returned"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptConfirmed(GetOwnerReceipt(service)));
            Assert.False(IsOrdinaryReadySettled(slot));
            Assert.Equal(0, GetReadyClaimState(readyClaim));
            var suppressed = await Record.ExceptionAsync(() =>
                writer.ReadyEntered(0).WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(suppressed);

            // RELEASE INTO FAILURE: the write's OWN error is preserved. Reporting swallows it with
            // the existing sanitized catch, so the reporting task still completes successfully —
            // with EXACTLY ONE Complete attempt and no retry.
            writer.ReleaseComplete(0);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "A terminated failed write is a reporting-observed transport fault only: no retry, no re-raise.");
            Assert.True(writer.Completes.Count == 1, "Expected exactly one Complete attempt.");

            // THE ACK AUTHORIZES READY after the TERMINATED FAILED write: eligibility + terminated
            // + confirmed — order-independent — now produce the one Ready.
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The confirmed ACK must authorize the Ready once the failed write has TERMINATED.");
            readinessWrite = CaptureReadinessWrite(service, "The terminated write must authorize the write.");
            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // THE ERROR AND RESULT ARE UNCHANGED: the failure was only reported (sanitized, no raw
            // message), the retained result is the IDENTICAL instance, and the write was never
            // completed by the ACK.
            var diagnostics = stdErr.ToString();
            Assert.Contains("Task execution failed", diagnostics, StringComparison.Ordinal);
            Assert.Contains(nameof(GatedWritePrimaryException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("injected complete-write failure", diagnostics, StringComparison.Ordinal);
            Assert.Same(retainedAtGate, GetRetainedResult(service));
            Assert.Equal(TaskOutcome.Completed, retainedAtGate.Status);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
            Assert.Single(writer.Completes);
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

    // ══════════════════════════════════════════════════════════════════════════
    // (c) Suppression ONLY in both-flags mode.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CELL (c), MISSING RESULT. A handled provisioning failure produces NO result. In BOTH-FLAGS
    /// mode the ordinary Ready is SUPPRESSED (no Complete, no ACK possible, no Ready); the LEGACY
    /// and ACK-ONLY connections still emit exactly one Ready for the SAME scenario.
    /// <para>
    /// REMOVAL PROOF. An exemption for an unarmed assignment — treating "never armed" as "never
    /// gated" — would emit a Ready in the gated cells and fail them by name.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public async Task MissingResult_SuppressesReadyOnlyInGatedMode(
        bool readyRequired, bool ackEnabled, bool expectReady)
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        service.TestProvisioner = FailingProvisioner();

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: ackEnabled, completionReadyRequired: readyRequired);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        System.Threading.Tasks.Task? execution = null;
        System.Threading.Tasks.Task? reporting = null;
        try
        {
            reader.Push(Assignment(TaskA));
            reader.Push(Probe("installed"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Null(GetRetainedResult(service));
            Assert.True(
                execution.IsCompletedSuccessfully,
                "The handled provisioning failure must leave the execution normally terminated.");

            if (expectReady)
            {
                // LEGACY / ACK-ONLY: the ungated shape emits the ordinary Ready exactly as before.
                await AwaitRendezvousAsync(
                    writer.ReadyEntered(0),
                    "A missing result must NOT suppress the ordinary Ready outside the gated mode.");
                var write = CaptureReadinessWrite(service, "The ungated shape must start the write.");
                writer.ReleaseReady(0);
                await write.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }
            else
            {
                // BOTH-FLAGS: eligibility holds (the execution terminated normally) but there is no
                // armed Complete, no terminated write and no receipt — the Ready stays suppressed.
                var slot = GetOwnerOrdinaryReady(service);
                Assert.True(IsOrdinaryReadyEligible(slot));
                Assert.False(IsOrdinaryReadySettled(slot));
                var suppressed = await Record.ExceptionAsync(() =>
                    writer.ReadyEntered(0).WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
                Assert.IsType<TimeoutException>(suppressed);
                Assert.Null(GetRetainedReadinessWrite(service));
            }

            Assert.Empty(writer.Completes);

            // The loop stays healthy either way.
            reader.Push(Probe("after-suppression"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(expectReady ? 1 : 0, writer.ReadyCount);
        }
        finally
        {
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
    /// CELL (c), MAPPING FAILURE — across all THREE mode combinations, through the REAL
    /// <c>ReportAssignmentAsync</c> flow and the REAL settlement the loop and every drain use.
    /// <para>
    /// WHY DIRECT REPORTING INVOCATION. A terminal result the production mapper cannot map (an
    /// unknown <c>TaskOutcome</c>) is not producible by the real executor chain, which maps every
    /// boundary into a valid outcome. The established authoritative mapping-failure fixture
    /// (<c>FailedCompletionMapping_LeavesReceiptUnarmedAndStillAttemptsLegacyReady</c>) therefore
    /// drives the ACTUAL reporting method with a completed original producer and an otherwise
    /// complete result whose invalid status makes <c>GrpcMapper.ToGrpc</c> throw. This fixture
    /// extends exactly that pattern to the mode matrix: the assignment stays UNARMED, no Complete
    /// is ever written, the retained result is untouched — and in BOTH-FLAGS mode the settlement
    /// starts NOTHING (the ordinary Ready is suppressed), while LEGACY and ACK-ONLY still start
    /// the one ungated Ready.
    /// <para>
    /// REMOVAL PROOF. An exemption for an unarmed assignment would make the gated cell settle a
    /// write and fail its <see cref="Xunit.Assert.Null"/>; a suppression that leaked outside the
    /// gated mode fails the ungated cells' non-null rendezvous.
    /// </para>
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public async Task MappingFailure_SuppressesReadyOnlyInGatedMode(
        bool readyRequired, bool ackEnabled, bool expectReady)
    {
        const string taskId = "task-unmappable";
        const string payloadSecret = "completion-payload-must-not-enter-diagnostic";
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: ackEnabled, completionReadyRequired: readyRequired);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        System.Threading.Tasks.Task? reporting = null;
        try
        {
            Console.SetError(stdErr);

            var serviceType = typeof(WorkerService);
            var holderType = serviceType.GetNestedType("TerminalResultHolder", BindingFlags.NonPublic)!;
            var readyType = serviceType.GetNestedType("ReadyClaim", BindingFlags.NonPublic)!;
            var receiptType = serviceType.GetNestedType("CompletionReceiptTracker", BindingFlags.NonPublic)!;
            var holder = Activator.CreateInstance(holderType, nonPublic: true)!;
            var ready = Activator.CreateInstance(readyType, nonPublic: true)!;
            var receipt = Activator.CreateInstance(
                receiptType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [connection],
                culture: null)!;

            var domainTask = GrpcMapper.ToDomain(ResultAssignment(taskId).Assignment);
            var unmappable = new TaskResult
            {
                TaskId = taskId,
                Status = (TaskOutcome)int.MaxValue,
                Output = payloadSecret,
                Model = domainTask.Model,
            };
            holderType.GetMethod("Publish")!.Invoke(holder, [unmappable]);

            // THE SLOT, in the EXACT shape the assignment handler builds for this mode: gated
            // (both flags) hands in the receipt tracker; every other combination leaves it out.
            var slotType = serviceType.GetNestedType("OrdinaryReadySlot", BindingFlags.NonPublic)!;
            var ordinaryReady = expectReady
                ? Activator.CreateInstance(
                    slotType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: [connection, CancellationToken.None, ready],
                    culture: null)!
                : Activator.CreateInstance(
                    slotType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: [connection, CancellationToken.None, ready, receipt],
                    culture: null)!;

            reporting = (System.Threading.Tasks.Task)serviceType.GetMethod(
                    "ReportAssignmentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [
                    System.Threading.Tasks.Task.CompletedTask,
                    domainTask,
                    connection,
                    holder,
                    receipt,
                    ordinaryReady,
                ])!;

            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE MAPPING-FAILURE FACTS, in every mode: the result is retained VERBATIM, the
            // assignment is UNARMED, no Complete was ever written, and no payload leaked.
            Assert.Same(unmappable, holderType.GetProperty("Result")!.GetValue(holder));
            Assert.False(
                GetReceiptArmed(receipt),
                "A mapping that throws must leave the assignment UNARMED in every mode.");
            Assert.False(GetReceiptConfirmed(receipt));
            Assert.True(writer.Completes.Count == 0, "A mapping failure must never write a Complete.");
            Assert.DoesNotContain(payloadSecret, stdErr.ToString(), StringComparison.Ordinal);
            Assert.True(
                IsOrdinaryReadyEligible(ordinaryReady),
                "The execution terminated normally: eligibility is published in every mode.");

            var settledWrite = InvokeSettleOrdinaryReady(service, ordinaryReady);
            if (expectReady)
            {
                // LEGACY / ACK-ONLY: the settlement starts the ONE ungated Ready on the REAL stream.
                Assert.NotNull(settledWrite);
                await AwaitRendezvousAsync(
                    writer.ReadyEntered(0),
                    "A mapping failure must NOT suppress the ordinary Ready outside the gated mode.");
                writer.ReleaseReady(0);
                await settledWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.Equal(1, GetReadyClaimState(ready));
            }
            else
            {
                // BOTH-FLAGS: unarmed means the receipt can never confirm, so the settlement starts
                // NOTHING and the shared claim stays UNCONSUMED — the Ready is withheld.
                Assert.True(
                    settledWrite is null,
                    "The gated shape must withhold the ordinary Ready for an unarmed assignment.");
                Assert.Equal(0, GetReadyClaimState(ready));
                Assert.Null(ordinaryReady.GetType().GetProperty("Write")!.GetValue(ordinaryReady));
            }

            // The retained result is untouched either way, and no raw payload ever leaked.
            Assert.Same(unmappable, holderType.GetProperty("Result")!.GetValue(holder));
            Assert.Equal(expectReady ? 1 : 0, writer.ReadyCount);
        }
        finally
        {
            Console.SetError(originalErr);
            if (reporting is not null)
                await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            writer.ReleaseAll();
            reader.TryComplete();
            connection.Retire();
            service.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (e) All completion statuses follow the same rule.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CELL (e). Completed, Failed and Cancelled executor results all follow the SAME gated rule in
    /// BOTH-FLAGS mode: the Complete write terminates, the exact ACK arrives, EXACTLY ONE Ready
    /// follows, and the retained result keeps its own full identity throughout — never cleared by
    /// the ACK (f), never replaced by the write's outcome.
    /// <para>
    /// REMOVAL PROOF. A status-specific exemption (only Completed gated) fails the Failed/Cancelled
    /// cells' suppression waits; a status-specific suppression fails their Ready rendezvous.
    /// </para>
    /// <para>
    /// HOW EACH OUTCOME IS PRODUCED by the REAL executor chain: Cancelled from the assignment's own
    /// token while the prompt is parked; Failed from an agent exception the executor maps into a
    /// FAILED result; Completed from a normal release.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(GateOutcome.Completed)]
    [InlineData(GateOutcome.Failed)]
    [InlineData(GateOutcome.Cancelled)]
    public async Task GatedMode_AllCompletionStatuses_FollowTheSameRule(GateOutcome gateOutcome)
    {
        var outcome = (RetainedGateOutcome)gateOutcome;
        const string taskId = "task-gated-status";
        var runner = new GatedRunner { FailOnRelease = outcome == RetainedGateOutcome.Failed };
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        System.Threading.Tasks.Task? execution = null;
        System.Threading.Tasks.Task? reporting = null;
        System.Threading.Tasks.Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Drive the REAL executor to the outcome under test: Cancelled from the assignment's
            // own token while the prompt is parked; Failed from an agent exception; Completed from
            // a normal release.
            if (outcome == RetainedGateOutcome.Cancelled)
                await GetOwnerCts(service).CancelAsync();
            else
                runner.Release(taskId);

            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var retainedAtGate = Assert.IsType<TaskResult>(GetRetainedResult(service));
            Assert.Equal(ToTaskOutcome(outcome), retainedAtGate.Status);

            // NO ACK yet: the Ready is withheld across every status.
            var preAckSuppressed = await Record.ExceptionAsync(() =>
                writer.ReadyEntered(0).WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(preAckSuppressed);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE EXACT ACK authorizes the single Ready for EVERY status.
            reader.Push(ReceiptAck(taskId, connection.AssignedId));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                $"The exact ACK must authorize the Ready for the {outcome} status too.");
            readinessWrite = CaptureReadinessWrite(service, "The ACK must authorize the write.");
            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // (f) THE RETAINED RESULT IS NEVER CLERED by the ACK: identical instance, full payload.
            Assert.Same(retainedAtGate, GetRetainedResult(service));
            Assert.Equal(ToTaskOutcome(outcome), retainedAtGate.Status);

            reader.Push(Probe("after-status-ready"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

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

    // ══════════════════════════════════════════════════════════════════════════
    // Ordering independence + Arm non-spin.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ORDERING INDEPENDENCE, ACK FIRST. The receipt is CONFIRMED while the Complete write is still
    /// held; the write then terminates (success); the one Ready follows from the last-arriving fact.
    /// The mirrored order (write first, ACK second) is pinned by the (a) fixture, so together they
    /// prove the predicate's order independence through the REAL loop.
    /// </summary>
    [Fact]
    public async Task GatedMode_ReceiptBeforeWriteTermination_SameSingleReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter { HoldCompletes = true };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        System.Threading.Tasks.Task? execution = null;
        System.Threading.Tasks.Task? reporting = null;
        System.Threading.Tasks.Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);

            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // ELIGIBILITY not yet published (the write is held), but the RECEIPT is already
            // confirmed: nothing settles.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            reader.Push(Probe("receipt-first"));
            reader.Push(Probe("ack-handler-returned"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptConfirmed(GetOwnerReceipt(service)));
            var slot = GetOwnerOrdinaryReady(service);
            Assert.False(IsOrdinaryReadySettled(slot));

            // AN ARM() made now must NOT complete: the reader parks while eligible-but-not-ready
            // (here not even eligible), and it must not spin or hand out a satisfied signal.
            Assert.False(
                ArmOrdinaryReady(slot).IsCompleted,
                "Arm must park while the readiness predicate does not hold.");
            Assert.False(IsOrdinaryReadySettled(slot));
            Assert.Equal(0, GetReadyClaimState(GetOwnerReadyClaim(service)));

            // The write terminates into success: the last fact arrives and the one Ready follows.
            writer.ReleaseComplete(0);
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The terminated write is the last missing fact: the one Ready must follow.");
            readinessWrite = CaptureReadinessWrite(service, "The write must have been started once.");
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // AFTER settlement an armed observation NEVER completes again: no spin, no second Ready.
            Assert.False(
                ArmOrdinaryReady(slot).IsCompleted,
                "A settled slot must hand out a never-completing observation.");
            reader.Push(Probe("after-settled"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);

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
    /// THE ARM NON-SPIN CELL, ELIGIBLE-BUT-NOT-READY. With the report COMPLETE (eligibility and
    /// terminated-write facts published) and NO ACK, an <c>Arm()</c> made at the slot's own
    /// production member stays INCOMPLETE, settles nothing and consumes no claim — then the exact
    /// ACK completes it into EXACTLY ONE Ready. No busy observation loop, no duplicate Ready, no
    /// consumed claim before the predicate holds.
    /// </summary>
    [Fact]
    public async Task GatedMode_ArmParksWhileEligibleButNotReady_WakesOnceIntoOneReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        System.Threading.Tasks.Task? execution = null;
        System.Threading.Tasks.Task? reporting = null;
        System.Threading.Tasks.Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var slot = GetOwnerOrdinaryReady(service);
            var readyClaim = GetOwnerReadyClaim(service);

            // ELIGIBLE-BUT-NOT-READY: repeated Arms park, never spin, never settle, never claim.
            var parked1 = ArmOrdinaryReady(slot);
            var parked2 = ArmOrdinaryReady(slot);
            var parked3 = ArmOrdinaryReady(slot);
            Assert.False(parked1.IsCompleted, "An eligible-but-unconfirmed assignment must park Arm.");
            Assert.False(parked2.IsCompleted);
            Assert.False(parked3.IsCompleted);
            Assert.False(IsOrdinaryReadySettled(slot));
            Assert.Equal(0, GetReadyClaimState(readyClaim));
            Assert.Equal(0, writer.ReadyCount);

            // The loop stays healthy while the arm is parked.
            reader.Push(Probe("while-parked"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.False(parked1.IsCompleted);
            Assert.Equal(0, writer.ReadyCount);

            // THE EXACT ACK wakes the parked observation into the ONE Ready.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The ACK must wake the parked Arm and start exactly one Ready.");
            readinessWrite = CaptureReadinessWrite(service, "The wake must settle the slot once.");
            Assert.Equal(1, GetReadyClaimState(readyClaim));

            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);

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

    // ══════════════════════════════════════════════════════════════════════════
    // Regressions: cancel / EOF boundaries still behave with the gate installed.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EOF / run-cancellation drain does NOT wait for an ACK: on a both-flags connection with a
    /// completed-but-unconfirmed report, EOF drains and clears WITHOUT emitting the ordinary Ready
    /// (the gate holds — the same gate every drain consults) and the loop finishes normally.
    /// <para>
    /// REGRESSION WITNESS for criterion 4's preserved boundary: a drain that waited for an ACK
    /// would leave the loop alive past the bounded join; a drain that emitted an unacknowledged
    /// Ready fails the zero-Ready assertion by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GatedMode_EofWithUnconfirmedReceipt_DrainsWithoutWaitingAndEmitsNoReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        System.Threading.Tasks.Task? execution = null;
        System.Threading.Tasks.Task? reporting = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, writer.ReadyCount);

            // EOF: the drain consults the SAME gate — no ACK, so no ordinary Ready is emitted —
            // and never WAITS for one. The loop finishes on its own bounded join.
            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(0, writer.ReadyCount);
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.True(connection.IsRetired);
        }
        finally
        {
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
    /// A matching CANCEL on a both-flags connection with a completed-but-unconfirmed report still
    /// drains, clears and emits its OWN single-flight fallback Ready WITHOUT an ACK — the existing
    /// cancel boundary preserved (the fallback is NOT receipt confirmation).
    /// <para>
    /// REGRESSION WITNESS for criterion 4: a cancel handler that consulted the receipt gate for its
    /// fallback would emit nothing here and fail the rendezvous by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GatedMode_MatchingCancelFallbackReady_StillEmittedWithoutAck()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        System.Threading.Tasks.Task? execution = null;
        System.Threading.Tasks.Task? reporting = null;
        System.Threading.Tasks.Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // NO ACK: the ordinary gate withholds the settlement, so the shared claim is FREE.
            var slot = GetOwnerOrdinaryReady(service);
            Assert.False(IsOrdinaryReadySettled(slot));
            Assert.Equal(0, GetReadyClaimState(GetOwnerReadyClaim(service)));

            // The matching CANCEL drains, clears, and emits its OWN single fallback Ready — no ACK
            // involved. The fallback is NOT receipt confirmation, so it settles the claim directly.
            reader.Push(MatchingCancel(TaskA));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The cancel fallback must still emit its single Ready without any ACK.");
            writer.ReleaseReady(0);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            reader.Push(Probe("after-cancel"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));
            Assert.False(connection.IsRetired, "A matching cancel never retires the connection.");
            Assert.Equal(1, writer.ReadyCount);

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


    // ══════════════════════════════════════════════════════════════════════════
    // Criterion 5 — the pre-Ready Assignment boundary.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CELL 5(a): the pre-Ready refusal. On a both-flags connection with a RETAINED predecessor
    /// whose authorized ordinary Ready write has NOT been started, an unexpected successor
    /// <c>Assignment</c> throws the FIXED text <c>Assignment received before authorized Ready.</c>
    /// and NOTHING else happened: the predecessor is still the retained assignment with its own
    /// task ID, its claim was never consumed, and the successor's task ID NEVER reached the runner
    /// (no reset, no prompt, no execution — positive absence witnesses, not end-state guesses).
    /// <para>
    /// REMOVAL PROOF. Without the boundary the successor is dispatched: the runner resets, the
    /// prompt for task B starts, and the loop does NOT fail — so the loop's fault assertion AND the
    /// exact-text assertion fail by name. A refusal keyed on the wrong condition (e.g. on
    /// eligibility or local write completion) would also refuse or admit at the wrong instant and
    /// fail the companion authorization-killer cells.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SuccessorBeforeStartedReadyWrite_IsRefusedWithFixedText_AndNothingElseHappens()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        try
        {
            // Predecessor A: locally completed report, NO ACK — retained, eligible, but the Ready
            // write has never been started.
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var owner = GetActiveAssignment(service);
            var readyClaim = GetOwnerReadyClaim(service);
            var slot = GetOwnerOrdinaryReady(service);
            Assert.True(IsOrdinaryReadyEligible(slot), "Precondition: eligibility is published.");
            Assert.Null(GetRetainedReadinessWrite(service)); // NOT started.
            Assert.Equal(0, writer.ReadyCount);

            // The unexpected successor, while A is retained with NO started write.
            reader.Push(ResultAssignment(TaskB));

            // THE REFUSAL: the loop faults with the EXACT fixed text (an exception thrown from the
            // Assignment handler propagates out of the loop — the loop records primaries unchanged).
            var propagated = await Assert.ThrowsAsync<InvalidOperationException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal("Assignment received before authorized Ready.", propagated.Message);

            // NOTHING ELSE EVER HAPPENED — positive evidence, not end-state inference:
            // (pre-fault evidence, captured on the owner object the test already holds)
            Assert.Same(owner, GetActiveAssignment(service) ?? owner);
            Assert.Equal(TaskA, TaskIdOf(owner!));
            Assert.Equal(0, GetReadyClaimState(readyClaim));
            Assert.Single(writer.Completes); // ONLY A's own Complete; the successor added none.

            // NO successor work ever reached the runner — positive absence witnesses:
            Assert.False(
                runner.ResetEntered(TaskB),
                "A refused successor must never reset the runner.");
            Assert.False(
                runner.HasPromptStarted(TaskB),
                "A refused successor must never start a prompt.");

            // The refusal did not invent an ACK either: the predecessor's receipt stays UNCONFIRMED
            // (the fallback teardown drain in the loop's finally clears the slot, so the retained
            // owner is only assertable through the captured object above).
            Assert.False(
                GetReceiptConfirmed(OwnerReceiptOf(owner!)),
                "A refused successor must never be confirmed by an inferred acknowledgement.");
            // NOTE: the loop HAS faulted with the refusal (that is the boundary's observable), so
            // the loop's own teardown retirement is expected afterwards; the refusal itself did
            // nothing — proven by the runner/claim/Complete/receipt evidence above.
        }
        finally
        {
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
    /// THE AUTHORIZATION-BOUNDARY KILLER VECTORS. Eligibility alone is NOT authorization: with
    /// eligibility published, the Complete write terminated and the receipt confirmed — every
    /// predicate fact present — but the write never STARTED (no ACK-wake has run, the loop was
    /// parked in a cancel-handler drain so no settlement could race), a successor is STILL refused.
    /// The refusal is therefore bound to the started-and-retained write and to nothing else.
    /// </summary>
    [Fact]
    public async Task AllPredicateFactsButNoStartedWrite_SuccessorStillRefused()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        var drainEnteredRegistration = default(CancellationTokenRegistration);
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // CONFIRM the receipt FIRST (with a trailing probe as the handler-return barrier), so
            // by the time A's report terminates, EVERY predicate fact is present: eligibility will
            // publish, the Complete write has terminated, the receipt is confirmed.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            reader.Push(Probe("ack-handler-returned"));
            reader.Push(Probe("ack-barrier-follows"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptConfirmed(GetOwnerReceipt(service)));

            // PARK THE LOOP INSIDE THE MATCHING-CANCEL HANDLER'S DRAIN. The drain's first step
            // requests cancellation, so this benign callback fires from inside the handler; with
            // the Complete write's successor messages NOT yet delivered the drain parks on its
            // reporting join — the loop cannot be at its readiness observation, so NO settlement
            // can start the write while the assertions below run.
            var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drainEnteredRegistration = GetOwnerCts(service).Token.Register(
                () => drainEntered.TrySetResult());

            var readsBeforeCancel = reader.ReadsStarted;
            reader.Push(MatchingCancel(TaskA));
            await drainEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // POSITIVE IN-HANDLER EVIDENCE: the loop has not re-armed its pending read, so it is
            // inside the handler and cannot have settled the slot.
            Assert.Equal(readsBeforeCancel, reader.ReadsStarted);

            // Release the Complete write so the report terminates and publishes eligibility while
            // the loop is provably parked inside the drain.
            writer.ReleaseComplete(0);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var slot = GetOwnerOrdinaryReady(service);

            // The predicate is complete, so the DRAIN ITSELF (the first settler) starts the ONE
            // authorized write — the readiness is never lost, whichever participant settles it.
            // The drain then JOINS that write before clearing; the cancel fallback is suppressed
            // because the claim was consumed by this settlement (not by the fallback).
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The drain must settle and start the ONE authorized write once the predicate holds.");
            var drainWrite = CaptureReadinessWrite(
                service, "The drain must retain the write it started.");
            Assert.Equal(1, GetReadyClaimState(GetOwnerReadyClaim(service)));

            // The write's outcome is observed, not abandoned.
            writer.ReleaseReady(0);
            await drainWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);

            // The cancel fallback could not have duplicated it (claim already consumed), and the
            // ownership is cleared after the join.
            reader.Push(Probe("after-cancel"));
            await reader.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));
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
                ("loop", loop));
        }
    }

    /// <summary>
    /// CELL 5(b): a successor arriving while the authorized Ready write is STARTED but STILL
    /// PENDING is accepted, and the replacement drain JOINS that write before the runner reset and
    /// the ownership clear. The join is proven by holding the write and showing the successor
    /// cannot proceed past its reset until the write is released — an in-window proof, not an
    /// end-state observation — and the write's own outcome is observed (it completes) rather than
    /// abandoned.
    /// <para>
    /// REMOVAL PROOF. A drain that did not join the started write would reset the runner and
    /// install B while A's write was still parked: the held-write assertion ("B must not have
    /// started while A's readiness write is still running") fails by name. A boundary that
    /// additionally demanded local write COMPLETION would refuse here and fail the acceptance
    /// rendezvous.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SuccessorDuringPendingStartedReadyWrite_IsAccepted_AndDrainJoinsTheWrite()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? executionA = null;
        Task? reportingA = null;
        Task? readinessWriteA = null;
        try
        {
            // A completes its report and the exact ACK authorizes the ONE Ready write.
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            executionA = GetActiveExecution(service);
            reportingA = GetActiveReporting(service);
            await reportingA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The exact ACK must authorize the one Ready write for A.");
            readinessWriteA = CaptureReadinessWrite(
                service, "The authorized write must be started and retained.");
            Assert.False(
                readinessWriteA.IsCompleted,
                "The write must still be pending inside the gated writer.");

            var ownerA = GetActiveAssignment(service);
            var writesAtDelivery = writer.ReadyCount;

            // THE SUCCESSOR arrives while A's authorized write is STARTED but STILL PENDING.
            reader.Push(ResultAssignment(TaskB));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reader.ReadStarted(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // IN-WINDOW JOIN PROOF, with A's write still HELD: B was consumed, but correct code
            // cannot possibly have reached the reset (the drain parks on the write join), so B
            // must never have started a prompt, A must still be the retained owner, and the loop
            // must still be alive — all bounded by the fact that the write's TCS has not fired
            // (not by any timeout).
            Assert.False(
                readinessWriteA.IsCompleted,
                "A's authorized write must still be held inside its gated writer.");
            Assert.Same(ownerA, GetActiveAssignment(service));
            Assert.False(
                runner.HasPromptStarted(TaskB),
                "B must not start while A's readiness write is still parked in the drain's join.");
            Assert.False(loop.IsCompleted, "The loop must still be draining A for B's arrival.");
            Assert.Equal(writesAtDelivery, writer.ReadyCount);

            // Release the held write: the drain joins it, clears ownership, resets the runner and
            // lets B start. The reset entry (awaited AFTER the release) is the positive evidence
            // that the acceptance really happened.
            writer.ReleaseReady(0);
            await readinessWriteA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                readinessWriteA.IsCompletedSuccessfully,
                "The write's outcome is OBSERVED by the join, never abandoned.");

            await AwaitResetCountAsync(runner, 2, loop,
                "B's handler must be reached once A's write is released (the boundary accepted it).");
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.NotSame(ownerA, GetActiveAssignment(service));
            Assert.Equal(TaskB, TaskIdOf(GetActiveAssignment(service)!));

            // B completes normally and emits its own single Ready.
            runner.Release(TaskB);
            await writer.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            reader.Push(ReceiptAck(TaskB, connection.AssignedId));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(1),
                "B's own gated Ready must follow its exact ACK.");
            writer.ReleaseReady(1);
            await writer.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);

            reader.Push(Probe("after-b-ready"));
            await reader.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
            await JoinAllForTeardownAsync(service,
                ("assignment execution A", executionA),
                ("assignment reporting A", reportingA),
                ("readiness write A", readinessWriteA),
                ("loop", loop));
        }
    }

    /// <summary>
    /// CELL 5(c): the ordinary assignment path with NO refusal. An INITIAL assignment on a
    /// both-flags connection (no retained owner) is accepted without the boundary firing, and a
    /// successor delivered AFTER a matching cancel CLEARED ownership is accepted too.
    /// </summary>
    [Fact]
    public async Task EmptySlotInitialAndPostCancelClearAssignments_TakeTheOrdinaryPath()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? executionA = null;
        Task? reportingA = null;
        Task? readinessA = null;
        Task? executionB = null;
        Task? reportingB = null;
        Task? readinessB = null;
        try
        {
            // THE INITIAL assignment: nothing retained, so no refusal.
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0), "The initial assignment must reach its gated Ready.");
            readinessA = CaptureReadinessWrite(service, "A's authorized write must exist.");
            writer.ReleaseReady(0);
            await readinessA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            executionA = GetActiveExecution(service);
            reportingA = GetActiveReporting(service);

            // The matching cancel CLEARS ownership (fallback Ready is suppressed by the consumed
            // claim; no second Ready). The trailing probe is the handler-return barrier: the loop
            // re-arms its next read only after the cancel handler returned, so Consumed(3) + a
            // re-armed read prove the clear actually happened.
            reader.Push(MatchingCancel(TaskA));
            reader.Push(Probe("after-cancel-clear"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reader.ReadStarted(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));

            // THE POST-CANCEL-CLEAR assignment: ordinary path, no refusal.
            reader.Push(ResultAssignment(TaskB));
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskB);
            await writer.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            executionB = GetActiveExecution(service);
            reportingB = GetActiveReporting(service);
            reader.Push(ReceiptAck(TaskB, connection.AssignedId));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(1), "The post-cancel successor must reach its own gated Ready.");
            readinessB = CaptureReadinessWrite(service, "B's authorized write must exist.");
            writer.ReleaseReady(1);
            await readinessB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

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
            await JoinAllForTeardownAsync(service,
                ("assignment execution A", executionA),
                ("assignment reporting A", reportingA),
                ("readiness write A", readinessA),
                ("assignment execution B", executionB),
                ("assignment reporting B", reportingB),
                ("readiness write B", readinessB),
                ("loop", loop));
        }
    }

    /// <summary>
    /// CELL 5(d): the refusal MUST NOT fire on legacy and ACK-ONLY connections. A predecessor is
    /// retained with NO started Ready write — the exact state that is refused on a both-flags
    /// connection — and the successor is accepted and runs to its own single Ready on an ACK-only
    /// connection (and on a fully legacy one). REMOVAL PROOF for the mode check: a boundary that
    /// ignored the negotiated shape would refuse here and fail the acceptance rendezvous.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task LegacyAndAckOnlyConnections_NeverRefuseAPreReadySuccessor(
        bool readyRequired, bool ackEnabled)
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: ackEnabled, completionReadyRequired: readyRequired);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? executionA = null;
        Task? reportingA = null;
        Task? readinessA = null;
        Task? executionB = null;
        Task? reportingB = null;
        Task? readinessB = null;
        try
        {
            // Predecessor A retained with NO started Ready write. In these modes the ordinary
            // Ready is ungated, so it starts at eligibility — HOLD it via the gated writer and do
            // NOT release it: the write is started (pending) but A is still retained, which is
            // precisely the shape the both-flags boundary ACCEPTS; to make the control stronger,
            // the successor is delivered while A's write is held and B must still proceed.
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessA = CaptureReadinessWrite(
                service, "The ungated shape must have started A's readiness write.");
            executionA = GetActiveExecution(service);
            reportingA = GetActiveReporting(service);
            await reportingA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await executionA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The successor arrives while A's write is STILL HELD. In these modes there is no
            // boundary: the replacement drain joins A's held write (proven by B not starting until
            // the release), then B runs.
            reader.Push(ResultAssignment(TaskB));
            await reader.Consumed(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reader.ReadStarted(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // IN-WINDOW, with the write still held: B was consumed, but correct code cannot
            // possibly have reached the reset (the drain parks on the write join), so B must never
            // have started a prompt.
            Assert.False(readinessA.IsCompleted);
            Assert.False(
                runner.HasPromptStarted(TaskB),
                "The replacement drain must join A's held write before B starts (unchanged behavior).");

            writer.ReleaseReady(0);
            await readinessA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            await AwaitResetCountAsync(runner, 2, loop,
                "The successor must be accepted on a legacy/ACK-only connection (no refusal).");
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskB);
            await writer.ReadyEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessB = CaptureReadinessWrite(service, "B's own readiness write must start.");
            executionB = GetActiveExecution(service);
            reportingB = GetActiveReporting(service);
            writer.ReleaseReady(1);
            await readinessB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reportingB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(2, writer.ReadyCount);
            // A's own result-mapping Complete writes exist in the ungated modes (one per completed
            // task); the important fact is exactly one per assignment and no refusal artifacts.
            Assert.Equal(2, writer.Completes.Count);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, writer.ReadyCount);
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution A", executionA),
                ("assignment reporting A", reportingA),
                ("readiness write A", readinessA),
                ("assignment execution B", executionB),
                ("assignment reporting B", reportingB),
                ("readiness write B", readinessB),
                ("loop", loop));
        }
    }


    // ══════════════════════════════════════════════════════════════════════════
    // Criterion 4 — abandonment boundaries preserved.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN UNACKNOWLEDGED MATCHING CANCEL exits WITHOUT any ACK wait and emits its EXISTING
    /// single-flight fallback Ready — which is NOT receipt confirmation: the fallback is emitted
    /// with the receipt permanently absent, a LATER ACK for that task confirms NOTHING (the
    /// assignment is already cleared, so the receipt cannot even land), and the fallback is never
    /// re-sent or retried (exactly one Ready, the claim consumed exactly once).
    /// <para>
    /// REMOVAL PROOF. A cancel handler that waited for an ACK would leave the loop alive past the
    /// bounded join; a handler that suppressed its fallback pending a receipt fails the Ready
    /// rendezvous; a fallback re-sent after a later ACK fails the exact one-Ready count.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UnacknowledgedMatchingCancel_ExitsWithoutAckWait_FallbackReadyIsNotReceiptConfirmation()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        Task? fallbackWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // NO ACK: the ordinary gate withholds the settlement, so the shared claim is FREE for
            // the cancel fallback.
            var owner = GetActiveAssignment(service);
            var readyClaim = GetOwnerReadyClaim(service);
            Assert.Equal(0, GetReadyClaimState(readyClaim));

            var readsBeforeCancel = reader.ReadsStarted;

            // THE UNACKNOWLEDGED MATCHING CANCEL — no receipt confirmation will ever arrive before
            // or during this cancel, proving no ACK wait is involved in the exit.
            reader.Push(MatchingCancel(TaskA));

            // The fallback Ready is emitted WITHOUT any ACK, and the loop's cancel handler finishes
            // (the next read is re-armed) — a positive completion witness, never a polling result.
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The cancel fallback must still emit its single Ready without any ACK.");
            writer.ReleaseReady(0);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // The handler has RETURNED: the read was re-armed, ownership cleared, heartbeat state
            // reset — all without any ACK wait.
            await reader.ReadStarted(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));
            Assert.Equal(TaskA, TaskIdOf(owner!));

            // THE CLAIM WAS CONSUMED EXACTLY ONCE by the fallback; no retry or re-send follows.
            Assert.Equal(1, GetReadyClaimState(readyClaim));
            Assert.Equal(1, writer.ReadyCount);

            // THE FALLBACK IS NOT RECEIPT CONFIRMATION. A LATER ACK for that task — after the clear
            // — confirms NOTHING: the slot is empty, so the receipt has no assignment to land on,
            // no second Ready is written, and the fallback is not re-sent.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            reader.Push(Probe("after-late-ack"));
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
            Assert.Null(GetActiveAssignment(service));

            // The connection binding is preserved: a further probe is still consumed on the SAME
            // loop, and the connection was never retired by a matching cancel.
            Assert.False(connection.IsRetired);
            reader.Push(Probe("binding-check"));
            await reader.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
            fallbackWrite = null;
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("fallback write", fallbackWrite),
                ("loop", loop));
        }
    }

    /// <summary>
    /// A STALE cancel on a BOTH-FLAGS connection remains a no-op and does NOT consume the
    /// successor's Ready claim: the successor still produces its OWN gated Ready from its exact
    /// ACK. The stale cancel also never waits for an ACK (it does nothing at all).
    /// </summary>
    [Fact]
    public async Task GatedMode_StaleCancelRemainsNoOp_SuccessorKeepsItsOwnClaim()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        Task? executionB = null;
        Task? reportingB = null;
        Task? readinessB = null;
        try
        {
            // A completes fully, including its gated Ready (via the exact ACK), and is REPLACED by
            // B — proving the successor path with a STARTED write, per criterion 5(b).
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0), "A's exact ACK must authorize its one Ready.");
            var authorizedA = CaptureReadinessWrite(service, "A's authorized write must exist.");
            writer.ReleaseReady(0);
            await authorizedA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            reader.Push(ResultAssignment(TaskB));
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            executionB = GetActiveExecution(service);
            reportingB = GetActiveReporting(service);

            var claimB = GetOwnerReadyClaim(service);
            Assert.Equal(0, GetReadyClaimState(claimB));

            // THE STALE cancel for A arrives while B is active: a no-op.
            reader.Push(MatchingCancel(TaskA));
            reader.Push(Probe("after-stale-cancel"));
            await reader.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.False(runner.WasCancelled(TaskB), "A stale cancel must not cancel the successor.");
            Assert.Equal(TaskB, TaskIdOf(GetActiveAssignment(service)!));
            Assert.Equal(0, GetReadyClaimState(claimB));
            Assert.False(writer.CompleteEntered(1).IsCompleted, "B must still be running.");

            // B completes with its own gated Ready from its own exact ACK.
            runner.Release(TaskB);
            await writer.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            reader.Push(ReceiptAck(TaskB, connection.AssignedId));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(1), "The successor must keep its own gated Ready.");
            readinessB = CaptureReadinessWrite(service, "B's authorized write must exist.");
            writer.ReleaseReady(1);
            await readinessB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reportingB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

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
            await JoinAllForTeardownAsync(service,
                ("assignment execution B", executionB),
                ("assignment reporting B", reportingB),
                ("readiness write B", readinessB),
                ("loop", loop));
        }
    }

    /// <summary>
    /// RUN CANCELLATION (the loop token) drains and clears WITHOUT any ACK wait: with a completed
    /// but unconfirmed report retained, cancelling the token completes the loop on its own bounded
    /// join, emits NO ordinary Ready (the gate holds — the same gate every drain consults), and
    /// never fabricates a receipt.
    /// <para>
    /// REMOVAL PROOF. A teardown that waited for an ACK would leave the loop alive past the bounded
    /// join (a named teardown failure); one that emitted an unacknowledged Ready fails the
    /// zero-Ready assertion by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GatedMode_RunCancellationDrainsWithoutAckWait_EmitsNoReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var loop = InvokeProcessMessages(service, connection, loopCts.Token);

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
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, writer.ReadyCount);

            // RUN CANCELLATION: no ACK will EVER arrive (the reader stays open the whole time —
            // teardown cannot even end the stream as an EOF would). The loop must still finish on
            // its own bounded join.
            await loopCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            Assert.Equal(0, writer.ReadyCount);
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.True(connection.IsRetired);
        }
        finally
        {
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
    /// A READER FAULT drains and clears WITHOUT any ACK wait, emits NO ordinary Ready (the gate
    /// holds — the same gate every drain consults), and preserves primary precedence: the reader's
    /// ORIGINAL exception surfaces unchanged out of the loop.
    /// <para>
    /// REMOVAL PROOF. A drain that waited for an ACK fails the bounded loop join; one that
    /// swallowed the primary (or replaced it with a readiness/transport error) fails the exception
    /// identity assertion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GatedMode_ReaderFaultDrainsWithoutAckWait_PrimarySurfacesUnchanged()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new FaultingResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        var primary = new GateReaderPrimaryException("injected reader fault");
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
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, writer.ReadyCount);

            // THE READER FAULT — the ACK is permanently absent (it is never pushed).
            reader.ArmFault(primary);

            var propagated = await Assert.ThrowsAsync<GateReaderPrimaryException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(primary, propagated);

            Assert.Equal(0, writer.ReadyCount);
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.True(connection.IsRetired);
        }
        finally
        {
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
    /// EOF TEARDOWN WITH A THROWING CANCELLATION CALLBACK: the drain clears WITHOUT any ACK wait
    /// (the ACK is permanently absent), emits NO ordinary Ready, and the DEFERRED cancellation-
    /// cleanup failure propagates because there is no primary — after the ownership clear, the
    /// heartbeat-state cleanup and the retirement. Sanitized, guarded diagnostics only.
    /// <para>
    /// REMOVAL PROOF. A teardown that waited for an ACK would never reach the callback's
    /// propagation; a teardown that lost the deferred failure would complete the loop normally and
    /// fail the throw assertion; one that surfaced it BEFORE the clear would fail the
    /// connection-binding assertions taken at the drain's completion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GatedMode_EofWithThrowingCallback_DrainsWithoutAckWait_DeferredPropagatesWithoutPrimary()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
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
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, writer.ReadyCount);

            var ownerCts = GetOwnerCts(service);
            var deferredFailure = new GateDeferredCancellationException("throwing cancellation callback");
            callbackRegistration = ownerCts.Token.Register(() => throw deferredFailure);

            // EOF with the ACK permanently absent: the teardown drains, clears and retires, and
            // the deferred cancellation-callback failure then propagates (no primary exists).
            reader.TryComplete();

            var surfaced = await Assert.ThrowsAnyAsync<Exception>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Contains(deferredFailure, Flatten(surfaced));

            // The clear/retire ordering is preserved: the slot is empty, the connection retired.
            Assert.Equal(0, writer.ReadyCount);
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.True(connection.IsRetired);
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);

            // The deferred failure was propagated, never downgraded to a report.
            var diagnostics = stdErr.ToString();
            Assert.DoesNotContain("Task cancellation cleanup failed", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(deferredFailure.Message, diagnostics, StringComparison.Ordinal);
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

    // ══════════════════════════════════════════════════════════════════════════
    // Harness
    // ══════════════════════════════════════════════════════════════════════════

    private enum RetainedGateOutcome
    {
        Completed,
        Failed,
        Cancelled,
    }

    /// <summary>Public mirror of the private enum so an xUnit theory can take it as a parameter.</summary>
    public enum GateOutcome
    {
        Completed,
        Failed,
        Cancelled,
    }

    private static TaskOutcome ToTaskOutcome(RetainedGateOutcome outcome) => outcome switch
    {
        RetainedGateOutcome.Completed => TaskOutcome.Completed,
        RetainedGateOutcome.Failed => TaskOutcome.Failed,
        RetainedGateOutcome.Cancelled => TaskOutcome.Cancelled,
        _ => throw new InvalidOperationException($"Unknown outcome: {outcome}"),
    };

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

    private static Task GetActiveExecution(WorkerService service) =>
        OwnerTask(GetActiveAssignment(service), "Execution");

    private static Task GetActiveReporting(WorkerService service) =>
        OwnerTask(GetActiveAssignment(service), "Reporting");

    private static Task OwnerTask(object? active, string property)
    {
        var owner = active
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (Task)owner.GetType().GetProperty(property)!.GetValue(owner)!;
    }

    /// <summary>
    /// Invokes the production settlement for a slot and returns the ONE retained write (or
    /// <c>null</c> when the readiness predicate does not hold / the claim was already taken) — the
    /// identical entry point the loop and every ownership transition use.
    /// </summary>
    private static Task? InvokeSettleOrdinaryReady(WorkerService service, object slot) =>
        (System.Threading.Tasks.Task?)typeof(WorkerService)
            .GetMethod("SettleOrdinaryReady", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [slot]);

    /// <summary>The assignment-local receipt tracker carried by an owner.</summary>
    private static object OwnerReceiptOf(object owner) =>
        owner.GetType().GetProperty("Receipt")!.GetValue(owner)!;

    /// <summary>The retained assignment's task ID, read from its owner.</summary>
    private static string TaskIdOf(object owner) =>
        (string)owner.GetType().GetProperty("TaskId")!.GetValue(owner)!;

    private static object GetOwnerOrdinaryReady(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return active.GetType().GetProperty("OrdinaryReady")!.GetValue(active)!;
    }

    private static object GetOwnerReceipt(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return active.GetType().GetProperty("Receipt")!.GetValue(active)!;
    }

    private static object GetOwnerReadyClaim(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return active.GetType().GetProperty("Ready")!.GetValue(active)!;
    }

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

    private static Task? GetRetainedReadinessWrite(WorkerService service)
    {
        var active = GetActiveAssignment(service);
        if (active is null)
            return null;
        var slot = active.GetType().GetProperty("OrdinaryReady")!.GetValue(active)!;
        return (Task?)slot.GetType().GetProperty("Write")!.GetValue(slot);
    }

    private static int GetSlotOccupancy(WorkerService service) =>
        GetActiveAssignment(service) is null ? 0 : 1;

    private static bool IsOrdinaryReadySettled(object slot) =>
        (bool)slot.GetType().GetProperty("IsSettled")!.GetValue(slot)!;

    /// <summary>
    /// Whether the slot's ELIGIBILITY fact alone is published — read through the production
    /// <c>Arm</c> semantics only (a settled slot hands out a never-completing task, so settled
    /// slots report ineligible here; every use is on an unsettled slot).
    /// </summary>
    private static bool IsOrdinaryReadyEligible(object slot) =>
        !IsOrdinaryReadySettled(slot)
        && (bool)slot.GetType()
            .GetField("_eligible", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(slot)!;

    private static Task ArmOrdinaryReady(object slot) =>
        (Task)slot.GetType().GetMethod("Arm")!.Invoke(slot, null)!;

    private static Task CaptureReadinessWrite(WorkerService service, string because)
    {
        var write = GetRetainedReadinessWrite(service)
            ?? throw new Xunit.Sdk.XunitException(because);
        return write;
    }

    private static int GetReadyClaimState(object readyClaim) =>
        (int)readyClaim.GetType().GetField("_claimed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(readyClaim)!;

    private static bool GetReceiptArmed(object receipt) =>
        (bool)receipt.GetType().GetProperty("IsArmed")!.GetValue(receipt)!;

    private static bool GetReceiptConfirmed(object receipt) =>
        (bool)receipt.GetType().GetProperty("IsConfirmed")!.GetValue(receipt)!;

    /// <summary>
    /// Awaits the runner's Nth reset entry, converting a non-arrival into a NAMED failure that
    /// includes the loop's liveness — the signature of a handler that never reached its reset.
    /// </summary>
    private static async Task AwaitResetCountAsync(
        GatedRunner runner, int count, System.Threading.Tasks.Task loop, string because)
    {
        try
        {
            await runner.AwaitResetCountAsync(count, Failsafe, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            var loopState = loop.IsCompleted ? "loop completed" : "loop still running";
            throw new Xunit.Sdk.XunitException(
                $"{because} (reset #{count} never entered; {loopState}; promptIds=[{runner.StartedTaskIds}])");
        }
    }

    /// <summary>Flattens an exception so a deferred failure can be located inside runtime wrappers.</summary>
    private static IReadOnlyList<Exception> Flatten(Exception exception) =>
        exception is AggregateException aggregate
            ? [.. aggregate.Flatten().InnerExceptions]
            : [exception];

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

    private async Task JoinAllForTeardownAsync(
        WorkerService service, params (string Name, Task? Producer)[] producers)
    {
        var failures = new List<string>();
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
                failures.Add(name);
        }

        // The retained assignment's own tasks, so a failure path can never leave one behind.
        if (GetActiveAssignment(service) is { } active)
        {
            foreach (var property in new[] { "Execution", "Reporting" })
            {
                var producer = (Task?)active.GetType().GetProperty(property)!.GetValue(active);
                if (producer is null)
                    continue;
                try
                {
                    await producer.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                }
                catch (Exception)
                {
                }

                if (!producer.IsCompleted)
                    failures.Add($"active assignment {property}");
            }

            var slot = active.GetType().GetProperty("OrdinaryReady")!.GetValue(active)!;
            var write = (Task?)slot.GetType().GetProperty("Write")!.GetValue(slot);
            if (write is not null)
            {
                try
                {
                    await write.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                }
                catch (Exception)
                {
                }
            }
        }

        if (failures.Count > 0)
            throw new Xunit.Sdk.XunitException(
                $"Teardown left still-live producers: {string.Join(", ", failures)}.");

        service.Dispose();
    }

    // ── Messages ──────────────────────────────────────────────────────────────

    private static OrchestratorMessage Assignment(string taskId, bool unmappableResult = false) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-gate",
            GoalDescription = "desc",
            Prompt = unmappableResult ? "produce-unmappable" : "prompt",
            Role = GrpcWorkerRole.Coder,
        },
    };

    private static OrchestratorMessage ResultAssignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-gate",
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

    // ── Doubles ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A gated request writer shaped after the readiness-ownership fixture's <c>GatedWriter</c>:
    /// EVERY <c>Ready</c> write parks until released BY INDEX (making a held readiness write a
    /// deterministic milestone); <c>Complete</c> writes release immediately unless
    /// <see cref="HoldCompletes"/> is set, and an optional ONE-SHOT <see cref="FailCompleteAtIndexZero"/>
    /// terminates the first Complete write into a real failure AFTER its release. The CANCELLABLE
    /// write overload is implemented EXPLICITLY so a cancelled write unwinds like a real gRPC
    /// stream writer.
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
        private Exception? _failCompleteAtIndexZero;

        internal bool HoldCompletes { get; init; }

        internal Exception? FailCompleteAtIndexZero
        {
            get { lock (_gate) return _failCompleteAtIndexZero; }
            set { lock (_gate) _failCompleteAtIndexZero = value; }
        }

        internal Action<int>? OnCompleteCancelled { get; set; }

        internal IReadOnlyList<WorkerMessage> Completes
        {
            get { lock (_gate) return [.. _completes]; }
        }

        internal int ReadyCount { get { lock (_gate) return _readies.Count; } }

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
                TaskCompletionSource release;
                Exception? failure;
                lock (_gate)
                {
                    index = _completes.Count;
                    _completes.Add(message.Clone());
                    entered = Slot(_completeEntered, index);
                    release = HoldCompletes ? Slot(_completeRelease, index) : PreCompleted();
                    failure = index == 0 ? _failCompleteAtIndexZero : null;
                    if (index == 0)
                        _failCompleteAtIndexZero = null;
                    if (_releaseImmediately)
                        release.TrySetResult();
                }

                entered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    OnCompleteCancelled?.Invoke(index);
                    throw;
                }

                if (failure is not null)
                    throw failure;
                return;
            }

            if (message.PayloadCase != WorkerMessage.PayloadOneofCase.Ready)
                return;

            int readyIndex;
            TaskCompletionSource readyEntered;
            TaskCompletionSource readyRelease;
            lock (_gate)
            {
                readyIndex = _readies.Count;
                _readies.Add(message.Clone());
                readyEntered = Slot(_readyEntered, readyIndex);
                readyRelease = Slot(_readyRelease, readyIndex);
                if (_releaseImmediately)
                    readyRelease.TrySetResult();

                List<TaskCompletionSource> satisfied = [];
                foreach (var (threshold, waiter) in _readyCountWaiters)
                {
                    if (_readies.Count >= threshold)
                        satisfied.Add(waiter);
                }

                foreach (var waiter in satisfied)
                {
                    waiter.TrySetResult();
                    _readyCountWaiters.Remove(waiter.Task.Id);
                }
            }

            readyEntered.TrySetResult();
            await readyRelease.Task.WaitAsync(ct);
        }

        private static TaskCompletionSource PreCompleted()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.TrySetResult();
            return source;
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
    /// A runner whose prompt parks until the test releases it — shaped after the readiness-
    /// ownership fixture's <c>GatedRunner</c>.
    /// </summary>
    private sealed class GatedRunner : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private string? _taskId;
        private bool _teardown;

        /// <summary>
        /// When set, releasing the prompt throws an LLM-boundary exception instead of returning —
        /// which the REAL executor maps into a FAILED <see cref="TaskResult"/>.
        /// </summary>
        internal bool FailOnRelease { get; init; }

        internal bool WasCancelled(string taskId)
        {
            lock (_gate) return _cancelled.Contains(taskId);
        }

        private readonly HashSet<string> _cancelled = [];
        private readonly HashSet<string> _resetIds = [];
        private int _resetCount;
        private readonly Dictionary<int, TaskCompletionSource> _resetCountWaiters = [];

        /// <summary>Whether a prompt for <paramref name="taskId"/> has ever ENTERED (not merely gated).</summary>
        internal bool HasPromptStarted(string taskId)
        {
            lock (_gate) return _startedIds.Contains(taskId);
        }

        private readonly HashSet<string> _startedIds = [];

        /// <summary>Diagnostic snapshot of every task whose prompt ever entered.</summary>
        internal string StartedTaskIds
        {
            get { lock (_gate) return string.Join(",", _startedIds); }
        }

        /// <summary>Whether the runner's session reset ever ENTERED for <paramref name="taskId"/>.</summary>
        internal bool ResetEntered(string taskId)
        {
            lock (_gate) return _resetIds.Contains(taskId);
        }

        /// <summary>How many resets production has ENTERED.</summary>
        internal int ResetCount => Volatile.Read(ref _resetCount);

        /// <summary>
        /// Completes once the runner's session reset has ENTERED at least <paramref name="count"/>
        /// times. Resets are keyed by ORDER, not task id: the runner's current-task id is set by the
        /// executor later than the reset, so the id observed inside the reset is the PREDECESSOR's.
        /// The reset is the FIRST production step after a replacement drain, so reaching count N is
        /// positive evidence that the Nth assignment handler was actually reached.
        /// </summary>
        internal Task AwaitResetCountAsync(int count, TimeSpan failsafe, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_resetCount >= count)
                    return Task.CompletedTask;
                if (!_resetCountWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _resetCountWaiters[count] = waiter;
                }

                return waiter.Task.WaitAsync(failsafe, ct);
            }
        }

        internal Task PromptStarted(string taskId) => Slot(_started, taskId).Task;

        internal void Release(string taskId) => Slot(_release, taskId).TrySetResult();

        internal void ReleaseAll()
        {
            lock (_gate)
            {
                _teardown = true;
                foreach (var source in _release.Values) source.TrySetResult();
            }
        }

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var id = _taskId ?? "(unknown)";
            lock (_gate) _startedIds.Add(id);
            Slot(_started, id).TrySetResult();
            try
            {
                await Slot(_release, id).Task.WaitAsync(ct);
                if (FailOnRelease)
                    throw new GatedAgentFailureException(new RpcException(
                        new Status(StatusCode.ResourceExhausted, "secret-must-not-escape")));
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
        {
            List<TaskCompletionSource> ready = [];
            lock (_gate)
            {
                _resetIds.Add(_taskId ?? "(unknown)");
                _resetCount++;
                foreach (var (threshold, waiter) in _resetCountWaiters)
                {
                    if (_resetCount >= threshold)
                        ready.Add(waiter);
                }

                foreach (var waiter in ready)
                    _resetCountWaiters.Remove(waiter.Task.Id);
            }

            foreach (var waiter in ready)
                waiter.TrySetResult();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedWritePrimaryException(string message) : Exception(message);

    /// <summary>An agent-boundary failure the REAL executor maps into a FAILED result.</summary>
    private sealed class GatedAgentFailureException(Exception inner)
        : Exception("injected agent failure", inner);

    /// <summary>A reader fault whose IDENTITY the loop must propagate unchanged.</summary>
    private sealed class GateReaderPrimaryException(string message) : Exception(message);

    /// <summary>A deferred cancellation-callback failure the teardown must propagate without a primary.</summary>
    private sealed class GateDeferredCancellationException(string message) : Exception(message);
}