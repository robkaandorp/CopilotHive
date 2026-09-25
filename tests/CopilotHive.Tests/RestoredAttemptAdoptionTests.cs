using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// THE ATOMIC ADOPTION PRIMITIVES of the non-destructive restart slice, exercised against REAL
/// collaborators: a <see cref="GoalPipeline"/> genuinely restored from a persisted snapshot that
/// carries a hydrated registry and a PENDING active slot, the real
/// <see cref="WorkerPool"/>/<see cref="TaskQueue"/>, and the real SQLite
/// <see cref="WorkerAssignmentContextStore"/> holding the assignment context a real dispatch would
/// have recorded.
/// <para>
/// COVERAGE, honestly:
/// <list type="bullet">
///   <item><description>(g) <see cref="GoalPipeline.TryAdoptRestoredActiveAttempt"/> and
///     <see cref="GoalPipeline.TryReleaseRestoredActiveAttemptHold"/> are mutually exclusive, and
///     <see cref="GoalPipeline.TryRevertRestoredActiveAttemptAdoption"/> only ever transitions FROM
///     Adopted;</description></item>
///   <item><description>(h) <see cref="TaskQueue.TryActivateNew"/> never overwrites an existing
///     entry, and <see cref="TaskQueue.TryRemoveOwned"/> removes only the IDENTICAL instance —
///     refusing an equal-valued but distinct one;</description></item>
///   <item><description>the adopted happy path itself, as the non-vacuous positive control for the
///     refusals;</description></item>
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
    /// records the assignment context a real delivery would have left behind.
    /// </summary>
    private (string GoalId, string TaskId, int Attempt) SeedHeldAttempt(string goalId, string workerId)
    {
        var manager = new GoalPipelineManager(CreateStore(), NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        Arrange(pipeline, GoalPhase.Coding);

        var slot = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.True(pipeline.TrySetActiveTask(slot.TaskId, "copilothive/" + goalId));

        var admission = manager.PersistAdmission(pipeline, slot.TaskId);
        Assert.Equal(AdmissionCommitStatus.Committed, admission.Status);

        var recorded = CreateAssignmentStore().InsertOnce(new WorkerAssignmentContext(
            goalId,
            workerId,
            WorkerRole.Coder,
            new WorkSlot(slot.TaskId, slot.Position, slot.Attempt),
            HeldModel));
        Assert.Equal(WorkerAssignmentWriteStatus.Recorded, recorded.Status);

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
}
