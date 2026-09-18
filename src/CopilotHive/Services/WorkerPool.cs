using System.Collections.Concurrent;
using CopilotHive.Models;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>Aggregate statistics about the worker pool at a point in time.</summary>
public sealed record WorkerPoolStats
{
    /// <summary>Total number of registered workers.</summary>
    public required int TotalWorkers { get; init; }
    /// <summary>Number of workers currently executing a task.</summary>
    public required int BusyWorkers { get; init; }
    /// <summary>Number of workers that are idle and available for tasks.</summary>
    public required int IdleWorkers { get; init; }
    /// <summary>Count of workers grouped by their role string.</summary>
    public required IReadOnlyDictionary<string, int> WorkersByRole { get; init; }
}

/// <summary>
/// A point-in-time, lock-consistent view of ONE worker's assignment ownership, taken under the
/// pool's activity lock so the three facts it carries can never be torn against each other.
/// </summary>
/// <remarks>
/// It is a pure OBSERVATION: the referenced instance stays live and its state may change before
/// the observer acts. That is exactly why the checked idle/release operations re-validate the
/// captured instance and task id under the same lock instead of trusting the snapshot.
/// </remarks>
internal readonly record struct WorkerOwnershipSnapshot
{
    /// <summary>The exact instance registered under the requested ID at the snapshot instant.</summary>
    public required ConnectedWorker Worker { get; init; }

    /// <summary>Whether that instance was executing a task at the snapshot instant.</summary>
    public required bool IsBusy { get; init; }

    /// <summary>The task the instance was executing, or <c>null</c> when it was idle.</summary>
    public required string? CurrentTaskId { get; init; }
}

/// <summary>
/// Thread-safe registry of currently connected workers. Supports registration,
/// lookup, heartbeat tracking, and busy/idle state management.
/// </summary>
public sealed class WorkerPool : IWorkerPool
{
    private readonly ConcurrentDictionary<string, ConnectedWorker> _workers = new();

    /// <summary>
    /// Guards all reads and writes of mutable worker state: <see cref="ConnectedWorker.IsBusy"/>,
    /// <see cref="ConnectedWorker.CurrentTaskId"/>, <see cref="ConnectedWorker.CurrentTaskStartedAt"/>,
    /// <see cref="ConnectedWorker.LastActivityAt"/>, <see cref="ConnectedWorker.LastHeartbeat"/> and
    /// <see cref="ConnectedWorker.ContextUsagePercent"/>.
    /// <para>
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> only makes the dictionary itself thread-safe —
    /// it establishes no happens-before relationship for mutable fields on the stored values. Without
    /// this lock the stream thread's activity write may never become visible to the cleanup thread,
    /// and inactivity-based reclamation could evict a worker that has just reported progress.
    /// </para>
    /// <para>
    /// The lock also covers dictionary membership changes made on the strength of that state
    /// (<see cref="RegisterWorker(string, string[])"/>, <see cref="RemoveWorker(string)"/>,
    /// <see cref="RemoveWorker(ConnectedWorker)"/>, <see cref="TryRemoveTimedOutWorker"/> and
    /// <see cref="PurgeStaleWorkers"/>), so a worker is never transiently absent from the pool while
    /// its state is being evaluated. A transient absence would make <see cref="TouchActivity"/>
    /// silently drop a stream activity update and let the following inactivity scan evict a worker
    /// that was, in fact, active.
    /// </para>
    /// <para>
    /// Membership and state share ONE lock, so the checked operations that both validate an
    /// instance and then mutate it — <see cref="TryClaimAndActivate"/> above all — cannot have the
    /// registration under that ID change between the two halves of their decision.
    /// </para>
    /// </summary>
    private readonly Lock _activityLock = new();

    /// <summary>
    /// Registers a new worker with the pool and returns the created <see cref="ConnectedWorker"/>.
    /// Throws if a worker with the same ID is already registered.
    /// </summary>
    /// <remarks>
    /// THE EXISTING TWO-ARGUMENT ENTRY POINT, PRESERVED: callers that cannot express a negotiation
    /// request register a worker that asked for nothing.
    /// </remarks>
    /// <param name="id">Unique identifier for the worker.</param>
    /// <param name="capabilities">Capabilities advertised by the worker.</param>
    /// <returns>The newly created <see cref="ConnectedWorker"/>.</returns>
    public ConnectedWorker RegisterWorker(string id, string[] capabilities) =>
        RegisterWorker(
            id, capabilities, requestCompletionReceiptAck: false, completionReceiptAckEnabled: false);

    /// <summary>
    /// Registers a new worker carrying the requested completion-receipt negotiation fact, and
    /// returns the created <see cref="ConnectedWorker"/>. Throws if a worker with the same ID is
    /// already registered.
    /// </summary>
    /// <remarks>
    /// THE EXISTING THREE-ARGUMENT CALLER, PRESERVED: it can express the request but not the
    /// orchestrator's ANSWER to it, so the instance it registers is left at the DISABLED default
    /// for <see cref="ConnectedWorker.CompletionReceiptAckEnabled"/> — the conservative choice, as
    /// an unanswered request is never enablement.
    /// </remarks>
    /// <param name="id">Unique identifier for the worker.</param>
    /// <param name="capabilities">Capabilities advertised by the worker.</param>
    /// <param name="requestCompletionReceiptAck">
    /// Whether the worker requested durable completion-receipt acknowledgement. A request, never an
    /// enablement.
    /// </param>
    /// <returns>The newly created <see cref="ConnectedWorker"/>.</returns>
    internal ConnectedWorker RegisterWorker(
        string id, string[] capabilities, bool requestCompletionReceiptAck) =>
        RegisterWorker(
            id, capabilities, requestCompletionReceiptAck, completionReceiptAckEnabled: false);

