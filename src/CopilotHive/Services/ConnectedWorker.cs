using System.Threading.Channels;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Represents a worker that is currently connected to the orchestrator via gRPC.
/// Holds worker metadata and the channel used to push messages to it.
/// </summary>
public sealed class ConnectedWorker
{
    /// <summary>Unique identifier assigned to this worker.</summary>
    public required string Id { get; init; }
    /// <summary>Current role of this worker. Initially Unspecified; updated dynamically per task.</summary>
    public required Workers.WorkerRole Role { get; set; }
    /// <summary>Capabilities advertised by this worker during registration.</summary>
    public required string[] Capabilities { get; init; }
    /// <summary>
    /// THE IMMUTABLE PER-REGISTRATION NEGOTIATION FACT: whether this worker asked the orchestrator
    /// to retain durable completion receipts (<c>RegisterRequest.request_completion_receipt_ack</c>).
    /// Set BEFORE the pool publishes the instance, so no consumer can ever observe a registered
    /// worker whose requested flag is not yet decided. A REQUEST IS NOT ENABLEMENT: it says nothing
    /// about the orchestrator supporting or enabling the protocol, and it is never inferred from
    /// <see cref="Capabilities"/>. An absent wire field decodes to <c>false</c>.
    /// </summary>
    public bool RequestCompletionReceiptAck { get; init; }
    /// <summary>
    /// THE IMMUTABLE PER-REGISTRATION ENABLEMENT DECISION, separate from
    /// <see cref="RequestCompletionReceiptAck"/>: whether the orchestrator will publish durable
    /// completion-receipt acknowledgements on THIS instance's stream.
    /// <para>
    /// A REQUEST IS NOT ENABLEMENT. This is <c>true</c> only for an ACCEPTED registration that
    /// explicitly requested the acknowledgement AND was answered by an orchestrator that had a
    /// completion recorder configured at registration time. It is never inferred from
    /// <see cref="Capabilities"/>, from the model or from any version, and it is decided BEFORE the
    /// pool publishes the instance, so no consumer can observe a registered worker whose enablement
    /// is not yet settled.
    /// </para>
    /// <para>
    /// IT AUTHORISES NOTHING BY ITSELF: an enabled instance still has to complete a task through the
    /// ordinary validated release before any acknowledgement becomes eligible.
    /// </para>
    /// </summary>
    public bool CompletionReceiptAckEnabled { get; init; }
    /// <summary>Whether the worker is currently executing a task.</summary>
    public bool IsBusy { get; set; }
    /// <summary>Identifier of the task the worker is currently executing, or <c>null</c> when idle.</summary>
    public string? CurrentTaskId { get; set; }
    /// <summary>
    /// UTC timestamp when the worker started its current task, or <c>null</c> when idle.
    /// For display/statistics only. Stale detection uses <see cref="LastActivityAt"/>.
    /// </summary>
    public DateTime? CurrentTaskStartedAt { get; set; }
    /// <summary>UTC timestamp of the last task-specific stream activity (ToolRequest, Progress, or Complete message). NOT updated by Ready messages or heartbeats. Used for inactivity-based stale detection. A silently processing worker will time out — intentional, as it's indistinguishable from a hung call.</summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    /// <summary>UTC timestamp of the last heartbeat received from this worker.</summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    /// <summary>UTC timestamp when this worker first connected.</summary>
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;
    /// <summary>Model used for the current task, or <c>null</c> when idle.</summary>
    public string? CurrentModel { get; set; }
    /// <summary>Estimated context window usage as a percentage (0–100), or 0 when idle.</summary>
    public int ContextUsagePercent { get; set; }

