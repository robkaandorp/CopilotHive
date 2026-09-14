using System.Data.Common;
using System.Reflection;
using System.Runtime.ExceptionServices;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// THE READY-DRIVEN DELIVERY SLICE, end to end through the REAL
/// <see cref="HiveOrchestratorService.WorkStream"/> → <c>HandleWorkerReady</c> path with the
/// PRODUCTION <see cref="WorkerAssignmentPublisher"/> over a REAL file-backed SQLite database.
/// <para>
/// THESE ARE THE GOAL'S CORE VECTORS. They pin the ordering contract that is the whole point of the
/// slice: an assignment becomes available on the delivery transport ONLY AFTER its
/// assignment-context row is confirmed, and a refused record leaves the delivery blocked while the
/// pinned worker, its stream, the active task and the busy state all stay untouched.
/// </para>
/// <para>
/// WHERE THE OBSERVATION IS TAKEN, AND WHY. The transport's own channel pump forwards every
/// queued <see cref="OrchestratorMessage"/> to the server stream writer it was given, and the
/// stream's teardown closes the worker's message channel. The stream writer is therefore the ONE
/// place where "was an assignment delivered to this worker" is both observable and stable past
/// teardown — reading the channel itself after the stream ends would always look empty. Every
/// delivery assertion below is made there.
/// </para>
/// <para>
/// THE COLLABORATORS' OWN CONTRACTS ARE COVERED ELSEWHERE and are reused, not repeated:
/// <c>WorkerAssignmentContextStoreTests</c> owns the store's write/duplicate/conflict/uncertainty
/// matrix and <c>WorkerAssignmentPublisherTests</c> owns the publisher's focused outcome mapping.
/// Here the store and the publisher are the REAL collaborators of the real transport path.
/// </para>
/// </summary>
/// <remarks>
/// EVERY FIXTURE IS ISOLATED: a per-instance temporary database FILE, connection strings with
/// <c>Pooling=False</c> so disposing the factory really releases the file handles, a finite SQLite
/// busy timeout, DETERMINISTIC synchronization (the transport's own assignment-forwarded signal, the
/// production warning's own log signal, and the stream's completion — never a sleep), and best-effort
/// cleanup in <see cref="Dispose"/>.
/// </remarks>
public sealed class WorkerAssignmentPublicationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"copilothive-wap-ready-{Guid.NewGuid():N}.db");

    private readonly List<IDisposable> _fixtures = [];

    /// <summary>Upper bound for every await in these vectors — never a fixed delay.</summary>
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    public WorkerAssignmentPublicationTests()
    {
        using var connection = OpenConnection();
        using var context = ContextOn(connection);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
        {
            try
            {
                fixture.Dispose();
            }
            catch
            {
                // Best-effort — a leftover fixture must never fail a test.
            }
        }

        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
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

    // ───────────────────────────── fixture plumbing ─────────────────────────────

    private string ConnectionString => $"Data Source={_dbPath};Pooling=False;Default Timeout=15";

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static CopilotHiveDbContext ContextOn(SqliteConnection connection, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new CopilotHiveDbContext(builder.Options);
    }

    /// <summary>
    /// Builds the store factory for this fixture. When an <paramref name="observer"/> is supplied it
    /// is wired to THIS file and installed as a commit interceptor, so the observation really fires
    /// inside the production INSERT.
    /// </summary>
    private ReadyFactory NewFactory(CommitObservationInterceptor? observer = null, params IInterceptor[] interceptors)
    {
        var all = new List<IInterceptor>(interceptors);
        if (observer is not null)
        {
            observer.ProbeConnectionString = ConnectionString;
            all.Add(new CommitObservingTransactionInterceptor(observer));
        }

        var factory = new ReadyFactory(ConnectionString, [.. all]);
        _fixtures.Add(factory);
        return factory;
    }

    /// <summary>The recorded row's task id, read through a fresh connection; <c>null</c> when absent.</summary>
    private string? RawAssignmentTaskId()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_id FROM worker_assignment_contexts LIMIT 1";
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// The recorded row for ONE specific task id, read through a fresh connection; <c>null</c> when
    /// that task has no row (a decoy row for another id therefore never satisfies this probe).
    /// </summary>
    private string? RawAssignmentTaskId(string taskId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_id FROM worker_assignment_contexts WHERE task_id = $t";
        command.Parameters.AddWithValue("$t", taskId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// THE ONE LIFECYCLE every vector in this fixture runs through: the body, then the harness's
    /// SHARED teardown, on EVERY path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS RATHER THAN A PER-TEST <c>finally</c>: a per-test release call is only reached
    /// on the SUCCESS path, so a failing load-bearing assertion (or an intentional mutant) would leave
    /// the transport pump parked and the <c>WorkStream</c> producer LIVE and UNJOINED. Routing every
    /// vector through one helper means a future test cannot forget the release — the teardown owns it.
    /// </para>
    /// <para>
    /// THE PRIMARY FAILURE STAYS AUTHORITATIVE. The body's exception is captured and RETHROWN
    /// UNCHANGED (via <see cref="ExceptionDispatchInfo"/>, so its original message and stack survive),
    /// and the teardown's own outcome is only surfaced when the body SUCCEEDED. A genuine cleanup
    /// failure is therefore never silently swallowed, and it can never replace the assertion message
    /// the reviewer needs to see.
    /// </para>
    /// </remarks>
    /// <param name="harness">The harness whose shared teardown must run.</param>
    /// <param name="body">The vector's assertions.</param>
    private static async Task RunAsync(ReadyHarness harness, Func<Task> body)
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

        // THE TEARDOWN RUNS ON EVERY PATH and never throws — it reports instead.
        var cleanupFailure = await harness.StopAsync();

        // THE PRIMARY FAILURE WINS: rethrown with its ORIGINAL message and stack.
        primary?.Throw();

        // No primary failure, so a genuine cleanup failure must still surface.
        if (cleanupFailure is not null)
            throw cleanupFailure;

        // THE PRODUCER CLEANUP POST-CONDITIONS, asserted for every green vector: nothing is left live.
        Assert.True(harness.PumpReleasedAfterTeardown, "the transport pump was not released by the teardown");
        Assert.True(harness.StreamJoined, "the stream task was not joined by the teardown");
        Assert.True(
            harness.StreamTaskCompletedAfterTeardown,
            "the WorkStream producer task is still live after teardown");
        Assert.True(
            harness.WorkerRemovedAfterTeardown,
            "the stream's finally did not run its pinned-worker cleanup");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (1) THE REAL READY PATH — record THEN publish, observed in that order
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE CORE ORDERING VECTOR. A real Ready message through a real
    /// <see cref="HiveOrchestratorService.WorkStream"/>, a real pipeline (routing + pointer + Pending
    /// slot), the PRODUCTION publisher and a real file-backed store.
    /// <para>
    /// It records the EXACT task/goal/worker/slot/role/model and delivers the MATCHING assignment;
    /// the commit-instant observer proves the row was confirmed while NO assignment had yet reached
    /// the transport, so no Assignment can be available before the record. A fresh-context readback
    /// reproduces the row.
    /// </para>
    /// <para>
    /// THE OBSERVATION CANNOT LOSE AN ASSIGNMENT. The harness first PARKS the transport's channel
    /// pump inside the response writer (see <see cref="ReadyHarness.ParkTransportPumpAsync"/>), so
    /// while the record is committing the pump is provably unable to dequeue. Anything the publisher
    /// writes therefore stays visible in the worker's channel, and the single observation point also
    /// counts writer ENTRIES (marked before the message is recorded), which covers the
    /// entered-but-not-yet-recorded window. The two facts are read at ONE instant through
    /// <see cref="TransportHandoff.HasAssignmentInFlight"/> — never as two independent polls.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_RecordsExactContextThenPublishesMatchingAssignment()
    {
        var observer = new CommitObservationInterceptor();
        var h = await ReadyHarness.CreateAsync(NewFactory(observer), observer);

        await RunAsync(h, async () =>
        {
            // THE PUMP IS PARKED FIRST: no dequeue can race the commit observation.
            await h.ParkTransportPumpAsync();

            await h.SendReadyAndAwaitPublisherReturnedAsync();

            // ── THE COMMIT-INSTANT OBSERVATION: the row was confirmed while nothing had been sent. ──
            var observed = Assert.Single(observer.Observations);
            Assert.Equal(h.TaskId, observed.DeliveredTaskId);
            Assert.True(
                observed.DeliveredRowPresentAtCommit,
                "the delivered task's assignment-context row was NOT present at the commit instant — " +
                "the observation did not land inside its record");
            Assert.False(
                observed.AssignmentInFlightAtCommit,
                "an assignment was already in flight to the worker AT THE COMMIT INSTANT — the record " +
                "must be confirmed before anything is published");
            Assert.Equal(0, observed.TransportEntriesAtCommit);
            Assert.False(
                observed.ChannelQueuedAtCommit,
                "an assignment was queued on the pinned worker's channel at the commit instant");
            Assert.True(
                CommitObservationInterceptor.HasConfirmedRowBeforeAssignment(
                    observed.AssignmentInFlightAtCommit, observed.DeliveredRowPresentAtCommit),
                "the row-before-publication invariant was violated at the commit instant");

            // THE PARKED PUMP REALLY WAS PARKED for the whole record — otherwise the observation
            // above could have missed an in-flight assignment.
            Assert.True(h.PumpWasParkedThroughoutRecord, "the transport pump was not parked across the record");

            // ── RELEASE and observe THE DELIVERY: exactly one assignment, naming the delivered task. ──
            await h.ReleaseTransportPumpAndAwaitDeliveryAsync();
            var published = Assert.Single(h.Writer.Assignments);
            Assert.Equal(h.TaskId, published.TaskId);
            Assert.Equal(h.GoalId, published.GoalId);
            Assert.Equal("copilot/claude-sonnet-4.6", published.Model);
            Assert.Equal(h.Prompt, published.Prompt);

            // ── NO RECORDING REFUSAL WAS LOGGED: this delivery really was recorded and sent. ──
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));

            // ── THE FRESH-CONTEXT READBACK reproduces every delivered value (observation only). ──
            var readback = new WorkerAssignmentContextStore(
                NewFactory(), NullLogger<WorkerAssignmentContextStore>.Instance).Load(h.TaskId);
            Assert.NotNull(readback);
            Assert.Equal(h.GoalId, readback!.Context.GoalId);
            Assert.Equal(h.Worker.Id, readback.Context.WorkerId);
            Assert.Equal(WorkerRole.Coder, readback.Context.Role);
            Assert.Equal(h.TaskId, readback.Context.Slot.TaskId);
            Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), readback.Context.Slot.Position);
            Assert.Equal(1, readback.Context.Slot.Attempt);
            Assert.Equal("copilot/claude-sonnet-4.6", readback.Context.Model);
        });
    }

    /// <summary>
    /// THE DELIBERATELY UNGATED CONTROL, REJECTED BY THE SAME OBSERVER — the load-bearing proof that
    /// the ordering assertion is not vacuous.
    /// <para>
    /// The control publisher delivers an assignment and then COMMITS (it records a decoy row rather
    /// than the delivered task's), so the identical observation point fires with an assignment
    /// ALREADY IN FLIGHT and the delivered task's row ABSENT. The same invariant therefore rejects it
    /// on a REAL post-delivery commit — not merely on the absence of any commit. The pump is parked
    /// exactly as in the positive vector, so the in-flight assignment cannot be lost.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_UngatedControl_IsRejectedByTheRowBeforePublicationObserver()
    {
        var observer = new CommitObservationInterceptor();
        var factory = NewFactory(observer);

        // THE CONTROL: publishes FIRST, then commits a decoy row through the SAME observed store.
        var control = new UngatedPublishThenCommitPublisher(
            new WorkerAssignmentContextStore(factory, NullLogger<WorkerAssignmentContextStore>.Instance));
        var h = await ReadyHarness.CreateAsync(factory, observer, publisher: control);

        await RunAsync(h, async () =>
        {
            await h.ParkTransportPumpAsync();
            await h.SendReadyAndAwaitPublisherReturnedAsync();

            // ── THE COMMIT REALLY HAPPENED AFTER THE DELIVERY, and the observer saw it. ──
            Assert.Equal(1, control.CommitCount);
            var observed = Assert.Single(observer.Observations);
            Assert.Equal(h.TaskId, observed.DeliveredTaskId);

            // The assignment was ALREADY in flight when that commit landed…
            Assert.True(
                observed.AssignmentInFlightAtCommit,
                "the ungated control's assignment was not observed in flight — the control is vacuous");
            // …and the DELIVERED task was never recorded.
            Assert.False(observed.DeliveredRowPresentAtCommit);

            // ── THE SAME INVARIANT REJECTS IT on that post-delivery commit. ──
            Assert.False(
                CommitObservationInterceptor.HasConfirmedRowBeforeAssignment(
                    observed.AssignmentInFlightAtCommit, observed.DeliveredRowPresentAtCommit),
                "the ungated control delivered an assignment before confirming the delivered task's " +
                "row, yet the row-before-publication invariant accepted it");

            // The control really delivered — asserted after releasing the parked pump.
            await h.ReleaseTransportPumpAndAwaitDeliveryAsync();
            Assert.Equal(h.TaskId, Assert.Single(h.Writer.Assignments).TaskId);
            Assert.Null(RawAssignmentTaskId(h.TaskId));
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) THE REAL RECORDING REFUSAL — blocked, but nothing unwound
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A REAL Ready recording refusal through the REAL stream: the delivery is BLOCKED — no
    /// assignment, no success log — while the pinned worker, the stream, the active task and the busy
    /// assignment all stay INTACT. An unrelated pipeline is untouched and the task is NOT requeued.
    /// <para>
    /// The refusal is GENUINE (a seeded differing context → <c>Conflict</c>), so the production
    /// decision path is exercised rather than a stub's.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_RecordingRefusal_LeavesWorkerStreamAndAssignmentIntact()
    {
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null);

        await RunAsync(h, async () =>
        {
            // THE GENUINE REFUSAL: a DIFFERENT worker already owns this task id's recorded context.
            h.SeedConflictingRecord();

            // A SECOND, independent pipeline that must be untouched by the refusal.
            var otherPipeline = h.Manager.CreatePipeline(
                new Goal { Id = $"goal-other-{Guid.NewGuid():N}", Description = "unrelated goal" });

            await h.SendReadyAndAwaitBlockedAsync();

            // ── THE HANDLED DISPOSITION RETURNED NORMALLY: the stream did not fault. ──
            Assert.False(
                h.StreamFaulted,
                $"the handled recording refusal must not fault the stream: {h.Logger.Messages.Count} log(s)");

            // ── NOTHING WAS DELIVERED to the worker. ──
            Assert.Empty(h.Writer.Assignments);
            Assert.Empty(h.Writer.Messages);

            // ── NO SUCCESS LOG; the actionable disposition warning is present instead. ──
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));
            Assert.Contains(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));

            // ── THE STREAM AND THE WORKER SURVIVE: still in the pool, still busy on the task. ──
            Assert.Same(h.Worker, h.Pool.GetWorker(h.Worker.Id));
            Assert.True(h.Worker.IsBusy, "the blocked delivery must keep the worker's busy state");
            Assert.Equal(h.TaskId, h.Worker.CurrentTaskId);
            Assert.Equal(WorkerRole.Coder, h.Worker.Role);

            // ── THE ACTIVE TASK IS RETAINED on its own pipeline… ──
            Assert.Equal(h.TaskId, h.Pipeline.ActiveTaskId);

            // ── …and the UNRELATED pipeline is completely unaffected. ──
            Assert.Equal(GoalPhase.Planning, otherPipeline.Phase);
            Assert.Null(otherPipeline.ActiveTaskId);

            // ── NO REQUEUE: the blocked task was not put back for another worker. ──
            Assert.Null(h.Queue.TryDequeueAny());
        });
    }

    /// <summary>
    /// THE EXISTING EXPLICIT CANCELLATION PATH STILL RELEASES A BLOCKED ASSIGNMENT, with the task
    /// timeout policy DISABLED — no fabricated completion, no automatic recovery.
    /// <para>
    /// This is the operator's real escape hatch: <c>GoalDispatcher.CancelGoalAsync</c> fails the goal
    /// and removes its pipeline. The test proves the blocked delivery is genuinely still HELD first,
    /// then that cancellation works, then that the route is gone so a further Ready delivers nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task BlockedReadyAssignment_ExistingGoalCancellation_RemainsUsable()
    {
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null);

        await RunAsync(h, async () =>
        {
            // ── THE TIMEOUT POLICY IS DISABLED: 0 minutes, so no automatic reclaim exists. ──
            Assert.Equal(0, h.Config.Orchestrator.WorkerTaskTimeoutMinutes);

            h.SeedConflictingRecord();
            await h.SendReadyAndAwaitBlockedAsync();

            // The blocked delivery is genuinely HELD: still active, still busy, nothing delivered.
            Assert.Equal(h.TaskId, h.Pipeline.ActiveTaskId);
            Assert.True(h.Worker.IsBusy);
            Assert.Empty(h.Writer.Assignments);

            // ── THE EXISTING EXPLICIT CANCELLATION PATH. ──
            Assert.True(await h.Dispatcher.CancelGoalAsync(h.GoalId, TestContext.Current.CancellationToken));

            // It failed the goal and deregistered the pipeline, in memory and for the routing lookup.
            Assert.Equal(GoalPhase.Failed, h.Pipeline.Phase);
            Assert.Null(h.Manager.GetByTaskId(h.TaskId));
            Assert.Null(h.Manager.GetByGoalId(h.GoalId));

            // ── THE ROUTE IS NOW UNRESOLVABLE: a further Ready delivers NOTHING — no fabricated
            //    recovery, no wrong-goal failure, and still no success log. ──
            //
            // THE SECOND READY IS GATED ON ITS OWN WARNING. The helper allocates a FRESH signal per
            // call, so this wait cannot be satisfied by the FIRST refusal's already-emitted warning —
            // the assertions below provably run AFTER this Ready was processed. The warning COUNT is
            // asserted to prove exactly that: one warning per processed Ready.
            var warningsBeforeSecondReady =
                h.Logger.Messages.Count(m => m.Contains("assignment blocked", StringComparison.Ordinal));
            Assert.Equal(1, warningsBeforeSecondReady);

            h.Queue.Enqueue(h.DeliveredTask);
            await h.SendReadyAndAwaitBlockedAsync();

            Assert.Equal(
                2,
                h.Logger.Messages.Count(m => m.Contains("assignment blocked", StringComparison.Ordinal)));
            Assert.Empty(h.Writer.Assignments);
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// MISSING PUBLISHER FAILS CLOSED ON THE REAL PATH. A production transport service constructed
    /// WITHOUT a publisher (the unrelated-fixture shape) produces the SAME explicit no-send
    /// disposition — and NEVER the old raw write, which was removed.
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_MissingPublisher_NoSendNoRawWriteFallback()
    {
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null, withPublisher: false);

        await RunAsync(h, async () =>
        {
            await h.SendReadyAndAwaitBlockedAsync();

            // NOTHING reached the transport — a raw-write fallback would have delivered an assignment.
            Assert.Empty(h.Writer.Messages);
            Assert.Empty(h.Writer.Assignments);
            Assert.False(
                h.Worker.MessageChannel.Reader.TryRead(out _),
                "an assignment reached the channel without a publisher — the old raw write is back");

            Assert.Contains(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));

            // The delivery is still held, not released.
            Assert.Equal(h.TaskId, h.Pipeline.ActiveTaskId);
            Assert.True(h.Worker.IsBusy);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) THE WARNING CONTRACT — exact wording, and guarded emission
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE EXACT DISPOSITION WORDING, emitted as a WARNING, and the GUARD around it: a logger that
    /// throws while emitting the warning must not escape — the handled refusal still returns normally
    /// and nothing is delivered.
    /// </summary>
    [Fact]
    public async Task RecordingRefusal_Warning_UsesExactWording_AndIsGuardedAgainstAThrowingLogger()
    {
        var throwingLogger = new ThrowingOnWarningReadyLogger();
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null, logger: throwingLogger);

        await RunAsync(h, async () =>
        {
            h.SeedConflictingRecord();

            // MUST NOT THROW: the guarded warning swallows the logger failure, and the disposition
            // (blocked, retained) stands.
            await h.SendReadyAndAwaitBlockedAsync();

            Assert.Empty(h.Writer.Assignments);
            Assert.True(h.Worker.IsBusy);
            Assert.Equal(h.TaskId, h.Pipeline.ActiveTaskId);
        });

        // THE EXACT WORDING, asserted against the message the throwing logger actually received.
        var warning = Assert.Single(
            throwingLogger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
        Assert.Contains(
            "assignment blocked; no assignment published; task retained; cancel the goal or use " +
            "configured recovery",
            warning,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Assignment published", warning, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) A POST-RECORD SEND FAULT KEEPS ITS EXISTING TEARDOWN SEMANTICS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A POST-RECORD CHANNEL FAULT IS NOT A RECORDING REFUSAL: the channel-closed fault is
    /// deliberately outside the handler's catch, so the stream terminates exactly as it did before
    /// this slice — while the row stays recorded and no success log is emitted.
    /// </summary>
    [Fact]
    public async Task WorkStream_Ready_PostRecordChannelFault_PropagatesAndKeepsRow()
    {
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null);

        await RunAsync(h, async () =>
        {
            // Complete the channel so the POST-RECORD write fails.
            Assert.True(h.Worker.MessageChannel.Writer.TryComplete());

            var streamFault = await h.SendReadyAndAwaitStreamEndAsync();

            // The fault propagated UNCHANGED — never re-labelled as a recording refusal, and not
            // reported as the handled blocked disposition.
            Assert.IsType<System.Threading.Channels.ChannelClosedException>(streamFault);
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));

            // The row WAS recorded (the record precedes the send) and no success log happened.
            Assert.Equal(h.TaskId, RawAssignmentTaskId());
            Assert.DoesNotContain(
                h.Logger.Messages, m => m.Contains("Assignment published", StringComparison.Ordinal));
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) THE FAILURE-PATH PRODUCER CLEANUP CONTRACT
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE TEARDOWN CONTRACT ON THE FAILURE PATH — the guarantee every vector above depends on.
    /// <para>
    /// A load-bearing assertion is made to FAIL while the transport pump is PARKED, which is exactly
    /// the state the ordering vectors (and the publish-before-record mutant) leave behind: the test
    /// never reaches its own release call. The shared teardown must still release the pump
    /// unconditionally, join the ORIGINAL stream task within a finite bound, and let the PRIMARY
    /// assertion exception through UNCHANGED.
    /// </para>
    /// <para>
    /// WITHOUT the unconditional release this test HANGS for the full bound and then reports a
    /// teardown leak — so it fails loudly instead of leaving a live <c>WorkStream</c> behind.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Teardown_WhenABodyAssertionFailsWithThePumpParked_ReleasesJoinsAndPreservesTheOriginalFailure()
    {
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null);
        const string primaryMessage = "PRIMARY-ASSERTION-SENTINEL: the load-bearing check failed";

        // THE FAILURE PATH, driven explicitly: the pump is parked and then the body throws, so the
        // body's own release call is never reached.
        var primary = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(
            () => RunAsync(h, async () =>
            {
                await h.ParkTransportPumpAsync();

                // The pump really is parked INSIDE the writer at this point.
                Assert.True(h.Writer.PumpStillParked, "the pump was not parked — the vector is vacuous");

                Assert.Fail(primaryMessage);
            }));

        // ── (1) THE PRIMARY FAILURE SURVIVED, with its ORIGINAL message — not a cleanup exception. ──
        Assert.Contains(primaryMessage, primary.Message, StringComparison.Ordinal);
        Assert.IsNotType<TimeoutException>(primary);
        Assert.DoesNotContain("TEARDOWN LEAK", primary.Message, StringComparison.Ordinal);

        // ── (2) THE PRODUCER CLEANUP RAN ANYWAY, on the failure path. ──
        Assert.True(h.PumpReleasedAfterTeardown, "the teardown did not release the parked pump");
        Assert.False(h.Writer.PumpStillParked, "a pump is STILL parked inside the transport writer");

        // ── (3) THE ORIGINAL STREAM TASK WAS JOINED — no live producer remains. ──
        Assert.True(h.StreamJoined, "the teardown did not join the stream task");
        Assert.True(h.StreamTaskCompletedAfterTeardown, "the WorkStream producer task is still live");

        // ── (4) THE PINNED WORKER'S OWN CLEANUP RAN (the stream's finally removed it). ──
        Assert.True(h.WorkerRemovedAfterTeardown, "the stream's finally did not remove the pinned worker");
    }

    /// <summary>
    /// A GENUINE CLEANUP FAILURE IS NOT SWALLOWED when the body SUCCEEDED: a pump that can never be
    /// released (its release gate is neutralised) makes the bounded join exceed its bound, and the
    /// teardown reports that leak loudly instead of ignoring it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the counterpart of the vector above: together they pin BOTH halves of the contract —
    /// a cleanup failure never replaces a primary failure, and it never disappears when there is no
    /// primary failure. The bound is shortened for this vector alone so the proof stays fast.
    /// </para>
    /// <para>
    /// THIS VECTOR DELIBERATELY STALLS A REAL PRODUCER, so it owns a stricter obligation than any
    /// other: its OWN restoration must be UNCONDITIONAL. Everything after
    /// <see cref="RecordingStreamWriter.IgnoreReleaseForLeakProof"/> runs inside a <c>try</c> whose
    /// <c>finally</c> restores the normal bound, re-enables release and performs the REAL join — so
    /// even a failing <c>TEARDOWN LEAK</c> expectation cannot leave the stalled
    /// <c>WorkStream</c> alive. The join itself is then PROVEN (not merely attempted): the original
    /// stream task must be observably completed afterwards.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Teardown_WhenTheProducerCannotBeReleased_ReportsTheLeakLoudly()
    {
        var h = await ReadyHarness.CreateAsync(NewFactory(), observer: null);
        h.UseShortJoinBoundForLeakProof();

        // THE UNRELEASABLE PUMP: the writer ignores the teardown's release, so the producer really
        // cannot terminate — the exact condition that must be reported rather than ignored.
        h.Writer.IgnoreReleaseForLeakProof();

        ExceptionDispatchInfo? primary = null;
        Exception? restorationFailure = null;
        var joined = false;

        try
        {
            var leak = await Assert.ThrowsAsync<TimeoutException>(
                () => RunAsync(h, async () =>
                {
                    await h.ParkTransportPumpAsync();
                    Assert.True(h.Writer.PumpStillParked);
                    // THE BODY SUCCEEDS — so nothing can mask the cleanup failure.
                }));

            // THE LOUD REPORT during the ignored-release window.
            Assert.Contains("TEARDOWN LEAK", leak.Message, StringComparison.Ordinal);
            Assert.Contains(h.Worker.Id, leak.Message, StringComparison.Ordinal);
            Assert.False(h.StreamJoined, "the join must be reported as failed when the producer cannot end");
        }
        catch (Exception ex)
        {
            primary = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            // ── THE UNCONDITIONAL RESTORATION. It runs even when an assertion above failed —
            //    including a failing TEARDOWN LEAK expectation — so this vector can never leave the
            //    producer it deliberately stalled alive. ──
            h.RestoreNormalJoinBound();
            h.Writer.StopIgnoringRelease();
            h.Writer.ReleaseParked();

            try
            {
                // THE REAL JOIN. It FAILS LOUDLY on a bounded-wait timeout — a timeout means the
                // producer is still live, which is exactly what this vector must never leave behind.
                await h.AwaitStreamTerminationForLeakProofAsync();
                joined = true;
            }
            catch (Exception ex)
            {
                restorationFailure = ex;
            }
        }

        // THE PRIMARY FAILURE WINS, with its ORIGINAL message and stack — cleanup never masks it.
        primary?.Throw();

        // No primary failure, so a genuine restoration failure must still surface.
        if (restorationFailure is not null)
            throw restorationFailure;

        // ── THE POST-RESTORATION JOIN IS PROVEN, not merely attempted. ──
        Assert.True(joined, "the leak-proof vector did not complete its real join");
        Assert.True(
            h.StreamTaskIsCompletedNow,
            "the leak-proof vector left its deliberately stalled WorkStream producer LIVE");
        Assert.True(h.Writer.ParkReleased, "the leak-proof vector did not re-enable the release");
        Assert.False(h.Writer.PumpStillParked, "a pump is STILL parked after the leak-proof restoration");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  harness
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REAL TRANSPORT HARNESS: a live <see cref="HiveOrchestratorService"/> over a REAL
    /// <see cref="GoalDispatcher"/>, a real <see cref="GoalPipelineManager"/>, real
    /// <see cref="WorkerPool"/> / <see cref="TaskQueue"/>, the PRODUCTION
    /// <see cref="WorkerAssignmentPublisher"/> over the fixture's file-backed store, and ONE pipeline
    /// whose routing, pointer and Pending slot are ALL genuinely registered.
    /// </summary>
    private sealed class ReadyHarness
    {
        public required HiveOrchestratorService Service { get; init; }
        public required WorkerAssignmentPublisher Publisher { get; init; }
        public required GoalDispatcher Dispatcher { get; init; }
        public required GoalPipelineManager Manager { get; init; }
        public required GoalPipeline Pipeline { get; init; }
        public required WorkerPool Pool { get; init; }
        public required TaskQueue Queue { get; init; }
        public required ConnectedWorker Worker { get; init; }
        public required WorkTask DeliveredTask { get; init; }
        public required string GoalId { get; init; }
        public required string TaskId { get; init; }
        public required string Prompt { get; init; }
        public required HiveConfigFile Config { get; init; }
        public required WorkerAssignmentContextStore Store { get; init; }
        public required CapturingReadyLogger Logger { get; init; }

        /// <summary>THE DELIVERY OBSERVATION: everything the transport forwarded to the worker.</summary>
        public required RecordingStreamWriter Writer { get; init; }

        private ChannelStreamReader Reader { get; init; } = null!;
        private Task StreamTask { get; init; } = null!;

        /// <summary>Whether the stream terminated with an exception rather than draining.</summary>
        public bool StreamFaulted { get; private set; }

        /// <summary>The finite bound the shared teardown joins the stream task with.</summary>
        private TimeSpan _joinBound = BoundedWait;

        /// <summary>
        /// Builds the harness. Every collaborator is REAL except the publisher seam, which is
        /// injectable so the missing-publisher and ungated-control vectors can vary it.
        /// </summary>
        public static async Task<ReadyHarness> CreateAsync(
            IDbContextFactory<CopilotHiveDbContext> storeFactory,
            CommitObservationInterceptor? observer,
            CapturingReadyLogger? logger = null,
            IWorkerAssignmentPublisher? publisher = null,
            bool withPublisher = true)
        {
            var pool = new WorkerPool();
            var queue = new TaskQueue();
            var manager = new GoalPipelineManager();

            var goal = new Goal
            {
                Id = $"goal-ready-{Guid.NewGuid():N}",
                Description = "Ready publication goal",
                RepositoryNames = ["test-repo"],
            };

            var goalManager = new GoalManager();
            goalManager.AddSource(new ReadyGoalSource(goal));
            await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

            // THE TIMEOUT POLICY IS DISABLED: 0 minutes makes the timed-out-task reclaim an immediate
            // no-op, so nothing here can automatically release a blocked assignment.
            var config = new HiveConfigFile
            {
                Orchestrator = new OrchestratorConfig { WorkerTaskTimeoutMinutes = 0 },
            };

            var completionNotifier = new TaskCompletionNotifier();
            var dispatcher = new GoalDispatcher(
                goalManager,
                manager,
                queue,
                new GrpcWorkerGateway(pool),
                completionNotifier,
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
                config: config,
                dashboardNotifier: new DashboardNotifier());

            var store = new WorkerAssignmentContextStore(
                storeFactory, NullLogger<WorkerAssignmentContextStore>.Instance);
            var realPublisher = new WorkerAssignmentPublisher(manager, pool, store);
            var readyLogger = logger ?? new CapturingReadyLogger();

            var service = new HiveOrchestratorService(
                pool,
                queue,
                manager,
                completionNotifier,
                dispatcher,
                readyLogger,
                dashboardNotifier: new DashboardNotifier(),
                assignmentPublisher: withPublisher ? publisher ?? realPublisher : null);

            // THE GENUINE OWNERSHIP: routing + pointer + Pending slot, ALL really registered.
            const string prompt = "do the ready work";
            var pipeline = manager.CreatePipeline(goal);
            pipeline.AdvanceTo(GoalPhase.Coding);
            var built = pipeline.AllocateAttemptAndRegisterSlot(
                $"task-ready-{Guid.NewGuid():N}", new WorkSlotPosition(1, GoalPhase.Coding, 1));
            pipeline.SetActiveTask(built.TaskId);
            manager.RegisterTask(built.TaskId, goal.Id);

            var worker = pool.RegisterWorker($"worker-ready-{Guid.NewGuid():N}", []);

            // THE DELIVERED TASK — the ACTUAL instance the queue's own dequeue path hands over.
            var task = new WorkTask
            {
                TaskId = built.TaskId,
                GoalId = goal.Id,
                GoalDescription = goal.Description,
                Prompt = prompt,
                Role = WorkerRole.Coder,
                Model = "copilot/claude-sonnet-4.6",
                Repositories =
                    [new TargetRepository { Name = "test-repo", Url = "https://example.invalid/repo" }],
            };
            queue.Enqueue(task);

            var writer = new RecordingStreamWriter();
            var reader = new ChannelStreamReader();

            // THE SINGLE HAND-OFF POINT is wired BEFORE the stream starts: the writer marks its
            // entries into the observer's hand-off and the hand-off reads the channel's queued state,
            // so the commit-instant observation reads both halves at one instant.
            if (observer is not null)
            {
                observer.DeliveredTaskId = built.TaskId;
                observer.Handoff.ChannelHasAssignment = () =>
                    worker.MessageChannel.Reader.TryPeek(out var queued) && queued.Assignment is not null;
                writer.Handoff = observer.Handoff;
            }

            var streamTask = service.WorkStream(reader, writer, MockContext());

            return new ReadyHarness
            {
                Service = service,
                Publisher = realPublisher,
                Dispatcher = dispatcher,
                Manager = manager,
                Pipeline = pipeline,
                Pool = pool,
                Queue = queue,
                Worker = worker,
                DeliveredTask = task,
                GoalId = goal.Id,
                TaskId = built.TaskId,
                Prompt = prompt,
                Config = config,
                Store = store,
                Logger = readyLogger,
                Writer = writer,
                Reader = reader,
                StreamTask = streamTask,
            };
        }

        /// <summary>
        /// PARKS THE TRANSPORT'S CHANNEL PUMP inside the response writer and waits until it is
        /// provably parked. While parked the pump CANNOT dequeue the worker's channel, so anything
        /// the publisher writes stays observable — this is what makes the commit-instant observation
        /// unable to lose an assignment between channel dequeue and transport recording.
        /// </summary>
        /// <remarks>
        /// The pump is the background loop <c>WorkStream</c> starts; it idles until the stream's FIRST
        /// message pins the worker, then spins on the channel and forwards each message to the
        /// response writer. Parking it INSIDE the writer means the message it holds has already been
        /// dequeued but not yet recorded — the exact window the review called out — and the writer
        /// marks that ENTRY before parking, so the single observation point still counts it (see
        /// <see cref="TransportHandoff"/>).
        /// <para>
        /// THE PIN COMES FIRST, deterministically: a Progress message (which touches no assignment
        /// state — <c>HandleTaskProgress</c> only logs) pins the worker so the pump begins reading.
        /// Awaiting <see cref="RecordingStreamWriter.Parked"/> then proves BOTH that the pin was
        /// processed and that the pump is now held inside the writer.
        /// </para>
        /// </remarks>
        public async Task ParkTransportPumpAsync()
        {
            Writer.ParkOnNextWrite();

            // (1) PIN the worker so the transport's channel pump starts reading at all.
            Reader.Push(new WorkerMessage
            {
                WorkerId = Worker.Id,
                Progress = new TaskProgress { TaskId = TaskId, Message = "pin the stream" },
            });

            // (2) Queue a NON-assignment message for the pump to carry into the writer, where it parks.
            await Worker.MessageChannel.Writer.WriteAsync(
                new OrchestratorMessage
                {
                    UpdateAgents = new UpdateAgents { AgentsMdContent = "park", Role = "coder" },
                },
                TestContext.Current.CancellationToken);

            await Writer.Parked.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Releases the parked pump and waits for the assignment to be recorded by the transport —
        /// the delivery observation, taken after the ordering proof has already been captured.
        /// </summary>
        public async Task ReleaseTransportPumpAndAwaitDeliveryAsync()
        {
            Writer.ReleaseParked();
            await Writer.AssignmentForwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Whether the pump stayed parked for the WHOLE record — i.e. it did not resume (and so could
        /// not have drained an assignment invisibly) before the commit observation was taken.
        /// </summary>
        public bool PumpWasParkedThroughoutRecord => Writer.ParkedThroughout;

        /// <summary>
        /// Pushes a real Ready and waits until HandleWorkerReady's publisher invocation has RETURNED
        /// — signalled by the production success log (or, for the ungated control, by the control's
        /// own completion signal). This never waits on the transport, so it is valid while the pump
        /// is parked.
        /// </summary>
        public async Task SendReadyAndAwaitPublisherReturnedAsync()
        {
            var signal = Logger.WaitForPublishedLog();
            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>Seeds a DIFFERENT context for the delivered task id — the genuine Conflict refusal.</summary>
        public void SeedConflictingRecord()
        {
            var slot = Pipeline.GetSlotsForTest().Single(v => v.Slot.TaskId == TaskId).Slot;
            var seeded = new WorkerAssignmentContext(
                GoalId, "worker-seeded-elsewhere", WorkerRole.Coder, slot, "model-seeded");
            Assert.Equal(WorkerAssignmentWriteStatus.Recorded, Store.InsertOnce(seeded).Status);
        }

        /// <summary>
        /// Pushes a real Ready and waits until the transport has FORWARDED an assignment — the
        /// deterministic signal that the delivery completed, taken from the transport itself.
        /// </summary>
        public async Task<Exception?> SendReadyAndAwaitPublishedAsync()
        {
            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });
            await Writer.AssignmentForwarded.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            return null;
        }

        /// <summary>
        /// Pushes a real Ready and waits until the PRODUCTION WARNING for THIS message has been
        /// emitted. The signal is allocated FRESH for every call (see
        /// <see cref="CapturingReadyLogger.WaitForBlockedWarning"/>), so a second Ready can never be
        /// satisfied by the FIRST Ready's already-completed signal.
        /// </summary>
        public async Task SendReadyAndAwaitBlockedAsync()
        {
            var signal = Logger.WaitForBlockedWarning();
            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });
            await signal.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }

        /// <summary>Pushes a real Ready and waits for the stream to end, returning its fault (or null).</summary>
        /// <remarks>
        /// THE SAME NARROWING AS THE LEAK PROOF: a bounded-wait timeout (or a cancellation of the
        /// waiter) means the stream is STILL RUNNING, so it is rethrown loudly rather than returned
        /// as if it were the stream's own terminal fault. Only an exception from a task that has
        /// actually COMPLETED is reported as the observed termination.
        /// </remarks>
        public async Task<Exception?> SendReadyAndAwaitStreamEndAsync()
        {
            Reader.Push(new WorkerMessage { WorkerId = Worker.Id, Ready = new WorkerReady() });
            Reader.Complete();

            try
            {
                await StreamTask.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
                StreamFaulted = false;
                return null;
            }
            catch (Exception ex) when (StreamTask.IsCompleted && ex is not TimeoutException)
            {
                // The stream's own termination is the observed result for the fault vector.
                StreamFaulted = true;
                return ex;
            }
            catch (Exception ex)
            {
                // STILL RUNNING: a timeout (or a cancelled waiter) is a live producer, never a fault.
                throw new TimeoutException(
                    $"the WorkStream for worker '{Worker.Id}' did not drain within " +
                    $"{BoundedWait.TotalSeconds:F0}s (streamCompleted={StreamTask.IsCompleted})",
                    ex);
            }
        }

        /// <summary>
        /// THE SHARED TEARDOWN — the ONE place every vector's producer cleanup happens, so no
        /// ordering/control/mutant path can leave the transport pump parked.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE ORDER IS THE CONTRACT, and every step is UNCONDITIONAL:
        /// </para>
        /// <list type="number">
        ///   <item><description>RELEASE THE PARK FIRST. The pump may be blocked inside
        ///     <see cref="RecordingStreamWriter.RecordAsync"/> awaiting its release gate — which is
        ///     exactly the state a FAILING load-bearing assertion leaves behind, because the test
        ///     never reached its own release call. Completing the request input first would not help:
        ///     <c>WorkStream</c> awaits its channel task, so a parked pump keeps the whole stream
        ///     alive. Releasing first is what makes the join below possible at all.</description></item>
        ///   <item><description>COMPLETE THE REQUEST INPUT so the read loop can finish.</description></item>
        ///   <item><description>JOIN THE ORIGINAL STREAM TASK with a FINITE bound. Exceeding the
        ///     bound is a REAL LEAK (a live producer), so it is reported as a cleanup failure rather
        ///     than silently ignored.</description></item>
        /// </list>
        /// <para>
        /// IT NEVER THROWS: the outcome is RETURNED so the caller
        /// (<see cref="WorkerAssignmentPublicationTests.RunAsync"/>) can keep a PRIMARY assertion
        /// failure authoritative and surface a cleanup failure only when there is no primary one.
        /// A stream that terminated with its own fault (the post-record channel-fault vector) is a
        /// normal, JOINED termination — not a cleanup failure.
        /// </para>
        /// </remarks>
        /// <returns><c>null</c> when teardown completed cleanly; otherwise the cleanup failure.</returns>
        public async Task<Exception?> StopAsync()
        {
            // (1) THE UNCONDITIONAL RELEASE. Idempotent: safe when nothing is parked and safe when a
            // test already released it on its own success path.
            Writer.ReleaseParked();

            // (2) END THE REQUEST INPUT.
            Reader.Complete();

            // (3) THE BOUNDED JOIN of the ORIGINAL stream task.
            try
            {
                await StreamTask.WaitAsync(_joinBound, CancellationToken.None);
                StreamJoined = true;
                StreamFaulted = false;
            }
            catch (TimeoutException)
            {
                // THE LEAK: the producer is still live. Report it loudly.
                StreamJoined = false;
                ObserveTeardownPostconditions();
                return new TimeoutException(
                    $"TEARDOWN LEAK: the WorkStream for worker '{Worker.Id}' did not terminate within " +
                    $"{_joinBound.TotalSeconds:F0}s — a live producer/stream task remains. " +
                    $"(pumpReleased={Writer.ParkReleased}, parkedPumpStillHeld={Writer.PumpStillParked})");
            }
            catch (Exception) when (StreamTask.IsCompleted)
            {
                // The stream's OWN terminal fault of a COMPLETED task is a joined termination, not a
                // cleanup failure. The IsCompleted guard is what keeps a STILL-RUNNING task out of
                // this branch, so "joined" can never be claimed for a live producer.
                StreamJoined = true;
                StreamFaulted = true;
            }
            catch (Exception ex)
            {
                // STILL RUNNING after a non-timeout wait failure — a live producer. Report it.
                StreamJoined = false;
                ObserveTeardownPostconditions();
                return new InvalidOperationException(
                    $"TEARDOWN LEAK: the WorkStream for worker '{Worker.Id}' is still running after its " +
                    $"join failed — a live producer/stream task remains. " +
                    $"(pumpReleased={Writer.ParkReleased}, parkedPumpStillHeld={Writer.PumpStillParked})",
                    ex);
            }

            ObserveTeardownPostconditions();
            return null;
        }

        /// <summary>
        /// Captures the post-teardown facts the leak proof asserts: the pump was released, the stream
        /// task really completed, and the stream's own <c>finally</c> ran its pinned-worker cleanup.
        /// </summary>
        private void ObserveTeardownPostconditions()
        {
            PumpReleasedAfterTeardown = Writer.ParkReleased;
            StreamTaskCompletedAfterTeardown = StreamTask.IsCompleted;
            WorkerRemovedAfterTeardown = !ReferenceEquals(Pool.GetWorker(Worker.Id), Worker);
        }

        /// <summary>Whether the bounded join actually joined the stream task.</summary>
        public bool StreamJoined { get; private set; }

        /// <summary>Post-teardown: the transport pump's release gate was signalled.</summary>
        public bool PumpReleasedAfterTeardown { get; private set; }

        /// <summary>Post-teardown: the original stream task has completed (no live producer).</summary>
        public bool StreamTaskCompletedAfterTeardown { get; private set; }

        /// <summary>Post-teardown: the stream's finally removed the pinned worker from the pool.</summary>
        public bool WorkerRemovedAfterTeardown { get; private set; }

        /// <summary>
        /// TEST-ONLY: shortens the join bound so the leak-reporting proof stays fast. Used ONLY by
        /// <c>Teardown_WhenTheProducerCannotBeReleased_ReportsTheLeakLoudly</c>; every other vector
        /// keeps the full bound.
        /// </summary>
        public void UseShortJoinBoundForLeakProof() => _joinBound = TimeSpan.FromSeconds(2);

        /// <summary>
        /// TEST-ONLY: restores the NORMAL join bound. The leak proof calls this as part of its
        /// unconditional restoration, so the REAL join that follows gets the full bound rather than
        /// the shortened one it used to force the leak.
        /// </summary>
        public void RestoreNormalJoinBound() => _joinBound = BoundedWait;

        /// <summary>Whether the original stream task is completed RIGHT NOW (the live-producer probe).</summary>
        public bool StreamTaskIsCompletedNow => StreamTask.IsCompleted;

        /// <summary>
        /// TEST-ONLY: awaits the REAL termination of the stream after the leak proof has re-enabled
        /// the release, so that proof never leaves the producer it deliberately stalled behind.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A BOUNDED-WAIT TIMEOUT IS NOT BENIGN AND IS NEVER SWALLOWED. The bounded
        /// <c>WaitAsync</c> raises <see cref="TimeoutException"/> when the task is STILL RUNNING, which is precisely
        /// the live-producer condition this helper exists to prevent — so it is rethrown loudly with
        /// diagnostic state instead of being mistaken for a terminal fault.
        /// </para>
        /// <para>
        /// THE SUPPRESSION IS NARROWED TO AN ACTUAL TERMINAL FAULT OF AN ALREADY-COMPLETED TASK: a
        /// stream that ended faulted or cancelled IS a joined termination, so its exception is
        /// tolerated — but only after <see cref="Task.IsCompleted"/> has been confirmed. A
        /// still-running task can never take that path.
        /// </para>
        /// </remarks>
        /// <exception cref="TimeoutException">The producer is still live after the bound.</exception>
        public async Task AwaitStreamTerminationForLeakProofAsync()
        {
            Reader.Complete();
            try
            {
                await StreamTask.WaitAsync(BoundedWait, CancellationToken.None);
            }
            catch (Exception ex) when (StreamTask.IsCompleted && ex is not TimeoutException)
            {
                // AN ACTUAL TERMINAL FAULT of a COMPLETED task — a genuine joined termination.
            }
            catch (TimeoutException ex)
            {
                // STILL RUNNING: the deliberately stalled producer never ended. FAIL LOUDLY.
                throw new TimeoutException(
                    $"LEAK-PROOF RESTORATION FAILED: the deliberately stalled WorkStream for worker " +
                    $"'{Worker.Id}' did not terminate within {BoundedWait.TotalSeconds:F0}s after the " +
                    $"release was restored — a live producer remains. " +
                    $"(pumpReleased={Writer.ParkReleased}, parkedPumpStillHeld={Writer.PumpStillParked}, " +
                    $"streamCompleted={StreamTask.IsCompleted})",
                    ex);
            }

            // THE JOIN IS PROVEN, not assumed: WaitAsync can only return without the task being
            // completed if the bound elapsed, which the branch above already rejects.
            if (!StreamTask.IsCompleted)
            {
                throw new InvalidOperationException(
                    $"LEAK-PROOF RESTORATION FAILED: the WorkStream for worker '{Worker.Id}' is still " +
                    "running after a wait that neither completed nor timed out.");
            }
        }
    }

    // ───────────────────────────── fakes and helpers ─────────────────────────────

    /// <summary>
    /// THE TRANSPORT HAND-OFF: the ONE place that answers "has an assignment left the publisher
    /// towards this worker?", capturing BOTH halves of the hand-off at a SINGLE instant.
    /// <para>
    /// WHY ONE POINT AND NOT TWO POLLS. An assignment travels channel → pump dequeue → writer entry →
    /// writer record. Sampling the channel and the recorded list as two independent reads can observe
    /// an assignment in NEITHER, because a legal schedule dequeues it from the channel before the
    /// writer records it. This type closes that window: the writer increments
    /// <see cref="MarkEntered"/> BEFORE recording anything, so the moment a message leaves the channel
    /// it is already counted here, and <see cref="HasAssignmentInFlight"/> reads the queued state and
    /// the entry count together while the pump is parked.
    /// </para>
    /// </summary>
    private sealed class TransportHandoff
    {
        private int _entered;

        /// <summary>Reports whether the pinned worker's channel still holds a queued assignment.</summary>
        public Func<bool>? ChannelHasAssignment { get; set; }

        /// <summary>Entries the transport writer has begun — incremented BEFORE the message is recorded.</summary>
        public int Entered => Volatile.Read(ref _entered);

        /// <summary>Marks that the transport writer has ENTERED with an assignment.</summary>
        public void MarkEntered() => Interlocked.Increment(ref _entered);

        /// <summary>
        /// The single-instant hand-off read: an assignment is in flight when it is either still
        /// queued on the channel OR has already entered the transport writer.
        /// </summary>
        public bool HasAssignmentInFlight() =>
            (ChannelHasAssignment?.Invoke() ?? false) || Entered > 0;
    }

    /// <summary>
    /// THE ROW-BEFORE-PUBLICATION OBSERVER: fired the instant a transaction COMMITS, it records
    /// whether the DELIVERED task's assignment-context row is durable and whether an assignment is
    /// already in flight to that worker AT THAT INSTANT.
    /// <para>
    /// EF raises the committed notification only AFTER the provider's commit returned, so the fresh
    /// connection used here really does see the committed row — which is what makes this an honest
    /// instrument rather than a guess. The in-flight half comes from <see cref="TransportHandoff"/>,
    /// a SINGLE observation point that cannot lose an assignment between the channel dequeue and the
    /// transport recording.
    /// </para>
    /// <para>
    /// EVERY commit is observed — including a commit that records some OTHER row — so a publisher
    /// that delivers first and then commits a decoy is caught with <c>AssignmentInFlight == true</c>
    /// and <c>DeliveredRowPresent == false</c>.
    /// </para>
    /// </summary>
    private sealed class CommitObservationInterceptor : IInterceptor
    {
        private readonly List<CommitObservation> _observations = [];

        /// <summary>The connection string of the same database the store writes to.</summary>
        public string ProbeConnectionString { get; set; } = "";

        /// <summary>The task id whose row the ordering contract is about.</summary>
        public string DeliveredTaskId { get; set; } = "";

        /// <summary>The single-point transport hand-off observation.</summary>
        public TransportHandoff Handoff { get; } = new();

        /// <summary>The observations, in commit order.</summary>
        public IReadOnlyList<CommitObservation> Observations
        {
            get
            {
                lock (_observations)
                    return [.. _observations];
            }
        }

        /// <summary>
        /// THE INVARIANT this observer exists to enforce: an assignment may be in flight ONLY together
        /// with (or after) the DELIVERED task's confirmed row — "assignment in flight" IMPLIES
        /// "delivered row confirmed".
        /// </summary>
        /// <param name="assignmentInFlight">Whether an assignment has left the publisher.</param>
        /// <param name="deliveredRowPresent">Whether the delivered task's row is durable.</param>
        /// <returns><c>true</c> when the facts are consistent with the ordering contract.</returns>
        public static bool HasConfirmedRowBeforeAssignment(bool assignmentInFlight, bool deliveredRowPresent) =>
            !assignmentInFlight || deliveredRowPresent;

        /// <summary>Records one commit-instant observation; called by the forwarding interceptor.</summary>
        public void Committed()
        {
            // ONE INSTANT, BOTH FACTS: the in-flight read is taken first (it is the fact the record is
            // supposed to precede), then the durable row is confirmed.
            var channelQueued = Handoff.ChannelHasAssignment?.Invoke() ?? false;
            var entries = Handoff.Entered;
            var inFlight = channelQueued || entries > 0;
            var deliveredRowPresent = ProbeDeliveredRowPresent();

            lock (_observations)
            {
                _observations.Add(new CommitObservation(
                    DeliveredTaskId, deliveredRowPresent, inFlight, channelQueued, entries));
            }
        }

        private bool ProbeDeliveredRowPresent()
        {
            if (string.IsNullOrEmpty(ProbeConnectionString) || string.IsNullOrEmpty(DeliveredTaskId))
                return false;

            try
            {
                using var connection = new SqliteConnection(ProbeConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT task_id FROM worker_assignment_contexts WHERE task_id = $t";
                command.Parameters.AddWithValue("$t", DeliveredTaskId);
                return command.ExecuteScalar() as string is not null;
            }
            catch
            {
                // A probe failure must never mask the test's own outcome.
                return false;
            }
        }
    }

    /// <summary>
    /// The EF interceptor that forwards COMMITS to the shared observer. Kept separate so the observer
    /// stays a plain, testable object while EF only ever sees a normal interceptor.
    /// </summary>
    private sealed class CommitObservingTransactionInterceptor(CommitObservationInterceptor observer)
        : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            observer.Committed();
    }

    /// <summary>One commit-instant observation: the delivered task's row and the hand-off state.</summary>
    /// <param name="DeliveredTaskId">The task whose ordering contract is being observed.</param>
    /// <param name="DeliveredRowPresentAtCommit">Whether THAT task's row was durable at this instant.</param>
    /// <param name="AssignmentInFlightAtCommit">Whether an assignment had left the publisher.</param>
    /// <param name="ChannelQueuedAtCommit">The queued half of the hand-off read.</param>
    /// <param name="TransportEntriesAtCommit">The entered-the-writer half of the hand-off read.</param>
    private sealed record CommitObservation(
        string DeliveredTaskId,
        bool DeliveredRowPresentAtCommit,
        bool AssignmentInFlightAtCommit,
        bool ChannelQueuedAtCommit,
        int TransportEntriesAtCommit);

    /// <summary>A factory handing out store-OWNED contexts on their own connections, disposed with the fixture.</summary>
    private sealed class ReadyFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly string _connectionString;
        private readonly IInterceptor[] _interceptors;
        private readonly List<CopilotHiveDbContext> _contexts = [];

        public ReadyFactory(string connectionString, IInterceptor[] interceptors)
        {
            _connectionString = connectionString;
            _interceptors = interceptors;
        }

        public CopilotHiveDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_connectionString);
            foreach (var interceptor in _interceptors)
                builder.AddInterceptors(interceptor);

            var context = new CopilotHiveDbContext(builder.Options);
            lock (_contexts)
                _contexts.Add(context);
            return context;
        }

        public void Dispose()
        {
            List<CopilotHiveDbContext> contexts;
            lock (_contexts)
                contexts = [.. _contexts];
            foreach (var context in contexts)
            {
                try
                {
                    context.Dispose();
                }
                catch
                {
                    // Best-effort.
                }
            }
        }
    }

    /// <summary>
    /// THE UNGATED CONTROL: it DELIVERS FIRST and only then COMMITS — recording a DECOY context for a
    /// different task id through the SAME observed store.
    /// <para>
    /// This is deliberately stronger than "never records at all": the observer fires on a REAL
    /// post-delivery commit, so the invariant must reject it on the evidence of that commit
    /// (assignment already in flight, delivered task's row absent) rather than merely on the absence
    /// of any commit.
    /// </para>
    /// </summary>
    private sealed class UngatedPublishThenCommitPublisher(WorkerAssignmentContextStore store)
        : IWorkerAssignmentPublisher
    {
        private int _commitCount;

        /// <summary>How many decoy commits the control performed (proves it really committed).</summary>
        public int CommitCount => Volatile.Read(ref _commitCount);

        public async Task PublishAsync(ConnectedWorker worker, WorkTask task, CancellationToken cancellationToken)
        {
            // (1) PUBLISH FIRST — the violation under test.
            await worker.MessageChannel.Writer.WriteAsync(
                new OrchestratorMessage { Assignment = GrpcMapper.ToGrpc(task) }, cancellationToken);

            // (2) THEN COMMIT a DECOY row, so the observer fires AFTER the delivery. The decoy uses a
            //     different task id, so the delivered task's row stays absent.
            var decoy = new WorkerAssignmentContext(
                task.GoalId,
                worker.Id,
                WorkerRole.Coder,
                new WorkSlot(
                    $"decoy-{task.TaskId}", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
                task.Model);

            var result = store.InsertOnce(decoy);
            Assert.Equal(WorkerAssignmentWriteStatus.Recorded, result.Status);
            Interlocked.Increment(ref _commitCount);
        }
    }

    /// <summary>
    /// THE DELIVERY OBSERVATION at the transport boundary: records every message the transport
    /// forwards, signals deterministically the moment an assignment is forwarded, and can PARK inside
    /// the write so the channel pump is provably unable to drain during a record.
    /// <para>
    /// THE ENTRY MARK IS THE KEY ORDERING DETAIL: an assignment write marks its ENTRY (via
    /// <see cref="TransportHandoff.MarkEntered"/>) BEFORE the message is recorded and before any
    /// parking, so the "dequeued from the channel but not yet recorded" window is counted rather than
    /// lost.
    /// </para>
    /// </summary>
    private sealed class RecordingStreamWriter : IServerStreamWriter<OrchestratorMessage>
    {
        private readonly List<OrchestratorMessage> _messages = [];
        private readonly TaskCompletionSource _assignmentForwarded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _parked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _parkArmed;
        private int _parkReleased;
        private int _parkedNow;
        private int _ignoreRelease;

        public WriteOptions? WriteOptions { get; set; }

        /// <summary>The single-point hand-off this writer marks entries into.</summary>
        public TransportHandoff? Handoff { get; set; }

        /// <summary>Completes when the first assignment has been forwarded to this worker.</summary>
        public Task AssignmentForwarded => _assignmentForwarded.Task;

        /// <summary>Completes when the pump has entered the writer and parked.</summary>
        public Task Parked => _parked.Task;

        /// <summary>Whether the parked pump has NOT been released yet (i.e. it is still held).</summary>
        public bool ParkedThroughout => Volatile.Read(ref _parkReleased) == 0;

        /// <summary>Whether the release gate has been signalled — the teardown's release evidence.</summary>
        public bool ParkReleased => Volatile.Read(ref _parkReleased) == 1;

        /// <summary>Whether a pump is CURRENTLY blocked inside this writer (the leak indicator).</summary>
        public bool PumpStillParked => Volatile.Read(ref _parkedNow) == 1;

        /// <summary>Arms the park so the NEXT write blocks inside the writer until released.</summary>
        public void ParkOnNextWrite() => Volatile.Write(ref _parkArmed, 1);

        /// <summary>
        /// Releases a parked write. IDEMPOTENT and safe when nothing is parked — the shared teardown
        /// calls it unconditionally on every path, including after a test already released it.
        /// </summary>
        public void ReleaseParked()
        {
            Volatile.Write(ref _parkReleased, 1);
            if (Volatile.Read(ref _ignoreRelease) == 0)
                _release.TrySetResult();
        }

        /// <summary>
        /// TEST-ONLY: makes <see cref="ReleaseParked"/> a NO-OP so a parked pump genuinely cannot
        /// terminate — the unreleasable-producer condition the leak-reporting proof needs.
        /// </summary>
        public void IgnoreReleaseForLeakProof() => Volatile.Write(ref _ignoreRelease, 1);

        /// <summary>TEST-ONLY: restores normal release behaviour so the leak proof can clean up.</summary>
        public void StopIgnoringRelease() => Volatile.Write(ref _ignoreRelease, 0);

        public IReadOnlyList<OrchestratorMessage> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        /// <summary>Every forwarded assignment, in order.</summary>
        public IReadOnlyList<TaskAssignment> Assignments =>
            [.. Messages.Where(m => m.Assignment is not null).Select(m => m.Assignment)];

        private async Task RecordAsync(OrchestratorMessage message)
        {
            // THE ENTRY MARK, before anything else: the message has left the channel, so the hand-off
            // must already count it even though it is not recorded yet.
            if (message.Assignment is not null)
                Handoff?.MarkEntered();

            if (Interlocked.Exchange(ref _parkArmed, 0) == 1)
            {
                // THE PARKED WINDOW is observable, so the teardown can report a still-held pump as a
                // leak rather than hanging silently.
                Volatile.Write(ref _parkedNow, 1);
                _parked.TrySetResult();
                try
                {
                    await _release.Task;
                }
                finally
                {
                    Volatile.Write(ref _parkedNow, 0);
                }
            }

            lock (_messages)
                _messages.Add(message);

            if (message.Assignment is not null)
                _assignmentForwarded.TrySetResult();
        }

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(OrchestratorMessage message) =>
            RecordAsync(message);

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(
            OrchestratorMessage message, CancellationToken cancellationToken) =>
            RecordAsync(message);
    }

    /// <summary>
    /// Records every logged message, and signals both the production warning and the production
    /// success log deterministically.
    /// </summary>
    private class CapturingReadyLogger : ILogger<HiveOrchestratorService>
    {
        private readonly List<string> _messages = [];

        // THE PENDING SIGNAL QUEUES: each waiter gets its OWN TaskCompletionSource, completed by the
        // NEXT matching log. A previously completed signal can therefore never satisfy a later wait.
        private readonly Queue<TaskCompletionSource> _blockedWaiters = new();
        private readonly Queue<TaskCompletionSource> _publishedWaiters = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        /// <summary>
        /// Returns a FRESH task that completes when the NEXT blocked-disposition warning is emitted.
        /// Called BEFORE the Ready is pushed, so the signal can never be missed — and never reused, so
        /// a second Ready is never satisfied by the FIRST Ready's warning.
        /// </summary>
        public Task WaitForBlockedWarning()
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_messages)
                _blockedWaiters.Enqueue(waiter);
            return waiter.Task;
        }

        /// <summary>
        /// Returns a FRESH task that completes when the NEXT production success log is emitted — the
        /// signal that HandleWorkerReady's publisher invocation RETURNED. It never waits on the
        /// transport, so it stays valid while the transport pump is parked.
        /// </summary>
        public Task WaitForPublishedLog()
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_messages)
                _publishedWaiters.Enqueue(waiter);
            return waiter.Task;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public virtual void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            TaskCompletionSource? blocked = null;
            TaskCompletionSource? published = null;
            lock (_messages)
            {
                _messages.Add(message);

                if (message.Contains("assignment blocked", StringComparison.Ordinal) && _blockedWaiters.Count > 0)
                    blocked = _blockedWaiters.Dequeue();

                if (message.Contains("Assignment published", StringComparison.Ordinal) && _publishedWaiters.Count > 0)
                    published = _publishedWaiters.Dequeue();
            }

            blocked?.TrySetResult();
            published?.TrySetResult();
        }
    }

    /// <summary>A logger that throws AFTER recording a warning — the guarded-warning vector.</summary>
    private sealed class ThrowingOnWarningReadyLogger : CapturingReadyLogger
    {
        public override void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            base.Log(logLevel, eventId, state, exception, formatter);
            if (logLevel == LogLevel.Warning)
                throw new InvalidOperationException("the logger threw while emitting the warning");
        }
    }

    /// <summary>A single-goal source so the real lifecycle service can persist status updates.</summary>
    private sealed class ReadyGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "ready-test-source";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

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

    private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;
}