    /// <summary>
    /// Registers a new worker carrying BOTH per-registration negotiation facts — what the worker
    /// requested and what the orchestrator decided to enable — and returns the created
    /// <see cref="ConnectedWorker"/>. Throws if a worker with the same ID is already registered.
    /// </summary>
    /// <remarks>
    /// THE TWO FACTS ARE PUBLISHED WITH THE INSTANCE, never written afterwards: the
    /// <see cref="ConnectedWorker"/> is fully constructed — requested flag AND enablement decision
    /// included — BEFORE <c>TryAdd</c> makes it visible to any other thread, so a registered worker
    /// is never observable with either fact undecided.
    /// <para>
    /// The <c>TryAdd</c> happens under <c>_activityLock</c>, so the registration of an ID cannot
    /// interleave with a checked operation that has already validated — or is in the middle of
    /// mutating — the instance registered under that ID.
    /// </para>
    /// </remarks>
    /// <param name="id">Unique identifier for the worker.</param>
    /// <param name="capabilities">Capabilities advertised by the worker.</param>
    /// <param name="requestCompletionReceiptAck">
    /// Whether the worker requested durable completion-receipt acknowledgement. A request, never an
    /// enablement.
    /// </param>
    /// <param name="completionReceiptAckEnabled">
    /// The orchestrator's decision to acknowledge this registration's completions. Only the
    /// registration path that answered the request may pass <c>true</c>.
    /// </param>
    /// <returns>The newly created <see cref="ConnectedWorker"/>.</returns>
    internal ConnectedWorker RegisterWorker(
        string id,
        string[] capabilities,
        bool requestCompletionReceiptAck,
        bool completionReceiptAckEnabled)
    {
        var worker = new ConnectedWorker
        {
            Id = id,
            Role = WorkerRole.Unspecified,
            Capabilities = capabilities,
            RequestCompletionReceiptAck = requestCompletionReceiptAck,
            CompletionReceiptAckEnabled = completionReceiptAckEnabled,
        };

        // The membership change is taken under the same lock as the state it will be evaluated
        // against, so registration can never land between a checked operation's validation of the
        // instance registered under this ID and that operation's mutation.
        lock (_activityLock)
        {
            if (!_workers.TryAdd(id, worker))
                throw new InvalidOperationException($"Worker '{id}' is already registered.");
        }

        return worker;
    }

    /// <summary>
    /// Removes a worker from the pool by ID and closes its message channel.
    /// </summary>
    /// <remarks>
    /// NOT instance-safe: if a replacement worker has re-registered under the same ID (ABA),
    /// this removes the replacement. Use <see cref="RemoveWorker(ConnectedWorker)"/> for
    /// removal that may race with re-registration.
    /// <para>
    /// The <c>TryRemove</c> happens under <c>_activityLock</c> so membership cannot change between
    /// a participating checked operation's validation and its mutation; only the channel
    /// completion is performed outside the lock.
    /// </para>
    /// </remarks>
    /// <param name="id">Identifier of the worker to remove.</param>
    /// <returns><c>true</c> if a worker with the ID was found and removed; <c>false</c> otherwise.</returns>
    public bool RemoveWorker(string id)
    {
        ConnectedWorker worker;

        lock (_activityLock)
        {
            if (!_workers.TryRemove(id, out worker!))
                return false;
        }

        // Complete the channel outside the lock: completion can resume the worker's stream reader
        // inline, and no reader continuation should ever run while the activity lock is held.
        worker.MessageChannel.Writer.TryComplete();
        return true;
    }

    /// <summary>
    /// Removes the given worker instance from the pool, but only if that exact instance is
    /// still registered under its ID. Instance-aware: a replacement instance registered
    /// under the same ID (ABA) is never removed by this call.
    /// </summary>
    /// <remarks>
    /// The ABA check and the <c>TryRemove</c> happen together under <c>_activityLock</c>, so the
    /// exact instance validated is the one removed and membership cannot change between a
    /// participating checked operation's validation and its mutation. Only the channel completion
    /// is performed outside the lock.
    /// </remarks>
    /// <param name="worker">The exact <see cref="ConnectedWorker"/> instance to remove.</param>
    /// <returns>
    /// <c>true</c> if the exact instance was found and removed (and its message channel
    /// completed); <c>false</c> if a different instance — or nothing — is registered.
    /// </returns>
    public bool RemoveWorker(ConnectedWorker worker)
    {
        lock (_activityLock)
        {
            if (!_workers.TryRemove(new KeyValuePair<string, ConnectedWorker>(worker.Id, worker)))
                return false;
        }

        // Complete the channel outside the lock: completion can resume the worker's stream reader
        // inline, and no reader continuation should ever run while the activity lock is held.
        worker.MessageChannel.Writer.TryComplete();
        return true;
    }