    /// <summary>
    /// THE COMPLETION-PUBLICATION SELECTION HOLD of THIS instance: <c>true</c> only during the short
    /// interval in which the instance has already been RELEASED by a negotiated ordinary completion
    /// but its completion-receipt acknowledgement has not yet been queued on its own stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT IT IS FOR, AND HOW NARROW THAT IS. A normal completion releases the worker to idle
    /// BEFORE the stream-local acknowledgement eligibility is advanced and before the
    /// acknowledgement is enqueued. An independently running dispatcher can NEWLY SELECT that
    /// just-released worker inside that gap (the eager push path consults the pool, never the
    /// stream) and enqueue another assignment. This flag closes ONLY that interval: while it is set,
    /// <see cref="WorkerPool.GetIdleWorker"/> never returns this instance and
    /// <see cref="WorkerPool.TryMarkIdleForReady"/> refuses it.
    /// </para>
    /// <para>
    /// IT IS NEITHER A RESERVATION NOR A DELIVERY CLAIM. It does not block a dispatcher that already
    /// holds an older candidate from calling the existing ID-based busy marking, it carries no task
    /// id, no deadline, no owner and no history, it is not keyed or looked up by worker id, and it
    /// says nothing about whether the queued acknowledgement was ever received. Stale-candidate /
    /// atomic-claim and failed-acknowledgement recovery remain SEPARATE contracts, and the
    /// stream-local eligibility holder remains the only thing that authorizes a re-acknowledgement.
    /// </para>
    /// <para>
    /// ONLY <see cref="WorkerPool"/> MUTATES IT, AND ONLY INSIDE ITS ACTIVITY LOCK. The hold is
    /// INSTALLED in the very lock span that applies the checked completion release, so the instance
    /// is never observable as idle-and-selectable; it is CLEARED in a later lock span that touches
    /// nothing else at all — not the role, not the model, not the task id and not any activity clock.
    /// A read made outside that lock is an OBSERVATION ONLY and establishes nothing. The setter is
    /// private and the two mutation methods below are the pool's only entry points.
    /// </para>
    /// <para>
    /// ENDING THE HOLD IS NOT SELECTABILITY: an instance whose negotiated completion was released may
    /// still be waiting for its own accepted Ready (<see cref="AwaitingWorkerReady"/>), which is a
    /// separate fact on a separate interval.
    /// </para>
    /// </remarks>
    internal bool CompletionPublicationPending { get; private set; }

    /// <summary>
    /// INSTALLS the completion-publication selection hold. Callers must hold the pool's activity
    /// lock; this method itself takes no lock of its own.
    /// </summary>
    /// <remarks>
    /// IT IS THE POOL'S MUTATION PRIMITIVE, NOT A PUBLIC SWITCH: the property's setter is private,
    /// so the only way to set the hold is through this method, and the only caller is the checked
    /// completion release's own lock span.
    /// </remarks>
    internal void BeginCompletionPublicationHold() => CompletionPublicationPending = true;

    /// <summary>
    /// CLEARS the completion-publication selection hold, touching NOTHING else on the instance.
    /// Callers must hold the pool's activity lock.
    /// </summary>
    /// <remarks>
    /// IT IS IDEMPOTENT AND NARROW ON PURPOSE: clearing an already-clear hold is a no-op, and no
    /// role, model, task id, activity clock or heartbeat value is written. Ending the hold is the
    /// ONLY thing that happens when a publication finishes.
    /// </remarks>
    internal void EndCompletionPublicationHold() => CompletionPublicationPending = false;

    /// <summary>
    /// THE INSTANCE-LOCAL READINESS WAIT of THIS registration: <c>true</c> from the moment a
    /// negotiated completion released this instance's transport ownership until this instance's own
    /// accepted Ready arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT IT IS FOR. A completion whose acknowledgement the registration negotiated ends this
    /// instance's ownership of the completed task, but it does NOT establish that the worker has
    /// consumed the outcome — the acknowledgement may still be in flight on the stream, or may never
    /// have been received. Until the worker itself says it is ready again, this instance must not be
    /// handed the next ordinary assignment. While this flag is set,
    /// <see cref="WorkerPool.GetIdleWorker"/> never returns it and
    /// <see cref="WorkerPool.TryClaimAndActivate"/> refuses it.
    /// </para>
    /// <para>
    /// IT IS NEITHER THE PUBLICATION HOLD NOR AN ASSIGNMENT FACT. It is distinct from
    /// <see cref="CompletionPublicationPending"/>, which covers only the short interval in which the
    /// released instance's acknowledgement has not yet been queued; ending that hold does NOT end this
    /// wait and does NOT make the instance selectable. It is likewise not
    /// <see cref="IsBusy"/>/<see cref="CurrentTaskId"/>, which describe ASSIGNMENT state and are reset
    /// by the idle resets below without touching this flag. It carries no task id, no deadline, no
    /// retry, no owner and no history, and it is nothing more than a per-instance bit that belongs to
    /// exactly this <see cref="ConnectedWorker"/> object.
    /// </para>
    /// <para>
    /// ONLY <see cref="WorkerPool"/> MUTATES IT, AND ONLY INSIDE ITS ACTIVITY LOCK. It is INSTALLED in
    /// the very lock span that applies the checked negotiated completion release, alongside the idle
    /// reset and the publication hold, so the instance is never observable as released-and-selectable
    /// in that span; it is CLEARED by the successfully validated
    /// <see cref="WorkerPool.TryMarkIdleForReady"/> transition for the still-registered instance. No
    /// other operation writes it — not the generic idle reset, not the legacy busy marking, not a
    /// heartbeat, not the publication hold's clear, and not an acknowledgement outcome. A read made
    /// outside the pool's activity lock is an OBSERVATION ONLY and establishes nothing. The setter is
    /// private and the two mutation methods below are the pool's only entry points.
    /// </para>
    /// </remarks>
    internal bool AwaitingWorkerReady { get; private set; }

