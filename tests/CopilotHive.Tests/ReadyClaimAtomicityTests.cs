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

        private int _notifyCount;

        public int NotifyCount => _notifyCount;

        private string? _agentsPath;

        /// <summary>
        /// THE REAL AGENTS ROOT this fixture handed to <see cref="AgentsManager"/>, or <c>null</c>
        /// when the fixture was built without one. A vector that must fault the production read has
        /// to target THIS root — a freshly generated one would be a different directory the service
        /// never opens.
        /// </summary>
        public string AgentsRoot =>
            _agentsPath ?? throw new InvalidOperationException(
                "this fixture was created without an AgentsManager, so it has no agents root");

        /// <summary>
        /// THE EXACT FILE the production guidance read opens for <paramref name="role"/>, composed
        /// the same way <c>AgentsManager.GetAgentsMdPath</c> composes it (root + role name).
        /// </summary>
        public string AgentsFilePath(WorkerRole role) =>
            Path.Combine(AgentsRoot, $"{role.ToRoleName()}.agents.md");

        public static Fixture Create(
            CapturingLogger? logger = null,
            bool withAgentsManager = false,
            bool withPublisher = true,
            bool ackEnabled = false)
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
            string? agentsPath = null;
            if (withAgentsManager)
            {
                agentsPath = Path.Combine(
                    Path.GetTempPath(), $"copilothive-ready-claim-agents-{Guid.NewGuid():N}");
                agentsManager = new AgentsManager(agentsPath);
                File.WriteAllText(
                    Path.Combine(agentsPath, $"{WorkerRole.Coder.ToRoleName()}.agents.md"),
                    "coder guidance");
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

            var worker = pool.RegisterWorker(
                WorkerId, [], requestCompletionReceiptAck: ackEnabled, completionReceiptAckEnabled: ackEnabled);

            var fixture = new Fixture
            {
                Service = service,
                Pool = pool,
                Queue = queue,
                Worker = worker,
                Logger = capturingLogger,
                Publisher = publisher,
                _agentsPath = agentsPath,
            };

            notifier.OnStateChanged += () => Interlocked.Increment(ref fixture._notifyCount);
            return fixture;
        }

        public void Dispose()
        {
            if (_agentsPath is null)
                return;

            try
            {
                if (Directory.Exists(_agentsPath))
                    Directory.Delete(_agentsPath, recursive: true);
            }
            catch
            {
                // Best-effort — a leftover temp directory must never fail a test.
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

        /// <summary>
        /// THE PUBLISHER FAULT SEAM, for the publisher-failure controls: when non-null, the next
        /// (and every) invocation records its call and then faults with this EXACT exception, so a
        /// vector chooses the failure kind without a new fixture type.
        /// </summary>
        public Exception? Failure { get; set; }

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
            var failure = Failure;
            lock (_calls)
                _calls.Add((worker, task));
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    /// <summary>One captured log call, including the exception OBJECT the logger was handed.</summary>
    /// <remarks>
    /// THE EXCEPTION OBJECT IS PART OF THE OBSERVATION ON PURPOSE. A real logger renders a supplied
    /// exception's raw <c>ToString()</c> (message AND stack) into its output, so "was an exception
    /// object passed" is exactly the fact a sanitization vector must be able to assert.
    /// </remarks>
    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>
    /// Records every logged message. <see cref="ThrowOnAgentsMd"/> makes the GUIDANCE step fail for a
    /// non-cancellation reason, which is what proves the post-claim guidance is best effort rather
    /// than load-bearing; <see cref="ThrowFactory"/> is the finer-grained form that lets a vector
    /// choose WHICH log line fails and WITH WHICH exception.
    /// </summary>
    private sealed class CapturingLogger : ILogger<HiveOrchestratorService>
    {
        private readonly List<LogEntry> _entries = [];

        public bool ThrowOnAgentsMd { get; set; }

        /// <summary>
        /// Returns the exception this logger must throw for the given rendered message, or
        /// <c>null</c> to record it normally. Lets one vector fail exactly one log line.
        /// </summary>
        public Func<string, Exception?>? ThrowFactory { get; set; }

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_entries)
                    return [.. _entries.Select(e => e.Message)];
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
            lock (_entries)
                _entries.Add(new LogEntry(logLevel, message, exception));

            // THE GUIDANCE FAILURE, deterministically: the upper-case production wording of the
            // agents.md send is what this logger refuses to emit.
            if (ThrowOnAgentsMd && message.Contains("AGENTS.md", StringComparison.Ordinal))
                throw new InvalidOperationException("the logger refused to emit the AGENTS.md message");

            var selected = ThrowFactory?.Invoke(message);
            if (selected is not null)
                throw selected;
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

    /// <summary>
    /// THE READINESS WAIT GATES THE READY ROUTE TOO, AND ONE ACCEPTED READY IS ENOUGH: an
    /// ACK-enabled instance whose negotiated completion was released — hold already cleared — is
    /// refused by the checked claim, so the dequeued task goes back exactly once; the SAME task is
    /// then delivered by ONE later Ready, which is what actually clears the wait.
    /// </summary>
    /// <remarks>
    /// THE WINDOW IS REACHED THROUGH THE PRODUCTION HOOK, so the completion is installed INSIDE the
    /// real post-dequeue/pre-claim interval the claim exists to close. It proves the readiness wait
    /// refuses at the same boundary the publication hold does, and that the refusal is not itself a
    /// deferred acceptance: a LATER Ready is genuinely required.
    /// </remarks>
    [Fact]
    public async Task Ready_WhileAwaitingTheInstancesOwnReady_RefusesThenDeliversOnTheNextReady()
    {
        var f = Fixture.Create(ackEnabled: true);
        Assert.True(f.Worker.CompletionReceiptAckEnabled);

        var task = BuildTask("task-awaiting-ready");
        f.Queue.Enqueue(task);

        // THE COMPLETION IS RELEASED THROUGH THE REAL HOLDING ROUTE INSIDE THE WINDOW, and its short
        // publication hold is then cleared — so the ONLY fact withholding the instance is the
        // readiness wait the same release installed.
        f.Service.OnBeforeReadyClaimForTest = () =>
        {
            f.Pool.MarkBusy(WorkerId, "task-completing");
            Assert.True(f.Pool.TryReleaseCompletedTaskHoldingForPublication(
                f.Worker, "task-completing"));
            Assert.True(f.Pool.ClearCompletionPublicationHold(f.Worker));
        };

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // THE CLAIM REFUSED, THE TASK WENT BACK EXACTLY ONCE, AND NOTHING WAS PUBLISHED.
        Assert.True(f.Worker.AwaitingWorkerReady);
        Assert.False(f.Worker.CompletionPublicationPending);
        Assert.False(f.Worker.IsBusy);
        Assert.Null(f.Queue.GetActiveTask(task.TaskId));
        Assert.Empty(f.Publisher.Calls);
        Assert.Equal(0, f.NotifyCount);

        // The requeued instance is the VERY SAME one, and it is put back so the later Ready has
        // something to deliver (TaskQueue exposes no count, so the peek must restore what it took).
        Assert.Same(task, f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.TryDequeueAny());
        f.Queue.Enqueue(task);

        // ── ONE LATER READY — PRODUCTION SHAPE AGAIN — DELIVERS THE VERY SAME INSTANCE ────────
        f.Service.OnBeforeReadyClaimForTest = null;
        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.False(f.Worker.AwaitingWorkerReady);
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Equal(1, f.NotifyCount);
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

    /// <summary>
    /// THE CANCELLATION STAYS PRIMARY ON ITS OWN PATH: a throwing enqueue hook cannot replace it,
    /// because the insert already happened and a second insert would duplicate the task. The task is
    /// back exactly once and the ORIGINAL cancellation is what escapes.
    /// </summary>
    [Fact]
    public async Task Ready_CancelledBeforeTheClaim_EnqueueHookThrowDoesNotReplaceTheCancellation()
    {
        var f = Fixture.Create();
        var task = BuildTask("task-cancelled-hook-throws");
        f.Queue.Enqueue(task);

        // INSTALLED AFTER THE SETUP ENQUEUE, so only the requeue can invoke it.
        f.Queue.OnEnqueue = _ => throw new InvalidOperationException("the enqueue hook threw");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => InvokeReadyAsync(f.Service, f.Worker, cts.Token));

        Assert.Equal(cts.Token, thrown.CancellationToken);

        // THE INSERT ALREADY HAPPENED — exactly once, and the same instance.
        Assert.Same(task, f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.TryDequeueAny());
        Assert.Empty(f.Publisher.Calls);
        Assert.Equal(0, f.NotifyCount);
    }

    /// <summary>
    /// THE CANCELLATION STAYS PRIMARY EVEN WHEN THE ENQUEUE HOOK THROWS AN
    /// <see cref="OperationCanceledException"/> OF ITS OWN. A hook OCE — carrying a FOREIGN token or
    /// the default one — must NOT become the outcome: it is contained like any other hook fault
    /// after the single insert, and the CALLER's cancellation (with the CALLER's token) is thrown.
    /// </summary>
    /// <remarks>
    /// WHY THE EXISTING HOOK VECTOR CANNOT CATCH THIS. That one throws an
    /// <see cref="InvalidOperationException"/>, which any <c>catch (Exception)</c> contains. Only an
    /// OCE distinguishes "contain EVERY hook fault" from "rethrow a caught cancellation": with the
    /// defective rethrow, the foreign token below escapes and this vector fails.
    /// </remarks>
    /// <param name="useDefaultToken">
    /// Whether the hook's OCE carries <see cref="CancellationToken.None"/> (the parameterless
    /// shape) instead of a distinct live token.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ready_CancelledBeforeTheClaim_HookCancellationNeverReplacesTheCallerCancellation(
        bool useDefaultToken)
    {
        var f = Fixture.Create();
        var task = BuildTask($"task-hook-oce-{useDefaultToken}");
        f.Queue.Enqueue(task);

        // A DIFFERENT, LIVE cancellation source — never the caller's.
        using var foreignCts = new CancellationTokenSource();
        foreignCts.Cancel();

        var hookFailure = useDefaultToken
            ? new OperationCanceledException("hook cancellation with the default token")
            : new OperationCanceledException(
                "hook cancellation with a foreign token", foreignCts.Token);

        // INSTALLED AFTER THE SETUP ENQUEUE, so only the requeue can invoke it.
        f.Queue.OnEnqueue = _ => throw hookFailure;

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => InvokeReadyAsync(f.Service, f.Worker, callerCts.Token));

        // THE CALLER'S CANCELLATION IS THE OUTCOME OF RECORD — not the hook's instance, not its
        // token, and not its message.
        Assert.NotSame(hookFailure, thrown);
        Assert.Equal(callerCts.Token, thrown.CancellationToken);
        Assert.NotEqual(foreignCts.Token, thrown.CancellationToken);
        Assert.DoesNotContain("hook cancellation", thrown.Message, StringComparison.Ordinal);

        // THE INSERT ALREADY HAPPENED — exactly once, the same instance, and no retry.
        Assert.Same(task, f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.TryDequeueAny());

        // NOTHING WAS PUBLISHED, RECORDED, NOTIFIED OR WRITTEN.
        Assert.Null(f.Queue.GetActiveTask(task.TaskId));
        Assert.False(task.Metadata.ContainsKey("assigned_worker"));
        Assert.Empty(f.Publisher.Calls);
        Assert.Equal(0, f.NotifyCount);
        Assert.False(f.Worker.IsBusy);
        Assert.Null(f.Worker.CurrentTaskId);
        Assert.Equal(WorkerRole.Unspecified, f.Worker.Role);
        Assert.Null(f.Worker.CurrentModel);
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("Assigning task", StringComparison.Ordinal));
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
    /// <remarks>
    /// RETARGETED ONTO THE SEND-FAILURE DIAGNOSTIC. This vector previously drove the OUTER guidance
    /// warning by making BOTH of the send's log lines throw, which relied on the send-failure
    /// diagnostic's exception ESCAPING <c>SendAgentsMdAsync</c> — the very defect now fixed by
    /// guarding that emission. The contained failure is now reported by the send's own guarded
    /// diagnostic, so that is what this vector asserts; the best-effort property is unchanged.
    /// </remarks>
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
            m => m.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal));
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
    }

    // ── the guidance diagnostics are sanitized ────────────────────────────────

    /// <summary>
    /// UNTRUSTED TEXT THAT WOULD FORGE LOG LINES: LF, CR, TAB, DEL and a C1 character, plus the
    /// Unicode line separator. Every one of these is a character the boundary sanitizer replaces.
    /// </summary>
    private const string ControlCharacterPayload =
        "boom\nFORGED level=Information Assignment published\r\tinjected\u007Fdel\u0085nel\u0090c1\u2028sep";

    /// <summary>
    /// Asserts a rendered log line carries NO character that could break it into several lines —
    /// the property the boundary sanitizer exists to guarantee.
    /// </summary>
    private static void AssertSingleSanitizedLine(string rendered)
    {
        var offender = rendered.FirstOrDefault(LogSanitizer.IsLogUnsafe);
        Assert.True(
            offender == default,
            $"the emitted log line carries the raw control character U+{(int)offender:X4}: '{rendered}'");

        // The forged continuation cannot exist as its own line, because no line break survived.
        Assert.DoesNotContain('\n', rendered);
        Assert.DoesNotContain('\r', rendered);
    }

    /// <summary>
    /// THE <c>SendAgentsMdAsync</c> FAILURE DIAGNOSTIC IS SANITIZED AND CARRIES NO EXCEPTION OBJECT:
    /// a guidance send that fails with control-character text is reported as a single, bounded,
    /// sanitized line, and the raw exception is never handed to the logger (which would render its
    /// unsanitized message and stack).
    /// </summary>
    /// <remarks>
    /// THE SEAM IS THE PRODUCTION SUCCESS LOG INSIDE THE SEND'S OWN <c>try</c>: failing it drives the
    /// send's catch deterministically, with an exception whose message this vector controls.
    /// </remarks>
    [Fact]
    public async Task Ready_AgentsMdSendFailure_IsLoggedSanitizedAndWithoutTheExceptionObject()
    {
        var f = Fixture.Create(withAgentsManager: true);
        f.Logger.ThrowFactory = message =>
            message.StartsWith("Sent AGENTS.md", StringComparison.Ordinal)
                ? new InvalidOperationException(ControlCharacterPayload)
                : null;

        var task = BuildTask("task-agentsmd-sanitized");
        f.Queue.Enqueue(task);

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            f.Logger.Entries,
            e => e.Message.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal));

        // (i) NO RAW EXCEPTION OBJECT: a logger handed one renders its unsanitized text and stack.
        Assert.Null(entry.Exception);

        // (ii) THE RENDERED LINE IS SANITIZED — the payload's own characters are gone…
        AssertSingleSanitizedLine(entry.Message);
        Assert.DoesNotContain(ControlCharacterPayload, entry.Message, StringComparison.Ordinal);
        // …while the readable remainder still identifies the failure.
        Assert.Contains("boom", entry.Message, StringComparison.Ordinal);
        Assert.Contains(f.Worker.Id, entry.Message, StringComparison.Ordinal);

        // (iii) THE FAILURE STAYED BEST EFFORT: the claim stands and publication continued with the
        // EXACT claimed instance and the ACTUAL task.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Null(f.Queue.TryDequeueAny());
    }

    /// <summary>
    /// THE SEND-FAILURE DIAGNOSTIC IS SANITIZED AND CARRIES NO EXCEPTION OBJECT, for a failure whose
    /// message is pure control-character payload.
    /// </summary>
    /// <remarks>
    /// RETARGETED. This vector previously made BOTH send log lines throw so the fault would ESCAPE
    /// into the outer guidance warning — the escape that was the defect. It now pins the same
    /// sanitization contract on the guarded send-failure diagnostic that renders it, through the
    /// SAME <c>SanitizedFailureDetail</c> helper.
    /// </remarks>
    [Fact]
    public async Task Ready_GuidanceFailure_IsLoggedSanitizedAndWithoutTheExceptionObject()
    {
        var f = Fixture.Create(withAgentsManager: true);
        f.Logger.ThrowFactory = message =>
            message.StartsWith("Sent AGENTS.md", StringComparison.Ordinal)
                ? new InvalidOperationException(ControlCharacterPayload)
                : null;

        var task = BuildTask("task-guidance-sanitized");
        f.Queue.Enqueue(task);

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            f.Logger.Entries,
            e => e.Message.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal));

        Assert.Null(entry.Exception);
        AssertSingleSanitizedLine(entry.Message);
        Assert.DoesNotContain(ControlCharacterPayload, entry.Message, StringComparison.Ordinal);
        Assert.Contains("boom", entry.Message, StringComparison.Ordinal);

        // NO FORGED LINE: the injected text cannot masquerade as a separate published-assignment
        // record, because the line break that would have created it is gone.
        Assert.DoesNotContain(
            f.Logger.Entries,
            e => e.Message.StartsWith("FORGED", StringComparison.Ordinal));

        // BEST EFFORT, UNCHANGED: the claim stands and publication continued on the exact instance.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.True(f.Worker.IsBusy);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Equal(1, f.NotifyCount);
    }

    /// <summary>
    /// THE NO-THROW GUARD SURVIVES SANITIZATION: neither a logger that throws ON the send-failure
    /// diagnostic itself nor an exception whose <c>Message</c> getter throws can escape the guarded
    /// diagnostic or mask the primary outcome.
    /// </summary>
    /// <remarks>
    /// RETARGETED ONTO THE NEWLY GUARDED EMISSION. It used to prove the OUTER guidance warning's
    /// guard by relying on the send-failure diagnostic escaping first; now it proves the SEND's own
    /// guard directly — the fix under review — by making that very emission throw.
    /// </remarks>
    [Fact]
    public async Task Ready_GuidanceDiagnostic_ThrowingLoggerAndMessageGetterCannotEscape()
    {
        var f = Fixture.Create(withAgentsManager: true);
        f.Logger.ThrowFactory = message =>
            // The send fails with an exception whose MESSAGE GETTER throws, so the diagnostic must
            // render it through its no-throw read…
            message.StartsWith("Sent AGENTS.md", StringComparison.Ordinal)
                ? new ThrowingMessageException()
                // …and the send-failure diagnostic ITSELF then throws too.
                : message.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal)
                    ? new InvalidOperationException("the send-failure warning's logger threw")
                    : null;

        var task = BuildTask("task-guidance-guard");
        f.Queue.Enqueue(task);

        // MUST NOT THROW: neither failure escapes the guarded diagnostic.
        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // The guarded warning really was attempted (so the guard is what contained the throw)…
        var entry = Assert.Single(
            f.Logger.Entries,
            e => e.Message.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal));
        AssertSingleSanitizedLine(entry.Message);
        Assert.Null(entry.Exception);

        // …and the PRIMARY OUTCOME is untouched: the claim stands and publication continued.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.True(f.Worker.IsBusy);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
    }

    /// <summary>
    /// A LOGGER THAT THROWS AN <see cref="OperationCanceledException"/> WHILE EMITTING THE SEND-FAILURE
    /// DIAGNOSTIC CANNOT BECOME THE OUTCOME. With the caller token already cancelled, an unguarded
    /// emission would let the LOGGER's OCE leave <c>SendAgentsMdAsync</c>, match the Ready path's
    /// broad caller-cancellation filter (which only tests that the caller token is requested) and be
    /// rethrown in place of the caller's cancellation — carrying a FOREIGN token and message.
    /// </summary>
    /// <remarks>
    /// THIS IS WHAT THE GUARD BUYS. Contained, the send helper returns normally and the retained
    /// explicit <c>ThrowIfCancellationRequested</c> observation is what produces the outcome, so the
    /// CALLER token is authoritative. The claim is post-claim state and must survive intact.
    /// </remarks>
    /// <param name="useDefaultToken">
    /// Whether the logger's OCE carries <see cref="CancellationToken.None"/> (the parameterless
    /// shape) instead of a distinct live token.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ready_SendFailureDiagnosticLoggerCancellation_NeverReplacesTheCallerCancellation(
        bool useDefaultToken)
    {
        const string loggerMessage = "logger cancellation emitted while reporting the send failure";

        var f = Fixture.Create(withAgentsManager: true);
        var task = BuildTask($"task-send-diag-oce-{useDefaultToken}");
        f.Queue.Enqueue(task);

        // A DIFFERENT, LIVE cancellation source — never the caller's.
        using var foreignCts = new CancellationTokenSource();
        foreignCts.Cancel();

        var loggerFailure = useDefaultToken
            ? new OperationCanceledException(loggerMessage)
            : new OperationCanceledException(loggerMessage, foreignCts.Token);

        // ONLY the send-failure diagnostic fails, and it fails with a CANCELLATION.
        f.Logger.ThrowFactory = message =>
            message.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal)
                ? loggerFailure
                : null;

        using var callerCts = new CancellationTokenSource();

        // THE CANCELLATION LANDS INSIDE THE WINDOW, so the claim succeeds first and the guidance
        // send then fails on the cancelled token — driving the send-failure diagnostic.
        f.Service.OnBeforeReadyClaimForTest = () => callerCts.Cancel();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => InvokeReadyAsync(f.Service, f.Worker, callerCts.Token));

        // (a)+(b) THE LOGGER'S OCE DID NOT ESCAPE: the outcome is the CALLER's cancellation, not the
        // logger's instance, and it carries the CALLER token rather than the foreign one.
        Assert.NotSame(loggerFailure, thrown);
        Assert.Equal(callerCts.Token, thrown.CancellationToken);
        Assert.NotEqual(foreignCts.Token, thrown.CancellationToken);

        // (c) THE LOGGER'S MESSAGE DOES NOT LEAK into the propagated outcome.
        Assert.DoesNotContain(loggerMessage, thrown.Message, StringComparison.Ordinal);

        // The diagnostic really was attempted — otherwise the guard proves nothing.
        Assert.Contains(
            f.Logger.Entries,
            e => e.Message.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal));

        // (d) THE CLAIM REMAINS ACTIVE: post-claim cancellation rolls nothing back.
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Equal(f.Worker.Id, task.Metadata["assigned_worker"]);

        // (e) NOTHING WAS REQUEUED AND NOTHING WAS PUBLISHED on this path.
        Assert.Null(f.Queue.TryDequeueAny());
        Assert.Empty(f.Publisher.Calls);
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("Assigning task", StringComparison.Ordinal));
    }

    /// <summary>An exception whose <c>Message</c> getter throws — the placeholder-read vector.</summary>
    private sealed class ThrowingMessageException : Exception
    {
        public override string Message =>
            throw new InvalidOperationException("the message getter threw");
    }

    /// <summary>
    /// THE OUTER GUIDANCE WARNING'S OWN GUARD IS GENUINELY REACHABLE AND HOLDS: a guidance failure
    /// that happens BEFORE the send's inner <c>try</c> — the agents.md read itself faults — escapes
    /// <c>SendAgentsMdAsync</c> to the Ready path's outer catch, whose guidance warning is then
    /// guarded, so even a logger that throws ON that warning cannot escape or mask the outcome.
    /// Publication continues on the exact claimed instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE FAULT IS REAL, DETERMINISTIC AND AT THE PATH PRODUCTION ACTUALLY OPENS. The file is the
    /// fixture's OWN agents root (the root handed to <c>AgentsManager</c>) composed exactly as
    /// <c>AgentsManager.GetAgentsMdPath</c> composes it — never a freshly generated directory the
    /// service would never read.
    /// </para>
    /// <para>
    /// THE MECHANISM IS AN EXCLUSIVE FILE HANDLE, NOT A DIRECTORY SWAP. <c>GetAgentsMd</c> tests
    /// <c>File.Exists</c> BEFORE reading, and <c>File.Exists</c> is FALSE for a directory — so
    /// replacing the file with a directory makes the read silently return empty and never faults.
    /// Holding the real file open with <see cref="FileShare.None"/> keeps <c>File.Exists</c> TRUE
    /// while <c>File.ReadAllText</c> throws, and it works even when the test process runs as root
    /// (unlike permission bits, which root bypasses).
    /// </para>
    /// <para>
    /// THE READ FAULT IS ASSERTED, NOT ASSUMED: the outer warning entry can only exist if the read
    /// really threw, so the mechanism proves itself inside the vector.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ready_GuidanceReadFault_OuterGuidanceWarningGuardSurvivesAThrowingLogger()
    {
        var f = Fixture.Create(withAgentsManager: true);

        // THE OUTER warning is the emission under test — matched on its real production wording.
        var loggerFailure = new InvalidOperationException("the guidance warning's logger threw");
        f.Logger.ThrowFactory = message =>
            message.Contains("agents.md update failed after the assignment was claimed",
                StringComparison.Ordinal)
                ? loggerFailure
                : null;

        // THE EXACT FILE the production read opens, in the fixture's OWN root.
        var agentsFilePath = f.AgentsFilePath(WorkerRole.Coder);
        Assert.True(
            File.Exists(agentsFilePath),
            $"the fixture's agents file is missing, so this vector would fault nothing: {agentsFilePath}");

        var task = BuildTask("task-guidance-read-fault");
        f.Queue.Enqueue(task);

        // THE PRE-TRY FAULT, held for the whole Ready: File.Exists stays true, ReadAllText throws.
        using (new FileStream(agentsFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(File.Exists(agentsFilePath), "File.Exists must stay TRUE while the file is held");

            // MUST NOT THROW: the logger's throw is contained by the outer warning's guard.
            await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);
        }

        // (1) THE OUTER WARNING REALLY WAS ATTEMPTED. The logger records the entry BEFORE invoking
        // ThrowFactory, so this entry existing proves the read faulted, the outer catch ran and the
        // guarded emission was reached — the whole point of the vector.
        var outer = Assert.Single(
            f.Logger.Entries,
            e => e.Message.Contains("agents.md update failed after the assignment was claimed",
                StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, outer.Level);
        Assert.Null(outer.Exception);

        // (2) THE VECTOR CANNOT BE SATISFIED BY THE INNER SEND DIAGNOSTIC: the fault happened before
        // the send's inner try, so that line can never have been emitted.
        Assert.DoesNotContain(
            f.Logger.Entries,
            e => e.Message.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal));

        // (3) THE READY REALLY HAPPENED.
        Assert.Contains(
            f.Logger.Entries,
            e => e.Message.Contains($"Worker {WorkerId} is ready", StringComparison.Ordinal));

        // (4) THE PRIMARY OUTCOME STANDS: publication continued on the EXACT claimed instance and
        // the ACTUAL task, the claim is retained, and nothing was requeued.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Equal(WorkerId, task.Metadata["assigned_worker"]);
        Assert.Null(f.Queue.TryDequeueAny());
    }

    /// <summary>
    /// THE REFUSAL PATH'S HOOK CONTRACT IS UNCHANGED: a hook-thrown
    /// <see cref="OperationCanceledException"/> on the REFUSAL path still propagates UNCHANGED —
    /// the pre-claim path's containment is specific to the cancellation path, where the caller's
    /// cancellation is primary. A refusal has no caller cancellation of record, so its hook failure
    /// is the outcome there — with the same single insert and no publication.
    /// </summary>
    [Fact]
    public async Task Ready_RefusalPath_HookCancellationPropagatesUnchanged()
    {
        var f = Fixture.Create();
        var task = BuildTask("task-refusal-hook-oce");
        f.Queue.Enqueue(task);

        using var hookCts = new CancellationTokenSource();
        hookCts.Cancel();
        var hookFailure = new OperationCanceledException("the refusal hook's own cancellation", hookCts.Token);

        // INSTALLED AFTER THE SETUP ENQUEUE, so only the refusal's requeue can invoke it.
        f.Queue.OnEnqueue = _ => throw hookFailure;

        f.Service.OnBeforeReadyClaimForTest = () => f.Pool.MarkBusy(WorkerId, "task-winner");

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken));

        // THE HOOK'S OWN FAILURE IS THE OUTCOME HERE — the exact instance, its own token.
        Assert.Same(hookFailure, thrown);
        Assert.Equal(hookCts.Token, thrown.CancellationToken);

        // THE INSERT ALREADY HAPPENED — exactly once, the same instance, and no retry.
        Assert.Same(task, f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.TryDequeueAny());

        // NO PUBLICATION, NO NOTIFICATION, AND THE WINNER IS UNTOUCHED.
        Assert.Null(f.Queue.GetActiveTask(task.TaskId));
        Assert.Empty(f.Publisher.Calls);
        Assert.Equal(0, f.NotifyCount);
        Assert.True(f.Worker.IsBusy);
        Assert.Equal("task-winner", f.Worker.CurrentTaskId);
    }

    /// <summary>
    /// THE FAILURE DETAIL IS BOUNDED AS WELL AS SANITIZED: a guidance failure whose message exceeds
    /// the 512-character cap is TRUNCATED before it reaches the logger — the emitted line stays a
    /// single sanitized line, carries the cap's truncation marker, and never floods the log with the
    /// full untrusted payload.
    /// </summary>
    /// <remarks>
    /// THE PAYLOAD IS CONTROL-CHARACTER-FREE ON PURPOSE: this vector isolates the LENGTH cap from
    /// the sanitization property the other vectors already pin, so a fix that only shortened the
    /// message without sanitizing would still fail those, and vice versa.
    /// <para>
    /// RETARGETED onto the guarded send-failure diagnostic, which renders through the SAME
    /// <c>SanitizedFailureDetail</c> helper; it previously depended on that diagnostic's throw
    /// escaping into the outer guidance warning, which is the defect now fixed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ready_GuidanceDiagnostic_OversizedFailureDetailIsTruncated()
    {
        var f = Fixture.Create(withAgentsManager: true);
        var oversized = "x" + new string('A', 600);
        f.Logger.ThrowFactory = message =>
            message.StartsWith("Sent AGENTS.md", StringComparison.Ordinal)
                ? new InvalidOperationException(oversized)
                : null;

        var task = BuildTask("task-guidance-oversized");
        f.Queue.Enqueue(task);

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            f.Logger.Entries,
            e => e.Message.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal));

        // THE RAW PAYLOAD NEVER REACHED THE LOG UNSHORTENED — well past the cap, it is cut.
        Assert.DoesNotContain(oversized, entry.Message, StringComparison.Ordinal);
        Assert.True(
            entry.Message.Length < 700,
            $"the guidance diagnostic was not bounded: {entry.Message.Length} characters");

        // THE TRUNCATION MARKER IS PRESENT, and the rendered line is still single and sanitized.
        Assert.Contains("…(truncated)", entry.Message, StringComparison.Ordinal);
        AssertSingleSanitizedLine(entry.Message);
        Assert.Null(entry.Exception);

        // THE PRIMARY OUTCOME IS UNTOUCHED: the claim stands and publication continued.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.True(f.Worker.IsBusy);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
    }

    // ── the guarded post-claim Information emissions ──────────────────────────

    /// <summary>The EXACT production wording of each guarded post-claim emission.</summary>
    private const string AssignedRoleEmission =
        "Worker {WorkerId} assigned role {Role} for task {TaskId}";

    private const string AssigningEmission =
        "Assigning task {TaskId} to worker {WorkerId}";

    private const string PublishedEmission =
        "Assignment published to worker {WorkerId} for task {TaskId}";

    /// <summary>
    /// THE THREE POST-CLAIM <see cref="LogLevel.Information"/> EMISSIONS ARE GUARDED: a logger that
    /// throws on ANY ONE of them — with an ordinary fault or an OCE carrying the default or a
    /// foreign token — cannot interrupt the ACCEPTED assignment. Each row witnesses that the
    /// targeted emission WAS REACHED (the fixture logger records before invoking its throw factory),
    /// that the handler COMPLETES, that the publication and notification happened exactly once with
    /// the exact claimed instances, that the claim's retained state stands, that nothing was
    /// requeued, and that a logging failure was never reported as a refusal.
    /// </summary>
    /// <param name="emission">The EXACT production wording of the targeted emission.</param>
    /// <param name="faultKind">
    /// <see cref="GuardedEmissionFaultKind.InvalidOperation"/>, an
    /// <see cref="OperationCanceledException"/> with the default token, or one with a distinct LIVE
    /// foreign token.
    /// </param>
    [Theory]
    [InlineData(AssignedRoleEmission, GuardedEmissionFaultKind.InvalidOperation)]
    [InlineData(AssignedRoleEmission, GuardedEmissionFaultKind.CancelledWithDefaultToken)]
    [InlineData(AssignedRoleEmission, GuardedEmissionFaultKind.CancelledWithForeignToken)]
    [InlineData(AssigningEmission, GuardedEmissionFaultKind.InvalidOperation)]
    [InlineData(AssigningEmission, GuardedEmissionFaultKind.CancelledWithDefaultToken)]
    [InlineData(AssigningEmission, GuardedEmissionFaultKind.CancelledWithForeignToken)]
    [InlineData(PublishedEmission, GuardedEmissionFaultKind.InvalidOperation)]
    [InlineData(PublishedEmission, GuardedEmissionFaultKind.CancelledWithDefaultToken)]
    [InlineData(PublishedEmission, GuardedEmissionFaultKind.CancelledWithForeignToken)]
    public async Task Ready_GuardedPostClaimEmission_LoggerFaultOnAnyEmissionIsContainedAndTheAssignmentCompletes(
        string emission, GuardedEmissionFaultKind faultKind)
    {
        var f = Fixture.Create();
        var task = BuildTask($"task-guarded-{faultKind}");
        f.Queue.Enqueue(task);

        // THE RENDERED FORM of the targeted emission, composed from the same template and arguments
        // production passes. The throw factory receives RENDERED messages, so the factory must match
        // this exact string — the match is equality, never a prefix or a Contains.
        var rendered = emission switch
        {
            AssignedRoleEmission =>
                $"Worker {f.Worker.Id} assigned role {task.Role.ToRoleName()} for task {task.TaskId}",
            AssigningEmission => $"Assigning task {task.TaskId} to worker {f.Worker.Id}",
            PublishedEmission =>
                $"Assignment published to worker {f.Worker.Id} for task {task.TaskId}",
            _ => throw new InvalidOperationException($"unhandled emission template: {emission}"),
        };

        // THE THROW FACTORY fails EXACTLY the targeted emission. THE FIXTURE LOGGER RECORDS THE
        // ENTRY BEFORE INVOKING THIS FACTORY, so a recorded entry IS the positive witness that the
        // emission was reached.
        using var foreignCts = new CancellationTokenSource();
        foreignCts.Cancel();
        f.Logger.ThrowFactory = message =>
            message == rendered ? GuardedEmissionFault(faultKind, foreignCts.Token) : null;

        // MUST NOT THROW: the guard contains the logger's fault, whatever its kind.
        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // THE TARGETED EMISSION REALLY WAS REACHED — exactly once, at Information level, with the
        // exact rendered arguments.
        var entry = Assert.Single(f.Logger.Entries, e => e.Message == rendered);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(f.Worker.Id, entry.Message, StringComparison.Ordinal);
        Assert.Contains(task.TaskId, entry.Message, StringComparison.Ordinal);

        // THE WHOLE ASSIGNMENT COMPLETED: exactly ONE publisher invocation, the SAME worker instance
        // and the SAME task instance the claim pinned.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);

        // THE DASHBOARD NOTIFICATION FIRED EXACTLY ONCE for the accepted assignment.
        Assert.Equal(1, f.NotifyCount);

        // THE RETAINED STATE: the claim stands, the queue keeps the active entry, and nothing was
        // requeued — a logging fault rolled nothing back.
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Equal(WorkerId, task.Metadata["assigned_worker"]);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Null(f.Queue.TryDequeueAny());

        // A LOGGING FAILURE IS NOT A REFUSAL: neither refusal wording may appear.
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("claim refused", StringComparison.Ordinal));
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
    }

    /// <summary>The fault kinds the guarded-emission matrix drives through the throw factory.</summary>
    public enum GuardedEmissionFaultKind
    {
        InvalidOperation,
        CancelledWithDefaultToken,
        CancelledWithForeignToken,
    }

    /// <summary>
    /// The EXACT exception a matrix cell's throw factory raises: the ordinary fault, or the OCE
    /// shape — parameterless (the default token) or carrying a distinct LIVE foreign token.
    /// </summary>
    private static Exception GuardedEmissionFault(GuardedEmissionFaultKind kind, CancellationToken foreignToken) =>
        kind switch
        {
            GuardedEmissionFaultKind.InvalidOperation =>
                new InvalidOperationException("the logger refused to emit the guarded post-claim line"),
            GuardedEmissionFaultKind.CancelledWithDefaultToken =>
                new OperationCanceledException("logger cancellation with the default token"),
            GuardedEmissionFaultKind.CancelledWithForeignToken =>
                new OperationCanceledException(
                    "logger cancellation with a foreign token", foreignToken),
            _ => throw new InvalidOperationException($"unhandled fault kind: {kind}"),
        };

    /// <summary>
    /// THE HEALTHY PATH STILL EMITS EACH OF THE THREE EMISSIONS, EXACTLY ONCE EACH, when the logger
    /// does not throw: the guard changed the containment, not the emission itself.
    /// </summary>
    [Fact]
    public async Task Ready_HealthyPath_EmitsEachOfTheThreePostClaimEmissionsExactlyOnce()
    {
        var f = Fixture.Create();
        var task = BuildTask("task-healthy-emissions");
        f.Queue.Enqueue(task);

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // EACH of the three emissions appeared EXACTLY ONCE, at Information level.
        Assert.Equal(
            1,
            f.Logger.Entries.Count(e =>
                e.Level == LogLevel.Information && e.Message == $"Worker {WorkerId} assigned role coder for task {task.TaskId}"));
        Assert.Equal(
            1,
            f.Logger.Entries.Count(e =>
                e.Level == LogLevel.Information && e.Message == $"Assigning task {task.TaskId} to worker {WorkerId}"));
        Assert.Equal(
            1,
            f.Logger.Entries.Count(e =>
                e.Level == LogLevel.Information
                && e.Message == $"Assignment published to worker {WorkerId} for task {task.TaskId}"));

        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.Equal(1, f.NotifyCount);
    }

    /// <summary>
    /// THE GENUINE CALLER CANCELLATION STAYS PRIMARY THROUGH THE GUARDED EMISSIONS: cancelling a
    /// REAL token inside the window (never a logger fake) still escapes as an
    /// <see cref="OperationCanceledException"/> carrying the CALLER token, with NO publication and
    /// NO requeue after the claim.
    /// </summary>
    /// <remarks>
    /// THE GUIDANCE HELPER IS PRESENT so the cancelled delivery must pass through the guidance send,
    /// the caller-token observation and BOTH remaining guarded emissions — every one of which must
    /// stay contained behind the caller's cancellation, never replacing it. The accepted claim's own
    /// dashboard notification is the legitimate one — exactly ONE.
    /// </remarks>
    [Fact]
    public async Task Ready_GenuineCallerCancellationInsideTheWindow_EscapesWithTheCallerTokenAndNoPublication()
    {
        var f = Fixture.Create(withAgentsManager: true);
        var task = BuildTask("task-genuine-cancellation");
        f.Queue.Enqueue(task);

        using var cts = new CancellationTokenSource();
        // THE CANCELLATION LANDS INSIDE THE WINDOW — after the pre-claim check, before the claim.
        f.Service.OnBeforeReadyClaimForTest = () => cts.Cancel();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => InvokeReadyAsync(f.Service, f.Worker, cts.Token));

        // THE CALLER'S TOKEN IS THE OUTCOME OF RECORD.
        Assert.Equal(cts.Token, thrown.CancellationToken);

        // NO PUBLICATION — and, after the claim, NO requeue either.
        Assert.Empty(f.Publisher.Calls);
        Assert.Null(f.Queue.TryDequeueAny());

        // THE CLAIM STANDS: post-claim cancellation rolls nothing back, and the claim's own
        // dashboard notification is the exactly-ONE legitimate one.
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Equal(WorkerId, task.Metadata["assigned_worker"]);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Equal(1, f.NotifyCount);

        // NO REFUSAL OR BLOCKED WORDING: a cancellation is neither.
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("claim refused", StringComparison.Ordinal));
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
    }

    /// <summary>
    /// A PUBLISHER FAILURE KEEPS ITS EXACT EXISTING HANDLING, and the <c>"Assignment published …"</c>
    /// emission is NOT emitted for a publication that never completed.
    /// </summary>
    /// <param name="faultKind">
    /// A recording REFUSAL (the handled blocked disposition, returning normally), an ordinary
    /// publisher fault (propagates unchanged), or an OCE with a FOREIGN live token (propagates
    /// unchanged) — each exactly the semantics today's catch shape gives it.
    /// </param>
    [Theory]
    [InlineData(PublisherFaultKind.RecordingRefusal)]
    [InlineData(PublisherFaultKind.OrdinaryFault)]
    [InlineData(PublisherFaultKind.ForeignTokenCancellation)]
    public async Task Ready_PublisherFailure_IsHandledExactlyAsTodayAndEmitsNoPublishedLine(
        PublisherFaultKind faultKind)
    {
        var f = Fixture.Create();
        var task = BuildTask($"task-publisher-fails-{faultKind}");
        f.Queue.Enqueue(task);

        // A DIFFERENT, LIVE cancellation source — never the caller's token, which stays LIVE so the
        // handler reaches publication.
        using var foreignCts = new CancellationTokenSource();
        foreignCts.Cancel();
        f.Publisher.Failure = faultKind switch
        {
            PublisherFaultKind.RecordingRefusal =>
                WorkerAssignmentRecordingException.MissingPublisher(),
            PublisherFaultKind.OrdinaryFault =>
                new InvalidOperationException("the publisher refused the delivery"),
            PublisherFaultKind.ForeignTokenCancellation =>
                new OperationCanceledException(
                    "the publisher's own cancellation, with a foreign token", foreignCts.Token),
            _ => throw new InvalidOperationException($"unhandled publisher fault kind: {faultKind}"),
        };

        if (faultKind == PublisherFaultKind.RecordingRefusal)
        {
            // THE HANDLED DISPOSITION RETURNED NORMALLY — the recording refusal is caught.
            await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);
        }
        else
        {
            // ESCAPES EXACTLY AS TODAY: neither an ordinary fault nor a foreign-token OCE is a
            // recording refusal, so neither is caught — the original exception propagates.
            var thrown = await Assert.ThrowsAnyAsync<Exception>(
                () => InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken));

            var expected = f.Publisher.Failure!;
            Assert.Same(expected, thrown);
        }

        // THE PUBLISHER WAS INVOKED EXACTLY ONCE with the exact claimed instances — and its failure
        // means the success line was NEVER emitted.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.DoesNotContain(
            f.Logger.Messages,
            m => m.Contains("Assignment published", StringComparison.Ordinal));

        // THE DISPOSITION WORDING MATCHES THE PATH: a recording refusal is the handled blocked
        // shape; an escaping fault reports no refusal either.
        if (faultKind == PublisherFaultKind.RecordingRefusal)
        {
            Assert.Contains(
                f.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("claim refused", StringComparison.Ordinal));

        // NO REQUEUE on a failed publication; the claim — and its one legitimate pre-publication
        // dashboard notification — is retained (publication happens AFTER the notification).
        Assert.Null(f.Queue.TryDequeueAny());
        Assert.Equal(1, f.NotifyCount);
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Equal(WorkerId, task.Metadata["assigned_worker"]);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
    }

    /// <summary>The publisher-fault kinds the publication-failure controls drive.</summary>
    public enum PublisherFaultKind
    {
        /// <summary>A recording refusal — the handled blocked disposition.</summary>
        RecordingRefusal,

        /// <summary>An ordinary fault, which propagates unchanged.</summary>
        OrdinaryFault,

        /// <summary>An OCE with a foreign live token, which propagates unchanged.</summary>
        ForeignTokenCancellation,
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

    // ── round-2 additions: the Ready boundary's remaining vectors ─────────────

    /// <summary>
    /// THE ACCEPTED READY PUBLISHES THE WHOLE CLAIM AND NOTHING MORE: the claimed instance carries
    /// the task's role and model, is busy with the exact task id, and carries ONE shared timestamp
    /// for both clocks; the queue's active entry is the EXACT dequeued instance tagged with the
    /// worker id; the publisher received the EXACT claimed instance and the EXACT dequeued task; and
    /// the dashboard was notified EXACTLY ONCE for the accepted path.
    /// </summary>
    [Fact]
    public async Task Ready_ClaimSucceeds_PublishesExactInstanceStateQueueAndOneNotification()
    {
        var f = Fixture.Create();
        var task = BuildTask("task-accepted-ready");
        f.Queue.Enqueue(task);

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // THE WORKER'S PUBLISHED STATE, all from ONE claim.
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Equal(WorkerRole.Coder, f.Worker.Role);
        Assert.Equal(task.Model, f.Worker.CurrentModel);
        Assert.NotNull(f.Worker.CurrentTaskStartedAt);
        Assert.Equal(f.Worker.CurrentTaskStartedAt, f.Worker.LastActivityAt);

        // THE QUEUE'S ACTIVE ENTRY IS THE EXACT DEQUEUED INSTANCE, tagged with this worker.
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Equal(WorkerId, task.Metadata["assigned_worker"]);
        Assert.Null(f.Queue.TryDequeueAny());

        // THE PUBLISHER RECEIVED THE EXACT CLAIMED INSTANCE AND THE EXACT DEQUEUED TASK — exactly
        // once.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);

        // EXACTLY ONE DASHBOARD NOTIFICATION for the accepted path — and none extra.
        Assert.Equal(1, f.NotifyCount);

        // NO REFUSAL OR BLOCKED WORDING: this delivery was published.
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("claim refused", StringComparison.Ordinal));
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("assignment blocked", StringComparison.Ordinal));
    }

    /// <summary>
    /// ABA AT THE READY BOUNDARY: the pinned instance is REMOVED and a replacement re-registered under
    /// the same ID while the Ready is parked in the pre-claim window. The claim is refused for the
    /// stale pinned instance, the ACTUAL dequeued task goes back EXACTLY ONCE, the replacement is
    /// untouched, and nothing was published, notified or recorded.
    /// </summary>
    [Fact]
    public async Task Ready_PinnedInstanceReplacedInWindow_RequeuesTheActualTaskOnceAndLeavesTheReplacementUntouched()
    {
        var f = Fixture.Create();
        var task = BuildTask("task-aba-ready");
        f.Queue.Enqueue(task);

        // THE REPLACEMENT WINS INSIDE THE WINDOW: the pinned instance is removed and a replacement
        // re-registered under the same ID, distinguishable by its model.
        f.Service.OnBeforeReadyClaimForTest = () =>
        {
            Assert.True(f.Pool.RemoveWorker(f.Worker));
            var replacement = f.Pool.RegisterWorker(WorkerId, []);
            replacement.CurrentModel = "replacement-model";
        };

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // THE STALE PINNED INSTANCE WAS NEVER MUTATED — the claim refused it.
        Assert.False(f.Worker.IsBusy);
        Assert.Null(f.Worker.CurrentTaskId);
        Assert.Null(f.Worker.CurrentModel);
        Assert.Equal(WorkerRole.Unspecified, f.Worker.Role);

        // THE REPLACEMENT UNDER THE SAME ID IS UNTOUCHED by the stale Ready.
        var replacement = f.Pool.GetWorker(WorkerId)!;
        Assert.NotSame(f.Worker, replacement);
        Assert.False(replacement.IsBusy);
        Assert.Null(replacement.CurrentTaskId);
        Assert.Equal("replacement-model", replacement.CurrentModel);

        // THE ACTUAL DEQUEUED TASK IS BACK, EXACTLY ONCE, AS THE VERY SAME INSTANCE.
        Assert.Same(task, f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.TryDequeueAny());
        Assert.Null(f.Queue.GetActiveTask(task.TaskId));
        Assert.False(task.Metadata.ContainsKey("assigned_worker"));

        // ZERO PUBLICATION, ZERO NOTIFICATION.
        Assert.Empty(f.Publisher.Calls);
        Assert.Equal(0, f.NotifyCount);
    }

    /// <summary>
    /// A MISSING PUBLISHER (the fail-closed shape) BLOCKS THE RECORDING AND RETURNS NORMALLY: the
    /// claim stands — the worker stays busy with the task and the queue keeps its active entry — no
    /// assignment is published, the task is NOT requeued, and the blocked disposition is reported.
    /// </summary>
    [Fact]
    public async Task Ready_MissingPublisher_RetainsTheClaimAndReturnsNormally()
    {
        var f = Fixture.Create(withPublisher: false);
        var task = BuildTask("task-missing-publisher");
        f.Queue.Enqueue(task);

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // THE CLAIM STANDS — no rollback of the busy state or the active entry.
        Assert.True(f.Worker.IsBusy);
        Assert.Equal(task.TaskId, f.Worker.CurrentTaskId);
        Assert.Equal(WorkerRole.Coder, f.Worker.Role);
        Assert.Equal(task.Model, f.Worker.CurrentModel);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
        Assert.Equal(WorkerId, task.Metadata["assigned_worker"]);

        // THE TASK WAS NOT REQUEUED and nothing reached the publisher seam.
        Assert.Null(f.Queue.TryDequeueAny());
        Assert.Empty(f.Publisher.Calls);

        // THE HANDLED DISPOSITION RETURNED NORMALLY, reported as blocked — not refused.
        Assert.Contains(
            f.Logger.Messages,
            m => m.Contains("assignment blocked", StringComparison.Ordinal)
                 && m.Contains("MissingPublisher", StringComparison.Ordinal));
        Assert.DoesNotContain(
            f.Logger.Messages, m => m.Contains("claim refused", StringComparison.Ordinal));
    }

    /// <summary>
    /// SIMULTANEOUS READIES ON DEDICATED THREADS RACE FOR THE SAME WORKER AND THE SAME QUEUE: both
    /// Readies park at a REAL two-participant barrier INSIDE the production pre-claim hook, so both
    /// have dequeued before either claims. Exactly one claim wins — the other's dequeued task is
    /// requeued exactly once, the winner's published state is complete and untorn, and exactly one
    /// notification is fired. No sleeps, no polling: the barrier is the rendezvous.
    /// </summary>
    [Fact]
    public async Task Ready_SimultaneousOnDedicatedThreads_ProduceExactlyOneWinnerAndOneRequeue()
    {
        var f = Fixture.Create();
        var winnerTask = BuildTask("task-race-winner", "race-winner-model");
        var loserTask = BuildTask("task-race-loser", "race-loser-model");
        f.Queue.Enqueue(winnerTask);
        f.Queue.Enqueue(loserTask);

        // THE REAL WINDOW, RENDEZVOUSED: both Readies must reach the pre-claim hook before either
        // proceeds to its claim, so the two claims genuinely contend at the pool's activity lock.
        using var barrier = new Barrier(participantCount: 2);
        f.Service.OnBeforeReadyClaimForTest = barrier.SignalAndWait;

        Task firstReady = Task.CompletedTask, secondReady = Task.CompletedTask;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstThread = new Thread(() =>
        {
            started.Task.GetAwaiter().GetResult();
            firstReady = InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);
            firstReady.GetAwaiter().GetResult();
        })
        { IsBackground = true };
        var secondThread = new Thread(() =>
        {
            started.Task.GetAwaiter().GetResult();
            secondReady = InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);
            secondReady.GetAwaiter().GetResult();
        })
        { IsBackground = true };

        try
        {
            firstThread.Start();
            secondThread.Start();
            started.SetResult();

            // JOIN ORIGINAL OPERATIONS so a failing assertion cannot leak parked work.
            Assert.True(firstThread.Join(BoundedWait), "the first Ready thread did not finish");
            Assert.True(secondThread.Join(BoundedWait), "the second Ready thread did not finish");

            await firstReady;
            await secondReady;

            // EXACTLY ONE WINNER: one claim took the worker, the loser's task is back.
            Assert.True(f.Worker.IsBusy);
            var winnerId = f.Worker.CurrentTaskId!;
            Assert.True(
                winnerId is "task-race-winner" or "task-race-loser",
                $"the claimed task id {winnerId} must be one of the two racing tasks.");

            // THE WINNER'S STATE IS UNTORN: the winning model, role, and one shared timestamp.
            var winningTask = winnerId == "task-race-winner" ? winnerTask : loserTask;
            Assert.Equal(winningTask.Model, f.Worker.CurrentModel);
            Assert.Equal(WorkerRole.Coder, f.Worker.Role);
            Assert.NotNull(f.Worker.CurrentTaskStartedAt);
            Assert.Equal(f.Worker.CurrentTaskStartedAt, f.Worker.LastActivityAt);
            Assert.Same(winningTask, f.Queue.GetActiveTask(winnerId));
            Assert.Equal(WorkerId, winningTask.Metadata["assigned_worker"]);

            // THE LOSER'S TASK WAS REQUEUED EXACTLY ONCE — the very same instance, untouched.
            var losingTask = winnerId == "task-race-winner" ? loserTask : winnerTask;
            var requeued = f.Queue.TryDequeueAny();
            Assert.Same(losingTask, requeued);
            Assert.False(requeued!.Metadata.ContainsKey("assigned_worker"));
            Assert.Null(f.Queue.TryDequeueAny());

            // EXACTLY ONE NOTIFICATION for the one accepted claim, and exactly one publication.
            Assert.Equal(1, f.NotifyCount);
            var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
            Assert.Same(f.Worker, publishedWorker);
            Assert.Same(winningTask, publishedTask);
        }
        finally
        {
            // JOIN ORIGINAL OPERATIONS so a failing assertion cannot leak parked work.
            if (firstThread.IsAlive) firstThread.Join(BoundedWait);
            if (secondThread.IsAlive) secondThread.Join(BoundedWait);
            await firstReady.WaitAsync(TestContext.Current.CancellationToken);
            await secondReady.WaitAsync(TestContext.Current.CancellationToken);
        }
    }
}
