using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// THE ATOMIC ADOPTION PRIMITIVES AND THE REGISTER DECISION of the non-destructive restart slice,
/// exercised against REAL collaborators: a <see cref="GoalPipeline"/> genuinely restored from a
/// persisted snapshot that carries a hydrated registry and a PENDING active slot, the real
/// <see cref="WorkerPool"/>/<see cref="TaskQueue"/>, and the real SQLite
/// <see cref="WorkerAssignmentContextStore"/> holding the assignment context a real dispatch would
/// have recorded.
/// <para>
/// COVERAGE, honestly:
/// <list type="bullet">
///   <item><description>(a) the ADOPTED COMPLETION, END TO END: the adopted worker's REAL
///     <c>WorkStream</c> <c>TaskComplete</c> reaches the REAL <see cref="TaskCompletionService"/>,
///     which admits it and advances the pipeline, with no bypass — plus the direct happy path and
///     the Register reply contract;</description></item>
///   <item><description>(b) the COMMIT ORDER: a worker visible through <c>GetWorker</c> implies the
///     hold had already left, observed at the adoption's own commit-window boundary;</description></item>
///   <item><description>(g) <see cref="GoalPipeline.TryAdoptRestoredActiveAttempt"/> and
///     <see cref="GoalPipeline.TryReleaseRestoredActiveAttemptHold"/> are mutually exclusive, and
///     <see cref="GoalPipeline.TryRevertRestoredActiveAttemptAdoption"/> only ever transitions FROM
///     Adopted;</description></item>
///   <item><description>(h) <see cref="TaskQueue.TryActivateNew"/> never overwrites an existing
///     entry, and <see cref="TaskQueue.TryRemoveOwned"/> removes only the IDENTICAL instance —
///     refusing an equal-valued but distinct one;</description></item>
///   <item><description>the adopted happy path itself, as the non-vacuous positive control for the
///     refusals, both directly (the adopter) and through the REAL
///     <see cref="HiveOrchestratorService.Register"/>;</description></item>
///   <item><description>(c) EVERY failed Register-level precondition → <c>adopted_task = false</c>,
///     an IDLE worker, an UNCHANGED hold and exactly ONE Warning naming the check;</description></item>
///   <item><description>(d) a pre-existing active queue entry → <c>adopted_task = false</c>, the
///     entry untouched, the hold back to Held;</description></item>
///   <item><description>(e) an already-registered worker id → today's duplicate reply and warning
///     and NO adoption warning, with the hold unchanged;</description></item>
///   <item><description>(f) a release that wins first → <c>adopted_task = false</c>, an idle worker
///     and no active entry;</description></item>
///   <item><description>(i) an EMPTY <c>current_task_id</c> → today's exact registration behavior
///     with no new log records;</description></item>
///   <item><description>(k) the DEFERRED SECOND-RESTART BOUNDARY: a restored pipeline whose pointer
///     names a task the restored registry does not contain fails CLOSED and stays held, so the
///     follow-up grace sweep — not this path — owns that attempt.</description></item>
/// </list>
/// </para>
/// <para>
/// REMOVAL-PROOFNESS. Each vector arranges the state a weaker implementation would accept: the
/// mutual-exclusion vectors would both succeed under an unconditional write; the queue vectors
/// would both misbehave under a value-based overwrite/removal; and (k) seeds a COMPLETE, matching
/// assignment context for the absent task, so its refusal cannot be explained away by a missing
/// context.
/// </para>
/// </summary>
public sealed class RestoredAttemptAdoptionTests : IDisposable
{
    private const string HeldModel = "copilot/claude-sonnet-4.6";

    private readonly string _connectionString =
        $"Data Source=file:memdb-adopt-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<SqliteConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];

    public RestoredAttemptAdoptionTests()
    {
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();
        CreateContext().Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        foreach (var connection in _connections)
            connection.Dispose();
        _keeper.Dispose();
    }

    // ═══════════════════════════════ fixture helpers ═══════════════════════════════

    private CopilotHiveDbContext CreateContext()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        _connections.Add(connection);