    /// <summary>
    /// INSTALLS the instance-local readiness wait. Callers must hold the pool's activity lock; this
    /// method itself takes no lock of its own.
    /// </summary>
    /// <remarks>
    /// IT IS THE POOL'S MUTATION PRIMITIVE, NOT A PUBLIC SWITCH: the property's setter is private, so
    /// the only way to set the wait is through this method, and the only caller is the checked
    /// negotiated completion release's own lock span.
    /// </remarks>
    internal void BeginAwaitingWorkerReady() => AwaitingWorkerReady = true;

    /// <summary>
    /// CLEARS the instance-local readiness wait, touching NOTHING else on the instance. Callers must
    /// hold the pool's activity lock.
    /// </summary>
    /// <remarks>
    /// IT IS THE ONLY WAY THE WAIT ENDS: the pool calls it exclusively from the accepted
    /// <see cref="WorkerPool.TryMarkIdleForReady"/> transition — i.e. when the worker itself said it
    /// is ready. It is deliberately not reachable from any other path, so no timeout, lease,
    /// removal, heartbeat, acknowledgement outcome or idle reset can silently make this instance
    /// selectable again.
    /// </remarks>
    internal void EndAwaitingWorkerReady() => AwaitingWorkerReady = false;

    /// <summary>
    /// THE EXCLUSIVE, ONE-WAY WORKSTREAM ATTACHMENT CLAIM of this instance. <c>0</c> = unclaimed,
    /// <c>1</c> = claimed. Mutated ONLY through <see cref="Interlocked.CompareExchange(ref int, int, int)"/>.
    /// </summary>
    private int _workStreamAttached;

    /// <summary>
    /// Attempts to claim EXCLUSIVE WorkStream attachment for this instance. Exactly ONE caller can
    /// ever succeed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE CLAIM IS ONE-WAY AND PER INSTANCE: once taken it is NEVER released, reset or reused, so a
    /// second stream can never take over this instance's channel — the only response writer is
    /// <see cref="MessageChannel"/>, and exactly one stream may forward from it. Normal stream
    /// teardown REMOVES this instance from the pool and closes the channel; a worker that
    /// re-registers under the same ID therefore gets a NEW instance with a FRESH claim.
    /// </para>
    /// <para>
    /// IT IS STREAM OWNERSHIP ONLY. It grants no re-registration authorization, performs no restart
    /// recovery, and says nothing about the registration negotiation fact above.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> for the one successful claim; <c>false</c> for every later attempt.</returns>
    public bool TryAttachWorkStream() =>
        Interlocked.CompareExchange(ref _workStreamAttached, 1, 0) == 0;

    /// <summary>
    /// Whether a WorkStream has claimed this instance. An OBSERVATION ONLY — it is never a gate:
    /// attachment always goes through the atomic <see cref="TryAttachWorkStream"/>.
    /// </summary>
    public bool IsWorkStreamAttached => Volatile.Read(ref _workStreamAttached) == 1;

    /// <summary>
    /// The orchestrator writes messages here; the worker's stream reads from it.
    /// </summary>
    public Channel<OrchestratorMessage> MessageChannel { get; } =
        Channel.CreateUnbounded<OrchestratorMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
}
