using System.Data.Common;
using System.Reflection;
using System.Reflection.Emit;

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
    // (7) THE ADMISSION — the eligible route writes mapping + pointer + the WHOLE registry
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ELIGIBLE ADMISSION COMMITS THE COMPLETE TUPLE: for a manager-created (ELIGIBLE) pipeline
    /// the admission's OWN transaction lands the <c>task_mappings</c> row, the captured
    /// <c>active_task_id</c> pointer AND the COMPLETE captured registry — read back through a FRESH
    /// context and through RAW SQLite. No task ID is parsed and no counter is reconstructed
    /// anywhere: the decoded registry is compared to the capture verbatim, historical slots in
    /// every state and the counter-only/higher-than-slot entries included.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF. Reverting <c>PersistAdmission</c> to the two-argument store call leaves the
    /// blob SQL NULL here and the pointer assertions fail; dropping the capture entirely fails the
    /// registry comparison. The admission really takes the NEW route because the pipeline is
    /// eligible and carries a genuine Pending active slot.
    /// </remarks>
    [Fact]
    public void PersistAdmission_EligiblePipeline_WritesMappingCapturedPointerAndTheCompleteRegistry()
    {
        const string goalId = "live-admission-goal";
        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);

        var (source, activeTaskId) = BuildRichPipeline(goalId + "-src");
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        pipeline.RestoreRegistry(source.CaptureRegistry());
        pipeline.SetActiveTask(activeTaskId, "copilothive/" + goalId);

        var captured = pipeline.CaptureAdmissionOwnership();
        Assert.Equal(activeTaskId, captured.ActiveTaskId);
        AssertSnapshotIsRich(captured.Registry);

        var result = manager.PersistAdmission(pipeline, activeTaskId);

        Assert.Equal(AdmissionCommitStatus.Committed, result.Status);
        Assert.True(result.ClaimedThisInvocation);
        Assert.True(result.CommittedThisInvocation);
        Assert.Null(result.PersistenceException);

        // ── RAW SQLITE: the exact mapping row, the exact pointer, the exact blob. ──
        Assert.Equal(goalId, RawScalar(
            "SELECT goal_id FROM task_mappings WHERE task_id = $task", ("$task", activeTaskId)));
        Assert.Equal(activeTaskId, RawPointer(goalId));

        // THE COMPLETE REGISTRY — every slot in every state and every counter entry.
        var decoded = DecodeBlob(goalId);
        Assert.Equal(SlotsOf(captured.Registry), SlotsOf(decoded));
        Assert.Equal(CountersOf(captured.Registry), CountersOf(decoded));
        AssertSnapshotIsRich(decoded);

        // ── A FRESH CONTEXT: the same committed tuple, no tracker in the way. ──
        var fresh = CreateStore().LoadPipeline(goalId);
        Assert.NotNull(fresh);
        Assert.Equal(activeTaskId, fresh!.ActiveTaskId);
        Assert.Equal(WorkSlotRegistryCodec.Encode(captured.Registry), fresh.WorkSlotRegistryJson);
    }

    /// <summary>
    /// THE ADMISSION'S CAPTURE IS FROZEN. An EF interceptor mutates the LIVE pointer AND the LIVE
    /// registry at the admission's own pipeline-row lookup — strictly AFTER the capture and BEFORE
    /// the captured pair is applied to the row — and the committed row is still the captured pair.
    /// A late live-pointer read would commit the mutated pointer; a live registry read would commit
    /// the mutated registry. Both assertions therefore fail under either mutant.
    /// </summary>
    /// <remarks>
    /// The mutation fires at the FIRST <c>pipelines</c> statement while armed. The manager's capture
    /// runs before the store is entered at all (the argument is evaluated first), so the only
    /// statements before it are the mapping INSERT — which the interceptor ignores. The store's
    /// transaction begins and the stage-2 row lookup is therefore the first armed statement,
    /// provably after the capture.
    /// </remarks>
    [Fact]
    public void PersistAdmission_LivePointerAndRegistryMutatedAfterCapture_WritesTheCapturedPair()
    {
        const string goalId = "live-admission-frozen-goal";
        var mutation = new LivePipelineMutationInterceptor();
        using var factory = new LiveCheckpointContextFactory(_connectionString, mutation);
        var store = new PipelineStore(factory, NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);

        var (source, activeTaskId) = BuildRichPipeline(goalId + "-src");
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        pipeline.RestoreRegistry(source.CaptureRegistry());
        pipeline.SetActiveTask(activeTaskId, "copilothive/" + goalId);

        // THE ORIGINAL CAPTURE — taken with no intervening mutation, so it is exactly what the
        // manager's own (later) capture must produce.
        var captured = pipeline.CaptureAdmissionOwnership();
        Assert.Equal(activeTaskId, captured.ActiveTaskId);

        // The mutation lands at the admission's own row lookup — after the capture, before apply.
        mutation.Mutate = () =>
        {
            pipeline.SetActiveTask("late-live-admission-task");
            pipeline.ClearRegistryForTest();
            AllocateTo(pipeline, "late-live-admission-task", Pos(9, GoalPhase.DocWriting, 1), WorkSlotState.Pending);
        };
        mutation.Arm();

        var result = manager.PersistAdmission(pipeline, activeTaskId);

        Assert.Equal(AdmissionCommitStatus.Committed, result.Status);
        Assert.True(mutation.Fired, "the mutation interceptor must actually have fired");

        // THE CAPTURED POINTER — never the late live pointer.
        Assert.Equal(activeTaskId, RawPointer(goalId));

        // THE CAPTURED REGISTRY — never the late live registry.
        var decoded = DecodeBlob(goalId);
        Assert.Equal(SlotsOf(captured.Registry), SlotsOf(decoded));
        Assert.Equal(CountersOf(captured.Registry), CountersOf(decoded));
        Assert.DoesNotContain(decoded.Slots, s => s.Slot.TaskId == "late-live-admission-task");

        // The mapping row is the admitted task's, written by the same transaction.
        Assert.Equal(goalId, RawScalar(
            "SELECT goal_id FROM task_mappings WHERE task_id = $task", ("$task", activeTaskId)));
    }

    /// <summary>
    /// THE ELIGIBLE ROUTE'S FAILURE SEMANTICS, on the store's own preflight: an eligible pipeline
    /// whose captured active task has NO Pending slot is refused by the store BEFORE any context is
    /// acquired, and the manager reports <see cref="AdmissionCommitStatus.PersistenceFailed"/>
    /// carrying that ORIGINAL exception — with THIS invocation's claim removed (pair-based) and the
    /// ownership flags exactly as the shared failure path always reports them. NOTHING was written.
    /// </summary>
    [Fact]
    public void PersistAdmission_EligibleRoutePreflightRefusal_PersistenceFailedWithClaimRemovedAndNothingWritten()
    {
        const string goalId = "live-admission-preflight-goal";
        const string taskId = "live-admission-preflight-task";
        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);

        // ELIGIBLE, with a real pointer but NO registered slot — the capture the store's preflight
        // must refuse. (A pointer-only fixture is exactly the shape the new route rejects.)
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        pipeline.SetActiveTask(taskId);

        var result = manager.PersistAdmission(pipeline, taskId);

        Assert.Equal(AdmissionCommitStatus.PersistenceFailed, result.Status);
        // THE β FLAGS ARE UNCHANGED: this call DID claim, and nothing committed.
        Assert.True(result.ClaimedThisInvocation);
        Assert.False(result.CommittedThisInvocation);
        // NO ROLLBACK EVIDENCE: a refusal is not a confirmed successful admission, so there is
        // nothing to invert — and the dispatch must keep the legacy (non-evidence) route.
        Assert.Null(result.RollbackEvidence);
        // THE ORIGINAL exception, by identity and by shape: the store's own preflight refusal.
        var refusal = Assert.IsType<ArgumentException>(result.PersistenceException);
        Assert.Contains("has no matching slot in the registry", refusal.Message, StringComparison.Ordinal);

        // THIS INVOCATION'S CLAIM — and only it — was removed.
        Assert.Null(manager.GetByTaskId(taskId));

        // NOTHING WAS WRITTEN: no mapping row, no registry blob, and the row's pointer is untouched.
        Assert.Null(RawScalar(
            "SELECT goal_id FROM task_mappings WHERE task_id = $task", ("$task", taskId)));
        Assert.Null(RawBlob(goalId));
        Assert.Null(RawPointer(goalId));
    }

    /// <summary>
    /// A LIVE MUTATION BETWEEN THE CAPTURE AND THE STORE CALL CANNOT FORCE A LATE REVALIDATION of
    /// the captured pair: the capture is taken inside the manager with the mapping lock held and
    /// handed over as a detached carrier, so clearing the live pointer afterwards leaves the
    /// committed pointer exactly as captured.
    /// </summary>
    /// <remarks>
    /// This vector uses the manager's own lock to arrange the mutation deterministically: the
    /// mutation runs on a second thread and is ordered strictly after the admission returns, which
    /// is the honest limit of the guarantee (a mutation AFTER the commit is not a revalidation
    /// window at all — the pair is already durable).
    /// </remarks>
    [Fact]
    public void PersistAdmission_CapturedPairIsNotRevalidated_AfterALatePointerClear()
    {
        const string goalId = "live-admission-no-revalidate";
        const string taskId = "live-admission-no-revalidate-task";
        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);

        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        AllocateTo(pipeline, taskId, Pos(1, GoalPhase.Coding, 1), WorkSlotState.Pending);
        pipeline.SetActiveTask(taskId);

        var result = manager.PersistAdmission(pipeline, taskId);
        Assert.Equal(AdmissionCommitStatus.Committed, result.Status);
        Assert.Equal(taskId, RawPointer(goalId));

        // A LATE live clear — the committed pointer must not be revalidated against it.
        Assert.True(pipeline.ClearActiveTaskIfCurrent(taskId));
        Assert.Equal(taskId, RawPointer(goalId));

        // The committed registry still carries the admission's Pending slot.
        var decoded = DecodeBlob(goalId);
        Assert.Contains(decoded.Slots, s => s.Slot.TaskId == taskId && s.State == WorkSlotState.Pending);
    }

    /// <summary>
    /// THE INELIGIBLE ROUTE PRESERVES THE COLUMN VERBATIM, for a RESTORED pipeline and for an
    /// EXISTING-ROW replacement alike: the admission still writes its mapping row and the pointer,
    /// but the row's registry blob — SQL NULL or an opaque/malformed payload — is left
    /// byte-for-byte as it was, because the ineligible path writes NO checkpoint.
    /// </summary>
    [Theory]
    [InlineData("restore-pipeline", "sql-null", null)]
    [InlineData("restore-pipeline", "malformed", "{not json")]
    [InlineData("restore-pipeline", "unsupported-version", "{\"version\":2,\"slots\":[],\"dispatchAttempts\":[]}")]
    [InlineData("restore-from-store", "sql-null", null)]
    [InlineData("restore-from-store", "malformed", "{not json")]
    [InlineData("restore-from-store", "unsupported-version", "{\"version\":2,\"slots\":[],\"dispatchAttempts\":[]}")]
    public void PersistAdmission_IneligibleRoute_PreservesTheBlobVerbatim(string route, string label, string? blob)
    {
        var goalId = $"live-admission-ineligible-{label}-{route}";
        var taskId = $"live-admission-ineligible-task-{label}-{route}";
        var store = CreateStore();

        // A pre-existing row carrying the blob this vector is about.
        var seed = NewPipeline(goalId);
        seed.AdvanceTo(GoalPhase.Coding);
        store.SavePipeline(seed);
        SeedBlob(goalId, blob);
        Assert.Equal(blob, RawBlob(goalId));

        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var restored = route == "restore-pipeline"
            ? manager.RestorePipeline(goalId)
            : Assert.Single(manager.RestoreFromStore(), p => p.GoalId == goalId);
        Assert.NotNull(restored);
        Assert.False(restored!.OwnershipCheckpointEligible);

        // A genuine Pending slot plus the pointer: the admission input is valid either way, so the
        // blob preservation cannot be explained by a refusal.
        AllocateTo(restored, taskId, Pos(1, GoalPhase.Testing, 1), WorkSlotState.Pending);
        restored.SetActiveTask(taskId);

        var result = manager.PersistAdmission(restored, taskId);

        Assert.Equal(AdmissionCommitStatus.Committed, result.Status);
        // The admission DID its own work: the mapping row and the pointer landed.
        Assert.Equal(goalId, RawScalar(
            "SELECT goal_id FROM task_mappings WHERE task_id = $task", ("$task", taskId)));
        Assert.Equal(taskId, RawPointer(goalId));
        // …and the blob is byte-identical — SQL NULL stayed SQL NULL, opaque text untouched.
        Assert.Equal(blob, RawBlob(goalId));
    }

    /// <summary>
    /// THE INVOCATION-LOCAL ROLLBACK EVIDENCE IS THE ADMISSION-WRITE PAYLOAD — PROVEN
    /// BEHAVIORALLY, not by source shape. A successful ELIGIBLE admission returns evidence whose
    /// token is BYTE-IDENTICAL to the registry text the same transaction wrote to the row (read back
    /// RAW through the keeper), and whose goal/task are the invocation's own validated pair.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: producing the evidence by a SECOND encode, by a decode/re-encode
    /// round trip, or from a fresh database read. A round trip would still match byte-for-byte for a
    /// canonical payload, so the token's IDENTITY is additionally pinned through the SECOND half of
    /// this vector: the rollback that consumes it succeeds against the ORIGINAL durable text.
    /// </remarks>
    [Fact]
    public void PersistAdmission_EligibleRoute_EvidenceIsExactlyTheAdmissionWritePayload()
    {
        const string goalId = "live-evidence-payload";
        const string taskId = "live-evidence-payload-task";
        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        AllocateTo(pipeline, taskId, Pos(1, GoalPhase.Coding, 1), WorkSlotState.Pending);
        pipeline.SetActiveTask(taskId, "copilothive/" + goalId);

        var result = manager.PersistAdmission(pipeline, taskId);

        Assert.Equal(AdmissionCommitStatus.Committed, result.Status);
        var evidence = Assert.IsType<AdmissionRollbackEvidence>(result.RollbackEvidence);
        Assert.Equal(goalId, evidence.GoalId);
        Assert.Equal(taskId, evidence.TaskId);
        // THE EXACT WRITTEN TEXT.
        Assert.Equal(RawBlob(goalId), evidence.EncodedRegistryJson);

        // THE TOKEN IS USABLE AS-IS: the guarded inverse accepts it and commits.
        var rollback = manager.RollbackPendingAdmission(pipeline, taskId, evidence);
        Assert.Equal(PendingAdmissionRollbackStatus.Committed, rollback.Status);
        Assert.Null(RawPointer(goalId));
    }

    /// <summary>
    /// EVIDENCE IS POPULATED ONLY ON A CONFIRMED SUCCESSFUL ELIGIBLE ADMISSION. The LEGACY/ineligible
    /// route, the NoStore route and every refusal/failure outcome all leave it <c>null</c> — the
    /// dispatch's route selection is decided from THIS field, so a stray carrier would make an
    /// ineligible admission take the eligible (atomic-inverse) route.
    /// </summary>
    [Fact]
    public void PersistAdmission_NonEligibleAndNonCommittedOutcomes_CarryNoEvidence()
    {
        // (a) INELIGIBLE (an existing-row replacement): committed, but NO evidence.
        var ineligibleGoal = "live-evidence-ineligible";
        var store = CreateStore();
        store.SavePipeline(NewPipeline(ineligibleGoal));
        var ineligibleTask = "live-evidence-ineligible-task";
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var replacement = manager.CreatePipeline(NewGoal(ineligibleGoal));
        Assert.False(replacement.OwnershipCheckpointEligible);
        AllocateTo(replacement, ineligibleTask, Pos(1, GoalPhase.Coding, 1), WorkSlotState.Pending);
        replacement.SetActiveTask(ineligibleTask);

        var ineligibleResult = manager.PersistAdmission(replacement, ineligibleTask);
        Assert.Equal(AdmissionCommitStatus.Committed, ineligibleResult.Status);
        Assert.Null(ineligibleResult.RollbackEvidence);

        // (b) NO STORE: claimed in memory only, and no evidence.
        var noStoreManager = new GoalPipelineManager(store: null, NullLogger<GoalPipelineManager>.Instance);
        var noStore = noStoreManager.CreatePipeline(NewGoal("live-evidence-nostore"));
        AllocateTo(noStore, "live-evidence-nostore-task", Pos(1, GoalPhase.Coding, 1), WorkSlotState.Pending);
        noStore.SetActiveTask("live-evidence-nostore-task");
        var noStoreResult = noStoreManager.PersistAdmission(noStore, "live-evidence-nostore-task");
        Assert.Equal(AdmissionCommitStatus.NoStore, noStoreResult.Status);
        Assert.Null(noStoreResult.RollbackEvidence);

        // (c) A REFUSAL: an eligible admission whose persisted mapping is owned by another attempt.
        var conflictGoal = "live-evidence-conflict";
        var conflictStore = CreateStore();
        var conflictTask = "live-evidence-conflict-task";
        // THE COMPETING ROW, seeded RAW through the keeper so no tracked entity confuses the shape.
        using (var command = _keeper.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO task_mappings (task_id, goal_id) VALUES ($task, 'goal-competitor')";
            command.Parameters.AddWithValue("$task", conflictTask);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var conflictManager = new GoalPipelineManager(conflictStore, NullLogger<GoalPipelineManager>.Instance);
        var conflict = conflictManager.CreatePipeline(NewGoal(conflictGoal));
        Assert.True(conflict.OwnershipCheckpointEligible);
        AllocateTo(conflict, conflictTask, Pos(1, GoalPhase.Coding, 1), WorkSlotState.Pending);
        conflict.SetActiveTask(conflictTask);

        var conflictResult = conflictManager.PersistAdmission(conflict, conflictTask);
        Assert.Equal(AdmissionCommitStatus.PersistConflict, conflictResult.Status);
        Assert.Null(conflictResult.RollbackEvidence);

        // (d) A PERSISTENCE FAILURE on the ELIGIBLE route, AFTER the ownership-aware overload has
        // already assigned its out token: the store's SQL throws at the mapping INSERT, so the
        // admission reports PersistenceFailed — and the evidence must still be null, because only a
        // CONFIRMED successful admission may carry it. (Without this case an implementation that
        // published the token on every eligible attempt would go unnoticed, and the dispatch would
        // then take the atomic-inverse route for an admission that never committed.)
        var failureGoal = "live-evidence-persistfail";
        var failureTask = "live-evidence-persistfail-task";
        var failureSentinel = new InvalidOperationException("live-evidence-persistfail-sentinel");
        var failureManager = new GoalPipelineManager(
            CreateStore(new SentinelThrowingInterceptor(failureSentinel, "INSERT")),
            NullLogger<GoalPipelineManager>.Instance);
        var failing = failureManager.CreatePipeline(NewGoal(failureGoal));
        Assert.True(failing.OwnershipCheckpointEligible);
        AllocateTo(failing, failureTask, Pos(1, GoalPhase.Coding, 1), WorkSlotState.Pending);
        failing.SetActiveTask(failureTask);

        var failureResult = failureManager.PersistAdmission(failing, failureTask);

        Assert.Equal(AdmissionCommitStatus.PersistenceFailed, failureResult.Status);
        // THE VACUITY GUARD: the store's SQL really was reached and really threw, so the out token
        // HAD been assigned by the route before the failure — this is not a pre-store refusal.
        var wrapper = Assert.IsType<DbUpdateException>(failureResult.PersistenceException);
        Assert.Same(failureSentinel, wrapper.InnerException);
        // THE LOAD-BEARING ASSERTION: no evidence on a failed admission.
        Assert.Null(failureResult.RollbackEvidence);
        // …and nothing was committed, so there is genuinely nothing to invert.
        Assert.False(failureResult.CommittedThisInvocation);
        Assert.Null(RawScalar(
            "SELECT goal_id FROM task_mappings WHERE task_id = $task", ("$task", failureTask)));
    }

    /// <summary>
    /// A POST-CAPTURE LIVE MUTATION CANNOT ALTER THE EVIDENCE — and the rollback it later drives
    /// still matches the ORIGINAL durable text. The mutation lands at the admission's own row lookup
    /// (strictly after the single capture, before the row write), the committed pair is the captured
    /// one, and the returned evidence is exactly that committed text.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: deriving the evidence from a LATER live capture or a fresh read
    /// would produce the mutated registry, which can never match the row's actual text — the
    /// byte-equality assertion fails and the round-trip rollback below would REFUSE.
    /// </remarks>
    [Fact]
    public void PersistAdmission_LiveMutationAfterCapture_DoesNotAlterTheEvidenceToken()
    {
        const string goalId = "live-evidence-frozen";
        var mutation = new LivePipelineMutationInterceptor();
        using var factory = new LiveCheckpointContextFactory(_connectionString, mutation);
        var store = new PipelineStore(factory, NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);

        var (source, taskId) = BuildRichPipeline(goalId + "-src");
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        pipeline.RestoreRegistry(source.CaptureRegistry());
        pipeline.SetActiveTask(taskId, "copilothive/" + goalId);

        mutation.Mutate = () =>
        {
            // The live registry gains a slot the capture never carried — WITHOUT erasing the
            // captured task's own Pending slot, so the rollback's memory fence still succeeds and
            // only the EVIDENCE's provenance decides whether its CAS commits or refuses.
            AllocateTo(pipeline, "late-live-evidence-task", Pos(9, GoalPhase.DocWriting, 1), WorkSlotState.Pending);
        };
        mutation.Arm();

        var result = manager.PersistAdmission(pipeline, taskId);

        Assert.Equal(AdmissionCommitStatus.Committed, result.Status);
        Assert.True(mutation.Fired, "the mutation interceptor must actually have fired");
        var evidence = Assert.IsType<AdmissionRollbackEvidence>(result.RollbackEvidence);

        // THE EVIDENCE IS THE COMMITTED (CAPTURED) TEXT — never the mutated live registry.
        Assert.Equal(RawBlob(goalId), evidence.EncodedRegistryJson);
        Assert.DoesNotContain("late-live-evidence-task", evidence.EncodedRegistryJson, StringComparison.Ordinal);

        // PROVEN USABLE: the inverse still finds the exact durable text and commits. A token derived
        // from the LATER live capture would carry the late slot, mismatch the row's text, and REFUSE.
        var rollback = manager.RollbackPendingAdmission(pipeline, taskId, evidence);
        Assert.Equal(PendingAdmissionRollbackStatus.Committed, rollback.Status);
    }

    /// <summary>
    /// A LATER-CHANGED DURABLE BLOB MAKES THE ROLLBACK REFUSE — it never refreshes its expectation
    /// from the row it finds. The stale text is written RAW, the supplied evidence is the ORIGINAL
    /// token, and the guarded inverse reports a CONFIRMED no-mutation refusal with the durable row
    /// left exactly as the stale write left it.
    /// </summary>
    [Fact]
    public void PersistAdmission_StaleDurableBlobAfterAdmission_MakesTheRollbackRefuse()
    {
        const string goalId = "live-evidence-stale";
        const string taskId = "live-evidence-stale-task";
        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        AllocateTo(pipeline, taskId, Pos(1, GoalPhase.Coding, 1), WorkSlotState.Pending);
        pipeline.SetActiveTask(taskId, "copilothive/" + goalId);

        var result = manager.PersistAdmission(pipeline, taskId);
        var evidence = Assert.IsType<AdmissionRollbackEvidence>(result.RollbackEvidence);

        // THE STALE REWRITE: a JSON-shaped payload that is NOT the admitted text.
        var stale = WorkSlotRegistryCodec.Encode(new WorkSlotRegistrySnapshot([], []));
        Assert.NotEqual(stale, evidence.EncodedRegistryJson);
        SeedBlob(goalId, stale);

        var rollback = manager.RollbackPendingAdmission(pipeline, taskId, evidence);

        Assert.Equal(PendingAdmissionRollbackStatus.Refused, rollback.Status);
        Assert.Null(rollback.Failure);
        Assert.Null(rollback.RollbackFailure);
        // NO DESTRUCTIVE FALLBACK: the stale text, the pointer and the mapping all survive.
        Assert.Equal(stale, RawBlob(goalId));
        Assert.Equal(taskId, RawPointer(goalId));
        Assert.Equal(goalId, RawScalar(
            "SELECT goal_id FROM task_mappings WHERE task_id = $task", ("$task", taskId)));
    }

    /// <summary>
    /// A REPLACED PIPELINE INSTANCE SKIPS THE ROLLBACK: the manager's CURRENT instance for the goal
    /// is a different object, so the evidence is not ours to act on and NOTHING is written.
    /// </summary>
    [Fact]
    public void RollbackPendingAdmission_ReplacedPipelineInstance_SkipsWithoutAnyWrite()
    {
        const string goalId = "live-evidence-replaced";
        const string taskId = "live-evidence-replaced-task";
        var store = CreateStore();
        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        AllocateTo(pipeline, taskId, Pos(1, GoalPhase.Coding, 1), WorkSlotState.Pending);
        pipeline.SetActiveTask(taskId, "copilothive/" + goalId);
        var admission = manager.PersistAdmission(pipeline, taskId);
        var evidence = Assert.IsType<AdmissionRollbackEvidence>(admission.RollbackEvidence);
        var blobAtAdmission = RawBlob(goalId);

        // THE REPLACEMENT — a different instance is now the manager's current pipeline for the goal.
        var pipelinesField = typeof(GoalPipelineManager)
            .GetField("_pipelines", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pipelines = (System.Collections.Concurrent.ConcurrentDictionary<string, GoalPipeline>)
            pipelinesField.GetValue(manager)!;
        pipelines[goalId] = new GoalPipeline(NewGoal(goalId));

        var rollback = manager.RollbackPendingAdmission(pipeline, taskId, evidence);

        Assert.Equal(PendingAdmissionRollbackStatus.Skipped, rollback.Status);
        // NOTHING was written: the pair is EXACTLY as admitted.
        Assert.Equal(taskId, RawPointer(goalId));
        Assert.Equal(blobAtAdmission, RawBlob(goalId));
        Assert.Equal(goalId, RawScalar(
            "SELECT goal_id FROM task_mappings WHERE task_id = $task", ("$task", taskId)));
    }

    /// <summary>
    /// THE EXISTING-ROW REPLACEMENT, admitted: an INELIGIBLE replacement (a <c>CreatePipeline</c>
    /// that FOUND a persisted row — a replacement or a goal-ID reuse) keeps that row's registry blob
    /// VERBATIM through a real admission, while the admission's own mapping row and pointer land.
    /// <para>
    /// THE SAME FOUR BLOB VALUES the restore routes cover are covered here: SQL NULL (the
    /// legacy-absence marker), OPAQUE text, MALFORMED JSON and an UNSUPPORTED VERSION envelope.
    /// Every one is read back RAW through the keeper connection and compared byte-for-byte, so a
    /// replacement route that started checkpointing — or that normalized/cleared the column —
    /// fails on every row.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("sql-null", null)]
    [InlineData("opaque", "historical-opaque-registry-blob")]
    [InlineData("malformed", "{not json")]
    [InlineData("unsupported-version", "{\"version\":2,\"slots\":[],\"dispatchAttempts\":[]}")]
    public void PersistAdmission_ExistingRowReplacement_PreservesTheHistoricalBlob(string label, string? blob)
    {
        var goalId = $"live-admission-replacement-{label}";
        var taskId = $"live-admission-replacement-task-{label}";
        var store = CreateStore();

        var seed = NewPipeline(goalId);
        seed.AdvanceTo(GoalPhase.Coding);
        store.SavePipeline(seed);
        SeedBlob(goalId, blob);
        Assert.Equal(blob, RawBlob(goalId));   // the seeded state is the byte-for-byte baseline

        var manager = new GoalPipelineManager(store, NullLogger<GoalPipelineManager>.Instance);
        var replacement = manager.CreatePipeline(NewGoal(goalId));
        // THE PROVENANCE FACT: the creation FOUND a row, so this instance is ineligible.
        Assert.False(replacement.OwnershipCheckpointEligible);

        // A genuine Pending slot plus the pointer: the admission input is valid, so the blob
        // preservation below cannot be explained away by a refusal.
        AllocateTo(replacement, taskId, Pos(1, GoalPhase.Review, 1), WorkSlotState.Pending);
        replacement.SetActiveTask(taskId);

        var result = manager.PersistAdmission(replacement, taskId);

        Assert.Equal(AdmissionCommitStatus.Committed, result.Status);
        // The admission DID its own work: the mapping row and the pointer landed.
        Assert.Equal(goalId, RawScalar(
            "SELECT goal_id FROM task_mappings WHERE task_id = $task", ("$task", taskId)));
        Assert.Equal(taskId, RawPointer(goalId));
        // …and the blob is byte-identical — SQL NULL stayed SQL NULL, every opaque/malformed/
        // unsupported payload untouched.
        Assert.Equal(blob, RawBlob(goalId));
    }

    /// <summary>
    /// NO-STORE AND MEMORY-CONFLICT REQUIRE NO REGISTRY VALIDATION AND TOUCH NO DATABASE. An
    /// eligible pipeline whose registry is INVALID (no Pending slot for the pointer) still returns
    /// <see cref="AdmissionCommitStatus.NoStore"/> when no store is configured, and
    /// <see cref="AdmissionCommitStatus.MemoryConflict"/> when the task is already mapped — neither
    /// path reaches the store's preflight, so nothing throws and nothing is written.
    /// </summary>
    [Fact]
    public void PersistAdmission_NoStoreAndMemoryConflict_SkipRegistryValidationEntirely()
    {
        // (a) NO STORE — the claim alone is the admission; the invalid registry is never validated.
        var noStoreManager = new GoalPipelineManager(store: null, NullLogger<GoalPipelineManager>.Instance);
        var noStore = noStoreManager.CreatePipeline(NewGoal("live-admission-nostore-invalid"));
        Assert.False(noStore.OwnershipCheckpointEligible);
        noStore.SetActiveTask("live-admission-nostore-task");   // pointer with NO slot

        var noStoreResult = noStoreManager.PersistAdmission(noStore, "live-admission-nostore-task");
        Assert.Equal(AdmissionCommitStatus.NoStore, noStoreResult.Status);
        Assert.True(noStoreResult.ClaimedThisInvocation);
        Assert.False(noStoreResult.CommittedThisInvocation);
        Assert.Null(noStoreResult.PersistenceException);

        // (b) MEMORY CONFLICT — refused before the store call, with the SAME invalid registry.
        var counter = new AdmissionCommandCounter();
        var manager = new GoalPipelineManager(CreateStore(counter), NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.CreatePipeline(NewGoal("live-admission-mc-invalid"));
        Assert.True(pipeline.OwnershipCheckpointEligible);
        pipeline.SetActiveTask("live-admission-mc-task");   // pointer with NO slot
        manager.RegisterTask("live-admission-mc-task", "live-admission-mc-invalid");
        counter.Start();

        var memoryConflict = manager.PersistAdmission(pipeline, "live-admission-mc-task");

        Assert.Equal(AdmissionCommitStatus.MemoryConflict, memoryConflict.Status);
        Assert.False(memoryConflict.ClaimedThisInvocation);
        Assert.False(memoryConflict.CommittedThisInvocation);
        Assert.Null(memoryConflict.PersistenceException);
        // NO STATEMENT AT ALL — the refusal precedes the store call and any validation.
        Assert.Empty(counter.Commands);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // (8) EXACTLY-ONCE: the single capture and the single encode, proved by INTERACTION
    // ═════════════════════════════════════════════════════════════════════════════════════════
    //
    // WHY TWO DIFFERENT MECHANISMS ARE USED HERE (the choice, stated explicitly):
    //
    //   • THE ENCODE uses mechanism (i), a RUNTIME COUNTING INSTRUMENT, because the encode really
    //     does enumerate the caller's own collections: FreezeOwnershipCheckpoint hands
    //     `ownership.Registry` straight to WorkSlotRegistryCodec.Encode, which walks Slots once and
    //     DispatchAttempts once. Wrapping those two collections in counting enumerables therefore
    //     yields an EXACT expected total (1 + 1) that a second encode would double. The
    //     instrumented entry point is the ordinary checkpoint save, because that route passes the
    //     caller's snapshot to the SAME FreezeOwnershipCheckpoint UNCOPIED — on the admission route
    //     the preflight first copies the registry into fresh lists, so a wrapper handed to the
    //     admission would be invisible to the encode and the probe would be vacuous.
    //
    //   • THE CAPTURE uses mechanism (ii), the repo's established EMITTED-CALL-SITE inspection,
    //     because a second CaptureAdmissionOwnership is NOT observable by any wrapper: the capture
    //     copies out of the pipeline's own private dictionaries, so no collection the test owns is
    //     re-enumerated and no seam is re-entered. Counting the emitted call sites is the only
    //     deterministic evidence available. The same structural backstop also pins the admission
    //     route's SINGLE call to FreezeOwnershipCheckpoint, which — combined with the runtime
    //     encode count above — is what makes "exactly one encode per eligible admission" provable.

    /// <summary>A single decoded call instruction: its IL offset and resolved target.</summary>
    private sealed record CallSite(int Offset, MethodBase Target);

    /// <summary>Opcode lookup table built once from <see cref="OpCodes"/> reflection.</summary>
    private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodeTable();

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
                continue;
            var opCode = (OpCode)field.GetValue(null)!;
            table[opCode.Value] = opCode;
        }
        return table;
    }

    /// <summary>
    /// Walks a method's emitted IL instruction-by-instruction (using the real opcode table, so
    /// operand bytes are never mistaken for opcodes) and returns every <c>call</c>/<c>callvirt</c>
    /// site with its offset and resolved target — the established structural backstop this
    /// codebase already uses for seam-free-interval proofs.
    /// </summary>
    private static List<CallSite> DecodeCallSites(MethodBase method)
    {
        var body = method.GetMethodBody()
            ?? throw new Xunit.Sdk.XunitException($"'{method.Name}' has no method body.");
        var il = body.GetILAsByteArray()
            ?? throw new Xunit.Sdk.XunitException($"'{method.Name}' exposes no IL.");
        var module = method.Module;
        var genericTypeArgs = method.DeclaringType?.GetGenericArguments();
        var genericMethodArgs = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

        var sites = new List<CallSite>();
        var pos = 0;
        while (pos < il.Length)
        {
            var start = pos;
            short value;
            if (il[pos] == 0xFE)
            {
                value = (short)(0xFE00 | il[pos + 1]);
                pos += 2;
            }
            else
            {
                value = il[pos];
                pos += 1;
            }

            if (!OpCodeByValue.TryGetValue(value, out var opCode))
                throw new Xunit.Sdk.XunitException($"Unknown opcode 0x{value:X} at offset {start} in '{method.Name}'.");

            var operandSize = OperandSize(opCode, il, pos);

            if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
            {
                var token = BitConverter.ToInt32(il, pos);
                MethodBase? target = null;
                try
                {
                    target = module.ResolveMethod(token, genericTypeArgs, genericMethodArgs);
                }
                catch (ArgumentException)
                {
                    // Not resolvable in this context; not a call this test asserts on.
                }

                if (target is not null)
                    sites.Add(new CallSite(start, target));
            }

            pos += operandSize;
        }

        return sites;
    }

    private static int OperandSize(OpCode opCode, byte[] il, int operandStart) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operandStart)),
        _ => throw new Xunit.Sdk.XunitException($"Unhandled operand type {opCode.OperandType}."),
    };

    /// <summary>Counts the emitted call sites in <paramref name="caller"/> whose target is
    /// <paramref name="declaringType"/>.<paramref name="name"/>.</summary>
    private static int CountCallsTo(MethodBase caller, Type declaringType, string name) =>
        DecodeCallSites(caller)
            .Count(c => c.Target.DeclaringType == declaringType
                && string.Equals(c.Target.Name, name, StringComparison.Ordinal));

    private static MethodBase RequireMethod(Type type, string name, params Type[] parameterTypes)
    {
        var method = type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            parameterTypes,
            modifiers: null);
        return method ?? throw new Xunit.Sdk.XunitException(
            $"No method '{name}({string.Join(", ", parameterTypes.Select(t => t.Name))})' on {type.Name}.");
    }

    /// <summary>
    /// THE EXACTLY-ONE-CAPTURE PROOF, asserted against the EMITTED BYTES of
    /// <c>GoalPipelineManager.PersistAdmission</c> (deterministic — no timing anywhere): the
    /// eligible admission route emits EXACTLY ONE call to
    /// <see cref="GoalPipeline.CaptureAdmissionOwnership"/>.
    /// <para>
    /// A duplicated capture would take TWO detached snapshots at two different instants, so the
    /// pointer and the registry the commit writes could drift apart from the pair the caller
    /// believes was frozen. Payload comparison alone cannot see that; this count can.
    /// </para>
    /// </summary>
    [Fact]
    public void PersistAdmission_EmitsExactlyOneOwnershipCapture()
    {
        var persistAdmission = RequireMethod(
            typeof(GoalPipelineManager), "PersistAdmission", typeof(GoalPipeline), typeof(string));

        var captures = CountCallsTo(persistAdmission, typeof(GoalPipeline), "CaptureAdmissionOwnership");

        Assert.True(
            captures == 1,
            $"'PersistAdmission' emits {captures} call(s) to CaptureAdmissionOwnership — the eligible " +
            "route must take EXACTLY ONE detached capture, so the committed pointer and registry " +
            "come from one instant.");

        // ANTI-VACUITY: the decoder really resolved this method's calls (it is not silently empty),
        // and the route really does reach the ownership-aware store overload.
        Assert.True(DecodeCallSites(persistAdmission).Count > 1, "the IL decoder resolved no call sites");
        Assert.Equal(
            2,
            CountCallsTo(persistAdmission, typeof(PipelineStore), "SaveAdmissionWithPointer"));
    }

    /// <summary>
    /// THE EXACTLY-ONE-ENCODE PROOF, part 1 (STRUCTURAL): the ownership-aware
    /// <c>SaveAdmissionWithPointer</c> overload emits EXACTLY ONE call to the freeze helper and
    /// EXACTLY ONE call to the reused preflight, and the freeze helper itself emits EXACTLY ONE
    /// call to <see cref="WorkSlotRegistryCodec.Encode"/>.
    /// </summary>
    /// <remarks>
    /// THE OVERLOAD IS THE EVIDENCE-CARRYING ONE ON PURPOSE: it is the SINGLE implementation. The
    /// convenience three-argument form merely delegates to it (and performs no validation, freeze or
    /// encode of its own), so counting here covers the whole ownership-aware route. If the evidence
    /// were ever produced by a SECOND encode, the freeze helper's count would become two — or the
    /// route would grow a second encode call — and this vector fails.
    /// </remarks>
    [Fact]
    public void OwnershipAwareAdmissionRoute_EmitsExactlyOneFreezeAndOneEncode()
    {
        var route = RequireMethod(
            typeof(PipelineStore), "SaveAdmissionWithPointer",
            typeof(GoalPipeline), typeof(string), typeof(AdmissionOwnershipSnapshot), typeof(string).MakeByRefType());
        var freeze = RequireMethod(
            typeof(PipelineStore), "FreezeOwnershipCheckpoint",
            typeof(GoalPipeline), typeof(AdmissionOwnershipSnapshot));

        var freezeCalls = CountCallsTo(route, typeof(PipelineStore), "FreezeOwnershipCheckpoint");
        Assert.True(
            freezeCalls == 1,
            $"the ownership-aware route emits {freezeCalls} call(s) to FreezeOwnershipCheckpoint — " +
            "exactly one freeze (hence exactly one encode) is the contract.");

        var preflightCalls = CountCallsTo(route, typeof(GoalPipeline), "PreflightAdmissionOwnership");
        Assert.True(
            preflightCalls == 1,
            $"the ownership-aware route emits {preflightCalls} call(s) to PreflightAdmissionOwnership — " +
            "the detached carrier is validated exactly once, by the reused validator.");

        var encodeCalls = CountCallsTo(freeze, typeof(WorkSlotRegistryCodec), "Encode");
        Assert.True(
            encodeCalls == 1,
            $"FreezeOwnershipCheckpoint emits {encodeCalls} call(s) to WorkSlotRegistryCodec.Encode — " +
            "the registry is encoded exactly once, before any context exists.");

        // THE EVIDENCE ADDS NO SECOND ENCODE: the route body itself must emit ZERO encode calls, so
        // the token it returns comes ONLY from the single freeze/encode inside FreezeOwnershipCheckpoint.
        // A mutant that produced the evidence by re-encoding the validated registry would have to
        // match the frozen token byte-for-byte on a canonical payload — so this structural count is
        // the discriminator (behaviorally indistinguishable here), and it is an assertion ON the
        // EXISTING exactly-once test rather than a new source-shape family.
        var routeEncodeCalls = CountCallsTo(route, typeof(WorkSlotRegistryCodec), "Encode");
        Assert.True(
            routeEncodeCalls == 0,
            $"the ownership-aware route emits {routeEncodeCalls} call(s) to WorkSlotRegistryCodec.Encode — " +
            "the route body must perform NO encode of its own; the single encode lives in the freeze.");

        // THE CONVENIENCE FORM IS A PURE DELEGATION: no freeze of its own, so the single
        // implementation above really is the only one.
        var convenience = RequireMethod(
            typeof(PipelineStore), "SaveAdmissionWithPointer",
            typeof(GoalPipeline), typeof(string), typeof(AdmissionOwnershipSnapshot));
        Assert.Equal(0, CountCallsTo(convenience, typeof(PipelineStore), "FreezeOwnershipCheckpoint"));
        Assert.Equal(0, CountCallsTo(convenience, typeof(WorkSlotRegistryCodec), "Encode"));
    }

    /// <summary>
    /// THE EXACTLY-ONE-ENCODE PROOF, part 2 (RUNTIME): the registry the caller hands over is
    /// ENUMERATED EXACTLY ONCE PER COLLECTION by the encode — the exact total the current
    /// implementation produces (<c>Encode</c> walks <c>Slots</c> once and <c>DispatchAttempts</c>
    /// once) — so a second encode inside <c>FreezeOwnershipCheckpoint</c> doubles the observed
    /// counts and fails this vector.
    /// </summary>
    /// <remarks>
    /// THE ENTRY POINT IS THE ORDINARY CHECKPOINT SAVE ON PURPOSE: it passes the caller's snapshot
    /// to the SAME <c>FreezeOwnershipCheckpoint</c> WITHOUT copying it, so the counting wrappers
    /// are the very collections the encode walks. (The admission route's preflight copies the
    /// registry into fresh lists first, so a wrapper handed to the admission could not observe the
    /// encode at all — the probe would be vacuous there.) The durable blob is decoded afterwards to
    /// prove the single enumeration really produced the whole payload.
    /// </remarks>
    [Fact]
    public void FreezeOwnershipCheckpoint_EnumeratesTheCapturedRegistryExactlyOncePerCollection()
    {
        const string goalId = "live-encode-once-goal";
        var store = CreateStore();

        var (source, activeTaskId) = BuildRichPipeline(goalId + "-src");
        var captured = source.CaptureAdmissionOwnership();
        AssertSnapshotIsRich(captured.Registry);

        var slots = new CountingList<WorkSlotView>(captured.Registry.Slots);
        var attempts = new CountingList<WorkSlotRegistryAttemptEntry>(captured.Registry.DispatchAttempts);
        var instrumented = new AdmissionOwnershipSnapshot(
            goalId, activeTaskId, new WorkSlotRegistrySnapshot(slots, attempts));

        var pipeline = NewPipeline(goalId);
        store.SavePipelineState(pipeline, instrumented);

        // THE EXACT EXPECTED TOTAL, derived from the current implementation: ONE encode, which
        // walks each collection ONCE. A duplicate encode makes these 2.
        Assert.Equal(1, slots.EnumerationCount);
        Assert.Equal(1, attempts.EnumerationCount);

        // ANTI-VACUITY: the single enumeration really produced the COMPLETE durable payload.
        var decoded = DecodeBlob(goalId);
        Assert.Equal(SlotsOf(captured.Registry), SlotsOf(decoded));
        Assert.Equal(CountersOf(captured.Registry), CountersOf(decoded));
        Assert.Equal(activeTaskId, RawPointer(goalId));
    }

    /// <summary>
    /// A read-only list that FORWARDS every element faithfully while COUNTING how many times it is
    /// enumerated — the deterministic interaction instrument the encode-once proof needs.
    /// </summary>
    private sealed class CountingList<T> : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _inner;
        private int _enumerationCount;

        public CountingList(IReadOnlyList<T> inner) => _inner = inner;

        /// <summary>How many times a caller began enumerating this collection.</summary>
        public int EnumerationCount => Volatile.Read(ref _enumerationCount);

        public T this[int index] => _inner[index];

        public int Count => _inner.Count;

        public IEnumerator<T> GetEnumerator()
        {
            Interlocked.Increment(ref _enumerationCount);
            return _inner.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // (9) No store stays memory-only
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
