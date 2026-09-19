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
    /// THE PRODUCTION FIVE-SECOND INTERVAL, read from production's own constant ONLY for the
    /// convenience advances that simply need "one whole interval" to elapse.
    /// </summary>
    /// <remarks>
    /// IT IS DELIBERATELY NOT USED BY THE BOUNDARY VECTORS. A fixture that both reads the constant
    /// and advances by it can never notice the constant changing, so
    /// <see cref="RetransmissionInterval_IsExactlyFiveSeconds_NoAttemptJustBeforeTheBoundary"/>
    /// states the five seconds INDEPENDENTLY (see <see cref="SpecifiedInterval"/>) and asserts the
    /// just-before / exactly-at behaviour against that stated value.
    /// </remarks>
    private static readonly TimeSpan ProductionInterval = ReadProductionInterval();

    /// <summary>
    /// THE SPEC'S OWN FIVE SECONDS, stated here and derived from NOTHING in production. The exact
    /// boundary vector advances to just before this instant (no attempt) and then onto it (exactly
    /// one attempt), so a production constant changed to any other value fails by name.
    /// </summary>
    private static readonly TimeSpan SpecifiedInterval = TimeSpan.FromSeconds(5);

    /// <summary>The smallest step the boundary vector uses to cross the stated interval exactly.</summary>
    private static readonly TimeSpan BoundaryEpsilon = TimeSpan.FromMilliseconds(1);

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
            var rawOriginal = harness.RawCompletes[0];

            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(1);
            var retransmission = harness.Completes[1];

            // THE SAME COMPLETE EVIDENCE, as a FRESH clone. The identity assertions are taken on
            // the RAW writer arguments (the exact objects production handed to WriteAsync, never
            // fixture-cloned), so a mutant that re-delivers the SAME frozen object cannot pass by
            // relying on the fixture's defensive clone.
            var rawRetransmission = harness.RawCompletes[1];
            Assert.NotSame(rawOriginal, rawRetransmission);
            Assert.NotSame(rawOriginal.Complete, rawRetransmission.Complete);
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

    /// <summary>
    /// THE PRIVATE SNAPSHOT IS NEVER HANDED TO A WRITER, proved on the RAW writer arguments and by
    /// MUTATING one of them. Each send's raw argument — the exact object production passed to
    /// <c>WriteAsync</c>, with no fixture-side clone in between — is a DISTINCT instance (message
    /// and nested <c>TaskComplete</c> alike) carrying EQUAL evidence. The first raw argument's
    /// nested collections are then CLEARED in place, and the next retransmission's raw argument
    /// still carries the ORIGINAL evidence in the ORIGINAL ORDER.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE RAW ARGUMENT IS LOAD-BEARING. A reference-inequality assertion taken on a
    /// fixture-CLONED record proves nothing: two clones of one object are two objects, so a
    /// production mutant that hands the SAME frozen <c>TaskComplete</c> to every writer would pass.
    /// The raw list is retained by reference precisely so that mutant is caught.
    /// </para>
    /// <para>
    /// WHY THE MUTATION IS LOAD-BEARING. Reference distinctness alone still permits a mutant that
    /// shallow-copies the message while SHARING the nested <c>Metrics.Issues</c> /
    /// <c>GitStatus.ChangedFiles</c> collections. Clearing those on the delivered raw argument and
    /// requiring the NEXT delivered raw argument to be unchanged rejects any shared nested state.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Retransmission_RawWriterArguments_AreDistinctInstances_UnaffectedByMutatingADeliveredOne()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryDelayCreatedAsync(1);

            // THE ORIGINAL SEND'S RAW ARGUMENT — the exact object production handed to the writer.
            var rawOriginal = harness.RawCompletes[0];
            var frozenIssues = rawOriginal.Complete.Metrics.Issues.ToArray();
            var frozenChangedFiles = rawOriginal.Complete.GitStatus.ChangedFiles.ToArray();

            // NON-VACUITY: the real executor chain produced genuine nested evidence, so clearing it
            // below actually changes something observable.
            Assert.NotEmpty(frozenIssues);
            Assert.NotEmpty(frozenChangedFiles);

            // FIRST RETRANSMISSION — a DISTINCT raw object with EQUAL evidence.
            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(1);
            var rawRetry1 = harness.RawCompletes[1];

            Assert.NotSame(rawOriginal, rawRetry1);
            Assert.NotSame(rawOriginal.Complete, rawRetry1.Complete);
            Assert.NotSame(rawOriginal.Complete.Metrics, rawRetry1.Complete.Metrics);
            Assert.NotSame(rawOriginal.Complete.GitStatus, rawRetry1.Complete.GitStatus);
            Assert.Equal(rawOriginal.Complete, rawRetry1.Complete);

            // THE MUTATION — performed on the DELIVERED raw arguments themselves, exactly as a
            // hostile (or merely careless) writer could. If production handed out the snapshot, or
            // shared its nested collections, the NEXT attempt would carry this damage.
            rawOriginal.Complete.Metrics.Issues.Clear();
            rawOriginal.Complete.Metrics.Issues.Add("MUTATED-BY-THE-FIRST-WRITER");
            rawOriginal.Complete.GitStatus.ChangedFiles.Clear();
            rawOriginal.Complete.GitStatus.ChangedFiles.Add("mutated/by/first/writer.cs");
            rawRetry1.Complete.Metrics.Issues.Clear();
            rawRetry1.Complete.GitStatus.ChangedFiles.Clear();
            rawRetry1.Complete.Output = "MUTATED-OUTPUT";

            // THE NEXT RETRANSMISSION still carries the FROZEN evidence, in the ORIGINAL ORDER.
            await harness.AdvanceOneIntervalAsync();
            await harness.CompleteEnteredAsync(2);
            var rawRetry2 = harness.RawCompletes[2];

            Assert.NotSame(rawOriginal, rawRetry2);
            Assert.NotSame(rawRetry1, rawRetry2);
            Assert.NotSame(rawRetry1.Complete, rawRetry2.Complete);
            Assert.Equal(frozenIssues, rawRetry2.Complete.Metrics.Issues);
            Assert.Equal(frozenChangedFiles, rawRetry2.Complete.GitStatus.ChangedFiles);
            Assert.DoesNotContain("MUTATED-BY-THE-FIRST-WRITER", rawRetry2.Complete.Metrics.Issues);
            Assert.DoesNotContain("mutated/by/first/writer.cs", rawRetry2.Complete.GitStatus.ChangedFiles);
            Assert.NotEqual("MUTATED-OUTPUT", rawRetry2.Complete.Output);
            Assert.Equal(AssignedWorkerId, rawRetry2.WorkerId);

            // …and the retained domain result was never cloned or replaced along the way.
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
    /// REPEATED MISSING-ACK ATTEMPTS STAY STRICTLY SEQUENTIAL, proved against a HELD attempt. One
    /// retry write is admitted and PARKED inside the writer; additional whole intervals are then
    /// advanced while it is still held, and NO new delay timer and NO new write appear. Releasing
    /// the held write requires a FRESH FULL interval before the next attempt — a just-before /
    /// at-the-boundary pair, so the fresh interval is measured, not assumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY A HELD WRITE IS REQUIRED. With immediately-completing retry writes, an implementation
    /// that overlaps only SLOW writes is indistinguishable: every attempt finishes before the next
    /// advance, so "one per advance" holds either way. Holding one attempt open is what makes the
    /// overlap observable.
    /// </para>
    /// <para>
    /// MUTATION PROOF. A retransmitter that armed its next delay BEFORE the current attempt
    /// terminated (or that allowed a second concurrent attempt) creates a timer, or writes, while
    /// the first write is still parked — both are asserted absent over bounded windows. The
    /// CONCRETE retry task is retained up front, so the final join can never be satisfied by an
    /// already-cleared ownership slot.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RepeatedMissingAckAttempts_StayStrictlySequential_EvenWhenAnAttemptIsHeld()
    {
        var harness = new RetryHarness { HoldRetries = true };
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryParkedInDelayAsync(1);

            // THE CONCRETE retry task, captured before any drain could clear the slot.
            var retry = harness.Retry;

            // ATTEMPT 1 is admitted and HELD inside the writer.
            harness.Advance(SpecifiedInterval);
            await harness.CompleteEnteredAsync(1);
            Assert.Equal(2, harness.Completes.Count);
            Assert.False(harness.RetryWriteCompleted(1), "attempt 1 must still be parked.");
            Assert.Equal(1, harness.Clock.TimerCount);

            // ADVANCING MORE INTERVALS WHILE IT IS HELD CHANGES NOTHING: no new delay is armed
            // (the retransmitter cannot be waiting — it is inside a write) and nothing new is sent.
            harness.Advance(SpecifiedInterval * 3);
            var noNewDelay = await Record.ExceptionAsync(() =>
                harness.Clock.WaitForRetryParkedInDelayAsync(2, TestContext.Current.CancellationToken)
                    .WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(noNewDelay);
            Assert.Equal(1, harness.Clock.TimerCount);
            await AssertNoRetrySendAsync(harness, "while a prior retry write is still admitted and held");
            Assert.Equal(2, harness.Completes.Count);
            Assert.False(retry.IsCompleted);

            // RELEASE attempt 1: only its OWN termination arms the next delay.
            harness.ReleaseComplete(1);
            await harness.RetryParkedInDelayAsync(2);
            Assert.True(harness.RetryWriteCompleted(1));

            // THE FRESH INTERVAL IS FULL: just-before produces nothing; the final millisecond
            // produces exactly one attempt.
            harness.Advance(SpecifiedInterval - BoundaryEpsilon);
            await AssertNoRetrySendAsync(harness, "one millisecond before the fresh interval elapsed");
            Assert.Equal(2, harness.Completes.Count);

            harness.Advance(BoundaryEpsilon);
            await harness.CompleteEnteredAsync(2);
            Assert.Equal(3, harness.Completes.Count);
            harness.ReleaseComplete(2);
            await harness.RetryParkedInDelayAsync(3);

            Assert.Equal(1, harness.ExecutionCount);

            harness.CompleteStream();
            await harness.JoinAsync();
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(retry.IsCompleted, "the drain must join the retained retry task.");
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// THE INTERVAL IS EXACTLY FIVE SECONDS, stated independently of production's constant. With
    /// the retry provably parked in its delay, advancing to ONE MILLISECOND BEFORE five seconds
    /// produces NO attempt (positive non-completion over a bounded window); advancing that last
    /// millisecond — reaching exactly five seconds of virtual time — produces EXACTLY ONE attempt.
    /// The next interval is then measured the same way, so the "fresh full interval after each
    /// prior attempt" shape is pinned at the same precision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE VALUE IS STATED, NOT READ. Reading production's own constant and advancing by that
    /// same value is a tautology: any changed constant still passes. This vector advances by
    /// <see cref="SpecifiedInterval"/> — five seconds written down here, from the spec — so a
    /// production interval shortened to (say) one second fires at the 1s mark and fails the
    /// just-before-the-boundary assertion, and one lengthened to ten seconds never fires and fails
    /// the at-the-boundary rendezvous.
    /// </para>
    /// <para>
    /// THE MANUAL CLOCK KEEPS VIRTUAL ABSOLUTE TIME, so advancing 4.999s and then 1ms is exactly
    /// equivalent to advancing 5s once — the split is an observation device, never a nudge.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RetransmissionInterval_IsExactlyFiveSeconds_NoAttemptJustBeforeTheBoundary()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);

            // THE RETRY IS PARKED IN ITS DELAY — positive evidence, through the clock seam.
            await harness.RetryParkedInDelayAsync(1);
            Assert.Single(harness.Completes);

            // JUST BEFORE the stated five seconds: NOTHING is attempted.
            harness.Advance(SpecifiedInterval - BoundaryEpsilon);
            await AssertNoRetrySendAsync(harness, "one millisecond before the five-second boundary");
            Assert.Single(harness.Completes);
            Assert.Equal(1, harness.Clock.TimerCount);

            // AT the boundary — the final millisecond — EXACTLY ONE attempt.
            harness.Advance(BoundaryEpsilon);
            await harness.CompleteEnteredAsync(1);
            Assert.Equal(2, harness.Completes.Count);

            // The NEXT interval is measured from that attempt's termination, to the same precision.
            await harness.RetryParkedInDelayAsync(2);
            harness.Advance(SpecifiedInterval - BoundaryEpsilon);
            await AssertNoRetrySendAsync(harness, "one millisecond before the SECOND boundary");
            Assert.Equal(2, harness.Completes.Count);

            harness.Advance(BoundaryEpsilon);
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

    /// <summary>
    /// THE CLOCK IS CAPTURED PER ASSIGNMENT, not re-read per wait. After the assignment has been
    /// built (its retry is already parked in a delay on the ORIGINAL clock), the service's
    /// <c>TimeProvider</c> property is SWAPPED for a second, independent manual clock. The
    /// assignment keeps using its ORIGINAL provider: advancing the ORIGINAL clock drives the
    /// attempts, while the REPLACEMENT clock is never consulted at all — it creates no timer and
    /// advancing it produces nothing.
    /// </summary>
    /// <remarks>
    /// MUTATION PROOF. A production that re-read the mutable <c>TimeProvider</c> property on each
    /// iteration would arm its NEXT delay on the replacement clock: the replacement's timer count
    /// would become non-zero (failing the zero-timer assertion by name) and advancing the ORIGINAL
    /// clock would no longer produce the second attempt (failing that rendezvous by name).
    /// </remarks>
    [Fact]
    public async Task RetryUsesTheClockCapturedWithTheAssignment_EvenAfterTheServiceClockIsSwapped()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);

            // The assignment exists and its retry is parked on the ORIGINAL clock.
            await harness.RetryParkedInDelayAsync(1);
            var originalClock = harness.Clock;
            Assert.Equal(1, originalClock.TimerCount);

            // THE SWAP — strictly AFTER the assignment was constructed and its retry started.
            var replacementClock = harness.SwapServiceClock();
            Assert.NotSame(originalClock, replacementClock);
            Assert.Equal(0, replacementClock.TimerCount);

            // The FIRST attempt is driven by the ORIGINAL clock alone.
            originalClock.Advance(SpecifiedInterval);
            await harness.CompleteEnteredAsync(1);
            Assert.Equal(2, harness.Completes.Count);

            // The retry's FRESH delay was armed on the ORIGINAL clock too — the replacement was
            // never consulted, so it still holds no timer at all.
            await originalClock
                .WaitForRetryParkedInDelayAsync(2, TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(2, originalClock.TimerCount);
            Assert.Equal(0, replacementClock.TimerCount);

            // ADVANCING THE REPLACEMENT DOES NOTHING: it owns no timer of this assignment's.
            replacementClock.Advance(SpecifiedInterval * 3);
            await AssertNoRetrySendAsync(harness, "after advancing only the REPLACEMENT clock");
            Assert.Equal(2, harness.Completes.Count);
            Assert.Equal(0, replacementClock.TimerCount);

            // …and the ORIGINAL clock still drives the next attempt.
            originalClock.Advance(SpecifiedInterval);
            await harness.CompleteEnteredAsync(2);
            Assert.Equal(3, harness.Completes.Count);
            Assert.Equal(0, replacementClock.TimerCount);

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
    /// THE HARNESS RELEASES ITS TEMP ROOT. A full retry cycle runs, and after
    /// <c>TeardownAsync</c> the harness's unique <c>/tmp/copilothive-retry-*</c> directory is GONE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS. The teardown comment claimed the temp root was released, but nothing
    /// deleted it, so every run of every vector in this fixture leaked a directory. This vector
    /// makes the claim an ASSERTION: it runs the same real loop the other vectors do (so the root
    /// genuinely contains a config-repo and the git seam really used it), tears down explicitly,
    /// and then requires the directory to be absent.
    /// </para>
    /// <para>
    /// IT VERIFIES THE ROOT EXISTED FIRST, so a harness that silently stopped creating one could
    /// not satisfy this by accident. The second teardown in the <c>finally</c> is harmless: the
    /// delete is idempotent and best-effort.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Teardown_DeletesTheHarnessTempRoot()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryParkedInDelayAsync(1);

            // NON-VACUITY: the root really exists and really was used by the run above.
            Assert.True(harness.RootExists, "the harness must create its temp root: " + harness.RootPath);

            harness.CompleteStream();
            await harness.JoinAsync();

            // THE EXPLICIT TEARDOWN — the same call every vector makes in its finally.
            await harness.TeardownAsync();

            Assert.False(
                harness.RootExists,
                "teardown must delete the harness temp root, but it still exists: " + harness.RootPath);
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// ASSERTION-FAILURE CLEANUP: a deliberate xUnit failure is raised while the real loop and a
    /// concrete retry task are live. The fixture's normal <c>finally</c> teardown must join both,
    /// delete the unique temp root, and never replace the original assertion with cleanup output.
    /// </summary>
    [Fact]
    public async Task Teardown_OnAssertionFailure_KeepsPrimaryAndDeletesRootAfterJoiningTasks()
    {
        RetryHarness? harness = null;
        Task? loop = null;
        Task? retry = null;

        var failure = await Record.ExceptionAsync(async () =>
        {
            var owned = harness = new RetryHarness();
            try
            {
                await owned.StartAsync();
                owned.ReleasePrompt(TaskA);
                await owned.CompleteEnteredAsync(0);
                await owned.RetryParkedInDelayAsync(1);

                loop = owned.Loop;
                retry = owned.Retry;
                Assert.False(loop.IsCompleted);
                Assert.False(retry.IsCompleted);
                Assert.True(owned.RootExists, "precondition: the temp root must exist.");

                Assert.Fail("DELIBERATE-RETRY-HARNESS-PRIMARY");
            }
            finally
            {
                await owned.TeardownAsync();
            }
        });

        var primary = Assert.IsType<Xunit.Sdk.FailException>(failure);
        Assert.Contains("DELIBERATE-RETRY-HARNESS-PRIMARY", primary.Message, StringComparison.Ordinal);

        var disposed = Assert.IsType<RetryHarness>(harness);
        Assert.NotNull(loop);
        Assert.NotNull(retry);
        Assert.True(loop.IsCompleted, "teardown must join the real loop before returning.");
        Assert.True(retry.IsCompleted, "teardown must join the concrete retry before returning.");
        Assert.False(disposed.RootExists, "assertion-failure teardown must delete: " + disposed.RootPath);
    }

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
    /// STOP BEFORE THE PAYLOAD IS EVEN MAPPED: a MATCHING CANCEL arrives while the assignment's
    /// EXECUTION is still held, so reporting has not mapped, has not frozen and has not armed —
    /// there is no payload anywhere. The drain still closes retry admission, cancels the (never
    /// created) wait and JOINS the retained retry task, with NO acknowledgement involved, and the
    /// cancel's existing single fallback Ready is preserved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE BARRIER IS PRE-MAPPING, NOT PRE-WRITE. Waiting for the Complete write to ENTER would be
    /// too late: by then production has already mapped, frozen and armed. This vector instead holds
    /// the PROMPT — so the execution has not produced a result at all — and states that fact
    /// positively (<c>ReceiptArmed == false</c>, zero Completes, zero delay timers) before pushing
    /// the cancel.
    /// </para>
    /// <para>
    /// MUTATION PROOF. A drain that closed admission only AFTER joining reporting is killed here:
    /// once the execution is released, reporting maps/freezes/arms and the retry would then park in
    /// a five-second delay this fixture NEVER advances, so the retained retry task never terminates
    /// and its bounded join fails by name. The message ordinals are B-specific — the cancel is
    /// message 2 (the assignment is message 1) and the trailing probe is message 3 — so neither
    /// barrier can be satisfied by earlier traffic.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MatchingCancelBeforeMappingOrFreeze_ClosesAndJoinsTheRetryWithoutAnyAck()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();

            // THE EXECUTION IS HELD: the prompt never returns, so the executor cannot produce a
            // result and reporting cannot map, freeze or arm anything.
            await harness.PromptStartedAsync(TaskA);
            await harness.AwaitOwnerInstalledAsync();

            // THE CONCRETE retry task, captured BEFORE any drain can clear the ownership slot.
            var retry = harness.Retry;

            // POSITIVE PRE-MAPPING STATE: nothing armed, nothing written, no delay ever created.
            Assert.False(harness.ReceiptArmed, "Precondition: reporting has not mapped or armed.");
            Assert.False(harness.Reporting.IsCompleted);
            Assert.False(harness.Execution.IsCompleted);
            Assert.Empty(harness.Completes);
            Assert.Equal(0, harness.Clock.TimerCount);

            // THE MATCHING CANCEL — message 2 (the assignment was message 1), so this barrier is
            // cancel-specific and cannot be satisfied by the assignment's own consumption.
            var cancelConsumed = harness.ConsumedAsync(2);
            harness.PushCancel(TaskA);
            await cancelConsumed;

            // The matching-cancel drain cancels the assignment, which unblocks the held prompt;
            // execution and reporting then terminate and the drain joins the retry. The clock is
            // NEVER advanced, so a retry that was allowed to park in a delay could not terminate.
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                retry.IsCompletedSuccessfully,
                "The drain must close admission and join the retry cleanly, with no ACK.");

            // NO RETRY DELAY WAS EVER CREATED. This is the load-bearing discriminator: admission
            // was closed BEFORE the drain joined reporting, so even though reporting subsequently
            // produced and mapped the CANCELLED result (the executor's legitimate terminal
            // outcome), the retransmitter never entered a wait — and the clock, never advanced,
            // could not have released one.
            Assert.Equal(0, harness.Clock.TimerCount);
            Assert.Equal(0, harness.Clock.AdvanceCount);

            // …and nothing was ever RE-sent: at most the assignment's own single terminal Complete.
            Assert.True(
                harness.Completes.Count <= 1,
                $"No retransmission may occur; observed {harness.Completes.Count} Complete(s).");

            // The existing cancel fallback Ready is preserved exactly once. It is the cancel
            // handler's OWN direct write, so its completion is observed through a trailing probe
            // (message 3) the loop can only read AFTER the handler returned.
            await harness.ReadyEnteredAsync(0);
            harness.ReleaseReady(0);
            var afterCancel = harness.ConsumedAsync(3);
            harness.PushProbe("after-cancel-ready");
            await afterCancel;
            Assert.Equal(1, harness.ReadyCount);
            Assert.Equal(0, harness.SlotOccupancy);

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
            await harness.RetryParkedInDelayAsync(1);

            // THE CONCRETE retry task, captured BEFORE the EOF can clear the ownership slot. A
            // post-drain lookup would read null, which is also what an implementation that
            // cancelled and then ABANDONED the unwind produces — so it could never discriminate.
            var retry = harness.Retry;
            Assert.False(retry.IsCompleted);

            harness.CompleteStream();
            await harness.JoinAsync();

            // ORDERING CLAIM, taken SYNCHRONOUSLY at the instant the loop returned: the drain
            // JOINED the retry, so by the time the loop completed the EXACT retained task had
            // already terminated. No further await may intervene — awaiting first would let a
            // drain that merely cancelled and ABANDONED the unwind pass.
            Assert.True(
                retry.IsCompleted,
                "the drain must JOIN the retained retry task BEFORE the loop returns.");
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
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
            await harness.RetryParkedInDelayAsync(1);

            // THE CONCRETE retry task, captured BEFORE the EOF clears the ownership slot.
            var retry = harness.Retry;

            await harness.HoldSendGateAsync();
            harness.Advance(SpecifiedInterval);
            await harness.RetryQueuedOnSendGateAsync();
            Assert.False(retry.IsCompleted, "the retry must be parked on the permit queue.");

            // EOF while the retry is queued: teardown must still converge.
            harness.CompleteStream();
            await harness.JoinAsync();

            // ORDERING CLAIM, taken SYNCHRONOUSLY at the instant the loop returned: the permit
            // wait was cancelled AND JOINED, never abandoned.
            Assert.True(
                retry.IsCompleted,
                "the drain must JOIN the retained retry task BEFORE the loop returns.");
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

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
        var harness = new RetryHarness { UseFaultingReader = true, HoldRetries = true };
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);

            // The retry is PROVABLY parked in its delay.
            await harness.RetryParkedInDelayAsync(1);

            // THE CONCRETE retry task, captured BEFORE the fault can clear the ownership slot.
            var retry = harness.Retry;
            Assert.False(retry.IsCompleted);

            // ADMIT AND HOLD one retransmission write. This is what makes the JOIN observable: a
            // drain that merely cancels admission cannot finish this task, so the loop must
            // genuinely wait for the write's own termination.
            harness.Advance(SpecifiedInterval);
            await harness.CompleteEnteredAsync(1);
            Assert.False(harness.RetryWriteCompleted(1), "the retry write must be admitted and HELD.");
            Assert.False(retry.IsCompleted);

            // THE READER FAULT — the ACK is permanently absent (it is never pushed).
            harness.ArmReaderFault(new ReaderFaultPrimaryException("injected reader fault"));

            // IN-WINDOW PROOF: the loop CANNOT finish while the admitted write is held, because
            // the drain joins the retry. A drain that abandoned the unwind would finish here.
            var premature = await Record.ExceptionAsync(() =>
                harness.Loop.WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(premature);
            Assert.False(retry.IsCompleted);
            Assert.Equal(1, harness.SlotOccupancy);

            // RELEASE: the drain observes the write's own termination and then propagates the
            // reader's ORIGINAL exception.
            harness.ReleaseComplete(1);
            var propagated = await Assert.ThrowsAsync<ReaderFaultPrimaryException>(() =>
                harness.JoinAsync());
            Assert.Equal("injected reader fault", propagated.Message);

            // ORDERING CLAIM, taken SYNCHRONOUSLY at the instant the loop's fault surfaced: the
            // drain closed admission and JOINED the task — never merely detached it from the slot.
            Assert.True(
                retry.IsCompleted,
                "the drain must JOIN the retained retry task BEFORE the loop's fault surfaces.");
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // No Ready was emitted (the receipt was never confirmed) and ownership is empty.
            Assert.Equal(2, harness.Completes.Count);
            Assert.Equal(0, harness.ReadyCount);
            Assert.Equal(0, harness.SlotOccupancy);
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// RUN-CANCELLATION TEARDOWN WITH AN ADMITTED RETRY: cancelling the loop's own token is observed
    /// inside the retry writer, whose cancellation unwind is then held. The drain must join that
    /// concrete task before surfacing the run cancellation, with NO ACK and NO ordinary Ready.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. A drain that skipped the retry join surfaces the loop cancellation while the
    /// admitted writer is still held in its cancellation unwind; the bounded non-completion
    /// assertion then receives <see cref="OperationCanceledException"/> instead of timing out.
    /// </remarks>
    [Fact]
    public async Task RunCancellationWithParkedRetry_DrainsCancelsAndJoinsWithoutReady()
    {
        var harness = new RetryHarness
        {
            HoldRetries = true,
            HoldRetryCancellationUnwind = true,
        };
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryParkedInDelayAsync(1);

            // THE CONCRETE retry task, captured BEFORE cancellation can clear the slot. Admit its
            // write and hold it inside the writer; this transport consumes the STREAM token.
            var retry = harness.Retry;
            harness.Advance(SpecifiedInterval);
            await harness.CompleteEnteredAsync(1);
            Assert.False(harness.RetryWriteCompleted(1));
            Assert.False(retry.IsCompleted);

            // THE RUN CANCELLATION: no ACK and no EOF. The admitted writer observes stream-token
            // cancellation, then parks in its OWN deterministic cancellation-unwind barrier before
            // rethrowing. This is a real writer boundary, not a token-only assertion.
            harness.CancelLoopToken();
            await harness.RetryCancellationObservedAsync();
            Assert.False(retry.IsCompleted, "the retry must still be unwinding in the writer.");

            // IN-WINDOW JOIN PROOF. Correct production cannot let the loop surface cancellation
            // while the concrete retry is still unwinding. Removing the retry join returns here.
            var premature = await Record.ExceptionAsync(() =>
                harness.Loop.WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(premature);
            Assert.False(retry.IsCompleted);
            Assert.Equal(1, harness.SlotOccupancy);

            // RELEASE the writer's cancellation unwind. The retry now terminates and only then may
            // the loop's original cancellation surface.
            harness.ReleaseRetryCancellationUnwind();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.JoinAsync());

            Assert.True(
                retry.IsCompleted,
                "the teardown must JOIN the retained retry task BEFORE the loop unwinds.");
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The admitted attempt entered but never completed successfully, and no Ready emitted.
            Assert.Equal(2, harness.Completes.Count);
            Assert.Equal(0, harness.ReadyCount);
            Assert.Equal(0, harness.SlotOccupancy);
        }
        finally
        {
            harness.ReleaseRetryCancellationUnwind();
            await harness.TeardownAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8. Successor and sequential-run boundaries.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REPLACEMENT DRAIN'S RETRY JOIN, ISOLATED FROM THE READY JOIN. A real gated assignment's
    /// retry is admitted and HELD inside its transport writer while the receipt remains unconfirmed
    /// (therefore no ordinary Ready task exists). The real <c>DrainRetainedForReplacementAsync</c>
    /// member is invoked: it must remain incomplete until that admitted retry write terminates,
    /// then join the concrete retry and clear ownership.
    /// <para>
    /// REMOVAL PROOF. Removing ONLY the retry join makes the replacement drain return and clear
    /// ownership while the admitted retry writer remains parked; because no Ready was authorized,
    /// there is no co-guarding readiness join to hide that defect. The pre-release bounded
    /// non-completion and retained-owner assertions both fail under that mutant.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReplacementDrain_WithoutAReadyTask_JoinsTheAdmittedRetryBeforeClearing()
    {
        var harness = new RetryHarness { HoldRetries = true };
        Task? drain = null;
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryParkedInDelayAsync(1);

            // Admit and hold A's real retry while the receipt stays UNCONFIRMED: no Ready task can
            // exist, so the retry join is the replacement drain's ONLY blocker.
            harness.Advance(SpecifiedInterval);
            await harness.CompleteEnteredAsync(1);
            var owner = harness.Owner;
            var retry = harness.Retry;
            Assert.False(harness.ReceiptConfirmed);
            Assert.Null(harness.ReadinessSlotWrite);
            Assert.False(harness.RetryWriteCompleted(1));

            drain = harness.InvokeReplacementDrainAsync();

            // IN-WINDOW: the real replacement drain must not return or clear while the admitted
            // retry write is held. A retry-join removal returns here immediately.
            var premature = await Record.ExceptionAsync(() =>
                drain.WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(premature);
            Assert.Same(owner, harness.Owner);
            Assert.False(retry.IsCompleted);
            Assert.Equal(1, harness.SlotOccupancy);

            // The write's OWN termination releases the join; only then may replacement clear.
            harness.ReleaseComplete(1);
            await drain.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(retry.IsCompleted, "replacement must join the concrete retry task.");
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, harness.SlotOccupancy);
        }
        finally
        {
            if (drain is not null)
                await RetryHarness.ObserveForTeardownAsync(drain);
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// NO OLD RETRY ON A SUCCESSOR ASSIGNMENT, and the replacement drain JOINS an ADMITTED retry.
    /// Assignment A reaches its authorized Ready, then a RETRY of A's frozen completion is admitted
    /// and HELD inside the writer. The successor B arrives while that retry write is still parked:
    /// the replacement drain must JOIN it — proved IN-WINDOW, by showing B cannot reset the runner
    /// or start its prompt until the held write is released — and only then does B install with its
    /// OWN retry task and OWN frozen envelope. A's evidence never replays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY A'S RETRY MUST BE ADMITTED. ACK-ing A while its retry is merely DELAYED lets the drain
    /// join a task that was already finishing, so removing the retry join from the replacement path
    /// is not discriminated. Holding A's retry write open forces the drain to actually wait for it.
    /// </para>
    /// <para>
    /// MUTATION PROOF. A replacement that joined NEITHER A's retry nor A's authorized Ready resets
    /// the runner and starts B while A's write is still parked — the in-window "B must not have
    /// started" assertions fail by name. A retry that outlived the ownership clear is caught by the
    /// distinct-task and task-identity assertions on B's own sends.
    /// </para>
    /// <para>
    /// RESIDUAL, STATED HONESTLY. This vector cannot isolate "the retry join alone was removed":
    /// A's authorized Ready is queued on the SAME send permit behind A's admitted retry write, so
    /// the drain's readiness join independently blocks the replacement for exactly as long. That is
    /// a consequence of production's single-gate design, not a gap in the barrier — the
    /// retry-join-only mutant is isolated instead by the teardown vectors, where no Ready is ever
    /// authorized (the receipt stays unconfirmed) and the retry join is therefore the ONLY thing
    /// that can hold the drain.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SuccessorAssignment_ReplacementJoinsAnAdmittedRetry_ThenBGetsItsOwn()
    {
        var harness = new RetryHarness { HoldRetries = true };
        try
        {
            await harness.StartAsync();

            // ── ASSIGNMENT A ──────────────────────────────────────────────────────
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryParkedInDelayAsync(1);

            var ownerA = harness.Owner;
            var retryA = harness.Retry;

            // A's AUTHORIZED Ready — the pre-Ready boundary that makes a successor legitimate.
            // The ACK also closes A's retry ADMISSION, so A's retry must be admitted FIRST.
            harness.Advance(SpecifiedInterval);
            await harness.CompleteEnteredAsync(1);
            Assert.False(harness.RetryWriteCompleted(1), "A's retry write must be admitted and HELD.");

            // THE ACK authorizes the ordinary Ready. That Ready CANNOT enter the wire yet: it
            // serializes behind the admitted retry on the ONE send permit, so the positive
            // milestone is the gate's own waiter queue, and the retained slot write is the
            // authorization fact the successor boundary consults.
            harness.PushAck(TaskA);
            harness.PushProbe("ack-returned");
            await harness.ConsumedAsync(3);
            await harness.SenderQueuedOnSendGateAsync();
            var authorizedReadyA = harness.ReadinessSlotWrite
                ?? throw new Xunit.Sdk.XunitException("A's authorized Ready write must be retained.");
            Assert.Equal(0, harness.ReadyCount);

            // A's RETRY IS STILL PARKED INSIDE ITS ADMITTED WRITE: the ACK must not release it.
            Assert.False(retryA.IsCompleted, "the ACK must never cancel an admitted transport write.");
            Assert.False(harness.RetryWriteCompleted(1));

            // ── THE SUCCESSOR ARRIVES while A's retry write is still held ────────
            var resetBaseline = harness.ResetCount;
            harness.PushAssignment(TaskB);
            await harness.ConsumedAsync(4); // assignment A, ACK, probe, then B.

            // IN-WINDOW JOIN PROOF: B has been consumed, but the replacement drain is parked on
            // A's admitted retry write, so the runner has NOT been reset and B has NOT started.
            var prematureReset = await Record.ExceptionAsync(() =>
                harness.ResetCountReachedAsync(resetBaseline + 1)
                    .WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(prematureReset);
            Assert.Equal(resetBaseline, harness.ResetCount);
            Assert.False(harness.PromptStarts.Contains(TaskB), "B must not start while A's retry is held.");
            Assert.Same(ownerA, harness.Owner);
            Assert.False(retryA.IsCompleted);

            // RELEASE A's admitted retry write: the drain joins it, then joins A's queued Ready,
            // clears ownership and admits B.
            harness.ReleaseComplete(1);
            await retryA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(retryA.IsCompleted, "the replacement drain must JOIN A's admitted retry.");

            await harness.ReadyEnteredAsync(0);
            harness.ReleaseReady(0);
            await authorizedReadyA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            await harness.ResetCountReachedAsync(resetBaseline + 1);
            await harness.PromptStartedAsync(TaskB);

            // ── ASSIGNMENT B ──────────────────────────────────────────────────────
            var ownerB = harness.Owner;
            Assert.NotSame(ownerA, ownerB);

            var retryB = harness.RetryTask;
            Assert.NotNull(retryB);
            Assert.NotSame(retryA, retryB);

            harness.ReleasePrompt(TaskB);
            await harness.CompleteEnteredAsync(2);
            harness.ReleaseComplete(2);
            Assert.Equal(TaskA, harness.Completes[0].Complete.TaskId);
            Assert.Equal(TaskA, harness.Completes[1].Complete.TaskId); // A's admitted retry.
            Assert.Equal(TaskB, harness.Completes[2].Complete.TaskId);

            // B waits its OWN interval and then re-sends B's OWN evidence; A's never replays.
            await harness.RetryParkedInDelayAsync(2);
            harness.Advance(SpecifiedInterval);
            await harness.CompleteEnteredAsync(3);
            harness.ReleaseComplete(3);
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
    /// A SEQUENTIAL RUN STARTS WITH A FRESH RETRY BOUND TO THE NEW REGISTRATION'S OWN STREAM. Run 1
    /// ends with its retry joined and ownership cleared; run 2 publishes a SECOND connection with
    /// its OWN request-stream writer. Run 2's retry arms its OWN delay (its writer's clock evidence
    /// is counted from zero for that run) and its retransmission is written on the NEW connection's
    /// stream — the OLD writer receives NOTHING after run 1 ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY SEPARATE WRITERS. With one shared writer, "the third Complete names B" is satisfied by a
    /// retry writing on either stream, and a cumulative timer count cannot say WHICH run armed a
    /// delay. Per-connection writers make the target stream an observable, and the run-2 delay
    /// count is taken as a DELTA over run 1's, so run 2 must arm its own.
    /// </para>
    /// <para>
    /// MUTATION PROOF. A retransmitter bound to anything other than its OWN assignment's connection
    /// (for example a service-lifetime binding to the first connection ever published) writes run
    /// 2's retransmission onto writer 1: the new-stream count assertion and the old-stream
    /// no-further-writes assertion both fail by name. A run 2 that never armed its own delay fails
    /// the delta assertion by name.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SequentialRun_StartsAFreshRetry_ArmingItsOwnDelay_OnTheNewConnectionsStream()
    {
        var harness = new RetryHarness();
        try
        {
            // ── RUN 1 ─────────────────────────────────────────────────────────────
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryParkedInDelayAsync(1);

            var firstConnection = harness.Connection;
            var retry1 = harness.Retry;
            var delaysAfterRun1 = harness.Clock.TimerCount;
            Assert.Equal(1, delaysAfterRun1);
            Assert.Single(harness.CompletesOnWriter(0));

            // Run 1 ends: EOF drains, joins the retry and clears ownership.
            harness.CompleteStream();
            await harness.JoinAsync();
            await retry1.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(0, harness.SlotOccupancy);
            Assert.Equal(0, harness.ReadyCount);
            Assert.True(retry1.IsCompleted);

            var writesOnOldStreamAtRun1End = harness.CompletesOnWriter(0).Count;

            // ── RUN 2, on a NEW connection with its OWN request stream ────────────
            await harness.StartSecondRunAsync();

            harness.ReleasePrompt(TaskB);
            await harness.CompleteEnteredOnWriterAsync(1, 0);

            var secondConnection = harness.Connection;
            Assert.NotSame(firstConnection, secondConnection);

            var retry2 = harness.RetryTask;
            Assert.NotNull(retry2);
            Assert.NotSame(retry1, retry2);

            // RUN 2 ARMED ITS OWN DELAY: a NEW timer, over and above every timer run 1 created.
            await harness.RetryParkedInDelayAsync(delaysAfterRun1 + 1);
            Assert.Equal(delaysAfterRun1 + 1, harness.Clock.TimerCount);

            // THE RETRANSMISSION LANDS ON THE NEW CONNECTION'S STREAM.
            harness.Advance(SpecifiedInterval);
            await harness.CompleteEnteredOnWriterAsync(1, 1);

            var newStreamCompletes = harness.CompletesOnWriter(1);
            Assert.Equal(2, newStreamCompletes.Count);
            Assert.Equal(TaskB, newStreamCompletes[0].Complete.TaskId);
            Assert.Equal(TaskB, newStreamCompletes[1].Complete.TaskId);

            // …and the OLD stream received NOTHING after run 1 ended: the joined retry cannot
            // write on a connection whose run is over, nor can it be retargeted.
            Assert.Equal(writesOnOldStreamAtRun1End, harness.CompletesOnWriter(0).Count);
            Assert.Equal(TaskA, harness.CompletesOnWriter(0)[0].Complete.TaskId);

            harness.CompleteStream();
            await harness.JoinAsync();
            Assert.Equal(writesOnOldStreamAtRun1End, harness.CompletesOnWriter(0).Count);
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// AN INVALID ACK NEVER CLOSES RETRY ADMISSION. A wrong-TASK ack, a wrong-WORKER ack and a
    /// DUPLICATE-before-confirmation ack are each consumed as no-ops: the receipt stays unconfirmed,
    /// the ordinary Ready stays withheld — and, crucially, ADVANCING THE RETRY CLOCK AFTERWARDS
    /// still produces the retransmission, proving the retry was never closed.
    /// </summary>
    /// <remarks>
    /// MUTATION PROOF. A handler that closed admission on ANY incoming ack (rather than only on the
    /// FIRST ACCEPTED one) leaves the retry stopped here: the post-ack advance then produces no
    /// attempt and the rendezvous fails by name. Ready-suppression alone cannot discriminate that
    /// mutant, which is precisely why the advance is part of this vector.
    /// </remarks>
    [Theory]
    [InlineData(InvalidAckKind.WrongTask)]
    [InlineData(InvalidAckKind.WrongWorker)]
    [InlineData(InvalidAckKind.PreviousConnection)]
    [InlineData(InvalidAckKind.RepeatedWrongTask)]
    public async Task InvalidAck_NeverClosesRetryAdmission_TheRetryStillHappensAfterwards(
        InvalidAckKind kind)
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryParkedInDelayAsync(1);

            var retry = harness.Retry;

            // THE INVALID ACK, consumed as a no-op (the trailing probe is the handler-return
            // barrier: the loop re-arms its next read only after the handler returned).
            switch (kind)
            {
                case InvalidAckKind.WrongTask:
                    harness.PushAck(TaskB);
                    break;
                case InvalidAckKind.WrongWorker:
                    harness.PushAckForWorker(TaskA, "worker-impostor");
                    break;
                case InvalidAckKind.PreviousConnection:
                    harness.DeliverAckOnPreviousConnection(TaskA);
                    break;
                case InvalidAckKind.RepeatedWrongTask:
                    harness.PushAck(TaskB);
                    harness.PushAck(TaskB);
                    break;
                default:
                    throw new Xunit.Sdk.XunitException($"Unhandled invalid-ack kind: {kind}");
            }

            harness.PushProbe("invalid-ack-returned");
            await harness.ConsumedAsync(kind switch
            {
                InvalidAckKind.PreviousConnection => 2, // assignment + probe; ACK invoked directly.
                InvalidAckKind.RepeatedWrongTask => 4, // assignment + two wrong ACKs + probe.
                _ => 3, // assignment + one invalid ACK + probe.
            });

            // NOTHING WAS CONFIRMED and the Ready stays withheld.
            Assert.False(harness.ReceiptConfirmed);
            Assert.Equal(0, harness.ReadyCount);
            Assert.False(retry.IsCompleted, "an invalid ACK must never end the retry task.");

            // THE DISCRIMINATOR: the retry is STILL live, so the interval still produces an attempt.
            harness.Advance(SpecifiedInterval);
            await harness.CompleteEnteredAsync(1);
            Assert.Equal(2, harness.Completes.Count);
            Assert.Equal(TaskA, harness.Completes[1].Complete.TaskId);
            await harness.RetryParkedInDelayAsync(2);

            // …and the EXACT ack then closes it, so the suppression that follows is meaningful.
            var consumedBeforeExact = kind switch
            {
                InvalidAckKind.PreviousConnection => 2,
                InvalidAckKind.RepeatedWrongTask => 4,
                _ => 3,
            };
            harness.PushAck(TaskA);
            harness.PushProbe("exact-ack-returned");
            await harness.ConsumedAsync(consumedBeforeExact + 2);
            Assert.True(harness.ReceiptConfirmed);
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            harness.Advance(SpecifiedInterval);
            await AssertNoRetrySendAsync(harness, "after the EXACT ACK closed admission");
            Assert.Equal(2, harness.Completes.Count);

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

    /// <summary>
    /// A DUPLICATE ACK AFTER CONFIRMATION changes nothing — and, because the FIRST accepted ack
    /// already closed admission, the retry stays closed across the duplicate: advancing the clock
    /// afterwards still produces no attempt, and exactly one Ready was ever emitted.
    /// </summary>
    /// <remarks>
    /// This is the complement of the invalid-ack rows: there the advance proves the retry was NOT
    /// closed; here it proves a duplicate cannot REOPEN it (nor duplicate the Ready).
    /// </remarks>
    [Fact]
    public async Task DuplicateAckAfterConfirmation_LeavesTheRetryClosed_AndEmitsNoSecondReady()
    {
        var harness = new RetryHarness();
        try
        {
            await harness.StartAsync();
            harness.ReleasePrompt(TaskA);
            await harness.CompleteEnteredAsync(0);
            await harness.RetryParkedInDelayAsync(1);

            var retry = harness.Retry;

            // THE EXACT ACK closes admission and authorizes the single Ready.
            harness.PushAck(TaskA);
            harness.PushProbe("first-ack-returned");
            await harness.ConsumedAsync(3);
            Assert.True(harness.ReceiptConfirmed);
            await retry.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            await harness.ReadyEnteredAsync(0);
            harness.ReleaseReady(0);
            await harness.JoinedReadyWriteAsync();
            Assert.Equal(1, harness.ReadyCount);

            // THE DUPLICATE: consumed, and it changes nothing at all.
            harness.PushAck(TaskA);
            harness.PushProbe("duplicate-ack-returned");
            await harness.ConsumedAsync(5);

            harness.Advance(SpecifiedInterval * 2);
            await AssertNoRetrySendAsync(harness, "after a DUPLICATE ACK following confirmation");
            Assert.Single(harness.Completes);
            Assert.Equal(1, harness.ReadyCount);
            Assert.True(retry.IsCompleted);

            harness.CompleteStream();
            await harness.JoinAsync();
            Assert.Equal(1, harness.ReadyCount);
        }
        finally
        {
            await harness.TeardownAsync();
        }
    }

    /// <summary>
    /// The kinds of INVALID acknowledgement the retry-admission vectors drive. Public so an xUnit
    /// theory can take it as a parameter.
    /// </summary>
    public enum InvalidAckKind
    {
        /// <summary>A correct worker identity but a DIFFERENT task id.</summary>
        WrongTask,

        /// <summary>The correct task id but a DIFFERENT worker identity.</summary>
        WrongWorker,

        /// <summary>Matching wire identities delivered through a DIFFERENT connection object.</summary>
        PreviousConnection,

        /// <summary>The same wrong-task acknowledgement delivered twice before any confirmation.</summary>
        RepeatedWrongTask,
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

        /// <summary>
        /// ONE REQUEST-STREAM WRITER PER RUN. A sequential run publishes a NEW connection with its
        /// OWN writer, so "which stream did this write land on" is an observable rather than an
        /// inference from a shared log. <see cref="Completes"/> and the release/entry milestones
        /// address the CURRENT run's writer; <see cref="CompletesOnWriter"/> addresses any run's.
        /// </summary>
        private readonly List<RetransmissionWriter> _writers = [];
        private readonly string _root;

        /// <summary>One-way latch making <see cref="TeardownAsync"/> idempotent.</summary>
        private int _tornDown;
        private readonly IDisposable _gitRestore;
        private readonly IDisposable _consoleRestore;
        private readonly List<Task> _loops = [];
        private readonly List<WorkerConnection> _connections = [];
        private readonly List<WorkerConnection> _foreignConnections = [];
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
        /// <remarks>
        /// The hold/fault switches are stored as CONFIGURATION rather than pushed into a writer
        /// instance, because each run builds its OWN writer (see <c>_writers</c>). They are applied
        /// when a run's writer is created, so a sequential run inherits the same configured shape.
        /// </remarks>
        internal bool HoldOriginalComplete { get; init; }

        /// <summary>Whether RETRY Complete writes (index &gt;= 1) park until released.</summary>
        internal bool HoldRetries { get; init; }

        /// <summary>
        /// Whether an admitted retry write, after observing stream-token cancellation, must park in
        /// a deterministic cancellation-unwind barrier before it can rethrow and terminate.
        /// </summary>
        internal bool HoldRetryCancellationUnwind { get; init; }

        /// <summary>
        /// Whether READY writes park until released — which is what makes a FAILING Ready fault come
        /// from inside a genuine suspension (exactly as a real gRPC writer's does) rather than
        /// synchronously out of the settlement that starts the write.
        /// </summary>
        internal bool HoldReadies { get; init; }

        /// <summary>A ONE-SHOT failure for the next RETRY write (applied to the FIRST run's writer).</summary>
        internal Exception? FailNextRetryWrite { get; init; }

        /// <summary>A ONE-SHOT failure for the first Ready write (applied to the FIRST run's writer).</summary>
        internal Exception? FailFirstReadyWrite { get; init; }

        /// <summary>The CURRENT run's request-stream writer.</summary>
        private RetransmissionWriter Writer => _writers[^1];

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
        internal IReadOnlyList<WorkerMessage> Completes => Writer.Completes;

        /// <summary>
        /// The RAW writer arguments for every Complete — the EXACT objects production handed to
        /// <c>WriteAsync</c>, never cloned by the fixture. These are the only values the
        /// private-snapshot ownership assertions may use.
        /// </summary>
        internal IReadOnlyList<WorkerMessage> RawCompletes => Writer.RawCompletes;

        internal int ReadyCount => Writer.ReadyCount;

        /// <summary>
        /// The Completes recorded by the run at <paramref name="runIndex"/>'s OWN writer (0 is the
        /// first run). This is what makes "which stream did this land on" observable across a
        /// sequential run rather than an inference from a shared log.
        /// </summary>
        internal IReadOnlyList<WorkerMessage> CompletesOnWriter(int runIndex) =>
            _writers[runIndex].Completes;

        /// <summary>Entry milestone for a Complete on a SPECIFIC run's writer.</summary>
        internal Task CompleteEnteredOnWriterAsync(int runIndex, int index) =>
            _writers[runIndex].CompleteEntered(index)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        /// <summary>The number of prompt invocations that ENTERED (one per execution).</summary>
        internal int ExecutionCount => _runner.PromptCount;

        /// <summary>Task ids whose prompt ever started, in order.</summary>
        internal IReadOnlyList<string> PromptStarts => _runner.StartedTaskIds;

        /// <summary>
        /// How many runner session RESETS production has entered. The reset is the FIRST production
        /// step after a replacement drain returns, so this is the load-bearing witness that a
        /// successor was actually admitted (rather than an end-state guess about the prompt).
        /// </summary>
        internal int ResetCount => _runner.ResetCount;

        /// <summary>Completes once production has entered at least <paramref name="count"/> resets.</summary>
        internal Task ResetCountReachedAsync(int count) =>
            _runner.ResetCountReached(count).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

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

        /// <summary>
        /// Whether the assignment's receipt has been ARMED — i.e. reporting mapped a result and
        /// froze the envelope. It is the positive witness a "before payload" barrier needs to state
        /// WHICH side of the mapping it is on.
        /// </summary>
        internal bool ReceiptArmed => (bool)ReadReceiptProperty("IsArmed");

        /// <summary>Whether the writer observed the retry write at <paramref name="index"/> COMPLETE.</summary>
        internal bool RetryWriteCompleted(int index) => Writer.CompleteCompleted(index);

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

            // THIS RUN'S OWN WRITER, configured with the vector's hold/fault shape. A sequential
            // run therefore gets a SEPARATE stream, which is what makes "run 2's retransmission
            // landed on the NEW connection" an observation rather than an inference. The one-shot
            // faults belong to the FIRST run's writer only (a second run must not inherit a
            // consumed one-shot).
            var writer = new RetransmissionWriter
            {
                HoldOriginalComplete = HoldOriginalComplete,
                HoldRetries = HoldRetries,
                HoldRetryCancellationUnwind = HoldRetryCancellationUnwind,
                HoldReadies = HoldReadies,
            };

            if (_writers.Count == 0)
            {
                writer.FailNextRetryWrite = FailNextRetryWrite;
                writer.FailNextReadyWrite = FailFirstReadyWrite;
            }

            _writers.Add(writer);

            var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
                writer, reader,
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

        /// <summary>
        /// Pushes an acknowledgement carrying an ARBITRARY worker identity — the wrong-WORKER
        /// invalid-ack vector. Every other field matches, so only the identity discriminates.
        /// </summary>
        internal void PushAckForWorker(string taskId, string workerId) => PushToReader(new OrchestratorMessage
        {
            CompletionReceiptAck = new CompletionReceiptAck { TaskId = taskId, WorkerId = workerId },
        });

        /// <summary>
        /// Delivers a matching acknowledgement through a DIFFERENT, unpublished connection object
        /// — the previous/stale-connection vector the real reader cannot manufacture. It invokes
        /// the SAME private handler the loop's ACK case calls, with only the delivering connection
        /// changed; the foreign connection is retained and retired during teardown.
        /// </summary>
        internal void DeliverAckOnPreviousConnection(string taskId)
        {
            var reader = new ChannelResponseReader();
            var writer = new RetransmissionWriter();
            var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
                writer, reader,
                _ => Task.FromResult(new Metadata()),
                _ => new Status(StatusCode.OK, string.Empty),
                _ => new Metadata(),
                _ => { },
                null!);
            var previous = TestConnectionFactory.CreateUnpublished(
                AssignedWorkerId, stream,
                completionReceiptAckEnabled: true,
                completionReadyRequired: true);
            _foreignConnections.Add(previous);

            typeof(WorkerService)
                .GetMethod("HandleCompletionReceiptAck", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_service, [
                    previous,
                    new CompletionReceiptAck { TaskId = taskId, WorkerId = AssignedWorkerId },
                ]);
        }

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
            Writer.CompleteEntered(index).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        internal void ReleaseComplete(int index) => Writer.ReleaseComplete(index);

        internal Task RetryCancellationObservedAsync() =>
            Writer.RetryCancellationObserved.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        internal void ReleaseRetryCancellationUnwind() => Writer.ReleaseRetryCancellationUnwind();

        internal Task ReadyEnteredAsync(int index) =>
            Writer.ReadyEntered(index).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        internal void ReleaseReady(int index) => Writer.ReleaseReady(index);

        internal Task RetryDelayCreatedAsync(int count) =>
            Clock.WaitForTimerCountAsync(count, TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        /// <summary>
        /// POSITIVE, NON-POLLING evidence that the retry has PARKED IN ITS DELAY: the creation of
        /// the <paramref name="count"/>-th delay timer through the production clock seam. The
        /// rendezvous is completed by the creation itself, so abandoning it on a shorter bounded
        /// wait leaves no stray poller behind.
        /// </summary>
        internal Task RetryParkedInDelayAsync(int count) =>
            Clock.WaitForRetryParkedInDelayAsync(count, TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        /// <summary>Advances the CURRENT assignment's clock by an EXPLICIT amount.</summary>
        internal void Advance(TimeSpan delta) => Clock.Advance(delta);

        /// <summary>
        /// Replaces the SERVICE's <c>TimeProvider</c> with a FRESH manual clock and returns it —
        /// used strictly AFTER an assignment has been constructed, to prove that assignment keeps
        /// using the provider it captured. <see cref="Clock"/> keeps returning the ORIGINAL clock.
        /// </summary>
        internal ManualRetransmissionClock SwapServiceClock()
        {
            var replacement = new ManualRetransmissionClock();
            typeof(WorkerService)
                .GetProperty("TimeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(_service, replacement);
            return replacement;
        }

        internal Task JoinedReportingAsync() =>
            Reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        internal Task JoinedExecutionAsync() =>
            Execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        /// <summary>
        /// Joins the CURRENT owner's retry task. It FAILS LOUDLY when the ownership slot is already
        /// empty rather than returning a completed task: a drain vector must capture the CONCRETE
        /// retry task BEFORE it triggers the drain, because "the slot is null" is exactly the state
        /// an implementation that cancels and then ABANDONS the unwind would also produce.
        /// </summary>
        internal Task JoinedRetryAsync() =>
            (RetryTask ?? throw new Xunit.Sdk.XunitException(
                "No retry task is retained — capture the CONCRETE task before triggering the drain."))
            .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

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

        /// <summary>
        /// POSITIVE proof that SOME sender (here the authorized Ready) is PARKED on the production
        /// permit queue behind an admitted write — the gate's own async waiter list reports one.
        /// </summary>
        internal Task SenderQueuedOnSendGateAsync() =>
            SendGateObserver.WaitForWaitersAsync(_sendGate, 1, TestContext.Current.CancellationToken);

        /// <summary>The production send gate's current count: 0 while a write holds the permit.</summary>
        internal int SendGateCurrentCount => _sendGate.CurrentCount;

        /// <summary>
        /// Completes once the FIRST Ready write has ENTERED the writer — used AFTER the admitted
        /// retry terminated, so the assertion proves the queued Ready entered only after that
        /// release rather than racing it.
        /// </summary>
        internal Task ReadyWriteEnteredAfterRetryTerminatedAsync() =>
            Writer.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        // ── lifecycle ────────────────────────────────────────────────────────────

        internal Task JoinAsync() =>
            Loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        /// <summary>
        /// Invokes the REAL replacement drain against the CURRENT retained owner. Used to isolate
        /// the retry join from the pre-Ready protocol boundary and from any readiness write join.
        /// </summary>
        internal Task InvokeReplacementDrainAsync() =>
            (Task)typeof(WorkerService)
                .GetMethod("DrainRetainedForReplacementAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_service, null)!;

        internal static Task ObserveForTeardownAsync(Task task) => ObserveAsync(task);

        /// <summary>
        /// Releases every parked gate, joins every started task within the bound, retires the
        /// connections, disposes the service and DELETES the temp root — so a failing assertion
        /// cannot leave a producer stuck or a <c>/tmp/copilothive-retry-*</c> directory behind.
        /// </summary>
        /// <remarks>
        /// THE ORDER MATTERS: every join and disposal happens FIRST, so nothing still holds a file
        /// under the root when <see cref="Dispose"/> deletes it. The delete itself is best-effort
        /// and never throws, so it can never replace a primary assertion failure.
        /// </remarks>
        internal async Task TeardownAsync()
        {
            // IDEMPOTENT: every vector calls this in its finally, and a vector that also calls it
            // explicitly (the temp-root vector) must not then cancel an already-disposed CTS or
            // dispose the service twice. The FIRST caller performs the teardown; later callers are
            // no-ops, which keeps the single teardown path shared by every vector.
            if (Interlocked.Exchange(ref _tornDown, 1) != 0)
                return;

            ReleaseSendGate();
            _runner.ReleaseAll();
            foreach (var writer in _writers)
                writer.ReleaseAll();

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
            foreach (var connection in _foreignConnections)
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

            // THE TEMP ROOT IS ACTUALLY DELETED — best effort, and LAST, so the console/git seam
            // restores above always happen even if the delete fails. A leaked
            // /tmp/copilothive-retry-* directory per test is what this closes.
            TryDeleteRoot();
        }

        /// <summary>
        /// Best-effort recursive delete of this harness's temp root. It NEVER throws: a teardown
        /// failure must not replace the test's primary assertion failure, and a locked file on a
        /// failing path is not the outcome under test.
        /// </summary>
        private void TryDeleteRoot()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>Whether this harness's temp root still exists on disk.</summary>
        internal bool RootExists => Directory.Exists(_root);

        /// <summary>The harness's temp root path, for the teardown assertion.</summary>
        internal string RootPath => _root;
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

        /// <summary>
        /// THE RAW WRITER ARGUMENTS — the EXACT <see cref="WorkerMessage"/> objects production handed
        /// to <c>WriteAsync</c>, retained WITHOUT any fixture-side clone.
        /// </summary>
        /// <remarks>
        /// WHY BOTH LISTS EXIST. <see cref="Completes"/> keeps a defensive CLONE so a fixture that
        /// only reads payload values is unaffected by later mutation. That clone would, however,
        /// MASK a production mutant that hands the SAME frozen <c>TaskComplete</c> to every writer:
        /// two clones of one object are still two objects. The raw list is therefore the ONLY
        /// evidence the private-snapshot ownership assertions use — reference distinctness of the
        /// raw arguments, plus mutating a raw argument and proving the NEXT raw argument is
        /// unaffected.
        /// </remarks>
        private readonly List<WorkerMessage> _rawCompletes = [];
        private readonly List<WorkerMessage> _readies = [];
        private readonly List<bool> _completeDone = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyCountWaiters = [];
        private readonly TaskCompletionSource _retryCancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _retryCancellationRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _releaseImmediately;
        private Exception? _failNextRetryWrite;
        private Exception? _failNextReadyWrite;

        internal bool HoldOriginalComplete { get; init; }

        internal bool HoldRetries { get; init; }

        internal bool HoldRetryCancellationUnwind { get; init; }

        internal Task RetryCancellationObserved => _retryCancellationObserved.Task;

        internal void ReleaseRetryCancellationUnwind() => _retryCancellationRelease.TrySetResult();

        /// <summary>
        /// Whether READY writes park until released. A failing Ready must be raised from INSIDE a
        /// genuine suspension, exactly as a real gRPC writer's fault is, rather than synchronously
        /// out of the settlement that starts the write.
        /// </summary>
        internal bool HoldReadies { get; init; }

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

        /// <summary>
        /// The RAW writer arguments, oldest first — the exact objects production passed to
        /// <c>WriteAsync</c>, never cloned by this fixture.
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
                _retryCancellationRelease.TrySetResult();
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

                // THE RAW ARGUMENT, retained BY REFERENCE. This is the object identity the
                // private-snapshot assertions compare and mutate; cloning it here would make a
                // "same frozen object handed to every writer" mutant indistinguishable.
                _rawCompletes.Add(message);
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
            try
            {
                await release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (index > 0 && HoldRetryCancellationUnwind)
            {
                // POSITIVE writer-boundary evidence for the run-cancellation drain vector. The
                // stream token was genuinely observed inside the admitted retry write; hold the
                // unwind until the test releases it so an awaited join is distinguishable from a
                // drain that cancels and abandons the concrete retry task.
                _retryCancellationObserved.TrySetResult();
                await _retryCancellationRelease.Task;
                throw;
            }

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

        /// <summary>
        /// How many session RESETS production has ENTERED. Resets are counted by ORDER, never keyed
        /// by task id: the executor sets the runner's current-task id AFTER the reset, so the id
        /// visible inside a reset is the PREDECESSOR's.
        /// </summary>
        internal int ResetCount { get { lock (_gate) return _resetCount; } }

        private int _resetCount;
        private readonly List<(int Count, TaskCompletionSource Waiter)> _resetWaiters = [];

        /// <summary>
        /// Completes once production has entered at least <paramref name="count"/> resets — a
        /// creation-signalled rendezvous, never a poll.
        /// </summary>
        internal Task ResetCountReached(int count)
        {
            lock (_gate)
            {
                if (_resetCount >= count)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _resetWaiters.Add((count, waiter));
                return waiter.Task;
            }
        }

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
        {
            List<TaskCompletionSource> satisfied = [];
            lock (_gate)
            {
                _resetCount++;
                for (var i = _resetWaiters.Count - 1; i >= 0; i--)
                {
                    var (count, waiter) = _resetWaiters[i];
                    if (_resetCount >= count)
                    {
                        satisfied.Add(waiter);
                        _resetWaiters.RemoveAt(i);
                    }
                }
            }

            foreach (var waiter in satisfied)
                waiter.TrySetResult();

            return Task.CompletedTask;
        }
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