        var context = new CopilotHiveDbContext(
            new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection).Options);
        _contexts.Add(context);
        return context;
    }

    private PipelineStore CreateStore() =>
        new(CreateContext(), NullLogger<PipelineStore>.Instance);

    /// <summary>A factory-created (store-OWNED) context per operation — the production shape.</summary>
    private sealed class SharedCacheContextFactory(string connectionString) : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext()
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            return new CopilotHiveDbContext(
                new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection).Options);
        }
    }

    private PipelineStore CreateFactoryBackedStore() =>
        new(new SharedCacheContextFactory(_connectionString), NullLogger<PipelineStore>.Instance);

    private WorkerAssignmentContextStore CreateAssignmentStore() =>
        new(new SharedCacheContextFactory(_connectionString), NullLogger<WorkerAssignmentContextStore>.Instance);

    private static Goal NewGoal(string id) =>
        new() { Id = id, Description = "adoption goal " + id, RepositoryNames = ["test-repo"] };

    /// <summary>Installs the full plan and puts pipeline AND state machine on the phase.</summary>
    private static void Arrange(GoalPipeline pipeline, GoalPhase phase)
    {
        var plan = IterationPlan.Default(includeImprove: true);
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, phase);
        pipeline.AdvanceTo(phase);
    }

    /// <summary>
    /// Persists a GENUINELY ADMITTED Pending attempt through the live manager APIs — the manager
    /// creates the pipeline, the production capture allocates the slot, the atomic claim takes the
    /// pointer and <see cref="GoalPipelineManager.PersistAdmission"/> commits the durable mapping,
    /// the pointer AND (for the eligible provenance) the registry blob in one transaction — and then
    /// optionally records the assignment context a real delivery would have left behind.
    /// </summary>
    /// <param name="goalId">The goal to seed.</param>
    /// <param name="workerId">The worker the recorded assignment context belongs to.</param>
    /// <param name="recordAssignmentContext">
    /// Whether to record the assignment context. <c>false</c> leaves the task with NO recorded
    /// context at all — the NoAssignmentContext fixture.
    /// </param>
    /// <returns>The goal id, the claimed task id and its attempt number.</returns>
    private (string GoalId, string TaskId, int Attempt) SeedHeldAttempt(
        string goalId, string workerId, bool recordAssignmentContext = true)
    {
        var manager = new GoalPipelineManager(CreateStore(), NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        Arrange(pipeline, GoalPhase.Coding);

        var slot = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.True(pipeline.TrySetActiveTask(slot.TaskId, "copilothive/" + goalId));

        var admission = manager.PersistAdmission(pipeline, slot.TaskId);
        Assert.Equal(AdmissionCommitStatus.Committed, admission.Status);

        if (recordAssignmentContext)
        {
            var recorded = CreateAssignmentStore().InsertOnce(new WorkerAssignmentContext(
                goalId,
                workerId,
                WorkerRole.Coder,
                new WorkSlot(slot.TaskId, slot.Position, slot.Attempt),
                HeldModel));
            Assert.Equal(WorkerAssignmentWriteStatus.Recorded, recorded.Status);
        }

        return (goalId, slot.TaskId, slot.Attempt);
    }

    /// <summary>
    /// Reopens the attempt through a FRESH manager over the same database — the orchestrator-restart
    /// shape — and asserts the fixture really is an ADOPTABLE held attempt: hydrated registry
    /// evidence, a PENDING active slot, and pipeline/machine phases both on the slot's worker phase.
    /// </summary>
    private (GoalPipelineManager Manager, GoalPipeline Pipeline) RestoreHeldAttempt(string goalId, string taskId)
    {
        var manager = new GoalPipelineManager(
            CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.RestorePipeline(goalId);

        Assert.NotNull(pipeline);
        Assert.True(pipeline!.IsRestoredActiveAttemptHold, "the fixture must restore HELD");
        Assert.Equal(RestoredRegistryOutcome.Restored, pipeline.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.ActiveSlotPending, pipeline.RestoredActivePointerClassification);
        Assert.Equal(taskId, pipeline.ActiveTaskId);
        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
        Assert.Equal(GoalPhase.Coding, pipeline.StateMachine.Phase);
        Assert.Same(pipeline, manager.GetByTaskId(taskId));

        return (manager, pipeline);
    }

    private RestoredAttemptAdopter CreateAdopter(GoalPipelineManager manager, WorkerPool pool, TaskQueue queue) =>
        new(manager, pool, queue, CreateAssignmentStore(), NullLogger<RestoredAttemptAdopter>.Instance);

    private void RawUpdate(string goalId, string column, string? value)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = $"UPDATE pipelines SET {column} = $value WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("$goal", goalId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    /// <summary>
    /// Repoints a RECORDED assignment context at another goal — the GoalMismatch fixture. It writes
    /// the stored column directly, because the insert-once contract deliberately refuses to rebind a
    /// row, so the only way to construct a stored context whose goal disagrees with the pipeline is
    /// to edit the row the way real data corruption or a foreign writer would.
    /// </summary>
    /// <param name="taskId">The task id whose recorded goal is rewritten.</param>
    /// <param name="goalId">The goal id to store.</param>
    private void RawUpdateContextGoal(string taskId, string goalId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText =
            "UPDATE worker_assignment_contexts SET goal_id = $value WHERE task_id = $task";
        command.Parameters.AddWithValue("$value", goalId);
        command.Parameters.AddWithValue("$task", taskId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static WorkTask BuildTask(string taskId) => new()
    {
        TaskId = taskId,
        GoalId = "queue-goal",
        GoalDescription = "queue goal",
        Prompt = "prompt",
        Role = WorkerRole.Coder,
        Model = HeldModel,
        Repositories = [],
    };

    // ── THE REGISTER-LEVEL HARNESS. The REAL HiveOrchestratorService.Register over the REAL
    //    adopter, pool, queue and assignment store — no fake stands in for any of them, so the
    //    decision under test is the production one. ─────────────────────────────────────────────

    /// <summary>
    /// The real <see cref="HiveOrchestratorService"/> and the collaborators its Register decision
    /// actually touches, with BOTH log sinks exposed — the service's and the adopter's — so the
    /// exactly-one-warning assertions count every warning the production path can emit.
    /// </summary>
    internal sealed record RegisterHarness(
        HiveOrchestratorService Service,
        WorkerPool Pool,
        TaskQueue Queue,
        ILogEntrySink Logger,
        RestoredAttemptAdopter? Adopter,
        ILogEntrySink? AdopterLogger = null)
    {
        /// <summary>
        /// EVERY record the production path emitted, across BOTH sinks (the service's and, when an
        /// adopter is wired, the adopter's) — so a second warning hiding in either sink is counted.
        /// </summary>
        public IReadOnlyList<(LogLevel LogLevel, string Message, Exception? Exception)> AllEntries =>
            [.. Logger.LogEntries, .. AdopterLogger?.LogEntries ?? []];
    }

    /// <summary>
    /// THE ADOPTION SUITE'S OWN CAPTURING LOGGER: the <see cref="ILogEntrySink"/> shape defined in
    /// THIS file, so no other suite's logger is widened to serve it. Every record is retained in
    /// emission order.
    /// </summary>
    internal sealed class AdoptionCapturingLogger<TCategory> : ILogger<TCategory>, ILogEntrySink
    {
        /// <summary>Every record, in emission order.</summary>
        public List<(LogLevel LogLevel, string Message, Exception? Exception)> LogEntries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (LogEntries)
                LogEntries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    /// <summary>
    /// Builds the production Register path: the REAL adopter over the given manager, pool and queue,
    /// wired into a REAL <see cref="HiveOrchestratorService"/> as its optional trailing parameter.
    /// BOTH the service and the adopter get a CAPTURING logger, so a warning emitted by either is
    /// visible to the one-warning assertions.
    /// </summary>
    /// <param name="manager">The pipeline manager the claim is resolved through.</param>
    /// <param name="withAdopter">
    /// Whether an adopter is supplied at all. <c>false</c> deliberately leaves the parameter absent,
    /// which is the NoAdopter shape.
    /// </param>
    /// <param name="queue">
    /// An existing queue to share — the pre-existing-entry vectors arrange it BEFORE the harness is
    /// built — or <c>null</c> for a fresh one.
    /// </param>
    /// <returns>The harness, with fresh pool, queue and loggers.</returns>
    private RegisterHarness CreateRegisterHarness(
        GoalPipelineManager manager, bool withAdopter, TaskQueue? queue = null)
    {
        // ONE pool and ONE queue, shared by the harness and the adopter: the adopter's registration
        // must land in the very pool and queue the Register reply is then asserted against.
        var pool = new WorkerPool();
        queue ??= new TaskQueue();
        var logger = new AdoptionCapturingLogger<HiveOrchestratorService>();
        var adopterLogger = new AdoptionCapturingLogger<RestoredAttemptAdopter>();

        RestoredAttemptAdopter? concreteAdopter = withAdopter
            ? new RestoredAttemptAdopter(
                manager, pool, queue, CreateAssignmentStore(), adopterLogger)
            : null;

        var dispatcher = new GoalDispatcher(
            new GoalManager(), manager, queue,
            new GrpcWorkerGateway(pool), new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        var service = new HiveOrchestratorService(
            pool, queue, manager, new TaskCompletionNotifier(), dispatcher, logger,
            restoredAttemptAdopter: concreteAdopter);

        return new RegisterHarness(service, pool, queue, logger, concreteAdopter, adopterLogger);
    }

    /// <summary>
    /// Reopens the seeded row through a FRESH manager — the orchestrator-restart shape — for the
    /// vectors that deliberately mutate the evidence before the claim.
    /// </summary>
    /// <param name="goalId">The goal whose row is restored.</param>
    /// <param name="expectHeld">
    /// Whether the restoration is expected to be HELD. <c>false</c> is for the NotHeld vector, where
    /// a null pointer is precisely the mutated evidence.
    /// </param>
    /// <returns>The fresh manager and the restored pipeline.</returns>
    private (GoalPipelineManager Manager, GoalPipeline Pipeline) RestoreForRegister(
        string goalId, bool expectHeld = true)
    {
        var manager = new GoalPipelineManager(
            CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.RestorePipeline(goalId);

        Assert.NotNull(pipeline);
        Assert.Equal(expectHeld, pipeline!.IsRestoredActiveAttemptHold);

        return (manager, pipeline);
    }

    /// <summary>A trivial <see cref="ServerCallContext"/>, matching the suite's existing fixtures.</summary>
    private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;

    /// <summary>
    /// THE END-TO-END COMPLETION HARNESS for the adopted-completion vector. The production shape:
    /// ONE shared <see cref="TaskCompletionNotifier"/> (the transport publishes to it and the REAL
    /// dispatcher — which owns the REAL <see cref="TaskCompletionService"/> — is subscribed to it,
    /// exactly as Program.cs wires them), a REAL completion recorder over the shared SQLite
    /// assignment store and a REAL receipt store, a signalling logger on BOTH the service and the
    /// dispatcher, and a <see cref="SignallingStreamWriter"/> the real <c>WorkStream</c> pump
    /// forwards to.
    /// </summary>
    /// <param name="manager">The restored pipeline's manager, shared by the service and the dispatcher.</param>
    /// <param name="goal">The restored pipeline's goal, registered on the dispatcher's goal source.</param>
    /// <returns>
    /// The register-level transport harness, the dispatcher's signalling logger, the real WorkStream
    /// reader, and a join that terminates the stream and asserts the producer drained.
    /// </returns>
    private async Task<AdoptedCompletionHarness> CreateCompletionHarnessAsync(
        GoalPipelineManager manager, Goal goal)
    {
        var pool = new WorkerPool();
        var queue = new TaskQueue();
        var serviceLogger = new SignallingLogger<HiveOrchestratorService>();
        var dispatcherLogger = new SignallingLogger<GoalDispatcher>();

        // THE SHARED NOTIFIER: the transport publishes a completion to it and the dispatcher's real
        // TaskCompletionService is its awaiting subscriber — the production singleton bridge.
        var completionNotifier = new TaskCompletionNotifier();

        // THE GOAL SOURCE the lifecycle service writes status updates to — the in-memory source the
        // transport suites use, registered on the dispatcher's own GoalManager so a terminal
        // finalization can really persist the status.
        var goalManager = new GoalManager();
        goalManager.AddSource(new InMemoryGoalSource(goal));
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var dispatcher = new GoalDispatcher(
            goalManager, manager, queue,
            new GrpcWorkerGateway(pool), completionNotifier,
            dispatcherLogger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        // THE REAL DURABLE EVIDENCE STORES, over the shared SQLite database: the assignment store
        // the adopter reads and the receipt store the completion recorder writes.
        var assignmentStore = CreateAssignmentStore();
        var receiptStore = new CompletionReceiptStore(
            new SharedCacheContextFactory(_connectionString),
            NullLogger<CompletionReceiptStore>.Instance);

        var service = new HiveOrchestratorService(
            pool, queue, manager, completionNotifier, dispatcher, serviceLogger,
            restoredAttemptAdopter: new RestoredAttemptAdopter(
                manager, pool, queue, assignmentStore, NullLogger<RestoredAttemptAdopter>.Instance),
            assignmentPublisher: new WorkerAssignmentPublisher(manager, pool, assignmentStore),
            completionRecorder: new WorkerCompletionRecorder(assignmentStore, receiptStore));

        var reader = new CompletionStreamReader();
        var writer = new SignallingStreamWriter();

        // THE REAL WORKSTREAM PRODUCER, retained and joined by the harness's own teardown: a vector
        // never leaks it, and a fault it terminated with is surfaced rather than swallowed.
        var streamTask = service.WorkStream(reader, writer, MockContext());

        return new AdoptedCompletionHarness(
            new RegisterHarness(service, pool, queue, serviceLogger, null),
            dispatcherLogger, manager, reader, writer, streamTask);
    }

    /// <summary>
    /// THE MINIMAL GOAL SOURCE the adopted-completion harness registers on the REAL GoalManager: the
    /// seeded goal is its only pending goal, so the lifecycle service's own status writes reach it.
    /// </summary>
    private sealed class InMemoryGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "in-memory-goal-source";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null,
            CancellationToken ct = default)
        {
            if (string.Equals(goalId, goal.Id, StringComparison.Ordinal))
                goal.Status = status;

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// THE LOG-SINK SHAPE every harness logger shares: the retained records in emission order. The
    /// register-level assertions read this shape, so both this file's
    /// <see cref="AdoptionCapturingLogger{TCategory}"/> and the adopted-completion harness's
    /// signalling logger satisfy it and the harness record can hold EITHER.
    /// </summary>
    internal interface ILogEntrySink
    {
        /// <summary>Every record, in emission order.</summary>
        List<(LogLevel LogLevel, string Message, Exception? Exception)> LogEntries { get; }
    }

    /// <summary>
    /// THE SERVICE-SIDE logger for the adopted-completion harness: the full
    /// <see cref="ILogEntrySink.LogEntries"/> shape PLUS
    /// <see cref="WaitFor"/> — a FRESH <see cref="TaskCompletionSource"/> completed by the NEXT
    /// record containing the fragment, the deterministic handle on a production line, allocated
    /// before the message is produced so it can never be missed.
    /// </summary>
    internal sealed class SignallingLogger<TCategory> : ILogger<TCategory>, ILogEntrySink
    {
        private readonly List<(LogLevel LogLevel, string Message, Exception? Exception)> _entries = [];
        private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];

        /// <summary>Every record, in emission order.</summary>
        public List<(LogLevel LogLevel, string Message, Exception? Exception)> LogEntries => _entries;

        /// <summary>Every formatted message, in emission order.</summary>
        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_entries)
                    return [.. _entries.Select(e => e.Message)];
            }
        }

        /// <summary>A FRESH signal completed by the NEXT record containing <paramref name="fragment"/>.</summary>
        public Task WaitFor(string fragment)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_entries)
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
            lock (_entries)
            {
                _entries.Add((logLevel, message, exception));
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
        }
    }

    /// <summary>
    /// The in-memory request stream the real <c>WorkStream</c> read loop drains, backed by an
    /// unbounded channel — the same fixture shape the transport-ownership suite uses. The FIRST
    /// message pins the stream to the worker id it carries, so a vector pushes its own messages.
    /// </summary>
    internal sealed class CompletionStreamReader : IAsyncStreamReader<CopilotHive.Shared.Grpc.WorkerMessage>
    {
        private readonly System.Threading.Channels.Channel<CopilotHive.Shared.Grpc.WorkerMessage> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<CopilotHive.Shared.Grpc.WorkerMessage>();

        public CopilotHive.Shared.Grpc.WorkerMessage Current { get; private set; } = new();

        public void Push(CopilotHive.Shared.Grpc.WorkerMessage message) =>
            _channel.Writer.TryWrite(message);

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
    /// forwards every queued <see cref="CopilotHive.Shared.Grpc.OrchestratorMessage"/> to. It records
    /// what it is given and signals per predicate; it never intercepts, delays or drops anything.
    /// </summary>
    internal sealed class SignallingStreamWriter : IServerStreamWriter<CopilotHive.Shared.Grpc.OrchestratorMessage>
    {
        private readonly List<CopilotHive.Shared.Grpc.OrchestratorMessage> _messages = [];

        public WriteOptions? WriteOptions { get; set; }

        public IReadOnlyList<CopilotHive.Shared.Grpc.OrchestratorMessage> Messages
        {
            get { lock (_messages) return [.. _messages]; }
        }

        public Task WriteAsync(CopilotHive.Shared.Grpc.OrchestratorMessage message)
        {
            lock (_messages)
                _messages.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// DRIVES THE REAL <see cref="HiveOrchestratorService.Register"/> with a claim on
    /// <paramref name="taskId"/> and returns the reply.
    /// </summary>
    /// <param name="harness">The harness whose service is called.</param>
    /// <param name="workerId">The caller-supplied worker id.</param>
    /// <param name="taskId">The claimed task id; empty means "no claim".</param>
    /// <returns>The registration reply.</returns>
    private static async Task<CopilotHive.Shared.Grpc.RegisterResponse> RegisterAsync(
        RegisterHarness harness, string workerId, string taskId) =>
        await harness.Service.Register(
            new CopilotHive.Shared.Grpc.RegisterRequest
            {
                WorkerId = workerId,
                CurrentTaskId = taskId,
            },
            MockContext());

    /// <summary>
    /// THE ONE-WARNING CONTRACT FOR A DECLINED CLAIM: exactly one Warning record exists in the whole
    /// run ACROSS BOTH SINKS — the service's AND the adopter's — it names the expected check, and it
    /// carries BOTH identities. Counting EVERY warning in EVERY sink is what kills a mutant that emits
    /// an extra record, including one hidden in the adopter's own logger.
    /// </summary>
    /// <param name="harness">The harness whose records are inspected.</param>
    /// <param name="expectedCheck">The check name the single warning must carry.</param>
    /// <param name="workerId">The worker id the warning must name.</param>
    /// <param name="taskId">The task id the warning must name.</param>
    /// <returns>The single warning's message, for vector-specific follow-up assertions.</returns>
    private static string AssertSingleAdoptionRefusalWarning(
        RegisterHarness harness, string expectedCheck, string workerId, string taskId)
    {
        var warnings = harness.AllEntries.Where(e => e.LogLevel >= LogLevel.Warning).ToList();
        Assert.True(
            warnings.Count == 1,
            $"exactly ONE warning must be emitted across BOTH sinks; actual {warnings.Count}: " +
            string.Join(" | ", warnings.Select(w => w.Message)));

        var warning = warnings[0];
        Assert.True(
            warning.Message.Contains("was not adopted", StringComparison.Ordinal),
            $"the single warning must be the adoption refusal; actual: {warning.Message}");
        Assert.True(
            warning.Message.Contains($"check={expectedCheck})", StringComparison.Ordinal),
            $"the warning must name the check '{expectedCheck}'; actual: {warning.Message}");
        Assert.Contains(workerId, warning.Message, StringComparison.Ordinal);
        Assert.Contains(taskId, warning.Message, StringComparison.Ordinal);
        return warning.Message;
    }

    /// <summary>
    /// THE DECLINED-CLAIM OUTCOME, asserted in ONE place so every refusal vector proves the SAME
    /// four facts: the claim was not adopted, the worker is registered but IDLE and carrying no
    /// task, the hold is exactly as it was, and the queue holds no active entry.
    /// </summary>
    /// <param name="harness">The harness whose pool and queue are inspected.</param>
    /// <param name="pipeline">The held pipeline the claim named.</param>
    /// <param name="workerId">The registering worker id.</param>
    /// <param name="taskId">The claimed task id.</param>
    /// <param name="expectHeld">Whether the hold must still be in force (false for a pre-released one).</param>
    private static void AssertDeclinedClaim(
        RegisterHarness harness, GoalPipeline pipeline, string workerId, string taskId, bool expectHeld = true)
    {
        var worker = Assert.IsType<ConnectedWorker>(harness.Pool.GetWorker(workerId));
        Assert.False(worker.IsBusy, "a declined claim must leave the worker idle");
        Assert.Null(worker.CurrentTaskId);
        Assert.Null(worker.CurrentModel);
        Assert.Equal(WorkerRole.Unspecified, worker.Role);
        Assert.Equal(expectHeld, pipeline.IsRestoredActiveAttemptHold);
        Assert.Null(harness.Queue.GetActiveTask(taskId));
    }

    // ═════════════ (g) hold-state transitions: mutual exclusion and revert scope ═════════════

    /// <summary>
    /// (g) THE ADOPT-THEN-RELEASE ORDER. A successful adoption ends the hold; a release after it is
    /// refused and changes nothing; a second adoption is refused; and the ROLLBACK returns the
    /// instance to Held — after which the release finally wins and NOTHING can re-adopt.
    /// </summary>
    [Fact]
    public void HoldState_AdoptFirst_ThenReleaseIsRefused_AndRevertReturnsToHeld()
    {
        var goalId = "adopt-h-state-a";
        var (_, taskId, _) = SeedHeldAttempt(goalId, "worker-a");
        var (_, pipeline) = RestoreHeldAttempt(goalId, taskId);

        // ON HELD: the rollback is a no-op — there is nothing adopted to revert.
        Assert.False(pipeline.TryRevertRestoredActiveAttemptAdoption());
        Assert.True(pipeline.IsRestoredActiveAttemptHold, "a revert on Held must leave the hold in force");

        // THE ADOPTION WINS.
        Assert.True(pipeline.TryAdoptRestoredActiveAttempt());
        Assert.False(pipeline.IsRestoredActiveAttemptHold, "an adopted attempt is no longer held");

        // RELEASE IS REFUSED — the two can never both succeed for one instance.
        Assert.False(pipeline.TryReleaseRestoredActiveAttemptHold());
        Assert.False(pipeline.IsRestoredActiveAttemptHold, "a refused release must not change the state");

        // A SECOND ADOPTION IS REFUSED TOO: the state machine has no re-entry into Adopted.
        Assert.False(pipeline.TryAdoptRestoredActiveAttempt());

        // THE ROLLBACK WORKS ONLY FROM ADOPTED, and puts the attempt back under hold.
        Assert.True(pipeline.TryRevertRestoredActiveAttemptAdoption());
        Assert.True(pipeline.IsRestoredActiveAttemptHold, "the rollback must put the attempt back under hold");
        Assert.False(pipeline.TryRevertRestoredActiveAttemptAdoption(), "there is nothing left to revert");

        // AND ONLY THEN CAN THE RELEASE WIN — after which adoption is refused for good.
        Assert.True(pipeline.TryReleaseRestoredActiveAttemptHold());
        Assert.False(pipeline.IsRestoredActiveAttemptHold);
        Assert.False(pipeline.TryAdoptRestoredActiveAttempt(), "a released hold can never be adopted");
        Assert.False(pipeline.TryRevertRestoredActiveAttemptAdoption(), "a released hold can never be reverted");
    }

    /// <summary>
    /// (g) THE RELEASE-THEN-ADOPT ORDER, plus the never-held shapes: a released hold refuses every
    /// later adoption, and an instance that never had a hold (a fresh Goal-created pipeline) refuses
    /// all three transitions.
    /// </summary>
    [Fact]
    public void HoldState_ReleaseFirst_ThenAdoptIsRefused_AndNeverHeldInstancesRefuseEverything()
    {
        var goalId = "adopt-h-state-b";
        var (_, taskId, _) = SeedHeldAttempt(goalId, "worker-b");
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        Assert.True(pipeline.TryReleaseRestoredActiveAttemptHold());
        Assert.False(pipeline.IsRestoredActiveAttemptHold);
        Assert.False(pipeline.TryAdoptRestoredActiveAttempt(), "the release already consumed the hold");
        Assert.False(pipeline.TryReleaseRestoredActiveAttemptHold(), "the release is not repeatable");
        Assert.False(pipeline.TryRevertRestoredActiveAttemptAdoption());

        // THE NEVER-HELD CONTROL: a fresh pipeline created by the manager (no persisted row existed)
        // is not held and therefore offers no transition at all.
        var freshManager = new GoalPipelineManager(CreateStore(), NullLogger<GoalPipelineManager>.Instance);
        var fresh = freshManager.CreatePipeline(NewGoal(goalId + "-fresh"));
        Assert.False(fresh.IsRestoredActiveAttemptHold);
        Assert.False(fresh.TryAdoptRestoredActiveAttempt());
        Assert.False(fresh.TryReleaseRestoredActiveAttemptHold());
        Assert.False(fresh.TryRevertRestoredActiveAttemptAdoption());

        // The held fixture's manager is untouched by the control above.
        Assert.Same(pipeline, manager.GetByGoalId(goalId));
    }

    // ═════════════ (h) queue primitives: no overwrite, reference-identity removal ═════════════

    /// <summary>
    /// (h) <see cref="TaskQueue.TryActivateNew"/> NEVER OVERWRITES: a second call for a task id that
    /// already has an entry — with an equal-valued copy just as much as with a differing task —
    /// returns <c>false</c> and leaves the ORIGINAL instance (and the winner's recorded worker)
    /// exactly as it is.
    /// </summary>
    [Fact]
    public void TryActivateNew_NeverOverwritesAnExistingEntry()
    {
        var queue = new TaskQueue();
        var original = BuildTask("task-activate-new");

        Assert.True(queue.TryActivateNew(original, "worker-first"));
        Assert.Same(original, queue.GetActiveTask("task-activate-new"));
        Assert.Equal("worker-first", original.Metadata["assigned_worker"]);

        // AN EQUAL-VALUED COPY: distinct instance, identical members — the copy an overwriting
        // implementation would happily install.
        var equalCopy = original with { };
        Assert.NotSame(original, equalCopy);
        Assert.Equal(original, equalCopy);

        Assert.False(queue.TryActivateNew(equalCopy, "worker-second"));
        Assert.Same(original, queue.GetActiveTask("task-activate-new"));
        Assert.NotEqual("worker-second", original.Metadata["assigned_worker"]);

        // A DIFFERING TASK UNDER THE SAME ID IS REFUSED TOO — no partial write of any field.
        var differing = BuildTask("task-activate-new") with { GoalId = "another-goal" };
        Assert.False(queue.TryActivateNew(differing, "worker-third"));
        Assert.Same(original, queue.GetActiveTask("task-activate-new"));

        // THE POSITIVE CONTROL: a FREE id is added, and its own worker is recorded on the TASK.
        var fresh = BuildTask("task-activate-new-2");
        Assert.True(queue.TryActivateNew(fresh, "worker-fresh"));
        Assert.Same(fresh, queue.GetActiveTask("task-activate-new-2"));
        Assert.Equal("worker-fresh", fresh.Metadata["assigned_worker"]);
    }

    /// <summary>
    /// (h) <see cref="TaskQueue.TryRemoveOwned"/> removes ONLY the identical reference: an
    /// equal-valued but DISTINCT instance — the exact shape
    /// <c>TryRemove(KeyValuePair&lt;string, WorkTask&gt;)</c> would match through
    /// <see cref="WorkTask"/>'s record equality — is refused, and the identical instance is removed
    /// exactly once.
    /// </summary>
    [Fact]
    public void TryRemoveOwned_RefusesAnEqualValuedDistinctInstance_AndRemovesTheIdenticalReference()
    {
        var queue = new TaskQueue();
        var task = BuildTask("task-remove-owned");
        Assert.True(queue.TryActivateNew(task, "worker-owner"));

        // THE VALUE-EQUALITY TRAP, asserted BEFORE the refusal: a value-based removal would take it.
        var equalCopy = task with { };
        Assert.NotSame(task, equalCopy);
        Assert.Equal(task, equalCopy);

        Assert.False(queue.TryRemoveOwned("task-remove-owned", equalCopy));
        Assert.Same(task, queue.GetActiveTask("task-remove-owned"));
        Assert.Equal("worker-owner", task.Metadata["assigned_worker"]);

        // The IDENTICAL instance is removed, once.
        Assert.True(queue.TryRemoveOwned("task-remove-owned", task));
        Assert.Null(queue.GetActiveTask("task-remove-owned"));
        Assert.False(queue.TryRemoveOwned("task-remove-owned", task));

        // THE POSITIVE CONTROL: with the entry really gone, a fresh activation succeeds.
        Assert.True(queue.TryActivateNew(equalCopy, "worker-next"));
        Assert.Same(equalCopy, queue.GetActiveTask("task-remove-owned"));
    }

    // ═════════════ the adopted happy path: the positive control for every refusal ═════════════

    /// <summary>
    /// THE ADOPTED HAPPY PATH, through the real adopter over a real held restoration: the attempt is
    /// adopted, the worker is registered BUSY with the RECORDED role and model, the active queue
    /// entry is assigned to it, and the hold is no longer reported. This is the non-vacuous control
    /// for every refusal vector below — the same fixture, refused only by the mutated evidence.
    /// </summary>
    [Fact]
    public void TryAdoptRestoredAttempt_MatchingEvidence_AdoptsAndRegistersTheWorkerBusy()
    {
        var goalId = "adopt-happy";
        var workerId = "worker-happy";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        var pool = new WorkerPool();
        var queue = new TaskQueue();
        var result = CreateAdopter(manager, pool, queue)
            .TryAdoptRestoredAttempt(workerId, taskId, ["dotnet"], requestCompletionReceiptAck: false,
                completionReceiptAckEnabled: false);

        Assert.True(result.Adopted);
        Assert.Equal(RestoredAttemptAdoptionOutcome.Adopted, result.Outcome);

        // THE WORKER IS THE EXACT REGISTERED INSTANCE, FULLY BUSY WITH THE RECORDED ROLE AND MODEL.
        var worker = Assert.IsType<ConnectedWorker>(result.Worker);
        Assert.Same(worker, pool.GetWorker(workerId));
        Assert.True(worker.IsBusy);
        Assert.Equal(taskId, worker.CurrentTaskId);
        Assert.Equal(WorkerRole.Coder, worker.Role);
        Assert.Equal(HeldModel, worker.CurrentModel);
        Assert.Equal(["dotnet"], worker.Capabilities);

        // THE ACTIVE ENTRY IS THE EXACT TASK BUILT FROM THE RECORDED CONTEXT, ASSIGNED TO THE WORKER.
        var task = Assert.IsType<WorkTask>(result.Task);
        Assert.Same(task, queue.GetActiveTask(taskId));
        Assert.Equal(workerId, task.Metadata["assigned_worker"]);
        Assert.Equal(taskId, task.TaskId);
        Assert.Equal(goalId, task.GoalId);
        Assert.Equal(pipeline.Description, task.GoalDescription);
        Assert.Equal(WorkerRole.Coder, task.Role);
        Assert.Equal(HeldModel, task.Model);
        Assert.Equal("", task.Prompt);
        Assert.Empty(task.Repositories);

        // THE HOLD IS GONE, and only the adopted state can explain it.
        Assert.False(pipeline.IsRestoredActiveAttemptHold);
    }

    // ═════════════ (k) the deferred second-restart boundary ═════════════

    /// <summary>
    /// (k) THE DEFERRED LIMITATION, PINNED. A restored pipeline (ineligible for registry
    /// checkpoints, so a task it dispatches AFTER the restart is never recorded in the blob) whose
    /// active pointer names a task the RESTORED REGISTRY does not contain — the second-restart
    /// shape — FAILS CLOSED: the adoption is refused with <c>SlotNotPending</c> (the pointer
    /// observation found no matching slot) or <c>SlotMismatch</c>, the pipeline STAYS HELD, and the
    /// worker is neither registered nor given an active queue entry. The follow-up grace sweep, not
    /// this path, owns that attempt.
    /// <para>
    /// The absent task's evidence is COMPLETE — a persisted mapping and a fully matching recorded
    /// assignment context — so the refusal cannot be explained by missing evidence: the only thing
    /// that fails is the restored registry's silence about the task.
    /// </para>
    /// </summary>
    [Fact]
    public void TryAdoptRestoredAttempt_SecondRestartPointerAbsentFromRegistry_FailsClosedAndStaysHeld()
    {
        var goalId = "adopt-second-restart";
        var (_, registeredTaskId, _) = SeedHeldAttempt(goalId, "worker-first");

        // THE TASK DISPATCHED AFTER THE RESTART: a committed mapping and a fully matching recorded
        // assignment context, but NO registry entry — the restored blob still names only the first
        // attempt, because a restored pipeline never becomes checkpoint-eligible.
        var absentTaskId = $"{goalId}-coder-001-01-001-postrestart";
        var secondWorkerId = "worker-second-restart";
        CreateStore().SaveTaskMapping(absentTaskId, goalId);

        var contextWrite = CreateAssignmentStore().InsertOnce(new WorkerAssignmentContext(
            goalId,
            secondWorkerId,
            WorkerRole.Coder,
            new WorkSlot(absentTaskId, new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
            HeldModel));
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, contextWrite.Status);

        // The pointer is moved onto the ABSENT task by the persisted row itself — exactly what a
        // post-restart dispatch leaves behind.
        RawUpdate(goalId, "active_task_id", absentTaskId);

        var manager = new GoalPipelineManager(
            CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.RestorePipeline(goalId);
        Assert.NotNull(pipeline);
        Assert.True(pipeline!.IsRestoredActiveAttemptHold);
        Assert.Equal(RestoredRegistryOutcome.Restored, pipeline.RestoredRegistryClassification);
        Assert.Equal(absentTaskId, pipeline.ActiveTaskId);
        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
        Assert.Equal(GoalPhase.Coding, pipeline.StateMachine.Phase);
        // THE MISSING SLOT IS THE ONLY BROKEN EVIDENCE — reported, never repaired.
        Assert.Equal(RestoredActivePointerOutcome.NoMatchingSlot, pipeline.RestoredActivePointerClassification);
        Assert.Same(pipeline, manager.GetByTaskId(absentTaskId));

        var pool = new WorkerPool();
        var queue = new TaskQueue();
        var result = CreateAdopter(manager, pool, queue)
            .TryAdoptRestoredAttempt(secondWorkerId, absentTaskId, [], requestCompletionReceiptAck: false,
                completionReceiptAckEnabled: false);

        Assert.False(result.Adopted);
        Assert.Equal(RestoredAttemptAdoptionOutcome.Refused, result.Outcome);
        Assert.Contains(
            result.Refusal,
            new[] { RestoredAttemptAdoptionRefusal.SlotNotPending, RestoredAttemptAdoptionRefusal.SlotMismatch });
        Assert.Null(result.Worker);
        Assert.Null(result.Task);

        // IT STAYS HELD, and neither the pool nor the queue was touched — the attempt waits for the
        // sweep, and for nothing else.
        Assert.True(pipeline.IsRestoredActiveAttemptHold, "a refused adoption must leave the hold in force");
        Assert.Null(pool.GetWorker(secondWorkerId));
        Assert.Equal(0, pool.ConnectedWorkerCount);
        Assert.Null(queue.GetActiveTask(absentTaskId));
        Assert.Null(queue.GetActiveTask(registeredTaskId));
    }

    // ═══════════════ THE REGISTER DECISION — (a), (c), (d), (e), (f), (i) ═══════════════

    /// <summary>
    /// (i) THE EMPTY CLAIM IS TODAY'S PATH, EXACTLY: the SAME accepted reply (accepted, version,
    /// assigned id, the negotiated enablement facts) with <c>adopted_task = false</c>, the worker
    /// registered IDLE, no active queue entry, and NO new log records — a single Information
    /// <c>"Worker registered"</c> line and nothing else. The non-empty claim is included as the
    /// control: it is the SAME request shape, so the difference in records is attributable to the
    /// claim alone.
    /// </summary>
    [Fact]
    public async Task Register_EmptyClaim_TakesTodaysPath_WithNoNewLogs()
    {
        var goalId = "adopt-register-empty";
        var (_, taskId, _) = SeedHeldAttempt(goalId, "worker-empty");
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);
        var harness = CreateRegisterHarness(manager, withAdopter: true);

        var response = await RegisterAsync(harness, "worker-empty", "");

        // TODAY'S EXACT REPLY, plus an explicit adopted_task = false.
        Assert.True(response.Accepted);
        Assert.False(response.AdoptedTask);
        Assert.Equal(CopilotHive.Services.VersionHelper.InformationalVersion, response.OrchestratorVersion);
        Assert.Equal("worker-empty", response.AssignedWorkerId);

        // THE WORKER IS REGISTERED AND IDLE, and the attempt is untouched.
        var registered = Assert.IsType<ConnectedWorker>(harness.Pool.GetWorker("worker-empty"));
        Assert.False(registered.IsBusy);
        Assert.Null(registered.CurrentTaskId);
        Assert.True(pipeline.IsRestoredActiveAttemptHold);
        Assert.Null(harness.Queue.GetActiveTask(taskId));

        // NO NEW LOGS: exactly one record, today's own Information line, and NO warning at all.
        var entry = Assert.Single(harness.Logger.LogEntries);
        Assert.Equal(LogLevel.Information, entry.LogLevel);
        Assert.Equal("Worker registered: worker-empty", entry.Message);

        // CONTROL: the same registration WITH a claim over an identical fresh harness emits the one
        // refusal warning, so the absence above is the empty claim's doing rather than a dead sink.
        var controlManager = new GoalPipelineManager(
            CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var control = CreateRegisterHarness(controlManager, withAdopter: true);
        var controlResponse = await RegisterAsync(control, "worker-claimed", "no-such-task");

        Assert.False(controlResponse.AdoptedTask);
        Assert.Contains(control.Logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning && e.Message.Contains("check=NoPipeline", StringComparison.Ordinal));
    }

    /// <summary>
    /// (c) EVERY FAILED PRECONDITION, driven through the REAL
    /// <see cref="HiveOrchestratorService.Register"/>: <c>adopted_task = false</c>, the worker
    /// registered and IDLE, the hold UNCHANGED, exactly ONE Warning naming that check, and no active
    /// queue entry.
    /// <para>
    /// EACH CASE OWNS ITS BROKEN EVIDENCE. The seeded fixture is the PERFECT adoptable shape, and
    /// every case then damages EXACTLY ONE thing — the mapping, the pointer, the registry text, the
    /// recorded context, the restore-time classification or the phase — so the named check is the
    /// only check that could have refused. Cases that need a different task or phase re-seed
    /// themselves rather than mutating the shared fixture.
    /// </para>
    /// </summary>
    /// <param name="caseName">
    /// The case; its name (before any <c>-Variant</c> suffix) is the check the single warning must
    /// carry. <c>RegistryEvidenceUntrusted</c> is covered as BOTH DecodeRejected and MissingRegistry.
    /// </param>
    [Theory]
    [InlineData("NoPipeline")]
    [InlineData("NotHeld")]
    [InlineData("PointerMismatch")]
    [InlineData("RegistryEvidenceUntrusted")]
    [InlineData("RegistryEvidenceUntrusted-MissingRegistry")]
    [InlineData("SlotNotPending")]
    [InlineData("NoAssignmentContext")]
    [InlineData("WorkerMismatch")]
    [InlineData("GoalMismatch")]
    [InlineData("SlotMismatch")]
    [InlineData("PhaseMismatch")]
    [InlineData("NoAdopter")]
    public async Task Register_FailedPrecondition_DeclinesWithOneNamedWarning(string caseName)
    {
        // A case name may carry a "-Variant" suffix; the CHECK the warning must name is the prefix.
        var expectedCheck = caseName.Split('-')[0];
        var goalId = $"adopt-register-{caseName}".ToLowerInvariant();
        var workerId = $"worker-{caseName}".ToLowerInvariant();

        string claimedTaskId;
        string registeringWorkerId;
        bool useAdopter = true;
        bool expectHeld = true;
        GoalPipelineManager manager;
        GoalPipeline pipeline;

        switch (caseName)
        {
            case "NoPipeline":
            {
                // A task id no pipeline and no mapping is registered for.
                var seeded = SeedHeldAttempt(goalId, "worker-recorded");
                (manager, pipeline) = RestoreForRegister(goalId);
                claimedTaskId = seeded.TaskId + "-unmapped";
                registeringWorkerId = workerId;
                break;
            }

            case "NotHeld":
            {
                // A NULL pointer: the row is not held at all, so no claim can ever be adopted.
                var pipelineSeed = new GoalPipelineManager(CreateStore(), NullLogger<GoalPipelineManager>.Instance);
                var notHeld = pipelineSeed.CreatePipeline(NewGoal(goalId));
                Arrange(notHeld, GoalPhase.Coding);

                // The claimed task gets a mapping and a recorded context BEFORE the restore, so both
                // travel with the snapshot and the RESTORED manager can resolve the id — while the
                // pointer stays NULL, which is what makes the row unheld. NOTHING else can refuse, so
                // the missing hold is genuinely the first check to fail.
                var taskId = $"{goalId}-coder-001-01-001-unheld";
                pipelineSeed.RegisterTask(taskId, goalId);
                CreateStore().SaveTaskMapping(taskId, goalId);
                CreateAssignmentStore().InsertOnce(new WorkerAssignmentContext(
                    goalId, workerId, WorkerRole.Coder,
                    new WorkSlot(taskId, new WorkSlotPosition(1, GoalPhase.Coding, 1), 1), HeldModel));
                pipelineSeed.PersistFull(notHeld);
                Assert.Null(notHeld.ActiveTaskId);

                (manager, pipeline) = RestoreForRegister(goalId, expectHeld: false);
                Assert.Same(pipeline, manager.GetByTaskId(taskId));
                claimedTaskId = taskId;
                registeringWorkerId = workerId;
                expectHeld = false;
                break;
            }

            case "PointerMismatch":
            {
                // The pointer names a DIFFERENT task than the one being claimed.
                var seeded = SeedHeldAttempt(goalId, "worker-recorded");

                // A second task with its own mapping and correct context, saved BEFORE the restore so
                // the restored manager can resolve it. The pointer stays on the ORIGINAL task, so the
                // claim names a task this pipeline does not currently point at.
                claimedTaskId = $"{goalId}-coder-001-01-002-other";
                CreateStore().SaveTaskMapping(claimedTaskId, goalId);
                CreateAssignmentStore().InsertOnce(new WorkerAssignmentContext(
                    goalId, workerId, WorkerRole.Coder,
                    new WorkSlot(claimedTaskId, new WorkSlotPosition(1, GoalPhase.Coding, 2), 1), HeldModel));

                (manager, pipeline) = RestoreForRegister(goalId);
                Assert.Equal(seeded.TaskId, pipeline.ActiveTaskId);
                Assert.Same(pipeline, manager.GetByTaskId(claimedTaskId));
                registeringWorkerId = workerId;
                break;
            }

            case "RegistryEvidenceUntrusted":
            {
                // Decode-rejected registry text: the restore classification cannot be Restored.
                SeedHeldAttempt(goalId, "worker-recorded");
                RawUpdate(goalId, "work_slot_registry_json", "{not json");
                (manager, pipeline) = RestoreForRegister(goalId);
                Assert.Equal(RestoredRegistryOutcome.DecodeRejected, pipeline.RestoredRegistryClassification);
                claimedTaskId = pipeline.ActiveTaskId!;
                registeringWorkerId = workerId;
                break;
            }

            case "RegistryEvidenceUntrusted-MissingRegistry":
            {
                // A GENUINE SQL-NULL registry column: the evidence is MISSING, so the restore
                // installs nothing and classifies it MissingRegistry — still held (the hold is
                // decided from the phase and pointer alone), never adoptable.
                var seeded = SeedHeldAttempt(goalId, workerId);
                RawUpdate(goalId, "work_slot_registry_json", null);
                (manager, pipeline) = RestoreForRegister(goalId);
                Assert.Equal(RestoredRegistryOutcome.MissingRegistry, pipeline.RestoredRegistryClassification);
                Assert.Empty(pipeline.CaptureRegistry().Slots);
                Assert.Equal(seeded.TaskId, pipeline.ActiveTaskId);
                claimedTaskId = seeded.TaskId;
                registeringWorkerId = workerId;
                break;
            }

            case "SlotNotPending":
            {
                // The matching slot was CLAIMED in the persisted registry the restore hydrated.
                var seeded = SeedHeldAttempt(goalId, "worker-recorded");
                RawUpdate(goalId, "work_slot_registry_json", WorkSlotRegistryCodec.Encode(
                    new WorkSlotRegistrySnapshot(
                        [new WorkSlotView(
                            new WorkSlot(seeded.TaskId, new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
                            WorkSlotState.Claimed)],
                        [new WorkSlotRegistryAttemptEntry(new WorkSlotPosition(1, GoalPhase.Coding, 1), 1)])));
                (manager, pipeline) = RestoreForRegister(goalId);
                Assert.Equal(RestoredActivePointerOutcome.ActiveSlotClaimed,
                    pipeline.RestoredActivePointerClassification);
                claimedTaskId = seeded.TaskId;
                registeringWorkerId = workerId;
                break;
            }

            case "NoAssignmentContext":
            {
                // A genuinely-mapped, genuinely-pointed-at task with NO recorded context at all.
                var seeded = SeedHeldAttempt(goalId, "worker-recorded", recordAssignmentContext: false);
                (manager, pipeline) = RestoreForRegister(goalId);
                Assert.Equal(RestoredRegistryOutcome.Restored, pipeline.RestoredRegistryClassification);
                Assert.Equal(RestoredActivePointerOutcome.ActiveSlotPending,
                    pipeline.RestoredActivePointerClassification);
                Assert.Null(CreateAssignmentStore().Load(seeded.TaskId));
                claimedTaskId = seeded.TaskId;
                registeringWorkerId = workerId;
                break;
            }

            case "WorkerMismatch":
            {
                // The recorded context belongs to a DIFFERENT worker than the registering one: the
                // seed's own worker id is recorded and a DIFFERENT id is what registers.
                SeedHeldAttempt(goalId, "worker-recorded");
                (manager, pipeline) = RestoreForRegister(goalId);
                claimedTaskId = pipeline.ActiveTaskId!;
                registeringWorkerId = workerId;
                Assert.NotEqual("worker-recorded", registeringWorkerId);
                break;
            }

            case "GoalMismatch":
            {
                // The stored context names a different goal than the pipeline's — written directly,
                // because the insert-once contract deliberately refuses to rebind a row.
                var seeded = SeedHeldAttempt(goalId, workerId);
                RawUpdateContextGoal(seeded.TaskId, goalId + "-other");
                (manager, pipeline) = RestoreForRegister(goalId);
                claimedTaskId = seeded.TaskId;
                registeringWorkerId = workerId;
                break;
            }

            case "SlotMismatch":
            {
                // The restored registry's slot carries a DIFFERENT attempt than the recorded context.
                // The recorded context IS the seed's own (worker id and all), so WorkerMismatch cannot
                // pre-empt the comparison this vector is about.
                var seeded = SeedHeldAttempt(goalId, workerId);
                RawUpdate(goalId, "work_slot_registry_json", WorkSlotRegistryCodec.Encode(
                    new WorkSlotRegistrySnapshot(
                        [new WorkSlotView(
                            new WorkSlot(seeded.TaskId, new WorkSlotPosition(1, GoalPhase.Coding, 1), 7),
                            WorkSlotState.Pending)],
                        [new WorkSlotRegistryAttemptEntry(new WorkSlotPosition(1, GoalPhase.Coding, 1), 7)])));
                (manager, pipeline) = RestoreForRegister(goalId);
                claimedTaskId = seeded.TaskId;
                registeringWorkerId = workerId;
                break;
            }

            case "PhaseMismatch":
            {
                // The restored pipeline sits on a phase with NO worker (Planning). The recorded
                // context is the seed's own, so the phase is the only broken evidence.
                var seeded = SeedHeldAttempt(goalId, workerId);
                RawUpdate(goalId, "phase", "Planning");
                (manager, pipeline) = RestoreForRegister(goalId);
                Assert.Equal(GoalPhase.Planning, pipeline.Phase);
                claimedTaskId = seeded.TaskId;
                registeringWorkerId = workerId;
                break;
            }

            case "NoAdopter":
            {
                // No adopter is injected at all, so nothing can be adopted.
                SeedHeldAttempt(goalId, "worker-recorded");
                (manager, pipeline) = RestoreForRegister(goalId);
                claimedTaskId = pipeline.ActiveTaskId!;
                registeringWorkerId = workerId;
                useAdopter = false;
                break;
            }

            default:
                throw new InvalidOperationException($"Unhandled case: {caseName}");
        }

        var harness = CreateRegisterHarness(manager, useAdopter);
        var response = await RegisterAsync(harness, registeringWorkerId, claimedTaskId);

        // THE DECLINED CLAIM: registered, idle, hold unchanged, no active entry.
        Assert.True(response.Accepted, "a declined claim never fails the registration itself");
        Assert.False(response.AdoptedTask);
        AssertSingleAdoptionRefusalWarning(harness, expectedCheck, registeringWorkerId, claimedTaskId);
        AssertDeclinedClaim(harness, pipeline, registeringWorkerId, claimedTaskId, expectHeld);
    }

    /// <summary>
    /// (c) THE <c>ReadFailed</c> PRECONDITION, which needs a THROWING read rather than broken
    /// evidence: the assignment store's own read raises, so the adopter must contain it, name the
    /// check and decline. The store is a real one whose read is made to fail by dropping the
    /// underlying table — a genuine database failure, not a fabricated result.
    /// </summary>
    [Fact]
    public async Task Register_AssignmentReadThrows_DeclinesWithReadFailed()
    {
        var goalId = "adopt-register-readfailed";
        var workerId = "worker-readfailed";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        // THE GENUINE READ FAILURE: the assignment table is gone, so Load throws.
        using (var command = _keeper.CreateCommand())
        {
            command.CommandText = "DROP TABLE worker_assignment_contexts";
            command.ExecuteNonQuery();
        }

        var harness = CreateRegisterHarness(manager, withAdopter: true);
        var response = await RegisterAsync(harness, workerId, taskId);

        Assert.True(response.Accepted);
        Assert.False(response.AdoptedTask);

        // EXACTLY ONE WARNING ACROSS BOTH SINKS (the adopter's sink is CAPTURED, not discarded), and
        // the underlying read failure stays visible in that single line as a sanitized cause.
        var warning = AssertSingleAdoptionRefusalWarning(harness, "ReadFailed", workerId, taskId);
        Assert.Contains("cause:", warning, StringComparison.Ordinal);
        Assert.Contains("worker_assignment_contexts", warning, StringComparison.Ordinal);
        Assert.Empty(harness.AdopterLogger!.LogEntries);
        Assert.DoesNotContain(harness.AllEntries, e => e.Exception is not null);
        AssertDeclinedClaim(harness, pipeline, workerId, taskId);
    }

    /// <summary>
    /// (d) AN EXISTING ACTIVE QUEUE ENTRY: the attempt is adopted and then the busy registration is
    /// refused by the non-overwriting queue primitive, so the adoption is ROLLED BACK — the reply
    /// says <c>adopted_task = false</c>, the pre-existing entry is UNTOUCHED (same instance, same
    /// worker), the worker is idle, and the hold is back to Held.
    /// </summary>
    [Fact]
    public async Task Register_ExistingActiveEntry_DeclinesAndRollsBackTheHold()
    {
        var goalId = "adopt-register-active-entry";
        var workerId = "worker-active-entry";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        // THE PRE-EXISTING ENTRY, owned by a different worker: it must survive untouched.
        var incumbent = BuildTask(taskId) with { GoalId = goalId };
        var queue = new TaskQueue();
        Assert.True(queue.TryActivateNew(incumbent, "worker-incumbent"));

        var harness = CreateRegisterHarness(manager, withAdopter: true, queue: queue);
        var response = await RegisterAsync(harness, workerId, taskId);

        Assert.True(response.Accepted);
        Assert.False(response.AdoptedTask);
        AssertSingleAdoptionRefusalWarning(harness, "ActiveQueueEntryExists", workerId, taskId);

        // THE ENTRY IS UNTOUCHED — the SAME instance, still assigned to the incumbent.
        Assert.Same(incumbent, queue.GetActiveTask(taskId));
        Assert.Equal("worker-incumbent", incumbent.Metadata["assigned_worker"]);

        // THE HOLD WAS ROLLED BACK to Held, and the worker is idle.
        Assert.True(pipeline.IsRestoredActiveAttemptHold,
            "a refused busy registration must put the attempt back under hold");
        var registered = Assert.IsType<ConnectedWorker>(harness.Pool.GetWorker(workerId));
        Assert.False(registered.IsBusy);
        Assert.Null(registered.CurrentTaskId);
    }

    /// <summary>
    /// (e) AN ALREADY-REGISTERED WORKER ID TAKES PRECEDENCE OVER THE CLAIM: today's exact duplicate
    /// reply and warning, NO adoption warning at all, the hold UNCHANGED, and the already-registered
    /// instance untouched — even though the claim itself is perfectly valid.
    /// </summary>
    [Fact]
    public async Task Register_DuplicateWorkerId_TakesTodaysDuplicatePath_WithNoAdoptionWarning()
    {
        var goalId = "adopt-register-duplicate";
        var workerId = "worker-duplicate";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        var harness = CreateRegisterHarness(manager, withAdopter: true);
        var incumbent = harness.Pool.RegisterWorker(workerId, []);
        harness.Logger.LogEntries.Clear();

        var response = await RegisterAsync(harness, workerId, taskId);

        // TODAY'S EXACT DUPLICATE REJECTION.
        Assert.False(response.Accepted);
        Assert.False(response.AdoptedTask);
        Assert.False(response.CompletionReceiptAckEnabled);
        Assert.False(response.CompletionReadyRequired);

        // EXACTLY ONE WARNING, and it is the DUPLICATE one — no adoption warning was emitted.
        var warning = Assert.Single(harness.Logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("Registration rejected — duplicate worker ID", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("was not adopted", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Logger.LogEntries,
            e => e.Message.Contains("was not adopted", StringComparison.Ordinal));

        // THE INSTANCE AND THE ATTEMPT ARE UNTOUCHED.
        Assert.Same(incumbent, harness.Pool.GetWorker(workerId));
        Assert.False(incumbent.IsBusy);
        Assert.True(pipeline.IsRestoredActiveAttemptHold);
        Assert.Null(harness.Queue.GetActiveTask(taskId));
    }

    /// <summary>
    /// (f) THE RELEASE WINS FIRST: the reconciliation sweep releases the hold INSIDE the adoption's
    /// own commit window — the real interleaving, driven through the adopter's test-only hook placed
    /// at that exact boundary — so the compare-and-swap loses. The reply says
    /// <c>adopted_task = false</c> with the <c>HoldAlreadyReleased</c> warning, the worker is idle and
    /// there is no active queue entry. Nothing is re-adopted and the released hold is not resurrected.
    /// </summary>
    [Fact]
    public async Task Register_ReleaseWonTheRace_DeclinesWithHoldAlreadyReleased()
    {
        var goalId = "adopt-register-release-wins";
        var workerId = "worker-release-wins";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        var harness = CreateRegisterHarness(manager, withAdopter: true);
        var adopter = Assert.IsType<RestoredAttemptAdopter>(harness.Adopter);

        // THE SWEEP LANDS INSIDE THE COMMIT WINDOW: after every read-only precondition held and
        // before the adoption's compare-and-swap — the production interleaving, at the real boundary.
        var sweepRan = false;
        adopter.BeforeAdoptCommitForTest = () =>
        {
            sweepRan = true;
            Assert.True(pipeline.TryReleaseRestoredActiveAttemptHold(),
                "the sweep must win the hold inside the commit window");
        };

        var response = await RegisterAsync(harness, workerId, taskId);

        Assert.True(sweepRan, "the commit window must have been reached for this vector to be non-vacuous");
        Assert.True(response.Accepted, "a lost race never fails the registration itself");
        Assert.False(response.AdoptedTask);
        AssertSingleAdoptionRefusalWarning(harness, "HoldAlreadyReleased", workerId, taskId);

        // THE RELEASE STANDS: the hold is NOT resurrected and nothing was registered busy.
        Assert.False(pipeline.IsRestoredActiveAttemptHold);
        Assert.False(pipeline.TryAdoptRestoredActiveAttempt(),
            "a released hold can never be adopted afterwards");
        AssertDeclinedClaim(harness, pipeline, workerId, taskId, expectHeld: false);
    }

    /// <summary>
    /// (a)/(b) THE ADOPTED HAPPY PATH THROUGH THE REAL <c>Register</c>: <c>adopted_task = true</c>,
    /// the instance busy with the recorded Role/Model, the active entry assigned to it, the hold
    /// gone — and the ONE Information adoption line, with no warning at all. The commit order itself
    /// is pinned by the direct vector above; this one proves the Reply contract.
    /// </summary>
    [Fact]
    public async Task Register_MatchingClaim_AdoptsAndRepliesAdoptedTaskTrue()
    {
        var goalId = "adopt-register-happy";
        var workerId = "worker-register-happy";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        var harness = CreateRegisterHarness(manager, withAdopter: true);
        var response = await RegisterAsync(harness, workerId, taskId);

        Assert.True(response.Accepted);
        Assert.True(response.AdoptedTask);
        Assert.Equal(workerId, response.AssignedWorkerId);

        // THE INSTANCE IS BUSY WITH THE RECORDED ROLE AND MODEL, and the queue entry is its own.
        var registered = Assert.IsType<ConnectedWorker>(harness.Pool.GetWorker(workerId));
        Assert.True(registered.IsBusy);
        Assert.Equal(taskId, registered.CurrentTaskId);
        Assert.Equal(WorkerRole.Coder, registered.Role);
        Assert.Equal(HeldModel, registered.CurrentModel);

        var active = Assert.IsType<WorkTask>(harness.Queue.GetActiveTask(taskId));
        Assert.Equal(workerId, active.Metadata["assigned_worker"]);
        Assert.False(pipeline.IsRestoredActiveAttemptHold);

        // THE ONE INFORMATION ADOPTION LINE, AND NO WARNING AT ALL.
        Assert.Contains(harness.Logger.LogEntries,
            e => e.LogLevel == LogLevel.Information &&
                 e.Message.Contains("adopted by worker", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
    }

    // ═══════════════ (b) THE COMMIT ORDER — worker visibility implies the hold already left ═══════════════

    /// <summary>
    /// (b) THE COMMIT ORDER, OBSERVED AT ITS OWN BOUNDARY. The adopter's commit-window hook sits
    /// exactly between the read-only evidence phase and the adoption's compare-and-swap — the same
    /// boundary a racing observer of <see cref="WorkerPool.GetWorker"/> would land on. The test PARKS
    /// the adoption on that hook with a <see cref="TaskCompletionSource"/> barrier, observes the
    /// pre-commit state, releases it, and derives the ordered fact from the completed observable
    /// state.
    /// <para>
    /// THE PRE-COMMIT OBSERVATION (the window, held open while the adoption is parked): the attempt
    /// is STILL HELD, the worker is NOT YET visible through <see cref="WorkerPool.GetWorker"/>, and
    /// the queue holds no active entry. This kills the register-first mutant outright: an
    /// implementation that published the worker before taking the hold would be visible inside the
    /// window.
    /// </para>
    /// <para>
    /// THE ORDERED FACT, DERIVED FROM OBSERVABLE STATE: after the released commit completes, the
    /// worker IS visible and busy, and the hold IS gone. The worker's visibility is produced ONLY by
    /// <see cref="RestoredAttemptAdopter"/>'s commit step
    /// (<see cref="WorkerPool.RegisterAdoptedWorker"/>), which the adopter reaches only AFTER
    /// <see cref="GoalPipeline.TryAdoptRestoredActiveAttempt"/> returned true in the same synchronous
    /// sequence — a CAS loss never registers the worker at all, which the release-wins vector below
    /// pins. Worker visibility therefore implies the hold had already left at or before the
    /// visibility instant: no observer can see a busy worker under a hold, and the in-window
    /// observation plus the end state pin that order without any timing dependence.
    /// </para>
    /// <para>
    /// DETERMINISTIC SYNCHRONIZATION, NO SLEEPS: the adopter is parked on a
    /// <see cref="TaskCompletionSource"/> the test completes; the test waits on the entry signal the
    /// same way. Every await carries a 30-second failure bound, so a lost rendezvous is a named
    /// failure, never a stall.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TryAdoptRestoredAttempt_AtTheCommitWindow_TheWorkerIsInvisible_AndVisibilityImpliesTheHoldLeft()
    {
        var goalId = "adopt-commit-order";
        var workerId = "worker-commit-order";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        var pool = new WorkerPool();
        var queue = new TaskQueue();

        // THE PRE-ADOPTION CONTROL: before the commit, the pool has no worker under this id at all,
        // so the later positive reading can never be an artifact of a pool that always answers.
        Assert.Null(pool.GetWorker(workerId));
        Assert.True(pipeline.IsRestoredActiveAttemptHold);

        // THE HELD COMMIT WINDOW: the hook is invoked exactly once, after every precondition held and
        // immediately before the CAS. It signals entry and then BLOCKS the adoption until the test
        // releases it, so the pre-commit state is observable and stable.
        var windowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var windowReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookInvocations = 0;

        var adopter = CreateAdopter(manager, pool, queue);
        adopter.BeforeAdoptCommitForTest = () =>
        {
            Interlocked.Increment(ref hookInvocations);
            windowEntered.TrySetResult();
            // THE PARK: the adoption cannot proceed until the test releases it. The blocking wait
            // lives INSIDE the hook, so the whole commit sequence is frozen at the boundary.
            windowReleased.Task.Wait();
        };

        var adoptionTask = Task.Run(
            () => adopter.TryAdoptRestoredAttempt(
                workerId, taskId, ["dotnet"], requestCompletionReceiptAck: false,
                completionReceiptAckEnabled: false),
            CancellationToken.None);

        try
        {
            await windowEntered.Task.WaitAsync(CommitOrderWait, CancellationToken.None);

            // ── THE WINDOW: preconditions held, CAS not yet run, nothing published. ──────────────
            Assert.True(pipeline.IsRestoredActiveAttemptHold,
                "before the compare-and-swap the attempt is still held — the commit has not begun");
            Assert.True(pool.GetWorker(workerId) is null,
                "no worker is visible before the commit");
            Assert.Null(queue.GetActiveTask(taskId));
        }
        finally
        {
            // THE RENDEZVOUS IS ALWAYS RELEASED, whatever the window's assertions did — a parked
            // adoption must never be leaked by a failing assertion above.
            windowReleased.TrySetResult();

            var result = await adoptionTask;

            Assert.True(result.Adopted, "the fixture's adoption must succeed for this vector");
            Assert.True(1 == Volatile.Read(ref hookInvocations),
                "the commit-window hook must fire exactly once per adoption");

            // ── THE END STATE the ordering must be consistent with: the worker is visible and busy,
            //    the entry is the adopter's own, and the hold is gone. ─────────────────────────────
            var worker = Assert.IsType<ConnectedWorker>(pool.GetWorker(workerId));
            Assert.True(worker.IsBusy);
            Assert.Equal(taskId, worker.CurrentTaskId);
            var task = Assert.IsType<WorkTask>(queue.GetActiveTask(taskId));
            Assert.Equal(workerId, task.Metadata["assigned_worker"]);
            Assert.False(pipeline.IsRestoredActiveAttemptHold,
                "at the instant the worker is visible via GetWorker the hold must already be gone");

            // THE OUTCOME ITSELF IS ORDER EVIDENCE: an Adopted outcome is reachable ONLY through a
            // successful CAS followed by the busy registration, in that order, in one synchronous
            // sequence. The release-wins vector below separately proves a lost CAS never registers
            // the worker at all — so visibility implies the CAS had already succeeded.
            Assert.Equal(RestoredAttemptAdoptionOutcome.Adopted, result.Outcome);
        }
    }

    /// <summary>Bound for the commit-window rendezvous; a hang is a named failure, never a stall.</summary>
    private static readonly TimeSpan CommitOrderWait = TimeSpan.FromSeconds(30);

    // ═══════════════ (b) THE COMMIT ORDER AT THE REAL POOL-PUBLICATION BOUNDARY ═══════════════

    /// <summary>
    /// (b) THE COMMIT ORDER, WITNESSED AT THE REAL PUBLICATION POINT. The pool's publication-boundary
    /// hook runs inside <see cref="WorkerPool.RegisterAdoptedWorker"/>'s own lock span, after the
    /// active entry was claimed and IMMEDIATELY BEFORE the worker enters the pool dictionary. At that
    /// instant the hold must ALREADY have left (the adopter's CAS precedes publication) and the
    /// worker must still be INVISIBLE through <see cref="WorkerPool.GetWorker"/>.
    /// <para>
    /// REMOVAL-PROOFNESS. A register-before-adopt reversal would reach this hook while the attempt is
    /// still HELD, failing the in-hook assertion — which is captured and re-thrown after the call so
    /// it cannot be swallowed by the adopter's commit-failure containment. The hook firing exactly
    /// once proves the witness genuinely sat on the publication path.
    /// </para>
    /// </summary>
    [Fact]
    public void TryAdoptRestoredAttempt_AtThePoolPublicationBoundary_TheHoldHasAlreadyLeft_AndTheWorkerIsInvisible()
    {
        var goalId = "adopt-publication-order";
        var workerId = "worker-publication-order";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        var pool = new WorkerPool();
        var queue = new TaskQueue();
        var adopter = CreateAdopter(manager, pool, queue);

        var hookInvocations = 0;
        bool? heldAtPublication = null;
        ConnectedWorker? visibleAtPublication = null;
        pool.BeforeAdoptedWorkerPublicationForTest = id =>
        {
            hookInvocations++;
            Assert.Equal(workerId, id);
            heldAtPublication = pipeline.IsRestoredActiveAttemptHold;
            visibleAtPublication = pool.GetWorker(id);
        };

        var result = adopter.TryAdoptRestoredAttempt(
            workerId, taskId, [], requestCompletionReceiptAck: false, completionReceiptAckEnabled: false);

        Assert.Equal(1, hookInvocations);
        Assert.True(heldAtPublication == false,
            "at the instant of publication the hold must ALREADY have left — the adoption CAS precedes it");
        Assert.Null(visibleAtPublication);

        Assert.True(result.Adopted);
        Assert.Same(result.Worker, pool.GetWorker(workerId));
        Assert.False(pipeline.IsRestoredActiveAttemptHold);
    }

    // ═══════════════ (F7) THE STALE-EVIDENCE RACE — revalidated in ONE synchronized region ═══════════════

    /// <summary>
    /// THE STALE-EVIDENCE RACE, one vector per REAL invalidating writer. Every read-only precondition
    /// has already held when the adoption is PARKED on its commit-window hook; the writer then runs
    /// its real production transition; the adoption is released. The synchronized
    /// validate-and-adopt region must see the invalidated LIVE evidence and REFUSE with
    /// <c>AttemptNoLongerValid</c> — never <c>adopted = true</c>, never a published busy worker, never
    /// an active entry — and the hold is left exactly as the writer left it (still Held: the
    /// invalidation does not touch it, and the refusal mutates nothing).
    /// <para>
    /// THE WRITERS, each driven through its production transition:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>cancel</c> — the lifecycle service's terminal
    ///     <c>MarkGoalFailedAsync</c> (what <c>GoalDispatcher.CancelGoalAsync</c> runs), which
    ///     advances the pipeline to Failed under the pipeline lock;</description></item>
    ///   <item><description><c>admission</c> — <see cref="GoalPipeline.AdmitCompletion"/>, which
    ///     CLAIMS the Pending slot;</description></item>
    ///   <item><description><c>retire</c> — the stale-cleanup
    ///     <see cref="GoalPipeline.RetireSlotAndClearIfCurrent"/>, which abandons the slot and clears
    ///     the pointer;</description></item>
    ///   <item><description><c>new-iteration</c> — the machine's <c>Fail</c> plus the paired
    ///     <c>AdvanceTo</c> of a terminal failure: both the pipeline and the machine leave the slot's
    ///     phase.</description></item>
    /// </list>
    /// <para>
    /// REMOVAL-PROOFNESS (scratch-verified): replacing the synchronized region with the bare hold CAS
    /// makes every case fail with <c>adopted = true</c> and a published busy worker.
    /// </para>
    /// </summary>
    /// <param name="writer">Which invalidating writer lands inside the commit window.</param>
    [Theory]
    [InlineData("cancel")]
    [InlineData("admission")]
    [InlineData("retire")]
    [InlineData("new-iteration")]
    public async Task Register_InvalidatingTransitionInsideTheCommitWindow_RefusesWithAttemptNoLongerValid(string writer)
    {
        var goalId = $"adopt-stale-{writer}";
        var workerId = $"worker-stale-{writer}";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        var harness = CreateRegisterHarness(manager, withAdopter: true);
        var adopter = Assert.IsType<RestoredAttemptAdopter>(harness.Adopter);

        // THE INVALIDATING WRITER, run inside the commit window — after every precondition read,
        // before the synchronized region — through its real production transition.
        var writerRan = false;
        adopter.BeforeAdoptCommitForTest = () =>
        {
            writerRan = true;
            switch (writer)
            {
                case "cancel":
                    CancelLifecycle(pipeline)
                        .MarkGoalFailedAsync(pipeline, "Cancelled by user", CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert.Equal(GoalPhase.Failed, pipeline.Phase);
                    break;
                case "admission":
                    Assert.Equal(AdmissionOutcome.Admitted, pipeline.AdmitCompletion(taskId));
                    break;
                case "retire":
                    Assert.Equal(SlotRetirementOutcome.Retired, pipeline.RetireSlotAndClearIfCurrent(taskId));
                    Assert.Null(pipeline.ActiveTaskId);
                    break;
                case "new-iteration":
                    pipeline.StateMachine.Fail();
                    pipeline.AdvanceTo(GoalPhase.Failed);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled writer: {writer}");
            }
        };

        var response = await RegisterAsync(harness, workerId, taskId);

        Assert.True(writerRan, "the invalidating writer must have landed inside the commit window");
        Assert.True(response.Accepted, "a lost race never fails the registration itself");
        Assert.False(response.AdoptedTask, "an invalidated attempt must NEVER be reported adopted");

        // ONE NAMED WARNING, and the worker is registered ordinarily and IDLE with no active entry:
        // no busy worker was ever published for the invalidated attempt.
        AssertSingleAdoptionRefusalWarning(harness, "AttemptNoLongerValid", workerId, taskId);
        AssertDeclinedClaim(harness, pipeline, workerId, taskId, expectHeld: true);
    }

    /// <summary>
    /// The REAL <see cref="GoalLifecycleService"/> <c>GoalDispatcher.CancelGoalAsync</c> drives, over a
    /// real <see cref="GoalManager"/> whose goal source owns the pipeline's goal — so the terminal
    /// <c>MarkGoalFailedAsync</c> runs its whole production path (phase advance AND status write).
    /// </summary>
    private static GoalLifecycleService CancelLifecycle(GoalPipeline pipeline)
    {
        var goalManager = new GoalManager();
        goalManager.AddSource(new InMemoryGoalSource(pipeline.Goal));
        goalManager.GetNextGoalAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);
    }

    /// <summary>
    /// THE SAME RACE WITH THE MANAGER REMOVAL: <c>CancelGoalAsync</c>'s terminal failure followed by
    /// <see cref="GoalPipelineManager.RemovePipeline"/> lands inside the commit window. The removed
    /// pipeline no longer owns the claimed task, so the adoption refuses with
    /// <c>AttemptNoLongerValid</c> and publishes no busy worker.
    /// </summary>
    [Fact]
    public async Task Register_PipelineRemovedInsideTheCommitWindow_RefusesWithAttemptNoLongerValid()
    {
        var goalId = "adopt-stale-removed";
        var workerId = "worker-stale-removed";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        var harness = CreateRegisterHarness(manager, withAdopter: true);
        var adopter = Assert.IsType<RestoredAttemptAdopter>(harness.Adopter);

        adopter.BeforeAdoptCommitForTest = () =>
        {
            // THE PRODUCTION CANCEL SEQUENCE: terminal failure, then removal from the manager.
            CancelLifecycle(pipeline)
                .MarkGoalFailedAsync(pipeline, "Cancelled by user", CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.True(manager.RemovePipeline(goalId));
            Assert.Null(manager.GetByTaskId(taskId));
        };

        var response = await RegisterAsync(harness, workerId, taskId);

        Assert.True(response.Accepted);
        Assert.False(response.AdoptedTask);
        AssertSingleAdoptionRefusalWarning(harness, "AttemptNoLongerValid", workerId, taskId);
        AssertDeclinedClaim(harness, pipeline, workerId, taskId, expectHeld: true);
    }

    /// <summary>
    /// THE LINEARIZATION POINT ITSELF: an invalidating writer that tries to land INSIDE the
    /// synchronized region blocks on the pipeline lock until the region's CAS has committed, so the
    /// adoption wins and the writer then sees an ADOPTED attempt — the "after the CAS" half of the
    /// contract. The writer is started from the region's own hook, proven BLOCKED while the region
    /// holds the lock, and joined afterwards.
    /// </summary>
    [Fact]
    public async Task Register_WriterContendingForTheCommitRegion_BlocksUntilTheCasCommits()
    {
        var goalId = "adopt-stale-contended";
        var workerId = "worker-stale-contended";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        var harness = CreateRegisterHarness(manager, withAdopter: true);

        Task<AdmissionOutcome>? admission = null;
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.InsideAdoptionCommitRegionForTest = () =>
        {
            admission = Task.Run(() =>
            {
                writerStarted.TrySetResult();
                return pipeline.AdmitCompletion(taskId);
            });

            // The writer is running and contending — but it cannot enter while the region holds the
            // pipeline lock, so it has NOT completed by the time the region proceeds to its CAS.
            writerStarted.Task.Wait(CommitOrderWait);
            Assert.False(admission.Wait(TimeSpan.FromMilliseconds(200)),
                "a writer contending for the pipeline lock must block until the region's CAS commits");
        };

        var response = await RegisterAsync(harness, workerId, taskId);

        Assert.True(response.AdoptedTask, "the region held the lock through its CAS, so the adoption wins");
        Assert.NotNull(admission);
        Assert.Equal(AdmissionOutcome.Admitted, await admission!.WaitAsync(CommitOrderWait, TestContext.Current.CancellationToken));
        Assert.False(pipeline.IsRestoredActiveAttemptHold, "the writer landed AFTER the CAS: the hold is Adopted");
    }

    // ═══════════════ (F4) HONEST CLEANUP — a refused removal is REPORTED, never assumed away ═══════════════

    /// <summary>
    /// Replaces the active entry under <paramref name="taskId"/> with an equal-valued but DISTINCT
    /// instance through the queue's own public API — exactly what a concurrent writer re-activating
    /// the same task id does. The reference-checked <see cref="TaskQueue.TryRemoveOwned"/> then
    /// GENUINELY refuses the adopted registration's own instance: no seam, no decorator.
    /// </summary>
    private static WorkTask ReplaceActiveEntryWithForeignInstance(TaskQueue queue, string taskId)
    {
        var ours = Assert.IsType<WorkTask>(queue.GetActiveTask(taskId));
        var foreign = ours with { Metadata = new Dictionary<string, string>() };
        queue.MarkComplete(taskId);
        queue.Activate(foreign, "worker-foreign");
        Assert.NotSame(ours, queue.GetActiveTask(taskId));
        return foreign;
    }

    /// <summary>
    /// THE LOST-RACE CLEANUP IS HONEST: a same-id registration lands at the publication point (so the
    /// pool's <c>TryAdd</c> loses) while a concurrent writer has re-activated the task with a foreign
    /// instance, so the reference-checked removal GENUINELY refuses. The call still returns
    /// <c>DuplicateId</c>, but the refused cleanup is REPORTED — exactly once, naming the task and
    /// worker — instead of being silently assumed away, and the foreign entry is left untouched.
    /// <para>
    /// REMOVAL-PROOFNESS: ignoring <c>TryRemoveOwned</c>'s result (the pre-fix code) emits no warning
    /// and fails the single-warning assertion.
    /// </para>
    /// </summary>
    [Fact]
    public void RegisterAdoptedWorker_LostRaceWithRefusedRemoval_ReportsTheRefusedCleanup()
    {
        var logger = new AdoptionCapturingLogger<WorkerPool>();
        var pool = new WorkerPool(logger);
        var queue = new TaskQueue();
        var task = BuildTask("task-residue-race");
        WorkTask? foreign = null;

        // THE LOST RACE, at the real publication point: the pool's activity lock is reentrant for this
        // thread, so the competing same-id registration and the foreign re-activation complete here.
        pool.BeforeAdoptedWorkerPublicationForTest = id =>
        {
            pool.RegisterWorker(id, []);
            foreign = ReplaceActiveEntryWithForeignInstance(queue, task.TaskId);
        };

        var result = pool.RegisterAdoptedWorker(
            "worker-residue-race", [], requestCompletionReceiptAck: false, completionReceiptAckEnabled: false,
            task, queue);

        Assert.Equal(AdoptedRegistrationOutcome.DuplicateId, result.Outcome);
        Assert.Null(result.Worker);

        // THE FOREIGN ENTRY IS UNTOUCHED, and the refused cleanup is REPORTED exactly once.
        Assert.Same(foreign, queue.GetActiveTask(task.TaskId));
        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("stale-active-entry", warning.Message, StringComparison.Ordinal);
        Assert.Contains(task.TaskId, warning.Message, StringComparison.Ordinal);
        Assert.Contains("worker-residue-race", warning.Message, StringComparison.Ordinal);
        Assert.Contains("removal refused", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE EXCEPTION CLEANUP IS HONEST: a throw lands after the active entry was claimed (at the
    /// publication point), and the reference-checked removal GENUINELY refuses because a concurrent
    /// writer re-activated the task. The ORIGINAL exception is rethrown — never replaced — no worker
    /// is published, and the refused cleanup is REPORTED rather than claimed clean.
    /// </summary>
    [Fact]
    public void RegisterAdoptedWorker_ThrowWithRefusedRemoval_RethrowsTheOriginal_AndReportsTheCleanup()
    {
        var logger = new AdoptionCapturingLogger<WorkerPool>();
        var pool = new WorkerPool(logger);
        var queue = new TaskQueue();
        var task = BuildTask("task-residue-throw");
        var original = new InvalidOperationException("original-commit-failure");
        WorkTask? foreign = null;

        pool.BeforeAdoptedWorkerPublicationForTest = _ =>
        {
            foreign = ReplaceActiveEntryWithForeignInstance(queue, task.TaskId);
            throw original;
        };

        var thrown = Assert.Throws<InvalidOperationException>(() => pool.RegisterAdoptedWorker(
            "worker-residue-throw", [], requestCompletionReceiptAck: false, completionReceiptAckEnabled: false,
            task, queue));

        Assert.Same(original, thrown);
        Assert.Null(pool.GetWorker("worker-residue-throw"));
        Assert.Same(foreign, queue.GetActiveTask(task.TaskId));

        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("stale-active-entry", warning.Message, StringComparison.Ordinal);
        Assert.Contains("worker-residue-throw", warning.Message, StringComparison.Ordinal);
        Assert.Contains("removal refused", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CLEAN-CLEANUP CONTROL: with no concurrent writer the same throw unwinds the adopted
    /// registration's own entry and emits NO warning — so the warnings above are the refused
    /// removal's doing, not a blanket emission.
    /// </summary>
    [Fact]
    public void RegisterAdoptedWorker_ThrowWithWorkingRemoval_UnwindsTheEntry_WithoutAWarning()
    {
        var logger = new AdoptionCapturingLogger<WorkerPool>();
        var pool = new WorkerPool(logger);
        var queue = new TaskQueue();
        var task = BuildTask("task-residue-clean");
        var original = new InvalidOperationException("original-commit-failure");

        pool.BeforeAdoptedWorkerPublicationForTest = _ => throw original;

        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => pool.RegisterAdoptedWorker(
            "worker-residue-clean", [], requestCompletionReceiptAck: false, completionReceiptAckEnabled: false,
            task, queue)));

        Assert.Null(queue.GetActiveTask(task.TaskId));
        Assert.Empty(logger.LogEntries);
    }

    // ═══════════════ (a) THE ADOPTED COMPLETION, END TO END THROUGH THE REAL PATHS ═══════════════

    /// <summary>
    /// (a) THE ADOPTED COMPLETION, END TO END: a worker adopts the held attempt through the REAL
    /// <see cref="HiveOrchestratorService.Register"/>, then delivers a REAL <c>WorkStream</c>
    /// <c>TaskComplete</c> — the same read loop, ownership classification, receipt recording and
    /// checked release any ordinary busy worker uses — and the REAL
    /// <see cref="TaskCompletionService"/> ADMITS it and drives the pipeline OFF the restored
    /// Coding phase to its terminal completion. There is no bypass and no second completion route:
    /// the adopted instance is an ordinary busy worker whose completion flows through the existing
    /// transport path.
    /// <para>
    /// THE DRIVE IS REAL THROUGHOUT. The downstream dispatcher is the REAL <see cref="GoalDispatcher"/>,
    /// which owns the real <c>TaskCompletionService</c> and subscribes to the shared
    /// <see cref="TaskCompletionNotifier"/> exactly as production wires it; the completion travels
    /// the real <c>WorkStream</c> read loop, the REAL recorder retains the durable receipt, the
    /// checked release idles the instance and removes the active entry, and the REAL
    /// <see cref="GoalLifecycleService"/> marks the goal Completed in its REAL goal source. The
    /// downstream evidence is a production <c>TaskCompletionService</c> line, so a returned call
    /// proves the real domain chain ran — not merely that a test handler fired.
    /// </para>
    /// <para>
    /// THE ADVANCEMENT IS READ FROM THE PIPELINE, THE SLOT AND THE DURABLE EVIDENCE: with no Brain
    /// the existing no-brain path is the production advancement — the completing slot is admitted
    /// (Pending → Claimed, the A1a in-flight exemption the terminal abandon respects), the IN-MEMORY
    /// phase reaches <see cref="GoalPhase.Done"/>, the goal is marked Completed in its source, and the
    /// completion receipt is durably retained. The persisted PIPELINE ROW is deliberately NOT read:
    /// the no-brain terminal path returns BEFORE the drive's own <c>PersistFull</c>, so the row's
    /// phase is not terminal and is not evidence of this completion.
    /// </para>
    /// <para>
    /// DETERMINISTIC SYNCHRONIZATION, NO SLEEPS: the downstream wait is a
    /// <see cref="TaskCompletionSource"/> completed by the production log line itself, allocated
    /// before the message is pushed; the only bounded await is the 30-second failure bound every
    /// existing transport vector uses, which a healthy run never waits out.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AdoptedWorker_TaskComplete_ReachesTaskCompletionService_AndAdvancesThePipeline()
    {
        var goalId = "adopt-e2e-complete";
        var workerId = "worker-adopted-completes";
        var (_, taskId, _) = SeedHeldAttempt(goalId, workerId);
        var (manager, pipeline) = RestoreHeldAttempt(goalId, taskId);

        // THE COMPLETION HARNESS: the production wiring — the real service, adopter, recorder,
        // publisher, pool, queue, dispatcher subscribed to the shared notifier, and WorkStream.
        var service = await CreateCompletionHarnessAsync(manager, pipeline.Goal);
        var bound = TimeSpan.FromSeconds(30);
        var harness = service.Harness;
        var dispatcherLogger = service.DispatcherLogger;

        Exception? primary = null;
        try
        {
            // THE ADOPTION, through the REAL Register over the REAL adopter: the instance becomes an
            // ordinary busy worker with the active entry and the recorded evidence already in place.
            var response = await RegisterAsync(harness, workerId, taskId);

            Assert.True(response.Accepted);
            Assert.True(response.AdoptedTask);
            var worker = Assert.IsType<ConnectedWorker>(harness.Pool.GetWorker(workerId));
            Assert.True(worker.IsBusy);
            Assert.Equal(taskId, worker.CurrentTaskId);
            Assert.Equal(HeldModel, worker.CurrentModel);
            Assert.NotNull(harness.Queue.GetActiveTask(taskId));
            Assert.False(pipeline.IsRestoredActiveAttemptHold, "the hold must leave before any completion");

            // THE COMPLETION IS DELIVERED ON THE WORKER'S OWN STREAM — the same path any assignment
            // completion travels: the read loop pins the worker on this first message, classifies
            // the delivery, validates ownership, records the receipt and releases the instance. The
            // downstream signal is the REAL lifecycle service's own terminal line ("Goal {GoalId}
            // completed"), which the no-brain completion path reaches only AFTER the admission, the
            // pointer release, the phase advance to Done and the goal-status write — so a returned
            // call proves the whole real chain ran and SETTLED.
            var downstream = dispatcherLogger.WaitFor("completed in");

            service.Reader.Push(new CopilotHive.Shared.Grpc.WorkerMessage
            {
                WorkerId = workerId,
                Complete = new CopilotHive.Shared.Grpc.TaskComplete
                {
                    TaskId = taskId,
                    Status = CopilotHive.Shared.Grpc.TaskStatus.Completed,
                    Output = "adopted-completion-output",
                    Model = HeldModel,

                    // FILE CHANGES: a Coder completion reporting none is the production no-op
                    // path (a stronger-prompt retry instead of an advance), so the completing
                    // delivery carries real change counts — exactly what a working coder reports.
                    GitStatus = new CopilotHive.Shared.Grpc.GitStatus
                    {
                        FilesChanged = 3,
                        Insertions = 10,
                        Deletions = 2,
                        Pushed = true,
                        CurrentBranch = "copilothive/" + goalId,
                    },
                },
            });

            await downstream.WaitAsync(bound, TestContext.Current.CancellationToken);

            // ── THE ADOPTED COMPLETION WAS ADMITTED AND THE PIPELINE ADVANCED TO ITS TERMINAL. ────
            // With no Brain the existing no-brain path is the production advancement: the completing
            // slot is admitted (Pending → Claimed), the phase reaches Done, and the goal row is
            // marked Completed in its source. (The state machine is NOT part of this terminal path:
            // MarkGoalCompletedAsync advances the pipeline phase directly.)
            Assert.Equal(GoalPhase.Done, pipeline.Phase);

            // THE ADMITTED SLOT STAYS CLAIMED — the A1a in-flight exemption the terminal abandon
            // leaves it with (never Recorded on the no-brain path, never Abandoned).
            Assert.Equal(WorkSlotState.Claimed, service.SlotState(taskId));

            // The completion release: the instance is idle again with no task, and the active entry
            // is gone — the EXISTING transport behavior, unchanged by the adoption.
            Assert.False(worker.IsBusy);
            Assert.Null(worker.CurrentTaskId);
            Assert.Null(harness.Queue.GetActiveTask(taskId));

            // THE REAL DOWNSTREAM CHAIN RAN — the production admitted-completion line naming the
            // goal, plus the terminal line the wait observed, not a counter.
            Assert.Contains(
                dispatcherLogger.Messages,
                m => m.Contains("task completed", StringComparison.Ordinal) &&
                     m.Contains(goalId, StringComparison.Ordinal));
            Assert.Contains(
                dispatcherLogger.Messages,
                m => m.Contains("completed in", StringComparison.Ordinal) &&
                     m.Contains(goalId, StringComparison.Ordinal));

            // THE GOAL WAS MARKED COMPLETED in its source — the lifecycle service's own status write,
            // through the REAL GoalManager over the REAL goal source. That is the durable evidence
            // the no-brain terminal path persists: it returns BEFORE the drive's own PersistFull,
            // so the pipeline row's phase is deliberately left as it was.
            Assert.Equal(GoalStatus.Completed, pipeline.Goal.Status);

            // THE ADOPTED COMPLETION'S RECEIPT IS DURABLE: the real recorder retained the exact
            // attempt — the recorded slot, the adopting worker and the mapped result — read back
            // through a FRESH store instance over the same database, so the evidence survives the
            // completion and names the adoption.
            var receipt = new CompletionReceiptStore(
                new SharedCacheContextFactory(_connectionString),
                NullLogger<CompletionReceiptStore>.Instance).Load(taskId);
            Assert.NotNull(receipt);
            Assert.Equal(goalId, receipt!.Receipt.GoalId);
            Assert.Equal(workerId, receipt.Receipt.WorkerId);
            Assert.Equal(WorkerRole.Coder, receipt.Receipt.Role);
            Assert.Equal(taskId, receipt.Receipt.Slot.TaskId);
            Assert.Equal(taskId, receipt.Receipt.Result.TaskId);
            Assert.Equal(TaskOutcome.Completed, receipt.Receipt.Result.Status);

            // NO HOLD FENCE REFUSAL: the completion was never dropped by the held-attempt fence.
            Assert.DoesNotContain(
                dispatcherLogger.Messages,
                m => m.Contains("completion-refused", StringComparison.Ordinal));
        }
        catch (Exception failure)
        {
            primary = failure;
        }
        finally
        {
            // THE STRICT TEARDOWN, ON EVERY PATH: the producer is joined under the bound. A leaked
            // or faulted stream is reported, and the PRIMARY failure stays authoritative.
            service.Reader.Complete();
            Exception? cleanup = null;
            try
            {
                await service.StreamTask.WaitAsync(bound, CancellationToken.None);
            }
            catch (Exception teardown) when (teardown is not OperationCanceledException
                                             and not TaskCanceledException)
            {
                cleanup = teardown;
            }

            if (primary is not null)
                throw primary;

            if (cleanup is not null)
                throw new InvalidOperationException(
                    "THE WORKSTREAM FAILED TO DRAIN CLEANLY on teardown — a live producer remains " +
                    "or the stream terminated with a fault.", cleanup);
        }
    }
}

/// <summary>
/// THE END-TO-END COMPLETION HARNESS for the adopted-completion vector: the REAL
/// <see cref="HiveOrchestratorService"/> with the REAL adopter, recorder and assignment publisher
/// wired in, over the REAL <see cref="WorkerPool"/>, <see cref="TaskQueue"/>, SQLite assignment store
/// and receipt store — and the REAL <see cref="GoalDispatcher"/> subscribed to the shared
/// <see cref="TaskCompletionNotifier"/>, so a published completion genuinely reaches the real
/// <see cref="TaskCompletionService"/>.
/// </summary>
/// <param name="Harness">The register-level transport collaborators (service, pool, queue, logger, adopter).</param>
/// <param name="DispatcherLogger">The REAL dispatcher's signalling logger — the source of the production completion lines.</param>
/// <param name="Manager">The manager shared by the service, the dispatcher and the restored pipeline.</param>
/// <param name="Reader">The real WorkStream's request stream the vector pushes messages onto.</param>
/// <param name="Writer">The response writer the real pump forwards to — the publication ledger.</param>
/// <param name="StreamTask">The retained WorkStream producer; the teardown joins exactly this task.</param>
internal sealed record AdoptedCompletionHarness(
    RestoredAttemptAdoptionTests.RegisterHarness Harness,
    RestoredAttemptAdoptionTests.SignallingLogger<GoalDispatcher> DispatcherLogger,
    GoalPipelineManager Manager,
    RestoredAttemptAdoptionTests.CompletionStreamReader Reader,
    RestoredAttemptAdoptionTests.SignallingStreamWriter Writer,
    Task StreamTask) : IAsyncDisposable
{
    /// <summary>The bounded wait every transport await carries; a hang is a named failure.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The state of a task's slot in its pipeline's own registry, read through
    /// <see cref="GoalPipeline.GetSlotsForTest"/>.
    /// </summary>
    public WorkSlotState SlotState(string taskId)
    {
        var pipeline = Manager.GetByTaskId(taskId)
            ?? throw new InvalidOperationException($"no pipeline for task '{taskId}'");
        return pipeline.GetSlotsForTest().Single(v => v.Slot.TaskId == taskId).State;
    }

    /// <summary>
    /// THE STRICT TEARDOWN: the request stream is completed and the retained producer is joined under
    /// the bound, on EVERY path — a leaked or faulted WorkStream is a test failure, never a leak.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Reader.Complete();

        try
        {
            await StreamTask.WaitAsync(Bound, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"TEARDOWN LEAK: the adopted worker's WorkStream did not terminate within " +
                $"{Bound.TotalSeconds:F0}s — a live producer remains.");
        }
        catch (Exception ex) when (StreamTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "THE WORKSTREAM TERMINATED WITH A FAULT. A clean vector must leave the transport " +
                "draining normally; a fault here means a handler escaped instead of returning.", ex);
        }
    }
}
