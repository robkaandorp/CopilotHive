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
    /// The mapping could not be claimed because this manager already held an in-memory entry for
    /// the task. The refusal is NON-THROWING — it is reported through the result. (Blank arguments
    /// no longer map here: invalid input throws <see cref="ArgumentException"/> before any mutation.)
    /// </summary>
    DuplicateMapping,

    /// <summary>
    /// RETAINED FOR COMPATIBILITY, UNREACHABLE from <see cref="GoalPipelineManager.TryRegisterTask"/>.
    /// The registers are memory-only since admission-atomic-switch removed their store path, so no
    /// register can produce a store-related outcome and this member is never returned by them. The
    /// persistence-failure outcome of the production admission path is
    /// <see cref="AdmissionCommitStatus.PersistenceFailed"/> on <see cref="AdmissionCommitResult"/>.
    /// </summary>
    PersistenceFailed,
}

/// <summary>Outcome of an ownership-checked, MEMORY-ONLY task registration.</summary>
/// <param name="Success"><c>true</c> only when the mapping is owned by the requesting goal.</param>
/// <param name="Cause">Why the registration failed, or <see cref="TaskRegistrationFailure.None"/>.
/// The only reachable failure is <see cref="TaskRegistrationFailure.DuplicateMapping"/>.</param>
/// <param name="PersistenceException">RETAINED FOR COMPATIBILITY, ALWAYS <c>null</c>: the registers
/// no longer touch the store, so they can never carry a store exception. The admission path's
/// store exception is carried on <see cref="AdmissionCommitResult.PersistenceException"/>.</param>
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

