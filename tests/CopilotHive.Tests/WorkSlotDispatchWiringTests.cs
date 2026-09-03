using System.Collections.Concurrent;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System.Reflection;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// The work-slot ADMISSION TRANSACTION (slice A2''): the wiring of
/// <see cref="GoalPipeline.CaptureDispatchPosition"/>,
/// <see cref="GoalPipelineManager.TryRegisterTask"/>,
/// <see cref="GoalPipelineManager.TryUnregisterTask"/> and
/// <see cref="GoalPipeline.ClearActiveTaskIfCurrent"/> onto
/// <see cref="TaskDispatchService.DispatchToRole"/>, plus the cleanup's slot abandonment.
/// </summary>
/// <remarks>
/// <para>
/// THE SEVEN TEMPLATES are asserted VERBATIM (rendered message text, at their declared level):
/// the five capture refusals in <see cref="Dispatch_CaptureRefusal_LogsExactTemplateAndPropagates"/>,
/// <c>abandoned-registration</c> in every failure vector, and <c>rollback-failure</c> for both
/// step values a test can reach (<c>unregister</c>, <c>unregister-persist</c>). The
/// <c>abandon</c>/<c>pointer</c> steps are belt-and-braces catches over sealed, non-virtual code
/// with no feasible failure vector — they are a CODE-STRUCTURE requirement verified by
/// inspection, deliberately without a test vector.
/// </para>
/// <para>
/// Persisted vectors run against a per-instance shared-cache in-memory SQLite database anchored
/// by a keeper connection, exactly like <see cref="WorkSlotMappingOwnershipTests"/>. No test uses
/// <c>Task.Delay</c>: every race is arranged deterministically through the synchronous
/// <see cref="TaskQueue.OnEnqueue"/> seam.
/// </para>
/// </remarks>
public sealed class WorkSlotDispatchWiringTests : IDisposable
{
    private readonly string _connectionString =
        $"Data Source=file:memdb-workslotdispatch-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<SqliteConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];

    public WorkSlotDispatchWiringTests()
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

    private CopilotHiveDbContext CreateContext(IInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        _connections.Add(connection);

        var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        var context = new CopilotHiveDbContext(builder.Options);
        _contexts.Add(context);
        return context;
    }

    private PipelineStore CreateStore(IInterceptor? interceptor = null) =>
        new(CreateContext(interceptor), NullLogger<PipelineStore>.Instance);

    /// <summary>Reads the persisted goal id for a task RAW — no EF Core, no change tracker.</summary>
    private string? ReadPersistedGoalId(string taskId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "SELECT goal_id FROM task_mappings WHERE task_id = $taskId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$taskId";
        parameter.Value = taskId;
        command.Parameters.Add(parameter);
        return command.ExecuteScalar() as string;
    }

    private const string GoalId = "goal-wiring";

    private static HiveConfigFile CreateConfig()
    {
        var config = new HiveConfigFile();
        config.Repositories.Add(new RepositoryConfig
        {
            Name = "test-repo",
            Url = "https://example.com/test-repo.git",
            DefaultBranch = "develop",
        });
        foreach (var roleName in new[] { "coder", "tester", "docwriter", "reviewer", "improver" })
            config.Workers[roleName] = new WorkerConfig { Model = $"{roleName}-model" };
        return config;
    }

    private static Goal CreateGoal(string goalId, bool withRepository = true) => new()
    {
        Id = goalId,
        Description = "Wiring goal",
        RepositoryNames = withRepository ? ["test-repo"] : [],
    };