    /// <summary>
    /// Returns the first SELECTABLE idle worker: one that is idle and NOT holding a
    /// completion publication. All workers are generic and accept any role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE TWO FACTS ARE READ UNDER THE SAME <c>_activityLock</c>, as ONE predicate — deliberately
    /// not as two independent unlocked reads. Reading <see cref="ConnectedWorker.IsBusy"/> and the
    /// completion-publication hold separately would let a candidate be selected on a stale pair of
    /// reads, which is exactly the gap this predicate exists to close.
    /// </para>
    /// <para>
    /// WHAT IT RETURNS IS STILL ONLY A CANDIDATE, NEVER A RESERVATION. The returned instance stays
    /// live and may become busy, be removed or be replaced before the caller acts; a dispatcher that
    /// already holds an older candidate may still busy it through the existing ID-based
    /// <see cref="MarkBusy"/>. This method excludes only NEW selections of a worker whose
    /// completion is still being published.
    /// </para>
    /// </remarks>
    /// <returns>A selectable idle <see cref="ConnectedWorker"/>, or <c>null</c>.</returns>
    public ConnectedWorker? GetIdleWorker()
    {
        lock (_activityLock)
        {
            foreach (var kvp in _workers)
            {
                if (!kvp.Value.IsBusy && !kvp.Value.CompletionPublicationPending)
                    return kvp.Value;
            }

            return null;
        }
    }

    /// <summary>Returns a read-only snapshot of all currently registered workers.</summary>
    public IReadOnlyList<ConnectedWorker> GetAllWorkers() =>
        _workers.Values.ToList().AsReadOnly();

    /// <summary>
    /// Looks up a worker by its identifier.
    /// </summary>
    /// <param name="id">Identifier of the worker to retrieve.</param>
    /// <returns>The worker, or <c>null</c> if not found.</returns>
    public ConnectedWorker? GetWorker(string id) =>
        _workers.GetValueOrDefault(id);

    /// <summary>
    /// Gets the number of workers currently registered in the pool.
    /// </summary>
    /// <returns>The count of entries in the internal worker dictionary.</returns>
    public int ConnectedWorkerCount => _workers.Count;

    /// <summary>
    /// Updates the last heartbeat timestamp for the specified worker.
    /// </summary>
    /// <remarks>
    /// Taken under <c>_activityLock</c> so the write is ordered against
    /// <see cref="PurgeStaleWorkers"/>, which decides eviction on the strength of this timestamp.
    /// </remarks>
    /// <param name="id">Identifier of the worker.</param>
    /// <param name="contextUsagePercent">Estimated context window usage as a percentage (0–100).</param>
    public void UpdateHeartbeat(string id, int contextUsagePercent = 0)
    {
        lock (_activityLock)
        {
            if (!_workers.TryGetValue(id, out var worker))
                return;

            worker.LastHeartbeat = DateTime.UtcNow;
            worker.ContextUsagePercent = contextUsagePercent;
        }
    }

