using CopilotHive.Dashboard;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Git;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;

using CopilotHive.Tests.Persistence;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

using Moq;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcTaskComplete = CopilotHive.Shared.Grpc.TaskComplete;

namespace CopilotHive.Tests;

/// <summary>
/// THE BOUNDED COMPLETION/IDLE-RELEASE GUARD, driven through the REAL
/// <see cref="HiveOrchestratorService.WorkStream"/> loop.
/// <para>
/// A completion is acted on ONLY when BOTH ownership authorities still agree: the pool (the pinned
/// instance is registered, busy and executing the completing task) and the queue (an ACTIVE entry
/// for that exact task id, assigned to this worker). Every other observation — missing entry,
/// foreign assigned_worker, a stale instance while a successor holds a DISTINCT task id — is
/// IGNORED: nothing released, nothing removed, nothing notified.
/// </para>
/// <para>
/// A Ready is likewise ignored while the observed task still has an ACTIVE queue entry; the
/// initial Ready and the Ready that follows an ACCEPTED completion keep working.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// THE THREE TOPOLOGY RULES EVERY VECTOR HERE OBEYS, because the evidence is worthless without
/// them:
/// </para>
/// <list type="number">
///   <item><description>ONE NOTIFIER, REAL DOWNSTREAM. The transport and the real
///     <see cref="GoalDispatcher"/> share a SINGLE <see cref="TaskCompletionNotifier"/>, and the
///     dispatcher is the LAST subscriber — so production's own <c>NotifyAsync</c> AWAITS the real
///     <see cref="GoalDispatcher.HandleTaskCompletionAsync"/> chain. Downstream evidence is read
///     from the REAL <c>TaskCompletionService</c> log lines, never from a test-owned
///     counter.</description></item>
///   <item><description>PUBLICATION IS OBSERVED AT THE gRPC WRITER. The real <c>WorkStream</c>
///     pump is the ONLY consumer of the worker's message channel; a publication is observed where
///     the pump forwards it — at the <see cref="IServerStreamWriter{T}"/> — so no test reader ever
///     races the production pump.</description></item>
///   <item><description>EVERY REFUSAL IS BARRIERED AND THE STREAM IS STRICTLY JOINED. A refusal is
///     only asserted after a POST-HANDLER BARRIER proves the handler RETURNED (see
///     <see cref="Harness.BarrierAsync"/>), and the shared teardown joins the producer with a
///     bound where a timeout or a terminal fault is a TEST FAILURE. Deleting an early return
///     therefore cannot hide behind a swallowed fault.</description></item>
/// </list>
/// <para>
/// No sleeps, no live dependencies, no fire-and-forget producers: every started task is retained
/// and joined.
/// </para>
/// </remarks>
public sealed class CompletionTransportOwnershipTests
{
    /// <summary>Bound applied to every await; a hang becomes a named failure, never a stall.</summary>
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    private const string WorkerId = "ownership-worker";

    /// <summary>
    /// THE ONE LIFECYCLE every vector runs through: the body, then the harness's SHARED STRICT
    /// teardown, on EVERY path.
    /// </summary>
    /// <remarks>
    /// THE PRIMARY FAILURE STAYS AUTHORITATIVE: the body's exception is rethrown UNCHANGED (via
    /// <see cref="ExceptionDispatchInfo"/>, so its message and stack survive), and the teardown's
    /// own outcome is surfaced only when the body SUCCEEDED. A cleanup failure is therefore never
    /// swallowed and never replaces the assertion the reviewer needs to see.
    /// </remarks>
    /// <param name="harness">The harness whose strict teardown must run.</param>
    /// <param name="body">The vector's assertions.</param>
    private static async Task RunAsync(Harness harness, Func<Task> body)
    {
        ExceptionDispatchInfo? primary = null;
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            primary = ExceptionDispatchInfo.Capture(ex);
        }

        // THE STRICT TEARDOWN RUNS ON EVERY PATH and never throws — it reports instead.
        var cleanupFailure = await harness.StopAsync();

        primary?.Throw();

        if (cleanupFailure is not null)
            throw cleanupFailure;

        // THE PRODUCER POST-CONDITION, asserted for every green vector: nothing is left live.
        Assert.True(harness.StreamEnded, "the WorkStream producer is still live after teardown");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (1) THE ACCEPTED COMPLETION — the positive control
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// VALID ACTIVE OWNERSHIP IS ACCEPTED: the worker is released, its model cleared, that exact
    /// active entry removed, and the completion genuinely reaches the REAL downstream dispatcher.
    /// </summary>
    [Fact]
    public async Task Completion_WithValidActiveOwnership_ReleasesAndNotifies()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-valid", model: "assigned-model");

            var result = await h.CompleteAndAwaitDownstreamAsync("task-valid");

