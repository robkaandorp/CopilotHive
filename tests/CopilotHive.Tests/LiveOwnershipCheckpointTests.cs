using System.Data.Common;
using System.Reflection;

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

namespace CopilotHive.Tests;

/// <summary>
/// THE LIVE OWNERSHIP CHECKPOINT — the ordinary manager full/state save of an ELIGIBLE live
/// pipeline writes the active pointer and the COMPLETE work-slot registry (slots and per-position
/// counters) from ONE detached ownership capture, inside the SAME pipeline-row write as the
/// ordinary fields.
/// </summary>
/// <remarks>
/// <para>
/// THE SLICE'S BOUNDARY, deliberately pinned by these vectors: only the POINTER and the REGISTRY of
/// each eligible ordinary-save checkpoint share the one capture — plan, phase, metrics, the phase
/// log and the conversation keep their existing capture timing and may change afterwards. This is
/// NOT receipt replay, NOT a processed marker, NOT whole-pipeline atomicity and NOT restart
/// activation: no restore route consumes a checkpoint here, and the restored rows below must KEEP
/// their opaque stored blobs byte-for-byte.
/// </para>
/// <para>
/// The database is a per-instance shared-cache in-memory SQLite file anchored by a keeper
/// connection, so every readback is RAW SQL with no EF and no change tracker in the way. No test
/// uses <c>Task.Delay</c>: the races are arranged through synchronous EF interceptors and explicit
/// gates.
/// </para>
/// </remarks>
public sealed class LiveOwnershipCheckpointTests : IDisposable
{
    private readonly string _connectionString =
        $"Data Source=file:memdb-live-checkpoint-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<DbConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];

    public LiveOwnershipCheckpointTests()
    {
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        foreach (var connection in _connections)
            connection.Dispose();
        _keeper.Dispose();
    }

    // ═══════════════════════════════════ fixture plumbing ═══════════════════════════════════

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

    /// <summary>A factory-created (store-OWNED) context per operation — the shape two concurrent saves need.</summary>
    private sealed class LiveCheckpointContextFactory(string connectionString, IInterceptor? interceptor)
        : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly List<CopilotHiveDbContext> _contexts = [];
        private readonly List<SqliteConnection> _connections = [];

        public CopilotHiveDbContext CreateDbContext()
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            _connections.Add(connection);

