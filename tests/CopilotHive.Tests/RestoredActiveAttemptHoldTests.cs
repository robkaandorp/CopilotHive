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
/// (<see cref="GoalPipeline.IsRestoredActiveAttemptHold"/>) and every DESTRUCTIVE automatic step
/// refuses it — orchestrator restart alone is not permission to invalidate or replace a valid
/// attempt. The NON-DESTRUCTIVE Brain setup (registration plus the goal-session fork/reattach) is
/// performed for a held restore, exactly as it is for an unheld one, because a surviving worker may
/// reclaim the attempt at Register.
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

    // ── THE NO-LEAK SENTINELS. Each is a UNIQUE, unmistakable secret reachable through EXACTLY
    //    ONE forbidden channel, so every forbidden payload class can fail its vector on its own
    //    rather than being covered by a single shared heuristic.
    //
    //    They are deliberately not substrings of each other and contain no text the enriched
    //    warning legitimately emits (goal id, task id, classification names).

    /// <summary>Shared marker that makes an accidental sentinel match unmistakable in failures.</summary>
    private const string RawLeakSentinel = "LEAKCANARY";

    /// <summary>
    /// The UNKNOWN top-level JSON member carrying the RAW-ONLY sentinel. The codec ignores unknown
    /// members, so neither this name nor its value can appear in ANY decoded value — the only way
    /// either reaches a log sink is emitting the raw blob text.
    /// </summary>
    private const string RawOnlyLeakMember = "rawOnlyCanaryMember";

    /// <summary>The value of <see cref="RawOnlyLeakMember"/> — raw-registry-text channel only.</summary>
    private const string RawOnlyLeakSentinel = "raw-json-" + RawLeakSentinel;

    /// <summary>
    /// The secret embedded in MALFORMED registry text: reachable only through raw text or an
    /// unsanitized parser exception, whose message quotes the offending payload region.
    /// <para>
    /// IMPORTANT — this sentinel covers the RAW-TEXT channel of the malformed blob. It is NOT
    /// assumed to appear in the parser's own diagnostic: <c>System.Text.Json</c> reports a byte
    /// position for most malformed shapes rather than echoing nearby values. The parser-diagnostic
    /// channel is proven separately with <see cref="ParserEchoedFragment"/>, whose presence in the
    /// real inner message is OBSERVED at run time before any absence is asserted.
    /// </para>
    /// </summary>
    private const string ParserLeakSentinel = "parser-" + RawLeakSentinel;

    /// <summary>
    /// A distinctive fragment the JSON reader ECHOES VERBATIM into its own diagnostic. An invalid
    /// JSON literal makes <c>System.Text.Json</c> quote the offending token itself (for example
    /// <c>'tRUE…}' is an invalid JSON literal. Expected the literal 'true'.</c>), which is the one
    /// malformed shape whose parser text genuinely carries attributable content rather than only a
    /// byte offset. The vector PROVES this fragment is present in the real inner message before it
    /// asserts the fragment's absence from every emitted record.
    /// </summary>
    private const string ParserEchoedFragment = "PARSERECHO" + RawLeakSentinel;

    /// <summary>
    /// MALFORMED registry text carrying BOTH malformed-blob channels at once:
    /// <list type="bullet">
    ///   <item><description><see cref="ParserLeakSentinel"/> as ordinary blob text — reachable only
    ///     by emitting the RAW bytes (the parser never echoes it: it fails later, at the literal);</description></item>
    ///   <item><description><see cref="ParserEchoedFragment"/> inside an invalid JSON literal placed
    ///     where a value is expected, so <c>JsonDocument.Parse</c> fails while QUOTING that bad
    ///     token — the unsanitized-parser-text leak channel under test.</description></item>
    /// </list>
    /// </summary>
    private const string MalformedRegistryJson =
        "{\"version\":1,\"rawCanary\":\"" + ParserLeakSentinel + "\",\"slots\":tRUE" + ParserEchoedFragment + "}";

    /// <summary>
    /// Asserts that <paramref name="sentinel"/> appears in NO emitted record — neither in a
    /// message nor in any attached exception's text — across EVERY record the run produced, so a
    /// leak hiding on a second record cannot slip through.
    /// </summary>
    private static void AssertNoSentinelInRecords(
        TestLogger<DispatcherMaintenance> logger, string sentinel, string channel)
    {
        foreach (var record in logger.LogEntries)
        {
            Assert.False(
                record.Message.Contains(sentinel, StringComparison.OrdinalIgnoreCase),
                $"a record leaked {channel} (sentinel '{sentinel}'): {record.Message}");
            Assert.False(
                record.Exception?.ToString().Contains(sentinel, StringComparison.OrdinalIgnoreCase) == true,
                $"an attached exception leaked {channel} (sentinel '{sentinel}')");
        }
    }

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
        ConcurrentQueue<string>? redispatchQueue = null,
        TaskQueue? taskQueue = null)
    {
        return new DispatcherMaintenance(
            pipelineManager, goalManager ?? new GoalManager(), taskQueue ?? new TaskQueue(),
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
    /// THE STARTUP GATE, through the ACTUAL RestoreActivePipelinesAsync: a held attempt DOES get the
    /// NON-DESTRUCTIVE Brain setup — the goal session is probed and reattached, so a worker that
    /// reconnects can adopt the attempt at Register — while still skipping EVERY destructive step:
    /// no goal-row status repair, no queue entry and no redispatch. The instance STAYS in the manager
    /// (GetActivePipelines keeps naming it) and the durable evidence — pointer, mapping, phase, blob —
    /// is untouched even though the goal row drifted to a terminal status.
    /// </summary>
    [Fact]
    public async Task RestoreActivePipelinesAsync_HeldAttempt_BrainSetupButNoDestruction_NoGoalChange_NoQueueEntry()
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

        // THE NON-DESTRUCTIVE HALF IS PERFORMED: the session existence was probed and, because the
        // fake reports it already exists, the goal session was REATTACHED — exactly the unheld
        // path's own outcome, so the Brain tracks the goal a surviving worker may adopt into.
        Assert.True(brain.GoalSessionExistsCalled,
            "a held restoration gets the non-destructive Brain setup, so the session is probed");
        Assert.Empty(brain.ForkCalls);
        Assert.Equal([goalId], brain.RegisterExistingCalls);

        // THE DESTRUCTIVE HALF IS STILL SKIPPED: no goal-row repair and no redispatch.
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
    /// startup with a REAL connected DistributedBrain and queries its BrainActor mailbox: BOTH the
    /// HELD and the otherwise identical UNHELD null-pointer Coding control are present. Removing the
    /// held path's non-destructive setup — or restoring the early <c>continue</c> ahead of it —
    /// leaves the held goal unregistered and fails the held assertion; inverting or breaking the
    /// unheld path fails the control assertion, so a single shared call is still proven to serve
    /// both. No held object is driven through PlanIterationAsync.
    /// </summary>
    [Fact]
    public async Task RestoreActivePipelinesAsync_ConcreteBrain_RegistersHeldPipeline_AndUnheldControl()
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
            Assert.Same(held, await GetConcreteBrainPipelineAsync(
                brain, heldGoalId, TestContext.Current.CancellationToken));
            Assert.Same(unheld, await GetConcreteBrainPipelineAsync(
                brain, controlGoalId, TestContext.Current.CancellationToken));

            // THE HOLD SURVIVED THE REGISTRATION: the Brain knowing the goal is knowledge, not
            // authority, so the attempt is still held and its pointer is untouched.
            Assert.True(held.IsRestoredActiveAttemptHold);
            Assert.NotNull(held.ActiveTaskId);
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
    /// THE ROUND-2 ENRICHED HOLD WARNING — CLASSIFICATION VALUES ONLY, asserted against the
    /// pipeline's OWN restore-time facts so the log's payload cannot drift from the object:
    /// <list type="bullet">
    ///   <item><description>the registry classification, the active-slot classification and the
    ///     exact-ordinal mapping-present observation are all present in the emitted record, with
    ///     the EXACT values the pipeline instance reports;</description></item>
    ///   <item><description>the goal and task identity values appear;</description></item>
    ///   <item><description>the run emits EXACTLY ONE WARNING RECORD IN TOTAL — every warning the
    ///     maintenance instance emitted is counted, not merely the ones matching the hold phrase,
    ///     so a second leaking warning carrying NEITHER marker still fails;</description></item>
    ///   <item><description>NO-LEAK BY UNIQUE SENTINEL, one per forbidden channel, each able to
    ///     fail this vector INDEPENDENTLY: a sentinel that exists ONLY in the RAW registry text
    ///     (an unknown JSON member the codec ignores, so it can reach the sink only via the raw
    ///     blob) and a sentinel that exists ONLY in a DECODED slot field (a historical slot's task
    ///     id, reachable only by rendering decoded/captured registry state — including an object
    ///     rendering of a snapshot/slot). Neither may appear anywhere in the emitted records, and
    ///     no exception object may reach the sink. The MALFORMED-parser channel is proven by the
    ///     sibling vector, which needs an undecodable blob and therefore its own isolated
    ///     run;</description></item>
    ///   <item><description>the loop iteration performs NO DESTRUCTION behind the warning: no goal-row
    ///     read or status repair, no pointer clear, no TaskQueue.MarkComplete, no pipeline removal and
    ///     no redispatch enqueue — proven by a real tracking Brain, the REAL TaskQueue THE MAINTENANCE
    ///     INSTANCE ITSELF RECEIVED seeded with an ACTIVE entry for the held attempt (a MarkComplete on
    ///     that queue would consume it), a live Mock IGoalStore, and the untouched durable row. The
    ///     NON-DESTRUCTIVE Brain setup DOES run (it is asserted positively), because a surviving worker
    ///     may adopt the attempt at Register;</description></item>
    ///   <item><description>the classification stays observable WITHOUT the log line: after the
    ///     same startup run, the pipeline object itself reports the identical values.</description></item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task RestoreActivePipelinesAsync_EnrichedHoldWarning_ClassificationFieldsValuesOnly_NoLeak_NoWork()
    {
        var goalId = "hold-warn-enriched";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);

        // ── THE SENTINEL-BEARING EVIDENCE. Two distinct secrets enter through two DIFFERENT
        //    channels, so each forbidden payload class fails this vector on its own:
        //      RAW-ONLY   — an unknown top-level JSON member. The codec ignores unknown members,
        //                   so this text can reach a log sink ONLY by emitting the raw blob.
        //      DECODED-ONLY — a HISTORICAL slot's task id. It is not the active pointer and is
        //                   never an identity field, so it can reach a sink ONLY by rendering
        //                   decoded/captured registry state (a WorkSlotRegistrySnapshot or
        //                   WorkSlot object rendering included).
        var decodedSentinelTaskId = "decoded-payload-" + RawLeakSentinel;
        var richBlob = WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot(
            [
                // The ACTIVE attempt, kept Pending so the pointer classification stays meaningful.
                new WorkSlotView(new WorkSlot(taskId, new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
                    WorkSlotState.Pending),
                // A DEAD historical slot whose task id is the decoded-only sentinel.
                new WorkSlotView(
                    new WorkSlot(decodedSentinelTaskId, new WorkSlotPosition(1, GoalPhase.Testing, 1), 1),
                    WorkSlotState.Recorded),
            ],
            [
                new WorkSlotRegistryAttemptEntry(new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
                new WorkSlotRegistryAttemptEntry(new WorkSlotPosition(1, GoalPhase.Testing, 1), 1),
            ]));
        // Inject the raw-only sentinel as an unknown member — present in the TEXT, absent from
        // every decoded value.
        var blobWithRawSentinel = richBlob.Insert(1, $"\"{RawOnlyLeakMember}\":\"{RawOnlyLeakSentinel}\",");
        RawUpdate(goalId, "work_slot_registry_json", blobWithRawSentinel);

        var expectedBlob = RawBlob(goalId);
        Assert.Equal(blobWithRawSentinel, expectedBlob);
        var expectedPointer = RawPointer(goalId);
        Assert.Equal(taskId, expectedPointer);

        // THE REAL QUEUE ENTRY, ACTIVE — and THIS VERY INSTANCE is handed to the maintenance under
        // test below, so a MarkComplete on the maintenance's own queue would consume it here.
        var taskQueue = new TaskQueue();
        taskQueue.Enqueue(new WorkTask
        {
            TaskId = taskId,
            GoalId = goalId,
            GoalDescription = "enriched warning goal",
            Prompt = "x",
            Role = WorkerRole.Coder,
            Repositories = [],
        });
        taskQueue.Activate(taskQueue.TryDequeueAny()!, "worker-hold");
        Assert.NotNull(taskQueue.GetActiveTask(taskId));

        var brain = new HoldTrackingBrain();
        var goalStore = new Mock<IGoalStore>();
        var redispatchQueue = new ConcurrentQueue<string>();
        var logger = new TestLogger<DispatcherMaintenance>();
        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var maintenance = CreateMaintenance(restoreManager, logger: logger, brain: brain,
            goalStore: goalStore.Object, redispatchQueue: redispatchQueue, taskQueue: taskQueue);

        await maintenance.RestoreActivePipelinesAsync(CancellationToken.None);

        // THE EXACTLY-ONCE EMISSION, counted over EVERY warning this run produced — not only the
        // ones carrying the hold phrase — so an extra warning bearing no known marker still fails.
        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("holds restored active attempt", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel > LogLevel.Warning);

        // THE CLASSIFICATION VALUES, exactly the pipeline's own facts, so a value drift cannot
        // pass unnoticed; each is additionally cross-checked against the pipeline object below.
        Assert.Contains("registry evidence Restored", warning.Message, StringComparison.Ordinal);
        Assert.Contains("active slot ActiveSlotPending", warning.Message, StringComparison.Ordinal);
        Assert.Contains("active mapping present True", warning.Message, StringComparison.Ordinal);

        // THE IDENTITY VALUES.
        Assert.Contains(goalId, warning.Message, StringComparison.Ordinal);
        Assert.Contains(taskId, warning.Message, StringComparison.Ordinal);

        // THE NO-LEAK CONTRACT, sentinel-driven and checked across EVERY emitted record (message
        // AND any exception text), so a leak on a second record cannot hide. Each sentinel fails
        // independently: the raw one can only come from the blob text, the decoded one only from
        // rendered registry state.
        Assert.Null(warning.Exception);
        AssertNoSentinelInRecords(logger, RawOnlyLeakSentinel, "raw registry JSON text");
        AssertNoSentinelInRecords(logger, decodedSentinelTaskId, "a decoded registry slot field");
        // The unknown member's NAME is raw-text-only too — a whole-blob dump carries it.
        AssertNoSentinelInRecords(logger, RawOnlyLeakMember, "the raw blob's unknown JSON member");
        Assert.DoesNotContain(logger.LogEntries, e => e.Exception is not null);

        // THE HOLD IS STILL ENFORCED AGAINST DESTRUCTION, even though the NON-DESTRUCTIVE Brain
        // setup now runs after the warning: no goal-row READ or repair, no pointer clear, no queue
        // completion, no removal and no redispatch. The Brain's own knowledge work (the session
        // probe and reattach below) is explicitly NOT destruction.
        Assert.True(brain.GoalSessionExistsCalled,
            "the non-destructive Brain setup runs for a held restoration");
        Assert.Empty(brain.ForkCalls);
        Assert.Equal([goalId], brain.RegisterExistingCalls);
        goalStore.Verify(s => s.GetGoalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        goalStore.Verify(
            s => s.UpdateGoalStatusAsync(It.IsAny<string>(), It.IsAny<GoalStatus>(),
                It.IsAny<GoalUpdateMetadata?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // THE MAINTENANCE'S OWN QUEUE is the one inspected: its active entry survives untouched.
        Assert.NotNull(taskQueue.GetActiveTask(taskId));
        Assert.Equal(taskId, taskQueue.GetActiveTask(taskId)!.TaskId);
        Assert.Null(taskQueue.TryDequeueAny());
        Assert.Empty(redispatchQueue);

        // The instance STAYS in the manager, still active and still held.
        var held = restoreManager.GetByGoalId(goalId);
        Assert.NotNull(held);
        Assert.True(held!.IsRestoredActiveAttemptHold);
        Assert.False(held.OwnershipCheckpointEligible);
        Assert.Contains(held, restoreManager.GetActivePipelines());

        // THE CLASSIFICATION IS OBSERVABLE WITHOUT THE LOG: the object reports the values the
        // warning must have carried — including the boolean, which is cross-checked against the
        // pipeline so the log's rendering of it can never drift.
        Assert.Equal(RestoredRegistryOutcome.Restored, held.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.ActiveSlotPending, held.RestoredActivePointerClassification);
        Assert.True(held.RestoredActiveTaskMappingPresent,
            "the seeded admission's (task, goal) pair is in the snapshot's captured mappings");

        // NON-VACUITY OF THE DECODED SENTINEL: the hydrated registry really does carry it, so its
        // absence from the log is a genuine containment result rather than an absent payload.
        Assert.Contains(held.CaptureRegistry().Slots, s => s.Slot.TaskId == decodedSentinelTaskId);

        // THE DURABLE EVIDENCE IS UNTOUCHED by the whole startup pass — including the raw
        // sentinel-bearing bytes, which are preserved verbatim.
        Assert.Equal(expectedBlob, RawBlob(goalId));
        Assert.Equal(expectedPointer, RawPointer(goalId));
        Assert.Equal(1, RawMappingCount(taskId));
    }

    /// <summary>
    /// THE MALFORMED-PARSER LEAK CHANNEL, in its own isolated run because it needs an UNDECODABLE
    /// blob. Two INDEPENDENTLY FATAL text channels plus the exception-object channel are proven:
    /// <list type="number">
    ///   <item><description>RAW BLOB TEXT — <see cref="ParserLeakSentinel"/> sits in the stored
    ///     bytes and is NOT echoed by the parser, so it can reach a sink only by emitting the raw
    ///     registry text;</description></item>
    ///   <item><description>UNSANITIZED PARSER DIAGNOSTIC — the REAL inner
    ///     <c>JsonException</c> message is captured from the live codec and scanned for, so a
    ///     mutant that appends ONLY that inner text (attaching NO exception object) is killed BY
    ///     NAME. The reader genuinely echoes <see cref="ParserEchoedFragment"/>, and that presence
    ///     is OBSERVED here before any absence is asserted — an absence claim about text never
    ///     shown to exist would be vacuous;</description></item>
    ///   <item><description>EXCEPTION OBJECT — asserted SEPARATELY and still required: no record
    ///     may carry an attached exception, which is a different channel from the text one.</description></item>
    /// </list>
    /// <para>
    /// The wrapper relationship is proven too: the codec's own message CONTAINS the inner parser
    /// text verbatim, which is precisely what makes the inner diagnostic a reachable leak source.
    /// The run must still emit EXACTLY ONE warning in total and carry the honest rejected
    /// classifications, while the hold and the durable bytes survive.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RestoreActivePipelinesAsync_EnrichedHoldWarning_MalformedRegistry_NoParserTextLeak()
    {
        var goalId = "hold-warn-malformed";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);

        var malformed = MalformedRegistryJson;
        RawUpdate(goalId, "work_slot_registry_json", malformed);
        var expectedBlob = RawBlob(goalId);
        Assert.Equal(malformed, expectedBlob);

        // ── CAPTURE THE REAL INNER PARSER DIAGNOSTIC, from the live codec, BEFORE the run. ──
        // This is the exact text an unsanitized emission would leak, so it is what the scan below
        // looks for — never a stand-in and never the codec's own wrapper wording.
        var parseFailure = Assert.Throws<WorkSlotRegistryCodecException>(
            () => WorkSlotRegistryCodec.Decode(malformed));
        var innerParserMessage = parseFailure.InnerException!.Message;

        // (1) The inner diagnostic is REAL: nonempty, and DISTINCT from the outer wrapper text
        //     rather than a mere repeat of it.
        Assert.False(string.IsNullOrWhiteSpace(innerParserMessage));
        Assert.NotEqual(parseFailure.Message, innerParserMessage);

        // (2) THE WRAPPER RELATIONSHIP: the codec message CONTAINS the inner parser text verbatim.
        //     That containment is the shape of the leak channel and proves it is genuinely
        //     reachable — emitting either string would surface the parser's own words.
        Assert.Contains(innerParserMessage, parseFailure.Message, StringComparison.Ordinal);

        // (3) NON-VACUITY OF THE SCANNED FRAGMENT: the reader really does echo our controlled
        //     token, so asserting its absence below is genuine containment. (The raw-only
        //     sentinel is deliberately NOT expected here — the parser fails at the literal and
        //     never quotes it, which is exactly why it isolates the RAW-TEXT channel.)
        Assert.Contains(ParserEchoedFragment, innerParserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(ParserLeakSentinel, innerParserMessage, StringComparison.Ordinal);
        Assert.Contains(ParserLeakSentinel, malformed, StringComparison.Ordinal);

        var taskQueue = new TaskQueue();
        taskQueue.Enqueue(new WorkTask
        {
            TaskId = taskId,
            GoalId = goalId,
            GoalDescription = "malformed registry goal",
            Prompt = "x",
            Role = WorkerRole.Coder,
            Repositories = [],
        });
        taskQueue.Activate(taskQueue.TryDequeueAny()!, "worker-hold");
        Assert.NotNull(taskQueue.GetActiveTask(taskId));

        var brain = new HoldTrackingBrain();
        var goalStore = new Mock<IGoalStore>();
        var redispatchQueue = new ConcurrentQueue<string>();
        var logger = new TestLogger<DispatcherMaintenance>();
        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var maintenance = CreateMaintenance(restoreManager, logger: logger, brain: brain,
            goalStore: goalStore.Object, redispatchQueue: redispatchQueue, taskQueue: taskQueue);

        await maintenance.RestoreActivePipelinesAsync(CancellationToken.None);

        // EXACTLY ONE warning in total for this isolated run.
        var warning = Assert.Single(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("holds restored active attempt", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel > LogLevel.Warning);

        // THE HONEST REJECTED CLASSIFICATIONS — a rejected registry is not assessed, and is never
        // reported as proof the task is missing.
        Assert.Contains("registry evidence DecodeRejected", warning.Message, StringComparison.Ordinal);
        Assert.Contains("active slot Unassessed", warning.Message, StringComparison.Ordinal);
        Assert.Contains(goalId, warning.Message, StringComparison.Ordinal);
        Assert.Contains(taskId, warning.Message, StringComparison.Ordinal);

        // (5) THE EXCEPTION-OBJECT CHANNEL, asserted SEPARATELY and still required.
        Assert.Null(warning.Exception);
        Assert.DoesNotContain(logger.LogEntries, e => e.Exception is not null);

        // (4) THE TEXT CHANNELS, each independently fatal, scanned across EVERY emitted record's
        //     message AND any attached exception text:
        //       • the WHOLE real inner parser diagnostic — kills an append-inner-text-only mutant;
        //       • the echoed fragment — kills a mutant that emits only the quoted bad token;
        //       • the raw-only sentinel — kills a mutant that dumps the stored bytes.
        AssertNoSentinelInRecords(logger, innerParserMessage, "the real inner parser diagnostic");
        AssertNoSentinelInRecords(logger, ParserEchoedFragment, "parser-echoed malformed content");
        AssertNoSentinelInRecords(logger, ParserLeakSentinel, "raw malformed blob text");

        // The hold and the durable bytes survive: the destructive steps remain skipped (no goal-row
        // repair, no pointer clear, no queue completion, no redispatch) while the non-destructive
        // Brain setup legitimately ran.
        Assert.True(brain.GoalSessionExistsCalled);
        Assert.Empty(brain.ForkCalls);
        Assert.Equal([goalId], brain.RegisterExistingCalls);
        Assert.Empty(redispatchQueue);
        Assert.NotNull(taskQueue.GetActiveTask(taskId));
        Assert.Null(taskQueue.TryDequeueAny());
        var held = restoreManager.GetByGoalId(goalId);
        Assert.NotNull(held);
        Assert.True(held!.IsRestoredActiveAttemptHold);
        Assert.False(held.OwnershipCheckpointEligible);
        Assert.Equal(RestoredRegistryOutcome.DecodeRejected, held.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.Unassessed, held.RestoredActivePointerClassification);
        Assert.Empty(held.CaptureRegistry().Slots);
        Assert.Equal(expectedBlob, RawBlob(goalId));
        Assert.Equal(taskId, RawPointer(goalId));
    }

    /// <summary>
    /// THE THROWING-LOGGER CONTRACT for the enriched hold path, driven through the GUARDED
    /// EMISSION ITSELF. The logger is SELECTIVE: it permits the surrounding informational
    /// emissions so the startup method can progress, and throws ONLY when the hold warning is
    /// emitted (matched on the hold-warning message text), recording that it genuinely reached
    /// that emission. The three assertions together are what make this non-vacuous:
    /// <list type="number">
    ///   <item>the hold-warning emission was REACHED (without this witness the vector could pass
    ///     while never exercising the guard at all);</item>
    ///   <item>the sentinel did NOT propagate — <c>RestoreActivePipelinesAsync</c> COMPLETES
    ///     normally, which is exactly what removing or bypassing the <c>LogSafely</c> wrapper
    ///     around the hold warning would break;</item>
    ///   <item>the hold path stayed SAFE — still held, the object's classification channel still
    ///     readable independently of the faulting sink, and the durable evidence untouched.</item>
    /// </list>
    /// This deliberately does NOT rely on the earlier UNGUARDED information emission; that is a
    /// different, pre-existing behavior and proves nothing about this guard.
    /// </summary>
    [Fact]
    public async Task RestoreActivePipelinesAsync_EnrichedHoldWarning_ThrowingLogger_LeavesHoldPathSafe()
    {
        var goalId = "hold-warn-throwing";
        var store = CreateStore();
        var seedManager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (_, taskId) = SeedAdmittedAttempt(seedManager, goalId);
        var expectedBlob = RawBlob(goalId);

        var taskQueue = new TaskQueue();
        taskQueue.Enqueue(new WorkTask
        {
            TaskId = taskId,
            GoalId = goalId,
            GoalDescription = "throwing logger goal",
            Prompt = "x",
            Role = WorkerRole.Coder,
            Repositories = [],
        });
        taskQueue.Activate(taskQueue.TryDequeueAny()!, "worker-hold");

        var redispatchQueue = new ConcurrentQueue<string>();
        var logger = new SelectiveHoldWarningThrowingLogger();
        var restoreManager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);
        var maintenance = CreateMaintenance(restoreManager, logger: logger,
            redispatchQueue: redispatchQueue, taskQueue: taskQueue);

        // (ii) NOTHING PROPAGATES: the guarded emission swallows the fault, so the whole restore
        // method COMPLETES normally. Deleting/bypassing the hold warning's LogSafely wrapper makes
        // the sentinel escape here.
        var threw = await Record.ExceptionAsync(() =>
            maintenance.RestoreActivePipelinesAsync(CancellationToken.None));
        Assert.Null(threw);

        // (i) THE NON-VACUITY WITNESS: the guarded hold emission was genuinely REACHED and really
        // did throw — so the swallow above is the guard's doing, not an emission that never ran.
        Assert.True(logger.HoldWarningReached,
            "the hold warning must actually have been emitted — otherwise the guard was never exercised");
        Assert.Equal(1, logger.HoldWarningThrowCount);
        // The surrounding informational emissions were permitted, which is how the loop progressed.
        Assert.Contains(logger.PermittedMessages, m =>
            m.Contains("active pipeline(s) from persistence store", StringComparison.Ordinal));

        // (iii) THE HOLD PATH STAYED SAFE.
        var held = restoreManager.GetByGoalId(goalId);
        Assert.NotNull(held);
        Assert.True(held!.IsRestoredActiveAttemptHold);
        Assert.False(held.OwnershipCheckpointEligible);
        Assert.Contains(held, restoreManager.GetActivePipelines());
        // The object's own classification channel is INDEPENDENT of the (faulting) log sink.
        Assert.Equal(RestoredRegistryOutcome.Restored, held.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.ActiveSlotPending, held.RestoredActivePointerClassification);
        // The early continue still ran: the queue entry and the redispatch queue are untouched.
        Assert.NotNull(taskQueue.GetActiveTask(taskId));
        Assert.Empty(redispatchQueue);
        // The durable evidence is untouched.
        Assert.Equal(expectedBlob, RawBlob(goalId));
        Assert.Equal(taskId, RawPointer(goalId));
        Assert.Equal(1, RawMappingCount(taskId));
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
        // ROUND-1 HYDRATION: the held restore hydrated the admitted attempt's own Pending slot
        // (the admission checkpoint committed it into the row's blob) — evidence only, never
        // authority. The refusal below must not depend on any hydrated evidence.
        Assert.Equal(RestoredRegistryOutcome.Restored, held.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.ActiveSlotPending, held.RestoredActivePointerClassification);
        var hydratedSlot = Assert.Single(held.GetSlotsForTest());
        Assert.Equal(taskId, hydratedSlot.Slot.TaskId);
        Assert.Equal(WorkSlotState.Pending, hydratedSlot.State);

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
        // The hydrated evidence slot is UNTOUCHED by the refusal — still the single Pending slot.
        var untouchedSlot = Assert.Single(held.GetSlotsForTest());
        Assert.Equal(taskId, untouchedSlot.Slot.TaskId);
        Assert.Equal(WorkSlotState.Pending, untouchedSlot.State);
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
        // ROUND-1 HYDRATION: the held restore hydrated the admitted attempt's own Pending slot
        // (the admission checkpoint committed it into the row's blob) — evidence only, never
        // authority. The refusal below must not depend on any hydrated evidence.
        Assert.Equal(RestoredRegistryOutcome.Restored, held.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.ActiveSlotPending, held.RestoredActivePointerClassification);
        var hydratedSlot = Assert.Single(held.GetSlotsForTest());
        Assert.Equal(taskId, hydratedSlot.Slot.TaskId);
        Assert.Equal(WorkSlotState.Pending, hydratedSlot.State);

        var logger = new TestLogger<TaskCompletionService>();
        var service = CreateCompletionService(restoreManager, logger);

        await service.HandleTaskCompletionAsync(
            new TaskResult { TaskId = taskId, Status = TaskOutcome.Completed, Output = "done" },
            CancellationToken.None);

        // Nothing mutated: no admission, the pointer STILL names the task, the phase was NOT
        // driven, the conversation/phase log untouched — and the hydrated evidence slot is
        // UNTOUCHED (still the single Pending slot the restore hydrated).
        var untouchedSlot = Assert.Single(held.GetSlotsForTest());
        Assert.Equal(taskId, untouchedSlot.Slot.TaskId);
        Assert.Equal(WorkSlotState.Pending, untouchedSlot.State);
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
        // ROUND-1 HYDRATION: the held restore now hydrates the admitted attempt's own slot —
        // the admission checkpoint committed it into the row's blob — but the slot's PRESENCE is
        // evidence only: the reclaim below must still refuse it untouched.
        Assert.Equal(RestoredRegistryOutcome.Restored, held.RestoredRegistryClassification);
        Assert.Equal(RestoredActivePointerOutcome.ActiveSlotPending, held.RestoredActivePointerClassification);
        var hydrated = Assert.Single(held.GetSlotsForTest());
        Assert.Equal(taskId, hydrated.Slot.TaskId);
        Assert.Equal(WorkSlotState.Pending, hydrated.State);

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
        Assert.Null(queue.TryDequeueAny());
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
    /// again performs ONLY the non-destructive Brain setup (no destruction, no goal-row change, no
    /// queue entry).
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
            // A FRESH manager per cycle: the maintenance instance restores the row ITSELF, so the
            // gate under test genuinely runs instead of being short-circuited by an already-loaded
            // pipeline.
            var manager = new GoalPipelineManager(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);

            var brain = new HoldTrackingBrain();
            var goalStore = new Mock<IGoalStore>();
            var redispatchQueue = new ConcurrentQueue<string>();
            var maintenance = CreateMaintenance(manager, brain: brain, goalStore: goalStore.Object,
                redispatchQueue: redispatchQueue);
            await maintenance.RestoreActivePipelinesAsync(CancellationToken.None);

            // THE HOLD IS STILL RECOGNISED AND REPORTED for the same evidence every cycle.
            var restored = Assert.Single(manager.GetActivePipelines(), p => p.GoalId == goalId);
            Assert.True(restored.IsRestoredActiveAttemptHold,
                $"cycle {cycle}: the hold must be re-classified identically");
            Assert.Equal(taskId, restored.ActiveTaskId);
            Assert.Equal(RestoredRegistryOutcome.Restored, restored.RestoredRegistryClassification);
            Assert.Equal(RestoredActivePointerOutcome.ActiveSlotPending, restored.RestoredActivePointerClassification);

            Assert.True(brain.GoalSessionExistsCalled,
                "each cycle performs the non-destructive Brain setup");
            Assert.Empty(brain.ForkCalls);
            Assert.Equal([goalId], brain.RegisterExistingCalls);
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

    /// <summary>
    /// Tracks the Brain's session work so the vectors can assert BOTH halves of the held contract:
    /// the non-destructive setup (the session probe and reattach/fork) IS performed, while the
    /// destructive steps (goal-row repair, pointer clear, queue completion, removal, redispatch) are
    /// NOT. The recorded calls are what make each half a positive observation rather than an
    /// absence.</summary>
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

    /// <summary>
    /// A SELECTIVE logger that throws ONLY on the guarded startup hold warning — the contract
    /// vector for that emission's <c>LogSafely</c> guard.
    /// <para>
    /// It deliberately PERMITS every other emission (in particular the startup loop's UNGUARDED
    /// informational lines), so the method can actually progress to the hold warning instead of
    /// aborting earlier. The match is on the hold-warning MESSAGE TEXT rather than the level, and
    /// <see cref="HoldWarningReached"/> records that the guarded emission was genuinely reached —
    /// without that witness the vector could pass while never exercising the guard at all.
    /// </para>
    /// </summary>
    private sealed class SelectiveHoldWarningThrowingLogger : ILogger
    {
        /// <summary>The hold warning's stable marker phrase.</summary>
        private const string HoldWarningMarker = "holds restored active attempt";

        /// <summary>The exact instance thrown, so the propagation check is unambiguous.</summary>
        public const string Sentinel = "hold-warning-logger-sentinel";

        /// <summary><c>true</c> once the guarded hold-warning emission was actually reached.</summary>
        public bool HoldWarningReached { get; private set; }

        /// <summary>How many times the hold-warning emission was reached and refused.</summary>
        public int HoldWarningThrowCount { get; private set; }

        /// <summary>Every message this logger allowed through — the progress witness.</summary>
        public List<string> PermittedMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            // THROW ONLY FOR THE GUARDED HOLD WARNING — matched on its message text, never on the
            // level, so unrelated warnings could never stand in for it.
            if (message.Contains(HoldWarningMarker, StringComparison.Ordinal))
            {
                HoldWarningReached = true;
                HoldWarningThrowCount++;
                throw new InvalidOperationException(Sentinel);
            }

            PermittedMessages.Add(message);
        }
    }
}