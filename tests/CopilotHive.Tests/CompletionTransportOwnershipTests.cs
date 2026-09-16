using CopilotHive.Dashboard;
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
    /// THE ACCEPTED COMPLETION'S RECEIPT IS REAL AND DURABLE: the same invocation that released the
    /// worker and notified the real downstream dispatcher left a receipt row written by the REAL
    /// recorder over the REAL stores, carrying the mapped result and the recorded slot.
    /// </summary>
    /// <remarks>
    /// THE ORDER OF THE ASSERTIONS MATTERS. The downstream evidence is awaited FIRST — production's
    /// own <c>NotifyAsync</c> chain — and the receipt is read afterwards, so this vector really
    /// proves the receipt existed when the notification was observed, rather than merely that the
    /// write happened eventually.
    /// </remarks>
    [Fact]
    public async Task Completion_Accepted_RetainsDurableReceiptAndReleasesOnce()
    {
        var h = Harness.Create();
        await RunAsync(h, async () =>
        {
            h.Assign("task-receipt-valid", model: "assigned-model");

            var result = await h.CompleteAndAwaitDownstreamAsync("task-receipt-valid");

            // THE EXISTING CHECKED RELEASE AND THE ONE NOTIFICATION still happen.
            Assert.False(h.Worker.IsBusy);
            Assert.Null(h.Worker.CurrentTaskId);
            Assert.Null(h.Queue.GetActiveTask("task-receipt-valid"));
            Assert.Equal(1, h.DownstreamHandledCount("task-receipt-valid"));
            Assert.Equal(1, h.TransportNotifications);
            Assert.Equal("task-receipt-valid", result.TaskId);

            // THE REAL RECEIPT ROW is readable, and it carries what the RECORDED assignment and the
            // MAPPED result actually said.
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

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-unrecorded",
                nameof(WorkerCompletionRecordingFailureReason.InvalidContext));

            // NOTHING WAS RELEASED, REMOVED OR NOTIFIED.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-unrecorded", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-unrecorded"));
            Assert.Equal(0, h.TransportNotifications);
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

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-foreign-context",
                nameof(WorkerCompletionRecordingFailureReason.InvalidContext));

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-foreign-context", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-foreign-context"));
            Assert.Equal(0, h.TransportNotifications);
            Assert.Equal(0, h.DownstreamHandledCount("task-foreign-context"));
            Assert.Null(h.ReadReceipt("task-foreign-context"));
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

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-conflict",
                nameof(WorkerCompletionRecordingFailureReason.Conflict));

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-conflict", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-conflict"));
            Assert.Equal(0, h.TransportNotifications);
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
                Assert.Equal(0, h.DownstreamHandledCount("task-indeterminate"));
                Assert.False(h.StreamEnded);

                // The row IS durable (the fault fired after the autocommit) and nothing was inferred.
                var uncertain = h.ReadReceipt("task-indeterminate");
                Assert.NotNull(uncertain);
                var firstStored = uncertain!.FirstStoredAtUtc;

                // ── THE IDENTICAL LATER COMPLETION settles AlreadyStored AND PROCEEDS ────────
                interceptor.Disarm();
                var result = await h.CompleteAndAwaitDownstreamAsync("task-indeterminate");

                Assert.Equal("task-indeterminate", result.TaskId);
                Assert.False(h.Worker.IsBusy);
                Assert.Null(h.Worker.CurrentTaskId);
                Assert.Null(h.Queue.GetActiveTask("task-indeterminate"));
                Assert.Equal(1, h.DownstreamHandledCount("task-indeterminate"));
                Assert.Equal(1, h.TransportNotifications);

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

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-corrupt",
                nameof(WorkerCompletionRecordingFailureReason.StoreError));

            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-corrupt", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-corrupt"));
            Assert.Equal(0, h.TransportNotifications);
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

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-no-recorder",
                nameof(WorkerCompletionRecordingFailureReason.MissingRecorder));

            // NOTHING WAS RELEASED, REMOVED OR NOTIFIED, and NO receipt exists.
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-no-recorder", h.Worker.CurrentTaskId);
            Assert.Equal("assigned-model", h.Worker.CurrentModel);
            Assert.NotNull(h.Queue.GetActiveTask("task-no-recorder"));
            Assert.Equal(0, h.TransportNotifications);
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

            await h.CompleteAndAwaitNotRecordedAsync(
                "task-logger-fault",
                nameof(WorkerCompletionRecordingFailureReason.InvalidContext));

            Assert.True(h.ServiceLogger.ThrowCount > 0, "the armed logger fault never fired");
            Assert.True(h.Worker.IsBusy);
            Assert.Equal("task-logger-fault", h.Worker.CurrentTaskId);
            Assert.NotNull(h.Queue.GetActiveTask("task-logger-fault"));
            Assert.Equal(0, h.TransportNotifications);
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
            h.InvokeHandleTaskCompleteDirectly(stale, "task-aba");

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

        private ChannelStreamReader Reader { get; init; } = null!;

        /// <summary>The RETAINED producer task; the strict teardown joins exactly this instance.</summary>
        private Task StreamTask { get; init; } = null!;

        private readonly CompletionObservations _observations;

        private int _barrierSequence;
        private int _tasksEnqueued;

        private Harness(CompletionObservations observations) => _observations = observations;

        /// <summary>Transport-level notifications the shared notifier emitted (auxiliary observation).</summary>
        public int TransportNotifications => _observations.Count;

        /// <summary>How many tasks the REAL queue accepted — the "did the pipeline advance" probe.</summary>
        public int TasksEnqueued => Volatile.Read(ref _tasksEnqueued);

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
        /// Creates a harness WITH the published-assignment support the sequence vector needs: a
        /// REAL <see cref="WorkerAssignmentPublisher"/> over a REAL file-backed SQLite store
        /// supplied to the service.
        /// </summary>
        public static Harness CreateWithPublishedAssignmentSupport(string dbPath) =>
            CreateCore(withPublishedAssignmentSupport: true, dbPath);

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

        private static Harness CreateCore(
            bool withPublishedAssignmentSupport,
            string? dbPath,
            IInterceptor[]? interceptors = null,
            bool withRecorder = true)
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

            var dispatcherLogger = new SignallingLogger<GoalDispatcher>();
            var dispatcher = new GoalDispatcher(
                goalManager,
                pipelineManager,
                queue,
                new GrpcWorkerGateway(pool),
                completionNotifier,
                dispatcherLogger,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

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

            var serviceLogger = new SignallingLogger<HiveOrchestratorService>();
            var service = new HiveOrchestratorService(
                pool,
                queue,
                pipelineManager,
                completionNotifier,
                dispatcher,
                serviceLogger,
                dashboardNotifier: dashboard,
                assignmentPublisher: assignmentPublisher,
                completionRecorder: withRecorder
                    ? new WorkerCompletionRecorder(
                        recordStores.AssignmentStore, recordStores.ReceiptStore)
                    : null);

            var worker = pool.RegisterWorker(WorkerId, []);
            var reader = new ChannelStreamReader();

            // THE SINGLE CONSUMER OF THE WORKER'S CHANNEL IS THE PRODUCTION PUMP. Publication is
            // observed at the writer the pump forwards to — no competing test reader.
            var writer = new SignallingStreamWriter();
            var streamTask = service.WorkStream(reader, writer, MockContext());

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
            };

            // THE ADVANCE PROBE: a pipeline that advances dispatches its successor through the real
            // PipelineDriver, which enqueues here.
            queue.OnEnqueue = _ => Interlocked.Increment(ref harness._tasksEnqueued);

            return harness;
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

        /// <summary>The retained receipt for a task, read through a FRESH store — the durability evidence.</summary>
        public CompletionReceiptReadResult? ReadReceipt(string taskId) =>
            Stores.ReceiptStore.Load(taskId);

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
        /// Invokes the PRODUCTION <c>HandleTaskComplete</c> directly with a stale pinned instance,
        /// SIMULATING the TOCTOU window between <c>WorkStream</c>'s per-message pinned-instance
        /// check and its dispatch to this handler — a window a replacement re-registering under the
        /// same ID really can land in. Direct invocation is how that interleaving is reproduced
        /// deterministically without adding a production seam.
        /// </summary>
        /// <remarks>
        /// THE CALL ITSELF IS THE BARRIER on this path: it is synchronous, so returning proves the
        /// handler returned, and any exception a removed early return would raise propagates
        /// straight into the vector (unwrapped below) instead of into a swallowed stream fault.
        /// </remarks>
        public void InvokeHandleTaskCompleteDirectly(ConnectedWorker pinned, string taskId)
        {
            var method = typeof(HiveOrchestratorService).GetMethod(
                "HandleTaskComplete",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            try
            {
                method!.Invoke(
                    Service,
                    [
                        pinned,
                        new GrpcTaskComplete
                        {
                            TaskId = taskId,
                            Status = CopilotHive.Shared.Grpc.TaskStatus.Completed,
                            Output = $"output-{taskId}",
                        },
                    ]);
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

            return failure;
        }
    }

    /// <summary>
    /// The transport-side observation of the SHARED notifier: it records each emitted
    /// <see cref="TaskResult"/> so the model assertions have a value to read.
    /// </summary>
    /// <remarks>
    /// IT IS NEVER DOWNSTREAM EVIDENCE. This observer proves only that the TRANSPORT published a
    /// domain result; whether the REAL downstream chain received and classified it is read from
    /// <c>TaskCompletionService</c>'s own production log lines. It is subscribed FIRST so the real
    /// dispatcher stays the last (and therefore awaited) subscriber.
    /// </remarks>
    private sealed class CompletionObservations
    {
        private readonly Queue<TaskCompletionSource<TaskResult>> _waiters = new();
        private int _count;

        /// <summary>How many results the transport published on the shared notifier.</summary>
        public int Count => Volatile.Read(ref _count);

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
        private string? _throwFragment;

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
            lock (_messages)
            {
                _messages.Add(message);

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

            // THE ARMED DIAGNOSTIC FAULT fires AFTER the message was recorded and its waiters were
            // released, so the production code under test really emitted the warning and the FAULT is
            // what its guard has to survive.
            var armed = _throwFragment;
            if (armed is not null && message.Contains(armed, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _throwCount);
                throw LoggerSentinel;
            }
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
            TaskCompletionSource<TaskAssignment>? waiter = null;
            lock (_messages)
            {
                _messages.Add(message);

                if (message.Assignment is not null && _assignmentWaiters.Count > 0)
                    waiter = _assignmentWaiters.Dequeue();
            }

            waiter?.TrySetResult(message.Assignment!);
            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(OrchestratorMessage message) =>
            RecordAsync(message);

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(
            OrchestratorMessage message, CancellationToken cancellationToken) =>
            RecordAsync(message);
    }
}
