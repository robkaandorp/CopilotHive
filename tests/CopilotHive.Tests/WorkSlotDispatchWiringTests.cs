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
    private readonly List<AdmissionSecondCommitFaultConnection> _secondCommitConnections = [];
    private readonly List<CopilotHiveDbContext> _secondCommitContexts = [];

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
        foreach (var context in _secondCommitContexts)
            context.Dispose();
        foreach (var connection in _connections)
            connection.Dispose();
        foreach (var connection in _secondCommitConnections)
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

    /// <summary>Reads the RAW persisted <c>work_slot_registry_json</c> blob — no EF, no tracker.</summary>
    private string? ReadPersistedRegistryBlob(string goalId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "SELECT work_slot_registry_json FROM pipelines WHERE goal_id = $goalId";
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

    /// <summary>
    /// Forces the persisted <c>pipelines.work_slot_registry_json</c> column to
    /// <paramref name="blob"/> RAW, bypassing EF entirely — used to arrange STALE durable registry
    /// text so the rollback's byte-exact compare-and-swap must refuse.
    /// </summary>
    private void ForcePersistedRegistryBlob(string goalId, string? blob)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "UPDATE pipelines SET work_slot_registry_json = $blob WHERE goal_id = $goalId";
        var blobParameter = command.CreateParameter();
        blobParameter.ParameterName = "$blob";
        blobParameter.Value = (object?)blob ?? DBNull.Value;
        command.Parameters.Add(blobParameter);
        var goalParameter = command.CreateParameter();
        goalParameter.ParameterName = "$goalId";
        goalParameter.Value = goalId;
        command.Parameters.Add(goalParameter);
        command.ExecuteNonQuery();
    }

    /// <summary>Deletes the persisted <c>task_mappings</c> row RAW — the missing-mapping arrangement.</summary>
    private void ForceDeletePersistedMapping(string taskId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "DELETE FROM task_mappings WHERE task_id = $taskId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$taskId";
        parameter.Value = taskId;
        command.Parameters.Add(parameter);
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
    /// <para>
    /// THE ROUTE IS THE INELIGIBLE (LEGACY) ONE ON PURPOSE: this sequence only exists where the
    /// admission carries NO rollback evidence. An ELIGIBLE admission takes the atomic-inverse route
    /// instead, whose own ordering is pinned by the eligible vectors.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Dispatch_EnqueueThrows_AbandonPrecedesUnregisterPrecedesPointerClear()
    {
        // THE INELIGIBLE ROUTE: a persisted row already exists for this goal, so the manager-created
        // replacement pipeline is INELIGIBLE and this admission takes the LEGACY route — no
        // ownership checkpoint and therefore no rollback evidence. That is precisely the path whose
        // (a)→(b)→(c) order these probes pin; the ELIGIBLE route's own atomic inverse is a different
        // sequence, covered by its own vectors.
        var store = CreateStore();
        store.SavePipeline(new GoalPipeline(CreateGoal(GoalId)));
        var manager = new GoalPipelineManager(store, new TestLogger<GoalPipelineManager>());
        var pipeline = WithControlledNonce(manager.CreatePipeline(CreateGoal(GoalId)));
        Assert.False(pipeline.OwnershipCheckpointEligible);
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
    /// <remarks>
    /// THE ROUTE IS THE INELIGIBLE (LEGACY) ONE, because <c>TryUnregisterTask</c> — the step that
    /// owns this partial outcome — is deliberately NOT part of the eligible route's atomic inverse
    /// (it has durable side effects). The persisted row is seeded first so the creation is
    /// ineligible, and the mapping row is then seeded for THIS task so the admission can still
    /// commit it.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EnqueueThrowsAndRowDeleteFails_LogsUnregisterPersistWarning()
    {
        var deleteSentinel = new InvalidOperationException("delete-sentinel");
        var store = CreateStore(new SentinelThrowingInterceptor(deleteSentinel, "DELETE"));
        store.SavePipeline(new GoalPipeline(CreateGoal(GoalId)));
        var manager = new GoalPipelineManager(store, new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.False(pipeline.OwnershipCheckpointEligible);
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

    /// <summary>
    /// THE ROLLBACK USES THE ORIGINAL ADMISSION TOKEN — PROVEN BEHAVIORALLY. An eligible admission
    /// commits; the enqueue callback then lands a LIVE registry mutation on the pipeline (a fresh
    /// Pending slot for another task); and the rollback, which still succeeds, writes back exactly
    /// the ORIGINAL admission's Abandoned replacement — with the live post-capture slot ABSENT from
    /// the durable blob.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: any rollback that derived its expectation from a FRESH live capture
    /// (or a re-encode of the post-capture registry) would mismatch the durable blob's exact text,
    /// so its compare-and-swap would REFUSE — the durable slot would stay <c>Pending</c> and the
    /// confirmed-outcome record would never be emitted. Both assertions fail under that mutant.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailure_LiveMutationAfterCapture_StillRollsBackWithTheOriginalToken()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        string? taskIdAtEntry = null;
        queue.OnEnqueue = t =>
        {
            taskIdAtEntry = t.TaskId;
            // A POST-CAPTURE LIVE MUTATION: a brand-new Pending slot that the admission's frozen
            // token can never contain.
            pipeline.AllocateAttemptAndRegisterSlot(
                "late-live-rollback-task", new WorkSlotPosition(7, GoalPhase.Testing, 1));
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        var taskId = taskIdAtEntry!;
        var durable = WorkSlotRegistryCodec.Decode(ReadPersistedRegistryBlob(GoalId)!);

        // THE ORIGINAL TOKEN WAS USED: the admission's own slot is Abandoned…
        var durableSlot = Assert.Single(durable.Slots, s => s.Slot.TaskId == taskId);
        Assert.Equal(WorkSlotState.Abandoned, durableSlot.State);
        // …while the LIVE post-capture mutation never entered the committed/rolled-back pair.
        Assert.DoesNotContain(durable.Slots, s => s.Slot.TaskId == "late-live-rollback-task");

        // And the rest of the inverse held: pointer cleared, mapping deleted.
        Assert.Null(ReadPersistedActiveTaskId(GoalId));
        Assert.Null(ReadPersistedGoalId(taskId));

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Debug &&
            e.Message.Contains("outcome=committed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A LATER-CHANGED DURABLE BLOB MAKES THE ROLLBACK REFUSE — it never refreshes its expectation
    /// from the row it finds. The enqueue callback overwrites the durable registry text with STALE
    /// content, so the original token's compare-and-swap matches nothing, the whole rollback
    /// transaction is refused, and everything durable is left EXACTLY as it was.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: a rollback that re-read the current blob (or re-encoded the current
    /// memory) would "succeed" against the stale text — the refused-outcome record would be missing
    /// and the durable witnesses would change. Both fail here. THERE IS NO DESTRUCTIVE FALLBACK: the
    /// pointer, the mapping and the stale blob all survive.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailure_StaleDurableBlob_RefusesWithoutRepair()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        // STALE TEXT, written by the callback: deliberately JSON-shaped but NOT the admitted text.
        var staleBlob = WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot([], []));

        queue.OnEnqueue = t =>
        {
            // The admission's own row text at entry (the state the CAS would have matched)…
            ForcePersistedRegistryBlob(GoalId, staleBlob);
            // …is replaced before the rollback runs. The actual id is captured for the assertions.
            throw sentinel;
        };

        // The settled id, resolved from the durable text the CALLBACK replaced — so read it first
        // through the in-memory registry, which the rollback settles regardless of the store outcome.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        var taskId = SettledTaskId(pipeline);

        // THE DURABLE WITNESSES ARE EXACTLY AS THE CALLBACK LEFT THEM — no destructive fallback ran.
        Assert.Equal(staleBlob, ReadPersistedRegistryBlob(GoalId));
        Assert.False(string.IsNullOrEmpty(ReadPersistedActiveTaskId(GoalId)));
        Assert.Equal(GoalId, ReadPersistedGoalId(taskId));

        // The outcome is reported HONESTLY as a refusal, never as a success.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("outcome=refused", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("outcome=committed", StringComparison.Ordinal));

        // THE MEMORY SETTLEMENT STILL HAPPENED: abandoned, unmapped, if-current-cleared.
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// A MISSING DURABLE MAPPING REFUSES THE BETWEEN-STATEMENT ROLLBACK: the callback deletes the
    /// mapping row, so the rollback's second statement matches nothing and the preceding UPDATE is
    /// rolled back — the durable pointer and registry text stay exactly as admitted.
    /// </summary>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailure_MissingDurableMapping_RefusesAndRollsTheUpdateBack()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        string? taskIdAtEntry = null;
        string? blobAtEntry = null;
        queue.OnEnqueue = t =>
        {
            taskIdAtEntry = t.TaskId;
            blobAtEntry = ReadPersistedRegistryBlob(GoalId);
            ForceDeletePersistedMapping(t.TaskId);
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        var taskId = SettledTaskId(pipeline);
        Assert.Equal(taskId, taskIdAtEntry);

        // THE BETWEEN-STATEMENT ROLLBACK: the pointer and the admitted blob are UNCHANGED.
        Assert.Equal(taskId, ReadPersistedActiveTaskId(GoalId));
        Assert.Equal(blobAtEntry, ReadPersistedRegistryBlob(GoalId));

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("outcome=refused", StringComparison.Ordinal));

        // Memory is still settled.
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(manager.GetByTaskId(taskId));
    }

    /// <summary>
    /// A FOREIGN DURABLE MAPPING (the same task id owned by a NEWER goal) REFUSES the rollback
    /// without stealing the row, and the newer owner's mapping survives untouched.
    /// </summary>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailure_ForeignDurableMapping_RefusesWithoutStealing()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        string? taskIdAtEntry = null;
        string? blobAtEntry = null;
        queue.OnEnqueue = t =>
        {
            taskIdAtEntry = t.TaskId;
            blobAtEntry = ReadPersistedRegistryBlob(GoalId);
            // Re-point the mapping at a FOREIGN, newer goal through the store's own seeding path.
            using var command = _keeper.CreateCommand();
            command.CommandText = "UPDATE task_mappings SET goal_id = 'newer-foreign-goal' WHERE task_id = $taskId";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$taskId";
            parameter.Value = t.TaskId;
            command.Parameters.Add(parameter);
            command.ExecuteNonQuery();
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        var taskId = SettledTaskId(pipeline);
        Assert.Equal(taskId, taskIdAtEntry);

        // THE FOREIGN OWNER SURVIVES — never a steal — and the preceding UPDATE was rolled back.
        Assert.Equal("newer-foreign-goal", ReadPersistedGoalId(taskId));
        Assert.Equal(taskId, ReadPersistedActiveTaskId(GoalId));
        Assert.Equal(blobAtEntry, ReadPersistedRegistryBlob(GoalId));

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("outcome=refused", StringComparison.Ordinal));
    }

    /// <summary>
    /// A NEWER DURABLE POINTER REFUSES THE ROLLBACK: the callback moves the durable pointer to a
    /// different task, so the ownership-checked rollback declines and the newer pointer survives.
    /// </summary>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailure_NewerDurablePointer_RefusesAndLeavesItIntact()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        string? blobAtEntry = null;
        queue.OnEnqueue = _ =>
        {
            blobAtEntry = ReadPersistedRegistryBlob(GoalId);
            ForcePersistedActiveTaskId(GoalId, "newer-durable-task");
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        // THE NEWER DURABLE POINTER SURVIVES and the admitted blob is unchanged (the UPDATE rolled back).
        Assert.Equal("newer-durable-task", ReadPersistedActiveTaskId(GoalId));
        Assert.Equal(blobAtEntry, ReadPersistedRegistryBlob(GoalId));

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("outcome=refused", StringComparison.Ordinal));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (7c) THE ELIGIBLE ROLLBACK'S OWN OWNERSHIP GUARDS — the skips
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A REPLACED PIPELINE INSTANCE PRODUCES A SKIP: by the time the enqueue fails, the manager's
    /// current instance for the goal is a DIFFERENT pipeline, so the rollback is skipped ENTIRELY —
    /// no store write, the ownership intact and the mapping row preserved.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: dropping the current-instance/reference check lets the stale
    /// pipeline's rollback run against the replaced pipeline's durable row — the durable witnesses
    /// would change and the skipped-outcome record would be replaced by a committed/refused one.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailureAfterPipelineReplacement_SkipsWithOwnershipIntact()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        string? taskIdAtEntry = null;
        string? blobAtEntry = null;
        queue.OnEnqueue = t =>
        {
            taskIdAtEntry = t.TaskId;
            blobAtEntry = ReadPersistedRegistryBlob(GoalId);
            // THE REPLACEMENT: swap the manager's CURRENT instance for the goal with a different
            // one, WITHOUT the durable side effect of RemovePipeline (the row must stay intact for
            // the "no store write happened" assertions below).
            var pipelinesField = typeof(GoalPipelineManager)
                .GetField("_pipelines", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pipelines = (ConcurrentDictionary<string, GoalPipeline>)pipelinesField.GetValue(manager)!;
            pipelines[GoalId] = new GoalPipeline(CreateGoal(GoalId));
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        var taskId = taskIdAtEntry!;

        // THE SKIP: no store write at all — the durable pair is EXACTLY as admitted…
        Assert.Equal(taskId, ReadPersistedActiveTaskId(GoalId));
        Assert.Equal(blobAtEntry, ReadPersistedRegistryBlob(GoalId));
        // …and the outcome is reported as a SKIP, never as a commitment or a refusal.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Debug &&
            e.Message.Contains("outcome=skipped", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("outcome=committed", StringComparison.Ordinal) ||
            e.Message.Contains("outcome=refused", StringComparison.Ordinal));

        // NO FALSE SLOT-RELEASE CLAIM: a skipped rollback mutates NOTHING, so the
        // abandoned-registration record — whose text asserts "the slot is released" — must be
        // ABSENT. Asserting the template's ABSENCE (not merely the skip record's presence) is what
        // kills an unconditional release record sitting next to outcome=skipped.
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("abandoned-registration", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("the slot is released", StringComparison.Ordinal));

        // The skipped route performs NO memory settlement of its own, so the stale pipeline's own
        // pointer is left exactly as the admission set it — nothing was invented or repaired.
        Assert.Equal(taskId, pipeline.ActiveTaskId);
    }

    /// <summary>
    /// A FOREIGN MEMORY MAPPING PRODUCES A SKIP: the callback re-points the task at another goal
    /// (without the pipeline being replaced), so the ownership guard declines and no rollback write
    /// is attempted — the other owner's memory mapping and the durable row both survive.
    /// </summary>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailureAfterMemoryMappingSteal_SkipsWithForeignMappingIntact()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        manager.CreatePipeline(CreateGoal("goal-other"));
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        string? taskIdAtEntry = null;
        string? blobAtEntry = null;
        queue.OnEnqueue = t =>
        {
            taskIdAtEntry = t.TaskId;
            blobAtEntry = ReadPersistedRegistryBlob(GoalId);
            // THE STEAL, in the same two steps a real competitor uses.
            manager.UnregisterTask(t.TaskId);
            manager.RegisterTask(t.TaskId, "goal-other");
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        var taskId = SettledTaskId(pipeline);
        Assert.Equal(taskId, taskIdAtEntry);

        // THE FOREIGN OWNER SURVIVES; no rollback write ran.
        Assert.Equal("goal-other", manager.GetByTaskId(taskId)?.GoalId);
        Assert.Equal(taskId, ReadPersistedActiveTaskId(GoalId));
        Assert.Equal(blobAtEntry, ReadPersistedRegistryBlob(GoalId));
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Debug &&
            e.Message.Contains("outcome=skipped", StringComparison.Ordinal));

        // NO FALSE SLOT-RELEASE CLAIM on this skip either — the release template must be ABSENT.
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("abandoned-registration", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("the slot is released", StringComparison.Ordinal));
    }

    /// <summary>
    /// AN ABANDON-WINNING CASE REJECTS SUBSEQUENT COMPLETION ADMISSION: the rollback's atomic
    /// Pending → Abandoned fence is what the memory settlement records, so a completion arriving
    /// afterwards is refused by <see cref="GoalPipeline.AdmitCompletion"/> with
    /// <see cref="AdmissionOutcome.SlotAbandoned"/> — the attempt is genuinely retired.
    /// </summary>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailureAfterRollback_SlotIsFencedAgainstLaterCompletion()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue { OnEnqueue = _ => throw sentinel };
        var service = CreateService(manager, queue, new TestLogger<TaskDispatchService>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        var taskId = SettledTaskId(pipeline);

        // THE FENCE: the abandon already happened atomically, so the later completion is refused
        // (never Claimed) — the slot cannot be re-admitted by a racing completion.
        Assert.Equal(AdmissionOutcome.SlotAbandoned, pipeline.AdmitCompletion(taskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (7d) THE ELIGIBLE ROLLBACK'S STORE OUTCOMES — evidence preservation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A THROWING STORE COMMIT ON THE ROLLBACK (the SECOND explicit transaction on this connection):
    /// the rollback's fate is UNKNOWN, so the dispatch reports <c>outcome=indeterminate</c> carrying
    /// the store's EXACT commit sentinel, the memory settlement still completes, and the ORIGINAL
    /// enqueue exception is what leaves the dispatch — never the store's sentinel. NO durable
    /// fallback runs afterwards: the rollback's own two statements are the LAST durable writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE SECOND-COMMIT LATCH IS WHAT MAKES THIS DETERMINISTIC: the connection lets the ADMISSION's
    /// commit (the first explicit transaction) land normally, so the admission carries real
    /// invocation-local evidence, and throws ONLY at the rollback's commit.
    /// </para>
    /// <para>
    /// THE DURABLE CLAIM IS SPLIT HONESTLY. With <paramref name="throwAfterUnderlyingCommit"/> the
    /// SQLite commit may have landed underneath, so the row's CONTENT is deliberately left
    /// UNRESOLVED and nothing in the dispatch may claim otherwise. With the throw BEFORE the
    /// underlying commit the transaction really was rolled back, so the admitted witnesses (pointer,
    /// registry blob and mapping row) MUST all survive unchanged — that arm is asserted in full,
    /// matching the body-error vector's proof style. Both arms assert the STATEMENT-LEVEL
    /// no-follow-up-write rule, so a legacy delete/clear/save fallback after an indeterminate
    /// outcome is visible either way.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_EligibleEnqueueFailure_RollbackCommitThrows_IndeterminateWithExactEvidence(
        bool throwAfterUnderlyingCommit)
    {
        var goalId = GoalId + "-rollback-commit-" + throwAfterUnderlyingCommit;
        var connection = new AdmissionSecondCommitFaultConnection(_connectionString, throwAfterUnderlyingCommit);
        connection.Open();
        _secondCommitConnections.Add(connection);
        // THE STATEMENT RECORDER: armed at the enqueue callback, so it captures EXACTLY the
        // rollback-time statements — the no-durable-fallback witness.
        var recorder = new AdmissionCommandCounter();
        var context = new CopilotHiveDbContext(
            new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite((DbConnection)connection)
                .AddInterceptors(recorder).Options);
        _secondCommitContexts.Add(context);
        var manager = new GoalPipelineManager(
            new PipelineStore(context, NullLogger<PipelineStore>.Instance),
            new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(goalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var enqueueSentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        string? taskIdAtEntry = null;
        string? blobAtEntry = null;
        queue.OnEnqueue = t =>
        {
            taskIdAtEntry = t.TaskId;
            blobAtEntry = ReadPersistedRegistryBlob(goalId);
            recorder.Start();
            throw enqueueSentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE ORIGINAL EXCEPTION LEFT THE DISPATCH — never the store's sentinel.
        Assert.Same(enqueueSentinel, thrown);
        // THE ADMISSION COMMITTED AND THE ROLLBACK'S COMMIT WAS REALLY ATTEMPTED…
        Assert.True(connection.FirstCommitCount >= 1, "the admission's own commit must have been attempted");
        Assert.Equal(1, connection.SecondCommitCount);
        // …so this really is the throwing-rollback-commit vector, not a mis-arranged no-op.
        Assert.True(connection.SecondCommitAttempted, "the rollback's own commit must have been attempted");

        // THE HONEST OUTCOME: indeterminate, never a false success.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("outcome=indeterminate", StringComparison.Ordinal) &&
            ReferenceEquals(e.Exception, connection.CommitSentinel));
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("outcome=committed", StringComparison.Ordinal));

        // ── THE NO-DURABLE-FALLBACK WITNESS (statement level) ──
        // The rollback's OWN transaction issues exactly its two guarded statements: the CAS update
        // and the mapping delete. NOTHING further — no fallback mapping delete, no pointer clear,
        // no unconditional save — may follow an indeterminate outcome.
        var writes = recorder.Commands
            .Where(c => c.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || c.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, writes.Count);
        Assert.Single(writes, c =>
            c.Contains("work_slot_registry_json", StringComparison.Ordinal) &&
            c.Contains("active_task_id = NULL", StringComparison.Ordinal));
        Assert.Single(writes, c =>
            c.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) &&
            c.Contains("task_mappings", StringComparison.Ordinal));

        var taskId = taskIdAtEntry!;
        if (!throwAfterUnderlyingCommit)
        {
            // THE THROW PRECEDED THE UNDERLYING COMMIT: the transaction really rolled back, so the
            // admitted durable witnesses ALL survive exactly as the failing invocation left them.
            Assert.Equal(taskId, ReadPersistedActiveTaskId(goalId));
            Assert.Equal(blobAtEntry, ReadPersistedRegistryBlob(goalId));
            Assert.Equal(goalId, ReadPersistedGoalId(taskId));
        }

        // THE LOCAL SETTLEMENT STILL COMPLETED: abandoned, unmapped, if-current-cleared.
        Assert.Equal(taskId, Assert.Single(pipeline.GetSlotsForTest()).Slot.TaskId);
        Assert.Equal(WorkSlotState.Abandoned, Assert.Single(pipeline.GetSlotsForTest()).State);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// A THROWING TRANSACTION ROLLBACK ON THE PENDING-ADMISSION ROLLBACK: the store records
    /// <c>Indeterminate</c> carrying its EXACT rollback exception (with a <c>null</c> primary,
    /// because the body itself refused cleanly), and BOTH evidence slots reach the dispatch's
    /// diagnostic. The ORIGINAL enqueue exception is still what leaves the dispatch, unchanged and
    /// unaggregated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MUTATION THIS KILLS: dropping the rollback evidence when there is NO primary. On this
    /// vector the primary is <c>null</c> — a clean body refusal whose ROLLBACK then threw — so a
    /// Failure-only report emits an indeterminate warning carrying NO exception evidence at all,
    /// and rendering the instance as type/message text would destroy it at the diagnostic boundary.
    /// The identity assertion below fails under either mutant.
    /// </para>
    /// <para>
    /// WHY THIS VECTOR IS NOT REDUNDANT with the dual-non-null vector that follows: only THIS one
    /// pins the null-primary half of the contract — that the outcome record's exception argument is
    /// null EXACTLY when there is no primary failure, while the rollback instance is still retained
    /// by its own record.
    /// </para>
    /// <para>
    /// THE ARRANGEMENT IS REAL AND DETERMINISTIC: the existing second-commit fault connection lets
    /// the ADMISSION's transaction commit normally (so the admission produces genuine
    /// invocation-local evidence), and the rollback fault is armed from the enqueue callback so ONLY
    /// the pending-admission rollback's own transaction can throw at <c>Rollback()</c>. A stale
    /// durable blob makes the store's guarded body REFUSE — which is exactly the path whose
    /// confirmed rollback is then faulted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailure_RollbackTransactionRollbackThrows_ReportsBothExactInstances()
    {
        var goalId = GoalId + "-rollback-rollback-throws";
        var connection = new AdmissionSecondCommitFaultConnection(_connectionString, throwAfterCommit: false);
        connection.Open();
        _secondCommitConnections.Add(connection);
        var context = new CopilotHiveDbContext(
            new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite((DbConnection)connection).Options);
        _secondCommitContexts.Add(context);
        var manager = new GoalPipelineManager(
            new PipelineStore(context, NullLogger<PipelineStore>.Instance),
            new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(goalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var enqueueSentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        // A STALE durable blob makes the store's own guarded body refuse CLEANLY (primary == null);
        // the faulted transaction Rollback() is then the ONLY exception the store captures.
        var staleBlob = WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot([], []));
        queue.OnEnqueue = _ =>
        {
            ForcePersistedRegistryBlob(goalId, staleBlob);
            connection.ArmRollbackFault();
            throw enqueueSentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE ORIGINAL EXCEPTION LEFT THE DISPATCH — never a store sentinel, never a wrapper.
        Assert.Same(enqueueSentinel, thrown);
        Assert.NotSame(connection.RollbackSentinel, thrown);
        Assert.Null(thrown.InnerException);

        // THE VACUITY GUARD: the transaction rollback really was faulted.
        Assert.Equal(1, connection.RollbackFaultCount);

        // ── BOTH EVIDENCE SLOTS REACH THE DIAGNOSTIC ──
        // (1) THE OUTCOME RECORD: the primary is NULL here (a clean body refusal), so its exception
        // argument must be null EXACTLY — that is the null-primary half of the contract, which the
        // dual-non-null vector below cannot pin.
        var record = Assert.Single(
            logger.LogEntries,
            e => e.Message.Contains("outcome=indeterminate", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, record.LogLevel);
        Assert.Null(record.Exception);
        Assert.Contains("primary=none", record.Message, StringComparison.Ordinal);

        // (2) THE ROLLBACK-EVIDENCE RECORD: the EXACT instance, asserted by OBJECT IDENTITY. A
        // type/message rendering is NOT sufficient — reducing the instance to text destroys it at
        // the diagnostic boundary, which is precisely the defect this assertion kills.
        Assert.Contains(logger.LogEntries, e =>
            e.Message.Contains("pending-admission-rollback", StringComparison.Ordinal) &&
            ReferenceEquals(e.Exception, connection.RollbackSentinel));

        // …and it is never reported as a success.
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("outcome=committed", StringComparison.Ordinal));

        // THE LOCAL SETTLEMENT STILL COMPLETED: abandoned, unmapped, if-current-cleared.
        var taskId = Assert.Single(pipeline.GetSlotsForTest()).Slot.TaskId;
        Assert.Equal(WorkSlotState.Abandoned, Assert.Single(pipeline.GetSlotsForTest()).State);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// A ROLLBACK BODY ERROR FOLLOWED BY A THROWING TRANSACTION ROLLBACK produces an
    /// <c>Indeterminate</c> result with TWO non-null exception instances. The dispatch diagnostic
    /// must carry BOTH exact objects — not merely the primary object plus a type/message rendering
    /// of the rollback object — while the ORIGINAL enqueue exception still leaves unchanged.
    /// </summary>
    /// <remarks>
    /// This is the dispatch-level counterpart to
    /// <c>CommitPendingAdmissionRollback_BodyErrorAndRollbackThrow_IndeterminateWithBothExactInstances</c>.
    /// The existing null-primary rollback-throw vector cannot prove preservation of two identities:
    /// rendering the rollback type/message passes it while silently dropping the actual instance.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailure_RollbackBodyAndTransactionRollbackThrow_DiagnosticCarriesBothExactInstances()
    {
        var goalId = GoalId + "-rollback-body-and-rollback-throw";
        var bodySentinel = new InvalidOperationException("pending-rollback-body-primary-sentinel");
        var bodyFault = new PendingRollbackThrowInterceptor(bodySentinel);
        var connection = new AdmissionSecondCommitFaultConnection(_connectionString, throwAfterCommit: false);
        connection.Open();
        _secondCommitConnections.Add(connection);
        var context = new CopilotHiveDbContext(
            new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite((DbConnection)connection)
                .AddInterceptors(bodyFault)
                .Options);
        _secondCommitContexts.Add(context);
        var manager = new GoalPipelineManager(
            new PipelineStore(context, NullLogger<PipelineStore>.Instance),
            new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(goalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var enqueueSentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);
        queue.OnEnqueue = _ =>
        {
            // Arm AFTER the real eligible admission committed, so the rollback's CAS body throws and
            // its transaction rollback then throws independently.
            bodyFault.Arm();
            connection.ArmRollbackFault();
            throw enqueueSentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // The original enqueue exception remains the sole propagated exception.
        Assert.Same(enqueueSentinel, thrown);
        Assert.Null(thrown.InnerException);

        // VACUITY GUARDS: both distinct rollback faults actually occurred.
        Assert.Equal(1, bodyFault.ThrowCount);
        Assert.Equal(1, connection.RollbackFaultCount);
        Assert.NotSame(bodySentinel, connection.RollbackSentinel);

        // BOTH exact instances must be carried by the diagnostic surface. A type/message-only
        // rendering of the rollback sentinel is insufficient and fails the second identity check.
        Assert.Contains(logger.LogEntries, e =>
            e.Message.Contains("outcome=indeterminate", StringComparison.Ordinal) &&
            ReferenceEquals(e.Exception, bodySentinel));
        Assert.Contains(logger.LogEntries, e =>
            e.Message.Contains("pending-admission-rollback", StringComparison.Ordinal) &&
            ReferenceEquals(e.Exception, connection.RollbackSentinel));

        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("outcome=committed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A STORE CALL THAT THROWS BEFORE IT COULD RECORD AN OUTCOME — a body error whose transaction
    /// the store's own guarded rollback CONFIRMED — reports <c>outcome=failed</c> at the dispatch
    /// carrying the store's EXACT exception, with the memory settlement still completed and the
    /// ORIGINAL enqueue exception preserved. NO fallback durable cleanup runs: the durable
    /// witnesses (pointer, blob, mapping row) survive exactly as the failing rollback left them.
    /// </summary>
    /// <remarks>
    /// THE SECOND-COMMIT LATCH IS WHAT MAKES THIS DETERMINISTIC: the connection lets the ADMISSION's
    /// commit land normally (real invocation-local evidence) and the interceptor then throws at the
    /// ROLLBACK's own CAS statement — a body error, distinct from the commit-throws vector above.
    /// Collapsing <c>Failed</c> into <c>Skipped</c> or <c>Indeterminate</c>, or reporting a false
    /// success, fails the outcome assertions; a fallback mapping delete or pointer clear changes
    /// the durable witnesses.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailure_RollbackBodyErrorWithConfirmedRollback_FailedWithExactEvidence()
    {
        var goalId = GoalId + "-rollback-body-failed";
        var sentinel = new InvalidOperationException("pending-rollback-body-sentinel");
        var interceptor = new PendingRollbackThrowInterceptor(sentinel);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        _connections.Add(connection);
        var context = new CopilotHiveDbContext(
            new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite((DbConnection)connection)
                .AddInterceptors(interceptor)
                .Options);
        _contexts.Add(context);
        var manager = new GoalPipelineManager(
            new PipelineStore(context, NullLogger<PipelineStore>.Instance),
            new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(goalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var enqueueSentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        string? taskIdAtEntry = null;
        string? blobAtEntry = null;
        queue.OnEnqueue = t =>
        {
            taskIdAtEntry = t.TaskId;
            blobAtEntry = ReadPersistedRegistryBlob(goalId);
            // Arm AFTER the admission's checkpoint was observed durable — only the ROLLBACK's own
            // statement can be targeted from here.
            interceptor.Arm();
            throw enqueueSentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE ORIGINAL EXCEPTION LEFT THE DISPATCH — never the store's sentinel.
        Assert.Same(enqueueSentinel, thrown);

        var taskId = taskIdAtEntry!;
        // THE VACUITY GUARD: the rollback's CAS really threw exactly once, and NO follow-up durable
        // UPDATE/DELETE ran afterwards — no mapping-delete fallback, no pointer clear, no save.
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.Empty(interceptor.StatementsAfterThrow);

        // THE HONEST OUTCOME: failed, with the store's EXACT exception — never a false success.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("outcome=failed", StringComparison.Ordinal) &&
            e.Message.Contains("pending-admission-rollback", StringComparison.Ordinal) &&
            ReferenceEquals(e.Exception, sentinel));
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("outcome=committed", StringComparison.Ordinal));

        // THE DURABLE WITNESSES ARE EXACTLY AS THE FAILING INVOCATION LEFT THEM.
        Assert.Equal(taskId, ReadPersistedActiveTaskId(goalId));
        Assert.Equal(blobAtEntry, ReadPersistedRegistryBlob(goalId));
        Assert.Equal(goalId, ReadPersistedGoalId(taskId));

        // THE LOCAL SETTLEMENT STILL COMPLETED: abandoned, unmapped, if-current-cleared.
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// A MANAGER ROLLBACK THAT THROWS IS CONTAINED: the dispatch's guarded route swallows the escape
    /// (recording <c>step=pending-admission</c>), the remaining settlement still runs, and the
    /// ORIGINAL enqueue exception is rethrown BARE.
    /// </summary>
    /// <remarks>
    /// THE SEAM: a fault-commit connection whose commit throws for the admission too, so the
    /// admission itself is uncertain — this vector's purpose is only to prove that NOTHING a
    /// rollback does can replace the enqueue exception or skip the settlement.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailure_RollbackReportingThrows_OriginalEnqueueExceptionIsPreserved()
    {
        // A logger that throws at the rollback-outcome record — the guarded diagnostic seam.
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var enqueueSentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue { OnEnqueue = _ => throw enqueueSentinel };
        var logger = new SelectivelyThrowingLogger<TaskDispatchService>(
            m => m.Contains("pending-admission-rollback", StringComparison.Ordinal));
        var service = CreateService(manager, queue, logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE TWO-EXCEPTION DISTINCTION: the logger's throw was swallowed and the ORIGINAL left.
        Assert.Same(enqueueSentinel, thrown);
        Assert.True(logger.ThrewAtLeastOnce, "the rollback-outcome record's logger must have thrown");

        // THE SETTLEMENT STILL RAN: abandoned, unmapped, if-current-cleared, and the slot-release
        // record was emitted after the swallowed throw.
        var taskId = SettledTaskId(pipeline);
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Contains(logger.SeenMessages, m => m == AbandonedRegistrationMessage(GoalId, taskId, 1, GoalPhase.Coding, 1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (7e) THE EVIDENCE-ROUTE ORDERING — the manager's capture-through-settlement span
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE MANAGER LOCK IS HELD THROUGH THE ROLLBACK'S STORE WORK <b>AND ITS LOCAL SETTLEMENT</b>.
    /// The rollback is first parked inside its own SQL (the manager's mapping monitor held), and is
    /// then WEDGED inside its SETTLEMENT phase by holding the pipeline's monitor — the monitor
    /// <c>ClearActiveTaskIfCurrent</c> must acquire. While it is wedged there, a concurrent DISJOINT
    /// manager save CANNOT complete: the settlement runs INSIDE the <c>_mappingLock</c> span.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MUTATION THIS KILLS: moving <c>_taskToGoal.TryRemove</c> and
    /// <c>ClearActiveTaskIfCurrent</c> OUTSIDE the lock span. The earlier bounded non-completion
    /// (while SQL is gated) cannot see that, and a bare "observe at the competitor's entry" probe
    /// only RACES two field writes. The WEDGE removes the race entirely: the rollback is held in its
    /// settlement phase for as long as the test wants, so the disjoint save's completion becomes a
    /// deterministic discriminator — with the settlement inside the lock it must stay blocked; with
    /// the settlement outside it, the lock has already been released and the save completes.
    /// </para>
    /// <para>
    /// THE WEDGE IS SOUND because the pipeline monitor is provably RELEASED during the store work
    /// (asserted below via a cross-thread probe), so taking it while the rollback is parked in SQL
    /// cannot deadlock the arrangement. The disjoint pipeline is a DIFFERENT instance with its own
    /// monitor, so its save is never blocked by the wedge itself — only by the manager lock. Every
    /// wait is bounded (a timeout IS the failure), there are no sleeps and no reentrant callbacks.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Dispatch_EligibleRollback_HoldsTheMappingLockThroughStoreWorkAndSettlement()
    {
        const string disjointGoalId = "goal-serial-disjoint";
        var gate = new PendingRollbackGateInterceptor();
        var manager = new GoalPipelineManager(CreateStore(gate), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        // A DISJOINT eligible pipeline — its OWN instance, hence its own monitor — that the
        // concurrent save will checkpoint.
        var disjoint = manager.CreatePipeline(CreateGoal(disjointGoalId));
        disjoint.AllocateAttemptAndRegisterSlot("serial-disjoint-task", new WorkSlotPosition(1, GoalPhase.Coding, 1));
        disjoint.SetActiveTask("serial-disjoint-task");

        var enqueueSentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        // THE WEDGE HANDLE: the ROLLED-BACK pipeline's own private monitor, taken by reflection —
        // the same established direct-monitor pattern the mapping-surface contention vector uses.
        var pipelineLock = typeof(GoalPipeline)
            .GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pipeline);
        Assert.NotNull(pipelineLock);

        var wedgeHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wedgeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wedge = new Thread(() =>
        {
            lock (pipelineLock!)
            {
                wedgeHeld.SetResult();
                wedgeRelease.Task.GetAwaiter().GetResult();
            }
        })
        { IsBackground = true, Name = "settlement-wedge" };

        // Arm the gate AFTER setup: only the rollback's registry UPDATE can block.
        gate.Arm();
        string? taskIdAtEntry = null;
        queue.OnEnqueue = t =>
        {
            // The rollback is now due; the gate will park it inside the store call.
            taskIdAtEntry = t.TaskId;
            throw enqueueSentinel;
        };

        var dispatch = Task.Factory.StartNew(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        Task? disjointSave = null;
        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // THE PIPELINE MONITOR IS RELEASED DURING THE STORE WORK. This probe runs on ANOTHER
            // thread while the dispatch is parked inside the rollback's store call; if the pipeline
            // monitor were held across that work, the probe would BLOCK instead of completing — so
            // it is evaluated under a bounded wait (a timeout IS the failure, never a hang). A
            // same-thread probe could not prove this, because Monitor is reentrant per thread.
            var probe = Task.Factory.StartNew(
                () => Assert.Single(pipeline.GetSlotsForTest()).State,
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var probedState = await probe.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            // …and the FENCE had already happened BEFORE the store work: the slot reads Abandoned.
            Assert.Equal(WorkSlotState.Abandoned, probedState);

            // THE MAPPING AND THE POINTER ARE STILL OURS while the store work is parked — so the
            // settlement assertions below are real state CHANGES, not values that were never set.
            Assert.Same(pipeline, manager.GetByTaskId(taskIdAtEntry!));
            Assert.Equal(taskIdAtEntry, pipeline.ActiveTaskId);

            // TAKE THE WEDGE (safe: the pipeline monitor is free, as just proven), then let the
            // store work finish. The rollback proceeds into its SETTLEMENT and blocks there, on
            // ClearActiveTaskIfCurrent — still holding the manager lock.
            wedge.Start();
            await wedgeHeld.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            gate.Release();

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disjointSave = Task.Factory.StartNew(
                () =>
                {
                    started.SetResult();
                    manager.PersistState(disjoint);
                },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // ── THE SETTLEMENT-SPAN PROOF ──
            // The rollback is WEDGED inside its settlement. A disjoint manager save — which needs
            // only the manager lock and touches an unrelated pipeline and row — must NOT complete,
            // because the settlement is still inside the _mappingLock span. Under the mutant that
            // settles OUTSIDE the lock, the lock is already free here and this save completes.
            await Assert.ThrowsAsync<TimeoutException>(
                () => disjointSave.WaitAsync(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken));
            Assert.False(disjointSave.IsCompleted);
            Assert.Null(ReadPersistedRegistryBlob(disjointGoalId));

            // The dispatch itself is likewise still in flight — wedged mid-settlement.
            Assert.False(dispatch.IsCompleted);
        }
        finally
        {
            // Release in the order that cannot strand a thread: the gate first (harmless if already
            // released), then the wedge.
            gate.Release();
            wedgeRelease.TrySetResult();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await disjointSave!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(wedge.Join(TimeSpan.FromSeconds(10)), "the wedge thread must have exited");

        Assert.Equal(1, gate.BlockCount);

        // BOTH local settlement actions completed once the wedge released.
        Assert.Null(manager.GetByTaskId(taskIdAtEntry!));
        Assert.Null(pipeline.ActiveTaskId);

        // The rollback completed confirmably and the disjoint save landed afterwards.
        Assert.Null(ReadPersistedActiveTaskId(GoalId));
        Assert.Null(ReadPersistedGoalId(SettledTaskId(pipeline)));
        Assert.NotNull(ReadPersistedRegistryBlob(disjointGoalId));
        Assert.Contains(logger.LogEntries, e => e.Message.Contains("outcome=committed", StringComparison.Ordinal));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (7c) THE OWNERSHIP CHECKPOINT — the REAL dispatch route
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REAL DISPATCH WRITES THE COMPLETE ADMISSION TUPLE BEFORE THE ENQUEUE. A genuine
    /// <see cref="TaskDispatchService.DispatchToRole"/> call on an ELIGIBLE manager-created pipeline
    /// observes — INSIDE the existing <see cref="TaskQueue.OnEnqueue"/> callback, i.e. at the
    /// instant Enqueue was entered — the durable <c>task_mappings</c> row, the durable
    /// <c>active_task_id</c> pointer AND the COMPLETE captured work-slot registry, all read RAW
    /// through the keeper connection. That proves commit-before-enqueue on the production path
    /// rather than a test-only store call.
    /// </summary>
    /// <remarks>
    /// THE ORDERING PROOF lives in the callback: the callback runs synchronously inside
    /// <c>TaskQueue.Enqueue</c>, so whatever it observes was already durable when Enqueue was
    /// ENTERED. Moving the admission after the enqueue makes every callback observation null and
    /// fails this test. No task ID is parsed and no counter is reconstructed: the decoded registry
    /// is compared to what the production capture allocated.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EligibleAdmission_CommitsTheOwnershipTupleBeforeEnqueue()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        // The provenance fact the eligible route is selected on.
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        // Observations taken AT CALLBACK ENTRY.
        string? taskIdAtEntry = null;
        string? mappingGoalAtEntry = null;
        string? pointerAtEntry = null;
        string? blobAtEntry = null;
        queue.OnEnqueue = t =>
        {
            taskIdAtEntry = t.TaskId;
            mappingGoalAtEntry = ReadPersistedGoalId(t.TaskId);
            pointerAtEntry = ReadPersistedActiveTaskId(GoalId);
            blobAtEntry = ReadPersistedRegistryBlob(GoalId);
        };

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // THE TUPLE WAS ALREADY DURABLE WHEN Enqueue WAS ENTERED.
        Assert.NotNull(taskIdAtEntry);
        AssertSuffixedTaskId(taskIdAtEntry!, TaskIdPrefix(GoalId, WorkerRole.Coder));
        Assert.Equal(GoalId, mappingGoalAtEntry);
        Assert.Equal(taskIdAtEntry, pointerAtEntry);
        Assert.NotNull(blobAtEntry);

        // THE COMPLETE REGISTRY: the capture's own slot, in the Pending state the admission saw.
        var decoded = WorkSlotRegistryCodec.Decode(blobAtEntry!);
        var durable = Assert.Single(decoded.Slots);
        Assert.Equal(taskIdAtEntry, durable.Slot.TaskId);
        Assert.Equal(WorkSlotState.Pending, durable.State);
        Assert.Equal(1, durable.Slot.Attempt);
        Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), durable.Slot.Position);
        var counter = Assert.Single(decoded.DispatchAttempts);
        Assert.Equal(durable.Slot.Position, counter.Position);
        Assert.Equal(durable.Slot.Attempt, counter.HighWaterAttempt);

        Assert.DoesNotContain(Warnings(logger), m => m.Contains("WorkSlotIntegrity", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE ENQUEUE-FAILURE ROLLBACK ON THE ELIGIBLE ROUTE, pinned EXPLICITLY: the ORIGINAL enqueue
    /// exception leaves the dispatch and the admission is undone by its OWN guarded atomic inverse —
    /// the DURABLE registry's matching Pending slot becomes <c>Abandoned</c> (no longer Pending),
    /// the DURABLE matching pointer is cleared and the matching mapping row is deleted, all in ONE
    /// rollback transaction — while history, counters and the ordinary fields are preserved.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: routing an evidence-carrying eligible admission into the LEGACY
    /// sequence (or dropping the rollback call entirely) leaves the durable slot <c>Pending</c>, so
    /// the <c>Abandoned</c> assertions below fail while every in-memory assertion still passes —
    /// exactly the gap this vector closes.
    /// </remarks>
    [Fact]
    public async Task Dispatch_EligibleEnqueueFailure_RollsBackThePendingAdmissionAtomically()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("enqueue-sentinel");
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(manager, queue, logger);

        string? taskIdAtEntry = null;
        string? blobAtEntry = null;
        queue.OnEnqueue = t =>
        {
            taskIdAtEntry = t.TaskId;
            blobAtEntry = ReadPersistedRegistryBlob(GoalId);
            throw sentinel;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE ORIGINAL enqueue exception left the dispatch.
        Assert.Same(sentinel, thrown);

        var taskId = SettledTaskId(pipeline);
        Assert.Equal(taskId, taskIdAtEntry);

        // THE POSITIVE OBSERVATION BEFORE THE ROLLBACK: the admission's checkpoint was durable with
        // a Pending slot (so the Abandoned assertion below is a real state CHANGE, not a default).
        var atEntry = WorkSlotRegistryCodec.Decode(blobAtEntry!);
        var entrySlot = Assert.Single(atEntry.Slots, s => s.Slot.TaskId == taskId);
        Assert.Equal(WorkSlotState.Pending, entrySlot.State);

        // ── THE ATOMIC INVERSE ──
        // The matching mapping row is DELETED…
        Assert.Null(ReadPersistedGoalId(taskId));
        // …the matching DURABLE pointer is CLEARED…
        Assert.Null(ReadPersistedActiveTaskId(GoalId));
        // …and the DURABLE registry's matching slot is ABANDONED — in ONE rollback transaction.
        var durable = WorkSlotRegistryCodec.Decode(ReadPersistedRegistryBlob(GoalId)!);
        var durableSlot = Assert.Single(durable.Slots, s => s.Slot.TaskId == taskId);
        Assert.Equal(WorkSlotState.Abandoned, durableSlot.State);
        // HISTORY, IDENTITY AND COUNTERS ARE PRESERVED EXACTLY — the position, the attempt and every
        // high-water entry the admission wrote survive the rollback untouched.
        Assert.Equal(entrySlot.Slot.Position, durableSlot.Slot.Position);
        Assert.Equal(entrySlot.Slot.Attempt, durableSlot.Slot.Attempt);
        Assert.Equal(atEntry.DispatchAttempts, durable.DispatchAttempts);

        // ── THE MEMORY SETTLEMENT: abandoned, unmapped and if-current-cleared ──
        Assert.Equal(WorkSlotState.Abandoned, SingleSlot(pipeline).State);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Null(pipeline.ActiveTaskId);

        // THE CONFIRMED ROLLBACK IS REPORTED HONESTLY (DEBUG), and no rollback-failure is recorded.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Debug &&
            e.Message.Contains("WorkSlotIntegrity: pending-admission-rollback", StringComparison.Ordinal) &&
            e.Message.Contains("outcome=committed", StringComparison.Ordinal));
        Assert.DoesNotContain(Warnings(logger), m => m.Contains("rollback-failure", StringComparison.Ordinal));

        // THE SLOT-RELEASE RECORD IS STILL EMITTED.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message == AbandonedRegistrationMessage(GoalId, taskId, 1, GoalPhase.Coding, 1));

        // NOTHING claims recoverability: the durable slot is Abandoned AND has no mapping row.
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
    /// <para>
    /// THE ROUTE IS THE INELIGIBLE (LEGACY) ONE: E3 (<c>RollbackPersistedPointer</c>) is a step of
    /// the legacy sequence only — the evidence-carrying eligible route uses the atomic inverse and
    /// never calls it. The persisted row is seeded first so the creation is ineligible.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Dispatch_EnqueueThrowsAndPersistedPointerRollbackFails_LogsPointerRollbackAndContinues()
    {
        var updateSentinel = new InvalidOperationException("pointer-update-sentinel");
        var store = CreateStore(new PipelinesUpdateThrowingInterceptor(updateSentinel));
        store.SavePipeline(new GoalPipeline(CreateGoal(GoalId)));
        var manager = new GoalPipelineManager(store, new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.False(pipeline.OwnershipCheckpointEligible);
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
    /// <para>
    /// THE ROUTE IS THE INELIGIBLE (LEGACY) ONE: E2→E3→E4 is the legacy sequence, and the eligible
    /// route's evidence-carrying rollback never issues E3's statement at all. The persisted row is
    /// seeded first so the creation is ineligible, and the mapping row for THIS task is seeded so
    /// the admission can still commit.
    /// </para>
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

        var store = CreateStore(observer);
        // THE INELIGIBLE SEED: a persisted row makes the replacement pipeline ineligible.
        store.SavePipeline(new GoalPipeline(CreateGoal(GoalId)));
        var manager = new GoalPipelineManager(store, new TestLogger<GoalPipelineManager>());
        pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Assert.False(pipeline.OwnershipCheckpointEligible);
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
        Assert.Equal([taskId], gateway.ClaimedTaskIds);
        Assert.NotNull(queue.GetActiveTask(taskId));
        Assert.Empty(DrainPending(queue));
        Assert.Equal(WorkerRole.Coder, worker.Role);
        Assert.Equal("coder-model", worker.CurrentModel);

        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains("delivery-", StringComparison.Ordinal));

        // THE PUBLICATION-SUCCESS RECORD fires for the PUBLISHED outcome — and the reported-refusal
        // vector above pins that it does NOT fire for a Blocked one.
        Assert.Contains(
            logger.LogEntries,
            e => e.LogLevel == LogLevel.Information &&
                 e.Message == $"Task {taskId} pushed to worker {worker.Id}");

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
        Assert.Equal([taskId], gateway.ClaimedTaskIds);
        Assert.Equal([taskId], gateway.SentTaskIds);
        Assert.NotNull(queue.GetActiveTask(taskId));

        // (iii) It was NOT diverted into a requeue: nothing went back to pending and no recovery or
        // failure record was written — only the POST-CLAIM best-effort guidance record.
        Assert.Empty(DrainPending(queue));
        Assert.DoesNotContain(
            logger.SeenMessages, m => m.Contains("delivery-recovery", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.SeenMessages, m => m.Contains("delivery-failure", StringComparison.Ordinal));
        Assert.Contains(
            logger.SeenMessages, m => m.Contains("delivery-guidance-failed", StringComparison.Ordinal));
    }

    // ── (c) + (d-normal) + (f-normal) THE CANCEL-CHECK REQUEUE, in order ─────
    /// <summary>
    /// THE PRE-CLAIM REQUEUE, in order: the Enqueue, then the <c>delivery-recovery</c> guard line,
    /// then the <c>delivery-failure</c> record — and finally the CALLER'S
    /// <see cref="OperationCanceledException"/>. No role is written and none is restored.
    /// </summary>
    /// <remarks>
    /// THE ORDER PROOF is temporal, not post-hoc: the enqueue callback appends its own event to the
    /// same list the logger appends to, so (1) before (2) before (3) are index comparisons. The
    /// logger also captures the worker's Role AT LOG TIME, which pins the restore-free contract:
    /// the role is the PRE-SELECTION one at every record because it is only ever published by the
    /// claim, which this vector never reaches.
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

        // THE ROLE IS NEVER WRITTEN BEFORE THE CLAIM, so there is nothing to restore: the worker
        // still carries its PRE-SELECTION role at BOTH records — a restore-free recovery.
        var guardEntry = Assert.Single(logger.Entries, e => e.Message == guardLine);
        Assert.Equal(LogLevel.Debug, guardEntry.Level);
        Assert.Equal(WorkerRole.Tester, guardEntry.RoleAtLog);

        var failureEntry = Assert.Single(logger.Entries, e => e.Message == failureLine);
        Assert.Equal(LogLevel.Warning, failureEntry.Level);
        Assert.Equal(WorkerRole.Tester, failureEntry.RoleAtLog);

        // The settled state: the role was never touched, nothing activated, no busy worker.
        Assert.Equal(WorkerRole.Tester, worker.Role);
        Assert.Null(worker.CurrentModel);
        Assert.Empty(gateway.ClaimedTaskIds);
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

    // ── (e) THE CHECKED CLAIM — refusal and throw ────────────────────────────

    /// <summary>
    /// A REFUSED checked claim is a CONFIRMED no-mutation outcome: the ACTUAL dequeued task goes
    /// back to the pending queue EXACTLY ONCE, nothing is activated, no guidance and no assignment
    /// are sent, the worker's role/model are untouched, the admission is retained, and the dispatch
    /// returns NORMALLY with the guarded refusal record.
    /// </summary>
    [Fact]
    public async Task Delivery_ClaimRefused_RequeuesOnceSendsNothingAndReturnsNormally()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var gateway = new DeliveryWorkerGateway(worker) { ClaimRefuses = true };
        var service = CreateService(
            manager, queue, logger, workerGateway: gateway, agentsManager: CreateAgentsManager());

        // THE NORMAL RETURN: a refusal is not a failure.
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

        Assert.Equal(1, gateway.ClaimAttempts);
        Assert.Empty(gateway.ClaimedTaskIds);
        Assert.Empty(gateway.SentTaskIds);
        // NO GUIDANCE for a loser.
        Assert.Equal(0, gateway.AgentsUpdateAttempts);
        // NOTHING ACTIVATED, and the task is pending again EXACTLY ONCE.
        Assert.Null(queue.GetActiveTask(taskId));
        Assert.Equal([taskId], DrainPending(queue));

        // THE WORKER IS UNTOUCHED: a loser never writes Role or CurrentModel.
        Assert.False(worker.IsBusy);
        Assert.Null(worker.CurrentTaskId);
        Assert.Null(worker.CurrentModel);
        Assert.Equal(WorkerRole.Tester, worker.Role);

        // THE ADMISSION IS RETAINED: no slot abandonment, pointer clear or mapping deletion.
        Assert.Equal(WorkSlotState.Pending, SingleSlot(pipeline).State);
        Assert.Same(pipeline, manager.GetByTaskId(taskId));
        Assert.Equal(taskId, pipeline.ActiveTaskId);

        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("delivery-claim-refused", StringComparison.Ordinal) &&
            e.Message.Contains(taskId, StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.LogEntries, e => e.Message.Contains("pushed to worker", StringComparison.Ordinal));
    }

    /// <summary>
    /// A THROWING checked claim is NOT a refusal: whether anything was mutated is unknowable, so
    /// the task is deliberately NOT requeued, nothing is sent, and the ORIGINAL exception instance
    /// propagates.
    /// </summary>
    [Fact]
    public async Task Delivery_ClaimThrows_DoesNotRequeueAndRethrowsOriginal()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var sentinel = new InvalidOperationException("claim-sentinel");
        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var gateway = new DeliveryWorkerGateway(worker) { ClaimThrows = sentinel };
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));
        Assert.Same(sentinel, thrown);

        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

        // NO REQUEUE and NO SEND: the outcome is unknowable, so nothing is assumed.
        Assert.Empty(DrainPending(queue));
        Assert.Empty(gateway.SentTaskIds);
        Assert.Empty(gateway.ClaimedTaskIds);

        // THE DIAGNOSTIC MUST NOT CLAIM THE ACTIVATION HAPPENED.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("delivery-claim-threw", StringComparison.Ordinal) &&
            e.Message.Contains("unknowable", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.LogEntries, e => e.Message.Contains("delivery-recovery", StringComparison.Ordinal));
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
    /// An ORDINARY throwing <c>IWorkerGateway.SendTaskAsync</c> — THE AMBIGUITY POINT — is
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

    // ── (h2) STAGE S — THE REPORTED REFUSAL (Blocked) ────────────────────────

    /// <summary>
    /// A <see cref="WorkerTaskSendOutcome.Blocked"/> report is NOT a failure: the dispatch returns
    /// NORMALLY with NO <c>delivery-failure</c> record, and the WHOLE delivery state is retained —
    /// the active queue entry, the pointer, the Pending slot, the mapping and the worker's
    /// busy/role/model state.
    /// </summary>
    [Fact]
    public async Task Delivery_SendReportsBlocked_ReturnsNormallyAndRetainsEverything()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var gateway = new DeliveryWorkerGateway(worker) { SendTaskBlocks = true };
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        // THE NORMAL RETURN — nothing escapes the dispatch for a reported refusal.
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        var taskId = SettledTaskId(pipeline);
        AssertSuffixedTaskId(taskId, TaskIdPrefix(GoalId, WorkerRole.Coder));

        // THE EAGER ADMISSION AND DELIVERY STATE ARE ALL RETAINED.
        Assert.NotNull(queue.GetActiveTask(taskId));
        Assert.Equal([taskId], gateway.ClaimedTaskIds);
        Assert.True(worker.IsBusy);
        Assert.Equal(WorkerRole.Coder, worker.Role);
        Assert.Equal("coder-model", worker.CurrentModel);
        Assert.Empty(DrainPending(queue));
        Assert.Equal(WorkSlotState.Pending, SingleSlot(pipeline).State);
        Assert.Same(pipeline, manager.GetByTaskId(taskId));
        Assert.Equal(taskId, pipeline.ActiveTaskId);

        // NO FAILURE RECORD AND NO RECOVERY: a refusal is not an ambiguity-preserve.
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains("delivery-failure", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains("delivery-recovery", StringComparison.Ordinal));
        // NO PUBLICATION-SUCCESS RECORD EITHER: it reports channel publication, which a refusal
        // deliberately did NOT perform.
        Assert.DoesNotContain(
            logger.LogEntries,
            e => e.Message.Contains("pushed to worker", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.LogEntries,
            e => e.LogLevel == LogLevel.Information &&
                 e.Message == $"Task {taskId} pushed to worker {worker.Id}");
        Assert.Contains(
            logger.LogEntries,
            e => e.LogLevel == LogLevel.Information &&
                 e.Message == $"Dispatched Coder task {taskId} for goal {GoalId} " +
                              $"(branch=copilothive/{GoalId})");
    }

    /// <summary>
    /// TWO-GOAL FIFO ISOLATION FOR THE REFUSAL: pipeline B's push delivers pipeline A's EARLIER
    /// queued task, which is BLOCKED. B's own admission — its slot, its mapping, its pointer — is
    /// completely untouched, and A's delivered task is preserved rather than thrown into B's
    /// dispatch.
    /// </summary>
    [Fact]
    public async Task Delivery_BlockedEarlierTaskOfAnotherGoal_LeavesBothAdmissionsIntact()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());

        // Pipeline A dispatched first; its coder task sits in the pending queue with no idle worker.
        var pipelineA = manager.CreatePipeline(CreateGoal("goal-a"));
        Arrange(pipelineA, GoalPhase.Coding);
        var queue = new TaskQueue();
        var serviceA = CreateService(
            manager, queue, new TestLogger<TaskDispatchService>(), goal: CreateGoal("goal-a"));
        await serviceA.DispatchToRole(pipelineA, WorkerRole.Coder, "Code A", TestContext.Current.CancellationToken);

        var taskA = SettledTaskId(pipelineA);
        AssertSuffixedTaskId(taskA, TaskIdPrefix("goal-a", WorkerRole.Coder));

        // Pipeline B dispatches WITH an idle worker — and the FIFO hands it A's task.
        var pipelineB = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipelineB, GoalPhase.Coding);

        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var loggerB = new TestLogger<TaskDispatchService>();
        var gateway = new DeliveryWorkerGateway(worker) { SendTaskBlocks = true };
        var serviceB = CreateService(manager, queue, loggerB, workerGateway: gateway);

        // THE NORMAL RETURN: the refusal for A must not surface as B's failure.
        await serviceB.DispatchToRole(pipelineB, WorkerRole.Coder, "Code B", TestContext.Current.CancellationToken);

        var taskB = SettledTaskId(pipelineB);
        AssertSuffixedTaskId(taskB, TaskIdPrefix(GoalId, WorkerRole.Coder));
        Assert.NotEqual(taskA, taskB, StringComparer.Ordinal);

        // THE MISMATCH RECORD names the DELIVERED goal and BOTH task IDs.
        Assert.Contains(loggerB.LogEntries, e =>
            e.LogLevel == LogLevel.Debug && e.Message == DeliveryMismatchMessage("goal-a", taskB, taskA));
        // NO failure record names the DELIVERED task: the refusal is not a preserve.
        Assert.DoesNotContain(loggerB.LogEntries, e => e.Message.Contains("delivery-failure", StringComparison.Ordinal));

        // THE DELIVERED task is the one retained by the delivery.
        Assert.NotNull(queue.GetActiveTask(taskA));
        Assert.Equal([taskA], gateway.ClaimedTaskIds);

        // B's ADMISSION is untouched, and A's admission stands too.
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipelineB.GetSlotsForTest()).State);
        Assert.Same(pipelineB, manager.GetByTaskId(taskB));
        Assert.Equal(taskB, pipelineB.ActiveTaskId);
        Assert.Equal([taskB], DrainPending(queue));
        Assert.Same(pipelineA, manager.GetByTaskId(taskA));
        Assert.Equal(taskA, pipelineA.ActiveTaskId);
    }

    // ── (h3) THE REAL-GATEWAY OBSERVATION FIXTURES ───────────────────────────

    /// <summary>
    /// THE COMPLETE TWO-GOAL CONFLICT/REFUSAL CHAIN: the REAL
    /// <see cref="GrpcWorkerGateway"/> — with the REAL publisher and the fixture-minted
    /// <see cref="CapturingGatewayLogger"/> — is reached by a real two-goal FIFO delivery, and its
    /// single guarded warning NAMES the DELIVERED goal and task, the worker and the refusal reason.
    /// </summary>
    /// <remarks>
    /// THE GENUINE REFUSAL: a DIFFERENT context is seeded for A's delivered task id, so the real
    /// publisher's insert-once arbitration reports <c>Conflict</c>. The warning therefore comes from
    /// the real gateway's own guarded diagnostic, and A's delivered task is never published while B's
    /// admission — its slot, its mapping, its pointer — is untouched.
    /// </remarks>
    [Fact]
    public async Task Delivery_RealGatewayBlockedWarning_NamesDeliveredGoalTaskWorkerAndReason()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());

        // Pipeline A first, with NO idle worker: its coder task is only ENQUEUED.
        var pipelineA = manager.CreatePipeline(CreateGoal("goal-warn-a"));
        Arrange(pipelineA, GoalPhase.Coding);
        var queue = new TaskQueue();
        var serviceA = CreateService(
            manager, queue, new TestLogger<TaskDispatchService>(), goal: CreateGoal("goal-warn-a"));
        await serviceA.DispatchToRole(pipelineA, WorkerRole.Coder, "Code A", TestContext.Current.CancellationToken);

        var taskA = SettledTaskId(pipelineA);
        AssertSuffixedTaskId(taskA, TaskIdPrefix("goal-warn-a", WorkerRole.Coder));

        // THE REAL GATEWAY, the REAL publisher and the CAPTURING gateway logger — all three sharing
        // ONE worker pool, so the publisher's pinned-instance check really holds.
        var pool = new WorkerPool();
        var registered = pool.RegisterWorker("worker-warn", []);
        registered.Role = WorkerRole.Tester;
        var gatewayLogger = new CapturingGatewayLogger();

        using var recording = EagerAssignmentRecording.Start(manager, pool);
        var conflictingSlot = new WorkSlot(taskA, new WorkSlotPosition(4, GoalPhase.Testing, 2), 5);
        var seeded = recording.Store.InsertOnce(new WorkerAssignmentContext(
            "goal-warn-a", "worker-elsewhere", WorkerRole.Tester, conflictingSlot, "conflicting-model"));
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, seeded.Status);
        var seededRowBeforeDelivery = recording.Store.Load(taskA);
        Assert.NotNull(seededRowBeforeDelivery);

        var gateway = new GrpcWorkerGateway(pool, recording.Publisher, gatewayLogger);

        // Pipeline B's dispatch FIFO-delivers A's EARLIER coder task through the real gateway.
        var pipelineB = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipelineB, GoalPhase.Coding);
        var loggerB = new TestLogger<TaskDispatchService>();
        var serviceB = CreateService(manager, queue, loggerB, workerGateway: gateway);

        await serviceB.DispatchToRole(pipelineB, WorkerRole.Coder, "Code B", TestContext.Current.CancellationToken);

        var taskB = SettledTaskId(pipelineB);
        AssertSuffixedTaskId(taskB, TaskIdPrefix(GoalId, WorkerRole.Coder));
        Assert.NotEqual(taskA, taskB, StringComparer.Ordinal);

        // THE SINGLE GUARDED WARNING, with its structured fields: worker, DELIVERED task, DELIVERED
        // goal, the refusal reason and the blocked/retained disposition. It must never accidentally
        // identify B, whose dispatch merely triggered delivery of A.
        var warning = Assert.Single(
            gatewayLogger.Emitted, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("assignment blocked", warning.Message, StringComparison.Ordinal);
        Assert.Contains("no assignment published", warning.Message, StringComparison.Ordinal);
        Assert.Contains("task retained", warning.Message, StringComparison.Ordinal);
        Assert.Contains(taskA, warning.Message, StringComparison.Ordinal);
        Assert.Contains("goal-warn-a", warning.Message, StringComparison.Ordinal);
        Assert.Contains(registered.Id, warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            nameof(WorkerAssignmentRecordingFailureReason.Conflict),
            warning.Message,
            StringComparison.Ordinal);

        // THE STRUCTURED ARGUMENTS carry the same four fields verbatim.
        Assert.Contains(registered.Id, warning.Arguments);
        Assert.Contains(taskA, warning.Arguments);
        Assert.Contains("goal-warn-a", warning.Arguments);
        Assert.Contains(WorkerAssignmentRecordingFailureReason.Conflict, warning.Arguments);
        Assert.DoesNotContain(taskB, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(taskB, warning.Arguments);

        // THE THROWING ARM: a logger asked to fail at WARNING (and one asked to fail at ANY level)
        // really throws through the REAL gateway — which is the capability the guarded warning's
        // resilience vector needs.
        foreach (var throwingLogger in new[]
                 {
                     new CapturingGatewayLogger { ThrowOnLevel = LogLevel.Warning },
                     new CapturingGatewayLogger { ThrowOnAnyWrite = true },
                 })
        {
            var throwingGateway = new GrpcWorkerGateway(pool, recording.Publisher, throwingLogger);
            var outcome = await throwingGateway.SendTaskAsync(
                registered.Id,
                new WorkTask
                {
                    TaskId = taskA,
                    GoalId = "goal-warn-a",
                    GoalDescription = "warning fixture",
                    Prompt = "do the work",
                    Role = WorkerRole.Coder,
                    Model = "model-warn",
                    Repositories = [new TargetRepository { Name = "repo", Url = "https://example.invalid/repo" }],
                },
                TestContext.Current.CancellationToken);

            // THE THROW WAS REAL AND THE HANDLED DISPOSITION SURVIVED IT.
            Assert.Equal(WorkerTaskSendOutcome.Blocked, outcome);
            Assert.Equal(1, throwingLogger.ThrowCount);
            Assert.Single(throwingLogger.Emitted);
        }

        // NOTHING WAS PUBLISHED and the FINAL publication-success record for A does not exist.
        Assert.False(registered.MessageChannel.Reader.TryRead(out _));
        Assert.DoesNotContain(
            loggerB.LogEntries,
            e => e.Message.Contains("pushed to worker", StringComparison.Ordinal));
        Assert.DoesNotContain(
            loggerB.LogEntries,
            e => e.LogLevel == LogLevel.Information &&
                 e.Message == $"Task {taskA} pushed to worker {registered.Id}");

        // THE EARLIER ADMISSION/ENQUEUE RECORD FOR B REMAINS. This is intentionally NOT blanket log
        // silence: suppressing all post-admission information would fail this positive assertion.
        Assert.Contains(
            loggerB.LogEntries,
            e => e.LogLevel == LogLevel.Information &&
                 e.Message == $"Dispatched Coder task {taskB} for goal {GoalId} " +
                              $"(branch=copilothive/{GoalId})");

        // THE CONFLICTING ASSIGNMENT ROW FOR A IS BYTE-FOR-BYTE SEMANTICALLY UNCHANGED, including its
        // first-assigned instant; B was never delivered and therefore acquired no assignment row.
        var seededRowAfterDelivery = recording.Store.Load(taskA);
        Assert.NotNull(seededRowAfterDelivery);
        Assert.Equal(seededRowBeforeDelivery!.Context, seededRowAfterDelivery!.Context);
        Assert.Equal(seededRowBeforeDelivery.FirstAssignedAtUtc, seededRowAfterDelivery.FirstAssignedAtUtc);
        Assert.Null(recording.Store.Load(taskB));

        // BOTH ADMISSIONS REMAIN INTACT. A is the active queue entry retained by the blocked
        // delivery; B remains pending. Both pointers, Pending slots, in-memory mappings and durable
        // task-mapping rows still identify their original pipelines.
        Assert.NotNull(queue.GetActiveTask(taskA));
        Assert.Null(queue.GetActiveTask(taskB));
        Assert.Equal(taskA, pipelineA.ActiveTaskId);
        Assert.Equal(taskB, pipelineB.ActiveTaskId);
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipelineA.GetSlotsForTest()).State);
        Assert.Equal(WorkSlotState.Pending, Assert.Single(pipelineB.GetSlotsForTest()).State);
        Assert.Same(pipelineA, manager.GetByTaskId(taskA));
        Assert.Same(pipelineB, manager.GetByTaskId(taskB));
        Assert.Equal("goal-warn-a", ReadPersistedGoalId(taskA));
        Assert.Equal(GoalId, ReadPersistedGoalId(taskB));
        Assert.Equal([taskB], DrainPending(queue));

        // THE PINNED WORKER RETAINS A'S actual delivered identity; no B state unwound it.
        Assert.Equal(taskA, registered.CurrentTaskId);
        Assert.True(registered.IsBusy);
        Assert.Equal(WorkerRole.Coder, registered.Role);
        Assert.Equal("coder-model", registered.CurrentModel);
    }

    /// <summary>
    /// THE UNDEFINED-OUTCOME BOUNDARY: a gateway double returning an UNDEFINED
    /// <see cref="WorkerTaskSendOutcome"/> makes the dispatch's explicit <c>default</c> branch throw,
    /// and the publication-success record is NEVER emitted for it.
    /// </summary>
    [Fact]
    public async Task Delivery_SendReportsUndefinedOutcome_ThrowsAndEmitsNoSuccessRecord()
    {
        var manager = new GoalPipelineManager(CreateStore(), new TestLogger<GoalPipelineManager>());
        var pipeline = manager.CreatePipeline(CreateGoal(GoalId));
        Arrange(pipeline, GoalPhase.Coding);

        var worker = CreateIdleWorker(role: WorkerRole.Tester);
        var queue = new TaskQueue();
        var logger = new TestLogger<TaskDispatchService>();
        var gateway = new DeliveryWorkerGateway(worker)
        {
            SendTaskOutcomeOverride = (WorkerTaskSendOutcome)999,
        };
        var service = CreateService(manager, queue, logger, workerGateway: gateway);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE UNDEFINED VALUE IS REPORTED AS SUCH — never mistaken for a publication.
        Assert.Equal("Unhandled WorkerTaskSendOutcome: 999", thrown.Message);

        var taskId = SettledTaskId(pipeline);

        // NO FINAL SUCCESS RECORD FOR THE ACTUAL TASK. The explicit default throw occurs before that
        // log site, and no delivery-failure record is invented (the undefined return is a contract
        // violation, not an exception thrown by the send ambiguity point). Retain the pre-existing
        // broad assertion and add the exact-task assertion; neither existing coverage nor precision
        // is traded away.
        Assert.DoesNotContain(
            logger.LogEntries,
            e => e.Message.Contains("pushed to worker", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.LogEntries,
            e => e.LogLevel == LogLevel.Information &&
                 e.Message == $"Task {taskId} pushed to worker {worker.Id}");
        Assert.DoesNotContain(
            logger.LogEntries,
            e => e.Message.Contains("delivery-failure", StringComparison.Ordinal));

        // THE THROW DOES NOT SECRETLY ROLLBACK OR REQUEUE: stage S had already activated and marked
        // the worker before it inspected the undefined result.
        Assert.NotNull(queue.GetActiveTask(taskId));
        Assert.Empty(DrainPending(queue));
        Assert.Equal([taskId], gateway.ClaimedTaskIds);
        Assert.Equal(taskId, pipeline.ActiveTaskId);
        Assert.True(worker.IsBusy);

        // The adjacent Delivery_SendReportsBlocked_ReturnsNormallyAndRetainsEverything vector pins
        // the other pre-success branch: Blocked returns normally and likewise emits no final success
        // record. Together the two vectors prove only Published can reach that log.
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
        Assert.Equal([taskId], gateway.ClaimedTaskIds);
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
        Assert.Equal([taskA], gateway.ClaimedTaskIds);

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
        /// <summary>Unreachable here — the idle probe throws first — and never a blanket success.</summary>
        public bool TryClaimAndActivate(ConnectedWorker expected, WorkTask task, TaskQueue queue) => false;
        public Task<WorkerTaskSendOutcome> SendTaskAsync(string workerId, WorkTask task, CancellationToken ct = default) =>
            Task.FromResult(WorkerTaskSendOutcome.Published);
        public Task<WorkerTaskSendOutcome> SendTaskAsync(ConnectedWorker worker, WorkTask task, CancellationToken ct = default) =>
            Task.FromResult(WorkerTaskSendOutcome.Published);
        public Task SendCancelAsync(string workerId, string taskId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAgentsUpdateAsync(string workerId, string role, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAgentsUpdateAsync(ConnectedWorker worker, string role, string content, CancellationToken ct = default) => Task.CompletedTask;
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
    /// THE DELIVERY GATEWAY: hands back one fixed idle worker and records every claim/send it
    /// receives, with an injectable throw for each stage the delivery classifies.
    /// </summary>
    /// <remarks>
    /// <see cref="TryClaimAndActivate"/> mirrors the pool primitive: it REFUSES a foreign or
    /// non-idle instance and, when it accepts, applies the REAL activation and busy/role/model
    /// publication, so the send-stage preserve can assert a genuinely busy worker.
    /// </remarks>
    private sealed class DeliveryWorkerGateway : IWorkerGateway
    {
        private readonly ConnectedWorker _worker;

        public DeliveryWorkerGateway(ConnectedWorker worker) => _worker = worker;

        /// <summary>When set, <see cref="TryClaimAndActivate"/> throws it.</summary>
        public Exception? ClaimThrows { get; init; }

        /// <summary>When set, <see cref="TryClaimAndActivate"/> REFUSES without mutating anything.</summary>
        public bool ClaimRefuses { get; init; }

        /// <summary>When set, <c>SendTaskAsync</c> throws it (stage S).</summary>
        public Exception? SendTaskThrows { get; init; }

        /// <summary>
        /// When set, <c>SendTaskAsync</c> reports the RECORDING REFUSAL
        /// (<see cref="WorkerTaskSendOutcome.Blocked"/>) instead of publishing — the eager
        /// gateway's blocked disposition, WITHOUT any thrown failure. The cancel/throw injections
        /// above still take precedence, so a vector can combine an injected throw with this flag
        /// only deliberately.
        /// </summary>
        public bool SendTaskBlocks { get; init; }

        /// <summary>
        /// THE UNDEFINED-OUTCOME ARM: when set, <c>SendTaskAsync</c> returns THIS value
        /// verbatim instead of the two defined outcomes — e.g. <c>(WorkerTaskSendOutcome)999</c>.
        /// A vector uses it to prove the dispatch's explicit <c>default</c> branch throws (and
        /// therefore can never be mistaken for a successful publication).
        /// </summary>
        /// <remarks>
        /// IT IS DELIBERATELY PLACED BESIDE <see cref="SendTaskBlocks"/> AND APPLIED THE SAME WAY:
        /// the cancel/throw injections above still take precedence, so an armed throw is still an
        /// armed throw.
        /// </remarks>
        public WorkerTaskSendOutcome? SendTaskOutcomeOverride { get; init; }

        /// <summary>
        /// When set, <c>SendTaskAsync</c> CANCELS this source and then throws by observing
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

        /// <summary>When set, <c>SendAgentsUpdateAsync</c> throws it (stage A).</summary>
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

        /// <summary>The task ids this gateway ACCEPTED a checked claim for.</summary>
        public List<string> ClaimedTaskIds { get; } = [];

        /// <summary>Number of claim attempts — the proof the claim stage really ran.</summary>
        public int ClaimAttempts { get; private set; }

        public List<string> SentTaskIds { get; } = [];

        /// <summary>Number of agents-md sends attempted — the proof that stage A really ran.</summary>
        public int AgentsUpdateAttempts { get; private set; }

        /// <summary>
        /// THE DELIVERY-TIME OBSERVATION SEAM. When set, it is invoked from INSIDE
        /// <c>SendTaskAsync</c> on the SUCCESS path — after the cancel/throw injections and
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

        /// <summary>UNUSED BY THE EAGER PATH — kept only because the interface declares it.</summary>
        public void MarkBusy(string workerId, string taskId) => MarkedBusyTaskIds.Add(taskId);

        /// <summary>
        /// THE CHECKED CLAIM, mirroring <c>WorkerPool.TryClaimAndActivate</c>: an injected throw
        /// first, then the injected refusal, then the real shape checks — and only then the single
        /// activation + busy/role/model publication.
        /// </summary>
        public bool TryClaimAndActivate(ConnectedWorker expected, WorkTask task, TaskQueue queue)
        {
            ClaimAttempts++;

            if (ClaimThrows is not null)
                throw ClaimThrows;

            if (ClaimRefuses
                || !ReferenceEquals(expected, _worker)
                || expected.IsBusy
                || expected.CurrentTaskId is not null)
            {
                return false;
            }

            queue.Activate(task, expected.Id);
            ClaimedTaskIds.Add(task.TaskId);
            expected.IsBusy = true;
            expected.CurrentTaskId = task.TaskId;
            expected.CurrentTaskStartedAt = DateTime.UtcNow;
            expected.Role = task.Role;
            expected.CurrentModel = task.Model;
            return true;
        }

        public Task<WorkerTaskSendOutcome> SendTaskAsync(string workerId, WorkTask task, CancellationToken ct = default) =>
            SendTaskAsync(_worker, task, ct);

        public async Task<WorkerTaskSendOutcome> SendTaskAsync(ConnectedWorker worker, WorkTask task, CancellationToken ct = default)
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

            if (SendTaskOutcomeOverride is WorkerTaskSendOutcome overridden)
                return overridden;

            if (SendTaskBlocks)
                return WorkerTaskSendOutcome.Blocked;

            // THE DELIVERY-TIME OBSERVATION, taken INSIDE the real send — the instant at which the
            // whole identity chain is simultaneously live (see OnSendTask).
            OnSendTask?.Invoke(task);

            SentTaskIds.Add(task.TaskId);
            return WorkerTaskSendOutcome.Published;
        }

        public Task SendCancelAsync(string workerId, string taskId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendAgentsUpdateAsync(string workerId, string role, string content, CancellationToken ct = default)
        {
            AgentsUpdateAttempts++;
            return AgentsUpdateThrows is not null ? throw AgentsUpdateThrows : Task.CompletedTask;
        }

        public Task SendAgentsUpdateAsync(ConnectedWorker worker, string role, string content, CancellationToken ct = default)
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
/// A transaction wrapper that lets the FIRST explicit transaction COMMIT normally and throws a
/// pre-created sentinel at every LATER commit — BEFORE the underlying SQLite commit
/// (<c>throwAfterCommit</c> = <c>false</c>) or immediately after it (the ambiguity the store must
/// never resolve).
/// </summary>
/// <remarks>
/// WHY THE FIRST COMMIT IS LET THROUGH: on the production dispatch the ADMISSION's transaction is
/// the first one, so letting it land is what makes the admission produce real invocation-local
/// rollback evidence; the rollback's own transaction is then the one whose commit throws. A blanket
/// fault connection would break the admission instead and could never reach the vector this test is
/// about.
/// </remarks>
internal sealed class AdmissionSecondCommitFaultConnection : AdmissionTransactionConnectionBase
{
    private readonly bool _throwAfterCommit;
    private int _firstCommitCount;
    private int _secondCommitCount;
    private int _rollbackFaultCount;
    private volatile bool _rollbackFaultArmed;

    public AdmissionSecondCommitFaultConnection(string connectionString, bool throwAfterCommit)
        : base(connectionString) => _throwAfterCommit = throwAfterCommit;

    /// <summary>The distinct exception every faulting commit throws.</summary>
    public InvalidOperationException CommitSentinel { get; } = new("rollback commit timing sentinel");

    /// <summary>
    /// The DISTINCT exception every faulting transaction ROLLBACK throws — deliberately a different
    /// instance from <see cref="CommitSentinel"/> so a test can tell the two evidence slots apart.
    /// </summary>
    public InvalidOperationException RollbackSentinel { get; } = new("rollback transaction rollback sentinel");

    /// <summary>How many commits returned normally (the admission's).</summary>
    public int FirstCommitCount => Volatile.Read(ref _firstCommitCount);

    /// <summary>How many commits were faulted (the rollback's).</summary>
    public int SecondCommitCount => Volatile.Read(ref _secondCommitCount);

    /// <summary>True once a faulting commit was attempted.</summary>
    public bool SecondCommitAttempted => SecondCommitCount > 0;

    /// <summary>How many transaction rollbacks were faulted (the vacuity guard).</summary>
    public int RollbackFaultCount => Volatile.Read(ref _rollbackFaultCount);

    /// <summary>
    /// Arms the TRANSACTION-ROLLBACK fault. Call it AFTER the admission has committed (from the
    /// enqueue callback), so only the pending-admission rollback's own transaction can be affected
    /// and the fixture's setup runs through untouched.
    /// </summary>
    public void ArmRollbackFault() => _rollbackFaultArmed = true;

    internal bool RollbackFaultArmed => _rollbackFaultArmed;

    /// <summary>Counts a commit attempt; the FIRST is the admission's (not faulted).</summary>
    internal int RecordCommitAttempt() => Interlocked.Increment(ref _firstCommitCount);

    /// <summary>Counts a faulted commit attempt.</summary>
    internal void RecordFaultedCommit() => Interlocked.Increment(ref _secondCommitCount);

    /// <summary>Counts a faulted transaction rollback.</summary>
    internal void RecordFaultedRollback() => Interlocked.Increment(ref _rollbackFaultCount);

    protected override DbTransaction WrapTransaction(SqliteTransaction transaction) =>
        new FaultTransaction(this, transaction);

    private sealed class FaultTransaction : DbTransaction
    {
        private readonly AdmissionSecondCommitFaultConnection _owner;
        private readonly SqliteTransaction _inner;

        public FaultTransaction(AdmissionSecondCommitFaultConnection owner, SqliteTransaction inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public override System.Data.IsolationLevel IsolationLevel => _inner.IsolationLevel;
        protected override DbConnection DbConnection => _owner;

        public override void Commit()
        {
            // The FIRST commit (the admission's) lands normally; every later one is faulted.
            if (_owner.RecordCommitAttempt() == 1)
            {
                _inner.Commit();
                return;
            }

            _owner.RecordFaultedCommit();
            if (!_owner._throwAfterCommit)
                throw _owner.CommitSentinel;
            _inner.Commit();
            throw _owner.CommitSentinel;
        }

        public override void Rollback()
        {
            // ONLY once armed — so the admission's own transaction is never affected.
            if (_owner.RollbackFaultArmed)
            {
                _owner.RecordFaultedRollback();
                throw _owner.RollbackSentinel;
            }

            _inner.Rollback();
        }

        protected override void Dispose(bool disposing) => _inner.Dispose();
    }
}

/// <summary>
/// Parks the FIRST statement that writes the work-slot registry column on the <c>pipelines</c> row
/// (<c>UPDATE … SET … work_slot_registry_json …</c>) on an external gate while armed — the
/// rollback's own CAS update, which the manager reaches with its mapping monitor held. The gate
/// performs no re-entrant work and is released by the test; the bounded wait keeps a never-released
/// gate a test failure rather than a hang.
/// </summary>
internal sealed class PendingRollbackGateInterceptor : DbCommandInterceptor
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _blockCount;
    private volatile bool _armed;

    /// <summary>Completes the first time the targeted statement is reached.</summary>
    public Task Entered => _entered.Task;

    /// <summary>How many targeted statements actually parked (the vacuity guard).</summary>
    public int BlockCount => Volatile.Read(ref _blockCount);

    /// <summary>Arms the gate (call AFTER setup so only the rollback's statement can park).</summary>
    public void Arm() => _armed = true;

    /// <summary>Releases a parked statement.</summary>
    public void Release() => _release.TrySetResult();

    private void BlockIfTargeted(DbCommand command)
    {
        if (!_armed)
            return;

        var text = command.CommandText.TrimStart();
        if (!text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            return;
        // THE ROLLBACK'S OWN CAS STATEMENT — its unique shape is the pointer CLEAR plus the
        // replacement blob. The ADMISSION's row write names a concrete pointer (so it never matches
        // this predicate), which is what keeps the gate from parking the admission instead.
        if (!text.Contains("work_slot_registry_json", StringComparison.Ordinal))
            return;
        if (!text.Contains("active_task_id = NULL", StringComparison.Ordinal))
            return;

        // Only the FIRST targeted statement blocks; TrySetResult is the one-shot latch.
        if (!_entered.TrySetResult())
            return;

        Interlocked.Increment(ref _blockCount);
        _release.Task.Wait(TimeSpan.FromSeconds(60));
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        BlockIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        BlockIfTargeted(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        BlockIfTargeted(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        BlockIfTargeted(command);
        return ValueTask.FromResult(result);
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
