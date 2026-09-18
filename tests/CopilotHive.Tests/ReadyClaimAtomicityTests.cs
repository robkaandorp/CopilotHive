using System.Reflection;

using CopilotHive.Agents;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// THE READY-DRIVEN CHECKED CLAIM, exercised through the real
/// <c>HandleWorkerReady</c> boundary: the claim is the ONE publication point, a refused claim puts
/// the ACTUAL dequeued task back exactly once and changes nothing else, and a caller cancellation
/// observed before the claim requeues once while a post-claim cancellation never requeues.
/// </summary>
/// <remarks>
/// THE WINDOW IS REACHED THROUGH THE PRODUCTION HOOK, never by scheduling luck: the handler exposes
/// one null-default synchronous hook after the dequeue/cancellation check and before the claim, so a
/// competing owner or a completion hold can be installed INSIDE the real interval the claim exists to
/// close. Production leaves the hook null, so every vector below is the production path plus a
/// deliberate mutation of ownership at the documented point.
/// </remarks>
public sealed class ReadyClaimAtomicityTests
{
    private const string WorkerId = "w-ready-claim";

    /// <summary>Upper bound for every await in these vectors — never a fixed delay.</summary>
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    // ── fixture ───────────────────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public required HiveOrchestratorService Service { get; init; }
        public required WorkerPool Pool { get; init; }
        public required TaskQueue Queue { get; init; }
        public required ConnectedWorker Worker { get; init; }
        public required CapturingLogger Logger { get; init; }
        public required RecordingPublisher Publisher { get; init; }
        public int Notifications { get; set; }
        public int NotifyCount => _notifyCount;
        private int _notifyCount;

        private readonly List<string> _tempDirs = [];

