using CopilotHive.Shared.Grpc;

using Grpc.Core;

namespace CopilotHive.Worker;

/// <summary>
/// THE WORKER'S ONE COHERENTLY PUBLISHED CONNECTION: an ACCEPTED registration (the fixed
/// orchestrator-assigned worker identity) together with an OPENED duplex work stream, the gRPC
/// client every unary operation on that registration goes through, and the provisioner associated
/// with them.
/// <para>
/// WHY ONE OBJECT. The four values it replaces (the client, the assigned id, the stream and the
/// provisioner) used to be published independently, so an operation could observe a torn
/// combination — A's writer with B's identity, or a client whose registration had already been
/// retired. An operation now SNAPSHOTS this one reference before it awaits, so the identity, the
/// writer and the client it uses always belong to the same accepted registration.
/// </para>
/// <para>
/// PUBLICATION. <see cref="WorkerService"/> publishes the reference only after this object has been
/// fully constructed (registration accepted, stream opened, provisioner associated), and always
/// BEFORE the initial <c>Ready</c> and any assignment processing. A rejected or partially built
/// attempt never becomes observable.
/// </para>
/// <para>
/// RETIREMENT. Retirement is the teardown signal: it makes NEW operations fail with the existing
/// disconnected error BEFORE any transport starts, while an operation that already captured this
/// connection keeps its own client, token and outcome. It is one-way — a connection is never
/// revived, and concurrent reconnect is not a supported contract.
/// </para>
/// </summary>
internal sealed class WorkerConnection
{
    /// <summary>
    /// The EXISTING disconnected error message, shared by every "no usable connection" path so that
    /// a retired connection and a never-published one fail with the same error category.
    /// </summary>
    internal const string DisconnectedMessage = "Not connected to orchestrator";

    private int _retired;

    /// <summary>
    /// THE ONE PER-CONNECTION TOOL-RESPONSE LOCK. It makes registration, response completion,
    /// removal and the one-way <see cref="EndToolResponses"/> a single atomic lifetime: a
    /// registration either lands inside the still-open lifetime or is rejected with the EXISTING
    /// disconnected error, and no caller ever observes a half-updated registry.
    /// </summary>
    private readonly object _toolResponsesLock = new();

    /// <summary>
    /// The pending response-bearing tool calls THIS connection owns, keyed by the request ID the
    /// request write carried. Entries are removed as soon as they settle (completion, removal or
    /// <see cref="EndToolResponses"/>), so this holds only genuinely outstanding waits and never
    /// accumulates history.
    /// </summary>
    private readonly Dictionary<string, TaskCompletionSource<ToolCallResponse>> _toolResponses = [];

    /// <summary>
    /// Whether the response lifetime has been ended. ONE-WAY: once <see cref="EndToolResponses"/>
    /// has run, registration is closed for the rest of this connection's life.
    /// </summary>
    private bool _toolResponsesClosed;

    /// <summary>
    /// Creates a connection.
    /// </summary>
    /// <param name="assignedId">The fixed orchestrator-assigned worker identity.</param>
    /// <param name="client">The gRPC client the registration was accepted on.</param>
    /// <param name="stream">The OPENED duplex work stream for this registration.</param>
    /// <param name="provisionerOverride">
    /// When non-null, the provisioner associated with this connection INSTEAD of the production one
    /// (the <c>TestProvisioner</c> seam). That single instance then backs BOTH provisioning sites.
    /// </param>
    /// <param name="includeProductionProvisioner">
    /// <c>false</c> together with a <c>null</c> <paramref name="provisionerOverride"/> leaves the
    /// connection with NO provisioner at all, which selects the legacy, seam-free executor path for
    /// direct-loop tests. Production always leaves this <c>true</c>.
    /// </param>
    /// <param name="provisioningEnvironment">
    /// The WORKER PROCESS's shared environment provenance, threaded from the attempt-construction
    /// path into the production provisioner. <c>null</c> gives this connection its OWN isolated
    /// provenance, which is the previous per-connection behavior (and what the focused fixtures
    /// that build a connection themselves keep). Only the environment snapshot and the
    /// provisioned-variable tracking are shared: this connection's identity, client, stream and
    /// provisioner all stay its own.
    /// </param>
    internal WorkerConnection(
        string assignedId,
        HiveOrchestrator.HiveOrchestratorClient client,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        WorkerConfigProvisioner? provisionerOverride = null,
        bool includeProductionProvisioner = true,
        WorkerProvisioningEnvironment? provisioningEnvironment = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(assignedId);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stream);

