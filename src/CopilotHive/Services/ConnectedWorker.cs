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
    /// THE WORKSTREAM-LOCAL ACKNOWLEDGEMENT STATE OF THIS INSTANCE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY IT LIVES HERE, HONESTLY: an instance is EXCLUSIVELY attached to at most one WorkStream
    /// (see <see cref="TryAttachWorkStream"/>, whose claim is one-way and never reused), and normal
    /// teardown removes the instance from the pool — so a worker that re-registers, and therefore
    /// gets a new stream, gets a NEW instance and FRESH state here. That makes this state
    /// stream-local in practice without inventing a per-stream registry.
    /// </para>
    /// <para>
    /// IT IS BOUNDED STATE, NOT A CACHE: exactly one task id is retained (the most recent completion
    /// whose durable evidence was confirmed AND whose checked release succeeded). It is never
    /// history, never replay authorization and never reconnect state.
    /// </para>
    /// </remarks>
    internal WorkStreamCompletionAckState AckState { get; } = new();

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

/// <summary>
/// THE BOUNDED, WORKSTREAM-LOCAL ACKNOWLEDGEMENT ELIGIBILITY OF ONE ATTACHED INSTANCE: at most ONE
/// task id — the most recent completion whose durable evidence was confirmed and whose checked
/// release SUCCEEDED.
/// </summary>
/// <remarks>
/// <para>
/// WHAT ADVANCES IT: only an ORDINARY completion that passed every validated ownership check, was
/// RECORDED first, and then had its checked release APPLIED. A mapping failure, a recording refusal
/// and a refused checked release all leave it exactly as it was — no eligibility is ever created by
/// a failure.
/// </para>
/// <para>
/// IT IS DELIBERATELY JUST ONE OPAQUE ID. It is not a history, not a receipt cache, not a retry
/// budget and not a reconnect/resume authorization: the id is stored so a later duplicate attempt on
/// the SAME stream can be answered from retained evidence, and the slot simply advances on the next
/// successful ordinary completion.
/// </para>
/// <para>
/// THREADING: the WorkStream read loop handles messages strictly sequentially, so the ordinary
/// completion path is single-threaded here. The lock nevertheless makes the read/write pair atomic
/// for any observer, because the value is read on paths that must not tear against a concurrent
/// advance.
/// </para>
/// </remarks>
internal sealed class WorkStreamCompletionAckState
{
    private readonly Lock _gate = new();
    private string? _latestEligibleTaskId;

    /// <summary>
    /// The most recent task id made acknowledgement-eligible by a successful ordinary completion, or
    /// <c>null</c> when no ordinary completion has been released on this stream yet.
    /// </summary>
    internal string? LatestEligibleTaskId
    {
        get
        {
            lock (_gate)
                return _latestEligibleTaskId;
        }
    }

    /// <summary>
    /// Advances the single latest-eligible slot to <paramref name="taskId"/>, REPLACING any previous
    /// id rather than accumulating one.
    /// </summary>
    /// <param name="taskId">The opaque task id of the just-released completion. Never blank.</param>
    internal void AdvanceLatestEligible(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        lock (_gate)
            _latestEligibleTaskId = taskId;
    }
}
