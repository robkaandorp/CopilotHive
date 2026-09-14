using System.Collections.Concurrent;
using System.Data.Common;

using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Git;
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
        foreach (var directory in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup: a locked file must never fail the fixture.
            }
        }
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

    /// <summary>
    /// Seeds a DURABLE <c>task_mappings</c> row directly through the store — the registers became
    /// MEMORY-ONLY with the admission-atomic-switch and no longer write one.
    /// </summary>
    private void SeedPersistedMapping(string taskId, string goalId) =>
        CreateStore().SaveTaskMapping(taskId, goalId);

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

    /// <summary>
    /// Reads the PERSISTED <c>pipelines.active_task_id</c> column RAW — no EF Core, no change
    /// tracker, so a stale tracked entity can never mask the durable truth. This is the column
    /// the dispatch's E3 step (<c>RollbackPersistedPointer</c>) clears, and it is DISTINCT from
    /// the in-memory <see cref="GoalPipeline.ActiveTaskId"/> pointer.
    /// </summary>
    private string? ReadPersistedActiveTaskId(string goalId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "SELECT active_task_id FROM pipelines WHERE goal_id = $goalId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$goalId";
        parameter.Value = goalId;
        command.Parameters.Add(parameter);
        var value = command.ExecuteScalar();
        return value == DBNull.Value ? null : value as string;
    }

    /// <summary>
    /// Forces the persisted <c>pipelines.active_task_id</c> to <paramref name="taskId"/> RAW,
    /// bypassing EF entirely — used to arrange a durable pointer owned by a DIFFERENT task so the
    /// ownership-checked E3 clear must decline it.
    /// </summary>
    private void ForcePersistedActiveTaskId(string goalId, string? taskId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "UPDATE pipelines SET active_task_id = $taskId WHERE goal_id = $goalId";
        var taskParameter = command.CreateParameter();
        taskParameter.ParameterName = "$taskId";
        taskParameter.Value = (object?)taskId ?? DBNull.Value;
        command.Parameters.Add(taskParameter);
        var goalParameter = command.CreateParameter();
        goalParameter.ParameterName = "$goalId";
        goalParameter.Value = goalId;
        command.Parameters.Add(goalParameter);
        command.ExecuteNonQuery();
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
        Goal? goal = null,
        AgentsManager? agentsManager = null)
    {
        config ??= CreateConfig();
        workerGateway ??= new GrpcWorkerGateway(new WorkerPool());

        var goalManager = new GoalManager();
        goalManager.AddSource(new WiringGoalSource(goal ?? CreateGoal("setup-goal")));
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var lifecycleService = new GoalLifecycleService(goalManager, logger);
        var maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway,
            brain: null, agentsManager: agentsManager, configRepo: null,
            new ConcurrentQueue<string>(), logger, config: config);

        return new TaskDispatchService(
            taskQueue, workerGateway, new TaskBuilder(new BranchCoordinator()), config,
            logger, pipelineManager, lifecycleService, maintenance);
    }

    /// <summary>
    /// An <see cref="AgentsManager"/> rooted in a fresh temp directory with a NON-EMPTY coder
    /// AGENTS.md, so <c>SendAgentsMdToWorkerAsync</c> actually reaches its gateway call (stage A
    /// really runs) rather than returning early on empty content.
    /// </summary>
    private AgentsManager CreateAgentsManager()
    {
        var root = Path.Combine(Path.GetTempPath(), $"workslot-agents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirectories.Add(root);
        var manager = new AgentsManager(root);
        manager.UpdateAgentsMd(WorkerRole.Coder, "# coder agents");
        return manager;
    }

    private readonly List<string> _tempDirectories = [];

    // ── THE ALLOCATED-TASK-ID SHAPE: readable prefix + '-'-plus-32-lowercase-hex nonce ──

    /// <summary>
    /// THE CONTROLLED NONCE. A vector that must name the EXACT id a prospective dispatch will
    /// allocate (so its seed genuinely collides) installs this on the pipeline it owns; every
    /// other vector reads the ACTUAL id production allocated out of the settled admission. The
    /// nonce is PER-PIPELINE instance state — no global override, no environment toggle.
    /// </summary>
    private static readonly Guid ControlledNonce = new("0123456789abcdef0123456789abcdef");

    private static string ControlledNonceSuffix => ControlledNonce.ToString("N");

    private static GoalPipeline WithControlledNonce(GoalPipeline pipeline)
    {
        pipeline.TaskIdNonceForTest = () => ControlledNonce;
        return pipeline;
    }

    /// <summary>
    /// The READABLE PREFIX of a built task ID:
    /// <c>{goalId}-{roleName}-{iteration:D3}-{occurrence:D2}-{attempt:D3}</c>.
    /// </summary>
    private static string TaskIdPrefix(string goalId, WorkerRole role, int iteration = 1, int occurrence = 1, int attempt = 1) =>
        $"{goalId}-{role.ToRoleName()}-{iteration:D3}-{occurrence:D2}-{attempt:D3}";

    /// <summary>
    /// Asserts the allocated-ID format: the EXACT readable prefix, then <c>'-'</c>, then exactly 32
    /// lowercase-hex characters — total length prefix + 33.
    /// </summary>
    private static void AssertSuffixedTaskId(string taskId, string expectedPrefix)
    {
        Assert.StartsWith(expectedPrefix + "-", taskId, StringComparison.Ordinal);
        Assert.Equal(expectedPrefix.Length + 33, taskId.Length);
        Assert.Matches("^[0-9a-f]{32}$", taskId[(expectedPrefix.Length + 1)..]);
    }

    /// <summary>
    /// The id the dispatch ACTUALLY admitted: the single registered slot's task id. Used by every
    /// vector whose assertions concern an admission that completed (or was rolled back with its
    /// slot kept), so nothing is hand-computed from the old unsuffixed format.
    /// </summary>
    private static string SettledTaskId(GoalPipeline pipeline) => SingleSlot(pipeline).Slot.TaskId;

    private static string ExpectedTaskId(string goalId, WorkerRole role, int iteration = 1, int occurrence = 1, int attempt = 1) =>
        TaskIdPrefix(goalId, role, iteration, occurrence, attempt);

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
    /// capture → build (verbatim, attempt-stamped task ID) → TrySetActiveTask (the atomic claim)
    /// → PersistAdmission → enqueue. Nothing is abandoned and no WorkSlotIntegrity warning is
    /// emitted.
    /// </summary>
    /// <remarks>
    /// THE ORDERING PROOF lives in the enqueue callback, not in the post-hoc assertions: the
    /// callback runs synchronously INSIDE <see cref="TaskQueue.Enqueue"/>, so whatever it observes
    /// was already true when Enqueue was ENTERED. Capturing the slot state, the mapping owner and
    /// the active pointer there pins the pre-enqueue ordering — moving <c>TrySetActiveTask</c> (or the
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

        // THE ACTUAL allocated id — read from the settled admission, never hand-computed.
        var expectedTaskId = enqueued!.TaskId;
        AssertSuffixedTaskId(expectedTaskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

        // (1) THE CAPTURE had already allocated the live slot before Enqueue was entered.
        var slotAtEntry = Assert.Single(slotsAtEntry);
        Assert.Equal(expectedTaskId, slotAtEntry.Slot.TaskId);
        Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), slotAtEntry.Slot.Position);
        Assert.Equal(1, slotAtEntry.Slot.Attempt);
        Assert.Equal(WorkSlotState.Pending, slotAtEntry.State);
        // (3) THE MAPPING was already ours, in memory AND in the store.
        Assert.Equal(GoalId, mappedGoalAtEntry);
        Assert.Equal(GoalId, persistedGoalAtEntry);
        // (4) THE POINTER already named this task — the TrySetActiveTask claim precedes the enqueue.
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

        // The READABLE PREFIX the capture would have used for the first attempt at this position.
        // The refusal mints NO id, so absence is asserted by prefix for the registry and by the
        // canonical prospective id for the mapping surface.
        var wouldBePrefix = ExpectedTaskId(GoalId, role);
        // BEFORE: the registry snapshot the refusal must leave untouched.
        var slotsBefore = SlotSnapshot(pipeline);

        await Assert.ThrowsAsync<WorkSlotException>(
            () => service.DispatchToRole(pipeline, role, "Do it", TestContext.Current.CancellationToken));

        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Warning && e.Message == expected);

        // AFTER: the registry is EXACTLY as before — no slot allocated for the refused position,
        // and no pre-existing slot's state disturbed. No slot carries the prospective prefix.
        Assert.Equal(slotsBefore, SlotSnapshot(pipeline));
        Assert.DoesNotContain(
            pipeline.GetSlotsForTest(),
            s => s.Slot.TaskId.StartsWith(wouldBePrefix + "-", StringComparison.Ordinal));

        // No mapping was claimed — neither in the manager's memory nor in the store.
        var wouldBeTaskId = wouldBePrefix + "-" + ControlledNonceSuffix;
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

        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
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
        var pipeline = WithControlledNonce(manager.CreatePipeline(CreateGoal(GoalId)));
        Arrange(pipeline, GoalPhase.Coding);

        // THE NONCE IS CONTROLLED so the competing mapping is seeded for the EXACT id the dispatch
        // will allocate — the duplicate/occupied-mapping refusal stays genuinely armed. The register
        // is memory-only now, so the persisted row is seeded separately for the untouched-competitor
        // assertion below.
        var taskId = TaskIdPrefix(GoalId, WorkerRole.Coder) + "-" + ControlledNonceSuffix;
        manager.CreatePipeline(CreateGoal("goal-other"));
        manager.RegisterTask(taskId, "goal-other");
        SeedPersistedMapping(taskId, "goal-other");

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
    /// <remarks>
    /// THE PATH CHANGED: the failure now comes from the ADMISSION (PersistAdmission's
    /// SaveAdmissionWithPointer transaction), not from the removed register store path. EF wraps
    /// the interceptor's sentinel in a <c>DbUpdateException</c>, and R5 carries THAT wrapper —
    /// <c>admission.PersistenceException</c> — verbatim. The identity assertion therefore pins the
    /// carried instance to the admission result's own exception, with the injected sentinel proven
    /// to be its inner cause: the runtime identity of the injected failure stays observable.
    /// </remarks>
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

        // THE ACTUAL allocated id — the slot is retained (Abandoned) after the refusal.
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        Assert.Equal(
            $"Task mapping registration failed for {taskId} (goal {GoalId}) — the mapping is occupied or the persistence failed",
            ex.Message);

        // THE INNER-EXCEPTION RULE: PersistenceFailed carries the store's exception (the EF
        // wrapper), and the INJECTED sentinel is reachable as its cause.
        Assert.NotNull(ex.InnerException);
        Assert.IsType<DbUpdateException>(ex.InnerException);
        Assert.Same(sentinel, ex.InnerException!.InnerException);

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

        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
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
        var pipeline = WithControlledNonce(manager.CreatePipeline(CreateGoal(GoalId)));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue { OnEnqueue = _ => throw sentinel };

        // THE NONCE IS CONTROLLED so the logger's slot probe names the EXACT id the dispatch will
        // allocate — the a/b boundary observation stays real.
        var taskId = TaskIdPrefix(GoalId, WorkerRole.Coder) + "-" + ControlledNonceSuffix;
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
            // THE STEAL, restated for the memory-only TryAdd register: RegisterTask now REFUSES a
            // duplicate instead of overwriting it, so a genuine steal must first release our claim
            // and then take it — which is exactly what a real competitor's
            // unregister-then-register sequence does.
            manager.UnregisterTask(task.TaskId);
            manager.RegisterTask(task.TaskId, "goal-other");
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        // THE ACTUAL allocated id, read from the settled slot (never hand-computed).
        var taskId = SettledTaskId(pipeline);

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

        // THE ACTUAL allocated id, read from the settled slot (never hand-computed).
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

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
    // (7 + 11d) The dispatch-owned unregister-result record — the GUARDED seam
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE β-PREP-2 SEAM: a dispatch-level predicate logger whose throw targets ONLY the
    /// dispatch-owned unregister-result DEBUG template. THE VECTOR: the same enqueue-throw shape
    /// the old contract-violation test used — the OnEnqueue two-step seed (the competitor steals
    /// the mapping, then the sentinel infrastructure throw) — so the enqueue catch's rollback
    /// runs. The rollback's TryUnregisterTask hits the not-mapped path → (false, false); the
    /// dispatch-owned DEBUG record of that result then THROWS — and is SWALLOWED inside
    /// <c>TaskDispatchService.LogSafely</c>, with no catch elsewhere firing and the control
    /// flow unchanged.
    /// </summary>
    /// <remarks>
    /// WHAT THE SWALLOW PROVES: with the (false, false) result, the (true-memory, failed-persist)
    /// partial is IMPOSSIBLE here, so no LogRollbackFailure("unregister") record may appear
    /// anywhere — the no-rollback-failure semantics of the (false, false) path are PRESERVED
    /// through the guarded emission. The abandoned-registration line RECORDED proves the
    /// remaining rollback steps still ran after the swallowed throw, and the ORIGINAL sentinel is
    /// what leaves the dispatch — the logger's exception appears NOWHERE (the two-exception
    /// distinction: only the LOGGER's throw is swallowed; the infrastructure exception still
    /// rethrows).
    /// </remarks>
    [Fact]
    public async Task Dispatch_UnregisterResultLogThrows_LogIsSwallowedAndCleanupContinues_RethrowsOriginal()
    {
        var manager = new GoalPipelineManager(store: null, new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        manager.CreatePipeline(CreateGoal("goal-other"));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();

        // THE ACTUAL allocated id is resolved AFTER the dispatch — the slot survives the rollback
        // (Abandoned), so the single registered slot names it. The logger's predicate below keys on
        // the TEMPLATE, not the id, so ordering the resolution after the act is sound.
        var logger = new SelectivelyThrowingLogger<TaskDispatchService>(
            m => m.Contains("WorkSlotIntegrity: unregister goal=", StringComparison.Ordinal));
        var service = CreateService(manager, queue, logger);

        // The OnEnqueue two-step seed: (i) the competitor claims the mapping (so the rollback's
        // TryUnregisterTask is not ours → (false, false)), (ii) the ORIGINAL sentinel throw.
        queue.OnEnqueue = task =>
        {
            // THE STEAL, restated for the memory-only TryAdd register (see the ownership-race test
            // above): release our claim first, then take it, so the rollback's ownership-checked
            // TryUnregisterTask genuinely finds a mapping that is not ours → (false, false).
            manager.UnregisterTask(task.TaskId);
            manager.RegisterTask(task.TaskId, "goal-other");
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE TWO-EXCEPTION DISTINCTION: the ORIGINAL sentinel is what left the dispatch —
        // the logger's exception is nowhere.
        Assert.Same(sentinel, thrown);

        // THE ACTUAL allocated id — the rollback retained the slot (Abandoned).
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

        // THE SWALLOW HAPPENED (the guarded site really was reached)…
        Assert.Contains(
            logger.SeenMessages,
            m => m == $"WorkSlotIntegrity: unregister goal={GoalId} task={taskId} memoryRemoved=False persistenceRemoved=False");

        // …AND the (false, false)'s no-rollback-failure semantics are preserved: NO
        // LogRollbackFailure("unregister") record anywhere — the DEBUG template's throw was
        // swallowed by LogSafely, never diverted into the surrounding catch.
        Assert.DoesNotContain(
            logger.SeenMessages, m => m.Contains("rollback-failure", StringComparison.Ordinal));

        // THE ROLLBACK CONTINUED: the slot is abandoned, the pointer cleared, and the
        // abandoned-registration line was RECORDED (the predicate never threw at it).
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Contains(
            logger.SeenMessages,
            m => m == AbandonedRegistrationMessage(GoalId, taskId, 1, GoalPhase.Coding, 1));

        // THE NOT-MAPPED SEMANTICS: the pair-based remove took nothing of ours — the
        // competitor's mapping survives (the (false, false) shape in memory).
        var otherPipeline = manager.GetByGoalId("goal-other");
        Assert.Same(otherPipeline, manager.GetByTaskId(taskId));
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
        var taskId = SettledTaskId(pipeline);
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(manager.GetByTaskId(taskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (8b) E3 — the PERSISTED pointer rollback, at DISPATCH level
    // ═══════════════════════════════════════════════════════════════════════
    //
    // These vectors assert the DURABLE pipelines.active_task_id column (read RAW through the
    // keeper connection), NOT the in-memory pointer that section (8) covers. E3 is the enqueue
    // catch's step (b2): it runs ONLY when this dispatch actually committed the rows, is
    // ownership-checked in the store's WHERE clause, and reports a Failed outcome as the
    // step=pointer-rollback record while the remaining cleanup continues.

    /// <summary>
    /// E3 CLEARS THE PERSISTED POINTER. The admission commits mapping + pointer in one
    /// transaction; the enqueue then throws, and the rollback nulls the DURABLE
    /// <c>pipelines.active_task_id</c> column — not merely the in-memory pointer.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: deleting the dispatch's E3 block leaves the committed pointer
    /// durably set, so the persisted-column assertion fails while every in-memory assertion in
    /// section (8) still passes — which is exactly the gap this vector closes.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EnqueueThrows_ClearsPersistedActiveTaskId()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        // OBSERVED INSIDE Enqueue: the admission has committed, so the DURABLE pointer names this
        // task at the moment the rollback is about to run. This makes the post-hoc null assertion
        // a real state CHANGE rather than a value that was never set. The callback also records the
        // ACTUAL allocated id, so the assertions below never depend on a hand-computed string.
        string? persistedPointerAtEnqueue = null;
        string? taskIdAtEnqueue = null;
        queue.OnEnqueue = t =>
        {
            taskIdAtEnqueue = t.TaskId;
            persistedPointerAtEnqueue = ReadPersistedActiveTaskId(GoalId);
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        // THE COMMIT REALLY HAPPENED (the E3 precondition CommittedThisInvocation was true), under
        // the id this dispatch actually allocated.
        Assert.NotNull(taskIdAtEnqueue);
        AssertSuffixedTaskId(taskIdAtEnqueue!, TaskIdPrefix(GoalId, WorkerRole.Coder));
        Assert.Equal(taskIdAtEnqueue, persistedPointerAtEnqueue);

        // E3'S EFFECT: the DURABLE column is NULL.
        Assert.Null(ReadPersistedActiveTaskId(GoalId));

        // The rest of the rollback still ran, and no rollback-failure was recorded.
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(ReadPersistedGoalId(taskIdAtEnqueue!));
        Assert.DoesNotContain(Warnings(logger), m => m.Contains("rollback-failure", StringComparison.Ordinal));
    }

    /// <summary>
    /// E3 IS OWNERSHIP-PRESERVING. When the DURABLE pointer has moved on to a different task, the
    /// store's <c>WHERE active_task_id = $task</c> declines the clear (NotMatched) and the newer
    /// durable pointer survives — a live dispatch is never made to look idle.
    /// </summary>
    [Fact]
    public async Task Dispatch_EnqueueThrowsWithDifferentPersistedPointer_LeavesPersistedPointerIntact()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        // THE RACE, arranged deterministically INSIDE Enqueue: a competing attempt takes over the
        // DURABLE pointer after our admission committed but before the rollback runs.
        queue.OnEnqueue = _ =>
        {
            ForcePersistedActiveTaskId(GoalId, "newer-durable-task");
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        // THE OWNERSHIP CHECK HELD: the competitor's durable pointer is untouched.
        Assert.Equal("newer-durable-task", ReadPersistedActiveTaskId(GoalId));

        // NotMatched is the correct completion, NOT a failure — no pointer-rollback record. The id
        // is the ACTUAL allocated one (read from the settled slot).
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        Assert.DoesNotContain(
            Warnings(logger), m => m == RollbackFailureMessage(GoalId, taskId, "pointer-rollback"));

        // Our own slot and mapping are still released.
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(manager.GetByTaskId(taskId));
    }

    /// <summary>
    /// E3 FAILURE: the persisted-pointer UPDATE throws, so
    /// <see cref="PointerRollbackResult.Failed"/> is reported. The dispatch records
    /// <c>step=pointer-rollback</c>, CONTINUES the remaining cleanup, and rethrows the ORIGINAL
    /// enqueue exception.
    /// </summary>
    /// <remarks>
    /// The interceptor targets <c>UPDATE ... pipelines</c> ONLY, so the admission's INSERTs commit
    /// normally and only E3's clear fails — the narrow injection that isolates this step.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EnqueueThrowsAndPersistedPointerRollbackFails_LogsPointerRollbackAndContinues()
    {
        var updateSentinel = new InvalidOperationException("pointer-update-sentinel");
        var manager = new GoalPipelineManager(
            CreateStore(new PipelinesUpdateThrowingInterceptor(updateSentinel)),
            new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var enqueueSentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue { OnEnqueue = _ => throw enqueueSentinel };
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE ORIGINAL enqueue failure leaves the dispatch — never the pointer sentinel, and
        // never a wrapper.
        Assert.Same(enqueueSentinel, thrown);
        Assert.NotSame(updateSentinel, thrown);

        // THE ACTUAL allocated id, read from the settled slot (never hand-computed).
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

        // THE RECORD, rendered verbatim at WARNING.
        Assert.Contains(
            Warnings(logger), m => m == RollbackFailureMessage(GoalId, taskId, "pointer-rollback"));

        // THE CLEANUP CONTINUED PAST THE FAILED STEP: E4's in-memory clear and E5's
        // abandoned-registration record both still happened.
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == AbandonedRegistrationMessage(GoalId, taskId, 1, GoalPhase.Coding, 1));

        // The durable residue is honestly left behind — the E2b successor owns reconciling it.
        Assert.Equal(taskId, ReadPersistedActiveTaskId(GoalId));
    }

    /// <summary>
    /// THE E2 → E3 → E4 ORDER, observed rather than assumed. Each step's OWN observable is
    /// captured at the moment the NEXT step's seam runs, so the three are pinned in sequence:
    /// E2 (mapping removal) is already done when E3's UPDATE is issued, and E3 is already done
    /// when E4 clears the in-memory pointer.
    /// </summary>
    /// <remarks>
    /// THE SEAM: the store's UPDATE against <c>pipelines</c> is E3 itself, so an interceptor that
    /// records state at that statement observes the world strictly BETWEEN E2 and E4. Moving E3
    /// above E2 makes the mapping still present; moving it below E4 makes the in-memory pointer
    /// already null — either mutation flips one of the two assertions.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EnqueueThrows_RunsE3BetweenMappingRemovalAndPointerClear()
    {
        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue { OnEnqueue = _ => throw sentinel };
        var logger = new TestLogger<TaskDispatchService>();

        // The observation runs INSIDE E3's own statement, so whatever it sees is the state
        // between E2 and E4 by construction. It needs the ACTUAL id, which is only knowable BEFORE
        // the dispatch via the CONTROLLED nonce — resolved from the settled slot AFTERWARDS and
        // compared below, so the observation is pinned to a real id rather than a guess.
        GoalPipeline? pipeline = null;
        string? mappingOwnerDuringE3 = null;
        string? memoryPointerDuringE3 = null;
        string? taskIdDuringE3 = null;
        var e3Observed = false;
        var observer = new PipelinesPointerUpdateObserver(() =>
        {
            e3Observed = true;
            taskIdDuringE3 = pipeline!.ActiveTaskId;
            mappingOwnerDuringE3 = taskIdDuringE3 is null ? null : ReadPersistedGoalId(taskIdDuringE3);
            memoryPointerDuringE3 = pipeline!.ActiveTaskId;
        });

        var manager = new GoalPipelineManager(
            CreateStore(observer), new TestLogger<GoalPipelineManager>());
        pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);
        var service = CreateService(manager, queue, logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        // E3 REALLY RAN (the vacuity guard: without it both observations stay null).
        Assert.True(e3Observed, "E3's persisted-pointer UPDATE must have been issued");

        // THE ID THE DISPATCH ALLOCATED: observed live at E3, and equal to the settled slot's id.
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        Assert.Equal(taskId, taskIdDuringE3);

        // E2 PRECEDED E3: the mapping row was already gone when E3's UPDATE ran.
        Assert.Null(mappingOwnerDuringE3);

        // E4 FOLLOWED E3: the in-memory pointer still named this task when E3's UPDATE ran.
        Assert.Equal(taskId, memoryPointerDuringE3);

        // …and E4 did eventually run.
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Null(ReadPersistedActiveTaskId(GoalId));
    }

    /// <summary>
    /// THE NO-STORE SKIP is preserved: with no store there is nothing to roll back, so E3 issues
    /// no statement and records nothing, while the rest of the rollback runs normally.
    /// </summary>
    /// <remarks>
    /// The admission returns <c>NoStore</c> with <c>CommittedThisInvocation == false</c>, which is
    /// precisely the condition E3 is gated on — the same gate the β-PREP-2 fixture relies on.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EnqueueThrowsWithNoStore_SkipsPersistedPointerRollback()
    {
        var manager = new GoalPipelineManager(store: null, new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue { OnEnqueue = _ => throw sentinel };
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        // THE ACTUAL allocated id, read from the settled slot (never hand-computed).
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

        // NO pointer-rollback record: the step was skipped, not attempted and failed.
        Assert.DoesNotContain(
            Warnings(logger), m => m == RollbackFailureMessage(GoalId, taskId, "pointer-rollback"));
        Assert.DoesNotContain(Warnings(logger), m => m.Contains("rollback-failure", StringComparison.Ordinal));

        // The rest of the rollback is unchanged.
        Assert.Null(pipeline.ActiveTaskId);
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

        // THE ACTUAL allocated id (the admission stands, so the live slot names it).
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
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
    // (10) The cleanup's replacement reclaim — D2 (retire + unregister + redispatch)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE RE-ENQUEUE INTERIM IS RETIRED. Both branches (task still active / already gone) now
    /// complete the queue entry UNCONDITIONALLY with NO re-enqueue, retire the slot and
    /// unregister the mapping — the replacement comes ONLY from the redispatch's fresh dispatch.
    /// (Replaces the interim assertion that the active-task branch re-enqueues the old task.)
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cleanup_CompletesEntryInBothBranches_NeverReEnqueues(bool taskStillActive)
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var service = CreateService(manager, queue, new TestLogger<TaskDispatchService>());
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        var dispatched = queue.TryDequeueAny();
        Assert.NotNull(dispatched);
        if (taskStillActive)
            queue.Activate(dispatched!, "worker-dead");

        var cleanup = CreateCleanup(manager, queue, taskId);
        await cleanup.RunCleanupCycleAsync();

        // The slot is DEAD in both branches, and the pointer is cleared.
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(pipeline.ActiveTaskId);
        // THE MAPPING IS UNREGISTERED in both branches.
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(ReadPersistedGoalId(taskId));

        // THE RETIREMENT OF THE INTERIM: NO branch re-enqueues the old task — the entry is gone.
        Assert.Null(queue.GetActiveTask(taskId));
        Assert.Null(queue.TryDequeueAny());
    }

    /// <summary>
    /// THE FRESH-CAPTURE END-TO-END: after a reclaim the old TaskId is absent from
    /// <c>_pending</c>/<c>_active</c>, and one FRESH replacement dispatch happens and is green —
    /// the new attempt is admitted (captured, registered, pointed at, enqueued) while the old
    /// slot stays dead.
    /// </summary>
    [Fact]
    public async Task Cleanup_ThenRedisdispatch_OldTaskAbsentOneFreshReplacementDispatchIsGreen()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var service = CreateService(manager, queue, new TestLogger<TaskDispatchService>());
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        var firstTaskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(firstTaskId, TaskIdPrefix(GoalId, WorkerRole.Coder, attempt: 1));
        var dispatched = queue.TryDequeueAny();
        Assert.NotNull(dispatched);
        queue.Activate(dispatched!, "worker-dead");

        var cleanup = CreateCleanup(manager, queue, firstTaskId);
        await cleanup.RunCleanupCycleAsync();

        // THE OLD TASK IS ABSENT from both belts of the queue.
        Assert.Null(queue.GetActiveTask(firstTaskId));
        Assert.Null(queue.TryDequeueAny());

        // ONE FRESH REPLACEMENT DISPATCH happens and is GREEN: the capture succeeds, the mapping
        // is registered (memory + persisted), the pointer names the new task, and the task is
        // enqueued with NO integrity record of any kind.
        var logger = new TestLogger<TaskDispatchService>();
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it again", TestContext.Current.CancellationToken);

        // THE ACTUAL second id: the replacement's slot is the LIVE (Pending) one, and its id is the
        // id the fresh capture allocated — attempt 2 with its OWN fresh suffix.
        var secondTaskId = Assert.Single(pipeline.GetSlotsForTest(), s => s.State == WorkSlotState.Pending).Slot.TaskId;
        AssertSuffixedTaskId(secondTaskId, TaskIdPrefix(GoalId, WorkerRole.Coder, attempt: 2));
        Assert.NotEqual(firstTaskId, secondTaskId, StringComparer.Ordinal);
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipeline.GetSlotsForTest(), s => s.Slot.TaskId == secondTaskId).State);
        Assert.Equal(WorkSlotState.Abandoned, Assert.Single(pipeline.GetSlotsForTest(), s => s.Slot.TaskId == firstTaskId).State);
        Assert.Same(pipeline, manager.GetByTaskId(secondTaskId));
        Assert.Equal(GoalId, ReadPersistedGoalId(secondTaskId));
        Assert.Equal(secondTaskId, pipeline.ActiveTaskId);
        Assert.Equal([secondTaskId], DrainPending(queue));
        Assert.DoesNotContain(Warnings(logger), m => m.Contains("WorkSlotIntegrity", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE POST-RECLAIM ECHO: the reclaim's <c>(true, true)</c> unregister is the mapping belt —
    /// when the OLD completion arrives afterwards it resolves NO pipeline and is dropped; the
    /// pipeline is untouched by the echo.
    /// </summary>
    [Fact]
    public async Task Cleanup_ReclaimThenOldCompletion_ResolvesNoPipeline()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var service = CreateService(manager, queue, new TestLogger<TaskDispatchService>());
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        var dispatched = queue.TryDequeueAny();
        Assert.NotNull(dispatched);
        queue.Activate(dispatched!, "worker-dead");

        var cleanup = CreateCleanup(manager, queue, taskId);
        await cleanup.RunCleanupCycleAsync();

        // THE MAPPING BELT: the old completion can no longer resolve ANY pipeline.
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(ReadPersistedGoalId(taskId));

        // THE COMPLETION GUARD'S NO-PIPELINE DROP: the late completion hits the no-pipeline
        // warning and the pipeline's phase and admission state are untouched.
        var completionLogger = new TestLogger<TaskCompletionService>();
        var completionService = CreateCompletionService(manager, completionLogger);
        await completionService.HandleTaskCompletionAsync(
            new TaskResult { TaskId = taskId, Status = TaskOutcome.Completed, Output = "late" },
            TestContext.Current.CancellationToken);

        Assert.Contains(completionLogger.LogEntries, l =>
            l.LogLevel == LogLevel.Warning &&
            l.Message.Contains("No pipeline found for completed task") &&
            l.Message.Contains(taskId));
        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
    }

    /// <summary>
    /// THE RESTART VECTOR — the mapping-durability claim ONLY. After the reclaim's
    /// <c>(true, true)</c> unregister, a FRESH manager/store round (new instances reading the
    /// same persisted store) resolves the old completion to NO pipeline, and the REPLACEMENT's
    /// completion flows. The restored pipeline's slot/pointer reconciliation is NOT asserted —
    /// it belongs to the completion-protocol successor.
    /// </summary>
    [Fact]
    public async Task Cleanup_AfterReclaim_RestartedManagerCannotResolveOldCompletion_ButNewOneFlows()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var service = CreateService(manager, queue, new TestLogger<TaskDispatchService>());
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // THE ACTUAL first id — the dispatch admitted it, so the live slot names it.
        var firstTaskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(firstTaskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        var dispatched = queue.TryDequeueAny();
        Assert.NotNull(dispatched);
        queue.Activate(dispatched!, "worker-dead");

        var cleanup = CreateCleanup(manager, queue, firstTaskId);
        await cleanup.RunCleanupCycleAsync();

        // THE PERSISTED MAPPING IS GONE — the durable belt held.
        Assert.Null(manager.GetByTaskId(firstTaskId));
        Assert.Null(ReadPersistedGoalId(firstTaskId));

        // THE RESTART: a FRESH manager and store round over the SAME persisted database.
        var restoredStore = CreateStore();
        var restartedManager = new GoalPipelineManager(restoredStore, new TestLogger<GoalPipelineManager>());
        restartedManager.RestoreFromStore();

        // The old completion resolves NO pipeline in the restarted world.
        Assert.Null(restartedManager.GetByTaskId(firstTaskId));
        var restartedLogger = new TestLogger<TaskCompletionService>();
        var restartedCompletion = CreateCompletionService(restartedManager, restartedLogger);
        await restartedCompletion.HandleTaskCompletionAsync(
            new TaskResult { TaskId = firstTaskId, Status = TaskOutcome.Completed, Output = "post-restart echo" },
            TestContext.Current.CancellationToken);
        Assert.Contains(restartedLogger.LogEntries, l =>
            l.LogLevel == LogLevel.Warning &&
            l.Message.Contains("No pipeline found for completed task") &&
            l.Message.Contains(firstTaskId));

        // THE REPLACEMENT flows: a fresh dispatch in the restarted round claims its mapping IN
        // MEMORY (the register is memory-only since the admission-atomic-switch; persistence
        // belongs to the admission path), and the replacement's completion resolves the pipeline.
        // The replacement id is a REAL allocated id from the allocator itself — never a
        // hand-computed string. NOTE the RESTART-IDENTITY shape this pins: the restored pipeline's
        // counters restarted, so the replacement's attempt is 1 AGAIN, over the SAME readable
        // prefix — and its id is nevertheless DISTINCT from the first task's, because a genuinely
        // new allocation mints its own nonce suffix. That is collision RESISTANCE, not restart
        // recovery: no ID is replayed and no assignment is durably bound.
        var restoredPipeline = restartedManager.GetByGoalId(GoalId);
        Assert.NotNull(restoredPipeline);
        var replacement = restoredPipeline!.AllocateAttemptAndRegisterSlotWithId(
            GoalId, WorkerRole.Coder, new WorkSlotPosition(1, GoalPhase.Coding, 1));
        Assert.Equal(1, replacement.Attempt);
        AssertSuffixedTaskId(replacement.TaskId, TaskIdPrefix(GoalId, WorkerRole.Coder, attempt: 1));
        var replacementTaskId = replacement.TaskId;
        Assert.NotEqual(firstTaskId, replacementTaskId, StringComparer.Ordinal);
        restartedManager.RegisterTask(replacementTaskId, GoalId);
        await restartedCompletion.HandleTaskCompletionAsync(
            new TaskResult
            {
                TaskId = replacementTaskId,
                Status = TaskOutcome.Completed,
                Output = "the replacement completed",
                Metrics = new TaskMetrics { Verdict = "PASS" },
            },
            TestContext.Current.CancellationToken);
        // THE MAPPING CLAIM ONLY (not the restored-pipeline slot/pointer reconciliation — that is
        // the completion-protocol successor's, per the production note): the restarted manager
        // resolves the replacement task to the restored pipeline.
        Assert.Same(
            restartedManager.GetByGoalId(GoalId),
            restartedManager.GetByTaskId(replacementTaskId));
        Assert.DoesNotContain(restartedLogger.LogEntries, l =>
            l.Message.Contains("No pipeline found for completed task") &&
            l.Message.Contains(replacementTaskId));
    }

    /// <summary>
    /// Builds a <see cref="TaskCompletionService"/> over the given manager with a pass-through
    /// driver (no brain), so the early-exit guards are what is under test.
    /// </summary>
    private static TaskCompletionService CreateCompletionService(
        GoalPipelineManager manager, TestLogger<TaskCompletionService> logger)
    {
        var goalManager = new GoalManager();
        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);
        var pipelineDriver = new PipelineDriver(
            brain: null,
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => Task.CompletedTask,
            resolvePrompt: (_, _, _, _) => Task.FromResult("prompt"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);
        return new TaskCompletionService(
            manager, null, pipelineDriver, lifecycleService, null, logger);
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
    // (13) THE DELIVERY TRANSACTION — the stage machine and its recovery table
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The delivery-failure record's outcome clause for the cancel-check requeue.</summary>
    private const string RequeueOutcome = "was returned to the pending queue";

    /// <summary>The delivery-failure record's outcome clause for the P2/S preserve.</summary>
    private const string PreserveOutcome =
        "remains active; the outcome is unknowable; the recovery is deferred";

    private static string DeliveryFailureMessage(
        string goalId, string taskId, string workerId, string stage, string recovery, string outcome) =>
        $"WorkSlotIntegrity: delivery-failure goal={goalId} task={taskId} worker={workerId} " +
        $"stage={stage} recovery={recovery} — the task {outcome}";

    private static string DeliveryMismatchMessage(string goalId, string registeredTaskId, string deliveredTaskId) =>
        $"WorkSlotIntegrity: delivery-mismatch goal={goalId} registered={registeredTaskId} delivered={deliveredTaskId} — " +
        "the push delivered an earlier queued task of the requested role (role-aware FIFO; no action)";

    private static string DeliveryRollbackFailureMessage(string goalId, string taskId, string step) =>
        $"WorkSlotIntegrity: delivery-rollback-failure goal={goalId} task={taskId} step={step} — " +
        "the rollback step failed; continuing";

    private static string DeliveryRecoveryMessage(string goalId, string taskId) =>
        $"WorkSlotIntegrity: delivery-recovery goal={goalId} task={taskId} stage=cancel-check — " +
        "the recovery steps completed through the re-enqueue";

    /// <summary>An idle worker holding <paramref name="role"/> as its PRE-MUTATION role.</summary>
    private static ConnectedWorker CreateIdleWorker(
        string id = "worker-delivery", WorkerRole role = WorkerRole.Tester) => new()
        {
            Id = id,
            Role = role,
            Capabilities = [],
        };

    /// <summary>Drains the pending queue WITHOUT re-triggering the enqueue callback.</summary>
    private static IReadOnlyList<string> DrainPending(TaskQueue queue)
    {
        queue.OnEnqueue = null;
        var ids = new List<string>();
        while (queue.TryDequeueAny() is { } task)
            ids.Add(task.TaskId);
        return ids;
    }

    // ── (k) The happy delivery ───────────────────────────────────────────────

    /// <summary>
    /// THE HAPPY PATH, unchanged: the dequeued task is activated, the worker is marked busy with
    /// its model, the task is sent — and NO delivery-transaction record is emitted at all.
    /// <para>
    /// EXTENDED WITH THE ONE-ID END-TO-END CHAIN. This SINGLE fixture now also proves that ONE
    /// exact allocated task ID — the one the PRODUCTION allocator minted for this run, never a
    /// hand-computed or predicted string — is the ID carried by ALL SIX named surfaces of a REAL
    /// dispatch+delivery:
    /// </para>
    /// <list type="number">
    ///   <item>the registered WORK SLOT (its <c>TaskId</c> plus its structured
    ///     <see cref="WorkSlotPosition"/> and attempt);</item>
    ///   <item><see cref="GoalPipeline.ActiveTaskId"/> — the active pointer;</item>
    ///   <item>the IN-MEMORY pipeline-manager mapping (task → goal);</item>
    ///   <item>the PERSISTED <c>task_mappings</c> row, read RAW from the SQLite database;</item>
    ///   <item>the queued <see cref="WorkTask"/>'s <c>TaskId</c>;</item>
    ///   <item>WORKER DELIVERY — the gateway actually received a task carrying that ID.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// WHERE EACH SURFACE IS OBSERVED. All six are captured from the <c>OnSendTask</c> seam, which
    /// runs INSIDE the gateway's real <c>SendTaskAsync</c> — stage S, the LAST step of the delivery
    /// transaction. At that instant the admission is fully committed AND nothing has been released
    /// yet, so the slot, the pointer, both mappings, the queue's active entry and the delivered
    /// task are ALL simultaneously live and are read from the REAL production objects (no copies,
    /// no synthesis, no sleep). Surface 6 is necessarily observed by the DELIVERED task handed to
    /// the seam, which IS the delivery; the post-await assertions then re-confirm the same single
    /// ID on every surface after the dispatch returned, which is still before any completion
    /// handling could release the pointer.
    /// <para>
    /// THE MUTATIONS THIS KILLS: removing (or pre-clearing) the active-task claim fails surface 2;
    /// dropping the in-memory claim or the persisted admission row fails surface 3 or 4; minting a
    /// second ID anywhere along the chain fails whichever surface diverges, because every
    /// assertion compares against the ONE ID read out of the settled slot.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Delivery_HappyPath_ActivatesMarksBusyAndSendsWithoutAnyDeliveryRecord()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var worker = CreateIdleWorker();

        // THE QUEUED TASK, captured from the enqueue seam — surface 5's own observation, taken on
        // the real path rather than reconstructed afterwards (the queue is drained by the delivery).
        WorkTask? enqueuedTask = null;
        queue.OnEnqueue = t => enqueuedTask = t;

        // ── THE DELIVERY-TIME OBSERVATIONS (stage S — see the remarks) ──────────────────
        WorkTask? deliveredTask = null;
        IReadOnlyList<WorkSlotView> slotsAtSend = [];
        string? pointerAtSend = null;
        string? memoryMappedGoalAtSend = null;
        string? persistedMappedGoalAtSend = null;
        WorkTask? activeQueueEntryAtSend = null;

        var gateway = new DeliveryWorkerGateway(worker)
        {
            OnSendTask = task =>
            {
                deliveredTask = task;                                   // (6) worker delivery
                slotsAtSend = pipeline.GetSlotsForTest();               // (1) the work slot
                pointerAtSend = pipeline.ActiveTaskId;                  // (2) the active pointer
                memoryMappedGoalAtSend = manager.GetByTaskId(task.TaskId)?.GoalId;   // (3) in-memory
                persistedMappedGoalAtSend = ReadPersistedGoalId(task.TaskId);        // (4) persisted, RAW
                activeQueueEntryAtSend = queue.GetActiveTask(task.TaskId);           // (5) the queue
            },
        };
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // THE ACTUAL allocated id (the admission stands, so the live slot names it).
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        Assert.Equal([taskId], gateway.SentTaskIds);
        Assert.Equal([taskId], gateway.MarkedBusyTaskIds);
        Assert.NotNull(queue.GetActiveTask(taskId));
        Assert.Empty(DrainPending(queue));
        Assert.Equal(WorkerRole.Coder, worker.Role);
        Assert.Equal("coder-model", worker.CurrentModel);

        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains("delivery-", StringComparison.Ordinal));

        // ═══════════════════════════════════════════════════════════════════════════════
        //  THE ONE-ID END-TO-END CHAIN — all six surfaces, the SAME single allocated ID,
        //  observed at stage S where they are simultaneously live.
        // ═══════════════════════════════════════════════════════════════════════════════

        // THE SEAM REALLY RAN — otherwise every observation below would be vacuously null.
        Assert.NotNull(deliveredTask);

        // (1) THE WORK SLOT: the id, plus its STRUCTURED position and attempt.
        var slotAtSend = Assert.Single(slotsAtSend);
        Assert.Equal(taskId, slotAtSend.Slot.TaskId);
        Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), slotAtSend.Slot.Position);
        Assert.Equal(1, slotAtSend.Slot.Attempt);
        Assert.Equal(WorkSlotState.Pending, slotAtSend.State);
        // The ID is the one built from THOSE structured values plus a well-formed nonce.
        AssertSuffixedTaskId(
            slotAtSend.Slot.TaskId,
            TaskIdPrefix(GoalId, WorkerRole.Coder, slotAtSend.Slot.Position.Iteration,
                slotAtSend.Slot.Position.Occurrence, slotAtSend.Slot.Attempt));

        // (2) THE ACTIVE POINTER named this exact id at delivery time…
        Assert.Equal(taskId, pointerAtSend);
        // …and still does after the dispatch returned (nothing has completed yet).
        Assert.Equal(taskId, pipeline.ActiveTaskId);

        // (3) THE IN-MEMORY MAPPING resolves this exact id to this goal.
        Assert.Equal(GoalId, memoryMappedGoalAtSend);
        Assert.Same(pipeline, manager.GetByTaskId(taskId));

        // (4) THE PERSISTED MAPPING: the raw task_mappings row for this exact id.
        Assert.Equal(GoalId, persistedMappedGoalAtSend);
        Assert.Equal(GoalId, ReadPersistedGoalId(taskId));
        Assert.Equal([taskId], ReadAllPersistedTaskIds());

        // (5) THE QUEUED TASK — the enqueued instance and the queue's active entry are both it.
        Assert.NotNull(enqueuedTask);
        Assert.Equal(taskId, enqueuedTask!.TaskId);
        Assert.NotNull(activeQueueEntryAtSend);
        Assert.Equal(taskId, activeQueueEntryAtSend!.TaskId);

        // (6) WORKER DELIVERY: the task the gateway actually received carries this exact id, and
        // the worker it was marked busy with names it too.
        Assert.Equal(taskId, deliveredTask!.TaskId);
        Assert.Equal(taskId, worker.CurrentTaskId);
        Assert.True(worker.IsBusy);

        // NOTHING MINTED A SECOND ID anywhere along the chain: every surface observed at stage S
        // carries exactly one distinct value.
        Assert.Single(
            new[]
            {
                slotAtSend.Slot.TaskId, pointerAtSend!, enqueuedTask.TaskId,
                activeQueueEntryAtSend.TaskId, deliveredTask.TaskId, worker.CurrentTaskId!,
            }.Distinct(StringComparer.Ordinal));
    }

    // ── (a) STAGE G — the throw propagates uncaught ──────────────────────────
    //
    // Covered UNMODIFIED by Dispatch_DirectPushFails_PerformsNoRollback above: the
    // ThrowingIdleWorkerGateway's GetIdleWorker throws BEFORE the dequeue, so nothing has been
    // touched and the delivery transaction deliberately does not guard it.

    // ── (b) STAGE A — the non-observable best-effort stage ───────────────────

    /// <summary>
    /// A throwing agents-md send is swallowed by <c>DispatcherMaintenance</c>'s own catch, so
    /// stage A is NOT OBSERVABLE from the delivery transaction: the dispatch PROCEEDS all the way
    /// to the send.
    /// </summary>
    /// <remarks>
    /// The end-to-end assertion is the point: had the transaction wrapped stage A in a recovery of
    /// its own, the task would have been requeued or the failure re-raised and the send would never
    /// have happened.
    /// </remarks>
    [Fact]
    public async Task Delivery_AgentsMdSendThrows_IsNotObservableAndDispatchProceeds()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var worker = CreateIdleWorker();
        var gateway = new DeliveryWorkerGateway(worker)
        {
            AgentsUpdateThrows = new InvalidOperationException("agents-md-sentinel"),
        };
        var service = CreateService(
            manager, queue, logger, workerGateway: gateway, agentsManager: CreateAgentsManager());

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // THE ACTUAL allocated id (the admission stands, so the live slot names it).
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        // The agents-md send WAS attempted and DID throw — the stage really ran.
        Assert.Equal(1, gateway.AgentsUpdateAttempts);
        Assert.Equal([taskId], gateway.SentTaskIds);
        Assert.NotNull(queue.GetActiveTask(taskId));
        Assert.Empty(DrainPending(queue));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains("delivery-", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE STAGE A STRANDING HOLE, closed: the agents-md gateway send FAILS and the maintenance
    /// class's own catch tries to log that failure — with a logger that THROWS. That nested throw
    /// escapes the awaited stage A call at the worst instant (task dequeued, Role reassigned, NOT
    /// yet activated, cancel-check not yet run), so without the boundary containment the dequeued
    /// task is STRANDED by a pure diagnostic failure.
    /// </summary>
    /// <remarks>
    /// This is the vector the earlier stage A test could not reach: it used a non-throwing logger,
    /// and the post-dequeue boundary test configured no <c>AgentsManager</c>, so the maintenance
    /// path returned on empty content before ever reaching its logging catch. Here BOTH conditions
    /// hold at once — a real AgentsManager with content AND a failing gateway send AND a throwing
    /// logger — which is exactly what makes the indirect path live.
    /// </remarks>
    [Fact]
    public async Task Delivery_AgentsMdFailurePathLoggerThrows_IsContainedAndTaskIsNotStranded()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var worker = CreateIdleWorker();
        // The gateway's agents-md send FAILS → DispatcherMaintenance enters its logging catch.
        var gateway = new DeliveryWorkerGateway(worker)
        {
            AgentsUpdateThrows = new InvalidOperationException("agents-md-sentinel"),
        };
        // ...and THAT log throws. The predicate targets the maintenance warning specifically, so
        // the guard-swallowing being proven here is the INDIRECT stage A one, not a blanket one.
        var logger = new SelectivelyThrowingLogger<TaskDispatchService>(
            m => m.StartsWith("Failed to send AGENTS.md to worker", StringComparison.Ordinal));
        var service = CreateService(
            manager, queue, logger, workerGateway: gateway, agentsManager: CreateAgentsManager());

        // (i) THE THROW DOES NOT ESCAPE the dispatch.
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // THE ACTUAL allocated id (the admission stands, so the live slot names it).
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        // The indirect path really was exercised — otherwise this test proves nothing.
        Assert.Equal(1, gateway.AgentsUpdateAttempts);
        Assert.True(logger.ThrewAtLeastOnce, "the maintenance path's logger must actually have thrown");

        // (ii) THE TASK IS NOT STRANDED: the dispatch proceeded THROUGH the cancel-check to the
        // delivery, and the dequeued task reached its destination.
        Assert.Equal([taskId], gateway.MarkedBusyTaskIds);
        Assert.Equal([taskId], gateway.SentTaskIds);
        Assert.NotNull(queue.GetActiveTask(taskId));

        // (iii) It was NOT diverted into the cancel-check's requeue: nothing went back to pending
        // and no recovery record was written.
        Assert.Empty(DrainPending(queue));
        Assert.DoesNotContain(
            logger.SeenMessages, m => m.Contains("delivery-", StringComparison.Ordinal));
    }

    // ── (c) + (d-normal) + (f-normal) THE CANCEL-CHECK REQUEUE, in order ─────
    /// <summary>
    /// THE REQUEUE, steps 1–5 IN ORDER: the Enqueue, then the <c>delivery-recovery</c> guard line,
    /// then the Role-ONLY restore, then the <c>delivery-failure</c> record — and finally the
    /// ORIGINAL <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks>
    /// THE ORDER PROOF is temporal, not post-hoc: the enqueue callback appends its own event to the
    /// same list the logger appends to, and the logger captures the worker's Role AT LOG TIME. So
    /// (1) before (2) is an index comparison, (2) before (3) is "the Role was still the ASSIGNED
    /// value when the guard line was written", and (3) before (4) is "the Role was already the
    /// PRE-MUTATION value when the failure line was written". Moving any step fails the test.
    /// <para>
    /// THE CANCELLATION TRIGGER is the gateway's <c>CancelAtDeliveryStart</c> gate at stage G —
    /// deterministically AFTER the admission committed and enqueued, which is the boundary this
    /// recovery is defined at. (A pre-cancelled caller token is refused by the preparation's
    /// credential resolution before ANY admission, so it can never reach the requeue.)
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Delivery_CancelledBeforeCheck_RequeuesRestoresRoleLogsInOrderAndPropagates()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var events = new List<string>();
        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var queue = new TaskQueue();
        queue.OnEnqueue = t => events.Add($"enqueue:{t.TaskId}");

        var logger = new DeliveryProbingLogger<TaskDispatchService>(events, () => worker.Role);
        using var cts = new CancellationTokenSource();
        var gateway = new DeliveryWorkerGateway(worker) { CancelAtDeliveryStart = cts };
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", cts.Token));

        // The delivery transaction really was entered — the cancellation happened AT its boundary,
        // not before the admission.
        Assert.Equal(1, gateway.IdleWorkerProbes);

        // THE ACTUAL allocated id: the admission stands (the requeue does not retire the slot), so
        // the live slot names it.
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        var guardLine = DeliveryRecoveryMessage(GoalId, taskId);
        var failureLine = DeliveryFailureMessage(
            GoalId, taskId, worker.Id, "cancel-check", "requeue", RequeueOutcome);

        // (1) THE ENQUEUE — the recovery's own re-enqueue is the LAST enqueue event.
        var lastEnqueue = events.FindLastIndex(e => e == $"enqueue:{taskId}");
        var guardIndex = events.IndexOf($"log:{guardLine}");
        var failureIndex = events.IndexOf($"log:{failureLine}");
        Assert.True(lastEnqueue >= 0, "the recovery must re-enqueue the dequeued task");
        Assert.True(guardIndex >= 0, "the guard line must be emitted after a normal Enqueue");
        Assert.True(failureIndex >= 0, "the delivery-failure record must be emitted");
        // (1) before (2) before (4).
        Assert.True(lastEnqueue < guardIndex, "the Enqueue must precede the guard line");
        Assert.True(guardIndex < failureIndex, "the guard line must precede the failure line");

        // (2) before (3): the restore had NOT run when the guard line was written.
        var guardEntry = Assert.Single(logger.Entries, e => e.Message == guardLine);
        Assert.Equal(LogLevel.Debug, guardEntry.Level);
        Assert.Equal(WorkerRole.Coder, guardEntry.RoleAtLog);

        // (3) before (4): the ROLE-ONLY restore had already run when the failure line was written.
        var failureEntry = Assert.Single(logger.Entries, e => e.Message == failureLine);
        Assert.Equal(LogLevel.Warning, failureEntry.Level);
        Assert.Equal(WorkerRole.Tester, failureEntry.RoleAtLog);

        // The settled state: the pre-mutation Role restored, nothing activated, no busy worker.
        Assert.Equal(WorkerRole.Tester, worker.Role);
        Assert.Null(worker.CurrentModel);
        Assert.Empty(gateway.MarkedBusyTaskIds);
        Assert.Empty(gateway.SentTaskIds);
        Assert.Null(queue.GetActiveTask(taskId));
        // THE TASK IS PENDING AGAIN.
        Assert.Equal([taskId], DrainPending(queue));
        // A clean requeue records no rollback-step failure.
        Assert.DoesNotContain(
            logger.Entries, e => e.Message.Contains("delivery-rollback-failure", StringComparison.Ordinal));
    }

    // ── (d-throwing) + (j) THE GUARD LINE'S EXACT EMISSION RULE ──────────────

    /// <summary>
    /// A THROWING <see cref="TaskQueue.OnEnqueue"/> during the recovery's re-enqueue: the task IS
    /// already pending (Enqueue inserts BEFORE invoking the callback), the
    /// <c>delivery-rollback-failure step=re-enqueue</c> record is THE record, the guard line is NOT
    /// emitted, the Role restore still runs, and the ORIGINAL cancellation is rethrown.
    /// </summary>
    /// <remarks>
    /// THE CANCELLATION TRIGGER is the gateway's stage-G gate, so the admission has already
    /// completed and the recovery genuinely operates on ADMITTED work.
    /// </remarks>
    [Fact]
    public async Task Delivery_CancelledAndRecoveryEnqueueThrows_SkipsGuardLineAndContinuesRollback()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("recovery-enqueue-sentinel");
        var queue = new TaskQueue();
        // The FIRST enqueue is the admission's and must succeed; only the recovery's throws.
        var enqueues = 0;
        queue.OnEnqueue = _ =>
        {
            if (++enqueues >= 2)
                throw sentinel;
        };

        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var logger = new TestLogger<TaskDispatchService>();
        using var cts = new CancellationTokenSource();
        var gateway = new DeliveryWorkerGateway(worker) { CancelAtDeliveryStart = cts };
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", cts.Token));

        Assert.Equal(1, gateway.IdleWorkerProbes);

        // THE ACTUAL allocated id — the rollback retained the slot (Abandoned), so the single
        // registered slot names it.
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

        // THE RULE: no guard line, because the Enqueue call did NOT return normally.
        Assert.DoesNotContain(
            logger.LogEntries, e => e.Message == DeliveryRecoveryMessage(GoalId, taskId));
        // The record instead — carrying the step's own exception.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == DeliveryRollbackFailureMessage(GoalId, taskId, "re-enqueue") &&
            ReferenceEquals(e.Exception, sentinel));

        // The remaining steps CONTINUED: the Role restore ran and the failure line was written.
        Assert.Equal(WorkerRole.Tester, worker.Role);
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == DeliveryFailureMessage(
                GoalId, taskId, worker.Id, "cancel-check", "requeue", RequeueOutcome));

        // THE INSERT-BEFORE-CALLBACK SHAPE: the task IS pending despite the callback's throw.
        Assert.Equal([taskId], DrainPending(queue));
        Assert.Null(queue.GetActiveTask(taskId));
    }

    // ── (f) THE ROLE RESTORE — the concurrent-mutation vector ────────────────

    /// <summary>
    /// THE CONCURRENT-MUTATION VECTOR: a third party re-assigns the worker's Role between the
    /// delivery's assignment and the restore's compare, so the restore SKIPS — a live claim is
    /// never overwritten.
    /// </summary>
    /// <remarks>
    /// The mutation is injected from the recovery's own enqueue callback, which runs strictly
    /// between step (1) and step (3). The check-then-write is NOT atomic — this test pins the
    /// common overwrite being avoided, which is exactly what the production comment claims, and
    /// nothing more. The <c>step=role-model-restore</c> catch has NO runtime vector at all:
    /// <see cref="ConnectedWorker.Role"/> is a plain auto-property on a sealed class, so that catch
    /// is a CODE-REVIEW CRITERION (the belt-and-braces structure), not a testable path.
    /// <para>
    /// THE CANCELLATION TRIGGER is the gateway's stage-G gate — after the admission, at the
    /// delivery boundary the recovery is defined at.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Delivery_CancelledAfterConcurrentRoleReassignment_SkipsTheRestore()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var queue = new TaskQueue();
        var enqueues = 0;
        queue.OnEnqueue = _ =>
        {
            // On the RECOVERY's enqueue only: a competitor claims the worker for another role.
            if (++enqueues >= 2)
                worker.Role = WorkerRole.Reviewer;
        };

        var logger = new TestLogger<TaskDispatchService>();
        using var cts = new CancellationTokenSource();
        var gateway = new DeliveryWorkerGateway(worker) { CancelAtDeliveryStart = cts };
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", cts.Token));

        Assert.Equal(1, gateway.IdleWorkerProbes);

        // The competitor's value SURVIVES: the restore refused to write over it.
        Assert.Equal(WorkerRole.Reviewer, worker.Role);

        // THE ACTUAL allocated id — the rollback retained the slot (Abandoned).
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        // The rest of the recovery still ran exactly as usual.
        Assert.Contains(logger.LogEntries, e => e.Message == DeliveryRecoveryMessage(GoalId, taskId));
        Assert.Contains(logger.LogEntries, e =>
            e.Message == DeliveryFailureMessage(
                GoalId, taskId, worker.Id, "cancel-check", "requeue", RequeueOutcome));
        Assert.Equal([taskId], DrainPending(queue));
    }

    // ── (e) STAGE P2 — THE AMBIGUITY-PRESERVE ────────────────────────────────

    /// <summary>
    /// A throwing <see cref="IWorkerGateway.MarkBusy"/> is THE PRESERVE: NO re-enqueue, NO
    /// MarkComplete, the task stays ACTIVE, the <c>stage=prepare recovery=preserve</c> record is
    /// written and the ORIGINAL exception instance is rethrown.
    /// </summary>
    /// <remarks>
    /// THE DEFERRAL NOTE, mirroring the production comment: (a) if the busy mutation HAD been
    /// applied before the throw, the stale-cleanup's busy-task timeout reclaims the task; (b) if it
    /// had NOT, the task is active with an IDLE worker — a shape the stale-cleanup's predicate does
    /// not cover, owned by the ORDERED SUCCESSOR <c>atomic-worker-reservation</c> (the reservation
    /// API plus the idle-worker-with-active-task reconciliation sweep). This goal does not claim
    /// that recovery; it defers it.
    /// </remarks>
    [Fact]
    public async Task Delivery_MarkBusyThrows_PreservesActiveTaskLogsPrepareAndRethrowsOriginal()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("mark-busy-sentinel");
        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var gateway = new DeliveryWorkerGateway(worker) { MarkBusyThrows = sentinel };
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        // THE ACTUAL allocated id — the PRESERVE left the slot live, so it names the id.
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

        // THE PRESERVE: still active, never sent, and NOT put back on the pending queue.
        Assert.NotNull(queue.GetActiveTask(taskId));
        Assert.Empty(gateway.SentTaskIds);
        Assert.Empty(DrainPending(queue));

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == DeliveryFailureMessage(
                GoalId, taskId, worker.Id, "prepare", "preserve", PreserveOutcome));

        // No recovery was attempted at all.
        Assert.DoesNotContain(
            logger.LogEntries, e => e.Message.Contains("delivery-recovery", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.LogEntries, e => e.Message.Contains("delivery-rollback-failure", StringComparison.Ordinal));
    }

    // ── (g) THE POST-DEQUEUE LOGGING BOUNDARY ────────────────────────────────

    /// <summary>
    /// A logger that throws at the assigned-role record — the FIRST log call after the dequeue and
    /// before the activation — must NOT strand the dequeued task: the logging guard swallows it and
    /// the dispatch PROCEEDS to the delivery.
    /// </summary>
    [Fact]
    public async Task Delivery_LoggerThrowsAfterDequeue_IsSwallowedAndDeliveryProceeds()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var worker = CreateIdleWorker();
        var gateway = new DeliveryWorkerGateway(worker);
        // Throws ONLY on the post-dequeue assigned-role record.
        var logger = new SelectivelyThrowingLogger<TaskDispatchService>(
            m => m.StartsWith("Worker ", StringComparison.Ordinal));
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // THE ACTUAL allocated id — the admission stands, so the live slot names it.
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));
        Assert.True(logger.ThrewAtLeastOnce, "the throwing diagnostic must actually have been reached");
        // The dequeued task reached the worker — it was never stranded by the diagnostic failure.
        Assert.Equal([taskId], gateway.SentTaskIds);
        Assert.NotNull(queue.GetActiveTask(taskId));
        Assert.Empty(DrainPending(queue));
    }

    // ── (h) STAGE S — THE PRESERVE ───────────────────────────────────────────

    /// <summary>
    /// An ORDINARY throwing <see cref="IWorkerGateway.SendTaskAsync"/> — THE AMBIGUITY POINT — is
    /// preserved: the task stays active, the worker stays busy, the Role is NOT restored, the
    /// delivered task's pipeline state is untouched, the <c>stage=send recovery=preserve</c> record
    /// is written and the ORIGINAL exception is rethrown.
    /// </summary>
    [Fact]
    public async Task Delivery_SendThrows_PreservesEverythingAndPropagates()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("send-sentinel");
        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var gateway = new DeliveryWorkerGateway(worker) { SendTaskThrows = sentinel };
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        AssertSendPreserve(manager, pipeline, queue, logger, gateway, worker);
    }

    /// <summary>
    /// A CALLER CANCELLATION AT STAGE S takes THE SAME PRESERVE PATH, and the cancellation is
    /// genuinely driven by the CALLER'S TOKEN — not a fabricated tokenless
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks>
    /// THE GENUINENESS PROOF, which a fabricated OCE cannot satisfy: the gateway records the token
    /// it was HANDED at the send point, cancels THAT source, and throws by calling
    /// <c>ThrowIfCancellationRequested</c> ON IT — so the observed exception's
    /// <see cref="OperationCanceledException.CancellationToken"/> IS the caller's token, and that
    /// token was demonstrably cancelled at observation time. Reverting to a tokenless OCE thrown
    /// against a non-cancelled dispatch token fails all three of those assertions.
    /// </remarks>
    [Fact]
    public async Task Delivery_SendObservesCancelledCallerToken_PreservesEverythingAndPropagatesOce()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();

        // The caller's OWN source. It is NOT cancelled up front — that would trip the cancel-check
        // long before stage S; the cancellation must first become observable AT the send point.
        using var cts = new CancellationTokenSource();
        var gateway = new DeliveryWorkerGateway(worker) { CancelCallerTokenAtSend = cts };
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", cts.Token));

        // (i) The exception that left the dispatch is the very instance the gateway threw.
        Assert.NotNull(gateway.ThrownAtSend);
        Assert.Same(gateway.ThrownAtSend, thrown);
        // (ii) It is CALLER-TOKEN-DRIVEN: the OCE carries the caller's token, not CancellationToken.None.
        Assert.Equal(cts.Token, thrown.CancellationToken);
        Assert.NotEqual(CancellationToken.None, thrown.CancellationToken);
        // (iii) The token the gateway was HANDED is the caller's, and it was really cancelled then.
        Assert.Equal(cts.Token, gateway.TokenAtSend);
        Assert.True(gateway.TokenWasCancelledAtSend, "the caller's token must be cancelled at observation time");

        // THE SAME PRESERVE as the ordinary S vector — identical assertions, identical record.
        AssertSendPreserve(manager, pipeline, queue, logger, gateway, worker);
    }

    /// <summary>
    /// The shared stage-S PRESERVE assertions: nothing is undone, the exact record fires, and no
    /// recovery is attempted. Used by BOTH S vectors so the ordinary and caller-cancellation cases
    /// are held to exactly the same standard.
    /// </summary>
    private void AssertSendPreserve(
        GoalPipelineManager manager,
        GoalPipeline pipeline,
        TaskQueue queue,
        TestLogger<TaskDispatchService> logger,
        DeliveryWorkerGateway gateway,
        ConnectedWorker worker)
    {
        // THE ACTUAL allocated id — the send PRESERVE left the slot live, so it names the id.
        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

        // THE PRESERVE: active, busy, model set, Role left on the delivered task's role.
        Assert.NotNull(queue.GetActiveTask(taskId));
        Assert.Equal([taskId], gateway.MarkedBusyTaskIds);
        Assert.True(worker.IsBusy);
        Assert.Equal(WorkerRole.Coder, worker.Role);
        Assert.Equal("coder-model", worker.CurrentModel);
        Assert.Empty(DrainPending(queue));

        // The DELIVERED task's pipeline state is untouched — no rollback of any kind.
        Assert.Equal(WorkSlotState.Pending, SingleSlot(pipeline).State);
        Assert.Same(pipeline, manager.GetByTaskId(taskId));
        Assert.Equal(taskId, pipeline.ActiveTaskId);

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == DeliveryFailureMessage(
                GoalId, taskId, worker.Id, "send", "preserve", PreserveOutcome));
        Assert.DoesNotContain(
            logger.LogEntries, e => e.Message.Contains("delivery-recovery", StringComparison.Ordinal));
    }

    // ── (i) THE MISMATCH HANDOFF ─────────────────────────────────────────────

    /// <summary>
    /// THE ROLE-AWARE FIFO, made observable: pipeline A's EARLIER same-role task is delivered by
    /// pipeline B's push. The <c>delivery-mismatch</c> DEBUG record carries the DELIVERED goal and
    /// BOTH task IDs; a forced send failure preserves the DELIVERED task and names ITS goal in the
    /// <c>delivery-failure</c> record; and B's own admission — its slot, its mapping, its pointer —
    /// is untouched by the recovery.
    /// </summary>
    [Fact]
    public async Task Delivery_DeliversAnotherPipelinesEarlierTask_LogsMismatchAndActsOnDeliveredTaskOnly()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());

        // Pipeline A dispatched first; its coder task sits in the pending queue with no idle worker.
        var pipelineA = manager.CreatePipeline(CreateGoal("goal-a"));
        Arrange(pipelineA, GoalPhase.Coding);
        var queue = new TaskQueue();
        var loggerA = new TestLogger<TaskDispatchService>();
        var serviceA = CreateService(manager, queue, loggerA, goal: CreateGoal("goal-a"));
        await serviceA.DispatchToRole(pipelineA, WorkerRole.Coder, "Code A", TestContext.Current.CancellationToken);

        // THE ACTUAL first id: the admitted slot names it.
        var taskA = SettledTaskId(pipelineA);
        AssertSuffixedTaskId(taskA, TaskIdPrefix("goal-a", WorkerRole.Coder));

        // Pipeline B now dispatches WITH an idle worker — and the FIFO hands it A's task.
        var pipelineB = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipelineB, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("send-sentinel");
        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var loggerB = new TestLogger<TaskDispatchService>();
        var gateway = new DeliveryWorkerGateway(worker) { SendTaskThrows = sentinel };
        var serviceB = CreateService(manager, queue, loggerB, workerGateway: gateway);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => serviceB.DispatchToRole(pipelineB, WorkerRole.Coder, "Code B", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        // THE ACTUAL second id: B's admission stands (its slot is live), so it names the id.
        var taskB = SettledTaskId(pipelineB);
        AssertSuffixedTaskId(taskB, TaskIdPrefix(GoalId, WorkerRole.Coder));
        Assert.NotEqual(taskA, taskB, StringComparer.Ordinal);

        // THE MISMATCH RECORD: the DELIVERED goal, plus BOTH task IDs.
        Assert.Contains(loggerB.LogEntries, e =>
            e.LogLevel == LogLevel.Debug &&
            e.Message == DeliveryMismatchMessage("goal-a", taskB, taskA));

        // THE LOG OWNERSHIP: the failure record names the DELIVERED task's goal, not B's.
        Assert.Contains(loggerB.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == DeliveryFailureMessage(
                "goal-a", taskA, worker.Id, "send", "preserve", PreserveOutcome));

        // The DELIVERED task is the one preserved.
        Assert.NotNull(queue.GetActiveTask(taskA));
        Assert.Equal([taskA], gateway.MarkedBusyTaskIds);

        // B's ADMISSION is untouched: its slot, its mapping, its pointer — and its task is still
        // pending, waiting for the next push.
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipelineB.GetSlotsForTest()).State);
        Assert.Same(pipelineB, manager.GetByTaskId(taskB));
        Assert.Equal(taskB, pipelineB.ActiveTaskId);
        Assert.Equal([taskB], DrainPending(queue));
        // NO pipeline-level operation appears in the recovery: A's admission stands too.
        Assert.Same(pipelineA, manager.GetByTaskId(taskA));
        Assert.Equal(taskA, pipelineA.ActiveTaskId);
    }

    /// <summary>
    /// The MATCHING delivery — the push hands back the very task this dispatch admitted — emits NO
    /// mismatch record. The record is a genuine discriminator, not an unconditional line.
    /// </summary>
    [Fact]
    public async Task Delivery_DeliversOwnTask_LogsNoMismatchRecord()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(
            manager, queue, logger, workerGateway: new DeliveryWorkerGateway(CreateIdleWorker()));

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            logger.LogEntries, e => e.Message.Contains("delivery-mismatch", StringComparison.Ordinal));
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
    /// THE DELIVERY GATEWAY: hands back one fixed idle worker and records every MarkBusy/send it
    /// receives, with an injectable throw for each stage the delivery transaction classifies.
    /// </summary>
    /// <remarks>
    /// <see cref="MarkBusy"/> also applies the REAL busy mutation on the success path (mirroring
    /// <c>WorkerPool.MarkBusy</c>) so the S-stage preserve can assert a genuinely busy worker.
    /// </remarks>
    private sealed class DeliveryWorkerGateway : IWorkerGateway
    {
        private readonly ConnectedWorker _worker;

        public DeliveryWorkerGateway(ConnectedWorker worker) => _worker = worker;

        /// <summary>When set, <see cref="MarkBusy"/> throws it (stage P2).</summary>
        public Exception? MarkBusyThrows { get; init; }

        /// <summary>When set, <see cref="SendTaskAsync"/> throws it (stage S).</summary>
        public Exception? SendTaskThrows { get; init; }

        /// <summary>
        /// When set, <see cref="SendTaskAsync"/> CANCELS this source and then throws by observing
        /// the token it was HANDED — producing a genuine caller-token-driven cancellation at
        /// stage S rather than a fabricated <see cref="OperationCanceledException"/>.
        /// </summary>
        public CancellationTokenSource? CancelCallerTokenAtSend { get; init; }

        /// <summary>The token the gateway was handed at the send point.</summary>
        public CancellationToken TokenAtSend { get; private set; }

        /// <summary>Whether that token was actually cancelled when the send observed it.</summary>
        public bool TokenWasCancelledAtSend { get; private set; }

        /// <summary>The exception the send actually threw, for an identity assertion.</summary>
        public OperationCanceledException? ThrownAtSend { get; private set; }

        /// <summary>When set, <see cref="SendAgentsUpdateAsync"/> throws it (stage A).</summary>
        public Exception? AgentsUpdateThrows { get; init; }

        /// <summary>
        /// THE DELIVERY-BOUNDARY GATE. When set, <see cref="GetIdleWorker"/> — stage G, the FIRST
        /// step of the delivery transaction and therefore strictly AFTER the whole admission
        /// (capture → build → claim → PersistAdmission → enqueue) — cancels this source.
        /// </summary>
        /// <remarks>
        /// This is what makes the cancel-check vectors exercise the ADMITTED-WORK recovery they
        /// assert. Pre-cancelling the caller's token before <c>DispatchToRole</c> no longer
        /// reaches this transaction at all: the preparation's stored-credential resolution
        /// observes the cancellation first and refuses BEFORE any admission, so nothing would be
        /// enqueued to requeue. The gate is a synchronous callback on the dispatch's own thread —
        /// deterministic, with no sleep and no race.
        /// </remarks>
        public CancellationTokenSource? CancelAtDeliveryStart { get; init; }

        /// <summary>Number of idle-worker probes — the proof the delivery transaction was entered.</summary>
        public int IdleWorkerProbes { get; private set; }

        public List<string> MarkedBusyTaskIds { get; } = [];
        public List<string> SentTaskIds { get; } = [];

        /// <summary>Number of agents-md sends attempted — the proof that stage A really ran.</summary>
        public int AgentsUpdateAttempts { get; private set; }

        /// <summary>
        /// THE DELIVERY-TIME OBSERVATION SEAM. When set, it is invoked from INSIDE
        /// <see cref="SendTaskAsync"/> on the SUCCESS path — after the cancel/throw injections and
        /// immediately before the send is recorded — with the very <see cref="WorkTask"/> the
        /// gateway is delivering. Stage S is the LAST step of the delivery transaction, so at that
        /// instant the slot, the active pointer, both mappings and the queue's active entry are ALL
        /// simultaneously live: it is the one point where the whole identity chain can be observed
        /// at once, on the REAL path, with no sleep and no polling. Default <c>null</c>, so every
        /// existing vector is unaffected.
        /// </summary>
        public Action<WorkTask>? OnSendTask { get; init; }

        public ConnectedWorker? GetIdleWorker()
        {
            IdleWorkerProbes++;
            // The admission is COMPLETE by the time stage G runs; cancelling here is observed at
            // the cancel-check, the one provably-safe recovery point.
            CancelAtDeliveryStart?.Cancel();
            return _worker;
        }

        public IReadOnlyList<ConnectedWorker> GetAllWorkers() => [_worker];

        public void MarkBusy(string workerId, string taskId)
        {
            if (MarkBusyThrows is not null)
                throw MarkBusyThrows;

            MarkedBusyTaskIds.Add(taskId);
            _worker.IsBusy = true;
            _worker.CurrentTaskId = taskId;
        }

        public Task SendTaskAsync(string workerId, WorkTask task, CancellationToken ct = default)
        {
            TokenAtSend = ct;

            if (CancelCallerTokenAtSend is not null)
            {
                // The CALLER's own source is cancelled HERE, at the send point — so the dispatch
                // sailed through the cancel-check on a live token and only meets the cancellation
                // at stage S. The throw then comes from OBSERVING the handed token, which is what
                // makes the resulting OCE carry that token.
                CancelCallerTokenAtSend.Cancel();
                TokenWasCancelledAtSend = ct.IsCancellationRequested;
                try
                {
                    ct.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException ex)
                {
                    ThrownAtSend = ex;
                    throw;
                }
            }

            if (SendTaskThrows is not null)
                throw SendTaskThrows;

            // THE DELIVERY-TIME OBSERVATION, taken INSIDE the real send — the instant at which the
            // whole identity chain is simultaneously live (see OnSendTask).
            OnSendTask?.Invoke(task);

            SentTaskIds.Add(task.TaskId);
            return Task.CompletedTask;
        }

        public Task SendCancelAsync(string workerId, string taskId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendAgentsUpdateAsync(string workerId, string role, string content, CancellationToken ct = default)
        {
            AgentsUpdateAttempts++;
            return AgentsUpdateThrows is not null ? throw AgentsUpdateThrows : Task.CompletedTask;
        }
    }

    /// <summary>
    /// A logger that throws ONLY for messages matching a predicate — the targeted vector for the
    /// post-dequeue logging boundary, where an indiscriminate thrower would be indistinguishable
    /// from a pre-dequeue infrastructure failure.
    /// </summary>
    private sealed class SelectivelyThrowingLogger<T> : ILogger<T>
    {
        private readonly Func<string, bool> _shouldThrow;

        public SelectivelyThrowingLogger(Func<string, bool> shouldThrow) => _shouldThrow = shouldThrow;

        /// <summary>True once the throwing branch has actually been taken.</summary>
        public bool ThrewAtLeastOnce { get; private set; }

        /// <summary>Every message the logger was asked to emit, throwing ones included.</summary>
        public List<string> SeenMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            SeenMessages.Add(message);
            if (!_shouldThrow(message))
                return;

            ThrewAtLeastOnce = true;
            throw new InvalidOperationException("delivery-logger-sentinel");
        }
    }

    /// <summary>
    /// A logger that appends every message to a SHARED event list (so log events interleave with
    /// enqueue events on one timeline) and records the worker's Role AT LOG TIME.
    /// </summary>
    /// <remarks>
    /// The Role probe reads the REAL worker instance production is mutating, never a copy, so a
    /// record written before the restore observes the ASSIGNED role and one written after it
    /// observes the PRE-MUTATION role — which is what pins the requeue's step order.
    /// </remarks>
    private sealed class DeliveryProbingLogger<T> : ILogger<T>
    {
        private readonly List<string> _events;
        private readonly Func<WorkerRole> _roleProbe;

        public DeliveryProbingLogger(List<string> events, Func<WorkerRole> roleProbe)
        {
            _events = events;
            _roleProbe = roleProbe;
        }

        public List<(LogLevel Level, string Message, WorkerRole RoleAtLog)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _events.Add($"log:{message}");
            Entries.Add((logLevel, message, _roleProbe()));
        }
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

/// <summary>
/// Invokes a callback at the moment the persisted-pointer clear's statement is issued — the
/// dispatch's E3 step — WITHOUT altering its outcome. The statement proceeds normally, so the
/// observation lands strictly between E2 (the mapping removal) and E4 (the in-memory clear).
/// </summary>
/// <remarks>
/// Targets <c>UPDATE ... pipelines ... active_task_id</c> only, so EF's own pipeline-row writes
/// (the admission's INSERT/UPDATE through the change tracker) are never mistaken for E3.
/// The callback performs read-only work and no re-entrant store call.
/// </remarks>
internal sealed class PipelinesPointerUpdateObserver : DbCommandInterceptor
{
    private readonly Action _onPointerUpdate;

    public PipelinesPointerUpdateObserver(Action onPointerUpdate) => _onPointerUpdate = onPointerUpdate;

    private void ObserveIfTargeted(DbCommand command)
    {
        var text = command.CommandText;
        if (text.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            && text.Contains("pipelines", StringComparison.OrdinalIgnoreCase)
            && text.Contains("active_task_id", StringComparison.OrdinalIgnoreCase))
        {
            _onPointerUpdate();
        }
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        ObserveIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ObserveIfTargeted(command);
        return ValueTask.FromResult(result);
    }
}
