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
    internal WorkerConnection(
        string assignedId,
        HiveOrchestrator.HiveOrchestratorClient client,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        WorkerConfigProvisioner? provisionerOverride = null,
        bool includeProductionProvisioner = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(assignedId);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stream);

        AssignedId = assignedId;
        Client = client;
        Stream = stream;
        Provisioner = provisionerOverride
            ?? (includeProductionProvisioner ? CreateProductionProvisioner() : null);
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
    internal void Retire() => Interlocked.Exchange(ref _retired, 1);

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
    /// pointed at another registration's client or identity.
    /// </summary>
    /// <remarks>
    /// Called ONLY from the constructor. The fetch delegate captures <c>this</c>, and the only way
    /// to reach the delegate is through this object — which the service does not publish until
    /// construction has completed, so no partially built connection is ever observable.
    /// </remarks>
    private WorkerConfigProvisioner CreateProductionProvisioner() =>
        new(AssignedId, (request, token) => FetchWorkerConfigAsync(request, token));
}
