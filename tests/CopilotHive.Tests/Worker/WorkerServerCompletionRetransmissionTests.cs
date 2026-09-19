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
    [Fact]
    public async Task LostFirstAck_OnALiveStream_WorkerResendsAndTheRealServerConfirmsAndReadies()
    {
        var root = CreateRoot();
        try
        {
            var chain = new ServerChain(Path.Combine(root, "receipts.db"), root);
            try
            {
                var worker = chain.BuildWorker();
                await using var bridge = chain.StartBridge(worker);
                worker.Install(bridge);

                var run = worker.Service.RunAsync(TestContext.Current.CancellationToken);

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

                // THE REAL SERVER RECORDED THE RECEIPT DURABLY (read back through a FRESH store).
                var receipt = await chain.WaitForStoredReceiptAsync(Failsafe);
                Assert.Equal(TaskId, receipt.Receipt.Slot.TaskId);
                Assert.Equal(WorkerId, receipt.Receipt.WorkerId);
                Assert.Equal(GoalId, receipt.Receipt.GoalId);

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

                var original = bridge.WorkerWriter.Of(WorkerMessage.PayloadOneofCase.Complete)[0];
                var resent = bridge.WorkerWriter.Of(WorkerMessage.PayloadOneofCase.Complete)[1];
                Assert.NotSame(original, resent);
                Assert.NotSame(original.Complete, resent.Complete);
                Assert.Equal(original.WorkerId, resent.WorkerId);
                Assert.Equal(original.Complete, resent.Complete);

                // ── THE REAL SERVER CONFIRMS AND RE-ACKS ────────────────────────────────────────
                await bridge.ReAckForwarded.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.Equal(1, chain.Recorder.ConfirmCalls);
                Assert.Equal(1, chain.Recorder.ConfirmAccepted);
                Assert.Equal(1, chain.Recorder.RecordCalls);

                // THE RECEIPT IS UNCHANGED: the re-acknowledgement re-recorded and rebound nothing.
                var afterReAck = chain.Stores.NewReceiptStore().Load(TaskId);
                Assert.NotNull(afterReAck);
                Assert.Equal(receipt.Receipt.Slot.TaskId, afterReAck.Receipt.Slot.TaskId);
                Assert.Equal(receipt.Receipt.WorkerId, afterReAck.Receipt.WorkerId);
                Assert.Equal(receipt.Receipt.GoalId, afterReAck.Receipt.GoalId);
                Assert.Equal(receipt.Receipt.Result.Output, afterReAck.Receipt.Result.Output);
                Assert.Equal(receipt.Receipt.Result.Model, afterReAck.Receipt.Result.Model);
                Assert.Equal(receipt.FirstStoredAtUtc, afterReAck.FirstStoredAtUtc);

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
                bridge.CompleteServerSide();
                await bridge.JoinServerStreamAsync();

                // EOF for the WORKER side last, so the whole lost-ACK window above ran with BOTH
                // sides of the stream fully live.
                bridge.CompleteWorkerSide();
                await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.True(run.IsCompletedSuccessfully, "the worker lifecycle must end normally: " + run.Status);
            }
            finally
            {
                chain.Dispose();
            }        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// THE CONTROL: with NO acknowledgement dropped, the FIRST acknowledgement alone authorizes the
    /// single ordinary Ready and the retry never even arms a delay — so the vector above is genuinely
    /// about the DROPPED message and not about a stream that simply never acknowledges.
    /// </summary>
    [Fact]
    public async Task NoDroppedAck_TheFirstAcknowledgementAloneAuthorizesTheSingleReady()
    {
        var root = CreateRoot();
        try
        {
            var chain = new ServerChain(Path.Combine(root, "receipts.db"), root);
            try
            {
                var worker = chain.BuildWorker();
                await using var bridge = chain.StartBridge(worker);
                worker.Install(bridge);

                var run = worker.Service.RunAsync(TestContext.Current.CancellationToken);

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

                // NO RETRY WAS EVER NEEDED: production created AT MOST the one delay timer whose
                // wait the first acknowledgement then cancelled, and no second Complete was written.
                Assert.True(worker.RetryDelayTimers <= 1, "a forwarded first ACK must not require retries");
                Assert.Equal(ExpectedPromptCount, worker.PromptCount);
                Assert.Single(bridge.WorkerWriter.Of(WorkerMessage.PayloadOneofCase.Complete));
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

                bridge.CompleteServerSide();
                await bridge.JoinServerStreamAsync();

                // EOF for the WORKER side last, so the whole lost-ACK window above ran with BOTH
                // sides of the stream fully live.
                bridge.CompleteWorkerSide();
                await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.True(run.IsCompletedSuccessfully);
            }
            finally
            {
                chain.Dispose();
            }
        }
        finally
        {
            TryDelete(root);
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
            Queue.Enqueue(new WorkTask
            {
                TaskId = TaskId,
                GoalId = GoalId,
                GoalDescription = "lost-ack chain",
                Prompt = "do the lost-ack work",
                Role = CopilotHive.Workers.WorkerRole.Coder,
                Model = AssignedModel,
                Repositories = [],
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

        /// <summary>Awaits the REAL server's receipt row, read through a FRESH store.</summary>
        internal async Task<CompletionReceiptReadResult> WaitForStoredReceiptAsync(TimeSpan bound)
        {
            var deadline = DateTime.UtcNow + bound;
            while (DateTime.UtcNow < deadline)
            {
                var loaded = Stores.NewReceiptStore().Load(TaskId);
                if (loaded is not null)
                    return loaded;

                await Task.Delay(5, TestContext.Current.CancellationToken);
            }

            throw new Xunit.Sdk.XunitException(
                "The REAL server never durably recorded the completion receipt.");
        }

        internal Task WaitForOrdinaryReadyAcceptedAsync(TimeSpan bound) =>
            _ordinaryReadyAccepted.Task.WaitAsync(bound, TestContext.Current.CancellationToken);

        internal Task WaitForAssignmentEnqueuedAsync(TimeSpan bound) =>
            _assignmentEnqueued.Task.WaitAsync(bound, TestContext.Current.CancellationToken);

        internal WorkerConnection? PublishedConnection(WorkerService service) =>
            (WorkerConnection?)typeof(WorkerService)
                .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(service);

        /// <summary>Detaches every fixture-owned subscription so no test leaves one behind.</summary>
        public void Dispose()
        {
            Notifier.OnTaskCompleted -= OnTaskCompleted;
            Dashboard.OnStateChanged -= OnDashboardStateChanged;
            Queue.OnEnqueue -= OnTaskEnqueued;
            Stores.Dispose();
        }

        private Task OnTaskCompleted(TaskResult result)
        {
            lock (_notified)
                _notified.Add($"{result.TaskId}|{result.Status}|{result.Output.Length}");

            Interlocked.Increment(ref _downstreamCompletions);
            return Task.CompletedTask;
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

        public async ValueTask DisposeAsync()
        {
            CompleteServerSide();
            try
            {
                await Task.WhenAll(_serverStreams).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }
            catch (Exception)
            {
                // Quiescent: the real outcome is asserted on the test's normal path.
            }

            WorkerReader.Complete();
            _worker.Dispose();
        }

        private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;
    }

    /// <summary>
    /// The worker-side request writer: it FORWARDS every <see cref="WorkerMessage"/> into the channel
    /// the REAL server <c>WorkStream</c> reads, and COUNTS by payload case — so "one Complete",
    /// "two Readies" and the frozen-evidence equality are all positive observations. The cancellable
    /// overload is implemented explicitly, exactly as production writes.
    /// </summary>
    private sealed class WorkerMessageWriter : IClientStreamWriter<WorkerMessage>
    {
        private readonly ServerChain _chain;
        private readonly Channel<WorkerMessage> _sink;
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _messages = [];
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
            lock (_gate)
            {
                _messages.Add(message.Clone());

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

            // FORWARD to the REAL server reader. The clone is deliberate: the recorded observation
            // must not be mutable by any later consumer.
            _sink.Writer.TryWrite(message.Clone());
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
                    return new GitProcessResult(0, "2\t1\tsrc/one.cs\0", string.Empty);
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

        public void Dispose() => _gitRestore.Dispose();
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
    /// </summary>
    private sealed class CountingRecorder(IWorkerCompletionRecorder inner) : IWorkerCompletionRecorder
    {
        private int _recordCalls;
        private int _confirmCalls;
        private int _confirmAccepted;

        internal int RecordCalls => Volatile.Read(ref _recordCalls);

        internal int ConfirmCalls => Volatile.Read(ref _confirmCalls);

        internal int ConfirmAccepted => Volatile.Read(ref _confirmAccepted);

        public void Record(string workerId, WorkTask task, TaskResult result)
        {
            inner.Record(workerId, task, result);
            Interlocked.Increment(ref _recordCalls);
        }

        public bool ConfirmStoredReceipt(string workerId, string taskId, TaskResult result)
        {
            var confirmed = inner.ConfirmStoredReceipt(workerId, taskId, result);
            Interlocked.Increment(ref _confirmCalls);
            if (confirmed)
                Interlocked.Increment(ref _confirmAccepted);

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

        internal static StoresImpl FileBacked(string dbPath)
        {
            var factory = new FileDbContextFactory(dbPath);
            using (var bootstrap = factory.CreateDbContext())
                bootstrap.Database.EnsureCreated();

            return new StoresImpl(factory, dbPath);
        }

        public void Dispose() => SqliteConnection.ClearAllPools();

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
