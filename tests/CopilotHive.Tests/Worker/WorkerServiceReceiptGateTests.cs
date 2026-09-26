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

    /// <summary>
    /// THE FIVE-SECOND RETRANSMISSION SEQUENCE VALUE the test clock is advanced by. It is the
    /// EXACT constant the production retransmitter waits, so advancing the manual clock by this
    /// amount is precisely one production interval — not an approximation the test chose.
    /// </summary>
    private static readonly TimeSpan RetransmissionIntervalFiveSeconds = TimeSpan.FromSeconds(5);

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


    /// <summary>
    /// CANCELLED LOCAL COMPLETE TERMINATION — the missing third termination state. The exact ACK
    /// arrives FIRST while the Complete write is held; releasing that ORIGINAL write then makes it
    /// terminate Canceled (the writer's cancellable overload reports the in-write cancellation),
    /// and only after that termination does the one Ready follow. The ACK never completes the write,
    /// never replaces its cancellation, and never changes the retained result.
    /// <para>
    /// MUTATION PROOF. Removing <c>PublishCompleteWriteTerminated</c> from the write's finally — or
    /// withdrawing authorization specifically for a cancelled write — leaves Ready absent and fails
    /// the named rendezvous. Treating the early ACK as write completion fails the pre-termination
    /// zero-Ready/claim assertions. Treating cancellation as a failed execution would suppress Ready
    /// and fail the same rendezvous.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GatedMode_EarlyAckThenCancelledCompleteTermination_PreservesCancellationBeforeSingleReady()
    {
        var runner = new GatedRunner();
        var injected = new OperationCanceledException("injected local Complete cancellation");
        var writer = new GatedWriter
        {
            HoldCompletes = true,
            FailCompleteAtIndexZero = injected,
        };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        var completeCancelled = new TaskCompletionSource<OperationCanceledException>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        writer.OnCompleteCancelled = (_, cancellation) => completeCancelled.TrySetResult(cancellation);

        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var retainedBeforeCancellation = Assert.IsType<TaskResult>(GetRetainedResult(service));
            var readyClaim = GetOwnerReadyClaim(service);

            // RECEIPT FIRST while the original Complete write is still pending. A trailing probe
            // proves the ACK handler returned; the write remains held and nothing has settled.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId)); // message 2
            reader.Push(Probe("cancelled-write-ack-handler-returned")); // message 3
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reader.ReadStarted(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptConfirmed(GetOwnerReceipt(service)));
            Assert.False(reporting.IsCompleted);
            Assert.Equal(0, writer.ReadyCount);
            Assert.Equal(0, GetReadyClaimState(readyClaim));

            // The ORIGINAL writer task now terminates CANCELED from inside its cancellable path.
            writer.ReleaseComplete(0);
            var observedCancellation = await completeCancelled.Task.WaitAsync(
                Failsafe, TestContext.Current.CancellationToken);
            Assert.Same(
                injected,
                observedCancellation);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "Reporting observes a cancelled local Complete write without replacing its outcome.");

            // RECEIPT + CANCELLED TERMINATION + ELIGIBILITY authorize exactly one Ready.
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "A cancelled-but-terminated Complete write must still authorize Ready after its ACK.");
            readinessWrite = CaptureReadinessWrite(service, "The cancelled-write Ready must be retained.");
            Assert.Equal(1, GetReadyClaimState(readyClaim));
            Assert.Same(retainedBeforeCancellation, GetRetainedResult(service));
            Assert.Single(writer.Completes);

            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);
        }
        finally
        {
            writer.OnCompleteCancelled = null;
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
    /// ELIGIBILITY IS AN INDEPENDENT REQUIRED CONJUNCT. The Complete is mapped/ARMED, the exact
    /// receipt is CONFIRMED, and the original local Complete write has TERMINATED — but reporting
    /// is deliberately held in its success diagnostic BEFORE its finally publishes eligibility.
    /// Ready must remain absent until that diagnostic is released and eligibility is published.
    /// <para>
    /// MUTATION PROOF. Removing <c>_eligible</c> from <c>ReadinessHolds_Locked</c> makes the ACK wake
    /// settle Ready while this test still holds reporting at the diagnostic; the bounded
    /// non-completion assertion then fails (ReadyEntered completes instead of timing out). This
    /// varies only the eligibility conjunct: arm, receipt and write termination are all asserted
    /// true first.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GatedMode_ArmedConfirmedTerminatedButIneligible_WithholdsReadyUntilEligibilityPublishes()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter { HoldCompletes = true };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var output = new StringWriter();
        var successDiagnosticFailure = new EligibilityDiagnosticException();
        var successThrower = new MarkerThrowingWriter(
            "Task task-A completed", output, successDiagnosticFailure);
        var diagnosticGate = new MarkerBlockingWriter("Task execution failed", output);
        Task? execution = null;
        Task? reporting = null;
        Task? readinessWrite = null;
        try
        {
            // The successful completion diagnostic throws; its enclosing inner finally publishes
            // the terminated-write fact. The outer catch then enters the blocking error diagnostic,
            // holding reporting before its outer finally can publish eligibility.
            Console.SetOut(successThrower);
            Console.SetError(diagnosticGate);
            reader.Push(ResultAssignment(TaskA));
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            var receipt = GetOwnerReceipt(service);
            var slot = GetOwnerOrdinaryReady(service);
            var claim = GetOwnerReadyClaim(service);
            Assert.True(GetReceiptArmed(receipt), "The successfully mapped Complete is armed.");
            Assert.False(IsOrdinaryReadyEligible(slot), "Eligibility is deliberately still false.");

            // RECEIPT FIRST while the Complete is still held. The ACK handler can return before the
            // diagnostic gate is entered, so the Console.Out synchronization cannot mask the
            // handler-return barrier.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId)); // message 2
            reader.Push(Probe("ineligible-ack-handler-returned")); // message 3
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reader.ReadStarted(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptConfirmed(receipt));
            Assert.False(IsOrdinaryReadyEligible(slot));
            Assert.Equal(0, GetReadyClaimState(claim));

            // Now terminate the original Complete write. The success diagnostic throws AFTER
            // SendAsync returned; its inner finally publishes the terminated-write fact. Entering
            // the blocking OUTER error diagnostic therefore proves termination is published while
            // eligibility is still false (the outer finally has not run).
            writer.ReleaseComplete(0);
            await diagnosticGate.Entered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(execution.IsCompleted);
            Assert.False(reporting.IsCompleted, "Reporting is held before eligibility publication.");
            Assert.Single(writer.Completes);
            Assert.True(GetReceiptConfirmed(receipt));
            Assert.False(IsOrdinaryReadyEligible(slot));
            Assert.Equal(0, GetReadyClaimState(claim));

            // POSITIVE NON-COMPLETION while ONLY eligibility is absent.
            var prematureReady = await Record.ExceptionAsync(() =>
                writer.ReadyEntered(0).WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(prematureReady);
            Assert.Equal(0, writer.ReadyCount);

            // Release the diagnostic: reporting's finally now publishes eligibility. The same
            // already-armed/confirmed/terminated facts immediately authorize exactly one Ready.
            diagnosticGate.Release();
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "Publishing the final eligibility conjunct must authorize the one Ready.");
            readinessWrite = CaptureReadinessWrite(service, "Eligibility publication must retain Ready.");
            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, writer.ReadyCount);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            diagnosticGate.Release();
            Console.SetOut(originalOut);
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
    [InlineData(true, false, true)]
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
    [InlineData(true, false, true)]
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

            // THE SLOT, in the EXACT shape the assignment handler builds for this mode. The shape
            // is selected by invoking production's OWN OrdinaryReadyGateEnabled predicate — NEVER
            // from expectReady — so a mutation from `ack && readyRequired` to `readyRequired`
            // changes the 10 cell to a gated slot and is killed by its expected ungated Ready.
            var gateEnabled = InvokeOrdinaryReadyGateEnabled(connection);
            Assert.Equal(!expectReady, gateEnabled);

            var slotType = serviceType.GetNestedType("OrdinaryReadySlot", BindingFlags.NonPublic)!;
            var ordinaryReady = gateEnabled
                ? Activator.CreateInstance(
                    slotType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: [connection, CancellationToken.None, ready, receipt],
                    culture: null)!
                : Activator.CreateInstance(
                    slotType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: [connection, CancellationToken.None, ready],
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
    /// <para>
    /// EVERY BARRIER IS A REAL MESSAGE/HANDLER BOUNDARY. The retained owner tasks are read only
    /// after read #2 has STARTED — the loop re-arms exactly one read after the Assignment handler
    /// returned, and the owner slot is installed inside that handler, so writer entry alone (which
    /// the reporting task can reach before the install) is never used as installation evidence. The
    /// receipt is observed only after the ACK's own handler-return boundary, and the trailing probe
    /// has its own not-already-satisfied message-count milestone.
    /// </para>
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

            // THE OWNER-INSTALLATION MILESTONE, taken while the original Complete write stays HELD.
            // Read #1 consumed the assignment; the loop re-arms exactly one pending read only AFTER
            // that Assignment handler returned (WorkerService's re-arm point), and
            // InstallActiveAssignment runs INSIDE that handler — so read #2 STARTING is positive
            // proof that the owner slot is published. Writer entry alone proves nothing about the
            // slot: `reporting` is created before the install and can reach this held Complete write
            // first, so the retained owner tasks could otherwise be read before they are installed.
            await reader.ReadStarted(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // ELIGIBILITY not yet published (the write is held), but the RECEIPT is already
            // confirmed: nothing settles. The trailing probe is the HANDLER-RETURN boundary and is
            // message 3: it is consumed by read #3, which the loop re-arms only after the ACK handler
            // returned — so this milestone (created and proved UNSATISFIED before the pushes) proves
            // the receipt was actually latched, not merely delivered.
            var ackHandlerReturned = reader.Consumed(3);
            Assert.False(ackHandlerReturned.IsCompleted);
            reader.Push(ReceiptAck(TaskA, connection.AssignedId)); // message 2
            reader.Push(Probe("ack-handler-returned")); // message 3
            await ackHandlerReturned.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
            // THE AFTER-SETTLED PROBE'S OWN MILESTONE. This probe is message 4 (assignment, ACK and
            // the handler-return probe are 1..3), so Consumed(3) is already satisfied and would
            // prove nothing. Consumed(4) cannot be satisfied before this push, and read #5 starting
            // afterwards additionally proves this probe's handler returned.
            var afterSettledConsumed = reader.Consumed(4);
            Assert.False(
                afterSettledConsumed.IsCompleted,
                "The after-settled barrier must not be an already-satisfied milestone.");
            reader.Push(Probe("after-settled")); // message 4
            await afterSettledConsumed.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reader.ReadStarted(5).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, loopCts.Token);

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

            // CANCEL THE PROCESS/LOOP TOKEN (the token that still expresses the cancel-and-drain
            // teardown; an EOF with a LIVE token now CARRIES the assignment instead). The drain
            // consults the SAME gate — no ACK, so no ordinary Ready is emitted — and never WAITS for
            // one. The loop finishes on its own bounded join, surfacing its cancelled read.
            await loopCts.CancelAsync();
            reader.TryComplete();
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

        var teardownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTeardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownRegistration = default(CancellationTokenRegistration);
        Task? execution = null;
        Task? reporting = null;
        object? owner = null;
        try
        {
            // Predecessor A: locally completed report, NO ACK — retained, eligible, but the Ready
            // write has never been started. This is the UNCONFIRMED refusal state, distinct from
            // the full-predicate reader-first vector below.
            reader.Push(ResultAssignment(TaskA)); // message 1
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            owner = GetActiveAssignment(service);
            var readyClaim = GetOwnerReadyClaim(service);
            var receipt = OwnerReceiptOf(owner!);
            var slot = GetOwnerOrdinaryReady(service);
            var resetBaseline = runner.ResetCount;
            Assert.Equal(1, resetBaseline);
            Assert.True(IsOrdinaryReadyEligible(slot), "Precondition: eligibility is published.");
            Assert.False(GetReceiptConfirmed(receipt), "Precondition: receipt remains unconfirmed.");
            Assert.Null(GetRetainedReadinessWrite(service)); // NOT started.
            Assert.Equal(0, writer.ReadyCount);

            // Hold fault-teardown at its FIRST cancellation boundary. When this fires, TaskB's
            // refusal was thrown and the loop entered finally, but cancellation/drain/clear cannot
            // proceed. The service slot must therefore still directly contain the exact owner A.
            teardownRegistration = GetOwnerCts(service).Token.Register(() =>
            {
                teardownEntered.TrySetResult();
                releaseTeardown.Task.GetAwaiter().GetResult();
            });

            // TaskB-specific consumption barrier: A is message 1, so Consumed(2) cannot already be
            // satisfied. This proves the refusal is reached only after B is actually consumed.
            var bConsumed = reader.Consumed(2);
            Assert.False(bConsumed.IsCompleted);
            reader.Push(ResultAssignment(TaskB)); // message 2
            await bConsumed.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await teardownEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // NON-VACUOUS SIDE-EFFECT PROOF while teardown is held BEFORE clear:
            Assert.Same(owner, GetActiveAssignment(service));
            Assert.Equal(TaskA, TaskIdOf(owner!));
            Assert.Equal(resetBaseline, runner.ResetCount);
            Assert.False(
                runner.HasPromptStarted(TaskB),
                "A refused successor must never start a prompt.");
            Assert.Equal(0, GetReadyClaimState(readyClaim));
            Assert.Single(writer.Completes); // ONLY A's own Complete; B added none.
            Assert.Equal(0, writer.ReadyCount);
            Assert.False(
                GetReceiptConfirmed(receipt),
                "An unconfirmed refusal must never infer an ACK.");

            // Release teardown and assert the literal PRIMARY refusal unchanged. Normal teardown
            // may now clear/retire; all no-side-effect assertions above were taken before it could.
            releaseTeardown.TrySetResult();
            var propagated = await Assert.ThrowsAsync<InvalidOperationException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal("Assignment received before authorized Ready.", propagated.Message);
            Assert.Equal(resetBaseline, runner.ResetCount);
            Assert.False(runner.HasPromptStarted(TaskB));
        }
        finally
        {
            releaseTeardown.TrySetResult();
            teardownRegistration.Dispose();
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
    /// THE READER-FIRST AUTHORIZATION KILLER. The production reader is arranged so TaskB and the
    /// fully satisfied readiness observation are BOTH complete before <c>Task.WhenAny</c>: because
    /// the read is checked first, TaskB reaches the Assignment handler while every predicate fact
    /// is present but <c>OrdinaryReadySlot.Write</c> is still null. The literal protocol refusal is
    /// required, and teardown is held inside the predecessor CTS callback so the retained owner can
    /// be inspected BEFORE cleanup clears it.
    /// <para>
    /// MUTATION PROOF. A boundary keyed on eligibility / full predicate facts instead of the
    /// started-and-retained write accepts TaskB and increments the reset count, failing the literal
    /// exception, owner-identity and reset-delta assertions. Removing the boundary likewise starts
    /// TaskB. Reader-first ordering is real: the one-shot BeforeNextRead hook synchronously publishes
    /// eligibility and enqueues B before MoveNext returns, so pendingRead and readinessWait are both
    /// complete and production's read-first tie rule selects B without first settling Ready.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AllPredicateFactsButNoStartedWrite_SuccessorStillRefused()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter { HoldCompletes = true };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var output = new StringWriter();
        var successThrower = new MarkerThrowingWriter(
            "Task task-A completed", output, new EligibilityDiagnosticException());
        var reportingDiagnostic = new MarkerBlockingWriter("Task execution failed", output);
        var teardownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTeardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tiePrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownRegistration = default(CancellationTokenRegistration);

        Task? execution = null;
        Task? reporting = null;
        object? owner = null;
        try
        {
            Console.SetOut(successThrower);
            Console.SetError(reportingDiagnostic);

            reader.Push(ResultAssignment(TaskA)); // message 1
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            owner = GetActiveAssignment(service);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            var slot = GetOwnerOrdinaryReady(service);
            var receipt = GetOwnerReceipt(service);
            var claim = GetOwnerReadyClaim(service);
            var resetBaseline = runner.ResetCount;
            Assert.Equal(1, resetBaseline);
            Assert.True(GetReceiptArmed(receipt));
            Assert.False(GetReceiptConfirmed(receipt));
            Assert.False(IsCompleteWriteTerminated(slot));
            Assert.False(IsOrdinaryReadyEligible(slot));
            Assert.Null(GetRetainedReadinessWrite(service));

            // Hold the loop's teardown at its FIRST cancellation boundary. Once this callback fires,
            // the refusal was thrown and the finally entered, but the captured predecessor is still
            // directly retained — cleanup cannot join, clear or replace it until releaseTeardown.
            teardownRegistration = GetOwnerCts(service).Token.Register(() =>
            {
                teardownEntered.TrySetResult();
                releaseTeardown.Task.GetAwaiter().GetResult();
            });

            // PREFERRED DETERMINISTIC ARMING ROUTE: first await READ #2 STARTED. MoveNext increments
            // that counter only after it has taken the reader gate and atomically read+cleared the
            // current hook. Therefore this await proves read #2 already consumed the default null;
            // assigning below cannot be stolen by read #2. Read #3 cannot start until the ACK is
            // pushed and its handler returns, and the hook is assigned before that push, so there is
            // no concurrent assignment/read-and-clear window.
            await reader.ReadStarted(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, reader.ReadsStarted);
            Assert.False(tiePrepared.Task.IsCompleted);

            // Arm the one-shot ONLY for read #3, then push the ACK into already-pending read #2.
            // The hook runs synchronously inside read #3, before the loop can arm readiness:
            //  1) release Complete; the success diagnostic throws, inner finally publishes
            //     termination, and outer error diagnostic blocks before eligibility;
            //  2) publish eligibility via the exact production slot method;
            //  3) enqueue TaskB and return. Thus pendingRead(B) and readiness are BOTH complete.
            reader.BeforeNextRead = () =>
            {
                writer.ReleaseComplete(0);
                reportingDiagnostic.Entered.Task.GetAwaiter().GetResult();
                PublishOrdinaryReadyEligibility(slot);
                reader.Push(ResultAssignment(TaskB)); // message 3
                tiePrepared.TrySetResult();
                return Task.CompletedTask;
            };

            // Still exactly two reads and the hook has not fired: read #2 cannot consume an arm
            // installed after its read-and-clear point. Only now is the ACK allowed to complete it.
            Assert.Equal(2, reader.ReadsStarted);
            Assert.False(tiePrepared.Task.IsCompleted);
            reader.Push(ReceiptAck(TaskA, connection.AssignedId)); // message 2
            await tiePrepared.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await teardownEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE EXACT PRE-BOUNDARY STATE, inspected while teardown is deterministically held.
            Assert.True(GetReceiptConfirmed(receipt));
            Assert.True(IsCompleteWriteTerminated(slot));
            Assert.True(IsOrdinaryReadyEligible(slot));
            Assert.False(IsOrdinaryReadySettled(slot));
            Assert.Null(GetRetainedReadinessWrite(service));
            Assert.Equal(0, GetReadyClaimState(claim));
            Assert.Equal(0, writer.ReadyCount);
            Assert.Same(owner, GetActiveAssignment(service));
            Assert.Equal(TaskA, TaskIdOf(owner!));

            // NO SUCCESSOR SIDE EFFECTS. Reset count is the load-bearing witness (not the task-keyed
            // set): a mutation that drains/resets before throwing increments it. Prompt absence is
            // retained as a second, non-load-bearing witness.
            Assert.Equal(resetBaseline, runner.ResetCount);
            Assert.False(runner.HasPromptStarted(TaskB));
            Assert.True(reader.Consumed(3).IsCompleted, "TaskB must have been consumed in the tie.");

            // Release both held cleanup boundaries. Teardown is allowed to settle/join the now-full
            // predecessor predicate, but the PRIMARY refusal must surface unchanged afterwards.
            reportingDiagnostic.Release();
            releaseTeardown.TrySetResult();
            writer.ReleaseAll();

            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal("Assignment received before authorized Ready.", refusal.Message);
            Assert.Equal(resetBaseline, runner.ResetCount);
            Assert.False(runner.HasPromptStarted(TaskB));
        }
        finally
        {
            reportingDiagnostic.Release();
            releaseTeardown.TrySetResult();
            teardownRegistration.Dispose();
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
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
    /// THE DRAIN IS AN INDEPENDENT LEGITIMATE SETTLER. With every predicate fact present while the
    /// loop is already inside a matching-cancel drain, the drain starts and retains the one
    /// authorized Ready, JOINS it before clearing, and suppresses the cancel fallback because the
    /// shared claim was consumed. This is intentionally separate from the reader-first refusal.
    /// </summary>
    [Fact]
    public async Task MatchingCancelDrain_FullPredicate_StartsAndJoinsOneAuthorizedReady()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter { HoldCompletes = true };
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
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
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            reader.Push(ReceiptAck(TaskA, connection.AssignedId)); // message 2
            reader.Push(Probe("drain-ack-returned")); // message 3
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reader.ReadStarted(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptConfirmed(GetOwnerReceipt(service)));

            var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drainEnteredRegistration = GetOwnerCts(service).Token.Register(
                () => drainEntered.TrySetResult());
            var readsBeforeCancel = reader.ReadsStarted;
            reader.Push(MatchingCancel(TaskA)); // message 4
            await drainEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(readsBeforeCancel, reader.ReadsStarted);

            // Complete terminates and reporting publishes eligibility while the loop is inside the
            // drain. The drain itself is the only participant that can settle the full predicate.
            writer.ReleaseComplete(0);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The matching-cancel drain must start the one authorized Ready.");
            readinessWrite = CaptureReadinessWrite(service, "The drain must retain the write it starts.");

            Assert.Equal(1, GetReadyClaimState(GetOwnerReadyClaim(service)));
            Assert.False(readinessWrite.IsCompleted);
            Assert.NotNull(GetActiveAssignment(service));

            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The next read starts only after the drain joined the write and cleared ownership.
            await reader.ReadStarted(5).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));
            Assert.Equal(1, writer.ReadyCount); // no cancel fallback duplicate.

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
            // These are B-specific milestones: A+ACK already account for messages 1 and 2 / reads
            // 1..3, so neither B-consumed(3) nor next-read(4) can be satisfied before B is pushed.
            var bConsumed = reader.Consumed(3);
            var nextReadAfterB = reader.ReadStarted(4);
            Assert.False(bConsumed.IsCompleted);
            Assert.False(nextReadAfterB.IsCompleted);
            var resetBaseline = runner.ResetCount;
            Assert.Equal(1, resetBaseline);

            reader.Push(ResultAssignment(TaskB)); // message 3
            await bConsumed.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // IN-WINDOW JOIN PROOF, with A's write still HELD: B has now been consumed, but the
            // handler cannot return/re-arm read 4 or reset/start B because its replacement drain is
            // parked on A's write join. Every assertion is taken before the write is released.
            Assert.False(
                nextReadAfterB.IsCompleted,
                "The loop must not re-arm after B while A's write remains unreleased.");
            var prematureNextRead = await Record.ExceptionAsync(() =>
                nextReadAfterB.WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(prematureNextRead);
            Assert.Equal(resetBaseline, runner.ResetCount);
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
                "B's reset must follow release and the completed write join.");
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await nextReadAfterB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
    /// STARTED-THEN-FAILED AUTHORIZATION IS MONOTONIC. On a both-flags connection A's exact ACK
    /// starts and retains its authorized Ready task; that exact task then FAULTS. TaskB must still
    /// be accepted because authorization is the fact that the write was started/retained — never
    /// its later outcome. The replacement drain observes the original failure through the existing
    /// sanitized diagnostic, does not retry A's Ready, then resets/starts B normally.
    /// <para>
    /// MUTATION PROOF. Withdrawing authorization when <c>Write.IsFaulted</c>, clearing Write after
    /// failure, or refusing a successor based on successful completion faults the loop with the
    /// protocol error and prevents reset/prompt B. Retrying A's failed Ready changes the pre-B
    /// Ready count from exactly one and fails by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SuccessorAfterAuthorizedReadyWriteFaulted_IsAccepted_DrainObservesFailureWithoutRetry()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        var readyFailure = new AuthorizedReadyFailureException("raw ready failure must stay sanitized");
        writer.FailNextReadyWrite = readyFailure;

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
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
            Console.SetError(stdErr);

            reader.Push(ResultAssignment(TaskA)); // message 1
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            executionA = GetActiveExecution(service);
            reportingA = GetActiveReporting(service);
            await reportingA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            reader.Push(ReceiptAck(TaskA, connection.AssignedId)); // message 2
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0), "A's exact ACK must start the authorized Ready.");
            readinessA = CaptureReadinessWrite(service, "A's authorized Ready must be retained.");
            Assert.False(readinessA.IsCompleted);

            // The ORIGINAL retained task faults; its exact exception identity is preserved. The
            // write remains retained and authorization cannot be withdrawn. Exactly one attempt.
            writer.ReleaseReady(0);
            var observed = await Assert.ThrowsAsync<AuthorizedReadyFailureException>(
                () => readinessA.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(readyFailure, observed);
            Assert.True(readinessA.IsFaulted);
            Assert.Same(readinessA, GetRetainedReadinessWrite(service));
            Assert.Equal(1, writer.ReadyCount);

            var resetBaseline = runner.ResetCount;
            Assert.Equal(1, resetBaseline);
            var bConsumed = reader.Consumed(3);
            var nextReadAfterB = reader.ReadStarted(4);
            Assert.False(bConsumed.IsCompleted);
            Assert.False(nextReadAfterB.IsCompleted);

            // TaskB AFTER the fault must be accepted. Its replacement drain observes A's already-
            // faulted write (no blocking, no retry), reports the failure in sanitized form, then
            // reaches reset/prompt B and re-arms the next read.
            reader.Push(ResultAssignment(TaskB)); // message 3
            await bConsumed.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await AwaitResetCountAsync(runner, 2, loop,
                "A faulted-but-started Ready must still authorize successor B.");
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await nextReadAfterB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(1, writer.ReadyCount); // A was NOT retried.
            Assert.Equal(2, runner.ResetCount);
            Assert.False(loop.IsCompleted);
            var diagnostics = stdErr.ToString();
            Assert.Contains("Task drain observed a fault", diagnostics, StringComparison.Ordinal);
            Assert.Contains(nameof(AuthorizedReadyFailureException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(readyFailure.Message, diagnostics, StringComparison.Ordinal);

            // Complete B and prove the stream remains usable after observing A's failure.
            executionB = GetActiveExecution(service);
            reportingB = GetActiveReporting(service);
            runner.Release(TaskB);
            await writer.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            reader.Push(ReceiptAck(TaskB, connection.AssignedId)); // message 4
            await AwaitRendezvousAsync(
                writer.ReadyEntered(1), "B must reach its own authorized Ready after A's fault.");
            readinessB = CaptureReadinessWrite(service, "B's authorized Ready must be retained.");
            writer.ReleaseReady(1);
            await readinessB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, writer.ReadyCount);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(originalErr);
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
    /// CELL 5(d): the refusal MUST NOT fire on legacy, ACK-ONLY or readiness-ONLY connections. A
    /// predecessor is retained with NO started Ready write — the exact state that is refused on a
    /// both-flags connection — and the successor is accepted and runs to its own single Ready in
    /// every non-gated mode. MUTATION PROOF for the mode check: removing that check while retaining
    /// <c>!HasStartedWrite</c> refuses all three rows and fails their reset/prompt rendezvous.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
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
            // PREDECESSOR A is retained BEFORE any Ready task starts: its prompt is still held, so
            // execution/reporting are live and OrdinaryReadySlot.Write is null. This is the exact
            // pre-Ready state that a guard with the MODE CHECK REMOVED would wrongly refuse.
            reader.Push(ResultAssignment(TaskA)); // message 1
            await runner.PromptStarted(TaskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            executionA = GetActiveExecution(service);
            reportingA = GetActiveReporting(service);
            var ownerA = GetActiveAssignment(service);
            Assert.Null(GetRetainedReadinessWrite(service));
            Assert.Equal(0, writer.ReadyCount);

            // B-specific barriers: neither can be satisfied by A. Assert that BEFORE pushing B.
            var bConsumed = reader.Consumed(2);
            var nextReadAfterB = reader.ReadStarted(3);
            Assert.False(bConsumed.IsCompleted);
            Assert.False(nextReadAfterB.IsCompleted);
            var resetBaseline = runner.ResetCount;
            Assert.Equal(1, resetBaseline);

            reader.Push(ResultAssignment(TaskB)); // message 2
            await bConsumed.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The non-gated mode check admitted B into the unchanged replacement drain. With A's
            // execution still held, the handler cannot reset/start B or re-arm read 3 yet.
            Assert.False(nextReadAfterB.IsCompleted);
            Assert.Equal(resetBaseline, runner.ResetCount);
            Assert.False(runner.HasPromptStarted(TaskB));
            Assert.Same(ownerA, GetActiveAssignment(service));
            Assert.Null(GetRetainedReadinessWrite(service));
            Assert.False(loop.IsCompleted, "A mode-guard removal would fault the loop here.");

            // Release A. Its report writes Complete, then its ungated ordinary Ready starts. The
            // replacement drain must join that HELD write before reset/prompt B.
            runner.Release(TaskA);
            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessA = CaptureReadinessWrite(
                service, "The ungated replacement drain must start and retain A's Ready.");
            await reportingA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await executionA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.False(readinessA.IsCompleted);
            Assert.False(nextReadAfterB.IsCompleted);
            var prematureNextRead = await Record.ExceptionAsync(() =>
                nextReadAfterB.WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(prematureNextRead);
            Assert.Equal(resetBaseline, runner.ResetCount);
            Assert.False(runner.HasPromptStarted(TaskB));
            Assert.Same(ownerA, GetActiveAssignment(service));

            // Release → write joins → reset → prompt → next read. Each later milestone was proved
            // absent before release, so this sequence cannot pass vacuously.
            writer.ReleaseReady(0);
            await readinessA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await AwaitResetCountAsync(runner, 2, loop,
                "The successor must be accepted on 00/01/10 connections before any Ready started.");
            await runner.PromptStarted(TaskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await nextReadAfterB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            executionB = GetActiveExecution(service);
            reportingB = GetActiveReporting(service);
            runner.Release(TaskB);
            await writer.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.ReadyEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            readinessB = CaptureReadinessWrite(service, "B's own ungated Ready must start.");
            writer.ReleaseReady(1);
            await readinessB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reportingB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(2, writer.ReadyCount);
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
            var capturedReceipt = OwnerReceiptOf(owner!);
            var readyClaim = GetOwnerReadyClaim(service);
            Assert.Equal(0, GetReadyClaimState(readyClaim));
            Assert.False(
                GetReceiptConfirmed(capturedReceipt),
                "Precondition: no receipt has been confirmed before the cancel fallback.");

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
            Assert.False(
                GetReceiptConfirmed(capturedReceipt),
                "The cancel fallback itself must NOT be treated as receipt confirmation.");

            // THE FALLBACK IS NOT RECEIPT CONFIRMATION. A LATER ACK for that task — after the clear
            // — confirms NOTHING on the CAPTURED predecessor receipt, writes no second Ready, and
            // cannot re-send the fallback. The count-4 barrier names the trailing probe and is
            // proved incomplete both before the ACK and after only the ACK (message 3) is consumed.
            var lateAckHandlerReturned = reader.Consumed(4);
            Assert.False(lateAckHandlerReturned.IsCompleted);
            reader.Push(ReceiptAck(TaskA, connection.AssignedId)); // message 3
            await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.False(
                lateAckHandlerReturned.IsCompleted,
                "The trailing-probe barrier must not be satisfied by consuming the ACK itself.");
            reader.Push(Probe("after-late-ack")); // message 4
            await lateAckHandlerReturned.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reader.ReadStarted(5).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.False(
                GetReceiptConfirmed(capturedReceipt),
                "A late ACK after cancel-clear must not confirm the captured predecessor receipt.");
            Assert.Equal(1, writer.ReadyCount);
            Assert.Null(GetActiveAssignment(service));

            // The connection binding is preserved: the NEW binding probe is message 5. Its own
            // milestone is created and proved incomplete BEFORE the push, so it cannot pass on the
            // prior ACK or trailing probe.
            Assert.False(connection.IsRetired);
            var bindingProbeConsumed = reader.Consumed(5);
            Assert.False(bindingProbeConsumed.IsCompleted);
            reader.Push(Probe("binding-check")); // message 5
            await bindingProbeConsumed.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reader.ReadStarted(6).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

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
    /// Non-carry EOF with a LIVE process token: ACK tracking is enabled but ordinary Ready is
    /// ungated, so ReadyStarted wins before EOF despite NO receipt ACK. The loop itself must
    /// propagate the deferred cancellation-callback failure AFTER joining the held Ready and
    /// clearing ownership; dropping the deferred failure cannot pass this vector.
    /// </summary>
    [Fact]
    public async Task AckOnlyMode_EofAfterReadyStarted_ThrowingCallbackPropagatesFromLoopWithoutPrimary()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: false);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);
        CancellationTokenRegistration callbackRegistration = default;
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
            var ownerCts = GetOwnerCts(service);
            var failure = new GateDeferredCancellationException("callback must propagate from loop");
            callbackRegistration = ownerCts.Token.Register(() => throw failure);
            Assert.Equal(1, writer.ReadyCount);
            Assert.False(ownerCts.IsCancellationRequested);

            // This is EOF with a LIVE process token and an actual started ordinary Ready.
            reader.TryComplete();
            writer.ReleaseReady(0);
            var surfaced = await Assert.ThrowsAnyAsync<Exception>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Contains(failure, Flatten(surfaced));
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.True(connection.IsRetired);
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
        }
        finally
        {
            callbackRegistration.Dispose();
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution), ("assignment reporting", reporting), ("loop", loop));
        }
    }

    /// <summary>
    /// EOF TEARDOWN UNDER PROCESS-TOKEN CANCELLATION WITH A THROWING CANCELLATION CALLBACK (an EOF
    /// with a LIVE token now CARRIES, so this variant is the one that still drains): the drain clears
    /// WITHOUT any ACK wait (the ACK is permanently absent), emits NO ordinary Ready, and the
    /// deferred cancellation-cleanup failure is the observed outcome after the ownership clear, the
    /// heartbeat-state cleanup and the retirement. Sanitized, guarded diagnostics only.
    /// <para>
    /// REMOVAL PROOF. A teardown that waited for an ACK would never reach the callback's
    /// propagation; a teardown that lost the deferred failure would complete the loop normally; one
    /// that surfaced it BEFORE the clear would fail the connection-binding assertions taken at the
    /// drain's completion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GatedMode_EofWithThrowingCallback_DrainsWithoutAckWait_DeferredPropagatesWithoutPrimary()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, loopCts.Token);

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

            // CANCEL THE PROCESS/LOOP TOKEN (the token that still expresses the cancel-and-drain
            // teardown; an EOF with a LIVE token now CARRIES the assignment instead): the teardown
            // drains and clears, and the throwing cancellation callback's failure is observed on the
            // cancellation request's OWN dispatch — never as an unobserved thread-pool fault.
            var cancellationFailure = await Record.ExceptionAsync(() => loopCts.CancelAsync());
            Assert.NotNull(cancellationFailure);
            Assert.Contains(deferredFailure, Flatten(cancellationFailure!));
            reader.TryComplete();

            // The loop itself surfaces its CANCELLED READ; the callback evidence stays on the
            // cancellation request captured above.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

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
    // RETRANSMISSION — the gated assignment's ONE frozen envelope re-sent while its receipt
    // stays unacknowledged. Driven through the REAL loop and the REAL TimeProvider seam.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE GATE, EXACTLY WHEN BOTH FLAGS HOLD. On a BOTH-FLAGS connection a completed but
    /// unacknowledged report's frozen envelope is retransmitted: the retry's FIRST delay timer is
    /// created through the production <c>TimeProvider</c> seam after reporting terminates, and
    /// advancing the clock drives ONE retransmission Complete whose payload is IDENTICAL to the
    /// original — the deep clone of the frozen snapshot. On legacy (00), ACK-only (01) and
    /// Ready-only (10) connections NO retry exists: no delay timer is EVER created through the
    /// clock seam (positive non-completion of the retransmitter's entry, observed over a bounded
    /// window while the loop keeps consuming), and the exact ACK still authorizes the single
    /// Ready exactly as before.
    /// <para>
    /// REMOVAL PROOF. Removing the both-flags gate puts the retry on legacy/ACK-only rows too, so
    /// their <see cref="ManualRetransmissionClock.TimerCount"/> is no longer zero and the
    /// zero-timer assertion fails by name; removing the retransmitter itself leaves the both-flags
    /// row's first timer absent and its retransmission rendezvous failing by name.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Retransmission_FirstDelayThroughClockSeam_OnlyOnBothFlagsConnection(
        bool readyRequired, bool ackEnabled, bool expectRetransmission)
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        var clock = new ManualRetransmissionClock();
        typeof(WorkerService)
            .GetProperty("TimeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, clock);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: ackEnabled, completionReadyRequired: readyRequired);
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

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var originalComplete = Assert.Single(writer.Completes);
            Assert.Equal(TaskA, originalComplete.Complete.TaskId);
            Assert.Equal(connection.AssignedId, originalComplete.WorkerId);

            if (!expectRetransmission)
            {
                // NON-GATED: the retransmitter must never exist. POSITIVE NON-COMPLETION with a
                // bounded wait: while the loop keeps consuming (the probe proves the loop is
                // alive) and the retained assignment stays in place, the clock seam must have
                // created ZERO delay timers. A mode-guard removal (retry built on a legacy,
                // ACK-only or Ready-only connection) creates the first timer here and fails by name.
                reader.Push(Probe("non-gated-window"));
                await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.Equal(0, clock.TimerCount);

                var suppressed = await Record.ExceptionAsync(() =>
                    clock.WaitForTimerCountAsync(1, TestContext.Current.CancellationToken)
                        .WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
                Assert.IsType<TimeoutException>(suppressed);
                Assert.Equal(0, clock.TimerCount);
                Assert.Single(writer.Completes);

                // The legacy/ACK-only settlement path is UNCHANGED: the exact ACK still
                // authorizes exactly ONE Ready on the ungated slot.
                reader.Push(ReceiptAck(TaskA, connection.AssignedId));
                await AwaitRendezvousAsync(
                    writer.ReadyEntered(0),
                    "The exact ACK must still authorize the one ungated Ready on a non-gated connection.");
                readinessWrite = CaptureReadinessWrite(service, "The ACK must authorize the write.");
                writer.ReleaseReady(0);
                await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

                reader.Push(Probe("after-non-gated-ready"));
                await reader.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.Equal(1, writer.ReadyCount);
                Assert.Single(writer.Completes); // no retransmission Complete followed.
                Assert.Equal(0, clock.TimerCount);

                reader.TryComplete();
                await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                return;
            }

            // BOTH-FLAGS: the frozen envelope's FIRST retry delay is created through the seam —
            // positively witnessed, never a sleep.
            await clock.WaitForTimerCountAsync(1, TestContext.Current.CancellationToken);

            // Advancing the clock drives ONE retransmission attempt: a SECOND Complete whose
            // payload is the frozen envelope's deep clone — identical identity to the original.
            clock.Advance(RetransmissionIntervalFiveSeconds);
            await writer.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var retransmission = writer.Completes[1];
            Assert.NotSame(originalComplete, retransmission);
            Assert.Equal(originalComplete.Complete.TaskId, retransmission.Complete.TaskId);
            Assert.Equal(originalComplete.Complete.Output, retransmission.Complete.Output);
            Assert.Equal(originalComplete.Complete.Status, retransmission.Complete.Status);
            Assert.Equal(originalComplete.WorkerId, retransmission.WorkerId);
            Assert.Equal(originalComplete.Complete.Model, retransmission.Complete.Model);
            Assert.Equal(originalComplete.Complete.Metrics, retransmission.Complete.Metrics);

            // ONE assignment-local retry task, at most one attempt at a time: with the receipt
            // still unconfirmed, the SAME advance (one interval) produced exactly ONE further
            // Complete. The retransmitter parks in a FRESH delay afterwards — witnessed by the
            // second timer created through the seam.
            Assert.Equal(2, writer.Completes.Count);
            Assert.Equal(2, clock.TimerCount);

            // THE EXACT ACK still closes the retry and authorizes the single Ready, unchanged.
            reader.Push(ReceiptAck(TaskA, connection.AssignedId));
            await AwaitRendezvousAsync(
                writer.ReadyEntered(0),
                "The exact ACK must still authorize the one gated Ready alongside the retry.");
            readinessWrite = CaptureReadinessWrite(service, "The ACK must authorize the write.");
            writer.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await writer.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // After the ACK, NO further retransmission is admitted: advancing the clock again
            // (a bounded window) produces no new Complete.
            var completesAfterAck = writer.Completes.Count;
            reader.Push(Probe("after-ack"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            clock.Advance(RetransmissionIntervalFiveSeconds);
            var premature = await Record.ExceptionAsync(() =>
                writer.CompleteEntered(completesAfterAck)
                    .WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(premature);
            Assert.Equal(completesAfterAck, writer.Completes.Count);

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
    /// THE FROZEN ENVELOPE IS NEVER HANDED TO A WRITER — proved on the RAW writer arguments and by
    /// MUTATING one of them. Every send's raw argument (the exact object production passed to
    /// <c>WriteAsync</c>, with no fixture clone in between) is a DISTINCT instance carrying EQUAL
    /// evidence; clearing the first delivered argument's nested <c>Metrics.Issues</c> in place does
    /// NOT change what the next retransmission delivers. The retained <see cref="TaskResult"/> keeps
    /// its exact identity throughout.
    /// <para>
    /// REMOVAL PROOF. A production mutant that hands the SAME frozen <c>TaskComplete</c> to every
    /// writer fails the raw reference-distinctness assertions (the fixture deliberately does NOT
    /// clone the raw list, so two deliveries of one object are ONE object here); a mutant that
    /// shallow-copies the message while SHARING the nested collections survives distinctness but
    /// fails the post-mutation evidence assertions.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Retransmission_RawWriterArguments_AreDistinct_AndImmuneToMutatingADeliveredOne()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        var clock = new ManualRetransmissionClock();
        typeof(WorkerService)
            .GetProperty("TimeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, clock);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
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
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var retained = Assert.IsType<TaskResult>(GetRetainedResult(service));

            // THE ORIGINAL SEND'S RAW ARGUMENT and the frozen nested evidence it carried.
            var rawOriginal = Assert.Single(writer.RawCompletes);
            var frozenIssues = rawOriginal.Complete.Metrics.Issues.ToArray();
            Assert.NotEmpty(frozenIssues); // NON-VACUITY: there is something real to mutate.

            // THE FIRST RETRANSMISSION — a DISTINCT raw object with EQUAL evidence.
            await clock.WaitForRetryParkedInDelayAsync(1, TestContext.Current.CancellationToken);
            clock.Advance(RetransmissionIntervalFiveSeconds);
            await writer.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var rawRetry1 = writer.RawCompletes[1];

            Assert.NotSame(rawOriginal, rawRetry1);
            Assert.NotSame(rawOriginal.Complete, rawRetry1.Complete);
            Assert.NotSame(rawOriginal.Complete.Metrics, rawRetry1.Complete.Metrics);
            Assert.Equal(rawOriginal.Complete, rawRetry1.Complete);

            // THE MUTATION, applied to the DELIVERED raw arguments themselves.
            rawOriginal.Complete.Metrics.Issues.Clear();
            rawOriginal.Complete.Metrics.Issues.Add("MUTATED-BY-THE-FIRST-WRITER");
            rawOriginal.Complete.Output = "MUTATED-ORIGINAL-OUTPUT";
            rawRetry1.Complete.Metrics.Issues.Clear();
            rawRetry1.Complete.Output = "MUTATED-RETRY-OUTPUT";

            // THE NEXT RETRANSMISSION still carries the FROZEN evidence, in the ORIGINAL ORDER.
            clock.Advance(RetransmissionIntervalFiveSeconds);
            await writer.CompleteEntered(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var rawRetry2 = writer.RawCompletes[2];

            Assert.NotSame(rawRetry1, rawRetry2);
            Assert.NotSame(rawRetry1.Complete, rawRetry2.Complete);
            Assert.Equal(frozenIssues, rawRetry2.Complete.Metrics.Issues);
            Assert.DoesNotContain("MUTATED-BY-THE-FIRST-WRITER", rawRetry2.Complete.Metrics.Issues);
            Assert.NotEqual("MUTATED-ORIGINAL-OUTPUT", rawRetry2.Complete.Output);
            Assert.NotEqual("MUTATED-RETRY-OUTPUT", rawRetry2.Complete.Output);
            Assert.Equal(TaskA, rawRetry2.Complete.TaskId);
            Assert.Equal(connection.AssignedId, rawRetry2.WorkerId);

            // THE RETAINED RESULT keeps its exact identity: never cloned, re-mapped or replaced.
            Assert.Same(retained, GetRetainedResult(service));
            Assert.Equal(3, writer.Completes.Count);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
    /// THE FROZEN ENVELOPE IS NEVER HANDED TO A WRITER, and the retained
    /// <see cref="TaskResult"/> keeps its exact identity across the ORIGINAL send and every
    /// retransmission. The original Complete and the retransmitted Complete both carry a payload
    /// EQUAL to the mapped completion but NEITHER is reference-identical to the other, so no
    /// writer can mutate what the next attempt re-sends; the retained result is never re-mapped,
    /// cloned or replaced, and a domain-result mutation after freezing does NOT change what the
    /// retransmission sends — the frozen clone is stable.
    /// <para>
    /// REMOVAL PROOF. A retransmitter that handed the snapshot itself to the writer, or re-mapped
    /// the retained result per attempt, is caught because the two sent messages would then be
    /// reference-identical to each other (or to a later mutated mapping) rather than equal
    /// clones; a mapping-failure leak would arm the receipt without a frozen envelope and fail
    /// the TimerCount == 0 assertion after the mapping failure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Retransmission_FrozenSnapshotStableAcrossDomainMutation_CloneNotIdentity()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        var clock = new ManualRetransmissionClock();
        typeof(WorkerService)
            .GetProperty("TimeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, clock);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
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
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var retained = Assert.IsType<TaskResult>(GetRetainedResult(service));
            var original = Assert.Single(writer.Completes);
            Assert.Equal(TaskA, original.Complete.TaskId);

            // THE RETAINED RESULT IS NEVER REPLACED by freezing: identical instance, full payload.
            Assert.Same(retained, GetRetainedResult(service));
            Assert.Equal(TaskOutcome.Completed, retained.Status);
            Assert.Equal(TaskA, retained.TaskId);

            // THE FIRST RETRANSMISSION — a FRESH deep clone of the frozen envelope, not the
            // snapshot itself and not a re-mapped result. Identity is asserted on the RAW writer
            // arguments so the fixture's own defensive clone cannot mask a same-object mutant.
            await clock.WaitForRetryParkedInDelayAsync(1, TestContext.Current.CancellationToken);
            clock.Advance(RetransmissionIntervalFiveSeconds);
            await writer.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var retransmitted = writer.Completes[1];
            var rawOriginal = writer.RawCompletes[0];
            var rawRetransmitted = writer.RawCompletes[1];

            Assert.NotSame(rawOriginal, rawRetransmitted);
            Assert.NotSame(rawOriginal.Complete, rawRetransmitted.Complete);
            Assert.Equal(original.Complete, retransmitted.Complete); // equal CLONE, not identity.

            // A SECOND attempt sends the SAME frozen payload again: a fresh clone of the ONE
            // snapshot, never a re-read or re-map of the retained result. The frozen evidence is
            // stable: every attempt sends an EQUAL, fresh clone of the ONE envelope.
            clock.Advance(RetransmissionIntervalFiveSeconds);
            await writer.CompleteEntered(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var third = writer.Completes[2];
            Assert.Equal(original.Complete, third.Complete);
            Assert.NotSame(rawRetransmitted.Complete, writer.RawCompletes[2].Complete);
            Assert.Equal(3, writer.Completes.Count);
            Assert.Equal(3, clock.TimerCount);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
    /// NO RETRANSMISSION WITHOUT A MAPPED PAYLOAD — on a FULLY WIRED owner. A MISSING result (a
    /// handled provisioning failure) goes through the REAL assignment path, so the assignment
    /// genuinely OWNS an attached, STARTED retry task; that retry nevertheless freezes nothing,
    /// creates NO delay timer through the clock seam and sends NOTHING, and the drain still joins
    /// it before clearing ownership.
    /// <para>
    /// WHY THE WIRED OWNER MATTERS. Asserting "zero timers" on an assignment that never had a
    /// retransmitter proves nothing at all. The retry task is therefore retained and asserted
    /// NON-NULL first — positive evidence the retransmitter exists and was started — so the
    /// zero-timer and zero-send observations are statements about a LIVE retry that chose not to
    /// act, not about an absent one.
    /// </para>
    /// <para>
    /// REMOVAL PROOF. A freeze that fired on the absent-result path (or a retransmitter that
    /// waited without a payload) creates the first delay timer and fails the zero-timer assertion
    /// by name; a retry that outlived the drain fails the retained-task termination assertion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Retransmission_MissingResult_WiredRetryFreezesNothing_NoDelayNoSend_StillJoined()
    {
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new ManualRetransmissionClock();
        typeof(WorkerService)
            .GetProperty("TimeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, clock);
        service.TestProvisioner = FailingProvisioner();

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);
        var loop = InvokeProcessMessages(service, connection, loopCts.Token);

        Task? execution = null;
        Task? reporting = null;
        Task? retry = null;
        try
        {
            reader.Push(Assignment(TaskA));
            reader.Push(Probe("installed"));
            await reader.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // THE OWNER IS FULLY WIRED: the real assignment path attached AND started a retry on
            // this both-flags connection. Capture the CONCRETE task NOW, before any drain can
            // clear the slot, so the termination assertion below can never be vacuous.
            retry = GetActiveRetry(service);
            Assert.NotNull(retry);

            // THE RETRANSMITTER ITSELF, reached through the receipt tracker exactly as production's
            // ACK handler and drains reach it. Captured BEFORE the drain clears ownership, so the
            // frozen-slot assertions below survive the clear.
            var retransmitter = GetActiveRetransmitter(service)
                ?? throw new Xunit.Sdk.XunitException(
                    "A both-flags assignment must own a retransmitter.");

            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Null(GetRetainedResult(service));
            Assert.Empty(writer.Completes);

            // NOTHING WAS FROZEN. An absent result never reaches the mapping, so production's
            // freeze — which happens only on the successful-mapping path — cannot have run. This is
            // the load-bearing observable: a mutant that froze a fabricated envelope without arming
            // would still produce no timer and no send (admission also requires ARMED), so only
            // inspecting the slot itself rejects it.
            Assert.Null(ReadFrozenSlot(retransmitter));

            // THE LIVE RETRY CANNOT ACT: nothing was frozen, so it never creates a delay timer and
            // never sends. Advancing the clock cannot conjure an attempt either.
            clock.Advance(RetransmissionIntervalFiveSeconds * 3);
            var premature = await Record.ExceptionAsync(() =>
                clock.WaitForRetryParkedInDelayAsync(1, TestContext.Current.CancellationToken)
                    .WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(premature);
            Assert.Equal(0, clock.TimerCount);
            Assert.Empty(writer.Completes);
            Assert.Equal(0, writer.ReadyCount);

            // CANCEL THE PROCESS/LOOP TOKEN: the token that still expresses the cancel-and-drain
            // teardown (an EOF with a LIVE token now CARRIES the assignment, so no drain — and no
            // retry join — would run at all). The drain closes retry admission and joins the
            // retained retry task before the ownership clear.
            await loopCts.CancelAsync();
            reader.TryComplete();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // THE EXACT retained retry task terminated as part of the drain, before the clear.
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(retry.IsCompleted, "The drain must join the assignment's retry task.");
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Empty(writer.Completes);

            // STILL NOTHING FROZEN AFTER THE DRAIN — asserted on the retransmitter captured before
            // the ownership clear, so this remains observable once the slot is empty.
            Assert.Null(ReadFrozenSlot(retransmitter));
        }
        finally
        {
            runner.ReleaseAll();
            writer.ReleaseAll();
            reader.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("assignment retry", retry),
                ("loop", loop));
        }
    }

    /// <summary>
    /// NO RETRANSMISSION AFTER A MAPPING FAILURE — on a FULLY WIRED owner whose retry is ATTACHED
    /// and STARTED. A terminal result the production mapper cannot map (an unknown
    /// <c>TaskOutcome</c>) is driven through the REAL <c>ReportAssignmentAsync</c> on a both-flags
    /// connection, with the assignment's REAL <c>CompletionRetry</c> built from production's own
    /// constructor, ATTACHED to the receipt tracker and STARTED on that reporting task — exactly the
    /// wiring the assignment handler performs. The throwing mapper then leaves that LIVE retry
    /// unarmed and frozen with nothing: it creates NO delay timer through the clock seam and sends
    /// NOTHING even after several intervals, while the REAL drain still closes admission and JOINS
    /// the exact retry task before the ownership state is released.
    /// <para>
    /// WHY THE WIRING MATTERS. Asserting "zero delay timers" against an owner that never had a
    /// retransmitter is vacuous — it would hold with the whole feature deleted. Here the retry task
    /// is asserted non-null and live BEFORE the zero-timer/zero-send observations, so those are
    /// statements about a started retransmitter that correctly declined to act.
    /// </para>
    /// <para>
    /// REMOVAL PROOF. Moving the freeze before (or outside) the successful-mapping path arms this
    /// assignment's retry with a payload, so the first delay timer appears and the zero-timer
    /// assertion fails by name; a drain that skipped the retry join leaves the retained task
    /// unterminated and fails the termination assertion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Retransmission_MappingFailure_WiredAndStartedRetry_NeverDelaysNeverSends_StillJoined()
    {
        const string taskId = "task-unmappable-retry";
        const string payloadSecret = "completion-payload-must-not-enter-diagnostic";
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        var clock = new ManualRetransmissionClock();
        typeof(WorkerService)
            .GetProperty("TimeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, clock);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        Task? reporting = null;
        Task? retryTask = null;
        object? retry = null;
        CancellationTokenSource? cts = null;
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

            var gateEnabled = InvokeOrdinaryReadyGateEnabled(connection);
            Assert.True(gateEnabled, "Precondition: the BOTH-FLAGS shape is gated.");

            var slotType = serviceType.GetNestedType("OrdinaryReadySlot", BindingFlags.NonPublic)!;
            var ordinaryReady = Activator.CreateInstance(
                slotType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [connection, CancellationToken.None, ready, receipt],
                culture: null)!;

            // THE REAL RETRANSMITTER, built from production's OWN constructor with the service's
            // OWN send gate and diagnostic reporter, then ATTACHED to the receipt exactly as the
            // assignment handler does. From here the owner is wired like a real gated assignment.
            retry = NewCompletionRetry(service, connection, receipt, clock);
            receiptType.GetMethod("AttachRetry")!.Invoke(receipt, [retry]);
            Assert.Same(retry, receiptType.GetProperty("Retry")!.GetValue(receipt));

            reporting = (Task)serviceType.GetMethod(
                    "ReportAssignmentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [
                    Task.CompletedTask,
                    domainTask,
                    connection,
                    holder,
                    receipt,
                    ordinaryReady,
                ])!;

            // THE RETRY IS STARTED on that ORIGINAL reporting task — production's own Start.
            retryTask = (Task)retry.GetType().GetMethod("Start")!.Invoke(retry, [reporting])!;
            Assert.NotNull(retryTask);

            // PRECONDITION: nothing is frozen before reporting runs, so the post-report assertion
            // below is about what the MAPPING FAILURE did rather than about an empty start state.
            Assert.Null(ReadFrozenSlot(retry));

            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE MAPPING-FAILURE FACTS: UNARMED, NOTHING FROZEN, no Complete written, result
            // retained verbatim, no payload leaked.
            Assert.False(GetReceiptArmed(receipt));
            Assert.False(GetReceiptConfirmed(receipt));

            // THE FROZEN SLOT IS THE LOAD-BEARING OBSERVABLE. Production freezes ONLY on the
            // successful-mapping path, so a mapper that threw must leave this null. A mutant that
            // catches the mapping failure and installs a fabricated envelope WITHOUT arming would
            // satisfy every behavioural assertion below (admission needs ARMED too, so it would
            // still create no timer and send nothing) — only this assertion rejects it.
            Assert.Null(ReadFrozenSlot(retry));

            Assert.Empty(writer.Completes);
            Assert.Same(unmappable, holderType.GetProperty("Result")!.GetValue(holder));
            Assert.DoesNotContain(payloadSecret, stdErr.ToString(), StringComparison.Ordinal);

            // THE LIVE RETRY DECLINES TO ACT: no delay timer through the seam, and no send, even
            // after several whole intervals of virtual time.
            clock.Advance(RetransmissionIntervalFiveSeconds * 3);
            var premature = await Record.ExceptionAsync(() =>
                clock.WaitForRetryParkedInDelayAsync(1, TestContext.Current.CancellationToken)
                    .WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(premature);
            Assert.Equal(0, clock.TimerCount);
            Assert.Empty(writer.Completes);

            // THE REAL DRAIN still closes admission and JOINS the exact retry task.
            cts = new CancellationTokenSource();
            var assignment = NewActiveAssignment(
                taskId, Task.CompletedTask, reporting, retryTask, cts, ready, holder, receipt, ordinaryReady);
            await InvokeDrainAssignmentAsync(service, assignment, cancelFirst: false)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(retryTask.IsCompleted, "The drain must join the started retry task.");
            Assert.True(
                retryTask.IsCompletedSuccessfully,
                "An unarmed retry must terminate cleanly, never faulted.");

            // STILL NOTHING FROZEN AFTER THE DRAIN: the drain closes admission and joins, it never
            // installs a payload, so the slot a failed mapping left empty stays empty.
            Assert.Null(ReadFrozenSlot(retry));

            Assert.Equal(0, clock.TimerCount);
            Assert.Empty(writer.Completes);
        }
        finally
        {
            Console.SetError(originalErr);
            if (reporting is not null)
                await ObserveTaskAsync(reporting);
            if (retryTask is not null)
                await ObserveTaskAsync(retryTask);
            writer.ReleaseAll();
            reader.TryComplete();
            connection.Retire();
            cts?.Dispose();
            service.Dispose();
        }
    }

    /// <summary>
    /// MATCHING CANCEL AT A REAL PRE-MAPPING BARRIER. The REAL message loop owns a fully wired
    /// gated <c>ActiveAssignment</c>: a pending execution task, the REAL reporting method waiting
    /// on it, a real attached-and-started <c>CompletionRetry</c>, and the real readiness/receipt
    /// objects. The matching cancel enters the REAL handler while execution is still pending and
    /// the holder is empty — so reporting has not mapped, frozen or armed anything. Only AFTER the
    /// drain's cancellation boundary is positively observed does the test publish a valid result
    /// and complete execution, letting reporting map/freeze/arm and terminate.
    /// <para>
    /// REMOVAL PROOF. Correct production closes retry admission BEFORE cancellation or any join,
    /// so the subsequently mapped payload cannot arm a delay: the concrete retry task terminates
    /// with ZERO timers. A mutant that moves <c>CloseAdmission</c> until AFTER the reporting join
    /// lets the already-started retry observe the now-frozen/armed report and create its delay;
    /// because this clock is never advanced, the timer-count assertion (and bounded handler return)
    /// fail by name. This differs from a prompt-cancellation vector, where cancellation can prevent
    /// any result from existing and makes the late-close mutant vacuous.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MatchingCancel_PreMappingWiredOwner_ClosesAdmissionBeforeReportingJoin()
    {
        const string taskId = "task-cancel-pre-mapping";
        var runner = new GatedRunner();
        var writer = new GatedWriter();
        var reader = new ChannelResponseReader();
        var service = BuildService(runner);
        var clock = new ManualRetransmissionClock();
        typeof(WorkerService)
            .GetProperty("TimeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, clock);

        var connection = TestConnectionFactory.Attach(
            service, "worker-1", BuildStream(writer, reader), service.TestProvisioner,
            completionReceiptAckEnabled: true, completionReadyRequired: true);

        var serviceType = typeof(WorkerService);
        var holderType = serviceType.GetNestedType("TerminalResultHolder", BindingFlags.NonPublic)!;
        var readyType = serviceType.GetNestedType("ReadyClaim", BindingFlags.NonPublic)!;
        var receiptType = serviceType.GetNestedType("CompletionReceiptTracker", BindingFlags.NonPublic)!;
        var slotType = serviceType.GetNestedType("OrdinaryReadySlot", BindingFlags.NonPublic)!;

        var holder = Activator.CreateInstance(holderType, nonPublic: true)!;
        var ready = Activator.CreateInstance(readyType, nonPublic: true)!;
        var receipt = Activator.CreateInstance(
            receiptType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [connection],
            culture: null)!;
        var ordinaryReady = Activator.CreateInstance(
            slotType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [connection, CancellationToken.None, ready, receipt],
            culture: null)!;

        // Deliberately inline: reporting registered on this task BEFORE the drain can, so completing
        // it runs reporting (and then the retry's earlier reporting continuation) through its
        // pre-mapping window before the drain continuation can resume past its execution/reporting
        // joins. This deterministic registration order is what kills the late-CloseAdmission mutant.
        var executionSource = new TaskCompletionSource();
        var domainTask = GrpcMapper.ToDomain(ResultAssignment(taskId).Assignment);
        var retry = NewCompletionRetry(service, connection, receipt, clock);
        receiptType.GetMethod("AttachRetry")!.Invoke(receipt, [retry]);

        var reporting = (Task)serviceType.GetMethod(
                "ReportAssignmentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [
                executionSource.Task,
                domainTask,
                connection,
                holder,
                receipt,
                ordinaryReady,
            ])!;
        var retryTask = (Task)retry.GetType().GetMethod("Start")!.Invoke(retry, [reporting])!;

        // THE DRAIN-JOIN BARRIER. It includes the real reporting task, then stays incomplete until
        // the test releases it. Production retains `reporting` directly; this transparent wrapper
        // adds only an AFTER-report boundary so the test can observe whether the already-started
        // retry creates a delay while a late-close mutant is still blocked in the reporting join.
        var releaseReportingJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retainedReporting = HoldAfterAsync(reporting, releaseReportingJoin.Task);

        using var cts = new CancellationTokenSource();
        var assignment = NewActiveAssignment(
            taskId, executionSource.Task, retainedReporting, retryTask, cts, ready, holder, receipt, ordinaryReady);
        serviceType.GetMethod("InstallActiveAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [assignment]);

        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cts.Token.Register(() => drainEntered.TrySetResult());
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);
        try
        {
            // THE TRUE PRE-MAPPING STATE: execution pending, holder empty, receipt unarmed,
            // reporting pending, concrete retry started but not waiting, and nothing on the wire.
            Assert.False(executionSource.Task.IsCompleted);
            Assert.Null(holderType.GetProperty("Result")!.GetValue(holder));
            Assert.False(GetReceiptArmed(receipt));
            Assert.False(reporting.IsCompleted);
            Assert.False(retryTask.IsCompleted);
            Assert.Equal(0, clock.TimerCount);
            Assert.Empty(writer.Completes);

            // THE REAL MATCHING-CANCEL HANDLER enters. The assignment CTS callback runs only after
            // DrainAssignmentAsync has executed its pre-cancellation CloseAdmission in correct
            // production, and before it awaits the still-pending execution.
            reader.Push(MatchingCancel(taskId));
            await drainEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.False(executionSource.Task.IsCompleted);
            Assert.False(reporting.IsCompleted);
            Assert.False(retryTask.IsCompleted);
            Assert.Equal(0, clock.TimerCount);

            // NOW publish a valid result and complete execution. Reporting genuinely maps, freezes,
            // arms and writes the original Complete. Correct production's admission was already
            // closed; a late-close mutant creates its first delay here.
            holderType.GetMethod("Publish")!.Invoke(holder, [new TaskResult
            {
                TaskId = taskId,
                Status = TaskOutcome.Completed,
                Output = "mapped-after-cancel-entered",
                Model = domainTask.Model,
            }]);
            executionSource.TrySetResult();

            await writer.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptArmed(receipt), "reporting really mapped and armed the payload.");

            // HOLD the drain in its retained-reporting join and observe the retry independently.
            // Correct production closed admission before reaching this join, so the concrete retry
            // terminates with no timer. A late-close mutant remains blocked on retainedReporting
            // with admission open; the already-started retry creates its timer and this NEGATIVE
            // rendezvous completes instead of timing out.
            var lateTimer = await Record.ExceptionAsync(() =>
                clock.WaitForRetryParkedInDelayAsync(1, TestContext.Current.CancellationToken)
                    .WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(lateTimer);
            Assert.Equal(0, clock.TimerCount);
            Assert.True(retryTask.IsCompletedSuccessfully);

            // Release only after the discriminator has been observed; the real drain can now finish.
            releaseReportingJoin.TrySetResult();
            await retainedReporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await retryTask.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE LOAD-BEARING CLAIM: the concrete retry terminated without ever creating a wait.
            Assert.Equal(0, clock.AdvanceCount);
            Assert.Single(writer.Completes);

            // The cancel's existing fallback Ready completes the real handler; read #2 proves the
            // handler returned and ownership was cleared.
            await writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            writer.ReleaseReady(0);
            await reader.ReadStarted(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));
            Assert.Equal(1, writer.ReadyCount);

            reader.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            registration.Dispose();
            executionSource.TrySetResult();
            releaseReportingJoin.TrySetResult();
            writer.ReleaseAll();
            reader.TryComplete();
            await ObserveTaskAsync(reporting);
            await ObserveTaskAsync(retainedReporting);
            await ObserveTaskAsync(retryTask);
            await ObserveTaskAsync(loop);
            connection.Retire();
            service.Dispose();
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

    /// <summary>
    /// The retained assignment's CONCRETE retry task, or <c>null</c> when the assignment owns none
    /// (a non-gated connection). Callers capture this BEFORE triggering a drain, so a later
    /// termination assertion cannot be satisfied by an already-cleared ownership slot.
    /// </summary>
    private static Task? GetActiveRetry(WorkerService service)
    {
        var owner = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (Task?)owner.GetType().GetProperty("Retry")!.GetValue(owner);
    }

    /// <summary>
    /// Builds production's OWN <c>CompletionRetry</c> for a wired-owner fixture: the same
    /// constructor the assignment handler calls, with the SERVICE'S real send gate and its real
    /// guarded sanitized failure reporter, so nothing about the retransmitter is simulated.
    /// </summary>
    private static object NewCompletionRetry(
        WorkerService service, WorkerConnection connection, object receipt, TimeProvider clock)
    {
        var serviceType = typeof(WorkerService);
        var retryType = serviceType.GetNestedType("CompletionRetry", BindingFlags.NonPublic)!;
        var sendGate = serviceType
            .GetField("_sendGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;

        // The REAL guarded reporter production passes, bound to the REAL service instance.
        var reportMethod = serviceType.GetMethod(
            "ReportRetransmissionFailure", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var reportFailure = Delegate.CreateDelegate(typeof(Action<Exception>), service, reportMethod);

        return Activator.CreateInstance(
            retryType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [connection, CancellationToken.None, receipt, clock, sendGate, reportFailure],
            culture: null)!;
    }

    /// <summary>
    /// THE RETRANSMITTER'S PRIVATE FROZEN SLOT — production's own <c>_frozen</c> field on the
    /// assignment's <c>CompletionRetry</c>, read by reflection. <c>null</c> means NOTHING has been
    /// frozen for that assignment.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS THE LOAD-BEARING OBSERVABLE. "No delay timer" and "no send" are both downstream
    /// of production's <c>AdmissionHolds_Locked</c>, which requires the receipt to be ARMED as well
    /// as a frozen payload to exist. A mutant that freezes a fabricated payload but does NOT arm
    /// therefore produces no timer and no send, and passes every behavioural assertion — while
    /// having violated the "freeze once, on the successful-mapping path only" contract. Inspecting
    /// the frozen slot directly is what makes that mutant observable.
    /// </remarks>
    /// <param name="retransmitter">The assignment's <c>CompletionRetry</c> instance.</param>
    private static object? ReadFrozenSlot(object retransmitter) =>
        retransmitter.GetType()
            .GetField("_frozen", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(retransmitter);

    /// <summary>
    /// The RETAINED assignment's <c>CompletionRetry</c> instance (not its task), reached through the
    /// receipt tracker exactly as production's ACK handler and drains reach it. <c>null</c> when the
    /// assignment owns no retransmitter.
    /// </summary>
    private static object? GetActiveRetransmitter(WorkerService service)
    {
        var owner = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        var receipt = owner.GetType().GetProperty("Receipt")!.GetValue(owner)
            ?? throw new Xunit.Sdk.XunitException("Expected the assignment's receipt tracker.");
        return receipt.GetType().GetProperty("Retry")!.GetValue(receipt);
    }

    /// <summary>
    /// Builds production's OWN <c>ActiveAssignment</c> so a wired-owner fixture can invoke the REAL
    /// drain against it — never a stand-in for the drain's behavior.
    /// </summary>
    private static object NewActiveAssignment(
        string taskId,
        Task execution,
        Task reporting,
        Task? retry,
        CancellationTokenSource cts,
        object readyClaim,
        object terminalResult,
        object receipt,
        object ordinaryReady)
    {
        var ownerType = typeof(WorkerService).GetNestedType("ActiveAssignment", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(
            ownerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [taskId, execution, reporting, retry, cts, readyClaim, terminalResult, receipt, ordinaryReady],
            culture: null)!;
    }

    /// <summary>Invokes the REAL <c>DrainAssignmentAsync</c> for a wired-owner fixture.</summary>
    private static Task InvokeDrainAssignmentAsync(
        WorkerService service, object assignment, bool cancelFirst) =>
        (Task)typeof(WorkerService)
            .GetMethod("DrainAssignmentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [assignment, cancelFirst])!;

    /// <summary>
    /// Retains a real producer's termination and then exposes a deterministic AFTER-producer barrier.
    /// Used only by the pre-mapping matching-cancel vector to hold the real drain's reporting join.
    /// </summary>
    private static async Task HoldAfterAsync(Task producer, Task release)
    {
        await producer;
        await release;
    }

    /// <summary>Awaits a task to quiescence in teardown; the real outcome is asserted on the happy path.</summary>
    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // Quiescent teardown only.
        }
    }

    private static Task OwnerTask(object? active, string property)
    {
        var owner = active
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (Task)owner.GetType().GetProperty(property)!.GetValue(owner)!;
    }

    /// <summary>
    /// Invokes production's OWN gate-selection predicate. This keeps direct-reporting fixtures
    /// wired to the exact predicate the real Assignment handler uses, so mutating
    /// <c>ack &amp;&amp; required</c> to either single flag changes the test's slot shape and is observable.
    /// </summary>
    private static bool InvokeOrdinaryReadyGateEnabled(WorkerConnection connection) =>
        (bool)typeof(WorkerService)
            .GetMethod("OrdinaryReadyGateEnabled", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [connection])!;

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

    private static bool IsCompleteWriteTerminated(object slot) =>
        (bool)slot.GetType()
            .GetField("_completeWriteTerminated", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(slot)!;

    private static void PublishOrdinaryReadyEligibility(object slot) =>
        slot.GetType().GetMethod("PublishEligibility")!.Invoke(slot, null);

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

        /// <summary>
        /// THE RAW WRITER ARGUMENTS for Complete writes — the EXACT objects production handed to
        /// <c>WriteAsync</c>, retained BY REFERENCE with no fixture-side clone.
        /// </summary>
        /// <remarks>
        /// <see cref="Completes"/> keeps a defensive clone for value assertions, but that clone
        /// would MASK a production mutant that hands the SAME frozen <c>TaskComplete</c> to every
        /// writer (two clones of one object are still two objects). Ownership assertions therefore
        /// use this raw list, and additionally MUTATE a delivered raw argument to reject any shared
        /// nested collection.
        /// </remarks>
        private readonly List<WorkerMessage> _rawCompletes = [];
        private readonly List<WorkerMessage> _readies = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyCountWaiters = [];
        private bool _releaseImmediately;
        private Exception? _failCompleteAtIndexZero;
        private Exception? _failNextReadyWrite;

        internal bool HoldCompletes { get; init; }

        /// <summary>
        /// ONE-SHOT failure for the next Ready write, applied after its explicit release. The
        /// attempted message remains recorded, so no-retry and monotonic-authorization assertions
        /// can distinguish a faulted original task from no write at all.
        /// </summary>
        internal Exception? FailNextReadyWrite
        {
            get { lock (_gate) return _failNextReadyWrite; }
            set { lock (_gate) _failNextReadyWrite = value; }
        }

        internal Exception? FailCompleteAtIndexZero
        {
            get { lock (_gate) return _failCompleteAtIndexZero; }
            set { lock (_gate) _failCompleteAtIndexZero = value; }
        }

        internal Action<int, OperationCanceledException>? OnCompleteCancelled { get; set; }

        internal IReadOnlyList<WorkerMessage> Completes
        {
            get { lock (_gate) return [.. _completes]; }
        }

        /// <summary>
        /// The RAW writer arguments for Complete writes, oldest first — the exact objects
        /// production passed to <c>WriteAsync</c>, never cloned by this fixture.
        /// </summary>
        internal IReadOnlyList<WorkerMessage> RawCompletes
        {
            get { lock (_gate) return [.. _rawCompletes]; }
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

                    // THE RAW ARGUMENT, retained BY REFERENCE: cloning it here would make a
                    // "same frozen object handed to every writer" mutant indistinguishable.
                    _rawCompletes.Add(message);
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
                catch (OperationCanceledException cancelled)
                {
                    OnCompleteCancelled?.Invoke(index, cancelled);
                    throw;
                }

                if (failure is OperationCanceledException injectedCancellation)
                {
                    // A test-injected CANCELLED local termination is reported from inside the
                    // actual cancellable writer path, immediately before that original write task
                    // terminates as Canceled. The loop token itself stays live, so the separately
                    // authorized Ready can still be written afterwards.
                    OnCompleteCancelled?.Invoke(index, injectedCancellation);
                    throw injectedCancellation;
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
            Exception? readyFailure;
            lock (_gate)
            {
                readyIndex = _readies.Count;
                _readies.Add(message.Clone());
                readyEntered = Slot(_readyEntered, readyIndex);
                readyRelease = Slot(_readyRelease, readyIndex);
                readyFailure = _failNextReadyWrite;
                _failNextReadyWrite = null;
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

            if (readyFailure is not null)
                throw readyFailure;
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

    /// <summary>
    /// A diagnostic writer that throws on one marker and forwards everything else. The throw is
    /// used to enter reporting's real catch after the inner write-termination finally has run.
    /// </summary>
    private sealed class MarkerThrowingWriter(
        string marker, TextWriter inner, Exception failure) : TextWriter
    {
        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            if (value?.Contains(marker, StringComparison.Ordinal) == true)
                throw failure;
            inner.WriteLine(value);
        }

        public override void Write(string? value) => inner.Write(value);
        public override void Write(char value) => inner.Write(value);
    }

    /// <summary>
    /// A diagnostic writer that blocks ONE marker line synchronously on the production logging
    /// stack. This creates a deterministic window after the Complete write's termination fact is
    /// published but before reporting's outer finally publishes eligibility — no sleep, polling or
    /// production hook.
    /// </summary>
    private sealed class MarkerBlockingWriter(string marker, TextWriter inner) : TextWriter
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            if (value?.Contains(marker, StringComparison.Ordinal) == true)
            {
                Entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
            }

            inner.WriteLine(value);
        }

        public override void Write(string? value) => inner.Write(value);

        public override void Write(char value) => inner.Write(value);

        internal void Release() => _release.TrySetResult();
    }

    private sealed class GatedWritePrimaryException(string message) : Exception(message);

    private sealed class AuthorizedReadyFailureException(string message) : Exception(message);

    /// <summary>Sentinel thrown by the success diagnostic to reach the outer reporting catch.</summary>
    private sealed class EligibilityDiagnosticException : Exception;

    /// <summary>An agent-boundary failure the REAL executor maps into a FAILED result.</summary>
    private sealed class GatedAgentFailureException(Exception inner)
        : Exception("injected agent failure", inner);

    /// <summary>A reader fault whose IDENTITY the loop must propagate unchanged.</summary>
    private sealed class GateReaderPrimaryException(string message) : Exception(message);

    /// <summary>A deferred cancellation-callback failure the teardown must propagate without a primary.</summary>
    private sealed class GateDeferredCancellationException(string message) : Exception(message);
}