using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
    /// actually touches, with the log sink exposed for the exactly-one-warning assertions.
    /// </summary>
    private sealed record RegisterHarness(
        HiveOrchestratorService Service,
        WorkerPool Pool,
        TaskQueue Queue,
        TestLogger<HiveOrchestratorService> Logger,
        RestoredAttemptAdopter? Adopter);

    /// <summary>
    /// Builds the production Register path: the REAL adopter over the given manager, pool and queue,
    /// wired into a REAL <see cref="HiveOrchestratorService"/> as its optional trailing parameter.
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
    /// <returns>The harness, with fresh pool, queue and logger.</returns>
    private RegisterHarness CreateRegisterHarness(
        GoalPipelineManager manager, bool withAdopter, TaskQueue? queue = null)
    {
        // ONE pool and ONE queue, shared by the harness and the adopter: the adopter's registration
        // must land in the very pool and queue the Register reply is then asserted against.
        var pool = new WorkerPool();
        queue ??= new TaskQueue();
        var logger = new TestLogger<HiveOrchestratorService>();

        RestoredAttemptAdopter? concreteAdopter = withAdopter
            ? new RestoredAttemptAdopter(
                manager, pool, queue, CreateAssignmentStore(), NullLogger<RestoredAttemptAdopter>.Instance)
            : null;

        var dispatcher = new GoalDispatcher(
            new GoalManager(), manager, queue,
            new GrpcWorkerGateway(pool), new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        var service = new HiveOrchestratorService(
            pool, queue, manager, new TaskCompletionNotifier(), dispatcher, logger,
            restoredAttemptAdopter: concreteAdopter);

        return new RegisterHarness(service, pool, queue, logger, concreteAdopter);
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
    /// run, it names the expected check, and it carries BOTH identities. Counting EVERY warning —
    /// not merely the matching ones — is what kills a mutant that emits an extra unnamed record.
    /// </summary>
    /// <param name="harness">The harness whose records are inspected.</param>
    /// <param name="expectedCheck">The check name the single warning must carry.</param>
    /// <param name="workerId">The worker id the warning must name.</param>
    /// <param name="taskId">The task id the warning must name.</param>
    private static void AssertSingleAdoptionRefusalWarning(
        RegisterHarness harness, string expectedCheck, string workerId, string taskId)
    {
        var warning = Assert.Single(harness.Logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        Assert.True(
            warning.Message.Contains("was not adopted", StringComparison.Ordinal),
            $"the single warning must be the adoption refusal; actual: {warning.Message}");
        Assert.True(
            warning.Message.Contains($"check={expectedCheck})", StringComparison.Ordinal),
            $"the warning must name the check '{expectedCheck}'; actual: {warning.Message}");
        Assert.Contains(workerId, warning.Message, StringComparison.Ordinal);
        Assert.Contains(taskId, warning.Message, StringComparison.Ordinal);
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
    /// <param name="expectedCheck">The check name the single warning must carry.</param>
    [Theory]
    [InlineData("NoPipeline")]
    [InlineData("NotHeld")]
    [InlineData("PointerMismatch")]
    [InlineData("RegistryEvidenceUntrusted")]
    [InlineData("SlotNotPending")]
    [InlineData("NoAssignmentContext")]
    [InlineData("WorkerMismatch")]
    [InlineData("GoalMismatch")]
    [InlineData("SlotMismatch")]
    [InlineData("PhaseMismatch")]
    [InlineData("NoAdopter")]
    public async Task Register_FailedPrecondition_DeclinesWithOneNamedWarning(string expectedCheck)
    {
        var goalId = $"adopt-register-{expectedCheck}".ToLowerInvariant();
        var workerId = $"worker-{expectedCheck}".ToLowerInvariant();

        string claimedTaskId;
        string registeringWorkerId;
        bool useAdopter = true;
        bool expectHeld = true;
        GoalPipelineManager manager;
        GoalPipeline pipeline;

        switch (expectedCheck)
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
                throw new InvalidOperationException($"Unhandled case: {expectedCheck}");
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
        AssertSingleAdoptionRefusalWarning(harness, "ReadFailed", workerId, taskId);
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
}