/// <summary>PersistAdmission's outcome, extended with the cleanup truth the production dispatch's
/// refusal and rollback flows act on.</summary>
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
    /// MEMORY-ONLY OWNERSHIP OF THE REGISTERS (admission-atomic-switch). <see cref="RegisterTask"/>
    /// and <see cref="TryRegisterTask"/> guard <see cref="_taskToGoal"/> ONLY — they no longer
    /// perform ANY store write, and they REFUSE rather than OVERWRITE an existing mapping. Four
    /// intentional BREAKING PUBLIC-API changes came with that conversion:
    /// <list type="number">
    ///   <item><description><see cref="RegisterTask"/>'s unconditional overwrite became a silent,
    ///     non-throwing refusal (TryAdd semantics).</description></item>
    ///   <item><description><see cref="RegisterTask"/>'s durable registration became memory-only —
    ///     its <c>SaveTaskMapping</c> call is removed.</description></item>
    ///   <item><description>Both registers now reject blank <c>taskId</c>/<c>goalId</c> with
    ///     <see cref="ArgumentException"/>, validated BEFORE any mutation, instead of silently
    ///     no-op'ing (TryRegisterTask) or poisoning the dictionary (RegisterTask).</description></item>
    ///   <item><description><see cref="TryRegisterTask"/>'s entire store path is removed; its
    ///     store-related result members (<see cref="TaskRegistrationFailure.PersistenceFailed"/>,
    ///     <see cref="TaskRegistrationResult.PersistenceException"/>) are retained for
    ///     compatibility but are UNREACHABLE from it.</description></item>
    /// </list>
    /// The persisted <c>task_mappings</c> row is written on the production ADMISSION PATH
    /// exclusively by <see cref="PersistAdmission"/>.
    /// </para>
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
    ///     <c>LoadPipeline</c>, <c>LoadActivePipelines</c>, <c>SaveAdmissionWithPointer</c>,
    ///     <c>ClearActiveTaskIdIfMatches</c>) is
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

    /// <summary>
    /// Registers an IN-MEMORY mapping from taskId → goalId so pipelines can be looked up by task.
    /// </summary>
    /// <remarks>
    /// BREAKING PUBLIC-API CHANGES (intentional, admission-atomic-switch):
    /// <list type="number">
    ///   <item><description>OVERWRITE BECOMES REFUSAL. The call used to assign the dictionary slot
    ///     unconditionally (<c>_taskToGoal[taskId] = goalId</c>), silently re-pointing a mapping that
    ///     already belonged to another goal. It now uses TryAdd semantics: an existing mapping — for
    ///     ANY goal, including the same one — is left EXACTLY as it is. The duplicate is declined
    ///     SILENTLY: no exception is thrown and no log record is added (this method introduces no
    ///     logging contract; the duplicate warning lives on
    ///     <see cref="TryRegisterTask"/>, where it already applied).</description></item>
    ///   <item><description>DURABLE REGISTRATION BECOMES MEMORY-ONLY. The store write
    ///     (<c>SaveTaskMapping</c>) is REMOVED. Even with a store configured, this method persists
    ///     NOTHING: the persisted <c>task_mappings</c> row is written on the production admission
    ///     path exclusively by <see cref="PersistAdmission"/>. Callers that relied on this method to
    ///     make a mapping durable must call the admission path (or the store directly, e.g. fixture
    ///     seeding via <c>PipelineStore.SaveTaskMapping</c>).</description></item>
    ///   <item><description>BLANK INPUT NOW THROWS. A null/blank <paramref name="taskId"/> or
    ///     <paramref name="goalId"/> raises <see cref="ArgumentException"/> instead of poisoning the
    ///     dictionary with a blank key. Validation runs BEFORE any mutation.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="taskId">The worker task id to map; must be non-blank.</param>
    /// <param name="goalId">The goal that claims the task; must be non-blank.</param>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> or <paramref name="goalId"/> is
    /// null, empty or whitespace.</exception>
    public void RegisterTask(string taskId, string goalId)
    {
        // VALIDATION FIRST, taskId then goalId — no mutation may precede a rejected argument.
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);

        lock (_mappingLock)
        {
            // TryAdd semantics: an existing mapping — for ANY goal — is NEVER overwritten. The
            // duplicate is declined SILENTLY: no exception, and no new logging contract is
            // introduced here (the duplicate warning belongs to TryRegisterTask, where it
            // already applies).
            _taskToGoal.TryAdd(taskId, goalId);
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
    /// Ownership-checked registration of an IN-MEMORY task → goal mapping: claims the mapping in
    /// this manager's dictionary, refusing rather than stealing a mapping that already belongs to
    /// someone else. MEMORY-ONLY — nothing is persisted, with or without a store.
    /// </summary>
    /// <remarks>
    /// The algorithm, in order:
    /// <list type="number">
    ///   <item><description>Blank <paramref name="taskId"/>, then blank <paramref name="goalId"/> →
    ///     <see cref="ArgumentException"/>, thrown BEFORE any mutation.</description></item>
    ///   <item><description>TryAdd. Success → the mapping is ours, an in-memory-only success.</description></item>
    ///   <item><description>An entry already exists (any goal) → refusal with
    ///     <see cref="TaskRegistrationFailure.DuplicateMapping"/>; the existing entry is left INTACT.</description></item>
    /// </list>
    /// <para>
    /// BREAKING PUBLIC-API CHANGE (intentional, admission-atomic-switch): THE ENTIRE STORE PATH IS
    /// REMOVED — the store-null early-out, the <c>TrySaveTaskMappingIfUnowned</c> conditional write,
    /// the store-conflict in-memory rollback, the carried persistence exception and their log lines
    /// are gone. This method is genuinely memory-only EVEN WHEN A STORE EXISTS, so it can no longer
    /// return <see cref="TaskRegistrationFailure.PersistenceFailed"/> and never populates
    /// <c>PersistenceException</c>. Those result members are RETAINED for source compatibility only
    /// (see their own remarks). The persisted <c>task_mappings</c> row is written on the production
    /// admission path exclusively by <see cref="PersistAdmission"/>.
    /// </para>
    /// <para>
    /// A duplicate refusal is NON-THROWING (it is reported through the result); only INVALID INPUT
    /// throws. NOTE: no cross-manager reconciliation is performed or claimed. The real system has
    /// exactly ONE manager (the singleton); all this method promises is that OUR memory matches OUR
    /// refusal.
    /// </para>
    /// </remarks>
    /// <param name="taskId">The worker task id to map; must be non-blank.</param>
    /// <param name="goalId">The goal that claims the task; must be non-blank.</param>
    /// <returns>The registration outcome — success, or a DuplicateMapping refusal.</returns>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> or <paramref name="goalId"/> is
    /// null, empty or whitespace.</exception>
    internal TaskRegistrationResult TryRegisterTask(string taskId, string goalId)
    {
        // VALIDATION FIRST, taskId then goalId — no mutation may precede a rejected argument.
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);

        lock (_mappingLock)
        {
            // TryAdd semantics: a task this manager already tracks is refused, and the existing
            // entry is left EXACTLY as it is — never stolen, never re-pointed.
            if (!_taskToGoal.TryAdd(taskId, goalId))
            {
                _logger?.LogWarning(
                    "Task {TaskId} is already mapped to goal {ExistingGoalId}; refusing registration for goal {GoalId}",
                    taskId, _taskToGoal.TryGetValue(taskId, out var existing) ? existing : "(unknown)", goalId);
                return new TaskRegistrationResult(false, TaskRegistrationFailure.DuplicateMapping, null);
            }

            return new TaskRegistrationResult(true, TaskRegistrationFailure.None, null);
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
    /// (the E2a-i transaction). THE PRODUCTION DISPATCH'S ADMISSION STEP — <c>TaskDispatchService</c>
    /// calls this immediately after its atomic <c>TrySetActiveTask</c> claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// QUALIFIED EXCLUSIVITY. This method is the EXCLUSIVE writer of persisted <c>task_mappings</c>
    /// rows ON THE PRODUCTION ADMISSION PATH. That is NOT a claim of exclusivity over the table or
    /// over persistence generally: <c>PipelineStore.SaveTaskMapping</c>,
    /// <c>TrySaveTaskMappingIfUnowned</c>, <c>DeleteTaskMapping(IfForGoal)</c>,
    /// <c>RemovePipeline</c> and the restore paths still write/remove rows, and fixture seeding
    /// commonly uses <c>SaveTaskMapping</c> directly.
    /// </para>
    /// <para>
    /// THE GUARANTEES (the honest wording): the mapping WRITERS are serialized (every
    /// <see cref="_taskToGoal"/> mutation runs under <see cref="_mappingLock"/>) and the DATABASE
    /// commit is ATOMIC — the <c>task_mappings</c> row and the pipelines row's <c>active_task_id</c>
    /// pointer land in ONE transaction (<c>SaveAdmissionWithPointer</c>) or neither does. The
    /// persisted pointer is the IMMUTABLE SNAPSHOT validated at claim time (the taskId argument is
    /// passed through as the store's <c>activeTaskIdOverride</c>), never a later live-pointer
    /// re-read, so a concurrent pointer change can never be persisted by this commit.
    /// </para>
    /// <para>
    /// NO-STORE BEHAVIOR: with no store configured the in-memory claim ALONE is the admission —
    /// nothing is persisted and the result is <see cref="AdmissionCommitStatus.NoStore"/> with
    /// <c>CommittedThisInvocation == false</c>.
    /// </para>
    /// <para>
    /// THERE IS NO SINGLE LINEARIZABLE MEMORY-PLUS-DATABASE EVENT: lock-free readers may observe the
    /// transient in-memory claim before the commit resolves, and the claim may subsequently roll
    /// back. The outcome record carries ClaimedThisInvocation and CommittedThisInvocation — the
    /// cleanup truths the dispatch's refusal and enqueue-rollback sequences act on (the field
    /// semantics per the record's param docs).
    /// </para>
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
    /// dispatch's <c>ClearActiveTaskIfCurrent</c> step).</summary>
    /// <remarks>ITS CALLER IS THE PRODUCTION DISPATCH: <c>TaskDispatchService</c>'s enqueue-failure
    /// rollback invokes this when — and only when — its admission actually committed the rows
    /// (<c>CommittedThisInvocation</c>). CONTRACT: a null pipeline or a null/blank taskId →
    /// PointerRollbackResult.NotMatched
    /// (the safe no-op — no store call); a null store → NotMatched (nothing persisted to roll back);
    /// the store's NotMatched → the correct completion (the row absent or its pointer unmatched —
    /// the ownership-check invariant held; nothing further to undo); the store's Cleared → the
    /// persisted pointer is NULL; the store's Failed → the store's (a) WARNING surfaced, this method
    /// returns Failed (the row's state unknown — the durable-reconciliation successor owns the
    /// residue; the dispatch records it as a <c>pointer-rollback</c> rollback failure and continues).
    /// NEVER PROPAGATES
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