            var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection);
            if (interceptor is not null)
                builder.AddInterceptors(interceptor);
            var context = new CopilotHiveDbContext(builder.Options);
            _contexts.Add(context);
            return context;
        }

        public void Dispose()
        {
            foreach (var context in _contexts)
                context.Dispose();
            foreach (var connection in _connections)
                connection.Dispose();
        }
    }

    private static Goal NewGoal(string id) =>
        new() { Id = id, Description = "goal " + id, RepositoryNames = ["test-repo"] };

    private static GoalPipeline NewPipeline(string goalId) => new(NewGoal(goalId));

    private static WorkSlotPosition Pos(int iteration, GoalPhase phase, int occurrence) =>
        new(iteration, phase, occurrence);

    private string? RawScalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result as string;
    }

    private string? RawBlob(string goalId) => RawScalar(
        "SELECT work_slot_registry_json FROM pipelines WHERE goal_id = $goal", ("$goal", goalId));

    private string? RawPointer(string goalId) => RawScalar(
        "SELECT active_task_id FROM pipelines WHERE goal_id = $goal", ("$goal", goalId));

    private void SeedBlob(string goalId, string? blob)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "UPDATE pipelines SET work_slot_registry_json = $blob WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId);
        command.Parameters.AddWithValue("$blob", (object?)blob ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private WorkSlotRegistrySnapshot DecodeBlob(string goalId) =>
        WorkSlotRegistryCodec.Decode(
            RawBlob(goalId) ?? throw new InvalidOperationException($"no blob for '{goalId}'"));

    private static HashSet<WorkSlotView> SlotsOf(WorkSlotRegistrySnapshot snapshot) => [.. snapshot.Slots];

    private static HashSet<WorkSlotRegistryAttemptEntry> CountersOf(WorkSlotRegistrySnapshot snapshot) =>
        [.. snapshot.DispatchAttempts];

    /// <summary>Drives a pipeline's registry through the production allocation/claim/record/abandon path.</summary>
    private static void AllocateTo(GoalPipeline pipeline, string taskId, WorkSlotPosition position,
        WorkSlotState target)
    {
        pipeline.AllocateAttemptAndRegisterSlot(taskId, position);
        switch (target)
        {
            case WorkSlotState.Pending:
                break;
            case WorkSlotState.Claimed:
                Assert.Equal(SlotGuardResult.Proceed, pipeline.ResolveAndCheckSlot(taskId));
                break;
            case WorkSlotState.Recorded:
                Assert.Equal(SlotGuardResult.Proceed, pipeline.ResolveAndCheckSlot(taskId));
                Assert.Equal(SlotRecordOutcome.Recorded, pipeline.RecordSlot(taskId));
                break;
            case WorkSlotState.Abandoned:
                Assert.True(pipeline.AbandonSlot(taskId));
                break;
            default:
                throw new InvalidOperationException($"Unhandled WorkSlotState: {target}");
        }
    }

    private static readonly WorkSlotPosition PosRecordedHistory = Pos(1, GoalPhase.Improve, 2);
    private static readonly WorkSlotPosition PosCounterOnly = Pos(1, GoalPhase.Merging, 4);

    /// <summary>
    /// THE RICH REGISTRY every pointer/registry vector uses: a seeded historical portion (a dead
    /// Recorded slot whose position's counter stands at 9, plus a COUNTER-ONLY position at 7 — the
    /// shape a recovered registry carries), then REAL allocations covering all four lifecycle
    /// states, a repeated occurrence retry, and a slot in a later iteration.
    /// </summary>
    /// <returns>The pipeline and the ACTIVE task id of the richest live slot (the retry's successor).</returns>
    private static (GoalPipeline Pipeline, string ActiveTaskId) BuildRichPipeline(string goalId)
    {
        var pipeline = NewPipeline(goalId);

        pipeline.RestoreRegistry(new WorkSlotRegistrySnapshot(
            [new WorkSlotView(new WorkSlot("history-recorded", PosRecordedHistory, 2), WorkSlotState.Recorded)],
            [new WorkSlotRegistryAttemptEntry(PosRecordedHistory, 9),
             new WorkSlotRegistryAttemptEntry(PosCounterOnly, 7)]));

        AllocateTo(pipeline, "claimed-task", Pos(1, GoalPhase.Coding, 1), WorkSlotState.Claimed);
        AllocateTo(pipeline, "recorded-task", Pos(1, GoalPhase.Coding, 2), WorkSlotState.Recorded);
        AllocateTo(pipeline, "retried-task", Pos(1, GoalPhase.Coding, 2), WorkSlotState.Pending);
        AllocateTo(pipeline, "abandoned-task", Pos(1, GoalPhase.Testing, 1), WorkSlotState.Abandoned);
        AllocateTo(pipeline, "pending-task", Pos(2, GoalPhase.Review, 1), WorkSlotState.Pending);

        return (pipeline, "retried-task");
    }

    /// <summary>
    /// THE ANTI-VACUOUS PRECONDITION: the capture really carries all four lifecycle states, a
    /// counter-only position and a counter standing HIGHER than its position's slot attempt — so the
    /// "round-trip preserved everything" assertions below cannot pass over a trivial registry.
    /// </summary>
    private static void AssertSnapshotIsRich(WorkSlotRegistrySnapshot snapshot)
    {
        Assert.Equal(Enum.GetValues<WorkSlotState>().ToHashSet(), snapshot.Slots.Select(s => s.State).ToHashSet());
        Assert.Contains(snapshot.DispatchAttempts, a => a.Position == PosCounterOnly);
        Assert.DoesNotContain(snapshot.Slots, s => s.Slot.Position == PosCounterOnly);

        var higher = Assert.Single(snapshot.DispatchAttempts, a => a.Position == PosRecordedHistory);
        var slotThere = Assert.Single(snapshot.Slots, s => s.Slot.Position == PosRecordedHistory);
        Assert.True(higher.HighWaterAttempt > slotThere.Slot.Attempt);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // (1) A fresh manager-created pipeline checkpoints through BOTH PersistFull and PersistState
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FULL SAVE, non-null pointer: the fresh manager-created (ELIGIBLE) pipeline's rich registry
    /// and live pointer land in the pipeline row, and a fresh readback decodes to EXACTLY the capture.
    /// </summary>
    [Fact]
    public void PersistFull_FreshEligiblePipeline_WritesCapturedPointerAndCompleteRegistry()
    {
        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (source, activeTaskId) = BuildRichPipeline("live-full-goal");
        var goal = NewGoal("live-full-goal");

        // Build the ELIGIBLE pipeline through the manager, then transplant the rich registry onto it
        // through the registry's own API (the freshly created pipeline's registry is empty).
        var pipeline = manager.CreatePipeline(goal);
        Assert.True(pipeline.OwnershipCheckpointEligible,
            "a fresh manager-created pipeline that found no existing row must be eligible");

        pipeline.RestoreRegistry(source.CaptureRegistry());
        pipeline.SetActiveTask(activeTaskId, "copilothive/live-full-goal");

        var captured = pipeline.CaptureAdmissionOwnership();
        Assert.Equal(activeTaskId, captured.ActiveTaskId);
        AssertSnapshotIsRich(captured.Registry);

        manager.PersistFull(pipeline);

        Assert.Equal(activeTaskId, RawPointer("live-full-goal"));
        var decoded = DecodeBlob("live-full-goal");
        Assert.Equal(SlotsOf(captured.Registry), SlotsOf(decoded));
        Assert.Equal(CountersOf(captured.Registry), CountersOf(decoded));
        AssertSnapshotIsRich(decoded);
    }

    /// <summary>
    /// THE STATE SAVE, NULL pointer + conversation neutrality: the captured NULL pointer is written as
    /// SQL NULL (never a late live read), the complete registry still lands, and — exactly like the
    /// legacy state save — the conversation is NOT rewritten.
    /// </summary>
    [Fact]
    public void PersistState_FreshEligiblePipeline_WritesCapturedNullPointerAndRegistry_LeavesConversationUntouched()
    {
        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (source, _) = BuildRichPipeline("live-state-goal");

        var pipeline = manager.CreatePipeline(NewGoal("live-state-goal"));
        pipeline.RestoreRegistry(source.CaptureRegistry());
        pipeline.Conversation.Add(new ConversationEntry("user", "written by the full save"));
        manager.PersistFull(pipeline);

        // Entries added AFTER the full save must stay in memory only under a state-only save.
        pipeline.Conversation.Add(new ConversationEntry("assistant", "in memory only"));

        Assert.Null(pipeline.ActiveTaskId);
        var captured = pipeline.CaptureAdmissionOwnership();
        Assert.Null(captured.ActiveTaskId);   // the captured NULL this vector is about
        AssertSnapshotIsRich(captured.Registry);

        manager.PersistState(pipeline);

        Assert.Null(RawPointer("live-state-goal"));   // SQL NULL, not a late read
        var decoded = DecodeBlob("live-state-goal");
        Assert.Equal(SlotsOf(captured.Registry), SlotsOf(decoded));
        Assert.Equal(CountersOf(captured.Registry), CountersOf(decoded));

        // CONVERSATION NEUTRALITY: only the full save's single entry is durable.
        var conversation = store.GetConversation("live-state-goal");
        Assert.Equal("written by the full save", Assert.Single(conversation).Content);
    }

    /// <summary>
    /// EACH SAVE CARRIES ITS OWN CAPTURE: a mutation between the two saves is reflected by the second
    /// one — so the checkpoint is genuinely taken per save invocation, not cached at creation.
    /// </summary>
    [Fact]
    public void PersistFull_ThenPersistState_EachCheckpointCarriesItsOwnCapture()
    {
        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.CreatePipeline(NewGoal("live-per-save-goal"));

        AllocateTo(pipeline, "first-task", Pos(1, GoalPhase.Coding, 1), WorkSlotState.Claimed);
        pipeline.SetActiveTask("first-task");
        manager.PersistFull(pipeline);
        Assert.Equal("first-task", RawPointer("live-per-save-goal"));
        Assert.Equal(SlotsOf(pipeline.CaptureRegistry()), SlotsOf(DecodeBlob("live-per-save-goal")));

        // The registry AND the pointer move on before the second save.
        AllocateTo(pipeline, "second-task", Pos(1, GoalPhase.Testing, 1), WorkSlotState.Recorded);
        Assert.True(pipeline.ClearActiveTaskIfCurrent("first-task"));
        manager.PersistState(pipeline);

        Assert.Null(RawPointer("live-per-save-goal"));
        var decoded = DecodeBlob("live-per-save-goal");
        Assert.Equal(SlotsOf(pipeline.CaptureRegistry()), SlotsOf(decoded));
        Assert.Equal(CountersOf(pipeline.CaptureRegistry()), CountersOf(decoded));
        Assert.Contains(decoded.Slots, s => s.Slot.TaskId == "second-task" && s.State == WorkSlotState.Recorded);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // (2) The pointer/registry pair is the ORIGINAL snapshot, even when the live state moves on
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FROZEN PAIR, at the strongest seam available: an EF interceptor mutates the LIVE pointer
    /// AND the LIVE registry at the pipeline row's own lookup — strictly AFTER the capture and BEFORE
    /// the captured values are applied to the row — and the written row is still the captured
    /// snapshot, with the captured NULL written as SQL NULL. A late live-pointer read would commit
    /// the mutated pointer; a live registry read would commit the mutated registry. Both assertions
    /// therefore fail under either mutant.
    /// </summary>
    /// <remarks>
    /// The store hands out a FRESH context per operation, so the save's row lookup is a genuine
    /// SELECT on an untracked row — the one statement that provably sits between the capture and
    /// <c>ApplyToEntity</c>. Without that, the entity would already be tracked and the only row
    /// statement would be the write itself, AFTER the values had been applied, making the vector
    /// vacuous.
    /// </remarks>
    [Fact]
    public void PersistState_LivePointerAndRegistryMutatedAfterCapture_WritesTheCapturedPair()
    {
        var mutation = new LivePipelineMutationInterceptor();
        using var factory = new LiveCheckpointContextFactory(_connectionString, mutation);
        var store = new PipelineStore(factory, NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var (source, _) = BuildRichPipeline("live-frozen-goal");

        var pipeline = manager.CreatePipeline(NewGoal("live-frozen-goal"));
        pipeline.RestoreRegistry(source.CaptureRegistry());
        Assert.Null(pipeline.ActiveTaskId);

        var captured = pipeline.CaptureAdmissionOwnership();
        Assert.Null(captured.ActiveTaskId);
        AssertSnapshotIsRich(captured.Registry);

        // The mutation lands at the save's own row lookup — after the capture, before the row write.
        mutation.Mutate = () =>
        {
            pipeline.SetActiveTask("late-live-task");
            pipeline.ClearRegistryForTest();
            AllocateTo(pipeline, "late-live-task", Pos(9, GoalPhase.DocWriting, 1), WorkSlotState.Pending);
        };
        mutation.Arm();

        manager.PersistState(pipeline);

        Assert.True(mutation.Fired, "the mutation interceptor must actually have fired");

        // THE CAPTURED NULL — never the late live pointer.
        Assert.Null(RawPointer("live-frozen-goal"));

        // THE CAPTURED REGISTRY — never the late live registry.
        var decoded = DecodeBlob("live-frozen-goal");
        Assert.Equal(SlotsOf(captured.Registry), SlotsOf(decoded));
        Assert.Equal(CountersOf(captured.Registry), CountersOf(decoded));
        Assert.DoesNotContain(decoded.Slots, s => s.Slot.TaskId == "late-live-task");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // (3) The manager's capture-through-save span is serialized
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SERIALIZATION VECTOR: a gate inside the store call parks an ELIGIBLE manager save while it
    /// holds the manager's mapping monitor, and a concurrent save for a DISJOINT pipeline MUST NOT
    /// complete in that window. The non-completion is the mutual-exclusion proof — without the lock
    /// the disjoint save would finish immediately. After the release both saves complete durably.
    /// </summary>
    [Fact]
    public async Task PersistState_ParksInsideTheStoreCall_SerializingADisjointManagerSave()
    {
        var gate = new LivePipelinesSelectGateInterceptor();
        using var factory = new LiveCheckpointContextFactory(_connectionString, gate);
        var store = new PipelineStore(factory, NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);

        var pipelineA = manager.CreatePipeline(NewGoal("live-serial-a"));
        var pipelineB = manager.CreatePipeline(NewGoal("live-serial-b"));
        AllocateTo(pipelineA, "serial-a-task", Pos(1, GoalPhase.Coding, 1), WorkSlotState.Claimed);
        pipelineA.SetActiveTask("serial-a-task");
        AllocateTo(pipelineB, "serial-b-task", Pos(1, GoalPhase.Testing, 1), WorkSlotState.Claimed);
        pipelineB.SetActiveTask("serial-b-task");

        gate.Arm();
        var saveA = Task.Factory.StartNew(
            () => manager.PersistState(pipelineA),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? saveB = null;
        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            saveB = Task.Factory.StartNew(
                () =>
                {
                    started.SetResult();
                    manager.PersistState(pipelineB);
                },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // THE MUTUAL-EXCLUSION PROOF: the disjoint save cannot pass the manager's lock.
            await Assert.ThrowsAsync<TimeoutException>(
                () => saveB.WaitAsync(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken));
            Assert.False(saveB.IsCompleted);

            // …and its row is untouched while it is blocked (the initial save left no blob).
            Assert.Null(RawBlob("live-serial-b"));
        }
        finally
        {
            gate.Release();
        }

        await saveA.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await saveB!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1, gate.BlockCount);
        Assert.Equal("serial-a-task", RawPointer("live-serial-a"));
        Assert.Equal("serial-b-task", RawPointer("live-serial-b"));
        Assert.Equal(SlotsOf(pipelineA.CaptureRegistry()), SlotsOf(DecodeBlob("live-serial-a")));
        Assert.Equal(SlotsOf(pipelineB.CaptureRegistry()), SlotsOf(DecodeBlob("live-serial-b")));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // (4) The REAL completion chain: TaskCompletionService's final PersistFull
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE LIVE INTEGRATION PROOF. A REAL <see cref="TaskCompletionService"/> drives its
    /// nonterminal success path end to end over a store-backed manager: the completion is ADMITTED
    /// (Pending → Claimed), the drive succeeds, the post-drive record marks the slot
    /// <see cref="WorkSlotState.Recorded"/>, and the service's own final
    /// <c>PersistFull</c> carries that Recorded predecessor into SQLite.
    /// <para>
    /// The admitted slot is allocated by the PRODUCTION capture
    /// (<see cref="GoalPipeline.CaptureDispatchPosition"/>) and claimed by the PRODUCTION
    /// <c>TrySetActiveTask</c> — never seeded — and the terminal phase is never reached, so the
    /// failed/cancelled/no-brain policies are untouched. The durable slot must read back as
    /// <c>Recorded</c>: a Claimed slot would mean the drive never completed, and the assertion fails.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TaskCompletionService_NonterminalSuccess_FinalPersistFull_PersistsTheRecordedPredecessor()
    {
        const string goalId = "live-completion-goal";
        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var goal = NewGoal(goalId);

        var goalManager = new GoalManager();
        goalManager.AddSource(new InMemoryGoalStore());
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipeline = manager.CreatePipeline(goal);
        Assert.True(pipeline.OwnershipCheckpointEligible);

        // Mid-iteration at Coding — the same arrangement the production dispatch sees.
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Coding);
        pipeline.AdvanceTo(GoalPhase.Coding);

        // THE PRODUCTION ALLOCATION and THE PRODUCTION CLAIM.
        var slot = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.Equal(1, slot.Attempt);
        Assert.True(pipeline.TrySetActiveTask(slot.TaskId, "copilothive/" + goalId));
        manager.RegisterTask(slot.TaskId, goalId);

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);
        var driver = new PipelineDriver(
            brain: new LiveCheckpointBrain(),
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
        var service = new TaskCompletionService(
            manager, new LiveCheckpointBrain(), driver, lifecycleService, null, NullLogger<TaskCompletionService>.Instance);

        await service.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = slot.TaskId,
            Status = TaskOutcome.Completed,
            Output = "Coding done.",
            GitStatus = new GitChangeSummary { FilesChanged = 2, Pushed = true },
            Metrics = new TaskMetrics { Verdict = "PASS" },
        }, TestContext.Current.CancellationToken);

        // The drive genuinely ran and the goalless terminal state was NOT reached.
        Assert.NotEqual(GoalPhase.Coding, pipeline.Phase);
        Assert.NotEqual(GoalPhase.Done, pipeline.Phase);
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);

        // THE RECORD: Recorded, never left Claimed.
        var live = Assert.Single(pipeline.GetSlotsForTest());
        Assert.Equal(slot.TaskId, live.Slot.TaskId);
        Assert.Equal(WorkSlotState.Recorded, live.State);

        // …AND IT IS DURABLE, carried by the service's own final PersistFull.
        var decoded = DecodeBlob(goalId);
        var durable = Assert.Single(decoded.Slots, s => s.Slot.TaskId == slot.TaskId);
        Assert.Equal(WorkSlotState.Recorded, durable.State);
        Assert.Equal(slot.Position, durable.Slot.Position);
        Assert.Equal(slot.Attempt, durable.Slot.Attempt);
        // The pointer was released before the drive and no later phase claimed one.
        Assert.Null(RawPointer(goalId));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // (5) Both restore routes stay UNACTIVATED and preserve the stored blob byte-for-byte
    // ═════════════════════════════════════════════════════════════════════════════════════════

    public static IEnumerable<object?[]> PreservedBlobCases()
    {
        var rich = BuildRichPipeline("live-preserve-source-" + Guid.NewGuid().ToString("N")).Pipeline
            .CaptureRegistry();

        yield return ["sql-null", null];
        yield return ["empty-payload", WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot([], []))];
        yield return ["populated", WorkSlotRegistryCodec.Encode(rich)];
        yield return ["malformed", "{not json"];
        yield return ["unsupported-version", "{\"version\":2,\"slots\":[],\"dispatchAttempts\":[]}"];
        yield return ["noncanonical", "{ \"dispatchAttempts\" : [], \"version\" : 1 , \"slots\" : [] }"];
    }

    /// <summary>
    /// THE NO-ACTIVATION + BYTE-PRESERVATION PROOF, over BOTH manager restore routes: a pipeline
    /// constructed from a <see cref="PipelineSnapshot"/> is INELIGIBLE, so its full and state saves
    /// KEEP the stored blob byte-for-byte — for SQL NULL, an empty payload, a populated one, and for
    /// MALFORMED, unsupported-version and noncanonical text alike, INCLUDING after a new slot has been
    /// allocated post-restore.
    /// <para>
    /// The route really is exercised: the restored registry comes up EMPTY (nothing recovered) and a
    /// genuinely NEW slot is installed before the saves, so a mutant that (a) activated recovery or
    /// (b) rewrote the column on an ordinary save would change the blob and fail.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(PreservedBlobCases))]
    public void RestoreRoutes_RemainUnactivated_AndPreserveStoredBlobByteForByte(string label, string? blob)
    {
        foreach (var route in new[] { "restore-pipeline", "restore-from-store" })
        {
            var goalId = $"live-preserve-{label}-{route}";
            var store = new PipelineStore(CreateContext(), NullLogger<PipelineStore>.Instance);

            var seed = NewPipeline(goalId);
            seed.AdvanceTo(GoalPhase.Coding);
            store.SavePipeline(seed);
            SeedBlob(goalId, blob);
            Assert.Equal(blob, RawBlob(goalId));   // the seeded state is the baseline

            var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
            var restored = route == "restore-pipeline"
                ? manager.RestorePipeline(goalId)
                : Assert.Single(manager.RestoreFromStore(), p => p.GoalId == goalId);
            Assert.NotNull(restored);

            // THE NO-ACTIVATION PRECONDITION: nothing was recovered from the stored bytes.
            Assert.False(restored!.OwnershipCheckpointEligible,
                "a pipeline constructed from a snapshot must be ineligible for registry checkpoint writes");
            Assert.Empty(restored.CaptureRegistry().Slots);
            Assert.Empty(restored.CaptureRegistry().DispatchAttempts);

            // A genuinely NEW slot post-restore — the case that must STILL preserve the stored blob.
            AllocateTo(restored, "post-restore-task", Pos(1, GoalPhase.Testing, 1), WorkSlotState.Claimed);
            Assert.Single(restored.GetSlotsForTest());

            manager.PersistFull(restored);
            Assert.Equal(blob, RawBlob(goalId));

            manager.PersistState(restored);
            Assert.Equal(blob, RawBlob(goalId));

            // And the ordinary save really did its own work.
            Assert.Equal("Coding", RawScalar("SELECT phase FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
        }
    }

    /// <summary>
    /// AN EXISTING-ROW <c>CreatePipeline</c> — a replacement or a goal-ID reuse — stays INELIGIBLE and
    /// preserves the row's opaque historical blob rather than erasing it. Only a creation that found
    /// NO row may checkpoint.
    /// </summary>
    [Fact]
    public void CreatePipeline_ExistingRow_StaysIneligible_AndPreservesTheOldBlob()
    {
        var store = new PipelineStore(CreateContext(), NullLogger<PipelineStore>.Instance);
        const string goalId = "live-replacement-goal";

        // A pre-existing row carrying opaque history this manager never created.
        var seed = NewPipeline(goalId);
        seed.AdvanceTo(GoalPhase.Coding);
        store.SavePipeline(seed);
        var oldBlob = WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot(
            [new WorkSlotView(new WorkSlot("historical-task", Pos(1, GoalPhase.Coding, 1), 1), WorkSlotState.Recorded)],
            [new WorkSlotRegistryAttemptEntry(Pos(1, GoalPhase.Coding, 1), 1)]));
        SeedBlob(goalId, oldBlob);

        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var replacement = manager.CreatePipeline(NewGoal(goalId));

        // THE PROVENANCE FACT: an existing-row creation is ineligible…
        Assert.False(replacement.OwnershipCheckpointEligible);

        // …and even after real registry history plus an ordinary save, the old blob is untouched.
        AllocateTo(replacement, "replacement-task", Pos(1, GoalPhase.Testing, 1), WorkSlotState.Recorded);
        replacement.SetActiveTask("replacement-task");
        manager.PersistFull(replacement);

        Assert.Equal(oldBlob, RawBlob(goalId));
        Assert.Equal("replacement-task", RawPointer(goalId));
    }

    /// <summary>
    /// THE LEGACY DIRECT-STORE CALLER, unchanged: the plain <c>SavePipeline</c>/<c>SavePipelineState</c>
    /// overloads never capture, clear or rewrite the registry column, so an existing blob survives
    /// byte-for-byte. This is the non-vacuity companion of the preservation vectors above: the
    /// byte-equality is satisfiable only because the legacy path leaves the column alone.
    /// </summary>
    [Fact]
    public void DirectStore_LegacyOrdinarySaves_StillPreserveAnExistingBlobByteForByte()
    {
        const string goalId = "live-legacy-preserve-goal";
        var store = new PipelineStore(CreateContext(), NullLogger<PipelineStore>.Instance);

        var pipeline = NewPipeline(goalId);
        store.SavePipeline(pipeline);
        var blob = WorkSlotRegistryCodec.Encode(BuildRichPipeline("live-legacy-preserve-src").Pipeline.CaptureRegistry());
        SeedBlob(goalId, blob);

        pipeline.Conversation.Add(new ConversationEntry("user", "legacy save"));
        pipeline.AdvanceTo(GoalPhase.Coding);
        store.SavePipeline(pipeline);
        Assert.Equal(blob, RawBlob(goalId));

        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipelineState(pipeline);
        Assert.Equal(blob, RawBlob(goalId));
        Assert.Equal("Testing", RawScalar("SELECT phase FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // (6) Error propagation
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ENCODE-BEFORE-TOUCH: a capture the codec cannot represent fails BEFORE any database
    /// interaction — the factory is never even asked for a context (its acquisition count stays 0, and
    /// its <c>CreateDbContext</c> throws if it were).
    /// </summary>
    [Fact]
    public void SavePipeline_UnencodableCapture_FailsWithNoDatabaseInteraction()
    {
        var factory = new CountingContextFactory();
        var store = new PipelineStore(factory, NullLogger<PipelineStore>.Instance);
        var pipeline = NewPipeline("live-encode-fail-goal");

        // A null slot entry is not representable by the codec.
        var candidate = new AdmissionOwnershipSnapshot(
            "live-encode-fail-goal", null, new WorkSlotRegistrySnapshot([null!], []));

        Assert.Throws<WorkSlotRegistryCodecException>(() => store.SavePipeline(pipeline, candidate));
        Assert.Equal(0, factory.Acquisitions);

        Assert.Throws<WorkSlotRegistryCodecException>(() => store.SavePipelineState(pipeline, candidate));
        Assert.Equal(0, factory.Acquisitions);
    }

    /// <summary>
    /// GOAL-IDENTITY GUARD: a carrier captured from another pipeline is refused with
    /// <see cref="ArgumentException"/> before any database interaction — an ordinary, pre-store
    /// validation, never a database refusal.
    /// </summary>
    [Fact]
    public void SavePipeline_CarrierForAnotherGoal_ThrowsWithoutDatabaseInteraction()
    {
        var factory = new CountingContextFactory();
        var store = new PipelineStore(factory, NullLogger<PipelineStore>.Instance);
        var pipeline = NewPipeline("live-goal-guard-goal");

        var foreign = new AdmissionOwnershipSnapshot(
            "some-other-goal", null, new WorkSlotRegistrySnapshot([], []));

        var ex = Assert.Throws<ArgumentException>(() => store.SavePipeline(pipeline, foreign));
        Assert.Equal("ownership", ex.ParamName);
        Assert.Contains("some-other-goal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("live-goal-guard-goal", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, factory.Acquisitions);
    }

    /// <summary>
    /// THE BORROWED-CONTEXT LEAK GUARD. A checkpoint save that fails at its row write must not leave
    /// the rejected blob staged in the caller-owned tracker, where a LATER LEGACY save on the SAME
    /// context would flush it. The rejected blob is distinct from the durable one, and after the
    /// legacy follow-up the durable blob is still the one that survives.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: dropping the failed write's key-scoped cleanup leaves the entity
    /// tracked with its staged checkpoint values, so the follow-up legacy save flushes the rejected
    /// blob and the final byte-equality assertion fails.
    /// </remarks>
    [Fact]
    public void SavePipeline_BorrowedContext_PreCommitFailure_SubsequentLegacySaveDoesNotLeakTheRejectedBlob()
    {
        const string goalId = "live-leak-goal";
        var sentinel = new InvalidOperationException("checkpoint-row-write-sentinel");

        // The durable row and its blob are seeded on a SEPARATE context, so the borrowed context the
        // operation uses starts with nothing tracked.
        var seeding = new PipelineStore(CreateContext(), NullLogger<PipelineStore>.Instance);
        var seed = NewPipeline(goalId);
        seed.AdvanceTo(GoalPhase.Coding);
        seeding.SavePipeline(seed);

        var durableBlob = WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot(
            [new WorkSlotView(new WorkSlot("durable-task", Pos(1, GoalPhase.Coding, 1), 1), WorkSlotState.Recorded)],
            [new WorkSlotRegistryAttemptEntry(Pos(1, GoalPhase.Coding, 1), 1)]));
        SeedBlob(goalId, durableBlob);
        Assert.Equal(durableBlob, RawBlob(goalId));

        var interceptor = new OneShotPipelinesUpdateThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var store = new PipelineStore(context, NullLogger<PipelineStore>.Instance);

        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("checkpoint-task");
        pipeline.RestoreRegistry(new WorkSlotRegistrySnapshot(
            [new WorkSlotView(new WorkSlot("rejected-task", Pos(1, GoalPhase.Testing, 1), 1), WorkSlotState.Claimed)],
            [new WorkSlotRegistryAttemptEntry(Pos(1, GoalPhase.Testing, 1), 1)]));
        var rejected = WorkSlotRegistryCodec.Encode(pipeline.CaptureRegistry());
        Assert.NotEqual(durableBlob, rejected);
        Assert.Contains("rejected-task", rejected, StringComparison.Ordinal);

        // The pre-commit row-write failure: the sentinel propagates and the durable blob is unchanged.
        var thrown = Record.Exception(() => store.SavePipeline(pipeline, pipeline.CaptureAdmissionOwnership()));
        Assert.NotNull(thrown);
        Assert.Contains(sentinel, EnumerateChain(thrown!));
        Assert.Equal(durableBlob, RawBlob(goalId));

        // THE LEAK GUARD: a subsequent LEGACY save on the SAME borrowed context must not flush the
        // rejected checkpoint blob.
        pipeline.AdvanceTo(GoalPhase.Testing);
        store.SavePipeline(pipeline);

        Assert.Equal(durableBlob, RawBlob(goalId));
        Assert.DoesNotContain("rejected-task", RawBlob(goalId) ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Testing", RawScalar("SELECT phase FROM pipelines WHERE goal_id = $goal", ("$goal", goalId)));
    }

    /// <summary>Every exception in the propagated chain, outermost first.</summary>
    private static IEnumerable<Exception> EnumerateChain(Exception exception)
    {
        for (var current = (Exception?)exception; current is not null; current = current.InnerException)
            yield return current;
    }

    /// <summary>
    /// THE AUTHORITATIVE-FAILURE PROOF, by identity: the propagated exception is the
    /// <see cref="DbUpdateException"/> EF raises for the failed row write, wrapping the EXACT injected
    /// sentinel (<see cref="Assert.Same"/>), the logger's own sentinel appears NOWHERE in the chain,
    /// the injection really fired exactly once, and the guarded diagnostic really reached the throwing
    /// logger — so "nothing was ever logged" cannot pass this vector vacuously.
    /// <para>
    /// The ATTEMPTED diagnostics are pinned too, which is what makes the CLEANUP site's guard
    /// observable: the cleanup must report its COMPLETION (its own write's throw is swallowed by that
    /// guard), never a fabricated "cleanup did not complete".
    /// </para>
    /// </summary>
    private static void AssertDatabaseFailureRemainsAuthoritative(
        Exception? thrown, Exception sentinel,
        OneShotPipelinesUpdateThrowInterceptor interceptor, ThrowingPipelineStoreLogger logger)
    {
        Assert.NotNull(thrown);
        var update = Assert.IsType<DbUpdateException>(thrown);
        Assert.Same(sentinel, update.InnerException);
        Assert.Equal(1, interceptor.ThrowCount);
        Assert.DoesNotContain(EnumerateChain(thrown), e => ReferenceEquals(e, logger.LoggerSentinel));
        Assert.True(logger.ThrowCount >= 1,
            "the failed save's diagnostic never reached the throwing logger — the guard would be vacuous");

        // THE CLEANUP SITE'S GUARD: the hygiene COMPLETED (the tracker-detach work really ran and the
        // completion diagnostic was attempted), and the throwing logger's write at that site was
        // swallowed by the guard rather than being mistaken for a cleanup fault.
        Assert.Contains(logger.Messages, m => m.Contains("tracker hygiene completed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("cleanup did not complete", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.Contains("the primary exception is rethrown unchanged", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE THROWING-LOGGER GUARD on the FULL save's BORROWED-CONTEXT failure path: the pre-commit
    /// row-write failure is reported through the file's existing no-throw diagnostic helper, so a
    /// logger that throws on EVERY write can never replace the database failure the caller must
    /// observe. No disposal is involved on this path (the context is caller-owned), so the logger's
    /// throw is the ONLY competing failure.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: routing the catch's diagnostic back to a bare
    /// <c>_logger.LogError</c> lets the armed logger's own sentinel escape the catch block instead of
    /// the rethrown row-write failure, so the propagated exception stops carrying the injected
    /// sentinel and the identity assertions fail.
    /// </remarks>
    [Fact]
    public void SavePipeline_BorrowedContext_ThrowingLogger_KeepsTheDatabaseFailureAuthoritative()
    {
        const string goalId = "live-throwinglogger-full-goal";
        var sentinel = new InvalidOperationException("checkpoint-row-write-sentinel");

        // The durable row is seeded on a SEPARATE context, so the borrowed context starts clean.
        var seeding = new PipelineStore(CreateContext(), NullLogger<PipelineStore>.Instance);
        seeding.SavePipeline(NewPipeline(goalId));

        var interceptor = new OneShotPipelinesUpdateThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var logger = new ThrowingPipelineStoreLogger();
        var store = new PipelineStore(context, logger);
        logger.Arm(); // the throw starts AFTER the constructor's logging

        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("throwing-logger-full-task");

        var thrown = Record.Exception(
            () => store.SavePipeline(pipeline, pipeline.CaptureAdmissionOwnership()));

        AssertDatabaseFailureRemainsAuthoritative(thrown, sentinel, interceptor, logger);
    }

    /// <summary>
    /// THE THROWING-LOGGER GUARD on the STATE save's BORROWED-CONTEXT failure path — the scalar-only
    /// sibling, whose cleanup (the store's own <c>OnCheckpointSaveFailure</c> included) is audited by
    /// its own vector rather than inferred from the full save's.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: exactly as for the full save — an unguarded diagnostic in either the
    /// catch block or the cleanup lets the armed logger's sentinel out of the failure path.
    /// </remarks>
    [Fact]
    public void SavePipelineState_BorrowedContext_ThrowingLogger_KeepsTheDatabaseFailureAuthoritative()
    {
        const string goalId = "live-throwinglogger-state-goal";
        var sentinel = new InvalidOperationException("checkpoint-state-row-write-sentinel");

        var seeding = new PipelineStore(CreateContext(), NullLogger<PipelineStore>.Instance);
        seeding.SavePipeline(NewPipeline(goalId));

        var interceptor = new OneShotPipelinesUpdateThrowInterceptor(sentinel);
        var context = CreateContext(interceptor);
        var logger = new ThrowingPipelineStoreLogger();
        var store = new PipelineStore(context, logger);
        logger.Arm();

        var pipeline = NewPipeline(goalId);
        pipeline.SetActiveTask("throwing-logger-state-task");

        var thrown = Record.Exception(
            () => store.SavePipelineState(pipeline, pipeline.CaptureAdmissionOwnership()));

        AssertDatabaseFailureRemainsAuthoritative(thrown, sentinel, interceptor, logger);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // (7) No store stays memory-only
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// NO-STORE BEHAVIOR: with no store there is no persisted row, so no eligibility is inferred and
    /// both persists stay memory-only no-ops — nothing is captured, nothing is written.
    /// </summary>
    [Fact]
    public void NoStore_PersistsStayMemoryOnly_AndInferNoEligibility()
    {
        var manager = new GoalPipelineManager(store: null, NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.CreatePipeline(NewGoal("live-nostore-goal"));
        AllocateTo(pipeline, "nostore-task", Pos(1, GoalPhase.Coding, 1), WorkSlotState.Claimed);
        pipeline.SetActiveTask("nostore-task");

        Assert.False(pipeline.OwnershipCheckpointEligible);
        Assert.Null(Record.Exception(() => manager.PersistFull(pipeline)));
        Assert.Null(Record.Exception(() => manager.PersistState(pipeline)));
        Assert.Equal("nostore-task", pipeline.ActiveTaskId);
        Assert.Single(pipeline.GetSlotsForTest());
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Local test doubles (kept minimal and focused on THIS slice's seams)
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A factory that must never be reached: it counts acquisitions and throws if asked.</summary>
    private sealed class CountingContextFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        private int _acquisitions;

        public int Acquisitions => Volatile.Read(ref _acquisitions);

        public CopilotHiveDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _acquisitions);
            throw new InvalidOperationException("the context factory must not be reached before the encode");
        }
    }

    /// <summary>
    /// Mutates the LIVE domain state — and nothing else — the FIRST time a <c>pipelines</c> statement
    /// is issued while armed. It performs no store call and no re-entrant work, so it can never
    /// disturb the in-flight save beyond the live-state mutation it exists to inject.
    /// </summary>
    private sealed class LivePipelineMutationInterceptor : DbCommandInterceptor
    {
        private int _armed;   // 0 = disarmed, 1 = armed, 2 = fired
        private int _fired;

        public Action? Mutate { get; set; }

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public void Arm() => Volatile.Write(ref _armed, 1);

        private void MutateIfArmed(DbCommand command)
        {
            if (!command.CommandText.Contains("pipelines", StringComparison.OrdinalIgnoreCase))
                return;
            if (Interlocked.CompareExchange(ref _armed, 2, 1) != 1)
                return;

            Volatile.Write(ref _fired, 1);
            Mutate?.Invoke();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            MutateIfArmed(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MutateIfArmed(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            MutateIfArmed(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MutateIfArmed(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Parks the FIRST <c>SELECT … FROM "pipelines"</c> read on an external gate while armed — the
    /// lookup inside the store's upsert, which the manager reaches with its mapping monitor held. The
    /// gate performs no re-entrant work and is released by the test; the bounded wait keeps a
    /// never-released gate a test failure rather than a hang.
    /// </summary>
    private sealed class LivePipelinesSelectGateInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockCount;
        private volatile bool _armed;

        public Task Entered => _entered.Task;

        public int BlockCount => Volatile.Read(ref _blockCount);

        public void Arm() => _armed = true;

        public void Release() => _release.TrySetResult();

        private void BlockIfTargeted(DbCommand command)
        {
            if (!_armed)
                return;

            var text = command.CommandText;
            if (!text.Contains("pipelines", StringComparison.OrdinalIgnoreCase))
                return;
            if (!text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                return;

            // Only the FIRST targeted statement blocks; TrySetResult is the one-shot latch.
            if (!_entered.TrySetResult())
                return;

            Interlocked.Increment(ref _blockCount);
            _release.Task.Wait(TimeSpan.FromSeconds(60));
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            BlockIfTargeted(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            BlockIfTargeted(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Throws a caller-supplied sentinel before the FIRST <c>UPDATE … pipelines</c> executes, then
    /// lets every later statement through — exactly what the "rejected write, then a legacy save on
    /// the same borrowed context" sequence needs.
    /// </summary>
    /// <remarks>
    /// BOTH statement paths are hooked deliberately: the SQLite provider emits an EF row update as
    /// <c>UPDATE … RETURNING 1</c>, which EF executes through the READER path, while other providers
    /// (and the raw statements elsewhere in this store) use the non-query path. Hooking only one of
    /// them would make the injection silently vacuous.
    /// </remarks>
    private sealed class OneShotPipelinesUpdateThrowInterceptor(Exception sentinel) : DbCommandInterceptor
    {
        private int _fired;
        private int _throwCount;

        public int ThrowCount => Volatile.Read(ref _throwCount);

        private void ThrowIfTargeted(DbCommand command)
        {
            var text = command.CommandText;
            if (!text.Contains("pipelines", StringComparison.OrdinalIgnoreCase))
                return;
            if (!text.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                return;
            if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
                return;

            Interlocked.Increment(ref _throwCount);
            throw sentinel;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ThrowIfTargeted(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTargeted(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfTargeted(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTargeted(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// A logger that THROWS on EVERY write once armed (the store's constructor logging must still
    /// succeed so the test reaches the failure path). The thrown instance is a DISTINCT,
    /// pre-created <see cref="LoggerSentinel"/> so "the database failure was replaced by a logger
    /// failure" is detectable BY IDENTITY, and <see cref="ThrowCount"/> proves the guarded
    /// diagnostic ACTUALLY reached the logger — distinguishing "the guard swallowed the throw and the
    /// database failure escaped" from "nothing was ever logged".
    /// </summary>
    /// <remarks>
    /// Every armed write's formatted text is also RECORDED before the throw, so a test can pin WHICH
    /// diagnostics were attempted — in particular that the tracker-hygiene cleanup reported its
    /// completion rather than fabricating a cleanup failure the moment its own write threw.
    /// </remarks>
    private sealed class ThrowingPipelineStoreLogger : ILogger<PipelineStore>
    {
        private readonly List<string> _messages = [];
        private bool _armed;
        private int _throwCount;

        /// <summary>The DISTINCT exception instance every armed log write throws.</summary>
        public InvalidOperationException LoggerSentinel { get; } = new("the pipeline-store logger itself threw SENTINEL");

        /// <summary>How many armed log writes threw.</summary>
        public int ThrowCount => Volatile.Read(ref _throwCount);

        /// <summary>The formatted text of every armed log write, in order (the write still threw).</summary>
        public IReadOnlyList<string> Messages => _messages;

        /// <summary>Arms the throw (call AFTER store construction).</summary>
        public void Arm() => _armed = true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!_armed)
                return;

            _messages.Add(formatter(state, exception));
            Interlocked.Increment(ref _throwCount);
            throw LoggerSentinel;
        }
    }

    /// <summary>Minimal brain that plans successfully and crafts a canned prompt.</summary>
    private sealed class LiveCheckpointBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens,
            Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(string goalId, int iteration, string phase,
            string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }
}