            Assert.Equal("task-valid", result.TaskId);
            Assert.Equal("assigned-model", result.Model);

            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Worker.CurrentTaskId);
            Assert.Null(h.Worker.CurrentModel);
            Assert.Null(h.Queue.GetActiveTask("task-valid"));

            // THE REAL DOWNSTREAM CHAIN RAN — evidence from the production TaskCompletionService
            // log line, not from a test counter.
            Assert.Equal(1, h.DownstreamHandledCount("task-valid"));
        });
    }

    /// <summary>
    /// HasModel SEMANTICS SURVIVE THE GUARD for valid active ownership: an EXPLICIT empty value
    /// wins over the queue's model rather than falling back to it.
    /// </summary>
    [Fact]
    public async Task Completion_ExplicitEmptyModel_BeatsQueueFallback()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-explicit-empty", model: "queue-model");

            var result = await h.CompleteAndAwaitDownstreamAsync(
                "task-explicit-empty", model: "", modelPresent: true);

            Assert.Equal("", result.Model);
        });
    }

    /// <summary>
    /// HasModel SEMANTICS SURVIVE THE GUARD: an ABSENT field falls back to the VALIDATED active
    /// queue entry's model.
    /// </summary>
    [Fact]
    public async Task Completion_AbsentModel_UsesValidatedQueueEntryModel()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-absent-model", model: "queue-model");

            var result = await h.CompleteAndAwaitDownstreamAsync("task-absent-model");

            Assert.Equal("queue-model", result.Model);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2b) THE COMPLETION-RECEIPT RETENTION
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ACCEPTED COMPLETION'S RECEIPT IS REAL AND DURABLE, AND IT ALREADY EXISTS AT THE MOMENT
    /// THE DOWNSTREAM NOTIFICATION IS OBSERVED: the same invocation that released the worker and
    /// notified the real downstream dispatcher left a receipt row written by the REAL recorder over
    /// the REAL stores, carrying the mapped result and the recorded slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE ORDERING EVIDENCE IS TAKEN INSIDE THE NOTIFICATION ITSELF, not after it. The shared
    /// notifier's FIRST subscriber probes the REAL receipt store for this task id and captures the
    /// row BEFORE the awaiting test is released (see
    /// <see cref="CompletionObservations.ReceiptProbe"/>). A regression that published the
    /// completion BEFORE persisting the receipt would therefore observe a MISSING row at that
    /// instant and fail here — which a post-call readback alone cannot detect, because it would be
    /// satisfied by a write that landed later.
    /// </para>
    /// <para>
    /// THE POST-CALL READBACK IS KEPT as additional DURABILITY evidence: the row is still there,
    /// through a fresh store, after the whole handler returned.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Completion_Accepted_RetainsDurableReceiptAndReleasesOnce()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-receipt-valid", model: "assigned-model");

            // The assignment's own dashboard notification is excluded, so the count asserted below
            // is the COMPLETION's own.
            h.ResetDashboardNotifications();

            var result = await h.CompleteAndAwaitDownstreamAsync("task-receipt-valid");

            // THE EXISTING CHECKED RELEASE AND THE ONE NOTIFICATION still happen.
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Worker.CurrentTaskId);
            Assert.Null(h.Queue.GetActiveTask("task-receipt-valid"));
            Assert.Equal(1, h.DownstreamHandledCount("task-receipt-valid"));
            Assert.Equal(1, h.TransportNotifications);
            Assert.Equal("task-receipt-valid", result.TaskId);

            // …and EXACTLY ONE dashboard state-change for the accepted completion.
            Assert.Equal(1, h.DashboardNotifications);

            // ── THE RECEIPT ALREADY EXISTED WHEN THE NOTIFICATION WAS OBSERVED ───────────────
            // Read from INSIDE the notifier observation, through a real store load, before this
            // test was released. This is the record-BEFORE-notification ordering proof.
            var observed = h.LastObservation;
            Assert.NotNull(observed);
            Assert.Null(observed!.ProbeFailure);
            Assert.NotNull(observed.ReceiptAtNotification);

            var atNotification = observed.ReceiptAtNotification!.Receipt;
            Assert.Equal("goal-ownership", atNotification.GoalId);
            Assert.Equal(WorkerId, atNotification.WorkerId);
            Assert.Equal(DomainWorkerRole.Coder, atNotification.Role);
            Assert.Equal("task-receipt-valid", atNotification.Slot.TaskId);
            Assert.Equal(GoalPhase.Coding, atNotification.Slot.Position!.Phase);
            Assert.Equal(1, atNotification.Slot.Position.Iteration);
            Assert.Equal(1, atNotification.Slot.Position.Occurrence);
            Assert.Equal(1, atNotification.Slot.Attempt);
            Assert.Equal("output-task-receipt-valid", atNotification.Result.Output);
            Assert.Equal("assigned-model", atNotification.Result.Model);
            Assert.Equal(TaskOutcome.Completed, atNotification.Result.Status);

            // THE REAL RECEIPT ROW is still readable afterwards, and it carries what the RECORDED
            // assignment and the MAPPED result actually said — the durability half of the evidence.
            var loaded = h.ReadReceipt("task-receipt-valid");
            Assert.NotNull(loaded);
            Assert.Equal("goal-ownership", loaded!.Receipt.GoalId);
            Assert.Equal(WorkerId, loaded.Receipt.WorkerId);
            Assert.Equal(DomainWorkerRole.Coder, loaded.Receipt.Role);
            Assert.Equal("task-receipt-valid", loaded.Receipt.Slot.TaskId);
            Assert.Equal(GoalPhase.Coding, loaded.Receipt.Slot.Position!.Phase);
            Assert.Equal(1, loaded.Receipt.Slot.Position.Iteration);
            Assert.Equal(1, loaded.Receipt.Slot.Position.Occurrence);
            Assert.Equal(1, loaded.Receipt.Slot.Attempt);
            Assert.Equal("output-task-receipt-valid", loaded.Receipt.Result.Output);

            // THE SAME ROW, not a rewritten one: the notification-time and post-call reads agree on
            // the first-stored instant.
            Assert.Equal(
                observed.ReceiptAtNotification.FirstStoredAtUtc.Ticks, loaded.FirstStoredAtUtc.Ticks);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2c) THE RECORD-BEFORE-RELEASE BOUNDARY — a refusal AFTER the receipt was written
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SHARED UNCHANGED-ROW ASSERTION for a post-record checked-release refusal: the receipt
    /// captured the instant the REAL recorder returned — BEFORE the ownership mutation and before
    /// the checked release — is compared against a read taken through a NEWLY CONSTRUCTED store
    /// AFTER the refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY BOTH HALVES ARE REQUIRED. The payload half proves the evidence was not rebound; the
    /// FIRST-STORED half proves it was not deleted and re-inserted. A compensating delete/reinsert
    /// during the refusal preserves the payload exactly (the codec is deterministic) and changes
    /// ONLY the timestamp — so without the timestamp comparison that mutant passes, and without the
    /// PRE-mutation baseline the comparison is vacuous because both reads would already carry the
    /// refreshed value.
    /// </para>
    /// <para>
    /// IT IS NON-VACUOUS BY CONSTRUCTION: the baseline must be non-null, its capture must not have
    /// thrown, and the post-refusal read must be non-null, before any comparison is made.
    /// </para>
    /// </remarks>
    /// <param name="harness">The harness whose recorder hook holds the baseline.</param>
    /// <param name="taskId">The task id whose receipt must be unchanged.</param>
    /// <param name="expectedGoalId">The goal the recorded assignment named.</param>
    /// <param name="expectedRole">The role the recorded assignment named.</param>
    /// <param name="expectedModel">The model the transport selected for the mapped result.</param>
    private static void AssertReceiptUnchangedAcrossRefusal(
        Harness harness,
        string taskId,
        string expectedGoalId,
        DomainWorkerRole expectedRole,
        string expectedModel)
    {
        // ── (1) THE PRE-MUTATION BASELINE really exists and was really read ──────────────────
        var hook = harness.RecorderHook;
        Assert.NotNull(hook);
        Assert.Null(hook!.CaptureFailure);

        var captured = hook.CapturedReceipt;
        Assert.NotNull(captured);

        // The baseline is the row this delivery wrote, not some earlier one.
        Assert.Equal(taskId, captured!.Receipt.Slot.TaskId);
        Assert.Equal($"output-{taskId}", captured.Receipt.Result.Output);

        // ── (2) THE POST-REFUSAL READ, through a store constructed FRESH for this call ───────
        var afterRefusal = harness.ReadReceipt(taskId);
        Assert.NotNull(afterRefusal);

        // ── (3a) THE PAYLOAD/IDENTITY IS UNCHANGED, member by member ────────────────────────
        var after = afterRefusal!.Receipt;
        Assert.Equal(expectedGoalId, after.GoalId);
        Assert.Equal(WorkerId, after.WorkerId);
        Assert.Equal(expectedRole, after.Role);
        Assert.Equal(taskId, after.Slot.TaskId);
        Assert.Equal(GoalPhase.Coding, after.Slot.Position!.Phase);
        Assert.Equal(1, after.Slot.Position.Iteration);
        Assert.Equal(1, after.Slot.Position.Occurrence);
        Assert.Equal(1, after.Slot.Attempt);
        Assert.Equal($"output-{taskId}", after.Result.Output);
        Assert.Equal(expectedModel, after.Result.Model);
        Assert.Equal(TaskOutcome.Completed, after.Result.Status);

        // …and it agrees with the BASELINE on every one of those members.
        Assert.Equal(captured.Receipt.GoalId, after.GoalId);
        Assert.Equal(captured.Receipt.WorkerId, after.WorkerId);
        Assert.Equal(captured.Receipt.Role, after.Role);
        Assert.Equal(captured.Receipt.Slot.TaskId, after.Slot.TaskId);
        Assert.Equal(captured.Receipt.Slot.Position!.Phase, after.Slot.Position.Phase);
        Assert.Equal(captured.Receipt.Slot.Position.Iteration, after.Slot.Position.Iteration);
        Assert.Equal(captured.Receipt.Slot.Position.Occurrence, after.Slot.Position.Occurrence);
        Assert.Equal(captured.Receipt.Slot.Attempt, after.Slot.Attempt);
        Assert.Equal(captured.Receipt.Result.Output, after.Result.Output);
        Assert.Equal(captured.Receipt.Result.Model, after.Result.Model);
        Assert.Equal(captured.Receipt.Result.Status, after.Result.Status);

        // ── (3b) THE FIRST-STORED INSTANT IS EXACTLY THE PRE-MUTATION ONE ───────────────────
        // THIS is the delete/reinsert detector: a compensation during the refusal would reproduce
        // the payload byte for byte and refresh ONLY this value.
        Assert.Equal(captured.FirstStoredAtUtc, afterRefusal.FirstStoredAtUtc);
        Assert.Equal(captured.FirstStoredAtUtc.Ticks, afterRefusal.FirstStoredAtUtc.Ticks);
        Assert.Equal(captured.FirstStoredAtUtc.Kind, afterRefusal.FirstStoredAtUtc.Kind);
    }

    /// <summary>
    /// THE HEADLINE ORDERING PROOF: the receipt is written BEFORE the checked release runs, so a
    /// release that is refused AFTER the record leaves the durable evidence in place and mutates
    /// NOTHING.
    /// <para>
    /// The ownership is invalidated INSIDE the recording call, by a decorator that performs the
    /// REAL store write through the REAL recorder and only then clears the worker's busy flag and
    /// current task. The handler therefore passes every PRE-record validation, really retains the
    /// receipt, and is then refused by <c>ApplyTaskCompletion</c>'s own re-validation.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS IS THE VECTOR THAT PINS THE ORDER. Every other refusal here stops BEFORE the
    /// recorder runs, so none of them can distinguish "record, then release" from "release, then
    /// record". This one can: the receipt provably exists while the release provably refused.
    /// </para>
    /// <para>
    /// THE REFUSAL IS ATTRIBUTED, NOT ASSUMED. The vector waits for the CHECKED-RELEASE refusal
    /// reason specifically, and separately asserts the acceptance provenance line IS present — so a
    /// pre-record gate silently refusing instead would fail both ways.
    /// </para>
    /// <para>
    /// IT IS FULLY DETERMINISTIC: the mutation happens synchronously inside the handler's own call
    /// stack, on the handler's thread. There is no live race and no timing sleep.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Completion_OwnershipClearedAfterRecording_ReleaseRefusedAndReceiptRemains()
    {
        var h = Harness.CreateWithOwnershipMutationAfterRecord();
        await RunAsync(h, async () =>
        {
            h.Assign("task-post-record-idle", model: "assigned-model");

            // The assignment's own dashboard notification is excluded, so a NON-zero count below
            // can only come from the completion path.
            h.ResetDashboardNotifications();

            // THE MUTATION, ARMED FOR THIS DELIVERY: the REAL write happens first, then the pool's
            // busy flag and current task are cleared — the shape the checked release refuses.
            h.RecorderHook!.AfterRecord = () => h.Pool.MarkIdle(WorkerId);

            await h.CompleteAndAwaitCheckedReleaseRefusedAsync("task-post-record-idle");

            // THE RECORDER REALLY RAN: the refusal is genuinely POST-record.
            Assert.Equal(1, h.RecorderHook.RecordCount);

            // THE DURABLE RECEIPT REMAINS — never deleted, compensated or rebound by the refusal.
            // The baseline was captured INSIDE the recording call, before the mutation and before
            // the checked release; the comparison read comes from a FRESHLY CONSTRUCTED store.
            AssertReceiptUnchangedAcrossRefusal(
                h,
                "task-post-record-idle",
                expectedGoalId: "goal-ownership",
                expectedRole: DomainWorkerRole.Coder,
                expectedModel: "assigned-model");

            // The individually named payload members, kept as they were.
            var afterRefusal = h.ReadReceipt("task-post-record-idle");
            Assert.NotNull(afterRefusal);
            Assert.Equal("goal-ownership", afterRefusal!.Receipt.GoalId);
            Assert.Equal(WorkerId, afterRefusal.Receipt.WorkerId);
            Assert.Equal(DomainWorkerRole.Coder, afterRefusal.Receipt.Role);
            Assert.Equal("task-post-record-idle", afterRefusal.Receipt.Slot.TaskId);
            Assert.Equal("output-task-post-record-idle", afterRefusal.Receipt.Result.Output);

            // THE CHECKED RELEASE MUTATED NOTHING. CurrentModel is the discriminator: the pool
            // clears it INSIDE an accepted release and nowhere else, and the test's own MarkIdle
            // deliberately does not touch it — so its survival proves the release never applied.
            Assert.Equal("assigned-model", h.Worker.CurrentModel);

            // …and the ACTIVE QUEUE ENTRY survives: only an accepted release removes it.
            Assert.NotNull(h.Queue.GetActiveTask("task-post-record-idle"));

            // NOTHING WAS NOTIFIED: no dashboard success notification and no completion
            // notification, so nothing downstream ran either.
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-post-record-idle"));

            // The stream is alive — a refused release must not unwind it.
            Assert.False(h.StreamEnded);

            // THE ROW IS STABLE across the following activity too: same payload, same first-stored
            // instant, so nothing rewrote or refreshed it afterwards.
            var later = h.ReadReceipt("task-post-record-idle");
            Assert.NotNull(later);
            Assert.Equal(afterRefusal.FirstStoredAtUtc.Ticks, later!.FirstStoredAtUtc.Ticks);
            Assert.Equal(afterRefusal.Receipt.Result.Output, later.Receipt.Result.Output);
        });
    }

    /// <summary>
    /// THE SAME BOUNDARY, WITH THE WORKER MOVED ON: the worker takes a DIFFERENT task inside the
    /// recording call, so the checked release is refused and the SUCCESSOR's own ownership and
    /// active queue entry are left completely untouched — while the predecessor's receipt remains.
    /// </summary>
    /// <remarks>
    /// THE SUCCESSOR IS REAL STATE, not a fixture flag: it has its own active queue entry assigned
    /// to this worker and the pool's busy pointer names it. An accepted release would have cleared
    /// that pointer, cleared the model and removed a queue entry; none of that happened.
    /// </remarks>
    [Fact]
    public async Task Completion_WorkerMovedOnAfterRecording_ReleaseRefusedAndSuccessorSurvives()
    {
        var h = Harness.CreateWithOwnershipMutationAfterRecord();
        await RunAsync(h, async () =>
        {
            h.Assign("task-post-record-moved", model: "assigned-model");

            // The successor's own REAL transport state, prepared up front so the mutation below is a
            // single deterministic pointer move rather than a multi-step setup inside the handler.
            var successor = h.BuildTask("task-post-record-successor", "successor-model");
            h.Queue.Activate(successor, WorkerId);

            // Every dashboard notification the setup produced is excluded.
            h.ResetDashboardNotifications();

            h.RecorderHook!.AfterRecord = () =>
            {
                // THE WORKER MOVES ON — same pinned instance (so the stream's own pinned-instance
                // guard is untouched), different current task.
                h.Pool.MarkBusy(WorkerId, "task-post-record-successor");
            };

            await h.CompleteAndAwaitCheckedReleaseRefusedAsync("task-post-record-moved");

            Assert.Equal(1, h.RecorderHook.RecordCount);

            // THE PREDECESSOR'S RECEIPT REMAINS — payload AND first-stored instant both compared
            // against the baseline captured before the ownership moved.
            AssertReceiptUnchangedAcrossRefusal(
                h,
                "task-post-record-moved",
                expectedGoalId: "goal-ownership",
                expectedRole: DomainWorkerRole.Coder,
                expectedModel: "assigned-model");

            var afterRefusal = h.ReadReceipt("task-post-record-moved");
            Assert.NotNull(afterRefusal);
            Assert.Equal("task-post-record-moved", afterRefusal!.Receipt.Slot.TaskId);

            // THE SUCCESSOR IS UNTOUCHED: still busy with its own task, its queue entry intact.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-post-record-successor", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-post-record-successor"));

            // AND THE PREDECESSOR'S OWN ENTRY AND MODEL SURVIVE: the refused release removed
            // nothing and cleared nothing.
            Assert.NotNull(h.Queue.GetActiveTask("task-post-record-moved"));
            Assert.Equal("assigned-model", h.Worker.CurrentModel);

            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-post-record-moved"));
            Assert.False(h.StreamEnded);
        });
    }

    /// <summary>
    /// THE SAME BOUNDARY FOR AN ABA REPLACEMENT: the pinned instance is REPLACED inside the
    /// recording call, so the checked release refuses and the replacement's own assignment survives
    /// — while the predecessor's receipt remains durably stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS ONE IS INVOKED DIRECTLY, stated honestly. Replacing the pinned instance makes the
    /// real read loop's OWN per-message pinned-instance guard end the stream on the next message, so
    /// the post-handler barrier could never run. The direct, SYNCHRONOUS invocation is the same
    /// deterministic technique the existing TOCTOU vector uses: the call RETURNING is itself the
    /// barrier, and any escaping exception surfaces in the vector rather than in a swallowed stream
    /// fault.
    /// </para>
    /// <para>
    /// The two stream-driven vectors above cover the other two refusal shapes of the checked
    /// release, so the ordering claim does not rest on this simulation alone.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Completion_PinnedInstanceReplacedAfterRecording_ReleaseRefusedAndReceiptRemains()
    {
        var h = Harness.CreateWithOwnershipMutationAfterRecord();
        await RunAsync(h, () =>
        {
            h.Assign("task-post-record-aba", model: "assigned-model");
            var stale = h.Worker;

            h.ResetDashboardNotifications();

            ConnectedWorker? replacement = null;
            h.RecorderHook!.AfterRecord = () =>
            {
                // THE REPLACEMENT under the SAME id, taking over the very same task — so ONLY the
                // checked release's instance check can refuse the mutation.
                Assert.True(h.Pool.RemoveWorker(stale));
                replacement = h.Pool.RegisterWorker(WorkerId, []);
                h.Pool.MarkBusy(WorkerId, "task-post-record-aba");
                replacement.CurrentModel = "replacement-model";
            };

            // A DIRECT, SYNCHRONOUS call: returning IS the post-handler barrier here. The
            // eligibility holder is this vector's OWN — a direct handler call is not a live
            // WorkStream invocation, so it cannot borrow one's local.
            h.InvokeHandleTaskCompleteDirectly(
                stale, "task-post-record-aba", new WorkStreamCompletionAckState());

            Assert.Equal(1, h.RecorderHook.RecordCount);

            // THE REFUSAL WAS THE CHECKED RELEASE'S, and it named this task.
            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.CheckedReleaseRefused,
                         StringComparison.Ordinal)
                     && m.Contains("task-post-record-aba", StringComparison.Ordinal));

            // THE PRE-RECORD VALIDATION PASSED: the acceptance provenance line was emitted, so this
            // really is a POST-record refusal rather than an early gate.
            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionAccepted, StringComparison.Ordinal)
                     && m.Contains("task-post-record-aba", StringComparison.Ordinal));

            // THE DURABLE RECEIPT REMAINS — payload AND first-stored instant both compared against
            // the baseline captured before the pinned instance was replaced.
            AssertReceiptUnchangedAcrossRefusal(
                h,
                "task-post-record-aba",
                expectedGoalId: "goal-ownership",
                expectedRole: DomainWorkerRole.Coder,
                expectedModel: "assigned-model");

            var afterRefusal = h.ReadReceipt("task-post-record-aba");
            Assert.NotNull(afterRefusal);
            Assert.Equal("task-post-record-aba", afterRefusal!.Receipt.Slot.TaskId);
            Assert.Equal("output-task-post-record-aba", afterRefusal.Receipt.Result.Output);

            // THE REPLACEMENT'S OWN ASSIGNMENT, and the queue entry, both survive.
            Assert.NotNull(replacement);
            Assert.True(replacement!.IsBusy);
            Assert.Equal("task-post-record-aba", replacement.CurrentTaskId);
            Assert.Equal("replacement-model", replacement.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-post-record-aba"));

            // NOTHING WAS NOTIFIED.
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-post-record-aba"));
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// A MISSING STORED ASSIGNMENT CONTEXT REFUSES THE COMPLETION: the ownership validation passes
    /// (both authorities really agree), but no assignment was ever recorded for the task, so the
    /// receipt cannot be evidenced. The completion is RETAINED — no release, no queue removal, no
    /// dashboard success notification and no completion notification — and the following Ready is
    /// still ignored by the existing active-assignment guard.
    /// </summary>
    /// <remarks>
    /// THE TRANSPORT OWNERSHIP IS ESTABLISHED WITHOUT RECORDING A CONTEXT on purpose: the pool and
    /// the queue are made to agree directly, which is exactly the state an unrecorded delivery
    /// leaves behind.
    /// </remarks>
    [Fact]
    public async Task Completion_WithoutStoredAssignmentContext_IsRetainedAndThenReadyIsIgnored()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            // Pool + queue ownership, with NO recorded assignment context.
            h.Pool.MarkBusy(WorkerId, "task-unrecorded");
            h.Worker.Role = DomainWorkerRole.Coder;
            h.Worker.CurrentModel = "assigned-model";
            h.Queue.Activate(h.BuildTask("task-unrecorded", "assigned-model"), WorkerId);

            // THE DASHBOARD COUNTER IS RESET AFTER THE SETUP, so the zero asserted below can only be
            // broken by a notification the COMPLETION path itself raised.
            h.ResetDashboardNotifications();

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-unrecorded",
                nameof(WorkerCompletionRecordingFailureReason.InvalidContext));

            // NOTHING WAS RELEASED, REMOVED OR NOTIFIED.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-unrecorded", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-unrecorded"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-unrecorded"));
            Assert.Null(h.ReadReceipt("task-unrecorded"));
            Assert.False(h.StreamEnded, "a receipt refusal must not unwind the worker's stream");

            // THE FOLLOWING READY IS STILL IGNORED by the existing active-assignment guard.
            h.Queue.Enqueue(h.BuildTask("task-next", "next-model"));
            await h.ReadyAndAwaitIgnoredAsync();

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-unrecorded", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-unrecorded"));

            // The pending task was never dequeued for this ignored Ready.
            var stillPending = h.Queue.TryDequeueAny();
            Assert.NotNull(stillPending);
            Assert.Equal("task-next", stillPending!.TaskId);
        });
    }

    /// <summary>
    /// A STORED CONTEXT THAT NAMES A DIFFERENT WORKER REFUSES: the pinned worker is genuinely busy
    /// with the completing task, but the RECORDED assignment belongs to somebody else, so the
    /// completion is retained and no receipt is written.
    /// </summary>
    [Fact]
    public async Task Completion_StoredContextNamesAnotherWorker_IsRetained()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Pool.MarkBusy(WorkerId, "task-foreign-context");
            h.Worker.Role = DomainWorkerRole.Coder;
            h.Queue.Activate(h.BuildTask("task-foreign-context", "assigned-model"), WorkerId);

            h.RecordAssignmentContext(
                "task-foreign-context", "goal-ownership", DomainWorkerRole.Coder,
                workerId: "worker-somewhere-else");

            // THE DASHBOARD COUNTER IS RESET AFTER THE SETUP.
            h.ResetDashboardNotifications();

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-foreign-context",
                nameof(WorkerCompletionRecordingFailureReason.InvalidContext));

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-foreign-context", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-foreign-context"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-foreign-context"));
            Assert.Null(h.ReadReceipt("task-foreign-context"));
            Assert.False(h.StreamEnded);
        });
    }

    /// <summary>
    /// THE REMAINING MISMATCHED-CONTEXT CELLS — a stored GOAL that disagrees with the active task,
    /// and a stored ROLE that disagrees with it — are refused exactly like the foreign-worker cell:
    /// the completion is retained, no receipt is written, and NOTHING is notified on any channel.
    /// </summary>
    /// <remarks>
    /// THE ROLE CELL IS A CONSTRUCTIBLE DISAGREEMENT, not a fabricated one: the stored context is
    /// recorded at the TESTING phase (whose existing mapping yields <c>Tester</c>), which the
    /// assignment context's own constructor accepts, while the active task is a <c>Coder</c> task.
    /// </remarks>
    /// <param name="cell">Which stored-context disagreement to seed.</param>
    [Theory]
    [InlineData("goal")]
    [InlineData("role")]
    public async Task Completion_StoredContextDisagreesOnGoalOrRole_IsRetainedAndNothingNotified(string cell)
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            var taskId = $"task-mismatch-{cell}";

            h.Pool.MarkBusy(WorkerId, taskId);
            h.Worker.Role = DomainWorkerRole.Coder;
            h.Worker.CurrentModel = "assigned-model";
            h.Queue.Activate(h.BuildTask(taskId, "assigned-model"), WorkerId);

            switch (cell)
            {
                case "goal":
                    // The SAME worker and role, a DIFFERENT goal than the active task's.
                    h.RecordAssignmentContext(taskId, "goal-somewhere-else", DomainWorkerRole.Coder);
                    break;

                case "role":
                    // The SAME worker and goal, a role the active Coder task does not carry.
                    h.RecordAssignmentContext(taskId, "goal-ownership", DomainWorkerRole.Tester);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown mismatch cell '{cell}'.");
            }

            // THE DASHBOARD COUNTER IS RESET AFTER THE SETUP.
            h.ResetDashboardNotifications();

            await h.CompleteAndAwaitNotRecordedAsync(
                taskId, nameof(WorkerCompletionRecordingFailureReason.InvalidContext));

            // THE COMPLETION IS RETAINED and NOTHING was notified on ANY channel.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal(taskId, h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask(taskId));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.DownstreamHandledCount(taskId));
            Assert.Null(h.ReadReceipt(taskId));
            Assert.False(h.StreamEnded);
        });
    }

    /// <summary>
    /// A GENUINE STORE CONFLICT RETAINS THE HOLD: a DIFFERENT, well-formed receipt is already
    /// retained for the task, so the real recorder reports <c>Conflict</c>, nothing is released and
    /// the existing row is left byte-identical.
    /// </summary>
    [Fact]
    public async Task Completion_ConflictingReceiptAlreadyRetained_IsRetainedAndRowUnchanged()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-conflict", model: "assigned-model");

            // A DIFFERENT receipt for the SAME task id, retained through the REAL store.
            var firstStored = h.RecordForeignReceipt("task-conflict", "an-earlier-output");
            var before = h.ReadReceipt("task-conflict");
            Assert.NotNull(before);

            // THE DASHBOARD COUNTER IS RESET AFTER THE SETUP.
            h.ResetDashboardNotifications();

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-conflict",
                nameof(WorkerCompletionRecordingFailureReason.Conflict));

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-conflict", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-conflict"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-conflict"));
            Assert.False(h.StreamEnded);

            // THE EXISTING ROW IS UNTOUCHED: same payload, same first-stored instant.
            var after = h.ReadReceipt("task-conflict");
            Assert.NotNull(after);
            Assert.Equal("an-earlier-output", after!.Receipt.Result.Output);
            Assert.Equal(firstStored.Ticks, after.FirstStoredAtUtc.Ticks);

            // THE FOLLOWING READY IS STILL IGNORED.
            await h.ReadyAndAwaitIgnoredAsync();
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-conflict", h.Worker.CurrentTaskId);

            // AND IT DID NOT DEQUEUE: a pending task enqueued before the Ready is still in the
            // queue, so the refusal's hold really blocked the dispatch.
            h.Queue.Enqueue(h.BuildTask("task-conflict-pending", "pending-model"));
            await h.ReadyAndAwaitIgnoredAsync();

            var conflictPending = h.Queue.TryDequeueAny();
            Assert.NotNull(conflictPending);
            Assert.Equal("task-conflict-pending", conflictPending!.TaskId);
        });
    }

    /// <summary>
    /// A GENUINE, COMMITTED-BUT-REPORTED-INDETERMINATE WRITE retains the hold and the stream: the
    /// injected post-execution fault makes the real recorder report <c>Indeterminate</c> carrying the
    /// EXACT sentinel, no release happens, and an IDENTICAL LATER COMPLETION then settles
    /// <c>AlreadyStored</c> and proceeds to the ordinary checked release and notification — with the
    /// stored row and its first-stored time unchanged.
    /// </summary>
    /// <remarks>
    /// THE INJECTION IS THE EXISTING EF INTERCEPTOR FACILITY over the file-backed store, so the
    /// uncertainty is genuinely the provider's: the autocommit really landed.
    /// </remarks>
    [Fact]
    public async Task Completion_IndeterminateThenIdenticalCompletion_ThenOrdinaryRelease()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"copilothive-ownership-indeterminate-{Guid.NewGuid():N}.db");

        try
        {
            var interceptor = new ReceiptInsertThrowingInterceptor(ReceiptInsertFault.AfterExecution);
            var h = Harness.CreateWithReceiptInterceptors(dbPath, interceptor);

            await RunAsync(h, async () =>
            {
                h.Assign("task-indeterminate", model: "assigned-model");

                // THE DASHBOARD COUNTER IS RESET AFTER THE ASSIGNMENT, so the zero asserted for the
                // refusal below is the completion path's own.
                h.ResetDashboardNotifications();

                // ── THE UNCONFIRMED WRITE ────────────────────────────────────────────────────
                await h.CompleteAndAwaitNotRecordedAsync(
                    "task-indeterminate",
                    nameof(WorkerCompletionRecordingFailureReason.Indeterminate));

                Assert.Equal(1, interceptor.FireCount);
                Assert.True(h.Worker.IsBusy);
                Assert.Equal("task-indeterminate", h.Worker.CurrentTaskId);
                Assert.Equal("assigned-model", h.Worker.CurrentModel);
                Assert.NotNull(h.Queue.GetActiveTask("task-indeterminate"));
                Assert.Equal(0, h.TransportNotifications);
                Assert.Equal(0, h.DashboardNotifications);
                Assert.Equal(0, h.DownstreamHandledCount("task-indeterminate"));
                Assert.False(h.StreamEnded);

                // The row IS durable (the fault fired after the autocommit) and nothing was inferred.
                var uncertain = h.ReadReceipt("task-indeterminate");
                Assert.NotNull(uncertain);
                var firstStored = uncertain!.FirstStoredAtUtc;

                // ── THE IDENTICAL LATER COMPLETION settles AlreadyStored AND PROCEEDS ────────
                interceptor.Disarm();
                h.ResetDashboardNotifications();
                var result = await h.CompleteAndAwaitDownstreamAsync("task-indeterminate");

                Assert.Equal("task-indeterminate", result.TaskId);
                Assert.False(h.Worker.IsBusy);
                Assert.Null(h.Worker.CurrentTaskId);
                Assert.Null(h.Queue.GetActiveTask("task-indeterminate"));
                Assert.Equal(1, h.DownstreamHandledCount("task-indeterminate"));
                Assert.Equal(1, h.TransportNotifications);

                // THE ACCEPTED SETTLEMENT DID notify the dashboard exactly once — the positive
                // control for the zeros asserted on the refusal above.
                Assert.Equal(1, h.DashboardNotifications);

                // THE STORED ROW AND ITS TIME ARE UNCHANGED by either invocation.
                var settled = h.ReadReceipt("task-indeterminate");
                Assert.NotNull(settled);
                Assert.Equal(firstStored.Ticks, settled!.FirstStoredAtUtc.Ticks);
                Assert.Equal("output-task-indeterminate", settled.Receipt.Result.Output);

                // THE FOLLOWING READY IS NOW ACCEPTED: the settlement released the worker.
                await h.ReadyAndAwaitAcceptedAsync();
                Assert.False(h.Worker.IsBusy);
                Assert.Null(h.Worker.CurrentTaskId);
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                try
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch
                {
                    // Best-effort cleanup — a leftover temp file must never fail a test.
                }
            }
        }
    }

    /// <summary>
    /// A READ/CODEC FAILURE ON THE DUPLICATE PATH RETAINS THE HOLD: a corrupt stored payload makes
    /// the REAL store's readback throw, so the real recorder reports <c>StoreError</c>, the
    /// completion is retained and the unusable row is left exactly as it was.
    /// </summary>
    /// <remarks>
    /// THE CORRUPT ROW IS SEEDED DIRECTLY, through the harness's own factory: a well-formed JSON
    /// envelope missing the required members is exactly what the store's codec refuses, and the
    /// recorder must report that as a read failure rather than as a duplicate or an uncertainty.
    /// </remarks>
    [Fact]
    public async Task Completion_StoredPayloadUnusable_IsRetainedAndRowUnchanged()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-corrupt", model: "assigned-model");

            // Built by CONCATENATION, never interpolation: the corrupt payload deliberately contains
            // JSON braces, which an interpolated raw-SQL string would misparse as a format hole.
            const string corruptPayload = """{"version":1}""";
            var seededFirstStored = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero)
                .UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            h.Stores.ExecuteRaw(
                "INSERT INTO completion_receipts (task_id, goal_id, payload_json, first_stored_at_utc) " +
                "VALUES ('task-corrupt', 'goal-ownership', '" + corruptPayload + "', '" +
                seededFirstStored + "')");

            // THE DASHBOARD COUNTER IS RESET AFTER THE SETUP.
            h.ResetDashboardNotifications();

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-corrupt",
                nameof(WorkerCompletionRecordingFailureReason.StoreError));

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-corrupt", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-corrupt"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-corrupt"));
            Assert.False(h.StreamEnded);

            // THE UNUSABLE ROW IS NEVER REPAIRED OR REPLACED.
            Assert.Equal(
                corruptPayload,
                h.RawScalar($"SELECT payload_json FROM completion_receipts WHERE task_id = 'task-corrupt'"));
            Assert.Equal(
                seededFirstStored,
                h.RawScalar($"SELECT first_stored_at_utc FROM completion_receipts WHERE task_id = 'task-corrupt'"));
        });
    }

    /// <summary>
    /// A MISSING RECORDER FAILS CLOSED: with no recorder configured the incoming completion is
    /// retained — NO release, NO queue removal, NO dashboard success notification and NO completion
    /// notification — and the stream survives. The old unrecorded completion path is never taken.
    /// </summary>
    [Fact]
    public async Task Completion_WithoutCompletionRecorder_FailsClosedAndRetainsOwnership()
    {
        var h = Harness.CreateWithoutCompletionRecorder();
        await RunAsync(h, async () =>
        {
            h.Assign("task-no-recorder", model: "assigned-model");

            // THE DASHBOARD COUNTER IS RESET AFTER THE ASSIGNMENT.
            h.ResetDashboardNotifications();

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-no-recorder",
                nameof(WorkerCompletionRecordingFailureReason.MissingRecorder));

            // NOTHING WAS RELEASED, REMOVED OR NOTIFIED, and NO receipt exists.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-no-recorder", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-no-recorder"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-no-recorder"));
            Assert.Null(h.ReadReceipt("task-no-recorder"));
            Assert.False(h.StreamEnded);

            // THE FOLLOWING READY IS STILL IGNORED.
            await h.ReadyAndAwaitIgnoredAsync();
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-no-recorder", h.Worker.CurrentTaskId);
        });
    }

    /// <summary>
    /// THE DIAGNOSTIC ITSELF CANNOT UNWIND THE STREAM. The service logger's own warning write throws
    /// the pre-created sentinel for the recording-refusal warning, yet the handler still RETURNS: the
    /// guard swallows the logger fault, nothing is released, and the post-handler barrier — carried
    /// by a DIFFERENT message — proves the read loop continued.
    /// </summary>
    /// <remarks>
    /// THE THROW IS ARMED ONLY FOR THE RECORDING-REFUSAL FRAGMENT, so the barrier's own Progress line
    /// is unaffected and a returned barrier really means the loop advanced past the guarded warning.
    /// </remarks>
    [Fact]
    public async Task Completion_LoggerThrowsOnTheRefusalWarning_StreamStillSurvives()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Pool.MarkBusy(WorkerId, "task-logger-fault");
            h.Worker.Role = DomainWorkerRole.Coder;
            h.Queue.Activate(h.BuildTask("task-logger-fault", "assigned-model"), WorkerId);

            h.ServiceLogger.ArmThrowOnFragment(ProductionLogFragments.CompletionNotRecorded);

            // THE DASHBOARD COUNTER IS RESET AFTER THE SETUP.
            h.ResetDashboardNotifications();

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-logger-fault",
                nameof(WorkerCompletionRecordingFailureReason.InvalidContext));

            Assert.True(h.ServiceLogger.ThrowCount > 0, "the armed logger fault never fired");
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-logger-fault", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-logger-fault"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-logger-fault"));
            Assert.Null(h.ReadReceipt("task-logger-fault"));
            Assert.False(h.StreamEnded, "a throwing diagnostic must never unwind the worker's stream");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) THE REFUSALS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A MISSING ACTIVE ENTRY is refused: the worker really owns the task, but the queue does not,
    /// so nothing is released, nothing is notified and the handler RETURNS cleanly.
    /// </summary>
    [Fact]
    public async Task Completion_WithoutActiveQueueEntry_IsIgnored()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            // Pool ownership WITHOUT queue activation.
            h.Pool.MarkBusy(WorkerId, "task-unqueued");
            h.Worker.CurrentModel = "assigned-model";

            await h.CompleteAndAwaitIgnoredAsync(
                "task-unqueued", HiveOrchestratorService.OwnershipRefusalReasons.NoActiveQueueEntry);

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-unqueued", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-unqueued"));
        });
    }

    /// <summary>
    /// A FOREIGN assigned_worker is refused: the active entry exists for this task id, but the
    /// queue says another worker owns it.
    /// </summary>
    [Fact]
    public async Task Completion_WithForeignAssignedWorker_IsIgnored()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-foreign", model: "assigned-model");

            // The queue's ownership moves to somebody else while the pool still names this worker.
            h.Queue.MarkActive("task-foreign", "another-worker");

            await h.CompleteAndAwaitIgnoredAsync(
                "task-foreign", HiveOrchestratorService.OwnershipRefusalReasons.ForeignAssignedWorker);

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-foreign", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-foreign"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-foreign"));
        });
    }

    /// <summary>
    /// A STALE COMPLETION WHILE A SUCCESSOR HOLDS A DISTINCT TASK ID is refused, and the
    /// successor's own active queue entry is NEVER removed.
    /// </summary>
    /// <remarks>
    /// THE QUEUE AGREES WITH THE DELIVERY HERE: the predecessor's own active entry names THIS
    /// worker as its assigned_worker, exactly as a genuinely re-activated predecessor would. So
    /// the only thing that can refuse this delivery is the POOL's busy/current-task check — this
    /// vector is what proves that check, not the queue's assigned_worker one.
    /// </remarks>
    [Fact]
    public async Task Completion_StaleTaskWhileSuccessorHoldsDistinctTask_IsIgnoredAndSuccessorSurvives()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            // The successor the worker is now executing.
            h.Assign("task-successor", model: "successor-model");

            // The predecessor's own active entry — assigned to THIS SAME worker, so the queue-side
            // checks all pass and only the pool's ownership can refuse the delivery.
            var predecessor = h.BuildTask("task-predecessor", "predecessor-model");
            h.Queue.Activate(predecessor, WorkerId);
            Assert.Equal(
                WorkerId, h.Queue.GetActiveTask("task-predecessor")!.Metadata["assigned_worker"]);

            await h.CompleteAndAwaitIgnoredAsync(
                "task-predecessor",
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            // The successor's ownership is untouched, and the predecessor's entry survives too.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-successor", h.Worker.CurrentTaskId);
            Assert.Equal("successor-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-successor"));
            Assert.NotNull(h.Queue.GetActiveTask("task-predecessor"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-predecessor"));
        });
    }

    /// <summary>
    /// A COMPLETION FROM A WORKER THAT IS NO LONGER BUSY is refused: the queue still holds an
    /// active entry naming this worker, but the pool says the assignment was already released, so
    /// nothing is removed and nothing is notified.
    /// </summary>
    [Fact]
    public async Task Completion_FromWorkerThatIsNoLongerBusy_IsIgnored()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-released", model: "assigned-model");

            // The pool-side ownership is released while the queue entry survives.
            h.Pool.MarkIdle(WorkerId);
            Assert.False(h.Worker.IsBusy);
            Assert.NotNull(h.Queue.GetActiveTask("task-released"));

            await h.CompleteAndAwaitIgnoredAsync(
                "task-released", HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            Assert.NotNull(h.Queue.GetActiveTask("task-released"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-released"));
        });
    }

    /// <summary>
    /// A MAPPING FAILURE returns locally with a guarded warning and RETAINS the held task and the
    /// stream: no release, no queue removal, no fabricated failure notification. The worker's
    /// following Ready is then correctly IGNORED, because the task is still queue-active.
    /// </summary>
    [Fact]
    public async Task Completion_MappingFailure_RetainsOwnershipAndThenReadyIsIgnored()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-unmappable", model: "assigned-model");

            // An UNKNOWN wire status: GrpcMapper.ToDomain throws for it. The helper barriers on a
            // following Progress message, so a returned call proves the handler RETURNED rather
            // than unwinding the read loop.
            await h.CompleteAndAwaitMappingFailureAsync(
                "task-unmappable", (CopilotHive.Shared.Grpc.TaskStatus)9999);

            // NOTHING WAS RELEASED, REMOVED OR NOTIFIED.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-unmappable", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-unmappable"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-unmappable"));
            Assert.False(h.StreamEnded, "a mapping failure must not unwind the worker's stream");

            // THE FOLLOWING READY IS IGNORED: the task is still active in the queue.
            h.Queue.Enqueue(h.BuildTask("task-next", "next-model"));
            await h.ReadyAndAwaitIgnoredAsync();

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-unmappable", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-unmappable"));

            // The pending task was never dequeued for this ignored Ready.
            var stillPending = h.Queue.TryDequeueAny();
            Assert.NotNull(stillPending);
            Assert.Equal("task-next", stillPending!.TaskId);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) READY
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE INITIAL READY of a fresh worker still works: no task is held, so the checked idle is
    /// applied and the handler proceeds to its normal dequeue (which finds nothing here).
    /// </summary>
    [Fact]
    public async Task Ready_InitialReadyOnIdleWorker_IsAccepted()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            await h.ReadyAndAwaitAcceptedAsync();

            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Worker.CurrentTaskId);
        });
    }

    /// <summary>
    /// THE READY AFTER AN ACCEPTED COMPLETION still works: the completion released the ownership,
    /// so the following Ready is accepted rather than refused.
    /// </summary>
    [Fact]
    public async Task Ready_AfterAcceptedCompletion_IsAccepted()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-then-ready", model: "assigned-model");

            await h.CompleteAndAwaitDownstreamAsync("task-then-ready");
            Assert.Null(h.Queue.GetActiveTask("task-then-ready"));

            await h.ReadyAndAwaitAcceptedAsync();

            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Worker.CurrentTaskId);
        });
    }

    /// <summary>
    /// A READY WHILE THE TASK IS STILL QUEUE-ACTIVE is IGNORED before any idle or dequeue: the
    /// held ownership survives and no pending task is handed out.
    /// </summary>
    [Fact]
    public async Task Ready_WithStillActiveQueueEntry_IsIgnoredBeforeIdlingOrDequeuing()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-held", model: "assigned-model");
            h.Queue.Enqueue(h.BuildTask("task-pending", "pending-model"));

            await h.ReadyAndAwaitIgnoredAsync();

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-held", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-held"));

            // The pending task was never dequeued.
            var pending = h.Queue.TryDequeueAny();
            Assert.NotNull(pending);
            Assert.Equal("task-pending", pending!.TaskId);
        });
    }

    /// <summary>
    /// AN INCONSISTENT OWNERSHIP SHAPE REFUSES THE READY AT THE CHECKED IDLE. The worker carries
    /// a task id while NOT busy — the shape the pool's checked idle refuses — and the task has no
    /// active queue entry, so the earlier still-active gate does not apply. The handler must
    /// therefore stop at the CHECKED IDLE: nothing is cleared and no pending task is handed out.
    /// </summary>
    [Fact]
    public async Task Ready_WithInconsistentOwnershipShape_IsRefusedByTheCheckedIdle()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            // The inconsistent shape, with NO active queue entry for the task.
            h.Pool.MarkBusy(WorkerId, "task-inconsistent");
            h.Worker.Role = DomainWorkerRole.Coder;
            h.Worker.IsBusy = false;
            Assert.Null(h.Queue.GetActiveTask("task-inconsistent"));

            h.Queue.Enqueue(h.BuildTask("task-pending", "pending-model"));

            await h.ReadyAndAwaitRefusedIdleAsync();

            // NOTHING WAS CLEARED by the refused idle.
            Assert.Equal("task-inconsistent", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Worker.CurrentTaskStartedAt);
            Assert.Equal(DomainWorkerRole.Coder, h.Worker.Role);

            // And no pending task was dequeued or assigned.
            var pending = h.Queue.TryDequeueAny();
            Assert.NotNull(pending);
            Assert.Equal("task-pending", pending!.TaskId);
            Assert.Null(h.Queue.GetActiveTask("task-pending"));
        });
    }

    /// <summary>
    /// THE ABA DEFENCE-IN-DEPTH, exercised directly. A completion delivered on behalf of an
    /// ALREADY-REPLACED worker instance is refused by the handler's OWN pinned-instance check,
    /// naming that guard — not by the later checked release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT THIS VECTOR IS, STATED HONESTLY: it SIMULATES the narrow TIME-OF-CHECK-TO-TIME-OF-USE
    /// window that the real stream leaves open, by invoking the handler directly with the stale
    /// instance. It is NOT a claim that the case is unreachable through a real stream, and it is
    /// NOT a reproduction of the concurrent interleaving either.
    /// </para>
    /// <para>
    /// WHY THE WINDOW IS REAL. <c>WorkStream</c>'s per-message pinned-instance check
    /// (<c>HiveOrchestratorService</c>, the <c>ReferenceEquals(current, pinnedWorker)</c> guard in
    /// the read loop) runs BEFORE the payload switch dispatches to <c>HandleTaskComplete</c>. A
    /// replacement that re-registers under the same ID in between passes that check and still
    /// reaches the handler with a stale pinned instance. The handler's own pinned-instance check is
    /// what closes that window — which is exactly what this vector pins.
    /// </para>
    /// <para>
    /// WHY IT IS NOT DRIVEN CONCURRENTLY HERE: hitting that window through a live stream would
    /// need a production seam between the loop's check and the handler call, and this round adds no
    /// new production seams. Every REACHABLE-without-a-race ownership vector above does run through
    /// the real stream; only this TOCTOU simulation calls the handler directly.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Completion_ForReplacedWorkerInstance_IsRefusedByThePinnedInstanceGuard()
    {
        var h = Harness.Create();
        await RunAsync(h, () =>
        {
            h.Assign("task-aba", model: "assigned-model");
            var stale = h.Worker;

            // The pinned instance is replaced under the SAME id, and the replacement takes over the
            // very same task — so ONLY the pinned-instance check can refuse this delivery. This is
            // the state the TOCTOU window leaves behind: the stream's own check already passed
            // against the pre-replacement instance, and the handler is reached with the stale pin.
            Assert.True(h.Pool.RemoveWorker(stale));
            var replacement = h.Pool.RegisterWorker(WorkerId, []);
            h.Pool.MarkBusy(WorkerId, "task-aba");
            replacement.CurrentModel = "replacement-model";

            // A DIRECT, SYNCHRONOUS call: it RETURNING is itself the post-handler barrier, and any
            // throw from the removed early return would surface here rather than being swallowed.
            // The eligibility holder is this vector's OWN, stated plainly.
            h.InvokeHandleTaskCompleteDirectly(stale, "task-aba", new WorkStreamCompletionAckState());

            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.PinnedInstanceReplaced,
                         StringComparison.Ordinal));

            // THE HANDLER STOPPED AT THE PINNED-INSTANCE GATE: it never reached the acceptance
            // provenance line that follows all four validations.
            h.AssertNeverAccepted("task-aba");

            // The replacement's own assignment, and the queue entry, both survive.
            Assert.True(replacement.IsBusy);
            Assert.Equal("task-aba", replacement.CurrentTaskId);
            Assert.Equal("replacement-model", replacement.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-aba"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-aba"));
            return Task.CompletedTask;
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) THE PUBLISHED → CANCELLED → LATE-COMPLETION → READY SEQUENCE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PUBLISHED/CANCELLED/LATE-COMPLETION SEQUENCE: a GENUINELY PUBLISHED assignment, a
    /// subsequent LOGICAL goal cancellation, then the worker's LATE REAL incoming completion, and
    /// finally a Ready.
    /// <para>
    /// THE SEQUENCE: (1) a REAL Ready is pushed on the real stream; the handler dequeues the
    /// queued task, applies the assignment and publishes it through the REAL
    /// <see cref="WorkerAssignmentPublisher"/> over a REAL SQLite store — and the assignment is
    /// observed WHERE THE REAL PUMP FORWARDS IT, at the gRPC response writer. (2)
    /// <see cref="GoalDispatcher.CancelGoalAsync"/> — logical cancellation — SUCCEEDS: the pipeline
    /// is marked Failed and removed, but the worker keeps the task. (3) The worker's LATE REAL
    /// completion arrives on the real stream: the TRANSPORT guard accepts it (both authorities
    /// still agree), so the worker's OWN transport ownership clears, and the domain result is
    /// handed to the REAL <see cref="GoalDispatcher.HandleTaskCompletionAsync"/> — which drops it
    /// through <c>TaskCompletionService</c>'s missing-pipeline guard. (4) The following Ready is
    /// then ACCEPTED: the released worker is idle with no task.
    /// </para>
    /// <para>
    /// HOW THE "CANCELLED GOAL CANNOT ADVANCE" CLAIM IS EVIDENCED — and it is NOT by re-reading
    /// pre-removed pipeline state. The proof is (a) the REAL downstream guard's own production log
    /// line for THIS task id, emitted by <c>TaskCompletionService</c> after production's
    /// <c>NotifyAsync</c> awaited the dispatcher, and (b) the fact that NO successor task was
    /// enqueued — an advancing pipeline would have dispatched one through the real
    /// <c>PipelineDriver</c>.
    /// </para>
    /// <para>
    /// NOT CLAIMED HERE: no recovery, no fabricated completion for never-delivered work, and no
    /// protection against post-Ready queue insertion or subsequent-assignment races — those are
    /// excluded by the goal's bounded contract.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PublishedAssignment_LogicalCancellation_LateCompletion_ReadiesAgain()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"copilothive-ownership-seq-{Guid.NewGuid():N}.db");

        try
        {
            var h = Harness.CreateWithPublishedAssignmentSupport(dbPath);

            await RunAsync(h, async () =>
            {
                const string goalId = "goal-owned-seq";
                const string taskId = "task-owned-seq";

                // ── THE DISPATCHABLE SETUP: a real pipeline with a Pending slot at the active-task
                //    pointer, the task→goal mapping registered, and the task QUEUED so the real
                //    Ready path can dequeue it. ──
                var goal = new Goal { Id = goalId, Description = "owned transport sequence" };
                h.Manager.CreatePipeline(goal, maxRetries: 3);
                h.GoalSource.Register(goal);
                var pipeline = h.Manager.GetByGoalId(goalId);
                Assert.NotNull(pipeline);

                var position = new WorkSlotPosition(1, GoalPhase.Coding, 1);
                pipeline!.AllocateAttemptAndRegisterSlot(taskId, position);
                pipeline.SetActiveTask(taskId);
                h.Manager.RegisterTask(taskId, goalId);

                h.Queue.Enqueue(h.BuildTask(taskId, "seq-model") with { GoalId = goalId });

                // ── (1) THE GENUINELY PUBLISHED ASSIGNMENT, through the REAL Ready path ──
                await h.ReadyAndAwaitAssignmentPublishedAsync(taskId);

                // The published assignment left the worker genuinely busy, with its model set and
                // the queue entry active — exactly the state the completion guard validates later.
                Assert.True(h.Worker.IsBusy);
                Assert.Equal(taskId, h.Worker.CurrentTaskId);
                Assert.Equal("seq-model", h.Worker.CurrentModel);
                Assert.NotNull(h.Queue.GetActiveTask(taskId));

                // ── (2) THE LOGICAL CANCELLATION — the real GoalDispatcher path ─────────
                var cancelledPipeline = h.Manager.GetByGoalId(goalId);
                Assert.NotNull(cancelledPipeline);
                Assert.True(
                    await h.Dispatcher.CancelGoalAsync(goalId, TestContext.Current.CancellationToken));

                // Cancellation is LOGICAL ONLY: the pipeline is failed, but the worker's transport
                // ownership SURVIVES.
                Assert.Equal(GoalPhase.Failed, cancelledPipeline!.Phase);
                Assert.True(h.Worker.IsBusy, "logical cancellation must not release the worker");
                Assert.Equal(taskId, h.Worker.CurrentTaskId);
                Assert.NotNull(h.Queue.GetActiveTask(taskId));

                // ── (3) THE LATE REAL INCOMING COMPLETION on the real stream ────────────
                // The transport guard accepts it and clears THIS task's transport ownership; the
                // domain result is handed to the REAL dispatcher, which production's own
                // NotifyAsync awaits.
                var tasksEnqueuedBeforeLateCompletion = h.TasksEnqueued;
                var result = await h.CompleteAndAwaitDownstreamAsync(taskId);

                Assert.Equal(taskId, result.TaskId);
                Assert.False(h.Worker.IsBusy, "the late completion clears its own transport ownership");
                Assert.Null(h.Worker.CurrentTaskId);
                Assert.Null(h.Worker.CurrentModel);
                Assert.Null(h.Queue.GetActiveTask(taskId));

                // ── THE GOAL CANNOT ADVANCE, evidenced by the REAL DOWNSTREAM GUARD ─────
                // (a) The production TaskCompletionService missing-pipeline guard fired for THIS
                //     task id. This is the real guard's own log line, not test bookkeeping.
                Assert.Contains(
                    h.DispatcherLogger.Messages,
                    m => m.Contains(ProductionLogFragments.NoPipelineForTask, StringComparison.Ordinal)
                         && m.Contains(taskId, StringComparison.Ordinal));

                // (b) …and NOTHING was dispatched: an advancing pipeline drives the real
                //     PipelineDriver, which enqueues the successor task. No enqueue happened.
                Assert.Equal(tasksEnqueuedBeforeLateCompletion, h.TasksEnqueued);

                // (c) THE RECEIPT PERSISTED through the late completion — even though the
                //     downstream missing-pipeline guard dropped it. The recorder holds no
                //     pipeline precondition, so the evidence survives the cancelled goal.
                var lateReceipt = h.ReadReceipt(taskId);
                Assert.NotNull(lateReceipt);
                Assert.Equal(goalId, lateReceipt!.Receipt.GoalId);
                Assert.Equal(taskId, lateReceipt.Receipt.Slot.TaskId);

                // The stream is alive — the late completion did not unwind it.
                Assert.False(h.StreamEnded);

                // ── (4) THE FOLLOWING READY IS ACCEPTED ────────────────────────────────
                await h.ReadyAndAwaitAcceptedAsync();

                Assert.False(h.Worker.IsBusy);
                Assert.Null(h.Worker.CurrentTaskId);
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                try
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch
                {
                    // Best-effort cleanup — a leftover temp file must never fail a test.
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (N) THE NEGOTIATED ACKNOWLEDGEMENT — EMISSION, IDENTITY AND REFUSALS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ORDINARY ENABLED COMPLETION, END TO END: a worker that NEGOTIATED the acknowledgement
    /// through the REAL registration RPC completes a task with a PRESENT model, and the
    /// acknowledgement is observed AT THE WRITER — forwarded by the production pump — carrying the
    /// exact opaque task and pinned worker identities, while the durable receipt for that same task
    /// is already readable and the ordinary release/notification happened exactly once.
    /// </summary>
    /// <remarks>
    /// THE IDENTITY IS OPAQUE ON PURPOSE: the task id carries spaces, a slash and a non-breaking
    /// space. Nothing between production and the writer may parse, split, trim or normalize it, so an
    /// echo that \"looks right\" after normalization fails here.
    /// </remarks>
    [Fact]
    public async Task EnabledCompletion_Accepted_PublishesAcknowledgementWithExactIdentityAndDurableReceipt()
    {
        const string opaqueTaskId = "  ord/ack-1 \u00a0";

        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            // THE NEGOTIATION REALLY HAPPENED IN PRODUCTION: the request was recorded and the
            // registration REPLY enabled the acknowledgement, and the published instance agrees.
            Assert.True(h.Worker.RequestCompletionReceiptAck);
            Assert.True(h.Worker.CompletionReceiptAckEnabled);

            h.Assign(opaqueTaskId, model: "assigned-model");
            h.ResetDashboardNotifications();

            // The acknowledgement is awaited at the writer BEFORE the completion is pushed, so the
            // observation can never miss the publication.
            var acknowledgement = h.AwaitAcknowledgementAsync();

            var result = await h.CompleteWithPresentModelAndAwaitDownstreamAsync(
                opaqueTaskId, "assigned-model");

            var ack = await acknowledgement;
            Assert.Equal(opaqueTaskId, ack.TaskId);
            Assert.Equal(WorkerId, ack.WorkerId);

            // …and the identity is not merely equal by accident: the lengths match too, so no
            // trimming happened on either leg.
            Assert.Equal(opaqueTaskId.Length, ack.TaskId.Length);

            // THE ORDINARY COMPLETION HAPPENED EXACTLY ONCE.
            Assert.Equal(opaqueTaskId, result.TaskId);
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Worker.CurrentTaskId);
            Assert.Null(h.Queue.GetActiveTask(opaqueTaskId));
            Assert.Equal(1, h.DownstreamHandledCount(opaqueTaskId));
            Assert.Equal(1, h.TransportNotifications);
            Assert.Equal(1, h.DashboardNotifications);

            // EXACTLY ONE acknowledgement was forwarded for this completion.
            Assert.Single(
                h.Writer.Messages,
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck);

            // THE EVIDENCE THE ACKNOWLEDGEMENT IS ABOUT IS ALREADY DURABLE — read through a freshly
            // constructed store, after the whole handler returned.
            var receipt = h.ReadReceipt(opaqueTaskId);
            Assert.NotNull(receipt);
            Assert.Equal(opaqueTaskId, receipt!.Receipt.Slot.TaskId);
            Assert.Equal(WorkerId, receipt.Receipt.WorkerId);
            Assert.Equal("assigned-model", receipt.Receipt.Result.Model);

            // THIS STREAM'S ONE LATEST ELIGIBILITY now names this task, proven LIVE: an identical
            // duplicate on the SAME stream is RE-ACKNOWLEDGED, which only the retained latest id can
            // authorize. The opaque identity survives that leg too.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(opaqueTaskId);
            Assert.Equal(
                2,
                h.Writer.Messages.Count(
                    m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck
                         && string.Equals(
                             m.CompletionReceiptAck.TaskId, opaqueTaskId, StringComparison.Ordinal)));
        });
    }

    /// <summary>
    /// A LEGACY (DISABLED) REGISTRATION'S RUNTIME IS UNCHANGED: it publishes NO acknowledgement even
    /// though its completion is fully accepted, and its ABSENT model still falls back to the
    /// validated active task — the pre-negotiation behaviour, preserved exactly.
    /// </summary>
    [Fact]
    public async Task DisabledCompletion_Accepted_PublishesNoAcknowledgementAndKeepsAbsentModelFallback()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            Assert.False(h.Worker.RequestCompletionReceiptAck);
            Assert.False(h.Worker.CompletionReceiptAckEnabled);

            h.Assign("task-ack-legacy", model: "queue-model");
            h.ResetDashboardNotifications();

            // NO model is sent: the legacy sender shape.
            var result = await h.CompleteAndAwaitDownstreamAsync("task-ack-legacy");
            Assert.Equal("queue-model", result.Model);

            // THE COMPLETION IS ACCEPTED IN FULL, with nothing acknowledged.
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Queue.GetActiveTask("task-ack-legacy"));
            Assert.Equal(1, h.DownstreamHandledCount("task-ack-legacy"));
            await h.AssertNoAcknowledgementAsync();

            // THE LEGACY PATH CREATED NO ELIGIBILITY EITHER, proven LIVE: a re-sent completion for
            // the same task falls to the ORDINARY validation and is refused there, with no
            // confirmation and no acknowledgement anywhere.
            await h.AssertNoLatestEligibilityForAsync(
                "task-ack-legacy",
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);
        });
    }

    /// <summary>
    /// AN ENABLED REGISTRATION'S ABSENT MODEL IS A GUARDED LOCAL REFUSAL: the completion is retained
    /// — no release, no queue removal, no dashboard success notification, no completion notification,
    /// no receipt and no acknowledgement — and the handler RETURNS normally, so the stream survives
    /// and the following Ready is still refused by the existing active-assignment guard.
    /// </summary>
    [Fact]
    public async Task EnabledCompletion_AbsentModel_IsRefusedLocallyWithNothingReleasedOrNotified()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            Assert.True(h.Worker.CompletionReceiptAckEnabled);

            h.Assign("task-ack-no-model", model: "assigned-model");
            h.ResetDashboardNotifications();

            await h.CompleteAndAwaitIgnoredAsync(
                "task-ack-no-model", HiveOrchestratorService.OwnershipRefusalReasons.ModelPresenceRequired);

            // NOTHING WAS RELEASED, REMOVED, RECORDED OR NOTIFIED.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-ack-no-model", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-ack-no-model"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-ack-no-model"));
            Assert.Null(h.ReadReceipt("task-ack-no-model"));

            // NO ELIGIBILITY was created, and nothing was acknowledged — observed through a LIVE
            // pump, so the absence cannot be a stalled pump hiding a published acknowledgement. The
            // worker still HOLDS this task, so no duplicate probe is possible here (a held task can
            // never be classified as one) — the absence is asserted at the pump itself.
            await h.AssertNoAcknowledgementAsync();
            Assert.False(h.StreamEnded, "the model-presence refusal must not unwind the worker's stream");

            // THE FOLLOWING READY IS STILL IGNORED: the task is still active in the queue.
            await h.ReadyAndAwaitIgnoredAsync();
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-ack-no-model", h.Worker.CurrentTaskId);
        });
    }

    /// <summary>
    /// PRESENCE, NOT CONTENT: an EXPLICIT empty and an EXPLICIT whitespace model are both perfectly
    /// valid for an enabled registration. Each is used VERBATIM — never replaced by the queue's model
    /// — the completion is accepted, and the acknowledgement is published.
    /// </summary>
    /// <remarks>
    /// THIS IS THE CELL THAT DISTINGUISHES \"presence\" FROM \"non-empty\": a gate that tested the
    /// value instead of <c>HasModel</c> would refuse both of these.
    /// </remarks>
    /// <param name="wireModel">The explicitly present model value, preserved verbatim.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnabledCompletion_PresentEmptyOrWhitespaceModel_IsAcceptedVerbatim(string wireModel)
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            h.Assign("task-ack-empty-model", model: "queue-model");
            h.ResetDashboardNotifications();

            var acknowledgement = h.AwaitAcknowledgementAsync();

            var result = await h.CompleteWithPresentModelAndAwaitDownstreamAsync(
                "task-ack-empty-model", wireModel);

            // THE EXPLICIT VALUE WINS OVER THE QUEUE, verbatim.
            Assert.Equal(wireModel, result.Model);

            var ack = await acknowledgement;
            Assert.Equal("task-ack-empty-model", ack.TaskId);
            Assert.Equal(WorkerId, ack.WorkerId);

            Assert.False(h.Worker.IsBusy);
            Assert.Equal(1, h.DownstreamHandledCount("task-ack-empty-model"));
            Assert.Equal(1, h.DashboardNotifications);

            var receipt = h.ReadReceipt("task-ack-empty-model");
            Assert.NotNull(receipt);
            Assert.Equal(wireModel, receipt!.Receipt.Result.Model);
        });
    }

    /// <summary>
    /// A MAPPING FAILURE CREATES NO ELIGIBILITY AND LEAVES THE PREVIOUS ONE INTACT: the unmappable
    /// status is refused locally, the receipt is never written, the worker keeps its task — and the
    /// PRIOR successful completion's eligibility still re-acknowledges once the failed hold is gone,
    /// while the failed id itself never becomes eligible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE HOLD IS REMOVED BEFORE THE ELIGIBILITY PROBES, STATED PLAINLY. While the failed task
    /// is still held, <c>IsLatestEligibleDuplicate</c> rejects it on the held-task gate ALONE,
    /// independently of any eligibility — so "nothing was acknowledged" would be guaranteed by that
    /// gate and would prove nothing about whether eligibility was wrongly created or displaced.
    /// Clearing the hold (test-owned pool/queue state, never a completion, so it creates no
    /// eligibility of its own) is what makes both probes below discriminating.
    /// </para>
    /// <para>
    /// IT IS REMOVAL-PROOF IN BOTH DIRECTIONS: a mutant that advanced the holder BEFORE the mapping
    /// failure would displace the prior id — failing the survival probe — and would make the failed
    /// id eligible, failing the zero-eligibility probe.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EnabledCompletion_MappingFailure_CreatesNoEligibilityAndKeepsThePreviousOne()
    {
        // THE TWO IDS DELIBERATELY SHARE NO SUBSTRING. Several observation helpers attribute a log
        // line to a task by CONTAINS, so a prior id like "task-ack-unmappable-prior" would make the
        // prior completion's own downstream/diagnostic lines count as the FAILED id's — quietly
        // corrupting the very assertions this vector rests on.
        const string priorTaskId = "prior-eligible-unmappable";
        const string failedTaskId = "task-ack-unmappable";

        var h = Harness.CreateWithOwnershipMutationAfterRecordAndRequestedAck();
        await RunAsync(h, async () =>
        {
            // ── A REAL PRIOR ELIGIBILITY ON THIS SAME LIVE STREAM ────────────────────────────
            // The seed is an ACCEPTED completion, so it legitimately records once; the failure's own
            // "recorded nothing" claim is therefore made against that baseline, not against zero.
            await h.SeedPriorEligibleCompletionAsync(priorTaskId);
            var recordsAfterSeed = h.RecorderHook!.RecordCount;

            h.Assign(failedTaskId, model: "assigned-model");
            h.ResetDashboardNotifications();

            // A PRESENT model, so the refusal really is the mapping and not the presence gate.
            await h.CompleteAndAwaitMappingFailureWithPresentModelAsync(
                failedTaskId, (CopilotHive.Shared.Grpc.TaskStatus)9999);

            Assert.True(h.Worker.IsBusy);
            Assert.NotNull(h.Queue.GetActiveTask(failedTaskId));
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.DownstreamHandledCount(failedTaskId));
            Assert.Null(h.ReadReceipt(failedTaskId));

            // THE MAPPING REFUSAL CAME BEFORE THE RECORD: no additional record since the seed.
            Assert.Equal(recordsAfterSeed, h.RecorderHook.RecordCount);
            Assert.False(h.StreamEnded);

            // ── THE FAILED HOLD IS REMOVED, so the held-task gate can no longer mask anything ──
            h.ReleaseFailedHold(failedTaskId);

            // THE FAILED ID NEVER BECAME ELIGIBLE: zero confirmation reads, zero acknowledgements.
            await h.AssertFailedCompletionCreatedNoEligibilityAsync(failedTaskId);

            // …AND THE PREVIOUS ELIGIBILITY IS INTACT: the prior id still re-acknowledges through
            // the REAL confirmation path, so the failure displaced nothing.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(priorTaskId);
            Assert.Equal(3, h.AcknowledgedCountFor(priorTaskId));
            Assert.Equal(0, h.AcknowledgedCountFor(failedTaskId));
            Assert.False(h.StreamEnded);
        });
    }

    /// <summary>
    /// A RECORDING REFUSAL CREATES NO ELIGIBILITY AND LEAVES THE PREVIOUS ONE INTACT: with no stored
    /// assignment context the real recorder refuses, so nothing is released or notified — and once
    /// the failed hold is gone, the failed id is still not eligible while the PRIOR completion's
    /// eligibility still re-acknowledges. The emission boundary really is AFTER the confirmed record.
    /// </summary>
    /// <remarks>
    /// THE HOLD IS REMOVED BEFORE THE PROBES for the reason the mapping vector states: the
    /// held-task gate would otherwise refuse the delivery on its own and hide a wrongly created or
    /// displaced eligibility behind a guaranteed absence.
    /// </remarks>
    [Fact]
    public async Task EnabledCompletion_RecordingRefusal_CreatesNoEligibilityAndKeepsThePreviousOne()
    {
        const string priorTaskId = "prior-eligible-unrecorded";
        const string failedTaskId = "task-ack-unrecorded";

        var h = Harness.CreateWithOwnershipMutationAfterRecordAndRequestedAck();
        await RunAsync(h, async () =>
        {
            // ── A REAL PRIOR ELIGIBILITY ON THIS SAME LIVE STREAM ────────────────────────────
            await h.SeedPriorEligibleCompletionAsync(priorTaskId);
            var recordsAfterSeed = h.RecorderHook!.RecordCount;

            // OWNERSHIP WITHOUT A RECORDED ASSIGNMENT CONTEXT, established directly.
            h.Pool.MarkBusy(WorkerId, failedTaskId);
            h.Worker.Role = DomainWorkerRole.Coder;
            h.Worker.CurrentModel = "assigned-model";
            h.Queue.Activate(h.BuildTask(failedTaskId, "assigned-model"), WorkerId);
            h.ResetDashboardNotifications();

            await h.CompleteAndAwaitNotRecordedWithPresentModelAsync(
                failedTaskId,
                nameof(WorkerCompletionRecordingFailureReason.InvalidContext));

            Assert.True(h.Worker.IsBusy);
            Assert.NotNull(h.Queue.GetActiveTask(failedTaskId));
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Null(h.ReadReceipt(failedTaskId));

            // THE RECORD REALLY REFUSED: no additional successful record since the seed.
            Assert.Equal(recordsAfterSeed, h.RecorderHook.RecordCount);
            Assert.False(h.StreamEnded);

            // ── THE FAILED HOLD IS REMOVED, so the held-task gate can no longer mask anything ──
            h.ReleaseFailedHold(failedTaskId);

            // THE FAILED ID NEVER BECAME ELIGIBLE: zero confirmation reads, zero acknowledgements.
            await h.AssertFailedCompletionCreatedNoEligibilityAsync(failedTaskId);

            // …AND THE PREVIOUS ELIGIBILITY IS INTACT.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(priorTaskId);
            Assert.Equal(3, h.AcknowledgedCountFor(priorTaskId));
            Assert.Equal(0, h.AcknowledgedCountFor(failedTaskId));
            Assert.False(h.StreamEnded);
        });
    }

    /// <summary>
    /// A REFUSED CHECKED RELEASE CREATES NO ELIGIBILITY AND LEAVES THE PREVIOUS ONE INTACT, even
    /// though the receipt WAS recorded: the ownership is invalidated inside the recording call, so
    /// the release is refused afterwards, the failed id never becomes acknowledgement-eligible, and
    /// the PRIOR completion's eligibility still re-acknowledges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE VECTOR THAT PINS THE CONSERVATIVE EMISSION BOUNDARY, and it is the sharpest of the
    /// three because the failed id has a GENUINE DURABLE RECEIPT. That receipt would CONFIRM if the
    /// delivery ever reached the read-only branch — so if a mutant advanced the holder as soon as the
    /// record succeeded, ignoring the release outcome, the failed id would become eligible and a REAL
    /// acknowledgement would be published for it. Both the zero-confirmation and zero-acknowledgement
    /// assertions below would then fail, as would the prior id's survival probe.
    /// </para>
    /// <para>
    /// THE HOLD IS REMOVED FIRST, DELIBERATELY. The refused release leaves the active queue entry in
    /// place, and the duplicate classifier rejects any task with an active entry on the held gate
    /// ALONE — so asserting the absence while it is held would be satisfied by that gate no matter
    /// what the eligibility holder contained. The clearing goes straight to the pool and the queue,
    /// never through a completion, so it creates no eligibility of its own.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EnabledCompletion_RefusedCheckedRelease_CreatesNoEligibilityAndKeepsThePreviousOne()
    {
        const string priorTaskId = "prior-eligible-refused-release";
        const string failedTaskId = "task-ack-refused-release";

        var h = Harness.CreateWithOwnershipMutationAfterRecordAndRequestedAck();
        await RunAsync(h, async () =>
        {
            // ── A REAL PRIOR ELIGIBILITY ON THIS SAME LIVE STREAM ────────────────────────────
            await h.SeedPriorEligibleCompletionAsync(priorTaskId);
            var recordsAfterSeed = h.RecorderHook!.RecordCount;

            h.Assign(failedTaskId, model: "assigned-model");
            h.ResetDashboardNotifications();

            h.RecorderHook.AfterRecord = () => h.Pool.MarkIdle(WorkerId);

            await h.CompleteAndAwaitCheckedReleaseRefusedWithPresentModelAsync(failedTaskId);

            // THE RECORDER REALLY RAN — the refusal is genuinely POST-record — and the failed id's
            // evidence really is DURABLE, so a wrongly created eligibility WOULD confirm.
            Assert.Equal(recordsAfterSeed + 1, h.RecorderHook.RecordCount);
            Assert.NotNull(h.ReadReceipt(failedTaskId));

            // …AND THE REFUSED RELEASE STILL PROCESSED NOTHING.
            Assert.NotNull(h.Queue.GetActiveTask(failedTaskId));
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Equal(0, h.DownstreamHandledCount(failedTaskId));
            Assert.False(h.StreamEnded);

            // ── THE FAILED HOLD IS REMOVED, so the held-task gate can no longer mask anything ──
            h.ReleaseFailedHold(failedTaskId);

            // THE FAILED ID NEVER BECAME ELIGIBLE, despite its durable, matching receipt: ZERO
            // confirmation reads and ZERO acknowledgements for it.
            await h.AssertFailedCompletionCreatedNoEligibilityAsync(failedTaskId);
            Assert.Equal(0, h.AcknowledgedCountFor(failedTaskId));

            // …AND THE PREVIOUS ELIGIBILITY IS INTACT: the prior id still re-acknowledges through
            // the REAL confirmation path, so the post-Record failure displaced nothing.
            await h.AssertLatestEligibleStillReAcknowledgedAsync(priorTaskId);
            Assert.Equal(3, h.AcknowledgedCountFor(priorTaskId));

            // Nothing about the refused delivery was processed by the probes either.
            Assert.Equal(recordsAfterSeed + 1, h.RecorderHook.RecordCount);
            Assert.Equal(0, h.DownstreamHandledCount(failedTaskId));
            Assert.False(h.StreamEnded);
        });
    }

    /// <summary>
    /// A FAILED ACKNOWLEDGEMENT ENQUEUE LOSES NOTHING: with the pinned instance's channel already
    /// completed, the acknowledgement cannot be queued — yet the completion is still released, the
    /// durable receipt still exists, the dashboard and the REAL downstream chain are still notified
    /// exactly once, and the guarded diagnostic that reports the lost acknowledgement cannot unwind
    /// the completion handler even when the logger itself throws on it.
    /// </summary>
    /// <remarks>
    /// THE CHANNEL IS CLOSED BEFORE THE COMPLETION ON PURPOSE: that is the one deterministic way to
    /// make <c>TryWrite</c> refuse without racing the pump. The pump's own exit is a pre-existing
    /// consequence of a closed channel and is not what is asserted here — the assertion is that the
    /// COMPLETION is unaffected.
    /// </remarks>
    [Fact]
    public async Task EnabledCompletion_FailedAcknowledgementEnqueue_LosesNoAcceptedCompletion()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            h.Assign("task-ack-enqueue-fail", model: "assigned-model");
            h.ResetDashboardNotifications();

            // THE ENQUEUE CANNOT SUCCEED, and the diagnostic that reports it THROWS when written.
            Assert.True(h.Worker.MessageChannel.Writer.TryComplete());
            h.ServiceLogger.ArmThrowOnFragment(ProductionLogFragments.ReceiptAckNotQueued);

            var result = await h.CompleteWithPresentModelAndAwaitDownstreamAsync(
                "task-ack-enqueue-fail", "assigned-model");

            // THE DIAGNOSTIC REALLY RAN AND REALLY THREW — yet it escaped nowhere.
            Assert.True(h.ServiceLogger.ThrowCount > 0, "the armed diagnostic fault never fired");
            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.ReceiptAckNotQueued, StringComparison.Ordinal)
                     && m.Contains("task-ack-enqueue-fail", StringComparison.Ordinal)
                     && m.Contains(WorkerId, StringComparison.Ordinal));

            // THE ACCEPTED COMPLETION SURVIVED COMPLETELY.
            Assert.Equal("task-ack-enqueue-fail", result.TaskId);
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Worker.CurrentTaskId);
            Assert.Null(h.Queue.GetActiveTask("task-ack-enqueue-fail"));
            Assert.Equal(1, h.DownstreamHandledCount("task-ack-enqueue-fail"));
            Assert.Equal(1, h.TransportNotifications);
            Assert.Equal(1, h.DashboardNotifications);

            // THE EVIDENCE IS STILL DURABLE, and the eligibility was still advanced BEFORE the
            // failed enqueue — the fact is about what happened, not about what could be delivered.
            Assert.NotNull(h.ReadReceipt("task-ack-enqueue-fail"));

            // THE ELIGIBILITY REALLY EXISTS, proven LIVE despite the lost enqueue: the next
            // identical delivery still ENTERS the read-only duplicate branch and is refused only at
            // the (still closed) channel. A delivery with NO eligibility would instead have been
            // refused by the ordinary busy/current-task gate, which is asserted absent here.
            await h.CompleteAndAwaitAcknowledgementNotQueuedAsync("task-ack-enqueue-fail");
            Assert.DoesNotContain(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask,
                         StringComparison.Ordinal)
                     && m.Contains("task-ack-enqueue-fail", StringComparison.Ordinal));

            // NOTHING WAS CONSUMED: the pump never forwarded an acknowledgement (the channel was
            // closed), and no second record, release or notification happened.
            AssertNoAcknowledgementPublished(h);
            Assert.Equal(1, h.DownstreamHandledCount("task-ack-enqueue-fail"));
            Assert.Equal(1, h.TransportNotifications);
            Assert.False(h.StreamEnded, "a lost acknowledgement must not end the worker's stream");
        });
    }

    /// <summary>
    /// A ONE-SHOT ACTUAL ACK WRITER FAULT IS ISOLATED IN THE PUMP: the real response writer throws for
    /// the acknowledgement exactly once, the failure is reported in a guarded diagnostic that cannot
    /// escape even when the logger throws on it, and the pump CARRIES ON — proven by observing a later
    /// message forwarded to the same writer.
    /// </summary>
    /// <remarks>
    /// WHY ONE SHOT. A permanently throwing writer would make the pump unusable for everything, so the
    /// vector could not tell \"the acknowledgement write failed\" from \"the stream is dead\" — and the
    /// contract here is precisely that only the acknowledgement is isolated.
    /// </remarks>
    [Fact]
    public async Task EnabledCompletion_OneShotAcknowledgementWriterFault_IsIsolatedAndLosesNothing()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            h.Assign("task-ack-write-fault", model: "assigned-model");
            h.ResetDashboardNotifications();

            var writerFault = new InvalidOperationException("the response writer threw SENTINEL");
            h.ArmOneShotAcknowledgementWriteFault(writerFault);

            // THE DIAGNOSTIC THAT REPORTS IT ALSO THROWS, so the isolation is proven on both legs.
            h.ServiceLogger.ArmThrowOnFragment(ProductionLogFragments.ReceiptAckWriteFailed);

            // A FRESH WAITER FOR THE PUMP'S OWN DIAGNOSTIC, allocated BEFORE the completion is
            // pushed: the acknowledgement is forwarded asynchronously by the pump, so the failure is
            // observed where production reports it rather than inferred from a later state read.
            var writeFailed = h.ServiceLogger.WaitFor(ProductionLogFragments.ReceiptAckWriteFailed);

            var result = await h.CompleteWithPresentModelAndAwaitDownstreamAsync(
                "task-ack-write-fault", "assigned-model");

            await writeFailed.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // THE WRITE WAS REALLY ATTEMPTED and really faulted.
            Assert.Equal(1, h.AcknowledgementWriteAttempts);
            Assert.True(h.ServiceLogger.ThrowCount > 0, "the armed diagnostic fault never fired");
            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.ReceiptAckWriteFailed, StringComparison.Ordinal)
                     && m.Contains("task-ack-write-fault", StringComparison.Ordinal));

            // THE ACCEPTED COMPLETION IS INTACT.
            Assert.Equal("task-ack-write-fault", result.TaskId);
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Queue.GetActiveTask("task-ack-write-fault"));
            Assert.Equal(1, h.DownstreamHandledCount("task-ack-write-fault"));
            Assert.Equal(1, h.TransportNotifications);
            Assert.Equal(1, h.DashboardNotifications);
            Assert.NotNull(h.ReadReceipt("task-ack-write-fault"));

            // THE FAULTED ACKNOWLEDGEMENT was never recorded at the writer…
            AssertNoAcknowledgementPublished(h);

            // ── THE PUMP SURVIVED THE FAULT ──────────────────────────────────────────────────
            // A later message is really forwarded to the SAME writer, which is only possible if the
            // pump carried on rather than treating the acknowledgement fault as terminal.
            await h.AssertPumpObservationIsLiveAsync();
            Assert.False(h.StreamEnded);
        });
    }

    /// <summary>
    /// THE ACKNOWLEDGEMENT IS ADVISORY, NOT A DELIVERY CLAIM: its presence or absence never changes
    /// the transport ownership, and a refused completion still leaves the stream acknowledgement-free
    /// even on an ENABLED registration.
    /// </summary>
    [Fact]
    public async Task EnabledCompletion_RefusedCompletion_PublishesNoAcknowledgement()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            // The worker is genuinely busy — with a DIFFERENT task than the one it completes — so
            // the ownership guard refuses the completion and the assignment stays held.
            h.Assign("task-ack-own", model: "assigned-model");

            // The observation point is proven live on THIS harness too, so the absence asserted
            // below cannot be an artifact of a pump that forwards nothing.
            await h.AssertPumpObservationIsLiveAsync();

            await h.CompleteAndAwaitIgnoredAsync(
                "task-ack-refused",
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            // The assignment and the refusal both survive with no acknowledgement anywhere —
            // observed through a LIVE pump, so a stalled pump cannot hide a published one.
            await h.AssertNoAcknowledgementAsync();

            // NO ELIGIBILITY EXISTS FOR THE REFUSED NAME EITHER, proven LIVE: re-delivering it takes
            // the ORDINARY path and is refused there, with nothing acknowledged.
            await h.AssertNoLatestEligibilityForAsync(
                "task-ack-refused",
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-ack-own", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-ack-own"));
        });
    }

    /// <summary>
    /// THE LATEST-ELIGIBLE SLOT ADVANCES, ONE ID AT A TIME, ACROSS TWO ORDINARY COMPLETIONS, and each
    /// completion carries its OWN acknowledgement — so the slot is a single latest id rather than an
    /// accumulating history, and a disabled registration beside it stays silent.
    /// </summary>
    [Fact]
    public async Task EnabledCompletion_TwoOrdinaryCompletions_PublishEachAckAndAdvanceTheSingleSlot()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            h.Assign("task-ack-first", model: "assigned-model");
            h.ResetDashboardNotifications();

            var firstAck = h.AwaitAcknowledgementAsync();
            await h.CompleteWithPresentModelAndAwaitDownstreamAsync("task-ack-first", "assigned-model");

            var first = await firstAck;
            Assert.Equal("task-ack-first", first.TaskId);
            Assert.Equal(WorkerId, first.WorkerId);

            // THE FIRST TASK REALLY IS THIS STREAM'S LATEST ELIGIBLE ONE, proven LIVE.
            await h.AssertLatestEligibleStillReAcknowledgedAsync("task-ack-first");

            // ── THE SECOND ORDINARY COMPLETION REPLACES THE SINGLE HOLDER ───────────────────
            h.Assign("task-ack-second", model: "assigned-model");
            h.ResetDashboardNotifications();

            var secondAck = h.AwaitAcknowledgementAsync();
            await h.CompleteWithPresentModelAndAwaitDownstreamAsync("task-ack-second", "assigned-model");

            var second = await secondAck;
            Assert.Equal("task-ack-second", second.TaskId);
            Assert.Equal(WorkerId, second.WorkerId);

            // EXACTLY ONE ID IS RETAINED, AND IT IS THE LATEST ONE — proven LIVE from both sides:
            // the SECOND task's duplicate is re-acknowledged, while the FIRST task's identical
            // duplicate no longer is and falls to the ordinary validation instead, even though its
            // own receipt is still durably retained.
            await h.AssertLatestEligibleStillReAcknowledgedAsync("task-ack-second");
            await h.AssertNoLatestEligibilityForAsync(
                "task-ack-first",
                HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask,
                expectedAcknowledgementsForTask: 2);

            // BOTH completions were accepted and released exactly once, and both are durable.
            Assert.Equal(1, h.DownstreamHandledCount("task-ack-first"));
            Assert.Equal(1, h.DownstreamHandledCount("task-ack-second"));
            Assert.Equal(1, h.DashboardNotifications);
            Assert.NotNull(h.ReadReceipt("task-ack-first"));
            Assert.NotNull(h.ReadReceipt("task-ack-second"));

            // …and each task carries exactly its own completion's acknowledgement PLUS the one its
            // live eligibility probe drew: two for the first, two for the second, and nothing else.
            Assert.Equal(
                2,
                h.Writer.Messages.Count(
                    m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck
                         && string.Equals(
                             m.CompletionReceiptAck.TaskId, "task-ack-first", StringComparison.Ordinal)));
            Assert.Equal(
                2,
                h.Writer.Messages.Count(
                    m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck
                         && string.Equals(
                             m.CompletionReceiptAck.TaskId, "task-ack-second", StringComparison.Ordinal)));
            Assert.Equal(
                4,
                h.Writer.Messages.Count(
                    m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck));
        });
    }

    /// <summary>
    /// A WORKER THAT REQUESTED THE ACKNOWLEDGEMENT BUT WAS ANSWERED BY AN ORCHESTRATOR WITH NO
    /// COMPLETION RECORDER STAYS DISABLED, and the completion then fails CLOSED exactly as before:
    /// the failure is the RECORDER's, not the negotiation's, so this is genuinely the
    /// missing-recorder cell rather than an ACK refusal.
    /// </summary>
    [Fact]
    public async Task RequestedAckWithoutRecorder_IsDisabledAndTheCompletionFailsClosed()
    {
        var h = Harness.CreateWithoutCompletionRecorderAndRequestedAck();
        await RunAsync(h, async () =>
        {
            // THE REQUEST WAS RECORDED, THE ANSWER WAS NOT ENABLEMENT.
            Assert.True(h.Worker.RequestCompletionReceiptAck);
            Assert.False(h.Worker.CompletionReceiptAckEnabled);

            h.Pool.MarkBusy(WorkerId, "task-ack-no-recorder");
            h.Worker.Role = DomainWorkerRole.Coder;
            h.Worker.CurrentModel = "assigned-model";
            h.Queue.Activate(h.BuildTask("task-ack-no-recorder", "assigned-model"), WorkerId);
            h.ResetDashboardNotifications();

            // A PRESENT model, so the refusal is provably the missing recorder and not the disabled
            // registration's own (non-existent) presence requirement.
            await h.CompleteAndAwaitNotRecordedWithPresentModelAsync(
                "task-ack-no-recorder",
                nameof(WorkerCompletionRecordingFailureReason.MissingRecorder));

            Assert.True(h.Worker.IsBusy);
            Assert.NotNull(h.Queue.GetActiveTask("task-ack-no-recorder"));
            Assert.Equal(0, h.DashboardNotifications);
            Assert.Null(h.ReadReceipt("task-ack-no-recorder"));

            // NO ELIGIBILITY was created; the task remains HELD (asserted above), so it can never be
            // classified as a duplicate — the absence is asserted at the LIVE pump.
            await h.AssertNoAcknowledgementAsync();
            Assert.False(h.StreamEnded);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (N2) THE COMPLETION-PUBLICATION SELECTION HOLD — the interleaving
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE CORE INTERLEAVING REGRESSION: while a negotiated completion's acknowledgement is still
    /// being published, an INDEPENDENTLY RUNNING real eager dispatch cannot NEWLY SELECT the
    /// just-released worker, so no successor is assigned; once the publication finishes, the real
    /// Ready route delivers it normally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE CHAIN IS REAL THROUGHOUT. A is ACK-enabled through the production registration RPC and
    /// holds a genuinely assigned, recorded task; the completion travels the real <c>WorkStream</c>
    /// read loop; the pause is the production post-release/pre-acknowledgement hook (with NO pool lock
    /// held); B is dispatched by a REAL <see cref="TaskDispatchService"/> over the SAME pool and queue,
    /// using a REAL <see cref="GrpcWorkerGateway"/> handed the REAL
    /// <see cref="WorkerAssignmentPublisher"/> — the same publisher the Ready route uses — so a
    /// passing vector cannot be one in which eager publication was merely disabled.
    /// </para>
    /// <para>
    /// WHAT IS ASSERTED AT THE PAUSE, and why each is discriminating: B's dispatch ran all the way to
    /// the delivery transaction — it admitted its slot, claimed the pipeline pointer, committed its
    /// mapping and enqueued B — and then found NO SELECTABLE WORKER, so B is neither active in the
    /// queue nor recorded in the assignment-context store nor published to the channel, and
    /// <see cref="WorkerPool.GetIdleWorker"/> returns nothing. WITHOUT the hold, the released worker is
    /// idle at that instant, so the eager dispatch selects it, activates B, marks it busy and publishes
    /// it — every one of those assertions flips.
    /// </para>
    /// <para>
    /// THE ORDERING IS READ OFF THE SOLE RESPONSE-PUMP WRITER'S OWN LEDGER, not from a counter: ACK(A)
    /// is recorded BEFORE Assignment(B), which is the whole property the slice exists to protect. A
    /// vector in which the eager push had selected the released worker would have published B's
    /// assignment in that window, i.e. at a different position in the same single ledger.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CompletionPublicationHeld_EagerDispatchCannotSelectTheReleasedWorker()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"copilothive-hold-eager-{Guid.NewGuid():N}.db");

        try
        {
            var h = Harness.CreateWithRequestedAckAndPublishedAssignmentSupport(dbPath);

            await RunAsync(h, async () =>
            {
                // THE SAME PUBLISHER FEEDS BOTH ROUTES — asserted, so this vector cannot pass because
                // the eager send would fail closed for a missing publisher.
                Assert.NotNull(h.AssignmentPublisher);

                // ── A: GENUINELY ASSIGNED AND RECORDED, DELIVERED THROUGH THE REAL READY ROUTE ──
                const string taskA = "task-hold-eager-a";
                var goalA = new Goal { Id = "goal-hold-eager", Description = "held completion" };
                h.Manager.CreatePipeline(goalA, maxRetries: 3);
                h.GoalSource.Register(goalA);
                var pipelineA = h.Manager.GetByGoalId(goalA.Id);
                Assert.NotNull(pipelineA);

                pipelineA!.AllocateAttemptAndRegisterSlot(taskA, new WorkSlotPosition(1, GoalPhase.Coding, 1));
                pipelineA.SetActiveTask(taskA);
                h.Manager.RegisterTask(taskA, goalA.Id);
                h.Queue.Enqueue(h.BuildTask(taskA, "assigned-model") with { GoalId = goalA.Id });

                await h.ReadyAndAwaitAssignmentPublishedAsync(taskA);

                Assert.True(h.Worker.IsBusy);
                Assert.Equal(taskA, h.Worker.CurrentTaskId);
                Assert.NotNull(h.Queue.GetActiveTask(taskA));
                h.ResetDashboardNotifications();

                // ── B: A DISTINCT, VALID PIPELINE READY FOR AN EAGER PUSH ──────────────────────
                const string goalB = "goal-hold-eager-b";
                var goal = new Goal
                {
                    Id = goalB,
                    Description = "eager successor",
                    RepositoryNames = ["ownership-repo"],
                };
                h.Manager.CreatePipeline(goal, maxRetries: 3);
                h.GoalSource.Register(goal);
                var pipelineB = h.Manager.GetByGoalId(goalB);
                Assert.NotNull(pipelineB);
                pipelineB!.AdvanceTo(GoalPhase.Coding);
                var plan = IterationPlan.Default(includeImprove: true);
                pipelineB.SetPlan(plan);
                pipelineB.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);

                // ── THE PAUSE: INSIDE THE REAL COMPLETION PUBLICATION, AFTER THE RELEASE ───────
                var window = h.PauseInCompletionPublicationWindow();
                var acknowledgementA = h.AwaitAcknowledgementAsync();
                var downstreamA = h.DispatcherLogger.WaitFor(taskA);

                try
                {
                    h.PushCompletion(taskA, "assigned-model", true, CopilotHive.Shared.Grpc.TaskStatus.Completed);

                    await window.Entered.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

                    // THE PRECONDITIONS OF THE WINDOW ARE PRODUCTION'S OWN: the release has been
                    // applied (A is idle with no task and no model), its queue entry is gone, and the
                    // hold is in force.
                    Assert.Same(h.Worker, window.ObservedWorker);
                    Assert.Equal(taskA, window.ObservedTaskId);
                    Assert.False(h.Worker.IsBusy);
                    Assert.Null(h.Worker.CurrentTaskId);
                    Assert.Null(h.Worker.CurrentModel);
                    Assert.Null(h.Queue.GetActiveTask(taskA));
                    Assert.True(h.Worker.CompletionPublicationPending);
                    Assert.Null(h.Pool.GetIdleWorker());

                    // ── THE INDEPENDENT EAGER DISPATCH, AWAITED TO COMPLETION INSIDE THE WINDOW ──
                    // It runs to completion: it admits B's slot, claims the pointer, commits the
                    // mapping, enqueues B — and then finds NO selectable worker at the delivery
                    // transaction's own first step.
                    await h.EagerDispatcher.DispatchToRole(
                        pipelineB, DomainWorkerRole.Coder, "do B", TestContext.Current.CancellationToken);

                    var admittedB = pipelineB.ActiveTaskId;
                    Assert.NotNull(admittedB);

                    // B IS LEFT PENDING AND UNSETTLED:
                    //   * its mapping was admitted, so the dispatch really reached the delivery
                    //     transaction rather than being refused in the preparation…
                    Assert.Equal(goalB, h.Manager.GetByTaskId(admittedB!)!.GoalId);
                    //   * …it is NOT active in the queue…
                    Assert.Null(h.Queue.GetActiveTask(admittedB!));
                    //   * the worker is NOT busy with it…
                    Assert.False(h.Worker.IsBusy);
                    Assert.Null(h.Worker.CurrentTaskId);
                    //   * NO assignment context was recorded for it…
                    Assert.Null(h.Stores.AssignmentStore.Load(admittedB!));
                    //   * and IT IS STILL PENDING in the queue, undequeued and undelivered.
                    var pendingB = h.Queue.TryDequeueAny();
                    Assert.NotNull(pendingB);
                    Assert.Equal(admittedB, pendingB!.TaskId);

                    // …and, still, NOTHING IS SELECTABLE.
                    Assert.Null(h.Pool.GetIdleWorker());

                    // THE SOLE RESPONSE-PUMP WRITER HAS FORWARDED NOTHING FOR B YET.
                    Assert.DoesNotContain(
                        h.Writer.Messages,
                        m => m.Assignment is not null
                             && string.Equals(m.Assignment.TaskId, admittedB, StringComparison.Ordinal));

                    // ── THE PUBLICATION FINISHES, AND A'S OWN OUTCOMES ARE INTACT ──────────────
                    h.ClearCompletionPublicationHook();
                    window.Release();

                    var ackA = await acknowledgementA;
                    await downstreamA.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
                    await h.BarrierAsync();

                    Assert.Equal(taskA, ackA.TaskId);
                    Assert.Equal(WorkerId, ackA.WorkerId);
                    Assert.False(h.Worker.CompletionPublicationPending);
                    Assert.NotNull(h.ReadReceipt(taskA));
                    Assert.Equal(1, h.DownstreamHandledCount(taskA));
                    Assert.Equal(1, h.DashboardNotifications);

                    // ── AND THE HOLD IS OVER, BUT THE ENABLED INSTANCE STILL WAITS FOR ITS OWN
                    //    READY: A's successor is delivered only once that Ready arrives ──────────
                    Assert.False(h.Worker.CompletionPublicationPending);
                    Assert.True(h.Worker.AwaitingWorkerReady);
                    Assert.Null(h.Pool.GetIdleWorker());

                    var admittedBTaskId = pipelineB.ActiveTaskId!;
                    h.Queue.Enqueue(pendingB!);
                    await h.ReadyAndAwaitAssignmentPublishedAsync(admittedBTaskId);

                    Assert.True(h.Worker.IsBusy);
                    Assert.Equal(admittedBTaskId, h.Worker.CurrentTaskId);
                    Assert.NotNull(h.Stores.AssignmentStore.Load(admittedBTaskId));

                    // ── THE ORDERING, READ OFF THE ONE LEDGER THE REAL PUMP WRITES ─────────────
                    // ACK(A) was recorded BEFORE Assignment(B): the release publication completed
                    // before the successor's own assignment could be published at all.
                    var messages = h.Writer.Messages;
                    var ackIndex = IndexOfMessage(
                        messages,
                        m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck
                             && string.Equals(
                                 m.CompletionReceiptAck.TaskId, taskA, StringComparison.Ordinal));
                    var assignmentIndex = IndexOfMessage(
                        messages,
                        m => m.Assignment is not null
                             && string.Equals(m.Assignment.TaskId, admittedBTaskId, StringComparison.Ordinal));

                    Assert.True(ackIndex >= 0, "the completion's acknowledgement was never forwarded");
                    Assert.True(assignmentIndex >= 0, "the successor's assignment was never forwarded");
                    Assert.True(
                        ackIndex < assignmentIndex,
                        "the successor's assignment was forwarded BEFORE the completion's acknowledgement");
                }
                finally
                {
                    // THE WINDOW IS ALWAYS RELEASED AND THE HOOK ALWAYS CLEARED, whatever happened.
                    h.ClearCompletionPublicationHook();
                    window.Release();
                }
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                try
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch
                {
                    // Best-effort cleanup — a leftover temp file must never fail a test.
                }
            }
        }
    }

    /// <summary>The index of the FIRST forwarded message satisfying <paramref name="predicate"/>.</summary>
    /// <param name="messages">The forwarded ledger, in order.</param>
    /// <param name="predicate">Selects the message of interest.</param>
    /// <returns>The index, or <c>-1</c> when nothing matched.</returns>
    private static int IndexOfMessage(
        IReadOnlyList<OrchestratorMessage> messages, Func<OrchestratorMessage, bool> predicate)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (predicate(messages[i]))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// A LEGACY (DISABLED) REGISTRATION'S COMPLETION INSTALLS NO HOLD: the released worker is
    /// immediately selectable, and the eager dispatch therefore behaves exactly as it always did.
    /// </summary>
    /// <remarks>
    /// IT IS THE OTHER HALF OF THE HOLD'S CONTRACT. The hold is the NEGOTIATED path's alone, so a
    /// disabled registration must keep the unchanged legacy runtime — including leaving the just
    /// released worker available for a new selection in the very interval the enabled path withholds
    /// it. Asserting the hold IS set for the enabled registration (the interleaving vector above) and
    /// NOT set here is what keeps the opt-in honest rather than decorative.
    /// </remarks>
    [Fact]
    public async Task DisabledCompletion_InstallsNoHoldAndLeavesTheWorkerSelectableInTheWindow()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            Assert.False(h.Worker.CompletionReceiptAckEnabled);

            h.Assign("task-hold-disabled", model: "assigned-model");

            var window = h.PauseInCompletionPublicationWindow();

            try
            {
                h.PushCompletion("task-hold-disabled", null, false, CopilotHive.Shared.Grpc.TaskStatus.Completed);

                await window.Entered.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

                // THE RELEASE HAPPENED…
                Assert.False(h.Worker.IsBusy);
                Assert.Null(h.Queue.GetActiveTask("task-hold-disabled"));

                // …AND NO HOLD EXISTS: the instance is selectable exactly as before this slice.
                Assert.False(h.Worker.CompletionPublicationPending);
                Assert.Same(h.Worker, h.Pool.GetIdleWorker());
            }
            finally
            {
                h.ClearCompletionPublicationHook();
                window.Release();
            }

            await h.BarrierAsync();

            Assert.False(h.Worker.CompletionPublicationPending);
            Assert.Same(h.Worker, h.Pool.GetIdleWorker());
        });
    }

    /// <summary>
    /// A REFUSED CHECKED RELEASE ACQUIRES NO HOLD, and therefore clears none: the successor's
    /// ownership survives untouched and the instance is never withheld on behalf of a completion that
    /// was never applied.
    /// </summary>
    /// <remarks>
    /// THE WINDOW HOOK IS DELIBERATELY NOT EXPECTED TO FIRE HERE: a refused release must never reach
    /// the post-release window, so the observables are that the hook never fired AND no hold exists.
    /// </remarks>
    [Fact]
    public async Task RefusedCheckedRelease_AcquiresAndClearsNoHold()
    {
        const string refusedTaskId = "task-hold-refuse-predecessor";

        var h = Harness.CreateWithOwnershipMutationAfterRecordAndRequestedAck();
        await RunAsync(h, async () =>
        {
            Assert.True(h.Worker.CompletionReceiptAckEnabled);

            h.Assign(refusedTaskId, model: "assigned-model");
            h.ResetDashboardNotifications();

            // THE POST-RECORD OWNERSHIP MUTATION makes the CHECKED RELEASE itself the refusing guard:
            // the recorded receipt is real and durable, and the release is then refused at the
            // mutation point.
            h.RecorderHook!.AfterRecord = () => h.Pool.MarkIdle(WorkerId);

            var window = h.PauseInCompletionPublicationWindow();

            try
            {
                await h.CompleteAndAwaitCheckedReleaseRefusedWithPresentModelAsync(refusedTaskId);

                // THE POST-RELEASE WINDOW WAS NEVER ENTERED, so no hold existed to clear.
                Assert.Equal(0, window.Invocations);
                Assert.False(h.Worker.CompletionPublicationPending);

                // THE REFUSAL'S OWN STATE: the task is still ACTIVE, the worker is not busy with it
                // (the injected mutation idled it) and nothing was notified.
                Assert.NotNull(h.Queue.GetActiveTask(refusedTaskId));
                Assert.Equal(0, h.DashboardNotifications);
                Assert.Equal(0, h.DownstreamHandledCount(refusedTaskId));

                // NO HOLD IS INSTALLED: the instance is selectable, exactly as it became when the
                // injected mutation idled it — the refused release neither withheld nor released it.
                Assert.False(h.Worker.CompletionPublicationPending);
                Assert.Same(h.Worker, h.Pool.GetIdleWorker());
            }
            finally
            {
                h.ClearCompletionPublicationHook();
                window.Release();
            }
        });
    }

    /// <summary>
    /// AN ABA REPLACEMENT REGISTERED DURING THE PUBLICATION IS LEFT UNTOUCHED BY THE HOLD'S END: the
    /// finishing publication clears the flag on the EXACT instance it belongs to, never on whatever is
    /// registered under the id at that instant.
    /// </summary>
    /// <remarks>
    /// THE WINDOW IS THE DETERMINISTIC SEAM. The replacement is registered INSIDE the real
    /// post-release window — with the completion-publication hold already installed on the original
    /// instance — so the ordering "replacement lands, THEN the publication ends" is exact rather than
    /// raced. The vector asserts the replacement keeps its own (unheld, busy) state, that the original
    /// instance's flag is left alone by the clear, and that the stream's own handler still completes.
    /// </remarks>
    [Fact]
    public async Task CompletionPublicationHoldEnd_LeavesAnAbaReplacementUntouched()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            Assert.True(h.Worker.CompletionReceiptAckEnabled);

            const string taskId = "task-hold-aba-window";
            h.Assign(taskId, model: "assigned-model");
            h.ResetDashboardNotifications();

            var originalWorker = h.Worker;
            ConnectedWorker? replacement = null;

            var downstream = h.DispatcherLogger.WaitFor(taskId);
            var window = h.PauseInCompletionPublicationWindow();

            try
            {
                h.PushCompletion(taskId, "assigned-model", true);

                await window.Entered.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

                Assert.True(originalWorker.CompletionPublicationPending);

                // ── THE REPLACEMENT LANDS INSIDE THE WINDOW ────────────────────────────────────
                Assert.True(h.Pool.RemoveWorker(originalWorker));
                replacement = h.Pool.RegisterWorker(WorkerId, []);
                h.Pool.MarkBusy(WorkerId, taskId);
                replacement.CurrentModel = "replacement-model";
            }
            finally
            {
                h.ClearCompletionPublicationHook();
                window.Release();
            }

            // THE PUBLICATION FINISHES, AND ITS OWN OUTCOME IS INTACT — the ABA replacement does not
            // stop the completion's ordinary notification.
            await downstream.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // THE REPLACEMENT IS UNTOUCHED: not held, still busy with its own task and model.
            Assert.NotNull(replacement);
            Assert.False(replacement!.CompletionPublicationPending);
            Assert.True(replacement.IsBusy);
            Assert.Equal(taskId, replacement.CurrentTaskId);
            Assert.Equal("replacement-model", replacement.CurrentModel);

            // …AND THE ORIGINAL INSTANCE KEEPS ITS OWN HOLD: the finishing publication cleared the
            // flag it installed, but the stale instance is no longer registered, so nothing of a
            // DIFFERENT instance was mutated on its behalf.
            Assert.True(originalWorker.CompletionPublicationPending);
        });
    }

    /// <summary>
    /// THE HOLD ENDS ON A FAILED ACKNOWLEDGEMENT ENQUEUE: a closed channel plus a throwing enqueue
    /// diagnostic must lose neither the accepted completion nor the worker's selectability.
    /// </summary>
    /// <remarks>
    /// THE ENQUEUE ATTEMPT — NOT ITS DELIVERY — IS WHAT THE HOLD COVERS. This vector closes the
    /// channel so the <c>TryWrite</c> must fail, arms the diagnostic to throw, and then asserts the
    /// hold is GONE, the completion is fully released and notified exactly once, the eligibility was
    /// still advanced, and the worker is selectable again — i.e. the failed publication never strands
    /// selection. The hold's PRESENCE inside the window is asserted first, so a vector that never
    /// installed one cannot pass vacuously.
    /// </remarks>
    [Fact]
    public async Task CompletionPublicationHold_EndsOnAFailedAcknowledgementEnqueue()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            const string taskId = "task-hold-enqueue-fail";
            h.Assign(taskId, model: "assigned-model");
            h.ResetDashboardNotifications();

            Assert.True(h.Worker.MessageChannel.Writer.TryComplete());
            h.ServiceLogger.ArmThrowOnFragment(ProductionLogFragments.ReceiptAckNotQueued);

            // THE REAL DOWNSTREAM CHAIN IS AWAITED SEPARATELY: the notification is fire-and-forget, so
            // the barrier alone proves only that the handler returned.
            var downstream = h.DispatcherLogger.WaitFor(taskId);

            var window = h.PauseInCompletionPublicationWindow();
            var heldInsideWindow = false;

            try
            {
                h.PushCompletion(taskId, "assigned-model", true, CopilotHive.Shared.Grpc.TaskStatus.Completed);

                await window.Entered.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

                // THE HOLD IS IN FORCE DURING THE WINDOW, on the failed-enqueue path too.
                heldInsideWindow = h.Worker.CompletionPublicationPending;
                Assert.True(heldInsideWindow);
                Assert.Null(h.Pool.GetIdleWorker());
            }
            finally
            {
                h.ClearCompletionPublicationHook();
                window.Release();
            }

            await h.BarrierAsync();

            // ── THE PUBLICATION ATTEMPT FAILED, AND THE HOLD STILL ENDED ──────────────────────
            Assert.True(heldInsideWindow, "the hold was never observed inside the publication window");
            Assert.True(h.ServiceLogger.ThrowCount > 0, "the armed diagnostic fault never fired");
            Assert.False(h.Worker.CompletionPublicationPending);

            // THE SHORT HOLD ENDED AT THE ENQUEUE ATTEMPT — and, for this enabled registration, the
            // release also installed the wait for the instance's OWN Ready, so selectability is still
            // withheld (by that longer fact, not by the publication).
            h.AssertAwaitingItsOwnReadyAfterThePublication();

            await downstream.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // THE ACCEPTED COMPLETION SURVIVED COMPLETELY — released once, notified once, durable.
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Worker.CurrentTaskId);
            Assert.Null(h.Queue.GetActiveTask(taskId));
            Assert.Equal(1, h.DownstreamHandledCount(taskId));
            Assert.Equal(1, h.TransportNotifications);
            Assert.Equal(1, h.DashboardNotifications);
            Assert.NotNull(h.ReadReceipt(taskId));

            // …AND THE ELIGIBILITY WAS STILL ADVANCED BEFORE THE FAILED ENQUEUE, proven LIVE.
            await h.CompleteAndAwaitAcknowledgementNotQueuedAsync(taskId);
            Assert.False(h.Worker.CompletionPublicationPending);
        });
    }

    /// <summary>
    /// A HELD/Faulted RESPONSE WRITE DOES NOT RETAIN THE SELECTION HOLD: the hold ends after the
    /// enqueue ATTEMPT, not after delivery — even though the acknowledgement never reached the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE PUMP-LEVEL FAULT IS DELIBERATELY CHOSEN. The channel write SUCCEEDS here; the failure is
    /// the response writer's, which happens strictly after the handler returned and after the hold was
    /// already ended. That is exactly the boundary the contract names: a held or failed response write
    /// must never be interpreted as "still publishing", because the hold does not wait for the network
    /// write or the worker's acknowledgement.
    /// </para>
    /// <para>
    /// THE TWO WITHHOLDING FACTS ARE ASSERTED SEPARATELY. The short hold is gone; the longer wait for
    /// the instance's own accepted Ready is what still withholds it. Collapsing the two would let a
    /// mutant that silently cleared the wait pass as "the hold ended correctly".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CompletionPublicationHold_IsNotRetainedByAResponseWriteFault()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            const string taskId = "task-hold-write-fault";
            h.Assign(taskId, model: "assigned-model");
            h.ResetDashboardNotifications();

            var writerFault = new InvalidOperationException("the response writer threw SENTINEL");
            h.ArmOneShotAcknowledgementWriteFault(writerFault);
            h.ServiceLogger.ArmThrowOnFragment(ProductionLogFragments.ReceiptAckWriteFailed);

            var writeFailed = h.ServiceLogger.WaitFor(ProductionLogFragments.ReceiptAckWriteFailed);

            var result = await h.CompleteWithPresentModelAndAwaitDownstreamAsync(taskId, "assigned-model");

            await writeFailed.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // THE WRITE REALLY WAS ATTEMPTED AND REALLY FAULTED…
            Assert.Equal(1, h.AcknowledgementWriteAttempts);
            Assert.True(h.ServiceLogger.ThrowCount > 0, "the armed diagnostic fault never fired");

            // ──…AND THE HOLD IS GONE LONG BEFORE THAT, because it ends at the ENQUEUE ATTEMPT ──
            Assert.Equal(taskId, result.TaskId);
            Assert.False(h.Worker.CompletionPublicationPending);

            // THE RELEASE ALSO PUT THIS ENABLED INSTANCE INTO ITS READINESS WAIT, so the end of the
            // SHORT hold is not selectability: the instance is withheld until its own Ready.
            h.AssertAwaitingItsOwnReadyAfterThePublication();

            // THE ACCEPTED COMPLETION IS INTACT AND NOTIFIED EXACTLY ONCE.
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Queue.GetActiveTask(taskId));
            Assert.Equal(1, h.DownstreamHandledCount(taskId));
            Assert.Equal(1, h.TransportNotifications);
            Assert.Equal(1, h.DashboardNotifications);
            Assert.NotNull(h.ReadReceipt(taskId));
            Assert.False(h.StreamEnded);
        });
    }

    /// <summary>
    /// DUPLICATE RE-ACKNOWLEDGEMENTS NEVER ACQUIRE OR CLEAR A HOLD: the read-only duplicate branch
    /// performs no release, so no hold is installed, and the worker stays selectable throughout.
    /// </summary>
    /// <remarks>
    /// IT PINS THE BOUNDARY BETWEEN THE TWO PATHS. A hold is a property of the ORDINARY negotiated
    /// completion's publication — the duplicate branch publishes an acknowledgement too, but it
    /// releases nothing, removes nothing and notifies nothing, so there is no "released but not yet
    /// published" interval for it to cover. The single carried routing and the duplicate guards are
    /// asserted unchanged alongside.
    /// </remarks>
    [Fact]
    public async Task DuplicateReAcknowledgement_NeitherAcquiresNorClearsAHold()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            const string taskId = "task-hold-duplicate";
            h.Assign(taskId, model: "assigned-model");

            var acknowledgement = h.AwaitAcknowledgementAsync();
            await h.CompleteWithPresentModelAndAwaitDownstreamAsync(taskId, "assigned-model");

            var ack = await acknowledgement;
            Assert.Equal(taskId, ack.TaskId);

            // THE ORDINARY COMPLETION'S HOLD IS ALREADY OVER — and, because this registration
            // negotiated acknowledgements, the release put the instance into the wait for its OWN
            // Ready, so the end of the hold is not selectability.
            h.AssertAwaitingItsOwnReadyAfterThePublication();

            // ── THE DUPLICATE, THROUGH THE REAL READ LOOP, WHILE THE WORKER IS TRULY IDLE ─────
            await h.AssertLatestEligibleStillReAcknowledgedAsync(taskId);

            // It neither acquired a hold (which would have withheld the worker) nor disturbed one —
            // and it did not end the readiness wait either: only the instance's OWN Ready does that.
            Assert.False(h.Worker.CompletionPublicationPending);
            Assert.True(h.Worker.AwaitingWorkerReady);
            Assert.Null(h.Pool.GetIdleWorker());

            // …and the duplicate guards and the once-only ordinary behaviour are unchanged.
            Assert.Equal(2, h.AcknowledgedCountFor(taskId));
            Assert.Equal(1, h.DownstreamHandledCount(taskId));
            Assert.Equal(1, h.TransportNotifications);
        });
    }

    /// <summary>
    /// A THROW FROM A POST-ACQUISITION OPERATION STILL ENDS THE HOLD, EXACTLY: the publication's
    /// <c>try/finally</c> genuinely covers every operation after the hold was acquired, so the SHORT
    /// selection hold is over even though the publication faulted — and the release that had already
    /// been applied is preserved. What still withholds the enabled instance afterwards is the
    /// instance-local wait for its own Ready, not the publication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE MANDATORY "EVERY POST-ACQUISITION OPERATION IS PROTECTED" PROOF, and it is
    /// behavioural rather than structural. The fault is raised from the post-release window — the
    /// representative post-acquisition operation, sitting between the queue removal and the
    /// eligibility advance — so a publication whose removal, hook, eligibility or enqueue had escaped
    /// the guarded scope, or whose <c>finally</c> had been deleted, leaves the hold installed and
    /// FAILS here.
    /// </para>
    /// <para>
    /// THE DELIVERY IS DIRECT AND SYNCHRONOUS ON PURPOSE. <c>HandleTaskComplete</c> is invoked
    /// directly so the production exception propagates INTO the vector (the read loop would otherwise
    /// unwind the stream and the fault would be observed only as a dead producer). The call returning
    /// by throwing is itself the post-handler barrier: everything the handler did is complete when the
    /// exception surfaces.
    /// </para>
    /// <para>
    /// WHAT IT DELIBERATELY DOES NOT CLAIM: no recovery, no retry and no notification is asserted for
    /// the FAULTED publication — a throwing post-acquisition operation is a test-only fault, and the
    /// contract is only that the hold ends, the release survives, and nothing extra is released or
    /// notified.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CompletionPublicationHold_EndsWhenAPostAcquisitionOperationThrows()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            const string taskId = "task-hold-window-throws";
            Assert.True(h.Worker.CompletionReceiptAckEnabled);

            h.Assign(taskId, model: "assigned-model");
            h.ResetDashboardNotifications();

            // THE PUMP IS PINNED FIRST, so a stray acknowledgement could actually be forwarded and
            // the "nothing was published" assertion below is a real absence rather than an unbound pump.
            await h.AssertPumpObservationIsLiveAsync();

            var windowFault = new InvalidOperationException("the post-release window threw SENTINEL");
            var heldInsideWindow = false;
            var busyInsideWindow = true;
            var queueEntryInsideWindow = true;
            var windowRan = false;

            h.ArmThrowingCompletionPublicationWindow(windowFault, (pinned, deliveredTaskId) =>
            {
                windowRan = true;
                Assert.Same(h.Worker, pinned);
                Assert.Equal(taskId, deliveredTaskId);

                // THE STATE AT THE INSTANT OF THE FAULT: the release has been applied, the queue entry
                // is gone, and the hold IS installed — so what follows is genuinely "the hold was held
                // when the operation threw".
                heldInsideWindow = pinned.CompletionPublicationPending;
                busyInsideWindow = pinned.IsBusy;
                queueEntryInsideWindow = h.Queue.GetActiveTask(deliveredTaskId) is not null;
            });

            // THE PRODUCTION EXCEPTION SURFACES HERE, unwrapped: the handler did not swallow it, which
            // is what makes the finally's cleanup the only thing that could have ended the hold.
            var ackState = new WorkStreamCompletionAckState();
            var thrown = Assert.Throws<InvalidOperationException>(
                () => h.InvokeHandleTaskCompleteDirectly(
                    h.Worker, taskId, ackState, model: "assigned-model"));
            Assert.Same(windowFault, thrown);

            // THE FAULT REALLY WAS RAISED FROM THE POST-ACQUISITION WINDOW, with the hold in force.
            Assert.True(windowRan, "the post-release window never ran; the fault proved nothing");
            Assert.True(
                heldInsideWindow,
                "the selection hold was not installed when the post-acquisition operation threw, so "
                + "this vector could not prove the finally cleans it up");
            Assert.False(busyInsideWindow, "the release had not been applied when the window ran");
            Assert.False(queueEntryInsideWindow, "the queue removal had not run when the window ran");

            // ── THE CONTRACT: THE HOLD IS ENDED, EXACTLY, DESPITE THE THROW ───────────────────
            Assert.False(
                h.Worker.CompletionPublicationPending,
                "a throwing post-acquisition operation left the selection hold installed — the "
                + "publication's try/finally does not cover every operation after the acquisition");

            // THE RELEASE IS COMPLETE, AND THE ENABLED INSTANCE NOW WAITS FOR ITS OWN READY: the
            // finally ended the SHORT hold, and nothing else could have made it selectable.
            h.AssertAwaitingItsOwnReadyAfterThePublication();

            // ── AND *ONLY* THE HOLD WAS CLEANED UP: the released completion is preserved ──────
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Worker.CurrentTaskId);
            Assert.Null(h.Worker.CurrentModel);
            Assert.Null(h.Queue.GetActiveTask(taskId));

            // THE EVIDENCE THE RELEASE WAS ABOUT IS STILL DURABLE — the fault did not undo the record.
            Assert.NotNull(h.ReadReceipt(taskId));

            // ── NOTHING EXTRA HAPPENED: the fault aborted the publication BEFORE the enqueue and
            //    BEFORE the ordinary notification, so neither an acknowledgement nor a notification
            //    was produced for it. This is the "no extra notification/release" half of the contract.
            AssertNoAcknowledgementPublished(h);
            Assert.Null(ackState.LatestEligibleTaskId);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount(taskId));
            Assert.Equal(0, h.DashboardNotifications);

            // ── THE STREAM AND THE POOL ARE NOT STRANDED: the worker is selectable, and an ordinary
            //    completion still works afterwards, taking and ending its own hold.
            h.Assign("task-hold-window-throws-next", model: "assigned-model");
            h.ResetDashboardNotifications();
            var acknowledgement = h.AwaitAcknowledgementAsync();
            await h.CompleteWithPresentModelAndAwaitDownstreamAsync(
                "task-hold-window-throws-next", "assigned-model");

            var ack = await acknowledgement;
            Assert.Equal("task-hold-window-throws-next", ack.TaskId);
            Assert.False(h.Worker.CompletionPublicationPending);
            h.AssertAwaitingItsOwnReadyAfterThePublication();
            Assert.Equal(1, h.DownstreamHandledCount("task-hold-window-throws-next"));
            Assert.Equal(1, h.TransportNotifications);
            Assert.Equal(1, h.DashboardNotifications);
        });
    }

    /// <summary>
    /// THE ORDINARY DASHBOARD NOTIFICATION HAPPENS AFTER THE HOLD IS FINISHED: at the instant the
    /// notification's own observation runs, the instance is already selectable again.
    /// </summary>
    /// <remarks>
    /// THE ORDERING IS THE CONTRACT — "finish the hold BEFORE the ordinary dashboard/downstream
    /// notification" — and this observation runs INSIDE the production notification chain, so its read
    /// is the state at that exact instant rather than a later one. A regression that moved the hold's
    /// end after the notification would observe the flag still set here.
    /// </remarks>
    [Fact]
    public async Task CompletionPublicationHold_IsFinishedBeforeTheOrdinaryNotification()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            const string taskId = "task-hold-order";
            h.Assign(taskId, model: "assigned-model");

            bool? heldAtNotification = null;
            h.ObserveAtNotification(() => heldAtNotification = h.Worker.CompletionPublicationPending);

            await h.CompleteWithPresentModelAndAwaitDownstreamAsync(taskId, "assigned-model");

            Assert.NotNull(heldAtNotification);
            Assert.False(
                heldAtNotification,
                "the selection hold was still installed when the completion was notified");
            Assert.False(h.Worker.CompletionPublicationPending);
        });
    }

    /// <summary>
    /// THE COMPLETION-PUBLICATION HOLD IS TAKEN AND FINISHED AROUND EVERY ORDINARY NEGOTIATED
    /// COMPLETION'S PUBLICATION, and never outlives it: two successive completions each take their
    /// own hold, each ends it, and the worker is selectable at the end.
    /// </summary>
    /// <remarks>
    /// THIS IS THE SLICE'S OWN REPETITION CHECK: an accidental hold that outlives its publication — a
    /// held instance that never becomes selectable again, or one whose second completion never takes
    /// its own hold — is visible here without reading the other vectors.
    /// </remarks>
    [Fact]
    public async Task CompletionPublicationHold_IsTakenAndFinishedForEachOrdinaryCompletion()
    {
        var h = Harness.CreateWithRequestedCompletionReceiptAck();
        await RunAsync(h, async () =>
        {
            foreach (var taskId in new[] { "task-hold-lifecycle-a", "task-hold-lifecycle-b" })
            {
                h.Assign(taskId, model: "assigned-model");
                h.ResetDashboardNotifications();

                var acknowledgement = h.AwaitAcknowledgementAsync();
                var window = h.PauseInCompletionPublicationWindow();
                var heldInsideWindow = false;

                try
                {
                    h.PushCompletion(taskId, "assigned-model", true);

                    await window.Entered.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

                    // THE HOLD IS IN FORCE FOR THIS PUBLICATION, AND THE RELEASE REALLY HAPPENED.
                    heldInsideWindow = h.Worker.CompletionPublicationPending;
                    Assert.True(heldInsideWindow);
                    Assert.False(h.Worker.IsBusy);
                    Assert.Null(h.Queue.GetActiveTask(taskId));
                    Assert.Null(h.Pool.GetIdleWorker());
                }
                finally
                {
                    h.ClearCompletionPublicationHook();
                    window.Release();
                }

                var ack = await acknowledgement;
                Assert.Equal(taskId, ack.TaskId);
                await h.BarrierAsync();

                // EACH COMPLETION'S OWN HOLD IS GONE — and the enabled instance is withheld by the
                // LONGER fact instead: the wait for its own Ready, which its next Ready ends.
                Assert.True(heldInsideWindow, "the hold was never observed inside the publication window");
                Assert.False(h.Worker.CompletionPublicationPending);
                h.AssertAwaitingItsOwnReadyAfterThePublication();
                h.GrantReadinessAndAssertSelectable();
            }

            // TWO COMPLETIONS, TWO HOLDS, TWO ACKNOWLEDGEMENTS — and the worker is not stranded.
            Assert.Equal(2, h.Writer.Messages.Count(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck));
        });
    }

    /// <summary>
    /// A READY ENQUEUED WHILE THE COMPLETION PUBLICATION IS PAUSED IS NOT LOST: the real WorkStream
    /// serializes Complete and Ready, so a Ready pushed during the paused post-release publication
    /// window is read only AFTER that publication finishes — by which time the SHORT hold has already
    /// been ended by its own finally. That single Ready is therefore accepted, clears the readiness
    /// wait, and dispatches the pending successor through the real publication point — with NO second
    /// Ready and no buffering, wake loop or deferred-Ready machinery anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE ORDERING IS EXACT RATHER THAN RACED. The publication window is the production hook
    /// between the checked release and the acknowledgement attempt, and the read loop handles one
    /// message at a time — so the Ready cannot be read while the completion handler is still paused.
    /// The acknowledgement and the downstream notification awaited BEFORE the Ready's acceptance line
    /// prove the publication had fully finished (hold cleared in its finally, wait still installed)
    /// before the Ready was handled.
    /// </para>
    /// <para>
    /// WHY THE DELIVERY PROVES THE WAIT. Between the publication's end and the Ready's acceptance the
    /// instance is idle, unheld and still awaiting its own Ready — unselectable for NEW selection, yet
    /// exactly the shape the checked idle accepts. One Ready then clears the wait and hands out the
    /// pending successor, which is the criterion's "no second Ready" proof.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ready_EnqueuedDuringThePausedPublication_IsNotLostAndDeliversAfterIt()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"copilothive-ready-in-window-{Guid.NewGuid():N}.db");

        try
        {
            // THE PUBLISHED-ASSIGNMENT HARNESS: the Ready route's real publisher is wired, so the
            // successor's delivery is a genuine production publication, not a fail-closed refusal.
            var h = Harness.CreateWithRequestedAckAndPublishedAssignmentSupport(dbPath);
            await RunAsync(h, async () =>
            {
                // ── A IS GENUINELY ASSIGNED AND THE SUCCESSOR IS PENDING ─────────────────────────
                const string taskId = "task-ready-in-window";
                h.Assign(taskId, model: "assigned-model");
                h.ResetDashboardNotifications();

                // THE SUCCESSOR'S DISPATCHABLE SETUP: a real pipeline with a Pending slot at the
                // active-task pointer and the task→goal mapping registered, so the Ready route's
                // REAL publisher can record and publish its delivery.
                const string successorId = "task-ready-in-window-successor";
                const string successorGoalId = "goal-ready-in-window-successor";
                var successorGoal = new Goal { Id = successorGoalId, Description = "successor" };
                h.Manager.CreatePipeline(successorGoal, maxRetries: 3);
                h.GoalSource.Register(successorGoal);
                var successorPipeline = h.Manager.GetByGoalId(successorGoalId);
                Assert.NotNull(successorPipeline);
                successorPipeline!.AllocateAttemptAndRegisterSlot(
                    successorId, new WorkSlotPosition(1, GoalPhase.Coding, 1));
                successorPipeline.SetActiveTask(successorId);
                h.Manager.RegisterTask(successorId, successorGoalId);

                h.Queue.Enqueue(
                    h.BuildTask(successorId, "successor-model") with { GoalId = successorGoalId });

                var acknowledgement = h.AwaitAcknowledgementAsync();
                var downstream = h.DispatcherLogger.WaitFor(taskId);

                // ── THE PAUSE: INSIDE THE REAL COMPLETION PUBLICATION, HOLD IN FORCE ─────────────
                var window = h.PauseInCompletionPublicationWindow();
                try
                {
                    h.PushCompletion(taskId, "assigned-model", true);
                    await window.Entered.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

                    // THE PRECONDITIONS ARE PRODUCTION'S OWN: released, hold installed, NOT
                    // selectable — the release installed the instance's readiness wait.
                    Assert.True(h.Worker.CompletionPublicationPending);
                    Assert.True(h.Worker.AwaitingWorkerReady);
                    Assert.Null(h.Pool.GetIdleWorker());

                    // ── THE READY IS ENQUEUED INTO THE REAL STREAM WHILE THE PUBLICATION IS PAUSED ──
                    // The read loop is synchronous, so the message CANNOT be read while the
                    // completion handler is paused inside this window; it simply waits behind it.
                    var accepted = h.ServiceLogger.WaitFor(ProductionLogFragments.ReadyAccepted);
                    var forwarded = h.Writer.WaitForAssignment();
                    h.PushReady();

                    // ── THE PUBLICATION FINISHES (the window is the ONLY thing holding it) ──────────
                    h.ClearCompletionPublicationHook();
                    window.Release();

                    var ack = await acknowledgement;
                    Assert.Equal(taskId, ack.TaskId);
                    await downstream.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

                    // ── THE IN-WINDOW READY IS READ AFTERWARD, ACCEPTED, AND NOT LOST ───────────────
                    // The SHORT hold is already gone, so the checked idle accepts this ONE Ready: it
                    // ends the wait and dispatches the pending successor — no second Ready needed.
                    await accepted.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

                    var assignment = await forwarded.WaitAsync(
                        BoundedWait, TestContext.Current.CancellationToken);
                    Assert.Equal(successorId, assignment.TaskId);

                    // ── THE ORDERING, READ OFF THE ONE LEDGER THE REAL PUMP WRITES ─────────────────
                    // ACK(A) was forwarded BEFORE Assignment(successor): the completion publication
                    // (hold cleared in its finally, wait installed) fully finished before the Ready
                    // that had been enqueued behind it was read and dispatched the successor.
                    var messages = h.Writer.Messages;
                    var ackIndex = IndexOfMessage(
                        messages,
                        m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck
                             && string.Equals(m.CompletionReceiptAck.TaskId, taskId, StringComparison.Ordinal));
                    var assignmentIndex = IndexOfMessage(
                        messages,
                        m => m.Assignment is not null
                             && string.Equals(m.Assignment.TaskId, successorId, StringComparison.Ordinal));

                    Assert.True(ackIndex >= 0, "the completion's acknowledgement was never forwarded");
                    Assert.True(assignmentIndex >= 0, "the successor's assignment was never forwarded");
                    Assert.True(
                        ackIndex < assignmentIndex,
                        "the successor's assignment was forwarded BEFORE the completion's acknowledgement — "
                        + "the paused publication did not finish before the in-window Ready was read");

                    // THE WAIT IS ENDED BY THAT ONE READY — and the successor is genuinely claimed.
                    Assert.False(h.Worker.AwaitingWorkerReady);
                    Assert.False(h.Worker.CompletionPublicationPending);
                    Assert.True(h.Worker.IsBusy);
                    Assert.Equal(successorId, h.Worker.CurrentTaskId);
                    Assert.Equal("successor-model", h.Worker.CurrentModel);
                    Assert.NotNull(h.Queue.GetActiveTask(successorId));
                    Assert.Null(h.Queue.TryDequeueAny());
                }
                finally
                {
                    h.ClearCompletionPublicationHook();
                    window.Release();
                }
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                try
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch
                {
                    // Best-effort cleanup — a leftover temp file must never fail a test.
                }
            }
        }
    }

    /// <summary>
    /// Asserts NO acknowledgement was forwarded to the worker: not as the new oneof case, and not as
    /// a stray message that happens to carry a <see cref="CompletionReceiptAck"/> payload.
    /// </summary>
    /// <param name="harness">The harness whose production pump observation is inspected.</param>
    private static void AssertNoAcknowledgementPublished(Harness harness)
    {
        Assert.DoesNotContain(
            harness.Writer.Messages,
            m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck);
        Assert.DoesNotContain(harness.Writer.Messages, m => m.CompletionReceiptAck is not null);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (A) THE EXCLUSIVE PER-INSTANCE WORKSTREAM ATTACHMENT CLAIM
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EXACTLY ONE OF TWO LIVE STREAMS FOR THE SAME REGISTERED INSTANCE ATTACHES, and the winner
    /// keeps working normally afterwards.
    /// </summary>
    /// <remarks>
    /// THE WINNER IS IDENTIFIED BY PRODUCTION, NOT ASSUMED: this harness's primary stream is the one
    /// that already pinned the instance, and it stays the only stream that ever consumes the
    /// channel — so the later completion reaches the primary writer and the real downstream chain.
    /// </remarks>
    [Fact]
    public async Task Attachment_TwoStreamsForSameInstance_OnlyOneWinsAndKeepsWorking()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            // The primary stream pins the instance first.
            await h.BarrierAsync();
            Assert.True(h.Worker.IsWorkStreamAttached);

            // A SECOND stream attempts the SAME instance; it must lose.
            var second = await h.StartSecondStreamAndAwaitTerminationAsync(WorkerId);
            h.AssertSecondStreamWasRejectedNormally(second);
            Harness.AssertSecondStreamForwardedNothing(second);

            // THE WINNER IS UNTOUCHED AND STILL FULLY FUNCTIONAL: its registration state survives,
            // its channel still delivers, and its completion still reaches the real downstream chain.
            Assert.Same(h.Worker, h.Pool.GetWorker(WorkerId));

            h.Assign("task-attach-win", model: "assigned-model");
            var result = await h.CompleteAndAwaitDownstreamAsync("task-attach-win");
            Assert.Equal("task-attach-win", result.TaskId);
            Assert.Equal(1, h.DownstreamHandledCount("task-attach-win"));

            // The loser forwarded nothing, even after the winner's completion published.
            Harness.AssertSecondStreamForwardedNothing(second);
        });
    }

    /// <summary>
    /// THE LOSING STREAM'S FIRST MESSAGE HAS NO EFFECT WHATSOEVER. Its first message is a Ready for
    /// the SAME instance while a task is still held, so a stream that processed it would be refused
    /// by the Ready guard and would EMIT the ready-ignored warning — the loser emits neither that
    /// warning nor the acceptance line, keeps no ownership, and produces no receipt or notification.
    /// </summary>
    [Fact]
    public async Task Attachment_LosingFirstReady_HasNoDispatchReceiptOrNotificationEffect()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            // A held assignment: the winner is busy, so ANY stream that handled a Ready here would
            // have to refuse it and log the refusal — an unmistakable production observable.
            h.Assign("task-attach-held", model: "assigned-model");
            var tasksEnqueuedBefore = h.TasksEnqueued;

            // The primary stream pins the instance (Progress resolves no assignment).
            await h.BarrierAsync();

            var second = await h.StartSecondStreamAndAwaitTerminationAsync(WorkerId);
            h.AssertSecondStreamWasRejectedNormally(second);

            // NO DISPATCH EFFECT: the loser never dequeued, never assigned and never enqueued.
            Assert.Equal(tasksEnqueuedBefore, h.TasksEnqueued);
            Assert.Equal("task-attach-held", h.Worker.CurrentTaskId);
            Assert.True(h.Worker.IsBusy);
            Assert.NotNull(h.Queue.GetActiveTask("task-attach-held"));

            // NO READY-HANDLING EFFECT: the loser never reached the Ready guards, so NEITHER the
            // refusal diagnostic nor the acceptance line exists for it.
            Assert.DoesNotContain(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.ReadyIgnored, StringComparison.Ordinal));
            Assert.DoesNotContain(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.ReadyAccepted, StringComparison.Ordinal));

            // NO COMPLETION/RECEIPT/NOTIFICATION EFFECT.
            Assert.Equal(0, h.TransportNotifications);
            Assert.Null(h.ReadReceipt("task-attach-held"));

            // The loser never forwarded a channel message either.
            Harness.AssertSecondStreamForwardedNothing(second);
        });
    }

    /// <summary>
    /// A LOSING FIRST <c>Complete</c> OR <c>tool_request</c> MESSAGE IS LIKEWISE INERT. For a
    /// completion, a stream that handled it would publish the acceptance provenance line, release
    /// the assignment and record a receipt; for a tool request, it would run the tool and log the
    /// call. The loser does none of them.
    /// </summary>
    /// <param name="shape">0 = Complete, 1 = tool_request.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Attachment_LosingFirstCompleteOrToolRequest_HasNoEffect(int shape)
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-attach-shape", model: "assigned-model");
            var tasksEnqueuedBefore = h.TasksEnqueued;

            // The primary stream pins the instance first.
            await h.BarrierAsync();

            const string toolName = "report_progress";
            var first = shape switch
            {
                0 => new WorkerMessage
                {
                    WorkerId = WorkerId,
                    Complete = new GrpcTaskComplete
                    {
                        TaskId = "task-attach-shape",
                        Status = CopilotHive.Shared.Grpc.TaskStatus.Completed,
                        Output = "loser-output",
                    },
                },
                1 => new WorkerMessage
                {
                    WorkerId = WorkerId,
                    ToolRequest = new ToolCallRequest
                    {
                        RequestId = "loser-req",
                        TaskId = "task-attach-shape",
                        ToolName = toolName,
                        ArgumentsJson = "{\"status\":\"loser\",\"details\":\"loser\"}",
                    },
                },
                _ => throw new InvalidOperationException($"unknown shape '{shape}'"),
            };

            var second = h.StartSecondStream(WorkerId, first);
            second.Completion = await h.AwaitSecondStreamTerminationAsync(second);
            h.AssertSecondStreamWasRejectedNormally(second);

            // NO COMPLETION EFFECT: the winner still owns the task, nothing was released, no
            // acceptance provenance line was emitted and no receipt was recorded.
            Assert.Equal("task-attach-shape", h.Worker.CurrentTaskId);
            Assert.True(h.Worker.IsBusy);
            Assert.NotNull(h.Queue.GetActiveTask("task-attach-shape"));
            Assert.Equal(tasksEnqueuedBefore, h.TasksEnqueued);
            Assert.Equal(0, h.TransportNotifications);
            Assert.Null(h.ReadReceipt("task-attach-shape"));
            Assert.DoesNotContain(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionAccepted, StringComparison.Ordinal));

            // NO TOOL EFFECT: the loser never dispatched a tool call.
            Assert.DoesNotContain(
                h.ServiceLogger.Messages,
                m => m.Contains($"Tool call '{toolName}'", StringComparison.Ordinal));

            Harness.AssertSecondStreamForwardedNothing(second);
        });
    }

    /// <summary>
    /// THE LOSER REMOVES NOTHING, CLEARS NO HEARTBEAT STATE AND NOTIFIES NO DISCONNECTION. The
    /// dashboard counter and the heartbeat dictionary are production observables, so a loser that
    /// ran the normal teardown would be caught even though its message named the same worker id.
    /// </summary>
    [Fact]
    public async Task Attachment_LosingStream_RemovesNothingClearsNoHeartbeatAndNotifiesNothing()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            await h.BarrierAsync();

            // A genuine heartbeat entry for the instance, created by the REAL heartbeat path.
            await h.SendHeartbeatAsync();
            Assert.Contains(WorkerId, h.HeartbeatStateKeys());

            h.ResetDashboardNotifications();

            var second = await h.StartSecondStreamAndAwaitTerminationAsync(WorkerId);
            h.AssertSecondStreamWasRejectedNormally(second);

            // NOT REMOVED: the instance is still registered, and the pool still identifies it.
            Assert.Same(h.Worker, h.Pool.GetWorker(WorkerId));
            Assert.Equal(1, h.Pool.ConnectedWorkerCount);

            // NO HEARTBEAT CLEANUP: the winner's throttle entry survives the loser's teardown.
            Assert.Contains(WorkerId, h.HeartbeatStateKeys());

            // NO DISCONNECTION NOTIFICATION OF ANY KIND.
            Assert.Equal(0, h.DashboardNotifications);
        });
    }

    /// <summary>
    /// A LOSING STREAM NEITHER CANCELS NOR UNWINDS THE WINNER: after the loser has returned
    /// normally, the winner's stream is still live and still drives the full
    /// completion -> release -> Ready sequence.
    /// </summary>
    [Fact]
    public async Task Attachment_LosingStream_DoesNotDisturbTheWinnersStream()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            await h.BarrierAsync();
            Assert.False(h.StreamEnded);

            var second = await h.StartSecondStreamAndAwaitTerminationAsync(WorkerId);
            h.AssertSecondStreamWasRejectedNormally(second);

            // THE WINNER IS STILL LIVE: it accepts a completion AND a following Ready.
            h.Assign("task-attach-live", model: "assigned-model");
            await h.CompleteAndAwaitDownstreamAsync("task-attach-live");
            await h.ReadyAndAwaitAcceptedAsync();

            Assert.False(h.StreamEnded, "the winner must not be ended by the loser's rejection");
            Assert.Same(h.Worker, h.Pool.GetWorker(WorkerId));
        });
    }

    /// <summary>
    /// THE CLAIM IS ONE-WAY AND PER INSTANCE: it is NOT reset by the winning stream's own teardown,
    /// so the same instance can never be re-attached — while a re-registration under the same id
    /// produces a NEW instance with a FRESH claim that attaches normally.
    /// </summary>
    [Fact]
    public async Task Attachment_ClaimIsOneWay_ReRegistrationYieldsANewEligibleInstance()
    {
        var first = Harness.Create();
        await RunAsync(first, async () =>
        {
            await first.BarrierAsync();
            Assert.True(first.Worker.IsWorkStreamAttached);

            // A second stream loses even while the winner is live.
            var whileLive = await first.StartSecondStreamAndAwaitTerminationAsync(WorkerId);
            first.AssertSecondStreamWasRejectedNormally(whileLive);
        });

        // The winning stream's teardown has now run; the claim is STILL held — never reset or reused.
        Assert.True(first.Worker.IsWorkStreamAttached);
        Assert.False(first.Worker.TryAttachWorkStream());

        // A RE-REGISTERED instance is a DIFFERENT object with its OWN fresh claim.
        var replacement = Harness.Create();
        await RunAsync(replacement, async () =>
        {
            // Swap the instance under the same id BEFORE the stream pins it, so the stream that
            // attaches is genuinely the replacement.
            Assert.True(replacement.Pool.RemoveWorker(replacement.Worker));
            var fresh = replacement.Pool.RegisterWorker(WorkerId, []);
            Assert.NotSame(replacement.Worker, fresh);

            await replacement.BarrierAsync();

            Assert.Same(fresh, replacement.Pool.GetWorker(WorkerId));
            Assert.True(fresh.IsWorkStreamAttached);

            // The replacement is separately eligible — and a second stream still loses to IT.
            var second = await replacement.StartSecondStreamAndAwaitTerminationAsync(WorkerId);
            replacement.AssertSecondStreamWasRejectedNormally(second);
        });
    }

    /// <summary>
    /// DISTINCT WORKERS ATTACH INDEPENDENTLY: a second stream over a DIFFERENT registered instance
    /// attaches, pins its own instance and reaches its own Ready acceptance — while the
    /// already-attached diagnostic is never emitted for it.
    /// </summary>
    [Fact]
    public async Task Attachment_DistinctWorkers_AttachIndependentlyOnTheSameService()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            const string otherId = "ownership-worker-2";
            var other = h.Pool.RegisterWorker(otherId, []);

            // The primary stream takes THIS harness's instance.
            await h.BarrierAsync();
            Assert.True(h.Worker.IsWorkStreamAttached);

            // A second stream for a DIFFERENT id must attach, and its own Ready acceptance line is
            // the deterministic gate proving the claim happened in PRODUCTION.
            var accepted = h.ServiceLogger.WaitFor(ProductionLogFragments.ReadyAccepted);
            var otherStream = h.StartSecondStream(otherId, new WorkerMessage
            {
                WorkerId = otherId,
                Ready = new WorkerReady(),
            });

            await accepted.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            Assert.True(other.IsWorkStreamAttached);
            Assert.Contains(
                h.ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.ReadyAccepted, StringComparison.Ordinal)
                     && m.Contains(otherId, StringComparison.Ordinal));

            // The already-attached diagnostic was NEVER emitted for the distinct worker.
            Assert.DoesNotContain(
                h.ServiceLogger.Messages,
                m => m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.WorkStreamAlreadyAttached,
                         StringComparison.Ordinal)
                     && m.Contains(otherId, StringComparison.Ordinal));

            // Both instances remain registered, each owning its own stream.
            Assert.Same(h.Worker, h.Pool.GetWorker(WorkerId));
            Assert.Same(other, h.Pool.GetWorker(otherId));
            Assert.Equal(2, h.Pool.ConnectedWorkerCount);

            // Ending the second stream's reader lets it complete normally.
            otherStream.Reader.Complete();
            await otherStream.Producer.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        });
    }

    /// <summary>
    /// REMOVING AND RE-REGISTERING THE SAME ID YIELDS A NEW ELIGIBLE INSTANCE, and a STALE winning
    /// stream's cleanup cannot remove it. The replacement's own claim is intact, so the attachment
    /// claim is stream ownership only — never a re-registration authorization.
    /// </summary>
    [Fact]
    public async Task Attachment_StaleWinnerCleanup_CannotRemoveTheReRegisteredInstance()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            var stale = h.Worker;
            await h.BarrierAsync();
            Assert.True(stale.IsWorkStreamAttached);

            // A replacement registers under the same id while the stale stream is still live, and it
            // takes its OWN stream, which attaches to the replacement.
            Assert.True(h.Pool.RemoveWorker(stale));
            var replacement = h.Pool.RegisterWorker(WorkerId, []);
            Assert.Same(replacement, h.Pool.GetWorker(WorkerId));

            var accepted = h.ServiceLogger.WaitFor(ProductionLogFragments.ReadyAccepted);
            var replacementStream = h.StartSecondStream(WorkerId, new WorkerMessage
            {
                WorkerId = WorkerId,
                Ready = new WorkerReady(),
            });

            await accepted.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // The replacement's stream was NOT rejected as a duplicate: it is a DIFFERENT instance.
            Assert.True(replacement.IsWorkStreamAttached);
            Assert.DoesNotContain(
                h.ServiceLogger.Messages,
                m => m.Contains(
                    HiveOrchestratorService.OwnershipRefusalReasons.WorkStreamAlreadyAttached,
                    StringComparison.Ordinal));

            // The STALE stream's teardown path must refuse, because the pool no longer holds that
            // instance — the same instance-aware removal the winning stream uses.
            Assert.False(h.Pool.RemoveWorker(stale));

            // The replacement — and its claim — survive untouched.
            Assert.Same(replacement, h.Pool.GetWorker(WorkerId));
            Assert.True(replacement.IsWorkStreamAttached);
            Assert.Equal(1, h.Pool.ConnectedWorkerCount);
            Assert.Equal(WorkerId, replacementStream.WorkerId);
        });
    }
    // ═══════════════════════════════════════════════════════════════════════
    //  harness
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A live <see cref="HiveOrchestratorService"/> over real collaborators and a REAL
    /// <c>WorkStream</c>, wired so that downstream completion handling really runs through the REAL
    /// <see cref="GoalDispatcher"/> and publication is observed at the gRPC response writer.
    /// </summary>
    private sealed class Harness
    {
        public required HiveOrchestratorService Service { get; init; }
        public required WorkerPool Pool { get; init; }
        public required TaskQueue Queue { get; init; }
        public required ConnectedWorker Worker { get; init; }

        /// <summary>The service's own signalling logger — the source of every transport log gate.</summary>
        public required SignallingLogger<HiveOrchestratorService> ServiceLogger { get; init; }

        /// <summary>
        /// The REAL dispatcher's logger, and therefore the source of the REAL
        /// <c>TaskCompletionService</c> downstream log lines. Downstream evidence is read HERE.
        /// </summary>
        public required SignallingLogger<GoalDispatcher> DispatcherLogger { get; init; }

        /// <summary>
        /// The REAL dispatcher. It is the LAST subscriber on the SHARED notifier, so production's
        /// own <c>NotifyAsync</c> awaits its handler chain.
        /// </summary>
        public required GoalDispatcher Dispatcher { get; init; }

        /// <summary>The pipeline registry shared by the service and the dispatcher.</summary>
        public required GoalPipelineManager Manager { get; init; }

        /// <summary>
        /// THE REAL EAGER DISPATCHER, wired over the SAME pool, the SAME queue and the SAME gateway
        /// the Ready route uses — so a vector can exercise the production eager push path exactly as
        /// it runs while the completion publication is in flight.
        /// </summary>
        /// <remarks>
        /// IT IS A SECOND, INDEPENDENT CONSUMER OF THE POOL, which is the whole point: the eager push
        /// path consults <see cref="WorkerPool.GetIdleWorker"/> directly and therefore never sees the
        /// stream-local eligibility holder or anything else that belongs to a <c>WorkStream</c>.
        /// </remarks>
        public required TaskDispatchService EagerDispatcher { get; init; }

        /// <summary>
        /// THE REAL PUBLISHER THE HARNESS BUILT, or <c>null</c> when published-assignment support was
        /// not requested. Exposed so a vector can prove the SAME instance is handed to BOTH delivery
        /// routes — a vector that ran with eager publication silently disabled would not be exercising
        /// the eager push at all.
        /// </summary>
        public required IWorkerAssignmentPublisher? AssignmentPublisher { get; init; }

        /// <summary>
        /// THE REAL RECORD/RECEIPT STORES the service's REAL completion recorder is built from —
        /// exposed for TEST OBSERVATION ONLY. The production path performs no readback.
        /// </summary>
        public required SequenceStores Stores { get; init; }

        /// <summary>The in-memory goal source backing the sequence vector's real cancellation.</summary>
        public required SequenceGoalSource GoalSource { get; init; }

        /// <summary>
        /// THE PUBLICATION OBSERVATION POINT: the gRPC response writer the real <c>WorkStream</c>
        /// pump forwards to. The test never reads the worker's message channel, so it can never
        /// race the production pump.
        /// </summary>
        public required SignallingStreamWriter Writer { get; init; }

        /// <summary>
        /// THE OWNERSHIP-MUTATING RECORDER DECORATOR, when the vector asked for one. It is the seam
        /// the post-record checked-release vectors use to invalidate ownership AFTER the real
        /// receipt has been written. <c>null</c> for every other harness.
        /// </summary>
        public OwnershipMutatingRecorder? RecorderHook { get; private init; }

        private ChannelStreamReader Reader { get; init; } = null!;

        /// <summary>The RETAINED producer task; the strict teardown joins exactly this instance.</summary>
        private Task StreamTask { get; init; } = null!;

        /// <summary>
        /// EVERY ADDITIONAL stream a vector started through
        /// <see cref="StartSecondStream"/>: its own reader, writer and retained task. They are
        /// joined by the SAME strict teardown as the primary stream, so a vector never leaks one.
        /// </summary>
        private readonly List<SecondStream> _extraStreams = [];

        private readonly CompletionObservations _observations;

        private int _barrierSequence;
        private int _tasksEnqueued;
        private int _dashboardNotifications;

        private Harness(CompletionObservations observations) => _observations = observations;

        /// <summary>Transport-level notifications the shared notifier emitted (auxiliary observation).</summary>
        public int TransportNotifications => _observations.Count;

        /// <summary>
        /// How many REAL <see cref="DashboardNotifier.NotifyStateChanged"/> invocations have been
        /// observed since the last <see cref="ResetDashboardNotifications"/>.
        /// </summary>
        /// <remarks>
        /// THE OBSERVATION IS THE PRODUCTION EVENT ITSELF: the harness subscribes to the real
        /// notifier's existing <see cref="DashboardNotifier.OnStateChanged"/> event, so a stray
        /// state-change raised anywhere on the completion path is counted here. No production type
        /// is modified to make this observable.
        /// </remarks>
        public int DashboardNotifications => Volatile.Read(ref _dashboardNotifications);

        /// <summary>
        /// Zeroes the dashboard counter, so a vector's assertion can only be broken by a
        /// notification raised AFTER the reset.
        /// </summary>
        /// <remarks>
        /// EVERY REFUSAL VECTOR RESETS AFTER ITS SETUP. The assignment path legitimately notifies
        /// (ApplyTaskAssignment does), so without the reset a setup notification would mask a stray
        /// completion-path notification — and, worse, a vector could "pass" while a regression
        /// notified.
        /// </remarks>
        public void ResetDashboardNotifications() => Interlocked.Exchange(ref _dashboardNotifications, 0);

        /// <summary>
        /// The LAST observation the shared notifier's first subscriber recorded, including the
        /// receipt probe taken AT the notification instant. <c>null</c> before any notification.
        /// </summary>
        public CompletionObservations.Observation? LastObservation => _observations.Last;

        /// <summary>How many tasks the REAL queue accepted — the "did the pipeline advance" probe.</summary>
        public int TasksEnqueued => Volatile.Read(ref _tasksEnqueued);

        /// <summary>
        /// INSTALLS A ONE-SHOT OBSERVATION THAT RUNS INSIDE THE PRODUCTION NOTIFICATION CHAIN, so a
        /// vector can read state AT the instant the completion was published rather than after it.
        /// </summary>
        /// <remarks>
        /// IT IS THE FIRST SUBSCRIBER'S OWN CALLBACK, so it runs before the awaiting test is released
        /// and inside production's <c>NotifyAsync</c> invocation. The observation is consumed once and
        /// never affects anything: a throw from it is the fixture's own bug and propagates where the
        /// vector can see it (this harness's notifier chain is the transport's, and the vector
        /// observes only its own state).
        /// </remarks>
        /// <param name="observe">The observation to run at the notification instant.</param>
        public void ObserveAtNotification(Action observe) => _observations.PendingObservation = observe;

        /// <summary>Whether the real stream task has terminated.</summary>
        public bool StreamEnded => StreamTask.IsCompleted;

        /// <summary>
        /// How many times the REAL downstream <c>TaskCompletionService</c> handled a completion for
        /// <paramref name="taskId"/>, counted from ITS OWN production log lines.
        /// </summary>
        /// <remarks>
        /// Every terminating path of <c>TaskCompletionService.HandleTaskCompletionAsync</c> emits a
        /// line naming the task id (the missing-pipeline warning, the terminal-goal line, the
        /// stale/duplicate warnings, or the "task completed" progress line), so a zero here means
        /// the real downstream chain was never entered for that task at all.
        /// </remarks>
        public int DownstreamHandledCount(string taskId) =>
            DispatcherLogger.Messages.Count(
                m => m.Contains(taskId, StringComparison.Ordinal)
                     && ProductionLogFragments.DownstreamEntered(m));

        public static Harness Create() => CreateCore(withPublishedAssignmentSupport: false, dbPath: null);
        /// <summary>
        /// Creates a harness whose worker reached the pool through the REAL <see cref="HiveOrchestratorService.Register"/>
        /// RPC carrying the completion-receipt REQUEST — so the instance's enablement is genuinely
        /// NEGOTIATED BY PRODUCTION and not set by the fixture. Everything else is the plain harness.
        /// </summary>
        /// <remarks>
        /// THE ROUTE MATTERS. Registering through the RPC is what makes it possible to assert that the
        /// registration REPLY and the PUBLISHED INSTANCE agree — the registration path constructs the
        /// answer from the instance it actually registered, and this factory proves both sides here
        /// before any stream is opened.
        /// </remarks>
        public static Harness CreateWithRequestedCompletionReceiptAck() =>
            CreateCore(
                withPublishedAssignmentSupport: false,
                dbPath: null,
                requestCompletionReceiptAck: true,
                registerThroughRealRpc: true);

        /// <summary>
        /// Creates a harness whose worker REQUESTED the acknowledgement but whose service has NO
        /// completion recorder configured at all — the negotiation cell that must stay DISABLED no
        /// matter what was asked for.
        /// </summary>
        public static Harness CreateWithoutCompletionRecorderAndRequestedAck() =>
            CreateCore(
                withPublishedAssignmentSupport: false,
                dbPath: null,
                withRecorder: false,
                requestCompletionReceiptAck: true,
                registerThroughRealRpc: true);

        /// <summary>
        /// Creates a harness WITH the published-assignment support the sequence vector needs: a
        /// REAL <see cref="WorkerAssignmentPublisher"/> over a REAL file-backed SQLite store
        /// supplied to the service.
        /// </summary>
        public static Harness CreateWithPublishedAssignmentSupport(string dbPath) =>
            CreateCore(withPublishedAssignmentSupport: true, dbPath);

        /// <summary>
        /// THE HARNESS THE INTERLEAVING REGRESSION NEEDS: an ACK-ENABLED registration (negotiated
        /// through the REAL registration RPC) AND the published-assignment support both dispatch
        /// routes require, over a REAL file-backed SQLite database.
        /// </summary>
        /// <remarks>
        /// BOTH HALVES ARE LOAD-BEARING. The enablement makes the completion path actually publish an
        /// acknowledgement (and therefore take the completion-publication hold), and the publisher —
        /// wired into the ONE gateway BOTH routes share — makes the eager send able to publish for
        /// real, so an ordering vector cannot pass merely because eager publication was disabled.
        /// </remarks>
        /// <param name="dbPath">The per-vector temporary database path.</param>
        /// <returns>The live harness.</returns>
        public static Harness CreateWithRequestedAckAndPublishedAssignmentSupport(string dbPath) =>
            CreateCore(
                withPublishedAssignmentSupport: true,
                dbPath,
                requestCompletionReceiptAck: true,
                registerThroughRealRpc: true);

        /// <summary>
        /// Creates a harness whose completion-receipt store carries the supplied EF interceptors —
        /// the write-uncertainty vector's injection point.
        /// </summary>
        public static Harness CreateWithReceiptInterceptors(
            string dbPath, params IInterceptor[] interceptors) =>
            CreateCore(withPublishedAssignmentSupport: false, dbPath, interceptors);

        /// <summary>
        /// Creates a harness with NO completion recorder configured at all — the fail-CLOSED
        /// disposition's own vector.
        /// </summary>
        public static Harness CreateWithoutCompletionRecorder() =>
            CreateCore(
                withPublishedAssignmentSupport: false, dbPath: null, interceptors: null, withRecorder: false);

        /// <summary>
        /// Creates a harness whose recorder is the REAL <see cref="WorkerCompletionRecorder"/>
        /// wrapped in an <see cref="OwnershipMutatingRecorder"/> decorator, so a vector can
        /// invalidate transport ownership at the instant AFTER the real receipt was written.
        /// </summary>
        /// <remarks>
        /// THE DECORATOR CHANGES NO RECORDING BEHAVIOUR: it delegates to the real recorder first
        /// and, only on a successful return, runs the vector's own mutation. Nothing about the
        /// production write path, its refusals or its evidence is altered.
        /// </remarks>
        public static Harness CreateWithOwnershipMutationAfterRecord() =>
            CreateCore(
                withPublishedAssignmentSupport: false,
                dbPath: null,
                interceptors: null,
                withRecorder: true,
                withOwnershipMutationHook: true);

        /// <summary>
        /// The SAME post-record mutation seam for an ENABLED registration: the worker negotiated the
        /// acknowledgement through the REAL registration RPC, so a refused checked release can be
        /// proven to create NO acknowledgement eligibility.
        /// </summary>
        public static Harness CreateWithOwnershipMutationAfterRecordAndRequestedAck() =>
            CreateCore(
                withPublishedAssignmentSupport: false,
                dbPath: null,
                interceptors: null,
                withRecorder: true,
                withOwnershipMutationHook: true,
                requestCompletionReceiptAck: true,
                registerThroughRealRpc: true);

        private static Harness CreateCore(
            bool withPublishedAssignmentSupport,
            string? dbPath,
            IInterceptor[]? interceptors = null,
            bool withRecorder = true,
            bool withOwnershipMutationHook = false,
            bool requestCompletionReceiptAck = false,
            bool registerThroughRealRpc = false)
        {
            var pool = new WorkerPool();
            var queue = new TaskQueue();
            var pipelineManager = new GoalPipelineManager();
            var dashboard = new DashboardNotifier();

            var goalManager = new GoalManager();
            var goalSource = new SequenceGoalSource();

            // The REAL dispatcher's cancellation persists the goal's status through its
            // GoalManager, so a minimal test goal source is registered BEFORE the dispatcher
            // subscribes — TEST-only persistence, not state the transport consults.
            goalManager.AddSource(goalSource);

            // ── ONE NOTIFIER FOR BOTH ────────────────────────────────────────────────────
            // The transport publishes here and the REAL dispatcher subscribes here, so the late
            // completion is genuinely handed to GoalDispatcher.HandleTaskCompletionAsync.
            var completionNotifier = new TaskCompletionNotifier();

            // THE OBSERVER SUBSCRIBES FIRST, ON PURPOSE. TaskCompletionNotifier.NotifyAsync awaits
            // only the LAST subscriber in the multicast chain, so the dispatcher — constructed
            // below — must be last: production then AWAITS the real downstream chain instead of
            // leaving it unobserved. This observer records the emitted TaskResult for the model
            // assertions; it is never used as evidence that the downstream guard ran.
            var observations = new CompletionObservations();
            completionNotifier.OnTaskCompleted += observations.Record;

            // ── THE COMPLETION-RECEIPT STORES: REAL, over a REAL SQLite database ─────────────
            // The recorder is a REAL WorkerCompletionRecorder over these two REAL stores, so the
            // durability evidence every accepting vector points at is production-written.
            var recordStores = dbPath is not null
                ? SequenceStores.FileBacked(dbPath, interceptors ?? [])
                : SequenceStores.InMemory();

            IWorkerAssignmentPublisher? assignmentPublisher = null;
            if (withPublishedAssignmentSupport)
            {
                assignmentPublisher = new WorkerAssignmentPublisher(
                    pipelineManager,
                    pool,
                    recordStores.AssignmentStore);
            }

            // ── ONE PUBLISHER, BOTH DELIVERY ROUTES ──────────────────────────────────────────
            // The REAL publisher is handed to the service (the READY route) AND to the eager
            // dispatcher's own gateway (the EAGER route). Wiring it into only one of the two would
            // let an ordering vector pass because eager publication was DISABLED rather than because
            // nothing was selected.
            var eagerGateway = new GrpcWorkerGateway(pool, assignmentPublisher);

            var dispatcherLogger = new SignallingLogger<GoalDispatcher>();
            var dispatcher = new GoalDispatcher(
                goalManager,
                pipelineManager,
                queue,
                new GrpcWorkerGateway(pool),
                completionNotifier,
                dispatcherLogger,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

            var serviceLogger = new SignallingLogger<HiveOrchestratorService>();

            // THE RECORDER THE SERVICE GETS: the REAL one, optionally wrapped in the
            // ownership-mutating decorator the post-record release vectors need.
            OwnershipMutatingRecorder? recorderHook = null;
            IWorkerCompletionRecorder? completionRecorder = null;
            if (withRecorder)
            {
                var realRecorder = new WorkerCompletionRecorder(
                    recordStores.AssignmentStore, recordStores.ReceiptStore);

                if (withOwnershipMutationHook)
                {
                    // THE BASELINE READER IS A FRESH STORE PER CALL, deliberately NOT the instance
                    // the recorder writes through: the captured first-stored instant must be a
                    // genuine durable read, not the writer's own view of its write.
                    recorderHook = new OwnershipMutatingRecorder(
                        realRecorder, taskId => recordStores.NewReceiptStore().Load(taskId));
                    completionRecorder = recorderHook;
                }
                else
                {
                    completionRecorder = realRecorder;
                }
            }

            var service = new HiveOrchestratorService(
                pool,
                queue,
                pipelineManager,
                completionNotifier,
                dispatcher,
                serviceLogger,
                dashboardNotifier: dashboard,
                assignmentPublisher: assignmentPublisher,
                completionRecorder: completionRecorder);

            // THE WORKER'S REGISTRATION. Two routes, and the difference is the point:
            //   * the LEGACY route builds the instance directly, with the request left absent. That is
            //     exactly what an existing worker does, and it is what keeps every vector that is not
            //     about negotiation on the unchanged legacy runtime.
            //   * the NEGOTIATED route goes through the REAL Register RPC, so the instance's
            //     enablement is PRODUCTION's answer to the request rather than a fixture decision.
            ConnectedWorker worker;
            if (registerThroughRealRpc)
            {
                var registration = service.Register(
                    new RegisterRequest
                    {
                        WorkerId = WorkerId,
                        RequestCompletionReceiptAck = requestCompletionReceiptAck,
                    },
                    MockContext()).GetAwaiter().GetResult();

                Assert.True(registration.Accepted);
                Assert.Equal(requestCompletionReceiptAck && withRecorder, registration.CompletionReceiptAckEnabled);

                worker = pool.GetWorker(WorkerId)
                    ?? throw new InvalidOperationException(
                        $"the registration RPC did not publish '{WorkerId}' in the pool.");
                Assert.Equal(
                    registration.CompletionReceiptAckEnabled, worker.CompletionReceiptAckEnabled);
            }
            else
            {
                worker = pool.RegisterWorker(
                    WorkerId, [], requestCompletionReceiptAck: requestCompletionReceiptAck);

                // A fixture-created instance is never ACK-enabled: enablement is the registration
                // RPC's decision, and nothing else may manufacture it.
                Assert.False(worker.CompletionReceiptAckEnabled);
            }

            var reader = new ChannelStreamReader();

            // THE SINGLE CONSUMER OF THE WORKER'S CHANNEL IS THE PRODUCTION PUMP. Publication is
            // observed at the writer the pump forwards to — no competing test reader.
            var writer = new SignallingStreamWriter();
            var streamTask = service.WorkStream(reader, writer, MockContext());

            // ── THE REAL EAGER DISPATCHER: SAME POOL, SAME QUEUE, OWN GATEWAY ─────────────────
            // Built exactly as production builds it (GoalDispatcher's own wiring shape), over the
            // very pool and queue this harness's transport uses. Its gateway is handed the SAME real
            // publisher, so an eager push can genuinely SELECT and PUBLISH here.
            var eagerGoalManager = new GoalManager();
            eagerGoalManager.AddSource(goalSource);

            var eagerDispatcher = new TaskDispatchService(
                queue,
                eagerGateway,
                new TaskBuilder(new BranchCoordinator()),
                ConfigForEagerDispatch,
                NullLogger<TaskDispatchService>.Instance,
                pipelineManager,
                new GoalLifecycleService(eagerGoalManager, NullLogger<GoalLifecycleService>.Instance),
                new DispatcherMaintenance(
                    pipelineManager,
                    eagerGoalManager,
                    queue,
                    eagerGateway,
                    brain: null,
                    agentsManager: null,
                    configRepo: null,
                    new ConcurrentQueue<string>(),
                    NullLogger<DispatcherMaintenance>.Instance,
                    config: ConfigForEagerDispatch));

            var harness = new Harness(observations)
            {
                Service = service,
                Pool = pool,
                Queue = queue,
                Worker = worker,
                ServiceLogger = serviceLogger,
                DispatcherLogger = dispatcherLogger,
                Dispatcher = dispatcher,
                Manager = pipelineManager,
                GoalSource = goalSource,
                Writer = writer,
                Reader = reader,
                StreamTask = streamTask,
                Stores = recordStores,
                RecorderHook = recorderHook,
                EagerDispatcher = eagerDispatcher,
                AssignmentPublisher = assignmentPublisher,
            };

            // THE DASHBOARD SIDE-EFFECT OBSERVATION: the REAL notifier's own state-changed event.
            // Every NotifyStateChanged the production paths raise increments this counter, so a
            // refusal vector asserting zero really sees a stray notification.
            dashboard.OnStateChanged += () => Interlocked.Increment(ref harness._dashboardNotifications);

            // THE RECEIPT PROBE the accepted vector reads AT the notification instant: the first
            // notifier subscriber loads the row through the REAL store BEFORE the awaiting test is
            // released, so an ordering regression is visible rather than merely eventually correct.
            observations.ReceiptProbe = taskId => recordStores.ReceiptStore.Load(taskId);

            // THE ADVANCE PROBE: a pipeline that advances dispatches its successor through the real
            // PipelineDriver, which enqueues here.
            queue.OnEnqueue = _ => Interlocked.Increment(ref harness._tasksEnqueued);

            return harness;
        }

        /// <summary>
        /// THE MINIMAL CONFIGURATION the REAL eager dispatch path needs: one repository and one
        /// configured coder model. It is configuration ONLY — the eager route's own refusal gate
        /// resolves the model from it, and nothing about the hold or the transport reads it.
        /// </summary>
        private static HiveConfigFile ConfigForEagerDispatch
        {
            get
            {
                var config = new HiveConfigFile();
                config.Repositories.Add(new RepositoryConfig
                {
                    Name = "ownership-repo",
                    Url = "https://example.com/ownership-repo.git",
                    DefaultBranch = "develop",
                });
                config.Workers["coder"] = new WorkerConfig { Model = "eager-model" };
                return config;
            }
        }

        /// <summary>
        /// THE REAL COMPLETION-RECEIPT STORES one harness runs against: the PRODUCTION insert-once
        /// assignment-context store and the PRODUCTION insert-once completion-receipt store, both
        /// over a REAL SQLite database (a file when the vector needs disk durability or an injected
        /// interceptor, an in-memory database with a live anchor connection otherwise).
        /// </summary>
        /// <remarks>
        /// TEST INFRASTRUCTURE ONLY: it supplies the two stores the production recorder is built
        /// from and nothing the production paths consult beyond them.
        /// </remarks>
        public sealed class SequenceStores : IDisposable
        {
            private readonly SqliteConnection? _anchor;

            private SequenceStores(
                IDbContextFactory<CopilotHiveDbContext> factory,
                SqliteConnection? anchor)
            {
                Factory = factory;
                _anchor = anchor;
                AssignmentStore = new WorkerAssignmentContextStore(
                    factory, NullLogger<WorkerAssignmentContextStore>.Instance);
                ReceiptStore = new CompletionReceiptStore(
                    factory, NullLogger<CompletionReceiptStore>.Instance);
            }

            public IDbContextFactory<CopilotHiveDbContext> Factory { get; }

            public WorkerAssignmentContextStore AssignmentStore { get; }

            public CompletionReceiptStore ReceiptStore { get; }

            /// <summary>
            /// A NEWLY CONSTRUCTED <see cref="CompletionReceiptStore"/> over the same database —
            /// never the instance the production recorder was built from.
            /// </summary>
            /// <remarks>
            /// THE FRESHNESS IS THE POINT. The store owns one short-lived context per operation, so
            /// a load through a NEW instance re-opens the database and re-decodes the row rather
            /// than reusing anything the writing instance may hold. That is what makes a readback
            /// genuine DURABILITY evidence instead of an echo of the writer.
            /// </remarks>
            /// <returns>A fresh store instance; the caller simply lets it go out of scope.</returns>
            public CompletionReceiptStore NewReceiptStore() =>
                new(Factory, NullLogger<CompletionReceiptStore>.Instance);

            /// <summary>An in-memory database kept alive by an anchor connection for the test's lifetime.</summary>
            public static SequenceStores InMemory()
            {
                var connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();

                var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                    .UseSqlite(connection)
                    .Options;

                using (var bootstrap = new CopilotHiveDbContext(options))
                    bootstrap.Database.EnsureCreated();

                return new SequenceStores(new SharedDbContextFactory(connection, options), connection);
            }

            /// <summary>A per-vector temporary FILE, optionally carrying the supplied interceptors.</summary>
            public static SequenceStores FileBacked(string dbPath, IInterceptor[] interceptors)
            {
                var factory = new SequenceDbContextFactory(dbPath, interceptors);
                using (var bootstrap = factory.CreateDbContext())
                    bootstrap.Database.EnsureCreated();

                return new SequenceStores(factory, anchor: null);
            }

            public void Dispose() => _anchor?.Dispose();

            /// <summary>
            /// Runs raw SQL through a factory-owned connection — the CORRUPT-ROW setup, which needs
            /// to write a payload the store's own codec would never produce. It issues the statement
            /// DIRECTLY, never through a raw-SQL helper that would treat the text as a format string
            /// (the seeded payload deliberately contains JSON braces).
            /// </summary>
            /// <param name="sql">The statement to execute.</param>
            public void ExecuteRaw(string sql)
            {
                using var context = Factory.CreateDbContext();
                var connection = context.Database.GetDbConnection();
                var wasClosed = connection.State != System.Data.ConnectionState.Open;
                if (wasClosed)
                    connection.Open();

                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
                finally
                {
                    if (wasClosed)
                        connection.Close();
                }
            }
        }

        /// <summary>
        /// An <see cref="IDbContextFactory{CopilotHiveDbContext}"/> handing out contexts on the
        /// sequence vector's own file-backed SQLite database, with the optional injected
        /// interceptors. TEST INFRASTRUCTURE ONLY — it does not touch any state the production paths
        /// consult.
        /// </summary>
        private sealed class SequenceDbContextFactory(string dbPath, IInterceptor[] interceptors)
            : IDbContextFactory<CopilotHiveDbContext>
        {
            public CopilotHiveDbContext CreateDbContext()
            {
                var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                    .UseSqlite($"Data Source={dbPath};Pooling=False");
                if (interceptors.Length > 0)
                    builder.AddInterceptors(interceptors);

                return new CopilotHiveDbContext(builder.Options);
            }
        }

        /// <summary>
        /// A minimal in-memory <see cref="IGoalStore"/> for the sequence vector: goals register
        /// themselves on demand and status updates are recorded, so the REAL
        /// <see cref="GoalDispatcher.CancelGoalAsync"/> path completes without a real store.
        /// </summary>
        public sealed class SequenceGoalSource : IGoalStore
        {
            private readonly Dictionary<string, Goal> _goals = [];

            public string Name => "sequence-test-source";

            /// <summary>Registers a goal so the source can resolve and update it.</summary>
            public void Register(Goal goal) { lock (_goals) _goals[goal.Id] = goal; }

            public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<Goal>>([]);

            public Task UpdateGoalStatusAsync(
                string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null,
                CancellationToken ct = default)
            {
                lock (_goals)
                {
                    if (_goals.TryGetValue(goalId, out var goal))
                    {
                        goal.Status = status;
                        goal.FailureReason = metadata?.FailureReason;
                    }
                }

                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
            {
                lock (_goals) return Task.FromResult<IReadOnlyList<Goal>>([.. _goals.Values]);
            }

            public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
            {
                lock (_goals) return Task.FromResult(
                    _goals.TryGetValue(goalId, out var goal) ? goal : null);
            }

            public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
            {
                lock (_goals) _goals[goal.Id] = goal;
                return Task.FromResult(goal);
            }

            public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) => Task.CompletedTask;

            public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
            {
                lock (_goals) return Task.FromResult(_goals.Remove(goalId));
            }

            public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
                string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<Goal>>([]);

            public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(
                GoalStatus status, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<Goal>>([]);

            public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<IterationSummary>>([]);

            public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
                Task.FromResult(release);

            public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
                Task.FromResult<Release?>(null);

            public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<Release>>([]);

            public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

            public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
                Task.FromResult(false);

            public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<Goal>>([]);

            public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

            public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

            public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
                int? limit = null, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
        }

        /// <summary>Builds a task carrying this harness's shape.</summary>
        public WorkTask BuildTask(string taskId, string model) => new()
        {
            TaskId = taskId,
            GoalId = "goal-ownership",
            GoalDescription = "bounded ownership goal",
            Prompt = "do the work",
            Role = DomainWorkerRole.Coder,
            Model = model,
            Repositories = [],
        };

        /// <summary>
        /// Gives the worker GENUINE active ownership of the task through the PRODUCTION assignment
        /// path: the queue entry is activated for this worker and the pool is marked busy.
        /// </summary>
        /// <remarks>
        /// THE ASSIGNMENT CONTEXT IS REALLY RECORDED TOO, through the PRODUCTION insert-once store
        /// over the harness's REAL database. That is the valid-assignment setup the completion
        /// recorder's STORED-context agreement rule requires: without a recorded context the
        /// recorder refuses and the completion is retained, which is exactly what these ownership
        /// vectors are not about.
        /// </remarks>
        public void Assign(string taskId, string model)
        {
            GrantReadiness(Worker);

            var task = BuildTask(taskId, model);
            Queue.Enqueue(task);
            var dequeued = Queue.TryDequeue(DomainWorkerRole.Unspecified);
            Assert.NotNull(dequeued);
            Service.ApplyTaskAssignment(Worker, dequeued!);

            RecordAssignmentContext(taskId, task.GoalId, task.Role);

            Assert.True(Worker.IsBusy);
            Assert.Equal(taskId, Worker.CurrentTaskId);
            Assert.Equal(model, Worker.CurrentModel);
            Assert.NotNull(Queue.GetActiveTask(taskId));
        }

        /// <summary>
        /// THE READINESS the production delivery boundary requires before an instance may be
        /// (re-)assigned: an ACK-enabled instance whose negotiated completion was released stays
        /// unselectable — even once its short publication hold has ended — until its own accepted
        /// Ready arrives, and the checked claim refuses it until then.
        /// </summary>
        /// <remarks>
        /// IT IS THE HARNESS'S OWN READY, driven through the REAL checked idle
        /// (<c>TryMarkIdleForReady</c>) exactly as the production <c>HandleWorkerReady</c> drives it,
        /// so a harness assignment after a released negotiated completion takes the same route the
        /// worker's next Ready would. An instance that is not waiting is left untouched, so this
        /// cannot mask the wait: the vectors that assert the wait itself keep asserting it directly.
        /// </remarks>
        /// <param name="worker">The instance whose readiness is being established.</param>
        /// <returns>The worker's own Ready outcome.</returns>
        public bool GrantReadiness(ConnectedWorker worker) =>
            Pool.TryGetWorkerSnapshot(worker.Id, out var observed)
            && ReferenceEquals(observed.Worker, worker)
            && Pool.TryMarkIdleForReady(observed, queueEntryAbsent: true);

        /// <summary>
        /// WHAT A FINISHED NEGOTIATED PUBLICATION LEAVES BEHIND on an ACK-enabled instance: the SHORT
        /// completion-publication hold is OVER, and the instance is STILL NOT SELECTABLE — not because
        /// it is publishing anything, but because the release of its negotiated completion put it into
        /// the instance-local wait for its OWN accepted Ready.
        /// </summary>
        /// <remarks>
        /// THE TWO FACTS ARE DELIBERATELY SEPARATED HERE. Asserting only "no hold" would let a mutant
        /// that clears the hold silently re-open the eager-selection window; asserting only
        /// "unselectable" would not say which fact withholds the instance. This pair — hold gone,
        /// still awaiting Ready, and <see cref="WorkerPool.GetIdleWorker"/> returning nothing — is what
        /// distinguishes the end of the publication from the end of the wait.
        /// </remarks>
        public void AssertAwaitingItsOwnReadyAfterThePublication()
        {
            Assert.False(
                Worker.CompletionPublicationPending,
                "the short completion-publication hold outlived the publication that installed it");
            Assert.True(
                Worker.AwaitingWorkerReady,
                "the released negotiated completion did not put the instance into the wait for its "
                + "own accepted Ready");
            Assert.False(Worker.IsBusy);
            Assert.Null(Worker.CurrentTaskId);
            Assert.Null(
                Pool.GetIdleWorker());
        }

        /// <summary>
        /// THE OTHER HALF OF THE WAIT'S CONTRACT, ASSERTED THE SAME WAY: the instance's own accepted
        /// Ready ends the wait, and ONLY then is the instance selectable again.
        /// </summary>
        /// <returns>A task-free observation of the post-Ready state.</returns>
        public void GrantReadinessAndAssertSelectable()
        {
            Assert.True(GrantReadiness(Worker), "the instance's own Ready was refused");
            Assert.False(Worker.AwaitingWorkerReady);
            Assert.Same(Worker, Pool.GetIdleWorker());
        }

        /// <summary>
        /// Seeds the STORED assignment context for a task through the harness's REAL insert-once
        /// store, using the phase that maps to <paramref name="role"/> — the ONLY way a context for
        /// that role can exist, because the context's own constructor enforces the mapping.
        /// </summary>
        /// <param name="taskId">The opaque task id the context is recorded for.</param>
        /// <param name="goalId">The goal the recorded binding names.</param>
        /// <param name="role">The dispatched role; its phase's mapped role must equal it.</param>
        /// <param name="workerId">The recorded worker, when a vector needs a disagreement.</param>
        /// <param name="position">The recorded position; a repeated position is a valid choice.</param>
        /// <param name="attempt">The recorded attempt.</param>
        /// <param name="model">The recorded assignment model, preserved verbatim.</param>
        /// <returns>The recorded context, for assertions.</returns>
        public WorkerAssignmentContext RecordAssignmentContext(
            string taskId,
            string goalId,
            DomainWorkerRole role,
            string? workerId = null,
            WorkSlotPosition? position = null,
            int attempt = 1,
            string model = "assigned-model")
        {
            var phase = PhaseMappedTo(role);
            var context = new WorkerAssignmentContext(
                goalId,
                workerId ?? WorkerId,
                role,
                new WorkSlot(taskId, position ?? new WorkSlotPosition(1, phase, 1), attempt),
                model);

            var write = Stores.AssignmentStore.InsertOnce(context);
            Assert.Equal(WorkerAssignmentWriteStatus.Recorded, write.Status);
            return context;
        }

        /// <summary>
        /// The ONE worker-backed phase whose existing mapping produces <paramref name="role"/>. An
        /// unmappable role is a fixture bug and throws rather than guessing a phase.
        /// </summary>
        private static GoalPhase PhaseMappedTo(DomainWorkerRole role) => role switch
        {
            DomainWorkerRole.Coder => GoalPhase.Coding,
            DomainWorkerRole.Tester => GoalPhase.Testing,
            DomainWorkerRole.Reviewer => GoalPhase.Review,
            DomainWorkerRole.DocWriter => GoalPhase.DocWriting,
            DomainWorkerRole.Improver => GoalPhase.Improve,
            _ => throw new InvalidOperationException(
                $"Worker role '{role}' has no worker-backed phase mapping."),
        };

        /// <summary>
        /// Records a DIFFERENT, well-formed receipt for the task through the harness's REAL
        /// insert-once receipt store — the genuine Conflict setup: the completed invocation then
        /// finds a row whose canonical evidence is not its own.
        /// </summary>
        /// <param name="taskId">The task id the foreign receipt is retained for.</param>
        /// <param name="output">A distinctly different output, so the canonical texts differ.</param>
        /// <returns>The retained receipt's first-stored instant, for the unchanged-row assertion.</returns>
        public DateTime RecordForeignReceipt(string taskId, string output)
        {
            var context = Stores.AssignmentStore.Load(taskId)
                ?? throw new InvalidOperationException(
                    $"no recorded assignment context for task '{taskId}'");

            var receipt = new CompletionReceipt(
                context.Context.GoalId,
                context.Context.WorkerId,
                context.Context.Role,
                context.Context.Slot,
                new TaskResult
                {
                    TaskId = taskId,
                    Status = TaskOutcome.Completed,
                    Output = output,
                    Model = "foreign-model",
                });

            Assert.Equal(CompletionReceiptWriteStatus.Stored, Stores.ReceiptStore.InsertOnce(receipt).Status);
            return Stores.ReceiptStore.Load(taskId)!.FirstStoredAtUtc;
        }

        /// <summary>
        /// The retained receipt for a task, read through a store instance constructed FRESH for
        /// this call — the durability evidence.
        /// </summary>
        /// <remarks>
        /// IT IS GENUINELY FRESH, and the name is honest. Earlier this helper reused the very store
        /// instance the production recorder writes through, so its "fresh" claim was not true and a
        /// readback could not distinguish durable state from the writer's own view. Every call now
        /// builds a NEW <see cref="CompletionReceiptStore"/> over the same database, which opens its
        /// own short-lived context and re-decodes the row.
        /// </remarks>
        /// <param name="taskId">The task id whose receipt to load.</param>
        /// <returns>The decoded receipt and its first-stored instant, or <c>null</c> when absent.</returns>
        public CompletionReceiptReadResult? ReadReceipt(string taskId) =>
            Stores.NewReceiptStore().Load(taskId);

        /// <summary>
        /// Sends a heartbeat for the pinned worker through the REAL RPC, so the production throttle
        /// dictionary gains an entry for it.
        /// </summary>
        public Task SendHeartbeatAsync() =>
            Service.Heartbeat(
                new HeartbeatRequest { WorkerId = WorkerId, Busy = false, ContextUsagePercent = 10 },
                MockContext());

        /// <summary>
        /// The worker ids currently present in the service's OWN <c>_heartbeatState</c> dictionary —
        /// read via reflection because the dictionary is the production throttle authority the
        /// loser's teardown must not touch.
        /// </summary>
        /// <returns>A snapshot of the dictionary's keys.</returns>
        public IReadOnlyList<string> HeartbeatStateKeys() =>
            HeartbeatState().Keys.ToList();

        private IDictionary<string, (DateTime LastNotify, bool WasBusy, int LastNotifiedCtx)> HeartbeatState()
        {
            var field = typeof(HiveOrchestratorService).GetField(
                "_heartbeatState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(field);
            var dict = field!.GetValue(Service)
                as IDictionary<string, (DateTime LastNotify, bool WasBusy, int LastNotifiedCtx)>;
            Assert.NotNull(dict);
            return dict!;
        }

        /// <summary>A raw column read through the harness's own factory — the byte-identity probe.</summary>
        /// <param name="sql">The scalar query to execute.</param>
        /// <returns>The scalar, or <c>null</c> for SQL NULL.</returns>
        public object? RawScalar(string sql)
        {
            using var context = Stores.Factory.CreateDbContext();
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;

            var wasClosed = command.Connection!.State != System.Data.ConnectionState.Open;
            if (wasClosed)
                command.Connection.Open();

            try
            {
                var value = command.ExecuteScalar();
                return value is DBNull ? null : value;
            }
            finally
            {
                if (wasClosed)
                    command.Connection.Close();
            }
        }

        private static GrpcTaskComplete BuildComplete(
            string taskId,
            string? model,
            bool modelPresent,
            CopilotHive.Shared.Grpc.TaskStatus status)
        {
            var complete = new GrpcTaskComplete
            {
                TaskId = taskId,
                Status = status,
                Output = $"output-{taskId}",
            };
            if (modelPresent)
                complete.Model = model ?? "";

            Assert.Equal(modelPresent, complete.HasModel);
            return complete;
        }

        // ── THE POST-HANDLER BARRIER ─────────────────────────────────────────────────────

        /// <summary>
        /// PROVES THE PUBLICATION OBSERVATION POINT IS LIVE, so a later "nothing was forwarded"
        /// assertion is a real absence rather than a silent/dead pump.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A message written straight to the worker's own channel must appear AT THE WRITER. That is
        /// the same production pump path every acknowledgement would have to travel, so once this
        /// returns, an observed absence genuinely means nothing was published. The probe message
        /// carries no payload case of its own beyond <c>None</c>, so it can neither satisfy nor be
        /// mistaken for a real acknowledgment.
        /// </para>
        /// <para>
        /// A POST-HANDLER BARRIER RUNS FIRST, ON PURPOSE. The production pump only forwards once the
        /// stream has pinned the worker on its first inbound message, and the barrier's Progress
        /// message is exactly that first inbound message — so the probe below cannot be mistaken for
        /// a dead pump merely because nothing was pushed to the reader yet.
        /// </para>
        /// </remarks>
        public async Task AssertPumpObservationIsLiveAsync()
        {
            await BarrierAsync();

            var token = $"ownership-pump-live-{Interlocked.Increment(ref _barrierSequence)}";
            var forwarded = Writer.WaitForMessage(m => m.UpdateAgents?.Role == token);

            Assert.True(Worker.MessageChannel.Writer.TryWrite(new OrchestratorMessage
            {
                UpdateAgents = new UpdateAgents { Role = token },
            }));

            var observed = await forwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            Assert.Equal(token, observed.UpdateAgents.Role);
        }

        /// <summary>
        /// THE POST-HANDLER BARRIER, and the reason every refusal vector here is removal-proof.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A refusal warning is emitted BEFORE the guard's early return, so waiting on the warning
        /// alone proves nothing about what the handler did next: delete the return and the handler
        /// runs on and throws, which would unwind the read loop while the asserted state still
        /// looked untouched.
        /// </para>
        /// <para>
        /// THE BARRIER CLOSES THAT HOLE USING THE REAL LOOP. <c>WorkStream</c> reads its request
        /// stream STRICTLY SEQUENTIALLY and <c>HandleTaskComplete</c>/<c>HandleWorkerReady</c> are
        /// awaited inline, so the NEXT message can only be processed after the previous handler
        /// RETURNED. This pushes a Progress message carrying a UNIQUE token and waits for
        /// <c>HandleTaskProgress</c>'s own production log line. A handler that threw never lets the
        /// loop reach this message, so the wait expires and the vector FAILS.
        /// </para>
        /// </remarks>
        public async Task BarrierAsync()
        {
            var token = $"ownership-barrier-{Interlocked.Increment(ref _barrierSequence)}";
            var signal = ServiceLogger.WaitFor(token);

            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Progress = new TaskProgress
                {
                    TaskId = "barrier",
                    Status = CopilotHive.Shared.Grpc.TaskStatus.InProgress,
                    Message = token,
                },
            });

            try
            {
                await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"POST-HANDLER BARRIER '{token}' was never reached: the WorkStream read loop " +
                    "did not process the following message, which means the handler under test did " +
                    "NOT return normally (streamCompleted=" + StreamTask.IsCompleted + ").",
                    ex);
            }
        }

        /// <summary>
        /// Delivers a completion, awaits the REAL downstream <c>TaskCompletionService</c> handling
        /// for that task id, and returns the domain result the transport emitted.
        /// </summary>
        /// <remarks>
        /// THE DOWNSTREAM SIGNAL IS THE REAL ONE: production's <c>NotifyAsync</c> awaits the
        /// dispatcher (the last subscriber), and this waits for a production
        /// <c>TaskCompletionService</c> log line naming the task — so a returned call proves the
        /// real domain chain actually ran, not merely that a test handler fired.
        /// </remarks>
        public async Task<TaskResult> CompleteAndAwaitDownstreamAsync(
            string taskId, string? model = null, bool modelPresent = false)
        {
            var recorded = _observations.NextResult();
            var downstream = DispatcherLogger.WaitFor(taskId);

            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, model, modelPresent, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });

            var result = await recorded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await downstream.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();
            return result;
        }

        /// <summary>
        /// Delivers a completion that an ENABLED registration must carry a model for — the field is
        /// PRESENT with <paramref name="model"/> verbatim, so presence is satisfied even when the
        /// value itself is empty or whitespace.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="model">The model value to carry, preserved verbatim.</param>
        /// <returns>The domain result the transport emitted.</returns>
        public Task<TaskResult> CompleteWithPresentModelAndAwaitDownstreamAsync(
            string taskId, string model) =>
            CompleteAndAwaitDownstreamAsync(taskId, model, modelPresent: true);

        /// <summary>
        /// The MODEL PRESENCE this harness's registration requires: an ENABLED registration must
        /// carry the field, a DISABLED (legacy) one keeps the original absent-field behaviour.
        /// </summary>
        /// <returns><c>true</c> when the fixture must send an explicit model value.</returns>
        private bool RequiresModelPresence => Worker.CompletionReceiptAckEnabled;

        /// <summary>
        /// Delivers an ORDINARY accepted completion on this harness's own registration shape,
        /// awaiting the REAL downstream chain, and returns the domain result.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="model">The model value to send when presence is required.</param>
        /// <returns>The domain result the transport emitted.</returns>
        public Task<TaskResult> CompleteOrdinaryAsync(string taskId, string model = "assigned-model") =>
            CompleteAndAwaitDownstreamAsync(
                taskId,
                model: RequiresModelPresence ? model : null,
                modelPresent: RequiresModelPresence);

        // ── THE ACKNOWLEDGEMENT OBSERVATION ──────────────────────────────────────────

        /// <summary>
        /// Waits for the NEXT acknowledgement the REAL pump forwards to the gRPC writer and returns
        /// it, so the identity is read where the worker would have received it.
        /// </summary>
        /// <remarks>
        /// THE OBSERVATION POINT IS THE WRITER, not the channel: the production pump is the channel's
        /// only consumer, so reading the writer observes the publication AFTER the real pump forwarded
        /// it rather than competing with it.
        /// </remarks>
        /// <returns>The forwarded acknowledgement.</returns>
        public async Task<CompletionReceiptAck> AwaitAcknowledgementAsync()
        {
            var forwarded = Writer.WaitForMessage(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck);

            return (await forwarded.WaitAsync(
                BoundedWait, TestContext.Current.CancellationToken)).CompletionReceiptAck;
        }

        /// <summary>
        /// PROVES THE PUMP OBSERVATION IS LIVE and then asserts that NO acknowledgement was forwarded
        /// for the worker, so the absence is a real refusal rather than a dead or silent pump.
        /// </summary>
        /// <remarks>
        /// THE ORDER IS THE POINT: the handler barrier inside
        /// <see cref="AssertPumpObservationIsLiveAsync"/> runs first, so the handler under test has
        /// provably RETURNED before the absence is asserted — a publication that merely had not been
        /// scheduled yet cannot masquerade as a refusal.
        /// </remarks>
        /// <returns>A task that completes once liveness and absence are both established.</returns>
        public async Task AssertNoAcknowledgementAsync()
        {
            await AssertPumpObservationIsLiveAsync();
            AssertNoAcknowledgementPublished(this);
        }

        /// <summary>
        /// WAITS FOR THE RECORDED COMPLETION TO HAVE BEEN RELEASED AND NOTIFIED, then asserts the
        /// stream carried no acknowledgement for it.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <returns>The domain result the transport emitted.</returns>
        public async Task<TaskResult> CompleteExpectingNoAcknowledgementAsync(string taskId)
        {
            var result = await CompleteAndAwaitDownstreamAsync(taskId, modelPresent: false);
            await AssertNoAcknowledgementAsync();
            return result;
        }

        /// <summary>
        /// THE LIVE PROOF THAT THIS STREAM'S OWN LATEST ELIGIBILITY STILL NAMES
        /// <paramref name="taskId"/>: an identical duplicate is delivered through the REAL read loop
        /// and must be RE-ACKNOWLEDGED, which ONLY the stream's retained latest id can authorize.
        /// </summary>
        /// <remarks>
        /// IT IS A BEHAVIOURAL PROBE, NOT A STATE READ. The eligibility holder is a local of ONE
        /// <c>WorkStream</c> invocation and is unreachable from a test by construction, so the
        /// property is asserted by exercising the protocol it authorizes. A regression that never
        /// created, or that lost, the eligibility sends this delivery down the ORDINARY path and the
        /// wait for the success line expires as a named failure.
        /// </remarks>
        /// <param name="taskId">The task the stream's latest eligibility must still name.</param>
        /// <returns>A task that completes once the re-acknowledgement was observed at the writer.</returns>
        public async Task AssertLatestEligibleStillReAcknowledgedAsync(string taskId)
        {
            var acknowledged = ServiceLogger.WaitFor(ProductionLogFragments.DuplicateReAcknowledged);
            var forwarded = Writer.WaitForMessage(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck
                     && string.Equals(
                         m.CompletionReceiptAck.TaskId, taskId, StringComparison.Ordinal));

            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, "assigned-model", true, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });

            await acknowledged.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            var message = await forwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerId, message.CompletionReceiptAck.WorkerId);

            await BarrierAsync();
        }

        /// <summary>
        /// THE LIVE PROOF THAT THIS STREAM HAS NO ELIGIBILITY FOR <paramref name="taskId"/>: an
        /// identical duplicate is delivered through the REAL read loop and must fall to the ORDINARY
        /// ownership validation, which refuses it with NO confirmation read and NO acknowledgement.
        /// </summary>
        /// <remarks>
        /// THE ORDINARY REFUSAL IS THE DISCRIMINATOR, and the pump-liveness assertion that follows it
        /// is what makes the absence of an acknowledgement a real refusal rather than a stalled pump.
        /// </remarks>
        /// <param name="taskId">The task no eligibility may exist for.</param>
        /// <param name="expectedReason">The ordinary guard the refusal diagnostic must name.</param>
        /// <param name="expectedAcknowledgementsForTask">
        /// The cumulative acknowledgement count this task must STILL report after the probe — the
        /// ledger is cumulative, so an earlier accepted completion's own acknowledgement is stated
        /// here rather than assumed away.
        /// </param>
        /// <returns>A task that completes once the refusal and the absence were both established.</returns>
        public async Task AssertNoLatestEligibilityForAsync(
            string taskId, string expectedReason, int expectedAcknowledgementsForTask = 0)
        {
            // THE BASELINE IS TAKEN BEFORE THE DELIVERY, because the logger's history is CUMULATIVE:
            // a vector that legitimately proved this stream's eligibility for the task EARLIER would
            // make a bare "was it ever emitted" assertion permanently false, even though the delivery
            // under test emitted nothing. Comparing counts keeps the claim about THIS delivery.
            var reAckedBefore = DiagnosticCount(ProductionLogFragments.DuplicateReAcknowledged, taskId);

            await CompleteAndAwaitIgnoredWithPresentModelAsync(taskId, expectedReason);

            Assert.Equal(
                reAckedBefore,
                DiagnosticCount(ProductionLogFragments.DuplicateReAcknowledged, taskId));

            // THE PUMP IS PROVEN LIVE FIRST, so the count below is a real refusal rather than an
            // acknowledgement that merely had not been forwarded yet.
            await AssertPumpObservationIsLiveAsync();
            Assert.Equal(expectedAcknowledgementsForTask, AcknowledgedCountFor(taskId));
        }

        /// <summary>
        /// SEEDS A REAL PRIOR ELIGIBILITY on THIS live stream, through the ordinary protocol path:
        /// the task is assigned, completed and acknowledged, and its re-acknowledgement is then
        /// demonstrated — so a later "the previous eligibility survived" claim starts from a fact
        /// this stream genuinely established rather than from an assumption.
        /// </summary>
        /// <remarks>
        /// IT IS DELIBERATELY A DIFFERENT TASK from the failure under test. The whole point of the
        /// vectors that use it is that an ordinary-completion FAILURE for one id must not displace
        /// the eligibility a DIFFERENT, earlier, genuinely successful completion created.
        /// </remarks>
        /// <param name="taskId">The prior task's identifier; distinct from the failing one.</param>
        /// <returns>A task that completes once the prior eligibility is established and proven.</returns>
        public async Task SeedPriorEligibleCompletionAsync(string taskId)
        {
            Assign(taskId, model: "assigned-model");

            var acknowledgement = AwaitAcknowledgementAsync();
            await CompleteWithPresentModelAndAwaitDownstreamAsync(taskId, "assigned-model");

            var ack = await acknowledgement;
            Assert.Equal(taskId, ack.TaskId);

            // THE PRIOR ELIGIBILITY IS REAL AND CURRENTLY ACTIVE: its duplicate re-acknowledges now.
            await AssertLatestEligibleStillReAcknowledgedAsync(taskId);
            Assert.Equal(2, AcknowledgedCountFor(taskId));

            // The task is released and gone from the queue, so it stays probe-able later.
            Assert.False(Worker.IsBusy);
            Assert.Null(Queue.GetActiveTask(taskId));
        }

        /// <summary>
        /// DETERMINISTICALLY REMOVES THE FAILED COMPLETION'S REMAINING HOLD — the pool's busy flag
        /// and the queue's active entry — so the duplicate classifier's HELD-task rejection can no
        /// longer mask a wrongly created eligibility.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WHY THIS IS REQUIRED FOR AN HONEST PROOF. <c>IsLatestEligibleDuplicate</c> refuses ANY task
        /// that is still the worker's current task or still has an active queue entry, INDEPENDENTLY
        /// of the stream's eligibility. So while the failed task remains held, "no acknowledgement was
        /// published" is guaranteed by the held gate alone and says nothing whatsoever about whether
        /// eligibility was created. Removing the hold first is what makes the following probes
        /// discriminating.
        /// </para>
        /// <para>
        /// IT IS TEST-OWNED STATE, NOT A COMPLETION. It goes straight to the pool and the queue rather
        /// than through a completion delivery, precisely so it cannot itself create or advance any
        /// eligibility — production advances the holder only on an accepted ordinary completion.
        /// </para>
        /// </remarks>
        /// <param name="taskId">The failed task whose hold is being cleared.</param>
        public void ReleaseFailedHold(string taskId)
        {
            Pool.MarkIdle(WorkerId);
            Queue.MarkComplete(taskId);

            Assert.False(Worker.IsBusy);
            Assert.Null(Queue.GetActiveTask(taskId));
        }

        /// <summary>
        /// THE EARLY-ADVANCE DETECTOR for a FAILED ordinary completion, run only AFTER its hold has
        /// been removed: the failed id must get ZERO confirmation reads and ZERO acknowledgements, so
        /// a mutant that advanced the eligibility BEFORE the failure fails here rather than passing
        /// silently behind the held-task rejection.
        /// </summary>
        /// <remarks>
        /// THE CONFIRMATION COUNT IS THE SHARP EDGE. Only the read-only duplicate branch performs a
        /// confirmation at all, and it is reachable only when the stream's own holder names this id.
        /// For the post-Record case the failed id even has a DURABLE receipt, so a wrongly advanced
        /// eligibility would confirm TRUE and publish a real acknowledgement — which this refuses.
        /// </remarks>
        /// <param name="taskId">The failed task no eligibility may name.</param>
        /// <returns>A task that completes once the absence is established through the live stream.</returns>
        public async Task AssertFailedCompletionCreatedNoEligibilityAsync(string taskId)
        {
            var confirmationsBefore = RecorderHook?.ConfirmCount
                ?? throw new InvalidOperationException(
                    "this probe needs the confirmation-counting recorder decorator; use a harness "
                    + "factory that installs it.");

            // THE DELIVERY FALLS TO THE ORDINARY VALIDATION and is refused there — which is only
            // where a delivery with NO eligibility can go.
            await AssertNoLatestEligibilityForAsync(
                taskId, HiveOrchestratorService.OwnershipRefusalReasons.WorkerNotBusyWithTask);

            // …AND THE READ-ONLY BRANCH WAS NEVER ENTERED: not one confirmation was attempted.
            Assert.Equal(confirmationsBefore, RecorderHook.ConfirmCount);
        }

        /// <summary>
        /// How many logged messages carry <paramref name="fragment"/> AND name
        /// <paramref name="taskId"/> so far — a cumulative count, used with a pre-delivery baseline.
        /// </summary>
        /// <param name="fragment">The production fragment to count.</param>
        /// <param name="taskId">The task the message must name.</param>
        /// <returns>The number of matching messages logged so far.</returns>
        public int DiagnosticCount(string fragment, string taskId) =>
            ServiceLogger.Messages.Count(
                m => m.Contains(fragment, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));

        /// <summary>How many acknowledgements the pump forwarded for one exact opaque task id.</summary>
        /// <param name="taskId">The task id the acknowledgement must name.</param>
        /// <returns>The count of forwarded acknowledgements for that id.</returns>
        public int AcknowledgedCountFor(string taskId) =>
            Writer.Messages.Count(
                m => m.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck
                     && string.Equals(
                         m.CompletionReceiptAck.TaskId, taskId, StringComparison.Ordinal));

        /// <summary>
        /// The ORDINARY-refusal delivery with an EXPLICITLY PRESENT model, so an ENABLED
        /// registration's model requirement cannot be what refused it.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="expectedReason">The refusing guard's reason text.</param>
        /// <returns>A task that completes once the refusal was observed and the handler returned.</returns>
        public async Task CompleteAndAwaitIgnoredWithPresentModelAsync(
            string taskId, string expectedReason)
        {
            var signal = ServiceLogger.WaitFor(expectedReason);
            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, "assigned-model", true, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(expectedReason, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Delivers an identical duplicate whose ACKNOWLEDGEMENT CANNOT BE QUEUED (the channel is
        /// already completed), awaiting the production lost-enqueue diagnostic for that exact task
        /// and then the post-handler barrier.
        /// </summary>
        /// <remarks>
        /// REACHING THAT DIAGNOSTIC IS ITSELF THE EVIDENCE: only the read-only duplicate branch —
        /// which requires the stream's own latest eligibility to name this task and a true
        /// confirmation — attempts the enqueue at all on a delivery for a task the worker no longer
        /// holds. No success line can ever arrive for a refused enqueue, so waiting for one would
        /// hang; the barrier is the terminal observation instead.
        /// </remarks>
        /// <param name="taskId">The duplicated task's identifier.</param>
        /// <returns>A task that completes once the lost enqueue was observed and the handler returned.</returns>
        public async Task CompleteAndAwaitAcknowledgementNotQueuedAsync(string taskId)
        {
            var notQueued = ServiceLogger.WaitFor(ProductionLogFragments.ReceiptAckNotQueued);

            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, "assigned-model", true, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });

            await notQueued.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.ReceiptAckNotQueued, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal)
                     && m.Contains(WorkerId, StringComparison.Ordinal));
        }

        /// <summary>Arms a ONE-SHOT fault on the next acknowledgement forwarded to this writer.</summary>
        /// <param name="failure">The exception the write must throw exactly once.</param>
        public void ArmOneShotAcknowledgementWriteFault(Exception failure) =>
            Writer.ArmOneShotAcknowledgementWriteFault(failure);

        /// <summary>How many acknowledgement writes the writer was asked to perform.</summary>
        public int AcknowledgementWriteAttempts => Writer.AcknowledgementWriteAttempts;

        /// <summary>
        /// Delivers a completion and awaits the PRODUCTION IGNORED warning carrying
        /// <paramref name="expectedReason"/>, THEN the post-handler barrier.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WAITING ON THE REASON, not merely on "a refusal", is what keeps these vectors
        /// discriminating: the checked release at the end of the handler would otherwise refuse a
        /// mis-validated delivery too, and a generic wait would be satisfied by that late refusal
        /// even though the early validation had been removed. The barrier then proves the handler
        /// RETURNED after emitting it.
        /// </para>
        /// <para>
        /// THE STOP IS PROVEN BY A DOWNSTREAM-OF-THE-GUARD OBSERVABLE, not only by the barrier. The
        /// handler's ACCEPTANCE PROVENANCE line ("Task … completed by …") is emitted ONLY after ALL
        /// FOUR validation gates have passed. Asserting its absence for this task id is what makes
        /// each vector reject a deleted early return even when a LATER gate would have refused the
        /// delivery anyway — the barrier alone cannot see that, because the handler still returns
        /// normally in that case.
        /// </para>
        /// </remarks>
        public async Task CompleteAndAwaitIgnoredAsync(string taskId, string expectedReason)
        {
            var signal = ServiceLogger.WaitFor(expectedReason);
            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, null, false, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // THE EARLY RETURN IS PROVEN, not assumed.
            await BarrierAsync();

            // The refusal really was the expected guard's, and it named this task.
            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(expectedReason, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));

            // THE HANDLER STOPPED AT THAT GUARD: it never reached the post-validation acceptance
            // provenance line, so no later gate silently "rescued" a deleted early return.
            AssertNeverAccepted(taskId);
        }

        /// <summary>
        /// Asserts the handler NEVER passed its validation gates for <paramref name="taskId"/>, by
        /// the absence of the acceptance provenance line that follows them.
        /// </summary>
        public void AssertNeverAccepted(string taskId) =>
            Assert.DoesNotContain(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionAccepted, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));

        /// <summary>
        /// Delivers a completion whose mapping FAILS, awaits the production mapping-failure warning
        /// and then the post-handler barrier proving the handler returned locally.
        /// </summary>
        public async Task CompleteAndAwaitMappingFailureAsync(
            string taskId, CopilotHive.Shared.Grpc.TaskStatus status)
        {
            var signal = ServiceLogger.WaitFor(ProductionLogFragments.MappingFailed);
            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(taskId, null, false, status),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();
        }

        /// <summary>
        /// The SAME mapping-failure delivery with an EXPLICITLY PRESENT model, so the refusal is
        /// provably the MAPPING rather than the enabled-registration model-presence gate.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="status">The wire status the mapper refuses.</param>
        public async Task CompleteAndAwaitMappingFailureWithPresentModelAsync(
            string taskId, CopilotHive.Shared.Grpc.TaskStatus status)
        {
            var signal = ServiceLogger.WaitFor(ProductionLogFragments.MappingFailed);
            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(taskId, "assigned-model", true, status),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            // THE PRESENCE GATE DID NOT REFUSE: the handler reached the mapping step.
            Assert.DoesNotContain(
                ServiceLogger.Messages,
                m => m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.ModelPresenceRequired,
                         StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));
        }

        /// <summary>
        /// The recording-refusal delivery with an EXPLICITLY PRESENT model, so the refusal is provably
        /// the RECORDER's rather than the model-presence gate.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="expectedReason">The expected <see cref="WorkerCompletionRecordingFailureReason"/> name.</param>
        public async Task CompleteAndAwaitNotRecordedWithPresentModelAsync(
            string taskId, string expectedReason)
        {
            var signal = ServiceLogger.WaitFor(expectedReason);
            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, "assigned-model", true, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionNotRecorded, StringComparison.Ordinal)
                     && m.Contains(expectedReason, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));
        }

        /// <summary>
        /// THE POST-RELEASE, PRE-ACKNOWLEDGEMENT WINDOW, made deterministic: installs the
        /// production hook that fires AFTER the checked release (and its active-queue removal) and
        /// BEFORE the eligibility advance and the acknowledgement enqueue, with NO pool lock held.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WHY THIS WINDOW AND NOT ANOTHER. The classification hook runs before the handler is
        /// entered, so nothing has been released and no hold exists yet. A response-writer gate runs
        /// after the acknowledgement has already been queued and forwarded. Neither can express "the
        /// worker is released and held, and the acknowledgement is not yet enqueued", which is
        /// exactly the interval the selection hold exists to cover.
        /// </para>
        /// <para>
        /// THE HOOK DISARMS ITSELF AS IT FIRES, so it can never affect a later completion on the same
        /// stream. The returned window is the fixture's handle for the coordination; the caller MUST
        /// release it (and clear the hook) in a <c>finally</c>.
        /// </para>
        /// </remarks>
        /// <returns>The window handle the vector coordinates through.</returns>
        public PostReleaseWindow PauseInCompletionPublicationWindow()
        {
            var hookField = typeof(HiveOrchestratorService).GetField(
                "_afterCompletionReleaseBeforeAckForTest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(hookField);

            var window = new PostReleaseWindow();
            Action<ConnectedWorker, string> hook = (pinned, deliveredTaskId) =>
            {
                hookField!.SetValue(Service, null);
                window.Enter(pinned, deliveredTaskId);
            };

            hookField!.SetValue(Service, hook);
            return window;
        }

        /// <summary>Clears the completion-publication window hook, whatever state it is in.</summary>
        public void ClearCompletionPublicationHook()
        {
            var hookField = typeof(HiveOrchestratorService).GetField(
                "_afterCompletionReleaseBeforeAckForTest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            hookField?.SetValue(Service, null);
        }

        /// <summary>
        /// PUSHES ONE COMPLETION THROUGH THE REAL <c>WorkStream</c> READ LOOP AND AWAITS THE
        /// COMPLETION-PUBLICATION WINDOW, so a vector can act INSIDE the publication with the release
        /// already applied, the active-queue entry already removed and the hold already installed.
        /// </summary>
        /// <remarks>
        /// THE MESSAGE REALLY TRAVELS THE PRODUCTION ARM: classification, activity decision, the
        /// handler's validation gates, the recorder and the checked release all run as production runs
        /// them. The only test-owned element is the window hook, which observes and delays but decides
        /// nothing — everything it could affect is re-validated by the guards that follow it.
        /// </remarks>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="modelPresent">Whether the completion carries an explicit model.</param>
        /// <param name="model">The model value to carry when presence is required.</param>
        /// <returns>The window handle the caller must release (and whose hook it must clear).</returns>
        public async Task<PostReleaseWindow> CompleteAndPauseInPublicationWindowAsync(
            string taskId, string? model, bool modelPresent)
        {
            var window = PauseInCompletionPublicationWindow();

            PushCompletion(taskId, model, modelPresent);

            await window.Entered.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            return window;
        }

        /// <summary>
        /// Pushes ONE completion for the pinned worker onto the real request stream, built through the
        /// harness's own completion shape, so no vector has to reach the reader or the builder itself.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="model">The model value to carry, or <c>null</c> for ABSENT.</param>
        /// <param name="modelPresent">Whether the completion carries an explicit model.</param>
        /// <param name="status">The wire status to carry.</param>
        public void PushCompletion(
            string taskId,
            string? model,
            bool modelPresent,
            CopilotHive.Shared.Grpc.TaskStatus status = CopilotHive.Shared.Grpc.TaskStatus.Completed) =>
            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(taskId, model, modelPresent, status),
            });

        /// <summary>
        /// Pushes ONE Ready for the pinned worker onto the real request stream, so a vector can
        /// enqueue the worker's own readiness statement at a chosen instant (e.g. behind a paused
        /// completion publication) without touching the reader itself.
        /// </summary>
        public void PushReady() =>
            Reader.Push(new WorkerMessage { WorkerId = WorkerId, Ready = new WorkerReady() });

        /// <summary>
        /// THE DETERMINISTIC HANDLE ON ONE COMPLETION-PUBLICATION WINDOW: the signal that the window
        /// was entered, the release the fixture grants, and the exact instance/task the window was
        /// entered with.
        /// </summary>
        /// <remarks>
        /// A TIMED-OUT WAIT IS A LOUD FAILURE, never a silent continuation: a fixture that forgot to
        /// release would otherwise let the completion publication hang and the vector would fail on
        /// its own bound anyway; making the timeout explicit keeps the diagnosis in the window.
        /// </remarks>
        public sealed class PostReleaseWindow
        {
            private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

            private readonly System.Threading.ManualResetEventSlim _release = new(false);
            private readonly TaskCompletionSource _entered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Completes once the window has been entered.</summary>
            public Task Entered => _entered.Task;

            /// <summary>How many times the hook fired.</summary>
            public int Invocations { get; private set; }

            /// <summary>The exact instance the completion was delivered on.</summary>
            public ConnectedWorker? ObservedWorker { get; private set; }

            /// <summary>The completing task's identifier.</summary>
            public string? ObservedTaskId { get; private set; }

            /// <summary>Whether the fixture released the window.</summary>
            public bool WasReleased { get; private set; }

            internal void Enter(ConnectedWorker worker, string taskId)
            {
                ObservedWorker = worker;
                ObservedTaskId = taskId;
                Invocations++;
                _entered.TrySetResult();

                if (!_release.Wait(Bound))
                {
                    throw new TimeoutException(
                        $"the completion-publication window for task '{taskId}' was not released within " +
                        $"{Bound.TotalSeconds:F0}s — the fixture never granted it.");
                }

                WasReleased = true;
            }

            /// <summary>Grants the window, letting the completion publication continue.</summary>
            public void Release() => _release.Set();
        }

        /// <summary>
        /// The post-record CHECKED-RELEASE refusal delivery with an EXPLICITLY PRESENT model, so the
        /// refusal is provably the release's and the enabled model requirement is satisfied.
        /// </summary>
        /// <param name="taskId">The completing task's identifier.</param>
        public async Task CompleteAndAwaitCheckedReleaseRefusedWithPresentModelAsync(string taskId)
        {
            var signal = ServiceLogger.WaitFor(
                HiveOrchestratorService.OwnershipRefusalReasons.CheckedReleaseRefused);

            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, "assigned-model", true, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.CheckedReleaseRefused,
                         StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Delivers a completion whose RECEIPT WAS NOT CONFIRMED, awaits the production
        /// recording-refusal warning naming <paramref name="expectedReason"/> and then the
        /// post-handler barrier proving the handler returned locally.
        /// </summary>
        /// <remarks>
        /// WAITING ON THE REASON, not merely on "a refusal", keeps the vector discriminating: every
        /// refusal family emits the SAME canonical disposition sentence, so a generic wait would be
        /// satisfied by the wrong guard. The barrier then proves the handler RETURNED rather than
        /// unwinding the stream.
        /// </remarks>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="expectedReason">The refusal family's name, as the production warning renders it.</param>
        public async Task CompleteAndAwaitNotRecordedAsync(string taskId, string expectedReason)
        {
            var signal = ServiceLogger.WaitFor(expectedReason);
            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, null, false, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // THE EARLY RETURN IS PROVEN, not assumed.
            await BarrierAsync();

            // The refusal really was the recorder's, it named this task and the worker, and it
            // carried the canonical disposition sentence.
            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionNotRecorded, StringComparison.Ordinal)
                     && m.Contains(expectedReason, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal)
                     && m.Contains(WorkerId, StringComparison.Ordinal)
                     && m.Contains(
                         "logical cancellation alone does not release transport ownership",
                         StringComparison.Ordinal));

            // THE HANDLER STOPPED BEFORE THE RELEASE: the acceptance provenance line was emitted
            // (the ownership validation really passed), but the worker still owns the task.
            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionAccepted, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Delivers a completion whose CHECKED RELEASE is refused AFTER the receipt was recorded,
        /// awaits the production checked-release refusal warning and then the post-handler barrier
        /// proving the handler returned locally.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE REFUSAL IS ATTRIBUTED TO THE CHECKED RELEASE SPECIFICALLY, by waiting on that
        /// guard's OWN reason text. A pre-record gate refusing instead would emit a different
        /// reason and this wait would expire.
        /// </para>
        /// <para>
        /// THE PRE-RECORD VALIDATION IS PROVEN TO HAVE PASSED, by the presence of the acceptance
        /// provenance line for this task id — which production emits only after all four ownership
        /// gates and before the recording call. Together with the recorder's own invocation count,
        /// that is what makes this a POST-record refusal rather than an early one.
        /// </para>
        /// </remarks>
        /// <param name="taskId">The completing task's identifier.</param>
        public async Task CompleteAndAwaitCheckedReleaseRefusedAsync(string taskId)
        {
            var signal = ServiceLogger.WaitFor(
                HiveOrchestratorService.OwnershipRefusalReasons.CheckedReleaseRefused);

            Reader.Push(new WorkerMessage
            {
                WorkerId = WorkerId,
                Complete = BuildComplete(
                    taskId, null, false, CopilotHive.Shared.Grpc.TaskStatus.Completed),
            });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // THE EARLY RETURN IS PROVEN, not assumed.
            await BarrierAsync();

            // The refusal really was the CHECKED RELEASE's, and it named this task.
            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.CheckedReleaseRefused,
                         StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));

            // THE PRE-RECORD VALIDATION PASSED: the acceptance provenance line is present, so the
            // handler reached the recorder — this is NOT an early ownership refusal.
            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionAccepted, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));

            // AND IT WAS NOT A RECORDING REFUSAL EITHER: the canonical receipt-not-confirmed
            // disposition never appeared for this task.
            Assert.DoesNotContain(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.CompletionNotRecorded, StringComparison.Ordinal)
                     && m.Contains(taskId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Pushes a Ready and awaits the production READY-IGNORED warning naming the STILL-ACTIVE
        /// queue entry as the refusing guard, then the post-handler barrier.
        /// </summary>
        public async Task ReadyAndAwaitIgnoredAsync()
        {
            var signal = ServiceLogger.WaitFor(
                HiveOrchestratorService.OwnershipRefusalReasons.ReadyTaskStillActive);
            Reader.Push(new WorkerMessage { WorkerId = WorkerId, Ready = new WorkerReady() });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.ReadyIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.ReadyTaskStillActive,
                         StringComparison.Ordinal));
        }

        /// <summary>
        /// Pushes a Ready and awaits the production READY-IGNORED warning naming the CHECKED-IDLE
        /// refusal as the refusing guard, then the post-handler barrier.
        /// </summary>
        public async Task ReadyAndAwaitRefusedIdleAsync()
        {
            var signal = ServiceLogger.WaitFor(
                HiveOrchestratorService.OwnershipRefusalReasons.ReadyCheckedIdleRefused);
            Reader.Push(new WorkerMessage { WorkerId = WorkerId, Ready = new WorkerReady() });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();

            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(ProductionLogFragments.ReadyIgnored, StringComparison.Ordinal)
                     && m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.ReadyCheckedIdleRefused,
                         StringComparison.Ordinal));
        }

        /// <summary>
        /// Pushes a Ready and awaits the production "is ready" line, which is emitted ONLY after
        /// the checked idle was applied, then the post-handler barrier.
        /// </summary>
        public async Task ReadyAndAwaitAcceptedAsync()
        {
            var signal = ServiceLogger.WaitFor(ProductionLogFragments.ReadyAccepted);
            Reader.Push(new WorkerMessage { WorkerId = WorkerId, Ready = new WorkerReady() });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            await BarrierAsync();
        }

        /// <summary>
        /// ARMS THE POST-RELEASE WINDOW TO THROW <paramref name="fault"/>, so a vector can prove the
        /// publication's <c>try/finally</c> really covers EVERY post-acquisition operation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// IT REUSES THE EXISTING HOOK, and it is the only way to inject a fault at exactly that
        /// window: the queue removal, the eligibility advance and the enqueue are all production calls
        /// with no injectable fault of their own on this path, so the hook is the representative
        /// post-acquisition operation. The hook disarms itself as it fires, so a later delivery on the
        /// same stream is unaffected.
        /// </para>
        /// <para>
        /// THE FAULT IS RAISED *AFTER* THE STATE OBSERVATION, so the vector can record what the hold
        /// and the release looked like at the instant the fault was raised rather than inferring it.
        /// </para>
        /// </remarks>
        /// <param name="fault">The exception the window must throw exactly once.</param>
        /// <param name="observe">Observation run inside the window, immediately before the throw.</param>
        public void ArmThrowingCompletionPublicationWindow(
            Exception fault, Action<ConnectedWorker, string> observe)
        {
            var hookField = typeof(HiveOrchestratorService).GetField(
                "_afterCompletionReleaseBeforeAckForTest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(hookField);

            Action<ConnectedWorker, string> hook = (pinned, deliveredTaskId) =>
            {
                hookField!.SetValue(Service, null);
                observe(pinned, deliveredTaskId);
                throw fault;
            };

            hookField!.SetValue(Service, hook);
        }

        /// <summary>
        /// Invokes the PRODUCTION <c>HandleTaskComplete</c> directly with a stale pinned instance,
        /// SIMULATING the TOCTOU window between <c>WorkStream</c>'s per-message pinned-instance
        /// check and its dispatch to this handler — a window a replacement re-registering under the
        /// same ID really can land in. Direct invocation is how that interleaving is reproduced
        /// deterministically without adding a production seam.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE CALL ITSELF IS THE BARRIER on this path: it is synchronous, so returning proves the
        /// handler returned, and any exception a removed early return would raise propagates
        /// straight into the vector (unwrapped below) instead of into a swallowed stream fault.
        /// </para>
        /// <para>
        /// THE ELIGIBILITY HOLDER IS THE TEST'S OWN, AND IS SAID SO. Production's holder belongs to
        /// one live <c>WorkStream</c> invocation; a direct handler call is not that invocation, so
        /// this supplies a holder the caller owns rather than pretending to reach a stream's local.
        /// </para>
        /// </remarks>
        /// <param name="pinned">The pinned instance to hand the handler.</param>
        /// <param name="taskId">The completing task's identifier.</param>
        /// <param name="ackState">
        /// The TEST-OWNED eligibility holder; a caller that simulates an ordinary completion and then
        /// a duplicate passes the SAME instance to both calls.
        /// </param>
        /// <param name="model">
        /// The model to carry, or <c>null</c> for an ABSENT field (the pre-existing default). An
        /// ENABLED registration requires presence, so a vector that drives one states it here.
        /// </param>
        public void InvokeHandleTaskCompleteDirectly(
            ConnectedWorker pinned,
            string taskId,
            WorkStreamCompletionAckState ackState,
            string? model = null)
        {
            var method = typeof(HiveOrchestratorService).GetMethod(
                "HandleTaskComplete",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            var complete = new GrpcTaskComplete
            {
                TaskId = taskId,
                Status = CopilotHive.Shared.Grpc.TaskStatus.Completed,
                Output = $"output-{taskId}",
            };
            if (model is not null)
                complete.Model = model;

            Assert.Equal(model is not null, complete.HasModel);

            try
            {
                method!.Invoke(Service, [pinned, complete, ackState]);
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                // Surface the PRODUCTION exception itself, not the reflection wrapper.
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        /// <summary>
        /// Pushes a Ready and awaits BOTH the production READY-ACCEPTED line AND the real pump's
        /// forwarding of the published assignment to the gRPC response writer — so a returned call
        /// proves the Ready was accepted AND the assignment genuinely left the transport.
        /// </summary>
        /// <remarks>
        /// THE OBSERVATION POINT IS THE WRITER, NOT THE CHANNEL. The production pump is the only
        /// consumer of the worker's message channel; observing there would mean competing with it
        /// (either reader could win). Waiting at the writer observes the publication AFTER the real
        /// pump forwarded it, so nothing is intercepted and nothing races.
        /// </remarks>
        public async Task ReadyAndAwaitAssignmentPublishedAsync(string expectedTaskId)
        {
            var forwarded = Writer.WaitForAssignment();

            await ReadyAndAwaitAcceptedAsync();

            var assignment = await forwarded.WaitAsync(
                BoundedWait, TestContext.Current.CancellationToken);
            Assert.Equal(expectedTaskId, assignment.TaskId);
        }

        // ── THE EXCLUSIVE WORKSTREAM ATTACHMENT CLAIM ────────────────────────────────

        /// <summary>
        /// Starts a SECOND, independent stream for <paramref name="workerId"/> on the SAME service,
        /// with its own request reader and its own response writer, and returns the retained handle.
        /// The teardown joins it like any other producer.
        /// </summary>
        /// <remarks>
        /// THE FIRST MESSAGE IS THE ATTACHMENT ATTEMPT: it is pushed into this second stream's own
        /// reader, so whichever stream wins the claim is decided by production, not by the test.
        /// </remarks>
        /// <param name="workerId">The worker id the second stream's first message names.</param>
        /// <param name="firstMessage">The message the second stream sees first.</param>
        /// <returns>The retained handle for the second stream.</returns>
        public SecondStream StartSecondStream(string workerId, WorkerMessage firstMessage)
        {
            var reader = new ChannelStreamReader();
            var writer = new SignallingStreamWriter();
            var task = Service.WorkStream(reader, writer, MockContext());

            reader.Push(firstMessage);

            var handle = new SecondStream(reader, writer, task, workerId);
            _extraStreams.Add(handle);
            return handle;
        }

        /// <summary>
        /// Starts a second stream whose first message is a Ready for <paramref name="workerId"/> and
        /// AWAITS its normal termination, so a loser's clean completion is directly observable.
        /// </summary>
        /// <param name="workerId">The worker id the second stream's Ready names.</param>
        /// <returns>The retained handle, already finished.</returns>
        public async Task<SecondStream> StartSecondStreamAndAwaitTerminationAsync(string workerId)
        {
            var second = StartSecondStream(workerId, new WorkerMessage
            {
                WorkerId = workerId,
                Ready = new WorkerReady(),
            });

            second.Completion = await AwaitCleanCompletionAsync(second);
            return second;
        }

        /// <summary>
        /// Awaits a second stream's termination and records how it ended, so a vector that supplied
        /// its own first message can still assert a CLEAN return.
        /// </summary>
        /// <param name="second">The second-stream handle to join.</param>
        /// <returns>The classified completion, also stored on the handle.</returns>
        public async Task<StreamCompletion> AwaitSecondStreamTerminationAsync(SecondStream second)
        {
            var completion = await AwaitCleanCompletionAsync(second);
            second.Completion = completion;
            return completion;
        }

        /// <summary>
        /// AWAITS A LOSING STREAM'S NORMAL COMPLETION and classifies how it ended, so "returned
        /// normally" is asserted rather than assumed.
        /// </summary>
        /// <param name="second">The second-stream handle to join.</param>
        /// <returns>The observed completion.</returns>
        private static async Task<StreamCompletion> AwaitCleanCompletionAsync(SecondStream second)
        {
            try
            {
                await second.Producer.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
                return new StreamCompletion(Faulted: false, Fault: null);
            }
            catch (TimeoutException ex)
            {
                return new StreamCompletion(
                    Faulted: true,
                    Fault: new TimeoutException(
                        $"the second WorkStream for '{second.WorkerId}' did not terminate within " +
                        $"{BoundedWait.TotalSeconds:F0}s — it neither completed nor faulted cleanly.",
                        ex));
            }
            catch (Exception ex)
            {
                // A clean loser must RETURN, never fault, so ANY exception is a failure — an
                // RpcException with a transport status especially so.
                return new StreamCompletion(Faulted: true, Fault: ex);
            }
        }

        /// <summary>
        /// Asserts the second stream REALLY was rejected: it terminated normally, emitted the
        /// guarded already-attached warning for its worker id, and never became the channel's
        /// consumer.
        /// </summary>
        /// <param name="second">The second-stream handle.</param>
        public void AssertSecondStreamWasRejectedNormally(SecondStream second)
        {
            Assert.NotNull(second.Completion);
            Assert.False(
                second.Completion!.Faulted,
                $"a losing WorkStream must return normally, not fault: {second.Completion.Fault}");

            Assert.Contains(
                ServiceLogger.Messages,
                m => m.Contains(
                         HiveOrchestratorService.OwnershipRefusalReasons.WorkStreamAlreadyAttached,
                         StringComparison.Ordinal)
                     && m.Contains(second.WorkerId, StringComparison.Ordinal));
        }

        /// <summary>The second stream's own writer never received a forwarded message.</summary>
        /// <param name="second">The second-stream handle.</param>
        public static void AssertSecondStreamForwardedNothing(SecondStream second) =>
            Assert.Empty(second.Writer.Messages);

        /// <summary>
        /// THE SHARED STRICT TEARDOWN: ends the request stream and joins the RETAINED producer with
        /// a finite bound. It NEVER throws — the outcome is RETURNED so a primary assertion failure
        /// stays authoritative.
        /// </summary>
        /// <remarks>
        /// <para>
        /// IT IS STRICT IN BOTH DIRECTIONS, which is the whole point:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>A BOUNDED EXPIRY IS A FAILURE, never "cleanup succeeded": the
        ///     producer is still live.</description></item>
        ///   <item><description>A TERMINAL FAULT IS A FAILURE too. The old teardown swallowed the
        ///     fault of a completed task, which is exactly how a deleted early return could hide —
        ///     the guard's warning was still emitted, the handler then threw, and the fault
        ///     disappeared into cleanup.</description></item>
        /// </list>
        /// </remarks>
        /// <returns><c>null</c> when the producer terminated cleanly; otherwise the failure.</returns>
        public async Task<Exception?> StopAsync()
        {
            Reader.Complete();

            Exception? failure = null;
            try
            {
                await StreamTask.WaitAsync(BoundedWait, CancellationToken.None);
            }
            catch (TimeoutException ex)
            {
                failure = new TimeoutException(
                    $"TEARDOWN LEAK: the WorkStream for worker '{WorkerId}' did not terminate " +
                    $"within {BoundedWait.TotalSeconds:F0}s — a live producer remains.",
                    ex);
            }
            catch (Exception ex) when (StreamTask.IsCompleted)
            {
                failure = new InvalidOperationException(
                    "THE WORKSTREAM TERMINATED WITH A FAULT. A clean vector must leave the transport " +
                    "draining normally; a fault here means a handler escaped instead of returning.",
                    ex);
            }
            catch (Exception ex)
            {
                failure = new InvalidOperationException(
                    $"TEARDOWN LEAK: the WorkStream for worker '{WorkerId}' is still running after " +
                    "its join failed — a live producer remains.",
                    ex);
            }

            // THE RECORD STORES' OWN LIFETIME: the in-memory anchor connection is released here,
            // after the producer has been joined, so a leftover handle can never fail a test — and
            // its disposal is deliberately NOT accounted as a teardown failure.
            try
            {
                Stores.Dispose();
            }
            catch
            {
                // Best-effort — a leftover fixture must never fail a test.
            }

            // EVERY ADDITIONAL STREAM IS JOINED TOO — no vector may leak a second producer.
            foreach (var extra in _extraStreams)
            {
                extra.Reader.Complete();

                if (!extra.Producer.IsCompleted)
                {
                    try
                    {
                        await extra.Producer.WaitAsync(BoundedWait, CancellationToken.None);
                    }
                    catch (TimeoutException ex)
                    {
                        failure ??= new TimeoutException(
                            $"TEARDOWN LEAK: the second WorkStream for '{extra.WorkerId}' did not " +
                            $"terminate within {BoundedWait.TotalSeconds:F0}s — a live producer remains.",
                            ex);
                    }
                    catch (Exception ex) when (!extra.Producer.IsCompleted)
                    {
                        failure ??= new InvalidOperationException(
                            $"TEARDOWN LEAK: the second WorkStream for '{extra.WorkerId}' is still " +
                            "running after its join failed — a live producer remains.",
                            ex);
                    }
                }
            }

            return failure;
        }

        /// <summary>
        /// ONE ADDITIONAL STREAM over a worker id: its own request reader, its own response writer
        /// and its retained producer, plus the classified completion a vector observed.
        /// </summary>
        public sealed record SecondStream(
            ChannelStreamReader Reader,
            SignallingStreamWriter Writer,
            Task Producer,
            string WorkerId)
        {
            /// <summary>
            /// How the stream terminated once <see cref="Harness.AwaitCleanCompletionAsync"/> joined
            /// it, or <c>null</c> when the vector never awaited it.
            /// </summary>
            public StreamCompletion? Completion { get; set; }
        }

        /// <summary>
        /// HOW A SECOND STREAM ENDED. <see cref="Faulted"/> is the whole point: a losing stream must
        /// RETURN normally (a clean RPC completion), never fault with an
        /// <see cref="RpcException"/>.
        /// </summary>
        /// <param name="Faulted">Whether the stream ended by throwing rather than returning.</param>
        /// <param name="Fault">The observed failure, when <paramref name="Faulted"/> is <c>true</c>.</param>
        public sealed record StreamCompletion(bool Faulted, Exception? Fault);
    }

    /// <summary>
    /// The transport-side observation of the SHARED notifier: it records each emitted
    /// <see cref="TaskResult"/> so the model assertions have a value to read, AND — crucially — it
    /// probes the REAL receipt store AT THAT INSTANT, before the awaiting test is released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT IS NEVER DOWNSTREAM EVIDENCE. This observer proves only that the TRANSPORT published a
    /// domain result; whether the REAL downstream chain received and classified it is read from
    /// <c>TaskCompletionService</c>'s own production log lines. It is subscribed FIRST so the real
    /// dispatcher stays the last (and therefore awaited) subscriber.
    /// </para>
    /// <para>
    /// THE PROBE IS THE ORDERING EVIDENCE. Because this subscriber runs INSIDE production's
    /// <c>NotifyAsync</c> invocation chain, the row it loads is the row that existed at the moment
    /// the completion was published. A regression that published BEFORE persisting the receipt
    /// would be caught here; a post-call readback could not tell the two apart.
    /// </para>
    /// </remarks>
    private sealed class CompletionObservations
    {
        private readonly Queue<TaskCompletionSource<TaskResult>> _waiters = new();
        private readonly object _gate = new();
        private int _count;
        private Observation? _last;

        /// <summary>
        /// ONE observation: the published result, the receipt read AT that instant, and the probe's
        /// own failure when the load itself threw.
        /// </summary>
        /// <param name="Result">The domain result the transport published.</param>
        /// <param name="ReceiptAtNotification">
        /// The receipt row as it existed when the notification was raised, or <c>null</c> when no
        /// row existed at that instant.
        /// </param>
        /// <param name="ProbeFailure">
        /// The exception the probe's own load threw, or <c>null</c>. It is CAPTURED rather than
        /// thrown so a probe failure can never corrupt the production notification chain under
        /// observation.
        /// </param>
        public sealed record Observation(
            TaskResult Result,
            CompletionReceiptReadResult? ReceiptAtNotification,
            Exception? ProbeFailure);

        /// <summary>
        /// The REAL receipt load the probe performs, installed by the harness. It is a plain read
        /// through the production store — it mutates nothing and it is never used by production.
        /// </summary>
        public Func<string, CompletionReceiptReadResult?>? ReceiptProbe { get; set; }

        /// <summary>
        /// A ONE-SHOT observation the harness installs, run INSIDE this subscriber's own callback (and
        /// therefore inside production's notification chain) and consumed exactly once.
        /// </summary>
        /// <remarks>
        /// IT OBSERVES, IT DECIDES NOTHING: the callback is cleared as it fires, and nothing it does can
        /// change the emitted result or the ordering the vector is measuring. It exists so a vector can
        /// read state AT the notification instant instead of after it.
        /// </remarks>
        public Action? PendingObservation { get; set; }

        /// <summary>How many results the transport published on the shared notifier.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>The most recent observation, including its notification-instant receipt probe.</summary>
        public Observation? Last
        {
            get
            {
                lock (_gate)
                    return _last;
            }
        }

        /// <summary>A FRESH waiter for the NEXT published result; never satisfied by an earlier one.</summary>
        public Task<TaskResult> NextResult()
        {
            var waiter = new TaskCompletionSource<TaskResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiters)
                _waiters.Enqueue(waiter);
            return waiter.Task;
        }

        public Task Record(TaskResult result)
        {
            Interlocked.Increment(ref _count);

            // ── THE ONE-SHOT NOTIFICATION-INSTANT OBSERVATION, taken BEFORE the waiter is released ──
            // Consumed exactly once, and it observes rather than decides: nothing it can do changes the
            // emitted result.
            var pendingObservation = PendingObservation;
            PendingObservation = null;
            pendingObservation?.Invoke();

            // ── THE NOTIFICATION-INSTANT PROBE, taken BEFORE the waiter is released ──────────
            // A throwing probe is captured, never propagated: this subscriber sits inside
            // production's own multicast chain, and a test-owned fault must not alter it.
            CompletionReceiptReadResult? atNotification = null;
            Exception? probeFailure = null;
            var probe = ReceiptProbe;
            if (probe is not null)
            {
                try
                {
                    atNotification = probe(result.TaskId);
                }
                catch (Exception ex)
                {
                    probeFailure = ex;
                }
            }

            lock (_gate)
                _last = new Observation(result, atNotification, probeFailure);

            TaskCompletionSource<TaskResult>? waiter = null;
            lock (_waiters)
            {
                if (_waiters.Count > 0)
                    waiter = _waiters.Dequeue();
            }

            waiter?.TrySetResult(result);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// THE OWNERSHIP-MUTATING RECORDER: a DECORATOR over the REAL
    /// <see cref="WorkerCompletionRecorder"/> that performs the genuine store write first, CAPTURES
    /// the persisted receipt, and only then runs the vector's own mutation — so a test can
    /// invalidate transport ownership at exactly the instant BETWEEN the record and the checked
    /// release, with a baseline taken before anything else could have touched the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT IS NOT A FAKE RECORDER. The real recorder does the whole job — the real assignment load,
    /// the real agreement checks, the real <c>InsertOnce</c> — and every refusal it raises
    /// propagates UNCHANGED, because nothing below runs unless it returned successfully. The
    /// durable evidence the vectors then read is production-written.
    /// </para>
    /// <para>
    /// THE CAPTURE ORDER IS THE CONTRACT: real record → <see cref="CapturedReceipt"/> →
    /// <see cref="AfterRecord"/>. Taking the baseline BEFORE the mutation (and therefore before the
    /// checked release) is the ONLY point from which a compensating delete/reinsert during the
    /// refusal is detectable: a baseline read afterwards would already carry the refreshed
    /// timestamp and would agree with itself.
    /// </para>
    /// <para>
    /// IT IS DETERMINISTIC. Both the capture and <see cref="AfterRecord"/> run synchronously on the
    /// handler's own thread, inside its call stack, so the interleaving is exact rather than raced.
    /// The mutation callback is consumed ONCE per arming, so a later delivery in the same vector is
    /// unaffected.
    /// </para>
    /// </remarks>
    internal sealed class OwnershipMutatingRecorder(
        IWorkerCompletionRecorder inner,
        Func<string, CompletionReceiptReadResult?> receiptReader)
        : IWorkerCompletionRecorder
    {
        private int _recordCount;
        private int _confirmCount;

        /// <summary>
        /// The mutation to run IMMEDIATELY AFTER a successful real record and AFTER the receipt has
        /// been captured, or <c>null</c> for none. It is cleared as it fires, so exactly one
        /// delivery is affected per arming.
        /// </summary>
        public Action? AfterRecord { get; set; }

        /// <summary>How many times the REAL recorder returned successfully through this decorator.</summary>
        public int RecordCount => Volatile.Read(ref _recordCount);

        /// <summary>
        /// How many CONFIRMATION READS were ATTEMPTED through this decorator — the probe that
        /// distinguishes "the delivery never entered the read-only duplicate branch" from "it entered
        /// and the evidence merely did not match".
        /// </summary>
        /// <remarks>
        /// IT COUNTS ATTEMPTS, NOT ANSWERS. Only the duplicate branch calls the confirmation at all,
        /// so a ZERO here is positive evidence that the stream's own eligibility never named the task
        /// — which is exactly what an early-advance mutant would violate.
        /// </remarks>
        public int ConfirmCount => Volatile.Read(ref _confirmCount);

        /// <summary>
        /// THE PRE-MUTATION BASELINE: the persisted receipt as it stood the instant the real
        /// recorder returned — before the ownership mutation and before the checked release. It is
        /// <c>null</c> until a record succeeds, and it carries the row's own
        /// <see cref="CompletionReceiptReadResult.FirstStoredAtUtc"/>.
        /// </summary>
        public CompletionReceiptReadResult? CapturedReceipt { get; private set; }

        /// <summary>
        /// The exception the baseline capture itself threw, or <c>null</c>. It is CAPTURED rather
        /// than thrown, so a test-owned read failure can never masquerade as a production recording
        /// refusal and change the disposition under test.
        /// </summary>
        public Exception? CaptureFailure { get; private set; }

        /// <inheritdoc />
        public void Record(string workerId, WorkTask task, TaskResult result)
        {
            // THE REAL RECORDING, unchanged: a refusal propagates from here and nothing below runs,
            // so a refusing vector still exercises the production contract exactly.
            inner.Record(workerId, task, result);

            Interlocked.Increment(ref _recordCount);

            // ── THE BASELINE, TAKEN BEFORE THE MUTATION ──────────────────────────────────────
            // A throwing read is recorded, never propagated: this decorator sits inside the
            // production handler's call stack and must not alter its outcome.
            try
            {
                CapturedReceipt = receiptReader(result.TaskId);
            }
            catch (Exception ex)
            {
                CaptureFailure = ex;
            }

            var mutation = AfterRecord;
            AfterRecord = null;
            mutation?.Invoke();
        }

        /// <inheritdoc />
        /// <remarks>
        /// IT IS FORWARDED VERBATIM TO THE REAL RECORDER, and that is load-bearing. Leaving this
        /// operation to the interface's fail-closed default would make this decorator answer "no
        /// matching evidence" for EVERY duplicate, so every "nothing was acknowledged" assertion on
        /// this harness would pass vacuously — including the ones that must FAIL when an early
        /// eligibility advance wrongly routes a failed completion into the read-only branch. The
        /// decorator adds only the attempt counter; it changes no confirmation behaviour.
        /// </remarks>
        public bool ConfirmStoredReceipt(string workerId, string taskId, TaskResult result)
        {
            Interlocked.Increment(ref _confirmCount);
            return inner.ConfirmStoredReceipt(workerId, taskId, result);
        }
    }

    /// <summary>
    /// THE PRODUCTION LOG FRAGMENTS these vectors synchronize on. Kept together so a wording change
    /// in production surfaces as one obvious edit rather than as scattered flaky waits.
    /// </summary>
    private static class ProductionLogFragments
    {
        /// <summary>The guarded completion-refusal warning's stable fragment.</summary>
        public const string CompletionIgnored = "completion for task";

        /// <summary>The guarded mapping-failure warning's stable fragment.</summary>
        public const string MappingFailed = "could not be mapped";

        /// <summary>
        /// The guarded completion-RECORDING refusal warning's stable fragment — the canonical
        /// disposition wording of a completion whose receipt was not confirmed.
        /// </summary>
        public const string CompletionNotRecorded = "receipt not confirmed";

        /// <summary>The guarded Ready-refusal warning's stable fragment.</summary>
        public const string ReadyIgnored = "ready ignored";

        /// <summary>The accepted-Ready information line, emitted after the checked idle.</summary>
        public const string ReadyAccepted = "is ready";

        /// <summary>
        /// THE COMPLETION ACCEPTANCE PROVENANCE line, emitted ONLY after ALL FOUR of
        /// <c>HandleTaskComplete</c>'s validation gates have passed. Its ABSENCE for a task id is
        /// the evidence that the handler stopped at a guard.
        /// </summary>
        public const string CompletionAccepted = "completed by";

        /// <summary>
        /// <c>TaskCompletionService</c>'s MISSING-PIPELINE guard — the real downstream drop the
        /// cancellation sequence proves.
        /// </summary>
        public const string NoPipelineForTask = "No pipeline found for completed task";

        /// <summary>
        /// The guarded acknowledgement-ENQUEUE diagnostic's stable fragment: the acknowledgement
        /// could not be queued on the pinned instance's channel, so no delivery is claimed.
        /// </summary>
        public const string ReceiptAckNotQueued = "acknowledgement for task";

        /// <summary>
        /// The same-stream RE-ACKNOWLEDGEMENT success line, emitted ONLY once the confirmation
        /// matched, the post-read eligibility recheck passed AND the message was really QUEUED.
        /// </summary>
        public const string DuplicateReAcknowledged = "re-acknowledgement queued for task";

        /// <summary>
        /// The pump's guarded acknowledgement-WRITE diagnostic's stable fragment: forwarding an
        /// acknowledgement to the response writer failed and it was dropped.
        /// </summary>
        public const string ReceiptAckWriteFailed = "could not be written to the worker's stream";

        /// <summary>
        /// Whether a dispatcher log line is one of <c>TaskCompletionService</c>'s own
        /// completion-handling lines — i.e. the real downstream chain was entered.
        /// </summary>
        /// <remarks>
        /// Every terminating path of <c>HandleTaskCompletionAsync</c> emits exactly one of these,
        /// so their ABSENCE for a task id means the downstream chain never ran for it at all.
        /// </remarks>
        public static bool DownstreamEntered(string message) =>
            message.Contains(NoPipelineForTask, StringComparison.Ordinal)
            || message.Contains("already", StringComparison.Ordinal)
            || message.Contains("StaleCompletion", StringComparison.Ordinal)
            || message.Contains("ignoring stale completion", StringComparison.Ordinal)
            || message.Contains("WorkSlotIntegrity", StringComparison.Ordinal)
            || message.Contains("task completed", StringComparison.Ordinal);
    }

    /// <summary>
    /// Records every logged message and hands out FRESH per-call signals for the production lines
    /// the vectors synchronize on. A previously emitted line can never satisfy a later wait.
    /// </summary>
    /// <typeparam name="TCategory">The logger category — the service or the real dispatcher.</typeparam>
    private sealed class SignallingLogger<TCategory> : ILogger<TCategory>
    {
        private readonly List<string> _messages = [];
        private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];

        /// <summary>The message fragment whose emission must throw — armed by the diagnostic vector.</summary>
        private volatile string? _throwFragment;

        private int _throwCount;

        /// <summary>How many writes actually threw — the proof the fallible diagnostic really ran.</summary>
        public int ThrowCount => Volatile.Read(ref _throwCount);

        /// <summary>The DISTINCT instance a faulted write throws, so identity is assertable.</summary>
        public InvalidOperationException LoggerSentinel { get; } = new("the logger itself threw SENTINEL");

        /// <summary>
        /// Arms the throw for any message containing <paramref name="fragment"/>. Every OTHER write
        /// keeps its normal behaviour — crucially the post-handler barrier's own Progress line, so a
        /// guarded diagnostic can be proven not to unwind the stream.
        /// </summary>
        /// <param name="fragment">The fragment a faulted write must contain.</param>
        public void ArmThrowOnFragment(string fragment) => _throwFragment = fragment;

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        /// <summary>A FRESH signal completed by the NEXT message containing <paramref name="fragment"/>.</summary>
        public Task WaitFor(string fragment)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_messages)
                _waiters.Add((fragment, signal));
            return signal.Task;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            List<TaskCompletionSource> matched = [];
            var faulted = false;
            lock (_messages)
            {
                _messages.Add(message);

                // THE ARMED DIAGNOSTIC FAULT IS COUNTED BEFORE any waiter is released: a test that
                // awaits the diagnostic's own signal must never be able to run its continuation and
                // observe a counter that has not yet been incremented. The THROW itself still happens
                // after the waiters are released, so the production code under test really emitted
                // the warning and the FAULT is what its guard has to survive.
                var armed = _throwFragment;
                if (armed is not null && message.Contains(armed, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _throwCount);
                    faulted = true;
                }

                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (!message.Contains(_waiters[i].Fragment, StringComparison.Ordinal))
                        continue;

                    matched.Add(_waiters[i].Signal);
                    _waiters.RemoveAt(i);
                }
            }

            foreach (var signal in matched)
                signal.TrySetResult();

            if (faulted)
                throw LoggerSentinel;
        }
    }

    private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;

    /// <summary>In-memory stream reader backed by an unbounded channel.</summary>
    private sealed class ChannelStreamReader : IAsyncStreamReader<WorkerMessage>
    {
        private readonly System.Threading.Channels.Channel<WorkerMessage> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<WorkerMessage>();

        public WorkerMessage Current { get; private set; } = new();

        public void Push(WorkerMessage message) => _channel.Writer.TryWrite(message);

        public void Complete() => _channel.Writer.TryComplete();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_channel.Reader.TryRead(out var message))
                {
                    Current = message;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// THE PUBLICATION OBSERVATION POINT: the gRPC response writer the real <c>WorkStream</c> pump
    /// forwards every queued <see cref="OrchestratorMessage"/> to.
    /// </summary>
    /// <remarks>
    /// This is where "was an assignment delivered to this worker" is observable WITHOUT competing
    /// with the production pump for the worker's message channel. It records what it is given and
    /// signals per assignment; it never intercepts, delays or drops anything.
    /// </remarks>
    private sealed class SignallingStreamWriter : IServerStreamWriter<OrchestratorMessage>
    {
        private readonly List<OrchestratorMessage> _messages = [];
        private readonly Queue<TaskCompletionSource<TaskAssignment>> _assignmentWaiters = new();
        private readonly List<(Func<OrchestratorMessage, bool> Predicate, TaskCompletionSource<OrchestratorMessage> Signal)>
            _messageWaiters = [];

        /// <summary>The one-shot fault the NEXT acknowledgement write throws, or <c>null</c>.</summary>
        private Exception? _acknowledgementWriteFault;

        private int _acknowledgementWriteAttempts;

        public WriteOptions? WriteOptions { get; set; }

        /// <summary>Every message the transport forwarded, in order.</summary>
        public IReadOnlyList<OrchestratorMessage> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        /// <summary>How many acknowledgement writes this writer was asked to perform.</summary>
        public int AcknowledgementWriteAttempts => Volatile.Read(ref _acknowledgementWriteAttempts);

        /// <summary>
        /// Arms a ONE-SHOT fault: the NEXT acknowledgement handed to this writer throws
        /// <paramref name="failure"/>, and every later write behaves normally.
        /// </summary>
        /// <remarks>
        /// ONE SHOT, DELIBERATELY. A permanently throwing writer would make the pump fail on every
        /// subsequent message too, so the vector could not distinguish \"the acknowledgement write
        /// failed\" from \"the stream is now unusable\" — and the point of the vector is that the
        /// failure is ISOLATED.
        /// </remarks>
        /// <param name="failure">The exception the next acknowledgement write must throw.</param>
        public void ArmOneShotAcknowledgementWriteFault(Exception failure) =>
            _acknowledgementWriteFault = failure;

        /// <summary>
        /// A FRESH signal completed by the NEXT forwarded message satisfying
        /// <paramref name="predicate"/>. Allocated BEFORE the message is produced, so a publication
        /// can never be missed.
        /// </summary>
        /// <param name="predicate">Selects the forwarded message the caller is waiting for.</param>
        public Task<OrchestratorMessage> WaitForMessage(Func<OrchestratorMessage, bool> predicate)
        {
            var signal = new TaskCompletionSource<OrchestratorMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_messages)
                _messageWaiters.Add((predicate, signal));
            return signal.Task;
        }

        /// <summary>
        /// A FRESH signal completed by the NEXT forwarded assignment. Allocated BEFORE the Ready is
        /// pushed, so it can never be missed and never satisfied by an earlier publication.
        /// </summary>
        public Task<TaskAssignment> WaitForAssignment()
        {
            var waiter = new TaskCompletionSource<TaskAssignment>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_messages)
                _assignmentWaiters.Enqueue(waiter);
            return waiter.Task;
        }

        private Task RecordAsync(OrchestratorMessage message)
        {
            // THE ONE-SHOT ACKNOWLEDGEMENT WRITE FAULT. It is armed and consumed HERE, before the
            // message is recorded, so a faulted write really means \"the writer threw\" and not
            // \"the writer also recorded it\".
            if (message.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck)
            {
                Interlocked.Increment(ref _acknowledgementWriteAttempts);

                var armed = Interlocked.Exchange(ref _acknowledgementWriteFault, null);
                if (armed is not null)
                    throw armed;
            }

            TaskCompletionSource<TaskAssignment>? waiter = null;
            List<TaskCompletionSource<OrchestratorMessage>> matched = [];

            lock (_messages)
            {
                _messages.Add(message);

                if (message.Assignment is not null && _assignmentWaiters.Count > 0)
                    waiter = _assignmentWaiters.Dequeue();

                for (var i = _messageWaiters.Count - 1; i >= 0; i--)
                {
                    if (!_messageWaiters[i].Predicate(message))
                        continue;

                    matched.Add(_messageWaiters[i].Signal);
                    _messageWaiters.RemoveAt(i);
                }
            }

            waiter?.TrySetResult(message.Assignment!);

            foreach (var signal in matched)
                signal.TrySetResult(message);

            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(OrchestratorMessage message) =>
            RecordAsync(message);

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(
            OrchestratorMessage message, CancellationToken cancellationToken) =>
            RecordAsync(message);
    }
}
