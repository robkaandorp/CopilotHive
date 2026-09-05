using System.Collections.Concurrent;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Why a <see cref="GoalPipelineManager.TryRegisterTask"/> call refused to take ownership of a
/// task → goal mapping.
/// </summary>
internal enum TaskRegistrationFailure
{
    /// <summary>No failure — the mapping is owned by the requesting goal.</summary>
    None,

    /// <summary>
    /// The mapping could not be claimed: the argument was blank, this manager already held an
    /// in-memory entry for the task, or the persisted row belongs to a different goal.
    /// </summary>
    DuplicateMapping,

    /// <summary>The persisted write threw; the exception is carried on the result.</summary>
    PersistenceFailed,
}

/// <summary>Outcome of an ownership-checked task registration.</summary>
/// <param name="Success"><c>true</c> only when the mapping is owned by the requesting goal.</param>
/// <param name="Cause">Why the registration failed, or <see cref="TaskRegistrationFailure.None"/>.</param>
/// <param name="PersistenceException">The store exception when <see cref="Cause"/> is
/// <see cref="TaskRegistrationFailure.PersistenceFailed"/>; otherwise <c>null</c>.</param>
internal record TaskRegistrationResult(bool Success, TaskRegistrationFailure Cause, Exception? PersistenceException);

/// <summary>Outcome of an ownership-checked task unregistration.</summary>
/// <param name="MemoryRemoved"><c>true</c> when the in-memory entry for the pair was removed.</param>
/// <param name="PersistenceRemoved">
/// <c>true</c> when the persisted row was removed (or there was nothing to persist);
/// <c>false</c> signals persisted RESIDUE — the row was absent, owned by another goal, or the
/// delete threw. The residue is reported honestly rather than silently ignored.
/// </param>
internal record TaskUnregisterResult(bool MemoryRemoved, bool PersistenceRemoved);

/// <summary>
/// The outcome kind of <see cref="GoalPipelineManager.PersistAdmission"/>.
/// </summary>
internal enum AdmissionCommitStatus
{
    /// <summary>The in-memory claim is held AND the mapping+pointer rows were committed.</summary>
    Committed,

    /// <summary>This manager already held an in-memory entry for the task; the store was never called.</summary>
    MemoryConflict,

    /// <summary>The persisted mapping row is owned by another attempt; the in-memory claim was rolled back.</summary>
    PersistConflict,

    /// <summary>The store threw; the exception is carried on the result and the in-memory claim was rolled back.</summary>
    PersistenceFailed,

    /// <summary>No store is configured; the in-memory claim alone is held.</summary>
    NoStore,
}

/// <summary>PersistAdmission's outcome, extended with the cleanup truth for the successor's
/// dispatch flows.</summary>
/// <param name="Status">The admission's outcome status (the α enum, unchanged:
/// Committed, MemoryConflict, PersistConflict, PersistenceFailed, NoStore).</param>
/// <param name="ClaimedThisInvocation">True IFF THIS call created the in-memory mapping claim
/// (_taskToGoal gained the pair during this invocation). False ONLY on MemoryConflict: a
/// pre-existing claim — identical or foreign — existed before this call and is NOT ours to
/// remove. True on every other status: Committed/NoStore (the claim stands), and
/// PersistConflict/PersistenceFailed (this call DID claim before the α rollback removed it).</param>
/// <param name="CommittedThisInvocation">True ONLY on Committed: this call committed the DB rows
/// (the task_mappings row AND the pipelines row's active_task_id pointer, one transaction via
/// SaveAdmissionWithPointer). False on every other status — including NoStore (the in-memory
/// claim alone; nothing persisted) and the conflict/failure statuses (the transaction refused,
/// rolled back, or never ran).</param>
/// <param name="PersistenceException">The store's original exception — the exact exception caught
/// from SaveAdmissionWithPointer (the EF DbUpdateException wrapper when the store's SQL failure
/// surfaces through EF; the interceptor's sentinel is that wrapper's InnerException — the α's
/// existing identity contract preserved). Non-null IFF Status == PersistenceFailed.</param>
internal sealed record AdmissionCommitResult(
    AdmissionCommitStatus Status,
    bool ClaimedThisInvocation,
    bool CommittedThisInvocation,
    Exception? PersistenceException = null);

