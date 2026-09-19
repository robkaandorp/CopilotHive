using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE WORKER-SIDE RETRANSMISSION CONTRACT, driven through the REAL
/// <c>WorkerService.ProcessMessagesAsync</c> loop and the REAL completion-receipt ACK handler.
/// <para>
/// WHAT THIS FIXTURE PINS, per cell:
/// <list type="bullet">
///   <item>the QUIET-STREAM retry: with NO response traffic at all, the retry's delay is created
///   through the production <c>TimeProvider</c> seam, exactly ONE execution occurs, and the
///   retransmitted <c>Complete</c> is a fresh clone carrying the SAME full evidence;</item>
///   <item>the frozen envelope is INSENSITIVE to later IN-PLACE mutation of the retained domain
///   result's nested collections (<c>Metrics.Issues</c>, <c>GitStatus.ChangedFiles</c>);</item>
///   <item>NO retry while the original reporting/Complete write is still PENDING;</item>
///   <item>an EARLY ACK (while the Complete is held) suppresses every retry;</item>
///   <item>an ACK arriving DURING the pending retry delay, and one arriving while a retry is QUEUED
///   behind a competing admitted send, each prevents that retry's transport invocation;</item>
///   <item>an ACK arriving while a retry is ALREADY ADMITTED and HELD: the loop still consumes a
///   probe, the admitted write is neither completed nor released by the ACK, and the ordinary Ready
///   follows that write's OWN termination even when the Ready write itself FAILS;</item>
///   <item>repeated missing-ACK attempts stay STRICTLY SEQUENTIAL, and a FAILED attempt is retried;</item>
///   <item>CONTROLS: a missing result and non-both-flags connections produce no retry at all;</item>
///   <item>TEARDOWN BOUNDARIES: stop-before-payload, mid-delay, queued-on-the-gate and admitted
///   states all close retry admission, cancel the pending wait and JOIN before clearing;</item>
///   <item>the SUCCESSOR-ASSIGNMENT and SEQUENTIAL-RUN boundaries get their OWN retry and never
///   replay the predecessor's evidence.</item>
/// </list>
/// </para>
/// <para>
/// TEST QUALITY CONTRACT. Every milestone is a <see cref="TaskCompletionSource"/> gate, a counted
/// write or the production send gate's own waiter queue, each with a bounded FAILURE bound; nothing
/// here sleeps or polls as an ordering device, and every suppressed behavior is observed with a
/// bounded POSITIVE non-completion wait. Nothing asserts on a cancellation token alone. Every started
/// task — the loop, the execution, the reporting, the retry and every readiness write — is hoisted
/// and joined in <c>finally</c>.
/// </para>
/// <para>
/// NO PRODUCTION TEST HOOKS BEYOND THE CLOCK: the harness drives production's own internal
/// <c>TimeProvider</c> seam (the same dependency production resolves to <c>TimeProvider.System</c>)
/// and otherwise reaches production only through its real loop, its real members and reflected
/// OBSERVATION of the assignment owner and of the production send gate.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerCompletionRetransmissionTests
{
    /// <summary>Bound on every await a regression could otherwise block forever.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The bounded POSITIVE NON-COMPLETION window: a suppressed retry must still be absent after
    /// this wait, which is what makes the suppression a real observation.
    /// </summary>
    private static readonly TimeSpan SuppressionBound = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// THE PRODUCTION FIVE-SECOND INTERVAL, read from production's own constant so this fixture
    /// never hard-codes an approximation the retry does not actually use.
    /// </summary>
    private static readonly TimeSpan ProductionInterval = ReadProductionInterval();

    private const string TaskA = "task-retry-A";
    private const string TaskB = "task-retry-B";
    private const string AssignedModel = "model-retransmission";
    private const string AssignedWorkerId = "worker-1";
    private const string RetransmissionDiagnostic = "Completion retransmission failed";

    /// <summary>The EXISTING drain-fault diagnostic a faulted joined task is reported through.</summary>
    private const string DrainFaultDiagnostic = "Task drain observed a fault";

    // ══════════════════════════════════════════════════════════════════════════
    // 1. The quiet-stream retry: one execution, full frozen evidence.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE QUIET-STREAM TIMED RETRY. With NO response message after the assignment, the retry's
    /// first delay is created through the production clock seam, exactly ONE execution runs, and one
    /// production interval drives ONE retransmission whose <c>Complete</c> is a FRESH clone carrying
    /// the SAME full evidence as the original. A second interval then produces the NEXT attempt.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. Deleting the retry leaves the delay timer uncreated and the retransmission
    /// rendezvous failing BY NAME; a retry that re-mapped the retained result per attempt keeps the
    /// count but fails the mutation cell below.
    /// </remarks>
    [Fact]
    public async Task QuietStream_NoAck_RetransmitsOncePerInterval_WithOneExecution()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();

            // NO further orchestrator traffic from here on: the response stream stays quiet.
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);
            Assert.Equal(1, harness.ExecutionCount);

            var original = harness.Completes[0];

            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(1);
            var retransmission = harness.Completes[1];

            // THE SAME COMPLETE EVIDENCE, as a FRESH clone (never the same object).
            Assert.NotSame(original, retransmission);
            Assert.NotSame(original.Complete, retransmission.Complete);
            Assert.Equal(AssignedWorkerId, retransmission.WorkerId);
            Assert.Equal(original.Complete, retransmission.Complete);

            // STILL EXACTLY ONE EXECUTION: the retry re-sends frozen evidence, never re-runs work.
            Assert.Equal(1, harness.ExecutionCount);
            Assert.Single(harness.PromptStarts);

            // The retry parked in a FRESH delay after that attempt.
            await harness.RetryDelayCreatedAsync(2);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// THE FROZEN ENVELOPE IS INSENSITIVE TO LATER IN-PLACE MUTATION OF THE DOMAIN RESULT'S NESTED
    /// COLLECTIONS. After the original mapping this fixture mutates the RETAINED
    /// <c>TaskResult</c>'s own mutable collections (<c>Metrics.Issues</c> and
    /// <c>GitStatus.ChangedFiles</c>) and the retransmission still carries the ORIGINAL values in the
    /// ORIGINAL ORDER.
    /// </summary>
    /// <remarks>
    /// WHY THE MUTATION IS THE DISCRIMINATOR. <c>TaskResult</c> is a record whose nested
    /// <c>Metrics.Issues</c> / <c>GitStatus.ChangedFiles</c> are MUTABLE <c>List&lt;string&gt;</c>
    /// instances. A retry that kept a reference into the mapped payload — or that re-mapped the
    /// retained result per attempt — would send the MUTATED collections. The deep clone captured at
    /// mapping time cannot.
    /// </remarks>
    [Fact]
    public async Task Retransmission_SurvivesInPlaceMutationOfDomainNestedCollections()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            // THE FROZEN, MAPPED EVIDENCE as the original send carried it.
            var original = harness.Completes[0];
            var originalIssues = original.Complete.Metrics.Issues.ToArray();
            var originalChangedFiles = original.Complete.GitStatus.ChangedFiles.ToArray();
            Assert.NotEmpty(originalIssues);
            Assert.NotEmpty(originalChangedFiles);

            // NON-VACUITY: the real executor really produced these nested collections, so the
            // mutation below has something real to change.
            var retained = harness.RetainedResult
                ?? throw new Xunit.Sdk.XunitException("The executor must retain a terminal result.");
            Assert.Equal(originalIssues, retained.Metrics!.Issues);
            Assert.Equal(originalChangedFiles, retained.GitStatus!.ChangedFiles);

            // THE MUTATION — in place, on the RETAINED domain evidence, AFTER the mapping.
            retained.Metrics.Issues.Clear();
            retained.Metrics.Issues.Add("MUTATED-AFTER-MAPPING");
            retained.GitStatus.ChangedFiles.Clear();
            retained.GitStatus.ChangedFiles.Add("mutated/after/mapping.cs");

            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(1);
            var retransmission = harness.Completes[1];

            // THE RETRANSMISSION STILL CARRIES THE FROZEN VALUES, IN ORDER.
            Assert.Equal(originalIssues, retransmission.Complete.Metrics.Issues);
            Assert.Equal(originalChangedFiles, retransmission.Complete.GitStatus.ChangedFiles);
            Assert.DoesNotContain("MUTATED-AFTER-MAPPING", retransmission.Complete.Metrics.Issues);

            // …and the retained result keeps its EXACT identity (never cloned or replaced).
            Assert.Same(retained, harness.RetainedResult);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. No retry while reporting is pending; early ACK suppression.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// NO RETRY WHILE THE ORIGINAL REPORTING IS STILL PENDING. The original Complete is HELD, so
    /// reporting has not terminated and the retry has not even begun to wait: advancing the clock by
    /// several whole intervals produces NO retry delay and NO second Complete.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. A retry that did not observe reporting's termination parks its delay here
    /// (TimerCount &gt; 0) and, once advanced, sends a second Complete — both fail by name.
    /// </remarks>
    [Fact]
    public async Task NoRetry_WhileTheOriginalReportingWriteIsStillPending()
    {
        var harness = new RetryHarness { HoldOriginalComplete = true };
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);

            // The ORIGINAL Complete write is PARKED: reporting is still inside it.
            await harness.CompleteEnteredAsync(0);
            Assert.False(harness.Reporting.IsCompleted);

            await AssertNoRetryStartedAsync(harness, "while the original Complete write is held");
            Assert.Single(harness.Completes);

            // Releasing the write lets reporting terminate; only THEN does the retry park.
            harness.ReleaseComplete(0);
            await harness.JoinedReportingAsync();
            await harness.RetryDelayCreatedAsync(1);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// AN EARLY ACK — arriving while the ORIGINAL Complete write is still held — SUPPRESSES EVERY
    /// RETRY: the receipt is latched, retry admission closes, and advancing the clock by several
    /// intervals produces no delay timer and no second Complete.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. Without the ACK-side admission close the retry parks and then sends once the
    /// write terminates; the bounded non-completion window and the one-Complete assertion fail by
    /// name.
    /// </remarks>
    [Fact]
    public async Task EarlyAckDuringHeldOriginalWrite_SuppressesEveryRetry()
    {
        var harness = new RetryHarness { HoldOriginalComplete = true };
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);

            // THE EARLY ACK, with the original write provably still pending.
            harness.PushAck(TaskA);
            harness.PushProbe("early-ack-returned");
            await harness.ConsumedAsync(3);
            Assert.True(harness.ReceiptConfirmed);

            harness.ReleaseComplete(0);
            await harness.JoinedReportingAsync();

            // NO RETRY WAS EVER ADMITTED.
            await AssertNoRetryStartedAsync(harness, "after an early ACK closed the receipt");

            // The ACK also authorizes the ordinary ready exactly once — existing behavior kept.
            await harness.ReadyEnteredAsync(0);
            harness.ReleaseReady(0);
            await harness.JoinedReadyWriteAsync();
            Assert.Equal(1, harness.ReadyCount);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. ACK during the delay, and ACK while a retry is queued on the send gate.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN ACK ARRIVING DURING THE PENDING RETRY DELAY CANCELS THAT WAIT. The delay existed (positive
    /// proof the retry was parked), the retry task then reaches termination, and NOTHING is ever
    /// retransmitted even after the whole interval elapses.
    /// </summary>
    [Fact]
    public async Task AckDuringThePendingRetryDelay_PreventsTheRetryInvocation()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);

            // The retry is PROVABLY parked in its delay.
            await harness.RetryDelayCreatedAsync(1);
            Assert.False(harness.Retry.IsCompleted);

            harness.PushAck(TaskA);
            harness.PushProbe("delay-ack-returned");
            await harness.ConsumedAsync(3);

            // The parked wait is cancelled by the retry's own lifetime, so the task ends. The
            // ownership slot still holds the assignment, so the retry task is directly joinable.
            await harness.JoinedRetryAsync();

            // …and NOTHING was ever retransmitted, even after the whole interval elapses.
            await harness.AdvanceOneIntervalAsync();
            await AssertNoRetrySendAsync(harness, "after the ACK cancelled the pending delay");
            Assert.Single(harness.Completes);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// AN ACK ARRIVING WHILE THE RETRY IS QUEUED ON THE PRODUCTION SEND GATE PREVENTS THAT RETRY'S
    /// TRANSPORT INVOCATION. A competing admitted send holds the gate, the retry reaches the permit
    /// queue (proved by the production gate's own waiter count), the ACK lands, and releasing the
    /// gate makes the post-permit arbitration refuse — so no transport is invoked.
    /// </summary>
    /// <remarks>
    /// THE CELL THAT DISCRIMINATES "CHECK BEFORE THE WAIT" FROM "ARBITRATE AFTER THE PERMIT". A
    /// production that merely checked the ACK before waiting for the permit would still send here,
    /// because its check ran before the ACK arrived.
    /// </remarks>
    [Fact]
    public async Task AckWhileRetryIsQueuedOnTheSendGate_PreventsInvocationAfterThePermit()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            // THE COMPETING ADMITTED SEND: the production permit is held, so the retry must QUEUE.
            await harness.HoldSendGateAsync();

            await harness.AdvanceOneIntervalAsync();
            await harness.RetryQueuedOnSendGateAsync();

            // THE ACK LANDS WHILE THE RETRY IS STILL QUEUED.
            harness.PushAck(TaskA);
            harness.PushProbe("queued-ack-returned");
            await harness.ConsumedAsync(3);

            // Release the permit: the retry acquires it, arbitrates and must NOT write.
            harness.ReleaseSendGate();
            await harness.JoinedRetryAsync();

            await AssertNoRetrySendAsync(harness, "after the queued retry acquired the permit");
            Assert.Single(harness.Completes);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. ACK during an ADMITTED held retry; Ready follows the write's termination.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ACK DURING AN ALREADY-ADMITTED, HELD RETRY. The retry's own transport write is held by
    /// the writer; the ACK arrives and the loop still CONSUMES a probe (proving the handler ran),
    /// but the admitted write is neither completed nor released by the ACK. Only that write's OWN
    /// termination then authorizes the single ordinary Ready — and that Ready's write FAILS, whose
    /// fault must be reported sanitized rather than converted into anything else.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. A production that cancelled an admitted write on the ACK would let the retry
    /// terminate while the writer is still parked — failing the bounded non-completion assertion —
    /// and a production that withheld authorization on a FAILED Ready would emit no Ready at all.
    /// </remarks>
    [Fact]
    public async Task AckDuringAdmittedHeldRetry_DoesNotReleaseIt_ReadyFollowsEvenWhenItsWriteFails()
    {
        var readyFailure = new ReadyWriteFailureException("raw ready failure must stay sanitized");
        var harness = new RetryHarness
        {
            HoldRetries = true,
            HoldReadies = true,
            FailFirstReadyWrite = readyFailure,
        };

        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(1);

            // THE RETRY WRITE IS ADMITTED AND HELD.
            Assert.False(harness.Retry.IsCompleted);

            // THE ACK LANDS while that write is admitted and held.
            harness.PushAck(TaskA);
            harness.PushProbe("admitted-ack-returned");
            await harness.ConsumedAsync(3);
            Assert.True(harness.ReceiptConfirmed);

            // THE ADMITTED WRITE IS NEITHER COMPLETED NOR RELEASED BY THE ACK — observed with a
            // bounded positive non-completion window while the loop keeps consuming.
            harness.PushProbe("post-ack-window");
            await harness.ConsumedAsync(4);
            var stillHeld = await Record.ExceptionAsync(() =>
                harness.JoinedRetryAsync().WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(stillHeld);
            Assert.False(harness.Retry.IsCompleted, "The ACK must never cancel an admitted transport write.");
            Assert.False(harness.RetryWriteCompleted(1));

            // RELEASE the admitted retry write into SUCCESS: only now can the single Ready start.
            harness.ReleaseComplete(1);
            await harness.JoinedRetryAsync();

            // THE ORDINARY READY FOLLOWS THE TERMINATED WRITE, and its OWN write fails. The Ready
            // write is HELD, so its fault is raised from inside a genuine suspension — exactly as a
            // real gRPC writer's fault is — and observed by the drain's guarded sanitized report.
            await harness.ReadyEnteredAsync(0);
            harness.ReleaseReady(0);

            // The loop's teardown drain joins that faulted write and reports it.
            harness.CompleteStream();
            await harness.JoinAsync();
            await harness.WaitForDrainFaultDiagnosticAsync();

            var diagnostics = harness.Diagnostics;
            Assert.Contains("Task drain observed a fault", diagnostics, StringComparison.Ordinal);
            Assert.Contains(nameof(ReadyWriteFailureException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(readyFailure.Message, diagnostics, StringComparison.Ordinal);

            // Still exactly two Completes: the original and the ONE admitted retry, never a third.
            Assert.Equal(2, harness.Completes.Count);
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// THE ORDINARY READY SERIALIZES BEHIND AN ADMITTED RETRY ON THE SAME SEND PERMIT. The retry's
    /// own transport write is admitted and HELD, the exact ACK authorizes the ordinary Ready, and
    /// the Ready write must then QUEUE on the production send gate — reached only after the retry's
    /// admitted write terminates and releases the permit — and enter only after that release.
    /// <para>
    /// REMOVAL PROOF. With the shared-permit serialization removed (a second gate, or a bypass),
    /// the Ready writes straight through while the retry is still parked: the queued-waiter
    /// observation fails, or the overlap-detecting writer records the overlap. A production that
    /// arbitrated the Ready BEFORE acquiring the permit would write it while the retry is parked,
    /// failing the zero-Ready observation at the queued barrier.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OrdinaryReady_SerializesBehindAnAdmittedHeldRetry_OnTheSameSendGate()
    {
        var harness = new RetryHarness { HoldRetries = true };
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            // THE RETRY IS ADMITTED AND HELD in the writer — it owns the production send permit
            // (the send releases it only after the write terminates, so the permit is BUSY here).
            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(1);
            Assert.False(harness.Retry.IsCompleted);
            Assert.False(harness.RetryWriteCompleted(1));
            Assert.Equal(0, harness.SendGateCurrentCount);

            // THE EXACT ACK lands while the admitted retry is still held. It latches the receipt
            // and authorizes the ordinary Ready — which must then QUEUE on the SAME permit.
            harness.PushAck(TaskA);
            harness.PushProbe("admitted-ack-returned");
            await harness.ConsumedAsync(3);
            Assert.True(harness.ReceiptConfirmed);

            // THE READY IS AUTHORIZED — its slot write is STARTED — and is PROVABLY PARKED ON THE
            // PERMIT QUEUE, not on the wire: the gate's own waiter list reports one waiter, and the
            // wire still has exactly the two Completes and NO Ready. The slot write's existence is
            // the positive proof the settlement already authorized it (it cannot write before the
            // permit, so its own entry milestone only fires after the release below).
            var slotWrite = harness.ReadinessSlotWrite
                ?? throw new Xunit.Sdk.XunitException("The authorized Ready write must be retained.");
            await harness.RetryQueuedOnSendGateAsync();
            Assert.Equal(2, harness.Completes.Count);
            Assert.Equal(0, harness.ReadyCount);

            // POSITIVE NON-COMPLETION with a bounded window: with the permit still held the queued
            // Ready cannot enter the wire, so its writer entry stays absent beyond the bound.
            var premature = await Record.ExceptionAsync(() =>
                harness.ReadyWriteEnteredAfterRetryTerminatedAsync()
                    .WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(premature);
            Assert.Equal(0, harness.ReadyCount);

            // RELEASE the admitted retry: the permit is released only by the write's own unwind,
            // so the queued Ready can only NOW enter — strictly after the retry write completed.
            harness.ReleaseComplete(1);
            await harness.JoinedRetryAsync();
            Assert.True(harness.RetryWriteCompleted(1), "the admitted retry write must have completed.");
            await slotWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await harness.ReadyWriteEnteredAfterRetryTerminatedAsync();

            // The Ready write finally happens — ONE Ready, after the retry.
            harness.ReleaseReady(0);
            await slotWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, harness.ReadyCount);
            Assert.Equal(2, harness.Completes.Count);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5. Sequentially bounded attempts, and a failed attempt that retries.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// REPEATED MISSING-ACK ATTEMPTS STAY STRICTLY SEQUENTIAL. Each advance of exactly one interval
    /// produces exactly ONE attempt and exactly ONE FRESH delay afterwards — never a catch-up burst,
    /// and never more than one delay or one attempt outstanding.
    /// </summary>
    [Fact]
    public async Task RepeatedMissingAckAttempts_StayStrictlySequential()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var delaysBefore = harness.Clock.TimerCount;
                await harness.AdvanceOneIntervalAsync();
                await harness.CompleteEnteredAsync(attempt);

                // EXACTLY ONE new attempt and EXACTLY ONE new delay, in order.
                Assert.Equal(attempt + 1, harness.Completes.Count);
                await harness.RetryDelayCreatedAsync(delaysBefore + 1);
                Assert.Equal(delaysBefore + 1, harness.Clock.TimerCount);
            }

            Assert.Equal(4, harness.Completes.Count);
            Assert.Equal(1, harness.ExecutionCount);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// A FAILED RETRY ATTEMPT DOES NOT END THE RETRY: the failed write is reported through the
    /// existing guarded sanitized diagnostic (TYPE only, never the raw message), the retry parks in a
    /// FRESH interval, and the NEXT attempt really is sent.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. A retry that treated a failed write as terminal stops after the first failure,
    /// so the next attempt's entry never arrives and the assertion fails by name.
    /// </remarks>
    [Fact]
    public async Task FailedRetryAttempt_IsReported_AndTheNextAttemptIsReallySent()
    {
        var injected = new RetryWriteFailureException("injected retry write failure");
        var harness = new RetryHarness { FailNextRetryWrite = injected };

        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            // THE FIRST RETRY FAILS: the write is admitted (recorded) and then throws.
            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(1);

            // GUARDED, SANITIZED, TYPE-ONLY diagnostic: no raw message leaks.
            await harness.WaitForSanitizedDiagnosticAsync();
            var diagnostics = harness.Diagnostics;
            Assert.Contains(RetransmissionDiagnostic, diagnostics, StringComparison.Ordinal);
            Assert.Contains(nameof(RetryWriteFailureException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(injected.Message, diagnostics, StringComparison.Ordinal);

            // A FRESH interval was armed after that failure.
            await harness.RetryDelayCreatedAsync(2);

            // THE NEXT ATTEMPT REALLY HAPPENS.
            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(2);
            Assert.Equal(3, harness.Completes.Count);
            Assert.Equal(1, harness.ExecutionCount);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6. Controls: no payload, and non-both-flags connections.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// NO PAYLOAD, NO RETRY. A handled provisioning failure produces NO result, so nothing is
    /// frozen, no delay is ever created and no Complete is ever written — in EVERY negotiation
    /// mode. The loop stays healthy throughout.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingResult_NeverFreezesOrRetries(bool readyRequired, bool ackEnabled)
    {
        var harness = new RetryHarness
        {
            ReadyRequired = readyRequired,
            AckEnabled = ackEnabled,
            FailProvisioning = true,
        };

        try
        {
            await harness.StartAsync();
            await harness.AwaitOwnerInstalledAsync();
            await harness.JoinedExecutionAsync();
            await harness.JoinedReportingAsync();

            Assert.Null(harness.RetainedResult);

            // POSITIVE NON-COMPLETION: no delay exists and nothing was written, in every mode.
            await AssertNoRetryStartedAsync(harness, "for an assignment that produced no result");
            Assert.Empty(harness.Completes);

            // The loop is still healthy and consuming.
            harness.PushProbe("after-suppression");
            await harness.ConsumedAsync(3);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// NON-BOTH-FLAGS CONNECTIONS NEVER BUILD A RETRY: legacy (00), ACK-only (01) and Ready-only (10)
    /// each produce NO retry task and NO delay timer, while their EXISTING ordinary Ready behavior is
    /// unchanged (one Ready, from the existing gate).
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. Removing the both-flags gate installs a retry on these rows, so their retry
    /// task is non-null and their delay count is non-zero — both fail by name.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NonBothFlagsConnections_NeverBuildARetry(bool readyRequired, bool ackEnabled)
    {
        var harness = new RetryHarness { ReadyRequired = readyRequired, AckEnabled = ackEnabled };
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.JoinedReportingAsync();

            // NO RETRY TASK ON THE ASSIGNMENT OWNER AT ALL.
            Assert.Null(harness.RetryTask);

            // …and the ordinary (ungated) Ready still happens exactly once.
            await harness.ReadyEnteredAsync(0);
            harness.ReleaseReady(0);
            await harness.JoinedReadyWriteAsync();
            Assert.Equal(1, harness.ReadyCount);

            await AssertNoRetryStartedAsync(harness, "on a non-both-flags connection");
            Assert.Single(harness.Completes);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7. Teardown boundaries.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// STOP BEFORE THE PAYLOAD: a MATCHING CANCEL arrives while reporting has NOT yet published its
    /// payload (the original Complete is held). The drain still closes retry admission, cancels the
    /// pending wait and JOINS the retry — with NO acknowledgement involved — and the cancel's
    /// existing single fallback Ready is preserved.
    /// </summary>
    [Fact]
    public async Task MatchingCancelBeforePayload_ClosesAndJoinsTheRetryWithoutAnyAck()
    {
        var harness = new RetryHarness { HoldOriginalComplete = true };
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);

            // The payload is NOT published yet (the Complete write is still parked).
            Assert.False(harness.Reporting.IsCompleted);
            var retry = harness.Retry;

            harness.PushCancel(TaskA);

            // The cancel is READ (the loop re-arms its next read only after the handler returns), so
            // this is the positive proof the handler was entered — and it stays parked there.
            await harness.ConsumedAsync(1);

            // THE DRAIN IS PARKED ON THE UNPUBLISHED REPORTING: the matching-cancel drain always
            // joins reporting, so with the Complete write still held the loop cannot reach the
            // ownership clear. Nothing has been settled and no delay exists yet.
            var parked = await Record.ExceptionAsync(() =>
                harness.JoinAsync().WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(parked);
            Assert.Equal(0, harness.Clock.TimerCount);
            Assert.Equal(1, harness.SlotOccupancy);

            // RELEASING the Complete lets reporting terminate; the drain then closes retry admission,
            // cancels the (never-created) wait and JOINS the retry — with NO acknowledgement involved.
            harness.ReleaseComplete(0);
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(retry.IsCompletedSuccessfully, "The drain must join the retry cleanly, with no ACK.");

            // The existing cancel fallback Ready is preserved exactly once. It is the cancel
            // handler's OWN direct write (not a slot-owned one), so its completion is observed
            // through a trailing probe that the loop can only read AFTER the handler returned.
            await harness.ReadyEnteredAsync(0);
            harness.ReleaseReady(0);
            harness.PushProbe("after-cancel-ready");
            await harness.ConsumedAsync(2);
            Assert.Equal(1, harness.ReadyCount);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// MID-DELAY TEARDOWN: an EOF with the receipt permanently unconfirmed cancels the retry's
    /// parked five-second delay and joins it, emitting NO ordinary Ready — and the NEVER-ADVANCED
    /// clock is the positive witness that the retry really was parked.
    /// </summary>
    [Fact]
    public async Task EofDuringPendingRetryDelay_ClosesCancelsAndJoinsWithoutReady()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);

            // The retry is PROVABLY parked (its delay exists); the clock is NEVER advanced.
            await harness.RetryDelayCreatedAsync(1);
            Assert.False(harness.Retry.IsCompleted);

            harness.CompleteStream();
            await harness.JoinAsync();

            // Joined, and nothing was retransmitted or readied.
            await harness.JoinedRetryAsync();
            Assert.Single(harness.Completes);
            Assert.Equal(0, harness.ReadyCount);
            Assert.Equal(0, harness.Clock.AdvanceCount);
            Assert.Equal(0, harness.SlotOccupancy);
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// QUEUED-ON-THE-GATE TEARDOWN: with the production permit held and the retry PARKED ON THE
    /// PERMIT QUEUE, an EOF cancels the permit acquisition (never a transport write) and joins the
    /// retry before the ownership clear.
    /// </summary>
    [Fact]
    public async Task EofWhileRetryIsQueuedOnTheGate_CancelsThePermitWaitAndJoins()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            await harness.HoldSendGateAsync();
            await harness.AdvanceOneIntervalAsync();
            await harness.RetryQueuedOnSendGateAsync();

            // EOF while the retry is queued: teardown must still converge.
            harness.CompleteStream();
            await harness.JoinAsync();
            await harness.JoinedRetryAsync();

            // No retransmission was ever invoked.
            Assert.Single(harness.Completes);
            Assert.Equal(0, harness.SlotOccupancy);
        }
        finally
        {
            harness.ReleaseSendGate();
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// ADMITTED-WRITE TEARDOWN: with the retry's own transport write HELD, an EOF genuinely JOINS it
    /// under the EXISTING stream token — the drain awaits the admitted write exactly as the original
    /// Complete/Ready writes are awaited today — and only then clears ownership.
    /// </summary>
    [Fact]
    public async Task EofDuringAdmittedHeldRetry_JoinsTheAdmittedWriteBeforeClearing()
    {
        var harness = new RetryHarness { HoldRetries = true };
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(1);

            // EOF while the admitted write is HELD: the loop must NOT finish yet.
            harness.CompleteStream();
            var premature = await Record.ExceptionAsync(() =>
                harness.Loop.WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(premature);
            Assert.False(harness.RetryWriteCompleted(1));
            Assert.Equal(1, harness.SlotOccupancy);

            // RELEASE: the join observes the write's own termination, then the clear happens.
            harness.ReleaseComplete(1);
            await harness.JoinedRetryAsync();
            await harness.JoinAsync();

            Assert.Equal(0, harness.SlotOccupancy);
            Assert.Equal(2, harness.Completes.Count);
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// READER-FAULT TEARDOWN WITH A PARKED RETRY: a reader fault whose identity the loop propagates
    /// arrives while the retry is parked in its never-advanced five-second delay. The drain must
    /// close retry admission, cancel the pending delay BEFORE joining, join the retry and the
    /// original tasks, and propagate the reader's ORIGINAL exception — with NO acknowledgement and
    /// NO ordinary Ready. The un-advanced clock is the positive witness the retry really was parked
    /// when the fault armed.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. A drain that skipped the admission close (or ordered it after the joins)
    /// leaves the retry parked in a delay only the un-advanced clock could satisfy, so the bounded
    /// loop join times out; a drain that emitted an unacknowledged Ready fails the zero-Ready count.
    /// </remarks>
    [Fact]
    public async Task ReaderFaultWhileRetryIsParkedMidDelay_ClosesAdmission_JoinsAndPropagates()
    {
        var harness = new RetryHarness { UseFaultingReader = true };
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);

            // The retry is PROVABLY parked in its delay; the clock is NEVER advanced.
            await harness.RetryDelayCreatedAsync(1);
            Assert.False(harness.Retry.IsCompleted);
            Assert.Equal(0, harness.Clock.AdvanceCount);

            // THE READER FAULT — the ACK is permanently absent (it is never pushed).
            harness.ArmReaderFault(new ReaderFaultPrimaryException("injected reader fault"));
            var propagated = await Assert.ThrowsAsync<ReaderFaultPrimaryException>(() =>
                harness.JoinAsync());
            Assert.Equal("injected reader fault", propagated.Message);

            // The drain closed admission and joined EVERYTHING before clearing: no retransmission,
            // no Ready, ownership empty, and the fault's IDENTITY propagated unchanged.
            Assert.Equal(0, harness.Clock.AdvanceCount);
            Assert.Single(harness.Completes);
            Assert.Equal(0, harness.ReadyCount);
            Assert.Equal(0, harness.SlotOccupancy);
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// RUN-CANCELLATION TEARDOWN WITH A PARKED RETRY: cancelling the loop's own token drains and
    /// clears WITHOUT any ACK wait, cancels the retry's parked delay (never-advanced clock), joins
    /// the retry, emits NO ordinary Ready and retires the connection.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. A drain that waited for an ACK leaves the loop alive past the bounded join; a
    /// drain that skipped the admission close leaves the retry parked past the join.
    /// </remarks>
    [Fact]
    public async Task RunCancellationWithParkedRetry_DrainsCancelsAndJoinsWithoutReady()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);

            // The retry is PROVABLY parked; the clock is NEVER advanced.
            await harness.RetryDelayCreatedAsync(1);
            Assert.False(harness.Retry.IsCompleted);
            Assert.Equal(0, harness.Clock.AdvanceCount);

            // THE RUN CANCELLATION: no ACK will ever arrive, and the response stream is never
            // completed — the reader stays open, so EOF cannot be the drain's trigger.
            harness.CancelLoopToken();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                harness.JoinAsync());

            Assert.Equal(0, harness.Clock.AdvanceCount);
            Assert.Single(harness.Completes);
            Assert.Equal(0, harness.ReadyCount);
            Assert.Equal(0, harness.SlotOccupancy);
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8. Successor and sequential-run boundaries.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// NO OLD RETRY ON A SUCCESSOR ASSIGNMENT. Assignment A completes and its (closed) retry is
    /// retained; a successor B arrives — authorized by A's STARTED Ready write, the negotiated
    /// boundary — and gets its OWN retry task with its OWN frozen envelope. A's evidence never
    /// reappears and A's retry task is not B's.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. A retry that outlived the ownership clear is either the SAME task instance
    /// (asserted different) or resends A's frozen completion under A's identity (asserted by task id).
    /// </remarks>
    [Fact]
    public async Task SuccessorAssignment_GetsItsOwnRetry_AndThePredecessorsNeverReplays()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();

            // ── ASSIGNMENT A ──────────────────────────────────────────────────────
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            var ownerA = harness.Owner;
            var retryA = harness.Retry;

            // A's authorized ordinary Ready: the ACK authorizes the write, which is what makes the
            // successor legitimate at the pre-Ready boundary.
            harness.PushAck(TaskA);
            await harness.ReadyEnteredAsync(0);
            harness.ReleaseReady(0);
            await harness.JoinedReadyWriteAsync();

            // ── ASSIGNMENT B ──────────────────────────────────────────────────────
            harness.PushAssignment(TaskB);
            await harness.PromptStartedAsync(TaskB);

            var ownerB = harness.Owner;
            Assert.NotSame(ownerA, ownerB);

            var retryB = harness.RetryTask;
            Assert.NotNull(retryB);
            Assert.NotSame(retryA, retryB);

            // B's own frozen envelope names B, and B's execution is its own.
            harness.ReleasePrompt(TaskB);
            await harness.CompleteEnteredAsync(1);
            Assert.Equal(TaskA, harness.Completes[0].Complete.TaskId);
            Assert.Equal(TaskB, harness.Completes[1].Complete.TaskId);

            // B is genuinely WAITING for its own interval, and NOTHING has been re-sent yet: A's
            // closed retry contributed no attempt at all.
            await harness.RetryDelayCreatedAsync(2);
            Assert.Equal(2, harness.Completes.Count);

            // THE ONE interval produces B's retry, under B's identity — A's never replays.
            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(2);
            Assert.Equal(TaskB, harness.Completes[^1].Complete.TaskId);
            Assert.Equal(2, harness.ExecutionCount);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// A SEQUENTIAL RUN STARTS WITH A FRESH RETRY BOUND TO THE NEW REGISTRATION. The service's single
    /// published connection is replaced by a second run's connection (the per-attempt shape
    /// <c>Program.cs</c> produces): the new assignment's retry observes the NEW connection, and the
    /// OLD retry task was already joined so it cannot write on the new one.
    /// </summary>
    [Fact]
    public async Task SequentialRun_StartsAFreshRetry_BoundToTheNewConnection()
    {
        var harness = new RetryHarness();
        try
        {
            // ── RUN 1 ─────────────────────────────────────────────────────────────
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            var firstConnection = harness.Connection;
            var retry1 = harness.Retry;

            // Run 1 ends: EOF drains, joins the retry and clears ownership.
            harness.CompleteStream();
            await harness.JoinAsync();
            await harness.JoinedRetryAsync();
            Assert.Equal(0, harness.SlotOccupancy);
            Assert.Equal(0, harness.ReadyCount);
            Assert.True(retry1.IsCompleted);

            // ── RUN 2, on a NEW connection (the per-attempt registration shape) ────
            await harness.StartSecondRunAsync();

            harness.ReleasePrompt(TaskB);
            await harness.CompleteEnteredAsync(1);
            await harness.RetryDelayCreatedAsync(1);

            var secondConnection = harness.Connection;
            Assert.NotSame(firstConnection, secondConnection);

            var retry2 = harness.RetryTask;
            Assert.NotNull(retry2);
            Assert.NotSame(retry1, retry2);

            // The NEW retry re-sends the NEW assignment's frozen evidence.
            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(2);
            Assert.Equal(TaskA, harness.Completes[0].Complete.TaskId);
            Assert.Equal(TaskB, harness.Completes[1].Complete.TaskId);
            Assert.Equal(TaskB, harness.Completes[2].Complete.TaskId);

            harness.CompleteStream();
            await harness.JoinAsync();
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  harness
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ONE run of the retransmission fixture: a REAL <see cref="WorkerService"/> driving the REAL
    /// <c>ProcessMessagesAsync</c> loop over a test duplex stream, a fully test-controlled clock, a
    /// writer that can hold the ORIGINAL Complete and/or every RETRY write, reflected observation of
    /// the assignment owner, and a held production send permit for the queued-admission vectors.
    /// </summary>
    private sealed class RetryHarness : IDisposable
    {
        private readonly WorkerService _service;
        private readonly ScriptedRunner _runner = new();
        private readonly RetransmissionWriter _writer = new();
        private readonly string _root;
        private readonly IDisposable _gitRestore;
        private readonly IDisposable _consoleRestore;
        private readonly List<Task> _loops = [];
        private readonly List<WorkerConnection> _connections = [];
        private readonly List<IAsyncStreamReader<OrchestratorMessage>> _readers = [];
        private readonly List<CancellationTokenSource> _loopCtsList = [];
        private readonly SemaphoreSlim _sendGate;
        private bool _gateHeld;
        private string _taskId = TaskA;

        internal RetryHarness()
        {
            Clock = new ManualRetransmissionClock();

            _root = Path.Combine(Path.GetTempPath(), "copilothive-retry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var configRepoDir = Path.Combine(_root, "config-repo");
            Directory.CreateDirectory(configRepoDir);

            // THE PROCESS-RUNNER SEAM: a healthy fake git, so the REAL executor chain runs and
            // produces a genuine TaskResult with real nested evidence — no real process starts.
            _gitRestore = WorkerServiceConfigRepoHarness.InstallProcessRunner(new FakeGitLauncher(tokens =>
            {
                // The per-repo status probe goes through the OPAQUE-argument path, so the whole
                // command is a single token; matching the joined text covers both shapes.
                var command = string.Join(' ', tokens);

                if (command.Contains("rev-parse --is-inside-work-tree", StringComparison.Ordinal))
                    return new GitProcessResult(0, "true\n", string.Empty);
                if (command.Contains("rev-parse --show-toplevel", StringComparison.Ordinal))
                    return new GitProcessResult(0, configRepoDir + "\n", string.Empty);
                if (command.Contains("remote get-url origin", StringComparison.Ordinal))
                    return new GitProcessResult(0, "https://example.invalid/config.git\n", string.Empty);
                if (command.Contains("rev-parse HEAD", StringComparison.Ordinal))
                    return new GitProcessResult(0, "0123456789abcdef0123456789abcdef01234567\n", string.Empty);
                if (command.Contains("diff --numstat", StringComparison.Ordinal))
                {
                    // NUL-DELIMITED records, exactly the `--numstat -z` shape the parser expects;
                    // the ORDER is the frozen evidence the mutation cell pins.
                    return new GitProcessResult(
                        0,
                        "3\t1\tsrc/alpha.cs\0" + "7\t0\tsrc/beta.cs\0" + "0\t0\tsrc/gamma.cs\0",
                        string.Empty);
                }

                if (command.Contains("status --porcelain", StringComparison.Ordinal))
                {
                    // CLEAN: a dirty tree makes the executor re-prompt the agent to commit, which
                    // would add prompt invocations and break the "one execution" witness. The
                    // ChangedFiles evidence comes from `diff --numstat` above, not from this probe.
                    return new GitProcessResult(0, string.Empty, string.Empty);
                }

                return new GitProcessResult(0, string.Empty, string.Empty);
            }));

            _service = BuildService(_runner, configRepoDir);
            typeof(WorkerService)
                .GetProperty("TimeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(_service, Clock);

            _sendGate = (SemaphoreSlim)typeof(WorkerService)
                .GetField("_sendGate", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(_service)!;

            // A stderr TEE, so the guarded sanitized diagnostics are observed with a POSITIVE
            // rendezvous rather than by polling the console.
            _consoleRestore = _runner.CaptureDiagnostics();
        }

        /// <summary>Whether the ORIGINAL (index 0) Complete write parks until released.</summary>
        internal bool HoldOriginalComplete
        {
            init => _writer.HoldOriginalComplete = value;
        }

        /// <summary>
        /// Whether RETRY Complete writes (index &gt;= 1) park until released.
        /// </summary>
        internal bool HoldRetries
        {
            init => _writer.HoldRetries = value;
        }

        /// <summary>
        /// Whether READY writes park until released — which is what makes a FAILING Ready fault come
        /// from inside a genuine suspension (exactly as a real gRPC writer's does) rather than
        /// synchronously out of the settlement that starts the write.
        /// </summary>
        internal bool HoldReadies
        {
            init => _writer.HoldReadies = value;
        }

        /// <summary>A ONE-SHOT failure for the next RETRY write.</summary>
        internal Exception? FailNextRetryWrite
        {
            init => _writer.FailNextRetryWrite = value;
        }

        /// <summary>A ONE-SHOT failure for the first Ready write.</summary>
        internal Exception? FailFirstReadyWrite
        {
            init => _writer.FailNextReadyWrite = value;
        }

        /// <summary>The connection's negotiated ACK fact (both-flags default).</summary>
        internal bool AckEnabled { get; init; } = true;

        /// <summary>The connection's negotiated readiness-requirement fact (both-flags default).</summary>
        internal bool ReadyRequired { get; init; } = true;

        /// <summary>Whether the assignment's connection carries a provisioner that always fails.</summary>
        internal bool FailProvisioning { get; init; }

        /// <summary>
        /// Whether this run's response reader is a <see cref="FaultingResponseReader"/> instead of a
        /// plain <see cref="ChannelResponseReader"/> — the reader-fault teardown vector's switch.
        /// </summary>
        internal bool UseFaultingReader { get; init; }

        /// <summary>Arms the CURRENT run's reader fault (one-shot; see <c>FaultingResponseReader</c>).</summary>
        internal void ArmReaderFault(Exception fault)
        {
            if (CurrentReader is FaultingResponseReader faulting)
                faulting.ArmFault(fault);
            else
                throw new Xunit.Sdk.XunitException(
                    "ArmReaderFault requires UseFaultingReader: the current reader cannot fault.");
        }

        /// <summary>Cancels the CURRENT run's loop token (the run-cancellation teardown vector).</summary>
        internal void CancelLoopToken() => _loopCtsList[^1].Cancel();

        internal ManualRetransmissionClock Clock { get; }

        internal WorkerConnection Connection => _connections[^1];

        internal Task Loop => _loops[^1];

        /// <summary>Snapshot of every Complete the wire has seen, oldest first.</summary>
        internal IReadOnlyList<WorkerMessage> Completes => _writer.Completes;

        internal int ReadyCount => _writer.ReadyCount;

        /// <summary>The number of prompt invocations that ENTERED (one per execution).</summary>
        internal int ExecutionCount => _runner.PromptCount;

        /// <summary>Task ids whose prompt ever started, in order.</summary>
        internal IReadOnlyList<string> PromptStarts => _runner.StartedTaskIds;

        /// <summary>Everything production has written to stderr so far.</summary>
        internal string Diagnostics => _runner.Diagnostics;

        /// <summary>The CURRENT assignment's retained terminal result, or <c>null</c>.</summary>
        internal TaskResult? RetainedResult => ReadOwnerProperty("TerminalResult") is { } holder
            ? (TaskResult?)holder.GetType().GetProperty("Result")!.GetValue(holder)
            : null;

        /// <summary>The CURRENT retained assignment owner.</summary>
        internal object Owner => ReadOwnerField()
            ?? throw new Xunit.Sdk.XunitException("Expected a retained assignment owner.");

        internal int SlotOccupancy => ReadOwnerField() is null ? 0 : 1;

        /// <summary>The CURRENT assignment's ONE retry task.</summary>
        internal Task Retry => RetryTask
            ?? throw new Xunit.Sdk.XunitException("Expected the assignment to own a retry task.");

        /// <summary>The CURRENT assignment's retry task, or <c>null</c> when it has none.</summary>
        internal Task? RetryTask => ReadOwnerProperty("Retry") as Task;

        internal Task Execution => ReadOwnerTask("Execution");

        internal Task Reporting => ReadOwnerTask("Reporting");

        internal bool ReceiptConfirmed => (bool)ReadReceiptProperty("IsConfirmed");

        /// <summary>Whether the writer observed the retry write at <paramref name="index"/> COMPLETE.</summary>
        internal bool RetryWriteCompleted(int index) => _writer.CompleteCompleted(index);

        // ── driving ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Publishes the connection, starts the REAL loop and pushes the assignment for this run.
        /// </summary>
        /// <remarks>
        /// THE CONNECTION IS BUILT HERE, NOT IN THE CONSTRUCTOR, so every <c>init</c> property the
        /// vector configured (hold modes, injected faults, negotiation flags, provisioning failure)
        /// is already in force when the connection's negotiated facts and the writer's behavior are
        /// fixed.
        /// </remarks>
        internal Task StartAsync()
        {
            BuildConnectionAndStartLoop();
            PushAssignment(_taskId);
            return Task.CompletedTask;
        }

        /// <summary>Starts a SECOND sequential run with a FRESH connection and stream.</summary>
        internal Task StartSecondRunAsync()
        {
            _taskId = TaskB;
            BuildConnectionAndStartLoop();
            PushAssignment(_taskId);
            return Task.CompletedTask;
        }

        private void BuildConnectionAndStartLoop()
        {
            var provisioner = FailProvisioning
                ? new WorkerConfigProvisioner(
                    AssignedWorkerId,
                    (_, _) => Task.FromException<GetWorkerConfigResponse>(
                        new InvalidOperationException("injected provisioning failure")),
                    _ => null,
                    (_, _) => { })
                : _service.TestProvisioner;

            // THE PER-RUN LOOP TOKEN: the run-cancellation teardown vector cancels THIS source, and
            // teardown disposes every one of them.
            var loopCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            _loopCtsList.Add(loopCts);

            // A FRESH reader per run: the loop's own consumption counters are then unambiguous,
            // which is what the barrier counts rely on. The reader-fault vector substitutes the
            // FAULTING reader for the run that asked for it.
            IAsyncStreamReader<OrchestratorMessage> reader = UseFaultingReader && _readers.Count == 0
                ? new FaultingResponseReader()
                : new ChannelResponseReader();
            _readers.Add(reader);

            var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
                _writer, reader,
                _ => Task.FromResult(new Metadata()),
                _ => new Status(StatusCode.OK, string.Empty),
                _ => new Metadata(),
                _ => { },
                null!);

            var connection = TestConnectionFactory.Attach(
                _service, AssignedWorkerId, stream, provisioner,
                completionReceiptAckEnabled: AckEnabled,
                completionReadyRequired: ReadyRequired);
            _connections.Add(connection);

            _loops.Add((Task)typeof(WorkerService)
                .GetMethod("ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_service, [connection, loopCts.Token])!);
        }

        private dynamic CurrentReader => _readers[^1];

        /// <summary>
        /// The CURRENT run's reader as the channel-backed double when it is one, so Push/Consumed/
        /// CompleteStream keep their existing shape. The faulting reader exposes the same members.
        /// </summary>
        private void PushToReader(OrchestratorMessage message)
        {
            if (CurrentReader is ChannelResponseReader channel)
                channel.Push(message);
            else
                ((FaultingResponseReader)(object)CurrentReader).Push(message);
        }

        private Task ConsumedOnReaderAsync(int count) =>
            (CurrentReader switch
            {
                ChannelResponseReader channel => channel.Consumed(count),
                FaultingResponseReader faulting => faulting.Consumed(count),
                _ => throw new Xunit.Sdk.XunitException("Unknown reader shape."),
            }).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        private void CompleteCurrentReader()
        {
            if (CurrentReader is ChannelResponseReader channel)
                channel.TryComplete();
            else
                ((FaultingResponseReader)(object)CurrentReader).TryComplete();
        }

        internal Task PromptStartedAsync(string taskId) =>
            _runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        internal void ReleasePrompt(string taskId) => _runner.Release(taskId);

        internal void PushAssignment(string taskId) => PushToReader(new OrchestratorMessage
        {
            Assignment = new TaskAssignment
            {
                TaskId = taskId,
                GoalId = "goal-retransmission",
                GoalDescription = "retransmit unacknowledged completions",
                Prompt = "do the work",
                Role = GrpcWorkerRole.Coder,
                Model = AssignedModel,

                // A REPOSITORY IS REQUIRED FOR REAL GIT EVIDENCE: the executor's per-repo status
                // probe (and therefore GitStatus.ChangedFiles) only runs for a task with
                // repositories, so the nested-collection mutation cell needs one.
                Repositories = { new RepositoryInfo { Name = "repo-alpha", Url = "https://example.invalid/repo-alpha", DefaultBranch = "main" } },
            },
        });

        internal void PushAck(string taskId) => PushToReader(new OrchestratorMessage
        {
            CompletionReceiptAck = new CompletionReceiptAck { TaskId = taskId, WorkerId = AssignedWorkerId },
        });

        internal void PushProbe(string requestId) => PushToReader(new OrchestratorMessage
        {
            ToolResponse = new ToolCallResponse { RequestId = requestId, Success = true, ResultJson = "{}" },
        });

        internal void PushCancel(string taskId) => PushToReader(new OrchestratorMessage
        {
            Cancel = new CancelTask { TaskId = taskId, Reason = "matching cancel" },
        });

        internal void CompleteStream() => CompleteCurrentReader();

        internal Task ConsumedAsync(int count) => ConsumedOnReaderAsync(count);

        /// <summary>
        /// OWNER-INSTALLED BARRIER. The assignment handler installs the ownership slot only after it
        /// has started both original tasks and the retry, and the loop re-arms its next read only
        /// after that handler returned — so a probe whose READ completes proves the owner is
        /// published. Nothing here polls or sleeps.
        /// </summary>
        internal async Task AwaitOwnerInstalledAsync()
        {
            PushProbe("owner-installed");
            await ConsumedAsync(2);
            Assert.NotNull(Owner);
        }

        // ── milestones ───────────────────────────────────────────────────────────

        internal Task CompleteEnteredAsync(int index) =>
            _writer.CompleteEntered(index).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        internal void ReleaseComplete(int index) => _writer.ReleaseComplete(index);

        internal Task ReadyEnteredAsync(int index) =>
            _writer.ReadyEntered(index).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        internal void ReleaseReady(int index) => _writer.ReleaseReady(index);

        internal Task RetryDelayCreatedAsync(int count) =>
            Clock.WaitForTimerCountAsync(count, TestContext.Current.CancellationToken);

        internal Task JoinedReportingAsync() =>
            Reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        internal Task JoinedExecutionAsync() =>
            Execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        internal Task JoinedRetryAsync() =>
            RetryTask is { } retry
                ? retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken)
                : Task.CompletedTask;

        /// <summary>
        /// POSITIVE evidence the CURRENT owner's retry task is GONE — used by the teardown rows,
        /// where the ownership slot has already been cleared.
        /// </summary>
        internal bool RetryStopped => RetryTask is null || RetryTask.IsCompleted;

        internal Task JoinedReadyWriteAsync()
        {
            var slot = ReadOwnerProperty("OrdinaryReady")
                ?? throw new Xunit.Sdk.XunitException("Expected the assignment's readiness slot.");
            var write = (Task?)slot.GetType().GetProperty("Write")!.GetValue(slot)
                ?? throw new Xunit.Sdk.XunitException("Expected a started readiness write.");
            return write.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }

        /// <summary>The CURRENT assignment's retained readiness write, or <c>null</c> before it started.</summary>
        internal Task? ReadinessSlotWrite => ReadOwnerProperty("OrdinaryReady") is not { } slot
            ? null
            : (Task?)slot.GetType().GetProperty("Write")!.GetValue(slot);

        /// <summary>
        /// Completes once production has emitted the guarded SANITIZED RETRANSMISSION diagnostic.
        /// </summary>
        internal Task WaitForSanitizedDiagnosticAsync() =>
            _runner.RetransmissionDiagnosticObserved.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        /// <summary>
        /// Completes once production has emitted the existing guarded DRAIN-fault diagnostic (the
        /// path that reports a faulted task an ownership transition joined).
        /// </summary>
        internal Task WaitForDrainFaultDiagnosticAsync() =>
            _runner.DrainFaultObserved.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        /// <summary>
        /// Advances the clock by ONE production interval. The manual clock fires the delay's own
        /// completion callback synchronously and <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
        /// dispatches continuations asynchronously, so this returns immediately even when the retry
        /// that wakes then parks on the send gate — the caller awaits the real milestone instead.
        /// </summary>
        internal Task AdvanceOneIntervalAsync()
        {
            Clock.Advance(ProductionInterval);
            return Task.CompletedTask;
        }

        // ── the held production send permit ───────────────────────────────────────

        /// <summary>
        /// Takes the PRODUCTION send permit and holds it, so a retry that is due must QUEUE behind an
        /// already-admitted send. The permit is the very <c>SemaphoreSlim</c> <c>SendAsync</c> uses.
        /// </summary>
        internal async Task HoldSendGateAsync()
        {
            await _sendGate.WaitAsync(TestContext.Current.CancellationToken);
            _gateHeld = true;
        }

        internal void ReleaseSendGate()
        {
            if (!_gateHeld)
                return;

            _gateHeld = false;
            _sendGate.Release();
        }

        /// <summary>
        /// POSITIVE proof the retry is PARKED ON THE PERMIT QUEUE: the production gate's own async
        /// waiter list reports at least one waiter.
        /// </summary>
        internal Task RetryQueuedOnSendGateAsync() =>
            SendGateObserver.WaitForWaitersAsync(_sendGate, 1, TestContext.Current.CancellationToken);

        /// <summary>The production send gate's current count: 0 while a write holds the permit.</summary>
        internal int SendGateCurrentCount => _sendGate.CurrentCount;

        /// <summary>
        /// Completes once the FIRST Ready write has ENTERED the writer — used AFTER the admitted
        /// retry terminated, so the assertion proves the queued Ready entered only after that
        /// release rather than racing it.
        /// </summary>
        internal Task ReadyWriteEnteredAfterRetryTerminatedAsync() =>
            _writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        // ── lifecycle ────────────────────────────────────────────────────────────

        internal Task JoinAsync() =>
            Loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        /// <summary>
        /// Releases every parked gate, joins every started task within the bound, retires the
        /// connections, disposes the service and releases the temp root — so a failing assertion
        /// cannot leave a producer stuck.
        /// </summary>
        internal async Task TeardownAsync()
        {
            ReleaseSendGate();
            _runner.ReleaseAll();
            _writer.ReleaseAll();

            foreach (var reader in _readers)
            {
                if (reader is ChannelResponseReader channel)
                    channel.TryComplete();
                else
                    ((FaultingResponseReader)reader).TryComplete();
            }

            foreach (var loopCts in _loopCtsList)
                await loopCts.CancelAsync();

            foreach (var loop in _loops)
                await ObserveAsync(loop);

            foreach (var task in OwnerTasks())
                await ObserveAsync(task);

            foreach (var connection in _connections)
                connection.Retire();

            _service.Dispose();

            foreach (var loopCts in _loopCtsList)
                loopCts.Dispose();

            Dispose();
        }

        private IEnumerable<Task> OwnerTasks()
        {
            if (ReadOwnerField() is not { } owner)
                yield break;

            foreach (var name in new[] { "Execution", "Reporting", "Retry" })
            {
                if (owner.GetType().GetProperty(name)!.GetValue(owner) is Task task)
                    yield return task;
            }

            if (ReadOwnerProperty("OrdinaryReady") is { } slot
                && slot.GetType().GetProperty("Write")!.GetValue(slot) is Task write)
            {
                yield return write;
            }
        }

        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }
            catch (Exception)
            {
                // Quiescent: the real outcome was asserted on the test's normal path.
            }
        }

        private object? ReadOwnerField() =>
            typeof(WorkerService)
                .GetField("_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(_service);

        private object? ReadOwnerProperty(string name)
        {
            var owner = ReadOwnerField();
            return owner?.GetType().GetProperty(name)!.GetValue(owner);
        }

        private object ReadReceiptProperty(string name)
        {
            var receipt = ReadOwnerProperty("Receipt")
                ?? throw new Xunit.Sdk.XunitException("Expected the assignment's receipt tracker.");
            return receipt.GetType().GetProperty(name)!.GetValue(receipt)!;
        }

        private Task ReadOwnerTask(string name) =>
            (Task?)ReadOwnerProperty(name)
            ?? throw new Xunit.Sdk.XunitException($"Expected the owner's {name} task.");

        public void Dispose()
        {
            _consoleRestore.Dispose();
            _gitRestore.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  shared helpers
    // ══════════════════════════════════════════════════════════════════════════

    private static WorkerService BuildService(IAgentRunner runner, string configRepoDir)
    {
        var service = new WorkerService("http://localhost:9999", AssignedWorkerId, ["coder"], configRepoDir);
        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);
        return service;
    }

    private static bool Matches(IReadOnlyList<string> tokens, params string[] prefix)
    {
        if (tokens.Count < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(tokens[i], prefix[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>Reads production's OWN retransmission interval, so the fixture never guesses it.</summary>
    private static TimeSpan ReadProductionInterval() =>
        (TimeSpan)typeof(WorkerService)
            .GetField("RetransmissionInterval", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    /// <summary>
    /// POSITIVE NON-COMPLETION for the retry: after advancing the clock by several whole intervals
    /// and waiting a bounded window, NO delay timer exists and NO Complete was written.
    /// </summary>
    private static async Task AssertNoRetryStartedAsync(RetryHarness harness, string because)
    {
        var before = harness.Completes.Count;
        await harness.AdvanceOneIntervalAsync();
        harness.Clock.Advance(ProductionInterval * 2);

        var suppressed = await Record.ExceptionAsync(() =>
            harness.RetryDelayCreatedAsync(1).WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));

        Assert.True(
            suppressed is TimeoutException,
            $"A retry delay must never be created {because}; observed {harness.Clock.TimerCount} timer(s).");

        await AssertNoRetrySendAsync(harness, because);
        Assert.Equal(before, harness.Completes.Count);
    }

    /// <summary>POSITIVE NON-COMPLETION for a retransmission SEND once the retry is stopped.</summary>
    private static async Task AssertNoRetrySendAsync(RetryHarness harness, string because)
    {
        var before = harness.Completes.Count;
        var unexpected = await Record.ExceptionAsync(() =>
            harness.CompleteEnteredAsync(before).WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));

        Assert.True(
            unexpected is TimeoutException,
            $"No retransmission may be sent {because}; observed {harness.Completes.Count} Complete(s).");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  doubles
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The request-stream writer. It records every <see cref="WorkerMessage"/>, parks Complete writes
    /// BY INDEX (index 0 is the ORIGINAL attempt, every later index is a RETRY), supports a ONE-SHOT
    /// failure for the next RETRY write and for the first Ready write, and tracks whether each write
    /// actually COMPLETED — which is exactly what the "admitted but not released by the ACK"
    /// assertions need.
    /// </summary>
    /// <remarks>
    /// INDEX-KEYED, SO THE TWO HALVES ARE SEPARABLE. The ORIGINAL Complete and the retry writes are
    /// distinguishable by order alone, which is what lets this fixture hold one and release the other.
    /// </remarks>
    private sealed class RetransmissionWriter : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _completes = [];
        private readonly List<WorkerMessage> _readies = [];
        private readonly List<bool> _completeDone = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyCountWaiters = [];
        private bool _releaseImmediately;
        private Exception? _failNextRetryWrite;
        private Exception? _failNextReadyWrite;

        internal bool HoldOriginalComplete { get; set; }

        internal bool HoldRetries { get; set; }

        /// <summary>
        /// Whether READY writes park until released. A failing Ready must be raised from INSIDE a
        /// genuine suspension, exactly as a real gRPC writer's fault is, rather than synchronously
        /// out of the settlement that starts the write.
        /// </summary>
        internal bool HoldReadies { get; set; }

        internal Exception? FailNextRetryWrite
        {
            set { lock (_gate) _failNextRetryWrite = value; }
        }

        internal Exception? FailNextReadyWrite
        {
            set { lock (_gate) _failNextReadyWrite = value; }
        }

        internal IReadOnlyList<WorkerMessage> Completes
        {
            get { lock (_gate) return [.. _completes]; }
        }

        internal int ReadyCount { get { lock (_gate) return _readies.Count; } }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message) => WriteCoreAsync(message, CancellationToken.None);

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken ct)
            => WriteCoreAsync(message, ct);

        internal bool CompleteCompleted(int index)
        {
            lock (_gate)
                return _completeDone.Count > index && _completeDone[index];
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

        public Task CompleteAsync() => Task.CompletedTask;

        private async Task WriteCoreAsync(WorkerMessage message, CancellationToken ct)
        {
            switch (message.PayloadCase)
            {
                case WorkerMessage.PayloadOneofCase.Complete:
                    await WriteCompleteAsync(message, ct);
                    return;

                case WorkerMessage.PayloadOneofCase.Ready:
                    await WriteReadyAsync(message, ct);
                    return;

                default:
                    return;
            }
        }

        private async Task WriteCompleteAsync(WorkerMessage message, CancellationToken ct)
        {
            int index;
            TaskCompletionSource entered;
            TaskCompletionSource release;
            Exception? failure;

            lock (_gate)
            {
                index = _completes.Count;
                _completes.Add(message.Clone());
                _completeDone.Add(false);
                entered = Slot(_completeEntered, index);
                release = Slot(_completeRelease, index);

                // INDEX 0 IS THE ORIGINAL ATTEMPT — never a retry, so it never carries a retry fault.
                failure = index == 0 ? null : _failNextRetryWrite;
                if (index != 0)
                    _failNextRetryWrite = null;

                var holds = index == 0 ? HoldOriginalComplete : HoldRetries;
                if (!holds || _releaseImmediately)
                    release.TrySetResult();
            }

            entered.TrySetResult();

            // The CANCELLABLE path: a cancelled in-flight write unwinds like a real gRPC writer.
            await release.Task.WaitAsync(ct);

            if (failure is not null)
                throw failure;

            lock (_gate)
                _completeDone[index] = true;
        }

        private async Task WriteReadyAsync(WorkerMessage message, CancellationToken ct)
        {
            int index;
            TaskCompletionSource entered;
            TaskCompletionSource release;
            Exception? failure;

            lock (_gate)
            {
                index = _readies.Count;
                _readies.Add(message.Clone());
                entered = Slot(_readyEntered, index);
                release = Slot(_readyRelease, index);
                failure = _failNextReadyWrite;
                _failNextReadyWrite = null;

                // A Ready write parks when the vector asked for it AND a fault is armed: that is how
                // the fault is raised from INSIDE a genuine suspension rather than synchronously.
                var holds = failure is not null && HoldReadies;
                if (!holds || _releaseImmediately)
                    release.TrySetResult();

                Satisfy(_readies.Count, _readyCountWaiters);
            }

            entered.TrySetResult();

            await release.Task.WaitAsync(ct);

            if (failure is not null)
                throw failure;
        }

        private static void Satisfy(int count, Dictionary<int, TaskCompletionSource> waiters)
        {
            List<int> satisfied = [];
            foreach (var (threshold, waiter) in waiters)
            {
                if (count >= threshold)
                {
                    waiter.TrySetResult();
                    satisfied.Add(threshold);
                }
            }

            foreach (var threshold in satisfied)
                waiters.Remove(threshold);
        }

        private static TaskCompletionSource Slot(Dictionary<int, TaskCompletionSource> slots, int index)
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
    /// The agent runner: prompts park until released, every PROMPT ENTRY is counted (the exact
    /// "one execution" witness) and stderr is teed so the guarded sanitized retransmission diagnostic
    /// is observable with a positive rendezvous.
    /// </summary>
    private sealed class ScriptedRunner : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private readonly List<string> _startedIds = [];
        private readonly StringWriter _diagnostics = new();
        private readonly TaskCompletionSource _retransmissionDiagnostic =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drainFaultDiagnostic =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _teardown;
        private string? _currentTaskId;

        internal Task RetransmissionDiagnosticObserved => _retransmissionDiagnostic.Task;

        internal Task DrainFaultObserved => _drainFaultDiagnostic.Task;

        internal int PromptCount { get { lock (_gate) return _startedIds.Count; } }

        internal IReadOnlyList<string> StartedTaskIds { get { lock (_gate) return [.. _startedIds]; } }

        internal string Diagnostics { get { lock (_diagnostics) return _diagnostics.ToString(); } }

        /// <summary>
        /// Tees <see cref="Console.Error"/>: production's guarded diagnostics reach the real stderr
        /// AND this runner's own tap, where the retransmission diagnostic completes a rendezvous.
        /// </summary>
        internal IDisposable CaptureDiagnostics()
        {
            var original = Console.Error;
            Console.SetError(new DiagnosticTee(original, this));
            return new Restore(() => Console.SetError(original));
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

        internal void NoteDiagnostic(string text)
        {
            lock (_diagnostics)
                _diagnostics.Write(text);

            if (text.Contains(RetransmissionDiagnostic, StringComparison.Ordinal))
                _retransmissionDiagnostic.TrySetResult();

            if (text.Contains(DrainFaultDiagnostic, StringComparison.Ordinal))
                _drainFaultDiagnostic.TrySetResult();
        }

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var id = _currentTaskId ?? "(unknown)";
            lock (_gate) _startedIds.Add(id);
            Slot(_started, id).TrySetResult();

            await Slot(_release, id).Task.WaitAsync(ct);
            return "done";
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

        public TestResultReport? LastTestReport { get; } = new()
        {
            Verdict = CopilotHive.Workers.TaskVerdict.Pass,
            BuildSuccess = true,
            TotalTests = 42,
            PassedTests = 42,
            FailedTests = 0,
            CoveragePercent = 91.5,
            Issues = ["frozen-issue-one", "frozen-issue-two"],
            Summary = "frozen metrics evidence",
        };

        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) => _currentTaskId = taskId;
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

        /// <summary>A <see cref="TextWriter"/> that forwards to the real stderr and taps the line.</summary>
        private sealed class DiagnosticTee(TextWriter inner, ScriptedRunner runner) : TextWriter
        {
            public override System.Text.Encoding Encoding => inner.Encoding;

            public override void WriteLine(string? value)
            {
                inner.WriteLine(value);
                if (value is not null)
                    runner.NoteDiagnostic(value);
            }

            public override void Write(string? value) => inner.Write(value);

            public override void Write(char value) => inner.Write(value);
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>A failure injected as a RETRY transport-write fault.</summary>
    private sealed class RetryWriteFailureException(string message) : Exception(message);

    /// <summary>A failure injected as the first ordinary Ready write's fault.</summary>
    private sealed class ReadyWriteFailureException(string message) : Exception(message);

    /// <summary>A reader fault whose IDENTITY the loop must propagate unchanged.</summary>
    private sealed class ReaderFaultPrimaryException(string message) : Exception(message);
}
