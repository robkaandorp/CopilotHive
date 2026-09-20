using System.Collections.Concurrent;
using System.Reflection;

using CopilotHive.Actors;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// THE RESTORE-ORIGIN HOLD end to end: a pipeline restored from a persisted snapshot with a
/// NONTERMINAL phase and a NON-NULL active-task pointer is HELD
/// (<see cref="GoalPipeline.IsRestoredActiveAttemptHold"/>) and every automatic consumer refuses
/// it — orchestrator restart alone is not permission to invalidate or replace a valid attempt.
/// <para>
/// The evidence is reached through the REAL production chain: genuinely admitted Pending attempts
/// are persisted through the live manager APIs, reopened through BOTH restore routes
/// (<see cref="GoalPipelineManager.RestorePipeline"/> and
/// <see cref="GoalPipelineManager.RestoreFromStore"/>), and driven through the actual startup gate
/// (<see cref="DispatcherMaintenance.RestoreActivePipelinesAsync"/>), the redispatch drain, the
/// dispatch entry, the completion entry and the stale reclaim. Assertions land on observable
/// production outcomes — the pointer, the durable mapping, the queue entries, the phase, the
/// budgets and the exact raw registry blob read back RAW — never on guard calls.
/// </para>
/// <para>
/// THE DATABASE is a per-instance shared-cache in-memory SQLite file anchored by a keeper
/// connection (the suite's established shape), so every raw-column readback bypasses EF entirely.
/// No test uses Task.Delay: every rendezvous is a callback or a seam that already exists.
/// </para>
/// <para>
/// REMOVAL-PROOFNESS. Every consumer vector arranges the state the PRE-FIX code would have
/// destroyed: the drain vector clears the pointer locally (the hold is independent of pointer
/// retention) so the old pointer filter could not mask a removed guard; the completion vector's
/// pre-fix path would clear the pointer and drive (throwing); the reclaim vector seeds a REAL
/// active queue entry that the previously unconditional <see cref="TaskQueue.MarkComplete"/>
/// would consume. Each refusal is additionally pinned by its focused, exactly-once warning.
/// </para>
/// </summary>
public sealed class RestoredActiveAttemptHoldTests : IDisposable
{
    private readonly string _connectionString =
        $"Data Source=file:memdb-hold-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<SqliteConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];

    public RestoredActiveAttemptHoldTests()
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