/// <summary>
/// Singleton that holds all active goal pipelines and provides lookup by goalId or taskId.
/// Persists pipeline state to SQLite via <see cref="PipelineStore"/>.
/// </summary>
public sealed class GoalPipelineManager
{
    private readonly ConcurrentDictionary<string, GoalPipeline> _pipelines = new();
    private readonly ConcurrentDictionary<string, string> _taskToGoal = new();

    /// <summary>
    /// THE SINGLE LOCK over this manager's MAPPING SURFACE — <see cref="RegisterTask"/>,
    /// <see cref="TryRegisterTask"/>, <see cref="UnregisterTask"/>, <see cref="TryUnregisterTask"/>,
    /// <see cref="RestorePipeline"/>, <see cref="RestoreFromStore"/>, <see cref="RemovePipeline"/>
    /// and <see cref="PersistAdmission"/>. Every mutation of <see cref="_taskToGoal"/> performed by
    /// those methods runs under it, so the WRITERS are serialized: a memory claim and its persisted
    /// counterpart can no longer be interleaved by a second mapping-surface call.
    /// <para>
    /// The results and functional contracts are unchanged; the log lines are GUARDED — a throwing
    /// logger is swallowed and the outcome returned (the β-PREP-2 hardening). For non-throwing
    /// loggers (the production wiring) nothing observable changes. The ONLY observable change is
    /// CONCURRENCY TIMING: mapping-surface calls for UNRELATED tasks now serialize with each other,
    /// which adds contention and latency under load. That contention — and the possible-deadlock
    /// surface introduced by holding the lock across the store and logger seams — is DOCUMENTED AS A
    /// RISK, not eliminated. This is NOT a general deadlock-safety guarantee.
    /// </para>
    /// <para>
    /// (a) THE REENTRANCY TRUTH. <see cref="Monitor"/> is reentrant PER THREAD, and that reentrancy
    /// protects against NEITHER danger that matters here. First, a same-thread recursive call back
    /// into the mapping surface (e.g. a store or logger callback that re-enters
    /// <c>UnregisterTask</c>) is silently ADMITTED by the Monitor and mutates the dictionary in the
    /// middle of an in-flight admission — mutual exclusion is not violated, but the ATOMICITY the
    /// lock is supposed to provide is. Second, cross-thread cycles are not helped at all: a thread
    /// holding <c>_mappingLock</c> that synchronously waits on another thread which is itself
    /// blocked acquiring <c>_mappingLock</c> deadlocks outright.
    /// </para>
    /// <para>
    /// (b) THE PRECONDITION. Components invoked while the lock is held — today the
    /// <see cref="PipelineStore"/> and the <see cref="ILogger{TCategoryName}"/> — MUST NOT re-enter
    /// this manager's mapping surface, and MUST NOT synchronously wait (<c>Task.Wait</c>,
    /// <c>.Result</c>, <c>Join</c>, a blocking handle) on other threads that do. Any future
    /// component wired under the lock must be audited against this precondition before it is added.
    /// </para>
    /// <para>
    /// (c) THE AUDIT LOCATION. The precondition was verified by CONCRETE INSPECTION of the
    /// production wiring at the time this lock was introduced:
    /// <list type="bullet">
    ///   <item><description><see cref="PipelineStore"/> — every method (<c>SavePipeline</c>,
    ///     <c>SavePipelineState</c>, <c>SaveTaskMapping</c>, <c>TrySaveTaskMappingIfUnowned</c>,
    ///     <c>DeleteTaskMapping</c>, <c>DeleteTaskMappingIfForGoal</c>, <c>RemovePipeline</c>,
    ///     <c>LoadPipeline</c>, <c>LoadActivePipelines</c>, <c>SaveAdmissionWithPointer</c>) is
    ///     synchronous EF Core work over a per-operation context; none holds a reference to
    ///     <see cref="GoalPipelineManager"/>, none raises events or invokes callbacks into it, and
    ///     none blocks on another thread. Its only injectable seams —
    ///     <c>PipelineStore.ContextDisposerForTest</c> and EF <c>IInterceptor</c>s — are
    ///     TEST-ONLY: production registers no interceptors (see the plain
    ///     <c>AddDbContextFactory(options =&gt; options.UseSqlite(...))</c> in <c>Program.cs</c>)
    ///     and leaves the disposer seam null.</description></item>
    ///   <item><description>The production logger wiring in <c>Program.cs</c> — the default console
    ///     provider plus category filters, and the single custom provider
    ///     <c>DashboardLoggerProvider</c>. Its logger appends to <c>DashboardLogSink</c>'s
    ///     <c>ConcurrentQueue</c> and raises <c>OnNewEntry</c>, which has NO production subscriber;
    ///     the sink performs no manager callback and no synchronous cross-thread wait.</description></item>
    /// </list>
    /// Both seams are therefore confirmed callback-free into this manager and free of synchronous
    /// cross-thread waits.
    /// </para>
    /// </summary>
    private readonly object _mappingLock = new();