    /// <summary>
    /// Installs the full plan and puts BOTH the pipeline and the state machine on
    /// <paramref name="phase"/>, the only coherent arrangement a real dispatch ever sees.
    /// </summary>
    private static void Arrange(GoalPipeline pipeline, GoalPhase phase)
    {
        var plan = IterationPlan.Default(includeImprove: true);
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, phase);
        pipeline.AdvanceTo(phase);
    }

    private static TaskDispatchService CreateService(
        GoalPipelineManager pipelineManager,
        TaskQueue taskQueue,
        ILogger<TaskDispatchService> logger,
        IWorkerGateway? workerGateway = null,
        HiveConfigFile? config = null,
        Goal? goal = null)
    {
        config ??= CreateConfig();
        workerGateway ??= new GrpcWorkerGateway(new WorkerPool());

        var goalManager = new GoalManager();
        goalManager.AddSource(new WiringGoalSource(goal ?? CreateGoal("setup-goal")));
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var lifecycleService = new GoalLifecycleService(goalManager, logger);
        var maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway,
            brain: null, agentsManager: null, configRepo: null,
            new ConcurrentQueue<string>(), logger, config: config);

        return new TaskDispatchService(
            taskQueue, workerGateway, new TaskBuilder(new BranchCoordinator()), config,
            logger, pipelineManager, lifecycleService, maintenance);
    }

    private static string ExpectedTaskId(string goalId, WorkerRole role, int iteration = 1, int occurrence = 1, int attempt = 1) =>
        $"{goalId}-{role.ToRoleName()}-{iteration:D3}-{occurrence:D2}-{attempt:D3}";

    private static string AbandonedRegistrationMessage(string goalId, string taskId, int iteration, GoalPhase phase, int occurrence) =>
        $"WorkSlotIntegrity: abandoned-registration goal={goalId} task={taskId} " +
        $"position={iteration}:{phase}:{occurrence} — the dispatch failed before delivery; the slot is released";

    private static string RollbackFailureMessage(string goalId, string taskId, string step) =>
        $"WorkSlotIntegrity: rollback-failure goal={goalId} task={taskId} step={step} — the rollback step failed; continuing";

    private static IReadOnlyList<string> Warnings(TestLogger<TaskDispatchService> logger) =>
        [.. logger.LogEntries.Where(e => e.LogLevel == LogLevel.Warning).Select(e => e.Message)];

    private static WorkSlotView SingleSlot(GoalPipeline pipeline) => Assert.Single(pipeline.GetSlotsForTest());

    /// <summary>
    /// An order-independent, value-comparable snapshot of the whole slot registry. Records give
    /// value equality, so comparing two snapshots detects ANY added, removed or re-stated slot.
    /// </summary>
    private static HashSet<WorkSlotView> SlotSnapshot(GoalPipeline pipeline) =>
        [.. pipeline.GetSlotsForTest()];

    /// <summary>Reads every persisted task id RAW — no EF Core, no change tracker.</summary>
    private IReadOnlyList<string> ReadAllPersistedTaskIds()
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "SELECT task_id FROM task_mappings";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (1) The happy path
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// capture → build (verbatim, attempt-stamped task ID) → TryRegisterTask → SetActiveTask →
    /// enqueue. Nothing is abandoned and no WorkSlotIntegrity warning is emitted.
    /// </summary>
    /// <remarks>
    /// THE ORDERING PROOF lives in the enqueue callback, not in the post-hoc assertions: the
    /// callback runs synchronously INSIDE <see cref="TaskQueue.Enqueue"/>, so whatever it observes
    /// was already true when Enqueue was ENTERED. Capturing the slot state, the mapping owner and
    /// the active pointer there pins the pre-enqueue ordering — moving <c>SetActiveTask</c> (or the
    /// registration, or the capture) to AFTER the enqueue makes the corresponding entry observation
    /// null and fails this test.
    /// </remarks>
    [Fact]
    public async Task Dispatch_HappyPath_CapturesRegistersPointsAndEnqueues()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        var expectedTaskId = ExpectedTaskId(GoalId, WorkerRole.Coder);

        WorkTask? enqueued = null;
        // Observations taken AT CALLBACK ENTRY — i.e. at the instant Enqueue was entered.
        IReadOnlyList<WorkSlotView> slotsAtEntry = [];
        string? mappedGoalAtEntry = null;
        string? persistedGoalAtEntry = null;
        string? pointerAtEntry = null;

        queue.OnEnqueue = t =>
        {
            enqueued = t;
            slotsAtEntry = pipeline.GetSlotsForTest();
            mappedGoalAtEntry = manager.GetByTaskId(t.TaskId)?.GoalId;
            persistedGoalAtEntry = ReadPersistedGoalId(t.TaskId);
            pointerAtEntry = pipeline.ActiveTaskId;
        };

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(enqueued);
        Assert.Equal(expectedTaskId, enqueued!.TaskId);

        // (1) THE CAPTURE had already allocated the live slot before Enqueue was entered.
        var slotAtEntry = Assert.Single(slotsAtEntry);
        Assert.Equal(expectedTaskId, slotAtEntry.Slot.TaskId);
        Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), slotAtEntry.Slot.Position);
        Assert.Equal(1, slotAtEntry.Slot.Attempt);
        Assert.Equal(WorkSlotState.Pending, slotAtEntry.State);
        // (3) THE MAPPING was already ours, in memory AND in the store.
        Assert.Equal(GoalId, mappedGoalAtEntry);
        Assert.Equal(GoalId, persistedGoalAtEntry);
        // (4) THE POINTER already named this task — SetActiveTask precedes the enqueue.
        Assert.Equal(expectedTaskId, pointerAtEntry);

        // And the admission still stands after a successful dispatch.
        var slot = SingleSlot(pipeline);
        Assert.Equal(expectedTaskId, slot.Slot.TaskId);
        Assert.Equal(WorkSlotState.Pending, slot.State);
        Assert.Same(pipeline, manager.GetByTaskId(expectedTaskId));
        Assert.Equal(GoalId, ReadPersistedGoalId(expectedTaskId));
        Assert.Equal(expectedTaskId, pipeline.ActiveTaskId);

        Assert.DoesNotContain(Warnings(logger), m => m.Contains("WorkSlotIntegrity", StringComparison.Ordinal));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2 + 11a) The five capture refusals and their EXACT templates
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Each of the five refusals logs its matching template VERBATIM at WARNING and propagates the
    /// <c>WorkSlotException</c>. Nothing is registered, pointed at, enqueued or delivered.
    /// </summary>
    /// <remarks>
    /// THE REMOVAL/STATE PROOF per row: the registry is snapshotted BEFORE the dispatch and must be
    /// byte-identical AFTER it (no slot allocated for the refused position, no existing slot's state
    /// touched), and the task ID the capture WOULD have built must be absent from both the manager's
    /// memory and the persisted <c>task_mappings</c> row set. A mutant that allocates a slot or
    /// inserts a mapping before the refusal check is therefore detected.
    /// </remarks>
    [Theory]
    [InlineData("double-assignment")]
    [InlineData("role-mismatch")]
    [InlineData("invalid-phase")]
    [InlineData("phase-divergence")]
    [InlineData("plan-unavailable")]
    public async Task Dispatch_CaptureRefusal_LogsExactTemplateAndPropagates(string refusal)
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        WorkerRole role;
        string expected;
        switch (refusal)
        {
            case "double-assignment":
                Arrange(pipeline, GoalPhase.Coding);
                Assert.True(pipeline.SeedSlotForTest(
                    "occupant", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1, WorkSlotState.Pending));
                role = WorkerRole.Coder;
                expected =
                    $"WorkSlotIntegrity: double-assignment goal={GoalId} position=1:Coding:1 existing=occupant — the dispatch is refused";
                break;

            case "role-mismatch":
                Arrange(pipeline, GoalPhase.Coding);
                role = WorkerRole.Tester;
                expected =
                    $"WorkSlotIntegrity: role-mismatch goal={GoalId} position=1:Coding:1 passed=Tester derived=Coder — the dispatch is refused";
                break;

            case "invalid-phase":
                Arrange(pipeline, GoalPhase.Merging);
                role = WorkerRole.Coder;
                expected =
                    $"WorkSlotIntegrity: invalid-phase goal={GoalId} position=1:Merging:1 machine-phase=Merging — the dispatch is refused";
                break;

            case "phase-divergence":
                Arrange(pipeline, GoalPhase.Coding);
                // The pipeline moves on; the machine stays on Coding.
                pipeline.AdvanceTo(GoalPhase.Testing);
                role = WorkerRole.Coder;
                expected =
                    $"WorkSlotIntegrity: phase-divergence goal={GoalId} position=1:Coding:1 pipeline-phase=Testing machine-phase=Coding — the dispatch is refused";
                break;

            case "plan-unavailable":
                // No plan installed: the machine agrees on the worker phase but the capture has
                // no phase list to derive the occurrence from.
                pipeline.StateMachine.RestoreFromPlan([], GoalPhase.Coding);
                pipeline.AdvanceTo(GoalPhase.Coding);
                role = WorkerRole.Coder;
                expected =
                    $"WorkSlotIntegrity: plan-unavailable goal={GoalId} position=1:Coding:0 machine-phase=Coding — the dispatch is refused";
                break;

            default:
                throw new InvalidOperationException($"Unhandled refusal vector: {refusal}");
        }

        WorkTask? enqueued = null;
        queue.OnEnqueue = t => enqueued = t;

        // The task ID the capture WOULD have built for the first attempt at this position.
        var wouldBeTaskId = ExpectedTaskId(GoalId, role);
        // BEFORE: the registry snapshot the refusal must leave untouched.
        var slotsBefore = SlotSnapshot(pipeline);

        await Assert.ThrowsAsync<WorkSlotException>(
            () => service.DispatchToRole(pipeline, role, "Do it", TestContext.Current.CancellationToken));

        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Warning && e.Message == expected);

        // AFTER: the registry is EXACTLY as before — no slot allocated for the refused position,
        // and no pre-existing slot's state disturbed.
        Assert.Equal(slotsBefore, SlotSnapshot(pipeline));
        Assert.DoesNotContain(pipeline.GetSlotsForTest(), s => s.Slot.TaskId == wouldBeTaskId);

        // No mapping was claimed — neither in the manager's memory nor in the store.
        Assert.Null(manager.GetByTaskId(wouldBeTaskId));
        Assert.Null(ReadPersistedGoalId(wouldBeTaskId));
        Assert.Empty(ReadAllPersistedTaskIds());

        // The refusal admits nothing: no pointer, no queue entry, no delivery.
        Assert.Null(enqueued);
        Assert.Null(queue.TryDequeueAny());
        Assert.Null(pipeline.ActiveTaskId);
        Assert.DoesNotContain(
            Warnings(logger), m => m.Contains("abandoned-registration", StringComparison.Ordinal));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3 + 11b) The build failure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A build failure releases the captured slot, logs the exact
    /// <c>abandoned-registration</c> template and propagates the ORIGINAL failure — never a wrapper.
    /// </summary>
    [Fact]
    public async Task Dispatch_BuildFails_AbandonsSlotLogsTemplateAndPropagatesOriginal()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        // A goal with NO repositories resolves to an empty repository list, which TaskBuilder
        // refuses — the first failure vector AFTER the capture.
        var goal = CreateGoal(GoalId, withRepository: false);
        var pipeline = manager.CreatePipeline(goal);
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger, goal: goal);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // The ORIGINAL TaskBuilder failure, not the registration wrapper.
        Assert.Contains("No repositories configured", ex.Message, StringComparison.Ordinal);

        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(queue.TryDequeueAny());

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == AbandonedRegistrationMessage(GoalId, taskId, 1, GoalPhase.Coding, 1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) The registration failure's two causes — the INNER distinction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A DUPLICATE mapping refuses with a NULL inner exception: nothing was carried, because no
    /// store call ever threw.
    /// </summary>
    [Fact]
    public async Task Dispatch_RegistrationDuplicate_ThrowsExactMessageWithNullInner()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);
        // Another goal already owns the mapping the capture is about to claim.
        manager.CreatePipeline(CreateGoal("goal-other"));
        manager.RegisterTask(taskId, "goal-other");

        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Task mapping registration failed for {taskId} (goal {GoalId}) — the mapping is occupied or the persistence failed",
            ex.Message);
        Assert.Null(ex.InnerException);

        // The slot is released; the competing mapping is left INTACT.
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Equal("goal-other", ReadPersistedGoalId(taskId));
        Assert.Null(queue.TryDequeueAny());
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == AbandonedRegistrationMessage(GoalId, taskId, 1, GoalPhase.Coding, 1));
    }

    /// <summary>
    /// A PERSISTENCE failure refuses with the SAME message but CARRIES the store's exception as
    /// the inner — so the two causes stay distinguishable at the exception level.
    /// </summary>
    [Fact]
    public async Task Dispatch_RegistrationPersistenceFails_ThrowsExactMessageCarryingStoreException()
    {
        var sentinel = new InvalidOperationException("register-store-sentinel");
        var manager = new GoalPipelineManager(
            CreateStore(new SentinelThrowingInterceptor(sentinel, "INSERT")),
            new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);
        Assert.Equal(
            $"Task mapping registration failed for {taskId} (goal {GoalId}) — the mapping is occupied or the persistence failed",
            ex.Message);
        Assert.Same(sentinel, ex.InnerException);

        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(ReadPersistedGoalId(taskId));
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == AbandonedRegistrationMessage(GoalId, taskId, 1, GoalPhase.Coding, 1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) The enqueue rollback — the complete undo
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An enqueue-span failure undoes the slot, the mapping AND the pointer, then rethrows the
    /// ORIGINAL exception instance.
    /// </summary>
    /// <remarks>
    /// THE ORPHAN EDGE (accepted trade). <see cref="TaskQueue.Enqueue"/> inserts BEFORE it invokes
    /// its callback, so the task stays admitted to the pending queue while the rollback removes its
    /// mapping. The later assignment then finds no pipeline and hits the existing no-pipeline
    /// drop — an orphaned queue entry, never a double-assigned slot. The final assertions pin
    /// exactly that shape.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EnqueueThrows_RollsBackSlotMappingPointerAndRethrowsOriginal()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue { OnEnqueue = _ => throw sentinel };
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // Exception IDENTITY: the original instance, never a wrapper.
        Assert.Same(sentinel, thrown);

        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(ReadPersistedGoalId(taskId));
        Assert.Null(pipeline.ActiveTaskId);

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == AbandonedRegistrationMessage(GoalId, taskId, 1, GoalPhase.Coding, 1));
        // A clean rollback logs NO rollback-failure at any step.
        Assert.DoesNotContain(Warnings(logger), m => m.Contains("rollback-failure", StringComparison.Ordinal));

        // THE ORPHAN EDGE, covered: the task is still queued, but owns no mapping.
        var orphan = queue.TryDequeueAny();
        Assert.NotNull(orphan);
        Assert.Equal(taskId, orphan!.TaskId);
        Assert.Null(manager.GetByTaskId(orphan.TaskId));
    }

    /// <summary>
    /// THE ROLLBACK STEP ORDER — (a) before (b) before (c): the slot is abandoned FIRST, then the
    /// mapping is unregistered, and only then is the pointer cleared.
    /// </summary>
    /// <remarks>
    /// All three steps touch DISJOINT state (the slot registry, the manager's mapping, the
    /// pipeline's pointer), so no final state distinguishes their order. The discriminator is
    /// TEMPORAL: step (b) emits a DEBUG unregister-result record, and that single event sits
    /// BETWEEN (a) and (c) — so what the state looks like AT that instant pins both boundaries.
    /// <list type="bullet">
    ///   <item><description>
    ///     THE a/b BOUNDARY: (a) has already run, so the slot must read <c>Abandoned</c> — no
    ///     longer live. Swapping (a) and (b) leaves it <c>Pending</c> at that instant.
    ///   </description></item>
    ///   <item><description>
    ///     THE b/c BOUNDARY: (c) has NOT run yet, so the pointer must still be ours. Swapping
    ///     (b) and (c) makes the probe observe a null pointer.
    ///   </description></item>
    /// </list>
    /// Both probes read the REAL pipeline through A1a/A1b's registry test seam and the live
    /// <see cref="GoalPipeline.ActiveTaskId"/> — the same instance production is mutating, never a
    /// recorded copy or a stand-in. The closing <c>abandoned-registration</c> record then confirms
    /// the post-(c) state, so the c/d boundary is pinned too. No production seam is required.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EnqueueThrows_AbandonPrecedesUnregisterPrecedesPointerClear()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue { OnEnqueue = _ => throw sentinel };

        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);
        // THE PROBES read production state live at every log event: the pipeline's own pointer and
        // the slot's state straight out of the registry seam (null until the capture allocates it).
        var logger = new RollbackProbingLogger<TaskDispatchService>(
            () => pipeline.ActiveTaskId,
            () => pipeline.GetSlotsForTest().SingleOrDefault(s => s.Slot.TaskId == taskId)?.State);
        var service = CreateService(manager, queue, logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        var unregisterRecord = Assert.Single(
            logger.Entries,
            e => e.Message == $"WorkSlotIntegrity: unregister goal={GoalId} task={taskId} memoryRemoved=True persistenceRemoved=True");

        // THE a/b BOUNDARY: (a) AbandonSlot had already freed the slot when (b) logged its result.
        // Under the a/b swap the slot is still Pending here — that is the kill.
        Assert.Equal(WorkSlotState.Abandoned, unregisterRecord.SlotStateAtLog);
        // THE b/c BOUNDARY: (c) had NOT run yet — the pointer was still ours.
        Assert.Equal(taskId, unregisterRecord.PointerAtLog);

        // AFTER (c): the closing record sees the cleared pointer and the still-abandoned slot.
        var closingRecord = Assert.Single(
            logger.Entries,
            e => e.Message == AbandonedRegistrationMessage(GoalId, taskId, 1, GoalPhase.Coding, 1));
        Assert.Null(closingRecord.PointerAtLog);
        Assert.Equal(WorkSlotState.Abandoned, closingRecord.SlotStateAtLog);

        // The settled end state, for completeness.
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(manager.GetByTaskId(taskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (6 + 11c) The unregister result predicate — its two cases
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE RACED OWNERSHIP: the mapping is re-pointed at another goal AFTER our successful
    /// registration and BEFORE the enqueue failure, so the pair-based remove takes nothing —
    /// <c>(false, false)</c>. That removed nothing OF OURS, so it is a DEBUG record and NOT a
    /// rollback-failure warning.
    /// </summary>
    /// <remarks>
    /// The callback captures the mapping owner and the pointer AT ENTRY — before it overwrites
    /// anything — so the assertions below PROVE the corrected setup: the registration had already
    /// succeeded for OUR goal when Enqueue was entered, and only then did the race steal it. A
    /// race arranged before the registration would leave the entry owner unequal to our goal and
    /// fail this test.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EnqueueThrowsAfterOwnershipRace_LogsDebugOnlyWithoutRollbackWarning()
    {
        var manager = new GoalPipelineManager(store: null, new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        var other = manager.CreatePipeline(CreateGoal("goal-other"));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        string? mappedGoalAtEntry = null;
        string? pointerAtEntry = null;
        WorkSlotState? slotStateAtEntry = null;

        // The race happens strictly BETWEEN the successful registration and the enqueue failure.
        queue.OnEnqueue = task =>
        {
            // (i) Observe FIRST: the admission must already be complete at Enqueue entry.
            mappedGoalAtEntry = manager.GetByTaskId(task.TaskId)?.GoalId;
            pointerAtEntry = pipeline.ActiveTaskId;
            slotStateAtEntry = pipeline.GetSlotsForTest()
                .SingleOrDefault(s => s.Slot.TaskId == task.TaskId)?.State;

            // (ii) Only THEN does the competitor steal the mapping, and the enqueue fails.
            manager.RegisterTask(task.TaskId, "goal-other");
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);

        // THE SETUP PROOF: OUR registration had already succeeded when the enqueue was entered.
        Assert.Equal(GoalId, mappedGoalAtEntry);
        Assert.Equal(taskId, pointerAtEntry);
        Assert.Equal(WorkSlotState.Pending, slotStateAtEntry);

        // (false, false): the winner's mapping survives untouched.
        Assert.Same(other, manager.GetByTaskId(taskId));
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Debug &&
            e.Message == $"WorkSlotIntegrity: unregister goal={GoalId} task={taskId} memoryRemoved=False persistenceRemoved=False");
        Assert.DoesNotContain(Warnings(logger), m => m.Contains("rollback-failure", StringComparison.Ordinal));

        // The rest of the rollback still ran.
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// THE PARTIAL REMOVAL: our memory ownership IS removed but the conditional row delete throws,
    /// so the result is <c>(true, false)</c> — the <c>step=unregister-persist</c> warning, rendered
    /// verbatim, and the persisted residue is left behind honestly.
    /// </summary>
    [Fact]
    public async Task Dispatch_EnqueueThrowsAndRowDeleteFails_LogsUnregisterPersistWarning()
    {
        var deleteSentinel = new InvalidOperationException("delete-sentinel");
        var manager = new GoalPipelineManager(
            CreateStore(new SentinelThrowingInterceptor(deleteSentinel, "DELETE")),
            new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var enqueueSentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue { OnEnqueue = _ => throw enqueueSentinel };
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(enqueueSentinel, thrown);

        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Debug &&
            e.Message == $"WorkSlotIntegrity: unregister goal={GoalId} task={taskId} memoryRemoved=True persistenceRemoved=False");
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == RollbackFailureMessage(GoalId, taskId, "unregister-persist"));

        // Memory removed, persisted residue reported honestly rather than silently dropped.
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Equal(GoalId, ReadPersistedGoalId(taskId));
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(pipeline.ActiveTaskId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (7 + 11d) The unregister contract violation — the belt-and-braces catch
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="GoalPipelineManager.TryUnregisterTask"/> promises never to throw; an INJECTED
    /// THROWING LOGGER is the one feasible way to violate that. The <c>step=unregister</c> catch
    /// fires, the rollback continues, and the ORIGINAL enqueue exception is still thrown.
    /// </summary>
    [Fact]
    public async Task Dispatch_UnregisterThrows_LogsUnregisterStepAndStillRethrowsOriginal()
    {
        // A throwing logger: the success path of TryRegisterTask with a null store never logs,
        // so only the rollback's TryUnregisterTask trips it.
        var manager = new GoalPipelineManager(store: null, new ThrowingLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        manager.CreatePipeline(CreateGoal("goal-other"));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        // The race makes the manager take its DEBUG-logging branch, where the injected logger throws.
        queue.OnEnqueue = task =>
        {
            manager.RegisterTask(task.TaskId, "goal-other");
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        Assert.Same(sentinel, thrown);

        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == RollbackFailureMessage(GoalId, taskId, "unregister") &&
            e.Exception is not null);

        // The rollback CONTINUED past the swallowed throw.
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == AbandonedRegistrationMessage(GoalId, taskId, 1, GoalPhase.Coding, 1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (8) The conditional pointer clear
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A NEWER pointer, set after this task's registration, is never erased by the rollback —
    /// the clear is ownership-checked, so a live dispatch cannot be made to look idle.
    /// </summary>
    [Fact]
    public async Task Dispatch_EnqueueThrowsAfterNewerPointer_LeavesNewerPointerIntact()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        queue.OnEnqueue = _ =>
        {
            pipeline.SetActiveTask("newer-task");
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        // The newer pointer survives; our own slot and mapping are still released.
        Assert.Equal("newer-task", pipeline.ActiveTaskId);
        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(manager.GetByTaskId(taskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (9) The catch boundary
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A failure in the DIRECT-PUSH path lies OUTSIDE the enqueue catch (the delivery transaction
    /// owns that path), so nothing is abandoned and no rollback warning is emitted.
    /// </summary>
    [Fact]
    public async Task Dispatch_DirectPushFails_PerformsNoRollback()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("direct-push-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger, workerGateway: new ThrowingIdleWorkerGateway(sentinel));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);
        // The admission stands: slot live, mapping ours, pointer set.
        Assert.Equal(WorkSlotState.Pending, SingleSlot(pipeline).State);
        Assert.Same(pipeline, manager.GetByTaskId(taskId));
        Assert.Equal(taskId, pipeline.ActiveTaskId);
        Assert.DoesNotContain(
            Warnings(logger), m => m.Contains("abandoned-registration", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Warnings(logger), m => m.Contains("rollback-failure", StringComparison.Ordinal));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (10) The cleanup's abandonment — both branches + the DEAD rule
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// BOTH cleanup branches abandon the slot: the task still in the active queue (re-enqueued —
    /// the interim behaviour, unchanged) and the task already gone from it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cleanup_AbandonsSlotInBothBranches(bool taskStillActive)
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var service = CreateService(manager, queue, new TestLogger<TaskDispatchService>());
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        var taskId = ExpectedTaskId(GoalId, WorkerRole.Coder);
        var dispatched = queue.TryDequeueAny();
        Assert.NotNull(dispatched);
        if (taskStillActive)
            queue.Activate(dispatched!, "worker-dead");

        var cleanup = CreateCleanup(manager, queue, taskId);
        await cleanup.RunCleanupCycleAsync();

        // The slot is DEAD in both branches, and the pointer is cleared.
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(pipeline.ActiveTaskId);

        // The re-enqueue interim behaviour: preserved exactly, only on the active-task branch.
        var requeued = queue.TryDequeueAny();
        if (taskStillActive)
        {
            Assert.NotNull(requeued);
            Assert.Equal(taskId, requeued!.TaskId);
        }
        else
        {
            Assert.Null(requeued);
        }
    }

    /// <summary>
    /// THE DEAD RULE, end to end: after <c>RescheduleAbandonedTask</c> the position is free again,
    /// so the redispatch's FRESH capture SUCCEEDS (with the next attempt number) instead of being
    /// refused as a double assignment.
    /// </summary>
    [Fact]
    public async Task Cleanup_ThenRedispatch_FreshCaptureSucceeds()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var service = CreateService(manager, queue, new TestLogger<TaskDispatchService>());
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        var firstTaskId = ExpectedTaskId(GoalId, WorkerRole.Coder, attempt: 1);
        var dispatched = queue.TryDequeueAny();
        Assert.NotNull(dispatched);
        queue.Activate(dispatched!, "worker-dead");

        // Without the cleanup's abandonment the live slot would refuse the next capture.
        var cleanup = CreateCleanup(manager, queue, firstTaskId);
        await cleanup.RunCleanupCycleAsync();

        var second = pipeline.CaptureDispatchPosition(WorkerRole.Coder);

        Assert.Equal(ExpectedTaskId(GoalId, WorkerRole.Coder, attempt: 2), second.TaskId);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), second.Position);
        // The first slot stayed dead; the new one is live.
        var slots = pipeline.GetSlotsForTest();
        Assert.Equal(2, slots.Count);
        Assert.Equal(WorkSlotState.Abandoned, Assert.Single(slots, s => s.Slot.TaskId == firstTaskId).State);
        Assert.Equal(WorkSlotState.Pending, Assert.Single(slots, s => s.Slot.TaskId == second.TaskId).State);
    }

    /// <summary>Builds a cleanup service whose pool reports one dead worker holding <paramref name="taskId"/>.</summary>
    private static StaleWorkerCleanupService CreateCleanup(
        GoalPipelineManager manager, TaskQueue queue, string taskId)
    {
        var deadWorker = new ConnectedWorker
        {
            Id = "worker-dead",
            Role = WorkerRole.Coder,
            Capabilities = [],
            IsBusy = true,
            CurrentTaskId = taskId,
            LastHeartbeat = DateTime.UtcNow.AddMinutes(-30),
        };

        return new StaleWorkerCleanupService(
            new SingleStaleWorkerPool(deadWorker), queue, manager,
            NullLogger<StaleWorkerCleanupService>.Instance);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (12) FormatLogValue — exercised through reflection so the production
    //      helper can stay PRIVATE (acceptance criterion 3).
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the PRIVATE static <c>FormatLogValue</c> helper. The lookup itself is an
    /// assertion: removing or renaming the helper fails every vector below, so the reflection
    /// does not weaken the proof — it only avoids widening production visibility for a test.
    /// </summary>
    private static string InvokeFormatLogValue(object? value)
    {
        var method = typeof(TaskDispatchService).GetMethod(
            "FormatLogValue", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        // The helper must stay PRIVATE: a widened accessibility is itself a contract break.
        Assert.True(method!.IsPrivate, "FormatLogValue must remain a private static helper.");
        return (string)method.Invoke(null, [value])!;
    }

    [Fact]
    public void FormatLogValue_Null_RendersUnknown() =>
        Assert.Equal("unknown", InvokeFormatLogValue(null));

    [Theory]
    [InlineData("task-1", "task-1")]
    [InlineData(7, "7")]
    public void FormatLogValue_Value_RendersToString(object value, string expected) =>
        Assert.Equal(expected, InvokeFormatLogValue(value));

    [Fact]
    public void FormatLogValue_EnumValue_RendersEnumName()
    {
        Assert.Equal("Coding", InvokeFormatLogValue(GoalPhase.Coding));
        Assert.Equal("Tester", InvokeFormatLogValue(WorkerRole.Tester));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test doubles
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Minimal goal source returning a single pre-configured goal.</summary>
    private sealed class WiringGoalSource : IGoalSource
    {
        private readonly Goal _goal;
        public WiringGoalSource(Goal goal) => _goal = goal;
        public string Name => "workslot-wiring-fake";
        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([_goal]);
        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>A gateway whose idle-worker probe (the DIRECT-PUSH path) throws.</summary>
    private sealed class ThrowingIdleWorkerGateway : IWorkerGateway
    {
        private readonly Exception _sentinel;
        public ThrowingIdleWorkerGateway(Exception sentinel) => _sentinel = sentinel;
        public ConnectedWorker? GetIdleWorker() => throw _sentinel;
        public IReadOnlyList<ConnectedWorker> GetAllWorkers() => [];
        public void MarkBusy(string workerId, string taskId) { }
        public Task SendTaskAsync(string workerId, WorkTask task, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCancelAsync(string workerId, string taskId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAgentsUpdateAsync(string workerId, string role, string content, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A pool that purges exactly one stale worker per cycle and never times anything out.</summary>
    private sealed class SingleStaleWorkerPool : IWorkerPool
    {
        private ConnectedWorker? _stale;
        public SingleStaleWorkerPool(ConnectedWorker stale) => _stale = stale;

        public IReadOnlyList<ConnectedWorker> PurgeStaleWorkers(TimeSpan staleness)
        {
            var worker = Interlocked.Exchange(ref _stale, null);
            return worker is null ? [] : [worker];
        }

        public int ConnectedWorkerCount => _stale is null ? 0 : 1;
        public IReadOnlyList<ConnectedWorker> GetStaleWorkers(TimeSpan timeout) => [];
        public IReadOnlyList<ConnectedWorker> GetWorkersWithTimedOutTasks(TimeSpan timeout) => [];
        public bool TryRemoveTimedOutWorker(string id, TimeSpan timeout) => false;
        public bool RemoveWorker(string id) => false;
        public bool RemoveWorker(ConnectedWorker worker) => false;
    }

    /// <summary>A logger whose every <c>Log</c> call throws — the contract-violation vector.</summary>
    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger-sentinel");
    }

    /// <summary>
    /// A logger that records, alongside each message, the values caller-supplied probes return AT
    /// LOG TIME. Used to pin the temporal ORDER of rollback steps whose final states are disjoint:
    /// a record written before a mutation observes the pre-mutation value.
    /// </summary>
    /// <remarks>
    /// The probes read the REAL pipeline/manager state on every call — never a recorded copy — so
    /// what they capture at a given log event is exactly what production had done by that instant.
    /// </remarks>
    private sealed class RollbackProbingLogger<T> : ILogger<T>
    {
        private readonly Func<string?> _pointerProbe;
        private readonly Func<WorkSlotState?> _slotStateProbe;

        public RollbackProbingLogger(Func<string?> pointerProbe, Func<WorkSlotState?> slotStateProbe)
        {
            _pointerProbe = pointerProbe;
            _slotStateProbe = slotStateProbe;
        }

        /// <summary>Each logged message paired with both probes' values at the moment it was logged.</summary>
        public List<(string Message, string? PointerAtLog, WorkSlotState? SlotStateAtLog)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), _pointerProbe(), _slotStateProbe()));
    }
}

/// <summary>
/// The logger DI (§3): <c>Program.cs</c> constructs <see cref="GoalPipelineManager"/> with a
/// non-null <see cref="ILogger{TCategoryName}"/>, consuming A2's optional parameter instead of
/// leaving the manager's ownership diagnostics silent.
/// </summary>
[Collection("HiveIntegration")]
public sealed class WorkSlotPipelineManagerLoggerRegistrationTests
{
    private readonly WebApplicationFactory<Program> _factory;

    public WorkSlotPipelineManagerLoggerRegistrationTests(HiveTestFactory factory) => _factory = factory;

    [Fact]
    public void GoalPipelineManager_IsRegisteredWithNonNullLogger()
    {
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<GoalPipelineManager>();

        var loggerField = typeof(GoalPipelineManager)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(loggerField);

        var logger = loggerField!.GetValue(manager);
        Assert.NotNull(logger);
        Assert.IsAssignableFrom<ILogger<GoalPipelineManager>>(logger);
    }
}