        AssignedId = assignedId;
        Client = client;
        Stream = stream;
        Provisioner = provisionerOverride
            ?? (includeProductionProvisioner
                ? CreateProductionProvisioner(provisioningEnvironment)
                : null);
    }

    /// <summary>
    /// The fixed assigned worker identity. Every message this connection writes carries exactly this
    /// value, so a writer can never be paired with another registration's identity.
    /// </summary>
    internal string AssignedId { get; }

    /// <summary>
    /// The gRPC client for this registration — used for the UNARY RPCs (heartbeat, session,
    /// provisioning) only; the work stream is a separate, already-opened call.
    /// </summary>
    internal HiveOrchestrator.HiveOrchestratorClient Client { get; }

    /// <summary>The duplex work stream opened for this registration.</summary>
    internal AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> Stream { get; }

    /// <summary>
    /// The provisioner associated with this connection, or <c>null</c> for a connection built
    /// WITHOUT one — which selects the legacy, seam-free executor path. Production always carries
    /// one, and both provisioning sites use this same instance.
    /// </summary>
    internal WorkerConfigProvisioner? Provisioner { get; }

    /// <summary>Whether this connection has been retired (teardown). One-way.</summary>
    internal bool IsRetired => Volatile.Read(ref _retired) != 0;

    /// <summary>Retires this connection. Idempotent.</summary>
    /// <remarks>
    /// RETIREMENT ALSO ENDS THIS CONNECTION'S TOOL-RESPONSE LIFETIME, as an IDEMPOTENT fallback:
    /// a connection torn down from a path that never entered the message loop (a fallible
    /// post-publication setup step, for example) must not leave a bridge call parked on a response
    /// that can never arrive. The message loop still ends responses EXPLICITLY and FIRST, before its
    /// assignment drain — see <c>WorkerService.ProcessMessagesAsync</c>.
    /// </remarks>
    internal void Retire()
    {
        // RETIRED FIRST: a wait released by the response closure below then observes a connection
        // that is already unusable, so its continuation can never re-enter this connection.
        Interlocked.Exchange(ref _retired, 1);
        EndToolResponses();
    }

    /// <summary>
    /// CHECKED ACCESS — returns this connection, or fails with the EXISTING disconnected error when
    /// it has been retired. Every NEW operation on a connection goes through this (directly, or
    /// through one of the checked accessors below) so a retired connection can never start
    /// transport.
    /// </summary>
    internal WorkerConnection EnsureUsable()
    {
        if (IsRetired)
            throw new InvalidOperationException(DisconnectedMessage);

        return this;
    }

    // ── Tool-response lifetime (per connection) ────────────────────────────────
    //
    // A response-bearing bridge call's pending wait belongs to the connection its REQUEST was
    // written on, not to the service. The lifetime is therefore:
    //
    //   REGISTER   — one entry per request ID, on the OPEN lifetime, or the EXISTING disconnected
    //                error when the lifetime has already ended.
    //   SETTLE     — a matching response, the caller's cancellation, or a failed send removes the
    //                entry; settled entries never accumulate.
    //   END        — one-way: closes registration, clears every entry and faults the unresolved
    //                response tasks with the EXISTING disconnected category/message. Never a
    //                fabricated negative response.
    //
    // All of it is coordinated by the ONE lock above, so a registration can never land in a
    // lifetime that has already ended and no reader ever observes a partially updated registry.

    /// <summary>
    /// The number of response-bearing waits currently owned by this connection (observation seam
    /// for tests; entries are removed as they settle, so this is never a history length).
    /// </summary>
    internal int PendingToolResponseCount
    {
        get { lock (_toolResponsesLock) return _toolResponses.Count; }
    }

    /// <summary>
    /// REGISTERS a response-bearing wait on THIS connection and returns the task the caller awaits.
    /// </summary>
    /// <remarks>
    /// Registration and the lifetime state are decided under the ONE lock, so the wait either
    /// belongs to the still-open lifetime or is rejected BEFORE anything is written with the
    /// EXISTING disconnected error category — the same failure a caller would see if it had
    /// snapshotted a retired connection.
    /// </remarks>
    /// <param name="requestId">The request ID the request write will carry.</param>
    /// <exception cref="InvalidOperationException">
    /// This connection's response lifetime has ended, or this connection has been retired.
    /// </exception>
    internal Task<ToolCallResponse> RegisterToolResponse(string requestId)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);

        lock (_toolResponsesLock)
        {
            if (_toolResponsesClosed || IsRetired)
                throw new InvalidOperationException(DisconnectedMessage);

            var pending = new TaskCompletionSource<ToolCallResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _toolResponses[requestId] = pending;
            return pending.Task;
        }
    }

    /// <summary>
    /// COMPLETES a pending wait from an incoming tool response. Unknown and late responses are
    /// IGNORED (the entry is already gone). The completed entry is REMOVED, so settled calls never
    /// accumulate.
    /// </summary>
    /// <param name="response">The genuine server response, used exactly as received.</param>
    /// <returns><c>true</c> when a pending wait was matched and completed.</returns>
    internal bool TryCompleteToolResponse(ToolCallResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        TaskCompletionSource<ToolCallResponse>? pending;
        lock (_toolResponsesLock)
        {
            if (!_toolResponses.Remove(response.RequestId, out pending))
                return false;
        }

        pending.TrySetResult(response);
        return true;
    }

    /// <summary>
    /// SETTLES a pending wait as cancelled by its CALLER's token and removes the entry. A no-op
    /// when the entry has already settled (first-terminal-winner).
    /// </summary>
    /// <param name="requestId">The request ID whose wait is being cancelled.</param>
    /// <param name="ct">The caller's token, which the cancelled wait reports.</param>
    internal void CancelToolResponse(string requestId, CancellationToken ct)
    {
        TaskCompletionSource<ToolCallResponse>? pending;
        lock (_toolResponsesLock)
        {
            if (!_toolResponses.Remove(requestId, out pending))
                return;
        }

        pending.TrySetCanceled(ct);
    }

    /// <summary>
    /// REMOVES an entry without settling it — used by the bridge's own cleanup. The wait's outcome
    /// was already decided by whichever terminal path won. A no-op when it is already gone.
    /// </summary>
    /// <param name="requestId">The request ID to drop.</param>
    internal void RemoveToolResponse(string requestId)
    {
        lock (_toolResponsesLock)
            _toolResponses.Remove(requestId);
    }

    /// <summary>
    /// CHECKED ACCESS — fails with the EXISTING disconnected error when this connection's
    /// tool-response lifetime has ENDED. Response-bearing sends take this check AFTER acquiring the
    /// send gate and BEFORE starting the underlying write, so a request queued behind the gate can
    /// never begin a write whose response could no longer be delivered to it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The response lifetime has ended.</exception>
    internal void EnsureToolResponsesOpen()
    {
        lock (_toolResponsesLock)
        {
            if (_toolResponsesClosed)
                throw new InvalidOperationException(DisconnectedMessage);
        }
    }

    /// <summary>
    /// ENDS this connection's tool-response lifetime. ONE-WAY and IDEMPOTENT: registration is
    /// closed, every entry is cleared, and each unresolved response task is FAULTED with the
    /// EXISTING disconnected category/message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY FAULT AND NOT A SYNTHESIZED RESPONSE. A response that will never arrive says nothing
    /// about the remote outcome: a fabricated negative <see cref="ToolCallResponse"/> would claim
    /// the orchestrator refused or did not perform the request, which this worker cannot know. The
    /// wait therefore ends with the SAME disconnect error every other no-usable-connection path
    /// raises, leaving the caller to treat the remote effect as UNKNOWN.
    /// </para>
    /// <para>
    /// The decision (close + drain the registry) happens under the ONE lock; the faulting happens
    /// after it, so no continuation runs while the lock is held.
    /// </para>
    /// </remarks>
    internal void EndToolResponses()
    {
        List<TaskCompletionSource<ToolCallResponse>> unresolved = [];
        lock (_toolResponsesLock)
        {
            if (_toolResponsesClosed)
                return;

            _toolResponsesClosed = true;
            unresolved.AddRange(_toolResponses.Values);
            _toolResponses.Clear();
        }

        foreach (var pending in unresolved)
            pending.TrySetException(new InvalidOperationException(DisconnectedMessage));
    }

    /// <summary>
    /// CHECKED ACCESS — the provisioning fetch for THIS connection. Retirement is checked BEFORE the
    /// RPC is issued, so a fetch or lazy runner callback that arrives after teardown fails
    /// disconnected instead of starting transport, and the request always carries THIS connection's
    /// worker identity (never another registration's).
    /// </summary>
    internal Task<GetWorkerConfigResponse> FetchWorkerConfigAsync(
        GetWorkerConfigRequest request, CancellationToken ct) =>
        EnsureUsable().Client.GetWorkerConfigAsync(request, cancellationToken: ct).ResponseAsync;

    /// <summary>
    /// THE CONNECTION-OWNED CHECKED PROVISIONING ENTRY POINT — the SINGLE way either provisioning
    /// site reaches the installed provisioner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checked access is performed against THIS connection FIRST, so a provisioning attempt that
    /// arrives after retirement fails with the EXISTING disconnected error and the underlying
    /// provisioner is NEVER started — no snapshot is taken, no fetch delegate is invoked, no
    /// transport begins.
    /// </para>
    /// <para>
    /// WHY THE WRAPPER IS NECESSARY. The connection's PRODUCTION provisioner fetches through
    /// <see cref="FetchWorkerConfigAsync"/>, which is itself retirement-checked. An OVERRIDE
    /// provisioner (the <c>TestProvisioner</c> seam) carries an INDEPENDENT fetch delegate that this
    /// connection knows nothing about, so handing its raw <c>EnsureProvisionedAsync</c> to a caller
    /// would let a runner-cached callback keep provisioning after teardown. Routing BOTH sites
    /// through here makes the retirement contract identical no matter which provisioner is
    /// installed.
    /// </para>
    /// <para>
    /// The installed provisioner is otherwise untouched: the SAME instance backs both sites, its
    /// credential / operator-snapshot / cancellation implementation is unchanged, and this adds no
    /// retry, buffering or cancellation machinery of its own.
    /// </para>
    /// </remarks>
    /// <param name="taskModel">The task's model, forwarded verbatim to the provisioner.</param>
    /// <param name="ct">Cancellation token, forwarded verbatim to the provisioner.</param>
    /// <exception cref="InvalidOperationException">
    /// This connection has been retired, or it carries no provisioner at all.
    /// </exception>
    internal Task EnsureProvisionedAsync(string? taskModel, CancellationToken ct)
    {
        // CHECKED FIRST: a retired connection must not start the provisioner's transport.
        var provisioner = EnsureUsable().Provisioner
            ?? throw new InvalidOperationException(
                "This connection carries no provisioner — it cannot provision LLM configuration.");

        return provisioner.EnsureProvisionedAsync(taskModel, ct);
    }

    /// <summary>
    /// The provisioning callback handed to the agent runner for its LAZY first-client creation —
    /// the SAME checked entry point <see cref="EnsureProvisionedAsync"/> the eager per-assignment
    /// site uses, bound to THIS connection. <c>null</c> when this connection carries no provisioner,
    /// which clears any callback a previous connection installed.
    /// </summary>
    internal Func<string?, CancellationToken, Task>? CreateProvisioningCallback() =>
        Provisioner is null ? null : EnsureProvisionedAsync;

    /// <summary>
    /// The production provisioner for this connection: constructed with THIS connection's assigned
    /// identity and a fetch that goes through THIS connection's checked access, so it can never be
    /// pointed at another registration's client or identity. The ENVIRONMENT PROVENANCE it works
    /// through is shared from the attempt-construction path when one was supplied, and is otherwise
    /// a fresh ISOLATED one belonging to this connection alone.
    /// </summary>
    /// <remarks>
    /// Called ONLY from the constructor. The fetch delegate captures <c>this</c>, and the only way
    /// to reach the delegate is through this object — which the service does not publish until
    /// construction has completed, so no partially built connection is ever observable.
    /// </remarks>
    /// <param name="provisioningEnvironment">
    /// The shared worker-process provenance, taken AS IS; <c>null</c> builds an isolated one.
    /// </param>
    private WorkerConfigProvisioner CreateProductionProvisioner(
        WorkerProvisioningEnvironment? provisioningEnvironment) =>
        new(
            AssignedId,
            (request, token) => FetchWorkerConfigAsync(request, token),
            provisioningEnvironment ?? new WorkerProvisioningEnvironment());
}