    private readonly PipelineStore? _store;
    private readonly ILogger<GoalPipelineManager>? _logger;

    /// <summary>
    /// Initialises a new <see cref="GoalPipelineManager"/>.
    /// </summary>
    /// <param name="store">Optional persistence store; when provided, pipeline state is saved to SQLite.</param>
    /// <param name="logger">
    /// Optional logger for the ownership-checked registration APIs. Production instances are
    /// constructed WITH the DI-injected logger (Program.cs wires ILogger&lt;GoalPipelineManager&gt;
    /// into the singleton); the parameter remains optional for null-logger test fixtures.
    /// </param>
    public GoalPipelineManager(PipelineStore? store = null, ILogger<GoalPipelineManager>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Create and register a new pipeline for a goal.</summary>
    public GoalPipeline CreatePipeline(Goal goal, int maxRetries = Constants.DefaultMaxRetriesPerTask, int maxIterations = Constants.DefaultMaxIterations)
    {
        var pipeline = new GoalPipeline(goal, maxRetries, maxIterations);
        if (!_pipelines.TryAdd(goal.Id, pipeline))
            throw new InvalidOperationException($"Pipeline already exists for goal '{goal.Id}'");

        _store?.SavePipeline(pipeline);
        return pipeline;
    }

    /// <summary>Get a pipeline by goal ID.</summary>
    public GoalPipeline? GetByGoalId(string goalId) =>
        _pipelines.TryGetValue(goalId, out var p) ? p : null;

    /// <summary>Get a pipeline by its currently active task ID.</summary>
    public GoalPipeline? GetByTaskId(string taskId) =>
        _taskToGoal.TryGetValue(taskId, out var goalId) ? GetByGoalId(goalId) : null;

    /// <summary>Register a mapping from taskId → goalId so we can look up pipelines by task.</summary>
    public void RegisterTask(string taskId, string goalId)
    {
        lock (_mappingLock)
        {
            _taskToGoal[taskId] = goalId;
            _store?.SaveTaskMapping(taskId, goalId);
        }
    }

    /// <summary>
    /// Restore a single pipeline from the persistent store by goal ID, regardless of phase.
    /// If the pipeline is already in memory, returns the existing instance.
    /// Returns null if the store is unavailable or no pipeline is found.
    /// </summary>
    public GoalPipeline? RestorePipeline(string goalId)
    {
        lock (_mappingLock)
        {
            // If already in memory, return existing
            if (_pipelines.TryGetValue(goalId, out var existing))
                return existing;

            if (_store is null)
                return null;

            var snapshot = _store.LoadPipeline(goalId);
            if (snapshot is null)
                return null;

            var pipeline = new GoalPipeline(snapshot);
            if (_pipelines.TryAdd(goalId, pipeline))
            {
                foreach (var (taskId, gid) in snapshot.TaskMappings)
                    _taskToGoal[taskId] = gid;
            }
            else
            {
                // Another thread added it — return their instance
                return _pipelines[goalId];
            }

            return pipeline;
        }
    }

    /// <summary>
    /// Remove a task mapping from both the in-memory dictionary and the persistent store.
    /// </summary>
    public void UnregisterTask(string taskId)
    {
        lock (_mappingLock)
        {
            _taskToGoal.TryRemove(taskId, out _);
            _store?.DeleteTaskMapping(taskId);
        }
    }

    /// <summary>
    /// Ownership-checked registration of a task → goal mapping: claims the mapping in memory AND
    /// (when a store is present) in the persisted <c>task_mappings</c> row, refusing rather than
    /// stealing a mapping that already belongs to someone else.
    /// </summary>
    /// <remarks>
    /// The algorithm, in order:
    /// <list type="number">
    ///   <item><description>Blank argument → a safe no-op refusal. No memory write, no SQL.</description></item>
    ///   <item><description>This manager already holds an entry for the task → refusal. The SQL is NEVER executed on this path.</description></item>
    ///   <item><description>No store → in-memory-only success.</description></item>
    ///   <item><description>The conditional write claims (or re-claims) the row → success.</description></item>
    ///   <item><description>The row belongs to another goal → PAIR-BASED rollback of our own in-memory
    ///     entry, leaving the competing row INTACT.</description></item>
    ///   <item><description>The write threw → pair-based rollback, and the exception is CARRIED on the result.</description></item>
    /// </list>
    /// NOTE: no cross-manager reconciliation is performed or claimed. The real system has exactly
    /// ONE manager (the singleton); a second manager observing the same database is a test-fixture
    /// artifact, and all this method promises there is that OUR memory matches OUR refusal.
    /// </remarks>
    /// <param name="taskId">The worker task id to map.</param>
    /// <param name="goalId">The goal that claims the task.</param>
    /// <returns>The registration outcome.</returns>
    internal TaskRegistrationResult TryRegisterTask(string taskId, string goalId)
    {
        lock (_mappingLock)
        {
            if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(goalId))
            {
                _logger?.LogDebug(
                    "TryRegisterTask called with blank taskId or goalId — no-op refusal (taskId='{TaskId}', goalId='{GoalId}')",
                    taskId, goalId);
                return new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null);
            }

            // The in-memory claim comes FIRST: a task this manager already tracks is refused before a
            // single statement reaches the database.
            if (!_taskToGoal.TryAdd(taskId, goalId))
            {
                _logger?.LogWarning(
                    "Task {TaskId} is already mapped to goal {ExistingGoalId}; refusing registration for goal {GoalId}",
                    taskId, _taskToGoal.TryGetValue(taskId, out var existing) ? existing : "(unknown)", goalId);
                return new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null);
            }

