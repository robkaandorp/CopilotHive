using System.Collections.Concurrent;
using System.Reflection;

using CopilotHive.Dashboard;
using CopilotHive.Git;
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
/// THE UNADOPTED-HOLD GRACE SWEEP of <see cref="StaleWorkerCleanupService"/>: a restored HELD
/// attempt (<see cref="GoalPipeline.IsRestoredActiveAttemptHold"/>) whose worker never re-registers
/// is released to the ordinary reclaim once
/// <see cref="CleanupDefaults.HeldAttemptAdoptionGraceMinutes"/> have elapsed since the service was
/// constructed.
/// <para>
/// The fixture is REAL end to end: a genuinely admitted Pending attempt is persisted through the
/// live manager APIs and reopened through a FRESH <see cref="GoalPipelineManager"/> over the same
/// shared-cache in-memory SQLite database (the orchestrator-restart shape); the
/// <see cref="TaskQueue"/>, the <see cref="WorkerPool"/> (empty — no stale or timed-out workers, so
/// only the sweep can act), the <see cref="GoalDispatcher"/> redispatch queue and the
/// <see cref="DashboardNotifier"/> are real. The grace clock is the service's internal
/// <c>UtcNow</c>/<c>StartedAtUtc</c> seam — no sleeps, no timing waits.
/// </para>
/// <para>
/// REMOVAL-PROOFNESS. Removing the sweep call from <c>RunCleanupCycleAsync</c> fails every positive
/// vector (the hold stays, nothing is enqueued); bypassing the grace gate fails the one-tick-before
/// vector; relaxing the gate from <c>&gt;=</c> to <c>&gt;</c> fails the at-grace boundary; dropping
/// the blank-pointer check fails the blank vector (the hold is released and a redispatch enqueued);
/// dropping the release CAS fails every positive vector (the reclaim's own hold fence then refuses
/// the still-held pipeline); and unwrapping a reclaim log emission from <c>LogSafely</c> makes the
/// throwing-logger vector's cycle throw.
/// </para>
/// </summary>
public sealed class HeldAttemptGraceReleaseTests : IDisposable
{
    private const string SweepWarningMarker = "not adopted within grace — releasing to ordinary reclaim";
    private const string BlankWarningMarker = "has a blank active-task pointer — left held; cancel or reset the goal manually";

    private static readonly DateTime Origin = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(CleanupDefaults.HeldAttemptAdoptionGraceMinutes);
    private static readonly TimeSpan CycleBound = TimeSpan.FromSeconds(30);

    private readonly string _connectionString =
        $"Data Source=file:memdb-grace-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keeper;
    private readonly List<SqliteConnection> _connections = [];
    private readonly List<CopilotHiveDbContext> _contexts = [];

    public HeldAttemptGraceReleaseTests()
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

    private static Goal NewGoal(string id) =>
        new() { Id = id, Description = "grace goal " + id, RepositoryNames = ["test-repo"] };

