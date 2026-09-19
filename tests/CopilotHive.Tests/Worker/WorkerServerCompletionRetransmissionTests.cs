using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using System.Reflection;
using System.Threading.Channels;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE LOST-ACK END-TO-END VECTOR, IN PROCESS: the REAL worker-side
/// <see cref="WorkerService.RunAsync"/> lifecycle connected to the REAL server-side
/// <see cref="HiveOrchestratorService"/> registration, work stream, Ready-driven assignment
/// publisher, SQLite-backed completion recorder and receipt confirmation — with exactly ONE
/// acknowledgement dropped in the bridge, and NO cancellation and NO EOF.
/// <para>
/// WHAT THIS PROVES. A single lost acknowledgement on a STILL-LIVE stream cannot stall the worker:
/// the first ordinary completion is recorded durably by the real server, its acknowledgement is
/// swallowed by the bridge before the worker can see it, the worker's ordinary Ready stays withheld,
/// the worker then RESENDS its frozen completion on the SAME stream after its retry interval, the
/// real server re-confirms and re-acknowledges it, and exactly ONE ordinary Ready is then emitted
/// and ACCEPTED by the real server.
/// </para>
/// <para>
/// HOW THE BRIDGE IS WIRED, AND WHY IT IS NOT A FAKE OF PRODUCTION.
/// <list type="bullet">
///   <item>The worker runs the REAL <see cref="WorkerService.RunAsync"/> with its
///   <see cref="WorkerService.CallInvokerFactory"/> seam pointed at a <see cref="CallInvoker"/> that
///   FORWARDS <c>Register</c> straight into the REAL service's <c>Register</c> override, so the
///   accepted registration — and therefore the negotiated ACK/readiness facts — is genuinely
///   PRODUCTION's answer.</item>
///   <item>The worker's duplex stream is supplied through <see cref="WorkerService.WorkStreamFactory"/>:
///   its request writer enqueues into a channel the REAL <c>WorkStream</c> READS, and its response
///   reader consumes a channel the REAL <c>WorkStream</c>'s SINGLE response pump WRITES to. There is
///   exactly one pump and one reader.</item>
///   <item>The server's <c>WorkStream</c> is driven with that same request channel as its reader and a
///   writer whose only special behavior is the explicitly armed ONE-SHOT acknowledgement drop.</item>
///   <item>The server is built as production builds it: a REAL <c>WorkerAssignmentPublisher</c> over a
///   REAL SQLite-backed <c>WorkerAssignmentContextStore</c>, and a REAL <c>WorkerCompletionRecorder</c>
///   over REAL SQLite-backed stores — so <c>Record</c> and <c>ConfirmStoredReceipt</c> are the
///   production implementations.</item>
/// </list>
/// </para>
/// <para>
/// NO COMPLETE OR READY IS MANUFACTURED. The fixture never synthesizes, replays or injects a
/// <c>Complete</c> or a <c>Ready</c>: every one is produced by the REAL worker and consumed by the
/// REAL server. The only thing the bridge controls is whether ONE acknowledgement reaches the worker.
/// </para>
/// <para>
/// COUNTERS, NOT RECORD-ONLY FAKES. Every "exactly one"/"no X before Y" claim is made against a
/// FORWARDING decorator — which delegates to the real collaborator FIRST and counts only afterwards —
/// or against a real production-side observation (the worker's own writer, the recorder's durable
/// row, the server's own log lines). No default-false fake backs any absence assertion.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerServerCompletionRetransmissionTests
{
    private const string WorkerId = "worker-lost-ack";
    private const string GoalId = "goal-lost-ack";
    private const string TaskId = "task-lost-ack";
    private const string AssignedModel = "model-lost-ack";
    private const int ExpectedPromptCount = 1;

    /// <summary>Bound on every await a regression could otherwise block forever.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    /// <summary>The bounded positive NON-COMPLETION window for withheld behaviors.</summary>
    private static readonly TimeSpan SuppressionBound = TimeSpan.FromSeconds(1);

    /// <summary>
    /// THE ONE MANDATORY AC7 VECTOR: drop the FIRST acknowledgement, keep the stream perfectly alive,
    /// and prove the retransmitted completion is confirmed and readied end to end.
    /// </summary>
    /// <remarks>
    /// LIFETIME OWNERSHIP. The worker's <c>run</c> task, the server stream tasks and every
    /// constructor-subscribed downstream handler are owned by <see cref="LostAckFixture"/>, which
    /// joins them in a <c>finally</c> BEFORE any channel completion or resource disposal. An
    /// assertion failure therefore stays the authoritative exception while the worker lifecycle is
    /// still joined — nothing unwinds against a disposed SQLite store or a deleted temp root.
    /// </remarks>
    [Fact]
    public async Task LostFirstAck_OnALiveStream_WorkerResendsAndTheRealServerConfirmsAndReadies()
    {
        await using var fixture = new LostAckFixture();
        var chain = fixture.Chain;
        var worker = fixture.Worker;
        var bridge = fixture.Bridge;

        fixture.StartWorkerRun();

        // ── THE REAL REGISTRATION, PRODUCTION'S OWN ANSWER ─────────────────────────────
        await bridge.WorkerWriter.WaitForAsync(
            WorkerMessage.PayloadOneofCase.Ready, 0, TestContext.Current.CancellationToken);

        Assert.Equal(1, bridge.RegisterCalls);
        Assert.True(bridge.LastRegistrationAccepted);
        Assert.True(bridge.LastRegistrationAckEnabled);
        Assert.True(bridge.LastRegistrationReadyRequired);

        var connection = chain.PublishedConnection(worker.Service)
            ?? throw new Xunit.Sdk.XunitException("RunAsync must publish its accepted connection.");
        Assert.True(connection.CompletionReceiptAckEnabled);
        Assert.True(connection.CompletionReadyRequired);
        Assert.Equal(WorkerId, connection.AssignedId);

        // ── THE INITIAL READY DRIVES THE REAL ASSIGNMENT PUBLICATION ───────────────────
        var assignment = await worker.AssignmentDelivered.WaitAsync(
            Failsafe, TestContext.Current.CancellationToken);
        Assert.Equal(TaskId, assignment.TaskId);

        // THE REAL PUBLISHER CONFIRMED PUBLICATION, and the REAL assignment store holds the row.
        Assert.Equal(1, chain.Publisher.PublishCalls);
        Assert.Equal(TaskId, chain.Publisher.LastPublishedTask);
        Assert.NotNull(chain.Stores.AssignmentStore.Load(TaskId));

        // ── ARM THE ONE DROP, THEN LET THE WORKER FINISH ───────────────────────────────
        // The drop covers EXACTLY the FIRST acknowledgement; every later one forwards.
        bridge.ArmDropFirstAck();
        worker.ReleasePrompt();

        await bridge.WorkerWriter.WaitForAsync(
            WorkerMessage.PayloadOneofCase.Complete, 0, TestContext.Current.CancellationToken);

        // THE REAL SERVER RECORDED THE RECEIPT DURABLY. The rendezvous is the forwarding recorder's
        // own signal, raised AFTER production's Record returned — no polling, no sampling window.
        var receiptBefore = await chain.WaitForStoredReceiptAsync(Failsafe);
        Assert.Equal(TaskId, receiptBefore.Receipt.Slot.TaskId);
        Assert.Equal(WorkerId, receiptBefore.Receipt.WorkerId);
        Assert.Equal(GoalId, receiptBefore.Receipt.GoalId);

        // THE ONE DROPPED ACK: the server really attempted it; the bridge swallowed it.
        await bridge.DroppedAckObserved.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        Assert.Equal(1, bridge.AckWriteAttempts);
        Assert.Equal(0, bridge.AckForwardedCount);

        // ── NO ORDINARY READY YET: the readiness wait is REAL ──────────────────────────
        // Only the INITIAL Ready exists, and that absence is observed with a bounded window
        // while the loop is provably alive behind it. The Ready-ACCEPTANCE baseline is taken
        // AFTER the initial Ready (which legitimately is the one Ready the server has
        // accepted so far), so the assertion below is about what the COMPLETION did.
        Assert.Single(bridge.WorkerWriter.Of(WorkerMessage.PayloadOneofCase.Ready));
        var acceptedAfterInitialReady = chain.OrdinaryReadyAcceptedCount;
        var withheld = await Record.ExceptionAsync(async () =>
            await bridge.WorkerWriter
                .WaitForMessageAsync(
                    WorkerMessage.PayloadOneofCase.Ready, 1, TestContext.Current.CancellationToken)
                .WaitAsync(SuppressionBound, TestContext.Current.CancellationToken));
        Assert.IsType<TimeoutException>(withheld);
        Assert.Equal(0, chain.Recorder.ConfirmCalls);
        Assert.Equal(acceptedAfterInitialReady, chain.OrdinaryReadyAcceptedCount);

        // ── ADVANCE THE WORKER'S OWN RETRY CLOCK ───────────────────────────────────────
        await worker.RetryDelayCreatedAsync(1, TestContext.Current.CancellationToken);
        worker.Clock.Advance(chain.RetryInterval);

        // ── THE WORKER RESENDS ITS FROZEN COMPLETION ON THE SAME STREAM ─────────────────
        await bridge.WorkerWriter.WaitForAsync(
            WorkerMessage.PayloadOneofCase.Complete, 1, TestContext.Current.CancellationToken);

        // CLONE OWNERSHIP, ON THE RAW WRITER ARGUMENTS. These are the EXACT objects production
        // handed to WriteAsync — no fixture clone in between — so a production that re-delivered
        // its private snapshot object would show ONE object here, not two.
        var rawCompletes = bridge.WorkerWriter.RawOf(WorkerMessage.PayloadOneofCase.Complete);
        var rawOriginal = rawCompletes[0];
        var rawResent = rawCompletes[1];

        Assert.NotSame(rawOriginal, rawResent);
        Assert.NotSame(rawOriginal.Complete, rawResent.Complete);
        Assert.NotSame(rawOriginal.Complete.Metrics, rawResent.Complete.Metrics);
        Assert.NotSame(rawOriginal.Complete.GitStatus, rawResent.Complete.GitStatus);
        Assert.Equal(rawOriginal.WorkerId, rawResent.WorkerId);
        Assert.Equal(rawOriginal.Complete, rawResent.Complete);

        // THE MUTATION, applied to the DELIVERED raw argument itself. The frozen evidence captured
        // here is what the NEXT retransmission must still carry; a shallow copy that shared the
        // nested collections would leak this damage forward.
        var frozenIssues = rawOriginal.Complete.Metrics.Issues.ToArray();
        var frozenChangedFiles = rawOriginal.Complete.GitStatus.ChangedFiles.ToArray();
        Assert.NotEmpty(frozenIssues); // NON-VACUITY: there is real nested evidence to damage.
        Assert.NotEmpty(frozenChangedFiles);

        rawOriginal.Complete.Metrics.Issues.Clear();
        rawOriginal.Complete.Metrics.Issues.Add("MUTATED-BY-THE-FIRST-WRITER");
        rawOriginal.Complete.GitStatus.ChangedFiles.Clear();
        rawOriginal.Complete.Output = "MUTATED-ORIGINAL-OUTPUT";

        // THE ALREADY-DELIVERED RETRANSMISSION still carries the ORIGINAL evidence, IN ORDER.
        Assert.Equal(frozenIssues, rawResent.Complete.Metrics.Issues);
        Assert.Equal(frozenChangedFiles, rawResent.Complete.GitStatus.ChangedFiles);
        Assert.DoesNotContain("MUTATED-BY-THE-FIRST-WRITER", rawResent.Complete.Metrics.Issues);
        Assert.NotEqual("MUTATED-ORIGINAL-OUTPUT", rawResent.Complete.Output);

        // ── THE REAL SERVER CONFIRMS AND RE-ACKS ────────────────────────────────────────
        await bridge.ReAckForwarded.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        // THE RECEIPT IS UNCHANGED. Both sides are read back through a FRESH store, and the
        // COMPLETE persisted value is compared — production's own canonical encoding plus every
        // individual field, including FirstStoredAtUtc.
        var receiptAfter = await chain.WaitForReConfirmedReceiptAsync(Failsafe);
        AssertPersistedReceiptUnchanged(receiptBefore, receiptAfter);

        Assert.Equal(1, chain.Recorder.ConfirmCalls);
        Assert.Equal(1, chain.Recorder.ConfirmAccepted);
        Assert.Equal(1, chain.Recorder.RecordCalls);

        // ── EXACTLY ONE ORDINARY READY, ACCEPTED BY THE REAL SERVER ─────────────────────
        var ordinaryReady = await bridge.WorkerWriter.WaitForMessageAsync(
            WorkerMessage.PayloadOneofCase.Ready, 1, TestContext.Current.CancellationToken);
        Assert.Equal(WorkerId, ordinaryReady.WorkerId);
        Assert.Equal(2, bridge.WorkerWriter.Of(WorkerMessage.PayloadOneofCase.Ready).Count);

        await chain.WaitForOrdinaryReadyAcceptedAsync(Failsafe);

        // ── THE SAME REGISTRATION AND THE SAME STREAM THROUGHOUT ────────────────────────
        Assert.Same(connection, chain.PublishedConnection(worker.Service));
        Assert.False(connection.IsRetired);
        Assert.Equal(1, bridge.RegisterCalls);
        Assert.Equal(1, bridge.StreamOpens);
        Assert.Equal(1, bridge.ServerStreamCount);

        // ── ONE EXECUTION, ONE ORDINARY RECORD, ONE COMPLETION NOTIFICATION ─────────────
        Assert.Equal(ExpectedPromptCount, worker.PromptCount);
        Assert.True(
            chain.DownstreamCompletions == 1,
            $"exactly one downstream completion notification is expected, but observed: {string.Join(" ; ", chain.Notified)}");

        // THE DASHBOARD STATE CHANGES ARE EXACTLY TWO, AND BOTH ARE EXPECTED PRODUCTION
        // BEHAVIOR: the ONE completion publication, and the ONE accepted ordinary Ready
        // (whose own handler notifies after its accepted claim). A third would mean the
        // completion path ran — or was readied — more than once.
        Assert.True(
            chain.DashboardCompletionNotifications == 2,
            "expected exactly the completion publication plus the accepted Ready notification, observed "
                + chain.DashboardCompletionNotifications);

        // ── TEARDOWN: EOF ENDS BOTH SIDES CLEANLY AND EVERYTHING REMAINS JOINED ─────────
        // The fixture's DisposeAsync performs the EOF/join/dispose ordering on EVERY path; here
        // the NORMAL finish is additionally asserted, which a failure path cannot claim.
        await fixture.EndStreamAndJoinWorkerAsync();
        Assert.True(
            fixture.Run.IsCompletedSuccessfully,
            "the worker lifecycle must end normally: " + fixture.Run.Status);

        // A BROKEN TEARDOWN CANNOT HIDE BEHIND A GREEN RUN: on this passing path the fixture's
        // captured cleanup failures (if any) are surfaced now, where no assertion failure competes.
        fixture.AssertCleanTeardown();
    }

    /// <summary>
    /// THE CONTROL: with NO acknowledgement dropped, the FIRST acknowledgement alone authorizes the
    /// single ordinary Ready and the retry never even arms a delay — so the vector above is genuinely
    /// about the DROPPED message and not about a stream that simply never acknowledges.
    /// </summary>
    /// <remarks>
    /// It uses the SAME <see cref="LostAckFixture"/> ownership, so the worker's <c>run</c> task and
    /// every subscribed handler are joined in a <c>finally</c> before anything is disposed here too.
    /// </remarks>
    [Fact]
    public async Task NoDroppedAck_TheFirstAcknowledgementAloneAuthorizesTheSingleReady()
    {
        await using var fixture = new LostAckFixture();
        var chain = fixture.Chain;
        var worker = fixture.Worker;
        var bridge = fixture.Bridge;

        fixture.StartWorkerRun();

        await bridge.WorkerWriter.WaitForAsync(
            WorkerMessage.PayloadOneofCase.Ready, 0, TestContext.Current.CancellationToken);

        var assignment = await worker.AssignmentDelivered.WaitAsync(
            Failsafe, TestContext.Current.CancellationToken);
        Assert.Equal(TaskId, assignment.TaskId);

        worker.ReleasePrompt();
        await bridge.WorkerWriter.WaitForAsync(
            WorkerMessage.PayloadOneofCase.Complete, 0, TestContext.Current.CancellationToken);

        // THE FIRST ACK IS FORWARDED (no drop armed), and it authorizes the single Ready.
        await bridge.FirstAckForwarded.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        Assert.Equal(1, bridge.AckForwardedCount);
        Assert.Equal(0, bridge.AckWriteAttempts - bridge.AckForwardedCount);

        await bridge.WorkerWriter.WaitForAsync(
            WorkerMessage.PayloadOneofCase.Ready, 1, TestContext.Current.CancellationToken);
        await chain.WaitForOrdinaryReadyAcceptedAsync(Failsafe);

        // THE DURABLE RECEIPT EXISTS, observed through the SAME non-polling recorder rendezvous.
        var receipt = await chain.WaitForStoredReceiptAsync(Failsafe);
        Assert.Equal(TaskId, receipt.Receipt.Slot.TaskId);

        // NO RETRY WAS EVER NEEDED: production created AT MOST the one delay timer whose
        // wait the first acknowledgement then cancelled, and no second Complete was written.
        Assert.True(worker.RetryDelayTimers <= 1, "a forwarded first ACK must not require retries");
        Assert.Equal(ExpectedPromptCount, worker.PromptCount);
        Assert.Single(bridge.WorkerWriter.Of(WorkerMessage.PayloadOneofCase.Complete));
        Assert.Single(bridge.WorkerWriter.RawOf(WorkerMessage.PayloadOneofCase.Complete));
        Assert.Equal(1, chain.Recorder.RecordCalls);
        Assert.Equal(0, chain.Recorder.ConfirmCalls);
        Assert.True(
            chain.DownstreamCompletions == 1,
            $"exactly one downstream completion notification is expected, but observed: {string.Join(" ; ", chain.Notified)}");

        // The ONE completion publication plus the ONE accepted Ready notification.
        Assert.True(
            chain.DashboardCompletionNotifications == 2,
            "expected exactly the completion publication plus the accepted Ready notification, observed "
                + chain.DashboardCompletionNotifications);

        await fixture.EndStreamAndJoinWorkerAsync();
        Assert.True(fixture.Run.IsCompletedSuccessfully);
        fixture.AssertCleanTeardown();
    }

    /// <summary>
    /// THE FAILURE-PATH OWNERSHIP CONTROL. It deliberately triggers a real xUnit assertion failure
    /// while the worker RunAsync lifecycle, real WorkStream and a held assignment are all live, and
    /// injects a cleanup failure into the fixture's captured cleanup report. The assertion failure
    /// must remain the exact primary outcome; disposal must return without replacing it, while still
    /// joining both producers and every subscribed handler BEFORE stores, worker resources or the
    /// temporary root are released.
    /// </summary>
    [Fact]
    public async Task LostAckFixture_DeliberateAssertionFailure_RemainsPrimaryAndJoinsBeforeDisposal()
    {
        LostAckFixture? fixture = null;
        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var owned = fixture = new LostAckFixture();
            owned.StartWorkerRun();

            // Positive live-producer evidence: registration/initial Ready completed and the REAL
            // Ready-driven publisher delivered the assignment, whose prompt is still held.
            await owned.Bridge.WorkerWriter.WaitForAsync(
                WorkerMessage.PayloadOneofCase.Ready, 0, TestContext.Current.CancellationToken);
            await owned.Worker.AssignmentDelivered.WaitAsync(
                Failsafe, TestContext.Current.CancellationToken);
            Assert.False(owned.Run.IsCompleted);
            Assert.Contains(owned.Bridge.ServerStreamTasks, task => !task.IsCompleted);

            // A secondary cleanup failure is already recorded. DisposeAsync must report/retain it,
            // never throw it over the assertion that follows.
            owned.InjectCleanupFailureForTest();
            Assert.Fail("DELIBERATE-PRIMARY-ASSERTION");
        });

        var primary = Assert.IsType<Xunit.Sdk.FailException>(failure);
        Assert.Contains("DELIBERATE-PRIMARY-ASSERTION", primary.Message, StringComparison.Ordinal);

        var disposed = Assert.IsType<LostAckFixture>(fixture);
        Assert.True(disposed.Run.IsCompleted, "the worker RunAsync task must be joined on failure.");
        Assert.All(disposed.Bridge.ServerStreamTasks, task => Assert.True(task.IsCompleted));
        Assert.True(disposed.Chain.SubscriptionsDetached);
        Assert.True(disposed.Chain.Stores.IsDisposed);
        Assert.True(disposed.Worker.IsDisposed);
        Assert.False(disposed.RootExists);
        Assert.Contains(
            disposed.CleanupFailures,
            value => value.Contains(nameof(InjectedCleanupFailureException), StringComparison.Ordinal));

        // THE ORDER, observed by the fixture as each stage completed successfully. No resource
        // release can precede the server/worker joins or handler quiescence.
        Assert.Equal(
            [
                "server-streams-joined",
                "worker-run-joined",
                "handlers-joined",
                "chain-disposed",
                "worker-disposed",
                "root-deleted",
            ],
            disposed.CleanupOrder);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  THE OWNED LIFETIME
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ONE OWNER OF EVERY LIFETIME THIS FIXTURE STARTS: the temp root, the real server chain
    /// (with its constructor-subscribed downstream handlers), the bridge with its REAL server stream
    /// tasks, the worker harness — and the worker's own <see cref="WorkerService.RunAsync"/> task.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY IT EXISTS. With the <c>run</c> task as a test-local awaited only on the success path, an
    /// assertion failure unwound the test while the REAL worker lifecycle was still running — and
    /// the enclosing <c>finally</c> blocks then disposed the SQLite stores and deleted the temp root
    /// underneath it. Disposal ordering is therefore owned HERE, in one place, and applied on EVERY
    /// path.
    /// </para>
    /// <para>
    /// THE ORDER IS THE CONTRACT, and nothing is disposed before the joins:
    /// <list type="number">
    ///   <item>EOF the SERVER side and JOIN the real <c>WorkStream</c> tasks;</item>
    ///   <item>EOF the WORKER side and JOIN the <c>run</c> task — the worker lifecycle reaches
    ///   quiescence while its stores and temp root are still alive;</item>
    ///   <item>DETACH and quiesce every constructor-subscribed downstream handler
    ///   (<c>TaskCompletionNotifier.OnTaskCompleted</c>, <c>DashboardNotifier.OnStateChanged</c>,
    ///   <c>TaskQueue.OnEnqueue</c> and the service logger's armed rendezvous), joining any handler
    ///   invocation still in flight;</item>
    ///   <item>only THEN dispose the stores, the worker harness and the temp root.</item>
    /// </list>
    /// </para>
    /// <para>
    /// THE PRIMARY FAILURE ALWAYS WINS. <see cref="DisposeAsync"/> never throws: a cleanup failure is
    /// captured and REPORTED through the guarded diagnostic, so an assertion failure remains the
    /// authoritative exception the test reports. A cleanup failure on an OTHERWISE PASSING test is
    /// surfaced by <see cref="AssertCleanTeardown"/>, which the tests call at the end of their happy
    /// path — so a broken teardown cannot hide behind a green run either.
    /// </para>
    /// </remarks>
    private sealed class LostAckFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly List<string> _cleanupFailures = [];
        private readonly List<string> _cleanupOrder = [];
        private int _injectCleanupFailureAfterJoins;
        private Task? _run;
        private bool _streamEnded;

        internal LostAckFixture()
        {
            _root = CreateRoot();
            Chain = new ServerChain(Path.Combine(_root, "receipts.db"), _root);
            Worker = Chain.BuildWorker();
            Bridge = Chain.StartBridge(Worker);
            Worker.Install(Bridge);
        }

        internal ServerChain Chain { get; }

        internal WorkerHarness Worker { get; }

        internal Bridge Bridge { get; }

        internal bool RootExists => Directory.Exists(_root);

        internal IReadOnlyList<string> CleanupFailures
        {
            get { lock (_cleanupFailures) return [.. _cleanupFailures]; }
        }

        internal IReadOnlyList<string> CleanupOrder
        {
            get { lock (_cleanupOrder) return [.. _cleanupOrder]; }
        }

        /// <summary>The worker's REAL lifecycle task, owned and joined by this fixture.</summary>
        internal Task Run => _run
            ?? throw new Xunit.Sdk.XunitException("StartWorkerRun must be called before Run is read.");

        /// <summary>Starts the REAL worker lifecycle and RETAINS its task for the guaranteed join.</summary>
        internal void StartWorkerRun()
        {
            if (_run is not null)
                throw new Xunit.Sdk.XunitException("The worker run has already been started.");

            _run = Worker.Service.RunAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// ENDS BOTH SIDES OF THE STREAM AND JOINS THE WORKER, in the production-meaningful order:
        /// the server side first (so its pump finishes), then the worker side (so the whole lost-ACK
        /// window above ran with BOTH sides live). Idempotent, so <see cref="DisposeAsync"/> can call
        /// it again safely on every path.
        /// </summary>
        internal async Task EndStreamAndJoinWorkerAsync()
        {
            if (_streamEnded)
                return;

            _streamEnded = true;

            Bridge.CompleteServerSide();
            await Bridge.JoinServerStreamAsync();
            RecordCleanupOrder("server-streams-joined");

            Bridge.CompleteWorkerSide();
            if (_run is { } run)
                await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            RecordCleanupOrder("worker-run-joined");

            // TEST-ONLY FAILURE INJECTION, deliberately AFTER both producer joins and BEFORE every
            // handler/resource cleanup. DisposeAsync must capture this secondary failure, continue
            // quiescence/disposal in order, and never replace an assertion already unwinding.
            if (Interlocked.Exchange(ref _injectCleanupFailureAfterJoins, 0) != 0)
                throw new InjectedCleanupFailureException();
        }

        /// <summary>
        /// Fails the test when teardown itself failed. Called at the END of a passing test, so a
        /// broken teardown surfaces on a green run without ever competing with a real assertion
        /// failure (on a failing path the primary exception is already unwinding and this is not
        /// reached).
        /// </summary>
        internal void AssertCleanTeardown()
        {
            lock (_cleanupFailures)
            {
                if (_cleanupFailures.Count > 0)
                    throw new Xunit.Sdk.XunitException(
                        "Teardown failed: " + string.Join(" ; ", _cleanupFailures));
            }
        }

        public async ValueTask DisposeAsync()
        {
            // 1-2. EOF BOTH SIDES AND JOIN THE SERVER STREAMS AND THE WORKER RUN — before any
            //      resource is released, so the worker never unwinds against a disposed store.
            await CaptureAsync("stream EOF / worker join", EndStreamAndJoinWorkerAsync);

            // On a FAILURE path the join above may itself time out; the run task is still observed
            // so a faulted lifecycle never becomes an unobserved task exception.
            await CaptureAsync("worker run observation", async () =>
            {
                if (_run is { } run)
                {
                    try
                    {
                        await run.WaitAsync(Failsafe, CancellationToken.None);
                    }
                    catch (Exception)
                    {
                        // The run's real outcome is asserted on the test's normal path; here it is
                        // only OBSERVED so it can never surface as an unobserved task exception.
                    }
                }
            });

            // 3. DETACH AND QUIESCE the constructor-subscribed downstream handlers, joining any
            //    handler invocation still in flight.
            await CaptureAsync("downstream handler quiescence", async () =>
            {
                await Chain.QuiesceSubscriptionsAsync();
                RecordCleanupOrder("handlers-joined");
            });

            // 4. ONLY NOW release resources.
            Capture("chain dispose", () =>
            {
                Chain.Dispose();
                RecordCleanupOrder("chain-disposed");
            });
            Capture("worker dispose", () =>
            {
                Worker.Dispose();
                RecordCleanupOrder("worker-disposed");
            });
            Capture("temp root delete", () =>
            {
                TryDelete(_root);
                RecordCleanupOrder("root-deleted");
            });
        }

        internal void InjectCleanupFailureForTest() =>
            Interlocked.Exchange(ref _injectCleanupFailureAfterJoins, 1);

        private void RecordCleanupOrder(string stage)
        {
            lock (_cleanupOrder)
            {
                if (!_cleanupOrder.Contains(stage, StringComparer.Ordinal))
                    _cleanupOrder.Add(stage);
            }
        }

        private void Capture(string stage, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Note(stage, ex);
            }
        }

        private async Task CaptureAsync(string stage, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Note(stage, ex);
            }
        }

        /// <summary>
        /// Records a cleanup failure WITHOUT throwing, so it can never replace an assertion failure
        /// that is already unwinding. The text is a type/stage classification only.
        /// </summary>
        private void Note(string stage, Exception ex)
        {
            lock (_cleanupFailures)
                _cleanupFailures.Add($"{stage}: {ex.GetType().Name}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  THE SERVER-SIDE PRODUCTION CHAIN
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REAL SERVER CHAIN: a REAL <see cref="HiveOrchestratorService"/> over a REAL
    /// <c>WorkerAssignmentPublisher</c> and a REAL <c>WorkerCompletionRecorder</c>, both backed by
    /// REAL SQLite stores, plus the pipeline/slot/queue machinery the publisher resolves against.
    /// </summary>
    private sealed class ServerChain : IDisposable
    {
        private readonly TaskCompletionSource<bool> _ordinaryReadyAccepted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _assignmentEnqueued =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _downstreamCompletions;
        private int _dashboardCompletionNotifications;
        private int _completionPhase;
        private int _ordinaryReadyAcceptedCount;

        internal ServerChain(string dbPath, string root)
        {
            Root = root;
            Stores = StoresImpl.FileBacked(dbPath);

            Pool = new WorkerPool();
            Queue = new TaskQueue();
            Manager = new GoalPipelineManager();
            Dashboard = new DashboardNotifier();
            Notifier = new TaskCompletionNotifier();
            Logger = new FragmentLogger<HiveOrchestratorService>();

            Pipeline = Manager.CreatePipeline(new Goal { Id = GoalId, Description = "lost-ack chain" });

            // THE PIPELINE, POINTER AND SLOT the real publisher resolves the delivered task against.
            Pipeline.AllocateAttemptAndRegisterSlot(TaskId, new WorkSlotPosition(1, GoalPhase.Coding, 1));
            Pipeline.SetActiveTask(TaskId);
            Manager.RegisterTask(TaskId, GoalId);

            // THE PENDING WORK the real Ready handler dequeues — enqueued as production would, so the
            // dequeue, the claim, the recording and the publication are all the REAL path.
            //
            // A REPOSITORY IS SUPPLIED DELIBERATELY: the executor's per-repo status probe is what
            // produces real GitStatus.ChangedFiles evidence, so the receipt carries NESTED, ORDERED
            // collections. That is what lets the clone-ownership vector mutate something real, and
            // what makes the complete-receipt comparison cover git evidence rather than nulls.
            Queue.Enqueue(new WorkTask
            {
                TaskId = TaskId,
                GoalId = GoalId,
                GoalDescription = "lost-ack chain",
                Prompt = "do the lost-ack work",
                Role = CopilotHive.Workers.WorkerRole.Coder,
                Model = AssignedModel,
                Repositories =
                [
                    new TargetRepository
                    {
                        Name = "repo-lost-ack",
                        Url = "https://example.invalid/repo-lost-ack",
                        DefaultBranch = "main",
                    },
                ],
            });

            RealPublisher = new WorkerAssignmentPublisher(Manager, Pool, Stores.AssignmentStore);
            Publisher = new RecordingPublisher(RealPublisher);
            Recorder = new CountingRecorder(
                new WorkerCompletionRecorder(Stores.AssignmentStore, Stores.ReceiptStore));

            // ── CONSTRUCTOR-SUBSCRIBED DOWNSTREAM HANDLERS, CONTROLLED AND JOINED ─────────────
            // The fixture owns each subscription so it can count AND detach it on teardown.
            Notifier.OnTaskCompleted += OnTaskCompleted;
            Dashboard.OnStateChanged += OnDashboardStateChanged;
            Queue.OnEnqueue += OnTaskEnqueued;

            // THE REAL Ready-acceptance line is armed on the service's OWN logger, so the milestone
            // is the production diagnostic itself. It is observed by ORDINAL OCCURRENCE, because the
            // INITIAL Ready legitimately logs it too: the SECOND occurrence is the ordinary Ready
            // this contract is about. Because the notification is raised immediately BEFORE that
            // line, awaiting the second occurrence also guarantees the handler's own notification has
            // already fired — which is what makes the count assertion below race-free.
            Logger.ArmOccurrence("is ready", 2, _ordinaryReadyAccepted, NoteOrdinaryReadyAccepted);

            var dispatcher = new GoalDispatcher(
                new GoalManager(),
                Manager,
                Queue,
                new GrpcWorkerGateway(Pool),
                Notifier,
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

            Service = new HiveOrchestratorService(
                Pool,
                Queue,
                Manager,
                Notifier,
                dispatcher,
                Logger,
                dashboardNotifier: Dashboard,
                assignmentPublisher: Publisher,
                completionRecorder: Recorder);
        }

        internal string Root { get; }

        internal StoresImpl Stores { get; }

        internal WorkerPool Pool { get; }

        internal TaskQueue Queue { get; }

        internal GoalPipelineManager Manager { get; }

        internal GoalPipeline Pipeline { get; }

        internal DashboardNotifier Dashboard { get; }

        internal IWorkerAssignmentPublisher RealPublisher { get; }

        internal RecordingPublisher Publisher { get; }

        internal CountingRecorder Recorder { get; }

        internal TaskCompletionNotifier Notifier { get; }

        internal FragmentLogger<HiveOrchestratorService> Logger { get; }

        internal HiveOrchestratorService Service { get; }

        /// <summary>The number of REAL downstream completion notifications observed.</summary>
        internal int DownstreamCompletions => Volatile.Read(ref _downstreamCompletions);

        internal int OrdinaryReadyAcceptedCount => Volatile.Read(ref _ordinaryReadyAcceptedCount);

        /// <summary>
        /// The number of dashboard state changes attributable to the COMPLETION path. Registration
        /// and assignment legitimately notify, so the count only opens once the worker has really
        /// written its first Complete — attribution by construction, not by a blanket reset.
        /// </summary>
        internal int DashboardCompletionNotifications =>
            Volatile.Read(ref _dashboardCompletionNotifications);

        /// <summary>The worker's retry interval, read from production's own constant.</summary>
        internal TimeSpan RetryInterval { get; } = (TimeSpan)typeof(WorkerService)
            .GetField("RetransmissionInterval", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        internal WorkerHarness BuildWorker()
        {
            var worker = new WorkerHarness(this);
            Publisher.WorkerHarnessRef = worker;
            return worker;
        }

        /// <summary>
        /// Opens the completion phase. Called by the worker-side writer the instant the FIRST Complete
        /// is written, so a dashboard notification from then on is attributable to the completion.
        /// </summary>
        internal void OpenCompletionPhase() => Interlocked.Exchange(ref _completionPhase, 1);

        internal Bridge StartBridge(WorkerHarness worker) => new(this, worker);

        /// <summary>
        /// Awaits the REAL server's receipt row DETERMINISTICALLY and reads it back through a FRESH
        /// store. The rendezvous is the forwarding recorder's own signal, raised from inside the
        /// decorator strictly AFTER the production <c>Record</c> returned, so there is NO polling
        /// and no sampling window: when this returns, the durable write has provably completed.
        /// </summary>
        internal async Task<CompletionReceiptReadResult> WaitForStoredReceiptAsync(TimeSpan bound)
        {
            await Recorder.FirstRecordStored.WaitAsync(bound, TestContext.Current.CancellationToken);

            return Stores.NewReceiptStore().Load(TaskId)
                ?? throw new Xunit.Sdk.XunitException(
                    "The REAL recorder returned from Record, so the receipt row must be readable "
                    + "through a fresh store.");
        }

        /// <summary>
        /// Awaits the REAL server's re-CONFIRMATION deterministically — the same forwarding-recorder
        /// technique, signalled after production's <c>ConfirmStoredReceipt</c> returned — and reads
        /// the receipt back through a FRESH store so the comparison is against durable state rather
        /// than a writer's own view.
        /// </summary>
        internal async Task<CompletionReceiptReadResult> WaitForReConfirmedReceiptAsync(TimeSpan bound)
        {
            await Recorder.FirstConfirmReturned.WaitAsync(bound, TestContext.Current.CancellationToken);

            return Stores.NewReceiptStore().Load(TaskId)
                ?? throw new Xunit.Sdk.XunitException(
                    "The REAL recorder returned from ConfirmStoredReceipt, so the receipt row must "
                    + "still be readable through a fresh store.");
        }

        internal Task WaitForOrdinaryReadyAcceptedAsync(TimeSpan bound) =>
            _ordinaryReadyAccepted.Task.WaitAsync(bound, TestContext.Current.CancellationToken);

        internal Task WaitForAssignmentEnqueuedAsync(TimeSpan bound) =>
            _assignmentEnqueued.Task.WaitAsync(bound, TestContext.Current.CancellationToken);

        internal WorkerConnection? PublishedConnection(WorkerService service) =>
            (WorkerConnection?)typeof(WorkerService)
                .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(service);

        /// <summary>
        /// DETACHES every fixture-owned subscription and JOINS any handler invocation still in
        /// flight, so no downstream handler can still be running when the stores are disposed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE CONSTRUCTOR SUBSCRIBES FOUR DOWNSTREAM HANDLERS — <c>TaskCompletionNotifier
        /// .OnTaskCompleted</c>, <c>DashboardNotifier.OnStateChanged</c>, <c>TaskQueue.OnEnqueue</c>
        /// and the service logger's armed Ready-acceptance rendezvous. All four are owned here: the
        /// three events are detached, the logger's arming is cleared, and the ONE asynchronous
        /// handler (<c>OnTaskCompleted</c> returns a <c>Task</c>) is joined through its retained
        /// invocation.
        /// </para>
        /// <para>
        /// DETACH THEN JOIN, in that order: detaching first means no NEW invocation can start while
        /// the join is running, so the join is over a closed set.
        /// </para>
        /// </remarks>
        internal async Task QuiesceSubscriptionsAsync()
        {
            DetachSubscriptions();

            // JOIN every handler invocation that was already in flight when it was detached.
            List<Task> inFlight;
            lock (_handlerInvocations)
                inFlight = [.. _handlerInvocations];

            foreach (var invocation in inFlight)
            {
                try
                {
                    await invocation.WaitAsync(Failsafe, CancellationToken.None);
                }
                catch (Exception)
                {
                    // OBSERVED only: a handler's own outcome is asserted on the test's normal path,
                    // and a faulted handler must not become an unobserved task exception.
                }
            }
        }

        /// <summary>Detaches every fixture-owned subscription. Idempotent.</summary>
        private void DetachSubscriptions()
        {
            if (Interlocked.Exchange(ref _subscriptionsDetached, 1) != 0)
                return;

            Notifier.OnTaskCompleted -= OnTaskCompleted;
            Dashboard.OnStateChanged -= OnDashboardStateChanged;
            Queue.OnEnqueue -= OnTaskEnqueued;
            Logger.ClearArmings();
        }

        private int _subscriptionsDetached;

        internal bool SubscriptionsDetached => Volatile.Read(ref _subscriptionsDetached) != 0;

        /// <summary>Every asynchronous downstream handler invocation this chain started.</summary>
        private readonly List<Task> _handlerInvocations = [];

        /// <summary>Detaches every fixture-owned subscription so no test leaves one behind.</summary>
        public void Dispose()
        {
            DetachSubscriptions();
            Stores.Dispose();
        }

        private Task OnTaskCompleted(TaskResult result)
        {
            lock (_notified)
                _notified.Add($"{result.TaskId}|{result.Status}|{result.Output.Length}");

            Interlocked.Increment(ref _downstreamCompletions);

            // The handler's own (already completed) task is RETAINED so the teardown join is over a
            // real invocation set rather than an assumption that handlers are synchronous.
            var invocation = Task.CompletedTask;
            lock (_handlerInvocations)
                _handlerInvocations.Add(invocation);

            return invocation;
        }

        private readonly List<string> _notified = [];

        internal IReadOnlyList<string> Notified
        {
            get { lock (_notified) return [.. _notified]; }
        }

        private void OnDashboardStateChanged()
        {
            if (Volatile.Read(ref _completionPhase) == 0)
                return;

            Interlocked.Increment(ref _dashboardCompletionNotifications);
        }

        private void OnTaskEnqueued(WorkTask task)
        {
            if (string.Equals(task.TaskId, TaskId, StringComparison.Ordinal))
                _assignmentEnqueued.TrySetResult(true);
        }

        internal void NoteOrdinaryReadyAccepted() =>
            Interlocked.Increment(ref _ordinaryReadyAcceptedCount);
    }

    /// <summary>
    /// THE IN-PROCESS BRIDGE: the two channels connecting the worker's duplex stream to the server's
    /// REAL <c>WorkStream</c> invocation, the REAL server stream task, and the ONE deliberate drop.
    /// </summary>
    private sealed class Bridge : IAsyncDisposable
    {
        private readonly ServerChain _chain;
        private readonly WorkerHarness _worker;
        private readonly Channel<WorkerMessage> _toServer = Channel.CreateUnbounded<WorkerMessage>();
        private readonly Channel<OrchestratorMessage> _toWorker = Channel.CreateUnbounded<OrchestratorMessage>();
        private readonly List<Task> _serverStreams = [];
        private readonly TaskCompletionSource<bool> _droppedAck =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _ackForwarded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _reAckForwarded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dropFirstAck;
        private int _ackDroppedCount;
        private int _ackWriteAttempts;
        private int _ackForwardedCount;
        private int _registerCalls;
        private int _streamOpens;
        private bool _serverCompleted;

        internal Bridge(ServerChain chain, WorkerHarness worker)
        {
            _chain = chain;
            _worker = worker;

            WorkerWriter = new WorkerMessageWriter(chain, _toServer);
            WorkerReader = new OrchestratorMessageReader(_toWorker);
            ServerReader = new RequestMessageReader(_toServer);
            ServerWriter = new DroppingResponseWriter(this, _toWorker);

            _serverStreams.Add(chain.Service.WorkStream(ServerReader, ServerWriter, MockContext()));
        }

        internal WorkerMessageWriter WorkerWriter { get; }

        internal OrchestratorMessageReader WorkerReader { get; }

        internal RequestMessageReader ServerReader { get; }

        internal DroppingResponseWriter ServerWriter { get; }

        internal IReadOnlyList<Task> ServerStreamTasks => _serverStreams;

        /// <summary>The number of REAL <c>WorkStream</c> invocations this bridge opened.</summary>
        internal int ServerStreamCount => _serverStreams.Count;

        internal int RegisterCalls => Volatile.Read(ref _registerCalls);

        internal bool LastRegistrationAccepted { get; private set; }

        internal bool LastRegistrationAckEnabled { get; private set; }

        internal bool LastRegistrationReadyRequired { get; private set; }

        internal int StreamOpens => Volatile.Read(ref _streamOpens);

        /// <summary>How many acknowledgements the REAL pump asked the bridge to write.</summary>
        internal int AckWriteAttempts => Volatile.Read(ref _ackWriteAttempts);

        /// <summary>How many acknowledgements were actually FORWARDED to the worker.</summary>
        internal int AckForwardedCount => Volatile.Read(ref _ackForwardedCount);

        internal Task DroppedAckObserved => _droppedAck.Task;

        internal Task FirstAckForwarded => _ackForwarded.Task;

        internal Task ReAckForwarded => _reAckForwarded.Task;

        /// <summary>Arms the ONE drop: the NEXT acknowledgement is swallowed, later ones forward.</summary>
        internal void ArmDropFirstAck() => Interlocked.Exchange(ref _dropFirstAck, 1);

        /// <summary>
        /// THE FORWARDING REGISTER PATH: the worker's <c>Register</c> RPC lands on the REAL service
        /// override, and its answer is returned verbatim to the worker.
        /// </summary>
        internal async Task<RegisterResponse> RegisterAsync(RegisterRequest request)
        {
            Interlocked.Increment(ref _registerCalls);
            var response = await _chain.Service.Register(request, MockContext());
            LastRegistrationAccepted = response.Accepted;
            LastRegistrationAckEnabled = response.CompletionReceiptAckEnabled;
            LastRegistrationReadyRequired = response.CompletionReadyRequired;
            return response;
        }

        internal void NoteStreamOpen() => Interlocked.Increment(ref _streamOpens);

        /// <summary>Applies the ONE drop, then forwards. Called by the server-side writer.</summary>
        internal bool TryDropAck()
        {
            Interlocked.Increment(ref _ackWriteAttempts);
            return Interlocked.Exchange(ref _dropFirstAck, 0) == 1;
        }

        internal void NoteAckDropped()
        {
            Interlocked.Increment(ref _ackDroppedCount);
            _droppedAck.TrySetResult(true);
        }

        /// <summary>
        /// Records a FORWARDED acknowledgement. The RE-ACKNOWLEDGEMENT rendezvous is the FIRST
        /// forwarded acknowledgement that FOLLOWS a swallowed one — which is exactly what "the server
        /// re-acknowledged the retransmission" means, whether or not the drop happened to be first.
        /// </summary>
        internal void NoteAckForwarded()
        {
            Interlocked.Increment(ref _ackForwardedCount);
            _ackForwarded.TrySetResult(true);

            if (Volatile.Read(ref _ackDroppedCount) >= 1)
                _reAckForwarded.TrySetResult(true);
        }

        /// <summary>EOF for the SERVER side only: the worker's side stays live.</summary>
        internal void CompleteServerSide()
        {
            if (_serverCompleted)
                return;

            _serverCompleted = true;
            ServerReader.Complete();
        }

        /// <summary>
        /// EOF for the WORKER side, applied only at teardown. It is deliberately SEPARATE from the
        /// acknowledgement drop: the lost-ACK window above happens with BOTH sides fully live, and the
        /// stream is only ended once every assertion has been taken.
        /// </summary>
        internal void CompleteWorkerSide() => WorkerReader.Complete();

        internal Task JoinServerStreamAsync() =>
            Task.WhenAll(_serverStreams).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        /// <summary>
        /// A LAST-RESORT sweep only. <see cref="LostAckFixture"/> owns the real ordering (EOF both
        /// sides, join the server streams, join the WORKER RUN, quiesce handlers, then dispose), so
        /// this deliberately does NOT dispose the worker harness or the stores — doing so here would
        /// re-introduce the very race the fixture exists to remove, by releasing resources while the
        /// worker lifecycle may still be unwinding.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            CompleteServerSide();
            try
            {
                await Task.WhenAll(_serverStreams).WaitAsync(Failsafe, CancellationToken.None);
            }
            catch (Exception)
            {
                // Quiescent: the real outcome is asserted on the test's normal path.
            }

            WorkerReader.Complete();
        }

        private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;
    }

    /// <summary>
    /// The worker-side request writer: it FORWARDS every <see cref="WorkerMessage"/> into the channel
    /// the REAL server <c>WorkStream</c> reads, and COUNTS by payload case — so "one Complete",
    /// "two Readies" and the frozen-evidence equality are all positive observations. The cancellable
    /// overload is implemented explicitly, exactly as production writes.
    /// <para>
    /// IT ALSO RETAINS THE RAW WRITER ARGUMENTS. <see cref="Of"/> hands out defensive CLONES so a
    /// value assertion is stable, but a clone MASKS the one mutant that matters here: a production
    /// that hands the SAME private snapshot object to every write would still look like two objects
    /// once each is cloned. <see cref="RawOf"/> therefore returns the EXACT objects production passed
    /// to <c>WriteAsync</c>, by reference, with no fixture-side copy in between — and the forwarding
    /// clone below is taken from the raw argument only AFTER it has been retained, so the server
    /// still receives an independent object exactly as before.
    /// </para>
    /// </summary>
    private sealed class WorkerMessageWriter : IClientStreamWriter<WorkerMessage>
    {
        private readonly ServerChain _chain;
        private readonly Channel<WorkerMessage> _sink;
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _messages = [];

        /// <summary>
        /// THE RAW WRITER ARGUMENTS, retained BY REFERENCE and never cloned — the only evidence the
        /// private-snapshot ownership assertions may use.
        /// </summary>
        private readonly List<WorkerMessage> _rawMessages = [];
        private readonly Dictionary<int, TaskCompletionSource<bool>> _waiters = [];

        internal WorkerMessageWriter(ServerChain chain, Channel<WorkerMessage> sink)
        {
            _chain = chain;
            _sink = sink;
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message) => WriteCoreAsync(message, CancellationToken.None);

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken ct)
            => WriteCoreAsync(message, ct);

        public Task CompleteAsync() => Task.CompletedTask;

        internal IReadOnlyList<WorkerMessage> Of(WorkerMessage.PayloadOneofCase payloadCase)
        {
            lock (_gate)
                return [.. _messages.Where(m => m.PayloadCase == payloadCase)];
        }

        /// <summary>
        /// The RAW writer arguments of the given payload case, oldest first — the exact objects
        /// production handed to <c>WriteAsync</c>, never cloned by this fixture.
        /// </summary>
        internal IReadOnlyList<WorkerMessage> RawOf(WorkerMessage.PayloadOneofCase payloadCase)
        {
            lock (_gate)
                return [.. _rawMessages.Where(m => m.PayloadCase == payloadCase)];
        }

        /// <summary>
        /// Completes with the <paramref name="index"/>-th message of the given payload case once it
        /// has been written — a positive rendezvous on the WORKER's own production write.
        /// </summary>
        internal async Task<WorkerMessage> WaitForMessageAsync(
            WorkerMessage.PayloadOneofCase payloadCase, int index, CancellationToken ct)
        {
            await WaitForAsync(payloadCase, index, ct);
            return Of(payloadCase)[index];
        }

        /// <summary>
        /// Completes once the <paramref name="index"/>-th message of the given payload case has been
        /// written.
        /// </summary>
        internal Task WaitForAsync(
            WorkerMessage.PayloadOneofCase payloadCase, int index, CancellationToken ct)
        {
            lock (_gate)
            {
                if (CountOf_Locked(payloadCase) > index)
                    return Task.CompletedTask;

                var key = Key(payloadCase, index);
                if (!_waiters.TryGetValue(key, out var waiter))
                {
                    waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[key] = waiter;
                }

                return waiter.Task.WaitAsync(Failsafe, ct);
            }
        }

        private Task WriteCoreAsync(WorkerMessage message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            List<TaskCompletionSource<bool>> ready = [];
            WorkerMessage forwarded;
            lock (_gate)
            {
                // THE RAW ARGUMENT FIRST, by reference: cloning it here would make a production
                // mutant that re-delivers its private snapshot object indistinguishable.
                _rawMessages.Add(message);
                _messages.Add(message.Clone());

                // The forwarding copy is taken NOW, while the raw argument is still exactly as
                // production handed it over, so the server receives an independent object and a
                // later fixture-side mutation of the raw argument cannot reach the server.
                forwarded = message.Clone();

                // The COMPLETION PHASE opens the instant the FIRST Complete is written, so any
                // dashboard notification from here on is attributable to the completion path.
                if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Complete
                    && CountOf_Locked(WorkerMessage.PayloadOneofCase.Complete) == 1)
                {
                    _chain.OpenCompletionPhase();
                }

                foreach (var (key, waiter) in _waiters)
                {
                    var payloadCase = (WorkerMessage.PayloadOneofCase)(key / 10_000);
                    var index = key % 10_000;
                    if (CountOf_Locked(payloadCase) > index)
                        ready.Add(waiter);
                }
            }

            foreach (var waiter in ready)
                waiter.TrySetResult(true);

            // FORWARD to the REAL server reader.
            _sink.Writer.TryWrite(forwarded);
            return Task.CompletedTask;
        }

        private int CountOf_Locked(WorkerMessage.PayloadOneofCase payloadCase) =>
            _messages.Count(m => m.PayloadCase == payloadCase);

        private static int Key(WorkerMessage.PayloadOneofCase payloadCase, int index) =>
            ((int)payloadCase * 10_000) + index;
    }

    /// <summary>
    /// The worker-side response reader: it yields what the REAL server pump forwards. The
    /// cancellable read path is the one production uses.
    /// </summary>
    private sealed class OrchestratorMessageReader(Channel<OrchestratorMessage> source)
        : IAsyncStreamReader<OrchestratorMessage>
    {
        private int _ackCount;

        public OrchestratorMessage Current { get; private set; } = new();

        internal int AckCount => Volatile.Read(ref _ackCount);

        internal void Complete() => source.Writer.TryComplete();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!await source.Reader.WaitToReadAsync(cancellationToken))
                return false;

            if (!source.Reader.TryRead(out var message))
                return false;

            Current = message;

            if (message.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck)
                Interlocked.Increment(ref _ackCount);

            return true;
        }
    }

    /// <summary>
    /// The server-side request reader: the REAL <c>WorkStream</c> consumes the worker's own writes
    /// from here. This is the server's only input, so no message can be manufactured for it.
    /// </summary>
    private sealed class RequestMessageReader(Channel<WorkerMessage> source) : IAsyncStreamReader<WorkerMessage>
    {
        public WorkerMessage Current { get; private set; } = new();

        internal void Complete() => source.Writer.TryComplete();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!await source.Reader.WaitToReadAsync(cancellationToken))
                return false;

            if (!source.Reader.TryRead(out var message))
                return false;

            Current = message;
            return true;
        }
    }

    /// <summary>
    /// THE SERVER'S RESPONSE WRITER — the ONE production pump's target, and the ONLY place the
    /// deliberate drop lives. Every message is forwarded to the worker's response channel, EXCEPT
    /// the FIRST acknowledgement after <see cref="Bridge.ArmDropFirstAck"/>, which is silently
    /// swallowed. There is no cancellation, no EOF and no message rewriting: a dropped
    /// acknowledgement is simply never enqueued for the worker.
    /// </summary>
    private sealed class DroppingResponseWriter(Bridge bridge, Channel<OrchestratorMessage> target)
        : IServerStreamWriter<OrchestratorMessage>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(OrchestratorMessage message)
        {
            if (message.PayloadCase == OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck)
            {
                if (bridge.TryDropAck())
                {
                    bridge.NoteAckDropped();
                    return Task.CompletedTask;
                }

                bridge.NoteAckForwarded();
            }

            target.Writer.TryWrite(message);
            return Task.CompletedTask;
        }

        public Task WriteAsync(OrchestratorMessage message, CancellationToken cancellationToken)
            => WriteAsync(message);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  THE WORKER SIDE
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REAL WORKER LIFECYCLE, driven over the bridge: a REAL <see cref="WorkerService.RunAsync"/>
    /// whose registration RPC forwards to the REAL server, whose duplex stream is the bridge, and
    /// whose retry clock is the fixture's own <see cref="ManualRetransmissionClock"/>.
    /// </summary>
    private sealed class WorkerHarness : IDisposable
    {
        private readonly ScriptedAgentRunner _runner = new();
        private readonly IDisposable _gitRestore;
        private readonly string _configRepoDir;
        private readonly TaskCompletionSource<WorkTask> _assignmentDelivered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        internal WorkerHarness(ServerChain chain)
        {
            Clock = new ManualRetransmissionClock();
            _configRepoDir = Path.Combine(chain.Root, "config-repo");
            Directory.CreateDirectory(_configRepoDir);

            _gitRestore = WorkerServiceConfigRepoHarness.InstallProcessRunner(new FakeGitLauncher(tokens =>
            {
                var command = string.Join(' ', tokens);
                if (command.Contains("rev-parse --is-inside-work-tree", StringComparison.Ordinal))
                    return new GitProcessResult(0, "true\n", string.Empty);
                if (command.Contains("rev-parse --show-toplevel", StringComparison.Ordinal))
                    return new GitProcessResult(0, _configRepoDir + "\n", string.Empty);
                if (command.Contains("remote get-url origin", StringComparison.Ordinal))
                    return new GitProcessResult(0, "https://example.invalid/config.git\n", string.Empty);
                if (command.Contains("rev-parse HEAD", StringComparison.Ordinal))
                    return new GitProcessResult(0, "0123456789abcdef0123456789abcdef01234567\n", string.Empty);
                if (command.Contains("diff --numstat", StringComparison.Ordinal))
                {
                    // NUL-DELIMITED records, exactly the `--numstat -z` shape the parser expects.
                    // SEVERAL files, so the ORDER of the resulting ChangedFiles is itself evidence
                    // the clone-ownership and complete-receipt comparisons can pin.
                    return new GitProcessResult(
                        0,
                        "2\t1\tsrc/one.cs\0" + "5\t0\tsrc/two.cs\0" + "0\t3\tsrc/three.cs\0",
                        string.Empty);
                }
                if (command.Contains("status --porcelain", StringComparison.Ordinal))
                    return new GitProcessResult(0, string.Empty, string.Empty);
                return new GitProcessResult(0, string.Empty, string.Empty);
            }));

            Service = new WorkerService("http://localhost:9999", WorkerId, ["coder"], _configRepoDir);

            var field = typeof(WorkerService)
                .GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
            if (field.GetValue(Service) is IAgentRunner existing)
                existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
            field.SetValue(Service, _runner);

            typeof(WorkerService)
                .GetProperty("TimeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(Service, Clock);
        }

        internal WorkerService Service { get; }

        internal ManualRetransmissionClock Clock { get; }

        internal Task<WorkTask> AssignmentDelivered => _assignmentDelivered.Task;

        internal int PromptCount => _runner.PromptCount;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>How many retry delay timers production created through the clock seam.</summary>
        internal int RetryDelayTimers => Clock.TimerCount;

        /// <summary>
        /// THE PRODUCTION ENTRY POINTS: <c>CallInvokerFactory</c> answers the worker's unary RPCs —
        /// with <c>Register</c> FORWARDED to the real service — and <c>WorkStreamFactory</c> returns
        /// the bridge's duplex stream.
        /// </summary>
        internal void Install(Bridge bridge)
        {
            Service.CallInvokerFactory = () => new WorkerCallInvoker(bridge);
            Service.WorkStreamFactory = (_, _) =>
            {
                bridge.NoteStreamOpen();
                return new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
                    bridge.WorkerWriter,
                    bridge.WorkerReader,
                    _ => Task.FromResult(new Metadata()),
                    _ => new Status(StatusCode.OK, string.Empty),
                    _ => new Metadata(),
                    _ => { },
                    null!);
            };
        }

        internal void ReleasePrompt() => _runner.Release();

        internal Task RetryDelayCreatedAsync(int count, CancellationToken ct) =>
            Clock.WaitForTimerCountAsync(count, ct);

        internal void NoteAssignmentDelivered(WorkTask task) => _assignmentDelivered.TrySetResult(task);

        public void Dispose()
        {
            _gitRestore.Dispose();
            Interlocked.Exchange(ref _disposed, 1);
        }
    }

    /// <summary>
    /// THE WORKER'S CALL INVOKER. <c>Register</c> is FORWARDED to the REAL
    /// <c>HiveOrchestratorService.Register</c> — so the worker's negotiated facts are production's
    /// answer — while the other unary RPCs the lifecycle can reach are answered locally (they are not
    /// part of this contract and nothing asserted here consults them).
    /// </summary>
    private sealed class WorkerCallInvoker(Bridge bridge) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Task<object> payload = method.FullName switch
            {
                "/copilothive.HiveOrchestrator/Register" => RegisterAsync((RegisterRequest)(object)request!),
                "/copilothive.HiveOrchestrator/Heartbeat" =>
                    Task.FromResult<object>(new HeartbeatResponse { Acknowledged = true }),
                "/copilothive.HiveOrchestrator/GetSession" =>
                    Task.FromResult<object>(new GetSessionResponse { Found = false }),
                "/copilothive.HiveOrchestrator/SaveSession" =>
                    Task.FromResult<object>(new SaveSessionResponse { Success = true }),
                "/copilothive.HiveOrchestrator/GetWorkerConfig" =>
                    Task.FromResult<object>(new GetWorkerConfigResponse()),

                // NO SILENT FALLBACK: any other unary RPC is a fixture bug and fails loudly rather
                // than being answered with a fabricated payload.
                _ => throw new NotSupportedException($"Unexpected unary call {method.FullName}."),
            };

            var response = payload.ContinueWith(
                t => (TResponse)t.GetAwaiter().GetResult(), TaskContinuationOptions.ExecuteSynchronously);

            return new AsyncUnaryCall<TResponse>(
                response,
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        private async Task<object> RegisterAsync(RegisterRequest request) =>
            await bridge.RegisterAsync(request);

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

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

    /// <summary>
    /// The agent runner. Its prompt parks until released and every ENTRY is counted, so the "one
    /// execution" claim is a positive count of real prompt invocations.
    /// </summary>
    private sealed class ScriptedAgentRunner : IAgentRunner
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _promptCount;

        internal int PromptCount => Volatile.Read(ref _promptCount);

        internal void Release() => _release.TrySetResult(true);

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            Interlocked.Increment(ref _promptCount);
            await _release.Task.WaitAsync(ct);
            return "worker output";
        }

        public TestResultReport? LastTestReport { get; } = new()
        {
            Verdict = CopilotHive.Workers.TaskVerdict.Pass,
            BuildSuccess = true,
            TotalTests = 5,
            PassedTests = 5,
            FailedTests = 0,
            CoveragePercent = 80,
            Issues = ["lost-ack-issue"],
            Summary = "lost-ack metrics",
        };

        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
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

    // ══════════════════════════════════════════════════════════════════════════
    //  FORWARDING DECORATORS AND REAL STORES
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FORWARDING PUBLISHER DECORATOR: it delegates to the REAL
    /// <c>WorkerAssignmentPublisher</c> FIRST and counts only on a successful return, so it can never
    /// make a delivery look published when the real publication was refused.
    /// </summary>
    private sealed class RecordingPublisher(IWorkerAssignmentPublisher inner) : IWorkerAssignmentPublisher
    {
        private int _publishCalls;

        internal int PublishCalls => Volatile.Read(ref _publishCalls);

        /// <summary>The last task the REAL publisher confirmed publication for.</summary>
        internal string? LastPublishedTask { get; private set; }

        /// <summary>The harness that records the delivery (assigned once the harness exists).</summary>
        internal WorkerHarness? WorkerHarnessRef { get; set; }

        public async Task PublishAsync(ConnectedWorker worker, WorkTask task, CancellationToken cancellationToken)
        {
            await inner.PublishAsync(worker, task, cancellationToken);

            Interlocked.Increment(ref _publishCalls);
            LastPublishedTask = task.TaskId;
            WorkerHarnessRef?.NoteAssignmentDelivered(task);
        }
    }

    /// <summary>
    /// THE FORWARDING RECORDER DECORATOR over the REAL <c>WorkerCompletionRecorder</c>. Every call is
    /// delegated FIRST — so the real durable write and the real confirmation are what actually happen
    /// — and counted afterwards, with RECORDS and CONFIRMATIONS counted separately so "one ordinary
    /// record" is never confused with the later read-only confirmation.
    /// <para>
    /// IT IS ALSO THE DURABLE-STATE RENDEZVOUS. Each signal is raised from INSIDE this forwarding
    /// path, strictly AFTER the real <c>Record</c> / <c>ConfirmStoredReceipt</c> has RETURNED — so a
    /// completed signal means the production write really finished, and the row is readable through a
    /// fresh store. That is what lets the fixture observe "the first durable receipt exists" and "the
    /// receipt was re-confirmed" WITHOUT polling: no <c>Task.Delay</c> loop, no sampling window, and
    /// no chance of reading a half-written row.
    /// </para>
    /// <para>
    /// A THROWING inner call signals NOTHING and propagates unchanged: the signal can never claim a
    /// durable write that did not happen.
    /// </para>
    /// </summary>
    private sealed class CountingRecorder(IWorkerCompletionRecorder inner) : IWorkerCompletionRecorder
    {
        private readonly TaskCompletionSource<bool> _firstRecordStored =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstConfirmReturned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _recordCalls;
        private int _confirmCalls;
        private int _confirmAccepted;

        internal int RecordCalls => Volatile.Read(ref _recordCalls);

        internal int ConfirmCalls => Volatile.Read(ref _confirmCalls);

        internal int ConfirmAccepted => Volatile.Read(ref _confirmAccepted);

        /// <summary>
        /// Completes once the REAL recorder's <c>Record</c> has RETURNED for the first completion —
        /// i.e. the durable receipt row is written and readable through a fresh store.
        /// </summary>
        internal Task FirstRecordStored => _firstRecordStored.Task;

        /// <summary>
        /// Completes once the REAL recorder's <c>ConfirmStoredReceipt</c> has RETURNED for the first
        /// time — i.e. the re-acknowledged receipt has been re-confirmed against durable state.
        /// </summary>
        internal Task FirstConfirmReturned => _firstConfirmReturned.Task;

        public void Record(string workerId, WorkTask task, TaskResult result)
        {
            // DELEGATE FIRST: the real durable write is what actually happens here.
            inner.Record(workerId, task, result);

            // ONLY NOW is the row durable, so only now may the rendezvous complete.
            Interlocked.Increment(ref _recordCalls);
            _firstRecordStored.TrySetResult(true);
        }

        public bool ConfirmStoredReceipt(string workerId, string taskId, TaskResult result)
        {
            var confirmed = inner.ConfirmStoredReceipt(workerId, taskId, result);

            Interlocked.Increment(ref _confirmCalls);
            if (confirmed)
                Interlocked.Increment(ref _confirmAccepted);

            _firstConfirmReturned.TrySetResult(true);
            return confirmed;
        }
    }

    /// <summary>
    /// Real file-backed SQLite stores. One instance backs the production collaborators, while
    /// <see cref="NewReceiptStore"/> hands out a FRESH store for readbacks that re-open the database
    /// rather than echo the writer's own view.
    /// </summary>
    private sealed class StoresImpl : IDisposable
    {
        private int _disposed;

        private StoresImpl(IDbContextFactory<CopilotHiveDbContext> factory, string dbPath)
        {
            Factory = factory;
            DbPath = dbPath;
            AssignmentStore = new WorkerAssignmentContextStore(
                factory, NullLogger<WorkerAssignmentContextStore>.Instance);
            ReceiptStore = new CompletionReceiptStore(factory, NullLogger<CompletionReceiptStore>.Instance);
        }

        internal IDbContextFactory<CopilotHiveDbContext> Factory { get; }

        private string DbPath { get; }

        internal WorkerAssignmentContextStore AssignmentStore { get; }

        internal CompletionReceiptStore ReceiptStore { get; }

        internal CompletionReceiptStore NewReceiptStore() =>
            new(Factory, NullLogger<CompletionReceiptStore>.Instance);

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal static StoresImpl FileBacked(string dbPath)
        {
            var factory = new FileDbContextFactory(dbPath);
            using (var bootstrap = factory.CreateDbContext())
                bootstrap.Database.EnsureCreated();

            return new StoresImpl(factory, dbPath);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Interlocked.Exchange(ref _disposed, 1);
        }

        private sealed class FileDbContextFactory(string dbPath) : IDbContextFactory<CopilotHiveDbContext>
        {
            public CopilotHiveDbContext CreateDbContext()
            {
                var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                    .UseSqlite($"Data Source={dbPath}")
                    .Options;

                return new CopilotHiveDbContext(options);
            }
        }
    }

    /// <summary>
    /// A logger that records every rendered message and completes an armed rendezvous when its
    /// FRAGMENT has been observed the required number of times — so a server-side milestone is
    /// awaited positively (and, where a line legitimately repeats, by ORDINAL OCCURRENCE) rather than
    /// by polling.
    /// </summary>
    private sealed class FragmentLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];
        private readonly Dictionary<string, Arming> _armed = new(StringComparer.Ordinal);

        internal IReadOnlyList<string> Messages
        {
            get { lock (_gate) return [.. _messages]; }
        }

        /// <summary>Arms a rendezvous for the <paramref name="occurrence"/>-th appearance of a fragment.</summary>
        internal void ArmOccurrence(
            string fragment, int occurrence, TaskCompletionSource<bool> signal, Action? onArmed = null)
        {
            lock (_gate)
                _armed[fragment] = new Arming(occurrence, signal, onArmed);
        }

        /// <summary>
        /// CLEARS every armed rendezvous — the logger's own "subscription" — so no armed callback can
        /// run after the fixture has begun tearing down. Idempotent.
        /// </summary>
        internal void ClearArmings()
        {
            lock (_gate)
                _armed.Clear();
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var rendered = formatter(state, exception);

            List<Arming> ready = [];
            lock (_gate)
            {
                _messages.Add(rendered);

                foreach (var (fragment, arming) in _armed)
                {
                    if (!rendered.Contains(fragment, StringComparison.Ordinal))
                        continue;

                    arming.Observed++;
                    if (arming.Observed >= arming.Occurrence)
                        ready.Add(arming);
                }
            }

            foreach (var arming in ready)
            {
                arming.OnArmed?.Invoke();
                arming.Signal.TrySetResult(true);
            }
        }

        /// <summary>One armed rendezvous: how many times the fragment has been seen, and its target.</summary>
        private sealed class Arming(
            int occurrence, TaskCompletionSource<bool> signal, Action? onArmed)
        {
            internal int Occurrence { get; private set; } = occurrence;

            internal TaskCompletionSource<bool> Signal { get; } = signal;

            internal Action? OnArmed { get; } = onArmed;

            /// <summary>How many times the fragment has been observed. Written under the logger's lock.</summary>
            internal int Observed { get; set; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  shared helpers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ASSERTS THAT THE COMPLETE PERSISTED RECEIPT IS UNCHANGED across the re-acknowledgement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// BOTH VALUES ARE READ BACK THROUGH A FRESH <c>CompletionReceiptStore</c> over the same database
    /// file, so this compares DURABLE STATE on both sides — never a locally constructed input against
    /// itself, and never a writer's own in-memory view.
    /// </para>
    /// <para>
    /// THE WHOLE VALUE IS COMPARED, TWO WAYS. First the CANONICAL ENCODING: production's own
    /// <see cref="CompletionReceiptCodec.Encode"/> is the exact serialization the store round-trips,
    /// so an ordinal equality of the two encodings covers EVERY field the codec carries — including
    /// any field a future change adds, which a hand-listed comparison would silently skip. Then the
    /// individual fields are asserted as well, so a failure NAMES what moved instead of printing two
    /// long JSON blobs: identifiers (worker, goal, task), role, slot position (iteration, phase,
    /// occurrence) and attempt, model PRESENCE and value, result status/output, the complete metrics
    /// (verdict, build flag, test counts, coverage, issues IN ORDER, summary), the complete git
    /// evidence (counts, pushed flag, changed files IN ORDER) and the iteration SHA — plus
    /// <c>FirstStoredAtUtc</c>, which is what proves the row was not re-inserted.
    /// </para>
    /// </remarks>
    /// <param name="before">The receipt read back BEFORE the re-acknowledgement.</param>
    /// <param name="after">The receipt read back AFTER the re-acknowledgement.</param>
    private static void AssertPersistedReceiptUnchanged(
        CompletionReceiptReadResult before, CompletionReceiptReadResult after)
    {
        // NON-VACUITY: the two reads are genuinely separate objects from separate store instances,
        // so an equality below can never be an object comparing to itself.
        Assert.NotSame(before, after);
        Assert.NotSame(before.Receipt, after.Receipt);

        // ── THE WHOLE VALUE, through production's own canonical serialization ─────────────
        Assert.Equal(
            CompletionReceiptCodec.Encode(before.Receipt),
            CompletionReceiptCodec.Encode(after.Receipt));

        // ── AND FIELD BY FIELD, so a regression names itself ─────────────────────────────
        Assert.Equal(before.Receipt.WorkerId, after.Receipt.WorkerId);
        Assert.Equal(before.Receipt.GoalId, after.Receipt.GoalId);
        Assert.Equal(before.Receipt.Role, after.Receipt.Role);

        Assert.Equal(before.Receipt.Slot.TaskId, after.Receipt.Slot.TaskId);
        Assert.Equal(before.Receipt.Slot.Attempt, after.Receipt.Slot.Attempt);
        Assert.Equal(before.Receipt.Slot.Position.Iteration, after.Receipt.Slot.Position.Iteration);
        Assert.Equal(before.Receipt.Slot.Position.Phase, after.Receipt.Slot.Position.Phase);
        Assert.Equal(before.Receipt.Slot.Position.Occurrence, after.Receipt.Slot.Position.Occurrence);

        var beforeResult = before.Receipt.Result;
        var afterResult = after.Receipt.Result;
        Assert.Equal(beforeResult.TaskId, afterResult.TaskId);
        Assert.Equal(beforeResult.Status, afterResult.Status);
        Assert.Equal(beforeResult.Output, afterResult.Output);
        Assert.Equal(beforeResult.IterationStartSha, afterResult.IterationStartSha);

        // MODEL PRESENCE AND VALUE are separate facts: an empty model means "assigned model
        // unknown/empty" and must never silently become absent.
        Assert.Equal(beforeResult.Model is null, afterResult.Model is null);
        Assert.Equal(beforeResult.Model, afterResult.Model);

        Assert.Equal(beforeResult.Metrics is null, afterResult.Metrics is null);
        if (beforeResult.Metrics is { } beforeMetrics && afterResult.Metrics is { } afterMetrics)
        {
            Assert.Equal(beforeMetrics.Verdict, afterMetrics.Verdict);
            Assert.Equal(beforeMetrics.BuildSuccess, afterMetrics.BuildSuccess);
            Assert.Equal(beforeMetrics.TotalTests, afterMetrics.TotalTests);
            Assert.Equal(beforeMetrics.PassedTests, afterMetrics.PassedTests);
            Assert.Equal(beforeMetrics.FailedTests, afterMetrics.FailedTests);
            Assert.Equal(beforeMetrics.CoveragePercent, afterMetrics.CoveragePercent);
            Assert.Equal(beforeMetrics.Summary, afterMetrics.Summary);

            // IN ORDER: a reordered issue list is a changed receipt.
            Assert.Equal(beforeMetrics.Issues, afterMetrics.Issues);
        }

        Assert.Equal(beforeResult.GitStatus is null, afterResult.GitStatus is null);
        if (beforeResult.GitStatus is { } beforeGit && afterResult.GitStatus is { } afterGit)
        {
            Assert.Equal(beforeGit.FilesChanged, afterGit.FilesChanged);
            Assert.Equal(beforeGit.Insertions, afterGit.Insertions);
            Assert.Equal(beforeGit.Deletions, afterGit.Deletions);
            Assert.Equal(beforeGit.Pushed, afterGit.Pushed);

            // IN ORDER: the changed-file evidence is ordered evidence.
            Assert.Equal(beforeGit.ChangedFiles, afterGit.ChangedFiles);
        }

        // THE ROW WAS NEVER RE-INSERTED: the first-stored instant is preserved exactly.
        Assert.Equal(before.FirstStoredAtUtc, after.FirstStoredAtUtc);
    }

    private sealed class InjectedCleanupFailureException : Exception;

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "copilothive-lost-ack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