        public static Fixture Create(
            CapturingLogger? logger = null,
            bool withAgentsManager = false,
            bool withPublisher = true)
        {
            var pool = new WorkerPool();
            var queue = new TaskQueue();
            var pipelineManager = new GoalPipelineManager();
            var completionNotifier = new TaskCompletionNotifier();
            var goalManager = new GoalManager();
            var dispatcher = new GoalDispatcher(
                goalManager,
                pipelineManager,
                queue,
                new GrpcWorkerGateway(pool),
                completionNotifier,
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

            var notifier = new DashboardNotifier();
            var capturingLogger = logger ?? new CapturingLogger();
            var publisher = new RecordingPublisher();

            AgentsManager? agentsManager = null;
            if (withAgentsManager)
            {
                var agentsPath = Path.Combine(
                    Path.GetTempPath(), $"copilothive-ready-claim-agents-{Guid.NewGuid():N}");
                agentsManager = new AgentsManager(agentsPath);
                File.WriteAllText(
                    Path.Combine(agentsPath, $"{WorkerRole.Coder.ToRoleName()}.agents.md"),
                    "coder guidance");
                capturingLogger.TempDir = agentsPath;
            }

            var service = new HiveOrchestratorService(
                pool,
                queue,
                pipelineManager,
                completionNotifier,
                dispatcher,
                capturingLogger,
                agentsManager: agentsManager,
                dashboardNotifier: notifier,
                assignmentPublisher: withPublisher ? publisher : null);

            var worker = pool.RegisterWorker(WorkerId, []);

            var fixture = new Fixture
            {
                Service = service,
                Pool = pool,
                Queue = queue,
                Worker = worker,
                Logger = capturingLogger,
                Publisher = publisher,
            };

            notifier.OnStateChanged += () => Interlocked.Increment(ref fixture._notifyCount);
            return fixture;
        }

        public void Dispose()
        {
            foreach (var dir in _tempDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Best-effort — a leftover temp directory must never fail a test.
                }
            }
        }
    }

    /// <summary>A task whose role and model make the published claim observable.</summary>
    private static WorkTask BuildTask(string taskId, string model = "claim-model") => new()
    {
        TaskId = taskId,
        GoalId = "goal-ready-claim",
        GoalDescription = "claim atomically",
        Prompt = "do the work",
        Role = WorkerRole.Coder,
        Model = model,
        Repositories = [],
    };

    /// <summary>
    /// Drives the REAL private <c>HandleWorkerReady</c> once. The method returns a task, so a fault
    /// inside it surfaces as its ORIGINAL exception rather than as a reflection wrapper.
    /// </summary>
    private static async Task InvokeReadyAsync(
        HiveOrchestratorService service, ConnectedWorker worker, CancellationToken ct = default)
    {
        var method = typeof(HiveOrchestratorService)
            .GetMethod("HandleWorkerReady", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(service, [worker, new NullStreamWriter(), ct])!;
    }

    private sealed class NullStreamWriter : IServerStreamWriter<OrchestratorMessage>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(OrchestratorMessage message) => Task.CompletedTask;

        public Task WriteAsync(OrchestratorMessage message, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// THE RECORDING PUBLISHER SEAM: records the EXACT instance and the EXACT task it was handed, so
    /// a vector can prove the delivered assignment went through the production publication point
    /// after the claim — and that nothing at all was published on a refused path.
    /// </summary>
    private sealed class RecordingPublisher : IWorkerAssignmentPublisher
    {
        private readonly List<(ConnectedWorker Worker, WorkTask Task)> _calls = [];

        public IReadOnlyList<(ConnectedWorker Worker, WorkTask Task)> Calls
        {
            get
            {
                lock (_calls)
                    return [.. _calls];
            }
        }

        public Task PublishAsync(
            ConnectedWorker worker, WorkTask task, CancellationToken cancellationToken)
        {
            lock (_calls)
                _calls.Add((worker, task));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records every logged message. <see cref="ThrowOnAgentsMd"/> makes the GUIDANCE step fail for a
    /// non-cancellation reason, which is what proves the post-claim guidance is best effort rather
    /// than load-bearing.
    /// </summary>
    private sealed class CapturingLogger : ILogger<HiveOrchestratorService>
    {
        private readonly List<string> _messages = [];

        public bool ThrowOnAgentsMd { get; set; }

        public string? TempDir { get; set; }

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
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
            var message = formatter(state, exception);
            lock (_messages)
                _messages.Add(message);

            // THE GUIDANCE FAILURE, deterministically: the upper-case production wording of the
            // agents.md send is what this logger refuses to emit.
            if (ThrowOnAgentsMd && message.Contains("AGENTS.md", StringComparison.Ordinal))
                throw new InvalidOperationException("the logger refused to emit the AGENTS.md message");
        }
    }

    // ── the refusal path ──────────────────────────────────────────────────────

    /// <summary>
    /// A COMPETING OWNER THAT WINS INSIDE THE WINDOW REFUSES THE CLAIM: the ACTUAL dequeued task goes
    /// back EXACTLY ONCE, the winner's ownership survives untouched, and this Ready publishes,
    /// records, notifies and writes NO role.
    /// </summary>
    [Fact]
    public async Task Ready_ClaimRefusedByACompetingOwner_RequeuesTheActualTaskOnceAndChangesNothingElse()
    {
        var f = Fixture.Create();
        var task = BuildTask("task-loses-the-race");
        f.Queue.Enqueue(task);

        // THE COMPETITOR WINS in the real post-dequeue/pre-claim window.
        f.Service.OnBeforeReadyClaimForTest = () => f.Pool.MarkBusy(WorkerId, "task-winner");

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // THE WINNER IS UNTOUCHED.
        Assert.True(f.Worker.IsBusy);
        Assert.Equal("task-winner", f.Worker.CurrentTaskId);

        // THE LOSER'S OWN TASK IS BACK, EXACTLY ONCE, AS THE VERY SAME INSTANCE.
        Assert.Same(task, f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.TryDequeueAny());

        // ZERO PUBLICATION: the offered task never reached the active queue, was never tagged for this
        // worker, was never published, and never wrote a role.
        Assert.Null(f.Queue.GetActiveTask(task.TaskId));
        Assert.False(task.Metadata.ContainsKey("assigned_worker"));
        Assert.Empty(f.Publisher.Calls);
        Assert.Equal(0, f.NotifyCount);
        Assert.Equal(WorkerRole.Unspecified, f.Worker.Role);
        Assert.Null(f.Worker.CurrentModel);

        // THE REFUSAL IS REPORTED HONESTLY, naming the guard and the requeue.
        Assert.Contains(
            f.Logger.Messages,
            m => m.Contains("claim refused", StringComparison.Ordinal)
                 && m.Contains(
                     HiveOrchestratorService.OwnershipRefusalReasons.ReadyClaimRefused,
                     StringComparison.Ordinal)
                 && m.Contains("requeued exactly once", StringComparison.Ordinal));
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
    }

    /// <summary>
    /// AN ACTIVE COMPLETION-PUBLICATION HOLD INSTALLED IN THE WINDOW REFUSES THE CLAIM: a Ready may
    /// not claim a worker whose negotiated completion is still being published, and the refusal is
    /// the same single requeue with no publication.
    /// </summary>
    [Fact]
    public async Task Ready_ClaimRefusedByACompletionHold_RequeuesOnceAndLeavesTheHoldInForce()
    {
        var f = Fixture.Create();
        var task = BuildTask("task-under-hold");
        f.Queue.Enqueue(task);

        f.Service.OnBeforeReadyClaimForTest = () =>
        {
            f.Pool.MarkBusy(WorkerId, "task-completing");
            Assert.True(f.Pool.TryReleaseCompletedTaskHoldingForPublication(
                f.Worker, "task-completing"));
        };

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // THE HOLD IS STILL IN FORCE — the claim neither cleared it nor idled the instance.
        Assert.True(f.Worker.CompletionPublicationPending);
        Assert.False(f.Worker.IsBusy);
        Assert.Null(f.Worker.CurrentTaskId);

        Assert.Same(task, f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.GetActiveTask(task.TaskId));
        Assert.Empty(f.Publisher.Calls);
        Assert.Equal(0, f.NotifyCount);
    }

    /// <summary>
    /// THE POST-REFUSAL DELIVERY IS STILL VALID: once the competitor releases the worker, a LATER
    /// Ready delivers the SAME requeued task through the production publication point.
    /// </summary>
    [Fact]
    public async Task Ready_AfterARefusedClaim_TheRequeuedTaskIsStillDeliveredByALaterReady()
    {
        var f = Fixture.Create();
        var task = BuildTask("task-delivered-later");
        f.Queue.Enqueue(task);

        f.Service.OnBeforeReadyClaimForTest = () => f.Pool.MarkBusy(WorkerId, "task-winner");
        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);
        Assert.Equal(0, f.NotifyCount);

        // THE COMPETITOR FINISHES and the window hook is withdrawn — production shape again.
        Assert.True(f.Pool.TryReleaseCompletedTask(f.Worker, "task-winner"));
        f.Service.OnBeforeReadyClaimForTest = null;

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // THE VERY SAME DEQUEUED INSTANCE was claimed, published and delivered.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Equal(task.Model, f.Worker.CurrentModel);
        Assert.Equal(WorkerRole.Coder, f.Worker.Role);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Equal(1, f.NotifyCount);
        Assert.Null(f.Queue.TryDequeueAny());
    }

    // ── caller cancellation ───────────────────────────────────────────────────

    /// <summary>
    /// A CALLER CANCELLATION OBSERVED BEFORE THE CLAIM PUTS THE ACTUAL TASK BACK EXACTLY ONCE AND
    /// PROPAGATES THE ORIGINAL CANCELLATION: nothing is published, recorded, notified or written.
    /// </summary>
    [Fact]
    public async Task Ready_CancelledBeforeTheClaim_RequeuesOnceAndPropagatesTheOriginalCancellation()
    {
        var f = Fixture.Create();
        var task = BuildTask("task-cancelled-before-claim");
        f.Queue.Enqueue(task);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => InvokeReadyAsync(f.Service, f.Worker, cts.Token));

        Assert.Equal(cts.Token, thrown.CancellationToken);

        Assert.Same(task, f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.GetActiveTask(task.TaskId));
        Assert.False(task.Metadata.ContainsKey("assigned_worker"));
        Assert.Empty(f.Publisher.Calls);
        Assert.Equal(0, f.NotifyCount);
        Assert.False(f.Worker.IsBusy);
        Assert.Equal(WorkerRole.Unspecified, f.Worker.Role);
    }

    /// <summary>
    /// A CANCELLATION OBSERVED AFTER THE CLAIM NEVER REQUEUES: the claim stands — the worker stays
    /// busy with the task and the queue keeps its active entry — while the cancellation still
    /// propagates rather than being converted into a published assignment.
    /// </summary>
    /// <remarks>
    /// THE GUIDANCE HELPER IS PRESENT AND IT SWALLOWS THE CANCELLATION INTERNALLY, which is the
    /// shape that makes the explicit pre-publication observation load-bearing: the agents.md write
    /// is refused by the cancelled token and contained as a best-effort failure, so nothing but that
    /// observation can stop a cancelled delivery from being published.
    /// </remarks>
    [Fact]
    public async Task Ready_CancelledAfterTheClaim_NeverRequeuesAndPropagates()
    {
        var f = Fixture.Create(withAgentsManager: true);
        var task = BuildTask("task-cancelled-after-claim");
        f.Queue.Enqueue(task);

        using var cts = new CancellationTokenSource();

        // THE CANCELLATION LANDS INSIDE THE WINDOW — after the pre-claim check, before the claim.
        f.Service.OnBeforeReadyClaimForTest = () => cts.Cancel();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => InvokeReadyAsync(f.Service, f.Worker, cts.Token));

        Assert.Equal(cts.Token, thrown.CancellationToken);

        // THE CLAIM STANDS AND NOTHING WAS PUT BACK.
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Null(f.Queue.TryDequeueAny());

        // The accepted claim notified exactly once; the cancelled delivery published nothing.
        Assert.Equal(1, f.NotifyCount);
        Assert.Empty(f.Publisher.Calls);
    }

    // ── the requeue hook and the guidance ─────────────────────────────────────
    /// <summary>
    /// THE REQUEUE INSERT HAPPENS BEFORE ITS HOOK, SO A THROWING HOOK PROPAGATES AFTER THE ONE AND
    /// ONLY INSERT: the task really is back in the queue, and there is no retry and no second insert.
    /// </summary>
    [Fact]
    public async Task Ready_RefusalPath_EnqueueHookThrowsAfterTheSingleInsert()
    {
        var f = Fixture.Create();
        var task = BuildTask("task-requeue-hook-throws");
        f.Queue.Enqueue(task);

        // INSTALLED AFTER THE SETUP ENQUEUE, so only the requeue can invoke it.
        f.Queue.OnEnqueue = _ => throw new InvalidOperationException("the enqueue hook threw");

        f.Service.OnBeforeReadyClaimForTest = () => f.Pool.MarkBusy(WorkerId, "task-winner");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken));

        Assert.Equal("the enqueue hook threw", thrown.Message);

        // THE INSERT ALREADY HAPPENED — exactly once, and the same instance.
        Assert.Same(task, f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.TryDequeueAny());
        Assert.Empty(f.Publisher.Calls);
        Assert.Equal(0, f.NotifyCount);
    }

    /// <summary>
    /// POST-CLAIM GUIDANCE IS BEST EFFORT FOR A NON-CANCELLATION FAILURE: the agents.md update fails,
    /// the failure is contained with a sanitized diagnostic, and the ACCEPTED assignment still
    /// reaches the production publication point with the exact claimed instance.
    /// </summary>
    [Fact]
    public async Task Ready_GuidanceFailureAfterTheClaim_IsContainedAndPublicationContinues()
    {
        var f = Fixture.Create(withAgentsManager: true);
        f.Logger.ThrowOnAgentsMd = true;

        var task = BuildTask("task-guidance-fails");
        f.Queue.Enqueue(task);

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // THE CLAIM STANDS.
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Null(f.Queue.TryDequeueAny());

        // PUBLICATION CONTINUED, with the EXACT claimed instance and the ACTUAL task.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.Equal(1, f.NotifyCount);

        // THE GUIDANCE FAILURE IS REPORTED AS A DEGRADED STEP — never as a refusal or a success.
        Assert.Contains(
            f.Logger.Messages,
            m => m.Contains("agents.md update failed after the assignment was claimed",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
    }

    // ── ApplyTaskAssignment ───────────────────────────────────────────────────

    /// <summary>
    /// THE DIRECT ASSIGNMENT ENTRY POINT EXPOSES THE CLAIM'S OUTCOME: <c>true</c> with the whole
    /// publication and exactly ONE notification for an accepted claim, <c>false</c> with NO mutation
    /// and NO notification for a refused one.
    /// </summary>
    [Fact]
    public void ApplyTaskAssignment_ReturnsTheClaimOutcomeAndNotifiesOnlyOnAcceptance()
    {
        var f = Fixture.Create();

        var task = BuildTask("task-applied");
        Assert.True(f.Service.ApplyTaskAssignment(f.Worker, task));

        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Equal(task.Model, f.Worker.CurrentModel);
        Assert.Equal(WorkerRole.Coder, f.Worker.Role);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Equal(1, f.NotifyCount);

        // A SECOND ASSIGNMENT TO THE NOW-BUSY INSTANCE IS REFUSED — and changes nothing.
        var successor = BuildTask("task-successor", "successor-model");
        Assert.False(f.Service.ApplyTaskAssignment(f.Worker, successor));

        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Equal(task.Model, f.Worker.CurrentModel);
        Assert.Null(f.Queue.GetActiveTask(successor.TaskId));
        Assert.False(successor.Metadata.ContainsKey("assigned_worker"));
        Assert.Equal(1, f.NotifyCount);
    }

    /// <summary>
    /// AN INSTANCE THE POOL DOES NOT REGISTER IS REFUSED BY THE CLAIM, with no mutation and no
    /// notification — the ABA/foreign-instance shape at the direct entry point.
    /// </summary>
    [Fact]
    public void ApplyTaskAssignment_UnregisteredInstance_IsRefusedAndNotifiesNothing()
    {
        var f = Fixture.Create();
        var foreign = new ConnectedWorker
        {
            Id = WorkerId,
            Role = WorkerRole.Unspecified,
            Capabilities = [],
        };
        var task = BuildTask("task-foreign-claim");

        Assert.False(f.Service.ApplyTaskAssignment(foreign, task));

        Assert.False(foreign.IsBusy);
        Assert.Null(foreign.CurrentTaskId);
        Assert.Null(f.Queue.GetActiveTask(task.TaskId));
        Assert.False(task.Metadata.ContainsKey("assigned_worker"));
        Assert.Equal(0, f.NotifyCount);
    }
}