    private static void Arrange(GoalPipeline pipeline, GoalPhase phase)
    {
        var plan = IterationPlan.Default(includeImprove: true);
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, phase);
        pipeline.AdvanceTo(phase);
    }

    /// <summary>
    /// Persists a GENUINELY ADMITTED Pending attempt through the live manager APIs (create →
    /// capture → atomic claim → committed admission) and returns its task id.
    /// </summary>
    private string SeedHeldAttempt(string goalId)
    {
        var manager = new GoalPipelineManager(CreateStore(), NullLogger<GoalPipelineManager>.Instance);
        var pipeline = manager.CreatePipeline(NewGoal(goalId));
        Arrange(pipeline, GoalPhase.Coding);

        var slot = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.True(pipeline.TrySetActiveTask(slot.TaskId, "copilothive/" + goalId));

        var admission = manager.PersistAdmission(pipeline, slot.TaskId);
        Assert.Equal(AdmissionCommitStatus.Committed, admission.Status);
        Assert.Equal(1, RawMappingCount(slot.TaskId));
        return slot.TaskId;
    }

    private GoalPipelineManager NewRestartManager() =>
        new(CreateFactoryBackedStore(), NullLogger<GoalPipelineManager>.Instance);

    /// <summary>
    /// Restores the attempt through <paramref name="manager"/> and asserts it really is a HELD,
    /// ACTIVE-SLOT-PENDING attempt whose pointer names <paramref name="taskId"/> ordinally.
    /// </summary>
    private static GoalPipeline RestoreHeld(GoalPipelineManager manager, string goalId, string taskId, bool mappingExpected)
    {
        var pipeline = manager.RestorePipeline(goalId);

        Assert.NotNull(pipeline);
        Assert.True(pipeline!.IsRestoredActiveAttemptHold, "the fixture must restore HELD");
        Assert.Equal(RestoredActivePointerOutcome.ActiveSlotPending, pipeline.RestoredActivePointerClassification);
        Assert.Equal(taskId, pipeline.ActiveTaskId, StringComparer.Ordinal);
        Assert.Equal(WorkSlotState.Pending, SlotState(pipeline, taskId));
        if (mappingExpected)
            Assert.Same(pipeline, manager.GetByTaskId(taskId));
        else
            Assert.Null(manager.GetByTaskId(taskId));
        return pipeline;
    }

    private static WorkSlotState SlotState(GoalPipeline pipeline, string taskId) =>
        Assert.Single(pipeline.GetSlotsForTest(), v => v.Slot.TaskId == taskId).State;

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

    private int RawMappingCount(string taskId) =>
        int.Parse(RawScalar("SELECT COUNT(*) FROM task_mappings WHERE task_id = $task", ("$task", taskId))!, null);

    private void RawDeleteMapping(string taskId)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "DELETE FROM task_mappings WHERE task_id = $task";
        command.Parameters.AddWithValue("$task", taskId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private void RawSetPointer(string goalId, string value)
    {
        using var command = _keeper.CreateCommand();
        command.CommandText = "UPDATE pipelines SET active_task_id = $value WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$goal", goalId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static GoalDispatcher CreateDispatcher(GoalPipelineManager manager, TaskQueue queue) =>
        new(
            new GoalManager(),
            manager,
            queue,
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

    /// <summary>The service under test plus every observable the sweep can touch.</summary>
    private sealed class Harness
    {
        public required StaleWorkerCleanupService Service { get; init; }
        public required GoalDispatcher Dispatcher { get; init; }
        public required TaskQueue Queue { get; init; }
        public required WorkerPool Pool { get; init; }
        public int DashboardNotifications;
        public DateTime Now = Origin;

        public Task RunCycleAsync() => Service.RunCleanupCycleAsync().WaitAsync(CycleBound, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Builds the service over an EMPTY real <see cref="WorkerPool"/> (no stale or timed-out
    /// workers, so only the sweep and its reclaim run) with the grace origin pinned at
    /// <see cref="Origin"/> and the clock under test control.
    /// </summary>
    private static Harness CreateHarness(GoalPipelineManager manager, ILogger<StaleWorkerCleanupService> logger)
    {
        var queue = new TaskQueue();
        var pool = new WorkerPool();
        var dispatcher = CreateDispatcher(manager, queue);
        var notifier = new DashboardNotifier();

        var service = new StaleWorkerCleanupService(
            pool, queue, manager, logger, goalDispatcher: dispatcher, dashboardNotifier: notifier);

        var harness = new Harness { Service = service, Dispatcher = dispatcher, Queue = queue, Pool = pool };
        notifier.OnStateChanged += () => Interlocked.Increment(ref harness.DashboardNotifications);
        service.StartedAtUtc = Origin;
        service.UtcNow = () => harness.Now;
        return harness;
    }

    private static int CountWarnings(TestLogger<StaleWorkerCleanupService> logger, string marker) =>
        logger.LogEntries.Count(e => e.LogLevel == LogLevel.Warning && e.Message.Contains(marker, StringComparison.Ordinal));

    /// <summary>Asserts the held attempt is EXACTLY as restored and nothing was enqueued or notified.</summary>
    private void AssertUntouched(Harness harness, GoalPipelineManager manager, GoalPipeline pipeline, string taskId)
    {
        Assert.True(pipeline.IsRestoredActiveAttemptHold, "the hold must still be in force");
        Assert.Equal(taskId, pipeline.ActiveTaskId, StringComparer.Ordinal);
        Assert.Equal(WorkSlotState.Pending, SlotState(pipeline, taskId));
        Assert.Same(pipeline, manager.GetByTaskId(taskId));
        Assert.Equal(1, RawMappingCount(taskId));
        Assert.Empty(QueuedRedispatches(harness.Dispatcher));
        Assert.Equal(0, Volatile.Read(ref harness.DashboardNotifications));
    }

    // ═══════════════════════════════ vectors ═══════════════════════════════

    /// <summary>
    /// (a) ONE TICK BEFORE THE GRACE: nothing is mutated. The SAME fixture then crosses the exact
    /// boundary (<c>UtcNow - StartedAtUtc == grace</c>) and is released — the non-vacuous positive
    /// control that pins the gate at <c>&gt;=</c>.
    /// </summary>
    [Fact]
    public async Task Sweep_OneTickBeforeGrace_NothingMutated_ThenAtGraceReleased()
    {
        const string goalId = "grace-before";
        var taskId = SeedHeldAttempt(goalId);
        var manager = NewRestartManager();
        var pipeline = RestoreHeld(manager, goalId, taskId, mappingExpected: true);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var harness = CreateHarness(manager, logger);

        harness.Now = Origin + Grace - TimeSpan.FromTicks(1);
        await harness.RunCycleAsync();

        AssertUntouched(harness, manager, pipeline, taskId);
        Assert.Equal(0, CountWarnings(logger, SweepWarningMarker));

        harness.Now = Origin + Grace;
        await harness.RunCycleAsync();

        Assert.False(pipeline.IsRestoredActiveAttemptHold);
        Assert.Equal(WorkSlotState.Abandoned, SlotState(pipeline, taskId));
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Equal([goalId], QueuedRedispatches(harness.Dispatcher));
    }

    /// <summary>
    /// (b) AT THE GRACE, NOT ADOPTED, MAPPING PRESENT: the hold is released, the slot retired, the
    /// pointer cleared, the mapping unregistered in memory AND in the persisted store, the goal
    /// queued for re-dispatch exactly once and the dashboard notified exactly once. A worker that
    /// re-registers afterwards loses the attempt: the adoption CAS is refused.
    /// </summary>
    [Fact]
    public async Task Sweep_AtGrace_NotAdopted_MappingPresent_ReleasesAndReclaims()
    {
        const string goalId = "grace-mapped";
        var taskId = SeedHeldAttempt(goalId);
        var manager = NewRestartManager();
        var pipeline = RestoreHeld(manager, goalId, taskId, mappingExpected: true);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var harness = CreateHarness(manager, logger);

        harness.Now = Origin + Grace;
        await harness.RunCycleAsync();

        Assert.False(pipeline.IsRestoredActiveAttemptHold, "the hold must be released");
        Assert.Equal(WorkSlotState.Abandoned, SlotState(pipeline, taskId));
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Equal(0, RawMappingCount(taskId));
        Assert.Equal([goalId], QueuedRedispatches(harness.Dispatcher));
        Assert.Equal(1, Volatile.Read(ref harness.DashboardNotifications));
        Assert.Null(harness.Queue.GetActiveTask(taskId));
        Assert.Null(harness.Queue.TryDequeueAny());

        Assert.Equal(1, CountWarnings(logger, SweepWarningMarker));
        var warning = Assert.Single(logger.LogEntries, e => e.Message.Contains(SweepWarningMarker, StringComparison.Ordinal));
        Assert.Equal(
            $"held attempt {taskId} for goal {goalId} not adopted within grace — releasing to ordinary reclaim",
            warning.Message);
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Information
            && e.Message == $"Worker (not adopted after restart) task {taskId} reclaimed — slot retired; queued for re-dispatch (goal {goalId})");
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains("reclaim-refused", StringComparison.Ordinal));

        // THE POLICY: a late re-registration can no longer adopt the released attempt.
        Assert.False(pipeline.TryAdoptRestoredActiveAttempt());
    }

    /// <summary>
    /// (c) AT/AFTER THE GRACE, NOT ADOPTED, NO TASK MAPPING: the sweep passes the pipeline itself
    /// (no mapping lookup), so the attempt is still released, retired, its pointer cleared and the
    /// goal queued for re-dispatch exactly once — the unregister is a harmless no-op.
    /// </summary>
    [Fact]
    public async Task Sweep_AfterGrace_NotAdopted_NoMapping_ReleasesAndReclaims()
    {
        const string goalId = "grace-unmapped";
        var taskId = SeedHeldAttempt(goalId);
        RawDeleteMapping(taskId);
        var manager = NewRestartManager();
        var pipeline = RestoreHeld(manager, goalId, taskId, mappingExpected: false);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var harness = CreateHarness(manager, logger);

        harness.Now = Origin + Grace + TimeSpan.FromMinutes(3);
        await harness.RunCycleAsync();

        Assert.False(pipeline.IsRestoredActiveAttemptHold, "the hold must be released");
        Assert.Equal(WorkSlotState.Abandoned, SlotState(pipeline, taskId));
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Equal([goalId], QueuedRedispatches(harness.Dispatcher));
        Assert.Equal(1, Volatile.Read(ref harness.DashboardNotifications));
        Assert.Equal(1, CountWarnings(logger, SweepWarningMarker));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains("with no pipeline", StringComparison.Ordinal));
    }

    /// <summary>
    /// (d) ALREADY ADOPTED: an attempt adopted before the sweep is untouched, while an unadopted
    /// held control in the SAME manager and the SAME cycle is released — so the adopted vector's
    /// "nothing happened" cannot be explained by a sweep that never ran.
    /// </summary>
    [Fact]
    public async Task Sweep_AfterGrace_AlreadyAdopted_Untouched_UnadoptedControlReleased()
    {
        const string adoptedGoal = "grace-adopted";
        const string controlGoal = "grace-control";
        var adoptedTask = SeedHeldAttempt(adoptedGoal);
        var controlTask = SeedHeldAttempt(controlGoal);
        var manager = NewRestartManager();
        var adopted = RestoreHeld(manager, adoptedGoal, adoptedTask, mappingExpected: true);
        var control = RestoreHeld(manager, controlGoal, controlTask, mappingExpected: true);
        Assert.True(adopted.TryAdoptRestoredActiveAttempt());
        Assert.False(adopted.IsRestoredActiveAttemptHold);

        var logger = new TestLogger<StaleWorkerCleanupService>();
        var harness = CreateHarness(manager, logger);

        harness.Now = Origin + Grace;
        await harness.RunCycleAsync();

        // The adopted attempt: pointer, slot and mapping exactly as adopted; never released.
        Assert.Equal(adoptedTask, adopted.ActiveTaskId, StringComparer.Ordinal);
        Assert.Equal(WorkSlotState.Pending, SlotState(adopted, adoptedTask));
        Assert.Same(adopted, manager.GetByTaskId(adoptedTask));
        Assert.Equal(1, RawMappingCount(adoptedTask));
        Assert.False(adopted.TryReleaseRestoredActiveAttemptHold(), "an adopted attempt must stay adopted");
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(adoptedTask, StringComparison.Ordinal));

        // The control: released and reclaimed in the same cycle.
        Assert.False(control.IsRestoredActiveAttemptHold);
        Assert.Equal(WorkSlotState.Abandoned, SlotState(control, controlTask));
        Assert.Null(control.ActiveTaskId);
        Assert.Equal([controlGoal], QueuedRedispatches(harness.Dispatcher));
        Assert.Equal(1, Volatile.Read(ref harness.DashboardNotifications));
    }

    /// <summary>
    /// (e) BLANK POINTER: a held pipeline whose restored pointer is blank is NOT released — it stays
    /// held, nothing is mutated, no redispatch and no dashboard notification; the bounded-limitation
    /// warning is emitted once per pass.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sweep_AfterGrace_BlankPointer_LeftHeld_NothingMutated(string blank)
    {
        var goalId = "grace-blank-" + blank.Length;
        var taskId = SeedHeldAttempt(goalId);
        RawSetPointer(goalId, blank);
        var manager = NewRestartManager();
        var pipeline = manager.RestorePipeline(goalId);
        Assert.NotNull(pipeline);
        Assert.True(pipeline!.IsRestoredActiveAttemptHold, "a non-null blank pointer must restore HELD");
        Assert.Equal(RestoredActivePointerOutcome.BlankPointer, pipeline.RestoredActivePointerClassification);
        Assert.Equal(blank, pipeline.ActiveTaskId);

        var logger = new TestLogger<StaleWorkerCleanupService>();
        var harness = CreateHarness(manager, logger);

        harness.Now = Origin + Grace;
        await harness.RunCycleAsync();

        Assert.True(pipeline.IsRestoredActiveAttemptHold, "a blank-pointer hold must be left held");
        Assert.Equal(blank, pipeline.ActiveTaskId);
        Assert.Equal(WorkSlotState.Pending, SlotState(pipeline, taskId));
        Assert.Same(pipeline, manager.GetByTaskId(taskId));
        Assert.Equal(1, RawMappingCount(taskId));
        Assert.Empty(QueuedRedispatches(harness.Dispatcher));
        Assert.Equal(0, Volatile.Read(ref harness.DashboardNotifications));
        Assert.Equal(0, CountWarnings(logger, SweepWarningMarker));
        var warning = Assert.Single(logger.LogEntries);
        Assert.Equal(LogLevel.Warning, warning.LogLevel);
        Assert.Equal(
            $"held attempt for goal {goalId} {BlankWarningMarker}",
            warning.Message);
    }

    /// <summary>
    /// (f) A LOGGER THAT THROWS ON EVERY CALL. The pool has NO stale or timed-out workers, so only
    /// the sweep and <c>ReclaimTask</c> run. Every emission on that path is best-effort, so the
    /// release, the retire, the unregister and the redispatch all still complete and the dashboard
    /// is notified. The logger's call count proves the emissions really ran (and really threw).
    /// </summary>
    [Fact]
    public async Task Sweep_AfterGrace_ThrowingLogger_ReleaseRetireRedispatchStillComplete()
    {
        const string goalId = "grace-throwing-logger";
        var taskId = SeedHeldAttempt(goalId);
        var manager = NewRestartManager();
        var pipeline = RestoreHeld(manager, goalId, taskId, mappingExpected: true);
        var logger = new ThrowingLogger();
        var harness = CreateHarness(manager, logger);
        Assert.Empty(harness.Pool.GetAllWorkers());

        harness.Now = Origin + Grace;
        await harness.RunCycleAsync();

        Assert.False(pipeline.IsRestoredActiveAttemptHold);
        Assert.Equal(WorkSlotState.Abandoned, SlotState(pipeline, taskId));
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Null(manager.GetByTaskId(taskId));
        Assert.Equal(0, RawMappingCount(taskId));
        Assert.Equal([goalId], QueuedRedispatches(harness.Dispatcher));
        Assert.Equal(1, Volatile.Read(ref harness.DashboardNotifications));

        // The sweep warning, the unregister debug and the outcome information — all threw.
        Assert.True(logger.Calls >= 3, $"expected at least 3 throwing emissions, saw {logger.Calls}");
    }

    /// <summary>
    /// (g) A SECOND CYCLE DOES NOTHING MORE: the released pipeline is no longer held, so the redispatch,
    /// the dashboard notification and the sweep warning all stay at exactly one.
    /// </summary>
    [Fact]
    public async Task Sweep_SecondCycle_DoesNothingMore()
    {
        const string goalId = "grace-second-cycle";
        var taskId = SeedHeldAttempt(goalId);
        var manager = NewRestartManager();
        var pipeline = RestoreHeld(manager, goalId, taskId, mappingExpected: true);
        var logger = new TestLogger<StaleWorkerCleanupService>();
        var harness = CreateHarness(manager, logger);

        harness.Now = Origin + Grace;
        await harness.RunCycleAsync();
        Assert.Equal([goalId], QueuedRedispatches(harness.Dispatcher));
        Assert.Equal(1, Volatile.Read(ref harness.DashboardNotifications));
        var entriesAfterFirst = logger.LogEntries.Count;

        harness.Now = Origin + Grace + TimeSpan.FromMinutes(1);
        await harness.RunCycleAsync();

        Assert.False(pipeline.IsRestoredActiveAttemptHold);
        Assert.Equal(WorkSlotState.Abandoned, SlotState(pipeline, taskId));
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Equal([goalId], QueuedRedispatches(harness.Dispatcher));
        Assert.Equal(1, Volatile.Read(ref harness.DashboardNotifications));
        Assert.Equal(1, CountWarnings(logger, SweepWarningMarker));
        Assert.Equal(entriesAfterFirst, logger.LogEntries.Count);
    }

    /// <summary>
    /// THE GRACE ORIGIN defaults to construction time: without the test's pinned origin, a service
    /// constructed "now" does not sweep "now" (the default clock is inside the grace).
    /// </summary>
    [Fact]
    public async Task Sweep_DefaultOrigin_IsConstructionTime_InsideGraceNothingMutated()
    {
        const string goalId = "grace-default-origin";
        var taskId = SeedHeldAttempt(goalId);
        var manager = NewRestartManager();
        var pipeline = RestoreHeld(manager, goalId, taskId, mappingExpected: true);
        var queue = new TaskQueue();
        var dispatcher = CreateDispatcher(manager, queue);
        var logger = new TestLogger<StaleWorkerCleanupService>();

        var before = DateTime.UtcNow;
        var service = new StaleWorkerCleanupService(
            new WorkerPool(), queue, manager, logger, goalDispatcher: dispatcher);
        var after = DateTime.UtcNow;

        Assert.InRange(service.StartedAtUtc, before, after);
        await service.RunCleanupCycleAsync().WaitAsync(CycleBound, TestContext.Current.CancellationToken);

        Assert.True(pipeline.IsRestoredActiveAttemptHold);
        Assert.Equal(taskId, pipeline.ActiveTaskId, StringComparer.Ordinal);
        Assert.Empty(QueuedRedispatches(dispatcher));
    }

    /// <summary>A logger whose every emission throws — and counts that it was called.</summary>
    private sealed class ThrowingLogger : ILogger<StaleWorkerCleanupService>
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("logger failure: " + formatter(state, exception));
        }
    }
}
