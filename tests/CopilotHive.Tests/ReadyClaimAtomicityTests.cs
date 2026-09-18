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

            var worker = pool.RegisterWorker(WorkerId, []);

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
    /// THE <c>LogGuidanceBestEffortFailed</c> DIAGNOSTIC IS SANITIZED AND CARRIES NO EXCEPTION
    /// OBJECT, for a failure that ESCAPES the send helper entirely.
    /// </summary>
    /// <remarks>
    /// BOTH OF THE SEND'S LOG LINES FAIL HERE, so the send's own catch cannot contain the fault and
    /// the post-claim guidance catch in the Ready path is what handles it — the exact path this
    /// vector is about.
    /// </remarks>
    [Fact]
    public async Task Ready_GuidanceFailure_IsLoggedSanitizedAndWithoutTheExceptionObject()
    {
        var f = Fixture.Create(withAgentsManager: true);
        f.Logger.ThrowFactory = message =>
            message.StartsWith("Sent AGENTS.md", StringComparison.Ordinal)
            || message.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal)
                ? new InvalidOperationException(ControlCharacterPayload)
                : null;

        var task = BuildTask("task-guidance-sanitized");
        f.Queue.Enqueue(task);

        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            f.Logger.Entries,
            e => e.Message.Contains(
                "agents.md update failed after the assignment was claimed", StringComparison.Ordinal));

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
    /// THE NO-THROW GUARD SURVIVES SANITIZATION: neither a logger that throws ON the guidance
    /// warning itself nor an exception whose <c>Message</c> getter throws can escape the guarded
    /// diagnostic or mask the primary outcome.
    /// </summary>
    [Fact]
    public async Task Ready_GuidanceDiagnostic_ThrowingLoggerAndMessageGetterCannotEscape()
    {
        var f = Fixture.Create(withAgentsManager: true);
        f.Logger.ThrowFactory = message =>
            // The send's own two lines fail with an exception whose MESSAGE GETTER throws, so the
            // guidance warning must render it through its no-throw read…
            message.StartsWith("Sent AGENTS.md", StringComparison.Ordinal)
            || message.StartsWith("Failed to send AGENTS.md", StringComparison.Ordinal)
                ? new ThrowingMessageException()
                // …and the guidance warning ITSELF then throws too.
                : message.Contains("agents.md update failed", StringComparison.Ordinal)
                    ? new InvalidOperationException("the guidance warning's logger threw")
                    : null;

        var task = BuildTask("task-guidance-guard");
        f.Queue.Enqueue(task);

        // MUST NOT THROW: neither failure escapes the guarded diagnostic.
        await InvokeReadyAsync(f.Service, f.Worker, TestContext.Current.CancellationToken);

        // The guarded warning really was attempted (so the guard is what contained the throw)…
        var entry = Assert.Single(
            f.Logger.Entries,
            e => e.Message.Contains("agents.md update failed", StringComparison.Ordinal));
        AssertSingleSanitizedLine(entry.Message);
        Assert.Null(entry.Exception);

        // …and the PRIMARY OUTCOME is untouched: the claim stands and publication continued.
        var (publishedWorker, publishedTask) = Assert.Single(f.Publisher.Calls);
        Assert.Same(f.Worker, publishedWorker);
        Assert.Same(task, publishedTask);
        Assert.True(f.Worker.IsBusy);
        Assert.Same(task, f.Queue.GetActiveTask(task.TaskId));
    }

    /// <summary>An exception whose <c>Message</c> getter throws — the placeholder-read vector.</summary>
    private sealed class ThrowingMessageException : Exception
    {
        public override string Message =>
            throw new InvalidOperationException("the message getter threw");
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