    /// <summary>
    /// Marks the specified worker as busy with a task.
    /// </summary>
    /// <remarks>
    /// All four fields are published under <c>_activityLock</c> so that a reader can never observe
    /// the new <see cref="ConnectedWorker.IsBusy"/>/<see cref="ConnectedWorker.CurrentTaskId"/>
    /// together with a stale <see cref="ConnectedWorker.LastActivityAt"/> from a previous task —
    /// which would make a freshly assigned worker look immediately timed out.
    /// <para>
    /// IT IS THE UNCONDITIONAL ID-BASED ROUTE, PRESERVED EXACTLY: it resolves the instance
    /// registered under <paramref name="id"/> when it runs, performs no ownership or hold check, has
    /// a <c>void</c> return and refuses nothing silently. An older candidate may therefore still be
    /// busied through it after a checked claim has been taken elsewhere; that is a separate
    /// contract, not something this method guards.
    /// </para>
    /// </remarks>
    /// <param name="id">Identifier of the worker.</param>
    /// <param name="taskId">Identifier of the task the worker is executing.</param>
    public void MarkBusy(string id, string taskId)
    {
        lock (_activityLock)
        {
            if (!_workers.TryGetValue(id, out var worker))
                return;

            PublishBusyFieldsNoLock(worker, taskId, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// THE ONE BUSY-FIELD FIELD SET, shared by <see cref="MarkBusy"/> and by the checked claim so the
    /// two can never drift. Callers must hold <c>_activityLock</c>.
    /// </summary>
    /// <remarks>
    /// One caller-supplied timestamp is used for BOTH clocks, so a freshly claimed worker can never
    /// be observable as busy with an older activity clock than task-start clock. The activity clock
    /// is written BEFORE the busy flag, so the worker is never observable as "busy with an old
    /// <see cref="ConnectedWorker.LastActivityAt"/>".
    /// </remarks>
    /// <param name="worker">The captured worker instance to mark busy — never re-resolved by ID.</param>
    /// <param name="taskId">Identifier of the task the worker is executing.</param>
    /// <param name="now">The single UTC timestamp used for both activity and task-start clocks.</param>
    private static void PublishBusyFieldsNoLock(ConnectedWorker worker, string taskId, DateTime now)
    {
        worker.LastActivityAt = now;
        worker.CurrentTaskStartedAt = now;
        worker.CurrentTaskId = taskId;
        worker.IsBusy = true;
    }

    /// <summary>
    /// Records task-specific stream activity (ToolRequest, Progress, or Complete) for the
    /// specified worker by resetting its <see cref="ConnectedWorker.LastActivityAt"/> to now.
    /// This is the single synchronized authority for activity updates: the lookup and the write
    /// both happen under the same lock as <see cref="GetWorkersWithTimedOutTasks"/> and
    /// <see cref="TryRemoveTimedOutWorker"/>, so an activity update is always visible to — and
    /// strictly ordered against — inactivity-based reclamation. In particular, a worker can never
    /// be both touched and reclaimed: whichever operation acquires the lock first wins, and the
    /// other observes the result (fresh timestamp, or the worker already gone).
    /// </summary>
    /// <param name="id">Identifier of the worker that produced the activity.</param>
    /// <returns>
    /// <c>true</c> if the worker was still in the pool and its activity was recorded;
    /// <c>false</c> if it is no longer registered.
    /// </returns>
    public bool TouchActivity(string id)
    {
        lock (_activityLock)
        {
            if (!_workers.TryGetValue(id, out var worker))
                return false;

            worker.LastActivityAt = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Marks the specified worker as idle, clearing its current task identifier.
    /// </summary>
    /// <remarks>
    /// IT NEVER TOUCHES THE COMPLETION-PUBLICATION HOLD. The ID-based reset is a separate
    /// operation with separate callers (test helpers, recovery paths), so it must not be able to
    /// silently end a publication a completion path is still performing — and, symmetrically, it
    /// must not silently install one either.
    /// </remarks>
    /// <param name="id">Identifier of the worker.</param>
    public void MarkIdle(string id)
    {
        lock (_activityLock)
        {
            if (!_workers.TryGetValue(id, out var worker))
                return;

            ResetToIdleNoLock(worker);
        }
    }

    /// <summary>
    /// THE ONE IDLE-RESET FIELD SET, shared by <see cref="MarkIdle"/> and by the checked
    /// idle/release operations, so a checked release can never drift from the ID-based reset.
    /// Callers must hold <c>_activityLock</c>.
    /// </summary>
    /// <remarks>
    /// THE HOLD IS DELIBERATELY NOT ONE OF THESE FIELDS. This reset describes ASSIGNMENT state
    /// (busy, task, task start, role); the completion-publication hold describes an in-flight
    /// PUBLICATION and is owned exclusively by the checked release that installs it and the single
    /// clear operation that finishes it. Erasing it here would re-open the very selection window
    /// the hold exists to close, on behalf of an operation that knows nothing about the
    /// publication.
    /// </remarks>
    /// <param name="worker">The captured worker instance to reset — never re-resolved by ID.</param>
    private static void ResetToIdleNoLock(ConnectedWorker worker)
    {
        worker.IsBusy = false;
        worker.CurrentTaskId = null;
        worker.CurrentTaskStartedAt = null;
        worker.Role = WorkerRole.Unspecified;
    }

    /// <summary>
    /// Takes a lock-consistent snapshot of the ownership state of the worker currently registered
    /// under <paramref name="workerId"/>: the instance itself, its <see cref="ConnectedWorker.IsBusy"/>
    /// flag and its <see cref="ConnectedWorker.CurrentTaskId"/>, all read under <c>_activityLock</c>
    /// so the three facts belong to the SAME instant.
    /// </summary>
    /// <remarks>
    /// This is an OBSERVATION ONLY — it mutates nothing and it establishes no ownership. The
    /// returned instance stays live, so every acting caller must re-validate it through
    /// <see cref="TryReleaseCompletedTask"/> or
    /// <see cref="TryMarkIdleForReady"/>, which re-check the captured instance and task id under the
    /// same lock.
    /// </remarks>
    /// <param name="workerId">Identifier of the worker to observe.</param>
    /// <param name="snapshot">The snapshot, when a worker is registered under that ID.</param>
    /// <returns><c>true</c> when a worker was registered and observed; <c>false</c> otherwise.</returns>
    internal bool TryGetWorkerSnapshot(string workerId, out WorkerOwnershipSnapshot snapshot)
    {
        lock (_activityLock)
        {
            if (!_workers.TryGetValue(workerId, out var worker))
            {
                snapshot = default;
                return false;
            }

            snapshot = new WorkerOwnershipSnapshot
            {
                Worker = worker,
                IsBusy = worker.IsBusy,
                CurrentTaskId = worker.CurrentTaskId,
            };
            return true;
        }
    }

    /// <summary>
    /// THE CHECKED COMPLETION RELEASE. Marks <paramref name="expected"/> idle and clears its
    /// <see cref="ConnectedWorker.CurrentModel"/> — but ONLY when, at the mutation point and under
    /// <c>_activityLock</c>, that exact instance is still the one registered under its ID, it is
    /// still busy, and it is still executing <paramref name="expectedTaskId"/> (compared
    /// ORDINALLY).
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT THIS PROTECTS: a completion that arrives late must never release a SUCCESSOR's
    /// assignment. A replacement instance registered under the same ID (ABA), a worker that has
    /// moved on to a different task id, and a worker that is no longer busy are all REFUSED
    /// without touching a single assignment field.
    /// </para>
    /// <para>
    /// WHAT THIS DOES NOT PROTECT (deliberately out of scope): a concurrent reactivation of the
    /// SAME task id between an observation and this call. The reference/task checks here guard
    /// only this mutation; queue membership is a separate concurrent operation.
    /// </para>
    /// <para>
    /// THE EXISTING TWO-ARGUMENT ROUTE, PRESERVED: it installs NO completion-publication hold, so
    /// every legacy/direct caller keeps the unchanged release semantics and — importantly — never
    /// has unrelated selection state written on its behalf. It also deliberately leaves an
    /// EXISTING hold untouched rather than erasing one it did not install.
    /// </para>
    /// </remarks>
    /// <param name="expected">The exact instance the caller validated — the ONLY instance mutated.</param>
    /// <param name="expectedTaskId">The task the caller observed that instance executing.</param>
    /// <returns><c>true</c> when the release was applied; <c>false</c> when it was refused.</returns>
    internal bool TryReleaseCompletedTask(ConnectedWorker expected, string expectedTaskId) =>
        ReleaseCompletedTaskCore(expected, expectedTaskId, holdForCompletionPublication: false);

    /// <summary>
    /// THE COMPLETION-PUBLICATION ROUTE: THE SAME CHECKED RELEASE, plus the NARROW EXPLICIT OPT-IN of
    /// the negotiated ordinary completion path — a SUCCESSFUL release also INSTALLS the instance's
    /// completion-publication selection hold, in the SAME <c>_activityLock</c> span as the idle reset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY IT IS A SEPARATE ROUTE AND WHY IT IS OPT-IN. Only the negotiated ordinary completion path
    /// has an acknowledgement to publish after the release, so only that path has an interval in
    /// which the worker is already idle but not yet done publishing. Installing the hold IN THE SAME
    /// LOCK SPAN as the release is the whole point: there is then no instant at which the instance is
    /// observable as idle-and-selectable, so no dispatcher can NEWLY SELECT it in that interval.
    /// </para>
    /// <para>
    /// THE EXISTING <see cref="TryReleaseCompletedTask(ConnectedWorker, string)"/> CALLERS ARE
    /// UNCHANGED AND UNAFFECTED: they install no hold, and — symmetrically — they never clear or
    /// otherwise touch a hold this route installed on some other invocation's behalf.
    /// </para>
    /// <para>
    /// A REFUSED RELEASE INSTALLS NOTHING: the hold exists only for a release this route applied.
    /// The caller owns ending it through <see cref="ClearCompletionPublicationHold"/>.
    /// </para>
    /// </remarks>
    /// <param name="expected">The exact instance the caller validated — the ONLY instance mutated.</param>
    /// <param name="expectedTaskId">The task the caller observed that instance executing.</param>
    /// <returns><c>true</c> when the release was applied; <c>false</c> when it was refused.</returns>
    internal bool TryReleaseCompletedTaskHoldingForPublication(
        ConnectedWorker expected, string expectedTaskId) =>
        ReleaseCompletedTaskCore(expected, expectedTaskId, holdForCompletionPublication: true);

    /// <summary>
    /// THE ONE CHECKED-RELEASE IMPLEMENTATION both release routes share, so neither can drift from
    /// the other's validation or field set.
    /// </summary>
    /// <param name="expected">The exact instance the caller validated — the ONLY instance mutated.</param>
    /// <param name="expectedTaskId">The task the caller observed that instance executing.</param>
    /// <param name="holdForCompletionPublication">
    /// Whether the caller is the negotiated ordinary completion path and will therefore publish an
    /// acknowledgement after this release. <c>false</c> installs no hold.
    /// </param>
    /// <returns><c>true</c> when the release was applied; <c>false</c> when it was refused.</returns>
    private bool ReleaseCompletedTaskCore(
        ConnectedWorker expected, string expectedTaskId, bool holdForCompletionPublication)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(expectedTaskId);

        lock (_activityLock)
        {
            if (!IsStillOwnedNoLock(expected, expectedTaskId))
                return false;

            // A completion release only ever applies to a BUSY owner: an idle worker has nothing
            // of this task left to release.
            if (!expected.IsBusy)
                return false;

            ResetToIdleNoLock(expected);

            // THE MODEL IS CLEARED INSIDE THE CHECKED RELEASE, never afterwards: a later write
            // would land outside the ownership check and could clear a successor's model.
            expected.CurrentModel = null;

            // THE HOLD IS INSTALLED IN THIS SAME LOCK SPAN — the release is applied and the
            // instance is made unselectable as ONE observable step, so the "released but not yet
            // publishing" interval never becomes visible to a new selection.
            if (holdForCompletionPublication)
                expected.BeginCompletionPublicationHold();

            return true;
        }
    }

    /// <summary>
    /// THE COMPLETION-PUBLICATION HOLD'S ONLY CLEARING OPERATION: clears
    /// <see cref="ConnectedWorker.CompletionPublicationPending"/> on the EXACT instance the
    /// publication belongs to, under <c>_activityLock</c>, and touches NOTHING else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT IT DELIBERATELY DOES NOT DO: no idle reset, no role/model/task write, no activity-clock
    /// or heartbeat write, no queue or session operation, no dictionary change and — critically — no
    /// mutation of an ABA REPLACEMENT. A replacement registered under the same ID during the
    /// publication is not the instance whose publication is finishing, so it is left exactly as it
    /// is. The whole operation is one flag write on one captured reference: no timeout, no retry
    /// loop, no lease expiry, and no waiting for the queued acknowledgement to be written or
    /// received. It is idempotent.
    /// </para>
    /// <para>
    /// IT IS CALLED BY THE PUBLICATION PATH AND NOTHING ELSE. There is no timer, no reaper and no
    /// background expiry: the caller that installed the hold is the caller that ends it, which is
    /// why the hold can never outlive the publication attempt that created it.
    /// </para>
    /// </remarks>
    /// <param name="expected">The exact instance whose publication is finishing.</param>
    /// <returns>
    /// <c>true</c> when the exact instance was still the one registered under its ID and its hold is
    /// now cleared; <c>false</c> when nothing was mutated (a replacement, or no registered instance).
    /// </returns>
    internal bool ClearCompletionPublicationHold(ConnectedWorker expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        lock (_activityLock)
        {
            if (!_workers.TryGetValue(expected.Id, out var registered))
                return false;

            // ABA: a replacement under the same ID keeps its own state; the finishing publication
            // is about the captured instance only.
            if (!ReferenceEquals(registered, expected))
                return false;

            expected.EndCompletionPublicationHold();
            return true;
        }
    }

    /// <summary>
    /// THE CHECKED READY IDLE. Applies the idle reset to the instance the caller observed, under
    /// <c>_activityLock</c>, and only when the observation is still consistent at the mutation
    /// point. <see cref="ConnectedWorker.CurrentModel"/> is deliberately NOT touched — the Ready
    /// path's existing model behaviour is preserved.
    /// </summary>
    /// <remarks>
    /// <para>THE THREE ACCEPTED SHAPES:</para>
    /// <list type="bullet">
    ///   <item><description>AN IDLE/INITIAL READY — the observed <c>CurrentTaskId</c> is
    ///     <c>null</c> and the worker is NOT busy, at both the observation and the mutation
    ///     point. This is the first Ready of a stream and the Ready that follows an accepted
    ///     completion.</description></item>
    ///   <item><description>A RELEASING READY — the observed <c>CurrentTaskId</c> is non-null,
    ///     the worker was busy at the observation AND is still busy at the mutation point, the
    ///     caller observed that the task has NO active queue entry any more
    ///     (<paramref name="queueEntryAbsent"/>), and the registered instance and task id still
    ///     match the observation.</description></item>
    ///   <item><description>EVERYTHING ELSE IS REFUSED — including the inconsistent
    ///     "non-null task id but not busy" shape, which is refused WITHOUT releasing anything and
    ///     without writing a single assignment field.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="observed">The caller's snapshot: the captured instance, its busy flag and its task.</param>
    /// <param name="queueEntryAbsent">
    /// Whether the caller observed that the snapshot's task has no active queue entry. Ignored for
    /// the null-task shape; required for the non-null one. Queue inspection stays a SEPARATE
    /// concurrent operation — this is an observation the caller passes in, not a lock-held fact.
    /// </param>
    /// <returns><c>true</c> when the idle reset was applied; <c>false</c> when it was refused.</returns>
    internal bool TryMarkIdleForReady(WorkerOwnershipSnapshot observed, bool queueEntryAbsent)
    {
        var expected = observed.Worker;
        ArgumentNullException.ThrowIfNull(expected);

        lock (_activityLock)
        {
            // ── THE COMPLETION-PUBLICATION HOLD REFUSES THIS ENTIRE OPERATION ───────────────────
            // A Ready that arrives while this instance's negotiated completion is still being
            // published is a Ready for the interval this slice exists to protect: idling here would
            // re-publish the very "released and selectable" state the hold suppresses. The refusal
            // is taken BEFORE any shape check and mutates nothing.
            if (expected.CompletionPublicationPending)
                return false;

            if (!IsStillOwnedNoLock(expected, observed.CurrentTaskId))
                return false;

            if (observed.CurrentTaskId is null)
            {
                // THE IDLE/INITIAL READY: nothing may be released, so the worker must be idle
                // both when it was observed and now.
                return !observed.IsBusy && !expected.IsBusy && Applied(expected);
            }

            // THE RELEASING READY: the ownership must have been busy when observed, must still be
            // busy now, and the task must no longer be owned by the queue.
            if (!observed.IsBusy || !expected.IsBusy || !queueEntryAbsent)
                return false;

            return Applied(expected);
        }

        static bool Applied(ConnectedWorker worker)
        {
            // Only the CAPTURED instance is mutated — never a replacement resolved by ID.
            ResetToIdleNoLock(worker);
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="expected"/> is STILL the instance registered under its ID and is
    /// still executing <paramref name="expectedTaskId"/> (ordinal comparison, <c>null</c> meaning
    /// "no task"). Callers must hold <c>_activityLock</c>.
    /// </summary>
    private bool IsStillOwnedNoLock(ConnectedWorker expected, string? expectedTaskId)
    {
        if (!_workers.TryGetValue(expected.Id, out var registered))
            return false;

        // ABA: a replacement instance under the same ID is never mutated on the strength of the
        // old instance's observation.
        if (!ReferenceEquals(registered, expected))
            return false;

        return string.Equals(expected.CurrentTaskId, expectedTaskId, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CHECKED CLAIM-AND-ACTIVATE. Validates <paramref name="expected"/> and, only when the
    /// validation holds at the mutation point, performs the queue activation and the busy-field
    /// publication as ONE step under <c>_activityLock</c>.
    /// </summary>
    /// <remarks>
    /// <para>THE THREE CHECKS, IN THIS ORDER, EACH REFUSING WITH ZERO MUTATION:</para>
    /// <list type="number">
    ///   <item><description>EXACT REGISTERED REFERENCE — the instance registered under
    ///     <see cref="ConnectedWorker.Id"/> must be <paramref name="expected"/> itself
    ///     (<c>ReferenceEquals</c>), so an ABA replacement registered under
    ///     the same ID is never mutated on the strength of the old instance's validation.</description></item>
    ///   <item><description>IDLE/NULL-TASK SHAPE — <c>!IsBusy &amp;&amp; CurrentTaskId is null</c>;
    ///     a busy worker, or the inconsistent busy-less-but-has-a-task shape, is refused.</description></item>
    ///   <item><description>NOT HELD — <see cref="ConnectedWorker.CompletionPublicationPending"/>
    ///     must be <c>false</c>, so a worker whose negotiated completion is still being published is
    ///     never claimed.</description></item>
    /// </list>
    /// <para>
    /// A REFUSAL MUTATES NOTHING AT ALL: no worker field, no task metadata, no active-queue entry,
    /// no notification. <c>false</c> means precisely "this caller did not win", not "something was
    /// partially applied".
    /// </para>
    /// <para>
    /// THE GUARANTEE IS RELATIVE TO PARTICIPATING POOL OPERATIONS. Registration and removal are
    /// performed under this same lock, so membership cannot change between this validation and this
    /// mutation, and two simultaneous claims of the same instance cannot both succeed.
    /// </para>
    /// <para>
    /// THE EXPLICIT NON-GUARANTEES. It is NOT atomic with respect to queue observers — a caller that
    /// inspected <see cref="TaskQueue"/> before calling is looking at a separate concurrent object —
    /// nor with arbitrary external cleanup, nor with other UNCHECKED ID-based
    /// <see cref="MarkBusy"/> callers, which may still busy the instance afterwards. It promises no
    /// recovery if the worker is removed after a successful claim.
    /// </para>
    /// <para>
    /// ONLY THESE THINGS HAPPEN INSIDE THE LOCK: the three checks, the existing synchronous
    /// <see cref="TaskQueue.Activate(WorkTask, string)"/> call and the busy-field / role / model
    /// publication. No callbacks or delegates, no database or channel operation, no logging, no
    /// notification, and no <c>await</c>.
    /// </para>
    /// </remarks>
    /// <param name="expected">The exact instance the caller validated — the ONLY instance mutated.</param>
    /// <param name="task">The task dequeued for this claim.</param>
    /// <param name="queue">The concrete queue the task was dequeued from.</param>
    /// <returns><c>true</c> when the claim was taken; <c>false</c> when it was refused.</returns>
    internal bool TryClaimAndActivate(ConnectedWorker expected, WorkTask task, TaskQueue queue)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(queue);

        lock (_activityLock)
        {
            // 1. EXACT REGISTERED REFERENCE (ABA-safe).
            if (!_workers.TryGetValue(expected.Id, out var registered)
                || !ReferenceEquals(registered, expected))
                return false;

            // 2. IDLE/NULL-TASK SHAPE.
            if (expected.IsBusy || expected.CurrentTaskId is not null)
                return false;

            // 3. NOT HELD.
            if (expected.CompletionPublicationPending)
                return false;

            // The task becomes active and the worker becomes busy with it in the same lock span, so
            // no participating pool operation can observe the worker as idle with the task already
            // active, or select it between the two.
            queue.Activate(task, expected.Id);
            PublishBusyFieldsNoLock(expected, task.TaskId, DateTime.UtcNow);
            expected.Role = task.Role;
            expected.CurrentModel = task.Model;

            return true;
        }
    }

    /// <summary>
    /// Returns workers whose last heartbeat exceeds the given timeout (i.e., stale).
    /// </summary>
    /// <remarks>
    /// A read-only snapshot, not part of the removal path — but taken under <c>_activityLock</c> so
    /// the heartbeat values it reports are consistent with concurrent <see cref="UpdateHeartbeat"/>
    /// writes rather than possibly-unpublished ones.
    /// </remarks>
    /// <param name="timeout">Maximum acceptable time since the last heartbeat.</param>
    /// <returns>A read-only list of stale <see cref="ConnectedWorker"/> instances.</returns>
    public IReadOnlyList<ConnectedWorker> GetStaleWorkers(TimeSpan timeout)
    {
        lock (_activityLock)
        {
            var now = DateTime.UtcNow;
            return _workers.Values
                .Where(w => now - w.LastHeartbeat > timeout)
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>
    /// Returns workers that are busy with a task whose last task-specific stream activity
    /// (ToolRequest, Progress, or Complete) occurred longer ago than <paramref name="timeout"/>.
    /// Such workers are still heartbeating, so <see cref="GetStaleWorkers"/> will not report them,
    /// but their task has gone silent and would otherwise hold its pipeline slot forever.
    /// <para>
    /// This is only a <em>candidate selector</em>: the returned references are live and their
    /// activity may be refreshed before the caller acts. Callers must remove candidates via
    /// <see cref="TryRemoveTimedOutWorker"/>, which re-checks the condition atomically.
    /// </para>
    /// </summary>
    /// <param name="timeout">Maximum acceptable inactivity duration for a single task.</param>
    /// <returns>A read-only list of <see cref="ConnectedWorker"/> instances with silent tasks.</returns>
    public IReadOnlyList<ConnectedWorker> GetWorkersWithTimedOutTasks(TimeSpan timeout)
    {
        // Read the activity state under the lock so the snapshot is consistent with concurrent
        // TouchActivity/MarkBusy writes (no torn or never-published values).
        lock (_activityLock)
        {
            var now = DateTime.UtcNow;
            return _workers.Values
                .Where(w => IsTimedOutNoLock(w, now, timeout))
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>
    /// Atomically re-checks that the identified worker is still busy with a task that has been
    /// inactive for longer than <paramref name="timeout"/> and, only if so, removes it from the
    /// pool and completes its message channel.
    /// <para>
    /// This closes the time-of-check/time-of-use race in inactivity-based reclamation: between
    /// <see cref="GetWorkersWithTimedOutTasks"/> selecting a candidate and the caller acting on it,
    /// the worker may have reported new activity or finished its task. Re-checking under the same
    /// lock that <see cref="TouchActivity"/> takes guarantees such a worker is never evicted.
    /// </para>
    /// </summary>
    /// <param name="id">Identifier of the candidate worker to remove.</param>
    /// <param name="timeout">Maximum acceptable inactivity duration for a single task.</param>
    /// <returns>
    /// <c>true</c> if the worker was still timed out and has been removed; <c>false</c> if it is
    /// gone, is no longer busy with a task, or has shown activity since it was selected.
    /// </returns>
    public bool TryRemoveTimedOutWorker(string id, TimeSpan timeout)
    {
        ConnectedWorker removed;

        lock (_activityLock)
        {
            if (!_workers.TryGetValue(id, out var worker))
                return false;

            if (!IsTimedOutNoLock(worker, DateTime.UtcNow, timeout))
                return false;

            // Remove the exact instance we validated, so a re-registered worker under the same ID
            // is never removed on the strength of the old instance's timestamps.
            if (!_workers.TryRemove(new KeyValuePair<string, ConnectedWorker>(id, worker)))
                return false;

            removed = worker;
        }

        // Complete the channel outside the lock: completion can resume the worker's stream reader
        // inline, and no reader continuation should ever run while the activity lock is held.
        removed.MessageChannel.Writer.TryComplete();
        return true;
    }

    /// <summary>
    /// Evaluates the inactivity-timeout predicate. Callers must hold <c>_activityLock</c>.
    /// </summary>
    private static bool IsTimedOutNoLock(ConnectedWorker worker, DateTime now, TimeSpan timeout) =>
        worker.IsBusy
        && worker.CurrentTaskId is not null
        && now - worker.LastActivityAt > timeout;

    /// <summary>Returns aggregate statistics about the worker pool.</summary>
    /// <returns>A <see cref="WorkerPoolStats"/> snapshot.</returns>
    public WorkerPoolStats GetWorkerStats()
    {
        var workers = _workers.Values.ToList();
        var workersByRole = workers
            .GroupBy(w => w.Role.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        return new WorkerPoolStats
        {
            TotalWorkers = workers.Count,
            BusyWorkers = workers.Count(w => w.IsBusy),
            IdleWorkers = workers.Count(w => !w.IsBusy),
            WorkersByRole = workersByRole,
        };
    }

    /// <summary>
    /// Returns detailed worker pool statistics including per-worker information,
    /// suitable for the <c>/health</c> endpoint response.
    /// </summary>
    /// <returns>A <see cref="WorkerPoolStatsDto"/> snapshot with worker details.</returns>
    public WorkerPoolStatsDto GetDetailedStats()
    {
        var workers = _workers.Values.ToList();
        return new WorkerPoolStatsDto
        {
            TotalWorkers = workers.Count,
            IdleWorkers = workers.Count(w => !w.IsBusy),
            BusyWorkers = workers.Count(w => w.IsBusy),
            Workers = workers.Select(w => new WorkerInfoDto
            {
                Id = w.Id,
                Role = w.Role == WorkerRole.Unspecified ? null : w.Role.ToString(),
                IsBusy = w.IsBusy,
                CurrentTaskId = w.CurrentTaskId,
            }).ToList(),
        };
    }

    /// <summary>
    /// Removes stale workers from the pool and returns them.
    /// </summary>
    /// <remarks>
    /// The staleness check and the removal happen together under <c>_activityLock</c>, and a fresh
    /// worker is left in place rather than being removed and re-added. An earlier implementation
    /// removed every entry first and reinserted the fresh ones, which made workers transiently
    /// absent from the dictionary: a concurrent <see cref="TouchActivity"/> landing in that window
    /// returned <c>false</c> and silently dropped the activity update, after which the following
    /// inactivity scan could evict a worker that had just reported progress. Holding the lock for
    /// the whole decision makes this purge atomic with respect to <see cref="TouchActivity"/>,
    /// <see cref="UpdateHeartbeat"/>, <see cref="MarkBusy"/>, <see cref="MarkIdle"/> and
    /// <see cref="TryRemoveTimedOutWorker"/>.
    /// </remarks>
    /// <param name="timeout">Maximum acceptable time since the last heartbeat.</param>
    /// <returns>A read-only list of the removed <see cref="ConnectedWorker"/> instances.</returns>
    public IReadOnlyList<ConnectedWorker> PurgeStaleWorkers(TimeSpan timeout)
    {
        var removed = new List<ConnectedWorker>();

        lock (_activityLock)
        {
            // Snapshot now once so all staleness decisions are made against a consistent point in time.
            var now = DateTime.UtcNow;

            foreach (var key in _workers.Keys.ToList())
            {
                if (!_workers.TryGetValue(key, out var worker))
                    continue;

                // Fresh workers are never touched — no transient removal, so no window in which
                // a concurrent activity or heartbeat update could be lost.
                if (now - worker.LastHeartbeat <= timeout)
                    continue;

                // Remove the exact instance we validated as stale.
                if (_workers.TryRemove(new KeyValuePair<string, ConnectedWorker>(key, worker)))
                    removed.Add(worker);
            }
        }

        // Complete channels outside the lock: completion can resume a worker's stream reader
        // inline, and no reader continuation should ever run while the activity lock is held.
        foreach (var worker in removed)
            worker.MessageChannel.Writer.TryComplete();

        return removed.AsReadOnly();
    }
}