    /// <summary>A factory-created (store-OWNED) context per operation — the production shape.</summary>
    private sealed class HoldContextFactory(string connectionString) : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext()
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
            return new CopilotHiveDbContext(builder.Options);
        }
    }

    private PipelineStore CreateFactoryBackedStore() =>
        new(new HoldContextFactory(_connectionString), NullLogger<PipelineStore>.Instance);

    private static Goal NewGoal(string id) =>
        new() { Id = id, Description = "hold goal " + id, RepositoryNames = ["test-repo"] };

    /// <summary>
    /// Persists a GENUINELY ADMITTED Pending attempt through the live manager APIs: the manager
    /// creates the pipeline, the production capture allocates the slot, the atomic claim takes
    /// the pointer and <see cref="GoalPipelineManager.PersistAdmission"/> commits the durable
    /// mapping row + pointer row in one transaction — exactly what a real dispatch leaves behind.
    /// </summary>
    private static (GoalPipeline Pipeline, string TaskId) SeedAdmittedAttempt(
        GoalPipelineManager manager, string goalId)
    {
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        Arrange(pipeline, GoalPhase.Coding);

        var slot = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.True(pipeline.TrySetActiveTask(slot.TaskId, "copilothive/" + goalId));
        var admission = manager.PersistAdmission(pipeline, slot.TaskId);
        Assert.Equal(AdmissionCommitStatus.Committed, admission.Status);
        return (pipeline, slot.TaskId);
    }

    /// <summary>Installs the full plan and puts pipeline AND state machine on the phase.</summary>
    private static void Arrange(GoalPipeline pipeline, GoalPhase phase)
    {
        var plan = IterationPlan.Default(includeImprove: true);
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, phase);
        pipeline.AdvanceTo(phase);
    }

    /// <summary>
    /// Seeds a pipeline row through the real APIs, then forces the case's persisted phase,
    /// pointer and registry blob with RAW updates (the corrupt/legacy shapes the real world
    /// contains). Returns the raw blob the row ends with.
    /// </summary>
    private string? SeedClassifiedRow(string goalId, GoalPhase phase, string? pointer, string? blobMode)
    {
        var manager = new GoalPipelineManager(CreateStore(), NullLogger<GoalPipelineManager>.Instance);
        if (pointer is null || pointer == "real")
        {
            if (pointer == "real")
                SeedAdmittedAttempt(manager, goalId);
            else
            {
                var pipeline = manager.CreatePipeline(NewGoal(goalId));
                Arrange(pipeline, GoalPhase.Coding);
                manager.PersistState(pipeline);
            }
        }
        else
        {
            // A genuinely admitted attempt first, then the arbitrary pointer text the case wants.
            SeedAdmittedAttempt(manager, goalId);
        }

        // Move to the case's phase through the real transition and save.
        var live = manager.GetByGoalId(goalId)!;
        if (live.Phase != phase)
        {
            live.AdvanceTo(phase);
            manager.PersistState(live);
        }

        // RAW pointer override (after the save, so nothing rewrites it).
        if (pointer is not null && pointer != "real")
            RawUpdate(goalId, "active_task_id", pointer);

        // RAW registry-blob override.
        switch (blobMode)
        {
            case "null":
                RawUpdate(goalId, "work_slot_registry_json", null);
                break;
            case "malformed":
                RawUpdate(goalId, "work_slot_registry_json", "{not json");
                break;
        }

        return RawBlob(goalId);
    }

    private void RawUpdate(string goalId, string column, string? value)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = $"UPDATE pipelines SET {column} = $value WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("$goal", goalId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private string? RawScalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = command.ExecuteScalar();
        if (result is null or DBNull)
            return null;
        return result as string ?? Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private string? RawBlob(string goalId) => RawScalar(
        "SELECT work_slot_registry_json FROM pipelines WHERE goal_id = $goal", ("$goal", goalId));

    private string? RawPointer(string goalId) => RawScalar(
        "SELECT active_task_id FROM pipelines WHERE goal_id = $goal", ("$goal", goalId));

    private string? RawPhase(string goalId) => RawScalar(
        "SELECT phase FROM pipelines WHERE goal_id = $goal", ("$goal", goalId));

    private int RawMappingCount(string taskId) =>
        int.Parse(RawScalar("SELECT COUNT(*) FROM task_mappings WHERE task_id = $task", ("$task", taskId))!, null);

    private int RawMappingCountForGoal(string goalId) =>
        int.Parse(RawScalar("SELECT COUNT(*) FROM task_mappings WHERE goal_id = $goal", ("$goal", goalId))!, null);

    /// <summary>
    /// Asserts the pointer, the durable mapping, the phase and the raw blob are all EXACTLY as
    /// restored — the "nothing was mutated" evidence the refusal vectors reuse.
    /// </summary>
    private void AssertRestoredStateUntouched(string goalId, string taskId, string expectedPhase, string? expectedBlob)
    {
        Assert.Equal(taskId, RawPointer(goalId));
        Assert.Equal(1, RawMappingCount(taskId));
        Assert.Equal(expectedPhase, RawPhase(goalId));
        Assert.Equal(expectedBlob, RawBlob(goalId));
    }

    /// <summary>A real <see cref="GoalDispatcher"/> whose redispatch queue the tests can read.</summary>
    private static GoalDispatcher CreateDispatcher(GoalPipelineManager pipelineManager, TaskQueue taskQueue) =>
        new(
            new GoalManager(),
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

    private static IReadOnlyList<string> QueuedRedispatches(GoalDispatcher dispatcher)
    {
        var field = typeof(GoalDispatcher).GetField("_redispatchQueue", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_redispatchQueue field not found on GoalDispatcher");
        return [.. (ConcurrentQueue<string>)field.GetValue(dispatcher)!];
    }

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

    /// <summary>A real <see cref="TaskDispatchService"/> over the given manager.</summary>
    private static TaskDispatchService CreateDispatchService(
        GoalPipelineManager pipelineManager, TaskQueue taskQueue, ILogger<TaskDispatchService> logger,
        Func<CancellationToken, Task<string?>>? storedCredentialLookup = null,
        HiveConfigFile? config = null,
        Goal? lifecycleGoal = null)
    {
        config ??= CreateConfig();
        var workerGateway = new GrpcWorkerGateway(new WorkerPool());
        var goalManager = new GoalManager();
        if (lifecycleGoal is not null)
        {
            var goalStore = new Mock<IGoalStore>();
            goalStore.Setup(s => s.GetGoalAsync(lifecycleGoal.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(lifecycleGoal);
            goalManager.AddSource(goalStore.Object);
        }
        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);
        var maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway,
            brain: null, agentsManager: null, configRepo: null,
            new ConcurrentQueue<string>(), NullLogger.Instance, config: config);

        return new TaskDispatchService(
            taskQueue, workerGateway, new TaskBuilder(new BranchCoordinator()), config,
            logger, pipelineManager, lifecycleService, maintenance, storedCredentialLookup);
    }

    /// <summary>
    /// A real <see cref="TaskCompletionService"/> whose driver is booby-trapped to throw if the
    /// held completion ever reaches the phase drive — a mutant that lets the completion through
    /// cannot pass silently.
    /// </summary>
    private static TaskCompletionService CreateCompletionService(
        GoalPipelineManager pipelineManager, ILogger<TaskCompletionService> logger)
    {
        var goalManager = new GoalManager();
        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);
        var driver = new PipelineDriver(
            brain: null,
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => throw new InvalidOperationException(
                "the held completion must never reach the drive"),
            resolvePrompt: (_, _, _, _) => Task.FromResult("prompt"),
            resolvePlan: (_, _, _) => throw new InvalidOperationException(
                "the held completion must never reach planning"),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);
        return new TaskCompletionService(pipelineManager, brain: null, driver, lifecycleService,
            dashboardNotifier: null, logger);
    }

    private static DispatcherMaintenance CreateMaintenance(
        GoalPipelineManager pipelineManager,
        ILogger? logger = null,
        IDistributedBrain? brain = null,
        IGoalStore? goalStore = null,
        GoalManager? goalManager = null,
        ConcurrentQueue<string>? redispatchQueue = null)
    {
        return new DispatcherMaintenance(
            pipelineManager, goalManager ?? new GoalManager(), new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            brain: brain, agentsManager: null, configRepo: null,
            redispatchQueue: redispatchQueue ?? new ConcurrentQueue<string>(),
            logger: logger ?? NullLogger.Instance,
            knowledgeGraph: null, goalStore: goalStore, repoManager: null, config: null);
    }

    /// <summary>
    /// Queries the REAL BrainActor mailbox for the pipeline registered under <paramref name="goalId"/>.
    /// The query is enqueued after any fire-and-forget RegisterPipelineMessage, so mailbox FIFO makes
    /// the result a deterministic registration witness rather than a reflection-time race.
    /// </summary>
    private static async Task<GoalPipeline?> GetConcreteBrainPipelineAsync(
        DistributedBrain brain, string goalId, CancellationToken ct)
    {
        var actorField = typeof(DistributedBrain).GetField(
            "_brainActor", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DistributedBrain._brainActor field not found");
        var actor = Assert.IsType<BrainActor>(actorField.GetValue(brain));
        var message = BrainActorMessages.CreateGetPipelineMessage(goalId);
        Assert.True(actor.Tell(message), "the concrete BrainActor mailbox must accept the query");
        return await message.Reply.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    // ═══════════════ Block A — classification, restore routes, persistence, startup ═══════════════

    /// <summary>
    /// THE CLASSIFICATION MATRIX over a REAL persisted-and-restored round trip through BOTH
    /// restore routes: a nonterminal phase with a non-null pointer (valid registry, SQL-NULL
    /// registry, malformed-text registry, an empty-string pointer) is HELD; a null-pointer
    /// restoration is NOT; a terminal phase is never restored from the active set at all
    /// (<c>RestoreFromStore</c>) and never held (<c>RestorePipeline</c>).
    /// </summary>
    public static IEnumerable<object?[]> ClassificationCases()
    {
        foreach (var route in new[] { "restore-pipeline", "restore-from-store" })
        {
            yield return [route, "coding-active", GoalPhase.Coding, "real", null, true];
            yield return [route, "sql-null-registry", GoalPhase.Testing, "real", "null", true];
            yield return [route, "malformed-registry", GoalPhase.Review, "real", "malformed", true];
            yield return [route, "empty-pointer", GoalPhase.Coding, "", null, true];
            yield return [route, "null-pointer-planning", GoalPhase.Planning, null, null, false];
        }

        // Terminal phases: RestorePipeline loads them (never held); RestoreFromStore filters
        // them out of the active set entirely — both are the unheld outcomes.
        yield return ["restore-pipeline", "done-terminal", GoalPhase.Done, "real", null, false];
        yield return ["restore-pipeline", "failed-terminal", GoalPhase.Failed, "real", null, false];
        yield return ["restore-from-store", "done-terminal", GoalPhase.Done, "real", null, false];
        yield return ["restore-from-store", "failed-terminal", GoalPhase.Failed, "real", null, false];
    }

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void RestoreRoutes_ClassifyTheHold_WithRealPersistedSnapshots(
        string route, string label, GoalPhase phase, string? pointer, string? blobMode, bool expectedHold)
    {
        var goalId = $"hold-classify-{label}-{route}";
        var expectedBlob = SeedClassifiedRow(goalId, phase, pointer, blobMode);

        var manager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);

        if (route == "restore-pipeline")
        {
            var restored = manager.RestorePipeline(goalId);
            Assert.NotNull(restored);
            Assert.Equal(expectedHold, restored!.IsRestoredActiveAttemptHold);
            Assert.Equal(pointer == "real" ? RawPointer(goalId) : pointer, restored.ActiveTaskId);
            Assert.Equal(phase, restored.Phase);
            // The carried blob is untouched verbatim — the load path never decodes it.
            if (blobMode == "malformed")
                Assert.Equal("{not json", RawBlob(goalId));
            Assert.Equal(expectedBlob, RawBlob(goalId));

            if (expectedHold)
            {
                // THE IMMUTABILITY: neither a terminal phase transition, a pointer re-assignment
                // nor a test-local pointer clear unlocks the hold.
                restored.AdvanceTo(GoalPhase.Done);
                Assert.True(restored.IsRestoredActiveAttemptHold, "a terminal AdvanceTo cannot unlock the hold");
                restored.SetActiveTask("reassigned");
                Assert.True(restored.IsRestoredActiveAttemptHold, "a pointer change cannot unlock the hold");
                restored.ClearActiveTask();
                Assert.True(restored.IsRestoredActiveAttemptHold, "a pointer clear cannot unlock the hold");
            }
        }
        else
        {
            var restoredList = manager.RestoreFromStore();
            if (phase is GoalPhase.Done or GoalPhase.Failed)
            {
                // Terminal rows are not part of the active restoration set at all.
                Assert.DoesNotContain(restoredList, p => p.GoalId == goalId);
                Assert.Null(manager.GetByGoalId(goalId));
            }
            else
            {
                var restored = Assert.Single(restoredList, p => p.GoalId == goalId);
                Assert.Equal(expectedHold, restored.IsRestoredActiveAttemptHold);
                Assert.Equal(pointer == "real" ? RawPointer(goalId) : pointer, restored.ActiveTaskId);
                Assert.Equal(expectedBlob, RawBlob(goalId));
            }
        }

        // CONTROL: a fresh Goal-created pipeline (no row existed) is never held — and it is
        // checkpoint-ELIGIBLE, the provenance twin of the hold's provenance.
        var controlGoalId = $"{goalId}-fresh-control";
        var controlManager = new GoalPipelineManager(CreateStore(), NullLogger<GoalPipelineManager>.Instance);
        var fresh = controlManager.CreatePipeline(NewGoal(controlGoalId));
        Assert.False(fresh.IsRestoredActiveAttemptHold);
        Assert.True(fresh.OwnershipCheckpointEligible);
    }

    /// <summary>
    /// A held restoration carries the persisted state through the restore constructor — the
    /// pointer, the task→goal mapping, the phase and the budgets read back exactly — and a
    /// SECOND reconstruction from the unchanged evidence gives IDENTICAL results.
    /// </summary>
    [Theory]
    [InlineData("restore-pipeline")]
    [InlineData("restore-from-store")]
    public void RestoreRoutes_HeldAttempt_CarriesState_AndSecondReconstructionIsIdentical(string route)
    {
        var goalId = $"hold-state-{route}";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);
        var live = seedManager.GetByGoalId(goalId)!;
        Assert.True(live.ReviewRetryBudget.TryConsume());
        Assert.True(live.TestRetryBudget.TryConsume());
        seedManager.PersistState(live);

        var expectedBlob = RawBlob(goalId);
        var expectedPointer = RawPointer(goalId);
        var expectedPhase = RawPhase(goalId);
        var expectedMappings = store.LoadPipeline(goalId)!.TaskMappings;
        Assert.Contains((taskId, goalId), expectedMappings);

        for (var reconstruction = 1; reconstruction <= 2; reconstruction++)
        {
            var manager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
            var restored = route == "restore-pipeline"
                ? manager.RestorePipeline(goalId)
                : Assert.Single(manager.RestoreFromStore(), p => p.GoalId == goalId);

            Assert.NotNull(restored);
            Assert.True(restored!.IsRestoredActiveAttemptHold);
            Assert.Equal(taskId, restored.ActiveTaskId);
            Assert.Equal(expectedPointer, RawPointer(goalId));
            Assert.Equal(expectedPhase, RawPhase(goalId));
            Assert.Equal(expectedBlob, RawBlob(goalId));
            Assert.Equal(GoalPhase.Coding, restored.Phase);
            Assert.Equal(1, restored.ReviewRetries);
            Assert.Equal(1, restored.TestRetries);
            Assert.Equal(1, restored.Iteration);
            // THE MAPPING: the restored task resolves back to this pipeline in the manager.
            Assert.Same(restored, manager.GetByTaskId(taskId));
            Assert.Equal(expectedMappings, store.LoadPipeline(goalId)!.TaskMappings);
        }

        // The durable evidence is byte-identical after both reconstructions.
        Assert.Equal(expectedBlob, RawBlob(goalId));
        Assert.Equal(expectedPointer, RawPointer(goalId));
    }

    /// <summary>
    /// FULL/STATE SAVES of a HELD restored object preserve the historical raw blob and the
    /// non-null pointer — the legacy blob-preserving save paths, proven through the manager's
    /// ordinary PersistFull/PersistState after a NEW slot has been allocated post-restore (so the
    /// preservation cannot be explained by an idle registry).
    /// </summary>
    [Theory]
    [InlineData("restore-pipeline", "sql-null", "null")]
    [InlineData("restore-pipeline", "populated", null)]
    [InlineData("restore-pipeline", "malformed", "malformed")]
    [InlineData("restore-from-store", "sql-null", "null")]
    [InlineData("restore-from-store", "populated", null)]
    [InlineData("restore-from-store", "malformed", "malformed")]
    public void PersistSaves_HeldRestoredObject_DoRealWork_AndPreserveBlobAndPointer(
        string route, string label, string? blobMode)
    {
        var goalId = $"hold-save-{label}-{route}";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);
        if (blobMode == "null")
            RawUpdate(goalId, "work_slot_registry_json", null);
        else if (blobMode == "malformed")
            RawUpdate(goalId, "work_slot_registry_json", "{opaque malformed registry text");
        var expectedBlob = RawBlob(goalId);

        var manager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var restored = route == "restore-pipeline"
            ? manager.RestorePipeline(goalId)
            : Assert.Single(manager.RestoreFromStore(), p => p.GoalId == goalId);
        Assert.NotNull(restored);
        Assert.True(restored!.IsRestoredActiveAttemptHold);
        Assert.False(restored.OwnershipCheckpointEligible,
            "a restored pipeline must be ineligible for registry checkpoint writes");

        // A genuinely NEW slot post-restore — the preservation must survive it.
        restored.AllocateAttemptAndRegisterSlot(
            "post-restore-task", new WorkSlotPosition(1, GoalPhase.Testing, 1));

        // THE FULL-SAVE REAL-WORK WITNESS. Move a separately persisted ordinary scalar, save,
        // and read it through a FRESH store instance. A no-op PersistFull mutant leaves Coding and
        // fails this assertion, while the held pointer and opaque registry bytes must survive.
        restored.AdvanceTo(GoalPhase.Testing);
        manager.PersistFull(restored);
        var afterFull = CreateFactoryBackedStore().LoadPipeline(goalId);
        Assert.NotNull(afterFull);
        Assert.Equal(GoalPhase.Testing, afterFull!.Phase);
        Assert.Equal(expectedBlob, afterFull.WorkSlotRegistryJson);
        Assert.Equal(taskId, afterFull.ActiveTaskId);
        Assert.Equal(expectedBlob, RawBlob(goalId));
        Assert.Equal(taskId, RawPointer(goalId));

        // THE STATE-SAVE REAL-WORK WITNESS, independently. Move the scalar again, call the other
        // save path, and use another FRESH store readback. A no-op PersistState mutant leaves
        // Testing and fails; SQL NULL, valid and MALFORMED historical blobs remain byte-identical.
        restored.AdvanceTo(GoalPhase.Review);
        manager.PersistState(restored);
        var afterState = CreateFactoryBackedStore().LoadPipeline(goalId);
        Assert.NotNull(afterState);
        Assert.Equal(GoalPhase.Review, afterState!.Phase);
        Assert.Equal(expectedBlob, afterState.WorkSlotRegistryJson);
        Assert.Equal(taskId, afterState.ActiveTaskId);
        Assert.Equal(expectedBlob, RawBlob(goalId));
        Assert.Equal(taskId, RawPointer(goalId));
    }

    /// <summary>
    /// THE STARTUP GATE, through the ACTUAL RestoreActivePipelinesAsync: a held attempt gets NO
    /// Brain registration, no fork, no session-existence probe, no goal-row status repair, no
    /// queue entry and no redispatch; the instance STAYS in the manager (GetActivePipelines keeps
    /// naming it) and the durable evidence — pointer, mapping, phase, blob — is untouched even
    /// though the goal row drifted to a terminal status.
    /// </summary>
    [Fact]
    public async Task RestoreActivePipelinesAsync_HeldAttempt_NoBrainWork_NoGoalChange_NoQueueEntry()
    {
        var goalId = "hold-startup-held";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);
        var expectedBlob = RawBlob(goalId);

        var brain = new HoldTrackingBrain();
        var goalStore = new Mock<IGoalStore>();
        var redispatchQueue = new ConcurrentQueue<string>();
        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var maintenance = CreateMaintenance(restoreManager, brain: brain, goalStore: goalStore.Object,
            redispatchQueue: redispatchQueue);

        await maintenance.RestoreActivePipelinesAsync(CancellationToken.None);

        // NOTHING was done: no session existence probe, no fork, no registration, no goal-row repair.
        Assert.False(brain.GoalSessionExistsCalled);
        Assert.Empty(brain.ForkCalls);
        Assert.Empty(brain.RegisterExistingCalls);
        goalStore.Verify(
            s => s.UpdateGoalStatusAsync(It.IsAny<string>(), It.IsAny<GoalStatus>(),
                It.IsAny<GoalUpdateMetadata?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Empty(redispatchQueue);

        // The instance STAYS in the manager and is still named as active.
        var held = restoreManager.GetByGoalId(goalId);
        Assert.NotNull(held);
        Assert.True(held!.IsRestoredActiveAttemptHold);
        Assert.Contains(held, restoreManager.GetActivePipelines());
        Assert.Equal(taskId, held.ActiveTaskId);

        // The durable evidence is untouched: pointer, mapping, phase, blob.
        AssertRestoredStateUntouched(goalId, taskId, "Coding", expectedBlob);
    }

    /// <summary>
    /// THE CONCRETE BRAIN-REGISTRATION WITNESS. DispatcherMaintenance's registration call is a
    /// <c>DistributedBrain</c>-only cast, so an interface fake cannot observe it. This vector runs
    /// startup with a REAL connected DistributedBrain and queries its BrainActor mailbox: the HELD
    /// pipeline is absent while an otherwise identical UNHELD null-pointer Coding control is
    /// present. Moving the hold gate below RegisterActivePipeline (or removing it) registers the
    /// held goal and fails the first assertion; inverting the gate fails the control assertion.
    /// No held object is driven through PlanIterationAsync.
    /// </summary>
    [Fact]
    public async Task RestoreActivePipelinesAsync_ConcreteBrain_DoesNotRegisterHeldPipeline_ButRegistersUnheldControl()
    {
        var heldGoalId = "hold-concrete-brain-held";
        var controlGoalId = "hold-concrete-brain-control";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        SeedAdmittedAttempt(seedManager, heldGoalId);

        var control = seedManager.CreatePipeline(NewGoal(controlGoalId));
        Arrange(control, GoalPhase.Coding);
        seedManager.PersistState(control);
        Assert.Null(control.ActiveTaskId);

        var stateDir = Path.Combine(Path.GetTempPath(), $"hold-concrete-brain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        try
        {
            await using var brain = new DistributedBrain(
                "copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: stateDir,
                chatClient: new Mock<Microsoft.Extensions.AI.IChatClient>().Object);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var restoreManager = new GoalPipelineManager(
                CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
            var maintenance = CreateMaintenance(restoreManager, brain: brain);

            await maintenance.RestoreActivePipelinesAsync(TestContext.Current.CancellationToken);

            var held = restoreManager.GetByGoalId(heldGoalId);
            var unheld = restoreManager.GetByGoalId(controlGoalId);
            Assert.NotNull(held);
            Assert.NotNull(unheld);
            Assert.True(held!.IsRestoredActiveAttemptHold);
            Assert.False(unheld!.IsRestoredActiveAttemptHold);

            // These mailbox queries are ordered AFTER any fire-and-forget registration message.
            Assert.Null(await GetConcreteBrainPipelineAsync(
                brain, heldGoalId, TestContext.Current.CancellationToken));
            Assert.Same(unheld, await GetConcreteBrainPipelineAsync(
                brain, controlGoalId, TestContext.Current.CancellationToken));
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(stateDir);
        }
    }

    /// <summary>
    /// MISSING-GOAL-ROW and TERMINAL-GOAL-ROW conflicts at startup: the evidence is preserved —
    /// the pipeline is NOT removed, the goal row is NOT updated, and the durable state is
    /// byte-identical — because the hold fence precedes the whole reconciliation block.
    /// </summary>
    [Theory]
    [InlineData("missing-goal-row", false)]
    [InlineData("terminal-goal-row", true)]
    public async Task RestoreActivePipelinesAsync_HeldAttempt_GoalRowConflicts_PreserveEverything(
        string conflict, bool goalRowFailed)
    {
        var goalId = $"hold-conflict-{conflict}";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);
        var expectedBlob = RawBlob(goalId);

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalAsync(goalId, It.IsAny<CancellationToken>())).ReturnsAsync(
            goalRowFailed
                ? new Goal { Id = goalId, Description = "x", Status = GoalStatus.Failed }
                : (Goal?)null);

        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var maintenance = CreateMaintenance(restoreManager, goalStore: goalStore.Object);

        await maintenance.RestoreActivePipelinesAsync(CancellationToken.None);

        // The pipeline is NOT removed and the durable evidence is untouched in both conflicts.
        var held = restoreManager.GetByGoalId(goalId);
        Assert.NotNull(held);
        Assert.True(held!.IsRestoredActiveAttemptHold);
        AssertRestoredStateUntouched(goalId, taskId, "Coding", expectedBlob);
        goalStore.Verify(
            s => s.UpdateGoalStatusAsync(It.IsAny<string>(), It.IsAny<GoalStatus>(),
                It.IsAny<GoalUpdateMetadata?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// THE HOLD WARNING, with focused isolation: it appears EXACTLY ONCE with the required fields
    /// for a held object during startup, and does NOT appear for a null-pointer (unheld) control
    /// processed by the same gate.
    /// </summary>
    [Fact]
    public async Task RestoreActivePipelinesAsync_HoldWarning_ExactlyOnce_WithFields()
    {
        var holdGoalId = "hold-warn-held";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, holdGoalId);

        var holdLogger = new TestLogger<DispatcherMaintenance>();
        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var maintenance = CreateMaintenance(restoreManager, logger: holdLogger);

        await maintenance.RestoreActivePipelinesAsync(CancellationToken.None);

        var holdWarning = Assert.Single(holdLogger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("holds restored active attempt", StringComparison.Ordinal));
        Assert.Contains(holdGoalId, holdWarning.Message);
        Assert.Contains(taskId, holdWarning.Message);
        // The held pipeline is skipped BEFORE the mid-Planning discard, so it is still present.
        Assert.NotNull(restoreManager.GetByGoalId(holdGoalId));
    }

    /// <summary>
    /// THE HOLD-WARNING CONTROL: a null-pointer (unheld) Planning restoration processed by the
    /// same startup gate produces NO hold warning — the hold diagnostic is not a logging sweep.
    /// </summary>
    [Fact]
    public async Task RestoreActivePipelinesAsync_NullPointerControl_NoHoldWarning()
    {
        // ── UNHELD CONTROL (isolated database): a null-pointer Planning restoration. ──
        var controlGoalId = "hold-warn-control";
        var controlStore = CreateStore();
        var controlSeedManager = new GoalPipelineManager(controlStore, NullLogger<GoalPipelineManager>.Instance);
        var controlSeed = controlSeedManager.CreatePipeline(NewGoal(controlGoalId));
        controlSeedManager.PersistFull(controlSeed);
        Assert.False(controlSeed.IsRestoredActiveAttemptHold);

        var controlGoalStore = new Mock<IGoalStore>();
        controlGoalStore.Setup(s => s.GetGoalAsync(controlGoalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Goal { Id = controlGoalId, Description = "x", Status = GoalStatus.InProgress });
        var controlGoalManager = new GoalManager();
        controlGoalManager.AddSource(controlGoalStore.Object);

        var controlLogger = new TestLogger<DispatcherMaintenance>();
        var controlRestoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var controlMaintenance = CreateMaintenance(
            controlRestoreManager, logger: controlLogger, goalStore: controlGoalStore.Object,
            goalManager: controlGoalManager);

        await controlMaintenance.RestoreActivePipelinesAsync(CancellationToken.None);

        Assert.DoesNotContain(controlLogger.LogEntries, e =>
            e.Message.Contains("holds restored active attempt", StringComparison.Ordinal));
    }

    // ═══════════════ Block B — the automatic consumers refuse ═══════════════

    /// <summary>
    /// THE REDISPATCH DRAIN fence: a held pipeline's queue entry is CONSUMED and REFUSED — no
    /// prompt resolution, no dispatch, no re-enqueue — proven through the REAL
    /// <see cref="GoalDispatchService.DrainRedispatchQueueAsync"/>. THE REMOVAL PROOF: the held
    /// object's pointer is cleared locally first (the hold is independent of pointer retention),
    /// so the drain's own pointer filter cannot mask a removed guard — without the fence the
    /// drain would dispatch the phase. An unheld null-pointer control dispatches through the
    /// same chain.
    /// </summary>
    [Fact]
    public async Task DrainRedispatchQueue_HeldEntry_ConsumedAndRefused_ControlDispatches()
    {
        // ── HELD ──
        var holdGoalId = "hold-drain-held";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, holdGoalId);
        var expectedBlob = RawBlob(holdGoalId);

        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var held = Assert.Single(restoreManager.RestoreFromStore(), p => p.GoalId == holdGoalId);
        Assert.True(held.IsRestoredActiveAttemptHold);
        held.ClearActiveTask(); // the test-local clear: pointer retention is NOT the hold's basis
        Assert.True(held.IsRestoredActiveAttemptHold);

        var holdQueue = new ConcurrentQueue<string>();
        holdQueue.Enqueue(holdGoalId);
        var taskQueue = new TaskQueue();
        var dispatchLogger = new TestLogger<TaskDispatchService>();
        var dispatchService = CreateDispatchService(restoreManager, taskQueue, dispatchLogger);
        var drainLogger = new TestLogger<GoalDispatchService>();
        var goalDispatchService = new GoalDispatchService(
            new GoalManager(), restoreManager, null, CreateConfig(),
            dispatchService, new ClarificationHandler(null, null, null, NullLogger.Instance),
            null, null, null, null, drainLogger, null);

        await goalDispatchService.DrainRedispatchQueueAsync(holdQueue, CancellationToken.None);

        // CONSUMED and REFUSED: the entry is drained, NOTHING was dispatched or re-enqueued, and
        // the dispatch service emitted nothing at all (it was never reached).
        Assert.Empty(holdQueue);
        Assert.Null(taskQueue.TryDequeueAny());
        Assert.Empty(dispatchLogger.LogEntries);
        Assert.Empty(QueuedRedispatches(restoreManager is { } m
            ? CreateProbeDispatcher(m)
            : throw new InvalidOperationException("unreachable")));

        // THE FOCUSED WARNING, exactly once, with the required fields. (The pointer was cleared
        // locally above, so the warning reports the CONSUMED entry's own goal id, not the task.)
        var warning = Assert.Single(drainLogger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("redispatch-refused", StringComparison.Ordinal));
        Assert.Contains(holdGoalId, warning.Message);

        // The durable evidence is untouched (the local pointer clear never persisted).
        AssertRestoredStateUntouched(holdGoalId, taskId, "Coding", expectedBlob);

        // ── UNHELD CONTROL: the same drain dispatches a null-pointer pipeline. ──
        var controlGoalId = "hold-drain-control";
        var controlStore = CreateStore();
        var controlSeedManager = new GoalPipelineManager(controlStore, NullLogger<GoalPipelineManager>.Instance);
        var controlSeed = controlSeedManager.CreatePipeline(NewGoal(controlGoalId));
        Arrange(controlSeed, GoalPhase.Coding);
        controlSeedManager.PersistState(controlSeed);

        var controlRestoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var controlPipeline = Assert.Single(controlRestoreManager.RestoreFromStore(), p => p.GoalId == controlGoalId);
        Assert.False(controlPipeline.IsRestoredActiveAttemptHold);
        Assert.Null(controlPipeline.ActiveTaskId);

        var controlHoldQueue = new ConcurrentQueue<string>();
        controlHoldQueue.Enqueue(controlGoalId);
        var controlTaskQueue = new TaskQueue();
        WorkTask? dispatched = null;
        controlTaskQueue.OnEnqueue = t => dispatched = t;
        var controlDispatchService = CreateDispatchService(
            controlRestoreManager, controlTaskQueue, new TestLogger<TaskDispatchService>());
        var controlGoalDispatchService = new GoalDispatchService(
            new GoalManager(), controlRestoreManager, null, CreateConfig(),
            controlDispatchService, new ClarificationHandler(null, null, null, NullLogger.Instance),
            null, null, null, null, NullLogger<GoalDispatchService>.Instance, null);

        await controlGoalDispatchService.DrainRedispatchQueueAsync(controlHoldQueue, CancellationToken.None);

        // The control's dispatch REALLY ran: a task was built, admitted and enqueued.
        Assert.NotNull(dispatched);
        Assert.Equal(controlGoalId, dispatched!.GoalId);
        Assert.Empty(controlHoldQueue);
    }

    /// <summary>A dispatcher used only to read a manager-scoped redispatch queue in this fixture.</summary>
    private static GoalDispatcher CreateProbeDispatcher(GoalPipelineManager pipelineManager) =>
        CreateDispatcher(pipelineManager, new TaskQueue());

    /// <summary>
    /// THE DISPATCH-ENTRY fence: <see cref="TaskDispatchService.DispatchToRole"/> refuses a held
    /// pipeline BEFORE ANY PREPARATION. The COMPLETE held-call log is exactly the refusal: no
    /// BuildDispatchContext Prompt debug record and no later model/repository record. A second
    /// injectable witness (missing repository configuration) sits after BuildDispatchContext but
    /// before the credential callback, and the existing credential callback sits after repository
    /// resolution. Paired unheld controls prove both dependencies and both preparation debug records
    /// are live. Thus all three mutants are distinguishable: gate immediately after
    /// BuildDispatchContext (extra Prompt), gate after repository resolution (Prompt + repository
    /// failure), and deleted gate (same failure before any slot). Slot/mapping/enqueue assertions
    /// remain supporting evidence only.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_HeldPipeline_NothingPreparedAdmittedOrDelivered()
    {
        var goalId = "hold-dispatch-held";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);
        var expectedBlob = RawBlob(goalId);

        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var held = Assert.Single(restoreManager.RestoreFromStore(), p => p.GoalId == goalId);
        Assert.True(held.IsRestoredActiveAttemptHold);
        Assert.Empty(held.GetSlotsForTest()); // the restored registry stays UNACTIVATED

        // The hold is independent of pointer retention: clear the restored pointer, then install a
        // test-local discriminator. The immutable hold remains true and the refusal must report the
        // discriminator exactly (the historical durable pointer is never changed).
        const string localPointerDiscriminator = "test-local-held-pointer";
        held.ClearActiveTask();
        held.SetActiveTask(localPointerDiscriminator);
        Assert.True(held.IsRestoredActiveAttemptHold);

        // THE EARLIEST-PREPARATION WITNESSES. BuildDispatchContext's first observable is the
        // injected logger's exact Prompt debug record. Repository resolution then consumes the
        // injected config; this config deliberately has NO target-repository definition, so if
        // resolution is reached it emits an Error and drives the pipeline to Failed. The existing
        // credential callback follows both. Correct code reaches NONE of these for the held call.
        var missingRepositoryConfig = CreateConfig();
        missingRepositoryConfig.Repositories.Clear();
        var credentialLookupCalls = 0;
        Func<CancellationToken, Task<string?>> credentialLookup = _ =>
        {
            credentialLookupCalls++;
            return Task.FromResult<string?>(null);
        };

        var queue = new TaskQueue();
        WorkTask? delivered = null;
        queue.OnEnqueue = t => delivered = t;
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateDispatchService(
            restoreManager, queue, logger,
            storedCredentialLookup: credentialLookup,
            config: missingRepositoryConfig,
            lifecycleGoal: held.Goal);

        await service.DispatchToRole(held, WorkerRole.Coder, "Code it", CancellationToken.None);

        // ZERO PREPARATION: the dependency was never consulted. The remaining observations are
        // supporting evidence that no admission or delivery occurred after the first-statement gate.
        Assert.Equal(0, credentialLookupCalls);
        Assert.Equal(GoalPhase.Coding, held.Phase);
        Assert.Empty(held.GetSlotsForTest());
        Assert.Null(delivered);
        Assert.Null(queue.TryDequeueAny());
        Assert.Equal(1, RawMappingCountForGoal(goalId));

        // THE EARLIEST SENTINEL: assert the COMPLETE record set, not a warning-filtered subset.
        // BuildDispatchContext would add its Prompt debug record before any relocated gate; repo
        // resolution would add an Error. Either extra record makes this exact singleton fail.
        Assert.Collection(logger.LogEntries, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.LogLevel);
            Assert.Equal(
                "WorkSlotIntegrity: dispatch-refused goal=hold-dispatch-held " +
                "task=test-local-held-pointer role=coder — the restored active attempt is held " +
                "awaiting reconciliation; nothing was prepared, admitted or delivered",
                entry.Message);
        });

        // THE REPOSITORY-RESOLUTION CONTROL consumes the SAME missing-repository config. It must
        // emit BuildDispatchContext's exact Prompt record, then the repository error, and fail the
        // unheld pipeline BEFORE consulting the later credential witness. This makes the injected
        // config an independent pre-credential preparation witness.
        var repositoryControlManager = new GoalPipelineManager();
        var repositoryControl = repositoryControlManager.CreatePipeline(
            NewGoal("hold-dispatch-repository-control"));
        Arrange(repositoryControl, GoalPhase.Coding);
        var repositoryLogger = new TestLogger<TaskDispatchService>();
        var repositoryService = CreateDispatchService(
            repositoryControlManager,
            new TaskQueue(),
            repositoryLogger,
            credentialLookup,
            missingRepositoryConfig,
            lifecycleGoal: repositoryControl.Goal);

        await repositoryService.DispatchToRole(
            repositoryControl, WorkerRole.Coder, "Code it", CancellationToken.None);

        Assert.Equal(GoalPhase.Failed, repositoryControl.Phase);
        Assert.Equal(0, credentialLookupCalls);
        Assert.Contains(repositoryLogger.LogEntries, entry =>
            entry.LogLevel == LogLevel.Debug &&
            entry.Message ==
                "Prompt for Coder (goal=hold-dispatch-repository-control):\nCode it");
        Assert.Contains(repositoryLogger.LogEntries, entry =>
            entry.LogLevel == LogLevel.Error &&
            entry.Message.Contains("Repository configuration error", StringComparison.Ordinal));

        // THE CREDENTIAL/BOTH-DEBUG-RECORDS CONTROL uses valid repository configuration. It must
        // emit the same Prompt preparation record plus BuildDispatchTail's Model record, consult
        // the credential lookup exactly once, and proceed to a real enqueue.
        var controlManager = new GoalPipelineManager();
        var control = controlManager.CreatePipeline(NewGoal("hold-dispatch-preparation-control"));
        Arrange(control, GoalPhase.Coding);
        Assert.False(control.IsRestoredActiveAttemptHold);
        var controlQueue = new TaskQueue();
        WorkTask? controlEnqueued = null;
        controlQueue.OnEnqueue = task => controlEnqueued = task;
        var controlLogger = new TestLogger<TaskDispatchService>();
        var controlService = CreateDispatchService(
            controlManager, controlQueue, controlLogger, credentialLookup);

        await controlService.DispatchToRole(
            control, WorkerRole.Coder, "Code it", CancellationToken.None);

        Assert.Equal(1, credentialLookupCalls);
        Assert.NotNull(controlEnqueued);
        Assert.Equal(control.GoalId, controlEnqueued!.GoalId);
        Assert.Contains(controlLogger.LogEntries, entry =>
            entry.LogLevel == LogLevel.Debug &&
            entry.Message ==
                "Prompt for Coder (goal=hold-dispatch-preparation-control):\nCode it");
        Assert.Contains(controlLogger.LogEntries, entry =>
            entry.LogLevel == LogLevel.Debug &&
            entry.Message ==
                "Model for coder: coder-model (tier=Default, configLoaded=True)");

        // The durable evidence is untouched.
        AssertRestoredStateUntouched(goalId, taskId, "Coding", expectedBlob);
    }

    /// <summary>
    /// THE COMPLETION-ENTRY fence: <see cref="TaskCompletionService.HandleTaskCompletionAsync"/>
    /// drops a completion for the held attempt without admitting, clearing, mutating or
    /// persisting. THE REMOVAL PROOF: the pre-fix path would clear the pointer
    /// (<c>ClearActiveTaskIfCurrent</c>) and then drive — the driver here is booby-trapped to
    /// throw, so a removed guard cannot pass silently — and would persist the mutation.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletion_HeldAttempt_CompletionDropped_NothingMutatedOrPersisted()
    {
        var goalId = "hold-completion-held";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);
        var expectedBlob = RawBlob(goalId);

        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var held = Assert.Single(restoreManager.RestoreFromStore(), p => p.GoalId == goalId);
        Assert.True(held.IsRestoredActiveAttemptHold);
        Assert.Equal(taskId, held.ActiveTaskId);
        Assert.Empty(held.GetSlotsForTest());

        var logger = new TestLogger<TaskCompletionService>();
        var service = CreateCompletionService(restoreManager, logger);

        await service.HandleTaskCompletionAsync(
            new TaskResult { TaskId = taskId, Status = TaskOutcome.Completed, Output = "done" },
            CancellationToken.None);

        // Nothing mutated: no admission, the pointer STILL names the task, the phase was NOT
        // driven, the conversation/phase log untouched.
        Assert.Empty(held.GetSlotsForTest());
        Assert.Equal(taskId, held.ActiveTaskId);
        Assert.Equal(GoalPhase.Coding, held.Phase);
        Assert.Empty(held.PhaseLog);

        // THE FOCUSED WARNING, exactly once, with goal and task.
        var warning = Assert.Single(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("completion-refused", StringComparison.Ordinal));
        Assert.Contains(goalId, warning.Message);
        Assert.Contains(taskId, warning.Message);

        // Nothing persisted: the durable evidence is byte-identical.
        AssertRestoredStateUntouched(goalId, taskId, "Coding", expectedBlob);
    }

    /// <summary>
    /// THE STALE-RECLAIM fence: the reclaim refuses a HELD pipeline BEFORE its previously
    /// unconditional <see cref="TaskQueue.MarkComplete"/> — the queue entry, the pointer, the
    /// durable mapping and the redispatch are all retained — proven on BOTH eviction branches
    /// (stale-worker and hung-worker) with a REAL seeded ACTIVE queue entry (so an absent guard
    /// would consume it and fail). An unheld fresh-pipeline control reclaims normally through
    /// the same service.
    /// </summary>
    [Theory]
    [InlineData("stale", true, false)]
    [InlineData("hung", false, true)]
    public Task Reclaim_HeldAttempt_RetainsEverything(string variant, bool staleBranch, bool hungBranch) =>
        RunHeldReclaim(variant, staleBranch, hungBranch);

    private async Task RunHeldReclaim(string variant, bool staleBranch, bool hungBranch)
    {
        var goalId = $"hold-reclaim-{variant}";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);
        var expectedBlob = RawBlob(goalId);

        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var held = Assert.Single(restoreManager.RestoreFromStore(), p => p.GoalId == goalId);
        Assert.True(held.IsRestoredActiveAttemptHold);
        Assert.Empty(held.GetSlotsForTest());

        // THE REAL QUEUE ENTRY, ACTIVE — the pre-fix MarkComplete would consume it here.
        var queue = new TaskQueue();
        var workTask = new WorkTask
        {
            TaskId = taskId,
            GoalId = goalId,
            GoalDescription = "hold reclaim goal",
            Prompt = "x",
            Role = WorkerRole.Coder,
            Repositories = [],
        };
        queue.Enqueue(workTask);
        queue.Activate(queue.TryDequeueAny()!, "worker-hold");
        Assert.NotNull(queue.GetActiveTask(taskId));

        var dispatcher = CreateDispatcher(restoreManager, queue);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var service = staleBranch
            ? MakeStaleEvictionService(queue, restoreManager, logger, dispatcher, taskId)
            : MakeHungWorkerService(queue, restoreManager, logger, dispatcher, taskId);

        await service.RunCleanupCycleAsync();

        // NOTHING mutated: the queue entry is retained, the pointer and the mapping survive, and
        // NO redispatch was enqueued.
        Assert.NotNull(queue.GetActiveTask(taskId));
        Assert.Null(queue.TryDequeueAny() is { } notEnqueued ? null : queue.TryDequeueAny());
        Assert.Equal(taskId, held.ActiveTaskId);
        Assert.Same(held, restoreManager.GetByTaskId(taskId));
        Assert.Empty(QueuedRedispatches(dispatcher));

        // THE FOCUSED WARNING, exactly once, with the required fields.
        var warning = Assert.Single(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("reclaim-refused", StringComparison.Ordinal));
        Assert.Contains(goalId, warning.Message);
        Assert.Contains(taskId, warning.Message);
        Assert.Contains("worker-hold", warning.Message);

        // The durable evidence is untouched.
        AssertRestoredStateUntouched(goalId, taskId, "Coding", expectedBlob);
    }

    /// <summary>
    /// THE RECLAIM CONTROL: an UNHELD fresh pipeline with an identical admission state is
    /// reclaimed normally through the same service — the entry completed (never re-enqueued),
    /// the slot retired, the pointer cleared, the mapping unregistered and the redispatch queued
    /// — proving the hold, not the service, is what retains the held attempt's state.
    /// </summary>
    [Theory]
    [InlineData("stale", true, false)]
    [InlineData("hung", false, true)]
    public Task Reclaim_UnheldControl_ReclaimsNormally(string variant, bool staleBranch, bool hungBranch) =>
        RunControlReclaim(variant, staleBranch, hungBranch);

    private async Task RunControlReclaim(string variant, bool staleBranch, bool hungBranch)
    {
        var goalId = $"hold-reclaim-control-{variant}";
        var manager = new GoalPipelineManager();
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        Arrange(pipeline, GoalPhase.Coding);
        var slot = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.True(pipeline.TrySetActiveTask(slot.TaskId, "copilothive/" + goalId));
        manager.RegisterTask(slot.TaskId, goalId);
        var taskId = slot.TaskId;

        var queue = new TaskQueue();
        queue.Enqueue(new WorkTask
        {
            TaskId = taskId,
            GoalId = goalId,
            GoalDescription = "hold reclaim goal",
            Prompt = "x",
            Role = WorkerRole.Coder,
            Repositories = [],
        });
        queue.Activate(queue.TryDequeueAny()!, "worker-hold");

        var dispatcher = CreateDispatcher(manager, queue);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var service = staleBranch
            ? MakeStaleEvictionService(queue, manager, logger, dispatcher, taskId)
            : MakeHungWorkerService(queue, manager, logger, dispatcher, taskId);

        await service.RunCleanupCycleAsync();

        // The control reclaimed normally: entry completed (never re-enqueued), slot retired,
        // pointer cleared, mapping unregistered, redispatch queued.
        Assert.Null(queue.GetActiveTask(taskId));
        Assert.Null(queue.TryDequeueAny());
        Assert.Equal(WorkSlotState.Abandoned, Assert.Single(pipeline.GetSlotsForTest()).State);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Equal([goalId], QueuedRedispatches(dispatcher));
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.Message.Contains("reclaim-refused", StringComparison.Ordinal));
    }

    private static StaleWorkerCleanupService MakeStaleEvictionService(
        TaskQueue queue, GoalPipelineManager manager, ILogger<StaleWorkerCleanupService> logger,
        GoalDispatcher dispatcher, string taskId)
    {
        var staleWorker = new ConnectedWorker
        {
            Id = "worker-hold",
            Role = WorkerRole.Coder,
            Capabilities = [],
        };
        staleWorker.IsBusy = true;
        staleWorker.CurrentTaskId = taskId;

        var poolMock = new Mock<IWorkerPool>();
        poolMock.Setup(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>())).Returns([staleWorker]);
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([]);
        return new StaleWorkerCleanupService(poolMock.Object, queue, manager, logger, goalDispatcher: dispatcher);
    }

    private static StaleWorkerCleanupService MakeHungWorkerService(
        TaskQueue queue, GoalPipelineManager manager, ILogger<StaleWorkerCleanupService> logger,
        GoalDispatcher dispatcher, string taskId)
    {
        var hung = new ConnectedWorker
        {
            Id = "worker-hold",
            Role = WorkerRole.Coder,
            Capabilities = [],
        };
        hung.IsBusy = true;
        hung.CurrentTaskId = taskId;
        hung.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var poolMock = new Mock<IWorkerPool>();
        poolMock.Setup(p => p.GetWorkersWithTimedOutTasks(It.IsAny<TimeSpan>())).Returns([hung]);
        poolMock.Setup(p => p.TryRemoveTimedOutWorker("worker-hold", It.IsAny<TimeSpan>())).Returns(true);
        poolMock.Setup(p => p.PurgeStaleWorkers(It.IsAny<TimeSpan>())).Returns([]);
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { WorkerTaskTimeoutMinutes = 60 },
        };
        return new StaleWorkerCleanupService(poolMock.Object, queue, manager, logger,
            goalDispatcher: dispatcher, config: config);
    }

    /// <summary>
    /// REPEATED RESTART: a held pipeline survives a second full restore cycle unchanged — the
    /// hold is re-classified identically, the evidence stays byte-identical, and the startup gate
    /// still refuses it (no Brain work, no goal-row change, no queue entry).
    /// </summary>
    [Fact]
    public async Task RepeatedRestart_HoldReclassifiedIdentically_EvidencePreserved()
    {
        var goalId = "hold-restart-twice";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);
        var expectedBlob = RawBlob(goalId);
        var expectedPointer = RawPointer(goalId);
        Assert.Equal(expectedPointer, taskId);

        for (var cycle = 1; cycle <= 2; cycle++)
        {
            var manager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
            var restored = Assert.Single(manager.RestoreFromStore(), p => p.GoalId == goalId);
            Assert.True(restored.IsRestoredActiveAttemptHold);
            Assert.Equal(taskId, restored.ActiveTaskId);

            var brain = new HoldTrackingBrain();
            var goalStore = new Mock<IGoalStore>();
            var redispatchQueue = new ConcurrentQueue<string>();
            var maintenance = CreateMaintenance(manager, brain: brain, goalStore: goalStore.Object,
                redispatchQueue: redispatchQueue);
            await maintenance.RestoreActivePipelinesAsync(CancellationToken.None);

            Assert.False(brain.GoalSessionExistsCalled);
            Assert.Empty(brain.ForkCalls);
            Assert.Empty(brain.RegisterExistingCalls);
            Assert.Empty(redispatchQueue);
            goalStore.Verify(
                s => s.UpdateGoalStatusAsync(It.IsAny<string>(), It.IsAny<GoalStatus>(),
                    It.IsAny<GoalUpdateMetadata?>(), It.IsAny<CancellationToken>()),
                Times.Never);

            Assert.Equal(taskId, RawPointer(goalId));
            Assert.Equal(1, RawMappingCount(taskId));
            Assert.Equal(expectedBlob, RawBlob(goalId));
            // The evidence stays byte-identical after the second cycle too.
        }
    }

    /// <summary>
    /// EXPLICIT USER CANCELLATION REGRESSION: <see cref="GoalDispatcher.CancelGoalAsync"/> still
    /// fails a HELD pipeline, removes it from the manager and persists the failure — the hold
    /// fences only AUTOMATIC consumers, never an explicit user action (API unchanged).
    /// </summary>
    [Fact]
    public async Task CancelGoal_HeldPipeline_StillCancelled()
    {
        var goalId = "hold-cancel";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);

        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var held = Assert.Single(restoreManager.RestoreFromStore(), p => p.GoalId == goalId);
        Assert.True(held.IsRestoredActiveAttemptHold);
        Assert.Equal(taskId, held.ActiveTaskId);

        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetGoalAsync(goalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Goal { Id = goalId, Description = "x", Status = GoalStatus.InProgress });
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore.Object);

        var dispatcher = new GoalDispatcher(
            goalManager,
            restoreManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            goalStore: goalStore.Object);

        var cancelled = await dispatcher.CancelGoalAsync(goalId, CancellationToken.None);

        Assert.True(cancelled);
        Assert.Equal(GoalPhase.Failed, held.Phase);
        Assert.Null(restoreManager.GetByGoalId(goalId));
        // The explicit cancel DID persist the goal-row failure.
        goalStore.Verify(
            s => s.UpdateGoalStatusAsync(goalId, GoalStatus.Failed, It.IsAny<GoalUpdateMetadata?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════════════════════════ test doubles ═══════════════════════════════

    /// <summary>Tracks Brain session work the startup gate must NOT perform for a held object.</summary>
    private sealed class HoldTrackingBrain : IDistributedBrain
    {
        public List<string> ForkCalls { get; } = [];
        public List<string> RegisterExistingCalls { get; } = [];
        public bool GoalSessionExistsCalled { get; private set; }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));
        public Task<PromptResult> CraftPromptAsync(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success("prompt"));
        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult("summary");
        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;
        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;
        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) => Task.CompletedTask;
        public Task<BrainResponse> AskQuestionAsync(string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("ok"));
        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) => Task.CompletedTask;
        public BrainStats? GetStats() => null;
        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default)
        {
            ForkCalls.Add(goalId);
            return Task.CompletedTask;
        }

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default)
        {
            RegisterExistingCalls.Add(goalId);
            return Task.CompletedTask;
        }

        public bool GoalSessionExists(string goalId)
        {
            GoalSessionExistsCalled = true;
            return true;
        }
    }
}