            if (_store is null)
                return new TaskRegistrationResult(true, TaskRegistrationFailure.None, null);

            bool persisted;
            try
            {
                persisted = _store.TrySaveTaskMappingIfUnowned(taskId, goalId);
            }
            catch (Exception ex)
            {
                // Pair-based rollback removes OUR entry only — never another goal's.
                _taskToGoal.TryRemove(KeyValuePair.Create(taskId, goalId));
                _logger?.LogError(ex, "Failed to persist task mapping {TaskId} → {GoalId}", taskId, goalId);
                return new TaskRegistrationResult(false, TaskRegistrationFailure.PersistenceFailed, ex);
            }

            if (persisted)
                return new TaskRegistrationResult(true, TaskRegistrationFailure.None, null);

            // The persisted row is another goal's. Roll our own in-memory entry back so this manager's
            // memory agrees with the refusal; the competing row stays INTACT.
            _taskToGoal.TryRemove(KeyValuePair.Create(taskId, goalId));
            _logger?.LogWarning(
                "Task mapping {TaskId} is already owned by another goal in the store; refusing registration for goal {GoalId}",
                taskId, goalId);
            return new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null);
        }
    }

    /// <summary>
    /// Ownership-checked removal of a task → goal mapping. NEVER THROWS.
    /// </summary>
    /// <remarks>
    /// The in-memory removal is PAIR-BASED: an entry that has already been re-pointed at another
    /// goal is left alone and reported as <c>(false, false)</c>. When the memory entry was ours,
    /// the persisted row is deleted conditionally; a 0-row delete (row absent or another goal's)
    /// and a store exception both yield <c>(true, false)</c> — the residue is SIGNALLED in the
    /// record rather than silently swallowed. With no store there is nothing to persist, so the
    /// persistence flag is vacuously <c>true</c>.
    /// <para>
    /// NEVER THROWS, given any logger: the method's own log lines are guarded (a throwing logger
    /// is swallowed; the outcome returned). The store-failure path catches the store's exception
    /// and returns (true, false) — the residue signalled.
    /// </para>
    /// </remarks>
    /// <param name="taskId">The worker task id to unmap.</param>
    /// <param name="goalId">The goal that must own the mapping.</param>
    /// <returns>The unregistration outcome.</returns>
    internal TaskUnregisterResult TryUnregisterTask(string taskId, string goalId)
    {
        lock (_mappingLock)
        {
            if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(goalId))
            {
                // GUARDED SITE (β-PREP-2): a throwing logger is swallowed; the outcome returns.
                try
                {
                    _logger?.LogDebug(
                        "TryUnregisterTask called with blank taskId or goalId — no-op (taskId='{TaskId}', goalId='{GoalId}')",
                        taskId, goalId);
                }
                catch
                {
                    // Diagnostic failure only — never allowed to affect the guarded operation.
                }

                return new TaskUnregisterResult(false, false);
            }

            if (!_taskToGoal.TryRemove(KeyValuePair.Create(taskId, goalId)))
            {
                // GUARDED SITE (β-PREP-2): a throwing logger is swallowed; the outcome returns.
                try
                {
                    _logger?.LogDebug(
                        "Task {TaskId} is not mapped to goal {GoalId} in memory — nothing removed",
                        taskId, goalId);
                }
                catch
                {
                    // Diagnostic failure only — never allowed to affect the guarded operation.
                }

                return new TaskUnregisterResult(false, false);
            }

            if (_store is null)
                return new TaskUnregisterResult(true, true);

            try
            {
                var deleted = _store.DeleteTaskMappingIfForGoal(taskId, goalId);
                if (!deleted)
                {
                    // GUARDED SITE (β-PREP-2): a throwing logger is swallowed; the outcome returns.
                    try
                    {
                        _logger?.LogWarning(
                            "Task mapping {TaskId} for goal {GoalId} was not deleted (absent or owned by another goal); persisted residue remains",
                            taskId, goalId);
                    }
                    catch
                    {
                        // Diagnostic failure only — never allowed to affect the guarded operation.
                    }
                }
                return new TaskUnregisterResult(true, deleted);
            }
            catch (Exception ex)
            {
                // GUARDED SITE (β-PREP-2): a throwing logger is swallowed; the outcome returns.
                // The STORE's original exception is still carried as the residue signal — the
                // two-exception distinction (only the LOGGER's throw is swallowed).
                try
                {
                    _logger?.LogError(
                        ex, "Failed to delete persisted task mapping {TaskId} → {GoalId}; persisted residue remains", taskId, goalId);
                }
                catch
                {
                    // Diagnostic failure only — never allowed to affect the guarded operation.
                }

                return new TaskUnregisterResult(true, false);
            }
        }
    }

    /// <summary>
    /// Admits a task attempt: the in-memory mapping claim plus the persisted mapping+pointer rows
    /// (the E2a-i transaction). GUARANTEE (the honest wording): the mapping WRITERS are serialized
    /// (every _taskToGoal mutation under _mappingLock) and the DATABASE commit is atomic. NOT a
    /// single linearizable memory-plus-database event: lock-free readers may observe the transient
    /// claim before the commit resolves, and the claim may roll back — the honesty stated.
    /// </summary>
    /// <remarks>
    /// The API remains uncalled in production (the successor admission-atomic-switch migrates the
    /// dispatch onto it); the outcome record carries ClaimedThisInvocation and
    /// CommittedThisInvocation — the cleanup truths for the successor's Flow-A/Flow-B handling
    /// (the field semantics per the record's param docs).
    /// </remarks>
    /// <param name="pipeline">The pipeline claiming the task; its <c>ActiveTaskId</c> MUST equal
    /// <paramref name="taskId"/>.</param>
    /// <param name="taskId">The worker task id being admitted.</param>
    /// <returns>The admission outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> is null or blank, or does not
    /// equal <paramref name="pipeline"/>'s active task id.</exception>
    internal AdmissionCommitResult PersistAdmission(GoalPipeline pipeline, string taskId)
    {
        // THE VALIDATIONS run BEFORE the lock: an invalid call never touches the dictionary and
        // never contends for the mapping surface.
        ArgumentNullException.ThrowIfNull(pipeline);

        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task id must be a non-blank value.", nameof(taskId));

        if (pipeline.ActiveTaskId != taskId)
            throw new ArgumentException(
                $"Task id '{taskId}' does not match the pipeline's active task id '{pipeline.ActiveTaskId}' (goal={pipeline.GoalId}).",
                nameof(taskId));

        lock (_mappingLock)
        {
            // (1) PREFLIGHT: a task this manager already tracks is refused before a single
            //     statement reaches the database and before any claim is made.
            if (_taskToGoal.TryGetValue(taskId, out var existingGoalId))
            {
                // GUARDED SITE (β-PREP-2): a throwing logger is swallowed; the outcome returns.
                try
                {
                    _logger?.LogDebug(
                        "Admission refused — task {TaskId} is already mapped (goal={ExistingGoalId}); no store call made",
                        taskId, existingGoalId);
                }
                catch
                {
                    // Diagnostic failure only — never allowed to affect the guarded operation.
                }

                return new AdmissionCommitResult(
                    AdmissionCommitStatus.MemoryConflict,
                    ClaimedThisInvocation: false,
                    CommittedThisInvocation: false);
            }

            // (2) THE CLAIM.
            _taskToGoal[taskId] = pipeline.GoalId;

            // (3) NO STORE: the claim alone is the admission.
            if (_store is null)
            {
                // GUARDED SITE (β-PREP-2): a throwing logger is swallowed; the outcome returns.
                try
                {
                    _logger?.LogDebug(
                        "Admission committed in memory only — no store configured (goal={GoalId} task={TaskId})",
                        pipeline.GoalId, taskId);
                }
                catch
                {
                    // Diagnostic failure only — never allowed to affect the guarded operation.
                }

                return new AdmissionCommitResult(
                    AdmissionCommitStatus.NoStore,
                    ClaimedThisInvocation: true,
                    CommittedThisInvocation: false);
            }

            // (4) THE E2a-i PRIMITIVE, consumed untouched.
            AdmissionStoreResult storeResult;
            try
            {
                storeResult = _store.SaveAdmissionWithPointer(pipeline, taskId);
            }
            catch (Exception ex)
            {
                // Pair-based rollback removes OUR claim only — never another attempt's.
                _taskToGoal.TryRemove(KeyValuePair.Create(taskId, pipeline.GoalId));
                // GUARDED SITE (β-PREP-2): a throwing logger is swallowed; the outcome returns.
                // The STORE's original exception is still carried on the result — the
                // two-exception distinction (only the LOGGER's throw is swallowed).
                try
                {
                    _logger?.LogWarning(
                        ex,
                        "Admission failed — the store threw for task {TaskId} (goal={GoalId}); the memory claim removed",
                        taskId, pipeline.GoalId);
                }
                catch
                {
                    // Diagnostic failure only — never allowed to affect the guarded operation.
                }

                return new AdmissionCommitResult(
                    AdmissionCommitStatus.PersistenceFailed,
                    ClaimedThisInvocation: true,
                    CommittedThisInvocation: false,
                    ex);
            }

            switch (storeResult)
            {
                case AdmissionStoreResult.Committed:
                    // GUARDED SITE (β-PREP-2): a throwing logger is swallowed; the outcome returns.
                    try
                    {
                        _logger?.LogDebug(
                            "Admission committed goal={GoalId} task={TaskId}",
                            pipeline.GoalId, taskId);
                    }
                    catch
                    {
                        // Diagnostic failure only — never allowed to affect the guarded operation.
                    }

                    return new AdmissionCommitResult(
                        AdmissionCommitStatus.Committed,
                        ClaimedThisInvocation: true,
                        CommittedThisInvocation: true);

                case AdmissionStoreResult.PersistConflict:
                    _taskToGoal.TryRemove(KeyValuePair.Create(taskId, pipeline.GoalId));
                    // GUARDED SITE (β-PREP-2): a throwing logger is swallowed; the outcome returns.
                    try
                    {
                        _logger?.LogDebug(
                            "Admission rolled back — task {TaskId}'s persisted row is owned by another attempt; the memory claim removed",
                            taskId);
                    }
                    catch
                    {
                        // Diagnostic failure only — never allowed to affect the guarded operation.
                    }

                    return new AdmissionCommitResult(
                        AdmissionCommitStatus.PersistConflict,
                        ClaimedThisInvocation: true,
                        CommittedThisInvocation: false);

                default:
                    throw new InvalidOperationException(
                        $"Unhandled admission store result '{storeResult}' for task '{taskId}' (goal={pipeline.GoalId}).");
            }
        }
    }

    /// <summary>Rolls back the PERSISTED pipeline pointer (the pipelines row's active_task_id) for a
    /// failed admission's task: the ownership-checked store clear under <see cref="_mappingLock"/>.
    /// THE IN-MEMORY pointer is NOT touched (the caller's own ownership-checked clear owns it — the
    /// successor's dispatch Flow-B step).</summary>
    /// <remarks>CONTRACT: a null pipeline or a null/blank taskId → PointerRollbackResult.NotMatched
    /// (the safe no-op — no store call); a null store → NotMatched (nothing persisted to roll back);
    /// the store's NotMatched → the correct completion (the row absent or its pointer unmatched —
    /// the ownership-check invariant held; nothing further to undo); the store's Cleared → the
    /// persisted pointer is NULL; the store's Failed → the store's (a) WARNING surfaced, this method
    /// returns Failed (the row's state unknown — the durable-reconciliation successor owns the
    /// residue; the successor's Flow-B treats Failed as its reconciliation trigger). NEVER PROPAGATES
    /// store or logging failures (the store's guards; this method performs no logging of its own) —
    /// ordinary runtime failures (a thread abort mid-lock, out-of-memory) remain outside every
    /// method's practical guarantee and are not claimed away.</remarks>
    /// <param name="pipeline">The pipeline whose persisted pointer is rolled back; nullable — a null
    /// is the safe no-op.</param>
    /// <param name="taskId">The task whose pointer is expected; null/blank is the safe no-op.</param>
    /// <returns>The tri-state outcome — the caller distinguishes the safely-preserved pointer from
    /// the failed rollback.</returns>
    internal PointerRollbackResult RollbackPersistedPointer(GoalPipeline? pipeline, string? taskId)
    {
        if (pipeline is null || string.IsNullOrWhiteSpace(taskId))
            return PointerRollbackResult.NotMatched;   // the null/blank no-op — no store call
        lock (_mappingLock)
        {
            return _store?.ClearActiveTaskIdIfMatches(pipeline.GoalId, taskId)
                   ?? PointerRollbackResult.NotMatched;  // the null store — nothing persisted
        }
    }

    /// <summary>Persist the current state of a pipeline (call after state mutations).</summary>
    public void PersistState(GoalPipeline pipeline) => _store?.SavePipelineState(pipeline);

    /// <summary>Persist the full pipeline including conversation.</summary>
    public void PersistFull(GoalPipeline pipeline) => _store?.SavePipeline(pipeline);

    /// <summary>Get all active (non-completed) pipelines.</summary>
    public IReadOnlyList<GoalPipeline> GetActivePipelines() =>
        _pipelines.Values
            .Where(p => p.Phase is not (GoalPhase.Done or GoalPhase.Failed))
            .ToList()
            .AsReadOnly();

    /// <summary>Get all pipelines regardless of state.</summary>
    public IReadOnlyList<GoalPipeline> GetAllPipelines() =>
        _pipelines.Values.ToList().AsReadOnly();

    /// <summary>Remove a completed pipeline to free memory and clean up storage.</summary>
    public bool RemovePipeline(string goalId)
    {
        lock (_mappingLock)
        {
            var wasInMemory = _pipelines.TryRemove(goalId, out _);
            if (wasInMemory)
            {
                foreach (var key in _taskToGoal.Where(kv => kv.Value == goalId).Select(kv => kv.Key).ToList())
                    _taskToGoal.TryRemove(key, out _);
            }
            _store?.RemovePipeline(goalId);  // always clean up the store, even if not in memory
            return wasInMemory;
        }
    }

    /// <summary>Restore pipelines from persistent store (called once at startup).</summary>
    public List<GoalPipeline> RestoreFromStore()
    {
        lock (_mappingLock)
        {
            if (_store is null) return [];

            var snapshots = _store.LoadActivePipelines();
            var restored = new List<GoalPipeline>();

            foreach (var snap in snapshots)
            {
                var pipeline = new GoalPipeline(snap);
                if (_pipelines.TryAdd(snap.GoalId, pipeline))
                {
                    foreach (var (taskId, goalId) in snap.TaskMappings)
                        _taskToGoal[taskId] = goalId;
                    restored.Add(pipeline);
                }
            }

            return restored;
        }
    }

    /// <summary>
    /// Returns the persisted session JSON for the specified role in a goal's pipeline,
    /// or <c>null</c> if the goal or role session does not exist.
    /// </summary>
    /// <param name="goalId">The goal whose pipeline to look up.</param>
    /// <param name="roleName">The role name whose session to retrieve (case-insensitive).</param>
    /// <returns>The session JSON, or <c>null</c>.</returns>
    public string? GetRoleSession(string goalId, string roleName)
    {
        var pipeline = GetByGoalId(goalId);
        return pipeline?.GetRoleSession(roleName);
    }

    /// <summary>
    /// Stores the session JSON for the specified role in a goal's pipeline,
    /// then flushes the updated pipeline state to the persistent store so sessions
    /// survive orchestrator restarts.
    /// Does nothing if the goal pipeline does not exist.
    /// </summary>
    /// <param name="goalId">The goal whose pipeline to update.</param>
    /// <param name="roleName">The role name whose session to store (case-insensitive).</param>
    /// <param name="sessionJson">The serialised session JSON to persist.</param>
    public void SetRoleSession(string goalId, string roleName, string sessionJson)
    {
        var pipeline = GetByGoalId(goalId);
        if (pipeline is null) return;

        pipeline.SetRoleSession(roleName, sessionJson);
        PersistState(pipeline);
    }
}
